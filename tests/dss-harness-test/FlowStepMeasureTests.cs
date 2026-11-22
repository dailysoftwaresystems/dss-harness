using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// What one step instance's work measured, written by the engine as it runs: the attempt's tokens by kind, dollars and
/// the model that served it, the totals the sum of every attempt's, the record holding it round-tripping through
/// <c>step.json</c> - and, with the engine itself, the waits timed by their causes (a run limit, a person's answer),
/// the run's own spend the sum of its instances', and a resume completing a cut-off attempt's measure from its stored
/// stream.
/// </summary>
public sealed class FlowStepMeasureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every bound a test of a session waits for: a session that hangs fails the test rather than the run's minutes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>The fixed time every measure of these tests begins at.</summary>
    private static readonly DateTimeOffset Began = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The measure's types hold their shape: an attempt with its tokens by kind, its cost, the model that served it
    /// and how it ended; the totals the sum of the attempts; the record carrying it through <c>step.json</c>.
    /// </summary>
    [Fact]
    public void TheMeasure_HoldsTheAttemptSpend_WithTheModelAndTotals()
    {
        var measure = new FlowStepMeasure(
            Attempts:
            [
                new FlowStepAttempt(
                    N: 1,
                    StartedAt: DateTimeOffset.UnixEpoch,
                    FinishedAt: DateTimeOffset.UnixEpoch.AddSeconds(30),
                    Outcome: "Done",
                    Exit: 0,
                    Model: "claude-opus-4-5",
                    Provider: "anthropic",
                    Tokens: new AgentUsage(InputTokens: 1000, OutputTokens: 500, CacheReadTokens: 200, CacheWriteTokens: 100, CacheWrite1hTokens: 0, Searches: 2),
                    Usd: 0.015),
                new FlowStepAttempt(
                    N: 2,
                    Outcome: "Failed",
                    Exit: 20,
                    Model: "claude-opus-4-5",
                    Tokens: new AgentUsage(InputTokens: 500, OutputTokens: 200, CacheReadTokens: 100, CacheWriteTokens: 50, CacheWrite1hTokens: 0, Searches: 0),
                    Usd: 0.005,
                    Uncounted: null),
            ],
            Totals: new FlowStepTotals(
                Tokens: new AgentUsage(1500, 700, 300, 150, 0, 2),
                Usd: 0.02));

        Assert.Null(measure.Problem());

        var totals = FlowStepTotals.Of(measure.Attempts!);
        Assert.Equal(1500, totals.Tokens!.InputTokens);
        Assert.Equal(700, totals.Tokens!.OutputTokens);
        Assert.Equal(0.02, totals.Usd);
        Assert.Null(totals.Uncounted);
    }

    /// <summary>The record carries the measure through <c>step.json</c>: what the instance held is what the record holds.</summary>
    [Fact]
    public void TheRecordCarriesTheMeasure()
    {
        var record = new FlowStepRecord(
            "think",
            "think",
            false,
            "Done",
            1,
            1,
            [],
            [],
            Type: "reason",
            Measure: new FlowStepMeasure(
                Attempts: [new FlowStepAttempt(N: 1, Model: "claude-opus-4-5", Tokens: new AgentUsage(1000, 500, 200, 100, 0, 2), Usd: 0.015)],
                Totals: new FlowStepTotals(Tokens: new AgentUsage(1000, 500, 200, 100, 0, 2), Usd: 0.015)));

        Assert.Null(record.Problem());
        Assert.NotNull(record.Measure);
        var attempt = Assert.Single(record.Measure.Attempts!);
        Assert.Equal("claude-opus-4-5", attempt.Model);
        Assert.Equal(1000, attempt.Tokens!.InputTokens);
        Assert.Equal(0.015, record.Measure.Totals!.Usd);
    }

    /// <summary>The price of an attempt is the model's entry as the configuration gives it, copied - not referenced.</summary>
    [Fact]
    public void ThePrice_IsTheModelsEntry_CopiedFromTheConfiguration()
    {
        var provider = new DssHarness.Core.Configuration.AgentProviderConfig { Name = "anthropic", Currency = "USD", ReadOn = "2026-11-01" };
        var model = new DssHarness.Core.Configuration.AgentModelConfig { Code = "claude-opus-4-5", PriceInputMiss = 3.0, PriceInputHit = 0.3, PriceOutput = 15.0, PriceCacheWrite = 3.75, PriceCacheWrite1h = 5.0 };

        var price = FlowStepPrice.Of(provider, model);

        Assert.NotNull(price);
        Assert.Equal(("anthropic", "claude-opus-4-5", "USD", "2026-11-01"), (price.Provider, price.Model, price.Currency, price.ReadOn));
        Assert.Equal((3.0, 0.3, 15.0, 3.75, 5.0), (price.InputMiss, price.InputHit, price.Output, price.CacheWrite, price.CacheWrite1h));
    }

    /// <summary>A wait names its cause and its seconds, and one that does not is refused.</summary>
    [Fact]
    public void AWait_NamesItsCauseAndSeconds()
    {
        Assert.Null(new FlowStepWait("a person", 12.5).Problem());
        Assert.NotNull(new FlowStepWait("", 12.5).Problem());
        Assert.NotNull(new FlowStepWait("a person", -1).Problem());
    }

    /// <summary>
    /// An open wait is closed where its causes change, into the cause it waited on - and the run's own pause is kept
    /// apart, in <c>paused</c> and never in <c>waited</c>. A wait that spans sessions stays whole, for the open period
    /// is kept with its time.
    /// </summary>
    [Fact]
    public void AnOpenWait_ClosesWhereItsCausesChange_IntoItsCause()
    {
        var measure = new FlowStepMeasure()
            .Waiting([FlowWaits.ReasoningAgents], Began)
            .Waiting([FlowWaits.ReasoningAgents], Began.AddSeconds(10))
            .Waiting([FlowWaits.Claim], Began.AddSeconds(30))
            .Waiting([], Began.AddSeconds(45))
            .Waiting([FlowWaits.Resume], Began.AddMinutes(2))
            .Waiting([], Began.AddMinutes(5));

        // Thirty seconds on the reasoning limit, fifteen on a claim - the change of cause split the wait - and the
        // run's own pause, three minutes of it, kept apart from the waits.
        var waited = measure.Waited!;
        Assert.Equal(2, waited.Count);
        Assert.Equal((FlowWaits.ReasoningAgents, 30.0), (waited[0].Cause, waited[0].Seconds));
        Assert.Equal((FlowWaits.Claim, 15.0), (waited[1].Cause, waited[1].Seconds));
        Assert.Equal(180.0, measure.Paused);
        Assert.Null(measure.WaitingOn);
        Assert.Null(measure.WaitingSince);
        Assert.Null(measure.Problem());
    }

    /// <summary>What the advisor spent answering a step's questions is kept beside its attempts', summed over askings.</summary>
    [Fact]
    public void WhatTheAdvisorSpent_IsSummedBesideTheAttempts()
    {
        var measure = new FlowStepMeasure()
            .Advised(new FlowAdvice("answer", "why", null, Model: "model-a", Usd: 0.01, Tokens: 900, Uncounted: null))
            .Advised(new FlowAdvice("answer", "why", null, Model: "model-b", Usd: 0.02, Tokens: 100));

        Assert.Equal(("model-a", 1000, 0.03), (measure.Advice!.Model, measure.Advice.Tokens, measure.Advice.Usd));
        Assert.Null(measure.Advice.Uncounted);
    }

    /// <summary>A turn that reaches its attempt's entry after it was written is folded into it, with its model where the entry held none.</summary>
    [Fact]
    public void ALateTurn_IsFoldedIntoItsAttemptEntry()
    {
        var attempt = new FlowStepAttempt(N: 1, Model: null, Tokens: new AgentUsage(100, 40, 0, 0, 0, 0), Usd: 0.001)
            .Charged(new AgentUsage(900, 60, 10, 5, 0, 1), 0.009, null, "model-x");

        Assert.Equal(new AgentUsage(1000, 100, 10, 5, 0, 1), attempt.Tokens);
        Assert.Equal(0.01, attempt.Usd!.Value, 5);
        Assert.Equal("model-x", attempt.Model);
    }

    /// <summary>
    /// A step that waits for room is timed by the cause it waited on: with one reasoning agent allowed, the second of
    /// two reason steps waits on the limit for as long as the first runs, is ready from the moment the run drew it, and
    /// the first's own run time is its attempt's.
    /// </summary>
    [Fact]
    public async Task AStepThatWaitsForRoom_IsTimedByItsCause()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 1
            steps:
              - name: first
                type: reason
                prompt: one.md
                outputs: one.json
              - name: second
                type: reason
                prompt: two.md
                outputs: two.json
            """);
        var runner = new Runner();
        runner.Hold("first");
        runner.On("second", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var clock = new ManualClock(Began);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var running = coordinator.RunAsync(Token);
            await runner.StartedAsync("first").WaitAsync(Bound, Token);

            clock.Advance(TimeSpan.FromSeconds(30));
            runner.Release("first");
            var end = await running.WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        var first = flow.Steps.Single(step => step.Step == "first").Measure!;
        var second = flow.Steps.Single(step => step.Step == "second").Measure!;

        // The first ran for the thirty seconds the second waited on the limit.
        Assert.Equal(30.0, first.Ran);
        Assert.Equal([new FlowStepWait(FlowWaits.ReasoningAgents, 30.0)], second.Waited);
        Assert.Equal(Began, second.ReadyAt);
        Assert.Null(second.WaitingOn);
    }

    /// <summary>
    /// A step that asks a person is timed on its question, whole across the sessions its answer spans: the wait is one,
    /// from asked to answered, for the open period is kept in the record with its time. The answer lets the attempt go
    /// on where it stood - the same attempt, its entry ending with how it ended after the answer.
    /// </summary>
    [Fact]
    public async Task AStepThatAsksAPerson_IsTimedOnItsQuestion_AcrossSessions()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: asker
                type: reason
                prompt: ask.md
                outputs: ask.json
            """);
        var runner = new Runner().On("asker", attempt => attempt == 1
            ? FlowStepResult.Asking(new FlowQuestion(FlowQuestions.RunsOwn, "ask", "asker", "what now?", FlowQuestions.OptionsOf("ask", step: null)))
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var clock = new ManualClock(Began);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);

            var paused = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);

            var asked = coordinator.OpenQuestions.Single().Id;

            // The person answers a day later, with a text: the wait is the day, whole, and the step runs again.
            clock.Advance(TimeSpan.FromHours(26));
            Assert.True(coordinator.TryAnswer(asked, "answer", "do the other thing", out var refused), refused);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
        }

        var asker = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single().Measure!;

        Assert.Equal([new FlowStepWait("q1", 93600.0)], asker.Waited);

        var attempt = asker.Attempts!.Single();

        Assert.Equal(1, attempt.N);
        Assert.Equal("Done", attempt.Outcome);
    }

    /// <summary>
    /// What a reason step's turns used is charged to its instance as it is charged to the run - the one place a token
    /// is counted, two outputs - and the attempt's entry holds its own account: what served it, the effort, where a
    /// fallback moved it from, the price copied as its cost was counted, the turns, the tools and the compactions. The
    /// run's spend is the sum of its instances'.
    /// </summary>
    [Fact]
    public async Task AReasonStepsTurns_AreChargedToItsInstance_WithItsOwnAccount()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new Runner().OnAsync("think", run =>
        {
            run.Events.Used(1800, 0.015, uncounted: null, turn: new AgentUsage(1000, 500, 200, 100, 0, 2), serving: "model-x");
            return Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success) with
            {
                Attempt = new FlowStepAttempt(
                    N: 0,
                    Provider: "prov",
                    Model: "model-x",
                    Effort: "high",
                    FallbackFrom: "model-w",
                    FallbackReason: "key refused",
                    Price: new FlowStepPrice("prov", "model-x", "USD", ReadOn: "2026-11-01", InputMiss: 3.0, InputHit: 0.3, Output: 15.0, CacheWrite: 3.75, CacheWrite1h: 5.0),
                    Turns: 3,
                    Tools: [new FlowStepToolUse("read", 4, 1)],
                    Compactions: 1),
            });
        });
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        var measure = flow.Steps.Single().Measure!;
        var attempt = measure.Attempts!.Single();

        // The attempt's own spend and account.
        Assert.Equal(1, attempt.N);
        Assert.Equal("Done", attempt.Outcome);
        Assert.Equal(new AgentUsage(1000, 500, 200, 100, 0, 2), attempt.Tokens);
        Assert.Equal(0.015, attempt.Usd);
        Assert.Equal(("prov", "model-x", "high"), (attempt.Provider, attempt.Model, attempt.Effort));
        Assert.Equal(("model-w", "key refused"), (attempt.FallbackFrom, attempt.FallbackReason));
        Assert.Equal("2026-11-01", attempt.Price!.ReadOn);
        Assert.Equal((3.0, 0.3, 15.0), (attempt.Price.InputMiss, attempt.Price.InputHit, attempt.Price.Output));
        Assert.NotNull(attempt.Tools);
        Assert.Equal((3, 4, 1), (attempt.Turns, Assert.Single(attempt.Tools).Calls, Assert.Single(attempt.Tools).Failed));
        Assert.Equal(1, attempt.Compactions);

        // The run's spend is the sum of its instances'.
        Assert.Equal(1800, flow.Spent.Tokens);
        Assert.Equal(0.015, flow.Spent.Usd);
        Assert.Equal(1800, flow.Steps.Sum(step => (step.Measure?.Totals?.Tokens?.AllTokens ?? 0) + (step.Measure?.Advice?.Tokens ?? 0)));
        Assert.Equal(0.015, flow.Steps.Sum(step => (step.Measure?.Totals?.Usd ?? 0) + (step.Measure?.Advice?.Usd ?? 0)));
    }

    /// <summary>
    /// A resume completes a cut-off attempt's measure from its stored stream: a crash that left a reason step running
    /// gives its next session the attempt's entry - its tokens, its cost, its model, how it was cut off - and the run's
    /// budget is charged with the same numbers, so the run's spend stays the sum of its instances'.
    /// </summary>
    [Fact]
    public async Task AResume_CompletesTheMeasureFromTheStream()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new Runner();
        runner.Hold("think");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            _ = coordinator.RunAsync(Token);
            await runner.StartedAsync("think").WaitAsync(Bound, Token);

            // The crash, while the step's conversation was in flight.
        }

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var account = new CutAccount();
            var coordinator = new FlowCoordinator(
                definition,
                new Runner().On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success)),
                new RunnerFacts(),
                resume: TakeUp(harness, layout, definition),
                records: again,
                streamAccount: account);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("think", account.Asked.Single().Instance);

            var think = coordinator.Instances["think"].Measure!;
            Assert.Equal(2, think.Attempts!.Count);

            var cut = think.Attempts[0];
            Assert.Equal(1, cut.N);
            Assert.Equal("Stopped", cut.Outcome);
            Assert.Equal("the engine's process ended", cut.Error);
            Assert.Equal("model-x", cut.Model);
            Assert.Equal(new AgentUsage(700, 300, 0, 0, 0, 0), cut.Tokens);
            Assert.Equal(0.012, cut.Usd);
        }

        // The run's budget was charged with what the cut-off attempt spent, and its spend is the sum of its instances'.
        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        Assert.Equal(1000, flow.Spent.Tokens);
        Assert.Equal(0.012, flow.Spent.Usd);
        Assert.Equal(flow.Spent.Tokens, flow.Steps.Sum(step => (step.Measure?.Totals?.Tokens?.AllTokens ?? 0) + (step.Measure?.Advice?.Tokens ?? 0)));
    }

    /// <summary>
    /// A failed attempt that the engine runs again carries its backoff on its own entry: the wait is the failed
    /// attempt's ending's, and the next attempt's entry follows it.
    /// </summary>
    [Fact]
    public async Task AFailedAttemptTheEngineRunsAgain_CarriesItsBackoff()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: machine
                type: orient
            """);
        var runner = new Runner().On("machine", attempt => attempt == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, "it did not finish")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
        }

        var machine = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single().Measure!;

        Assert.Equal(2, machine.Attempts!.Count);
        Assert.Equal("Failed", machine.Attempts[0].Outcome);
        Assert.Equal(0.0, machine.Attempts[0].RetryAfter);
        Assert.Null(machine.Attempts[1].RetryAfter);
        Assert.Equal("Done", machine.Attempts[1].Outcome);
    }

    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The run's records in a directory of their own.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FlowRunRecords Records) Records(string run, string directory, WorkflowDefinition definition)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        return (harness, layout, new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of(run, definition)));
    }

    /// <summary>What a run left, taken up: the records read, reconciled, and handed back.</summary>
    private static FlowResumeState TakeUp(HarnessFactory harness, OrchestratorLayout layout, WorkflowDefinition definition)
        => new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>The account of a cut-off step's stored stream, as a test writes it: one attempt, spent and served as the test says.</summary>
    private sealed class CutAccount : IFlowStreamAccount
    {
        /// <summary>Every run the account was asked of, in order.</summary>
        public List<FlowStepRun> Asked { get; } = [];

        /// <inheritdoc/>
        public string? Account(FlowStepRun run) => null;

        /// <inheritdoc/>
        public Task<FlowStepAttempt?> AttemptOf(FlowStepRun run, CancellationToken cancellationToken)
        {
            Asked.Add(run);
            return Task.FromResult<FlowStepAttempt?>(new FlowStepAttempt(1, Model: "model-x", Tokens: new AgentUsage(700, 300, 0, 0, 0, 0), Usd: 0.012));
        }
    }

    /// <summary>A runner that answers each step as its test scripted it, holding a step open until the test completes it.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, int, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _held = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _asked = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _begun = new(StringComparer.Ordinal);

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public Runner On(string step, FlowStepResult result)
        {
            _scripts[step] = (_, _) => Task.FromResult(result);
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by how often it has run, from 1.</summary>
        public Runner On(string step, Func<int, FlowStepResult> script)
        {
            _scripts[step] = (_, asked) => Task.FromResult(script(asked));
            return this;
        }

        /// <summary>Answers <paramref name="step"/> from its run.</summary>
        public Runner OnAsync(string step, Func<FlowStepRun, Task<FlowStepResult>> script)
        {
            _scripts[step] = (run, _) => script(run);
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it.</summary>
        public void Hold(string step)
            => _held[step] = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes a held <paramref name="step"/> as passed.</summary>
        public bool Release(string step)
            => _held[step].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));

        /// <summary>Completes once <paramref name="step"/> was asked once, at once where it already was; a session that never asks it hangs the test.</summary>
        public Task StartedAsync(string step)
        {
            lock (_begun)
            {
                if (_asked.GetValueOrDefault(step) > 0)
                {
                    return Task.CompletedTask;
                }

                return _begun.TryGetValue(step, out var begun) ? begun.Task
                    : (_begun[step] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (_begun)
            {
                _asked[run.Step.Name] = _asked.GetValueOrDefault(run.Step.Name) + 1;

                if (_begun.TryGetValue(run.Step.Name, out var begun))
                {
                    _ = begun.TrySetResult();
                }
            }

            var answer = _held.TryGetValue(run.Step.Name, out var held)
                ? held.Task
                : _scripts.GetValueOrDefault(run.Step.Name, (_, _) => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run, _asked[run.Step.Name]);

            return await answer.WaitAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }
}
