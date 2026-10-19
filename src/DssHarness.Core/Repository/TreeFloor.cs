using DssHarness.Core.Configuration;

namespace DssHarness.Core.Repository;

/// <summary>
/// The paths of a tree that are never moved into another tree, whatever git says of them: git's own directory, what
/// orchestrators keep, the tree's own copies - its mutation workers and the copies its legs work in - and where worktrees
/// are made. Sync withholds them from every copy, and an agent is never seeded with them nor folded back from them.
/// </summary>
/// <remarks>
/// Asserted where the moving happens rather than left to the ignore rules, which a repository can edit away: without a
/// rule ignoring the worktrees root, git would list every agent's worktree as work of the main tree's, and a seed would
/// copy each into the next, <c>.git</c> included.
/// </remarks>
public static class TreeFloor
{
    /// <summary>The floor of a tree whose worktrees root is <paramref name="worktreesRoot"/>, relative to the tree.</summary>
    /// <param name="worktreesRoot">The configured worktrees root.</param>
    public static IReadOnlyList<string> Of(string worktreesRoot)
    {
        ArgumentNullException.ThrowIfNull(worktreesRoot);

        return [.. SyncConfig.NeverTransferFloor.Append(PathPatterns.Normalize(worktreesRoot)).Where(path => path.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The floor for the moves an agent takes part in - what it is seeded with, folded back, and
    /// what refresh and rebase move - which the tree's credential slots join
    /// (<see cref="HarnessLayout.SecretSlots"/>).
    /// </summary>
    /// <param name="worktreesRoot">The configured worktrees root.</param>
    /// <remarks>
    /// The slots are already covered by the managed ignore block, but an ignore rule is a rule a
    /// repository can edit away, and a planted <c>.env</c> folded back could redirect a key: a
    /// floor names what no move ever takes, whatever git or the ignore block says of it.
    /// <see cref="Of"/> stays as it is, for the verbs that are not an agent's moves: it also feeds
    /// what sync withholds, where a slot is already never transferred and its contents are read
    /// from the main checkout alone.
    /// </remarks>
    public static IReadOnlyList<string> OfAgentMoves(string worktreesRoot)
    {
        ArgumentNullException.ThrowIfNull(worktreesRoot);

        return [.. Of(worktreesRoot).Concat(HarnessLayout.SecretSlots).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether <paramref name="relativePath"/> is on <paramref name="floor"/>, or beneath a directory on it.</summary>
    /// <param name="floor">A floor, as <see cref="Of"/> gives it.</param>
    /// <param name="relativePath">A path relative to the tree.</param>
    public static bool Covers(IReadOnlyList<string> floor, string relativePath) => PathPatterns.Matches(floor, relativePath);
}
