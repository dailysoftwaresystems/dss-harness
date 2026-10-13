using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Orchestration;

/// <summary>
/// The tree a command leaves, as the arm registry it merges into that tree is judged: the tree as it stands, under each
/// path the command writes there and without each it removes, before anything is written - since the registry cites
/// texts, and names site files, the same command may be bringing.
/// </summary>
/// <remarks>
/// Each path written is what reads its bytes when the validation asks for it, never the bytes: a fold of hundreds of
/// paths has its registry cite a few, and a blob of a new base costs git processes to read.
/// </remarks>
internal static class TreeAfter
{
    /// <summary>
    /// The view of <paramref name="root"/> once <paramref name="changed"/> is applied, a later entry for a path in place
    /// of an earlier one - as the tree's own file system compares names, so a path a command removes and writes again in
    /// another case is the one file it writes.
    /// </summary>
    /// <param name="sync">Says what a sync of the tree withholds from every copy of it.</param>
    /// <param name="fileSystem">Reads the tree.</param>
    /// <param name="context">The configuration the command loaded, which a sync of the tree decides by.</param>
    /// <param name="root">The tree the registry is written into.</param>
    /// <param name="changed">Each path the command writes there, with what reads its bytes; each it removes, with null.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    /// <exception cref="HarnessException">git could not be asked what it ignores in the tree.</exception>
    public static async Task<IRegistryFiles> OfAsync(
        ISyncService sync,
        IFileSystem fileSystem,
        HarnessContext context,
        string root,
        IEnumerable<(string Path, Func<CancellationToken, Task<byte[]>>? Read)> changed,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(changed);

        var tree = context with { Layout = new HarnessLayout(root, context.Layout.MainCheckoutRoot) };
        var under = await TreeRegistryFiles.OfAsync(sync, fileSystem, tree, cancellationToken).ConfigureAwait(false);
        var written = new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>(under.Names);

        foreach (var (path, read) in changed)
        {
            // Removed first, so the path is spelled as the later entry spells it.
            written.Remove(path);
            written[path] = read;
        }

        return new OverlaidRegistryFiles(under, written);
    }

    /// <summary>
    /// What a fold changes in the main tree: each path it deletes removed, each it writes read from the agent's worktree,
    /// and each merged path read from the bytes in hand - the merged registry among them.
    /// </summary>
    /// <param name="fileSystem">Reads the agent's files.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="deleted">The paths the fold removes.</param>
    /// <param name="written">The paths it copies from the agent's worktree.</param>
    /// <param name="merged">The paths it merges, each with the merged bytes.</param>
    public static IEnumerable<(string Path, Func<CancellationToken, Task<byte[]>>? Read)> Folded(
        IFileSystem fileSystem,
        string worktree,
        IEnumerable<string> deleted,
        IEnumerable<string> written,
        IEnumerable<(string Path, byte[] Bytes)> merged)
        => [
            .. deleted.Select(path => (path, (Func<CancellationToken, Task<byte[]>>?)null)),
            .. written.Select(path => (path, (Func<CancellationToken, Task<byte[]>>?)(token => fileSystem.ReadAllBytesAsync(Path.Combine(worktree, path), token)))),
            .. merged.Select(merge => (merge.Path, Held(merge.Bytes))),
        ];

    /// <summary>
    /// What a move of an agent's base changes in its worktree: each path it takes as the new base holds it - read from its
    /// blob as a checkout into the worktree writes it, or removed where the new base holds no file there - and each merged
    /// path read from the bytes in hand.
    /// </summary>
    /// <param name="git">Reads the new base's blobs, and brings them to the form a checkout writes.</param>
    /// <param name="worktree">The agent's worktree.</param>
    /// <param name="taken">The paths the move takes, each with the blob the new base holds there, or null where it holds none.</param>
    /// <param name="merged">The paths it merges, each with the merged bytes.</param>
    public static IEnumerable<(string Path, Func<CancellationToken, Task<byte[]>>? Read)> Moved(
        IGitClient git,
        string worktree,
        IEnumerable<(string Path, string? Blob)> taken,
        IEnumerable<(string Path, byte[] Bytes)> merged)
        => [
            .. taken.Select(take => (take.Path, take.Blob is { } blob ? (Func<CancellationToken, Task<byte[]>>?)(token => CheckedOutAsync(git, worktree, take.Path, blob, token)) : null)),
            .. merged.Select(merge => (merge.Path, Held(merge.Bytes))),
        ];

    /// <summary>What reads bytes already in hand.</summary>
    private static Func<CancellationToken, Task<byte[]>>? Held(byte[] bytes) => _ => Task.FromResult(bytes);

    /// <summary>
    /// The bytes a checkout of <paramref name="blob"/> writes at <paramref name="path"/> in <paramref name="worktree"/>. One
    /// git cannot give is a file that could not be read, as the validation says of any.
    /// </summary>
    /// <exception cref="IOException">git could not read the blob, or check it out.</exception>
    private static async Task<byte[]> CheckedOutAsync(IGitClient git, string worktree, string path, string blob, CancellationToken cancellationToken)
    {
        try
        {
            var stored = (await git.ReadBlobsAsync(worktree, [blob], cancellationToken).ConfigureAwait(false))[0]
                ?? throw new IOException($"git holds no blob {blob} for '{path}'");

            return await git.ReadCheckedOutAsync(worktree, path, stored, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            throw new IOException(ex.Message, ex);
        }
    }
}
