using System.Diagnostics;
using NSubstitute;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
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

    /// <summary>What every build the service starts in these tests ends with: none builds anything.</summary>
    private const string Unbuilt = "no build runs in these tests";

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
    public async Task ARegistryReadWhole_SelectsEveryArm_OrThoseArmsNames()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable());

        Assert.Equal(["charge", "depth"], (await service.ReadAsync(context, null, TestContext.Current.CancellationToken)).Selected.Select(arm => arm.Id));
        Assert.Equal(["depth"], (await service.ReadAsync(context, ["depth"], TestContext.Current.CancellationToken)).Selected.Select(arm => arm.Id));
        Assert.Equal(["charge", "depth"], (await service.ReadAsync(context, ["depth,charge"], TestContext.Current.CancellationToken)).Selected.Select(arm => arm.Id));
    }

    /// <summary>No registry configured is refused, naming the setting; one configured that is not there, naming it.</summary>
    [Fact]
    public async Task NoRegistry_IsRefused_NamingTheSetting()
    {
        using var temp = new TempDirectory();
        var (unset, unsetContext) = Prepare(temp, Sweepable(new MutationSettings()));
        var (absent, absentContext) = Prepare(temp, Sweepable(new MutationSettings { Registry = "mutations/none.txt" }));

        var none = await Assert.ThrowsAsync<HarnessException>(() => unset.ReadAsync(unsetContext, null, TestContext.Current.CancellationToken));
        var missing = await Assert.ThrowsAsync<HarnessException>(() => absent.ReadAsync(absentContext, null, TestContext.Current.CancellationToken));

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
    public async Task ARegistryThatCannotBeReadWhole_IsRefused_NamingEveryProblem()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(), ["Z | charge | x", .. Arms]);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

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
    public async Task ACitedTextThatCannotBeSwept_IsRefusedBeforeAnythingStarts(string text, string? holds, string? problem)
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
            Assert.Equal(2, (await service.ReadAsync(context, null, TestContext.Current.CancellationToken)).Selected.Count);
            return;
        }

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("  - " + problem, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry, a text directory or a cited text that is there and cannot be read - held open by another process, or
    /// not this user's to read - is the registry's refusal, exit 12, naming it and why, with every other problem beside
    /// it. Left to escape, it ended the command as a defect of this tool, exit 70, naming no row.
    /// </summary>
    [Theory]
    [InlineData(false, "mutations/arms.txt", "mutations.registry names 'mutations/arms.txt', which could not be read: it is held by another process")]
    [InlineData(true, "mutations/arms.txt", "mutations.registry names 'mutations/arms.txt', which could not be read: it is held by another process")]
    [InlineData(
        false,
        "mutations/texts",
        "  - the text directory 'mutations/texts' could not be listed, so whether every text in it is cited cannot be read: it is held by another process")]
    [InlineData(true, "mutations/texts/charge.before", "  - line 1: text 'mutations/texts/charge.before' could not be read: it is held by another process")]
    [InlineData(false, "mutations/texts/depth.control-before", "  - line 4: text 'mutations/texts/depth.control-before' could not be read: it is held by another process")]
    public async Task WhatASweepReadsOfItsRegistry_AndCannot_IsRefusedAsTheRegistryIs(bool denied, string unreadable, string said)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        Exception raised = denied ? new UnauthorizedAccessException("it is held by another process") : new IOException("it is held by another process");
        var (service, context) = Prepare(
            temp,
            Sweepable(),
            harness: harness,
            files: new Unreadable(harness.FileSystem, temp.Combine(unreadable.Split('/')), raised));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains(said, refusal.Message, StringComparison.Ordinal);

        if (said.StartsWith("  - ", StringComparison.Ordinal))
        {
            Assert.StartsWith($"The arms registry '{Registry}' cannot be swept: ", refusal.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An M row's site is compared with its arm's other sites as the tree's own file system compares names: one naming
    /// the arm's own file in another case is that file again where the file system folds case - two edits taken and put
    /// back over each other - and is refused, naming how the arm spells it; where it does not fold, it is another file.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnMRowsSite_IsComparedAsTheTreesOwnFileSystemComparesNames(bool folds)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (service, context) = Prepare(
            temp,
            Sweepable(),
            [.. Arms, "M | charge | src/Fixture.cpp | mutations/texts/charge.before | mutations/texts/charge.after | the header moves with it"],
            harness,
            files: new CaseOf(harness.FileSystem, temp.Path, folds));

        if (!folds)
        {
            Assert.Equal(["src/fixture.cpp", "src/Fixture.cpp"], (await service.ReadAsync(context, null, TestContext.Current.CancellationToken)).Selected[0].Sites.Select(site => site.Site));
            return;
        }

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains(
            "  - line 5: arm 'charge' already mutates 'src/Fixture.cpp', as 'src/fixture.cpp' at line 1: two edits to one file would be taken and put back over each other, so a coupled site is another file",
            refusal.Message,
            StringComparison.Ordinal);
    }

    /// <summary>An arm --arms names that the registry does not declare is a usage error, naming it.</summary>
    [Fact]
    public async Task AnArmTheRegistryDoesNotDeclare_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable());

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, ["depth", "nowhere"], TestContext.Current.CancellationToken));

        Assert.Equal((HarnessExit.UsageError, "--arms names 'nowhere', which no A row of the registry declares"), (refusal.ExitCode, refusal.Message));
    }

    /// <summary>A scope naming no leg is refused with the registry's other problems, as configuration.</summary>
    [Fact]
    public async Task AScopeNamingNoLeg_IsRefused()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(), [.. Arms, "S | charge | nowhere | where it is built"]);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("nowhere", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sweep selecting a TEST-RED arm with no report arguments configured is refused, naming the setting and an example;
    /// one selecting BUILD-RED arms alone runs no binary, and needs none.
    /// </summary>
    [Fact]
    public async Task TestRedArmsWithoutReportArguments_AreRefused_AndBuildRedArmsNeedNone()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(new MutationSettings { Registry = Registry, TextDirectory = "mutations/texts" }));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.StartsWith("mutations.reportArgs is not set, and the sweep runs TEST-RED arms", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("[\"--gtest_output=xml:{report}\"]", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(["depth"], (await service.ReadAsync(context, ["depth"], TestContext.Current.CancellationToken)).Selected.Select(arm => arm.Id));
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
    /// selected leg is not said, and the sweep goes on to drive it.
    /// </summary>
    [Fact]
    public async Task AnArmItsScopeKeepsFromEverySelectedLeg_IsSaidBeforeAnyLegIsSwept()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Legs["other"] = config.Legs["native"];

        // Every host measured as one the leg runs on, so the leg is placed and its sweep starts - and what had been said
        // by then is kept: a host is measured before any leg is swept.
        string? saidBefore = null;
        var inspector = new RecordingInspector(host =>
        {
            saidBefore ??= harness.StandardError.ToString();

            return new HostReport { Host = host, Os = "linux", Processor = "x86_64" };
        });
        var (service, _) = Prepare(temp, config, [.. Arms, "S | charge | other | only the other leg builds the charge"], harness, inspector);

        await service.RunAsync(new MutationRequest(temp.Path, ["native"], null), RunId.New(), TestContext.Current.CancellationToken);

        Assert.Contains("arm 'charge' runs on none of the selected legs: its S row, line 5, names other", saidBefore, StringComparison.Ordinal);
        Assert.Contains("starting 1 leg(s)", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("arm 'depth'", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A sweep none of whose selected arms runs on a selected leg is refused as a usage error before any host is measured,
    /// naming each arm and where it runs - every leg would be skipped, and the run would pass having swept nothing - and
    /// warned of nothing besides. A host sweeping one leg of a run another machine dispatched is given that run's arms,
    /// and refuses nothing and warns of nothing: an arm among them runs on another of the run's legs, which only the
    /// machine that selected them can tell, and this leg's line says it drove none.
    /// </summary>
    [Fact]
    public async Task ASweepDrivingNoArmOnAnySelectedLeg_IsRefusedBeforeAnyHostIsMeasured_SaveOnAHostSweepingOneLegOfARun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Legs["other"] = config.Legs["native"];
        config.Hosts.Wsl["Example-Linux"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" };

        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "x86_64" });
        var (service, _) = Prepare(
            temp,
            config,
            [.. Arms, "S | charge | other | only the other leg builds the charge", "S | depth | other | nor the depth"],
            harness,
            inspector);

        var every = await Assert.ThrowsAsync<HarnessException>(
            () => service.RunAsync(new MutationRequest(temp.Path, ["native"], null), RunId.New(), TestContext.Current.CancellationToken));
        var one = await Assert.ThrowsAsync<HarnessException>(
            () => service.RunAsync(new MutationRequest(temp.Path, ["native"], ["depth"]), RunId.New(), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, every.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "The sweep would drive no arm, so nothing was run: no arm selected runs on a selected leg (native), and a sweep that drove none would pass having proved nothing.",
                "  - arm 'charge' runs where its S row, line 5, names: other",
                "  - arm 'depth' runs where its S row, line 6, names: other",
                "Name a leg an arm runs on with --legs, or an arm these legs run with --arms."),
            every.Message);
        Assert.Equal(HarnessExit.UsageError, one.ExitCode);
        Assert.DoesNotContain("arm 'charge'", one.Message, StringComparison.Ordinal);
        Assert.Empty(inspector.Inspected);
        Assert.DoesNotContain("runs on none of the selected legs", harness.StandardError.ToString(), StringComparison.Ordinal);

        var onAHost = await service.RunAsync(
            new MutationRequest(temp.Path, ["native"], null, Json: true, Here: HostId.Wsl("Example-Linux")),
            RunId.New(),
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, onAHost.ExitCode);
        Assert.Contains("the sweep drives no arm on this leg; 2 arm(s): 2 skipped-not-selected", Assert.Single(onAHost.Data), StringComparison.Ordinal);
        Assert.DoesNotContain("runs on none of the selected legs", harness.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A sweep none of whose selected legs can run is incomplete, exit 21, naming why each cannot: nothing failed, and no
    /// leg reached a verdict. A build, a test and a run give 1 there, which of a sweep is an arm violated - so a caller
    /// sorting by exit code was sent to fix an arm's declaration by a sweep that drove none.
    /// </summary>
    [Fact]
    public async Task ASweepNoSelectedLegCanRun_IsIncomplete_NeverWhatAnArmViolatedExitsWith()
    {
        using var temp = new TempDirectory();
        var (service, _) = Prepare(temp, Sweepable());

        var outcome = await service.RunAsync(new MutationRequest(temp.Path, ["native"], null, Json: true), RunId.New(), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, outcome.ExitCode);
        Assert.NotEqual(Verdicts.ExitCodeFor(LegVerdict.Violated), outcome.ExitCode);
        Assert.Equal("no selected leg can run", outcome.Message);

        using var document = System.Text.Json.JsonDocument.Parse(Assert.Single(outcome.Data));
        var root = document.RootElement;

        Assert.Equal(HarnessExit.Incomplete, root.GetProperty("exitCode").GetInt32());
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.Equal("skipped-unavailable", Assert.Single(root.GetProperty("legs").EnumerateArray()).GetProperty("verdict").GetString());
    }

    /// <summary>
    /// A leg on another host is swept there by what this machine was asked: a self-test where this is one, and the arms
    /// <c>--arms</c> named, each value as it was given - so a host never sweeps its own registry where the fixture was
    /// asked for, nor every arm where some were named - and by nothing more where neither was said.
    /// </summary>
    [Fact]
    public async Task ARemoteLegsSweep_IsDispatchedWithTheSelfTestAndTheArms()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();
        var pi = HostId.Ssh("pi");

        config.Hosts.Ssh["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" };
        config.Legs["pi"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Toolchain = "gcc", Project = "app", Ssh = "pi" };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "pi", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });
        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "windows", Processor = "x86_64" }
            : new HostReport { Host = host, Os = "linux", Processor = "arm64", Session = new HostSession(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness") });
        var (service, _) = Prepare(temp, config, harness: harness, inspector: inspector, hosts: hosts);
        var cancellationToken = TestContext.Current.CancellationToken;

        var selfTest = await service.RunAsync(new MutationRequest(temp.Path, ["pi"], ["charge-bound,spare-unseen"], Json: true, SelfTest: true), RunId.New(), cancellationToken);
        var named = await service.RunAsync(new MutationRequest(temp.Path, ["pi"], ["charge"], Json: true), RunId.New(), cancellationToken);
        var every = await service.RunAsync(new MutationRequest(temp.Path, ["pi"], null, Json: true), RunId.New(), cancellationToken);

        Assert.All([selfTest, named, every], outcome => Assert.Equal(HarnessExit.Success, outcome.ExitCode));

        var sent = hosts.Calls
            .Select(call => System.Text.Json.JsonSerializer.Deserialize<HostAgentRequest>(call.Command.StandardInput!, HostAgentProtocol.JsonOptions)!)
            .Where(request => request.Kind == HostAgentRequestKind.Run)
            .Select(request => string.Join(' ', request.Arguments))
            .ToList();

        Assert.Equal(
            [
                $"check-mutations --legs pi --json --here {pi} --self-test --arms charge-bound,spare-unseen",
                $"check-mutations --legs pi --json --here {pi} --arms charge",
                $"check-mutations --legs pi --json --here {pi}",
            ],
            sent);
    }

    /// <summary>
    /// A cited text that a sync withholds from every copy of the tree - never transferred, or excluded - is refused
    /// before anything starts, with its row's line: a worker is such a copy, so every arm citing it would read violated
    /// for a text nobody carried. The registry and the text directory are held to the same when the configuration is
    /// read, which is where their paths are known.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACitedTextASyncWithholds_IsRefusedBeforeAnythingStarts(bool neverTransferred)
    {
        using var temp = new TempDirectory();
        var config = Sweepable();

        (neverTransferred ? config.Sync.NeverTransfer : config.Sync.Exclude).Add("mutations/texts/charge.diag");

        var (service, context) = Prepare(temp, config);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "The arms registry 'mutations/arms.txt' cannot be swept: 1 problem(s), each to fix:",
                "  - line 1: text 'mutations/texts/charge.diag' is withheld from every copy of the tree by a sync - git ignores it, or "
                + "sync.neverTransfer, sync.exclude or worktrees.root covers it - so no worker would hold it"),
            refusal.Message);
    }

    /// <summary>
    /// What git ignores a sync leaves behind as it leaves what the configuration names, and only the tree says which
    /// that is: a cited text git ignores is in no copy of the tree, and is refused before anything starts, with its
    /// row's line - never each arm citing it read violated for a text nobody carried. A registry git ignores is refused
    /// the same way, since no host sweeping a leg would hold it; one git tracks is carried whatever its rules say.
    /// </summary>
    [Fact]
    public async Task ACitedTextGitIgnores_IsRefusedBeforeAnythingStarts_AsARegistryItIgnoresIs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (service, context) = Prepare(temp, Sweepable(), harness: harness);
        var token = TestContext.Current.CancellationToken;

        temp.WriteFile(".gitignore", "*.diag\n");

        var text = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, token));

        Assert.Equal(HarnessExit.ConfigInvalid, text.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "The arms registry 'mutations/arms.txt' cannot be swept: 1 problem(s), each to fix:",
                "  - line 1: text 'mutations/texts/charge.diag' is withheld from every copy of the tree by a sync - git ignores it, or "
                + "sync.neverTransfer, sync.exclude or worktrees.root covers it - so no worker would hold it"),
            text.Message);

        // Tracked, a file is carried whatever rule would ignore it: git ignores only what it does not track.
        await harness.RunGitAsync(temp.Path, ["add", "--force", "mutations/texts/charge.diag"], token);

        Assert.Equal(2, (await service.ReadAsync(context, null, token)).Selected.Count);

        temp.WriteFile(".gitignore", "/mutations/arms.txt\n");

        var registry = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, token));

        Assert.Equal(HarnessExit.ConfigInvalid, registry.ExitCode);
        Assert.Equal(
            "mutations.registry names 'mutations/arms.txt', which a sync withholds from every copy of the tree - git ignores it, or "
            + "sync.neverTransfer, sync.exclude or worktrees.root covers it - so no host sweeping a leg would hold it: keep it where a sync carries it.",
            registry.Message);
    }

    /// <summary>
    /// A leg's sweep is given its tree; its project, as the leg builds it; the dependency sources the leg's own build
    /// fetched, which each worker is given its own copy of - none for a leg never built; the test settings its tests
    /// start by; the arms it drives; and what a build of its variant is expected to come to, as the leg's own last build
    /// recorded it. Its paths are reckoned by the configured reserve, and each arm's verdict is the judge's own.
    /// </summary>
    [Fact]
    public async Task ALegsSweep_IsGivenItsProject_WithWhatItsOwnBuildFetched_AndWhatThatBuildCameTo()
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var (service, context) = Prepare(temp, config);
        var arms = await service.ReadAsync(context, null, TestContext.Current.CancellationToken);
        var tests = new TestConfig { All = new TestInvocation { Runner = "ctest", WorkingDirectory = "{buildDir}" } };
        var project = new ProjectConfig { Name = "app", Type = "cmake", CacheVars = { ["FOO"] = "1" }, Test = tests };
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
        Assert.Same(FetchedSet.None, unbuilt.Fetched);
        Assert.Same(tests, unbuilt.Tests);
        Assert.Null(unbuilt.PathReserve);
        Assert.Null(unbuilt.Hold);
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
        Assert.Equal(MutationWorkers.Of(temp.Path, variant), built.Workers);
        // Nothing of the leg's own build directory is in the project a worker builds: each worker is pointed at its own copy.
        Assert.Equal([("FOO", "1")], built.Project.CacheVars.Select(pair => (pair.Key, pair.Value)));
        Assert.Equal([new FetchedSource("GOOGLETEST", Path.Combine(Path.Combine(build, "_deps").Replace('\\', '/'), "googletest-src"))], built.Fetched.Found);
        Assert.True(built.Fetched.Every);
        Assert.True(Directory.Exists(googletest));
        Assert.Equal(4096, built.ExpectedBuildBytes);
        Assert.Equal("what its last build there came to", built.ExpectedBuildSource);
        Assert.Equal(["charge", "depth"], built.Arms.Driven.Select(arm => arm.Id));
        Assert.Empty(built.Arms.Unselected);
        Assert.Same(config.Mutations, built.Settings);
        Assert.True(built.Force);
    }

    /// <summary>
    /// A host sweeping one of a run's legs is given each value <c>--arms</c> was given, as given and in order - an empty
    /// one among them, which the host refuses as this machine does - and nothing where it was left out; and is told to
    /// self-test where the run is a self-test.
    /// </summary>
    [Fact]
    public void AHostSweepingALeg_IsGivenEachArmsValue_AsGiven_AndTheSelfTest()
    {
        Assert.Empty(MutationService.RemoteArguments(null, selfTest: false));
        Assert.Equal(
            ["--arms", "depth,charge", "--arms", string.Empty, "--arms", "floor"],
            MutationService.RemoteArguments(["depth,charge", string.Empty, "floor"], selfTest: false));
        Assert.Equal(["--self-test"], MutationService.RemoteArguments(null, selfTest: true));
        Assert.Equal(["--self-test", "--arms", "spare-unseen"], MutationService.RemoteArguments(["spare-unseen"], selfTest: true));
    }

    /// <summary>
    /// A self-test drives the arms of the fixture's own registry - every one, in its order, or those <c>--arms</c> names -
    /// whatever the repository's registry declares, and refuses an arm the fixture does not declare as a usage error.
    /// </summary>
    [Fact]
    public void ASelfTest_DrivesTheFixturesArms_OrThoseArmsNames()
    {
        var every = MutationService.SelfTestArms(null);

        Assert.Equal(MutationFixture.Registry().Arms.Select(arm => arm.Id), every.Selected.Select(arm => arm.Id));
        Assert.Empty(every.Scopes);
        Assert.Equal(["charge-bound", "spare-unseen"], MutationService.SelfTestArms(["SPARE-UNSEEN,charge-bound"]).Selected.Select(arm => arm.Id));

        var refusal = Assert.Throws<HarnessException>(() => MutationService.SelfTestArms(["charge"]));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
    }

    /// <summary>
    /// A self-test of a leg sweeps the fixture kept where it was written, as the leg's variant builds it: the fixture's
    /// project, settings and arms, its binary run in its worker with nothing of the leg's tests, each worker's paths
    /// reckoned by the fixture's own build, nothing said of what a build of it comes to, and each arm held to its design.
    /// Its workers are kept beside the leg's own tree, in the self-test's family - so two repositories self-testing with
    /// one fixture never share a worker, and each one's are removed with its tree.
    /// </summary>
    [Fact]
    public void ASelfTest_SweepsTheFixture_AsTheLegBuilds_HoldingEachArmToItsDesign()
    {
        using var temp = new TempDirectory();
        var config = Sweepable(new MutationSettings { Workers = 3, RunTimeFactor = 4 });
        var context = new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config);
        var project = new ProjectConfig { Name = "app", Type = "cmake", Test = new TestConfig { All = new TestInvocation { Runner = "ctest" } } };
        var variant = VariantKey.For(config, config.Legs["native"], "linux");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };
        var work = new LegWork(
            new PlacedLeg("native", config.Legs["native"], host, project, variant, temp.Path, temp.Path, variant.DirectoryUnder(temp.Path), new LocalHostConfig(), Emulated: false),
            context,
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);
        var fixture = temp.Combine("data", MutationFixture.DirectoryName);

        var subject = MutationService.SelfTestSubject(work, MutationService.SelfTestArms(null), force: true, fixture);

        Assert.Equal(fixture, subject.TreeRoot);
        Assert.Equal(MutationWorkers.Of(temp.Path, variant, selfTest: true), subject.Workers);
        Assert.Equal(temp.Path + ".mutation-" + MutationWorkers.KeyOf(variant) + "s-1", subject.Workers.PathOf(1));

        var elsewhere = temp.Combine("another", "repository");
        var theirs = MutationService.SelfTestSubject(
            new LegWork(
                new PlacedLeg("native", config.Legs["native"], host, project, variant, elsewhere, elsewhere, variant.DirectoryUnder(elsewhere), new LocalHostConfig(), Emulated: false),
                new HarnessContext(new HarnessLayout(elsewhere, elsewhere), config),
                RunId.New(),
                Path.Combine(elsewhere, "runs", "r1"),
                Time: false),
            MutationService.SelfTestArms(null),
            force: false,
            fixture);

        Assert.Equal(fixture, theirs.TreeRoot);
        Assert.Equal(elsewhere + ".mutation-" + MutationWorkers.KeyOf(variant) + "s-1", theirs.Workers.PathOf(1));
        Assert.Equal((MutationFixture.DirectoryName, "cmake", "."), (subject.Project.Name, subject.Project.Type, subject.Project.Path));
        Assert.Equal(MutationFixture.Project.BuildOutputs, subject.Project.BuildOutputs);
        Assert.Null(subject.Tests);
        Assert.Equal(MutationFixture.LongestBuildPath, subject.PathReserve);
        Assert.Null(subject.ExpectedBuildBytes);
        Assert.Equal(("arms.txt", 3, 4.0), (subject.Settings.Registry, subject.Settings.Workers, subject.Settings.RunTimeFactor));
        Assert.Equal(MutationFixture.Designed.Keys.Order(StringComparer.Ordinal), subject.Arms.Driven.Select(arm => arm.Id).Order(StringComparer.Ordinal));
        Assert.True(subject.Force);

        var survived = ReachedVerdict.Of(LegVerdict.Survived, "ran 3 case(s), and none failed");

        Assert.Equal(MutationFixture.Hold("spare-unseen", survived), subject.Hold!("spare-unseen", survived));
        Assert.Equal(LegVerdict.Violated, subject.Hold!("charge-bound", survived).Verdict);
    }

    /// <summary>
    /// A self-test builds the fixture, a CMake project whatever the leg's own is: a leg whose project is no CMake one is
    /// self-tested through its toolchain, and a toolchain without the Ninja generator, or a leg that cannot be built, is
    /// still refused.
    /// </summary>
    [Fact]
    public void ASelfTest_AsksOnlyTheLegsToolchain()
    {
        var config = Sweepable();
        config.Toolchains["make"] = new ToolchainConfig { Generator = "Unix Makefiles" };
        config.Projects.Add(new ProjectConfig { Name = "tool", Type = "dotnet", Path = "tool.sln" });
        config.Legs["dotnet"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc", Project = "tool" };
        config.Legs["made"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "make", Project = "tool" };
        config.Legs["bare"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Project = "app" };

        MutationService.RequireSweepable(config, LegSelection.Resolve(config, ["native", "dotnet"]).Legs, selfTest: true);

        var refusal = Assert.Throws<HarnessException>(() => MutationService.RequireSweepable(config, LegSelection.Resolve(config, ["made", "bare"]).Legs, selfTest: true));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("Leg 'made' builds with toolchain 'make', whose generator is 'Unix Makefiles'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Leg 'bare' cannot be built", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'dotnet'", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A self-test reads no registry of the repository's, and asks nothing of a leg's own project but its toolchain: one
    /// with no registry configured, of a leg whose project is no CMake one, is not refused, where a sweep of it is refused
    /// before any host is measured. It goes on to place its legs, asking no host about the room a build of the leg's tree
    /// needs, and sweeps the fixture where the service was given to keep it - never among the data of the user running it.
    /// </summary>
    [Fact]
    public async Task ASelfTest_NeedsNoRegistry_AndAsksNoRoomOfTheLegsTree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable(new MutationSettings());

        config.Projects.Add(new ProjectConfig { Name = "tool", Type = "dotnet", Path = "tool.sln" });
        config.Legs["dotnet"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc", Project = "tool" };

        // Every host measured as one the leg runs on.
        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "x86_64" });
        var service = Service(harness, temp, HostDoubles.Loader(config, temp.Path, temp.Path), inspector);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => service.RunAsync(new MutationRequest(temp.Path, ["dotnet"], null), RunId.New(), TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Empty(inspector.Inspected);

        var outcome = await service.RunAsync(new MutationRequest(temp.Path, ["dotnet"], null, SelfTest: true), RunId.New(), TestContext.Current.CancellationToken);

        Assert.NotEmpty(inspector.RoomAsked);
        Assert.All(inspector.RoomAsked, asked => Assert.Empty(asked.Room.Builds));

        // Swept as far as its workers' builds, which no build here makes, from the fixture written where it is kept.
        Assert.Equal(Verdicts.ExitCodeFor(LegVerdict.Failed), outcome.ExitCode);
        Assert.Contains($"worker 1: its build of the unmutated tree: {Unbuilt}", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.All(MutationFixture.Files(), pair => Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(Fixture(temp), pair.Key))));
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
    /// <paramref name="harness"/> writes, or a harness of its own, its hosts measured by <paramref name="inspector"/>, or
    /// as ones no leg runs on.
    /// </summary>
    private static (MutationService Service, HarnessContext Context) Prepare(
        TempDirectory temp,
        HarnessConfig config,
        string[]? rows = null,
        HarnessFactory? harness = null,
        IHostInspector? inspector = null,
        IFileSystem? files = null,
        ScriptedHostCommands? hosts = null)
    {
        harness ??= new HarnessFactory();

        InitRepository(temp.Path);
        temp.WriteFile(Registry, string.Join("\n", rows ?? Arms) + "\n");

        foreach (var (name, text) in Texts)
        {
            temp.WriteFile(Path.Combine("mutations", "texts", name), text);
        }

        var loader = HostDoubles.Loader(config, temp.Path, temp.Path);

        return (
            Service(harness, temp, loader, inspector ?? new RecordingInspector(host => new HostReport { Host = host }), files, hosts),
            new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config));
    }

    /// <summary>
    /// The service as the command builds it, its hosts measured by <paramref name="inspector"/> and reached through
    /// <paramref name="hosts"/> - or nothing able to reach one - every build it starts failing without building, and a
    /// self-test's fixture kept in <paramref name="temp"/>, never among the data of the user running the tests - reading
    /// the repository through <paramref name="files"/>, or the harness's own file system.
    /// </summary>
    private static MutationService Service(
        HarnessFactory harness,
        TempDirectory temp,
        IHarnessContextLoader loader,
        IHostInspector inspector,
        IFileSystem? files = null,
        ScriptedHostCommands? hosts = null)
    {
        var processes = Substitute.For<IProcessRunner>();
        var builds = Substitute.For<IBuildService>();

        builds.BuildAsync(Arg.Any<HarnessConfig>(), Arg.Any<BuildRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<BuildRequest>();

                return new BuildResult(ReachedVerdict.Of(LegVerdict.Failed, Unbuilt), request.Variant.DirectoryUnder(request.TreeRoot), [], null, null);
            });

        var legRuns = new LegRunService(
            loader,
            new LegsService(loader, inspector, harness.Platform, harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            new LogOwnership(harness.FileSystem, harness.Output, harness.Identity),
            Substitute.For<ISyncService>(),
            Substitute.For<ISyncTransportFactory>(),
            new RemoteLegRunner(hosts ?? new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),
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
            builds,
            processes,
            new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions),
            new PhaseRunner(processes, harness.FileSystem, harness.Output),
            harness.PathBudget,
            files ?? harness.FileSystem,
            new MutationFixtureStore(harness.FileSystem, Fixture(temp)),
            harness.Identity,
            TimeProvider.System,
            harness.Output);
    }

    /// <summary>
    /// Makes <paramref name="directory"/> a repository of its own, as every tree a sweep reads is: what a sync withholds
    /// from a copy is asked of git there.
    /// </summary>
    private static void InitRepository(string directory)
    {
        using var git = Process.Start(new ProcessStartInfo("git", ["init", "--quiet", "."])
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var said = git.StandardError.ReadToEnd() + git.StandardOutput.ReadToEnd();

        git.WaitForExit();

        Assert.True(git.ExitCode == 0, said);
    }

    /// <summary>Where the service <see cref="Service"/> builds over <paramref name="temp"/> keeps a self-test's fixture.</summary>
    private static string Fixture(TempDirectory temp) => temp.Combine("state", MutationFixture.DirectoryName);

    /// <summary>
    /// The real file system, save that it finds <paramref name="root"/> by its name in another case only where
    /// <paramref name="folds"/> says so: a file system that folds case, or one that does not, whatever this machine's does.
    /// </summary>
    private sealed class CaseOf(IFileSystem inner, string root, bool folds) : PassThroughFileSystem(inner)
    {
        public override bool DirectoryExists(string path)
            => path != root && string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ? folds : base.DirectoryExists(path);
    }

    /// <summary>The real file system, save that <paramref name="path"/> is there and every read of it raises <paramref name="raised"/>.</summary>
    private sealed class Unreadable(IFileSystem inner, string path, Exception raised) : PassThroughFileSystem(inner)
    {
        public override string ReadAllText(string read) => read == path ? throw raised : base.ReadAllText(read);

        public override byte[] ReadAllBytes(string read) => read == path ? throw raised : base.ReadAllBytes(read);

        public override IEnumerable<string> EnumerateFiles(string listed, bool recursive)
            => listed == path ? throw raised : base.EnumerateFiles(listed, recursive);
    }
}
