using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// What the coordinator reads for a commit step where it starts, over fake step runners: the folds upstream of it - every
/// visit and iteration of them, never another work item's - and the texts of what the run checked, of the whole run so
/// far and of what the commit carries: the checks upstream of it since the commit before it, what did not check, what is
/// red, and the checks still to come.
/// </summary>
public sealed class FlowCommitReadsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A commit reads as its folds every fold and deletion upstream of it: one before the group it sits in, its own item's
    /// and never another item's, and in a repeat every earlier iteration's beside its own; a step that is no fold is not
    /// among them.
    /// </summary>
    [Fact]
    public async Task ACommitsFolds_AreTheFoldsUpstreamOfIt_NeverAnotherItems()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: made
                type: create-agent
                agent: worker.md
              - name: prep
                type: fold
                of: made
                dependsOn: [made]
              - name: items
                forEach: plan.list
                dependsOn: [plan, prep]
                steps:
                  - name: fold
                    type: fold
                    of: made
                  - name: checkpoint
                    type: commit
                    message: checkpoint
                    dependsOn: [fold]
              - name: fix
                dependsOn: [items]
                repeat:
                  until: check.ok
                  maxIterations: 3
                steps:
                  - name: fold
                    type: fold
                    of: made
                  - name: save
                    type: commit
                    message: save
                    dependsOn: [fold]
                  - name: check
                    type: orient
                    dependsOn: [save]
            """);
        var checks = 0;
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""{"list": ["x", "y"]}""")))
            .On("check", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["ok"] = ++checks == 2 }));

        var end = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["prep", "items[0].fold"], FoldsOf(runner, "items[0].checkpoint"));
        Assert.Equal(["prep", "items[1].fold"], FoldsOf(runner, "items[1].checkpoint"));
        Assert.Equal(["prep", "items[0].fold", "items[1].fold", "fix#1.fold"], FoldsOf(runner, "fix#1.save"));
        Assert.Equal(["prep", "items[0].fold", "items[1].fold", "fix#1.fold", "fix#2.fold"], FoldsOf(runner, "fix#2.save"));
        Assert.All(runner.Runs.Where(run => run.Step.Type != "commit"), run => Assert.False(run.Read?.ContainsKey("folds") ?? false));
    }

    /// <summary>
    /// What ran says of each check where it ran and how it ended - a failure with its code and why, passed over or taken by
    /// a handler, a check in an agent's worktree - and a check whose failure was passed over did not check what a commit
    /// carries, though the run's own list of what did not check leaves it out.
    /// </summary>
    [Fact]
    public async Task WhatRan_SaysHowEachCheckEnded_AndACommitCarriesAPassedOverFailureAsNotChecked()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: made
                type: create-agent
                agent: worker.md
              - name: lint
                type: build
                onFailure: continue
                dependsOn: [made]
              - name: gate
                type: test
                dependsOn: [lint]
              - name: fix
                type: orient
                on: {step: gate, outcome: failure}
              - name: inside
                type: build
                in: made
                dependsOn: [gate]
              - name: checkpoint
                type: commit
                message: "{carried.notRun} | {run.notRun} | {run.ran}"
                dependsOn: [inside]
            """);
        var runner = new FakeRunner()
            .On("lint", _ => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the lint failed"))
            .On("gate", _ => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "1 test failed"));

        var end = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            "lint (build) in the main tree: failed and passed over - the lint failed | none | "
                + "lint (build) in the main tree: failed (20): the lint failed; passed over\n"
                + "gate (test) in the main tree: failed (20): 1 test failed; handled by fix\n"
                + "inside (build) in a1's worktree: done",
            MessageOf(runner, "checkpoint"));
    }

    /// <summary>
    /// A redirect that draws a check and its commit again: the commit's later visit carries the check's later visit, which
    /// ended after the commit's first visit did, and not the first visit's check, which that commit carried.
    /// </summary>
    [Fact]
    public async Task ACommitDrawnAgainByARedirect_CarriesTheChecksThatRanAgain()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: test
              - name: save
                type: commit
                message: "{carried.ran}"
                dependsOn: [gate]
              - name: ci
                type: orient
                dependsOn: [save]
              - name: repair
                type: orient
                on: {step: ci, outcome: failure}
                then: gate
            """);
        var runner = new FakeRunner().On("ci", run => run.Visit == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));

        var end = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("gate (test) in the main tree: done", MessageOf(runner, "save"));
        Assert.Equal("gate+2 (test) in the main tree: done", MessageOf(runner, "save+2"));
    }

    /// <summary>
    /// Where the clock does not move, so every step ends at the same time, a check is carried by the commit it ended after
    /// as it was made: the second commit carries the check between the two, and not the one the first commit carried.
    /// </summary>
    [Fact]
    public async Task WhereStepsEndAtOnce_ACommitCarriesTheChecksMadeAfterTheCommitBefore()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: test
              - name: first
                type: commit
                message: "{carried.ran}"
                dependsOn: [one]
              - name: two
                type: test
                dependsOn: [first]
              - name: second
                type: commit
                message: "{carried.ran}"
                dependsOn: [two]
              - name: three
                type: test
                dependsOn: [second]
              - name: third
                type: commit
                message: "{carried.ran}"
                dependsOn: [three]
            """);
        var runner = new FakeRunner();

        var end = await new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock()).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("one (test) in the main tree: done", MessageOf(runner, "first"));
        Assert.Equal("two (test) in the main tree: done", MessageOf(runner, "second"));
        Assert.Equal("three (test) in the main tree: done", MessageOf(runner, "third"));
    }

    /// <summary>
    /// A commit that failed carried nothing away: the commit after it carries the checks before it as well as its own,
    /// back to the newest commit upstream that was done.
    /// </summary>
    [Fact]
    public async Task ACommitThatFailed_CarriesNothingAway()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: test
              - name: first
                type: commit
                message: checkpoint
                onFailure: continue
                dependsOn: [one]
              - name: two
                type: test
                dependsOn: [first]
              - name: second
                type: commit
                message: "{carried.ran}"
                dependsOn: [two]
            """);
        var runner = new FakeRunner().On("first", _ => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "a hook refused"));

        var end = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("one (test) in the main tree: done\ntwo (test) in the main tree: done", MessageOf(runner, "second"));
    }

    /// <summary>
    /// A check beside the commit before - begun before it, ended after it - is carried by the next commit: it ended after
    /// the commit before it did. It reads CI, so it holds nothing in the tree the others wait on.
    /// </summary>
    [Fact]
    public async Task ACheckBesideTheCommitBefore_ThatEndedAfterIt_IsCarriedByTheNext()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: slow
                type: ci-read
                branch: main
              - name: one
                type: test
              - name: first
                type: commit
                message: "{carried.ran}"
                dependsOn: [one]
              - name: after
                type: orient
                dependsOn: [first]
              - name: second
                type: commit
                message: "{carried.ran}"
                dependsOn: [after, slow]
            """);
        var clock = new ManualClock();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .Holding("slow", released.Task)
            .On("after", _ =>
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                released.SetResult();
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });

        var end = await new FlowCoordinator(definition, runner, new RunnerFacts(), clock).RunAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("one (test) in the main tree: done", MessageOf(runner, "first"));
        Assert.Equal("slow (ci-read) on CI: done", MessageOf(runner, "second"));
    }

    /// <summary>
    /// A deletion upstream of a commit is among its folds, for it folds what its agent left; one that discards what is not
    /// committed folds nothing, and is not.
    /// </summary>
    [Fact]
    public async Task ADeletionThatFolds_IsAmongACommitsFolds_AndOneThatDiscardsIsNot()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: kept
                type: create-agent
                agent: worker.md
              - name: dropped
                type: create-agent
                agent: worker.md
              - name: close
                type: delete-agent
                of: kept
                dependsOn: [kept]
              - name: discard
                type: delete-agent
                of: dropped
                discardUncommitted: true
                dependsOn: [dropped]
              - name: commit
                type: commit
                message: checkpoint
                dependsOn: [close, discard]
            """);
        var runner = new FakeRunner();

        var end = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["close"], FoldsOf(runner, "commit"));
    }

    /// <summary>
    /// A commit's message reads what it carries - the checks upstream of it that ended since the commit upstream of it was
    /// done, what did not check among them and what is red - and the whole run's texts: every check ended so far, the
    /// checks still to come by name and each red result not seen green since; a check of another item is not carried.
    /// </summary>
    [Fact]
    public async Task ACommitsMessage_ReadsWhatItCarries_AndWhatTheRunChecked()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: made
                type: create-agent
                agent: worker.md
              - name: fold
                type: fold
                of: made
                dependsOn: [made]
              - name: gate
                type: test
                failOn: never
                dependsOn: [fold]
              - name: audit
                type: reason
                prompt: work.md
                outputs: work.json
                independent: true
                when: 'false'
                dependsOn: [fold]
              - name: checkpoint
                type: commit
                message: "ran: {carried.ran} | not: {carried.notRun} | red: {carried.red} | to come: {run.notRun}"
                dependsOn: [gate, audit]
              - name: review
                type: reason
                prompt: work.md
                outputs: work.json
                independent: true
                dependsOn: [checkpoint]
              - name: final
                type: commit
                message: "carried: {carried.ran} | run: {run.ran} | red: {run.red} | not: {carried.notRun}"
                dependsOn: [review]
              - name: late
                type: build
                dependsOn: [final]
            """);
        var runner = new FakeRunner().On("gate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""
            {"passed": false, "exitCode": 1, "verdicts": [{"leg": "win", "verdict": "failed"}],
             "red": [{"leg": "win", "verdict": "failed", "phase": "test", "detail": "1 failed", "tail": []}], "runDirectory": null}
            """)));

        var end = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);

        var checkpoint = MessageOf(runner, "checkpoint");

        Assert.StartsWith("ran: gate (test) in the main tree: red - win failed | not: audit (independent) in the main tree: skipped", checkpoint, StringComparison.Ordinal);
        Assert.Contains(" | red: gate (test) in the main tree: win failed at test | to come: ", checkpoint, StringComparison.Ordinal);
        Assert.EndsWith("\nreview (independent)\nlate (build)", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Runs, run => run.Instance == "audit");

        Assert.Equal(
            "carried: review (independent) in the main tree: done | run: gate (test) in the main tree: red - win failed\nreview (independent) in the main tree: done"
            + " | red: gate (test) in the main tree: win failed at test | not: none",
            MessageOf(runner, "final"));
    }

    /// <summary>What a work item's commit carries is its own item's checks, never another's, and with none it says none.</summary>
    [Fact]
    public async Task AnItemsCommit_CarriesItsOwnChecks_NeverAnotherItems()
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
                  - name: gate
                    type: build
                  - name: checkpoint
                    type: commit
                    message: "{carried.ran}"
                    dependsOn: [gate]
              - name: bare
                type: commit
                message: "{carried.ran} / {carried.red} / {carried.notRun}"
            """);
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""{"list": ["x", "y"]}""")))
            .On("gate", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse($$"""
                {"passed": true, "exitCode": 0, "verdicts": [{"leg": "leg{{run.Index}}", "verdict": "passed"}], "red": [], "runDirectory": null}
                """)));

        _ = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token);

        Assert.Equal("items[0].gate (build) in the main tree: passed - leg0 passed", MessageOf(runner, "items[0].checkpoint"));
        Assert.Equal("items[1].gate (build) in the main tree: passed - leg1 passed", MessageOf(runner, "items[1].checkpoint"));
        Assert.Equal("none / none / none", MessageOf(runner, "bare"));
    }

    /// <summary>
    /// In a repeat, each iteration's commit carries what its own iteration checked, since the commit of the iteration before
    /// it: a check skipped in a later iteration - drawn ended, its when false - is among what did not check, as it ended
    /// when it was drawn.
    /// </summary>
    [Fact]
    public async Task EachIterationsCommit_CarriesItsOwnIteration_ASkippedCheckAmongIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fix
                repeat:
                  until: check.ok
                  maxIterations: 3
                steps:
                  - name: gate
                    type: build
                  - name: audit
                    type: run
                    runner: lint
                    when: iteration == 1
                  - name: save
                    type: commit
                    message: "{carried.ran} / {carried.notRun}"
                    dependsOn: [gate, audit]
                  - name: check
                    type: orient
                    dependsOn: [save]
            """);
        var checks = 0;
        var runner = new FakeRunner()
            .On("check", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["ok"] = ++checks == 2 }))
            .On("gate", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""
                {"passed": true, "exitCode": 0, "verdicts": [], "red": [], "runDirectory": null}
                """)))
            .On("audit", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""
                {"passed": true, "exitCode": 0, "verdicts": [], "red": [], "runDirectory": null}
                """)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(Token);

        Assert.Equal("fix#1.gate (build) in the main tree: passed\nfix#1.audit (run) in the main tree: passed / none", MessageOf(runner, "fix#1.save"));
        Assert.Equal("fix#2.gate (build) in the main tree: passed / fix#2.audit (run) in the main tree: skipped - its when read false", MessageOf(runner, "fix#2.save"));
        Assert.NotNull(coordinator.Instances["fix#2.audit"].FinishedAt);
    }

    /// <summary>
    /// The checks still to come are the flow's own, by step name: never a handler, a step raised for an issue, a step of
    /// the recovery, nor a step inside any of those, which run only where something raises them.
    /// </summary>
    [Fact]
    public async Task TheChecksStillToCome_AreTheFlowsOwn_NeverAHandlerARaisedStepOrTheRecoverys()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: recheck
                  type: build
            steps:
              - name: gate
                type: build
              - name: fix
                on: {step: gate, outcome: failure}
                steps:
                  - name: retest
                    type: test
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                independent: true
                raised: preExisting
                onAddress: recover
              - name: save
                type: commit
                message: "{run.notRun}"
                dependsOn: [gate]
              - name: late
                type: test
                dependsOn: [save]
            """);
        var runner = new FakeRunner().On("gate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""
            {"passed": true, "exitCode": 0, "verdicts": [], "red": [], "runDirectory": null}
            """)));

        _ = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token);

        Assert.Equal("late (test)", MessageOf(runner, "save"));
    }

    private static WorkflowDefinition Parse(string text) => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    private static IReadOnlyList<string> FoldsOf(FakeRunner runner, string instance)
        => [.. ((JsonArray)runner.Runs.Single(run => run.Instance == instance).Read!["folds"].ToNode()!).Select(item => item!.GetValue<string>())];

    private static string MessageOf(FakeRunner runner, string instance) => runner.Runs.Single(run => run.Instance == instance).Step.Message!;

    /// <summary>A runner that answers each step as its test scripted it, done where it scripted nothing, and keeps every run asked.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task> _holds = new(StringComparer.Ordinal);

        /// <summary>Every run asked, in the order asked.</summary>
        public List<FlowStepRun> Runs { get; } = [];

        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        /// <summary>Holds each run of <paramref name="step"/> open until <paramref name="until"/> completes.</summary>
        public FakeRunner Holding(string step, Task until)
        {
            _holds[step] = until;
            return this;
        }

        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (Runs)
            {
                Runs.Add(run);
            }

            if (_holds.TryGetValue(run.Step.Name, out var hold))
            {
                await hold.WaitAsync(cancellationToken);
            }

            return _scripts.GetValueOrDefault(run.Step.Name, _ => FlowStepResult.Ended(passed: true, HarnessExit.Success))(run);
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        public bool WritesTree(string runner) => false;
    }
}
