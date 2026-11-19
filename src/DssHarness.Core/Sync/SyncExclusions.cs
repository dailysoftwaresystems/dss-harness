using DssHarness.Core.Configuration;
using DssHarness.Core.Git;
using DssHarness.Core.Results;

using DssHarness.Core.FileSystem;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Sync;

/// <summary>
/// What a sync withholds, and equally what it must never delete.
/// </summary>
/// <remarks>
/// The two are one list on purpose. A path the harness will not write is one it cannot know the
/// source lacks, so deleting it would remove the host's own state rather than a file the source gave
/// up: its git repository, its harness configuration, its worktrees and the build directories that
/// make an incremental build possible.
/// </remarks>
/// <summary>What a look for rooted entries matching nothing found, the whole tree read.</summary>
/// <param name="MatchingNothing">The entry names that protect nothing here though the name exists deeper.</param>
public sealed record RootedEntryReport(IReadOnlyList<string> MatchingNothing);

/// <summary>
/// The search for rooted <c>sync.neverTransfer</c> entries that match nothing (<see cref="SyncExclusions.SearchRootedEntries"/>),
/// made as the tree's manifest is read: told what the walk meets, and asked which withheld directories it goes into.
/// </summary>
public sealed class RootedEntrySearch : IManifestLook
{
    private readonly SyncExclusions _exclusions;
    private readonly IReadOnlySet<string> _absent;
    private readonly HashSet<string> _found = new(StringComparer.Ordinal);

    /// <summary>A search for <paramref name="absent"/>, by <paramref name="exclusions"/>'s rules.</summary>
    internal RootedEntrySearch(SyncExclusions exclusions, IReadOnlySet<string> absent)
    {
        _exclusions = exclusions;
        _absent = absent;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The harness's own directory, but for the worktrees root and what an entry covers, and never git's own directory:
    /// nothing else the manifest withholds is gone into, and nothing at all where no name is looked for.
    /// </remarks>
    public bool Enters(string relativePath)
        => _found.Count < _absent.Count
            && !string.Equals(Name(relativePath), SyncExclusions.NeverRead, StringComparison.Ordinal)
            && _exclusions.Searches(relativePath);

    /// <inheritdoc/>
    public void Met(string relativePath)
    {
        // Files as well as directories: '.env' and '.secrets' - the two names this check was written for - are usually
        // files. Nothing under git's own directory counts: a name there is git's copy of something.
        if (_found.Count < _absent.Count
            && _absent.Contains(Name(relativePath))
            && !UnderGit(relativePath)
            && _exclusions.Counts(relativePath))
        {
            _found.Add(Name(relativePath));
        }
    }

    /// <summary>What the search found, once the walk that made it has read the whole tree.</summary>
    public RootedEntryReport Report() => new([.. _found.Order(StringComparer.Ordinal)]);

    /// <summary>The last part of <paramref name="relativePath"/>.</summary>
    private static string Name(string relativePath) => relativePath[(relativePath.LastIndexOf('/') + 1)..];

    /// <summary>Whether <paramref name="relativePath"/> lies in a directory git keeps for itself, at the root or in a submodule.</summary>
    private static bool UnderGit(string relativePath)
        => relativePath.StartsWith(SyncExclusions.NeverRead + "/", StringComparison.Ordinal)
            || relativePath.Contains("/" + SyncExclusions.NeverRead + "/", StringComparison.Ordinal);
}

public sealed class SyncExclusions
{
    private readonly string[] _withheld;
    private readonly string[] _excluded;
    private readonly string[] _neverTransfer;
    private readonly string[] _coveredByConfiguration;
    private readonly string _worktreesRoot;

    /// <summary>Builds the policy from configuration.</summary>
    /// <param name="sync">The sync section.</param>
    /// <param name="worktreesRoot">The configured worktrees root, withheld with the rest.</param>
    /// <param name="gitIgnored">
    /// What git ignores in the source tree, withheld with the rest. <c>sync.exclude</c> is documented
    /// as naming paths to withhold <em>in addition to</em> these, and without them a repository's
    /// ignored files — a local <c>.env</c>, a virtual environment, an editor's cache — are copied to
    /// every host the moment a leg is placed on one. Withheld rather than merely excluded, because a
    /// path that is local to a machine is local on the far side too: deleting the host's own copy of
    /// one would remove that machine's state rather than a file this tree gave up.
    /// </param>
    public SyncExclusions(SyncConfig sync, string worktreesRoot, IReadOnlyList<string>? gitIgnored = null)
    {
        ArgumentNullException.ThrowIfNull(sync);

        // Kept apart from the rest of the withheld list, because only these came from the setting
        // a message about them names. A worktrees root or a git-ignored path reported as
        // 'sync.neverTransfer names ...' would send a reader to a line that is not in the file, and
        // the fix it suggests would be wrong for it.
        _neverTransfer = [.. sync.EffectiveNeverTransfer
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)];

        _withheld = [.. sync.EffectiveNeverTransfer
            .Concat(TreeFloor.Of(worktreesRoot))
            .Concat(gitIgnored ?? [])
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)];

        _excluded = [.. sync.Exclude
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)];

        _worktreesRoot = Normalize(worktreesRoot);

        // What an entry of the configuration's, or the worktrees root, already covers - where the search
        // for a misplaced entry counts no name: see RootedEntriesMatchingNothing.
        _coveredByConfiguration = [.. _neverTransfer
            .Append(Normalize(worktreesRoot))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Paths that are never transferred and never deleted.</summary>
    public IReadOnlyList<string> Withheld => _withheld;

    /// <summary>Paths that are not transferred, but which a sync may delete from a copy.</summary>
    public IReadOnlyList<string> Excluded => _excluded;

    /// <summary>
    /// This policy with <paramref name="withheld"/> withheld too - not transferred, and protected from deletion -
    /// for the one sync that asks: what that sync's own caller keeps in the copy it is syncing, as a sweep keeps the
    /// directory its build writes in its own worker. The configuration's own statements are untouched.
    /// </summary>
    /// <param name="withheld">The paths to add, spelled as the configuration spells them.</param>
    public SyncExclusions With(IReadOnlyList<string> withheld)
    {
        ArgumentNullException.ThrowIfNull(withheld);

        var extra = withheld.Select(Normalize).Where(path => path.Length > 0).ToArray();

        return extra.Length == 0
            ? this
            : new SyncExclusions(
                [.. _withheld.Concat(extra).Distinct(StringComparer.Ordinal)],
                _excluded,
                _neverTransfer,
                _coveredByConfiguration,
                _worktreesRoot);
    }

    private SyncExclusions(string[] withheld, string[] excluded, string[] neverTransfer, string[] coveredByConfiguration, string worktreesRoot)
    {
        _withheld = withheld;
        _excluded = excluded;
        _neverTransfer = neverTransfer;
        _coveredByConfiguration = coveredByConfiguration;
        _worktreesRoot = worktreesRoot;
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> is withheld from transfer. True for a withheld or
    /// excluded path and for everything beneath one.
    /// </summary>
    /// <param name="relativePath">A path relative to the tree root, with forward separators.</param>
    public bool IsWithheldFromTransfer(string relativePath)
        => HarnessDirectorySync.Withholds(relativePath)
            || Matches(_withheld, relativePath)
            || Matches(_excluded, relativePath);

    /// <summary>
    /// Whether <paramref name="relativePath"/> is protected from deletion. True only for the
    /// withheld list: an excluded path is one the source chooses not to send, and a copy that keeps
    /// it for ever is a copy of a tree that no longer exists.
    /// </summary>
    /// <param name="relativePath">A path relative to the tree root, with forward separators.</param>
    public bool IsProtectedFromDeletion(string relativePath)
        => HarnessDirectorySync.Withholds(relativePath) || Matches(_withheld, relativePath);

    /// <summary>
    /// The search that names every rooted <c>sync.neverTransfer</c> entry that matches nothing here while that name
    /// exists deeper in the tree: made as the tree's manifest is read (<see cref="IManifestLook"/>), so the tree is read
    /// once for both, and answered once that reading is done (<see cref="RootedEntrySearch.Report"/>).
    /// </summary>
    /// <param name="fileSystem">Looks at the tree's root for each entry's name.</param>
    /// <param name="root">The tree the entries are relative to.</param>
    /// <remarks>
    /// A protective rule that silently matches nothing is worse than no rule, because the name in
    /// the file reads as evidence the thing is protected and any reader stops there. Measured on a
    /// consumer's tree: of seventeen entries, three protected nothing at all, and one of them was
    /// <c>.secrets</c> — which protected the root instance while a second <c>.secrets</c> three
    /// levels down, the directory this tool's own design names as where a host credential goes, was
    /// covered by nothing.
    /// <para>
    /// Reported rather than refused, and rather than quietly widened. Widening a bare name to mean
    /// any depth would stop transferring nested directories that builds read; refusing would fail a
    /// tree whose author meant exactly what they wrote. Naming it lets them decide, and the fix is
    /// one they can see: write <c>**/name</c>.
    /// </para>
    /// <para>
    /// A name counts only where nothing the configuration writes covers it: not where a
    /// <c>sync.neverTransfer</c> entry covers the path - the <c>**/name</c> the warning asks for among
    /// them - and not in the worktrees root. There the configuration already keeps it from every sync
    /// by an entry its author wrote, and each worktree is a checkout of its own, whose names are not
    /// this tree's. Counted, a name its author had covered as the warning advised would be named again
    /// on every sync.
    /// </para>
    /// <para>
    /// The search goes where a sync goes, and into the harness's own directory besides. It goes into
    /// nothing a sync withholds - what an entry covers, the worktrees root, what git ignores, what
    /// <c>sync.exclude</c> names - though such a directory's own name is seen from the one holding it,
    /// so a <c>node_modules</c> or a <c>__pycache__</c> git ignores still counts. What lies inside one is
    /// generated, fetched or another checkout's, not something anybody wrote into this tree, and it is
    /// where a tree's size is: measured on a consumer's tree, 68,697 of its 69,890 directories lay under
    /// what its <c>sync.neverTransfer</c> names, 63,888 under <c>build</c>, <c>.worktrees</c> and
    /// <c>.temp</c> alone. The harness's own directory is searched all the same, but for the worktrees
    /// root and what an entry covers: git ignores most of it by design, it holds this tree's connection
    /// data, and a <c>.secrets</c> there is the one this was written to find.
    /// </para>
    /// <para>
    /// Save for what the harness writes there as it works - each run's records, the record of host copies, each
    /// action's own <c>build</c> and <c>artifacts</c> - where a name neither counts nor is looked for: the run state
    /// of whichever machine made it, which no sync carries and no one edits by hand, and where a busy tree's
    /// runs pile up. Counted, a fresh worktree's first run - its tree's own <c>build</c> not made yet, an action's
    /// working space already there - was told the <c>build</c> entry <c>init</c> writes protected nothing, and to
    /// write <c>**/build</c>, which would withhold every source directory of that name as well.
    /// </para>
    /// <para>
    /// The manifest's walk skips links and reads each directory once, on a stack of its own, however deep the tree and
    /// however many directories it holds: the search reads all of what it goes into, and a name found deep in a tree of a
    /// million files is named. A directory that cannot be read fails the reading of the tree itself, which no sync goes on
    /// without, so the search never answers for a tree it did not finish.
    /// </para>
    /// </remarks>
    public RootedEntrySearch SearchRootedEntries(IFileSystem fileSystem, string root)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var rooted = _neverTransfer
            .Where(path => !path.StartsWith(PathPatterns.AnyDepth, StringComparison.Ordinal))
            .Where(path => !path.Contains('/', StringComparison.Ordinal))
            .Where(path => !SyncConfig.NeverTransferFloor.Contains(path, StringComparer.Ordinal))
            .ToList();

        // Absent from the root is what makes an entry worth looking for deeper. A name that is there
        // is doing its job, whatever else in the tree shares the name.
        var absent = rooted.Count == 0 || !fileSystem.DirectoryExists(root)
            ? []
            : rooted
                .Where(name => !fileSystem.DirectoryExists(Path.Combine(root, name))
                    && !fileSystem.FileExists(Path.Combine(root, name)))
                .ToHashSet(StringComparer.Ordinal);

        return new RootedEntrySearch(this, absent);
    }

    /// <summary>The name of a directory this never reads, wherever it is, whatever the configuration says.</summary>
    /// <remarks>
    /// Git's own directory, at the root and wherever a submodule puts one: it is large, and a name
    /// inside it is git's copy of something rather than a file anybody wrote. What else the search
    /// leaves unread is decided by <see cref="Searches"/>.
    /// </remarks>
    internal const string NeverRead = ".git";

    /// <summary>
    /// Whether a name found at <paramref name="relativePath"/> counts: not where a <c>sync.neverTransfer</c>
    /// entry, or the worktrees root, covers the path, nor in what the harness writes as it works.
    /// </summary>
    internal bool Counts(string relativePath)
        => !Matches(_coveredByConfiguration, relativePath) && !HarnessDirectorySync.IsRunState(relativePath);

    /// <summary>
    /// Whether the search goes into the directory at <paramref name="relativePath"/>: where a sync goes,
    /// and anywhere in the harness's own directory a name counts.
    /// </summary>
    internal bool Searches(string relativePath)
        => Counts(relativePath)
            && (HarnessDirectorySync.IsAt(relativePath, HarnessLayout.DirectoryName) || !IsWithheldFromTransfer(relativePath));

    /// <summary>
    /// Refuses when git has stopped ignoring something the configuration withholds.
    /// </summary>
    /// <param name="gitClient">Used to ask git what it ignores.</param>
    /// <param name="root">The source tree.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <exception cref="HarnessException">
    /// A withheld path is no longer ignored, so the two statements of what is local to a machine
    /// disagree.
    /// </exception>
    /// <remarks>
    /// The question asked is narrow on purpose: does a withheld path hold files git neither ignores
    /// nor tracks? Those are the files the ignore rule used to cover. Sync goes on withholding them
    /// either way, so nothing leaks; what has happened is that a rule was removed or renamed, and
    /// the next person to read <c>git status</c> sees a build tree as work to commit while sync
    /// still treats it as machine-local. Reporting that is cheap; discovering it as a committed
    /// build directory is not.
    /// Deliberately not "is this path ignored": a rule such as <c>/dir/*</c> ignores a directory's
    /// contents without ignoring the directory, a path that does not exist is ignored by nothing,
    /// and a placeholder a repository tracks on purpose is not a disagreement. Each of those would
    /// make this refuse a tree that is exactly as its author meant it.
    /// <c>.git</c> alone is exempt: git never ignores it, and never could. The orchestrators' directory and the slots a
    /// tree keeps its copies in, on the floor too, are not: each is ignored by the rule init writes, and a branch whose
    /// rules predate that one would hand records, transcripts and copies to the next <c>git add</c>.
    /// </remarks>
    public async Task RefuseWhenNoLongerIgnoredAsync(
        IGitClient gitClient,
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);

        var uncovered = new List<string>();

        foreach (var path in _withheld.Where(path => !string.Equals(path, ".git", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await gitClient
                .RunAsync(
                    root,
                    ["status", "--porcelain=v1", "--untracked-files=all", "--", path],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!status.Succeeded)
            {
                // Unreadable is not clean. A status that failed says nothing about whether the rule
                // still covers the path, and reporting it as covered is the answer that hides a
                // build tree becoming committable.
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"Whether git still ignores '{path}' could not be established: {status.FailureMessage}");
            }

            // The placeholder init keeps in the worktrees root and in each slot is the harness's own, tracked on purpose,
            // and shows as untracked only until it is committed: it is no file an ignore rule stopped covering.
            var placeholder = $"?? {path}/{HarnessLayout.GitKeepFileName}";

            if (status.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.StartsWith("?? ", StringComparison.Ordinal) && !string.Equals(line.TrimEnd('\r'), placeholder, StringComparison.Ordinal)))
            {
                uncovered.Add(path);
            }
        }

        if (uncovered.Count == 0)
        {
            return;
        }

        // Each named by where it came from: a worktrees root, or a path on the floor every sync withholds, reported as a
        // sync.neverTransfer entry would send a reader to a line that is not in the file.
        var named = uncovered
            .Select(path => string.Equals(path, _worktreesRoot, StringComparison.Ordinal) ? $"the worktrees root '{path}'"
                : SyncConfig.NeverTransferFloor.Contains(path, StringComparer.Ordinal) ? $"init's '{path}'"
                : _neverTransfer.Contains(path, StringComparer.Ordinal) ? $"sync.neverTransfer's '{path}'"
                : $"'{path}', which git ignored")
            .ToList();

        throw new HarnessException(
            HarnessExit.Refused,
            $"{string.Join(", ", named)} now {(uncovered.Count == 1 ? "holds" : "hold")} files git neither ignores nor tracks. "
            + "Sync still withholds them, but the repository has two disagreeing statements of what is local to a machine, "
            + "and those files read as work to commit: restore the ignore rule, or, for a sync.neverTransfer entry, drop it.");
    }

    private static bool Matches(string[] paths, string relativePath)
        => PathPatterns.Matches(paths, relativePath);

    private static string Normalize(string path) => PathPatterns.Normalize(path);
}
