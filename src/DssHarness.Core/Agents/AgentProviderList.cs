namespace DssHarness.Core.Agents;

/// <summary>
/// What <c>list-agent-providers</c> answers: each provider, client and tier of the configuration, and for every name
/// it appoints whether the slot holds it - for the command line now and a settings screen later. The record has no
/// member that could hold a key's value: what the slot holds is said by name alone.
/// </summary>
/// <param name="Slot">The slot's path, relative to the repository's root.</param>
/// <param name="Files">How many files the slot holds.</param>
/// <param name="Providers">Each provider the configuration declares, in the order it lists them.</param>
/// <param name="BalanceKeyUsage">Whether <c>balanceKeyUsage</c> is on: whether the engine, when it runs a run's steps, is to balance each provider's keys by what each spent.</param>
/// <param name="Tiers">Each tier the configuration defines, with its models in the order they are tried.</param>
/// <param name="Clients">Each client a provider names, with how it signs in and the names it appoints.</param>
/// <param name="Settings">Each setting a client reads from the slot, by the name it is read under, with whether the slot holds it.</param>
/// <param name="Unappointed">The names the slot holds that nothing appoints, in the order their files are read.</param>
public sealed record AgentProviderList(
    string Slot,
    int Files,
    IReadOnlyList<AgentProviderListing> Providers,
    bool BalanceKeyUsage,
    IReadOnlyList<AgentTierListing> Tiers,
    IReadOnlyList<AgentClientListing> Clients,
    IReadOnlyList<AgentSettingListing> Settings,
    IReadOnlyList<string> Unappointed);

/// <summary>One setting a client reads from the slot, as the listing holds it: no key, and one the slot need not hold.</summary>
/// <param name="Client">The client that reads it.</param>
/// <param name="Key">The entry's key that renames it: <c>executable</c>, <c>configDir</c>.</param>
/// <param name="Name">The variable it is read under.</param>
/// <param name="Held">Whether the slot declares it, holding something.</param>
public sealed record AgentSettingListing(string Client, string Key, string Name, bool Held);

/// <summary>One provider as the listing holds it.</summary>
/// <param name="Name">The provider's name.</param>
/// <param name="Type">Whose API it is.</param>
/// <param name="Api">The address of its API.</param>
/// <param name="Client">The client its models are reached through.</param>
/// <param name="Keys">Its keys' names, in the order they are tried, each with whether the slot holds it.</param>
/// <param name="Models">Its models, in the order it lists them.</param>
/// <param name="Currency">The currency of every price of its models, where it says one.</param>
/// <param name="ReadOn">The date its numbers were read, where it says one.</param>
public sealed record AgentProviderListing(
    string Name,
    string Type,
    string Api,
    string Client,
    IReadOnlyList<AgentKeyListing> Keys,
    IReadOnlyList<AgentModelListing> Models,
    string? Currency,
    string? ReadOn);

/// <summary>One appointed name, as the listing holds it: whether the slot declares it, and never a value.</summary>
/// <param name="Name">The variable's name.</param>
/// <param name="Held">Whether the slot declares it, holding something.</param>
public sealed record AgentKeyListing(string Name, bool Held);

/// <summary>One model as the listing holds it.</summary>
/// <param name="Code">What is sent to the API, and how everything names the model.</param>
/// <param name="Name">The name a person reads.</param>
/// <param name="Tiers">The tiers it serves.</param>
/// <param name="Efforts">The efforts it takes.</param>
/// <param name="ContextTokens">What its context holds.</param>
/// <param name="MaxOutputTokens">The most a reply holds.</param>
/// <param name="Priced">Whether the configuration has a price for it.</param>
/// <param name="Prices">Its prices per million tokens, each by its configuration key; empty where it has none.</param>
public sealed record AgentModelListing(
    string Code,
    string Name,
    IReadOnlyList<string> Tiers,
    IReadOnlyList<string> Efforts,
    long ContextTokens,
    long MaxOutputTokens,
    bool Priced,
    IReadOnlyDictionary<string, double> Prices);

/// <summary>One tier as the listing holds it: its models in the order they are tried.</summary>
/// <param name="Name">The tier's name.</param>
/// <param name="Models">Its models, in the order they are tried, each by code and the name a person reads.</param>
public sealed record AgentTierListing(string Name, IReadOnlyList<(string Code, string Name)> Models);

/// <summary>
/// One client as the listing holds it: how it signs in, and the names it appoints with whether the slot holds each -
/// none for a client that signs in with the machine's login, its one token's name for one that signs in with a token,
/// its provider's keys for one that signs in with a key or signs each request with them (claude-api, glm).
/// </summary>
/// <param name="Name">The client's name, as its providers name it.</param>
/// <param name="SignIn">How it signs in, said as the listing says it.</param>
/// <param name="Keys">The names it appoints, in their order, each with whether the slot holds it.</param>
public sealed record AgentClientListing(string Name, string SignIn, IReadOnlyList<AgentKeyListing> Keys);
