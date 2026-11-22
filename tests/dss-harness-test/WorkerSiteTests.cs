using DssHarness.Core.Build;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Mutations;

namespace DssHarness.Tests;

/// <summary>
/// The files of a worker's copy an arm mutates: each written dated past everything the worker's last build left and past
/// the clock, so the build that follows sees it changed, and the build waiting until the clock is past that date, so
/// nothing it writes is dated before the file - however the clock stands against the last build's newest file.
/// </summary>
public sealed class WorkerSiteTests
{
    /// <summary>
    /// A site is dated past the clock, by the granularity its build directory's file system dates files at, where the
    /// worker's last build left nothing newer: no record, a record naming no newest file, or one whose newest file is
    /// older than now.
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

        var site = new WorkerSite(harness.FileSystem, clock);

        Assert.Equal(now + site.Granularity(build), site.Stamp(build));
    }

    /// <summary>
    /// A site is dated past the newest file the worker's last build left where that is ahead of the clock, as a clock
    /// stepped back since the build leaves it, or a WSL distribution's clock ahead of this machine's - however far ahead,
    /// since the build waits it out.
    /// </summary>
    [Theory]
    [InlineData(30.0)]
    [InlineData(61.0)]
    [InlineData(7200.0)]
    public void ASite_IsDatedPastTheLastBuildsNewestFile_WhereThatIsAheadOfTheClock_HoweverFar(double ahead)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new JumpingClock();
        var now = clock.GetUtcNow().UtcDateTime;
        var build = temp.Combine("build");
        var newest = now.AddSeconds(ahead);
        var site = new WorkerSite(harness.FileSystem, clock);

        Record(build, newest);

        Assert.Equal(newest + site.Granularity(build), site.Stamp(build));
    }

    /// <summary>
    /// The granularity a site is dated past by is the file system's own, measured where the build is: no time the file
    /// system can tell apart from the one before it - a tick where it keeps ticks, a second or two where it dates coarsely
    /// - and measured once for each build directory, on a file it removes at once.
    /// </summary>
    [Fact]
    public void TheGranularity_IsTheFileSystemsOwn_MeasuredOnceWhereTheBuildIs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var build = temp.Combine("build");
        var site = new WorkerSite(harness.FileSystem, new JumpingClock());
        var granularity = site.Granularity(build);
        var probe = temp.Combine("probe");

        Assert.InRange(granularity, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(2) + TimeSpan.FromTicks(1));
        Assert.Equal(granularity, site.Granularity(build));
        Assert.Empty(Directory.EnumerateFileSystemEntries(build));

        // Two times a granularity apart are kept apart there.
        File.WriteAllText(probe, string.Empty);
        File.SetLastWriteTimeUtc(probe, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc));
        var first = File.GetLastWriteTimeUtc(probe);
        File.SetLastWriteTimeUtc(probe, first + granularity);
        Assert.True(File.GetLastWriteTimeUtc(probe) > first);
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

        await site.WriteAsync(temp.Path, path, "return c < b;\n"u8.ToArray(), stamp, TestContext.Current.CancellationToken);

        Assert.Equal("return c < b;\n"u8.ToArray(), site.Read(temp.Path, path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Null(site.Read(temp.Path, temp.Combine("src", "gone.cpp")));
    }

    /// <summary>
    /// A site the worker's own builds turned into a link, or put a linked directory on the way to, is neither read nor
    /// written - each refused naming the link - and what the link leads to, a file of the user's, is left as it was: the
    /// worker is a place its builds and runs write, and the tool reads and writes it outside every isolation.
    /// </summary>
    [Fact]
    public async Task ASiteReachedThroughALink_IsNeitherReadNorWritten_AndWhatTheLinkLeadsToIsUntouched()
    {
        using var worker = new TempDirectory();
        using var outside = new TempDirectory();
        var site = new WorkerSite(new HarnessFactory().FileSystem, new JumpingClock());
        var users = outside.WriteFile("fixture.cpp", "the user's\n");
        var linked = worker.Combine("src", "fixture.cpp");
        var through = worker.Combine("lib", "fixture.cpp");
        Directory.CreateDirectory(worker.Combine("src"));

        if (!UnfollowedFilesTests.TryLink(linked, users, file: true) || !UnfollowedFilesTests.TryLink(worker.Combine("lib"), outside.Path))
        {
            Assert.Skip("This machine does not allow symbolic links to be made.");
        }

        var stamp = new DateTime(2031, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        var read = Assert.Throws<LinkOnTheWayException>(() => site.Read(worker.Path, linked));
        var readThrough = Assert.Throws<LinkOnTheWayException>(() => site.Read(worker.Path, through));
        var written = await Assert.ThrowsAsync<LinkOnTheWayException>(() => site.WriteAsync(worker.Path, through, "planted\n"u8.ToArray(), stamp, TestContext.Current.CancellationToken));

        Assert.Equal($"'{linked}' is reached through the link '{linked}', which the tool never follows in a place an isolated process writes", read.Message);
        Assert.Equal($"'{through}' is reached through the link '{worker.Combine("lib")}', which the tool never follows in a place an isolated process writes", readThrough.Message);
        Assert.Equal(readThrough.Message, written.Message);
        Assert.Equal("the user's\n", File.ReadAllText(users));
        Assert.NotEqual(stamp, File.GetLastWriteTimeUtc(users));
    }

    /// <summary>
    /// The wait for the clock to pass a stamp ends only once the clock says it is past it, however that takes, and a
    /// moment after: no wait is longer than what is left.
    /// </summary>
    [Fact]
    public async Task TheWait_EndsOnceTheClockIsPastTheStamp()
    {
        var clock = new JumpingClock();
        var site = new WorkerSite(new HarnessFactory().FileSystem, clock);
        var stamp = clock.GetUtcNow().UtcDateTime + TimeSpan.FromSeconds(42);

        await site.UntilPastAsync(stamp, _ => { }, TestContext.Current.CancellationToken);

        Assert.True(clock.GetUtcNow().UtcDateTime > stamp, $"the wait ended at {clock.GetUtcNow():O}, not past {stamp:O}");
        Assert.True(clock.GetUtcNow().UtcDateTime <= stamp + TimeSpan.FromSeconds(1), $"the wait ended at {clock.GetUtcNow():O}, long past {stamp:O}");
        Assert.All(clock.Waits, wait => Assert.True(wait <= TimeSpan.FromSeconds(42) + TimeSpan.FromTicks(1), $"one wait was {wait}"));
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

        await site.UntilPastAsync(stamp, _ => { }, TestContext.Current.CancellationToken);

        Assert.True(clock.GetUtcNow().UtcDateTime > stamp, $"the wait ended at {clock.GetUtcNow():O}, not past {stamp:O}");
    }

    /// <summary>A stamp the clock is past already is not waited for at all.</summary>
    [Fact]
    public async Task AStampTheClockIsPast_IsNotWaitedFor()
    {
        var clock = new JumpingClock();
        var site = new WorkerSite(new HarnessFactory().FileSystem, clock);

        var told = new List<string>();

        await site.UntilPastAsync(clock.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(1), wait => told.Add(wait.Line()), TestContext.Current.CancellationToken);

        Assert.Empty(clock.Waits);
        Assert.Empty(told);
    }

    /// <summary>
    /// A clock behind the stamp - a WSL distribution's clock three minutes ahead of this machine's left the build's files
    /// dated there - is waited for until it is past it, however far behind, and told: as the wait begins, how far behind
    /// this machine's clock is, and every five minutes while a longer one lasts. Red with the minute's longest wait put back.
    /// </summary>
    [Fact]
    public async Task AClockBehindTheStamp_IsWaitedAndTold()
    {
        var clock = new JumpingClock();
        var site = new WorkerSite(new HarnessFactory().FileSystem, clock);
        var told = new List<string>();
        var stamp = clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(3);
        var subject = $"the clock to pass {stamp.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)}Z, which its site was dated by";

        await site.UntilPastAsync(stamp, wait => told.Add(wait.Line()), TestContext.Current.CancellationToken);

        Assert.True(clock.GetUtcNow().UtcDateTime > stamp, $"the wait ended at {clock.GetUtcNow():O}, not past {stamp:O}");
        Assert.Equal([$"waits for {subject}: this machine's clock is 3m00s behind it"], told);

        // Twelve minutes behind: told as it begins, and at five and ten minutes.
        told.Clear();
        var further = clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(12);

        await site.UntilPastAsync(further, wait => told.Add(wait.Line()), TestContext.Current.CancellationToken);

        Assert.True(clock.GetUtcNow().UtcDateTime > further);
        Assert.Equal(3, told.Count);
        Assert.EndsWith(": this machine's clock is 12m00s behind it", told[0], StringComparison.Ordinal);
        Assert.StartsWith("has waited 5m00s for the clock to pass ", told[1], StringComparison.Ordinal);
        Assert.StartsWith("has waited 10m00s for the clock to pass ", told[2], StringComparison.Ordinal);
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

    /// <summary>
    /// A clock whose timers fire early: each wait it is given ends once half of it has passed, rounded up to a whole tick,
    /// since no timer ends before any time has passed.
    /// </summary>
    private sealed class EarlyClock : ManualClock
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);

            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                Advance(TimeSpan.FromTicks((dueTime.Ticks + 1) / 2));
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
