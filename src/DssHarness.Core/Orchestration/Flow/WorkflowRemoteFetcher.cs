using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A remote workflow a use resolved to: its source as written, the commit the ref resolved to, the cache entry its
/// directory lives in, its own yml, every file of its directory as the resolution read and hashed it, and how the
/// resolution came about - whether a moving ref was allowed and used, and whether the forge could not be reached and the
/// cache's newest resolution stood in.
/// </summary>
/// <param name="Source">The source as the use wrote it.</param>
/// <param name="Commit">The commit the ref resolved to, 40 hex characters.</param>
/// <param name="Entry">The cache entry holding the workflow's directory at that commit.</param>
/// <param name="Definition">The workflow's own yml.</param>
/// <param name="Files">
/// Every file of the entry, its bytes by its path in the repository, as the resolution fetched or read them and checked
/// each against its hash: what the check that resolved it reads the workflow's files from, and what a run keeps of it with
/// its frozen definition - so nothing that reads them after the resolution reads the cache again, which a pruning may have
/// changed since.
/// </param>
/// <param name="Moving">Whether the ref was a branch, allowed by <c>pin: any</c>, which moves.</param>
/// <param name="FromLastResolved">Whether the forge could not be reached and the cache's newest resolution of the ref was used.</param>
public sealed record WorkflowRemoteResolution(
    WorkflowRemoteSource Source,
    string Commit,
    WorkflowCacheEntry Entry,
    string Definition,
    IReadOnlyDictionary<string, byte[]> Files,
    bool Moving = false,
    bool FromLastResolved = false)
{
    /// <summary>The name the workflow goes by inside the run (<see cref="WorkflowRemote.Name(WorkflowRemoteSource, string)"/>).</summary>
    public string Name => WorkflowRemote.Name(Source, Commit);

    /// <summary>
    /// The text of one file of the workflow's directory, by its path in the repository, as the resolution read it: UTF-8,
    /// a byte order mark dropped (<see cref="WorkflowCache.Text"/>); null where the directory holds no such file.
    /// </summary>
    /// <param name="path">The file's path in the repository, forward slashes.</param>
    /// <exception cref="HarnessException">The file is not UTF-8 text (<see cref="HarnessExit.ConfigInvalid"/>).</exception>
    public string? Text(string path)
        => Files.TryGetValue(path, out var bytes) ? WorkflowCache.Text(bytes, $"'{path}' of the workflow '{Entry.Repository}' at '{Commit}'") : null;

    /// <summary>
    /// The resolution among <paramref name="resolutions"/> whose directory holds the remote workflow <paramref name="workflow"/>:
    /// its own, else the nearest above it at the same commit (<see cref="WorkflowRemote.HeldUnder"/>), as a workflow a
    /// relative use inside a remote one names is held; null where none does.
    /// </summary>
    /// <param name="resolutions">The resolutions a check made.</param>
    /// <param name="workflow">A remote workflow's name, as the run resolved it.</param>
    public static WorkflowRemoteResolution? Holding(IEnumerable<WorkflowRemoteResolution> resolutions, string workflow)
    {
        ArgumentNullException.ThrowIfNull(resolutions);

        var made = resolutions.ToList();

        foreach (var (host, owner, repo, commit, directory) in WorkflowRemote.HeldUnder(workflow))
        {
            if (made.FirstOrDefault(resolution => resolution.Entry.Commit == commit && resolution.Entry.Path == directory
                && resolution.Entry.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && resolution.Entry.Owner == owner && resolution.Entry.Repo == repo) is { } holding)
            {
                return holding;
            }
        }

        return null;
    }
}

/// <summary>
/// The one way a remote workflow is fetched: a ref resolved to a commit of the named repository's own - a tag peeled to
/// the commit it names, a branch where the pin allows one, and a commit named by its SHA proved to be one a branch or a
/// tag of the repository reaches - the commit's tree walked to the workflow's directory, every path under it held to
/// the tree's rule, every blob fetched as bytes and checked against the tree's hash of it, and the whole kept in the
/// workflow cache - read from it with no network where the commit is cached already. A remote source is trusted by
/// configuration alone: the repository's <c>workflowSources</c> allows its owner, names its host and the one credential
/// it is reached with, pins the refs it may name, and says what happens where the forge cannot be reached; nothing of
/// that is decided by a response. A command that runs under an agent - told by the one rule <c>run-side-task</c> is
/// told by, the agent's mark or a process of this tool's above it in the process tree, a tree that cannot be read taken
/// for no person's - never reaches the forge and never writes the cache: it reads what the cache holds, or is refused
/// naming the command a person runs to fetch it.
/// </summary>
/// <param name="gh">Runs the forge's tool, <c>gh api</c> among its calls.</param>
/// <param name="cache">The workflow cache the fetch is kept in.</param>
/// <param name="ancestry">Tells whether the command runs under an agent (<see cref="AgentAncestry.UnderAnAgentAsync"/>).</param>
/// <param name="output">Says what a resolution is warned of - a moving ref, the cache standing in; null says nothing.</param>
/// <param name="clock">Dates a fetch, a use and a resolution; the system's where none is given.</param>
public sealed class WorkflowRemoteFetcher(GhProcess gh, WorkflowCache cache, AgentAncestry ancestry, IHarnessOutput? output = null, TimeProvider? clock = null)
{
    /// <summary>What a resolution's warnings are said under, whichever command resolved it.</summary>
    public const string Said = "remote-workflow";

    /// <summary>
    /// The most refs a commit named by its SHA is compared with, beyond the repository's default branch, before it is
    /// refused as one no branch or tag of the repository reaches: the first tags, then the first branches, the forge lists.
    /// </summary>
    public const int MostRefsCompared = 20;

    /// <summary>The most tags a tag is peeled through to the commit it names.</summary>
    private const int MostPeels = 10;

    private readonly GhProcess _gh = gh;
    private readonly WorkflowCache _cache = cache;
    private readonly AgentAncestry _ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
    private readonly IHarnessOutput? _output = output;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Resolves the remote source a use names to a commit and a cached directory: the commit the run's own record
    /// says where it has one (<paramref name="recorded"/>, so a resume never resolves again, and a tag or branch
    /// that moved since changes no run already started), the cache where that commit is held, and the forge where
    /// it is not - the cache's newest resolution standing in where the forge cannot be reached and the configuration
    /// says <c>last-resolved</c>. Where the command runs under an agent, or cannot tell that it does not, the cache alone
    /// is read.
    /// </summary>
    /// <param name="source">The source a step's <c>use:</c> names, parsed.</param>
    /// <param name="settings">The repository's <c>workflowSources</c>; null where the file holds none.</param>
    /// <param name="workingDirectory">The tree the forge's tool is run in.</param>
    /// <param name="environment">Reads this process's environment: a host's credential, and the mark of an agent's command.</param>
    /// <param name="recorded">The commit this run's own record holds for the source, where one does; null else.</param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    /// <exception cref="HarnessException">
    /// A refusal of the source itself (<see cref="HarnessExit.Refused"/>): the ref is no ref git takes, the source's
    /// repository is not allowed, its host is not named or its credential not there, its ref is not one the pin takes -
    /// the cache's newest resolution of it as much as the forge's - or not the repository's own, the file it names is not
    /// in the directory, or the forge answered and refused what was asked, naming what it said; or its yml is not text
    /// (<see cref="HarnessExit.ConfigInvalid"/>). Every other failure keeps its own code, and is no refusal of the source:
    /// the forge not reached with nothing standing in, as the call that did not reach it raised it
    /// (<see cref="HarnessExit.Incomplete"/> where its limit passed, <see cref="HarnessExit.CommandFailed"/> where gh said
    /// no connection was made - <see cref="GhProcess.Unreached"/>); the forge answering what does not read
    /// (<see cref="HarnessExit.CommandFailed"/>); <c>gh</c> not there (<see cref="HarnessExit.ToolMissing"/>); and the
    /// workflow cache that could not be named, written or read, or whose lock was held (<see cref="WorkflowCache.FailedExit"/>).
    /// </exception>
    public async Task<WorkflowRemoteResolution> ResolveAsync(
        WorkflowRemoteSource source,
        WorkflowSourcesConfig? settings,
        string workingDirectory,
        Func<string, string?> environment,
        string? recorded,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);

        if (WorkflowRemote.RefProblem(source.Ref) is { } badRef)
        {
            throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}' names {badRef}; nothing was asked of the forge");
        }

        Trusted(source, settings);

        if (recorded is not null && !WorkflowRemoteSource.IsCommit(recorded))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}': the run's own record names '{recorded}' as the commit it began with, which is no commit's 40 hex characters; nothing was asked of the forge");
        }

        // The cache is named before anything is asked of it or of the forge: a process with nowhere to keep it is told so
        // at once, as the cache's own failure, and asks the forge nothing it could not keep.
        _ = _cache.Directory;

        // A command an agent runs reads what the person's cache holds and nothing else: it reaches no forge with the
        // person's login, and writes nothing of the tool's own. It is told by the one rule run-side-task is told by -
        // the mark, which a child can take away, and the process tree, which it cannot - and one whose tree could not be
        // read is no person's either.
        var origin = await _ancestry.UnderAnAgentAsync(environment, cancellationToken).ConfigureAwait(false);

        if (origin.UnderAnAgent || origin.Unread is not null)
        {
            return CachedOnly(source, settings, recorded, origin);
        }

        var asked = new Asked(source, settings, workingDirectory, environment, cancellationToken);

        // A run's own record of the resolution: the commit it began with, used again however the ref has moved, so a
        // resume is refused by nothing it cannot help.
        if (recorded is not null)
        {
            return await ReadAsync(asked, recorded, proven: false, resolution: null).ConfigureAwait(false);
        }

        if (WorkflowRemoteSource.IsCommit(source.Ref))
        {
            return await ReadAsync(asked, source.Ref, proven: false, resolution: null).ConfigureAwait(false);
        }

        Pin(source, WorkflowSourceRules.PinOf(settings));

        return await ResolveRefAsync(asked).ConfigureAwait(false);
    }

    /// <summary>
    /// What the configuration says of <paramref name="source"/>: that its repository is allowed and its host named.
    /// Nothing else trusts a remote source, for a workflow it brings directs agents and machine steps in the calling
    /// repository.
    /// </summary>
    private static void Trusted(WorkflowRemoteSource source, WorkflowSourcesConfig? settings)
    {
        if (!source.Host.Equals(WorkflowSourceRules.GitHub, StringComparison.OrdinalIgnoreCase)
            && !(settings?.Hosts ?? new Dictionary<string, WorkflowHostConfig>(StringComparer.OrdinalIgnoreCase)).ContainsKey(source.Host))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' names the host '{source.Host}', which the configuration does not: workflowSources.hosts names the GitHub Enterprise hosts a use may reach, and none names this one");
        }

        if (!WorkflowSourceRules.Allows(settings?.Allow ?? [], source.Host, source.Owner, source.Repo))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' names the repository '{source.Owner}/{source.Repo}', which workflowSources.allow does not allow; a remote workflow directs agents and machine steps in this repository, so it is trusted by configuration alone"
                    + (settings is null || settings.Allow.Count == 0
                        ? "; the allow list is empty, so no remote source is allowed until the repository allows its owner"
                        : $": {string.Join(", ", settings.Allow)}"));
        }
    }

    /// <summary>
    /// That <paramref name="source"/>'s ref is one the pin takes: a full commit SHA always; a tag or a branch where
    /// the pin is not <c>commit</c> - and the branch, which moves, is told from the tag by the resolution, which allows
    /// it only where the pin says <c>any</c>.
    /// </summary>
    private static void Pin(WorkflowRemoteSource source, string pin)
    {
        if (pin != WorkflowSourceRules.PinCommit)
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.Refused,
            $"'{source.Spelled}' names the ref '{source.Ref}', which is no full commit SHA; workflowSources.pin is commit, which takes a full commit SHA only - a tag or a branch moves, and a commit is what it is");
    }

    /// <summary>
    /// The commit a ref resolves to, through the named repository's own refs: its tag of that name, peeled to the commit
    /// it names, and where it has none - and the pin says <c>any</c> - its branch of that name, which moves. Where the
    /// forge could not be reached, the configuration's <c>offline</c> says what stands in; a forge that answered is never
    /// that, and what it refused is refused naming what it said.
    /// </summary>
    private async Task<WorkflowRemoteResolution> ResolveRefAsync(Asked asked)
    {
        var source = asked.Source;
        string commit;
        bool branch;

        try
        {
            var tag = await ApiAsync(asked, $"repos/{source.Owner}/{source.Repo}/git/ref/tags/{Escaped(source.Ref)}").ConfigureAwait(false);

            if (tag is not null)
            {
                commit = await PeeledAsync(asked, tag, $"the tag '{source.Ref}'").ConfigureAwait(false);
                branch = false;
            }
            else
            {
                // A 404 for the tag is also what a repository that is not there, or that the credential cannot see,
                // answers: the repository is asked before the ref is said to be no tag of it.
                _ = await RepositoryAsync(asked).ConfigureAwait(false);

                if (WorkflowSourceRules.PinOf(asked.Settings) != WorkflowSourceRules.PinAny)
                {
                    throw new HarnessException(
                        HarnessExit.Refused,
                        $"'{source.Spelled}' names the ref '{source.Ref}', which is no tag of '{source.Owner}/{source.Repo}'; workflowSources.pin is {WorkflowSourceRules.PinOf(asked.Settings)}, which takes a tag or a full commit SHA - a branch moves, and what a run ran must be what it said");
                }

                var head = await ApiAsync(asked, $"repos/{source.Owner}/{source.Repo}/git/ref/heads/{Escaped(source.Ref)}").ConfigureAwait(false)
                    ?? throw new HarnessException(
                        HarnessExit.Refused,
                        $"'{source.Spelled}' names the ref '{source.Ref}', which is neither a tag nor a branch of '{source.Owner}/{source.Repo}'; nothing was cached");

                commit = Peeled(source, head, $"the branch '{source.Ref}'") is ({ } sha, "commit")
                    ? sha
                    : throw Malformed(source, $"the branch '{source.Ref}'", "it names no commit");
                branch = true;
            }
        }
        catch (HarnessException failed) when (GhProcess.Unreached(failed))
        {
            return LastResolved(source, asked.Settings, failed);
        }

        var resolution = new WorkflowCacheResolution(source.Host, source.Owner, source.Repo, source.Directory, source.Ref, commit, branch, _clock.GetUtcNow());
        var resolved = await ReadAsync(asked, commit, proven: true, resolution).ConfigureAwait(false);

        if (branch)
        {
            _output?.Warn(Said, $"'{source.Spelled}' names the branch '{source.Ref}', which moves: it resolved to the commit {commit} now, and may resolve to another at the next run - pin it by a tag or a commit where what a run ran must be what it said");
        }

        return resolved with { Moving = branch };
    }

    /// <summary>
    /// The cache's newest resolution of <paramref name="source"/>'s ref, where the forge could not be reached and the
    /// configuration says <c>last-resolved</c> - held to the pin (<see cref="CachedResolution"/>) and warned of. Where the
    /// configuration says <c>refuse</c>, or the cache holds no whole resolution, nothing stands in and the failure keeps
    /// the code of the call that did not reach the forge, naming what the forge's tool said: the network not being there
    /// is no refusal of the source, and never a problem of the definition's.
    /// </summary>
    private WorkflowRemoteResolution LastResolved(WorkflowRemoteSource source, WorkflowSourcesConfig? settings, HarnessException failed)
    {
        var unreached = $"'{source.Spelled}' could not be resolved, the forge not being reached: {failed.Message.TrimEnd('.')}";

        if (WorkflowSourceRules.OfflineOf(settings) != WorkflowSourceRules.OfflineLastResolved)
        {
            throw new HarnessException(
                failed.ExitCode,
                $"{unreached}; workflowSources.offline is refuse, so nothing stands in - last-resolved would stand at the cache's newest resolution of the ref",
                failed);
        }

        var last = CachedResolution(source, settings)
            ?? throw new HarnessException(failed.ExitCode, $"{unreached}; and the workflow cache holds no resolution of '{source.Ref}'", failed);

        var entry = Found(source, last.Commit);
        var (cached, broken) = entry is null ? (null, "it holds no entry for the commit") : Whole(source, entry);

        if (cached is null)
        {
            throw new HarnessException(
                failed.ExitCode,
                $"{unreached}; the workflow cache's newest resolution of '{source.Ref}' is the commit '{last.Commit}', whose files are not whole in the cache ({broken}); it is fetched again where the forge is reached",
                failed);
        }

        _output?.Warn(Said, $"{unreached}; the workflow cache's newest resolution of '{source.Ref}' stands in, the commit {last.Commit} it resolved to on {Day(last.ResolvedAt)}, as workflowSources.offline says last-resolved");

        // What was read and hashed stands, the forge not being there to fetch it again; a pruning that removed the entry
        // as it was read is said.
        if (Hold(source, cached.Entry, resolution: null) is HoldAnswer.Gone)
        {
            _output?.Warn(Said, $"'{source.Spelled}': the workflow cache's entry for the commit {last.Commit} was pruned as it was read; what was read stands, and it is fetched again where the forge is reached");
        }

        return cached with { Moving = last.Branch, FromLastResolved = true };
    }

    /// <summary>
    /// The cache's newest resolution of <paramref name="source"/>'s ref where it is one the pin takes - the one rule a
    /// resolution the cache stands in with is held to, offline and under an agent alike: a ref the cache last resolved as
    /// a branch is refused where the pin is not <c>any</c>, as the forge's own answer would be, for the cache standing in
    /// never runs what the configuration refuses. Null where the cache recorded none.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The newest resolution is a branch the pin refuses (<see cref="HarnessExit.Refused"/>); the record could not be read
    /// (<see cref="WorkflowCache.FailedExit"/>).
    /// </exception>
    private WorkflowCacheResolution? CachedResolution(WorkflowRemoteSource source, WorkflowSourcesConfig? settings)
    {
        var last = _cache.LastResolved(source.Host, source.Owner, source.Repo, source.Directory, source.Ref);
        var pin = WorkflowSourceRules.PinOf(settings);

        return last is { Branch: true } && pin != WorkflowSourceRules.PinAny
            ? throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' names the ref '{source.Ref}', which the workflow cache last resolved as a branch, and the cache stands in for no ref the pin refuses: workflowSources.pin is {pin}, which takes a tag or a full commit SHA - a branch moves")
            : last;
    }

    /// <summary>
    /// The workflow resolved from the cache alone, for a command an agent runs or one that cannot tell it is no agent's: the
    /// commit the run's record or the source names, or the cache's newest resolution of its ref that the pin takes - read,
    /// its every file hashed again, and nothing written, not even the use's date. Refused, naming the command a person runs
    /// to fetch it, where the cache does not hold it whole.
    /// </summary>
    private WorkflowRemoteResolution CachedOnly(WorkflowRemoteSource source, WorkflowSourcesConfig? settings, string? recorded, AgentAncestryAnswer origin)
    {
        string commit;
        var branch = false;
        WorkflowCacheResolution? last = null;

        if (recorded is not null)
        {
            commit = recorded;
        }
        else if (WorkflowRemoteSource.IsCommit(source.Ref))
        {
            commit = source.Ref;
        }
        else
        {
            Pin(source, WorkflowSourceRules.PinOf(settings));

            last = CachedResolution(source, settings) ?? throw Unfetched(source, $"it holds no resolution of '{source.Ref}'", origin);
            (commit, branch) = (last.Commit, last.Branch);
        }

        var entry = Found(source, commit) ?? throw Unfetched(source, $"it holds no entry for the commit '{commit}'", origin);
        var (cached, broken) = Whole(source, entry);

        if (cached is null)
        {
            throw Unfetched(source, $"its files of the commit '{commit}' are not whole: {broken}", origin);
        }

        if (last is not null)
        {
            _output?.Warn(Said, $"'{source.Spelled}' was resolved from the workflow cache alone, for {Whose(origin)} never reaches the forge: it stands at the commit {commit} the cache's newest resolution of '{source.Ref}' names, from {Day(last.ResolvedAt)}");
        }

        return cached with { Moving = branch };
    }

    /// <summary>What a command that reads the cache alone is, as its refusals and warnings say it.</summary>
    private static string Whose(AgentAncestryAnswer origin)
        => origin.UnderAnAgent ? "a command an agent runs" : "a command that cannot tell it is no agent's";

    /// <summary>The refusal of a resolution a command that reads the cache alone would need the forge for.</summary>
    private static HarnessException Unfetched(WorkflowRemoteSource source, string why, AgentAncestryAnswer origin)
        => new(
            HarnessExit.Refused,
            $"'{source.Spelled}' is not in the workflow cache as {Whose(origin)} needs it ({why}): "
                + (origin is { UnderAnAgent: false, Unread: { } unread } ? $"{unread} so whether an agent runs it could not be told, and " : string.Empty)
                + $"such a command never reaches the forge nor writes the cache, so a person runs '{ToolPackage.Command} {WorkflowValidationService.CommandName}' to fetch it; nothing was asked of the forge");

    /// <summary>
    /// The workflow at <paramref name="commit"/>, from the cache where it is held whole - read with no network, every
    /// file of it hashed again, and its use recorded with the look that it is still there - else proved the repository's
    /// own where <paramref name="proven"/> is not, fetched, checked and cached: an entry a pruning removed as it was read
    /// is fetched again, never answered as held.
    /// </summary>
    private async Task<WorkflowRemoteResolution> ReadAsync(Asked asked, string commit, bool proven, WorkflowCacheResolution? resolution)
    {
        var source = asked.Source;

        if (Found(source, commit) is { } entry)
        {
            var (cached, broken) = Whole(source, entry);

            if (cached is not null)
            {
                if (Hold(source, entry, resolution) is not HoldAnswer.Gone)
                {
                    return cached;
                }

                _output?.Warn(Said, $"'{source.Spelled}': the workflow cache's entry for the commit {commit} was pruned as it was read; its directory is fetched again whole");
            }
            else
            {
                _output?.Warn(Said, $"'{source.Spelled}': the workflow cache's entry for the commit {commit} is not whole ({broken}); its directory is fetched again whole");
            }
        }

        if (!proven)
        {
            await ProveAsync(asked, commit).ConfigureAwait(false);
        }

        return await FetchAsync(asked, commit, resolution).ConfigureAwait(false);
    }

    /// <summary>
    /// The cache's entry for <paramref name="source"/>'s directory at <paramref name="commit"/>; null where it holds none,
    /// or one whose record could not be read, which is said and fetched again over it.
    /// </summary>
    private WorkflowCacheEntry? Found(WorkflowRemoteSource source, string commit)
    {
        try
        {
            return _cache.Find(source.Host, source.Owner, source.Repo, commit, source.Directory);
        }
        catch (HarnessException unread) when (unread.ExitCode == WorkflowCache.FailedExit)
        {
            _output?.Warn(Said, $"'{source.Spelled}': {unread.Message}");
            return null;
        }
    }

    /// <summary>
    /// The cached resolution of <paramref name="source"/> at its entry's commit, every file of the entry hashed again as
    /// it is read; null, with which file is not whole, where one is not - a blob changed on disk, or gone.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The entry holds no file the source names (<see cref="HarnessExit.Refused"/>): the commit's directory holds none,
    /// and fetching it again would hold none either; or the yml is not UTF-8 text (<see cref="HarnessExit.ConfigInvalid"/>).
    /// </exception>
    private (WorkflowRemoteResolution? Resolution, string? Broken) Whole(WorkflowRemoteSource source, WorkflowCacheEntry entry)
    {
        if (!entry.Files.Any(file => file.Path == source.File))
        {
            throw NoSuchFile(source, entry.Commit, entry.Files.Select(file => file.Path));
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var file in entry.Files)
        {
            try
            {
                files[file.Path] = _cache.ReadBytes(entry, file.Path)!;
            }
            catch (HarnessException broken)
            {
                return (null, broken.Message);
            }
        }

        return (new WorkflowRemoteResolution(source, entry.Commit, entry, WorkflowCache.Text(files[source.File], $"'{source.Spelled}'"), files), null);
    }

    /// <summary>How recording a use went: recorded, gone - a pruning removed the entry as it was read - or not recorded, the cache not being written.</summary>
    private enum HoldAnswer
    {
        Held,
        Gone,
        Unrecorded,
    }

    /// <summary>
    /// Records that a run used <paramref name="entry"/> now, and the resolution that led to it, in one step with the look
    /// that the entry is still there (<see cref="WorkflowCache.Hold"/>); a cache that cannot be written is warned of, and
    /// what was read stands, for only the use's date and the resolution go unkept.
    /// </summary>
    private HoldAnswer Hold(WorkflowRemoteSource source, WorkflowCacheEntry entry, WorkflowCacheResolution? resolution)
    {
        try
        {
            return _cache.Hold(entry, resolution, _clock.GetUtcNow()) is null ? HoldAnswer.Gone : HoldAnswer.Held;
        }
        catch (HarnessException unwritten)
        {
            _output?.Warn(Said, $"'{source.Spelled}': {unwritten.Message}");
            return HoldAnswer.Unrecorded;
        }
    }

    /// <summary>
    /// Proves <paramref name="commit"/>, named by its SHA, a commit of the named repository's own: one its default branch,
    /// or a tag or branch the forge lists, reaches. GitHub serves a commit of any fork of a repository under the
    /// repository's own name, so a SHA alone names nothing the allow list allowed.
    /// </summary>
    /// <exception cref="HarnessException">No branch or tag of the repository reaches it (<see cref="HarnessExit.Refused"/>).</exception>
    private async Task ProveAsync(Asked asked, string commit)
    {
        var source = asked.Source;
        var main = await RepositoryAsync(asked).ConfigureAwait(false);

        if (await ReachesAsync(asked, main, commit).ConfigureAwait(false))
        {
            return;
        }

        var refs = new List<(string Name, string Head)>();

        foreach (var kind in new[] { "tags", "branches" })
        {
            var listed = await ApiAsync(asked, $"repos/{source.Owner}/{source.Repo}/{kind}?per_page=100", "--jq", ".[] | .name + \" \" + .commit.sha").ConfigureAwait(false) ?? string.Empty;

            foreach (var line in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var at = line.LastIndexOf(' ');

                refs.Add(at > 0 ? (line[..at], line[(at + 1)..]) : throw Malformed(source, $"the {kind} of '{source.Owner}/{source.Repo}'", $"'{line}' names no ref and commit"));
            }
        }

        if (refs.Any(listed => listed.Head == commit))
        {
            return;
        }

        foreach (var (name, _) in refs.Where(listed => listed.Name != main).Take(MostRefsCompared))
        {
            if (await ReachesAsync(asked, name, commit).ConfigureAwait(false))
            {
                return;
            }
        }

        throw new HarnessException(
            HarnessExit.Refused,
            $"'{source.Spelled}' names the commit '{commit}', which no branch or tag of '{source.Owner}/{source.Repo}' reaches - its default branch '{main}', and the first {MostRefsCompared.ToString(CultureInfo.InvariantCulture)} of the tags and branches the forge lists, were compared: GitHub serves a commit of any fork of a repository under the repository's own name, and one that is not the repository's own is never run; name a tag of it, or a commit one of its branches or tags holds. Nothing was cached");
    }

    /// <summary>Whether <paramref name="name"/>, a branch or a tag of the repository, reaches <paramref name="commit"/>: the commit is it, or behind it.</summary>
    private async Task<bool> ReachesAsync(Asked asked, string name, string commit)
    {
        var status = await ApiAsync(asked, $"repos/{asked.Source.Owner}/{asked.Source.Repo}/compare/{Escaped(name)}...{commit}", "--jq", ".status").ConfigureAwait(false);

        return status?.Trim() is "behind" or "identical";
    }

    /// <summary>The repository's default branch, as the forge answers the repository's own record.</summary>
    /// <exception cref="HarnessException">The repository is not there, or the credential cannot see it (<see cref="HarnessExit.Refused"/>).</exception>
    private async Task<string> RepositoryAsync(Asked asked)
    {
        var source = asked.Source;
        var answer = await ApiAsync(asked, $"repos/{source.Owner}/{source.Repo}").ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' names the repository '{source.Owner}/{source.Repo}' on '{source.Host}', which the forge answers is not there: it is misspelt, or the credential gh reaches '{source.Host}' with cannot see it; nothing was cached");

        return Read(source, $"the repository '{source.Owner}/{source.Repo}'", answer, node => Text(node, "default_branch"));
    }

    /// <summary>
    /// The commit a tag's ref names: the object it points at, each tag object on the way peeled to what it points at in
    /// turn, until a commit - an annotated tag's ref names its tag object, never the commit.
    /// </summary>
    private async Task<string> PeeledAsync(Asked asked, string answer, string what)
    {
        var source = asked.Source;
        var (sha, type) = Peeled(source, answer, what);

        for (var peels = 0; type == "tag"; peels++)
        {
            if (peels == MostPeels)
            {
                throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}': {what} names a tag of a tag {MostPeels.ToString(CultureInfo.InvariantCulture)} deep, past which none is peeled; nothing was cached");
            }

            var tag = await ApiAsync(asked, $"repos/{source.Owner}/{source.Repo}/git/tags/{sha}").ConfigureAwait(false)
                ?? throw Malformed(source, what, $"the tag object '{sha}' it names is not there");

            (sha, type) = Peeled(source, tag, what);
        }

        return type == "commit"
            ? sha
            : throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}': {what} names a {type}, '{sha}', and no commit; a workflow is read at a commit. Nothing was cached");
    }

    /// <summary>The object a ref's or a tag object's answer points at: its hash and its type.</summary>
    private static (string Sha, string Type) Peeled(WorkflowRemoteSource source, string answer, string what)
        => Read(source, what, answer, node =>
        {
            var target = node["object"] as JsonObject ?? throw new JsonException("it names no object");

            return (Sha(target, "sha"), Text(target, "type"));
        });

    /// <summary>
    /// The workflow's directory at <paramref name="commit"/>: the commit's tree walked to the directory's own, every path
    /// under it held to the tree's rule - no symbolic link, no submodule, the listing whole - the file the use names among
    /// them, every blob fetched as bytes and checked against the tree's hash of it, the whole bounded, and kept in the
    /// cache only once every check holds, so a mismatch or a bound refuses with nothing cached.
    /// </summary>
    private async Task<WorkflowRemoteResolution> FetchAsync(Asked asked, string commit, WorkflowCacheResolution? resolution)
    {
        var source = asked.Source;
        var repository = $"repos/{source.Owner}/{source.Repo}";

        var commitAnswer = await ApiAsync(asked, $"{repository}/git/commits/{commit}").ConfigureAwait(false)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}': '{source.Owner}/{source.Repo}' holds no commit '{commit}'; nothing was cached");

        var tree = Read(source, $"the commit '{commit}'", commitAnswer, node =>
            Sha(node, "sha") == commit
                ? Sha(node["tree"] as JsonObject ?? throw new JsonException("it names no tree"), "sha")
                : throw new JsonException($"it is the commit '{Sha(node, "sha")}'"));

        // The commit's tree, walked to the workflow's directory a part at a time: the directory's own listing is whole
        // where the repository's would be cut short, and is the directory at this commit and no other.
        foreach (var part in source.Directory.Split('/'))
        {
            var listing = Listing(source, $"the tree '{tree}'", await ApiAsync(asked, $"{repository}/git/trees/{tree}").ConfigureAwait(false));

            tree = listing.FirstOrDefault(item => item.Path == part && item.Type == "tree") is { Sha.Length: > 0 } found
                ? found.Sha
                : throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}' resolved to the commit '{commit}', whose tree holds no directory '{source.Directory}'; nothing was cached");
        }

        var items = Listing(source, $"the directory '{source.Directory}'", await ApiAsync(asked, $"{repository}/git/trees/{tree}?recursive=1").ConfigureAwait(false));

        if (WorkflowCache.TreePathProblems([.. items.Select(item => item.Path)]) is { Count: > 0 } paths)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' resolved to the commit '{commit}', whose directory '{source.Directory}' holds a path no workflow's may: {string.Join("; ", paths)}. Nothing was fetched");
        }

        var blobs = new List<TreeItem>();

        foreach (var item in items)
        {
            switch (item.Type, item.Mode)
            {
                case ("tree", _):
                    continue;
                case ("blob", "120000"):
                    throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}' resolved to the commit '{commit}', whose '{source.Directory}/{item.Path}' is a symbolic link: a workflow's files are its directory's own, and a link is followed nowhere. Nothing was fetched");
                case ("commit", _):
                    throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}' resolved to the commit '{commit}', whose '{source.Directory}/{item.Path}' is a submodule: a workflow's files are its directory's own, and another repository's are not fetched with them. Nothing was fetched");
                case ("blob", var mode) when WorkflowCache.FileModes.Contains(mode, StringComparer.Ordinal) && item.Size is not null:
                    blobs.Add(item);
                    continue;
                default:
                    throw Malformed(source, $"the directory '{source.Directory}'", $"'{item.Path}' is a {item.Type} of mode {item.Mode}{(item.Size is null ? " with no size" : string.Empty)}, which is no file a workflow's directory holds");
            }
        }

        var file = source.File[(source.Directory.Length + 1)..];

        if (!blobs.Any(blob => blob.Path == file))
        {
            throw NoSuchFile(source, commit, blobs.Select(blob => $"{source.Directory}/{blob.Path}"));
        }

        if (blobs.Count > WorkflowCache.MostFiles)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' resolved to a directory of {blobs.Count.ToString(CultureInfo.InvariantCulture)} files, past the fetch's bound of {WorkflowCache.MostFiles.ToString(CultureInfo.InvariantCulture)}; nothing was fetched");
        }

        var bytes = blobs.Sum(blob => blob.Size!.Value);

        if (bytes > WorkflowCache.MostBytes)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' resolved to a directory of {bytes.ToString(CultureInfo.InvariantCulture)} bytes, past the fetch's bound of {WorkflowCache.MostBytes.ToString(CultureInfo.InvariantCulture)}; nothing was fetched");
        }

        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var blob in blobs)
        {
            var path = $"{source.Directory}/{blob.Path}";
            var answer = await ApiAsync(asked, $"{repository}/git/blobs/{blob.Sha}").ConfigureAwait(false)
                ?? throw Malformed(source, $"the blob '{blob.Sha}' of '{path}'", "it is not there");
            var content = Read(source, $"the blob '{blob.Sha}' of '{path}'", answer, node => Text(node, "encoding") switch
            {
                "base64" => Convert.FromBase64String(Text(node, "content").Replace("\n", string.Empty, StringComparison.Ordinal)),
                "utf-8" => System.Text.Encoding.UTF8.GetBytes(Text(node, "content")),
                var other => throw new JsonException($"its encoding is '{other}'"),
            });

            if (WorkflowCache.BlobSha(content) != blob.Sha)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source.Spelled}': the blob '{blob.Sha}' of '{path}' does not hash to what the commit's tree says of it; nothing was cached");
            }

            contents[path] = content;
        }

        var now = _clock.GetUtcNow();
        var written = new WorkflowCacheEntry(
            source.Host,
            source.Owner,
            source.Repo,
            commit,
            source.Directory,
            now,
            now,
            [.. blobs.Select(blob => new WorkflowCacheFile($"{source.Directory}/{blob.Path}", blob.Sha, blob.Mode))]);

        // The yml is read as text before anything is cached, so a definition that is not text caches nothing.
        var definition = WorkflowCache.Text(contents[source.File], $"'{source.Spelled}'");

        _cache.Write(written, contents, resolution);

        return new WorkflowRemoteResolution(source, commit, written, definition, contents);
    }

    /// <summary>The refusal of a use whose file the commit's directory does not hold, naming what it holds.</summary>
    private static HarnessException NoSuchFile(WorkflowRemoteSource source, string commit, IEnumerable<string> held)
    {
        var workflows = held.Where(path => path.EndsWith(HarnessLayout.WorkflowFileExtension, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        return new HarnessException(
            HarnessExit.Refused,
            $"'{source.Spelled}' names '{source.File}', which the directory '{source.Directory}' does not hold at the commit '{commit}'"
                + (workflows.Count == 0 ? ": it holds no workflow file" : $": its workflow files are {string.Join(", ", workflows.Select(path => $"'{path}'"))}")
                + "; nothing was fetched");
    }

    /// <summary>One entry of a tree's listing: its path under the listed tree, its type, mode and hash, and its size where it is a blob.</summary>
    private sealed record TreeItem(string Path, string Type, string Mode, string Sha, long? Size);

    /// <summary>Every entry of a tree's listing, refused where the listing was cut short - a part of a directory is no directory.</summary>
    private static List<TreeItem> Listing(WorkflowRemoteSource source, string what, string? answer)
        => Read(source, what, answer ?? throw Malformed(source, what, "it is not there"), node =>
        {
            if (node["truncated"] is JsonValue truncated && truncated.TryGetValue<bool>(out var cut) && cut)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source.Spelled}': the forge listed {what} cut short, past what one answer of it holds; a part of a directory is not fetched as the directory. Nothing was fetched");
            }

            return (node["tree"] as JsonArray ?? throw new JsonException("it holds no tree"))
                .Select(item => item as JsonObject ?? throw new JsonException("an entry of it is no object"))
                .Select(item => new TreeItem(
                    Text(item, "path"),
                    Text(item, "type"),
                    Text(item, "mode"),
                    Sha(item, "sha"),
                    item["size"] is JsonValue size ? size.GetValue<long>() : null))
                .ToList();
        });

    /// <summary>
    /// What <paramref name="read"/> takes from <paramref name="answer"/>, parsed as JSON: any shape it does not read as
    /// refused naming what was asked and why, never an exception of the runtime's.
    /// </summary>
    private static T Read<T>(WorkflowRemoteSource source, string what, string answer, Func<JsonObject, T> read)
    {
        try
        {
            return read(JsonNode.Parse(answer) as JsonObject ?? throw new JsonException("it is no JSON object"));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw Malformed(source, what, exception.Message.TrimEnd('.'));
        }
    }

    /// <summary>The string <paramref name="name"/> of <paramref name="node"/>.</summary>
    private static string Text(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new JsonException($"its '{name}' is no string");

    /// <summary>The git hash <paramref name="name"/> of <paramref name="node"/>: 40 hex characters, lower case.</summary>
    private static string Sha(JsonObject node, string name)
        => Text(node, name) is var sha && WorkflowRemoteSource.IsCommit(sha) ? sha : throw new JsonException($"its '{name}' is no git hash of 40 hex characters");

    private static HarnessException Malformed(WorkflowRemoteSource source, string what, string why)
        => new(
            HarnessExit.CommandFailed,
            $"'{source.Spelled}' could not be resolved: the forge answered {what} with what does not read as one ({why}); nothing was cached");

    /// <summary>A ref put into an address a part at a time, each escaped, so no character of it is read as the address's own.</summary>
    private static string Escaped(string @ref)
        => string.Join('/', @ref.Split('/').Select(Uri.EscapeDataString));

    private static string Day(DateTimeOffset when) => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// One <c>gh api</c> call to the source's host - named by <c>--hostname</c> always, and <c>GH_HOST</c> taken away, so
    /// no call reaches another - its answer whole, or null where the forge answered that the thing asked is not there.
    /// A host beyond github.com is given the one credential its configuration names, as the one variable gh reads for
    /// it, and none of the machine's tokens; the credential is written nowhere else. A forge that answered with a
    /// refusal is refused naming what it said; one that could not be reached is raised as the call raised it
    /// (<see cref="GhProcess.Unreached"/>).
    /// </summary>
    private async Task<string?> ApiAsync(Asked asked, string path, params string[] more)
    {
        var source = asked.Source;
        string? token = null;

        if (!source.Host.Equals(WorkflowSourceRules.GitHub, StringComparison.OrdinalIgnoreCase))
        {
            var variable = WorkflowSourceRules.CredentialOf(asked.Settings, source.Host)
                ?? throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source.Spelled}' names the host '{source.Host}', for which workflowSources.hosts names no credential: a host beyond github.com is reached with the one credential its entry names, and never with the machine's own tokens; nothing was asked of it");

            token = asked.Environment(variable) is { Length: > 0 } value
                ? value
                : throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source.Spelled}' names the host '{source.Host}', whose credential is the environment variable '{variable}', which is not set in this process; nothing was asked of the host, and no other token is given to it");
        }

        try
        {
            return await _gh.RunAsync(
                asked.WorkingDirectory,
                ["api", "--hostname", source.Host, path, .. more],
                $"so '{source.Spelled}' was not resolved",
                null,
                asked.CancellationToken,
                withEnvironment: GhProcess.HostEnvironment(source.Host, token)).ConfigureAwait(false);
        }
        catch (HarnessException failed) when (failed.ExitCode == HarnessExit.CommandFailed && failed.Message.Contains("(HTTP 404)", StringComparison.Ordinal))
        {
            return null;
        }
        catch (HarnessException failed) when (failed.ExitCode == HarnessExit.CommandFailed && !GhProcess.Unreached(failed))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' could not be resolved, the forge refusing what was asked: {failed.Message.TrimEnd('.')}. Nothing stands in for an answer the forge gave",
                failed);
        }
    }

    /// <summary>What one resolution asks of the forge with: the source, the configuration, where gh runs, the environment and the stop.</summary>
    private sealed record Asked(
        WorkflowRemoteSource Source,
        WorkflowSourcesConfig? Settings,
        string WorkingDirectory,
        Func<string, string?> Environment,
        CancellationToken CancellationToken);
}
