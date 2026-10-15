using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.SetUps;

namespace DssHarness.Tests;

/// <summary>
/// Which set-ups a leg brings up for what its command has it do, and the refusals of a run asked to set up that could
/// not: read from the configuration alone, before anything runs.
/// </summary>
public sealed class LegSetUpsTests
{
    /// <summary>
    /// A leg brings up what its runner names, then what its test invocation names where it tests - a platform's section
    /// replacing the all section's list, as its other keys do - each once, in the order named, as declared; and nothing
    /// where its run is not asked to set up.
    /// </summary>
    [Fact]
    public void ALegBringsUpItsRunnersSetUps_ThenItsTestInvocations_EachOnceAsDeclared()
    {
        var config = Config();
        var linux = config.Legs["lin"];
        var windows = config.Legs["win"];

        Assert.Equal(["db", "e2e"], Names(new LegWorkload(Build: true, Test: true, []) { SetUps = ["DB"], SetUp = true }.SetUpsOf(config, linux)));
        Assert.Equal(["cache"], Names(new LegWorkload(Build: true, Test: true, []) { SetUp = true }.SetUpsOf(config, windows)));
        Assert.Equal(["db"], Names(new LegWorkload(Build: true, Test: false, []) { SetUps = ["db"], SetUp = true }.SetUpsOf(config, linux)));
        Assert.Empty(new LegWorkload(Build: true, Test: false, []) { SetUp = true }.SetUpsOf(config, windows));
        Assert.Empty(new LegWorkload(Build: true, Test: true, []) { SetUps = ["db"] }.SetUpsOf(config, linux));
    }

    /// <summary>
    /// A runner's set-ups are its own and those of each runner its run checks name, which run on the leg as the runner
    /// left it: each once, however often named alike.
    /// </summary>
    [Fact]
    public void ARunnersSetUps_AreItsOwnAndItsRunChecksRunners()
    {
        var workload = LegWorkload.ForRunner(
            new RunnerConfig { Action = "a/a.yml", SetUp = ["e2e", "db"] },
            null,
            [("check", new RunnerConfig { Action = "c/c.yml", SetUp = ["E2E", "cache"] }, null, false)]);

        Assert.Equal(["e2e", "db", "cache"], Names(workload.SetUps));
        Assert.Empty(LegWorkload.ForRunner(new RunnerConfig { Action = "a/a.yml" }, null).SetUps);
    }

    /// <summary>
    /// --set-up on a run whose selected legs name no set-up is a usage error, since it would change nothing while the
    /// command line says it did; a run where one leg names one is not refused for the legs that name none.
    /// </summary>
    [Fact]
    public void ARunAskedToSetUp_WhereNothingNamesOne_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var setUps = SetUps(temp);
        var refused = Assert.Throws<HarnessException>(() => setUps.Require(config, temp.Path, Selected(config, "win"), Asked(Test: false)));

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            "--set-up was given, but nothing the selected legs run names a set-up: a test invocation's or a runner's setUp names the ones a leg brings up. Nothing was run.",
            refused.Message);

        setUps.Require(config, temp.Path, Selected(config, "win", "lin"), Asked(Test: false) with { SetUps = ["db"] });
    }

    /// <summary>
    /// A leg needing a set-up whose up or down names no line for its platform is refused, 13, before anything runs, every
    /// such leg and direction named, rather than found out leg by leg once its tree is synced.
    /// </summary>
    [Fact]
    public void ALegNeedingASetUpWithNoLineForItsPlatform_IsRefused_EveryOneNamed()
    {
        using var temp = new TempDirectory();
        var config = Config();

        config.SetUp["linuxOnly"] = new SetUpConfig
        {
            Up = SetUpLine.Keyed([new("linux", "docker up")]),
            Down = SetUpLine.Keyed([new("linux", "docker down")]),
        };

        var refused = Assert.Throws<HarnessException>(() => SetUps(temp).Require(
            config,
            temp.Path,
            Selected(config, "win", "lin"),
            Asked(Test: false) with { SetUps = ["linuxOnly"] }));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "leg 'win': setUp 'linuxOnly' up has no command line for windows: it names linux; "
            + "leg 'win': setUp 'linuxOnly' down has no command line for windows: it names linux. Nothing was run.",
            refused.Message);
    }

    /// <summary>
    /// A line whose program the configuration does not allow, values a set-up cannot be given, and one set-up a leg names
    /// twice with other values are each refused, 13, before anything runs - every one named, with its leg.
    /// </summary>
    [Fact]
    public void LinesAndValuesALegCouldNotRun_AreRefused_EveryOneNamed()
    {
        using var temp = new TempDirectory();
        var config = Config();

        config.SetUp["curl"] = new SetUpConfig { Up = "curl http://localhost", Down = "docker down" };

        var refused = Assert.Throws<HarnessException>(() => SetUps(temp).Require(
            config,
            temp.Path,
            Selected(config, "lin"),
            Asked(Test: true) with
            {
                SetUps = [new SetUpUse { Name = "db", With = { ["profile"] = "ci" } }, "curl", new SetUpUse { Name = "cache", With = { ["nope"] = "1" } }],
            }));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "leg 'lin' names set-up 'db' with other values: 'db (profile=ci)' and 'db (profile=dev)'; "
            + "leg 'lin': setUp 'curl' up 'curl' is not declared under 'tools' and is not a path inside the repository (declared: 'docker'); "
            + "leg 'lin': setUp 'cache' gives 'nope' a value, and set-up 'cache' declares no such input: it declares none. Nothing was run.",
            refused.Message);
    }

    /// <summary>
    /// A set-up named once with no values and once with the values its inputs take without them is named once: the same
    /// values written two ways are no other values.
    /// </summary>
    [Fact]
    public void TheSameValuesWrittenTwoWays_AreOneSetUp()
    {
        using var temp = new TempDirectory();
        var config = Config();
        var workload = Asked(Test: true) with { SetUps = [new SetUpUse { Name = "DB", With = { ["PROFILE"] = "dev" } }] };

        SetUps(temp).Require(config, temp.Path, Selected(config, "lin"), workload);

        Assert.Equal(["db (profile=dev)", "e2e"], workload.SetUpsOf(config, config.Legs["lin"]).Select(use => use.ToString()));
    }

    private static List<string> Names(IEnumerable<SetUpUse> uses) => [.. uses.Select(use => use.Name)];

    private static LegWorkload Asked(bool Test) => new(Build: true, Test, []) { SetUp = true };

    private static IReadOnlyList<SelectedLeg> Selected(HarnessConfig config, params string[] names)
        => LegSelection.Resolve(config, names).Legs;

    private static LegSetUps SetUps(TempDirectory state)
    {
        var harness = new HarnessFactory();
        var holds = new SetUpHolds(harness.FileSystem, harness.Output, harness.Identity, harness.Platform, () => state.Combine("setups.json"));
        var service = new SetUpService(
            harness.ContextLoader,
            harness.ProcessRunner,
            harness.Platform,
            new ActionToolPolicy(harness.Platform),
            new LocalProgramResolver(harness.Platform, FilePermissionsFactory.Create()),
            holds,
            harness.Output);

        return new LegSetUps(service, holds, new PhaseRunner(harness.ProcessRunner, harness.FileSystem, harness.Output));
    }

    private static HarnessConfig Config() => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Tools = [new ToolConfig { Name = "docker" }],
        SetUp =
        {
            ["db"] = new SetUpConfig { Up = "docker db-up {profile}", Down = "docker db-down", Inputs = { ["profile"] = new SetUpInput { Default = "dev" } } },
            ["e2e"] = new SetUpConfig { Up = "docker e2e-up", Down = "docker e2e-down" },
            ["cache"] = new SetUpConfig { Up = "docker cache-up", Down = "docker cache-down" },
        },
        Projects =
        {
            new ProjectConfig
            {
                Name = "app",
                Type = "cmake",
                Test = new TestConfig
                {
                    All = new TestInvocation { Runner = "ctest", SuccessPattern = "passed", SetUp = ["e2e", "db"] },
                    Windows = new TestInvocation { SetUp = ["cache"] },
                },
            },
        },
        Legs =
        {
            ["win"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Project = "app" },
            ["lin"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Project = "app" },
        },
    };
}
