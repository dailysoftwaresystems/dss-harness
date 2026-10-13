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
    /// A leg that works in a copy of its tree kept beside the tree on this machine is synced and in a copy, and is
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

    private static IReadOnlyList<PlacedLeg> Place(HarnessLayout layout, HostId host, HostId? here, string? namedWorktree)
    {
        var selected = new SelectedLeg("remote", new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Worktree = namedWorktree });
        var report = new HostReport { Host = host, Os = "linux", Processor = "x86_64" };

        return LegRunPlan.From(
            new HarnessContext(layout, Config),
            new LegsReport([new LegPlacement(selected, report, null)], [report], Named: false) { Here = here },
            HostDoubles.Platform(PlatformId.Linux),
            out _);
    }
}
