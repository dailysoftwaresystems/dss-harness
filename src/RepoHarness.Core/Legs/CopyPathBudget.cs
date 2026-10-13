using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Legs;

/// <summary>
/// Whether the build in a copy of a tree kept beside that tree stays within this machine's path limit: said once, for
/// every such copy a variant is built in.
/// </summary>
/// <remarks>
/// Reckoned as a worktree's is: the copy, its variant's build directory below it, and below that the longest path a
/// build of the project makes, with <c>worktrees.pathBudgetMargin</c> to spare, under <c>worktrees.pathLimit</c> or the
/// machine's own. A copy whose build passes the limit fails as compile errors in files nobody touched, so it is asked
/// before the copy is made.
/// </remarks>
public static class CopyPathBudget
{
    /// <summary>
    /// The longest path the build in <paramref name="copy"/> makes below it: its variant's build directory, the separator
    /// before it counted - what a worktree's check counts for the longest variant this machine builds - and below that
    /// <paramref name="reserve"/>.
    /// </summary>
    /// <param name="copy">The copy.</param>
    /// <param name="variant">The variant built in it.</param>
    /// <param name="reserve">The longest path a build of the project makes below its build directory.</param>
    public static int BuildBelow(string copy, VariantKey variant, int reserve)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(copy);
        ArgumentNullException.ThrowIfNull(variant);

        return variant.DirectoryUnder(copy).Length - Path.TrimEndingDirectorySeparator(copy).Length + reserve;
    }

    /// <summary>
    /// Whether the build of <paramref name="variant"/> in <paramref name="copy"/> stays within the path limit, by the
    /// margin and the limit <paramref name="settings"/> declare.
    /// </summary>
    /// <param name="budget">Reckons a path against this machine's limit.</param>
    /// <param name="settings">The repository's worktree settings: the margin, and a limit of its own where it declares one.</param>
    /// <param name="copy">The copy.</param>
    /// <param name="variant">The variant built in it.</param>
    /// <param name="reserve">The longest path a build of the project makes below its build directory.</param>
    /// <param name="given">
    /// The longest path below the copy among what it is given beside its tree - the dependency sources a sweep gives a
    /// worker - which reckons the copy where it is the longer; nothing where it is given none.
    /// </param>
    public static PathBudgetResult Check(IPathBudget budget, WorktreeSettings settings, string copy, VariantKey variant, int reserve, int given = 0)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(settings);

        return budget.Check(copy, Math.Max(BuildBelow(copy, variant, reserve), given), settings.PathBudgetMargin, settings.PathLimit);
    }
}
