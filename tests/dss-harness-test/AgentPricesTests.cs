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
        MaxOutputTokens = 128_000,
        PriceInputMiss = 0.1,
        PriceInputHit = 0.01,
        PriceOutput = 0.5,
        PriceCacheWrite = 0.125,
        LargePrompt = new LargePromptPrices { OverTokens = 100_000, PriceInputMiss = 0.5, PriceInputHit = 0.05, PriceOutput = 2.5 },
    };

    private static AgentProviderConfig Provider(AgentModelConfig model, double? searches = null)
        => new() { Name = "zai", Type = "zai", Api = "https://api", ApiKeyNames = ["KEY"], Client = "glm", Currency = "USD", ReadOn = "2026-10-12", PricePerThousandSearches = searches, Models = [model] };

    /// <summary>Each kind of token is counted at that kind's price per million: input, cache read, output.</summary>
    [Fact]
    public void EachKindOfToken_IsCountedAtItsOwnPrice()
    {
        Assert.Equal(
            2_000_000 / 1_000_000.0 * 1.4 + 500_000 / 1_000_000.0 * 0.26 + 100_000 / 1_000_000.0 * 4.4,
            AgentPrices.CostOf(Provider(Glm), Glm, new AgentUsage(InputTokens: 2_000_000, OutputTokens: 100_000, CacheReadTokens: 500_000, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0)));
    }

    /// <summary>
    /// A prompt over the model's own bound is priced by the large-prompt entry, one request at a time - the prompt
    /// counted from the request's own usage as the provider counts it: input, cache writes and cache reads together,
    /// so a request whose cache reads take it over the bound is a large one. At the bound exactly it is not over it.
    /// </summary>
    [Fact]
    public void APromptOverTheModelsOwnBound_IsPricedByTheLargePromptEntry()
    {
        var under = AgentPrices.CostOf(Provider(Haiku), Haiku, new AgentUsage(InputTokens: 50_000, OutputTokens: 10_000, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0));
        var over = AgentPrices.CostOf(Provider(Haiku), Haiku, new AgentUsage(InputTokens: 150_000, OutputTokens: 10_000, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0));
        var atTheBound = AgentPrices.CostOf(Provider(Haiku), Haiku, new AgentUsage(InputTokens: 100_000, OutputTokens: 10_000, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0));
        var byItsCache = AgentPrices.CostOf(Provider(Haiku), Haiku, new AgentUsage(InputTokens: 10_000, OutputTokens: 10_000, CacheReadTokens: 95_000, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0));

        // Under: 50k at 0.1, 10k at 0.5. Over: the same request at the large-prompt prices.
        Assert.Equal(50_000 / 1_000_000.0 * 0.1 + 10_000 / 1_000_000.0 * 0.5, under);
        Assert.Equal(150_000 / 1_000_000.0 * 0.5 + 10_000 / 1_000_000.0 * 2.5, over);
        Assert.Equal(100_000 / 1_000_000.0 * 0.1 + 10_000 / 1_000_000.0 * 0.5, atTheBound);
        Assert.Equal(10_000 / 1_000_000.0 * 0.5 + 95_000 / 1_000_000.0 * 0.05 + 10_000 / 1_000_000.0 * 2.5, byItsCache);
    }

    /// <summary>
    /// A large prompt using a kind of token its large-prompt set has no price for is unknown, never priced at the
    /// flat price, which would count it short: said naming the large-prompt key that would fill it.
    /// </summary>
    [Fact]
    public void ALargePromptUsingAKindItsSetHasNoPriceFor_IsUnknown()
    {
        // Haiku's large-prompt set here prices no cache write, while its flat set does.
        var usage = new AgentUsage(InputTokens: 150_000, OutputTokens: 10_000, CacheReadTokens: 0, CacheWriteTokens: 1_000, CacheWrite1hTokens: 0, Searches: 0);

        Assert.Null(AgentPrices.CostOf(Provider(Haiku), Haiku, usage));
        Assert.Equal("no price for 'claude-haiku-5-5' (providers 'zai', model 'claude-haiku-5-5': largePrompt.priceCacheWrite)", AgentPrices.UnknownOf(Provider(Haiku), Haiku, usage));
    }

    /// <summary>
    /// A cache write kept an hour is counted at its own price, apart from one kept five minutes: the Claude API bills the
    /// two apart, and counted as the other the budget would be short. A model with no price for it is unknown where a
    /// request wrote one; the hour-long writes are part of the prompt a large-prompt price is chosen by.
    /// </summary>
    [Fact]
    public void AnHourLongCacheWrite_IsCountedAtItsOwnPrice_AndUnknownWhereThereIsNone()
    {
        var fable = new AgentModelConfig
        {
            Code = "claude-fable-5-1",
            Name = "Claude Fable 5.1",
            Tiers = ["best"],
            Efforts = ["low", "medium", "high", "xhigh", "max"],
            ContextTokens = 1_000_000,
            MaxOutputTokens = 128_000,
            PriceInputMiss = 10,
            PriceInputHit = 0.25,
            PriceOutput = 50,
            PriceCacheWrite = 12.5,
            PriceCacheWrite1h = 20,
        };
        var both = new AgentUsage(InputTokens: 0, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 1_000_000, CacheWrite1hTokens: 500_000, Searches: 0);

        Assert.Equal(1_000_000 / 1_000_000.0 * 12.5 + 500_000 / 1_000_000.0 * 20, AgentPrices.CostOf(Provider(fable), fable, both));

        var hour = new AgentUsage(InputTokens: 100, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 100, Searches: 0);

        Assert.Null(AgentPrices.CostOf(Provider(Glm), Glm, hour));
        Assert.Equal("no price for 'glm-5.3' (providers 'zai', model 'glm-5.3': priceCacheWrite1h)", AgentPrices.UnknownOf(Provider(Glm), Glm, hour));

        var large = new AgentUsage(InputTokens: 1_000, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 150_000, Searches: 0);

        Assert.Equal(
            "no price for 'claude-haiku-5-5' (providers 'zai', model 'claude-haiku-5-5': largePrompt.priceCacheWrite1h)",
            AgentPrices.UnknownOf(Provider(Haiku), Haiku, large));
    }

    /// <summary>Each search is counted at the provider's price for a thousand of them.</summary>
    [Fact]
    public void EachSearch_IsCountedAtTheProvidersThousand()
    {
        var provider = Provider(Glm, searches: 10);

        Assert.Equal(2 / 1000.0 * 10, AgentPrices.CostOf(provider, Glm, new AgentUsage(InputTokens: 0, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 2)));
    }

    /// <summary>A model with no price for a kind the request used is unknown, never zero, said naming the keys to fill.</summary>
    [Fact]
    public void AModelWithNoPriceForAKind_IsUnknownNeverZero_SaidNamingTheKeys()
    {
        // GLM holds no cache-write price: a request that wrote a cache is unknown.
        var usage = new AgentUsage(InputTokens: 100, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 100, CacheWrite1hTokens: 0, Searches: 0);

        Assert.Null(AgentPrices.CostOf(Provider(Glm), Glm, usage));
        Assert.Equal("no price for 'glm-5.3' (providers 'zai', model 'glm-5.3': priceCacheWrite)", AgentPrices.UnknownOf(Provider(Glm), Glm, usage));

        // A search with no price for one is said the same way.
        var searched = new AgentUsage(InputTokens: 100, OutputTokens: 0, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 1);

        Assert.Null(AgentPrices.CostOf(Provider(Glm), Glm, searched));
        Assert.Equal("no price for 'glm-5.3' (providers 'zai', model 'glm-5.3': pricePerThousandSearches)", AgentPrices.UnknownOf(Provider(Glm), Glm, searched));
    }

    /// <summary>A kind the request did not use holds no price and decides nothing.</summary>
    [Fact]
    public void AKindTheRequestDidNotUse_DecidesNothing()
    {
        // No cache write in the request: the missing cache-write price is not read.
        Assert.NotNull(AgentPrices.CostOf(Provider(Glm), Glm, new AgentUsage(InputTokens: 1_000_000, OutputTokens: 1_000_000, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0)));
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

        // Every Claude model holds a 1M context and a 128K output - 128000, the max_tokens Anthropic's own Models API
        // reports and the API clients send, which a request over it is refused for - and GLM 5.3 its own 1M and 131072.
        Assert.Equal(
            [
                ("claude-fable-5-1", 1_000_000L, 128_000L),
                ("claude-opus-5-5", 1_000_000L, 128_000L),
                ("claude-sonnet-5-5", 1_000_000L, 128_000L),
                ("claude-haiku-5-5", 1_000_000L, 128_000L),
                ("glm-5.3", 1_000_000L, 131_072L),
            ],
            providers.SelectMany(provider => provider.Models).Select(model => (model.Code, model.ContextTokens, model.MaxOutputTokens)));
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


    /// <summary>init writes the sections whole into a new config, and changes nothing in an existing one.</summary>
    [Fact]
    public void InitWritesTheSectionsWhole_AndChangesNothingInAnExistingOne()
    {
        var config = DefaultConfigFactory.Create([], "windows", "x64");

        Assert.Equal("balanced", config.Defaults.AgentTier);
        Assert.Equal(2, config.Providers.Count);
        Assert.Equal(4, config.TierConfig.Count);
        Assert.Null(config.BalanceKeyUsage);

        // The shipped workflow resolves under what init writes: every step's tier is defined, every effort taken.
        var (resolved, problems) = AgentProviders.Resolve(config);

        Assert.Empty(problems);
        Assert.NotNull(resolved);
        Assert.Empty(WorkflowValidator.Validate(WorkflowTemplates.ShippedDefinition(), hasCommitTemplate: false, files: null, resolved));
    }

    /// <summary>
    /// The shipped workflow fails V38 with any one of its <c>glm-5.3: max</c> entries taken out: GLM 5.3 would run at
    /// the level <c>all</c> gives it, which it does not take, and the refusal names the level to give it.
    /// </summary>
    [Fact]
    public void TheShippedWorkflow_FailsWithAnyOneOfItsGlmMaxEntriesTakenOut()
    {
        var (resolved, problems) = AgentProviders.Resolve(DefaultConfigFactory.Create([], "windows", "x64"));

        Assert.Empty(problems);

        var text = WorkflowTemplates.TextOf("implementation.yml");
        Assert.NotNull(text);

        // The first of the workflow's glm-5.3: max entries - the plan step's, on the tier best - taken out alone.
        var lines = text.Split('\n');
        var at = Array.FindIndex(lines, held => held.TrimStart().StartsWith("glm-5.3:", StringComparison.Ordinal));

        Assert.True(at >= 0, "the shipped workflow holds a glm-5.3 effort entry");

        var without = string.Join("\n", lines.Where((_, index) => index != at));

        var parsed = WorkflowFileParser.Parse("implementation.yml", without);

        Assert.Contains(
            WorkflowValidator.Validate(parsed, hasCommitTemplate: false, files: WorkflowTemplates.TextOf, resolved),
            problem => problem.EndsWith("step 'planning.plan' runs 'glm-5.3' (tier 'best') at effort 'xhigh', which it does not take. It takes: low, high, max. Give it a level of its own under effort: glm-5.3: max", StringComparison.Ordinal));
    }
}
