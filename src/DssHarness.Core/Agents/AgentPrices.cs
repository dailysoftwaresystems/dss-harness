using DssHarness.Core.Configuration;

namespace DssHarness.Core.Agents;

/// <summary>
/// A cost counted from a request's usage at its model's prices, with no number of the tool's own: a model with no
/// price is unknown, never zero, and the answer says the keys to fill.
/// </summary>
public static class AgentPrices
{
    /// <summary>
    /// The cost of <paramref name="usage"/> on <paramref name="model"/>, in its provider's currency: each kind of
    /// token at that kind's price per million, each search at the provider's price for a thousand, and a prompt over
    /// the model's <c>largePrompt.overTokens</c> at its large-prompt prices.
    /// </summary>
    /// <param name="provider">The model's provider, whose search price and currency hold.</param>
    /// <param name="model">The model that served the request, whose prices hold.</param>
    /// <param name="usage">One request's usage, as its provider reported it: its prompt is counted from it.</param>
    /// <returns>
    /// The cost, or <see langword="null"/> where a kind the request used holds no price - the cost is unknown, never
    /// zero - and <see cref="UnknownOf"/> says the keys that would fill it.
    /// </returns>
    public static double? CostOf(AgentProviderConfig provider, AgentModelConfig model, AgentUsage usage)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);

        var cost = 0.0;

        foreach (var (_, count, price) in Charges(provider, model, usage))
        {
            if (price is not { } at)
            {
                return null;
            }

            cost += count * at;
        }

        return cost;
    }

    /// <summary>Why a request's cost is unknown: the sentence the report says, naming the model and every key its entry holds none of that the request used.</summary>
    /// <param name="provider">The model's provider.</param>
    /// <param name="model">The model that served the request.</param>
    /// <param name="usage">One request's usage, whose kinds name the keys that would fill.</param>
    /// <returns>The sentence, as a report says it.</returns>
    public static string UnknownOf(AgentProviderConfig provider, AgentModelConfig model, AgentUsage usage)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);

        return NoPrice(provider, model, [.. Charges(provider, model, usage).Where(charge => charge.Price is null).Select(charge => charge.Key)]);
    }

    /// <summary>
    /// Why a step held to a budget may not start on <paramref name="model"/>: the cost a budget is charged with is counted
    /// from the model's prices, so a run under one needs its input, cache-read and output prices before its first request
    /// (user, 2026-10-26), and its provider's prices in the budget's currency; null where it has them.
    /// </summary>
    /// <param name="provider">The model's provider, whose currency holds.</param>
    /// <param name="model">The model the step starts on.</param>
    /// <returns>The sentence the refusal says, naming the keys to fill; null where nothing is missing.</returns>
    public static string? BudgetRefusal(AgentProviderConfig provider, AgentModelConfig model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        string[] missing = [.. Charges(provider, model, EveryRequestUses).Where(charge => charge.Price is null).Select(charge => charge.Key)];

        return missing.Length > 0 ? $"{NoPrice(provider, model, missing)}, and a budget is charged with the cost counted from them"
            : provider.Currency is { } currency && !string.Equals(currency, BudgetCurrency, StringComparison.Ordinal)
                ? $"providers '{provider.Name}' prices its models in {currency}, and a budget is counted in {BudgetCurrency}"
                : null;
    }

    /// <summary>The currency a budget is counted in: what <c>maxBudgetUsd</c> says.</summary>
    public const string BudgetCurrency = "USD";

    /// <summary>A token of each kind every request may use - input missed and hit, and output - whose prices a budget needs first.</summary>
    private static readonly AgentUsage EveryRequestUses = new(InputTokens: 1, OutputTokens: 1, CacheReadTokens: 1, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0);

    /// <summary>The sentence of a price missing: the model, and each key of its entry that would fill it.</summary>
    private static string NoPrice(AgentProviderConfig provider, AgentModelConfig model, IReadOnlyList<string> keys)
        => $"no price for '{model.Code}' (providers '{provider.Name}', model '{model.Code}': {string.Join(", ", keys)})";

    /// <summary>
    /// The one choice of a price for each kind the request used, read by the cost and by its unknown alike: the key that
    /// prices it, how many it was billed by (tokens per million, searches per thousand), and the price, null where the
    /// configuration holds none. A request whose prompt - input, cache writes and cache reads together, as a provider
    /// that prices by length counts it - is over the model's <c>largePrompt.overTokens</c> is priced by the large-prompt
    /// set alone: a kind that set lacks is unknown, never the flat price, which would count the request short.
    /// </summary>
    private static IEnumerable<(string Key, double Count, double? Price)> Charges(AgentProviderConfig provider, AgentModelConfig model, AgentUsage usage)
    {
        var prompt = usage.InputTokens + usage.CacheWriteTokens + usage.CacheWrite1hTokens + usage.CacheReadTokens;
        var large = model.LargePrompt is { } set && prompt > set.OverTokens ? set : null;
        var prefix = large is null ? string.Empty : "largePrompt.";

        (string Kind, long Tokens, double? Flat, double? Large)[] kinds =
        [
            ("priceInputMiss", usage.InputTokens, model.PriceInputMiss, large?.PriceInputMiss),
            ("priceInputHit", usage.CacheReadTokens, model.PriceInputHit, large?.PriceInputHit),
            ("priceOutput", usage.OutputTokens, model.PriceOutput, large?.PriceOutput),
            ("priceCacheWrite", usage.CacheWriteTokens, model.PriceCacheWrite, large?.PriceCacheWrite),
            ("priceCacheWrite1h", usage.CacheWrite1hTokens, model.PriceCacheWrite1h, large?.PriceCacheWrite1h),
        ];

        foreach (var (kind, tokens, flat, largePrice) in kinds.Where(kind => kind.Tokens > 0))
        {
            yield return (prefix + kind, tokens / 1_000_000.0, large is null ? flat : largePrice);
        }

        if (usage.Searches > 0)
        {
            yield return ("pricePerThousandSearches", usage.Searches / 1000.0, provider.PricePerThousandSearches);
        }
    }
}
