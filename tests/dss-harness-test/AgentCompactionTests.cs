using System.Text.Json;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// The engine's compaction (part 03): the reduction the engine works out from the stored stream - the same input
/// always the same record, no model in it - the run context and the task whole, the last iterations word for word by
/// the continuation record's rule, the earlier messages reduced with their calls one line and their results left out,
/// and the new conversation it begins, on every client.
/// </summary>
public sealed class AgentCompactionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The record says what it is, holds the run context and the task whole, the reduced part, the last iterations word for word, and the task again.</summary>
    [Fact]
    public void TheRecord_SaysWhatItIs_HoldsThePartsWhole_AndEndsWithTheTaskAgain()
    {
        var compacted = AgentCompaction.Reduce(Stream(), new AgentContinuationBounds(LastIterations: 2), new AgentCompactionBounds());

        Assert.StartsWith(
            "This conversation was compacted by the engine at iteration 4. Everything up to iteration 2 is reduced to what you wrote and what you called; the last 2 iterations are word for word.",
            compacted.Record,
            StringComparison.Ordinal);
        Assert.Contains($"The run context and the task, as they were sent:{Environment.NewLine}{Environment.NewLine}do the work", compacted.Record, StringComparison.Ordinal);

        // The earlier iterations: the message text whole, the thinking left out, each call one line with its input
        // cut, how it ended and the path, the result itself left out.
        Assert.DoesNotContain("the thinking of the first", compacted.Record, StringComparison.Ordinal);
        Assert.Contains($"iteration 1: the first message{Environment.NewLine}  call c-1 (view_file): {{\"path\":\"a.txt\"}} (ok) [path: a.txt]", compacted.Record, StringComparison.Ordinal);
        Assert.Contains($"iteration 2: the second message{Environment.NewLine}  call c-2 (create_file): {{\"path\":\"made.txt\",\"content\":\"x\"}} (the error's first line)", compacted.Record, StringComparison.Ordinal);
        Assert.DoesNotContain("the whole result of the call", compacted.Record, StringComparison.Ordinal);

        // The engine's message since the first, whole, in its place.
        Assert.Contains($"the engine:{Environment.NewLine}what the engine said", compacted.Record, StringComparison.Ordinal);

        // The last iterations word for word, the continuation record's rule and bounds.
        Assert.Contains("iteration 3 of 4:", compacted.Record, StringComparison.Ordinal);
        Assert.Contains("iteration 4 of 4:", compacted.Record, StringComparison.Ordinal);
        Assert.Contains("the third message", compacted.Record, StringComparison.Ordinal);
        Assert.Contains("the fourth message", compacted.Record, StringComparison.Ordinal);

        Assert.EndsWith($"The task again:{Environment.NewLine}{Environment.NewLine}do the work", compacted.Record.TrimEnd(), StringComparison.Ordinal);
        Assert.Equal((2, 2), (compacted.KeptWordForWord, compacted.Reduced));
    }

    /// <summary>The same stream always gives the same record: a rule of the reduction, pinned.</summary>
    [Fact]
    public void TheSameStream_AlwaysGivesTheSameRecord()
    {
        var stream = Stream();
        var bounds = new AgentCompactionBounds();

        Assert.Equal(AgentCompaction.Reduce(stream, new AgentContinuationBounds(), bounds).Record, AgentCompaction.Reduce(stream, new AgentContinuationBounds(), bounds).Record);
    }

    /// <summary>Only the newest attempt is compacted, and a stream that wrote nothing says so.</summary>
    [Fact]
    public void OnlyTheNewestAttempt_IsCompacted()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent("the first attempt's task", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("the first attempt", []),
            new RunStarted(2),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("the second attempt", []),
        };

        var compacted = AgentCompaction.Reduce(stream, new AgentContinuationBounds(), new AgentCompactionBounds());

        Assert.Contains("the second attempt", compacted.Record, StringComparison.Ordinal);
        Assert.DoesNotContain("the first attempt's", compacted.Record, StringComparison.Ordinal);
        Assert.DoesNotContain("the first attempt ", compacted.Record, StringComparison.Ordinal);

        var empty = AgentCompaction.Reduce([new RunStarted(1)], new AgentContinuationBounds(), new AgentCompactionBounds());

        Assert.Contains("at iteration 0.", empty.Record, StringComparison.Ordinal);
        Assert.Contains("the last 0 iterations are word for word", empty.Record, StringComparison.Ordinal);
    }

    /// <summary>A call's input is cut at its bound of characters in a reduced line.</summary>
    [Fact]
    public void ACallSInput_IsCutAtItsBound()
    {
        var longInput = new string('x', 500);
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("one", [new AgentToolCall("c-1", AgentTools.RunCommand)]),
            new ToolCallFinished("c-1", Ok: true, TimeSpan.Zero, longInput, "ok"),
            new TurnStarted(2),
            new AssistantMessage("two", []),
        };

        var compacted = AgentCompaction.Reduce(stream, new AgentContinuationBounds(LastIterations: 1), new AgentCompactionBounds());

        Assert.Contains(new string('x', 400), compacted.Record, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 401), compacted.Record, StringComparison.Ordinal);
    }

    /// <summary>Where the reduced part is over its bound, the oldest messages of the agent are cut to their first 2,000 characters each, oldest first, and the record says how many were.</summary>
    [Fact]
    public void OverItsBound_TheOldestMessagesAreCut_AndSaid()
    {
        var first = new string('a', 3_000);
        var second = new string('b', 3_000);
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage(first, []),
            new TurnStarted(2),
            new AssistantMessage(second, []),
            new TurnStarted(3),
            new AssistantMessage("last", []),
        };

        var compacted = AgentCompaction.Reduce(stream, new AgentContinuationBounds(LastIterations: 1), new AgentCompactionBounds(ReducedChars: 5_200));

        Assert.Contains("1 of the oldest messages were cut to their first 2000 characters each.", compacted.Record, StringComparison.Ordinal);
        Assert.Contains(TextCut.Head(first, 2_000), compacted.Record, StringComparison.Ordinal);
        Assert.Contains(second, compacted.Record, StringComparison.Ordinal);
    }

    /// <summary>The bounds read from the configuration, each at its default where it says none, and one that is not a positive number refuses the file.</summary>
    [Fact]
    public void TheBounds_ReadFromTheConfiguration_EachAtItsDefault()
    {
        Assert.Equal(new AgentCompactionBounds(), AgentCompactionBounds.Of(null));
        Assert.Equal(new AgentCompactionBounds(9, 100), AgentCompactionBounds.Of(new AgentCompactionConfig { ReducedChars = 9, CallInputChars = 100 }));
        Assert.Equal(new AgentCompactionBounds(200_000, 77), AgentCompactionBounds.Of(new AgentCompactionConfig { CallInputChars = 77 }));
        Assert.Equal(
            "'agentContext.compaction.reducedChars' is 0, and a bound of the compaction is a positive number",
            Assert.Single(new AgentCompactionConfig { ReducedChars = 0 }.Problems()));
    }

    /// <summary>A run given a compaction record begins a new conversation whose first message is the record, whole, on every client - and the runner keeps it as the step's user.md.</summary>
    [Fact]
    public async Task ACompactedRun_BeginsANewConversation_WithTheRecordAsItsFirstMessage()
    {
        var events = new RecordedEvents();
        var chat = new FakeChat().Answer(FakeChat.Said("done"));
        var step = Path.Combine(_temp.Path, "step");
        var run = AgentRuns.Of(step, _temp.Combine("work")) with
        {
            Model = FakeChat.Model,
            Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30)),
            SessionsDirectory = Path.Combine(_temp.Path, "sessions"),
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1")]),
            Entries = new AgentEntries(
                new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Api = "https://api.anthropic.com", Currency = "USD" },
                new AgentModelConfig { Code = FakeChat.Model, MaxOutputTokens = 64000 },
                null),
            Ground = PolicyGrounds.Of(_temp.Combine("work"), [], [], [], [], []),
            UserPrompt = "do the work",
            RunContext = "run facts",
            Compaction = "the compaction record",
        };

        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var loop = new ToolLoop(
            _harness.FileSystem,
            new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System),
            TimeProvider.System,
            (_, _) => Task.CompletedTask,
            () => 0);

        var result = await loop.RunAsync(run, events, chat, new ToolLoopTerms("claude-api", run.Entries!.Provider, run.Entries!.Model), Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("the compaction record", FakeChat.ContentOf(chat.Requests[0].Messages[0]));
        Assert.Single(chat.Requests);

        // The runner keeps the message as sent, masked, as the step's user.md.
        var client = new Scripted();
        await new AgentRunner([client], _harness.FileSystem, Elevation.User, TimeProvider.System).RunAsync(
            new AgentStepRequest(
                "r1", "a7", "s1", 1, null, null, null, null, null, null, null, "be terse", "do the {item}", new Dictionary<string, string> { ["item"] = "work" }, null,
                new AgentWorkspace(null, _temp.Combine("work"), _temp.Path, [], _temp.Combine("work"), _temp.Path),
                null, null, new AgentLimits(10, null, null, null), null)
            {
                Compaction = "the compaction record",
            },
            events: null,
            Token);

        var userMd = Path.Combine(_temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1", "user.md");

        Assert.True(File.Exists(userMd));
        Assert.Equal("the compaction record", File.ReadAllText(userMd));
    }

    /// <summary>The stream a compacted conversation is reduced from: one attempt, a first message, two reduced iterations with calls, an engine message, and the last two.</summary>
    private static AgentEvent[] Stream()
        =>
        [
            new RunStarted(1),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new ToolCallStarted("c-1", AgentTools.ViewFile, AgentToolRunner.Harness),
            new AssistantMessage("the first message", [new AgentToolCall("c-1", AgentTools.ViewFile)], Thinking: "the thinking of the first"),
            new ToolCallFinished("c-1", Ok: true, TimeSpan.Zero, """{"path":"a.txt"}""", "the whole result of the call"),
            new TurnStarted(2),
            new ToolCallStarted("c-2", AgentTools.CreateFile, AgentToolRunner.Harness),
            new AssistantMessage("the second message", [new AgentToolCall("c-2", AgentTools.CreateFile)]),
            new ToolCallFinished("c-2", Ok: false, TimeSpan.Zero, """{"path":"made.txt","content":"x"}""", "the error's first line\nthe rest of the error"),
            new MessageSent("what the engine said", AgentMessageKind.PlanUpdate),
            new TurnStarted(3),
            new AssistantMessage("the third message", []),
            new TurnStarted(4),
            new AssistantMessage("the fourth message", []),
        ];

    /// <summary>A client that answers at once, remembering the run it was given.</summary>
    private sealed class Scripted : IAgentClient
    {
        public string Name => "scripted";

        public AgentClientCapabilities Capabilities { get; } = new(
            Streaming: true, Thinking: false, WritableWhileWorking: false, AgentConversationHandling.Handed, SchemaEnforcedOutput: false,
            AgentToolsOwner.Harness, AgentToolsOwner.Harness, ReportsCost: false, ["low"], ProviderType: "none", ToldModelBy: "the test");

        public Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
            => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Model: run.Model));
    }
}
