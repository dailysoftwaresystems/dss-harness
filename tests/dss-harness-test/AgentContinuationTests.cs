using System.Text.Json;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// Going on with earlier work (part 03): the continuation record the engine puts together from the step directories of
/// the agent - its parts in their order, its bounds, whatever is cut named with the copy that holds it whole, the last
/// iterations word for word from the stored stream of the newest attempt - and the first user message's parts, the run
/// context, the plan updates and the continuation record, as each client is sent them.
/// </summary>
public sealed class AgentContinuationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The whole record: its parts in their order, the handoff and the cut-off message and the last iterations last.</summary>
    [Fact]
    public void TheRecord_HoldsItsPartsInTheirOrder()
    {
        var (work, workDirectory) = Work();

        var record = Render(work, workDirectory);

        Assert.StartsWith(
            "You are going on with work begun by step 'items[2].implement'. This is a new conversation. What follows is the engine's record of that work, ending with its last iterations word for word; the files are as that work left them.",
            record,
            StringComparison.Ordinal);
        Assert.Contains("The steps so far, oldest first:", record, StringComparison.Ordinal);
        Assert.Contains("step 'items[1].plan' ended done.", record, StringComparison.Ordinal);
        Assert.Contains("step 'items[2].implement' ended stopped by a pause.", record, StringComparison.Ordinal);
        Assert.Contains("  Notes: what was done and why", record, StringComparison.Ordinal);
        Assert.Contains("  Output: {\"items\":[1]}", record, StringComparison.Ordinal);
        Assert.Contains($"The files changed so far, by name:{Environment.NewLine}src/one.cs{Environment.NewLine}src/two.cs", record, StringComparison.Ordinal);
        Assert.Contains($"- leg 'windows', phase 'build': the ledger's line (the ledger and its logs: {_temp.Combine("logs")})", record, StringComparison.Ordinal);
        Assert.Contains($"{AgentContinuation.HandoffHeading}:{Environment.NewLine}{Environment.NewLine}Done: the form.", record, StringComparison.Ordinal);
        Assert.Contains(AgentContinuation.CutOffMessage, record, StringComparison.Ordinal);
        var expectedEnd =
            $"iteration 2 of 2:{Environment.NewLine}the work stands{Environment.NewLine}{Environment.NewLine}call c-9 (view_file):{Environment.NewLine}  input: {{\"path\":\"a.txt\"}}{Environment.NewLine}  result: alpha{Environment.NewLine}  [the whole result is '{Path.Combine(work.StepDirectory, AgentCallRecords.ToolResultsDirectoryName, "c-9.txt")}']";

        Assert.True(record.TrimEnd().EndsWith(expectedEnd, StringComparison.Ordinal), $"EXPECTED: [{expectedEnd}]{Environment.NewLine}ACTUAL: [{record.TrimEnd()[^Math.Min(400, record.Length)..]}]");
    }

    /// <summary>An agent that changed none says so; a chain that kept no step says the attempt wrote none.</summary>
    [Fact]
    public void WhereNothingWasChangedOrWritten_TheRecordSaysSo()
    {
        var work = new AgentContinuationWork("s-1", [], [], [], CutOff: false);
        var record = Render(work, _temp.Combine("work"));

        Assert.Contains("The files: this agent has changed none.", record, StringComparison.Ordinal);
        Assert.Contains("The last iterations: the attempt wrote none.", record, StringComparison.Ordinal);
    }

    /// <summary>A step's notes and its output are each held to their own bound, cut at a whole line and named with the copy that holds the whole.</summary>
    [Fact]
    public void AStepsNotesAndOutput_AreHeldToTheirOwnBound_NamedWithTheCopy()
    {
        var (work, workDirectory) = Work();
        var notes = new string('n', 40);
        File.WriteAllText(Path.Combine(work.Steps[0].Directory, OrchestratorLayout.StepNotesFileName), $"first line{Environment.NewLine}{notes}");

        var record = Render(work, workDirectory, with => with with { StepChars = 12 });

        Assert.Contains($"  Notes: first line{Environment.NewLine}  [... ", record, StringComparison.Ordinal);
        Assert.DoesNotContain(notes, record, StringComparison.Ordinal);

        var copy = Path.Combine(workDirectory, AgentContinuation.CopiesFolder, $"{work.Steps[0].Instance}-notes.md");

        Assert.True(File.Exists(copy));
        Assert.Equal($"first line{Environment.NewLine}{notes}", File.ReadAllText(copy));
    }

    /// <summary>The files list is cut at its bound of paths, the count of the rest said.</summary>
    [Fact]
    public void TheFilesList_IsCutAtItsBoundOfPaths()
    {
        var (work, workDirectory) = Work();
        work = work with { ChangedPaths = [.. Enumerable.Range(1, 5).Select(each => $"src/file{each}.cs")] };

        var record = Render(work, workDirectory, with => with with { Paths = 3 });

        Assert.Contains($"src/file1.cs{Environment.NewLine}src/file2.cs{Environment.NewLine}src/file3.cs{Environment.NewLine}... and 2 more, not listed", record, StringComparison.Ordinal);
        Assert.DoesNotContain("src/file4.cs", record, StringComparison.Ordinal);
    }

    /// <summary>What is over the record's own bound is given up by the oldest steps, each named by its copies; the newest step is never dropped, and the last iterations are never crowded out.</summary>
    [Fact]
    public void TheOldestStepsAreDroppedToTheirCopies_WhereTheRecordIsOverItsBound()
    {
        var (work, workDirectory) = Work();
        File.WriteAllText(Path.Combine(work.Steps[0].Directory, OrchestratorLayout.StepNotesFileName), new string('n', 4_000));

        var record = Render(work, workDirectory, with => with with { RecordChars = 1_200 });

        Assert.Contains("1 older step left out here; each one's notes and output are in '", record, StringComparison.Ordinal);
        Assert.DoesNotContain("step 'items[1].plan' ended done.", record, StringComparison.Ordinal);
        Assert.Contains("step 'items[2].implement' ended stopped by a pause.", record, StringComparison.Ordinal);
        Assert.Contains("iteration 2 of 2:", record, StringComparison.Ordinal);
    }

    /// <summary>The last iterations: the last ones whole whatever their size, then as many earlier ones as fit, never cut in the middle of one, the stream's copy named where earlier ones were left out.</summary>
    [Fact]
    public void TheLastIterations_AreTheLastWhole_ThenAsManyEarlierAsFit_NeverCutInOne()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new TurnStarted(1),
            new AssistantMessage("first", []),
            new TurnStarted(2),
            new AssistantMessage("second", []),
            new TurnStarted(3),
            new AssistantMessage("third", []),
            new TurnStarted(4),
            new AssistantMessage("fourth", []),
            new TurnStarted(5),
            new AssistantMessage("fifth", []),
        };

        // The last two whole; of the earlier ones only 'third' fits the six characters left, so two are left out.
        var told = AgentContinuation.Iterations(stream, new AgentContinuationBounds(LastIterations: 2, IterationChars: 6), _temp.Combine("copies"));

        Assert.StartsWith("[2 earlier iterations left out: the stream of the attempt is in '", told, StringComparison.Ordinal);
        Assert.Contains("iteration 4 of 5:", told, StringComparison.Ordinal);
        Assert.Contains("iteration 5 of 5:", told, StringComparison.Ordinal);
        Assert.Contains("third", told, StringComparison.Ordinal);
        Assert.DoesNotContain("first", told, StringComparison.Ordinal);
        Assert.DoesNotContain("second", told, StringComparison.Ordinal);
    }

    /// <summary>Only the newest attempt's iterations are read: the stream holds every attempt's, each beginning with its RunStarted.</summary>
    [Fact]
    public void OnlyTheNewestAttemptsIterations_AreRead()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new TurnStarted(1),
            new AssistantMessage("the first attempt", []),
            new RunStarted(2),
            new TurnStarted(1),
            new AssistantMessage("the second attempt", []),
        };

        var told = AgentContinuation.Iterations(stream, new AgentContinuationBounds(), _temp.Combine("copies"));

        Assert.Contains("the second attempt", told, StringComparison.Ordinal);
        Assert.DoesNotContain("the first attempt", told, StringComparison.Ordinal);
    }

    /// <summary>An iteration is the agent's message as it wrote it, its thinking as far as the provider showed it, and each call it made with the tool, its input and its result as the stream holds them.</summary>
    [Fact]
    public void AnIteration_HoldsTheMessageTheThinkingAndEachCall()
    {
        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new TurnStarted(7),
            new ToolCallStarted("c-1", AgentTools.ViewFile, AgentToolRunner.Harness),
            new AssistantMessage("look", [new AgentToolCall("c-1", AgentTools.ViewFile)], Thinking: "where it is"),
            new ToolCallFinished("c-1", Ok: true, TimeSpan.Zero, """{"path":"a.txt"}""", "alpha", @"C:\run\tool-results\c-1.txt"),
            new AssistantMessage("alone", []),
        };

        var told = AgentContinuation.Iterations(stream, new AgentContinuationBounds(), _temp.Combine("copies"));

        Assert.Contains(
            $"iteration 7 of 7:{Environment.NewLine}[thinking]{Environment.NewLine}where it is{Environment.NewLine}[/thinking]{Environment.NewLine}look{Environment.NewLine}{Environment.NewLine}call c-1 (view_file):{Environment.NewLine}  input: {{\"path\":\"a.txt\"}}{Environment.NewLine}  result: alpha{Environment.NewLine}  [the whole result is 'C:\\run\\tool-results\\c-1.txt']",
            told,
            StringComparison.Ordinal);
    }

    /// <summary>The copies the record names are put in the agent's work directory: each step's notes and output, the stream of the newest attempt, and the results the stream names.</summary>
    [Fact]
    public void TheCopies_ArePutInTheAgentsWorkDirectory()
    {
        var (work, workDirectory) = Work();
        var results = Path.Combine(work.StepDirectory, AgentCallRecords.ToolResultsDirectoryName);
        Directory.CreateDirectory(results);
        File.WriteAllText(Path.Combine(results, "c-9.txt"), "alpha");

        Render(work, workDirectory);

        var copies = Path.Combine(workDirectory, AgentContinuation.CopiesFolder);

        Assert.Equal("what was done and why", File.ReadAllText(Path.Combine(copies, "items[2].implement-notes.md")));
        Assert.True(File.Exists(Path.Combine(copies, "items[2].implement-output.json")));
        Assert.True(File.Exists(Path.Combine(copies, StoredStream.FileName)));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(copies, AgentCallRecords.ToolResultsDirectoryName, "c-9.txt")));
    }

    /// <summary>A run given no part sends its prompt as it always did; a run given parts sends them in their order, the continuation record only where the conversation is a new one, the prompt as the task.</summary>
    [Fact]
    public void TheFirstUserMessage_HoldsThePartsInTheirOrder_TheContinuationOnlyOfANewConversation()
    {
        var bare = AgentRuns.Of(_temp.Combine("step"), _temp.Combine("work"));

        Assert.Equal(bare.UserPrompt, AgentPromptParts.FirstMessage(bare, newConversation: true));

        var going = bare with
        {
            UserPrompt = "do the work",
            RunContext = "run facts",
            PlanUpdates = "what changed",
            Continuation = "the record of the work",
        };

        Assert.Equal(
            $"<run-context>{Environment.NewLine}run facts{Environment.NewLine}</run-context>{Environment.NewLine}{Environment.NewLine}<plan-updates>{Environment.NewLine}what changed{Environment.NewLine}</plan-updates>{Environment.NewLine}{Environment.NewLine}<continuation>{Environment.NewLine}the record of the work{Environment.NewLine}</continuation>{Environment.NewLine}{Environment.NewLine}<task>{Environment.NewLine}do the work{Environment.NewLine}</task>",
            AgentPromptParts.FirstMessage(going, newConversation: true));
        Assert.Equal(
            $"<run-context>{Environment.NewLine}run facts{Environment.NewLine}</run-context>{Environment.NewLine}{Environment.NewLine}<plan-updates>{Environment.NewLine}what changed{Environment.NewLine}</plan-updates>{Environment.NewLine}{Environment.NewLine}<task>{Environment.NewLine}do the work{Environment.NewLine}</task>",
            AgentPromptParts.FirstMessage(going, newConversation: false));
    }

    /// <summary>The loop sends the parts as its client is sent them: a new conversation the record, a handed conversation none - and the runner writes the first user message to the step's user.md, masked, before the client is taken.</summary>
    [Fact]
    public async Task TheLoopSendsTheParts_AndTheRunnerWritesUserMd_Masked()
    {
        var events = new RecordedEvents();
        var chat = new FakeChat().Answer(FakeChat.Said("done"));
        var step = Path.Combine(_temp.Path, "step");
        var run = AgentRuns.Of(step, _temp.Combine("work"), new Masking("s3cret", "***")) with
        {
            Model = FakeChat.Model,
            Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30)),
            SessionsDirectory = Path.Combine(_temp.Path, "sessions"),
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1")]),
            Entries = new AgentEntries(
                new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Api = "https://api.anthropic.com", Currency = "USD" },
                new AgentModelConfig { Code = FakeChat.Model, MaxOutputTokens = 64000 },
                null),
            UserPrompt = "do the s3cret work",
            RunContext = "run facts",
            PlanUpdates = "what changed",
            Continuation = "the record of the work",
        };

        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var loop = new ToolLoop(
            _harness.FileSystem,
            new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System),
            TimeProvider.System,
            (_, _) => Task.CompletedTask,
            () => 0);

        run = run with { Ground = PolicyGrounds.Of(_temp.Combine("work"), [], [], [], [], []) };
        var result = await loop.RunAsync(run, events, chat, new ToolLoopTerms("claude-api", run.Entries!.Provider, run.Entries!.Model), Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.StartsWith($"<run-context>{Environment.NewLine}run facts", FakeChat.ContentOf(chat.Requests[0].Messages[0]), StringComparison.Ordinal);
        Assert.Contains("<continuation>", FakeChat.ContentOf(chat.Requests[0].Messages[0]), StringComparison.Ordinal);
        Assert.EndsWith($"<task>{Environment.NewLine}do the *** work{Environment.NewLine}</task>", FakeChat.ContentOf(chat.Requests[0].Messages[0]).TrimEnd(), StringComparison.Ordinal);

        // A handed conversation goes on with one more user message of run context and task alone: no record repeated.
        var handed = run with { Conversation = new AgentSessionRef("claude-api", "anthropic", Guid.NewGuid().ToString("D")) };
        var second = new FakeChat().Answer(FakeChat.Said("done"));

        await loop.RunAsync(handed, events, second, new ToolLoopTerms("claude-api", run.Entries!.Provider, run.Entries!.Model), Token);

        Assert.DoesNotContain("<continuation>", FakeChat.ContentOf(second.Requests[0].Messages[0]), StringComparison.Ordinal);
        Assert.StartsWith($"<run-context>{Environment.NewLine}run facts", FakeChat.ContentOf(second.Requests[0].Messages[0]), StringComparison.Ordinal);

        // The runner writes the first user message as it is sent, masked, before the client is taken.
        var client = new Scripted();
        await new AgentRunner([client], _harness.FileSystem, Elevation.User, TimeProvider.System).RunAsync(
            new AgentStepRequest(
                "r1", "a7", "s1", 1, null, null, null, null, null, null, null, "be terse", "do the {item}", new Dictionary<string, string> { ["item"] = "s3cret thing" }, null,
                new AgentWorkspace(null, _temp.Combine("work"), _temp.Path, [], _temp.Combine("work"), _temp.Path),
                null, null, new AgentLimits(10, null, null, null), new Masking("s3cret", "***"))
            {
                RunContext = "run facts",
            },
            events: null,
            Token);

        var userMd = Path.Combine(_temp.Path, ".orchestrators", "r1", "agents", "a7", "steps", "s1", "user.md");

        Assert.True(File.Exists(userMd));
        Assert.Equal($"<run-context>{Environment.NewLine}run facts{Environment.NewLine}</run-context>{Environment.NewLine}{Environment.NewLine}<task>{Environment.NewLine}do the *** thing{Environment.NewLine}</task>", File.ReadAllText(userMd));
    }

    /// <summary>The bounds read from the configuration, each at its default where it says none, and one that is not a positive number refuses the file.</summary>
    [Fact]
    public void TheBounds_ReadFromTheConfiguration_EachAtItsDefault()
    {
        Assert.Equal(new AgentContinuationBounds(), AgentContinuationBounds.Of(null));
        Assert.Equal(new AgentContinuationBounds(1, 2, 3, 4, 5), AgentContinuationBounds.Of(new AgentContinuationConfig { RecordChars = 1, StepChars = 2, Paths = 3, LastIterations = 4, IterationChars = 5 }));
        Assert.Equal(new AgentContinuationBounds(9, 16_000, 300, 3, 120_000), AgentContinuationBounds.Of(new AgentContinuationConfig { RecordChars = 9 }));
        Assert.Equal(
            "'agentContext.continuation.paths' is 0, and a bound of the continuation record is a positive number",
            Assert.Single(new AgentContinuationConfig { Paths = 0 }.Problems()));
    }

    /// <summary>The work the record holds, with its steps' records on the disk and the newest attempt's stream written.</summary>
    private (AgentContinuationWork Work, string WorkDirectory) Work()
    {
        var workDirectory = _temp.Combine("work");
        var plan = Directory.CreateDirectory(_temp.Combine("steps", "items[1].plan")).FullName;
        var implement = Directory.CreateDirectory(_temp.Combine("steps", "items[2].implement")).FullName;

        File.WriteAllText(Path.Combine(implement, OrchestratorLayout.StepNotesFileName), "what was done and why");
        File.WriteAllText(Path.Combine(implement, "output.json"), """{"items":[1]}""");
        File.WriteAllText(Path.Combine(implement, OrchestratorLayout.HandoffFileName), "Done: the form.");

        var stream = new AgentEvent[]
        {
            new RunStarted(1),
            new TurnStarted(1),
            new ToolCallStarted("c-9", AgentTools.ViewFile, AgentToolRunner.Harness),
            new AssistantMessage("reading", [new AgentToolCall("c-9", AgentTools.ViewFile)]),
            new ToolCallFinished("c-9", Ok: true, TimeSpan.Zero, """{"path":"a.txt"}""", "alpha", Path.Combine(implement, AgentCallRecords.ToolResultsDirectoryName, "c-9.txt")),
            new TurnStarted(2),
            new AssistantMessage("the work stands", [new AgentToolCall("c-9", AgentTools.ViewFile)]),
        };

        using (var writer = File.CreateText(Path.Combine(implement, StoredStream.FileName)))
        {
            foreach (var @event in stream)
            {
                writer.WriteLine(JsonSerializer.SerializeToNode(@event, typeof(AgentEvent), JsonStateFile.LineOptions)!.ToJsonString(JsonStateFile.LineOptions));
            }
        }

        return (
            new AgentContinuationWork(
                "items[2].implement",
                [new AgentContinuedStep("items[1].plan", "done", plan), new AgentContinuedStep("items[2].implement", "stopped by a pause", implement)],
                ["src/one.cs", "src/two.cs"],
                [new AgentRedLeg("windows", "build", "the ledger's line", _temp.Combine("logs"))],
                CutOff: true),
            workDirectory);
    }

    /// <summary>Renders <paramref name="work"/> through the builder on the real file system.</summary>
    private string Render(AgentContinuationWork work, string workDirectory, Func<AgentContinuationBounds, AgentContinuationBounds>? bounds = null)
    {
        var builder = new AgentContinuation(new PhysicalFileSystem(FilePermissionsFactory.Create()), _harness.Output);

        return builder.Render(work, workDirectory, bounds is null ? new AgentContinuationBounds() : bounds(new AgentContinuationBounds()));
    }

    /// <summary>A client that answers at once, remembering the run it was given.</summary>
    private sealed class Scripted : IAgentClient
    {
        public string Name => "scripted";

        public AgentClientCapabilities Capabilities { get; } = new(
            Streaming: true, Thinking: false, WritableWhileWorking: false, AgentConversationHandling.Handed, SchemaEnforcedOutput: false,
            AgentToolsOwner.Harness, AgentToolsOwner.Harness, ReportsCost: false, ["low"], ProviderType: "none", ToldModelBy: "the test");

        public AgentRun? Given { get; private set; }

        public Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            Given = run;
            return Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Model: run.Model));
        }
    }
}
