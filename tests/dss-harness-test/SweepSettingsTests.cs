using DssHarness.Core.Configuration;
using DssHarness.Core.Mutations;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// What of the configuration an arm is judged on beyond what its build records: what the build is given, how the run
/// starts and what the judge is given - each in its own part - and nothing else of the configuration.
/// </summary>
public sealed class SweepSettingsTests
{
    /// <summary>
    /// Each value the build is given, the run starts by, or the judge reads moves its own part of the settings, and only
    /// that part; every other value of the configuration - one that cannot change a verdict, or that reaches an arm only
    /// through what its build records, the reading of the tree, or the compilers identified - moves none.
    /// </summary>
    [Theory]
    [InlineData("variant", "build")]
    [InlineData("toolchain env", "build")]
    [InlineData("configuration env", "build")]
    [InlineData("project env", "build")]
    [InlineData("host env", "build")]
    [InlineData("working directory", "run")]
    [InlineData("test env", "run")]
    [InlineData("report args", "run")]
    [InlineData("run time factor", "judge")]
    [InlineData("stall seconds", "judge")]
    [InlineData("another system's test env", null)]
    [InlineData("another system's working directory", null)]
    [InlineData("test runner", null)]
    [InlineData("workers", null)]
    [InlineData("build cores", null)]
    [InlineData("host cores", null)]
    [InlineData("clock step tolerance", null)]
    [InlineData("path budget", null)]
    [InlineData("cache vars", null)]
    [InlineData("build type", null)]
    [InlineData("generator", null)]
    [InlineData("sync exclude", null)]
    [InlineData("tool search directories", null)]
    [InlineData("registry", null)]
    public void TheSettings_HoldTheBuildsEnvironment_TheRunsStart_AndWhatTheJudgeIsGiven_AndNothingElseOfTheConfiguration(string edit, string? moves)
    {
        var (config, leg, host) = Configured(string.Empty);
        var (editedConfig, editedLeg, editedHost) = Configured(edit);

        var settings = SweepSettings.Of(config, leg, host);
        var edited = SweepSettings.Of(editedConfig, editedLeg, editedHost);

        Assert.Equal(
            (Build: moves == "build", Run: moves == "run", Judge: moves == "judge"),
            (Build: settings.Build != edited.Build, Run: settings.Run != edited.Run, Judge: settings.Judge != edited.Judge));
    }

    /// <summary>
    /// A configuration of one Windows leg building one project, its tests run by CTest - the environment declared for
    /// every system, the working directory for each - every value as it is, save the one <paramref name="edit"/> names.
    /// </summary>
    private static (HarnessConfig Config, LegConfig Leg, HostSettings Host) Configured(string edit)
    {
        string Either(string name, string value, string edited) => edit == name ? edited : value;

        var configuration = Either("variant", "debug", "checked");
        var config = new HarnessConfig
        {
            Defaults = new HarnessDefaults
            {
                StallSeconds = edit == "stall seconds" ? 90 : 60,
                BuildCores = edit == "build cores" ? 3 : 2,
                ClockStepToleranceMilliseconds = edit == "clock step tolerance" ? 500 : 2000,
            },
            Toolchains =
            {
                ["cc"] = new ToolchainConfig
                {
                    Generator = Either("generator", "Ninja", "Ninja Multi-Config"),
                    Env = { ["CC"] = Either("toolchain env", "gcc", "clang") },
                    CacheVars = { ["CMAKE_EXPORT_COMPILE_COMMANDS"] = Either("cache vars", "ON", "OFF") },
                },
            },
            BuildConfigs =
            {
                [configuration] = new BuildConfiguration
                {
                    CmakeBuildType = Either("build type", "Debug", "RelWithDebInfo"),
                    Env = { ["CFLAGS"] = Either("configuration env", "-g", "-g3") },
                },
            },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    Env = { ["APP_MODE"] = Either("project env", "one", "two") },
                    Test = new TestConfig
                    {
                        All = new TestInvocation
                        {
                            Runner = Either("test runner", "ctest", "ctest3"),
                            Env = new() { ["GTEST_COLOR"] = Either("test env", "no", "yes") },
                        },
                        Windows = new TestInvocation { WorkingDirectory = Either("working directory", "build", "out") },
                        Linux = new TestInvocation
                        {
                            WorkingDirectory = Either("another system's working directory", "build", "out"),
                            Env = new() { ["LD_EXTRA"] = Either("another system's test env", "lib", "lib64") },
                        },
                    },
                },
            },
            Mutations = new MutationSettings
            {
                Registry = Either("registry", "arms.txt", "other-arms.txt"),
                Workers = edit == "workers" ? 4 : 2,
                RunTimeFactor = edit == "run time factor" ? 5 : 10,
                ReportArgs = [Either("report args", "--gtest_output=xml:{report}", "--gtest_output=junit:{report}")],
            },
            Sync = new SyncConfig { Exclude = [Either("sync exclude", "*.log", "*.tmp")] },
            Worktrees = new WorktreeSettings { PathBudgetReserve = edit == "path budget" ? 200 : 100 },
            ToolSearchDirectories = { [PlatformNames.Windows] = [Either("tool search directories", "C:/tools", "D:/tools")] },
        };
        var leg = new LegConfig { Os = PlatformNames.Windows, Processor = "x86_64", Config = configuration, Toolchain = "cc" };

        config.Legs["native"] = leg;

        var host = new LocalHostConfig
        {
            BuildCores = edit == "host cores" ? 8 : 4,
            Env = { ["CCACHE_DIR"] = Either("host env", "C:/cache", "D:/cache") },
        };

        return (config, leg, host);
    }
}
