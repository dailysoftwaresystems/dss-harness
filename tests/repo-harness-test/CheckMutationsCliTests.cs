using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The built CLI's <c>check-mutations</c>, end to end where nothing is built: what it refuses before any host is touched,
/// each run named as every run is.
/// </summary>
public sealed class CheckMutationsCliTests
{
    private const string Registry = "mutations/arms.txt";

    /// <summary>
    /// A sweep with no registry to read is refused as configuration, naming the setting to set - and is named as every run
    /// is, in its first line and as the document's runId, though it keeps no records.
    /// </summary>
    [Fact]
    public async Task ASweepWithNoRegistry_IsRefused_NamingTheSetting_AndIsNamedAsEveryRunIs()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp, new MutationSettings());

        var result = await CliRunner.RunAsync(["check-mutations", "--json", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var runId = root.GetProperty("runId").GetString()!;

        Assert.Equal(HarnessExit.ConfigInvalid, root.GetProperty("exitCode").GetInt32());
        Assert.Empty(root.GetProperty("legs").EnumerateArray());
        Assert.StartsWith($"check-mutations: run {runId}", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("set mutations.registry", result.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", runId)));
    }

    /// <summary>
    /// An arm <c>--arms</c> names that the registry does not declare is a usage error naming it, and a value naming no arm
    /// at all is one too, rather than read as every arm.
    /// </summary>
    [Theory]
    [InlineData("nobody-declared-this", "--arms names 'nobody-declared-this', which no A row of the registry declares")]
    [InlineData("", "--arms was given no arm id")]
    public async Task AnArmsValueNamingNoDeclaredArm_IsAUsageError(string value, string said)
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp, new MutationSettings { Registry = Registry, TextDirectory = "mutations/texts" });

        temp.WriteFile(
            Registry,
            "A | depth | src/budget.hpp | mutations/texts/depth.before | mutations/texts/depth.after | BUILD-RED | fixture | - | 0 | PAIRED-CONTROL | a depth that is no integer does not compile\n"
            + "B | depth | mutations/texts/depth.control-before | mutations/texts/depth.control-after | builds\n");
        temp.WriteFile(Path.Combine("src", "budget.hpp"), "int depth = 3;\n");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.before"), "int depth = 3");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.after"), "int depth = three");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.control-before"), "int depth = 3");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.control-after"), "int depth = 4");

        var result = await CliRunner.RunAsync(["check-mutations", "--arms", value, "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains(said, result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A self-test reads the fixture's registry, never the repository's - which this one does not configure - and an arm
    /// <c>--arms</c> names that the fixture does not declare is a usage error naming it, before any host is touched.
    /// </summary>
    [Fact]
    public async Task ASelfTestsArmsValueNamingNoArmOfTheFixture_IsAUsageError()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp, new MutationSettings());

        var result = await CliRunner.RunAsync(["check-mutations", "--self-test", "--arms", "charge", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("--arms names 'charge', which no A row of the registry declares", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("mutations.registry", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sweep whose only arm runs on a leg it did not select is a usage error, naming the arm and where it runs, with no
    /// leg's line and no records: every leg selected would be skipped, and the run would pass having driven nothing.
    /// </summary>
    [Fact]
    public async Task ASweepWhoseArmsRunOnNoSelectedLeg_IsAUsageError_NamingThemAndWhereTheyRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var leg = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Toolchain = "cc", Project = "app" };

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Toolchains = { ["cc"] = new ToolchainConfig { Generator = "Ninja", Env = { ["CC"] = "cc" } } },
            Projects = [new ProjectConfig { Name = "app", Type = "cmake" }],
            Legs = { ["native"] = leg, ["other"] = leg },
            Mutations = new MutationSettings { Registry = Registry, TextDirectory = "mutations/texts" },
        });

        temp.WriteFile(
            Registry,
            "A | depth | src/budget.hpp | mutations/texts/depth.before | mutations/texts/depth.after | BUILD-RED | fixture | - | 0 | PAIRED-CONTROL | a depth that is no integer does not compile\n"
            + "B | depth | mutations/texts/depth.control-before | mutations/texts/depth.control-after | builds\n"
            + "S | depth | other | only the other leg builds it\n");
        temp.WriteFile(Path.Combine("src", "budget.hpp"), "int depth = 3;\n");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.before"), "int depth = 3");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.after"), "int depth = three");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.control-before"), "int depth = 3");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.control-after"), "int depth = 4");

        var result = await CliRunner.RunAsync(["check-mutations", "--legs", "native", "--json", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == HarnessExit.UsageError, result.StandardError + result.StandardOutput);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.Equal(HarnessExit.UsageError, root.GetProperty("exitCode").GetInt32());
        Assert.Empty(root.GetProperty("legs").EnumerateArray());
        Assert.Contains("The sweep would drive no arm, so nothing was run: no arm selected runs on a selected leg (native)", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("  - arm 'depth' runs where its S row, line 3, names: other", result.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", root.GetProperty("runId").GetString()!)));
    }

    /// <summary>
    /// A listing sweeps nothing, asks no host, begins no run - no run line, no runs directory - and writes nothing: the tree
    /// is as it was, and no record, lock or worker is made. It says what it listed and where the record is, a row to each
    /// arm and leg, a line to each leg, and that what is unmoved here is not said to hold.
    /// </summary>
    [Fact]
    public async Task AListing_SweepsNothing_AsksNoHost_BeginsNoRun_AndWritesNothing()
    {
        using var temp = new TempDirectory();
        await PrepareListedAsync(temp);
        var before = Files(temp);

        var result = await CliRunner.RunAsync(["check-mutations", "--list", "--legs", "native", "-C", temp.Path], TestContext.Current.CancellationToken);
        var said = result.StandardError + result.StandardOutput;

        Assert.True(result.ExitCode == HarnessExit.Success, said);
        Assert.DoesNotContain("check-mutations: run ", said, StringComparison.Ordinal);
        Assert.Contains(
            $"check-mutations: listing 2 arm(s) of '{Registry}' on 1 leg(s); nothing is swept and no host is asked",
            said,
            StringComparison.Ordinal);
        Assert.Contains($"check-mutations: the record is '{Path.Combine(temp.Path, HarnessLayout.DirectoryName, HarnessLayout.MutationRecordDirectoryName)}'", said, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^charge\s+native\s+-\s+-\s+-\s+pending: never swept on this leg\r?$", result.StandardOutput);
        Assert.Contains("native: 1 arm(s): 1 pending (1 never swept)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("'unmoved here' is not 'holds'.", said, StringComparison.Ordinal);
        Assert.Contains("'check-mutations --pending' verifies each there, and sweeps what does not hold.", Prose.Words(said), StringComparison.Ordinal);
        Assert.Equal(before, Files(temp));
        Assert.False(Directory.Exists(temp.Combine(HarnessLayout.DirectoryName, HarnessLayout.RunsDirectoryName)));
        Assert.False(Directory.Exists(temp.Combine(HarnessLayout.DirectoryName, HarnessLayout.MutationRecordDirectoryName)));
        Assert.False(File.Exists(temp.Combine(HarnessLayout.DirectoryName, HarnessLayout.LockFileName)));
    }

    /// <summary>
    /// A listing names a leg that cannot be swept once, above its rows, and lists every arm on it pending for that; and it
    /// refuses nothing only a sweep needs - report arguments for a TEST-RED arm, a leg built with Ninja, a selection that
    /// drives an arm on a selected leg.
    /// </summary>
    [Fact]
    public async Task AListing_NamesALegThatCannotBeSwept_AndRefusesNothingOnlyASweepNeeds()
    {
        using var temp = new TempDirectory();
        await PrepareListedAsync(temp);
        var token = TestContext.Current.CancellationToken;

        var text = await CliRunner.RunAsync(["check-mutations", "--list", "-C", temp.Path], token);

        Assert.True(text.ExitCode == HarnessExit.Success, text.StandardError + text.StandardOutput);
        Assert.Contains(
            "check-mutations: 'other' cannot be swept, so every arm on it is pending: Leg 'other' builds with toolchain 'make', whose generator is not declared",
            text.StandardOutput,
            StringComparison.Ordinal);

        var listed = await CliRunner.RunAsync(["check-mutations", "--list", "--json", "-C", temp.Path], token);

        Assert.True(listed.ExitCode == HarnessExit.Success, listed.StandardError + listed.StandardOutput);

        using (var document = JsonDocument.Parse(listed.StandardOutput))
        {
            var legs = document.RootElement.GetProperty("legs");

            Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal((true, "charge", "never-swept"), (legs[0].GetProperty("sweepable").GetBoolean(), legs[0].GetProperty("arms")[0].GetProperty("arm").GetString(), legs[0].GetProperty("arms")[0].GetProperty("why")[0].GetProperty("code").GetString()));
            Assert.False(legs[1].GetProperty("sweepable").GetBoolean());
            Assert.StartsWith("Leg 'other' builds with toolchain 'make'", legs[1].GetProperty("unsweepable").GetString(), StringComparison.Ordinal);
            Assert.Equal(
                [("charge", "leg-unsweepable", "never-swept"), ("depth", "leg-unsweepable", "never-swept")],
                legs[1].GetProperty("arms").EnumerateArray().Select(arm => (arm.GetProperty("arm").GetString(), arm.GetProperty("why")[0].GetProperty("code").GetString(), arm.GetProperty("why")[1].GetProperty("code").GetString())));
        }

        var nowhere = await CliRunner.RunAsync(["check-mutations", "--list", "--legs", "native", "--arms", "depth", "--json", "-C", temp.Path], token);

        Assert.True(nowhere.ExitCode == HarnessExit.Success, nowhere.StandardError + nowhere.StandardOutput);

        using (var document = JsonDocument.Parse(nowhere.StandardOutput))
        {
            Assert.Empty(document.RootElement.GetProperty("legs")[0].GetProperty("arms").EnumerateArray());
        }
    }

    /// <summary>
    /// A listing of a leg naming a host asks that host nothing - one nothing can reach is listed as any other - and reads
    /// the host as the leg names it, so the host is among what it compares.
    /// </summary>
    [Fact]
    public async Task AListingOfALegNamingAHost_AsksItNothing_AndComparesTheHost()
    {
        using var temp = new TempDirectory();
        await PrepareListedAsync(temp);

        var result = await CliRunner.RunAsync(["check-mutations", "--list", "--legs", "far", "--json", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == HarnessExit.Success, result.StandardError + result.StandardOutput);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var arm = document.RootElement.GetProperty("legs")[0].GetProperty("arms")[0];

        Assert.Equal("never-swept", arm.GetProperty("why")[0].GetProperty("code").GetString());
        Assert.DoesNotContain("host", arm.GetProperty("notRead").EnumerateArray().Select(part => part.GetString()));
    }

    /// <summary>A listing reads and verifies the registry as a sweep does, every problem listed, and lists nothing.</summary>
    [Fact]
    public async Task AListing_ListsTheRegistrysProblemsFirst_AndStops()
    {
        using var temp = new TempDirectory();
        await PrepareListedAsync(temp);
        File.AppendAllText(temp.Combine(Registry), "Z | charge | what\nC | nobody | Fixture.Charge | red\n");

        var result = await CliRunner.RunAsync(["check-mutations", "--list", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, result.ExitCode);
        Assert.Contains("cannot be swept: 2 problem(s), each to fix:", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("'Z' is no row this registry reads", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("names arm 'nobody'", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("ARM", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("check-mutations: run ", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A listing given an option only a sweep has a use for is a usage error, said in one sentence naming both, and begins
    /// no run.
    /// </summary>
    [Theory]
    [InlineData("--pending", "--list sweeps nothing, so --pending has no arm to sweep: leave one of them out.")]
    [InlineData("--self-test", "--list sweeps nothing, so --self-test has no fixture to sweep: leave one of them out.")]
    [InlineData("--force-lock", "--list sweeps nothing, so --force-lock has no lock to take: leave one of them out.")]
    [InlineData("--use-staged", "--list sweeps nothing, so --use-staged has nothing staged to sweep: leave one of them out.")]
    [InlineData("--held", "--list sweeps nothing, so --held is given to a host sweeping one leg of a run: leave one of them out.", "QUE=")]
    [InlineData("--here", "--list sweeps nothing, so --here has no leg to sweep for another machine: leave one of them out.", "ssh mac")]
    public async Task ListWithAnOptionOnlyASweepUses_IsAUsageError(string option, string said, string? value = null)
    {
        using var temp = new TempDirectory();
        await PrepareListedAsync(temp);
        string[] given = value is null ? [option] : [option, value];

        var result = await CliRunner.RunAsync(["check-mutations", "--list", .. given, "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains(said, result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("check-mutations: run ", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A self-test under --pending is a usage error, said in one sentence, and begins no run: nothing of a self-test is
    /// ever recorded, so nothing of it could be held or pending.
    /// </summary>
    [Fact]
    public async Task PendingWithSelfTest_IsAUsageError()
    {
        using var temp = new TempDirectory();
        await PrepareListedAsync(temp);

        var result = await CliRunner.RunAsync(["check-mutations", "--pending", "--self-test", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("A self-test is never recorded, so nothing of it is pending: leave --pending out.", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("check-mutations: run ", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// --held is given by the machine that dispatches a leg's sweep to the host that sweeps it, carried as bytes and
    /// typed by nobody: without --here it is a usage error, said in one sentence, and no run begins.
    /// </summary>
    [Fact]
    public async Task HeldWithNoHostSweepingForAnotherMachine_IsAUsageError()
    {
        using var temp = new TempDirectory();
        await PrepareListedAsync(temp);

        var result = await CliRunner.RunAsync(["check-mutations", "--pending", "--held", "QUE=", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("--held is given by the machine that dispatches a leg's sweep to the host that sweeps it, so it needs --here: leave it out.", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("check-mutations: run ", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repository with a leg this machine builds with Ninja, one it builds with no generator declared, and one naming an
    /// ssh host nothing can reach, as the configuration declares it in another case; a TEST-RED arm with no report
    /// arguments configured, and a BUILD-RED arm only the other leg runs.
    /// </summary>
    private static async Task PrepareListedAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        var leg = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Toolchain = "cc", Project = "app" };

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Toolchains =
            {
                ["cc"] = new ToolchainConfig { Generator = "Ninja", Env = { ["CC"] = "cc" } },
                ["make"] = new ToolchainConfig { Env = { ["CC"] = "cc" } },
            },
            Projects = [new ProjectConfig { Name = "app", Type = "cmake" }],
            Legs =
            {
                ["native"] = leg,
                ["other"] = new LegConfig { Os = leg.Os, Processor = leg.Processor, Config = "debug", Toolchain = "make", Project = "app" },
                ["far"] = new LegConfig { Os = leg.Os, Processor = leg.Processor, Config = "debug", Toolchain = "cc", Project = "app", Ssh = "FAR" },
            },
            SshItems = { "far" },
            Hosts = new HostsConfig { Ssh = { ["far"] = new SshHostConfig { RepositoryPath = "~/repo" } } },
            Mutations = new MutationSettings { Registry = Registry, TextDirectory = "mutations/texts" },
        });

        temp.WriteFile(
            Registry,
            "A | charge | src/fixture.cpp | mutations/texts/charge.before | mutations/texts/charge.after | TEST-RED | fixture | fixture_tests | 3 | mutations/texts/charge.diag | a charge past the budget is refused\n"
            + "C | charge | Fixture.Charge | red\n"
            + "A | depth | src/budget.hpp | mutations/texts/depth.before | mutations/texts/depth.after | BUILD-RED | fixture | - | 0 | PAIRED-CONTROL | a depth that is no integer does not compile\n"
            + "B | depth | mutations/texts/depth.control-before | mutations/texts/depth.control-after | builds\n"
            + "S | depth | other | only the other leg builds it\n");
        temp.WriteFile(Path.Combine("src", "fixture.cpp"), "bool within(int c, int b) { return c <= b; }\n");
        temp.WriteFile(Path.Combine("src", "budget.hpp"), "int depth = 3;\n");
        temp.WriteFile(Path.Combine("mutations", "texts", "charge.before"), "c <= b");
        temp.WriteFile(Path.Combine("mutations", "texts", "charge.after"), "c < b");
        temp.WriteFile(Path.Combine("mutations", "texts", "charge.diag"), "charge exceeded");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.before"), "int depth = 3");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.after"), "int depth = three");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.control-before"), "int depth = 3");
        temp.WriteFile(Path.Combine("mutations", "texts", "depth.control-after"), "int depth = 4");
    }

    /// <summary>Every file of the tree but git's own, by its path, with what it holds.</summary>
    private static Dictionary<string, string> Files(TempDirectory temp)
        => Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(temp.Path, file).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToDictionary(file => file, File.ReadAllText, StringComparer.Ordinal);

    /// <summary>A repository holding one leg this machine runs, its mutation testing configured by <paramref name="settings"/>.</summary>
    private static async Task PrepareAsync(TempDirectory temp, MutationSettings settings)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug" } },
            Mutations = settings,
        });
    }
}
