using System.Diagnostics;
using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

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
    [InlineData(
        true,
        "mutations/texts",
        "  - the text directory 'mutations/texts' could not be listed, so whether every text in it is cited cannot be read: it is held by another process")]
    [InlineData(false, "mutations/texts/charge.before", "  - line 1: text 'mutations/texts/charge.before' could not be read: it is held by another process")]
    [InlineData(true, "mutations/texts/charge.before", "  - line 1: text 'mutations/texts/charge.before' could not be read: it is held by another process")]
    [InlineData(false, "mutations/texts/depth.control-before", "  - line 4: text 'mutations/texts/depth.control-before' could not be read: it is held by another process")]
    [InlineData(true, "mutations/texts/depth.control-before", "  - line 4: text 'mutations/texts/depth.control-before' could not be read: it is held by another process")]
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
    /// the arm's own file in another case is that file again where the file system folds case - a further site of it,
    /// spelt otherwise than the row the tree's spelling is held to - and is refused, naming how the arm spells it; where
    /// it does not fold, it is another file.
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
            "  - line 5: arm 'charge' names 'src/Fixture.cpp', the file it already mutates as 'src/fixture.cpp' at line 1: a further site of a file is spelt as the arm's first row for it spells it",
            refusal.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Two texts of one arm that overlap in one file, as the tree holds it, are the registry's problem, listed at the
    /// later row's line with its others before any host is touched: one within the other, or the same text cited twice.
    /// Texts apart from each other in the file are read, and one that is not in the file exactly once is left to its
    /// arm's pre-flight, which counts it.
    /// </summary>
    [Fact]
    public async Task TwoTextsOfOneArmThatOverlapInOneFile_AreRefusedWithTheRegistrysOtherProblems()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(
            temp,
            Sweepable(),
            [
                .. Arms,
                "M | charge | src/fixture.cpp | mutations/texts/within.before | mutations/texts/within.after | lies across the arm's own text",
                "M | charge | src/fixture.cpp | mutations/texts/floor.before | mutations/texts/floor.after | apart from both",
                "M | charge | src/fixture.cpp | mutations/texts/floor.before | mutations/texts/floor.after | the same text again",
                "M | charge | src/fixture.cpp | mutations/texts/absent.before | mutations/texts/floor.after | not in the file at all",
            ]);

        temp.WriteFile("src/fixture.cpp", "bool within(int c, int b) { return c <= b; }\nbool positive(int c) { return c > 0; }\n");
        temp.WriteFile("mutations/texts/within.before", "return c <= b;\n");
        temp.WriteFile("mutations/texts/within.after", "return true;\n");
        temp.WriteFile("mutations/texts/floor.before", "c > 0\n");
        temp.WriteFile("mutations/texts/floor.after", "c >= 0\n");
        temp.WriteFile("mutations/texts/absent.before", "nowhere\n");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(
            (HarnessExit.ConfigInvalid, string.Join(
                Environment.NewLine,
                $"The arms registry '{Registry}' cannot be swept: 2 problem(s), each to fix:",
                "  - line 5: the text in 'mutations/texts/within.before' overlaps the text in 'mutations/texts/charge.before', line 1, in "
                + "'src/fixture.cpp': the texts of one arm in one file are replaced together, each as the tree holds the file, so no two share a byte of it",
                "  - line 7: the text in 'mutations/texts/floor.before' overlaps the text in 'mutations/texts/floor.before', line 6, in "
                + "'src/fixture.cpp': the texts of one arm in one file are replaced together, each as the tree holds the file, so no two share a byte of it")),
            (refusal.ExitCode, refusal.Message));
    }

    /// <summary>
    /// Texts of one arm apart from each other in one file are read as one site with each of them, whatever --arms names;
    /// and each is held to what any cited text is: one that is not there is its own row's problem.
    /// </summary>
    [Fact]
    public async Task TextsOfOneArmApartInOneFile_AreReadAsOneSite_AndEachIsHeldAsAnyCitedTextIs()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(
            temp,
            Sweepable(),
            [.. Arms, "M | charge | src/fixture.cpp | mutations/texts/floor.before | mutations/texts/floor.after | the second site of the same file"]);

        temp.WriteFile("src/fixture.cpp", "bool within(int c, int b) { return c <= b; }\nbool positive(int c) { return c > 0; }\n");
        temp.WriteFile("mutations/texts/floor.before", "c > 0\n");
        temp.WriteFile("mutations/texts/floor.after", "c >= 0\n");

        var charge = (await service.ReadAsync(context, null, TestContext.Current.CancellationToken)).Selected[0];

        Assert.Equal(["src/fixture.cpp"], charge.Sites.Select(site => site.Site));
        Assert.Equal(["mutations/texts/charge.before", "mutations/texts/floor.before"], charge.Own.Texts.Select(text => text.Before));

        File.Delete(temp.Combine("mutations", "texts", "floor.after"));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Contains("  - line 5: text 'mutations/texts/floor.after' is not a file in the tree", refusal.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// A scope naming no leg is the registry's problem, listed at its line as configuration whatever <c>--arms</c> names:
    /// every arm, the scoped arm, another, or one the registry does not declare - which was refused first, as a usage
    /// error, so the one command that reads a registry and stops said nothing of a row no sweep could start over.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("charge")]
    [InlineData("depth")]
    [InlineData("nobody-declared-this")]
    public async Task AScopeNamingNoLeg_IsRefusedAsTheRegistrys_WhateverArmsNames(string? arms)
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(), [.. Arms, "S | charge | nowhere | where it is built"]);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => service.ReadAsync(context, arms is null ? null : [arms], TestContext.Current.CancellationToken));

        Assert.Equal(
            (HarnessExit.ConfigInvalid, string.Join(
                Environment.NewLine,
                $"The arms registry '{Registry}' cannot be swept: 1 problem(s), each to fix:",
                "  - line 5: the S row of arm 'charge' names 'nowhere', which is neither a leg nor a leg set; declared legs: native")),
            (refusal.ExitCode, refusal.Message));
    }

    /// <summary>
    /// A scope naming no leg is listed with the registry's other problems, in one refusal: beside a row the registry does
    /// not read it went unsaid, to be met by the run after that row was fixed.
    /// </summary>
    [Fact]
    public async Task AScopeNamingNoLeg_IsListedBesideTheRegistrysOtherProblems()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(), [.. Arms, "S | charge | nowhere | where it is built", "Z | charge | x"]);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(
            (HarnessExit.ConfigInvalid, string.Join(
                Environment.NewLine,
                $"The arms registry '{Registry}' cannot be swept: 2 problem(s), each to fix:",
                "  - line 6: 'Z' is no row this registry reads, which are A, C, G, B, M and S",
                "  - line 5: the S row of arm 'charge' names 'nowhere', which is neither a leg nor a leg set; declared legs: native")),
            (refusal.ExitCode, refusal.Message));
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
            .Select(call => System.Text.Json.JsonSerializer.Deserialize<HostAgentRequest>(call.Command.StandardInput.Read(), HostAgentProtocol.JsonOptions)!)
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
    /// for a text nobody carried. Whatever cites it: a site's before-text or what replaces it, a paired control's, or a
    /// diagnostic. The registry and the text directory are held to the same when the configuration is read, which is
    /// where their paths are known.
    /// </summary>
    [Theory]
    [InlineData(true, "charge.diag", 1)]
    [InlineData(false, "charge.diag", 1)]
    [InlineData(true, "charge.before", 1)]
    [InlineData(false, "charge.after", 1)]
    [InlineData(true, "depth.control-before", 4)]
    [InlineData(false, "depth.control-after", 4)]
    public async Task ACitedTextASyncWithholds_IsRefusedBeforeAnythingStarts(bool neverTransferred, string cited, int line)
    {
        using var temp = new TempDirectory();
        var config = Sweepable();

        (neverTransferred ? config.Sync.NeverTransfer : config.Sync.Exclude).Add("mutations/texts/" + cited);

        var (service, context) = Prepare(temp, config);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "The arms registry 'mutations/arms.txt' cannot be swept: 1 problem(s), each to fix:",
                $"  - line {line}: text 'mutations/texts/{cited}' is withheld from every copy of the tree by a sync - git ignores it, or "
                + "sync.neverTransfer, sync.exclude or worktrees.root covers it - so no worker would hold it"),
            refusal.Message);
    }

    /// <summary>
    /// A file in the text directory that a sync withholds from every copy of the tree - one git ignores, as a desktop's
    /// or an editor's leftover is, or one the configuration never transfers or excludes - is no mutation text: no copy
    /// would hold it, so nobody could drive it, and the cover check passes it over. One a sync carries that no row
    /// cites is still a text nobody drives.
    /// </summary>
    [Theory]
    [InlineData("git")]
    [InlineData("neverTransfer")]
    [InlineData("exclude")]
    public async Task AFileInTheTextDirectoryThatASyncWithholds_IsNoTextNobodyDrives(string withheldBy)
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var token = TestContext.Current.CancellationToken;

        switch (withheldBy)
        {
            case "neverTransfer":
                config.Sync.NeverTransfer.Add("mutations/texts/.DS_Store");
                break;

            case "exclude":
                config.Sync.Exclude.Add("mutations/texts/.DS_Store");
                break;
        }

        var (service, context) = Prepare(temp, config);

        temp.WriteFile(Path.Combine("mutations", "texts", ".DS_Store"), "a desktop's own");

        if (withheldBy == "git")
        {
            temp.WriteFile(".gitignore", ".DS_Store\n");
        }

        Assert.Equal(2, (await service.ReadAsync(context, null, token)).Selected.Count);

        temp.WriteFile(Path.Combine("mutations", "texts", "stray.diag"), "nobody's");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, token));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "The arms registry 'mutations/arms.txt' cannot be swept: 1 problem(s), each to fix:",
                "  - 'mutations/texts/stray.diag' is in the text directory and no row cites it: a mutation text nobody drives"),
            refusal.Message);
    }

    /// <summary>
    /// A sweep that cannot ask git what it ignores in the tree is refused, as a failure, before anything is read as
    /// carried: no sync of the tree could be made without it, and read as a tree git ignores nothing in, a registry or
    /// a text no worker would hold would pass.
    /// </summary>
    [Fact]
    public async Task ASweepWhereGitCannotSayWhatItIgnores_IsRefused_AsAFailure()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["ls-files", "--others", "--ignored", ..] ? new GitCommandResult(128, string.Empty, "fatal: the index is locked") : null,
        };
        var (service, context) = Prepare(temp, Sweepable(), harness: harness, git: git);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, refusal.ExitCode);
        Assert.Equal(
            $"What git ignores in '{temp.Path}' could not be listed, so a sync cannot tell which files are local to this machine: fatal: the index is locked",
            refusal.Message);
    }

    /// <summary>
    /// A text directory holding only what a sync withholds holds no text any copy of the tree would, and is said as
    /// that, with how many: never as a directory holding no file, which it does.
    /// </summary>
    [Fact]
    public async Task ATextDirectoryOfNothingButWhatASyncWithholds_IsSaidAsThat()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(
            temp,
            Sweepable(new MutationSettings { Registry = Registry, TextDirectory = "mutations/held", ReportArgs = ["--gtest_output=xml:{report}"] }));

        temp.WriteFile(Path.Combine("mutations", "held", ".DS_Store"), "a desktop's own");
        temp.WriteFile(Path.Combine("mutations", "held", "Thumbs.db"), "another's");
        temp.WriteFile(".gitignore", ".DS_Store\nThumbs.db\n");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "The arms registry 'mutations/arms.txt' cannot be swept: 1 problem(s), each to fix:",
                "  - the text directory 'mutations/held' holds no file a copy of the tree would: a sync withholds each of the 2 in it"),
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

        var unbuilt = service.Subject(work, arms, force: false, new RepositoryTreesFound([new RepositoryTree(temp.Path, RepositoryTree.MainCheckout)]), records: true);

        Assert.Equal([("FOO", "1")], unbuilt.Project.CacheVars.Select(pair => (pair.Key, pair.Value)));
        Assert.Same(FetchedSet.None, unbuilt.Fetched);
        Assert.Same(tests, unbuilt.Tests);
        Assert.Null(unbuilt.PathReserve);
        Assert.Null(unbuilt.Hold);
        Assert.Null(unbuilt.ExpectedBuildBytes);
        Assert.Empty(unbuilt.ExpectedBuildSource);
        Assert.False(unbuilt.Force);

        var googletest = Directory.CreateDirectory(Path.Combine(build, "_deps", "googletest-src")).FullName;

        File.WriteAllText(
            Path.Combine(build, BuildDirectoryGuard.CMakeCacheFileName),
            $"FETCHCONTENT_BASE_DIR:PATH={Path.Combine(build, "_deps").Replace('\\', '/')}\nFETCHCONTENT_SOURCE_DIR_GOOGLETEST:PATH=\n");
        File.WriteAllText(
            Path.Combine(build, BuildRecord.FileName),
            new BuildRecord(null, variant.DirectoryName, null, new Dictionary<string, string>(), new Dictionary<string, DateTime>()) { Bytes = 4096 }.Write());

        var built = service.Subject(work, arms, force: true, new RepositoryTreesFound([new RepositoryTree(temp.Path, RepositoryTree.MainCheckout)]), records: true);

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
    /// A runner <c>mutations.runReads</c> names that no TEST-RED row of the registry names is refused where the registry
    /// is read: an entry for a runner that was renamed or misspelled would silently cover nothing, and every key it was
    /// meant to move would hold for ever.
    /// </summary>
    [Fact]
    public async Task ARunReadsRunnerNoTestRedRowNames_IsRefusedWhereTheRegistryIsRead()
    {
        using var temp = new TempDirectory();
        var (service, context) = Prepare(temp, Sweepable(new MutationSettings
        {
            Registry = Registry,
            TextDirectory = "mutations/texts",
            ReportArgs = ["--gtest_output=xml:{report}"],
            RunReads = new() { ["fixture_tests"] = ["data"], ["fixture_testz"] = ["data"] },
        }));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => service.ReadAsync(context, null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            "mutations.runReads names runner 'fixture_testz', which no TEST-RED row of 'mutations/arms.txt' names: an entry no arm runs covers nothing. "
            + "Name the runner as its A row does, or leave it out.",
            refusal.Message);
    }

    /// <summary>
    /// A sweep typed on this machine names the record it writes, in its lines and in its ledger's document, as a listing
    /// does; a self-test, and a sweep this machine runs for another machine, record nothing and name nothing.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ASweepThatRecordsNamesTheRecord_InItsLinesAndItsLedger(bool selfTest, bool onAHost)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Hosts.Wsl["Example-Linux"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" };

        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "x86_64" });
        var (service, _) = Prepare(temp, config, harness: harness, inspector: inspector);
        var record = Path.Combine(temp.Path, HarnessLayout.DirectoryName, HarnessLayout.MutationRecordDirectoryName);

        var outcome = await service.RunAsync(
            new MutationRequest(temp.Path, ["native"], null, Json: true, Here: onAHost ? HostId.Wsl("Example-Linux") : null, SelfTest: selfTest),
            RunId.New(),
            TestContext.Current.CancellationToken);

        using (var document = JsonDocument.Parse(Assert.Single(outcome.Data)))
        {
            if (selfTest || onAHost)
            {
                Assert.False(document.RootElement.TryGetProperty("mutationRecord", out _));
            }
            else
            {
                Assert.Equal(record, document.RootElement.GetProperty("mutationRecord").GetString());
            }
        }

        await service.RunAsync(
            new MutationRequest(temp.Path, ["native"], null, Here: onAHost ? HostId.Wsl("Example-Linux") : null, SelfTest: selfTest),
            RunId.New(),
            TestContext.Current.CancellationToken);

        if (selfTest || onAHost)
        {
            Assert.DoesNotContain($"the record is '{record}'", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"check-mutations: the record is '{record}'", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A sweep typed by hand in a copy the harness synced - a host's - records nothing: the record of verdicts is the main
    /// checkout's on the machine the copy was synced from, and a record kept in the copy would hold what that machine
    /// never sees and decide what a later sweep typed there holds. It says so once, under --json too, names no record in
    /// its lines or its ledger, and writes none; under --pending it holds nothing, so every selected arm is swept.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASweepTypedByHandInASyncedCopy_RecordsNothing_AndSaysSoOnce(bool pending)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "x86_64" });
        var (service, context) = Prepare(temp, Sweepable(), harness: harness, inspector: inspector, syncedCopy: true);
        var record = new ArmVerdictRecord(harness.FileSystem, context.Layout);

        var outcome = await service.RunAsync(new MutationRequest(temp.Path, ["native"], null, Json: true, Pending: pending), RunId.New(), TestContext.Current.CancellationToken);

        using (var document = JsonDocument.Parse(Assert.Single(outcome.Data)))
        {
            Assert.False(document.RootElement.TryGetProperty("mutationRecord", out _));
        }

        await service.RunAsync(new MutationRequest(temp.Path, ["native"], null, Pending: pending), RunId.New(), TestContext.Current.CancellationToken);

        var said = "check-mutations: WARN - this tree is a copy the harness synced, so this sweep records nothing: the record of "
            + "verdicts is kept in the main checkout it was synced from"
            + (pending ? ", and --pending holds nothing here, so every selected arm is swept" : string.Empty);
        var warnings = harness.StandardError.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        Assert.Equal(2, warnings.Count(line => line == said));
        Assert.DoesNotContain("the record is '", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(record.Directory), "a sweep typed in a synced copy wrote a record there");
    }

    /// <summary>
    /// A sweep, or a self-test, in a tree whose git does not ignore what it would make in the slot its workers are kept
    /// in - a branch whose rules predate init's - is refused before anything is made, naming init, and the run ends
    /// there: a worker committed by <c>git add -A</c> would travel to every clone. It is asked of the tree on the machine
    /// that makes the workers, a host sweeping one leg of a run as this machine. Once the rule is there, the same sweep
    /// goes on to read the tree its workers are synced from.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ASweepInATreeWhoseGitDoesNotIgnoreItsWorkers_IsRefusedNamingInit_WithNothingMade(bool selfTest, bool onAHost)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Hosts.Wsl["Example-Linux"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" };

        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "x86_64" });
        var (service, _) = Prepare(temp, config, harness: harness, inspector: inspector);
        var request = new MutationRequest(temp.Path, ["native"], null, Here: onAHost ? HostId.Wsl("Example-Linux") : null, SelfTest: selfTest);

        // Rules that predate init's: they ignore the build, and nothing of the slot.
        temp.WriteFile(".gitignore", "/build/\n");

        var refusal = await service.RunAsync(request, RunId.New(), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.StartsWith($"git does not ignore '{HarnessLayout.MutationsDirectoryName}/", refusal.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            $"{(selfTest ? WorkerFamily.SelfTestMark : WorkerFamily.OwnMark)}-1', so the mutation workers a sweep makes in '{temp.Path}' would be committed. "
            + $"Run 'dssharness init', which keeps {HarnessLayout.MutationsDirectoryName} in git and everything made in it out. Nothing was swept.",
            refusal.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine(HarnessLayout.MutationsDirectoryName)), "a refused sweep made a worker");
        Assert.False(Directory.Exists(Fixture(temp)), "a refused self-test wrote its fixture");
        Assert.DoesNotContain("worker 1", harness.StandardOutput.ToString(), StringComparison.Ordinal);

        IgnoreTheSlots(temp.Path);

        var swept = await service.RunAsync(request, RunId.New(), TestContext.Current.CancellationToken);

        // Past the refusal, to the tree its workers are synced from: what it then comes to is no business of this test's.
        Assert.NotEqual(HarnessExit.Refused, swept.ExitCode);
        Assert.Contains("', once for every worker", harness.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A sweep whose tree git cannot answer in - whether it ignores what the sweep would make there - leaves its leg
    /// skipped-unavailable, in git's own words, with nothing made, as a runner's leg is left: no answer either way, and no
    /// verdict on the code, so never the leg failing.
    /// </summary>
    [Fact]
    public async Task ASweepWhoseTreeGitCannotSayItIgnoresItsWorkersIn_LeavesTheLegUnavailable()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var git = new InterceptingGitClient(harness.GitClient)
        {
            IsIgnoredFailure = new HarnessException(HarnessExit.CommandFailed, "fatal: not a git repository: .git"),
        };
        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "x86_64" });
        var (service, _) = Prepare(temp, Sweepable(), harness: harness, inspector: inspector, git: git);

        var outcome = await service.RunAsync(new MutationRequest(temp.Path, ["native"], null, Json: true), RunId.New(), TestContext.Current.CancellationToken);

        using var document = System.Text.Json.JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("skipped-unavailable", leg.GetProperty("verdict").GetString());
        Assert.StartsWith(
            $"git could not say whether it ignores '{HarnessLayout.MutationsDirectoryName}/", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.EndsWith(
            $"', so whether the mutation workers a sweep makes in '{temp.Path}' would be committed is unknown. Nothing has run. "
            + "fatal: not a git repository: .git",
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);

        var slot = temp.Combine(HarnessLayout.MutationsDirectoryName);

        Assert.Empty(Directory.Exists(slot) ? Directory.EnumerateDirectories(slot) : []);
    }

    /// <summary>
    /// A sweep reads the tree its leg's work happens in, and keeps its workers in that tree - its own and its
    /// self-test's alike - never the tree the command reads here, where the two are not one.
    /// </summary>
    [Fact]
    public async Task ASweep_ReadsTheTreeItsLegsWorkHappensIn_AndKeepsItsWorkersInThatTree()
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var (service, context) = Prepare(temp, config);
        var arms = await service.ReadAsync(context, null, TestContext.Current.CancellationToken);
        var variant = VariantKey.For(config, config.Legs["native"], "linux");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };
        var works = temp.Combine("where", "it", "works");
        var work = new LegWork(
            new PlacedLeg("native", config.Legs["native"], host, config.Projects[0], variant, temp.Path, works, variant.DirectoryUnder(works), new LocalHostConfig(), Emulated: false),
            context,
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);

        var subject = service.Subject(work, arms, force: false, new RepositoryTreesFound([new RepositoryTree(temp.Path, RepositoryTree.MainCheckout)]), records: true);
        var selfTest = MutationService.SelfTestSubject(work, MutationService.SelfTestArms(null), force: false, temp.Combine("data", MutationFixture.DirectoryName));

        Assert.Equal(works, subject.TreeRoot);
        Assert.Equal(MutationWorkers.Of(works, variant), subject.Workers);
        Assert.Equal(MutationWorkers.Of(works, variant, selfTest: true), selfTest.Workers);
    }

    /// <summary>
    /// A leg's sweep records each verdict in the record of verdicts in the main checkout, whichever tree it sweeps - an
    /// agent's worktree's, so its verdicts count for the main checkout - naming that tree as a line names it.
    /// </summary>
    [Fact]
    public async Task AVerdict_IsRecordedInTheMainCheckout_WhicheverTreeSwept()
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var (service, main) = Prepare(temp, config);
        var arms = await service.ReadAsync(main, null, TestContext.Current.CancellationToken);
        var worktree = Path.Combine(main.Layout.WorktreesDirectoryUnder(config.Worktrees.Root), "xa");
        var context = new HarnessContext(new HarnessLayout(worktree, temp.Path), config);
        var variant = VariantKey.For(config, config.Legs["native"], "linux");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };

        LegWork In(HarnessContext of, string tree) => new(
            new PlacedLeg("native", config.Legs["native"], host, config.Projects[0], variant, tree, tree, variant.DirectoryUnder(tree), new LocalHostConfig(), Emulated: false),
            of,
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);

        var inWorktree = service.Subject(In(context, worktree), arms, force: false, new RepositoryTreesFound([]), records: true);
        var inMain = service.Subject(In(main, temp.Path), arms, force: false, new RepositoryTreesFound([]), records: true);
        var record = new ArmVerdictRecord(new HarnessFactory().FileSystem, main.Layout);

        Assert.Equal(("worktree xa", RepositoryTree.MainCheckout), (inWorktree.SweptIn, inMain.SweptIn));

        inWorktree.Sink!.Judged(
            "charge",
            new RecordedVerdict
            {
                Verdict = "passed",
                Run = "20261012-145059-ba433418",
                At = DateTimeOffset.UnixEpoch,
                SweptIn = inWorktree.SweptIn,
                Key = "k",
                JudgedOn = new JudgedOn
                {
                    Judge = ArmJudge.Revision,
                    Rows = new RowDigests("rows"),
                    Texts = new Dictionary<string, string?>(StringComparer.Ordinal),
                    Sites = new Dictionary<string, string?>(StringComparer.Ordinal),
                    Built = new Dictionary<string, string>(StringComparer.Ordinal),
                    Compilers = [],
                    Settings = new SweepSettings("build", "run", "judge"),
                    RunReads = new Dictionary<string, string>(StringComparer.Ordinal),
                    Host = "local",
                },
            },
            []);

        Assert.Null(inWorktree.Sink.Unrecorded);
        Assert.True(File.Exists(Path.Combine(temp.Path, HarnessLayout.DirectoryName, HarnessLayout.MutationRecordDirectoryName, "native.json")));
        Assert.Equal("worktree xa", Assert.Single(record.Read("native").Arms["charge"]).SweptIn);
        Assert.False(Directory.Exists(Path.Combine(worktree, HarnessLayout.DirectoryName)));
    }

    /// <summary>
    /// A listing finds each file of the tree a recorded table names as the tree's own file system compares names: a file
    /// the table spells in another case is the file the tree holds where the file system folds case, and the arm is
    /// unmoved here; where it does not fold, the file is gone, and the arm pending for it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AListing_FindsEachFileATableNames_AsTheTreesFileSystemComparesNames(bool folds)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();
        var token = TestContext.Current.CancellationToken;
        var (service, context) = Prepare(temp, config, harness: harness, files: new CaseOf(harness.FileSystem, temp.Path, folds));

        temp.WriteFile(Path.Combine("src", "fixture.cpp"), "bool within(int c, int b) { return c <= b; }\n");

        harness.WriteConfig(temp.Path, config);

        var arm = (await service.ReadAsync(context, ["charge"], token)).Selected[0];
        string Held(string path) => FileContentHash.Of(File.ReadAllBytes(temp.Combine(path)));
        var table = new BuiltFromTable
        {
            Target = "fixture",
            Lines = "lines",
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["SRC/Fixture.cpp"] = Held("src/fixture.cpp") },
            Fetched = new Dictionary<string, string>(StringComparer.Ordinal),
            Made = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
            Outside = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
        };
        var on = new JudgedOn
        {
            Judge = ArmJudge.Revision,
            Rows = JudgedOn.RowsOf(arm),
            Texts = JudgedOn.TextsOf(arm).ToDictionary(path => path, path => (string?)Held(path), StringComparer.Ordinal),
            Sites = arm.Sites.ToDictionary(site => site.Site, site => (string?)Held(site.Site), StringComparer.Ordinal),
            Built = new Dictionary<string, string>(StringComparer.Ordinal) { ["fixture"] = table.Digest() },
            Compilers = [],
            Settings = SweepSettings.Of(config, config.Legs["native"], config.Hosts.SettingsFor(HostId.Local)),
            RunReads = new Dictionary<string, string>(StringComparer.Ordinal),
            Host = HostId.Local.ToString(),
        };

        Assert.Null(new ArmVerdictRecord(harness.FileSystem, context.Layout).Merge(
            "native",
            "charge",
            new RecordedVerdict { Verdict = "passed", Run = "20261012-145059-ba433418", At = DateTimeOffset.UnixEpoch, SweptIn = RepositoryTree.MainCheckout, Key = on.Key(), JudgedOn = on },
            [table]));

        var listed = await service.ListAsync(new MutationRequest(temp.Path, null, ["charge"], Json: true, List: true), token);

        using var document = System.Text.Json.JsonDocument.Parse(Assert.Single(listed.Data));
        var listedArm = document.RootElement.GetProperty("legs")[0].GetProperty("arms")[0];

        Assert.Equal(folds ? "unmoved" : "pending", listedArm.GetProperty("state").GetString());

        if (!folds)
        {
            Assert.Equal("what 'fixture' is built from moved: SRC/Fixture.cpp (gone)", listedArm.GetProperty("why")[0].GetProperty("said").GetString());
        }
    }

    /// <summary>
    /// A listing reads a leg that names no host as though it ran where the verdict shown was judged - which host a sweep
    /// would place it on is no part of what a listing reads - so the environment its build is given is the one that host
    /// declares: a leg placed on a WSL distribution that declares an environment is unmoved right after its sweep, and
    /// moved once what that distribution declares changes, whatever this machine's own section says.
    /// </summary>
    [Fact]
    public async Task AListing_OfALegNamingNoHost_ReadsTheEnvironmentOfTheHostItsVerdictWasJudgedOn()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();
        var token = TestContext.Current.CancellationToken;
        var ubuntu = HostId.Wsl("Ubuntu");

        config.Hosts.Wsl["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo", Env = { ["MODE"] = "fast" } };

        var (service, context) = Prepare(temp, config, harness: harness);

        temp.WriteFile(Path.Combine("src", "fixture.cpp"), "bool within(int c, int b) { return c <= b; }\n");
        harness.WriteConfig(temp.Path, config);

        var arm = (await service.ReadAsync(context, ["charge"], token)).Selected[0];
        string Held(string path) => FileContentHash.Of(File.ReadAllBytes(temp.Combine(path)));
        var on = new JudgedOn
        {
            Judge = ArmJudge.Revision,
            Rows = JudgedOn.RowsOf(arm),
            Texts = JudgedOn.TextsOf(arm).ToDictionary(path => path, path => (string?)Held(path), StringComparer.Ordinal),
            Sites = arm.Sites.ToDictionary(site => site.Site, site => (string?)Held(site.Site), StringComparer.Ordinal),
            Built = new Dictionary<string, string>(StringComparer.Ordinal),
            Compilers = [],
            Settings = SweepSettings.Of(config, config.Legs["native"], config.Hosts.SettingsFor(ubuntu)),
            RunReads = new Dictionary<string, string>(StringComparer.Ordinal),
            Host = ubuntu.ToString(),
        };

        Assert.Null(new ArmVerdictRecord(harness.FileSystem, context.Layout).Merge(
            "native",
            "charge",
            new RecordedVerdict { Verdict = "passed", Run = "20261012-145059-ba433418", At = DateTimeOffset.UnixEpoch, SweptIn = RepositoryTree.MainCheckout, Key = on.Key(), JudgedOn = on },
            []));

        async Task<JsonElement> ListedAsync()
        {
            var listed = await service.ListAsync(new MutationRequest(temp.Path, null, ["charge"], Json: true, List: true), token);

            using var document = JsonDocument.Parse(Assert.Single(listed.Data));

            return document.RootElement.GetProperty("legs")[0].GetProperty("arms")[0].Clone();
        }

        config.Hosts.Local.Env["MODE"] = "slow";

        Assert.Equal("unmoved", (await ListedAsync()).GetProperty("state").GetString());

        config.Hosts.Wsl["Ubuntu"].Env["MODE"] = "slow";

        var moved = await ListedAsync();

        Assert.Equal("pending", moved.GetProperty("state").GetString());
        Assert.Equal("build-environment-changed", moved.GetProperty("why")[0].GetProperty("code").GetString());
    }

    /// <summary>
    /// What a sweep's key holds of the settings is what its leg is swept with, resolved once with the leg: the variant
    /// placed, the environment its host declares, and the test section of the system its host runs, however the leg
    /// spells its system - so a leg declaring "Linux" is keyed on the test.linux settings its runs start with, as a leg
    /// declaring "linux" is.
    /// </summary>
    [Theory]
    [InlineData("Linux")]
    [InlineData("linux")]
    public async Task ASubjectsSettings_AreThoseItsRunsStartWith_HoweverTheLegSpellsItsSystem(string os)
    {
        async Task<SweepSettings> SettingsWith(string mode, string hostMode = "quick")
        {
            using var temp = new TempDirectory();
            var leg = new LegConfig
            {
                Os = os,
                Processor = "x86_64",
                Config = "debug",
                Toolchain = "gcc",
                Project = "app",
                Test = new TestConfig
                {
                    All = new TestInvocation { Runner = "fixture_tests", SuccessPattern = "passed" },
                    Linux = new TestInvocation { Env = new Dictionary<string, string> { ["MODE"] = mode } },
                },
            };
            var config = Sweepable();

            config.Legs["native"] = leg;

            var (service, context) = Prepare(temp, config);
            var arms = await service.ReadAsync(context, null, TestContext.Current.CancellationToken);
            var variant = VariantKey.For(config, leg, PlatformNames.Linux);
            var host = new HostReport { Host = HostId.Local, Os = PlatformNames.Linux, Processor = "x86_64" };
            var work = new LegWork(
                new PlacedLeg(
                    "native",
                    leg,
                    host,
                    config.Projects[0],
                    variant,
                    temp.Path,
                    temp.Path,
                    variant.DirectoryUnder(temp.Path),
                    new LocalHostConfig { Env = { ["HOST_MODE"] = hostMode } },
                    Emulated: false),
                context,
                RunId.New(),
                temp.Combine("runs", "r1"),
                Time: false);

            return service.Subject(work, arms, force: false, new RepositoryTreesFound([]), records: false).SweepSettings;
        }

        var quick = await SettingsWith("quick");
        var thorough = await SettingsWith("thorough");

        Assert.NotEqual(quick.Run, thorough.Run);
        Assert.Equal((quick.Build, quick.Judge), (thorough.Build, thorough.Judge));

        var hosted = await SettingsWith("quick", hostMode: "thorough");

        Assert.NotEqual(quick.Build, hosted.Build);
        Assert.Equal((quick.Run, quick.Judge), (hosted.Run, hosted.Judge));
    }

    /// <summary>
    /// Nothing of a self-test is recorded: its subject has no sink. A leg swept for another machine keeps no record
    /// where it runs - the host's copy holds none - so its subject hands each verdict to a sink that tells the machine
    /// that dispatched the leg, as each arm ends there; a leg swept here hands them to the record-writing sink.
    /// </summary>
    [Fact]
    public async Task ASelfTest_RecordsNothing_ALegSweptForAnotherMachineTellsItsDispatcher_AndALegHereWritesTheRecord()
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var (service, context) = Prepare(temp, config);
        var arms = await service.ReadAsync(context, null, TestContext.Current.CancellationToken);
        var variant = VariantKey.For(config, config.Legs["native"], "linux");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };
        var work = new LegWork(
            new PlacedLeg("native", config.Legs["native"], host, config.Projects[0], variant, temp.Path, temp.Path, variant.DirectoryUnder(temp.Path), new LocalHostConfig(), Emulated: false),
            context,
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);

        var selfTest = MutationService.SelfTestSubject(work, MutationService.SelfTestArms(null), force: false, temp.Combine("data", MutationFixture.DirectoryName));
        var forAnother = service.Subject(work, arms, force: false, new RepositoryTreesFound([]), records: false);
        var here = service.Subject(work, arms, force: false, new RepositoryTreesFound([]), records: true);

        Assert.Null(selfTest.Sink);
        Assert.True(selfTest.ComparesKeys);
        Assert.IsType<NoteSink>(forAnother.Sink);
        Assert.IsType<RecordSink>(here.Sink);
    }

    /// <summary>
    /// A leg swept under --pending is given what the main checkout's record holds of the arms it drives and of no other -
    /// those --arms leaves out are not weighed - and a leg swept without it is given nothing, and drives every arm.
    /// </summary>
    [Fact]
    public async Task UnderPending_ALegIsGivenWhatTheRecordHoldsOfTheArmsItDrives_AndOfNoOther()
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var (service, context) = Prepare(temp, config);
        var every = await service.ReadAsync(context, null, TestContext.Current.CancellationToken);
        var charge = await service.ReadAsync(context, ["charge"], TestContext.Current.CancellationToken);
        var variant = VariantKey.For(config, config.Legs["native"], "linux");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };
        var work = new LegWork(
            new PlacedLeg("native", config.Legs["native"], host, config.Projects[0], variant, temp.Path, temp.Path, variant.DirectoryUnder(temp.Path), new LocalHostConfig(), Emulated: false),
            context,
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);
        var record = new ArmVerdictRecord(new HarnessFactory().FileSystem, context.Layout);

        foreach (var arm in new[] { "charge", "depth" })
        {
            Assert.Null(record.Merge("native", arm, new RecordedVerdict { Verdict = "passed", Run = "r0", At = DateTimeOffset.UnixEpoch, SweptIn = RepositoryTree.MainCheckout }, []));
        }

        var narrowed = service.Subject(work, charge, force: false, new RepositoryTreesFound([]), records: true, pending: true);
        var whole = service.Subject(work, every, force: false, new RepositoryTreesFound([]), records: true, pending: true);
        var plain = service.Subject(work, every, force: false, new RepositoryTreesFound([]), records: true);

        Assert.Equal(["charge"], narrowed.Recorded!.Arms.Keys);
        Assert.Equal(["charge", "depth"], whole.Recorded!.Arms.Keys.Order(StringComparer.Ordinal));
        Assert.Null(plain.Recorded);
    }

    /// <summary>
    /// Each arm a host's sweep judges is recorded on the machine that dispatched the leg as the arm ends there: a note
    /// on the host's standard error, which that machine reads and never relays, feeding the same record-writing sink a
    /// leg swept here feeds - the tables the entry names written before it. The host's final answer carries the arms as
    /// today and no record material: it travels one way only.
    /// </summary>
    [Fact]
    public async Task AHostsArm_IsRecordedOnTheDispatcherAsItEnds_BeforeTheHostAnswers()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();
        var pi = HostId.Ssh("pi");

        config.Hosts.Ssh["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" };
        config.Legs["pi"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Toolchain = "gcc", Project = "app", Ssh = "pi" };

        var table = new BuiltFromTable
        {
            Target = "fixture_tests",
            Lines = "lines",
            Files = new Dictionary<string, string>(StringComparer.Ordinal) { ["src/fixture.cpp"] = "hash" },
            Fetched = new Dictionary<string, string>(StringComparer.Ordinal),
            Made = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
            Outside = new Dictionary<string, FilesIn>(StringComparer.Ordinal),
        };
        var verdict = new RecordedVerdict
        {
            Verdict = "passed",
            Run = "20261012-145059-ba433418",
            At = DateTimeOffset.UnixEpoch,
            SweptIn = "/home/pi/repo",
            Key = "k",
            JudgedOn = new JudgedOn
            {
                Judge = ArmJudge.Revision,
                Rows = new RowDigests("rows"),
                Texts = new Dictionary<string, string?>(StringComparer.Ordinal),
                Sites = new Dictionary<string, string?>(StringComparer.Ordinal),
                Built = new Dictionary<string, string>(StringComparer.Ordinal) { ["fixture_tests"] = table.Digest() },
                Compilers = [],
                Settings = new SweepSettings("build", "run", "judge"),
                RunReads = new Dictionary<string, string>(StringComparer.Ordinal),
                Host = "ssh pi",
            },
        };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            var nonce = Request(command).Nonce!;

            foreach (var (kind, text) in new[]
            {
                (NoteSink.TableKind, JsonSerializer.Serialize(table, JsonStateFile.LineOptions)),
                (NoteSink.ArmKind, JsonSerializer.Serialize(new NotedArm("charge", verdict), JsonStateFile.LineOptions)),
            })
            {
                foreach (var line in NoteLines(nonce, kind, text))
                {
                    command.OnErrorLine?.Invoke(line);
                }
            }

            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "pi", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });
        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "windows", Processor = "x86_64" }
            : new HostReport { Host = host, Os = "linux", Processor = "arm64", Session = new HostSession(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness") });
        var (service, context) = Prepare(temp, config, harness: harness, inspector: inspector, hosts: hosts);

        var outcome = await service.RunAsync(new MutationRequest(temp.Path, ["pi"], null, Json: true), RunId.New(), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        var record = new ArmVerdictRecord(harness.FileSystem, context.Layout);
        var entry = Assert.Single(record.Read("pi").Arms["charge"]);

        Assert.Equal(("passed", "20261012-145059-ba433418", "k"), (entry.Verdict, entry.Run, entry.Key));
        Assert.True(File.Exists(record.TableFile("pi", table.Digest())));
    }

    /// <summary>
    /// A host cut off part way - its transport failing before it said how it finished - leaves on the dispatcher what it
    /// had judged: each note was read and recorded as it arrived, so a sweep stopped part way, or cut off, keeps what it
    /// had judged.
    /// </summary>
    [Fact]
    public async Task AHostCutOffPartWay_LeavesWhatItHadJudgedRecorded()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Hosts.Ssh["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" };
        config.Legs["pi"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Toolchain = "gcc", Project = "app", Ssh = "pi" };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            var nonce = Request(command).Nonce!;
            var verdict = new RecordedVerdict { Verdict = "survived", Run = "20261012-145059-ba433418", At = DateTimeOffset.UnixEpoch, SweptIn = "/home/pi/repo" };

            foreach (var line in NoteLines(nonce, NoteSink.ArmKind, JsonSerializer.Serialize(new NotedArm("charge", verdict), JsonStateFile.LineOptions)))
            {
                command.OnErrorLine?.Invoke(line);
            }

            return HostResults.Failed(255, "ssh: connection closed");
        });
        var inspector = new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "arm64", Session = new HostSession(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness") });
        var (service, context) = Prepare(temp, config, harness: harness, inspector: inspector, hosts: hosts);

        var cut = await service.RunAsync(new MutationRequest(temp.Path, ["pi"], null, Json: true), RunId.New(), TestContext.Current.CancellationToken);

        // The sweep is no pass: the host never said how it finished, so its leg reached no verdict.
        Assert.NotEqual(HarnessExit.Success, cut.ExitCode);

        var entry = Assert.Single(new ArmVerdictRecord(harness.FileSystem, context.Layout).Read("pi").Arms["charge"]);

        Assert.Equal(("survived", "20261012-145059-ba433418"), (entry.Verdict, entry.Run));
    }

    /// <summary>
    /// Under --pending each dispatched leg's host is given what the record holds of that leg's own arms alone - its
    /// newest entry and every recorded passed, with the tables they name - as bytes in a hidden option of that leg
    /// alone, beside the run's own --pending: held and pending are decided there, by the leg's own reading and worker,
    /// from the one record there is, on the machine that keeps it.
    /// </summary>
    [Fact]
    public async Task ARemoteLeg_IsGivenWhatTheRecordHoldsOfItsOwnArms_AndDecidesThere()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();
        var pi = HostId.Ssh("pi");
        var mac = HostId.Ssh("mac");

        config.Hosts.Ssh["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" };
        config.Hosts.Ssh["mac"] = new SshHostConfig { RepositoryPath = "/home/mac/repo" };
        config.Legs["pi"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Toolchain = "gcc", Project = "app", Ssh = "pi" };
        config.Legs["mac"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Toolchain = "gcc", Project = "app", Ssh = "mac" };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            var leg = Request(command).Arguments[2].Text;

            ScriptedHostCommands.Answer(command, $$"""{"legs": [{"leg": "{{leg}}", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });
        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "windows", Processor = "x86_64" }
            : new HostReport { Host = host, Os = "linux", Processor = "arm64", Session = new HostSession(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness") });

        // The charge arm runs on both legs, the depth arm on pi alone.
        var (service, context) = Prepare(
            temp,
            config,
            [TestRedArm, "C | charge | Fixture.Charge | red", BuildRedArm, "B | depth | mutations/texts/depth.control-before | mutations/texts/depth.control-after | builds", "S | depth | pi | where its site is"],
            harness: harness,
            inspector: inspector,
            hosts: hosts);
        var record = new ArmVerdictRecord(harness.FileSystem, context.Layout);

        foreach (var (leg, arm) in new[] { ("pi", "charge"), ("mac", "charge"), ("pi", "depth") })
        {
            Assert.Null(record.Merge(leg, arm, new RecordedVerdict { Verdict = "passed", Run = "r0", At = DateTimeOffset.UnixEpoch, SweptIn = RepositoryTree.MainCheckout }, []));
        }

        var outcome = await service.RunAsync(new MutationRequest(temp.Path, ["pi", "mac"], null, Json: true, Pending: true), RunId.New(), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        var held = hosts.Calls.ToDictionary(
            call => call.Connection.Host.ToString(),
            call => Sent(call.Command));

        Assert.Equal(["--pending", "--held"], held["ssh pi"].Arguments[^3..^1]);
        Assert.Equal(["charge", "depth"], held["ssh pi"].Held.Arms.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["--pending", "--held"], held["ssh mac"].Arguments[^3..^1]);
        Assert.Equal(["charge"], held["ssh mac"].Held.Arms.Keys);
    }

    /// <summary>
    /// What the record holds is the machine that keeps it's to read: a host given --pending and no --held holds nothing
    /// and sweeps every selected arm, and what a dispatch gives it stands as given, however unlike the record of any leg
    /// swept here.
    /// </summary>
    [Fact]
    public async Task AHostGivenNoRecord_SweepsEverySelectedArm()
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var (service, context) = Prepare(temp, config);
        var every = await service.ReadAsync(context, null, TestContext.Current.CancellationToken);
        var variant = VariantKey.For(config, config.Legs["native"], "linux");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };
        var work = new LegWork(
            new PlacedLeg("native", config.Legs["native"], host, config.Projects[0], variant, temp.Path, temp.Path, variant.DirectoryUnder(temp.Path), new LocalHostConfig(), Emulated: false),
            context,
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);

        var given = new RecordedArms
        {
            Arms = new Dictionary<string, IReadOnlyList<RecordedVerdict>>(StringComparer.OrdinalIgnoreCase)
            {
                ["charge"] = [new RecordedVerdict { Verdict = "passed", Run = "r0", At = DateTimeOffset.UnixEpoch, SweptIn = RepositoryTree.MainCheckout, Key = "k" }],
            },
        };

        var none = service.Subject(work, every, force: false, new RepositoryTreesFound([]), records: false, pending: true);
        var held = service.Subject(work, every, force: false, new RepositoryTreesFound([]), records: false, pending: true, held: given);

        Assert.Equal([], none.Recorded!.Arms.Keys.Order());
        Assert.Null(none.Recorded.Unread);
        Assert.Equal(["charge"], held.Recorded!.Arms.Keys);
    }

    /// <summary>
    /// A listing of a tree written while it is read is refused, in the reading's own sentence, naming the path that moved:
    /// a listing of a tree that never existed would say <c>unmoved here</c> of files as no moment held them, so it is not
    /// printed - the arm the registry read has no row.
    /// </summary>
    [Fact]
    public async Task AListingOfATreeWrittenWhileItIsRead_IsRefused_NamingThePath_AndPrintsNoRow()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();
        var written = temp.Combine("mutations", "texts", "charge.before");
        var token = TestContext.Current.CancellationToken;

        // Once every file of the tree is hashed, as the reading's second look begins, the tree is written to.
        var (service, _) = Prepare(
            temp,
            config,
            reading: SyncKit.Service(
                harness,
                fileSystem: new WrittenAsTheSecondLookBegins(
                    harness.FileSystem,
                    temp.Path,
                    () =>
                    {
                        File.WriteAllText(written, "c < b, and written since\n");
                        File.SetLastWriteTimeUtc(written, File.GetLastWriteTimeUtc(written).AddSeconds(4));
                    })));

        // The real reading reads the tree's own configuration, which a listing's does; the command's own is the one it
        // loaded, given to it by the loader a test doubles.
        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig());

        var refused = await Assert.ThrowsAsync<HarnessException>(() => service.ListAsync(new MutationRequest(temp.Path, null, null, List: true), token));

        Assert.Equal(LegExit.InputsMoved, refused.ExitCode);
        Assert.Equal(
            $"'{temp.Path}' changed while it was read: 'mutations/texts/charge.before' was written. Let it settle, then run again.",
            refused.Message);
    }

    /// <summary>
    /// A note whose base64 decodes to no JSON this build writes - a piece lost, a marker missed, a truncation that lands
    /// aligned - is dropped as a damaged one is: the record then lacks that arm, which is pending, and never does the
    /// dispatcher's run fail for what a host said in passing.
    /// </summary>
    [Fact]
    public async Task ACorruptNote_IsDropped_NeverFailingTheDispatchersRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Hosts.Ssh["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" };
        config.Legs["pi"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Toolchain = "gcc", Project = "app", Ssh = "pi" };

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            // Decodable base64 holding no JSON: what a damaged note arrives as.
            command.OnErrorLine?.Invoke(HostAgentProtocol.NoteLine(Request(command).Nonce!, NoteSink.ArmKind, last: true, Convert.ToBase64String("not json"u8)));

            ScriptedHostCommands.Answer(command, """{"legs": [{"leg": "pi", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });
        var inspector = new RecordingInspector(host => host.Kind == HostKind.Local
            ? new HostReport { Host = host, Os = "windows", Processor = "x86_64" }
            : new HostReport { Host = host, Os = "linux", Processor = "arm64", Session = new HostSession(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness") });
        var (service, context) = Prepare(temp, config, harness: harness, inspector: inspector, hosts: hosts);

        var outcome = await service.RunAsync(new MutationRequest(temp.Path, ["pi"], null, Json: true), RunId.New(), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Empty(new ArmVerdictRecord(harness.FileSystem, context.Layout).Read("pi").Arms);
    }

    /// <summary>
    /// The real file system, save that <paramref name="move"/> runs once, as the tree's root is listed again after its
    /// files began to be hashed: as a reading's second look begins, every hash taken.
    /// </summary>
    private sealed class WrittenAsTheSecondLookBegins(IFileSystem inner, string root, Action move) : PassThroughFileSystem(inner)
    {
        private bool _hashing;
        private bool _moved;

        public override Stream OpenRead(string path)
        {
            _hashing = true;
            return base.OpenRead(path);
        }

        public override IEnumerable<string> EnumerateFiles(string path, bool recursive)
        {
            if (_hashing && !_moved && !recursive
                && string.Equals(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                _moved = true;
                move();
            }

            return base.EnumerateFiles(path, recursive);
        }
    }

    /// <summary>The run request a host was sent, read from what its transport carried.</summary>
    private static HostAgentRequest Request(HostCommand command)
        => JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput.Read(), HostAgentProtocol.JsonOptions)!;

    /// <summary>The arguments a host sweeping one leg was given, as it reads them, and what its leg's <c>--held</c> holds.</summary>
    private static (string[] Arguments, RecordedArms Held) Sent(HostCommand command)
    {
        var arguments = Request(command).Arguments.Select(argument => argument.Text).ToArray();

        return (arguments, RecordedArms.OfCarried(Convert.FromBase64String(arguments[Array.IndexOf(arguments, "--held") + 1])));
    }

    /// <summary>The note carrying <paramref name="text"/> of <paramref name="kind"/>, as the host's command writes it: pieces of at most CarriedPiece bytes, each one line.</summary>
    private static IEnumerable<string> NoteLines(string nonce, string kind, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        for (var at = 0; at < bytes.Length; at += HostAgentProtocol.CarriedPiece)
        {
            var piece = new ReadOnlyMemory<byte>(bytes, at, Math.Min(HostAgentProtocol.CarriedPiece, bytes.Length - at));

            yield return HostAgentProtocol.NoteLine(nonce, kind, at + piece.Length >= bytes.Length, Convert.ToBase64String(piece.Span));
        }
    }

    /// <summary>
    /// A sweep of a leg whose own tree never built its variant on this machine counts its workers' builds by the most any
    /// other tree of the repository here recorded of it, naming whose: the main checkout's copy had never built it where a
    /// consumer's leg filled a disk, while three worktrees' copies beside it had.
    /// </summary>
    [Fact]
    public async Task ALegsSweep_WhoseOwnTreeNeverBuiltItsVariant_CountsItsWorkersByTheMostAnotherTreeHereRecorded()
    {
        using var temp = new TempDirectory();
        var config = Sweepable();
        var (service, context) = Prepare(temp, config);
        var arms = await service.ReadAsync(context, null, TestContext.Current.CancellationToken);
        var variant = VariantKey.For(config, config.Legs["native"], "linux");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };
        var work = new LegWork(
            new PlacedLeg("native", config.Legs["native"], host, config.Projects[0], variant, temp.Path, temp.Path, variant.DirectoryUnder(temp.Path), new LocalHostConfig(), Emulated: false),
            context,
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);

        foreach (var (tree, bytes) in new[] { ("plain", 4096L), ("xa", 8192L) })
        {
            var build = Directory.CreateDirectory(variant.DirectoryUnder(temp.Combine(tree))).FullName;

            File.WriteAllText(
                Path.Combine(build, BuildRecord.FileName),
                new BuildRecord(null, variant.DirectoryName, null, new Dictionary<string, string>(), new Dictionary<string, DateTime>()) { Bytes = bytes }.Write());
        }

        var subject = service.Subject(
            work,
            arms,
            force: false,
            new RepositoryTreesFound(
            [
                new RepositoryTree(temp.Path, RepositoryTree.MainCheckout),
                new RepositoryTree(temp.Combine("plain"), "worktree plain"),
                new RepositoryTree(temp.Combine("xa"), "worktree o1/xa"),
            ]),
            records: true);

        Assert.Equal((8192L, "what worktree o1/xa's copy of the same variant came to there"), (subject.ExpectedBuildBytes, subject.ExpectedBuildSource));
    }

    /// <summary>
    /// A sweep on a machine whose worktrees beside the main checkout could not be listed says so, naming the leg and why:
    /// its workers' builds are then measured by its own tree's and the main checkout's alone.
    /// </summary>
    [Fact]
    public async Task ASweepWhereTheTreesCouldNotBeListed_SaysSo_NamingTheLegAndWhy()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Hosts.Wsl["Example-Linux"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" };

        var (service, _) = Prepare(
            temp,
            config,
            harness: harness,
            inspector: new RecordingInspector(host => new HostReport { Host = host, Os = "linux", Processor = "x86_64" }),
            trees: new KnownTrees { Unlisted = "the disk is gone" });

        await service.RunAsync(
            new MutationRequest(temp.Path, ["native"], null, Json: true, Here: HostId.Wsl("Example-Linux")),
            RunId.New(),
            TestContext.Current.CancellationToken);

        Assert.Contains(
            "leg 'native': the worktrees beside the main checkout could not be listed, so a worker's build is measured by its own tree's "
            + "and the main checkout's alone: the disk is gone",
            harness.StandardError.ToString(),
            StringComparison.Ordinal);
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
    /// Its workers are kept in the leg's own tree, in the self-test's family - so two repositories self-testing with
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
        Assert.Equal(HarnessLayout.InSlot(temp.Path, HarnessLayout.MutationsDirectoryName, variant.CopyKey + "s-1"), subject.Workers.PathOf(1));

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
        Assert.Equal(HarnessLayout.InSlot(elsewhere, HarnessLayout.MutationsDirectoryName, variant.CopyKey + "s-1"), theirs.Workers.PathOf(1));
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

        InitRepository(temp.Path);

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
    /// A sweep reads what the leg's own build fetched from where the leg's configuration places that build - the copy of
    /// its tree in the tree's .legs where its key says one, never the tree's own - and makes no copy of its own: its
    /// workers are already copies of one reading, kept in the tree itself.
    /// </summary>
    [Fact]
    public void ASweepOfALegThatWorksInACopy_ReadsWhatTheCopysBuildFetched_AndMakesNoCopyOfItsOwn()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Sweepable();

        config.Legs["native"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc", Project = "app", LocalCopy = true };

        var (service, context) = Prepare(temp, config, harness: harness);

        var leg = config.Legs["native"];
        var variant = VariantKey.For(config, leg, "linux");
        var copy = LegCopies.PathOf(temp.Path, variant);
        var source = Directory.CreateDirectory(temp.Combine("vendor", "plumb")).FullName;

        File.WriteAllText(Path.Combine(source, "CMakeLists.txt"), "add_library(plumb INTERFACE)\n");

        var build = variant.DirectoryUnder(copy);

        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "CMakeCache.txt"), $"FETCHCONTENT_SOURCE_DIR_PLUMB:PATH={source.Replace('\\', '/')}\n");

        var placed = new PlacedLeg(
            "native",
            leg,
            new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" },
            config.Projects[0],
            variant,
            temp.Path,
            temp.Path,
            variant.DirectoryOn(HostId.Local, temp.Path),
            config.Hosts.SettingsFor(HostId.Local),
            Emulated: false);

        var subject = service.Subject(new LegWork(placed, context, RunId.New(), temp.Path, Time: false), MutationService.SelfTestArms(null), force: false, new RepositoryTreesFound([]), records: false);

        Assert.Equal([new FetchedSource("PLUMB", source.Replace('\\', '/'))], subject.Fetched.Found);
        Assert.Equal(temp.Path, subject.TreeRoot);
        Assert.Equal(MutationWorkers.Of(temp.Path, variant).Root, subject.Workers.Root);

        // Only what the test made is in the tree's .legs: a sweep made no copy of its own.
        Assert.Equal([copy], Directory.GetDirectories(Path.Combine(temp.Path, HarnessLayout.LegsDirectoryName)));
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
    /// arms with their rows - and every text it cites, with the context a run of it loads, a copy the harness synced
    /// where <paramref name="syncedCopy"/> says; writing where <paramref name="harness"/> writes, or a harness of its own,
    /// its hosts measured by <paramref name="inspector"/>, or as ones no leg runs on.
    /// </summary>
    private static (MutationService Service, HarnessContext Context) Prepare(
        TempDirectory temp,
        HarnessConfig config,
        string[]? rows = null,
        HarnessFactory? harness = null,
        IHostInspector? inspector = null,
        IFileSystem? files = null,
        ScriptedHostCommands? hosts = null,
        IGitClient? git = null,
        KnownTrees? trees = null,
        ISyncService? reading = null,
        bool syncedCopy = false)
    {
        harness ??= new HarnessFactory();

        InitRepository(temp.Path);
        temp.WriteFile(Registry, string.Join("\n", rows ?? Arms) + "\n");

        foreach (var (name, text) in Texts)
        {
            temp.WriteFile(Path.Combine("mutations", "texts", name), text);
        }

        var loader = HostDoubles.Loader(config, temp.Path, temp.Path, syncedCopy);

        return (
            Service(harness, temp, loader, inspector ?? new RecordingInspector(host => new HostReport { Host = host }), files, hosts, git, trees, reading),
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
        ScriptedHostCommands? hosts = null,
        IGitClient? git = null,
        KnownTrees? trees = null,
        ISyncService? reading = null)
    {
        var processes = Substitute.For<IProcessRunner>();
        var builds = Substitute.For<IBuildService>();
        var sync = SyncKit.ServiceDouble();

        builds.BuildAsync(Arg.Any<HarnessConfig>(), Arg.Any<BuildRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<BuildRequest>();

                return new BuildResult(ReachedVerdict.Of(LegVerdict.Failed, Unbuilt), request.Variant.DirectoryUnder(request.TreeRoot), [], null, null);
            });

        var legRuns = new LegRunService(
            loader,
            new LegsService(loader, inspector, new KnownTrees(), harness.Platform, harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            new LogOwnership(harness.FileSystem, harness.Output, harness.Identity),
            sync,

            // Each host's copy as the run's sync leaves it: a sync double writes nothing.
            SyncKit.CopiesSyncedBy(sync),
            new RemoteLegRunner(hosts ?? new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),
            AdmissionKit.Admission(harness, temp.Combine("state", "admission.json"), new ScriptedGauge(10), new ManualClock()),
            new ScriptedPageCache(),
            trees ?? new KnownTrees(),
            new KeepAwake(new HeldProcesses(), harness.Output),
            new DeveloperEnvironmentProvider(harness.Platform, processes, harness.FileSystem, harness.Output),
            harness.GitClient,
            harness.PathBudget,
            harness.FileSystem,
            harness.FilePermissions,
            harness.Platform,
            harness.Output);

        return new MutationService(
            loader,
            git ?? harness.GitClient,
            legRuns,
            reading ?? SyncKit.Service(harness, loader, git: git),
            SyncKit.Transport(harness),
            builds,
            processes,
            new BuildDirectoryGuard(harness.FileSystem, harness.Platform, harness.FilePermissions),
            new PhaseRunner(processes, harness.FileSystem, harness.Output),
            harness.PathBudget,
            files ?? harness.FileSystem,
            new MutationFixtureStore(harness.FileSystem, Fixture(temp)),
            harness.Identity,
            harness.Platform,
            TimeProvider.System,
            new HostNotes(CommandOrigin.Typed, harness.Output),
            harness.Output);
    }

    /// <summary>
    /// Makes <paramref name="directory"/> a repository of its own, as every tree a sweep reads is: what a sync withholds
    /// from a copy is asked of git there; and one whose git ignores what is made in its slots, as init has it do.
    /// </summary>
    private static void InitRepository(string directory)
    {
        IgnoreTheSlots(directory);

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

    /// <summary>Writes the rules init writes for the slots a tree keeps its copies in, as the tree's whole ignore file.</summary>
    private static void IgnoreTheSlots(string directory)
        => File.WriteAllText(
            Path.Combine(directory, ".gitignore"),
            string.Concat(new[] { HarnessLayout.MutationsDirectoryName, HarnessLayout.LegsDirectoryName }.Select(slot => $"/{slot}/*\n!/{slot}/{HarnessLayout.GitKeepFileName}\n")));

    /// <summary>Where the service <see cref="Service"/> builds over <paramref name="temp"/> keeps a self-test's fixture.</summary>
    private static string Fixture(TempDirectory temp) => temp.Combine("state", MutationFixture.DirectoryName);

    /// <summary>The real file system, save that <paramref name="path"/> is there and every read of it raises <paramref name="raised"/>.</summary>
    private sealed class Unreadable(IFileSystem inner, string path, Exception raised) : PassThroughFileSystem(inner)
    {
        public override string ReadAllText(string read) => read == path ? throw raised : base.ReadAllText(read);

        public override byte[] ReadAllBytes(string read) => read == path ? throw raised : base.ReadAllBytes(read);

        public override Task<byte[]> ReadAllBytesAsync(string read, CancellationToken cancellationToken = default)
            => read == path ? Task.FromException<byte[]>(raised) : base.ReadAllBytesAsync(read, cancellationToken);

        public override IEnumerable<string> EnumerateFiles(string listed, bool recursive)
            => listed == path ? throw raised : base.EnumerateFiles(listed, recursive);
    }
}
