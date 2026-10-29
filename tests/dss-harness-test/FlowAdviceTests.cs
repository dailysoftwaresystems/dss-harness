using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The run's advisor: a question the engine raises is recommended on before a person is left with it - never in the
/// person's way - and where none was sought the question says so plainly. Over a fake advisor, in memory.
/// </summary>
public sealed class FlowAdviceTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A runner that answers each step as its test scripted it.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => Task.FromResult(result);
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
            => _scripts.GetValueOrDefault(run.Step.Name, _ => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run);

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>The advisor as a fake: each asking held until the test lets it answer, and every call kept.</summary>
    private sealed class Advisor : IFlowAdvisorRunner
    {
        private readonly Func<FlowAdvisorCall, Task<FlowAdvice>> _answer;

        /// <summary>Every asking, in order.</summary>
        public List<FlowAdvisorCall> Asked { get; } = [];

        public Advisor(Func<FlowAdvisorCall, Task<FlowAdvice>> answer) => _answer = answer;

        /// <inheritdoc/>
        public Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken)
        {
            Asked.Add(call);
            return _answer(call);
        }
    }

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    private static WorkflowDefinition Advised(string? kinds = null)
        => Parse(kinds is null
            ? """
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """
            : $"""
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
              kinds: [{kinds}]
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);

    /// <summary>
    /// A question the engine raises is recommended on before a person is left with it: the recommendation marked
    /// sought while the advisor runs, then given with the answer it recommends, who wrote it and where its records
    /// are - and the person's answer still settles the question, whatever was recommended.
    /// </summary>
    [Fact]
    public async Task AQuestionTheEngineRaises_IsRecommendedOn_BeforeAPersonIsLeftWithIt()
    {
        var gate = new TaskCompletionSource<FlowAdvice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var advisor = new Advisor(_ => gate.Task);
        var runner = new FakeRunner().On("think", FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true });
        var coordinator = new FlowCoordinator(Advised(), runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The question is raised at once, its recommendation marked sought; the session does not end while the
        // advisor runs.
        await TestEventuallyAsync(() => coordinator.OpenQuestions.Count == 1 && advisor.Asked.Count == 1);

        var open = coordinator.OpenQuestions.Single();

        Assert.Equal("sought", open.Recommendation.State);
        Assert.Equal("think", advisor.Asked.Single().Step);
        Assert.Equal("turnLimit", advisor.Asked.Single().Kind);
        Assert.Equal(1, advisor.Asked.Single().Advice);
        Assert.Contains("no recommendations file", advisor.Asked.Single().Bar, StringComparison.Ordinal);
        Assert.False(session.IsCompleted);

        gate.SetResult(new FlowAdvice("more", "the step was nearly done", null, Model: "claude-opus-5-5", Directory: "steps/think/advice/1", Usd: 0.01, Tokens: 900));

        await TestEventuallyAsync(() => coordinator.OpenQuestions.Single().Recommendation.State == "given");

        var advised = coordinator.OpenQuestions.Single();

        Assert.Equal("more", advised.Recommendation.Answer);
        Assert.Equal("advisor", advised.Recommendation.By);
        Assert.Equal("claude-opus-5-5", advised.Recommendation.Model);
        Assert.Equal("steps/think/advice/1", advised.Recommendation.Advice);

        // The person's answer settles it, whatever was recommended.
        Assert.True(coordinator.TryAnswer(advised.Id, "fail", null, out _));

        gate = new TaskCompletionSource<FlowAdvice>(TaskCreationOptions.RunContinuationsAsynchronously);
        await session;
    }

    /// <summary>
    /// A person answering while the advisor still runs ends the asking: their answer is what the run goes on with,
    /// and the advice that lands after it changes nothing.
    /// </summary>
    [Fact]
    public async Task APersonAnsweringWhileTheAdvisorRuns_EndsTheAsking()
    {
        var gate = new TaskCompletionSource<FlowAdvice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var advisor = new Advisor(_ => gate.Task);
        var runner = new FakeRunner().On("think", run => run.Attempt == 1
            ? FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true }
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(Advised(), runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

        await TestEventuallyAsync(() => coordinator.OpenQuestions.Count == 1);

        var open = coordinator.OpenQuestions.Single();

        Assert.Equal("sought", open.Recommendation.State);

        // The person answers retry; the next attempt passes, and the run ends done.
        Assert.True(coordinator.TryAnswer(open.Id, "retry", null, out _));

        gate.SetResult(new FlowAdvice("more", "too late", null));

        var end = await session;

        Assert.Equal(FlowRunStatus.Done, end.Status);

        // The advice that landed after the answer changed nothing: the answered question keeps the person's answer.
        var answered = coordinator.Questions.Single(question => question.Id == open.Id);

        Assert.Equal("retry", answered.Given.Single().Answer);
        Assert.Equal("sought", answered.Recommendation.State);
    }

    /// <summary>
    /// Where the advisor obtains nothing - it failed past its retries, reached a bound, was declined by every model -
    /// the question says so, and the person is asked with the plain statement.
    /// </summary>
    [Fact]
    public async Task WhereTheAdvisorObtainsNothing_TheQuestionSaysSo()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice(null, null, null, NotObtained: "agent 'advisor' reached its 10 minutes")));
        var runner = new FakeRunner().On("think", FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true });
        var coordinator = new FlowCoordinator(Advised(), runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var open = coordinator.OpenQuestions.Single();

        Assert.Equal("notObtained", open.Recommendation.State);
        Assert.Contains("one was sought and not obtained (agent 'advisor' reached its 10 minutes)", open.Recommendation.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the orchestrator names no advisor the question says so plainly, naming it; where the advisor's kinds
    /// leave the question's kind out, that is said too; nothing is put in a person's place.
    /// </summary>
    [Fact]
    public async Task WhereNoAdvisorIsNamed_TheQuestionSaysSoPlainly()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner().On("think", FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(
            "no recommendation was sought: orchestrator 'flow' names no advisor",
            coordinator.OpenQuestions.Single().Recommendation.Why);

        // A kind the advisor's kinds leave out is said as plainly: no advisor runs for it.
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("more", "never read", null)));
        coordinator = new FlowCoordinator(Advised(kinds: "loopBound"), runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(advisor.Asked);
        Assert.Equal(
            "no recommendation was sought: the advisor's kinds leave out 'turnLimit'",
            coordinator.OpenQuestions.Single().Recommendation.Why);
    }

    /// <summary>An agent's own questions are never advised - the agent recommends within each of them - nor is the budget's own question.</summary>
    [Fact]
    public async Task AnAgentsOwnQuestions_AreNeverAdvised()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("answer", "never read", null)));
        var definition = Parse("""
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            steps:
              - name: think
                type: reason
                ask: true
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner().On("think", FlowStepResult.Asking(
            new FlowQuestion("q0", "ask", "think", "the agent asked", ["answer", "decide", "abort"])
            {
                AgentQuestions = [new DssHarness.Core.Agents.AgentQuestion("which way?", "two ways stand", "the map", "the route", ["left", "right"], "left", "it is shorter")],
            }));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(advisor.Asked);
        Assert.Equal("agent", coordinator.OpenQuestions.Single().Recommendation.By);
    }

    /// <summary>Waits a little at a time for <paramref name="done"/>, failing the test where it does not within ten seconds.</summary>
    private static async Task TestEventuallyAsync(Func<bool> done)
    {
        for (var waited = 0; waited < 500 && !done(); waited++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        }

        Assert.True(done(), "what was waited for did not happen within ten seconds");
    }
}
