using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// The lock that keeps the engine's writes to a repository's main tree and a side-task's writes and commands apart:
/// one at a time, whichever of the two is writing.
/// </summary>
/// <remarks>
/// <para>
/// Every write the engine makes in the main tree - a fold, a deletion that folds, a refresh, a rebase, a commit step -
/// takes it, from the measure its write is weighed by to its last write, and so does every write tool and every command
/// of a side-task's agent, for the duration of the one write or command. A fold never weighs or writes a file a running
/// write or command is part way through, and a side-task's write waits while a fold applies; a long command holds the
/// folds back for as long as it runs, and each side waits out the other rather than refusing, for neither can tell how
/// long the other's work is, and both end. The engine's wait ends with the command's or the step's own stop, nothing
/// weighed or written; once held, its work is never interrupted. A holder that ended holding it - a process killed part
/// way - leaves the tree as it stopped, and the next holder takes the lock all the same (<see cref="MachineWideMutex.Wait(Mutex, TimeSpan)"/>).
/// </para>
/// <para>
/// It is a <see cref="MachineWideMutex"/> named for the tree's path, so two processes of this tool contend for it
/// however their terminals were opened. The engine's other main-tree serialisation - the run lock - is among harness
/// engines alone and is refused rather than waited for; a side-task takes no part in it, which is the gap this lock
/// fills. Case is folded, whatever the filesystem says: two spellings of one tree must share a lock, and on a
/// case-sensitive filesystem the cost of folding is only a moment of waiting between trees that are none of each other.
/// A hold is a thread's own, so the engine takes it once for the whole of one write and hands that hold on: a second
/// take on another thread of the same process would wait on the first forever.
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

    /// <summary>The member of a step's output that says what its wait for the lock was.</summary>
    public const string WaitedMember = "waited";

    /// <summary>One mutex per main tree, however its path is spelled.</summary>
    public static string NameFor(string mainCheckoutRoot)
        => MachineWideMutex.NameFor(Prefix, [mainCheckoutRoot], ignoreCase: true);

    /// <summary>
    /// Takes the tree's lock for one write or command, waiting as long as <paramref name="cancellationToken"/> leaves
    /// it: a side-task's command can run long, and the engine's fold waits it out, as the engine's fold can.
    /// </summary>
    /// <param name="mainCheckoutRoot">The tree the lock is named for.</param>
    /// <param name="cancellationToken">Ends the wait where the lock was not taken.</param>
    /// <returns>
    /// The hold, which says how long it waited; disposed, the write or command is alone in the tree. Null where the
    /// token ended the wait: nothing else ends it.
    /// </returns>
    /// <exception cref="Results.HarnessException">The mutex could not be opened, as <see cref="MachineWideMutex.Open(string, string)"/> says.</exception>
    public static Task<MachineWideHold?> HoldAsync(string mainCheckoutRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCheckoutRoot);

        return MachineWideMutex.HoldAsync(NameFor(mainCheckoutRoot), $"the main tree '{mainCheckoutRoot}'", System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
    }

    /// <summary>
    /// What a side-task's chat says of its write's or command's wait for the lock, where it waited - the one form of it on
    /// every client: the seconds to a tenth, in the invariant culture, or "a moment" for a wait shorter than a tenth would
    /// show; null where it did not wait.
    /// </summary>
    /// <param name="waited">How long the take waited; zero where nobody held the lock.</param>
    public static string? WaitedNotice(TimeSpan waited)
    {
        if (waited <= TimeSpan.Zero)
        {
            return null;
        }

        var seconds = Math.Round(waited.TotalSeconds, 1);
        var said = seconds == 0 ? "a moment" : string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#} s");

        return $"waited {said} for {WaitedOutOnFromSideTask} to end";
    }

    /// <summary>
    /// What a step's output says of its wait for the lock, where it waited: the seconds, and what it waited out - the
    /// one form of it, whichever step waited. The output it is given gains the member alone; with none given, one is
    /// made for it. Nothing is said where it did not wait.
    /// </summary>
    /// <param name="waited">How long the step waited; zero where it did not.</param>
    /// <param name="output">The output the step built; null where it built none.</param>
    /// <returns>The output with the wait said; <paramref name="output"/> as it was where the step did not wait.</returns>
    public static JsonObject? WaitedFor(TimeSpan waited, JsonObject? output)
    {
        if (waited <= TimeSpan.Zero)
        {
            return output;
        }

        (output ??= new JsonObject())[WaitedMember] = new JsonObject
        {
            ["seconds"] = Math.Round(waited.TotalSeconds, 1),
            ["for"] = WaitedOutOn,
        };

        return output;
    }
}
