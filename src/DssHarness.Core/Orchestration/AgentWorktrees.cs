using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// The one rule of what the directory at an agent's worktree path is to git: the agent commands work on it only where it
/// stands registered, and <c>list-orchestrator</c> says a live agent stands at its worktree only there.
/// </summary>
internal static class AgentWorktrees
{
    /// <summary>What the directory at an agent's worktree path is.</summary>
    public enum Standing
    {
        Registered,
        Gone,
        Husk,
        Foreign,
        Unreadable,

        /// <summary>A worktree git records at another place holding none now, as after the checkout was moved by hand.</summary>
        Moved,
    }

    /// <summary>What the directory at an agent's worktree path is, its git directory where it is a worktree, and why where it is not usable.</summary>
    public sealed record WorktreeStanding(Standing Kind, string? AdministrativeDirectory, string? Why);

    /// <summary>What the directory at an agent's worktree path is to git; a path that cannot be looked at is never taken for one that is gone.</summary>
    /// <param name="fileSystem">Looks at the path.</param>
    /// <param name="inspector">Asks git.</param>
    /// <param name="mainCheckoutRoot">The main checkout.</param>
    /// <param name="record">The agent's record, which says the kind of tree it was made with.</param>
    /// <param name="path">Where its worktree is.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    public static async Task<WorktreeStanding> StandingAsync(
        IFileSystem fileSystem,
        WorktreeInspector inspector,
        string mainCheckoutRoot,
        AgentRecord record,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            switch (fileSystem.KindOf(path))
            {
                case PathKind.None:
                    return new WorktreeStanding(Standing.Gone, null, null);
                case PathKind.File:
                    return new WorktreeStanding(Standing.Foreign, null, "it is a file, not a directory");
                case PathKind.Link:
                    return new WorktreeStanding(Standing.Foreign, null, "it is a link, and a worktree is never reached through one");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorktreeStanding(Standing.Unreadable, null, $"whether anything is there cannot be told: {ex.Message.TrimEnd('.')}");
        }

        WorktreeIdentity identity;
        string? moved = null;

        try
        {
            identity = await inspector.IdentifyAsync(mainCheckoutRoot, WorktreeAddress.Nested(record.Orchestrator, record.Name), path, cancellationToken).ConfigureAwait(false);

            // A linked worktree git cannot open from its .git file may be one whose checkout was moved by hand: git's record of
            // it names where it was, and its work is all still in it.
            if (identity.Membership == WorktreeMembership.NotAWorktree && !record.OwnRepository && WorktreeInspector.HoldsOwnGit(fileSystem, path))
            {
                moved = await inspector.MovedFromAsync(mainCheckoutRoot, path, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return new WorktreeStanding(Standing.Unreadable, null, ex.Message);
        }

        if (moved is not null)
        {
            return new WorktreeStanding(Standing.Moved, null, WorktreeInspector.SaidMoved(mainCheckoutRoot, path, moved));
        }

        // A husk is never read: a directory with no .git of its own is answered for by the main checkout that contains it,
        // whose HEAD and uncommitted changes would be read as the agent's. A tree is the agent's only as the kind its record
        // names: a repository of its own where the record says so, one of git's linked worktrees where an earlier version's
        // record says nothing - one of the other kind there is not the tree the agent was made with.
        return (identity.Membership, record.OwnRepository) switch
        {
            (WorktreeMembership.AgentRepository, true) or (WorktreeMembership.OfThisRepository, false) => new WorktreeStanding(Standing.Registered, identity.AdministrativeDirectory, null),
            (WorktreeMembership.OfThisRepository, true) => new WorktreeStanding(Standing.Foreign, null, "it is one of git's linked worktrees, and its record says it was made a repository of its own"),
            (WorktreeMembership.AgentRepository, false) => new WorktreeStanding(Standing.Foreign, null, "it is a repository of its own, and its record says it was made one of git's linked worktrees"),
            (WorktreeMembership.OfAnotherRepository, true) => new WorktreeStanding(Standing.Foreign, null, "it is a repository whose own configuration names no agent of this main checkout at its address, or which does not borrow this main checkout's objects"),
            (WorktreeMembership.OfAnotherRepository, false) => new WorktreeStanding(Standing.Foreign, null, "it is a repository of its own"),
            _ when WorktreeInspector.HoldsOwnGit(fileSystem, path) => new WorktreeStanding(Standing.Unreadable, null, "its .git is there, and git cannot read the worktree from it"),
            _ => new WorktreeStanding(Standing.Husk, null, "it holds no .git of its own, and git answers for it with the main checkout"),
        };
    }
}
