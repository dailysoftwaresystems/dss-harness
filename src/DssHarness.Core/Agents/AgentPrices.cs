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
    /// the model's <c>largePrompt.overTokens</c> at its own prices.
    /// </summary>
    /// <param name="provider">The model's provider, whose search price and currency hold.</param>
    /// <param name="model">The model that served the request, whose prices hold.</param>
    /// <param name="usage">The request's usage, as its provider reported it.</param>
    /// <param name="promptTokens">The prompt's size, counted as the provider counts it: input, cache writes and cache reads together; 0 where not known, and no large-prompt prices are used.</param>
    /// <returns>
    /// The cost, or <see langword="null"/> where a kind the request used holds no price - the cost is unknown, never
    /// zero - and <see cref="UnknownOf"/> says the keys that would fill it.
    /// </returns>
    public static double? CostOf(AgentProviderConfig provider, AgentModelConfig model, AgentUsage usage, long promptTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);

        var large = promptTokens > 0 && model.LargePrompt is { } over && promptTokens > over.OverTokens ? over : null;
        var cost = 0.0;
        var missing = false;

        void Add(long tokens, double? price, double? largePrice)
        {
            if (tokens <= 0)
            {
                return;
            }

            // A prompt over the model's own bound is priced by the large-prompt entry where it names the kind, and by
            // the flat price where it does not.
            if ((large is null ? price : largePrice ?? price) is not { } at)
            {
                missing = true;
                return;
            }

            cost += tokens / 1_000_000.0 * at;
        }

        Add(usage.InputTokens, model.PriceInputMiss, large?.PriceInputMiss);
        Add(usage.CacheReadTokens, model.PriceInputHit, large?.PriceInputHit);
        Add(usage.OutputTokens, model.PriceOutput, large?.PriceOutput);
        Add(usage.CacheWriteTokens, model.PriceCacheWrite, large?.PriceCacheWrite);

        if (usage.Searches > 0)
        {
            if (provider.PricePerThousandSearches is not { } perThousand)
            {
                missing = true;
            }
            else
            {
                cost += usage.Searches / 1000.0 * perThousand;
            }
        }

        return missing ? null : cost;
    }

    /// <summary>Why a request's cost is unknown: the sentence the report says, naming the model and the keys its entry holds none of.</summary>
    /// <param name="provider">The model's provider.</param>
    /// <param name="model">The model that served the request.</param>
    /// <param name="usage">The request's usage, whose kinds name the keys that would fill.</param>
    /// <param name="promptTokens">The prompt's size, where it was known.</param>
    /// <returns>The sentence, as a report says it.</returns>
    public static string UnknownOf(AgentProviderConfig provider, AgentModelConfig model, AgentUsage usage, long promptTokens = 0)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);

        var large = promptTokens > 0 && model.LargePrompt is { } over && promptTokens > over.OverTokens ? over : null;
        var missing = new List<string>();

        if (usage.InputTokens > 0 && model.PriceInputMiss is null && large?.PriceInputMiss is null)
        {
            missing.Add("priceInputMiss");
        }

        if (usage.CacheReadTokens > 0 && model.PriceInputHit is null && large?.PriceInputHit is null)
        {
            missing.Add("priceInputHit");
        }

        if (usage.OutputTokens > 0 && model.PriceOutput is null && large?.PriceOutput is null)
        {
            missing.Add("priceOutput");
        }

        if (usage.CacheWriteTokens > 0 && model.PriceCacheWrite is null && large?.PriceCacheWrite is null)
        {
            missing.Add("priceCacheWrite");
        }

        if (usage.Searches > 0 && provider.PricePerThousandSearches is null)
        {
            missing.Add("pricePerThousandSearches");
        }

        return $"no price for '{model.Code}' (providers '{provider.Name}', model '{model.Code}': {string.Join(", ", missing)})";
    }
}
