using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Processes;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Mutations;

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
    public ProgramDirectories ProgramDirectories { get; init; } = ProgramDirectories.None;

    /// <summary>How long it may print nothing before it is stopped as hung: <c>defaults.stallSeconds</c>.</summary>
    public int StallSeconds { get; init; }

    /// <summary>
    /// How far wall-clock time may move from monotonic time across the run before it is said to have stepped:
    /// <c>defaults.clockStepToleranceMilliseconds</c>, zero watching for none.
    /// </summary>
    public int ClockStepToleranceMilliseconds { get; init; }

    /// <summary>How long it may run before it is stopped, and what set that, or <see langword="null"/> for a control, which is unbounded.</summary>
    public RunBound? Bound { get; init; }

    /// <summary>The text its output must say, or <see langword="null"/> where nothing is watched for.</summary>
    public string? Diagnostic { get; init; }

    /// <summary>
    /// Told where a wait of the run stands - its report held by another program as the run ends, read again for as long
    /// as the hold lasts - every <see cref="Waiting.Waits.SaidEvery"/> it lasts and as each wait begins.
    /// </summary>
    public Action<Waiting.WaitTold> Told { get; init; } = _ => { };
}

/// <summary>
/// What one run established. Its output and its report are kept in the directory its request named, under
/// <see cref="ArmTestRunner.PhaseName"/> and <see cref="ArmTestRunner.ReportFileName"/>.
/// </summary>
/// <param name="Run">What the judge reads of it.</param>
/// <param name="Duration">
/// How long it ran, from the monotonic clock, over the window a bound is held against: from the moment it is begun - where
/// the timer of a run given a bound starts - to the moment its phase ends, however it ended.
/// </param>
internal sealed record ArmRunResult(ArmRun Run, TimeSpan Duration)
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
/// Its report is read once it ends, and read again where its file cannot be: for as long as another program holds it -
/// the binary's own child still closing it, a scanner reading it - told while it lasts, and otherwise while each failure
/// is new on the file as it stands. A report that is no report is the binary's, said with why it is none, and one this
/// could not read is this tool's, said as that.
/// </para>
/// </remarks>
internal sealed class ArmTestRunner(PhaseRunner phaseRunner, IFileSystem fileSystem, TimeProvider clock) : IArmTestRunner
{
    /// <summary>The name a run's log is kept under, beside its report.</summary>
    public const string PhaseName = "run";

    /// <summary>The name a run's report is kept under.</summary>
    public const string ReportFileName = "report.xml";

    /// <summary>
    /// How long a read of a report waits before it is tried again: while another program holds the report, every time,
    /// since what holds a report as its run ends - the binary's own child still closing it, a scanner reading it - lets go
    /// within moments, and the read should follow at once; after any other refusal, the first of the waits that double. A
    /// pace, never a bound: the read is tried again for as long as the hold lasts.
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
        var log = PhaseLogs.In(request.RecordDirectory, PhaseName);

        _fileSystem.CreateDirectory(request.RecordDirectory);

        if (_fileSystem.FileExists(report))
        {
            _fileSystem.DeleteFile(report);
        }

        using var timed = request.Bound is { } bound ? new CancellationTokenSource(bound.Limit, _clock) : new CancellationTokenSource();
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
                        ProgramDirectories = request.ProgramDirectories,
                        StallSeconds = request.StallSeconds,
                    },
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timed.IsCancellationRequested)
        {
            // The timer that stopped it is the request's own bound's, so the bound is there.
            return new ArmRunResult(ArmRun.StoppedAt(request.Bound!), _clock.GetElapsedTime(started)) { SteppedBy = SteppedBy() };
        }

        var steppedBy = SteppedBy();

        // Over the window a bound is held against, however the run ended: from the timer's start to the phase's end, what
        // this tool took to start the binary and read its end counted as the binary's own time is - so the unmutated run
        // a bound is made from measures what a mutated run's timer counts, never only the part of it the program ran.
        var ran = _clock.GetElapsedTime(started);

        // Hung only where the silence bound stopped it. The phase declares no wall-clock bound of its own: the arm's bound
        // is the timer above, whose stop never reaches here.
        if (phase.Stopped == PhaseStop.Silence)
        {
            return new ArmRunResult(ArmRun.Hung(phase.StallSeconds), ran) { SteppedBy = steppedBy };
        }

        return new ArmRunResult(
            ArmRun.Exited(
                phase.ExitCode,
                _fileSystem.FileExists(report) ? await ReadAsync(report, request.Told, cancellationToken).ConfigureAwait(false) : RunReport.None,
                request.Diagnostic is { } diagnostic && DiagWindow.Appears(diagnostic, phase.Output.Lines())),
            ran)
        {
            SteppedBy = steppedBy,
        };
    }

    /// <summary>
    /// The report at <paramref name="path"/>, which is there: itself; why it is no report; or why its file could not be
    /// read. A read another program's hold refuses is tried again for as long as the hold lasts (<see cref="Retrying"/>),
    /// told by the report's name while it waits; any other refusal is tried again only while it is new on the file as it
    /// stands, so one met again on a file that did not change ends the reading. The two are never said as one: a file this
    /// could not read is the harness's own failure, and what it holds may be a report naming every case.
    /// </summary>
    private async Task<RunReport> ReadAsync(string path, Action<Waiting.WaitTold> told, CancellationToken cancellationToken)
    {
        var ended = await Retrying.RunAsync<RunReport>(
                (_, _) =>
                {
                    try
                    {
                        return Task.FromResult<RetryTry<RunReport>>(new RetryTry<RunReport>.Done(
                            JUnitReport.Read(_fileSystem.ReadAllText(path), out var problem) is { } report ? RunReport.Of(report) : RunReport.NoReport(problem)));
                    }
                    catch (IOException ex) when (FileHolds.HeldElsewhere(ex))
                    {
                        return Task.FromResult<RetryTry<RunReport>>(new RetryTry<RunReport>.Passing($"another program holds it: {ex.Message.TrimEnd('.')}", After: ReadRetry));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        var standing = _fileSystem.StandingOf(path);

                        return Task.FromResult<RetryTry<RunReport>>(new RetryTry<RunReport>.Failed(
                            ex.Message.TrimEnd('.'),
                            $"{ex.GetType().Name}\n{ex.Message}\n{standing?.Length}\n{standing?.Written?.Ticks}"));
                    }
                },
                new RetryWatch($"the report '{path}' to be read", told, ReadRetry, Bound: null, _clock),
                cancellationToken)
            .ConfigureAwait(false);

        return ended.Kind switch
        {
            RetryEndKind.Done => ended.Value!,
            RetryEndKind.Cancelled => throw new OperationCanceledException(cancellationToken),
            _ => RunReport.NotRead(ended.Failure!),
        };
    }
}
