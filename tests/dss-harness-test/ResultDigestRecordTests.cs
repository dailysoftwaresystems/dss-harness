using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The digest as a record: made in one place, a finding at a time, folding a repeat where it stands so the first error is
/// the first the tool met; holding each severity, the failures and the unparsed lines to their bound with the rest counted
/// over the whole output; holding every stack and cause chain to its bound with the cut said; saying a finding it cannot
/// hold rather than dropping it; reading every place against the tree once; masking every text it holds; and rendering
/// for a model the first problem once with its causes, the next three, and the raw lines only where it holds a problem.
/// </summary>
public sealed class ResultDigestRecordTests
{
    /// <summary>
    /// A diagnostic printed again is folded into the one held where it stands, its count grown by the printings it
    /// carries: the first error is the first the tool met, never one printed after it, and a digest gathered from digests
    /// already folded counts every printing.
    /// </summary>
    [Fact]
    public void ARepeatIsFoldedWhereItStands_SoTheFirstErrorIsTheFirstMet()
    {
        var header = new Diagnostic("gcc", "error", null, "unknown type name 'u32'", "h.h", 3, 5);
        var digest = ResultDigest.Of(
            [header, new Diagnostic("gcc", "error", null, "'x' undeclared", "a.c", 10, 3), header with { Count = 2 }],
            [],
            []);

        Assert.Equal([("h.h", 3), ("a.c", 1)], digest.Diagnostics.Select(diagnostic => (diagnostic.File!, diagnostic.Count)));
        Assert.Same(digest.Diagnostics[0], digest.First);
        Assert.Equal("gcc h.h(3,5): unknown type name 'u32' (printed 3 times)", digest.FirstSaid());
        Assert.Equal(2, digest.Errors);
    }

    /// <summary>
    /// Each severity is held to its bound apart, so no number of warnings displaces an error: sixty warnings then one error
    /// hold fifty warnings and the error, the first error is held, and the counts by severity, code and file are over all
    /// sixty-one - the ten left out said as left out.
    /// </summary>
    [Fact]
    public void AnErrorIsNeverDisplacedByWarnings_AndTheRestIsCountedOverTheWhole()
    {
        var warnings = Enumerable.Range(1, 60).Select(at => new Diagnostic("msbuild", "warning", "CS8618", $"field {at} is null", $"src/W{at % 3}.cs", at));
        var error = new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist", "src/E.cs", 4);

        var digest = ResultDigest.Of([.. warnings, error], [], []);

        Assert.Equal(51, digest.Diagnostics.Count);
        Assert.Equal(50, digest.Diagnostics.Count(diagnostic => diagnostic.Severity == "warning"));
        Assert.Same(digest.Diagnostics[^1], digest.First);
        Assert.Equal((1, 60, 0), (digest.Errors, digest.Warnings, digest.Notes));
        Assert.Equal(10, digest.DiagnosticsLeftOut);
        Assert.Equal([new Counted("CS8618", 60), new Counted("CS0103", 1)], digest.ByCode);
        Assert.Equal([new Counted("src/W1.cs", 20), new Counted("src/W2.cs", 20), new Counted("src/W0.cs", 20), new Counted("src/E.cs", 1)], digest.ByFile);
        Assert.Null(digest.Problem());

        var document = digest.Document();

        Assert.Equal("msbuild CS0103 src/E.cs(4): The name 'x' does not exist", document["first"]!.GetValue<string>());
        Assert.Equal(10, document["leftOut"]!["diagnostics"]!.GetValue<int>());
        Assert.Equal(60, document["byCode"]![0]!["count"]!.GetValue<int>());
    }

    /// <summary>
    /// The failures are held to their bound with every one counted by its outcome, and the unparsed lines to theirs with
    /// every one counted: what the bound left out is said, never silent.
    /// </summary>
    [Fact]
    public void FailuresAndUnparsedLines_AreHeldToTheirBound_EveryOneCounted()
    {
        var failures = Enumerable.Range(1, 120).Select(at => new TestFailure("trx", $"T{at}", Outcome: at % 2 == 0 ? "failed" : "errored"));
        var unparsed = Enumerable.Range(1, 70).Select(at => $"error line {at}");

        var digest = ResultDigest.Of([], failures, unparsed);

        Assert.Equal((ResultDigest.Bound, ResultDigest.Bound), (digest.Failures.Count, digest.Unparsed.Count));
        Assert.Equal((60, 60, 70), (digest.Failed, digest.Errored, digest.UnparsedCount));
        Assert.Equal((70, 20), (digest.FailuresLeftOut, digest.UnparsedLeftOut));
        Assert.Equal("T1", digest.Failures[0].Id);
        Assert.Equal("error line 50", digest.Unparsed[^1]);
    }

    /// <summary>
    /// The first failure is the first the runner met whatever its outcome: a digest whose only failure timed out says it
    /// first - on the ledger's line and to a model - and a model is handed it once, never again among the failures after it.
    /// </summary>
    [Fact]
    public void TheFirstFailure_IsTheFirstMet_WhateverItsOutcome_AndSaidOnce()
    {
        var digest = ResultDigest.Of(
            [],
            [
                new TestFailure("ctest", "store_times_out", Message: "Test timeout", Outcome: "timedOut"),
                new TestFailure("ctest", "demo_test", Message: "exit 1"),
            ],
            []);

        Assert.Equal("ctest store_times_out timedOut: Test timeout", digest.FirstSaid());

        var model = digest.ForAModel();

        Assert.Equal(("store_times_out", "timedOut", "ctest store_times_out timedOut: Test timeout"), (model["first"]!["id"]!.GetValue<string>(), model["first"]!["outcome"]!.GetValue<string>(), model["first"]!["said"]!.GetValue<string>()));
        Assert.Equal(["demo_test"], model["failures"]!.AsArray().Select(failure => failure!["id"]!.GetValue<string>()));
    }

    /// <summary>
    /// A thrown error a stack parser names by its type and message is said once: its message is the failure's own where
    /// the failure holds none, and where the test's name is already that message it is not said again after it.
    /// </summary>
    [Fact]
    public void AThrownErrorNamedByItsMessage_IsSaidOnce()
    {
        var thrown = new ExceptionRecord("System.ArgumentException", "An item with the same key has already been added.", [], []);
        var named = ResultDigest.Of([], [new TestFailure("dotnet", "System.ArgumentException: An item with the same key has already been added.", Exception: thrown, Outcome: "errored")], []);
        var test = ResultDigest.Of([], [new TestFailure("trx", "Demo.Loads", Exception: thrown)], []);

        Assert.Equal("dotnet System.ArgumentException: An item with the same key has already been added. errored", named.FirstSaid());
        Assert.Equal("trx Demo.Loads: System.ArgumentException: An item with the same key has already been added.", test.FirstSaid());
    }

    /// <summary>
    /// A stack past the frame bound is held to its first frames and its last with the cut counted, where every record of
    /// the digest is held - a diagnostic's chain, a failure's stack, an exception's and each cause's - and the cut is said
    /// to a model where it was made and kept in the document.
    /// </summary>
    [Fact]
    public void AStackPastTheBound_IsHeldWithItsCutSaid_InTheRecordTheDocumentAndToAModel()
    {
        var frames = Enumerable.Range(0, 100).Select(at => new StackFrame($"at Frame{at}()")).ToList();
        var digest = ResultDigest.Of(
            [new Diagnostic("gcc", "error", null, "deep", Stack: frames)],
            [new TestFailure("dotnet", "Thrown", Exception: new ExceptionRecord("System.Exception", "boom", frames, []))],
            []);

        Assert.Equal((StackTrace.FrameBound, 36), (digest.First!.Stack.Count, digest.First.StackCut));
        Assert.Equal((StackTrace.FrameBound, 36), (digest.Failures[0].Exception!.Stack.Count, digest.Failures[0].Exception!.StackCut));
        Assert.Equal(36, digest.Document()["diagnostics"]![0]!["stackCut"]!.GetValue<int>());

        var rendered = digest.ForAModel()["first"]!["stack"]!.AsArray().Select(line => line!.GetValue<string>()).ToList();

        Assert.Equal(
            ["at Frame0()", "... 46 frames outside the tree", "at Frame47()", "... 36 frames left out of the record; the log holds them", "at Frame84()", "... 14 frames outside the tree", "at Frame99()"],
            rendered);
    }

    /// <summary>
    /// A cause chain is followed eight deep, the records below counted on the eighth, never left out unsaid; the document
    /// keeps the chain whole down to its cut, each cause beneath the one it caused; a model reads each cause under its error.
    /// </summary>
    [Fact]
    public void ACauseChain_IsFollowedToItsBound_TheRestCounted_AndRenderedUnderItsError()
    {
        var chain = new ExceptionRecord("Cause12", "the deepest", [], []);

        for (var depth = 11; depth >= 1; depth--)
        {
            chain = new ExceptionRecord($"Cause{depth}", $"cause {depth}", [new StackFrame($"at C{depth}()")], [chain]);
        }

        var digest = ResultDigest.Of([], [new TestFailure("jvm", "Chained", Exception: chain)], []);
        var held = digest.Failures[0].Exception!;
        var eighth = held;

        for (var depth = 1; depth < ExceptionRecord.CauseBound; depth++)
        {
            eighth = Assert.Single(eighth.CausedBy);
        }

        Assert.Equal(("Cause8", 4), (eighth.Type, eighth.CausesCut));
        Assert.Empty(eighth.CausedBy);
        Assert.Null(held.Problem());

        var document = digest.Document()["failures"]![0]!["exception"]!;

        Assert.Equal("Cause2", document["causedBy"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("Cause3", document["causedBy"]![0]!["causedBy"]![0]!["type"]!.GetValue<string>());

        var rendered = digest.ForAModel()["first"]!["stack"]!.AsArray().Select(line => line!.GetValue<string>()).ToList();

        Assert.Equal(
            ["at C1()", "caused by Cause2: cause 2", "at C2()", "caused by Cause3: cause 3", "at C3()", "caused by Cause4: cause 4", "at C4()",
                "caused by Cause5: cause 5", "at C5()", "caused by Cause6: cause 6", "at C6()", "caused by Cause7: cause 7", "at C7()",
                "caused by Cause8: cause 8", "at C8()", "... 4 causes deeper, left out of the record"],
            rendered);
    }

    /// <summary>A record whose chain is deeper than its bound says so: a cause depth is checked, never a count of siblings.</summary>
    [Fact]
    public void ARecordPastItsCauseDepth_SaysItsProblem_ManySiblingsDoNot()
    {
        var siblings = new ExceptionRecord("System.AggregateException", "many", [], [.. Enumerable.Range(0, 20).Select(at => new ExceptionRecord("Inner", $"inner {at}", [], []))]);
        var deep = new ExceptionRecord("Cause9", "nine", [], []);

        for (var depth = 8; depth >= 1; depth--)
        {
            deep = new ExceptionRecord($"Cause{depth}", $"cause {depth}", [], [deep]);
        }

        Assert.Null(siblings.Problem());
        Assert.Equal("its cause chain is over 8 deep", deep.Problem());
        Assert.Null(deep.Held().Problem());
    }

    /// <summary>
    /// The engine's own error is kept as the runtime gave it: each inner exception nested under the one it caused, every one
    /// of an aggregate's, the stack's frames with the file and line the runtime printed.
    /// </summary>
    [Fact]
    public void TheEnginesOwnError_IsKeptNested_ItsFramesPlaced()
    {
        Exception thrown;

        try
        {
            Throws();
            return;
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        var record = ExceptionRecord.Of(thrown);

        Assert.Equal(("System.InvalidOperationException", "outer"), (record.Type, record.Message));

        var aggregate = Assert.Single(record.CausedBy);

        Assert.Equal("System.AggregateException", aggregate.Type);
        Assert.Equal(["System.ArgumentException", "System.FormatException"], aggregate.CausedBy.Select(cause => cause.Type));
        Assert.Contains(record.Stack, frame => frame.Symbol is { } symbol && symbol.Contains(nameof(Throws), StringComparison.Ordinal));
        Assert.All(record.Stack.Where(frame => frame.File is not null), frame => Assert.NotNull(frame.Line));
    }

    /// <summary>Throws an error wrapping an aggregate of two, as an engine's step might.</summary>
    private static void Throws()
    {
        try
        {
            throw new AggregateException("both", new ArgumentException("one"), new FormatException("two"));
        }
        catch (AggregateException inner)
        {
            throw new InvalidOperationException("outer", inner);
        }
    }

    /// <summary>
    /// A finding that is not one a digest holds - a severity, an outcome or a line no tool means - is said among the
    /// unparsed lines with why, never held as typed and never dropped unsaid.
    /// </summary>
    [Fact]
    public void AFindingNoDigestHolds_IsSaidWithWhy_NeverHeldNorDropped()
    {
        var digest = ResultDigest.Of(
            [new Diagnostic("mytool", "ERROR", null, "it broke"), new Diagnostic("gcc", "error", null, "at a timestamp", "12", 0)],
            [new TestFailure("ctest", "lost", Outcome: "vanished")],
            []);

        Assert.False(digest.HoldsAFinding);
        Assert.Equal(
            [
                "mytool: it broke - not held, for 'ERROR' is not a severity a diagnostic has",
                "gcc: at a timestamp - not held, for a diagnostic names line 0",
                "ctest lost: not held, for 'vanished' is not an outcome a failure has",
            ],
            digest.Unparsed);
    }

    /// <summary>
    /// Every place is read against the tree once - a diagnostic's file and its project's, each frame's, a failure's: a file
    /// the tree holds is in it, named relative to its root - a rooted one
    /// in either slash and any case of the root's own form, a relative one read from where the tool ran, a file URI -
    /// while a bare name the tree has no file of, a frame naming no file, a runtime's own module and a path elsewhere are
    /// in no tree and stay as written; a failure's place is then the top frame the tree holds, and the counts by file are
    /// the tree's. Read by the paths' own text and the file system's answer, so it holds the same on every machine.
    /// </summary>
    [Fact]
    public void EveryPlace_IsReadAgainstTheTreeOnce_InTheTreeWhereTheTreeHoldsItsFile()
    {
        const string Root = @"D:\work\tree";
        var files = new Holding(Path.Combine(Root, "src/a.cs"), Path.Combine(Root, "src/Engine.cs"), Path.Combine(Root, "include/h.h"), Path.Combine(Root, "src/App.csproj"));
        var stack = new List<StackFrame>
        {
            new("at System.Collections.Generic.Dictionary.Insert()"),
            new("at Thread.run(Thread.java:829)", "Thread.java", 829),
            new("at node:internal/process/task_queues:95", "node:internal/process/task_queues", 95),
            new("at App.Engine.Run() in d:/WORK/Tree/src/Engine.cs:line 42", "d:/WORK/Tree/src/Engine.cs", 42),
            new("at App.Tests.Run() in D:\\elsewhere\\Tests.cs:line 7", @"D:\elsewhere\Tests.cs", 7),
        };
        var digest = ResultDigest.Of(
            [
                new Diagnostic("gcc", "error", null, "jammed", "../../include/h.h", 3, Project: @"D:\work\tree\src\App.csproj", Stack: [new StackFrame("In file included from file:///D:/work/tree/src/a.cs:2", "file:///D:/work/tree/src/a.cs", 2, Kind: "include")]),
            ],
            [new TestFailure("dotnet", "Engine", Exception: new ExceptionRecord("System.Exception", "boom", stack, []), File: "D:/elsewhere/Tests.cs", Line: 1)],
            []);

        var marked = digest.WithInTree(new DigestTree(Root, @"D:\work\tree\build\x64", files));

        var diagnostic = marked.Diagnostics[0];

        Assert.Equal(("include/h.h", "src/App.csproj", "src/a.cs", true), (diagnostic.File, diagnostic.Project, diagnostic.Stack[0].File, diagnostic.Stack[0].InTree));
        Assert.Equal(
            [
                ((string?)null, false),
                ("Thread.java", false),
                ("node:internal/process/task_queues", false),
                ("src/Engine.cs", true),
                (@"D:\elsewhere\Tests.cs", false),
            ],
            marked.Failures[0].Exception!.Stack.Select(frame => (frame.File, frame.InTree)));
        Assert.Equal(("src/Engine.cs", (int?)42), (marked.Failures[0].File, marked.Failures[0].Line));
        Assert.Equal([new Counted("include/h.h", 1)], marked.ByFile);
    }

    /// <summary>A failure whose stack the tree holds no frame of keeps the place its runner named apart, read against the tree as every place is.</summary>
    [Fact]
    public void AFailureWithNoFrameInTheTree_KeepsThePlaceItsRunnerNamed()
    {
        const string Root = "/home/u/tree";
        var marked = ResultDigest.Of(
            [],
            [new TestFailure("dart", "charge test", File: "file:///home/u/tree/test/charge_test.dart", Line: 7, Stack: [new StackFrame("#0 expect (package:test_api/expect.dart:3)", "package:test_api/expect.dart", 3)])],
            [])
            .WithInTree(new DigestTree(Root, null, new Holding(Path.Combine(Root, "test/charge_test.dart"))));

        Assert.Equal(("test/charge_test.dart", (int?)7), (marked.Failures[0].File, marked.Failures[0].Line));
    }

    /// <summary>
    /// Masking a digest masks every text it holds - each message, expected and actual, each test's id, each frame's line,
    /// each exception's message down its causes, each unparsed line - by the one rule, whichever reader made it.
    /// </summary>
    [Fact]
    public void MaskingADigest_MasksEveryTextItHolds()
    {
        const string Secret = "sk-secret-one";
        var frame = new StackFrame($"at Login({Secret})");
        var digest = ResultDigest.Of(
            [new Diagnostic("csc", "error", "CS1", $"key {Secret} leaked", Stack: [frame])],
            [
                new TestFailure("trx", $"Login({Secret})", Message: $"saw {Secret}", Expected: Secret, Actual: Secret, Stack: [frame]),
                new TestFailure("dotnet", "Thrown", Exception: new ExceptionRecord("E", $"outer {Secret}", [frame], [new ExceptionRecord("I", $"inner {Secret}", [frame], [])])),
            ],
            [$"line {Secret}"]);

        var masked = digest.MaskedBy(new Masking(Secret)).Document().ToJsonString();

        Assert.DoesNotContain(Secret, masked, StringComparison.Ordinal);
        Assert.Equal(13, masked.Split("***").Length - 1);
    }

    /// <summary>
    /// The raw lines beside a digest: where it holds an error or a failure, the lines no parser held; where it holds
    /// warnings alone, the phase's tail whole - the line saying why the phase failed is in no warning.
    /// </summary>
    [Fact]
    public void TheTailBesideADigest_IsTheUnparsedLinesOnlyWhereItHoldsAProblem()
    {
        IReadOnlyList<string> tail = ["warning: unused x", "Killed"];
        var warnings = ResultDigest.Of([new Diagnostic("gcc", "warning", null, "unused x")], [], ["warning: odd"]);
        var errors = ResultDigest.Of([new Diagnostic("gcc", "error", null, "x undeclared")], [], ["error: odd"]);
        var failed = ResultDigest.Of([], [new TestFailure("ctest", "t")], ["error: odd"]);

        Assert.Same(tail, warnings.TailBeside(tail));
        Assert.Equal(["error: odd"], errors.TailBeside(tail));
        Assert.Equal(["error: odd"], failed.TailBeside(tail));
    }

    /// <summary>What results files that held no finding said is put ahead of the log's own unparsed lines, counted with them; a digest holding a finding is never set aside.</summary>
    [Fact]
    public void WhatTheFilesSaid_IsSaidBesideTheLogsDigest()
    {
        var log = ResultDigest.Of([], [new TestFailure("ctest", "demo_test")], ["error: odd"]);
        var said = ResultDigest.Of([], [], ["results: 'junit.xml' named no file"]);

        var beside = log.Beside(said);

        Assert.Equal(["results: 'junit.xml' named no file", "error: odd"], beside.Unparsed);
        Assert.Equal(2, beside.UnparsedCount);
        Assert.Equal(["demo_test"], beside.Failures.Select(failure => failure.Id));
        Assert.Throws<ArgumentException>(() => said.Beside(log));
    }

    /// <summary>
    /// What a model is handed says each thing once and nothing that says nothing: the first error whole with its message
    /// said once, in its saying; the next three as their lines; the rest - the printings the four do not say, a warning's
    /// among them - counted by code and by file; and no count of none, no empty list and no null at any depth. The record
    /// keeps everything, its counts by code and by file over the whole output.
    /// </summary>
    [Fact]
    public void WhatAModelIsHanded_SaysEachThingOnce_TheRestCounted_AndNothingThatSaysNothing()
    {
        var digest = ResultDigest.Of(
            [
                new Diagnostic("msbuild", "error", "CS1", "one", "a.cs", 1),
                new Diagnostic("msbuild", "error", "CS1", "two", "a.cs", 2),
                new Diagnostic("msbuild", "error", "CS2", "three", "b.cs", 3),
                new Diagnostic("msbuild", "warning", "CS9", "unused", "c.cs", 1),
                new Diagnostic("msbuild", "error", "CS2", "four", "b.cs", 4),
                new Diagnostic("msbuild", "error", "CS3", "five", "b.cs", 5),
            ],
            [],
            []);

        var model = digest.ForAModel();

        Assert.Equal(["errors", "warnings", "first", "diagnostics", "byCode", "byFile"], model.Select(member => member.Key));
        Assert.Equal(["kind", "tool", "code", "severity", "file", "line", "count", "said"], model["first"]!.AsObject().Select(member => member.Key));
        Assert.Equal("msbuild CS1 a.cs(1): one", model["first"]!["said"]!.GetValue<string>());
        Assert.Equal(
            ["msbuild CS1 a.cs(2): two", "msbuild CS2 b.cs(3): three", "msbuild CS2 b.cs(4): four"],
            model["diagnostics"]!.AsArray().Select(item => item!["said"]!.GetValue<string>()));
        Assert.Equal([("CS9", 1), ("CS3", 1)], Counts(model["byCode"]!));
        Assert.Equal([("b.cs", 1), ("c.cs", 1)], Counts(model["byFile"]!));
        Assert.Equal([("CS1", 2), ("CS2", 2), ("CS9", 1), ("CS3", 1)], Counts(digest.Document()["byCode"]!));
        Assert.Equal(0, digest.Document()["notes"]!.GetValue<int>());

        static IEnumerable<(string, int)> Counts(System.Text.Json.Nodes.JsonNode counts)
            => counts.AsArray().Select(entry => (entry!["of"]!.GetValue<string>(), entry["count"]!.GetValue<int>()));
    }

    /// <summary>The real file system, but holding exactly the files named.</summary>
    private sealed class Holding(params string[] files) : PassThroughFileSystem(new PhysicalFileSystem(FilePermissionsFactory.Create()))
    {
        public override bool FileExists(string path) => files.Contains(path, StringComparer.Ordinal);
    }
}
