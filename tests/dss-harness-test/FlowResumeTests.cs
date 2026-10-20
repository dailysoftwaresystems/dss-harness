using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// Recovery from the state a run left: the index reconciled against every step's own record - the step's own taken
/// where the two differ, what only a step's directory holds adopted - the graph rebuilt over the items and iterations
/// the records name, what a stop stopped running again with its handoff, a crash cut off given one the engine writes,
/// the definition frozen as it ran, the owner claimed and reclaimed, and a resume beside an open question refused only
/// where nothing can run until it is answered.
/// </summary>
public sealed class FlowResumeTests
{
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The run's records in a directory of their own.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FlowRunRecords Records) Records(string run, string directory, WorkflowDefinition definition)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        return (harness, layout, new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of(run, definition)));
    }

    /// <summary>What a run left, taken up: the records read, reconciled, and handed back with the handoffs of its stopped steps.</summary>
    private static FlowResumeState TakeUp(HarnessFactory harness, OrchestratorLayout layout, string? digest = null)
        => new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(digest);

    /// <summary>A run that ends writes its state whole: every instance with its state and what it waits on, next empty, the pause it ended by, and the events in order.</summary>
    [Fact]
    public async Task ARunThatEnds_WritesItsStateWhole()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
                dependsOn: [one]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
        }

        var file = harness.OrchestrationStore.ReadFlow(layout);

        Assert.NotNull(file);
        Assert.Equal("r1", file.Run);
        Assert.Equal("flow", file.Definition);
        Assert.Equal("Done", file.Status);
        Assert.Equal(["one", "two"], file.Steps.Select(step => step.Instance));
        Assert.All(file.Steps, step => Assert.Equal("Done", step.State));
        Assert.Empty(file.Next);
        Assert.Null(file.Pause);

        var (events, torn) = FlowEventLog.Read(harness.FileSystem, layout.EventsFile);

        Assert.Null(torn);
        Assert.Equal("sessionBegan", events[0].Kind);
        Assert.Equal("sessionEnded", events[^1].Kind);
        Assert.Equal("Done", events[^1].To);
    }

    /// <summary>The state a pause left is taken up whole: the stopped step runs again as a new attempt, its handoff read from its own directory and given to the attempt, and the file removed.</summary>
    [Fact]
    public async Task TheStateAPauseLeft_IsTakenUpWhole_TheHandoffGivenAndRemoved()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: after
                type: orient
                dependsOn: [think]
            """);
        var runner = new Runner().Hold("think");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            runner.HandsOff("think", "halfway through the reasoning");
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("think");
            coordinator.RequestPause("command");
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);
        }

        var taken = TakeUp(harness, layout);
        var handoff = layout.StepHandoffFile("think");

        Assert.Equal(["think"], taken.File.Pause!.Handoffs);
        Assert.NotNull(taken.File.Pause.CompletedAt);
        Assert.True(taken.Handoffs.TryGetValue("think", out var account));
        Assert.Equal("halfway through the reasoning", account);

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: taken, records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["think"].State);
            Assert.Equal(2, coordinator.Instances["think"].Attempt);
            Assert.True(harness.FileSystem.KindOf(handoff) == PathKind.None, "the handoff file is removed once the attempt is given it");
        }

        var file = harness.OrchestrationStore.ReadFlow(layout);

        Assert.NotNull(file);
        Assert.Null(file.Pause);
    }

    /// <summary>What a crash cut off is taken up: it runs again as a new attempt, and the engine writes the handoff its agent never gave - for the next attempt is told what the cut-off one had done.</summary>
    [Fact]
    public async Task WhatACrashCutOff_RunsAgainWithAHandoffTheEngineWrites()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: after
                type: orient
                dependsOn: [think]
            """);
        var runner = new Runner().Hold("think");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            _ = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("think");

            // The crash: the engine's process ended, and its records stand as its last whole writes left them.
        }

        var taken = TakeUp(harness, layout);

        Assert.Equal("Running", taken.File.Steps.Single(step => step.Instance == "think").State);

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: taken, records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(2, coordinator.Instances["think"].Attempt);
        }

        Assert.Contains("the engine's process ended", next.Given[2], StringComparison.Ordinal);
    }

    /// <summary>The index is reconciled against the step's own record - written first, so the truth where the two differ - and a step directory the index does not name is adopted.</summary>
    [Fact]
    public async Task TheIndexIsReconciledAgainstTheStepsOwnRecords_AndAdoptsWhatOnlyADirectoryNames()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
                dependsOn: [one]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        // The index is made to say less than the step's own record does, as a crash between the two writes would:
        // the step ended, its own record says so, and the index was never written over it.
        var file = harness.OrchestrationStore.ReadFlow(layout)!;
        var lying = file with
        {
            Steps = [.. file.Steps.Select(step => step.Instance == "two" ? step with { State = "NotStarted", FinishedAt = null } : step)],
        };
        harness.OrchestrationStore.WriteFlow(layout, lying);

        // A step directory the index does not name, holding a record of its own.
        var orphan = layout.StepRecordFile("extra");
        harness.FileSystem.CreateDirectory(Path.GetDirectoryName(orphan)!);
        harness.OrchestrationStore.WriteStepRecord(orphan, new FlowStepRecord("extra", "extra", false, "Done", 1, 1, [], []));

        var taken = TakeUp(harness, layout);

        Assert.Equal(["two"], taken.Differing);
        Assert.Equal(["extra"], taken.Adopted);
        Assert.Equal(["extra", "one", "two"], taken.File.Steps.Select(step => step.Instance));

        var written = harness.OrchestrationStore.ReadFlow(layout);

        Assert.NotNull(written);
        Assert.Equal(["extra", "one", "two"], written.Steps.Select(step => step.Instance));
        Assert.Equal("Done", written.Steps.Single(step => step.Instance == "two").State);
    }

    /// <summary>A config whose entry now differs from the one the run began under is refused with a pointer to a new run.</summary>
    [Fact]
    public async Task AConfigWhoseEntryNowDiffersIsRefusedWithAPointerToANewRun()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        var raised = Assert.Throws<HarnessException>(() => TakeUp(harness, layout, digest: "another digest entirely"));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Contains("has changed since run 'r1' began", raised.Message, StringComparison.Ordinal);
        Assert.Contains("start a new run", raised.Message, StringComparison.Ordinal);
    }

    /// <summary>The graph is rebuilt over the items and iterations the records name: a taken-up forEach holds each item's value, and what a finished item wrote is read by what comes after.</summary>
    [Fact]
    public async Task TheGraphIsRebuiltOverTheItemsTheRecordsName()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: round
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: work
                    type: reason
                    prompt: work.md
                    outputs: work.json
            """);
        var inputs = new Dictionary<string, string>();
        var runner = new Runner()
            .Hold("round[1].work")
            .On("plan", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("""{"list": ["a", "b"]}""")));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), inputs: inputs, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("round[0].work");
            runner.Complete("round[0].work");
            await runner.StartedAsync("round[1].work");

            // The crash, between one item's end and another's.
        }

        var taken = TakeUp(harness, layout);
        var next = new Runner().On("work", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("{\"done\": \"" + run.Item.AsTemplateText() + "\"}")));
        var (_, _, again) = Records("r1", temp.Path, definition);

        string? lastOutput = null;

        using (again)
        {
            var coordinator = new FlowCoordinator(
                definition,
                next,
                new RunnerFacts(),
                inputs: inputs,
                resume: taken,
                records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["round[0].work"].State);
            Assert.Equal(["b"], next.ItemsWorked);
            lastOutput = coordinator.Instances["round[1].work"].Output?["done"]?.GetValue<string>();
        }

        Assert.Equal("b", lastOutput);
    }

    /// <summary>A resume beside an open question is not refused where something can run; it is refused only where nothing can until the question is answered, naming answer-orchestrator.</summary>
    [Fact]
    public async Task AResumeBesideAnOpenQuestionIsRefusedOnlyWhereNothingCanRun()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: asker
                type: reason
                prompt: work.md
                outputs: work.json
              - name: gate
                type: build
            """);
        var runner = new Runner().Asking("asker").Hold("gate");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("asker");
            await runner.StartedAsync("gate");

            // The crash, with the question open and the build still running.
        }

        var taken = TakeUp(harness, layout);
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: taken, records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            // Something beside the question could run - the build the crash cut off - so the resume ran it; the
            // question is still open.
            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["gate"].State);
            Assert.Equal(FlowInstanceState.Paused, coordinator.Instances["asker"].State);
        }

        // Now nothing is left but the question: the resume is refused, naming the way on.
        var besideOnly = Parse("""
            name: flow
            steps:
              - name: asker
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        using var other = new TempDirectory();
        var (harness2, layout2, records2) = Records("r2", other.Path, besideOnly);

        using (records2)
        {
            var coordinator = new FlowCoordinator(besideOnly, new Runner().Asking("asker"), new RunnerFacts(), records: records2);
            var session = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, session.Status);
        }

        var taken2 = TakeUp(harness2, layout2);
        var (_, _, again2) = Records("r2", other.Path, besideOnly);

        using (again2)
        {
            var coordinator = new FlowCoordinator(besideOnly, new Runner(), new RunnerFacts(), resume: taken2, records: again2);

            var raised = await Assert.ThrowsAsync<HarnessException>(() => coordinator.RunAsync(TestContext.Current.CancellationToken));

            Assert.Equal(HarnessExit.Refused, raised.ExitCode);
            Assert.Contains("answer-orchestrator", raised.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>The owner of a run is claimed at the start of a session: a live owner refuses it, a dead one is reclaimed and said, and the run is released where its session leaves it.</summary>
    [Fact]
    public void TheOwnerIsClaimed_ALiveOwnerRefusesAndADeadOneIsReclaimed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var owner = new FlowRunOwner(harness.OrchestrationStore, harness.Identity);

        _ = owner.Claim(layout);

        var held = harness.OrchestrationStore.ReadOwner(layout);

        Assert.NotNull(held);
        Assert.Equal(Environment.ProcessId, held.ProcessId);

        var identity = Substitute.For<IProcessIdentity>();

        // A live owner: another process that still stands.
        identity.CurrentMachine.Returns(held!.Machine);
        identity.IsAlive(Arg.Any<int>(), Arg.Any<string?>()).Returns(true);

        var refused = Assert.Throws<HarnessException>(() => new FlowRunOwner(harness.OrchestrationStore, identity).Claim(layout));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("owned by a live engine", refused.Message, StringComparison.Ordinal);

        // A dead one: reclaimed and said.
        identity.IsAlive(Arg.Any<int>(), Arg.Any<string?>()).Returns(false);
        identity.CurrentId.Returns(99);
        identity.Current.Returns((string?)"stamp");
        var reclaimed = new FlowRunOwner(harness.OrchestrationStore, identity).Claim(layout);

        Assert.NotNull(reclaimed);
        Assert.Contains("reclaimed", reclaimed, StringComparison.Ordinal);
        Assert.Equal(99, harness.OrchestrationStore.ReadOwner(layout)!.ProcessId);

        new FlowRunOwner(harness.OrchestrationStore, identity).Release(layout);

        Assert.Null(harness.OrchestrationStore.ReadOwner(layout));
    }

    /// <summary>A runner that answers each step as its test scripted it, holding open the steps the test wants to time.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _asking = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in order.</summary>
        public List<string> Started { get; } = [];

        /// <summary>The handoff each attempt was given, by attempt number from 1.</summary>
        public Dictionary<int, string?> Given { get; } = new();

        /// <summary>Each item a work step saw, in order.</summary>
        public List<string> ItemsWorked { get; } = [];

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

        public Runner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        public Runner Asking(string step)
        {
            _ = _asking.Add(step);
            return this;
        }

        public void Complete(string instance)
            => Gate(instance).TrySetResult(FlowStepResult.Ended(passed: true, 0));

        public Runner HandsOff(string step, string account)
        {
            _handoffs[step] = account;
            return this;
        }

        private readonly Dictionary<string, string?> _handoffs = new(StringComparer.Ordinal);

        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (_starts)
            {
                Started.Add(run.Instance);
                StartOf(run.Instance).TrySetResult();
                Given[run.Attempt] = run.Handoff;

                if (run.Step.Name == "work")
                {
                    ItemsWorked.Add(run.Item.AsTemplateText());
                }
            }

            if (_asking.Contains(run.Step.Name))
            {
                return Task.FromResult(FlowStepResult.AtLimit("turns"));
            }

            if (_scripts.TryGetValue(run.Step.Name, out var script))
            {
                return Task.FromResult(script(run));
            }

            if (_gates.TryGetValue(run.Instance, out var gate) || _gates.TryGetValue(run.Step.Name, out gate))
            {
                return gate.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(FlowStepResult.Ended(passed: true, 0));
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
}
