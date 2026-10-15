using DssHarness.Core.Git;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// The bytes of a blob a commit holds, as git stores them or as a checkout into a tree writes them: read one way, and
/// refused in one sentence where git holds no such blob, whichever command reads them.
/// </summary>
internal static class CommittedBytes
{
    /// <summary>The bytes git stores for <paramref name="blob"/>.</summary>
    /// <param name="git">Reads the blob.</param>
    /// <param name="tree">A tree of the repository that holds it.</param>
    /// <param name="blob">The blob's id.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <exception cref="HarnessException">git holds no such blob, or could not be asked.</exception>
    public static async Task<byte[]> StoredAsync(IGitClient git, string tree, string blob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);

        return (await git.ReadBlobsAsync(tree, [blob], cancellationToken).ConfigureAwait(false))[0]
            ?? throw new HarnessException(HarnessExit.CommandFailed, $"git holds no blob {blob}.");
    }

    /// <summary>The bytes a checkout of <paramref name="blob"/> writes at <paramref name="path"/> in <paramref name="tree"/>.</summary>
    /// <param name="git">Reads the blob, and brings it to the form a checkout writes.</param>
    /// <param name="tree">The tree whose attributes and configuration say how the path is checked out.</param>
    /// <param name="path">The path, as git names it.</param>
    /// <param name="blob">The blob's id.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <exception cref="HarnessException">git holds no such blob, could not be asked, or could not check it out: a filter that failed, for one.</exception>
    public static async Task<byte[]> CheckedOutAsync(IGitClient git, string tree, string path, string blob, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(git);

        return await git.ReadCheckedOutAsync(tree, path, await StoredAsync(git, tree, blob, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }
}
