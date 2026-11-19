using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Text;

namespace DssHarness.Core.Execution;

/// <summary>One phase to run: a program, its arguments, and the evidence it must produce.</summary>
public sealed record PhaseRequest
{
    /// <summary>The leg this phase belongs to, which the ledger and the log path are keyed by.</summary>
    public required string Leg { get; init; }

    /// <summary>The phase's name, which also names its log file.</summary>
    public required string Phase { get; init; }

    /// <summary>
    /// The program: a name looked up on PATH, or a path. Never a shell string, so no shell's process
    /// emulation sits between the harness and the runner — MSYS's was measured losing tests from a
    /// parallel test run with no failure reported.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>Arguments, one element per argument, never pre-quoted.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Where the log of this phase's output is written; its directory is created if missing.</summary>
    public required string LogFile { get; init; }

    /// <summary>Working directory for the command, or <see langword="null"/> to inherit this one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment overrides; a <see langword="null"/> value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Directories put around the PATH the command runs with, and looks its program up on: raised ahead of it, or appended.</summary>
    public ProgramDirectories ProgramDirectories { get; init; } = ProgramDirectories.None;

    /// <summary>
    /// The pattern proving the command ran, matched against each line of its output as it arrives, or
    /// <see langword="null"/> where the phase declares none. An empty or blank pattern matches anything,
    /// so it is refused rather than honoured.
    /// </summary>
    public string? SuccessPattern { get; init; }

    /// <summary>Seconds without output after which the phase is hung; zero disables the bound.</summary>
    public int StallSeconds { get; init; }

    /// <summary>
    /// Patterns whose every match is pulled out of each line of the command's output as it arrives, each written to the
    /// phase's timing record as it is found (<see cref="PhaseTimings"/>).
    /// </summary>
    public IReadOnlyList<string> TimingPatterns { get; init; } = [];

    /// <summary>
    /// Milliseconds wall-clock time may drift from monotonic time across the phase before it is
    /// recorded as having spanned a clock step; zero or less disables the check, since drift below a
    /// millisecond is ordinary and flagging it would mark every phase suspect.
    /// </summary>
    public int ClockStepToleranceMilliseconds { get; init; }

    /// <summary>
    /// Masks each line of the child's output before anything keeps or shows it, or <see langword="null"/>
    /// when the phase carries nothing to mask.
    /// </summary>
    /// <remarks>
    /// Applied here rather than by the caller afterwards, because for the verbose echo there is no
    /// afterwards: a line reaches the terminal as the child writes it, and masking only the finished
    /// log would leave the value in front of whoever asked to watch. The caller that supplied the
    /// secret is the only one that knows what to look for, so it supplies the mask too - and says where
    /// a line too long to keep whole may be cut, since a secret cut in two is masked in neither piece.
    /// </remarks>
    public ILineMask? Mask { get; init; }

    /// <summary>
    /// What is written to the child's standard input, which is then closed so the child sees its end;
    /// <see langword="null"/> gives it an input that ends at once, as a phase's child always had.
    /// </summary>
    public ChildInput? StandardInput { get; init; }

    /// <summary>
    /// Invoked for each line of either stream once it is kept - masked, written to the log and read
    /// by the witness - for a reader that takes each line as one answer and keeps what it needs of
    /// them itself. Each stream's lines arrive in the order the child wrote them, but nothing orders
    /// one stream's against the other's: each stream is read on a thread of its own, and a line of
    /// one may arrive before, after or at the same time as a line of the other, so implementations
    /// must be thread safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unless <see cref="WholeLines"/> is declared, a line longer than
    /// <see cref="ProcessRunner.LongestLine"/> characters arrives in pieces of at most that many,
    /// never held whole.
    /// </para>
    /// <para>
    /// A callback that throws is handed no more lines, and the phase goes on without it: every line
    /// is still logged, read by the witness and counted against the silence bound, and the log ends
    /// with its exit line. What the callback raised first is raised once the phase has ended.
    /// </para>
    /// </remarks>
    public Action<string>? OnLine { get; init; }

    /// <summary>
    /// Wall-clock bound on the whole phase, or <see langword="null"/> for none. The silence bound
    /// (<see cref="StallSeconds"/>) is the one in force for a phase that declares none of its own:
    /// a wall-clock budget is a guess about workload size, and an honest run that exceeds it gets
    /// stopped - which is what a phase is allowed to ask for where the run is bounded outside, by
    /// what asked for it.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Whether each line the child prints is handed on whole to <see cref="OnLine"/>, however long,
    /// rather than in pieces of at most <see cref="ProcessRunner.LongestLine"/> characters. For a
    /// reader that takes each line as one answer, where a line is one answer entire and a piece of
    /// one is not: the log keeps the line either way, and what is read of the output is read back
    /// from there.
    /// </summary>
    public bool WholeLines { get; init; }

    /// <summary>
    /// Whether what the phase starts is to outlive it and the process that ran it: a set-up's <c>up</c> line, whose
    /// services legs of other runs share and its <c>down</c> line ends. Every other phase is bound to the process that
    /// runs it (<see cref="ProcessRequest.Bound"/>): on Windows that process ending in any way ends what the phase started,
    /// and elsewhere as that property says.
    /// </summary>
    public bool LeftRunning { get; init; }

    /// <summary>
    /// Whether what the phase starts is contained (<see cref="ProcessRequest.Contained"/>): ended whole with it, at its own
    /// end as at a stop, what detached itself among it - a command an agent runs. A phase that is not is ended by its
    /// process tree, as a leg's always is.
    /// </summary>
    public bool Contained { get; init; }

    /// <summary>
    /// Where a <see cref="Contained"/> phase's session is recorded while it runs, so a later run of the tool ends it where
    /// this process was killed and left it (<see cref="ProcessRequest.SessionRecord"/>); null where it is recorded nowhere.
    /// </summary>
    public IContainedSessionRecord? SessionRecord { get; init; }

    /// <summary>
    /// The operating system's isolation the phase runs inside (<see cref="ProcessRequest.Isolation"/>); null for the one
    /// its leg's work holds (<see cref="PhaseIsolation"/>), and none where that holds none or the phase is
    /// <see cref="LeftRunning"/>.
    /// </summary>
    public ProcessIsolation? Isolation { get; init; }
}

/// <summary>
/// Runs one phase of a leg and reports what it established.
/// </summary>
/// <remarks>
/// The rules here are the ones a green result stops meaning anything without: the exit code is read
/// from the process, a declared pattern must match the command's own output, a phase is bounded by
/// its silence rather than by a guess at how long it should take unless it declares a wall-clock
/// bound of its own, and every duration comes from the monotonic clock while the wall clock is
/// watched for the steps one host makes every few seconds.
/// <para>
/// The child's output goes to the phase's log and nowhere else whole. Each line is read once, as it
/// arrives - for the witness, the timing marks and the last lines - and what is kept of it is bounded
/// whatever the child prints; whatever reads more reads it back from the log (see
/// <see cref="PhaseOutput"/>). A consumer's test once printed two gigabytes of one traceback, and a
/// runner that kept every line as text died out of memory with most of the machine's free: no string
/// holds more than about a billion characters.
/// </para>
/// </remarks>
public sealed class PhaseRunner(
    IProcessRunner processRunner,
    IFileSystem fileSystem,
    IHarnessOutput output,
    TimeProvider? wallClock = null)
{
    /// <summary>
    /// How each line of a log ends: this machine's own line ending, as it always has. Spelled once, for the runner that
    /// writes a log and for <see cref="PhaseOutput"/>, which reads it back.
    /// </summary>
    internal static readonly string LogLineEnding = Environment.NewLine;

    /// <summary>Written without a byte order mark, so a regex reading the log sees the first line.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// The wall clock, read as a phase starts and ends so a step in it can be told from the phase's
    /// own duration: the system's, unless a test needs one that steps.
    /// </summary>
    private readonly TimeProvider _wallClock = wallClock ?? TimeProvider.System;

    /// <summary>Runs <paramref name="request"/> to completion, or until it stalls.</summary>
    /// <param name="request">The phase.</param>
    /// <param name="cancellationToken">Stops the child and its descendants.</param>
    /// <exception cref="HarnessException">
    /// The success pattern is empty, or a declared pattern is not a regular expression. Refused
    /// rather than treated as "no pattern": a phase whose witness cannot be evaluated would pass on
    /// its exit code alone, which is exactly what the witness exists to stop.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancelled; the child tree was stopped.</exception>
    public async Task<PhaseResult> RunAsync(PhaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (success, timings) = Patterns(request.Leg, request.Phase, request.SuccessPattern, request.TimingPatterns);

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.LogFile));
        if (!string.IsNullOrEmpty(directory))
        {
            _fileSystem.CreateDirectory(directory);
        }

        // FileShare.ReadWrite, and flushed per line: a phase that hangs must have a readable log
        // while it is hanging, which is the only evidence of what it was doing.
        await using var log = new StreamWriter(
            new FileStream(request.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
            Utf8NoBom)
        {
            AutoFlush = true,
            NewLine = LogLineEnding,
        };

        var gate = new Lock();
        // Silence is the child's, so the clock is told when the child starts. Everything before that
        // — reading the request, opening the log, resolving the program, a machine under load taking
        // its time over any of it — is this tool's own, and counting it as the child being quiet is
        // how a slow launch reads as a hung command.
        var clock = new StallClock();

        // Every timing mark goes to the record beside the log as it is found, and nothing of them is held: a pattern that
        // matches every line of a flood costs the record its lines and the phase nothing.
        var timingRecord = timings.Count == 0 ? null : PhaseTimings.RecordOf(request.LogFile);
        await using var marks = timingRecord is null
            ? null
            : new FileStream(timingRecord, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var reading = new Reading(success, timings, marks is null ? null : new JsonLineWriter(marks));
        var callback = new LineCallback(request.OnLine);

        // Where the child's own lines begin: what is above is the header this run wrote, and a header
        // that echoes the command line contains the pattern whenever the command does.
        var start = WriteHeader(log, gate, request, _wallClock.GetUtcNow());

        void Line(string raw, bool error)
        {
            clock.Saw();

            // Redacted before the line reaches anything that keeps or shows it, not after. A phase
            // whose environment carries a secret can print it, and the log file, what is read of the
            // line and the verbose echo all read from here: masking only the finished log would leave
            // the value on the terminal of whoever asked for --verbose, which is the one place a reader
            // is certain to be looking. The witness and the timings are matched against the masked
            // line too, deliberately: a success pattern that only matches a password is a pattern
            // nobody should be able to write.
            var line = request.Mask is { } mask ? mask.Redact(raw) : raw;

            lock (gate)
            {
                log.WriteLine(line);

                // Read as it is kept, and now: a phase stopped for stalling, or one whose child printed
                // more than any string holds, has nothing else to be read from but its log.
                reading.Read(line);
            }

            // The line is kept, and this is where a reader that takes each line as one answer sees it:
            // masked, as the log holds it, and after it is kept. What the reader raises is kept for
            // the end of the phase, never let through to the runner, which would hand this handler -
            // the log, the witness and the silence clock with it - no more lines of the stream.
            callback.Hand(line);

            // Progress is one line per leg transition, not a stream of child output; the child's own
            // output is echoed only when it was asked for. Echoed, it says which leg and phase wrote
            // it: legs run at the same time, so without that the terminal carries several children's
            // output woven together with nothing to tell one from another. The log files keep the
            // line as the child wrote it — this prefix is for the terminal alone, so nothing that
            // reads a log has to know about it.
            if (_output.IsVerbose)
            {
                _output.Publish(new PhaseLine(request.Leg, request.Phase, line, error));
            }
        }

        var processRequest = new ProcessRequest
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = request.Environment,
            ProgramDirectories = request.ProgramDirectories,
            StandardInput = request.StandardInput,
            OnStarted = clock.Saw,
            OnOutputLine = line => Line(line, error: false),
            OnErrorLine = line => Line(line, error: true),

            // Taken line by line, and kept above as each line is read: the runner keeps the end of
            // each stream and nothing more, and hands on a line whole - or, where the phase's reader
            // takes each line as one answer entire, hands it on whole however long - never a line in
            // pieces save where a piece is all a line too long to keep can be.
            OutputKept = request.WholeLines ? StreamKept.TailOfWholeLines : StreamKept.Tail,
            ErrorKept = request.WholeLines ? StreamKept.TailOfWholeLines : StreamKept.Tail,
            CutLine = request.Mask is { } cutting ? cutting.Cut : null,

            // A line past the longest string .NET makes is read in pieces, and said: never matched in pieces unseen.
            OnLineInPieces = () => _output.Warn(
                request.Phase,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{request.Leg}: a line of its output ran past the longest text .NET holds ({ProcessRunner.LongestLine:N0} characters), and was kept and matched in pieces")),

            // A wall-clock bound only where the phase declares one of its own; the stall bound below
            // is the one in force otherwise, for the reason the class remarks give.
            Timeout = request.Timeout,

            // On Windows ended with the process that runs it, however that process ends (ProcessRequest.Bound says how it
            // goes elsewhere): a dispatcher killed where it stood left its legs' test runners running for minutes, each
            // holding a heavy leg's room. A set-up's up line is left out: what it brings up is shared, and outlives the run
            // on purpose.
            Bound = !request.LeftRunning,
            Contained = request.Contained,
            SessionRecord = request.SessionRecord,

            // Its own where it names one; else the one its leg's work holds - but never for what is left running, which
            // outlives the isolation's own end of everything inside it.
            Isolation = request.Isolation ?? (request.LeftRunning ? null : PhaseIsolation.Current),
        };

        var startedUtc = _wallClock.GetUtcNow();
        var (result, stopped) = await RunBoundedAsync(processRequest, request.StallSeconds, clock, cancellationToken).ConfigureAwait(false);
        var wall = _wallClock.GetUtcNow() - startedUtc;

        // Every character the child wrote is in the log; what a process it left running - a server its build started,
        // kept for the next phase - writes from now on is that process's own, and is not read. Said, so a log that ends
        // where the child's output did is never taken for all that process went on to write.
        if (result.OutputHeld is { } held)
        {
            _output.Warn(request.Phase, $"{request.Leg}: {held}");
        }

        // Both readings cover the same window, so what they disagree by is the clock's own movement:
        // a step forward, a step back, or a host that slept in the middle of the phase.
        var drift = ClockStep.Drift(wall, clock.Elapsed);
        var stepped = ClockStep.IsPast(drift, request.ClockStepToleranceMilliseconds);

        long end;

        // What closes the child's own lines in the log, which the log is held to wherever they are read back: it says
        // how the phase ended and, to the tick, how long it ran, so a log another phase wrote since does not hold it.
        var exit = $"# exit {(result.TimedOut ? "(stopped)" : result.ExitCode.ToString(CultureInfo.InvariantCulture))} after {clock.Elapsed}";

        lock (gate)
        {
            // Where the child's own lines end: what follows is the exit line this run writes.
            end = log.BaseStream.Position;
            log.WriteLine(exit);

            // Every mark found is in the record, for whatever reads them back.
            marks?.Flush();
        }

        // Now that the log is whole, its exit line included: a reader's failure is its own, and the
        // record of the phase it was reading is kept all the same.
        callback.RaiseFailure();

        return new PhaseResult(
            Leg: request.Leg,
            Phase: request.Phase,
            ExitCode: result.ExitCode,
            Stopped: stopped,
            StallSeconds: request.StallSeconds,
            Witnessed: success is null ? null : reading.Witnessed(request.SuccessPattern!),
            Duration: result.Duration,
            ClockDrift: drift,
            ClockStepped: stepped,
            Timings: timingRecord is null ? PhaseTimings.None : PhaseTimings.InRecord(_fileSystem, timingRecord, [.. timings.Select(timing => timing.Pattern)], reading.Marks),
            LogFile: request.LogFile,

            // The child's lines as the log keeps them, already masked: read back from there by whatever
            // needs more of them than was read above, never held as text. A redaction every reader of
            // the output had to remember is one a new reader would not.
            Output: PhaseOutput.InLog(_fileSystem, request.LogFile, start, end, Utf8NoBom.GetBytes(exit + LogLineEnding)))
        {
            // In the order the lines came, masked as each came, whichever stream carried each.
            LastLines = reading.LastLines,
            Uncontained = result.Uncontained,
        };
    }

    /// <summary>
    /// Runs the child, stopping it when it goes <paramref name="stallSeconds"/> without a line on
    /// either stream, and says which bound stopped it, if one did. The bound is enforced through a
    /// source of this method's own, so a hung phase and a run the operator interrupted stay
    /// distinguishable: the first comes back as a stopped phase to be reported, the second is raised
    /// and ends the leg.
    /// </summary>
    /// <remarks>
    /// Which bound stopped the child is read from the bounds themselves, never from how the runner
    /// reported the stop: the silence bound where this method's own source fired - it fires only once
    /// the child has been quiet that long - and otherwise the wall-clock bound the request carries,
    /// which is the only other bound that stops a child the caller did not give up on. Where both pass
    /// in one moment the silence bound is named, and that is true too.
    /// </remarks>
    private async Task<(ProcessResult Result, PhaseStop Stopped)> RunBoundedAsync(
        ProcessRequest request,
        int stallSeconds,
        StallClock clock,
        CancellationToken cancellationToken)
    {
        if (stallSeconds <= 0)
        {
            var unbounded = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);

            return (unbounded, unbounded.TimedOut ? PhaseStop.WallClock : PhaseStop.None);
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var finished = new CancellationTokenSource();
        var bound = TimeSpan.FromSeconds(stallSeconds);
        var watching = WatchAsync(clock, bound, stall, finished.Token);

        try
        {
            var result = await _processRunner.RunAsync(request, stall.Token).ConfigureAwait(false);

            return (
                result,
                !result.TimedOut ? PhaseStop.None
                : stall.IsCancellationRequested ? PhaseStop.Silence
                : PhaseStop.WallClock);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The bound fired, and the runner stopped the child and its descendants. The exit code
            // is meaningless, which is what a stopped phase reports; the lines the child wrote
            // before it went quiet were kept as they arrived, and what a contained child's container
            // could not reach at the stop is said.
            return (
                new ProcessResult(-1, string.Empty, string.Empty, clock.Elapsed, TimedOut: true) { Uncontained = (ex as ProcessStoppedException)?.Uncontained },
                PhaseStop.Silence);
        }
        finally
        {
            await finished.CancelAsync().ConfigureAwait(false);
            await watching.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Watches the silence - from the launch, a start counting as silence - and cancels <paramref name="stall"/> when it
    /// passes <paramref name="bound"/>, the user's own stall bound. The poll
    /// is a quarter of the bound so the phase is stopped near the moment it is due, and never more
    /// often than every 50 milliseconds, which would cost more than it measures.
    /// </summary>
    private static async Task WatchAsync(StallClock clock, TimeSpan bound, CancellationTokenSource stall, CancellationToken finished)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(bound.TotalMilliseconds / 4, 50, 1000));

        try
        {
            while (!finished.IsCancellationRequested)
            {
                await Task.Delay(poll, finished).ConfigureAwait(false);

                if (clock.Overdue(bound))
                {
                    await stall.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The phase finished, so there is nothing left to bound.
        }
    }

    /// <summary>Writes the log's header, and says where in the log the line after it begins.</summary>
    private static long WriteHeader(StreamWriter log, Lock gate, PhaseRequest request, DateTimeOffset started)
    {
        // Written for whoever reads the log, and deliberately never matched against: this is the
        // text a success pattern would otherwise witness itself in.
        lock (gate)
        {
            log.WriteLine($"# leg {request.Leg}, phase {request.Phase}");
            log.WriteLine($"# command {request.FileName} {string.Join(' ', request.Arguments)}");
            log.WriteLine($"# started {started:u}");

            return log.BaseStream.Position;
        }
    }

    /// <summary>
    /// The success pattern and the timing patterns a phase is read by, compiled, or refused where one cannot
    /// be: what starting a phase refuses before anything of it starts, for a caller to refuse as well before
    /// the work the phase would follow.
    /// </summary>
    /// <param name="leg">The leg the phase belongs to, named in a refusal.</param>
    /// <param name="phase">The phase, named in a refusal.</param>
    /// <param name="successPattern">The success pattern, or <see langword="null"/> where the phase declares none.</param>
    /// <param name="timingPatterns">The timing patterns.</param>
    /// <exception cref="HarnessException">The success pattern is empty, or a pattern is not a regular expression.</exception>
    internal static (Regex? Success, IReadOnlyList<(string Pattern, Regex Regex)> Timings) Patterns(
        string leg,
        string phase,
        string? successPattern,
        IReadOnlyList<string> timingPatterns)
    {
        ArgumentNullException.ThrowIfNull(timingPatterns);

        if (successPattern is not null && string.IsNullOrWhiteSpace(successPattern))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"Phase '{phase}' of leg '{leg}' declares an empty success pattern, which matches anything; "
                + "declare the line the command prints when it succeeds, or declare no pattern.");
        }

        return (
            successPattern is null ? null : Compile(successPattern, "a success pattern"),
            [.. timingPatterns.Select(pattern => (pattern, Compile(pattern, "a timing pattern")))]);
    }

    private static Regex Compile(string pattern, string what)
    {
        // Multiline, so a pattern anchored with ^ or $ means the start or end of a line of output, which is how such a
        // pattern is written and what it is matched against; and linear, so no line of a phase's output, however long,
        // holds a match up - a construct the linear engine lacks is refused by name.
        return LinearRegex.TryCompile(pattern, RegexOptions.Multiline, out var regex, out var refusal)
            ? regex
            : throw new HarnessException(HarnessExit.ConfigInvalid, $"'{pattern}' is not {what}: {refusal}");
    }

    /// <summary>
    /// Hands each kept line to a phase's line callback, and keeps what the callback raised: one that fails is handed no
    /// more lines, and the first thing it raised is raised once the phase has ended.
    /// </summary>
    /// <remarks>
    /// Caught here, inside the handler the process runner calls, because that handler is also what keeps the log, reads
    /// the witness and tells the silence clock the child spoke. Let through, a failure made the runner hand the handler
    /// no more lines of that stream: the log stopped where the callback did and never got its exit line, the witness
    /// read nothing more, and a child still talking was timed as silent.
    /// </remarks>
    /// <param name="onLine">The phase's line callback, or <see langword="null"/> where it declares none.</param>
    private sealed class LineCallback(Action<string>? onLine)
    {
        private ExceptionDispatchInfo? _failed;

        /// <summary>Hands <paramref name="line"/> on, unless the callback has already failed; called from both streams' threads.</summary>
        public void Hand(string line)
        {
            if (onLine is null || Volatile.Read(ref _failed) is not null)
            {
                return;
            }

            try
            {
                onLine(line);
            }
            catch (Exception ex)
            {
                // The first failure is the one raised: the two streams' threads may both fail, and the
                // later says nothing the first did not.
                Interlocked.CompareExchange(ref _failed, ExceptionDispatchInfo.Capture(ex), null);
            }
        }

        /// <summary>Raises what the callback raised first, where it raised anything.</summary>
        public void RaiseFailure() => Volatile.Read(ref _failed)?.Throw();
    }

    /// <summary>
    /// What a phase establishes from its child's output, read a line at a time as each line is kept: whether the success
    /// pattern matched, the timing marks - each written to the phase's timing record as it is found - and the last lines.
    /// Nothing else of the output is held, so what this holds is the same whatever the child prints.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each line is read as the log keeps it, whatever ended it - a Windows program's carriage return otherwise stands
    /// between a line's text and the $ a pattern ends with, and the pattern never matches there - and as reading the log
    /// back reads it, so that what was read as the phase ran is what any later reader of its output finds: a line longer
    /// than <see cref="ProcessRunner.LongestLine"/> characters, which only masking a secret shorter than its mask can make,
    /// is read as its pieces.
    /// </para>
    /// <para>
    /// Every pattern is linear (<see cref="LinearRegex"/>), so each is decided against every line, however long: none is
    /// ever left unknown for the time a match took.
    /// </para>
    /// </remarks>
    /// <param name="success">The success pattern, or <see langword="null"/> where the phase declares none.</param>
    /// <param name="timings">The timing patterns.</param>
    /// <param name="record">Where each timing mark is written as it is found; <see langword="null"/> where the phase declares no timing pattern.</param>
    private sealed class Reading(Regex? success, IReadOnlyList<(string Pattern, Regex Regex)> timings, JsonLineWriter? record)
    {
        private readonly LastLinesRing _last = new(PhaseResult.TailLines);
        private bool _witnessed;

        /// <summary>How many timing marks were written to the record, every pattern's together.</summary>
        public long Marks { get; private set; }

        /// <summary>The last lines read, in the order they came.</summary>
        public IReadOnlyList<string> LastLines => _last.ToList();

        /// <summary>Reads the next line the child printed, as the log keeps it.</summary>
        public void Read(string line)
        {
            foreach (var piece in LineSplitter.PiecesOf(line, ProcessRunner.LongestLineHandedOn))
            {
                Witness(piece);
                Time(piece);
                _last.Add(piece);
            }
        }

        /// <summary>Whether the success pattern matched a line.</summary>
        /// <param name="pattern">The pattern as declared.</param>
        public bool Witnessed(string pattern)
        {
            ArgumentNullException.ThrowIfNull(pattern);
            return _witnessed;
        }

        private void Witness(string line)
        {
            // Once matched, matched: a pattern is evidence the command ran the moment one line shows it.
            if (success is null || _witnessed)
            {
                return;
            }

            _witnessed = success.IsMatch(line);
        }

        private void Time(string line)
        {
            for (var index = 0; index < timings.Count; index++)
            {
                foreach (var match in timings[index].Regex.Matches(line).Cast<Match>())
                {
                    var value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
                    record!.Write(new PhaseTimings.Mark(index, match.Value, value));
                    Marks++;
                }
            }
        }
    }
}
