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
    /// date, before a run (claude-code's key; true where nothing is said). With false the tool installs nothing and
    /// updates nothing: a run that needs the client and finds Claude Code missing or too old is refused (14), with the
    /// command.
    /// </summary>
    public bool? Update { get; init; }
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
