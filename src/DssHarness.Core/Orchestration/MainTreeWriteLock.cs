using DssHarness.Core.Platform;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// The lock that keeps the engine's writes to a repository's main tree and a side-task's writes and commands apart:
/// one at a time, whichever of the two is writing (the user, 2026-10-19; part 03, "One write at a time").
/// </summary>
/// <remarks>
/// <para>
/// Every write the engine makes in the main tree - a fold, a deletion that folds, a refresh, a rebase, a commit step -
/// takes it, and so does every write tool and every command of a side-task's agent, for the duration of the one write
/// or command. A fold never meets a half-written file, and a side-task's write waits while a fold applies; a long
/// command holds the folds back for as long as it runs, and each side waits out the other rather than refusing, for
/// neither can tell how long the other's work is, and both end.
/// </para>
/// <para>
/// It is a <see cref="MachineWideMutex"/> named for the tree's path, so two processes of this tool contend for it
/// however their terminals were opened. The engine's other main-tree serialisation - the run lock - is among harness
/// engines alone and is refused rather than waited for; a side-task takes no part in it, which is the gap this lock
/// fills. Case is folded, whatever the filesystem says: two spellings of one tree must share a lock, and on a
/// case-sensitive filesystem the cost of folding is only a moment of waiting between trees that are none of each other.
/// </para>
/// </remarks>
public static class MainTreeWriteLock
{
    /// <summary>The mutex's kind, so no other subject shares a name with it.</summary>
    public const string Prefix = "main-tree";

    /// <summary>What the engine's side of the lock names the other holder, as the step's record says it.</summary>
    public const string WaitedOutOn = "a side-task's write or command";

    /// <summary>What a side-task's side of the lock names the other holder, as its chat says it.</summary>
    public const string WaitedOutOnFromSideTask = "a run writing the main tree";

    /// <summary>One mutex per main tree, however its path is spelled.</summary>
    public static string NameFor(string mainCheckoutRoot)
        => MachineWideMutex.NameFor(Prefix, [mainCheckoutRoot], ignoreCase: true);

    /// <summary>
    /// Takes the tree's lock for one write or command, waiting as long as <paramref name="cancellationToken"/> leaves
    /// it: a side-task's command can run long, and the engine's fold waits it out, as the engine's fold can.
    /// </summary>
    /// <param name="mainCheckoutRoot">The tree the lock is named for.</param>
    /// <param name="cancellationToken">Ends the wait where the lock was not taken.</param>
    /// <returns>The hold, which says how long it waited; disposed, the write or command is alone in the tree.</returns>
    /// <exception cref="Results.HarnessException">The mutex could not be opened, as <see cref="MachineWideMutex.Open(string, string)"/> says.</exception>
    public static Task<MachineWideHold?> HoldAsync(string mainCheckoutRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckoutRoot);

        return MachineWideMutex.HoldAsync(NameFor(mainCheckoutRoot), $"the main tree '{mainCheckoutRoot}'", System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
