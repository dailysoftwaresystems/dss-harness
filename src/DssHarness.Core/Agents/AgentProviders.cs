using System.Globalization;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Agents;

/// <summary>
/// A client kind the tool builds in: its name under <c>agentClients</c>, the provider types it reaches, the effort
/// levels its protocol can say, how it signs in, and the settings of the slot it reads. A client names no model -
/// which model, at what effort, where and with whose key is said by the step, <c>tierConfig</c> and the provider.
/// </summary>
/// <param name="Name">The client's name, as a provider's <c>client</c> names it.</param>
/// <param name="ProviderType">The provider type it reaches: the one kind of API it speaks.</param>
/// <param name="Efforts">The effort levels its protocol can say; a model's <c>efforts</c> are one of these.</param>
/// <param name="SignsInWithLogin">
/// Whether it signs in with the machine's login and reads no key - <c>claude-code</c>, whose sign-in no configuration
/// changes in this build (part 03's <c>auth</c> is a later key of <c>agentClients</c>) - rather than signing each
/// request with its provider's key.
/// </param>
/// <param name="Settings">The names of the slot it reads that hold no key - a setting, such as the program it starts - each with what its value is.</param>
public sealed record AgentClientKind(
    string Name,
    string ProviderType,
    IReadOnlyList<string> Efforts,
    bool SignsInWithLogin,
    IReadOnlyList<(string Name, string What)> Settings);

/// <summary>
/// The configuration's providers and tiers, resolved: the way from a model's code to its provider, its address, its
/// key's name and its client, and from a tier to its models in the order they are tried. Nothing of a model is in the
/// tool's code - a config that declares no <c>providers</c> has no model - and the validation here is pure: whether
/// the slot holds an appointed name is checked when a run needs the value, so a machine without a key still reads its
/// config.
/// </summary>
public sealed partial class AgentProviders
{
    /// <summary>The client kinds the tool builds in, each naming no model.</summary>
    public static readonly IReadOnlyList<AgentClientKind> ClientKinds =
    [
        new(
            "claude-code",
            "anthropic",
            ["low", "medium", "high", "xhigh", "max"],
            SignsInWithLogin: true,
            [(ClaudeCode.ClaudeCodeUpdateService.ExecutableVariable, "the program the claude-code client starts in place of npm's Claude Code")]),
        new("claude-api", "anthropic", ["low", "medium", "high", "xhigh", "max"], SignsInWithLogin: false, []),
        new("glm", "zai", ["low", "high", "max"], SignsInWithLogin: false, []),
    ];

    private readonly Dictionary<string, (AgentProviderConfig Provider, AgentModelConfig Model)> _models = new(StringComparer.Ordinal);

    private AgentProviders(HarnessConfig config)
    {
        Config = config;

        foreach (var provider in config.Providers)
        {
            foreach (var model in provider.Models)
            {
                _models[model.Code] = (provider, model);
            }
        }
    }

    private HarnessConfig Config { get; }

    /// <summary>Every provider the configuration declares, in the order it lists them.</summary>
    public IReadOnlyList<AgentProviderConfig> Providers => Config.Providers;

    /// <summary>Every tier the configuration defines, in no order in particular.</summary>
    public IReadOnlyList<string> Tiers => [.. Config.TierConfig.Keys];

    /// <summary>The tier of a step that names neither a tier nor a model; null where the configuration names none.</summary>
    public string? DefaultTier => Config.Defaults.AgentTier;

    /// <summary>Whether the configuration holds neither providers nor tiers: a config with no model, which a step that needs one is told about.</summary>
    public bool HasNoProvidersAtAll => Config.Providers.Count == 0 && Config.TierConfig.Count == 0;

    /// <summary>
    /// Resolves the configuration's providers and tiers, answering every problem the sections hold: each is refused
    /// where the file is read, so a config that loads is one the tool can use. An empty answer with a non-null
    /// providers is a config that holds none of the sections, which is itself a valid config - it simply has no model.
    /// </summary>
    /// <param name="config">The configuration, as it was read.</param>
    /// <returns>The resolved providers, and every problem found, in the order the sections declare what they concern.</returns>
    public static (AgentProviders? Providers, IReadOnlyList<string> Problems) Resolve(HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var problems = new List<string>();

        if (config.Providers.Count == 0 && config.TierConfig.Count > 0)
        {
            problems.Add("tierConfig is declared and providers is not: a tier names models, and models are a provider's. Declare providers with its models, or take tierConfig out");
        }

        var providers = config.Providers;

        foreach (var (provider, at) in providers.Select((entry, at) => (entry, at)))
        {
            var where = $"providers[{at.ToString(CultureInfo.InvariantCulture)}] '{provider.Name}'";

            if (provider.Models.Count == 0)
            {
                problems.Add($"{where} declares no models; a provider is the models it lists, and it lists at least one");
            }
        }

        // A provider's name is listed once; a model's code is listed once, across every provider; a key's variable is
        // appointed once - for a second key of a company is a second name in its provider's apiKeyNames.
        foreach (var twice in providers.GroupBy(provider => provider.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var ats = providers.Select((provider, at) => (provider, at)).Where(entry => string.Equals(entry.provider.Name, twice.Key, StringComparison.Ordinal)).Select(entry => entry.at + 1);
            problems.Add($"providers {string.Join(" and ", ats.Select(entry => entry.ToString(CultureInfo.InvariantCulture)))} are both named '{twice.Key}': a provider's name is listed once");
        }

        var resolved = new AgentProviders(config);

        foreach (var twice in providers.SelectMany(provider => provider.Models.Select(model => (provider, model))).GroupBy(entry => entry.model.Code, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            problems.Add($"model '{twice.Key}' is listed by providers '{string.Join("' and '", twice.Select(entry => entry.provider.Name))}': a model's code is listed once, across every provider");
        }

        foreach (var provider in providers)
        {
            foreach (var twice in provider.ApiKeyNames.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                problems.Add($"provider '{provider.Name}' lists '{twice.Key}' twice in apiKeyNames: each key is listed once");
            }
        }

        foreach (var twice in providers.SelectMany(provider => provider.ApiKeyNames.Select(name => (provider, name))).GroupBy(entry => entry.name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            problems.Add($"providers '{string.Join("' and '", twice.Select(entry => entry.provider.Name))}' both appoint '{twice.Key}' for a key: a second key is another variable's name");
        }

        // A client is one the tool builds in, its kind; the keys of one entry are read by the slices that build what
        // they say, and an unknown key of the section is refused where the file is read, as of every section. A key of
        // one client alone is refused on the others, so a key that loads is a key that is read.
        foreach (var client in config.AgentClients.Keys.Where(name => ClientKinds.All(kind => !string.Equals(kind.Name, name, StringComparison.Ordinal))))
        {
            problems.Add($"agentClients '{client}' is not a client this tool builds in: {string.Join(", ", ClientKinds.Select(kind => $"'{kind.Name}'"))}");
        }

        foreach (var (client, entry) in config.AgentClients.Where(entry => entry.Value.Update is not null && entry.Key != "claude-code"))
        {
            problems.Add($"agentClients '{client}' names update, a key of the claude-code client alone: whether the tool installs Claude Code with npm and brings one up to date");
        }

        foreach (var (provider, at) in providers.Select((entry, at) => (entry, at)))
        {
            var where = $"providers[{at.ToString(CultureInfo.InvariantCulture)}] '{provider.Name}'";

            if (KindOf(provider.Client) is not { } client)
            {
                problems.Add($"{where} says client '{provider.Client}', which is not declared; a client is one the tool builds in: {string.Join(", ", ClientKinds.Select(kind => $"'{kind.Name}'"))}");
            }
            else if (!Reaches(client.Name, provider))
            {
                problems.Add($"{where} says client '{provider.Client}' ({client.Name}), which does not reach provider type '{provider.Type}'");
            }

            if (!Uri.TryCreate(provider.Api, UriKind.Absolute, out var api) || (api.Scheme != "https" && !string.Equals(api.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{where} api '{provider.Api}' is no absolute https address; an address is the company's own or a gateway's, and http only for localhost");
            }

            if (provider.ApiKeyNames.Count == 0)
            {
                problems.Add($"{where} declares no apiKeyNames; a provider's keys are at least one variable's name, in the order they are tried");
            }

            foreach (var name in provider.ApiKeyNames.Where(name => !Secrets.AgentKeyStore.IsVariableName(name)))
            {
                problems.Add($"{where} apiKeyNames '{name}' is not a variable's name: {Secrets.AgentKeyStore.VariableNameShape}, a name and never a key");
            }

            if (provider.Models.Any(model => Priced(model)) && string.IsNullOrWhiteSpace(provider.Currency))
            {
                problems.Add($"{where} holds prices and names no currency; a provider says once the currency of every price of its models, three capital letters");
            }

            if (provider.Models.Any(model => Priced(model)) && string.IsNullOrWhiteSpace(provider.ReadOn))
            {
                problems.Add($"{where} holds prices and names no readOn; the date its numbers were read (YYYY-MM-DD) is shown wherever a price is, so an old number is not taken for today's");
            }

            if (provider.ReadOn is { Length: > 0 } read && !DateOnly.TryParse(read, CultureInfo.InvariantCulture, out _))
            {
                problems.Add($"{where} readOn '{read}' is no date; the date its numbers were read is YYYY-MM-DD");
            }

            if (provider.Currency is { Length: > 0 } currency && !CurrencyName().IsMatch(currency))
            {
                problems.Add($"{where} currency '{currency}' is not three capital letters");
            }

            foreach (var model in provider.Models)
            {
                ModelRules(config, provider, model, $"{where} model '{model.Code}'", problems);
            }
        }

        TierRules(config, resolved, problems);
        BalanceRules(config, problems);

        return (problems.Count == 0 ? resolved : null, problems);
    }

    /// <summary>
    /// balanceKeyUsage balances a provider's keys by what each spent, so a model with no price could not be counted:
    /// refused where the switch is on and a provider with two keys or more lists one. A provider with one key has
    /// nothing to balance and is not refused.
    /// </summary>
    private static void BalanceRules(HarnessConfig config, List<string> problems)
    {
        if (config.BalanceKeyUsage is not true)
        {
            return;
        }

        foreach (var provider in config.Providers.Where(provider => provider.ApiKeyNames.Count >= 2))
        {
            foreach (var model in provider.Models.Where(model => !Priced(model)))
            {
                problems.Add(
                    $"balanceKeyUsage balances the keys of '{provider.Name}' by what each spent, and '{model.Code}' has no price "
                    + $"(providers '{provider.Name}', model '{model.Code}': priceInputMiss, priceOutput). Price it, or turn balanceKeyUsage off");
            }
        }
    }

    /// <summary>The provider that lists <paramref name="code"/>, with the model's entry; null where no provider lists it.</summary>
    public (AgentProviderConfig Provider, AgentModelConfig Model)? Of(string code)
        => _models.TryGetValue(code, out var entry) ? entry : null;

    /// <summary>
    /// Whether <paramref name="client"/> signs in with the machine's login and reads no key: its kind's sign-in, which
    /// no configuration changes in this build. A client the tool does not build in reads nothing either; it is refused
    /// where the configuration or the definition names it.
    /// </summary>
    /// <param name="client">The client's name.</param>
    public bool SignsInWithLogin(string client)
        => KindOf(client) is { SignsInWithLogin: true };

    /// <summary>
    /// The names a model's key is read under on the client that reaches it - <paramref name="client"/> where the step
    /// names one, else the provider's own - in the order they are tried: the provider's <c>apiKeyNames</c> on a client
    /// that signs each request with its provider's key (<c>claude-api</c>, <c>glm</c>), and none on one that signs in
    /// with the machine's login (<c>claude-code</c>). The one place that says what a step's model needs of the slot:
    /// <c>validate-workflow</c> asks it of every step, and so will the run that refuses a missing key.
    /// </summary>
    /// <param name="provider">The provider that lists the model.</param>
    /// <param name="client">The client the step names, or null where it names none.</param>
    public IReadOnlyList<string> KeysRead(AgentProviderConfig provider, string? client = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return SignsInWithLogin(client ?? provider.Client) ? [] : provider.ApiKeyNames;
    }

    /// <summary>
    /// Every name the configuration appoints a value of the slot for, of providers and clients both, each with who
    /// appoints it, what its value is and whether it is a secret: each provider's keys, in its order, and the settings a
    /// built-in client reads where a provider of the type it reaches is declared - the program the claude-code client
    /// starts among them. What the slot holds beyond these is appointed by nothing, which a listing and a missing key's
    /// refusal say, where a misspelt name shows.
    /// </summary>
    public IReadOnlyList<Secrets.AppointedKey> Appointed()
    {
        var appointed = new List<Secrets.AppointedKey>();

        foreach (var provider in Config.Providers)
        {
            appointed.AddRange(provider.ApiKeyNames.Select(name => new Secrets.AppointedKey(name, $"provider '{provider.Name}'", Secrets.AgentKeyStore.WhatHolds(provider))));
        }

        foreach (var kind in ClientKinds.Where(kind => Config.Providers.Any(provider => string.Equals(provider.Type, kind.ProviderType, StringComparison.Ordinal))))
        {
            appointed.AddRange(kind.Settings.Select(setting => new Secrets.AppointedKey(setting.Name, $"client '{kind.Name}'", setting.What, Secret: false)));
        }

        return appointed;
    }

    /// <summary>Whether <paramref name="client"/> is one the tool builds in and reaches <paramref name="provider"/>'s type: the one rule of reach, for a provider's own client and a step's alike.</summary>
    /// <param name="client">The client's name.</param>
    /// <param name="provider">The provider it would reach.</param>
    public static bool Reaches(string client, AgentProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return KindOf(client) is { } kind && string.Equals(kind.ProviderType, provider.Type, StringComparison.Ordinal);
    }

    /// <summary>The client kind the tool builds in under <paramref name="client"/>; null where it builds in none.</summary>
    private static AgentClientKind? KindOf(string client)
        => ClientKinds.FirstOrDefault(kind => string.Equals(kind.Name, client, StringComparison.Ordinal));

    /// <summary>Every model of <paramref name="tier"/>, in the order <c>tierConfig</c> says they are tried; empty where the tier is not defined.</summary>
    public IReadOnlyList<(AgentProviderConfig Provider, AgentModelConfig Model)> ModelsOf(string tier)
    {
        if (!Config.TierConfig.TryGetValue(tier, out var codes))
        {
            return [];
        }

        var models = new List<(AgentProviderConfig Provider, AgentModelConfig Model)>();

        foreach (var code in codes)
        {
            if (_models.GetValueOrDefault(code) is { } entry)
            {
                models.Add(entry);
            }
        }

        return models;
    }

    /// <summary>Whether a model's entry holds any price at all.</summary>
    private static bool Priced(AgentModelConfig model)
        => model.PriceInputMiss is not null || model.PriceOutput is not null;

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z]{3}$")]
    private static partial System.Text.RegularExpressions.Regex CurrencyName();

    private static void ModelRules(HarnessConfig config, AgentProviderConfig provider, AgentModelConfig model, string where, List<string> problems)
    {
        // 'all' is the word a step's effort uses for every model it does not name (part 04), so no model may take it
        // as its code: a step could never name it and every map would mean two things.
        if (string.Equals(model.Code, "all", StringComparison.Ordinal))
        {
            problems.Add("'all' cannot be a model's code: a step's effort uses it for every model it does not name");
        }

        if (model.Efforts.Count == 0)
        {
            problems.Add($"{where} takes no efforts; a model's entry names the efforts it takes, each one of its client's levels");
        }

        if (KindOf(provider.Client) is { } client)
        {
            foreach (var level in model.Efforts.Where(level => !client.Efforts.Contains(level, StringComparer.Ordinal)))
            {
                problems.Add($"{where} takes effort '{level}', which its client '{client.Name}' cannot say. It can: {string.Join(", ", client.Efforts)}");
            }
        }

        if (model.ContextTokens <= 0)
        {
            problems.Add($"{where} contextTokens {model.ContextTokens.ToString(CultureInfo.InvariantCulture)} is not a positive whole number");
        }

        if (model.MaxOutputTokens <= 0)
        {
            problems.Add($"{where} maxOutputTokens {model.MaxOutputTokens.ToString(CultureInfo.InvariantCulture)} is not a positive whole number");
        }

        if (model.MaxOutputTokens > model.ContextTokens)
        {
            problems.Add($"{where} maxOutputTokens {model.MaxOutputTokens.ToString(CultureInfo.InvariantCulture)} is over its contextTokens {model.ContextTokens.ToString(CultureInfo.InvariantCulture)}; the most a reply holds is at most what the context holds");
        }

        foreach (var tier in model.Tiers.Where(tier => !config.TierConfig.ContainsKey(tier)))
        {
            problems.Add($"{where} serves tier '{tier}', which tierConfig does not define. Defined: {string.Join(", ", config.TierConfig.Keys.OrderBy(key => key, StringComparer.Ordinal))}");
        }

        foreach (var price in PricesOf(model).Where(price => price.Value < 0))
        {
            problems.Add($"{where} {price.Key} {price.Value.ToString(CultureInfo.InvariantCulture)} is below zero");
        }

        if (model.PriceInputMiss is null != (model.PriceOutput is null))
        {
            problems.Add($"{where} names one of priceInputMiss and priceOutput and not the other; the two are written together or not at all");
        }

        if (model.LargePrompt is { } large && large.OverTokens <= 0)
        {
            problems.Add($"{where} largePrompt.overTokens {large.OverTokens.ToString(CultureInfo.InvariantCulture)} is not a positive whole number");
        }

        foreach (var price in PricesOf(model).Where(price => price.Value >= 0 && price.Key.StartsWith("price", StringComparison.Ordinal)))
        {
            if (string.IsNullOrWhiteSpace(provider.Currency) || string.IsNullOrWhiteSpace(provider.ReadOn))
            {
                problems.Add($"{where} names {price.Key} and its provider names no currency or no readOn; a price is said once for the provider, with the date its numbers were read");
                break;
            }
        }
    }

    /// <summary>Every price a model's entry names, with its value.</summary>
    internal static IEnumerable<KeyValuePair<string, double>> PricesOf(AgentModelConfig model)
    {
        if (model.PriceInputMiss is { } miss)
        {
            yield return new("priceInputMiss", miss);
        }

        if (model.PriceInputHit is { } hit)
        {
            yield return new("priceInputHit", hit);
        }

        if (model.PriceOutput is { } output)
        {
            yield return new("priceOutput", output);
        }

        if (model.PriceCacheWrite is { } write)
        {
            yield return new("priceCacheWrite", write);
        }

        if (model.PriceCacheWrite1h is { } write1h)
        {
            yield return new("priceCacheWrite1h", write1h);
        }

        if (model.LargePrompt is { } large)
        {
            foreach (var price in LargePromptPricesOf(large))
            {
                yield return price;
            }
        }
    }

    /// <summary>Every price a large-prompt entry names, with its value, each named as the flat one it stands in for.</summary>
    private static IEnumerable<KeyValuePair<string, double>> LargePromptPricesOf(LargePromptPrices large)
    {
        if (large.PriceInputMiss is { } miss)
        {
            yield return new("largePrompt.priceInputMiss", miss);
        }

        if (large.PriceInputHit is { } hit)
        {
            yield return new("largePrompt.priceInputHit", hit);
        }

        if (large.PriceOutput is { } output)
        {
            yield return new("largePrompt.priceOutput", output);
        }

        if (large.PriceCacheWrite is { } write)
        {
            yield return new("largePrompt.priceCacheWrite", write);
        }

        if (large.PriceCacheWrite1h is { } write1h)
        {
            yield return new("largePrompt.priceCacheWrite1h", write1h);
        }
    }

    private static void TierRules(HarnessConfig config, AgentProviders resolved, List<string> problems)
    {
        foreach (var (tier, codes) in config.TierConfig)
        {
            if (codes.Count == 0)
            {
                problems.Add($"tierConfig.{tier} is empty; a tier's list is its models in the order they are tried, and it holds at least one");
            }

            foreach (var twice in codes.GroupBy(code => code, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                problems.Add($"tierConfig.{tier} lists '{twice.Key}' twice");
            }

            foreach (var code in codes)
            {
                if (!resolved._models.TryGetValue(code, out var entry))
                {
                    problems.Add($"tierConfig.{tier} lists '{code}', which no provider lists");
                }
                else if (!entry.Model.Tiers.Contains(tier, StringComparer.Ordinal))
                {
                    problems.Add($"tierConfig.{tier} lists '{code}', whose tiers are {string.Join(", ", entry.Model.Tiers)}: add {tier} to its tiers, or take it out of the list");
                }
            }
        }

        foreach (var (code, entry) in resolved._models)
        {
            foreach (var tier in entry.Model.Tiers.Where(tier => !config.TierConfig.TryGetValue(tier, out var codes) || !codes.Contains(code, StringComparer.Ordinal)))
            {
                problems.Add($"model '{code}' serves tier '{tier}', and tierConfig.{tier} does not list it: put it in the list where it is to be tried");
            }
        }
    }
}
