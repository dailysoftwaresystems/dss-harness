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
/// <param name="SignsIn">
/// Whether it signs in, by its entry's <c>auth</c> - <c>claude-code</c>, the machine's login unless its entry says
/// otherwise - rather than signing each request with its provider's key, as an API client does.
/// </param>
/// <param name="Settings">The names of the slot it reads that hold no key - a setting, such as the program it starts - each by the entry's key that renames it.</param>
public sealed record AgentClientKind(
    string Name,
    string ProviderType,
    IReadOnlyList<string> Efforts,
    bool SignsIn,
    IReadOnlyList<AgentClientSetting> Settings);

/// <summary>
/// A setting a client reads from the slot - a value that is no key, such as the program it starts - under the name its
/// entry's key gives, or its own where the entry says none.
/// </summary>
/// <param name="Key">The entry's key that names the slot's variable: <c>executable</c>, <c>configDir</c>.</param>
/// <param name="DefaultName">The variable read where the entry names none.</param>
/// <param name="What">What its value is, as a refusal and the slot's checks say it.</param>
public sealed record AgentClientSetting(string Key, string DefaultName, string What)
{
    /// <summary>The program a claude-code client starts in place of npm's Claude Code.</summary>
    public const string ExecutableKey = "executable";

    /// <summary>The Claude Code directory a claude-code client signs in from and reads the user's settings in.</summary>
    public const string ConfigDirKey = "configDir";

    /// <summary>The variable this setting is read under for <paramref name="entry"/>: the name its key gives, else the default.</summary>
    /// <param name="entry">The client's entry; null where the configuration declares none.</param>
    public string NameIn(AgentClientConfig? entry) => Named(entry) ?? DefaultName;

    /// <summary>The name <paramref name="entry"/>'s key gives this setting; null where it gives none.</summary>
    /// <param name="entry">The client's entry; null where the configuration declares none.</param>
    public string? Named(AgentClientConfig? entry)
        => Key switch
        {
            ExecutableKey => entry?.Executable,
            ConfigDirKey => entry?.ConfigDir,
            _ => throw new InvalidOperationException($"'{Key}' is no setting key of a client."),
        };
}

/// <summary>
/// How a client that signs in does (<c>agentClients.&lt;client&gt;.auth</c>), and the one place each way is named: the
/// machine's login, a key of the provider's, or a token the slot holds.
/// </summary>
public static class AgentAuth
{
    /// <summary>The machine's login: no key is read, and none is given.</summary>
    public const string Login = "login";

    /// <summary>A key of the provider's <c>apiKeyNames</c>, tried in their order.</summary>
    public const string ApiKey = "apiKey";

    /// <summary>An OAuth token the slot holds, under the entry's <c>token</c>.</summary>
    public const string OAuthToken = "oauthToken";

    /// <summary>An authentication token the slot holds, under the entry's <c>token</c>.</summary>
    public const string AuthToken = "authToken";

    /// <summary>Every way a client signs in, in the order help names them.</summary>
    public static IReadOnlyList<string> All { get; } = [Login, ApiKey, OAuthToken, AuthToken];

    /// <summary>
    /// The variable a token is held under where the entry names none, and the one Claude Code reads it from:
    /// <c>CLAUDE_CODE_OAUTH_TOKEN</c> for an OAuth token, <c>ANTHROPIC_AUTH_TOKEN</c> for an authentication token; null
    /// for a way that reads no token.
    /// </summary>
    /// <param name="auth">The way it signs in.</param>
    public static string? TokenVariable(string auth)
        => auth switch
        {
            OAuthToken => "CLAUDE_CODE_OAUTH_TOKEN",
            AuthToken => "ANTHROPIC_AUTH_TOKEN",
            _ => null,
        };

    /// <summary>How a client that signs in this way signs in, as the listing and a refusal say it.</summary>
    /// <param name="auth">The way it signs in.</param>
    public static string Described(string auth)
        => auth switch
        {
            Login => "the machine's login",
            ApiKey => "a key of its provider's apiKeyNames",
            _ => $"the token of auth: {auth}",
        };
}

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
            ClaudeCode.ClaudeCodeClient.ClientName,
            "anthropic",
            ["low", "medium", "high", "xhigh", "max"],
            SignsIn: true,
            [
                new(AgentClientSetting.ExecutableKey, ClaudeCode.ClaudeCodeUpdateService.ExecutableVariable, "the program the claude-code client starts in place of npm's Claude Code"),
                new(AgentClientSetting.ConfigDirKey, Orchestration.ClaudeTranscripts.ConfigDirectoryVariable, "the Claude Code directory the claude-code client signs in from and reads the user's settings in"),
            ]),
        new(ClaudeApi.ClaudeApiClient.ClientName, "anthropic", ["low", "medium", "high", "xhigh", "max"], SignsIn: false, []),
        new("glm", "zai", ["low", "high", "max"], SignsIn: false, []),
    ];

    private readonly Dictionary<string, (AgentProviderConfig Provider, AgentModelConfig Model)> _models = new(StringComparer.Ordinal);

    /// <summary>
    /// Each tier with its models' codes, by its name exactly: a tier's name is held to the orchestration name rule and
    /// is one name in one case everywhere - <c>tierConfig</c>, a model's <c>tiers</c>, <c>defaults.agentTier</c> and a
    /// step - whatever case the section's reader folds its keys in.
    /// </summary>
    private readonly Dictionary<string, List<string>> _tiers;

    private AgentProviders(HarnessConfig config)
    {
        Config = config;
        _tiers = new Dictionary<string, List<string>>(config.TierConfig, StringComparer.Ordinal);

        foreach (var provider in config.Providers)
        {
            foreach (var model in provider.Models)
            {
                _models.TryAdd(model.Code, (provider, model));
            }
        }
    }

    private HarnessConfig Config { get; }

    /// <summary>Every provider the configuration declares, in the order it lists them.</summary>
    public IReadOnlyList<AgentProviderConfig> Providers => Config.Providers;

    /// <summary>Every tier the configuration defines, in no order in particular.</summary>
    public IReadOnlyList<string> Tiers => [.. _tiers.Keys];

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
            var place = $"providers[{at.ToString(CultureInfo.InvariantCulture)}]";

            // A provider's name is the orchestration name rule's, as a name the tool keeps is.
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                problems.Add($"{place} has no name; a provider is named once in providers, by the orchestration name rule");
            }
            else if (Orchestration.OrchestrationRules.NameProblem(provider.Name) is { } shape)
            {
                problems.Add($"{place} '{provider.Name}' is not named by the orchestration name rule: {shape}");
            }

            if (provider.Models.Count == 0)
            {
                problems.Add($"{place} '{provider.Name}' declares no models; a provider is the models it lists, and it lists at least one");
            }
        }

        // A provider's name is listed once; a model's code is listed once, across every provider; a key's variable is
        // appointed once - for a second key of a company is a second name in its provider's apiKeyNames. Listed twice
        // by one provider, each is said of that provider alone, never as two providers that share it.
        foreach (var twice in providers.GroupBy(provider => provider.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var ats = providers.Select((provider, at) => (provider, at)).Where(entry => string.Equals(entry.provider.Name, twice.Key, StringComparison.Ordinal)).Select(entry => entry.at + 1);
            problems.Add($"providers {string.Join(" and ", ats.Select(entry => entry.ToString(CultureInfo.InvariantCulture)))} are both named '{twice.Key}': a provider's name is listed once");
        }

        var resolved = new AgentProviders(config);

        foreach (var provider in providers)
        {
            foreach (var twice in provider.Models.GroupBy(model => model.Code, StringComparer.Ordinal).Where(group => group.Key.Length > 0 && group.Count() > 1))
            {
                problems.Add($"provider '{provider.Name}' lists model '{twice.Key}' twice: a model's code is listed once, across every provider");
            }
        }

        foreach (var shared in SharedAcross(providers, provider => provider.Models.Select(model => model.Code).Where(code => code.Length > 0)))
        {
            problems.Add($"model '{shared.Name}' is listed by providers '{string.Join("' and '", shared.By.Select(provider => provider.Name))}': a model's code is listed once, across every provider");
        }

        foreach (var provider in providers)
        {
            foreach (var twice in provider.ApiKeyNames.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                problems.Add($"provider '{provider.Name}' lists '{twice.Key}' twice in apiKeyNames: each key is listed once");
            }
        }

        foreach (var shared in SharedAcross(providers, provider => provider.ApiKeyNames))
        {
            problems.Add($"providers '{string.Join("' and '", shared.By.Select(provider => provider.Name))}' both appoint '{shared.Name}' for a key: a second key is another variable's name");
        }

        // A client is one the tool builds in, its kind; the keys of one entry are read by the slices that build what
        // they say, and an unknown key of the section is refused where the file is read, as of every section. A key of
        // one client alone is refused on the others, so a key that loads is a key that is read.
        foreach (var client in config.AgentClients.Keys.Where(name => ClientKinds.All(kind => !string.Equals(kind.Name, name, StringComparison.Ordinal))))
        {
            problems.Add($"agentClients '{client}' is not a client this tool builds in: {string.Join(", ", ClientKinds.Select(kind => $"'{kind.Name}'"))}");
        }

        // The rules of each client's keys read the entries of the clients the tool builds in: an entry of any other name is
        // refused above, whole, and nothing of it is said again.
        List<KeyValuePair<string, AgentClientConfig>> builtIn = [.. config.AgentClients.Where(entry => KindOf(entry.Key) is not null)];

        foreach (var (client, entry) in builtIn.Where(entry => entry.Value.Update is not null && entry.Key != ClaudeCode.ClaudeCodeClient.ClientName))
        {
            problems.Add($"agentClients '{client}' names update, a key of the claude-code client alone: whether the tool installs Claude Code with npm and brings one up to date");
        }

        AuthRules(builtIn, problems);
        RequestRules(builtIn, problems);
        AppointedNameRules(builtIn, providers, problems);

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

            // Every price is in the provider's currency, with the date its numbers were read: a model's flat prices, its
            // large-prompt prices and the provider's own price of a thousand searches alike, each said once for the
            // provider rather than again for every model that holds one.
            var holdsAPrice = provider.PricePerThousandSearches is not null || provider.Models.Any(model => PricesOf(model).Any());

            if (holdsAPrice && string.IsNullOrWhiteSpace(provider.Currency))
            {
                problems.Add($"{where} holds prices and names no currency; a provider says once the currency of every price of its models, three capital letters");
            }

            if (holdsAPrice && string.IsNullOrWhiteSpace(provider.ReadOn))
            {
                problems.Add($"{where} holds prices and names no readOn; the date its numbers were read (YYYY-MM-DD) is shown wherever a price is, so an old number is not taken for today's");
            }

            // One form of a date, read the same on every machine: the one the refusal names.
            if (provider.ReadOn is { Length: > 0 } read && !DateOnly.TryParseExact(read, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                problems.Add($"{where} readOn '{read}' is no date; the date its numbers were read is YYYY-MM-DD");
            }

            if (provider.Currency is { Length: > 0 } currency && !CurrencyName().IsMatch(currency))
            {
                problems.Add($"{where} currency '{currency}' is not three capital letters");
            }

            foreach (var (model, number) in provider.Models.Select((model, index) => (model, index + 1)))
            {
                // A code is what is sent to the API and how every step, tier and effort names the model: required, and
                // one word. A name is the one a person reads beside it: required.
                if (string.IsNullOrWhiteSpace(model.Code))
                {
                    problems.Add($"{where} model #{number.ToString(CultureInfo.InvariantCulture)} has no code; a model's code is what is sent to the API, and how everything names it");
                    continue;
                }

                if (model.Code.Any(char.IsWhiteSpace))
                {
                    problems.Add($"{where} model '{model.Code}' code holds a space; a model's code is what is sent to the API, one word");
                }

                if (string.IsNullOrWhiteSpace(model.Name))
                {
                    problems.Add($"{where} model '{model.Code}' has no name; a model's name is the one a person reads, shown beside its code");
                }

                ModelRules(resolved._tiers, provider, model, $"{where} model '{model.Code}'", problems);
            }
        }

        TierRules(resolved, problems);
        BalanceRules(config, problems);

        return (problems.Count == 0 ? resolved : null, problems);
    }

    /// <summary>
    /// How each client that signs in does: <c>auth</c> and <c>token</c> are keys of such a client alone - an API client
    /// signs each request with its provider's key - <c>auth</c> one of its ways, and <c>token</c> read only by a way that
    /// reads one, and a variable's name. That it is appointed once is <see cref="AppointedNameRules"/>'.
    /// </summary>
    private static void AuthRules(List<KeyValuePair<string, AgentClientConfig>> clients, List<string> problems)
    {
        foreach (var (client, entry) in clients)
        {
            var signsIn = KindOf(client) is { SignsIn: true };

            if (entry.Auth is not null && !signsIn)
            {
                problems.Add($"agentClients '{client}' names auth, a key of a client that signs in (claude-code): '{client}' signs each request with its provider's key");
                continue;
            }

            if (entry.Auth is { } auth && !AgentAuth.All.Contains(auth, StringComparer.Ordinal))
            {
                problems.Add($"agentClients '{client}' auth '{auth}' is none of: {string.Join(", ", AgentAuth.All)}");
                continue;
            }

            if (entry.Token is not { } token)
            {
                continue;
            }

            if (AgentAuth.TokenVariable(entry.Auth ?? AgentAuth.Login) is null)
            {
                problems.Add($"agentClients '{client}' names token, which only auth: {AgentAuth.OAuthToken} or {AgentAuth.AuthToken} reads; it signs in with auth: {entry.Auth ?? AgentAuth.Login}");
            }
            else if (!Secrets.AgentKeyStore.IsVariableName(token))
            {
                problems.Add($"agentClients '{client}' token '{token}' is not a variable's name: {Secrets.AgentKeyStore.VariableNameShape}, a name and never a token");
            }
        }
    }

    /// <summary>
    /// What the claude-api client's entry says of its requests - its tries, its longest wait, its cache's lifetime, the
    /// fields and betas it adds - each a key of that client alone, so a key that loads is a key that is read: tries none or
    /// more, a wait of a second or more, a lifetime of <see cref="ClaudeApi.ClaudeApiRequest.CacheTtls"/>, fields that set
    /// nothing the tool sends itself, and betas each one word, named once, none the API's own fallback's.
    /// </summary>
    private static void RequestRules(List<KeyValuePair<string, AgentClientConfig>> clients, List<string> problems)
    {
        const string Api = ClaudeApi.ClaudeApiClient.ClientName;

        foreach (var (client, entry) in clients)
        {
            var where = $"agentClients '{client}'";

            if (!string.Equals(client, Api, StringComparison.Ordinal))
            {
                (string Key, bool Named, string What)[] keys =
                [
                    ("maxRetries", entry.MaxRetries is not null, "how many times a failed request is asked again"),
                    ("maxRetryWaitSeconds", entry.MaxRetryWaitSeconds is not null, "the longest wait before a failed request is asked again"),
                    ("cacheTtl", entry.CacheTtl is not null, "how long what a request caches is kept"),
                    ("requestExtras", entry.RequestExtras is not null, "the fields merged into every request's body"),
                    ("betas", entry.Betas is not null, "the beta headers sent beside the tool's own"),
                ];

                foreach (var (key, _, what) in keys.Where(key => key.Named))
                {
                    problems.Add($"{where} names {key}, a key of the {Api} client alone: {what}");
                }

                continue;
            }

            if (entry.MaxRetries is < 0)
            {
                problems.Add($"{where} maxRetries is {entry.MaxRetries.Value.ToString(CultureInfo.InvariantCulture)}; how many times a failed request is asked again is none or more");
            }

            if (entry.MaxRetryWaitSeconds is < 1)
            {
                problems.Add($"{where} maxRetryWaitSeconds is {entry.MaxRetryWaitSeconds.Value.ToString(CultureInfo.InvariantCulture)}; the longest wait before a failed request is asked again is a second or more");
            }

            if (entry.CacheTtl is { } ttl && !ClaudeApi.ClaudeApiRequest.CacheTtls.Contains(ttl, StringComparer.Ordinal))
            {
                problems.Add($"{where} cacheTtl '{ttl}' is none of: {string.Join(", ", ClaudeApi.ClaudeApiRequest.CacheTtls)}");
            }

            if (entry.RequestExtras is { } extras)
            {
                problems.AddRange(ClaudeApi.ClaudeApiRequest.ExtrasProblems(extras, $"{where} requestExtras"));
            }

            foreach (var beta in entry.Betas ?? [])
            {
                if (beta.Length == 0 || beta.Any(character => char.IsWhiteSpace(character) || character == ','))
                {
                    problems.Add($"{where} betas '{beta}' is no beta's name: one word, sent in a header beside the tool's own");
                }
                else if (beta.StartsWith(ClaudeApi.ClaudeApiRequest.ServerSideFallbackPrefix, StringComparison.Ordinal))
                {
                    problems.Add($"{where} betas '{beta}' is refused: the API's own fallback is never asked for, for the engine's fallback is the only one a step has");
                }
            }

            foreach (var twice in (entry.Betas ?? []).GroupBy(beta => beta, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                problems.Add($"{where} lists '{twice.Key}' twice in betas: each beta is listed once");
            }
        }
    }

    /// <summary>
    /// The names a client appoints in the slot - its token, where it signs in with one, and each setting it reads - each by
    /// the name its entry gives or its own where it gives none, and each appointed once, beside every provider's keys and
    /// the client's other names, for a name holds one value. A setting's key is one of the client that reads it alone,
    /// and the name it gives is a variable's name.
    /// </summary>
    private static void AppointedNameRules(List<KeyValuePair<string, AgentClientConfig>> builtIn, List<AgentProviderConfig> providers, List<string> problems)
    {
        var keys = ClientKinds.SelectMany(kind => kind.Settings).Select(setting => setting.Key).Distinct(StringComparer.Ordinal).ToList();
        var clients = builtIn.Select(entry => entry.Key).Concat(Served(providers).Select(kind => kind.Name)).Distinct(StringComparer.Ordinal);

        foreach (var client in clients)
        {
            // builtIn holds only entries named as a client exactly: one of another case is no client's, and is said once above.
            var entry = builtIn.FirstOrDefault(each => each.Key == client).Value;
            var kind = KindOf(client)!;

            foreach (var key in keys.Where(key => !kind.Settings.Any(own => own.Key == key)))
            {
                var owner = ClientKinds.First(other => other.Settings.Any(own => own.Key == key));
                var setting = owner.Settings.First(own => own.Key == key);

                if (setting.Named(entry) is not null)
                {
                    problems.Add($"agentClients '{client}' names {key}, a key of the {owner.Name} client alone: the variable that holds {setting.What}");
                }
            }

            var names = new List<(string For, string Name)>();

            // The token a way of signing in reads, by the entry's name or Claude Code's; one the auth rules refuse is theirs to say.
            if (kind.SignsIn && AgentAuth.TokenVariable(entry?.Auth ?? AgentAuth.Login) is { } read && (entry?.Token ?? read) is var token && Secrets.AgentKeyStore.IsVariableName(token))
            {
                names.Add(("token", token));
            }

            foreach (var setting in kind.Settings)
            {
                if (setting.Named(entry) is { } named && !Secrets.AgentKeyStore.IsVariableName(named))
                {
                    problems.Add($"agentClients '{client}' {setting.Key} '{named}' is not a variable's name: {Secrets.AgentKeyStore.VariableNameShape}, a name and never a value");
                    continue;
                }

                names.Add((setting.Key, setting.NameIn(entry)));
            }

            for (var at = 0; at < names.Count; at++)
            {
                var (@for, name) = names[at];

                if (providers.FirstOrDefault(provider => provider.ApiKeyNames.Contains(name, StringComparer.Ordinal)) is { } provider)
                {
                    problems.Add($"agentClients '{client}' appoints '{name}' for its {@for} and provider '{provider.Name}' for a key: a name is appointed once");
                }
                else if (names.Take(at).FirstOrDefault(other => other.Name == name) is { For: { } first })
                {
                    problems.Add($"agentClients '{client}' appoints '{name}' for its {first} and its {@for}: a name is appointed once");
                }
            }
        }
    }

    /// <summary>
    /// Every setting the clients that serve the configuration's providers read from the slot, each by the name it is read
    /// under: the one rule of which settings are appointed, which the listing says and the slot is checked against.
    /// </summary>
    public IReadOnlyList<(string Client, AgentClientSetting Setting, string Name)> SettingsRead()
        => [.. Served(Config.Providers).SelectMany(kind => kind.Settings.Select(setting => (kind.Name, setting, setting.NameIn(Config.AgentClients.GetValueOrDefault(kind.Name)))))];

    /// <summary>The client kinds that serve a provider of <paramref name="providers"/>: those reaching its type.</summary>
    private static IEnumerable<AgentClientKind> Served(IEnumerable<AgentProviderConfig> providers)
        => ClientKinds.Where(kind => providers.Any(provider => string.Equals(provider.Type, kind.ProviderType, StringComparison.Ordinal)));

    /// <summary>The name the slot holds <paramref name="client"/>'s setting <paramref name="key"/> under: its entry's, else the setting's own.</summary>
    /// <param name="client">The client's name.</param>
    /// <param name="key">The setting's key: <see cref="AgentClientSetting.ExecutableKey"/>, <see cref="AgentClientSetting.ConfigDirKey"/>.</param>
    public string? SettingOf(string client, string key)
        => KindOf(client)?.Settings.FirstOrDefault(setting => setting.Key == key)?.NameIn(Config.AgentClients.GetValueOrDefault(client));

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
    /// How <paramref name="client"/> signs in, where it is a client that signs in: its entry's <c>auth</c>, the machine's
    /// login where it says none; null for a client that signs each request with its provider's key, and for one the
    /// tool does not build in, which is refused where the configuration or the definition names it.
    /// </summary>
    /// <param name="client">The client's name.</param>
    public string? AuthOf(string client)
        => KindOf(client) is { SignsIn: true }
            ? Config.AgentClients.TryGetValue(client, out var entry) && entry.Auth is { } auth ? auth : AgentAuth.Login
            : null;

    /// <summary>
    /// The variable the slot holds <paramref name="client"/>'s token under, where it signs in with one: its entry's
    /// <c>token</c>, else the one Claude Code reads; null where it reads no token.
    /// </summary>
    /// <param name="client">The client's name.</param>
    public string? TokenOf(string client)
        => AuthOf(client) is { } auth && AgentAuth.TokenVariable(auth) is { } read
            ? Config.AgentClients.TryGetValue(client, out var entry) && entry.Token is { } named ? named : read
            : null;

    /// <summary>
    /// The names a model's key is read under on the client that reaches it - <paramref name="client"/> where the step
    /// names one, else the provider's own - in the order they are tried: the provider's <c>apiKeyNames</c> on a client
    /// that signs each request with its provider's key (<c>claude-api</c>, <c>glm</c>) and on one that signs in with a
    /// key (<c>auth: apiKey</c>); the token's one name on one that signs in with a token; none on one that signs in
    /// with the machine's login. The one place that says what a step's model needs of the slot: <c>validate-workflow</c>
    /// asks it of every step, and so will the engine once the agent step lands, for the key a launch is given.
    /// </summary>
    /// <param name="provider">The provider that lists the model.</param>
    /// <param name="client">The client the step names, or null where it names none.</param>
    public IReadOnlyList<string> KeysRead(AgentProviderConfig provider, string? client = null)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var reaching = client ?? provider.Client;

        return AuthOf(reaching) switch
        {
            null or AgentAuth.ApiKey => provider.ApiKeyNames,
            AgentAuth.Login => [],
            _ => TokenOf(reaching) is { } token ? [token] : [],
        };
    }

    /// <summary>
    /// Every name the configuration appoints a value of the slot for, of providers, clients and web-search sources, each
    /// with who appoints it, by which key, what its value is and whether it is a secret: each provider's keys, in its
    /// order, the settings and token a built-in client reads where a provider of the type it reaches is declared - the
    /// program the claude-code client starts among them - and each web-search source's key (<see cref="SourceKeys"/>).
    /// What the slot holds beyond these is appointed by nothing, which a listing and a missing key's refusal say, where a
    /// misspelt name shows.
    /// </summary>
    public IReadOnlyList<Secrets.AppointedKey> Appointed()
    {
        var appointed = new List<Secrets.AppointedKey>();

        foreach (var provider in Config.Providers)
        {
            appointed.AddRange(provider.ApiKeyNames.Select(name => new Secrets.AppointedKey(name, $"provider '{provider.Name}'", Secrets.AgentKeyStore.WhatHolds(provider), "apiKeyNames")));
        }

        foreach (var kind in Served(Config.Providers))
        {
            appointed.AddRange(SettingsRead().Where(read => read.Client == kind.Name).Select(read => new Secrets.AppointedKey(
                read.Name, $"client '{kind.Name}'", read.Setting.What, read.Setting.Key, Secret: false)));

            if (TokenOf(kind.Name) is { } token)
            {
                appointed.Add(new Secrets.AppointedKey(token, $"client '{kind.Name}'", $"the token client '{kind.Name}' signs in with ({AuthOf(kind.Name)})", "token"));
            }
        }

        foreach (var (source, name) in SourceKeys())
        {
            appointed.Add(new Secrets.AppointedKey(name, $"web-search source '{source}'", $"the key web-search source '{source}' is sent, where it is there", "credential.name"));
        }

        return appointed;
    }

    /// <summary>
    /// Each variable of the slot a web-search source is sent as its key, by the source, in the configuration's order:
    /// optional, each sent to its own source alone, and as secret as a provider's.
    /// </summary>
    public IReadOnlyList<(string Source, string Name)> SourceKeys()
        => [.. Web.Search.WebSearchSources.Of(Config, Web.Search.WebSearchKinds.OfThisBuild).All
            .Where(source => source.Credential is { From: "env", Name: not null })
            .Select(source => (source.Name, source.Credential!.Name!))];

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

    /// <summary>
    /// The models an agent runs on, in the order they are tried: the model it names, else its tier's, else the default
    /// tier's - the one resolution every agent of a workflow is read by. Empty where what it names resolves to nothing.
    /// </summary>
    /// <param name="tier">The tier it names; null where it names none.</param>
    /// <param name="model">The model it names; null where it names none.</param>
    public IReadOnlyList<(AgentProviderConfig Provider, AgentModelConfig Model)> ModelsFor(string? tier, string? model)
        => model is { } named
            ? (Of(named) is { } of ? [(of.Provider, of.Model)] : [])
            : ModelsOf(tier ?? DefaultTier ?? string.Empty);

    /// <summary>Every model of <paramref name="tier"/>, in the order <c>tierConfig</c> says they are tried; empty where the tier is not defined.</summary>
    public IReadOnlyList<(AgentProviderConfig Provider, AgentModelConfig Model)> ModelsOf(string tier)
    {
        if (!_tiers.TryGetValue(tier, out var codes))
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

    /// <summary>
    /// Whether the configuration has a price for a model: its input and its output priced, the two written together or
    /// not at all. The one owner of "priced" - for the listing, for <c>balanceKeyUsage</c>, and for what
    /// <c>validate-workflow</c> says of each model a step may run on; a kind of token a request uses that its entry has
    /// no price for is still unknown (<see cref="AgentPrices"/>).
    /// </summary>
    /// <param name="model">The model's entry.</param>
    public static bool Priced(AgentModelConfig model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return model.PriceInputMiss is not null || model.PriceOutput is not null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z]{3}$")]
    private static partial System.Text.RegularExpressions.Regex CurrencyName();

    /// <summary>
    /// Each name more than one provider lists - one provider listing it twice is that provider's own problem, said
    /// apart - with the providers that list it, each once, in the order they are listed.
    /// </summary>
    private static IEnumerable<(string Name, IReadOnlyList<AgentProviderConfig> By)> SharedAcross(
        IReadOnlyList<AgentProviderConfig> providers,
        Func<AgentProviderConfig, IEnumerable<string>> names)
        => providers
            .SelectMany(provider => names(provider).Distinct(StringComparer.Ordinal).Select(name => (provider, name)))
            .GroupBy(entry => entry.name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => (group.Key, (IReadOnlyList<AgentProviderConfig>)[.. group.Select(entry => entry.provider)]));

    private static void ModelRules(IReadOnlyDictionary<string, List<string>> tiers, AgentProviderConfig provider, AgentModelConfig model, string where, List<string> problems)
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

        // An output over the context is said only of sizes that are each a size: a context that is none is said alone.
        if (model.ContextTokens > 0 && model.MaxOutputTokens > model.ContextTokens)
        {
            problems.Add($"{where} maxOutputTokens {model.MaxOutputTokens.ToString(CultureInfo.InvariantCulture)} is over its contextTokens {model.ContextTokens.ToString(CultureInfo.InvariantCulture)}; the most a reply holds is at most what the context holds");
        }

        foreach (var tier in model.Tiers.Where(tier => !tiers.ContainsKey(tier)))
        {
            problems.Add($"{where} serves tier '{tier}', which tierConfig does not define. Defined: {string.Join(", ", tiers.Keys.OrderBy(key => key, StringComparer.Ordinal))}");
        }

        foreach (var price in PricesOf(model).Where(price => price.Value < 0))
        {
            problems.Add($"{where} {price.Key} {price.Value.ToString(CultureInfo.InvariantCulture)} is below zero");
        }

        if (model.PriceInputMiss is null != (model.PriceOutput is null))
        {
            problems.Add($"{where} names one of priceInputMiss and priceOutput and not the other; the two are written together or not at all");
        }

        // A large-prompt set is held to the flat set's own rule: its input and its output priced together or not at all.
        if (model.LargePrompt is { } prices && prices.PriceInputMiss is null != (prices.PriceOutput is null))
        {
            problems.Add($"{where} largePrompt names one of priceInputMiss and priceOutput and not the other; the two are written together or not at all");
        }

        if (model.LargePrompt is { } large && large.OverTokens <= 0)
        {
            problems.Add($"{where} largePrompt.overTokens {large.OverTokens.ToString(CultureInfo.InvariantCulture)} is not a positive whole number");
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

    /// <summary>
    /// tierConfig's own rules, each tier by its name exactly: a tier is named by the orchestration name rule, its list
    /// holds at least one model, each once, each one a provider lists and whose tiers name the tier; and a model serving a
    /// tier tierConfig defines is in that tier's list. A model serving a tier tierConfig does not define is said where
    /// the model is (<see cref="ModelRules"/>), once: there is no list of it to put the model in.
    /// </summary>
    private static void TierRules(AgentProviders resolved, List<string> problems)
    {
        foreach (var (tier, codes) in resolved._tiers)
        {
            if (string.IsNullOrWhiteSpace(tier))
            {
                problems.Add("tierConfig has a tier with no name; a tier is named by the orchestration name rule");
            }
            else if (Orchestration.OrchestrationRules.NameProblem(tier) is { } shape)
            {
                problems.Add($"tierConfig '{tier}' is not named by the orchestration name rule: {shape}");
            }

            if (codes.Count == 0)
            {
                problems.Add($"tierConfig.{tier} is empty; a tier's list is its models in the order they are tried, and it holds at least one");
            }

            foreach (var twice in codes.GroupBy(code => code, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                problems.Add($"tierConfig.{tier} lists '{twice.Key}' twice");
            }

            foreach (var code in codes.Distinct(StringComparer.Ordinal))
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
            foreach (var tier in entry.Model.Tiers.Distinct(StringComparer.Ordinal).Where(tier => resolved._tiers.TryGetValue(tier, out var codes) && !codes.Contains(code, StringComparer.Ordinal)))
            {
                problems.Add($"model '{code}' serves tier '{tier}', and tierConfig.{tier} does not list it: put it in the list where it is to be tried");
            }
        }
    }
}
