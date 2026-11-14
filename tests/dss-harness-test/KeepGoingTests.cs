using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Legs;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// <c>--keep-going</c>: how each build tool a leg's build runs goes on past a unit that fails to build, what its build is
/// given for it, where the option is refused before anything runs, and what a failed build's line says of its units.
/// </summary>
public sealed class KeepGoingTests
{
    /// <summary>
    /// Each generator's build tool goes on its own way, never one guessed: ninja's are given -k 0 and make's -k, the
    /// arguments CMake hands on after '--'; MSBuild, which the Visual Studio generators run, goes on by itself and is given
    /// nothing; nmake, whose /K does not reach the makes CMake starts for each target, and every generator no way is known
    /// for, have none.
    /// </summary>
    [Theory]
    [InlineData("Ninja", KeepGoingHow.WhenTold, new[] { "-k", "0" })]
    [InlineData("Ninja Multi-Config", KeepGoingHow.WhenTold, new[] { "-k", "0" })]
    [InlineData("Unix Makefiles", KeepGoingHow.WhenTold, new[] { "-k" })]
    [InlineData("MinGW Makefiles", KeepGoingHow.WhenTold, new[] { "-k" })]
    [InlineData("MSYS Makefiles", KeepGoingHow.WhenTold, new[] { "-k" })]
    [InlineData("Visual Studio 17 2022", KeepGoingHow.ByItself, new string[0])]
    [InlineData("Visual Studio 18 2026", KeepGoingHow.ByItself, new string[0])]
    [InlineData("NMake Makefiles", KeepGoingHow.Never, new string[0])]
    [InlineData("NMake Makefiles JOM", KeepGoingHow.Never, new string[0])]
    [InlineData("Xcode", KeepGoingHow.Never, new string[0])]
    [InlineData("Watcom WMake", KeepGoingHow.Never, new string[0])]
    public void EachGeneratorsBuildTool_GoesOnItsOwnWay(string generator, KeepGoingHow how, string[] arguments)
    {
        var way = new CMakeAdapter().KeepGoingFor(Config(("tc", generator)), "tc");

        Assert.Equal(how, way.How);
        Assert.Equal(arguments, way.Arguments);
    }

    /// <summary>
    /// A generator no way is known for says which, and a toolchain naming none says that CMake, not the configuration,
    /// would pick its build tool as it configures - so neither is guessed at, and the refusal names what to change.
    /// </summary>
    [Fact]
    public void AGeneratorWithNoKnownWay_AndAToolchainNamingNone_SayWhy()
    {
        var config = Config(("xcode", "Xcode"), ("bare", null));

        Assert.Equal(
            "toolchain 'xcode' builds with generator 'Xcode', and no way is known to have its build tool go on past a failing unit",
            new CMakeAdapter().KeepGoingFor(config, "xcode").Said);
        Assert.Equal(
            "toolchain 'bare' names no generator, so the build tool CMake picks for it - its default for the system, CMAKE_GENERATOR, "
            + "or the one an earlier configure cached - and how that tool goes on past a failing unit are not known before it builds; "
            + "name one with toolchains.bare.generator",
            new CMakeAdapter().KeepGoingFor(config, "bare").Said);
        Assert.Equal(KeepGoingHow.Never, new CMakeAdapter().KeepGoingFor(config, "bare").How);
    }

    /// <summary>
    /// The build phase gives its tool its way after everything cmake --build reads itself, behind the '--' that hands the
    /// rest to that tool, and only where the build was asked to keep going; a tool that goes on by itself is given nothing.
    /// </summary>
    [Theory]
    [InlineData("Ninja", true, new[] { "--", "-k", "0" })]
    [InlineData("Ninja", false, new string[0])]
    [InlineData("MinGW Makefiles", true, new[] { "--", "-k" })]
    [InlineData("Unix Makefiles", false, new string[0])]
    [InlineData("Visual Studio 17 2022", true, new string[0])]
    public void TheBuildPhase_GivesItsToolItsWay_OnlyWhenAskedToKeepGoing(string generator, bool keepGoing, string[] after)
    {
        using var temp = new TempDirectory();
        var buildDirectory = temp.Combine("build");

        var phases = new CMakeAdapter()
            .Phases(Config(("tc", generator)), Request(temp, keepGoing), buildDirectory, new VariantOverlay(), new Dictionary<string, string?>())
            .ToList();

        Assert.Equal([CMakeAdapter.ConfigurePhase, CMakeAdapter.BuildPhase], phases.Select(phase => phase.Phase));
        Assert.Equal(["--build", buildDirectory, "--parallel", "1", "--target", "app", .. after], phases[1].Arguments);
        Assert.DoesNotContain("--", phases[0].Arguments);
    }

    /// <summary>
    /// dotnet and dart go on by themselves - MSBuild builds every project that does not need one that failed, and dart's
    /// compiler reports every error of a program in one run - so their builds are given nothing, kept going or not.
    /// </summary>
    [Theory]
    [InlineData("dotnet")]
    [InlineData("dart")]
    public void DotnetAndDart_GoOnByThemselves_AndAreGivenNothing(string type)
    {
        using var temp = new TempDirectory();
        var adapter = BuildAdapters.For(type);
        var config = Config(("tc", null));

        Assert.Equal(KeepGoingHow.ByItself, adapter.KeepGoingFor(config, "tc").How);
        Assert.Equal(
            adapter.Phases(config, Request(temp, keepGoing: false, type), temp.Combine("build"), new VariantOverlay(), new Dictionary<string, string?>()).Select(phase => phase.Arguments.ToList()),
            adapter.Phases(config, Request(temp, keepGoing: true, type), temp.Combine("build"), new VariantOverlay(), new Dictionary<string, string?>()).Select(phase => phase.Arguments.ToList()));
    }

    /// <summary>
    /// Help lists every way each adapter goes by, and every way it answers for a generator is one of them: a way help does
    /// not list would be a behaviour nobody was told of.
    /// </summary>
    [Theory]
    [InlineData("Ninja")]
    [InlineData("Unix Makefiles")]
    [InlineData("Visual Studio 17 2022")]
    [InlineData("NMake Makefiles")]
    public void EveryWayAGeneratorIsAnswered_IsOneHelpLists(string generator)
    {
        var adapter = new CMakeAdapter();

        Assert.Contains(adapter.KeepGoingFor(Config(("tc", generator)), "tc"), adapter.KeepGoingWays);
    }

    /// <summary>
    /// A run asked to keep going that builds none of the legs it selects is refused before anything runs: the option would
    /// change nothing, while the command line says it did.
    /// </summary>
    [Fact]
    public void ARunAskedToKeepGoing_ThatBuildsNoLeg_IsRefused()
    {
        var config = Config(("tc", "Ninja"));

        var refused = Assert.Throws<HarnessException>(() => KeepGoing.Require(config, Legs(config, "tc"), LegWorkload.Copy));

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            "--keep-going goes on past the units a leg's build fails, and this run builds none of the legs it selects. Leave --keep-going out.",
            refused.Message);
    }

    /// <summary>
    /// A run asked to keep going is refused before anything runs where a leg it builds is built by a tool with no known
    /// way to go on, naming every such leg and why at once; a leg whose tool goes on, told or by itself, is not named.
    /// </summary>
    [Fact]
    public void ARunAskedToKeepGoing_IsRefusedForEveryLegWhoseToolHasNoWay_NamingEach()
    {
        var config = Config(("ninja", "Ninja"), ("xcode", "Xcode"), ("bare", null), ("vs", "Visual Studio 17 2022"));

        var refused = Assert.Throws<HarnessException>(() => KeepGoing.Require(config, Legs(config, "ninja", "xcode", "bare", "vs"), LegWorkload.BuildOnly));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "--keep-going cannot be honoured for 2 legs this run builds, so nothing was run:",
                "  - Leg 'xcode': toolchain 'xcode' builds with generator 'Xcode', and no way is known to have its build tool go on past a failing unit.",
                "  - Leg 'bare': toolchain 'bare' names no generator, so the build tool CMake picks for it - its default for the system, CMAKE_GENERATOR, "
                + "or the one an earlier configure cached - and how that tool goes on past a failing unit are not known before it builds; "
                + "name one with toolchains.bare.generator.",
                "Leave --keep-going out, or leave those legs out of the run with --legs."),
            refused.Message);

        // The legs whose tools go on are the run's to build.
        KeepGoing.Require(config, Legs(config, "ninja", "vs"), LegWorkload.BuildOnly);
    }

    /// <summary>
    /// A failed build's line says how many units failed, and where its tool stops at its first failures, how far it got
    /// and that --keep-going builds past them - so a unit it never reached never reads as one that built; told to keep going,
    /// what it did not run; and where which units failed was not read, why, never that none did.
    /// </summary>
    [Theory]
    [InlineData(1, false, true, 1, 3, null, "1 unit(s) failed to build; ninja stops at its first failures, and ran 1 of the 3 steps it had to run: the 2 it never reached were not built, and say nothing of the code - --keep-going builds past failing units")]
    [InlineData(1, false, true, 2, 3, null, "1 unit(s) failed to build; ninja stops at its first failures, and ran 2 of the 3 steps it had to run: the one it never reached was not built, and says nothing of the code - --keep-going builds past failing units")]
    [InlineData(1, false, true, 3, 3, null, "1 unit(s) failed to build; ninja ran all 3 steps it had to run")]
    [InlineData(1, false, true, null, null, null, "1 unit(s) failed to build; ninja stops at its first failures, so a unit it never reached was not built, and says nothing of the code - --keep-going builds past failing units")]
    [InlineData(2, true, true, 2, 3, null, "2 unit(s) failed to build; ninja went on past them and ran 2 of the 3 steps it had to run: the one it did not run needs one that failed")]
    [InlineData(2, true, true, 2, 5, null, "2 unit(s) failed to build; ninja went on past them and ran 2 of the 5 steps it had to run: the 3 it did not run need one that failed")]
    [InlineData(2, true, true, 4, 4, null, "2 unit(s) failed to build; ninja went on past them and ran all 4 steps it had to run")]
    [InlineData(2, true, true, null, null, null, "2 unit(s) failed to build; ninja went on past them")]
    [InlineData(2, false, false, null, null, null, "2 unit(s) failed to build")]
    [InlineData(0, false, true, 4, 9, null, "ninja named no step that failed, so the build failed outside every unit")]
    [InlineData(0, false, true, null, null, "its log is gone", "which units failed to build was not read: its log is gone")]
    public void AFailedBuildsLine_SaysItsUnits_AndHowFarItsToolGot(int units, bool keptGoing, bool stopsAtFirst, int? run, int? planned, string? unread, string said)
    {
        var failed = new FailedUnits("ninja", [.. Enumerable.Range(0, units).Select(unit => new FailedUnit($"src/{unit}.c", $"CMakeFiles/app.dir/src/{unit}.c.o"))], unread, keptGoing, stopsAtFirst, run, planned);

        Assert.Equal(said, failed.Describe());
    }

    /// <summary>
    /// A build whose tool's lines are not read for its units - MSBuild's, dotnet's, dart's - says so, naming the tool and
    /// where each unit is named, never that none failed.
    /// </summary>
    [Fact]
    public void ABuildWhoseToolIsNotRead_SaysSo_NamingTheTool()
        => Assert.Equal(
            "which units failed to build was not read: only ninja's and make's output is read for them, and this build ran MSBuild: its log names each unit that failed",
            FailedUnits.NotRead("MSBuild", keptGoing: false, stopsAtFirst: false).Describe());

    /// <summary>
    /// A unit's source is named relative to the leg's tree with forward slashes where it is in the tree - absolute as CMake
    /// names it, or relative to the build directory - and as the build names it where it is outside the tree.
    /// </summary>
    [Fact]
    public void AUnitsSource_IsNamedRelativeToTheTree_WhereItIsInIt()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var tree = temp.Path;
        var build = temp.Combine("build", "x");

        Assert.Equal("src/a.c", FailedUnits.SourceIn(Path.Combine(tree, "src", "a.c").Replace('\\', '/'), build, tree));
        Assert.Equal("build/x/gen/b.c", FailedUnits.SourceIn("gen/b.c", build, tree));
        Assert.Equal(outside.Combine("c.c"), FailedUnits.SourceIn(outside.Combine("c.c"), build, tree));
    }

    /// <summary>
    /// A configuration of one project with a leg for each toolchain given, each toolchain naming the generator given -
    /// none where it is null.
    /// </summary>
    private static HarnessConfig Config(params (string Toolchain, string? Generator)[] toolchains)
    {
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "cmake", Targets = ["app"] } },
        };

        foreach (var (toolchain, generator) in toolchains)
        {
            config.Toolchains[toolchain] = new ToolchainConfig { Generator = generator };
            config.Legs[toolchain] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = toolchain };
        }

        return config;
    }

    /// <summary>The legs of <paramref name="config"/> <paramref name="names"/> names, in that order.</summary>
    private static List<SelectedLeg> Legs(HarnessConfig config, params string[] names) => [.. names.Select(name => new SelectedLeg(name, config.Legs[name]))];

    /// <summary>A build of the project <paramref name="type"/> builds with the toolchain 'tc', on one core, kept going or not.</summary>
    private static BuildRequest Request(TempDirectory temp, bool keepGoing, string type = "cmake")
        => new(
            "a",
            temp.Path,
            new ProjectConfig { Name = "app", Type = type, Path = type == "dotnet" ? "app.slnx" : ".", Targets = ["app"] },
            new VariantKey("x86_64", "tc", "debug", null),
            "linux",
            1,
            temp.Combine("runs", "r"))
        {
            KeepGoing = keepGoing,
        };
}
