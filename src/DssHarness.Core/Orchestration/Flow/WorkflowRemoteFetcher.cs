using System.Globalization;
using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
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
/// What a run's own record holds of one remote use, for a check that goes on with the run: the commit the use resolved to
/// when the run began, and the copy the run keeps of the remote workflows it began with (<see cref="WorkflowCache.OfRun"/>),
/// which the check that let the run start proved the repository's own, read and hashed, and which the run's steps read as
/// they run - so a check that reads the commit there first reads the very bytes the run's steps read.
/// </summary>
/// <param name="Commit">The commit the run recorded for the use.</param>
/// <param name="Kept">The run's own copy of the remote workflows it began with; null where the check is given none.</param>
public sealed record WorkflowRecordedUse(string Commit, WorkflowCache? Kept = null);

/// <summary>
/// The one way a remote workflow is fetched, over git itself (<see cref="WorkflowGitFetch"/>): a ref resolved to a commit
/// of the named repository's own through every ref it lists - a tag peeled to the commit it names, a branch where the pin
/// allows one, and a commit named by its SHA proved to be one a branch or a tag of the repository reaches, by git's own
/// walk over every ref - the commit's tree read whole at the workflow's directory, every path under it held to the tree's
/// rule, every blob fetched in one transfer and checked against the tree's hash of it, of any number and any size, and
/// the whole kept in the workflow cache - read from it with no network where the commit is cached already. A remote source is trusted by
/// configuration alone: the repository's <c>workflowSources</c> allows its owner, names its host and the one credential
/// it is reached with, pins the refs it may name, and says what happens where the forge cannot be reached; nothing of
/// that is decided by a response. A command that runs under an agent - told by the one rule <c>run-side-task</c> is
/// told by, the agent's mark or a process of this tool's above it in the process tree, a tree that cannot be read taken
/// for no person's - never reaches the forge and never writes the cache: it reads what the cache holds, or is refused
/// saying what made it an agent's and where a person fills the cache instead (<see cref="AgentAncestryAnswer.Why"/>).
/// A resolution a run already recorded reads the run's own copy first, then the cache, and the forge only where neither
/// holds it whole - one order of where its files come from, whoever runs the command. A file of the cache's own that
/// could not be read ends as the cache's own failure (<see cref="WorkflowCache.FailedExit"/>) wherever nothing is fetched
/// over it, never as a workflow the cache does not hold.
/// </summary>
/// <param name="git">Runs git, which lists, fetches and reads the remote repository.</param>
/// <param name="fileSystem">Makes and removes the directory a fetch is made in.</param>
/// <param name="cache">The workflow cache the fetch is kept in.</param>
/// <param name="ancestry">Tells whether the command runs under an agent (<see cref="AgentAncestry.UnderAnAgentAsync"/>).</param>
/// <param name="output">Says what a resolution is warned of - a moving ref, the cache standing in - and a transfer that lasts; null says nothing.</param>
/// <param name="clock">Dates a fetch, a use and a resolution; the system's where none is given.</param>
/// <param name="address">The address a source's repository is fetched from; <c>https://host/owner/repo.git</c> where none is given.</param>
public sealed class WorkflowRemoteFetcher(
    IGitClient git,
    IFileSystem fileSystem,
    WorkflowCache cache,
    AgentAncestry ancestry,
    IHarnessOutput? output = null,
    TimeProvider? clock = null,
    Func<WorkflowRemoteSource, string>? address = null)
{
    /// <summary>What a resolution's warnings are said under, whichever command resolved it.</summary>
    public const string Said = "remote-workflow";

    /// <summary>The user name a host's credential is handed under: a token stands in for the password.</summary>
    private const string TokenUser = "x-access-token";

    private readonly WorkflowGitFetch _fetch = new(git, fileSystem, output, clock ?? TimeProvider.System, address);
    private readonly WorkflowCache _cache = cache;
    private readonly AgentAncestry _ancestry = ancestry ?? throw new ArgumentNullException(nameof(ancestry));
    private readonly IHarnessOutput? _output = output;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Resolves the remote source a use names to a commit and a cached directory: the commit the run's own record
    /// says where it has one (<paramref name="recorded"/>, so a resume never resolves again, and a tag or branch
    /// that moved since changes no run already started) - read from the run's own copy first, which the run's start
    /// proved, read and hashed, so neither a pruning of the cache, the network, nor a tag deleted or a branch pushed over
    /// since refuses a run that started - the cache where that commit is held, and the forge where it is not - the
    /// cache's newest resolution standing in where the forge cannot be reached and the configuration says
    /// <c>last-resolved</c>. Where the command runs under an agent, or cannot tell that it does not, nothing beyond the
    /// run's copy and the cache is read.
    /// </summary>
    /// <param name="source">The source a step's <c>use:</c> names, parsed.</param>
    /// <param name="settings">The repository's <c>workflowSources</c>; null where the file holds none.</param>
    /// <param name="workingDirectory">
    /// The tree the use is resolved for. git is run in a directory of the fetch's own, so no configuration of the tree's
    /// own - an address rewritten, a helper named - decides what is fetched or who it is fetched as.
    /// </param>
    /// <param name="environment">Reads this process's environment: a host's credential, and the mark of an agent's command.</param>
    /// <param name="recorded">What this run's own record holds for the source - its commit, and the run's own copy - where one does; null else.</param>
    /// <param name="cancellationToken">Stops the fetch.</param>
    /// <exception cref="HarnessException">
    /// A refusal of the source itself (<see cref="HarnessExit.Refused"/>): the ref is no ref git takes, the source's
    /// repository is not allowed, its host is not named or its credential not there, its ref is not one the pin takes -
    /// the cache's newest resolution of it as much as the forge's - or not the repository's own, the file it names is not
    /// in the directory, or the forge answered and refused what was asked, naming what it said; or its yml is not text
    /// (<see cref="HarnessExit.ConfigInvalid"/>). Every other failure keeps its own code, and is no refusal of the source:
    /// the forge not reached with nothing standing in, as the call that did not reach it raised it
    /// (<see cref="HarnessExit.CommandFailed"/>, git saying it never reached the host - <see cref="WorkflowGitFetch.Unreached"/>);
    /// git failing otherwise, or answering what does not read (<see cref="HarnessExit.CommandFailed"/>); and the
    /// workflow cache that could not be named, written or read, or whose lock was held (<see cref="WorkflowCache.FailedExit"/>).
    /// </exception>
    public async Task<WorkflowRemoteResolution> ResolveAsync(
        WorkflowRemoteSource source,
        WorkflowSourcesConfig? settings,
        string workingDirectory,
        Func<string, string?> environment,
        WorkflowRecordedUse? recorded,
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

        if (recorded is not null && !WorkflowRemoteSource.IsCommit(recorded.Commit))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}': the run's own record names '{recorded.Commit}' as the commit it began with, which is no commit's 40 hex characters; nothing was asked of the forge");
        }

        // A run's own record of the resolution is read from the run's own copy first: what the check that let the run
        // start proved the repository's own, read and hashed, and what the run's steps read - asking the forge nothing and
        // writing nothing, so it serves whoever runs the command, and a resume is refused by nothing it cannot help.
        if (recorded?.Kept is { } kept && Kept(kept, source, recorded.Commit) is { } own)
        {
            return own;
        }

        // The cache is named before anything is asked of it or of the forge: a process with nowhere to keep it is told so
        // at once, as the cache's own failure, and asks the forge nothing it could not keep.
        _ = _cache.Directory;

        // A command an agent runs reads what the person's cache holds and nothing else: it reaches no forge with the
        // person's login, and writes nothing of the tool's own. It is told by the one rule run-side-task is told by -
        // the mark, which a child can take away, and the process tree, which it cannot - and one whose tree could not be
        // read is no person's either.
        var origin = await _ancestry.UnderAnAgentAsync(environment, cancellationToken).ConfigureAwait(false);

        if (origin.TakenForAnAgents)
        {
            return CachedOnly(source, settings, recorded?.Commit, origin);
        }

        var asked = new Asked(source, settings, environment, cancellationToken);

        // A run's own record of the resolution its copy does not hold whole: the commit it began with, used again however
        // the ref has moved, read from the cache, or fetched again and proved the repository's own as any commit named by
        // its SHA is - every branch and tag fetched for the walk that proves it.
        if (recorded is not null)
        {
            return await ReadAsync(asked, recorded.Commit, reference: null, clone => ProvedAsync(clone, source, recorded.Commit, cancellationToken), resolutionOf: null).ConfigureAwait(false);
        }

        if (WorkflowRemoteSource.IsCommit(source.Ref))
        {
            return await ReadAsync(asked, source.Ref, reference: null, clone => ProvedAsync(clone, source, source.Ref, cancellationToken), resolutionOf: null).ConfigureAwait(false);
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
    /// The commit a ref resolves to, through the named repository's own refs, every one of them listed in one call: its
    /// tag of that name, peeled to the commit it names, and where it has none - and the pin says <c>any</c> - its branch of
    /// that name, which moves. A ref of the repository's own proves itself. Where the forge could not be reached, the
    /// configuration's <c>offline</c> says what stands in; a forge that answered is never that, and what it refused is
    /// refused naming what it said.
    /// </summary>
    private async Task<WorkflowRemoteResolution> ResolveRefAsync(Asked asked)
    {
        var source = asked.Source;
        IReadOnlyDictionary<string, WorkflowRemoteRef> refs;

        try
        {
            refs = await _fetch.RefsAsync(source, asked.Credential, asked.CancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException failed) when (WorkflowGitFetch.Unreached(failed))
        {
            return LastResolved(source, asked.Settings, failed);
        }

        string commit;
        bool branch;
        string reference;

        if (refs.TryGetValue($"refs/tags/{source.Ref}", out var tag))
        {
            // The object the remote says the tag peels to, which the cache may hold already; where it is fetched, the tag
            // the clone fetched is peeled there through each tag object it names, to a commit.
            commit = tag.Peeled ?? tag.Object;
            branch = false;
            reference = tag.Name;
        }
        else
        {
            if (WorkflowSourceRules.PinOf(asked.Settings) != WorkflowSourceRules.PinAny)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source.Spelled}' names the ref '{source.Ref}', which is no tag of '{source.Owner}/{source.Repo}'; workflowSources.pin is {WorkflowSourceRules.PinOf(asked.Settings)}, which takes a tag or a full commit SHA - a branch moves, and what a run ran must be what it said");
            }

            commit = refs.TryGetValue($"refs/heads/{source.Ref}", out var head)
                ? head.Object
                : throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source.Spelled}' names the ref '{source.Ref}', which is neither a tag nor a branch of '{source.Owner}/{source.Repo}'; nothing was cached");
            branch = true;
            reference = head.Name;
        }

        // The clone fetches the ref alone, at its tip, and what it fetched is what is read: a ref that moved between the
        // listing and the fetch is read - and recorded - where it stands now.
        var resolved = await ReadAsync(
                asked,
                commit,
                reference,
                async clone =>
                {
                    var fetched = await clone.FetchedAsync(reference, asked.CancellationToken).ConfigureAwait(false)
                        ?? throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}' names the ref '{source.Ref}', which '{source.Owner}/{source.Repo}' no longer holds as it is fetched; nothing was cached");

                    return branch ? fetched : await PeeledAsync(clone, source, fetched, asked.CancellationToken).ConfigureAwait(false);
                },
                at => new WorkflowCacheResolution(source.Host, source.Owner, source.Repo, source.Directory, source.Ref, at, branch, _clock.GetUtcNow()))
            .ConfigureAwait(false);

        commit = resolved.Commit;

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
    /// is no refusal of the source, and never a problem of the definition's. A record or a blob of the cache's that could
    /// not be read is the cache's own failure (<see cref="WorkflowCache.FailedExit"/>), said with the forge not reached,
    /// never taken for a resolution or an entry the cache does not hold.
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

        WorkflowCacheResolution? last;
        Looked looked;

        try
        {
            last = CachedResolution(source, settings);
            looked = last is null ? Looked.Nothing : Lookup(_cache, source, last.Commit);
        }
        catch (HarnessException unread) when (unread.ExitCode == WorkflowCache.FailedExit)
        {
            throw new HarnessException(WorkflowCache.FailedExit, $"{unreached}; and the workflow cache, which would stand in, could not be read: {unread.Message.TrimEnd('.')}", unread);
        }

        if (last is null)
        {
            throw new HarnessException(failed.ExitCode, $"{unreached}; and the workflow cache holds no resolution of '{source.Ref}'", failed);
        }

        if (looked.Resolution is not { } cached)
        {
            throw new HarnessException(
                failed.ExitCode,
                $"{unreached}; the workflow cache's newest resolution of '{source.Ref}' is the commit '{last.Commit}', whose files are not whole in the cache ({looked.Broken ?? "it holds no entry for the commit"}); it is fetched again where the forge is reached",
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
    /// its every file hashed again, and nothing written, not even the use's date. Refused where the cache does not hold it
    /// whole, saying what made the command an agent's and where a person fills the cache instead; a record or a blob of the
    /// cache's that could not be read is the cache's own failure, never a workflow the cache does not hold.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The cache does not hold the workflow whole (<see cref="HarnessExit.Refused"/>); a file of the cache's own could not
    /// be read (<see cref="WorkflowCache.FailedExit"/>).
    /// </exception>
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

        var looked = Lookup(_cache, source, commit);

        if (looked.Resolution is not { } cached)
        {
            throw Unfetched(source, looked.Broken is { } broken ? $"its files of the commit '{commit}' are not whole: {broken}" : $"it holds no entry for the commit '{commit}'", origin);
        }

        if (last is not null)
        {
            _output?.Warn(Said, $"'{source.Spelled}' was resolved from the workflow cache alone, at the commit {commit} the cache's newest resolution of '{source.Ref}' names, from {Day(last.ResolvedAt)}, for this command is taken for an agent's, which never reaches the forge: {origin.Why}");
        }

        return cached with { Moving = branch };
    }

    /// <summary>
    /// The refusal of a resolution a command that reads the cache alone would need the forge for: what the cache lacks,
    /// what made the command an agent's - by the one wording <c>run-side-task</c>'s refusal gives it too
    /// (<see cref="AgentAncestryAnswer.Why"/>) - and what fills the cache from outside it, so the person told no is never
    /// sent to run again the command that was refused.
    /// </summary>
    private static HarnessException Unfetched(WorkflowRemoteSource source, string why, AgentAncestryAnswer origin)
        => new(
            HarnessExit.Refused,
            $"'{source.Spelled}' is not in the workflow cache as this command needs it ({why}), and this command reads the cache alone, taken for an agent's: {origin.Why}; "
                + "such a command never reaches the forge nor writes the cache, and nothing was asked of the forge. "
                + $"A person fills the cache from {AgentAncestryAnswer.APersonsShell}, where '{ToolPackage.Command} {WorkflowValidationService.CommandName}' "
                + $"fetches every remote use of the workflows the repository declares, and '{ToolPackage.Command} {WorkflowExportService.CommandName} --from <source> --to <directory>' the one it names");

    /// <summary>
    /// The workflow at <paramref name="commit"/>, from the cache where it is held whole - read with no network, every
    /// file of it hashed again, and its use recorded with the look that it is still there - else the repository cloned,
    /// the commit made the repository's own by <paramref name="owned"/> - a tag peeled, a commit named by its SHA proved -
    /// and its directory fetched, checked and cached: an entry a pruning removed as it was read is fetched again, never
    /// answered as held, and a record or a blob of the cache's that could not be read is said and fetched again over, the
    /// fetch writing it whole.
    /// </summary>
    /// <param name="asked">The resolution.</param>
    /// <param name="commit">The commit the cache is looked in for.</param>
    /// <param name="reference">The one ref the clone fetches, by its whole name; null for every branch and tag, which a commit named by its SHA is proved by.</param>
    /// <param name="owned">The commit of the repository's own to fetch, given the clone: refused where there is none.</param>
    /// <param name="resolutionOf">The resolution of a ref that leads to a commit, given it; null where a commit was named.</param>
    private async Task<WorkflowRemoteResolution> ReadAsync(
        Asked asked,
        string commit,
        string? reference,
        Func<WorkflowClone, Task<string>> owned,
        Func<string, WorkflowCacheResolution>? resolutionOf)
    {
        var source = asked.Source;
        var resolution = resolutionOf?.Invoke(commit);
        Looked looked;

        try
        {
            looked = Lookup(_cache, source, commit);
        }
        catch (HarnessException unread) when (unread.ExitCode == WorkflowCache.FailedExit)
        {
            _output?.Warn(Said, $"'{source.Spelled}': {unread.Message}");
            looked = Looked.Nothing;
        }

        if (looked.Resolution is { } cached)
        {
            if (Hold(source, cached.Entry, resolution) is not HoldAnswer.Gone)
            {
                return cached;
            }

            _output?.Warn(Said, $"'{source.Spelled}': the workflow cache's entry for the commit {commit} was pruned as it was read; its directory is fetched again whole");
        }
        else if (looked.Broken is { } broken)
        {
            _output?.Warn(Said, $"'{source.Spelled}': the workflow cache's entry for the commit {commit} is not whole ({broken}); its directory is fetched again whole");
        }

        using var clone = await _fetch.CloneAsync(source, asked.Credential, reference, asked.CancellationToken).ConfigureAwait(false);
        var own = await owned(clone).ConfigureAwait(false);

        return await FetchAsync(asked, clone, own, own == commit ? resolution : resolutionOf?.Invoke(own)).ConfigureAwait(false);
    }

    /// <summary>
    /// The workflow at a run's recorded <paramref name="commit"/> as the run's own copy holds it - every file hashed again
    /// as it is read, nothing asked of the forge and nothing written: the copy the check that let the run start proved the
    /// repository's own, read and hashed, and the run's steps read. Null where the copy does not hold it whole, and the
    /// cache and then the forge are asked for it, as at the run's start - the session then keeping it again - a copy that
    /// could not be read said and read past, as the cache's own record is where a fetch follows.
    /// </summary>
    private WorkflowRemoteResolution? Kept(WorkflowCache kept, WorkflowRemoteSource source, string commit)
    {
        try
        {
            return Lookup(kept, source, commit).Resolution;
        }
        catch (HarnessException unread) when (unread.ExitCode == WorkflowCache.FailedExit)
        {
            _output?.Warn(Said, $"'{source.Spelled}': {unread.Message.TrimEnd('.')}; the run's own copy is read past");
            return null;
        }
    }

    /// <summary>
    /// What one cache holds of a source's directory at a commit: the resolution, every file of its entry hashed again as
    /// it is read, where it holds the entry whole; which file of the entry is not whole - a blob changed on disk, or gone -
    /// where it does not; neither where it holds no entry.
    /// </summary>
    /// <param name="Resolution">The resolution read whole; null where the cache does not hold it so.</param>
    /// <param name="Broken">Which file of the entry is not whole, and why; null where it holds the entry whole, or none.</param>
    private sealed record Looked(WorkflowRemoteResolution? Resolution, string? Broken)
    {
        /// <summary>No entry held.</summary>
        public static Looked Nothing { get; } = new(null, null);
    }

    /// <summary>
    /// What <paramref name="cache"/> - the user's workflow cache, or a run's own copy in its shape - holds of
    /// <paramref name="source"/>'s directory at <paramref name="commit"/> (<see cref="Looked"/>): the one reading every
    /// resolution reads a cache by, whatever it does where the cache does not hold it whole.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The entry's record, or a blob of it, is there and could not be read (<see cref="WorkflowCache.FailedExit"/>) - never
    /// taken for one that is not there; the entry holds no file the source names (<see cref="HarnessExit.Refused"/>): the
    /// commit's directory holds none, and fetching it again would hold none either; or the yml is not UTF-8 text
    /// (<see cref="HarnessExit.ConfigInvalid"/>).
    /// </exception>
    private static Looked Lookup(WorkflowCache cache, WorkflowRemoteSource source, string commit)
    {
        if (cache.Find(source.Host, source.Owner, source.Repo, commit, source.Directory) is not { } entry)
        {
            return Looked.Nothing;
        }

        if (!entry.Files.Any(file => file.Path == source.File))
        {
            throw NoSuchFile(source, entry.Commit, entry.Files.Select(file => file.Path));
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var file in entry.Files)
        {
            try
            {
                files[file.Path] = cache.ReadBytes(entry, file.Path)!;
            }
            catch (HarnessException broken) when (broken.ExitCode != WorkflowCache.FailedExit)
            {
                // A blob changed on disk, or gone, leaves the entry not whole; one that could not be read is the cache's
                // own failure, and goes on with its code.
                return new Looked(null, broken.Message);
            }
        }

        return new Looked(new WorkflowRemoteResolution(source, entry.Commit, entry, WorkflowCache.Text(files[source.File], $"'{source.Spelled}'"), files), null);
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
    /// <paramref name="commit"/>, named by its SHA, proved a commit of the named repository's own: one some branch or tag
    /// of it reaches, as git's own walk over every ref the clone fetched answers it. GitHub serves a commit of any fork of a
    /// repository under the repository's own name, so a SHA alone names nothing the allow list allowed.
    /// </summary>
    /// <exception cref="HarnessException">No branch or tag of the repository reaches it (<see cref="HarnessExit.Refused"/>).</exception>
    private static async Task<string> ProvedAsync(WorkflowClone clone, WorkflowRemoteSource source, string commit, CancellationToken cancellationToken)
        => await clone.ReachedAsync(commit, cancellationToken).ConfigureAwait(false)
            ? commit
            : throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' names the commit '{commit}', which no branch or tag of '{source.Owner}/{source.Repo}' reaches - every one of them was walked: GitHub serves a commit of any fork of a repository under the repository's own name, and one that is not the repository's own is never run; name a tag of it, or a commit one of its branches or tags holds. Nothing was cached");

    /// <summary>
    /// The commit the tag object or commit <paramref name="tagged"/> - what the source's tag names - peels to in the clone,
    /// through each tag object on the way, however many (<see cref="WorkflowClone.PeelAsync"/>).
    /// </summary>
    /// <exception cref="HarnessException">The tag peels to what is no commit (<see cref="HarnessExit.Refused"/>).</exception>
    private static async Task<string> PeeledAsync(WorkflowClone clone, WorkflowRemoteSource source, string tagged, CancellationToken cancellationToken)
    {
        var what = $"the tag '{source.Ref}'";
        var (peeled, type) = await clone.PeelAsync(tagged, what, cancellationToken).ConfigureAwait(false);

        return type == "commit"
            ? peeled
            : throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}': {what} names a {type}, '{peeled}', and no commit; a workflow is read at a commit. Nothing was cached");
    }

    /// <summary>
    /// The workflow's directory at <paramref name="commit"/>: the commit's tree read whole at the directory, every path
    /// under it held to the tree's rule - no symbolic link, no submodule - the file the use names among them, every blob
    /// fetched in one transfer and checked against the tree's hash of it, of any number and any size, and kept in the cache
    /// only once every check holds, so a mismatch refuses with nothing cached.
    /// </summary>
    private async Task<WorkflowRemoteResolution> FetchAsync(Asked asked, WorkflowClone clone, string commit, WorkflowCacheResolution? resolution)
    {
        var source = asked.Source;
        var entries = await clone.TreeAsync(commit, source.Directory, asked.CancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}' resolved to the commit '{commit}', whose tree holds no directory '{source.Directory}'; nothing was cached");

        if (WorkflowCache.TreePathProblems([.. entries.Select(entry => entry.Path)]) is { Count: > 0 } paths)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{source.Spelled}' resolved to the commit '{commit}', whose directory '{source.Directory}' holds a path no workflow's may: {string.Join("; ", paths)}. Nothing was fetched");
        }

        var blobs = new List<WorkflowTreeEntry>();

        foreach (var entry in entries)
        {
            switch (entry.Type, entry.Mode)
            {
                case ("tree", _):
                    continue;
                case ("blob", "120000"):
                    throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}' resolved to the commit '{commit}', whose '{source.Directory}/{entry.Path}' is a symbolic link: a workflow's files are its directory's own, and a link is followed nowhere. Nothing was fetched");
                case ("commit", _):
                    throw new HarnessException(HarnessExit.Refused, $"'{source.Spelled}' resolved to the commit '{commit}', whose '{source.Directory}/{entry.Path}' is a submodule: a workflow's files are its directory's own, and another repository's are not fetched with them. Nothing was fetched");
                case ("blob", var mode) when WorkflowCache.FileModes.Contains(mode, StringComparer.Ordinal):
                    blobs.Add(entry);
                    continue;
                default:
                    throw WorkflowGitFetch.Malformed(source, $"the directory '{source.Directory}'", $"'{entry.Path}' is a {entry.Type} of mode {entry.Mode}, which is no file a workflow's directory holds");
            }
        }

        var file = source.File[(source.Directory.Length + 1)..];

        if (!blobs.Any(blob => blob.Path == file))
        {
            throw NoSuchFile(source, commit, blobs.Select(blob => $"{source.Directory}/{blob.Path}"));
        }

        var read = await clone.ReadAsync(commit, source.Directory, [.. blobs.Select(blob => blob.Object).Distinct(StringComparer.Ordinal)], asked.CancellationToken).ConfigureAwait(false);
        var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var blob in blobs)
        {
            var path = $"{source.Directory}/{blob.Path}";
            var content = read[blob.Object];

            // git checks what it fetches; the cache names each blob by this hash, so it is asked again of what is kept.
            if (WorkflowCache.BlobSha(content) != blob.Object)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{source.Spelled}': the blob '{blob.Object}' of '{path}' does not hash to what the commit's tree says of it; nothing was cached");
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
            [.. blobs.Select(blob => new WorkflowCacheFile($"{source.Directory}/{blob.Path}", blob.Object, blob.Mode))]);

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

    private static string Day(DateTimeOffset when) => when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// What one resolution asks the remote with: the source, the configuration, the environment, the stop - and the
    /// credential its host is reached with, asked only the first time something is asked of the remote, so a resolution
    /// the cache answers needs none. github.com is reached with the helpers git's configuration names - the machine's own
    /// login; a host beyond it with the one credential its entry names, and none of the machine's.
    /// </summary>
    private sealed class Asked(
        WorkflowRemoteSource source,
        WorkflowSourcesConfig? settings,
        Func<string, string?> environment,
        CancellationToken cancellationToken)
    {
        private GitCredential? _credential;
        private bool _asked;

        public WorkflowRemoteSource Source { get; } = source;

        public WorkflowSourcesConfig? Settings { get; } = settings;

        public Func<string, string?> Environment { get; } = environment;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        /// <summary>The credential the host is given; null where git's own helpers answer for it.</summary>
        /// <exception cref="HarnessException">A host beyond github.com names no credential, or its variable is not set (<see cref="HarnessExit.Refused"/>).</exception>
        public GitCredential? Credential
        {
            get
            {
                if (!_asked)
                {
                    _credential = Of();
                    _asked = true;
                }

                return _credential;
            }
        }

        private GitCredential? Of()
        {
            if (Source.Host.Equals(WorkflowSourceRules.GitHub, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var variable = WorkflowSourceRules.CredentialOf(Settings, Source.Host)
                ?? throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{Source.Spelled}' names the host '{Source.Host}', for which workflowSources.hosts names no credential: a host beyond github.com is reached with the one credential its entry names, and never with the machine's own tokens; nothing was asked of it");

            return Environment(variable) is { Length: > 0 } value
                ? new GitCredential(TokenUser, value)
                : throw new HarnessException(
                    HarnessExit.Refused,
                    $"'{Source.Spelled}' names the host '{Source.Host}', whose credential is the environment variable '{variable}', which is not set in this process; nothing was asked of the host, and no other token is given to it");
        }
    }
}
