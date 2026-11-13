using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The files a resolution reads a use's workflow by: the repository's own workflows root, and - where the resolution
/// is asked to - the remote sources it fetches and the cache entries their files are read in. The one bundle the
/// resolver is handed, so that each caller says where files come from once.
/// </summary>
/// <param name="Read">
/// Reads a workflow file by its path under the repository's workflows root, forward slashes, <c>a/b/b.yml</c> - null
/// where no file is there.
/// </param>
/// <param name="Recorded">
/// The commit this run's own record holds for a remote source as written, where one does; null where nothing is
/// recorded. A resolution that has one never resolves the ref again, so a tag or branch that moved since changes no
/// run already started.
/// </param>
/// <param name="Fetch">
/// Fetches a remote source as written to a commit and a cached directory; null where the resolution reads local
/// workflows only, so a remote source is refused saying that.
/// </param>
/// <param name="ReadCached">
/// Reads a workflow file by its path under a remote workflow's root, keyed by the remote workflow's own name; null
/// where none reads. A nested use inside a remote workflow resolves inside its cache entry, as a local one resolves
/// inside the repository.
/// </param>
public sealed record WorkflowResolution(
    Func<string, string?> Read,
    Func<string, string?>? Recorded = null,
    Func<string, Task<WorkflowRemoteResolution>>? Fetch = null,
    Func<string, string, string?>? ReadCached = null);

/// <summary>
/// The one owner of what a step that says <c>use:</c> is: it resolves every such step of a parsed definition into the
/// workflow it names - the called workflow's file read and parsed, its steps placed in the step's own, each marked with
/// the workflow its files are read under - so that the definition the engine runs, freezes and digests holds the whole
/// resolved tree and nothing else learns what a <c>use:</c> is.
/// </summary>
/// <remarks>
/// <para>
/// A used workflow is read as a definition of its own and embedded whole: its steps become the step's members, its
/// recovery is kept beside them for the engine to reach first, its declared outputs become the step's own once it ends,
/// and its inputs are the names the step's <c>with:</c> values fill. Its run-level keys - its budget, limits, advisor,
/// amend and answers - are never applied, and are said as not applied, for the calling run's own are what hold.
/// </para>
/// <para>
/// A use names a remote workflow as <c>owner/repo/path/workflow.yml@ref</c>, fetched into the workflow cache and read
/// from it; a remote workflow's own name under <c>remote/</c> is the cache's reserved place, so a local use that
/// resolves there is refused. V58, the cycle rule: a workflow that uses itself, directly or through others, is refused
/// with the whole chain of the uses that close the circle. V59, the depth rule: nesting counts across workflows as it
/// counts within one, the reader's own bound of <see cref="WorkflowFileParser.MaxDepth"/> levels in all.
/// </para>
/// </remarks>
public static class WorkflowResolver
{
    /// <summary>
    /// Resolves every <c>use:</c> step of <paramref name="definition"/> that names a local workflow: the definition it
    /// returns holds the called workflows' steps in the places the engine runs them, and no
    /// <see cref="WorkflowStep.UseSource"/> of its own. A step it cannot resolve is a problem, said with the step's
    /// path; a definition with problems is returned beside them, its use steps left as written.
    /// </summary>
    /// <param name="definition">The workflow as it was parsed, its use steps still paths.</param>
    /// <param name="directory">
    /// The calling workflow's own directory, relative to the repository's workflows root and written with forward
    /// slashes - empty where the workflow sits in the root itself.
    /// </param>
    /// <param name="readFile">
    /// Reads a workflow file by its path under the repository's workflows root, forward slashes, <c>a/b/b.yml</c> -
    /// null where no file is there.
    /// </param>
    public static (WorkflowDefinition Definition, IReadOnlyList<string> Problems) Resolve(
        WorkflowDefinition definition,
        string directory,
        Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(readFile);

        // Local files only: no fetch is awaited, so the task is done where it is made and the wait is only that.
        var (resolved, problems, _) = ResolveAsync(definition, directory, new WorkflowResolution(readFile)).GetAwaiter().GetResult();

        return (resolved, problems);
    }

    /// <summary>
    /// Resolves every <c>use:</c> step of <paramref name="definition"/>: the local ones as <see cref="Resolve"/> does,
    /// and the remote ones through <paramref name="reads"/>'s fetch, whose commit is the recorded one where the run
    /// has recorded it. The definition it returns holds the called workflows' steps in the places the engine runs
    /// them, and no <see cref="WorkflowStep.UseSource"/> of its own; the remote resolutions it made are said beside
    /// the problems, for the lines that report how each came about.
    /// </summary>
    /// <param name="definition">The workflow as it was parsed, its use steps still sources.</param>
    /// <param name="directory">
    /// The calling workflow's own directory, relative to the repository's workflows root and written with forward
    /// slashes - empty where the workflow sits in the root itself.
    /// </param>
    /// <param name="reads">Where every file a resolution reads comes from.</param>
    public static async Task<(WorkflowDefinition Definition, IReadOnlyList<string> Problems, IReadOnlyList<WorkflowRemoteResolution> Remotes)> ResolveAsync(
        WorkflowDefinition definition,
        string directory,
        WorkflowResolution reads)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(reads);

        var problems = new List<string>();
        var remotes = new List<WorkflowRemoteResolution>();
        var resolved = await ResolveStepsAsync(definition.Steps, directory, [], depth: 1, reads, problems, remotes).ConfigureAwait(false);
        var recovery = definition.Recover is null
            ? null
            : definition.Recover with
            {
                Steps = [.. await ResolveStepsAsync(definition.Recover.Steps, directory, [], depth: 1, reads, problems, remotes).ConfigureAwait(false)],
            };
        var root = definition with { Steps = [.. resolved], Recover = recovery };

        foreach (var input in definition.Inputs.Where(input => input.Kind is FlowInput.ValueKind or FlowInput.StepKind))
        {
            problems.Add($"input '{input.Name}' is of the kind '{input.Kind}', which a run is never started with: a workflow's own inputs are '{FlowInput.TextKind}' or '{FlowInput.FileKind}', and '{FlowInput.ValueKind}' and '{FlowInput.StepKind}' are a used workflow's, filled by the with of the step that uses it");
        }

        return (root, problems, remotes);
    }

    /// <summary>
    /// Resolves the use steps among <paramref name="steps"/>, leaving every other step as it is but for the workflow
    /// mark a used workflow's members carry; a group's own steps are walked whatever the group is, so a use nested in a
    /// group of the caller's resolves as a top-level one does.
    /// </summary>
    /// <param name="steps">The steps as parsed.</param>
    /// <param name="directory">The directory the steps' workflow sits in, relative to the root it resolves in.</param>
    /// <param name="chain">The workflows already entered, outermost first, to refuse a use that closes a circle.</param>
    /// <param name="depth">How many workflows deep this level sits, the calling workflow's own file the first.</param>
    /// <param name="reads">Where every file a resolution reads comes from.</param>
    /// <param name="problems">Every problem found, whole, in the order the tree declares what they concern.</param>
    /// <param name="remotes">Each remote resolution made on the way, in the order the tree declared the uses.</param>
    private static async Task<List<WorkflowStep>> ResolveStepsAsync(
        IReadOnlyList<WorkflowStep> steps,
        string directory,
        IReadOnlyList<string> chain,
        int depth,
        WorkflowResolution reads,
        List<string> problems,
        List<WorkflowRemoteResolution> remotes)
    {
        var resolved = new List<WorkflowStep>();

        foreach (var step in steps)
        {
            if (step.UseSource is not null)
            {
                resolved.Add(await ResolveUseAsync(step, directory, chain, depth, reads, problems, remotes).ConfigureAwait(false) ?? step);
                continue;
            }

            resolved.Add(step.Steps.Count == 0 ? step : step with { Steps = [.. await ResolveStepsAsync(step.Steps, directory, chain, depth, reads, problems, remotes).ConfigureAwait(false)] });
        }

        return resolved;
    }

    /// <summary>Resolves one use step: its workflow read, parsed, resolved in its turn and placed in the step.</summary>
    private static async Task<WorkflowStep?> ResolveUseAsync(
        WorkflowStep step,
        string directory,
        IReadOnlyList<string> chain,
        int depth,
        WorkflowResolution reads,
        List<string> problems,
        List<WorkflowRemoteResolution> remotes)
    {
        var where = step.Name;

        if (WorkflowRemote.Source(step.UseSource!) is { } remote)
        {
            return await ResolveRemoteAsync(step, remote, chain, depth, reads, problems, remotes).ConfigureAwait(false);
        }

        if (ResolvedPath(directory, step.UseSource!) is not { } path)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', which is no path to a workflow file: a use is a path relative to the calling workflow's directory, written with forward slashes, that resolves inside the repository's workflows directory");
            return null;
        }

        if (!path.EndsWith(HarnessLayout.WorkflowFileExtension, StringComparison.Ordinal))
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', which does not name a '{HarnessLayout.WorkflowFileExtension}' file");
            return null;
        }

        if (path.StartsWith($"{WorkflowRemote.Prefix}/", StringComparison.Ordinal))
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', which resolves to '{path}' under the workflows directory: '{WorkflowRemote.Prefix}/' is the workflow cache's own reserved place, where a remote workflow's files are read, and no local use resolves there");
            return null;
        }

        var name = path[..path.LastIndexOf('/')].Replace('\\', '/');

        if (chain.Contains(name, StringComparer.Ordinal))
        {
            // V58, said with the whole chain: each workflow by its name, the first said twice to close the circle.
            problems.Add($"step '{where}' uses '{step.UseSource}', the workflow '{name}', which is already in use around it: {string.Join(" -> ", [.. chain, name, chain[0]])}. A workflow that uses itself, directly or through others, would run its own steps without end");
            return null;
        }

        if (chain.Count + 1 >= WorkflowFileParser.MaxDepth)
        {
            // V59: nesting counts across workflows, the reader's own bound, so that no definition reaches the engine
            // deeper than its reader takes.
            problems.Add($"step '{where}' uses '{step.UseSource}' {chain.Count + 2} workflows deep, counting the calling one's own; workflows nest at most {WorkflowFileParser.MaxDepth} deep in all, within one file and across the uses of others");
            return null;
        }

        if (Read(reads, string.Empty, path) is not { } text)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', and no workflow file is at '{path}' under the repository's workflows directory");
            return null;
        }

        WorkflowDefinition called;

        try
        {
            called = WorkflowFileParser.Parse(path, text);
        }
        catch (HarnessException refused)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', whose file does not read: {refused.Message}");
            return null;
        }

        return await PlaceAsync(step, called, text, name, string.Empty, path, chain, depth, reads, problems, remotes, fingerprint: null).ConfigureAwait(false);
    }

    /// <summary>Resolves one use step that names a remote workflow: fetched, or read from the cache at its recorded commit.</summary>
    private static async Task<WorkflowStep?> ResolveRemoteAsync(
        WorkflowStep step,
        WorkflowRemoteSource source,
        IReadOnlyList<string> chain,
        int depth,
        WorkflowResolution reads,
        List<string> problems,
        List<WorkflowRemoteResolution> remotes)
    {
        var where = step.Name;

        if (reads.Fetch is null)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', a workflow in the repository '{source.Repository}', and no resolution of this kind fetches one: the workflows this reads are the repository's own");
            return null;
        }

        WorkflowRemoteResolution resolution;

        try
        {
            resolution = await reads.Fetch(step.UseSource!).ConfigureAwait(false);
        }
        catch (HarnessException refused)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', which could not be resolved: {refused.Message}");
            return null;
        }

        var name = WorkflowRemote.Name(source, resolution.Commit);

        if (chain.Contains(name, StringComparer.Ordinal))
        {
            // V58, said with the whole chain: each workflow by its name, the first said twice to close the circle.
            problems.Add($"step '{where}' uses '{step.UseSource}', the workflow '{source.Repository}' at '{resolution.Commit}', which is already in use around it: {string.Join(" -> ", [.. chain, name, chain[0]])}. A workflow that uses itself, directly or through others, would run its own steps without end");
            return null;
        }

        if (chain.Count + 1 >= WorkflowFileParser.MaxDepth)
        {
            // V59: nesting counts across workflows, the reader's own bound, so that no definition reaches the engine
            // deeper than its reader takes.
            problems.Add($"step '{where}' uses '{step.UseSource}' {chain.Count + 2} workflows deep, counting the calling one's own; workflows nest at most {WorkflowFileParser.MaxDepth} deep in all, within one file and across the uses of others");
            return null;
        }

        WorkflowDefinition called;

        try
        {
            called = WorkflowFileParser.Parse(source.File, resolution.Definition);
        }
        catch (HarnessException refused)
        {
            problems.Add($"step '{where}' uses '{step.UseSource}', whose file does not read: {refused.Message}");
            return null;
        }

        remotes.Add(resolution);

        return await PlaceAsync(step, called, resolution.Definition, name, name, source.File, chain, depth, reads, problems, remotes, resolution.Commit).ConfigureAwait(false);
    }

    /// <summary>
    /// Places a called workflow's steps in the use step's own: its members resolved in their turn - inside its root,
    /// a remote one's cache entry or the repository's own directory - marked with the workflow their files are read
    /// under, and the <see cref="FlowUse"/> the step becomes holding everything of it the run keeps.
    /// </summary>
    /// <param name="step">The use step as parsed.</param>
    /// <param name="called">The called workflow, parsed.</param>
    /// <param name="text">The called workflow's own yml.</param>
    /// <param name="name">The called workflow's name: its directory under the repository's root, or its cache place.</param>
    /// <param name="root">
    /// The root the called workflow's own uses resolve in: empty for the repository's own, the remote workflow's own
    /// name for one read from the cache.
    /// </param>
    /// <param name="file">The called workflow's file, as its root spells it.</param>
    /// <param name="chain">The workflows already entered, outermost first.</param>
    /// <param name="depth">How many workflows deep this level sits.</param>
    /// <param name="reads">Where every file a resolution reads comes from.</param>
    /// <param name="problems">Every problem found, whole.</param>
    /// <param name="remotes">Each remote resolution made on the way.</param>
    /// <param name="fingerprint">
    /// The SHA-256 of a local called workflow's file, or the commit a remote one resolved to; null where the local
    /// file has not been read yet.
    /// </param>
    private static async Task<WorkflowStep?> PlaceAsync(
        WorkflowStep step,
        WorkflowDefinition called,
        string text,
        string name,
        string root,
        string file,
        IReadOnlyList<string> chain,
        int depth,
        WorkflowResolution reads,
        List<string> problems,
        List<WorkflowRemoteResolution> remotes,
        string? fingerprint)
    {
        var where = step.Name;
        var inner = new List<string>(chain) { name };
        var calledSteps = (await ResolveStepsAsync(called.Steps, DirectoryOf(called, file), inner, depth + 1, reads, problems, remotes).ConfigureAwait(false))
            .Select(member => Marked(member, name))
            .ToList();
        var calledRecover = called.Recover is null ? null : called.Recover with
        {
            Steps = [.. (await ResolveStepsAsync(called.Recover.Steps, DirectoryOf(called, file), inner, depth + 1, reads, problems, remotes).ConfigureAwait(false))
                .Select(member => Marked(member, name))],
        };

        foreach (var parameter in called.Params.Where(parameter => parameter.Required && parameter.Default is null))
        {
            problems.Add($"step '{where}' uses the workflow '{name}', whose param '{parameter.Name}' is required and declares no default: a used workflow is given nothing but its inputs, so it holds the param's own value or none");
        }

        var notApplied = new List<string>();
        var own = step.UseParentLimits == false ? OwnOf(called) : null;

        if (own is null)
        {
            if (called.Budget is not null)
            {
                notApplied.Add("budget");
            }

            if (called.Advisor is not null)
            {
                notApplied.Add("advisor");
            }

            if (called.Reporter is not null)
            {
                notApplied.Add("reporter");
            }

            if (called.Amend is not null)
            {
                notApplied.Add("amend");
            }

            if (called.Answers.Count > 0)
            {
                notApplied.Add("answers");
            }

            if (new[] { called.MaxReasoningAgents, called.MaxParallelProcesses, called.MaxOpenAgents, called.MaxRetries, called.MaxInstances, called.HandoffSeconds, called.BackoffSeconds }.Any(limit => limit is not null))
            {
                notApplied.Add("limits");
            }

            if (called.CodebaseGraph is not null)
            {
                notApplied.Add("codebaseGraph");
            }
        }

        var use = new FlowUse(
            step.UseSource!,
            name,
            fingerprint ?? FingerprintOf(text),
            called.Inputs,
            called.Params,
            called.Outputs,
            calledRecover,
            notApplied,
            own);

        return step with
        {
            UseSource = null,
            Use = use,
            Steps = calledSteps,
        };
    }

    /// <summary>
    /// The called workflow's run-level settings, carried for a use that says <c>useParentLimits: false</c> and applied
    /// to its steps. An amend is never carried, for an amend is a run's own - the definition's check refuses the use
    /// that would apply one.
    /// </summary>
    private static FlowUseOwn OwnOf(WorkflowDefinition called)
        => new(
            called.Budget,
            called.MaxReasoningAgents,
            called.MaxParallelProcesses,
            called.MaxOpenAgents,
            called.MaxRetries,
            called.BackoffSeconds,
            called.MaxInstances,
            called.HandoffSeconds,
            called.Advisor,
            called.Reporter,
            called.Answers.Count == 0 ? null : called.Answers,
            called.CodebaseGraph,
            called.Amend is not null);

    /// <summary>
    /// Reads a workflow file by its path under <paramref name="root"/>: the repository's own workflows root where the
    /// root is empty, a remote workflow's cache entry where it names one.
    /// </summary>
    private static string? Read(WorkflowResolution reads, string root, string path)
        => root.Length == 0 ? reads.Read(path) : reads.ReadCached?.Invoke(root, path);

    /// <summary>The directory the called workflow's own uses resolve from: its file's path minus its own name.</summary>
    private static string DirectoryOf(WorkflowDefinition called, string file)
        => file.LastIndexOf('/') is var at && at > 0 ? file[..at] : string.Empty;

    /// <summary>
    /// A member of a used workflow, marked with the workflow its files are read under - it and every step nested in
    /// it, a group's members as much as the workflow's own; a member a nested use marked already, of a workflow deeper
    /// still, keeps the mark of that one.
    /// </summary>
    private static WorkflowStep Marked(WorkflowStep step, string name)
        => step with
        {
            WorkflowOf = step.WorkflowOf ?? name,
            Steps = [.. step.Steps.Select(member => Marked(member, name))],
        };

    /// <summary>
    /// The path a use names, under the repository's workflows root and written with forward slashes: the caller's
    /// directory joined to the source, <c>.</c> and <c>..</c> resolved, and null where the path leaves the root or is
    /// no relative path at all.
    /// </summary>
    private static string? ResolvedPath(string directory, string source)
    {
        if (source.Length == 0 || source.Contains(':') || source.StartsWith('/') || source.StartsWith('\\'))
        {
            return null;
        }

        var parts = new List<string>();

        foreach (var part in (directory.Length == 0 ? source : $"{directory}/{source}").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part)
            {
                case ".":
                    break;
                case "..":
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }

        return parts.Count == 0 || parts.Any(part => part is "." or "..") ? null : string.Join("/", parts);
    }

    /// <summary>The SHA-256 of a workflow file's text, as the run records the resolution of the workflow it uses.</summary>
    private static string FingerprintOf(string text)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
