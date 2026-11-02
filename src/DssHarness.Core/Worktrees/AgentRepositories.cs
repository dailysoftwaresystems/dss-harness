using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Worktrees;

/// <summary>What making an agent's repository did.</summary>
/// <param name="BaseCommit">The commit it was made at, the main checkout's HEAD then; null where it was not made.</param>
/// <param name="Failure">Why it was not made, with what of it could not be taken back; null where it was made.</param>
internal sealed record AgentRepositoryMade(string? BaseCommit, CommandOutcome? Failure);

/// <summary>An agent's repository found below the worktrees root.</summary>
/// <param name="Address">The agent's address, <c>orchestrator/agent</c>: where it sits under the root, and what its mark names.</param>
/// <param name="Path">Where it is, as the worktrees root is spelt.</param>
internal sealed record AgentRepositoryFound(WorktreeAddress Address, string Path);

/// <summary>
/// The one owner of an agent's repository: the tree an orchestrator's agent works in is a git repository of its own, at
/// <c>&lt;worktrees.root&gt;/&lt;orchestrator&gt;/&lt;agent&gt;</c>, with its own refs and its own object directory, borrowing the main
/// checkout's objects read-only through its <c>objects/info/alternates</c> and holding no ref but its detached HEAD.
/// </summary>
/// <remarks>
/// <para>
/// A linked worktree shares its repository's refs and objects: whatever writes in one writes the main checkout's branches,
/// tags and object store. A repository of its own shares neither, so nothing done in it - a commit, a ref written, an
/// object stored under an id it does not hash to - reaches the main checkout: the work it holds comes back only as files
/// read from its tree.
/// </para>
/// <para>
/// It is made by <c>git init</c>, its alternates and configuration written, and a checkout of the main checkout's HEAD -
/// never by a clone, which would copy the main checkout's refs into it as remote-tracking refs that dangle once the main
/// checkout's branches move on and its gc prunes, add a remote, and pick the ref format itself. Its configuration includes
/// the main checkout's, so filters, line endings, merge drivers, remotes and identity are the main checkout's, live; its own
/// keys follow the include and outrank it. The commit it stands on is kept alive in the main checkout's store by
/// <see cref="WorktreeKeepalive"/>, written before anything is made, since the repository holds no ref the main checkout's
/// gc reads.
/// </para>
/// </remarks>
/// <param name="gitClient">Asks the main checkout and makes the repository.</param>
/// <param name="fileSystem">Writes the repository's files and takes back one that could not be made.</param>
/// <param name="platform">How paths compare.</param>
internal sealed class AgentRepositories(IGitClient gitClient, IFileSystem fileSystem, IHostPlatform platform)
{
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <summary>
    /// Whether the directory at <paramref name="path"/>, which sits at <paramref name="address"/> under the worktrees root, is
    /// an agent's repository of the main checkout at <paramref name="mainCheckoutRoot"/>: an agent's address, its <c>.git</c> a
    /// directory whose own configuration carries the mark naming that checkout and that address, and that checkout lending it
    /// its objects. The one rule every command that finds, lists or weighs an agent's tree asks.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, as git spells its root.</param>
    /// <param name="address">Where the directory sits under the worktrees root.</param>
    /// <param name="path">The directory.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">git could not be asked (<see cref="HarnessExit.CommandFailed"/>): never read as no.</exception>
    public async Task<bool> IsAtAsync(string mainCheckoutRoot, WorktreeAddress address, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        return address.IsNested
            && await MarkedAtAsync(mainCheckoutRoot, address, path, cancellationToken).ConfigureAwait(false)
            && await AgentRepositoryMark.BorrowsAsync(
                _gitClient,
                _platform,
                path,
                await AgentRepositoryMark.ObjectsOfAsync(_gitClient, mainCheckoutRoot, cancellationToken).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The agents' repositories of the main checkout at <paramref name="mainCheckoutRoot"/> below
    /// <paramref name="worktreesDirectory"/>, in the order of their addresses: each directory two below the root, in a
    /// directory that is no tree itself, that <see cref="IsAtAsync"/> takes for one.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, as git spells its root.</param>
    /// <param name="worktreesDirectory">The worktrees root, as configured.</param>
    /// <param name="cancellationToken">Stops git.</param>
    /// <exception cref="HarnessException">
    /// A directory could not be looked in, or git could not be asked (<see cref="HarnessExit.CommandFailed"/>): never read as
    /// none there.
    /// </exception>
    public async Task<IReadOnlyList<AgentRepositoryFound>> ListUnderAsync(string mainCheckoutRoot, string worktreesDirectory, CancellationToken cancellationToken)
    {
        if (!_fileSystem.DirectoryExists(worktreesDirectory))
        {
            return [];
        }

        var candidates = new List<AgentRepositoryFound>();

        foreach (var group in Directories(worktreesDirectory).Where(group => !WorktreeInspector.HoldsOwnGit(_fileSystem, group)))
        {
            foreach (var tree in Directories(group).Where(tree => _fileSystem.DirectoryExists(Path.Combine(tree, ".git"))))
            {
                candidates.Add(new AgentRepositoryFound(WorktreeAddress.Nested(Path.GetFileName(group), Path.GetFileName(tree)), tree));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var objects = await AgentRepositoryMark.ObjectsOfAsync(_gitClient, mainCheckoutRoot, cancellationToken).ConfigureAwait(false);
        var found = new List<AgentRepositoryFound>();

        foreach (var candidate in candidates)
        {
            if (await MarkedAtAsync(mainCheckoutRoot, candidate.Address, candidate.Path, cancellationToken).ConfigureAwait(false)
                && await AgentRepositoryMark.BorrowsAsync(_gitClient, _platform, candidate.Path, objects, cancellationToken).ConfigureAwait(false))
            {
                found.Add(candidate);
            }
        }

        return [.. found.OrderBy(repository => repository.Address.Name, StringComparer.Ordinal)];

        IEnumerable<string> Directories(string directory)
        {
            try
            {
                return [.. _fileSystem.EnumerateDirectories(directory).Select(Path.TrimEndingDirectorySeparator)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw WorktreeInspector.CouldNotBeLookedIn(directory, ex);
            }
        }
    }

    /// <summary>Whether the repository at <paramref name="path"/> carries the mark naming the main checkout and <paramref name="address"/>.</summary>
    private async Task<bool> MarkedAtAsync(string mainCheckoutRoot, WorktreeAddress address, string path, CancellationToken cancellationToken)
        => await AgentRepositoryMark.ReadAsync(_gitClient, _fileSystem, path, cancellationToken).ConfigureAwait(false) is { } mark
            && string.Equals(mark.Agent, address.Name, StringComparison.Ordinal)
            && mark.MainCheckout.Length > 0
            && PathContainment.AreSame(mark.MainCheckout, mainCheckoutRoot, _platform.PathComparison);

    /// <summary>
    /// Makes the repository the agent at <paramref name="address"/> works in at <paramref name="path"/>, checked out at the main
    /// checkout's HEAD: the commit is kept alive first, and nothing is made where it cannot be; anything that fails after
    /// takes back what was made, the directory and the keepalive, and says what of either could not be.
    /// </summary>
    /// <param name="mainCheckoutRoot">The main checkout, as git spells its root.</param>
    /// <param name="address">The agent's address, <c>orchestrator/agent</c>.</param>
    /// <param name="path">Where the repository goes, which must not exist; its parent must.</param>
    /// <param name="cancellationToken">
    /// Stops git. Callers pass none once the keepalive could be written: a repository half made is taken back, and
    /// taking it back is never interrupted.
    /// </param>
    public async Task<AgentRepositoryMade> MakeAsync(string mainCheckoutRoot, WorktreeAddress address, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        // What the repository is made from, asked of the main checkout before anything is written: its HEAD, where its objects
        // are, their format, whether its history is cut short, and the settings its included configuration would carry wrong.
        if (await _gitClient.ResolveCommitAsync(mainCheckoutRoot, "HEAD", cancellationToken).ConfigureAwait(false) is not { } head)
        {
            return Refused(
                $"'{ReportText.Printable(mainCheckoutRoot)}' has no commit yet, and the repository '{address.Name}' works in is made at the "
                + "main checkout's HEAD; commit something there first. Nothing was created.");
        }

        var asked = await _gitClient
            .RunAsync(mainCheckoutRoot, ["rev-parse", "--path-format=absolute", "--git-common-dir", "--show-object-format", "--is-shallow-repository"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!asked.Succeeded || asked.OutputLines is not [var common, var format, var shallow])
        {
            return Failed(address, path, $"where the main checkout keeps its objects, and in what format, could not be read: {asked.FailureMessage.TrimEnd('.')}. Nothing was created.");
        }

        var settings = await _gitClient
            .RunAsync(mainCheckoutRoot, ["config", "--null", "--get-regexp", @"^(extensions\.partialclone|core\.worktree|lfs\.storage)$"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // git answers 1 where no key matches.
        if (!settings.Succeeded && (settings.ExitCode != 1 || settings.TimedOut))
        {
            return Failed(address, path, $"the main checkout's configuration could not be read: {settings.FailureMessage.TrimEnd('.')}. Nothing was created.");
        }

        var configured = settings.Succeeded ? Values(settings.StandardOutput) : new Dictionary<string, string>(StringComparer.Ordinal);

        // The keepalive first, and fatal: the repository holds no ref the main checkout's gc reads, and one made at a commit
        // nothing keeps alive would lose it to the first prune once the main checkout's branch moves on.
        if (await WorktreeKeepalive.WriteBaseAsync(_gitClient, mainCheckoutRoot, address.Name, head, cancellationToken).ConfigureAwait(false) is { } unkept)
        {
            return Failed(
                address,
                path,
                $"the commit it is made at, {ReportText.Commit(head)}, could not be kept alive in the main checkout as '{WorktreeKeepalive.BaseRef(address.Name)}': "
                + $"{unkept.TrimEnd('.')}. Nothing was created.");
        }

        string? failure;

        try
        {
            failure = await BuildAsync(mainCheckoutRoot, address, path, head, common, format, shallow == "true", configured, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            failure = ex.Message;
        }

        return failure is null
            ? new AgentRepositoryMade(head, null)
            : Failed(address, path, $"{failure.TrimEnd('.')}. {await TakeBackAsync(mainCheckoutRoot, address, path).ConfigureAwait(false)}");
    }

    /// <summary>Makes the repository once its commit is kept alive; why it could not be, or null where it was made and checked.</summary>
    private async Task<string?> BuildAsync(
        string mainCheckoutRoot,
        WorktreeAddress address,
        string path,
        string head,
        string common,
        string format,
        bool shallow,
        IReadOnlyDictionary<string, string> configured,
        CancellationToken cancellationToken)
    {
        // No template: a template's hooks and files are no part of an agent's repository. The files ref format, which every
        // git reads, whatever the user's configuration asks of a new repository.
        var made = await _gitClient
            .RunAsync(mainCheckoutRoot, ["-c", "init.defaultRefFormat=files", "init", "--quiet", "--template=", $"--object-format={format}", path], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!made.Succeeded)
        {
            return $"git could not make it: {made.FailureMessage}";
        }

        var gitDirectory = Path.Combine(path, ".git");
        var objects = common + "/objects";

        // Absolute, as git spells the main checkout's object directory: a relative one would follow the tree if it moved.
        _fileSystem.WriteAllTextAtomic(Path.Combine(gitDirectory, "objects", "info", "alternates"), objects + "\n");
        _fileSystem.WriteAllTextAtomic(Path.Combine(gitDirectory, "config"), Configuration(_fileSystem.ReadAllText(Path.Combine(gitDirectory, "config")), mainCheckoutRoot, address, path, common, configured));

        // A history cut short in the main checkout is cut short in the same places here, or every walk past the cut would
        // look for parents neither store holds.
        if (shallow)
        {
            _fileSystem.CopyFile(Path.Combine(common, "shallow"), Path.Combine(gitDirectory, "shallow"));
        }

        // The main checkout's sparse patterns, which its included configuration turns on here as well.
        var sparse = Path.Combine(common, "info", "sparse-checkout");

        if (_fileSystem.FileExists(sparse))
        {
            _fileSystem.CreateDirectory(Path.Combine(gitDirectory, "info"));
            _fileSystem.CopyFile(sparse, Path.Combine(gitDirectory, "info", "sparse-checkout"));
        }

        var checkedOut = await _gitClient
            .RunAsync(path, ["checkout", "--quiet", "--detach", head], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!checkedOut.Succeeded)
        {
            return $"git could not check out {ReportText.Commit(head)} in it: {checkedOut.FailureMessage}";
        }

        // git's exit codes are not taken as proof: a hook, a filter or a scanner can take the tree away between git's
        // success and here, and what is reported made must be there, at its commit, borrowing the main checkout's objects.
        if (!_fileSystem.DirectoryExists(path))
        {
            return $"git reported it made, but '{ReportText.Printable(path)}' does not exist";
        }

        var at = await _gitClient.ResolveCommitAsync(path, "HEAD", cancellationToken).ConfigureAwait(false);

        if (at != head)
        {
            return $"it was checked out at {ReportText.Commit(head)}, and its HEAD reads {(at is null ? "no commit" : ReportText.Commit(at))}";
        }

        return await AgentRepositoryMark.BorrowsAsync(_gitClient, _platform, path, objects, cancellationToken).ConfigureAwait(false)
            ? null
            : $"git does not read the main checkout's objects, '{ReportText.Printable(objects)}', from it";
    }

    /// <summary>
    /// The repository's configuration file: the main checkout's included first, then what <c>git init</c> probed of this file
    /// system, then the repository's own keys, each later one outranking what came before.
    /// </summary>
    /// <param name="probed">What <c>git init</c> wrote.</param>
    /// <param name="mainCheckoutRoot">The main checkout.</param>
    /// <param name="address">The agent's address.</param>
    /// <param name="path">The repository's root.</param>
    /// <param name="common">The main checkout's git directory.</param>
    /// <param name="configured">The main checkout's settings that would be wrong here if they came through the include.</param>
    private static string Configuration(string probed, string mainCheckoutRoot, WorktreeAddress address, string path, string common, IReadOnlyDictionary<string, string> configured)
    {
        var text = new StringBuilder()
            .Append(GitConfigText.Section("include", [("path", common + "/config")]))
            .Append(probed.EndsWith('\n') ? probed : probed + "\n");

        // The main checkout's work tree, where its configuration names one, is not this repository's.
        List<(string, string)> core = configured.ContainsKey("core.worktree") ? [("worktree", path)] : [];

        // A partial clone's objects are fetched as they are needed, and this repository needs them too: it lends itself the
        // promisor remote the main checkout names, and the repository format that reads the extension.
        if (configured.TryGetValue("extensions.partialclone", out var promisor))
        {
            core.Add(("repositoryformatversion", "1"));
        }

        if (core.Count > 0)
        {
            text.Append(GitConfigText.Section("core", core));
        }

        // No housekeeping of git's own starts here: an automatic gc or maintenance runs in the background, in a tree a deletion
        // removes, and writes files over commits the main checkout's store holds.
        text.Append(GitConfigText.Section("gc", [("auto", "0")]))
            .Append(GitConfigText.Section("maintenance", [("auto", "false")]))
            .Append(GitConfigText.Section("fetch", [("writeCommitGraph", "false")]));

        // The main checkout's large-file store, resolved as git-lfs resolves it there, against its git directory.
        var storage = configured.TryGetValue("lfs.storage", out var named)
            ? Path.GetFullPath(Path.Combine(common, named))
            : common + "/lfs";

        text.Append(GitConfigText.Section("lfs", [("storage", storage)]));

        if (promisor is not null)
        {
            text.Append(GitConfigText.Section("extensions", [("partialClone", promisor)]));
        }

        return text.Append(new AgentRepositoryMark(mainCheckoutRoot, address.Name).Section).ToString();
    }

    /// <summary>
    /// Takes back a repository that could not be made whole: its directory, and the keepalive written for it. Never
    /// interrupted. What it says follows the failure: that nothing was created, or what is left and how to remove it.
    /// </summary>
    private async Task<string> TakeBackAsync(string mainCheckoutRoot, WorktreeAddress address, string path)
    {
        var left = new List<string>();

        try
        {
            _fileSystem.DeleteDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left.Add($"'{ReportText.Printable(path)}' could not be removed: {ex.Message.TrimEnd('.')}; delete it by hand");
        }

        foreach (var (reference, why) in await WorktreeKeepalive.ForgetAsync(_gitClient, mainCheckoutRoot, address.Name, CancellationToken.None).ConfigureAwait(false))
        {
            left.Add($"'{reference}' could not be removed: {why.TrimEnd('.')}; remove it with 'git update-ref -d {reference}'");
        }

        return left.Count == 0
            ? "What was made of it was taken back: nothing was created."
            : $"What was made of it was taken back, but {string.Join(", and ", left)}.";
    }

    /// <summary>What <c>git config --null --get-regexp</c> answered, by key: the last of a key's values, which is the one git reads.</summary>
    private static Dictionary<string, string> Values(string output)
        => output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('\n', 2))
            .GroupBy(pair => pair[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last() is [_, var value] ? value : string.Empty, StringComparer.Ordinal);

    private static AgentRepositoryMade Refused(string message) => new(null, CommandOutcome.Refused(message));

    private static AgentRepositoryMade Failed(WorktreeAddress address, string path, string message)
        => new(null, CommandOutcome.Failed(HarnessExit.CommandFailed, $"The repository '{address.Name}' works in could not be made at '{ReportText.Printable(path)}': {message}"));
}
