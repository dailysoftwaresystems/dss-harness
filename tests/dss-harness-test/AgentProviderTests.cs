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
        List<string>? keys = null,
        List<AgentModelConfig>? models = null)
        => new()
        {
            Name = name,
            Type = type,
            Api = api,
            ApiKeyNames = keys ?? [key],
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
    private static (AgentProviders? Providers, IReadOnlyList<string> Problems) Resolve(
        List<AgentProviderConfig>? providers = null,
        Dictionary<string, List<string>>? tiers = null,
        string? agentTier = "balanced",
        bool? balanceKeyUsage = null)
        => AgentProviders.Resolve(new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = agentTier },
            BalanceKeyUsage = balanceKeyUsage,
            Providers = providers ?? [Provider()],
            TierConfig = tiers ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
        });

    /// <summary>The anthropic provider claude-code reaches, as a test spells it.</summary>
    private static AgentProviderConfig Anthropic(List<string>? keys = null)
        => Provider(
            name: "anthropic",
            type: "anthropic",
            api: "https://api.anthropic.com",
            client: "claude-code",
            keys: keys ?? ["ANTHROPIC_API_KEY"],
            models: [Model(code: "claude-sonnet-5-5", name: "Claude Sonnet 5.5", efforts: ["low", "medium", "high", "xhigh", "max"])]);

    /// <summary>A config over <paramref name="providers"/> whose client entries are <paramref name="clients"/>.</summary>
    private static HarnessConfig Config(Dictionary<string, AgentClientConfig> clients, List<AgentProviderConfig> providers)
        => new()
        {
            Defaults = new HarnessDefaults { AgentTier = "balanced" },
            Providers = providers,
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["balanced"] = [.. providers.SelectMany(provider => provider.Models).Select(model => model.Code)],
            },
            AgentClients = new(clients, StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>The providers resolved under <paramref name="clients"/>, which must hold.</summary>
    private static AgentProviders Clients(Dictionary<string, AgentClientConfig> clients, List<AgentProviderConfig> providers)
    {
        var (resolved, problems) = AgentProviders.Resolve(Config(clients, providers));

        Assert.Empty(problems);

        return resolved!;
    }

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
        Assert.Equal(["ZAI_API_KEY"], found.Provider.ApiKeyNames);
        Assert.Equal("glm", found.Provider.Client);
        Assert.Equal(["glm-5.3"], [.. providers.ModelsOf("balanced").Select(entry => entry.Model.Code)]);
    }

    /// <summary>A provider's keys are a list, in the order they are tried: each listed once, at least one, each a variable's name.</summary>
    [Fact]
    public void AProvidersKeys_AreAListTriedInOrder()
    {
        var (two, problems) = Resolve([Provider(keys: ["ZAI_API_KEY", "ZAI_API_KEY_TWO"])]);

        Assert.Empty(problems);
        Assert.Equal(["ZAI_API_KEY", "ZAI_API_KEY_TWO"], two!.Of("glm-5.3")!.Value.Provider.ApiKeyNames);

        var (_, twice) = Resolve([Provider(keys: ["ZAI_API_KEY", "ZAI_API_KEY"])]);
        Exactly(twice, @"provider 'zai' lists 'ZAI_API_KEY' twice in apiKeyNames: each key is listed once");

        var (_, none) = Resolve([Provider(keys: [])]);
        Exactly(none, @"providers[0] 'zai' declares no apiKeyNames; a provider's keys are at least one variable's name, in the order they are tried");

        var (_, notAName) = Resolve([Provider(keys: ["zai-key"])]);
        Exactly(notAName, @"providers[0] 'zai' apiKeyNames 'zai-key' is not a variable's name: [A-Z][A-Z0-9_]*, a name and never a key");
    }

    /// <summary>'all' is the word a step's effort uses for every model it does not name, so no model may take it as its code.</summary>
    [Fact]
    public void TheCodeAll_IsNoModelsCode()
    {
        var (_, problems) = Resolve([Provider(models: [Model(code: "all")])],
            tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["all"] });

        Exactly(problems, @"'all' cannot be a model's code: a step's effort uses it for every model it does not name");
    }

    /// <summary>A client of the configuration is one the tool builds in; each key of one is read by the slice that builds it.</summary>
    [Fact]
    public void AnAgentClientIsOneTheToolBuildsIn()
    {
        var mine = new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = "balanced" },
            Providers = [Provider()],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
            AgentClients = new(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-code"] = new AgentClientConfig { Update = false },
                ["mine"] = new AgentClientConfig(),
            },
        };

        var (_, problems) = AgentProviders.Resolve(mine);

        Exactly(problems, "agentClients 'mine' is not a client this tool builds in: 'claude-code', 'claude-api', 'glm'");

        var glm = new HarnessConfig
        {
            Defaults = new HarnessDefaults { AgentTier = "balanced" },
            Providers = [Provider()],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
            AgentClients = new(StringComparer.OrdinalIgnoreCase) { ["glm"] = new AgentClientConfig { Update = false } },
        };

        var (_, ofClientAlone) = AgentProviders.Resolve(glm);

        Exactly(ofClientAlone, "agentClients 'glm' names update, a key of the claude-code client alone: whether the tool installs Claude Code with npm and brings one up to date");

        var (_, misnamed) = AgentProviders.Resolve(Config(
            new() { ["Claude-API"] = new AgentClientConfig { Update = false, Auth = AgentAuth.Login, MaxRetries = 1, ConfigDir = "MY_DIR" } },
            [Provider()]));

        Exactly(misnamed, "agentClients 'Claude-API' is not a client this tool builds in: 'claude-code', 'claude-api', 'glm'");

        var served = Provider(name: "anthropic", type: "anthropic", api: "https://api.anthropic.com", key: "ANTHROPIC_API_KEY", client: "claude-api", models: [Model(code: "claude-sonnet-5-5", name: "Claude Sonnet 5.5", efforts: ["low", "high"])]);
        var (_, misnamedServed) = AgentProviders.Resolve(Config(new() { ["Claude-API"] = new AgentClientConfig { ConfigDir = "MY_DIR", MaxRetries = 1 } }, [served]));

        Exactly(misnamedServed, "agentClients 'Claude-API' is not a client this tool builds in: 'claude-code', 'claude-api', 'glm'");
    }

    /// <summary>
    /// claude-code signs in by its entry's auth - the machine's login where it says none, reading no key; a key of the
    /// provider's, in their order; a token under the entry's token or the name Claude Code reads it from, appointed by
    /// the client - and an API client signs each request with its provider's keys whatever claude-code's entry says.
    /// </summary>
    [Fact]
    public void ClaudeCodesSignIn_IsItsEntrysAuth_TheLoginWhereItSaysNone()
    {
        var keys = new List<string> { "ANTHROPIC_API_KEY", "ANTHROPIC_API_KEY_TWO" };
        var anthropic = Anthropic(keys);

        var login = Clients([], [anthropic]);
        Assert.Equal((AgentAuth.Login, (string?)null), (login.AuthOf("claude-code"), login.TokenOf("claude-code")));
        Assert.Empty(login.KeysRead(anthropic));
        Assert.Null(login.AuthOf("claude-api"));
        Assert.Null(login.AuthOf("glm"));
        Assert.Equal(keys, login.KeysRead(anthropic, "claude-api"));

        var apiKey = Clients(new() { ["claude-code"] = new AgentClientConfig { Auth = AgentAuth.ApiKey } }, [anthropic]);
        Assert.Equal((AgentAuth.ApiKey, (string?)null), (apiKey.AuthOf("claude-code"), apiKey.TokenOf("claude-code")));
        Assert.Equal(keys, apiKey.KeysRead(anthropic));

        var oauth = Clients(new() { ["claude-code"] = new AgentClientConfig { Auth = AgentAuth.OAuthToken } }, [anthropic]);
        Assert.Equal("CLAUDE_CODE_OAUTH_TOKEN", oauth.TokenOf("claude-code"));
        Assert.Equal(["CLAUDE_CODE_OAUTH_TOKEN"], oauth.KeysRead(anthropic));
        Assert.Equal(keys, oauth.KeysRead(anthropic, "claude-api"));
        Assert.Contains(
            new Core.Secrets.AppointedKey("CLAUDE_CODE_OAUTH_TOKEN", "client 'claude-code'", "the token client 'claude-code' signs in with (oauthToken)", "token"),
            oauth.Appointed());

        var named = Clients(new() { ["claude-code"] = new AgentClientConfig { Auth = AgentAuth.AuthToken, Token = "GATEWAY_TOKEN" } }, [anthropic]);
        Assert.Equal("GATEWAY_TOKEN", named.TokenOf("claude-code"));
        Assert.Equal(["GATEWAY_TOKEN"], named.KeysRead(anthropic));
        Assert.Equal("ANTHROPIC_AUTH_TOKEN", AgentAuth.TokenVariable(AgentAuth.AuthToken));
        Assert.DoesNotContain(login.Appointed(), key => key.Name is "CLAUDE_CODE_OAUTH_TOKEN" or "ANTHROPIC_AUTH_TOKEN");
    }

    /// <summary>
    /// A web-search source's key of the slot is appointed for it, a secret like a provider's; a source that takes the
    /// machine's gh login, as the built-in GitHub sources do, appoints none.
    /// </summary>
    [Fact]
    public void AWebSearchSourcesKey_IsAppointed_AndAGhLoginIsNot()
    {
        var config = new HarnessConfig
        {
            AgentWebSearch = System.Text.Json.JsonSerializer.Deserialize<AgentWebSearchConfig>(
                """{ "sources": { "stackoverflow": { "credential": { "from": "env", "name": "STACKEXCHANGE_KEY", "query": "key" } } } }""",
                JsonConfigOptions.Default),
        };
        var (providers, problems) = AgentProviders.Resolve(config);

        Assert.Empty(problems);
        Assert.Equal(
            [new Core.Secrets.AppointedKey("STACKEXCHANGE_KEY", "web-search source 'stackoverflow'", "the key web-search source 'stackoverflow' is sent, where it is there", "credential.name")],
            providers!.Appointed().Where(key => key.By.StartsWith("web-search", StringComparison.Ordinal)));
        Assert.True(providers.Appointed().Single(key => key.Name == "STACKEXCHANGE_KEY").Secret);
        Assert.Equal([("stackoverflow", "STACKEXCHANGE_KEY")], providers.SourceKeys());
    }

    /// <summary>
    /// auth and token are refused where they do not read: on a client that signs each request with its provider's key,
    /// a way that is none of the four, a token where the way reads none, a token that is not a variable's name, and a
    /// name appointed for a token and for a provider's key both.
    /// </summary>
    [Fact]
    public void AuthAndToken_AreRefusedWhereTheyDoNotRead()
    {
        Exactly(
            Refused(new() { ["glm"] = new AgentClientConfig { Auth = AgentAuth.ApiKey } }),
            "agentClients 'glm' names auth, a key of a client that signs in (claude-code): 'glm' signs each request with its provider's key");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Auth = "password" } }),
            "agentClients 'claude-code' auth 'password' is none of: login, apiKey, oauthToken, authToken");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Token = "MY_TOKEN" } }),
            "agentClients 'claude-code' names token, which only auth: oauthToken or authToken reads; it signs in with auth: login");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Auth = AgentAuth.ApiKey, Token = "MY_TOKEN" } }),
            "agentClients 'claude-code' names token, which only auth: oauthToken or authToken reads; it signs in with auth: apiKey");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Auth = AgentAuth.OAuthToken, Token = "my-token" } }),
            "agentClients 'claude-code' token 'my-token' is not a variable's name: [A-Z][A-Z0-9_]*, a name and never a token");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Auth = AgentAuth.AuthToken, Token = "ANTHROPIC_API_KEY" } }),
            "agentClients 'claude-code' appoints 'ANTHROPIC_API_KEY' for its token and provider 'anthropic' for a key: a name is appointed once");

        static IReadOnlyList<string> Refused(Dictionary<string, AgentClientConfig> clients)
            => AgentProviders.Resolve(Config(clients, [Anthropic()])).Problems;
    }

    /// <summary>
    /// claude-code's settings are read under the names its entry gives - configDir, executable - or its own where it gives
    /// none, and appointed by the client as settings, never keys.
    /// </summary>
    [Fact]
    public void ClaudeCodesSettings_AreReadUnderTheNamesItsEntryGives_OrItsOwn()
    {
        var anthropic = Anthropic();

        var own = Clients([], [anthropic]);
        Assert.Equal(("CLAUDE_CONFIG_DIR", "CLAUDE_EXECUTABLE"), (own.SettingOf("claude-code", AgentClientSetting.ConfigDirKey), own.SettingOf("claude-code", AgentClientSetting.ExecutableKey)));
        Assert.Null(own.SettingOf("glm", AgentClientSetting.ConfigDirKey));

        var named = Clients(new() { ["claude-code"] = new AgentClientConfig { ConfigDir = "WORK_CLAUDE_DIR", Executable = "MY_CLAUDE" } }, [anthropic]);
        Assert.Equal(("WORK_CLAUDE_DIR", "MY_CLAUDE"), (named.SettingOf("claude-code", AgentClientSetting.ConfigDirKey), named.SettingOf("claude-code", AgentClientSetting.ExecutableKey)));
        Assert.Contains(
            new Core.Secrets.AppointedKey("WORK_CLAUDE_DIR", "client 'claude-code'", "the Claude Code directory the claude-code client signs in from and reads the user's settings in", "configDir", Secret: false),
            named.Appointed());
        Assert.Contains(
            new Core.Secrets.AppointedKey("MY_CLAUDE", "client 'claude-code'", "the program the claude-code client starts in place of npm's Claude Code", "executable", Secret: false),
            named.Appointed());
        Assert.DoesNotContain(named.Appointed(), key => key.Name is "CLAUDE_CONFIG_DIR" or "CLAUDE_EXECUTABLE");
    }

    /// <summary>
    /// configDir and executable are refused where they do not read: on a client that reads no such setting, a name that is
    /// no variable's, and a name - given or a client's own - appointed twice: beside a provider's key, the client's token
    /// (the one its entry names or the one Claude Code reads) or its other setting.
    /// </summary>
    [Fact]
    public void ClaudeCodesSettings_AreRefusedWhereTheyDoNotRead()
    {
        Exactly(
            Refused(new() { ["glm"] = new AgentClientConfig { ConfigDir = "WORK_CLAUDE_DIR" } }),
            "agentClients 'glm' names configDir, a key of the claude-code client alone: the variable that holds the Claude Code directory the claude-code client signs in from and reads the user's settings in");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Executable = "my-claude" } }),
            "agentClients 'claude-code' executable 'my-claude' is not a variable's name: [A-Z][A-Z0-9_]*, a name and never a value");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { ConfigDir = "ANTHROPIC_API_KEY" } }),
            "agentClients 'claude-code' appoints 'ANTHROPIC_API_KEY' for its configDir and provider 'anthropic' for a key: a name is appointed once");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Auth = AgentAuth.OAuthToken, Token = "GATEWAY", ConfigDir = "GATEWAY" } }),
            "agentClients 'claude-code' appoints 'GATEWAY' for its token and its configDir: a name is appointed once");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Auth = AgentAuth.OAuthToken, Executable = "CLAUDE_CODE_OAUTH_TOKEN" } }),
            "agentClients 'claude-code' appoints 'CLAUDE_CODE_OAUTH_TOKEN' for its token and its executable: a name is appointed once");
        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { Executable = "CLAUDE_CONFIG_DIR" } }),
            "agentClients 'claude-code' appoints 'CLAUDE_CONFIG_DIR' for its executable and its configDir: a name is appointed once");
        Exactly(
            AgentProviders.Resolve(Config([], [Anthropic(["CLAUDE_EXECUTABLE"])])).Problems,
            "agentClients 'claude-code' appoints 'CLAUDE_EXECUTABLE' for its executable and provider 'anthropic' for a key: a name is appointed once");

        static IReadOnlyList<string> Refused(Dictionary<string, AgentClientConfig> clients)
            => AgentProviders.Resolve(Config(clients, [Anthropic()])).Problems;
    }

    /// <summary>
    /// The API clients' keys are each held to the clients it is of - their tries, their longest wait and the fields merged
    /// into their requests both API clients', the cache's lifetime and the betas claude-api's, keeping the reasoning glm's
    /// - and to what each can be: the API's own fallback never asked for by claude-api, and no field either client sends
    /// itself set by its requestExtras.
    /// </summary>
    [Fact]
    public void TheApiClientsKeys_AreItsAlone_AndHeldToWhatEachCanBe()
    {
        using var extras = System.Text.Json.JsonDocument.Parse("""{"metadata":{"user_id":"u1"},"fallbacks":"default","output_config":{"task_budget":{"type":"tokens","total":1}}}""");
        var api = Provider(name: "anthropic", type: "anthropic", api: "https://api.anthropic.com", key: "ANTHROPIC_API_KEY", client: ClaudeApiClient, models: [Model(code: "claude-sonnet-5-5", name: "Claude Sonnet 5.5", efforts: ["low", "high"])]);

        Exactly(
            Refused(new() { ["claude-code"] = new AgentClientConfig { MaxRetries = 2, MaxRetryWaitSeconds = 30, CacheTtl = "1h", RequestExtras = extras.RootElement.Clone(), Betas = ["b"], PreserveThinking = true } }, Anthropic()),
            "agentClients 'claude-code' names maxRetries, a key of the API clients alone (claude-api, glm): how many times a failed request is asked again",
            "agentClients 'claude-code' names maxRetryWaitSeconds, a key of the API clients alone (claude-api, glm): the longest wait before a failed request is asked again",
            "agentClients 'claude-code' names requestExtras, a key of the API clients alone (claude-api, glm): the fields merged into every request's body",
            "agentClients 'claude-code' names cacheTtl, a key of the claude-api client alone: how long what a request caches is kept",
            "agentClients 'claude-code' names betas, a key of the claude-api client alone: the beta headers sent beside the tool's own",
            "agentClients 'claude-code' names preserveThinking, a key of the glm client alone: whether Z.AI keeps the model's reasoning across turns");
        Exactly(
            Refused(new() { [ClaudeApiClient] = new AgentClientConfig { PreserveThinking = false } }, api),
            "agentClients 'claude-api' names preserveThinking, a key of the glm client alone: whether Z.AI keeps the model's reasoning across turns");

        using var zaiExtras = System.Text.Json.JsonDocument.Parse("""{"user_id":"u1","fallbacks":"default","model":"x","thinking":{"clear_thinking":true}}""");

        Exactly(
            Refused(new() { [Glm] = new AgentClientConfig { MaxRetries = -1, MaxRetryWaitSeconds = 0, CacheTtl = "5m", RequestExtras = zaiExtras.RootElement.Clone(), Betas = ["b"] } }, Provider()),
            "agentClients 'glm' names cacheTtl, a key of the claude-api client alone: how long what a request caches is kept",
            "agentClients 'glm' names betas, a key of the claude-api client alone: the beta headers sent beside the tool's own",
            "agentClients 'glm' maxRetries is -1; how many times a failed request is asked again is none or more",
            "agentClients 'glm' maxRetryWaitSeconds is 0; the longest wait before a failed request is asked again is a second or more",
            "agentClients 'glm' requestExtras.model is refused: the tool sends it itself",
            "agentClients 'glm' requestExtras.thinking.clear_thinking is refused: the tool sends it itself");

        using var zaiFine = System.Text.Json.JsonDocument.Parse("""{"user_id":"u1"}""");

        Assert.Empty(Refused(new() { [Glm] = new AgentClientConfig { MaxRetries = 0, MaxRetryWaitSeconds = 1, PreserveThinking = true, RequestExtras = zaiFine.RootElement.Clone() } }, Provider()));
        Exactly(
            Refused(new() { [ClaudeApiClient] = new AgentClientConfig { MaxRetries = -1, MaxRetryWaitSeconds = 0, CacheTtl = "1d", RequestExtras = extras.RootElement.Clone(), Betas = ["a b", "server-side-fallback-2026-01-01", "x", "x", ""] } }, api),
            "agentClients 'claude-api' maxRetries is -1; how many times a failed request is asked again is none or more",
            "agentClients 'claude-api' maxRetryWaitSeconds is 0; the longest wait before a failed request is asked again is a second or more",
            "agentClients 'claude-api' requestExtras.fallbacks is refused: no request carries fallbacks, for the engine's fallback is the only one a step has",
            "agentClients 'claude-api' cacheTtl '1d' is none of: 5m, 1h",
            "agentClients 'claude-api' betas 'a b' is no beta's name: one word, sent in a header beside the tool's own",
            "agentClients 'claude-api' betas 'server-side-fallback-2026-01-01' is refused: the API's own fallback is never asked for, for the engine's fallback is the only one a step has",
            "agentClients 'claude-api' betas '' is no beta's name: one word, sent in a header beside the tool's own",
            "agentClients 'claude-api' lists 'x' twice in betas: each beta is listed once");

        using var fine = System.Text.Json.JsonDocument.Parse("""{"metadata":{"user_id":"u1"}}""");

        Assert.Empty(Refused(new() { [ClaudeApiClient] = new AgentClientConfig { MaxRetries = 0, MaxRetryWaitSeconds = 1, CacheTtl = "5m", RequestExtras = fine.RootElement.Clone(), Betas = ["b-2026-01-01"] } }, api));

        static IReadOnlyList<string> Refused(Dictionary<string, AgentClientConfig> clients, AgentProviderConfig provider)
            => AgentProviders.Resolve(Config(clients, [provider])).Problems;
    }

    private const string ClaudeApiClient = Core.Agents.ClaudeApi.ClaudeApiClient.ClientName;

    private const string Glm = Core.Agents.ZaiApi.ZaiApiClient.ClientName;

    /// <summary>balanceKeyUsage is refused where it is on and a provider with two keys or more lists a model with no price; one key is nothing to balance, and off is never refused.</summary>
    [Fact]
    public void BalanceKeyUsage_IsRefusedWhereASpendCouldNotBeCounted()
    {
        var unpriced = Provider(keys: ["ZAI_API_KEY", "ZAI_API_KEY_TWO"], models: [Model(miss: null, outputPrice: null)]);

        var (_, on) = Resolve([unpriced], balanceKeyUsage: true);
        Exactly(on, @"balanceKeyUsage balances the keys of 'zai' by what each spent, and 'glm-5.3' has no price (providers 'zai', model 'glm-5.3': priceInputMiss, priceOutput). Price it, or turn balanceKeyUsage off");

        var (_, oneKey) = Resolve([Provider(models: [Model(miss: null, outputPrice: null)])], balanceKeyUsage: true);
        Assert.Empty(oneKey);

        // Two keys and every model priced: each key's spend can be counted, so balancing them is no problem.
        var (_, priced) = Resolve([Provider(keys: ["ZAI_API_KEY", "ZAI_API_KEY_TWO"])], balanceKeyUsage: true);
        Assert.Empty(priced);

        var (_, off) = Resolve([unpriced], balanceKeyUsage: false);
        Assert.Empty(off);

        var (_, leftOut) = Resolve([unpriced]);
        Assert.Empty(leftOut);
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

        Exactly(problems, @"providers 1 and 2 are both named 'zai': a provider's name is listed once");

        var (_, appointed) = Resolve(
        [
            Provider(name: "zai"),
            Provider(name: "zai-two", models: [Model(code: "other")]),
        ],
        tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3", "other"] });

        Exactly(appointed, @"providers 'zai' and 'zai-two' both appoint 'ZAI_API_KEY' for a key: a second key is another variable's name");
    }

    /// <summary>A model's code is listed once, across every provider.</summary>
    [Fact]
    public void AModelsCodeIsListedOnce_AcrossEveryProvider()
    {
        var (_, problems) = Resolve(
        [
            Provider(name: "zai"),
            Provider(name: "zai-two", key: "ZAI_API_KEY_TWO", models: [Model(code: "glm-5.3")]),
        ],
        tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] });

        Exactly(problems, @"model 'glm-5.3' is listed by providers 'zai' and 'zai-two': a model's code is listed once, across every provider");

        // Listed twice by one provider, it is said of that provider, never as two providers of one name.
        var (_, byOne) = Resolve([Provider(models: [Model(), Model()])]);
        Exactly(byOne, @"provider 'zai' lists model 'glm-5.3' twice: a model's code is listed once, across every provider");
    }

    /// <summary>A provider's client must be one the tool builds in and must reach its type; its api must be an absolute https address; its key a variable's name.</summary>
    [Fact]
    public void AProvidersClientApiAndKey_AreEachRefusedWhereTheyDoNotRead()
    {
        var (_, client) = Resolve([Provider(client: "nonexistent")]);
        Exactly(client, @"providers[0] 'zai' says client 'nonexistent', which is not declared; a client is one the tool builds in: 'claude-code', 'claude-api', 'glm'");

        var (_, reach) = Resolve([Provider(client: "claude-api")]);
        Exactly(reach, @"providers[0] 'zai' says client 'claude-api' (claude-api), which does not reach provider type 'zai'");

        var (_, api) = Resolve([Provider(api: "api.z.ai")]);
        Exactly(api, @"providers[0] 'zai' api 'api.z.ai' is no absolute https address; an address is the company's own or a gateway's, and http only for localhost");

        var (_, plain) = Resolve([Provider(api: "http://api.z.ai/api/paas/v4")]);
        Exactly(plain, @"providers[0] 'zai' api 'http://api.z.ai/api/paas/v4' is no absolute https address; an address is the company's own or a gateway's, and http only for localhost");

        var (_, local) = Resolve([Provider(api: "http://localhost:8080/v4")]);
        Assert.Empty(local);

        var (_, key) = Resolve([Provider(key: "zai-key")]);
        Exactly(key, @"providers[0] 'zai' apiKeyNames 'zai-key' is not a variable's name: [A-Z][A-Z0-9_]*, a name and never a key");

        // A provider with no models leaves its tier's list naming a code nobody lists, and both are said.
        var (_, none) = Resolve([Provider(models: [])]);
        Exactly(
            none,
            @"providers[0] 'zai' declares no models; a provider is the models it lists, and it lists at least one",
            @"tierConfig.balanced lists 'glm-5.3', which no provider lists");
    }

    /// <summary>A model's entry is refused where its efforts name a level its client cannot say, its sizes are not positive whole numbers, or its output is over its context.</summary>
    [Fact]
    public void AModelsEffortsAndSizes_AreRefusedWhereTheyDoNotRead()
    {
        var (_, efforts) = Resolve([Provider(models: [Model(efforts: ["low", "turbo"])])]);
        Exactly(efforts, @"providers[0] 'zai' model 'glm-5.3' takes effort 'turbo', which its client 'glm' cannot say. It can: low, medium, high, xhigh, max");

        var (_, none) = Resolve([Provider(models: [Model(efforts: [])])]);
        Exactly(none, @"providers[0] 'zai' model 'glm-5.3' takes no efforts; a model's entry names the efforts it takes, each one of its client's levels");

        var (_, context) = Resolve([Provider(models: [Model(context: 0)])]);
        Exactly(context, @"providers[0] 'zai' model 'glm-5.3' contextTokens 0 is not a positive whole number");

        var (_, output) = Resolve([Provider(models: [Model(output: 2_000_000)])]);
        Exactly(output, @"providers[0] 'zai' model 'glm-5.3' maxOutputTokens 2000000 is over its contextTokens 1000000; the most a reply holds is at most what the context holds");
    }

    /// <summary>A model's tiers and a tier's list must agree, each way, and a tier holds at least one model each once.</summary>
    [Fact]
    public void AModelsTiersAndATiersList_AgreeEachWay()
    {
        // A tier's list that leaves out a model its models serve, and a list that is empty: each is refused, each way.
        var (_, disagreeing) = Resolve(tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = [] });
        Exactly(
            disagreeing,
            @"tierConfig.balanced is empty; a tier's list is its models in the order they are tried, and it holds at least one",
            @"model 'glm-5.3' serves tier 'balanced', and tierConfig.balanced does not list it: put it in the list where it is to be tried");

        // A tier tierConfig does not define is said so once: there is no list of it to put the model in.
        var (_, deep) = Resolve([Provider(models: [Model(tiers: ["deep"])])]);
        Exactly(
            deep,
            @"providers[0] 'zai' model 'glm-5.3' serves tier 'deep', which tierConfig does not define. Defined: balanced",
            @"tierConfig.balanced lists 'glm-5.3', whose tiers are deep: add balanced to its tiers, or take it out of the list");

        var (_, unlisted) = Resolve(tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3", "other"] });
        Exactly(unlisted, @"tierConfig.balanced lists 'other', which no provider lists");

        var (_, twice) = Resolve(tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3", "glm-5.3"] });
        Exactly(twice, @"tierConfig.balanced lists 'glm-5.3' twice");
    }

    /// <summary>
    /// A tier's name is one name, exact in its case everywhere: a tierConfig key is held to the orchestration name rule
    /// - lower-case - and a model's tiers name it as it is defined, so 'Best' is no spelling of 'best' and is said so.
    /// </summary>
    [Fact]
    public void ATiersName_IsHeldToTheNameRule_AndComparedExactly()
    {
        var (_, capital) = Resolve(
            [Provider(models: [Model(tiers: ["Best"])])],
            tiers: new(StringComparer.OrdinalIgnoreCase) { ["Best"] = ["glm-5.3"] },
            agentTier: "Best");
        Exactly(capital, @"tierConfig 'Best' is not named by the orchestration name rule: 'Best' is not a valid name. Use lowercase letters and digits separated by single hyphens, for example 'fix-auth'.");

        var (_, cased) = Resolve([Provider(models: [Model(tiers: ["Balanced"])])]);
        Exactly(
            cased,
            @"providers[0] 'zai' model 'glm-5.3' serves tier 'Balanced', which tierConfig does not define. Defined: balanced",
            @"tierConfig.balanced lists 'glm-5.3', whose tiers are Balanced: add balanced to its tiers, or take it out of the list");

        // A step's tier is read the same way: a tier of another case is no tier tierConfig defines.
        var (resolved, _) = Resolve();
        Assert.Empty(resolved!.ModelsOf("Balanced"));
        Assert.Equal(["glm-5.3"], resolved.ModelsOf("balanced").Select(entry => entry.Model.Code));
    }

    /// <summary>
    /// A provider's name, a model's code and a model's name are required, and a provider's name is held to the
    /// orchestration name rule; a code is what is sent to the API, one word with no space in it.
    /// </summary>
    [Fact]
    public void ANameACodeAndAModelsName_AreRequired_EachByItsRule()
    {
        var (_, noName) = Resolve([Provider(name: "")]);
        Exactly(noName, @"providers[0] has no name; a provider is named once in providers, by the orchestration name rule");

        var (_, badName) = Resolve([Provider(name: "Z AI")]);
        Exactly(badName, @"providers[0] 'Z AI' is not named by the orchestration name rule: 'Z AI' is not a valid name. Use lowercase letters and digits separated by single hyphens, for example 'fix-auth'.");

        var (_, noCode) = Resolve([Provider(models: [Model(code: "")])], tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = [""] });
        Exactly(
            noCode,
            @"providers[0] 'zai' model #1 has no code; a model's code is what is sent to the API, and how everything names it");

        var (_, spaced) = Resolve([Provider(models: [Model(code: "glm 5.3")])], tiers: new(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm 5.3"] });
        Exactly(spaced, @"providers[0] 'zai' model 'glm 5.3' code holds a space; a model's code is what is sent to the API, one word");

        var (_, noModelName) = Resolve([Provider(models: [Model(name: " ")])]);
        Exactly(noModelName, @"providers[0] 'zai' model 'glm-5.3' has no name; a model's name is the one a person reads, shown beside its code");
    }

    /// <summary>A price below zero, one half of priceInputMiss and priceOutput, a largePrompt with no overTokens, and a price with no currency or readOn: each refused.</summary>
    [Fact]
    public void PricesThatDoNotRead_AreRefusedNamingThem()
    {
        var (_, negative) = Resolve([Provider(models: [Model(miss: -1)])]);
        Exactly(negative, @"providers[0] 'zai' model 'glm-5.3' priceInputMiss -1 is below zero");

        var (_, half) = Resolve([Provider(models: [Model(outputPrice: null)])]);
        Exactly(half, @"providers[0] 'zai' model 'glm-5.3' names one of priceInputMiss and priceOutput and not the other; the two are written together or not at all");

        var (_, noCurrency) = Resolve([Provider(currency: "")]);
        Exactly(noCurrency, @"providers[0] 'zai' holds prices and names no currency; a provider says once the currency of every price of its models, three capital letters");

        var (_, noReadOn) = Resolve([Provider(readOn: "")]);
        Exactly(noReadOn, @"providers[0] 'zai' holds prices and names no readOn; the date its numbers were read (YYYY-MM-DD) is shown wherever a price is, so an old number is not taken for today's");

        var (_, badDate) = Resolve([Provider(readOn: "yesterday")]);
        Exactly(badDate, @"providers[0] 'zai' readOn 'yesterday' is no date; the date its numbers were read is YYYY-MM-DD");

        // A date in another form is no date of the form the refusal names: one form, read the same on every machine.
        var (_, otherForm) = Resolve([Provider(readOn: "10/12/2026")]);
        Exactly(otherForm, @"providers[0] 'zai' readOn '10/12/2026' is no date; the date its numbers were read is YYYY-MM-DD");

        var (_, badCurrency) = Resolve([Provider(currency: "dollars")]);
        Exactly(badCurrency, @"providers[0] 'zai' currency 'dollars' is not three capital letters");

        var (_, noOver) = Resolve([Provider(models: [Large(new LargePromptPrices { OverTokens = 0, PriceInputMiss = 2, PriceOutput = 8 })])]);
        Exactly(noOver, @"providers[0] 'zai' model 'glm-5.3' largePrompt.overTokens 0 is not a positive whole number");

        // A large-prompt set is held to the flat set's rules: its input and output priced together, no price below
        // zero, and none where the provider names no currency or no readOn - though the model names no flat price.
        var (_, halfLarge) = Resolve([Provider(models: [Large(new LargePromptPrices { OverTokens = 100_000, PriceInputMiss = 2 })])]);
        Exactly(halfLarge, @"providers[0] 'zai' model 'glm-5.3' largePrompt names one of priceInputMiss and priceOutput and not the other; the two are written together or not at all");

        var (_, negativeLarge) = Resolve([Provider(models: [Large(new LargePromptPrices { OverTokens = 100_000, PriceInputMiss = 2, PriceOutput = 8, PriceCacheWrite1h = -1 })])]);
        Exactly(negativeLarge, @"providers[0] 'zai' model 'glm-5.3' largePrompt.priceCacheWrite1h -1 is below zero");

        var (_, largeAlone) = Resolve([Provider(currency: "", readOn: "", models: [Large(new LargePromptPrices { OverTokens = 100_000, PriceInputMiss = 2, PriceOutput = 8 }, flat: false)])]);
        Exactly(
            largeAlone,
            @"providers[0] 'zai' holds prices and names no currency; a provider says once the currency of every price of its models, three capital letters",
            @"providers[0] 'zai' holds prices and names no readOn; the date its numbers were read (YYYY-MM-DD) is shown wherever a price is, so an old number is not taken for today's");
    }

    /// <summary>GLM's entry with a large-prompt set, its flat prices with it or none.</summary>
    private static AgentModelConfig Large(LargePromptPrices large, bool flat = true)
        => new()
        {
            Code = "glm-5.3",
            Name = "GLM 5.3",
            Tiers = ["balanced"],
            Efforts = ["low", "high", "max"],
            ContextTokens = 1_000_000,
            MaxOutputTokens = 131_072,
            PriceInputMiss = flat ? 1.4 : null,
            PriceInputHit = flat ? 0.26 : null,
            PriceOutput = flat ? 4.4 : null,
            LargePrompt = large,
        };

    /// <summary>A price no model holds but the provider's own - a thousand searches - is in its currency too.</summary>
    [Fact]
    public void AProvidersSearchPrice_NeedsACurrencyAndADate()
    {
        var unpriced = Provider(currency: "", readOn: "", models: [Model(miss: null, outputPrice: null)]);
        var withSearches = new AgentProviderConfig
        {
            Name = unpriced.Name,
            Type = unpriced.Type,
            Api = unpriced.Api,
            ApiKeyNames = unpriced.ApiKeyNames,
            Client = unpriced.Client,
            Models = [new AgentModelConfig { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["balanced"], Efforts = ["low", "high", "max"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 }],
            PricePerThousandSearches = 10,
        };

        var (_, problems) = Resolve([withSearches]);
        Exactly(
            problems,
            @"providers[0] 'zai' holds prices and names no currency; a provider says once the currency of every price of its models, three capital letters",
            @"providers[0] 'zai' holds prices and names no readOn; the date its numbers were read (YYYY-MM-DD) is shown wherever a price is, so an old number is not taken for today's");
    }

    /// <summary>tierConfig without providers is refused: a tier names models, and models are a provider's.</summary>
    [Fact]
    public void TierConfigWithoutProviders_IsRefused()
    {
        var (_, problems) = AgentProviders.Resolve(new HarnessConfig
        {
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
        });

        Exactly(
            problems,
            @"tierConfig is declared and providers is not: a tier names models, and models are a provider's. Declare providers with its models, or take tierConfig out",
            @"tierConfig.balanced lists 'glm-5.3', which no provider lists");
    }

    /// <summary>
    /// The problems are exactly these sentences, whole and in order: nothing else is said beside them, so a doubled or a
    /// false sentence fails as a missing one does.
    /// </summary>
    private static void Exactly(IReadOnlyList<string> problems, params string[] sentences)
        => Assert.Equal(sentences, problems);
}