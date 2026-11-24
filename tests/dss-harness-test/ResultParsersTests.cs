using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Text;

namespace DssHarness.Tests;

/// <summary>
/// The parsers a log is read by and the one pass that reads it: each line offered once to each reading and claimed by
/// one, a line given back offered to the others, the findings merged in the order of their lines, numbered as the log's
/// file numbers them, and a line no parser took said unparsed only where it holds an error's marker; the parsers chosen
/// by what a phase runs - the compiler a toolchain declares, a test's runner, a runner's steps, a CI job - with the
/// configured matchers where they are named; a matcher's severity, its record, its stack and its refusals at load; and the
/// forms the fixtures do not hold.
/// </summary>
public sealed class ResultParsersTests
{
    /// <summary>
    /// One pass: each line is offered to the reading with a record open first, and a reading that leaves what it held gives
    /// it back, every held line offered to the others in the order printed, never to it again - while the line that ended
    /// its hold, which says only that the held lines were not its, is offered again as a line of its own, to it as well; a
    /// line a reading left with nothing held goes to the others alone; a given-back line no reading takes is said unparsed
    /// where it looks like an error; and each reading ends once.
    /// </summary>
    [Fact]
    public void AHeldLine_GoesToTheOthers_AndTheLineThatEndedItsHoldIsOfferedAgainOnItsOwn()
    {
        var offered = new List<string>();

        var digest = PhaseParsers.DigestOf(
            ["a1", "a2 error: lost", "x", "b1", "a3"],
            [new Holding(offered), new Taking(offered)]);

        Assert.Equal(
            ["A a1", "A a2 error: lost", "A x", "B a1", "B a2 error: lost", "A x", "B x", "B b1", "B a3", "A a3", "A end", "B end"],
            offered);
        Assert.Equal([("taking", "x", (3, 3))], digest!.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Message, diagnostic.Log!.Value)));
        Assert.Equal(["a2 error: lost"], digest.Unparsed);
    }

    public static TheoryData<string[], string> HeadsInARow() => new()
    {
        {
            [
                "System.Net.Http.HttpRequestException: Connection refused (localhost:5432)",
                "System.InvalidOperationException: The store could not be loaded.",
                @"   at Crash.Store.Load(String name) in D:\a\app\app\net\Crash\Program.cs:line 13",
                @"   at Crash.Program.Main() in D:\a\app\app\net\Crash\Program.cs:line 38",
            ],
            "dotnet errored 'System.InvalidOperationException: The store could not be loaded.' 2 frames (2, 4); unparsed: System.Net.Http.HttpRequestException: Connection refused (localhost:5432)"
        },
        {
            [
                "Error: retry 1",
                "TypeError: x is not a function",
                "    at load (/home/runner/work/app/app/store.js:4:10)",
                "    at Object.<anonymous> (/home/runner/work/app/app/store.js:7:1)",
            ],
            "node errored 'TypeError: x is not a function' 2 frames (2, 4); unparsed: Error: retry 1"
        },
        {
            [
                "java.lang.IllegalStateException: retry 1",
                "java.io.UncheckedIOException: the store could not be read",
                "\tat com.example.Store.load(Store.java:13)",
                "\tat com.example.Main.main(Main.java:38)",
            ],
            "jvm errored 'java.io.UncheckedIOException: the store could not be read' 2 frames (2, 4); unparsed: java.lang.IllegalStateException: retry 1"
        },
        {
            [
                "Traceback (most recent call last):",
                "  File \"/home/runner/work/app/app/first.py\", line 3, in load",
                "Traceback (most recent call last):",
                "  File \"/home/runner/work/app/app/store.py\", line 9, in load",
                "    raise KeyError(\"port\")",
                "KeyError: 'port'",
            ],
            "python errored 'KeyError: 'port'' 1 frames (3, 6); unparsed: Traceback (most recent call last):"
        },
    };

    /// <summary>
    /// A thrown error's head that ends what a stack reader held undecided - a logged error's line no frame followed, a
    /// traceback cut short - is read as the head it is, of the error whose frames follow it: never lost with the held line,
    /// nor its frames dropped for want of a record, on any runtime; the held line no frame followed is said unparsed.
    /// </summary>
    [Theory]
    [MemberData(nameof(HeadsInARow))]
    public void AHeadThatEndsAHold_IsReadAsTheHeadOfTheErrorItBegins(string[] lines, string read)
    {
        var digest = PhaseParsers.DigestOf(lines, PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()))!;

        Assert.Equal(
            read,
            $"{string.Join(" | ", digest.Failures.Select(failure => $"{failure.Framework} {failure.Outcome} '{failure.Id}' {failure.Frames.Count} frames {failure.Log}"))}; unparsed: {string.Join(" | ", digest.Unparsed ?? [])}");
    }

    /// <summary>
    /// A logged thrown error's head - a <c>Type: message</c> line a program printed - is decided by the line after it
    /// alone: a frame makes it a stack trace's, any other line gives it back, so a reader holds that one line and never the
    /// log below it. The process's own crash under it is read as that crash, never as the logged line's message, and a test
    /// console's block under it is read by its console.
    /// </summary>
    [Fact]
    public void ALoggedHead_IsDecidedByTheLineAfterIt_SoNoReaderHoldsTheLogBelowIt()
    {
        var dotnet = new List<string>();
        var crash = PhaseParsers.DigestOf(
            [
                "MyApp.Config.SettingException: 'x' missing, using default",
                "loading the store",
                "still loading",
                "Unhandled exception. System.InvalidOperationException: The store could not be loaded.",
                @"   at Crash.Store.Load(String name) in D:\a\app\app\net\Crash\Program.cs:line 13",
                @"   at Crash.Program.Main() in D:\a\app\app\net\Crash\Program.cs:line 38",
            ],
            [.. PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()).Select(parser => parser.Name == "dotnet-stack" ? new Recording(parser, dotnet) : parser)])!;

        // The logged line is held for the one line after it, which gives it back and is read again on its own.
        Assert.Equal(["1 Held", "2 Left", "2 Left", "3 Left", "4 Took", "5 Took", "6 Took"], dotnet);
        Assert.Equal(
            [("dotnet", "crashed", "System.InvalidOperationException: The store could not be loaded.", 2)],
            crash.Failures.Select(failure => (failure.Framework, failure.Outcome, failure.Id, failure.Frames.Count)));
        Assert.Equal(["MyApp.Config.SettingException: 'x' missing, using default"], crash.Unparsed);

        var v8 = new List<string>();
        var jest = PhaseParsers.DigestOf(
            [
                "Error: connect ECONNREFUSED 127.0.0.1:5432",
                " FAIL  src/store.test.js",
                "  ● Store › loads its record",
                string.Empty,
                "    expect(received).toBe(expected) // Object.is equality",
                string.Empty,
                "    Expected: 4",
                "    Received: 3",
                string.Empty,
                "      at Object.toBe (src/store.test.js:7:27)",
                string.Empty,
                "Tests:       1 failed, 1 total",
            ],
            [.. PhaseParsers.ForTests(new HarnessConfig(), "npx", null).Select(parser => parser.Name == "v8-stack" ? new Recording(parser, v8) : parser)])!;

        Assert.Equal(["1 Held", "2 Left"], v8);
        Assert.Equal(
            [("jest", "failed", "Store › loads its record", 1)],
            jest.Failures.Select(failure => (failure.Framework, failure.Outcome, failure.Id, failure.Frames.Count)));
        Assert.Equal(["Error: connect ECONNREFUSED 127.0.0.1:5432"], jest.Unparsed);
    }

    /// <summary>
    /// An error that ended its program has crashed it, on every runtime: Node's, printed under the header of where it was
    /// thrown, and Python's, whose traceback runs through a module's top level - as a script's or <c>-m</c>'s does - while
    /// one a program caught and printed has errored, the program going on: Node's logged with no header, Python's printed
    /// by a handler inside a function. The lines are each tool's own output, the run's directory written as a runner's.
    /// </summary>
    [Fact]
    public void AnErrorThatEndedItsProgram_HasCrashedIt_AndOneItCaughtAndPrintedHasErrored()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "TypeError: a store needs a path",
                "    at load (/home/runner/work/app/app/logged.js:1:25)",
                "    at Object.<anonymous> (/home/runner/work/app/app/logged.js:2:7)",
                "    at Module._compile (node:internal/modules/cjs/loader:1356:14)",
                "    at Module._extensions..js (node:internal/modules/cjs/loader:1414:10)",
                "    at Module.load (node:internal/modules/cjs/loader:1197:32)",
                "    at Module._load (node:internal/modules/cjs/loader:1013:12)",
                "    at Function.executeUserEntryPoint [as runMain] (node:internal/modules/run_main:128:12)",
                "    at node:internal/main/run_main_module:28:49",
                "went on",
                "/home/runner/work/app/app/cause.js:2",
                "  try { JSON.parse('{'); } catch (err) { throw new Error('the store could not be read', { cause: err }); }",
                "                                         ^",
                string.Empty,
                "Error: the store could not be read",
                "    at load (/home/runner/work/app/app/cause.js:2:48)",
                "    at Object.<anonymous> (/home/runner/work/app/app/cause.js:4:1)",
                "    ... 5 lines matching cause stack trace ...",
                "    at node:internal/main/run_main_module:28:49 {",
                "  [cause]: SyntaxError: Unexpected end of JSON input",
                "      at JSON.parse (<anonymous>)",
                "      at load (/home/runner/work/app/app/cause.js:2:14)",
                "      at Object.<anonymous> (/home/runner/work/app/app/cause.js:4:1)",
                "      at Module._compile (node:internal/modules/cjs/loader:1356:14)",
                "      at Module._extensions..js (node:internal/modules/cjs/loader:1414:10)",
                "      at Module.load (node:internal/modules/cjs/loader:1197:32)",
                "      at Module._load (node:internal/modules/cjs/loader:1013:12)",
                "      at Function.executeUserEntryPoint [as runMain] (node:internal/modules/run_main:128:12)",
                "      at node:internal/main/run_main_module:28:49",
                "}",
                string.Empty,
                "Node.js v18.19.1",
                "Traceback (most recent call last):",
                "  File \"/home/runner/work/app/app/handled.py\", line 8, in main",
                "    load()",
                "  File \"/home/runner/work/app/app/handled.py\", line 4, in load",
                "    raise KeyError(\"port\")",
                "KeyError: 'port'",
                "went on",
                "Traceback (most recent call last):",
                "  File \"<frozen runpy>\", line 198, in _run_module_as_main",
                "  File \"<frozen runpy>\", line 88, in _run_code",
                "  File \"/home/runner/work/app/app/pkg/run.py\", line 3, in <module>",
                "    main()",
                "  File \"/home/runner/work/app/app/pkg/run.py\", line 2, in main",
                "    raise RuntimeError(\"run failed\")",
                "RuntimeError: run failed",
            ],
            PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()))!;

        Assert.Equal(
            [
                ("node", "errored", "TypeError: a store needs a path", 8, (1, 9)),
                ("node", "crashed", "Error: the store could not be read", 3, (15, 30)),
                ("python", "errored", "KeyError: 'port'", 2, (33, 38)),
                ("python", "crashed", "RuntimeError: run failed", 4, (40, 47)),
            ],
            digest.Failures.Select(failure => (failure.Framework, failure.Outcome, failure.Id, failure.Frames.Count, failure.Log!.Value)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// A phase's log is digested as it streams, through to the digest: each line is offered to the readings before the
    /// next is read from the log - never the whole log read first and parsed after - and every line no reading took that
    /// looks like an error is counted as the pass meets it, the last fifty held - the nearest the failure - and the rest
    /// said as left out, for the one bound of those lines is the digest's own.
    /// </summary>
    [Fact]
    public void APhasesLog_IsDigestedAsItStreams_EveryLineNoReadingTookCounted()
    {
        using var temp = new TempDirectory();
        var trace = new List<string>();
        string[] lines = [.. Enumerable.Range(1, ResultDigest.Bound + 3).Select(at => $"c{at.ToString(CultureInfo.InvariantCulture)} error: lost")];

        var digest = PhaseDigest.Of(
            new Streaming(trace, lines),
            [new Holding(trace), new Taking(trace)],
            new DigestTree(temp.Path, null, new PhysicalFileSystem(FilePermissionsFactory.Create())))!;

        Assert.Equal(
            ["read c1 error: lost", "A c1 error: lost", "B c1 error: lost", "read c2 error: lost", "A c2 error: lost", "B c2 error: lost"],
            trace.Take(6));
        Assert.Equal((ResultDigest.Bound + 3, 3), (digest.UnparsedCount, digest.UnparsedLeftOut));
        Assert.Equal(lines.TakeLast(ResultDigest.Bound), digest.Unparsed);
    }

    /// <summary>
    /// The findings of every reading are merged in the order of the lines they begin at, never parser by parser: a C
    /// compiler's error, MSBuild's and the compiler's again come out as printed, the first error the first printed.
    /// </summary>
    [Fact]
    public void TheFindings_AreMergedInTheOrderOfTheirLines()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "src/store.c:3:5: error: 'count' undeclared (first use in this function)",
                @"D:\a\app\app\src\Program.cs(12,35): error CS0103: The name 'x' does not exist in the current context [D:\a\app\app\src\App.csproj]",
                "src/queue.c:8:1: error: expected ';' before '}' token",
            ],
            PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null))!;

        Assert.Equal(
            [("cc", "src/store.c", 3), ("msbuild", @"D:\a\app\app\src\Program.cs", 12), ("cc", "src/queue.c", 8)],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.File!, diagnostic.Line!.Value)));
        Assert.Equal((3, "src/store.c"), (digest.Errors, digest.First!.File));
    }

    /// <summary>
    /// A CI job's log is read once: each compiler's line is one form's, so a gcc error and an MSVC error are two
    /// diagnostics, never four, whatever forms are chosen beside them.
    /// </summary>
    [Fact]
    public void ACiJobsLog_IsReadOnce_EachLineByOneForm()
    {
        var digest = PhaseParsers.DigestOf(
            DssHarness.Core.Ci.CiLog.Of(string.Join(
                "\n",
                "2026-11-23T00:40:01.0000000Z src/store.c:3:5: error: 'count' undeclared (first use in this function)",
                @"2026-11-23T00:40:02.0000000Z D:\a\app\app\native\store.cpp(7): error C2065: 'y': undeclared identifier")).Select(line => line.Line),
            PhaseParsers.ForCi([]))!;

        Assert.Equal(
            [("cc", "error", (string?)null, "src/store.c", 3, (1, 1)), ("msvc", "error", "C2065", @"D:\a\app\app\native\store.cpp", 7, (2, 2))],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Severity, diagnostic.Code, diagnostic.File!, diagnostic.Line!.Value, diagnostic.Log!.Value)));
        Assert.Equal(2, digest.Errors);
    }

    /// <summary>
    /// A finding's log range runs from the number its first line has in the log's file to the last number of its last
    /// line - where a line the parsers read as one holds a carriage return printed alone, the file counts it as more.
    /// </summary>
    [Fact]
    public void AFindingsLogRange_RunsFromItsFirstLinesNumber_ToItsLastLinesLast()
    {
        var unparsed = new List<NumberedLine>();
        var found = PhaseParsers.FindingsOf(
            [new NumberedLine(4, 4, "src/a.c:3:5: error: 'x' undeclared"), new NumberedLine(5, 5, "    3 |   x++;"), new NumberedLine(6, 8, "      |   ^")],
            [ResultParsers.Of("gcc")!],
            unparsed.Add)!;

        Assert.Equal((4, 8), Assert.Single(found.Diagnostics).Log);
        Assert.Empty(unparsed);
    }

    /// <summary>
    /// A line looks like an error by an error's marker as a word - <c>error:</c>, <c>error CODE:</c>, <c>FAILED</c>,
    /// <c>fatal:</c>, a line's own <c>panic:</c>, a thrown error's <c>Type: </c>, Python's traceback head - never by a name
    /// that holds the word, nor by a count or a test's name.
    /// </summary>
    [Theory]
    [InlineData("src/a.c:3:5: error: x", true)]
    [InlineData("error: linker command failed with exit code 1", true)]
    [InlineData("Error: Cannot find module 'left-pad'", true)]
    [InlineData("src/app.ts: error TS2304: Cannot find name 'x'.", true)]
    [InlineData("FAILED: obj/store.o", true)]
    [InlineData("fatal: not a git repository", true)]
    [InlineData("a.c:1:10: fatal error: b.h: No such file or directory", true)]
    [InlineData("panic: runtime error: index out of range [3] with length 3", true)]
    [InlineData("TypeError: x is not a function", true)]
    [InlineData("java.lang.IllegalStateException: no runner", true)]
    [InlineData("TypeError [ERR_INVALID_ARG_VALUE]: bad", true)]
    [InlineData("Traceback (most recent call last):", true)]
    [InlineData("  Passed Demo.Tests.WarningTests.SaysItsWarnings [6 ms]", false)]
    [InlineData("    0 Warning(s)", false)]
    [InlineData("    1 Error(s)", false)]
    [InlineData("terror: no", false)]
    [InlineData("no_error: 1", false)]
    [InlineData("errors: 0", false)]
    [InlineData("the ErrorHandler: set", false)]
    [InlineData("it did not panic: fine", false)]
    [InlineData("FAILEDX", false)]
    public void ALine_LooksLikeAnError_ByAnErrorsMarkerAsAWord(string line, bool looks)
        => Assert.Equal(looks, PhaseParsers.LooksLikeError(line));

    /// <summary>A build's parsers are chosen by the compiler its toolchain declares, whatever the toolchain is named, and by the project's type.</summary>
    [Theory]
    [InlineData("dotnet", null, "msbuild")]
    [InlineData("dart", null, "dart")]
    [InlineData("cmake", "MSVC", "msbuild cmake ninja")]
    [InlineData("cmake", "GNU", "gcc ld cmake ninja")]
    [InlineData("cmake", "Clang", "clang ld cmake ninja")]
    [InlineData("cmake", "AppleClang", "clang ld cmake ninja")]
    [InlineData("cmake", "GNU Clang", "msbuild cc ld cmake ninja")]
    [InlineData("cmake", "", "msbuild cc ld cmake ninja")]
    public void ABuildsParsers_AreChosenByTheCompilerItsToolchainDeclares(string type, string? compilers, string chosen)
    {
        var config = new HarnessConfig();
        var ids = (compilers ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);

        config.Toolchains["gcc13-or-anything"] = new ToolchainConfig();

        for (var at = 0; at < ids.Length; at++)
        {
            config.Toolchains["gcc13-or-anything"].CompilerId[at == 0 ? "C" : "CXX"] = ids[at];
        }

        Assert.Equal(chosen, string.Join(' ', PhaseParsers.ForBuild(config, type, compilers is null ? null : "gcc13-or-anything").Select(parser => parser.Name)));
    }

    /// <summary>
    /// A test phase's parsers are chosen by the runner it starts, as its program's name - the last part of its path, a
    /// launcher's extension and a Python's version off - and a runner none names is read by GoogleTest's console and every
    /// runtime's stack form.
    /// </summary>
    [Theory]
    [InlineData("ctest", "ctest gtest sanitizer backtrace cmake")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", "dotnet-test dotnet-stack msbuild")]
    [InlineData("flutter", "dart-test dart-stack dart")]
    [InlineData("/usr/bin/python3.12", "python-stack")]
    [InlineData("npx", "jest vitest v8-stack tsc eslint")]
    [InlineData("go", "go-panic")]
    [InlineData("cargo", "rust-panic")]
    [InlineData("./gradlew", "jvm-stack")]
    [InlineData("build/store_tests", "gtest dotnet-stack jvm-stack python-stack v8-stack dart-stack go-panic rust-panic sanitizer backtrace")]
    public void ATestsParsers_AreChosenByItsRunner(string runner, string chosen)
        => Assert.Equal(chosen, string.Join(' ', PhaseParsers.ForTests(new HarnessConfig(), runner, null).Select(parser => parser.Name)));

    /// <summary>
    /// A matcher a runner or a test invocation names reads only their output; one no runner or invocation names reads every
    /// build, test and runner phase; a CI job's log is read by every matcher; and a name no matcher answers to is refused at
    /// load, as is a list given empty.
    /// </summary>
    [Fact]
    public void AMatcher_ReadsThePhasesThatNameIt_AndOneNamedNowhereReadsEvery()
    {
        ProblemMatcher Matcher(string name)
        {
            var matcher = new ProblemMatcher(name, "^E (?<message>.+)$", Message: "message");
            matcher.Compile();
            return matcher;
        }

        var config = new HarnessConfig
        {
            Results = new ResultsConfig { Matchers = [Matcher("runner-named"), Matcher("invocation-named"), Matcher("named-nowhere")] },
            PredefinedRunners = { ["lint"] = new RunnerConfig { Matchers = ["runner-named"] } },
            Legs = { ["linux"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "Debug", Test = new TestConfig { All = new TestInvocation { Runner = "ctest", Matchers = ["invocation-named"] } } } },
        };

        static string Matchers(IEnumerable<IResultParser> parsers) => string.Join(' ', parsers.OfType<MatcherParser>().Select(parser => parser.Name));

        Assert.Equal("named-nowhere", Matchers(PhaseParsers.ForBuild(config, "cmake", null)));
        Assert.Equal("invocation-named named-nowhere", Matchers(PhaseParsers.ForTests(config, "ctest", ["invocation-named"])));
        Assert.Equal("named-nowhere", Matchers(PhaseParsers.ForTests(config, "ctest", null)));
        Assert.Equal("runner-named named-nowhere", Matchers(PhaseParsers.ForRun(config, config.PredefinedRunners["lint"])));
        Assert.Equal("runner-named invocation-named named-nowhere", Matchers(PhaseParsers.ForCi(config.Results.Matchers)));

        var refused = new HarnessConfig
        {
            Results = config.Results,
            PredefinedRunners = { ["lint"] = new RunnerConfig { Matchers = ["nope"], Phases = [new RunnerPhase { Name = "lint", Command = ["lint"] }] }, ["fmt"] = new RunnerConfig { Matchers = [], Phases = [new RunnerPhase { Name = "fmt", Command = ["fmt"] }] } },
        };
        var problems = HarnessConfigValidator.Validate(refused);

        Assert.Contains("predefined runner 'lint' matchers names matcher 'nope', which results.matchers does not declare", problems);
        Assert.Contains("predefined runner 'fmt' matchers is given empty, which is never read as the matchers no runner or invocation names: name the matchers that read its output, or leave the key out for the matchers no runner or invocation names", problems);
    }

    /// <summary>
    /// A matcher's severity group is read as editors read it, case ignored: a warning's words a warning, a note's a note,
    /// anything else an error - so a matcher's errors are the digest's errors, its first error among them.
    /// </summary>
    [Theory]
    [InlineData("ERROR", "error")]
    [InlineData("Error", "error")]
    [InlineData("fatal", "error")]
    [InlineData("whatever", "error")]
    [InlineData("WARN", "warning")]
    [InlineData("Warning", "warning")]
    [InlineData("info", "note")]
    [InlineData("Information", "note")]
    [InlineData("HINT", "note")]
    [InlineData("notice", "note")]
    [InlineData("note", "note")]
    public void AMatchersSeverityGroup_IsReadAsEditorsReadIt(string said, string severity)
    {
        var matcher = new ProblemMatcher("mytool", @"^(?<sev>\w+) (?<file>[^:]+):(?<line>\d+): (?<message>.+)$", Severity: "sev", File: "file", Line: "line", Message: "message");
        matcher.Compile();

        var digest = PhaseParsers.DigestOf([$"{said} src/a.c:3: it jammed"], [new MatcherParser(matcher)])!;

        Assert.Equal(severity, Assert.Single(digest.Diagnostics).Severity);
        Assert.Equal(severity == "error" ? 1 : 0, digest.Errors);
    }

    /// <summary>
    /// A matcher's record runs from its match to the last line it read: a stack's frames to the line <c>until</c> matches,
    /// or to the last frame where a line matches no frame - the line after it read by the others - and an <c>end</c>'s
    /// lines to the one it matches; its frames read, and none of its lines repeated among the unparsed.
    /// </summary>
    [Fact]
    public void AMatchersRecord_RunsFromItsMatch_ToTheLastLineItRead()
    {
        var stacked = new ProblemMatcher("mytool", @"^ERROR (?<message>.+)$", Message: "message", Stack: new MatcherStack(@"^  at (?<symbol>\S+) \((?<file>[^:]+):(?<line>\d+)\)$", "^  end of stack$"));
        var ended = new ProblemMatcher("other", @"^BEGIN (?<message>.+)$", Message: "message", End: "^END$");
        stacked.Compile();
        ended.Compile();

        var digest = PhaseParsers.DigestOf(
            [
                "ERROR it jammed: error: the queue",
                "  at Queue.take (src/queue.c:12)",
                "  at main (src/main.c:4)",
                "  end of stack",
                "ERROR again",
                "  at Store.load (src/store.c:7)",
                "FAILED: obj/store.o",
                "BEGIN a block",
                "error: inside it",
                "END",
            ],
            [new MatcherParser(stacked), new MatcherParser(ended)])!;

        Assert.Equal(
            [("mytool", "it jammed: error: the queue", (1, 4), "Queue.take src/queue.c:12|main src/main.c:4"), ("mytool", "again", (5, 6), "Store.load src/store.c:7"), ("other", "a block", (8, 10), "")],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Message, diagnostic.Log!.Value, string.Join('|', (diagnostic.Stack ?? []).Select(frame => $"{frame.Symbol} {frame.File}:{frame.Line}")))));
        Assert.Equal(["FAILED: obj/store.o"], digest.Unparsed);
    }

    /// <summary>
    /// A matcher that does not read - a pattern that does not compile, a group it does not name, a severity that is
    /// neither, a stack whose frame pattern names none of a frame's groups - is refused at load.
    /// </summary>
    [Fact]
    public void AMatcherThatDoesNotRead_IsRefusedAtLoad()
    {
        static string? Problem(ProblemMatcher matcher)
        {
            matcher.Compile();
            return matcher.Problem();
        }

        Assert.Equal(
            "matcher 'mytool': its pattern names no group 'line'",
            Problem(new ProblemMatcher("mytool", "^(?<file>.+)$", File: "file", Line: "line")));
        Assert.Equal(
            "matcher 'mytool': its severity 'sev' is neither a severity nor a group its pattern names",
            Problem(new ProblemMatcher("mytool", "^(?<file>.+)$", Severity: "sev", File: "file")));
        Assert.Equal(
            "matcher 'mytool': its stack's frame pattern names none of the groups a frame reads (file, line, column, symbol, module)",
            Problem(new ProblemMatcher("mytool", "^(?<file>.+)$", File: "file", Stack: new MatcherStack(@"^  at (?<where>.+)$"))));
        Assert.Null(Problem(new ProblemMatcher("mytool", @"^(?<file>.+):(?<line>\d+): (?<message>.*)$", Severity: "warning", File: "file", Line: "line", Message: "message", Stack: new MatcherStack(@"^  at (?<file>.+)$"))));
    }

    /// <summary>
    /// A matcher's patterns are compiled as every pattern the tool runs is, linear in what they read with no timeout: one
    /// that holds what that matcher lacks - its pattern, its end's, its stack's frame's or until's - refuses the
    /// configuration at load, naming the matcher, which pattern and the construct in the compiler's own words; and a matcher
    /// that reads compiles all four, so its lines are read with no engine of their own.
    /// </summary>
    [Fact]
    public void AMatchersPatternTheLinearMatcherLacks_RefusesTheConfigurationAtLoad_NamingIt()
    {
        static IReadOnlyList<string> Problems(ProblemMatcher matcher)
            => [.. HarnessConfigValidator.Validate(new HarnessConfig { Results = new ResultsConfig { Matchers = [matcher] } })
                .Where(problem => problem.StartsWith("results.", StringComparison.Ordinal))];

        Assert.Equal(
            ["results.matchers: matcher 'mytool': its pattern is not a valid regular expression: The pattern '^(?<file>.+)(?=:)' holds a lookahead or lookbehind, which .NET's linear-time matcher lacks."],
            Problems(new ProblemMatcher("mytool", "^(?<file>.+)(?=:)", File: "file")));
        Assert.Equal(
            ["results.matchers: matcher 'mytool': its pattern is not a valid regular expression: Invalid pattern '(?<file>[' at offset 9. Unterminated [] set."],
            Problems(new ProblemMatcher("mytool", "(?<file>[", File: "file")));
        Assert.Equal(
            ["results.matchers: matcher 'mytool': its end pattern is not a valid regular expression: The pattern '(?>end)' holds an atomic group, which .NET's linear-time matcher lacks."],
            Problems(new ProblemMatcher("mytool", "^(?<file>.+)$", File: "file", End: "(?>end)")));
        Assert.Equal(
            [@"results.matchers: matcher 'mytool': its stack's frame pattern is not a valid regular expression: The pattern '(?<file>\w+)\1' holds a backreference, which .NET's linear-time matcher lacks."],
            Problems(new ProblemMatcher("mytool", "^(?<file>.+)$", File: "file", Stack: new MatcherStack(@"(?<file>\w+)\1"))));
        Assert.Equal(
            ["results.matchers: matcher 'mytool': its stack's until pattern is not a valid regular expression: The pattern '(?>end)' holds an atomic group, which .NET's linear-time matcher lacks."],
            Problems(new ProblemMatcher("mytool", "^(?<file>.+)$", File: "file", Stack: new MatcherStack("(?<file>.+)", "(?>end)"))));

        var reading = new ProblemMatcher("mytool", "^(?<file>.+)$", File: "file", End: "^done$", Stack: new MatcherStack("^  at (?<file>.+)$", "^end$"));

        Assert.Empty(Problems(reading));
        Assert.Equal(
            [RegexOptions.NonBacktracking, RegexOptions.NonBacktracking, RegexOptions.NonBacktracking, RegexOptions.NonBacktracking],
            new[] { reading.Compiled!, reading.EndCompiled!, reading.FrameCompiled!, reading.UntilCompiled! }.Select(regex => regex.Options & RegexOptions.NonBacktracking));
    }

    /// <summary>
    /// A frame a matcher reads names its line and column only where its own pattern captured a whole number there: one
    /// that captured anything else names no place, as a diagnostic's line does, and the digest is read on, never stopped.
    /// </summary>
    [Fact]
    public void AMatchersFrameWhoseLineIsNoNumber_NamesNoLine_AndTheDigestIsReadOn()
    {
        var matcher = new ProblemMatcher("mytool", @"^ERROR (?<message>.+)$", Message: "message", Stack: new MatcherStack(@"^  at (?<file>\S+):(?<line>\w+)$"));
        matcher.Compile();

        var digest = PhaseParsers.DigestOf(["ERROR it broke", "  at src/a.c:twelve", "  at src/b.c:7", "  at src/c.c:99999999999"], PhaseParsers.ForCi([matcher]))!;

        Assert.Equal(
            [("src/a.c", (int?)null), ("src/b.c", 7), ("src/c.c", null)],
            digest.First!.Stack!.Select(frame => (frame.File, frame.Line)));
    }

    /// <summary>
    /// A line or a column printed past what a number holds names no place, and the log is read on: never an overflow that
    /// stops the digest.
    /// </summary>
    [Fact]
    public void ALineNumberPastWhatANumberHolds_NamesNoLine_AndTheLogIsReadOn()
    {
        var digest = PhaseParsers.DigestOf(
            ["src/a.c:99999999999:5: error: far", @"D:\a\src\b.cpp(99999999999,3): error C2065: 'y': undeclared identifier", "src/c.c:3:5: error: near"],
            PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null))!;

        Assert.Equal(
            [("src/a.c", (int?)null), (@"D:\a\src\b.cpp", null), ("src/c.c", 3)],
            digest.Diagnostics.Select(diagnostic => (diagnostic.File, diagnostic.Line)));
    }

    /// <summary>
    /// MSBuild's canonical form is read wherever its tools print it: a Windows drive's path, a path holding parentheses,
    /// and cl's <c>fatal error</c>, each diagnostic named for the tool its code belongs to - its project the project's
    /// file, an inner build's properties MSBuild writes after it left with the build.
    /// </summary>
    [Fact]
    public void TheCanonicalForm_ReadsADrivesPath_AParenthesisedOne_AndAFatalError()
    {
        var digest = PhaseParsers.DigestOf(
            [
                @"D:\a\app\app\src\Program.cs(12,35): error CS0103: The name 'x' does not exist in the current context [D:\a\app\app\src\App.csproj]",
                @"C:\Program Files (x86)\Windows Kits\10\Include\um\winbase.h(4,1): fatal error C1083: Cannot open include file: 'b.h': No such file or directory",
                @"src\app.ts(3,7): error TS2304: Cannot find name 'y'.",
                @"D:\a\app\app\src\Lib.cs(3,1): warning CS0168: The variable 'e' is declared but never used [D:\a\app\app\src\Lib.csproj::TargetFramework=net10.0]",
            ],
            PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null))!;

        Assert.Equal(
            [
                ("msbuild", "CS0103", @"D:\a\app\app\src\Program.cs", 12, (int?)35, @"D:\a\app\app\src\App.csproj"),
                ("msvc", "C1083", @"C:\Program Files (x86)\Windows Kits\10\Include\um\winbase.h", 4, 1, null),
                ("tsc", "TS2304", @"src\app.ts", 3, 7, null),
                ("msbuild", "CS0168", @"D:\a\app\app\src\Lib.cs", 3, 1, @"D:\a\app\app\src\Lib.csproj"),
            ],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Code!, diagnostic.File!, diagnostic.Line!.Value, diagnostic.Column, diagnostic.Project)));
    }

    /// <summary>
    /// A CMake error says the command that raised it ahead of the message CMake printed under it - never as its project,
    /// which is the project or target a build names - while a head with no message under it is its own message, the
    /// command in it already, and one CMake names no command for says its message alone.
    /// </summary>
    [Fact]
    public void ACMakeError_SaysItsCommandAheadOfItsMessage_AndNamesNoProject()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "CMake Error at CMakeLists.txt:3 (add_subdirectory):",
                "  add_subdirectory given source \"lib/missing\" which is not an existing",
                "  directory.",
                string.Empty,
                string.Empty,
                "CMake Error in src/CMakeLists.txt:",
                "  No SOURCES given to target: store",
                string.Empty,
                string.Empty,
                "CMake Error at CMakeLists.txt:9 (find_package):",
                "-- Configuring incomplete, errors occurred!",
            ],
            PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null))!;

        Assert.Equal(
            [
                ("CMakeLists.txt", (int?)3, (string?)null, "add_subdirectory: add_subdirectory given source \"lib/missing\" which is not an existing directory."),
                ("src/CMakeLists.txt", null, null, "No SOURCES given to target: store"),
                ("CMakeLists.txt", 9, null, "CMake Error at CMakeLists.txt:9 (find_package):"),
            ],
            digest.Diagnostics.Select(diagnostic => (diagnostic.File!, diagnostic.Line, diagnostic.Project, diagnostic.Message)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// A compiler's chain is handed whole, however deep: the digest is the one owner of a stack's bound, so a parser
    /// never cuts one of its own.
    /// </summary>
    [Fact]
    public void ACompilersChain_IsHandedWhole_HoweverDeep()
    {
        var lines = Enumerable.Range(1, 70).Select(at => at == 1 ? $"In file included from include/h{at}.h:1," : $"                 from include/h{at}.h:1,")
            .Append("src/main.c:3:5: error: 'x' undeclared");

        var unparsed = new List<NumberedLine>();
        var found = PhaseParsers.FindingsOf(lines.Select((line, at) => new NumberedLine(at + 1, at + 1, line)), [ResultParsers.Of("gcc")!], unparsed.Add)!;
        var stack = Assert.Single(found.Diagnostics).Stack!;

        Assert.Empty(unparsed);

        Assert.Equal((70, "include/h1.h", "include/h70.h"), (stack.Count, stack[0].File, stack[^1].File));
    }

    /// <summary>
    /// GoogleTest's failure is read wherever its place is printed in the form the compiler it was built with uses - MSVC's
    /// <c>file(line): error:</c> among them - its message the lines under it, and its summary's list said once.
    /// </summary>
    [Fact]
    public void AGoogleTestFailure_IsReadInMsvcsForm_AndItsSummarySaysItOnce()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "[ RUN      ] StoreTest.LoadsItsRecords",
                @"D:\a\app\app\tests\store_test.cpp(42): error: Expected equality of these values:",
                "  store.Count()",
                "    Which is: 2",
                "  3",
                "[  FAILED  ] StoreTest.LoadsItsRecords (0 ms)",
                "[==========] 1 test from 1 test suite ran. (0 ms total)",
                "[  PASSED  ] 0 tests.",
                "[  FAILED  ] 1 test, listed below:",
                "[  FAILED  ] StoreTest.LoadsItsRecords",
                string.Empty,
                " 1 FAILED TEST",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "store_tests.exe", null))!;

        var failure = Assert.Single(digest.Failures);

        Assert.Equal(
            ("gtest", "StoreTest.LoadsItsRecords", "failed", @"D:\a\app\app\tests\store_test.cpp", 42, "Expected equality of these values:\n  store.Count()\n    Which is: 2\n  3"),
            (failure.Framework, failure.Id, failure.Outcome, failure.File!, failure.Line!.Value, failure.Message));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// CTest says a failed test only where nothing printed under its status line said why: a test whose GoogleTest output
    /// names its failure is said by that, and one that printed nothing by CTest's own line.
    /// </summary>
    [Fact]
    public void CTestSaysAFailedTest_OnlyWhereItsOwnOutputSaidNothing()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "    Start 1: store_tests",
                "1/2 Test #1: store_tests ......................***Failed    0.01 sec",
                "[==========] Running 1 test from 1 test suite.",
                "[ RUN      ] StoreTest.LoadsItsRecords",
                "/home/runner/work/app/app/tests/store_test.cpp:42: Failure",
                "Expected equality of these values:",
                "  store.Count()",
                "    Which is: 2",
                "  3",
                "[  FAILED  ] StoreTest.LoadsItsRecords (0 ms)",
                "    Start 2: parse_tests",
                "2/2 Test #2: parse_tests ......................***Failed    0.00 sec",
                string.Empty,
                "0% tests passed, 2 tests failed out of 2",
                string.Empty,
                "Total Test time (real) =   0.02 sec",
                string.Empty,
                "The following tests FAILED:",
                "\t  1 - store_tests (Failed)",
                "\t  2 - parse_tests (Failed)",
                "Errors while running CTest",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "ctest", null))!;

        Assert.Equal(
            [("gtest", "StoreTest.LoadsItsRecords", (4, 10)), ("ctest", "parse_tests", (12, 12))],
            digest.Failures.Select(failure => (failure.Framework, failure.Id, failure.Log!.Value)));
        Assert.Empty(digest.Diagnostics);
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// A test binary that crashed under CTest after GoogleTest placed a failure is said once, as GoogleTest's test that
    /// crashed - its message the lines under its place, ended by CTest's next line, never CTest's lines after it.
    /// </summary>
    [Fact]
    public void ATestBinaryThatCrashedUnderCTest_IsSaidOnce_ByTheTestThatCrashed()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "    Start 1: store_tests",
                "1/2 Test #1: store_tests ......................***Exception: SegFault  0.01 sec",
                "[==========] Running 1 test from 1 test suite.",
                "[ RUN      ] StoreTest.LoadsItsRecords",
                "/home/runner/work/app/app/tests/store_test.cpp:42: Failure",
                "Expected equality of these values:",
                "  store.Count()",
                "    Which is: 2",
                "  3",
                "    Start 2: parse_tests",
                "2/2 Test #2: parse_tests ......................   Passed    0.00 sec",
                string.Empty,
                "50% tests passed, 1 tests failed out of 2",
                string.Empty,
                "Total Test time (real) =   0.02 sec",
                string.Empty,
                "The following tests FAILED:",
                "\t  1 - store_tests (SEGFAULT)",
                "Errors while running CTest",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "ctest", null))!;

        var failure = Assert.Single(digest.Failures);

        Assert.Equal(
            ("gtest", "StoreTest.LoadsItsRecords", "crashed", (4, 9), "Expected equality of these values:\n  store.Count()\n    Which is: 2\n  3"),
            (failure.Framework, failure.Id, failure.Outcome, failure.Log!.Value, failure.Message));
        Assert.Empty(digest.Diagnostics);
    }

    /// <summary>
    /// A test binary that crashed under CTest before GoogleTest placed anything is said once, as GoogleTest's test that
    /// crashed, whichever reading read the log's last line: every reading ends after the ones chosen after it, so CTest
    /// ends knowing what GoogleTest said at the end.
    /// </summary>
    [Fact]
    public void ATestBinaryThatCrashedUnplaced_IsSaidOnce_WhicheverReadingReadTheLastLine()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "    Start 1: store_tests",
                "1/1 Test #1: store_tests ......................***Exception: SegFault  0.01 sec",
                "[==========] Running 1 test from 1 test suite.",
                "[ RUN      ] StoreTest.LoadsItsRecords",
                "loading the store",
                string.Empty,
                "0% tests passed, 1 tests failed out of 1",
                string.Empty,
                "Total Test time (real) =   0.01 sec",
                string.Empty,
                "The following tests FAILED:",
                "\t  1 - store_tests (SEGFAULT)",
                "Errors while running CTest",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "ctest", null))!;

        Assert.Equal(
            [("gtest", "StoreTest.LoadsItsRecords", "crashed", (4, 4))],
            digest.Failures.Select(failure => (failure.Framework, failure.Id, failure.Outcome, failure.Log!.Value)));
        Assert.Empty(digest.Diagnostics);
    }

    /// <summary>
    /// A JVM frame names only its source file's base name, which names no path - a library's frame names one as well - so
    /// it names no file: its line and its symbol are what it says, and no frame of it is ever read as the tree's.
    /// </summary>
    [Fact]
    public void AJvmFrame_NamesNoFile_ItsLineAndSymbolKept()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "Exception in thread \"main\" java.lang.IllegalStateException: no runner",
                "\tat com.example.Runner.start(Runner.java:42)",
                "\tat java.base/java.lang.Thread.run(Thread.java:1583)",
            ],
            PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()))!;

        Assert.Equal(
            [((string?)null, (int?)42, "com.example.Runner.start"), (null, 1583, "java.lang.Thread.run")],
            Assert.Single(digest.Failures).Exception!.Stack.Select(frame => (frame.File, frame.Line, frame.Symbol)));
    }

    /// <summary>
    /// <c>go test</c> names a package by its import path only after its output, on its result line, and a test's place it
    /// logged is relative to that package's directory: each failure read before the line is said with it, naming the
    /// package as its class, its place as Go printed it - and one no result line followed is said with no class.
    /// </summary>
    [Fact]
    public void AGoTestsFailure_NamesThePackageItsResultLineNames_AsItsClass()
    {
        var digest = PhaseParsers.DigestOf(
            [
                "--- FAIL: TestStoreLoads (0.00s)",
                "    store_test.go:14: Load(\"first\") = 3, want 4",
                "FAIL",
                "FAIL\texample.com/app/store\t0.004s",
                "--- FAIL: TestParse (0.01s)",
                "    parse_test.go:9: Parse(\"\") returned no error",
                "FAIL",
                "FAIL\texample.com/app/parse\t0.002s",
                "--- FAIL: TestCut (0.00s)",
                "    cut_test.go:3: cut short",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [
                ("TestStoreLoads", (string?)"example.com/app/store", "store_test.go", 14, (1, 2)),
                ("TestParse", "example.com/app/parse", "parse_test.go", 9, (5, 6)),
                ("TestCut", null, "cut_test.go", 3, (9, 10)),
            ],
            digest.Failures.Select(failure => (failure.Id, failure.Class, failure.File!, failure.Line!.Value, failure.Log!.Value)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>A phase's output read as a log streams, each line noted as it is read.</summary>
    private sealed class Streaming(List<string> trace, IReadOnlyList<string> lines) : PhaseOutput
    {
        public override IEnumerable<string> Lines()
        {
            foreach (var line in lines)
            {
                trace.Add($"read {line}");
                yield return line;
            }
        }
    }

    /// <summary>A parser whose reading notes what it answered of each line it was offered, by the line's number.</summary>
    private sealed class Recording(IResultParser parser, List<string> answers) : IResultParser
    {
        public string Name => parser.Name;

        public IResultReading Begin() => new Reading(parser.Begin(), answers);

        private sealed class Reading(IResultReading reading, List<string> answers) : IResultReading
        {
            public LineTaken Offer(NumberedLine line, ResultSink sink)
            {
                var taken = reading.Offer(line, sink);

                answers.Add($"{line.Number.ToString(CultureInfo.InvariantCulture)} {taken}");
                return taken;
            }

            public LineTaken End(ResultSink sink) => reading.End(sink);
        }
    }

    /// <summary>A reading that holds lines starting with <c>a</c> until <c>end</c>, and leaves them at any other line.</summary>
    private sealed class Holding(List<string> offered) : IResultParser
    {
        public string Name => "holding";

        public IResultReading Begin() => new Reading(offered);

        private sealed class Reading(List<string> offered) : IResultReading
        {
            private bool _holding;

            public LineTaken Offer(NumberedLine line, ResultSink sink)
            {
                offered.Add($"A {line.Text}");

                if (line.Text.StartsWith('a'))
                {
                    _holding = true;
                    return LineTaken.Held;
                }

                _holding = false;
                return LineTaken.Left;
            }

            public LineTaken End(ResultSink sink)
            {
                offered.Add("A end");

                var held = _holding;
                _holding = false;
                return held ? LineTaken.Left : LineTaken.Took;
            }
        }
    }

    /// <summary>A reading that takes lines starting with <c>b</c> or <c>x</c>, and says an error of each <c>x</c>.</summary>
    private sealed class Taking(List<string> offered) : IResultParser
    {
        public string Name => "taking";

        public IResultReading Begin() => new Reading(offered);

        private sealed class Reading(List<string> offered) : IResultReading
        {
            public LineTaken Offer(NumberedLine line, ResultSink sink)
            {
                offered.Add($"B {line.Text}");

                if (line.Text.StartsWith('x'))
                {
                    sink.Add(new Diagnostic("taking", "error", null, line.Text, Log: (line.Number, line.Last)));
                    return LineTaken.Took;
                }

                return line.Text.StartsWith('b') ? LineTaken.Took : LineTaken.Left;
            }

            public LineTaken End(ResultSink sink)
            {
                offered.Add("B end");
                return LineTaken.Took;
            }
        }
    }
}
