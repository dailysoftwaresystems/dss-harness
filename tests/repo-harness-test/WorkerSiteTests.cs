using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// The files of a worker's copy an arm mutates: each written dated past everything the worker's last build left and past
/// the clock, so the build that follows sees it changed, and the build waiting until the clock is past that date, so
/// nothing it writes is dated before the file - however the clock stands against the last build's newest file.
/// </summary>
public sealed class WorkerSiteTests
{
    /// <summary>
    /// A site is dated two seconds past the clock where the worker's last build left nothing newer: no record, a record
    /// naming no newest file, or one whose newest file is older than now.
    /// </summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, -90.0)]
    public void ASite_IsDatedPastTheClock_WhereTheLastBuildLeftNothingNewer(bool recorded, double? newestFromNow)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new JumpingClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var build = temp.Combine("build");

        if (recorded)
        {
            Record(build, newestFromNow is { } seconds ? now.AddSeconds(seconds) : null);
        }

        Assert.Equal(now + WorkerSite.Margin, new WorkerSite(harness.FileSystem, clock).Stamp(build));
    }

    /// <summary>
    /// A site is dated past the newest file the worker's last build left where that is ahead of the clock, as a clock
    /// stepped back since the build leaves it - unless it is more than a minute ahead, which no build waits out: it is
    /// dated by the clock, and the build's own rule answers what that leaves.
    /// </summary>
    [Theory]
    [InlineData(30.0, true)]
    [InlineData(60.0, true)]
    [InlineData(61.0, false)]
    [InlineData(7200.0, false)]
    public void ASite_IsDatedPastTheLastBuildsNewestFile_WhereThatIsAheadOfTheClock_ByAMinuteAtMost(double ahead, bool fromNewest)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new JumpingClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var build = temp.Combine("build");
        var newest = now.AddSeconds(ahead);

        Record(build, newest);

        Assert.Equal((fromNewest ? newest : now) + WorkerSite.Margin, new WorkerSite(harness.FileSystem, clock).Stamp(build));
    }

    /// <summary>A site is written whole and dated as it was stamped, and read back as the bytes it holds; a site that is no file reads as none.</summary>
    [Fact]
    public async Task ASite_IsWrittenDatedAsStamped_AndReadAsItsBytes()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var site = new WorkerSite(harness.FileSystem, new JumpingClock());
        var path = temp.WriteFile(Path.Combine("src", "fixture.cpp"), "return c <= b;\n");
        var stamp = new DateTime(2031, 5, 4, 3, 2, 1, DateTimeKind.Utc);

        await site.WriteAsync(path, "return c < b;\n"u8.ToArray(), stamp, TestContext.Current.CancellationToken);

        Assert.Equal("return c < b;\n"u8.ToArray(), site.Read(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Null(site.Read(temp.Combine("src", "gone.cpp")));
    }

    /// <summary>The wait for the clock to pass a stamp ends only once the clock says it is past it, however that takes.</summary>
    [Fact]
    public async Task TheWait_EndsOnceTheClockIsPastTheStamp()
    {
        var clock = new JumpingClock();
        var site = new WorkerSite(new HarnessFactory().FileSystem, clock);
        var stamp = clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(42);

        await site.UntilPastAsync(stamp, TestContext.Current.CancellationToken);

        Assert.True(clock.GetUtcNow().UtcDateTime > stamp, $"the wait ended at {clock.GetUtcNow():O}, not past {stamp:O}");
        Assert.All(clock.Waits, wait => Assert.True(wait <= WorkerSite.LongestWait + TimeSpan.FromSeconds(1), $"one wait was {wait}"));
    }

    /// <summary>
    /// A wait that ends early - a timer firing before the clock reaches its time - is waited again, for what is left, until
    /// the clock is past the stamp: the clock decides, never the wait.
    /// </summary>
    [Fact]
    public async Task AWaitThatEndsEarly_IsWaitedAgain_UntilTheClockIsPastTheStamp()
    {
        var clock = new EarlyClock();
        var site = new WorkerSite(new HarnessFactory().FileSystem, clock);
        var stamp = clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(42);

        await site.UntilPastAsync(stamp, TestContext.Current.CancellationToken);

        Assert.True(clock.GetUtcNow().UtcDateTime > stamp, $"the wait ended at {clock.GetUtcNow():O}, not past {stamp:O}");
    }

    /// <summary>A stamp the clock is past already is not waited for at all.</summary>
    [Fact]
    public async Task AStampTheClockIsPast_IsNotWaitedFor()
    {
        var clock = new JumpingClock();
        var site = new WorkerSite(new HarnessFactory().FileSystem, clock);

        await site.UntilPastAsync(clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.Empty(clock.Waits);
    }

    /// <summary>
    /// A clock that never reaches the stamp - stepped back while the build waited - is waited for no longer than a stamp
    /// is ever ahead of it: the build that follows is the one to answer for the step.
    /// </summary>
    [Fact]
    public async Task AClockThatNeverReachesTheStamp_IsWaitedForNoLongerThanAStampIsEverAhead()
    {
        var clock = new StoppedWallClock();
        var site = new WorkerSite(new HarnessFactory().FileSystem, clock);

        await site.UntilPastAsync(clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(30), TestContext.Current.CancellationToken);

        Assert.InRange(clock.Moved, WorkerSite.LongestWait + WorkerSite.Margin, WorkerSite.LongestWait + WorkerSite.Margin + WorkerSite.LongestWait + TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// When the newest file a build left was written, as it recorded it: nothing where no build recorded one, or the record
    /// cannot be read as one.
    /// </summary>
    [Fact]
    public void TheNewestFileABuildLeft_IsReadFromItsRecord()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var written = new DateTime(2026, 9, 23, 10, 0, 5, DateTimeKind.Utc);

        Assert.Null(BuildRecord.NewestIn(harness.FileSystem, temp.Combine("none")));

        Record(temp.Combine("unfinished"), null);
        Record(temp.Combine("finished"), written);
        temp.WriteFile(Path.Combine("garbled", BuildRecord.FileName), "newest soon\n");

        Assert.Null(BuildRecord.NewestIn(harness.FileSystem, temp.Combine("unfinished")));
        Assert.Equal(written, BuildRecord.NewestIn(harness.FileSystem, temp.Combine("finished")));
        Assert.Null(BuildRecord.NewestIn(harness.FileSystem, temp.Combine("garbled")));
    }

    /// <summary>Writes a build record into <paramref name="build"/> naming <paramref name="newest"/> as the newest file it left, or none.</summary>
    private static void Record(string build, DateTime? newest)
    {
        Directory.CreateDirectory(build);
        File.WriteAllText(
            Path.Combine(build, BuildRecord.FileName),
            new BuildRecord(
                null,
                "x86_64-gcc-debug",
                newest is { } at ? new WrittenFile("bin/fixture", at) : null,
                new Dictionary<string, string>(),
                new Dictionary<string, DateTime>()).Write());
    }

    /// <summary>A clock whose time of day stands still while time itself passes, as one stepped back as fast as it goes.</summary>
    private sealed class StoppedWallClock : ManualClock
    {
        private readonly DateTimeOffset _wall = new(2026, 9, 30, 16, 32, 14, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _wall;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);

            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Advance(dueTime);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new Inert();
        }
    }

    /// <summary>A clock whose timers fire early: each wait it is given ends once half of it has passed.</summary>
    private sealed class EarlyClock : ManualClock
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);

            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Advance(dueTime / 2);
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }

            return new Inert();
        }
    }

    /// <summary>A timer that has fired, and does nothing more.</summary>
    private sealed class Inert : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
