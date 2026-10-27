using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The run's status, <c>workflow.json</c>: written by one writer alone, atomically, at every change of a step's state,
/// the run's status or a recovery - every step of the definition from the start, its instances, the statuses as the
/// engine's identifiers, UTC times in the one form with null kept, seconds, progress and waitingOn - and rewritten by
/// a resume before it goes on.
/// </summary>
public sealed class WorkflowStatusTests
{
    /// <summary>The one form a time is written in, and the one a test reads.</summary>
    private static readonly Regex SqlTime = new("""^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$""", RegexOptions.Compiled);

    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The run's records, made whole in <paramref name="directory"/> under run <paramref name="run"/>.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FlowRunRecords Records) Records(string run, string directory, WorkflowDefinition definition, ManualClock clock)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        var context = FlowRunContext.Of(run, definition, startedAt: clock.GetUtcNow());
        return (harness, layout, new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, context, clock));
    }

    /// <summary>A run's status is created with the run: every step of the definition from the start, NotStarted, its times null and never left out, and nothing else in it.</summary>
    [Fact]
    public async Task AStatusIsCreatedWithTheRun_EveryStepOfTheDefinitionFromTheStart()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
                dependsOn: [one]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new DoneRunner(), new RunnerFacts(), clock, records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        var status = harness.OrchestrationStore.ReadWorkflowStatus(layout);

        Assert.NotNull(status);
        Assert.Equal(1, status.Schema);
        Assert.Equal("r1", status.Run);
        Assert.Equal("flow", status.Workflow);
        Assert.Equal("Done", status.Status);
        Assert.Matches(SqlTime, status.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        Assert.Matches(SqlTime, status.FinishedAt!.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        Assert.Equal(["one", "two"], status.Steps.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("Done", status.Steps["one"].Status);
        Assert.Equal("Done", status.Steps["two"].Status);
        Assert.Equal(["one"], status.Steps["one"].Instances.Keys);
        Assert.Equal(1, status.Steps["one"].Instances["one"].Attempt);
        Assert.Null(status.WaitingOn);
    }

    /// <summary>Every step of the definition is in the status before anything runs, NotStarted, with no instance and its times null - and the file is written before the first step starts.</summary>
    [Fact]
    public async Task EveryStepIsInTheStatusFromTheStart_NotStartedUntilOneOfItsInstancesRuns()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: orient
              - name: work
                type: reason
                prompt: work.md
                outputs: work.json
                dependsOn: [gate]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);
        var runner = new HeldRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("gate");

            var before = harness.OrchestrationStore.ReadWorkflowStatus(layout);

            Assert.NotNull(before);
            Assert.Equal("Running", before.Status);
            Assert.Equal("Running", before.Steps["gate"].Status);
            Assert.Equal("NotStarted", before.Steps["work"].Status);
            Assert.Empty(before.Steps["work"].Instances);
            Assert.Null(before.Steps["work"].StartedAt);
            Assert.Null(before.Steps["work"].FinishedAt);
            Assert.Null(before.Steps["work"].Seconds);
            Assert.Equal(1, before.Progress.Steps);
            Assert.Equal(0, before.Progress.NotStarted);
            Assert.Equal(1, before.Progress.Running);

            runner.Complete("gate");
            runner.Complete("work");
            await session;
        }
    }

    /// <summary>A step that runs more than once holds each instance under its id, and its own status and times are its instances' together.</summary>
    [Fact]
    public async Task AStepThatRunsMoreThanOnce_HoldsEachInstanceUnderItsId()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: round
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: check
                    type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var coordinator = new FlowCoordinator(
                definition,
                new DoneRunner().On("plan", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("""{"list": ["a", "b"]}"""))),
                new RunnerFacts(),
                clock,
                records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        var status = harness.OrchestrationStore.ReadWorkflowStatus(layout);

        Assert.NotNull(status);
        Assert.Equal("Done", status.Steps["round.check"].Status);
        Assert.Equal(["round[0].check", "round[1].check"], status.Steps["round.check"].Instances.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(6, status.Progress.Steps);
        Assert.Equal(6, status.Progress.Done);
        Assert.Null(status.WaitingOn);
    }

    /// <summary>What the run waits on is named: the open questions' ids, and the safe pause while it records; null where it waits on nothing.</summary>
    [Fact]
    public async Task WhatTheRunWaitsOn_IsNamed_QuestionsAndThePause()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);
        var runner = new HeldRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("gate");
            coordinator.RequestPause("interrupt");
            runner.Complete("gate");
            await session;

            var paused = harness.OrchestrationStore.ReadWorkflowStatus(layout);

            Assert.NotNull(paused);
            Assert.Equal("Paused", paused.Status);
            Assert.NotNull(paused.WaitingOn);
            Assert.Contains("safe pause (interrupt)", paused.WaitingOn);

            var second = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, second.Status);
            Assert.Null(harness.OrchestrationStore.ReadWorkflowStatus(layout)!.WaitingOn);
        }
    }

    /// <summary>The status says nothing it must not: no prompt, no output, no path outside the run and no secret - what it says is safe to show to anyone who may see the run.</summary>
    [Fact]
    public async Task TheStatusHoldsNoPromptOutputPathOrSecret()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);
        var runner = new DoneRunner().On("plan", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("""{"secret": "sk-live-123456", "prompt": "the full prompt text"}""")));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        var text = harness.FileSystem.ReadAllText(layout.WorkflowStatusFile);

        Assert.DoesNotContain("sk-live-123456", text, StringComparison.Ordinal);
        Assert.DoesNotContain("the full prompt text", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"output\"", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A failure the recovery is at work on shows Recovering - its instance with the visit at work, and the visits it was
    /// given - and the progress counts it; once the recovery is done the failure shows Failed again, its visits kept. An
    /// instance still running has no seconds yet, and a step none of whose instances has finished has none either.
    /// </summary>
    [Fact]
    public async Task AFailureTheRecoveryIsAtWorkOn_ShowsRecovering_AndARunningInstanceHasNoSecondsYet()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              maxVisits: 2
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);
        var runner = new HeldRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            await runner.StartedAsync("build").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            runner.Fail("build");
            await runner.StartedAsync("recover.mend").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            var recovering = harness.OrchestrationStore.ReadWorkflowStatus(layout)!;

            Assert.Equal("Recovering", recovering.Steps["build"].Status);
            Assert.Equal(new WorkflowInstanceStatus("Recovering", 1, 1, 1, clock.GetUtcNow(), clock.GetUtcNow(), 0, new WorkflowRecovery(1, 2, "fix")), recovering.Steps["build"].Instances["build"]);
            Assert.Equal(1, recovering.Progress.Recovering);
            Assert.Equal(1, recovering.Progress.Failed);
            Assert.Equal("Running", recovering.Steps["recover.mend"].Instances["recover.mend"].Status);
            Assert.Null(recovering.Steps["recover.mend"].Instances["recover.mend"].Seconds);
            Assert.Null(recovering.Steps["recover.mend"].Seconds);

            runner.Complete("recover.mend");
            await runner.StartedAsync("build+2").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            runner.Complete("build+2");
            await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        var recovered = harness.OrchestrationStore.ReadWorkflowStatus(layout)!;

        Assert.Equal(new WorkflowInstanceStatus("Failed", 1, 1, 1, clock.GetUtcNow(), clock.GetUtcNow(), 0), recovered.Steps["build"].Instances["build"]);
        Assert.Equal("Done", recovered.Steps["build"].Instances["build+2"].Status);
        Assert.Equal(0, recovered.Progress.Recovering);
    }

    /// <summary>A resume rewrites the status from the run's state and its step records before it goes on, so after a crash it is never behind.</summary>
    [Fact]
    public async Task AResumeRewritesTheStatusBeforeItGoesOn()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
              - name: after
                type: orient
                dependsOn: [gate]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);
        var runner = new HeldRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("gate");

            // The crash: the session never ends, and the records stop where they stand - the last whole writes.
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));

        var (_, _, again) = Records("r1", temp.Path, definition, clock);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new DoneRunner(), new RunnerFacts(), clock, resume: taken, records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
        }

        var status = harness.OrchestrationStore.ReadWorkflowStatus(layout);

        Assert.NotNull(status);
        Assert.Equal("Done", status.Status);
        Assert.Equal("Done", status.Steps["gate"].Status);
        Assert.Equal(2, status.Steps["gate"].Instances["gate"].Attempt);
    }

    /// <summary>
    /// A step whose only instance was drawn ended - skipped where its when read false, or not run where what it waits on
    /// failed - has finished: its time is when it was drawn, never left null as though it had not ended.
    /// </summary>
    [Fact]
    public async Task AStepDrawnEnded_HasFinished()
    {
        using var temp = new TempDirectory();
        var clock = new ManualClock();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
                when: 'false'
                dependsOn: [one]
              - name: three
                type: orient
              - name: four
                type: orient
                dependsOn: [three]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);
        var runner = new DoneRunner().On("three", _ => FlowStepResult.Ended(passed: false, DssHarness.Core.Results.HarnessExit.CommandFailed, error: "no"));

        using (records)
        {
            await new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records).RunAsync(TestContext.Current.CancellationToken);
        }

        var status = harness.OrchestrationStore.ReadWorkflowStatus(layout)!;

        Assert.Equal(("Skipped", "NotRun"), (status.Steps["two"].Status, status.Steps["four"].Status));
        Assert.All(new[] { status.Steps["two"], status.Steps["four"] }, step => Assert.NotNull(step.FinishedAt));
        Assert.Equal(status.Steps["two"].FinishedAt, status.Steps["two"].Instances["two"].FinishedAt);
        Assert.Equal(status.Steps["four"].FinishedAt, status.Steps["four"].Instances["four"].FinishedAt);
    }

    /// <summary>A runner that answers every step as asked, holding open any step whose test wants to time it.</summary>
    private sealed class DoneRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);

        public List<string> Started { get; } = [];

        public DoneRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add(run.Instance);
            return Task.FromResult(_scripts.TryGetValue(run.Step.Name, out var script) ? script(run) : FlowStepResult.Ended(passed: true, 0));
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>A runner that holds every step open until its test completes it.</summary>
    private sealed class HeldRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);

        public Task StartedAsync(string instance)
        {
            lock (_starts)
            {
                if (!_starts.TryGetValue(instance, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _starts.Add(instance, started);
                }

                return started.Task;
            }
        }

        public void Complete(string instance)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(instance, out var gate))
                {
                    gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates.Add(instance, gate);
                }

                gate.TrySetResult(FlowStepResult.Ended(passed: true, 0));
            }
        }

        public void Fail(string instance)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(instance, out var gate))
                {
                    gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates.Add(instance, gate);
                }

                gate.TrySetResult(FlowStepResult.Ended(passed: false, DssHarness.Core.Results.HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
            }
        }

        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (_starts)
            {
                if (!_starts.TryGetValue(run.Instance, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _starts.Add(run.Instance, started);
                }

                started.TrySetResult();
            }

            lock (_gates)
            {
                if (!_gates.TryGetValue(run.Instance, out var gate))
                {
                    gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates.Add(run.Instance, gate);
                }

                return gate.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>The facts every run of these tests asks of its runners.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        public bool WritesTree(string runner) => false;
    }
}
