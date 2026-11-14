using System.Text.Json;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;

namespace DssHarness.Tests;

/// <summary>
/// Real builds, by the cmake on this machine with ninja or make and its own C compiler, of a project whose two sources
/// each fail to compile, on one core: without <c>--keep-going</c> the build stops at its first failing unit, and its line
/// names that one alone and says the rest were never built; with it, both are named and counted by file. Skipped where
/// this machine lacks one of the tools - and failed there instead, where it says it is meant to hold every build tool
/// (<see cref="BuildTools"/>).
/// </summary>
public sealed class KeepGoingEndToEndTests
{
    /// <summary>The two units the project fails to build, by source as a leg's line names them.</summary>
    private static readonly string[] Sources = ["a.c", "b.c"];

    /// <summary>
    /// A ninja build stopped at its first failing unit names that one unit by its source, and says how many of the steps it
    /// had to run it ran - one of three - and that the two it never reached were not built, naming --keep-going, in text
    /// and in --json; under --keep-going it names both, counted by file, and that the one step it did not run, the archive,
    /// needs one that failed - in the table's block below it as well.
    /// </summary>
    [Fact]
    public async Task ANinjaBuild_NamesItsFirstFailingUnitAlone_AndUnderKeepGoingEveryOne()
    {
        var harness = new HarnessFactory();
        var compiler = OperatingSystem.IsWindows() ? "gcc" : "cc";

        BuildTools.Need(harness.ProcessRunner, "a real build", "cmake", "ninja", compiler);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        using var clock = new ClockWatch();

        await WriteProjectAsync(harness, temp, "Ninja", compiler, token);

        try
        {
            var stopped = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", temp.Path], token);
            var first = FailedUnitsOf(stopped);
            var unit = Assert.Single(first.Units);

            // Which of the two ninja reaches first is its own to order; that it stops there is the rule.
            Assert.Contains(unit.Source, Sources);
            Assert.Equal(("ninja", 1, false, true, 1, 3), (first.Tool, first.Count, first.KeptGoing, first.StopsAtFirst, first.StepsRun, first.StepsPlanned));
            Assert.Equal(
                "1 unit(s) failed to build; ninja stops at its first failures, and ran 1 of the 3 steps it had to run: the 2 it never reached were not "
                + "built, and say nothing of the code - --keep-going builds past failing units",
                first.Said);

            var text = await CliRunner.RunAsync(["build", "--legs", "native", "--keep-going", "-C", temp.Path], token);

            Assert.True(text.ExitCode == Verdicts.ExitCodeFor(LegVerdict.Failed), text.StandardError + text.StandardOutput);
            Assert.Contains(
                "2 unit(s) failed to build; ninja went on past them and ran 2 of the 3 steps it had to run: the one it did not run needs one that failed",
                text.StandardOutput,
                StringComparison.Ordinal);
            Assert.Equal(Sources.Select(source => "  " + source), BlockBelow(text.StandardOutput, "native: 2 unit(s) failed to build").Order(StringComparer.Ordinal));

            var kept = FailedUnitsOf(await CliRunner.RunAsync(["build", "--legs", "native", "--keep-going", "--json", "-C", temp.Path], token));

            Assert.Equal(Sources, kept.Units.Select(failed => failed.Source).Order(StringComparer.Ordinal));
            Assert.Equal(("ninja", 2, true, true, 2, 3), (kept.Tool, kept.Count, kept.KeptGoing, kept.StopsAtFirst, kept.StepsRun, kept.StepsPlanned));
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// A make build stopped at its first failing unit names that unit by the object make says it was building - a.c's, the
    /// first make reaches - and that a unit it never reached was not built, naming --keep-going; under --keep-going, which
    /// gives make -k, it names both. make counts nothing of how far it got, and the line says none.
    /// </summary>
    [Fact]
    public async Task AMakeBuild_NamesItsFirstFailingUnitAlone_AndUnderKeepGoingEveryOne()
    {
        var harness = new HarnessFactory();
        var compiler = OperatingSystem.IsWindows() ? "gcc" : "cc";
        var (generator, make, objects) = OperatingSystem.IsWindows() ? ("MinGW Makefiles", "mingw32-make", ".obj") : ("Unix Makefiles", "make", ".o");

        BuildTools.Need(harness.ProcessRunner, "a real build with make", "cmake", make, compiler);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        using var clock = new ClockWatch();

        await WriteProjectAsync(harness, temp, generator, compiler, token);

        try
        {
            var first = FailedUnitsOf(await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", temp.Path], token));

            // The make CMake found for the generator, as its cache records it, names the tool: gmake where a system keeps
            // GNU make under that name too, as Ubuntu does.
            var tool = MakeProgramOf(harness, temp);

            Assert.Contains(tool, new[] { make, "gmake" });
            Assert.Equal([new FailedUnit(null, $"CMakeFiles/probe.dir/a.c{objects}")], first.Units);
            Assert.Equal((tool, false, true, (int?)null, (int?)null), (first.Tool, first.KeptGoing, first.StopsAtFirst, first.StepsRun, first.StepsPlanned));
            Assert.Equal(
                "1 unit(s) failed to build; " + tool + " stops at its first failures, so a unit it never reached was not built, and says nothing of the code - "
                + "--keep-going builds past failing units",
                first.Said);

            var kept = FailedUnitsOf(await CliRunner.RunAsync(["build", "--legs", "native", "--keep-going", "--json", "-C", temp.Path], token));

            Assert.Equal([$"CMakeFiles/probe.dir/a.c{objects}", $"CMakeFiles/probe.dir/b.c{objects}"], kept.Units.Select(unit => unit.Step).Order(StringComparer.Ordinal));
            Assert.Equal($"2 unit(s) failed to build; {tool} went on past them", kept.Said);
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// A build that runs another in one of its steps - an external project, whose own static library is named as this
    /// build's and compiled from a source named as this build's, so its failing object is one this build's manifest names
    /// too - names that step as its unit, and none of the nested build's lines: never the nested object as this build's
    /// own source, and, under ninja, how far this build got by its own status, not the nested build's. Under ninja and under
    /// make, kept going or not, on one core.
    /// </summary>
    [Theory]
    [InlineData("Ninja", false)]
    [InlineData("Ninja", true)]
    [InlineData("Makefiles", false)]
    [InlineData("Makefiles", true)]
    public async Task ANestedBuildsLines_AreNeverReadAsThisBuildsOwn(string generator, bool keepGoing)
    {
        var harness = new HarnessFactory();
        var compiler = OperatingSystem.IsWindows() ? "gcc" : "cc";
        var (named, tool) = generator == "Ninja"
            ? ("Ninja", "ninja")
            : OperatingSystem.IsWindows() ? ("MinGW Makefiles", "mingw32-make") : ("Unix Makefiles", "make");

        BuildTools.Need(harness.ProcessRunner, "a real build of an external project", "cmake", tool, compiler);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        using var clock = new ClockWatch();

        await WriteNestedProjectAsync(harness, temp, named, compiler, token);

        try
        {
            string[] arguments = keepGoing ? ["build", "--legs", "native", "--keep-going", "--json", "-C", temp.Path] : ["build", "--legs", "native", "--json", "-C", temp.Path];
            var said = FailedUnitsOf(await CliRunner.RunAsync(arguments, token));

            // ninja's own status counts this build's steps - an external project's eight among them - where the nested
            // build's, printed last as the failing step's output, counts its two.
            if (generator == "Ninja")
            {
                Assert.True(said.StepsPlanned > 4 && said.StepsRun <= said.StepsPlanned, $"{said.StepsRun} of {said.StepsPlanned}: {said.Said}");
            }

            Assert.Equal([new FailedUnit(null, "ext-prefix/src/ext-stamp/ext-build")], said.Units);
            Assert.Equal(keepGoing, said.KeptGoing);
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// The ninja on this machine, its output piped, holds back every other step's lines while a step in the console pool
    /// runs - here one that sleeps while the others end, one of them failing after two that pass - and writes at its end
    /// only the failed step's status and lines and the last one's status, so the failed step's status is written at a count
    /// the build never wrote before it; and a step after the console step fails too. Each failure is the build's own, and
    /// how far it got is not said, for whether the console step ended before the last status is not written.
    /// </summary>
    [Fact]
    public async Task WhatARealNinjaHoldsBackWhileAConsoleStepRuns_IsTheBuildsOwn()
    {
        var harness = new HarnessFactory();

        BuildTools.Need(harness.ProcessRunner, "a real ninja build with a console step", "cmake", "ninja");

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var cmake = "\"" + harness.ProcessRunner.FindExecutable("cmake")!.Replace("$", "$$", StringComparison.Ordinal) + "\"";

        temp.WriteFile(
            "build.ninja",
            $"rule cons\n  command = {cmake} -E sleep 4\n  description = CONSOLE $out\n  pool = console\n"
            + $"rule quick\n  command = {cmake} -E touch $out\n  description = QUICK $out\n"
            + $"rule bad\n  command = {cmake} -E false\n  description = BAD $out\n"
            + "build c1: cons\nbuild q1: quick\nbuild q2: quick\nbuild b1: bad q1 q2\nbuild after: bad c1\n");

        var built = await harness.ProcessRunner.RunAsync(
            new Core.Processes.ProcessRequest { FileName = harness.ProcessRunner.FindExecutable("ninja")!, Arguments = ["-k", "0", "-j", "8"], WorkingDirectory = temp.Path },
            token);
        var lines = (built.StandardOutput + "\n" + built.StandardError).Split('\n');
        var own = Ninja.Own(lines, "ninja", NinjaManifest.Read(harness.FileSystem, temp.Path));

        Assert.True(built.ExitCode != 0, built.StandardOutput);
        Assert.Equal(["b1=Own", "after=Own"], own.Said.Select(line => $"{line.Step}={line.Whose}"));
        Assert.Null(own.Progress);
    }

    /// <summary>
    /// A tree whose one leg builds, with <paramref name="generator"/> and <paramref name="compiler"/> on one core, a static
    /// library 'probe' of a source that compiles, and an external project, built after it, whose own static library
    /// 'probe' is of a source of the same name that fails to compile - committed, its build directory ignored.
    /// </summary>
    private static async Task WriteNestedProjectAsync(HarnessFactory harness, TempDirectory temp, string generator, string compiler, CancellationToken token)
    {
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Defaults = new HarnessDefaults { BuildCores = 1 },
            Toolchains = { ["cc"] = new ToolchainConfig { Platforms = [platform.PlatformKey], Generator = generator, Env = { ["CC"] = compiler } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Projects = { new ProjectConfig { Name = "probe", Type = "cmake", Path = ".", BuildOutputs = [BuildOutput.Everywhere("libprobe.a")] } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
        });

        temp.WriteFile(".gitignore", "build/\n");
        temp.WriteFile("a.c", "int first(void) { return 1; }\n");
        temp.WriteFile(
            "CMakeLists.txt",
            "cmake_minimum_required(VERSION 3.20)\nproject(probe C)\ninclude(ExternalProject)\nadd_library(probe STATIC a.c)\n"
            + "ExternalProject_Add(ext SOURCE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/ext DEPENDS probe INSTALL_COMMAND \"\" CMAKE_ARGS -DCMAKE_C_COMPILER=${CMAKE_C_COMPILER})\n");
        temp.WriteFile(Path.Combine("ext", "a.c"), "int nested(void) { return undeclared_in_nested; }\n");
        temp.WriteFile(Path.Combine("ext", "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(ext C)\nadd_library(probe STATIC a.c)\n");
        await harness.CommitAllAsync(temp.Path, "a nested build that fails", token);
    }

    /// <summary>
    /// A tree whose one leg builds, with <paramref name="generator"/> and <paramref name="compiler"/> on one core, a static
    /// library of two sources that each fail to compile - committed, its build directory ignored.
    /// </summary>
    private static async Task WriteProjectAsync(HarnessFactory harness, TempDirectory temp, string generator, string compiler, CancellationToken token)
    {
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Defaults = new HarnessDefaults { BuildCores = 1 },
            Toolchains = { ["cc"] = new ToolchainConfig { Platforms = [platform.PlatformKey], Generator = generator, Env = { ["CC"] = compiler } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Projects = { new ProjectConfig { Name = "probe", Type = "cmake", Path = ".", BuildOutputs = [BuildOutput.Everywhere("libprobe.a")] } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
        });

        // Ignored, as a repository ignores its builds: committed, a build's own files would be inputs it changes.
        temp.WriteFile(".gitignore", "build/\n");
        temp.WriteFile("a.c", "int first(void) { return undeclared_in_a; }\n");
        temp.WriteFile("b.c", "int second(void) { return undeclared_in_b; }\n");
        temp.WriteFile("CMakeLists.txt", "cmake_minimum_required(VERSION 3.20)\nproject(probe C)\nadd_library(probe STATIC a.c b.c)\n");
        await harness.CommitAllAsync(temp.Path, "two failing units", token);
    }

    /// <summary>
    /// The make the one leg's build ran, by its file name without extension, as the build directory's CMake cache records
    /// it - read by the reader the build reads it with.
    /// </summary>
    private static string MakeProgramOf(HarnessFactory harness, TempDirectory temp)
    {
        var cache = Assert.Single(Directory.EnumerateFiles(temp.Combine("build"), BuildDirectoryGuard.CMakeCacheFileName, SearchOption.AllDirectories));
        var configured = new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions).Read(Path.GetDirectoryName(cache)!);

        return Path.GetFileNameWithoutExtension(Assert.IsType<string>(configured?.MakeProgram));
    }

    /// <summary>The one leg's failed units, as a run's --json document says them, the run having failed its leg.</summary>
    private static UnitsSaid FailedUnitsOf(Core.Processes.ProcessResult result)
    {
        Assert.True(result.ExitCode == Verdicts.ExitCodeFor(LegVerdict.Failed), result.StandardError + result.StandardOutput);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());
        var failed = leg.GetProperty("failedUnits");

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());

        // Which units failed was read: a document leaves out what holds nothing.
        Assert.False(failed.TryGetProperty("unread", out _));

        return new UnitsSaid(
            failed.GetProperty("tool").GetString()!,
            failed.GetProperty("count").GetInt32(),
            [.. failed.GetProperty("units").EnumerateArray().Select(unit => new FailedUnit(
                unit.TryGetProperty("source", out var source) ? source.GetString() : null,
                unit.GetProperty("step").GetString()!))],
            failed.GetProperty("keptGoing").GetBoolean(),
            failed.GetProperty("stopsAtFirst").GetBoolean(),
            failed.TryGetProperty("stepsRun", out var run) ? run.GetInt32() : null,
            failed.TryGetProperty("stepsPlanned", out var planned) ? planned.GetInt32() : null,
            failed.GetProperty("said").GetString()!);
    }

    /// <summary>
    /// The lines of a build's <paramref name="text"/> after the one that is <paramref name="heading"/>, up to the next blank
    /// one, each without the command's name the build writes before every line of its own.
    /// </summary>
    private static IEnumerable<string> BlockBelow(string text, string heading)
    {
        const string Said = BuildService.CommandName + ": ";

        return text.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.StartsWith(Said, StringComparison.Ordinal) ? line[Said.Length..] : line)
            .SkipWhile(line => line != heading)
            .Skip(1)
            .TakeWhile(line => line.Trim().Length > 0);
    }

    /// <summary>What a leg's failedUnits member says.</summary>
    private sealed record UnitsSaid(string Tool, int Count, IReadOnlyList<FailedUnit> Units, bool KeptGoing, bool StopsAtFirst, int? StepsRun, int? StepsPlanned, string Said);
}
