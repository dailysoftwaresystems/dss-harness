using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Testing;

namespace RepoHarness.Tests;

/// <summary>
/// What a leg runs is decided in three layers, and the two that matter most are the ones a
/// configuration can leave out: a run with no runner does nothing, and a run with no success
/// pattern reports a zero exit code as a pass. Both are refused when the file is read, and the
/// core count is handed over by one implementation rather than two.
/// </summary>
public sealed class TestInvocationResolverTests
{
    [Fact]
    public void AMergedInvocationWithNoSuccessPattern_IsRefused()
    {
        var settings = new TestConfig { All = new TestInvocation { Runner = "ctest" } };

        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.Resolve(settings, PlatformNames.Linux));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("successPattern", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlatformSectionThatRemovesNothing_StillNeedsTheSuccessPatternFromAll()
    {
        // The platform section names a runner and nothing else, so the pattern has to come from
        // 'all'. A merge that took the platform section whole would silently drop it.
        var settings = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" },
            Windows = new TestInvocation { Runner = "ctest.exe" },
        };

        var invocation = TestInvocationResolver.Resolve(settings, PlatformNames.Windows);

        Assert.Equal("ctest.exe", invocation.Runner);
        Assert.Equal("tests passed", invocation.SuccessPattern);
    }

    [Fact]
    public void AMergedInvocationWithNoRunner_IsRefused()
    {
        var settings = new TestConfig { All = new TestInvocation { SuccessPattern = "tests passed" } };

        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.Resolve(settings, PlatformNames.MacOs));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("no runner", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegsOwnTestSettings_ReplaceTheProjects()
    {
        var project = new ProjectConfig
        {
            Name = "core",
            Type = "cmake",
            Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" } },
        };

        var leg = new LegConfig
        {
            Os = PlatformNames.Linux,
            Processor = PlatformNames.Arm64,
            Config = "release",

            // A leg that runs another suite - a smoke subset here - declares a section of its own,
            // which replaces the project's rather than merging with it: a half inherited runner is
            // how a leg ends up running the project's suite with its own arguments.
            Test = new TestConfig
            {
                All = new TestInvocation { Runner = "ctest", Args = ["-L", "smoke"], SuccessPattern = "tests passed" },
            },
        };

        var settings = TestInvocationResolver.SettingsFor(new HarnessConfig(), leg, project);

        Assert.NotNull(settings);
        Assert.Equal(["-L", "smoke"], TestInvocationResolver.Resolve(settings, PlatformNames.Linux).Args);
    }

    /// <summary>
    /// A test set is merged per platform like every other setting: a platform's own names its set,
    /// and one naming none runs the set <c>all</c> names, or the project's shared one.
    /// </summary>
    [Fact]
    public void ATestSet_IsMergedPerPlatform_LikeEveryOtherSetting()
    {
        var settings = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" },
            Windows = new TestInvocation { TestSet = "windows" },
        };

        var named = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed", TestSet = "sanitized" },
            Windows = new TestInvocation { TestSet = "windows" },
        };

        Assert.Equal("windows", TestInvocationResolver.Resolve(settings, PlatformNames.Windows).TestSet);
        Assert.Null(TestInvocationResolver.Resolve(settings, PlatformNames.Linux).TestSet);
        Assert.Equal("sanitized", TestInvocationResolver.Resolve(named, PlatformNames.Linux).TestSet);
        Assert.Equal("windows", TestInvocationResolver.Resolve(named, PlatformNames.Windows).TestSet);
    }

    [Fact]
    public void AProjectsTestSettings_ApplyWhenTheLegDeclaresNone()
    {
        var project = new ProjectConfig
        {
            Name = "core",
            Type = "cmake",
            Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" } },
        };

        var leg = new LegConfig { Os = PlatformNames.Linux, Processor = PlatformNames.X64, Config = "release" };

        var settings = TestInvocationResolver.SettingsFor(new HarnessConfig(), leg, project);

        Assert.Same(project.Test, settings);
    }

    [Theory]
    [InlineData("-j", "8")]
    [InlineData("-j8", null)]
    [InlineData("-j=8", null)]
    public void AnExplicitCoreOptionInArgs_BeatsCoresArgs(string option, string? value)
    {
        string[] args = value is null ? [option] : [option, value];

        var command = TestInvocationResolver.CommandFor(
            Invocation(args: args, coresArgs: ["-j", "{cores}"]),
            cores: 6,
            filter: null,
            excludes: null);

        // Nothing is spliced in. Two '-j' options would leave the runner to pick one, and the
        // report would say which count was asked for rather than which one ran.
        Assert.Equal(args, command.Arguments);
    }

    [Fact]
    public void CoresArgs_CarryTheCount_WhenTheArgumentsDoNotSetIt()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--output-on-failure"], coresArgs: ["-j", "{cores}"]),
            cores: 4,
            filter: null,
            excludes: null);

        Assert.Equal(["--output-on-failure", "-j", "4"], command.Arguments);
    }

    [Fact]
    public void CoresEnv_CarriesTheCount_AlongsideTheInvocationsOwnEnvironment()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(
                env: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CTEST_OUTPUT_ON_FAILURE"] = "1" },
                coresEnv: ["CTEST_PARALLEL_LEVEL"]),
            cores: 3,
            filter: null,
            excludes: null);

        Assert.Equal("3", command.Environment["CTEST_PARALLEL_LEVEL"]);
        Assert.Equal("1", command.Environment["CTEST_OUTPUT_ON_FAILURE"]);
    }

    /// <summary>
    /// A filter and each exclusion are spliced in after the invocation's own arguments, each exclusion
    /// after an excludeArg of its own, for a runner that leaves out what each of several given apart
    /// matches.
    /// </summary>
    [Fact]
    public void AFilterAndAnExclusion_AreSplicedInWithTheInvocationsOwnArguments()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["test"], filterArg: "--name", excludeArg: "--exclude-tags", runner: "dart"),
            cores: 6,
            filter: "parser",
            excludes: ["slow", "flaky"]);

        Assert.Equal(["test", "--name", "parser", "--exclude-tags", "slow", "--exclude-tags", "flaky"], command.Arguments);
    }

    /// <summary>
    /// The exclusions given are appended, as one more alternative, to the value the invocation's own args
    /// give -LE - in either of ctest's spellings, after it, after '=' or after a space in the same argument -
    /// where it stands and as it is spelled: left apart beside it, ctest leaves out only a test both match.
    /// Where none is given, the args are the author's own, run as written, however many they give.
    /// </summary>
    [Theory]
    [InlineData(new[] { "--output-on-failure", "-LE", "manual" }, new[] { "slow" }, new[] { "--output-on-failure", "-LE", "manual|slow" })]
    [InlineData(new[] { "--output-on-failure", "-LE", "manual" }, new[] { "slow", "gpu" }, new[] { "--output-on-failure", "-LE", "manual|slow|gpu" })]
    [InlineData(new[] { "--label-exclude", "manual", "-V" }, new[] { "slow" }, new[] { "--label-exclude", "manual|slow", "-V" })]
    [InlineData(new[] { "-LE=manual" }, new[] { "slow" }, new[] { "-LE=manual|slow" })]
    [InlineData(new[] { "-LE manual" }, new[] { "slow" }, new[] { "-LE manual|slow" })]
    [InlineData(new[] { "--label-exclude=manual" }, new[] { "slow" }, new[] { "--label-exclude=manual|slow" })]
    [InlineData(new[] { "--output-on-failure", "-LE", "manual" }, new string[0], new[] { "--output-on-failure", "-LE", "manual" })]
    [InlineData(new[] { "-LE", "manual", "-LE", "nightly" }, new string[0], new[] { "-LE", "manual", "-LE", "nightly" })]
    public void AnExclusionTheArgsAlreadyGive_TakesThoseGiven_WhereItStands(string[] args, string[] excludes, string[] expected)
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: args, excludeArg: "-LE", excludeJoin: "|"),
            cores: 6,
            filter: null,
            excludes: excludes);

        Assert.Equal(expected, command.Arguments);
    }

    /// <summary>
    /// Two exclusions the args give apart keep their meaning with one given beside them: each -LE, which ctest
    /// reads as one more a test must match, takes it as one more alternative - "(A and B) or C" is "(A or C) and
    /// (B or C)", measured - and of two -E, which ctest reads the last of, the last takes it. With no join to
    /// give it as one, it is refused.
    /// </summary>
    [Theory]
    [InlineData("-LE", "|", new[] { "-LE", "manual|gpu", "-LE", "slow|gpu" })]
    [InlineData("-E", "|", new[] { "-E", "manual", "-E", "slow|gpu" })]
    [InlineData("-LE", null, null)]
    public void TwoExclusionsTheArgsGiveApart_TakeOneGivenBesideThem_AsCtestReadsThem(string excludeArg, string? join, string[]? expected)
    {
        TestCommand Command() => TestInvocationResolver.CommandFor(
            Invocation(args: [excludeArg, "manual", excludeArg, "slow"], excludeArg: excludeArg, excludeJoin: join), cores: 6, filter: null, excludes: ["gpu"]);

        if (expected is not null)
        {
            Assert.Equal(expected, Command().Arguments);
            return;
        }

        Assert.Equal(HarnessExit.UsageError, Assert.Throws<HarnessException>(Command).ExitCode);
    }

    /// <summary>
    /// An exclusion beside a ctest test preset the args name is refused, naming the preset, where the
    /// preset leaves tests out the same way - itself or through any preset it inherits, across the files
    /// ctest reads presets from - which ctest combines with the command line's so that neither leaves out
    /// what it names: a preset's label exclusion and -LE leave out only a test both match, and -E replaces
    /// the preset's. An empty value clears nothing, measured: ctest takes the first value along the inherits
    /// that says something, and one alone says nothing. A preset that leaves out nothing, or only another way -
    /// choosing tests by the part an exclusion reads among them - runs as asked, and one that cannot be found or
    /// read is refused, as nothing can say.
    /// </summary>
    [Theory]
    [InlineData("plain", "-LE", false)]
    [InlineData("clearing", "-LE", false)]
    [InlineData("labels", "-LE", true)]
    [InlineData("labels", "-E", false)]
    [InlineData("including", "-E", false)]
    [InlineData("labeled", "-LE", false)]
    [InlineData("inheriting", "-LE", true)]
    [InlineData("overriding", "-LE", true)]
    [InlineData("firstEmpty", "-LE", true)]
    [InlineData("names", "-E", true)]
    [InlineData("emptyName", "-E", true)]
    [InlineData("user", "-LE", true)]
    [InlineData("absent", "-LE", true)]
    public void AnExclusionBesideACtestTestPreset_IsRefused_WhereThePresetLeavesTestsOutTheSameWay(string preset, string excludeArg, bool refused)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        var invocation = Invocation(args: ["--preset", preset], excludeArg: excludeArg, excludeJoin: "|");

        TestCommand Command() => TestInvocationResolver.CommandFor(
            invocation, cores: 6, filter: null, excludes: ["slow"], new LegPaths(temp.Path, temp.Combine("build")), fileSystem: FileSystem());

        if (!refused)
        {
            Assert.Equal(["--preset", preset, excludeArg, "slow"], Command().Arguments);
            return;
        }

        var refusal = Assert.Throws<HarnessException>(Command);

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains($"test preset '{preset}'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Several exclusions beside a ctest test preset are refused where one would be, joined into one value or
    /// into the one the args give -LE; beside a preset that leaves out nothing, or leaves tests out only
    /// another way, they reach ctest as one.
    /// </summary>
    [Theory]
    [InlineData("labels", new string[0], null)]
    [InlineData("labels", new[] { "-LE", "manual" }, null)]
    [InlineData("plain", new string[0], new[] { "--preset", "plain", "-LE", "slow|gpu" })]
    [InlineData("plain", new[] { "-LE", "manual" }, new[] { "--preset", "plain", "-LE", "manual|slow|gpu" })]
    [InlineData("names", new string[0], new[] { "--preset", "names", "-LE", "slow|gpu" })]
    public void SeveralExclusionsBesideACtestTestPreset_AreRefusedWhereOneIs(string preset, string[] args, string[]? expected)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        TestCommand Command() => TestInvocationResolver.CommandFor(
            Invocation(args: ["--preset", preset, .. args], excludeArg: "-LE", excludeJoin: "|"),
            cores: 6,
            filter: null,
            excludes: ["slow", "gpu"],
            new LegPaths(temp.Path, temp.Combine("build")),
            fileSystem: FileSystem());

        if (expected is not null)
        {
            Assert.Equal(expected, Command().Arguments);
            return;
        }

        Assert.Contains($"test preset '{preset}'", Assert.Throws<HarnessException>(Command).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A preset given after '=' is the same preset, and of two the first is the one ctest uses, while one given
    /// after a space in the same argument is none to ctest; a preset named with a placeholder is read with it
    /// filled in, as the argument itself is - the leg's own operating system, never this machine's; another
    /// runner's --preset is its own, and never read; and a preset that inherits itself or one never declared,
    /// and preset files that include a file through a macro or by a path the runtime rejects, or that nothing
    /// here can read, cannot say what the preset leaves out, so an exclusion beside it is refused.
    /// </summary>
    [Fact]
    public void ATestPreset_IsReadAsCtestReadsIt_OrRefusedWhereItCannotBe()
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        var paths = new LegPaths(temp.Path, temp.Combine("build"))
        {
            Identity = new LegIdentity("native", "linux", "x86_64", "gcc", "debug", "x86_64-gcc-debug", "local", "run"),
        };

        TestCommand Command(string runner, string[] args, IFileSystem? fileSystem) => TestInvocationResolver.CommandFor(
            Invocation(args: args, excludeArg: "-LE", runner: runner), cores: 6, filter: null, excludes: ["slow"], paths, fileSystem: fileSystem);

        Assert.Throws<HarnessException>(() => Command("ctest", ["--preset=labels"], FileSystem()));
        Assert.Throws<HarnessException>(() => Command("ctest", ["--preset", "labels", "--preset", "plain"], FileSystem()));
        Assert.Equal(["--preset", "plain", "--preset", "labels", "-LE", "slow"], Command("ctest", ["--preset", "plain", "--preset", "labels"], FileSystem()).Arguments);
        Assert.Equal(["--preset", "linux-plain", "-LE", "slow"], Command("ctest", ["--preset", "{os}-plain"], FileSystem()).Arguments);
        Assert.Equal(["--preset labels", "-LE", "slow"], Command("ctest", ["--preset labels"], FileSystem()).Arguments);
        Assert.Contains("test preset 'loopA' inherits itself", Assert.Throws<HarnessException>(() => Command("ctest", ["--preset", "loopA"], FileSystem())).Message, StringComparison.Ordinal);
        Assert.Contains("test preset 'missing' is inherited and not declared", Assert.Throws<HarnessException>(() => Command("ctest", ["--preset", "orphan"], FileSystem())).Message, StringComparison.Ordinal);
        Assert.Equal(["--preset", "labels", "-LE", "slow"], Command("run-suite", ["--preset", "labels"], FileSystem()).Arguments);
        Assert.Contains("nothing here reads the preset files", Assert.Throws<HarnessException>(() => Command("ctest", ["--preset", "plain"], null)).Message, StringComparison.Ordinal);

        temp.WriteFile("CMakeUserPresets.json", """{ "version": 7, "include": ["$penv{PRESETS}/more.json"], "testPresets": [] }""");

        Assert.Contains("whose macros only CMake expands", Assert.Throws<HarnessException>(() => Command("ctest", ["--preset", "plain"], FileSystem())).Message, StringComparison.Ordinal);

        temp.WriteFile("CMakeUserPresets.json", """{ "version": 7, "include": ["more\u0000.json"], "testPresets": [] }""");

        Assert.Contains("could not be read", Assert.Throws<HarnessException>(() => Command("ctest", ["--preset", "plain"], FileSystem())).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A filter beside a -R the args give already, in any spelling and form, or beside a test preset that
    /// chooses tests by name itself, is refused: ctest keeps only the last of them, and the filter would
    /// run tests the settings leave out. Beside neither it runs as asked.
    /// </summary>
    [Theory]
    [InlineData(new[] { "-R", "unit" }, true)]
    [InlineData(new[] { "--tests-regex=unit" }, true)]
    [InlineData(new[] { "--preset", "including" }, true)]
    [InlineData(new[] { "--preset", "labels" }, false)]
    [InlineData(new string[0], false)]
    public void AFilterBesideOneTheArgsOrAPresetGive_IsRefused(string[] args, bool refused)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        TestCommand Command() => TestInvocationResolver.CommandFor(
            Invocation(args: args, filterArg: "-R"), cores: 6, filter: "parser", excludes: null, new LegPaths(temp.Path, temp.Combine("build")), fileSystem: FileSystem());

        if (!refused)
        {
            Assert.Equal([.. args, "-R", "parser"], Command().Arguments);
            return;
        }

        Assert.Equal(HarnessExit.UsageError, Assert.Throws<HarnessException>(Command).ExitCode);
    }

    /// <summary>
    /// Beside a test preset that takes the union of the tests its filters choose, beside --union in the args -
    /// in either spelling and any form, whatever its value - and beside --rerun-failed, every option that
    /// chooses tests is refused, an exclusion by -LE and a label among them: measured with ctest 4.3.2, the
    /// preset's union runs tests none of them would, beside the args' ctest also runs every test -I picks -
    /// every test, where the args give no -I - whatever -R, -L and -LE say, and --rerun-failed passes over -R,
    /// -L and -LE and runs others for -E.
    /// </summary>
    [Theory]
    [InlineData(new[] { "--preset", "union" }, "filter")]
    [InlineData(new[] { "--preset", "union" }, "exclusion")]
    [InlineData(new[] { "--preset", "union" }, "label")]
    [InlineData(new[] { "--rerun-failed" }, "filter")]
    [InlineData(new[] { "--rerun-failed" }, "exclusion")]
    [InlineData(new[] { "--rerun-failed" }, "label")]
    [InlineData(new[] { "-U", "ON" }, "filter")]
    [InlineData(new[] { "--union=1" }, "exclusion")]
    [InlineData(new[] { "--union", "OFF" }, "label")]
    [InlineData(new[] { "-U" }, "filter")]
    public void EveryOptionThatChoosesTests_IsRefused_BesideAUnionOrARerun(string[] args, string given)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        var refusal = Assert.Throws<HarnessException>(() => TestInvocationResolver.CommandFor(
            Invocation(args: args, filterArg: "-R", excludeArg: "-LE", labelArg: "-L"),
            cores: 6,
            filter: given == "filter" ? "parser" : null,
            excludes: given == "exclusion" ? ["slow"] : null,
            new LegPaths(temp.Path, temp.Combine("build")),
            labels: given == "label" ? ["unit"] : null,
            fileSystem: FileSystem()));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
    }

    /// <summary>
    /// Whether a test preset takes the union of what its filters choose is decided as ctest decides a flag: by the
    /// preset itself where it says, and otherwise by the first preset along its inherits that says. So a preset
    /// inheriting a union takes it, and one saying otherwise itself, or inheriting first a preset that does, does
    /// not: measured with ctest 4.3.2, the first runs every test, and the others only those all their filters
    /// choose.
    /// </summary>
    [Theory]
    [InlineData("unionInherited", true)]
    [InlineData("unionOff", false)]
    [InlineData("unionSecond", false)]
    public void AUnionAPresetInherits_IsItsOwn_UnlessItSaysOtherwise(string preset, bool refused)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        TestCommand Command() => TestInvocationResolver.CommandFor(
            Invocation(args: ["--preset", preset], excludeArg: "-LE"), cores: 6, filter: null, excludes: ["slow"], new LegPaths(temp.Path, temp.Combine("build")), fileSystem: FileSystem());

        if (!refused)
        {
            Assert.Equal(["--preset", preset, "-LE", "slow"], Command().Arguments);
            return;
        }

        Assert.Contains("takes the union", Assert.Throws<HarnessException>(Command).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A label beside a test preset that chooses tests by label itself runs as asked: ctest reads it as one more
    /// label a test must carry, which is what a label asks.
    /// </summary>
    [Fact]
    public void ALabelBesideAPresetChoosingByLabel_RunsAsAsked()
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--preset", "labeled"], labelArg: "-L"), cores: 6, filter: null, excludes: null, new LegPaths(temp.Path, temp.Combine("build")), labels: ["unit"], fileSystem: FileSystem());

        Assert.Equal(["--preset", "labeled", "-L", "unit"], command.Arguments);
    }

    /// <summary>
    /// A test preset is read from the directory ctest starts in, where ctest looks for preset files: the
    /// invocation's working directory where it names one, never the tree root's files beside it.
    /// </summary>
    [Fact]
    public void ATestPreset_IsReadFromTheDirectoryCtestStartsIn()
    {
        using var temp = new TempDirectory();

        WritePresets(temp);
        temp.WriteFile(Path.Combine("tests", "CMakePresets.json"), """{ "version": 6, "testPresets": [ { "name": "labels" } ] }""");

        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--preset", "labels"], excludeArg: "-LE", workingDirectory: "tests"), cores: 6, filter: null, excludes: ["slow"], new LegPaths(temp.Path, temp.Combine("build")), fileSystem: FileSystem());

        Assert.Equal(["--preset", "labels", "-LE", "slow"], command.Arguments);
    }

    /// <summary>
    /// A working directory rooted but not whole - '\tests' on Windows - has its preset read on the tree's own
    /// drive, where the runner starts, never on whichever drive this process happens to be on.
    /// </summary>
    [Fact]
    public void ATestPresetBesideAWorkingDirectoryOnNoDrive_IsReadOnTheTreesDrive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows has a path rooted at no drive.");

        using var temp = new TempDirectory();
        var drive = string.Equals(Path.GetPathRoot(Environment.CurrentDirectory), @"Z:\", StringComparison.OrdinalIgnoreCase) ? @"Y:\" : @"Z:\";

        temp.WriteFile(Path.Combine("tests", "CMakePresets.json"), """{ "version": 6, "testPresets": [ { "name": "ci" } ] }""");

        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--preset", "ci"], excludeArg: "-LE", workingDirectory: @"\tests"),
            cores: 6,
            filter: null,
            excludes: ["slow"],
            new LegPaths(drive + "repo", drive + @"repo\build"),
            fileSystem: new OnDrive(drive, temp.Path));

        Assert.Equal(["--preset", "ci", "-LE", "slow"], command.Arguments);
    }

    /// <summary>
    /// An exclusion by -E beside --union in the args runs as asked: measured with ctest 4.3.2, whatever value
    /// --union is given, ctest leaves out what -E names first, where nothing chooses tests by name too - a test
    /// preset choosing them by label among them.
    /// </summary>
    [Theory]
    [InlineData(new[] { "-U", "ON" }, new[] { "-U", "ON", "-E", "slow" })]
    [InlineData(new[] { "--preset", "labeled", "-U", "ON" }, new[] { "--preset", "labeled", "-U", "ON", "-E", "slow" })]
    public void AnExclusionByName_RunsAsAsked_BesideAUnionInTheArgs(string[] args, string[] expected)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        var command = TestInvocationResolver.CommandFor(
            Invocation(args: args, excludeArg: "-E"),
            cores: 6,
            filter: null,
            excludes: ["slow"],
            new LegPaths(temp.Path, temp.Combine("build")),
            fileSystem: FileSystem());

        Assert.Equal(expected, command.Arguments);
    }

    /// <summary>
    /// But not where the args choose tests by name as well, or the test preset they name does: measured with ctest
    /// 4.3.2, beside --union and -R, or a preset setting filter.include.name, -E narrows only the tests that choice
    /// makes, and ctest also runs every test -I picks - every test, where the args give no -I.
    /// </summary>
    [Theory]
    [InlineData(new[] { "-U", "ON", "-R", "parser" }, "with --union and -R, beside both of which - measured with ctest 4.3.2 - -E narrows only the tests -R chooses")]
    [InlineData(new[] { "--tests-regex=parser", "--union=1" }, "with --union and -R, beside both of which - measured with ctest 4.3.2 - -E narrows only the tests -R chooses")]
    [InlineData(new[] { "--preset", "including", "-U", "ON" }, "with --union and test preset 'including', which sets filter.include.name: beside both - measured with ctest 4.3.2 - -E narrows only the tests that filter chooses")]
    public void AnExclusionByName_IsRefused_BesideAUnionAndAChoiceByName(string[] args, string beside)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        var refusal = Assert.Throws<HarnessException>(() => TestInvocationResolver.CommandFor(
            Invocation(args: args, excludeArg: "-E"),
            cores: 6,
            filter: null,
            excludes: ["slow"],
            new LegPaths(temp.Path, temp.Combine("build")),
            fileSystem: FileSystem()));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains(beside, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("and ctest also runs every test -I picks, every test where the args give no -I", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A selection ctest could only find empty is refused before anything is built for it: a value one option chooses
    /// tests by that another leaves out by the same part of a test, where the filter, a label or an exclusion given is
    /// one of the two - the other given too, or one the args give, their only -LE beside a test preset that leaves no
    /// label out among them. Measured with ctest 4.3.2, -L x beside -LE x, and -R x beside -E x, choose no test, nor do
    /// -L git-state beside -LE git and -R parser_unit beside -E parser, since ctest finds a pattern anywhere in what it
    /// reads; and ctest says so only after the build: 'No tests were found!!!', exiting 8 under --no-tests=error.
    /// </summary>
    [Theory]
    [InlineData(new string[0], "-LE", null, new[] { "git-state" }, new[] { "git-state" }, "The label 'git-state', given with -L, chooses only tests carrying a label that matches it, and the exclusion 'git-state', given with -LE, leaves every one of them out")]
    [InlineData(new string[0], "-LE", null, new[] { "slow", "git-state" }, new[] { "git-state", "unit" }, "The label 'git-state', given with -L,")]
    [InlineData(new[] { "-L", "git-state" }, "-LE", null, new[] { "git-state" }, new string[0], "-L 'git-state' in the test settings' args chooses only tests carrying a label that matches it, and the exclusion 'git-state'")]
    [InlineData(new[] { "--label-regex=unit", "-L", "fast" }, "-LE", null, new[] { "unit" }, new string[0], "-L 'unit' in the test settings' args")]
    [InlineData(new[] { "-LE", "manual" }, "-LE", null, new string[0], new[] { "manual" }, "and -LE 'manual' in the test settings' args leaves every one of them out")]
    [InlineData(new[] { "-LE", "manual" }, "-LE", null, new[] { "slow" }, new[] { "manual" }, "and -LE 'manual' in the test settings' args leaves every one of them out")]
    [InlineData(new string[0], "-E", "parser", new[] { "parser" }, new string[0], "The filter 'parser', given with -R, chooses only tests whose name matches it, and the exclusion 'parser', given with -E,")]
    [InlineData(new[] { "-E", "flaky", "--exclude-regex", "parser" }, "-LE", "parser", new string[0], new string[0], "and -E 'parser' in the test settings' args")]
    [InlineData(new[] { "-R", "lexer", "--tests-regex", "parser" }, "-E", null, new[] { "parser" }, new string[0], "-R 'parser' in the test settings' args chooses only tests whose name matches it")]
    [InlineData(new[] { "--preset", "plain", "-LE", "manual" }, "-LE", null, new string[0], new[] { "manual" }, "and -LE 'manual' in the test settings' args leaves every one of them out")]
    [InlineData(new string[0], "-LE", null, new[] { "git" }, new[] { "git-state" }, "The label 'git-state', given with -L, chooses only tests carrying a label that matches it, and the exclusion 'git', given with -LE, leaves every one of them out, since ctest finds 'git' in all 'git-state' matches")]
    [InlineData(new string[0], "-E", "parser_unit", new[] { "parser" }, new string[0], "The filter 'parser_unit', given with -R, chooses only tests whose name matches it, and the exclusion 'parser', given with -E, leaves every one of them out, since ctest finds 'parser' in all 'parser_unit' matches")]
    public void ASelectionCtestCouldOnlyFindEmpty_IsRefused(string[] args, string excludeArg, string? filter, string[] excludes, string[] labels, string said)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        var refusal = Assert.Throws<HarnessException>(() => TestInvocationResolver.CommandFor(
            Invocation(args: args, filterArg: "-R", excludeArg: excludeArg, excludeJoin: "|", labelArg: "-L"),
            cores: 6,
            filter,
            excludes,
            new LegPaths(temp.Path, temp.Combine("build")),
            labels,
            FileSystem()));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains(said, refusal.Message, StringComparison.Ordinal);
        Assert.EndsWith("so ctest would choose no test - and say so only once the leg had been built for it. Take one of them out.", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a selection ctest is bound to find empty is refused, read as ctest reads its options: two patterns spelled
    /// apart may still choose a test - a pattern ctest reads specially, a value that holds the other only the other
    /// way round, or one spelled in another case, since ctest matches case as written; a name is not a label; of two
    /// values the args give -LE ctest leaves out only a test both match, and of two they give -R or -E it keeps the
    /// last; a preset the args name that leaves out tests by label too may leave out only a test both match, so their
    /// one -LE is not all there is; another runner's options are its own; and the args alone run as their author wrote
    /// them. Measured with ctest 4.3.2: -L git-state beside -LE ^git-state$ runs a test labelled git-state-slow, -R
    /// parser beside -E parser_unit runs parser, -L Git-State beside -LE git-state runs a test labelled Git-State, and
    /// a preset leaving out one label beside -LE nightly and -L nightly runs a test labelled nightly alone.
    /// </summary>
    [Theory]
    [InlineData("ctest", new string[0], "-LE", null, new[] { "^git-state$" }, new[] { "git-state" }, new[] { "-LE", "^git-state$", "-L", "git-state" })]
    [InlineData("ctest", new[] { "-R", "parser" }, "-E", null, new[] { "parser_unit" }, new string[0], new[] { "-R", "parser", "-E", "parser_unit" })]
    [InlineData("ctest", new string[0], "-LE", null, new[] { "git-state" }, new[] { "Git-State" }, new[] { "-LE", "git-state", "-L", "Git-State" })]
    [InlineData("ctest", new string[0], "-E", null, new[] { "git-state" }, new[] { "git-state" }, new[] { "-E", "git-state", "-L", "git-state" })]
    [InlineData("ctest", new[] { "-LE", "slow", "-LE", "manual" }, "-LE", null, new string[0], new[] { "manual" }, new[] { "-LE", "slow", "-LE", "manual", "-L", "manual" })]
    [InlineData("ctest", new[] { "-R", "parser", "-R", "lexer" }, "-E", null, new[] { "parser" }, new string[0], new[] { "-R", "parser", "-R", "lexer", "-E", "parser" })]
    [InlineData("ctest", new[] { "-E", "parser", "-E", "flaky" }, "-LE", "parser", new string[0], new string[0], new[] { "-E", "parser", "-E", "flaky", "-R", "parser" })]
    [InlineData("ctest", new[] { "--preset", "labels", "-LE", "nightly" }, "-LE", null, new string[0], new[] { "nightly" }, new[] { "--preset", "labels", "-LE", "nightly", "-L", "nightly" })]
    [InlineData("ctest", new[] { "-L", "unit", "-LE", "unit" }, "-LE", null, new string[0], new string[0], new[] { "-L", "unit", "-LE", "unit" })]
    [InlineData("dart", new string[0], "-LE", null, new[] { "git-state" }, new[] { "git-state" }, new[] { "-LE", "git-state", "-L", "git-state" })]
    public void ASelectionCtestMayFindATestIn_RunsAsAsked(
        string runner,
        string[] args,
        string excludeArg,
        string? filter,
        string[] excludes,
        string[] labels,
        string[] expected)
    {
        using var temp = new TempDirectory();

        WritePresets(temp);

        var command = TestInvocationResolver.CommandFor(
            Invocation(args: args, filterArg: "-R", excludeArg: excludeArg, excludeJoin: "|", labelArg: "-L", runner: runner),
            cores: 6,
            filter,
            excludes,
            new LegPaths(temp.Path, temp.Combine("build")),
            labels,
            FileSystem());

        Assert.Equal(expected, command.Arguments);
    }

    /// <summary>
    /// A filter, an exclusion or a label given empty, or as spaces alone, is refused: ctest reads an empty one as
    /// not given at all, and joined with another as matching everything, and spaces alone are a value lost on the
    /// way. So is an empty exclusion in the args one given would be joined with.
    /// </summary>
    [Theory]
    [InlineData("", new string[0], new string[0], new string[0])]
    [InlineData(" ", new string[0], new string[0], new string[0])]
    [InlineData(null, new[] { "" }, new string[0], new string[0])]
    [InlineData(null, new[] { " " }, new string[0], new string[0])]
    [InlineData(null, new string[0], new[] { "" }, new string[0])]
    [InlineData(null, new string[0], new[] { " " }, new string[0])]
    [InlineData(null, new[] { "slow" }, new string[0], new[] { "-LE", "" })]
    public void AFilterAnExclusionOrALabelOfNothing_IsRefused(string? filter, string[] excludes, string[] labels, string[] args)
    {
        var refusal = Assert.Throws<HarnessException>(() => TestInvocationResolver.CommandFor(
            Invocation(args: args, filterArg: "-R", excludeArg: "-LE", excludeJoin: "|", labelArg: "-L"), cores: 6, filter: filter, excludes: excludes, labels: labels));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("given empty, or as spaces alone", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exclusions given are appended to the value the args give -E, in either spelling and each form ctest
    /// takes, where it stands: ctest keeps only the last -E, which then leaves out both.
    /// </summary>
    [Theory]
    [InlineData(new[] { "--exclude-regex", "flaky" }, new[] { "--exclude-regex", "flaky|slow" })]
    [InlineData(new[] { "-E=flaky" }, new[] { "-E=flaky|slow" })]
    [InlineData(new[] { "--exclude-regex flaky" }, new[] { "--exclude-regex flaky|slow" })]
    public void AnExclusionByNameTheArgsGive_TakesThoseGiven_WhereItStands(string[] args, string[] expected)
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: args, excludeArg: "-E", excludeJoin: "|"), cores: 6, filter: null, excludes: ["slow"]);

        Assert.Equal(expected, command.Arguments);
    }

    /// <summary>
    /// Another runner's exclusions in its args are lifted out and joined with those given where it declares a
    /// join, and left apart where it declares none: what it makes of one given twice is its own.
    /// </summary>
    [Theory]
    [InlineData("|", new[] { "test", "--exclude-tags", "manual|slow" })]
    [InlineData(null, new[] { "test", "--exclude-tags", "manual", "--exclude-tags", "slow" })]
    public void AnotherRunnersExclusionsInItsArgs_AreJoinedWhereItSaysSo(string? join, string[] expected)
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["test", "--exclude-tags", "manual"], excludeArg: "--exclude-tags", excludeJoin: join, runner: "dart"), cores: 6, filter: null, excludes: ["slow"]);

        Assert.Equal(expected, command.Arguments);
    }

    /// <summary>
    /// Test presets as ctest reads them. CMakePresets.json holds a preset that sets nothing, one for Linux that
    /// sets nothing either and one for each other operating system that leaves tests out by label, two that
    /// inherit each other, one inheriting a preset never declared, one that leaves tests out by label, one
    /// inheriting that, one inheriting
    /// it and setting it to nothing itself, one setting it to nothing and inheriting nothing, one inheriting a
    /// preset that sets it to nothing and then the one that leaves tests out by label, one that leaves tests out
    /// by name, one inheriting that and setting it to nothing itself, one that chooses tests by name, one that
    /// chooses them by label, one that takes the union of what its filters choose, one inheriting that, one
    /// inheriting it and saying it takes none, and one inheriting first a preset saying it takes none and then
    /// the union. CMakeUserPresets.json, including the first, holds a preset inheriting the one that leaves tests
    /// out by label.
    /// </summary>
    private static void WritePresets(TempDirectory temp)
    {
        temp.WriteFile("CMakePresets.json", """
            {
              "version": 6,
              "testPresets": [
                { "name": "plain" },
                { "name": "linux-plain" },
                { "name": "windows-plain", "filter": { "exclude": { "label": "manual" } } },
                { "name": "macos-plain", "filter": { "exclude": { "label": "manual" } } },
                { "name": "loopA", "inherits": "loopB" },
                { "name": "loopB", "inherits": "loopA" },
                { "name": "orphan", "inherits": "missing" },
                { "name": "labels", "filter": { "exclude": { "label": "manual" } } },
                { "name": "inheriting", "inherits": "labels" },
                { "name": "overriding", "inherits": ["labels"], "filter": { "exclude": { "label": "" } } },
                { "name": "clearing", "filter": { "exclude": { "label": "" } } },
                { "name": "emptyLabels", "hidden": true, "filter": { "exclude": { "label": "" } } },
                { "name": "firstEmpty", "inherits": ["emptyLabels", "labels"] },
                { "name": "names", "filter": { "exclude": { "name": "^flaky_" } } },
                { "name": "emptyName", "inherits": "names", "filter": { "exclude": { "name": "" } } },
                { "name": "including", "filter": { "include": { "name": "^unit_" } } },
                { "name": "labeled", "filter": { "include": { "label": "^fast$" } } },
                { "name": "union", "filter": { "include": { "useUnion": true } } },
                { "name": "unionInherited", "inherits": "union" },
                { "name": "unionOff", "inherits": "union", "filter": { "include": { "useUnion": false } } },
                { "name": "noUnion", "hidden": true, "filter": { "include": { "useUnion": false } } },
                { "name": "unionSecond", "inherits": ["noUnion", "union"] }
              ]
            }
            """);
        temp.WriteFile("CMakeUserPresets.json", """
            { "version": 6, "include": ["CMakePresets.json"], "testPresets": [ { "name": "user", "inherits": "labels" } ] }
            """);
    }

    private static PhysicalFileSystem FileSystem() => new(FilePermissionsFactory.Create());

    /// <summary>
    /// A file system that holds, on <paramref name="drive"/>, what <paramref name="root"/> holds, and nothing
    /// anywhere else: a drive this machine need not have.
    /// </summary>
    private sealed class OnDrive(string drive, string root) : PassThroughFileSystem(FileSystem())
    {
        public override bool FileExists(string path) => On(path) && base.FileExists(Mapped(path));

        public override string ReadAllText(string path) => base.ReadAllText(Mapped(path));

        private bool On(string path) => path.StartsWith(drive, StringComparison.OrdinalIgnoreCase);

        private string Mapped(string path) => On(path) ? Path.Combine(root, path[drive.Length..]) : path;
    }

    /// <summary>
    /// ctest given several exclusions apart, with no join to give them as one, is refused, naming the
    /// join to declare: it would leave out only a test every one matches, or only what the last does,
    /// and the leg would report a selection nobody asked for. The args' own count among them. One given
    /// beside nothing is passed as it is, and args that alone give several are the author's own, run as
    /// written.
    /// </summary>
    [Theory]
    [InlineData(new string[0], new[] { "slow", "gpu" }, null)]
    [InlineData(new[] { "-LE", "manual" }, new[] { "slow" }, null)]
    [InlineData(new[] { "--label-exclude", "manual" }, new[] { "slow" }, null)]
    [InlineData(new[] { "-LE=manual" }, new[] { "slow" }, null)]
    [InlineData(new string[0], new[] { "slow" }, new[] { "-LE", "slow" })]
    [InlineData(new[] { "-LE", "manual", "-LE", "nightly" }, new string[0], new[] { "-LE", "manual", "-LE", "nightly" })]
    public void CtestGivenSeveralExclusionsApart_IsRefused_NamingTheJoin(string[] args, string[] excludes, string[]? passed)
    {
        var invocation = Invocation(args: args, excludeArg: "-LE");

        if (passed is not null)
        {
            Assert.Equal(passed, TestInvocationResolver.CommandFor(invocation, cores: 6, filter: null, excludes: excludes).Arguments);
            return;
        }

        var refusal = Assert.Throws<HarnessException>(() => TestInvocationResolver.CommandFor(invocation, cores: 6, filter: null, excludes: excludes));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("\"excludeJoin\": \"|\"", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ctest is known by its program's name, however its path or case is spelled, and only ctest is
    /// refused several exclusions apart: another runner, whatever its excludeArg, is given each one.
    /// </summary>
    [Theory]
    [InlineData("ctest", true)]
    [InlineData("CTEST.EXE", true)]
    [InlineData("cmake/bin/ctest", true)]
    [InlineData("run-suite", false)]
    public void OnlyCtest_IsRefusedSeveralExclusionsApart(string runner, bool refused)
    {
        var invocation = Invocation(excludeArg: "-E", runner: runner);

        if (!refused)
        {
            Assert.Equal(["-E", "a", "-E", "b"], TestInvocationResolver.CommandFor(invocation, cores: 6, filter: null, excludes: ["a", "b"]).Arguments);
            return;
        }

        var refusal = Assert.Throws<HarnessException>(() => TestInvocationResolver.CommandFor(invocation, cores: 6, filter: null, excludes: ["a", "b"]));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
    }

    /// <summary>
    /// An empty excludeJoin gives each exclusion an excludeArg of its own, which lets an operating system's
    /// section whose runner leaves out each of several given apart say so over the shared section's join.
    /// </summary>
    [Fact]
    public void AnEmptyExcludeJoin_GivesEachExclusionItsOwn_OverTheSharedJoin()
    {
        var settings = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed", ExcludeArg = "-LE", ExcludeJoin = "|" },
            Linux = new TestInvocation { Runner = "dart", ExcludeArg = "--exclude-tags", ExcludeJoin = string.Empty },
        };

        Assert.Equal(
            ["--exclude-tags", "slow", "--exclude-tags", "gpu"],
            TestInvocationResolver.CommandFor(TestInvocationResolver.Resolve(settings, PlatformNames.Linux), cores: 6, filter: null, excludes: ["slow", "gpu"]).Arguments);
        Assert.Equal(
            ["-LE", "slow|gpu"],
            TestInvocationResolver.CommandFor(TestInvocationResolver.Resolve(settings, PlatformNames.Windows), cores: 6, filter: null, excludes: ["slow", "gpu"]).Arguments);
    }

    [Fact]
    public void AFilterWithNoFilterArg_IsRefused()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.CommandFor(Invocation(), cores: 6, filter: "parser", excludes: null));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("filterArg", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Several exclusions reach a runner that declares excludeJoin as one value joined by it, and one
    /// exclusion as itself: ctest, given -LE apart, leaves out only a test whose labels match every one,
    /// and given -E apart only what the last matches.
    /// </summary>
    [Theory]
    [InlineData(new[] { "slow", "gpu" }, new[] { "-LE", "slow|gpu" })]
    [InlineData(new[] { "slow" }, new[] { "-LE", "slow" })]
    public void SeveralExclusions_AreJoinedIntoOne_WhereTheInvocationSaysHow(string[] excludes, string[] expected)
    {
        var command = TestInvocationResolver.CommandFor(Invocation(excludeArg: "-LE", excludeJoin: "|"), cores: 6, filter: null, excludes: excludes);

        Assert.Equal(expected, command.Arguments);
    }

    /// <summary>An operating system's own excludeJoin replaces the shared one, as every other field does.</summary>
    [Fact]
    public void AnOperatingSystemsExcludeJoin_ReplacesTheSharedOne()
    {
        var settings = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed", ExcludeArg = "-LE", ExcludeJoin = "|" },
            Linux = new TestInvocation { ExcludeJoin = "," },
        };

        Assert.Equal(",", TestInvocationResolver.Resolve(settings, PlatformNames.Linux).ExcludeJoin);
        Assert.Equal("|", TestInvocationResolver.Resolve(settings, PlatformNames.Windows).ExcludeJoin);
    }

    [Fact]
    public void AnExclusionWithNoExcludeArg_IsRefused()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.CommandFor(Invocation(), cores: 6, filter: null, excludes: ["slow"]));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("excludeArg", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each label the tests to run must carry is passed through labelArg, beside a filter and an
    /// exclusion: a group ctest labels can be chosen, and not only left out. Measured on a consumer's
    /// tree, 31 guard tests could be selected only by a name pattern that happened to match them.
    /// </summary>
    [Fact]
    public void ALabel_IsPassedThroughLabelArg_BesideAFilterAndAnExclusion()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(filterArg: "-R", excludeArg: "-LE", labelArg: "-L"),
            cores: 6,
            filter: "parser",
            excludes: ["slow"],
            labels: ["git-state", "fast"]);

        Assert.Equal(["-R", "parser", "-LE", "slow", "-L", "git-state", "-L", "fast"], command.Arguments);
    }

    [Fact]
    public void ALabelWithNoLabelArg_IsRefused()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.CommandFor(Invocation(), cores: 6, filter: null, excludes: null, labels: ["git-state"]));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("labelArg", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host running one of a test run's legs is given every option that decides which tests run -
    /// the filter, each exclusion and each label - so it runs the suite this machine was asked for; an
    /// empty one among them, for the host to refuse as this machine does rather than run every test.
    /// </summary>
    [Fact]
    public void AHostRunningALeg_IsGivenTheFilterTheExclusionsAndTheLabels()
    {
        Assert.Equal(
            ["--filter", "auth", "--exclude", "slow", "--label", "git-state", "--label", "fast", "--no-build"],
            TestService.RemoteArguments("auth", ["slow"], ["git-state", "fast"], skipBuild: true, time: false));
        Assert.Equal(
            ["--filter", "", "--exclude", "", "--label", ""],
            TestService.RemoteArguments("", [""], [""], skipBuild: false, time: false));
        Assert.Empty(TestService.RemoteArguments(null, [], [], skipBuild: false, time: false));
    }

    /// <summary>An operating system's own labelArg replaces the shared one, as every other field does.</summary>
    [Fact]
    public void AnOperatingSystemsLabelArg_ReplacesTheSharedOne()
    {
        var settings = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed", LabelArg = "-L" },
            Linux = new TestInvocation { LabelArg = "--label-regex" },
        };

        Assert.Equal("--label-regex", TestInvocationResolver.Resolve(settings, PlatformNames.Linux).LabelArg);
        Assert.Equal("-L", TestInvocationResolver.Resolve(settings, PlatformNames.Windows).LabelArg);
    }

    /// <summary>
    /// An operating system's own remoteExcludes replace the shared ones, as every other field does, and an
    /// invocation declaring none has none.
    /// </summary>
    [Fact]
    public void AnOperatingSystemsRemoteExcludes_ReplaceTheSharedOnes()
    {
        var settings = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed", RemoteExcludes = ["git-state"] },
            Linux = new TestInvocation { RemoteExcludes = ["git-state", "gpu"] },
        };

        Assert.Equal(["git-state", "gpu"], TestInvocationResolver.Resolve(settings, PlatformNames.Linux).RemoteExcludes);
        Assert.Equal(["git-state"], TestInvocationResolver.Resolve(settings, PlatformNames.Windows).RemoteExcludes);
        Assert.Empty(TestInvocationResolver.Resolve(
            new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" } },
            PlatformNames.Linux).RemoteExcludes!);
    }

    /// <summary>
    /// A project that builds out of source keeps its tests in a directory this tool chooses:
    /// build/&lt;processor&gt;-&lt;toolchain&gt;-&lt;config&gt;, which differs per leg and so cannot be written
    /// down. Measured on a consumer's tree: ctest started at the tree root reported "No tests were
    /// found!!!" and exited 8 in under a fifth of a second, in a tree holding 2204 tests.
    /// </summary>
    [Fact]
    public void AnArgumentNamingTheBuildDirectory_IsHandedTheOneThisLegBuildsIn()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--test-dir", "{buildDir}", "--no-tests=error"]),
            cores: 6,
            filter: null,
            excludes: null,
            new LegPaths("/tree", "/tree/build/x86_64-gcc-debug"));

        Assert.Equal(["--test-dir", "/tree/build/x86_64-gcc-debug", "--no-tests=error"], command.Arguments);
    }

    /// <summary>
    /// The other half of the same need: a runner with no such argument, which only ever looks at
    /// the directory it was started in.
    /// </summary>
    [Fact]
    public void AnInvocationNamingAWorkingDirectory_StartsThere_AndOneNamingNoneKeepsTheTreeRoot()
    {
        var paths = new LegPaths("/tree", "/tree/build/x86_64-gcc-debug");

        var declared = TestInvocationResolver.CommandFor(
            Invocation(workingDirectory: "{buildDir}"), cores: 6, filter: null, excludes: null, paths);

        Assert.Equal("/tree/build/x86_64-gcc-debug", declared.WorkingDirectory);

        // Null rather than the tree root, so the caller keeps the one it already had: a test phase
        // written before this key existed ran at the tree root and still does.
        var silent = TestInvocationResolver.CommandFor(
            Invocation(), cores: 6, filter: null, excludes: null, paths);

        Assert.Null(silent.WorkingDirectory);
    }

    /// <summary>
    /// A relative working directory means what every other path in the configuration means, which
    /// is somewhere under the tree.
    /// </summary>
    [Fact]
    public void ARelativeWorkingDirectory_IsUnderTheTree()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(workingDirectory: "tests/integration"),
            cores: 6,
            filter: null,
            excludes: null,
            new LegPaths(Path.Combine("/tree"), "/tree/build/x86_64-gcc-debug"));

        Assert.Equal(Path.Combine("/tree", "tests/integration"), command.WorkingDirectory);
    }

    /// <summary>
    /// A test invocation names one of the leg's compilers as it names its build directory: its args, its coresArgs and
    /// its working directory are filled in with what the leg's build identified - and ahead of that build each stands
    /// as written, so the command made to be checked then is refused over nothing the build has yet to say.
    /// </summary>
    [Fact]
    public void AnInvocationNamingTheLegsCompiler_IsFilledInWithIt_AndStandsAsWrittenAheadOfTheBuild()
    {
        var invocation = Invocation(args: ["--cxx={compiler_CXX}"], coresArgs: ["-j", "{cores}", "--cc", "{compiler_C}"], workingDirectory: "{compiler_CXX}.d");
        var built = new LegPaths("/tree", "/tree/build/v")
        {
            Compilers = LegCompilers.Of(new Dictionary<string, LegCompiler>
            {
                ["C"] = LegCompiler.Of("/opt/gcc/bin/gcc"),
                ["CXX"] = LegCompiler.Of("/opt/gcc/bin/g++"),
            }),
        };

        var filled = TestInvocationResolver.CommandFor(invocation, cores: 6, filter: null, excludes: null, built);

        Assert.Equal(["--cxx=/opt/gcc/bin/g++", "-j", "6", "--cc", "/opt/gcc/bin/gcc"], filled.Arguments);
        Assert.Equal("/opt/gcc/bin/g++.d", filled.WorkingDirectory);

        var ahead = TestInvocationResolver.CommandFor(invocation, cores: 6, filter: null, excludes: null, built with { Compilers = LegCompilers.AheadOfTheBuild });

        Assert.Equal(["--cxx={compiler_CXX}", "-j", "6", "--cc", "{compiler_C}"], ahead.Arguments);
        Assert.Equal(Path.Combine("/tree", "{compiler_CXX}.d"), ahead.WorkingDirectory);

        var refused = Assert.Throws<HarnessException>(() => TestInvocationResolver.CommandFor(
            Invocation(workingDirectory: "{compiler_CXX}"),
            cores: 6,
            filter: null,
            excludes: null,
            built with { Compilers = LegCompilers.None("project 'app' is built by dotnet, which identifies no compiler") }));

        Assert.Equal("test.workingDirectory names '{compiler_CXX}', and project 'app' is built by dotnet, which identifies no compiler.", refused.Message);
    }

    /// <summary>
    /// Expanded after the filter and the exclusions are spliced in, so every argument the runner
    /// sees has been through one rule. Expanded before them, an argument that arrived from --filter
    /// could name a directory an argument from the configuration could not.
    /// </summary>
    [Fact]
    public void EveryArgumentGoesThroughOneRule_WhereverItCameFrom()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["{buildDir}"], filterArg: "-R", coresArgs: ["-j", "{cores}"]),
            cores: 6,
            filter: "{treeDir}",
            excludes: null,
            new LegPaths("/tree", "/tree/build/x86_64-gcc-debug"));

        // The core count is still the core count: two vocabularies, neither eating the other.
        Assert.Equal(["/tree/build/x86_64-gcc-debug", "-R", "/tree", "-j", "6"], command.Arguments);
    }

    /// <summary>
    /// With no leg in hand nothing is expanded, so a caller that only wants to see the command line
    /// gets it as written rather than a half-resolved one.
    /// </summary>
    [Fact]
    public void WithNoLegInHand_APlaceholderIsLeftAsWritten()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--test-dir", "{buildDir}"]), cores: 6, filter: null, excludes: null);

        Assert.Equal(["--test-dir", "{buildDir}"], command.Arguments);
    }

    private static ResolvedTestInvocation Invocation(
        IReadOnlyList<string>? args = null,
        string? filterArg = null,
        string? excludeArg = null,
        IReadOnlyList<string>? coresArgs = null,
        IReadOnlyList<string>? coresEnv = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null,
        string? labelArg = null,
        string? excludeJoin = null,
        string runner = "ctest")
        => new(
            runner,
            args ?? [],
            FilterArg: filterArg,
            ExcludeArg: excludeArg,
            ExcludeJoin: excludeJoin,
            LabelArg: labelArg,
            Cores: null,
            coresArgs ?? [],
            coresEnv ?? [],
            env ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "tests passed",
            CountPattern: null,
            workingDirectory);
}
