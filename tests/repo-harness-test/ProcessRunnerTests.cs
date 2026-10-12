using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// The process runner is exercised against this test assembly started as a child, whose
/// output, timing and descendants each test controls exactly. No shell or other tool is
/// involved, so every test means the same thing on every platform.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static ProcessRunner CreateRunner() => new(new HostPlatform(), FilePermissionsFactory.Create());

    [Fact]
    public async Task RunAsync_PassesEveryArgumentThroughUnchanged()
    {
        // Each of these is mangled by at least one way of building a command line by hand.
        string[] arguments = ["plain", "with space", "", "quote\"inside", "trailing\\", "tab\there", "a'b"];

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-args", arguments),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(arguments.Select(argument => "[" + argument + "]"), Lines(result.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_DecodesOutputAsUtf8_OnEveryPlatform()
    {
        // git writes paths as UTF-8. Decoded with the Windows console code page instead, a
        // repository under C:\Users\João would be reported at a path that does not exist.
        string[] arguments = ["João", "ação", "日本語"];

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-args", arguments),
            TestContext.Current.CancellationToken);

        Assert.Equal(arguments.Select(argument => "[" + argument + "]"), Lines(result.StandardOutput));
    }

    [Fact]
    public async Task RunAsync_ReportsAFailingExitCode_WithoutThrowing()
    {
        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("exit", "3"),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_DeliversEachLineWhileTheProcessIsStillRunning()
    {
        using var temp = new TempDirectory();
        var signal = temp.Combine("signal");
        var errorLines = new List<string>();

        var request = TestHost.ChildRequest("stream", signal) with
        {
            // The child finishes only after this file appears, so it can succeed only if
            // "first" reached this callback while the child was still running.
            OnOutputLine = line =>
            {
                if (line == "first")
                {
                    File.WriteAllText(signal, "received");
                }
            },
            OnErrorLine = line =>
            {
                lock (errorLines)
                {
                    errorLines.Add(line);
                }
            },
        };

        var result = await CreateRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, "Output arrived only after the child exited (exit code 3 means it gave up waiting).");
        Assert.Equal(["first", "second"], Lines(result.StandardOutput));
        Assert.Equal(["problem"], Lines(result.StandardError));
        Assert.Equal(["problem"], errorLines);
    }

    /// <summary>
    /// A stream kept as a tail is kept as its last characters and no more, however much the child prints, while every line
    /// of it still reaches the caller as it comes; one kept whole - what git's answers and a probe's need - is kept whole.
    /// Each stream is kept as asked for it: standard error a tail beside standard output whole, as a relay of a host's
    /// command keeps them.
    /// </summary>
    [Fact]
    public async Task RunAsync_KeepsOnlyTheEndOfAStreamKeptAsATail_WhileEveryLineArrives()
    {
        const int Count = 1000;
        const int Length = 200;

        var printed = string.Concat(Enumerable.Range(0, Count).Select(index => TestChild.FloodLine(index, Length) + "\n"));
        var arrived = new List<string>();

        var tail = await CreateRunner().RunAsync(
            TestHost.ChildRequest("flood", $"{Count}", $"{Length}") with
            {
                OnOutputLine = arrived.Add,
                OutputKept = StreamKept.Tail,
            },
            TestContext.Current.CancellationToken);

        Assert.True(printed.Length > ProcessRunner.TailLength, "the child printed no more than the end that is kept");
        Assert.Equal(printed[^ProcessRunner.TailLength..], tail.StandardOutput);
        Assert.Equal(Enumerable.Range(0, Count).Select(index => TestChild.FloodLine(index, Length)), arrived);

        var whole = await CreateRunner().RunAsync(
            TestHost.ChildRequest("flood", $"{Count}", $"{Length}") with { OnOutputLine = _ => { } },
            TestContext.Current.CancellationToken);

        Assert.Equal(printed, whole.StandardOutput);

        var errorArrived = new List<string>();

        var errorTail = await CreateRunner().RunAsync(
            TestHost.ChildRequest("flood-error", $"{Count}", $"{Length}") with
            {
                OnErrorLine = errorArrived.Add,
                ErrorKept = StreamKept.Tail,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(printed[^ProcessRunner.TailLength..], errorTail.StandardError);
        Assert.Equal(Enumerable.Range(0, Count).Select(index => TestChild.FloodLine(index, Length)), errorArrived);
    }

    /// <summary>
    /// A line handler that fails - of the output or of the error output - is handed no more lines, and its stream is still
    /// read to the end: a child writing more than a pipe holds would otherwise block on a pipe nobody reads, and one whose
    /// input is held open, as a host's agent's is, would never end. What the handler raised is raised once the child has
    /// gone, and a handler of the other stream is handed every line of it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ALineHandlerThatFails_IsRaisedOnceTheChildHasGone_NeverLeavingItBlockedOnItsPipe(bool ofErrors)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(60));
        var handed = 0;
        var other = 0;

        void Failing(string line)
        {
            handed++;
            throw new InvalidOperationException("a line this caller cannot read");
        }

        void Counting(string line) => Interlocked.Increment(ref other);

        var raised = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(
            TestHost.ChildRequest("flood-both", "20000", "200") with
            {
                StandardInput = string.Empty,
                HoldStandardInputOpen = true,
                OnOutputLine = ofErrors ? Counting : Failing,
                OnErrorLine = ofErrors ? Failing : Counting,
            },
            stop.Token));

        Assert.False(stop.IsCancellationRequested, "the child blocked on a pipe nobody read, until the run was stopped");
        Assert.Equal("a line this caller cannot read", raised.Message);
        Assert.Equal(1, handed);
        Assert.Equal(20000, other);
    }

    /// <summary>
    /// Handlers of both streams that fail are both raised: together where they failed differently - saying another thing,
    /// or the same thing as another kind of failure; one said and the other lost would send whoever read it after half
    /// the trouble - and once where they raised the same, one reason met on each.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RunAsync_HandlersOfBothStreamsThatFail_AreBothRaised(bool sayingAnotherThing, bool ofAnotherKind)
    {
        const string Output = "an output line this caller cannot read";
        const string Error = "an error line this caller cannot read";
        var differently = sayingAnotherThing || ofAnotherKind;
        var said = sayingAnotherThing ? Error : Output;

        Exception Failure() => ofAnotherKind ? new FormatException(said) : new InvalidOperationException(said);

        var raised = await Assert.ThrowsAnyAsync<Exception>(() => CreateRunner().RunAsync(
            TestHost.ChildRequest("flood-both", "100", "20") with
            {
                OnOutputLine = _ => throw new InvalidOperationException(Output),
                OnErrorLine = _ => throw Failure(),
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(differently, raised is AggregateException);
        Assert.Equal(
            differently ? [Output, said] : [Output],
            raised is AggregateException both ? both.InnerExceptions.Select(inner => inner.Message) : [raised.Message]);
    }

    /// <summary>
    /// A line that never ends, on a stream kept as a tail, arrives in pieces of at most the longest line handed on - cut
    /// where the caller says, where it says - each as soon as it is complete; on a stream kept whole it arrives whole, as
    /// an answer written on one line has to.
    /// </summary>
    [Fact]
    public async Task RunAsync_HandsOnALineThatNeverEnds_InPieces_OnAStreamKeptAsATail()
    {
        const int Giant = 100_000;
        var pieces = new List<string>();

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("flood", "0", "200", $"{Giant}") with { OnOutputLine = pieces.Add, OutputKept = StreamKept.Tail },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [ProcessRunner.LongestLine, ProcessRunner.LongestLine, ProcessRunner.LongestLine, Giant - (3 * ProcessRunner.LongestLine)],
            pieces.Select(piece => piece.Length));
        Assert.Equal(TestChild.GiantLine(Giant), string.Concat(pieces));
        Assert.Equal(TestChild.GiantLine(ProcessRunner.TailLength), result.StandardOutput);

        var cutShort = new List<string>();

        await CreateRunner().RunAsync(
            TestHost.ChildRequest("flood", "0", "200", $"{Giant}") with
            {
                OnOutputLine = cutShort.Add,
                OutputKept = StreamKept.Tail,
                CutLine = full => full.Length - 100,
            },
            TestContext.Current.CancellationToken);

        Assert.All(cutShort.SkipLast(1), piece => Assert.Equal(ProcessRunner.LongestLine - 100, piece.Length));
        Assert.Equal(TestChild.GiantLine(Giant), string.Concat(cutShort));

        var whole = new List<string>();

        await CreateRunner().RunAsync(
            TestHost.ChildRequest("flood", "0", "200", $"{Giant}") with { OnOutputLine = whole.Add },
            TestContext.Current.CancellationToken);

        Assert.Equal([TestChild.GiantLine(Giant)], whole);
    }

    [Fact]
    public async Task RunAsync_StopsAProcessThatExceedsItsBudget()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("sleep", "120000") with { Timeout = TimeSpan.FromMilliseconds(500) },
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"The budget was enforced only after {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_StopsTheWholeProcessTree()
    {
        using var temp = new TempDirectory();
        var processIdFile = temp.Combine("grandchild.pid");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = CreateRunner().RunAsync(
            TestHost.ChildRequest("spawn-grandchild", processIdFile, "120000"),
            cancellation.Token);

        var grandchildId = await WaitForProcessIdAsync(processIdFile, TestContext.Current.CancellationToken);
        using var grandchild = Process.GetProcessById(grandchildId);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // A grandchild left running keeps what it inherited busy, a build tree or the very
        // output pipe the run was reading, long after the run was abandoned.
        Assert.True(
            grandchild.WaitForExit(TimeSpan.FromSeconds(30)),
            $"Grandchild process {grandchildId} was still running after the run was cancelled.");
    }

    [Fact]
    public async Task RunAsync_AppliesEnvironmentOverrides()
    {
        const string Name = "REPO_HARNESS_TEST_APPLIED";
        var request = TestHost.ChildRequest("print-env", Name);

        var result = await CreateRunner().RunAsync(
            request with { Environment = WithVariable(request, Name, "applied") },
            TestContext.Current.CancellationToken);

        Assert.Equal("applied", result.TrimmedOutput);
    }

    [Fact]
    public async Task RunAsync_RemovesAnInheritedVariable_WhenGivenNull()
    {
        // A name no other test uses, so setting it on this process cannot affect them.
        const string Name = "REPO_HARNESS_TEST_REMOVED";
        Environment.SetEnvironmentVariable(Name, "inherited");

        try
        {
            var request = TestHost.ChildRequest("print-env", Name);

            var result = await CreateRunner().RunAsync(
                request with { Environment = WithVariable(request, Name, null) },
                TestContext.Current.CancellationToken);

            Assert.Equal("<unset>", result.TrimmedOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    [Fact]
    public async Task RunAsync_WritesStandardInput_AndClosesIt()
    {
        // The child reads its input to the end, so it can finish only once the input is closed.
        // The text carries what a request to another host carries: spaces, quotes, non-ASCII; and
        // nothing before it, a byte order mark included, which a child reading bytes takes for text.
        const string Input = "{\"arguments\":[\"with space\",\"quote\\\"inside\",\"João\"]}\nsecond line";

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-stdin") with { StandardInput = Input, Timeout = TimeSpan.FromSeconds(60) },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut, "The child never saw the end of its input.");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("[" + Input + "]", result.StandardOutput.TrimEnd('\n'));
    }

    /// <summary>
    /// An input a writer writes as it makes it reaches the child whole and in order, and is closed once written, as text
    /// is: the child, reading to the end, finishes only once it is.
    /// </summary>
    [Fact]
    public async Task RunAsync_WritesAWrittenInputWhole_AndClosesIt()
    {
        var pieces = Enumerable.Range(0, 64).Select(index => $"piece {index} of João's input;").ToList();

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-stdin") with
            {
                StandardInput = ChildInput.WrittenBy(stream =>
                {
                    foreach (var piece in pieces)
                    {
                        stream.Write(Encoding.UTF8.GetBytes(piece));
                    }
                }),
                Timeout = TimeSpan.FromSeconds(60),
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut, "The child never saw the end of its input.");
        Assert.Equal("[" + string.Concat(pieces) + "]", result.StandardOutput.TrimEnd('\n'));
    }

    /// <summary>
    /// A writer that fails part way has the input closed though it was to be held open - the child would wait for the
    /// rest, which never comes - and its failure raised once the child has gone, never taken for a child that stopped
    /// reading.
    /// </summary>
    [Fact]
    public async Task RunAsync_ClosesAnInputItsWriterCouldNotFinish_AndRaisesWhyOnceTheChildHasGone()
    {
        using var temp = new TempDirectory();
        var read = temp.Combine("read.txt");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(
            TestHost.ChildRequest("stdin-to-file", read) with
            {
                StandardInput = ChildInput.WrittenBy(stream =>
                {
                    stream.Write("request"u8);
                    throw new InvalidOperationException("the writer stopped");
                }),
                HoldStandardInputOpen = true,
                Timeout = TimeSpan.FromSeconds(60),
            },
            TestContext.Current.CancellationToken));

        Assert.Equal("the writer stopped", failure.Message);

        // Written only by a child that saw the end of its input, and ended on its own rather than by the budget.
        Assert.Equal("request", await File.ReadAllTextAsync(read, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Text is written as UTF-8 a piece at a time, so a character whose two halves fall either side of where one piece
    /// ends and the next begins still reaches the child whole.
    /// </summary>
    [Fact]
    public async Task RunAsync_WritesTextAPieceAtATime_WithACharacterAcrossTwoPiecesWhole()
    {
        var input = new string('a', (16 * 1024) - 1) + "\U0001F600" + new string('b', 40 * 1024);

        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-stdin") with { StandardInput = input, Timeout = TimeSpan.FromSeconds(60) },
            TestContext.Current.CancellationToken);

        Assert.Equal("[" + input + "]", result.StandardOutput.TrimEnd('\n'));
    }

    [Fact]
    public async Task RunAsync_GivesAChildAnInputThatEndsAtOnce_WhenTheRequestHasNone()
    {
        // Never this process's own input: a child reading it would take what was meant for the harness, and on
        // Windows a child inheriting an input this process is reading at that moment can hang as it starts.
        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("echo-stdin") with { Timeout = TimeSpan.FromSeconds(60) },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut, "The child was left reading an input that never ended.");
        Assert.Equal("[]", result.StandardOutput.TrimEnd('\n'));
    }

    [Theory]
    [InlineData(false, "ended")]
    [InlineData(true, "held")]
    public async Task RunAsync_ClosesStandardInputOnceWritten_UnlessAskedToHoldItOpen(bool hold, string expected)
    {
        // A child that watches for the end of its input learns from it that this process has gone, which
        // only means something if the input stays open while this process is still there.
        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("read-line-then-watch", "1500") with
            {
                StandardInput = "request\n",
                HoldStandardInputOpen = hold,
                Timeout = TimeSpan.FromSeconds(60),
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.Equal(["[request]", expected], Lines(result.StandardOutput));
    }

    /// <summary>
    /// A beat is written on an input held open, a line each time its interval passes, for as long as the child runs, so
    /// that a child counting on it knows this process is still there; an input that is not held open is written none.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WritesABeatOnAnInputHeldOpen_UntilTheChildExits_AndNoneOnOneThatIsNot(bool hold)
    {
        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("read-lines-for", "1500") with
            {
                StandardInput = "request\n",
                HoldStandardInputOpen = hold,
                StandardInputBeat = new InputBeat(TimeSpan.FromMilliseconds(100), "still here"),
                Timeout = TimeSpan.FromSeconds(60),
            },
            TestContext.Current.CancellationToken);

        var lines = Lines(result.StandardOutput);

        Assert.False(result.TimedOut);
        Assert.Null(result.BeatLost);
        Assert.Equal("[request]", lines[0]);

        if (hold)
        {
            // One to an interval for as long as the child ran, however long a busy machine took to start it: never
            // none, and never more than the intervals that passed.
            Assert.InRange(lines.Count - 1, 1, (int)(result.Duration / TimeSpan.FromMilliseconds(100)) + 1);
            Assert.All(lines.Skip(1), line => Assert.Equal("[still here]", line));
        }
        else
        {
            Assert.Single(lines);
        }
    }

    /// <summary>
    /// A beat stops when the child has gone, however long its interval: a run is never held for a beat still to be
    /// written, and a child that exited took no beat that was lost.
    /// </summary>
    [Fact]
    public async Task RunAsync_StopsBeating_OnceTheChildHasGone_HoweverLongTheBeatsInterval()
    {
        var result = await CreateRunner().RunAsync(
            TestHost.ChildRequest("sleep", "200") with
            {
                StandardInput = "request\n",
                HoldStandardInputOpen = true,
                StandardInputBeat = new InputBeat(TimeSpan.FromMinutes(10), "still here"),
                Timeout = TimeSpan.FromMinutes(5),
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.BeatLost);
        Assert.InRange(result.Duration, TimeSpan.Zero, TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// A beat that can no longer be written to a child still running is said, with why, and the input is closed: the
    /// child - a carrier to another machine - has stopped hearing this process and goes on, and whatever counts on the
    /// beat at its far end will give this process up. Passed over, as the child ceasing to read its input always was,
    /// that left whoever started it with a run stopped for a machine that had gone, and nothing here to say why.
    /// A child that takes its input with it as it exits has lost no beat.
    /// </summary>
    [Theory]
    [InlineData("4000", true)]
    [InlineData("300", false)]
    public async Task RunAsync_SaysABeatWasLost_WhereAChildStillRunningStoppedTakingIt(string runsFor, bool lost)
    {
        // A child that takes its request, closes its own end of its input and goes on: this build's own on Windows, and
        // a shell elsewhere, where the runtime keeps a second descriptor of its input that closing the first does not
        // close. It takes the request first because one it closed its input on is the child ceasing to read, not a beat.
        var seconds = (int.Parse(runsFor, CultureInfo.InvariantCulture) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var child = OperatingSystem.IsWindows()
            ? TestHost.ChildRequest("close-input-then-sleep", runsFor)
            : new ProcessRequest { FileName = "sh", Arguments = ["-c", $"read request; exec <&-; sleep {seconds}"] };

        var result = await CreateRunner().RunAsync(
            child with
            {
                StandardInput = "request\n",
                HoldStandardInputOpen = true,
                StandardInputBeat = new InputBeat(TimeSpan.FromMilliseconds(100), "still here"),
                Timeout = TimeSpan.FromMinutes(5),
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(lost, result.BeatLost is { Length: > 0 });
    }

    /// <summary>
    /// A start that fails as a program being written does is made again until it succeeds, where the system refuses to
    /// start one held so: a process forked while the program was written holds it for a moment, and a program written
    /// and closed a line earlier then failed to start.
    /// </summary>
    [Fact]
    public async Task AStartThatFailsAsAProgramBeingWrittenDoes_IsMadeAgain_UntilItSucceeds()
    {
        var tries = 0;

        await ProcessRunner.StartAsync(
            () =>
            {
                if (++tries < 3)
                {
                    throw new System.ComponentModel.Win32Exception(ProcessRunner.TextFileBusy);
                }
            },
            busyPasses: true,
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, tries);
    }

    /// <summary>
    /// A program still held once the grace is spent fails as what it is; any other failure is the start's own at once,
    /// as is that one where the system gives the number another meaning; and the waiting between two tries stops when
    /// the run is cancelled.
    /// </summary>
    [Fact]
    public async Task AStartStillFailingOnceTheGraceIsSpent_OrFailingAnotherWay_IsItsOwnFailure()
    {
        var token = TestContext.Current.CancellationToken;
        var tries = 0;

        void Busy()
        {
            tries++;
            throw new System.ComponentModel.Win32Exception(ProcessRunner.TextFileBusy);
        }

        var spent = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
            () => ProcessRunner.StartAsync(Busy, busyPasses: true, TimeSpan.FromMilliseconds(100), token));

        Assert.Equal(ProcessRunner.TextFileBusy, spent.NativeErrorCode);
        Assert.InRange(tries, 2, 50);

        tries = 0;
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
            () => ProcessRunner.StartAsync(Busy, busyPasses: false, TimeSpan.FromSeconds(30), token));
        Assert.Equal(1, tries);

        tries = 0;
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
            () => ProcessRunner.StartAsync(
                () =>
                {
                    tries++;
                    throw new System.ComponentModel.Win32Exception(2);
                },
                busyPasses: true,
                TimeSpan.FromSeconds(30),
                token));
        Assert.Equal(1, tries);

        using var cancelled = new CancellationTokenSource();
        tries = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessRunner.StartAsync(
                () =>
                {
                    cancelled.Cancel();
                    Busy();
                },
                busyPasses: true,
                TimeSpan.FromSeconds(30),
                cancelled.Token));
        Assert.Equal(1, tries);
    }

    /// <summary>
    /// On a system that starts no program held open for writing, one still held is started once it is let go, by the
    /// runner itself: the whole of it, with a real program and a real hold.
    /// </summary>
    [Fact]
    public async Task RunAsync_StartsAProgramStillHeldForWriting_OnceItIsLetGo()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Only Linux refuses to start a program something holds open for writing.");
        }

        using var temp = new TempDirectory();
        var program = TestHost.StartableProgram(temp.Path, "held");
        Task<ProcessResult> running;

        using (new FileStream(program, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            running = CreateRunner().RunAsync(
                new ProcessRequest { FileName = program, Timeout = TimeSpan.FromSeconds(30) },
                TestContext.Current.CancellationToken);

            await Task.WhenAny(running, Task.Delay(300, TestContext.Current.CancellationToken));
        }

        // Started while it was held, on a kernel that refuses none: there is then nothing here to see.
        Assert.Equal(0, (await running).ExitCode);
    }

    [Fact]
    public async Task RunAsync_ReportsAMissingExecutable_AsNotFound()
    {
        await Assert.ThrowsAsync<ExecutableNotFoundException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = "definitely-not-a-real-tool-xyzzy" },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_ReportsAFileThatIsNotAProgram_AsUnableToStart_RatherThanAsMissing()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile(OperatingSystem.IsWindows() ? "not-a-program.exe" : "not-a-program", "plain text\n");
        MakeExecutable(file);

        // Exactly this type: "not found" would send the reader after a file that is there.
        var exception = await Assert.ThrowsAsync<ProgramStartException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = file },
            TestContext.Current.CancellationToken));

        Assert.Contains("could not be started", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task RunAsync_ReportsAScriptWhoseInterpreterIsMissing_AsUnableToStart_OnLinuxAndMacOs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows starts no script through the line naming its interpreter.");

        using var temp = new TempDirectory();
        var script = temp.WriteFile("script", "#!/definitely/not/an/interpreter\n");
        MakeExecutable(script);

        // Exactly this type, never the not-found one: the script is there, and "not found" would send the
        // reader after a file that is.
        var exception = await Assert.ThrowsAsync<ProgramStartException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = script },
            TestContext.Current.CancellationToken));

        // Which error a system gives for an interpreter that is not there is its own to choose, and they do
        // not all choose alike: most report the file missing, with the same error as for a missing program,
        // and that reading is explained rather than passed on as it stands, because the script itself is
        // there; one that reports anything else has its own words passed on. Either way the script is named
        // and said not to have started, and a failure here says what the system reported, since only the
        // system can say why it chose it.
        const int fileMissing = 2;
        var reported = (exception.InnerException as Win32Exception)?.NativeErrorCode;

        Assert.Contains(script, exception.Message, StringComparison.Ordinal);
        Assert.True(
            exception.Message.Contains(
                reported == fileMissing ? "interpreter or loader" : "could not be started: ",
                StringComparison.Ordinal),
            $"The system reported error {reported}, and the script's failure reads: {exception.Message}");
    }

    [Fact]
    public async Task RunAsync_NeverStartsAProgram_FromBesideTheRunningExecutable()
    {
        // Left to the runtime, a name is looked for beside the running executable, and then in the current
        // directory, before PATH. This test's own executable is beside it and on no PATH, so a runner that
        // looked there would start it. The current directory is not changed to exercise that lookup too:
        // every test in the run shares it.
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? string.Empty;
        Assert.SkipWhen(name.Length == 0 || CreateRunner().FindExecutable(name) is not null, $"'{name}' is on PATH in this run.");

        await Assert.ThrowsAsync<ExecutableNotFoundException>(() => CreateRunner().RunAsync(
            new ProcessRequest { FileName = name, Arguments = ["--help"], Timeout = TimeSpan.FromSeconds(60) },
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A name is one variable whatever case it is spelled in, as the configuration's rule says. On
    /// Linux and macOS, where the system tells spellings apart, a request that sets 'Path' sets the
    /// PATH this machine already has, rather than a second variable beside it that no program reads.
    /// </summary>
    [Fact]
    public async Task ANameSpelledInAnotherCase_SetsTheVariableThisMachineAlreadyHas()
    {
        // Ahead of the PATH this process has, so the child can still be started by name.
        var configured = Path.Combine(Path.GetTempPath(), "rh-configured-bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var child = TestHost.ChildRequest("print-env", "PATH");
        var request = child with
        {
            Environment = new Dictionary<string, string?>(child.Environment, StringComparer.Ordinal) { ["Path"] = configured },
        };

        var result = await CreateRunner().RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(configured, result.StandardOutput.Trim());
    }

    /// <summary>
    /// A value reaches every spelling of its name this machine has, and the one written: on Linux
    /// and macOS programs differ in which they read - curl reads http_proxy, others HTTP_PROXY - so
    /// folded into one, the other lost it. A name removed is removed in every spelling.
    /// </summary>
    [Fact]
    public async Task AValue_ReachesEverySpellingOfItsName_AndARemovedNameGoesFromAll()
    {
        var inherited = "RH_SPELLING_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var written = inherited.ToLowerInvariant();

        Environment.SetEnvironmentVariable(inherited, "inherited");

        try
        {
            async Task<string> SeenAs(string name, string? value)
            {
                var child = TestHost.ChildRequest("print-env", name);
                var request = child with
                {
                    Environment = new Dictionary<string, string?>(child.Environment, StringComparer.Ordinal) { [written] = value },
                };

                return (await CreateRunner().RunAsync(request, TestContext.Current.CancellationToken)).StandardOutput.Trim();
            }

            Assert.Equal("configured", await SeenAs(inherited, "configured"));
            Assert.Equal("configured", await SeenAs(written, "configured"));
            Assert.Equal("<unset>", await SeenAs(inherited, null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(inherited, null);
        }
    }

    [Fact]
    public async Task RunAsync_ReportsAMissingWorkingDirectory_AsThat_RatherThanAsAMissingExecutable()
    {
        // On Linux and macOS the operating system reports both with the same error number.
        using var temp = new TempDirectory();
        var missing = temp.Combine("gone");

        // The program could not start, which every reader names: a leg already running fails, and
        // a command ends as one whose program never ran - and never as a missing executable.
        var exception = await Assert.ThrowsAsync<ProgramStartException>(() => CreateRunner().RunAsync(
            TestHost.ChildRequest("exit", "0") with { WorkingDirectory = missing },
            TestContext.Current.CancellationToken));

        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
        Assert.Contains("working directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindExecutable_LocatesGit()
    {
        // git is a hard prerequisite of the harness, so its absence would be a
        // meaningful failure rather than a flaky test.
        Assert.NotNull(CreateRunner().FindExecutable("git"));
    }

    [Fact]
    public void FindExecutable_ReturnsNull_ForAnUnknownCommand()
    {
        Assert.Null(CreateRunner().FindExecutable("definitely-not-a-real-tool-xyzzy"));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void FindExecutable_RequiresAnExecuteBit_OnLinuxAndMacOs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows has no execute bit; the extension decides.");

        using var temp = new TempDirectory();
        var tool = temp.WriteFile("tool", "#!/bin/sh\n");

        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Null(CreateRunner().FindExecutable(tool));

        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var found = CreateRunner().FindExecutable(tool);

        Assert.NotNull(found);
        PathAssert.Same(tool, found);
    }

    [Fact]
    public void FindExecutable_OnWindows_FindsOnlyTheProgramThatWouldStart()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows decides by extension what a name starts.");

        using var temp = new TempDirectory();
        temp.WriteFile("tool.cmd", "@echo off\r\n");

        // Starting "tool" never runs tool.cmd: Windows adds .exe to a name without an extension, and
        // nothing else.
        Assert.Null(CreateRunner().FindExecutable(temp.Combine("tool")));

        var program = temp.WriteFile("tool.exe", string.Empty);
        var found = CreateRunner().FindExecutable(temp.Combine("tool"));

        Assert.NotNull(found);
        PathAssert.Same(program, found);
    }

    [Fact]
    public void ProgramOnPath_TakesTheFirstPathDirectoryThatHasTheProgram()
    {
        var first = Path.Combine(TestHost.TemporaryRoot, "first");
        var second = Path.Combine(TestHost.TemporaryRoot, "second");
        var third = Path.Combine(TestHost.TemporaryRoot, "third");
        string[] present = [Path.Combine(second, "tool"), Path.Combine(third, "tool")];

        var found = ProcessRunner.ProgramOnPath(
            "tool",
            string.Join(Path.PathSeparator, first, second, third),
            windows: false,
            path => present.Contains(path));

        Assert.Equal(Path.Combine(second, "tool"), found);
    }

    [Fact]
    public void ProgramOnPath_NeverLooksInTheCurrentDirectory_OrBesideTheRunningExecutable()
    {
        // Both are where the runtime would look before PATH; the current directory is usually the
        // repository, so a file committed there under a tool's name would run instead of the tool.
        string[] present = [Path.Combine(Environment.CurrentDirectory, "tool"), Path.Combine(AppContext.BaseDirectory, "tool")];

        Assert.Null(ProcessRunner.ProgramOnPath("tool", Path.Combine(TestHost.TemporaryRoot, "bin"), windows: false, path => present.Contains(path)));
        Assert.Null(ProcessRunner.ProgramOnPath("tool", pathVariable: null, windows: false, path => present.Contains(path)));
    }

    [Theory]
    [InlineData("tool", "tool.exe")]
    [InlineData("tool.exe", "tool.exe")]
    [InlineData("tool.cmd", "tool.cmd")]
    public void ProgramOnPath_OnWindows_AddsOnlyExe_ToANameWithoutAnExtension(string name, string expected)
    {
        var directory = Path.Combine(TestHost.TemporaryRoot, "bin");
        string[] present = [Path.Combine(directory, "tool.bat"), Path.Combine(directory, "tool.cmd"), Path.Combine(directory, "tool.exe")];

        Assert.Equal(Path.Combine(directory, expected), ProcessRunner.ProgramOnPath(name, directory, windows: true, path => present.Contains(path)));
    }

    [Fact]
    public void ProgramOnPath_OnWindows_NeverFindsABatchFile_ForABareName()
    {
        // cmd.exe parses a batch file's arguments a second time, so arguments passed one by one would not
        // arrive as they were passed.
        var directory = Path.Combine(TestHost.TemporaryRoot, "bin");
        string[] present = [Path.Combine(directory, "tool.bat"), Path.Combine(directory, "tool.cmd")];

        Assert.Null(ProcessRunner.ProgramOnPath("tool", directory, windows: true, path => present.Contains(path)));
    }

    private static List<string> Lines(string text)
        => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'))];

    /// <summary>Gives a file every execute bit on Linux and macOS; Windows decides by extension alone.</summary>
    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }

    private static Dictionary<string, string?> WithVariable(ProcessRequest request, string name, string? value)
        => new(request.Environment, StringComparer.Ordinal) { [name] = value };

    private static async Task<int> WaitForProcessIdAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)
                && int.TryParse(File.ReadAllText(path), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return id;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"The child never recorded its grandchild's process id in '{path}'.");
        return 0;
    }
}
