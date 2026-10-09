using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// How many workers a sweep runs: no more than its cap and its arms, one in WSL, and of those the ones that fit the room,
/// counted each beside the ones before it - fewer said as fewer, and none turning the leg away.
/// </summary>
public sealed class WorkerRoomTests
{
    private const long Gibibyte = 1L << 30;

    /// <summary>A sweep wants its cap, never more than it has arms, and one in a WSL distribution.</summary>
    [Theory]
    [InlineData(2, 10, false, 2)]
    [InlineData(4, 3, false, 3)]
    [InlineData(2, 0, false, 0)]
    [InlineData(4, 10, true, 1)]
    [InlineData(4, 0, true, 0)]
    public void ASweepWants_ItsCap_NoMoreThanItsArms_AndOneInWsl(int cap, int arms, bool wsl, int wanted)
        => Assert.Equal(wanted, WorkerRoom.Wanted(cap, arms, wsl));

    /// <summary>
    /// A worker needs its copy where it has none, and its build's expected size less what its last build recorded -
    /// all of it where it has no build directory, nothing where its directory holds what no build recorded, and nothing
    /// for the build where nothing says what one comes to.
    /// </summary>
    [Theory]
    [InlineData(false, 10L, false, null, 13L)]
    [InlineData(true, 10L, false, null, 10L)]
    [InlineData(true, 10L, true, 4L, 6L)]
    [InlineData(true, 10L, true, 12L, 0L)]
    [InlineData(false, 10L, true, 4L, 9L)]
    [InlineData(true, 10L, true, null, 0L)]
    [InlineData(false, null, false, null, 3L)]
    [InlineData(true, null, true, 4L, 0L)]
    public void AWorkerNeeds_ItsCopy_AndWhatItsBuildHasNotReached(bool copyPresent, long? expected, bool buildPresent, long? recorded, long need)
        => Assert.Equal(need * Gibibyte, WorkerRoom.Need(3 * Gibibyte, copyPresent, expected * Gibibyte, buildPresent, recorded * Gibibyte));

    /// <summary>Every worker wanted runs where they fit together, the last to the byte.</summary>
    [Fact]
    public void EveryWorkerRuns_WhereTheyFitTogether()
        => Assert.Equal(WorkerPlan.Running(2), Plan([6 * Gibibyte, 4 * Gibibyte], Room(10 * Gibibyte), null, null));

    /// <summary>
    /// Workers are counted in order, each beside the ones before it: a sweep runs those that fit, and says it runs fewer,
    /// with what is free and what one more would need - never skipping a worker that does not fit for a later one that would.
    /// </summary>
    [Fact]
    public void FewerWorkersRun_WhereFewerFit_SayingSo()
    {
        var plan = Plan([6 * Gibibyte, 5 * Gibibyte, 0], Room(10 * Gibibyte), null, "as its buildSpaceGiB, 4, declares");

        Assert.Equal(
            WorkerPlan.Running(1, "1 of 3 workers: running 2 needs ~11 GiB, each worker's build as its buildSpaceGiB, 4, declares; 10 GiB free on '/srv'"),
            plan);
        Assert.False(plan.RunsNone);
    }

    /// <summary>A sweep whose first worker does not fit runs none, saying what is free and what that worker needs.</summary>
    [Fact]
    public void NoWorkerRuns_WhereTheFirstDoesNotFit()
    {
        var none = Plan([12 * Gibibyte, 0], Room(10 * Gibibyte), null, "what its last build there came to");

        Assert.Equal(
            WorkerPlan.None("its first worker needs ~12 GiB, each worker's build what its last build there came to; 10 GiB free on '/srv'"),
            none);
        Assert.True(none.RunsNone);
        Assert.Equal((0, null), (none.Count, none.Unchecked));
        Assert.Equal(
            WorkerPlan.None("its first worker needs ~3 GiB; 1 GiB free on '/srv'"),
            Plan([3 * Gibibyte], Room(Gibibyte), null, null));
    }

    /// <summary>
    /// Where the room could not be measured every worker wanted runs, unchecked, saying why - with whatever reason
    /// nothing gave said as that.
    /// </summary>
    [Fact]
    public void AnUnmeasuredRoom_RunsEveryWorker_Unchecked()
    {
        Assert.Equal(
            WorkerPlan.Running(2, null, "the room for its workers could not be measured: the drive is gone"),
            Plan([100 * Gibibyte, 100 * Gibibyte], null, "the drive is gone", null));
        Assert.Equal(
            WorkerPlan.Running(1, null, "the room for its workers could not be measured: no reason was given"),
            Plan([100 * Gibibyte], null, null, null));
    }

    /// <summary>
    /// A sweep kept from some of the workers it wanted by the machine's path limit says how many it runs of those it
    /// wanted, and why the next is not run - beside what the room keeps out, where it keeps any out too; and where not
    /// even its first is within the limit it runs none, for that.
    /// </summary>
    [Theory]
    [InlineData(3, 100, 3, "3 of 4 workers: worker 4 is past the limit", null)]
    [InlineData(3, 10, 2, "2 of 4 workers: running 3 needs ~12 GiB, each worker's build as declared; 10 GiB free on '/srv'; and worker 4 is past the limit", null)]
    [InlineData(3, 3, 0, "its first worker needs ~4 GiB, each worker's build as declared; 3 GiB free on '/srv'; and worker 4 is past the limit", null)]
    [InlineData(3, null, 3, "3 of 4 workers: worker 4 is past the limit", "the room for its workers could not be measured: the drive is gone")]
    [InlineData(0, 100, 0, "worker 1 is past the limit", null)]
    [InlineData(0, null, 0, "worker 1 is past the limit", null)]
    public void WorkersPastThePathLimit_AreCountedAmongThoseWanted(int within, int? freeGiB, int count, string fewer, string? unmeasured)
    {
        var plan = WorkerRoom.Plan(
            [.. Enumerable.Repeat(4 * Gibibyte, within)],
            wanted: 4,
            beyond: $"worker {within + 1} is past the limit",
            freeGiB is { } free ? Room(free * Gibibyte) : null,
            "the drive is gone",
            "as declared");

        Assert.Equal((count, fewer, unmeasured), (plan.Count, plan.Fewer, plan.Unchecked));
        Assert.Equal(count == 0, plan.RunsNone);
    }

    /// <summary>
    /// A plan is made for a sweep that wants a worker, of no more workers than it wanted, with why those beyond the path
    /// limit are not run said where there are any and only there; and a plan that runs some runs at least one, where one
    /// that runs none says why.
    /// </summary>
    [Fact]
    public void APlan_IsMadeOnlyOfWhatASweepCanWant()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerRoom.Plan([], 0, null, Room(Gibibyte), null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerRoom.Plan([1, 1], 1, null, Room(Gibibyte), null, null));
        Assert.Throws<ArgumentException>(() => WorkerRoom.Plan([1], 2, null, Room(Gibibyte), null, null));
        Assert.Throws<ArgumentException>(() => WorkerRoom.Plan([1], 2, " ", Room(Gibibyte), null, null));
        Assert.Throws<ArgumentException>(() => WorkerRoom.Plan([1], 1, "worker 2 is past the limit", Room(Gibibyte), null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerPlan.Running(0));
        Assert.Throws<ArgumentException>(() => WorkerPlan.None(" "));
    }

    private static DiskSpace Room(long free) => new(free, 100 * Gibibyte, "/srv");

    /// <summary>The plan of a sweep that wants a worker for each of <paramref name="needs"/>, every one within the path limit.</summary>
    private static WorkerPlan Plan(long[] needs, DiskSpace? room, string? unmeasured, string? source)
        => WorkerRoom.Plan(needs, needs.Length, beyond: null, room, unmeasured, source);
}
