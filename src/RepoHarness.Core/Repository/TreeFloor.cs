using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Repository;

/// <summary>
/// The paths of a tree that are never moved into another tree, whatever git says of them: git's own directory, what
/// orchestrators keep, and where worktrees are made. Sync withholds them from every host's copy, and an agent is never
/// seeded with them nor folded back from them.
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

    /// <summary>Whether <paramref name="relativePath"/> is on <paramref name="floor"/>, or beneath a directory on it.</summary>
    /// <param name="floor">A floor, as <see cref="Of"/> gives it.</param>
    /// <param name="relativePath">A path relative to the tree.</param>
    public static bool Covers(IReadOnlyList<string> floor, string relativePath) => PathPatterns.Matches(floor, relativePath);
}
