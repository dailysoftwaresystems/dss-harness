using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// One whole run of a test binary in a worker, against a child whose output, report and timing this test controls: told
/// where to write its report, which is read once it ends - never one an earlier run left - stopped past its bound or as
/// hung, each said as what it was, and its diagnostic read from what it printed, a line at a time.
/// </summary>
public sealed class ArmTestRunnerTests
{
    /// <summary>A report of two cases, one red.</summary>
    private const string Report = "<testsuites><testsuite name=\"Fixture\"><testcase classname=\"Fixture\" name=\"Charge\"><failure message=\"red\"/></testcase>"
        + "<testcase classname=\"Fixture\" name=\"Depth\"/></testsuite></testsuites>";

    /// <summary>
    /// The run is told where to write its report, among the arm's records, which is read once it ends, with what it
    /// exited; its output is kept beside the report.
    /// </summary>
    [Fact]
    public async Task ARun_WritesItsReportWhereItIsTold_AndItIsReadWithItsExit()
    {
        using var temp = new TempDirectory();
        var records = temp.Combine("arms", "charge-bound");

        var result = await RunAsync(Child("write-file", records, "{report}", Report));

        Assert.Equal(0, result.Run.ExitCode);
        Assert.True(result.Run.ReportWritten);
        Assert.Equal(["Fixture.Charge"], result.Run.Report?.Reds);
        Assert.Equal(2, result.Run.Report?.Ran);
        Assert.Equal(Path.Combine(records, ArmTestRunner.ReportFileName), result.ReportFile);
        Assert.Equal(Path.Combine(records, ArmTestRunner.PhaseName + ".log"), result.LogFile);
        Assert.True(File.Exists(result.LogFile));
        Assert.False(result.Run.StoppedAtBound);
        Assert.Null(result.Run.StalledAfterSeconds);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    /// <summary>A report an earlier run left is removed before the run starts: a run that writes none is never judged by it.</summary>
    [Fact]
    public async Task AReportAnEarlierRunLeft_IsRemovedBeforeTheRun()
    {
        using var temp = new TempDirectory();
        var records = temp.Combine("arms", "charge-bound");
        var left = temp.WriteFile(Path.Combine("arms", "charge-bound", ArmTestRunner.ReportFileName), Report);

        var result = await RunAsync(Child("exit", records, "3"));

        Assert.Equal(3, result.Run.ExitCode);
        Assert.False(result.Run.ReportWritten);
        Assert.Null(result.Run.Report);
        Assert.False(File.Exists(left));
    }

    /// <summary>A report written that is no report is said as written, and read as none, with why it is none.</summary>
    [Fact]
    public async Task AReportThatIsNoReport_IsWrittenAndNone_AndWhyIsSaid()
    {
        using var temp = new TempDirectory();

        var result = await RunAsync(Child("write-file", temp.Combine("arms", "charge-bound"), "{report}", "Segmentation fault"));

        Assert.True(result.Run.ReportWritten);
        Assert.Null(result.Run.Report);
        Assert.StartsWith("it is no XML: ", result.Run.ReportProblem, StringComparison.Ordinal);
        Assert.Null(result.Run.ReportUnread);
    }

    /// <summary>
    /// A report that is there and cannot be read - another process still holds it as the run ends - is read again, a
    /// few times, <see cref="ArmTestRunner.ReadRetry"/> apart: one let go meanwhile is read as it is, and one held
    /// throughout is said as written and unread, with why, and never as no report, which would be a finding about the
    /// binary.
    /// </summary>
    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(int.MaxValue, false)]
    [InlineData(int.MaxValue, true)]
    public async Task AReportThatCannotBeReadFromItsFile_IsReadAgain_AndSaidAsUnread_NeverAsNoReport(int heldFor, bool denied)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var held = new HeldReport(harness.FileSystem, heldFor, denied);
        var clock = new JumpingClock();

        var result = await RunAsync(Child("write-file", temp.Combine("arms", "charge-bound"), "{report}", Report), fileSystem: held, clock: clock);

        Assert.True(result.Run.ReportWritten);
        Assert.Null(result.Run.ReportProblem);

        // Waited between two reads, each time as long: what holds a report lets go within moments, never at once.
        Assert.Equal(Enumerable.Repeat(ArmTestRunner.ReadRetry, Math.Min(heldFor, ArmTestRunner.ReadAttempts - 1)), clock.Waits);

        if (heldFor == int.MaxValue)
        {
            Assert.Null(result.Run.Report);
            Assert.Equal("'report.xml' is held by another process", result.Run.ReportUnread);
            Assert.Equal(ArmTestRunner.ReadAttempts, held.Reads);
        }
        else
        {
            Assert.Equal(["Fixture.Charge"], result.Run.Report?.Reds);
            Assert.Null(result.Run.ReportUnread);
            Assert.Equal(heldFor + 1, held.Reads);
        }
    }

    /// <summary>
    /// A run starts where it is told, and finds programs in the directories it is given beside its PATH: where the
    /// leg's tests start, and with the compiler's own libraries the leg's host found - without which a binary a
    /// compiler outside the PATH built does not start at all.
    /// </summary>
    [Fact]
    public async Task ARun_StartsWhereItIsTold_AndFindsProgramsInTheDirectoriesItIsGiven()
    {
        using var temp = new TempDirectory();
        var where = Directory.CreateDirectory(temp.Combine("where it starts")).FullName;
        var given = temp.Combine("toolchain", "bin");

        var started = await RunAsync(Child("write-file", temp.Combine("arms", "starts"), Path.Combine("made", "here.txt"), "here") with { WorkingDirectory = where });

        Assert.Equal(0, started.Run.ExitCode);
        Assert.True(File.Exists(Path.Combine(where, "made", "here.txt")), "the run did not start where it was told");

        var printed = await RunAsync(Child("print-env", temp.Combine("arms", "path"), "PATH") with { AppendToPath = [given] });

        Assert.Equal(0, printed.Run.ExitCode);
        Assert.Contains(given, await File.ReadAllTextAsync(printed.LogFile, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    /// <summary>
    /// A run whose log no longer holds what it printed, once it has ended, is not read for its diagnostic as one that
    /// printed nothing - which would be a mutation whose red never said why: it is said as unread, which its arm ends
    /// unmeasured for.
    /// </summary>
    [Fact]
    public async Task ARunWhoseOutputCouldNotBeReadBack_IsSaidAsUnread_NeverAsOneThatDidNotSayItsDiagnostic()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var records = temp.Combine("arms", "charge");
        var writtenAgain = new LogWrittenAgain(harness.FileSystem, Path.Combine(records, ArmTestRunner.PhaseName + ".log"));

        var unread = await Assert.ThrowsAsync<PhaseOutputUnreadException>(
            () => RunAsync(Child("echo-args", records, "the charge exceeded its bound") with { Diagnostic = "charge exceeded" }, fileSystem: writtenAgain));

        Assert.Contains("it was written again since", unread.Message, StringComparison.Ordinal);
    }

    /// <summary>A run past its bound is stopped and said as one, with the bound and the factor it had.</summary>
    [Fact]
    public async Task ARunPastItsBound_IsStopped_AndSaidAsOne()
    {
        using var temp = new TempDirectory();

        var result = await RunAsync(Child("sleep", temp.Combine("arms", "loop"), "120000") with { Bound = TimeSpan.FromSeconds(1), Factor = 10 });

        Assert.True(result.Run.StoppedAtBound);
        Assert.Equal((TimeSpan.FromSeconds(1), 10.0), (result.Run.Bound, result.Run.Factor));
        Assert.Null(result.Run.ExitCode);
        Assert.False(result.Run.ReportWritten);
        Assert.True(result.Duration < TimeSpan.FromSeconds(60), $"the run took {result.Duration}");
    }

    /// <summary>A run that prints nothing for <c>defaults.stallSeconds</c> is stopped as hung, as every phase is, and said as that.</summary>
    [Fact]
    public async Task ARunSilentForTheStallBound_IsStoppedAsHung()
    {
        using var temp = new TempDirectory();

        var result = await RunAsync(Child("sleep", temp.Combine("arms", "silent"), "120000") with { StallSeconds = 1, Bound = TimeSpan.FromMinutes(10) });

        Assert.Equal(1, result.Run.StalledAfterSeconds);
        Assert.False(result.Run.StoppedAtBound);
        Assert.Null(result.Run.ExitCode);
        Assert.Equal(TimeSpan.FromMinutes(10), result.Run.Bound);
    }

    /// <summary>
    /// The diagnostic is read from what the run printed, a line at a time: found within a line, or across two, and not
    /// found where it was not said or nothing was watched for.
    /// </summary>
    [Theory]
    [InlineData("charge exceeded", true)]
    [InlineData("compiling]\n[the charge", true)]
    [InlineData("compiling]\r\n[the charge", true)]
    [InlineData("depth exceeded", false)]
    [InlineData(null, false)]
    public async Task TheDiagnostic_IsReadFromWhatTheRunPrinted(string? diagnostic, bool said)
    {
        using var temp = new TempDirectory();

        var result = await RunAsync(Child("echo-args", temp.Combine("arms", "charge"), "compiling", "the charge exceeded its bound") with { Diagnostic = diagnostic });

        Assert.Equal(said, result.Run.DiagnosticSaid);
    }

    /// <summary>
    /// A run is timed on both clocks, and says how far they disagreed where that is past the tolerance it was given -
    /// the clock stepped, forward or back, or the host slept, while it ran - however it ended: at its own exit, stopped
    /// as hung, or stopped at its bound, where no phase is left to say it.
    /// </summary>
    [Theory]
    [InlineData("exit", 90)]
    [InlineData("exit", -90)]
    [InlineData("stall", 90)]
    [InlineData("bound", 90)]
    public async Task ARunThatSpannedAClockStep_SaysByHowMuch_HoweverItEnded(string ended, int steppedSeconds)
    {
        using var temp = new TempDirectory();
        var records = temp.Combine("arms", "charge-bound");
        var request = ended switch
        {
            "stall" => Child("sleep", records, "120000") with { StallSeconds = 1 },
            "bound" => Child("sleep", records, "120000") with { Bound = TimeSpan.FromSeconds(1), Factor = 10 },
            _ => Child("exit", records, "0"),
        };

        var result = await RunAsync(request with { ClockStepToleranceMilliseconds = 2000 }, clock: new SteppedOnce(TimeSpan.FromSeconds(steppedSeconds)));

        Assert.Equal(ended == "bound", result.Run.StoppedAtBound);
        Assert.Equal(ended == "stall" ? 1 : (int?)null, result.Run.StalledAfterSeconds);
        Assert.NotNull(result.SteppedBy);
        Assert.InRange(result.SteppedBy.Value, TimeSpan.FromSeconds(89), TimeSpan.FromSeconds(91));
    }

    /// <summary>
    /// A run whose clocks agree within its tolerance says no step, and neither does one given no tolerance, which
    /// watches for none.
    /// </summary>
    [Theory]
    [InlineData(0, 2000)]
    [InlineData(1, 2000)]
    [InlineData(90, 0)]
    public async Task ARunWithinItsTolerance_OrGivenNone_SaysNoStep(int steppedSeconds, int tolerance)
    {
        using var temp = new TempDirectory();

        var result = await RunAsync(
            Child("exit", temp.Combine("arms", "charge-bound"), "0") with { ClockStepToleranceMilliseconds = tolerance },
            clock: new SteppedOnce(TimeSpan.FromSeconds(steppedSeconds)));

        Assert.Equal(0, result.Run.ExitCode);
        Assert.Null(result.SteppedBy);
    }

    /// <summary>
    /// The system's clock, save that its wall time is moved <paramref name="by"/> once it has been read once: a clock
    /// that stepped while a run went. Its monotonic time, and its timers, are the system's.
    /// </summary>
    private sealed class SteppedOnce(TimeSpan by) : TimeProvider
    {
        private int _read;

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + (Interlocked.Increment(ref _read) > 1 ? by : TimeSpan.Zero);
    }

    /// <summary>A sweep stopped while a run goes is the sweep's to say: the run is not said as one past its bound.</summary>
    [Fact]
    public async Task ASweepStoppedMidRun_IsTheSweeps_NeverABound()
    {
        using var temp = new TempDirectory();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        stop.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunAsync(Child("sleep", temp.Combine("arms", "loop"), "120000") with { Bound = TimeSpan.FromMinutes(10) }, stop.Token));
    }

    /// <summary>Runs <paramref name="request"/> as a sweep runs an arm's binary.</summary>
    private static Task<ArmRunResult> RunAsync(ArmRunRequest request, CancellationToken? cancellationToken = null, IFileSystem? fileSystem = null, TimeProvider? clock = null)
    {
        var harness = new HarnessFactory();

        return new ArmTestRunner(new PhaseRunner(harness.ProcessRunner, harness.FileSystem, harness.Output), fileSystem ?? harness.FileSystem, clock ?? TimeProvider.System)
            .RunAsync(request, cancellationToken ?? TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The real file system, save that a run's log is written over, as long as it was, once the run has ended: as its
    /// report is looked for.
    /// </summary>
    private sealed class LogWrittenAgain(IFileSystem inner, string log) : PassThroughFileSystem(inner)
    {
        public override bool FileExists(string path)
        {
            if (Path.GetFileName(path) == ArmTestRunner.ReportFileName && File.Exists(log))
            {
                File.WriteAllBytes(log, [.. File.ReadAllBytes(log).Select(_ => (byte)'x')]);
            }

            return base.FileExists(path);
        }
    }

    /// <summary>
    /// The real file system, save that a run's report cannot be read the first <paramref name="heldFor"/> times it is
    /// tried, as one another process holds cannot - or, where <paramref name="denied"/>, as one this user may not read.
    /// </summary>
    private sealed class HeldReport(IFileSystem inner, int heldFor, bool denied) : PassThroughFileSystem(inner)
    {
        private int _reads;

        /// <summary>How many times the report was read, or tried.</summary>
        public int Reads => _reads;

        public override string ReadAllText(string path)
        {
            if (Path.GetFileName(path) != ArmTestRunner.ReportFileName)
            {
                return base.ReadAllText(path);
            }

            if (Interlocked.Increment(ref _reads) > heldFor)
            {
                return base.ReadAllText(path);
            }

            const string Held = "'report.xml' is held by another process";

            throw denied ? new UnauthorizedAccessException(Held) : new IOException(Held);
        }
    }

    /// <summary>
    /// A run of this assembly as a child doing <paramref name="mode"/>, its arguments given as the report arguments are -
    /// <c>{report}</c> among them filled in with the report's path - and its records in <paramref name="records"/>.
    /// </summary>
    private static ArmRunRequest Child(string mode, string records, params string[] arguments)
    {
        var child = TestHost.ChildRequest(mode);

        return new ArmRunRequest
        {
            Leg = "native/arms/charge-bound",
            Program = child.FileName,
            ReportArgs = [.. child.Arguments, .. arguments],
            RecordDirectory = records,
            WorkingDirectory = Path.GetDirectoryName(records)!,
            Environment = child.Environment,
        };
    }
}
