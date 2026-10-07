using RepoHarness.Core.Build;
using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>What ninja says as a build ends, read as ninja says it.</summary>
public sealed class NinjaTests
{
    /// <summary>
    /// A build ninja ran that exited non-zero is stopped where ninja said nothing of why - killed part way, which on
    /// Windows exits 1 as a failed build does - or nothing but what is no reason, or said it was interrupted, and the
    /// detail names the exit code; it keeps its own verdict where ninja named a failed step, or said anything else of its
    /// own, however its lines end or a terminal's colours wrap them. A phase that exited 0, or stalled, keeps its own
    /// verdict too.
    /// </summary>
    [Theory]
    [InlineData(1, false, "", "build exited 1 without ninja saying why, as it says whenever it ends a build itself: something stopped it from outside before it finished")]
    [InlineData(1, false, "[104/916] Building CXX object src/CMakeFiles/app.dir/main.cpp.obj", "build exited 1 without ninja saying why")]
    [InlineData(1, false, "ninja: warning: bad deps log signature or version; starting over\n[1/9] Building CXX object a.o\n", "build exited 1 without ninja saying why")]
    [InlineData(2, false, "[3/9] Building CXX object a.o\r\nninja: build stopped: interrupted by user.\r\n", "build exited 2: ninja says it was interrupted before it finished")]
    [InlineData(1, false, "FAILED: a.o \nmain.cpp:1: error: expected ';'\nninja: build stopped: subcommand failed.\n", null)]
    [InlineData(1, false, "\u001b[31mFAILED: \u001b[0ma.o\r\n", null)]
    [InlineData(1, false, "ninja: error: loading 'build.ninja': The system cannot find the file specified.", null)]
    [InlineData(1, false, "ninja: fatal: ReadFile: Access is denied.", null)]
    [InlineData(1, false, "ninja: build stopped: cannot make progress due to previous errors.", null)]
    [InlineData(1, false, "ninja: a reason ninja may give one day\n", null)]
    [InlineData(0, false, "", null)]
    [InlineData(1, true, "", null)]
    public void Stopped_IsToldFromWhatNinjaSaid(int exitCode, bool stalled, string output, string? detail)
        => AssertStopped(detail, Ninja.Stopped(Phase(exitCode, stalled, output), program: null));

    /// <summary>
    /// samurai, which CMake runs for a ninja generator where it is what is installed, says what it says under the name it
    /// was started by - the file CMake's cache records, <c>samu</c>, or <c>ninja</c> through a link - and never names a
    /// failed step as ninja does: a step it says failed, or a failure it ends the build for, keeps the build failed, and
    /// only what is no reason, or its word that a signal reached it, leaves it stopped, said under its name.
    /// </summary>
    [Theory]
    [InlineData("/usr/bin/samu", "[1/2] cc -c a.c\nsamu: job failed with status 1: cc -c a.c\na.c:1: error: expected ';'\nsamu: subcommand failed\n", null)]
    [InlineData("/usr/bin/ninja", "ninja: job failed with status 1: cc -c a.c\nninja: subcommand failed\n", null)]
    [InlineData("/usr/bin/samu", "samu: cannot make progress due to previous errors\n", null)]
    [InlineData("/usr/bin/samu", "[1/2] cc -c a.c\n", "build exited 1 without samu saying why")]
    [InlineData("/usr/bin/samu", "samu: explain a.o: missing\n[1/2] cc -c a.c\n", "build exited 1 without samu saying why")]
    [InlineData("/usr/bin/samu", "[1/2] cc -c a.c\nsamu: received signal: Interrupt\n", "build exited 1: samu says it was interrupted before it finished")]
    [InlineData("C:/Program Files/ninja/ninja.exe", "[1/2] Building C object a.obj\n", "build exited 1 without ninja saying why")]
    public void Stopped_IsToldFromWhatSamuraiSaid_UnderTheNameItWasStartedBy(string program, string output, string? detail)
        => AssertStopped(detail, Ninja.Stopped(Phase(1, stalled: false, output), program));

    /// <summary>A build phase of one leg that exited <paramref name="exitCode"/> having printed <paramref name="output"/>.</summary>
    private static PhaseResult Phase(int exitCode, bool stalled, string output) => new(
        Leg: "linux-x86_64-debug",
        Phase: "build",
        ExitCode: exitCode,
        Stalled: stalled,
        StallSeconds: stalled ? 60 : 0,
        Witnessed: null,
        Duration: TimeSpan.Zero,
        ClockDrift: TimeSpan.Zero,
        ClockStepped: false,
        Timings: [],
        LogFile: "build.log",
        Output: output);

    /// <summary>That <paramref name="stopped"/> is no verdict where <paramref name="detail"/> is none, and otherwise stopped with a detail it begins.</summary>
    private static void AssertStopped(string? detail, ReachedVerdict? stopped)
    {
        if (detail is null)
        {
            Assert.Null(stopped);
            return;
        }

        Assert.Equal(LegVerdict.Stopped, stopped!.Verdict);
        Assert.StartsWith(detail, stopped.Detail, StringComparison.Ordinal);
    }

    /// <summary>Ninja's generators, as configuration or CMake's cache names them, in any case; no other.</summary>
    [Theory]
    [InlineData("Ninja", true)]
    [InlineData("Ninja Multi-Config", true)]
    [InlineData("ninja", true)]
    [InlineData("Unix Makefiles", false)]
    [InlineData("Visual Studio 17 2022", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Generates_NamesNinjasGeneratorsAlone(string? generator, bool ninja)
        => Assert.Equal(ninja, Ninja.Generates(generator));
}
