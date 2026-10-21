using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The recovery in memory, over fake step runners: a failure no handler takes given to <c>recover</c> where the
/// <c>onFailure</c> that decides says so, the hand-over stopping at a loop's member and at a top-level step; the step
/// that gave it the failure running again from where it stopped; a failure of the recovery's own work; the recovery
/// sustaining itself; and its bound asked with a recommended fix, <c>direct</c> handing its text to the next visit.
/// </summary>
public sealed class FlowRecoverTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A runner that answers each step as its test scripted it.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => Task.FromResult(result);
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by its script, which sees how often the step has run.</summary>
        public FakeRunner On(string step, Func<int, FlowStepResult> script)
        {
            var asked = 0;
            _scripts[step] = _ => Task.FromResult(script(++asked));
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = run => Task.FromResult(script(run));
            return this;
        }

        /// <inheritdoc/>
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add(run.Instance);

            return _scripts.GetValueOrDefault(run.Step.Name, _ => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>A step's output, read from JSON text.</summary>
    private static JsonNode? Output(string json)
        => JsonNode.Parse(json);

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>The one open question that fits, or a failure that says there is not exactly one.</summary>
    private static FlowQuestion One(IReadOnlyList<FlowQuestion> open, Func<FlowQuestion, bool> fits)
    {
        var asked = open.Where(fits).ToList();

        if (asked.Count != 1)
        {
            throw new InvalidOperationException($"expected exactly one open question to fit, found {asked.Count}");
        }

        return asked[0];
    }

    /// <summary>A failure whose onFailure says recover is given to the recovery, and the step that failed runs again from where it stopped.</summary>
    [Fact]
    public async Task AFailureSayingRecoverIsGivenToTheRecovery_AndTheFailedStepRunsAgain()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new FakeRunner()
            .On("build", asked => asked == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["build", "recover.mend", "build+2", "after"], runner.Started);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["build"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["build+2"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["recover"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>The recovery reads the step whose failure it took as source: its code, its error, and the fix a person gave.</summary>
    [Fact]
    public async Task TheRecoveryReadsItsSource_CodeErrorAndThePersonsFix()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              maxVisits: 1
              steps:
                - name: diagnose
                  type: orient
                  when: "source.code == 20 && source.direction == 'write the schema by hand'"
            steps:
              - name: build
                type: orient
                onFailure: recover
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("diagnose", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var first = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The recovery's visit 1 is spent with its diagnose never raised - its when read false, for no person had
        // given a fix - and the step failed again: at its bound the run asks, and a direct answer hands the person's
        // fix to the next visit.
        Assert.Equal(FlowRunStatus.Paused, first.Status);
        Assert.Equal(["build", "build+2"], runner.Started);
        var asked = One(coordinator.OpenQuestions, question => question.Kind == "recoveryBound");
        Assert.Equal("more, direct, abort", string.Join(", ", asked.Options));

        Assert.True(coordinator.TryAnswer(asked.Id, "direct", "write the schema by hand", out _));
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The second visit's diagnose reads the person's fix as source.direction, and only for that is it raised.
        Assert.Equal(["build", "build+2", "recover+2.diagnose", "build+3"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["recover+2.diagnose"].State);

        // build+3 failed once more, and the bound comes again.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Contains(coordinator.OpenQuestions, question => question.Kind == "recoveryBound");
    }

    /// <summary>
    /// The recovery's steps - however deep in it - read, in a template as in an expression, the step that gave the
    /// failure ({source.step}), the recovery's own visit for it ({visit}), and the fix a person gave for that visit
    /// alone ({source.direction}): empty on every other visit.
    /// </summary>
    [Fact]
    public async Task TheRecoverysSteps_ReadTheirSource_TheirVisit_AndAFixGivenForThatVisitAlone()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              maxVisits: 1
              steps:
                - name: inner
                  steps:
                    - name: say
                      type: pause
                      question: 'visit {visit} of {source.step}, told [{source.direction}]'
                      options: [ok]
            steps:
              - name: build
                type: orient
                onFailure: recover
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var first = One(coordinator.OpenQuestions, question => question.Kind == "pause");
        Assert.Equal("visit 1 of build, told []", first.Problem);
        Assert.True(coordinator.TryAnswer(first.Id, "ok", out _));

        _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var bound = One(coordinator.OpenQuestions, question => question.Kind == "recoveryBound");
        Assert.True(coordinator.TryAnswer(bound.Id, "direct", "use the cache", out _));

        _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var second = One(coordinator.OpenQuestions, question => question.Kind == "pause");
        Assert.Equal("visit 2 of build+2, told [use the cache]", second.Problem);
        Assert.True(coordinator.TryAnswer(second.Id, "ok", out _));

        _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var again = One(coordinator.OpenQuestions, question => question.Kind == "recoveryBound");
        Assert.True(coordinator.TryAnswer(again.Id, "more", out _));

        _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var third = One(coordinator.OpenQuestions, question => question.Kind == "pause");
        Assert.Equal("visit 3 of build+3, told []", third.Problem);
    }

    /// <summary>A step that fails again is recovered again, until it passes or a person is asked.</summary>
    [Fact]
    public async Task AStepThatFailsAgainIsRecoveredAgain_UntilItPasses()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
            """);
        var runner = new FakeRunner()
            .On("build", asked => asked == 3
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success)
                : FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["build", "recover.mend", "build+2", "recover+2.mend", "build+3"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["build+3"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A failure of the recovery's own work, past its steps' retries, does what its onFailure says: a pause asks.</summary>
    [Fact]
    public async Task AFailureOfTheRecoverysOwnWork_PausesWhereItsOnFailureSays()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("mend", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the remedy stayed red"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = One(coordinator.OpenQuestions, question => question.Kind == "failure");
        Assert.Equal("recover", asked.Instance);
    }

    /// <summary>
    /// retry, answered to the failure of the recovery's own work, runs the recovery again for the same failure: its next
    /// visit, and once it is done the failed step runs again - never a recovery left stranded.
    /// </summary>
    [Fact]
    public async Task ARetryOfTheRecoverysOwnFailure_RunsTheRecoveryAgain_AndThenTheFailedStep()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
            """);
        var runner = new FakeRunner()
            .On("build", asked => asked == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("mend", asked => asked == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the remedy stayed red")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = One(coordinator.OpenQuestions, question => question.Kind == "failure");
        Assert.True(coordinator.TryAnswer(asked.Id, "retry", out _));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["build", "recover.mend", "recover+2.mend", "build+2"], runner.Started);
        Assert.Equal(2, coordinator.Instances["recover+2"].Visit);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["build+2"].State);
    }

    /// <summary>A failure of the recovery's own work ends the run where its onFailure says stop.</summary>
    [Fact]
    public async Task AFailureOfTheRecoverysOwnWork_EndsTheRunWhereItsOnFailureSaysStop()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: stop
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("mend", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the remedy stayed red"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(["build", "recover.mend"], runner.Started);
    }

    /// <summary>A handler at its bound after a failure is not asked about in a workflow that declares a recovery: the failure is left unhandled and the recovery takes it.</summary>
    [Fact]
    public async Task AHandlerAtItsBoundAfterFailure_HandsToTheRecoveryAndAsksNothing()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                then: build
                maxVisits: 1
            """);
        var runner = new FakeRunner()
            .On("build", asked => asked == 3
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success)
                : FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // build failed, fix answered it once, build+2 failed again: fix is at its bound, and the recovery takes the
        // failure without a question.
        Assert.Equal(["build", "fix", "build+2", "recover.mend", "build+3"], runner.Started);
        Assert.Empty(coordinator.OpenQuestions);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A member's failure whose hand-over meets a loop that says recover is taken by the recovery for that step, and the loop's other items go on.</summary>
    [Fact]
    public async Task ALoopMembersFailureIsTakenForThatStep_TheLoopsOtherItemsGoOn()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                onFailure: recover
                steps:
                  - name: work
                    type: orient
            """);
        var runner = new FakeRunner()
            .On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["a", "b"]}""")))
            .On("work", (FlowStepRun run) => run.Instance == "items[0].work"
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["plan", "items[0].work", "items[1].work", "items[0].recover.mend", "items[0].work+2"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A failure nothing decides, at the top level of a workflow that declares a recovery, pauses: a person is asked.</summary>
    [Fact]
    public async Task ATopLevelFailureNothingDecides_PausesWhereARecoveryIsDeclared()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
            """);
        var runner = new FakeRunner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = One(coordinator.OpenQuestions, question => question.Kind == "failure");
        Assert.Equal("build", asked.Instance);
    }

    /// <summary>At the recovery's bound the run asks with the advisor's recommendation carried as every question's is; skip skips the step where it is skippable.</summary>
    [Fact]
    public async Task AtTheRecoverysBoundTheRunAsks_AndSkipSkipsASkippableStep()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              maxVisits: 1
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
                skippable: true
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var first = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, first.Status);
        var asked = One(coordinator.OpenQuestions, question => question.Kind == "recoveryBound");
        Assert.Contains("'build' failed, and the recovery took it 1 time without putting it right (20", asked.Problem, StringComparison.Ordinal);
        Assert.Equal("more, direct, skip, abort", string.Join(", ", asked.Options));

        Assert.True(coordinator.TryAnswer(asked.Id, "skip", out _));
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["build+2"].State);
        Assert.Equal("skipped by the answer to its bound", coordinator.Instances["build+2"].SkippedBecause);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }
}
