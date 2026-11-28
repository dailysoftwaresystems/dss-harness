using System.Text.Json;
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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
            .On("agent", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"agent": "a1"}""")));
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The agent step ran once and made its agent; the redirect visited it again and the engine ended the new
        // visit at once, for the agent it made is live.
        Assert.Equal(["work", "agent", "replan"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["agent+2"].State);
        Assert.Equal("a1", coordinator.Instances["agent+2"].Made);
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
            .On("worker", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"agent": "a1"}""")))
            .On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var first = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The handler sends the run back past the group it lives in, and the agent made in it is still open. The
        // question is of the handler's ending: the handler stays done while it is open.
        Assert.Equal(FlowRunStatus.Paused, first.Status);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "openAgents");
        Assert.Equal("'fix' would go back to 'plan', and agents 'a1', made since, are still open", asked.Problem);
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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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

        var failing = TestAdvice.Coordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock());
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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
    /// A handler a redirect leaves before it ended took nothing: the failure it was raised for is not counted handled by
    /// it - the summary names only the failures a handler that ran took - and the source's record says the handler was
    /// left, so no resume settles the ending by it either.
    /// </summary>
    [Fact]
    public async Task AHandlerARedirectLeaves_SettlesNothing_ItsSourceNotCountedHandled()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: verify
                type: ci-read
                dependsOn: [plan]
              - name: fix
                type: orient
                on: {step: verify, outcome: failure}
                then: plan
              - name: round
                dependsOn: [plan]
                steps:
                  - name: check
                    type: orient
                  - name: mend
                    type: pause
                    on: {step: check, outcome: failure}
                    question: mend it?
                    options: [go, stop]
            """);
        var cancel = TestContext.Current.CancellationToken;

        // Made without asynchronous continuations, so the session settles over what its completion posts.
        var verify = new TaskCompletionSource<FlowStepResult>();
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("check", run => run.Instance == "round.check"
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the check failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .OnAsync("verify", run =>
            {
                if (run.Visit != 1)
                {
                    return Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
                }

                verifying.TrySetResult();
                return verify.Task;
            });
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        using var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition));
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: records);

        var session = coordinator.RunAsync(cancel);
        await verifying.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);
        await coordinator.SettledAsync(cancel);

        // The check failed and its handler asks; then verify fails, and fix sends the run back past the group.
        Assert.Equal("round.mend", Assert.Single(coordinator.OpenQuestions).Instance);
        verify.SetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the read failed"));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 failure was handled: verify, by fix", end.Summary);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["round.mend"].State);
        Assert.Null(coordinator.Instances["round.check"].HandledBy);
        Assert.Equal([new FlowHandlerState("mend", "left")], harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "round.check").Handlers);
    }

    /// <summary>
    /// An ending a redirect abandoned - it left one of its handlers before that one ended - is never rebuilt by a resume,
    /// whatever its other handlers did: here a sibling handler ran done, with a then, before the redirect; the resume, from
    /// the records alone, settles nothing by either, counts the source handled by neither and decides no redirect over
    /// the visit the run left.
    /// </summary>
    [Fact]
    public async Task AnEndingARedirectAbandoned_IsNeverRebuiltByAResume_ThoughASiblingHandlerRanDone()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: verify
                type: ci-read
                dependsOn: [plan]
              - name: fix
                type: orient
                on: {step: verify, outcome: failure}
                then: plan
              - name: round
                dependsOn: [plan]
                steps:
                  - name: check
                    type: orient
                  - name: mend
                    type: pause
                    on: {step: check, outcome: failure}
                    question: mend it?
                    options: [go, stop]
                  - name: log
                    type: orient
                    on: {step: check, outcome: failure}
                    then: check
              - name: ship
                type: pause
                dependsOn: [round]
                question: ship it?
                options: [ship, hold]
            """);
        var cancel = TestContext.Current.CancellationToken;

        // Made without asynchronous continuations, so the session settles over what its completion posts.
        var verify = new TaskCompletionSource<FlowStepResult>();
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("check", run => run.Instance == "round.check"
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the check failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .OnAsync("verify", run =>
            {
                if (run.Visit != 1)
                {
                    return Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
                }

                verifying.TrySetResult();
                return verify.Task;
            });
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new JumpingClock();

        using (var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var session = coordinator.RunAsync(cancel);
            await verifying.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);
            await coordinator.SettledAsync(cancel);
            await coordinator.SettledAsync(cancel);

            // The check failed: log ran done, and mend asks; then verify fails, and fix sends the run back past the
            // group, leaving mend before it ended.
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["round.log"].State);
            Assert.Equal("round.mend", Assert.Single(coordinator.OpenQuestions).Instance);
            verify.SetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the read failed"));

            var paused = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            Assert.Equal("ship", Assert.Single(coordinator.OpenQuestions).Instance);
        }

        Assert.Equal(
            [new FlowHandlerState("log", "raised"), new FlowHandlerState("mend", "left")],
            harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "round.check").Handlers!.OrderBy(handler => handler.Handler, StringComparer.Ordinal));

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);

        using (var again = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, new FakeRunner(), new RunnerFacts(), clock, records: again, resume: taken);

            Assert.True(resumed.TryAnswer(Assert.Single(resumed.OpenQuestions).Id, "ship", out _));

            var done = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, done.Status);
            Assert.Equal("done; 1 failure was handled: verify, by fix", done.Summary);
            Assert.Null(resumed.Instances["round.check"].HandledBy);
            Assert.Equal(
                [new FlowHandlerState("log", "raised"), new FlowHandlerState("mend", "left")],
                harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "round.check").Handlers!.OrderBy(handler => handler.Handler, StringComparer.Ordinal));
        }

        // The one redirect the run decided is fix's: none was decided at the resume over the visit the run left.
        Assert.Equal(
            ["the run is sent to 'plan' by 'fix'"],
            FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Select(read => read.Detail).Where(detail => detail?.StartsWith("the run is sent to ", StringComparison.Ordinal) == true));
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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Empty(coordinator.OpenQuestions);
        Assert.Equal(["check", "again", "check+2", "fix+2"], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 failure was handled: check, by fix", end.Summary);
    }

    /// <summary>
    /// The most visits the recovery's maxVisits may give, and what it gives where the file names none, has one owner,
    /// <see cref="FlowRecover.MostVisits"/>: no file of the engine spells the number itself, so the range the definition's
    /// check holds the key to and the default the engine counts by cannot drift apart.
    /// </summary>
    [Fact]
    public void TheRecoverVisitBound_HasOneOwner()
    {
        var engine = Path.Combine(Prose.RepositoryRoot(), "src", "DssHarness.Core", "Orchestration");
        var spelt = FlowRecover.MostVisits.ToString(System.Globalization.CultureInfo.InvariantCulture);

        static bool Spells(string line, string number)
        {
            for (var at = line.IndexOf(number, StringComparison.Ordinal); at >= 0; at = line.IndexOf(number, at + 1, StringComparison.Ordinal))
            {
                var before = at == 0 ? ' ' : line[at - 1];
                var after = at + number.Length >= line.Length ? ' ' : line[at + number.Length];

                if (!char.IsLetterOrDigit(before) && before is not '_' and not '.' && !char.IsLetterOrDigit(after) && after is not '_' and not '.')
                {
                    return true;
                }
            }

            return false;
        }

        var spelling = Directory.EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => File.ReadAllLines(file).Where(line => Spells(line, spelt)).Select(line => (Path.GetFileName(file), line.Trim())))
            .ToList();

        Assert.Equal([("WorkflowModels.cs", $"public const int MostVisits = {spelt};")], spelling);
    }

    /// <summary>
    /// The help, the README and the architecture say that a loop is held to its own bound alone - no engine-wide bound on
    /// a step's visits beside it - that the recovery's maxVisits is counted for each step's failures and each issue on its
    /// own, and that a handler whose then sends the run back over its group is counted where its loop goes round.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayALoopIsHeldToItsOwnBoundAlone()
    {
        var workflows = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "how often this handler may be raised in the scope its loop goes round in - its own, or, where its then sends the run back over a group around it, the scope that then goes back to - from 1 to 20; absent, 3; past it the run asks",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            $"how often the recovery may take one thing - the failures of one step in one scope, or one issue sent to fix - from 1 to {FlowRecover.MostVisits}; absent, {FlowRecover.MostVisits}; past it the run asks (recoveryBound), with the advisor's recommended fix. It bounds that loop alone: the recovery takes any number of steps' failures and issues, each counted on its own",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "A loop stops at its own bound alone - a repeat's, a handler's `maxVisits`, the recovery's - and a step visited again for what is no loop, a triage for each issue reported among them, is never refused for how often.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "No engine-wide bound on the visits of a step stands beside the loops' own: a step visited again for what is not a loop - a triage for each issue reported, a pause's changed answer, the always steps once per session, the planning step for each plan update - is never refused, and each loop stops at the bound it declares - a repeat's, a handler's `maxVisits`, the recovery's - or where a person answers its question.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "save where its `then` sends the run back over a group around it, which each turn makes anew, holding the handler again; there they are counted, and its `more` answers granted, in the scope that `then` goes back to, so that loop too ends at the handler's own bound.",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A loop is held to its own bound and to nothing else: a handler at its maxVisits asks, and each more a person gives
    /// is one more turn of it - past a hundred visits of the step it sends the run back to, none of them refused - until
    /// the answer that ends it, after which the step's own onFailure decides.
    /// </summary>
    [Fact]
    public async Task AHandlersLoop_GoesOnForEveryMoreAPersonGives_PastAHundredVisits()
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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());
        const int more = 120;

        // The run asks at the handler's bound once for each of the target's failures past it, and each more grants one
        // more visit of fix, and so one more of build.
        for (var given = 0; given <= more; given++)
        {
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");
            Assert.Equal($"'fix' has answered 'build' {(given + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} times, and 'build' failed again (20)", asked.Problem);

            if (!coordinator.TryAnswer(asked.Id, given < more ? "more" : "fail", out _))
            {
                Assert.Fail("the bound's answer was refused");
            }
        }

        var ended = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The first visit of build and one for each visit of fix: the one its bound gave, and one for each more.
        var builds = coordinator.Instances.Values.Where(instance => instance.Step.Name == "build").ToList();
        Assert.Equal(more + 2, builds.Count);
        Assert.Equal(more + 1, coordinator.Instances.Values.Count(instance => instance.Step.Name == "fix"));
        Assert.All(builds, build => Assert.Equal("leg 'windows' ended Red (20)", build.Error));
        Assert.Equal(FlowRunStatus.Failed, ended.Status);
    }

    /// <summary>
    /// A handler whose then sends the run back over the group it is in - each turn a new visit of that group, the
    /// handler in it again with none of its visits there - is held to its own maxVisits all the same: its visits are
    /// counted in the scope its loop goes round in, and at its bound the run asks, as at any handler's.
    /// </summary>
    [Fact]
    public async Task AHandlerWhoseThenSendsTheRunBackOverItsGroup_StopsAtItsOwnBound()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: prepare
                type: orient
              - name: stage
                dependsOn: [prepare]
                steps:
                  - name: test
                    type: orient
                    onFailure: stop
                  - name: fix
                    type: orient
                    on: {step: test, outcome: failure, codes: [20]}
                    then: prepare
                    maxVisits: 2
            """);
        // test fails at every visit the bound lets the loop make, and passes past them: a loop its bound did not stop
        // ends there, done, rather than going round for ever.
        var runner = new FakeRunner()
            .On("test", (int asked) => asked <= 10
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());
        var cancel = TestContext.Current.CancellationToken;

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // Two turns, each a new visit of prepare and of stage; test's third failure meets fix at its bound.
        Assert.Equal(["prepare", "stage.test", "stage.fix", "prepare+2", "stage+2.test", "stage+2.fix", "prepare+3", "stage+3.test"], runner.Started);
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");
        Assert.Equal("'fix' has answered 'test' 2 times, and 'test' failed again (20)", asked.Problem);
        Assert.Equal("stage+3.test", asked.Instance);

        // A more is one more turn of the same loop, counted where it goes round: one more visit of fix, then the question again.
        Assert.True(coordinator.TryAnswer(asked.Id, "more", out _));
        var again = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(["prepare", "stage.test", "stage.fix", "prepare+2", "stage+2.test", "stage+2.fix", "prepare+3", "stage+3.test", "stage+3.fix", "prepare+4", "stage+4.test"], runner.Started);
        Assert.Equal(FlowRunStatus.Paused, again.Status);
        Assert.Equal("'fix' has answered 'test' 3 times, and 'test' failed again (20)", AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound").Problem);
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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

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

    /// <summary>
    /// A handler that is a group, answered retry within the session, is raised again for the ending it answers - a new
    /// visit naming the same source, for the schedule never draws a handler - and the ending waits on that visit in the
    /// place of the one it replaced: what depends on the source starts once the new visit has ended, never while the
    /// failed one waits on its question, and never left waiting for ever once the failed one stands settled by the answer.
    /// </summary>
    [Fact]
    public async Task AGroupHandlerAnsweredRetry_IsRaisedAgainForItsEnding_WhichWaitsOnTheNewVisit()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                on: {step: build, outcome: failure}
                onFailure: pause
                steps:
                  - name: attempt
                    type: orient
              - name: other
                type: orient
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FlowCoordinator? coordinator = null;
        FlowInstanceState? fixAgainAtAfter = null;
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("attempt", (int asked) => asked == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .OnAsync("other", async _ =>
            {
                await other.Task;
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            })
            .On("after", (FlowStepRun _) =>
            {
                fixAgainAtAfter = coordinator!.Instances.TryGetValue("fix+2", out var again) ? again.State : null;
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        var advisor = new Asked();
        coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        // The session goes on while other runs: the answer is taken within it, where no session's start reads the ending
        // again.
        var session = coordinator.RunAsync(cancel);
        await advisor.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var asked = Assert.Single(coordinator.OpenQuestions);
        Assert.Equal("fix", asked.Instance);
        Assert.Null(coordinator.Instances["build"].HandledBy);
        Assert.DoesNotContain("after", runner.Started);

        Assert.Equal(FlowHandedOver.Taken, (await coordinator.OfferAnswerAsync(asked.Id, "retry").WaitAsync(TimeSpan.FromSeconds(30), cancel)).How);
        other.SetResult();
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
        Assert.Equal(["fix.attempt", "fix+2.attempt", "after"], runner.Started.Where(started => started is not "build" and not "other"));
        Assert.Equal(FlowInstanceState.Done, fixAgainAtAfter);
        var fixAgain = coordinator.Instances["fix+2"];
        Assert.Equal((FlowInstanceState.Done, 2), (fixAgain.State, fixAgain.Visit));
        Assert.Same(coordinator.Instances["build"], fixAgain.RaisedBy);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["fix"].State);
        Assert.Equal("fix", coordinator.Instances["build"].HandledBy);
    }

    /// <summary>
    /// A handler that ended failed has not ended for the ending it answers until its failure is decided: its policy
    /// asking a person holds what depends on the source, and retry runs the handler again, the ending settled by that
    /// run's end - never settled at the failed end, before the person was heard.
    /// </summary>
    [Fact]
    public async Task AHandlerThatFailedAndAsks_HoldsWhatDependsOnItsSource_UntilItsRetryHasEnded()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                onFailure: pause
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", (int asked) => asked == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());
        var cancel = TestContext.Current.CancellationToken;

        var paused = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, paused.Status);
        Assert.Equal(["build", "fix"], runner.Started);
        Assert.Null(coordinator.Instances["build"].HandledBy);
        var asked = Assert.Single(coordinator.OpenQuestions);
        Assert.Equal(("fix", "failure"), (asked.Instance, asked.Kind));

        Assert.True(coordinator.TryAnswer(asked.Id, "retry", out _));
        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
        Assert.Equal(["build", "fix", "fix", "after"], runner.Started);
        Assert.Equal((FlowInstanceState.Done, 2), (coordinator.Instances["fix"].State, coordinator.Instances["fix"].Attempt));
    }

    /// <summary>
    /// A handler whose failure its own handler takes ends for the ending it answers once that handler has ended and its
    /// own ending is settled - the settling is the end, through the one owner of an ending's settling: what depends on the
    /// first source starts then, never beside the second handler's run, and never left waiting for ever.
    /// </summary>
    [Fact]
    public async Task AHandlerWhoseFailureItsOwnHandlerTakes_EndsForItsSourceOnceThatSettles()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
              - name: mend
                type: orient
                on: {step: fix, outcome: failure}
              - name: after
                type: orient
                dependsOn: [build]
            """);
        FlowCoordinator? coordinator = null;
        FlowInstanceState? mendAtAfter = null;
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"))
            .On("after", (FlowStepRun _) =>
            {
                mendAtAfter = coordinator!.Instances["mend"].State;
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());
        var cancel = TestContext.Current.CancellationToken;

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["build", "fix", "mend", "after"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, mendAtAfter);
        Assert.Equal(("fix", "mend"), (coordinator.Instances["build"].HandledBy, coordinator.Instances["fix"].HandledBy));
        Assert.Equal("done; 2 failures were handled: build, by fix, fix, by mend", end.Summary);
    }

    /// <summary>
    /// A handler's failure an answer decides settles the ending that handler answers through the one owner of a failure's
    /// settling: the handler's own handler at its bound, answered accept, takes the handler's failure as handled - and the
    /// ending of the step it answers, which waited on it alone, settles with it, so what depends on that step starts -
    /// never left waiting on a handler whose failure is settled. The answer is taken within the session, where no session's
    /// start reads the ending again.
    /// </summary>
    [Fact]
    public async Task AnAcceptAtAHandlersOwnBound_SettlesTheEndingThatHandlerAnswers()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
              - name: mend
                type: orient
                on: {step: fix, outcome: failure}
                maxVisits: 1
                then: build
              - name: other
                type: orient
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var other = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"))
            .OnAsync("other", async _ =>
            {
                await other.Task;
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        var advisor = new Asked();
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        // mend answered fix once and sent the run back; fix's second failure meets mend at its bound, while other runs.
        var session = coordinator.RunAsync(cancel);
        await advisor.Seen.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var bound = Assert.Single(coordinator.OpenQuestions);
        Assert.Equal(("visitBound", "fix+2"), (bound.Kind, bound.Instance));
        Assert.Equal(["build", "fix", "mend", "build+2", "fix+2"], runner.Started.Where(started => started != "other"));

        Assert.Equal(FlowHandedOver.Taken, (await coordinator.OfferAnswerAsync(bound.Id, "accept").WaitAsync(TimeSpan.FromSeconds(30), cancel)).How);
        other.SetResult();
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["build", "fix", "mend", "build+2", "fix+2", "after"], runner.Started.Where(started => started != "other"));
        Assert.Equal(bound.Id, coordinator.Instances["fix+2"].HandledBy);
        Assert.Equal("fix", coordinator.Instances["build+2"].HandledBy);
    }

    /// <summary>
    /// A source run again as a new attempt has an ending of its own, which raises its handlers anew: a handler an earlier
    /// attempt's ending raised answers that ending alone, so the new attempt's failure reads the handler's when again and
    /// raises its next visit - here one that runs, its when readable now - never taken as raised already and settled by the
    /// visit the earlier attempt left. The way in: a handler at its bound answered fail, the source's own onFailure pausing,
    /// and retry - the first attempt's ending had raised the handler whose when could not be read beside the bound's question.
    /// </summary>
    [Fact]
    public async Task ASourceRunAgainAsANewAttempt_RaisesItsHandlersAnewForThatAttemptsEnding()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: pause
              - name: note
                type: orient
                on: {step: build, outcome: failure}
                when: build.go
                onFailure: continue
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                maxVisits: 1
                then: build
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;

        // build fails every time; only its third run writes the go its handler note reads.
        var runner = new FakeRunner()
            .On("build", (int asked) => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)", output: Output(asked == 3 ? """{"go": true}""" : "{}")))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var bound = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");
        Assert.Equal("build+2", bound.Instance);
        Assert.True(coordinator.TryAnswer(bound.Id, "fail", out _));

        _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var failure = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "failure");
        Assert.True(coordinator.TryAnswer(failure.Id, "retry", out _));

        _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var again = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");
        Assert.True(coordinator.TryAnswer(again.Id, "accept", out _));

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The second attempt's ending raised note again - its when read anew, true now - and the visit ran; the one the
        // first attempt's ending raised stands as it ended, the answer of that ending alone.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["build", "fix", "build+2", "build+2", "note+3", "after"], runner.Started);
        var first = coordinator.Instances["note+2"];
        var raisedAgain = coordinator.Instances["note+3"];
        Assert.Equal((FlowInstanceState.Failed, 1), (first.State, first.RaisedByAttempt));
        Assert.Equal((FlowInstanceState.Done, 2), (raisedAgain.State, raisedAgain.RaisedByAttempt));
        Assert.Same(coordinator.Instances["build+2"], raisedAgain.RaisedBy);
        Assert.Equal((2, "note"), (coordinator.Instances["build+2"].Attempt, coordinator.Instances["build+2"].HandledBy));
        Assert.DoesNotContain(coordinator.Instances.Keys, id => id == "note+4");
    }

    /// <summary>
    /// The help, the README and the architecture say what the engine does with a handler's ending: what depends on a step
    /// that failed waits for its handlers to end, their failures decided, what depends on one that ended done starts beside
    /// them, a handler made again is a new visit that ending waits on, a source run again as a new attempt raises its
    /// handlers anew, a fail answer at a handler's bound gives the ending up, the attempt an older raise answered is read
    /// once, and what the recovery put right, a retry and a handler a stop left NotRun are made again once from the
    /// records - the rules the tests above hold, in the words a reader meets.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayAnEndingWaitsOnItsHandlersDecidedEnd_AndOnAVisitMadeAgain()
    {
        var workflows = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "the schedule never starts it. What depends on a step that failed waits for each handler its failure raised to end - one that failed once its failure is decided, never while it asks, the recovery works on it or the run it stopped is still to resume - while what depends on a step that ended done starts beside the handlers its success raised. A handler made again, a group answered retry or one the recovery put right, is a new visit raised by the same ending, which waits on it; and a step run again as a new attempt raises its handlers anew, for that attempt's own ending. A fail answer at a handler's bound after a failure gives that ending up: the failure stands, and the ending waits on none of its handlers any more.",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "failed-step, the step that gave it its failure, which runs again from where it stopped, once: the visit's own record says what it sent the run back to, whatever became of that since, and nothing is sent back into a work item a plan update is bringing to rest -",
            workflows,
            StringComparison.Ordinal);
        Assert.Contains(
            "A step that says `on` is a handler, raised by another step's ending: what depends on a step that failed waits for every handler of that ending to end - one that failed once its failure is decided, never while it asks, the recovery works on it or a run it stopped is still to resume - while what depends on a step that ended done starts beside the handlers its success raised. A handler that is a group answered `retry` is a new visit raised by the same ending, which waits on it, and a step run again as a new attempt raises its handlers anew, for that attempt's own ending. A `fail` answer at a handler's bound after a failure gives that ending up: the failure stands, and the ending waits on none of its handlers any more.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "`failed-step`, the step that gave it its failure, which runs again from where it stopped, a handler (a step that says `on`) as a new visit raised by the ending it answers, which waits on it - once: the visit's own record says what it sent the run back to, whatever became of that since, and nothing is sent back into a work item a plan update is bringing to rest -",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            $"What depends on a step that failed waits until every handler raised for that ending has ended (`FlowCoordinator.Ended`): one that failed once its failure is decided - passed over, settled by its own handlers or by an answer - never while it is owed to what decides it, asks a person, is the recovery's, or stopped the run, which the next session runs again; a failure is unsettled from its end until then, as a resume reads one owed. What depends on a step that ended done starts beside the handlers its success raised, for a success is settled as it ends. Every way a handler's visit ends settles the ending through the one owner (`FlowCoordinator.MaybeSettle`) - a failure its handlers or an answer took as handled through `FlowCoordinator.Settle`, which says what took it and settles with it the ending its instance answers - and a handler made again - a group answered `retry`, or one the recovery put right going on as `failed-step` - is a new visit raised by the same ending, naming the same source and the same attempt of it, which the ending waits on in the place of the one it replaced (`FlowCoordinator.RaiseAgain`), as a resume rebuilds it. A step run again as a new attempt has an ending of its own, which raises its handlers anew: each handler's visit records the attempt whose ending raised it (`{JsonNamingPolicy.CamelCase.ConvertName(nameof(FlowStepRecord.RaisedByAttempt))}`), and a raise answers that attempt's ending alone (`FlowCoordinator.RaisedFor`, which `FlowCoordinator.EndingOf` and `FlowInstance.{nameof(FlowInstance.RaisesOwed)}` read), so the new attempt's failure raises the handler's next visit, its `when` read again; a new attempt keeps none of the handler states the attempt before it ended with, nor an ending of it still waiting on them, nor what took its failure (`FlowCoordinator.NewAttempt`), and only a step that stands as an ending - done or failed - owes a raise or has an ending rebuilt (`FlowInstance.{nameof(FlowInstance.StandsAsAnEnding)}`). A `fail` answer at a handler's bound after a failure gives that ending up in the source's record, in one write: a handler it still owed is `passed`, one it raised is `left`, and the ending waits on none of them, in the session and at every resume (`FlowCoordinator.GiveUpTheEnding`). A raise an older build recorded names no attempt and is read as the attempt its source's record holds where that attempt ended and its failure is not owed - as the build that wrote it read it, matching a raise by its source's visit alone - or where a `fail` answer at a success bound (`visitBound`) ended that same attempt failed, and otherwise as the attempt before it, never before the first, for an ending raises its handlers only as it is decided (`FlowInstance.{nameof(FlowInstance.AttemptRaisedFor)}`). It is read once: the first session that takes the run up writes it into the visit's own record, held with what its start writes and before anything rewrites the source, and every session after - and the run's status facts (`{nameof(FlowStatus)}`) - reads that record, never deriving it again from a source a new attempt has moved on (`FlowCoordinator.WriteTheAttemptsRead`).",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"What a visit of the recovery that ended done puts right runs again as decided from the records, once: the visit's done record starts the list of what it sent the run back to (`{JsonNamingPolicy.CamelCase.ConvertName(nameof(FlowStepRecord.Reruns))}`), and each redirect it decides puts the step in that list in the redirect's own write - with the question the redirect asks where it would leave an agent open, so no crash and no answer taken at once loses the question - and the rerun is owed until that record names it; for a handler, until a visit is raised again for the same ending in its place. A crash between the visit's done record and the rerun's leaves it to the resume, which makes it once; whatever becomes of the redirect after - made, given up by a `stay`, or dropped, its question withdrawn, with a work item a plan update brings to rest (`FlowCoordinator.DropWhatItOwes`) - no session decides it again, for that one fact is what is read. A visit an older build recorded names no list: that build decided its redirect in the write after the visit's done record and never again at a resume - and a handler's not at all, for no redirect resets a handler - so whether it decided is read from what that redirect left in the records: the redirect still owed, a question it asked of the agents it would leave open - which a `stay` that gave it up answered - or the step inside a work item a plan update is bringing or brought to rest, which dropped it; one made left the step, which no rerun goes back over. None of those is the crash between the two writes, which leaves the rerun undecided, and the resume sends the run back once. The visit's own record tells those shapes apart, never a question's state: that build wrote the question a redirect asks before the redirect, so a crash between the two leaves the question open naming the visit and no redirect owed, and the resume, withdrawing it as one that asks of nothing, starts the visit's list, empty - written at the session's start before the withdrawal (`FlowCoordinator.WriteTheListsStarted`) - so the rerun is decided again, asking anew, never read as decided by the question withdrawn; and a drop of such a visit's redirect starts its list in the drop's own write, naming what the redirect sent back. Nothing is sent back into a work item a plan update is bringing or brought to rest, whichever way the visit's record says what it decided. Whether a rerun is decided has that one owner, which the rerun of a failed step and of a reporter both ask (`FlowCoordinator.RerunDecided`). A reporter of an issue a visit fixed runs again by the same fact of the visit that fixed it last, once - a `stay` an older build's visit was given is never asked again when another triage of the reporter ends - and never into a work item a plan update is bringing or brought to rest, whose work is done again in the item visited in its place (`FlowCoordinator.RerunOwed`, `FlowCoordinator.RunTheReporterAgain`, `FlowCoordinator.GoOnFromTheRecovery`). An answer the records hold and the session never carried out - a `stay` written before a crash - finds its redirect's question restored with the redirect, and is carried out on it at the resume. A redirect and the question it asks are one fact: a redirect dropped - with a work item a plan update brings to rest, or with a visit a redirect leaves, whose redirects still owed go with it, never left asking a question withdrawn (`FlowCoordinator.Leave`) - leaves the run's records in one write before its question is withdrawn (`FlowCoordinator.DropRedirectsOf`, the one owner of dropping a redirect), so a crash between the two leaves a question that asks of no owed redirect, which the resume withdraws; a redirect whose record names a question withdrawn - an older build withdrew the question before it wrote the drop, or left the redirect of a visit a redirect left owed - is never made; and one whose record names no question in play while the question its handler asked for it is open - the process ended between that question's write and the record that would name it: an older build's first ask, which it named only in the write after, or a question asked again after `retry` - waits on that question. A handler group made again for a retry is made before its failure stops standing - a crash between the two writes leaves that failure beside the new visit, which is the retry made - while a group the schedule draws says its retry in the very write that settles its failure (`{JsonNamingPolicy.CamelCase.ConvertName(nameof(FlowStepRecord.Retried))}`): the schedule, the one owner of drawing a group - its dependencies, its `when`, what holds it back - draws the new visit from that record, so a crash before the draw leaves the retry owed, which the resume's schedule draws, never a failure gone with no new visit owed for it; and a visit of the recovery answered `retry` is taken again only while it is still the newest taken for what it puts right (`FlowCoordinator.Retry`). A handler's visit a raise made that a failure's `stop` left NotRun before it started - the run ended before it - is put back by the resume that goes on with no failure standing to stop it, not started again under the same id and raised by the same ending, where that ending still waits on it: the schedule never draws a handler, and the visit never ran, held nothing and wrote nothing but its place in that ending (`FlowCoordinator.RedrawWhatTheStopLeft`).",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "An ending the records leave waiting - a handler's own among them, which the ending it answers waits on, and each raise it still owes made - is rebuilt as its raise made it, from its source's record: the visit of each handler that record holds `raised` - the newest raised for that ending, so a visit made again in a failed one's place is the one waited on - in the order the workflow declares them (`FlowCoordinator.EndingOf`)",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>An advisor that recommends nothing, and tells the test a question was asked of it.</summary>
    private sealed class Asked : IFlowAdvisorRunner
    {
        private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once a question has been asked of the advisor: it is open.</summary>
        public Task Seen => _seen.Task;

        /// <inheritdoc/>
        public Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken)
        {
            _ = _seen.TrySetResult();
            return Task.FromResult(new FlowAdvice(Answer: null, Why: null, Text: null, NotObtained: "no advice in this test"));
        }
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
        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
            => inner.Question(questions, refused);

        public void Updates(FlowUpdatesFile updates)
            => inner.Updates(updates);

        public void AffectedTests(IReadOnlyList<FlowAffectedTests> entries)
            => inner.AffectedTests(entries);

        public void Flush()
            => inner.Flush();
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

        /// <summary>Answers <paramref name="step"/> by a script that may hold the step open, on the task it answers.</summary>
        public Runner OnAsync(string step, Func<FlowStepRun, Task<FlowStepResult>> script)
        {
            _scripts[step] = script;
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

    /// <summary>
    /// A handler that is a group is made running in its first record: a crash right after that record is written leaves
    /// a visit the resume takes up, whose block is drawn and run - never one that never starts, holding the ending it was
    /// raised for open for ever.
    /// </summary>
    [Fact]
    public async Task AGroupHandlersFirstRecord_IsOneTheResumeRuns_ACrashRightAfterItNeverLeavesItUnstarted()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: orient
              - name: mend
                on: {step: work, outcome: failure}
                steps:
                  - name: fix
                    type: orient
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var runner = new Runner()
            .On("work", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, _ => false, picksStep: (instance, _) => instance == "mend");

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(TestContext.Current.CancellationToken);
            Assert.True(crashing.Ended);
        }

        // On disk: the handler's one record, and nothing of its block.
        Assert.Equal(nameof(FlowInstanceState.Running), harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "mend").State);
        Assert.DoesNotContain(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance == "mend.fix");

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner().On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["mend.fix", "after"], again.Started);
            Assert.Equal(FlowInstanceState.Done, resumed.Instances["mend"].State);
            Assert.Equal("mend", resumed.Instances["work"].HandledBy);
        }
    }

    /// <summary>
    /// A handler that is a group whose record says it never started - its block never begun, as one made in two writes
    /// with a crash between them is left - is started by the resume, which begins its block as its raise would have, and
    /// its block runs: the ending it was raised for is never held open by a visit that cannot start.
    /// </summary>
    [Fact]
    public async Task AGroupHandlerItsRecordLeftUnstarted_IsStartedByTheResume_AndItsBlockRuns()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: orient
              - name: mend
                on: {step: work, outcome: failure}
                steps:
                  - name: fix
                    type: orient
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var runner = new Runner()
            .On("work", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, _ => false, picksStep: (instance, _) => instance == "mend");

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(TestContext.Current.CancellationToken);
        }

        // The records as a visit made in two writes, cut between them, leaves them: not started, in its own record and
        // in the run's state alike.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        harness.OrchestrationStore.WriteFlow(layout, flow with
        {
            Steps = [.. flow.Steps.Select(step => step.Instance == "mend" ? step with { State = nameof(FlowInstanceState.NotStarted) } : step)],
        });
        harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile("mend"), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("mend"))! with { State = nameof(FlowInstanceState.NotStarted) });

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner().On("fix", FlowStepResult.Ended(passed: true, HarnessExit.Success));

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["mend.fix", "after"], again.Started);
            Assert.Equal(FlowInstanceState.Done, resumed.Instances["mend"].State);
        }
    }

    /// <summary>
    /// A raise is said in its source's own record before the handler's visit's first record is written, so a crash
    /// between the two leaves the raise said and no visit of it - never a raise owed beside the visit it made: the run's
    /// status reads the raise as still owed, named by the visit the resume raises, and the resume makes it once, the
    /// handler run and the failure it was raised for handled.
    /// </summary>
    [Fact]
    public async Task ARaiseIsSaidBeforeItsVisitIsRecorded_ACrashBetweenLeavesItOwed_AndTheResumeMakesItOnce()
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
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new Runner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        // The process ends at the write that says the raise while no visit of it is made yet.
        var crashing = new CrashingRecords(records, _ => false, picksRun: run =>
            run.Steps.Any(step => step.Instance == "build" && step.Handlers?.Contains(new FlowHandlerState("fix", "raised")) == true)
            && run.Steps.All(step => step.Instance != "fix"));

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(TestContext.Current.CancellationToken);
            Assert.True(crashing.Ended);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;

        Assert.Equal([new FlowHandlerState("fix", "raised")], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build"))!.Handlers);
        Assert.DoesNotContain(flow.Steps, step => step.Instance == "fix");
        Assert.Equal(
            "left      at least 1 step instance, at most 2 step instances (the engine's bounds); 1 handler an ending owes a raise: fix",
            FlowStatus.Facts(definition, flow, questions: null, updates: null, new FlowEngineState(FlowEngineState.None, null, []), clock.GetUtcNow())
                .First(fact => fact.StartsWith("left", StringComparison.Ordinal)));

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
            Assert.Equal(["fix", "after"], again.Started);
        }

        Assert.Equal([new FlowHandlerState("fix", "done")], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build"))!.Handlers);
    }

    /// <summary>
    /// A record an older build left holding a raise owed beside the visit it made - it wrote the handler's visit before its
    /// source's raise, and a crash came between the two - is read raised, as this build writes it: the visit is that
    /// raise, run once and never raised again beside it, and the failure it was raised for is handled.
    /// </summary>
    [Fact]
    public async Task AnOlderRecordHoldingARaiseOwedBesideItsVisit_IsReadRaised_TheVisitRunOnce()
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
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new Runner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, _ => false, picksStep: (instance, _) => instance == "fix");

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(TestContext.Current.CancellationToken);
            Assert.True(crashing.Ended);
        }

        // The records as the older build left them, the raise owed, in the source's own record and in the run's state alike,
        // and the handler's visit naming no attempt of its source: an ending decided, whose raise is read as the attempt the
        // source's record holds (FlowInstance.AttemptRaisedFor).
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        harness.OrchestrationStore.WriteFlow(layout, flow with
        {
            Steps = [.. flow.Steps.Select(step => step.Instance switch
            {
                "build" => step with { Handlers = [new FlowHandlerState("fix", "owed")] },
                "fix" => step with { RaisedByAttempt = null },
                _ => step,
            })],
        });
        harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile("build"), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build"))! with { Handlers = [new FlowHandlerState("fix", "owed")] });
        harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile("fix"), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("fix"))! with { RaisedByAttempt = null });
        Assert.Equal(nameof(FlowInstanceState.NotStarted), harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "fix").State);

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
            Assert.Equal(["fix", "after"], again.Started);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "fix+2");
        }

        Assert.Equal([new FlowHandlerState("fix", "done")], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build"))!.Handlers);
    }

    /// <summary>
    /// No write of the run's state holds a raise owed beside the visit it raised, across a crash and the resume that goes on
    /// from it: an ending with two handlers, cut off after the first one's visit was recorded, is taken up - the raise made
    /// written raised in the first write the resume makes of its source, the other raised said before its visit is - and
    /// both handlers run once, the failure handled.
    /// </summary>
    [Fact]
    public async Task NoWriteHoldsARaiseOwedBesideTheVisitItRaised_AcrossACrashAndItsResume()
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
              - name: note
                type: orient
                on: {step: build, outcome: failure}
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new Runner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var both = new List<string>();

        // Each write is looked at whole: a source's handler written owed while a visit of it that source raised is in the
        // same write.
        void Look(FlowSnapshot run)
        {
            foreach (var source in run.Steps.Where(step => step.Handlers is not null))
            {
                both.AddRange(source.Handlers!
                    .Where(handler => handler.State == "owed" && run.Steps.Any(visit => visit.RaisedBy == source.Instance && FlowInstanceId.PathOf(visit.Instance) == handler.Handler))
                    .Select(handler => $"{source.Instance}: {handler.Handler}"));
            }
        }

        var crashing = new CrashingRecords(new Inspected(records, Look), _ => false, picksStep: (instance, _) => instance == "fix");

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(TestContext.Current.CancellationToken);
            Assert.True(crashing.Ended);
        }

        Assert.Equal([new FlowHandlerState("note", "owed"), new FlowHandlerState("fix", "raised")], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build"))!.Handlers);

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: new Inspected(resumedRecords, Look), resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["fix", "note", "after"], again.Started);
        }

        Assert.Empty(both);
    }

    /// <summary>The run's records, each write passed on after the test has looked at the run it writes whole.</summary>
    private sealed class Inspected(IFlowRecords inner, Action<FlowSnapshot> look) : IFlowRecords
    {
        /// <inheritdoc/>
        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            look(snapshot);
            inner.Write(kind, snapshot, instance, from, to, attempt, detail);
        }

        /// <inheritdoc/>
        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
            => inner.Question(questions, refused);

        /// <inheritdoc/>
        public void Updates(FlowUpdatesFile updates)
            => inner.Updates(updates);

        /// <inheritdoc/>
        public void AffectedTests(IReadOnlyList<FlowAffectedTests> entries)
            => inner.AffectedTests(entries);

        /// <inheritdoc/>
        public string NameAgent(IReadOnlyCollection<string> recorded)
            => inner.NameAgent(recorded);

        /// <inheritdoc/>
        public void Flush()
            => inner.Flush();

        /// <inheritdoc/>
        public void Stand(FlowStartsStanding standing, IReadOnlyCollection<string> leftSought)
            => inner.Stand(standing, leftSought);

        /// <inheritdoc/>
        public void Unstand()
            => inner.Unstand();
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);

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

        // The report says what handled the failure, apart from the steps passed over: the failed attempt by its id - the
        // step and its attempt - with its code, its error and the handler that took it.
        var left = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, flow), "done; 1 failure was handled: build, by fix", 0, clock.GetUtcNow());
        var report = harness.FileSystem.ReadAllText(layout.ReportFile).Replace("\r\n", "\n");

        Assert.EndsWith("\n## Errors\n\n- build@1, exit 20: leg 'windows' ended Red (20) (handled by fix)\n", report, StringComparison.Ordinal);
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: watched);
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            // The redirect is owed once the handler has ended done and named the group in flight.
            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt");
            _ = runner.Release("member");
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Single(harness.OrchestrationStore.ReadFlow(layout)?.Redirects ?? []);
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);

        using (var again = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, new Runner(), new RunnerFacts(), clock, records: again, resume: taken);
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            // The redirect waits for slow, a member of its set in flight; the hard stop stops slow and makes nothing.
            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestStop();
            var end = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Stopped, end.Status);
            Assert.Equal("implement", Assert.Single(harness.OrchestrationStore.ReadFlow(layout)?.Redirects ?? []).Target);
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner()
            .On("verify", run => FlowStepResult.Ended(passed: true, HarnessExit.Success));

        using (var records2 = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: records2, resume: taken);
            var done = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, done.Status);
            Assert.Equal(["implement+2", "verify+2", "slow+2"], again.Started);
            Assert.Equal(FlowInstanceState.Skipped, resumed.Instances["slow"].State);
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)?.Redirects);
        }
    }

    /// <summary>
    /// A crash right after a handler's failed end is written - its failure owed, the recovery its policy - leaves records
    /// the resume completes to the run the session would have made: the recovery takes the failure, the handler runs
    /// again as a new visit raised by the ending it answers, and that ending settles as the new visit ends - never what
    /// depends on the source failed as unable to start beside an ending that waits for ever. One outcome whether or not
    /// a crash fell between the two writes.
    /// </summary>
    [Fact]
    public async Task ACrashAfterAHandlersFailedEnd_TheResumeRecoversItAndRunsItAgain_AsTheSessionWould()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: fold
                type: orient
              - name: reconcile
                type: orient
                on: {step: fold, outcome: failure}
                onFailure: recover
              - name: after
                type: orient
                dependsOn: [fold]
            """);
        var runner = new Runner()
            .On("fold", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("reconcile", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, _ => false, picksStep: (instance, to) => instance == "reconcile" && to == nameof(FlowInstanceState.Failed));

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.True(crashing.Ended);
        }

        // On disk: the handler's failure owed, and nothing after it.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        Assert.Equal(FlowStepRecord.NameOf(FlowFailure.Owed), flow.Steps.Single(step => step.Instance == "reconcile").Failure);
        Assert.DoesNotContain(flow.Steps, step => step.Instance is "recover" or "after");

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: fold, by reconcile", end.Summary);
            Assert.Equal(["recover.mend", "reconcile+2", "after"], again.Started);
            Assert.Same(resumed.Instances["fold"], resumed.Instances["reconcile+2"].RaisedBy);
            Assert.Equal("reconcile", resumed.Instances["fold"].HandledBy);
        }
    }

    /// <summary>
    /// A handler that is a group whose failure stopped the run has not ended for the ending it answers - the resume runs
    /// it again, as a group stands open over such a member - so the source is not counted handled when the run stops,
    /// and the resume opens the handler again as a new visit raised by that ending, which the ending waits on: what
    /// depends on the source starts once it has ended.
    /// </summary>
    [Fact]
    public async Task AGroupHandlerWhoseFailureStoppedTheRun_IsRaisedAgainByTheResume_ItsEndingWaitingOnIt()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                on: {step: build, outcome: failure}
                onFailure: stop
                steps:
                  - name: attempt
                    type: orient
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new Runner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("attempt", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var stopped = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Failed, stopped.Status);
            Assert.Equal("failed; 1 step failed: fix", stopped.Summary);
            Assert.Equal(["build", "fix.attempt"], runner.Started);
            Assert.Null(coordinator.Instances["build"].HandledBy);
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
            Assert.Equal(["fix+2.attempt", "after"], again.Started);
            Assert.Same(resumed.Instances["build"], resumed.Instances["fix+2"].RaisedBy);
            Assert.Equal(FlowInstanceState.Failed, resumed.Instances["fix"].State);
        }
    }

    /// <summary>
    /// The ending of a handler is rebuilt by the resume as any ending is: a handler whose failure its own handler takes
    /// has ended for its source only once that ending settles, so a stop while the second handler runs leaves both endings
    /// waiting, and the resume rebuilds each from its source's record - the second handler runs again, the first's
    /// failure is handled, and the first source settles with it - never what depends on the first source failed as unable
    /// to start beside an ending nothing rebuilt.
    /// </summary>
    [Fact]
    public async Task AHandlersOwnEnding_IsRebuiltByTheResume_AndSettlesTheEndingItAnswers()
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
              - name: mend
                type: orient
                on: {step: fix, outcome: failure}
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new Runner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("fix", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"));
        runner.HoldHearingTheStop("mend");
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var watched = new Watched(records, snapshot => snapshot.Steps.Any(step => step.Instance == "mend" && step.State == FlowInstanceState.Running));
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestStop();
            var stopped = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Stopped, stopped.Status);
            Assert.Equal(["build", "fix", "mend"], runner.Started);
            Assert.Null(coordinator.Instances["build"].HandledBy);
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 2 failures were handled: build, by fix, fix, by mend", end.Summary);
            Assert.Equal(["mend", "after"], again.Started);
        }
    }

    /// <summary>
    /// A raise a handler's own ending owes is made by the resume as any ending's is: a handler that ends done while a
    /// pause records what it left running is written owing its own handler, raises nothing then, and the session that
    /// goes on - another engine, from the records alone - raises it once, never leaving it unraised for ever.
    /// </summary>
    [Fact]
    public async Task ARaiseAHandlersEndingOwesAtAPause_IsMadeByTheResume()
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
              - name: note
                type: orient
                on: {step: fix, outcome: success}
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new Runner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        runner.Hold("fix");
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var watched = new Watched(records, snapshot => snapshot.Steps.Any(step => step.Instance == "fix" && step.State == FlowInstanceState.Running));
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            await watched.Seen.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt");
            _ = runner.Release("fix");
            var paused = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            Assert.Equal(["build", "fix"], runner.Started);
            Assert.Equal(
                new FlowHandlerState("note", "owed"),
                Assert.Single(harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "fix").Handlers ?? []));
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["after", "note"], again.Started.Order(StringComparer.Ordinal));
            Assert.Same(resumed.Instances["fix"], resumed.Instances["note"].RaisedBy);
            Assert.Equal(FlowInstanceState.Done, resumed.Instances["note"].State);
        }
    }

    /// <summary>
    /// A group handler answered retry between sessions has its new visit, raised by the ending it answers, in the run's
    /// records; a resume that takes the run up from them rebuilds that ending waiting on the new visit - the one reading
    /// of an ending's handlers, the newest visit raised for it - never on the failed one it replaced, which a resume reads
    /// back unsettled and would hold the ending for ever.
    /// </summary>
    [Fact]
    public async Task AGroupHandlersRetryInTheRecords_IsWhatTheResumesRebuiltEndingWaitsOn()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                on: {step: build, outcome: failure}
                onFailure: pause
                steps:
                  - name: attempt
                    type: orient
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new Runner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("attempt", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var paused = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            Assert.True(coordinator.TryAnswer(Assert.Single(coordinator.OpenQuestions).Id, "retry", out var problem), problem);
        }

        // The new visit is in the records, raised by build, its block not begun running yet.
        var written = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "fix+2");
        Assert.Equal((nameof(FlowInstanceState.Running), "build"), (written.State, written.RaisedBy));

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
            Assert.Equal(["fix+2.attempt", "after"], again.Started);
            Assert.Equal(FlowInstanceState.Failed, resumed.Instances["fix"].State);
        }
    }

    /// <summary>
    /// Which attempt's ending raised a handler's visit is in that visit's own record, so a resume reads the raise as the
    /// session that made it held it: a source run again as a new attempt, its failure asking at the handler's bound when
    /// the session ends, is taken up by another engine from the records alone, and the answer raises the handler anew for
    /// the new attempt's ending - its next visit, which runs - never the visit the earlier attempt's ending raised.
    /// </summary>
    [Fact]
    public async Task ARaisesAttemptIsInItsRecord_TheResumeRaisesTheHandlerAnewForANewAttemptsEnding()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: pause
              - name: note
                type: orient
                on: {step: build, outcome: failure}
                when: build.go
                onFailure: continue
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                maxVisits: 1
                then: build
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var asked = 0;
        var runner = new Runner()
            .On("build", (FlowStepRun _) => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)", output: JsonNode.Parse(++asked == 3 ? """{"go": true}""" : "{}")));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);

            foreach (var (kind, answer) in new[] { ("visitBound", "fail"), ("failure", "retry") })
            {
                _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
                Assert.True(coordinator.TryAnswer(Assert.Single(coordinator.OpenQuestions, question => question.Kind == kind).Id, answer, out var problem), problem);
            }

            var paused = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            Assert.Equal(["build", "fix", "build+2", "build+2"], runner.Started);
        }

        // On disk: the visit the first attempt's ending raised says so, and the second attempt's ending asks at the bound.
        Assert.Equal(("build+2", 1), (harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("note+2"))!.RaisedBy, harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("note+2"))!.RaisedByAttempt));
        Assert.Equal(2, harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build+2"))!.Attempt);

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var bound = Assert.Single(resumed.OpenQuestions);
            Assert.Equal(("visitBound", "build+2"), (bound.Kind, bound.Instance));
            Assert.True(resumed.TryAnswer(bound.Id, "accept", out var problem), problem);
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["note+3", "after"], again.Started);
            Assert.Equal((FlowInstanceState.Done, 2), (resumed.Instances["note+3"].State, resumed.Instances["note+3"].RaisedByAttempt));
            Assert.Equal("note", resumed.Instances["build+2"].HandledBy);
        }

        Assert.Equal(2, harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("note+3"))!.RaisedByAttempt);
    }

    /// <summary>
    /// The rerun a visit of the recovery that ended done owes the step it put right is decided from the records: a crash
    /// right after the visit's done record leaves the rerun owed - no redirect of it in the records, the step still its
    /// scope's visit - and the resume decides it, the step running again as a new visit and what depends on it after; a
    /// crash right after the redirect's own record leaves the rerun made, and the resume makes it once - never a second
    /// redirect beside the one the records hold.
    /// </summary>
    [Theory]
    [InlineData("the visit's done record")]
    [InlineData("the redirect's record")]
    public async Task ARecoveryDonesRerunOfAStep_IsDecidedFromTheRecords_TheResumeMakingItOnce(string crashAfter)
    {
        using var temp = new TempDirectory();
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
        var runner = new Runner().On("build", (FlowStepRun run) => run.Visit == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = crashAfter == "the redirect's record"
            ? new CrashingRecords(records, _ => false, picksRun: run => run.Redirects?.Any(redirect => redirect.Handler == "recover") == true)
            : new CrashingRecords(records, _ => false, picksStep: (instance, to) => instance == "recover" && to == nameof(FlowInstanceState.Done));
        var cancel = TestContext.Current.CancellationToken;

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(crashing.Ended);
        }

        // On disk: the visit done, the step it put right failed and its scope's visit still, and no new visit of it.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        Assert.Equal(nameof(FlowInstanceState.Done), flow.Steps.Single(step => step.Instance == "recover").State);
        Assert.Equal(crashAfter == "the redirect's record" ? new[] { "recover" } : [], (flow.Redirects ?? []).Select(redirect => redirect.Handler));
        Assert.DoesNotContain(flow.Steps, step => step.Instance is "build+2" or "after");

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner().On("build", FlowStepResult.Ended(passed: true, HarnessExit.Success));

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["build+2", "after"], again.Started);
            Assert.Equal("recover", resumed.Instances["build"].LeftBy);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "build+3");
        }

        // One redirect decided over the whole run, by whichever session made it: none beside the one the records held.
        Assert.Equal(
            ["the run is sent to 'build' by 'recover'"],
            FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Select(read => read.Detail).Where(detail => detail?.StartsWith("the run is sent to ", StringComparison.Ordinal) == true));
    }

    /// <summary>
    /// A visit of the recovery an older build recorded keeps no list of what it sent the run back to - that build decided
    /// the redirect as the visit ended, in the write after its done record, and never again at a resume - so the rerun is
    /// read from what that redirect left in the records: the process ended between the two writes, the step still failed,
    /// its failure counted handled by nothing, its scope's visit still and no redirect of it owed, leaves the rerun
    /// undecided, and the resume sends the run back to the step, which runs again with what depends on it after - never a
    /// failure taken as put right with nothing run again, what depends on it failed as unable to start; and a redirect that
    /// build wrote is made once, as it stood, never a second beside it.
    /// </summary>
    [Theory]
    [InlineData("the visit's done record")]
    [InlineData("the redirect's record")]
    public async Task AnOlderBuildsRecoveryVisitThatEndedDone_IsReadFromWhatItsRedirectLeft_TheResumeMakingTheRerunOnce(string crashAfter)
    {
        using var temp = new TempDirectory();
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
        var runner = new Runner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = crashAfter == "the redirect's record"
            ? new CrashingRecords(records, _ => false, picksRun: run => run.Redirects?.Any(redirect => redirect.Handler == "recover") == true)
            : new CrashingRecords(records, _ => false, picksStep: (instance, to) => instance == "recover" && to == nameof(FlowInstanceState.Done));
        var cancel = TestContext.Current.CancellationToken;

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(crashing.Ended);
        }

        // The records as the older build left them: the visit's record keeps no list of reruns.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        harness.OrchestrationStore.WriteFlow(layout, flow with { Steps = [.. flow.Steps.Select(step => step with { Reruns = null })] });
        harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile("recover"), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))! with { Reruns = null });
        var build = flow.Steps.Single(step => step.Instance == "build");
        Assert.Equal((nameof(FlowInstanceState.Failed), null), (build.State, build.HandledBy));
        Assert.Equal(crashAfter == "the redirect's record" ? new[] { "recover" } : [], (flow.Redirects ?? []).Select(redirect => redirect.Handler));
        Assert.DoesNotContain(flow.Steps, step => step.Instance is "build+2" or "after");

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal(["build+2", "after"], again.Started);
            Assert.Equal("recover", resumed.Instances["build"].LeftBy);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "build+3");
        }

        // One redirect over the whole run, by whichever session made it; the visit's record starts its list with the one
        // this build decided, and holds none where the older build's redirect was written.
        Assert.Equal(
            ["the run is sent to 'build' by 'recover'"],
            FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Select(read => read.Detail).Where(detail => detail?.StartsWith("the run is sent to ", StringComparison.Ordinal) == true));
        string[]? reruns = crashAfter == "the redirect's record" ? null : ["build"];
        Assert.Equal(reruns, harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))!.Reruns);
    }

    /// <summary>
    /// A redirect a visit of the recovery an older build recorded asked about and was given up by <c>stay</c> is decided:
    /// the question it asked says so, though the visit's record keeps no list of reruns - so the session that takes the run
    /// up asks nothing again and resets nothing, and what depends on the step runs on what the recovery left.
    /// </summary>
    [Fact]
    public async Task AnOlderBuildsRedirectGivenUpByStay_IsNeverDecidedAgain_AtTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: round
                onFailure: recover
                steps:
                  - name: worker
                    type: create-agent
                    agent: maker
                  - name: check
                    type: orient
                    dependsOn: [worker]
              - name: after
                type: orient
                dependsOn: [round]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var paused = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            var asked = Assert.Single(coordinator.OpenQuestions);
            Assert.Equal(("openAgents", "recover"), (asked.Kind, asked.Instance));
            Assert.True(coordinator.TryAnswer(asked.Id, "stay", out var problem), problem);
        }

        // The records as the older build left them: the visit's record keeps no list of reruns.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        harness.OrchestrationStore.WriteFlow(layout, flow with { Steps = [.. flow.Steps.Select(step => step with { Reruns = null })] });
        harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile("recover"), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))! with { Reruns = null });
        Assert.Equal("recover", flow.Steps.Single(step => step.Instance == "round").HandledBy);

        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.True(FlowRunStatus.Done == end.Status, $"{end.Summary} | asked: {string.Join("; ", resumed.OpenQuestions.Select(question => $"{question.Kind}: {question.Problem}"))}");
            Assert.Equal(["after"], again.Started);
            Assert.Null(resumed.Instances["round"].LeftBy);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "round+2");
        }

        Assert.Single(harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions, question => question.Kind == "openAgents");
    }

    /// <summary>
    /// A redirect a visit of the recovery an older build recorded decided asks of the agent it would leave open, and that
    /// build wrote the question before the redirect that names it: the process ended between the two leaves the visit done
    /// keeping no list of reruns, the question open naming the visit, and no redirect owed. The question asks of nothing
    /// the run owes and the resume withdraws it, and the visit's own record tells that shape apart, never the question's
    /// state: the resume starts the visit's list, written before the withdrawal, so the rerun reads as undecided - never as
    /// decided by the question it withdrew, the step left failed and what depends on it never run - and is decided again,
    /// asking anew; a stay given to that question lets what depends on the step run. A crash between the list's write and
    /// the withdrawal's leaves the next resume to read the same.
    /// </summary>
    /// <param name="crashAtTheWithdrawal">Whether the session that takes the run up ends at the write that withdraws the question, and another takes it up after.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOlderBuildsRedirectWhoseQuestionWasWrittenFirst_IsDecidedAgain_NeverReadAsDecidedByTheQuestionWithdrawn(bool crashAtTheWithdrawal)
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: round
                onFailure: recover
                steps:
                  - name: worker
                    type: create-agent
                    agent: maker
                  - name: check
                    type: orient
                    dependsOn: [worker]
              - name: after
                type: orient
                dependsOn: [round]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            // The process ends at the write of the question, before the record of the redirect that asks it.
            var crashing = new CrashingRecords(records, _ => false, picksQuestions: questions => questions.Any(question => question.Kind == "openAgents"));
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(crashing.Ended);
        }

        // The records as the older build left them: the visit's record keeps no list of reruns, the question is open, and
        // no redirect is owed.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        harness.OrchestrationStore.WriteFlow(layout, flow with { Steps = [.. flow.Steps.Select(step => step with { Reruns = null })] });
        harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile("recover"), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))! with { Reruns = null });
        Assert.True(flow.Redirects is null or []);
        var first = Assert.Single(harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions, question => question.Kind == "openAgents");
        Assert.Equal("open", first.State);

        if (crashAtTheWithdrawal)
        {
            using var crashed = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock);
            var crashing = new CrashingRecords(crashed, _ => false, picksQuestions: questions => questions.Any(question => question.Id == first.Id && question.State == "withdrawn"));
            _ = await TestAdvice.Coordinator(definition, new Runner(), new RunnerFacts(), clock, records: crashing, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition)).RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(crashing.Ended);

            // The visit's list was written before the question's withdrawal, which the process ended at.
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))!.Reruns));
        }

        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));

            // The rerun is decided again: the redirect back to the group asks anew, the question the process cut off withdrawn.
            var paused = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            var asked = Assert.Single(resumed.OpenQuestions);
            Assert.Equal(("openAgents", "recover"), (asked.Kind, asked.Instance));
            Assert.NotEqual(first.Id, asked.Id);
            Assert.True(resumed.TryAnswer(asked.Id, "stay", out var problem), problem);

            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal(["after"], again.Started);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "round+2");
        }

        // The question cut off stands withdrawn beside the one the rerun asked, and the visit's list says the rerun decided.
        Assert.Equal(
            [(true, "withdrawn"), (false, "answered")],
            harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions.Where(question => question.Kind == "openAgents").Select(question => (question.Id == first.Id, question.State)));
        Assert.Equal<IEnumerable<string>>(["round"], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))!.Reruns!);
    }

    /// <summary>
    /// A redirect whose handler an earlier redirect leaves goes with that visit, dropped with the question it asks: the
    /// handler <c>fix</c>, in the group <c>work</c> another handler's redirect resets, ends while that redirect waits for
    /// quiet, and its own redirect back to <c>make</c> - which would leave the agent that group made open - asks; the earlier
    /// redirect is then made, and leaving <c>fix</c> drops its redirect and withdraws the question, in that order - never a
    /// redirect left owed asking a question withdrawn, which would hold it and everything it holds back for ever in the
    /// session, and be made from a visit the run left at a resume, resetting <c>make</c> and what depends on it. The new
    /// visit of <c>work</c> runs and the run ends done, whether in the session, across a resume, or across a resume from the
    /// records an older build left - its redirect still owed beside the question it withdrew, which the resume never makes.
    /// </summary>
    [Theory]
    [InlineData("in the session")]
    [InlineData("across a resume")]
    [InlineData("across a resume from an older build's records")]
    public async Task ARedirectWhoseHandlerAnEarlierRedirectLeaves_IsDroppedWithItsQuestion_AndNeverMade(string where)
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: make
                steps:
                  - name: worker
                    type: create-agent
                    agent: maker
              - name: work
                dependsOn: [make]
                steps:
                  - name: check
                    type: orient
                  - name: fix
                    type: orient
                    on: {step: check, outcome: failure}
                    then: make
                  - name: slow
                    type: orient
                    dependsOn: [check]
              - name: watch
                type: orient
                dependsOn: [make]
              - name: back
                type: orient
                on: {step: watch, outcome: failure}
                then: work
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner()
            .On("watch", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("check", run => run.Instance == "work.check"
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        runner.Hold("fix");
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        // Across a resume the process ends at the write that says back's redirect made: work left, the redirect gone.
        IFlowRecords written = where == "in the session"
            ? records
            : new CrashingRecords(records, _ => false, picksRun: run => run.Steps.Any(step => step is { Instance: "work", LeftBy: "back" }) && run.Redirects?.Any(redirect => redirect.Handler == "back") != true);

        using (records)
        {
            // fix ends once back's redirect to work is owed, waiting for fix to end.
            var watched = new Watched(written, run => run.Redirects?.Any(redirect => redirect.Handler == "back") == true);
            _ = watched.Seen.ContinueWith(_ => runner.Release("fix"), TaskScheduler.Default);
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: watched);
            var ended = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.True(FlowRunStatus.Done == ended.Status, ended.Summary);
            Assert.Equal("back", coordinator.Instances["work"].LeftBy);
            Assert.Null(coordinator.Instances["make"].LeftBy);
            Assert.True(written is not CrashingRecords crashing || crashing.Ended);
        }

        // On disk: fix's redirect was dropped and its question withdrawn - one fact, gone together.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        Assert.True(flow.Redirects is null or []);
        var asked = Assert.Single(harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions, question => question.Kind == "openAgents");
        Assert.Equal(("withdrawn", "work.fix"), (asked.State, asked.RaisedBy?.Step));

        if (where == "across a resume from an older build's records")
        {
            // The records as an older build left them: fix's redirect owed still, as it was decided, naming the question it
            // withdrew.
            harness.OrchestrationStore.WriteFlow(layout, flow with { Redirects = [new FlowRedirectRecord("work.fix", "make", ["make", "work", "watch"], asked.Id)] });
        }

        if (where != "in the session")
        {
            var again = new Runner();

            using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
            {
                var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
                var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

                Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
                Assert.Equal(["work+2.check", "work+2.slow"], again.Started);
                Assert.Null(resumed.Instances["make"].LeftBy);
            }
        }

        // make was never reset: no redirect but back's was ever made.
        Assert.Equal(
            ["the run was sent to 'work', resetting 1 of its scope"],
            FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Select(read => read.Detail).Where(detail => detail?.StartsWith("the run was sent to ", StringComparison.Ordinal) == true));
    }

    /// <summary>
    /// The rerun a visit of the recovery that ended done owes a handler it put right is decided from the records too: a
    /// crash right after the visit's done record leaves the handler's failed visit the newest raised for the ending it
    /// answers, and the resume raises it again - a new visit naming the same source, which that ending waits on, the
    /// failure it answers handled once it has ended; a crash right after the new visit's first record leaves the rerun
    /// made, and the resume raises nothing more - the new visit runs, once.
    /// </summary>
    [Theory]
    [InlineData("the visit's done record")]
    [InlineData("the new visit's record")]
    public async Task ARecoveryDonesRerunOfAHandler_IsDecidedFromTheRecords_TheResumeMakingItOnce(string crashAfter)
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: fold
                type: orient
              - name: reconcile
                type: orient
                on: {step: fold, outcome: failure}
                onFailure: recover
              - name: after
                type: orient
                dependsOn: [fold]
            """);
        var runner = new Runner()
            .On("fold", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("reconcile", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = crashAfter == "the new visit's record"
            ? new CrashingRecords(records, _ => false, picksStep: (instance, _) => instance == "reconcile+2")
            : new CrashingRecords(records, _ => false, picksStep: (instance, to) => instance == "recover" && to == nameof(FlowInstanceState.Done));
        var cancel = TestContext.Current.CancellationToken;

        using (records)
        {
            _ = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing).RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(crashing.Ended);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        Assert.Equal(nameof(FlowInstanceState.Done), flow.Steps.Single(step => step.Instance == "recover").State);
        Assert.Equal(crashAfter == "the new visit's record" ? new[] { "reconcile+2" } : [], flow.Steps.Where(step => step.Instance == "reconcile+2").Select(step => step.Instance));
        Assert.DoesNotContain(flow.Steps, step => step.Instance == "after");

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: fold, by reconcile", end.Summary);
            Assert.Equal(["reconcile+2", "after"], again.Started);
            Assert.Same(resumed.Instances["fold"], resumed.Instances["reconcile+2"].RaisedBy);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "reconcile+3");
        }
    }

    /// <summary>
    /// A handler raised for an ending that a failure's stop left NotRun - it never started, held nothing and wrote nothing
    /// but its place in its source's ending - is put back by the resume that goes on with no failure standing to stop it,
    /// under the same id and raised by the same ending, which already waits on it: it runs, the failure it answers is
    /// handled, and what depends on that step starts - never an ending waiting for ever on a visit nothing makes again, and
    /// what depends on its source failed as unable to start at every resume.
    /// </summary>
    [Fact]
    public async Task AHandlerAStopLeftNotRun_IsPutBackByTheResume_AndTheEndingItAnswersSettles()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: lint
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var watched = new Watched(records, snapshot => snapshot.Steps.Any(step => step is { Instance: "lint", State: FlowInstanceState.Failed }));

        // lint's failure stops the run while build still runs: build's failure then raises fix, which the stop leaves NotRun.
        var runner = new Runner()
            .On("lint", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .OnAsync("build", async _ =>
            {
                await watched.Seen;
                return FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)");
            });

        using (records)
        {
            var stopped = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: watched).RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Failed, stopped.Status);
        }

        var fix = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "fix");
        Assert.Equal((nameof(FlowInstanceState.NotRun), nameof(FlowNotRunCause.RunEnded), "build"), (fix.State, fix.NotRunCause, fix.RaisedBy));
        Assert.Equal([new FlowHandlerState("fix", "raised")], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build"))!.Handlers);

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
            Assert.Equal(["lint", "fix", "after"], again.Started);
            var putBack = resumed.Instances["fix"];
            Assert.Equal((FlowInstanceState.Done, 1, 1), (putBack.State, putBack.Visit, putBack.RaisedByAttempt));
            Assert.Same(resumed.Instances["build"], putBack.RaisedBy);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "fix+2");
        }
    }

    /// <summary>
    /// A group handler whose failure stopped the run is raised again by the resume before its failure stops standing, each
    /// in its own write: a crash between the two leaves the failure standing beside the new visit's record, which the next
    /// resume reads as the retry made - the new visit runs once - and a crash right after the second leaves the new visit
    /// made and the failure gone; either way the ending the handler answers waits on the new visit and settles with it,
    /// never a failure gone with no new visit made for it.
    /// </summary>
    [Theory]
    [InlineData("the new visit's record")]
    [InlineData("the failure's clearing")]
    public async Task AGroupHandlersRetryAtAResume_IsMadeOnce_WhereverACrashCutsItsWrites(string crashAfter)
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                on: {step: build, outcome: failure}
                onFailure: stop
                steps:
                  - name: attempt
                    type: orient
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner()
            .On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"))
            .On("attempt", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var stopped = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records).RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Failed, stopped.Status);
        }

        // The first resume ends at the write the test picks, the session going on in memory alone.
        using (var cut = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var crashing = crashAfter == "the new visit's record"
                ? new CrashingRecords(cut, _ => false, picksStep: (instance, _) => instance == "fix+2")
                : new CrashingRecords(cut, _ => false, picksStep: (instance, to) => instance == "fix" && to == nameof(FlowInstanceState.Failed));
            var first = TestAdvice.Coordinator(definition, new Runner(), new RunnerFacts(), clock, records: crashing, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            _ = await first.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(crashing.Ended);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        Assert.Equal(
            crashAfter == "the new visit's record" ? FlowStepRecord.NameOf(FlowFailure.EndedRun) : null,
            harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("fix"))!.Failure);
        Assert.Equal(nameof(FlowInstanceState.Running), flow.Steps.Single(step => step.Instance == "fix+2").State);

        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
            Assert.Equal(["fix+2.attempt", "after"], again.Started);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "fix+3");
        }
    }

    /// <summary>
    /// A group the schedule draws, run again as a retry - at the resume after its failure stopped the run, or by a
    /// <c>retry</c> answer to its failure's question - is opened again by the schedule, whose new visit is drawn after the
    /// write that settles the visit that failed: that write says the retry (<see cref="FlowStepRecord.Retried"/>), so a
    /// crash right after it leaves the new visit owed, and the next resume draws it - the group runs again once and what
    /// depends on it starts after - never a failure gone, no question waiting and no new visit drawn, with what depends on
    /// the group waiting for ever.
    /// </summary>
    [Theory]
    [InlineData("stop")]
    [InlineData("pause")]
    public async Task APlainGroupsRetry_IsInTheRecordThatSettlesItsFailure_ACrashBeforeItsNewVisitIsDrawnLeavesItOwed(string onFailure)
    {
        using var temp = new TempDirectory();
        var definition = Parse($"""
            name: flow
            steps:
              - name: round
                onFailure: {onFailure}
                steps:
                  - name: check
                    type: orient
              - name: after
                type: orient
                dependsOn: [round]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var ended = await TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records).RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(onFailure == "stop" ? FlowRunStatus.Failed : FlowRunStatus.Paused, ended.Status);
        }

        // The retry, carried out where the test picks it: the resume of the stopped run, or the answer to the question; the
        // process ends at the first write of the visit that failed from there, which lands, and the session goes on in memory.
        var settling = false;

        using (var cut = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var cutting = new CrashingRecords(cut, _ => false, picksStep: (instance, _) => settling && instance == "round");
            var first = TestAdvice.Coordinator(definition, new Runner(), new RunnerFacts(), clock, records: cutting, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            settling = true;

            if (onFailure == "pause")
            {
                var asked = Assert.Single(first.OpenQuestions);
                Assert.Equal(("failure", "round"), (asked.Kind, asked.Instance));
                Assert.True(first.TryAnswer(asked.Id, "retry", out var problem), problem);
            }
            else
            {
                _ = await first.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            }

            Assert.True(cutting.Ended);
        }

        // On disk: the visit that failed stands settled - no failure ending the run, no question waited on - and says its
        // retry; its new visit was never drawn.
        var failed = harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("round"))!;
        Assert.Equal((nameof(FlowInstanceState.Failed), null, null, true), (failed.State, failed.Failure, failed.Question, failed.Retried));
        Assert.DoesNotContain(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance == "round+2");

        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["round+2.check", "after"], again.Started);
            Assert.Equal(FlowInstanceState.Failed, resumed.Instances["round"].State);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "round+3");
        }
    }

    /// <summary>
    /// A visit of the recovery whose failure was answered retry is taken again once: the answer is written, then the next
    /// visit, then the failed visit's record - a crash right after the next visit's first record leaves the failed visit
    /// still waiting on its answered question, which the resume carries out again, and the next visit made in its place is
    /// that retry: it runs, the step it puts right runs again, and no third visit is taken beside it.
    /// </summary>
    [Fact]
    public async Task ARecoveryVisitsRetryCarriedOutAgainAtAResume_TakesTheRecoveryOnce()
    {
        using var temp = new TempDirectory();
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
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner()
            .On("build", (FlowStepRun run) => run.Visit == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("mend", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "its own work ended failed"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, _ => false, picksStep: (instance, _) => instance == "recover+2");

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing);
            var paused = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            var asked = Assert.Single(coordinator.OpenQuestions);
            Assert.Equal(("failure", "recover"), (asked.Kind, asked.Instance));
            Assert.True(coordinator.TryAnswer(asked.Id, "retry", out var problem), problem);
            Assert.True(crashing.Ended);
        }

        Assert.NotNull(harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))!.Question);
        Assert.Equal(nameof(FlowInstanceState.Running), harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "recover+2").State);

        var again = new Runner().On("build", FlowStepResult.Ended(passed: true, HarnessExit.Success));

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["recover+2.mend", "build+2", "after"], again.Started);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "recover+3");
        }
    }

    /// <summary>
    /// The one rule of which attempt of its source a handler's visit was raised for: the attempt its record names; for a
    /// record that names none, the attempt its source's record holds where that attempt ended and its failure is not owed,
    /// and the attempt before it beside one not ended - not started, running, paused, stopped - or whose failure is owed,
    /// for an ending raises its handlers only as it is decided; save a failure owed that a fail answer at a handler's bound
    /// gave an attempt that had ended done, whose raise is that attempt's own; and never below the first attempt.
    /// </summary>
    [Theory]
    [InlineData(3, FlowInstanceState.Failed, 1, null, null, 3)]
    [InlineData(null, FlowInstanceState.Failed, 2, null, null, 2)]
    [InlineData(null, FlowInstanceState.Done, 2, null, null, 2)]
    [InlineData(null, FlowInstanceState.Failed, 2, FlowFailure.PassedOver, null, 2)]
    [InlineData(null, FlowInstanceState.Failed, 2, FlowFailure.Owed, null, 1)]
    [InlineData(null, FlowInstanceState.Failed, 2, FlowFailure.Owed, "visitBound", 2)]
    [InlineData(null, FlowInstanceState.Failed, 1, FlowFailure.Owed, "visitBound", 1)]
    [InlineData(null, FlowInstanceState.Failed, 1, FlowFailure.Owed, null, 1)]
    [InlineData(null, FlowInstanceState.Failed, 2, FlowFailure.Owed, "failure", 1)]
    [InlineData(null, FlowInstanceState.Failed, 2, FlowFailure.PassedOver, "visitBound", 2)]
    [InlineData(null, FlowInstanceState.NotStarted, 2, null, null, 1)]
    [InlineData(null, FlowInstanceState.NotStarted, 1, null, null, 1)]
    [InlineData(null, FlowInstanceState.Running, 2, null, null, 1)]
    [InlineData(null, FlowInstanceState.Paused, 2, null, null, 1)]
    [InlineData(null, FlowInstanceState.Stopped, 2, null, null, 1)]
    public void TheAttemptARaiseAnswered_IsItsRecordsOrReadFromItsSourcesRecord(int? recorded, FlowInstanceState state, int attempt, FlowFailure? failure, string? endedByQuestion, int answered)
        => Assert.Equal(answered, FlowInstance.AttemptRaisedFor(recorded, state, attempt, sourceEndedFor: null, failure, endedByQuestion));

    /// <summary>
    /// A raise an older build recorded names no attempt, and is read as the attempt it answered by the engine's one rule of
    /// it (<see cref="FlowInstance.AttemptRaisedFor"/>): beside a source whose new attempt ended failed with its failure
    /// still owed - a crash came between that end and what decides it - the raise can only be an earlier attempt's, for an
    /// ending raises its handlers only as it is decided, so the resume decides the new attempt's failure and raises the
    /// handler anew.
    /// </summary>
    [Fact]
    public async Task AnOlderRaiseBesideANewAttemptsOwedFailure_IsReadAsAnEarlierAttempts_AndTheHandlerIsRaisedAnew()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: pause
              - name: note
                type: orient
                on: {step: build, outcome: failure}
                when: build.go
                onFailure: continue
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                maxVisits: 1
                then: build
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var asked = 0;
        var runner = new Runner()
            .On("build", (FlowStepRun _) => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)", output: JsonNode.Parse(++asked == 3 ? """{"go": true}""" : "{}")));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        // The process ends at the write of the second attempt's failed end, its failure owed.
        var crashing = new CrashingRecords(records, _ => false, picksRun: run => run.Steps.Any(step => step is { Instance: "build+2", Attempt: 2, State: FlowInstanceState.Failed }));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing);

            foreach (var (kind, answer) in new[] { ("visitBound", "fail"), ("failure", "retry") })
            {
                _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
                Assert.True(coordinator.TryAnswer(Assert.Single(coordinator.OpenQuestions, question => question.Kind == kind).Id, answer, out var problem), problem);
            }

            _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(crashing.Ended);
        }

        // The records as an older build leaves them: the raise of the first attempt's ending names no attempt.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        Assert.Equal(FlowStepRecord.NameOf(FlowFailure.Owed), flow.Steps.Single(step => step.Instance == "build+2").Failure);
        harness.OrchestrationStore.WriteFlow(layout, flow with
        {
            Steps = [.. flow.Steps.Select(step => step.RaisedByAttempt is null ? step : step with { RaisedByAttempt = null })],
        });

        foreach (var raised in flow.Steps.Where(step => step.RaisedByAttempt is not null))
        {
            harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile(raised.Instance), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile(raised.Instance))! with { RaisedByAttempt = null });
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition);
        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: taken);
            Assert.Equal(1, resumed.Instances["note+2"].RaisedByAttempt);

            var paused = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            // The owed failure was decided: note fits it, and fix at its bound asks.
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            var bound = Assert.Single(resumed.OpenQuestions);
            Assert.True(resumed.TryAnswer(bound.Id, "accept", out var problem), problem);
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["note+3", "after"], again.Started);
            Assert.Equal((FlowInstanceState.Done, 2), (resumed.Instances["note+3"].State, resumed.Instances["note+3"].RaisedByAttempt));
            Assert.Equal("note", resumed.Instances["build+2"].HandledBy);
        }
    }

    /// <summary>
    /// The attempt an older raise answered is read once, by the first resume that reads its record, and written into that
    /// visit's own record before anything rewrites its source's: the source's record changes as the session decides its
    /// failure - here its owed failure decided, and the session ending on the bound's question - so a second resume that
    /// read the attempt again from it would read the earlier attempt's raise as the new attempt's, take the handler as
    /// raised already and settle the new failure on it. The second resume reads what the first wrote: the answer raises the
    /// handler anew for the new attempt, its visit runs once, and the status a reader is shown names the same attempt.
    /// </summary>
    [Fact]
    public async Task AnOlderRaisesAttempt_IsReadOnceAndWritten_ASecondResumeReadsTheSameAttempt()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: pause
              - name: note
                type: orient
                on: {step: build, outcome: failure}
                when: build.go
                onFailure: continue
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                maxVisits: 1
                then: build
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var asked = 0;
        var runner = new Runner()
            .On("build", (FlowStepRun _) => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)", output: JsonNode.Parse(++asked == 3 ? """{"go": true}""" : "{}")));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, _ => false, picksRun: run => run.Steps.Any(step => step is { Instance: "build+2", Attempt: 2, State: FlowInstanceState.Failed }));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing);

            foreach (var (kind, answer) in new[] { ("visitBound", "fail"), ("failure", "retry") })
            {
                _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
                Assert.True(coordinator.TryAnswer(Assert.Single(coordinator.OpenQuestions, question => question.Kind == kind).Id, answer, out var problem), problem);
            }

            _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.True(crashing.Ended);
        }

        // The records as an older build leaves them: no raise names the attempt it answered.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        harness.OrchestrationStore.WriteFlow(layout, flow with
        {
            Steps = [.. flow.Steps.Select(step => step.RaisedByAttempt is null ? step : step with { RaisedByAttempt = null })],
        });

        foreach (var raised in flow.Steps.Where(step => step.RaisedByAttempt is not null))
        {
            harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile(raised.Instance), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile(raised.Instance))! with { RaisedByAttempt = null });
        }

        // The first resume decides the owed failure, and the session ends on fix's bound question.
        using (var firstRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var first = TestAdvice.Coordinator(definition, new Runner(), new RunnerFacts(), clock, records: firstRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            var paused = await first.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            Assert.Equal(("visitBound", "build+2"), (Assert.Single(first.OpenQuestions).Kind, Assert.Single(first.OpenQuestions).Instance));
        }

        // The source's own record has changed since the attempt was read - its failure decided - and the raise's says it.
        var source = harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build+2"))!;
        Assert.Equal((2, null), (source.Attempt, source.Failure));
        Assert.Equal(1, harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("note+2"))!.RaisedByAttempt);
        Assert.Equal(1, harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "note+2").RaisedByAttempt);

        var again = new Runner();

        using (var secondRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var second = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: secondRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            Assert.Equal(1, second.Instances["note+2"].RaisedByAttempt);
            Assert.True(second.TryAnswer(Assert.Single(second.OpenQuestions).Id, "accept", out var problem), problem);
            var end = await second.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["note+3", "after"], again.Started);
            Assert.Equal((FlowInstanceState.Done, 2), (second.Instances["note+3"].State, second.Instances["note+3"].RaisedByAttempt));
            Assert.Equal("note", second.Instances["build+2"].HandledBy);
            Assert.DoesNotContain(second.Instances.Keys, id => id == "note+4");
        }
    }

    /// <summary>
    /// A fail answer at a handler's bound, after a failure, decides that the failure stands unhandled: the ending no longer
    /// waits on the handlers it raised before the bound was met - one whose <c>when</c> could not be read among them - and
    /// the source's record says it (each such handler <c>left</c>), so a session that takes the run up rebuilds no ending
    /// that never waited in the session and settles nothing on it: the failure its policy decided - here the stop that
    /// ended the run, which a failure a person answered fail keeps standing - is never said to be one a handler took.
    /// </summary>
    [Fact]
    public async Task AFailAnswerAtAHandlersBoundAfterAFailure_EndsTheEndingsWaitOnItsRaisedHandlers_InTheSourcesRecord()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: stop
              - name: note
                type: orient
                on: {step: build, outcome: failure}
                when: build.go
                onFailure: continue
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                maxVisits: 1
                then: build
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)", output: JsonNode.Parse("{}")));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        // build+2's failure meets fix's bound beside note+2, raised with its when unreadable; the answer fail leaves the
        // failure to its policy, whose stop ends the run.
        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);
            _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            var bound = Assert.Single(coordinator.OpenQuestions);
            Assert.Equal(("visitBound", "build+2"), (bound.Kind, bound.Instance));
            Assert.True(coordinator.TryAnswer(bound.Id, "fail", out var problem), problem);
            Assert.Null(coordinator.Instances["build+2"].HandledBy);
        }

        // On disk: the failure stands, the stop it ended the run with, and the ending says it waits on none of its handlers.
        var failed = harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build+2"))!;
        Assert.Equal((nameof(FlowInstanceState.Failed), FlowStepRecord.NameOf(FlowFailure.EndedRun)), (failed.State, failed.Failure));
        Assert.Equal([new FlowHandlerState("fix", "passed"), new FlowHandlerState("note", "left")], failed.Handlers);
        Assert.Equal((nameof(FlowInstanceState.Failed), FlowStepRecord.NameOf(FlowFailure.PassedOver)), (harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("note+2"))!.State, harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("note+2"))!.Failure));

        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            // The run stays stopped on the failure the answer kept standing, and nothing took it.
            Assert.Equal(FlowRunStatus.Failed, end.Status);
            Assert.Empty(again.Started);
            Assert.Null(resumed.Instances["build+2"].HandledBy);
        }

        var standing = harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build+2"))!;
        Assert.Equal((null, FlowStepRecord.NameOf(FlowFailure.EndedRun)), (standing.HandledBy, standing.Failure));
        Assert.Equal([new FlowHandlerState("fix", "passed"), new FlowHandlerState("note", "left")], standing.Handlers);
    }

    /// <summary>
    /// A fail answer at the bound of a handler of a step that ended done ends that same attempt failed: its success ending
    /// had raised what it raised before the bound was met - a handler whose <c>when</c> could not be read among it - so an
    /// older raise beside a failure still owed that a fail answer at a visit's bound gave is that attempt's own, and its first
    /// attempt is never read as attempt 0. A crash right after the failed end, the raise naming no attempt as an older build
    /// wrote it: the resume decides the failure, takes the handler as raised already for this attempt, and raises it no
    /// second time.
    /// </summary>
    [Fact]
    public async Task AnOlderRaiseBesideAFailureAFailAnswerAtASuccessBoundGave_IsThatAttemptsOwn_AndIsNeverRaisedTwice()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: note
                type: orient
                on: {step: build, outcome: end}
                when: build.go
                onFailure: continue
              - name: fix
                type: orient
                on: {step: build, outcome: end}
                maxVisits: 1
                then: build
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("build", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("{}")));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        // build ends done twice - fix sends the run back once - and its second ending meets fix's bound, answered fail.
        var crashing = new CrashingRecords(records, _ => false, picksRun: run => run.Steps.Any(step => step is { Instance: "build+2", State: FlowInstanceState.Failed }));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing);
            _ = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            var bound = Assert.Single(coordinator.OpenQuestions);
            Assert.Equal(("visitBound", "build+2"), (bound.Kind, bound.Instance));
            Assert.True(coordinator.TryAnswer(bound.Id, "fail", out var problem), problem);
            Assert.True(crashing.Ended);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        var failed = flow.Steps.Single(step => step.Instance == "build+2");
        Assert.Equal((1, FlowStepRecord.NameOf(FlowFailure.Owed), "visitBound"), (failed.Attempt, failed.Failure, failed.EndedByQuestion));
        Assert.Equal(nameof(FlowInstanceState.Failed), flow.Steps.Single(step => step.Instance == "note+2").State);
        harness.OrchestrationStore.WriteFlow(layout, flow with
        {
            Steps = [.. flow.Steps.Select(step => step.RaisedByAttempt is null ? step : step with { RaisedByAttempt = null })],
        });

        foreach (var raised in flow.Steps.Where(step => step.RaisedByAttempt is not null))
        {
            harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile(raised.Instance), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile(raised.Instance))! with { RaisedByAttempt = null });
        }

        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            Assert.Equal(1, resumed.Instances["note+2"].RaisedByAttempt);
            var paused = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            // The failure's ending takes note as raised for this attempt already, and fix at its bound asks of it.
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            var bound = Assert.Single(resumed.OpenQuestions);
            Assert.Equal(("visitBound", "build+2"), (bound.Kind, bound.Instance));
            Assert.True(resumed.TryAnswer(bound.Id, "accept", out var problem), problem);
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["after+2"], again.Started);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "note+3");
            Assert.Equal("note", resumed.Instances["build+2"].HandledBy);
        }
    }

    /// <summary>
    /// A redirect that asks before it goes names its question in the question's own write, and waits on the answer the
    /// questions file holds: a <c>stay</c> written, and the process ended before the change it makes reached the step's
    /// record, is carried out at the resume - the redirect is given up, the step put right counted handled by the recovery
    /// - never a redirect made in its place, its group reset and run again over the answer a person gave.
    /// </summary>
    [Fact]
    public async Task AStayWrittenAndNotCarriedOut_IsCarriedOutAtTheResume_AndTheRedirectIsNeverMade()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: round
                onFailure: recover
                steps:
                  - name: worker
                    type: create-agent
                    agent: maker
                  - name: check
                    type: orient
                    dependsOn: [worker]
              - name: after
                type: orient
                dependsOn: [round]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);
        var answering = false;
        var crashing = new CrashingRecords(records, _ => false, lands: false, picksStep: (_, _) => answering);

        // The recovery put the group right, and its redirect back to it - which would leave the group's agent open - asks.
        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing);
            var paused = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            var asked = Assert.Single(coordinator.OpenQuestions);
            Assert.Equal(("openAgents", "recover"), (asked.Kind, asked.Instance));
            Assert.Equal(asked.Id, Assert.Single(harness.OrchestrationStore.ReadFlow(layout)!.Redirects!).Question);

            // The answer reaches the questions file; the process ends at the first write of the change it makes.
            answering = true;
            Assert.True(coordinator.TryAnswer(asked.Id, "stay", out var problem), problem);
            Assert.True(crashing.Ended);
        }

        Assert.Equal("answered", Assert.Single(harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions).State);
        Assert.NotNull(harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))!.Question);

        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["after"], again.Started);
            Assert.Equal("recover", resumed.Instances["round"].HandledBy);
            Assert.Null(resumed.Instances["round"].LeftBy);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "round+2");
        }
    }

    /// <summary>
    /// A redirect being made is never dropped as one the visits it leaves decided: a handler whose <c>then</c> sends the
    /// run back over the group it is in leaves itself with that group, and its redirect - the one being made - goes on,
    /// said made once and never said given up; the group's new visit runs, and the run ends done.
    /// </summary>
    [Fact]
    public async Task AHandlersRedirectOverItsOwnGroup_IsMade_NeverDroppedAsTheRedirectOfAVisitItLeaves()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: round
                steps:
                  - name: check
                    type: orient
                  - name: fix
                    type: orient
                    on: {step: check, outcome: failure}
                    then: round
                  - name: slow
                    type: orient
                    dependsOn: [check]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("check", run => run.Instance == "round.check"
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal("round.fix", coordinator.Instances["round"].LeftBy);
            Assert.Equal(["round.check", "round.fix", "round+2.check", "round+2.slow"], runner.Started);
        }

        Assert.Equal(
            ["the run was sent to 'round', resetting 1 of its scope"],
            FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Select(read => read.Detail).Where(detail => detail?.StartsWith("the run was ", StringComparison.Ordinal) == true));
    }

    /// <summary>
    /// A redirect and the question it asks are one fact, though the process ended between the question's write and the
    /// redirect's record that would name it: the redirect's record names no question, as an older build wrote it until the
    /// write after its ask, or the one answered <c>retry</c> before the question asked again - and the resume has the
    /// redirect wait on the question its handler asked, which is still open, never make it at once over a question nobody
    /// answered. The answer <c>stay</c> then gives it up: the group is not reset, and what depends on it runs.
    /// </summary>
    [Theory]
    [InlineData("an older build's first ask")]
    [InlineData("a question asked again after retry")]
    public async Task ARedirectWhoseRecordNamesNotTheQuestionItsHandlerJustAsked_WaitsOnThatQuestion_AtTheResume(string cut)
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: round
                onFailure: recover
                steps:
                  - name: worker
                    type: create-agent
                    agent: maker
                  - name: check
                    type: orient
                    dependsOn: [worker]
              - name: after
                type: orient
                dependsOn: [round]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        // Asked again, the process ends at the questions' write that holds the new question, before the redirect's record.
        var crashing = new CrashingRecords(records, _ => false, picksQuestions: questions => cut == "a question asked again after retry" && questions.Count(question => question.Kind == "openAgents") == 2);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: crashing);
            var paused = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            var asked = Assert.Single(coordinator.OpenQuestions);
            Assert.Equal(("openAgents", "recover"), (asked.Kind, asked.Instance));

            if (cut == "a question asked again after retry")
            {
                // The agent is still open, so the redirect asks again.
                Assert.True(coordinator.TryAnswer(asked.Id, "retry", out var problem), problem);
                Assert.True(crashing.Ended);
            }
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        var questions = harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions.Where(question => question.Kind == "openAgents").ToList();

        if (cut == "an older build's first ask")
        {
            // The records as an older build left them: the redirect names no question, and the visit keeps no reruns.
            harness.OrchestrationStore.WriteFlow(layout, flow with
            {
                Steps = [.. flow.Steps.Select(step => step with { Reruns = null })],
                Redirects = [Assert.Single(flow.Redirects!) with { Question = null }],
            });
            harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile("recover"), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover"))! with { Reruns = null });
            Assert.Equal(["open"], questions.Select(question => question.State));
        }
        else
        {
            // On disk: the redirect still names the question answered retry; the one asked again is open.
            Assert.Equal(questions[0].Id, Assert.Single(flow.Redirects!).Question);
            Assert.Equal(["answered", "open"], questions.Select(question => question.State));
        }

        var again = new Runner();

        using (var resumedRecords = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = TestAdvice.Coordinator(definition, again, new RunnerFacts(), clock, records: resumedRecords, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition));

            if (cut == "a question asked again after retry")
            {
                // Asked again just before the process ended, the question owes its recommendation, which the session seeks:
                // the redirect waits on the question meanwhile, and nothing is reset.
                Assert.Equal(FlowRunStatus.Paused, (await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel)).Status);
                Assert.Empty(again.Started);
            }

            // The redirect waits on the question still open, which the answer stay gives up.
            var open = Assert.Single(resumed.OpenQuestions);
            Assert.Equal(questions[^1].Id, open.Id);
            Assert.True(resumed.TryAnswer(open.Id, "stay", out var problem), problem);
            var end = await resumed.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["after"], again.Started);
            Assert.Equal("recover", resumed.Instances["round"].HandledBy);
            Assert.Null(resumed.Instances["round"].LeftBy);
            Assert.DoesNotContain(resumed.Instances.Keys, id => id == "round+2");
        }
    }

    /// <summary>
    /// A redirect that asks before it goes is the one its question asks of from the moment the question is made: an
    /// answer the workflow configured for it, taken at once as the question is raised, finds the redirect and gives it up -
    /// the run goes on as if the visit named no rerun, the step put right counted handled by the recovery - where a
    /// redirect that named its question only once the ask returned would miss that answer and wait for ever on a question
    /// already answered.
    /// </summary>
    [Fact]
    public async Task AConfiguredOpenAgentsAnswerTakenAtOnce_FindsTheRedirectThatAsked()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            answers:
              openAgents: stay
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: round
                onFailure: recover
                steps:
                  - name: worker
                    type: create-agent
                    agent: maker
                  - name: check
                    type: orient
                    dependsOn: [worker]
              - name: after
                type: orient
                dependsOn: [round]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new Runner().On("check", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)"));
        var (harness, layout, records, clock) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.True(FlowRunStatus.Done == end.Status, $"{end.Summary} | asked: {string.Join("; ", coordinator.OpenQuestions.Select(question => $"{question.Kind}: {question.Problem}"))}");
            Assert.Equal(["round.worker", "round.check", "recover.mend", "after"], runner.Started);
            Assert.Equal("recover", coordinator.Instances["round"].HandledBy);
            Assert.DoesNotContain(coordinator.Instances.Keys, id => id == "round+2");
        }

        var answered = Assert.Single(harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions);
        Assert.Equal(("openAgents", "answered", "stay"), (answered.Kind, answered.State, answered.Answers[^1].Answer));
    }
}
