using System.Text.Json;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The built CLI's <c>run</c> and <c>test</c> naming a leg's own compiler, end to end through a real build by the CMake,
/// Ninja and C and C++ compilers on this machine. Skipped where this machine lacks one of them - failed there instead,
/// where it says it is meant to hold every build tool (<see cref="BuildTools"/>) - and where its build did not run on an
/// honest clock.
/// </summary>
public sealed class CompilerNameEndToEndTests
{
    /// <summary>The C and C++ compilers a leg here builds with: MinGW's on Windows, the system's own elsewhere.</summary>
    private static readonly (string C, string Cxx) Compilers = OperatingSystem.IsWindows() ? ("gcc", "g++") : ("cc", "c++");

    /// <summary>
    /// A step written as the leg's C++ compiler alone starts the very program CMake recorded for the leg's build,
    /// though nothing declares it under 'tools', and the compiler answers; a step naming both in its arguments is handed
    /// both paths; and the run built the leg first to know them, in a repository whose runner requires no build. A test
    /// invocation naming one is checked before the build and handed the same path once the leg is built.
    /// </summary>
    [Fact]
    public async Task ARunLineAndATestInvocation_AreHandedTheCompilerTheLegsBuildIdentified()
    {
        var harness = new HarnessFactory();

        BuildTools.Need(harness.ProcessRunner, "a real build of the fixture", "cmake", "ninja", Compilers.C, Compilers.Cxx);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        // A real build, which ninja orders by the times of the files it writes: on a clock that steps, it proves nothing.
        using var clock = new ClockWatch();

        var repository = temp.Combine("r");
        Directory.CreateDirectory(repository);

        var config = Config(harness.Platform);

        await harness.InitializeHarnessAsync(repository, token, config);

        foreach (var (path, bytes) in MutationFixture.Files())
        {
            var file = Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar));

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, bytes);
        }

        var action = Path.Combine(repository, ".harness-config", "runner", "actions", "census", "census.yml");

        Directory.CreateDirectory(Path.GetDirectoryName(action)!);
        File.WriteAllText(
            action,
            "name: census\nsteps:\n  - name: version\n    run: '{compiler_CXX} --version'\n"
            + "  - name: told\n    run: cmake -E echo cc={compiler_C} cxx={compiler_CXX}\n");

        // Ignored, as a repository ignores its builds: committed, a build's own files would be inputs it changes.
        File.AppendAllText(Path.Combine(repository, ".gitignore"), "build/\n");
        await harness.CommitAllAsync(repository, "the fixture, and a census of its compilers", token);

        var build = VariantKey.For(config, config.Legs["native"], harness.Platform.PlatformKey).DirectoryUnder(repository);

        try
        {
            Assert.False(Directory.Exists(build), "Nothing built the leg before the run.");

            var ran = await CliRunner.RunAsync(["run", "census", "--legs", "native", "--json", "-C", repository], token);
            var said = ran.StandardError + ran.StandardOutput;

            Assert.True(ran.ExitCode == HarnessExit.Success, said);

            // What CMake recorded of the build this run made, which is what the names are filled in with.
            var named = new CMakeToolchainReader(harness.FileSystem).Named(build);
            var c = named["C"].Program;
            var cxx = named["CXX"].Program;

            Assert.True(cxx is not null && c is not null, $"{named["C"].Problem}; {named["CXX"].Problem}");
            Assert.True(Path.IsPathFullyQualified(cxx) && File.Exists(cxx), cxx);
            Assert.DoesNotContain('/', OperatingSystem.IsWindows() ? cxx : string.Empty);

            var logs = Logs(ran.StandardOutput);

            Assert.Contains(logs, log => log.Contains($"# command {cxx} --version") && log.Any(line => line.StartsWith("# exit 0 after ", StringComparison.Ordinal)));
            Assert.Contains(logs, log => log.Contains($"cc={c} cxx={cxx}"));

            var tested = await CliRunner.RunAsync(["test", "--legs", "native", "--json", "-C", repository], token);

            Assert.True(tested.ExitCode == HarnessExit.Success, tested.StandardError + tested.StandardOutput);
            Assert.Contains(Logs(tested.StandardOutput), log => log.Contains($"tests passed with {cxx}"));
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its build did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>Every log the leg of the run a ledger's JSON reports wrote, each as its lines.</summary>
    private static List<string[]> Logs(string ledger)
    {
        using var document = JsonDocument.Parse(ledger);

        var leg = Path.Combine(document.RootElement.GetProperty("runDirectory").GetString()!, "native");

        return [.. Directory.EnumerateFiles(leg, "*.log", SearchOption.AllDirectories).Select(File.ReadAllLines)];
    }

    /// <summary>
    /// A repository whose one project - the fixture - is built from its root by one leg this machine runs, with the
    /// Ninja generator and its C and C++ compilers; a runner that requires no build; and tests that only say which
    /// compiler they were handed.
    /// </summary>
    private static HarnessConfig Config(IHostPlatform platform) => new()
    {
        Toolchains =
        {
            ["cc"] = new ToolchainConfig { Platforms = [platform.PlatformKey], Generator = "Ninja", Env = { ["CC"] = Compilers.C, ["CXX"] = Compilers.Cxx } },
        },
        BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
        Projects = { MutationFixture.Project },
        Tools = { new ToolConfig { Name = "cmake" } },
        Legs =
        {
            ["native"] = new LegConfig
            {
                Os = platform.PlatformKey,
                Processor = platform.Processor,
                Config = "debug",
                Toolchain = "cc",
                Test = new TestConfig
                {
                    All = new TestInvocation
                    {
                        Runner = "cmake",
                        Args = ["-E", "echo", "tests passed with {compiler_CXX}"],
                        SuccessPattern = "^tests passed with ",
                    },
                },
            },
        },
        PredefinedRunners = { ["census"] = new RunnerConfig { Action = "census/census.yml" } },
        Worktrees = new WorktreeSettings { PathBudgetReserve = MutationFixture.LongestBuildPath },
    };
}
