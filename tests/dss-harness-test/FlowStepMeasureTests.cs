using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// What one step instance's work measured: the attempt's tokens by kind, dollars and the model that served it, the
/// totals the sum of every attempt's, and the record holding it round-trips - written to <c>step.json</c> and read
/// back, so the report of a run that spanned sessions reads the same numbers.
/// </summary>
public sealed class FlowStepMeasureTests
{
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
}
