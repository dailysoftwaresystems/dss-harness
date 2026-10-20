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

        var itemWithoutIndex = step with { Instance = "one", Item = JsonNode.Parse("""{"a": 1}""") };
        Assert.Contains("an item without its index", itemWithoutIndex.Problem(), StringComparison.Ordinal);

        var owner = new RunOwner(string.Empty, 0, DateTimeOffset.UtcNow);
        Assert.NotNull(owner.Problem());

        var file = new FlowFile(2, "r1", "flow", "digest", new Dictionary<string, string>(), "Running", new FlowBudgetRecord(0, 0, 0), 0, [], ["nope"]);
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
            Assert.Equal(1, log.Append(DateTimeOffset.UtcNow, "sessionBegan").Seq);
            Assert.Equal(2, log.Append(DateTimeOffset.UtcNow, "step", "one", "one", "NotStarted", "Running", 1).Seq);
        }

        using (var again = FlowEventLog.Open(Files(), file))
        {
            Assert.Equal(3, again.Append(DateTimeOffset.UtcNow, "sessionEnded", to: "Done").Seq);
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
            _ = log.Append(DateTimeOffset.UtcNow, "sessionBegan");
        }

        files.WriteAllTextAtomic(file, files.ReadAllText(file) + """{"seq": 2, "at": "2026-10-19 12""");

        var (events, torn) = FlowEventLog.Read(files, file);

        Assert.Single(events);
        Assert.NotNull(torn);
        Assert.Contains("no newline", torn, StringComparison.Ordinal);

        using (var again = FlowEventLog.Open(files, file))
        {
            Assert.Equal(2, again.Append(DateTimeOffset.UtcNow, "resumed").Seq);
        }

        var (whole, _) = FlowEventLog.Read(files, file);

        Assert.Equal([1, 2], whole.Select(read => read.Seq));
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
