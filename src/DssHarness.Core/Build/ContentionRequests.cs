using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Build;

/// <summary>What sampling watches for during one leg, from the configuration every verb reads.</summary>
/// <remarks>
/// One construction for build, test and run. Built at each verb, it was three copies of the same
/// four settings, and a fourth setting added to one of them would have been a verb that watched
/// for less than the others.
/// </remarks>
public static class ContentionRequests
{
    /// <summary>The request for <paramref name="leg"/>, building in <paramref name="buildDirectory"/>.</summary>
    /// <param name="config">The whole configuration.</param>
    /// <param name="leg">The leg being watched.</param>
    /// <param name="buildDirectory">The leg's own build directory.</param>
    /// <param name="treeRoot">The tree the leg builds, which every other leg of that tree on this machine builds in too.</param>
    /// <param name="platformKey">This machine's platform, which decides each other leg's variant.</param>
    /// <param name="beside">
    /// The repository's other trees on this machine, the leg's own left out, as they were listed as its work began there;
    /// <see langword="null"/> where nothing listed them, and only this tree's other legs are owned.
    /// </param>
    public static ContentionRequest For(
        HarnessConfig config,
        string leg,
        string buildDirectory,
        string treeRoot,
        string platformKey,
        RepositoryTreesFound? beside)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        return new ContentionRequest
        {
            Leg = leg,
            BuildDirectory = buildDirectory,
            BuildTools = config.Contention.BuildTools,
            SharedResourceTools = config.Contention.SharedResourceTools,
            SampleSeconds = config.Defaults.ProcessSampleSeconds,
            Owned = Owned(config, leg, treeRoot, platformKey, beside?.Trees ?? []),
            OthersUnlisted = beside?.Unlisted,
        };
    }

    /// <summary>
    /// Every other build directory of the repository this machine could build in, with whose it is: each other declared
    /// leg's under <paramref name="treeRoot"/>, by name, then each leg's - this one's among them - under each of
    /// <paramref name="beside"/>, in turn.
    /// </summary>
    /// <remarks>
    /// What lets a process be said to belong to a sibling. Each leg a host runs is run by a harness process of its own, so a
    /// sibling's compilers are outside this leg's process tree and read, to this leg, exactly as a stranger's would. Legs
    /// of one tree placed on one host build in its one copy there, so every sibling's directory is under the same root as
    /// this leg's; a leg of another tree of the repository builds in that tree's own copy, under its root - measured, a
    /// consumer's agents, each testing its own worktree on the same machines, warned of one another's test processes as
    /// nobody's, on every run. Only legs of this machine's operating system are listed: no other kind ever builds here.
    /// Another tree's directories are reckoned by this tree's configuration, which a tree on another branch may have
    /// changed.
    /// </remarks>
    private static IReadOnlyList<OwnedBuildDirectory> Owned(
        HarnessConfig config,
        string leg,
        string treeRoot,
        string platformKey,
        IReadOnlyList<RepositoryTree> beside)
    {
        var legs = config.Legs
            .Where(pair => string.Equals(pair.Value.Os, platformKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (Name: pair.Key, Variant: VariantKey.For(config, pair.Value, platformKey)))
            .ToList();

        return
        [
            .. legs
                .Where(other => !string.Equals(other.Name, leg, StringComparison.OrdinalIgnoreCase))
                .Select(other => new OwnedBuildDirectory(other.Variant.DirectoryUnder(treeRoot), BuildDirectoryOwner.Sibling(other.Name))),
            .. beside.SelectMany(tree => legs.Select(other => new OwnedBuildDirectory(
                other.Variant.DirectoryUnder(tree.Root),
                BuildDirectoryOwner.InTree(tree.Name, other.Name)))),
        ];
    }
}
