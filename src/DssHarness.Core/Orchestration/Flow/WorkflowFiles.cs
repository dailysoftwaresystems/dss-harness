using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The files a workflow's definition names, relative to its own directory: the one list the check that each is there
/// (<c>validate-workflow</c>), <c>init</c>, which writes the shipped files a definition names and no other, and
/// <c>export-workflow</c>, which copies them, read.
/// </summary>
public static class WorkflowFiles
{
    /// <summary>One file a definition names.</summary>
    /// <param name="File">The file, as the definition names it, relative to the workflow's directory.</param>
    /// <param name="What">What names it, as a problem about it says it.</param>
    /// <param name="IsSchema">Whether it is a step's schema, which is held to the subset the engine reads as well as found.</param>
    public sealed record Named(string File, string What, bool IsSchema);

    /// <summary>
    /// Why a schema the definition names is not one the engine reads, each problem
    /// <see cref="Schemas.JsonSchemaSubset.TextProblems"/> says on its own line naming the file and what names it; none
    /// where it is one. Said where a repository's workflow is checked and where this build's own is read, so a
    /// definition never holds over a schema no answer could be held to.
    /// </summary>
    /// <param name="named">The schema, as the definition names it.</param>
    /// <param name="text">Its text.</param>
    public static IReadOnlyList<string> SchemaProblems(Named named, string text)
    {
        ArgumentNullException.ThrowIfNull(named);

        return [.. Schemas.JsonSchemaSubset.TextProblems(text).Select(problem => $"'{named.File}', {named.What}, is not a schema the engine reads: {problem}")];
    }

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

        if (step.Prompt is { } prompt && !prompt.Contains('{'))
        {
            named.Add(new Named(prompt, $"{where}'s prompt", IsSchema: false));
        }

        if (step.ResumePrompt is { } resumePrompt && !resumePrompt.Contains('{'))
        {
            named.Add(new Named(resumePrompt, $"{where}'s resume prompt", IsSchema: false));
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

    /// <summary>
    /// Every file a resolved definition names - its own, every step's, and each file a use fills a file-kind input of
    /// the workflow it uses with - each once, spelled relative to the workflow's own directory: a file of a used
    /// workflow's step carries that workflow's own place under this one, so one flat list names every file of the set
    /// the way it sits on the disk.
    /// </summary>
    /// <param name="resolved">The definition as the resolver placed every use in.</param>
    public static IReadOnlyList<string> AllUnder(WorkflowDefinition resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var files = new List<string>();

        string Under(string? owner, string file)
            => owner is null || owner == resolved.Name ? file : $"{owner[(resolved.Name.Length + 1)..]}/{file}";

        files.AddRange(OfDefinition(resolved).Select(named => Under(null, named.File)));

        foreach (var (path, step) in Steps(resolved))
        {
            foreach (var named in OfStep(step, $"step '{path}'"))
            {
                files.Add(Under(step.WorkflowOf, named.File));
            }

            if (step.UseSource is { } source)
            {
                // A use the resolver could not place still names the file of the workflow it uses: that file is one
                // of the set wherever the workflow it names is read from.
                files.Add(Under(step.WorkflowOf, source.TrimStart('.', '/')));
            }

            if (step.Use is { } placed)
            {
                // The file of the workflow a use places - its yml, as the source spelled it - is one of the set too,
                // beside every file the workflow it places names.
                files.Add(Under(step.WorkflowOf, placed.Source.TrimStart('.', '/')));
            }

            if (step.Use is { } use)
            {
                // The file a use fills a file-kind input with is a file of the used workflow's directory, as every
                // file a step of it names is.
                foreach (var input in use.Inputs.Where(input => input.Kind == FlowInput.FileKind))
                {
                    var filled = step.With.TryGetValue(input.Name, out var with) ? with : input.Default;

                    if (filled is { Length: > 0 } && !filled.Contains('{'))
                    {
                        files.Add(Under(use.Name, filled));
                    }
                }
            }
        }

        return [.. files.Distinct(StringComparer.Ordinal)];
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
