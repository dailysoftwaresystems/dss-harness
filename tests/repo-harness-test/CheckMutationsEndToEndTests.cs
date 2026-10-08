using System.Globalization;
using System.Text.Json;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;

namespace RepoHarness.Tests;

/// <summary>
/// The built CLI's <c>check-mutations</c>, end to end through real builds by the CMake, Ninja and C++ compiler on this
/// machine: the fixture this tool carries, swept as a repository's own project and as a self-test, each of its arms
/// reaching the verdict it is designed to reach. Skipped where this machine has no CMake, no Ninja or no C or C++
/// compiler, and where its builds did not run on an honest clock.
/// </summary>
public sealed class CheckMutationsEndToEndTests
{
    /// <summary>The C and C++ compilers a leg here builds with: MinGW's on Windows, the system's own elsewhere.</summary>
    private static readonly (string C, string Cxx) Compilers = OperatingSystem.IsWindows() ? ("gcc", "g++") : ("cc", "c++");

    /// <summary>
    /// The fixture, swept as a repository's own project, reaches each arm's designed verdict, its leg failing as its worst
    /// arm does; each arm's record is written beside its logs; and a claim an earlier sweep died holding on a worker is
    /// cleared, saying so. With the sweep's lock held by another run, the leg is refused-locked, naming that run - and a
    /// build of the same leg, which the lock never holds off, still builds.
    /// </summary>
    [Fact]
    public async Task EachArm_ReachesItsDesignedVerdict_AnAbandonedClaimIsCleared_AndAHeldLockRefusesOnlyTheSweep()
    {
        var harness = new HarnessFactory();

        SkipUnlessThisMachineBuilds(harness);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        // Real builds, which ninja orders by the times of the files they write, and arms dated forward of them: on a clock
        // that steps, what they do proves nothing here. Watched from before the files they build from are written.
        using var clock = new ClockWatch();

        var repository = temp.Combine("r");
        Directory.CreateDirectory(repository);

        await harness.InitializeHarnessAsync(repository, token, Config(harness.Platform, MutationFixture.SettingsFor(new MutationSettings())));

        foreach (var (path, bytes) in MutationFixture.Files())
        {
            var file = Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }

        // Ignored, as a repository ignores its builds: committed, a build's own files would be inputs it changes.
        File.AppendAllText(Path.Combine(repository, ".gitignore"), "build/\n");
        await harness.CommitAllAsync(repository, "the fixture", token);

        var context = await harness.ContextLoader.LoadAsync(repository, token);
        var variant = VariantKey.For(context.Config, context.Config.Legs["native"], harness.Platform.PlatformKey);
        var claim = MutationWorkers.PathOf(context.Layout.RepositoryRoot, variant, 1) + MutationWorkers.ClaimSuffix;

        File.WriteAllText(
            claim,
            "{ \"machine\": \"" + Environment.MachineName + "\", \"processId\": " + (int.MaxValue - 1).ToString(CultureInfo.InvariantCulture)
            + ", \"processStamp\": \"gone\", \"runId\": \"20250101-120000-deadbeef\", \"takenUtc\": \"2025-01-01T12:00:00Z\" }");

        try
        {
            var swept = await CliRunner.RunAsync(["check-mutations", "--legs", "native", "--json", "-C", repository], token);
            var said = swept.StandardError + swept.StandardOutput;

            using (var document = JsonDocument.Parse(swept.StandardOutput))
            {
                var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());
                var arms = Arms(leg);

                Assert.True(
                    MutationFixture.Designed.All(pair => arms.TryGetValue(pair.Key, out var reached) && reached.Verdict == Verdicts.Display(pair.Value)),
                    said);
                Assert.Equal(Verdicts.Display(LegVerdict.Failed), leg.GetProperty("verdict").GetString());
                Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.Failed), swept.ExitCode);

                foreach (var arm in leg.GetProperty("arms").EnumerateArray())
                {
                    Assert.True(File.Exists(Path.Combine(arm.GetProperty("records").GetString()!, MutationRecords.ArmRecordFileName)), said);
                }
            }

            Assert.Contains("An earlier run was abandoned", swept.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(claim), said);

            var held = await harness.RunLock.AcquireAsync(
                context.Layout,
                MutationWorkers.SweepLock(HostId.Local, context.Layout.RepositoryRoot, variant, RunId.New(), MutationService.CommandName),
                token);

            await using (held)
            {
                var refused = await CliRunner.RunAsync(["check-mutations", "--legs", "native", "--json", "-C", repository], token);

                using (var document = JsonDocument.Parse(refused.StandardOutput))
                {
                    var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

                    Assert.Equal(Verdicts.Display(LegVerdict.RefusedLocked), leg.GetProperty("verdict").GetString());
                    Assert.Contains(held.Entry.Holder.RunId, leg.GetProperty("detail").GetString()!, StringComparison.Ordinal);
                }

                var built = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", repository], token);

                Assert.True(built.ExitCode == HarnessExit.Success, built.StandardError + built.StandardOutput);
            }
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// A self-test sweeps the fixture this tool carries through the leg's own toolchain, in a repository declaring no arm
    /// of its own: every arm reaches the verdict it is designed to reach, each passed saying so, and the leg passes. The
    /// fixture is written among this user's own data for this tool; its workers are kept beside the leg's own tree, never
    /// beside the fixture, and a clean of the leg removes them.
    /// </summary>
    [Fact]
    public async Task ASelfTest_HoldsEachArmOfTheFixtureToItsDesign_ThroughTheLegsToolchain()
    {
        var harness = new HarnessFactory();

        SkipUnlessThisMachineBuilds(harness);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        // Real builds, which ninja orders by the times of the files they write: on a clock that steps, what they do
        // proves nothing here.
        using var clock = new ClockWatch();

        var repository = temp.Combine("r");
        Directory.CreateDirectory(repository);

        var config = Config(harness.Platform, new MutationSettings());

        await harness.InitializeHarnessAsync(repository, token, config);
        await harness.CommitAllAsync(repository, "a repository declaring no arm", token);

        var (environment, data) = OwnUserData(temp);
        var fixture = Path.Combine(data, ToolPackage.Command, MutationFixture.DirectoryName);
        var variant = VariantKey.For(config, config.Legs["native"], harness.Platform.PlatformKey);

        try
        {
            var result = await CliRunner.RunAsync(["check-mutations", "--self-test", "--legs", "native", "--json", "-C", repository], token, environment: environment);
            var said = result.StandardError + result.StandardOutput;

            Assert.True(result.ExitCode == HarnessExit.Success, said);

            using (var document = JsonDocument.Parse(result.StandardOutput))
            {
                var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());
                var arms = Arms(leg);

                Assert.Equal(MutationFixture.Designed.Keys.Order(StringComparer.Ordinal), arms.Keys.Order(StringComparer.Ordinal));
                Assert.All(MutationFixture.Designed, pair =>
                {
                    Assert.Equal(Verdicts.Display(LegVerdict.Passed), arms[pair.Key].Verdict);
                    Assert.StartsWith($"{Verdicts.Display(pair.Value)}, as designed: ", arms[pair.Key].Detail, StringComparison.Ordinal);
                });
            }

            Assert.All(MutationFixture.Files(), pair => Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(fixture, pair.Key.Replace('/', Path.DirectorySeparatorChar)))));

            var worker = MutationWorkers.Of(repository, variant, selfTest: true).PathOf(1);

            Assert.True(Directory.Exists(worker), said);
            Assert.False(Directory.Exists(MutationWorkers.PathOf(fixture, variant, 1)), "A self-test's worker was kept beside the fixture, where every repository's would be one.");

            var cleaned = await CliRunner.RunAsync(["clean", "--legs", "native", "--json", "-C", repository], token, environment: environment);

            Assert.True(cleaned.ExitCode == HarnessExit.Success, cleaned.StandardError + cleaned.StandardOutput);
            Assert.False(Directory.Exists(worker), cleaned.StandardError + cleaned.StandardOutput);
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// A leg whose build fetches a dependency has each worker of its sweep given its own copy of what that build
    /// fetched, and configured with it, fetching nothing: so the worker's build reads nothing of the leg's own build
    /// directory, and a sweep after that directory is gone - cleaned from under it - still drives its arm, the worker
    /// then fetching as the leg would, and keeping nothing it is no longer given.
    /// </summary>
    [Fact]
    public async Task AWorker_IsBuiltFromItsOwnCopyOfWhatTheLegsBuildFetched_NeverTheLegsBuildDirectory()
    {
        var harness = new HarnessFactory();

        SkipUnlessThisMachineBuilds(harness);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        using var clock = new ClockWatch();

        var repository = temp.Combine("r");
        Directory.CreateDirectory(repository);

        await harness.InitializeHarnessAsync(repository, token, Config(harness.Platform, MutationFixture.SettingsFor(new MutationSettings())));

        foreach (var (path, bytes) in MutationFixture.Files())
        {
            var file = Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }

        // A dependency FetchContent fetches from a directory of the tree, so nothing is asked of the network: it lands in
        // the build directory, as one fetched from anywhere does.
        Directory.CreateDirectory(Path.Combine(repository, "vendor", "plumb", "include"));
        File.WriteAllText(Path.Combine(repository, "vendor", "plumb", "CMakeLists.txt"), "add_library(plumb INTERFACE)\ntarget_include_directories(plumb INTERFACE include)\n");
        File.WriteAllText(Path.Combine(repository, "vendor", "plumb", "include", "plumb.hpp"), "#pragma once\n");
        File.AppendAllText(
            Path.Combine(repository, "CMakeLists.txt"),
            "\ninclude(FetchContent)\nFetchContent_Declare(plumb URL \"${CMAKE_CURRENT_SOURCE_DIR}/vendor/plumb\")\n"
            + "FetchContent_MakeAvailable(plumb)\ntarget_link_libraries(fixture PUBLIC plumb)\n");
        File.AppendAllText(Path.Combine(repository, ".gitignore"), "build/\n");
        await harness.CommitAllAsync(repository, "the fixture, fetching a dependency", token);

        var context = await harness.ContextLoader.LoadAsync(repository, token);
        var variant = VariantKey.For(context.Config, context.Config.Legs["native"], harness.Platform.PlatformKey);
        var legsBuild = variant.DirectoryUnder(context.Layout.RepositoryRoot);
        var worker = MutationWorkers.PathOf(context.Layout.RepositoryRoot, variant, 1);
        var kept = FetchedSources.KeptIn(worker, "PLUMB");

        try
        {
            var built = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", repository], token);

            Assert.True(built.ExitCode == HarnessExit.Success, built.StandardError + built.StandardOutput);
            Assert.True(File.Exists(Path.Combine(legsBuild, "_deps", "plumb-src", "CMakeLists.txt")), "The leg's build fetched nothing into its build directory.");

            var swept = await CliRunner.RunAsync(["check-mutations", "--legs", "native", "--arms", "charge-bound", "--json", "-C", repository], token);
            var said = swept.StandardError + swept.StandardOutput;

            Assert.True(swept.ExitCode == HarnessExit.Success, said);
            Assert.Equal("#pragma once\n", File.ReadAllText(Path.Combine(kept, "include", "plumb.hpp")));

            var cache = File.ReadAllLines(Path.Combine(variant.DirectoryUnder(worker), BuildDirectoryGuard.CMakeCacheFileName));
            var pointed = Assert.Single(cache, line => line.StartsWith("FETCHCONTENT_SOURCE_DIR_PLUMB:", StringComparison.Ordinal));

            Assert.EndsWith("/.harness-config/deps/plumb", pointed, StringComparison.Ordinal);
            Assert.Contains(cache, line => line.StartsWith("FETCHCONTENT_FULLY_DISCONNECTED:", StringComparison.Ordinal) && line.EndsWith("=ON", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(variant.DirectoryUnder(worker), "_deps", "plumb-src")), "The worker fetched what it was given.");

            // The leg's build directory gone, as a clean of the leg leaves it: nothing says what its build fetched, so the
            // worker is given nothing, keeps nothing, and fetches as the leg would.
            harness.FileSystem.DeleteDirectory(legsBuild);

            var again = await CliRunner.RunAsync(["check-mutations", "--legs", "native", "--arms", "charge-bound", "--json", "-C", repository], token);

            Assert.True(again.ExitCode == HarnessExit.Success, again.StandardError + again.StandardOutput);
            Assert.False(Directory.Exists(FetchedSources.KeptIn(worker)), "The worker kept dependency sources it was no longer given.");
            Assert.True(File.Exists(Path.Combine(variant.DirectoryUnder(worker), "_deps", "plumb-src", "CMakeLists.txt")), "The worker did not fetch as the leg would.");
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>Skips the test where this machine lacks a program a real build of the fixture needs.</summary>
    private static void SkipUnlessThisMachineBuilds(HarnessFactory harness)
        => Assert.SkipUnless(
            new[] { "cmake", "ninja", Compilers.C, Compilers.Cxx }.All(program => harness.ProcessRunner.FindExecutable(program) is not null),
            $"This machine lacks cmake, ninja, {Compilers.C} or {Compilers.Cxx}, which a real build of the fixture needs.");

    /// <summary>The one leg this machine runs, built by its C and C++ compilers with the Ninja generator.</summary>
    private static LegConfig Leg(IHostPlatform platform)
        => new() { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" };

    /// <summary>
    /// A repository whose one project is built from its root, by one leg this machine runs, its worker paths reckoned by
    /// the fixture's own longest - the default reserve is a large project's - and its mutation testing <paramref name="mutations"/>.
    /// </summary>
    private static HarnessConfig Config(IHostPlatform platform, MutationSettings mutations) => new()
    {
        Toolchains =
        {
            ["cc"] = new ToolchainConfig { Platforms = [platform.PlatformKey], Generator = "Ninja", Env = { ["CC"] = Compilers.C, ["CXX"] = Compilers.Cxx } },
        },
        BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
        Projects = { MutationFixture.Project },
        Legs = { ["native"] = Leg(platform) },
        Worktrees = new WorktreeSettings { PathBudgetReserve = MutationFixture.LongestBuildPath },
        Mutations = mutations,
    };

    /// <summary>Each arm beneath <paramref name="leg"/>'s line in a ledger's JSON, by its id: its verdict and its detail.</summary>
    private static Dictionary<string, (string? Verdict, string? Detail)> Arms(JsonElement leg)
        => leg.GetProperty("arms").EnumerateArray().ToDictionary(
            arm => arm.GetProperty("arm").GetString()!,
            arm => (arm.GetProperty("verdict").GetString(), arm.GetProperty("detail").GetString()),
            StringComparer.Ordinal);

    /// <summary>
    /// An environment giving the CLI a user's data of its own below <paramref name="temp"/>, kept short so a worker's build
    /// below it stays within the path limit, and the directory this tool's own data is then kept in.
    /// </summary>
    private static (Dictionary<string, string?> Environment, string Data) OwnUserData(TempDirectory temp)
    {
        var home = temp.Combine("h");
        var local = temp.Combine("d");
        var share = temp.Combine("s");

        Directory.CreateDirectory(home);

        var data = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support")
            : OperatingSystem.IsWindows() ? local
            : share;

        return (
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HOME"] = home,
                ["XDG_DATA_HOME"] = share,
                ["LOCALAPPDATA"] = local,
            },
            data);
    }
}
