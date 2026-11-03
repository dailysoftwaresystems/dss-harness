using DssHarness.Core.Git;

namespace DssHarness.Core.Worktrees;

/// <summary>
/// The refs in the main checkout that record the commit each worktree stands on and keep it alive there, the one owner of
/// them: <c>refs/harness/worktree-base/&lt;name&gt;</c> for the commit a worktree was made from or last moved to, and, while an
/// agent's base is being moved, <c>refs/harness/worktree-moving/&lt;name&gt;</c> for the commit it is moving to.
/// </summary>
/// <remarks>
/// Under <c>refs/harness/</c> rather than under heads, tags or remotes, so a record can never be mistaken for somewhere work
/// is kept, and a deletion's check, which reads branches, tags, remote-tracking refs, the newest stash and other worktrees'
/// HEADs, never takes one for keeping lost work safe. A ref is the record because git keeps it, it survives a clone of the
/// repository, and every pruning git does - gc, maintenance, repack - keeps what a ref reaches: an agent's repository
/// borrows the main checkout's objects and holds no ref of the main checkout's own, so these refs are what keep the commit
/// it stands on, and everything that commit reaches, in the store it borrows from.
/// </remarks>
internal static class WorktreeKeepalive
{
    /// <summary>Where the commit a worktree stands on is recorded.</summary>
    public const string BasePrefix = "refs/harness/worktree-base/";

    /// <summary>Where the commit an agent's base is moving to is recorded, from before the move writes anything until its record names it.</summary>
    public const string MovingPrefix = "refs/harness/worktree-moving/";

    /// <summary>The ref recording the commit the worktree <paramref name="name"/> stands on.</summary>
    /// <param name="name">The worktree's address.</param>
    public static string BaseRef(string name) => BasePrefix + name;

    /// <summary>The ref recording the commit the base of the worktree <paramref name="name"/> is moving to.</summary>
    /// <param name="name">The worktree's address.</param>
    public static string MovingRef(string name) => MovingPrefix + name;

    /// <summary>Records <paramref name="commit"/> as the commit the worktree <paramref name="name"/> stands on.</summary>
    /// <param name="gitClient">Writes the ref.</param>
    /// <param name="mainCheckoutRoot">The main checkout, whose repository holds the ref.</param>
    /// <param name="name">The worktree's address.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>Why the record could not be written; null where it was.</returns>
    public static Task<string?> WriteBaseAsync(IGitClient gitClient, string mainCheckoutRoot, string name, string commit, CancellationToken cancellationToken)
        => WriteAsync(gitClient, mainCheckoutRoot, BaseRef(name), commit, cancellationToken);

    /// <summary>Records <paramref name="commit"/> as the commit the base of the worktree <paramref name="name"/> is moving to.</summary>
    /// <param name="gitClient">Writes the ref.</param>
    /// <param name="mainCheckoutRoot">The main checkout, whose repository holds the ref.</param>
    /// <param name="name">The worktree's address.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>Why the record could not be written; null where it was.</returns>
    public static Task<string?> WriteMovingAsync(IGitClient gitClient, string mainCheckoutRoot, string name, string commit, CancellationToken cancellationToken)
        => WriteAsync(gitClient, mainCheckoutRoot, MovingRef(name), commit, cancellationToken);

    /// <summary>
    /// Records a finished move: <paramref name="commit"/> as the commit the worktree <paramref name="name"/> stands on, and
    /// then no move, in that order, so the commit is kept alive by one ref or the other at every moment between.
    /// </summary>
    /// <param name="gitClient">Writes the refs.</param>
    /// <param name="mainCheckoutRoot">The main checkout, whose repository holds the refs.</param>
    /// <param name="name">The worktree's address.</param>
    /// <param name="commit">The commit the move went to.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>Why the records could not be written; null where they were.</returns>
    public static async Task<string?> SettleAsync(IGitClient gitClient, string mainCheckoutRoot, string name, string commit, CancellationToken cancellationToken)
        => await WriteBaseAsync(gitClient, mainCheckoutRoot, name, commit, cancellationToken).ConfigureAwait(false)
            ?? await DeleteAsync(gitClient, mainCheckoutRoot, MovingRef(name), cancellationToken).ConfigureAwait(false);

    /// <summary>Reads the commit recorded as the one the worktree <paramref name="name"/> stands on, or null where none is.</summary>
    /// <param name="gitClient">Reads the ref.</param>
    /// <param name="mainCheckoutRoot">The main checkout, whose repository holds the ref.</param>
    /// <param name="name">The worktree's address.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <exception cref="Results.HarnessException">
    /// git could not be asked: distinguished from "no such ref" on purpose, since null is reported as a worktree made before
    /// the record existed, and a reader who sees that stops looking.
    /// </exception>
    public static Task<string?> ReadBaseAsync(IGitClient gitClient, string mainCheckoutRoot, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        return gitClient.ResolveCommitAsync(mainCheckoutRoot, BaseRef(name), cancellationToken);
    }

    /// <summary>Reads the commit recorded as the one the base of the worktree <paramref name="name"/> is moving to, or null where none is.</summary>
    /// <param name="gitClient">Reads the ref.</param>
    /// <param name="mainCheckoutRoot">The main checkout, whose repository holds the ref.</param>
    /// <param name="name">The worktree's address.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <exception cref="Results.HarnessException">git could not be asked.</exception>
    public static Task<string?> ReadMovingAsync(IGitClient gitClient, string mainCheckoutRoot, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        return gitClient.ResolveCommitAsync(mainCheckoutRoot, MovingRef(name), cancellationToken);
    }

    /// <summary>
    /// Removes both records of the worktree <paramref name="name"/>, once nothing of it is left that they keep alive: left
    /// behind, one would answer for a later worktree of the same name with the commit an earlier one stood on, and keep that
    /// commit from ever being pruned.
    /// </summary>
    /// <param name="gitClient">Removes the refs.</param>
    /// <param name="mainCheckoutRoot">The main checkout, whose repository holds the refs.</param>
    /// <param name="name">The worktree's address.</param>
    /// <param name="cancellationToken">Cancels git.</param>
    /// <returns>Each ref that could not be removed, with why; empty where both are gone.</returns>
    public static async Task<IReadOnlyList<(string Ref, string Why)>> ForgetAsync(IGitClient gitClient, string mainCheckoutRoot, string name, CancellationToken cancellationToken)
    {
        var left = new List<(string Ref, string Why)>();

        foreach (var reference in new[] { BaseRef(name), MovingRef(name) })
        {
            if (await DeleteAsync(gitClient, mainCheckoutRoot, reference, cancellationToken).ConfigureAwait(false) is { } why)
            {
                left.Add((reference, why));
            }
        }

        return left;
    }

    private static async Task<string?> WriteAsync(IGitClient gitClient, string mainCheckoutRoot, string reference, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);

        var written = await gitClient
            .RunAsync(mainCheckoutRoot, ["update-ref", reference, commit], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return written.Succeeded ? null : written.FailureMessage;
    }

    /// <summary>Deletes <paramref name="reference"/>; one that is not there is deleted already, as git takes it.</summary>
    private static async Task<string?> DeleteAsync(IGitClient gitClient, string mainCheckoutRoot, string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);

        var deleted = await gitClient
            .RunAsync(mainCheckoutRoot, ["update-ref", "-d", reference], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return deleted.Succeeded ? null : deleted.FailureMessage;
    }
}
