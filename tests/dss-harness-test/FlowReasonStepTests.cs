using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The engine's half of a reason step: the agent a step that names none is given - the engine's own read-only one,
/// named with its model before its record - the agent a step that goes on with another's work works as, the prompt
/// files read and filled where the step starts with each skill's name left for its runner, and the read-only agent
/// deleted once no step can go on with its work.
/// </summary>
public sealed class FlowReasonStepTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

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
            Runs.Add(run);
            return Task.FromResult(_scripts.GetValueOrDefault(run.Step.Name, _ => FlowStepResult.Ended(passed: true, HarnessExit.Success))(run));
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    /// <summary>The deletions the engine asked for, as a test reads them.</summary>
    private sealed class Deletions
    {
        /// <summary>Every agent the engine asked to delete, in order.</summary>
        public List<string> Agents { get; } = [];

        public Task<CommandOutcome> DeleteAsync(string run, string agent, CancellationToken cancellationToken)
        {
            Agents.Add(agent);
            return Task.FromResult(new CommandOutcome(0, "deleted"));
        }
    }

    private static FlowCoordinator Coordinator(
        WorkflowDefinition definition,
        Runner runner,
        IReadOnlyDictionary<string, string>? models = null,
        Func<string, string>? workflowFile = null,
        Deletions? deletions = null)
        => new(
            definition,
            runner,
            new ConfiguredRunnerFacts(new HarnessConfig()),
            new JumpingClock(),
            inputs: null,
            records: null,
            runName: "r1",
            agentModels: models,
            workflowFile: workflowFile,
            deleteAgent: deletions is null ? null : (run, agent, token) => deletions.DeleteAsync(run, agent, token));

    /// <summary>
    /// A reason step that names no agent is given the engine's own read-only one - named a1, with the model resolved
    /// for it, both in its record before its runner is asked - and works in it.
    /// </summary>
    [Fact]
    public async Task AReasonStepThatNamesNoAgent_IsGivenTheEnginesOwnReadOnlyAgent()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
            """);
        var runner = new Runner();
        var coordinator = Coordinator(definition, runner, new Dictionary<string, string>(StringComparer.Ordinal) { ["plan"] = "model-one" });

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(new FlowMadeAgent("a1", "model-one"), runner.Runs.Single().Made);
        Assert.Equal("a1", runner.Runs.Single().WorksIn);
        Assert.Equal("a1", coordinator.Instances["plan"].Made);
    }

    /// <summary>A reason step that works in an agent (in) works in that create-agent's and makes none of its own.</summary>
    [Fact]
    public async Task AReasonStepThatNamesAnAgent_WorksInIt_AndMakesNoneOfItsOwn()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: think
                type: reason
                in: work
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
                dependsOn: [work]
            """);
        var runner = new Runner().On("work", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["agent"] = run.Made!.Name }));
        var coordinator = Coordinator(definition, runner);

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        var think = runner.Runs.Single(asked => asked.Step.Name == "think");
        Assert.Equal("a1", think.WorksIn);
        Assert.Null(think.Made);
        Assert.Null(coordinator.Instances["think"].Made);
    }

    /// <summary>
    /// A reason step that goes on with another's work (resume) works as that step's agent - the engine's own read-only
    /// one the resumed step was given - and makes none of its own.
    /// </summary>
    [Fact]
    public async Task AReasonStepThatGoesOnWithAnothersWork_WorksAsThatStepsAgent()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
              - name: check
                type: reason
                prompt: prompts/plan.md
                resume: plan
                resumePrompt: prompts/check.md
                outputs: schemas/check.schema.json
                dependsOn: [plan]
            """);
        var runner = new Runner();
        var coordinator = Coordinator(definition, runner);

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("a1", runner.Runs.Single(asked => asked.Step.Name == "check").WorksIn);
        Assert.Null(coordinator.Instances["check"].Made);
    }

    /// <summary>
    /// A reason step's prompt file is read and filled where the step starts, every name but a skill's filled by the
    /// same roots an expression reads, and handed to its runner under prompt; a name that reads nothing fails the step.
    /// </summary>
    [Fact]
    public async Task AReasonStepsPromptFile_IsReadAndFilledWhereTheStepStarts_SkillsLeftForItsRunner()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
            """);
        var runner = new Runner();
        var coordinator = Coordinator(
            definition,
            runner,
            workflowFile: named => named == "prompts/plan.md" ? "Plan the work for run {run.name} with {skill.planning}." : throw new ArgumentException(named));

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("Plan the work for run r1 with {skill.planning}.", runner.Runs.Single().Read!["prompt"].AsScalar("prompt"));
        Assert.False(runner.Runs.Single().Read!.ContainsKey("resumePrompt"));
    }

    /// <summary>
    /// A resume prompt is read and filled in place of the step's own where the step goes on with another's work; the
    /// step that begins its own work is handed its own prompt, never the resumed one.
    /// </summary>
    [Fact]
    public async Task AResumePromptIsReadInPlaceOfTheStepsOwn_WhereTheStepGoesOnWithAnothersWork()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
              - name: check
                type: reason
                prompt: prompts/plan.md
                resume: plan
                resumePrompt: prompts/again.md
                outputs: schemas/again.schema.json
                dependsOn: [plan]
            """);
        var runner = new Runner();
        var end = await Coordinator(
            definition,
            runner,
            workflowFile: named => named == "prompts/plan.md" ? "First attempt."
                : named == "prompts/again.md" ? "Going on with {run.name}."
                : throw new ArgumentException(named)).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal("First attempt.", runner.Runs.Single(asked => asked.Step.Name == "plan").Read!["prompt"].AsScalar("prompt"));
        Assert.Equal("Going on with r1.", runner.Runs.Single(asked => asked.Step.Name == "check").Read!["resumePrompt"].AsScalar("resumePrompt"));
    }

    /// <summary>
    /// A read-only agent no step goes on with is deleted when its step ends done and the run goes on; one a later step
    /// goes on with is kept until that step has ended, and then deleted.
    /// </summary>
    [Fact]
    public async Task AReadOnlyAgentIsDeletedWhenNoStepCanGoOnWithItsWork()
    {
        var alone = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
            """);
        var aloneDeletions = new Deletions();
        var end = await Coordinator(alone, new Runner(), deletions: aloneDeletions).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["a1"], aloneDeletions.Agents);

        var resumed = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
              - name: check
                type: reason
                prompt: prompts/plan.md
                resume: plan
                resumePrompt: prompts/check.md
                outputs: schemas/check.schema.json
                dependsOn: [plan]
            """);
        var resumedDeletions = new Deletions();
        var endTwo = await Coordinator(resumed, new Runner(), deletions: resumedDeletions).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, endTwo.Status);
        Assert.Equal(["a1"], resumedDeletions.Agents);
    }

    /// <summary>A step that fails keeps its agent live, for its work may yet be gone on with.</summary>
    [Fact]
    public async Task AReasonStepThatFails_KeepsItsAgentLive()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
            """);
        var deletions = new Deletions();
        var end = await Coordinator(definition, new Runner().On("plan", _ => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "it failed")), deletions: deletions).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Empty(deletions.Agents);
    }

    /// <summary>
    /// A step that resumes itself begins work of its own again, and is given the engine's read-only agent like one
    /// that resumes nothing: named, with its model, and working in it.
    /// </summary>
    [Fact]
    public async Task AStepThatResumesItself_IsGivenTheEnginesOwnAgent()
    {
        var rounds = 0;
        var definition = Parse("""
            name: flow
            steps:
              - name: loop
                repeat:
                  until: think.done
                  maxIterations: 2
                steps:
                  - name: think
                    type: reason
                    prompt: prompts/plan.md
                    resumePrompt: prompts/again.md
                    outputs: schemas/plan.schema.json
                    resume: think
            """);
        var runner = new Runner().On("think", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["done"] = ++rounds == 2 }));
        var coordinator = Coordinator(definition, runner, new Dictionary<string, string>(StringComparer.Ordinal) { ["loop.think"] = "model-one" });

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.All(runner.Runs, run => Assert.Equal(("a1", "model-one"), (run.Made!.Name, run.Made.Model)));
        Assert.All(runner.Runs, run => Assert.Equal("a1", run.WorksIn));
    }

    /// <summary>
    /// A chain of resumed work keeps its agent until the chain's last step has ended: the step that goes on with a
    /// resumer - two links from the maker - holds the agent as a direct resumer does, and the group it sat in settling
    /// is what deletes it.
    /// </summary>
    [Fact]
    public async Task AChainOfResumedWork_KeepsItsAgent_UntilTheChainsLastStepHasEnded()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
              - name: fix
                dependsOn: [plan]
                steps:
                  - name: check
                    type: reason
                    prompt: prompts/plan.md
                    resume: plan
                    outputs: schemas/check.schema.json
                  - name: mend
                    type: reason
                    prompt: prompts/plan.md
                    resume: check
                    outputs: schemas/mend.schema.json
                    dependsOn: [check]
            """);
        var deletions = new Deletions();
        var end = await Coordinator(definition, new Runner(), deletions: deletions).RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["a1"], deletions.Agents);
    }

    /// <summary>A step that goes on with the work of one the flow never ran is never run itself, saying why - not failed as the engine's fault.</summary>
    [Fact]
    public async Task AStepThatGoesOnWithWorkThatNeverRan_IsNeverRun_SayingWhy()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: reason
                prompt: prompts/plan.md
                outputs: schemas/plan.schema.json
                when: run.name == 'nobody'
              - name: check
                type: reason
                prompt: prompts/plan.md
                resume: plan
                outputs: schemas/check.schema.json
                dependsOn: [plan]
            """);
        var runner = new Runner();
        var coordinator = Coordinator(definition, runner);
        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.DoesNotContain(runner.Runs, asked => asked.Step.Name == "check");
        Assert.Equal(FlowInstanceState.NotRun, coordinator.Instances["check"].State);
        Assert.StartsWith("the step it goes on with never made an agent", coordinator.Instances["check"].NotRunBecause, StringComparison.Ordinal);
    }
}
