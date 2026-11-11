using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeApi;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Http;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Agents.SideTasks;
using DssHarness.Core.Agents.ZaiApi;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Secrets;
using DssHarness.Core.Web;
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
            await events.EmitAsync(new TurnStarted(1));

            // The provider began its answer: the conversation is begun, as the API clients tell it.
            await events.ConversationBegunAsync();
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
    /// as the real client says it - the first as the first prompt, each later one as the chat's, the moment it is written
    /// to the launch - and its result counting, as the real client's does, the messages its turns answered.
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

        /// <summary>
        /// Where set, each launch's turns past this many outgrow its context, as a launch that can hold no more does: the
        /// next message is written to it and taken by a turn that ends without answering, and the launch ends with it.
        /// </summary>
        public int? EndsAfterTurns { get; set; }

        /// <summary>Where set, each launch's turn of this number fails, as an API error ends one, and the launch with it.</summary>
        public int? FailsOnTurn { get; set; }

        /// <summary>Whether the turn that fails ends with no result of its own, as a program gone in the middle of a turn does.</summary>
        public bool FailsWithoutAResult { get; set; }

        /// <summary>
        /// Where set, each launch is gone before its turn of this number begins - the turn before it ended, the message
        /// written to it waiting and never taken - as a program gone between two turns is.
        /// </summary>
        public int? GoneBeforeTurn { get; set; }

        /// <summary>
        /// Where set, asked with the launch's number and the turn's once each turn has ended: a launch it answers true for
        /// is gone there - idle, nothing written to it waiting - as a program that crashes between turns is.
        /// </summary>
        public Func<int, int, bool>? GoneAfter { get; set; }

        /// <summary>Where set, a launch on this model is never started, as one its client refuses before it begins.</summary>
        public string? NotStartedOn { get; set; }

        /// <summary>
        /// Where set, asked with the launch's number and the turn's before each turn: an ending it answers is the turn's,
        /// which takes its message and ends the launch with no result of its own.
        /// </summary>
        public Func<int, int, AgentRunResult?>? EndsWith { get; set; }

        /// <summary>Where set, waited on before each turn of a launch answers, with the turn's number.</summary>
        public Func<int, Task>? BeforeAnswering { get; set; }

        /// <summary>
        /// Where set, waited on inside each turn of a launch, once it began and its text came, with the turn's number; the
        /// messages written to the launch meanwhile are said then - as the real client says each the moment it is written -
        /// and each waits for a turn of its own.
        /// </summary>
        public Func<AgentRun, int, Task>? WhileTurning { get; set; }

        /// <summary>Where set, what each launch that ends well reports it cost, booked to the key it signed in with, as Claude Code reports a launch's.</summary>
        public decimal? LaunchCost { get; set; }

        public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            lock (Runs)
            {
                Runs.Add(run);
            }

            if (run.Model == NotStartedOn)
            {
                return AgentEndings.NotStarted(ClientName, AgentNotStartedBecause.ConfigurationWrong, "the model has no maxOutputTokens", run.Model, run.Mask);
            }

            await events.EmitAsync(new SessionStarted(ClientName, run.Model ?? "m", "hash", [], [], HandedConversation: false, Provider: "anthropic"));

            // The first message is the launch's own prompt; each later one reaches the agent through the channel a
            // message to an agent at work uses - in a chat, whose launch ends when the channel does.
            await events.EmitAsync(new MessageSent(AgentPromptParts.FirstMessage(run, newConversation: true), AgentMessageKind.FirstPrompt));

            var turns = 0;
            var answered = 0;
            var message = run.UserPrompt;
            var written = new Queue<string>();
            await using var later = run.Messages.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

            // A chat's interrupt the launch is begun with, raised already, cuts what is under way as it begins - its first
            // message, written and not taken yet - as the real client's does: dropped, settled, and no turn answers it.
            if (run.Chat && run.Interrupt is { Asked.IsCompleted: true })
            {
                answered++;
                await events.EmitAsync(new MessagesDropped(1, 0));

                if (!await later.MoveNextAsync())
                {
                    return Ended(AgentRunStatus.Completed, null);
                }

                message = later.Current;
                await events.EmitAsync(new MessageSent(message, AgentMessageKind.ChatMessage));
            }

            while (true)
            {
                turns++;

                if (turns == GoneBeforeTurn)
                {
                    // The program is gone with the message written to it and never taken.
                    return new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "the program is gone") { Turns = turns - 1, MessagesAnswered = Chat(answered) };
                }

                if (EndsWith?.Invoke(Launched(), turns) is { } ending)
                {
                    await events.EmitAsync(new TurnStarted(turns));
                    return ending with { Turns = turns, MessagesAnswered = Chat(answered) };
                }

                if (turns == FailsOnTurn || turns - 1 == EndsAfterTurns)
                {
                    // The turn takes the message and ends without answering it, and the launch ends with that turn.
                    await events.EmitAsync(new TurnStarted(turns));

                    if (!(turns == FailsOnTurn && FailsWithoutAResult))
                    {
                        // A turn that ended stands: the conversation is begun, as Claude Code tells it.
                        await events.ConversationBegunAsync();
                        await events.EmitAsync(new TurnFinished(turns, run.Model ?? "m", new AgentUsage(7, 3, 0, 0, 0, 0), "error"));
                    }

                    // The launch ends at that turn's own result, where it had one.
                    var atItsEnd = !(turns == FailsOnTurn && FailsWithoutAResult);

                    return turns == FailsOnTurn
                        ? new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "the API failed") { Turns = turns, MessagesAnswered = Chat(answered), EndedWithItsLastTurn = atItsEnd }
                        : new AgentRunResult(AgentRunStatus.ContextExhausted, Model: run.Model, Error: "the context is full") { Turns = turns, MessagesAnswered = Chat(answered), EndedWithItsLastTurn = atItsEnd };
                }

                if (BeforeAnswering is { } before)
                {
                    await before(turns);
                }

                await OneTurnAsync(run, events, turns, message, written);
                answered++;

                // A one shot's turn the person interrupted ends the launch at the cut turn's result, with what it used.
                if (!run.Chat && run.Interrupt is { Asked.IsCompleted: true })
                {
                    return Ended(AgentRunStatus.Cancelled, ClaudeCodeClient.TurnCut);
                }

                if (GoneAfter?.Invoke(Launched(), turns) == true)
                {
                    // The program is gone, idle, with nothing written to it waiting.
                    return new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "the program is gone") { Turns = turns, MessagesAnswered = Chat(answered) };
                }

                // The person left, or chose another model, while the turn ran: the launch ends at the turn's result, the
                // lines written to it meanwhile dropped untaken, as the real client's interrupt drops them.
                if (run.Chat && run.Messages.Ended.IsCompleted && written.Count > 0)
                {
                    break;
                }

                if (run.Chat && written.TryDequeue(out var waited))
                {
                    message = waited;
                    continue;
                }

                if (!run.Chat || !await later.MoveNextAsync())
                {
                    break;
                }

                // Written to the launch as it comes, and said so the moment it is - as the real client says each message.
                message = later.Current;
                await events.EmitAsync(new MessageSent(message, AgentMessageKind.ChatMessage));
            }

            return Ended(AgentRunStatus.Completed, null);

            int? Chat(int count) => run.Chat ? count : null;

            // This launch's number among the client's launches, from one.
            int Launched()
            {
                lock (Runs)
                {
                    return Runs.Count;
                }
            }

            // The launch ended at its last turn's end, with what it used and cost.
            AgentRunResult Ended(AgentRunStatus status, string? error)
            {
                var used = new AgentUsage(7, 3, 0, 0, 0, 0);

                return new AgentRunResult(status, FinalText: "the launch's last word", Model: run.Model, Error: error)
                {
                    Turns = turns,
                    Usage = LaunchCost is { } cost
                        ? new AgentUsageReport(used, [new AgentModelUsage(run.Model ?? "m", used, run.Credential?.Keys[0].Name, new AgentCost(Reported: cost))], new AgentCost(Reported: cost))
                        : new AgentUsageReport(used, []),
                    MessagesAnswered = Chat(answered),
                    EndedWithItsLastTurn = true,
                };
            }
        }

        private async Task OneTurnAsync(AgentRun run, IAgentEventSink events, int turn, string message, Queue<string> written)
        {
            var about = "about: " + message[..Math.Min(20, message.Length)];

            // The turn begins taking its message, said as the real client says each message it takes.
            await events.EmitAsync(new TurnStarted(turn));
            await events.EmitAsync(new TextDelta(about));

            if (WhileTurning is { } meanwhile)
            {
                await meanwhile(run, turn);

                while (run.Messages.TryTake(out var line))
                {
                    written.Enqueue(line);
                    await events.EmitAsync(new MessageSent(line, AgentMessageKind.ChatMessage));
                }
            }

            await events.EmitAsync(new AssistantMessage(about, []));

            // A turn that ended stands: the conversation is begun, as Claude Code tells it.
            await events.ConversationBegunAsync();
            await events.EmitAsync(new TurnFinished(turn, run.Model ?? "m", new AgentUsage(7, 3, 0, 0, 0, 0), "end_turn"));
        }
    }

    /// <summary>
    /// A graph service that holds nothing: every side-task test's default, so the conversation alone is what runs and
    /// nothing of the graph is served or said. The graph's own tests pass the real service.
    /// </summary>
    private sealed class EmptyGraph : DssHarness.Core.Codebase.ICodebaseGraphService
    {
        public Task<DssHarness.Core.Codebase.CodebaseGraphSynced> SyncAsync(
            DssHarness.Core.Codebase.CodebaseGraphSync request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DssHarness.Core.Codebase.CodebaseGraphSynced(
                request.Run.Name,
                DssHarness.Core.Codebase.CodebaseGraphService.BaseView,
                Commit: null,
                Dirty: false,
                Files: 0,
                Listed: 0,
                Hashed: 0,
                Extracted: 0,
                Reused: 0,
                Removed: 0,
                Nodes: 0,
                Edges: 0,
                Languages: [],
                Skipped: [],
                Seconds: 0));

        public Task<DssHarness.Core.Codebase.CodebaseGraphSynced?> SyncPathsAsync(
            DssHarness.Core.Codebase.CodebaseGraphSync request,
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DssHarness.Core.Codebase.CodebaseGraphSynced?>(null);

        public Task<DssHarness.Core.Codebase.CodebaseGraphChecked> CheckAsync(
            DssHarness.Core.Codebase.CodebaseGraphSync request,
            IReadOnlyList<string> paths,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DropViewAsync(DssHarness.Core.Orchestration.OrchestratorLayout run, string agent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<DssHarness.Core.Codebase.CodebaseGraphRead?> ReadOnlyAsync(
            DssHarness.Core.Orchestration.OrchestratorLayout run,
            string? agent,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DssHarness.Core.Codebase.CodebaseGraphRead?>(null);

        public Task<IReadOnlyList<string>> DifferingAsync(
            DssHarness.Core.Codebase.CodebaseGraphSync request,
            string baseCommit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<DssHarness.Core.Codebase.CodebaseGraphChanges> ChangesAsync(
            DssHarness.Core.Codebase.CodebaseGraphSync request,
            DssHarness.Core.Codebase.CodebaseGraphRead read,
            string baseCommit,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// The real graph service over the test's repository, as a side-task's own graph is opened by: the tree listed, its
    /// files extracted, its map made - a source file the real extractors read among them.
    /// </summary>
    private static DssHarness.Core.Codebase.ICodebaseGraphService RealGraph(HarnessFactory harness)
        => new DssHarness.Core.Codebase.CodebaseGraphService(
            harness.GitClient,
            harness.FileSystem,
            harness.OrchestrationStore,
            DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild,
            harness.Platform,
            new RunningToolDouble(),
            harness.Output,
            TimeProvider.System);

    /// <summary>
    /// A side-task has its own codebase graph, under its records: opened - synced whole - before its first message is
    /// sent, its repository map made once from the base as it stands and given with that message alone, a context pack
    /// made from each message, and its agent given the graph tool among its own.
    /// </summary>
    [Fact]
    public async Task ASideTask_HasItsOwnGraph_ItsMapWithItsFirstMessage_APackWithEach_AndTheToolForItsAgent()
    {
        var (harness, temp, service, stored, _) = await RepoAsync(graphs: RealGraph);
        OrchestrationKit.Write(temp.Path, "Greeter.cs", "public class Greeter\n{\n    public string Hello() => \"hi\";\n}\n");

        var outcome = await service.RunAsync(
            new SideTaskCall(temp.Path, Prompt: "where does Greeter say hello", Model: "glm-5.3", Json: false),
            Token);

        Assert.Equal(0, outcome.ExitCode);
        var task = Assert.Single(Directory.EnumerateDirectories(temp.Path, ".side-tasks", SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(task, "graph.json", SearchOption.AllDirectories));
        var map = Assert.Single(Directory.EnumerateFiles(task, "map.md", SearchOption.AllDirectories));

        Assert.Contains("Greeter", File.ReadAllText(map), StringComparison.Ordinal);
        Assert.Contains("<codebase>", stored.Runs[0].UserPrompt, StringComparison.Ordinal);
        Assert.EndsWith("where does Greeter say hello", stored.Runs[0].UserPrompt, StringComparison.Ordinal);
        Assert.NotNull(stored.Runs[0].Graph);

        // Its parts are written by the one writer of a first message's parts, in their form, the map's first.
        Assert.StartsWith(AgentPromptParts.MapPart(File.ReadAllText(map)) + $"<{AgentPromptParts.CodebaseTag}>{Environment.NewLine}", stored.Runs[0].UserPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A side-task's <c>find</c> breaks a tie by how near each node is to the work of the person's last message - its
    /// pack, the files it names - so of two declarations that match alike, the one the message names comes first.
    /// </summary>
    [Fact]
    public async Task ASideTasksFind_BreaksATieByNearnessToItsLastMessage()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Path.Combine("src", "One", "Widget.cs"), "namespace One;\n\npublic sealed class Widget\n{\n}\n");
        OrchestrationKit.Write(kit.Main, Path.Combine("src", "Two", "Widget.cs"), "namespace Two;\n\npublic sealed class Widget\n{\n}\n");
        await kit.Harness.CommitAllAsync(kit.Main, "code", Token);
        var repository = new DssHarness.Core.Repository.HarnessLayout(kit.Main, kit.Main);
        var graph = new SideTaskGraph(kit.Harness.CodebaseGraph, kit.Harness.FileSystem, kit.Harness.Platform, kit.Harness.GitClient, kit.Harness.OrchestrationStore, TimeProvider.System, repository, SideTaskLayout.Of(repository, "t1"), Path.Combine(kit.Main, OrchestrationKit.Settings.Root));
        await graph.OpenAsync(Token);
        var view = new DssHarness.Core.Codebase.CodebaseGraphQueries((await kit.Harness.CodebaseGraph.ReadOnlyAsync(SideTaskLayout.Of(repository, "t1").AsGraph, null, Token))!);
        var widgets = view.Find("Widget", "type", null, 10, 0).Cards.OrderBy(card => card.Id, StringComparer.Ordinal).ToList();

        Assert.Equal(2, widgets.Count);

        // The message names the namespace of the one the ids put last: its pack holds that one, and not the other.
        var named = widgets[^1].Path;
        _ = graph.Front($"look under {widgets[^1].Qualified.Split('.')[0]}", first: true);
        var support = graph.Support(SideTaskPolicy.Ground(repository, new HarnessConfig { Worktrees = new WorktreeSettings() }, [], []))!;
        var found = await support.Tool!.RunAsync(new JsonObject { ["op"] = "find", ["query"] = "Widget", ["kind"] = "type" }, CodebaseGraphTool.Operations(write: true, narrow: false), Token);
        var near = found.Text.IndexOf($"| {named}:", StringComparison.Ordinal);
        var far = found.Text.IndexOf($"| {widgets[0].Path}:", StringComparison.Ordinal);

        Assert.True(near >= 0 && far > near, $"the nearer declaration is not first:\n{found.Text}");
    }

    /// <summary>
    /// One tool held for the whole chat - as Claude Code's one launch holds it - breaks <c>find</c>'s ties by the person's
    /// last message, not the one the tool was made after: each message's pack moves what is nearest.
    /// </summary>
    [Fact]
    public async Task OneToolForAWholeChat_BreaksFindsTiesByTheLastMessage()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Path.Combine("src", "One", "Widget.cs"), "namespace One;\n\npublic sealed class Widget\n{\n}\n");
        OrchestrationKit.Write(kit.Main, Path.Combine("src", "Two", "Widget.cs"), "namespace Two;\n\npublic sealed class Widget\n{\n}\n");
        await kit.Harness.CommitAllAsync(kit.Main, "code", Token);
        var repository = new DssHarness.Core.Repository.HarnessLayout(kit.Main, kit.Main);
        var graph = new SideTaskGraph(kit.Harness.CodebaseGraph, kit.Harness.FileSystem, kit.Harness.Platform, kit.Harness.GitClient, kit.Harness.OrchestrationStore, TimeProvider.System, repository, SideTaskLayout.Of(repository, "t1"), Path.Combine(kit.Main, OrchestrationKit.Settings.Root));
        await graph.OpenAsync(Token);

        // The tool is made once, after the first message, and the chat goes on with it.
        _ = graph.Front("look under One", first: true);
        var tool = graph.Support(SideTaskPolicy.Ground(repository, new HarnessConfig { Worktrees = new WorktreeSettings() }, [], []))!.Tool!;

        async Task<string> FirstWidgetAsync()
        {
            var found = await tool.RunAsync(new JsonObject { ["op"] = "find", ["query"] = "Widget", ["kind"] = "type" }, CodebaseGraphTool.Operations(write: true, narrow: false), Token);
            var one = found.Text.IndexOf("| src/One/Widget.cs:", StringComparison.Ordinal);
            var two = found.Text.IndexOf("| src/Two/Widget.cs:", StringComparison.Ordinal);

            Assert.True(one >= 0 && two >= 0, $"find did not give both:\n{found.Text}");
            return one < two ? "One" : "Two";
        }

        Assert.Equal("One", await FirstWidgetAsync());

        _ = graph.Front("now look under Two", first: false);

        Assert.Equal("Two", await FirstWidgetAsync());

        _ = graph.Front("and back under One", first: false);

        Assert.Equal("One", await FirstWidgetAsync());
    }

    /// <summary>
    /// A side-task given --no-graph has none of it: no graph under its records, no map and no pack at its message's
    /// head, no tool for its agent - the person's message as it was typed.
    /// </summary>
    [Fact]
    public async Task ASideTaskGivenNoGraph_HasNone_ItsMessageAsTyped()
    {
        var (harness, temp, service, stored, _) = await RepoAsync(graphs: RealGraph);
        OrchestrationKit.Write(temp.Path, "Greeter.cs", "public class Greeter { }\n");

        var outcome = await service.RunAsync(
            new SideTaskCall(temp.Path, Prompt: "hello there", Model: "glm-5.3", Json: false) { NoGraph = true },
            Token);

        Assert.Equal(0, outcome.ExitCode);
        var task = Assert.Single(Directory.EnumerateDirectories(temp.Path, ".side-tasks", SearchOption.AllDirectories));

        Assert.False(Directory.Exists(Path.Combine(task, "graph")));
        Assert.Equal("hello there", stored.Runs[0].UserPrompt);
        Assert.Null(stored.Runs[0].Graph);
    }

    /// <summary>
    /// The chat's later messages carry the pack and not the map: the map is a cache of the base as the side-task opened,
    /// read as it is, while the pack is made from each message the person types.
    /// </summary>
    [Fact]
    public async Task AChatsLaterMessages_CarryThePackAndNotTheMap()
    {
        var (harness, temp, service, stored, _) = await RepoAsync(graphs: RealGraph);
        OrchestrationKit.Write(temp.Path, "Greeter.cs", "public class Greeter { }\n");
        var typed = new Typed(["hello", "and again"], until: "the answer");

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Chat: typed.Chat()), Token);

        Assert.Equal(0, outcome.ExitCode);
        Assert.StartsWith("<repository-map>", stored.Runs[0].UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<repository-map>", stored.Runs[1].UserPrompt, StringComparison.Ordinal);
        Assert.Contains("<codebase>", stored.Runs[1].UserPrompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A chat over a client that holds one launch open - Claude Code - gives each later message the pack as the chat over
    /// a client that stores its conversations does: the map with the first message alone, the pack made from each message
    /// at its head as it is posted to the launch, never the line bare.
    /// </summary>
    [Fact]
    public async Task AChatOnALaunchHeldOpen_GivesEachLaterMessageItsPack_AndTheMapWithTheFirstAlone()
    {
        var (_, temp, service, _, rendered) = await RepoAsync(graphs: RealGraph);
        using var tree = temp;
        OrchestrationKit.Write(temp.Path, "Greeter.cs", "public class Greeter { }\n");
        var typed = new Typed(["hello", "and again"]);

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "held", Chat: typed.Chat()), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith("<repository-map>", Assert.Single(rendered.Runs).UserPrompt, StringComparison.Ordinal);

        // What each turn of the launch took, said by its first twenty characters: the first message its prompt, the later
        // one as it was posted.
        Assert.Equal(2, typed.Said.Count);
        Assert.StartsWith($"about: <repository-map>{Environment.NewLine}", typed.Said[0], StringComparison.Ordinal);
        Assert.StartsWith($"about: <codebase>{Environment.NewLine}", typed.Said[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A side-task whose graph cannot be opened - its records cannot be written, or the graph service refuses - goes on
    /// without it, its message sent as typed and no graph tool given, and says so once, with why: in its one shot's
    /// answer beside its records, and in its graph's record as the prompt line that names why no pack is given.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASideTaskWhoseGraphCannotBeOpened_GoesOnWithoutIt_SayingSoOnceWithWhy(bool io)
    {
        Exception failure = io
            ? new IOException("There is not enough space on the disk.")
            : new HarnessException(HarnessExit.CommandFailed, "The graph's records do not read.");
        var (harness, temp, service, stored, _) = await RepoAsync(graphs: _ => new UnopenableGraph(failure));
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "hello there", Model: "glm-5.3", Name: "blind"), Token);

        var directory = LayoutOf(temp, "blind").Directory;
        var why = failure.Message.TrimEnd('.');

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal([$"side-task 'blind' runs without its codebase graph, which could not be opened: {why}", $"Records: {directory}"], outcome.Details);
        Assert.Equal("hello there", Assert.Single(stored.Runs).UserPrompt);
        Assert.Null(stored.Runs[0].Graph);
        Assert.Equal(
            new GraphPromptLine(0, 0, 0, 0, 0, $"the side-task's graph could not be opened: {why}"),
            Assert.Single(harness.OrchestrationStore.ReadGraphStepLines(directory)));
    }

    /// <summary>A chat whose graph cannot be opened says so once, as the chat's first line, and goes on without it.</summary>
    [Fact]
    public async Task AChatWhoseGraphCannotBeOpened_SaysSoAsItsFirstLine()
    {
        var failure = new UnauthorizedAccessException("Access to the path 'graph' is denied.");
        var (_, temp, service, stored, _) = await RepoAsync(graphs: _ => new UnopenableGraph(failure));
        using var tree = temp;
        var typed = new Typed(["hello"], until: "the answer");

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "blind", Chat: typed.Chat()), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            ["side-task 'blind' runs without its codebase graph, which could not be opened: Access to the path 'graph' is denied", "the answer"],
            typed.Said);
        Assert.Equal("hello", Assert.Single(stored.Runs).UserPrompt);
    }

    /// <summary>A graph service that cannot open anything: every call fails with the one failure it is given.</summary>
    private sealed class UnopenableGraph(Exception failure) : DssHarness.Core.Codebase.ICodebaseGraphService
    {
        public Task<DssHarness.Core.Codebase.CodebaseGraphSynced> SyncAsync(DssHarness.Core.Codebase.CodebaseGraphSync request, CancellationToken cancellationToken = default)
            => Task.FromException<DssHarness.Core.Codebase.CodebaseGraphSynced>(failure);

        public Task<DssHarness.Core.Codebase.CodebaseGraphSynced?> SyncPathsAsync(DssHarness.Core.Codebase.CodebaseGraphSync request, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromException<DssHarness.Core.Codebase.CodebaseGraphSynced?>(failure);

        public Task<DssHarness.Core.Codebase.CodebaseGraphChecked> CheckAsync(DssHarness.Core.Codebase.CodebaseGraphSync request, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
            => Task.FromException<DssHarness.Core.Codebase.CodebaseGraphChecked>(failure);

        public Task DropViewAsync(DssHarness.Core.Orchestration.OrchestratorLayout run, string agent, CancellationToken cancellationToken = default)
            => Task.FromException(failure);

        public Task<DssHarness.Core.Codebase.CodebaseGraphRead?> ReadOnlyAsync(DssHarness.Core.Orchestration.OrchestratorLayout run, string? agent, CancellationToken cancellationToken = default)
            => Task.FromException<DssHarness.Core.Codebase.CodebaseGraphRead?>(failure);

        public Task<IReadOnlyList<string>> DifferingAsync(DssHarness.Core.Codebase.CodebaseGraphSync request, string baseCommit, CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<string>>(failure);

        public Task<DssHarness.Core.Codebase.CodebaseGraphChanges> ChangesAsync(
            DssHarness.Core.Codebase.CodebaseGraphSync request,
            DssHarness.Core.Codebase.CodebaseGraphRead read,
            string baseCommit,
            CancellationToken cancellationToken = default)
            => Task.FromException<DssHarness.Core.Codebase.CodebaseGraphChanges>(failure);
    }

    /// <summary>
    /// A side-task's graph's paths are the graph records' own - the directory, the map and what the map was made from -
    /// named by the one layout every graph's records take, so neither can drift from the other.
    /// </summary>
    [Fact]
    public void ASideTasksGraphPaths_AreTheOneGraphLayoutsOwn()
    {
        var layout = new SideTaskLayout(Path.Combine(Path.GetTempPath(), ".side-tasks"), "named");

        Assert.Equal(
            (layout.AsGraph.GraphDirectory, layout.AsGraph.GraphMapFile, layout.AsGraph.GraphMapMetaFile),
            (layout.GraphDirectory, layout.MapFile, layout.MapMetaFile));
        Assert.Equal(Path.Combine(layout.Directory, DssHarness.Core.Orchestration.OrchestratorLayout.GraphDirectoryName), layout.GraphDirectory);
    }

    /// <summary>
    /// The chat's front end as a person's typed lines: one read per message, the chat's own lines collected, and a
    /// completion heard once the chat has said that line - as a person who waits for the answer before leaving.
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

    /// <summary>
    /// A repository with the template's providers - and one model of a narrow effort list - over the two fakes, the Claude
    /// Code a launch starts made ready by <paramref name="updates"/> where one is given; or, where <paramref name="apis"/>
    /// is given, over the API clients it builds in place of the fake that stores its conversations.
    /// </summary>
    private static async Task<(HarnessFactory Harness, TempDirectory Tree, SideTaskService Service, StoredClient Stored, RenderedClient Rendered)> RepoAsync(
        TimeProvider? clock = null,
        Func<string, string?>? environment = null,
        DssHarness.Core.Agents.ClaudeCode.ClaudeCodeUpdateService? updates = null,
        Func<int, ProcessTableReading>? tree = null,
        Dictionary<string, AgentClientConfig>? clients = null,
        ClientGates? gates = null,
        Func<HarnessFactory, IReadOnlyList<IAgentClient>>? apis = null,
        DssHarness.Core.Agents.Isolation.IAgentIsolation? isolation = null,
        TimeProvider? runnerClock = null,
        Func<HarnessFactory, DssHarness.Core.Codebase.ICodebaseGraphService>? graphs = null)
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

        // A tier of two models Claude Code reaches, for a move between two launches held open; one of three, the third
        // reached by an API client; and one whose first model an API client reaches, Claude Code its fallback.
        foreach (var model in agents.Providers.SelectMany(provider => provider.Models).Where(model => model.Code is "claude-sonnet-5-5" or "claude-opus-5-5"))
        {
            model.Tiers.Add("claudes");
        }

        foreach (var model in agents.Providers.SelectMany(provider => provider.Models).Where(model => model.Code is "claude-sonnet-5-5" or "claude-opus-5-5" or "glm-5.3"))
        {
            model.Tiers.Add("trio");
        }

        foreach (var model in agents.Providers.SelectMany(provider => provider.Models).Where(model => model.Code is "claude-sonnet-5-5" or "glm-5.3"))
        {
            model.Tiers.Add("api-first");
        }

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            Defaults = new() { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers, narrow],
            TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase) { ["narrow"] = ["narrow-1"], ["pair"] = ["narrow-2", "narrow-1"], ["claudes"] = ["claude-sonnet-5-5", "claude-opus-5-5"], ["trio"] = ["claude-sonnet-5-5", "claude-opus-5-5", "glm-5.3"], ["api-first"] = ["glm-5.3", "claude-sonnet-5-5"] },
            AgentClients = clients ?? new(StringComparer.OrdinalIgnoreCase),
        });

        foreach (var (name, value) in new[] { ("zai.env", "ZAI_API_KEY=sk-test-one\n"), ("anthropic.env", "ANTHROPIC_API_KEY=sk-test-two\n"), ("narrow.env", "NARROW_API_KEY=sk-test-three\n") })
        {
            OrchestrationKit.Write(temp.Path, Path.Combine(".harness-config", "workflows", ".env", name), value);
            harness.FilePermissions.ProtectSecret(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", name));
        }

        var stored = new StoredClient();
        var rendered = new RenderedClient();
        IReadOnlyList<IAgentClient> serving = apis is null ? [stored, rendered] : [.. apis(harness), rendered];
        var runner = new AgentRunner(serving, harness.FileSystem, harness.Platform, runnerClock ?? TimeProvider.System, gates);
        var keys = new AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform);
        var service = new SideTaskService(
            harness.ContextLoader,
            runner,
            serving,
            harness.Platform,
            harness.Identity,
            harness.OrchestrationStore,
            keys,
            new WebSearchKeys(keys, new GhProcess(harness.ProcessRunner)),
            updates,
            harness.FileSystem,
            harness.Output,
            harness.GitClient,
            new AgentAncestry(new AgentAncestryTests.FixedTable(tree?.Invoke(harness.Identity.CurrentId) ?? AgentAncestryTests.UnderAShell(harness.Identity.CurrentId)), harness.Identity, new AgentAncestryTests.OwnProgramAt(null), harness.Platform, harness.FileSystem),
            isolation ?? new TestIsolation(),
            graphs is null ? new EmptyGraph() : graphs(harness),
            clock,
            environment ?? (_ => null));

        return (harness, temp, service, stored, rendered);
    }

    /// <summary>
    /// The two API clients the tool builds in - claude-api and glm - over <paramref name="endpoint"/> in place of the
    /// network, their waits taken at once; what they open is let go with <paramref name="opened"/>.
    /// </summary>
    private static Func<HarnessFactory, IReadOnlyList<IAgentClient>> ApiClients(ApiEndpoint endpoint, List<IDisposable> opened)
        => harness =>
        {
            var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
            var connection = new ApiConnection(TimeProvider.System, endpoint);
            var paced = new TempDirectory();
            var indexes = new DssHarness.Core.Web.Index.WebIndexes(harness.FileSystem, new DssHarness.Core.Web.Index.SitemapFill(harness.FileSystem, fetch, TimeProvider.System, (_, _) => Task.CompletedTask), TimeProvider.System);
            var pace = new WebSearchPace(harness.FileSystem, () => Path.Combine(paced.Path, "pace.json"), TimeProvider.System, (_, _) => Task.CompletedTask);
            var search = new HarnessWebSearch(WebSearchKinds.OfThisBuild, fetch, indexes, pace, harness.FileSystem, TimeProvider.System);
            var host = new AgentToolHost(harness.FileSystem, harness.Platform, harness.GitClient, new PhaseRunner(harness.ProcessRunner, harness.FileSystem, harness.Output), harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System, search: search);
            opened.AddRange([search, indexes, fetch, connection, paced]);

            return
            [
                new ClaudeApiClient(harness.FileSystem, host, TimeProvider.System, connection, (_, _) => Task.CompletedTask, () => 0),
                new ZaiApiClient(harness.FileSystem, host, TimeProvider.System, connection, (_, _) => Task.CompletedTask, () => 0),
            ];
        };

    /// <summary>
    /// The conversation so far as a new conversation's first message carries it, where every iteration is kept word for
    /// word: compacted at <paramref name="iterations"/>, the task as it was first sent, the iterations' own lines, and the
    /// task again.
    /// </summary>
    private static string Carried(int iterations, string task, IReadOnlyList<string> last)
        => string.Join(
            "\n",
            [
                $"{AgentCompaction.SaysWhatItIs}{iterations}. Everything up to iteration 0 is reduced to what you wrote and what you called; the last {iterations} iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                task,
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                .. last,
                string.Empty,
                "The task again:",
                string.Empty,
                task,
            ]);

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

    /// <summary>
    /// Where agentPolicy.isolation is required and the machine has no isolation of an agent's commands, the side-task is
    /// refused with the code for what could not be started, saying why and what would give it one, and nothing is started;
    /// where its isolation was decided, each turn's record says what its commands ran under, and the client is handed it.
    /// The isolation is asked of the side-task's own ground and its records' directory.
    /// </summary>
    [Fact]
    public async Task ASideTaskWithNoIsolationWhereItIsRequired_IsRefused_AndEachTurnRecordsItsIsolation()
    {
        var unavailable = TestIsolation.Unavailable("bubblewrap is not here", "install it");
        var (refusedHarness, refusedTemp, refusedService, refusedStored, _) = await RepoAsync(isolation: unavailable);
        using var refusedTree = refusedTemp;

        var refused = await refusedService.RunAsync(new SideTaskCall(refusedTemp.Path, Prompt: "add a search source", Model: "glm-5.3", Name: "one"), Token);

        Assert.Equal(
            (HarnessExit.ToolMissing, "'run-side-task' is refused: agentPolicy.isolation is \"required\" and bubblewrap is not here, so no agent that may run a command starts here; install it. Nothing was started."),
            (refused.ExitCode, refused.Message));
        Assert.Empty(refusedStored.Runs);

        var (ground, launchDirectory) = Assert.Single(unavailable.Asked);

        Assert.Equal((true, LayoutOf(refusedTemp, "one").Directory), (ground.SideTask, launchDirectory));
        Assert.Contains(Path.Combine(refusedHarness.Platform.HomeDirectory, ".ssh"), ground.CredentialPlaces.Select(place => place.Path));

        var under = new DssHarness.Core.Agents.Isolation.LaunchIsolation("required", new PassThroughMechanism("bubblewrap", "0.9.0"), DssHarness.Core.Processes.IsolationProfile.Nothing, null, null);
        var (harness, temp, service, stored, _) = await RepoAsync(isolation: new TestIsolation((_, _) => under));
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "add a search source", Model: "glm-5.3", Name: "one"), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(under.Processes, Assert.Single(stored.Runs).Isolation);
        Assert.Equal(
            new DssHarness.Core.Agents.Isolation.AgentIsolationRecord("required") { Mechanism = "bubblewrap", Version = "0.9.0" },
            Assert.Single(RecordOf(harness, temp, LayoutOf(temp, "one").Directory)!.Turns).Isolation);
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
    /// whatever name it was started and whatever the mark holds, a mark cleared to nothing among them: nothing is begun
    /// and nothing written.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("")]
    public async Task ASideTaskStartedUnderAnAgent_RefusesItself(string marked)
    {
        var (_, temp, service, stored, _) = await RepoAsync(environment: name => name == AgentChildEnvironment.AgentMarker ? marked : null);
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "start another", Model: "glm-5.3", Name: "nested"), Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal($"'run-side-task' is refused: {SideTaskService.NoAgentStartsOne}. Nothing was started.", outcome.Message);
        Assert.Equal([SideTaskService.NoRecords], outcome.Details);
        Assert.Empty(stored.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "nested").Directory));
    }

    /// <summary>
    /// A side-task whose mark was taken away is still told by the process tree: started, through an agent and its shell,
    /// by a process of this tool's, it refuses itself in the mark's own words; where the tree could not be read it is
    /// refused saying why, never read as one with no agent in it. Nothing is begun and nothing written.
    /// </summary>
    [Fact]
    public async Task ASideTaskStartedUnderAnAgent_WithItsMarkTakenAway_IsToldByTheProcessTree()
    {
        ProcessTableReading UnderTheEngine(int current)
            => new(
                [
                    new SampledProcess(10, null, "dssharness", null, "dssharness run-orchestrator implementation", null),
                    new SampledProcess(20, 10, "claude", null, "claude --print", "/usr/bin/node"),
                    new SampledProcess(30, 20, "bash", null, "bash -c 'printf ... | sed e'", "/usr/bin/bash"),
                    new SampledProcess(current, 30, "dssharness", null, "dssharness run-side-task --prompt x", null),
                ],
                null);

        var (_, temp, service, stored, _) = await RepoAsync(tree: UnderTheEngine);
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "start another", Model: "glm-5.3", Name: "nested"), Token);

        Assert.Equal((HarnessExit.Refused, $"'run-side-task' is refused: {SideTaskService.NoAgentStartsOne}. Nothing was started."), (outcome.ExitCode, outcome.Message));
        Assert.Equal([SideTaskService.NoRecords], outcome.Details);

        var (_, unreadTemp, unread, _, _) = await RepoAsync(tree: current => new ProcessTableReading([new SampledProcess(current, null, "dssharness", null, null)], "the process table came back empty, which no running machine's is"));
        using var unreadTree = unreadTemp;

        var refused = await unread.RunAsync(new SideTaskCall(unreadTemp.Path, Prompt: "start another", Model: "glm-5.3", Name: "nested"), Token);

        Assert.Equal(
            (HarnessExit.Refused, $"'run-side-task' is refused: which processes started this one could not be read - the process table came back empty, which no running machine's is - so whether an agent started it could not be told, and {SideTaskService.NoAgentStartsOne}. Nothing was started."),
            (refused.ExitCode, refused.Message));
        Assert.Empty(stored.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "nested").Directory));
        Assert.False(Directory.Exists(LayoutOf(unreadTemp, "nested").Directory));
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
    /// line it was given and never answered - written to it, said as written, and taken by the turn that outgrew the
    /// context, as the real client does - begins a new launch, going on from the conversation compacted, and the person
    /// is told, and told the conversation was compacted as the new launch begins it - as its first turn ends, where a
    /// client that renders its conversations can no longer end never started. What is unanswered is the client's own
    /// count of what its turns answered, never what was written.
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
        Assert.Equal(["first", "second"], rendered.Runs.Select(run => run.UserPrompt));
        Assert.Null(rendered.Runs[0].Continuation);
        Assert.Equal(
            string.Join(
                "\n",
                $"{AgentCompaction.SaysWhatItIs}1. Everything up to iteration 0 is reduced to what you wrote and what you called; the last 1 iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                "first",
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                "iteration 1 of 1:",
                "about: first",
                string.Empty,
                "The task again:",
                string.Empty,
                "first"),
            rendered.Runs[1].Continuation!.ReplaceLineEndings("\n"));
        Assert.Equal(
            [
                "about: first",
                "the agent's launch ended ContextExhausted: the context is full; the next message begins a new launch, going on from the conversation so far",
                "about: second",
                $"compacted: {AgentCompaction.OutgrewItsContext}; this turn begins a new conversation from what it holds",
            ],
            typed.Said);

        // The line the ended launch never answered is the new launch's own, read and written once.
        var transcript = await File.ReadAllLinesAsync(Path.Combine(LayoutOf(temp, "relaunched").Directory, "transcript.md"), Token);
        Assert.Equal(["    first", "    second"], transcript.Where((line, at) => at > 0 && transcript[at - 1] == "person:"));
    }

    /// <summary>
    /// A launch gone between one turn's end and taking the message written to it meanwhile - by the client's own count of
    /// the messages it took - ended between turns: the turn before it ended well and keeps its own ending, never the
    /// launch's failure, and the line it never took goes on to the next launch, written to the transcript once.
    /// </summary>
    [Fact]
    public async Task ALaunchGoneBetweenTurns_LeavesTheTurnBeforeItAsItEnded_TheLineItNeverTookGoesOn()
    {
        var (harness, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        rendered.GoneBeforeTurn = 2;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rendered.WhileTurning = (_, _) => posted.Task.WaitAsync(TimeSpan.FromMinutes(1));
        var lines = new Queue<string>(["first", "second"]);
        var said = new List<string>();
        var chat = new SideTaskChat(
            async token =>
            {
                if (lines.TryDequeue(out var line))
                {
                    return line;
                }

                // The second line is written while the first turn runs; the person waits for its answer, then leaves.
                posted.TrySetResult();
                await heard.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                return null;
            },
            line =>
            {
                lock (said)
                {
                    said.Add(line);
                }

                if (line == "about: second")
                {
                    heard.TrySetResult();
                }
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "between", Chat: chat), Token);

        Assert.Equal((HarnessExit.Success, "side-task 'between' ended, 2 turn(s) in all"), (outcome.ExitCode, outcome.Message));
        Assert.Equal(["first", "second"], rendered.Runs.Select(run => run.UserPrompt));
        Assert.Equal(
            [
                "about: first",
                "the agent's launch ended Failed: the program is gone; the next message begins a new launch, going on from the conversation so far",
                "about: second",
            ],
            said);

        var record = RecordOf(harness, temp, LayoutOf(temp, "between").Directory)!;
        Assert.Equal([(AgentRunStatus?)null, AgentRunStatus.Completed], record.Turns.Select(turn => turn.Status));

        var transcript = await File.ReadAllLinesAsync(Path.Combine(LayoutOf(temp, "between").Directory, "transcript.md"), Token);
        Assert.Equal(["    first", "    second"], transcript.Where((line, at) => at > 0 && transcript[at - 1] == "person:"));
    }

    /// <summary>
    /// The first interrupt of a chat whose launch is held open is said by whether a turn of the agent's is under way - the
    /// launch's own turns telling it: in a turn, the turn is cut; between turns, the launch idle, nothing is, and it says
    /// so. Either way it is the one line the command says of it.
    /// </summary>
    [Theory]
    [InlineData(true, SideTaskTerminal.ChatCut)]
    [InlineData(false, SideTaskTerminal.ChatIdle)]
    public async Task TheFirstInterrupt_OfALaunchHeldOpen_IsSaidByWhetherATurnIsUnderWay(bool inATurn, string line)
    {
        var (harness, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        var terminal = new SideTaskTerminal(harness.Output, chat: true);
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new Queue<string>(["first"]);
        var directory = LayoutOf(temp, "pressed").Directory;

        // In a turn: pressed as the turn's text comes, the turn held until it was.
        rendered.WhileTurning = (_, _) => inATurn ? pressed.Task.WaitAsync(TimeSpan.FromMinutes(1)) : Task.CompletedTask;

        var chat = new SideTaskChat(
            async token =>
            {
                if (lines.TryDequeue(out var typed))
                {
                    return typed;
                }

                // The turn recorded - so ended - the launch is idle between turns: pressed there, and the person leaves.
                var until = DateTimeOffset.UtcNow.AddMinutes(1);

                while (Recorded() < 1 && DateTimeOffset.UtcNow < until)
                {
                    await Task.Delay(20, token);
                }

                if (!inATurn)
                {
                    terminal.Interrupted();
                }

                return null;
            },
            said =>
            {
                if (inATurn && said == "about: first")
                {
                    terminal.Interrupted();
                    pressed.TrySetResult();
                }
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "pressed", Chat: chat) { Interrupts = terminal.Interrupts }, Token);

        Assert.Equal((HarnessExit.Success, "side-task 'pressed' ended, 1 turn(s) in all"), (outcome.ExitCode, outcome.Message));
        Assert.Equal(1, terminal.Interrupts.Presses);
        Assert.Equal(
            [$"{SideTaskService.RunCommand}: WARN - {line}"],
            harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        int Recorded()
        {
            try
            {
                return RecordOf(harness, temp, directory)?.Turns.Count ?? 0;
            }
            catch (IOException)
            {
                // Read while it is written: read again.
                return 0;
            }
        }
    }

    /// <summary>
    /// Whether a turn of the agent's is under way is told by its own turns - one begun and not ended - however many of the
    /// person's messages were written to the launch while another ran: a launch gone in the middle of its third turn,
    /// two messages written while its first ran, leaves its second turn - which ended well - as it ended, never with the
    /// launch's failure, and the third turn's line goes on to the next launch, written to the transcript once.
    /// </summary>
    [Fact]
    public async Task ALaunchGoneInsideATurn_LeavesTheTurnsThatEndedAsTheyEnded_HoweverManyMessagesWaited()
    {
        var (harness, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        rendered.FailsOnTurn = 3;
        rendered.FailsWithoutAResult = true;
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rendered.WhileTurning = (_, _) => posted.Task.WaitAsync(TimeSpan.FromMinutes(1));
        var lines = new Queue<string>(["first", "second", "third"]);
        var said = new List<string>();
        var chat = new SideTaskChat(
            async token =>
            {
                if (lines.TryDequeue(out var line))
                {
                    return line;
                }

                // Every line is written to the launch while its first turn runs; the person waits for the last one's
                // answer, then leaves.
                posted.TrySetResult();
                await heard.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                return null;
            },
            line =>
            {
                lock (said)
                {
                    said.Add(line);
                }

                if (line == "about: third")
                {
                    heard.TrySetResult();
                }
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "waited", Chat: chat), Token);

        Assert.Equal((HarnessExit.Success, "side-task 'waited' ended, 3 turn(s) in all"), (outcome.ExitCode, outcome.Message));
        Assert.Equal(["first", "third"], rendered.Runs.Select(run => run.UserPrompt));
        Assert.Equal(
            [
                "about: first",
                "about: second",
                "the agent's launch ended Failed: the API failed; the next message begins a new launch, going on from the conversation so far",
                "about: third",
            ],
            said);

        var record = RecordOf(harness, temp, LayoutOf(temp, "waited").Directory)!;
        Assert.Equal([(AgentRunStatus?)null, null, AgentRunStatus.Completed], record.Turns.Select(turn => turn.Status));

        var transcript = await File.ReadAllLinesAsync(Path.Combine(LayoutOf(temp, "waited").Directory, "transcript.md"), Token);
        Assert.Equal(["    first", "    second", "    third"], transcript.Where((line, at) => at > 0 && transcript[at - 1] == "person:"));
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

        // Each launch's turn that outgrew the context is an iteration of the conversation too: the second launch's answer
        // is the third, never its launch's first. Each line a launch never answered is read once, as the next launch's
        // own: "second" once, and "third" not yet, for it is the third launch's task.
        Assert.Equal(
            string.Join(
                "\n",
                $"{AgentCompaction.SaysWhatItIs}3. Everything up to iteration 0 is reduced to what you wrote and what you called; the last 2 iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                "first",
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                "iteration 1 of 3:",
                "about: first",
                string.Empty,
                "the engine:",
                "second",
                string.Empty,
                "iteration 3 of 3:",
                "about: second",
                string.Empty,
                "The task again:",
                string.Empty,
                "first"),
            rendered.Runs[2].Continuation!.ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// A launch held open for a chat is never tried again by the runner - a try would begin again from its first message,
    /// redoing it and losing every line it had taken: a turn that fails ends the launch, and the line it never answered
    /// begins a new launch going on from the conversation so far, the person told. The launch's ending is its failed
    /// turn's where that turn ended with a result; where it ended with none, no turn recorded takes it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedTurnOfALaunchHeldOpen_IsNeverTriedAgain_ANewLaunchGoesOnFromTheConversation(bool withoutAResult)
    {
        var (harness, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        rendered.FailsOnTurn = 2;
        rendered.FailsWithoutAResult = withoutAResult;
        var typed = new Typed(["first", "second"], until: "about: second");

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "unretried", Chat: typed.Chat()), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(["first", "second"], rendered.Runs.Select(run => run.UserPrompt));
        Assert.Equal(
            Carried(1, "first", ["iteration 1 of 1:", "about: first"]),
            rendered.Runs[1].Continuation!.ReplaceLineEndings("\n"));
        Assert.Equal(
            [
                "about: first",
                "the agent's launch ended Failed: the API failed; the next message begins a new launch, going on from the conversation so far",
                "about: second",
            ],
            typed.Said);
        AgentRunStatus?[] endings = withoutAResult ? [null, AgentRunStatus.Completed] : [null, AgentRunStatus.Failed, AgentRunStatus.Completed];
        Assert.Equal(endings, RecordOf(harness, temp, LayoutOf(temp, "unretried").Directory)!.Turns.Select(turn => turn.Status));
    }

    /// <summary>
    /// A model fallback inside a chat's launch is the chat's to make, never the runner's: the walk ends with the launch's
    /// ending and hands back the entry it would move to, and the chat goes on there from the conversation so far with the
    /// line no turn answered - a new launch where that entry's client holds one open, a request of its own where it
    /// stores its conversations - never answering the chat's first message again. The move is said, and each line is
    /// read and written once.
    /// </summary>
    [Theory]
    [InlineData("claudes", "claude-opus-5-5")]
    [InlineData("balanced", "glm-5.3")]
    public async Task AFallbackInAChat_GoesOnFromTheConversation_NeverFromTheLaunchsFirstMessage(string tier, string movedTo)
    {
        var (harness, temp, service, stored, rendered) = await RepoAsync();
        using var tree = temp;
        rendered.FailsOnTurn = 2;
        var rendersIt = movedTo != "glm-5.3";
        var typed = new Typed(["first", "second"], until: rendersIt ? "about: second" : "the answer");

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Tier: tier, Name: "moved", Chat: typed.Chat()), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            [
                "about: first",
                $"claude-sonnet-5-5 -> {movedTo} (failed, with no retries)",
                "the agent's launch ended Failed: the API failed; the next message begins a new launch, going on from the conversation so far",
                rendersIt ? "about: second" : "the answer",
            ],
            typed.Said);

        // The launch that failed was the walk's only one: what goes on is the chat's, from "second", the conversation so far
        // carried before it.
        IReadOnlyList<AgentRun> after = rendersIt ? [rendered.Runs[1]] : [Assert.Single(stored.Runs)];
        Assert.Equal(rendersIt ? 2 : 1, rendered.Runs.Count);
        Assert.Equal((movedTo, "second", false), (after[0].Model, after[0].UserPrompt, after[0].Continuation is null));
        Assert.Equal(rendersIt, after[0].Chat);

        var transcript = await File.ReadAllLinesAsync(Path.Combine(LayoutOf(temp, "moved").Directory, "transcript.md"), Token);
        Assert.Equal(["    first", "    second"], transcript.Where((line, at) => at > 0 && transcript[at - 1] == "person:"));

        var record = RecordOf(harness, temp, LayoutOf(temp, "moved").Directory)!;
        Assert.Equal(
            [("claude-sonnet-5-5", (AgentRunStatus?)null), ("claude-sonnet-5-5", AgentRunStatus.Failed), (movedTo, AgentRunStatus.Completed)],
            record.Turns.Select(turn => (turn.Model, turn.Status)));
    }

    /// <summary>
    /// The person leaving a chat whose next launch waits its turn at the gate - the limit its launch met not lifted yet -
    /// ends it there: the launch is never begun, so nothing more is spent after the person left, and the chat ends at once.
    /// The wait is said as it begins, so the chat is never silent while it waits.
    /// </summary>
    [Fact]
    public async Task ThePersonLeavingWhileALaunchWaitsAtTheGate_BeginsNoLaunch()
    {
        // The wait is said from the time the limit lifts at and the gate's own clock, neither moving while the run goes on,
        // so a busy machine says the same wait; the person's leaving ends it.
        var wall = new FrozenWall();
        var gates = new ClientGates(new ManualClock(), (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        var (_, temp, service, _, rendered) = await RepoAsync(gates: gates, runnerClock: wall);
        using var tree = temp;
        rendered.EndsWith = (launch, turn) => launch == 1 && turn == 1
            ? new AgentRunResult(AgentRunStatus.RateLimited, Model: "claude-sonnet-5-5", Error: "limit") { Limit = new AgentAccountLimit(false, wall.GetUtcNow().AddSeconds(25), null, "claude-sonnet-5-5") }
            : null;
        var interrupts = new SideTaskInterrupts();
        var lines = new Queue<string>(["first"]);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var said = new List<string>();
        var chat = new SideTaskChat(
            async token =>
            {
                if (lines.TryDequeue(out var line))
                {
                    return line;
                }

                // The next launch waits at the gate: the person presses Ctrl+C twice.
                await waiting.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                interrupts.Pressed();
                interrupts.Pressed();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            },
            line =>
            {
                lock (said)
                {
                    said.Add(line);
                }

                if (line.StartsWith("the run waits for ", StringComparison.Ordinal))
                {
                    waiting.TrySetResult();
                }
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "gate", Chat: chat) { Interrupts = interrupts }, Token);

        Assert.Equal((HarnessExit.Success, "side-task 'gate' ended, 0 turn(s) in all"), (outcome.ExitCode, outcome.Message));
        Assert.Single(rendered.Runs);
        Assert.Equal(
            [
                "the agent's launch ended RateLimited: limit; the next message begins a new launch, going on from the conversation so far",
                $"the run waits for {ClientGate.CoolDown}: waiting 25 s",
            ],
            said);
    }

    /// <summary>
    /// A chat's first interrupt while its next launch waits its turn at the gate cuts nothing - no turn of the agent's is
    /// under way - and says so: the launch is begun once the wait passes, never handed the interrupt the person spent
    /// there, so the line it carries is answered rather than dropped as it begins.
    /// </summary>
    [Fact]
    public async Task AChatsFirstInterruptWhileItsLaunchWaitsAtTheGate_CutsNothing_AndTheLaunchBeginsAfterTheWait()
    {
        // The wait is said from the time the limit lifts at and the gate's own clock, neither moving while the run goes on,
        // so a busy machine says the same wait; it passes once the person's interrupt is in, the gate's clock moved past it.
        var wall = new FrozenWall();
        var pressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new ManualClock();
        var gates = new ClientGates(
            lane,
            async (wait, token) =>
            {
                await pressed.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                lane.Advance(wait);
            });
        var (harness, temp, service, _, rendered) = await RepoAsync(gates: gates, runnerClock: wall);
        using var tree = temp;
        rendered.EndsWith = (launch, turn) => launch == 1 && turn == 1
            ? new AgentRunResult(AgentRunStatus.RateLimited, Model: "claude-sonnet-5-5", Error: "limit") { Limit = new AgentAccountLimit(false, wall.GetUtcNow().AddSeconds(2), null, "claude-sonnet-5-5") }
            : null;
        var terminal = new SideTaskTerminal(harness.Output, chat: true);
        var lines = new Queue<string>(["first"]);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var said = new List<string>();
        var chat = new SideTaskChat(
            async token =>
            {
                if (lines.TryDequeue(out var line))
                {
                    return line;
                }

                // The next launch waits at the gate: the person presses Ctrl+C once, and leaves once the line is answered.
                await waiting.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                terminal.Interrupted();
                pressed.TrySetResult();
                await answered.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                return null;
            },
            line =>
            {
                lock (said)
                {
                    said.Add(line);
                }

                (line.StartsWith("the run waits for ", StringComparison.Ordinal) ? waiting : line == "about: first" ? answered : null)?.TrySetResult();
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "gate-press", Chat: chat) { Interrupts = terminal.Interrupts }, Token);

        Assert.Equal((HarnessExit.Success, "side-task 'gate-press' ended, 1 turn(s) in all"), (outcome.ExitCode, outcome.Message));
        Assert.Equal(["first", "first"], rendered.Runs.Select(run => run.UserPrompt));
        Assert.Equal(
            [
                "the agent's launch ended RateLimited: limit; the next message begins a new launch, going on from the conversation so far",
                $"the run waits for {ClientGate.CoolDown}: waiting 2 s",
                "about: first",
            ],
            said);
        Assert.Equal(
            [$"{SideTaskService.RunCommand}: WARN - {SideTaskTerminal.ChatIdle}"],
            harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// A one shot's interrupt while its launch waits its turn at the gate - a cool-down within the longest wait - stops it
    /// there: no launch is begun, nothing is spent, and it ends interrupted with its records named.
    /// </summary>
    [Fact]
    public async Task AOneShotsInterruptWhileItsLaunchWaitsAtTheGate_BeginsNoLaunch()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gates = new ClientGates(
            TimeProvider.System,
            (wait, token) =>
            {
                waiting.TrySetResult();
                return Task.Delay(wait, TimeProvider.System, token);
            });
        var (harness, temp, service, _, rendered) = await RepoAsync(gates: gates);
        using var tree = temp;

        // The lane is held by a cool-down a 429 set, for the launch to wait out.
        gates.LaneOf("anthropic", "claude-code", null, null).RateLimited(TimeSpan.FromSeconds(25));
        var interrupts = new SideTaskInterrupts();
        _ = waiting.Task.ContinueWith(_ => interrupts.Pressed(), TaskScheduler.Default);

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "slow", Model: "claude-sonnet-5-5", Name: "gate-once") { Interrupts = interrupts }, Token).WaitAsync(TimeSpan.FromSeconds(20), Token);

        var directory = LayoutOf(temp, "gate-once").Directory;
        Assert.Equal(
            (HarnessExit.Cancelled, $"side-task 'gate-once' ended Cancelled: {AgentRunner.CutBeforeItBegan}"),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal([$"Records: {directory}"], outcome.Details);
        Assert.Empty(rendered.Runs);
        Assert.Equal([(AgentRunStatus?)AgentRunStatus.Cancelled], RecordOf(harness, temp, directory)!.Turns.Select(turn => turn.Status));
        Assert.Empty(new AgentKeySpend(harness.FileSystem, harness.OrchestrationStore, TimeProvider.System).Spent(new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path)));
    }

    /// <summary>
    /// A one shot interrupted as its launch fails is never tried again: the retry's wait ends at the interrupt, no launch
    /// is begun after it, and it ends interrupted - never waiting out the retry's backoff first.
    /// </summary>
    [Fact]
    public async Task AOneShotInterruptedAsItsLaunchFails_IsNeverTriedAgain()
    {
        var (_, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        var interrupts = new SideTaskInterrupts();
        rendered.EndsWith = (_, _) =>
        {
            interrupts.Pressed();
            return new AgentRunResult(AgentRunStatus.Failed, Model: "claude-sonnet-5-5", Error: "the API failed");
        };

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "slow", Model: "claude-sonnet-5-5", Name: "fail-once") { Interrupts = interrupts }, Token).WaitAsync(TimeSpan.FromSeconds(20), Token);

        Assert.Equal(
            (HarnessExit.Cancelled, $"side-task 'fail-once' ended Cancelled: {AgentRunner.CutBeforeItBegan}"),
            (outcome.ExitCode, outcome.Message));
        Assert.Single(rendered.Runs);
    }

    /// <summary>
    /// <c>/model</c> typed while the open launch works - a line typed ahead of it - gives the launch way as the person
    /// leaving does: the turn under way ends at its result and is recorded on the model that served it, and the line it
    /// never took goes on to a new launch on the model the person chose, from the conversation so far - never dropped,
    /// never read twice.
    /// </summary>
    [Fact]
    public async Task AModelChosenWhileALaunchWorks_CarriesTheLinesItNeverTookToTheNewLaunch()
    {
        var (harness, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;

        // The first launch's turn runs until its messages end, as the person's /model ends them.
        rendered.WhileTurning = (run, _) => run.Model == "claude-sonnet-5-5" ? run.Messages.Ended.WaitAsync(TimeSpan.FromMinutes(1)) : Task.CompletedTask;
        var lines = new Queue<string>(["A", "B", "/model claude-opus-5-5"]);
        var heard = new Dictionary<string, TaskCompletionSource>
        {
            ["about: A"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            ["about: B"] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var said = new List<string>();
        var chat = new SideTaskChat(
            async token =>
            {
                // The first turn under way, the person types a line ahead of it and chooses another model; once the line
                // is answered, the person leaves.
                if (lines.Count < 3)
                {
                    await heard[lines.Count == 0 ? "about: B" : "about: A"].Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                }

                return lines.TryDequeue(out var line) ? line : null;
            },
            line =>
            {
                lock (said)
                {
                    said.Add(line);
                }

                heard.GetValueOrDefault(line)?.TrySetResult();
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "chosen", Chat: chat), Token);

        Assert.Equal((HarnessExit.Success, "side-task 'chosen' ended, 2 turn(s) in all"), (outcome.ExitCode, outcome.Message));
        Assert.Equal([("claude-sonnet-5-5", "A"), ("claude-opus-5-5", "B")], rendered.Runs.Select(run => (run.Model, run.UserPrompt)));
        Assert.Equal(Carried(1, "A", ["iteration 1 of 1:", "about: A"]), rendered.Runs[1].Continuation!.ReplaceLineEndings("\n"));
        Assert.Equal(
            ["about: A", "the next message goes to claude-opus-5-5 through claude-code, at its own default effort", "about: B"],
            said);

        var directory = LayoutOf(temp, "chosen").Directory;
        Assert.Equal(
            [("claude-sonnet-5-5", (AgentRunStatus?)AgentRunStatus.Completed), ("claude-opus-5-5", AgentRunStatus.Completed)],
            RecordOf(harness, temp, directory)!.Turns.Select(turn => (turn.Model, turn.Status)));

        var transcript = await File.ReadAllLinesAsync(Path.Combine(directory, "transcript.md"), Token);
        Assert.Equal(["    A", "    B"], transcript.Where((line, at) => at > 0 && transcript[at - 1] == "person:"));
    }

    /// <summary>
    /// A chat a move brought to an entry that cannot be started is handed the entry after it, as a step's walk passes one
    /// over - here a model an API client reaches, which answers the line - and goes on there: its next message is never
    /// sent to the entry that cannot start again.
    /// </summary>
    [Fact]
    public async Task AChatMovedToAnEntryThatCannotStart_GoesOnToTheEntryAfterIt()
    {
        var (_, temp, service, stored, rendered) = await RepoAsync();
        using var tree = temp;
        rendered.FailsOnTurn = 1;
        rendered.NotStartedOn = "claude-opus-5-5";
        var lines = new Queue<string>(["first", "second"]);
        var answered = 0;
        var said = new List<string>();
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var once = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = new SideTaskChat(
            async token =>
            {
                if (lines.TryDequeue(out var line))
                {
                    if (line == "second")
                    {
                        await once.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                    }

                    return line;
                }

                await both.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                return null;
            },
            line =>
            {
                lock (said)
                {
                    said.Add(line);
                }

                if (line == "the answer" && Interlocked.Increment(ref answered) is var count)
                {
                    (count == 1 ? once : both).TrySetResult();
                }
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Tier: "trio", Name: "past", Chat: chat), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(["claude-sonnet-5-5", "claude-opus-5-5"], rendered.Runs.Select(run => run.Model));
        Assert.Equal(["first", "second"], stored.Runs.Select(run => run.UserPrompt));
        Assert.Equal(
            [
                "claude-sonnet-5-5 -> claude-opus-5-5 (failed, with no retries)",
                "the agent's launch ended Failed: the API failed; the next message begins a new launch, going on from the conversation so far",
                "claude-sonnet-5-5 -> glm-5.3 (failed, with no retries)",
                "the agent's launch ended NotStarted: client 'claude-code' was not started: the model has no maxOutputTokens; the next message begins a new launch, going on from the conversation so far",
                "the answer",
                "the answer",
            ],
            said);
    }

    /// <summary>
    /// A launch gone while idle between turns - nothing written to it waiting, the turn before it answered - ended at no
    /// turn's end: the answered turn keeps its own ending, never the launch's failure.
    /// </summary>
    [Fact]
    public async Task ALaunchGoneWhileIdle_LeavesTheTurnItAnsweredAsItEnded()
    {
        var (harness, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        rendered.GoneAfter = (launch, turn) => launch == 1 && turn == 1;
        const string gone = "the agent's launch ended Failed: the program is gone; the next message begins a new launch, going on from the conversation so far";
        var lines = new Queue<string>(["first", "second"]);
        var told = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var said = new List<string>();
        var chat = new SideTaskChat(
            async token =>
            {
                if (lines.TryDequeue(out var line))
                {
                    if (line == "second")
                    {
                        await told.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                    }

                    return line;
                }

                await heard.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                return null;
            },
            line =>
            {
                lock (said)
                {
                    said.Add(line);
                }

                (line == gone ? told : line == "about: second" ? heard : null)?.TrySetResult();
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "idle", Chat: chat), Token);

        Assert.Equal((HarnessExit.Success, "side-task 'idle' ended, 2 turn(s) in all"), (outcome.ExitCode, outcome.Message));
        Assert.Equal(["about: first", gone, "about: second"], said);
        Assert.Equal(
            [(AgentRunStatus?)null, AgentRunStatus.Completed],
            RecordOf(harness, temp, LayoutOf(temp, "idle").Directory)!.Turns.Select(turn => turn.Status));
    }

    /// <summary>
    /// A one shot's interrupt cuts its launch's turn, never kills the launch: the side-task ends at the cut turn's result,
    /// interrupted (130), with what it used and cost its own and what its key spent recorded for the balancing of keys.
    /// </summary>
    [Fact]
    public async Task AOneShotsInterrupt_CutsItsLaunchsTurn_ItsUseCostAndSpendKept()
    {
        var (harness, temp, service, _, rendered) = await RepoAsync(clients: new(StringComparer.OrdinalIgnoreCase) { ["claude-code"] = new() { Auth = AgentAuth.ApiKey } });
        using var tree = temp;
        rendered.LaunchCost = 0.25m;
        var interrupts = new SideTaskInterrupts();
        rendered.WhileTurning = (_, _) =>
        {
            interrupts.Pressed();
            return Task.CompletedTask;
        };

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "slow", Model: "claude-sonnet-5-5", Name: "cut-launch") { Interrupts = interrupts }, Token);

        Assert.Equal(
            (HarnessExit.Cancelled, $"side-task 'cut-launch' ended Cancelled: {ClaudeCodeClient.TurnCut}"),
            (outcome.ExitCode, outcome.Message));
        var record = RecordOf(harness, temp, LayoutOf(temp, "cut-launch").Directory)!;
        Assert.Equal((new AgentUsage(7, 3, 0, 0, 0, 0), (decimal?)0.25m), (record.InAll!.Total, record.InAll.Cost!.Usd));
        Assert.Equal([(AgentRunStatus?)AgentRunStatus.Cancelled], record.Turns.Select(turn => turn.Status));
        Assert.Equal(
            [new KeyValuePair<(string Provider, string Key), decimal>(("anthropic", "ANTHROPIC_API_KEY"), 0.25m)],
            new AgentKeySpend(harness.FileSystem, harness.OrchestrationStore, TimeProvider.System).Spent(new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path)));
    }

    /// <summary>
    /// The person leaving a chat by the second interrupt ends its open launch as <c>/exit</c> does - its turn cut, its
    /// messages ended - never by stopping it hard: what the launch used and cost is the side-task's, what its key spent is
    /// recorded for the balancing of keys, and its last turn has the launch's ending.
    /// </summary>
    [Fact]
    public async Task TheSecondInterrupt_EndsTheOpenLaunchAsExitDoes_ItsUseCostAndSpendKept()
    {
        // Claude Code signed in with the slot's key, whose spend is the one a balancing of keys reads.
        var (harness, temp, service, _, rendered) = await RepoAsync(clients: new(StringComparer.OrdinalIgnoreCase) { ["claude-code"] = new() { Auth = AgentAuth.ApiKey } });
        using var tree = temp;
        rendered.LaunchCost = 0.25m;
        var interrupts = new SideTaskInterrupts();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = new Queue<string>(["first"]);
        var chat = new SideTaskChat(
            async token =>
            {
                if (lines.TryDequeue(out var line))
                {
                    return line;
                }

                // The answer in, the person presses Ctrl+C twice and types nothing more.
                await answered.Task.WaitAsync(TimeSpan.FromMinutes(1), token);
                interrupts.Pressed();
                interrupts.Pressed();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            },
            line =>
            {
                if (line == "about: first")
                {
                    answered.TrySetResult();
                }
            });

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "left", Chat: chat) { Interrupts = interrupts }, Token);

        Assert.Equal((HarnessExit.Success, "side-task 'left' ended, 1 turn(s) in all"), (outcome.ExitCode, outcome.Message));

        var record = RecordOf(harness, temp, LayoutOf(temp, "left").Directory)!;
        Assert.Equal((new AgentUsage(7, 3, 0, 0, 0, 0), (decimal?)0.25m), (record.InAll!.Total, record.InAll.Cost!.Usd));
        Assert.Equal([(AgentRunStatus?)AgentRunStatus.Completed], record.Turns.Select(turn => turn.Status));
        Assert.Equal(
            [new KeyValuePair<(string Provider, string Key), decimal>(("anthropic", "ANTHROPIC_API_KEY"), 0.25m)],
            new AgentKeySpend(harness.FileSystem, harness.OrchestrationStore, TimeProvider.System).Spent(new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path)));
    }

    /// <summary>
    /// Making Claude Code ready can end on a change npm did not finish - not within its bound, or the registry not
    /// reached - which is the code every program's update begun and not finished ends on (21), passed through as the
    /// finder answers it with its own words: never read as a model cut short, never as a program missing.
    /// </summary>
    [Fact]
    public async Task AnInstallOfClaudeCodeNotFinished_RefusesTheSideTask_Incomplete()
    {
        using var kit = new ClaudeCodeUpdateServiceTests.Kit(package: "2.1.201", program: "2.1.201");
        kit.Runner.Install = new ProcessResult(-1, "running postinstall\n", string.Empty, ToolPackage.ChangeBudget, TimedOut: true);
        var finder = await kit.Service().ReadyAsync(kit.Layout, [], client: null, DssHarness.Core.Agents.ClaudeCode.ClaudeCodeAsker.SideTask("unfinished"), Token);
        var (_, temp, service, _, rendered) = await RepoAsync(updates: kit.Service());
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "claude-sonnet-5-5", Name: "unfinished"), Token);

        Assert.Equal((HarnessExit.Incomplete, finder.Message), (outcome.ExitCode, outcome.Message));
        Assert.Equal([.. finder.Details, SideTaskService.NoRecords], outcome.Details);
        Assert.Empty(rendered.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "unfinished").Directory));
    }

    /// <summary>
    /// Each turn of a launch held open reaches the side-task's record as it ends - one record each, never two merged
    /// into one, and never only when the launch ends: the first turn is in <c>task.json</c> before the second is
    /// answered, and the launch's ending is its last turn's.
    /// </summary>
    [Fact]
    public async Task EachTurnOfALaunchHeldOpen_ReachesTheRecordAsItEnds()
    {
        var (harness, temp, service, _, rendered) = await RepoAsync();
        using var tree = temp;
        var directory = LayoutOf(temp, "turned").Directory;
        var recordedBeforeTheSecond = -1;

        rendered.BeforeAnswering = async turn =>
        {
            if (turn != 2)
            {
                return;
            }

            // The second turn answers once the first is in the record - or, where it never is, after a while.
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);

            while ((RecordOf(harness, temp, directory)?.Turns.Count ?? 0) == 0 && DateTime.UtcNow < until)
            {
                await Task.Delay(20, Token);
            }

            recordedBeforeTheSecond = RecordOf(harness, temp, directory)?.Turns.Count ?? 0;
        };

        var typed = new Typed(["first", "second"], until: "about: second");

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "turned", Chat: typed.Chat()), Token);

        Assert.Equal((HarnessExit.Success, "side-task 'turned' ended, 2 turn(s) in all"), (outcome.ExitCode, outcome.Message));
        Assert.Equal(1, recordedBeforeTheSecond);

        var record = RecordOf(harness, temp, directory)!;
        var served = rendered.Runs[0].Model!;
        Assert.Equal(
            [((AgentRunStatus?)null, served, 1, 7), (AgentRunStatus.Completed, served, 1, 7)],
            record.Turns.Select(turn => (turn.Status, Assert.Single(turn.Served).Model, turn.Turns, turn.Usage!.Total.InputTokens)));
    }

    /// <summary>
    /// A new model named by <c>/model</c> starts from the efforts that name it - the entry for the rest kept - never from an
    /// entry for the model it replaces, which it does not run on; where no entry is left, at its own default effort.
    /// </summary>
    [Fact]
    public async Task ModelInAChat_KeepsOnlyTheEffortsThatNameTheNewModel()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var rest = new Typed(["/model narrow-1", "after the model"]);
        var none = new Typed(["/model narrow-1", "after the model"]);

        var keptRest = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "rest", Efforts: ["glm-5.3=low", "all=high"], Chat: rest.Chat()), Token);
        var keptNone = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "none", Efforts: ["glm-5.3=high"], Chat: none.Chat()), Token);

        Assert.Equal((HarnessExit.Success, HarnessExit.Success), (keptRest.ExitCode, keptNone.ExitCode));
        Assert.Equal(["the next message goes to narrow-1 through glm, at effort high", "the answer"], rest.Said);
        Assert.Equal(["the next message goes to narrow-1 through glm, at its own default effort", "the answer"], none.Said);
        Assert.Equal([("narrow-1", "high"), ("narrow-1", (string?)null)], stored.Runs.Select(run => (run.Model!, run.Effort)));

        var restRunsOn = RecordOf(harness, temp, LayoutOf(temp, "rest").Directory)!.RunsOn!;
        Assert.Equal(("narrow-1", (string?)null), (restRunsOn.Model, restRunsOn.Effort));
        Assert.Equal([new KeyValuePair<string, string>(SideTaskEffort.Every, "high")], restRunsOn.EffortByModel!);
        Assert.Equal(new SideTaskRunsOn(Model: "narrow-1"), RecordOf(harness, temp, LayoutOf(temp, "none").Directory)!.RunsOn);
    }

    /// <summary>
    /// What a side-task's records and its answer are held to is a step's mask, built by the one builder of it: every
    /// provider key the slot answers is masked - in the run's mask and in the stored stream - not only the web search's.
    /// </summary>
    [Fact]
    public async Task TheMaskASideTaskIsHeldTo_IsAStepsWithEveryProviderKey()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        const string Keys = "keys: sk-test-one sk-test-two sk-test-three";

        stored.Script = (run, events, _) => StoredClient.Answered(run, events, Keys);

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "masked"), Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal($"keys: {ActionValues.Mask} {ActionValues.Mask} {ActionValues.Mask}", Assert.Single(stored.Runs).Mask.Redact(Keys));
        Assert.DoesNotContain("sk-test-", File.ReadAllText(Path.Combine(LayoutOf(temp, "masked").Directory, StoredStream.FileName)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Claude Code a side-task starts is made ready as a step's is, and one not ready refuses it on the finder's own
    /// code and words: an update refused - a session of Claude Code running - is 13, never folded into a missing
    /// program's 14. Nothing is started or written.
    /// </summary>
    [Fact]
    public async Task AClaudeCodeNotReady_RefusesTheSideTask_OnTheFindersOwnCode()
    {
        using var kit = new ClaudeCodeUpdateServiceTests.Kit(package: "2.1.201", program: "2.1.201");
        kit.Table = new ClaudeCodeUpdateServiceTests.ScriptedTable([[new SampledProcess(4242, 1, "claude", null, "claude --resume", kit.Program)]]);
        var (_, temp, service, _, rendered) = await RepoAsync(updates: kit.Service());
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "claude-sonnet-5-5", Name: "held-back"), Token);

        Assert.Equal(
            (HarnessExit.Refused, "a session of Claude Code is running on this machine (pid 4242); start the side-task again when they have ended."),
            (outcome.ExitCode, outcome.Message));
        Assert.Equal([SideTaskService.NoRecords], outcome.Details);
        Assert.Empty(rendered.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "held-back").Directory));
    }

    /// <summary>
    /// A Claude Code that is not ready refuses a side-task only where the entry that would serve it first needs it: on a
    /// tier whose first model an API client reaches, with npm off the PATH, the side-task runs on that model - Claude
    /// Code, its fallback, never asked to start - as a step on the same tier does; on a tier whose first model Claude Code
    /// reaches, it is refused with the finder's own words and code.
    /// </summary>
    [Fact]
    public async Task AClaudeCodeNotReady_RefusesASideTask_OnlyWhereItsFirstEntryNeedsIt()
    {
        using var kit = new ClaudeCodeUpdateServiceTests.Kit(npm: false);
        var finder = await kit.Service().ReadyAsync(kit.Layout, [], client: null, DssHarness.Core.Agents.ClaudeCode.ClaudeCodeAsker.SideTask("claude-first"), Token);
        var (_, temp, service, stored, rendered) = await RepoAsync(updates: kit.Service());
        using var tree = temp;

        var served = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Tier: "api-first", Name: "api-first"), Token);

        Assert.Equal((HarnessExit.Success, "side-task 'api-first' answered on glm-5.3"), (served.ExitCode, served.Message));
        Assert.Equal(["glm-5.3"], stored.Runs.Select(run => run.Model!));
        Assert.Empty(rendered.Runs);

        var refused = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Tier: "claudes", Name: "claude-first"), Token);

        Assert.Equal((false, HarnessExit.ToolMissing, finder.Message), (finder.Ready, refused.ExitCode, refused.Message));
        Assert.Empty(rendered.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "claude-first").Directory));
    }

    /// <summary>
    /// While a run's owner is live over the main tree - the record the engine writes, read where the side-task asks -
    /// the side-task's agent is refused a command that writes the git state the run works over, the refusal naming the
    /// run and nothing committed; once the owner is released, the same command runs and commits.
    /// </summary>
    [Fact]
    public async Task ASideTasksCommit_IsRefusedWhileARunsOwnerIsLive_NamingTheRun_AndRunsOnceItIsReleased()
    {
        var endpoint = new ApiEndpoint();
        var opened = new List<IDisposable>();

        try
        {
            var (harness, temp, service, _, _) = await RepoAsync(apis: ApiClients(endpoint, opened));
            using var tree = temp;
            await harness.CommitAllAsync(temp.Path, "base", Token);
            temp.WriteFile("file.txt", "changed\n");

            var run = DssHarness.Core.Orchestration.OrchestratorLayout.Of(new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path), "r1");
            harness.OrchestrationStore.WriteOrchestrator(run, new DssHarness.Core.Orchestration.OrchestratorRecord { Kind = DssHarness.Core.Orchestration.OrchestratorRecord.KindName, Name = "r1", Model = "flow-engine", Parallel = 1, CreatedAt = DateTimeOffset.UnixEpoch });
            var owner = new FlowRunOwner(harness.OrchestrationStore, harness.Identity);
            _ = owner.Claim(run);

            async Task<string> SubjectAsync() => (await harness.RunGitAsync(temp.Path, ["log", "-1", "--format=%s"], Token)).StandardOutput.Trim();

            // The agent asks to commit, then answers once it is told what came of it.
            void Scripted()
                => endpoint
                    .Then(ApiReply.Stream(ClaudeApiFixtures.Events(
                    [
                        ClaudeApiFixtures.Start("claude-sonnet-5-5"),
                        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_000000000000000000000001","name":"run_command","input":{}}}"""),
                        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"program\":\"git\",\"args\":[\"commit\",\"-am\",\"x\"]}"}}"""),
                        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                        .. ClaudeApiFixtures.End("tool_use"),
                    ])))
                    .Then(ApiReply.Stream(ClaudeApiFixtures.Events(
                    [
                        ClaudeApiFixtures.Start("claude-sonnet-5-5"),
                        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
                        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"told"}}"""),
                        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
                        .. ClaudeApiFixtures.End("end_turn"),
                    ])));

            Scripted();
            var held = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "commit it", Model: "claude-sonnet-5-5", Client: ClaudeApiClient.ClientName, Name: "held"), Token);

            Assert.Equal(HarnessExit.Success, held.ExitCode);
            var denied = Assert.Single(harness.OrchestrationStore.ReadStepStream(LayoutOf(temp, "held").Directory).OfType<PermissionDenied>());
            Assert.Equal(
                (AgentTools.RunCommand, DssHarness.Core.Agents.Policy.AgentPolicy.LiveRunRule, "run 'r1' is working over this tree, and 'git commit -am x' writes the git state it works over - its HEAD, index, refs, stash, configuration or worktrees; ask again when it ends, or pause it yourself"),
                (denied.Tool, denied.Rule, denied.Reason));
            Assert.Equal("base", await SubjectAsync());

            owner.Release(run);
            Scripted();
            var free = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "commit it", Model: "claude-sonnet-5-5", Client: ClaudeApiClient.ClientName, Name: "free"), Token);

            Assert.Equal(HarnessExit.Success, free.ExitCode);
            Assert.Empty(harness.OrchestrationStore.ReadStepStream(LayoutOf(temp, "free").Directory).OfType<PermissionDenied>());
            Assert.Equal("x", await SubjectAsync());
        }
        finally
        {
            opened.ForEach(each => each.Dispose());
        }
    }

    /// <summary>
    /// An interrupt while the side-task is being set up - here while its Claude Code is made ready, the machine's
    /// processes being read - stops it where it stands: a one shot's first, a chat's second. Nothing is started or
    /// written, and the command ends interrupted (130).
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task AnInterruptWhileTheSideTaskIsSetUp_StopsIt(bool chat, int presses)
    {
        var interrupts = new SideTaskInterrupts();
        var table = new PressingTable(interrupts, presses);
        using var kit = new ClaudeCodeUpdateServiceTests.Kit(package: "2.1.201", program: "2.1.201");
        var (_, temp, service, _, rendered) = await RepoAsync(updates: kit.Service(table: table));
        using var tree = temp;

        var call = chat
            ? new SideTaskCall(temp.Path, Model: "claude-sonnet-5-5", Name: "stopped", Chat: new Typed(["never read"]).Chat())
            : new SideTaskCall(temp.Path, Prompt: "x", Model: "claude-sonnet-5-5", Name: "stopped");

        var outcome = await service.RunAsync(call with { Interrupts = interrupts }, Token);

        Assert.Equal((HarnessExit.Cancelled, "'run-side-task' was stopped before the side-task began. Nothing was started."), (outcome.ExitCode, outcome.Message));
        Assert.Equal([SideTaskService.NoRecords], outcome.Details);
        Assert.True(table.Stopped, "the wait it stood in was stopped");
        Assert.Empty(rendered.Runs);
        Assert.False(Directory.Exists(LayoutOf(temp, "stopped").Directory));
    }

    /// <summary>
    /// An interrupt the terminal took before the side-task's stop was bound to its interrupts is applied once it is: the
    /// one shot stops before it begins, never left to run as though the press had not come.
    /// </summary>
    [Fact]
    public async Task AnInterruptTakenBeforeTheStopIsBound_IsAppliedOnceItIs()
    {
        var interrupts = new SideTaskInterrupts();
        var (_, temp, service, stored, _) = await RepoAsync(environment: name =>
        {
            // Read as the command begins, before anything else: the person presses Ctrl+C right then.
            if (name == AgentChildEnvironment.AgentMarker)
            {
                interrupts.Pressed();
            }

            return null;
        });
        using var tree = temp;

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "x", Model: "glm-5.3", Name: "early") { Interrupts = interrupts }, Token);

        Assert.Equal((HarnessExit.Cancelled, "'run-side-task' was stopped before the side-task began. Nothing was started."), (outcome.ExitCode, outcome.Message));
        Assert.Equal([SideTaskService.NoRecords], outcome.Details);
        Assert.Empty(stored.Runs);
    }

    /// <summary>
    /// The machine's timers and the time that passes, its time of day held where it began: what a run computes from the
    /// time of day - how long a limit has left - is the same however long the machine takes to get there.
    /// </summary>
    private sealed class FrozenWall : TimeProvider
    {
        private readonly DateTimeOffset _at = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _at;
    }

    /// <summary>
    /// A process table whose reading takes the person's interrupts - pressed as it is read - and waits, as a slow machine
    /// does, until it is stopped or a while has passed.
    /// </summary>
    private sealed class PressingTable(SideTaskInterrupts interrupts, int presses) : IProcessTable
    {
        public bool Stopped { get; private set; }

        public async Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
        {
            for (var press = 0; press < presses; press++)
            {
                interrupts.Pressed();
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Stopped = true;
                throw;
            }

            return new ProcessTableReading([], null);
        }
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

            // The person presses Ctrl+C while the turn runs; the client cuts the turn where it stands, as every client does.
            interrupts.Pressed();
            await run.Interrupt!.Asked.WaitAsync(TimeSpan.FromSeconds(30), token);
            return new AgentRunResult(AgentRunStatus.Cancelled, Model: run.Model, Error: AgentTurnInterrupt.Cut);
        };

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "cut", Chat: typed.Chat()) { Interrupts = interrupts }, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            ["half an answer", "the turn was interrupted; the conversation keeps what was said up to its last whole turn - ask again, or /exit to leave", "quickly done"],
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
            await run.Interrupt!.Asked.WaitAsync(TimeSpan.FromSeconds(30), token);
            return new AgentRunResult(AgentRunStatus.Cancelled, Model: run.Model, Error: AgentTurnInterrupt.Cut);
        };

        var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "slow", Model: "glm-5.3", Name: "cut-once") { Interrupts = interrupts }, Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal([$"Records: {LayoutOf(temp, "cut-once").Directory}"], outcome.Details);
        Assert.Equal(SideTaskStatus.Ended, RecordOf(harness, temp, LayoutOf(temp, "cut-once").Directory)!.Status);
        Assert.False(File.Exists(LayoutOf(temp, "cut-once").OwnerFile), "its owner is released");
    }

    /// <summary>
    /// A one shot's interrupt on a client that stores its conversations cuts its turn where it stands, as a Claude Code
    /// launch's is: the request under way is left, what its provider had said it used is the side-task's - counted at the
    /// model's prices, its key's spend recorded - and it ends interrupted (130); where the provider had said nothing of it
    /// yet, as Z.AI says it only at its answer's end, the cost is unknown, saying why, and no spend is recorded - never
    /// naught.
    /// </summary>
    [Theory]
    [InlineData(ClaudeApiClient.ClientName)]
    [InlineData(ZaiApiClient.ClientName)]
    public async Task AOneShotsInterrupt_OnAnApiClient_LeavesItsRequest_ItsUseCostAndSpendKept(string client)
    {
        var endpoint = new ApiEndpoint();
        var opened = new List<IDisposable>();

        try
        {
            var (harness, temp, service, _, _) = await RepoAsync(apis: ApiClients(endpoint, opened));
            using var tree = temp;
            var interrupts = new SideTaskInterrupts();
            var api = Api.Of(client);
            endpoint.Then(api.Cut(() => interrupts.Pressed()));

            // Bounded, so a cut the client does not honour fails here rather than waiting on the silent request for good.
            var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "slow", Model: api.Model, Client: client, Name: "cut-api") { Interrupts = interrupts }, Token).WaitAsync(TimeSpan.FromMinutes(1), Token);

            Assert.Equal((HarnessExit.Cancelled, $"side-task 'cut-api' ended Cancelled: {AgentTurnInterrupt.Cut}"), (outcome.ExitCode, outcome.Message));
            Assert.Single(endpoint.Asked);
            var record = RecordOf(harness, temp, LayoutOf(temp, "cut-api").Directory)!;
            Assert.Equal((api.CutUsed, api.CutCost), (record.InAll!.Total, record.InAll.Cost));
            Assert.Equal([(AgentRunStatus?)AgentRunStatus.Cancelled], record.Turns.Select(turn => turn.Status));
            Assert.Equal(
                api.CutSpend,
                new AgentKeySpend(harness.FileSystem, harness.OrchestrationStore, TimeProvider.System).Spent(new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path)));
        }
        finally
        {
            opened.ForEach(each => each.Dispose());
        }
    }

    /// <summary>
    /// The first interrupt of a chat on a client that stores its conversations cuts the turn where it stands and leaves
    /// the chat open: the request under way is left - what its provider said it used kept as the turn's, an unknown cost
    /// unknown - its partial answer thrown away, so the next message goes on from the conversation's last whole turn,
    /// and what both turns used is the side-task's.
    /// </summary>
    [Theory]
    [InlineData(ClaudeApiClient.ClientName)]
    [InlineData(ZaiApiClient.ClientName)]
    public async Task TheFirstInterrupt_OnAnApiClient_CutsTheTurnWhereItStands_AndTheChatGoesOn(string client)
    {
        var endpoint = new ApiEndpoint();
        var opened = new List<IDisposable>();

        try
        {
            var (harness, temp, service, _, _) = await RepoAsync(apis: ApiClients(endpoint, opened));
            using var tree = temp;
            var interrupts = new SideTaskInterrupts();
            var typed = new Typed(["slow one", "quick one"]);
            var api = Api.Of(client);
            endpoint.Then(api.Cut(() => interrupts.Pressed())).Then(api.Answer("quickly done"));

            var outcome = await service.RunAsync(new SideTaskCall(temp.Path, Model: api.Model, Client: client, Name: "cut-chat", Chat: typed.Chat()) { Interrupts = interrupts }, Token).WaitAsync(TimeSpan.FromMinutes(1), Token);

            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.Equal(
                ["half an answer", "the turn was interrupted; the conversation keeps what was said up to its last whole turn - ask again, or /exit to leave", "quickly done"],
                typed.Said);
            Assert.Equal(2, endpoint.Asked.Count);
            Assert.DoesNotContain("half an answer", endpoint.Asked[1].Body, StringComparison.Ordinal);
            var record = RecordOf(harness, temp, LayoutOf(temp, "cut-chat").Directory)!;
            Assert.Equal([AgentRunStatus.Cancelled, AgentRunStatus.Completed], record.Turns.Select(turn => turn.Status!.Value));
            Assert.Equal((api.CutUsed.Plus(api.AnswerUsed), api.ChatCost), (record.InAll!.Total, record.InAll.Cost));
        }
        finally
        {
            opened.ForEach(each => each.Dispose());
        }
    }

    /// <summary>
    /// What a test of an API client's interrupted turn sends and reads back, by the client: the request cut after its
    /// provider began a piece of its answer - the Claude API having said what it read, Z.AI nothing yet - and a whole
    /// answer after it.
    /// </summary>
    /// <param name="Model">The model the side-task runs on.</param>
    /// <param name="Cut">The answer that falls silent after half an answer, doing the given action as it does.</param>
    /// <param name="Answer">A whole answer of the given text.</param>
    /// <param name="CutUsed">What the cut request is counted to have used.</param>
    /// <param name="CutCost">What the cut turn cost.</param>
    /// <param name="CutSpend">What its key is recorded to have spent.</param>
    /// <param name="AnswerUsed">What the whole answer used.</param>
    /// <param name="ChatCost">What the cut turn and the whole answer cost together.</param>
    private sealed record Api(
        string Model,
        Func<Action, ApiReply> Cut,
        Func<string, ApiReply> Answer,
        AgentUsage CutUsed,
        AgentCost CutCost,
        KeyValuePair<(string Provider, string Key), decimal>[] CutSpend,
        AgentUsage AnswerUsed,
        AgentCost ChatCost)
    {
        public static Api Of(string client)
        {
            if (client == ClaudeApiClient.ClientName)
            {
                // Sonnet at 2 a million in and 10 out: the cut request's 1000 in and 1 out cost 0.00201, the answer's 10 in
                // and 5 out 0.00007.
                return new Api(
                    "claude-sonnet-5-5",
                    held => ApiReply.Silent(ClaudeApiFixtures.Events(Begun(1000), TextBegun, Text("half an answer")), held),
                    text => ApiReply.Stream(ClaudeApiFixtures.Events([Begun(10), TextBegun, Text(text), ("content_block_stop", """{"type":"content_block_stop","index":0}"""), .. ClaudeApiFixtures.End("end_turn")])),
                    new AgentUsage(1000, 1, 0, 0, 0, 0),
                    new AgentCost(Counted: 0.00201m),
                    [new(("anthropic", "ANTHROPIC_API_KEY"), 0.00201m)],
                    new AgentUsage(10, 5, 0, 0, 0, 0),
                    new AgentCost(Counted: 0.00208m));
            }

            var unsaid = new AgentCost(MissingPrice: "a request to 'glm-5.3' on ZAI_API_KEY was left before its provider said what it used");

            return new Api(
                "glm-5.3",
                held => ApiReply.Silent(Chunk("half an answer"), held),
                ZaiApiFixtures.Answer,
                AgentUsage.None,
                unsaid,
                [],
                new AgentUsage(20, 8, 0, 0, 0, 0),
                unsaid);

            static (string, string) Begun(long input)
                => ("message_start", """{"type":"message_start","message":{"id":"msg_000000000000000000000099","type":"message","role":"assistant","model":"claude-sonnet-5-5","content":[],"stop_reason":null,"usage":{"input_tokens":""" + input.ToString(System.Globalization.CultureInfo.InvariantCulture) + ""","output_tokens":1}}}""");

            static (string, string) Text(string text)
                => ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":""" + JsonSerializer.Serialize(text) + "}}");

            static string Chunk(string text)
                => "data: {\"id\":\"1\",\"object\":\"chat.completion.chunk\",\"model\":\"glm-5.3\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n\n";
        }

        private static (string, string) TextBegun => ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
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
    /// the engine's compaction of the one before carried in its first message before the person's message - and the
    /// compaction is said in the side-task's stream and counted in its run's record, as a step's is, once, and told the
    /// person once, as the run given it begins: the turn's ending says what the next message does, never that it was
    /// compacted, and the command's own lines say nothing of it.
    /// </summary>
    [Fact]
    public async Task ATurnThatOutgrewItsContext_CompactsTheConversation_ForTheNextMessage()
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["a long one", "go on"]);
        stored.Script = OutgrowsItsContextOn("a long one");

        await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "compacted", Chat: typed.Chat()), Token);

        Assert.Equal(2, stored.Runs.Count);
        Assert.Null(stored.Runs[1].Conversation);
        Assert.Null(stored.Runs[1].Compaction);
        Assert.Equal("go on", stored.Runs[1].UserPrompt);
        Assert.Equal(
            Carried(1, "a long one", ["iteration 1 of 1:", "too much"]),
            stored.Runs[1].Continuation!.ReplaceLineEndings("\n"));

        var directory = LayoutOf(temp, "compacted").Directory;
        var stream = (await File.ReadAllLinesAsync(Path.Combine(directory, StoredStream.FileName), Token))
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<AgentEvent>(line, JsonStateFile.LineOptions)!)
            .ToList();
        var left = stream.OfType<SessionStarted>().First().SessionId;
        var said = Assert.Single(stream.OfType<Compacted>());

        Assert.Equal(
            (AgentCompaction.OutgrewItsContext, left, stream.OfType<SessionStarted>().Last().SessionId, 1, 0, (long)stored.Runs[1].Continuation!.Length),
            (said.Why, said.Left, said.Began, said.KeptWordForWord, said.Reduced, said.SizeAfter));
        Assert.True(stream.FindLastIndex(one => one is RunStarted) < stream.IndexOf(said), "said by the second turn's run, which is given it");
        Assert.Equal(
            1,
            System.Text.Json.JsonSerializer.Deserialize<AgentRunResult>(File.ReadAllText(Path.Combine(directory, AgentRunner.ResultFileName)), JsonStateFile.Options)!.Compactions);

        Assert.Equal(
            [
                "too much",
                "the turn ended ContextExhausted: the context is full; the next message begins a new conversation, going on from the conversation so far",
                $"compacted: {AgentCompaction.OutgrewItsContext}; this turn begins a new conversation from what it holds",
                "fresh",
            ],
            typed.Said);
        Assert.Equal(
            [$"{SideTaskService.RunCommand}: side-task 'compacted' on glm-5.3 through glm; /help lists what the chat takes"],
            harness.StandardOutput.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>
    /// A side-task whose last turn outgrew its model's context - a one shot's, or a chat's left right after it - keeps no
    /// stored conversation to go on with, for handed again it would meet the same wall: opened again, it goes on from
    /// the engine's compaction of its conversation, said and counted once as the run given it begins, and the new
    /// conversation that run began is the one it goes on with after.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASideTaskThatOutgrewItsContext_OpenedAgain_GoesOnFromItsCompaction(bool chat)
    {
        var (harness, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        stored.Script = OutgrowsItsContextOn("a long one");

        var first = await service.RunAsync(
            chat
                ? new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "walled", Chat: new Typed(["a long one"]).Chat())
                : new SideTaskCall(temp.Path, Prompt: "a long one", Model: "glm-5.3", Name: "walled"),
            Token);

        var directory = LayoutOf(temp, "walled").Directory;
        Assert.Equal(chat ? HarnessExit.Success : HarnessExit.CommandFailed, first.ExitCode);
        Assert.Null(RecordOf(harness, temp, directory)!.Conversation);

        var again = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "go on", Resume: "walled"), Token);

        Assert.Equal((HarnessExit.Success, "side-task 'walled' answered on glm-5.3"), (again.ExitCode, again.Message));
        Assert.Equal(2, stored.Runs.Count);
        Assert.Equal(("go on", (AgentSessionRef?)null), (stored.Runs[1].UserPrompt, stored.Runs[1].Conversation));
        Assert.Equal(
            Carried(1, "a long one", ["iteration 1 of 1:", "too much"]),
            stored.Runs[1].Continuation!.ReplaceLineEndings("\n"));

        var stream = (await File.ReadAllLinesAsync(Path.Combine(directory, StoredStream.FileName), Token))
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<AgentEvent>(line, JsonStateFile.LineOptions)!)
            .ToList();
        Assert.Equal(AgentCompaction.OutgrewItsContext, Assert.Single(stream.OfType<Compacted>()).Why);

        var began = stream.OfType<SessionStarted>().Last();
        Assert.Equal(new AgentSessionRef(StoredClient.ClientName, "zai", began.SessionId!), RecordOf(harness, temp, directory)!.Conversation);
    }

    /// <summary>
    /// A turn given the compaction that never starts - its provider not serving the model before any answer, though its
    /// session began - says and counts none, and leaves the conversation outgrown: the next message is given the
    /// compaction again, and it is said and counted once, then.
    /// </summary>
    [Fact]
    public async Task ACompactedTurnThatNeverStarted_LeavesTheConversationOutgrown_ForTheNextMessage()
    {
        var (_, temp, service, stored, _) = await RepoAsync();
        using var tree = temp;
        var typed = new Typed(["a long one", "go on", "again"]);
        var outgrows = OutgrowsItsContextOn("a long one");
        stored.Script = async (run, events, token) =>
        {
            if (run.UserPrompt != "go on")
            {
                return await outgrows(run, events, token);
            }

            await events.EmitAsync(new SessionStarted(StoredClient.ClientName, run.Model ?? "glm-5.3", "hash", [], [], HandedConversation: false, Provider: "zai", SessionId: $"c{Guid.NewGuid():N}"));
            return AgentEndings.NotStarted(StoredClient.ClientName, AgentNotStartedBecause.ConfigurationWrong, "the plan does not serve the model", run.Model, run.Mask);
        };

        await service.RunAsync(new SideTaskCall(temp.Path, Model: "glm-5.3", Name: "unserved", Chat: typed.Chat()), Token);

        Assert.Equal(["a long one", "go on", "again"], stored.Runs.Select(run => run.UserPrompt));
        Assert.Equal(((AgentSessionRef?)null, (AgentSessionRef?)null), (stored.Runs[1].Conversation, stored.Runs[2].Conversation));
        Assert.Equal(stored.Runs[1].Continuation, stored.Runs[2].Continuation);

        var stream = (await File.ReadAllLinesAsync(Path.Combine(LayoutOf(temp, "unserved").Directory, StoredStream.FileName), Token))
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<AgentEvent>(line, JsonStateFile.LineOptions)!)
            .ToList();
        var said = Assert.Single(stream.OfType<Compacted>());

        Assert.Equal((AgentCompaction.OutgrewItsContext, stream.OfType<SessionStarted>().Last().SessionId), (said.Why, said.Began));
        Assert.True(stream.FindLastIndex(one => one is RunStarted) < stream.IndexOf(said), "said by the third message's run, which began the conversation");
    }

    /// <summary>A stored client's script whose turn on <paramref name="prompt"/> outgrows the model's context, every other answered.</summary>
    private static Func<AgentRun, IAgentEventSink, CancellationToken, Task<AgentRunResult>> OutgrowsItsContextOn(string prompt)
        => async (run, events, _) =>
        {
            if (run.UserPrompt == prompt)
            {
                await StoredClient.SpeakAsync(run, events, "too much");
                return new AgentRunResult(AgentRunStatus.ContextExhausted, Model: run.Model, Error: "the context is full") { Turns = 1 };
            }

            return await StoredClient.Answered(run, events, "fresh");
        };

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
        Assert.Equal(
            Carried(1, "begin here", ["iteration 1 of 1:", "the answer"]),
            stored.Runs[1].Continuation!.ReplaceLineEndings("\n"));

        var launch = Assert.Single(rendered.Runs);
        Assert.Equal(
            Carried(2, "begin here", ["iteration 1 of 2:", "the answer", string.Empty, "the engine:", "go on there", string.Empty, "iteration 2 of 2:", "the answer"]),
            launch.Continuation!.ReplaceLineEndings("\n"));
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
        Assert.Equal(
            Carried(1, "one", ["iteration 1 of 1:", "the answer"]),
            stored.Runs[1].Continuation!.ReplaceLineEndings("\n"));
        Assert.Null(stored.Runs[1].Conversation);
        Assert.All(stored.Runs.Skip(2), run => Assert.NotNull(run.Conversation));

        // Carried once: the earlier turns reduced, the last three word for word, and no record inside another.
        Assert.Equal(
            string.Join(
                "\n",
                $"{AgentCompaction.SaysWhatItIs}5. Everything up to iteration 2 is reduced to what you wrote and what you called; the last 3 iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                "one",
                string.Empty,
                "Reduced, everything up to the last iterations:",
                string.Empty,
                "iteration 1: the answer",
                "the engine:",
                "two",
                "iteration 2: the answer",
                "the engine:",
                "three",
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                "iteration 3 of 5:",
                "the answer",
                string.Empty,
                "the engine:",
                "four",
                string.Empty,
                "iteration 4 of 5:",
                "the answer",
                string.Empty,
                "the engine:",
                "five",
                string.Empty,
                "iteration 5 of 5:",
                "the answer",
                string.Empty,
                "The task again:",
                string.Empty,
                "one"),
            Assert.Single(rendered.Runs).Continuation!.ReplaceLineEndings("\n"));
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
            $"{SideTaskService.RunCommand}: WARN - the owner recorded for side-task 'orphan' (process {Environment.ProcessId}) has gone; it was reclaimed",
            harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.False(File.Exists(LayoutOf(temp, "orphan").OwnerFile), "and released once it ended");
    }

    /// <summary>
    /// A name that is taken is refused, never taken over: the command wrote no records, and the records the name holds
    /// are named as the other side-task's - never as this command's, in text or in its document.
    /// </summary>
    [Fact]
    public async Task ANameThatIsTakenIsRefused_TheRecordsItHoldsNamedAsTheOthers()
    {
        var (_, temp, service, _, _) = await RepoAsync();
        using var tree = temp;

        var first = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "one", Model: "glm-5.3", Name: "mine"), Token);
        Assert.Equal(HarnessExit.Success, first.ExitCode);

        var second = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "two", Model: "glm-5.3", Name: "mine"), Token);
        var json = await service.RunAsync(new SideTaskCall(temp.Path, Prompt: "two", Model: "glm-5.3", Name: "mine", Json: true), Token);

        Assert.Equal((HarnessExit.Refused, HarnessExit.Refused), (second.ExitCode, json.ExitCode));
        Assert.Equal("side-task 'mine' is taken: --resume mine opens it again, or --name gives a new one another name. Nothing was changed.", second.Message);
        Assert.Equal([SideTaskService.NoRecords, $"Side-task 'mine' keeps its own records in {LayoutOf(temp, "mine").Directory}"], second.Details);

        // No document of the side-task's: the command answers in its stopped shape, naming no records of its own.
        Assert.Empty(json.Data);
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
    /// The terminal's interrupts are counted for the side-task and said by their place in the count and by whether a turn
    /// was there to cut - the first cuts the turn where one runs, a second ends the chat - and a hang-up is one more, after
    /// which nothing more is said. A first interrupt with no turn to cut - the side-task being set up - never says a turn
    /// was cut.
    /// </summary>
    [Theory]
    [InlineData(true, true, SideTaskTerminal.ChatCut)]
    [InlineData(true, false, SideTaskTerminal.ChatIdle)]
    [InlineData(false, true, SideTaskTerminal.OneShotCut)]
    [InlineData(false, false, SideTaskTerminal.OneShotStopped)]
    public void TheTerminalsInterrupts_AreCountedAndSaid_ByWhetherATurnTookThem(bool chat, bool turning, string first)
    {
        var harness = new HarnessFactory();
        var terminal = new SideTaskTerminal(harness.Output, chat);
        var cut = 0;

        if (turning)
        {
            terminal.Interrupts.WhileTurning(_ =>
            {
                cut++;
                return true;
            });
        }

        terminal.Interrupted();
        Assert.False(terminal.Interrupts.Ended.IsCompleted);
        terminal.HungUp();

        Assert.Equal((2, true, turning ? 2 : 0), (terminal.Interrupts.Presses, terminal.Interrupts.Ended.IsCompleted, cut));
        Assert.Equal(
            [$"{SideTaskService.RunCommand}: WARN - {first}"],
            harness.StandardError.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
