using NSubstitute;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// What <c>check-mutations</c> refuses before any host is touched, each naming its fix: no registry, a registry or a text
/// it cites that cannot be swept, an arm <c>--arms</c> names that it does not declare, report arguments the arms' runs
/// need and nobody configured, and a leg whose build no sweep can read.
/// </summary>
public sealed class MutationServiceTests
{
    private const string Registry = "mutations/arms.txt";

    private const string TestRedArm = "A | charge | src/fixture.cpp | mutations/texts/charge.before | mutations/texts/charge.after | TEST-RED | fixture | fixture_tests | 3 | mutations/texts/charge.diag | the charge is pinned";

    private const string BuildRedArm = "A | depth | src/budget.hpp | mutations/texts/depth.before | mutations/texts/depth.after | BUILD-RED | fixture | - | 0 | PAIRED-CONTROL | a depth that is no integer does not compile";

    /// <summary>The registry's two arms, each with the rows it needs.</summary>
    private static readonly string[] Arms =
    [
        TestRedArm,
        "C | charge | Fixture.Charge | red",
        BuildRedArm,
        "B | depth | mutations/texts/depth.control-before | mutations/texts/depth.control-after | builds",
    ];

    /// <summary>The texts the two arms cite, each holding something.</summary>
    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        ["charge.before"] = "c <= b",
        ["charge.after"] = "c < b",
        ["charge.diag"] = "charge exceeded",
        ["depth.before"] = "int depth = 3",
        ["depth.after"] = "int depth = three",
        ["depth.control-before"] = "int depth = 3",
        ["depth.control-after"] = "int depth = 4",
    };

    /// <summary>A registry read whole and every text there: the arms selected, every one where --arms is left out, in the registry's order.</summary>
    [Fact]
    public void ARegistryReadWhole_SelectsEveryArm_OrThoseArmsNames()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable());

        Assert.Equal(["charge", "depth"], service.Read(context, null).Selected.Select(arm => arm.Id));
        Assert.Equal(["depth"], service.Read(context, ["depth"]).Selected.Select(arm => arm.Id));
        Assert.Equal(["charge", "depth"], service.Read(context, ["depth,charge"]).Selected.Select(arm => arm.Id));
    }

    /// <summary>No registry configured is refused, naming the setting; one configured that is not there, naming it.</summary>
    [Fact]
    public void NoRegistry_IsRefused_NamingTheSetting()
    {
        using var temp = new TempDirectory();
        var (unset, unsetContext) = Prepare(temp, Sweepable(new MutationSettings()));
        var (absent, absentContext) = Prepare(temp, Sweepable(new MutationSettings { Registry = "mutations/none.txt" }));

        var none = Assert.Throws<HarnessException>(() => unset.Read(unsetContext, null));
        var missing = Assert.Throws<HarnessException>(() => absent.Read(absentContext, null));

        Assert.Equal(
            (HarnessExit.ConfigInvalid, "check-mutations has no arms registry to sweep: set mutations.registry to the registry's path, relative to the repository root."),
            (none.ExitCode, none.Message));
        Assert.Equal(
            (HarnessExit.ConfigInvalid, $"mutations.registry names 'mutations/none.txt', which is not a file in '{temp.Path}'."),
            (missing.ExitCode, missing.Message));
    }

    /// <summary>
    /// A registry that cannot be read whole is refused with every problem at once, each to fix: a row it does not read, and
    /// a text a row cites that is not there.
    /// </summary>
    [Fact]
    public void ARegistryThatCannotBeReadWhole_IsRefused_NamingEveryProblem()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(), ["Z | charge | x", .. Arms]);

        var refusal = Assert.Throws<HarnessException>(() => service.Read(context, null));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.StartsWith($"The arms registry '{Registry}' cannot be swept: ", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("  - line 1: 'Z' is no row this registry reads", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A text a valid registry cites that the tree does not hold, a before-text, a control's before-text or a diagnostic
    /// holding nothing is refused before anything starts - one holding nothing occurs everywhere, or is said by every run -
    /// and an after-text holding nothing is a mutation that deletes, which is read.
    /// </summary>
    [Theory]
    [InlineData("charge.before", "", "line 1: the text in 'mutations/texts/charge.before' holds nothing, which occurs everywhere and which every run says")]
    [InlineData("charge.diag", "\n", "line 1: the text in 'mutations/texts/charge.diag' holds nothing, which occurs everywhere and which every run says")]
    [InlineData("depth.control-before", "", "line 4: the text in 'mutations/texts/depth.control-before' holds nothing, which occurs everywhere and which every run says")]
    [InlineData("depth.before", null, "line 3: text 'mutations/texts/depth.before' is not a file in the tree")]
    [InlineData("charge.after", "", null)]
    public void ACitedTextThatCannotBeSwept_IsRefusedBeforeAnythingStarts(string text, string? holds, string? problem)
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable());
        var path = temp.Combine("mutations", "texts", text);

        if (holds is null)
        {
            File.Delete(path);
        }
        else
        {
            File.WriteAllText(path, holds);
        }

        if (problem is null)
        {
            Assert.Equal(2, service.Read(context, null).Selected.Count);
            return;
        }

        var refusal = Assert.Throws<HarnessException>(() => service.Read(context, null));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("  - " + problem, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An arm --arms names that the registry does not declare is a usage error, naming it.</summary>
    [Fact]
    public void AnArmTheRegistryDoesNotDeclare_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable());

        var refusal = Assert.Throws<HarnessException>(() => service.Read(context, ["depth", "nowhere"]));

        Assert.Equal((HarnessExit.UsageError, "--arms names 'nowhere', which no A row of the registry declares"), (refusal.ExitCode, refusal.Message));
    }

    /// <summary>A scope naming no leg is refused with the registry's other problems, as configuration.</summary>
    [Fact]
    public void AScopeNamingNoLeg_IsRefused()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(), [.. Arms, "S | charge | nowhere | where it is built"]);

        var refusal = Assert.Throws<HarnessException>(() => service.Read(context, null));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("nowhere", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sweep selecting a TEST-RED arm with no report arguments configured is refused, naming the setting and an example;
    /// one selecting BUILD-RED arms alone runs no binary, and needs none.
    /// </summary>
    [Fact]
    public void TestRedArmsWithoutReportArguments_AreRefused_AndBuildRedArmsNeedNone()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(new MutationSettings { Registry = Registry, TextDirectory = "mutations/texts" }));

        var refusal = Assert.Throws<HarnessException>(() => service.Read(context, null));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.StartsWith("mutations.reportArgs is not set, and the sweep runs TEST-RED arms", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("[\"--gtest_output=xml:{report}\"]", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(["depth"], service.Read(context, ["depth"]).Selected.Select(arm => arm.Id));
    }

    /// <summary>
    /// A leg built by anything but CMake with the Ninja generator is refused before anything starts, naming the leg and
    /// the fix - a generator not declared, or another - and a leg that cannot be built at all, as a build refuses it;
    /// every such leg in one refusal.
    /// </summary>
    [Fact]
    public void ALegNoSweepCanRead_IsRefused_NamingEachAndItsFix()
    {
        var config = Sweepable();
        config.Toolchains["make"] = new ToolchainConfig { Generator = "Unix Makefiles" };
        config.Toolchains["any"] = new ToolchainConfig();
        config.Projects.Add(new ProjectConfig { Name = "tool", Type = "dotnet", Path = "tool.sln" });
        config.Legs["made"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "make", Project = "app" };
        config.Legs["picked"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "any", Project = "app" };
        config.Legs["dotnet"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc", Project = "tool" };
        config.Legs["bare"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Project = "app" };

        var refusal = Assert.Throws<HarnessException>(() => MutationService.RequireSweepable(config, LegSelection.Resolve(config, null).Legs));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "check-mutations cannot sweep every leg it was asked to, so nothing was run:",
                "  - Leg 'made' builds with toolchain 'make', whose generator is 'Unix Makefiles': set toolchains.make.generator to \"Ninja\". A sweep reads which objects each mutation rebuilt from ninja's own records of the build, which only the Ninja generator keeps for one configuration alone.",
                "  - Leg 'picked' builds with toolchain 'any', whose generator is not declared, so CMake picks one: set toolchains.any.generator to \"Ninja\". A sweep reads which objects each mutation rebuilt from ninja's own records of the build, which only the Ninja generator keeps for one configuration alone.",
                "  - Leg 'dotnet' builds project 'tool', a dotnet project: only a CMake build's ninja records say which objects a mutation rebuilt.",
                "  - Leg 'bare' cannot be built: it names no toolchain, and project 'app' declares no default toolchain for linux."),
            refusal.Message);

        MutationService.RequireSweepable(config, LegSelection.Resolve(config, ["native"]).Legs);
    }

    /// <summary>
    /// A sweep refused for its registry is refused before any host is measured: the refusal is the run's, and nothing is
    /// asked of a machine to reach it.
    /// </summary>
    [Fact]
    public async Task ASweepRefusedForItsRegistry_MeasuresNoHost()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable(new MutationSettings());
        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "x86_64" });
        var service = Service(harness, temp, HostDoubles.Loader(config, temp.Path, temp.Path), inspector);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => service.RunAsync(new MutationRequest(temp.Path, null, null), RunId.New(), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Empty(inspector.Inspected);
    }

    /// <summary>
    /// An arm whose S row names none of the legs a sweep selected is said before any leg is swept, naming its row and the
    /// legs it names: it is driven nowhere this run, which the legs' lines alone would never show. An arm driven on a
    /// selected leg is not said.
    /// </summary>
    [Fact]
    public async Task AnArmItsScopeKeepsFromEverySelectedLeg_IsSaidBeforeAnyLegIsSwept()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Legs["other"] = config.Legs["native"];

        var (service, _) = Prepare(temp, config, [.. Arms, "S | charge | other | only the other leg builds the charge"], harness);

        await service.RunAsync(new MutationRequest(temp.Path, ["native"], null), RunId.New(), TestContext.Current.CancellationToken);

        var said = harness.StandardError.ToString();

        Assert.Contains("arm 'charge' runs on none of the selected legs: its S row, line 5, names other", said, StringComparison.Ordinal);
        Assert.DoesNotContain("arm 'depth'", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg's sweep is given its tree; its project, configured with the dependency sources the leg's own build fetched
    /// beneath what the project sets itself - none for a leg never built; the arms it drives; and what a build of its
    /// variant is expected to come to, as the leg's own last build recorded it.
    /// </summary>
    [Fact]
    public void ALegsSweep_IsGivenItsProject_WithWhatItsOwnBuildFetched_AndWhatThatBuildCameTo()
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var (service, context) = Prepare(temp, config);
        var arms = service.Read(context, null);
        var project = new ProjectConfig { Name = "app", Type = "cmake", CacheVars = { ["FOO"] = "1" } };
        var variant = VariantKey.For(config, config.Legs["native"], "linux");
        var build = variant.DirectoryUnder(temp.Path);
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };
        var work = new LegWork(
            new PlacedLeg("native", config.Legs["native"], host, project, variant, temp.Path, temp.Path, build, new LocalHostConfig(), Emulated: false),
            context,
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);

        var unbuilt = service.Subject(work, arms, force: false);

        Assert.Equal([("FOO", "1")], unbuilt.Project.CacheVars.Select(pair => (pair.Key, pair.Value)));
        Assert.Null(unbuilt.ExpectedBuildBytes);
        Assert.Equal("what the main checkout's copy of the same variant came to there", unbuilt.ExpectedBuildSource);
        Assert.False(unbuilt.Force);

        var googletest = Directory.CreateDirectory(Path.Combine(build, "_deps", "googletest-src")).FullName;

        File.WriteAllText(
            Path.Combine(build, BuildDirectoryGuard.CMakeCacheFileName),
            $"FETCHCONTENT_BASE_DIR:PATH={Path.Combine(build, "_deps").Replace('\\', '/')}\nFETCHCONTENT_SOURCE_DIR_GOOGLETEST:PATH=\n");
        File.WriteAllText(
            Path.Combine(build, BuildRecord.FileName),
            new BuildRecord(null, variant.DirectoryName, null, new Dictionary<string, string>(), new Dictionary<string, DateTime>()) { Bytes = 4096 }.Write());

        var built = service.Subject(work, arms, force: true);

        Assert.Equal(temp.Path, built.TreeRoot);
        Assert.Equal(
            [(FetchedSources.FullyDisconnected, "ON"), ("FETCHCONTENT_SOURCE_DIR_GOOGLETEST", googletest.Replace('\\', '/')), ("FOO", "1")],
            built.Project.CacheVars.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value)));
        Assert.Equal(4096, built.ExpectedBuildBytes);
        Assert.Equal("what its last build there came to", built.ExpectedBuildSource);
        Assert.Equal(["charge", "depth"], built.Arms.Driven.Select(arm => arm.Id));
        Assert.Empty(built.Arms.Unselected);
        Assert.Same(config.Mutations, built.Settings);
        Assert.True(built.Force);
    }

    /// <summary>
    /// A host sweeping one of a run's legs is given each value <c>--arms</c> was given, as given and in order - an empty
    /// one among them, which the host refuses as this machine does - and nothing where it was left out.
    /// </summary>
    [Fact]
    public void AHostSweepingALeg_IsGivenEachArmsValue_AsGiven()
    {
        Assert.Empty(MutationService.RemoteArguments(null));
        Assert.Equal(
            ["--arms", "depth,charge", "--arms", string.Empty, "--arms", "floor"],
            MutationService.RemoteArguments(["depth,charge", string.Empty, "floor"]));
    }

    /// <summary>
    /// A configuration a sweep can read: one CMake project, a gcc toolchain with the Ninja generator, one leg on Linux,
    /// and the registry and its texts where <paramref name="settings"/> says, by default with report arguments.
    /// </summary>
    private static HarnessConfig Sweepable(MutationSettings? settings = null) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Toolchains = { ["gcc"] = new ToolchainConfig { Generator = "Ninja" } },
        Projects = [new ProjectConfig { Name = "app", Type = "cmake" }],
        Legs = { ["native"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc", Project = "app" } },
        Mutations = settings ?? new MutationSettings { Registry = Registry, TextDirectory = "mutations/texts", ReportArgs = ["--gtest_output=xml:{report}"] },
    };

    /// <summary>
    /// The service over a repository in <paramref name="temp"/> holding the registry - <paramref name="rows"/>, or the two
    /// arms with their rows - and every text it cites, with the context a run of it loads; writing where
    /// <paramref name="harness"/> writes, or a harness of its own.
    /// </summary>
    private static (MutationService Service, HarnessContext Context) Prepare(TempDirectory temp, HarnessConfig config, string[]? rows = null, HarnessFactory? harness = null)
    {
        harness ??= new HarnessFactory();

        temp.WriteFile(Registry, string.Join("\n", rows ?? Arms) + "\n");

        foreach (var (name, text) in Texts)
        {
            temp.WriteFile(Path.Combine("mutations", "texts", name), text);
        }

        var loader = HostDoubles.Loader(config, temp.Path, temp.Path);

        return (
            Service(harness, temp, loader, new RecordingInspector(host => new HostReport { Host = host })),
            new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config));
    }

    /// <summary>The service as the command builds it, its hosts measured by <paramref name="inspector"/> and nothing able to reach one.</summary>
    private static MutationService Service(HarnessFactory harness, TempDirectory temp, IHarnessContextLoader loader, IHostInspector inspector)
    {
        var processes = Substitute.For<IProcessRunner>();
        var legRuns = new LegRunService(
            loader,
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            new LogOwnership(harness.FileSystem, harness.Output, harness.Identity),
            Substitute.For<ISyncService>(),
            Substitute.For<ISyncTransportFactory>(),
            new RemoteLegRunner(new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),
            AdmissionKit.Admission(harness, temp.Combine("state", "admission.json"), new ScriptedGauge(10), new ManualClock()),
            new KeepAwake(new HeldProcesses(), harness.Output),
            new DeveloperEnvironmentProvider(harness.Platform, processes, harness.FileSystem, harness.Output),
            harness.FileSystem,
            harness.FilePermissions,
            harness.Platform,
            harness.Output);

        return new MutationService(
            loader,
            legRuns,
            SyncKit.Service(harness, loader),
            SyncKit.Transport(harness),
            Substitute.For<IBuildService>(),
            processes,
            new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions),
            new PhaseRunner(processes, harness.FileSystem, harness.Output),
            harness.PathBudget,
            harness.FileSystem,
            harness.Identity,
            TimeProvider.System,
            harness.Output);
    }
}
