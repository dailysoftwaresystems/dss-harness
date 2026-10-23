using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// A refresh or a rebase of <c>others</c>, and the questions a fold family step's dry run raises, as the coordinator holds
/// them over fake step runners, in memory: one turn for each target - every open agent of the run but the one
/// <c>others</c> names - in the order of their names, each admitted on its own against its own tree, retried on its own,
/// ended on its own; the step done, or failed with the code of the first target that failed, once each has had its turn.
/// </summary>
public sealed class FlowOthersTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Three agents made, and a refresh of every one of them but the first's.</summary>
    private static readonly WorkflowDefinition Others = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
        name: flow
        maxOpenAgents: 4
        steps:
          - name: one
            type: create-agent
            agent: worker.md
          - name: two
            type: create-agent
            agent: worker.md
            dependsOn: [one]
          - name: three
            type: create-agent
            agent: worker.md
            dependsOn: [two]
          - name: refresh
            type: refresh
            others: one
            dependsOn: [three]
            onFailure: continue
        """);

    /// <summary>
    /// Each target has its turn alone, in the order of the agents' names, the one others names left out: the runner is
    /// given each target - never an agent it works in - and the step ends done once each did, its record saying so.
    /// </summary>
    [Fact]
    public async Task EachTargetHasItsTurnAlone_InTheOrderOfTheirNames_TheOneOthersNamesLeftOut()
    {
        var runner = new Runner().On("refresh", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(Others, runner, new Facts(), new JumpingClock());

        var end = await coordinator.RunAsync(Token);
        var refresh = coordinator.Instances["refresh"];

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, refresh.State);
        Assert.Equal([("a2", null), ("a3", null)], runner.Runs.Where(run => run.Instance == "refresh").Select(run => (run.Target, run.WorksIn)));
        Assert.Equal(
            [new FlowTargetRecord("a2", FlowTargetRecord.Done, 1, HarnessExit.Success), new FlowTargetRecord("a3", FlowTargetRecord.Done, 1, HarnessExit.Success)],
            refresh.Targets);
    }

    /// <summary>
    /// A target that fails ends its own turn and the others still have theirs; the step then ends failed with the code of
    /// the first, by the order of their names, that failed, saying which target and why.
    /// </summary>
    [Fact]
    public async Task ATargetThatFails_EndsItsTurn_TheOthersStillHaveTheirs_AndTheStepFailsWithTheFirstsCode()
    {
        var runner = new Runner().On("refresh", run => run.Target == "a2"
            ? FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, "a key was filled wrong")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(Others, runner, new Facts(), new JumpingClock());

        _ = await coordinator.RunAsync(Token);
        var refresh = coordinator.Instances["refresh"];

        Assert.Equal(FlowInstanceState.Failed, refresh.State);
        Assert.Equal(HarnessExit.ConfigInvalid, refresh.Exit);
        Assert.Equal("target 'a2': a key was filled wrong", refresh.Error);
        Assert.Equal([FlowTargetRecord.Failed, FlowTargetRecord.Done], refresh.Targets!.Select(target => target.State));
    }

    /// <summary>
    /// An incomplete target is run again by the retry table on its own count of attempts, never the step's - the next
    /// target begins at its first, with every retry the table gives still its own; a dry run refused for anything but
    /// conflicts ends that target 13 and the next has its turn.
    /// </summary>
    [Fact]
    public async Task ATargetIsRetriedOnItsOwnCount_AndARefusedDryRunEndsThatTargetAlone()
    {
        var (a2, a3) = (0, 0);
        var runner = new Runner().On("refresh", run => run.Target == "a2"
            ? ++a2 < 3 ? FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, "stopped part way") : FlowStepResult.Ended(passed: true, HarnessExit.Success)
            : ++a3 < 3 ? FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, "stopped part way")
            : FlowStepResult.Standing(new FlowDryRun(RequestStanding.Refused, [], [], [RefusalKind.Other], "refused for itself")));
        var clock = new JumpingClock();
        var coordinator = new FlowCoordinator(Others, runner, new Facts(), clock);

        _ = await coordinator.RunAsync(Token);
        var refresh = coordinator.Instances["refresh"];

        Assert.Equal([new FlowTargetRecord("a2", FlowTargetRecord.Done, 3, HarnessExit.Success), new FlowTargetRecord("a3", FlowTargetRecord.Failed, 3, HarnessExit.Refused, "refused for itself")], refresh.Targets);
        Assert.Equal("target 'a3': refused for itself", refresh.Error);
        Assert.Empty(coordinator.OpenQuestions);
    }

    /// <summary>
    /// A question a target's dry run raises names the target beside the step; answered fail, it ends that target with the
    /// kind's code, and the next target has its turn.
    /// </summary>
    [Fact]
    public async Task AQuestionOfATarget_NamesIt_AndAFailAnswerEndsThatTargetAlone()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            maxOpenAgents: 4
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: three
                type: create-agent
                agent: worker.md
                dependsOn: [two]
              - name: refresh
                type: refresh
                others: one
                dependsOn: [three]
                onFailure: continue
                answers:
                  foldConflicts: fail
            """);
        var runner = new Runner().On("refresh", run => run.Target == "a2"
            ? FlowStepResult.Standing(new FlowDryRun(RequestStanding.Refused, [], [], [RefusalKind.Conflict], "1 path conflicts") { Conflicting = ["src/a.cs"], Target = run.Target, Agent = run.Target })
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new Facts(), new JumpingClock());

        _ = await coordinator.RunAsync(Token);
        var refresh = coordinator.Instances["refresh"];
        var asked = Assert.Single(coordinator.Questions);

        Assert.Equal("foldConflicts", asked.Kind);
        Assert.StartsWith("'refresh' (target 'a2') was refused: paths both trees changed conflict in their three-way merge: src/a.cs", asked.Problem, StringComparison.Ordinal);
        Assert.Equal([FlowTargetRecord.Failed, FlowTargetRecord.Done], refresh.Targets!.Select(target => target.State));
        Assert.Equal(HarnessExit.Refused, refresh.Exit);
        Assert.Equal(("foldConflicts", "foldConflicts"), (refresh.Targets![0].EndedByQuestion, refresh.EndedByQuestion));
    }

    /// <summary>
    /// A consent a target's question is answered with by configuration is no attempt of its turn: the dry run is made
    /// again in the same one, and the target ends done after its first.
    /// </summary>
    [Fact]
    public async Task AnAnswerGivenATargetsQuestion_IsNoAttemptOfItsTurn()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            maxOpenAgents: 4
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: refresh
                type: refresh
                others: one
                dependsOn: [two]
                answers:
                  foldTwoChanges: merge
            """);
        var runner = new Runner().On("refresh", run => run.Answers.Count == 0
            ? FlowStepResult.Standing(new FlowDryRun(RequestStanding.NeedsConsent, [ConsentKind.FoldTwoChanges], [new FlowNamed(ConsentKind.FoldTwoChanges, ["src/a.cs"])], [], "lacks a merge") { Agent = run.Target, Target = run.Target })
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new Facts(), new JumpingClock());

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal([new FlowTargetRecord("a2", FlowTargetRecord.Done, 1, HarnessExit.Success)], coordinator.Instances["refresh"].Targets);
        Assert.Equal(2, runner.Runs.Count(run => run.Instance == "refresh"));
    }

    /// <summary>
    /// A step of others that failed and is run again - answered retry - gives each target that failed its turn again,
    /// whole, from its first attempt, and those done keep what they did; and a target that failed over a held lock gives
    /// the step that cause, which its code alone cannot say.
    /// </summary>
    [Fact]
    public async Task AStepOfOthersRunAgain_GivesEachFailedTargetItsTurnAgain_AndAHeldLockIsTheStepsCause()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            maxOpenAgents: 4
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: three
                type: create-agent
                agent: worker.md
                dependsOn: [two]
              - name: refresh
                type: refresh
                others: one
                dependsOn: [three]
                onFailure: pause
            """);
        var held = true;
        var runner = new Runner().On("refresh", run => run.Target == "a2" && held
            ? FlowStepResult.Ended(passed: false, HarnessExit.Refused, "a run holds its tree") with { Cause = OutcomeCause.LockHeld }
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new Facts(), new JumpingClock());

        var paused = await coordinator.RunAsync(Token);
        var refresh = coordinator.Instances["refresh"];

        Assert.Equal(FlowRunStatus.Paused, paused.Status);
        Assert.Equal((FlowInstanceState.Paused, HarnessExit.Refused, OutcomeCause.LockHeld), (refresh.State, refresh.Exit, refresh.Cause));
        Assert.Equal(("failed", nameof(OutcomeCause.LockHeld)), (refresh.Targets![0].State, refresh.Targets[0].Cause));

        held = false;
        Assert.True(coordinator.TryAnswer(Assert.Single(coordinator.OpenQuestions).Id, "retry", out var problem), problem);
        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [new FlowTargetRecord("a2", FlowTargetRecord.Done, 1, HarnessExit.Success), new FlowTargetRecord("a3", FlowTargetRecord.Done, 1, HarnessExit.Success)],
            coordinator.Instances["refresh"].Targets);
        Assert.Equal(1, runner.Runs.Count(run => run.Instance == "refresh" && run.Target == "a3"));
    }

    /// <summary>
    /// A step of others run again by a retry claims the tree of the target it gives its turn again before it starts:
    /// while another step holds that tree, it waits on the claim, and runs the target once the tree is free.
    /// </summary>
    [Fact]
    public async Task AStepOfOthersRetried_ClaimsTheTreeOfTheTargetItReopens()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            maxOpenAgents: 4
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: three
                type: create-agent
                agent: worker.md
                dependsOn: [two]
              - name: rebase
                type: rebase
                others: one
                dependsOn: [three]
                onFailure: pause
              - name: hold
                type: rebase
                of: two
                dependsOn: [three]
            """);
        var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = 0;
        var runner = new Runner()
            .On("rebase", run => run.Target == "a2" && Interlocked.Increment(ref failed) == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, "a key was filled wrong")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .Async("hold", _ => gate.Task);
        var records = new Watched();
        var coordinator = new FlowCoordinator(definition, runner, new Facts(), new JumpingClock(), records: records);

        var session = coordinator.RunAsync(Token);
        var asked = await records.Asked.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var retried = records.SeenAsync(snapshot => snapshot.Steps.Any(step => step.Instance == "rebase" && step.Attempt == 3
            && (step.State == FlowInstanceState.Running || step.WaitsOn.Count > 0)));
        _ = coordinator.OfferAnswerAsync(asked, "retry");
        var first = await retried.WaitAsync(TimeSpan.FromSeconds(30), Token);

        gate.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(FlowInstanceState.NotStarted, first.State);
        Assert.Equal(["claim"], first.WaitsOn);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [new FlowTargetRecord("a2", FlowTargetRecord.Done, 1, HarnessExit.Success), new FlowTargetRecord("a3", FlowTargetRecord.Done, 1, HarnessExit.Success)],
            coordinator.Instances["rebase"].Targets);
    }

    /// <summary>
    /// A step of others claims the first target still open: one closed before its turn is never claimed, so the next is,
    /// and the step waits on that one's tree while another step holds it.
    /// </summary>
    [Fact]
    public async Task ATargetClosedBeforeItsTurn_IsNeverClaimed_TheNextOpenOneIs()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            maxOpenAgents: 5
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: three
                type: create-agent
                agent: worker.md
                dependsOn: [two]
              - name: four
                type: create-agent
                agent: worker.md
                dependsOn: [three]
              - name: rebase
                type: rebase
                others: one
                dependsOn: [four]
              - name: gone
                type: delete-agent
                of: three
                dependsOn: [four]
              - name: hold
                type: rebase
                of: four
                dependsOn: [gone]
            """);
        var first = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner()
            .Async("rebase", run => run.Target == "a2" ? first.Task : null)
            .Async("hold", _ =>
            {
                holding.TrySetResult();
                return held.Task;
            });
        var records = new Watched();
        var coordinator = new FlowCoordinator(definition, runner, new Facts(), new JumpingClock(), records: records);

        var session = coordinator.RunAsync(Token);
        await holding.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var next = records.SeenAsync(snapshot => snapshot.Steps.Any(step => step.Instance == "rebase" && step.Attempt == 2
            && (step.State == FlowInstanceState.Running || step.WaitsOn.Count > 0)));
        first.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var waiting = await next.WaitAsync(TimeSpan.FromSeconds(30), Token);

        held.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(FlowInstanceState.NotStarted, waiting.State);
        Assert.Equal(["claim"], waiting.WaitsOn);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [("a2", FlowTargetRecord.Done), ("a3", FlowTargetRecord.PassedOver), ("a4", FlowTargetRecord.Done)],
            coordinator.Instances["rebase"].Targets!.Select(target => (target.Agent, target.State)));
    }

    /// <summary>
    /// A step of others waiting on its target's tree, which another step holds, waits holding nothing else: a step that
    /// writes another target's tree runs meanwhile, and the step takes each target in turn once its tree is free.
    /// </summary>
    [Fact]
    public async Task AStepOfOthersWaitingOnItsTarget_HoldsNothingElse()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            maxOpenAgents: 4
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: three
                type: create-agent
                agent: worker.md
                dependsOn: [two]
              - name: hold
                type: rebase
                of: two
                dependsOn: [three]
              - name: rebase
                type: rebase
                others: one
                dependsOn: [three]
              - name: other
                type: rebase
                of: three
                dependsOn: [three]
            """);
        var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner().Async("hold", _ => gate.Task);
        var coordinator = new FlowCoordinator(definition, runner, new Facts(), new JumpingClock());

        var session = coordinator.RunAsync(Token);

        await runner.Ended("other").WaitAsync(TimeSpan.FromSeconds(30), Token);
        Assert.DoesNotContain(runner.Runs.ToArray(), run => run.Instance == "rebase");

        gate.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["a2", "a3"], runner.Runs.Where(run => run.Instance == "rebase").Select(run => run.Target));
    }

    /// <summary>
    /// A target is claimed on its own tree - a rebase's that alone, a refresh's beside the main tree it reads - and with no
    /// target named, nothing.
    /// </summary>
    [Fact]
    public void AStepOfOthers_ClaimsItsTargetsTree_NeverTheMainTreesForIt()
    {
        var rebase = new WorkflowStep { Name = "rebase", Type = "rebase", Others = "one" };
        var refresh = new WorkflowStep { Name = "refresh", Type = "refresh", Others = "one" };

        Assert.Equal([new FlowClaim("a2", FlowClaimKind.Write)], FlowClaims.Of(rebase, new Facts(), "a2"));
        Assert.Equal([new FlowClaim(FlowClaims.Main, FlowClaimKind.Write), new FlowClaim("a3", FlowClaimKind.Write)], FlowClaims.Of(refresh, new Facts(), "a3"));
        Assert.Empty(FlowClaims.Of(rebase, new Facts(), agent: null));
    }

    /// <summary>A run takes its agents in turn by the number each name carries - a2 before a10 - and every other name after them.</summary>
    [Fact]
    public void AgentsAreTakenInTheOrderOfTheirNumbers()
        => Assert.Equal(["a1", "a2", "a10", "a01", "b"], new[] { "b", "a10", "a01", "a2", "a1" }.Order(OrchestrationRules.AgentNameOrder));

    /// <summary>
    /// A target closed before its turn - deleted by a step that ran meanwhile, beside a rebase that holds its target alone - is passed over, saying why, and the step
    /// does not fail for it: it has no turn to fail.
    /// </summary>
    [Fact]
    public async Task ATargetClosedBeforeItsTurn_IsPassedOver_SayingWhy()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            maxOpenAgents: 4
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: three
                type: create-agent
                agent: worker.md
                dependsOn: [two]
              - name: rebase
                type: rebase
                others: one
                dependsOn: [three]
              - name: gone
                type: delete-agent
                of: three
                dependsOn: [three]
            """);
        var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner()
            .On("rebase", run => run.Target == "a2" ? null : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .Async("rebase", run => run.Target == "a2" ? gate.Task : null)
            .On("gone", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new Facts(), new JumpingClock());

        var session = coordinator.RunAsync(Token);

        // The deletion of a3 ends while a2 has its turn - a rebase holds its target alone, and the deletion a3 and the main
        // tree - so a3 is closed before its own.
        await runner.Ended("gone").WaitAsync(TimeSpan.FromSeconds(30), Token);
        gate.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var rebase = coordinator.Instances["rebase"];

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, rebase.State);
        Assert.Equal(
            [new FlowTargetRecord("a2", FlowTargetRecord.Done, 1, HarnessExit.Success), new FlowTargetRecord("a3", FlowTargetRecord.PassedOver, 0, Error: "agent 'a3' was closed before its turn")],
            rebase.Targets);
    }

    /// <summary>A fold family step's dry run raises the question the one rule names, with what it is about, a consent's by its kind.</summary>
    [Fact]
    public void TheFoldQuestion_IsTheOneRules_RefusedThenOutsideThenAConsent_APersonsFirst()
    {
        var lacking = new FlowDryRun(
            RequestStanding.NeedsConsent,
            [ConsentKind.FoldNewRows, ConsentKind.FoldLostRows],
            [new FlowNamed(ConsentKind.FoldNewRows, ["R-1"]), new FlowNamed(ConsentKind.FoldLostRows, ["R-2:closing"])],
            [],
            "lacks two")
        {
            Agent = "a1",
        };

        Assert.Equal(new FlowFoldQuestion("foldLostRows", ["R-2:closing"]).Kind, FlowQuestions.FoldQuestionOf(lacking)!.Kind);
        Assert.Equal(["R-2:closing"], FlowQuestions.FoldQuestionOf(lacking)!.Named);
        Assert.Equal("foldOutside", FlowQuestions.FoldQuestionOf(lacking with { Outside = ["c.txt"] })!.Kind);
        Assert.Equal(
            "'fold' was not applied: it lacks what nobody gave (foldLostRows) for 'R-2:closing': lacks two",
            FlowQuestions.FoldProblemOf("fold", lacking, FlowQuestions.FoldQuestionOf(lacking)!));
        Assert.Null(FlowQuestions.FoldQuestionOf(lacking with { Standing = RequestStanding.Refused, Refusals = [RefusalKind.Conflict, RefusalKind.Other], Outside = ["c.txt"] }));

        var conflicts = lacking with { Standing = RequestStanding.Refused, Refusals = [RefusalKind.Conflict], Conflicting = ["src/a.cs"], Lines = ["  src/a.cs   (conflicts)"], Outside = ["c.txt"] };

        Assert.Equal(new FlowFoldQuestion("foldConflicts", ["src/a.cs"]).Kind, FlowQuestions.FoldQuestionOf(conflicts)!.Kind);
        Assert.Equal(
            "'fold' was refused: paths both trees changed conflict in their three-way merge: src/a.cs\n  src/a.cs   (conflicts)",
            FlowQuestions.FoldProblemOf("fold", conflicts, FlowQuestions.FoldQuestionOf(conflicts)!));
    }

    /// <summary>A runner that answers each step as its test scripted it, recording every run it was asked.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult?>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>?>> _held = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _ended = new(StringComparer.Ordinal);

        /// <summary>Every run asked, whole, in order.</summary>
        public List<FlowStepRun> Runs { get; } = [];

        public Runner On(string step, Func<FlowStepRun, FlowStepResult?> script)
        {
            _scripts[step] = script;
            return this;
        }

        /// <summary>Holds a run of <paramref name="step"/> on the task its script gives, where it gives one.</summary>
        public Runner Async(string step, Func<FlowStepRun, Task<FlowStepResult>?> script)
        {
            _held[step] = script;
            return this;
        }

        /// <summary>Completes once a run of <paramref name="step"/> has answered.</summary>
        public Task Ended(string step) => EndOf(step).Task;

        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (Runs)
            {
                Runs.Add(run);
            }

            var result = _held.TryGetValue(run.Step.Name, out var held) && held(run) is { } waiting
                ? await waiting.WaitAsync(cancellationToken).ConfigureAwait(false)
                : _scripts.TryGetValue(run.Step.Name, out var script) && script(run) is { } scripted ? scripted : FlowStepResult.Ended(passed: true, HarnessExit.Success);

            EndOf(run.Step.Name).TrySetResult();
            return result;
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        private TaskCompletionSource EndOf(string step)
        {
            lock (_ended)
            {
                if (!_ended.TryGetValue(step, out var ended))
                {
                    ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ended[step] = ended;
                }

                return ended;
            }
        }
    }

    private sealed class Facts : IFlowRunnerFacts
    {
        public bool WritesTree(string runner) => false;
    }

    /// <summary>The run's records, watched and kept nowhere: a test waits on the first question asked, or on a change it times.</summary>
    private sealed class Watched : IFlowRecords
    {
        private readonly List<(Func<FlowSnapshot, bool> Holds, TaskCompletionSource<FlowSnapshotStep> Seen)> _waits = [];

        /// <summary>The id of the first question the run asked.</summary>
        public TaskCompletionSource<string> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The step of rebase as the first change written after this call whose snapshot <paramref name="holds"/> says left it.</summary>
        public Task<FlowSnapshotStep> SeenAsync(Func<FlowSnapshot, bool> holds)
        {
            var seen = new TaskCompletionSource<FlowSnapshotStep>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_waits)
            {
                _waits.Add((holds, seen));
            }

            return seen.Task;
        }

        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            lock (_waits)
            {
                foreach (var (holds, seen) in _waits.Where(wait => wait.Holds(snapshot)))
                {
                    seen.TrySetResult(snapshot.Steps.Single(step => step.Instance == "rebase"));
                }
            }
        }

        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
        {
            if (questions.Count > 0)
            {
                Asked.TrySetResult(questions[0].Id);
            }
        }
    }

    /// <summary>A graph step reads the main tree, and - where its <c>in</c> names an agent - that agent's tree too.</summary>
    [Fact]
    public void AGraphStep_ReadsTheMainTree_AndTheTreeItsInNames()
    {
        var main = new WorkflowStep { Name = "map", Type = "graphify-codebase" };
        var agents = main with { In = "make" };

        Assert.Equal([new FlowClaim(FlowClaims.Main, FlowClaimKind.Read)], FlowClaims.Of(main, new Facts()));
        Assert.Equal([new FlowClaim(FlowClaims.Main, FlowClaimKind.Read), new FlowClaim("a4", FlowClaimKind.Read)], FlowClaims.Of(agents, new Facts(), "a4"));
        Assert.Equal([new FlowClaim(FlowClaims.Main, FlowClaimKind.Read), new FlowClaim("make", FlowClaimKind.Read)], FlowClaims.Of(agents, new Facts(), agent: null));
    }
}
