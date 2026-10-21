using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The run's records on disk: the strict records and what they refuse, the event log's sequence and its torn last
/// line, each step's directory where the rule puts it, the handoff written in the stopped step's directory and removed
/// once the next attempt is given it, and the state files written before the events that tell of them.
/// </summary>
public sealed class FlowRecordsTests
{
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The run's records, made whole in <paramref name="directory"/> under run <paramref name="run"/>, watched in the order they are written.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, WatchedFileSystem Watched, FlowRunRecords Records) Records(
        string run,
        string directory,
        WorkflowDefinition definition,
        ManualClock clock)
    {
        var harness = new HarnessFactory();
        var watched = new WatchedFileSystem(harness.FileSystem);
        var store = new OrchestrationStore(watched, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        return (harness, layout, watched, new FlowRunRecords(watched, store, layout, FlowRunContext.Of(run, definition, startedAt: clock.GetUtcNow()), clock));
    }

    /// <summary>The file system of these tests' own.</summary>
    private static DssHarness.Core.FileSystem.IFileSystem Files()
    {
        var harness = new HarnessFactory();
        return harness.FileSystem;
    }

    /// <summary>A record that is not one is refused, named with why - a record is never guessed at.</summary>
    [Fact]
    public void ARecordThatIsNotOneIsRefused()
    {
        var step = new FlowStepRecord("not an id", "one", false, "Done", 1, 1, [], []);
        Assert.Contains("is not an instance id", step.Problem(), StringComparison.Ordinal);

        var badState = step with { Instance = "one", State = "Finished" };
        Assert.Contains("is not an instance state", badState.Problem(), StringComparison.Ordinal);

        var badFailure = step with { Instance = "one", State = "Failed", Failure = "ignored" };
        Assert.Contains("is not where a failure leaves the run", badFailure.Problem(), StringComparison.Ordinal);

        Assert.Equal("'None' is not a cause an ending is recorded with", (step with { Instance = "one", Cause = "None" }).Problem());
        Assert.Equal("'lockHeld' is not a cause an ending is recorded with", (step with { Instance = "one", Cause = "lockHeld" }).Problem());
        Assert.Null((step with { Instance = "one", State = "Failed", Cause = "LockHeld" }).Problem());

        var itemWithoutIndex = step with { Instance = "one", Item = JsonNode.Parse("""{"a": 1}""") };
        Assert.Contains("an item without its index", itemWithoutIndex.Problem(), StringComparison.Ordinal);

        var owner = new RunOwner(string.Empty, 0, DateTimeOffset.UtcNow);
        Assert.NotNull(owner.Problem());

        var file = new FlowFile(2, "r1", "flow", "digest", new Dictionary<string, string>(), "Running", new FlowBudgetRecord(0, 0, 0), 0, [], ["nope"], DateTimeOffset.UnixEpoch);
        Assert.Contains("its schema is 2", file.Problem(), StringComparison.Ordinal);
        Assert.Contains("next names an instance the file does not hold", (file with { Schema = 1 }).Problem(), StringComparison.Ordinal);
    }

    /// <summary>The event log's events are numbered from 1, each whole on the disk before the next, and a reopened log goes on from its last whole line's seq.</summary>
    [Fact]
    public void EventsAreNumberedFrom1_WholeOnTheDisk_AndAReopenedLogGoesOn()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("events.jsonl");

        using (var log = FlowEventLog.Open(Files(), file))
        {
            Assert.Equal(1, log.Append(DateTimeOffset.UtcNow, FlowChangeKind.SessionBegan).Seq);
            Assert.Equal(2, log.Append(DateTimeOffset.UtcNow, FlowChangeKind.Step, "one", "one", "NotStarted", "Running", 1).Seq);
        }

        using (var again = FlowEventLog.Open(Files(), file))
        {
            Assert.Equal(3, again.Append(DateTimeOffset.UtcNow, FlowChangeKind.SessionEnded, to: "Done").Seq);
        }

        var (events, torn) = FlowEventLog.Read(Files(), file);

        Assert.Null(torn);
        Assert.Equal([1, 2, 3], events.Select(read => read.Seq));
        Assert.Equal(["sessionBegan", "step", "sessionEnded"], events.Select(read => read.Kind));
        Assert.Equal("one", events[1].Instance);
        Assert.Equal("NotStarted", events[1].From);
        Assert.Equal("Running", events[1].To);
    }

    /// <summary>A last line with no newline is the crash's own: the reader ignores it and says so, and the next writer takes it back.</summary>
    [Fact]
    public void ATornLastLineIsIgnoredAndSaid_AndTakenBackByTheNextWriter()
    {
        using var temp = new TempDirectory();
        var files = Files();
        var file = temp.Combine("events.jsonl");

        using (var log = FlowEventLog.Open(files, file))
        {
            _ = log.Append(DateTimeOffset.UtcNow, FlowChangeKind.SessionBegan);
        }

        files.WriteAllTextAtomic(file, files.ReadAllText(file) + """{"seq": 2, "at": "2026-10-19 12""");

        var (events, torn) = FlowEventLog.Read(files, file);

        Assert.Single(events);
        Assert.NotNull(torn);
        Assert.Contains("no newline", torn, StringComparison.Ordinal);

        using (var again = FlowEventLog.Open(files, file))
        {
            Assert.Equal(2, again.Append(DateTimeOffset.UtcNow, FlowChangeKind.Resumed).Seq);
        }

        var (whole, _) = FlowEventLog.Read(files, file);

        Assert.Equal([1, 2], whole.Select(read => read.Seq));
    }

    /// <summary>
    /// The engine's own values are held to the engine's own names, exactly: a number, two names joined and a name the
    /// engine does not have are each refused - which a parse of the enumeration would let through - and a question's id
    /// is held to the one rule of its shape.
    /// </summary>
    [Theory]
    [InlineData("7")]
    [InlineData("99")]
    [InlineData("Done, Failed")]
    [InlineData("done")]
    public void ANameTheEngineDoesNotHaveIsRefused_NeverParsed(string name)
    {
        var step = new FlowStepRecord("one", "one", false, name, 1, 1, [], []);
        var file = new FlowFile(1, "r1", "flow", "digest", new Dictionary<string, string>(), name, new FlowBudgetRecord(0, 0, 0), 0, [], [], DateTimeOffset.UnixEpoch);

        Assert.Equal($"'{name}' is not an instance state", step.Problem());
        Assert.Equal($"'{name}' is not a run status", file.Problem());
        Assert.False(FlowRecordNames.Is<FlowInstanceState>(name));
        Assert.Throws<InvalidOperationException>(() => FlowRecordNames.Of<FlowInstanceState>(name));
    }

    /// <summary>A question's id a record names is held to the one rule of its shape, not to its first letter.</summary>
    [Fact]
    public void AQuestionsIdIsHeldToItsOneRule()
    {
        var step = new FlowStepRecord("one", "one", false, "Paused", 1, 1, [], []);

        Assert.Equal("'qx' is not a question's id", (step with { Question = "qx" }).Problem());
        Assert.Equal("'not an id' names neither an instance nor a question", (step with { HandledBy = "not an id" }).Problem());
        Assert.Null((step with { Question = "q12", HandledBy = "q3" }).Problem());
    }

    /// <summary>Every kind of event is the engine's own kind of change, by its camelCase name - one list, and a kind that is not one is never written.</summary>
    [Fact]
    public void EveryKindOfEventIsAKindOfChange_ByItsName()
    {
        Assert.Equal(["sessionBegan", "resumed", "step", "pauseBegan", "handoff", "pauseCompleted", "sessionEnded", "answer"], FlowEvent.Kinds);
        Assert.Throws<ArgumentOutOfRangeException>(() => FlowEvent.KindOf((FlowChangeKind)99));
    }

    /// <summary>The records nested in a record are checked with it: a pause asked by nobody the engine knows, a redirect to nowhere, and an engine's handoff that says no why are refused.</summary>
    [Fact]
    public void TheRecordsNestedInARecordAreCheckedWithIt()
    {
        var file = new FlowFile(1, "r1", "flow", "digest", new Dictionary<string, string>(), "Paused", new FlowBudgetRecord(0, 0, 0), 0, [], [], DateTimeOffset.UnixEpoch);
        var step = new FlowStepRecord("one", "one", false, "Stopped", 1, 1, [], []);

        Assert.Equal("its pause: 'someone' is not who asks for a pause", (file with { Pause = new FlowPauseRecord("someone", false, DateTimeOffset.UnixEpoch, []) }).Problem());
        Assert.Equal("its redirects: it names no step to go to", (file with { Redirects = [new FlowRedirectRecord("one", "two", ["one"]), new FlowRedirectRecord("one", " ", [])] }).Problem());
        Assert.Equal("its handoff: a handoff the engine wrote says why", (step with { Handoff = new FlowHandoffRecord("engine") }).Problem());
        Assert.Null((step with { Handoff = new FlowHandoffRecord("agent") }).Problem());
    }

    /// <summary>
    /// Every time a run's records hold is written in the one form, and one that has not come yet is written null, never
    /// left out: a step's record, the run's state and its pause have the same members whatever has happened.
    /// </summary>
    [Fact]
    public void EveryTimeIsWrittenInTheOneForm_AndOneNotYetComeIsWrittenNull()
    {
        var at = new DateTimeOffset(2026, 10, 19, 12, 30, 45, TimeSpan.Zero);
        var step = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new FlowStepRecord("one", "one", false, "NotStarted", 1, 1, [], []), JsonStateFile.Options))!.AsObject();
        var file = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            new FlowFile(1, "r1", "flow", "digest", new Dictionary<string, string>(), "Paused", new FlowBudgetRecord(0, 0, 0), 0, [], [], at, Pause: new FlowPauseRecord("command", false, at, [])),
            JsonStateFile.Options))!.AsObject();

        Assert.Contains("startedAt", step.Select(member => member.Key));
        Assert.Contains("finishedAt", step.Select(member => member.Key));
        Assert.Null(step["startedAt"]);
        Assert.Null(step["finishedAt"]);
        Assert.Equal("2026-10-19 12:30:45", file["startedAt"]!.GetValue<string>());
        Assert.Equal("2026-10-19 12:30:45", file["pause"]!["at"]!.GetValue<string>());
        Assert.Contains("completedAt", file["pause"]!.AsObject().Select(member => member.Key));
        Assert.Null(file["pause"]!["completedAt"]);
        Assert.Equal("2026-10-19 12:30:45", FlowTimeConverter.Written(at.ToOffset(TimeSpan.FromHours(-3))));
    }

    /// <summary>A whole line of the log that is not an event is refused by its number, never skipped: history is never guessed at.</summary>
    [Fact]
    public void ACorruptWholeLineIsRefusedByItsNumber_NeverSkipped()
    {
        using var temp = new TempDirectory();
        var files = Files();
        var file = temp.Combine("events.jsonl");

        using (var log = FlowEventLog.Open(files, file))
        {
            _ = log.Append(DateTimeOffset.UtcNow, FlowChangeKind.SessionBegan);
        }

        files.WriteAllTextAtomic(file, files.ReadAllText(file) + "not an event\n" + """{"seq": 3, "at": "2026-10-19 12:00:00", "kind": "resumed"}""" + "\n");

        var corrupt = Assert.Throws<DssHarness.Core.Results.HarnessException>(() => FlowEventLog.Read(files, file));

        Assert.Equal(DssHarness.Core.Results.HarnessExit.Refused, corrupt.ExitCode);
        Assert.StartsWith($"line 2 of '{file}', the run's events, does not read as its next event: ", corrupt.Message, StringComparison.Ordinal);
        Assert.Throws<DssHarness.Core.Results.HarnessException>(() => FlowEventLog.Open(files, file));
    }

    /// <summary>A whole line whose seq is not the one after the line before it is refused by its number: a reopened log never numbers from an earlier event.</summary>
    [Fact]
    public void ALineNotNumberedOneAfterTheLastIsRefused()
    {
        using var temp = new TempDirectory();
        var files = Files();
        var file = temp.Combine("events.jsonl");

        files.WriteAllTextAtomic(file, """{"seq": 1, "at": "2026-10-19 12:00:00", "kind": "sessionBegan"}""" + "\n" + """{"seq": 3, "at": "2026-10-19 12:00:01", "kind": "resumed"}""" + "\n");

        var refused = Assert.Throws<DssHarness.Core.Results.HarnessException>(() => FlowEventLog.Read(files, file));

        Assert.Equal(
            $"line 2 of '{file}', the run's events, does not read as its next event: its seq is 3, and the event before it was 1. Nothing was changed: an event is never guessed at, "
            + "and the run's state files - not its events - are what a resume reads; move the file aside to go on with a log of its own.",
            refused.Message);
    }

    /// <summary>A last line with no newline is set aside even where it holds a whole event: it was never durable, so its seq is never taken, and the next writer numbers from the last whole line.</summary>
    [Fact]
    public void ATornLastLineThatIsWholeJson_IsSetAside_AndItsSeqNeverTaken()
    {
        using var temp = new TempDirectory();
        var files = Files();
        var file = temp.Combine("events.jsonl");

        using (var log = FlowEventLog.Open(files, file))
        {
            _ = log.Append(DateTimeOffset.UtcNow, FlowChangeKind.SessionBegan);
        }

        files.WriteAllTextAtomic(file, files.ReadAllText(file) + """{"seq": 7, "at": "2026-10-19 12:00:00", "kind": "resumed"}""");

        var (events, torn) = FlowEventLog.Read(files, file);

        Assert.Equal([1], events.Select(read => read.Seq));
        Assert.Equal("""the log's last line has no newline and was set aside: '{"seq": 7, "at": "2026-10-19 12:00:00", "kind": "resumed"}'""", torn);

        using (var again = FlowEventLog.Open(files, file))
        {
            Assert.Equal(2, again.Append(DateTimeOffset.UtcNow, FlowChangeKind.Resumed).Seq);
        }
    }

    /// <summary>Each step's directory is where the rule puts it: a step that names an agent with in or of inside that agent's, the create-agent that made it under the run, and every step that names none under the run.</summary>
    [Fact]
    public async Task EachStepsDirectoryIsWhereTheRulePutsIt()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: worker.md
              - name: work
                type: build
                in: make
                dependsOn: [make]
              - name: plain
                type: orient
                dependsOn: [work]
            """);
        var runner = new Runner().On("make", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("""{"agent": "a1"}""")));
        var (harness, layout, _, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(harness.FileSystem.KindOf(layout.StepRecordFile("make")) == PathKind.File);
        Assert.True(harness.FileSystem.KindOf(layout.AgentStepRecordFile("a1", "work")) == PathKind.File);
        Assert.True(harness.FileSystem.KindOf(layout.StepRecordFile("plain")) == PathKind.File);

        var made = harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("make"));
        var work = harness.OrchestrationStore.ReadStepRecord(layout.AgentStepRecordFile("a1", "work"));

        Assert.NotNull(made);
        Assert.NotNull(work);
        Assert.Equal("a1", made.Made);
        Assert.Equal("a1", work.Agent);
    }

    /// <summary>The handoff a safe pause writes is a file in the stopped step's own directory, and it is removed once the next attempt is given it.</summary>
    [Fact]
    public async Task TheHandoffIsAFileInTheStoppedStepsDirectory_RemovedOnceGiven()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new Runner().Hold("think");
        var (harness, layout, _, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            runner.HandsOff("think", "what the agent said it was doing");
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("think");
            coordinator.RequestPause("interrupt");
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);

            var handoff = layout.StepHandoffFile("think");

            Assert.True(harness.FileSystem.KindOf(handoff) == PathKind.File);
            Assert.Equal("what the agent said it was doing\n", harness.FileSystem.ReadAllText(handoff));

            // The attempt the resume gives the handoff to may complete what the step was doing.
            runner.Complete("think");

            var again = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, again.Status);
            Assert.Equal("what the agent said it was doing", runner.Given[2]);
            Assert.Null(runner.Given[1]);
            Assert.True(harness.FileSystem.KindOf(handoff) == PathKind.None);
        }
    }

    /// <summary>The state files are written before the events that tell of them: each event's step record, the run's state and its status are on the disk by the time the event is.</summary>
    [Fact]
    public async Task TheStateFilesAreWrittenBeforeTheEventsThatTellOfThem()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var runner = new Runner();
        var (harness, layout, watched, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        // The step's own record, the run's state and its status each precede the event log's lines; the log is held
        // open by its one writer, so the order is watched at the write of each state file beside the event's seq.
        var flow = watched.Writes.FindIndex(path => Path.GetFileName(path) == "flow.json");
        var status = watched.Writes.FindIndex(path => Path.GetFileName(path) == "workflow.json");

        Assert.True(flow >= 0, "flow.json was written");
        Assert.True(status > flow, "workflow.json is written after flow.json");
        Assert.True(watched.Writes.Any(path => Path.GetFileName(path) == "step.json"), "the step's own record was written");
        Assert.True(harness.FileSystem.KindOf(layout.EventsFile) == PathKind.File);

        var (events, _) = FlowEventLog.Read(harness.FileSystem, layout.EventsFile);

        Assert.Equal("sessionBegan", events[0].Kind);
        Assert.Equal("Done", events[^1].To);
        Assert.Equal("one", events.First(read => read.Kind == "step" && read.To == "Done").Instance);
    }

    /// <summary>
    /// A handoff's file is written before the record that names it, so a record never says a step holds a handoff its
    /// directory does not - and a crash between the two leaves the file for the next resume to read back.
    /// </summary>
    [Fact]
    public void AHandoffsFileIsWrittenBeforeTheRecordThatNamesIt()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var (_, layout, watched, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            IFlowRecords writer = records;
            writer.Write(FlowChangeKind.Handoff, Snapshot([StepOf("think", FlowInstanceState.Stopped, new FlowHandoff("what it was doing", "agent", null))]), "think");
        }

        var handoff = watched.Writes.IndexOf(layout.StepHandoffFile("think"));
        var record = watched.Writes.IndexOf(layout.StepRecordFile("think"));
        var flow = watched.Writes.LastIndexOf(layout.FlowFile);

        Assert.NotEqual(-1, handoff);
        Assert.Equal(handoff + 1, record);
        Assert.True(flow > record, "the run's state is written after the step's own files");
    }

    /// <summary>The machine steps a run's state holds running are its steps running that are no group's own record: the one rule every command reads.</summary>
    [Fact]
    public void TheMachineStepsAStateHoldsRunning_AreItsRunningStepsThatAreNoGroup()
    {
        var flow = new FlowFile(
            1, "r1", "flow", "digest", new Dictionary<string, string>(), "Paused", new FlowBudgetRecord(0, 0, 0), 0,
            [
                new FlowStepRecord("round", "round", true, "Running", 1, 1, [], []),
                new FlowStepRecord("round.build", "round.build", false, "Running", 1, 1, [], [], "build"),
                new FlowStepRecord("think", "think", false, "Stopped", 1, 1, [], [], "reason"),
                new FlowStepRecord("test", "test", false, "Running", 1, 1, [], [], "test"),
            ],
            [],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(["round.build", "test"], flow.MachineStepsRunning());
    }

    /// <summary>
    /// A handoff the engine writes says so and why in its first line, and what its text reads back as is the record it
    /// was written with - why whole, whatever breaks or closing stop it was given with, and what it holds after.
    /// </summary>
    [Theory]
    [InlineData("the run was stopped", "Attempt 1 was cut off.", "Written by the engine: the run was stopped.\nAttempt 1 was cut off.", "the run was stopped")]
    [InlineData("the agent's handoff turn failed: the client ended.\r\nexit 3.", null, "Written by the engine: the agent's handoff turn failed: the client ended. exit 3.", "the agent's handoff turn failed: the client ended. exit 3")]
    public void AHandoffTheEngineWrites_ReadsBackAsTheRecordItWasWrittenWith(string why, string? body, string text, string said)
    {
        var handoff = FlowHandoff.ByEngine(why, body);

        Assert.Equal(new FlowHandoff(text, "engine", said), handoff);
        Assert.Equal(new FlowHandoffRecord("engine", said), FlowHandoffRecord.Of(handoff.Text));
        Assert.Equal(new FlowHandoffRecord("agent"), FlowHandoffRecord.Of("Written by the agent: what it did"));
    }

    /// <summary>The issues a change lists are written after the run's state and its status and before the event that tells of it: the order of every change of a run.</summary>
    [Fact]
    public void TheIssuesAChangeListsAreWrittenAfterTheStatus_BeforeTheEvent()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: triage
                type: orient
            """);
        var (harness, layout, watched, records) = Records("r1", temp.Path, definition, clock);
        var issue = new PreExistingIssue("pe-1", "a broken test", "it fails on the base commit", ["tests/a.c"], new PreExistingProof("ran", "abc123", Command: "make test", Output: "1 failed"), "in no step's way", "triage", clock.GetUtcNow());

        using (records)
        {
            IFlowRecords writer = records;
            var before = watched.Writes.Count;
            writer.Write(FlowChangeKind.Step, Snapshot([StepOf("triage", FlowInstanceState.Done)]) with { Issues = [issue] }, "triage", "Running", "Done");

            var written = watched.Writes.Skip(before).Select(Path.GetFileName).ToList();

            Assert.Equal(["step.json", "flow.json", "workflow.json", "pre-existing-issues.json"], written);
        }

        var (events, _) = FlowEventLog.Read(harness.FileSystem, layout.EventsFile);

        Assert.Equal("triage", Assert.Single(events).Instance);
        Assert.Equal(["pe-1"], harness.OrchestrationStore.ReadPreExistingIssues(layout)!.Issues.Select(listed => listed.Id));
    }

    /// <summary>A snapshot of a run holding <paramref name="steps"/>, as the coordinator hands one to its records.</summary>
    private static FlowSnapshot Snapshot(IReadOnlyList<FlowSnapshotStep> steps)
        => new(FlowRunStatus.Running, null, new FlowBudgetSpent(), 0, null, steps, [], []);

    /// <summary>One step of a snapshot, as little as a record of it needs.</summary>
    private static FlowSnapshotStep StepOf(string instance, FlowInstanceState state, FlowHandoff? handoff = null)
        => new(instance, instance, "orient", false, state, 1, 1, null, null, null, null, null, null, null, null, null, null, null, handoff, null, FlowValue.Missing, null, null, [], []);

    /// <summary>What a run is, is frozen: the definition as it ran is written once, read back whole, and the digest of it names it.</summary>
    [Fact]
    public async Task TheDefinitionIsFrozenAsItRan()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var (harness, layout, _, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), clock, records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        var frozen = harness.OrchestrationStore.ReadDefinition(layout);

        Assert.NotNull(frozen);
        Assert.Equal("flow", frozen.Name);
        Assert.Equal(FlowRunContext.Of("r1", definition).DefinitionDigest, FlowRunContext.DigestOf(System.Text.Json.JsonSerializer.Serialize(frozen, JsonStateFile.Options)));
    }

    /// <summary>A runner that answers each step as its test scripted it, holding open the steps the test wants to time and recording the handoffs its attempts are given.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _handoffs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>The handoff each attempt was given, by attempt number from 1.</summary>
        public Dictionary<int, string?> Given { get; } = new();

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run.</summary>
        public Runner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        public Task StartedAsync(string instance)
            => StartOf(instance).Task;

        private TaskCompletionSource StartOf(string instance)
        {
            lock (_starts)
            {
                if (!_starts.TryGetValue(instance, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _starts.Add(instance, started);
                }

                return started;
            }
        }

        private TaskCompletionSource<FlowStepResult> Gate(string instance)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(instance, out var gate))
                {
                    gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates.Add(instance, gate);
                }

                return gate;
            }
        }

        public Runner Hold(string step)
        {
            _ = Gate(step);
            return this;
        }

        public void Complete(string instance)
            => Gate(instance).TrySetResult(FlowStepResult.Ended(passed: true, 0));

        public Runner HandsOff(string step, string? account)
        {
            lock (_handoffs)
            {
                _handoffs[step] = account;
            }

            return this;
        }

        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (_starts)
            {
                Started.Add(run.Instance);
                StartOf(run.Instance).TrySetResult();
                Given[run.Attempt] = run.Handoff;
            }

            if (_scripts.TryGetValue(run.Step.Name, out var script))
            {
                return script(run);
            }

            if (_gates.TryGetValue(run.Instance, out var gate) || _gates.TryGetValue(run.Step.Name, out gate))
            {
                return await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return FlowStepResult.Ended(passed: true, 0);
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (_handoffs)
            {
                return Task.FromResult(_handoffs.TryGetValue(run.Step.Name, out var account) ? account : null);
            }
        }
    }

    /// <summary>The facts every run of these tests asks of its runners.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        public bool WritesTree(string runner) => false;
    }

    /// <summary>Watches the order the run's files are written in, as the law of the records is pinned by it.</summary>
    private sealed class WatchedFileSystem(DssHarness.Core.FileSystem.IFileSystem inner) : PassThroughFileSystem(inner)
    {
        /// <summary>Each file written atomically, in the order they were written.</summary>
        public List<string> Writes { get; } = [];

        public override void WriteAllTextAtomic(string path, string contents)
        {
            base.WriteAllTextAtomic(path, contents);
            Writes.Add(path);
        }
    }
}
