using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;

namespace DssHarness.Tests;

/// <summary>
/// The configuration's providers, models and tiers: what resolves (the way from a model's code to its provider, its
/// address, its key's name and its client, and from a tier to its models in order), and every refusal the sections
/// hold, each by its exact sentence - nothing of a model is in the tool's code, and a config that loads is one the
/// tool can use.
/// </summary>
public sealed class AgentProviderTests
{
    /// <summary>One provider with one model, as a test spells it.</summary>
    private static AgentProviderConfig Provider(
        string name = "zai",
        string type = "zai",
        string api = "https://api.z.ai/api/paas/v4",
        string key = "ZAI_API_KEY",
        string client = "glm",
        string currency = "USD",
        string readOn = "2026-10-12",
        List<AgentModelConfig>? models = null)
        => new()
        {
            Name = name,
            Type = type,
            Api = api,
            ApiKeyName = key,
            Client = client,
            Currency = currency,
            ReadOn = readOn,
            Models = models ?? [Model()],
        };

    /// <summary>One model, as a test spells it.</summary>
    private static AgentModelConfig Model(
        string code = "glm-5.3",
        string name = "GLM 5.3",
        List<string>? tiers = null,
        List<string>? efforts = null,
        long context = 1_000_000,
        long output = 131_072,
        double? miss = 1.4,
        double? outputPrice = 4.4)
        => new()
        {
            Code = code,
            Name = name,
            Tiers = tiers ?? ["balanced"],
            Efforts = efforts ?? ["low", "high", "max"],
            ContextTokens = context,
            MaxOutputTokens = output,
            PriceInputMiss = miss,
            PriceInputHit = 0.26,
            PriceOutput = outputPrice,
        };

    /// <summary>The sections resolved over one provider and one tier.</summary>
    private static (AgentProviders? Providers, IReadOnlyList<string> Problems) Resolve(List<AgentProviderConfig>? providers = null, Dictionary<string, List<string>>? tiers = null, string? agentTier = "balanced")
        => AgentProviders.Resolve(new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = agentTier },
            Providers = providers ?? [Provider()],
            TierConfig = tiers ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
        });

    /// <summary>Selecting a model is enough: its provider, its address, its key's name and its client all follow from its code.</summary>
    [Fact]
    public void SelectingAModelIsEnough_TheProviderAddressKeyAndClientFollow()
    {
        var (providers, problems) = Resolve();

        Assert.Empty(problems);
        Assert.NotNull(providers);

        var of = providers.Of("glm-5.3");

        Assert.True(of is { }, "the model resolves");
        var found = of!.Value;

        Assert.Equal("zai", found.Provider.Name);
        Assert.Equal("https://api.z.ai/api/paas/v4", found.Provider.Api);
        Assert.Equal("ZAI_API_KEY", found.Provider.ApiKeyName);
        Assert.Equal("glm", found.Provider.Client);
        Assert.Equal(["glm-5.3"], [.. providers.ModelsOf("balanced").Select(entry => entry.Model.Code)]);
    }

    /// <summary>A config that declares no providers has no model: a step that needs one is told what to add, and nothing is built in.</summary>
    [Fact]
    public void AConfigWithNoProviders_HasNoModelAndSaysWhatToAdd()
    {
        var (providers, problems) = AgentProviders.Resolve(new HarnessConfig());

        Assert.Empty(problems);
        Assert.NotNull(providers);
        Assert.True(providers.HasNoProvidersAtAll);
        Assert.False(providers.Of("anything") is { }, "no provider lists anything, so nothing resolves");
        Assert.Empty(providers.ModelsOf("balanced"));
        Assert.Null(providers.DefaultTier);
    }

    /// <summary>A provider's name is listed once, and a key's variable is appointed once: a second key is another variable's name.</summary>
    [Fact]
    public void AProvidersNameAndAKeysVariable_AreEachListedOnce()
    {
        var (_, problems) = Resolve(
        [
            Provider(name: "zai"),
            Provider(name: "zai", key: "OTHER_KEY", models: [Model(code: "other")]),
        ],
        tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3", "other"] });

        One(problems, @"providers 1 and 2 are both named 'zai': a provider's name is listed once");

        var (_, appointed) = Resolve(
        [
            Provider(name: "zai"),
            Provider(name: "zai-two", models: [Model(code: "other")]),
        ],
        tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3", "other"] });

        One(appointed, @"providers 'zai' and 'zai-two' both appoint 'ZAI_API_KEY' for their key: a second key is another variable's name");
    }

    /// <summary>A model's code is listed once, across every provider.</summary>
    [Fact]
    public void AModelsCodeIsListedOnce_AcrossEveryProvider()
    {
        var (_, problems) = Resolve(
        [
            Provider(name: "zai"),
            Provider(name: "zai-two", models: [Model(code: "glm-5.3")]),
        ],
        tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] });

        One(problems, @"model 'glm-5.3' is listed by providers 'zai' and 'zai-two': a model's code is listed once, across every provider");
    }

    /// <summary>A provider's client must be one the tool builds in and must reach its type; its api must be an absolute https address; its key a variable's name.</summary>
    [Fact]
    public void AProvidersClientApiAndKey_AreEachRefusedWhereTheyDoNotRead()
    {
        var (_, client) = Resolve([Provider(client: "nonexistent")]);
        One(client, @"providers[0] 'zai' says client 'nonexistent', which is not declared; a client is one the tool builds in: 'claude-code', 'claude-api', 'glm'");

        var (_, reach) = Resolve([Provider(client: "claude-api")]);
        One(reach, @"providers[0] 'zai' says client 'claude-api' (claude-api), which does not reach provider type 'zai'");

        var (_, api) = Resolve([Provider(api: "api.z.ai")]);
        One(api, @"providers[0] 'zai' api 'api.z.ai' is no absolute https address; an address is the company's own or a gateway's, and http only for localhost");

        var (_, key) = Resolve([Provider(key: "zai-key")]);
        One(key, @"providers[0] 'zai' apiKeyName 'zai-key' is not a variable's name: [A-Z][A-Z0-9_]*, a name and never a key");

        var (_, none) = Resolve([Provider(models: [])]);
        One(none, @"providers[0] 'zai' declares no models; a provider is the models it lists, and it lists at least one");
    }

    /// <summary>A model's entry is refused where its efforts name a level its client cannot say, its sizes are not positive whole numbers, or its output is over its context.</summary>
    [Fact]
    public void AModelsEffortsAndSizes_AreRefusedWhereTheyDoNotRead()
    {
        var (_, efforts) = Resolve([Provider(models: [Model(efforts: ["low", "turbo"])])]);
        One(efforts, @"providers[0] 'zai' model 'glm-5.3' takes effort 'turbo', which its client 'glm' cannot say. It can: low, high, max");

        var (_, none) = Resolve([Provider(models: [Model(efforts: [])])]);
        One(none, @"providers[0] 'zai' model 'glm-5.3' takes no efforts; a model's entry names the efforts it takes, each one of its client's levels");

        var (_, context) = Resolve([Provider(models: [Model(context: 0)])]);
        One(context, @"providers[0] 'zai' model 'glm-5.3' contextTokens 0 is not a positive whole number");

        var (_, output) = Resolve([Provider(models: [Model(output: 2_000_000)])]);
        One(output, @"providers[0] 'zai' model 'glm-5.3' maxOutputTokens 2000000 is over its contextTokens 1000000; the most a reply holds is at most what the context holds");
    }

    /// <summary>A model's tiers and a tier's list must agree, each way, and a tier holds at least one model each once.</summary>
    [Fact]
    public void AModelsTiersAndATiersList_AgreeEachWay()
    {
        // A tier's list that leaves out a model its models serve, and a list that is empty: each is refused, each way.
        var (_, disagreeing) = Resolve(tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = [] });
        One(disagreeing, @"model 'glm-5.3' serves tier 'balanced', and tierConfig.balanced does not list it: put it in the list where it is to be tried");
        One(disagreeing, @"tierConfig.balanced is empty; a tier's list is its models in the order they are tried, and it holds at least one");

        var (_, deep) = Resolve([Provider(models: [Model(tiers: ["deep"])])]);
        One(deep, @"providers[0] 'zai' model 'glm-5.3' serves tier 'deep', which tierConfig does not define. Defined: balanced");

        var (_, unlisted) = Resolve(tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["other"] });
        One(unlisted, @"tierConfig.balanced lists 'other', which no provider lists");
    }

    /// <summary>A price below zero, one half of priceInputMiss and priceOutput, a largePrompt with no overTokens, and a price with no currency or readOn: each refused.</summary>
    [Fact]
    public void PricesThatDoNotRead_AreRefusedNamingThem()
    {
        var (_, negative) = Resolve([Provider(models: [Model(miss: -1)])]);
        One(negative, @"providers[0] 'zai' model 'glm-5.3' priceInputMiss -1 is below zero");

        var (_, half) = Resolve([Provider(models: [Model(outputPrice: null)])]);
        One(half, @"providers[0] 'zai' model 'glm-5.3' names one of priceInputMiss and priceOutput and not the other; the two are written together or not at all");

        var (_, noCurrency) = Resolve([Provider(currency: "")]);
        One(noCurrency, @"providers[0] 'zai' holds prices and names no currency; a provider says once the currency of every price of its models, three capital letters");

        var (_, noReadOn) = Resolve([Provider(readOn: "")]);
        One(noReadOn, @"providers[0] 'zai' holds prices and names no readOn; the date its numbers were read (YYYY-MM-DD) is shown wherever a price is, so an old number is not taken for today's");

        var (_, badDate) = Resolve([Provider(readOn: "yesterday")]);
        One(badDate, @"providers[0] 'zai' readOn 'yesterday' is no date; the date its numbers were read is YYYY-MM-DD");

        var (_, badCurrency) = Resolve([Provider(currency: "dollars")]);
        One(badCurrency, @"providers[0] 'zai' currency 'dollars' is not three capital letters");

        var (_, noOver) = Resolve([Provider(models: [new AgentModelConfig { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["balanced"], Efforts = ["low", "high", "max"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072, PriceInputMiss = 1.4, PriceInputHit = 0.26, PriceOutput = 4.4, LargePrompt = new LargePromptPrices { OverTokens = 0, PriceInputMiss = 2 } }])]);
        One(noOver, @"providers[0] 'zai' model 'glm-5.3' largePrompt.overTokens 0 is not a positive whole number");
    }

    /// <summary>tierConfig without providers is refused: a tier names models, and models are a provider's.</summary>
    [Fact]
    public void TierConfigWithoutProviders_IsRefused()
    {
        var (_, problems) = AgentProviders.Resolve(new HarnessConfig
        {
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
        });

        One(problems, @"tierConfig is declared and providers is not: a tier names models, and models are a provider's. Declare providers with its models, or take tierConfig out");
    }

    /// <summary>One problem in the list says this, in part: the sentence is pinned by its own words.</summary>
    private static void One(IReadOnlyList<string> problems, string part)
        => Assert.Contains(problems, problem => problem.Contains(part, StringComparison.Ordinal));
}