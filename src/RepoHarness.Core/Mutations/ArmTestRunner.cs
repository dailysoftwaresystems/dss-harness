using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Mutations;

/// <summary>One whole run of a test binary in a worker: an arm's, or its binary's pristine control.</summary>
internal sealed record ArmRunRequest
{
    /// <summary>What the run is logged and echoed under: <c>leg/arms/arm</c>, or <c>leg/controls/runner</c>.</summary>
    public required string Leg { get; init; }

    /// <summary>The binary, as the worker's build made it.</summary>
    public required string Program { get; init; }

    /// <summary>The arguments that make it write its report: <c>mutations.reportArgs</c>, before <c>{report}</c> is filled in.</summary>
    public required IReadOnlyList<string> ReportArgs { get; init; }

    /// <summary>Where its log and its report are kept: the arm's records, or the control's.</summary>
    public required string RecordDirectory { get; init; }

    /// <summary>Where it starts.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>The environment it starts in.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>The directories it finds programs in beside its PATH, as the leg's host declares them.</summary>
    public IReadOnlyList<string> AppendToPath { get; init; } = [];

    /// <summary>How long it may print nothing before it is stopped as hung: <c>defaults.stallSeconds</c>.</summary>
    public int StallSeconds { get; init; }

    /// <summary>
    /// How far wall-clock time may move from monotonic time across the run before it is said to have stepped:
    /// <c>defaults.clockStepToleranceMilliseconds</c>, zero watching for none.
    /// </summary>
    public int ClockStepToleranceMilliseconds { get; init; }

    /// <summary>How long it may run before it is stopped, or <see langword="null"/> for a control, which is unbounded.</summary>
    public TimeSpan? Bound { get; init; }

    /// <summary>What <see cref="Bound"/> is of the unmutated run, as a verdict says it.</summary>
    public double Factor { get; init; }

    /// <summary>The text its output must say, or <see langword="null"/> where nothing is watched for.</summary>
    public string? Diagnostic { get; init; }
}

/// <summary>What one run established, and where it was kept.</summary>
/// <param name="Run">What the judge reads of it.</param>
/// <param name="Duration">How long it ran, from the monotonic clock.</param>
/// <param name="LogFile">Its output, both streams in the order they came.</param>
/// <param name="ReportFile">Where it was told to write its report.</param>
internal sealed record ArmRunResult(ArmRun Run, TimeSpan Duration, string LogFile, string ReportFile)
{
    /// <summary>
    /// How far wall-clock time moved from monotonic time across the run, where that is past the tolerance its request
    /// set - the clock stepped, or the host slept, while it ran - or <see langword="null"/>. Told however the run ended,
    /// stopped at its bound included: what it marks is how long the run took, which a bound is held against.
    /// </summary>
    public TimeSpan? SteppedBy { get; init; }
}

/// <summary>Runs a test binary whole, and reads its report and what it said.</summary>
internal interface IArmTestRunner
{
    /// <summary>Runs <paramref name="request"/>.</summary>
    /// <param name="request">The run.</param>
    /// <param name="cancellationToken">Stops the sweep, which this run is part of.</param>
    Task<ArmRunResult> RunAsync(ArmRunRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IArmTestRunner"/>
/// <remarks>
/// <para>
/// Whole, never filtered: a mutation can leave green the case named after what it breaks while another case takes the
/// red, and a filter would never see that one. The report it is told to write is removed before it starts, so a report
/// an earlier run left is never read as this run's.
/// </para>
/// <para>
/// Run as every phase is - its output to its log a line at a time, stopped as hung when it prints nothing for
/// <c>defaults.stallSeconds</c> - and besides bounded, where a bound is given, by how long it may take at all: a mutation
/// that turns a loop endless can keep printing. A run past its bound is stopped and said as one; the sweep stopped
/// meanwhile is the sweep's to say. What it said is read from its log, a line at a time, and never held whole.
/// </para>
/// <para>
/// Timed on both clocks across the run, as a phase is across itself (<see cref="ClockStep"/>), and by this rather than
/// by its phase: a step past <c>defaults.clockStepToleranceMilliseconds</c> is said however the run ended, and one
/// stopped at its bound has no phase left to say it.
/// </para>
/// <para>
/// Its report is read once it ends, and read again where its file cannot be: a report that is no report is the
/// binary's, said with why it is none, and one this could not read is this tool's, said as that.
/// </para>
/// </remarks>
internal sealed class ArmTestRunner(PhaseRunner phaseRunner, IFileSystem fileSystem, TimeProvider clock) : IArmTestRunner
{
    /// <summary>The name a run's log is kept under, beside its report.</summary>
    public const string PhaseName = "run";

    /// <summary>The name a run's report is kept under.</summary>
    public const string ReportFileName = "report.xml";

    /// <summary>How many times a report that is there is read before it is said to be unread.</summary>
    public const int ReadAttempts = 5;

    /// <summary>
    /// How long a read of a report waits before it is tried again: what holds a report as its run ends - the binary's
    /// own child still closing it, a scanner reading it - lets go within moments.
    /// </summary>
    public static readonly TimeSpan ReadRetry = TimeSpan.FromMilliseconds(200);

    private readonly PhaseRunner _phaseRunner = phaseRunner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc/>
    public async Task<ArmRunResult> RunAsync(ArmRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var report = Path.Combine(request.RecordDirectory, ReportFileName);
        var log = Path.Combine(request.RecordDirectory, PhaseName + ".log");

        _fileSystem.CreateDirectory(request.RecordDirectory);

        if (_fileSystem.FileExists(report))
        {
            _fileSystem.DeleteFile(report);
        }

        using var timed = request.Bound is { } bound ? new CancellationTokenSource(bound, _clock) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timed.Token);
        var started = _clock.GetTimestamp();
        var startedAt = _clock.GetUtcNow();
        PhaseResult phase;

        // Both clocks read across the run, as a phase reads them across itself, and here rather than by the phase: a
        // run stopped at its bound answers with no phase to say a step, and its length is what the bound was held against.
        TimeSpan? SteppedBy()
        {
            var drift = ClockStep.Drift(_clock.GetUtcNow() - startedAt, _clock.GetElapsedTime(started));

            return ClockStep.IsPast(drift, request.ClockStepToleranceMilliseconds) ? drift : null;
        }

        try
        {
            phase = await _phaseRunner
                .RunAsync(
                    new PhaseRequest
                    {
                        Leg = request.Leg,
                        Phase = PhaseName,
                        FileName = request.Program,
                        Arguments = MutationReport.Arguments(request.ReportArgs, report),
                        LogFile = log,
                        WorkingDirectory = request.WorkingDirectory,
                        Environment = request.Environment,
                        AppendToPath = request.AppendToPath,
                        StallSeconds = request.StallSeconds,
                    },
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timed.IsCancellationRequested)
        {
            return new ArmRunResult(
                new ArmRun
                {
                    StoppedAtBound = true,
                    Bound = request.Bound!.Value,
                    Factor = request.Factor,
                    ReportWritten = _fileSystem.FileExists(report),
                },
                _clock.GetElapsedTime(started),
                log,
                report)
            {
                SteppedBy = SteppedBy(),
            };
        }

        var steppedBy = SteppedBy();

        if (phase.Stalled)
        {
            return new ArmRunResult(
                new ArmRun
                {
                    StalledAfterSeconds = phase.StallSeconds,
                    Bound = request.Bound ?? TimeSpan.Zero,
                    Factor = request.Factor,
                    ReportWritten = _fileSystem.FileExists(report),
                },
                phase.Duration,
                log,
                report)
            {
                SteppedBy = steppedBy,
            };
        }

        var written = _fileSystem.FileExists(report);
        var read = written ? await ReadAsync(report, cancellationToken).ConfigureAwait(false) : default;

        return new ArmRunResult(
            new ArmRun
            {
                ExitCode = phase.ExitCode,
                Bound = request.Bound ?? TimeSpan.Zero,
                Factor = request.Factor,
                ReportWritten = written,
                Report = read.Report,
                ReportProblem = read.Problem,
                ReportUnread = read.Unread,
                DiagnosticSaid = request.Diagnostic is { } diagnostic && DiagWindow.Appears(diagnostic, phase.Output.Lines()),
            },
            phase.Duration,
            log,
            report)
        {
            SteppedBy = steppedBy,
        };
    }

    /// <summary>
    /// The report at <paramref name="path"/>, which is there: itself; why it is no report; or why its file could not be
    /// read, tried <see cref="ReadAttempts"/> times, <see cref="ReadRetry"/> apart. The two are never said as one: a
    /// file this could not read is the harness's own failure, and what it holds may be a report naming every case.
    /// </summary>
    private async Task<(JUnitReport? Report, string? Problem, string? Unread)> ReadAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var report = JUnitReport.Read(_fileSystem.ReadAllText(path), out var problem);

                return (report, problem, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == ReadAttempts)
                {
                    return (null, null, ex.Message);
                }
            }

            await Task.Delay(ReadRetry, _clock, cancellationToken).ConfigureAwait(false);
        }
    }
}
