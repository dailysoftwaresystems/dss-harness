using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The asking of a reason step that may ask, and the endings that pause the run safely: the step waits on its agent's
/// questions and goes on with the answer in the same attempt, what its agents assumed is kept, and every account at a
/// limit or a provider's service failing resumes that very step - all over fake step runners, in memory.
/// </summary>
public sealed class FlowAskTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A step's output, read from JSON text.</summary>
    private static JsonNode? Output(string json)
        => JsonNode.Parse(json);

    /// <summary>The question an agent asked, in the envelope's own shape.</summary>
    private static AgentQuestion Asked(string what, string recommended)
        => new(what, "two ways stand", "the map", "the route", ["left", "right"], recommended, "it is shorter");

    /// <summary>A runner that answers each step as its test scripted it.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);

        /// <summary>Every run asked, whole.</summary>
        public List<FlowStepRun> Runs { get; } = [];

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = run => Task.FromResult(script(run));
            return this;
        }

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
            => On(step, _ => result);

        /// <inheritdoc/>
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Runs.Add(run);

            return _scripts.GetValueOrDefault(run.Step.Name, _ => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run);
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
    /// A reason step that may ask is paused on its agent's questions, whatever else depends on it waiting alone: the
    /// question shows each of them with the answer its agent recommends, and the answer a person gives - a text, read
    /// by the agent as a person wrote it - goes on in the same attempt.
    /// </summary>
    [Fact]
    public async Task AStepThatMayAsk_WaitsOnItsAgentsQuestions_AndGoesOnWithTheAnswerInTheSameAttempt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                ask: true
                prompt: work.md
                outputs: work.json
              - name: after
                type: orient
                dependsOn: [think]
            """);
        var runner = new FakeRunner().On("think", run => run.Answers.Count == 0
            ? FlowStepResult.Asking(new FlowQuestion("q0", "ask", "think", "the agent asked", ["answer", "decide", "abort"])
            {
                AgentQuestions = [Asked("which way?", "left")],
            })
            : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "left"}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var open = coordinator.OpenQuestions.Single();

        Assert.Equal("ask", open.Kind);
        Assert.Equal(["answer", "decide", "abort"], open.Options);
        Assert.Equal("which way?", open.AgentQuestions.Single().Question);
        Assert.Equal("agent", open.Recommendation.By);
        Assert.Equal(FlowInstanceState.Paused, coordinator.Instances["think"].State);

        Assert.True(coordinator.TryAnswer(open.Id, "answer", "take the left way", out var problem));

        Assert.Null(problem);

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        var think = coordinator.Instances["think"];

        Assert.Equal(FlowInstanceState.Done, think.State);
        Assert.Equal(1, think.Attempt);
        var answers = runner.Runs.Where(run => run.Step.Name == "think").Skip(1).Single().Answers;

        Assert.Equal("answer", answers.Single().Answer);
        Assert.Equal("take the left way", answers.Single().Text);
        Assert.Equal("ask", answers.Single().Kind);
    }

    /// <summary>
    /// <c>decide</c> leaves the questions to the agent: it goes on in the same attempt as an answer does, told to take
    /// what it recommended and to record each under its assumptions.
    /// </summary>
    [Fact]
    public async Task DecideLeavesTheQuestionsToTheAgent_GoingOnInTheSameAttempt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                ask: true
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner().On("think", run => run.Answers.Count == 0
            ? FlowStepResult.Asking(new FlowQuestion("q0", "ask", "think", "the agent asked", ["answer", "decide", "abort"])
            {
                AgentQuestions = [Asked("which way?", "left")],
            })
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);
        var open = coordinator.OpenQuestions.Single();

        Assert.True(coordinator.TryAnswer(open.Id, "decide", null, out _));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(1, coordinator.Instances["think"].Attempt);
        Assert.Equal("decide", runner.Runs.Where(run => run.Step.Name == "think").Skip(1).Single().Answers.Single().Answer);
    }

    /// <summary>
    /// What a step's agent assumed is kept whatever the attempt ended by, read as the step's own fact and written into
    /// the run's text of everything its agents assumed without asking.
    /// </summary>
    [Fact]
    public async Task WhatTheAgentsAssumed_IsKeptAndReadAsTheStepsFactAndTheRunsOwn()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: told
                type: pause
                dependsOn: [think]
                question: '{run.assumptions} and {think.assumptions}'
                options: [go]
            """);
        var runner = new FakeRunner().On(
            "think",
            FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}""")) with
            {
                Assumptions = [new AgentAssumption("the framework", "the newest", "the plan names none")],
            });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Single(coordinator.Instances["think"].Assumptions);

        var open = coordinator.OpenQuestions.Single();

        Assert.Contains("think' assumed about the framework: the newest (the plan names none)", open.Problem, StringComparison.Ordinal);
        Assert.Contains("the framework", open.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every account a reason step could use at its limit pauses the run safely, and the resume runs that very step:
    /// never a failure, never a question, the step stopped where it stands and begun again as a new attempt.
    /// </summary>
    [Fact]
    public async Task EveryAccountAtItsLimit_PausesTheRunSafely_AndTheResumeRunsThatVeryStep()
    {
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
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        runner.On("think", FlowStepResult.SafelyPausing("still limited after 4 tries: every account is at its limit"));

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(coordinator.OpenQuestions);
        Assert.Equal(FlowInstanceState.Stopped, coordinator.Instances["think"].State);
        Assert.Contains("every account is at its limit", coordinator.Instances["think"].Error, StringComparison.Ordinal);

        // The resume runs that very step: a new attempt of the same visit, which this time finishes.
        runner.On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}""")));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["think"].State);
        Assert.Equal(2, coordinator.Instances["think"].Attempt);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }

    /// <summary>
    /// A provider's service failing past the client's own tries pauses the run the same way every account at its limit
    /// does: the step runs again at the resume, for every model it could move to is reached through the same service.
    /// </summary>
    [Fact]
    public async Task AProvidersServiceFailing_PausesTheRunTheSameWay()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        runner.On("think", FlowStepResult.SafelyPausing("the provider's service failed past its 4 tries: unreachable"));

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(coordinator.OpenQuestions);
        Assert.Equal(FlowInstanceState.Stopped, coordinator.Instances["think"].State);

        runner.On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
    }
}
