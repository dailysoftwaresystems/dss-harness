using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using NSubstitute;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

public sealed class RepositoryLocatorTests
{
    private static readonly string Main = Path.Combine(TestHost.TemporaryRoot, "locator", "repo");

    private static readonly string Worktree = Path.Combine(Main, ".harness-config", "worktrees", "wt");

    private readonly IGitClient _git = Substitute.For<IGitClient>();

    [Fact]
    public async Task LocateAsync_ReturnsNull_OutsideARepository()
    {
        _git.GetRepositoryRootAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        Assert.Null(await new RepositoryLocator(_git, Substitute.For<IFileSystem>(), new HostPlatform()).LocateAsync(Main, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LocateAsync_PairsTheTreeWithItsMainCheckout()
    {
        _git.GetRepositoryRootAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(Worktree));
        _git.GetMainWorktreeAsync(Worktree, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GitWorktree?>(new GitWorktree(Main, "abc", "main", IsMain: true, IsBare: false)));

        var layout = await new RepositoryLocator(_git, Substitute.For<IFileSystem>(), new HostPlatform()).LocateAsync(Worktree, TestContext.Current.CancellationToken);

        Assert.NotNull(layout);
        Assert.Equal(Worktree, layout.RepositoryRoot);
        Assert.Equal(Main, layout.MainCheckoutRoot);
    }

    [Fact]
    public async Task LocateAsync_NeverFallsBackToTheTree_WhenTheMainCheckoutIsUnknown()
    {
        // Inside a worktree that guess is wrong, and worktrees, the run lock and ssh
        // secrets would all resolve inside the worktree.
        _git.GetRepositoryRootAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(Worktree));
        _git.GetMainWorktreeAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<GitWorktree?>(null));

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            new RepositoryLocator(_git, Substitute.For<IFileSystem>(), new HostPlatform()).LocateAsync(Worktree, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, exception.ExitCode);
    }

    [Fact]
    public async Task LocateAsync_RefusesAWorktreeOfABareRepository()
    {
        // A bare repository has no checkout to keep harness state in.
        _git.GetRepositoryRootAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(Worktree));
        _git.GetMainWorktreeAsync(Worktree, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GitWorktree?>(new GitWorktree(Main, null, null, IsMain: true, IsBare: true)));

        var exception = await Assert.ThrowsAsync<HarnessException>(() =>
            new RepositoryLocator(_git, Substitute.For<IFileSystem>(), new HostPlatform()).LocateAsync(Worktree, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("bare", exception.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The mark of an agent's repository is read from its own configuration file by git, and git is asked only where that file
/// may hold the mark's section: every location of a main checkout asks it, so a file whose values merely spell the
/// section's name - a user's address, a remote's name, a path naming the tool - is never a program started for nothing.
/// </summary>
public sealed class AgentRepositoryMarkTests
{
    private readonly IGitClient _git = Substitute.For<IGitClient>();

    /// <summary>A repository whose own configuration file holds <paramref name="configuration"/>, under a directory of its own.</summary>
    private static string RepositoryHolding(TempDirectory temp, string configuration)
    {
        Directory.CreateDirectory(Path.Combine(temp.Path, ".git"));
        File.WriteAllText(Path.Combine(temp.Path, ".git", "config"), configuration);

        return temp.Path;
    }

    private static PhysicalFileSystem Files() => new(FilePermissionsFactory.Create());

    /// <summary>git asked for the mark's section, answering as it answers <paramref name="output"/> with <paramref name="exitCode"/>.</summary>
    private void Answers(int exitCode, string output)
        => _git.RunAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(arguments => arguments.Contains("--get-regexp")), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GitCommandResult(exitCode, output, string.Empty)));

    [Theory]
    [InlineData("[user]\n\temail = harness@test.invalid\n\tname = Harness Test\n")]
    [InlineData("[remote \"origin\"]\n\turl = https://example.invalid/acme/dss-harness.git\n")]
    [InlineData("[core]\n\texcludesFile = /work/.harness-config/excludes\n# [harnessed] is no section of the mark's\n[harnesses]\n\tkey = value\n")]
    public async Task AConfigurationWhoseValuesOnlySpellTheSectionsName_HoldsNoMark_AndGitIsNeverAsked(string configuration)
    {
        using var temp = new TempDirectory();
        var repository = RepositoryHolding(temp, configuration);
        Answers(1, string.Empty);

        var mark = await AgentRepositoryMark.ReadAsync(_git, Files(), repository, TestContext.Current.CancellationToken);

        Assert.Null(mark);
        await _git.DidNotReceive().RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("[harness]\n\tmainCheckout = \"/work/main\"\n\tagent = \"o1/a1\"\n")]
    [InlineData("[core]\n\tbare = false\n  [HARNESS]\n\tmainCheckout = \"/work/main\"\n\tagent = \"o1/a1\"\n")]
    [InlineData("[core] [Harness]\n\tmainCheckout = \"/work/main\"\n\tagent = \"o1/a1\"\n")]
    public async Task AConfigurationThatMayOpenTheSection_InAnyCaseAndWhereverAHeaderStands_IsReadByGit(string configuration)
    {
        using var temp = new TempDirectory();
        var repository = RepositoryHolding(temp, configuration);
        Answers(0, $"{AgentRepositoryMark.MainCheckoutKey}\n/work/main\0{AgentRepositoryMark.AgentKey}\no1/a1\0");

        var mark = await AgentRepositoryMark.ReadAsync(_git, Files(), repository, TestContext.Current.CancellationToken);

        Assert.Equal(new AgentRepositoryMark("/work/main", "o1/a1"), mark);
        await _git.Received(1).RunAsync(repository, Arg.Is<IReadOnlyList<string>>(arguments => arguments.Contains("--get-regexp")), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("[harness]", true)]
    [InlineData("[harness \"sub\"]", true)]
    [InlineData("[harness.sub]", true)]
    [InlineData("[HarNess]", true)]
    [InlineData("x = [harness", true)]
    [InlineData("[harness!", true)]
    [InlineData("[harnesses]", false)]
    [InlineData("[harness-x]", false)]
    [InlineData("[harness2]", false)]
    [InlineData("harness = 1", false)]
    [InlineData("[ harness]", false)]
    [InlineData("", false)]
    public void AHeaderMayOpenTheSection_OnlyWhereTheBracketIsFollowedAtOnceByItsName_AndByNoCharacterANameGoesOnWith(string text, bool may)
        => Assert.Equal(may, GitConfigText.MayOpenSection(text, AgentRepositoryMark.SectionName));
}

public sealed class HarnessContextLoaderTests
{
    private static readonly string Main = Path.Combine(TestHost.TemporaryRoot, "loader", "repo");

    private static readonly string Worktree = Path.Combine(Main, ".harness-config", "worktrees", "wt");

    private readonly IRepositoryLocator _locator = Substitute.For<IRepositoryLocator>();
    private readonly IConfigStore _configStore = Substitute.For<IConfigStore>();
    private readonly IGitClient _git = Substitute.For<IGitClient>();
    private readonly IFileSystem _fileSystem = Substitute.For<IFileSystem>();

    public HarnessContextLoaderTests()
    {
        _git.IsInstalled().Returns(true);
    }

    [Fact]
    public async Task LoadAsync_ReportsMissingGit_AsAMissingTool()
    {
        _git.IsInstalled().Returns(false);

        var exception = await Assert.ThrowsAsync<HarnessException>(() => Load(Main));

        Assert.Equal(HarnessExit.ToolMissing, exception.ExitCode);
        _ = _locator.DidNotReceiveWithAnyArgs().LocateAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LoadAsync_RefusesADirectoryOutsideARepository()
    {
        _locator.LocateAsync(Main, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(null));

        var exception = await Assert.ThrowsAsync<HarnessException>(() => Load(Main));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
    }

    [Fact]
    public async Task LoadAsync_ReportsNotInitialised_WhenTheMainCheckoutHasNoConfiguration()
    {
        _locator.LocateAsync(Main, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<HarnessLayout?>(new HarnessLayout(Main, Main)));
        _fileSystem.FileExists(Arg.Any<string>()).Returns(false);

        var exception = await Assert.ThrowsAsync<HarnessException>(() => Load(Main));

        Assert.Equal(HarnessExit.NotInitialized, exception.ExitCode);
        _configStore.DidNotReceiveWithAnyArgs().Load(default!);
    }

    /// <summary>
    /// config.json is tracked, so a worktree has its own and a branch may legitimately change it.
    /// Read from the main checkout — which is what this did — a worktree could not run anything it had
    /// just written, and nothing said so. Measured by a consumer: a worktree's config replaced with
    /// text that is not JSON left the survey answering normally, from inside the worktree and with
    /// an explicit --directory alike.
    /// </summary>
    [Fact]
    public async Task LoadAsync_ReadsTheWorktreesOwnConfiguration_NotTheMainCheckouts()
    {
        var layout = new HarnessLayout(Worktree, Main);
        var worktreeConfig = Path.Combine(Worktree, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        var mainConfig = Path.Combine(Main, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        var config = new HarnessConfig();

        _locator.LocateAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(layout));
        _fileSystem.FileExists(worktreeConfig).Returns(true);
        _fileSystem.FileExists(mainConfig).Returns(true);
        _configStore.Load(worktreeConfig).Returns(config);

        var context = await Load(Worktree);

        Assert.Same(layout, context.Layout);
        Assert.Same(config, context.Config);
        _configStore.Received(1).Load(worktreeConfig);
        _configStore.DidNotReceive().Load(mainConfig);
    }

    /// <summary>
    /// A worktree of a branch that predates the harness has no tracked copy to read. The main
    /// checkout's is then the only configuration there is, and using it without a word would be the
    /// same silence the rule above exists to end.
    /// </summary>
    [Fact]
    public async Task AWorktreeWithNoConfigurationOfItsOwn_FallsBackToTheMainCheckout_AndSaysSo()
    {
        var layout = new HarnessLayout(Worktree, Main);
        var worktreeConfig = Path.Combine(Worktree, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        var mainConfig = Path.Combine(Main, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        var config = new HarnessConfig();

        _locator.LocateAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(layout));
        _fileSystem.FileExists(worktreeConfig).Returns(false);
        _fileSystem.FileExists(mainConfig).Returns(true);
        _configStore.Load(mainConfig).Returns(config);

        var context = await Load(Worktree);

        Assert.Same(config, context.Config);
        _configStore.Received(1).Load(mainConfig);
        Assert.Contains(
            "no '.harness-config/config.json'",
            _errors.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The repository a tree is of is the same whether a command is typed in it or a run works in it: a tree git holds,
    /// and a copy made for a run, name the main checkout; a copy a sync made, a repository of its own to git, is told by
    /// where it is kept - in a tree's .legs, of that tree's repository, a host's worktree copy, of the main checkout's copy
    /// beside it, anything else, its own.
    /// </summary>
    [Fact]
    public async Task TheRepositoryATreeIsOf_IsTheSame_TypedInItOrWorkedInByARun()
    {
        var config = new HarnessConfig();
        var hostMain = Path.Combine(TestHost.TemporaryRoot, "loader", "host", "repo");
        var hostWorktree = hostMain + ".worktree-o1--xa";
        var localLegCopy = Path.Combine(Worktree, HarnessLayout.LegsDirectoryName, "abc1234");
        var hostLegCopy = Path.Combine(hostWorktree, HarnessLayout.LegsDirectoryName, "abc1234");

        _locator.LocateAsync(Worktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(new HarnessLayout(Worktree, Main)));
        _locator.LocateAsync(hostWorktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(new HarnessLayout(hostWorktree, hostWorktree)));
        _fileSystem.FileExists(new HarnessLayout(hostWorktree, hostWorktree).SyncedCopyMarkerFile).Returns(true);

        Assert.Equal(Main, await RepositoryOf(new HarnessContext(new HarnessLayout(Worktree, Main), config)));
        Assert.Equal(Main, await RepositoryOf(new HarnessContext(new HarnessLayout(localLegCopy, Main), config) { IsSyncedCopy = true }));
        Assert.Equal(Main, await RepositoryOf(new HarnessContext(new HarnessLayout(localLegCopy, localLegCopy), config) { IsSyncedCopy = true }));
        Assert.Equal(hostMain, await RepositoryOf(new HarnessContext(new HarnessLayout(hostMain, hostMain), config) { IsSyncedCopy = true }));
        Assert.Equal(hostMain, await RepositoryOf(new HarnessContext(new HarnessLayout(hostWorktree, hostWorktree), config) { IsSyncedCopy = true }));
        Assert.Equal(hostMain, await RepositoryOf(new HarnessContext(new HarnessLayout(hostLegCopy, hostLegCopy), config) { IsSyncedCopy = true }));
    }

    /// <summary>
    /// The repository a host's tree is of is the main copy the host finds beside it, by the one rule both answers are
    /// given by (<see cref="DssHarness.Core.Sync.HostCopies.MainCopyOfTree"/>), compared as the machine compares paths: a
    /// leg's copy in a '.LEGS' its holder's on a machine that folds case, and a copy of its own on one that does not -
    /// the set-ups a machine shares are shared by the repository this answers, and a host's legs are measured beside the
    /// main copy the other answers, so the two never disagree.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheRepositoryOfAHostsTree_IsTheMainCopyTheHostFindsBesideIt_ComparedAsTheMachineComparesPaths(bool foldsCase)
    {
        var platform = HostDoubles.Platform(foldsCase ? PlatformId.Windows : PlatformId.Linux);
        var comparison = foldsCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var hostMain = Path.Combine(TestHost.TemporaryRoot, "loader", "host", "repo");
        var hostWorktree = hostMain + ".worktree-o1--xa";
        var held = new HarnessLayout(hostWorktree, hostWorktree);

        _locator.LocateAsync(hostWorktree, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(held));
        _fileSystem.FileExists(held.SyncedCopyMarkerFile).Returns(true);

        foreach (var tree in new[] { hostMain, hostWorktree, Path.Combine(hostWorktree, HarnessLayout.LegsDirectoryName, "abc1234"), Path.Combine(hostWorktree, ".LEGS", "abc1234") })
        {
            var context = new HarnessContext(new HarnessLayout(tree, tree), new HarnessConfig()) { IsSyncedCopy = true };
            var expected = tree.Contains(".LEGS", StringComparison.Ordinal) && !foldsCase ? tree : hostMain;

            Assert.Equal(expected, await Loader(platform).RepositoryOfAsync(context, TestContext.Current.CancellationToken));
            Assert.Equal(expected, DssHarness.Core.Worktrees.RepositoryTree.MainCheckoutOf(context, DssHarness.Core.Hosts.HostId.Ssh("pi"), comparison));
        }
    }

    /// <summary>
    /// What the hosts' items declare is read from this machine's own items for a command typed here, and taken from what
    /// the machine that sent the legs told for one it asked a host to run: a host's copy holds no item, so read there,
    /// every WSL item's distribution was not known and a leg's run line naming one was refused as an item not read.
    /// </summary>
    [Fact]
    public async Task TheHostsItems_AreReadHereForATypedCommand_AndToldForOneAnotherMachineAsked()
    {
        var layout = new HarnessLayout(Main, Main);
        var configFile = Path.Combine(Main, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        var ubuntu = DssHarness.Core.Hosts.HostId.Wsl("ubuntu");
        var secrets = Substitute.For<IHostSecretsStore>();

        _locator.LocateAsync(Main, Arg.Any<CancellationToken>()).Returns(Task.FromResult<HarnessLayout?>(layout));
        _fileSystem.FileExists(configFile).Returns(true);
        _configStore.Load(configFile).Returns(new HarnessConfig());
        secrets.ReadFacts(Arg.Any<HarnessLayout>(), ubuntu).Returns(new ItemFacts { File = ".harness-config/wslDistros/ubuntu/.env", Present = true, Distribution = "Read-Here" });

        var told = DssHarness.Core.Hosts.Dispatch.Of(new DssHarness.Core.Hosts.HostAgentRequest
        {
            Kind = DssHarness.Core.Hosts.HostAgentRequestKind.Run,
            Distributions = { ["Ubuntu"] = "Told-There" },
        });

        var served = await Loader(secrets: secrets, origin: CommandOrigin.Served(told)).LoadAsync(Main, TestContext.Current.CancellationToken);

        Assert.Equal("Told-There", served.HostItems.Of(ubuntu).Distribution);
        secrets.DidNotReceiveWithAnyArgs().ReadFacts(default!, default!);

        var typed = await Loader(secrets: secrets).LoadAsync(Main, TestContext.Current.CancellationToken);

        Assert.Equal("Read-Here", typed.HostItems.Of(ubuntu).Distribution);
        secrets.Received(1).ReadFacts(Arg.Any<HarnessLayout>(), ubuntu);
    }

    private readonly StringWriter _errors = new();

    private Task<HarnessContext> Load(string directory) => Loader().LoadAsync(directory, TestContext.Current.CancellationToken);

    private Task<string> RepositoryOf(HarnessContext context) => Loader().RepositoryOfAsync(context, TestContext.Current.CancellationToken);

    /// <summary>
    /// The loader over this test's locator, configuration and files, on <paramref name="platform"/> - this machine, where
    /// none is given - reading the hosts' items from <paramref name="secrets"/>, for a command of <paramref name="origin"/>.
    /// </summary>
    private HarnessContextLoader Loader(IHostPlatform? platform = null, IHostSecretsStore? secrets = null, CommandOrigin? origin = null)
    {
        var output = new ConsoleHarnessOutput(new StringWriter(), _errors, verbose: false);

        return new HarnessContextLoader(
            _locator,
            _configStore,
            _git,
            _fileSystem,
            platform ?? new HostPlatform(),
            output,
            new SyncedCopyToolCheck(new PublishedVersionsDouble(), new RunningToolDouble(), origin ?? CommandOrigin.Typed, output),
            secrets ?? Substitute.For<IHostSecretsStore>(),
            origin ?? CommandOrigin.Typed);
    }
}
