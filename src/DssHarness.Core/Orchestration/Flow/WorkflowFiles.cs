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

    /// <summary>A use whose workflow sits beyond the directory of the workflow that uses it.</summary>
    /// <param name="Path">The use step's path, dotted from the flow's root.</param>
    /// <param name="Source">The use as written.</param>
    /// <param name="Remote">Whether it names a remote workflow; otherwise a local one outside the directory.</param>
    public sealed record Beyond(string Path, string Source, bool Remote);

    /// <summary>Every file a resolved definition names under its own directory, and every use whose workflow sits beyond it.</summary>
    /// <param name="Files">Each file, relative to the workflow's directory with '/', once, in the order they are named.</param>
    /// <param name="Beyond">Each use of a workflow outside the directory or of a remote one, in the order the tree declares them.</param>
    public sealed record Placed(IReadOnlyList<string> Files, IReadOnlyList<Beyond> Beyond);

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
    /// The files the definition itself names, beside its steps: the bar for recommendations, each text, the advisor's
    /// agent and prompt, and the reporter's agent with each of its two prompts.
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

        if (definition.Texts is { } texts)
        {
            foreach (var (name, file) in texts)
            {
                named.Add(new Named(file, $"the text '{name}'", IsSchema: false));
            }
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
    /// The files a use step names in the directory of the workflow it uses: each file it fills a file-kind input of that
    /// workflow with - its with value, else the input's default - each text the used workflow declares, and, where the
    /// use governs itself (<c>useParentLimits: false</c>), the agent and prompts of the advisor and of the reporter the
    /// used workflow declares; none for a step that uses nothing.
    /// </summary>
    /// <param name="step">A step of the resolved definition.</param>
    /// <param name="path">The step's path, as a problem about one of its files names it.</param>
    public static IReadOnlyList<Named> OfUse(WorkflowStep step, string path)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (step.Use is not { } use)
        {
            return [];
        }

        var named = new List<Named>();

        foreach (var input in use.Inputs.Where(input => input.Kind == FlowInput.FileKind))
        {
            var given = step.With.TryGetValue(input.Name, out var with);

            if ((given ? with : input.Default) is { Length: > 0 } filled)
            {
                named.Add(new Named(
                    filled,
                    given ? $"the input '{input.Name}' of '{use.Name}', as step '{path}' fills it" : $"the input '{input.Name}' of '{use.Name}', by its default",
                    IsSchema: false));
            }
        }

        foreach (var (name, file) in use.Texts ?? new Dictionary<string, string>())
        {
            named.Add(new Named(file, $"the text '{name}' of '{use.Name}'", IsSchema: false));
        }

        if (use.Own?.Advisor is { } advisor)
        {
            named.AddRange(AgentAndPrompt(advisor.Agent, advisor.Prompt, $"the advisor of '{use.Name}'"));
        }

        if (use.Own?.Reporter is { } reporter)
        {
            named.AddRange(AgentAndPrompt(reporter.Agent, reporter.StatusPrompt, $"the reporter of '{use.Name}''s status prompt"));
            named.AddRange(AgentAndPrompt(reporter.Agent, reporter.AskPrompt, $"the reporter of '{use.Name}''s ask prompt"));
        }

        return named;
    }

    /// <summary>
    /// Every step of the definition with its path, a group before its members - the flow's, the recovery's under
    /// <c>recover</c>, and each used workflow's recovery under the step that uses it: the steps whose files the
    /// definition names, walked by the one owner of every declared step (<see cref="FlowScope.Declared"/>), so no step a
    /// run can reach is left out of a check.
    /// </summary>
    /// <param name="definition">A workflow's parsed definition.</param>
    public static IEnumerable<(string Path, WorkflowStep Step)> Steps(WorkflowDefinition definition)
        => FlowScope.Declared(definition);

    /// <summary>
    /// Every file a resolved definition names, spelt relative to the workflow's own directory, and every use whose
    /// workflow sits beyond it. A file of a used workflow - its yml, each file a step of it names and each file the use
    /// names in it (<see cref="OfUse"/>) - carries that workflow's own place under this one (<see cref="PlaceOf"/>), so
    /// one flat list names every file of the set the way it sits on the disk; a use of a workflow outside the directory,
    /// or of a remote one, is said apart with nothing of it in the list, for no file of it is under the directory.
    /// </summary>
    /// <param name="resolved">The definition as the resolver placed every use in.</param>
    public static Placed AllUnder(WorkflowDefinition resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        var files = new List<string>();
        var beyond = new List<Beyond>();

        files.AddRange(OfDefinition(resolved).Select(named => named.File));

        foreach (var (path, step) in Steps(resolved))
        {
            // A step of a workflow beyond the directory is none of the set: the use that placed it is said apart.
            if (PlaceOf(resolved.Name, step.WorkflowOf) is not { } place)
            {
                continue;
            }

            files.AddRange(OfStep(step, $"step '{path}'").Select(named => Joined(place, named.File)));

            // A use left as written - a remote one that a resolution of the repository's own files did not fetch -
            // names no file under the directory.
            if (step.UseSource is { } source)
            {
                beyond.Add(new Beyond(path, source, Remote: !string.IsNullOrWhiteSpace(source) && WorkflowRemote.Source(source) is not null));
            }

            if (step.Use is not { } use)
            {
                continue;
            }

            if (PlaceOf(resolved.Name, use.Name) is not { } used)
            {
                beyond.Add(new Beyond(path, use.Source, WorkflowRemote.Is(use.Name)));
                continue;
            }

            // The used workflow's own yml, under its place by the file name its source ends in.
            files.Add(Joined(used, use.Source[(use.Source.LastIndexOf('/') + 1)..]));
            files.AddRange(OfUse(step, path).Select(named => Joined(used, named.File)));
        }

        return new Placed([.. files.Distinct(StringComparer.Ordinal)], beyond);
    }

    /// <summary>
    /// Where the workflow <paramref name="owner"/> sits under the directory of the workflow <paramref name="root"/>,
    /// with forward slashes: empty for the root's own, its path under the root's directory for one inside it, and null
    /// for one outside it or a remote one - the one placing of a used workflow's files under the workflow that uses it.
    /// </summary>
    /// <param name="root">The workflow whose directory the files are placed under, by its name.</param>
    /// <param name="owner">The workflow a file belongs to, as a step's mark names it; null for the root's own.</param>
    public static string? PlaceOf(string root, string? owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return owner is null || string.Equals(owner, root, StringComparison.Ordinal)
            ? string.Empty
            : !WorkflowRemote.Is(owner) && owner.StartsWith($"{root}/", StringComparison.Ordinal)
                ? owner[(root.Length + 1)..]
                : null;
    }

    /// <summary>A file of a workflow at <paramref name="place"/> under the root's directory, relative to that directory.</summary>
    private static string Joined(string place, string file) => place.Length == 0 ? file : $"{place}/{file}";

    private static IReadOnlyList<Named> AgentAndPrompt(string agent, string prompt, string what)
        => [new Named(AgentFile(agent), $"the agent {what} runs", IsSchema: false), new Named(prompt, $"{what}'s prompt", IsSchema: false)];

    private static string AgentFile(string agent) => $"{HarnessLayout.WorkflowAgentsDirectoryName}/{agent}.md";
}
