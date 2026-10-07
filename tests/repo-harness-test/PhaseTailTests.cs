using RepoHarness.Core.Build;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// What a phase that did not pass shows of itself where its log is not to hand: its last lines, in
/// the order the child printed them on either stream, with its secrets masked.
/// </summary>
public sealed class PhaseTailTests
{
    /// <summary>
    /// A phase that failed shows its last lines, however it ended them, and no empty line after the
    /// last; one that printed more shows only the last of them.
    /// </summary>
    [Fact]
    public void APhaseThatFailed_ShowsItsLastLines()
    {
        var many = string.Concat(Enumerable.Range(1, PhaseResult.TailLines + 10).Select(line => $"line {line}\r\n"));

        Assert.Equal(["compiling", "error: expected ';'"], Phase(2, "compiling\r\nerror: expected ';'\n\n").Tail);
        Assert.Equal(
            [.. Enumerable.Range(11, PhaseResult.TailLines).Select(line => $"line {line}")],
            Phase(1, many).Tail);
    }

    /// <summary>A phase that passed shows nothing, and neither does one that printed nothing.</summary>
    [Fact]
    public void APhaseThatPassed_OrPrintedNothing_ShowsNothing()
    {
        Assert.Empty(Phase(0, "all 412 tests passed\n").Tail);
        Assert.Empty(Phase(1, string.Empty).Tail);
    }

    /// <summary>
    /// A build that did not pass shows the last lines of the phase that failed - configure's, where
    /// configure failed - and one whose phases all passed shows none.
    /// </summary>
    [Fact]
    public void ABuildThatFailed_ShowsTheLastLinesOfThePhaseThatFailed()
    {
        var failed = new BuildResult(
            ReachedVerdict.Of(LegVerdict.Failed, "configure exited 1"),
            "build/x86_64-gcc-debug",
            [Phase(1, "CMake Error at CMakeLists.txt:3 (project):\n  No CMAKE_CXX_COMPILER could be found.\n", "configure")],
            RebuiltFromClean: null,
            Dependencies: null);
        var built = failed with
        {
            Verdict = ReachedVerdict.Of(LegVerdict.Failed, "build exited 1"),
            Phases = [Phase(0, "-- Configuring done\n", "configure"), Phase(1, "[1/2] Building C object main.c.o\nmain.c:3: error: expected ';'\n", "build")],
        };
        var passed = failed with
        {
            Verdict = ReachedVerdict.Of(LegVerdict.Passed, string.Empty),
            Phases = [Phase(0, "-- Configuring done\n", "configure"), Phase(0, "[2/2] Linking C executable app\n", "build")],
        };

        Assert.Equal(["CMake Error at CMakeLists.txt:3 (project):", "  No CMAKE_CXX_COMPILER could be found."], failed.Tail);
        Assert.Equal(["[1/2] Building C object main.c.o", "main.c:3: error: expected ';'"], built.Tail);
        Assert.Empty(passed.Tail);
    }

    /// <summary>
    /// The lines are the ones the child printed last, in the order they came, whichever stream carried
    /// each - a summary on standard output after errors on standard error is the last thing shown - and so
    /// is the output read back from the log, which keeps the lines in that order too.
    /// </summary>
    [Fact]
    public async Task ThePhaseRunnerTakesTheLastLines_InTheOrderTheyCame()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();

        var result = await new PhaseRunner(new Interleaved(), factory.FileSystem, factory.Output).RunAsync(
            new PhaseRequest { Leg = "native", Phase = "test", FileName = "suite", Arguments = [], LogFile = temp.Combine("test.log") },
            TestContext.Current.CancellationToken);

        Assert.Equal(["[1/2] check", "warning: slow fixture", "1 of 2 checks failed"], result.Tail);
        Assert.Equal(["[1/2] check", "warning: slow fixture", "1 of 2 checks failed"], result.Output.Lines());
    }

    /// <summary>
    /// The last lines are kept in a ring of as many as are shown, whatever the child prints: a line divided by a
    /// carriage return alone, or a form feed, is shown as the lines it divides into, while one ended by a carriage
    /// return before its line feed is one line; and empty lines after the last that is not are not shown, however many
    /// came - even more than the ring holds.
    /// </summary>
    [Fact]
    public void TheLastLines_AreKeptInARing_ShownAsTheyDivide_WithNoEmptyOneAfterTheLast()
    {
        var many = string.Concat(Enumerable.Range(1, 10_000).Select(line => $"line {line}\n"));

        Assert.Equal(
            [.. Enumerable.Range(10_001 - PhaseResult.TailLines, PhaseResult.TailLines).Select(line => $"line {line}")],
            LastLinesOf(many + string.Concat(Enumerable.Repeat("\n", PhaseResult.TailLines * 3))));

        Assert.Equal(["50%", "100%", "page", "next", "done", "", "end"], LastLinesOf("50%\r100%\npage\fnext\ndone\r\r\n\nend\n\n"));
        Assert.Empty(LastLinesOf("\n\n\n"));
    }

    /// <summary>
    /// The last lines a phase that printed <paramref name="output"/> keeps, each line read into the ring as the runner
    /// reads it: for a phase made up rather than run.
    /// </summary>
    private static IReadOnlyList<string> LastLinesOf(string output)
    {
        var last = new LastLinesRing(PhaseResult.TailLines);

        foreach (var line in PhaseOutput.Of(output).Lines())
        {
            last.Add(line);
        }

        return last.ToList();
    }

    private static PhaseResult Phase(int exitCode, string output, string phase = "test")
        => new(
            "native",
            phase,
            exitCode,
            Stalled: false,
            StallSeconds: 0,
            Witnessed: null,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            ClockStepped: false,
            [],
            "test.log",
            PhaseOutput.Of(output))
        {
            LastLines = LastLinesOf(output),
        };

    /// <summary>A runner that prints on standard output, then standard error, then standard output again, and fails.</summary>
    private sealed class Interleaved : RepoHarness.Core.Processes.IProcessRunner
    {
        public Task<RepoHarness.Core.Processes.ProcessResult> RunAsync(RepoHarness.Core.Processes.ProcessRequest request, CancellationToken cancellationToken = default)
        {
            request.OnOutputLine?.Invoke("[1/2] check");
            request.OnErrorLine?.Invoke("warning: slow fixture");
            request.OnOutputLine?.Invoke("1 of 2 checks failed");

            return Task.FromResult(new RepoHarness.Core.Processes.ProcessResult(
                1,
                "[1/2] check\n1 of 2 checks failed\n",
                "warning: slow fixture\n",
                TimeSpan.FromMilliseconds(5),
                TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }
}
