using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The run's advisor: every question the engine raises is recommended on before a person is asked it - never in the
/// person's way - the budget's own among them; an answer, a pause and a stop each end an advice still running, what it
/// spent counted all the same. Over a fake advisor, in memory - and, where what an advice is given to spend is the
/// question, over the advisor runner itself and a fake client that holds each launch to its bound as a client does.
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

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run and ends when the test lets it.</summary>
        public FakeRunner OnAsync(string step, Func<FlowStepRun, Task<FlowStepResult>> script)
        {
            _scripts[step] = script;
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

    /// <summary>The run's records as a test reads them: the last snapshot written, the kind of every change, and the answers refused as last written.</summary>
    private sealed class Records : IFlowRecords
    {
        private readonly List<FlowChangeKind> _kinds = [];

        /// <summary>The run as the last change wrote it.</summary>
        public FlowSnapshot? Last { get; private set; }

        /// <summary>Every answer handed over that the run refused, as the questions were last written.</summary>
        public IReadOnlyList<FlowRefusedAnswer> Refused { get; private set; } = [];

        /// <summary>Whether a change of <paramref name="kind"/> was written.</summary>
        public bool Saw(FlowChangeKind kind) => Count(kind) > 0;

        /// <summary>How many changes of <paramref name="kind"/> were written.</summary>
        public int Count(FlowChangeKind kind)
        {
            lock (_kinds)
            {
                return _kinds.Count(written => written == kind);
            }
        }

        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            lock (_kinds)
            {
                _kinds.Add(kind);
            }

            Last = snapshot;
        }

        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
            => Refused = refused ?? [];

        public void Updates(FlowUpdatesFile updates)
        {
        }

        public void AffectedTests(IReadOnlyList<FlowAffectedTests> entries)
        {
        }
    }

    /// <summary>
    /// One reason step under the run's advisor, with <paramref name="budget"/> where one is given, and the input the
    /// advisor runner's prompt reads (<c>request</c>) where <paramref name="input"/> says so.
    /// </summary>
    private static WorkflowDefinition Advised(string? budget = null, bool input = false)
        => Parse($"""
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best{(budget is null ? string.Empty : $"\nbudget:\n  {budget}")}{(input ? "\ninputs:\n  - name: request" : string.Empty)}
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
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor);

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
        var coordinator = TestAdvice.Coordinator(Advised(), runner, new RunnerFacts(), new TimerClock(), records: records, advisor: advisor);

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
        var coordinator = TestAdvice.Coordinator(Advised(), runner, new RunnerFacts(), clock, records: records, advisor: advisor);

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
    /// An advice works in the tree of the agent the asking step works in, and holds it while its runner runs, though the
    /// asking step holds nothing while it waits: a step that works as the same agent - here one that goes on with the same
    /// reading step's work, whose start brings that tree to the main checkout's HEAD - waits on the claim while the advice
    /// runs, though the run's reasoning slots have room, and starts once the advice has ended, never under it.
    /// </summary>
    [Fact]
    public async Task AnAdvice_HoldsTheTreeItWorksIn_SoAStepWorkingAsTheSameAgentStartsOnceItHasEnded()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 4
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: again
                type: reason
                resume: think
                prompt: work.md
                outputs: work.json
                dependsOn: [think]
              - name: more
                type: reason
                resume: think
                prompt: work.md
                outputs: work.json
                dependsOn: [think]
            """);
        var gate = new TaskCompletionSource<FlowAdvice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var advisor = new Advisor(_ => gate.Task);
        var moreStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("again", FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true })
            .On("more", _ =>
            {
                moreStarted.TrySetResult();
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => advisor.Asked.Count == 1);

        // The advice works in the agent think made, which more works as too: more is not started beside it.
        _ = await Task.WhenAny(moreStarted.Task, Task.Delay(TimeSpan.FromSeconds(2), Token));

        Assert.False(moreStarted.Task.IsCompleted, "'more' started while the advice ran in the tree it works in");
        Assert.Equal((coordinator.Instances["think"].Made, "again"), (advisor.Asked.Single().Agent, advisor.Asked.Single().Instance));
        Assert.Equal((FlowInstanceState.Paused, FlowInstanceState.NotStarted), (coordinator.Instances["again"].State, coordinator.Instances["more"].State));

        gate.SetResult(new FlowAdvice("more", "the step was nearly done", null, Model: "claude-opus-5-5", Directory: "steps/again/advice/1"));
        await moreStarted.Task.WaitAsync(Bound, Token);
        var end = await session.WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["more"].State);
        Assert.Equal(coordinator.Instances["think"].Made, coordinator.Instances["more"].WorksIn);
    }

    /// <summary>
    /// A person answers a question recommended: between sessions no advisor runs, so an answer to a question still owing
    /// its recommendation - here one a stop deferred - is refused, nothing answered and the recommendation still owed, the
    /// refusal naming resume-orchestrator, which seeks it first; abort is taken all the same, and settles nothing of the
    /// question, which stays open owing its recommendation.
    /// </summary>
    [Fact]
    public async Task AnAnswerBetweenSessions_ToAQuestionOwingItsRecommendation_IsRefused_AndAbortIsTaken()
    {
        var held = new HeldAdvisor();
        held.TornDown.SetResult();
        var advisor = new Advisor((_, token) => held.AdviseAsync(token));
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), runName: "r1", advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => advisor.Asked.Count == 1);
        coordinator.RequestStop();
        _ = await session.WaitAsync(Bound, Token);

        var open = coordinator.OpenQuestions.Single();
        Assert.Equal("deferred", open.Recommendation.State);

        Assert.False(coordinator.TryAnswer(open.Id, "retry", null, out var problem));
        Assert.Equal(
            "question 'q1' of run 'r1' still owes the recommendation it is answered with, and no advisor seeks one between sessions, so nothing was answered; resume-orchestrator seeks it first, and 'abort' is taken now",
            problem);

        // Nothing was answered: the question is open as it was, its recommendation still owed.
        var refused = coordinator.OpenQuestions.Single();
        Assert.Equal((open.Id, 0, "deferred"), (refused.Id, refused.Given.Count, refused.Recommendation.State));

        Assert.True(coordinator.TryAnswer(open.Id, "abort", null, out var none));
        Assert.Null(none);

        var aborted = coordinator.Questions.Single();
        Assert.Equal(("open", "abort", "deferred"), (aborted.State, aborted.Given.Single().Answer, aborted.Recommendation.State));
        Assert.Single(advisor.Asked);
    }

    /// <summary>What an advice spent - in dollars, and every token of every kind - is counted against the run's budget.</summary>
    [Fact]
    public async Task WhatAnAdviceSpent_IsCountedAgainstTheRunsBudget()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("retry", "it went wrong", null, Usd: 0.25, Tokens: 4800)));
        var records = new Records();
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), records: records, advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal((0.25, 4800L), (records.Last!.Spent.Usd, records.Last.Spent.Tokens));
    }

    /// <summary>
    /// An advice that spends the run to its budget's reserve pauses the run as it is taken, while a machine step still
    /// runs - never only once that step ends and wakes the loop: what it spent is weighed against the budget before the
    /// loop waits again.
    /// </summary>
    [Fact]
    public async Task AnAdviceThatSpendsTheRunToItsReserve_PausesItAtOnce_WhileAMachineStepStillRuns()
    {
        var (coordinator, records, _, fold, _) = DrainingBudgetPause(holding: false);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => records.Saw(FlowChangeKind.PauseBegan));

        Assert.Equal(FlowInstanceState.Running, coordinator.Instances["folding"].State);
        Assert.Equal(45.10, records.Last!.Spent.Usd);

        fold.SetResult();
        var end = await session.WaitAsync(Bound, Token);

        Assert.Equal((FlowRunStatus.Paused, "budget"), (end.Status, end.Pause!.Cause));
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
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), records: records, advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal((new FlowDollars(0.25, Why), 4800L), (records.Last!.Spent.Dollars, records.Last.Spent.Tokens));
    }

    /// <summary>
    /// An advice is held to its own definition's bounds and never to what is left of the run's dollars: a run whose
    /// share before its reserve is less than the advisor's own bound launches the advisor on its own bound all the same,
    /// as a run that budgets no dollars does.
    /// </summary>
    [Fact]
    public async Task AnAdvice_IsHeldToItsOwnBounds_NeverToWhatIsLeftOfTheRunsDollars()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAdvisorAsync(temp);
        var spends = new FakeRunner().On("think", run =>
        {
            if (run.Attempt > 1)
            {
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            }

            // A dollar is left of the run's share before its reserve of 9.00 USD: less than the advisor's own two, and
            // more than the advice spends, so the advice pauses nothing.
            run.Events.Used(new FlowTurnSpend(new DssHarness.Core.Agents.AgentUsage(1_000, 0, 0, 0, 0, 0), 8.0));
            return FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true };
        });

        var budgeted = await AdvisedByTheRunner(kit, agents, Advised(budget: "usd: 10", input: true), spends).RunAsync(Token).WaitAsync(Bound, Token);
        var unbudgeted = await AdvisedByTheRunner(kit, agents, Advised(input: true), AtItsTurnsFirst()).RunAsync(Token).WaitAsync(Bound, Token);

        // Neither run pauses for its budget: each waits on its step's question alone, recommended.
        Assert.Equal((FlowRunStatus.Paused, (FlowPauseState?)null, FlowRunStatus.Paused), (budgeted.Status, budgeted.Pause, unbudgeted.Status));
        Assert.Equal(["given", "given"], new[] { budgeted, unbudgeted }.Select(end => end.OpenQuestions.Single().Recommendation.State));
        Assert.Equal<(int?, decimal?, TimeSpan?)>(
            [AdvisorRunnerTests.BoundedAdvisorLimits, AdvisorRunnerTests.BoundedAdvisorLimits],
            agents.Asked.Select(asked => (asked.Limits.MaxTurns, asked.Limits.MaxBudgetUsd, asked.Limits.MaxDuration)));
    }

    /// <summary>
    /// A budget's pause, past its reserve, advises every question it leaves - one a machine step the pause left running
    /// raised as it drained, and the budget's own - each on the advisor's own bounds, never on what is left of the run's
    /// dollars, which is nothing: each recommendation is obtained, what each advice spent is counted on its own measure
    /// and in the run's total, and that spend, taken while the run is pausing, asks for no second pause - the session ends
    /// paused once, each question advised once. The advice that spent the run to its reserve, a question's before it, was
    /// held to its own bounds too, though less was left of the run's share.
    /// </summary>
    [Fact]
    public async Task EveryQuestionABudgetsPausePastItsReserveLeaves_IsRecommendedOnTheAdvisorsOwnBounds_ItsSpendCounted()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAdvisorAsync(temp);
        var definition = Parse("""
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            budget:
              usd: 50
            inputs:
              - name: request
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                dependsOn: [work]
              - name: folding
                type: fold
                of: work
                dependsOn: [think]
              - name: approval
                type: pause
                question: 'Go on?'
                options: [go, stay]
                dependsOn: [think]
            """);
        var fold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .On("think", run =>
            {
                // Twenty cents are left of the run's share before its reserve of 45.00 USD: the approval's advice, asked
                // while the fold runs, spends the run past it, so the budget's pause leaves the fold running.
                run.Events.Used(new FlowTurnSpend(new DssHarness.Core.Agents.AgentUsage(1_000, 0, 0, 0, 0, 0), 44.80));
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            })
            .OnAsync("folding", async _ =>
            {
                await fold.Task;
                return FlowStepResult.Standing(new FlowDryRun(RequestStanding.Refused, [], [], [RefusalKind.Conflict], "1 path conflicts") { Conflicting = ["src/a.cs"] });
            });
        var records = new Records();
        var coordinator = AdvisedByTheRunner(kit, agents, definition, runner, records);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => records.Saw(FlowChangeKind.PauseCompleted));
        fold.SetResult();
        var end = await session.WaitAsync(Bound, Token);
        var open = coordinator.OpenQuestions;
        var asked = agents.Asked;

        Assert.Equal((FlowRunStatus.Paused, "budget", 1), (end.Status, end.Pause!.Cause, records.Count(FlowChangeKind.PauseBegan)));
        Assert.Equal([("q1", "pause"), ("q2", "foldConflicts"), ("q3", "runBudget")], open.Select(question => (question.Id, question.Kind)));

        // The approval's advice first; the two the complete pause seeks after it, side by side, each once, in no order of
        // their own.
        Assert.Equal("approval", asked[0].StepInstance.Split('/')[0]);
        Assert.Equal(["folding", FlowQuestions.RunsOwn], asked.Skip(1).Select(request => request.StepInstance.Split('/')[0]).Order(StringComparer.Ordinal));
        Assert.All(asked, request => Assert.Equal(AdvisorRunnerTests.BoundedAdvisorLimits, (request.Limits.MaxTurns, request.Limits.MaxBudgetUsd, request.Limits.MaxDuration)));
        Assert.Equal(
            [
                Recommended("go", asked[0]),
                Recommended("retry", asked.Single(request => request.StepInstance.StartsWith("folding/", StringComparison.Ordinal))),
                Recommended("more", asked.Single(request => request.StepInstance.StartsWith($"{FlowQuestions.RunsOwn}/", StringComparison.Ordinal))),
            ],
            open.Select(question => question.Recommendation));

        // Each advice is charged where it was asked - the approval's and the fold's on their steps, the budget's on the
        // run's own measure - and all three in the run's total, beside the step's own spend.
        Assert.Equal([AdviceSpent, AdviceSpent], new[] { "approval", "folding" }.Select(step => coordinator.Instances[step].Measure!.Advice));
        Assert.Equal((AdviceSpent, Math.Round(44.80 + (3 * AdviceUsd), 6), 1_000L + (3 * AdviceTurn.AllTokens)), (records.Last!.Spent.Advice, Math.Round(records.Last.Spent.Usd, 6), records.Last.Spent.Tokens));
    }

    /// <summary>
    /// A resume of a run past its budget's reserve - its question still open, a build's failure owed to the resume by the
    /// pause that drained it - raises the failure and advises it once, on the advisor's own bounds and never on what is
    /// left of the run's dollars, which is nothing: the recommendation is obtained, what it spent is counted on the step
    /// and in the run's total, the run is paused on its budget again, and the next resume seeks nothing more.
    /// </summary>
    [Fact]
    public async Task AResumeOfARunPastItsReserve_AdvisesTheOwedFailureOnce_OnTheAdvisorsOwnBounds()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAdvisorAsync(temp);
        var definition = Parse("""
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            budget:
              usd: 50
            inputs:
              - name: request
            steps:
              - name: gate
                type: build
                onFailure: pause
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var gateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateFails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .OnAsync("gate", async _ =>
            {
                gateStarted.TrySetResult();
                await gateFails.Task;
                return FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "a leg failed");
            })
            .OnAsync("think", async run =>
            {
                await gateStarted.Task;
                run.Events.Used(new FlowTurnSpend(new DssHarness.Core.Agents.AgentUsage(1_000, 0, 0, 0, 0, 0), 45.10));
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        var records = new Records();
        var coordinator = AdvisedByTheRunner(kit, agents, definition, runner, records);

        // The budget's pause drains the build, which fails while it does: its failure is owed to the resume, and the
        // budget's own question is advised before the session ends.
        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => records.Saw(FlowChangeKind.PauseCompleted));
        gateFails.SetResult();
        var first = await session.WaitAsync(Bound, Token);

        Assert.Equal((FlowRunStatus.Paused, "budget"), (first.Status, first.Pause!.Cause));
        Assert.Equal(["runBudget"], coordinator.OpenQuestions.Select(question => question.Kind));

        // The resume raises the failure and advises it; the budget's open question pauses the run again, and the resume
        // after it seeks nothing more.
        var second = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
        var third = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
        var failure = coordinator.OpenQuestions.Single(question => question.Kind == "failure");
        var asked = agents.Asked;

        Assert.Equal((FlowRunStatus.Paused, "budget", FlowRunStatus.Paused, "budget"), (second.Status, second.Pause!.Cause, third.Status, third.Pause!.Cause));
        Assert.Equal([FlowQuestions.RunsOwn, "gate"], asked.Select(request => request.StepInstance.Split('/')[0]));
        Assert.All(asked, request => Assert.Equal(AdvisorRunnerTests.BoundedAdvisorLimits, (request.Limits.MaxTurns, request.Limits.MaxBudgetUsd, request.Limits.MaxDuration)));
        Assert.Equal(Recommended("retry", asked[1]), failure.Recommendation);
        Assert.Equal(AdviceSpent, coordinator.Instances["gate"].Measure!.Advice);
        Assert.Equal((AdviceSpent, Math.Round(45.10 + (2 * AdviceUsd), 6), 1_000L + (2 * AdviceTurn.AllTokens)), (records.Last!.Spent.Advice, Math.Round(records.Last.Spent.Usd, 6), records.Last.Spent.Tokens));
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
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor);

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
    /// A safe pause while the advisor runs never ends it: the advice finishes as a machine step the pause leaves running
    /// does - its token never ended, the session waiting for it past the pause's completion - and its recommendation is
    /// kept, so the question the pause leaves to a person is recommended before the session ends, and the person's
    /// answer between sessions is taken.
    /// </summary>
    [Fact]
    public async Task APauseWhileTheAdvisorRuns_LetsItFinish_AndTheQuestionIsRecommendedBeforeTheSessionEnds()
    {
        var gate = new TaskCompletionSource<FlowAdvice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var advisor = new Advisor((_, token) => gate.Task.WaitAsync(token));
        var records = new Records();
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), records: records, advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => advisor.Asked.Count == 1);
        coordinator.RequestPause("command");
        await TestEventuallyAsync(() => records.Saw(FlowChangeKind.PauseCompleted));

        // The pause is complete and the advice still runs: its token is not ended, and the session waits for it.
        Assert.False(advisor.Tokens.Single().IsCancellationRequested);
        Assert.False(session.IsCompleted);
        Assert.Equal("sought", coordinator.OpenQuestions.Single().Recommendation.State);

        gate.SetResult(new FlowAdvice("more", "the step was nearly done", null, Model: "claude-opus-5-5", Directory: "steps/think/advice/1"));
        var end = await session.WaitAsync(Bound, Token);
        var question = coordinator.OpenQuestions.Single();

        Assert.Equal((FlowRunStatus.Paused, "command"), (end.Status, end.Pause!.Cause));
        Assert.Single(advisor.Asked);
        Assert.Equal(
            new FlowRecommendation("given", "more", null, "the step was nearly done", "advisor", "advisor", "claude-opus-5-5", "steps/think/advice/1"),
            question.Recommendation);

        Assert.True(coordinator.TryAnswer(question.Id, "fail", null, out _));
        Assert.Equal("fail", coordinator.Questions.Single().Given.Single().Answer);
    }

    /// <summary>
    /// An answer a person gives within a live session while the advisor still works is taken, a pause under way or not:
    /// it ends the asking, the question saying it was answered while the advisor ran - the decision that a person who is
    /// there never waits for the recommendation stands beside the refusal of an answer nothing recommends.
    /// </summary>
    [Fact]
    public async Task AnAnswerWhileAPausedRunsAdvisorWorks_IsTaken_AndEndsTheAsking()
    {
        var held = new HeldAdvisor();
        held.TornDown.SetResult();
        var advisor = new Advisor((_, token) => held.AdviseAsync(token));
        var records = new Records();
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), records: records, advisor: advisor);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => advisor.Asked.Count == 1);
        coordinator.RequestPause("command");
        await TestEventuallyAsync(() => records.Saw(FlowChangeKind.PauseCompleted));
        var open = coordinator.OpenQuestions.Single();

        Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAnswerAsync(open.Id, "fail", inbox: "e1").WaitAsync(Bound, Token));
        var end = await session.WaitAsync(Bound, Token);
        var answered = coordinator.Questions.Single();

        // The step ends failed by the answer, and the run stays paused, its failure owed to the resume.
        Assert.Equal((FlowRunStatus.Paused, FlowInstanceState.Failed), (end.Status, coordinator.Instances["think"].State));
        Assert.True(advisor.Tokens.Single().IsCancellationRequested);
        Assert.Equal(("answered", "fail"), (answered.State, answered.Given.Single().Answer));
        Assert.Equal(
            new FlowRecommendation("cancelled", Why: "no recommendation: the question was answered by a person while the advisor ran, and its asking was ended"),
            answered.Recommendation);
        Assert.Empty(records.Refused);
    }

    /// <summary>
    /// A budget whose pause drains: a question a machine step the pause left running raises meanwhile is deferred, and
    /// once the pause is complete the budget's question is raised and every open question owing its recommendation is
    /// advised before the session ends - the deferred one with it - so the person the pause leaves them to is asked each
    /// recommended.
    /// </summary>
    [Fact]
    public async Task EveryQuestionABudgetsPauseLeaves_IsRecommended_OnceThePauseIsComplete()
    {
        var (coordinator, records, advisor, fold, _) = DrainingBudgetPause(holding: false);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => records.Saw(FlowChangeKind.PauseCompleted));
        fold.SetResult();
        var end = await session.WaitAsync(Bound, Token);
        var open = coordinator.OpenQuestions;

        Assert.Equal((FlowRunStatus.Paused, "budget"), (end.Status, end.Pause!.Cause));
        Assert.Equal([("q1", "pause"), ("q2", "foldConflicts"), ("q3", "runBudget")], open.Select(question => (question.Id, question.Kind)));
        Assert.Equal(
            [
                new FlowRecommendation("given", "go", null, "advised for pause", "advisor", "advisor"),
                new FlowRecommendation("given", "retry", null, "advised for foldConflicts", "advisor", "advisor"),
                new FlowRecommendation("given", "more", null, "advised for runBudget", "advisor", "advisor"),
            ],
            open.Select(question => question.Recommendation));
        // The two the complete pause seeks are asked side by side, in no order of their own.
        Assert.Equal("pause", advisor.Asked[0].Kind);
        Assert.Equal(["foldConflicts", "runBudget"], advisor.Asked.Skip(1).Select(call => call.Kind).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Within a live session that is pausing, an answer to a question whose recommendation the pause defers - no advisor
    /// seeks it until the pause is complete - is refused, nothing answered, the refusal kept under its inbox entry and
    /// saying the run seeks the recommendation once its pause is complete; once the advice the pause left running has
    /// ended, the deferred question is advised, and the session ends with it recommended.
    /// </summary>
    [Fact]
    public async Task AnAnswerWhileAPauseDefersItsRecommendation_IsRefused_UntilThePauseRecommendsIt()
    {
        var (coordinator, records, advisor, fold, held) = DrainingBudgetPause(holding: true);

        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => records.Saw(FlowChangeKind.PauseCompleted));
        fold.SetResult();
        await TestEventuallyAsync(() => coordinator.OpenQuestions.Any(question => question.Kind == "foldConflicts"));
        var deferred = coordinator.OpenQuestions.Single(question => question.Kind == "foldConflicts");

        Assert.Equal(
            new FlowRecommendation("deferred", Why: "the run is pausing, and no reason step is started before its pause is complete: the recommendation is sought then, or when the run is resumed where a stop came first"),
            deferred.Recommendation);
        Assert.Equal(FlowHandedOver.Refused, await coordinator.OfferAnswerAsync(deferred.Id, "fail", inbox: "e1").WaitAsync(Bound, Token));
        Assert.Equal(
            ("e1", "q3", "fail", "question 'q3' of run 'r1' still owes the recommendation it is answered with, which the run seeks once its pause is complete, so nothing was answered; answer it then, and 'abort' is taken now"),
            (records.Refused.Single().Inbox, records.Refused.Single().Question, records.Refused.Single().Answer, records.Refused.Single().Why));
        Assert.DoesNotContain(advisor.Asked, call => call.Kind == "foldConflicts");

        held!.SetResult();
        var end = await session.WaitAsync(Bound, Token);
        var advised = coordinator.Questions.Single(question => question.Id == deferred.Id);

        Assert.Equal((FlowRunStatus.Paused, "budget"), (end.Status, end.Pause!.Cause));
        Assert.Equal(("open", "given", 0), (advised.State, advised.Recommendation.State, advised.Given.Count));
    }

    /// <summary>
    /// A resume that comes back to the budget's pause - its question still open - pays for each advice it seeks once: a
    /// failure the pause left owed is raised at the resume and its advisor started, the pause the budget's open question
    /// asks for again never ends it, and its recommendation is kept, so the next resume seeks nothing.
    /// </summary>
    [Fact]
    public async Task AResumeThatComesBackToTheBudgetsPause_PaysForEachAdviceOnce_AndKeepsIt()
    {
        var definition = Parse("""
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            budget:
              usd: 50
            steps:
              - name: gate
                type: build
                onFailure: pause
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var gateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateFails = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner()
            .OnAsync("gate", async _ =>
            {
                gateStarted.TrySetResult();
                await gateFails.Task;
                return FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "a leg failed");
            })
            .OnAsync("think", async run =>
            {
                await gateStarted.Task;
                run.Events.Used(new FlowTurnSpend(new DssHarness.Core.Agents.AgentUsage(1_000, 0, 0, 0, 0, 0), 45.10));
                return FlowStepResult.Ended(passed: true, HarnessExit.Success);
            });
        var advisor = new Advisor(call => Task.FromResult(new FlowAdvice(call.Answers[0].Answer, $"advised for {call.Kind}", null)));
        var records = new Records();
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: records, advisor: advisor);

        // The budget's pause drains the build, which fails while it does: its failure is owed to the resume.
        var session = coordinator.RunAsync(Token);
        await TestEventuallyAsync(() => records.Saw(FlowChangeKind.PauseCompleted));
        gateFails.SetResult();
        var first = await session.WaitAsync(Bound, Token);

        Assert.Equal((FlowRunStatus.Paused, "budget"), (first.Status, first.Pause!.Cause));
        Assert.Equal(["runBudget"], coordinator.OpenQuestions.Select(question => question.Kind));

        // The resume raises the failure and seeks its advice; the budget's open question pauses the run again, twice.
        var second = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
        var third = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
        var failure = coordinator.OpenQuestions.Single(question => question.Kind == "failure");

        Assert.Equal((FlowRunStatus.Paused, "budget", FlowRunStatus.Paused, "budget"), (second.Status, second.Pause!.Cause, third.Status, third.Pause!.Cause));
        Assert.Equal(["runBudget", "failure"], advisor.Asked.Select(call => call.Kind));
        Assert.Equal(new FlowRecommendation("given", "retry", null, "advised for failure", "advisor", "advisor"), failure.Recommendation);
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
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new ManualClock(), advisor: advisor);
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
        var coordinator = TestAdvice.Coordinator(Advised(), AtItsTurnsFirst(), new RunnerFacts(), new JumpingClock(), advisor: advisor);

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
        var coordinator = TestAdvice.Coordinator(
            definition,
            AtItsTurnsFirst(),
            new RunnerFacts(),
            new JumpingClock(),
            workflowFile: (owner, named) => named == "bar.md"
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
    /// A question is never put to a person unrecommended: a run whose workflow names no advisor, or that was started with no
    /// runner for it, fails at its first question as the defect it is - a run's workflow is refused without an advisor, and
    /// every session is given its runner - rather than asking it with no recommendation.
    /// </summary>
    [Fact]
    public async Task AQuestionWithNoAdvisorToRecommendIt_IsNeverAskedUnrecommended()
    {
        var runner = new FakeRunner().On("think", FlowStepResult.AtLimit("turns", "12 turns") with { StoredConversation = true });
        var bare = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("more", "never read", null)));

        var unnamed = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlowCoordinator(bare, runner, new RunnerFacts(), new JumpingClock(), advisor: advisor).RunAsync(Token).WaitAsync(Bound, Token));
        var unstarted = await Assert.ThrowsAsync<InvalidOperationException>(() => new FlowCoordinator(Advised(), runner, new RunnerFacts(), new JumpingClock()).RunAsync(Token).WaitAsync(Bound, Token));

        Assert.Equal(
            "question q1 (turnLimit) is to be recommended before a person is asked it, and this run is of workflow 'flow', which names no advisor: a run's workflow is refused without one, and every session is given its runner",
            unnamed.Message);
        Assert.Equal(
            "question q1 (turnLimit) is to be recommended before a person is asked it, and this run was started with no advisor to run: a run's workflow is refused without one, and every session is given its runner",
            unstarted.Message);
        Assert.Empty(advisor.Asked);
    }

    /// <summary>
    /// The budget's own question is recommended as every question is, before a person is asked it: once the budget's
    /// pause is complete the advisor is asked of the run as a whole - the run's own name, no agent's tree - on its own
    /// bounds, as every advice is; what it spends is the run's own measure's, counted in the run's spend and kept in its
    /// file's spent; and the session ends paused on the question, recommended.
    /// </summary>
    [Fact]
    public async Task TheBudgetsOwnQuestion_IsRecommended_OnceItsPauseIsComplete()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice("abort", "what is left is small", null, Model: "claude-opus-5-5", Directory: "steps/run/advice/1", Usd: 0.5, Tokens: 700)));
        var runner = new FakeRunner().On("think", run =>
        {
            run.Events.Used(new FlowTurnSpend(new DssHarness.Core.Agents.AgentUsage(1_000, 0, 0, 0, 0, 0), 45.10));
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var records = new Records();
        var coordinator = new FlowCoordinator(Advised(budget: "usd: 50"), runner, new RunnerFacts(), new JumpingClock(), records: records, runName: "r7", advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
        var question = coordinator.OpenQuestions.Single();
        var call = advisor.Asked.Single();

        Assert.Equal((FlowRunStatus.Paused, "budget"), (end.Status, end.Pause!.Cause));
        Assert.Equal(("runBudget", FlowQuestions.RunsOwn, (string?)null), (call.Kind, call.Instance, call.Agent));
        Assert.Equal("run 'r7' has spent 45.10 of 50.00 USD (its reserve of 10% was reached)", call.Problem);
        Assert.Equal(["more", "abort"], call.Answers.Select(option => option.Answer));
        Assert.Equal(
            new FlowRecommendation("given", "abort", null, "what is left is small", "advisor", "advisor", "claude-opus-5-5", "steps/run/advice/1"),
            question.Recommendation);
        Assert.Equal(
            (45.6, 1_700L, new FlowStepAdvice("claude-opus-5-5", 700, 0.5)),
            (Math.Round(records.Last!.Spent.Usd, 6), records.Last.Spent.Tokens, records.Last.Spent.Advice));
    }

    /// <summary>
    /// An advisor that obtains nothing for the budget's own question - it failed past its retries - leaves the question
    /// saying why, and the person is asked it all the same: the recommendation step ran.
    /// </summary>
    [Fact]
    public async Task TheBudgetsOwnQuestion_WhoseAdvisorObtainsNothing_IsAskedSayingWhy()
    {
        var advisor = new Advisor(_ => Task.FromResult(new FlowAdvice(null, null, null, NotObtained: "agent 'advisor' failed past its 3 retries")));
        var runner = new FakeRunner().On("think", run =>
        {
            run.Events.Used(new FlowTurnSpend(new DssHarness.Core.Agents.AgentUsage(1_000, 0, 0, 0, 0, 0), 45.10));
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var coordinator = new FlowCoordinator(Advised(budget: "usd: 50"), runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(
            ("runBudget", new FlowRecommendation("notObtained", Why: "no recommendation: one was sought and not obtained (agent 'advisor' failed past its 3 retries)")),
            (coordinator.OpenQuestions.Single().Kind, coordinator.OpenQuestions.Single().Recommendation));
    }

    /// <summary>An agent's own questions are never advised: the agent recommends within each of them.</summary>
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
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), advisor: advisor);

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
        var coordinator = TestAdvice.Coordinator(definition, new FakeRunner(), new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["preapproved"] = "yes" }, advisor: advisor);

        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Empty(advisor.Asked);
        Assert.Equal(FlowRecommendation.NotSought, coordinator.Questions.Single().Recommendation);
    }

    /// <summary>
    /// A run named 'r1' whose budget's reserve the advice of a pause step's question meets while a fold still runs: the
    /// budget's pause leaves the fold to finish, held until the test lets it end, and it then asks of its conflicting
    /// merge. Where <paramref name="holding"/>, a second pause step asks beside the first, its advice held until the test
    /// lets it answer, so the session lives on past the fold's question. The advisor recommends each other question's
    /// first answer at once.
    /// </summary>
    private static (FlowCoordinator Coordinator, Records Records, Advisor Advisor, TaskCompletionSource Fold, TaskCompletionSource? Held) DrainingBudgetPause(bool holding)
    {
        var definition = Parse($"""
            name: flow
            advisor:
              agent: advisor
              prompt: prompts/recommend.md
              tier: best
            budget:
              usd: 50
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: folding
                type: fold
                of: work
                dependsOn: [work]
              - name: approval
                type: pause
                question: 'Go on?'
                options: [go, stay]
                dependsOn: [work]{(holding ? "\n  - name: check\n    type: pause\n    question: 'Still?'\n    options: [yes, no]\n    dependsOn: [work]" : string.Empty)}
            """);
        var fold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = holding ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        var runner = new FakeRunner()
            .OnAsync("folding", async _ =>
            {
                await fold.Task;
                return FlowStepResult.Standing(new FlowDryRun(RequestStanding.Refused, [], [], [RefusalKind.Conflict], "1 path conflicts") { Conflicting = ["src/a.cs"] });
            });

        // The approval's advice spends the run to its reserve; the check's waits for the test.
        var advisor = new Advisor(async call =>
        {
            if (call.Instance == "check")
            {
                await held!.Task;
            }

            return new FlowAdvice(call.Answers[0].Answer, $"advised for {call.Kind}", null, Usd: call.Instance == "approval" ? 45.10 : 0);
        });
        var records = new Records();
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: records, runName: "r1", advisor: advisor);

        return (coordinator, records, advisor, fold, held);
    }

    /// <summary>What every launch of the advisor the fake client answers reports it used: output alone, so its cost at the kit's price is exact.</summary>
    private static readonly DssHarness.Core.Agents.AgentUsage AdviceTurn = new(0, 15_625, 0, 0, 0, 0);

    /// <summary>What one launch of the advisor costs at the kit's price for its model: 15,625 output tokens at 50 USD a million.</summary>
    private const double AdviceUsd = 0.78125;

    /// <summary>What the advisor spent on one question, as the measure it is charged on keeps it.</summary>
    private static FlowStepAdvice AdviceSpent => new("claude-opus-5-5", AdviceTurn.AllTokens, AdviceUsd);

    /// <summary>Why the fake client recommends what it does.</summary>
    private const string WithinItsBounds = "it was given its own bounds to weigh the question in";

    /// <summary>
    /// The advisor runner's kit, its advisor's definition bounded - seven turns, two dollars, three minutes
    /// (<see cref="AdvisorRunnerTests.BoundedAdvisorLimits"/>) - and its fake client answering each launch as a client
    /// does: within the launch's bound in dollars it reports its turn (<see cref="AdviceTurn"/>) and recommends the
    /// question's first answer that takes no text, as the schema it was given spells them; under a bound that turn would
    /// pass, the launch's budget is spent and nothing is recommended.
    /// </summary>
    private static async Task<(OrchestrationKit Kit, AdvisorRunnerTests.Agents Agents)> ReadyAdvisorAsync(TempDirectory temp)
    {
        var (kit, agents) = await AdvisorRunnerTests.ReadyAsync(temp);
        AdvisorRunnerTests.WriteBoundedAdvisor(kit);
        agents.Says.Add(new DssHarness.Core.Agents.UsageReported(AdviceTurn, AdviceTurn));
        agents.Answer = request => request.Limits.MaxBudgetUsd is { } bound && bound < (decimal)AdviceUsd
            ? new DssHarness.Core.Agents.AgentRunResult(DssHarness.Core.Agents.AgentRunStatus.BudgetSpent, Model: request.Model, Error: $"its budget of {bound.ToString(System.Globalization.CultureInfo.InvariantCulture)} USD was spent")
            : new DssHarness.Core.Agents.AgentRunResult(DssHarness.Core.Agents.AgentRunStatus.Completed, StructuredOutput: new JsonObject { ["answer"] = FirstPlainAnswer(request), ["why"] = WithinItsBounds }.ToJsonString(), Model: request.Model);

        return (kit, agents);

        // The first answer of the shape that takes no text: a question whose answers are of both kinds offers its shapes
        // as anyOf.
        static string FirstPlainAnswer(DssHarness.Core.Agents.AgentStepRequest request)
        {
            var schema = JsonNode.Parse(request.Output!.Schema)!;
            var shapes = (schema["anyOf"]?.AsArray() ?? new JsonArray(schema)).OfType<JsonObject>();
            var plain = shapes.First(shape => shape["required"]!.AsArray().All(member => member!.GetValue<string>() != "text"));

            return plain["properties"]!["answer"]!["enum"]!.AsArray()[0]!.GetValue<string>();
        }
    }

    /// <summary>
    /// A run of <paramref name="definition"/> in the advisor runner's kit, its questions recommended by the advisor runner
    /// itself over the kit's fake client - the run held in memory, its advisor working in the kit's repository under the
    /// kit's orchestrator, as a run's does in its own.
    /// </summary>
    private static FlowCoordinator AdvisedByTheRunner(OrchestrationKit kit, AdvisorRunnerTests.Agents agents, WorkflowDefinition definition, IFlowStepRunner runner, IFlowRecords? records = null)
        => TestAdvice.Coordinator(
            definition,
            runner,
            new RunnerFacts(),
            new JumpingClock(),
            inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["request"] = "build the login" },
            records: records,
            runName: kit.Layout.Name,
            place: new FlowRunPlace(kit.Main, kit.Layout.Name, StartCommit: null, definition),
            advisor: AdvisorRunnerTests.Runner(kit, agents));

    /// <summary>The recommendation the fake client gives through the advisor runner, of <paramref name="answer"/>, with the model and the records of the launch <paramref name="asked"/>.</summary>
    private static FlowRecommendation Recommended(string answer, DssHarness.Core.Agents.AgentStepRequest asked)
        => new("given", answer, null, WithinItsBounds, "advisor", "advisor", asked.Model, asked.RecordDirectory);

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
