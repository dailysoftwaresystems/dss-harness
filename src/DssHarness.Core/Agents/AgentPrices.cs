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

        var missing = Charges(provider, model, usage).Where(charge => charge.Price is null).Select(charge => charge.Key);

        return $"no price for '{model.Code}' (providers '{provider.Name}', model '{model.Code}': {string.Join(", ", missing)})";
    }

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
