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
    /// few times: one let go meanwhile is read as it is, and one held throughout is said as written and unread, with
    /// why, and never as no report, which would be a finding about the binary.
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

        var result = await RunAsync(Child("write-file", temp.Combine("arms", "charge-bound"), "{report}", Report), fileSystem: held);

        Assert.True(result.Run.ReportWritten);
        Assert.Null(result.Run.ReportProblem);

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
    private static Task<ArmRunResult> RunAsync(ArmRunRequest request, CancellationToken? cancellationToken = null, IFileSystem? fileSystem = null)
    {
        var harness = new HarnessFactory();

        return new ArmTestRunner(new PhaseRunner(harness.ProcessRunner, harness.FileSystem, harness.Output), fileSystem ?? harness.FileSystem, TimeProvider.System)
            .RunAsync(request, cancellationToken ?? TestContext.Current.CancellationToken);
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
