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
/// each model that served it, the totals the sum of every attempt's, the record holding it round-tripping through
/// <c>step.json</c> - and, with the engine itself, the waits timed by their causes (a run limit, a person's answer, the
/// machine holding a step's legs), the run's own spend the sum of its instances', an attempt that asks keeping both of
/// its runs, and a resume completing a cut-off attempt's measure from its stored stream, charging only what its record
/// had not.
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

    /// <summary>
    /// Each period of a wait is kept as exact as the clock gave it, never rounded: three waits of 40 milliseconds on one
    /// cause sum to 0.12 seconds, where rounding each to a tenth before summing lost them all.
    /// </summary>
    [Fact]
    public void AWait_IsKeptAsTheClockGaveIt()
    {
        var measure = new FlowStepMeasure();

        for (var period = 0; period < 3; period++)
        {
            var at = Began.AddSeconds(period);
            measure = measure.Waiting([FlowWaits.Claim], at).Waiting([], at.AddMilliseconds(40));
        }

        var wait = Assert.Single(measure.Waited!);
        Assert.Equal(FlowWaits.Claim, wait.Cause);
        Assert.Equal(0.12, wait.Seconds, 9);
    }

    /// <summary>What the advisor spent answering a step's questions is kept beside its attempts', summed over askings - and an asking that spent nothing leaves the measure as it was.</summary>
    [Fact]
    public void WhatTheAdvisorSpent_IsSummedBesideTheAttempts()
    {
        var measure = new FlowStepMeasure()
            .Advised(new FlowAdvice("answer", "why", null, Model: "model-a", Usd: 0.01, Tokens: 900, Uncounted: null))
            .Advised(new FlowAdvice("answer", "why", null, Model: "model-b", Usd: 0.02, Tokens: 100));

        Assert.Equal(("model-a", 1000, 0.03), (measure.Advice!.Model, measure.Advice.Tokens, measure.Advice.Usd));
        Assert.Null(measure.Advice.Uncounted);
        Assert.Same(measure, measure.Advised(new FlowAdvice("answer", "why", null, Model: "model-c")));
    }

    /// <summary>
    /// An attempt that falls back from one model to another keeps each model's spend as its own part, at its own price:
    /// the attempt's total is both parts', and the part a turn served is the model that served it, never the last one.
    /// </summary>
    [Fact]
    public void AnAttemptThatFallsBack_KeepsEachModelsSpendAtItsOwnPrice()
    {
        var first = new FlowStepPrice("prov", "model-a", "USD", InputMiss: 3.0, Output: 15.0);
        var second = new FlowStepPrice("other", "model-b", "USD", InputMiss: 1.0, Output: 5.0);

        var attempt = new FlowStepAttempt(N: 1)
            .Charged(new FlowTurnSpend(new AgentUsage(1000, 100, 0, 0, 0, 0), 0.25, Serving: "model-a", Price: first))
            .Charged(new FlowTurnSpend(new AgentUsage(3000, 200, 0, 0, 0, 0), 0.25, Serving: "model-a", Price: first))
            .Charged(new FlowTurnSpend(new AgentUsage(500, 50, 0, 0, 0, 0), 0.125, Serving: "model-b", Price: second));

        Assert.Equal(new AgentUsage(4500, 350, 0, 0, 0, 0), attempt.Tokens);
        Assert.Equal(0.625, attempt.Usd);
        Assert.Equal(3, attempt.Reports);
        Assert.Equal(("other", "model-b"), (attempt.Provider, attempt.Model));
        Assert.Equal(
            [
                new FlowStepPart("model-a", "prov", new AgentUsage(4000, 300, 0, 0, 0, 0), 0.5, null, first),
                new FlowStepPart("model-b", "other", new AgentUsage(500, 50, 0, 0, 0, 0), 0.125, null, second),
            ],
            attempt.Parts);
        Assert.Null(new FlowStepMeasure(Attempts: [attempt], Totals: FlowStepTotals.Of([attempt])).Problem());
    }

    /// <summary>A step's record whose measure does not hold is refused, naming what is wrong with the measure.</summary>
    [Fact]
    public void ARecordWhoseMeasureDoesNotHold_IsRefused()
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
            Measure: new FlowStepMeasure(Attempts: [new FlowStepAttempt(N: 1), new FlowStepAttempt(N: 1)], Totals: new FlowStepTotals()));

        Assert.Equal("its measure: it holds one attempt's entry twice", record.Problem());
    }

    /// <summary>
    /// A step that waits for room is timed by the cause it waited on: with one reasoning agent allowed, the second of
    /// two reason steps waits on the limit for as long as the first runs, is ready from the moment the run drew it, and
    /// the first's own run time is its attempt's. The clock moves only once the record says the second waits.
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
        var clock = new ManualClock(Began);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var seen = new Seen(records);
            var waiting = seen.SeenAsync(snapshot => Step(snapshot, "second").WaitsOn.SequenceEqual([FlowWaits.ReasoningAgents]));
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: seen);
            var running = coordinator.RunAsync(Token);
            await runner.StartedAsync("first").WaitAsync(Bound, Token);
            await waiting.WaitAsync(Bound, Token);

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
    /// Only what the run draws to run holds a measure, from the moment it was drawn: a step drawn once the step it depends
    /// on ended is measured from then - that step's run is never its wait - and a group, which runs nothing of its own,
    /// holds none: not its own record, nor any item's scope of it.
    /// </summary>
    [Fact]
    public async Task OnlyWhatTheRunDraws_HoldsAMeasure_FromWhenItWasDrawn()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: work
                    type: orient
            """);
        var runner = new Runner();
        runner.Hold("plan");
        var clock = new ManualClock(Began);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
            var running = coordinator.RunAsync(Token);
            await runner.StartedAsync("plan").WaitAsync(Bound, Token);

            clock.Advance(TimeSpan.FromMinutes(10));
            _ = runner.Release("plan", JsonNode.Parse("""{"list": ["a", "b"]}"""));

            Assert.Equal(FlowRunStatus.Done, (await running.WaitAsync(Bound, Token)).Status);
        }

        var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;

        Assert.Equal(
            [("items", false), ("items[0]", false), ("items[0].work", true), ("items[1]", false), ("items[1].work", true), ("plan", true)],
            steps.Select(step => (step.Instance, step.Measure is not null)).OrderBy(step => step.Instance, StringComparer.Ordinal));
        Assert.All(
            steps.Where(step => step.Step == "items.work"),
            step => Assert.Equal((Began.AddMinutes(10), (IReadOnlyList<FlowStepWait>?)null), (step.Measure!.ReadyAt, step.Measure.Waited)));
    }

    /// <summary>
    /// A step that asks a person is timed on its question, whole across the sessions its answer spans: the wait is one,
    /// from asked to answered. The answer lets the attempt go on where it stood - the same attempt, one entry holding both
    /// of its runs: what each spent, both run times, and how it ended after the answer.
    /// </summary>
    [Fact]
    public async Task AnAttemptThatAsksAndGoesOn_KeepsBothRunsInOneEntry()
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
        var clock = new ManualClock(Began);
        var runner = new Runner().OnAsync("asker", (run, asked) =>
        {
            run.Events.Used(new FlowTurnSpend(new AgentUsage(1000, 200, 0, 0, 0, 0), asked == 1 ? 0.25 : 0.125, Serving: "model-x"));
            clock.Advance(TimeSpan.FromSeconds(asked == 1 ? 10 : 20));

            return Task.FromResult(asked == 1
                ? FlowStepResult.Asking(new FlowQuestion(FlowQuestions.RunsOwn, "ask", "asker", "what now?", FlowQuestions.OptionsOf("ask", step: null)))
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        });
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);

            var paused = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.Equal(FlowRunStatus.Paused, paused.Status);

            var asked = coordinator.OpenQuestions.Single().Id;

            // The person answers a day later, with a text: the wait is the day, whole, and the attempt goes on.
            clock.Advance(TimeSpan.FromHours(26));
            Assert.True(coordinator.TryAnswer(asked, "answer", "do the other thing", out var refused), refused);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        var asker = flow.Steps.Single().Measure!;

        Assert.Equal([new FlowStepWait("q1", 93600.0)], asker.Waited);
        Assert.Equal(30.0, asker.Ran);

        var attempt = asker.Attempts!.Single();

        Assert.Equal((1, "Done", 2), (attempt.N, attempt.Outcome, attempt.Reports));
        Assert.Equal(new AgentUsage(2000, 400, 0, 0, 0, 0), attempt.Tokens);
        Assert.Equal(0.375, attempt.Usd);
        Assert.Equal(
            [new FlowStepSpan(Began, Began.AddSeconds(10)), new FlowStepSpan(Began.AddSeconds(93610), Began.AddSeconds(93630))],
            attempt.Runs);

        // The run's spend is the attempt's, both runs of it.
        Assert.Equal((2400, 0.375), (flow.Spent.Tokens, flow.Spent.Usd));
    }

    /// <summary>
    /// What a reason step's turns used is charged to its instance as it is charged to the run - the one place a token
    /// is counted - and the attempt's entry holds its own account: what served it, the effort, where a fallback moved it
    /// from, each part of its spend at the price copied as its cost was counted, the turns, the tools and the
    /// compactions. The run's spend is the sum of its instances'.
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
        var price = new FlowStepPrice("prov", "model-x", "USD", ReadOn: "2026-11-01", InputMiss: 3.0, InputHit: 0.3, Output: 15.0, CacheWrite: 3.75, CacheWrite1h: 5.0);
        var runner = new Runner().OnAsync("think", (run, _) =>
        {
            run.Events.Used(new FlowTurnSpend(new AgentUsage(1000, 500, 200, 100, 0, 2), 0.015, Serving: "model-x", Price: price));
            return Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success) with
            {
                Attempt = new FlowStepAttempt(
                    N: 0,
                    Provider: "prov",
                    Model: "model-x",
                    Effort: "high",
                    FallbackFrom: "model-w",
                    FallbackReason: "key refused",
                    Turns: 3,
                    Tools: [new FlowStepToolUse("read", 4, 1, Denied: 1)],
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
        Assert.Equal((1, "Done", 1), (attempt.N, attempt.Outcome, attempt.Reports));
        Assert.Equal(new AgentUsage(1000, 500, 200, 100, 0, 2), attempt.Tokens);
        Assert.Equal(0.015, attempt.Usd);
        Assert.Equal(("prov", "model-x", "high"), (attempt.Provider, attempt.Model, attempt.Effort));
        Assert.Equal(("model-w", "key refused"), (attempt.FallbackFrom, attempt.FallbackReason));
        Assert.Equal([new FlowStepPart("model-x", "prov", new AgentUsage(1000, 500, 200, 100, 0, 2), 0.015, null, price)], attempt.Parts);
        Assert.Equal([new FlowStepToolUse("read", 4, 1, Denied: 1)], attempt.Tools);
        Assert.Equal((3, 1), (attempt.Turns, attempt.Compactions));

        // The run's spend is the sum of its instances'.
        Assert.Equal(1800, flow.Spent.Tokens);
        Assert.Equal(0.015, flow.Spent.Usd);
        Assert.Equal(1800, flow.Steps.Sum(step => (step.Measure?.Totals?.Tokens?.AllTokens ?? 0) + (step.Measure?.Advice?.Tokens ?? 0)));
        Assert.Equal(0.015, flow.Steps.Sum(step => (step.Measure?.Totals?.Usd ?? 0) + (step.Measure?.Advice?.Usd ?? 0)));
    }

    /// <summary>
    /// A resume completes a cut-off attempt's measure from its stored stream, charging only what its record had not: the
    /// turn the run had charged and written before the crash stays charged once, the turn past it is charged now, so
    /// the run's spend stays the sum of its instances'. The attempt's run ends where the stream ended, and the time from
    /// there to the resume is its pause - as it is for the step that stood waiting on a limit when the engine ended,
    /// whose wait on that limit ends where the run's last record did.
    /// </summary>
    [Fact]
    public async Task AResume_ChargesOnlyWhatTheRecordHadNot_AndTheCrashIsAPause()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 1
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: other
                type: reason
                prompt: other.md
                outputs: other.json
                dependsOn: [tick]
              - name: tick
                type: orient
            """);
        var charged = new FlowTurnSpend(new AgentUsage(600, 100, 0, 0, 0, 0), 0.25, Serving: "model-x");
        var past = new FlowTurnSpend(new AgentUsage(250, 50, 0, 0, 0, 0), 0.125, Serving: "model-x");
        var used = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner().OnAsync("think", (run, _) =>
        {
            run.Events.Used(charged);
            used.TrySetResult();
            return new TaskCompletionSource<FlowStepResult>().Task;
        });
        runner.Hold("tick");
        var (harness, layout, records) = Records("r1", temp.Path, definition, new ManualClock(Began));

        using (records)
        {
            var seen = new Seen(records);
            var written = seen.SeenAsync(snapshot => Step(snapshot, "tick").State == FlowInstanceState.Done
                && Step(snapshot, "other").WaitsOn.SequenceEqual([FlowWaits.ReasoningAgents])
                && Step(snapshot, "think").Measure?.Attempts?.Single().Reports == 1);
            using var engine = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var session = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(Began), records: seen).RunAsync(engine.Token);
            await used.Task.WaitAsync(Bound, Token);
            runner.Release("tick");
            await written.WaitAsync(Bound, Token);

            // The crash: the engine's process ends, its records as its last whole writes left them.
            await engine.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WaitAsync(Bound, Token));
        }

        var resumedAt = Began.AddHours(1);
        var (_, _, again) = Records("r1", temp.Path, definition, new ManualClock(resumedAt));

        using (again)
        {
            var account = new CutAccount(new FlowCutAttempt(new FlowStepAttempt(0, Model: "model-x", Turns: 2), [charged, past], Began.AddSeconds(5)));
            var coordinator = new FlowCoordinator(
                definition,
                new Runner(),
                new RunnerFacts(),
                new ManualClock(resumedAt),
                resume: TakeUp(harness, layout, definition),
                records: again,
                streamAccount: account);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("think", account.Asked.Single().Instance);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        var think = flow.Steps.Single(step => step.Step == "think").Measure!;
        var cut = think.Attempts![0];

        Assert.Equal((1, "Stopped", "the engine's process ended"), (cut.N, cut.Outcome, cut.Error));
        Assert.Equal(("model-x", 2, 2), (cut.Model, cut.Turns, cut.Reports));
        Assert.Equal(new AgentUsage(850, 150, 0, 0, 0, 0), cut.Tokens);
        Assert.Equal(0.375, cut.Usd);
        Assert.Equal([new FlowStepSpan(Began, Began.AddSeconds(5))], cut.Runs);
        Assert.Equal(3595.0, think.Paused);

        // The run's budget was charged what the cut-off attempt spent once, and its spend is the sum of its instances'.
        Assert.Equal((1000, 0.375), (flow.Spent.Tokens, flow.Spent.Usd));

        // The step that waited on the limit when the engine ended waited no longer on it than the run stood: the hour is its pause.
        var other = flow.Steps.Single(step => step.Step == "other").Measure!;
        Assert.Equal((Began, (IReadOnlyList<FlowStepWait>?)null), (other.ReadyAt, other.Waited));
        Assert.Equal(3600.0, other.Paused);
    }

    /// <summary>
    /// A run a build that kept no measures recorded is resumed with what it had spent carried whole beside the measures
    /// kept since (<c>spent.unmeasured</c>): its total is never lost for want of a measure, nor counted twice once one is kept.
    /// </summary>
    [Fact]
    public async Task ARunRecordedWithoutMeasures_CarriesItsSpendBesideTheMeasuresKeptSince()
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
        var runner = new Runner().OnAsync("asker", (run, asked) =>
        {
            run.Events.Used(new FlowTurnSpend(new AgentUsage(1000, 200, 0, 0, 0, 0), asked == 1 ? 0.25 : 0.125, Serving: "model-x"));

            return Task.FromResult(asked == 1
                ? FlowStepResult.Asking(new FlowQuestion(FlowQuestions.RunsOwn, "ask", "asker", "what now?", FlowQuestions.OptionsOf("ask", step: null)))
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        });
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);

            Assert.Equal(FlowRunStatus.Paused, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        // The records as a build that kept no measures left them: the run's spend in flow.json, and no step's measure.
        foreach (var file in Directory.GetFiles(layout.Directory, OrchestratorLayout.StepRecordFileName, SearchOption.AllDirectories).Append(layout.FlowFile))
        {
            var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();

            foreach (var step in node["steps"]?.AsArray().Select(step => step!.AsObject()) ?? [node])
            {
                _ = step.Remove("measure");
            }

            File.WriteAllText(file, node.ToJsonString());
        }

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "answer", "go on", out var refused), refused);
            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        var spent = harness.OrchestrationStore.ReadFlow(layout)!.Spent;

        Assert.Equal(new FlowUnmeasuredSpend(0.25, 1200), spent.Unmeasured);
        Assert.Equal((2400, 0.375), (spent.Tokens, spent.Usd));
    }

    /// <summary>
    /// A cut-off attempt whose stream could not be read whole keeps that said on its entry: what it spent past its record
    /// is not known, never charged as nothing, and the run's dollars are not known past what was counted.
    /// </summary>
    [Fact]
    public async Task AResume_WhoseStreamDoesNotRead_SaysTheSpendIsNotKnown()
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
        var (harness, layout, records) = Records("r1", temp.Path, definition, new ManualClock(Began));

        using (records)
        {
            var seen = new Seen(records);
            var running = seen.SeenAsync(snapshot => Step(snapshot, "think").State == FlowInstanceState.Running);
            using var engine = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var session = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(Began), records: seen).RunAsync(engine.Token);
            await running.WaitAsync(Bound, Token);
            await engine.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WaitAsync(Bound, Token));
        }

        var (_, _, again) = Records("r1", temp.Path, definition, new ManualClock(Began.AddMinutes(1)));

        using (again)
        {
            var coordinator = new FlowCoordinator(
                definition,
                new Runner(),
                new RunnerFacts(),
                new ManualClock(Began.AddMinutes(1)),
                resume: TakeUp(harness, layout, definition),
                records: again,
                streamAccount: new CutAccount(FlowCutAttempt.NotRead("its stream does not read whole")));

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;
        var cut = flow.Steps.Single().Measure!.Attempts![0];

        const string NotKnown = "what attempt 1 of 'think' spent past its record is not known: its stream does not read whole";

        Assert.Equal(("Stopped", NotKnown), (cut.Outcome, cut.Uncounted));
        Assert.Equal((0, 0.0, NotKnown), (flow.Spent.Tokens, flow.Spent.Usd, flow.Spent.Uncounted));
    }

    /// <summary>
    /// The time a machine step's machine holds every leg of it back is its wait on that cause, never its run: the attempt's
    /// run stops while it is held and goes on as a leg is let start, and its record says what it waits on meanwhile.
    /// </summary>
    [Fact]
    public async Task AMachineHoldingAStepsLegs_IsItsWait_NotItsRun()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
            """);
        var clock = new ManualClock(Began);
        var (harness, layout, records) = Records("r1", temp.Path, definition, clock);
        var seen = new Seen(records);
        var runner = new Runner().OnAsync("build", async (run, _) =>
        {
            var held = seen.SeenAsync(snapshot => Step(snapshot, "build").WaitsOn.SequenceEqual([FlowWaits.MachineQueue, FlowWaits.MachineRoom]));
            run.Events.Held([FlowWaits.MachineQueue, FlowWaits.MachineRoom]);
            await held.WaitAsync(Bound, Token);

            clock.Advance(TimeSpan.FromSeconds(40));
            var let = seen.SeenAsync(snapshot => Step(snapshot, "build").WaitsOn.Count == 0 && Step(snapshot, "build").Measure?.Attempts?.Single().Runs?.Count == 2);
            run.Events.Held([]);
            await let.WaitAsync(Bound, Token);

            clock.Advance(TimeSpan.FromSeconds(20));
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: seen);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        var build = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single().Measure!;

        Assert.Equal([new FlowStepWait(FlowWaits.MachineQueue, 40.0), new FlowStepWait(FlowWaits.MachineRoom, 40.0)], build.Waited);
        Assert.Equal(20.0, build.Ran);
        Assert.Equal([new FlowStepSpan(Began, Began), new FlowStepSpan(Began.AddSeconds(40), Began.AddSeconds(60))], build.Attempts!.Single().Runs);
    }

    /// <summary>
    /// A machine step's legs are held while every one still to run waits at its machine and none holds a slot there: a
    /// leg that runs, or a unit of a leg let start, keeps the step running; a leg that ends no longer counts; each cause
    /// any of them waits on is said, once, in the order of the admission's stages.
    /// </summary>
    [Fact]
    public void AStepsLegs_AreHeldWhileEveryOneStillToRunWaits()
    {
        var hold = new LegStepRunner.StepHold();

        hold.Placed(["linux", "windows"]);
        Assert.Empty(hold.On());

        hold.Admitting("linux", "linux", AdmissionStage.Room);
        Assert.Empty(hold.On());

        hold.Admitting("windows", "windows", AdmissionStage.Queue);
        Assert.Equal([FlowWaits.MachineQueue, FlowWaits.MachineRoom], hold.On());

        hold.Admitting("windows", "windows", AdmissionStage.Taken);
        Assert.Empty(hold.On());

        hold.Ended("windows");
        Assert.Equal([FlowWaits.MachineRoom], hold.On());

        // A sweep's units: one let start keeps the leg running while another waits; the leg is held once none holds a slot.
        hold.Admitting("linux", "linux", AdmissionStage.Ended);
        hold.Admitting("linux", "linux/arm-1", AdmissionStage.Taken);
        hold.Admitting("linux", "linux/arm-2", AdmissionStage.Memory);
        Assert.Empty(hold.On());

        hold.Admitting("linux", "linux/arm-1", AdmissionStage.Ended);
        Assert.Equal([FlowWaits.MachineMemory], hold.On());

        hold.Ended("linux");
        Assert.Empty(hold.On());
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

    /// <summary>
    /// A runner that throws an exception the engine did not expect ends the step 70, and the attempt's measure entry
    /// keeps the exception's own record: its type, its message, the stack with each frame's file and line.
    /// </summary>
    [Fact]
    public async Task ARunnerThatThrows_ItsAttemptKeepsTheExceptionRecord()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: orient
            """);
        var runner = new Runner().OnAsync("broke", (_, _) => throw new InvalidOperationException("the runner broke"));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Failed, end.Status);
        }

        var broke = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single().Measure!;
        var attempt = broke.Attempts!.Single();

        Assert.Equal("Failed", attempt.Outcome);
        Assert.NotNull(attempt.Exception);
        Assert.Equal("System.InvalidOperationException", attempt.Exception.Type);
        Assert.Equal("the runner broke", attempt.Exception.Message);
        Assert.Contains(attempt.Exception.Stack, frame => frame.Text.Contains(nameof(FlowStepMeasureTests), StringComparison.Ordinal));
    }

    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The snapshot's record of <paramref name="instance"/>.</summary>
    private static FlowSnapshotStep Step(FlowSnapshot snapshot, string instance)
        => snapshot.Steps.Single(step => step.Instance == instance);

    /// <summary>The run's records in a directory of their own, timed by <paramref name="clock"/> where one is given.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FlowRunRecords Records) Records(string run, string directory, WorkflowDefinition definition, TimeProvider? clock = null)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        return (harness, layout, new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of(run, definition, startedAt: Began), clock));
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

    /// <summary>The account of a cut-off step's stored stream, as a test writes it.</summary>
    private sealed class CutAccount(FlowCutAttempt cut) : IFlowStreamAccount
    {
        /// <summary>Every run the account was asked of, in order.</summary>
        public List<FlowStepRun> Asked { get; } = [];

        /// <inheritdoc/>
        public string? Account(FlowStepRun run) => null;

        /// <inheritdoc/>
        public Task<FlowCutAttempt?> AttemptOf(FlowStepRun run, CancellationToken cancellationToken)
        {
            Asked.Add(run);
            return Task.FromResult<FlowCutAttempt?>(cut);
        }
    }

    /// <summary>A run's records written as the engine writes them, telling a test the first snapshot that holds what it waits for.</summary>
    private sealed class Seen(IFlowRecords records) : IFlowRecords
    {
        private readonly List<(Func<FlowSnapshot, bool> Holds, TaskCompletionSource<FlowSnapshot> Seen)> _waits = [];

        /// <summary>The first snapshot written after this call that <paramref name="holds"/> holds of.</summary>
        public Task<FlowSnapshot> SeenAsync(Func<FlowSnapshot, bool> holds)
        {
            var seen = new TaskCompletionSource<FlowSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_waits)
            {
                _waits.Add((holds, seen));
            }

            return seen.Task;
        }

        /// <inheritdoc/>
        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            records.Write(kind, snapshot, instance, from, to, attempt, detail);

            lock (_waits)
            {
                foreach (var (_, seen) in _waits.Where(wait => Holds(wait.Holds, snapshot)))
                {
                    _ = seen.TrySetResult(snapshot);
                }
            }
        }

        // A snapshot that does not hold the step a test asks of yet holds nothing it waits for.
        private static bool Holds(Func<FlowSnapshot, bool> holds, FlowSnapshot snapshot)
        {
            try
            {
                return holds(snapshot);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <inheritdoc/>
        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused) => records.Question(questions, refused);

        /// <inheritdoc/>
        public void Updates(FlowUpdatesFile updates) => records.Updates(updates);

        /// <inheritdoc/>
        public void AffectedTests(IReadOnlyList<FlowAffectedTests> entries) => records.AffectedTests(entries);

        /// <inheritdoc/>
        public string NameAgent(IReadOnlyCollection<string> recorded) => records.NameAgent(recorded);
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

        /// <summary>Answers <paramref name="step"/> from its run and how often it has run, from 1.</summary>
        public Runner OnAsync(string step, Func<FlowStepRun, int, Task<FlowStepResult>> script)
        {
            _scripts[step] = script;
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it.</summary>
        public void Hold(string step)
            => _held[step] = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes a held <paramref name="step"/> as passed, with <paramref name="output"/> where one is given.</summary>
        public bool Release(string step, JsonNode? output = null)
            => _held[step].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: output));

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
            int asked;

            lock (_begun)
            {
                asked = _asked[run.Step.Name] = _asked.GetValueOrDefault(run.Step.Name) + 1;

                if (_begun.TryGetValue(run.Step.Name, out var begun))
                {
                    _ = begun.TrySetResult();
                }
            }

            var answer = _held.TryGetValue(run.Step.Name, out var held)
                ? held.Task
                : _scripts.GetValueOrDefault(run.Step.Name, (_, _) => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run, asked);

            return await answer.WaitAsync(cancellationToken);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }
}
