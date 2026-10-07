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
        => Assert.Equal(new WorkerPlan(2, 2, null, null), WorkerRoom.Plan([6 * Gibibyte, 4 * Gibibyte], Room(10 * Gibibyte), null, null));

    /// <summary>
    /// Workers are counted in order, each beside the ones before it: a sweep runs those that fit, and says it runs fewer,
    /// with what is free and what one more would need - never skipping a worker that does not fit for a later one that would.
    /// </summary>
    [Fact]
    public void FewerWorkersRun_WhereFewerFit_SayingSo()
    {
        var plan = WorkerRoom.Plan([6 * Gibibyte, 5 * Gibibyte, 0], Room(10 * Gibibyte), null, "as its buildSpaceGiB, 4, declares");

        Assert.Equal(
            new WorkerPlan(1, 3, "1 of 3 workers: 10 GiB free on '/srv', and 2 need ~11 GiB, each worker's build as its buildSpaceGiB, 4, declares", null),
            plan);
    }

    /// <summary>A sweep whose first worker does not fit runs none, saying what is free and what that worker needs.</summary>
    [Fact]
    public void NoWorkerRuns_WhereTheFirstDoesNotFit()
    {
        Assert.Equal(
            new WorkerPlan(0, 2, "10 GiB free on '/srv', and its first worker needs ~12 GiB, each worker's build what its last build there came to", null),
            WorkerRoom.Plan([12 * Gibibyte, 0], Room(10 * Gibibyte), null, "what its last build there came to"));
        Assert.Equal(
            new WorkerPlan(0, 1, "1 GiB free on '/srv', and its first worker needs ~3 GiB", null),
            WorkerRoom.Plan([3 * Gibibyte], Room(Gibibyte), null, null));
    }

    /// <summary>Where the room could not be measured every worker wanted runs, unchecked, saying why; and a sweep wanting none plans none.</summary>
    [Fact]
    public void AnUnmeasuredRoom_RunsEveryWorker_Unchecked()
    {
        Assert.Equal(
            new WorkerPlan(2, 2, null, "the room for its workers could not be measured: the drive is gone"),
            WorkerRoom.Plan([100 * Gibibyte, 100 * Gibibyte], null, "the drive is gone", null));
        Assert.Equal(new WorkerPlan(0, 0, null, null), WorkerRoom.Plan([], null, "the drive is gone", null));
    }

    private static DiskSpace Room(long free) => new(free, 100 * Gibibyte, "/srv");
}
