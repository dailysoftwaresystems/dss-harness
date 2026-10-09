using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>Finding the trees of a repository a machine holds, and what a line calls each.</summary>
public sealed class RepositoryTreesTests
{
    /// <summary>
    /// Where this machine holds the repository, its trees are the main checkout and every worktree git records - a plain
    /// one and an agent's - each spelt where its address puts it under the worktrees root, as a leg naming it spells its
    /// tree, and named by that address.
    /// </summary>
    [Fact]
    public async Task HereTheTrees_AreTheMainCheckoutAndEveryWorktreeGitRecords_NamedByTheirAddresses()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var context = new HarnessContext(new HarnessLayout(temp.Path, temp.Path), new HarnessConfig());
        var root = context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);

        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.RunGitAsync(temp.Path, ["worktree", "add", "--detach", Path.Combine(root, "plain")], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["worktree", "add", "--detach", Path.Combine(root, "o1", "xa")], cancellationToken);

        var found = await Trees(harness).HereAsync(context, here: null, cancellationToken);

        Assert.Null(found.Unlisted);
        Assert.Equal(new RepositoryTree(temp.Path, RepositoryTree.MainCheckout), found.Trees[0]);
        Assert.Equal(
            [new RepositoryTree(Path.Combine(root, "o1", "xa"), "worktree o1/xa"), new RepositoryTree(Path.Combine(root, "plain"), "worktree plain")],
            found.Trees.Skip(1).OrderBy(tree => tree.Name, StringComparer.Ordinal));
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
    /// directory beside it named otherwise: a mutation worker kept beside a copy, or a name no worktree's copy is kept under.
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
    /// On a host keeping its main copy under its home - <c>~/src/repo</c>, as a configuration shared by every host says it -
    /// the trees are spelt with that home, where this process is: what is read of a tree there, and the paths its processes
    /// name, are its home's, never a directory called <c>~</c> below wherever the process started.
    /// </summary>
    [Fact]
    public async Task OnAHostKeepingItsMainCopyUnderItsHome_TheTreesAreSpeltWithThatHome()
    {
        var context = new HarnessContext(
            new HarnessLayout("/repo", "/repo"),
            new HarnessConfig { Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/src/repo" } } } });

        var found = await Trees(new HarnessFactory()).HereAsync(context, HostId.Ssh("pi"), TestContext.Current.CancellationToken);

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "src/repo"),
            found.Trees[0].Root);
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

    private static RepositoryTrees Trees(HarnessFactory harness)
        => new(harness.GitClient, harness.LocalTransport, harness.FileSystem, harness.Platform);

    /// <summary>A host's copy of the main checkout at <paramref name="main"/>, as a host running legs it was sent loads it.</summary>
    private static HarnessContext Copy(string main)
        => new(
            new HarnessLayout(main, main),
            new HarnessConfig { Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = main } } } });
}
