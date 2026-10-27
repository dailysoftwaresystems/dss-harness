using DssHarness.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One agent definition, as a workflow's <c>agents/&lt;name&gt;.md</c> file declares it (part 03, "Agent definitions"):
/// markdown whose frontmatter declares the agent and whose body is the system prompt, verbatim. The level below a
/// step's: where a step names a tier, a model, an effort or a client, its own wins; where it names none, the
/// definition's stands. A definition names no fallback - that is the order of the tier's models in <c>tierConfig</c>.
/// </summary>
/// <param name="Name">The agent's name: the file's, held to the orchestration name rule.</param>
/// <param name="Description">What the agent is for, shown wherever agents are listed.</param>
/// <param name="Prompt">The body after the frontmatter: the system prompt, verbatim.</param>
/// <param name="Tier">The tier a step that names neither a tier nor a model runs on; null where the definition names none.</param>
/// <param name="Model">The model outright, whose provider, key and client follow from it; null where the definition names none.</param>
/// <param name="Effort">One effort level, for every model the agent may run on; null where it gives none this way.</param>
/// <param name="EffortByModel">A level for each model by its code, <c>all</c> for the rest; null where it gives none this way.</param>
/// <param name="Client">The client to reach the model through, where it is not the model's provider's own.</param>
/// <param name="MaxTurns">A limit, where the definition names one.</param>
/// <param name="MaxBudgetUsd">A limit, where the definition names one.</param>
/// <param name="MaxMinutes">A limit, where the definition names one.</param>
/// <param name="StallSeconds">A limit, where the definition names one.</param>
/// <param name="Capabilities">What the agent may do: of read, write, run and web. All four where the definition names none.</param>
/// <param name="Writes">Whether its worktree is writable; true where the definition names nothing.</param>
/// <param name="Restrictions">Rules added to the configured ones, never removing one.</param>
public sealed record AgentDefinition(
    string Name,
    string Description,
    string Prompt,
    string? Tier = null,
    string? Model = null,
    string? Effort = null,
    IReadOnlyDictionary<string, string>? EffortByModel = null,
    string? Client = null,
    int? MaxTurns = null,
    decimal? MaxBudgetUsd = null,
    int? MaxMinutes = null,
    int? StallSeconds = null,
    IReadOnlyList<string>? Capabilities = null,
    bool Writes = true,
    IReadOnlyList<string>? Restrictions = null)
{
    /// <summary>Every capability an agent may be given, as help names them.</summary>
    public static readonly IReadOnlyList<string> AllCapabilities = ["read", "write", "run", "web"];

    /// <summary>What the agent may do: the capabilities it names, or all four where it names none.</summary>
    public IReadOnlyList<string> May => Capabilities ?? AllCapabilities;

    /// <summary>Rules added to the configured ones, never removing one.</summary>
    public IReadOnlyList<string> AddedRules => Restrictions ?? [];
}

/// <summary>
/// Reads an agent definition's file: its frontmatter split, the YAML handed to the reader every file of a workflow is
/// read by (<see cref="YamlReading"/>), and the body kept as the system prompt, verbatim. Every problem the file has
/// is said at once, each with the line it stands on - and each key of another tool's agent files is refused naming the
/// key of this tool's that says it, as the retired keys of a step are.
/// </summary>
public static class AgentDefinitionParser
{
    /// <summary>
    /// One definition's file, read whole.
    /// </summary>
    /// <param name="text">The file's whole text.</param>
    /// <param name="agent">The agent's name, as a step names it: the file's name, which the definition must say again.</param>
    /// <returns>The definition, or null where the file does not read; and every problem, each with its line.</returns>
    public static (AgentDefinition? Definition, IReadOnlyList<string> Problems) Parse(string text, string agent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);

        var problems = new List<string>();
        var (frontmatter, prompt) = Split(text);

        if (frontmatter is null)
        {
            problems.Add("the file holds no frontmatter: '---' on the first line, the agent's keys, '---' again, and the system prompt after it");
            return (null, problems);
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            problems.Add("the file holds no prompt: the body after its frontmatter is the agent's system prompt, verbatim");
        }

        // The frontmatter's first line is the file's second, so a blank line before it puts every problem's line where
        // the file has it.
        var root = YamlReading.Root(Environment.NewLine + frontmatter, "an agent's frontmatter", problems);

        if (root is null)
        {
            return (null, problems);
        }

        var keys = WorkflowFileParser.CollectKeys(root, "an agent", Known, problems, ClaudeKey);

        string? name = null;
        string? description = null;
        string? tier = null;
        string? model = null;
        string? client = null;
        (string? Effort, IReadOnlyDictionary<string, string>? ByModel) effort = (null, null);
        int? maxTurns = null;
        decimal? maxBudgetUsd = null;
        int? maxMinutes = null;
        int? stallSeconds = null;
        List<string>? capabilities = null;
        bool? writes = null;
        var restrictions = new List<string>();

        foreach (var (key, value) in keys)
        {
            switch (key)
            {
                case "name":
                    name = YamlReading.RequireScalar(value, "name", problems);
                    break;

                case "description":
                    description = YamlReading.RequireScalar(value, "description", problems);
                    break;

                case "tier":
                    tier = YamlReading.RequireScalar(value, "tier", problems);
                    break;

                case "model":
                    model = YamlReading.RequireScalar(value, "model", problems);
                    break;

                case "effort":
                    effort = WorkflowFileParser.ReadEffort(value, "effort", problems);
                    break;

                case "client":
                    client = YamlReading.RequireScalar(value, "client", problems);
                    break;

                case "maxTurns":
                    maxTurns = Whole(value, "maxTurns", problems);
                    break;

                case "maxBudgetUsd":
                    maxBudgetUsd = Amount(value, "maxBudgetUsd", problems);
                    break;

                case "maxMinutes":
                    maxMinutes = Whole(value, "maxMinutes", problems);
                    break;

                case "stallSeconds":
                    stallSeconds = Whole(value, "stallSeconds", problems);
                    break;

                case "capabilities":
                    capabilities = [.. ListOf(value, "capabilities", AgentDefinition.AllCapabilities, problems)];
                    break;

                case "writes":
                    writes = YamlReading.RequireBoolean(value, "writes", problems);
                    break;

                case "restrictions":
                    restrictions.AddRange(ListOf(value, "restrictions", null, problems));
                    break;
            }
        }

        if (name is null)
        {
            problems.Add("'name' is named nowhere; it is required.");
        }
        else if (OrchestrationRules.NameProblem(name) is { } invalid)
        {
            problems.Add($"name '{name}': {invalid}");
        }
        else if (!string.Equals(name, agent, StringComparison.Ordinal))
        {
            problems.Add($"the agent's name is '{name}', which is not the file's ('{agent}'); a definition is named by its file");
        }

        if (description is null)
        {
            problems.Add("'description' is named nowhere; it is required.");
        }

        if (tier is not null && model is not null)
        {
            problems.Add("'tier' and 'model' are named together: a definition says one - the tier's models in tierConfig's order, or the model outright");
        }

        if (problems.Count > 0)
        {
            return (null, problems);
        }

        return (
            new AgentDefinition(
                name!,
                description!,
                prompt ?? string.Empty,
                tier,
                model,
                effort.Effort,
                effort.ByModel,
                client,
                maxTurns,
                maxBudgetUsd,
                maxMinutes,
                stallSeconds,
                capabilities,
                writes ?? true,
                [.. restrictions]),
            problems);    }

    /// <summary>Every key an agent definition takes, in the order help lists them.</summary>
    public static readonly IReadOnlyList<string> Known =
        [
            "name", "description", "tier", "model", "effort", "client", "maxTurns", "maxBudgetUsd", "maxMinutes",
            "stallSeconds", "capabilities", "writes", "restrictions",
        ];

    /// <summary>
    /// A key of another tool's agent files, refused naming the key of this tool's that says it: the engine decides an
    /// agent's tools and its policy, and a skill is a step's prompt's to name. The retired keys of a step are refused
    /// here as there (<see cref="WorkflowFileParser.RetiredAgentKey"/>).
    /// </summary>
    private static string? ClaudeKey(string key)
        => WorkflowFileParser.RetiredAgentKey(key) ?? key switch
        {
            "tools" => "an agent's tools follow from its capabilities; say those",
            "disallowedTools" => "an agent's restrictions say what it may not run; agentPolicy.restrictions and a step's own add to them",
            "permissionMode" => "every call an agent makes is decided by this tool's policy; restrictions add to it",
            "skills" => "a step's prompt names one: {skill.<name>}",
            "mcpServers" => "the engine serves an agent its tools itself; none is named in a definition",
            "hooks" => "no agent of this tool takes hooks",
            _ => null,
        };

    /// <summary>The file's frontmatter and its body, split at the closing of the frontmatter; null frontmatter where the file opens with none.</summary>
    private static (string? Frontmatter, string? Prompt) Split(string text)
    {
        var opening = text.StartsWith("---\n", StringComparison.Ordinal) || text.StartsWith("---\r\n", StringComparison.Ordinal)
            ? text.IndexOf('\n') + 1
            : 0;

        if (opening == 0)
        {
            return (null, null);
        }

        var at = opening;

        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);

            if (text.AsSpan(at, (end < 0 ? text.Length : end) - at).TrimEnd('\r').SequenceEqual("---"))
            {
                return (text[opening..at], text[((end < 0 ? text.Length : end) + 1)..]);
            }

            if (end < 0)
            {
                break;
            }

            at = end + 1;
        }

        return (null, null);
    }

    /// <summary>A whole number at least one, as a limit is; null where the value does not read.</summary>
    private static int? Whole(YamlNode node, string what, List<string> problems)
    {
        var scalar = YamlReading.RequireScalar(node, what, problems);

        if (!int.TryParse(scalar, out var whole))
        {
            problems.Add(YamlReading.At(node, $"'{what}' is not a whole number; a limit is"));
            return null;
        }

        if (whole < 1)
        {
            problems.Add(YamlReading.At(node, $"'{what}' is {whole}; a limit is at least 1"));
            return null;
        }

        return whole;
    }

    /// <summary>An amount of money at least naught, as a budget is; null where the value does not read.</summary>
    private static decimal? Amount(YamlNode node, string what, List<string> problems)
    {
        var scalar = YamlReading.RequireScalar(node, what, problems);

        if (!decimal.TryParse(scalar, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount))
        {
            problems.Add(YamlReading.At(node, $"'{what}' is not a number of dollars; a budget is"));
            return null;
        }

        if (amount < 0)
        {
            problems.Add(YamlReading.At(node, $"'{what}' is below 0; a budget is at least that"));
            return null;
        }

        return amount;
    }

    /// <summary>A list of names, each of <paramref name="known"/> where it is given, none written twice; empty where the value does not read.</summary>
    private static IEnumerable<string> ListOf(YamlNode node, string what, IReadOnlyList<string>? known, List<string> problems)
    {
        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(YamlReading.At(node, $"'{what}' is not a list of names."));
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in sequence.Children)
        {
            var name = YamlReading.ScalarOf(item);

            if (name is null)
            {
                problems.Add(YamlReading.At(item, $"'{what}' holds a name that is not one value"));
                continue;
            }

            if (!seen.Add(name))
            {
                problems.Add(YamlReading.At(item, $"'{what}' names '{name}' more than once"));
                continue;
            }

            if (known is not null && !known.Contains(name, StringComparer.Ordinal))
            {
                problems.Add(YamlReading.At(item, $"'{what}' names '{name}'. Known: {string.Join(", ", known)}"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(YamlReading.At(item, $"'{what}' holds a name that is blank"));
                continue;
            }

            yield return name;
        }
    }
}
