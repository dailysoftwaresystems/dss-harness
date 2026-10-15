using DssHarness.Core.Execution;
using DssHarness.Core.Output;

namespace DssHarness.Core.Sync;

/// <summary>
/// What one reading of a tree read: the tree, the commit its HEAD named, and how many of the paths a sync of it carries
/// git calls changed - what every copy made from the reading holds, and what a leg on one of them says it tested.
/// </summary>
/// <param name="Tree">The tree, as the run names it.</param>
/// <param name="Commit">The commit its HEAD named; <see langword="null"/> where it named none yet.</param>
/// <param name="UncommittedChanges">
/// How many paths git's status called changed - modified, staged, deleted or untracked - that a sync does not withhold.
/// </param>
public sealed record TreeReading(string Tree, string? Commit, int UncommittedChanges)
{
    /// <summary>What a run says once it has read the tree: <c>'&lt;tree&gt;' was read at commit &lt;12&gt; with N uncommitted change(s)</c>.</summary>
    public string Said => $"'{Tree}' was read {At(Commit, UncommittedChanges)}";

    /// <summary>What a copy made from this reading by <paramref name="run"/> records of it.</summary>
    /// <param name="run">The run that read it, or <see langword="null"/> for a command that keeps no run.</param>
    public CopyReading By(RunId? run) => new(run?.Value, Commit, UncommittedChanges);

    /// <summary>
    /// Where a reading stood: <c>at commit &lt;12&gt;</c> - or <c>at &lt;12&gt;</c>, where the word is said already -
    /// with <c>with N uncommitted change(s)</c> after it where there were any, or <c>with no commit yet</c>. One spelling,
    /// for the line a run says and for each leg's.
    /// </summary>
    /// <param name="commit">The commit, or <see langword="null"/> for none yet.</param>
    /// <param name="uncommittedChanges">How many paths git called changed.</param>
    /// <param name="named">Whether the commit is named as one, as the line a run says names it; a leg's says the tree's.</param>
    internal static string At(string? commit, int uncommittedChanges, bool named = true)
        => commit is null
            ? "with no commit yet"
            : $"at {(named ? "commit " : string.Empty)}{ReportText.Commit(commit)}"
                + (uncommittedChanges > 0 ? $" with {ReportText.Counted(uncommittedChanges, "uncommitted change")}" : string.Empty);
}

/// <summary>
/// Which reading a copy holds, as its marker records it once a sync of it finishes: the run that read the tree, the
/// commit and the count of uncommitted changes. Spelt <c>readBy</c>, <c>commit</c> and <c>uncommittedChanges</c>
/// wherever it is written - the marker, a host's answer, a leg's <c>tree</c> - so one reading is spelt one way.
/// </summary>
/// <param name="ReadBy">The run whose reading it is; <see langword="null"/> where the sync kept no run - the sync command.</param>
/// <param name="Commit">The commit the tree's HEAD named; <see langword="null"/> where it named none yet.</param>
/// <param name="UncommittedChanges">How many paths git called changed, as <see cref="TreeReading"/> counts them.</param>
public sealed record CopyReading(string? ReadBy, string? Commit, int UncommittedChanges)
{
    /// <summary>Whether this is the reading <paramref name="run"/> made: a copy holding another's is no tree that run read.</summary>
    /// <param name="run">The run.</param>
    public bool IsBy(RunId run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return string.Equals(ReadBy, run.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a leg on the copy says it tested: <c>tree read at &lt;12&gt;</c> for the reading of its own run, and
    /// <c>tree staged at &lt;12&gt; by run &lt;id&gt;</c> for what a run on what is staged found there.
    /// </summary>
    /// <param name="staged">Whether the leg ran on what was staged, finding this there, rather than reading it itself.</param>
    public string Said(bool staged)
        => staged
            ? $"tree staged {TreeReading.At(Commit, UncommittedChanges, named: false)}" + (ReadBy is { } run ? $" by {RunLine.Message(run)}" : string.Empty)
            : $"tree read {TreeReading.At(Commit, UncommittedChanges, named: false)}";
}
