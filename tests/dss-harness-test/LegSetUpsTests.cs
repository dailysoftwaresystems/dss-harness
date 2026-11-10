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

    /// <summary>
    /// A set-up's line reads a tree the agents wrote where a path any word names lies in it, as the policy's reader reads
    /// the word: its program, an option's value, a container's source:target, a word of a shell's script, a home at its
    /// start, spelled with either separator and in any case, and a relative path read against the empty directory it runs
    /// from - climbing out of it too - or the leg's environment gives it one. Never a sibling whose name the tree's begins,
    /// nor a word that names no place, nor a relative path that stays where it runs.
    /// </summary>
    [Fact]
    public void ALine_IsRefused_WhereAPathAWordNamesLiesInATree_HoweverItIsSpelt()
    {
        using var home = new TempDirectory();
        var (tree, outside, confinement) = Confined(home);
        var compose = Path.Combine(tree, "compose.yml");
        var climbing = Path.GetRelativePath(outside, compose);
        IReadOnlyDictionary<string, string> none = new Dictionary<string, string>();

        string? Refusal(string program, params string[] arguments) => confinement.Refusal(new SetUpCommandLine("x", program, arguments), none);
        string In(string place) => $"names '{place}', a place in '{tree}', which the agents wrote";

        Assert.Equal(In(Path.Combine(tree, "scripts", "db.sh")), Refusal(Path.Combine(tree, "scripts", "db.sh")));
        Assert.Equal(In(Path.GetFullPath(compose.Replace('\\', '/').ToUpperInvariant())), Refusal("docker", "compose", $"--file={compose.Replace('\\', '/').ToUpperInvariant()}", "up"));
        Assert.Equal(In(tree), Refusal("docker", "run", "-v", $"{tree}:/src", "image"));
        Assert.Equal(In(compose), Refusal("docker", "compose", "-f", climbing, "up"));
        Assert.Equal(In(compose), Refusal("docker", "compose", "-f", "~/" + Path.GetRelativePath(home.Path, compose).Replace('\\', '/'), "up"));
        Assert.Equal(In(tree), Refusal("sh", "-c", $"cd '{tree}' && docker compose up -d"));
        Assert.Equal(
            $"is given COMPOSE_FILE by the leg's environment, which {In(compose)}",
            confinement.Refusal(new SetUpCommandLine("x", "docker", ["compose", "up", "-d"]), new Dictionary<string, string> { ["COMPOSE_FILE"] = compose }));
        Assert.Null(Refusal("docker", "run", "-v", $"{tree}-cache:/cache", "postgres:16"));
        Assert.Null(Refusal("docker", "compose", "-f", "compose.yml", "up", "-d"));
        Assert.Null(Refusal("sh", "-c", "docker compose -f ./compose.yml up -d >> up.log 2>&1"));
    }

    /// <summary>
    /// A set-up's line is refused where what it reads cannot be held to no tree: a whole path that holds a tree, which a
    /// program handed it may walk; a word its shell expands into a place before it runs - a substitution, a variable, the
    /// directory stack's tilde - but a home at its start, and never a tilde a program reads out of its word or one quoted
    /// away; a move to a directory that is no whole path; a relative path read where a move to a whole one took it; a line
    /// the reader cannot read whole; and a directory to run from that is itself in a tree.
    /// </summary>
    [Fact]
    public void ALine_IsRefused_WhereWhatItReadsCannotBeHeldToNoTree()
    {
        using var home = new TempDirectory();
        var (tree, _, confinement) = Confined(home);
        var beside = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(tree)!, "beside")).FullName;
        IReadOnlyDictionary<string, string> none = new Dictionary<string, string>();

        string? Refusal(string program, params string[] arguments) => confinement.Refusal(new SetUpCommandLine("x", program, arguments), none);

        Assert.Equal(
            $"names '{home.Path}', which holds '{tree}', which the agents wrote - a program handed it may walk it",
            Refusal("docker", "run", "-v", $"{home.Path}:/h", "image"));
        Assert.Equal(
            "names a place by a substitution in '$(git rev-parse --show-toplevel)', which cannot be told before it runs",
            Refusal("sh", "-c", "cd \"$(git rev-parse --show-toplevel)\" && docker compose up -d"));
        Assert.Equal(
            "names a place by a variable in '$REPO/compose.yml', which cannot be told before it runs",
            Refusal("sh", "-c", "docker compose -f \"$REPO/compose.yml\" up -d"));
        Assert.Equal(
            "moves to 'db', which is no whole path, so where it then reads cannot be told before it runs",
            Refusal("sh", "-c", "cd db && docker compose up -d"));
        Assert.Equal(
            $"names '{Path.Combine(tree, "compose.yml")}', a place in '{tree}', which the agents wrote",
            Refusal("sh", "-c", $"cd '{beside}' && docker compose -f ../{Path.GetFileName(tree)}/compose.yml up -d"));
        Assert.Equal(
            "could not be read whole: '$X' names the program it runs by a variable, which this does not read",
            Refusal("sh", "-c", "$X up"));
        Assert.Equal(
            "could not be read whole: '~1/compose.yml' names a directory of the stack pushd keeps, which this does not know",
            Refusal("sh", "-c", "docker compose -f ~1/compose.yml up -d"));
        Assert.Null(Refusal("sh", "-c", "docker compose -f ~/setups/compose.yml up -d"));
        Assert.Null(Refusal("sh", "-c", $"cd '{beside}' && docker compose up -d"));
        Assert.Null(Refusal("git", "log", "@~1"));
        Assert.Null(Refusal("sh", "-c", "git log @~1 && git commit --fixup=@~1 && cat '~1'"));

        var inTree = new SetUpConfinement([tree], Path.Combine(tree, "outside"), new HarnessFactory().FileSystem, new HomedPlatform(home.Path));

        Assert.Equal(
            $"would run from '{Path.Combine(tree, "outside")}', which lies in '{tree}', which the agents wrote",
            inTree.Refusal(new SetUpCommandLine("x", "docker", ["compose", "up", "-d"]), none));
    }

    /// <summary>
    /// A path a set-up's line names outside every tree, whose link leads into one, reads that tree: the place is read as the
    /// system follows it.
    /// </summary>
    [Fact]
    public void ALine_NamingALinkThatLeadsIntoATree_ReadsTheTree()
    {
        using var home = new TempDirectory();
        var (tree, _, confinement) = Confined(home);
        var link = Path.Combine(home.Path, "link");

        TestLinks.OrSkip(() => Directory.CreateSymbolicLink(link, tree));

        Assert.Equal(
            $"names '{Path.Combine(link, "compose.yml")}', a place in '{tree}', which the agents wrote",
            confinement.Refusal(new SetUpCommandLine("x", "docker", ["compose", "-f", Path.Combine(link, "compose.yml"), "up"]), new Dictionary<string, string>()));
    }

    /// <summary>
    /// A confinement over a tree under <paramref name="home"/>, its lines run from an empty directory under the isolation's
    /// own there, as the decision makes one.
    /// </summary>
    private static (string Tree, string Outside, SetUpConfinement Confinement) Confined(TempDirectory home)
    {
        var tree = Directory.CreateDirectory(Path.Combine(home.Path, "work", "tree")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(home.Path, ".cache", "dssharness-isolation", "outside", "0123456789ab")).FullName;

        return (tree, outside, new SetUpConfinement([tree], outside, new HarnessFactory().FileSystem, new HomedPlatform(home.Path)));
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
            HostDoubles.Floors(harness.Platform, harness.ProcessRunner),
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
