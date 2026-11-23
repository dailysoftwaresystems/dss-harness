using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// Results digested: each built-in parser's fixture gives its stored digest - the compiler forms with their chains,
/// the stack traces with their frames, kinds and in-tree marks, the test consoles with their failures - the digest
/// folds the same diagnostic printed again and holds to its bounds, a matcher is validated at load, and a step
/// attempt the engine did not expect keeps the exception's record.
/// </summary>
public sealed class ResultDigestTests
{
    /// <summary>MSBuild's form reads: the file, the line and column, the code, the project, the message.</summary>
    [Fact]
    public void MsBuild_ReadsItsForm_WithTheProject()
    {
        var (diagnostics, _, _) = new MsBuildParserFixture().Read();

        var one = Assert.Single(diagnostics);

        Assert.Equal(("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, "src/App.csproj"), (one.Tool, one.Severity, one.Code, one.Message, one.File, one.Line, one.Column, one.Project));
    }

    /// <summary>MSBuild folds the same error printed once per target framework into one entry with its count.</summary>
    [Fact]
    public void TheSameDiagnosticPrintedAgain_IsFoldedIntoOneWithItsCount()
    {
        var digest = ResultDigest.Of(
        [
            new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, Project: "src/App.csproj"),
            new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, Project: "src/App.iOS.csproj"),
            new Diagnostic("msbuild", "warning", "CS0219", "The variable 'y' is assigned but its value is never used", "src/Program.cs", 20, 13, Project: "src/App.csproj"),
        ],
            [],
            []);

        Assert.Equal(2, digest.Diagnostics.Count);
        Assert.Equal(2, digest.Diagnostics[0].Count);
        Assert.Equal(1, digest.Diagnostics[1].Count);
        Assert.Equal(1, digest.Errors);
        Assert.Equal(1, digest.Warnings);
        Assert.Equal(digest.Diagnostics[0], digest.First);
    }

    /// <summary>gcc's form reads, with the include chain before the error as its stack.</summary>
    [Fact]
    public void Gcc_ReadsItsForm_WithTheIncludeChain()
    {
        var (diagnostics, _, _) = new GccParserFixture().Read();

        var one = Assert.Single(diagnostics);

        Assert.Equal(("gcc", "error", "-Wunused-variable", "unused variable 'x'", "include/header.h", 1, 9), (one.Tool, one.Severity, one.Code, one.Message, one.File, one.Line, one.Column));
        var include = Assert.Single(one.Stack!);

        Assert.Equal("include", include.Kind);
        Assert.Equal("src/main.c", include.File);
        Assert.Equal(2, include.Line);
    }

    /// <summary>MSVC's form reads with its code, and the linker's unresolved symbol beside it.</summary>
    [Fact]
    public void Msvc_ReadsItsForm_AndTheLinkers()
    {
        var (diagnostics, _, _) = new MsvcParserFixture().Read();

        Assert.Equal(("msvc", "error", "C2065", "'x': undeclared identifier", "src/main.cpp", 5), (diagnostics[0].Tool, diagnostics[0].Severity, diagnostics[0].Code, diagnostics[0].Message, diagnostics[0].File, diagnostics[0].Line));

        var (linked, _, _) = new LinkerParserFixture().Read();
        var unresolved = Assert.Single(linked);

        Assert.Equal(("link", "error", "LNK-undefined"), (unresolved.Tool, unresolved.Severity, unresolved.Code));
        Assert.Contains("unresolved external symbol", unresolved.Message, StringComparison.Ordinal);
    }

    /// <summary>CMake's form reads its command and the message block under it; Ninja names the output that failed.</summary>
    [Fact]
    public void CMakeReadsItsBlock_AndNinjaNamesItsOutput()
    {
        var (cmake, _, _) = new CMakeParserFixture().Read();
        var one = Assert.Single(cmake);

        Assert.Equal(("cmake", "error", "src/CMakeLists.txt", 4, "add_subdirectory"), (one.Tool, one.Severity, one.File, one.Line, one.Project));
        Assert.Contains("Non-existent subdirectory", one.Message, StringComparison.Ordinal);

        var (ninja, _, _) = new NinjaParserFixture().Read();
        var failed = Assert.Single(ninja);

        Assert.Equal(("ninja", "error"), (failed.Tool, failed.Severity));
        Assert.Contains("build.ninja", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>tsc's and the Dart analyser's forms read with their codes and columns.</summary>
    [Fact]
    public void TscAndDart_ReadTheirForms()
    {
        var (tsc, _, _) = new TscParserFixture().Read();
        var one = Assert.Single(tsc);

        Assert.Equal(("tsc", "error", "TS2304", "Cannot find name 'x'.", "src/app.ts", 12, 9), (one.Tool, one.Severity, one.Code, one.Message, one.File, one.Line, one.Column));

        var (dart, _, _) = new DartAnalyserParserFixture().Read();
        var two = Assert.Single(dart);

        Assert.Equal(("dart", "warning", "unused_local_variable", "The value of the local variable 'x' isn't used.", "lib/main.dart", 7, 9), (two.Tool, two.Severity, two.Code, two.Message, two.File, two.Line, two.Column));
    }

    /// <summary>A .NET stack trace reads: its exception, every frame with its file and line, and the inner-exception cause.</summary>
    [Fact]
    public void ADotNetStack_ReadsItsException_ItsFrames_AndItsCause()
    {
        var (_, failures, _) = new DotNetStackFixture().Read();

        var one = Assert.Single(failures);

        Assert.NotNull(one.Exception);
        Assert.Equal("System.InvalidOperationException", one.Exception.Type);
        Assert.Equal("The operation is invalid.", one.Exception.Message);
        Assert.Equal(2, one.Exception.Stack.Count);
        Assert.Equal(("DssHarness.Core.Runs.Runner.RunAsync(RunnerRequest request)", "D:/src/Runner.cs", 42), (one.Exception.Stack[0].Symbol, one.Exception.Stack[0].File, one.Exception.Stack[0].Line));

        var cause = Assert.Single(one.Exception.CausedBy);

        Assert.Equal("System.ArgumentException", cause.Type);
        Assert.Equal("The argument was wrong.", cause.Message);
    }

    /// <summary>A JVM stack reads its frames and its <c>Caused by</c>; a Python traceback reads its frames innermost first.</summary>
    [Fact]
    public void JvmAndPython_ReadTheirStacks()
    {
        var (_, jvm, _) = new JvmStackFixture().Read();

        Assert.NotEmpty(jvm);
        var one = Assert.Single(jvm);
        var frame = Assert.Single(one.Exception!.Stack);

        Assert.Equal("com.example.Thing.doIt", frame.Symbol);
        Assert.Equal("Thing.java", frame.File);
        Assert.Equal(12, frame.Line);

        var (_, python, _) = new PythonStackFixture().Read();
        var thrown = Assert.Single(python);

        Assert.Equal("ValueError", thrown.Exception!.Type);
        Assert.Equal("not a number", thrown.Exception.Message);
        var innermost = thrown.Exception.Stack[0];

        Assert.Equal("inner", innermost.Symbol);
    }

    /// <summary>The xUnit console's failed tests read with their messages; gtest's and ctest's lists read their names.</summary>
    [Fact]
    public void TheTestConsoles_ReadTheirFailures()
    {
        var (_, xunit, _) = new XUnitConsoleFixture().Read();
        var one = Assert.Single(xunit);

        Assert.Equal(("xunit", "DssHarness.Tests.Demo.One", "One", "DssHarness.Tests.Demo"), (one.Framework, one.Id, one.Name, one.Class));
        Assert.Equal("Expected: 2, Actual: 3", one.Message);

        var (_, gtest, _) = new GTestConsoleFixture().Read();

        Assert.Equal("gtest", Assert.Single(gtest).Framework);

        var (_, ctest, _) = new CTestConsoleFixture().Read();
        var failed = Assert.Single(ctest);

        Assert.Equal("ctest", failed.Framework);
        Assert.Equal("demo_test", failed.Id);
    }

    /// <summary>A stack longer than the bound keeps its first frames and its last, with the cut said.</summary>
    [Fact]
    public void AStackOverTheBound_KeepsItsFirstAndLastFrames()
    {
        var frames = Enumerable.Range(0, 100).Select(at => new DssHarness.Core.Results.StackFrame($"at Frame{at}()")).ToList();

        var held = StackTrace.Held(frames, out var cut);

        Assert.Equal(StackTrace.FrameBound, held.Count);
        Assert.Equal(36, cut);
        Assert.Equal("at Frame0()", held[0].Text);
        Assert.Equal("at Frame99()", held[^1].Text);
        Assert.Equal("at Frame47()", held[StackTrace.FirstKept - 1].Text);
        Assert.Equal("at Frame84()", held[StackTrace.FirstKept].Text);
    }

    /// <summary>A matcher that does not read - a pattern that does not compile, a group it does not name - is refused at load.</summary>
    [Fact]
    public void AMatcherThatDoesNotRead_IsRefusedAtLoad()
    {
        var broken = new ProblemMatcher("mytool", "(?<file>[", Severity: "error", File: "file", Line: "line", Message: "message");
        broken.Compile();

        Assert.NotNull(broken.Problem());

        var missing = new ProblemMatcher("mytool", @"^(?<file>.+)$", Severity: "error", File: "file", Line: "line", Message: "message");
        missing.Compile();

        Assert.NotNull(missing.Problem());
        Assert.Contains("names no group 'line'", missing.Problem(), StringComparison.Ordinal);

        var reading = new ProblemMatcher("mytool", @"^(?<file>.+):(?<line>\d+): (?<message>.*)$", Severity: "error", File: "file", Line: "line", Message: "message");
        reading.Compile();

        Assert.Null(reading.Problem());
    }

    /// <summary>
    /// A matcher's patterns are compiled as every pattern the tool runs is, linear in what they read with no timeout: one
    /// that holds what that matcher lacks - its pattern, its frame's, its end's - refuses the configuration at load, naming
    /// the matcher, which pattern and the construct, in the words every refused pattern of a configuration is said in; and
    /// a matcher that reads compiles all three, so its frames are read with no engine of their own.
    /// </summary>
    [Fact]
    public void AMatchersPatternTheLinearMatcherLacks_RefusesTheConfigurationAtLoad_NamingIt()
    {
        static IReadOnlyList<string> Problems(ProblemMatcher matcher)
            => [.. HarnessConfigValidator.Validate(new HarnessConfig { Results = new ResultsConfig { Matchers = [matcher] } })
                .Where(problem => problem.StartsWith("results.", StringComparison.Ordinal))];

        Assert.Equal(
            ["results.matchers: matcher 'mytool': its pattern is not a valid regular expression: The pattern '^(?<file>.+)(?=:)' holds a lookahead or lookbehind, which .NET's linear-time matcher lacks."],
            Problems(new ProblemMatcher("mytool", "^(?<file>.+)(?=:)", Severity: "error", File: "file")));
        Assert.Equal(
            [@"results.matchers: matcher 'mytool': its frame pattern is not a valid regular expression: The pattern '(?<file>\w+)\1' holds a backreference, which .NET's linear-time matcher lacks."],
            Problems(new ProblemMatcher("mytool", "^(?<file>.+)$", Severity: "error", File: "file", Frame: @"(?<file>\w+)\1")));
        Assert.Equal(
            ["results.matchers: matcher 'mytool': its until pattern is not a valid regular expression: The pattern '(?>end)' holds an atomic group, which .NET's linear-time matcher lacks."],
            Problems(new ProblemMatcher("mytool", "^(?<file>.+)$", Severity: "error", File: "file", Frame: "(?<file>.+)", Until: "(?>end)")));

        var reading = new ProblemMatcher("mytool", "^(?<file>.+)$", Severity: "error", File: "file", Frame: "^  at (?<file>.+)$", Until: "^end$");

        Assert.Empty(Problems(reading));
        Assert.Equal(
            [RegexOptions.NonBacktracking, RegexOptions.NonBacktracking, RegexOptions.NonBacktracking],
            new[] { reading.Compiled!, reading.FrameCompiled!, reading.UntilCompiled! }.Select(regex => regex.Options & RegexOptions.NonBacktracking));
    }

    /// <summary>
    /// A frame a matcher reads names its line and column only where its own pattern captured a whole number there: one
    /// that captured anything else names no place, as a diagnostic's line does, and the digest is read on, never stopped.
    /// </summary>
    [Fact]
    public void AMatchersFrameWhoseLineIsNoNumber_NamesNoLine_AndTheDigestIsReadOn()
    {
        var matcher = new ProblemMatcher("mytool", @"^ERROR (?<message>.+)$", Severity: "error", Message: "message", Frame: @"^  at (?<file>\S+):(?<line>\w+)$");
        matcher.Compile();

        var digest = PhaseParsers.DigestOf(["ERROR it broke", "  at src/a.c:twelve", "  at src/b.c:7"], PhaseParsers.ForBuild(null, [matcher]))!;

        Assert.Equal(
            [("src/a.c", (int?)null), ("src/b.c", 7)],
            digest.First!.Stack!.Select(frame => (frame.File, frame.Line)));
    }

    /// <summary>The digest's bounds: at most 50 diagnostics and 50 failures, the rest folded and counted.</summary>
    [Fact]
    public void TheDigest_HoldsToItsBounds()
    {
        var diagnostics = Enumerable.Range(0, 80).Select(at => new Diagnostic("msbuild", "error", $"CS{at:0000}", $"message {at}", "src/x.cs", at + 1)).ToList();
        var digest = ResultDigest.Of(diagnostics, [], []);

        Assert.Equal(ResultDigest.Bound, digest.Diagnostics.Count);
        Assert.Equal(80, digest.Errors);
    }

    /// <summary>A step attempt the engine did not expect keeps the exception's record - type, message, stack, cause.</summary>
    [Fact]
    public void AnUnexpectedException_IsKeptOnTheAttempt()
    {
        var attempt = new FlowStepAttempt(1, Exception: new DssHarness.Core.Results.ExceptionRecord(
            "System.InvalidOperationException",
            "The operation is invalid.",
            [new DssHarness.Core.Results.StackFrame("   at N.C.M() in src/x.cs:line 12", "src/x.cs", 12, Symbol: "N.C.M")],
            [new DssHarness.Core.Results.ExceptionRecord("System.ArgumentException", "The argument was wrong.", [], [])]));

        Assert.Null(attempt.Problem());
        Assert.Equal("System.InvalidOperationException", attempt.Exception!.Type);
        Assert.Equal("src/x.cs", attempt.Exception.Stack[0].File);
        Assert.Equal("System.ArgumentException", attempt.Exception.CausedBy[0].Type);
    }

    /// <summary>
    /// The parsers a phase's log is read by are chosen: an MSVC build's by its toolchain, a test's by its runner, and
    /// a configured matcher everywhere - so a log no rule names is left as it stands, and a matcher's findings fold
    /// with the built-ins'.
    /// </summary>
    [Fact]
    public void APhaseLog_IsDigestedByTheParsersChosenForIt_MatchersAmongThem()
    {
        var msvc = PhaseParsers.ForBuild("msvc", []);
        var gcc = PhaseParsers.ForBuild("gcc", []);
        var none = PhaseParsers.ForTests("tools/run-tests", []);
        var ctest = PhaseParsers.ForTests("ctest", []);

        Assert.Contains(msvc, parser => parser.Name == "msvc");
        Assert.Contains(gcc, parser => parser.Name == "gcc");
        Assert.DoesNotContain(gcc, parser => parser.Name == "msvc");
        Assert.Empty(none);
        Assert.Contains(ctest, parser => parser.Name == "ctest-console");

        var mine = new ProblemMatcher(
            "mytool",
            @"^(?<file>[^:]+):(?<line>\d+): (?<message>ERROR .+)$",
            Severity: "error",
            File: "file",
            Line: "line",
            Message: "message");
        mine.Compile();

        var digest = PhaseParsers.DigestOf(
            ["mytool.cpp:12: ERROR the frobnicator jammed", "unrelated line"],
            PhaseParsers.ForBuild(null, [mine]));

        var first = digest!.First!;

        Assert.Equal(("mytool", "error", "mytool.cpp", 12), (first.Tool, first.Severity, first.File, first.Line));
        Assert.Equal("ERROR the frobnicator jammed", first.Message);
        Assert.Empty(digest?.Unparsed ?? []);
    }

    /// <summary>
    /// Every frame of every stack is marked by whether the tree holds its file, on the machine's own file system: a rooted
    /// file under the tree and a relative one read from where the tool ran are in it, named relative to its root; a file
    /// the tree does not hold under it, and a rooted file elsewhere, are not - the one place a frame is marked, so every
    /// reader reads one.
    /// </summary>
    [Fact]
    public void EveryFrameOfEveryStack_IsMarkedByWhetherTheTreeHoldsItsFile()
    {
        using var temp = new TempDirectory();
        var main = temp.WriteFile(Path.Combine("src", "main.c"), "int main;");
        var elsewhere = Path.Combine(Path.GetPathRoot(temp.Path) ?? "/", "elsewhere", "out.c");
        var digest = ResultDigest.Of(
            [new Diagnostic("gcc", "error", null, "jammed", "src/main.c", 3, 9, Stack:
            [
                new StackFrame("in main.c", main, 3),
                new StackFrame("from ../src/main.c", "../src/main.c", 1),
                new StackFrame("in src/gone.c", Path.Combine(temp.Path, "src", "gone.c"), 12),
                new StackFrame("elsewhere", elsewhere, 1),
            ])],
            [],
            []);

        var marked = digest.WithInTree(new DigestTree(temp.Path, temp.Combine("build"), new PhysicalFileSystem(FilePermissionsFactory.Create())));

        Assert.Equal(
            [("src/main.c", true), ("src/main.c", true), (Path.Combine(temp.Path, "src", "gone.c"), false), (elsewhere, false)],
            marked.Diagnostics[0].Stack.Select(frame => (frame.File, frame.InTree)));
    }

    /// <summary>The digest as one JSON document keeps everything: every diagnostic and failure whole, its counts, its unparsed lines.</summary>
    [Fact]
    public void TheDocument_KeepsEverythingWhole()
    {
        var digest = ResultDigest.Of(
            [new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", "src/Program.cs", 12, 35, Count: 2, Log: (10, 11))],
            [],
            ["a stray line"]);

        var document = digest.Document();

        Assert.Equal(1, document["errors"]!.GetValue<int>());
        Assert.Equal("CS0103", document["diagnostics"]![0]!["code"]!.GetValue<string>());
        Assert.Equal(2, document["diagnostics"]![0]!["count"]!.GetValue<int>());
        Assert.Equal([10, 11], document["diagnostics"]![0]!["log"]!.AsArray().Select(line => line!.GetValue<int>()));
        Assert.Equal(["a stray line"], document["unparsed"]!.AsArray().Select(line => line!.GetValue<string>()));
    }

    /// <summary>
    /// A digest's document reads back as the digest it was written from - every diagnostic, failure, frame, cause and
    /// count, each stack's and each cause chain's cut, the counts by code and by file and what the bounds left out, its
    /// first error the first of its errors - so another machine's ledger, which carries a leg's digest as that machine's
    /// --json wrote it, is read by the names it was written under; every text it holds read through what each line
    /// another machine printed becomes on this one.
    /// </summary>
    [Fact]
    public void ADigestsDocument_ReadsBackAsTheDigest_EachTextAsThisMachineSaysIt()
    {
        IReadOnlyList<StackFrame> deep = [.. Enumerable.Range(1, StackTrace.FrameBound + 6).Select(at => new StackFrame($"in src/Program.cs:{at.ToString(CultureInfo.InvariantCulture)}", "src/Program.cs", at, Kind: "include", InTree: true))];
        var chain = new ExceptionRecord("System.IO.IOException", "inner at 192.0.2.10", [], []);

        for (var depth = 0; depth < ExceptionRecord.CauseBound + 2; depth++)
        {
            chain = new ExceptionRecord("System.InvalidOperationException", depth == ExceptionRecord.CauseBound + 1 ? "outer" : "wrapping at 192.0.2.10", [new StackFrame("at A()", "tests/A.cs", 45, InTree: true)], [chain]);
        }

        var digest = ResultDigest.Of(
            [
                new Diagnostic("gcc", "warning", "-Wunused", "unused at 192.0.2.10", "src/a.c", 3, 9, 3, 12, "main", "app", Log: (4, 4)),
                new Diagnostic("msbuild", "error", "CS0103", "no x at 192.0.2.10", "src/Program.cs", 12, 35, Count: 2, Stack: deep, Log: (10, 11)),
            ],
            [
                new TestFailure(
                    "xunit",
                    "App.Tests.Holds",
                    "Holds",
                    "App.Tests",
                    "Holds",
                    "expected 1",
                    "1",
                    "2",
                    "tests/A.cs",
                    45,
                    chain,
                    null,
                    0.25,
                    "errored",
                    (2, 3)),
            ],
            [.. Enumerable.Range(1, ResultDigest.Bound + 3).Select(at => $"a stray line {at.ToString(CultureInfo.InvariantCulture)} from 192.0.2.10")]);

        var read = ResultDigest.Read(digest.Document());

        Assert.Equal(digest.Document().ToJsonString(), read!.Document().ToJsonString());
        Assert.Same(read.Diagnostics[1], read.First);
        var cut = read.Failures[0].Exception!;

        for (var depth = 1; depth < ExceptionRecord.CauseBound; depth++)
        {
            cut = cut.CausedBy[0];
        }

        // The cuts the bounds made are read back as they were said: six frames of seventy, the three records below the
        // eighth, and the three unparsed lines past the fifty held.
        Assert.Equal((6, 3, ResultDigest.Bound + 3), (read.Diagnostics[1].StackCut, cut.CausesCut, read.UnparsedCount));
        Assert.Null(ResultDigest.Read(null));

        var said = ResultDigest.Read(digest.Document(), text => text.Replace("192.0.2.10", "mac", StringComparison.Ordinal))!;

        Assert.Equal(
            ["unused at mac", "no x at mac", "wrapping at mac", "a stray line 1 from mac"],
            [said.Diagnostics[0].Message, said.Diagnostics[1].Message, said.Failures[0].Exception!.CausedBy[0].Message, said.Unparsed[0]]);
    }

    /// <summary>A document of no digest's shape, or holding what no digest does, is refused, naming what is wrong - never read as less than it holds.</summary>
    [Theory]
    [InlineData("""{"diagnostics":{}}""", "'diagnostics' is no list")]
    [InlineData("""{"diagnostics":[{"tool":"gcc","severity":"error"}]}""", "'message' is missing")]
    [InlineData("""{"diagnostics":[{"tool":"gcc","severity":"error","message":"m","log":[1]}]}""", "'log' is no range of two lines")]
    [InlineData("""{"diagnostics":[{"tool":"gcc","severity":"fatal","message":"m"}]}""", "'fatal' is not a severity a diagnostic has")]
    [InlineData("""{"failures":[{"framework":"xunit","id":"t","outcome":"failed","duration":"long"}]}""", "'duration' is no number")]
    [InlineData("""{"failures":[{"framework":"xunit","id":"t","outcome":"failed","stack":[{"text":"at A()","inTree":"yes"}]}]}""", "'inTree' is no flag")]
    [InlineData("""[]""", "a digest is no object")]
    [InlineData("""{"errors":1,"leftOut":{"diagnostics":2}}""", "it says it left out other than its counts and what it holds make")]
    [InlineData("""{"failures":[{"framework":"xunit","id":"t","outcome":"failed","exception":{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m","causedBy":[{"type":"T","message":"m"}]}]}]}]}]}]}]}]}}],"failed":1}""", "a cause chain is deeper than its bound of 8")]
    public void ADocumentOfNoDigestsShape_IsRefused_NamingWhatIsWrong(string document, string problem)
        => Assert.Equal(problem, Assert.Throws<System.Text.Json.JsonException>(() => ResultDigest.Read(System.Text.Json.Nodes.JsonNode.Parse(document))).Message);

    private static IReadOnlyList<string> Lines(params string[] lines) => lines;

    private sealed class MsBuildParserFixture : IResultParser
    {
        public string Name => "fixture";

        public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string>? lines = null)
            => DssHarness.Core.Results.ResultParsers.Of("msbuild")!.Read(lines ?? Lines("src/Program.cs(12,35): error CS0103: The name 'x' does not exist in the current context [src/App.csproj]"));

        (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) IResultParser.Read(IReadOnlyList<string> lines)
            => Read();
    }

    private sealed class GccParserFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("gcc")!.Read(Lines(
                "In file included from src/main.c:2:",
                "include/header.h:1:9: error: unused variable 'x' [-Wunused-variable]",
                "  1 | int x;"));
    }

    private sealed class MsvcParserFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("msvc")!.Read(Lines("src/main.cpp(5): error C2065: 'x': undeclared identifier"));
    }

    private sealed class LinkerParserFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("link")!.Read(Lines("main.obj : error LNK2019: unresolved external symbol \"void __cdecl f(void)\" (?f@@YAXXZ) referenced in function main"));
    }

    private sealed class CMakeParserFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("cmake")!.Read(Lines(
                "CMake Error at src/CMakeLists.txt:4 (add_subdirectory):",
                "  Non-existent subdirectory: missing"));
    }

    private sealed class NinjaParserFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("ninja")!.Read(Lines("FAILED: build.ninja"));
    }

    private sealed class TscParserFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("tsc")!.Read(Lines("src/app.ts(12,9): error TS2304: Cannot find name 'x'."));
    }

    private sealed class DartAnalyserParserFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("dart")!.Read(Lines("warning • The value of the local variable 'x' isn't used. • lib/main.dart:7:9 • unused_local_variable"));
    }

    private sealed class DotNetStackFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("dotnet-stack")!.Read(Lines(
                "System.InvalidOperationException: The operation is invalid.",
                "   at DssHarness.Core.Runs.Runner.RunAsync(RunnerRequest request) in D:/src/Runner.cs:line 42",
                "   at DssHarness.Cli.Program.Main(String[] args) in D:/src/Program.cs:line 10",
                "--- End of inner exception stack trace ---",
                "System.ArgumentException: The argument was wrong.",
                "   at DssHarness.Core.Runs.Runner.Check(RunnerRequest request)"));
    }

    private sealed class JvmStackFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("jvm-stack")!.Read(Lines(
                "java.lang.IllegalStateException: The state was wrong",
                "        at com.example.Thing.doIt(Thing.java:12)",
                "Caused by: java.lang.NullPointerException: null"));
    }

    private sealed class PythonStackFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("python-stack")!.Read(Lines(
                "Traceback (most recent call last):",
                "  File \"outer.py\", line 3, in outer",
                "  File \"inner.py\", line 7, in inner",
                "ValueError: not a number"));
    }

    private sealed class XUnitConsoleFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("xunit-console")!.Read(Lines(
                "  Failed DssHarness.Tests.Demo.One [12 ms]",
                "  Error Message:",
                "   Expected: 2, Actual: 3"));
    }

    private sealed class GTestConsoleFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("gtest-console")!.Read(Lines("[  FAILED  ] Demo.One (12 ms)"));
    }

    private sealed class CTestConsoleFixture
    {
        public (IReadOnlyList<Diagnostic>, IReadOnlyList<TestFailure>, IReadOnlyList<string>) Read()
            => ResultParsers.Of("ctest-console")!.Read(Lines(
                "The following tests FAILED:",
                "\t 3 - demo_test (Failed)"));
    }
}
