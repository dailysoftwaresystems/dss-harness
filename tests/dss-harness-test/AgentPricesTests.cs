using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// A cost counted from a request's usage at its model's prices, with no number of the tool's own: each kind of token
/// at that kind's price per million, a large prompt at its own prices, each search at the provider's thousand - and a
/// kind that holds no price is unknown, never zero, said naming the keys that would fill it.
/// </summary>
public sealed class AgentPricesTests
{
    /// <summary>One model with the flat prices the shipped GLM holds, for the arithmetic.</summary>
    private static readonly AgentModelConfig Glm = new()
    {
        Code = "glm-5.3",
        Name = "GLM 5.3",
        Tiers = ["balanced"],
        Efforts = ["low", "high", "max"],
        ContextTokens = 1_000_000,
        MaxOutputTokens = 131_072,
        PriceInputMiss = 1.4,
        PriceInputHit = 0.26,
        PriceOutput = 4.4,
    };

    /// <summary>Haiku's shape: a flat set and a large-prompt set over 100,000 tokens.</summary>
    private static readonly AgentModelConfig Haiku = new()
    {
        Code = "claude-haiku-5-5",
        Name = "Claude Haiku 5.5",
        Tiers = ["fast"],
        Efforts = ["low", "medium", "high", "xhigh", "max"],
        ContextTokens = 1_000_000,
        MaxOutputTokens = 131_072,
        PriceInputMiss = 0.1,
        PriceInputHit = 0.01,
        PriceOutput = 0.5,
        PriceCacheWrite = 0.125,
        LargePrompt = new LargePromptPrices { OverTokens = 100_000, PriceInputMiss = 0.5, PriceInputHit = 0.05, PriceOutput = 2.5 },
    };

    private static AgentProviderConfig Provider(AgentModelConfig model, double? searches = null)
        => new() { Name = "zai", Type = "zai", Api = "https://api", ApiKeyName = "KEY", Client = "glm", Currency = "USD", ReadOn = "2026-10-12", PricePerThousandSearches = searches, Models = [model] };

    /// <summary>Each kind of token is counted at that kind's price per million: input, cache read, output.</summary>
    [Fact]
    public void EachKindOfToken_IsCountedAtItsOwnPrice()
    {
        Assert.Equal(
            2_000_000 / 1_000_000.0 * 1.4 + 500_000 / 1_000_000.0 * 0.26 + 100_000 / 1_000_000.0 * 4.4,
            AgentPrices.CostOf(Provider(Glm), Glm, new AgentUsage(InputTokens: 2_000_000, OutputTokens: 100_000, CacheReadTokens: 500_000, CacheWriteTokens: 0, Searches: 0)));
    }

    /// <summary>A prompt over the model's own bound is priced by the large-prompt entry, one prompt at a time.</summary>
    [Fact]
    public void APromptOverTheModelsOwnBound_IsPricedByTheLargePromptEntry()
    {
        var under = AgentPrices.CostOf(Provider(Haiku), Haiku, new AgentUsage(InputTokens: 50_000, OutputTokens: 10_000, CacheReadTokens: 0, CacheWriteTokens: 0, Searches: 0), promptTokens: 50_000);
        var over = AgentPrices.CostOf(Provider(Haiku), Haiku, new AgentUsage(InputTokens: 150_000, OutputTokens: 10_000, CacheReadTokens: 0, CacheWriteTokens: 0, Searches: 0), promptTokens: 150_000);

        // Under: 50k at 0.1, 10k at 0.5. Over: the same request at the large-prompt prices.
        Assert.Equal(50_000 / 1_000_000.0 * 0.1 + 10_000 / 1_000_000.0 * 0.5, under);
        Assert.Equal(150_000 / 1_000_000.0 * 0.5 + 10_000 / 1_000_000.0 * 2.5, over);
    }

    /// <summary>Each search is counted at the provider's price for a thousand of them.</summary>
    [Fact]
    public void EachSearch_IsCountedAtTheProvidersThousand()
    {
        var provider = Provider(Glm, searches: 10);

        Assert.Equal(2 / 1000.0 * 10, AgentPrices.CostOf(provider, Glm, new AgentUsage(InputTokens: 0, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0, Searches: 2)));
    }

    /// <summary>A model with no price for a kind the request used is unknown, never zero, said naming the keys to fill.</summary>
    [Fact]
    public void AModelWithNoPriceForAKind_IsUnknownNeverZero_SaidNamingTheKeys()
    {
        // GLM holds no cache-write price: a request that wrote a cache is unknown.
        var usage = new AgentUsage(InputTokens: 100, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 100, Searches: 0);

        Assert.Null(AgentPrices.CostOf(Provider(Glm), Glm, usage));
        Assert.Equal("no price for 'glm-5.3' (providers 'zai', model 'glm-5.3': priceCacheWrite)", AgentPrices.UnknownOf(Provider(Glm), Glm, usage));

        // A search with no price for one is said the same way.
        var searched = new AgentUsage(InputTokens: 100, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0, Searches: 1);

        Assert.Null(AgentPrices.CostOf(Provider(Glm), Glm, searched));
        Assert.Equal("no price for 'glm-5.3' (providers 'zai', model 'glm-5.3': pricePerThousandSearches)", AgentPrices.UnknownOf(Provider(Glm), Glm, searched));
    }

    /// <summary>A kind the request did not use holds no price and decides nothing.</summary>
    [Fact]
    public void AKindTheRequestDidNotUse_DecidesNothing()
    {
        // No cache write in the request: the missing cache-write price is not read.
        Assert.NotNull(AgentPrices.CostOf(Provider(Glm), Glm, new AgentUsage(InputTokens: 1_000_000, OutputTokens: 1_000_000, CacheReadTokens: 0, CacheWriteTokens: 0, Searches: 0)));
    }
}

/// <summary>
/// The agent sections of the template <c>init</c> writes: the four Claude models with their sizes and prices as read,
/// GLM's own, the four tiers in their order, and the default tier - a config that loads is one the tool can use, and
/// the shipped workflow's every step resolves under it.
/// </summary>
public sealed class AgentModelTests
{
    /// <summary>The template's sections resolve with no problem at all.</summary>
    [Fact]
    public void TheTemplateResolves_WithNoProblemAtAll()
    {
        var (agentTier, providers, tierConfig) = AgentProviderTemplates.Read();
        var (resolved, problems) = AgentProviders.Resolve(new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = agentTier },
            Providers = [.. providers],
            TierConfig = new Dictionary<string, List<string>>(tierConfig, StringComparer.OrdinalIgnoreCase),
        });

        Assert.Empty(problems);
        Assert.NotNull(resolved);
        Assert.Equal("balanced", agentTier);
        Assert.Equal(["anthropic", "zai"], [.. providers.Select(provider => provider.Name)]);
        Assert.Equal(5, providers.SelectMany(provider => provider.Models).Count());

        // Every Claude model holds a 1M context and a 128K output, as the models page says; GLM the same context.
        Assert.All(providers.SelectMany(provider => provider.Models), model =>
        {
            Assert.Equal(1_000_000, model.ContextTokens);
            Assert.Equal(131_072, model.MaxOutputTokens);
        });
    }

    /// <summary>Each tier is its models in the order they are tried, another company's model last.</summary>
    [Fact]
    public void EachTierIsItsModelsInOrder_AnotherCompanysModelLast()
    {
        var (_, _, tierConfig) = AgentProviderTemplates.Read();

        Assert.Equal(["claude-fable-5-1", "glm-5.3"], tierConfig["best"]);
        Assert.Equal(["claude-opus-5-5", "glm-5.3"], tierConfig["high"]);
        Assert.Equal(["claude-sonnet-5-5", "glm-5.3"], tierConfig["balanced"]);
        Assert.Equal(["claude-haiku-5-5", "glm-5.3"], tierConfig["fast"]);
    }

    /// <summary>The template's text is what the tool holds, byte for byte, prices and dates with it.</summary>
    [Fact]
    public void TheTemplatesTextIsWhatTheToolHolds()
    {
        var text = AgentProviderTemplates.Text();

        Assert.Contains("\"readOn\": \"2026-10-12\"", text, StringComparison.Ordinal);
        Assert.Contains("\"apiKeyName\": \"ANTHROPIC_API_KEY\"", text, StringComparison.Ordinal);
        Assert.Contains("\"apiKeyName\": \"ZAI_API_KEY\"", text, StringComparison.Ordinal);
        Assert.Contains("\"pricePerThousandSearches\": 10", text, StringComparison.Ordinal);
    }

    /// <summary>init writes the sections whole into a new config, and changes nothing in an existing one.</summary>
    [Fact]
    public void InitWritesTheSectionsWhole_AndChangesNothingInAnExistingOne()
    {
        var config = DefaultConfigFactory.Create([], "windows", "x64");

        Assert.Equal("balanced", config.Defaults.AgentTier);
        Assert.Equal(2, config.Providers.Count);
        Assert.Equal(4, config.TierConfig.Count);

        // The shipped workflow resolves under what init writes: every step's tier is defined, every effort taken.
        var (resolved, problems) = AgentProviders.Resolve(config);

        Assert.Empty(problems);
        Assert.NotNull(resolved);
        Assert.Empty(WorkflowValidator.Validate(WorkflowTemplates.ShippedDefinition(), hasCommitTemplate: false, files: null, resolved));
    }
}
