using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// A step raised by another step's ending, in memory over fake step runners: what raises a handler, by outcome and
/// code, every one that fits and one passed by; a failure a handler takes and one nothing handles; a group's handlers
/// raised by a member's failure that nothing inside handled; what a redirect resets, what is in flight, an agent left
/// open, and visits bounded; and a repeat bound by rounds and blocking, with its leftover.
/// </summary>
public sealed class FlowHandlerTests
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

        /// <summary>Answers <paramref name="step"/> by a script that may hold the step open, on the task it answers.</summary>
        public FakeRunner OnAsync(string step, Func<FlowStepRun, Task<FlowStepResult>> script)
        {
            _scripts[step] = script;
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

    /// <summary>A failure raises every handler that fits it, and the failure counts as handled: what depends on the source starts.</summary>
    [Fact]
    public async Task AFailingStepRaisesItsHandler_TheFailureCountsAsHandled_AndTheRunEndsDone()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure, codes: [20]}
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)", output: Output("""{"passed": false}""")))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["build", "fix", "after"], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["build"].State);
        Assert.Equal("fix", coordinator.Instances["build"].HandledBy);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }

    /// <summary>A handler that does not fit the ending is passed by: one never raised before is Skipped, with the reason, which satisfies what depends on it.</summary>
    [Fact]
    public async Task AHandlerThatDoesNotFitIsPassedBy_SkippedWithTheReason_SatisfyingWhatDependsOnIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
              - name: after
                type: orient
                dependsOn: [fix]
            """);
        var runner = new FakeRunner().On("build", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The source ended done, and the handler that answers only failures never came into being: it is Skipped,
        // with the reason, and what depends on it runs.
        Assert.Equal(["build", "after"], runner.Started);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["fix"].State);
        Assert.Equal("not raised: 'build' ended done", coordinator.Instances["fix"].SkippedBecause);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A handler that fits a success is raised by it, and one that fits only a failure is passed by as done.</summary>
    [Fact]
    public async Task ASuccessRaisesTheHandlerThatFitsIt_AndPassesTheOneThatAnswersOnlyFailures()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: announce
                type: orient
                on: {step: build, outcome: success}
              - name: mend
                type: orient
                on: {step: build, outcome: failure}
            """);
        var runner = new FakeRunner().On("build", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["build", "announce"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["announce"].State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["mend"].State);
        Assert.Equal("not raised: 'build' ended done", coordinator.Instances["mend"].SkippedBecause);
    }

    /// <summary>Every handler that fits one ending is raised, in declaration order.</summary>
    [Fact]
    public async Task EveryHandlerThatFitsOneEndingIsRaised()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: first-mender
                type: orient
                on: {step: build, outcome: failure, codes: [20]}
              - name: second-mender
                type: orient
                on: {step: build, outcome: end}
            """);
        var runner = new FakeRunner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["build", "first-mender", "second-mender"], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 failure was handled: build, by first-mender", end.Summary);
    }

    /// <summary>A handler is never started by the schedule: what brings it into being is its source's ending.</summary>
    [Fact]
    public async Task AHandlerIsNeverStartedByTheSchedule()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                on: {step: other, outcome: failure}
              - name: other
                type: orient
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["other"], runner.Started);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["build"].State);
    }

    /// <summary>A source the flow decided not to run leaves its handlers Skipped with it, which satisfies what depends on them.</summary>
    [Fact]
    public async Task ASkippedSourceLeavesItsHandlersSkippedWithIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                when: "1 == 2"
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
              - name: after
                type: orient
                dependsOn: [fix]
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["fix"].State);
        Assert.Equal("not raised: 'build' was skipped", coordinator.Instances["fix"].SkippedBecause);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }

    /// <summary>A handler done with a then sends the run back: the target runs again as a new visit, and everything of its scope that depends on it with it.</summary>
    [Fact]
    public async Task AThenSendsTheRunBack_TheTargetRunsANewVisit_AndItsDependentsAreResetWithIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                then: build
              - name: verify
                type: orient
                dependsOn: [build]
              - name: after
                type: orient
                dependsOn: [verify]
            """);
        // The runner is told which visit it runs, and fails the first.
        var visits = new List<int>();
        var runner = new FakeRunner()
            .On("build", run =>
            {
                visits.Add(run.Visit);
                return run.Visit == 1
                    ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                    : FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The first visit of build failed; verify and after waited on the failure's settling, are part of what the
        // redirect resets, and run - once each, untouched by the reset for they had not started - after the target's
        // new visit.
        Assert.Equal([1, 2], visits);
        Assert.Equal(["build", "fix", "build+2", "verify", "after"], runner.Started);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["build"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["build+2"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["verify"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(2, coordinator.Instances["build+2"].Visit);
    }

    /// <summary>A handler at its bound asks: more raises it once more, accept takes the source's ending as it stands, fail gives the handler up.</summary>
    [Fact]
    public async Task AHandlerAtItsBoundAsks_MoreRaisesOnceMore_AndAcceptTakesTheEnding()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: stop
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                then: build
                maxVisits: 1
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var waiting = coordinator.RunAsync(TestContext.Current.CancellationToken);
        var first = await waiting;

        // build failed, fix answered it once, build+2 failed again: fix is at its bound, and the run asks.
        Assert.Equal(FlowRunStatus.Paused, first.Status);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");
        Assert.Equal("'fix' has answered 'build' 1 times, and 'build' failed again (20)", asked.Problem);

        Assert.True(coordinator.TryAnswer(asked.Id, "more", out _));
        var second = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // One more visit was granted: fix+2 answers build's third failure, and the question comes again.
        Assert.Equal(FlowRunStatus.Paused, second.Status);
        var again = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");

        Assert.True(coordinator.TryAnswer(again.Id, "accept", out _));
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The source's ending is taken as it stands: the failure counts as handled, and what depends on the source starts.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["build", "fix", "build+2", "fix+2", "build+3", "after"], runner.Started);
        Assert.Equal(again.Id, coordinator.Instances["build+3"].HandledBy);
    }

    /// <summary>A fail answer to the bound gives the handler up: the source's own onFailure decides.</summary>
    [Fact]
    public async Task AFailAnswerToTheBound_GivesTheHandlerUp_AndTheSourcesOwnPolicyDecides()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: stop
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                then: build
                maxVisits: 1
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");

        Assert.True(coordinator.TryAnswer(asked.Id, "fail", out _));
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal("failed; 1 step failed: build+2", end.Summary);
    }

    /// <summary>A handler that itself fails meets its own onFailure: continue passes it over, and the source's ending is followed as if the handler had ended.</summary>
    [Fact]
    public async Task AHandlerThatFailsMeetsItsOwnPolicy_ContinuePassesItOver_AndTheSourceIsHandled()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                onFailure: continue
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.True(coordinator.Instances["fix"].PassedOver);
        Assert.Equal("fix", coordinator.Instances["build"].HandledBy);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }

    /// <summary>A group's handlers are raised by a member's failure that nothing inside the group handled, before the group's onFailure applies.</summary>
    [Fact]
    public async Task AGroupsHandlersAreRaisedByAMembersFailure_BeforeTheGroupsOnFailureApplies()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                steps:
                  - name: check
                    type: orient
              - name: cite
                type: orient
                on: {step: gate, outcome: failure, codes: [20]}
              - name: after
                type: orient
                dependsOn: [gate]
            """);
        var runner = new FakeRunner()
            .On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("cite", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["gate.check", "cite", "after"], runner.Started);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["gate"].State);
        Assert.Equal("cite", coordinator.Instances["gate"].HandledBy);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 failure was handled: gate, by cite", end.Summary);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }

    /// <summary>A create-agent step visited again in the same scope, while the agent it made is live, is Done at once and makes nothing.</summary>
    [Fact]
    public async Task ACreateAgentVisitedAgainWhileItsAgentIsLive_IsDoneAtOnce()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: orient
              - name: agent
                type: create-agent
                agent: maker
              - name: replan
                type: orient
                on: {step: work, outcome: failure, codes: [20]}
                then: agent
            """);
        var runner = new FakeRunner()
            .On("work", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("agent", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"agent": "a3"}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The agent step ran once and made its agent; the redirect visited it again and the engine ended the new
        // visit at once, for the agent it made is live.
        Assert.Equal(["work", "agent", "replan"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["agent+2"].State);
        Assert.Equal("a3", coordinator.Instances["agent+2"].Made);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A redirect that would leave a group while an agent made in it still has its worktree asks, naming each; stay gives the redirect up.</summary>
    [Fact]
    public async Task ARedirectThatWouldLeaveAnAgentOpenAsks_StayGivesItUp()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: round
                dependsOn: [plan]
                steps:
                  - name: worker
                    type: create-agent
                    agent: maker
                  - name: check
                    type: orient
                  - name: fix
                    type: orient
                    on: {step: check, outcome: failure, codes: [20]}
                    then: plan
            """);
        var runner = new FakeRunner()
            .On("worker", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"agent": "a3"}""")))
            .On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var first = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The handler sends the run back past the group it lives in, and the agent made in it is still open. The
        // question is of the handler's ending: the handler stays done while it is open.
        Assert.Equal(FlowRunStatus.Paused, first.Status);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "openAgents");
        Assert.Equal("'fix' would go back to 'plan', and agents 'a3', made since, are still open", asked.Problem);
        Assert.Equal(["retry", "stay", "abort"], asked.Options);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["round.fix"].State);
        Assert.Equal(asked.Id, coordinator.Instances["round.fix"].WaitingQuestion);
        Assert.Equal(FlowInstanceState.Running, coordinator.Instances["round"].State);

        Assert.True(coordinator.TryAnswer(asked.Id, "stay", out _));
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The redirect is given up: the run goes on as if the handler named no then, the source's failure counts as
        // handled, and the group ends over its done handler.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("fix", coordinator.Instances["round.check"].HandledBy);
        Assert.Equal("done; 1 failure was handled: check, by fix", end.Summary);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["round.fix"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["round"].State);
    }

    /// <summary>
    /// A handler at its bound after a success asks of the source's ending and leaves it as it ended: done. accept takes
    /// the success as it stands without that handler - the others it fits are raised; fail fails the source with the
    /// question's words, and its own onFailure decides.
    /// </summary>
    [Fact]
    public async Task AVisitBoundAfterASuccess_LeavesTheSourceDone_AcceptGoesOn_FailFailsIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: tell
                type: orient
                on: {step: build, outcome: success}
                maxVisits: 1
                then: build
              - name: note
                type: orient
                on: {step: build, outcome: success}
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // build, tell sending the run back, build again - and tell at its bound asks of that ending.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");
        Assert.Equal("build+2", asked.Instance);
        Assert.Equal("'tell' has answered 'build' 1 times, and 'build' ended done again", asked.Problem);
        var again = coordinator.Instances["build+2"];
        Assert.Equal(FlowInstanceState.Done, again.State);
        Assert.Equal(asked.Id, again.WaitingQuestion);
        Assert.Equal(["build", "tell", "note", "build+2"], runner.Started);

        Assert.True(coordinator.TryAnswer(asked.Id, "accept", out _));
        end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done", end.Summary);
        Assert.Equal(FlowInstanceState.Done, again.State);
        Assert.Null(again.WaitingQuestion);
        Assert.DoesNotContain(coordinator.Instances.Values, instance => instance.HandledBy is not null);
        Assert.Equal(["build", "tell", "note", "build+2", "note+2"], runner.Started);

        var failing = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock());
        _ = await failing.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var bound = AssertSingle(failing.OpenQuestions, question => question.Kind == "visitBound");
        Assert.True(failing.TryAnswer(bound.Id, "fail", out _));

        end = await failing.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        var failed = failing.Instances["build+2"];
        Assert.Equal(FlowInstanceState.Failed, failed.State);
        Assert.Equal("visitBound", failed.EndedByQuestion);
        Assert.Equal(HarnessExit.CommandFailed, failed.Exit);
        Assert.Equal(bound.Problem, failed.Error);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(["build+2"], end.Failed);
    }

    /// <summary>
    /// A redirect's new visits are drawn by the schedule as the first were: the target's when is read again - here it
    /// now reads false, so the target is skipped unrun - and what depends on it, directly or through others, waits for
    /// it and runs after it, once.
    /// </summary>
    [Fact]
    public async Task ARedirectsNewVisits_ReadTheirWhenAgain_AndWaitForWhatTheyDependOn()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: orient
                when: '!(fix.skip ?? false)'
              - name: verify
                type: ci-read
                dependsOn: [gate]
              - name: report
                type: orient
                dependsOn: [verify]
                when: verify.ok
              - name: fix
                type: orient
                on: {step: verify, outcome: failure}
                then: gate
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner()
            .On("verify", run => run.Visit == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the read failed", output: Output("""{"ok": false}"""))
                : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": true}""")))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"skip": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["gate", "verify", "fix", "verify+2", "report"], runner.Started);
        var again = coordinator.Instances["gate+2"];
        Assert.Equal(FlowInstanceState.Skipped, again.State);
        Assert.Equal("its when read false", again.SkippedBecause);
    }

    /// <summary>A step drawn again by a redirect reads, in its when, the visit it will be: {visit} is 2 for the second.</summary>
    [Fact]
    public async Task ARedirectedStepsWhen_ReadsTheVisitItWillBe()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: orient
                when: visit == 1
              - name: verify
                type: ci-read
                dependsOn: [gate]
              - name: fix
                type: orient
                on: {step: verify, outcome: failure}
                then: gate
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner().On("verify", run => run.Visit == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the read failed")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["gate", "verify", "fix", "verify+2"], runner.Started);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["gate+2"].State);
    }

    /// <summary>While a question of a source's ending is open - its handler at its bound after a success - what depends on the source waits for the answer.</summary>
    [Fact]
    public async Task AQuestionOfASourcesEnding_HoldsWhatDependsOnIt_UntilAnswered()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: tell
                type: orient
                on: {step: build, outcome: success}
                maxVisits: 1
                then: build
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");
        Assert.Equal(["build", "tell", "after", "build+2"], runner.Started);

        Assert.True(coordinator.TryAnswer(asked.Id, "accept", out _));
        end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["build", "tell", "after", "build+2", "after+2"], runner.Started);
    }

    /// <summary>What a redirect resets waits for the target's new visit: nothing that depends on it starts while it runs.</summary>
    [Fact]
    public async Task ARedirectsDependents_WaitForTheTargetsNewVisitToEnd()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: orient
              - name: verify
                type: ci-read
                dependsOn: [gate]
              - name: fix
                type: orient
                on: {step: verify, outcome: failure}
                then: gate
            """);
        var cancel = TestContext.Current.CancellationToken;
        var held = new TaskCompletionSource<FlowStepResult>();
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .OnAsync("gate", run =>
            {
                if (run.Visit == 1)
                {
                    return Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
                }

                heldStarted.TrySetResult();
                return held.Task;
            })
            .On("verify", run => run.Visit == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the read failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(cancel);
        await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        // The target's new visit runs; what depends on it has not started beside it.
        Assert.Equal(["gate", "verify", "fix", "gate+2"], runner.Started);

        held.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["gate", "verify", "fix", "gate+2", "verify+2"], runner.Started);
    }

    /// <summary>
    /// A redirect back past a group leaves that group's visit whole: a question a member had open is withdrawn, the
    /// members that never ran are skipped, and nothing of the old visit runs - the new visit is drawn when what it
    /// depends on has run again, its members reading what the new visit of their source wrote.
    /// </summary>
    [Fact]
    public async Task ARedirectIntoAGroup_WithdrawsItsMembersQuestions_AndRunsNothingOfTheOldVisit()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: check
                type: ci-read
                dependsOn: [plan]
              - name: fix
                type: orient
                on: {step: check, outcome: failure}
                then: plan
              - name: round
                dependsOn: [plan]
                steps:
                  - name: ask
                    type: pause
                    question: go on?
                    options: [go]
                  - name: extra
                    type: orient
                    when: plan.second
                  - name: after-ask
                    type: orient
                    dependsOn: [ask]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner()
            .On("plan", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output(run.Visit == 1 ? """{"second": false}""" : """{"second": true}""")))
            .On("check", run => run.Visit == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the read failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The old visit's question was withdrawn and its waiting member skipped; the new visit asks its own and ran
        // the member whose when only the second plan meets.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal("withdrawn", coordinator.Questions.Single(question => question.Instance == "round.ask").State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["round.ask"].State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["round"].State);
        var asked = Assert.Single(coordinator.OpenQuestions);
        Assert.Equal("round+2.ask", asked.Instance);
        Assert.Equal(["plan", "check", "fix", "plan+2", "check+2", "round+2.extra"], runner.Started);

        Assert.True(coordinator.TryAnswer(asked.Id, "go", out _));
        end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["plan", "check", "fix", "plan+2", "check+2", "round+2.extra", "round+2.after-ask"], runner.Started);
        Assert.DoesNotContain(coordinator.Instances.Keys, id => id == "round.after-ask");
    }

    /// <summary>
    /// A redirect reaches every level of a group it resets: a block nested in it, open over a member waiting on a
    /// question, is no step running - the redirect is made at once, the question withdrawn and the nested visit left
    /// whole, never waiting for a person to answer what it withdraws.
    /// </summary>
    [Fact]
    public async Task ARedirectIntoAGroup_ReachesABlockNestedInIt_WithdrawingItsQuestion()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: check
                type: ci-read
                dependsOn: [plan]
              - name: fix
                type: orient
                on: {step: check, outcome: failure}
                then: plan
              - name: round
                dependsOn: [plan]
                steps:
                  - name: review
                    steps:
                      - name: ask
                        type: pause
                        question: go on?
                        options: [go]
                      - name: after-ask
                        type: orient
                        dependsOn: [ask]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner()
            .On("check", run => run.Visit == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the read failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal("withdrawn", coordinator.Questions.Single(question => question.Instance == "round.review.ask").State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["round.review.ask"].State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["round.review"].State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["round"].State);
        Assert.Equal("round+2.review.ask", Assert.Single(coordinator.OpenQuestions).Instance);
        Assert.Equal(["plan", "check", "fix", "plan+2", "check+2"], runner.Started);
    }

    /// <summary>
    /// A redirect decided while what it resets is in flight freezes every level of a group it resets: a member of a block
    /// nested in it, freed meanwhile by its dependency, is not drawn, and nothing of the old visit runs.
    /// </summary>
    [Fact]
    public async Task ARedirectWaiting_FreezesABlockNestedInTheGroupItResets()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: check
                type: ci-read
                dependsOn: [plan]
              - name: fix
                type: orient
                on: {step: check, outcome: failure}
                then: plan
              - name: round
                dependsOn: [plan]
                steps:
                  - name: slow
                    type: orient
                  - name: review
                    steps:
                      - name: early
                        type: orient
                      - name: late
                        type: orient
                        dependsOn: [early]
            """);
        var cancel = TestContext.Current.CancellationToken;

        // Made without asynchronous continuations: what a gate's completion posts to the session is posted before the
        // completion returns, so the session settles over it.
        var slow = new TaskCompletionSource<FlowStepResult>();
        var early = new TaskCompletionSource<FlowStepResult>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("check", run => run.Visit == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the read failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .OnAsync("slow", run => run.Instance == "round.slow" ? slow.Task : Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))
            .OnAsync("early", run =>
            {
                if (run.Instance != "round.review.early")
                {
                    return Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
                }

                started.TrySetResult();
                return early.Task;
            });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(cancel);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The check's failure, then its handler's end: the redirect is decided, and waits for slow and early.
        await coordinator.SettledAsync(cancel);
        await coordinator.SettledAsync(cancel);
        Assert.Contains("fix", runner.Started);

        early.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        await coordinator.SettledAsync(cancel);

        Assert.DoesNotContain("round.review.late", runner.Started);

        slow.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.DoesNotContain("round.review.late", runner.Started);
        Assert.Contains("round+2.review.late", runner.Started);
    }

    /// <summary>
    /// A redirect decided while another waits is kept, never dropped: redirects are made in the order they were decided,
    /// each once nothing it resets is running - here two items of a forEach each send the run back within their own
    /// item, and both go back.
    /// </summary>
    [Fact]
    public async Task ASecondRedirectWhileOneWaits_IsQueued_AndMadeInTurn()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: build
                    type: orient
                  - name: slow
                    type: orient
                    dependsOn: [build]
                  - name: tell
                    type: orient
                    on: {step: build, outcome: success}
                    when: source.n == 1
                    then: build
            """);
        var cancel = TestContext.Current.CancellationToken;
        var held = new TaskCompletionSource<FlowStepResult>();
        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["a", "b"]}""")))
            .On("build", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output($$"""{"n": {{run.Visit}}}""")))
            .OnAsync("slow", run =>
            {
                if (run.Instance != "items[0].slow")
                {
                    return Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
                }

                heldStarted.TrySetResult();
                return held.Task;
            });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(cancel);
        await heldStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);

        // The first item's redirect waits for its slow step; the second's, decided meanwhile, waits its turn.
        Assert.DoesNotContain("items[0].build+2", runner.Started);
        Assert.DoesNotContain("items[1].build+2", runner.Started);

        held.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Contains("items[0].build+2", runner.Started);
        Assert.Contains("items[1].build+2", runner.Started);
        Assert.Contains("items[1].slow+2", runner.Started);
    }

    /// <summary>A handler's bound counts the visits it was raised for, never the place-holder a pass-by leaves: a handler never raised is raised, not asked about.</summary>
    [Fact]
    public async Task TheVisitBound_CountsTheVisitsRaised_NotAHandlerPassedBy()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: check
                type: orient
              - name: again
                type: orient
                on: {step: check, outcome: success}
                maxVisits: 1
                then: check
              - name: fix
                type: orient
                on: {step: check, outcome: failure}
                maxVisits: 1
            """);
        var cancel = TestContext.Current.CancellationToken;

        // The first visit passes - fix is passed by - and the second fails, which fix has never answered.
        var runner = new FakeRunner().On("check", run => run.Visit == 1
            ? FlowStepResult.Ended(passed: true, HarnessExit.Success)
            : FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Empty(coordinator.OpenQuestions);
        Assert.Equal(["check", "again", "check+2", "fix+2"], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 failure was handled: check, by fix", end.Summary);
    }

    /// <summary>A step that a redirect would visit a hundredth time fails, saying so.</summary>
    [Fact]
    public async Task ARedirectThatWouldVisitAStepMoreThan99Times_FailsItSayingSo()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: stop
              - name: fix
                type: orient
                on: {step: build, outcome: failure, codes: [20]}
                then: build
                maxVisits: 1
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        // The run asks at the handler's bound once for each of the target's failures, and each more grants one more
        // visit, until the run would visit build a hundredth time.
        while (true)
        {
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            if (end.Status is FlowRunStatus.Failed)
            {
                var bound = coordinator.Instances.Values.Last(instance => instance.Step.Name == "build");

                // Ninety-nine visits ran; the hundredth was refused before it ran.
                Assert.Equal(100, bound.Visit);
                Assert.Equal(99, runner.Started.Count(started => started == "build" || started.StartsWith("build+", StringComparison.Ordinal)));
                Assert.Equal(13, bound.Exit);
                Assert.Equal("'build' was visited 99 times in its scope; at most 99 visits of one step in one scope are allowed", bound.Error);
                return;
            }

            var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");

            if (!coordinator.TryAnswer(asked.Id, "more", out _))
            {
                Assert.Fail("the bound's more was refused");
            }
        }
    }

    /// <summary>A leftover that cannot be read fails its repeat as an unreadable until does: what a loop left is never dropped unsaid.</summary>
    [Fact]
    public async Task AnUnreadableLeftover_FailsItsRepeat_AsAnUnreadableUntilDoes()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: loop
                onFailure: continue
                repeat:
                  until: review.passed
                  rounds: 1
                  blocking: review.blocking
                  leftover: nowhere.left
                steps:
                  - name: review
                    type: orient
            """);
        var runner = new FakeRunner().On("review", FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: Output("""{"passed": false, "blocking": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var loop = coordinator.Instances["loop"];
        Assert.Equal(FlowInstanceState.Failed, loop.State);
        Assert.Equal(HarnessExit.ConfigInvalid, loop.Exit);
        Assert.StartsWith("expression 'leftover' of 'loop#1': ", loop.Error, StringComparison.Ordinal);
        Assert.Null(loop.Leftover);
    }

    /// <summary>A repeat bound by rounds runs its first rounds on until alone; past them, a false blocking ends it with no question and its leftover said.</summary>
    [Fact]
    public async Task ARoundsRepeatEndsWhereBlockingReadsFalse_WithItsLeftoverSaid()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: loop
                repeat:
                  until: review.passed
                  rounds: 2
                  blocking: review.blocking
                  leftover: review.left
                steps:
                  - name: review
                    type: orient
              - name: after
                type: orient
                dependsOn: [loop]
              - name: tell
                type: pause
                dependsOn: [after]
                question: 'what is left: {run.leftover}'
                options: [ok]
            """);
        var runner = new FakeRunner().On("review", FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: Output("""{"passed": false, "blocking": false, "left": "one function and one test"}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // Two rounds ran on until alone; past them blocking read false, so the loop ended with no question.
        Assert.Equal(2, coordinator.Instances["loop"].Children.Count);
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["loop"].State);
        Assert.Equal("\"one function and one test\"", coordinator.Instances["loop"].Leftover?.ToJsonString());

        // The run's leftover is what its loops left, as a template reads it.
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "pause");
        Assert.Equal("what is left: 'loop' left one function and one test", asked.Problem);
    }

    /// <summary>Past the rounds a true blocking runs on, and at the blockers' bound the run asks the loop's question.</summary>
    [Fact]
    public async Task PastTheRoundsATrueBlockingRunsOn_UntilTheBlockersBoundAsks()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: loop
                repeat:
                  until: review.passed
                  rounds: 2
                  blocking: review.blocking
                  blockersLimitRounds: 1
                steps:
                  - name: review
                    type: orient
            """);
        var runner = new FakeRunner().On("review", FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: Output("""{"passed": false, "blocking": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // Two rounds and one further iteration while blocking held: the fourth is past the bound, and the run asks.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "loopBound");
        Assert.Equal("'loop' has run 3 iterations and its until still reads false; 2 rounds and 1 more while blocking holds (rounds, blockersLimitRounds)", asked.Problem);

        Assert.True(coordinator.TryAnswer(asked.Id, "accept", out _));
        var settled = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, settled.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["loop"].State);
    }

    /// <summary>Asserts exactly one open question fits, and answers with it.</summary>
    private static FlowQuestion AssertSingle(IReadOnlyList<FlowQuestion> open, Func<FlowQuestion, bool> fits)
    {
        var asked = open.Where(fits).ToList();

        if (asked.Count != 1)
        {
            throw new InvalidOperationException($"expected exactly one open question to fit, found {asked.Count}");
        }

        return asked[0];
    }
}

/// <summary>
/// What an ending owes, on disk: the source's own record lists each handler as owed, raised, done or passed, and a
/// redirect the run owes is written to <c>flow.json</c> before the first reset - a pause finds it owed, and the resume
/// makes it.
/// </summary>
public sealed class FlowHandlerRecordsTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The run's records as a test watches them: each write passed on, and a signal once a snapshot holds what the test waits for.</summary>
    private sealed class Watched(IFlowRecords inner, Func<FlowSnapshot, bool> waitsFor) : IFlowRecords
    {
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a snapshot written holds what the test waits for.</summary>
        public Task Seen => _seen.Task;

        /// <inheritdoc/>
        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            inner.Write(kind, snapshot, instance, from, to, attempt, detail);

            if (waitsFor(snapshot))
            {
                _ = _seen.TrySetResult();
            }
        }

        /// <inheritdoc/>
        public void Question(IReadOnlyList<FlowQuestionEntry> questions)
            => inner.Question(questions);
    }

    /// <summary>A runner that answers each step as its test scripted it, holding open the steps the test wants to time.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public Runner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => Task.FromResult(result);
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it.</summary>
        public void Hold(string step)
        {
            var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _scripts[step] = _ => gate.Task;
            _gates[step] = gate;
        }

        /// <summary>Completes a held <paramref name="step"/> as passed.</summary>
        public bool Release(string step)
            => _gates[step].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run.</summary>
        public Runner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = run => Task.FromResult(script(run));
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until its work is cancelled - a stop - or the test completes it.</summary>
        public void HoldHearingTheStop(string step)
        {
            Hold(step);
            _ = _hearing.Add(step);
        }

        private readonly HashSet<string> _hearing = new(StringComparer.Ordinal);

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add(run.Instance);

            if (!_starts.TryGetValue(run.Step.Name, out var started))
            {
                started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _starts.Add(run.Step.Name, started);
            }

            _ = started.TrySetResult();

            if (_hearing.Contains(run.Step.Name))
            {
                return await _gates[run.Step.Name].Task.WaitAsync(cancellationToken);
            }

            return await _scripts.GetValueOrDefault(run.Step.Name, _ => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>The run's records, made whole in <paramref name="directory"/> under run <paramref name="run"/>.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FlowRunRecords Records, ManualClock Clock) Records(string run, string directory, WorkflowDefinition definition)
    {
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        var clock = new ManualClock();

        return (harness, layout, new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of(run, definition, startedAt: clock.GetUtcNow()), clock), clock);
    }

    /// <summary>An ending's record says what it owes and owes no longer: each handler owed, raised, then done, and the failure handled.</summary>
    [Fact]
    public async Task AnEndingsRecordSaysWhatItOwes_AndTheFailureHandled()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
            """);
        var runner = new Runner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);

            _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        var build = harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build"));

        Assert.NotNull(build);
        Assert.Equal("fix", Assert.Single(build.Handlers ?? []).Handler);
        Assert.Equal("done", build.Handlers![0].State);
        Assert.Equal("fix", build.HandledBy);

        var flow = harness.OrchestrationStore.ReadFlow(layout);

        Assert.NotNull(flow);
        Assert.Null(flow.Redirects);
        Assert.Equal("fix", flow.Steps.Single(step => step.Instance == "build").HandledBy);

        // The report says what handled the failure, apart from the steps passed over.
        var left = FlowReports.Write(harness.FileSystem, layout, flow, null, "done; 1 failure was handled: build, by fix", 0, clock.GetUtcNow());
        var report = harness.FileSystem.ReadAllText(layout.ReportFile);

        Assert.Contains("## Failures a handler took", report, StringComparison.Ordinal);
        Assert.Contains("- build, by fix", report, StringComparison.Ordinal);
        Assert.Empty(left);
    }

    /// <summary>A redirect the run owes is written to flow.json before the first reset, and a pause that finds it owed leaves it to the resume to make.</summary>
    [Fact]
    public async Task ARedirectOwedIsWrittenBeforeTheFirstReset_AMadeOnceAtTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: orient
              - name: round
                steps:
                  - name: member
                    type: orient
              - name: fix
                type: orient
                on: {step: work, outcome: failure}
                then: round
            """);
        var runner = new Runner()
            .On("work", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        runner.Hold("member");
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var watched = new Watched(records, snapshot => snapshot.Redirects is { Count: > 0 });
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            // The group is in flight when the handler ends done and names it: the redirect is owed, and nothing it
            // resets is cancelled.
            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt");
            _ = runner.Release("member");
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);

            var owed = Assert.Single(harness.OrchestrationStore.ReadFlow(layout)?.Redirects ?? []);

            Assert.NotNull(owed);
            Assert.Equal("fix", owed.Handler);
            Assert.Equal("round", owed.Target);
            Assert.Contains("round", owed.Steps, StringComparer.Ordinal);

            // The resume makes what the pause found owed: the group's visit ended where it stood, and a new one begins.
            var done = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, done.Status);
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)?.Redirects);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["round"].State);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["round+2"].State);
            Assert.Contains("round+2.member", ((Runner)runner).Started, StringComparer.Ordinal);
        }
    }

    /// <summary>A pause that finds a redirect owed leaves it to the resume to make - even a resume by another engine, from the records alone.</summary>
    [Fact]
    public async Task ARedirectOwedIsMadeByAResumeFromTheRecordsAlone()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: orient
              - name: round
                steps:
                  - name: member
                    type: orient
              - name: fix
                type: orient
                on: {step: work, outcome: failure}
                then: round
            """);
        var runner = new Runner()
            .On("work", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        runner.Hold("member");
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var watched = new Watched(records, snapshot => snapshot.Redirects is { Count: > 0 });
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            // The redirect is owed once the handler has ended done and named the group in flight.
            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt");
            _ = runner.Release("member");
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Single(harness.OrchestrationStore.ReadFlow(layout)?.Redirects ?? []);
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));

        using (var again = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), clock, records: again, resume: taken);
            var done = await resumed.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, done.Status);
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)?.Redirects);
            Assert.Equal(FlowInstanceState.Done, resumed.Instances["round+2"].State);
            Assert.Contains("round+2.member", resumed.Instances.Keys, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// A hard stop leaves a redirect owed as a pause does: one waiting on a member of its set still in flight is not made
    /// by the stop, and the resume - from the records alone - makes it: the target runs again exactly once, and the
    /// member the stop stopped never runs again beside its new visit.
    /// </summary>
    [Fact]
    public async Task AHardStop_LeavesARedirectOwed_TheResumeMakesIt_TheTargetRunningAgainOnce()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: implement
                type: orient
              - name: verify
                type: orient
                dependsOn: [implement]
              - name: slow
                type: orient
                dependsOn: [implement]
              - name: mend
                type: orient
                on: {step: verify, outcome: failure}
                then: implement
            """);
        var runner = new Runner()
            .On("verify", run => run.Visit == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        runner.HoldHearingTheStop("slow");
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var watched = new Watched(records, snapshot => snapshot.Redirects is { Count: > 0 });
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            // The redirect waits for slow, a member of its set in flight; the hard stop stops slow and makes nothing.
            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestStop();
            var end = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Stopped, end.Status);
            Assert.Equal("implement", Assert.Single(harness.OrchestrationStore.ReadFlow(layout)?.Redirects ?? []).Target);
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));
        var again = new Runner()
            .On("verify", run => FlowStepResult.Ended(passed: true, HarnessExit.Success));

        using (var records2 = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = new FlowCoordinator(definition, again, new RunnerFacts(), clock, records: records2, resume: taken);
            var done = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, done.Status);
            Assert.Equal(["implement+2", "verify+2", "slow+2"], again.Started);
            Assert.Equal(FlowInstanceState.Skipped, resumed.Instances["slow"].State);
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)?.Redirects);
        }
    }
}
