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
            "a site could not be put back as it was, so its worker drives no other arm: writing it back failed: the disk went away; "
            + "'src/budget.hpp' does not hold what the tree held when the sweep read it",
            entry.Arms.Single(arm => arm.Arm == "depth-type").Detail);
        Assert.All(entry.Arms.Where(arm => arm.Arm != "depth-type"), arm => Assert.Equal(LegVerdict.Passed, arm.Verdict));
    }

    /// <summary>
    /// A site is put back only as the sweep's reading of the tree vouches for it: one gone once its writing back failed is
    /// said gone, and one the reading never held - made in the worker after the tree was read - is vouched for by nothing.
    /// Either poisons its arm.
    /// </summary>
    [Fact]
    public async Task ASiteGone_OrOneTheReadingNeverHeld_PoisonsItsArm()
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
            (LegVerdict.Poisoned, "a site could not be put back as it was, so its worker drives no other arm: writing it back failed: the disk went away; 'src/fixture.cpp' is gone"),
            (lost.Arms[0].Verdict, lost.Arms[0].Detail));

        using var unread = new Sweep { Workers = 1 };
        unread.Copies.AfterSync = worker =>
        {
            Directory.CreateDirectory(Path.Combine(worker, "src", "gen"));
            File.WriteAllText(Path.Combine(worker, "src", "gen", "fixture.cpp"), TreeFiles["src/fixture.cpp"]);
        };

        var made = await unread.RunAsync([ChargeBound with { Own = ChargeBound.Own with { Site = "src/gen/fixture.cpp" } }]);

        Assert.Equal(
            (LegVerdict.Poisoned, "a site could not be put back as it was, so its worker drives no other arm: 'src/gen/fixture.cpp' is no file the sweep's reading of the tree holds, so nothing vouches for what it holds"),
            (made.Arms[0].Verdict, made.Arms[0].Detail));
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

    /// <summary>A refusal from inside an arm ends the sweep, as it ends any leg's work, once every site is put back.</summary>
    [Fact]
    public async Task ARefusalFromInsideAnArm_EndsTheSweep_OnceEverySiteIsPutBack()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Builder.Throws = request => request.Leg.Contains("/arms/", StringComparison.Ordinal)
            ? new HarnessException(HarnessExit.ConfigInvalid, "a setting is wrong")
            : null;

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => sweep.RunAsync([DepthType]));

        Assert.Equal((HarnessExit.ConfigInvalid, "a setting is wrong"), (refusal.ExitCode, refusal.Message));
        Assert.Equal(TreeFiles["src/budget.hpp"], File.ReadAllText(Path.Combine(sweep.Worker(1), "src", "budget.hpp")));
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Released);
    }

    /// <summary>A sweep stopped part way puts back each site it had mutated, gives its workers up, and reports nothing of the leg.</summary>
    [Fact]
    public async Task ASweepStoppedPartWay_PutsEverySiteBack_AndReportsNothing()
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep.RunAsync([ChargeBound, ChargeFloor], cancellationToken: stop.Token));

        Assert.Equal(TreeFiles["src/fixture.cpp"], File.ReadAllText(Path.Combine(sweep.Worker(1), "src", "fixture.cpp")));
        Assert.Equal([sweep.Worker(1)], sweep.Copies.Released);
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

    /// <summary>A copy the sync refuses to make - somebody's directory where a worker would be - refuses the run, as a host's copy does.</summary>
    [Fact]
    public async Task ACopyTheSyncRefusesToMake_RefusesTheRun()
    {
        using var sweep = new Sweep { Workers = 1 };
        sweep.Copies.SyncFails = worker => new HarnessException(HarnessExit.Refused, $"'{worker}' is no copy this tool made");

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => sweep.RunAsync([DepthType]));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
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
    /// An arm its machine does not admit is not-admitted, saying why, its record written; the arms beside it are still
    /// driven.
    /// </summary>
    [Fact]
    public async Task AnArmItsMachineDoesNotAdmit_IsNotAdmitted_AndTheOthersAreDriven()
    {
        using var sweep = new Sweep
        {
            Workers = 1,
            Admit = unit => unit.Unit == "depth-type" ? Admission.Refused(new AdmissionFact(false, 0), "memory 97% in use") : null,
        };

        var entry = await sweep.RunAsync([ChargeBound, DepthType]);

        Assert.Equal((LegVerdict.NotAdmitted, "2 arm(s): 1 not-admitted, 1 passed"), (entry.Verdict, entry.Detail));
        Assert.Equal((LegVerdict.NotAdmitted, "memory 97% in use"), (entry.Arms[1].Verdict, entry.Arms[1].Detail));
        Assert.True(File.Exists(Path.Combine(sweep.Records("depth-type"), MutationRecords.ArmRecordFileName)));
        Assert.DoesNotContain(sweep.Builder.Builds, build => build.Leg.StartsWith("native/arms/depth-type", StringComparison.Ordinal));
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
                AdmitUnit = (unit, _) =>
                {
                    Admissions.Enqueue(unit);
                    return Task.FromResult(Admit(unit));
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

        public bool Rebuilds { get; set; } = true;

        public bool Logs { get; set; } = true;

        public Task<BuildResult> BuildAsync(HarnessConfig config, BuildRequest request, CancellationToken cancellationToken)
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

            return Task.FromResult(new BuildResult(verdict, directory, phases, null, null));
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

    /// <summary>The real file system, save that writing <paramref name="name"/> back as <paramref name="pristine"/> fails, as a disk that went away fails.</summary>
    private sealed class FailingRestore(IFileSystem inner, string name, string pristine) : PassThroughFileSystem(inner)
    {
        public override Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
            => Path.GetFileName(path) == name && System.Text.Encoding.UTF8.GetString(contents) == pristine
                ? throw new IOException("the disk went away.")
                : base.WriteAllBytesAtomicAsync(path, contents, cancellationToken);
    }
}
