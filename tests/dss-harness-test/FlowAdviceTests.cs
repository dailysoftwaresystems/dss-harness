using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The run's advisor: a question the engine raises is recommended on before a person is left with it - never in the
/// person's way - and where none was sought the question says so plainly; an answer, a pause and a stop each end an
/// advice still running, what it spent counted all the same. Over a fake advisor, in memory.
/// </summary>
public sealed class FlowAdviceTests
{
    /// <summary>Every bound a test of a session waits for: a session that hangs fails the test rather than the run's minutes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    /// <summary>The advisor as a fake: each asking answered as the test says, and every call kept with the token it was given.</summary>
    private sealed class Advisor : IFlowAdvisorRunner
    {
        private readonly Func<FlowAdvisorCall, CancellationToken, Task<FlowAdvice>> _answer;

        /// <summary>Every asking, in order.</summary>
        public List<FlowAdvisorCall> Asked { get; } = [];

        /// <summary>The token of every asking, in order.</summary>
        public List<CancellationToken> Tokens { get; } = [];

        public Advisor(Func<FlowAdvisorCall, Task<FlowAdvice>> answer) => _answer = (call, _) => answer(call);

        public Advisor(Func<FlowAdvisorCall, CancellationToken, Task<FlowAdvice>> answer) => _answer = answer;

        /// <inheritdoc/>
        public Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(call);
                Tokens.Add(cancellationToken);
            }

            return _answer(call, cancellationToken);
        }
    }

    /// <summary>
    /// An advisor that runs until its token ends it, as the real one does - spending as it goes - and answers then with
    /// what it spent, once the test lets its runner end.
    /// </summary>
    private sealed class HeldAdvisor
    {
        /// <summary>Lets an ended asking's runner end: until then it is still being torn down.</summary>
        public TaskCompletionSource TornDown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once an asking's token has ended it.</summary>
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Whether an asking's runner has ended.</summary>
        public bool RunnerEnded { get; private set; }

        public async Task<FlowAdvice> AdviseAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                Ended.TrySetResult();
            }

            await TornDown.Task;
            RunnerEnded = true;
            return new FlowAdvice(null, null, null, Directory: "steps/think/advice/1", NotObtained: "the asking was ended before the advisor answered", Usd: 0.02, Tokens: 1500);
        }
    }

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>The run's records as a test reads them: nothing kept but the last snapshot written, and every question as last written.</summary>
    private sealed class Records : IFlowRecords
    {
        /// <summary>The run as the last change wrote it.</summary>
        public FlowSnapshot? Last { get; private set; }

        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
            => Last = snapshot;

        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
        {
        }

        public void Updates(FlowUpdatesFile updates)
        {
        }
    }

    private static WorkflowDefinition Advised(string? kinds = null, string? budget = null)
        => Parse($"""
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best{(kinds is null ? string.Empty : $"\n  kinds: [{kinds}]")}{(budget is null ? string.Empty : $"\nbudget:\n  {budget}")}
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);

    /// <summary>The step's first attempt reaches its turns, a question the engine raises; every later one passes.</summary>
    private static FakeRunner AtItsTurnsFirst()
        => new FakeRunner().On("think", run => run.Attempt == 1
            ? FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true }
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));

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
        var coordinator = new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var session = coordinator.RunAsync(Token);

        // The question is raised at once, its recommendation marked sought; the session does not end while the
        // advisor runs.
        await TestEventuallyAsync(() => coordinator.OpenQuestions.Count == 1 && advisor.Asked.Count == 1);

        var open = coordinator.OpenQuestions.Single();
        var call = advisor.Asked.Single();

        Assert.Equal("sought", open.Recommendation.State);
        Assert.Equal(("think", "turnLimit", "a1"), (call.Instance, call.Kind, call.Agent));
        Assert.Equal("(orchestrator 'flow' has no recommendations file: no bar was written for this repository)", call.Bar);
        Assert.False(session.IsCompleted);

        gate.SetResult(new FlowAdvice("more", "the step was nearly done", null, Model: "claude-opus-5-5", Directory: "steps/think/advice/1", Usd: 0.01, Tokens: 900));

        var end = await session.WaitAsync(Bound, Token);
        var advised = coordinator.OpenQuestions.Single();

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(
            new FlowRecommendation("given", "more", null, "the step was nearly done", "advisor", "advisor", "claude-opus-5-5", "steps/think/advice/1"),
            advised.Recommendation);

        // The person's answer settles it, whatever was recommended.
        Assert.True(coordinator.TryAnswer(advised.Id, "fail", null, out _));
        end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>
    /// An answer given while the advisor still runs ends the asking: the advisor's token is ended, the question says the
    /// asking was ended, what the asking spent is counted against the run's budget all the same, the asking step's next
    /// attempt starts only once the advice's runner has ended - never beside it in the place the step works - and the
    /// run goes on with the answer.
    /// </summary>
    [Fact]
    public async Task AnAnswerWhileTheAdvisorRuns_EndsTheAsking_AndTheStepWaitsForItsRunner()
    {
        var held = new HeldAdvisor();
        var advisor = new Advisor((_, token) => held.AdviseAsync(token));
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? secondBesideTheAdvice = null;
        var runner = new FakeRunner().On("think", run =>
        {
            if (run.Attempt == 1)
            {
                return FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true };
            }

            secondBesideTheAdvice = !held.RunnerEnded;
            secondStarted.TrySetResult();
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var records = new Records();

        // A clock that never passes the bound on the advice's runner on its own: the runner is torn down within it.
        var coordinator = new FlowCoordinator(Advised(), runner, new RunnerFacts(), new TimerClock(), records: records, advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => coordinator.OpenQuestions.Count == 1 && advisor.Asked.Count == 1);
        var open = coordinator.OpenQuestions.Single();

        // The person answers retry through the session, as the inbox hands an answer over.
        _ = await coordinator.OfferAnswerAsync(open.Id, "retry").WaitAsync(Bound, Token);
        await held.Ended.Task.WaitAsync(Bound, Token);

        // The step does not start again while the advice's runner is still being torn down.
        _ = await Task.WhenAny(secondStarted.Task, Task.Delay(TimeSpan.FromSeconds(2), Token));
        held.TornDown.SetResult();

        var end = await session.WaitAsync(Bound, Token);
        var answered = coordinator.Questions.Single(question => question.Id == open.Id);

        Assert.True(advisor.Tokens.Single().IsCancellationRequested);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.False(secondBesideTheAdvice);
        Assert.Equal("retry", answered.Given.Single().Answer);
        Assert.Equal(
            new FlowRecommendation("cancelled", Why: "no recommendation: the question was answered by a person while the advisor ran, and its asking was ended"),
            answered.Recommendation);
        Assert.Equal((0.02, 1500L), (records.Last!.Spent.Usd, records.Last.Spent.Tokens));
    }

    /// <summary>
    /// An ended asking whose runner never ends is waited for no longer than a stopped step's runner is: once the drain's
    /// bound of its end passes it is let go of - the asking step's next attempt starts, and what the asking spent is not
    /// counted, for it never answered.
    /// </summary>
    [Fact]
    public async Task AnEndedAskingWhoseRunnerNeverEnds_IsLetGoOfAtTheDrainsBound()
    {
        var held = new HeldAdvisor();
        var advisor = new Advisor((_, token) => held.AdviseAsync(token));
        var secondStarted = false;
        var runner = new FakeRunner().On("think", run =>
        {
            if (run.Attempt == 1)
            {
                return FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true };
            }

            secondStarted = true;
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var records = new Records();
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(Advised(), runner, new RunnerFacts(), clock, records: records, advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => coordinator.OpenQuestions.Count == 1 && advisor.Asked.Count == 1);
        var open = coordinator.OpenQuestions.Single();

        _ = await coordinator.OfferAnswerAsync(open.Id, "retry").WaitAsync(Bound, Token);
        await held.Ended.Task.WaitAsync(Bound, Token);

        // The step waits for the advice's runner while the bound stands.
        await clock.WaitingAsync().WaitAsync(Bound, Token);
        Assert.False(session.IsCompleted);
        Assert.False(secondStarted);

        // The bound passes with the runner still going: the asking is let go of, and the step goes on.
        clock.Advance(TimeSpan.FromSeconds(90));
        var end = await session.WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal((2, FlowInstanceState.Done), (coordinator.Instances["think"].Attempt, coordinator.Instances["think"].State));
        Assert.False(held.RunnerEnded);
        Assert.Equal((0.0, 0L), (records.Last!.Spent.Usd, records.Last.Spent.Tokens));
        held.TornDown.SetResult();
    }

    /// <summary>
    /// A question answered while its recommendation is still owed - deferred by a pause, or sought by a session a crash
    /// ended - says it is owed no longer: no advice is sought for a question that is answered.
    /// </summary>
    [Fact]
    public async Task AQuestionAnsweredWhileItsRecommendationIsOwed_SaysItIsOwedNoLonger()
    {
        var held = new HeldAdvisor();
        held.TornDown.SetResult();
        var advisor = new Advisor((_, token) => held.AdviseAsync(token));
        var coordinator = new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => advisor.Asked.Count == 1);
        coordinator.RequestPause("command");
        _ = await session.WaitAsync(Bound, Token);

        var open = coordinator.OpenQuestions.Single();
        Assert.Equal("deferred", open.Recommendation.State);

        Assert.True(coordinator.TryAnswer(open.Id, "retry", null, out _));

        Assert.Equal(
            new FlowRecommendation("cancelled", Why: "no recommendation: the question was answered by a person before one was obtained"),
            coordinator.Questions.Single(question => question.Id == open.Id).Recommendation);
    }

    /// <summary>What an advice spent - in dollars, and every token of every kind - is counted against the run's budget.</summary>
    [Fact]
    public async Task WhatAnAdviceSpent_IsCountedAgainstTheRunsBudget()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("retry", "it went wrong", null, Usd: 0.25, Tokens: 4800)));
        var records = new Records();
        var coordinator = new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), records: records, advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal((0.25, 4800L), (records.Last!.Spent.Usd, records.Last.Spent.Tokens));
    }

    /// <summary>
    /// An advice a turn of which could not be counted charges the run's budget with what was counted and why the rest is
    /// not known, never a cost of nothing: the run's dollars are not known past it, through the same fold a reason step's
    /// turns go through.
    /// </summary>
    [Fact]
    public async Task AnAdviceATurnOfWhichWasNotCounted_LeavesTheRunsDollarsNotKnown()
    {
        const string Why = "no price for 'glm-5.3' (providers 'z', model 'glm-5.3': priceOutput)";
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("retry", "it went wrong", null, Usd: 0.25, Tokens: 4800, Uncounted: Why)));
        var records = new Records();
        var coordinator = new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), records: records, advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal((new FlowDollars(0.25, Why), 4800L), (records.Last!.Spent.Dollars, records.Last.Spent.Tokens));
    }

    /// <summary>
    /// An advice is held to what is left of the run's dollars before its reserve, the run's share handed with its call;
    /// a run that budgets no dollars hands none.
    /// </summary>
    [Fact]
    public async Task AnAdvice_IsHandedWhatIsLeftOfTheRunsDollars()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("retry", "it went wrong", null, Usd: 1.0)));

        _ = await new FlowCoordinator(Advised(budget: "usd: 10"), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor).RunAsync(Token).WaitAsync(Bound, Token);
        _ = await new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor).RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(new double?[] { 9.0, null }, advisor.Asked.Select(call => call.BudgetLeftUsd));
    }

    /// <summary>
    /// A stop while the advisor runs ends it at once - the session does not wait out the advisor's minutes - and its
    /// question says the recommendation is sought at the resume, which seeks it again.
    /// </summary>
    [Fact]
    public async Task AStopWhileTheAdvisorRuns_EndsIt_AndTheResumeSeeksItAgain()
    {
        var held = new HeldAdvisor();
        held.TornDown.SetResult();
        var calls = 0;
        var advisor = new Advisor((_, token) => Interlocked.Increment(ref calls) == 1 ? held.AdviseAsync(token) : Task.FromResult(new FlowAdvice("more", "now", null)));
        var coordinator = new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => advisor.Asked.Count == 1);
        coordinator.RequestStop();

        var end = await session.WaitAsync(Bound, Token);
        var open = coordinator.OpenQuestions.Single();

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
        Assert.True(advisor.Tokens.Single().IsCancellationRequested);
        Assert.Equal(
            new FlowRecommendation("deferred", Why: "the run was stopped while the advisor ran: the recommendation is sought when the run is resumed with this question still open"),
            open.Recommendation);

        end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(2, advisor.Asked.Count);
        Assert.Equal("given", coordinator.OpenQuestions.Single().Recommendation.State);
    }

    /// <summary>
    /// A safe pause while the advisor runs ends it - an advisor is a reason step, and none runs past a pause - its question
    /// saying the recommendation is sought at the resume.
    /// </summary>
    [Fact]
    public async Task APauseWhileTheAdvisorRuns_EndsIt_ItsQuestionSayingItIsSoughtAtTheResume()
    {
        var held = new HeldAdvisor();
        held.TornDown.SetResult();
        var advisor = new Advisor((_, token) => held.AdviseAsync(token));
        var coordinator = new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => advisor.Asked.Count == 1);
        coordinator.RequestPause("command");

        var end = await session.WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.True(advisor.Tokens.Single().IsCancellationRequested);
        Assert.Equal(
            new FlowRecommendation("deferred", Why: "the run paused while the advisor ran: the recommendation is sought when the run is resumed with this question still open"),
            coordinator.OpenQuestions.Single().Recommendation);
    }

    /// <summary>
    /// A session that ends while the advisor runs - its token cancelled - ends the advice and waits for its runner before
    /// it ends: no advisor outlives the session that started it.
    /// </summary>
    [Fact]
    public async Task ASessionEndedWhileTheAdvisorRuns_EndsItAndWaitsForItsRunner()
    {
        var held = new HeldAdvisor();
        var advisor = new Advisor((_, token) => held.AdviseAsync(token));

        // A clock whose waits pass in real time: the session's wait for the advisor's runner is bounded, not skipped.
        var coordinator = new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new ManualClock(), advisor: advisor);
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var session = coordinator.RunAsync(ending.Token);
        await TestEventuallyAsync(() => advisor.Asked.Count == 1);
        await ending.CancelAsync();
        await held.Ended.Task.WaitAsync(Bound, Token);

        // The runner is still being torn down: the session waits for it.
        _ = await Task.WhenAny(session, Task.Delay(TimeSpan.FromSeconds(1), Token));
        var endedFirst = session.IsCompleted;
        held.TornDown.SetResult();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WaitAsync(Bound, Token));

        Assert.False(endedFirst);
        Assert.True(held.RunnerEnded);
    }

    /// <summary>
    /// Where the advisor obtains nothing - it failed past its retries, reached a bound, was declined by every model -
    /// the question says so, and the person is asked with the plain statement.
    /// </summary>
    [Fact]
    public async Task WhereTheAdvisorObtainsNothing_TheQuestionSaysSo()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice(null, null, null, NotObtained: "agent 'advisor' reached its 10 minutes")));
        var coordinator = new FlowCoordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(
            new FlowRecommendation("notObtained", Why: "no recommendation: one was sought and not obtained (agent 'advisor' reached its 10 minutes)"),
            coordinator.OpenQuestions.Single().Recommendation);
    }

    /// <summary>
    /// A bar that cannot be read - the file the orchestrator names is gone - leaves the question saying why, and no
    /// advisor is started without it; the session goes on, paused on the question.
    /// </summary>
    [Fact]
    public async Task ABarThatCannotBeRead_LeavesTheQuestionSayingWhy()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("more", "never read", null)));
        var definition = Parse("""
            name: flow
            recommendations: bar.md
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var coordinator = new FlowCoordinator(
            definition,
            AtItsTurnsFirst(),
            new RunnerFacts(),
            new JumpingClock(),
            workflowFile: named => named == "bar.md"
                ? throw new HarnessException(HarnessExit.ConfigInvalid, $"'{named}', a workflow file, is not at '.harness-config/workflows/flow/{named}'; write it there, beside the definition that names it")
                : "Do the work.",
            advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(advisor.Asked);
        Assert.Equal(
            new FlowRecommendation("notObtained", Why: "no recommendation: the bar for one could not be read ('bar.md', a workflow file, is not at '.harness-config/workflows/flow/bar.md'; write it there, beside the definition that names it)"),
            coordinator.OpenQuestions.Single().Recommendation);
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

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(
            "no recommendation was sought: orchestrator 'flow' names no advisor",
            coordinator.OpenQuestions.Single().Recommendation.Why);

        // A kind the advisor's kinds leave out is said as plainly: no advisor runs for it.
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("more", "never read", null)));
        coordinator = new FlowCoordinator(Advised(kinds: "loopBound"), runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        end = await coordinator.RunAsync(Token);

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

        var end = await coordinator.RunAsync(Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(advisor.Asked);
        Assert.Equal("agent", coordinator.OpenQuestions.Single().Recommendation.By);
    }

    /// <summary>
    /// A <c>pause</c> step its run's input answers at once is never advised: the input is weighed before the advisor is
    /// sought, so no advisor is started only to be ended.
    /// </summary>
    [Fact]
    public async Task APauseStepTheInputAnswers_IsNeverAdvised()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("approve", "never read", null)));
        var definition = Parse("""
            name: flow
            inputs:
              - name: preapproved
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            steps:
              - name: approval
                type: pause
                preAnswer:
                  - when: "input.preapproved == 'yes'"
                    answer: approve
                question: 'Approve the plan?'
                options: [approve, revise]
            """);
        var coordinator = new FlowCoordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["preapproved"] = "yes" }, advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Empty(advisor.Asked);
        Assert.Equal(FlowRecommendation.NotSought, coordinator.Questions.Single().Recommendation);
    }

    /// <summary>Waits a little at a time for <paramref name="done"/>, failing the test where it does not within ten seconds.</summary>
    private static async Task TestEventuallyAsync(Func<bool> done)
    {
        for (var waited = 0; waited < 500 && !done(); waited++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), Token);
        }

        Assert.True(done(), "what was waited for did not happen within ten seconds");
    }
}
