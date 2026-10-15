using NSubstitute;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;

namespace RepoHarness.Tests;

/// <summary>Where a placed leg's tree is, here and in a host's copy of it.</summary>
public sealed class LegRunPlanTests
{
    private static readonly HostId Pi = HostId.Ssh("pi");

    private static readonly HarnessConfig Config = new()
    {
        SshItems = { "pi" },
        Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } } },
    };

    /// <summary>
    /// A leg on a host works in the host's copy of its own tree: the main checkout's at the host's repositoryPath,
    /// and a worktree's - the one the command runs in, or the one the leg names - in the worktree's own copy beside
    /// it, so that trees do not share a copy, or the lock on it.
    /// </summary>
    [Theory]
    [InlineData(false, null, "/home/pi/repo")]
    [InlineData(false, "feature", "/home/pi/repo.worktree-feature")]
    [InlineData(true, null, "/home/pi/repo.worktree-feature")]
    public void ALegOnAHost_WorksInTheHostsCopyOfItsOwnTree(bool inWorktree, string? namedWorktree, string expected)
    {
        using var temp = new TempDirectory();
        var worktree = temp.Combine(".harness-config", "worktrees", "feature");
        var layout = new HarnessLayout(inWorktree ? worktree : temp.Path, temp.Path);

        var leg = Assert.Single(Place(layout, Pi, here: null, namedWorktree));

        Assert.Equal(expected, leg.HostTreeRoot);
        PathAssert.Same(inWorktree || namedWorktree is not null ? worktree : temp.Path, leg.TreeRoot);
    }

    /// <summary>
    /// On the host a leg was sent to, its tree is the copy it was sent to, which is its worktree's own: the worktree
    /// the leg names is on the machine that sent it, and nothing in the copy is at that path.
    /// </summary>
    [Fact]
    public void ALegSentToAHost_RunsInTheCopyItWasSentTo_WhateverWorktreeItNames()
    {
        using var copy = new TempDirectory();
        var layout = new HarnessLayout(copy.Path, copy.Path);

        var leg = Assert.Single(Place(layout, HostId.Local, here: Pi, namedWorktree: "feature"));

        PathAssert.Same(copy.Path, leg.TreeRoot);
        PathAssert.Same(copy.Path, leg.HostTreeRoot);
    }

    /// <summary>
    /// What a placed leg is, told apart: whether another machine's process runs its work, whether this machine makes the
    /// tree it works in by a sync, and whether that tree is a copy a sync made. A leg this machine runs in its own tree
    /// is none of the three, and one on a host all three. A leg run on the host it was sent to is in a copy, which the
    /// machine that sent it made and dispatched it to: the host itself syncs nothing and dispatches it nowhere.
    /// </summary>
    [Fact]
    public void APlacedLeg_SaysWhoRunsItsWork_WhoMakesItsTree_AndWhetherThatTreeIsACopy()
    {
        using var temp = new TempDirectory();
        var layout = new HarnessLayout(temp.Path, temp.Path);

        var here = Assert.Single(Place(layout, HostId.Local, here: null, namedWorktree: null));
        var onAHost = Assert.Single(Place(layout, Pi, here: null, namedWorktree: null));
        var sentHere = Assert.Single(Place(layout, HostId.Local, here: Pi, namedWorktree: null));

        Assert.Equal((false, false, false, false), (here.LocalCopy, here.Dispatched, here.Synced, here.InCopy));
        Assert.Equal((false, true, true, true), (onAHost.LocalCopy, onAHost.Dispatched, onAHost.Synced, onAHost.InCopy));
        Assert.Equal((false, false, false, true), (sentHere.LocalCopy, sentHere.Dispatched, sentHere.Synced, sentHere.InCopy));
    }

    /// <summary>
    /// A leg that works in a copy of its tree kept in the tree's own .legs on this machine is synced and in a copy, and is
    /// dispatched nowhere: this machine makes its tree, and this machine's own process runs it. The executor is given
    /// its tree's key and its tree's name, as it is a host leg's, and neither for a leg that works in its own tree,
    /// which no sync makes.
    /// </summary>
    [Fact]
    public void ALegInACopyOfItsTreeOnThisMachine_IsSyncedAndInACopy_AndDispatchedNowhere()
    {
        using var temp = new TempDirectory();
        var here = Assert.Single(Place(new HarnessLayout(temp.Path, temp.Path), HostId.Local, here: null, namedWorktree: null));
        var copy = here with { LocalCopy = true, HostTreeRoot = temp.Path + ".leg-0000000" };

        Assert.Equal((false, true, true), (copy.Dispatched, copy.Synced, copy.InCopy));

        Assert.Equal((copy.TreeKey, $"'{copy.HostTreeRoot}' on local"), (copy.ToPlan().TreeKey, copy.ToPlan().Tree));
        Assert.Equal((string.Empty, string.Empty), (here.ToPlan().TreeKey, here.ToPlan().Tree));
    }

    /// <summary>
    /// The build a leg runs is of the tree its work happens in, never of the tree the command reads here where the two
    /// are not one: built from the tree here, a leg that works in a copy would build the tree its copy exists to leave
    /// alone.
    /// </summary>
    [Fact]
    public void ALegsBuild_IsOfTheTreeItsWorkHappensIn()
    {
        using var temp = new TempDirectory();
        var here = Assert.Single(Place(new HarnessLayout(temp.Path, temp.Path), HostId.Local, here: null, namedWorktree: null));
        var copy = here with { HostTreeRoot = temp.Path + ".leg-0000000" };
        var project = new ProjectConfig { Name = "app", Type = "cmake" };

        Assert.Equal(copy.HostTreeRoot, copy.BuildRequestFor(Config, temp.Combine("runs", "r1"), project).TreeRoot);
        PathAssert.Same(temp.Path, here.BuildRequestFor(Config, temp.Combine("runs", "r1"), project).TreeRoot);
    }

    /// <summary>
    /// Two legs that would each put a different tree in one copy on one host are refused before either starts, naming
    /// both and the copy: two trees whose directories' names are spelt as one name are kept under that name there,
    /// and whichever leg ran second would measure the tree the other put there.
    /// </summary>
    [Fact]
    public void TwoLegsThatWouldPutDifferentTreesInOneCopyOnAHost_AreRefusedBeforeEitherStarts()
    {
        using var temp = new TempDirectory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var report = new HostReport { Host = Pi, Os = "linux", Processor = "x86_64" };

        LegPlacement Placed(string name, string worktree, string config)
            => new(new SelectedLeg(name, new LegConfig { Os = "linux", Processor = "x86_64", Config = config, Worktree = worktree }), report, null);

        var refusal = Assert.Throws<HarnessException>(() => LegRunPlan.From(
            new HarnessContext(layout, Config),
            new LegsReport([Placed("first", "feature-1", "debug"), Placed("second", "feature_1", "release")], [report], Named: false),
            HostDoubles.Platform(PlatformId.Linux),
            out _));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.StartsWith(
            "Selected legs disagree about what a host's copy should hold: 'first' and 'second' would each put a different tree in "
            + "'/home/pi/repo.worktree-feature-1' on ssh pi: ",
            refusal.Message,
            StringComparison.Ordinal);
        Assert.EndsWith("Nothing was run. Rename one of the worktrees, or run them separately.", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg never works in a copy of a copy: where the command was typed in a synced copy - a host's copy, served on
    /// the host itself - the tree already is that machine's copy, and whatever the leg's key, the default or the
    /// command's option says, it works in that tree.
    /// </summary>
    [Fact]
    public void ALegTypedInASyncedCopy_IsNeverPlacedInACopyOfIt()
    {
        using var temp = new TempDirectory();
        var config = new HarnessConfig { Defaults = new HarnessDefaults { LocalCopy = true } };
        var leg = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", LocalCopy = true };
        var variant = VariantKey.For(config, leg, "linux");
        var comparison = HostDoubles.Platform(PlatformId.Linux).PathComparison;

        var inATree = LegTrees.For(new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config), leg, variant, HostId.Local, here: null, comparison, LocalTrees.Copy);
        var inACopy = LegTrees.For(
            new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config) { IsSyncedCopy = true },
            leg,
            variant,
            HostId.Local,
            here: null,
            comparison,
            LocalTrees.Copy);

        PathAssert.Same(LegCopies.PathOf(temp.Path, variant), inATree.Work);
        Assert.True(inATree.LocalCopy);

        PathAssert.Same(temp.Path, inACopy.Work);
        Assert.False(inACopy.LocalCopy);
    }

    /// <summary>
    /// Where a leg this machine runs itself works is said by the first of these that says anything - the command's
    /// option, the leg's own key, the repository's default, the tree - so a leg with the key works in
    /// <c>&lt;tree&gt;/.legs/&lt;key&gt;</c>, its own <see langword="false"/> turns a <see langword="true"/> default off,
    /// and the command's option overrules both either way.
    /// </summary>
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(true, null, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false, LocalTrees.InPlace)]
    [InlineData(true, null, false, LocalTrees.InPlace)]
    [InlineData(null, null, true, LocalTrees.Copy)]
    [InlineData(false, false, true, LocalTrees.Copy)]
    public void WhereALegThisMachineRunsItselfWorks_IsSaidByTheFirstOfOptionKeyAndDefaultThatSaysAnything(
        bool? key,
        bool? @default,
        bool copy,
        LocalTrees option = LocalTrees.AsConfigured)
    {
        using var temp = new TempDirectory();
        var config = new HarnessConfig { Defaults = new HarnessDefaults { LocalCopy = @default == true } };
        var leg = Assert.Single(Place(new HarnessLayout(temp.Path, temp.Path), HostId.Local, here: null, namedWorktree: null, key, config, option));

        var variant = VariantKey.For(config, leg.Leg, "linux");

        Assert.Equal(copy, leg.LocalCopy);
        PathAssert.Same(temp.Path, leg.TreeRoot);
        PathAssert.Same(copy ? LegCopies.PathOf(temp.Path, variant) : temp.Path, leg.HostTreeRoot);
        PathAssert.Same(variant.DirectoryUnder(leg.HostTreeRoot), leg.BuildDirectory);
    }

    /// <summary>
    /// A leg naming a worktree gets a copy of that worktree, of its own, in the worktree's .legs - never a copy of the
    /// main checkout, whose files the worktree's leg has no business building.
    /// </summary>
    [Fact]
    public void ALegNamingAWorktree_GetsACopyOfThatWorktree_InItsOwnLegs()
    {
        using var temp = new TempDirectory();
        var worktree = temp.Combine(".harness-config", "worktrees", "feature");
        var leg = Assert.Single(Place(new HarnessLayout(worktree, temp.Path), HostId.Local, here: null, namedWorktree: "feature", key: true));
        var variant = VariantKey.For(Config, leg.Leg, "linux");

        PathAssert.Same(worktree, leg.TreeRoot);
        PathAssert.Same(LegCopies.PathOf(worktree, variant), leg.HostTreeRoot);
    }

    /// <summary>
    /// A copy whose build would pass this machine's path limit refuses the run before anything starts, with the numbers
    /// and the way out; the same leg with the option that works it in its tree is placed as any in-place leg is.
    /// </summary>
    [Fact]
    public void ACopyWhoseBuildWouldPassThePathLimit_RefusesTheRun_BeforeAnythingStarts()
    {
        using var temp = new TempDirectory();
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var budget = Substitute.For<IPathBudget>();

        budget.Check(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>())
            .Returns(PathBudgetResult.Exceeded(300, 260, -1));

        var refusal = Assert.Throws<HarnessException>(() => Place(layout, HostId.Local, here: null, namedWorktree: null, key: true, budget: budget));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.StartsWith(
            $"Leg 'remote' is to work in a copy of '{temp.Path}', kept at '",
            refusal.Message,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "Keep the tree at a shorter path, set worktrees.pathLimit where every tool its build runs takes longer ones, "
            + "or run it with --in-place. Nothing was run.",
            refusal.Message,
            StringComparison.Ordinal);

        var inPlace = Assert.Single(Place(layout, HostId.Local, here: null, namedWorktree: null, key: true, budget: budget, option: LocalTrees.InPlace));

        Assert.False(inPlace.LocalCopy);
        PathAssert.Same(temp.Path, inPlace.HostTreeRoot);
    }

    /// <summary>
    /// A <see langword="true"/> default says nothing of a leg a host runs: that leg works in the host's copy of its tree,
    /// whatever the default says, and no copy is made here.
    /// </summary>
    [Fact]
    public void ATrueDefault_SaysNothingOfALegAHostRuns()
    {
        using var temp = new TempDirectory();
        var config = new HarnessConfig
        {
            SshItems = { "pi" },
            Hosts = new HostsConfig { Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } } },
            Defaults = new HarnessDefaults { LocalCopy = true },
        };

        var leg = Assert.Single(Place(new HarnessLayout(temp.Path, temp.Path), Pi, here: null, namedWorktree: null, config: config));

        Assert.False(leg.LocalCopy);
        Assert.Equal("/home/pi/repo", leg.HostTreeRoot);
    }

    private static IReadOnlyList<PlacedLeg> Place(
        HarnessLayout layout,
        HostId host,
        HostId? here,
        string? namedWorktree,
        bool? key = null,
        HarnessConfig? config = null,
        LocalTrees option = LocalTrees.AsConfigured,
        IPathBudget? budget = null)
    {
        var selected = new SelectedLeg("remote", new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Worktree = namedWorktree, LocalCopy = key });
        var report = new HostReport { Host = host, Os = "linux", Processor = "x86_64" };

        return LegRunPlan.From(
            new HarnessContext(layout, config ?? Config),
            new LegsReport([new LegPlacement(selected, report, null)], [report], Named: false) { Here = here, LocalTrees = option },
            HostDoubles.Platform(PlatformId.Linux),
            budget,
            out _);
    }
}
