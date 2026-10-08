using System.Text.Json;
using RepoHarness.Core.Configuration;
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
