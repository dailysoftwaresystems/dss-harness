using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Git;
using DssHarness.Core.Legs;

namespace DssHarness.Core.Worktrees;

/// <summary>
/// What a worktree must leave room for below it within this machine's path limit: the one owner the check that makes a
/// worktree and a run's start check both ask, so a run that passes its start never fails on a worktree it needs.
/// </summary>
/// <remarks>
/// Two things lie below a tree: the files the repository tracks, which the tree holds from the moment it is made, and,
/// where a leg builds on this machine, the build in it. Each is reckoned as what hangs off the tree after the one
/// separator the budget counts itself (<see cref="Platform.IPathBudget.Check"/>), and the deeper of the two is what the
/// tree needs.
/// </remarks>
public static class WorktreeRoom
{
    /// <summary>
    /// How many characters a build on this machine adds below a tree it builds in: <c>build/&lt;variant&gt;/</c> for the
    /// longest variant among the legs this machine builds, and below that <c>worktrees.pathBudgetReserve</c>, the longest
    /// path a build of the project makes there; nothing where this machine builds no leg, for then no build is coming
    /// below the tree and the reserve guesses at nothing.
    /// </summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="platformKey">This machine's operating system, as a leg names one.</param>
    /// <remarks>
    /// The build directory is counted here rather than folded into the reserve, because the harness knows every
    /// variant's name and the reserve could only guess at one: when build directories came to be keyed by variant, a
    /// reserve measured below the build directory stopped covering the directory itself, and the check believed it had
    /// twenty characters to spare where it had four. A leg builds here when it names no host of its own and its
    /// operating system is this machine's (<see cref="LegTrees.BuildsHere"/>); one that names a host builds there,
    /// whatever its os says. Every such leg is counted, whichever step names it, for a worktree is made before anything
    /// says which legs its work will build.
    /// </remarks>
    public static int ForBuilds(HarnessConfig config, string platformKey)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformKey);

        var longest = config.Legs.Values
            .Where(leg => LegTrees.BuildsHere(leg, platformKey))
            .Select(leg => VariantKey.For(config, leg, platformKey).DirectoryName.Length)
            .DefaultIfEmpty(-1)
            .Max();

        // A separator between 'build' and the variant, and one after the variant. The one before 'build' joins the tree
        // to what lies below it, which the budget counts itself.
        return longest < 0 ? 0 : VariantKey.BuildRootName.Length + longest + 2 + config.Worktrees.PathBudgetReserve;
    }

    /// <summary>
    /// The longest path, relative to the tree, of a file the repository tracks at <paramref name="commit"/>: what a tree
    /// made there holds before anything is built in it, and what every copy of it holds too.
    /// </summary>
    /// <param name="gitClient">Lists the commit's files.</param>
    /// <param name="checkout">A checkout of the repository that holds the commit.</param>
    /// <param name="commit">The commit the tree is made at, as <see cref="IGitClient.ResolveCommitAsync"/> returns one.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <exception cref="Results.HarnessException">git could not list the commit's files.</exception>
    public static async Task<int> ForFilesAsync(IGitClient gitClient, string checkout, string commit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(checkout);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        var files = await gitClient.ListFilesAtCommitAsync(checkout, commit, cancellationToken).ConfigureAwait(false);

        return files.Select(file => file.Text.Length).DefaultIfEmpty(0).Max();
    }
}
