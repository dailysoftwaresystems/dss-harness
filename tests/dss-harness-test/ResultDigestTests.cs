using System.Globalization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// Results digested: the digest folds the same diagnostic printed again and holds to its bounds, marks every frame by
/// whether it is the tree's, says itself whole as one document, and a step attempt the engine did not expect keeps the
/// exception's record. The parsers' own reading is held by ResultParsersTests and the fixtures.
/// </summary>
public sealed class ResultDigestTests
{

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
}
