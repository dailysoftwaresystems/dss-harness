using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Mutations;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Core.Orchestration;

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
    /// What a command that copies files from one tree into the other changes there: each path it removes, each it copies
    /// read from the tree it copies from, and each merged path read from the bytes in hand - the merged registry among
    /// them. A fold copies from the agent's worktree into the main tree; a refresh from the main tree into the worktree.
    /// </summary>
    /// <param name="fileSystem">Reads the files copied.</param>
    /// <param name="from">The tree the files are copied from.</param>
    /// <param name="removed">The paths the command removes.</param>
    /// <param name="copied">The paths it copies.</param>
    /// <param name="merged">The paths it merges, each with the merged bytes.</param>
    public static IEnumerable<(string Path, Func<CancellationToken, Task<byte[]>>? Read)> Copied(
        IFileSystem fileSystem,
        string from,
        IEnumerable<string> removed,
        IEnumerable<string> copied,
        IEnumerable<(string Path, byte[] Bytes)> merged)
        => [
            .. removed.Select(path => (path, (Func<CancellationToken, Task<byte[]>>?)null)),
            .. copied.Select(path => (path, (Func<CancellationToken, Task<byte[]>>?)(token => fileSystem.ReadAllBytesAsync(Path.Combine(from, path), token)))),
            .. merged.Select(merge => (merge.Path, Held(merge.Bytes))),
        ];

    /// <summary>
    /// What a move of an agent's base changes in its worktree: each path it takes as the new base holds it - removed where
    /// the new base holds no file there, and then read from its blob as a checkout into the worktree writes it - and each
    /// merged path read from the bytes in hand. Removals first, as the move makes them, so a file renamed in case alone is
    /// the file written.
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
            .. taken.Where(take => take.Blob is null).Select(take => (take.Path, (Func<CancellationToken, Task<byte[]>>?)null)),
            .. taken.Where(take => take.Blob is not null).Select(take => (take.Path, (Func<CancellationToken, Task<byte[]>>?)(token => CheckedOutAsync(git, worktree, take.Path, take.Blob!, token)))),
            .. merged.Select(merge => (merge.Path, Held(merge.Bytes))),
        ];

    /// <summary>What reads bytes already in hand.</summary>
    private static Func<CancellationToken, Task<byte[]>>? Held(byte[] bytes) => _ => Task.FromResult(bytes);

    /// <summary>
    /// The bytes a checkout of <paramref name="blob"/> writes at <paramref name="path"/> in <paramref name="worktree"/>
    /// (<see cref="CommittedBytes"/>). One git cannot give is a file that could not be read, as the validation says of any.
    /// </summary>
    /// <exception cref="IOException">git could not read the blob, or check it out.</exception>
    private static async Task<byte[]> CheckedOutAsync(IGitClient git, string worktree, string path, string blob, CancellationToken cancellationToken)
    {
        try
        {
            return await CommittedBytes.CheckedOutAsync(git, worktree, path, blob, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            throw new IOException(ex.Message, ex);
        }
    }
}
