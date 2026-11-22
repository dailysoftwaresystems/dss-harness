using System.Globalization;
using System.Text.Json.Serialization;
using DssHarness.Core.Agents;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What one step instance's work measured, written by the engine as it runs and read by the report: when it was ready,
/// how long it waited and for what, how long it ran, and what each of its attempts used - the model that served it, the
/// tokens of every kind, the dollars at the price of that moment, and the tools it called. Held on the instance's record
/// (<see cref="FlowStepRecord.Measure"/>), one owner, kept across a resume.
/// </summary>
/// <param name="ReadyAt">
/// When everything the instance depends on ended and it was free to be drawn; null where the engine never drew it.
/// </param>
/// <param name="Waited">
/// How long it waited, split by the engine's own causes (<see cref="FlowWaits"/>): the dispatch queue, admission's
/// memory or room, a run limit, a person's answer. Each entry names its cause and its seconds.
/// </param>
/// <param name="Ran">The wall-clock seconds of its attempts, summed - the time its own work took.</param>
/// <param name="Paused">The seconds it spent at a safe pause, its work held for a person or a limit.</param>
/// <param name="Attempts">One entry per attempt the instance made, in order.</param>
/// <param name="Totals">The attempts summed: every token, every dollar, the turns and the uncounted reasons; null where none ran.</param>
public sealed record FlowStepMeasure(
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ReadyAt = null,
    IReadOnlyList<FlowStepWait>? Waited = null,
    double? Ran = null,
    double? Paused = null,
    IReadOnlyList<FlowStepAttempt>? Attempts = null,
    FlowStepTotals? Totals = null)
{
    /// <summary>What is wrong with this measure; null when nothing is.</summary>
    public string? Problem()
        => Attempts is { } attempts && attempts.Any(attempt => attempt.Problem() is { } problem) ? attempts.First(attempt => attempt.Problem() is not null).Problem()
            : Attempts is { Count: > 0 } && Totals is null ? "it holds attempts and no totals"
            : Ran is < 0 ? $"it ran {Ran?.ToString("0.0", CultureInfo.InvariantCulture)} seconds"
            : null;
}

/// <summary>One cause the instance waited on, and how long.</summary>
/// <param name="Cause">The cause, as <see cref="FlowWaits"/> names it.</param>
/// <param name="Seconds">How long it waited on it, to one decimal.</param>
public sealed record FlowStepWait(string Cause, double Seconds)
{
    /// <summary>What is wrong with this wait; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Cause) ? "a wait names no cause"
            : Seconds < 0 ? $"a wait of '{Cause}' is {Seconds.ToString("0.0", CultureInfo.InvariantCulture)} seconds"
            : null;
}

/// <summary>
/// What one attempt of a step instance used: when it began and ended, how it ended, what served it and what that cost,
/// and what it called - the record the report's per-step rows and the budget's answer read.
/// </summary>
/// <param name="N">The attempt's number, from 1.</param>
/// <param name="StartedAt">When this attempt began; null where the engine never started it.</param>
/// <param name="FinishedAt">When it ended; null while it runs or where the engine never ended it.</param>
/// <param name="Outcome">How it ended, as the record's state names it (done, failed, skipped); null while it runs.</param>
/// <param name="Exit">The code it ended with; null while it runs.</param>
/// <param name="Error">Why it ended as it did, where the code alone cannot say; null where none or while it runs.</param>
/// <param name="Cause">The cause its ending was recorded with; null where none.</param>
/// <param name="Provider">The provider that served the attempt; null for a machine step.</param>
/// <param name="Model">The model that served it - the last after any fallback; null for a machine step.</param>
/// <param name="Effort">The effort the model was asked with; null where none.</param>
/// <param name="FallbackFrom">The model the attempt began on and left, where a fallback moved it; null where none did.</param>
/// <param name="FallbackReason">Why it moved: the reason the walk gives.</param>
/// <param name="Tokens">The tokens the attempt's turns used, by kind, summed; null where none was reported.</param>
/// <param name="Usd">What the attempt cost at the prices of the model serving it, as far as each turn could be counted.</param>
/// <param name="Price">The price its cost was counted at, copied when it was counted: a price changed in the configuration later never changes what a past attempt cost.</param>
/// <param name="Uncounted">Why a part of its cost could not be counted - a model with no price, a launch billed to usage credits; null where every part was.</param>
/// <param name="Turns">The agent's turns; null for a machine step.</param>
/// <param name="Tools">The tool calls the attempt made, by tool name, with how many failed or were denied; null for a machine step or where the agent called none.</param>
/// <param name="Compactions">How many times the agent's conversation was compacted; null where none.</param>
/// <param name="RetryAfter">The backoff the engine waited before the attempt ran again; null for the last or the only attempt.</param>
public sealed record FlowStepAttempt(
    int N,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartedAt = null,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FinishedAt = null,
    string? Outcome = null,
    int? Exit = null,
    string? Error = null,
    string? Cause = null,
    string? Provider = null,
    string? Model = null,
    string? Effort = null,
    string? FallbackFrom = null,
    string? FallbackReason = null,
    AgentUsage? Tokens = null,
    double? Usd = null,
    FlowStepPrice? Price = null,
    string? Uncounted = null,
    int? Turns = null,
    IReadOnlyList<FlowStepToolUse>? Tools = null,
    int? Compactions = null,
    double? RetryAfter = null)
{
    /// <summary>What is wrong with this attempt; null when nothing is.</summary>
    public string? Problem()
        => N < 1 ? $"its number is {N.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : Usd is < 0 ? $"it cost {Usd?.ToString("0.0000", CultureInfo.InvariantCulture)} USD"
            : RetryAfter is < 0 ? $"it waited {RetryAfter?.ToString("0.0", CultureInfo.InvariantCulture)} seconds to run again"
            : null;
}

/// <summary>The price an attempt's cost was counted at, as the configuration gave it when it was counted.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Currency">The currency the prices are in.</param>
/// <param name="ReadOn">When the numbers were read, as the configuration records it.</param>
/// <param name="InputMiss">Per million input tokens not read from the cache.</param>
/// <param name="InputHit">Per million input tokens read from the cache.</param>
/// <param name="Output">Per million output tokens.</param>
/// <param name="CacheWrite">Per million tokens written to the cache.</param>
/// <param name="CacheWrite1h">Per million tokens written to the one-hour cache.</param>
public sealed record FlowStepPrice(
    string Provider,
    string Model,
    string Currency,
    string? ReadOn = null,
    double? InputMiss = null,
    double? InputHit = null,
    double? Output = null,
    double? CacheWrite = null,
    double? CacheWrite1h = null)
{
    /// <summary>
    /// The price of <paramref name="model"/> under <paramref name="provider"/>, as the configuration gives it now; null
    /// where the model is not listed, which the attempt records as its uncounted reason instead.
    /// </summary>
    public static FlowStepPrice? Of(Configuration.AgentProviderConfig provider, Configuration.AgentModelConfig model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        return new FlowStepPrice(
            provider.Name,
            model.Code,
            provider.Currency is { Length: > 0 } currency ? currency : AgentPrices.BudgetCurrency,
            provider.ReadOn,
            model.PriceInputMiss,
            model.PriceInputHit,
            model.PriceOutput,
            model.PriceCacheWrite,
            model.PriceCacheWrite1h);
    }
}

/// <summary>The tool calls one attempt made, by tool name.</summary>
/// <param name="Tool">The tool, as the agent named it.</param>
/// <param name="Calls">How many times the agent called it.</param>
/// <param name="Failed">How many of its calls failed.</param>
public sealed record FlowStepToolUse(string Tool, int Calls, int Failed)
{
    /// <summary>What is wrong with this use; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Tool) ? "a tool use names no tool"
            : Calls < 0 || Failed < 0 || Failed > Calls ? $"the tool '{Tool}' was called {Calls.ToString(CultureInfo.InvariantCulture)} time(s), {Failed.ToString(CultureInfo.InvariantCulture)} of them failing"
            : null;
}

/// <summary>What the attempts of one instance used, summed: every token, every dollar, the turns and the uncounted reasons.</summary>
/// <param name="Tokens">The tokens every attempt used, by kind.</param>
/// <param name="Usd">What they cost, as far as each part could be counted.</param>
/// <param name="Uncounted">Why the rest could not be counted; null where every part was.</param>
/// <param name="Turns">The turns every agent attempt made; null where the instance ran no agent.</param>
public sealed record FlowStepTotals(
    AgentUsage? Tokens = null,
    double? Usd = null,
    string? Uncounted = null,
    int? Turns = null)
{
    /// <summary>The totals of <paramref name="attempts"/>, as the record holds them; null where none ran.</summary>
    public static FlowStepTotals Of(IReadOnlyList<FlowStepAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        if (attempts.Count == 0)
        {
            return new FlowStepTotals();
        }

        var tokens = attempts.Where(attempt => attempt.Tokens is not null).Select(attempt => attempt.Tokens!).Aggregate(AgentUsage.None, (sum, use) => sum.Plus(use));
        var usd = attempts.Sum(attempt => attempt.Usd ?? 0);
        var uncounted = attempts.Select(attempt => attempt.Uncounted).FirstOrDefault(reason => reason is not null);
        var turns = attempts.Sum(attempt => attempt.Turns ?? 0);

        return new FlowStepTotals(
            tokens.AllTokens > 0 ? tokens : null,
            usd > 0 ? usd : null,
            uncounted,
            turns > 0 ? turns : null);
    }
}

/// <summary>
/// What one attempt has used so far, as its turns report it - the engine's own accumulator, charged by the one place a
/// token is counted and read when the attempt ends to write the measure's entry. One owner of the counting per attempt.
/// </summary>
public sealed class FlowStepSpend
{
    /// <summary>Every token the attempt's turns used, of every kind; null where no turn's usage was reported.</summary>
    public AgentUsage? Tokens { get; private set; }

    /// <summary>What the attempt cost, as far as each turn could be counted; null where no turn reported usage.</summary>
    public double? Usd { get; private set; }

    /// <summary>Why a part of its cost could not be counted; null where every part was.</summary>
    public string? Uncounted { get; private set; }

    /// <summary>Whether any turn's usage was reported.</summary>
    public bool Reported { get; private set; }

    /// <summary>The model that served the attempt's latest turn - the last after any fallback; null where none served it yet.</summary>
    public string? Serving { get; internal set; }

    /// <summary>Charges one turn's usage, as <c>TakeMessage</c> hands it from <c>RunEvents.Used</c>.</summary>
    /// <param name="usage">The turn's tokens, by kind.</param>
    /// <param name="usd">What the turn cost, where it could be counted.</param>
    /// <param name="uncounted">Why it could not, where it could not.</param>
    public void Charge(AgentUsage usage, double? usd, string? uncounted)
    {
        ArgumentNullException.ThrowIfNull(usage);

        Tokens = (Tokens ?? AgentUsage.None).Plus(usage);
        Usd = (Usd ?? 0) + (usd ?? 0);
        Uncounted ??= uncounted;
        Reported = true;
    }
}
