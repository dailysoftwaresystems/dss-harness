using System.Collections.Concurrent;
using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// One leg's sweep, driven through doubles of what builds and runs: every worker synced from the one reading of the tree,
/// each test binary controlled once before its arms, each arm judged from its own build and run with its sites put back
/// and checked against that reading, a worker that cannot go on retired, and the leg's line the worst of its own and its
/// arms' - each arm beneath it, with its records.
/// </summary>
public sealed class MutationLegRunnerTests
{
    private const string LegName = "native";
    private const string SiteObject = "CMakeFiles/fixture.dir/src/fixture.cpp.o";
    private const string Program = "bin/fixture_tests";

    /// <summary>What an arm stopped with its sweep says, once its sites are back.</summary>
    private const string StoppedWhileDriven = "the sweep was stopped while it was driven, and each site was put back as it was";

    /// <summary>What an arm no worker drove before its sweep was stopped says.</summary>
    private const string StoppedUndriven = "the sweep was stopped before a worker drove it";

    /// <summary>How long a test waits for a sweep that should have ended, before it fails rather than hangs.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static readonly VariantKey Variant = new("x86_64", "gcc", "debug", null);

    /// <summary>The tree every sweep here reads: a source, a header, and the texts its arms cite.</summary>
    private static readonly Dictionary<string, string> TreeFiles = new(StringComparer.Ordinal)
    {
        ["src/fixture.cpp"] = "bool within(int c, int b) { return c <= b; }\nbool positive(int c) { return c > 0; }\n",
        ["src/budget.hpp"] = "constexpr int depth = 3;\n",
        ["texts/charge.before"] = "c <= b",
        ["texts/charge.after"] = "c < b",
        ["texts/charge.diag"] = "charge exceeded",
        ["texts/floor.before"] = "c > 0",
        ["texts/floor.after"] = "c >= 0",
        ["texts/depth.before"] = "int depth = 3",
        ["texts/depth.after"] = "int depth = three",
        ["texts/depth.control-before"] = "int depth = 3",
        ["texts/depth.control-after"] = "int depth = 4",
        ["texts/empty.before"] = string.Empty,
        ["texts/charge.same"] = "c <= b",
        ["texts/depth.control-same"] = "int depth = 3",
        ["texts/twice.before"] = "int c",
    };

    /// <summary>A TEST-RED arm whose mutation reddens one case of three, and says its diagnostic.</summary>
    private static readonly MutationArm ChargeBound = new()
    {
        Id = "charge-bound",
        Line = 1,
        Own = new MutationSite("src/fixture.cpp", "texts/charge.before", "texts/charge.after", 1),
        Kind = RedKind.TestRed,
        Target = "fixture",
        Runner = "fixture_tests",
        Cases = 3,
        Diagnostic = "texts/charge.diag",
        Why = "the charge is pinned",
        Reds = ["Fixture.Charge"],
        Greens = ["Fixture.Depth"],
    };

    /// <summary>A BUILD-RED arm, with its paired positive control.</summary>
    private static readonly MutationArm DepthType = new()
    {
        Id = "depth-type",
        Line = 4,
        Own = new MutationSite("src/budget.hpp", "texts/depth.before", "texts/depth.after", 4),
        Kind = RedKind.BuildRed,
        Target = "fixture",
        Runner = MutationRegistryParser.NoRunner,
        Cases = 0,
        Diagnostic = MutationRegistryParser.PairedControlToken,
        Why = "a depth that is no integer does not compile",
        Control = new PairedControl("texts/depth.control-before", "texts/depth.control-after", 5),
    };

    /// <summary>Another TEST-RED arm of the same binary, reddening another case.</summary>
    private static readonly MutationArm ChargeFloor = ChargeBound with
    {
        Id = "charge-floor",
        Line = 7,
        Own = new MutationSite("src/fixture.cpp", "texts/floor.before", "texts/floor.after", 7),
        Reds = ["Fixture.Floor"],
    };

    /// <summary>
    /// Each arm is driven in a worker synced from the one reading of the tree: built as the arm builds - its target, and a
    /// TEST-RED arm's runner - witnessed rebuilt, and run whole or paired with its control, each judged as declared; and
    /// every site is put back as the tree held it. Each arm's line names its worker and its records, which hold its
    /// record; each worker was made under a unit of its own, built whole as the leg builds, and given up.
    /// </summary>
    [Fact]
    public async Task EachArm_IsDrivenInAWorkerSyncedFromOneReading_AndEverySiteIsPutBack()
    {
        using var sweep = new Sweep();

        var entry = await sweep.RunAsync([ChargeBound, DepthType]);

        Assert.Equal((LegVerdict.Passed, "2 arm(s): 2 passed"), (entry.Verdict, entry.Detail));
        Assert.Equal(
            [
                ("charge-bound", LegVerdict.Passed, "ran 3 case(s), 1 red as declared, and said its diagnostic"),
                ("depth-type", LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
            ],
            entry.Arms.Select(arm => (arm.Arm, arm.Verdict, arm.Detail)));

        var charge = entry.Arms[0];
        var depth = entry.Arms[1];

        Assert.Equal(3, charge.Cases);
        Assert.Equal(3, charge.DeclaredCases);
        Assert.Equal(["Fixture.Charge"], charge.Reds);
        Assert.Equal(["Fixture.Charge"], charge.DeclaredReds);
        Assert.Null(depth.Cases);
        Assert.Equal(0, depth.DeclaredCases);
        Assert.Null(depth.Reds);

        foreach (var arm in entry.Arms)
        {
            Assert.Contains(arm.Worker, new int?[] { 1, 2 });
            Assert.Equal(sweep.Records(arm.Arm), arm.Records);

            using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(arm.Records!, MutationRecords.ArmRecordFileName)));

            Assert.Equal(arm.Arm, record.RootElement.GetProperty("arm").GetString());
            Assert.Equal("passed", record.RootElement.GetProperty("verdict").GetString());
            Assert.Equal(arm.Worker, record.RootElement.GetProperty("worker").GetInt32());
        }

        // One reading, every worker synced from it, and every site as the tree held it once the sweep is done.
        Assert.Equal(1, sweep.Reader!.Reads);
        Assert.Equal([sweep.Worker(1), sweep.Worker(2)], sweep.Copies.Synced.Select(sync => sync.Worker).Order(StringComparer.Ordinal));
        Assert.All(sweep.Copies.Synced, sync => Assert.Same(sweep.Reading, sync.Reading));
        Assert.Equal([sweep.Worker(1), sweep.Worker(2)], sweep.Copies.Released.Order(StringComparer.Ordinal));

        foreach (var worker in new[] { sweep.Worker(1), sweep.Worker(2) })
        {
            foreach (var site in new[] { "src/fixture.cpp", "src/budget.hpp" })
            {
                Assert.Equal(TreeFiles[site], File.ReadAllText(Path.Combine(worker, site)));
            }
        }

        // Each build where the sweep's rules put it, in a worker and never the tree, building what it was meant to.
        var builds = sweep.Builder.Builds.ToDictionary(build => build.Leg, StringComparer.Ordinal);

        Assert.Equal(
            [
                "native/arms/charge-bound",
                "native/arms/depth-type",
                "native/arms/depth-type/control",
                "native/controls/fixture_tests",
                "native/workers/1",
                "native/workers/2",
            ],
            builds.Keys.Order(StringComparer.Ordinal));
        Assert.All(sweep.Builder.Builds, build => Assert.Contains(build.TreeRoot, new[] { sweep.Worker(1), sweep.Worker(2) }));
        Assert.Null(builds["native/workers/1"].Project.Targets);
        Assert.Equal(["fixture", "fixture_tests"], builds["native/arms/charge-bound"].Project.Targets);
        Assert.Equal(["bin/fixture", Program], builds["native/arms/charge-bound"].Project.BuildOutputs.Select(output => output.For("linux")));
        Assert.Equal(["fixture"], builds["native/arms/depth-type"].Project.Targets);
        Assert.Equal(["fixture"], builds["native/arms/depth-type/control"].Project.Targets);
        Assert.Equal(["fixture_tests"], builds["native/controls/fixture_tests"].Project.Targets);
        Assert.Equal([Program], builds["native/controls/fixture_tests"].Project.BuildOutputs.Select(output => output.For("linux")));

        // The control unbounded, run once; the arm bounded by it, told its diagnostic, its records its own.
        var runs = sweep.Tests.Runs.ToDictionary(run => run.Leg, StringComparer.Ordinal);
        var control = runs["native/controls/fixture_tests"];
        var mutated = runs["native/arms/charge-bound"];

        Assert.Equal(2, sweep.Tests.Runs.Count);
        Assert.Equal((null, null), (control.Bound, control.Diagnostic));
        Assert.Equal(Path.Combine(sweep.RunDirectory, LegName, MutationRecords.ControlsDirectory, "fixture_tests"), control.RecordDirectory);
        Assert.Equal(PristineJudge.Bound(Tests.Took, 10), mutated.Bound);
        Assert.Equal(10.0, mutated.Factor);
        Assert.Equal("charge exceeded", mutated.Diagnostic);
        Assert.Equal(sweep.Records("charge-bound"), mutated.RecordDirectory);
        Assert.Equal(Path.Combine(Variant.DirectoryUnder(sweep.Worker(charge.Worker!.Value)), Program), mutated.Program);
        Assert.Equal(["--gtest_output=xml:{report}"], mutated.ReportArgs);
        Assert.Equal(sweep.Worker(charge.Worker!.Value), mutated.WorkingDirectory);

        // A unit for each worker and each arm, the room a worker's copy needs claimed by it alone, and only the first settles.
        var units = sweep.Admissions.ToList();

        Assert.Equal(["charge-bound", "depth-type", "worker-1", "worker-2"], units.Select(unit => unit.Unit).Order(StringComparer.Ordinal));
        Assert.Equal(TreeFiles.Values.Sum(text => (long)text.Length), units.Single(unit => unit.Unit == "worker-1").Room?.Bytes);
        Assert.All(units.Where(unit => !unit.Unit.StartsWith("worker-", StringComparison.Ordinal)), unit => Assert.Null(unit.Room));
        Assert.StartsWith("worker-", Assert.Single(units, unit => unit.Settle).Unit, StringComparison.Ordinal);

        // Each worker's abandoned claim released before it was claimed; what depends on an arm's sites asked of what its
        // build builds; and no build started while a site it builds was dated ahead of the clock.
        Assert.Equal([sweep.Worker(1), sweep.Worker(2)], sweep.Copies.Abandoned.Order(StringComparer.Ordinal));
        Assert.Contains(sweep.Builder.Graph.AskedOfTargets, targets => targets.SequenceEqual(["fixture", "fixture_tests"]));
        Assert.Contains(sweep.Builder.Graph.AskedOfTargets, targets => targets.SequenceEqual(["fixture"]));
        Assert.Empty(sweep.Builder.DatedAhead);
    }

    /// <summary>
    /// A worker an earlier sweep left holds its copy already, so making it again claims no room for one; a worker not yet
    /// made claims the room its copy takes.
    /// </summary>
    [Fact]
    public async Task AWorkerAlreadyThere_ClaimsNoRoomForItsCopy()
    {
        using var sweep = new Sweep();
        Directory.CreateDirectory(sweep.Worker(1));

        await sweep.RunAsync([ChargeBound, DepthType]);

        var units = sweep.Admissions.ToList();

        Assert.Null(units.Single(unit => unit.Unit == "worker-1").Room);
        Assert.Equal(TreeFiles.Values.Sum(text => (long)text.Length), units.Single(unit => unit.Unit == "worker-2").Room?.Bytes);
    }

    /// <summary>
    /// Where the room for its workers cannot be measured, the sweep runs every worker it wants, warning of it at once - a
    /// sweep can take hours - and saying so on the leg's line.
    /// </summary>
    [Fact]
    public async Task RoomThatCannotBeMeasured_IsWarnedOfAtOnce_AndSaidOnTheLegsLine()
    {
        using var sweep = new Sweep();
        sweep.Files = new ScriptedRoom(sweep.Harness.FileSystem, 0) { Unreadable = "the disk would not say" };

        var entry = await sweep.RunAsync([ChargeBound, DepthType]);

        const string Why = "the room for its workers could not be measured: the disk would not say";

        Assert.Equal((LegVerdict.Passed, $"2 arm(s): 2 passed; {Why}"), (entry.Verdict, entry.Detail));
        Assert.Contains($"native: {Why}", sweep.Harness.StandardError.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, sweep.Copies.Synced.Count);
    }

    /// <summary>
    /// An arm whose target the leg's build does not build, or whose runner builds no program there, is violated before
    /// anything of it is built or run: its declaration names what this leg cannot give it.
    /// </summary>
    [Fact]
    public async Task AnArmWhoseTargetOrRunnerTheBuildDoesNotMake_IsViolated_BeforeAnythingIsBuilt()
    {
        using var sweep = new Sweep { Workers = 1 };

        var entry = await sweep.RunAsync(
        [
            ChargeBound with { Id = "ghost-target", Target = "ghost" },
            ChargeBound with { Id = "ghost-runner", Line = 9, Runner = "ghost_tests" },
        ]);

        Assert.Equal(
            [
                (LegVerdict.Violated, "target 'ghost' is built by no line of the leg's build"),
                (LegVerdict.Violated, "runner 'ghost_tests' is built by no line of the leg's build"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.Contains("/arms/", StringComparison.Ordinal));
    }

    /// <summary>
    /// An unmutated binary that does not build decides the leg's own verdict, as its build's, and stops each of its arms
    /// unrun; an arm of no binary is still driven.
    /// </summary>
    [Fact]
    public async Task AControlThatDoesNotBuild_DecidesTheLeg_AndStopsItsBinarysArms()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Builder.Answer = request => request.Leg.EndsWith("/controls/fixture_tests", StringComparison.Ordinal)
            ? ReachedVerdict.Of(LegVerdict.Failed, "build exited 1")
            : null;

        var entry = await sweep.RunAsync([ChargeBound, DepthType]);

        Assert.Equal(
            (LegVerdict.Failed, "the unmutated build of fixture_tests: build exited 1; 2 arm(s): 1 stopped, 1 passed"),
            (entry.Verdict, entry.Detail));
        Assert.Equal(
            [
                (LegVerdict.Stopped, "the unmutated build of fixture_tests did not pass"),
                (LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.Empty(sweep.Tests.Runs);
    }

    /// <summary>
    /// The unmutated run of a binary is controlled once, whatever number of its arms there are; one that does not pass
    /// decides the leg's own verdict and stops each of its arms, saying why, with nothing of them built or run.
    /// </summary>
    [Fact]
    public async Task AControlThatDoesNotPass_DecidesTheLeg_AndStopsEachArmOfItsBinary()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Tests.Answer = request => request.Bound is null ? Tests.Ran(["Fixture.Depth"]) : Tests.Judged(request);

        var entry = await sweep.RunAsync([ChargeBound, ChargeFloor]);

        Assert.Equal(LegVerdict.Failed, entry.Verdict);
        Assert.Equal("the unmutated fixture_tests has 1 red: Fixture.Depth; 2 arm(s): 2 stopped", entry.Detail);
        Assert.All(entry.Arms, arm => Assert.Equal(
            (LegVerdict.Stopped, "the unmutated fixture_tests has 1 red, so no mutation of it proves anything", null, null),
            (arm.Verdict, arm.Detail, arm.Worker, arm.Records)));
        Assert.Single(sweep.Tests.Runs);
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.Contains("/arms/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A control that cannot be built and run at all is the leg's own verdict, as it would be for a build of the leg, and
    /// stops only its binary's arms: every other arm is still driven.
    /// </summary>
    [Fact]
    public async Task AControlThatCannotRun_IsTheLegsOwn_AndStopsOnlyItsBinarysArms()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Builder.Throws = request => request.Leg.EndsWith("controls/fixture_tests", StringComparison.Ordinal)
            ? new HarnessException(HarnessExit.ToolMissing, "cmake is missing")
            : null;

        var entry = await sweep.RunAsync([ChargeBound, DepthType]);

        Assert.Equal("the unmutated fixture_tests could not be built and run: cmake is missing; 2 arm(s): 1 stopped, 1 passed", entry.Detail);
        Assert.Equal(LegVerdict.Stopped, entry.Verdict);
        Assert.Equal(
            [
                (LegVerdict.Stopped, "the unmutated fixture_tests could not be built and run, so nothing could tell what a mutation of it changed"),
                (LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
    }

    /// <summary>
    /// A site its worker's copy does not hold as the sweep read it cannot be put back as the tree held it: the arm is
    /// poisoned, whatever its run said, and its worker is retired, so with no other worker every arm after it is stopped,
    /// saying why.
    /// </summary>
    [Fact]
    public async Task ASiteThatCannotBePutBack_PoisonsItsArm_AndRetiresItsWorker()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Copies.AfterSync = worker => File.AppendAllText(Path.Combine(worker, "src", "fixture.cpp"), "// stray\n");

        var entry = await sweep.RunAsync([ChargeBound, ChargeFloor]);

        const string Why = "'src/fixture.cpp' does not hold what the tree held when the sweep read it";

        Assert.Equal((LegVerdict.Poisoned, "2 arm(s): 1 poisoned, 1 stopped"), (entry.Verdict, entry.Detail));
        Assert.Equal(
            [
                (LegVerdict.Poisoned, $"a site could not be put back as it was, so its worker drives no other arm: {Why}"),
                (LegVerdict.Stopped, $"no worker was left to drive it: worker 1 was retired, a site of arm 'charge-bound' could not be put back as it was: {Why}"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.Equal(sweep.Records("charge-bound"), entry.Arms[0].Records);
    }

    /// <summary>
    /// A worker retired for a site it could not put back drives no other arm, and the other workers drive the rest: the
    /// write that failed is named beside what the site held after it.
    /// </summary>
    [Fact]
    public async Task AWorkerRetiredForASite_LeavesTheOtherWorkersToDriveTheRest()
    {
        using var sweep = new Sweep();
        sweep.SiteFiles = new FailingRestore(sweep.Harness.FileSystem, "budget.hpp", TreeFiles["src/budget.hpp"]);

        var entry = await sweep.RunAsync([ChargeBound, DepthType, ChargeFloor]);

        Assert.Equal((LegVerdict.Poisoned, "3 arm(s): 1 poisoned, 2 passed"), (entry.Verdict, entry.Detail));
        Assert.Equal(
            "a site could not be put back as it was, so its worker drives no other arm: writing 'src/budget.hpp' back failed: the disk went away; "
            + "'src/budget.hpp' does not hold what the tree held when the sweep read it",
            entry.Arms.Single(arm => arm.Arm == "depth-type").Detail);
        Assert.All(entry.Arms.Where(arm => arm.Arm != "depth-type"), arm => Assert.Equal(LegVerdict.Passed, arm.Verdict));
    }

    /// <summary>
    /// Each site of an arm is put back on its own: one whose writing back fails keeps no other from being put back, and
    /// only it is named as not holding what the tree held.
    /// </summary>
    [Fact]
    public async Task ASiteThatCannotBePutBack_KeepsNoOtherSiteOfItsArmFromBeingPutBack()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.SiteFiles = new FailingRestore(sweep.Harness.FileSystem, "fixture.cpp", TreeFiles["src/fixture.cpp"]);

        var coupled = ChargeBound with { Coupled = [new MutationSite("src/budget.hpp", "texts/depth.control-before", "texts/depth.control-after", 2)] };

        var entry = await sweep.RunAsync([coupled]);

        Assert.Equal(
            (LegVerdict.Poisoned, "a site could not be put back as it was, so its worker drives no other arm: writing 'src/fixture.cpp' back failed: the disk went away; 'src/fixture.cpp' does not hold what the tree held when the sweep read it"),
            (entry.Arms[0].Verdict, entry.Arms[0].Detail));
        Assert.Equal(TreeFiles["src/budget.hpp"], File.ReadAllText(Path.Combine(sweep.Worker(1), "src", "budget.hpp")));
        Assert.Contains("c < b", File.ReadAllText(Path.Combine(sweep.Worker(1), "src", "fixture.cpp")), StringComparison.Ordinal);
    }

    /// <summary>
    /// A site gone once its writing back failed cannot be put back as the tree held it: said gone, it poisons its arm.
    /// </summary>
    [Fact]
    public async Task ASiteGone_PoisonsItsArm()
    {
        using var gone = new Sweep { Workers = 1 };
        gone.SiteFiles = new FailingRestore(gone.Harness.FileSystem, "fixture.cpp", TreeFiles["src/fixture.cpp"]);
        gone.Tests.Answer = request => Tests.Ran(request.Bound is null ? [] : ["Fixture.Charge"]);
        gone.Tests.Before = (request, _) =>
        {
            if (request.Bound is not null)
            {
                File.Delete(Path.Combine(request.WorkingDirectory, "src", "fixture.cpp"));
            }

            return Task.CompletedTask;
        };

        var lost = await gone.RunAsync([ChargeBound]);

        Assert.Equal(
            (LegVerdict.Poisoned, "a site could not be put back as it was, so its worker drives no other arm: writing 'src/fixture.cpp' back failed: the disk went away; 'src/fixture.cpp' is gone"),
            (lost.Arms[0].Verdict, lost.Arms[0].Detail));
    }

    /// <summary>
    /// A site a row spells otherwise than the tree spells it - found all the same where the file system folds case, and
    /// then nothing its putting back could be checked against - is violated before it is written, naming the tree's
    /// spelling, on every machine alike: its own site, or a coupled one. Its worker is not retired, and drives the next arm.
    /// </summary>
    [Fact]
    public async Task ASiteSpeltInAnotherCase_IsViolatedBeforeItIsWritten()
    {
        using var sweep = new Sweep { Workers = 1 };
        var writes = new RecordingWrites(sweep.Harness.FileSystem);
        sweep.SiteFiles = writes;

        var shouted = ChargeBound with { Id = "charge-shouted", Own = ChargeBound.Own with { Site = "src/Fixture.cpp" } };
        var coupled = ChargeBound with { Id = "charge-coupled", Line = 2, Coupled = [new MutationSite("SRC/budget.hpp", "texts/depth.control-before", "texts/depth.control-after", 3)] };

        var entry = await sweep.RunAsync([shouted, coupled, ChargeFloor]);

        Assert.Equal(
            [
                (LegVerdict.Violated, "site 'src/Fixture.cpp' is spelt 'src/fixture.cpp' in the tree, and a row names a file as the tree spells it"),
                (LegVerdict.Violated, "site 'SRC/budget.hpp' is spelt 'src/budget.hpp' in the tree, and a row names a file as the tree spells it"),
                (LegVerdict.Passed, "ran 3 case(s), 1 red as declared, and said its diagnostic"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.Equal(["native/arms/charge-floor"], sweep.Builder.Builds.Select(build => build.Leg).Where(leg => leg.Contains("/arms/", StringComparison.Ordinal)));

        // Written twice, both for the arm that was driven: mutated, and put back.
        Assert.Equal(2, writes.Written.Count);
    }

    /// <summary>
    /// A site its worker holds that the sweep's reading of the tree does not - a file a build made there after the tree
    /// was read - is violated before it is written: nothing could vouch for what it held once it was put back. Its worker
    /// is not retired.
    /// </summary>
    [Fact]
    public async Task ASiteTheReadingNeverHeld_IsViolatedBeforeItIsWritten()
    {
        using var sweep = new Sweep { Workers = 1 };
        var writes = new RecordingWrites(sweep.Harness.FileSystem);
        sweep.SiteFiles = writes;
        sweep.Copies.AfterSync = worker =>
        {
            Directory.CreateDirectory(Path.Combine(worker, "src", "gen"));
            File.WriteAllText(Path.Combine(worker, "src", "gen", "fixture.cpp"), TreeFiles["src/fixture.cpp"]);
        };

        var entry = await sweep.RunAsync([ChargeBound with { Id = "charge-made", Own = ChargeBound.Own with { Site = "src/gen/fixture.cpp" } }, ChargeFloor]);

        Assert.Equal(
            [
                (LegVerdict.Violated, "site 'src/gen/fixture.cpp' is no file the sweep's reading of the tree holds - one a build makes there, or one sync leaves out - so nothing vouches for what it holds"),
                (LegVerdict.Passed, "ran 3 case(s), 1 red as declared, and said its diagnostic"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.Equal(2, writes.Written.Count);
        Assert.All(writes.Written, path => Assert.EndsWith(Path.Combine("src", "fixture.cpp"), path, StringComparison.Ordinal));
    }

    /// <summary>
    /// A mutation that changes nothing - its after-text its before-text again - is violated before anything of the arm is
    /// built or written: built and run it would redden nothing, and read as a mutation no test caught. So is a paired
    /// control that changes nothing, which would build as the unmutated tree builds and prove nothing of the site.
    /// </summary>
    [Fact]
    public async Task AMutationThatChangesNothing_IsViolated_BeforeAnythingIsBuilt()
    {
        using var sweep = new Sweep { Workers = 1 };
        var writes = new RecordingWrites(sweep.Harness.FileSystem);
        sweep.SiteFiles = writes;

        var same = ChargeBound with { Id = "charge-same", Own = ChargeBound.Own with { After = "texts/charge.same" } };
        var idle = DepthType with { Id = "depth-idle", Line = 9, Control = new PairedControl("texts/depth.control-before", "texts/depth.control-same", 10) };

        var entry = await sweep.RunAsync([same, idle]);

        Assert.Equal(
            [
                (LegVerdict.Violated, "the text in 'texts/charge.same' is the text in 'texts/charge.before', so replacing one with the other changes nothing in 'src/fixture.cpp'"),
                (LegVerdict.Violated, "the text in 'texts/depth.control-same' is the text in 'texts/depth.control-before', so replacing one with the other changes nothing in 'src/budget.hpp'"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.Contains("/arms/", StringComparison.Ordinal));
        Assert.Empty(writes.Written);
    }

    /// <summary>
    /// A before-text its site does not hold exactly once is violated before anything of the arm is built or written,
    /// saying how often it occurs and nothing else: no times, where the site moved under the arm; twice, where the arm
    /// does not say which it hits; and a paired control's, counted in the site as the tree holds it.
    /// </summary>
    [Fact]
    public async Task ABeforeTextNotThereExactlyOnce_IsViolated_SayingHowOften()
    {
        using var sweep = new Sweep { Workers = 1 };
        var writes = new RecordingWrites(sweep.Harness.FileSystem);
        sweep.SiteFiles = writes;

        var moved = DepthType with { Id = "depth-moved", Own = DepthType.Own with { Before = "texts/depth.after", After = "texts/depth.before" } };
        var twice = ChargeBound with { Id = "charge-twice", Line = 6, Own = ChargeBound.Own with { Before = "texts/twice.before" } };
        var control = DepthType with { Id = "depth-control", Line = 9, Control = new PairedControl("texts/depth.after", "texts/depth.control-after", 10) };

        var entry = await sweep.RunAsync([moved, twice, control]);

        Assert.Equal(
            [
                (LegVerdict.Violated, "the text in 'texts/depth.after' occurs 0 time(s) in 'src/budget.hpp', where it must occur exactly once"),
                (LegVerdict.Violated, "the text in 'texts/twice.before' occurs 2 time(s) in 'src/fixture.cpp', where it must occur exactly once"),
                (LegVerdict.Violated, "the text in 'texts/depth.after' occurs 0 time(s) in 'src/budget.hpp', where it must occur exactly once"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.Contains("/arms/", StringComparison.Ordinal));
        Assert.Empty(writes.Written);
    }

    /// <summary>
    /// A site that is no file at all - the worker's copy does not hold it, and the sweep's reading of the tree holds it
    /// under no spelling - is violated before anything of the arm is built, said missing and nothing else.
    /// </summary>
    [Fact]
    public async Task ASiteThatIsNoFile_IsViolated_SaidMissing()
    {
        using var sweep = new Sweep { Workers = 1 };

        var entry = await sweep.RunAsync([ChargeBound with { Id = "charge-nowhere", Own = ChargeBound.Own with { Site = "src/nowhere.cpp" } }]);

        Assert.Equal(
            (LegVerdict.Violated, "site 'src/nowhere.cpp' is not a file in the worker's copy of the tree"),
            (entry.Arms[0].Verdict, entry.Arms[0].Detail));
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.Contains("/arms/", StringComparison.Ordinal));
    }

    /// <summary>
    /// A defect of the harness's in one arm poisons that arm, naming it, and retires its worker, whose copy nothing then
    /// vouches for; the site is still put back.
    /// </summary>
    [Fact]
    public async Task ADefectInOneArm_PoisonsIt_AndRetiresItsWorker_AndTheSiteIsPutBack()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Tests.Before = (request, _) => request.Bound is null ? Task.CompletedTask : throw new InvalidOperationException("the report vanished");

        var entry = await sweep.RunAsync([ChargeBound, ChargeFloor]);

        const string Defect = "the sweep could not judge this arm, InvalidOperationException: the report vanished";

        Assert.Equal(
            [
                (LegVerdict.Poisoned, Defect),
                (LegVerdict.Stopped, $"no worker was left to drive it: worker 1 was retired, arm 'charge-bound' ended in a defect: {Defect}"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.Equal(TreeFiles["src/fixture.cpp"], File.ReadAllText(Path.Combine(sweep.Worker(1), "src", "fixture.cpp")));
    }

    /// <summary>
    /// A refusal of the run from inside an arm ends the sweep, as it ends any leg's work, once every site is put back -
    /// and the leg's line is still its answer, naming the refusal as what ends the run: each arm judged before it keeps
    /// its verdict, the arm it was raised in is stopped saying so, and each arm no worker reached is stopped too.
    /// </summary>
    [Fact]
    public async Task ARefusalFromInsideAnArm_EndsTheSweep_OnceEverySiteIsPutBack_AndTheLegsLineIsKept()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Builder.Throws = request => request.Leg == "native/arms/charge-bound"
            ? new HarnessException(HarnessExit.ConfigInvalid, "a setting is wrong.")
            : null;

        var entry = await sweep.RunAsync([DepthType, ChargeBound, ChargeFloor]);

        Assert.Equal((HarnessExit.ConfigInvalid, "a setting is wrong."), (entry.EndsTheRun?.ExitCode, entry.EndsTheRun?.Message));
        Assert.Equal((LegVerdict.Stopped, "3 arm(s): 2 stopped, 1 passed"), (entry.Verdict, entry.Detail));
        Assert.Equal(
            [
                ("charge-bound", LegVerdict.Stopped, "a refusal of the run ended the sweep while it was driven, and each site was put back as it was: a setting is wrong"),
                ("depth-type", LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
                ("charge-floor", LegVerdict.Stopped, StoppedUndriven),
            ],
            entry.Arms.Select(arm => (arm.Arm, arm.Verdict, arm.Detail)));
        sweep.AssertEverySiteAsTheTreeHoldsIt();
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Released);
    }

    /// <summary>
    /// A refusal of the run raised while an arm still waits for its machine ends the sweep as one from inside an arm
    /// does: the arm, of which nothing was written, is stopped with those no worker drove, and the arms judged before it
    /// are kept on the leg's line.
    /// </summary>
    [Fact]
    public async Task ARefusalAsAnArmWaitsForItsMachine_EndsTheSweep_AndTheLegsLineIsKept()
    {
        using var sweep = new Sweep
        {
            Workers = 1,
            Admit = unit => unit.Unit == "charge-bound"
                ? throw new HarnessException(HarnessExit.Refused, "the record of this machine's heavy legs could not be read")
                : (Admission?)null,
        };

        var entry = await sweep.RunAsync([DepthType, ChargeBound, ChargeFloor]);

        Assert.Equal(HarnessExit.Refused, entry.EndsTheRun?.ExitCode);
        Assert.Equal(
            [
                ("charge-bound", LegVerdict.Stopped, StoppedUndriven),
                ("depth-type", LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
                ("charge-floor", LegVerdict.Stopped, StoppedUndriven),
            ],
            entry.Arms.Select(arm => (arm.Arm, arm.Verdict, arm.Detail)));
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Released);
    }

    /// <summary>
    /// A refusal from inside one worker's arm ends a sweep of two workers once both have put their sites back and been
    /// given up: the other worker's arm, in the middle of its run, is stopped and its site put back before the leg's
    /// line, naming the refusal, is the sweep's answer.
    /// </summary>
    [Fact]
    public async Task ARefusalFromInsideAnArm_EndsASweepOfTwoWorkers_OnceBothSitesArePutBack()
    {
        using var sweep = new Sweep();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sweep.Tests.Before = async (request, token) =>
        {
            if (request.Bound is not null)
            {
                running.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        sweep.Builder.Before = (request, token) => request.Leg == "native/arms/depth-type" ? running.Task.WaitAsync(token) : Task.CompletedTask;
        sweep.Builder.Throws = request => request.Leg == "native/arms/depth-type" ? new HarnessException(HarnessExit.ConfigInvalid, "a setting is wrong") : null;

        var entry = await sweep.RunAsync([ChargeBound, DepthType]).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((HarnessExit.ConfigInvalid, "a setting is wrong"), (entry.EndsTheRun?.ExitCode, entry.EndsTheRun?.Message));
        Assert.Equal(
            [
                (LegVerdict.Stopped, StoppedWhileDriven),
                (LegVerdict.Stopped, "a refusal of the run ended the sweep while it was driven, and each site was put back as it was: a setting is wrong"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        sweep.AssertEverySiteAsTheTreeHoldsIt();
        Assert.Equal([sweep.Worker(1), sweep.Worker(2)], sweep.Copies.Released.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A sweep stopped part way puts back each site it had mutated, gives its workers up, and still says the leg: each arm
    /// judged by then with its verdict, the arm it was driving stopped with its record, and each arm no worker reached
    /// stopped too.
    /// </summary>
    [Fact]
    public async Task ASweepStoppedPartWay_PutsEverySiteBack_AndSaysWhatItJudgedAndWhatItStopped()
    {
        using var sweep = new Sweep { Workers = 1 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        sweep.Tests.Before = async (request, token) =>
        {
            if (request.Bound is not null)
            {
                await stop.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
        };

        var entry = await sweep.RunAsync([DepthType, ChargeBound, ChargeFloor], cancellationToken: stop.Token);

        Assert.Equal((LegVerdict.Stopped, "3 arm(s): 2 stopped, 1 passed"), (entry.Verdict, entry.Detail));
        Assert.Equal(
            [
                ("charge-bound", LegVerdict.Stopped, StoppedWhileDriven),
                ("depth-type", LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
                ("charge-floor", LegVerdict.Stopped, StoppedUndriven),
            ],
            entry.Arms.Select(arm => (arm.Arm, arm.Verdict, arm.Detail)));
        Assert.Equal([1, 1, null], entry.Arms.Select(arm => arm.Worker));
        Assert.True(File.Exists(Path.Combine(sweep.Records("charge-bound"), MutationRecords.ArmRecordFileName)));
        sweep.AssertEverySiteAsTheTreeHoldsIt();
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Released);
    }

    /// <summary>
    /// A sweep of two workers stopped while each drives an arm puts both sites back and gives both workers up before it
    /// says the leg: each arm it was driving stopped, and so the arm neither reached.
    /// </summary>
    [Fact]
    public async Task ASweepOfTwoWorkersStoppedPartWay_PutsBothSitesBack_AndGivesBothWorkersUp()
    {
        using var sweep = new Sweep();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sweep.Tests.Before = async (request, token) =>
        {
            if (request.Bound is not null)
            {
                running.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        sweep.Builder.Before = async (request, token) =>
        {
            if (request.Leg == "native/arms/depth-type")
            {
                building.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        var swept = sweep.RunAsync([ChargeBound, DepthType, ChargeFloor], cancellationToken: stop.Token);

        await Task.WhenAll(running.Task, building.Task).WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Each worker holds a mutated site as the sweep is stopped.
        Assert.Single(new[] { 1, 2 }, number => File.ReadAllText(Path.Combine(sweep.Worker(number), "src", "fixture.cpp")).Contains("c < b", StringComparison.Ordinal));
        Assert.Single(new[] { 1, 2 }, number => File.ReadAllText(Path.Combine(sweep.Worker(number), "src", "budget.hpp")).Contains("three", StringComparison.Ordinal));

        await stop.CancelAsync();

        var entry = await swept.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((LegVerdict.Stopped, "3 arm(s): 3 stopped"), (entry.Verdict, entry.Detail));
        Assert.Equal([StoppedWhileDriven, StoppedWhileDriven, StoppedUndriven], entry.Arms.Select(arm => arm.Detail));
        sweep.AssertEverySiteAsTheTreeHoldsIt();
        Assert.Equal([sweep.Worker(1), sweep.Worker(2)], sweep.Copies.Released.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// An arm a worker had taken and not yet driven when the sweep was stopped - still waiting for its machine to take
    /// it - is stopped with the arms no worker reached: nothing of it was built or written.
    /// </summary>
    [Fact]
    public async Task AnArmStoppedWhileItWaitedForItsMachine_IsStoppedWithTheArmsNoWorkerDrove()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var sweep = new Sweep
        {
            Workers = 1,
            AdmitAsync = async (unit, token) =>
            {
                if (unit.Unit == "charge-bound")
                {
                    await stop.CancelAsync();
                    token.ThrowIfCancellationRequested();
                }

                return null;
            },
        };

        var entry = await sweep.RunAsync([ChargeBound, ChargeFloor], cancellationToken: stop.Token);

        Assert.Equal((LegVerdict.Stopped, "2 arm(s): 2 stopped"), (entry.Verdict, entry.Detail));
        Assert.All(entry.Arms, arm => Assert.Equal((LegVerdict.Stopped, StoppedUndriven, null), (arm.Verdict, arm.Detail, arm.Worker)));
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.Contains("/arms/", StringComparison.Ordinal));
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Released);
    }

    /// <summary>
    /// A site that cannot be put back as a sweep is stopped still poisons its arm, naming it: stopping a sweep never
    /// hides a copy it left mutated.
    /// </summary>
    [Fact]
    public async Task ASiteThatCannotBePutBack_AsASweepIsStopped_StillPoisonsItsArm()
    {
        using var sweep = new Sweep { Workers = 1 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        sweep.SiteFiles = new FailingRestore(sweep.Harness.FileSystem, "fixture.cpp", TreeFiles["src/fixture.cpp"]);
        sweep.Tests.Before = async (request, token) =>
        {
            if (request.Bound is not null)
            {
                await stop.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
        };

        var entry = await sweep.RunAsync([ChargeBound], cancellationToken: stop.Token);

        Assert.Equal(
            (LegVerdict.Poisoned, "a site could not be put back as it was, so its worker drives no other arm: writing 'src/fixture.cpp' back failed: the disk went away; 'src/fixture.cpp' does not hold what the tree held when the sweep read it"),
            (entry.Arms[0].Verdict, entry.Arms[0].Detail));
        Assert.Equal(LegVerdict.Poisoned, entry.Verdict);
    }

    /// <summary>
    /// A failure inside an arm that refuses nothing of the run is that arm's verdict - a tool its build cannot find, a
    /// program that will not start - and its worker, the site put back, drives the next arm.
    /// </summary>
    [Fact]
    public async Task AFailureInsideAnArmThatRefusesNothing_IsItsVerdict_AndItsWorkerDrivesTheNext()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Builder.Throws = request => request.Leg == "native/arms/depth-type" ? new HarnessException(HarnessExit.ToolMissing, "ninja was not found") : null;
        sweep.Tests.Before = (request, _) => request.Leg == "native/arms/charge-bound"
            ? throw new ProgramStartException("fixture_tests", "'fixture_tests' could not be started: it is built for another processor")
            : Task.CompletedTask;

        var entry = await sweep.RunAsync([DepthType, ChargeBound, ChargeFloor]);

        Assert.Equal(
            [
                ("charge-bound", LegVerdict.Failed, "'fixture_tests' could not be started: it is built for another processor"),
                ("depth-type", LegVerdict.SkippedToolMissing, "ninja was not found"),
                ("charge-floor", LegVerdict.Passed, "ran 3 case(s), 1 red as declared, and said its diagnostic"),
            ],
            entry.Arms.Select(arm => (arm.Arm, arm.Verdict, arm.Detail)));
        Assert.All(entry.Arms, arm => Assert.Equal(1, arm.Worker));
        sweep.AssertEverySiteAsTheTreeHoldsIt();
    }

    /// <summary>
    /// A failure nobody named around an arm - in its machine's answer, outside its own build and run - poisons that arm
    /// alone and retires its worker, whose copy nothing then vouches for; the other worker drives the rest, and every
    /// arm judged keeps its verdict.
    /// </summary>
    [Fact]
    public async Task AFailureNobodyNamedAroundAnArm_PoisonsIt_AndTheOtherWorkerDrivesTheRest()
    {
        using var sweep = new Sweep
        {
            Admit = unit => unit.Unit == "depth-type" ? throw new InvalidOperationException("the record of the machine's slots vanished") : (Admission?)null,
        };

        var entry = await sweep.RunAsync([ChargeBound, DepthType, ChargeFloor]).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((LegVerdict.Poisoned, "3 arm(s): 1 poisoned, 2 passed"), (entry.Verdict, entry.Detail));
        Assert.Equal(
            "the sweep could not drive this arm, InvalidOperationException: the record of the machine's slots vanished",
            entry.Arms.Single(arm => arm.Arm == "depth-type").Detail);
        Assert.True(File.Exists(Path.Combine(sweep.Records("depth-type"), MutationRecords.ArmRecordFileName)));
        Assert.Equal([sweep.Worker(1), sweep.Worker(2)], sweep.Copies.Released.Order(StringComparer.Ordinal));

        using var alone = new Sweep
        {
            Workers = 1,
            Admit = unit => unit.Unit == "depth-type" ? throw new InvalidOperationException("the record of the machine's slots vanished") : (Admission?)null,
        };

        var retired = await alone.RunAsync([DepthType, ChargeFloor]);

        Assert.Equal(
            (LegVerdict.Stopped, "no worker was left to drive it: worker 1 was retired, arm 'depth-type' ended in a defect: the sweep could not drive this arm, InvalidOperationException: the record of the machine's slots vanished"),
            (retired.Arms[1].Verdict, retired.Arms[1].Detail));
    }

    /// <summary>
    /// A cancellation nobody asked of the sweep - a wait inside a build giving up - is a failure as any other nobody
    /// named, never a stop: the arm is poisoned, saying what was raised.
    /// </summary>
    [Fact]
    public async Task ACancellationNobodyAskedOfTheSweep_IsAFailure_NeverAStop()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Builder.Throws = request => request.Leg == "native/arms/depth-type" ? new OperationCanceledException("a wait inside the build gave up") : null;

        var entry = await sweep.RunAsync([DepthType]);

        Assert.Equal(
            (LegVerdict.Poisoned, "the sweep could not judge this arm, OperationCanceledException: a wait inside the build gave up"),
            (entry.Arms[0].Verdict, entry.Arms[0].Detail));
        sweep.AssertEverySiteAsTheTreeHoldsIt();
    }

    /// <summary>
    /// A failure raised as the sweep is stopped is still its arm's own: only a cancellation the stop caused is the arm
    /// being stopped.
    /// </summary>
    [Fact]
    public async Task AFailureAsTheSweepIsStopped_IsStillItsArmsOwn()
    {
        using var sweep = new Sweep { Workers = 1 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        sweep.Tests.Before = async (request, _) =>
        {
            if (request.Bound is not null)
            {
                await stop.CancelAsync();
                throw new IOException("The pipe is being closed.");
            }
        };

        var entry = await sweep.RunAsync([ChargeBound], cancellationToken: stop.Token);

        Assert.Equal(
            (LegVerdict.Poisoned, "the sweep could not judge this arm, IOException: The pipe is being closed"),
            (entry.Arms[0].Verdict, entry.Arms[0].Detail));
        sweep.AssertEverySiteAsTheTreeHoldsIt();
    }

    /// <summary>
    /// A failure outside every worker's own handling of its copy and its arms - in claiming a worker - ends the sweep
    /// and is the leg's own, as a leg whose work ends so is judged: poisoned where nobody named it, and the verdict a
    /// refusal names where one did. Every arm still has its line. A refusal of the run raised there ends the run.
    /// </summary>
    [Fact]
    public async Task AFailureOutsideEveryWorkersOwnHandling_IsTheLegsOwn_AndEveryArmHasItsLine()
    {
        using var defect = new Sweep { Workers = 1 };
        defect.Copies.ClaimThrows = _ => new InvalidOperationException("the claim's lock was abandoned.");

        var poisoned = await defect.RunAsync([ChargeBound, DepthType]);

        Assert.Equal(
            (LegVerdict.Poisoned, "the sweep ended in a failure nobody named, InvalidOperationException: the claim's lock was abandoned; 2 arm(s): 2 stopped", null),
            (poisoned.Verdict, poisoned.Detail, poisoned.EndsTheRun));
        Assert.All(poisoned.Arms, arm => Assert.Equal((LegVerdict.Stopped, StoppedUndriven), (arm.Verdict, arm.Detail)));

        using var named = new Sweep { Workers = 1 };
        named.Copies.ClaimThrows = _ => new HarnessException(HarnessExit.CommandFailed, "the claim could not be recorded");

        var failed = await named.RunAsync([ChargeBound, DepthType]);

        Assert.Equal(
            (LegVerdict.Failed, "the claim could not be recorded; 2 arm(s): 2 stopped", null),
            (failed.Verdict, failed.Detail, failed.EndsTheRun));

        using var refusing = new Sweep { Workers = 1 };
        refusing.Copies.ClaimThrows = _ => new HarnessException(HarnessExit.Refused, "the claim file cannot be written");

        var refused = await refusing.RunAsync([ChargeBound, DepthType]);

        Assert.Equal(
            (LegVerdict.Stopped, "2 arm(s): 2 stopped", HarnessExit.Refused),
            (refused.Verdict, refused.Detail, refused.EndsTheRun?.ExitCode));
    }

    /// <summary>
    /// A sweep stopped while a worker is being made stops its arms, and blames no worker: a worker stopped is not one
    /// that could not be made.
    /// </summary>
    [Fact]
    public async Task ASweepStoppedWhileAWorkerIsMade_StopsItsArms_AndBlamesNoWorker()
    {
        using var sweep = new Sweep { Workers = 1 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        sweep.Builder.Before = async (request, token) =>
        {
            if (request.Leg == "native/workers/1")
            {
                await stop.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
        };

        var entry = await sweep.RunAsync([ChargeBound, DepthType], cancellationToken: stop.Token);

        Assert.Equal((LegVerdict.Stopped, "2 arm(s): 2 stopped"), (entry.Verdict, entry.Detail));
        Assert.All(entry.Arms, arm => Assert.Equal((LegVerdict.Stopped, StoppedUndriven), (arm.Verdict, arm.Detail)));
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Released);
    }

    /// <summary>
    /// A sweep stopped while a binary is controlled stops that binary's arms as arms no worker drove, and blames no
    /// control: a control stopped is not one that did not pass.
    /// </summary>
    [Fact]
    public async Task ASweepStoppedWhileABinaryIsControlled_StopsItsArms_AndBlamesNoControl()
    {
        using var sweep = new Sweep { Workers = 1 };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        sweep.Tests.Before = async (request, token) =>
        {
            if (request.Bound is null)
            {
                await stop.CancelAsync();
                token.ThrowIfCancellationRequested();
            }
        };

        var entry = await sweep.RunAsync([ChargeBound, ChargeFloor], cancellationToken: stop.Token);

        Assert.Equal((LegVerdict.Stopped, "2 arm(s): 2 stopped"), (entry.Verdict, entry.Detail));
        Assert.All(entry.Arms, arm => Assert.Equal((LegVerdict.Stopped, StoppedUndriven), (arm.Verdict, arm.Detail)));
    }

    /// <summary>
    /// A refusal of the run raised while a binary is controlled ends the sweep, as one raised inside an arm does: the
    /// arms judged before it are kept, and the arm that needed the control is stopped undriven.
    /// </summary>
    [Fact]
    public async Task ARefusalAsABinaryIsControlled_EndsTheSweep_AndTheLegsLineIsKept()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Builder.Throws = request => request.Leg.EndsWith("/controls/fixture_tests", StringComparison.Ordinal)
            ? new HarnessException(HarnessExit.ConfigInvalid, "a setting is wrong")
            : null;

        var entry = await sweep.RunAsync([DepthType, ChargeBound]);

        Assert.Equal(HarnessExit.ConfigInvalid, entry.EndsTheRun?.ExitCode);
        Assert.Equal(
            [
                (LegVerdict.Stopped, StoppedUndriven),
                (LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
    }

    /// <summary>
    /// A site that cannot be put back as a refusal ends the sweep still poisons its arm, and the refusal still ends the
    /// run: neither hides the other.
    /// </summary>
    [Fact]
    public async Task ASiteThatCannotBePutBack_AsARefusalEndsTheSweep_StillPoisonsItsArm_AndTheRefusalStillEndsTheRun()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.SiteFiles = new FailingRestore(sweep.Harness.FileSystem, "budget.hpp", TreeFiles["src/budget.hpp"]);
        sweep.Builder.Throws = request => request.Leg == "native/arms/depth-type" ? new HarnessException(HarnessExit.ConfigInvalid, "a setting is wrong") : null;

        var entry = await sweep.RunAsync([DepthType]);

        Assert.Equal(HarnessExit.ConfigInvalid, entry.EndsTheRun?.ExitCode);
        Assert.Equal(
            (LegVerdict.Poisoned, "a site could not be put back as it was, so its worker drives no other arm: writing 'src/budget.hpp' back failed: the disk went away; 'src/budget.hpp' does not hold what the tree held when the sweep read it"),
            (entry.Arms[0].Verdict, entry.Arms[0].Detail));
    }

    /// <summary>
    /// A worker its machine does not admit ends that worker alone: its unit claims room, which no arm does, so the
    /// worker beside it still asks for each arm, and drives it.
    /// </summary>
    [Fact]
    public async Task AWorkerItsMachineDoesNotAdmit_EndsThatWorkerAlone_AndTheOtherDrivesEveryArm()
    {
        using var sweep = new Sweep
        {
            Admit = unit => unit.Unit == "worker-2" ? Admission.Refused(new AdmissionFact(false, 3600), "not admitted after 1h0m: its build would not fit") : null,
        };

        var entry = await sweep.RunAsync([ChargeBound, DepthType]).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(
            (LegVerdict.Passed, "2 arm(s): 2 passed; worker 2: not admitted after 1h0m: its build would not fit, so it drove no arm"),
            (entry.Verdict, entry.Detail));
        Assert.All(entry.Arms, arm => Assert.Equal((LegVerdict.Passed, 1), (arm.Verdict, arm.Worker)));
    }

    /// <summary>
    /// A worker whose making ends in a failure nobody named - the disk full as its copy is written, or as its build
    /// records what it built - is retired alone, saying what failed: the other worker drives every arm, and the leg says
    /// it ran with fewer. Where no worker could be made, their failures are the leg's own, and each arm is stopped.
    /// </summary>
    [Fact]
    public async Task AWorkerThatCannotBeMade_IsRetired_AndTheOthersSweep()
    {
        using var one = new Sweep();
        one.Builder.Throws = request => request.Leg == "native/workers/2" ? new IOException("There is not enough space on the disk.") : null;

        var fewer = await one.RunAsync([ChargeBound, DepthType]).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(
            (LegVerdict.Passed, "2 arm(s): 2 passed; worker 2: it could not be made, IOException: There is not enough space on the disk, so it drove no arm"),
            (fewer.Verdict, fewer.Detail));
        Assert.All(fewer.Arms, arm => Assert.Equal((LegVerdict.Passed, 1), (arm.Verdict, arm.Worker)));
        Assert.Equal([one.Worker(1), one.Worker(2)], one.Copies.Released.Order(StringComparer.Ordinal));

        using var none = new Sweep();
        none.Copies.SyncFails = worker => worker == none.Worker(1) ? new UnauthorizedAccessException("Access to the path is denied.") : null;
        none.Builder.Throws = request => request.Leg == "native/workers/2" ? new IOException("There is not enough space on the disk.") : null;

        var neither = await none.RunAsync([ChargeBound, DepthType]).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(
            (LegVerdict.Poisoned, "worker 1: it could not be made, UnauthorizedAccessException: Access to the path is denied; worker 2: it could not be made, IOException: There is not enough space on the disk; 2 arm(s): 2 stopped"),
            (neither.Verdict, neither.Detail));
    }

    /// <summary>
    /// A site that cannot be read back once it was put back - the disk gone, the file held - cannot be said to hold what
    /// the tree held: its arm is poisoned, naming the site and why, and its worker retired, while the other worker
    /// drives every other arm to its verdict.
    /// </summary>
    [Fact]
    public async Task ASiteThatCannotBeReadBack_PoisonsItsArm_AndTheOtherWorkersGoOn()
    {
        using var sweep = new Sweep();
        sweep.SiteFiles = new UnreadableOncePutBack(sweep.Harness.FileSystem, "budget.hpp", TreeFiles["src/budget.hpp"]);

        var entry = await sweep.RunAsync([ChargeBound, DepthType, ChargeFloor]).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal((LegVerdict.Poisoned, "3 arm(s): 1 poisoned, 2 passed"), (entry.Verdict, entry.Detail));
        Assert.Equal(
            "a site could not be put back as it was, so its worker drives no other arm: 'src/budget.hpp' could not be read back: the disk went away",
            entry.Arms.Single(arm => arm.Arm == "depth-type").Detail);
        Assert.All(entry.Arms.Where(arm => arm.Arm != "depth-type"), arm => Assert.Equal(LegVerdict.Passed, arm.Verdict));
        Assert.Equal([sweep.Worker(1), sweep.Worker(2)], sweep.Copies.Released.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A site or a text an arm's pre-flight cannot read - held by another process, the disk gone - poisons that arm
    /// alone, saying why: nothing of it was built or written, so its worker is not retired, and drives the next arm.
    /// </summary>
    [Fact]
    public async Task AFileAPreflightCannotRead_PoisonsItsArm_AndItsWorkerDrivesTheNext()
    {
        using var sweep = new Sweep { Workers = 1 };
        var writes = new RecordingWrites(new Unreadable(sweep.Harness.FileSystem, "budget.hpp", "floor.after"));
        sweep.SiteFiles = writes;

        var entry = await sweep.RunAsync([DepthType, ChargeFloor, ChargeBound]);

        Assert.Equal(
            [
                ("charge-bound", LegVerdict.Passed, "ran 3 case(s), 1 red as declared, and said its diagnostic"),
                ("depth-type", LegVerdict.Poisoned, "a file its pre-flight reads could not be read, so nothing of the arm was built or written: 'budget.hpp' is held by another process"),
                ("charge-floor", LegVerdict.Poisoned, "a file its pre-flight reads could not be read, so nothing of the arm was built or written: 'floor.after' is held by another process"),
            ],
            entry.Arms.Select(arm => (arm.Arm, arm.Verdict, arm.Detail)));
        Assert.All(entry.Arms, arm => Assert.Equal(1, arm.Worker));
        Assert.Equal(["native/arms/charge-bound"], sweep.Builder.Builds.Select(build => build.Leg).Where(leg => leg.Contains("/arms/", StringComparison.Ordinal)));

        // Written twice, both for the arm that was driven: mutated, and put back.
        Assert.Equal(2, writes.Written.Count);
    }

    /// <summary>
    /// A control that ends in a failure nobody named is the leg's own, as a leg's work ending so is: only its binary's
    /// arms are stopped, saying why, and every other arm is still driven.
    /// </summary>
    [Fact]
    public async Task AControlEndingInAFailureNobodyNamed_IsTheLegsOwn_AndStopsOnlyItsBinarysArms()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Tests.Before = (request, _) => request.Bound is null ? throw new IOException("The report's directory could not be made.") : Task.CompletedTask;

        var entry = await sweep.RunAsync([ChargeBound, DepthType, ChargeFloor]);

        Assert.Equal(
            (LegVerdict.Poisoned, "the unmutated fixture_tests could not be built and run, IOException: The report's directory could not be made; 3 arm(s): 2 stopped, 1 passed"),
            (entry.Verdict, entry.Detail));
        Assert.Equal(
            [
                (LegVerdict.Stopped, "the unmutated fixture_tests could not be built and run, so nothing could tell what a mutation of it changed"),
                (LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
                (LegVerdict.Stopped, "the unmutated fixture_tests could not be built and run, so nothing could tell what a mutation of it changed"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.Single(sweep.Tests.Runs);
    }

    /// <summary>
    /// A worker that cannot be made is retired, saying why: the leg says it ran with fewer where another worker drove the
    /// arms, and where none was made its workers' reasons are the leg's own and each arm is stopped, naming them.
    /// </summary>
    [Fact]
    public async Task AWorkerThatCannotBeMade_IsRetired_AndOnlyWhereNoneIsMadeIsItTheLegsOwn()
    {
        using var one = new Sweep();
        one.Builder.Answer = request => request.Leg == "native/workers/2" ? ReachedVerdict.Of(LegVerdict.Failed, "exited 2") : null;

        var fewer = await one.RunAsync([ChargeBound, DepthType]);

        Assert.Equal(
            (LegVerdict.Passed, "2 arm(s): 2 passed; worker 2: its build of the unmutated tree: exited 2, so it drove no arm"),
            (fewer.Verdict, fewer.Detail));
        Assert.All(fewer.Arms, arm => Assert.Equal(1, arm.Worker));

        using var both = new Sweep();
        both.Builder.Answer = request => request.Leg.StartsWith("native/workers/", StringComparison.Ordinal) ? ReachedVerdict.Of(LegVerdict.Failed, "exited 2") : null;

        var none = await both.RunAsync([ChargeBound, DepthType]);

        Assert.Equal(LegVerdict.Failed, none.Verdict);
        Assert.Equal(
            "worker 1: its build of the unmutated tree: exited 2; worker 2: its build of the unmutated tree: exited 2; 2 arm(s): 2 stopped",
            none.Detail);
        Assert.All(none.Arms, arm => Assert.Equal(
            "no worker was left to drive it: worker 1 was retired, its build of the unmutated tree: exited 2; worker 2 was retired, its build of the unmutated tree: exited 2",
            arm.Detail));
    }

    /// <summary>
    /// A worker another sweep holds is refused-locked and never given up by this one; one the machine does not admit is
    /// not-admitted; one whose copy cannot be made is the verdict the copying reached - each saying which worker, and why.
    /// </summary>
    [Fact]
    public async Task AWorkerHeldNotAdmittedOrUncopied_IsRetiredWithItsOwnVerdict()
    {
        using var held = new Sweep { Workers = 1 };
        held.Copies.Taken.Add(held.Worker(1));

        var locked = await held.RunAsync([DepthType]);

        Assert.Equal(
            (LegVerdict.RefusedLocked, $"worker 1: '{held.Worker(1)}' is held by another sweep: {Copies.Holder}; 1 arm(s): 1 stopped"),
            (locked.Verdict, locked.Detail));
        Assert.Empty(held.Copies.Released);

        using var refused = new Sweep { Workers = 1, Admit = unit => unit.Unit == "worker-1" ? Admission.Refused(new AdmissionFact(false, 0), "memory 97% in use") : null };

        var unadmitted = await refused.RunAsync([DepthType]);

        Assert.Equal((LegVerdict.NotAdmitted, "worker 1: memory 97% in use; 1 arm(s): 1 stopped"), (unadmitted.Verdict, unadmitted.Detail));
        Assert.Empty(refused.Copies.Synced);
        Assert.Equal([refused.Worker(1)], refused.Copies.Released);

        using var uncopied = new Sweep { Workers = 1 };
        uncopied.Copies.SyncFails = _ => new HarnessException(HarnessExit.CommandFailed, "the copy could not be written");

        var failed = await uncopied.RunAsync([DepthType]);

        Assert.Equal((LegVerdict.Failed, "worker 1: the copy could not be written; 1 arm(s): 1 stopped"), (failed.Verdict, failed.Detail));
    }

    /// <summary>
    /// A copy the sync refuses to make - somebody's directory where a worker would be - refuses the run, as a host's copy
    /// does: the leg's line names the refusal as what ends the run, each arm stopped undriven.
    /// </summary>
    [Fact]
    public async Task ACopyTheSyncRefusesToMake_RefusesTheRun()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Copies.SyncFails = worker => new HarnessException(HarnessExit.Refused, $"'{worker}' is no copy this tool made");

        var entry = await sweep.RunAsync([DepthType]);

        Assert.Equal((HarnessExit.Refused, $"'{sweep.Worker(1)}' is no copy this tool made"), (entry.EndsTheRun?.ExitCode, entry.EndsTheRun?.Message));
        Assert.Equal((LegVerdict.Stopped, StoppedUndriven), (entry.Arms[0].Verdict, entry.Arms[0].Detail));
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Released);
    }

    /// <summary>A worker another live sweep holds is taken where the sweep was told to force, as --force-lock takes a lock.</summary>
    [Fact]
    public async Task AHeldWorker_IsTaken_WhereTheSweepForces()
    {
        using var sweep = new Sweep { Workers = 1, Force = true };
        sweep.Copies.Taken.Add(sweep.Worker(1));

        var entry = await sweep.RunAsync([DepthType]);

        Assert.Equal(LegVerdict.Passed, entry.Verdict);
        Assert.Equal([(sweep.Worker(1), true)], sweep.Copies.Claimed);
    }

    /// <summary>
    /// An arm its machine does not admit is not-admitted, saying why, and so at once is every arm after it, its machine
    /// asked nothing more: each would wait as long as the one refused did, and a sweep of a hundred arms would wait days
    /// to say what its first refusal said. An arm judged before it keeps its verdict, and each arm not admitted has its
    /// record.
    /// </summary>
    [Fact]
    public async Task AnArmRefusedAdmission_EndsTheRestNotAdmitted_WithoutAskingAgain()
    {
        const string Refusal = "not admitted after 1h0m: it held a heavy-leg slot, and the memory 97% in use never fell below 76%";

        using var sweep = new Sweep
        {
            Workers = 1,
            Admit = unit => unit.Unit == "charge-bound" ? Admission.Refused(new AdmissionFact(false, 3600), Refusal) : null,
        };

        var entry = await sweep.RunAsync([DepthType, ChargeBound, ChargeFloor]);

        Assert.Equal((LegVerdict.NotAdmitted, "3 arm(s): 2 not-admitted, 1 passed"), (entry.Verdict, entry.Detail));
        Assert.Equal(
            [
                ("charge-bound", LegVerdict.NotAdmitted, Refusal),
                ("depth-type", LegVerdict.Passed, $"the mutation stops the build at {SiteObject}, and its paired control builds"),
                ("charge-floor", LegVerdict.NotAdmitted, $"the sweep stopped asking its machine once arm 'charge-bound' was not admitted: {Refusal}"),
            ],
            entry.Arms.Select(arm => (arm.Arm, arm.Verdict, arm.Detail)));
        Assert.Equal(["worker-1", "depth-type", "charge-bound"], sweep.Admissions.Select(unit => unit.Unit));
        Assert.All(
            new[] { "charge-bound", "charge-floor" },
            arm => Assert.True(File.Exists(Path.Combine(sweep.Records(arm), MutationRecords.ArmRecordFileName))));
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.StartsWith("native/arms/charge-", StringComparison.Ordinal));
        Assert.Empty(sweep.Tests.Runs);
    }

    /// <summary>
    /// An arm still waiting for its machine when another arm of the sweep is refused stops waiting at once, and is
    /// not-admitted as every arm after the refusal is.
    /// </summary>
    [Fact]
    public async Task AnArmWaitingForItsMachine_WhenAnotherIsRefused_StopsWaiting()
    {
        const string Refusal = "not admitted after 1h0m: it held a heavy-leg slot, and the memory 97% in use never fell below 76%";

        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var sweep = new Sweep
        {
            AdmitAsync = async (unit, token) =>
            {
                switch (unit.Unit)
                {
                    case "charge-bound":
                        await waiting.Task.WaitAsync(token);
                        return Admission.Refused(new AdmissionFact(false, 3600), Refusal);

                    case "charge-floor":
                        waiting.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return null;

                    default:
                        return null;
                }
            },
        };

        var entry = await sweep.RunAsync([ChargeBound, ChargeFloor]).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                (LegVerdict.NotAdmitted, Refusal),
                (LegVerdict.NotAdmitted, $"the sweep stopped asking its machine once arm 'charge-bound' was not admitted: {Refusal}"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.Equal([sweep.Worker(1), sweep.Worker(2)], sweep.Copies.Released.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Two arms refused while a third still waits: each refused arm says its own refusal, and the one waiting is told the
    /// first, whichever landed later.
    /// </summary>
    [Fact]
    public async Task TwoArmsRefused_TellAnArmStillWaitingTheFirstRefusal()
    {
        const string First = "not admitted after 1h0m: it held a heavy-leg slot, and the memory 97% in use never fell below 76%";
        const string Second = "not admitted after 1h0m: the 5 heavy leg(s) this machine admits at once were all held";

        var arrived = 0;
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Sweep? made = null;

        using var sweep = made = new Sweep
        {
            Workers = 3,
            AdmitAsync = async (unit, token) =>
            {
                switch (unit.Unit)
                {
                    case "charge-bound":
                        await waiting.Task.WaitAsync(token);
                        return Admission.Refused(new AdmissionFact(false, 3600), First);

                    case "charge-floor":
                        Arrived();

                        // Refused once the first refusal has ended its wait, and so after it.
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        catch (OperationCanceledException)
                        {
                        }

                        return Admission.Refused(new AdmissionFact(false, 3600), Second);

                    case "depth-type":
                        Arrived();

                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        catch (OperationCanceledException)
                        {
                            // Told only once the second refusal has landed too: its arm's record is written after it.
                            var second = Path.Combine(made!.Records("charge-floor"), MutationRecords.ArmRecordFileName);

                            while (!File.Exists(second))
                            {
                                await Task.Delay(10, TestContext.Current.CancellationToken);
                            }

                            throw;
                        }

                        return null;

                    default:
                        return null;
                }
            },
        };

        var entry = await sweep.RunAsync([ChargeBound, DepthType, ChargeFloor]).WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ("charge-bound", LegVerdict.NotAdmitted, First),
                ("depth-type", LegVerdict.NotAdmitted, $"the sweep stopped asking its machine once arm 'charge-bound' was not admitted: {First}"),
                ("charge-floor", LegVerdict.NotAdmitted, Second),
            ],
            entry.Arms.Select(arm => (arm.Arm, arm.Verdict, arm.Detail)));

        void Arrived()
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                waiting.TrySetResult();
            }
        }
    }

    /// <summary>
    /// What an arm's build says decides it as the judge reads it: an object depending on a site ninja's log shows was not
    /// rebuilt is unwitnessed, and a log that cannot be read is unmeasured - neither ever passed.
    /// </summary>
    [Fact]
    public async Task ABuildThatDidNotWitnessItsRebuild_IsUnwitnessed_OrUnmeasuredWithoutALog()
    {
        using var stale = new Sweep { Workers = 1 };
        stale.Builder.Rebuilds = false;

        var unwitnessed = await stale.RunAsync([ChargeBound]);

        Assert.Equal(
            (LegVerdict.Unwitnessed, $"{SiteObject} depends on the site, and the mutated build did not rebuild it"),
            (unwitnessed.Arms[0].Verdict, unwitnessed.Arms[0].Detail));

        using var unlogged = new Sweep { Workers = 1 };
        unlogged.Builder.Logs = false;

        var unmeasured = await unlogged.RunAsync([ChargeBound]);

        Assert.Equal(
            (LegVerdict.Unmeasured, $"the mutated build: ninja's log in '{Variant.DirectoryUnder(unlogged.Worker(1))}' could not be read before the build, so nothing witnessed that each object depending on the site was rebuilt"),
            (unmeasured.Arms[0].Verdict, unmeasured.Arms[0].Detail));
    }

    /// <summary>
    /// A text an arm cites that the worker's copy does not hold, or a before-text holding nothing, is violated before
    /// anything of the arm is built, and its site is never written.
    /// </summary>
    [Fact]
    public async Task ATextTheCopyCannotGive_IsViolated_BeforeAnythingIsBuilt()
    {
        using var sweep = new Sweep { Workers = 1 };
        var missing = DepthType with { Id = "depth-missing", Own = DepthType.Own with { After = "texts/missing.after" } };
        var empty = DepthType with { Id = "depth-empty", Line = 9, Own = DepthType.Own with { Before = "texts/empty.before" } };

        var entry = await sweep.RunAsync([missing, empty]);

        Assert.Equal(
            [
                (LegVerdict.Violated, "text 'texts/missing.after' is not a file in the worker's copy of the tree"),
                (LegVerdict.Violated, "the text in 'texts/empty.before' holds nothing, and a text holding nothing occurs everywhere"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.Contains("/arms/", StringComparison.Ordinal));
        Assert.Equal(TreeFiles["src/budget.hpp"], File.ReadAllText(Path.Combine(sweep.Worker(1), "src", "budget.hpp")));
    }

    /// <summary>
    /// An arm's run starts where the leg's tests start, read for the worker's copy, in the environment they start in: the
    /// leg's host's, and the test invocation's over it.
    /// </summary>
    [Fact]
    public async Task AnArmsRun_StartsWhereTheLegsTestsStart_InTheWorker_WithTheirEnvironment()
    {
        using var sweep = new Sweep
        {
            Workers = 1,
            Test = new TestConfig { All = new TestInvocation { Runner = "ctest", WorkingDirectory = "{buildDir}/tests", Env = new() { ["FIXTURE_MODE"] = "strict" } } },
        };
        sweep.HostEnv["HOST_SETTING"] = "1";
        sweep.HostEnv["FIXTURE_MODE"] = "loose";

        await sweep.RunAsync([ChargeBound]);

        var run = sweep.Tests.Runs.Single(request => request.Bound is not null);

        Assert.Equal(Path.GetFullPath(Path.Combine(Variant.DirectoryUnder(sweep.Worker(1)), "tests")), Path.GetFullPath(run.WorkingDirectory));
        Assert.Equal("strict", run.Environment["FIXTURE_MODE"]);
        Assert.Equal("1", run.Environment["HOST_SETTING"]);
    }

    /// <summary>
    /// A leg asked about no arm it drives touches nothing - no reading, no worker - and says each arm it left out, and
    /// why, beneath it; an arm left out beside driven ones is listed among them in the registry's order.
    /// </summary>
    [Fact]
    public async Task ALegDrivingNoArm_TouchesNothing_AndEveryArmLeftOutIsListedWithWhy()
    {
        using var idle = new Sweep();

        var nothing = await idle.RunAsync([], [new UnselectedArm(ChargeBound, "its S row, line 2, names other legs")]);

        Assert.Equal(
            (LegVerdict.SkippedNotSelected, "the sweep drives no arm on this leg; 1 arm(s): 1 skipped-not-selected"),
            (nothing.Verdict, nothing.Detail));
        Assert.Equal((LegVerdict.SkippedNotSelected, "its S row, line 2, names other legs"), (nothing.Arms[0].Verdict, nothing.Arms[0].Detail));
        Assert.Equal(0, idle.Reader!.Reads);
        Assert.Empty(idle.Copies.Claimed);

        using var some = new Sweep { Workers = 1 };

        var mixed = await some.RunAsync([DepthType], [new UnselectedArm(ChargeBound, "--arms does not name it")]);

        Assert.Equal((LegVerdict.Passed, "2 arm(s): 1 passed, 1 skipped-not-selected"), (mixed.Verdict, mixed.Detail));
        Assert.Equal(["charge-bound", "depth-type"], mixed.Arms.Select(arm => arm.Arm));
    }

    /// <summary>
    /// No room for even one worker turns the leg away, as a leg whose build does not fit is turned away, each arm with it,
    /// and nothing is copied; room for fewer than it wanted runs fewer, saying so.
    /// </summary>
    [Fact]
    public async Task NoRoomForOneWorker_TurnsTheLegAway_AndRoomForFewerRunsFewer()
    {
        var copy = TreeFiles.Values.Sum(text => (long)text.Length);

        using var full = new Sweep { ExpectedBuildBytes = 1L << 30 };
        full.Files = new ScriptedRoom(full.Harness.FileSystem, 10);

        var turned = await full.RunAsync([DepthType]);

        var why = $"{DiskSpace.Size(10)} free on '/data', and its first worker needs ~{DiskSpace.Size(copy + (1L << 30))}, each worker's build as declared";

        Assert.Equal((LegVerdict.SkippedUnavailable, $"{why}; 1 arm(s): 1 skipped-unavailable"), (turned.Verdict, turned.Detail));
        Assert.Equal((LegVerdict.SkippedUnavailable, why), (turned.Arms[0].Verdict, turned.Arms[0].Detail));
        Assert.Empty(full.Copies.Synced);

        using var tight = new Sweep { ExpectedBuildBytes = 1L << 30 };
        tight.Files = new ScriptedRoom(tight.Harness.FileSystem, (1L << 30) + copy + 100);

        var fewer = await tight.RunAsync([ChargeBound, DepthType]);

        Assert.StartsWith("2 arm(s): 2 passed; 1 of 2 workers: ", fewer.Detail, StringComparison.Ordinal);
        Assert.Equal([tight.Worker(1)], tight.Copies.Synced.Select(sync => sync.Worker));
    }

    /// <summary>
    /// A worker whose build would pass this machine's path limit is never made, as a worktree's is never made: where the
    /// first would, the leg is turned away saying how to fix it; where a later one would, the sweep runs those before it.
    /// </summary>
    [Fact]
    public async Task AWorkerPastThePathLimit_IsNeverMade()
    {
        using var deep = new Sweep { Worktrees = new WorktreeSettings { PathLimit = 40 } };
        deep.Budget = new PathBudget(deep.Harness.Platform);

        var turned = await deep.RunAsync([DepthType]);

        Assert.Equal(LegVerdict.SkippedUnavailable, turned.Verdict);
        Assert.StartsWith($"worker 1 would be kept at '{deep.Worker(1)}', where its build needs paths of ", turned.Detail, StringComparison.Ordinal);
        Assert.Contains(
            " characters, as worktrees.pathBudgetReserve and pathBudgetMargin reckon them, and every path must stay under 40: keep the tree at a "
            + "shorter path, or set worktrees.pathLimit where every tool its build runs takes longer ones; 1 arm(s): 1 skipped-unavailable",
            turned.Detail,
            StringComparison.Ordinal);
        Assert.Empty(deep.Copies.Synced);

        using var second = new Sweep();
        second.Budget.Check(Arg.Is<string>(path => path.EndsWith("-2", StringComparison.Ordinal)), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>())
            .Returns(PathBudgetResult.Exceeded(300, 260, -5));

        var fewer = await second.RunAsync([ChargeBound, DepthType]);

        Assert.Equal(
            $"2 arm(s): 2 passed; 1 of 2 workers: worker 2 would be kept at '{second.Worker(2)}', where its build needs paths of 300 characters, "
            + "as worktrees.pathBudgetReserve and pathBudgetMargin reckon them, and every path must stay under 260: keep the tree at a shorter "
            + "path, or set worktrees.pathLimit where every tool its build runs takes longer ones",
            fewer.Detail);
        Assert.Equal([second.Worker(1)], second.Copies.Synced.Select(sync => sync.Worker));
    }

    /// <summary>
    /// The path limit is reckoned for a worker as a worktree's is: the worker, its build directory below it, and below that
    /// the longest path a build makes, with the margin to spare.
    /// </summary>
    [Fact]
    public async Task APathLimit_IsReckonedForAWorker_AsAWorktreesIs()
    {
        using var sweep = new Sweep { Worktrees = new WorktreeSettings { PathBudgetReserve = 100, PathBudgetMargin = 7, PathLimit = 400 } };

        await sweep.RunAsync([DepthType]);

        var below = Variant.DirectoryUnder(sweep.Worker(1)).Length - sweep.Worker(1).Length;

        sweep.Budget.Received().Check(sweep.Worker(1), below + 100, 7, 400);
    }

    /// <summary>
    /// A subject that says how long a path its own build makes - the self-test's fixture - has each worker's paths reckoned
    /// by it in place of <c>worktrees.pathBudgetReserve</c>, the leg's own project's, and a worker past the limit says so.
    /// </summary>
    [Fact]
    public async Task ASubjectsOwnLongestPath_ReckonsItsWorkers_InPlaceOfTheConfiguredReserve()
    {
        using var sweep = new Sweep { PathReserve = 30, Worktrees = new WorktreeSettings { PathBudgetReserve = 100, PathBudgetMargin = 7, PathLimit = 400 } };

        await sweep.RunAsync([DepthType]);

        var below = Variant.DirectoryUnder(sweep.Worker(1)).Length - sweep.Worker(1).Length;

        sweep.Budget.Received().Check(sweep.Worker(1), below + 30, 7, 400);

        using var deep = new Sweep { PathReserve = 30, Worktrees = new WorktreeSettings { PathLimit = 40 } };
        deep.Budget = new PathBudget(deep.Harness.Platform);

        var turned = await deep.RunAsync([DepthType]);

        Assert.Contains(
            " characters, as the 30 its project's build makes below its build directory and worktrees.pathBudgetMargin reckon them, and every "
            + "path must stay under 40: ",
            turned.Detail,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A self-test holds each verdict the judge reached to the arm's design, its record written as held - a refusal of its
    /// pre-flight included - and leaves a verdict the sweep ended an arm with, as a control that stops it, as it is.
    /// </summary>
    [Fact]
    public async Task ASelfTest_HoldsEachVerdictTheJudgeReached_AndNoOther()
    {
        var misplaced = ChargeBound with { Id = "misplaced", Line = 9, Own = new MutationSite("src/nowhere.cpp", "texts/charge.before", "texts/charge.after", 9) };
        using var held = new Sweep { Workers = 1, Hold = (arm, reached) => ReachedVerdict.Of(LegVerdict.Passed, $"{arm} held, {Verdicts.Display(reached.Verdict)}: {reached.Detail}") };

        var entry = await held.RunAsync([ChargeBound, DepthType, misplaced]);

        Assert.Equal(
            [
                (LegVerdict.Passed, "charge-bound held, passed: ran 3 case(s), 1 red as declared, and said its diagnostic"),
                (LegVerdict.Passed, $"depth-type held, passed: the mutation stops the build at {SiteObject}, and its paired control builds"),
                (LegVerdict.Passed, "misplaced held, violated: site 'src/nowhere.cpp' is not a file in the worker's copy of the tree"),
            ],
            entry.Arms.Select(arm => (arm.Verdict, arm.Detail)));

        using (var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(held.Records("charge-bound"), MutationRecords.ArmRecordFileName))))
        {
            Assert.Equal(entry.Arms[0].Detail, record.RootElement.GetProperty("detail").GetString());
        }

        using var stopped = new Sweep { Workers = 1, Hold = (_, _) => throw new InvalidOperationException("a verdict the judge never reached was held") };
        stopped.Tests.Answer = request => request.Bound is null ? Tests.Ran(["Fixture.Depth"]) : Tests.Judged(request);

        var unheld = await stopped.RunAsync([ChargeBound]);

        Assert.Equal(
            (LegVerdict.Stopped, "the unmutated fixture_tests has 1 red, so no mutation of it proves anything"),
            (Assert.Single(unheld.Arms).Verdict, unheld.Arms[0].Detail));
    }

    /// <summary>
    /// A worker an earlier sweep left beyond <c>mutations.workers</c> is removed before the sweep plans, so lowering it frees
    /// the room it held - unless a live sweep holds it, or no sync made it, which is said and left.
    /// </summary>
    [Fact]
    public async Task WorkersBeyondTheCap_AreRemoved_UnlessHeldOrSomebodys()
    {
        using var sweep = new Sweep { Workers = 2 };
        sweep.Copies.Listed.AddRange(
        [
            sweep.Copy(1, CopyOrigin.Made),
            sweep.Copy(2, CopyOrigin.Made),
            sweep.Copy(3, CopyOrigin.Made),
            sweep.Copy(4, CopyOrigin.Unmarked),
            sweep.Copy(5, CopyOrigin.Made),
        ]);
        sweep.Copies.Held[sweep.Worker(5)] = Copies.Holder;

        await sweep.RunAsync([DepthType]);

        var said = sweep.Harness.StandardOutput.ToString() + sweep.Harness.StandardError;

        Assert.Equal([sweep.Worker(3)], sweep.Copies.Removed);
        Assert.Contains($"native: removed worker 3, '{sweep.Worker(3)}', 2 KiB, beyond the 2 worker(s) a sweep runs", said, StringComparison.Ordinal);
        Assert.Contains(
            $"native: worker 4, '{sweep.Worker(4)}', is beyond the 2 worker(s) a sweep runs, and was left where it is: nothing there says the harness made it, so it is yours to remove",
            said,
            StringComparison.Ordinal);
        Assert.Contains($"native: worker 5, '{sweep.Worker(5)}', is beyond the 2 worker(s) a sweep runs, and was left: {Copies.Holder}", said, StringComparison.Ordinal);

        // A claim a dead sweep left on a worker beyond the cap is released, and said, before the worker is removed or kept.
        Assert.Contains(sweep.Worker(3), sweep.Copies.Abandoned);
        Assert.Contains(sweep.Worker(5), sweep.Copies.Abandoned);
        Assert.DoesNotContain(sweep.Worker(4), sweep.Copies.Abandoned);
    }

    /// <summary>A sweep in a WSL distribution runs one worker, whatever <c>mutations.workers</c> says: it is admitted whole, as one heavy leg.</summary>
    [Fact]
    public async Task ASweepInAWslDistribution_RunsOneWorker()
    {
        using var sweep = new Sweep { Workers = 4, Host = HostId.Wsl("Example-Linux") };

        var entry = await sweep.RunAsync([ChargeBound, DepthType, ChargeFloor]);

        Assert.Equal(LegVerdict.Passed, entry.Verdict);
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Synced.Select(sync => sync.Worker));
    }

    /// <summary>
    /// Everything a sweep is given: its tree, the doubles of what touches a build, and how the leg is placed - each double
    /// recording what it was asked.
    /// </summary>
    private sealed class Sweep : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly JumpingClock _clock = new();

        public Sweep()
        {
            foreach (var (path, text) in TreeFiles)
            {
                _temp.WriteFile(Path.Combine("tree", path), text);
            }

            Copies = new Copies(Tree);
            Builder = new Builder(() => _clock.GetUtcNow().UtcDateTime);
            Budget.Check(default!, default, default, default).ReturnsForAnyArgs(PathBudgetResult.Unbounded());
        }

        public HarnessFactory Harness { get; } = new();

        public string Tree => _temp.Combine("tree");

        public string RunDirectory => _temp.Combine("runs", "r1");

        public Copies Copies { get; }

        public Builder Builder { get; }

        public Tests Tests { get; } = new();

        public IPathBudget Budget { get; set; } = Substitute.For<IPathBudget>();

        /// <summary>What the sites are read and written through, where not the real file system.</summary>
        public IFileSystem? SiteFiles { get; set; }

        /// <summary>What the runner measures room and writes records through, where not the real file system.</summary>
        public IFileSystem? Files { get; set; }

        public int Workers { get; init; } = 2;

        public long? ExpectedBuildBytes { get; init; }

        public bool Force { get; init; }

        public HostId Host { get; init; } = HostId.Local;

        public WorktreeSettings Worktrees { get; init; } = new();

        public TestConfig? Test { get; init; }

        public int? PathReserve { get; init; }

        public Func<string, ReachedVerdict, ReachedVerdict>? Hold { get; init; }

        public Dictionary<string, string> HostEnv { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Func<UnitAdmission, Admission?> Admit { get; init; } = _ => null;

        /// <summary>How the machine answers each unit asking to be taken, where it answers only after a while; otherwise <see cref="Admit"/> answers.</summary>
        public Func<UnitAdmission, CancellationToken, Task<Admission?>>? AdmitAsync { get; init; }

        public ConcurrentQueue<UnitAdmission> Admissions { get; } = new();

        public Source? Reader { get; private set; }

        public SyncSource? Reading => Reader?.Reading;

        public string Worker(int number) => MutationWorkers.PathOf(Tree, Variant, number);

        public string Records(string arm) => Path.Combine(RunDirectory, LegName, MutationRecords.ArmsDirectory, arm);

        /// <summary>Worker <paramref name="number"/> as a listing finds it, holding 2 KiB, its marker saying <paramref name="origin"/>.</summary>
        public WorkerCopy Copy(int number, CopyOrigin origin)
            => new(number, new HostCopyFound($"{Variant.DirectoryName}-{number}", Worker(number), origin, 2048));

        public async Task<LegEntry> RunAsync(IReadOnlyList<MutationArm> driven, IReadOnlyList<UnselectedArm>? unselected = null, CancellationToken? cancellationToken = null)
        {
            var os = Harness.Platform.PlatformKey;
            var settings = new MutationSettings { Workers = Workers, ReportArgs = ["--gtest_output=xml:{report}"], RunTimeFactor = 10 };
            var config = new HarnessConfig { Worktrees = Worktrees, Mutations = settings };
            var context = new HarnessContext(new HarnessLayout(Tree, Tree), config);
            var project = new ProjectConfig { Name = "app", Type = "cmake", Test = Test };
            var host = new HostReport { Host = Host, Os = os, Processor = Variant.Processor };
            var leg = new PlacedLeg(
                LegName,
                new LegConfig { Os = os, Processor = Variant.Processor, Config = "debug" },
                host,
                project,
                Variant,
                Tree,
                Tree,
                Variant.DirectoryUnder(Tree),
                new LocalHostConfig { Env = HostEnv },
                Emulated: false);

            Reader = new Source(Read(context));

            var work = new LegWork(leg, context, RunId.New(), RunDirectory, Time: false)
            {
                AdmitUnit = (unit, token) =>
                {
                    Admissions.Enqueue(unit);
                    return AdmitAsync is { } asked ? asked(unit, token) : Task.FromResult(Admit(unit));
                },
            };

            var runner = new MutationLegRunner(
                Reader,
                Copies,
                new WorkerSite(SiteFiles ?? Harness.FileSystem, _clock),
                Builder,
                Tests,
                Budget,
                Files ?? Harness.FileSystem,
                Harness.Output,
                MutationService.CommandName);

            return await runner.RunAsync(
                new MutationSubject
                {
                    TreeRoot = Tree,
                    Project = project,
                    Tests = Test,
                    PathReserve = PathReserve,
                    Hold = Hold,
                    Arms = new LegArms(driven, unselected ?? []),
                    Settings = settings,
                    ExpectedBuildBytes = ExpectedBuildBytes,
                    ExpectedBuildSource = ExpectedBuildBytes is null ? string.Empty : "as declared",
                    Force = Force,
                },
                work,
                cancellationToken ?? TestContext.Current.CancellationToken);
        }

        public void Dispose() => _temp.Dispose();

        /// <summary>Every source a worker that was made holds is as the tree holds it: each site an arm mutated was put back.</summary>
        public void AssertEverySiteAsTheTreeHoldsIt()
        {
            foreach (var worker in Copies.Synced.Select(sync => sync.Worker))
            {
                foreach (var site in new[] { "src/fixture.cpp", "src/budget.hpp" })
                {
                    Assert.Equal(TreeFiles[site], File.ReadAllText(Path.Combine(worker, site)));
                }
            }
        }

        /// <summary>The tree as a sync reads it: each file by its size and hash, keyed as a row spells it.</summary>
        private SyncSource Read(HarnessContext context)
        {
            var entries = new Dictionary<string, SyncEntry>(StringComparer.Ordinal);

            foreach (var path in TreeFiles.Keys)
            {
                var bytes = File.ReadAllBytes(Path.Combine(Tree, path));
                entries[path] = new SyncEntry(path, bytes.Length, FileContentHash.Of(bytes));
            }

            return new SyncSource(context, new SyncExclusions(new SyncConfig(), ".worktrees"), new SyncManifest(Tree, entries), []);
        }
    }

    /// <summary>The one reading of the tree, counting how many times it was asked for.</summary>
    private sealed class Source(SyncSource reading) : IMutationSource
    {
        private int _reads;

        public SyncSource Reading { get; } = reading;

        public int Reads => _reads;

        public Task<SyncSource> ReadAsync(string treeRoot, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(Reading);
        }
    }

    /// <summary>Workers' copies made by copying the tree's files, claimed, given up and removed as the test says.</summary>
    private sealed class Copies(string tree) : IWorkerCopies
    {
        /// <summary>How a refusal names the sweep holding a worker.</summary>
        public const string Holder = "run 20250101-120000-deadbeef (pid 4242 on this machine)";

        public List<WorkerCopy> Listed { get; } = [];

        public ConcurrentQueue<(SyncSource Reading, string Worker)> Synced { get; } = new();

        public ConcurrentQueue<(string Worker, bool Force)> Claimed { get; } = new();

        public ConcurrentQueue<string> Released { get; } = new();

        public ConcurrentQueue<string> Removed { get; } = new();

        public ConcurrentQueue<string> Abandoned { get; } = new();

        public Dictionary<string, string> Held { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Taken { get; } = new(StringComparer.Ordinal);

        public Func<string, Exception?> SyncFails { get; set; } = _ => null;

        /// <summary>What claiming a worker raises, where it raises anything.</summary>
        public Func<string, Exception?> ClaimThrows { get; set; } = _ => null;

        public Action<string> AfterSync { get; set; } = _ => { };

        public Task<IReadOnlyList<WorkerCopy>> ListAsync(string treeRoot, VariantKey variant, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<WorkerCopy>>([.. Listed]);

        public LogOwner? ReleaseAbandoned(string worker)
        {
            Abandoned.Enqueue(worker);
            return null;
        }

        public LogClaim Claim(string worker, RunId runId, bool force)
        {
            Claimed.Enqueue((worker, force));

            if (ClaimThrows(worker) is { } failure)
            {
                throw failure;
            }

            return Taken.Contains(worker) && !force
                ? new LogClaim(false, null, worker + MutationWorkers.ClaimSuffix) { HeldBy = Holder }
                : new LogClaim(true, null, worker + MutationWorkers.ClaimSuffix);
        }

        public void Release(string worker, RunId runId) => Released.Enqueue(worker);

        public string? HeldBy(string worker) => Held.GetValueOrDefault(worker);

        public Task SyncAsync(SyncSource source, string worker, CancellationToken cancellationToken)
        {
            if (SyncFails(worker) is { } failure)
            {
                throw failure;
            }

            foreach (var path in source.Files.Entries.Keys)
            {
                var copied = Path.Combine(worker, path);

                Directory.CreateDirectory(Path.GetDirectoryName(copied)!);
                File.Copy(Path.Combine(tree, path), copied, overwrite: true);
            }

            Synced.Enqueue((source, worker));
            AfterSync(worker);

            return Task.CompletedTask;
        }

        public Task<CopyRemoval> RemoveAsync(string worker, CancellationToken cancellationToken)
        {
            Removed.Enqueue(worker);
            return Task.FromResult(CopyRemoval.Removed);
        }
    }

    /// <summary>
    /// Builds as the fixture would: failing where the header says its depth is no integer, and every object rebuilt by each
    /// build that passes, as ninja's log says - unless the test says otherwise. Each build notes a site it finds mutated and
    /// dated at or after <paramref name="now"/>, which a build could leave its own outputs dated before.
    /// </summary>
    private sealed class Builder(Func<DateTime> now) : IArmBuilder
    {
        private readonly ConcurrentDictionary<string, int> _generations = new(StringComparer.Ordinal);

        public ConcurrentQueue<BuildRequest> Builds { get; } = new();

        /// <summary>Each build that started while a site it builds was dated at or after the clock.</summary>
        public ConcurrentQueue<string> DatedAhead { get; } = new();

        /// <summary>The one build graph every worker reads, recording what it is asked.</summary>
        public Graph Graph { get; } = new();

        public Func<BuildRequest, ReachedVerdict?> Answer { get; set; } = _ => null;

        public Func<BuildRequest, Exception?> Throws { get; set; } = _ => null;

        /// <summary>What happens before a build answers or throws, once it has been noted as started.</summary>
        public Func<BuildRequest, CancellationToken, Task> Before { get; set; } = (_, _) => Task.CompletedTask;

        public bool Rebuilds { get; set; } = true;

        public bool Logs { get; set; } = true;

        public async Task<BuildResult> BuildAsync(HarnessConfig config, BuildRequest request, CancellationToken cancellationToken)
        {
            Builds.Enqueue(request);

            foreach (var (path, text) in TreeFiles.Where(pair => pair.Key.StartsWith("src/", StringComparison.Ordinal)))
            {
                var site = Path.Combine(request.TreeRoot, path);

                if (File.Exists(site) && File.ReadAllText(site) != text && File.GetLastWriteTimeUtc(site) >= now())
                {
                    DatedAhead.Enqueue($"{request.Leg}: {path}");
                }
            }

            await Before(request, cancellationToken);

            if (Throws(request) is { } failure)
            {
                throw failure;
            }

            var directory = request.Variant.DirectoryUnder(request.TreeRoot);
            var broken = File.ReadAllText(Path.Combine(request.TreeRoot, "src", "budget.hpp")).Contains("three", StringComparison.Ordinal);
            var verdict = Answer(request) ?? (broken ? ReachedVerdict.Of(LegVerdict.Failed, "build exited 1") : ReachedVerdict.Of(LegVerdict.Passed, "built"));

            if (verdict.Verdict == LegVerdict.Passed)
            {
                _generations.AddOrUpdate(directory, 1, (_, generation) => generation + 1);
            }

            IReadOnlyList<PhaseResult> phases = verdict.Verdict == LegVerdict.Failed
                ? [new PhaseResult(request.Leg, CMakeAdapter.BuildPhase, 1, false, 0, null, TimeSpan.Zero, TimeSpan.Zero, false, [], Path.Combine(directory, "build.log"), PhaseOutput.Of($"[1/2] Building CXX object {SiteObject}\nFAILED: {SiteObject}\nerror: depth is no integer\n"))]
                : [];

            return new BuildResult(verdict, directory, phases, null, null);
        }

        public Task<IWorkerGraph> ReadGraphAsync(HarnessConfig config, BuildRequest request, CancellationToken cancellationToken)
            => Task.FromResult<IWorkerGraph>(Graph);

        public NinjaLog? ReadLog(string buildDirectory)
        {
            if (!Logs)
            {
                return null;
            }

            var generation = Rebuilds ? _generations.GetValueOrDefault(buildDirectory) : 0;

            return NinjaLog.Read(["# ninja log v5", $"{generation}\t{generation + 1}\t{generation}\t{SiteObject}\tfeed{generation}"]);
        }
    }

    /// <summary>
    /// The fixture's build as its manifest and records would say it: a library target, its test binary, and one object both
    /// build from the sites - recording the targets each question about what depends on a site was asked of.
    /// </summary>
    private sealed class Graph : IWorkerGraph
    {
        public ConcurrentQueue<IReadOnlyList<string>> AskedOfTargets { get; } = new();

        public IReadOnlyList<string> OutputsOf(string target) => target switch
        {
            "fixture" => ["bin/fixture"],
            "fixture_tests" => [Program],
            _ => [],
        };

        public WorkerProgram ProgramOf(string target)
            => target == "fixture_tests" ? new WorkerProgram(Program, null) : new WorkerProgram(null, $"runner '{target}' is built by no line of the leg's build");

        public IReadOnlyList<string> DependentObjects(IReadOnlyList<string> targets, IReadOnlyCollection<string> sites)
        {
            AskedOfTargets.Enqueue(targets);

            return sites.Any(site => Path.GetFileName(site) is "fixture.cpp" or "budget.hpp") ? [SiteObject] : [];
        }

        public IReadOnlyList<string> FailedOutputs(IEnumerable<string> lines)
            => [.. lines.Where(line => line.StartsWith("FAILED: ", StringComparison.Ordinal)).Select(line => line["FAILED: ".Length..])];
    }

    /// <summary>Runs the fixture's test binary as it would run: a case red for each mutation its source holds, and its diagnostic said with one.</summary>
    private sealed class Tests : IArmTestRunner
    {
        /// <summary>How long every run takes.</summary>
        public static readonly TimeSpan Took = TimeSpan.FromSeconds(30);

        public ConcurrentQueue<ArmRunRequest> Runs { get; } = new();

        public Func<ArmRunRequest, ArmRun> Answer { get; set; } = Judged;

        public Func<ArmRunRequest, CancellationToken, Task> Before { get; set; } = (_, _) => Task.CompletedTask;

        public async Task<ArmRunResult> RunAsync(ArmRunRequest request, CancellationToken cancellationToken)
        {
            Runs.Enqueue(request);
            await Before(request, cancellationToken);

            return new ArmRunResult(Answer(request), Took, Path.Combine(request.RecordDirectory, "run.log"), Path.Combine(request.RecordDirectory, "report.xml"));
        }

        /// <summary>What the binary built in the request's worker says, read from the worker's source as it stands.</summary>
        public static ArmRun Judged(ArmRunRequest request)
        {
            // The program is <worker>/build/<variant>/bin/fixture_tests.
            var worker = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(request.Program))))!;
            var source = File.ReadAllText(Path.Combine(worker, "src", "fixture.cpp"));
            var reds = new List<string>();

            if (source.Contains("c < b", StringComparison.Ordinal))
            {
                reds.Add("Fixture.Charge");
            }

            if (source.Contains("c >= 0", StringComparison.Ordinal))
            {
                reds.Add("Fixture.Floor");
            }

            return Ran(reds) with
            {
                Bound = request.Bound ?? TimeSpan.Zero,
                Factor = request.Factor,
                DiagnosticSaid = request.Diagnostic == "charge exceeded" && reds.Count > 0,
            };
        }

        /// <summary>A whole run of the binary's three cases, those in <paramref name="reds"/> red, exiting 1 where any is.</summary>
        public static ArmRun Ran(IReadOnlyList<string> reds)
        {
            var cases = string.Concat(new[] { "Charge", "Floor", "Depth" }.Select(name =>
                $"<testcase classname=\"Fixture\" name=\"{name}\">{(reds.Contains($"Fixture.{name}") ? "<failure message=\"red\"/>" : string.Empty)}</testcase>"));

            return new ArmRun
            {
                ExitCode = reds.Count > 0 ? 1 : 0,
                ReportWritten = true,
                Report = JUnitReport.Read($"<testsuites><testsuite name=\"Fixture\">{cases}</testsuite></testsuites>"),
            };
        }
    }

    /// <summary>A file system, recording each file written through it.</summary>
    private sealed class RecordingWrites(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        public ConcurrentQueue<string> Written { get; } = new();

        public override Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        {
            Written.Enqueue(path);
            return base.WriteAllBytesAtomicAsync(path, contents, cancellationToken);
        }
    }

    /// <summary>A file system, save that no file named as one of <paramref name="names"/> can be read, as one another process holds cannot.</summary>
    private sealed class Unreadable(IFileSystem inner, params string[] names) : PassThroughFileSystem(inner)
    {
        public override byte[] ReadAllBytes(string path)
            => names.Contains(Path.GetFileName(path), StringComparer.Ordinal)
                ? throw new IOException($"'{Path.GetFileName(path)}' is held by another process.")
                : base.ReadAllBytes(path);
    }

    /// <summary>
    /// The real file system, save that a file named <paramref name="name"/> cannot be read once <paramref name="pristine"/>
    /// was written back to it, as a disk that went away once the write returned cannot.
    /// </summary>
    private sealed class UnreadableOncePutBack(IFileSystem inner, string name, string pristine) : PassThroughFileSystem(inner)
    {
        private readonly ConcurrentDictionary<string, bool> _putBack = new(StringComparer.Ordinal);

        public override async Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        {
            await base.WriteAllBytesAtomicAsync(path, contents, cancellationToken);

            if (Path.GetFileName(path) == name && System.Text.Encoding.UTF8.GetString(contents) == pristine)
            {
                _putBack[path] = true;
            }
        }

        public override byte[] ReadAllBytes(string path)
            => _putBack.ContainsKey(path) ? throw new IOException("the disk went away.") : base.ReadAllBytes(path);
    }

    /// <summary>The real file system, save that writing <paramref name="name"/> back as <paramref name="pristine"/> fails, as a disk that went away fails.</summary>
    private sealed class FailingRestore(IFileSystem inner, string name, string pristine) : PassThroughFileSystem(inner)
    {
        public override Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
            => Path.GetFileName(path) == name && System.Text.Encoding.UTF8.GetString(contents) == pristine
                ? throw new IOException("the disk went away.")
                : base.WriteAllBytesAtomicAsync(path, contents, cancellationToken);
    }
}
