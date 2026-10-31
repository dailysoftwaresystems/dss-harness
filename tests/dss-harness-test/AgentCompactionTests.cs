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
/// The engine's compaction: the reduction the engine works out from the stored stream - the same input
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

        // The earlier iterations: the message text whole, the thinking left out, each call one line with its input cut,
        // how it ended and the path, the result itself left out; the engine's message since the first whole, in its
        // place; the last iterations word for word, by the continuation record's rule and bounds; the task again.
        Assert.Equal(
            Lines(
                "This conversation was compacted by the engine at iteration 4. Everything up to iteration 2 is reduced to what you wrote and what you called; the last 2 iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                "do the work",
                string.Empty,
                "Reduced, everything up to the last iterations:",
                string.Empty,
                "iteration 1: the first message",
                """  call c-1 (view_file): {"path":"a.txt"} (ok) [path: a.txt]""",
                "iteration 2: the second message",
                """  call c-2 (create_file): {"path":"made.txt","content":"x"} (the error's first line) [path: made.txt]""",
                AgentContinuation.EngineSaid,
                "what the engine said",
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                "iteration 3 of 4:",
                "the third message",
                string.Empty,
                "iteration 4 of 4:",
                "the fourth message",
                string.Empty,
                "The task again:",
                string.Empty,
                "do the work"),
            compacted.Record);
        Assert.Equal((2, 2), (compacted.KeptWordForWord, compacted.Reduced));
    }

    /// <summary>Lines joined as the records join theirs.</summary>
    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    /// <summary>The same stream always gives the same record: a rule of the reduction, pinned.</summary>
    [Fact]
    public void TheSameStream_AlwaysGivesTheSameRecord()
    {
        var stream = Stream();
        var bounds = new AgentCompactionBounds();

        Assert.Equal(AgentCompaction.Reduce(stream, new AgentContinuationBounds(), bounds).Record, AgentCompaction.Reduce(stream, new AgentContinuationBounds(), bounds).Record);
    }

    /// <summary>An attempt that wrote nothing says so: compacted at iteration naught, none kept word for word.</summary>
    [Fact]
    public void AnAttemptThatWroteNothing_SaysSo()
    {
        var empty = AgentCompaction.Reduce([new RunStarted(1)], new AgentContinuationBounds(), new AgentCompactionBounds());

        Assert.Contains("at iteration 0.", empty.Record, StringComparison.Ordinal);
        Assert.Contains("the last 0 iterations are word for word", empty.Record, StringComparison.Ordinal);
    }

    /// <summary>
    /// An attempt compacted a second time is reduced whole again - its first prompt the task as it was first sent, the
    /// earlier record never copied in, for the events it stood for are the attempt's own - and its iterations numbered
    /// across its runs: each record is the task and the work once, never a record of a record.
    /// </summary>
    [Fact]
    public void AnAttemptCompactedAgain_IsReducedWhole_NeverCopyingTheRecordBefore()
    {
        var first = AgentCompaction.Reduce(Stream(), new AgentContinuationBounds(LastIterations: 1), new AgentCompactionBounds());
        AgentEvent[] again =
        [
            .. Stream(),
            new RunStarted(1),
            new MessageSent(first.Record, AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("the fifth message", []),
            new TurnStarted(2),
            new AssistantMessage("the sixth message", []),
        ];

        var second = AgentCompaction.Reduce(AgentAttempt.Of(again, 1), new AgentContinuationBounds(LastIterations: 1), new AgentCompactionBounds());

        Assert.StartsWith(
            "This conversation was compacted by the engine at iteration 6. Everything up to iteration 5 is reduced to what you wrote and what you called; the last 1 iterations are word for word.",
            second.Record,
            StringComparison.Ordinal);

        // The head once, the task twice - where it was sent and again at the end - and the earlier record nowhere.
        Assert.Equal(1, Count(second.Record, AgentCompaction.SaysWhatItIs));
        Assert.Equal(2, Count(second.Record, "do the work"));
        Assert.Contains($"iteration 5: the fifth message{Environment.NewLine}", second.Record, StringComparison.Ordinal);
        Assert.Contains($"iteration 6 of 6:{Environment.NewLine}the sixth message", second.Record, StringComparison.Ordinal);
        Assert.Equal((1, 5), (second.KeptWordForWord, second.Reduced));
    }

    /// <summary>
    /// An attempt whose agent asked, was answered and then filled its context is compacted from its task: the attempt is
    /// both launches, each first message as Claude Code is sent it - the run context, and on the later launch the
    /// continuation record before the person's answer as its task - the answer one of the engine's messages in its
    /// place, never the first message the record holds and never the continuation record the later launch carried.
    /// </summary>
    [Fact]
    public void AnAttemptAnsweredThenFull_IsCompactedFromItsTask_TheAnswerInItsPlace()
    {
        var launch = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work")) with { RunContext = "run facts", UserPrompt = "do the work" };
        var first = AgentPromptParts.FirstMessage(launch, newConversation: true);
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent("the earlier attempt's task", AgentMessageKind.FirstPrompt),
            new RunStarted(2),
            new MessageSent(first, AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("which file?", []),
            new RunFinished(AgentRunStatus.Asked),
            new RunStarted(2),
            new MessageSent(AgentPromptParts.FirstMessage(launch with { Continuation = "the record of the launch before", UserPrompt = "the parser" }, newConversation: true), AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("working on the parser", []),
            new TurnStarted(2),
            new AssistantMessage("still working", []),
        };

        var compacted = AgentCompaction.Reduce(AgentAttempt.Of(stream, 2), new AgentContinuationBounds(LastIterations: 1), new AgentCompactionBounds());

        Assert.Equal(
            Lines(
                "This conversation was compacted by the engine at iteration 3. Everything up to iteration 2 is reduced to what you wrote and what you called; the last 1 iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                first,
                string.Empty,
                "Reduced, everything up to the last iterations:",
                string.Empty,
                "iteration 1: which file?",
                AgentContinuation.EngineSaid,
                "the parser",
                "iteration 2: working on the parser",
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                "iteration 3 of 3:",
                "still working",
                string.Empty,
                "The task again:",
                string.Empty,
                first),
            compacted.Record);
    }

    /// <summary>
    /// A launch begun again inside one run - a retry, a move to another client - counts its turns from one again: the
    /// attempt's iterations are numbered on across it, never back, and the task it was sent again is no message of the
    /// engine's.
    /// </summary>
    [Fact]
    public void ALaunchBegunAgainInsideItsRun_NumbersItsIterationsOn()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("one", []),
            new TurnStarted(2),
            new AssistantMessage("two", []),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("three", []),
            new TurnStarted(2),
            new AssistantMessage("four", []),
        };

        var compacted = AgentCompaction.Reduce(stream, new AgentContinuationBounds(LastIterations: 1), new AgentCompactionBounds());

        Assert.Equal(
            Lines(
                "This conversation was compacted by the engine at iteration 4. Everything up to iteration 3 is reduced to what you wrote and what you called; the last 1 iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                "do the work",
                string.Empty,
                "Reduced, everything up to the last iterations:",
                string.Empty,
                "iteration 1: one",
                "iteration 2: two",
                "iteration 3: three",
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                "iteration 4 of 4:",
                "four",
                string.Empty,
                "The task again:",
                string.Empty,
                "do the work"),
            compacted.Record);
    }

    /// <summary>
    /// What the engine sent among the last iterations is kept word for word in its place, as the continuation record's
    /// rule keeps it: a plan update told after the first kept message is never lost.
    /// </summary>
    [Fact]
    public void TheEnginesMessagesAmongTheLastIterations_AreKeptWordForWord()
    {
        AgentEvent[] stream =
        [
            .. Stream(),
            new MessageSent("the plan changed again", AgentMessageKind.PlanUpdate),
            new TurnStarted(5),
            new AssistantMessage("the fifth message", []),
        ];

        var compacted = AgentCompaction.Reduce(stream, new AgentContinuationBounds(LastIterations: 2), new AgentCompactionBounds());

        Assert.Equal(
            Lines(
                "This conversation was compacted by the engine at iteration 5. Everything up to iteration 3 is reduced to what you wrote and what you called; the last 2 iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                "do the work",
                string.Empty,
                "Reduced, everything up to the last iterations:",
                string.Empty,
                "iteration 1: the first message",
                """  call c-1 (view_file): {"path":"a.txt"} (ok) [path: a.txt]""",
                "iteration 2: the second message",
                """  call c-2 (create_file): {"path":"made.txt","content":"x"} (the error's first line) [path: made.txt]""",
                AgentContinuation.EngineSaid,
                "what the engine said",
                "iteration 3: the third message",
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                "iteration 4 of 5:",
                "the fourth message",
                string.Empty,
                AgentContinuation.EngineSaid,
                "the plan changed again",
                string.Empty,
                "iteration 5 of 5:",
                "the fifth message",
                string.Empty,
                "The task again:",
                string.Empty,
                "do the work"),
            compacted.Record);
    }

    /// <summary>
    /// A call whose input names a path that is no string is reduced without one, never ending the compaction: the input
    /// is the model's, and read as the engine's account reads it; a move names both its ends.
    /// </summary>
    [Fact]
    public void ACallsPaths_AreReadAsTheAccountReadsThem()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new MessageSent("do the work", AgentMessageKind.FirstPrompt),
            new TurnStarted(1),
            new AssistantMessage("one", [new AgentToolCall("c-1", AgentTools.ViewFile), new AgentToolCall("c-2", AgentTools.MovePath)]),
            new ToolCallFinished("c-1", Ok: true, TimeSpan.Zero, """{"path":1}""", "ok"),
            new ToolCallFinished("c-2", Ok: true, TimeSpan.Zero, """{"from":"a.txt","to":"b.txt"}""", "ok"),
            new TurnStarted(2),
            new AssistantMessage("two", []),
        };

        var compacted = AgentCompaction.Reduce(stream, new AgentContinuationBounds(LastIterations: 1), new AgentCompactionBounds());

        Assert.Equal(
            Lines(
                "This conversation was compacted by the engine at iteration 2. Everything up to iteration 1 is reduced to what you wrote and what you called; the last 1 iterations are word for word.",
                string.Empty,
                "The run context and the task, as they were sent:",
                string.Empty,
                "do the work",
                string.Empty,
                "Reduced, everything up to the last iterations:",
                string.Empty,
                "iteration 1: one",
                """  call c-1 (view_file): {"path":1} (ok)""",
                """  call c-2 (move_path): {"from":"a.txt","to":"b.txt"} (ok) [paths: a.txt, b.txt]""",
                string.Empty,
                "The last iterations, word for word:",
                string.Empty,
                "iteration 2 of 2:",
                "two",
                string.Empty,
                "The task again:",
                string.Empty,
                "do the work"),
            compacted.Record);
    }

    /// <summary>How many times <paramref name="part"/> stands in <paramref name="text"/>.</summary>
    private static int Count(string text, string part)
    {
        var count = 0;

        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
        var compaction = new AgentCompacted("the compaction record", KeptWordForWord: 2, Reduced: 3, SizeBefore: 400, SizeAfter: 21) { Left = "the-old-session" };

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

        // The runner keeps the message as sent, masked, as the step's user.md, says the compaction the run begins from -
        // the conversation it leaves, the one it begins, what the record kept and reduced - and counts it.
        var client = new Scripted();
        var seen = new RecordedEvents();
        var ran = await new AgentRunner([client], _harness.FileSystem, Elevation.User, TimeProvider.System).RunAsync(
            new AgentStepRequest(
                "r1", "a7", "s1", 1, null, null, null, null, null, null, null, "be terse", "do the work", null,
                new AgentWorkspace(null, _temp.Combine("work"), _temp.Path, [], _temp.Combine("work"), _temp.Path),
                null, null, new AgentLimits(10, null, null, null), null)
            {
                Compaction = compaction,
                SessionId = "the-new-session",
            },
            seen,
            Token);

        var userMd = Path.Combine(_temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1", "user.md");

        Assert.True(File.Exists(userMd));
        Assert.Equal("the compaction record", File.ReadAllText(userMd));
        Assert.Equal(1, ran.Compactions);

        var said = Assert.Single(seen.All.OfType<Compacted>());

        Assert.Equal(
            (AgentCompaction.AnsweredCompact, "the-old-session", "the-new-session", 2, 3, 400L, 21L),
            (said.Why, said.Left, said.Began, said.KeptWordForWord, said.Reduced, said.SizeBefore, said.SizeAfter));
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
