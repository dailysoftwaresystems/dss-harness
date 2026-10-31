using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.SideTasks;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;
using DssHarness.Core.Web.Search;

namespace DssHarness.Tests;

/// <summary>
/// The side-task commands: a conversation between a person and one agent over the main tree, through the same runner
/// every step is worked by. The one shot and its document, the chat and its records, the model and effort as a step's,
/// a resume and its refusals, and the listing - over the real runner and fakes of the clients, nothing live.
/// </summary>
public sealed class SideTaskServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A client that stores its conversations, as the two APIs do, answering as its script says.</summary>
    private sealed class StoredClient : IAgentClient
    {
        public const string ClientName = "glm";

        public string Name => ClientName;

        public AgentClientCapabilities Capabilities { get; } = new(
            Streaming: true,
            Thinking: false,
            WritableWhileWorking: true,
            AgentConversationHandling.Handed,
            SchemaEnforcedOutput: false,
            AgentToolsOwner.Harness,
            AgentToolsOwner.Harness,
            ReportsCost: false,
            ["low", "medium", "high", "xhigh", "max"],
            ProviderType: "zai",
            ToldModelBy: "--model with the code");

        public List<AgentRun> Runs { get; } = [];

        public Func<AgentRun, IAgentEventSink, CancellationToken, Task<AgentRunResult>> Script { get; set; }
            = (run, events, _) => Answered(run, events, "the answer");

        /// <summary>What a client that stores its conversations leaves behind: a session the next run goes on with.</summary>
        public static async Task SpeakAsync(AgentRun run, IAgentEventSink events, string text)
        {
            var session = run.Conversation?.Id ?? "c1";

            Directory.CreateDirectory(Path.Combine(run.SessionsDirectory!, session));
            await File.WriteAllTextAsync(Path.Combine(run.SessionsDirectory!, session, "conversation.jsonl"), "{}\n", CancellationToken.None);

            await events.EmitAsync(new MessageSent(run.UserPrompt, AgentMessageKind.FirstPrompt));
            await events.EmitAsync(new SessionStarted(run.Conversation?.Client ?? ClientName, run.Model ?? "glm-5.3", "hash", [], [], HandedConversation: run.Conversation is not null, Provider: "zai", SessionId: session));
            await events.EmitAsync(new TextDelta(text));
            await events.EmitAsync(new AssistantMessage(text, []));
            await events.EmitAsync(new TurnFinished(1, run.Model ?? "glm-5.3", new AgentUsage(10, 5, 0, 0, 0, 0), "end_turn"));
        }

        public static async Task<AgentRunResult> Answered(AgentRun run, IAgentEventSink events, string text)
        {
            await SpeakAsync(run, events, text);

            return new AgentRunResult(AgentRunStatus.Completed, FinalText: text, Model: run.Model)
            {
                Turns = 1,
                Usage = new AgentUsageReport(new AgentUsage(10, 5, 0, 0, 0, 0), [new AgentModelUsage(run.Model ?? "glm-5.3", new AgentUsage(10, 5, 0, 0, 0, 0))]),
            };
        }

        public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            Runs.Add(run);
            return await Script(run, events, cancellationToken);
        }
    }

    /// <summary>A client that renders its conversations, as Claude Code does: one launch held open for the chat.</summary>
    private sealed class RenderedClient : IAgentClient
    {
        public const string ClientName = "claude-code";

        public string Name => ClientName;

        public AgentClientCapabilities Capabilities { get; } = new(
            Streaming: true,
            Thinking: true,
            WritableWhileWorking: true,
            AgentConversationHandling.Rendered,
            SchemaEnforcedOutput: false,
            AgentToolsOwner.Client,
            AgentToolsOwner.Harness,
            ReportsCost: true,
            ["low", "medium", "high", "xhigh", "max"],
            ProviderType: "anthropic",
            ToldModelBy: "--model with the id");

        public List<AgentRun> Runs { get; } = [];

        public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            Runs.Add(run);

            await events.EmitAsync(new SessionStarted(ClientName, run.Model ?? "m", "hash", [], [], HandedConversation: false, Provider: "anthropic"));

            // The first message is the launch's own prompt; each later one reaches the agent through the channel a
            // message to an agent at work uses - in a chat, whose launch ends when the channel does.
            await OneTurnAsync(run, events, run.UserPrompt);

            if (run.Chat)
            {
                await foreach (var message in run.Messages.ReadAllAsync(cancellationToken))
                {
                    await OneTurnAsync(run, events, message);
                }
            }

            return new AgentRunResult(AgentRunStatus.Completed, FinalText: "the launch's last word", Model: run.Model)
            {
                Turns = 1,
                Usage = new AgentUsageReport(new AgentUsage(7, 3, 0, 0, 0, 0), []),
            };
        }

        private static async Task OneTurnAsync(AgentRun run, IAgentEventSink events, string message)
        {
            await events.EmitAsync(new TurnStarted(1));
            await events.EmitAsync(new TextDelta("about: " + message[..Math.Min(20, message.Length)]));
            await events.EmitAsync(new TurnFinished(1, run.Model ?? "m", new AgentUsage(7, 3, 0, 0, 0, 0), "end_turn"));
        }
    }

    /// <summary>A chat over lines a test types, collecting what it is told.</summary>
    private sealed class Typed(IEnumerable<string> lines)
    {
        private readonly Queue<string> _lines = new(lines);

        public List<string> Said { get; } = [];

        public SideTaskInterrupts Interrupts { get; } = new();

        public SideTaskChat Chat()
            => new(
                _ => Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null),
                line =>
                {
                    lock (Said)
                    {
                        Said.Add(line);
                    }
                })
            {
                Interrupts = Interrupts,
            };
    }

    /// <summary>A repository with the template's providers - and one model of a narrow effort list - over the two fakes.</summary>
    private static async Task<(HarnessFactory Harness, TempDirectory Tree, SideTaskService Service, StoredClient Stored, RenderedClient Rendered)> RepoAsync()
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();
        var narrow = new AgentProviderConfig
        {
            Name = "narrow",
            Type = "zai",
            Api = "https://api.narrow.example",
            ApiKeyNames = ["NARROW_API_KEY"],
            Client = "glm",
            Models =
            [
                new AgentModelConfig { Code = "narrow-1", Name = "Narrow 1", Tiers = ["narrow"], Efforts = ["high"], ContextTokens = 1000, MaxOutputTokens = 100 },
            ],
        };

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            Defaults = new() { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers, narrow],
            TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase) { ["narrow"] = ["narrow-1"] },
        });

        foreach (var (name, value) in new[] { ("zai.env", "ZAI_API_KEY=sk-test-one\n"), ("anthropic.env", "ANTHROPIC_API_KEY=sk-test-two\n"), ("narrow.env", "NARROW_API_KEY=sk-test-three\n") })
        {
            OrchestrationKit.Write(temp.Path, Path.Combine(".harness-config", "workflows", ".env", name), value);
            harness.FilePermissions.ProtectSecret(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", name));
        }

        var stored = new StoredClient();
        var rendered = new RenderedClient();
        var runner = new AgentRunner([stored, rendered], harness.FileSystem, harness.Platform, TimeProvider.System);
        var keys = new AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform);
        var service = new SideTaskService(
            harness.ContextLoader,
            runner,
            [stored, rendered],
            harness.Platform,
            harness.Identity,
            harness.OrchestrationStore,
            keys,
            new WebSearchKeys(keys, new GhProcess(harness.ProcessRunner)),
            updates: null,
            harness.FileSystem,
            harness.Output);

        return (harness, temp, service, stored, rendered);
    }

    /// <summary>The side-task's record, read back from where its records live.</summary>
    private static SideTaskRecord? RecordOf(HarnessFactory harness, TempDirectory temp, string directory)
        => new SideTaskStore(harness.FileSystem).Read(new SideTaskLayout(Path.Combine(temp.Path, ".side-tasks"), Path.GetFileName(directory)));

    /// <summary>The one shot: one message, one turn, the answer printed and the records named on every exit.</summary>
    [Fact]
    public async Task OneShot_AnswersOnce_AndKeepsItsRecordsUnderSideTasks()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "add a search source", Model: "glm-5.3"), Token);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("the answer", Assert.Single(outcome.Data));
        Assert.Contains(outcome.Details!, line => line.StartsWith("Records: ", StringComparison.Ordinal));

        var run = Assert.Single(stored.Runs);
        Assert.Equal(SideTaskService.AgentSystemPrompt, run.SystemPrompt);
        Assert.Equal(temp.Path, run.Workspace.WorkingDirectory);
        Assert.Equal(temp.Path, run.Workspace.MainCheckout);
        Assert.Null(run.Output);
        Assert.Null(run.Limits.MaxTurns);
        Assert.Null(run.Limits.MaxBudgetUsd);

        var directory = run.StepDirectory;
        Assert.StartsWith(Path.Combine(temp.Path, ".side-tasks"), directory, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(directory, StoredStream.FileName)), "the stored stream is kept under the side-task's own records");
        Assert.True(File.Exists(Path.Combine(directory, AgentRunner.ResultFileName)), "and the run's own record beside it");

        var record = RecordOf(harness, temp, directory);
        Assert.NotNull(record);
        Assert.Equal(SideTaskRecord.Ended, record!.Status);
        var turn = Assert.Single(record.Turns);
        Assert.Equal("glm", turn.Client);
        Assert.Equal("glm-5.3", turn.Model);
        Assert.Equal(SideTaskPolicy.Said, turn.Policy);
        Assert.Equal(AgentRunStatus.Completed, turn.Status);
        Assert.NotNull(record.Conversation);
        Assert.True(
            Directory.Exists(Path.Combine(directory, "sessions")),
            "the conversation is stored under the task's own sessions, in the shape every stored conversation keeps");

        var transcript = await File.ReadAllTextAsync(Path.Combine(directory, "transcript.md"), Token);
        Assert.Contains("person:", transcript, StringComparison.Ordinal);
        Assert.Contains("add a search source", transcript, StringComparison.Ordinal);
        Assert.Contains("agent:", transcript, StringComparison.Ordinal);
        Assert.Contains("add a search source", run.UserPrompt, StringComparison.Ordinal);
    }

    /// <summary>The one shot's document: the task, its records, how the turn stood, what it used and cost, its denials and its text.</summary>
    [Fact]
    public async Task OneShot_WithJson_AnswersWithOneDocument()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "how big is the tree?", Model: "glm-5.3", Json: true), Token);

        Assert.Equal(0, outcome.ExitCode);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

        Assert.Equal(0, (int)document["exitCode"]!);
        Assert.Equal("the answer", (string)document["finalText"]!);
        Assert.Equal("Completed", (string)document["status"]!);
        Assert.Equal("glm-5.3", (string)document["model"]!);
        Assert.Equal("glm", (string)document["client"]!);
        Assert.Equal(1, (int)document["turns"]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)document["records"]));
        Assert.Equal(10, (int)document["usage"]!["inputTokens"]!);
        Assert.Empty(document["denials"]!.AsArray());
    }

    /// <summary>--json without a prompt is a usage error: a chat is no document.</summary>
    [Fact]
    public async Task JsonWithoutAPrompt_IsAUsageError()
    {
        var (_, temp, service, _, _) = await RepoAsync();

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Json: true), Token);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Contains("a chat is no document", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The chat over a client that stores its conversations: one request per message, each going on with the stored
    /// conversation, the turn recorded per message, and the whole in one stored stream.
    /// </summary>
    [Fact]
    public async Task TheChat_RunsOneRequestPerMessage_GoingOnWithTheStoredConversation()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["what is here?", "and now?"]);

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Chat: typed.Chat()), Token);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, stored.Runs.Count);
        Assert.Null(stored.Runs[0].Conversation);
        Assert.NotNull(stored.Runs[1].Conversation);
        Assert.Equal(stored.Runs[0].StepDirectory, stored.Runs[1].StepDirectory);

        var record = RecordOf(harness, temp, stored.Runs[0].StepDirectory);
        Assert.Equal(2, record!.Turns.Count);
        Assert.NotNull(record.Conversation);
        Assert.Equal(["the answer", "the answer"], typed.Said);

        var transcript = await File.ReadAllTextAsync(Path.Combine(stored.Runs[0].StepDirectory, "transcript.md"), Token);
        Assert.Contains("what is here?", transcript, StringComparison.Ordinal);
        Assert.Contains("and now?", transcript, StringComparison.Ordinal);
    }

    /// <summary>
    /// The chat over a client that renders its conversations: ONE launch for the whole chat, the person's later
    /// messages reaching the agent through the channel a message to an agent at work uses, and the launch ending when
    /// the chat does.
    /// </summary>
    [Fact]
    public async Task TheChat_OverAClientThatHoldsItsLaunchOpen_PostsTheLaterMessagesToIt()
    {
        var (_, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["first", "second"]);

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Chat: typed.Chat()), Token);

        Assert.Equal(0, outcome.ExitCode);
        var run = Assert.Single(rendered.Runs);
        Assert.True(run.Chat);
        Assert.Contains(typed.Said, line => line.Contains("about: first", StringComparison.Ordinal));
        Assert.Contains(typed.Said, line => line.Contains("about: second", StringComparison.Ordinal));
    }

    /// <summary>The first interrupt cuts the agent's turn and leaves the chat open; what was said is kept.</summary>
    [Fact]
    public async Task TheFirstInterruptCutsTheTurn_AndTheChatStaysOpen()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["slow one", "quick one"]);

        stored.Script = async (run, events, token) =>
        {
            if (!run.UserPrompt.Contains("slow", StringComparison.Ordinal))
            {
                return await StoredClient.Answered(run, events, "quickly done");
            }

            await StoredClient.SpeakAsync(run, events, "half an answer");

            // The person presses Ctrl+C while the turn runs; the cancelled request is what the turn ends on.
            typed.Interrupts.Pressed();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new AgentRunResult(AgentRunStatus.Completed, FinalText: "never reached", Model: run.Model);
        };

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Chat: typed.Chat()), Token);

        Assert.Equal(0, outcome.ExitCode);
        Assert.Contains(typed.Said, line => line.Contains("the turn was interrupted", StringComparison.Ordinal));
        Assert.Equal(2, stored.Runs.Count);
        Assert.Contains(typed.Said, line => line.Contains("quickly done", StringComparison.Ordinal));
    }

    /// <summary>--resume opens a side-task again, on the model it last used, going on with its stored conversation.</summary>
    [Fact]
    public async Task ResumeOpensItAgain_OnTheModelItLastUsed_WithTheConversationItStored()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        var first = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "begin", Model: "glm-5.3", Name: "again"), Token);
        Assert.Equal(0, first.ExitCode);

        var second = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "go on", Resume: "again"), Token);

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(2, stored.Runs.Count);
        Assert.Equal("glm-5.3", stored.Runs[1].Model);
        Assert.NotNull(stored.Runs[1].Conversation);
        Assert.Contains("go on", stored.Runs[1].UserPrompt, StringComparison.Ordinal);
    }

    /// <summary>--resume naming no side-task is refused, and one open in another process is refused.</summary>
    [Fact]
    public async Task ResumeNamingNoneIsRefused_AndOneOpenInAnotherProcessIsRefused()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        var none = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Resume: "no-such"), Token);

        Assert.Equal(HarnessExit.Refused, none.ExitCode);
        Assert.Contains("names no side-task", none.Message, StringComparison.Ordinal);

        // A side-task open in another process: its owner names this very process, live as any running one is.
        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "open me", Model: "glm-5.3", Name: "held"), Token);
        var identity = new ProcessIdentity(new HostPlatform());
        JsonStateFile.Write(
            new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
            Path.Combine(temp.Path, ".side-tasks", "held.owner.json"),
            new RunOwner(identity.CurrentMachine, identity.CurrentId, DateTimeOffset.UtcNow, identity.Current));

        var held = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "again", Model: "glm-5.3", Resume: "held"), Token);

        Assert.Equal(HarnessExit.Refused, held.ExitCode);
        Assert.Contains("is open in another process", held.Message, StringComparison.Ordinal);
    }

    /// <summary>A name that is taken is refused, never taken over.</summary>
    [Fact]
    public async Task ANameThatIsTakenIsRefused()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        var first = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "one", Model: "glm-5.3", Name: "mine"), Token);
        Assert.Equal(0, first.ExitCode);

        var second = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "two", Model: "glm-5.3", Name: "mine"), Token);

        Assert.Equal(HarnessExit.Refused, second.ExitCode);
        Assert.Contains("is taken", second.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The effort a model does not take is a usage error naming what it takes; the entry-by-model form reaches the
    /// model it names.
    /// </summary>
    [Fact]
    public async Task AnEffortAModelDoesNotTake_IsAUsageErrorNamingWhatItTakes()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        var refused = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "narrow-1", Efforts: ["medium"]), Token);

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Contains("'narrow-1'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("It takes: high", refused.Message, StringComparison.Ordinal);

        var byModel = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "y", Model: "glm-5.3", Efforts: ["glm-5.3=high", "all=low"]), Token);

        Assert.Equal(0, byModel.ExitCode);
        Assert.Equal("high", Assert.Single(stored.Runs).Effort);
    }

    /// <summary>A turn that failed ends the one shot with 20; one that cannot start, with 14.</summary>
    [Fact]
    public async Task AModelThatFailedEndsTheOneShotWith20_OneThatCannotStartWith14()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        stored.Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Model: "glm-5.3", Error: "the model declined"));
        var failed = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "failed-one"), Token);
        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);

        // No key in the slot: the runner refuses the run before it begins, and the side-task answers 14.
        File.Delete(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "zai.env"));
        stored.Script = (run, events, _) => StoredClient.Answered(run, events, "never reached");
        var missing = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "failed-two"), Token);
        Assert.Equal(HarnessExit.ToolMissing, missing.ExitCode);
    }

    /// <summary>The listing: each side-task newest first, with its model, turns and whether it is open now.</summary>
    [Fact]
    public async Task TheListingNamesThem_NewestFirst_WithWhetherEachIsOpenNow()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "one", Model: "glm-5.3", Name: "older"), Token);
        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "two", Model: "glm-5.3", Name: "newer"), Token);

        var identity = new ProcessIdentity(new HostPlatform());
        JsonStateFile.Write(
            new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
            Path.Combine(temp.Path, ".side-tasks", "newer.owner.json"),
            new RunOwner(identity.CurrentMachine, identity.CurrentId, DateTimeOffset.UtcNow, identity.Current));

        var listed = await service.ListAsync(temp.Path, json: false, Token);
        var lines = listed.Details!.ToList();

        Assert.Equal(0, listed.ExitCode);
        Assert.True(
            lines.FindIndex(line => line.StartsWith("newer  ", StringComparison.Ordinal))
                < lines.FindIndex(line => line.StartsWith("older  ", StringComparison.Ordinal)),
            "newest first");
        Assert.Contains(listed.Details!, line => line.StartsWith("newer  ", StringComparison.Ordinal) && line.Contains("open now", StringComparison.Ordinal));
        Assert.Contains(listed.Details!, line => line.StartsWith("older  ", StringComparison.Ordinal) && !line.Contains("open now", StringComparison.Ordinal));

        var json = await service.ListAsync(temp.Path, json: true, Token);
        var document = JsonNode.Parse(Assert.Single(json.Data))!.AsObject();
        var tasks = document["sideTasks"]!.AsArray();

        Assert.Equal(2, tasks.Count);
        Assert.Equal("newer", (string)tasks[0]!["name"]!);
        Assert.True((bool)tasks[0]!["open"]!);
        Assert.False((bool)tasks[1]!["open"]!);
    }
}
