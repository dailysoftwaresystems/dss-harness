using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Core.Agents;

/// <summary>
/// One entry of the order a step's model is reached by: a model with one of its provider's keys - the
/// serving entry the first, and each later model of its tier with each of its keys in turn, each model's keys before
/// the next model - the effort the step gives that model resolved with it.
/// </summary>
/// <param name="Model">The model's code.</param>
/// <param name="Provider">The provider that lists it.</param>
/// <param name="Client">The client it is reached through: the step's, the definition's, or the provider's own.</param>
/// <param name="Key">The name the key is read under, of the provider's <c>apiKeyNames</c> in their order.</param>
/// <param name="Effort">The level this model runs at, resolved through the four sources; null for the model's own default.</param>
public sealed record AgentModelEntry(string Model, string Provider, string Client, string Key, string? Effort);

/// <summary>
/// A step's model resolved as part 03 rules ("Selecting a model is enough"): the entry that serves, and the order the
/// step goes on by - the serving model's other keys, then the later models of the tier the model came from, each with
/// each of its provider's keys, in <c>tierConfig</c>'s order (a model named outright, by the step or by its definition,
/// has its own other keys after it and no other model).
/// </summary>
/// <param name="Serving">The entry the step starts on.</param>
/// <param name="After">The entries the step may go on with, in the order they are tried; the model's other keys alone where it was named outright.</param>
/// <param name="Tier">The tier the model came from, which its order is; null where a model was named outright.</param>
public sealed record AgentResolution(AgentModelEntry Serving, IReadOnlyList<AgentModelEntry> After, string? Tier)
{
    /// <summary>
    /// A step's model, effort and client resolved against the configuration, with its agent definition's as the level
    /// below its own: the step's <c>model</c>, the first of its <c>tier</c>, the definition's
    /// <c>model</c>, the first of the definition's <c>tier</c>, the first of <c>defaults.agentTier</c> - each model's
    /// effort through the step's entry, its <c>all</c> or one level, the definition's entry, its <c>all</c> or one
    /// level, else the model's own default - the client the step's, the definition's, else the provider's own.
    /// </summary>
    /// <param name="providers">The configuration's providers and tiers, resolved.</param>
    /// <param name="tier">The tier the step names, or null.</param>
    /// <param name="model">The model the step names, or null.</param>
    /// <param name="effort">The step's one effort level, or null.</param>
    /// <param name="effortByModel">The step's effort map, or null.</param>
    /// <param name="client">The client the step names, or null.</param>
    /// <param name="definition">The step's agent definition, or null where it names no agent.</param>
    /// <returns>The resolution, or null where no model resolves - refused by the definition's check, which names why.</returns>
    public static AgentResolution? Of(
        AgentProviders providers,
        string? tier,
        string? model,
        string? effort,
        IReadOnlyDictionary<string, string>? effortByModel,
        string? client,
        AgentDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(providers);

        // The model, most specific first (Picked): the step's own, the first of its tier, the definition's own, the first
        // of the definition's tier, the first of the default tier's - and the tier the model came from, which its order
        // is: a model named outright, by the step or by the definition, has no tier and no other model after it.
        var picked = Picked(tier, model, definition);
        var fromTier = picked.Model is null ? picked.Tier ?? providers.DefaultTier : null;

        model = picked.Model ?? (fromTier is { } serving ? FirstOf(providers, serving) : null);

        if (model is null || providers.Of(model) is not { } named)
        {
            return null;
        }

        // The client the step names, else the definition's, else the provider's own of the provider that lists the model.
        var through = ClientOf(client, definition, named.Provider);

        // The step's whole order as entries of a model and one of its provider's keys, each model's keys before the
        // next model: every model of the tier the model came from, in tierConfig's order - a model named
        // outright has its own keys and nothing after them. The first entry serves; the rest is what the step goes on
        // by, later keys of the serving model first.
        var order = new List<AgentModelEntry>();

        void Entries(AgentProviderConfig provider, DssHarness.Core.Configuration.AgentModelConfig entry, string reached)
        {
            var level = LevelFor(entry.Code, effort, effortByModel, definition?.Effort, definition?.EffortByModel)?.Level;
            var keys = providers.KeysRead(provider, reached);

            // A client that signs in with the machine's login reads no key: the model is its own entry, the login
            // itself, and the tier's order keeps it where tierConfig puts it.
            if (keys.Count == 0)
            {
                order.Add(new AgentModelEntry(entry.Code, provider.Name, reached, string.Empty, level));
                return;
            }

            foreach (var key in keys)
            {
                order.Add(new AgentModelEntry(entry.Code, provider.Name, reached, key, level));
            }
        }

        if (fromTier is { } ordered)
        {
            foreach (var (provider, entry) in providers.ModelsOf(ordered))
            {
                Entries(provider, entry, ClientOf(client, definition, provider));
            }
        }
        else
        {
            Entries(named.Provider, named.Model, through);
        }

        return new AgentResolution(order[0], [.. order.Skip(1)], fromTier);
    }

    /// <summary>
    /// What a step runs on, as one pick decides it: the step's model, else its tier, else its definition's model, else
    /// its definition's tier - each level whole, never a tier of one and a model of the other - and neither where the
    /// configuration's default tier serves. The one pick the run resolves by (<see cref="Of"/>) and the definition's
    /// check holds a step to, so that what is checked, priced and asked a key for is what runs.
    /// </summary>
    /// <param name="tier">The tier the step names, or null.</param>
    /// <param name="model">The model the step names, or null.</param>
    /// <param name="definition">The step's agent definition, or null where it names no agent.</param>
    /// <returns>The model named outright, or the tier whose first model serves; both null for the default tier.</returns>
    public static (string? Tier, string? Model) Picked(string? tier, string? model, AgentDefinition? definition)
        => model is not null ? (null, model)
            : tier is not null ? (tier, null)
            : definition?.Model is { } byDefinition ? (null, byDefinition)
            : (definition?.Tier, null);

    /// <summary>
    /// The client a model of <paramref name="provider"/>'s is reached through: the step's, else its definition's, else the
    /// provider's own - each entry of the order on its own, so a tier whose models two providers list reaches each through
    /// its own provider's client where neither the step nor its definition names one. The one rule the run resolves by
    /// (<see cref="Of"/>) and the definition's check holds each model to.
    /// </summary>
    /// <param name="client">The client the step names, or null.</param>
    /// <param name="definition">The step's agent definition, or null where it names no agent.</param>
    /// <param name="provider">The provider that lists the model.</param>
    public static string ClientOf(string? client, AgentDefinition? definition, AgentProviderConfig provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        return client ?? definition?.Client ?? provider.Client;
    }

    /// <summary>The first model of a tier, by code, or null where the tier is not defined.</summary>
    private static string? FirstOf(AgentProviders providers, string tier)
        => providers.ModelsOf(tier) is [var first, ..] ? first.Model.Code : null;

    /// <summary>
    /// The level one model runs at, through the four sources an effort is read from - its own entry under the step's
    /// effort map, the map's <c>all</c>, the step's one level, the definition's entry, the definition's <c>all</c> or
    /// one level, else none (the model's own default) - and where the level came from, as a refusal says it.
    /// </summary>
    /// <param name="code">The model's code.</param>
    /// <param name="effort">The step's one effort level, or null.</param>
    /// <param name="effortByModel">The step's effort map, or null.</param>
    /// <param name="definitionEffort">The definition's one effort level, or null.</param>
    /// <param name="definitionByModel">The definition's effort map, or null.</param>
    public static (string Level, LevelSource From)? LevelFor(
        string code,
        string? effort,
        IReadOnlyDictionary<string, string>? effortByModel,
        string? definitionEffort = null,
        IReadOnlyDictionary<string, string>? definitionByModel = null)
    {
        if (effortByModel is not null)
        {
            if (effortByModel.TryGetValue(code, out var own))
            {
                return (own, LevelSource.StepEntry);
            }

            if (effortByModel.TryGetValue("all", out var every))
            {
                return (every, LevelSource.StepAll);
            }
        }
        else if (effort is not null)
        {
            return (effort, LevelSource.StepOne);
        }

        if (definitionByModel is not null)
        {
            if (definitionByModel.TryGetValue(code, out var own))
            {
                return (own, LevelSource.DefinitionEntry);
            }

            if (definitionByModel.TryGetValue("all", out var every))
            {
                return (every, LevelSource.DefinitionAll);
            }
        }
        else if (definitionEffort is not null)
        {
            return (definitionEffort, LevelSource.DefinitionOne);
        }

        return null;
    }
}

/// <summary>Where a model's level came from, as a refusal says it: the step's entry, the step's <c>all</c>, the step's one level, the definition's entry, the definition's <c>all</c> or one level.</summary>
public enum LevelSource
{
    /// <summary>The step's own entry for the model's code under its effort map.</summary>
    StepEntry,

    /// <summary>The step's effort map's <c>all</c>.</summary>
    StepAll,

    /// <summary>The step's one effort level, for every model.</summary>
    StepOne,

    /// <summary>The agent definition's own entry for the model's code under its effort map.</summary>
    DefinitionEntry,

    /// <summary>The agent definition's effort map's <c>all</c>.</summary>
    DefinitionAll,

    /// <summary>The agent definition's one effort level, for every model.</summary>
    DefinitionOne,
}
