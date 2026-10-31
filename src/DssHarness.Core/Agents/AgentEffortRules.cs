using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Core.Agents;

/// <summary>
/// The one check of the effort an agent is given against the models it runs on (V38): each model's level, resolved
/// through the four sources of value 418, held to that model's <c>efforts</c>, and every code an effort map names that
/// the agent does not run on - which would be dead. What a workflow's step is checked by, and a side-task's command line
/// and its chat's <c>/effort</c> alike.
/// </summary>
public static class AgentEffortRules
{
    /// <summary>The effort ladder by capability, lowest first: the order a refusal names the nearest level a model takes.</summary>
    private static readonly IReadOnlyList<string> CapabilityLadder = AgentProviders.EffortLevels;

    /// <summary>The levels of no map, as <see cref="Problems"/> reads none.</summary>
    private static readonly IReadOnlyDictionary<string, string> EmptyEfforts = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Each model's effort, resolved through the step's and its definition's four sources and checked against that
    /// model's <c>efforts</c> - every model of the tier, the ones this machine cannot use included - and a code
    /// under <c>effort</c>, the step's or its definition's, that the step does not run on, which would be dead: a
    /// repository that changes its tier then hears which steps still name the old model.
    /// </summary>
    /// <param name="models">Every model the agent may run on, in its order.</param>
    /// <param name="tier">The tier the models are of; null where a model was named outright.</param>
    /// <param name="effort">The one level given, or null.</param>
    /// <param name="effortByModel">The levels given by model, or null.</param>
    /// <param name="definition">The agent definition whose effort stands below, or null.</param>
    /// <param name="where">What the problem is of, as each begins: <c>step 'plan'</c>, <c>the side-task</c>.</param>
    /// <param name="giveOwn">
    /// How a level of a model's own is given, from the model's code, the level it takes nearest and whether the blanket
    /// came from the definition - the remedy a problem ends with; null for a workflow's <c>effort</c> key.
    /// </param>
    /// <returns>Each problem, one line; empty where every model takes its level.</returns>
    public static IReadOnlyList<string> Problems(
        IReadOnlyList<(AgentProviderConfig Provider, AgentModelConfig Model)> models,
        string? tier,
        string? effort,
        IReadOnlyDictionary<string, string>? effortByModel,
        AgentDefinition? definition,
        string where,
        Func<string, string, bool, string>? giveOwn = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(where);

        giveOwn ??= (code, nearest, fromDefinition) => $"Give it a level of its own under effort{(fromDefinition ? ", in the agent's definition" : string.Empty)}: {code}: {nearest}";

        var problems = new List<string>();

        foreach (var code in CodesOf(effortByModel, definition?.EffortByModel).Where(code => !string.Equals(code, "all", StringComparison.Ordinal) && models.All(entry => !string.Equals(entry.Model.Code, code, StringComparison.Ordinal))))
        {
            problems.Add($"{where} gives an effort to '{code}', which it does not run on. It runs on: {string.Join(", ", models.Select(entry => entry.Model.Code))}");
        }

        foreach (var (_, entry) in models)
        {
            var asked = AgentResolution.LevelFor(entry.Code, effort, effortByModel, definition?.Effort, definition?.EffortByModel);

            // A model whose entry takes no efforts is refused where the configuration is loaded; nothing is left
            // to say of it here.
            if (asked is not { } level || entry.Efforts is [] || entry.Efforts.Contains(level.Level, StringComparer.Ordinal))
            {
                continue;
            }

            var takes = string.Join(", ", entry.Efforts);
            var attached = level.From is LevelSource.StepEntry or LevelSource.StepAll
                or LevelSource.DefinitionEntry or LevelSource.DefinitionAll;
            var first = tier is null
                ? $"{where} runs at effort '{level.Level}', which '{entry.Code}' does not take. It takes: {takes}"
                : attached
                    ? $"{where} runs '{entry.Code}' (tier '{tier}') at effort '{level.Level}', which it does not take. It takes: {takes}"
                    : $"{where} runs at effort '{level.Level}', which '{entry.Code}' (tier '{tier}') does not take. It takes: {takes}";

            // Where the level came attached to this model by its own entry - the step's or the definition's - the
            // first sentence stands alone: it already has a level of its own, and the level is what is wrong. Where
            // it came from a blanket, the step or its definition is shown how to give this model another.
            var own = level.From is LevelSource.StepEntry or LevelSource.DefinitionEntry;
            var fromDefinition = level.From is LevelSource.DefinitionEntry
                or LevelSource.DefinitionAll
                or LevelSource.DefinitionOne;

            problems.Add(own
                ? first
                : $"{first}. {giveOwn(entry.Code, Nearest(entry, level.Level), fromDefinition)}");
        }

        return problems;
    }

    /// <summary>Every code an effort map names, the step's and its definition's, each once.</summary>
    private static IEnumerable<string> CodesOf(IReadOnlyDictionary<string, string>? effortByModel, IReadOnlyDictionary<string, string>? definitionByModel)
    {
        foreach (var code in (effortByModel ?? EmptyEfforts).Keys.Concat((definitionByModel ?? EmptyEfforts).Keys).Distinct(StringComparer.Ordinal))
        {
            yield return code;
        }
    }

    /// <summary>
    /// The nearest level the model takes at or above <paramref name="asked"/>, in the capability ladder, else its
    /// highest - the highest it takes by the ladder, whatever order its entry lists them in: what a refusal suggests
    /// giving the model under the step's effort.
    /// </summary>
    private static string Nearest(AgentModelConfig model, string asked)
    {
        var at = CapabilityLadder.ToList().IndexOf(asked);

        foreach (var level in CapabilityLadder.Skip(at < 0 ? CapabilityLadder.Count : at))
        {
            if (model.Efforts.Contains(level, StringComparer.Ordinal))
            {
                return level;
            }
        }

        return CapabilityLadder.Where(level => model.Efforts.Contains(level, StringComparer.Ordinal)).Last();
    }
}
