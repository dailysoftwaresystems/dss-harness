namespace DssHarness.Core.Configuration;

/// <summary>
/// One client of the configuration, under <c>agentClients</c>: how a model is reached, named by its kind. A client
/// names no model, no effort, no address and no key - which model, at what effort, where and with whose key is said by
/// the step, <c>tierConfig</c> and the provider. Each key is read by the slice that builds what it says.
/// </summary>
public sealed class AgentClientConfig
{
    /// <summary>
    /// Whether the tool installs Claude Code with npm where npm has none, and brings one older than it needs up to
    /// date, before a run (claude-code's key; true where nothing is said - the default is
    /// <c>ClaudeCodeUpdateService.UpdatesOn</c>'s, which is how the key is read). With false the service installs
    /// nothing and updates nothing and refuses (14), with the command, a Claude Code that is missing or too old: what a
    /// run that needs the client will be refused with once the engine's step runner asks it before the run's first step.
    /// </summary>
    public bool? Update { get; init; }

    /// <summary>
    /// How a claude-code client signs in (claude-code's key): <c>login</c>, the machine's login, where nothing is said;
    /// <c>apiKey</c>, a key of the provider's <c>apiKeyNames</c>, tried in their order; <c>oauthToken</c> or
    /// <c>authToken</c>, the token the slot holds under <see cref="Token"/>. A launch is given that credential and no
    /// other, whatever the tool's own environment holds.
    /// </summary>
    public string? Auth { get; init; }

    /// <summary>
    /// The name of the slot's variable that holds the token a claude-code client signs in with under
    /// <c>auth: oauthToken</c> (<c>CLAUDE_CODE_OAUTH_TOKEN</c> where nothing is said) or <c>auth: authToken</c>
    /// (<c>ANTHROPIC_AUTH_TOKEN</c>): a name and never a token, appointed once beside every provider's
    /// <c>apiKeyNames</c>.
    /// </summary>
    public string? Token { get; init; }

    /// <summary>
    /// The name of the slot's variable that holds the Claude Code directory a claude-code client signs in from and reads
    /// the user's settings in - a specific login's - where it is not the tool's own (claude-code's key;
    /// <c>CLAUDE_CONFIG_DIR</c> where nothing is said): a name, appointed once beside every key.
    /// </summary>
    public string? ConfigDir { get; init; }

    /// <summary>
    /// The name of the slot's variable that holds the program a claude-code client starts in place of npm's Claude Code
    /// (claude-code's key; <c>CLAUDE_EXECUTABLE</c> where nothing is said): a name, appointed once beside every key.
    /// </summary>
    public string? Executable { get; init; }
}

/// <summary>
/// What an agent of a run may do, under <c>agentPolicy</c>: the restrictions the repository adds to the rules built
/// into the tool. A restriction only ever refuses, from every source at once, and never removes one.
/// </summary>
public sealed class AgentPolicyConfig
{
    /// <summary>
    /// Restriction rules, each one string - <c>Run(git push *)</c>, <c>Write(.github/**)</c>,
    /// <c>Read(secrets/**)</c>, <c>Web(pastebin.com)</c> - applied beside the tool's built-in refusals to every
    /// agent of every run under this configuration, and added to by a workflow's definitions and steps. Left out
    /// where the repository restricts nothing, which is the default.
    /// </summary>
    public List<string> Restrictions { get; init; } = [];

    /// <summary>
    /// The allow list of every web request an agent makes: each entry one domain, and a request is allowed only to
    /// a domain on the list or a name under it. Left out where every public address is allowed, which is the
    /// default; given empty it is refused - leave the key out instead. A <c>Web(...)</c> restriction refuses
    /// beside it whatever this allows.
    /// </summary>
    public List<string>? WebDomains { get; init; }

    /// <summary>
    /// Whether the worktree rules are on: what keeps an agent's worktree a thing the engine can measure, and its run
    /// a thing only the engine moves - git's state-writing verbs, the merges and CI starts of <c>gh</c>, the tool's
    /// own configuration and state. True where nothing is said. The tool's own verbs and <c>run-side-task</c> are
    /// refused whatever this says.
    /// </summary>
    public bool? WorktreeRules { get; init; }

    /// <summary>
    /// Whether Claude Code's protected paths are refused on every client - the writes Claude Code guards, held to
    /// alike where this tool's own clients would pass them. True where nothing is said; false takes this tool's
    /// refusal away, and each client's own guard, where it has one, stays its own.
    /// </summary>
    public bool? ProtectedPaths { get; init; }
}

/// <summary>
/// A company whose models the tool reaches: the one place a model's API address, its key's name and its client are
/// said. A provider appoints the NAME of the variable that holds its key, and the value is in the git-ignored slot -
/// nothing of a key is tracked here.
/// </summary>
public sealed class AgentProviderConfig
{
    /// <summary>The provider's name, by the orchestration name rule, listed once under <c>providers</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Whose API it is: <c>anthropic</c> or <c>zai</c>. It decides which clients reach it.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>The address of its API, an absolute <c>https</c> address (<c>http</c> only for <c>localhost</c>): the company's own or a gateway's.</summary>
    public string Api { get; init; } = string.Empty;

    /// <summary>
    /// The names of the variables that hold its keys in the slot, at least one, in the order they are tried: each
    /// <c>[A-Z][A-Z0-9_]*</c>, a name and never a key. A second key of one company is a second name in the list.
    /// </summary>
    public List<string> ApiKeyNames { get; init; } = [];

    /// <summary>The client its models are reached through, unless a step or a definition names another.</summary>
    public string Client { get; init; } = string.Empty;

    /// <summary>The currency of every price of its models, said once: three capital letters. Needed where a model of it has a price.</summary>
    public string? Currency { get; init; }

    /// <summary>The date the numbers of its models - prices and sizes - were read (<c>YYYY-MM-DD</c>). Needed where a model of it has a price.</summary>
    public string? ReadOn { get; init; }

    /// <summary>Where they were read: an address, or a few words.</summary>
    public string? Source { get; init; }

    /// <summary>What the provider bills for a thousand web searches it runs itself, beside the tokens.</summary>
    public double? PricePerThousandSearches { get; init; }

    /// <summary>Its models, at least one: everything the tool knows of a model is its entry here.</summary>
    public List<AgentModelConfig> Models { get; init; } = [];
}

/// <summary>
/// A model's entry: everything the tool knows of it. The client knows how to speak its provider's API and nothing of
/// one model or another - what a model takes, holds and costs is read from here.
/// </summary>
public sealed class AgentModelConfig
{
    /// <summary>What is sent to the API, and how a step, an agent definition, <c>tierConfig</c> and <c>--model</c> name the model. Listed once across every provider.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>The name a person reads. Shown beside the code wherever a model is shown; never sent.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The tiers the model serves, each a name <c>tierConfig</c> defines; empty for a model only a step that names it outright runs on.</summary>
    public List<string> Tiers { get; init; } = [];

    /// <summary>The efforts it takes, each one of its client's levels. A step's effort is checked against it.</summary>
    public List<string> Efforts { get; init; } = [];

    /// <summary>What its context holds: the API clients' <c>max_tokens</c> ceiling is measured against it, and compaction starts by it.</summary>
    public long ContextTokens { get; init; }

    /// <summary>The most a reply holds. At most the context.</summary>
    public long MaxOutputTokens { get; init; }

    /// <summary>Per million tokens, in the provider's currency: input not read from a cache. Written with <see cref="PriceOutput"/> or not at all.</summary>
    public double? PriceInputMiss { get; init; }

    /// <summary>Per million tokens: input read from a cache.</summary>
    public double? PriceInputHit { get; init; }

    /// <summary>Per million tokens: output. Written with <see cref="PriceInputMiss"/> or not at all.</summary>
    public double? PriceOutput { get; init; }

    /// <summary>Per million tokens: a cache write kept five minutes, where the provider bills them apart.</summary>
    public double? PriceCacheWrite { get; init; }

    /// <summary>Per million tokens: a cache write kept an hour, where the provider bills them apart.</summary>
    public double? PriceCacheWrite1h { get; init; }

    /// <summary>The prices of a request whose prompt is over its <c>overTokens</c>, where a provider prices by the prompt's length: the same five keys.</summary>
    public LargePromptPrices? LargePrompt { get; init; }
}

/// <summary>The prices of a large prompt, where a provider prices a request by its length: the same five keys, over <see cref="OverTokens"/> tokens.</summary>
public sealed class LargePromptPrices
{
    /// <summary>Over this many tokens - input, cache writes and cache reads together - the large-prompt prices hold.</summary>
    public long OverTokens { get; init; }

    /// <summary>Per million tokens: input not read from a cache.</summary>
    public double? PriceInputMiss { get; init; }

    /// <summary>Per million tokens: input read from a cache.</summary>
    public double? PriceInputHit { get; init; }

    /// <summary>Per million tokens: output.</summary>
    public double? PriceOutput { get; init; }

    /// <summary>Per million tokens: a cache write kept five minutes.</summary>
    public double? PriceCacheWrite { get; init; }

    /// <summary>Per million tokens: a cache write kept an hour.</summary>
    public double? PriceCacheWrite1h { get; init; }
}
