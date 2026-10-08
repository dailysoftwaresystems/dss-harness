using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

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

    /// <summary>Directories added to the end of the PATH the command runs with, and looks its program up on.</summary>
    public IReadOnlyList<string> AppendToPath { get; init; } = [];

    /// <summary>
    /// The pattern proving the command ran, matched against each line of its output as it arrives, or
    /// <see langword="null"/> where the phase declares none. An empty or blank pattern matches anything,
    /// so it is refused rather than honoured.
    /// </summary>
    public string? SuccessPattern { get; init; }

    /// <summary>Seconds without output after which the phase is hung; zero disables the bound.</summary>
    public int StallSeconds { get; init; }

    /// <summary>
    /// Patterns whose every match is pulled out of each line of the command's output as it arrives, up to
    /// <see cref="PhaseRunner.MostTimings"/> of each.
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
}

/// <summary>
/// Runs one phase of a leg and reports what it established.
/// </summary>
/// <remarks>
/// The rules here are the ones a green result stops meaning anything without: the exit code is read
/// from the process, a declared pattern must match the command's own output, a phase is bounded by
/// its silence rather than by a guess at how long it should take, and every duration comes from the
/// monotonic clock while the wall clock is watched for the steps one host makes every few seconds.
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
    /// The most matches of one timing pattern a phase keeps. A timing mark per test of a suite of thousands is ordinary;
    /// a pattern that matches every line of a flood is not a measurement, and keeping every match would hold the flood.
    /// </summary>
    public const int MostTimings = 10_000;

    /// <summary>
    /// How each line of a log ends: this machine's own line ending, as it always has. Spelled once, for the runner that
    /// writes a log and for <see cref="PhaseOutput"/>, which reads it back.
    /// </summary>
    internal static readonly string LogLineEnding = Environment.NewLine;

    /// <summary>
    /// How long a pattern may spend on one match. A pattern that backtracks past this is refused
    /// rather than left to run: an unbounded match in a phase's own output would hang the leg it was
    /// supposed to be evidence for.
    /// </summary>
    private static readonly TimeSpan MatchBudget = TimeSpan.FromSeconds(5);

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
        var reading = new Reading(success, timings);

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

            // Progress is one line per leg transition, not a stream of child output; the child's own
            // output is echoed only when it was asked for. Echoed, it says which leg and phase wrote
            // it: legs run at the same time, so without that the terminal carries several children's
            // output woven together with nothing to tell one from another. The log files keep the
            // line as the child wrote it — this prefix is for the terminal alone, so nothing that
            // reads a log has to know about it.
            if (_output.IsVerbose)
            {
                var tagged = $"{request.Leg}/{request.Phase}: {line}";

                if (error)
                {
                    _output.RawError(tagged);
                }
                else
                {
                    _output.Raw(tagged);
                }
            }
        }

        var processRequest = new ProcessRequest
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
            Environment = request.Environment,
            AppendToPath = request.AppendToPath,
            OnStarted = clock.Saw,
            OnOutputLine = line => Line(line, error: false),
            OnErrorLine = line => Line(line, error: true),

            // Taken line by line, and kept above as each line is read: the runner keeps the end of
            // each stream and nothing more, and hands on a line that never ends in pieces, cut where
            // the mask parts no secret.
            OutputKept = StreamKept.Tail,
            ErrorKept = StreamKept.Tail,
            CutLine = request.Mask is { } cutting ? cutting.Cut : null,

            // Deliberately no Timeout: a wall-clock budget is a guess about workload size, and an
            // honest run that exceeds it gets killed. The stall bound below is the bound in force.
        };

        var startedUtc = _wallClock.GetUtcNow();
        var result = await RunBoundedAsync(processRequest, request.StallSeconds, clock, cancellationToken).ConfigureAwait(false);
        var wall = _wallClock.GetUtcNow() - startedUtc;

        // Both readings cover the same window, so what they disagree by is the clock's own movement:
        // a step forward, a step back, or a host that slept in the middle of the phase.
        var drift = Abs(wall - clock.Elapsed);
        var stepped = request.ClockStepToleranceMilliseconds > 0
            && drift > TimeSpan.FromMilliseconds(request.ClockStepToleranceMilliseconds);

        long end;

        // What closes the child's own lines in the log, which the log is held to wherever they are read back: it says
        // how the phase ended and, to the tick, how long it ran, so a log another phase wrote since does not hold it.
        var exit = $"# exit {(result.TimedOut ? "(stopped)" : result.ExitCode.ToString(CultureInfo.InvariantCulture))} after {clock.Elapsed}";

        lock (gate)
        {
            // Where the child's own lines end: what follows is the exit line this run writes.
            end = log.BaseStream.Position;
            log.WriteLine(exit);
        }

        return new PhaseResult(
            Leg: request.Leg,
            Phase: request.Phase,
            ExitCode: result.ExitCode,
            Stalled: result.TimedOut,
            StallSeconds: request.StallSeconds,
            Witnessed: success is null ? null : reading.Witnessed(request.SuccessPattern!),
            Duration: result.Duration,
            ClockDrift: drift,
            ClockStepped: stepped,
            Timings: reading.Timings(_output, request.Phase),
            LogFile: request.LogFile,

            // The child's lines as the log keeps them, already masked: read back from there by whatever
            // needs more of them than was read above, never held as text. A redaction every reader of
            // the output had to remember is one a new reader would not.
            Output: PhaseOutput.InLog(request.LogFile, start, end, Utf8NoBom.GetBytes(exit + LogLineEnding)))
        {
            // In the order the lines came, masked as each came, whichever stream carried each.
            LastLines = reading.LastLines,
        };
    }

    /// <summary>
    /// Runs the child, stopping it when it goes <paramref name="stallSeconds"/> without a line on
    /// either stream. The bound is enforced through a source of this method's own, so a hung phase
    /// and a run the operator interrupted stay distinguishable: the first comes back as a stopped
    /// phase to be reported, the second is raised and ends the leg.
    /// </summary>
    private async Task<ProcessResult> RunBoundedAsync(
        ProcessRequest request,
        int stallSeconds,
        StallClock clock,
        CancellationToken cancellationToken)
    {
        if (stallSeconds <= 0)
        {
            return await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var finished = new CancellationTokenSource();
        var bound = TimeSpan.FromSeconds(stallSeconds);
        var watching = WatchAsync(clock, bound, stall, finished.Token);

        try
        {
            return await _processRunner.RunAsync(request, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The bound fired, and the runner stopped the child and its descendants. The exit code
            // is meaningless, which is what a stopped phase reports; the lines the child wrote
            // before it went quiet were kept as they arrived.
            return new ProcessResult(-1, string.Empty, string.Empty, clock.Elapsed, TimedOut: true);
        }
        finally
        {
            await finished.CancelAsync().ConfigureAwait(false);
            await watching.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How much longer than the silence bound a child may take to start. Starting is this tool's own
    /// work and is not counted as the child being quiet, but it is not unbounded either: resolving a
    /// program walks every entry of PATH, and one naming an unreachable share blocks for that
    /// platform's own timeout per entry, as does a working directory on a mount that has gone away.
    /// <para>
    /// A multiple of the silence bound rather than a number of its own, because the two measure
    /// different things and only one of them repeats. A child may legitimately be silent for its
    /// whole bound over and over; it starts once. Four times over is past any honest launch and
    /// still finite, and it scales with how patient the run was asked to be, which is the knob
    /// somebody actually sets.
    /// </para>
    /// </summary>
    private const int StartBoundMultiple = 4;

    /// <summary>
    /// Watches the silence and cancels <paramref name="stall"/> when it passes <paramref name="bound"/>,
    /// or when the child has not started within <see cref="StartBoundMultiple"/> times that. The poll
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

                if (clock.Overdue(bound, bound * StartBoundMultiple))
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
        try
        {
            // Multiline, so a pattern anchored with ^ or $ means the start or end of a line of
            // output, which is how such a pattern is written and what it is matched against.
            return new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant, MatchBudget);
        }
        catch (ArgumentException ex)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"'{pattern}' is not {what}: {ex.Message}", ex);
        }
    }

    private static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;

    /// <summary>
    /// What a phase establishes from its child's output, read a line at a time as each line is kept: whether the success
    /// pattern matched, the timing marks, and the last lines. Nothing else of the output is held, so what this holds is the
    /// same whatever the child prints.
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
    /// A pattern that cannot be evaluated in time is never read as one that did not match. The success pattern's failure
    /// is kept and raised once the child has ended, as one over the whole output was; a timing pattern's leaves the phase
    /// unmeasured by it, and is said.
    /// </para>
    /// </remarks>
    /// <param name="success">The success pattern, or <see langword="null"/> where the phase declares none.</param>
    /// <param name="timings">The timing patterns.</param>
    private sealed class Reading(Regex? success, IReadOnlyList<(string Pattern, Regex Regex)> timings)
    {
        private readonly LastLinesRing _last = new(PhaseResult.TailLines);
        private readonly List<PhaseTiming>[] _marks = [.. timings.Select(_ => new List<PhaseTiming>())];
        private readonly long[] _matched = new long[timings.Count];
        private readonly bool[] _unreadable = new bool[timings.Count];
        private bool _witnessed;
        private RegexMatchTimeoutException? _unknown;

        /// <summary>The last lines read, in the order they came.</summary>
        public IReadOnlyList<string> LastLines => _last.ToList();

        /// <summary>Reads the next line the child printed, as the log keeps it.</summary>
        public void Read(string line)
        {
            foreach (var piece in LineSplitter.PiecesOf(line, ProcessRunner.LongestLine))
            {
                Witness(piece);
                Time(piece);
                _last.Add(piece);
            }
        }

        /// <summary>Whether the success pattern matched a line.</summary>
        /// <param name="pattern">The pattern as declared, named if it could not be evaluated.</param>
        /// <exception cref="HarnessException">The pattern could not be evaluated in time against a line it was read against.</exception>
        public bool Witnessed(string pattern)
        {
            if (_unknown is not null)
            {
                // Never reported as "did not match": a pattern that could not be evaluated is not
                // evidence either way, and a leg whose witness cannot be read must say so.
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"The pattern '{pattern}' took longer than {MatchBudget.TotalSeconds:0}s against a line of this phase's output, "
                    + "so whether it matched is unknown; it is written in a form that backtracks.",
                    _unknown);
            }

            return _witnessed;
        }

        /// <summary>
        /// Every timing mark kept, pattern by pattern, each in the order it appeared; and said to <paramref name="output"/>,
        /// for <paramref name="phase"/>, of each pattern that could not be read, or matched more often than was kept.
        /// </summary>
        public IReadOnlyList<PhaseTiming> Timings(IHarnessOutput output, string phase)
        {
            for (var index = 0; index < timings.Count; index++)
            {
                var pattern = timings[index].Pattern;

                if (_unreadable[index])
                {
                    // A timing mark never changes a verdict, so a pattern that cannot be read leaves
                    // this phase unmeasured rather than failing it. Said out loud, because a timing
                    // silently missing from a report reads as a phase that reported no timing.
                    output.Warn(phase, $"the timing pattern '{pattern}' could not be matched against this phase's output in time; no timing was taken from it");
                }
                else if (_matched[index] > MostTimings)
                {
                    // Said, because a report holding the first marks and none after reads as a phase
                    // that stopped reporting them.
                    output.Warn(
                        phase,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"the timing pattern '{pattern}' matched this phase's output {_matched[index]} times; only the first {MostTimings} were kept"));
                }
            }

            return [.. _marks.SelectMany(marks => marks)];
        }

        private void Witness(string line)
        {
            // Once matched, matched: a pattern is evidence the command ran the moment one line shows it.
            if (success is null || _witnessed || _unknown is not null)
            {
                return;
            }

            try
            {
                _witnessed = success.IsMatch(line);
            }
            catch (RegexMatchTimeoutException ex)
            {
                _unknown = ex;
            }
        }

        private void Time(string line)
        {
            for (var index = 0; index < timings.Count; index++)
            {
                if (_unreadable[index])
                {
                    continue;
                }

                try
                {
                    foreach (var match in timings[index].Regex.Matches(line).Cast<Match>())
                    {
                        if (++_matched[index] > MostTimings)
                        {
                            continue;
                        }

                        var value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
                        _marks[index].Add(new PhaseTiming(timings[index].Pattern, match.Value, value));
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Unmeasured by this pattern, as the warning will say: marks it found before are no
                    // account of the phase either.
                    _unreadable[index] = true;
                    _marks[index].Clear();
                }
            }
        }
    }

    /// <summary>
    /// How long the phase has run and how long it has been silent, both from the monotonic clock.
    /// A wall clock cannot answer either question on a host whose clock steps forward by 25 seconds
    /// every few seconds: the phase would look hung the moment the clock moved.
    /// </summary>
    private sealed class StallClock
    {
        /// <summary>What <see cref="_lastOutputMs"/> holds before the child has started.</summary>
        private const long NotYet = -1;

        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private long _lastOutputMs = NotYet;

        /// <summary>How long the phase has run.</summary>
        public TimeSpan Elapsed => _elapsed.Elapsed;

        /// <summary>
        /// How long since the last line on either stream, and nothing at all until the child is
        /// running. A process that has not started yet has not been quiet: the time spent starting
        /// one belongs to this tool, and counting it against the child makes a machine under load
        /// look like a hung command. That window has a bound of its own, in
        /// <see cref="Overdue(TimeSpan, TimeSpan)"/>.
        /// </summary>
        public TimeSpan Quiet
        {
            get
            {
                var last = Interlocked.Read(ref _lastOutputMs);

                return last == NotYet
                    ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(_elapsed.ElapsedMilliseconds - last);
            }
        }

        /// <summary>
        /// Whether the phase is past whichever bound is in force: how long the child may take to
        /// start before it has started, how long it may stay silent after.
        /// </summary>
        /// <param name="quiet">How long the child may say nothing.</param>
        /// <param name="starting">How long the child may take to start.</param>
        /// <remarks>
        /// Two bounds rather than one because they measure different things. Counting the launch as
        /// silence makes a slow machine read as a hung command, which is the defect this clock was
        /// written for; leaving the launch unmeasured makes an unreachable PATH entry a phase that
        /// never ends and never reports.
        /// </remarks>
        public bool Overdue(TimeSpan quiet, TimeSpan starting)
            => Interlocked.Read(ref _lastOutputMs) == NotYet
                ? _elapsed.Elapsed >= starting
                : Quiet >= quiet;

        /// <summary>
        /// Records that the child started, or said something. Called from the thread that starts the
        /// child and from the reader threads of both streams, so it is interlocked.
        /// </summary>
        public void Saw() => Interlocked.Exchange(ref _lastOutputMs, _elapsed.ElapsedMilliseconds);
    }
}
