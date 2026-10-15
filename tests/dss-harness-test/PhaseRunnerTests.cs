using System.Diagnostics;
using System.Text;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A phase is where a green result stops meaning anything: an exit code read through a wrapper, a
/// pattern that witnessed the harness's own log header, a hung command killed by a time budget
/// somebody guessed. Each rule is exercised here against a child whose output this test controls.
/// </summary>
public sealed class PhaseRunnerTests
{
    [Fact]
    public async Task APhasePasses_OnItsExitCodeAndItsOwnOutput()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("echo-args", temp.Combine("build.log"), "ready", "OK-MARKER") with { SuccessPattern = "OK-MARKER" },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Witnessed);
        Assert.True(result.Passed);
        Assert.Equal(LegVerdict.Passed, result.Verdict().Verdict);
        Assert.False(result.Stalled);
    }

    /// <summary>
    /// A pattern ending in $ matches a line a program ended with CRLF, as it matches one ended with LF: matched
    /// against the raw text, the carriage return stood between the line and the end, and a consumer's Windows
    /// leg was unwitnessed having printed exactly the line its pattern described - which its log showed.
    /// </summary>
    [Fact]
    public async Task APatternEndingInADollar_MatchesALineEndedWithCrlf()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("echo-crlf", temp.Combine("write.log"), "census: repaired 14 figure(s). Re-run to verify.") with
            {
                SuccessPattern = @"^census: repaired [0-9]+ figure\(s\)\. Re-run to verify\.$",
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Witnessed);
        Assert.Equal(LegVerdict.Passed, result.Verdict().Verdict);
    }

    [Fact]
    public async Task APatternThatOnlyMatchesWhatTheHarnessWrote_LeavesThePhaseUnwitnessed()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var log = temp.Combine("build.log");

        // "exec" is in the command line the log header echoes, and nowhere in the child's own
        // output: the child prints nothing at all. A pattern matched against the log would pass
        // this phase, which is how a command that never ran witnesses itself.
        var result = await Runner(factory).RunAsync(
            Child("exit", log, "0") with { SuccessPattern = "exec" },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Witnessed);
        Assert.False(result.Passed);
        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict().Verdict);
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(result.Verdict().Verdict));

        Assert.Contains("exec", await File.ReadAllTextAsync(log, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptySuccessPattern_IsRefused()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Runner(factory).RunAsync(
            Child("exit", temp.Combine("build.log"), "0") with { SuccessPattern = "  " },
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("matches anything", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStallBound_StopsAPhaseThatGoesQuiet()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var quiet = new QuietProcessRunner(TimeSpan.FromSeconds(30));

        // A phase that writes one line and then says nothing. Nothing about its total duration is
        // wrong; its silence is.
        //
        // Measured against a runner rather than a real child, for the reason PacedRunner below gives:
        // the stall clock starts before the child is spawned, so with a bound of one second this once
        // raced `dotnet` starting up, and on a loaded machine over a slow filesystem the race was
        // sometimes lost. That made the test report on how fast a process launches. Stopping a real
        // process tree is covered where it belongs, in ProcessRunnerTests.
        var result = await new PhaseRunner(quiet, factory.FileSystem, factory.Output).RunAsync(
            Child("stream", temp.Combine("test.log"), temp.Combine("never")) with { StallSeconds = 1 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Stalled, "the phase went quiet and was not stopped");
        Assert.False(result.Passed);
        Assert.Contains("hung", result.Verdict().Detail, StringComparison.Ordinal);

        // The claim without a clock in it: the phase was told to stop rather than left to finish on
        // its own. Had the bound never fired, the runner would have run its thirty seconds out and
        // reported that it was not stopped.
        Assert.True(quiet.Stopped, "the bound fired and the phase was not actually stopped");
    }

    /// <summary>
    /// A child that starts and then never says anything is the plainest hung command there is, and
    /// the one the bound must still catch now that the time before a child starts is not counted
    /// against it. It is caught because starting is itself something the clock is told about — take
    /// that away and silence never begins, so nothing is ever bounded.
    /// </summary>
    [Fact]
    public async Task AStallBound_StopsAPhaseThatNeverSaysAnythingAtAll()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var mute = new QuietProcessRunner(TimeSpan.FromSeconds(30), speaks: false);

        var result = await new PhaseRunner(mute, factory.FileSystem, factory.Output).RunAsync(
            Child("stream", temp.Combine("mute.log"), temp.Combine("never")) with { StallSeconds = 1 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Stalled, "a child that said nothing at all was never bounded");
        Assert.True(mute.Stopped, "the bound fired and the phase was not actually stopped");
    }

    /// <summary>
    /// Starting a process is this tool's own time, not the child being quiet. A machine under load
    /// can take longer over it than a short bound allows, and counting that as silence made a slow
    /// launch read as a hung command — which is exactly what made this suite's own stall test flake.
    /// </summary>
    [Fact]
    public async Task AStallBound_DoesNotCountTheTimeSpentStartingAChild()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // Three times the bound before the child is running, and then it finishes at once.
        var slow = new SlowToStartRunner(TimeSpan.FromSeconds(3));

        var result = await new PhaseRunner(slow, factory.FileSystem, factory.Output).RunAsync(
            Child("echo-args", temp.Combine("slow.log")) with { StallSeconds = 1 },
            TestContext.Current.CancellationToken);

        Assert.False(result.Stalled, "the time spent starting the child was counted as the child being quiet");
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// Not counted as silence is not the same as not counted. Resolving a program walks every entry
    /// of PATH, and one naming an unreachable share blocks for that platform's own timeout per
    /// entry; a working directory on a mount that has gone away does the same. Unbounded, that is a
    /// phase which never ends and never reports, and only the operator interrupting the run gets it
    /// back — with no verdict for the leg.
    /// </summary>
    [Fact]
    public async Task AChildThatNeverStartsAtAll_IsStillBounded()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // Far past four times the bound before the child is running.
        var never = new SlowToStartRunner(TimeSpan.FromSeconds(60));

        var result = await new PhaseRunner(never, factory.FileSystem, factory.Output).RunAsync(
            Child("echo-args", temp.Combine("never-starts.log")) with { StallSeconds = 1 },
            TestContext.Current.CancellationToken);

        Assert.True(result.Stalled, "a phase whose child never started was never bounded");
        Assert.True(result.Duration < TimeSpan.FromSeconds(30), $"it was bounded, but only after {result.Duration}");
    }

    [Fact]
    public async Task AStallBound_NeverFiresWhileOutputKeepsArriving()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        // Twenty lines, 200 milliseconds apart, against a bound of three seconds: no gap comes
        // near the bound, and the whole phase runs for longer than it. A wall-clock budget would
        // have killed it; a stall bound does not, which is the entire reason the bound is on
        // silence rather than on duration.
        //
        // The margin is fifteen times the gap on purpose. At one second it was under seven, and a
        // contended arm64 runner paused a single Task.Delay past the bound often enough to turn
        // this red — measuring that machine's scheduler rather than this rule. Fifteen times over,
        // a pause long enough to fire the bound is a stall by any reading.
        var runner = new PhaseRunner(
            new PacedRunner(TimeSpan.FromMilliseconds(200), lines: 20),
            factory.FileSystem,
            factory.Output);

        var result = await runner.RunAsync(
            Child("echo-args", temp.Combine("paced.log")) with { StallSeconds = 3 },
            TestContext.Current.CancellationToken);

        Assert.False(result.Stalled, "output was flowing and the phase was stopped anyway");
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Duration >= TimeSpan.FromSeconds(3), $"the phase ran for only {result.Duration}");
    }

    [Fact]
    public async Task AStallBoundOfZero_BoundsNothing()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("sleep", temp.Combine("sleep.log"), "300") with { StallSeconds = 0 },
            TestContext.Current.CancellationToken);

        Assert.False(result.Stalled);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task TimingPatterns_PullEveryMatchOutOfTheOutput()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("echo-args", temp.Combine("time.log"), "compile took 1.5s", "link took 12.25s") with
            {
                TimingPatterns = ["took ([0-9.]+)s"],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["1.5", "12.25"], result.Timings.Select(timing => timing.Value));
        Assert.All(result.Timings, timing => Assert.Equal("took ([0-9.]+)s", timing.Pattern));
    }

    /// <summary>
    /// The success pattern is matched against each line as it arrives, on either stream, and so never against two lines at
    /// once: a pattern that only matches across a line break witnesses nothing.
    /// </summary>
    [Fact]
    public async Task TheSuccessPattern_IsMatchedAgainstEachLine_OnEitherStream_NeverAcrossTwo()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var lines = new ScriptedLines(("compiling", false), ("all 12 checks passed", true), ("done", false));

        var onError = await new PhaseRunner(lines, factory.FileSystem, factory.Output).RunAsync(
            Child("exit", temp.Combine("a.log")) with { SuccessPattern = "^all [0-9]+ checks passed$" },
            TestContext.Current.CancellationToken);

        var across = await new PhaseRunner(lines, factory.FileSystem, factory.Output).RunAsync(
            Child("exit", temp.Combine("b.log")) with { SuccessPattern = "compiling\nall" },
            TestContext.Current.CancellationToken);

        Assert.True(onError.Witnessed);
        Assert.False(across.Witnessed);
        Assert.Equal(LegVerdict.Unwitnessed, across.Verdict().Verdict);
    }

    /// <summary>
    /// A success pattern that cannot be evaluated in time against a line is never read as one that did not match: what it
    /// could not decide is raised once the child has ended, with every line of it kept in the log and the exit line after.
    /// </summary>
    [Fact]
    public async Task ASuccessPatternThatCannotBeEvaluatedInTime_IsRaisedOnceTheChildHasEnded()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var log = temp.Combine("slow.log");
        var lines = new ScriptedLines((new string('a', 40) + "!", false), ("after it", false));

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => new PhaseRunner(lines, factory.FileSystem, factory.Output).RunAsync(
            Child("exit", log) with { SuccessPattern = "^(a+)+$" },
            TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("took longer than 5s against a line of this phase's output", refusal.Message, StringComparison.Ordinal);
        Assert.True(lines.Finished, "the child was not left to end");

        var kept = await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken);

        Assert.Equal("after it", kept[^2]);
        Assert.StartsWith("# exit 0 after ", kept[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// Timing marks are read from each line as it arrives, pattern by pattern; of a pattern that matches a flood, only the
    /// first are kept, and that is said, since a report holding the first marks and none after reads as a phase that
    /// stopped reporting them.
    /// </summary>
    [Fact]
    public async Task TimingPatterns_AreReadFromEachLine_AndKeepOnlyTheFirstMarksOfAFlood_SayingSo()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var flood = Enumerable.Range(0, PhaseRunner.MostTimings + 5).Select(index => ($"case {index} took {index}ms", false));
        var lines = new ScriptedLines([("link took 7s", true), .. flood, ("link took 9s", true)]);

        var result = await new PhaseRunner(lines, factory.FileSystem, factory.Output).RunAsync(
            Child("exit", temp.Combine("time.log")) with { TimingPatterns = ["took ([0-9]+)ms", "link took ([0-9]+)s"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(PhaseRunner.MostTimings + 2, result.Timings.Count);
        Assert.Equal(Enumerable.Range(0, PhaseRunner.MostTimings).Select(index => $"{index}"), result.Timings.Take(PhaseRunner.MostTimings).Select(timing => timing.Value));
        Assert.Equal(["7", "9"], result.Timings.Skip(PhaseRunner.MostTimings).Select(timing => timing.Value));
        Assert.Contains(
            $"the timing pattern 'took ([0-9]+)ms' matched this phase's output {PhaseRunner.MostTimings + 5} times; only the first {PhaseRunner.MostTimings} were kept",
            factory.StandardError.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("link took", factory.StandardError.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A timing pattern that cannot be read in time against a line leaves the phase unmeasured by it - none of its marks,
    /// not even those read before - and says so, while every other pattern keeps its marks and the phase its verdict.
    /// </summary>
    [Fact]
    public async Task ATimingPatternThatCannotBeReadInTime_LeavesThePhaseUnmeasuredByIt_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var lines = new ScriptedLines(("compile took 3s", false), (new string('a', 40) + "!", false), ("link took 9s", false));

        var result = await new PhaseRunner(lines, factory.FileSystem, factory.Output).RunAsync(
            Child("exit", temp.Combine("time.log")) with { TimingPatterns = ["^(a+)+$|compile took ([0-9]+)s", "link took ([0-9]+)s"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(["9"], result.Timings.Select(timing => timing.Value));
        Assert.Contains(
            "the timing pattern '^(a+)+$|compile took ([0-9]+)s' could not be matched against this phase's output in time; no timing was taken from it",
            factory.StandardError.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// A line longer than a line is kept in, carrying a secret where the line is cut, has that secret masked all the same:
    /// cut where the mask says no secret is parted, every piece is masked whole, in the log, in what the phase read and in
    /// its output read back.
    /// </summary>
    [Fact]
    public async Task ASecretInALineTooLongToKeepWhole_IsMaskedWhereverTheLineIsCut()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory(verbose: true);
        const string Secret = "s3cr3t-T0KEN-value";

        // The secret spans the first place the line would be cut, were it cut where it is full.
        var line = new string('x', ProcessRunner.LongestLine - 5) + Secret + new string('y', ProcessRunner.LongestLine) + Secret + "z";
        var file = temp.WriteFile("long.txt", "before\n" + line + "\nafter\n");
        var values = new Core.Runners.ActionValues(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["TOKEN"] = Secret });

        var result = await Runner(factory).RunAsync(
            Child("print-file", temp.Combine("long.log"), file) with { Mask = values, SuccessPattern = "after" },
            TestContext.Current.CancellationToken);

        var log = await File.ReadAllTextAsync(temp.Combine("long.log"), TestContext.Current.CancellationToken);
        var read = result.Output.Lines().ToList();

        Assert.DoesNotContain(Secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, factory.StandardOutput.ToString() + factory.StandardError, StringComparison.Ordinal);
        Assert.Equal(values.Redact(line), string.Concat(read.Skip(1).SkipLast(1)));
        Assert.Equal(["before", "after"], [read[0], read[^1]]);
        Assert.All(read, piece => Assert.True(piece.Length <= ProcessRunner.LongestLine, $"a piece of {piece.Length} characters"));
        Assert.True(result.Witnessed);
    }

    /// <summary>
    /// A line its mask makes longer than a line is kept in is read as the phase runs in the pieces its log is read back in:
    /// what the phase read of its output and what a later reader of it finds are the same lines.
    /// </summary>
    [Fact]
    public async Task ALineItsMaskLengthensPastALine_IsReadAsItsLogIsReadBack()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        const string Secret = "ab";

        // Every two characters masked as three: a line exactly as long as a line is kept in comes out half as long again.
        var line = string.Concat(Enumerable.Repeat(Secret, ProcessRunner.LongestLine / 2));
        var file = temp.WriteFile("short.txt", line + "\n");
        var values = new Core.Runners.ActionValues(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["TOKEN"] = Secret });

        var result = await Runner(factory).RunAsync(
            Child("print-file", temp.Combine("short.log"), file) with { Mask = values },
            TestContext.Current.CancellationToken);

        var read = result.Output.Lines().ToList();

        Assert.Equal(values.Redact(line), string.Concat(read));
        Assert.Equal(2, read.Count);
        Assert.Equal(read, result.LastLines);
    }

    [Fact]
    public async Task ChildOutput_GoesToTheLog_AndToTheConsoleOnlyWhenVerbose()
    {
        using var temp = new TempDirectory();
        var quiet = new HarnessFactory();
        var loud = new HarnessFactory(verbose: true);

        var quietLog = temp.Combine("quiet.log");
        var loudLog = temp.Combine("loud.log");

        await Runner(quiet).RunAsync(Child("echo-args", quietLog, "CHILD-LINE"), TestContext.Current.CancellationToken);
        await Runner(loud).RunAsync(Child("echo-args", loudLog, "CHILD-LINE"), TestContext.Current.CancellationToken);

        Assert.Contains("CHILD-LINE", await File.ReadAllTextAsync(quietLog, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("CHILD-LINE", await File.ReadAllTextAsync(loudLog, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        // Progress is one line per leg transition, not a stream of child output.
        Assert.DoesNotContain("CHILD-LINE", quiet.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("CHILD-LINE", loud.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingExitCode_IsReadFromTheProcess()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await Runner(factory).RunAsync(
            Child("exit", temp.Combine("fail.log"), "7"),
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal(LegVerdict.Failed, result.Verdict().Verdict);
        Assert.Null(result.Witnessed);
    }

    [Fact]
    public async Task AnHonestPhase_RecordsNoClockStep()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        using var clock = new ClockWatch();

        var result = await Runner(factory).RunAsync(
            Child("echo-args", temp.Combine("clock.log"), "quick") with { ClockStepToleranceMilliseconds = 2000 },
            TestContext.Current.CancellationToken);

        // What this machine's own clock did while the phase ran. On a clock that stepped, the phase
        // rightly records the step - measured in WSL, a run that took 26 seconds instead of a fraction of
        // one - and this case is not the one under test.
        Assert.SkipUnless(clock.Held, $"The phase did not run on an honest clock: {clock.Seen}.");

        // Both readings cover the same window, so on a machine whose clock is honest they agree. The
        // other side of this rule, a clock that steps mid-phase, is the next test, which steps the
        // runner's own wall clock rather than this machine's.
        Assert.False(result.ClockStepped);
        Assert.True(result.ClockDrift < TimeSpan.FromSeconds(1), $"wall and monotonic time disagreed by {result.ClockDrift}");
    }

    /// <summary>
    /// A wall clock that steps while a phase runs - by 25 seconds, as the measured host's does, and
    /// back as well as forward - disagrees with the monotonic clock by the step, and the phase says it
    /// spanned one. What stamps an object during a step can no longer be ordered against anything,
    /// and this is the reading everything downstream of that rests on.
    /// </summary>
    [Theory]
    [InlineData(25)]
    [InlineData(-25)]
    public async Task APhaseTheWallClockStepsDuring_RecordsTheStep(int seconds)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var clock = new SteppingClock();
        var step = TimeSpan.FromSeconds(seconds);

        var result = await new PhaseRunner(new SteppingRunner(clock, step), factory.FileSystem, factory.Output, clock).RunAsync(
            Child("echo-args", temp.Combine("clock.log"), "stepped") with { ClockStepToleranceMilliseconds = 2000 },
            TestContext.Current.CancellationToken);

        Assert.True(result.ClockStepped, $"a step of {step} went unrecorded; the phase measured a drift of {result.ClockDrift}");
        Assert.True(result.ClockDrift >= step.Duration() - TimeSpan.FromSeconds(1), $"a step of {step} measured as {result.ClockDrift}");
    }

    private static PhaseRunner Runner(HarnessFactory factory)
        => new(factory.ProcessRunner, factory.FileSystem, factory.Output);

    /// <summary>A phase that runs this assembly as a child, exactly as the process tests do.</summary>
    private static PhaseRequest Child(string mode, string logFile, params string[] arguments)
    {
        var request = TestHost.ChildRequest(mode, arguments);

        return new PhaseRequest
        {
            Leg = "win-msvc-release",
            Phase = "build",
            FileName = request.FileName,
            Arguments = request.Arguments,
            Environment = request.Environment,
            LogFile = logFile,
        };
    }

    /// <summary>
    /// A runner that prints <paramref name="lines"/>, each on standard error where it says so and standard output otherwise,
    /// and exits 0 having kept none of them, as the real runner keeps no more than a stream's end of what its caller reads.
    /// </summary>
    private sealed class ScriptedLines(params (string Line, bool Error)[] lines) : IProcessRunner
    {
        /// <summary>Whether every line was printed and the child ended.</summary>
        public bool Finished { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            request.OnStarted?.Invoke();

            foreach (var (line, error) in lines)
            {
                (error ? request.OnErrorLine : request.OnOutputLine)?.Invoke(line);
            }

            Finished = true;

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(5), TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>
    /// A runner that starts, steps <paramref name="clock"/> by <paramref name="step"/> while it runs,
    /// and exits 0 having said nothing.
    /// </summary>
    private sealed class SteppingRunner(SteppingClock clock, TimeSpan step) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            request.OnStarted?.Invoke();
            clock.Step(step);

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>
    /// A runner that takes <paramref name="launch"/> to get the child running and then finishes at
    /// once, so what the bound sees is a slow start rather than a silent child.
    /// </summary>
    private sealed class SlowToStartRunner(TimeSpan launch) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var watch = Stopwatch.StartNew();

            await Task.Delay(launch, cancellationToken);
            request.OnStarted?.Invoke();

            const string Line = "done";
            request.OnOutputLine?.Invoke(Line);

            return new ProcessResult(0, Line + "\n", string.Empty, watch.Elapsed, TimedOut: false);
        }

        public string? FindExecutable(string command) => command;
    }

    /// <summary>
    /// A runner that emits lines at a fixed cadence, so the stall bound can be measured against
    /// output that keeps arriving rather than against a child whose timing the machine decides.
    /// </summary>
    /// <param name="gap">How long between lines.</param>
    /// <param name="lines">How many lines to write.</param>
    private sealed class PacedRunner(TimeSpan gap, int lines) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var captured = new StringBuilder();
            var watch = Stopwatch.StartNew();

            // As the real runner does, and before any line: without it this stands for a runner
            // that never reports the start, which is a different thing to measure.
            request.OnStarted?.Invoke();

            try
            {
                for (var index = 0; index < lines; index++)
                {
                    await Task.Delay(gap, cancellationToken);

                    var line = $"line {index}";
                    captured.Append(line).Append('\n');
                    request.OnOutputLine?.Invoke(line);
                }
            }
            catch (OperationCanceledException)
            {
                // What the real runner reports when it stopped the child: the exit code is
                // meaningless and the phase is marked as stopped.
                return new ProcessResult(-1, captured.ToString(), string.Empty, watch.Elapsed, TimedOut: true);
            }

            return new ProcessResult(0, captured.ToString(), string.Empty, watch.Elapsed, TimedOut: false);
        }

        public string? FindExecutable(string command) => command;
    }
}
