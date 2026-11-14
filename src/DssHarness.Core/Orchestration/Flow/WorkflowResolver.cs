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
/// where no file is there; a <see cref="HarnessException"/> saying why where the file is not one the resolution may
/// read (its directory or itself leading out of the workflows directory once links are followed) or could not be looked
/// at or read, which the resolution says as the use's problem, never as a file that is not there.
/// </param>
/// <param name="Fetch">
/// Fetches a remote source as written to a commit and a cached directory; null where the resolution reads local
/// workflows only, so a remote source is refused saying that - or left as written, where
/// <paramref name="RemoteLeftAsWritten"/> says so. A refusal of the source itself (<see cref="RefusesTheSource"/>) is the
/// use's problem; any other failure - the forge's tool missing, the forge not reached with nothing standing in, an answer
/// of the forge's that does not read, the workflow cache that could not be worked on - is no problem of the definition's,
/// and goes on with its own code.
/// </param>
/// <param name="ReadCached">
/// Reads a file of a remote workflow's cache entry by its path in the remote repository, keyed by the name of the
/// remote workflow the entry was fetched as; null where the entry names no such file, a
/// <see cref="HarnessException"/> where its blob changed on disk; and null as a whole where the resolution reads no
/// cache entry. A relative use inside a remote workflow resolves inside its cache entry through it, as a local one
/// resolves inside the repository.
/// </param>
/// <param name="PathComparison">
/// How the files the resolution reads are told apart, which a use that closes a circle is found by: the machine's path
/// comparison where they are a disk's, ordinal - the default - where they are a set read by its exact names, as this
/// build's embedded one is.
/// </param>
/// <param name="RemoteLeftAsWritten">
/// Whether a remote use is left as written, unresolved and no problem, where <paramref name="Fetch"/> is null: the
/// resolution asks only which of the repository's own files a definition names - <c>init</c>'s question, which no
/// remote workflow's file answers - so a remote use is neither fetched nor refused.
/// </param>
public sealed record WorkflowResolution(
    Func<string, string?> Read,
    Func<string, Task<WorkflowRemoteResolution>>? Fetch = null,
    Func<string, string, string?>? ReadCached = null,
    StringComparison PathComparison = StringComparison.Ordinal,
    bool RemoteLeftAsWritten = false)
{
    /// <summary>
    /// Whether a fetch's failure is a refusal of the source itself, said as the use's problem: a refusal
    /// (<see cref="HarnessExit.Refused"/>) - not allowed, a host or credential not named, a ref the pin refuses or that is
    /// not the repository's own, a file not in the directory, a forge that answered and refused - or a workflow file that
    /// is no text (<see cref="HarnessExit.ConfigInvalid"/>). Every other code is a fetch that could not run, which the
    /// fetcher raises with a code of its own (<see cref="WorkflowRemoteFetcher.ResolveAsync"/>): the one line between the
    /// two, which the resolution and every command that resolves a remote use read.
    /// </summary>
    /// <param name="failed">What the fetch raised.</param>
    public static bool RefusesTheSource(HarnessException failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        return failed.ExitCode is HarnessExit.Refused or HarnessExit.ConfigInvalid;
    }
}

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
/// and its inputs are the names the step's <c>with:</c> values fill. Its run-level keys are said as not applied where
/// no step applies them (<see cref="FlowUse.NotApplied"/>): its budget, reporter, <c>pushCheckpoints</c> and
/// <c>recommendations</c> always, its advisor, amend, answers, limits and <c>codebaseGraph</c> unless the use says
/// <c>useParentLimits: false</c>, which carries them (<see cref="FlowUse.Own"/>) for its steps - an amend refused there -
/// and its <c>codebaseGraph</c> even then where the run's own workflow declares one, whose shape the run's one graph has.
/// </para>
/// <para>
/// A use names a path relative to the calling workflow's directory - inside the repository's workflows directory for a
/// local workflow, inside the cache entry for one inside a remote workflow - or a remote workflow as
/// <c>owner/repo/path/workflow.yml@ref</c>, fetched into the workflow cache and read from it; a remote workflow's own
/// name under <c>remote/</c> is the cache's reserved place, so a local use that resolves there is refused, and a used
/// workflow lives in a directory of its own, as every workflow does. V58, the cycle rule: a workflow file that uses
/// itself, directly or through others, is refused with the circle of the files that close it, told apart by the
/// machine's path comparison, the calling workflow's own file the first. V59, the depth rule: steps nest at most
/// <see cref="WorkflowFileParser.MaxDepth"/> levels deep in all, a used workflow's own counted below the step that uses
/// it - the reader's own bound held across the uses of others, so that no definition that holds is deeper than the
/// run's records can be written.
/// </para>
/// </remarks>
public static class WorkflowResolver
{
    /// <summary>
    /// Resolves every <c>use:</c> step of <paramref name="definition"/> that names a local workflow, from a set read by
    /// its exact names - this build's own, or one held in memory: the definition it returns holds the called
    /// workflows' steps in the places the engine runs them, and no <see cref="WorkflowStep.UseSource"/> of its own. A
    /// step it cannot resolve is a problem, said with the step's path; a definition with problems is returned beside
    /// them, its use steps left as written.
    /// </summary>
    /// <param name="definition">The workflow as it was parsed, its use steps still paths.</param>
    /// <param name="directory">
    /// The calling workflow's own directory under the workflows root - its name, whose file is
    /// <c>&lt;name&gt;/&lt;name&gt;.yml</c> there.
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
    /// and the remote ones through <paramref name="reads"/>'s fetch. The definition it returns holds the called
    /// workflows' steps in the places the engine runs them, and no <see cref="WorkflowStep.UseSource"/> of its own; the
    /// remote resolutions it made are said beside the problems, for the lines that report how each came about.
    /// </summary>
    /// <param name="definition">The workflow as it was parsed, its use steps still sources.</param>
    /// <param name="directory">
    /// The calling workflow's own directory under the workflows root - its name, whose file is
    /// <c>&lt;name&gt;/&lt;name&gt;.yml</c> there.
    /// </param>
    /// <param name="reads">Where every file a resolution reads comes from.</param>
    /// <exception cref="HarnessException">
    /// A remote source could not be fetched for a reason that is no refusal of the source itself: the fetch's own code.
    /// </exception>
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
        var walk = new Walk(reads, problems, remotes, definition.CodebaseGraph is not null);

        // The circle a use closes is found by files, the calling workflow's own the first: a use of it from anywhere
        // in the tree is a use of itself.
        IReadOnlyList<string> chain = [$"{directory}/{directory[(directory.LastIndexOf('/') + 1)..]}{HarnessLayout.WorkflowFileExtension}"];
        var place = new Place(directory, Remote: null);
        var resolved = await ResolveStepsAsync(definition.Steps, place, scope: null, depth: 1, chain, walk).ConfigureAwait(false);
        var recovery = definition.Recover is null
            ? null
            : definition.Recover with
            {
                Steps = [.. await ResolveStepsAsync(definition.Recover.Steps, place, FlowScope.Recovery, depth: 1, chain, walk).ConfigureAwait(false)],
            };
        var root = definition with { Steps = [.. resolved], Recover = recovery };

        foreach (var input in definition.Inputs.Where(input => input.Kind is FlowInput.ValueKind or FlowInput.StepKind))
        {
            problems.Add($"input '{input.Name}' is of the kind '{input.Kind}', which a run is never started with: a workflow's own inputs are '{FlowInput.TextKind}' or '{FlowInput.FileKind}', and '{FlowInput.ValueKind}' and '{FlowInput.StepKind}' are a used workflow's, filled by the with of the step that uses it");
        }

        return (root, problems, remotes);
    }

    /// <summary>What one resolution reads by and gathers: where files come from, every problem and every remote resolution.</summary>
    /// <param name="Reads">Where every file a resolution reads comes from.</param>
    /// <param name="Problems">Every problem found, whole, in the order the tree declares what they concern.</param>
    /// <param name="Remotes">Each remote resolution made on the way, in the order the tree declared the uses.</param>
    /// <param name="RunShapesTheGraph">
    /// Whether the run's own workflow declares a <c>codebaseGraph</c>, whose shape the run's one graph then has
    /// (<see cref="FlowUse.GraphOf"/>), whatever a use that governs itself declares.
    /// </param>
    private sealed record Walk(WorkflowResolution Reads, List<string> Problems, List<WorkflowRemoteResolution> Remotes, bool RunShapesTheGraph);

    /// <summary>
    /// Where a level of steps is resolved: the directory its workflow sits in, forward slashes - under the repository's
    /// workflows root, or in a remote repository - and, for a workflow of a remote one, the resolution whose cache entry
    /// holds it.
    /// </summary>
    /// <param name="Directory">The workflow's directory, relative to the root it resolves in.</param>
    /// <param name="Remote">The remote resolution whose cache entry the workflow is read from; null for the repository's own.</param>
    private sealed record Place(string Directory, WorkflowRemoteResolution? Remote);

    /// <summary>
    /// Resolves the use steps among <paramref name="steps"/>, leaving every other step as it is but for the workflow
    /// mark a used workflow's members carry; a group's own steps are walked whatever the group is, so a use nested in a
    /// group of the caller's resolves as a top-level one does.
    /// </summary>
    /// <param name="steps">The steps as parsed.</param>
    /// <param name="place">Where the steps' workflow sits.</param>
    /// <param name="scope">The path of the group the steps are members of, dotted from the flow's root; null at the top.</param>
    /// <param name="depth">How deep the steps sit, counting every level from the flow's top, the top's own the first.</param>
    /// <param name="chain">The workflow files already entered, outermost first, to refuse a use that closes a circle.</param>
    /// <param name="walk">What the resolution reads by and gathers.</param>
    private static async Task<List<WorkflowStep>> ResolveStepsAsync(
        IReadOnlyList<WorkflowStep> steps,
        Place place,
        string? scope,
        int depth,
        IReadOnlyList<string> chain,
        Walk walk)
    {
        var resolved = new List<WorkflowStep>();

        foreach (var step in steps)
        {
            var path = scope is null ? step.Name : $"{scope}.{step.Name}";

            if (step.UseSource is not null)
            {
                resolved.Add(await ResolveUseAsync(step, path, place, depth, chain, walk).ConfigureAwait(false) ?? step);
                continue;
            }

            resolved.Add(step.Steps.Count == 0 ? step : step with { Steps = [.. await ResolveStepsAsync(step.Steps, place, path, depth + 1, chain, walk).ConfigureAwait(false)] });
        }

        return resolved;
    }

    /// <summary>Resolves one use step: its workflow read, parsed, resolved in its turn and placed in the step.</summary>
    private static async Task<WorkflowStep?> ResolveUseAsync(
        WorkflowStep step,
        string where,
        Place place,
        int depth,
        IReadOnlyList<string> chain,
        Walk walk)
    {
        var source = step.UseSource!;

        if (string.IsNullOrWhiteSpace(source))
        {
            walk.Problems.Add($"step '{where}' uses no workflow: its use is empty, and it is a path relative to the calling workflow's directory, or owner/repo/path/workflow.yml@ref");
            return null;
        }

        if (WorkflowRemote.Source(source) is { } remote)
        {
            return await ResolveRemoteAsync(step, where, remote, depth, chain, walk).ConfigureAwait(false);
        }

        if (SpellingProblem(source) is { } misspelt)
        {
            walk.Problems.Add($"step '{where}' uses '{source}', {misspelt}");
            return null;
        }

        var path = ResolvedPath(place.Directory, source);
        var entry = place.Remote?.Source.Directory;

        if (entry is not null && (path is null || !path.StartsWith($"{entry}/", StringComparison.Ordinal)))
        {
            walk.Problems.Add($"step '{where}' uses '{source}', which leaves '{entry}', the directory the remote workflow of '{place.Remote!.Source.Repository}' was fetched as: a use inside a remote workflow resolves inside the same cache entry, as a use inside a local one resolves inside the repository's workflows directory");
            return null;
        }

        if (path is null)
        {
            walk.Problems.Add($"step '{where}' uses '{source}', which is no path to a workflow file: a use is a path relative to the calling workflow's directory, written with forward slashes, that resolves inside the repository's workflows directory");
            return null;
        }

        if (!path.EndsWith(HarnessLayout.WorkflowFileExtension, StringComparison.Ordinal))
        {
            walk.Problems.Add($"step '{where}' uses '{source}', which does not name a '{HarnessLayout.WorkflowFileExtension}' file");
            return null;
        }

        if (place.Remote is null && path.StartsWith($"{WorkflowRemote.Prefix}/", StringComparison.Ordinal))
        {
            walk.Problems.Add($"step '{where}' uses '{source}', which resolves to '{path}' under the workflows directory: '{WorkflowRemote.Prefix}/' is the workflow cache's own reserved place, where a remote workflow's files are read, and no local use resolves there");
            return null;
        }

        if (!path.Contains('/', StringComparison.Ordinal))
        {
            walk.Problems.Add($"step '{where}' uses '{source}', which resolves to '{path}', a file of the workflows directory itself: a workflow lives in a directory of its own under it, where every file it names is read");
            return null;
        }

        var name = place.Remote is { } fetched
            ? WorkflowRemote.Name(fetched.Source with { File = path }, fetched.Commit)
            : path[..path.LastIndexOf('/')];
        var file = place.Remote is null ? path : $"{name}/{path[(path.LastIndexOf('/') + 1)..]}";

        if (Circle(chain, file, walk.Reads.PathComparison) is { } circle)
        {
            // V58, said with the circle: each workflow file from the first that is used again, closed by that one.
            walk.Problems.Add($"step '{where}' uses '{source}', the workflow '{name}', which is already in use around it: {circle}. A workflow that uses itself, directly or through others, would run its own steps without end");
            return null;
        }

        string? text;

        try
        {
            text = place.Remote is { } held
                ? walk.Reads.ReadCached is { } cached
                    ? cached(WorkflowRemote.Name(held.Source, held.Commit), path)
                    : throw new HarnessException(HarnessExit.ConfigInvalid, $"it is a workflow inside the remote workflow '{WorkflowRemote.Name(held.Source, held.Commit)}', and no resolution of this kind reads the cache entry that holds it")
                : walk.Reads.Read(path);
        }
        catch (HarnessException refused)
        {
            walk.Problems.Add($"step '{where}' uses '{source}', which could not be read: {refused.Message.TrimEnd('.')}");
            return null;
        }

        if (text is null)
        {
            walk.Problems.Add(place.Remote is { } absent
                ? $"step '{where}' uses '{source}', and the commit '{absent.Commit}' of '{absent.Source.Repository}' holds no workflow file at '{path}'"
                : $"step '{where}' uses '{source}', and no workflow file is at '{path}' under the repository's workflows directory");
            return null;
        }

        WorkflowDefinition called;

        try
        {
            called = WorkflowFileParser.Parse(path, text);
        }
        catch (HarnessException refused)
        {
            walk.Problems.Add($"step '{where}' uses '{source}', whose file does not read: {refused.Message}");
            return null;
        }

        if (DepthProblem(where, source, called, depth) is { } deep)
        {
            walk.Problems.Add(deep);
            return null;
        }

        // A local workflow's fingerprint is its parsed definition's digest, taken as the run's own definition's is, so
        // a change no reader can see - a comment, the line endings an editor or a checkout wrote - refuses a resume of
        // neither; a workflow of a remote one is its commit's, which names every byte of it.
        var fingerprint = place.Remote?.Commit ?? FlowRunContext.DigestOf(called);

        return await PlaceAsync(step, where, called, name, new Place(path[..path.LastIndexOf('/')], place.Remote), depth, [.. chain, file], walk, fingerprint).ConfigureAwait(false);
    }

    /// <summary>Resolves one use step that names a remote workflow: fetched, or read from the cache at its recorded commit.</summary>
    private static async Task<WorkflowStep?> ResolveRemoteAsync(
        WorkflowStep step,
        string where,
        WorkflowRemoteSource source,
        int depth,
        IReadOnlyList<string> chain,
        Walk walk)
    {
        if (walk.Reads.Fetch is null)
        {
            if (!walk.Reads.RemoteLeftAsWritten)
            {
                walk.Problems.Add($"step '{where}' uses '{step.UseSource}', a workflow in the repository '{source.Repository}', and no resolution of this kind fetches one: the workflows this reads are the repository's own");
            }

            return null;
        }

        WorkflowRemoteResolution resolution;

        try
        {
            resolution = await walk.Reads.Fetch(step.UseSource!).ConfigureAwait(false);
        }
        catch (HarnessException refused) when (WorkflowResolution.RefusesTheSource(refused))
        {
            // Only a refusal of the source itself is the definition's problem; a fetch that could not run - gh missing
            // (14), the forge not reached with nothing standing in (21, or 20 where gh said no connection was made), an
            // answer that does not read (20), the workflow cache that could not be worked on (21) - goes on with its own code.
            walk.Problems.Add($"step '{where}' uses '{step.UseSource}', which could not be resolved: {refused.Message}");
            return null;
        }

        var name = WorkflowRemote.Name(source, resolution.Commit);
        var file = $"{name}/{source.File[(source.File.LastIndexOf('/') + 1)..]}";

        if (Circle(chain, file, walk.Reads.PathComparison) is { } circle)
        {
            // V58, said with the circle: each workflow file from the first that is used again, closed by that one.
            walk.Problems.Add($"step '{where}' uses '{step.UseSource}', the workflow '{source.Repository}' at '{resolution.Commit}', which is already in use around it: {circle}. A workflow that uses itself, directly or through others, would run its own steps without end");
            return null;
        }

        WorkflowDefinition called;

        try
        {
            called = WorkflowFileParser.Parse(source.File, resolution.Definition);
        }
        catch (HarnessException refused)
        {
            walk.Problems.Add($"step '{where}' uses '{step.UseSource}', whose file does not read: {refused.Message}");
            return null;
        }

        if (DepthProblem(where, step.UseSource!, called, depth) is { } deep)
        {
            walk.Problems.Add(deep);
            return null;
        }

        walk.Remotes.Add(resolution);

        return await PlaceAsync(step, where, called, name, new Place(source.Directory, resolution), depth, [.. chain, file], walk, resolution.Commit).ConfigureAwait(false);
    }

    /// <summary>
    /// Places a called workflow's steps in the use step's own: its members resolved in their turn - inside the
    /// repository's own workflows directory, or inside a remote one's cache entry - marked with the workflow their
    /// files are read under, and the <see cref="FlowUse"/> the step becomes holding everything of it the run keeps.
    /// </summary>
    /// <param name="step">The use step as parsed.</param>
    /// <param name="where">The use step's path, dotted from the flow's root.</param>
    /// <param name="called">The called workflow, parsed.</param>
    /// <param name="name">The called workflow's name: its directory under the repository's root, or its cache place.</param>
    /// <param name="place">Where the called workflow sits, which its own uses resolve against.</param>
    /// <param name="depth">How deep the use step sits; its members sit one level below it.</param>
    /// <param name="chain">The workflow files already entered, outermost first, the called one's last.</param>
    /// <param name="walk">What the resolution reads by and gathers.</param>
    /// <param name="fingerprint">
    /// The digest of a local called workflow's parsed definition, or the commit a remote one resolved to.
    /// </param>
    private static async Task<WorkflowStep?> PlaceAsync(
        WorkflowStep step,
        string where,
        WorkflowDefinition called,
        string name,
        Place place,
        int depth,
        IReadOnlyList<string> chain,
        Walk walk,
        string fingerprint)
    {
        var calledSteps = (await ResolveStepsAsync(called.Steps, place, where, depth + 1, chain, walk).ConfigureAwait(false))
            .Select(member => Marked(member, name))
            .ToList();
        var calledRecover = called.Recover is null ? null : called.Recover with
        {
            Steps = [.. (await ResolveStepsAsync(called.Recover.Steps, place, $"{where}.{FlowScope.Recovery}", depth + 1, chain, walk).ConfigureAwait(false))
                .Select(member => Marked(member, name))],
        };

        foreach (var parameter in called.Params.Where(parameter => parameter.Required && parameter.Default is null))
        {
            walk.Problems.Add($"step '{where}' uses the workflow '{name}', whose param '{parameter.Name}' is required and declares no default: a used workflow is given nothing but its inputs, so it holds the param's own value or none");
        }

        var governed = step.UseParentLimits == false;

        var use = new FlowUse(
            step.UseSource!,
            name,
            fingerprint,
            called.Inputs,
            called.Params,
            called.Outputs,
            calledRecover,
            NotAppliedOf(called, governed, walk.RunShapesTheGraph),
            governed ? OwnOf(called) : null,
            called.Texts);

        return step with
        {
            UseSource = null,
            Use = use,
            Steps = calledSteps,
        };
    }

    /// <summary>
    /// The run-level keys the called workflow declares that no step of the run applies, in the order a line says them:
    /// its budget and its reporter, which a use that governs itself carries but nothing applies yet - the run's own
    /// budget counts its steps, and the run's own reporter words them - its <c>pushCheckpoints</c> and its
    /// <c>recommendations</c>, which are a run's own, and, unless the use governs itself, its advisor, amend, answers,
    /// limits and <c>codebaseGraph</c>, the calling run's own holding - its <c>codebaseGraph</c> under a use that governs
    /// itself too, where the run's own workflow declares one, for the run's one graph has the run's shape then
    /// (<see cref="FlowUse.GraphOf"/>). An amend under a use that governs itself is refused by the definition's check,
    /// never said here.
    /// </summary>
    /// <param name="called">The called workflow.</param>
    /// <param name="governed">Whether the use says <c>useParentLimits: false</c>.</param>
    /// <param name="runShapesTheGraph">Whether the run's own workflow declares a <c>codebaseGraph</c>.</param>
    private static IReadOnlyList<string> NotAppliedOf(WorkflowDefinition called, bool governed, bool runShapesTheGraph)
    {
        var notApplied = new List<string>();

        if (called.Budget is not null)
        {
            notApplied.Add("budget");
        }

        if (!governed && called.Advisor is not null)
        {
            notApplied.Add("advisor");
        }

        if (called.Reporter is not null)
        {
            notApplied.Add("reporter");
        }

        if (!governed && called.Amend is not null)
        {
            notApplied.Add("amend");
        }

        if (!governed && called.Answers.Count > 0)
        {
            notApplied.Add("answers");
        }

        if (!governed && new[] { called.MaxReasoningAgents, called.MaxParallelProcesses, called.MaxOpenAgents, called.MaxRetries, called.MaxInstances, called.HandoffSeconds, called.BackoffSeconds }.Any(limit => limit is not null))
        {
            notApplied.Add("limits");
        }

        if ((!governed || runShapesTheGraph) && called.CodebaseGraph is not null)
        {
            notApplied.Add("codebaseGraph");
        }

        if (called.PushCheckpoints is not null)
        {
            notApplied.Add("pushCheckpoints");
        }

        if (called.Recommendations is not null)
        {
            notApplied.Add("recommendations");
        }

        return notApplied;
    }

    /// <summary>
    /// The called workflow's run-level settings, carried for a use that says <c>useParentLimits: false</c>: its limits,
    /// <c>maxInstances</c>, advisor, answers and <c>codebaseGraph</c> applied to its steps, and its budget and reporter
    /// carried but applied by nothing yet, which <see cref="NotAppliedOf"/> says. An amend is never carried, for an
    /// amend is a run's own - the definition's check refuses the use that would apply one.
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
    /// V59: why a use at <paramref name="depth"/> places the steps of <paramref name="called"/> deeper than steps nest at
    /// all - its members a level below it, and every level of its own below them, its recovery's as much as its flow's -
    /// or null where they fit. Each use is held to it where it is placed, so the whole resolved tree is, and no
    /// definition that holds reaches the run's records deeper than they can be written.
    /// </summary>
    private static string? DepthProblem(string where, string source, WorkflowDefinition called, int depth)
    {
        var levels = Math.Max(DepthOf(called.Steps), DepthOf(called.Recover?.Steps ?? []));

        return depth + levels > WorkflowFileParser.MaxDepth
            ? $"step '{where}' uses '{source}' {depth} level{(depth == 1 ? string.Empty : "s")} deep, and the workflow it uses nests its steps {levels} level{(levels == 1 ? string.Empty : "s")} below it, {depth + levels} in all; steps nest at most {WorkflowFileParser.MaxDepth} deep in all, a used workflow's own counted below the step that uses it"
            : null;
    }

    /// <summary>How many levels <paramref name="steps"/> nest, the steps themselves the first; none where there are none.</summary>
    private static int DepthOf(IReadOnlyList<WorkflowStep> steps)
        => steps.Count == 0 ? 0 : 1 + steps.Max(step => DepthOf(step.Steps));

    /// <summary>
    /// The circle <paramref name="file"/> closes where the chain already holds it - each file from the first time it was
    /// entered, then the file again - or null where it holds none: compared as the files are, by the machine's paths.
    /// </summary>
    private static string? Circle(IReadOnlyList<string> chain, string file, StringComparison comparison)
    {
        for (var at = 0; at < chain.Count; at++)
        {
            if (string.Equals(chain[at], file, comparison))
            {
                return string.Join(" -> ", [.. chain.Skip(at), file]);
            }
        }

        return null;
    }

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
    /// Why <paramref name="source"/> is no relative path a use may name, or null where it is one: a path that every
    /// machine reads as the same file - forward slashes only, never rooted or holding a drive, and no part that starts
    /// or ends with a space, which one machine's file system keeps and another's drops.
    /// </summary>
    private static string? SpellingProblem(string source)
    {
        if (source.Contains('\\', StringComparison.Ordinal))
        {
            return "which holds a backslash: a use is written with forward slashes, so that every machine reads the one path it names";
        }

        if (source.Contains(':', StringComparison.Ordinal) || source.StartsWith('/'))
        {
            return "which is no path to a workflow file: a use is a path relative to the calling workflow's directory, written with forward slashes, that resolves inside the repository's workflows directory";
        }

        return source.Split('/').FirstOrDefault(part => part.Length > 0 && !string.Equals(part, part.Trim(), StringComparison.Ordinal)) is { } padded
            ? $"whose part '{padded}' starts or ends with a space, which one machine's file system keeps and another's drops: a use names each part as the directory or file is named"
            : null;
    }

    /// <summary>
    /// The path a use names, written with forward slashes: the caller's directory joined to the source, <c>.</c> and
    /// <c>..</c> resolved, and null where the path leaves the root it resolves in.
    /// </summary>
    private static string? ResolvedPath(string directory, string source)
    {
        var parts = new List<string>();

        foreach (var part in $"{directory}/{source}".Split('/', StringSplitOptions.RemoveEmptyEntries))
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

        return parts.Count == 0 ? null : string.Join("/", parts);
    }
}
