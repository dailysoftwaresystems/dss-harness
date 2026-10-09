using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Tests;

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
