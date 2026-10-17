using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The files a workflow's definition names, relative to its own directory: the one list both the check that each is
/// there (<c>validate-workflow</c>) and <c>init</c>, which writes the shipped files a definition names and no other,
/// read.
/// </summary>
public static class WorkflowFiles
{
    /// <summary>One file a definition names.</summary>
    /// <param name="File">The file, as the definition names it, relative to the workflow's directory.</param>
    /// <param name="What">What names it, as a problem about it says it.</param>
    /// <param name="IsSchema">Whether it is a step's schema, which is read as a JSON object as well as found.</param>
    public sealed record Named(string File, string What, bool IsSchema);

    /// <summary>
    /// The files the definition itself names, beside its steps: the bar for recommendations, the advisor's agent and
    /// prompt, and the reporter's agent with each of its two prompts.
    /// </summary>
    /// <param name="definition">A workflow's parsed definition.</param>
    public static IReadOnlyList<Named> OfDefinition(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var named = new List<Named>();

        if (definition.Recommendations is not null)
        {
            named.Add(new Named(definition.Recommendations, "the bar for recommendations", IsSchema: false));
        }

        if (definition.Advisor is { } advisor)
        {
            named.AddRange(AgentAndPrompt(advisor.Agent, advisor.Prompt, "the advisor"));
        }

        if (definition.Reporter is { } reporter)
        {
            named.AddRange(AgentAndPrompt(reporter.Agent, reporter.StatusPrompt, "the reporter's status prompt"));
            named.AddRange(AgentAndPrompt(reporter.Agent, reporter.AskPrompt, "the reporter's ask prompt"));
        }

        return named;
    }

    /// <summary>The files one step names: its agent, its prompt, its resume prompt, its schema and a pull request's body.</summary>
    /// <param name="step">A step of the definition.</param>
    /// <param name="where">The step, as a problem about one of its files names it.</param>
    public static IReadOnlyList<Named> OfStep(WorkflowStep step, string where)
    {
        ArgumentNullException.ThrowIfNull(step);

        var named = new List<Named>();

        if (step.Agent is not null)
        {
            named.Add(new Named(AgentFile(step.Agent), $"{where}'s agent", IsSchema: false));
        }

        if (step.Prompt is not null)
        {
            named.Add(new Named(step.Prompt, $"{where}'s prompt", IsSchema: false));
        }

        if (step.ResumePrompt is not null)
        {
            named.Add(new Named(step.ResumePrompt, $"{where}'s resume prompt", IsSchema: false));
        }

        if (step.Outputs is not null)
        {
            named.Add(new Named(step.Outputs, $"{where}'s outputs", IsSchema: true));
        }

        if (step.Body is not null)
        {
            named.Add(new Named(step.Body, $"{where}'s body", IsSchema: false));
        }

        return named;
    }

    /// <summary>
    /// Every step of the definition with its path, the flow's and then the recovery's, each group's own steps after
    /// it: the steps whose files the definition names.
    /// </summary>
    /// <param name="definition">A workflow's parsed definition.</param>
    public static IEnumerable<(string Path, WorkflowStep Step)> Steps(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return Walk(definition.Steps, parent: null).Concat(Walk(definition.Recover?.Steps ?? [], "recover"));
    }

    /// <summary>Every file the definition names, its own and every step's, each once, in the order they are named.</summary>
    /// <param name="definition">A workflow's parsed definition.</param>
    public static IReadOnlyList<string> All(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return
        [
            .. OfDefinition(definition)
                .Concat(Steps(definition).SelectMany(entry => OfStep(entry.Step, entry.Path)))
                .Select(named => named.File)
                .Distinct(StringComparer.Ordinal),
        ];
    }

    private static IReadOnlyList<Named> AgentAndPrompt(string agent, string prompt, string what)
        => [new Named(AgentFile(agent), $"the agent {what} runs", IsSchema: false), new Named(prompt, $"{what}'s prompt", IsSchema: false)];

    private static string AgentFile(string agent) => $"{HarnessLayout.WorkflowAgentsDirectoryName}/{agent}.md";

    private static IEnumerable<(string Path, WorkflowStep Step)> Walk(IReadOnlyList<WorkflowStep> steps, string? parent)
    {
        foreach (var step in steps)
        {
            var path = parent is null ? step.Name : $"{parent}.{step.Name}";

            yield return (path, step);

            foreach (var nested in Walk(step.Steps, path))
            {
                yield return nested;
            }
        }
    }
}
