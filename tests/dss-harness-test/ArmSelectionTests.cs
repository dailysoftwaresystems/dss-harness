using RepoHarness.Core.Configuration;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// Which arms a sweep drives, and where: <c>--arms</c> resolved as <c>--legs</c> is, and each arm's S row resolved
/// through the same selection, so an arm's legs are named in the one syntax a reader already knows.
/// </summary>
public sealed class ArmSelectionTests
{
    private static readonly HarnessConfig Config = new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Legs =
        {
            ["linux-x64"] = HostDoubles.Leg("linux", "x86_64"),
            ["linux-arm64"] = HostDoubles.Leg("linux", "arm64"),
            ["windows-x64"] = HostDoubles.Leg("windows", "x86_64"),
        },
        LegSets = { ["linux"] = ["linux-x64", "linux-arm64"] },
    };

    private static readonly MutationRegistry Registry = MutationRegistryParser.Parse(
        [
            "A | charge | src/a.cpp | t/a.before | t/a.after | TEST-RED | fx | fx | 3 | t/a.diag | why",
            "C | charge | Fixture.A | why",
            "A | depth | src/b.cpp | t/b.before | t/b.after | TEST-RED | fx | fx | 3 | t/b.diag | why",
            "C | depth | Fixture.B | why",
            "S | depth | linux | only the linux legs build it",
            "A | spare | src/c.cpp | t/c.before | t/c.after | TEST-RED | fx | fx | 3 | t/c.diag | why",
            "C | spare | Fixture.C | why",
            "S | spare | windows-x64 | only windows builds it",
        ],
        texts: null).Registry;

    /// <summary>--arms left out selects every arm; given, the arms it names, ignoring case, in the registry's order.</summary>
    [Fact]
    public void Arms_LeftOutSelectsEveryArm_AndNamedSelectsThoseInTheRegistrysOrder()
    {
        Assert.Equal(["charge", "depth", "spare"], ArmSelection.Resolve(Registry, null).Select(arm => arm.Id));
        Assert.Equal(["charge", "spare"], ArmSelection.Resolve(Registry, ["SPARE,charge", "spare"]).Select(arm => arm.Id));
    }

    /// <summary>
    /// An id naming no arm is a usage error naming it, and --arms given no id is too, never read as every arm: a sweep
    /// of one arm whose name was lost would drive them all.
    /// </summary>
    [Theory]
    [InlineData("charge,nope", "--arms names 'nope', which no A row of the registry declares")]
    [InlineData(" , ", "--arms was given no arm id; leave it out to select every arm")]
    public void AnUnknownOrMissingArm_IsAUsageError(string value, string expected)
    {
        var exception = Assert.Throws<HarnessException>(() => ArmSelection.Resolve(Registry, [value]));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Equal(expected, exception.Message);
    }

    /// <summary>
    /// An S row names legs and leg sets as --legs does, and one naming neither is a problem with its line - collected
    /// with the registry's own, never a refusal of its own - while an arm without one has no scope: every leg.
    /// </summary>
    [Fact]
    public void AScope_IsResolvedAsLegsAre_AndAnUnknownLegIsAProblemWithItsLine()
    {
        var problems = new List<string>();
        var scopes = ArmSelection.Scopes(Config, Registry, problems);

        Assert.Empty(problems);
        Assert.False(scopes.ContainsKey("charge"));
        Assert.Equal(["linux-arm64", "linux-x64"], scopes["depth"].Order(StringComparer.Ordinal));
        Assert.Equal(["windows-x64"], scopes["spare"]);

        var wrong = MutationRegistryParser.Parse(
            [
                "A | charge | src/a.cpp | t/a.before | t/a.after | TEST-RED | fx | fx | 3 | t/a.diag | why",
                "C | charge | Fixture.A | why",
                "S | charge | linux, macos-arm64 | why",
            ],
            texts: null).Registry;

        _ = ArmSelection.Scopes(Config, wrong, problems);

        Assert.Equal(
            ["line 3: the S row of arm 'charge' names 'macos-arm64', which is neither a leg nor a leg set; declared legs: linux-x64, linux-arm64, windows-x64"],
            problems);
    }

    /// <summary>
    /// A leg drives the selected arms its scope names, and reports every other arm of the registry
    /// skipped-not-selected, saying whether --arms left it out or its S row does not name the leg.
    /// </summary>
    [Fact]
    public void ALeg_DrivesTheSelectedArmsInItsScope_AndSaysWhyOfEveryOther()
    {
        var scopes = ArmSelection.Scopes(Config, Registry, []);
        var selected = ArmSelection.Resolve(Registry, ["charge,depth"]);

        var windows = ArmSelection.For("windows-x64", Registry, selected, scopes);
        var linux = ArmSelection.For("linux-arm64", Registry, selected, scopes);

        Assert.Equal(["charge"], windows.Driven.Select(arm => arm.Id));
        Assert.Equal(
            [("depth", "its S row, line 5, does not name this leg"), ("spare", "--arms did not name it")],
            windows.Unselected.Select(arm => (arm.Arm.Id, arm.Reason)));
        Assert.Equal(["charge", "depth"], linux.Driven.Select(arm => arm.Id));
        Assert.Equal([("spare", "--arms did not name it")], linux.Unselected.Select(arm => (arm.Arm.Id, arm.Reason)));
    }

    /// <summary>An arm selected whose S row names none of the selected legs is driven nowhere, and said to be.</summary>
    [Fact]
    public void AnArmScopedToNoSelectedLeg_IsDrivenNowhere()
    {
        var scopes = ArmSelection.Scopes(Config, Registry, []);
        var every = ArmSelection.Resolve(Registry, null);

        Assert.Equal(["spare"], ArmSelection.DrivenNowhere(["linux-x64", "linux-arm64"], every, scopes).Select(arm => arm.Id));
        Assert.Empty(ArmSelection.DrivenNowhere(["linux-x64", "windows-x64"], every, scopes));
    }

    /// <summary>
    /// A selection none of whose arms runs on any selected leg is a usage error, naming each arm with the legs its S row
    /// names: every leg would be skipped, and the sweep would pass having driven nothing. One that drives an arm
    /// somewhere is not refused, whatever arms it drives nowhere.
    /// </summary>
    [Fact]
    public void ASelectionDrivingNoArmOnAnySelectedLeg_IsAUsageError_NamingEachArmAndWhereItRuns()
    {
        var scopes = ArmSelection.Scopes(Config, Registry, []);
        var scoped = ArmSelection.Resolve(Registry, ["depth,spare"]);

        var refusal = Assert.Throws<HarnessException>(() => ArmSelection.RequireDriven(["windows-x64"], ArmSelection.Resolve(Registry, ["depth"]), scopes));
        var both = Assert.Throws<HarnessException>(() => ArmSelection.RequireDriven([], scoped, scopes));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "The sweep would drive no arm, so nothing was run: no arm selected runs on a selected leg (windows-x64), and a sweep that drove none would pass having proved nothing.",
                "  - arm 'depth' runs where its S row, line 5, names: linux",
                "Name a leg an arm runs on with --legs, or an arm these legs run with --arms."),
            refusal.Message);
        Assert.Contains("  - arm 'depth' runs where its S row, line 5, names: linux", both.Message, StringComparison.Ordinal);
        Assert.Contains("  - arm 'spare' runs where its S row, line 8, names: windows-x64", both.Message, StringComparison.Ordinal);

        ArmSelection.RequireDriven(["windows-x64"], scoped, scopes);
        ArmSelection.RequireDriven(["linux-x64"], ArmSelection.Resolve(Registry, null), scopes);
    }
}
