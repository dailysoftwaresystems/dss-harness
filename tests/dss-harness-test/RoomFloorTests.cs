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

        Assert.Null(watch.Now(watch.Mark));

        await watch.DisposeAsync();
        room.Free = 1L << 30;
        var asked = room.Asked;

        Assert.Null(watch.Now(0));
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
        var asked = new List<(string Filesystem, long Free, long Least, long Most)>();
        var saidMade = made
            ? "removed ~7 GiB of other trees' idle builds on '/data' to make room: worktree o1/xa's at '/src/xa/build/x86_64-gcc-debug', last built "
                + "2026-11-01 08:00 UTC; 1 GiB free before, 8 GiB after"
            : "no other tree's idle build on '/data' could be removed to make room; worktree o1/xa's build at '/src/xa/build' was left, as a run holds it: it";
        var floor = RoomFloor.Of("native", 2L << 30) with
        {
            Need = 3L << 30,
            MakeRoom = (read, least, most) =>
            {
                asked.Add((read.Filesystem, read.FreeBytes, least, most));

                if (made)
                {
                    room.Free = 8L << 30;
                }

                return new MadeRoom(made ? 7L << 30 : 0, saidMade);
            },
        };

        await using var watch = RoomFloorWatch.Start(room, floor, temp.Path, (_, cancel) => Task.Delay(Timeout.Infinite, cancel), said.Add, TestContext.Current.CancellationToken);

        Assert.Equal([("/data", 1L << 30, 1L << 30, 4L << 30)], asked);
        Assert.Equal([saidMade], said);
        Assert.Equal(
            made
                ? null
                : "stopped with 1 GiB free on '/data', under the 2 GiB admission.minFreeGiB keeps free for every leg there; what it built is left for "
                    + "'dssharness clean --legs native'",
            watch.Why);
        Assert.Equal(!made, watch.Stopping.IsCancellationRequested);
    }

    /// <summary>
    /// Judging whether a failure was for want of room never makes any: a build whose phase failed with its filesystem under
    /// the floor is stopped, though the machine makes room from idle builds - judged by a reading that made room first, the
    /// room it made answered that the build had wanted none, and its failure was read as its code's.
    /// </summary>
    [Fact]
    public async Task JudgingAFailure_NeverMakesRoom_AndAFailureUnderTheFloorIsStopped()
    {
        using var temp = new TempDirectory();
        var room = new Room(new HarnessFactory().FileSystem) { Free = 10L << 30 };
        var making = 0;
        var floor = RoomFloor.Of("native", 2L << 30) with
        {
            MakeRoom = (_, _, _) =>
            {
                making++;
                room.Free = 8L << 30;
                return new MadeRoom(7L << 30, "removed ~7 GiB of other trees' idle builds");
            },
        };

        await using var watch = RoomFloorWatch.Start(room, floor, temp.Path, (_, cancel) => Task.Delay(Timeout.Infinite, cancel), _ => { }, TestContext.Current.CancellationToken);
        var began = watch.Mark;
        room.Free = 1L << 30;

        Assert.Equal(
            "stopped with 1 GiB free on '/data', under the 2 GiB admission.minFreeGiB keeps free for every leg there; what it built is left for "
                + "'dssharness clean --legs native'",
            watch.Now(began));
        Assert.Equal(0, making);
        Assert.Equal(1L << 30, room.Free);
        Assert.True(watch.Stopping.IsCancellationRequested);
    }

    /// <summary>
    /// A failure of work that ran while the watch's own reading found its filesystem under the floor is stopped, though that
    /// reading made room there since and the room is back: the failure overlapped the want of it. Work begun after the room
    /// was made is judged by the room as it is, and the build itself is never stopped for a shortage room was made for.
    /// </summary>
    [Fact]
    public async Task AFailureBesideAReadingUnderTheFloor_IsStopped_ThoughRoomWasMadeSince()
    {
        using var temp = new TempDirectory();
        var room = new Room(new HarnessFactory().FileSystem) { Free = 10L << 30 };
        var ticks = new Ticks();
        var making = 0;
        var floor = RoomFloor.Of("native", 2L << 30) with
        {
            MakeRoom = (_, _, _) =>
            {
                making++;
                room.Free = 8L << 30;
                return new MadeRoom(7L << 30, "removed ~7 GiB of other trees' idle builds");
            },
        };
        var said = new List<string>();

        await using var watch = RoomFloorWatch.Start(room, floor, temp.Path, ticks.WaitAsync, said.Add, TestContext.Current.CancellationToken);
        var began = watch.Mark;

        await ticks.Waiting(1);
        room.Free = 1L << 30;
        ticks.Go();
        await ticks.Waiting(2);
        var after = watch.Mark;

        Assert.Equal(
            "stopped: it failed while '/data' stood under the 2 GiB admission.minFreeGiB keeps free for every leg there, at 1 GiB free as it "
                + "was read, so the failure says nothing of its code; what it built is left for 'dssharness clean --legs native'",
            watch.Now(began));
        Assert.Null(watch.Now(after));
        Assert.Equal(1, making);
        Assert.Null(watch.Why);
        Assert.False(watch.Stopping.IsCancellationRequested);
        Assert.Equal(["removed ~7 GiB of other trees' idle builds"], said);
    }

    /// <summary>
    /// A build under its floor while another leg makes room on that filesystem is not stopped for that reading: it removes
    /// nothing beside the other, says so once, and is read again at the next reading, which judges it by the room then.
    /// </summary>
    [Fact]
    public async Task ABuildUnderItsFloor_BehindAnotherLegMakingRoom_IsNotStopped_AndIsReadAgain()
    {
        using var temp = new TempDirectory();
        var room = new Room(new HarnessFactory().FileSystem) { Free = 1L << 30 };
        var ticks = new Ticks();
        const string Behind = "leg 'native' of worktree o1/xa (pid 12) is making room on '/data' from other trees' idle builds";
        var floor = RoomFloor.Of("native", 2L << 30) with
        {
            MakeRoom = (_, _, _) => new MadeRoom(0, Behind) { Behind = true },
        };
        var said = new List<string>();

        await using var watch = RoomFloorWatch.Start(room, floor, temp.Path, ticks.WaitAsync, said.Add, TestContext.Current.CancellationToken);

        Assert.Null(watch.Why);
        Assert.False(watch.Stopping.IsCancellationRequested);

        await ticks.Waiting(1);
        ticks.Go();
        await ticks.Waiting(2);

        Assert.Null(watch.Why);
        Assert.Equal([Behind], said);
    }

    /// <summary>
    /// A floor of no ceiling may stand above the whole of a filesystem: a build there is stopped saying so - the floor, and
    /// the size of the whole filesystem it is more than - and what to do, for no build there could ever leave it free.
    /// </summary>
    [Fact]
    public async Task AFloorAboveTheWholeFilesystem_IsSaid_WhereTheBuildIsStopped()
    {
        using var temp = new TempDirectory();
        var room = new Room(new HarnessFactory().FileSystem) { Free = 100L << 30 };

        await using var watch = RoomFloorWatch.Start(
            room,
            RoomFloor.Of("native", 2048L << 30),
            temp.Path,
            (_, cancel) => Task.Delay(Timeout.Infinite, cancel),
            _ => { },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "stopped with 100 GiB free on '/data', under the 2 TiB admission.minFreeGiB keeps free for every leg there, which is more than the "
                + "whole of '/data' holds, 200 GiB: lower admission.minFreeGiB, or no build there runs; what it built is left for "
                + "'dssharness clean --legs native'",
            watch.Why);
        Assert.True(watch.Stopping.IsCancellationRequested);
    }

    /// <summary>
    /// The watch's waits between readings, each let go by the test: <see cref="Waiting"/> ends once the given wait has begun,
    /// which is once the reading before it has ended.
    /// </summary>
    private sealed class Ticks
    {
        private readonly SemaphoreSlim _go = new(0);
        private readonly Lock _counting = new();
        private readonly List<TaskCompletionSource> _begun = [];
        private int _count;

        public async Task WaitAsync(TimeSpan every, CancellationToken cancellationToken)
        {
            lock (_counting)
            {
                _count++;
                Begun(_count).TrySetResult();
            }

            await _go.WaitAsync(cancellationToken);
        }

        public Task Waiting(int wait)
        {
            lock (_counting)
            {
                return Begun(wait).Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
        }

        public void Go() => _go.Release();

        private TaskCompletionSource Begun(int wait)
        {
            while (_begun.Count < wait)
            {
                _begun.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return _begun[wait - 1];
        }
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
