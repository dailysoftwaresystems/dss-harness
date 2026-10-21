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
        var runner = new FakeRunner()
            .On("build", asked => asked == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "leg 'windows' ended Red (20)")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The first visit of build failed; verify and after waited on the failure's settling, are part of what the
        // redirect resets, and run - once each, untouched by the reset for they had not started - after the target's
        // new visit.
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

        // The handler sends the run back past the group it lives in, and the agent made in it is still open.
        Assert.Equal(FlowRunStatus.Paused, first.Status);
        var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "openAgents");
        Assert.Equal("'fix' would go back to 'plan', and agents 'a3', made since, are still open", asked.Problem);
        Assert.Equal(["retry", "stay", "abort"], asked.Options);

        Assert.True(coordinator.TryAnswer(asked.Id, "stay", out _));
        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The redirect is given up: the run goes on as if the handler named no then, and the source's failure counts as handled.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("fix", coordinator.Instances["round.check"].HandledBy);
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

                Assert.Equal(13, bound.Exit);
                Assert.Contains("was visited 99 times in its scope", bound.Error, StringComparison.Ordinal);
                return;
            }

            var asked = AssertSingle(coordinator.OpenQuestions, question => question.Kind == "visitBound");

            if (!coordinator.TryAnswer(asked.Id, "more", out _))
            {
                Assert.Fail("the bound's more was refused");
            }
        }
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
        Assert.Null(flow.Redirect);
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
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            // The group is in flight when the handler ends done and names it: the redirect is owed, and nothing it
            // resets is cancelled.
            await Task.Delay(50, TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt");
            _ = runner.Release("member");
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);

            var owed = harness.OrchestrationStore.ReadFlow(layout)?.Redirect;

            Assert.NotNull(owed);
            Assert.Equal("fix", owed.Handler);
            Assert.Equal("round", owed.Target);
            Assert.Contains("round", owed.Steps, StringComparer.Ordinal);

            // The resume makes what the pause found owed: the group's visit ended where it stood, and a new one begins.
            var done = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, done.Status);
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)?.Redirect);
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
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            await Task.Delay(50, TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt");
            _ = runner.Release("member");
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.NotNull(harness.OrchestrationStore.ReadFlow(layout)?.Redirect);
        }

        var taken = new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));

        using (var again = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), clock, records: again, resume: taken);
            var done = await resumed.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, done.Status);
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)?.Redirect);
            Assert.Equal(FlowInstanceState.Done, resumed.Instances["round+2"].State);
            Assert.Contains("round+2.member", resumed.Instances.Keys, StringComparer.Ordinal);
        }
    }
}
