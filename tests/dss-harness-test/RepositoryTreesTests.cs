using NSubstitute;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>Finding the trees of a repository a machine holds, and what a line calls each.</summary>
public sealed class RepositoryTreesTests
{
    /// <summary>
    /// Where this machine holds the repository, its trees are the main checkout and every worktree git records - a plain
    /// one and an agent's - each spelt where its address puts it under the worktrees root, as a leg naming it spells its
    /// tree, and named by that address: for a command typed here, and for one told it runs here as this machine's own
    /// host, <c>local</c>, which keeps no copy.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HereTheTrees_AreTheMainCheckoutAndEveryWorktreeGitRecords_NamedByTheirAddresses(bool toldLocal)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var context = new HarnessContext(new HarnessLayout(temp.Path, temp.Path), new HarnessConfig());
        var root = context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);

        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.RunGitAsync(temp.Path, ["worktree", "add", "--detach", Path.Combine(root, "plain")], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["worktree", "add", "--detach", Path.Combine(root, "o1", "xa")], cancellationToken);

        var found = await Trees(harness).HereAsync(context, toldLocal ? HostId.Local : null, cancellationToken);

        Assert.Null(found.Unlisted);
        Assert.Equal(new RepositoryTree(temp.Path, RepositoryTree.MainCheckout), found.Trees[0]);
        Assert.Equal(
            [new RepositoryTree(Path.Combine(root, "o1", "xa"), "worktree o1/xa"), new RepositoryTree(Path.Combine(root, "plain"), "worktree plain")],
            found.Trees.Skip(1).OrderBy(tree => tree.Name, StringComparer.Ordinal));
    }

    /// <summary>
    /// An agent's repository, which git records nowhere, is among the trees too, spelt where its address puts it under the
    /// worktrees root and named by that address, as a linked worktree of the same address is: a compiler working in it is
    /// that agent's tree's, never nobody's.
    /// </summary>
    [Fact]
    public async Task RepositoryTrees_NameAnAgentRepository_AsWorktreeOA()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        var main = (await harness.GitClient.GetMainWorktreeAsync(temp.Path, cancellationToken))!.Path;
        var context = new HarnessContext(new HarnessLayout(main, main), new HarnessConfig());
        var root = context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);
        Directory.CreateDirectory(Path.Combine(root, "o1"));

        var made = await new AgentRepositories(harness.GitClient, harness.FileSystem, harness.Platform)
            .MakeAsync(main, WorktreeAddress.Nested("o1", "a"), Path.Combine(root, "o1", "a"), cancellationToken);
        Assert.Null(made.Failure);

        var found = await Trees(harness).HereAsync(context, here: null, cancellationToken);

        Assert.Null(found.Unlisted);
        Assert.Equal(
            [new RepositoryTree(main, RepositoryTree.MainCheckout), new RepositoryTree(Path.Combine(root, "o1", "a"), "worktree o1/a")],
            found.Trees);
    }

    /// <summary>
    /// An agent's repository a deletion takes away while git is asked about it - the engine's own deletion of an agent that
    /// only read runs beside the run's next steps, whose legs name the trees here - is none of the trees, and the rest are
    /// listed whole, never said unlisted: whether git could not be started in it or started and could not read it.
    /// </summary>
    /// <param name="started">Whether git had started in it when it went.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAgentRepositoryTakenAwayAsItIsAsked_IsNoneOfTheTrees_AndTheRestAreListed(bool started)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        var main = (await harness.GitClient.GetMainWorktreeAsync(temp.Path, cancellationToken))!.Path;
        var context = new HarnessContext(new HarnessLayout(main, main), new HarnessConfig());
        var root = context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);
        Directory.CreateDirectory(Path.Combine(root, "o1"));
        var repositories = new AgentRepositories(harness.GitClient, harness.FileSystem, harness.Platform);

        foreach (var agent in new[] { "a", "b" })
        {
            Assert.Null((await repositories.MakeAsync(main, WorktreeAddress.Nested("o1", agent), Path.Combine(root, "o1", agent), cancellationToken)).Failure);
        }

        var taken = Path.Combine(root, "o1", "b");
        var git = InterceptingGitClient.TakingAway(harness.GitClient, harness.FileSystem, taken, "count-objects", started);

        var found = await new RepositoryTrees(git, harness.LocalTransport, harness.FileSystem, harness.Platform).HereAsync(context, here: null, cancellationToken);

        Assert.Null(found.Unlisted);
        Assert.Equal(
            [new RepositoryTree(main, RepositoryTree.MainCheckout), new RepositoryTree(Path.Combine(root, "o1", "a"), "worktree o1/a")],
            found.Trees);
        Assert.False(Directory.Exists(taken));
    }

    /// <summary>
    /// Trees git's record could not be read for are said, with why, rather than taken for none - and the main checkout,
    /// known without asking git anything, is still among them.
    /// </summary>
    [Fact]
    public async Task HereTheTreesGitCouldNotList_AreSaid_WithTheMainCheckoutStillKnown()
    {
        using var temp = new TempDirectory();
        var context = new HarnessContext(new HarnessLayout(temp.Path, temp.Path), new HarnessConfig());

        var found = await Trees(new HarnessFactory()).HereAsync(context, here: null, TestContext.Current.CancellationToken);

        Assert.Equal([new RepositoryTree(temp.Path, RepositoryTree.MainCheckout)], found.Trees);
        Assert.StartsWith("Could not list the worktrees", found.Unlisted, StringComparison.Ordinal);
    }

    /// <summary>
    /// On a host running legs another machine sent it, the trees are its copy of the main checkout, where the configuration
    /// says this host keeps it, and each worktree's copy beside it, spelt as the configuration spells the main copy - and no
    /// directory beside it named otherwise: a mutation worker an earlier version of this tool kept beside a copy, or a name
    /// no worktree's copy is kept under.
    /// </summary>
    [Fact]
    public async Task OnAHostRunningLegsItWasSent_TheTreesAreItsMainCopyAndTheWorktreesCopiesBesideIt()
    {
        using var temp = new TempDirectory();
        var main = temp.Combine("repo");

        foreach (var directory in new[] { main, main + ".worktree-o1--xa", main + ".worktree-plain", main + ".worktree-plain.mutation-ab12cd34s-1", main + ".worktree-Not_A_Copy" })
        {
            Directory.CreateDirectory(directory);
        }

        var found = await Trees(new HarnessFactory()).HereAsync(Copy(main), HostId.Ssh("pi"), TestContext.Current.CancellationToken);

        Assert.Null(found.Unlisted);
        Assert.Equal(
            [
                new RepositoryTree(main, RepositoryTree.MainCheckout),
                new RepositoryTree(main + ".worktree-o1--xa", "worktree o1/xa"),
                new RepositoryTree(main + ".worktree-plain", "worktree plain"),
            ],
            found.Trees);
    }

    /// <summary>
    /// On a host running legs another machine sent it, the main copy is the one the copy the legs run in is kept beside -
    /// that copy itself where it is the main checkout's, and the copy holding a leg's own copy in its <c>.legs</c> - never
    /// where config.json's repositoryPath says: the machine that sent the legs may keep its copies where its own item's
    /// .env says, which no host's copy holds an item to read.
    /// </summary>
    [Theory]
    [InlineData("repo", "repo")]
    [InlineData("repo.worktree-o1--xa", "repo")]
    [InlineData("repo.worktree-o1--xa/.legs/357e24c", "repo")]
    [InlineData("repo/.legs/357e24c", "repo")]
    public async Task OnAHostRunningLegsItWasSent_TheMainCopyIsTheOneItsCopyIsKeptBeside_NeverConfigJsons(string copy, string main)
    {
        using var temp = new TempDirectory();
        var tree = temp.Combine(copy.Split('/'));
        var context = new HarnessContext(
            new HarnessLayout(tree, tree),
            new HarnessConfig { Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/src/elsewhere" } } } });

        Directory.CreateDirectory(tree);

        var found = await Trees(new HarnessFactory()).HereAsync(context, HostId.Ssh("pi"), TestContext.Current.CancellationToken);

        Assert.Equal(temp.Combine(main), RepositoryTree.MainCheckoutOf(context, HostId.Ssh("pi"), new DssHarness.Core.Platform.HostPlatform().PathComparison));
        Assert.Equal(new RepositoryTree(temp.Combine(main), RepositoryTree.MainCheckout), found.Trees[0]);
    }

    /// <summary>A host whose copies could not be looked for says why, with its main copy still known.</summary>
    [Fact]
    public async Task OnAHostWhoseCopiesCouldNotBeLookedFor_TheyAreSaid_WithItsMainCopyStillKnown()
    {
        var harness = new HarnessFactory();
        var files = Substitute.For<IFileSystem>();
        files.DirectoryExists(Arg.Any<string>()).Returns(true);
        files.EnumerateDirectories(Arg.Any<string>()).Returns(_ => throw new IOException("The device is not ready."));

        var found = await new RepositoryTrees(harness.GitClient, SyncKit.Transport(harness, files), files, harness.Platform)
            .HereAsync(Copy("/srv/repo"), HostId.Ssh("pi"), TestContext.Current.CancellationToken);

        Assert.Equal([new RepositoryTree("/srv/repo", RepositoryTree.MainCheckout)], found.Trees);
        Assert.Equal("The device is not ready", found.Unlisted);
    }

    /// <summary>
    /// The trees this machine knows another host to keep are its copy of the main checkout, where the configuration says,
    /// and each worktree's copy this machine's record holds on that host - none it holds on another.
    /// </summary>
    [Fact]
    public void OnAnotherHost_TheTreesAreItsMainCopyAndTheCopiesTheRecordHoldsThere()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var context = new HarnessContext(
            new HarnessLayout(temp.Path, temp.Path),
            new HarnessConfig
            {
                Hosts = new HostsConfig
                {
                    Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/src/repo" } },
                    Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "~/src/repo" } },
                },
            });
        var record = new HostCopyRecord(harness.FileSystem, harness.Platform.PathComparison);

        Assert.Null(record.Claim(context.Layout, new HostCopyEntry("o1--xa", "ssh pi", "~/src/repo.worktree-o1--xa", temp.Combine("xa"))));
        Assert.Null(record.Claim(context.Layout, new HostCopyEntry("o1--xa", "wsl Ubuntu", "~/src/repo.worktree-o1--xa", temp.Combine("xa"))));
        Assert.Null(record.Claim(context.Layout, new HostCopyEntry("plain", "ssh pi", "~/src/repo.worktree-plain", temp.Combine("plain"))));

        var found = Trees(harness).On(context, HostId.Ssh("pi"));

        Assert.Null(found.Unlisted);
        Assert.Equal(
            [
                new RepositoryTree("~/src/repo", RepositoryTree.MainCheckout),
                new RepositoryTree("~/src/repo.worktree-o1--xa", "worktree o1/xa"),
                new RepositoryTree("~/src/repo.worktree-plain", "worktree plain"),
            ],
            found.Trees);
    }

    /// <summary>A record of host copies that cannot be read is said, with why, and the host's main copy is still known.</summary>
    [Fact]
    public void OnAnotherHostWhoseRecordCannotBeRead_ItIsSaid_WithTheMainCopyStillKnown()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var context = new HarnessContext(
            new HarnessLayout(temp.Path, temp.Path),
            new HarnessConfig { Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/src/repo" } } } });

        Directory.CreateDirectory(context.Layout.HostCopiesDirectory);
        File.WriteAllText(HostCopyRecord.PathOf(context.Layout), "{ not json");

        var found = Trees(harness).On(context, HostId.Ssh("pi"));

        Assert.Equal([new RepositoryTree("~/src/repo", RepositoryTree.MainCheckout)], found.Trees);
        Assert.Contains("records which hosts hold a copy of which worktree, and it cannot be read", found.Unlisted, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host this machine knows nowhere to keep its copy for - none declared, or an item that cannot be read - holds no
    /// tree of the repository this machine could have sent it, since a sync keeps its copy there: said as a listing that
    /// could not be made, with why, and never thrown, so no leg's work stops for it.
    /// </summary>
    [Fact]
    public void AHostKnownToKeepItsCopyNowhere_HoldsNoTree_SaidWithWhy_NeverThrown()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var undeclared = new HarnessContext(layout, new HarnessConfig());
        var unread = new HarnessContext(
            layout,
            new HarnessConfig { Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/src/repo" } } } })
        {
            HostItems = Items(new ItemFacts { Problem = "other users can change '.harness-config/sshItems/pi/.env'" }),
        };

        var nowhere = Trees(harness).On(undeclared, HostId.Ssh("pi"));
        var unknown = Trees(harness).On(unread, HostId.Ssh("pi"));

        Assert.Empty(nowhere.Trees);
        Assert.Equal("Host ssh pi declares no repositoryPath, so there is nowhere to keep its copy", nowhere.Unlisted);
        Assert.Empty(unknown.Trees);
        Assert.Equal(
            "Where host ssh pi keeps its copies for this machine is not known, since its item cannot be read: "
                + "other users can change '.harness-config/sshItems/pi/.env'",
            unknown.Unlisted);
    }

    /// <summary>
    /// The trees this machine knows a host to keep start where the host's item sets REPOSITORY_PATH, which wins over
    /// config.json's repositoryPath: the copies are this machine's, kept where its own item says.
    /// </summary>
    [Fact]
    public void OnAHostWhoseItemSetsWhereItKeepsItsCopies_TheMainCopyIsThere()
    {
        using var temp = new TempDirectory();
        var context = new HarnessContext(
            new HarnessLayout(temp.Path, temp.Path),
            new HarnessConfig { Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/src/repo" } } } })
        {
            HostItems = Items(new ItemFacts { RepositoryPath = "/srv/mine/repo" }),
        };

        var found = Trees(new HarnessFactory()).On(context, HostId.Ssh("pi"));

        Assert.Equal([new RepositoryTree("/srv/mine/repo", RepositoryTree.MainCheckout)], found.Trees);
    }

    /// <summary>
    /// Each tree this machine holds is followed by the copies of it its legs work in, from the tree's own slot for them,
    /// named as the tree's leg copies: a compiler working in one is named as that tree's leg copy's, never as nobody's -
    /// and a first build in a copy is measured by what the other trees' builds of the variant came to, as any tree's.
    /// </summary>
    [Fact]
    public async Task EachTreeHoldsIsFollowedByTheLegCopiesKeptInItsLegs_NamedAsIts()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var context = new HarnessContext(new HarnessLayout(temp.Path, temp.Path), new HarnessConfig());
        var root = context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);
        var plain = Path.Combine(root, "plain");

        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.RunGitAsync(temp.Path, ["worktree", "add", "--detach", plain], cancellationToken);

        foreach (var (tree, name) in new[] { (temp.Path, "357e24c"), (plain, "357e24d") })
        {
            var copy = HarnessLayout.InSlot(tree, HarnessLayout.LegsDirectoryName, name);

            Directory.CreateDirectory(copy);
            File.WriteAllText(Path.Combine(copy, "main.c"), "int main;");
        }

        var found = await Trees(harness).HereAsync(context, here: null, cancellationToken);

        Assert.Null(found.Unlisted);
        Assert.Equal(
            [
                new RepositoryTree(temp.Path, RepositoryTree.MainCheckout),
                new RepositoryTree(HarnessLayout.InSlot(temp.Path, HarnessLayout.LegsDirectoryName, "357e24c"), "the main checkout's leg copy") { LegCopy = true },
                new RepositoryTree(plain, "worktree plain"),
                new RepositoryTree(HarnessLayout.InSlot(plain, HarnessLayout.LegsDirectoryName, "357e24d"), "worktree plain's leg copy") { LegCopy = true },
            ],
            found.Trees);
    }

    private static RepositoryTrees Trees(HarnessFactory harness)
        => new(harness.GitClient, harness.LocalTransport, harness.FileSystem, harness.Platform);

    /// <summary>Hosts' items each declaring <paramref name="facts"/>.</summary>
    private static HostItems Items(ItemFacts facts)
    {
        var secrets = Substitute.For<IHostSecretsStore>();
        secrets.ReadFacts(Arg.Any<HarnessLayout>(), Arg.Any<HostId>()).Returns(facts);

        return HostItems.Reading(secrets, new HarnessLayout("/repo", "/repo"));
    }

    /// <summary>A host's copy of the main checkout at <paramref name="main"/>, as a host running legs it was sent loads it.</summary>
    private static HarnessContext Copy(string main)
        => new(
            new HarnessLayout(main, main),
            new HarnessConfig { Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = main } } } });
}
