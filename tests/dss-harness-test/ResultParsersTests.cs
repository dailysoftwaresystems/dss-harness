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
    /// One pass: each line is offered to each reading at most once - the reading with a record open first - and a reading
    /// that leaves what it held gives it back, every held line offered to the others in the order printed, never to it
    /// again; a given-back line no reading takes is said unparsed where it looks like an error; and each reading ends once.
    /// </summary>
    [Fact]
    public void EachLine_IsOfferedToEachReadingOnce_AndWhatOneGivesBackGoesToTheOthers()
    {
        var offered = new List<string>();

        var digest = PhaseParsers.DigestOf(
            ["a1", "a2 error: lost", "x", "b1", "a3"],
            [new Holding(offered), new Taking(offered)]);

        Assert.Equal(
            ["A a1", "A a2 error: lost", "A x", "B a1", "B a2 error: lost", "B x", "B b1", "B a3", "A a3", "A end", "B end"],
            offered);
        Assert.Equal([("taking", "x", (3, 3))], digest!.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Message, diagnostic.Log!.Value)));
        Assert.Equal(["a2 error: lost"], digest.Unparsed);
    }

    /// <summary>
    /// A phase's log is digested as it streams, through to the digest: each line is offered to the readings before the
    /// next is read from the log - never the whole log read first and parsed after - and every line no reading took that
    /// looks like an error is counted as the pass meets it, the first fifty held and the rest said as left out, for the
    /// one bound of those lines is the digest's own.
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
        Assert.Equal(lines.Take(ResultDigest.Bound), digest.Unparsed);
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
