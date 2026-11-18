using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// The least room a heavy leg's build leaves free (<see cref="RoomFloor"/>), in each of its shapes, and the watch that
/// holds a build to it (<see cref="RoomFloorWatch"/>) once that build has gone past what fills a disk.
/// </summary>
public sealed class RoomFloorTests
{
    /// <summary>A floor of nothing stops no build, and is none: refused in every shape, never a floor that holds nothing.</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void AFloorOfNothing_IsRefused_InEveryShape(long bytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomFloor.Of("native", bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomFloor.Beside("native", bytes, "/mnt/c", ", where WSL keeps its disk"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomFloor.Unwatching("native", bytes, "the drive is mounted nowhere here"));
    }

    /// <summary>
    /// A floor for no leg - which no clean could name - beside no other filesystem, or unwatched for no reason, is no shape
    /// a floor has: each is refused.
    /// </summary>
    [Fact]
    public void AFloorOfNoLeg_BesideNoFilesystem_OrUnwatchedForNoReason_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => RoomFloor.Of(" ", 2L << 30));
        Assert.Throws<ArgumentException>(() => RoomFloor.Beside("wsl", 2L << 30, " ", ", where WSL keeps its disk"));
        Assert.Throws<ArgumentNullException>(() => RoomFloor.Beside("wsl", 2L << 30, "/mnt/c", null!));
        Assert.Throws<ArgumentException>(() => RoomFloor.Unwatching("wsl", 2L << 30, " "));
    }

    /// <summary>
    /// Each shape holds what it says and nothing else: the build directory's filesystem alone; one more beside it, named as
    /// what it is; or the build directory's alone, with why another the build fills is not watched.
    /// </summary>
    [Fact]
    public void EachShape_HoldsWhatItSays_AndNothingElse()
    {
        var alone = RoomFloor.Of("native", 2L << 30);
        var beside = RoomFloor.Beside("wsl", 3L << 30, "/mnt/c", ", where WSL keeps its disk");
        var unwatched = RoomFloor.Unwatching("wsl", 4L << 30, "the drive is mounted nowhere here");

        Assert.Equal(("native", 2L << 30, 0, (string?)null), (alone.Leg, alone.Bytes, alone.Also.Count, alone.Unwatched));
        Assert.Equal(("wsl", 3L << 30, (string?)null), (beside.Leg, beside.Bytes, beside.Unwatched));
        Assert.Equal([("/mnt/c", ", where WSL keeps its disk")], beside.Also);
        Assert.Equal(("wsl", 4L << 30, 0, "the drive is mounted nowhere here"), (unwatched.Leg, unwatched.Bytes, unwatched.Also.Count, unwatched.Unwatched));
    }

    /// <summary>
    /// A watch that has ended reads no room again and stops nothing, however little room is left: the build it watched has
    /// gone past what fills a disk, and asking it again - as a build ending does - neither stops that build nor fails on
    /// what its end let go. Ended twice, it ends once.
    /// </summary>
    [Fact]
    public async Task AWatchThatEnded_ReadsNoRoomAgain_AndStopsNothing()
    {
        using var temp = new TempDirectory();
        var room = new Room(new HarnessFactory().FileSystem) { Free = 100L << 30 };
        var said = new List<string>();
        var watch = RoomFloorWatch.Start(
            room,
            RoomFloor.Of("native", 2L << 30),
            temp.Path,
            (_, cancel) => Task.Delay(Timeout.Infinite, cancel),
            said.Add,
            TestContext.Current.CancellationToken);

        Assert.Null(watch.Now());

        await watch.DisposeAsync();
        room.Free = 1L << 30;
        var asked = room.Asked;

        Assert.Null(watch.Now());
        Assert.Null(watch.Why);
        Assert.False(watch.Stopping.IsCancellationRequested);
        Assert.Equal(asked, room.Asked);
        Assert.Empty(said);

        await watch.DisposeAsync();
    }

    /// <summary>
    /// A build under its floor, on a machine that makes room from other trees' idle builds, makes it first - told what it is
    /// short of beneath the floor, with its need as it was admitted beyond it - says what was done, and is stopped only
    /// where the room read again is still under: one made room for builds on; one that could not be is stopped as before.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABuildUnderItsFloor_MakesRoomFromIdleBuildsFirst_AndIsStoppedOnlyWhereItIsStillUnder(bool made)
    {
        using var temp = new TempDirectory();
        var room = new Room(new HarnessFactory().FileSystem) { Free = 1L << 30 };
        var said = new List<string>();
        var asked = new List<(string Path, long Free, long Shortfall)>();
        var saidMade = made
            ? "removed ~7 GiB of other trees' idle builds on '/data' to make room: worktree o1/xa's at '/src/xa/build/x86_64-gcc-debug', last built "
                + "2026-11-01 08:00 UTC; 1 GiB free before, 8 GiB after"
            : "no other tree's idle build on '/data' could be removed to make room; worktree o1/xa's build at '/src/xa/build' was left, as a run holds it: it";
        var floor = RoomFloor.Of("native", 2L << 30) with
        {
            Need = 3L << 30,
            MakeRoom = (path, read, shortfall) =>
            {
                asked.Add((path, read.FreeBytes, shortfall));

                if (made)
                {
                    room.Free = 8L << 30;
                }

                return new MadeRoom(made ? 7L << 30 : 0, saidMade);
            },
        };

        await using var watch = RoomFloorWatch.Start(room, floor, temp.Path, (_, cancel) => Task.Delay(Timeout.Infinite, cancel), said.Add, TestContext.Current.CancellationToken);

        Assert.Equal([(temp.Path, 1L << 30, 4L << 30)], asked);
        Assert.Equal([saidMade], said);
        Assert.Equal(
            made
                ? null
                : "stopped with 1 GiB free on '/data', under the 2 GiB admission.minFreeGiB keeps free for every leg there; what it built is left for "
                    + "'dssharness clean --legs native'",
            watch.Why);
        Assert.Equal(!made, watch.Stopping.IsCancellationRequested);
    }

    /// <summary>A file system whose every filesystem has <see cref="Free"/> bytes free, counting how often it was asked.</summary>
    private sealed class Room(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        private int _asked;

        /// <summary>The room free on every filesystem.</summary>
        public long Free { get; set; }

        /// <summary>How many times the room was read.</summary>
        public int Asked => Volatile.Read(ref _asked);

        public override DiskSpace SpaceAt(string path)
        {
            Interlocked.Increment(ref _asked);

            return new DiskSpace(Free, 200L << 30, "/data");
        }
    }
}
