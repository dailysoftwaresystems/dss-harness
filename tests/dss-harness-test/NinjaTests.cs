using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

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
    [InlineData(1, false, "[1/9] Building CXX object a.o\rFAILED: a.o\r\n", null)]
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
        Stopped: stalled ? PhaseStop.Silence : PhaseStop.None,
        StallSeconds: stalled ? 60 : 0,
        Witnessed: null,
        Duration: TimeSpan.Zero,
        ClockDrift: TimeSpan.Zero,
        ClockStepped: false,
        Timings: [],
        LogFile: "build.log",
        Output: PhaseOutput.Of(output));

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

    /// <summary>
    /// The steps a build's lines say failed, each named by its first output as ninja canonicalizes it: ninja 1.12's
    /// <c>FAILED:</c> line and 1.13's, which writes the step's exit code first, in colour or not, its lines ending either
    /// way; each step once, in the order said; and nothing where nothing failed.
    /// </summary>
    [Theory]
    [InlineData("[1/4] Building CXX object a.o\nFAILED: CMakeFiles/upstream.dir/src/support.cpp.obj \nninja: build stopped: subcommand failed.\n", new[] { "CMakeFiles/upstream.dir/src/support.cpp.obj" })]
    [InlineData("FAILED: [code=2] CMakeFiles\\a.dir\\a.cpp.obj \r\n", new[] { "CMakeFiles/a.dir/a.cpp.obj" })]
    [InlineData("\u001b[31mFAILED: \u001b[0ma.o \r\nFAILED: b.o \nFAILED: a.o \n", new[] { "a.o", "b.o" })]
    [InlineData("[1/2] Building CXX object a.o\n[2/2] Linking CXX executable fx\n", new string[0])]
    [InlineData("ninja: build stopped: interrupted by user.\n", new string[0])]
    public void FailedOutputs_AreTheStepsTheBuildSaysFailed(string output, string[] expected)
        => Assert.Equal(expected, Ninja.FailedOutputs(output.Split('\n'), program: null, manifest: null));

    /// <summary>
    /// With the manifest, a failed step's outputs are told apart however their paths are spelt: the first output of a
    /// step whose paths hold spaces, which ninja does not escape, is the shortest start of what it wrote that names one;
    /// and the command samurai says failed, under its own name or ninja's, is the step that runs it - its response file's
    /// content after it or not - or, where no step runs it, the command as samurai said it.
    /// </summary>
    [Fact]
    public void FailedOutputs_AreToldApartThroughTheManifest()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(
            Path.Combine("build", NinjaDependencyCheck.ManifestFileName),
            "rule cc\n  command = cc -c $in -o $out\n"
            + "rule link\n  command = cc @$out.rsp -o $out\n  rspfile = $out.rsp\n  rspfile_content = $in\n"
            + "build my$ dir/a.o | my$ dir/a.o.d: cc a.c\n"
            + "build my: cc my.c\n"
            + "build app: link my$ dir/a.o\n");
        var manifest = NinjaManifest.Read(new PhysicalFileSystem(FilePermissionsFactory.Create()), temp.Combine("build"));

        Assert.Equal(["my dir/a.o"], Ninja.FailedOutputs(["FAILED: my dir/a.o my dir/a.o.d "], program: null, manifest));
        Assert.Equal(["my"], Ninja.FailedOutputs(["FAILED: my "], program: null, manifest));
        Assert.Equal(
            ["my dir/a.o", "app", "cc -c gone.c -o gone.o"],
            Ninja.FailedOutputs(
                [
                    "samu: job failed with status 1: cc -c a.c -o \"my dir/a.o\"",
                    "ninja: job failed with status 1: cc @app.rsp -o app",
                    "samu: job failed with status 1: cc -c gone.c -o gone.o",
                    "samu: subcommand failed",
                ],
                "/usr/bin/samu",
                manifest));
    }

    /// <summary>
    /// A manifest as CMake writes one for a project building app from two sources, and two external projects - each a step
    /// that runs a build of its own, the second in the console pool - every step described as CMake describes it.
    /// </summary>
    private static NinjaManifest OwnManifest(TempDirectory temp)
    {
        var source = temp.Combine("src").Replace('\\', '/').Replace(":", "$:", StringComparison.Ordinal);

        temp.WriteFile(
            Path.Combine("build", NinjaDependencyCheck.ManifestFileName),
            "rule C_COMPILER__app_unscanned_Debug\n  command = cc -c $in -o $out\n  description = Building C object $out\n"
            + "rule C_EXECUTABLE_LINKER__app_Debug\n  command = cc $in -o $out\n  description = Linking C executable $out\n"
            + "rule CUSTOM_COMMAND\n  command = $COMMAND\n  description = $DESC\n"
            + $"build CMakeFiles/app.dir/a.c.o: C_COMPILER__app_unscanned_Debug {source}/a.c\n"
            + $"build CMakeFiles/app.dir/b.c.o: C_COMPILER__app_unscanned_Debug {source}/b.c\n"
            + "build app: C_EXECUTABLE_LINKER__app_Debug CMakeFiles/app.dir/a.c.o CMakeFiles/app.dir/b.c.o\n"
            + "build ext-prefix/src/ext-stamp/ext-build: CUSTOM_COMMAND\n  COMMAND = cd /b/ext && cmake --build .\n  DESC = Performing build step for 'ext'\n"
            + "build term-prefix/src/term-stamp/term-build: CUSTOM_COMMAND\n  COMMAND = cd /b/term && cmake --build .\n  DESC = Performing build step for 'term'\n  pool = console\n");

        return NinjaManifest.Read(new PhysicalFileSystem(FilePermissionsFactory.Create()), temp.Combine("build"));
    }

    /// <summary>
    /// The steps this build's own ninja says failed, and how far it says it got: a FAILED line directly after a status line
    /// of its own, which prints what one of its build lines prints at the count it stands at or one more, in colour or not,
    /// under ninja 1.13's code; the last status of its own, whose plan may grow or shrink as the build goes; and none of a
    /// nested build's lines, which reach the output as one of its steps' - the nested build's object named as one of this
    /// build's is, its count starting again from one. A step in the console pool, whose status ninja writes as it starts
    /// and whose FAILED line after all it printed, is this build's where its start was; while it runs ninja holds back
    /// the other steps' lines, writing at its end only some of their statuses, so a failure after it is this build's at
    /// whatever count it follows; its end has no status, so after its FAILED line the count may be one more again, and
    /// where its FAILED line, or its start, came after the last status it was one more step run - but one that did not
    /// fail, followed by statuses, may have ended before or after them, and how far the build got is not said. Where
    /// NINJA_STATUS spells its status otherwise, no line is known for this build's own.
    /// </summary>
    [Theory]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c a.c\na.c:1: error\n[2/4] Building C object CMakeFiles/app.dir/b.c.o\nninja: build stopped: subcommand failed.\n",
        new[] { "CMakeFiles/app.dir/a.c.o" }, 2, 4)]
    [InlineData(
        "\u001b[32m[1/1093]\u001b[0m Building C object CMakeFiles/app.dir/a.c.o\r\n\u001b[31mFAILED: \u001b[0m[code=1] CMakeFiles/app.dir/a.c.o \r\n",
        new[] { "CMakeFiles/app.dir/a.c.o" }, 1, 1093)]
    [InlineData(
        "[1/5] Building C object CMakeFiles/app.dir/a.c.o\n[2/9] Building C object CMakeFiles/app.dir/b.c.o\n[3/7] Linking C executable app\n",
        new string[0], 3, 7)]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/b.c.o\nFAILED: CMakeFiles/app.dir/b.c.o \ncc -c b.c\n"
        + "[2/4] Performing build step for 'ext'\nFAILED: ext-prefix/src/ext-stamp/ext-build \ncd /b/ext && cmake --build .\n"
        + "[1/2] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c /ext/a.c\nninja: build stopped: subcommand failed.\n"
        + "ninja: build stopped: cannot make progress due to previously encountered errors.\n",
        new[] { "CMakeFiles/app.dir/b.c.o", "ext-prefix/src/ext-stamp/ext-build" }, 2, 4)]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/a.c.o\n[2/4] Linking C executable app\n[2/4] Performing build step for 'term'\n"
        + "[1/3] Building C object CMakeFiles/app.dir/b.c.o\nFAILED: CMakeFiles/app.dir/b.c.o \ncc -c /term/b.c\nninja: build stopped: subcommand failed.\n"
        + "FAILED: term-prefix/src/term-stamp/term-build \ncd /b/term && cmake --build .\n",
        new[] { "term-prefix/src/term-stamp/term-build" }, 3, 4)]
    [InlineData("(1 of 7) Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \n", new string[0], null, null)]
    [InlineData(
        "[0/5] Performing build step for 'term'\n"
        + "[1/2] Building C object CMakeFiles/x.dir/x.c.o\n[2/2] Linking C static library libx.a\n"
        + "[3/5] Building C object CMakeFiles/app.dir/b.c.o\nFAILED: CMakeFiles/app.dir/b.c.o \ncc -c b.c\nninja: build stopped: subcommand failed.\n",
        new[] { "CMakeFiles/app.dir/b.c.o" }, null, null)]
    [InlineData(
        "[1/5] Building C object CMakeFiles/app.dir/a.c.o\n[1/5] Performing build step for 'term'\n"
        + "FAILED: term-prefix/src/term-stamp/term-build \ncd /b/term && cmake --build .\n"
        + "[3/5] Building C object CMakeFiles/app.dir/b.c.o\nFAILED: CMakeFiles/app.dir/b.c.o \ncc -c b.c\n",
        new[] { "term-prefix/src/term-stamp/term-build", "CMakeFiles/app.dir/b.c.o" }, 3, 5)]
    [InlineData(
        "[1/3] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c a.c\n[1/3] Performing build step for 'term'\n"
        + "ninja: build stopped: cannot make progress due to previously encountered errors.\n",
        new[] { "CMakeFiles/app.dir/a.c.o" }, 2, 3)]
    public void OwnSteps_AreThoseItsOwnNinjaSaid_NeverANestedBuilds(string output, string[] failed, int? run, int? planned)
    {
        using var temp = new TempDirectory();

        var own = Ninja.Own(output.Split('\n'), "/usr/bin/ninja", OwnManifest(temp));

        Assert.Equal(failed, own.Failed);
        Assert.Equal(run is null ? null : (run.Value, planned!.Value), own.Progress);
    }

    /// <summary>
    /// What ninja 1.11 wrote, piped to a file, for a manifest whose console step ran while five other steps ended, one
    /// failing: the console step's start at the count the build stood at, then nothing of any other step until it ended -
    /// then the failed step's status and lines, and the last step's status, the three others' never written - so the count
    /// jumps from 0 to 3 and on to 5, and to 7 past the console step's end. Each failure is this build's own, and how far
    /// it got is not said: whether the console step ended before its last status is not written.
    /// </summary>
    [Fact]
    public void WhatNinjaHeldBackWhileAConsoleStepRan_IsTheBuildsOwn_AtWhateverCountItIsWritten()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

        temp.WriteFile(
            Path.Combine("build", NinjaDependencyCheck.ManifestFileName),
            "rule cons\n  command = sleep 0.3; echo nested-one; sleep 2; echo nested-two\n  description = CONSOLE $out\n  pool = console\n"
            + "rule quick\n  command = sleep $delay; touch $out\n  description = QUICK $out\n"
            + "rule bad\n  command = sleep $delay; echo bad-output; false\n  description = BAD $out\n"
            + "build c1: cons\nbuild q1: quick\n  delay = 0.6\nbuild q2: quick\n  delay = 0.9\nbuild q3: quick\n  delay = 1.2\n"
            + "build b1: bad\n  delay = 1.0\nbuild q4: quick\n  delay = 1.5\nbuild after1: quick c1\n  delay = 0.1\nbuild after2: bad c1\n  delay = 0.2\n");

        var own = Ninja.Own(
            ("[0/8] CONSOLE c1\nnested-one\nnested-two\n[3/8] BAD b1\nFAILED: b1 \nsleep 1.0; echo bad-output; false\nbad-output\n"
             + "[5/8] QUICK q4\n[7/8] QUICK after1\n[8/8] BAD after2\nFAILED: after2 \nsleep 0.2; echo bad-output; false\nbad-output\n"
             + "ninja: build stopped: cannot make progress due to previous errors.\n").Split('\n'),
            "/usr/bin/ninja",
            NinjaManifest.Read(fileSystem, temp.Combine("build")));

        Assert.Equal(["b1=Own", "after2=Own"], own.Said.Select(line => $"{line.Step}={line.Whose}"));
        Assert.Null(own.Progress);
        Assert.True(own.StatusRead);
    }

    /// <summary>
    /// Every failing line is told this build's own, a nested build's or neither: a nested build's only where it is provably
    /// within one - in a failed step's output, after its FAILED line, or a console step's after its start, and before that
    /// output's nested build said its end, which something of this build's then followed: its own end, or the console
    /// step's own FAILED line. A failing line after a status line that is no status of this build's, a nested build's end
    /// nothing followed - the build killed right after it - and every failing line where NINJA_STATUS spells the status
    /// otherwise, cannot be told either, and is unattributed; and so is a samurai failure whose command no build line runs.
    /// </summary>
    [Theory]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/b.c.o\nFAILED: CMakeFiles/app.dir/b.c.o \ncc -c b.c\n"
        + "[2/4] Performing build step for 'ext'\nFAILED: ext-prefix/src/ext-stamp/ext-build \ncd /b/ext && cmake --build .\n"
        + "[1/2] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c /ext/a.c\nninja: build stopped: subcommand failed.\n"
        + "ninja: build stopped: cannot make progress due to previously encountered errors.\n",
        new[] { "CMakeFiles/app.dir/b.c.o=Own", "ext-prefix/src/ext-stamp/ext-build=Own", "CMakeFiles/app.dir/a.c.o=Nested" })]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/b.c.o\n[2/4] Performing build step for 'ext'\nFAILED: ext-prefix/src/ext-stamp/ext-build \ncd /b/ext && cmake --build .\n"
        + "[1/2] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c /ext/a.c\nninja: build stopped: subcommand failed.\n",
        new[] { "ext-prefix/src/ext-stamp/ext-build=Own", "CMakeFiles/app.dir/a.c.o=Unattributed" })]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/b.c.o\n[2/4] Performing build step for 'ext'\nFAILED: ext-prefix/src/ext-stamp/ext-build \ncd /b/ext && cmake --build .\n"
        + "[1/2] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c /ext/a.c\nninja: build stopped: subcommand failed.\n"
        + "[3/4] Linking C executable app\n",
        new[] { "ext-prefix/src/ext-stamp/ext-build=Own", "CMakeFiles/app.dir/a.c.o=Nested" })]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c a.c\n"
        + "[2/4] Building something no build line describes\nFAILED: weird.o \nninja: build stopped: subcommand failed.\n",
        new[] { "CMakeFiles/app.dir/a.c.o=Own", "weird.o=Unattributed" })]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/a.c.o\n[2/4] Linking C executable app\n[2/4] Performing build step for 'term'\n"
        + "[1/3] Building C object CMakeFiles/x.dir/x.c.o\nFAILED: CMakeFiles/x.dir/x.c.o \ncc -c /term/x.c\nninja: build stopped: subcommand failed.\n"
        + "FAILED: term-prefix/src/term-stamp/term-build \ncd /b/term && cmake --build .\n",
        new[] { "CMakeFiles/x.dir/x.c.o=Nested", "term-prefix/src/term-stamp/term-build=Own" })]
    [InlineData(
        "(1 of 7) Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \n(2 of 7) Building C object CMakeFiles/app.dir/b.c.o\nFAILED: CMakeFiles/app.dir/b.c.o \n",
        new[] { "CMakeFiles/app.dir/a.c.o=Unattributed", "CMakeFiles/app.dir/b.c.o=Unattributed" })]
    [InlineData("[1/2] Building C object CMakeFiles/app.dir/a.c.o\nsamu: job failed with status 1: cc -c /ext/x.c -o x.o\n", new[] { "cc -c /ext/x.c -o x.o=Unattributed" })]
    public void EveryFailingLine_IsToldOwnNestedOrUnattributed(string output, string[] said)
    {
        using var temp = new TempDirectory();

        var own = Ninja.Own(output.Split('\n'), "/usr/bin/samu", OwnManifest(temp));

        Assert.Equal(said, own.Said.Select(line => $"{line.Step}={line.Whose}"));
    }

    /// <summary>
    /// The steps a sweep judges an arm's build by are read by the same reading as a failed build's units: every failing
    /// line that could be the build's own - its own and every one that cannot be told apart, so none that could be a step
    /// not depending on a site is lost - and a nested build's alone left out; where nothing is nested, every failing line,
    /// as before.
    /// </summary>
    [Theory]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/b.c.o\nFAILED: CMakeFiles/app.dir/b.c.o \ncc -c b.c\n"
        + "[2/4] Performing build step for 'ext'\nFAILED: ext-prefix/src/ext-stamp/ext-build \ncd /b/ext && cmake --build .\n"
        + "[1/2] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c /ext/a.c\nninja: build stopped: subcommand failed.\n"
        + "ninja: build stopped: cannot make progress due to previously encountered errors.\n",
        new[] { "CMakeFiles/app.dir/b.c.o", "ext-prefix/src/ext-stamp/ext-build" })]
    [InlineData(
        "[1/4] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \ncc -c a.c\n"
        + "[2/4] Building something no build line describes\nFAILED: weird.o \nninja: build stopped: subcommand failed.\n",
        new[] { "CMakeFiles/app.dir/a.c.o", "weird.o" })]
    [InlineData(
        "(1 of 7) Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \n(2 of 7) Building C object CMakeFiles/app.dir/b.c.o\nFAILED: CMakeFiles/app.dir/b.c.o \n",
        new[] { "CMakeFiles/app.dir/a.c.o", "CMakeFiles/app.dir/b.c.o" })]
    public void TheStepsASweepJudges_AreEveryFailingLineThatCouldBeTheBuildsOwn(string output, string[] judged)
    {
        using var temp = new TempDirectory();

        Assert.Equal(judged, Ninja.FailedOutputs(output.Split('\n'), "/usr/bin/ninja", OwnManifest(temp)));
    }

    /// <summary>
    /// samurai names no outputs, and says under its name which command failed: its line is this build's where one of the
    /// manifest's build lines runs that command, and a nested build's - its command one this build runs none of - is not.
    /// </summary>
    [Fact]
    public void ASamuraiFailure_IsThisBuildsOwn_WhereOneOfItsBuildLinesRunsTheCommand()
    {
        using var temp = new TempDirectory();
        var manifest = OwnManifest(temp);
        var command = manifest.EdgeFor("CMakeFiles/app.dir/a.c.o")!.Command;

        var own = Ninja.Own(
            ["[1/2] Building C object CMakeFiles/app.dir/a.c.o", $"samu: job failed with status 1: {command}", "samu: job failed with status 1: cc -c /ext/x.c -o x.o"],
            "/usr/bin/samu",
            manifest);

        Assert.Equal(["CMakeFiles/app.dir/a.c.o"], own.Failed);
    }

    /// <summary>
    /// The units a build ninja failed says it failed are its own failed steps, a compile named by the source its build line
    /// compiles - relative to the tree, however CMake named the object - and a step that runs a command named by its output;
    /// with how far the build got, and whether ninja went on past failing steps as what the phase ran with says. A log that
    /// cannot be read back says so, and so does one in which no line of its own named a failed step: never that no unit
    /// failed.
    /// </summary>
    [Fact]
    public void FailedUnits_AreEachOwnFailedStepBySourceOrOutput_ThroughTheManifest()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

        OwnManifest(temp);

        var failed = new FailedBuild(
            Phase(1, stalled: false, "[1/3] Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \n[2/3] Performing build step for 'ext'\nFAILED: ext-prefix/src/ext-stamp/ext-build \n"),
            ["--build", "build", "--", "-k", "0"],
            new BuildRequest("a", temp.Path, new ProjectConfig { Name = "app", Type = "cmake" }, new VariantKey("x86_64", "cc", "debug", null), "linux", 1, temp.Combine("runs")) { KeepGoing = true },
            temp.Combine("build"),
            new BuildDirectoryRecord(null, null, null, null, MakeProgram: "/usr/bin/ninja", Generator: "Ninja"),
            fileSystem);

        var units = Ninja.FailedUnitsOf(failed, keptGoing: true);

        Assert.Equal([new FailedUnit("src/a.c", "CMakeFiles/app.dir/a.c.o"), new FailedUnit(null, "ext-prefix/src/ext-stamp/ext-build")], units.Units);
        Assert.Equal(("ninja", (string?)null, true, true, (int?)2, (int?)3), (units.Tool, units.WhyUnread, units.KeptGoing, units.StopsAtFirst, units.StepsRun, units.StepsPlanned));

        var unread = Ninja.FailedUnitsOf(failed with { Phase = failed.Phase with { Output = new PhaseOutputTests.Unread() } }, keptGoing: false);

        Assert.Equal(("ninja", 0, PhaseOutputTests.Unread.Said, false), (unread.Tool, unread.Units.Count, unread.WhyUnread, unread.KeptGoing));

        var none = Ninja.FailedUnitsOf(failed with { Phase = Phase(1, stalled: false, "ninja: error: loading 'build.ninja': no such file\n") }, keptGoing: false);

        Assert.Equal("ninja named no unit that failed in any line read for them - its log says what failed", none.WhyUnread);

        // Where NINJA_STATUS spells the status otherwise no line is the build's own, and each failing line is said as one
        // that could not be told its own: never that no unit failed.
        var spelled = Ninja.FailedUnitsOf(failed with { Phase = Phase(1, stalled: false, "(1 of 7) Building C object CMakeFiles/app.dir/a.c.o\nFAILED: CMakeFiles/app.dir/a.c.o \n") }, keptGoing: false);

        Assert.Equal((0, 1), (spelled.Units.Count, spelled.Unattributed));
        Assert.Equal(
            "ninja wrote no status line read as its own - as where NINJA_STATUS spells its status otherwise - so none of the 1 failing line(s) it wrote "
            + "could be told its own or a nested build's: its log names each",
            spelled.WhyUnread);

        // Status lines of its own, a console step's run leaving how far it got unsaid, and a failure after one that is not:
        // never said as a build whose status was spelled otherwise.
        var followed = Ninja.FailedUnitsOf(
            failed with
            {
                Phase = Phase(1, stalled: false, "[0/5] Performing build step for 'term'\n[3/5] Building C object CMakeFiles/app.dir/b.c.o\n[3/5] Building something no build line describes\nFAILED: weird.o \n"),
            },
            keptGoing: false);

        Assert.Equal((0, 1, (int?)null), (followed.Units.Count, followed.Unattributed, followed.StepsRun));
        Assert.Equal(
            "ninja named no unit of its own that failed, and 1 failing line(s) could not be told its own or a nested build's - its log names each",
            followed.WhyUnread);
        Assert.Equal("which units failed to build was not read: ninja named no unit that failed in any line read for them - its log says what failed", none.Describe());
    }

    /// <summary>
    /// A build ninja was told to go on past failing steps with ended where ninja's last word under its name says it did -
    /// its own end, or an error of its own - and was stopped from outside where its last word is an interruption, where it
    /// said nothing after a step failed, or where its last word is a nested build's end, which ninja told to go on without
    /// a limit never says of its own: so the steps a ninja killed after a failure never ran are never said to need one that
    /// failed. Told nothing, a failed step decides as it always did.
    /// </summary>
    [Theory]
    [InlineData("FAILED: a.o \n[2/3] Building C object b.o\nninja: build stopped: cannot make progress due to previously encountered errors.\n", true, null)]
    [InlineData("ninja: error: loading 'build.ninja': The system cannot find the file specified.\n", true, null)]
    [InlineData("FAILED: a.o \n[2/9] Building C object b.o\n", true, "build exited 1: ninja was told to go on past failing steps and never said it had ended the build, as it says whenever it ends one itself: something stopped it from outside before it finished")]
    [InlineData("FAILED: ext-build \n[1/2] Building C object a.o\nFAILED: a.o \nninja: build stopped: subcommand failed.\n", true, "build exited 1: ninja was told to go on past failing steps and never said it had ended the build")]
    [InlineData("FAILED: a.o \nninja: build stopped: interrupted by user.\n", true, "build exited 1: ninja says it was interrupted before it finished")]
    [InlineData("[1/9] Building C object a.o\n", true, "build exited 1 without ninja saying why")]
    [InlineData("FAILED: a.o \n[2/9] Building C object b.o\n", false, null)]
    public void ABuildToldToGoOn_EndedOnlyWhereNinjasLastWordSaysItDid(string output, bool keptGoing, string? stopped)
        => AssertStopped(stopped, Ninja.Stopped(Phase(1, stalled: false, output), program: null, keptGoing));

    /// <summary>
    /// A build that did not pass, whose log no longer holds what it printed, is unmeasured: nothing says whether ninja
    /// failed it or something stopped it from outside. Read as a log with nothing in it, it was stopped - no failure at
    /// all, and one running again would finish.
    /// </summary>
    [Fact]
    public void ABuildWhoseOutputCouldNotBeReadBack_IsUnmeasured_NeverStopped()
    {
        var unread = Ninja.Stopped(Phase(1, stalled: false, "unused") with { Output = new PhaseOutputTests.Unread() }, "/usr/bin/samu");

        Assert.Equal(
            ReachedVerdict.Of(
                LegVerdict.Unmeasured,
                $"build exited 1, and whether samu failed it or something stopped it from outside could not be read: {PhaseOutputTests.Unread.Said}"),
            unread);
        Assert.Null(Ninja.Stopped(Phase(0, stalled: false, "unused") with { Output = new PhaseOutputTests.Unread() }, program: null));
    }

    /// <summary>
    /// What ninja said is read from the build's log a line at a time, and no further than the line that decides: a build's
    /// output can be larger than any text the harness could hold, and the line naming a failed step settles it.
    /// </summary>
    [Fact]
    public void Stopped_ReadsNoFurtherThanTheLineThatDecides()
    {
        var failed = Phase(1, stalled: false, "unused") with
        {
            Output = new PhaseOutputTests.ReadUpTo(
                line => line.StartsWith("FAILED: ", StringComparison.Ordinal),
                "[1/9] Building CXX object a.o",
                "FAILED: a.o",
                "main.cpp:1: error: expected ';'"),
        };

        Assert.Null(Ninja.Stopped(failed, program: null));
    }
}
