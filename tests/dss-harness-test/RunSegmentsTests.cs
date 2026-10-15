using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;

namespace DssHarness.Tests;

/// <summary>A run that aborts mid-way is resumed, and the union across its segments is reported.</summary>
public sealed class RunSegmentsTests
{
    private const string RunId = "20260916-100000-0a1b2c3d";
    private const string Leg = "win-msvc-release";

    [Fact]
    public async Task ASecondInvocationSkipsWhatIsDone_AndReportsTheUnionAcrossSegments()
    {
        using var temp = new TempDirectory();
        var runs = Runs(temp);
        var segments = Create();
        var token = TestContext.Current.CancellationToken;

        // The first attempt finishes two of four units and is interrupted before it ends.
        await segments.BeginAsync(runs, RunId, Leg, "s1", At(0), token);
        await segments.RecordCompletedAsync(runs, RunId, Leg, "s1", "case-1", RunOutcome.Ok("passed"), At(1), token);
        await segments.RecordCompletedAsync(runs, RunId, Leg, "s1", "case-2", RunOutcome.Failed(20, "failed"), At(2), token);

        string[] units = ["case-1", "case-2", "case-3", "case-4"];

        var resumed = await segments.BeginAsync(runs, RunId, Leg, "s2", At(10), token);

        Assert.Equal(["case-3", "case-4"], resumed.Remaining(units));

        await segments.RecordCompletedAsync(runs, RunId, Leg, "s2", "case-3", RunOutcome.Ok("passed"), At(11), token);
        await segments.RecordCompletedAsync(runs, RunId, Leg, "s2", "case-4", RunOutcome.Ok("passed"), At(12), token);
        await segments.EndAsync(runs, RunId, Leg, "s2", At(13), token);

        var record = segments.Load(runs, RunId, Leg);

        Assert.Equal(2, record.Segments.Count);
        Assert.Null(record.Segments[0].EndedAt);
        Assert.Equal(At(13), record.Segments[1].EndedAt);

        Assert.Equal(units, record.Union.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(record.Remaining(units));

        // The union keeps what each segment measured, failures included: a resumed run reports the
        // whole suite, not only the part the last invocation happened to run.
        Assert.Equal(20, record.Union["case-2"].ResultCode);
        Assert.True(record.Union["case-4"].Success);
    }

    [Fact]
    public void Load_ReturnsAnEmptyRecord_WhenTheRunHasNoSegmentsYet()
    {
        using var temp = new TempDirectory();

        var record = Create().Load(Runs(temp), RunId, Leg);

        Assert.Equal(RunId, record.RunId);
        Assert.Empty(record.Segments);
        Assert.Equal(["case-1"], record.Remaining(["case-1"]));
    }

    [Fact]
    public void Load_Refuses_ARecordItCannotRead_RatherThanTreatingItAsEmpty()
    {
        // Read as empty it would silently repeat work already measured, and hide that the evidence
        // of the first attempt is damaged.
        using var temp = new TempDirectory();
        var runs = Runs(temp);

        Directory.CreateDirectory(runs);
        File.WriteAllText(RunSegments.RecordPath(runs, Leg), "{ this is not json");

        var exception = Assert.Throws<HarnessException>(() => Create().Load(runs, RunId, Leg));

        Assert.Equal(HarnessExit.ConfigInvalid, exception.ExitCode);
        Assert.Contains("could not be read", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BeginAsync_Refuses_ASegmentIdTheRunAlreadyHas()
    {
        using var temp = new TempDirectory();
        var runs = Runs(temp);
        var segments = Create();
        var token = TestContext.Current.CancellationToken;

        await segments.BeginAsync(runs, RunId, Leg, "s1", At(0), token);

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => segments.BeginAsync(runs, RunId, Leg, "s1", At(1), token));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("already has a segment 's1'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneRunsLegs_KeepSeparateRecords_SoNoLegSkipsAnothersWork()
    {
        // One run gives every leg the same run id, and every leg of a runner walks the same step
        // names. Sharing a record, the second leg finds each step already done, skips all of them,
        // and reports that it passed work another machine did — green on nothing at all.
        using var temp = new TempDirectory();
        var runs = Runs(temp);
        var segments = Create();
        var token = TestContext.Current.CancellationToken;

        await segments.BeginAsync(runs, RunId, "win-msvc-release", "s1", At(0), token);
        await segments.RecordCompletedAsync(runs, RunId, "win-msvc-release", "s1", "case-1", RunOutcome.Ok("passed"), At(1), token);

        var other = await segments.BeginAsync(runs, RunId, "linux-gcc-debug", "s1", At(2), token);

        Assert.Equal(["case-1"], other.Remaining(["case-1"]));
        Assert.Empty(other.Union);

        // And each leg's record is its own file, as its logs already are.
        Assert.NotEqual(
            RunSegments.RecordPath(runs, "win-msvc-release"),
            RunSegments.RecordPath(runs, "linux-gcc-debug"));
    }

    [Fact]
    public void RecordPath_SitsBesideTheRunsLogs()
    {
        using var temp = new TempDirectory();
        var runs = Runs(temp);

        PathAssert.Same(
            Path.Combine(runs, "segments-win-msvc-release.json"),
            RunSegments.RecordPath(runs, Leg));
    }

    private static DateTimeOffset At(int second)
        => new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero).AddSeconds(second);

    /// <summary>The run's own directory in <paramref name="temp"/>, as a layout places it.</summary>
    private static string Runs(TempDirectory temp) => new HarnessLayout(temp.Path, temp.Path).RunDirectory(RunId);

    private static RunSegments Create()
        => new(
            new PhysicalFileSystem(FilePermissionsFactory.Create()),
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));
}
