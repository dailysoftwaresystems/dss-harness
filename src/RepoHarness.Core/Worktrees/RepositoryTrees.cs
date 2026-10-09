using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Worktrees;

/// <summary>A tree of the repository on one machine, and what a line calls it.</summary>
/// <param name="Root">Where it is on that machine, as that machine's paths are spelt where it is asked.</param>
/// <param name="Name">What a line calls it: <see cref="MainCheckout"/>, or <c>worktree o1/xa</c>.</param>
public sealed record RepositoryTree(string Root, string Name)
{
    /// <summary>What a line calls the main checkout, or a host's copy of it.</summary>
    public const string MainCheckout = "the main checkout";

    /// <summary>What a line calls the worktree whose copies are kept under <paramref name="copyName"/>: <c>worktree o1/xa</c>.</summary>
    /// <param name="copyName">The name its copies are kept under (<see cref="HostCopies.NameOf"/>).</param>
    public static string Worktree(string copyName) => $"worktree {WorktreeAddress.OfCopyName(copyName)}";
}

/// <summary>The trees of a repository a machine holds, as far as they could be listed.</summary>
/// <param name="Trees">Each tree: the main checkout's first, which is known without listing anything, then the rest.</param>
/// <param name="Unlisted">Why the trees beside the main checkout's could not be listed, where they could not.</param>
public sealed record RepositoryTreesFound(IReadOnlyList<RepositoryTree> Trees, string? Unlisted = null);

/// <summary>
/// Finds the trees of a repository a machine holds - every worktree beside the main checkout, an agent's or a plain one -
/// so that what one of them recorded or runs can be told for what it is on that machine: the room a build of a variant
/// came to there, and whose build directory a process works in.
/// </summary>
public interface IRepositoryTrees
{
    /// <summary>
    /// The trees of <paramref name="context"/>'s repository on this machine: where this machine holds the repository, the
    /// main checkout and every worktree git records; on a host running legs another machine sent it, the main checkout's
    /// copy, where the configuration says this host keeps it, and each worktree's copy beside it.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="here">The host this machine is to the machine that sent the legs here, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <remarks>
    /// A host declaring nowhere to keep its copy holds none: nothing could have been sent to it, since a sync keeps its copy
    /// there. Its trees are said as a listing that could not be made, with why, and never thrown, so no leg's work stops
    /// for them.
    /// </remarks>
    Task<RepositoryTreesFound> HereAsync(HarnessContext context, HostId? here, CancellationToken cancellationToken = default);

    /// <summary>
    /// The copies of <paramref name="context"/>'s repository this machine knows <paramref name="host"/> to keep: the main
    /// checkout's, where the configuration says, and each worktree's this machine's record of host copies holds there.
    /// </summary>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="host">A WSL distribution or an ssh host.</param>
    /// <remarks>A host declaring nowhere to keep its copy holds none, said as <see cref="HereAsync"/> says it.</remarks>
    RepositoryTreesFound On(HarnessContext context, HostId host);
}

/// <inheritdoc cref="IRepositoryTrees"/>
/// <remarks>
/// A consumer's leg was placed on a host whose disk could not hold its build, filled the disk and died, with two other legs
/// building there: neither its own tree's copy nor the main checkout's had built its variant on that host, though three
/// worktrees' copies beside them had, each recording about 11.4 GiB. Nothing is walked: each list is git's record, a look
/// at the directory a host keeps its copies in, or this machine's record of the copies it made.
/// </remarks>
/// <param name="gitClient">Reads git's record of the worktrees.</param>
/// <param name="localTransport">Finds the worktrees' copies a host keeps beside its main copy.</param>
/// <param name="fileSystem">Reads this machine's record of host copies.</param>
/// <param name="platform">How this machine compares paths.</param>
public sealed class RepositoryTrees(IGitClient gitClient, LocalSyncTransport localTransport, IFileSystem fileSystem, IHostPlatform platform) : IRepositoryTrees
{
    private readonly IGitClient _gitClient = gitClient;
    private readonly LocalSyncTransport _localTransport = localTransport;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <inheritdoc/>
    public async Task<RepositoryTreesFound> HereAsync(HarnessContext context, HostId? here, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (here is not null)
        {
            string declared;

            try
            {
                declared = HostCopies.RepositoryPathOf(context.Config, here);
            }
            catch (HarnessException ex)
            {
                return Nowhere(ex);
            }

            // Spelt with this host's home where the configuration keeps the copy under it, as every host's copy is spelt
            // where it is read: a path below a directory called '~' is nowhere.
            var main = LocalSyncTransport.Home(declared);

            try
            {
                return new RepositoryTreesFound(
                [
                    new RepositoryTree(main, RepositoryTree.MainCheckout),
                    .. _localTransport
                        .CopiesBeside(main, HostCopies.WorktreeSuffix, HostCopies.IsCopyName)
                        .Select(copy => new RepositoryTree(copy.Path, RepositoryTree.Worktree(copy.Name))),
                ]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RepositoryTreesFound([new RepositoryTree(main, RepositoryTree.MainCheckout)], ex.Message.TrimEnd('.'));
            }
        }

        var mainCheckout = new RepositoryTree(context.Layout.MainCheckoutRoot, RepositoryTree.MainCheckout);
        IReadOnlyList<GitWorktree> listed;

        try
        {
            listed = await _gitClient.ListWorktreesAsync(context.Layout.MainCheckoutRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return new RepositoryTreesFound([mainCheckout], ex.Message.TrimEnd('.'));
        }

        var worktreesDirectory = context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);
        var comparison = _platform.PathComparison;

        // A worktree under the root is spelt as its address puts it there, as a leg naming it spells its tree, not as git
        // spells it with its links resolved: one tree, said one way. Any other is spelt as git records it.
        return new RepositoryTreesFound(
        [
            mainCheckout,
            .. listed
                .Where(worktree => !worktree.IsMain && !worktree.IsBare)
                .Select(worktree => Path.GetFullPath(worktree.Path))
                .Select(path => new RepositoryTree(
                    HostCopies.AddressOf(worktreesDirectory, path, comparison)?.PathUnder(worktreesDirectory) ?? path,
                    RepositoryTree.Worktree(HostCopies.NameOf(worktreesDirectory, path, comparison)))),
        ]);
    }

    /// <inheritdoc/>
    public RepositoryTreesFound On(HarnessContext context, HostId host)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);

        RepositoryTree main;

        try
        {
            main = new RepositoryTree(HostCopies.RepositoryPathOf(context.Config, host), RepositoryTree.MainCheckout);
        }
        catch (HarnessException ex)
        {
            return Nowhere(ex);
        }

        var spelt = host.ToString();

        try
        {
            return new RepositoryTreesFound(
            [
                main,
                .. new HostCopyRecord(_fileSystem, _platform.PathComparison)
                    .All(context.Layout)
                    .Where(entry => string.Equals(entry.Host, spelt, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => new RepositoryTree(entry.Path, RepositoryTree.Worktree(entry.Worktree))),
            ]);
        }
        catch (HarnessException ex)
        {
            return new RepositoryTreesFound([main], ex.Message.TrimEnd('.'));
        }
    }

    /// <summary>
    /// The trees of a host declaring nowhere to keep its copy, as <paramref name="why"/> says it: none - nothing could have
    /// been sent to it, since a sync keeps its copy there - said as a listing that could not be made.
    /// </summary>
    private static RepositoryTreesFound Nowhere(HarnessException why) => new([], why.Message.TrimEnd('.'));
}
