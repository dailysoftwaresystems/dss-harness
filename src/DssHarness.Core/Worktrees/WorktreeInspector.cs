using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Worktrees;

/// <summary>What git makes of a directory under the worktrees directory.</summary>
internal enum WorktreeMembership
{
    /// <summary>A worktree of this repository, rooted at the directory.</summary>
    OfThisRepository,

    /// <summary>Not the root of any work tree git can find, such as a worktree whose .git file is gone.</summary>
    NotAWorktree,

    /// <summary>The root of a work tree of another repository, such as a clone.</summary>
    OfAnotherRepository,

    /// <summary>
    /// An agent's repository of this repository's main checkout, at the address it sits at (<see cref="AgentRepositories.IsAtAsync"/>):
    /// a repository of its own, borrowing the main checkout's objects, that git records no worktree for.
    /// </summary>
    AgentRepository,
}

/// <summary>Which repository a directory belongs to, and where git keeps its record of it.</summary>
/// <param name="Membership">What git makes of the directory.</param>
/// <param name="AdministrativeDirectory">
/// The worktree's own git directory, when it is a worktree of this repository: git's record of the worktree lasts exactly as
/// long as this directory does. For an agent's repository, its own git directory, which goes with it.
/// </param>
internal sealed record WorktreeIdentity(WorktreeMembership Membership, string? AdministrativeDirectory);

/// <summary>A submodule repository, deleted with the worktree, that holds work found nowhere else.</summary>
/// <param name="Path">
/// Where the submodule is checked out below the worktree's root, or, for one no longer checked out,
/// its name among the worktree's submodule repositories.
/// </param>
/// <param name="Commits">Commits on its HEAD or branches that no remote-tracking ref or tag contains.</param>
/// <param name="HasStash">Whether its repository holds a stash.</param>
internal sealed record SubmoduleLoss(string Path, int Commits, bool HasStash);

/// <summary>What is below a directory under the worktrees root that is no worktree itself.</summary>
/// <param name="Worktrees">The address of each worktree git records below it.</param>
/// <param name="Unrecorded">
/// In an orchestrator's directory, each directory directly in it holding a .git entry of its own that git records no
/// worktree at, as this machine spells it: a person's to look in and delete.
/// </param>
/// <param name="Moved">
/// Each directory directly in it that is a worktree git records at another place holding none now, as after the checkout
/// was moved by hand, with that place (<see cref="WorktreeInspector.MovedFromAsync"/>): its work is still in it, and git's
/// repair points the record back at it.
/// </param>
internal sealed record WorktreesBelow(IReadOnlyList<string> Worktrees, IReadOnlyList<string> Unrecorded, IReadOnlyList<(string Path, string Recorded)> Moved)
{
    /// <summary>Nothing below: no worktree, nothing unrecorded, nothing moved.</summary>
    public static WorktreesBelow None { get; } = new([], [], []);

    /// <summary>What is said of the moved ones, each with the repair that points git's record back at it.</summary>
    /// <param name="mainCheckoutRoot">The main checkout, whose git the repair runs in.</param>
    public string SaidMoved(string mainCheckoutRoot)
        => string.Join("; ", Moved.Select(moved => WorktreeInspector.SaidMoved(mainCheckoutRoot, moved.Path, moved.Recorded)));
}

/// <summary>
/// One uncommitted change found in a worktree: the path it is at, as git spells it, and how a line names it. The two looks a
/// checked removal takes - the checks, and the one just before its files go - compare by the path alone, so a change named
/// with why it was never looked into is the same change the second look's status gives by its path.
/// </summary>
/// <param name="Path">
/// The path relative to the worktree, as git's quoting writes it (<see cref="Git.GitName.Quoted"/>): what the second look's
/// status gives for the same change.
/// </param>
/// <param name="Shown">How a refusal, or the count of the changes discarded, names it: the path, with why where there is one.</param>
internal sealed record WorktreeChange(string Path, string Shown)
{
    /// <summary>A change named by its path alone.</summary>
    /// <param name="path">The path, as git's quoting writes it.</param>
    public static WorktreeChange At(string path) => new(path, path);
}

/// <summary>Everything that stops a worktree being deleted without --force, found before anything is touched.</summary>
/// <param name="Changes">The uncommitted changes, including edits status cannot see.</param>
/// <param name="Commits">
/// Commits that no branch, tag, remote-tracking ref, the newest stash, or other worktree's HEAD contains.
/// </param>
/// <param name="Head">The worktree's HEAD commit, or <see langword="null"/> when it has none.</param>
/// <param name="Submodules">Submodule repositories that hold work found nowhere else.</param>
/// <param name="LockReason">Why the worktree is locked, or <see langword="null"/> when it is not.</param>
/// <param name="HoldsSubmodules">Whether git counts the worktree as holding submodules, which plain removal refuses.</param>
/// <param name="MovedFrom">
/// The directory git's record of the worktree names when it is not this one, as after moving the
/// worktree by hand; empty when the record names none; <see langword="null"/> when it names this one.
/// </param>
internal sealed record WorktreeFindings(
    IReadOnlyList<WorktreeChange> Changes,
    int Commits,
    string? Head,
    IReadOnlyList<SubmoduleLoss> Submodules,
    string? LockReason,
    bool HoldsSubmodules,
    string? MovedFrom = null)
{
    /// <summary>
    /// Whether the worktree is an agent's repository of its own, whose <see cref="Commits"/> are those made in it that no
    /// ref of the main checkout's reaches: deleting it deletes its store, where they alone are.
    /// </summary>
    public bool OwnRepository { get; init; }

    /// <summary>Whether anything was found that deleting without --force must not override.</summary>
    public bool StopsDeletion
        => Changes.Count > 0 || Commits > 0 || Submodules.Count > 0 || LockReason is not null || MovedFrom is not null;
}

/// <summary>
/// Finds what deleting a worktree would lose, by asking git. Every question that fails throws
/// <see cref="HarnessException"/> with <see cref="HarnessExit.CommandFailed"/>, because a question
/// git could not answer is not a worktree with nothing to lose.
/// </summary>
internal sealed class WorktreeInspector(IGitClient gitClient, IFileSystem fileSystem, IHostPlatform platform, IHarnessOutput output)
{
    /// <summary>
    /// The first wait before a deletion of a temporary file is tried again: what holds a file it has just seen appear - a
    /// scanner - lets go within moments, and the waits double from here while it holds.
    /// </summary>
    private static readonly TimeSpan CleanupFirstWait = TimeSpan.FromMilliseconds(20);

    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <summary>The clock a retry of a temporary file's deletion waits on.</summary>
    private readonly TimeProvider _clock = TimeProvider.System;

    /// <summary>
    /// Whether <paramref name="directory"/> holds a <c>.git</c> of its own - a worktree's file or a repository's directory -
    /// rather than being answered for by whatever repository encloses it.
    /// </summary>
    /// <param name="fileSystem">Looks.</param>
    /// <param name="directory">The directory.</param>
    public static bool HoldsOwnGit(IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var git = Path.Combine(directory, ".git");
        return fileSystem.FileExists(git) || fileSystem.DirectoryExists(git);
    }

    /// <summary>What git makes of <paramref name="path"/>, which sits at <paramref name="address"/>, compared with the main checkout's repository.</summary>
    public async Task<WorktreeIdentity> IdentifyAsync(
        string mainCheckoutRoot,
        WorktreeAddress address,
        string path,
        CancellationToken cancellationToken)
    {
        var main = await MainGitDirectoryAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false);
        var here = await SessionTrees.LocationAsync(path, _platform.PathComparison, _fileSystem, () => _gitClient.GetLocationAsync(path, cancellationToken)).ConfigureAwait(false);

        // A directory that is not the root of a work tree, such as a worktree whose .git file is
        // gone, is answered for by whatever repository encloses it, and that answer says nothing
        // about the files in the directory.
        if (here is null || here.Prefix.Length != 0)
        {
            return new WorktreeIdentity(WorktreeMembership.NotAWorktree, null);
        }

        // A linked worktree's git directory sits in the worktrees directory of its repository's
        // git directory. Both paths come from git, resolved the same way, so no link in the path
        // the harness spelled can make them differ.
        var parent = Path.GetDirectoryName(here.GitDirectory);
        var grandparent = parent is null ? null : Path.GetDirectoryName(parent);

        var ofThisRepository = grandparent is not null
            && string.Equals(Path.GetFileName(parent), "worktrees", StringComparison.Ordinal)
            && string.Equals(grandparent, main, _platform.PathComparison);

        if (ofThisRepository)
        {
            return new WorktreeIdentity(WorktreeMembership.OfThisRepository, here.GitDirectory);
        }

        // A repository of its own is told apart before it is taken for another repository's: an agent's borrows the main
        // checkout's objects and carries its mark, and its git directory is the .git in it.
        return await new AgentRepositories(_gitClient, _fileSystem, _platform).IsAtAsync(mainCheckoutRoot, address, path, cancellationToken).ConfigureAwait(false)
            ? new WorktreeIdentity(WorktreeMembership.AgentRepository, here.GitDirectory)
            : new WorktreeIdentity(WorktreeMembership.OfAnotherRepository, null);
    }

    /// <summary>
    /// Everything that stops deleting the worktree at <paramref name="path"/> without --force: one of git's linked worktrees,
    /// or an agent's repository of its own, as <paramref name="identity"/> says.
    /// </summary>
    /// <remarks>
    /// An agent's repository is weighed as a linked worktree is - its changes, the edits status cannot see, the work in its
    /// submodules' repositories - but for its commits, which are those no ref of the main checkout's reaches: deleting it
    /// deletes its own store, where they alone are. It has no lock and no record that could name another directory.
    /// </remarks>
    public async Task<WorktreeFindings> FindAsync(
        string mainCheckoutRoot,
        string path,
        WorktreeIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var administrativeDirectory = identity.AdministrativeDirectory!;
        var ownRepository = identity.Membership == WorktreeMembership.AgentRepository;
        var status = await _gitClient.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
        var index = await _gitClient.ListIndexAsync(path, cancellationToken).ConfigureAwait(false);

        List<WorktreeChange> changes = [.. status.Select(entry => WorktreeChange.At(entry.Path.Quoted))];
        changes.AddRange((await FindHiddenEditsAsync(path, index, cancellationToken).ConfigureAwait(false)).Select(WorktreeChange.At));

        var head = await _gitClient.ResolveCommitAsync(path, "HEAD", cancellationToken).ConfigureAwait(false);
        var commits = head is null ? 0
            : ownRepository ? await CountOwnCommitsAsync(path, cancellationToken).ConfigureAwait(false)
            : await CountUnreferencedCommitsAsync(path, mainCheckoutRoot, head, administrativeDirectory, cancellationToken).ConfigureAwait(false);

        var found = new SubmoduleFind(_platform.PathComparison);
        var populated = await CollectCheckedOutSubmodulesAsync(mainCheckoutRoot, path, administrativeDirectory, index, found, changes, cancellationToken)
            .ConfigureAwait(false);
        await CollectModuleRepositoriesAsync(mainCheckoutRoot, Path.Combine(administrativeDirectory, "modules"), string.Empty, found, cancellationToken)
            .ConfigureAwait(false);

        // git's own rule for a worktree holding submodules: a modules directory in its git
        // directory, or a populated submodule in its index.
        var holdsSubmodules = populated
            || _fileSystem.DirectoryExists(Path.Combine(administrativeDirectory, "modules"));

        // git removes, and lists, the directory its record names. When that is not this one, as
        // after moving the worktree by hand, git's removal would fail once every check had passed.
        var recorded = ownRepository ? null : ReadRecordedWorktree(administrativeDirectory);
        var movedFrom = ownRepository || (recorded is not null && PathsEqual(recorded, ResolveLinks(path))) ? null : recorded ?? string.Empty;

        return new WorktreeFindings(
            changes,
            commits,
            head,
            await FindSubmoduleLossesAsync(found.Repositories, cancellationToken).ConfigureAwait(false),
            ownRepository ? null : ReadLockReason(administrativeDirectory),
            holdsSubmodules,
            movedFrom)
        {
            OwnRepository = ownRepository,
        };
    }

    /// <summary>
    /// What has come since its checks to stop deleting the worktree at <paramref name="path"/>, asked once more just before
    /// its files are deleted: each path git's status lists, and, for one of git's linked worktrees, its lock - the look
    /// git's own removal took before it deleted anything, which a removal that deletes the files itself, before git clears
    /// its record, takes here instead - or, for an agent's repository of its own, which has no lock, the commits made in it
    /// that no ref of the main checkout's reaches, which its own store takes with it.
    /// </summary>
    /// <param name="path">The worktree: one of git's linked worktrees of this repository, or an agent's repository.</param>
    /// <param name="identity">What git made of it: a linked worktree's git directory holds its lock.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <returns>Its changes, and its lock or its commits, spelt as <see cref="FindAsync"/> spells them; nothing else is asked again.</returns>
    /// <exception cref="HarnessException">git could not answer (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public async Task<WorktreeFindings> LookAgainAsync(string path, WorktreeIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var status = await _gitClient.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
        List<WorktreeChange> changes = [.. status.Select(entry => WorktreeChange.At(entry.Path.Quoted))];

        if (identity.Membership != WorktreeMembership.AgentRepository)
        {
            return new WorktreeFindings(changes, 0, null, [], ReadLockReason(identity.AdministrativeDirectory!), HoldsSubmodules: false);
        }

        var head = await _gitClient.ResolveCommitAsync(path, "HEAD", cancellationToken).ConfigureAwait(false);
        var commits = head is null ? 0 : await CountOwnCommitsAsync(path, cancellationToken).ConfigureAwait(false);

        return new WorktreeFindings(changes, commits, head, [], LockReason: null, HoldsSubmodules: false) { OwnRepository = true };
    }

    /// <summary>
    /// The commits made in an agent's repository of its own that no ref of the main checkout's reaches, which deleting it
    /// deletes with its own store: those its HEAD and its refs reach and none of its alternates' refs does.
    /// </summary>
    private Task<int> CountOwnCommitsAsync(string path, CancellationToken cancellationToken)
        => _gitClient.CountCommitsAsync(path, ["HEAD", "--all", "--not", "--alternate-refs"], cancellationToken);

    /// <summary>
    /// git's record of a worktree at <paramref name="path"/>, or <see langword="null"/> when git records none there:
    /// found in git's own list, which names each worktree by the path it resolved, so the path is resolved the same
    /// way first.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, whose list is read.</param>
    /// <param name="path">Where the worktree is, or was.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">git's list could not be read (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public async Task<GitWorktree?> FindRecordAsync(string mainCheckoutRoot, string path, CancellationToken cancellationToken)
    {
        var resolved = ResolveLinks(path);
        var worktrees = await _gitClient.ListWorktreesAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false);

        return RecordAt(worktrees, resolved);
    }

    /// <summary>
    /// Where git's record of the worktree in <paramref name="directory"/> says it is, where that is another place holding no
    /// worktree now - as after the checkout, or the worktrees in it, were moved by hand - or <see langword="null"/> where the
    /// directory is no such worktree. The empty text is a record that names no place. <c>git worktree repair</c> run on the
    /// directory points that record, and the directory's .git file, back at each other; until then git may not open the
    /// worktree at all, and its work is all still in it.
    /// </summary>
    /// <remarks>
    /// Read as git's own repair reads it: the directory's .git is a file naming a record in a worktrees directory; where
    /// that record is gone, as it is once the checkout itself moved, the record of the same name in this checkout's git
    /// directory is the one meant, and where it is there it must be that one. A record whose worktree is still where the
    /// record says is another worktree's, and is never taken for this one.
    /// </remarks>
    /// <param name="mainCheckoutRoot">The main checkout, whose git directory holds the records.</param>
    /// <param name="directory">A directory holding a .git entry of its own that git lists no worktree at.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">git's records could not be read (<see cref="HarnessExit.CommandFailed"/>).</exception>
    public async Task<string?> MovedFromAsync(string mainCheckoutRoot, string directory, CancellationToken cancellationToken)
    {
        if (await ReadGitFileAsync(directory, cancellationToken).ConfigureAwait(false) is not { } named)
        {
            return null;
        }

        var parent = Path.GetDirectoryName(named);
        var id = Path.GetFileName(named);

        if (parent is null || id.Length == 0 || !string.Equals(Path.GetFileName(parent), "worktrees", _platform.PathComparison))
        {
            return null;
        }

        var record = Path.Combine(await MainGitDirectoryAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false), "worktrees", id);

        try
        {
            if (!_fileSystem.DirectoryExists(record) || (_fileSystem.DirectoryExists(named) && !PathsEqual(ResolveLinks(named), ResolveLinks(record))))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Could not read git's worktree record '{record}': {ex.Message}");
        }

        var recorded = ReadRecordedWorktree(record);

        if (recorded is not null && (PathsEqual(recorded, ResolveLinks(directory)) || _fileSystem.FileExists(Path.Combine(recorded, ".git"))))
        {
            return null;
        }

        return recorded ?? string.Empty;
    }

    /// <summary>
    /// The full path of the record the .git file in <paramref name="directory"/> names, or null where its .git is no such
    /// file: read without following a link - a worktree's own files are a place its agent's commands write - and its first
    /// line read whole, as git reads it, however long the path it names.
    /// </summary>
    private async Task<string?> ReadGitFileAsync(string directory, CancellationToken cancellationToken)
    {
        var dotGit = Path.Combine(directory, ".git");

        try
        {
            if (!_fileSystem.FileExists(dotGit))
            {
                return null;
            }

            string? line;
            var opened = UnfollowedFiles.OpenRead(_fileSystem, directory, dotGit);

            await using (opened.ConfigureAwait(false))
            {
                line = await FileLines.FirstLineWholeAsync(opened, cancellationToken).ConfigureAwait(false);
            }

            const string Prefix = "gitdir:";

            return line is not null && line.StartsWith(Prefix, StringComparison.Ordinal) && line[Prefix.Length..].Trim() is { Length: > 0 } named
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(directory, named)))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new HarnessException(HarnessExit.CommandFailed, $"Could not read '{dotGit}': {ex.Message}");
        }
    }

    /// <summary>
    /// What is said of the worktree at <paramref name="directory"/> that git records at <paramref name="recorded"/>, elsewhere:
    /// that it is one, with its work in it, and the one command that points git's record back at it - never that it is a
    /// person's to delete.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, whose git the repair runs in.</param>
    /// <param name="directory">The worktree, where it is now.</param>
    /// <param name="recorded">Where git's record says it is; empty where the record names no place.</param>
    public static string SaidMoved(string mainCheckoutRoot, string directory, string recorded)
        => $"'{ReportText.Printable(directory)}' is a worktree git records "
            + (recorded.Length == 0 ? "with no place named" : $"at '{ReportText.Printable(recorded)}', as after the checkout or its worktrees were moved by hand")
            + $", and git cannot be relied on to open it until that record points here; everything in it is still there. Run 'git -C {ReportText.Printable(mainCheckoutRoot)} worktree repair {ReportText.Printable(directory)}', "
            + "which points git's record and its .git file back at each other";

    /// <summary>
    /// What is below <paramref name="path"/>, which is not a worktree itself: the address of each worktree git records
    /// strictly inside it and of each agent's repository directly in it, and - in the directory named for an orchestrator -
    /// each other directory directly in it holding a .git entry of its own that git records no worktree at, since an
    /// orchestrator's directory holds its agents' worktrees and nothing else.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, whose list is read.</param>
    /// <param name="worktreesDirectory">The worktrees root, which the addresses are relative to.</param>
    /// <param name="path">A directory directly under the root, named as a plain worktree is.</param>
    /// <param name="orchestrators">Whether <paramref name="path"/> is the directory named for an orchestrator.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <exception cref="HarnessException">
    /// git could not list its worktrees, or the directory could not be looked in (<see cref="HarnessExit.CommandFailed"/>):
    /// never read as no worktree below it.
    /// </exception>
    public async Task<WorktreesBelow> FindWorktreesBelowAsync(
        string mainCheckoutRoot,
        string worktreesDirectory,
        string path,
        bool orchestrators,
        CancellationToken cancellationToken)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var resolved = ResolveLinks(path);
        IReadOnlyList<string> holdingGit;

        // Every directory directly in it holding a .git of its own is looked at, whatever the directory is named: an agent's
        // repository is a worktree below it, which git records nowhere.
        try
        {
            holdingGit = [.. _fileSystem.EnumerateDirectories(path).Select(Path.TrimEndingDirectorySeparator).Where(child => HoldsOwnGit(_fileSystem, child)).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw CouldNotBeLookedIn(path, ex);
        }

        // A list git could not give is not an empty one: raised, never read as no worktree below.
        var listed = await _gitClient.ListWorktreesAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false);

        foreach (var worktree in listed)
        {
            if (!worktree.IsMain && PathContainment.IsStrictlyInside(resolved, worktree.Path, _platform.PathComparison))
            {
                found.Add(AddressOf(ResolveLinks(worktreesDirectory), worktree.Path));
            }
        }

        var unrecorded = new List<string>();
        var moved = new List<(string Path, string Recorded)>();
        var repositories = new AgentRepositories(_gitClient, _fileSystem, _platform);

        foreach (var child in holdingGit.Where(child => RecordAt(listed, ResolveLinks(child)) is null))
        {
            var address = WorktreeAddress.Nested(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), Path.GetFileName(child));

            if (await repositories.IsAtAsync(mainCheckoutRoot, address, child, cancellationToken).ConfigureAwait(false))
            {
                found.Add(address.Name);
            }
            else if (await MovedFromAsync(mainCheckoutRoot, child, cancellationToken).ConfigureAwait(false) is { } recorded)
            {
                // A worktree git records elsewhere is one, whatever directory it is in: never a person's to delete.
                moved.Add((child, recorded));
            }
            else if (orchestrators && HoldsOwnGit(_fileSystem, child))
            {
                // Each git does not record is told apart, whatever its name: nothing here can say what it holds, so it is a
                // person's to look in and delete - what a removal left, or a copy an earlier version of this tool kept there.
                // One a deletion took away while it was asked about holds no .git of its own any more, and is none.
                unrecorded.Add(child);
            }
        }

        return new WorktreesBelow([.. found], unrecorded, moved);

        string AddressOf(string root, string tree)
            => WorktreeAddress.OfTree(root, tree, _platform.PathComparison)?.Name
                ?? Path.GetRelativePath(root, tree).Replace(Path.DirectorySeparatorChar, WorktreeAddress.Separator);
    }

    /// <summary>
    /// What is raised where <paramref name="directory"/>, which worktrees and what was left beside them are kept in,
    /// cannot be looked in: a failure (<see cref="HarnessExit.CommandFailed"/>), so that whoever asked never reads it as
    /// a directory holding nothing.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="why">What the disk raised.</param>
    public static HarnessException CouldNotBeLookedIn(string directory, Exception why)
    {
        ArgumentNullException.ThrowIfNull(why);
        return new HarnessException(HarnessExit.CommandFailed, $"'{directory}' could not be looked in: {why.Message.TrimEnd('.')}");
    }

    /// <summary>
    /// Why a directory under the worktrees root holding a .git entry of its own that git records no worktree at is there,
    /// as every line that meets one says it: told by what it holds, never by its name.
    /// </summary>
    public const string UnrecordedWhy =
        "what a removal leaves when git's record goes and something in the directory could not, or a copy an earlier version of this tool kept beside a worktree, which 'clean --former-workers' removes";

    /// <summary>
    /// What is said of <paramref name="directories"/>, each holding a .git entry of its own that git records no worktree
    /// at, wherever a command that would not go on past them meets them: what they are, and that they are a person's to
    /// look in and delete, since nothing here can tell what they hold.
    /// </summary>
    /// <param name="directories">The directories, as this machine spells them.</param>
    public static string SaidUnrecorded(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);

        return $"git records no worktree at {Names.All([.. directories.Select(directory => $"'{directory}'")])}, though "
            + (directories.Count == 1 ? "it holds" : "each holds")
            + $" a .git entry of its own - {UnrecordedWhy}. Look inside {(directories.Count == 1 ? "it" : "each")} for anything wanted, then delete it";
    }

    /// <summary>The worktree among <paramref name="worktrees"/> recorded at <paramref name="resolvedPath"/>, never the main one.</summary>
    /// <param name="worktrees">git's list.</param>
    /// <param name="resolvedPath">A path with its links resolved, as git lists one.</param>
    public GitWorktree? RecordAt(IEnumerable<GitWorktree> worktrees, string resolvedPath)
        => worktrees.FirstOrDefault(worktree => !worktree.IsMain && PathsEqual(worktree.Path, resolvedPath));

    /// <summary>
    /// Everything that stops clearing git's record of a worktree whose directory is already gone:
    /// commits only its HEAD names, work in the submodule repositories its git directory still holds,
    /// and a lock. Removing the record deletes those repositories, and git checks none of them.
    /// </summary>
    public async Task<WorktreeFindings> FindForRecordAsync(
        string mainCheckoutRoot,
        GitWorktree record,
        CancellationToken cancellationToken)
    {
        // Without the record's own directory, neither which HEAD is this worktree's nor which
        // submodule repositories clearing it deletes can be known, and passing over that would
        // lose them.
        var administrativeDirectory = await FindAdministrativeDirectoryAsync(mainCheckoutRoot, record.Path, cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git lists a worktree at '{record.Path}', but the directory holding its record could not be found, so what clearing it would delete cannot be checked.");

        var commits = record.Commit is null
            ? 0
            : await CountUnreferencedCommitsAsync(mainCheckoutRoot, mainCheckoutRoot, record.Commit, administrativeDirectory, cancellationToken).ConfigureAwait(false);

        var found = new SubmoduleFind(_platform.PathComparison);
        await CollectModuleRepositoriesAsync(mainCheckoutRoot, Path.Combine(administrativeDirectory, "modules"), string.Empty, found, cancellationToken)
            .ConfigureAwait(false);

        return new WorktreeFindings(
            [],
            commits,
            record.Commit,
            await FindSubmoduleLossesAsync(found.Repositories, cancellationToken).ConfigureAwait(false),

            // git's list says it is locked; why is read here, as every record of the worktree's is, never through a link.
            record.LockReason is null ? null : ReadLockReason(administrativeDirectory) ?? string.Empty,
            HoldsSubmodules: false);
    }

    /// <summary>
    /// <paramref name="path"/> with every link along it followed, the form git reports worktree
    /// paths in, so the two compare equal even when the worktrees directory is reached through a link.
    /// </summary>
    public string ResolveLinks(string path)
    {
        try
        {
            return _fileSystem.ResolveLinks(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not follow the links in the worktree's path: {ex.Message}");
        }
    }

    /// <summary>The main checkout's git directory, as git names it: asked once in an engine's session, which keeps it (<see cref="SessionTrees.LocationAsync"/>).</summary>
    private async Task<string> MainGitDirectoryAsync(string mainCheckoutRoot, CancellationToken cancellationToken)
    {
        var main = await SessionTrees.LocationAsync(mainCheckoutRoot, _platform.PathComparison, _fileSystem, () => _gitClient.GetLocationAsync(mainCheckoutRoot, cancellationToken)).ConfigureAwait(false);

        return main?.GitDirectory ?? throw new HarnessException(
            HarnessExit.CommandFailed,
            $"git no longer finds a repository at '{mainCheckoutRoot}'.");
    }

    /// <summary>
    /// How many commits reachable from <paramref name="head"/> nothing else names: no branch, tag,
    /// remote-tracking ref, the newest stash, or the HEAD of another worktree, the main checkout's
    /// included. Deleting a worktree deletes its HEAD, which may be all that names them.
    /// </summary>
    private async Task<int> CountUnreferencedCommitsAsync(
        string directory,
        string mainCheckoutRoot,
        string head,
        string administrativeDirectory,
        CancellationToken cancellationToken)
    {
        var stash = await _gitClient.ResolveCommitAsync(directory, "refs/stash", cancellationToken).ConfigureAwait(false);

        // Not --all: it includes the HEAD of this worktree too.
        List<string> revisions = [head, "--not", "--branches", "--tags", "--remotes"];
        revisions.AddRange(await OtherWorktreeHeadsAsync(directory, mainCheckoutRoot, administrativeDirectory, cancellationToken).ConfigureAwait(false));

        if (stash is not null)
        {
            revisions.Add(stash);
        }

        return await _gitClient.CountCommitsAsync(directory, revisions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The HEAD commit of every worktree but this one, the main checkout's included, each named by its
    /// record rather than its path. A worktree moved by hand is still listed at its old path, where
    /// matching by path would take this worktree's own HEAD for another's. An unborn HEAD names no commit.
    /// </summary>
    private async Task<IReadOnlyList<string>> OtherWorktreeHeadsAsync(
        string directory,
        string mainCheckoutRoot,
        string administrativeDirectory,
        CancellationToken cancellationToken)
    {
        var heads = new List<string>();

        if (await _gitClient.ResolveCommitAsync(directory, "main-worktree/HEAD", cancellationToken).ConfigureAwait(false) is { } mainHead)
        {
            heads.Add(mainHead);
        }

        foreach (var record in EnumerateRecords(await MainGitDirectoryAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false)))
        {
            if (PathsEqual(record, administrativeDirectory))
            {
                continue;
            }

            if (await _gitClient.ResolveCommitAsync(directory, $"worktrees/{Path.GetFileName(record)}/HEAD", cancellationToken).ConfigureAwait(false)
                is { } head)
            {
                heads.Add(head);
            }
        }

        return [.. heads.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Files marked assume-unchanged or skip-worktree whose content on disk differs from the index.
    /// Status never compares a marked file, so an edit to one is invisible to it; a copy of the index
    /// with the marks cleared lets status compare them, and the real index is never touched.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindHiddenEditsAsync(
        string root,
        IReadOnlyList<GitIndexEntry> index,
        CancellationToken cancellationToken)
    {
        // A marked file absent from disk, as a sparse checkout leaves a skip-worktree file, holds
        // nothing to lose, and unmarked in the copy it would read as deleted.
        var present = index
            .Where(entry => entry.Stage == 0 && !entry.IsSubmodule && (entry.IsAssumedUnchanged || entry.IsSkipWorktree))
            .Where(entry => _fileSystem.FileExists(Path.Combine(root, entry.Path)))
            .ToList();

        if (present.Count == 0)
        {
            return [];
        }

        var indexFile = await _gitClient.GetIndexFileAsync(root, cancellationToken).ConfigureAwait(false);
        string copy;

        try
        {
            copy = _fileSystem.CopyToTemporaryFile(indexFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not copy the index '{indexFile}' to compare the files it marks: {ex.Message}");
        }

        try
        {
            return await _gitClient.FindEditedFilesAsync(
                root,
                copy,
                [.. present.Where(entry => entry.IsAssumedUnchanged).Select(entry => entry.Path)],
                [.. present.Where(entry => entry.IsSkipWorktree).Select(entry => entry.Path)],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DeleteTemporaryCopyAsync(copy).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds the repository of every submodule checked out under <paramref name="root"/>, nested ones
    /// included, and reports whether any is checked out there. A submodule whose .git is a directory in the
    /// work tree keeps its repository there rather than among the worktree's modules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The status of the tree a submodule is checked out in tells it by its commit alone
    /// (<see cref="GitOwnSettings.SubmoduleCommits"/>), so each one's own changes are asked of it here, by its own
    /// git - which runs its own configuration, a clean filter among it - only where its repository is one git keeps
    /// among the modules of <paramref name="gitDirectory"/>, which git's own submodule commands write and no isolated
    /// agent does. One whose repository is anywhere else - a directory of its work tree, or where a .git file there
    /// points, either written by whatever writes the tree - is named a change never looked into: deleting it is never
    /// taken for losing nothing.
    /// </para>
    /// <para>
    /// Walked on a stack of its own, never a recursion, so submodules nested however deep are each reached, and each work
    /// tree once (<see cref="SubmoduleFind.FirstTree"/>): one a link leads back into - a submodule's directory that is a
    /// link to one above it - is reached again as the directory it already walked, and the walk goes on past it.
    /// </para>
    /// </remarks>
    private async Task<bool> CollectCheckedOutSubmodulesAsync(
        string mainCheckoutRoot,
        string root,
        string gitDirectory,
        IReadOnlyList<GitIndexEntry> index,
        SubmoduleFind found,
        List<WorktreeChange> changes,
        CancellationToken cancellationToken)
    {
        var populated = false;
        var frames = new Stack<CheckedOutFrame>();

        if (found.FirstTree( ResolveLinks(root)))
        {
            frames.Push(new CheckedOutFrame(Submodules(index), root, gitDirectory, string.Empty));
        }

        try
        {
            while (frames.TryPeek(out var frame))
            {
                if (!frame.Entries.MoveNext())
                {
                    frames.Pop().Entries.Dispose();
                    continue;
                }

                var entry = frame.Entries.Current;
                var directory = Path.Combine(frame.Root, entry.Path);

                if (!HoldsOwnGit(_fileSystem, directory))
                {
                    continue;
                }

                // Only the tree asked about says whether it holds a checked-out submodule: a nested one's is its parent's.
                populated |= frames.Count == 1;
                var shown = frame.Shown + entry.Path;

                // Spelt as status spells the change, so the change it named is this one, and the second look finds it so.
                var at = frame.Shown + entry.Name.Quoted;

                var location = await _gitClient.GetLocationAsync(directory, cancellationToken).ConfigureAwait(false)
                    ?? throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"git does not see the submodule checked out at '{directory}' as a repository.");

                found.Add(shown, location.GitDirectory);
                await CollectModuleRepositoriesAsync(mainCheckoutRoot, Path.Combine(location.GitDirectory, "modules"), shown + "/", found, cancellationToken)
                    .ConfigureAwait(false);

                if (!PathContainment.IsStrictlyInside(Path.Combine(frame.GitDirectory, "modules"), location.GitDirectory, _platform.PathComparison))
                {
                    changes.RemoveAll(change => string.Equals(change.Path, at, StringComparison.Ordinal));
                    changes.Add(new WorktreeChange(at, $"{shown} (a submodule whose repository is not among its superproject's modules, so its work tree is never looked into)"));
                }
                else if (!changes.Exists(change => string.Equals(change.Path, at, StringComparison.Ordinal))
                    && (await _gitClient.GetStatusAsync(directory, cancellationToken).ConfigureAwait(false)).Count > 0)
                {
                    changes.Add(new WorktreeChange(at, shown));
                }

                // A work tree reached again - a link leading back into one already walked - is passed over: its
                // submodules were reached the first time.
                if (found.FirstTree( ResolveLinks(directory)))
                {
                    var nested = await _gitClient.ListIndexAsync(directory, cancellationToken).ConfigureAwait(false);
                    frames.Push(new CheckedOutFrame(Submodules(nested), directory, location.GitDirectory, shown + "/"));
                }
            }
        }
        finally
        {
            while (frames.TryPop(out var unfinished))
            {
                unfinished.Entries.Dispose();
            }
        }

        return populated;

        static IEnumerator<GitIndexEntry> Submodules(IReadOnlyList<GitIndexEntry> entries)
            => entries.Where(entry => entry.IsSubmodule && entry.Stage == 0).GetEnumerator();
    }

    /// <summary>
    /// Adds every repository under a modules directory, nested submodules' included, checked out or
    /// not: a submodule deinitialised in the worktree keeps its repository there, with its work. A
    /// submodule's name can hold slashes, so a directory that is not a repository is part of a name.
    /// </summary>
    /// <remarks>
    /// Walked on a stack of its own, never a recursion, each directory once (<see cref="SubmoduleFind.FirstModules"/>), by
    /// where its links lead: a directory that is a link to one above it - a modules directory leading back into itself -
    /// is reached again as the directory it already walked, and the walk ends there rather than going round it until a
    /// path is too long to name.
    /// </remarks>
    private async Task CollectModuleRepositoriesAsync(
        string mainCheckoutRoot,
        string modules,
        string shownPrefix,
        SubmoduleFind found,
        CancellationToken cancellationToken)
    {
        var frames = new Stack<ModulesFrame>();

        Enter(modules, shownPrefix);

        try
        {
            while (frames.TryPeek(out var frame))
            {
                if (!frame.Directories.MoveNext())
                {
                    frames.Pop().Directories.Dispose();
                    continue;
                }

                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(frame.Directories.Current));
                var shown = frame.Shown + Path.GetFileName(full);

                if (await _gitClient.ResolveGitDirectoryAsync(mainCheckoutRoot, full, cancellationToken).ConfigureAwait(false)
                    is { } gitDirectory)
                {
                    found.Add(shown, gitDirectory);
                    Enter(Path.Combine(gitDirectory, "modules"), shown + "/");
                }
                else if (_fileSystem.FileExists(Path.Combine(full, "HEAD"))
                    || _fileSystem.DirectoryExists(Path.Combine(full, "objects"))
                    || _fileSystem.DirectoryExists(Path.Combine(full, "refs")))
                {
                    // A repository git cannot read, such as one whose HEAD a crash emptied, still holds
                    // its branches on disk. Taken for part of a submodule's name, it would be walked
                    // through, and deleted without a word.
                    throw new HarnessException(
                        HarnessExit.CommandFailed,
                        $"git does not read '{full}' as a repository, though it looks like a submodule's, so the work it may hold cannot be checked.");
                }
                else
                {
                    Enter(full, shown + "/");
                }
            }
        }
        finally
        {
            while (frames.TryPop(out var unfinished))
            {
                unfinished.Directories.Dispose();
            }
        }

        // Pushes the directory's frame where it is there and not walked already, its subdirectories listed now.
        void Enter(string directory, string shown)
        {
            List<string> directories;

            try
            {
                if (!_fileSystem.DirectoryExists(directory) || !found.FirstModules(ResolveLinks(directory)))
                {
                    return;
                }

                directories = [.. _fileSystem.EnumerateDirectories(directory)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"Could not look for submodule repositories in '{directory}': {ex.Message}");
            }

            frames.Push(new ModulesFrame(directories.GetEnumerator(), shown));
        }
    }

    /// <summary>A work tree being walked for its checked-out submodules: what is left of its index, and where it is.</summary>
    /// <param name="Entries">Its submodules' index entries, read so far.</param>
    /// <param name="Root">The work tree.</param>
    /// <param name="GitDirectory">Its git directory, among whose modules a submodule's repository is looked into.</param>
    /// <param name="Shown">What a submodule's path in it is named after.</param>
    private sealed record CheckedOutFrame(IEnumerator<GitIndexEntry> Entries, string Root, string GitDirectory, string Shown);

    /// <summary>A modules directory being walked: what is left of its subdirectories, and what each is named after.</summary>
    /// <param name="Directories">Its subdirectories, read so far.</param>
    /// <param name="Shown">What a subdirectory's name is put after.</param>
    private sealed record ModulesFrame(IEnumerator<string> Directories, string Shown);

    /// <summary>
    /// What a look for the submodule repositories a deletion would delete has found, and where it has been: each
    /// repository once, by its git directory, and each directory walked once, by where its links lead - so a walk never
    /// goes round a link that leads back into what it walked.
    /// </summary>
    /// <param name="comparison">How this machine's file system compares paths.</param>
    private sealed class SubmoduleFind(StringComparison comparison)
    {
        private readonly HashSet<string> _gitDirectories = new(StringComparer.FromComparison(comparison));

        /// <summary>Every repository found, in the order it was found.</summary>
        public List<SubmoduleRepository> Repositories { get; } = [];

        /// <summary>The work trees walked for checked-out submodules, by where their links lead.</summary>
        private readonly HashSet<string> _trees = new(StringComparer.FromComparison(comparison));

        /// <summary>The modules directories walked, by where their links lead.</summary>
        private readonly HashSet<string> _modules = new(StringComparer.FromComparison(comparison));

        /// <summary>Whether the work tree <paramref name="directory"/> is walked for the first time, which it is now marked as.</summary>
        /// <param name="directory">The work tree, its links followed.</param>
        public bool FirstTree(string directory) => _trees.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));

        /// <summary>Whether the modules directory <paramref name="directory"/> is walked for the first time, which it is now marked as.</summary>
        /// <param name="directory">The directory, its links followed.</param>
        public bool FirstModules(string directory) => _modules.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));

        /// <summary>Adds a repository unless it was already found another way.</summary>
        public void Add(string shown, string gitDirectory)
        {
            if (_gitDirectories.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitDirectory))))
            {
                Repositories.Add(new SubmoduleRepository(shown, gitDirectory));
            }
        }
    }

    /// <summary>
    /// The repositories holding commits on their HEAD or branches that no remote-tracking ref or tag
    /// contains, or a stash. Only that much exists anywhere else once the worktree's git directory is
    /// deleted. Tags count as kept, since they usually come from upstream, so commits held only by a
    /// tag made inside the submodule are deleted with it, without a refusal.
    /// </summary>
    private async Task<IReadOnlyList<SubmoduleLoss>> FindSubmoduleLossesAsync(
        IReadOnlyList<SubmoduleRepository> repositories,
        CancellationToken cancellationToken)
    {
        var losses = new List<SubmoduleLoss>();

        foreach (var repository in repositories)
        {
            // Asked of the repository alone, since one no longer checked out has no work tree.
            var commits = await _gitClient
                .CountRepositoryCommitsAsync(repository.GitDirectory, ["HEAD", "--branches", "--not", "--remotes", "--tags"], cancellationToken)
                .ConfigureAwait(false);
            var hasStash = await _gitClient.HasStashAsync(repository.GitDirectory, cancellationToken).ConfigureAwait(false);

            if (commits > 0 || hasStash)
            {
                losses.Add(new SubmoduleLoss(repository.Shown, commits, hasStash));
            }
        }

        return losses;
    }

    /// <summary>
    /// The directory git keeps a worktree's record in, found by the worktree path the record names,
    /// or <see langword="null"/> when no record names it. Both paths come from git.
    /// </summary>
    private async Task<string?> FindAdministrativeDirectoryAsync(
        string mainCheckoutRoot,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        foreach (var record in EnumerateRecords(await MainGitDirectoryAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false)))
        {
            if (ReadRecordedWorktree(record) is { } recorded && PathsEqual(recorded, worktreePath))
            {
                return record;
            }
        }

        return null;
    }

    /// <summary>The directories holding git's worktree records, one for each linked worktree.</summary>
    private List<string> EnumerateRecords(string mainGitDirectory)
    {
        var records = Path.Combine(mainGitDirectory, "worktrees");

        try
        {
            return _fileSystem.DirectoryExists(records)
                ? [.. _fileSystem.EnumerateDirectories(records).Select(record => Path.TrimEndingDirectorySeparator(Path.GetFullPath(record)))]
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read git's worktree records in '{records}': {ex.Message}");
        }
    }

    /// <summary>
    /// The worktree directory git's record in <paramref name="administrativeDirectory"/> names, or
    /// <see langword="null"/> when the record names none.
    /// </summary>
    private string? ReadRecordedWorktree(string administrativeDirectory)
    {
        var gitdirFile = Path.Combine(administrativeDirectory, "gitdir");

        try
        {
            if (RecordText(administrativeDirectory, gitdirFile) is not { } record)
            {
                return null;
            }

            // The record holds the path of the worktree's .git file: absolute, or relative to the
            // record's own directory when worktree.useRelativePaths is set.
            var dotGit = Path.GetFullPath(Path.Combine(administrativeDirectory, record.Trim()));
            var recorded = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dotGit));

            // Resolved, because every path this is compared with comes from git resolved. A record
            // written relative to its own directory, as git 2.48 and later can, otherwise leads back
            // through the link a worktrees directory may be reached by, and a healthy worktree would
            // read as one moved by hand.
            return recorded is null ? null : ResolveLinks(recorded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read git's worktree record '{gitdirFile}': {ex.Message}");
        }
    }

    /// <summary>
    /// The text of one of git's records of a worktree in <paramref name="administrativeDirectory"/>, or null where none is
    /// there: read relative to that directory, held from git's directory of every worktree's records with no link on the
    /// way (<see cref="UnfollowedFiles"/>) - a worktree's own records are a place its agent's commands write, and a link
    /// put there would have this read, outside every isolation, print what a hidden place holds.
    /// </summary>
    private string? RecordText(string administrativeDirectory, string file)
    {
        if (!_fileSystem.FileExists(file))
        {
            return null;
        }

        using var reader = new StreamReader(UnfollowedFiles.OpenRead(_fileSystem, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(administrativeDirectory))!, file));

        return reader.ReadToEnd();
    }

    /// <summary>Why the worktree is locked, or <see langword="null"/> when it is not.</summary>
    private string? ReadLockReason(string administrativeDirectory)
    {
        // git records a lock as a file in the worktree's git directory, holding the reason given.
        var lockFile = Path.Combine(administrativeDirectory, "locked");

        try
        {
            return RecordText(administrativeDirectory, lockFile)?.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read whether the worktree is locked, from '{lockFile}': {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes the temporary copy of the index, and the lock file a git killed while writing it leaves
    /// beside it. A scanner can hold a file it has just seen appear: a deletion another program's hold refuses is tried
    /// again for as long as the hold lasts (<see cref="Retrying"/>), told by the file's name while it waits; any other
    /// refusal is tried again only while it is new. What still cannot be deleted is reported under --verbose, and never
    /// fails the command or replaces the answer the copy was made for.
    /// </summary>
    private async Task DeleteTemporaryCopyAsync(string copy)
    {
        foreach (var file in new[] { copy, copy + ".lock" })
        {
            var ended = await Retrying.RunAsync<bool>(
                    (_, _) =>
                    {
                        try
                        {
                            _fileSystem.DeleteFile(file);
                            return Task.FromResult<RetryTry<bool>>(new RetryTry<bool>.Done(true));
                        }
                        catch (IOException ex) when (FileHolds.HeldElsewhere(ex))
                        {
                            return Task.FromResult<RetryTry<bool>>(new RetryTry<bool>.Passing($"another program holds it: {ex.Message.TrimEnd('.')}", After: null));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            return Task.FromResult<RetryTry<bool>>(new RetryTry<bool>.Failed(ex.Message.TrimEnd('.'), ex.Message));
                        }
                    },
                    new RetryWatch(
                        $"the temporary index copy '{ReportText.Printable(file)}' to be deleted",
                        told => _output.Detail(WorktreeService.DeleteCommand, told.Line()),
                        CleanupFirstWait,
                        Bound: null,
                        _clock),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (ended.Kind != RetryEndKind.Done)
            {
                _output.Detail(WorktreeService.DeleteCommand, $"Could not delete the temporary index copy '{file}': {ended.Failure}");
            }
        }
    }

    private bool PathsEqual(string left, string right) => PathContainment.AreSame(left, right, _platform.PathComparison);

    /// <summary>A submodule repository the worktree's deletion would delete.</summary>
    /// <param name="Shown">How the submodule is named in a refusal.</param>
    /// <param name="GitDirectory">Its git directory, as git reports it.</param>
    private sealed record SubmoduleRepository(string Shown, string GitDirectory);
}
