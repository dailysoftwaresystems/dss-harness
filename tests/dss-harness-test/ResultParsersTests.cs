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

        var digest = Digested(
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
        var digest = Digested(lines, PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()))!;

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
        var crash = Digested(
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
        var jest = Digested(
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
    /// thrown, and Python's, whose traceback runs through a module's top level - as a script's or <c>-m</c>'s does - where
    /// it is not known that its program ended 0, while one a program caught and printed has errored, the program going on:
    /// Node's logged with no header, Python's printed by a handler inside a function. The lines are each tool's own
    /// output, the run's directory written as a runner's.
    /// </summary>
    [Fact]
    public void AnErrorThatEndedItsProgram_HasCrashedIt_AndOneItCaughtAndPrintedHasErrored()
    {
        var digest = Digested(
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

        var cause = Assert.Single(digest.Failures[1].Exception!.CausedBy);

        Assert.Equal(
            ("SyntaxError", "Unexpected end of JSON input", 9, "/home/runner/work/app/app/cause.js", 2, 0),
            (cause.Type, cause.Message, cause.Stack.Count, cause.Stack[1].File!, cause.Stack[1].Line!.Value, cause.CausedBy.Count));
    }

    /// <summary>
    /// Whether a Python error through its program's top level crashed the program is how the program ended, never a line
    /// printed after its traceback, which may as well be another program's - a shell's, make's: the error that ends a module
    /// run with <c>-m</c> has crashed it where it ended with a code not 0, followed by the capture's own line of its exit or
    /// by make's <c>Error 1</c>, and where how it ended is not known; one a handler at the top level logged
    /// (<c>logging.exception</c>) has errored where the program ended 0, and crashed where it did not or that is not known,
    /// for its traceback alone is the same. The lines are Python 3.12's and GNU Make 4.3's own output, each with the line the
    /// capture printed of the exit after it where it printed one, the run's directory written as a runner's; the logger's
    /// own line ahead of its traceback is no form a parser reads.
    /// </summary>
    [Fact]
    public void WhetherAPythonErrorCrashedItsProgram_IsHowTheProgramEnded_NeverALineAfterIt()
    {
        string[] module =
        [
            "Traceback (most recent call last):",
            "  File \"<frozen runpy>\", line 198, in _run_module_as_main",
            "  File \"<frozen runpy>\", line 88, in _run_code",
            "  File \"/home/runner/work/app/app/pkg/run.py\", line 3, in <module>",
            "    main()",
            "  File \"/home/runner/work/app/app/pkg/run.py\", line 2, in main",
            "    raise RuntimeError(\"run failed\")",
            "RuntimeError: run failed",
        ];
        string[] logged =
        [
            "ERROR:root:the store could not be loaded",
            "Traceback (most recent call last):",
            "  File \"/home/runner/work/app/app/logged.py\", line 7, in <module>",
            "    load()",
            "  File \"/home/runner/work/app/app/logged.py\", line 4, in load",
            "    raise KeyError(\"port\")",
            "KeyError: 'port'",
        ];
        (string Name, string[] Lines, int? Exit)[] runs =
        [
            ("module", [.. module, "exit 1"], 1),
            ("module under make", ["python3 -m pkg.run", .. module, "make: *** [Makefile:2: test] Error 1"], 2),
            ("module", module, null),
            ("logged", [.. logged, "exit 0"], 0),
            ("logged", logged, 1),
            ("logged", logged, null),
        ];

        var read = runs.Select(run =>
        {
            var digest = Digested(run.Lines, PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()), run.Exit)!;
            var failure = Assert.Single(digest.Failures);

            return (run.Name, run.Exit, failure.Outcome, failure.Id, failure.Frames.Count, failure.Log!.Value, string.Join(" | ", digest.Unparsed ?? []));
        });

        Assert.Equal(
            [
                ("module", (int?)1, "crashed", "RuntimeError: run failed", 4, (1, 8), string.Empty),
                ("module under make", 2, "crashed", "RuntimeError: run failed", 4, (2, 9), string.Empty),
                ("module", null, "crashed", "RuntimeError: run failed", 4, (1, 8), string.Empty),
                ("logged", 0, "errored", "KeyError: 'port'", 2, (2, 7), "ERROR:root:the store could not be loaded"),
                ("logged", 1, "crashed", "KeyError: 'port'", 2, (2, 7), "ERROR:root:the store could not be loaded"),
                ("logged", null, "crashed", "KeyError: 'port'", 2, (2, 7), "ERROR:root:the store could not be loaded"),
            ],
            read);
    }

    /// <summary>
    /// A Node error's cause, which Node prints among its properties in braces - <c>[cause]: Type: message</c> and its frames,
    /// a cause of its own inside its braces, indented further - is the record's cause, each nested one the cause of the
    /// one it is printed inside, so a matcher reading the cause chain meets each. The lines are Node 18's own output for an
    /// error thrown with a cause that has one, the run's directory written as a runner's.
    /// </summary>
    [Fact]
    public void ANodeErrorsCauses_AreItsRecordsCauses_EachNestedOneUnderTheOneItIsPrintedIn()
    {
        var digest = Digested(
            [
                "/home/runner/work/app/app/nested.js:2",
                "function load() { try { parse(); } catch (err) { throw new Error('the store could not be read', { cause: err }); } }",
                "                                                 ^",
                string.Empty,
                "Error: the store could not be read",
                "    at load (/home/runner/work/app/app/nested.js:2:56)",
                "    at Object.<anonymous> (/home/runner/work/app/app/nested.js:3:1)",
                "    ... 5 lines matching cause stack trace ...",
                "    at node:internal/main/run_main_module:28:49 {",
                "  [cause]: Error: the record could not be parsed",
                "      at parse (/home/runner/work/app/app/nested.js:1:65)",
                "      at load (/home/runner/work/app/app/nested.js:2:25)",
                "      ... 6 lines matching cause stack trace ...",
                "      at node:internal/main/run_main_module:28:49 {",
                "    [cause]: SyntaxError: Unexpected end of JSON input",
                "        at JSON.parse (<anonymous>)",
                "        at parse (/home/runner/work/app/app/nested.js:1:31)",
                "        at load (/home/runner/work/app/app/nested.js:2:25)",
                "        at Object.<anonymous> (/home/runner/work/app/app/nested.js:3:1)",
                "        at Module._compile (node:internal/modules/cjs/loader:1356:14)",
                "        at Module._extensions..js (node:internal/modules/cjs/loader:1414:10)",
                "        at Module.load (node:internal/modules/cjs/loader:1197:32)",
                "        at Module._load (node:internal/modules/cjs/loader:1013:12)",
                "        at Function.executeUserEntryPoint [as runMain] (node:internal/modules/run_main:128:12)",
                "        at node:internal/main/run_main_module:28:49",
                "  }",
                "}",
                string.Empty,
                "Node.js v18.19.1",
            ],
            PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()))!;

        var failure = Assert.Single(digest.Failures);
        var chain = new List<(string, string, int, string?)>();

        for (var thrown = failure.Exception; thrown is not null; thrown = thrown.CausedBy.SingleOrDefault())
        {
            chain.Add((thrown.Type, thrown.Message, thrown.Stack.Count, thrown.Stack[0].Symbol));
        }

        Assert.Equal(("crashed", (5, 27)), (failure.Outcome, failure.Log!.Value));
        Assert.Equal(
            [
                ("Error", "the store could not be read", 3, "load"),
                ("Error", "the record could not be parsed", 3, "parse"),
                ("SyntaxError", "Unexpected end of JSON input", 10, "JSON.parse"),
            ],
            chain);
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
            null,
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
        var digest = Digested(
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
        var digest = DssHarness.Core.Ci.CiDigest.Of(
            string.Join(
                "\n",
                "2026-11-23T00:40:01.0000000Z src/store.c:3:5: error: 'count' undeclared (first use in this function)",
                @"2026-11-23T00:40:02.0000000Z D:\a\app\app\native\store.cpp(7): error C2065: 'y': undeclared identifier"),
            [],
            null,
            said => said,
            [])!;

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
            unparsed.Add,
            () => null)!;

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

    /// <summary>A build's parsers are chosen by the compiler its toolchain declares, whatever the toolchain is named, and by the project's type as its adapter reads it, ignoring case.</summary>
    [Theory]
    [InlineData("dotnet", null, "msbuild")]
    [InlineData("DOTNET", null, "msbuild")]
    [InlineData("dart", null, "dart")]
    [InlineData("Dart", null, "dart")]
    [InlineData("cmake", "MSVC", "msbuild cmake ninja")]
    [InlineData("cmake", "GNU", "gcc ld cmake ninja")]
    [InlineData("cmake", "Clang", "clang msbuild ld cmake ninja")]
    [InlineData("cmake", "AppleClang", "clang msbuild ld cmake ninja")]
    [InlineData("cmake", "GNU Clang", "cc msbuild ld cmake ninja")]
    [InlineData("cmake", "", "cc msbuild ld cmake ninja")]
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

        var digest = Digested([$"{said} src/a.c:3: it jammed"], [new MatcherParser(matcher)])!;

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

        var digest = Digested(
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

        var digest = Digested(["ERROR it broke", "  at src/a.c:twelve", "  at src/b.c:7", "  at src/c.c:99999999999"], PhaseParsers.ForCi([matcher]))!;

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
        var digest = Digested(
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
        var digest = Digested(
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
    /// clang-cl - CMake's <c>Clang</c> where it is the compiler on Windows - writes its place as MSVC does and no code, and
    /// is read as clang, its flag its code and its notes its chain, while link's lines beside it are read in the canonical
    /// form; where the toolchain declares no compiler, the same lines are the C compiler's, never MSBuild's with a flag for
    /// a project.
    /// </summary>
    [Fact]
    public void ClangCl_IsReadAsClang_InMsvcsPlaceForm_AndLinkBesideIt()
    {
        string[] lines =
        [
            @"D:\a\app\src\main.cpp(7,5): error: use of undeclared identifier 'x'",
            "    7 |     x = count;",
            "      |     ^",
            @"D:\a\app\src\store.cpp(8,18): error: invalid operands to binary expression ('const Text' and 'int')",
            @"D:\a\app\src\store.cpp(14,12): note: in instantiation of function template specialization 'twice<Text>' requested here",
            @"D:\a\app\src\warn.cpp(3,9): warning: unused variable 'unused' [-Wunused-variable]",
            "2 errors generated.",
            @"main.cpp.obj : error LNK2019: unresolved external symbol ""int __cdecl store::load_records(char const *)"" (?load_records@store@@YAHPEBD@Z) referenced in function main",
        ];

        var config = new HarnessConfig();
        config.Toolchains["clang-cl"] = new ToolchainConfig { CompilerId = { ["C"] = "Clang", ["CXX"] = "Clang" } };

        static IEnumerable<string> Said(ResultDigest digest)
            => digest.Diagnostics.Select(diagnostic => $"{diagnostic.Tool} {diagnostic.Severity} {diagnostic.Code ?? "-"} {diagnostic.File ?? "-"}:{diagnostic.Line}:{diagnostic.Column} {diagnostic.Project ?? "-"} {diagnostic.Stack.Count}");

        Assert.Equal(
            [
                @"clang error - D:\a\app\src\main.cpp:7:5 - 0",
                @"clang error - D:\a\app\src\store.cpp:8:18 - 1",
                @"clang warning -Wunused-variable D:\a\app\src\warn.cpp:3:9 - 0",
                "link error LNK2019 -:: main.cpp.obj 0",
            ],
            Said(Digested(lines, PhaseParsers.ForBuild(config, "cmake", "clang-cl"))!));
        Assert.Equal(
            [
                @"cc error - D:\a\app\src\main.cpp:7:5 - 0",
                @"cc error - D:\a\app\src\store.cpp:8:18 - 1",
                @"cc warning -Wunused-variable D:\a\app\src\warn.cpp:3:9 - 0",
                "link error LNK2019 -:: main.cpp.obj 0",
            ],
            Said(Digested(lines, PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null))!));
    }

    /// <summary>
    /// GNU ld's reference is read wherever it is placed: by its line where the object holds line information - with its
    /// section after it, or with none as older binutils printed it - behind the object where ld names it first, and behind
    /// a linker named by a drive's path; every reference is said, the first under its function's line included, and the
    /// line that only says the link failed is taken with them.
    /// </summary>
    [Fact]
    public void ALinkersReference_IsReadByItsLine_BehindItsObject_AndBehindADrivesLinker()
    {
        var digest = Digested(
            [
                "/usr/bin/ld: /tmp/cc.o: in function `main':",
                "/home/u/app/link.c:4: undefined reference to `parse_record'",
                "/usr/bin/ld: /home/u/app/link.c:5: undefined reference to `load_store'",
                "collect2: error: ld returned 1 exit status",
                "C:/msys64/mingw64/bin/../lib/gcc/x86_64-w64-mingw32/13.2.0/../../../../x86_64-w64-mingw32/bin/ld.exe: CMakeFiles/app.dir/main.c.obj:main.c:(.text+0x15): undefined reference to `parse_record'",
                "C:/msys64/mingw64/bin/../lib/gcc/x86_64-w64-mingw32/13.2.0/../../../../x86_64-w64-mingw32/bin/ld.exe: C:/a/app/main.c:9:(.text+0x20): undefined reference to `load_store'",
                "collect2.exe: error: ld returned 1 exit status",
            ],
            PhaseParsers.ForBuild(new HarnessConfig { Toolchains = { ["gcc"] = new ToolchainConfig { CompilerId = { ["C"] = "GNU" } } } }, "cmake", "gcc"))!;

        Assert.Equal(
            [
                ("undefined reference to `parse_record'", "/home/u/app/link.c", (int?)4, (1, 2)),
                ("undefined reference to `load_store'", "/home/u/app/link.c", 5, (3, 3)),
                ("undefined reference to `parse_record'", "main.c", null, (5, 5)),
                ("undefined reference to `load_store'", "C:/a/app/main.c", 9, (6, 6)),
            ],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Message, diagnostic.File!, diagnostic.Line, diagnostic.Log!.Value)));
        Assert.Equal(4, digest.Errors);
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// A CMake error says the command that raised it ahead of the message CMake printed under it - never as its project,
    /// which is the project or target a build names - while a head with no message under it is its own message, the
    /// command in it already, and one CMake names no command for says its message alone.
    /// </summary>
    [Fact]
    public void ACMakeError_SaysItsCommandAheadOfItsMessage_AndNamesNoProject()
    {
        var digest = Digested(
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
        var found = PhaseParsers.FindingsOf(lines.Select((line, at) => new NumberedLine(at + 1, at + 1, line)), [ResultParsers.Of("gcc")!], unparsed.Add, () => null)!;
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
        var digest = Digested(
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
        var digest = Digested(
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
            [("gtest", "StoreTest.LoadsItsRecords", (4, 10)), ("ctest", "parse_tests", (12, 13))],
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
        var digest = Digested(
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
        var digest = Digested(
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
        var digest = Digested(
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
        var digest = Digested(
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

    /// <summary>
    /// A crash a report says ended the program inside a test a runner said began is that test's failure, said once: a GoogleTest test that
    /// crashed under AddressSanitizer is the test's own crash - the report's words its message, its first stack the
    /// failure's - and one the C runtime's failed assertion killed is the test's own too, the assertion's words its
    /// message and its place the failure's, never a second failure the runner says beside them. The lines are each
    /// tool's own output, a test binary run directly as a leg runs it.
    /// </summary>
    [Fact]
    public void ACrashAReportSaysInsideATestABegan_IsThatTestsFailure_SaidOnce()
    {
        var asan = Digested(AsanDirectRun, PhaseParsers.ForTests(new HarnessConfig(), "store_tests", null))!;

        var crashed = Assert.Single(asan.Failures);

        Assert.Equal(("gtest", "StoreTest.Loads", "crashed"), (crashed.Framework, crashed.Id, crashed.Outcome));
        Assert.Equal("AddressSanitizer: heap-buffer-overflow on address 0x502000000258 at pc 0x58ddc2a1101e bp 0x7fff5a29c580 sp 0x7fff5a29c570", crashed.Message);
        Assert.Equal(("AddressSanitizer", 12), (crashed.Exception!.Type, crashed.Exception.Stack.Count));
        Assert.Equal(("/home/runner/work/app/app/tests/store_test.cc", 7), (crashed.Exception.Stack[0].File, crashed.Exception.Stack[0].Line));
        Assert.Equal(("/home/runner/work/app/app/tests/store_test.cc", 22), (crashed.Exception.Stack[1].File, crashed.Exception.Stack[1].Line));
        Assert.Empty(asan.Unparsed ?? []);

        var assertion = Digested(AssertDirectRun, PhaseParsers.ForTests(new HarnessConfig(), "store_tests", null))!;

        var killed = Assert.Single(assertion.Failures);

        Assert.Equal(("gtest", "StoreTest.Loads", "crashed"), (killed.Framework, killed.Id, killed.Outcome));
        Assert.Equal("virtual void StoreTest_Loads_Test::TestBody(): Assertion `count == 2' failed.", killed.Message);
        Assert.Equal(("/home/runner/work/app/app/tests/store_test.cc", 18), (killed.File, killed.Line));
        Assert.Empty(assertion.Unparsed ?? []);
    }

    /// <summary>
    /// A sanitizer's report a test's program recovered from and ran on is the report's own, its message naming the test it
    /// ran in, and the test is said by its runner as it ended: one GoogleTest says failed is said once, as that failure,
    /// and one it says passed is in no failure. The lines are each tool's own form - GoogleTest's console as a test binary
    /// run directly prints it, UndefinedBehaviorSanitizer's runtime error as g++ 13's recovering UBSan prints it.
    /// </summary>
    [Fact]
    public void ASanitizersReportATestRanOn_IsTheReportsOwn_AndTheTestIsSaidByItsRunner()
    {
        const string Overflow = "signed integer overflow: 2147483647 + 1 cannot be represented in type 'int'";

        var digest = Digested(
            [
                "Running main() from /home/runner/work/app/app/build/_deps/googletest-src/googletest/src/gtest_main.cc",
                "[==========] Running 2 tests from 1 test suite.",
                "[----------] Global test environment set-up.",
                "[----------] 2 tests from StoreTest",
                "[ RUN      ] StoreTest.Loads",
                $"/home/runner/work/app/app/src/store.cc:5:18: runtime error: {Overflow}",
                "/home/runner/work/app/app/tests/store_test.cc:14: Failure",
                "Expected equality of these values:",
                "  store.Count()",
                "    Which is: -2147483648",
                "  2",
                "[  FAILED  ] StoreTest.Loads (0 ms)",
                "[ RUN      ] StoreTest.Counts",
                $"/home/runner/work/app/app/src/store.cc:5:18: runtime error: {Overflow}",
                "[       OK ] StoreTest.Counts (0 ms)",
                "[----------] 2 tests from StoreTest (0 ms total)",
                string.Empty,
                "[----------] Global test environment tear-down",
                "[==========] 2 tests from 1 test suite ran. (0 ms total)",
                "[  PASSED  ] 1 test.",
                "[  FAILED  ] 1 test, listed below:",
                "[  FAILED  ] StoreTest.Loads",
                string.Empty,
                " 1 FAILED TEST",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "store_tests", null))!;

        Assert.Equal(
            [
                ("gtest", "StoreTest.Loads", "failed", "Expected equality of these values:\n  store.Count()\n    Which is: -2147483648\n  2", (5, 12)),
                ("sanitizer", $"UndefinedBehaviorSanitizer: {Overflow}", "errored", $"{Overflow} (while the test StoreTest.Loads ran)", (6, 6)),
                ("sanitizer", $"UndefinedBehaviorSanitizer: {Overflow}", "errored", $"{Overflow} (while the test StoreTest.Counts ran)", (14, 14)),
            ],
            digest.Failures.Select(failure => (failure.Framework, failure.Id, failure.Outcome, failure.Message!, failure.Log!.Value)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// A GoogleTest binary's own output, run directly under AddressSanitizer (g++ 13, GoogleTest by CMake's
    /// FetchContent): its two stacks, its <c>SUMMARY:</c> and its <c>ABORTING</c>. The gtest-asan fixture holds the whole
    /// report, shadow bytes and legend included.
    /// </summary>
    private static readonly string[] AsanDirectRun =
    [
        "Running main() from /home/runner/work/app/app/build/_deps/googletest-src/googletest/src/gtest_main.cc",
        "[==========] Running 2 tests from 1 test suite.",
        "[----------] Global test environment set-up.",
        "[----------] 2 tests from StoreTest",
        "[ RUN      ] StoreTest.KeepsItsBounds",
        "[       OK ] StoreTest.KeepsItsBounds (0 ms)",
        "[ RUN      ] StoreTest.Loads",
        "=================================================================",
        "==473==ERROR: AddressSanitizer: heap-buffer-overflow on address 0x502000000258 at pc 0x58ddc2a1101e bp 0x7fff5a29c580 sp 0x7fff5a29c570",
        "READ of size 4 at 0x502000000258 thread T0",
        "    #0 0x58ddc2a1101d in store::read(std::vector<int, std::allocator<int> > const&, int) /home/runner/work/app/app/tests/store_test.cc:7",
        "    #1 0x58ddc2a11897 in StoreTest_Loads_Test::TestBody() /home/runner/work/app/app/tests/store_test.cc:22",
        "    #2 0x58ddc2a52740 in void testing::internal::HandleExceptionsInMethodIfSupported<testing::Test, void>(testing::Test*, void (testing::Test::*)(), char const*) (/home/runner/work/app/app/build/store_tests+0x51740) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        "    #3 0x58ddc2a407d9 in testing::Test::Run() [clone .part.0] (/home/runner/work/app/app/build/store_tests+0x3f7d9) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        "    #4 0x58ddc2a40ba9 in testing::TestInfo::Run() (/home/runner/work/app/app/build/store_tests+0x3fba9) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        "    #5 0x58ddc2a410e8 in testing::TestSuite::Run() [clone .part.0] (/home/runner/work/app/app/build/store_tests+0x400e8) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        "    #6 0x58ddc2a48c1e in testing::internal::UnitTestImpl::RunAllTests() (/home/runner/work/app/app/build/store_tests+0x47c1e) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        "    #7 0x58ddc2a4127b in testing::UnitTest::Run() (/home/runner/work/app/app/build/store_tests+0x4027b) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        "    #8 0x58ddc2a10b63 in main (/home/runner/work/app/app/build/store_tests+0xfb63) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        "    #9 0x75b886e2a1c9 in __libc_start_call_main ../sysdeps/nptl/libc_start_call_main.h:58",
        "    #10 0x75b886e2a28a in __libc_start_main_impl ../csu/libc-start.c:360",
        "    #11 0x58ddc2a10ef4 in _start (/home/runner/work/app/app/build/store_tests+0xfef4) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        string.Empty,
        "0x502000000258 is located 0 bytes after 8-byte region [0x502000000250,0x502000000258)",
        "allocated by thread T0 here:",
        "    #0 0x75b8876fe548 in operator new(unsigned long) ../../../../src/libsanitizer/asan/asan_new_delete.cpp:95",
        "    #1 0x58ddc2a14c93 in std::__new_allocator<int>::allocate(unsigned long, void const*) /usr/include/c++/13/bits/new_allocator.h:151",
        "    #2 0x58ddc2a143a0 in std::allocator_traits<std::allocator<int> >::allocate(std::allocator<int>&, unsigned long) /usr/include/c++/13/bits/alloc_traits.h:482",
        "    #3 0x58ddc2a143a0 in std::_Vector_base<int, std::allocator<int> >::_M_allocate(unsigned long) /usr/include/c++/13/bits/stl_vector.h:381",
        "    #4 0x58ddc2a13d7d in void std::vector<int, std::allocator<int> >::_M_range_initialize<int const*>(int const*, int const*, std::forward_iterator_tag) /usr/include/c++/13/bits/stl_vector.h:1692",
        "    #5 0x58ddc2a132f2 in std::vector<int, std::allocator<int> >::vector(std::initializer_list<int>, std::allocator<int> const&) /usr/include/c++/13/bits/stl_vector.h:682",
        "    #6 0x58ddc2a117ca in StoreTest_Loads_Test::TestBody() /home/runner/work/app/app/tests/store_test.cc:20",
        "    #7 0x58ddc2a52740 in void testing::internal::HandleExceptionsInMethodIfSupported<testing::Test, void>(testing::Test*, void (testing::Test::*)(), char const*) (/home/runner/work/app/app/build/store_tests+0x51740) (BuildId: eea6837c3cec38f7a4b00e06ddc4e6244ad60bae)",
        string.Empty,
        "SUMMARY: AddressSanitizer: heap-buffer-overflow /home/runner/work/app/app/tests/store_test.cc:7 in store::read(std::vector<int, std::allocator<int> > const&, int)",
        "==473==ABORTING",
    ];

    /// <summary>A GoogleTest binary's own output, run directly, its test killed by the C runtime's failed assertion (glibc, g++ 13).</summary>
    private static readonly string[] AssertDirectRun =
    [
        "Running main() from /home/runner/work/app/app/build/_deps/googletest-src/googletest/src/gtest_main.cc",
        "[==========] Running 2 tests from 1 test suite.",
        "[----------] Global test environment set-up.",
        "[----------] 2 tests from StoreTest",
        "[ RUN      ] StoreTest.KeepsItsBounds",
        "[       OK ] StoreTest.KeepsItsBounds (0 ms)",
        "[ RUN      ] StoreTest.Loads",
        "store_tests: /home/runner/work/app/app/tests/store_test.cc:18: virtual void StoreTest_Loads_Test::TestBody(): Assertion `count == 2' failed.",
    ];

    /// <summary>
    /// <c>go test -v</c> streams each test's logged lines as it runs, under the <c>===</c> line that named it, and says none
    /// of them again under its result line: the failure's message is what the test logged, read from the stream, its
    /// place the first line that names one. In the form the test binary prints for <c>test2json</c>, a subtest's result
    /// comes first, unindented, and its parent's after it, the parent's <c>=== NAME</c> line back: the subtest's failure is
    /// its own, and the parent that logged nothing of its own is said by it; a passing test's streamed lines are no
    /// failure's.
    /// </summary>
    [Fact]
    public void AVerboseGoRun_SaysEachFailingTestByWhatItsStreamLogged()
    {
        var digest = Digested(
            [
                "=== RUN   TestStoreLoads",
                "    store_test.go:14: Load(\"first\") = 3, want 4",
                "--- FAIL: TestStoreLoads (0.00s)",
                "FAIL",
                "FAIL\texample.com/app/store\t0.004s",
                "=== RUN   TestGroup",
                "=== RUN   TestGroup/sub",
                "=== PAUSE TestGroup/sub",
                "=== CONT  TestGroup/sub",
                "=== NAME  TestGroup/sub",
                "    sub_test.go:10: inner failure",
                "--- FAIL: TestGroup/sub (0.00s)",
                "=== NAME  TestGroup",
                "--- FAIL: TestGroup (0.00s)",
                "=== RUN   TestPasses",
                "=== NAME  TestPasses",
                "    pass_test.go:4: a line the stream logged of a passing test",
                "--- PASS: TestPasses (0.00s)",
                "FAIL",
                "FAIL\texample.com/app/store\t0.008s",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [
                ("TestStoreLoads", (string?)"example.com/app/store", "store_test.go", (int?)14, "store_test.go:14: Load(\"first\") = 3, want 4"),
                ("TestGroup/sub", "example.com/app/store", "sub_test.go", 10, "sub_test.go:10: inner failure"),
            ],
            digest.Failures.Select(failure => (failure.Id, failure.Class, failure.File, failure.Line, failure.Message)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// Plain <c>go test -v</c> prints a parent's result first and its subtests' nested under it, indented: a nested
    /// <c>--- PASS</c> or <c>--- SKIP</c> ends that subtest and is no line its parent logged, and a nested
    /// <c>--- FAIL</c> is the subtest's failure, its message what its stream logged - the parent said by it where the
    /// parent logged nothing of its own, whatever passed before it, and said as well, with its own lines, where it did.
    /// </summary>
    [Fact]
    public void PlainVerboseGo_NestsSubtestsResultsUnderTheirParents_NoneALoggedLine()
    {
        var digest = Digested(
            [
                "=== RUN   TestAdd",
                "=== RUN   TestAdd/one",
                "=== RUN   TestAdd/two",
                "    add_test.go:12: got 3, want 4",
                "--- FAIL: TestAdd (0.00s)",
                "    --- PASS: TestAdd/one (0.00s)",
                "    --- FAIL: TestAdd/two (0.00s)",
                "FAIL",
                "FAIL\texample.com/add\t0.002s",
                "=== RUN   TestSave",
                "    save_test.go:5: the store is read only",
                "=== RUN   TestSave/skipped",
                "    save_test.go:9: not on this system",
                "=== RUN   TestSave/first",
                "    save_test.go:14: Save(\"first\") failed",
                "--- FAIL: TestSave (0.00s)",
                "    --- SKIP: TestSave/skipped (0.00s)",
                "    --- FAIL: TestSave/first (0.00s)",
                "FAIL",
                "FAIL\texample.com/save\t0.003s",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [
                ("TestAdd/two", (string?)"example.com/add", "add_test.go:12: got 3, want 4", (4, 7)),
                ("TestSave", "example.com/save", "save_test.go:5: the store is read only", (11, 17)),
                ("TestSave/first", "example.com/save", "save_test.go:14: Save(\"first\") failed", (15, 18)),
            ],
            digest.Failures.Select(failure => (failure.Id, failure.Class, failure.Message, failure.Log!.Value)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// Run without <c>-v</c>, <c>go test</c> prints a failed subtest's block under its parent's result line and a line the
    /// parent logged after the subtest ran under that block, at the parent's own indent - its testing package writes a
    /// parent's lines and its subtests' reports into one buffer as each comes. The failed tests open at once are held by
    /// their indent, so that line is the parent's own: the parent, decided at its own end, is said with it beside its
    /// subtest, and a parent that logged nothing of its own by its end is said by its subtest.
    /// </summary>
    [Fact]
    public void AGoParentsOwnLineAfterItsSubtestsBlock_IsItsOwn_EachTestDecidedAtItsEnd()
    {
        var digest = Digested(
            [
                "--- FAIL: TestStore (0.00s)",
                "    --- FAIL: TestStore/load (0.00s)",
                "        store_test.go:2: load failed",
                "    store_test.go:3: count = 1, want 2",
                "--- FAIL: TestQuiet (0.00s)",
                "    --- FAIL: TestQuiet/one (0.00s)",
                "        quiet_test.go:7: one failed",
                "FAIL",
                "FAIL\texample.com/app\t0.002s",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [
                ("TestStore", (string?)"store_test.go:3: count = 1, want 2", (string?)"store_test.go", (int?)3, (1, 4)),
                ("TestStore/load", "store_test.go:2: load failed", "store_test.go", 2, (2, 3)),
                ("TestQuiet/one", "quiet_test.go:7: one failed", "quiet_test.go", 7, (6, 7)),
            ],
            digest.Failures.Select(failure => (failure.Id, failure.Message, failure.File, failure.Line, failure.Log!.Value)));
        Assert.All(digest.Failures, failure => Assert.Equal("example.com/app", failure.Class));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// What a test streamed is held only while it runs, never past its end: a subtest's lines are dropped at its nested
    /// result line - so a run of each test twice (<c>-count=2</c>) says the failing second run by its own lines alone, not
    /// the first run's - and every test's at its package's result line, so a package whose binary was killed before its
    /// test said its result leaves nothing to a test of the same name in the next package.
    /// </summary>
    [Fact]
    public void AGoTestsStreamedLines_AreHeldOnlyWhileItRuns()
    {
        var counted = Digested(
            [
                "=== RUN   TestShared",
                "=== RUN   TestShared/a",
                "    shared_test.go:3: first run, attempt passed",
                "--- PASS: TestShared (0.00s)",
                "    --- PASS: TestShared/a (0.00s)",
                "=== RUN   TestShared",
                "=== RUN   TestShared/a",
                "    shared_test.go:5: second run, attempt failed",
                "--- FAIL: TestShared (0.00s)",
                "    --- FAIL: TestShared/a (0.00s)",
                "FAIL",
                "FAIL\texample.com/app/shared\t0.004s",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [("TestShared/a", (string?)"shared_test.go:5: second run, attempt failed")],
            counted.Failures.Select(failure => (failure.Id, failure.Message)));

        var killed = Digested(
            [
                "=== RUN   TestLoad",
                "    load_test.go:5: loading the first record",
                "signal: killed",
                "FAIL\texample.com/app/load\t0.512s",
                "=== RUN   TestLoad",
                "    save_test.go:9: the store was not saved",
                "--- FAIL: TestLoad (0.00s)",
                "FAIL",
                "FAIL\texample.com/app/save\t0.003s",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [("TestLoad", (string?)"example.com/app/save", (string?)"save_test.go:9: the store was not saved")],
            killed.Failures.Select(failure => (failure.Id, failure.Class, failure.Message)));
    }

    /// <summary>
    /// A Go test's failure is the first line it logged that names its place - its message and its place - and its log
    /// range runs over every line it logged, so each is one read away and none is held as text: a test run without
    /// <c>-v</c> that logged a line more than once - a retry's same <c>t.Errorf</c> twice, two lines in turn twice over -
    /// has every one of them in its range, none taken for a copy of another, for Go says a test's lines once.
    /// </summary>
    [Fact]
    public void AGoTestsFailure_IsItsFirstLineThatNamesItsPlace_EveryLineItLoggedInItsRange()
    {
        var digest = Digested(
            [
                "--- FAIL: TestRetries (0.02s)",
                "    retry_test.go:9: attempt failed: connection refused",
                "    retry_test.go:9: attempt failed: connection refused",
                "--- FAIL: TestAlternates (0.00s)",
                "    alt_test.go:4: A",
                "    alt_test.go:5: B",
                "    alt_test.go:4: A",
                "    alt_test.go:5: B",
                "FAIL",
                "FAIL\texample.com/app/retry\t0.021s",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [
                ("TestRetries", (string?)"retry_test.go:9: attempt failed: connection refused", "retry_test.go", (int?)9, (1, 3)),
                ("TestAlternates", "alt_test.go:4: A", "alt_test.go", 4, (4, 8)),
            ],
            digest.Failures.Select(failure => (failure.Id, failure.Message, failure.File, failure.Line, failure.Log!.Value)));

        // Run with -v, the same lines are streamed under the test's === line before its result line.
        var streamed = Digested(
            [
                "=== RUN   TestRetries",
                "    retry_test.go:9: attempt failed: connection refused",
                "    retry_test.go:9: attempt failed: connection refused",
                "--- FAIL: TestRetries (0.02s)",
                "FAIL",
                "FAIL\texample.com/app/retry\t0.021s",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [("TestRetries", (string?)"retry_test.go:9: attempt failed: connection refused", "retry_test.go", (int?)9, (2, 4))],
            streamed.Failures.Select(failure => (failure.Id, failure.Message, failure.File, failure.Line, failure.Log!.Value)));
    }

    /// <summary>
    /// Every package that did not build is said by its own block, wherever Go prints it: <c>go test ./...</c> prints each
    /// block as its compile fails, out of the packages' order, and their <c>FAIL pkg [build failed]</c> lines later, in it
    /// - another package's results among them - so each head is read whenever it comes, each compiler's line is a
    /// diagnostic of the package whose head was the last, and a package's <c>[build failed]</c> is said only where its own
    /// block said nothing, never where its block said why however much was printed between.
    /// </summary>
    [Fact]
    public void GoPackagesThatDidNotBuild_AreEachSaidByTheirOwnBlock_WhereverGoPrintsIt()
    {
        var digest = Digested(
            [
                "# example.com/app/sum",
                "sum/sum.go:3:9: undefined: x",
                "# example.com/app/tools",
                "tools/tools.go:4:2: undefined: y",
                "# example.com/app/vendored",
                "no required module provides package example.com/other; to add it:",
                "\tgo get example.com/other",
                "--- FAIL: TestStoreLoads (0.00s)",
                "    store_test.go:14: Load(\"first\") = 3, want 4",
                "FAIL",
                "FAIL\texample.com/app/store\t0.004s",
                "FAIL\texample.com/app/sum [build failed]",
                "FAIL\texample.com/app/tools [build failed]",
                "FAIL\texample.com/app/vendored [build failed]",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [
                ("undefined: x", "sum/sum.go", "example.com/app/sum", (1, 2)),
                ("undefined: y", "tools/tools.go", "example.com/app/tools", (3, 4)),
            ],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Message, diagnostic.File!, diagnostic.Project!, diagnostic.Log!.Value)));
        Assert.Equal(
            [
                ("TestStoreLoads", (string?)"example.com/app/store", "failed"),
                ("example.com/app/vendored", null, "errored"),
            ],
            digest.Failures.Select(failure => (failure.Id, failure.Class, failure.Outcome)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// A Go package that did not build is said by its own block, never silenced by another package's failing test: the
    /// <c># pkg</c> line Go prints ahead of its compiler's words holds for the line under it, and a compiler's line
    /// under it is a diagnostic of that package, its project the package's import path, so the digest says the failing
    /// test and the package's compiler error both; where the block said nothing - a missing dependency's words, which
    /// name no file - the <c>FAIL pkg [build failed]</c> line is the failure that says it.
    /// </summary>
    [Fact]
    public void AGoPackageThatDidNotBuild_IsSaidByItsOwnBlock_NeverSilencedByAnotherPackagesTest()
    {
        var digest = Digested(
            [
                "--- FAIL: TestStoreLoads (0.00s)",
                "    store_test.go:14: Load(\"first\") = 3, want 4",
                "FAIL",
                "FAIL\texample.com/app/store\t0.004s",
                "# example.com/app/sum [example.com/app/sum.test]",
                "sum/sum.go:12:9: undefined: total",
                "FAIL\texample.com/app/sum [build failed]",
                "# example.com/app/tools [example.com/app/tools.test]",
                "no required module provides package example.com/other; to add it:",
                "\tgo get example.com/other",
                "FAIL\texample.com/app/tools [build failed]",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "go", null))!;

        Assert.Equal(
            [
                ("TestStoreLoads", (string?)"example.com/app/store", "failed"),
                ("example.com/app/tools", null, "errored"),
            ],
            digest.Failures.Select(failure => (failure.Id, failure.Class, failure.Outcome)));
        Assert.Equal("build failed", digest.Failures[1].Message);
        Assert.Equal(
            [("go", "error", "undefined: total", "sum/sum.go", 12, 9, "example.com/app/sum", (5, 6))],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Severity, diagnostic.Message, diagnostic.File!, diagnostic.Line!.Value, diagnostic.Column!.Value, diagnostic.Project!, diagnostic.Log!.Value)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// A sanitizer's report has crashed the program only where the sanitizer ended it - the line it halts with closed the
    /// report, AddressSanitizer's <c>==pid==ABORTING</c>, MemorySanitizer's <c>Exiting</c>, the last line of its report
    /// whether or not it was told to print its summary (the msan fixture holds its summary, from a program it halted and
    /// one it let run on) - and errored it where the program ran on, as one recovers from UndefinedBehaviorSanitizer's
    /// runtime error by default: the report still said, its place the line it named, never a crash the program did not
    /// make. The lines are the tools' own output: g++ 13's UBSan recovering, and clang 19's MemorySanitizer halting a
    /// program run with <c>print_summary=0</c>.
    /// </summary>
    [Fact]
    public void ASanitizersReport_HasCrashedTheProgramOnlyWhereItAbortedIt()
    {
        var digest = Digested(
            [
                "ubsan.c:5:18: runtime error: signed integer overflow: 2147483647 + 1 cannot be represented in type 'int'",
                "next is -2147483648",
                "==392==WARNING: MemorySanitizer: use-of-uninitialized-value",
                "    #0 0x55555561ffe6 in pick /home/runner/work/app/app/umr.c:5:3",
                "    #1 0x55555561fde9 in main /home/runner/work/app/app/umr.c:11:7",
                "    #2 0x7ffff7c2a1c9 in __libc_start_call_main csu/../sysdeps/nptl/libc_start_call_main.h:58:16",
                "    #3 0x7ffff7c2a28a in __libc_start_main csu/../csu/libc-start.c:360:3",
                "    #4 0x5555555862f4 in _start (/home/runner/work/app/app/umr+0x322f4) (BuildId: 8b79297ecb13ba3876f8c5850e6a6b9dfee7c518)",
                string.Empty,
                "Exiting",
            ],
            PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()))!;

        Assert.Equal(
            [
                ("sanitizer", "errored", "UndefinedBehaviorSanitizer: signed integer overflow: 2147483647 + 1 cannot be represented in type 'int'", (1, 1)),
                ("sanitizer", "crashed", "MemorySanitizer: use-of-uninitialized-value", (3, 10)),
            ],
            digest.Failures.Select(failure => (failure.Framework, failure.Outcome, failure.Id, failure.Log!.Value)));
        Assert.Equal(("ubsan.c", 5), (digest.Failures[0].File!, digest.Failures[0].Line!.Value));
        Assert.Equal(5, digest.Failures[1].Exception!.Stack.Count);
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// Ninja's <c>FAILED:</c> line is read by the one owner of it (<see cref="DssHarness.Core.Build.Ninja"/>), as the
    /// build's own reading of its failed units reads it: the exit code ninja writes between <c>FAILED:</c> and the step's
    /// outputs - a negative one, a Windows crash's - taken off, so the edge whose build failed is the outputs alone,
    /// never the code glued into the edge's name and every diagnostic's project.
    /// </summary>
    [Fact]
    public void NinjasFailedLine_IsReadByItsOneOwner_ItsExitCodeTakenOffTheEdge()
    {
        var digest = Digested(
            ["FAILED: [code=-1073741819] CMakeFiles/app.dir/src/main.cpp.obj"],
            PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null))!;

        Assert.Equal(
            [("ninja", "error", "build of 'CMakeFiles/app.dir/src/main.cpp.obj' failed", "CMakeFiles/app.dir/src/main.cpp.obj", (1, 1))],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Severity, diagnostic.Message, diagnostic.Project!, diagnostic.Log!.Value)));
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// samurai's lines are read as ninja's are, by the one owner of both programs' own words
    /// (<see cref="DssHarness.Core.Build.Ninja.WordOf"/>): its job line for a step that failed, which names the command the
    /// step ran, opens that step's edge - a step whose output said nothing is said by it, and a diagnostic its output said
    /// names it as its project - its status lines close an edge, and its end says only that the build ended. The lines
    /// are samurai's form as its source writes them, told to go on past failing steps as the harness tells it.
    /// </summary>
    [Fact]
    public void SamuraisLines_AreReadAsNinjasAre_ByTheOneOwnerOfTheirWords()
    {
        const string Killed = "/usr/bin/cc -o CMakeFiles/app.dir/main.c.o -c /home/runner/work/app/app/main.c";
        const string Failed = "/usr/bin/cc -o CMakeFiles/app.dir/store.c.o -c /home/runner/work/app/app/store.c";

        var digest = Digested(
            [
                "[1/3] Building C object CMakeFiles/app.dir/main.c.o",
                "[2/3] Building C object CMakeFiles/app.dir/store.c.o",
                $"samu: job terminated due to signal 9: {Killed}",
                $"samu: job failed with status 1: {Failed}",
                "/home/runner/work/app/app/store.c:3:5: error: 'count' undeclared (first use in this function)",
                "samu: cannot make progress due to previous errors",
            ],
            PhaseParsers.ForBuild(new HarnessConfig { Toolchains = { ["gcc"] = new ToolchainConfig { CompilerId = { ["C"] = "GNU" } } } }, "cmake", "gcc"))!;

        Assert.Equal(
            [
                ("ninja", $"build of '{Killed}' failed", Killed, (3, 3)),
                ("gcc", "'count' undeclared (first use in this function)", Failed, (5, 5)),
            ],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Message, diagnostic.Project!, diagnostic.Log!.Value)));
        Assert.Equal(2, digest.Errors);
        Assert.Empty(digest.Unparsed ?? []);
    }

    /// <summary>
    /// MSTest's thrown error is read as one under <c>dotnet test</c>, as xUnit's and NUnit's are: its
    /// <c>Test method X threw exception:</c> head, the error's <c>Type: message</c> line under it with each inner
    /// exception folded onto it after <c> ---&gt; </c>, and the stack in the order the runtime printed it, the inner
    /// one's frames after the <c>Inner Stack Trace</c> marker - the failure's exception whole, where it had been null.
    /// </summary>
    [Fact]
    public void AnMstestThrownError_IsReadAsOne_UnderDotnetTest()
    {
        var digest = Digested(
            [
                "  Failed Demo.Tests.StoreTests.Loads [31 ms]",
                "  Error Message:",
                "   Test method Demo.Tests.StoreTests.Loads threw exception: ",
                "System.InvalidOperationException: The store could not be loaded. ---> System.ArgumentNullException: Value cannot be null. (Parameter 'name')",
                "  Stack Trace:",
                @"    at Demo.Tests.StoreTests.Loads() in D:\a\app\app\tests\StoreTests.cs:line 18",
                "   ----- Inner Stack Trace -----",
                @"    at Demo.Library.Store.Load(String name) in D:\a\app\app\lib\Store.cs:line 9",
                "  Standard Output Messages:",
                " loading the store",
            ],
            PhaseParsers.ForTests(new HarnessConfig(), "dotnet", null))!;

        var failure = Assert.Single(digest.Failures);

        Assert.Equal(("dotnet", "Demo.Tests.StoreTests.Loads"), (failure.Framework, failure.Id));
        Assert.Equal(
            ("System.InvalidOperationException", "The store could not be loaded."),
            (failure.Exception!.Type, failure.Exception.Message));
        Assert.Equal(
            [("System.InvalidOperationException", "The store could not be loaded.", 1), ("System.ArgumentNullException", "Value cannot be null. (Parameter 'name')", 1)],
            CausesOf(failure.Exception));

        return;

        static List<(string Type, string Message, int Frames)> CausesOf(ExceptionRecord thrown)
        {
            var causes = new List<(string, string, int)>();

            for (var cause = thrown; cause is not null; cause = cause.CausedBy.FirstOrDefault())
            {
                causes.Add((cause.Type, cause.Message, cause.Stack.Count));
            }

            return causes;
        }
    }

    /// <summary>
    /// CMake's blank line between a block's paragraphs is decided by the line after it: a paragraph under it takes it
    /// as the separator, a second blank ends the block with it, and any other line ends the block and is offered again
    /// on its own, the blank given back - so the reading holds one line, never the log below it.
    /// </summary>
    [Fact]
    public void ACMakeBlocksBlankLine_IsDecidedByTheLineAfterIt()
    {
        var digest = Digested(
            [
                "CMake Error at CMakeLists.txt:3 (add_subdirectory):",
                "  add_subdirectory given source \"lib/missing\" which is not an existing",
                "  directory.",
                string.Empty,
                string.Empty,
                "-- Configuring incomplete, errors occurred!",
            ],
            PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null))!;

        Assert.Equal(
            [("CMakeLists.txt", (int?)3, "add_subdirectory: add_subdirectory given source \"lib/missing\" which is not an existing directory.", (1, 5))],
            digest.Diagnostics.Select(diagnostic => (diagnostic.File!, diagnostic.Line, diagnostic.Message, diagnostic.Log!.Value)));

        var answers = new List<string>();

        var ended = Digested(
            [
                "CMake Error at CMakeLists.txt:3 (add_subdirectory):",
                "  a message",
                string.Empty,
                "-- something else",
            ],
            [.. PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null).Select(parser => parser.Name == "cmake" ? new Recording(parser, answers) : parser)])!;

        Assert.Equal(["1 Took", "2 Took", "3 Held", "4 Left", "4 Left"], answers);
        var said = Assert.Single(ended.Diagnostics);
        Assert.Equal(("add_subdirectory: a message", (1, 2)), (said.Message, said.Log!.Value));
        Assert.Empty(ended.Unparsed ?? []);
    }

    /// <summary>
    /// eslint's stylish heading - a file alone on a line - is decided by the problem under it: a problem takes it as
    /// its file, and any other line gives it back and is offered again on its own, so the reading holds the one line.
    /// </summary>
    [Fact]
    public void AnEslintHeading_IsDecidedByTheProblemUnderIt()
    {
        var answers = new List<string>();

        var digest = Digested(
            [
                "/home/runner/work/app/app/src/store.js",
                "  4:9  error  'add' is not defined  no-undef",
            ],
            [.. PhaseParsers.ForTests(new HarnessConfig(), "npx", null).Select(parser => parser.Name == "eslint" ? new Recording(parser, answers) : parser)])!;

        Assert.Equal(["1 Held", "2 Took"], answers);
        Assert.Equal(
            [("error", "no-undef", "/home/runner/work/app/app/src/store.js", 4, 9)],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Severity, diagnostic.Code!, diagnostic.File!, diagnostic.Line!.Value, diagnostic.Column!.Value)));

        var noneAnswers = new List<string>();

        var none = Digested(
            ["/tmp/notes.txt", "something else"],
            [.. PhaseParsers.ForTests(new HarnessConfig(), "npx", null).Select(parser => parser.Name == "eslint" ? new Recording(parser, noneAnswers) : parser)]);

        Assert.Null(none);
        Assert.Equal(["1 Held", "2 Left", "2 Left"], noneAnswers);
    }

    /// <summary>
    /// GNU ld's <c>in function</c> line is decided by the reference under it: a reference takes it as the block's first
    /// line, and any other line gives it back and is offered again on its own, so the reading holds the one line.
    /// </summary>
    [Fact]
    public void AnLdInFunctionLine_IsDecidedByTheReferenceUnderIt()
    {
        var answers = new List<string>();

        var digest = Digested(
            [
                "/usr/bin/ld: /tmp/cc.o: in function `main':",
                "/home/u/app/link.c:4: undefined reference to `parse_record'",
            ],
            [.. PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null).Select(parser => parser.Name == "ld" ? new Recording(parser, answers) : parser)])!;

        Assert.Equal(["1 Held", "2 Took"], answers);
        Assert.Equal(
            [("undefined reference to `parse_record'", "/home/u/app/link.c", (int?)4, (1, 2))],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Message, diagnostic.File!, diagnostic.Line, diagnostic.Log!.Value)));

        var noneAnswers = new List<string>();

        var none = Digested(
            ["/usr/bin/ld: /tmp/cc.o: in function `main':", "something"],
            [.. PhaseParsers.ForBuild(new HarnessConfig(), "cmake", null).Select(parser => parser.Name == "ld" ? new Recording(parser, noneAnswers) : parser)]);

        Assert.Null(none);
        Assert.Equal(["1 Held", "2 Left", "2 Left"], noneAnswers);
    }

    /// <summary>
    /// The one blank line Python prints between the parts of a chain is decided by the line after it: the chain's own
    /// marker takes it as the part separator, and any other line ends the chain and is offered again on its own, the
    /// blank given back - so the reading holds one line, never the traceback after it.
    /// </summary>
    [Fact]
    public void APythonChainsBlankLine_IsDecidedByTheLineAfterIt()
    {
        var answers = new List<string>();

        var digest = Digested(
            [
                "Traceback (most recent call last):",
                "  File \"/home/runner/work/app/app/first.py\", line 3, in load",
                "    raise KeyError(\"port\")",
                "KeyError: 'port'",
                string.Empty,
                "During handling of the above exception, another exception occurred:",
                "Traceback (most recent call last):",
                "  File \"/home/runner/work/app/app/store.py\", line 9, in load",
                "    raise ValueError(\"store\")",
                "ValueError: 'store'",
            ],
            [.. PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()).Select(parser => parser.Name == "python-stack" ? new Recording(parser, answers) : parser)])!;

        Assert.Equal(
            ["1 Held", "2 Held", "3 Held", "4 Took", "5 Held", "6 Took", "7 Held", "8 Held", "9 Held", "10 Took"],
            answers);
        Assert.Equal(
            [("python", "ValueError: 'store'", "KeyError", "errored")],
            digest.Failures.Select(failure => (failure.Framework, failure.Id, failure.Exception!.CausedBy[0].Type, failure.Outcome)));

        var endedAnswers = new List<string>();

        var ended = Digested(
            [
                "Traceback (most recent call last):",
                "  File \"/home/runner/work/app/app/first.py\", line 3, in load",
                "    raise KeyError(\"port\")",
                "KeyError: 'port'",
                string.Empty,
                "went on",
            ],
            [.. PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()).Select(parser => parser.Name == "python-stack" ? new Recording(parser, endedAnswers) : parser)])!;

        Assert.Equal(["1 Held", "2 Held", "3 Held", "4 Took", "5 Held", "6 Left", "6 Left"], endedAnswers);
        var said = Assert.Single(ended.Failures);
        Assert.Equal(("KeyError: 'port'", "errored", (1, 4)), (said.Id, said.Outcome, said.Log!.Value));
        Assert.Empty(ended.Unparsed ?? []);
    }

    /// <summary>
    /// The one blank line Go prints between a panic's lines and the goroutine that panicked is decided by the line
    /// after it: the goroutine's head takes it as the panic's, and any other line says the lines were no panic's - both
    /// given back, the line offered again on its own - so the reading holds one line.
    /// </summary>
    [Fact]
    public void AGoPanicsBlankLine_IsDecidedByTheGoroutineUnderIt()
    {
        var answers = new List<string>();

        var digest = Digested(
            [
                "panic: runtime error: index out of range [3] with length 3",
                string.Empty,
                "goroutine 1 [running]:",
                "main.main()",
                "\t/home/runner/work/app/app/main.go:8 +0x1d",
            ],
            [.. PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()).Select(parser => parser.Name == "go-panic" ? new Recording(parser, answers) : parser)])!;

        Assert.Equal(["1 Held", "2 Held", "3 Took", "4 Took", "5 Took"], answers);
        var crashed = Assert.Single(digest.Failures);
        Assert.Equal(("panic: runtime error: index out of range [3] with length 3", "crashed", 1), (crashed.Id, crashed.Outcome, crashed.Exception!.Stack.Count));

        var givenAnswers = new List<string>();

        var given = Digested(
            ["panic: something", "next line"],
            [.. PhaseParsers.ForRun(new HarnessConfig(), new RunnerConfig()).Select(parser => parser.Name == "go-panic" ? new Recording(parser, givenAnswers) : parser)])!;

        Assert.Equal(["1 Held", "2 Left", "2 Left"], givenAnswers);
        Assert.Empty(given.Failures);
        Assert.Equal(["panic: something"], given.Unparsed);
    }

    /// <summary>
    /// The digest of <paramref name="lines"/>, a phase's output as its child printed it, by the one rule that turns a log
    /// into a digest (<see cref="PhaseDigest.Of"/>) - read against a tree that holds none of the files they name, so every
    /// place stays as the tool wrote it and no frame is the tree's.
    /// </summary>
    /// <param name="lines">The lines, each as printed.</param>
    /// <param name="parsers">The parsers chosen for them.</param>
    /// <param name="exit">The code the program that printed them ended with; null, as by default, where that is not known.</param>
    internal static ResultDigest? Digested(IReadOnlyList<string> lines, IReadOnlyList<IResultParser> parsers, int? exit = null)
        => PhaseDigest.Of(new Printed(lines), exit, parsers, new DigestTree(NoTree.Root, null, new NoTree()));

    /// <summary>A phase's output: the lines given, each one line as its child printed it.</summary>
    private sealed class Printed(IReadOnlyList<string> lines) : PhaseOutput
    {
        public override IEnumerable<string> Lines() => lines;
    }

    /// <summary>A tree that holds no file and no directory, and reads nothing from the disk.</summary>
    private sealed class NoTree() : PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create()))
    {
        /// <summary>Where the tree is: a root no place a test's lines name is below.</summary>
        public const string Root = "/no-tree";

        public override bool FileExists(string path) => false;

        public override bool IsLink(string path) => false;

        public override IEnumerable<string> EnumerateDirectories(string path) => [];

        public override Stream OpenRead(string path) => throw new FileNotFoundException($"'{path}' is no file of a tree that holds none.", path);
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
