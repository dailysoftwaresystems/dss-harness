using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>What sampling watches for during one leg, read from the configuration every verb reads.</summary>
public sealed class ContentionRequestsTests
{
    private static readonly string Tree = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo"));

    private static readonly string Worktree = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "repo.worktree-o1-xa"));

    /// <summary>
    /// Every build directory of the repository on the leg's machine has an owner: this tree's other legs of that machine's
    /// system first, then each leg of that system in every other tree of the repository there - the leg's own among them,
    /// whose copy in another tree builds in a directory of its own. A leg of another system builds in none of them.
    /// </summary>
    [Fact]
    public void EveryBuildDirectoryOfTheRepositoryHere_HasAnOwner_ThisTreesOtherLegsFirst()
    {
        var config = Config();

        var request = ContentionRequests.For(
            config,
            "mine",
            Directory(config, "mine", Tree),
            Tree,
            "linux",
            new RepositoryTreesFound([new RepositoryTree(Worktree, "worktree o1/xa")]));

        Assert.Equal(
            [
                new OwnedBuildDirectory(Directory(config, "other", Tree), BuildDirectoryOwner.Sibling("other")),
                new OwnedBuildDirectory(Directory(config, "mine", Worktree), BuildDirectoryOwner.InTree("worktree o1/xa", "mine")),
                new OwnedBuildDirectory(Directory(config, "other", Worktree), BuildDirectoryOwner.InTree("worktree o1/xa", "other")),
            ],
            request.Owned);
        Assert.Null(request.OthersUnlisted);
    }

    /// <summary>
    /// Where the repository's other trees could not be listed, only this tree's other legs are owned, and the request says
    /// why the rest are not; where nothing listed them, only this tree's are, as before any tree was listed.
    /// </summary>
    [Fact]
    public void WhereTheOtherTreesCouldNotBeListed_OnlyThisTreesLegsAreOwned_SayingWhy()
    {
        var config = Config();

        var unlisted = ContentionRequests.For(
            config, "mine", Directory(config, "mine", Tree), Tree, "linux", new RepositoryTreesFound([], "git worktree list exited 128"));
        var unasked = ContentionRequests.For(config, "mine", Directory(config, "mine", Tree), Tree, "linux", null);

        Assert.Equal([BuildDirectoryOwner.Sibling("other")], unlisted.Owned.Select(owned => owned.Owner));
        Assert.Equal("git worktree list exited 128", unlisted.OthersUnlisted);
        Assert.Equal([BuildDirectoryOwner.Sibling("other")], unasked.Owned.Select(owned => owned.Owner));
        Assert.Null(unasked.OthersUnlisted);
    }

    /// <summary>A repository declaring two Linux legs, of two configurations, and one Windows leg.</summary>
    private static HarnessConfig Config() => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration(), ["release"] = new BuildConfiguration() },
        Legs =
        {
            ["mine"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" },
            ["other"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "release" },
            ["windows"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug" },
        },
    };

    private static string Directory(HarnessConfig config, string leg, string tree)
        => VariantKey.For(config, config.Legs[leg], "linux").DirectoryUnder(tree);
}
