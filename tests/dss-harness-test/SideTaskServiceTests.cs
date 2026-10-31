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
            var session = run.Conversation?.Id ?? $"c{Guid.NewGuid():N}";

            Directory.CreateDirectory(Path.Combine(run.SessionsDirectory!, session));
            await File.WriteAllTextAsync(Path.Combine(run.SessionsDirectory!, session, "conversation.jsonl"), "{}\n", CancellationToken.None);

            // The first message as the API clients send it: the conversation so far before the person's, where the
            // conversation is a new one.
            await events.EmitAsync(new MessageSent(AgentPromptParts.FirstMessage(run, newConversation: run.Conversation is null), AgentMessageKind.FirstPrompt));
            await events.EmitAsync(new SessionStarted(run.Conversation?.Client ?? ClientName, run.Model ?? "glm-5.3", "hash", [], [], HandedConversation: run.Conversation is not null, Provider: run.Provider ?? "zai", SessionId: session));
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

    /// <summary>
    /// A client that renders its conversations, as Claude Code does: one launch held open for a chat, each message said
    /// as the real client says it - the first as the first prompt, each later one as the chat's.
    /// </summary>
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

        /// <summary>Where set, each launch ends after this many turns of its own, its context outgrown, as a launch that can hold no more does.</summary>
        public int? EndsAfterTurns { get; set; }

        public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            lock (Runs)
            {
                Runs.Add(run);
            }

            await events.EmitAsync(new SessionStarted(ClientName, run.Model ?? "m", "hash", [], [], HandedConversation: false, Provider: "anthropic"));

            // The first message is the launch's own prompt; each later one reaches the agent through the channel a
            // message to an agent at work uses - in a chat, whose launch ends when the channel does.
            await events.EmitAsync(new MessageSent(AgentPromptParts.FirstMessage(run, newConversation: true), AgentMessageKind.FirstPrompt));
            await OneTurnAsync(run, events, run.UserPrompt);
            var turns = 1;

            if (run.Chat)
            {
                await foreach (var message in run.Messages.ReadAllAsync(cancellationToken))
                {
                    if (turns == EndsAfterTurns)
                    {
                        break;
                    }

                    await events.EmitAsync(new MessageSent(message, AgentMessageKind.ChatMessage));
                    await OneTurnAsync(run, events, message);
                    turns++;
                }

                if (turns == EndsAfterTurns)
                {
                    return new AgentRunResult(AgentRunStatus.ContextExhausted, Model: run.Model, Error: "the context is full") { Turns = turns };
                }
            }

            return new AgentRunResult(AgentRunStatus.Completed, FinalText: "the launch's last word", Model: run.Model)
            {
                Turns = turns,
                Usage = new AgentUsageReport(new AgentUsage(7, 3, 0, 0, 0, 0), []),
            };
        }

        private static async Task OneTurnAsync(AgentRun run, IAgentEventSink events, string message)
        {
            var about = "about: " + message[..Math.Min(20, message.Length)];

            await events.EmitAsync(new TurnStarted(1));
            await events.EmitAsync(new TextDelta(about));
            await events.EmitAsync(new AssistantMessage(about, []));
            await events.EmitAsync(new TurnFinished(1, run.Model ?? "m", new AgentUsage(7, 3, 0, 0, 0, 0), "end_turn"));
        }
    }

    /// <summary>
    /// A chat over lines a test types, collecting what it is told; where the test says until when, its input ends only
    /// once the chat has said that line - as a person who waits for the answer before leaving.
    /// </summary>
    private sealed class Typed(IEnumerable<string> lines, string? until = null)
    {
        private readonly Queue<string> _lines = new(lines);
        private readonly TaskCompletionSource _heard = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Said { get; } = [];

        public SideTaskChat Chat()
            => new(
                async token =>
                {
                    lock (_lines)
                    {
                        if (_lines.Count > 0)
                        {
                            return _lines.Dequeue();
                        }
                    }

                    if (until is not null)
                    {
                        await _heard.Task.WaitAsync(TimeSpan.FromMinutes(2), token);
                    }

                    return null;
                },
                line =>
                {
                    lock (Said)
                    {
                        Said.Add(line);
                    }

                    if (line == until)
                    {
                        _heard.TrySetResult();
                    }
                });
    }

    /// <summary>A repository with the template's providers - and one model of a narrow effort list - over the two fakes.</summary>
    private static async Task<(HarnessFactory Harness, TempDirectory Tree, SideTaskService Service, StoredClient Stored, RenderedClient Rendered)> RepoAsync(TimeProvider? clock = null, Func<string, string?>? environment = null)
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
                new AgentModelConfig { Code = "narrow-1", Name = "Narrow 1", Tiers = ["narrow", "pair"], Efforts = ["high"], ContextTokens = 1000, MaxOutputTokens = 100 },
                new AgentModelConfig { Code = "narrow-2", Name = "Narrow 2", Tiers = ["pair"], Efforts = ["low", "high"], ContextTokens = 1000, MaxOutputTokens = 100 },
            ],
        };

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            Defaults = new() { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers, narrow],
            TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase) { ["narrow"] = ["narrow-1"], ["pair"] = ["narrow-2", "narrow-1"] },
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
            harness.Output,
            harness.GitClient,
            clock,
            environment ?? (_ => null));

        return (harness, temp, service, stored, rendered);
    }

    /// <summary>The side-task's layout under the tree.</summary>
    private static SideTaskLayout LayoutOf(TempDirectory temp, string name) => new(Path.Combine(temp.Path, ".side-tasks"), name);

    /// <summary>The side-task's record, read back from where its records live.</summary>
    private static SideTaskRecord? RecordOf(HarnessFactory harness, TempDirectory temp, string directory)
        => new SideTaskStore(harness.FileSystem).Read(LayoutOf(temp, Path.GetFileName(directory)));

    /// <summary>An owner file naming this very process, live as any running one is - a chat open in another process - or, given another stamp, one whose process has gone.</summary>
    private static void HeldElsewhere(TempDirectory temp, string name, string? stamp = null)
    {
        var identity = new ProcessIdentity(new HostPlatform());
        JsonStateFile.Write(
            new PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create())),
            Path.Combine(temp.Path, ".side-tasks", name + SideTaskLayout.OwnerSuffix),
            new RunOwner(identity.CurrentMachine, identity.CurrentId, DateTimeOffset.UtcNow, stamp ?? identity.Current));
    }

    /// <summary>The one shot: one message, one turn, the answer printed and the records named on every exit.</summary>
    [Fact]
    public async Task OneShot_AnswersOnce_AndKeepsItsRecordsUnderSideTasks()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "add a search source", Model: "glm-5.3", Name: "one"), Token);

        var directory = LayoutOf(temp, "one").Directory;
        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("side-task 'one' answered on glm-5.3", outcome.Message);
        Assert.Equal(["the answer"], outcome.Data);
        Assert.Equal([$"Records: {directory}"], outcome.Details);

        var run = Assert.Single(stored.Runs);
        Assert.Equal(SideTaskService.AgentSystemPrompt, run.SystemPrompt);
        Assert.Equal(temp.Path, run.Workspace.WorkingDirectory);
        Assert.Equal(temp.Path, run.Workspace.MainCheckout);
        Assert.Equal(directory, run.StepDirectory);
        Assert.Null(run.Output);
        Assert.Null(run.Limits.MaxTurns);
        Assert.Null(run.Limits.MaxBudgetUsd);
        Assert.Equal("add a search source", run.UserPrompt);
        Assert.Equal(["ANTHROPIC_API_KEY", "NARROW_API_KEY", "ZAI_API_KEY"], run.Withheld.Intersect(["ANTHROPIC_API_KEY", "NARROW_API_KEY", "ZAI_API_KEY"]).Order(StringComparer.Ordinal));
        Assert.True(File.Exists(Path.Combine(directory, StoredStream.FileName)), "the stored stream is kept under the side-task's own records");
        Assert.True(File.Exists(Path.Combine(directory, AgentRunner.ResultFileName)), "and the run's own record beside it");

        var record = RecordOf(harness, temp, directory)!;
        Assert.Equal(SideTaskStatus.Ended, record.Status);
        Assert.Equal(new SideTaskRunsOn(Model: "glm-5.3"), record.RunsOn);
        var turn = Assert.Single(record.Turns);
        Assert.Equal(("glm", "glm-5.3", SideTaskPolicy.Said, (AgentRunStatus?)AgentRunStatus.Completed), (turn.Client, turn.Model, turn.Policy, turn.Status));
        Assert.NotNull(record.Conversation);
        Assert.True(Directory.Exists(Path.Combine(directory, "sessions")), "the conversation is stored under the task's own sessions, in the shape every stored conversation keeps");

        Assert.Equal(
            ["person:", "    add a search source", string.Empty, "agent:", "    the answer"],
            (await File.ReadAllLinesAsync(Path.Combine(directory, "transcript.md"), Token)).SkipWhile(line => line.Length == 0));
    }

    /// <summary>The one shot's document: the exit code and its line first, the task, its records, how the turn stood, what it used and cost, its denials and its text.</summary>
    [Fact]
    public async Task OneShot_WithJson_AnswersWithOneDocument()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "how big is the tree?", Model: "glm-5.3", Name: "sized", Json: true), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

        Assert.Equal(
            ["exitCode", "summary", "task", "records", "status", "client", "provider", "model", "served", "usage", "cost", "turns", "toolCalls", "filesWritten", "webRequests", "denials", "finalText"],
            document.Select(member => member.Key));
        Assert.Equal(HarnessExit.Success, (int)document["exitCode"]!);
        Assert.Equal("side-task 'sized' answered on glm-5.3", (string)document["summary"]!);
        Assert.Equal("sized", (string)document["task"]!);
        Assert.Equal(LayoutOf(temp, "sized").Directory, (string)document["records"]!);
        Assert.Equal("the answer", (string)document["finalText"]!);
        Assert.Equal("Completed", (string)document["status"]!);
        Assert.Equal("glm-5.3", (string)document["model"]!);
        Assert.Equal("glm", (string)document["client"]!);
        Assert.Equal(1, (int)document["turns"]!);
        Assert.Equal(10, (int)document["usage"]!["inputTokens"]!);
        Assert.Empty(document["denials"]!.AsArray());
    }

    /// <summary>
    /// Every exit before the side-task was claimed says no records were written: --json without a prompt, --name with
    /// --resume, an effort that does not read - each a usage error, the whole line said.
    /// </summary>
    [Fact]
    public async Task EveryExitBeforeTheClaim_IsAUsageError_SayingNoRecordsWereWritten()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        var json = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Json: true, Chat: new Typed([]).Chat()), Token);
        var both = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Resume: "a", Name: "b"), Token);
        var effort = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Efforts: ["high", "high"]), Token);

        Assert.Equal(
            [
                (HarnessExit.UsageError, "--json without a prompt is a usage error: a chat is no document. Give --prompt or --prompt-file for one message."),
                (HarnessExit.UsageError, "--name with --resume is a usage error: a side-task opened again keeps the name it has. Give --resume alone to open it, or --name alone to begin a new one."),
                (HarnessExit.UsageError, "'--effort high' gives a second level after 'high': one level, or entries by model"),
            ],
            new[] { json, both, effort }.Select(outcome => (outcome.ExitCode, outcome.Message)));
        Assert.All(new[] { json, both, effort }, outcome => Assert.Equal([SideTaskService.NoRecords], outcome.Details));
        Assert.Empty(stored.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "b").Directory));
    }

    /// <summary>
    /// A side-task started under an agent - its environment marked as every agent's child's is - refuses itself, by
    /// whatever name it was started: nothing is begun and nothing written.
    /// </summary>
    [Fact]
    public async Task ASideTaskStartedUnderAnAgent_RefusesItself()
    {
        var (_, temp, service, stored, _) = await RepoAsync(environment: name => name == AgentChildEnvironment.AgentMarker ? "1" : null);
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "start another", Model: "glm-5.3", Name: "nested"), Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal($"'run-side-task' is refused: {SideTaskService.NoAgentStartsOne}. Nothing was started.", outcome.Message);
        Assert.Equal([SideTaskService.NoRecords], outcome.Details);
        Assert.Empty(stored.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "nested").Directory));
    }

    /// <summary>
    /// Where git does not ignore the side-tasks' directory, nothing is written there - a record committed by
    /// <c>git add -A</c> would travel to every clone - and the refusal names <c>init</c>.
    /// </summary>
    [Fact]
    public async Task ASideTasksDirectoryGitDoesNotIgnore_IsRefused_NothingWritten()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        foreach (var ignore in Directory.EnumerateFiles(temp.Path, ".gitignore", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            if (Path.GetDirectoryName(ignore) == Path.Combine(temp.Path, ".side-tasks"))
            {
                File.Delete(ignore);
                continue;
            }

            File.WriteAllLines(ignore, File.ReadAllLines(ignore).Where(line => !line.Contains(".side-tasks", StringComparison.Ordinal)));
        }

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "unignored"), Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(
            "git does not ignore '.side-tasks/unignored/task.json', so what side-task 'unignored' keeps would be committed. Run 'dssharness init', which keeps .side-tasks in git and everything made in it out. Nothing was started.",
            outcome.Message);
        Assert.Equal([SideTaskService.NoRecords], outcome.Details);
        Assert.Empty(stored.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "unignored").Directory));
    }

    /// <summary>
    /// A side-task's record is written as it is claimed, before its first turn: a process killed in that turn leaves a
    /// side-task that opens again, never a directory with no record.
    /// </summary>
    [Fact]
    public async Task TheRecordIsWrittenAsTheSideTaskIsClaimed_BeforeItsFirstTurn()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        SideTaskRecord? during = null;

        stored.Script = (run, events, _) =>
        {
            during = RecordOf(harness, temp, run.StepDirectory);
            return StoredClient.Answered(run, events, "the answer");
        };

        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "early"), Token);

        Assert.NotNull(during);
        Assert.Equal(("early", SideTaskStatus.Open, 0, new SideTaskRunsOn(Model: "glm-5.3")), (during!.Name, during.Status, during.Turns.Count, during.RunsOn));
    }

    /// <summary>
    /// A side-task the person did not name is named by the second it began with the first words of its first message;
    /// another begun in the same second with the same words takes the first free suffix, inside the claim.
    /// </summary>
    [Fact]
    public async Task ADerivedName_HasItsSecond_AndASuffixWhereAnotherBeganInTheSameSecond()
    {
        var clock = new ManualClock();
        var (harness, temp, service, _, _) = await RepoAsync(clock);
        using var tree = temp;

        var first = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "Hello there, agent!", Model: "glm-5.3"), Token);
        var second = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "Hello there, agent!", Model: "glm-5.3"), Token);

        var name = clock.GetUtcNow().LocalDateTime.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-hello-there-agent";

        Assert.Equal((HarnessExit.Success, HarnessExit.Success), (first.ExitCode, second.ExitCode));
        Assert.Equal([$"Records: {LayoutOf(temp, name).Directory}"], first.Details);
        Assert.Equal([$"Records: {LayoutOf(temp, name + "-2").Directory}"], second.Details);
        Assert.Equal(name + "-2", RecordOf(harness, temp, LayoutOf(temp, name + "-2").Directory)!.Name);
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

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "talk", Chat: typed.Chat()), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("side-task 'talk' ended, 2 turn(s) in all", outcome.Message);
        Assert.Equal([$"Records: {LayoutOf(temp, "talk").Directory}"], outcome.Details);
        Assert.Equal(2, stored.Runs.Count);
        Assert.Null(stored.Runs[0].Conversation);
        Assert.NotNull(stored.Runs[1].Conversation);
        Assert.Equal(stored.Runs[0].StepDirectory, stored.Runs[1].StepDirectory);

        var record = RecordOf(harness, temp, stored.Runs[0].StepDirectory)!;
        Assert.Equal((2, SideTaskStatus.Ended), (record.Turns.Count, record.Status));
        Assert.Equal(["the answer", "the answer"], typed.Said);
        Assert.Equal(new AgentUsage(20, 10, 0, 0, 0, 0), record.InAll!.Total);
    }

    /// <summary>
    /// The chat over a client that renders its conversations: ONE launch for the whole chat, the person's later
    /// messages reaching the agent through the channel a message to an agent at work uses, each message of the person's
    /// in the transcript - the later ones too - and the launch ending when the chat does, its record ended.
    /// </summary>
    [Fact]
    public async Task TheChat_OverAClientThatHoldsItsLaunchOpen_PostsTheLaterMessagesToIt_AndRecordsEach()
    {
        var (harness, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["first", "second"]);

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "held", Chat: typed.Chat()), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        var run = Assert.Single(rendered.Runs);
        Assert.True(run.Chat);
        Assert.Equal(["about: first", "about: second"], typed.Said);

        var record = RecordOf(harness, temp, run.StepDirectory)!;
        Assert.Equal(SideTaskStatus.Ended, record.Status);
        Assert.Equal(new AgentUsage(7, 3, 0, 0, 0, 0), record.InAll!.Total);

        var transcript = await File.ReadAllLinesAsync(Path.Combine(run.StepDirectory, "transcript.md"), Token);
        Assert.Equal(["    first", "    second"], transcript.Where((line, at) => at > 0 && transcript[at - 1] == "person:"));
    }

    /// <summary>
    /// A launch held open that ends by itself - its context outgrown - leaves no line of the person's unanswered: the
    /// line it never took begins a new launch, going on from the conversation compacted, and the person is told.
    /// </summary>
    [Fact]
    public async Task ALaunchThatEndsByItself_LosesNoLine_ANewLaunchGoesOnFromTheConversation()
    {
        var (_, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        rendered.EndsAfterTurns = 1;
        var typed = new Typed(["first", "second"], until: "about: second");

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "relaunched", Chat: typed.Chat()), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(2, rendered.Runs.Count);
        Assert.Equal("first", rendered.Runs[0].UserPrompt);
        Assert.Null(rendered.Runs[0].Continuation);
        Assert.Equal("second", rendered.Runs[1].UserPrompt);
        Assert.StartsWith(AgentCompaction.SaysWhatItIs, rendered.Runs[1].Continuation, StringComparison.Ordinal);
        Assert.Contains("first", rendered.Runs[1].Continuation!, StringComparison.Ordinal);
        Assert.Contains("about: second", typed.Said);
        Assert.Contains("the agent's launch ended ContextExhausted: the context is full; the next message begins a new launch, going on from the conversation so far", typed.Said);
    }

    /// <summary>
    /// The conversation a new launch goes on from is the whole side-task since it began, every launch of it read as one:
    /// its iterations numbered on across the launches - never each launch's own count from one again - and each later
    /// launch's first message the person's own.
    /// </summary>
    [Fact]
    public async Task TheConversationANewLaunchGoesOnFrom_NumbersItsIterationsAcrossEveryLaunch()
    {
        var (_, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        rendered.EndsAfterTurns = 1;
        var typed = new Typed(["first", "second", "third"], until: "about: third");

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "numbered", Chat: typed.Chat()), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(["first", "second", "third"], rendered.Runs.Select(run => run.UserPrompt));

        var carried = rendered.Runs[2].Continuation!;
        Assert.StartsWith($"{AgentCompaction.SaysWhatItIs}2.", carried, StringComparison.Ordinal);
        Assert.Contains("iteration 1 of 2:\nabout: first", carried.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("iteration 2 of 2:\nabout: second", carried.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    /// <summary>The first interrupt cuts the agent's turn and leaves the chat open; what was said is kept.</summary>
    [Fact]
    public async Task TheFirstInterruptCutsTheTurn_AndTheChatStaysOpen()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["slow one", "quick one"]);
        var interrupts = new SideTaskInterrupts();

        stored.Script = async (run, events, token) =>
        {
            if (!run.UserPrompt.Contains("slow", StringComparison.Ordinal))
            {
                return await StoredClient.Answered(run, events, "quickly done");
            }

            await StoredClient.SpeakAsync(run, events, "half an answer");

            // The person presses Ctrl+C while the turn runs; the cancelled request is what the turn ends on.
            interrupts.Pressed();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new AgentRunResult(AgentRunStatus.Completed, FinalText: "never reached", Model: run.Model);
        };

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "cut", Chat: typed.Chat()) { Interrupts = interrupts }, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            ["half an answer", "the turn was interrupted; the conversation keeps what was said - ask again, or /exit to leave", "quickly done"],
            typed.Said);
        Assert.Equal(2, stored.Runs.Count);
        Assert.Equal([AgentRunStatus.Cancelled, AgentRunStatus.Completed], RecordOf(harness, temp, stored.Runs[0].StepDirectory)!.Turns.Select(turn => turn.Status!.Value));
    }

    /// <summary>
    /// The first interrupt of a one shot cuts its turn; the side-task ends with what it said, its record ended and its
    /// records named, on the code an interrupted run ends on everywhere (130).
    /// </summary>
    [Fact]
    public async Task TheFirstInterruptOfAOneShot_CutsItsTurn_AndItEndsCancelled()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var interrupts = new SideTaskInterrupts();

        stored.Script = async (run, events, token) =>
        {
            await StoredClient.SpeakAsync(run, events, "half an answer");
            interrupts.Pressed();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new AgentRunResult(AgentRunStatus.Completed, FinalText: "never reached", Model: run.Model);
        };

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "slow", Model: "glm-5.3", Name: "cut-once") { Interrupts = interrupts }, Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal([$"Records: {LayoutOf(temp, "cut-once").Directory}"], outcome.Details);
        Assert.Equal(SideTaskStatus.Ended, RecordOf(harness, temp, LayoutOf(temp, "cut-once").Directory)!.Status);
        Assert.False(File.Exists(LayoutOf(temp, "cut-once").OwnerFile), "its owner is released");
    }

    /// <summary>
    /// The chat's own commands: /help and /records say what they say, an unknown command says so, a line ending in a
    /// backslash goes on to the next, and /model and /effort set up the next message's model and effort again - its
    /// order, not a label: the next request runs on what they named, a level the model does not take is refused by the
    /// one check of it, and the record says what the side-task runs on.
    /// </summary>
    [Fact]
    public async Task TheChatsCommands_AndALineThatGoesOn_AndModelAndEffortChangeWhatTheNextMessageRunsOn()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["/help", "/records", "/nope", "one \\", "two", "/effort high", "after the effort", "/model narrow-1", "after the model", "/effort medium", "/exit"]);

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "commanded", Chat: typed.Chat()), Token);

        var directory = LayoutOf(temp, "commanded").Directory;
        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            [
                Working.HelpLine,
                $"Records: {directory}",
                "'/nope' is no command of this chat; /help lists them",
                "the answer",
                "the next message goes to glm-5.3 through glm, at effort high",
                "the answer",
                "the next message goes to narrow-1 through glm, at effort high",
                "the answer",
                "the side-task runs at effort 'medium', which 'narrow-1' does not take. It takes: high. Give it a level of its own: /effort narrow-1=high, with all=<level> for the rest; the next message goes to narrow-1 as before",
            ],
            typed.Said);
        Assert.Equal(
            [("one\ntwo", "glm-5.3", (string?)null), ("after the effort", "glm-5.3", "high"), ("after the model", "narrow-1", "high")],
            stored.Runs.Select(run => (Task(run.UserPrompt), run.Model, run.Effort)));
        Assert.Equal(new SideTaskRunsOn(Model: "narrow-1", Effort: "high"), RecordOf(harness, temp, directory)!.RunsOn);

        // The person's message a first message carries after the conversation so far, where it carries any.
        static string Task(string prompt)
            => prompt.Contains("<task>", StringComparison.Ordinal) ? prompt[(prompt.LastIndexOf("<task>", StringComparison.Ordinal) + "<task>".Length)..prompt.LastIndexOf("</task>", StringComparison.Ordinal)].Trim() : prompt;
    }

    /// <summary>
    /// A turn that outgrew its model's context compacts the conversation: the next message begins a new conversation,
    /// the engine's compaction of the one before carried in its first message.
    /// </summary>
    [Fact]
    public async Task ATurnThatOutgrewItsContext_CompactsTheConversation_ForTheNextMessage()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["a long one", "go on"]);

        stored.Script = async (run, events, _) =>
        {
            if (run.UserPrompt == "a long one")
            {
                await StoredClient.SpeakAsync(run, events, "too much");
                return new AgentRunResult(AgentRunStatus.ContextExhausted, Model: run.Model, Error: "the context is full") { Turns = 1 };
            }

            return await StoredClient.Answered(run, events, "fresh");
        };

        await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "compacted", Chat: typed.Chat()), Token);

        Assert.Equal(2, stored.Runs.Count);
        Assert.Null(stored.Runs[1].Conversation);
        Assert.StartsWith(AgentCompaction.SaysWhatItIs, stored.Runs[1].Continuation, StringComparison.Ordinal);
        Assert.Contains("a long one", stored.Runs[1].Continuation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// --resume opens a side-task again on what the person set it to run on: begun on a tier, it keeps the tier's later
    /// models as its order - never the model it last ran on named outright - going on with its stored conversation.
    /// </summary>
    [Fact]
    public async Task ResumeOpensItAgain_OnTheTierItWasSetTo_WithTheConversationItStored()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        var first = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "begin", Tier: "pair", Name: "again", Efforts: ["high"]), Token);
        Assert.Equal(HarnessExit.Success, first.ExitCode);

        var second = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "go on", Resume: "again"), Token);

        Assert.Equal(HarnessExit.Success, second.ExitCode);
        Assert.Equal(2, stored.Runs.Count);
        Assert.Equal(("narrow-2", "narrow-2"), (stored.Runs[0].Model, stored.Runs[1].Model));
        Assert.Equal(["narrow-1"], stored.Runs[0].Fallback.Select(entry => entry.Model));
        Assert.Equal(["narrow-1"], stored.Runs[1].Fallback.Select(entry => entry.Model));
        Assert.NotNull(stored.Runs[1].Conversation);
        Assert.Equal("go on", stored.Runs[1].UserPrompt);
    }

    /// <summary>
    /// A side-task opened again on another provider's model - whose client is handed no conversation of another's -
    /// goes on from the conversation so far, carried in the new conversation's first message; and so does one opened
    /// again on a client that renders its conversations.
    /// </summary>
    [Fact]
    public async Task ResumeOnAnotherProviderOrClient_GoesOnFromTheConversationSoFar()
    {
        var (_, temp, service, stored, rendered) = await RepoAsync();
        using var tree = temp;

        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "begin here", Model: "glm-5.3", Name: "moved"), Token);
        var other = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "go on there", Resume: "moved", Model: "narrow-1", Efforts: ["high"]), Token);
        var claude = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "and here", Resume: "moved", Model: "claude-sonnet-5-5"), Token);

        Assert.Equal((HarnessExit.Success, HarnessExit.Success), (other.ExitCode, claude.ExitCode));
        Assert.Null(stored.Runs[1].Conversation);
        Assert.StartsWith(AgentCompaction.SaysWhatItIs, stored.Runs[1].Continuation, StringComparison.Ordinal);
        Assert.Contains("begin here", stored.Runs[1].Continuation!, StringComparison.Ordinal);

        var launch = Assert.Single(rendered.Runs);
        Assert.StartsWith(AgentCompaction.SaysWhatItIs, launch.Continuation, StringComparison.Ordinal);
        Assert.Contains("begin here", launch.Continuation!, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a later turn's first message carried of the conversation before it is that conversation's, which the
    /// side-task's own stream holds itself: the conversation is carried once, the person's message alone standing for
    /// that turn - never a record nested in the next, growing with every move.
    /// </summary>
    [Fact]
    public async Task TheConversationCarried_IsCarriedOnce_NeverARecordNestedInTheNext()
    {
        var (_, temp, service, stored, rendered) = await RepoAsync();
        using var tree = temp;

        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "one", Model: "glm-5.3", Name: "carried"), Token);
        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "two", Resume: "carried", Model: "narrow-1", Efforts: ["high"]), Token);

        foreach (var prompt in new[] { "three", "four", "five" })
        {
            await service.RunAsync(new SideTaskCall(temp.Path, Prompt: prompt, Resume: "carried"), Token);
        }

        var six = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "six", Resume: "carried", Model: "claude-sonnet-5-5"), Token);

        Assert.Equal(HarnessExit.Success, six.ExitCode);
        Assert.StartsWith(AgentCompaction.SaysWhatItIs, stored.Runs[1].Continuation, StringComparison.Ordinal);
        Assert.Null(stored.Runs[1].Conversation);
        Assert.All(stored.Runs.Skip(2), run => Assert.NotNull(run.Conversation));

        var carried = Assert.Single(rendered.Runs).Continuation!;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(carried, System.Text.RegularExpressions.Regex.Escape(AgentCompaction.SaysWhatItIs)));
        Assert.Contains("two", carried.ReplaceLineEndings("\n").Split('\n'));
    }

    /// <summary>--resume naming no side-task is refused saying none were written, and one open in another process is refused naming its records.</summary>
    [Fact]
    public async Task ResumeNamingNoneIsRefused_AndOneOpenInAnotherProcessIsRefused()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        var none = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Resume: "no-such"), Token);

        Assert.Equal(HarnessExit.Refused, none.ExitCode);
        Assert.Equal("side-task 'no-such' names no side-task of this repository: list-side-tasks lists them. Nothing was changed.", none.Message);
        Assert.Equal([SideTaskService.NoRecords], none.Details);

        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "open me", Model: "glm-5.3", Name: "held"), Token);
        HeldElsewhere(temp, "held");

        var held = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "again", Model: "glm-5.3", Resume: "held", Json: true), Token);

        Assert.Equal(HarnessExit.Refused, held.ExitCode);
        Assert.Equal($"side-task 'held' is open in another process (process {Environment.ProcessId}); end its chat there, or wait for it to leave. Nothing was changed.", held.Message);
        Assert.Equal([$"Records: {LayoutOf(temp, "held").Directory}"], held.Details);

        var document = JsonNode.Parse(Assert.Single(held.Data))!.AsObject();
        Assert.Equal(("held", LayoutOf(temp, "held").Directory), ((string?)document["task"], (string?)document["records"]));
    }

    /// <summary>
    /// An owner file that does not read as an owner - one holding null - is refused, never read as no owner and claimed
    /// over: the side-task is left as it is, its records named.
    /// </summary>
    [Fact]
    public async Task AnOwnerThatDoesNotReadAsOne_IsRefused_NeverClaimedOver()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "open me", Model: "glm-5.3", Name: "unread"), Token);
        var owner = LayoutOf(temp, "unread").OwnerFile;
        File.WriteAllText(owner, "null");

        var again = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "again", Resume: "unread"), Token);

        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.Equal($"'{owner}' is {SideTaskOwner.OwnerKind}, and does not read as one: it holds null. Nothing was changed: a record is never guessed at.", again.Message);
        Assert.Equal([$"Records: {LayoutOf(temp, "unread").Directory}"], again.Details);
        Assert.Single(stored.Runs);
        Assert.Equal("null", File.ReadAllText(owner));
    }

    /// <summary>An owner recorded for a side-task whose process has gone is reclaimed, said so, and the side-task opens again.</summary>
    [Fact]
    public async Task ADeadOwnersRecord_IsReclaimed_AndSaid()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "open me", Model: "glm-5.3", Name: "orphan"), Token);
        HeldElsewhere(temp, "orphan", stamp: "a process long gone");

        var again = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "again", Resume: "orphan"), Token);

        Assert.Equal(HarnessExit.Success, again.ExitCode);
        Assert.Equal(2, stored.Runs.Count);
        Assert.Contains(
            harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            line => line.EndsWith($"the owner recorded for side-task 'orphan' (process {Environment.ProcessId}) has gone; it was reclaimed", StringComparison.Ordinal));
        Assert.False(File.Exists(LayoutOf(temp, "orphan").OwnerFile), "and released once it ended");
    }

    /// <summary>A name that is taken is refused, never taken over, its records named.</summary>
    [Fact]
    public async Task ANameThatIsTakenIsRefused()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        var first = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "one", Model: "glm-5.3", Name: "mine"), Token);
        Assert.Equal(HarnessExit.Success, first.ExitCode);

        var second = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "two", Model: "glm-5.3", Name: "mine"), Token);

        Assert.Equal(HarnessExit.Refused, second.ExitCode);
        Assert.Equal("side-task 'mine' is taken: --resume mine opens it again, or --name gives a new one another name. Nothing was changed.", second.Message);
        Assert.Equal([$"Records: {LayoutOf(temp, "mine").Directory}"], second.Details);
    }

    /// <summary>A name that begins with what no name begins with, or holds what none holds, is refused by the rule it breaks.</summary>
    [Theory]
    [InlineData("-dash")]
    [InlineData("_under")]
    [InlineData(".dot")]
    [InlineData("a b")]
    public async Task ANameThatBreaksTheRule_IsRefused(string name)
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: name), Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal($"the side-task's name '{name}' is no name a side-task's directory takes: {SideTaskService.NameRule}. Nothing was started.", outcome.Message);
        Assert.Equal([SideTaskService.NoRecords], outcome.Details);
    }

    /// <summary>
    /// The effort a model does not take is a usage error naming what it takes; an effort given to a model the side-task
    /// does not run on is one too, by the one check every step's effort is held to; the entry-by-model form reaches the
    /// model it names.
    /// </summary>
    [Fact]
    public async Task AnEffortAModelDoesNotTake_OrAModelItDoesNotRunOn_IsAUsageError()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        var refused = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "narrow-1", Efforts: ["medium"]), Token);
        var typo = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Efforts: ["glm-5.3=high", "typo-model=high"]), Token);

        Assert.Equal(
            [
                (HarnessExit.UsageError, "the side-task runs at effort 'medium', which 'narrow-1' does not take. It takes: high. Give it a level of its own: --effort narrow-1=high, with all=<level> for the rest"),
                (HarnessExit.UsageError, "the side-task gives an effort to 'typo-model', which it does not run on. It runs on: glm-5.3"),
            ],
            new[] { refused, typo }.Select(outcome => (outcome.ExitCode, outcome.Message)));
        Assert.Empty(stored.Runs);

        var byModel = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "y", Model: "glm-5.3", Efforts: ["glm-5.3=high", "all=low"]), Token);

        Assert.Equal(HarnessExit.Success, byModel.ExitCode);
        Assert.Equal("high", Assert.Single(stored.Runs).Effort);
    }

    /// <summary>
    /// A one shot ends on the code its turn's ending gives everywhere a run's does: a failed turn 20, one cut short by its
    /// account's limit 21, a key missing 13.
    /// </summary>
    [Fact]
    public async Task AOneShotEndsOnTheCodeItsTurnsEndingGives()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        stored.Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Model: "glm-5.3", Error: "the model declined"));
        var failed = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "failed-one"), Token);

        stored.Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.RateLimited, Model: "glm-5.3", Error: "the key is at its limit") { Limit = new AgentAccountLimit(BalanceSpent: false) });
        var limited = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "failed-two"), Token);

        // No key in the slot: the runner refuses the run before it begins.
        File.Delete(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "zai.env"));
        stored.Script = (run, events, _) => StoredClient.Answered(run, events, "never reached");
        var missing = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "failed-three"), Token);

        Assert.Equal(
            [HarnessExit.CommandFailed, HarnessExit.Incomplete, HarnessExit.Refused],
            new[] { failed, limited, missing }.Select(outcome => outcome.ExitCode));
        Assert.Equal("side-task 'failed-one' ended Refused: the model declined", failed.Message);
    }

    /// <summary>A turn the runner refuses names the side-task's records in its document too, for they were written.</summary>
    [Fact]
    public async Task ATurnRefused_NamesItsRecordsInItsDocument()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;

        stored.Script = (_, _, _) => throw new HarnessException(HarnessExit.Refused, "the provider refused the key");

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "refused", Json: true), Token);

        var directory = LayoutOf(temp, "refused").Directory;
        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal([$"Records: {directory}"], outcome.Details);

        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();
        Assert.Equal(
            (HarnessExit.Refused, "the provider refused the key", "refused", directory),
            ((int)document["exitCode"]!, (string?)document["summary"], (string?)document["task"], (string?)document["records"]));
        Assert.Equal(SideTaskStatus.Ended, RecordOf(harness, temp, directory)!.Status);
    }

    /// <summary>
    /// What a side-task used in all is its turns' together, by the one fold of usage over parts
    /// (<see cref="AgentUsageReport.Sum"/>): a turn whose client reports no cost adds what was counted of it to the
    /// reported whole, a turn this tool did not count leaves the counted whole unknown, and a turn whose price was missed
    /// leaves the whole unknown - never a sum that leaves a part out.
    /// </summary>
    [Fact]
    public void WhatItUsedInAll_IsItsTurnsTogether_ByTheOneFoldOfUsage()
    {
        static AgentUsageReport Turn(AgentCost cost) => new(new AgentUsage(10, 5, 0, 0, 0, 0), [new AgentModelUsage("glm-5.3", new AgentUsage(10, 5, 0, 0, 0, 0))], cost);

        var two = AgentUsageReport.Sum(Turn(new(Counted: 0.5m)), Turn(new(Reported: 0.25m)))!;

        Assert.Equal(new AgentCost(Reported: 0.75m, Counted: null), two.Cost);
        Assert.Equal(0.75m, two.Cost!.Usd);

        var three = AgentUsageReport.Sum(two, Turn(new(MissingPrice: "glm-5.3")))!;

        Assert.Equal(new AgentCost(MissingPrice: "glm-5.3"), three.Cost);
        Assert.Null(three.Cost!.Usd);
        Assert.Equal(new AgentUsage(30, 15, 0, 0, 0, 0), three.Total);
        Assert.Equal(["glm-5.3", "glm-5.3", "glm-5.3"], three.PerModel.Select(part => part.Model));
        Assert.Same(two, AgentUsageReport.Sum(two, null));
        Assert.Null(AgentUsageReport.Sum(null, null));
    }

    /// <summary>The listing: each side-task newest first, with its model, turns and whether it is open now.</summary>
    [Fact]
    public async Task TheListingNamesThem_NewestFirst_WithWhetherEachIsOpenNow()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "one", Model: "glm-5.3", Name: "older"), Token);
        await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "two", Model: "glm-5.3", Name: "newer"), Token);
        HeldElsewhere(temp, "newer");

        var listed = await service.ListAsync(temp.Path, json: false, Token);
        var lines = listed.Details!.ToList();

        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        Assert.True(
            lines.FindIndex(line => line.StartsWith("newer  ", StringComparison.Ordinal))
                < lines.FindIndex(line => line.StartsWith("older  ", StringComparison.Ordinal)),
            "newest first");
        Assert.Contains(listed.Details!, line => line.StartsWith("newer  ", StringComparison.Ordinal) && line.EndsWith(", open now", StringComparison.Ordinal));
        Assert.Contains(listed.Details!, line => line.StartsWith("older  ", StringComparison.Ordinal) && !line.EndsWith(", open now", StringComparison.Ordinal));

        var json = await service.ListAsync(temp.Path, json: true, Token);
        var document = JsonNode.Parse(Assert.Single(json.Data))!.AsObject();
        var tasks = document["sideTasks"]!.AsArray();

        Assert.Equal(2, tasks.Count);
        Assert.Equal("newer", (string)tasks[0]!["name"]!);
        Assert.True((bool)tasks[0]!["open"]!);
        Assert.False((bool)tasks[1]!["open"]!);
    }

    /// <summary>
    /// The terminal's interrupts are counted for the side-task and said by their place in the count - the first cuts
    /// the turn, a second ends the chat - and a hang-up is one more, after which nothing more is said.
    /// </summary>
    [Fact]
    public void TheTerminalsInterrupts_AreCountedAndSaid()
    {
        var harness = new HarnessFactory();
        var chat = new SideTaskTerminal(harness.Output, chat: true);

        chat.Interrupted();
        Assert.False(chat.Interrupts.Ended.IsCompleted);
        chat.HungUp();

        Assert.Equal(2, chat.Interrupts.Presses);
        Assert.True(chat.Interrupts.Ended.IsCompleted);

        var said = harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.EndsWith(SideTaskTerminal.ChatCut, Assert.Single(said), StringComparison.Ordinal);

        var oneShot = new SideTaskTerminal(new HarnessFactory().Output, chat: false);
        oneShot.Interrupted();
        Assert.Equal(1, oneShot.Interrupts.Presses);
    }
}
