using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The working sets a reason step's pack is seeded from (value 372), as the coordinator names them in each run it asks:
/// the step it goes on with, each reason step it depends on directly, a group by each reason step in it, then its own
/// earlier visits of the same work item, newest first - each that began, once; none for any other step.
/// </summary>
public sealed class FlowWorkingSetsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A step that goes on with another and depends on a reviewer is seeded from both, the one it goes on with first; a step it depends on that never began, and a step not a reason step, give none.</summary>
    [Fact]
    public async Task AStepThatGoesOnWithAnother_IsSeededFromItThenFromEachReasonStepItDependsOn()
    {
        var runner = await RunAsync("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: agents/worker.md
              - name: think
                type: reason
                agent: agents/thinker.md
                prompt: think.md
                outputs: think.json
                in: work
                access: write
                dependsOn: [work]
              - name: review
                type: reason
                prompt: review.md
                outputs: review.json
                dependsOn: [think]
              - name: never
                type: reason
                prompt: never.md
                outputs: never.json
                when: run.name == 'nobody'
              - name: again
                type: reason
                prompt: again.md
                outputs: again.json
                resume: think
                dependsOn: [review, never, work]
              - name: after
                type: orient
                dependsOn: [again]
            """);

        Assert.Equal([new FlowWorkedFrom("think", "a1"), new FlowWorkedFrom("review", "a2")], WorkedFrom(runner, "again"));
        Assert.Empty(WorkedFrom(runner, "think"));
        Assert.Empty(WorkedFrom(runner, "after"));
        Assert.Equal([new FlowWorkedFrom("think", "a1")], WorkedFrom(runner, "review"));
    }

    /// <summary>
    /// A fix round's rework is seeded from the step it goes on with, from what this round's reviewers read - the group it
    /// depends on, by each reviewer in it - and from the rounds before's rework, newest first; a reviewer from the step the
    /// rounds depend on and its own visits the rounds before.
    /// </summary>
    [Fact]
    public async Task AFixRoundsRework_IsSeededFromTheStepItGoesOnWith_ThisRoundsReviewers_AndTheLastRoundsRework()
    {
        var rounds = 0;
        var runner = await RunAsync(
            """
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: agents/worker.md
              - name: implement
                type: reason
                agent: agents/implementer.md
                prompt: implement.md
                outputs: implement.json
                in: work
                access: write
                dependsOn: [work]
              - name: fix
                dependsOn: [implement]
                repeat:
                  until: revise.rework.done
                  maxIterations: 3
                steps:
                  - name: review
                    steps:
                      - name: correctness
                        type: reason
                        prompt: correctness.md
                        outputs: correctness.json
                      - name: errors
                        type: reason
                        prompt: errors.md
                        outputs: errors.json
                  - name: revise
                    dependsOn: [review]
                    steps:
                      - name: rework
                        type: reason
                        prompt: rework.md
                        outputs: rework.json
                        resume: implement
            """,
            ("rework", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["done"] = ++rounds == 3 })));

        Assert.Equal(
            [new FlowWorkedFrom("implement", "a1"), new FlowWorkedFrom("fix#3.review.correctness", "a6"), new FlowWorkedFrom("fix#3.review.errors", "a7"), new FlowWorkedFrom("fix#2.revise.rework", "a1"), new FlowWorkedFrom("fix#1.revise.rework", "a1")],
            WorkedFrom(runner, "fix#3.revise.rework"));
        Assert.Equal(
            [new FlowWorkedFrom("implement", "a1"), new FlowWorkedFrom("fix#2.review.correctness", "a4"), new FlowWorkedFrom("fix#2.review.errors", "a5"), new FlowWorkedFrom("fix#1.revise.rework", "a1")],
            WorkedFrom(runner, "fix#2.revise.rework"));
        Assert.Equal(
            [new FlowWorkedFrom("implement", "a1"), new FlowWorkedFrom("fix#1.review.correctness", "a2"), new FlowWorkedFrom("fix#1.review.errors", "a3")],
            WorkedFrom(runner, "fix#1.revise.rework"));
        Assert.Equal([new FlowWorkedFrom("implement", "a1"), new FlowWorkedFrom("fix#1.review.correctness", "a2")], WorkedFrom(runner, "fix#2.review.correctness"));
    }

    /// <summary>A step that goes on with its own work is seeded from its earlier visits, never from itself.</summary>
    [Fact]
    public async Task AStepThatGoesOnWithItsOwnWork_IsSeededFromItsEarlierVisits_NeverFromItself()
    {
        var rounds = 0;
        var runner = await RunAsync(
            """
            name: flow
            steps:
              - name: loop
                repeat:
                  until: think.done
                  maxIterations: 2
                steps:
                  - name: think
                    type: reason
                    agent: agents/thinker.md
                    prompt: think.md
                    resumePrompt: again.md
                    outputs: think.json
                    resume: think
            """,
            ("think", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["done"] = ++rounds == 2 })));

        Assert.Empty(WorkedFrom(runner, "loop#1.think"));
        Assert.Equal(["loop#1.think"], WorkedFrom(runner, "loop#2.think").Select(source => source.Instance));
    }

    /// <summary>A step raised on another's outcome is seeded from that step, as from one it depends on.</summary>
    [Fact]
    public async Task AStepRaisedOnAnothersOutcome_IsSeededFromIt()
    {
        var runner = await RunAsync("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: think.md
                outputs: think.json
              - name: mend
                type: reason
                prompt: mend.md
                outputs: mend.json
                on: {step: think, outcome: success}
            """);

        Assert.Equal([new FlowWorkedFrom("think", "a1")], WorkedFrom(runner, "mend"));
    }

    /// <summary>Each item of a forEach is seeded from its own item's steps alone, never a sibling item's.</summary>
    [Fact]
    public async Task EachItem_IsSeededFromItsOwnItemsStepsAlone()
    {
        var runner = await RunAsync(
            """
            name: flow
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: think
                    type: reason
                    prompt: think.md
                    outputs: think.json
                  - name: again
                    type: reason
                    prompt: again.md
                    outputs: again.json
                    dependsOn: [think]
            """,
            ("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: JsonNode.Parse("""{"list": ["x", "y"]}""")!.AsObject())));
        var again = runner.Runs.Where(run => run.Step.Name == "again").ToList();
        var think = runner.Runs.Where(run => run.Step.Name == "think").Select(run => run.Instance).ToList();

        Assert.Equal(2, again.Count);
        Assert.Equal([[new FlowWorkedFrom(think[0], "a1")], [new FlowWorkedFrom(think[1], "a2")]], again.Select(run => run.WorkedFrom!.ToArray()));
        Assert.All(runner.Runs.Where(run => run.Step.Name == "think"), run => Assert.Empty(run.WorkedFrom!));
    }

    [Theory]
    [InlineData("fix#2.revise.rework", "fix#1.revise.rework", true)]
    [InlineData("again+2", "again", true)]
    [InlineData("rounds#1.items[0].fix#2.revise.rework", "rounds#1.items[0].fix#1.revise.rework", true)]
    [InlineData("rounds#2.items[0].fix#1.revise.rework", "rounds#1.items[0].fix#1.revise.rework", false)]
    [InlineData("items[0].again", "items[1].again", false)]
    [InlineData("a[0].r#2.b[1].c", "a[0].r#1.b[1].c", false)]
    [InlineData("a[0].r#1.b[1].c#2", "a[0].r#1.b[1].c#1", true)]
    [InlineData("fix#1.revise.rework", "fix#1.review.correctness", false)]
    [InlineData("fix#1.rework", "fix#1.reworked", false)]
    public void TwoIds_AreVisitsOfOneStepOfOneWorkItem_WhereTheyDifferOnlyInMarksAfterTheInnermostItem(string one, string other, bool same)
    {
        Assert.Equal(same, FlowInstanceId.SameStep(one, other));
        Assert.Equal(same, FlowInstanceId.SameStep(other, one));
    }

    private static IReadOnlyList<FlowWorkedFrom> WorkedFrom(Runner runner, string instance)
        => runner.Runs.Single(run => run.Instance == instance).WorkedFrom!;

    private static async Task<Runner> RunAsync(string text, params (string Step, Func<FlowStepRun, FlowStepResult> Script)[] scripts)
    {
        var runner = new Runner();

        foreach (var (step, script) in scripts)
        {
            runner.On(step, script);
        }

        var end = await new FlowCoordinator(WorkflowFileParser.Parse("workflows/flow/flow.yml", text), runner, new ConfiguredRunnerFacts(new HarnessConfig()), new JumpingClock(), runName: "r1").RunAsync(Token);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        return runner;
    }

    /// <summary>A runner that answers each step by its script, keeping every run it was asked.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);

        /// <summary>Every run asked, in order.</summary>
        public List<FlowStepRun> Runs { get; } = [];

        public Runner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (Runs)
            {
                Runs.Add(run);
            }

            return Task.FromResult(_scripts.GetValueOrDefault(run.Step.Name, _ => FlowStepResult.Ended(passed: true, HarnessExit.Success))(run));
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
