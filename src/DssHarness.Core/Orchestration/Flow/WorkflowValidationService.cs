using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Checks the workflows a repository declares: that each definition file reads, holds, and names
/// files that are there - the one command that reads a workflow's whole definition and its files
/// together.
/// </summary>
/// <remarks>
/// The pure rules live in <see cref="WorkflowValidator"/>, the file's shape in
/// <see cref="WorkflowFileParser"/>; this adds what only the repository can answer: that the file
/// is where the configuration says it is, that the workflow's directory, its definition and the
/// files the definition names are the repository's own, inside the workflow's directory, that each
/// schema a step names is one the engine reads, and that
/// what the definition asks of the configuration holds: the legs and runners it names are
/// declared, a runner that writes the tree works in an agent's worktree, a commit step fills the
/// commit template, and the agents a run makes are named within the worktrees' bound.
/// </remarks>
public interface IWorkflowValidationService : DssHarness.Core.Application.IUseCase
{
    /// <summary>
    /// Validates the declared workflow <paramref name="named"/>, or every one the configuration
    /// declares where it names none.
    /// </summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="named">A workflow's name, as <c>workflows</c> declares it; <see langword="null"/> for every one.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <param name="recorded">
    /// The definition a run already began with, where the check is one that goes on with it: each remote use it
    /// holds is resolved to the commit the run recorded, so a tag or branch that moved since changes no run already
    /// started. Null where no run's own record stands behind the check.
    /// </param>
    /// <exception cref="HarnessException">
    /// <paramref name="named"/> is a workflow the configuration does not declare
    /// (<see cref="HarnessExit.UsageError"/>); or the configuration does not load, or the tree cannot
    /// be resolved. A workflow with problems is reported, not thrown, and so is a configuration that
    /// declares none.
    /// </exception>
    Task<WorkflowValidationReport> CheckAsync(string startDirectory, string? named, CancellationToken cancellationToken = default, WorkflowDefinition? recorded = null);
}

/// <summary>What checking one declared workflow found.</summary>
/// <param name="Name">The workflow, as <c>workflows</c> declares it.</param>
/// <param name="Lines">One line saying what it is, where it holds, and one line for each of its agents' models and levels; one line for each problem, where it does not.</param>
public sealed record WorkflowCheck(string Name, IReadOnlyList<string> Lines)
{
    /// <summary>Whether it holds: its first line says so, whatever its agents' models and levels add under it.</summary>
    public bool Holds => Lines is [var line, ..] && WorkflowValidationReport.Holds(line);

    /// <summary>
    /// The definition that held, as it was read and checked; <see langword="null"/> where it does not hold. What a command
    /// that goes on from a check reads, so that what it acts on is what was checked, never the file read a second time.
    /// </summary>
    public WorkflowDefinition? Definition { get; init; }

    /// <summary>
    /// The workflow's directory, every link along it followed, which every file the definition names was found inside;
    /// <see langword="null"/> where it does not hold.
    /// </summary>
    public string? Directory { get; init; }

    /// <summary>
    /// Each remote workflow the check resolved, in the order the definition declares its uses, with every file of its
    /// directory as the check read and hashed it - what a run that starts from the check keeps with its frozen definition
    /// and reads as it runs; empty where the definition uses none, or does not hold.
    /// </summary>
    public IReadOnlyList<WorkflowRemoteResolution> Remotes { get; init; } = [];

    /// <summary>Whether the check went to resolve a remote use, reading the workflow cache and fetching into it, whether or not the workflow holds.</summary>
    public bool ResolvedRemote { get; init; }
}

/// <summary>What checking a repository's workflows found, workflow by workflow.</summary>
/// <param name="Root">The tree whose workflows were checked.</param>
/// <param name="Workflows">Each workflow checked, in the order it was asked for; none where the configuration declares none.</param>
/// <param name="Cache">
/// The workflow cache's directory, where the remote workflows a definition uses are read from and fetched into - named on
/// every report, whatever its definitions use; null where the check is given no cache, or no directory of this user's own
/// can be named.
/// </param>
public sealed record WorkflowValidationReport(string Root, IReadOnlyList<WorkflowCheck> Workflows, string? Cache = null)
{
    /// <summary>Every line of every workflow checked, in order: one for a workflow that holds, one per problem of one that does not.</summary>
    public IReadOnlyList<string> Lines => [.. Workflows.SelectMany(workflow => workflow.Lines)];

    /// <summary>Whether a workflow was checked and every one holds: none checked is no pass.</summary>
    public bool Passed => Workflows.Count > 0 && Workflows.All(workflow => workflow.Holds);

    /// <summary>Whether one line says its workflow held.</summary>
    public static bool Holds(string line) => line.StartsWith("ok ", StringComparison.Ordinal);
}

/// <inheritdoc cref="IWorkflowValidationService"/>
public sealed class WorkflowValidationService(
    IHarnessContextLoader contextLoader,
    IFileSystem fileSystem,
    IHostPlatform platform,
    DssHarness.Core.Secrets.AgentKeyStore keys,
    Codebase.CodeGraphExtractors extractors,
    WorkflowRemoteFetcher? remotes = null,
    WorkflowCache? cache = null) : IWorkflowValidationService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "validate-workflow";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly DssHarness.Core.Secrets.AgentKeyStore _keys = keys;

    /// <summary>
    /// Fetches the remote workflows a definition uses, into the cache this reads them from; null where the check
    /// serves a definition whose workflows are local only, and a remote use is refused saying that.
    /// </summary>
    private readonly WorkflowRemoteFetcher? _remotes = remotes;

    /// <summary>
    /// The hash cache of remote workflows, in this user's own directory for this tool; null where the check serves a
    /// definition whose workflows are local only.
    /// </summary>
    private readonly WorkflowCache? _cache = cache;

    /// <summary>The rule every file a definition names is held to, which a run reads its files by too.</summary>
    private readonly WorkflowNamedFiles _named = new(fileSystem, platform.PathComparison);

    /// <summary>The implementations of the codebase graph's contract, whose names V52 holds <c>codebaseGraph.languages</c> to.</summary>
    private readonly Codebase.CodeGraphExtractors _extractors = extractors;

    /// <inheritdoc/>
    public async Task<WorkflowValidationReport> CheckAsync(
        string startDirectory,
        string? named,
        CancellationToken cancellationToken = default,
        WorkflowDefinition? recorded = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var config = context.Config;

        if (named is not null && !(config.Workflows ?? []).Contains(named, StringComparer.Ordinal))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                DeclaredName.NotDeclared(named, "workflows", config.Workflows ?? [])
                    + $" A workflow is defined by '{WorkflowName.Expected(named)}'.");
        }

        var workflows = new List<WorkflowCheck>();

        foreach (var name in named is null ? config.Workflows ?? [] : [named])
        {
            workflows.Add(await CheckAsync(context, name, recorded, cancellationToken).ConfigureAwait(false));
        }

        return new WorkflowValidationReport(context.Layout.RepositoryRoot, workflows, _cache?.DirectoryIfNamed);
    }

    /// <summary>
    /// Resolves one remote use through the fetcher, keeping what it resolved in <paramref name="resolved"/> for the check
    /// to read the workflow's files from: a refusal of the source goes to the resolver as it was raised, to be said as the
    /// use's problem, and any other failure - which stops the check with its own code - says the workflow cache's
    /// directory, where what the check had fetched before it is kept.
    /// </summary>
    private async Task<WorkflowRemoteResolution> ResolveRemoteAsync(
        WorkflowRemoteFetcher remotes,
        HarnessContext context,
        string source,
        Func<string, string?>? recordedCommit,
        List<WorkflowRemoteResolution> resolved,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await remotes.ResolveAsync(
                WorkflowRemote.Source(source) ?? throw new HarnessException(HarnessExit.UsageError, $"'{source}' is no remote workflow source"),
                context.Config.WorkflowSources,
                context.Layout.MainCheckoutRoot,
                Environment.GetEnvironmentVariable,
                recordedCommit?.Invoke(source),
                cancellationToken).ConfigureAwait(false);

            resolved.Add(resolution);

            return resolution;
        }
        catch (HarnessException failed) when (!WorkflowResolution.RefusesTheSource(failed) && _cache?.DirectoryIfNamed is { } kept)
        {
            throw new HarnessException(failed.ExitCode, $"{failed.Message.TrimEnd('.')}; {CacheSaid(kept)}", failed);
        }
    }

    /// <summary>What a check that resolved a remote use says of the workflow cache, on whatever exit it ends.</summary>
    /// <param name="directory">The cache's directory.</param>
    internal static string CacheSaid(string directory) => $"remote workflows are read from and fetched into the workflow cache '{directory}'";

    private async Task<WorkflowCheck> CheckAsync(HarnessContext context, string name, WorkflowDefinition? recorded, CancellationToken cancellationToken)
    {
        var file = context.Layout.WorkflowFile(name);

        if (!_named.IsFile(file, out var unlooked))
        {
            return new WorkflowCheck(name, [unlooked is null
                ? $"{Shown(name)} is not there; a workflow is defined by that file, written by 'dssharness init' where it is the implementation one"
                : $"{Shown(name)} could not be looked at: {unlooked}"]);
        }

        // Where the workflow's directory and its definition lead, every link along them followed, before a word of it is
        // read: the files a definition names are held to that directory, so it must itself be the repository's own.
        if (Contained(context.Layout, name, $"{name}/{name}{HarnessLayout.WorkflowFileExtension}", Shown(name), out var directory, out _) is { } outside)
        {
            return new WorkflowCheck(name, [outside]);
        }

        // The parser's refusal already names the file and every problem it found in it.
        var refusal = TryParse(name, file, out var definition);

        if (refusal is not null)
        {
            return new WorkflowCheck(name, [refusal]);
        }

        if (!string.Equals(definition!.Name, name, StringComparison.Ordinal))
        {
            return new WorkflowCheck(name, [$"{Shown(name)} names itself '{definition.Name}'; a workflow's name is its directory's and its file's, said again in the file so a copied directory cannot pass as the workflow it was copied from"]);
        }

        // Every use is resolved before the definition is checked: the called workflows are read, parsed and placed, so
        // that the definition under the check is the one a run runs - and what a resume is refused by is what was read.
        // A local one is held to the workflows directory as the run's own is; a remote one is fetched into the workflow
        // cache, or read from it at the commit this run's own record holds, where the check goes on with a run that
        // began at one; and a use inside a remote one is read from the same directory as the resolution read it. Every
        // file of a remote workflow is read from what its resolution read and hashed, never from the cache again, which
        // a pruning may change meanwhile.
        var recordedCommit = RecordedCommit(recorded);
        var fetched = new List<WorkflowRemoteResolution>();
        var asked = false;
        var (withUses, useProblems, remoteResolutions) = await WorkflowResolver.ResolveAsync(
            definition!,
            name,
            new WorkflowResolution(
                relative => ReadWorkflowFile(context.Layout, relative),
                _remotes is not { } remotes
                    ? null
                    : source =>
                    {
                        asked = true;
                        return ResolveRemoteAsync(remotes, context, source, recordedCommit, fetched, cancellationToken);
                    },
                (owner, path) => ReadFetched(fetched, owner, path),
                _platform.PathComparison)).ConfigureAwait(false);

        if (useProblems.Count > 0)
        {
            return new WorkflowCheck(name, [.. useProblems.Select(problem => $"{Shown(name)}: {problem}")]) { ResolvedRemote = asked };
        }

        definition = withUses;

        // The configuration's providers, resolved once: the same resolution checks the definition and says each
        // agent's models and levels where it holds.
        var resolved = DssHarness.Core.Agents.AgentProviders.Resolve(context.Config);

        var problems = new List<string>(WorkflowValidator.Validate(
            definition,
            context.Config.Commit.Template is not null,
            (named, owner) => TextOf(named, owner, name, directory, context, fetched),
            resolved.Providers,
            _extractors.Languages,
            Web.Search.WebSearchSources.Of(context.Config, Web.Search.WebSearchKinds.OfThisBuild),
            new AgentSkills(_fileSystem, _platform, context.Layout.RepositoryRoot)));
        problems.AddRange(Files(definition, directory, name, context, context.Config, fetched));

        // The key slot's own problems - a name declared twice, a line that defines nothing - are the configuration's,
        // said where the definition is checked; which names it holds is no problem at all, so a machine that only
        // checks a configuration needs no key, and the steps that would read one it lacks are said under the line that
        // says the definition holds.
        var slot = _keys.Present(context.Layout);
        problems.AddRange(slot.Problems);

        if (problems.Count > 0)
        {
            return new WorkflowCheck(name, [.. problems.Select(problem => $"{Shown(name)}: {problem}")]) { ResolvedRemote = asked };
        }

        // The recovery is counted apart: its steps run only where something failed. Under the one line that says the
        // definition holds, each agent's models and the level each runs at, resolved the same check resolved them - a
        // step's keys over its agent definition's - the prices of the models a step may run on, and the keys a step's
        // model would read that no file of the slot declares, with the steps that would read them.
        var steps = Count(definition.Steps);
        var recovering = definition.Recover is { } recover ? Count(recover.Steps) : 0;
        var reasons = Count([.. definition.Steps, .. definition.Recover?.Steps ?? []], "reason");
        var recovery = definition.Recover is null ? string.Empty : $", {recovering} recovering";
        IReadOnlyList<string> agents = resolved.Providers is { } providers
            ? [
                .. WorkflowValidator.ModelLevels(definition, providers, (named, owner) => TextOf(named, owner, name, directory, context, fetched)),
                .. Prices(definition, providers, (named, owner) => TextOf(named, owner, name, directory, context, fetched)),
                .. MissingKeys(definition, providers, slot),
            ]
            : [];
        return new WorkflowCheck(
            name,
            [
                $"ok {Shown(name)} ({steps} step{(steps == 1 ? string.Empty : "s")}{recovery}, {reasons} reasoning)",
                .. ResolutionsOf(definition, remoteResolutions),
                .. agents,
            ])
        {
            Definition = definition,
            Directory = directory,
            Remotes = remoteResolutions,
            ResolvedRemote = asked,
        };
    }

    /// <summary>
    /// The commit a run's own recorded definition holds for each remote source a use names, where the check goes on
    /// with a run that began at one: a source resolves to that commit again, however its ref has moved, so a resume is
    /// refused by nothing it cannot help. Null where no run's own record stands behind the check.
    /// </summary>
    /// <param name="recorded">The definition the run began with.</param>
    private static Func<string, string?>? RecordedCommit(WorkflowDefinition? recorded)
    {
        if (recorded is null)
        {
            return null;
        }

        // Only a use that names a remote source records the commit it resolved to: a relative use inside a remote
        // workflow is read from that one's entry, at that one's commit.
        var commits = FlowScope.Declared(recorded)
            .Select(entry => entry.Step.Use)
            .OfType<FlowUse>()
            .Where(use => WorkflowRemote.Is(use.Name) && WorkflowRemote.Source(use.Source) is not null)
            .GroupBy(use => use.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Fingerprint, StringComparer.Ordinal);

        return commits.Count == 0 ? null : source => commits.GetValueOrDefault(source);
    }

    /// <summary>
    /// One line for each workflow a step of <paramref name="definition"/> uses, in the order the tree declares them:
    /// what it uses, the called workflow's name - a remote one's repository, directory and resolved commit - and its
    /// fingerprint, beside what the called workflow declares that no step applies, so that nothing is applied in
    /// silence and nothing left out in silence either - a use that governs itself saying the keys it applies and,
    /// apart, the ones it carries that nothing applies yet - and beside how a remote one was resolved where that is
    /// worth a word: a moving ref, allowed by the pin, and a resolution the cache stood in for, the network not being
    /// there. The workflow cache a remote one was read from or fetched into is named once, on the line the check ends on
    /// (<see cref="WorkflowValidationReports.Render"/>), whatever it ends with.
    /// </summary>
    private static IReadOnlyList<string> ResolutionsOf(WorkflowDefinition definition, IReadOnlyList<WorkflowRemoteResolution> remotes)
    {
        var lines = new List<string>();

        foreach (var (path, step) in FlowScope.Declared(definition))
        {
            if (step.Use is not { } use)
            {
                continue;
            }

            var resolution = remotes.FirstOrDefault(resolved => WorkflowRemote.Name(resolved.Source, resolved.Commit) == use.Name);
            var said = WorkflowRemote.PartsOf(use.Name) is { } parts
                ? $"{parts.Host}/{parts.Owner}/{parts.Repo}/{parts.Directory} at commit {parts.Commit[..12]}"
                : use.Name;
            var applied = use.Own?.Declared.Where(key => !use.NotApplied.Contains(key, StringComparer.Ordinal)).ToList();

            lines.Add(
                $"'{path}' uses {use.Source} -> {said} ({use.Inputs.Count} input{(use.Inputs.Count == 1 ? string.Empty : "s")}, fingerprint {use.Fingerprint[..12]})"
                + (applied is null
                    ? string.Empty
                    : applied.Count == 0
                        ? "; useParentLimits: false, and it applies no key of its own, so the calling run's settings hold"
                        : $"; useParentLimits: false, governed by its own {string.Join(", ", applied)}")
                + (use.NotApplied.Count == 0 ? string.Empty : $"; applies none of the {string.Join(", ", use.NotApplied)} it declares")
                + (resolution?.Moving == true ? "; the ref is a branch, which moves - pin it by its commit where what a run ran must be what it said" : string.Empty)
                + (resolution?.FromLastResolved == true ? "; resolved from the workflow cache's last resolution, the network not being there" : string.Empty));
        }

        return lines;
    }

    /// <summary>
    /// One line for each provider whose models a step of the definition may run on: whether each of those models has a
    /// price, and when the provider's numbers were read - by the one line the listing says it in.
    /// </summary>
    private static IEnumerable<string> Prices(WorkflowDefinition definition, DssHarness.Core.Agents.AgentProviders providers, Func<string, string?, string?>? files)
    {
        var models = WorkflowValidator.ModelsRunOn(definition, providers, files);

        foreach (var provider in providers.Providers)
        {
            var runOn = provider.Models.Where(model => models.Any(entry => ReferenceEquals(entry.Model, model))).ToList();

            if (runOn.Count > 0)
            {
                yield return DssHarness.Core.Agents.AgentProviderReports.PriceLine(
                    provider.Name,
                    provider.Currency,
                    provider.ReadOn,
                    [.. runOn.Select(model => (model.Code, DssHarness.Core.Agents.AgentProviders.Priced(model)))],
                    everyModel: runOn.Count == provider.Models.Count);
            }
        }
    }

    /// <summary>
    /// One line for each provider with a key the slot lacks that a step of the definition would read: every such name,
    /// said by the one builder of the missing-key sentence with the steps that would read it - a report under the line
    /// that says the definition holds, never a problem of it.
    /// </summary>
    private static IEnumerable<string> MissingKeys(
        WorkflowDefinition definition,
        DssHarness.Core.Agents.AgentProviders providers,
        DssHarness.Core.Secrets.AgentSlotPresent slot)
    {
        var appointed = providers.Appointed();

        return WorkflowValidator.MissingKeys(definition, providers, slot.Names).Select(need => DssHarness.Core.Secrets.AgentKeyStore.Missing(
            slot,
            appointed,
            need.Missing,
            appointed.First(key => string.Equals(key.Name, need.Missing[0], StringComparison.Ordinal)),
            need.ReadBy));
    }

    /// <summary>
    /// Why a workflow is not the repository's own, or <see langword="null"/> where it is: its directory strictly inside
    /// the workflows directory, and its definition strictly inside its directory, each once every link along it is
    /// followed - so that what a reviewer reads there is what the workflow runs, as an action's file is held to the
    /// actions directory (<see cref="Runners.ActionPath.Resolve"/>). The one rule the run's own workflow and every
    /// workflow a use of the repository's names are held to.
    /// </summary>
    /// <param name="layout">The tree's layout.</param>
    /// <param name="relativeDirectory">The workflow's directory under the workflows directory, forward slashes - its name.</param>
    /// <param name="relativeFile">The workflow's file under the workflows directory, forward slashes.</param>
    /// <param name="shown">The workflow's file, as a problem names it.</param>
    /// <param name="directory">The workflow's directory with every link along it followed, which the files it names are held to.</param>
    /// <param name="file">The workflow's file with every link along it followed.</param>
    private string? Contained(HarnessLayout layout, string relativeDirectory, string relativeFile, string shown, out string directory, out string file)
    {
        string workflows;

        try
        {
            // The workflows directory is resolved the same way as the rest, so a repository reached through a link of its
            // own compares against the same real path, as the actions directory is.
            workflows = Path.TrimEndingDirectorySeparator(_fileSystem.ResolveLinks(Path.GetFullPath(layout.WorkflowsDirectory)));
            directory = _named.Directory(layout, relativeDirectory);
            file = _fileSystem.ResolveLinks(Path.GetFullPath(Path.Combine([layout.WorkflowsDirectory, .. relativeFile.Split('/')])));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            directory = string.Empty;
            file = string.Empty;
            return $"{shown} could not be resolved inside '{layout.WorkflowsDirectory}': {exception.Message}";
        }

        if (!PathContainment.IsStrictlyInside(workflows, directory, _platform.PathComparison))
        {
            return $"{shown}: its directory leads to '{directory}', which is outside '{workflows}'; a workflow's directory is the repository's own, so that what a reviewer reads there is what the workflow runs";
        }

        return PathContainment.IsStrictlyInside(directory, file, _platform.PathComparison)
            ? null
            : $"{shown} leads to '{file}', which is outside '{directory}'; a workflow's definition is a file of its own directory, so that what a reviewer reads there is what the workflow runs";
    }

    /// <summary>
    /// Reads and parses the definition at <paramref name="file"/>, answering why it could not be instead of throwing: the
    /// parser's refusal, which names the file and every problem in it, or the disk's, naming the file - one workflow that
    /// cannot be read is that workflow's problem, never the end of every other's check.
    /// </summary>
    private string? TryParse(string name, string file, out WorkflowDefinition? definition)
    {
        definition = null;
        string text;

        try
        {
            text = _fileSystem.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"{Shown(name)} could not be read: {exception.Message}";
        }

        try
        {
            definition = WorkflowFileParser.Parse(file, text);
            return null;
        }
        catch (HarnessException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// What only the repository can answer: that the files the definition names are in the
    /// workflow's own directory - a used workflow's in the used one's - that the legs and runners
    /// it names are declared, that a runner that writes the tree works in an agent's worktree,
    /// that its commit steps fill the configuration's commit template, and that the agents it
    /// makes are named within <c>worktrees.maxNameLength</c>.
    /// </summary>
    private IReadOnlyList<string> Files(WorkflowDefinition definition, string directory, string name, HarnessContext context, HarnessConfig config, IReadOnlyList<WorkflowRemoteResolution> fetched)
    {
        var problems = new List<string>();

        foreach (var named in WorkflowFiles.OfDefinition(definition))
        {
            problems.AddRange(Check(named, directory, name));
        }

        // The recovery's steps name files as the flow's do, and are checked the same way, under its own path.
        foreach (var (path, step) in WorkflowFiles.Steps(definition))
        {
            var where = $"step '{path}'";
            var ownerDirectory = step.WorkflowOf is { } owner && !WorkflowRemote.Is(owner) ? DirectoryOf(context.Layout, owner) : directory;
            var ownerName = step.WorkflowOf ?? name;

            foreach (var named in WorkflowFiles.OfStep(step, where))
            {
                problems.AddRange(step.WorkflowOf is { } ownerOf && WorkflowRemote.Is(ownerOf)
                    ? Remote(named, ownerOf, fetched)
                    : Check(named, ownerDirectory, ownerName));
            }

            if (step.Runner is not null && !config.PredefinedRunners.ContainsKey(step.Runner))
            {
                problems.Add($"{where} runs '{step.Runner}': " + DeclaredName.NotDeclared(step.Runner, "predefinedRunners", config.PredefinedRunners.Keys));
            }

            problems.AddRange(WritesTheMainTree(step, where, config));
            problems.AddRange(CommitVariables(step, where, config.Commit));

            // A leg set is named where a leg is, as --legs takes one.
            foreach (var leg in step.Legs.Where(leg => !config.Legs.ContainsKey(leg) && !config.LegSets.ContainsKey(leg)))
            {
                problems.Add(
                    $"{where} names '{leg}', which is neither a leg nor a leg set: "
                        + DeclaredName.NotDeclared(leg, "legs or legSets", [.. config.Legs.Keys, .. config.LegSets.Keys]));
            }

            // The files a use names in the used workflow's directory - a file it fills an input with, by its with or
            // the input's default, each text of the used workflow, and the advisor's and reporter's files of one that
            // governs itself - are files of that directory, as every file a step of it names is: read there, under the
            // used workflow's own name, the repository's own directory for a local workflow, the cache entry for a
            // remote one.
            if (step.Use is { } use)
            {
                foreach (var named in WorkflowFiles.OfUse(step, path))
                {
                    problems.AddRange(WorkflowRemote.Is(use.Name)
                        ? Remote(named, use.Name, fetched)
                        : Check(named, DirectoryOf(context.Layout, use.Name), use.Name));
                }
            }
        }

        Add(problems, GeneratedAgentNames(definition, config.Worktrees));
        return problems;
    }

    /// <summary>
    /// The problems with one file a remote workflow names: spelt as a path inside the workflow's own directory, as a
    /// local workflow's files are; there in the directory its resolution read and hashed, under the workflow's own
    /// directory in its repository; and, where it is a schema, one the engine reads (<see cref="WorkflowFiles.SchemaProblems"/>)
    /// - the one rule a local workflow's schema is held to (<see cref="Schema"/>), so no run starts over a remote reason
    /// step whose answer could be held to nothing.
    /// </summary>
    /// <param name="named">The file, as the definition names it.</param>
    /// <param name="owner">The remote workflow's name, as the run resolved it.</param>
    /// <param name="fetched">The remote workflows the check resolved.</param>
    private static IReadOnlyList<string> Remote(WorkflowFiles.Named named, string owner, IReadOnlyList<WorkflowRemoteResolution> fetched)
    {
        if (PlatformPaths.IsRootedOnAnyPlatform(named.File) || named.File.Split('/', '\\').Any(segment => segment is ".." or "." or ""))
        {
            return [$"'{named.File}', {named.What}, does not name a file inside the workflow's own directory; every file a workflow names is relative to it, without '.', '..' or an empty part"];
        }

        if (WorkflowRemoteResolution.Holding(fetched, owner) is not { } holding || WorkflowRemote.PartsOf(owner) is not { } parts)
        {
            return [$"'{named.File}', {named.What}, is of the workflow '{owner}', which no remote workflow the check resolved holds"];
        }

        string? text;

        try
        {
            text = holding.Text($"{parts.Directory}/{named.File.Replace('\\', '/')}");
        }
        catch (HarnessException refused)
        {
            return [refused.Message];
        }

        if (text is null)
        {
            return [$"'{named.File}', {named.What}, is not in the workflow '{owner}': the commit it was fetched at holds no such file in its directory"];
        }

        return named.IsSchema ? WorkflowFiles.SchemaProblems(named, text) : [];
    }

    /// <summary>
    /// The directory of a workflow a step uses, under the repository's workflows root and every link along it followed
    /// - the called workflow's files are read there, as the run's own are in its.
    /// </summary>
    private string DirectoryOf(HarnessLayout layout, string owner)
    {
        try
        {
            return _named.Directory(layout, owner);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.Combine([layout.WorkflowsDirectory, .. owner.Split('/')]);
        }
    }

    /// <summary>
    /// Reads a workflow file a local use names by its path under the repository's workflows root, forward slashes -
    /// the one reader the resolution of a use reads by, holding the called workflow to the rule the run's own is held
    /// to (<see cref="Contained"/>) before a word of it is read: null where it was looked for and is not there.
    /// </summary>
    /// <exception cref="HarnessException">
    /// Its directory or its file leads out of the workflows directory once links are followed, or it could not be looked
    /// at or read (<see cref="HarnessExit.ConfigInvalid"/>), saying why - never taken for a file that is not there.
    /// </exception>
    private string? ReadWorkflowFile(HarnessLayout layout, string relative)
    {
        var shown = $"{HarnessLayout.WorkflowsDirectoryRelative}/{relative}";

        if (Contained(layout, relative[..relative.LastIndexOf('/')], relative, shown, out _, out var file) is { } outside)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, outside);
        }

        if (!_named.IsFile(file, out var unlooked))
        {
            return unlooked is null ? null : throw new HarnessException(HarnessExit.ConfigInvalid, $"{shown} could not be looked at: {unlooked}");
        }

        try
        {
            return _fileSystem.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"{shown} could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Reads a file of a remote workflow's directory by its path in the remote repository, from what the resolution of
    /// the remote workflow it was fetched as read and hashed - never from the cache again, which a pruning may have changed
    /// since: null where the directory holds no such file.
    /// </summary>
    /// <exception cref="HarnessException">
    /// No resolution the check made holds the workflow, or the file is not UTF-8 text (<see cref="HarnessExit.ConfigInvalid"/>)
    /// - said as the use's problem, never as a file the commit does not hold.
    /// </exception>
    private static string? ReadFetched(IReadOnlyList<WorkflowRemoteResolution> fetched, string owner, string path)
        => WorkflowRemoteResolution.Holding(fetched, owner) is { } holding
            ? holding.Text(path)
            : throw new HarnessException(HarnessExit.ConfigInvalid, $"no remote workflow the check resolved holds '{owner}', whose use was read from one");

    /// <summary>
    /// V24: a run step whose runner writes the tree (<c>writesTree</c>) - the step's own, or one its run checks name,
    /// which runs within the step's legs - works in an agent's worktree, named with <c>in</c>, and never on the main
    /// tree: the run reads its definition there and folds into it, so what such a step wrote under the other steps would
    /// change what they read. A runner nobody declared is refused beside this, by name.
    /// </summary>
    private static IEnumerable<string> WritesTheMainTree(WorkflowStep step, string where, HarnessConfig config)
    {
        if (step.Runner is null || step.In is not null)
        {
            yield break;
        }

        const string OnTheMainTree = "on the main tree: the run reads its definition there and folds into it, so a runner "
            + "that writes works in an agent's worktree. Name the create-agent step with 'in'";

        foreach (var (writer, isCheck) in config.WritingRunners(step.Runner))
        {
            yield return isCheck
                ? $"{where} runs '{step.Runner}', whose run check '{writer}' writes the tree (writesTree), {OnTheMainTree}"
                : $"{where} runs '{step.Runner}', which writes the tree (writesTree), {OnTheMainTree}";
        }
    }

    /// <summary>
    /// V30, where config.json declares a <c>commit.template</c>: a commit step gives a value for every variable the
    /// template requires, and none for a variable <c>commit.variables</c> does not declare, which nothing would fill in.
    /// A name is matched as the configuration matches the template's placeholders, whatever its case. Where no template
    /// is declared the validator holds the step to its message and no variables, from what the configuration says.
    /// </summary>
    private static IEnumerable<string> CommitVariables(WorkflowStep step, string where, CommitConfig commit)
    {
        if (step.Type != "commit" || commit.Template is null)
        {
            yield break;
        }

        foreach (var (name, variable) in commit.Variables.Where(variable => variable.Value.Required))
        {
            if (DeclaredName.In(step.Variables.Keys, name) is null)
            {
                yield return $"{where} is a commit step and {CommitTemplate.NoValue(name, variable)}";
            }
        }

        foreach (var given in step.Variables.Keys.Where(given => DeclaredName.In(commit.Variables.Keys, given) is null))
        {
            yield return $"{where} is a commit step and gives a value for '{given}', which commit.variables does not declare: the template is filled with the variables it declares, and this one would be read by nothing";
        }
    }

    /// <summary>
    /// V27: the names a run gives the agents it makes fit <c>worktrees.maxNameLength</c>, as every agent's name must when
    /// it is made - the letter and a number that grows to the workflow's most step instances, one agent made by each
    /// at the most. A workflow none of whose steps makes an agent names none.
    /// </summary>
    private static string? GeneratedAgentNames(WorkflowDefinition definition, WorktreeSettings worktrees)
    {
        if (!WorkflowFiles.Steps(definition).Any(entry => WorkflowStepTypes.MakesAgent(entry.Step)))
        {
            return null;
        }

        var longest = OrchestrationRules.LongestGeneratedAgentName(definition.MaxInstances ?? WorkflowDefinition.DefaultMaxInstances);
        var digits = longest - OrchestrationRules.GeneratedAgentPrefix.Length;

        return longest > worktrees.MaxNameLength
            ? $"the workflow creates agents, whose generated names ('{OrchestrationRules.GeneratedAgentPrefix}' and up to {digits} digit{(digits == 1 ? string.Empty : "s")}) "
                + $"are {longest} characters, and worktrees.maxNameLength is {worktrees.MaxNameLength}"
            : null;
    }

    private static void Add(List<string> problems, string? problem)
    {
        if (problem is not null)
        {
            problems.Add(problem);
        }
    }

    /// <summary>The problems of one file the definition names: a schema read as a JSON object, any other found.</summary>
    private IReadOnlyList<string> Check(WorkflowFiles.Named named, string directory, string name)
    {
        if (named.IsSchema)
        {
            return Schema(named, directory, name);
        }

        var problems = new List<string>();
        Add(problems, _named.Problem(named.File, directory, name, named.What, out _));
        return problems;
    }

    /// <summary>
    /// The text of a file the definition names, for the validator to type a schema's paths and check a prompt's
    /// placeholders by, read where the workflow that names it keeps its files: the repository's own directory for a
    /// local workflow, the cache entry a remote one's resolution fetched for a remote one. <see langword="null"/> where
    /// it is not a file of the workflow's own place there to read - or a changed cache file refused to give it, which
    /// the check of its files says - so the problem is said once, where the file is checked.
    /// </summary>
    private string? TextOf(string named, string? owner, string name, string directory, HarnessContext context, IReadOnlyList<WorkflowRemoteResolution> fetched)
    {
        if (owner is null || !WorkflowRemote.Is(owner))
        {
            return Text(named, owner is null ? directory : DirectoryOf(context.Layout, owner), name);
        }

        if (WorkflowRemoteResolution.Holding(fetched, owner) is not { } holding || WorkflowRemote.PartsOf(owner) is not { } parts)
        {
            return null;
        }

        try
        {
            return holding.Text($"{parts.Directory}/{named}");
        }
        catch (HarnessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The text of a file the definition names, for the validator to type a schema's paths and check a prompt's
    /// placeholders by; <see langword="null"/> where it is not a file of the workflow's own directory there to read,
    /// which <see cref="Check(WorkflowFiles.Named, string, string)"/> says.
    /// </summary>
    private string? Text(string named, string directory, string name)
    {
        if (_named.Problem(named, directory, name, string.Empty, out var path) is not null)
        {
            return null;
        }

        try
        {
            return _fileSystem.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// That a schema file is there, can be read, and is a schema the engine reads (<see cref="WorkflowFiles.SchemaProblems"/>):
    /// the check a step's schema is held to again before its client is taken, made here so that no run starts over a
    /// schema no answer could be held to.
    /// </summary>
    private IReadOnlyList<string> Schema(WorkflowFiles.Named named, string directory, string name)
    {
        if (_named.Problem(named.File, directory, name, named.What, out var path) is { } problem)
        {
            return [problem];
        }

        string text;

        try
        {
            text = _fileSystem.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [$"'{named.File}', {named.What}, could not be read: {exception.Message}"];
        }

        return WorkflowFiles.SchemaProblems(named, text);
    }

    private static string Shown(string name)
        => $"{HarnessLayout.WorkflowsDirectoryRelative}/{name}/{name}{HarnessLayout.WorkflowFileExtension}";

    private static int Count(IReadOnlyList<WorkflowStep> steps, string? type = null)
        => steps.Count(step => type is null || step.Type == type)
            + steps.Where(step => step.Steps.Count > 0).Sum(step => Count(step.Steps, type));
}

/// <summary>Turns a workflow validation report into what validate-workflow prints.</summary>
public static class WorkflowValidationReports
{
    /// <summary>
    /// What validate-workflow says where the configuration declares no workflow: there is nothing
    /// to check, and what declares the one init writes.
    /// </summary>
    public static string NoneDeclared { get; } =
        $"config.json declares no workflow, so there is none to check; \"workflows\": [\"{WorkflowTemplates.ShippedWorkflowName}\"] "
        + $"declares the one '{ToolPackage.Command} init' writes, and init then writes its files. Nothing was run.";

    /// <summary>What validate-workflow reports.</summary>
    /// <param name="report">What was checked.</param>
    /// <param name="json">Whether to write the result as JSON instead of as lines.</param>
    /// <remarks>
    /// A workflow that does not hold is configuration that does not, as config.json, an action file
    /// or the mutation registry is - the parser itself refuses a malformed definition as that - so it
    /// is refused <see cref="HarnessExit.ConfigInvalid"/>, the code a run of that workflow would meet.
    /// A configuration that declares none has nothing to fix in a definition and nothing that passed:
    /// the request has no subject, a precondition not met, refused <see cref="HarnessExit.Refused"/>.
    /// Where the check went to resolve a remote use - reading the workflow cache, and fetching into it - the line it ends
    /// on names the cache's directory, whether the workflows hold or not; the document names it on every exit.
    /// </remarks>
    public static CommandOutcome Render(WorkflowValidationReport report, bool json)
    {
        ArgumentNullException.ThrowIfNull(report);

        var data = json ? [Document(report.Cache, report).ToJsonString(ReportJson.Options)] : Array.Empty<string>();

        if (report.Workflows.Count == 0)
        {
            return CommandOutcome.Refused(NoneDeclared) with { Data = data };
        }

        var cached = report.Cache is { } directory && report.Workflows.Any(workflow => workflow.ResolvedRemote)
            ? $"; {WorkflowValidationService.CacheSaid(directory)}"
            : string.Empty;

        if (report.Passed)
        {
            return CommandOutcome.Ok($"{ReportText.Counted(report.Workflows.Count, "workflow")} checked, all valid{cached}") with
            {
                Data = data,
                Quiet = json,
                Details = json ? null : report.Lines,
            };
        }

        var valid = report.Workflows.Count(workflow => workflow.Holds);

        return CommandOutcome.Failed(
            HarnessExit.ConfigInvalid,
            $"{ReportText.Counted(valid, "workflow")} valid, {report.Workflows.Count - valid} with problems{cached}. Nothing was run.") with
        {
            Data = data,
            Details = json ? null : report.Lines,
        };
    }

    /// <summary>
    /// The document validate-workflow answers with where it stops before it has a report - a <c>--directory</c> that is
    /// not there, a configuration that does not load, a workflow nobody declared, a remote use's fetch that could not run:
    /// its own, no workflows and no problems, after the code it exits with and the line it ends on, and the workflow
    /// cache's directory where one can be named.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary)
        => ReportJson.Stopped(exitCode, summary, Document(WorkflowCache.DefaultDirectoryIfNamed(), report: null));

    private static JsonObject Document(string? cache, WorkflowValidationReport? report)
        => new()
        {
            ["workflows"] = report is null
                ? null
                : new JsonArray([.. report.Lines.Select(line => (JsonNode)line!)]),
            ["passed"] = report?.Passed,
            ["cache"] = cache,
        };
}
