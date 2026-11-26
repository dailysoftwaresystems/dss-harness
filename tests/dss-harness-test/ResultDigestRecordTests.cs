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
    /// The failures are held to their bound with every one counted by its outcome - the first met held - and the unparsed
    /// lines to theirs with every one counted - the last met held, the nearest the failure, as a tail is: what the bound
    /// left out is said, never silent, to a model as much as in the record.
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
        Assert.Equal(("error line 21", "error line 70"), (digest.Unparsed[0], digest.Unparsed[^1]));
        Assert.Equal(20, ResultDigest.Handed(digest, [], PhaseParsers.AsRead, logHere: true).Digest!["unparsedLeftOut"]!.GetValue<int>());
        Assert.Equal(20, digest.Document()["leftOut"]!["unparsed"]!.GetValue<int>());
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

        var model = ResultDigest.Handed(digest, [], PhaseParsers.AsRead, logHere: true).Digest!;

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

        var rendered = ResultDigest.Handed(digest, [], PhaseParsers.AsRead, logHere: true).Digest!["first"]!["stack"]!.AsArray().Select(line => line!.GetValue<string>()).ToList();

        Assert.Equal(
            ["at Frame0()", "... 46 frames outside the tree", "at Frame47()", "... 36 frames left out of the record; the log holds them", "at Frame84()", "... 14 frames outside the tree", "at Frame99()"],
            rendered);
    }

    /// <summary>
    /// A cause chain is followed eight deep below its outermost record, the records below counted on the eighth cause,
    /// never left out unsaid; the document keeps the chain whole down to its cut, each cause beneath the one it caused; a
    /// model reads each cause under its error.
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

        for (var depth = 1; depth <= ExceptionRecord.CauseBound; depth++)
        {
            eighth = Assert.Single(eighth.CausedBy);
        }

        Assert.Equal(("Cause9", 3), (eighth.Type, eighth.CausesCut));
        Assert.Empty(eighth.CausedBy);
        Assert.Null(held.Problem());

        var document = digest.Document()["failures"]![0]!["exception"]!;

        Assert.Equal("Cause2", document["causedBy"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("Cause3", document["causedBy"]![0]!["causedBy"]![0]!["type"]!.GetValue<string>());

        var rendered = ResultDigest.Handed(digest, [], PhaseParsers.AsRead, logHere: true).Digest!["first"]!["stack"]!.AsArray().Select(line => line!.GetValue<string>()).ToList();

        Assert.Equal(
            ["at C1()", "caused by Cause2: cause 2", "at C2()", "caused by Cause3: cause 3", "at C3()", "caused by Cause4: cause 4", "at C4()",
                "caused by Cause5: cause 5", "at C5()", "caused by Cause6: cause 6", "at C6()", "caused by Cause7: cause 7", "at C7()",
                "caused by Cause8: cause 8", "at C8()", "caused by Cause9: cause 9", "at C9()", "... 3 causes deeper, left out of the record"],
            rendered);
    }

    /// <summary>
    /// A record whose chain is deeper than its bound says so - eight causes below the outermost record are within it, a
    /// ninth is not: a cause depth is checked, never a count of siblings.
    /// </summary>
    [Fact]
    public void ARecordPastItsCauseDepth_SaysItsProblem_ManySiblingsDoNot()
    {
        var siblings = new ExceptionRecord("System.AggregateException", "many", [], [.. Enumerable.Range(0, 20).Select(at => new ExceptionRecord("Inner", $"inner {at}", [], []))]);
        var deep = new ExceptionRecord("Cause10", "ten", [], []);

        for (var depth = 9; depth >= 1; depth--)
        {
            deep = new ExceptionRecord($"Cause{depth}", $"cause {depth}", [], [deep]);
        }

        Assert.Null(siblings.Problem());
        Assert.Equal("its cause chain is over 8 deep", deep.Problem());
        Assert.Null(deep.CausedBy[0].Problem());
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
    /// What is gathered through a mask - a results file's text, which no log's mask saw - is masked as it is gathered:
    /// each message, expected and actual, each test's id, each frame's line, each exception's message down its causes,
    /// each unparsed line - by the one rule, whichever reader made it - and gathered into the gathering the view is of.
    /// </summary>
    [Fact]
    public void WhatIsGatheredThroughAMask_IsMaskedAsItIsGathered_EveryTextItHolds()
    {
        const string Secret = "sk-secret-one";
        var frame = new StackFrame($"at Login({Secret})");
        var gathering = new ResultDigest.Gathering();
        var masking = gathering.Through(new Masking(Secret));

        masking.Add(new Diagnostic("csc", "error", "CS1", $"key {Secret} leaked", Stack: [frame]));
        masking.Add(new TestFailure("trx", $"Login({Secret})", Message: $"saw {Secret}", Expected: Secret, Actual: Secret, Stack: [frame]));
        masking.Add(new TestFailure("dotnet", "Thrown", Exception: new ExceptionRecord("E", $"outer {Secret}", [frame], [new ExceptionRecord("I", $"inner {Secret}", [frame], [])])));
        masking.Unparsed($"line {Secret}");

        var masked = gathering.Digest().Document().ToJsonString();

        Assert.Same(gathering, gathering.Through(null));
        Assert.DoesNotContain(Secret, masked, StringComparison.Ordinal);
        Assert.Equal(13, masked.Split("***").Length - 1);
        Assert.Equal(masked, masking.Digest().Document().ToJsonString());
    }

    /// <summary>
    /// What a red entry hands a model says every line once: where the digest holds an error or a failure and the log is
    /// one read away, it says the lines no parser held and no raw line goes beside it; where it holds warnings alone, the
    /// phase's tail goes whole - the line saying why the phase failed is in no warning - and the digest says those lines no
    /// parser held the tail does not, each tail line read as the digest's were answering for one; where there is no
    /// digest, the tail alone.
    /// </summary>
    [Fact]
    public void WhatARedEntryHandsAModel_SaysEveryLineOnce()
    {
        IReadOnlyList<string> tail = ["warning: unused x", "\u001b[31merror: odd\u001b[0m", "Killed"];
        var warnings = ResultDigest.Of([new Diagnostic("gcc", "warning", null, "unused x")], [], ["error: early", "error: odd"]);
        var errors = ResultDigest.Of([new Diagnostic("gcc", "error", null, "x undeclared")], [], ["error: odd"]);
        var failed = ResultDigest.Of([], [new TestFailure("ctest", "t")], ["error: odd"]);

        var beside = ResultDigest.Handed(warnings, tail, PhaseParsers.AsRead, logHere: true);

        Assert.Same(tail, beside.Lines);
        Assert.Equal(["error: early"], beside.Digest!["unparsed"]!.AsArray().Select(line => line!.GetValue<string>()));

        foreach (var holding in new[] { errors, failed })
        {
            var handed = ResultDigest.Handed(holding, tail, PhaseParsers.AsRead, logHere: true);

            Assert.Empty(handed.Lines);
            Assert.Equal(["error: odd"], handed.Digest!["unparsed"]!.AsArray().Select(line => line!.GetValue<string>()));
        }

        var none = ResultDigest.Handed(null, tail, PhaseParsers.AsRead, logHere: true);

        Assert.Null(none.Digest);
        Assert.Same(tail, none.Lines);
    }

    /// <summary>
    /// Where no log of the phase is on this machine to read - a host's that could not be brought back - nothing the phase
    /// printed is out of a model's reach: a digest holding an error or a failure goes with the phase's tail whole beside
    /// it, as one holding warnings alone does, and says only those lines no parser held the tail does not - every line
    /// once still. Handed the digest alone, the line that said why the test failed, which no parser typed, was in nothing
    /// a model could read.
    /// </summary>
    [Fact]
    public void WhereNoLogIsHere_ARedEntryHandsTheTailBesideADigestHoldingAProblem()
    {
        IReadOnlyList<string> tail = ["Start 3: folds_duplicates", "check.c:9: expected 2 records, found 3", "\u001b[31merror: odd\u001b[0m"];
        var failed = ResultDigest.Of([], [new TestFailure("ctest", "folds_duplicates")], ["error: early", "error: odd"]);

        var handed = ResultDigest.Handed(failed, tail, PhaseParsers.AsRead, logHere: false);

        Assert.Same(tail, handed.Lines);
        Assert.Equal(["error: early"], handed.Digest!["unparsed"]!.AsArray().Select(line => line!.GetValue<string>()));
        Assert.Equal("ctest folds_duplicates", handed.Digest["first"]!["said"]!.GetValue<string>());
    }

    /// <summary>
    /// A phase's log is gathered beside what its results files gave, each finding once: a diagnostic the files hold - the
    /// same code at the same place, however each form wrote the path - is not said again, one they lack is; a failure of a
    /// test the files name is no second failure and gives the file's what it lacks, its range in the log among it; a
    /// failure of a test no file names that crashed - the test host's - is said, and one that merely failed is the files'
    /// test in the console's own words, said once; where the files name no failure, the log's every failure is said; and
    /// the log's unparsed lines are gathered after what the files said.
    /// </summary>
    [Fact]
    public void ALogsFindings_AreGatheredBesideTheFiles_EachOnce_WhatTheFilesLackAdded()
    {
        var gathering = new ResultDigest.Gathering();

        gathering.Add(new Diagnostic("Microsoft Visual C# Compiler", "error", "CS0103", "The name 'x' does not exist in the current context.", "d:/src/Program.cs", 12, 35));
        gathering.Add(new TestFailure("junit", "parses_input", Message: "SEGFAULT", Outcome: "crashed"));
        gathering.Add(new TestFailure("trx", "Demo.Tests.One", Message: "Assert.Equal() Failure", Log: null));
        gathering.Unparsed("results: 'b.xml' named no file");
        gathering.Unparsed("error: lost");

        gathering.Beside(
            new PhaseFindings(
                [
                    new Diagnostic("msbuild", "error", "CS0103", "The name 'x' does not exist in the current context", @"d:\src\Program.cs", 12, 35, Log: (3, 3)),
                    new Diagnostic("msbuild", "warning", "CS0219", "The variable 'y' is assigned but its value is never used", @"d:\src\Program.cs", 20, 13, Log: (4, 4)),
                ],
                [
                    new TestFailure("ctest", "parses_input", Message: "Exception: SegFault", File: "src/parse.c", Line: 9, Outcome: "crashed", Log: (7, 7)),
                    new TestFailure("dotnet", "test run", Message: "Test host process crashed : Stack overflow", Outcome: "crashed", Log: (9, 9)),
                    new TestFailure("dotnet", "Demo Tests One", Message: "Assert.Equal() Failure", Log: (11, 11)),
                ]),
            file => file.Replace('\\', '/'));

        var digest = gathering.Digest();

        Assert.Equal([("error", "Microsoft Visual C# Compiler"), ("warning", "msbuild")], digest.Diagnostics.Select(diagnostic => (diagnostic.Severity, diagnostic.Tool)));
        Assert.Equal((1, 1), (digest.Errors, digest.Warnings));
        Assert.Equal(
            [("parses_input", "SEGFAULT", "src/parse.c", (int?)9, ((int, int)?)(7, 7)), ("Demo.Tests.One", "Assert.Equal() Failure", null, null, null), ("test run", "Test host process crashed : Stack overflow", null, null, (9, 9))],
            digest.Failures.Select(failure => (failure.Id, failure.Message!, failure.File, failure.Line, failure.Log)));
        Assert.Equal((1, 2), (digest.Failed, digest.Crashed));
        Assert.Equal(["results: 'b.xml' named no file", "error: lost"], digest.Unparsed);
        Assert.Throws<InvalidOperationException>(() => gathering.Through(new Masking("x")).Beside(new PhaseFindings([], []), file => file));

        var unfiled = new ResultDigest.Gathering();

        unfiled.Unparsed("results: 'junit.xml' named no file");
        unfiled.Beside(new PhaseFindings([], [new TestFailure("ctest", "folds_duplicates", Log: (5, 5))]), file => file);

        Assert.Equal(["folds_duplicates"], unfiled.Digest().Failures.Select(failure => failure.Id));
    }

    /// <summary>
    /// A test a results file's case was said through - the first failure its own output printed, whose message and place
    /// the case took - is one the files named: the log's printing of that failure is no second failure, and gives the
    /// case its range in the log, so the lines the console printed of it are one read away. Two cases said through
    /// failures that print alike are each given the printing the log holds under their own test's output, whatever order
    /// the log printed them in; where the log says no test's output either lies in, neither is given a range, for nothing
    /// says whose each is - and neither is a second failure.
    /// </summary>
    [Fact]
    public void ATestACaseWasSaidThrough_IsOneTheFilesNamed_GivenItsPrintingsRangeInTheLog()
    {
        var printed = new TestFailure("gtest", "StoreTest.Loads", Message: "Expected equality", File: "test/store_test.cc", Line: 12);
        var log = new PhaseFindings([], [printed with { Log = (40, 46) }, printed with { Log = (60, 66) }]);

        IReadOnlyList<(string, (int, int)?)> Gathered(PhaseFindings beside)
        {
            var gathering = new ResultDigest.Gathering();

            gathering.Add(new TestFailure("junit", "unit_tests", Message: "Expected equality", File: "test/store_test.cc", Line: 12), printed);
            gathering.Add(new TestFailure("junit", "more_unit_tests", Message: "Expected equality", File: "test/store_test.cc", Line: 12), printed);
            gathering.Beside(beside, file => file);

            return [.. gathering.Digest().Failures.Select(failure => (failure.Id, failure.Log))];
        }

        // The log printed the second case's test first.
        Assert.Equal(
            [("unit_tests", (60, 66)), ("more_unit_tests", ((int, int)?)(40, 46))],
            Gathered(log with { Tests = [new PrintedTest("more_unit_tests", 39, 50), new PrintedTest("unit_tests", 59, 70)] }));
        Assert.Equal([("unit_tests", null), ("more_unit_tests", ((int, int)?)null)], Gathered(log));
    }

    /// <summary>
    /// A printing the log holds under the output of a test whose own output printed none of the failures a case read is no
    /// printing of theirs, however alike it prints: the case read from another test's output is given nothing of it, and
    /// it is matched as any other failure of the log is - to the file's failure of its own id, where a file names one.
    /// </summary>
    [Fact]
    public void APrintingUnderAnotherTestsOutput_IsNoPrintingOfTheFailuresACaseRead()
    {
        var gathering = new ResultDigest.Gathering();
        var printed = new TestFailure("gtest", "StoreTest.Loads", Message: "Expected equality", File: "test/store_test.cc", Line: 12);

        gathering.Add(new TestFailure("junit", "store_sqlite", Message: "Expected equality", File: "test/store_test.cc", Line: 12), printed);
        gathering.Add(new TestFailure("junit", "StoreTest.Loads", Message: "Expected equality"));
        gathering.Beside(new PhaseFindings([], [printed with { Log = (8, 11) }]) { Tests = [new PrintedTest("store_pg", 5, 24)] }, file => file);

        Assert.Equal(
            [("store_sqlite", null), ("StoreTest.Loads", ((int, int)?)(8, 11))],
            gathering.Digest().Failures.Select(failure => (failure.Id, failure.Log)));
    }

    /// <summary>
    /// A case said through none of its test's own output reads what the log printed under that test's output as that
    /// output: where it printed no failure, the first error there gives the case its place, never its message or a range;
    /// a failure there it is not said through is matched to a file's failure of its own id where a file names one plainly,
    /// and is never a second failure of that test; and where two such cases name one test, nothing says which of them the
    /// output is - neither is said through it, and every failure printed there is its own.
    /// </summary>
    [Fact]
    public void ACaseSaidThroughNone_ReadsWhatTheLogPrintedUnderItsTest_AsItsOutput()
    {
        var placed = new ResultDigest.Gathering();

        placed.Add(new TestFailure("junit", "builds_fixture", Message: "Failed"), null, saysOnlyHowItEnded: true);
        placed.Beside(
            new PhaseFindings([new Diagnostic("gcc", "error", null, "expected ';' before '}' token", "src/fixture.c", 3, 5, Log: (6, 6))], []) { Tests = [new PrintedTest("builds_fixture", 5, 9)] },
            file => file);

        var digest = placed.Digest();
        var one = Assert.Single(digest.Failures);
        var error = Assert.Single(digest.Diagnostics);

        Assert.Equal(("builds_fixture", "Failed", "src/fixture.c", (int?)3, ((int, int)?)null), (one.Id, one.Message, one.File, one.Line, one.Log));
        Assert.Equal(("src/fixture.c", (int?)3, ((int, int)?)(6, 6)), (error.File, error.Line, error.Log));

        var loads = new TestFailure("gtest", "StoreTest.Loads", Message: "connection refused", File: "test/store_test.cc", Line: 12, Log: (8, 11));
        var saves = new TestFailure("gtest", "StoreTest.Saves", Message: "connection reset", File: "test/store_test.cc", Line: 20, Log: (12, 15));
        var named = new ResultDigest.Gathering();

        named.Add(new TestFailure("junit", "store_pg", Message: "Failed"), null, saysOnlyHowItEnded: true);
        named.Add(new TestFailure("gtest-xml", "StoreTest.Saves", Message: "connection reset"));
        named.Beside(new PhaseFindings([], [loads, saves]) { Tests = [new PrintedTest("store_pg", 5, 24)] }, file => file);

        Assert.Equal(
            [("store_pg", "connection refused", ((int, int)?)(8, 11)), ("StoreTest.Saves", "connection reset", (12, 15))],
            named.Digest().Failures.Select(failure => (failure.Id, failure.Message!, failure.Log)));

        var twice = new ResultDigest.Gathering();

        twice.Add(new TestFailure("junit", "store_pg", Message: "Failed"), null, saysOnlyHowItEnded: true);
        twice.Add(new TestFailure("junit", "store_pg", Message: "Failed"), null, saysOnlyHowItEnded: true);
        twice.Beside(new PhaseFindings([], [loads]) { Tests = [new PrintedTest("store_pg", 5, 24)] }, file => file);

        Assert.Equal(
            [("store_pg", "Failed", null), ("store_pg", "Failed", null), ("StoreTest.Loads", "connection refused", ((int, int)?)(8, 11))],
            twice.Digest().Failures.Select(failure => (failure.Id, failure.Message!, failure.Log)));
    }

    /// <summary>
    /// The failure a results file's case is said through, of the failures its test printed: where the case says its
    /// program died in the test - it crashed or timed out - the last crash printed, the test its program died in; else, and
    /// where nothing printed crashed, the first; none where nothing was printed.
    /// </summary>
    [Fact]
    public void TheFailureACaseIsSaidThrough_IsTheCrashItsProgramDiedOf_WhereItCrashedOrTimedOut_ElseTheFirst()
    {
        var first = new TestFailure("gtest", "StoreTest.Loads", Outcome: "crashed");
        var failed = new TestFailure("gtest", "StoreTest.Saves");
        var last = new TestFailure("gtest", "StoreTest.Frees", Outcome: "crashed");
        IReadOnlyList<TestFailure> printed = [first, failed, last];

        Assert.Same(last, ResultDigest.Gathering.SaidThroughOf(printed, "crashed"));
        Assert.Same(last, ResultDigest.Gathering.SaidThroughOf(printed, "timedOut"));
        Assert.Same(first, ResultDigest.Gathering.SaidThroughOf(printed, "failed"));
        Assert.Same(failed, ResultDigest.Gathering.SaidThroughOf([failed], "crashed"));
        Assert.Null(ResultDigest.Gathering.SaidThroughOf([], "crashed"));
    }

    /// <summary>
    /// A file's failure the log printed again is given what the log's printing of it holds and the file's lacks - what was
    /// expected and what there was among it, as its place and its range - and keeps what the file said of it.
    /// </summary>
    [Fact]
    public void AFilesFailure_IsGivenWhatItExpectedAndHad_FromTheLogsPrintingOfIt()
    {
        var gathering = new ResultDigest.Gathering();

        gathering.Add(new TestFailure("trx", "Demo.Tests.One", Message: "Assert.Equal() Failure: Values differ"));
        gathering.Beside(
            new PhaseFindings([], [new TestFailure("dotnet", "Demo.Tests.One", Message: "Values differ", Expected: "3", Actual: "4", File: "tests/Demo.cs", Line: 12, Log: (5, 9))]),
            file => file);

        var one = Assert.Single(gathering.Digest().Failures);

        Assert.Equal(
            ("trx", "Assert.Equal() Failure: Values differ", "3", "4", "tests/Demo.cs", (int?)12, ((int, int)?)(5, 9)),
            (one.Framework, one.Message, one.Expected, one.Actual, one.File, one.Line, one.Log));
    }

    /// <summary>
    /// A thrown error a results file's case was said through names no test: the log's printing of that same error is the
    /// case's, given to it and never a second failure, but a process that died of an error with the same type and message -
    /// the JVM's own "Exception in thread" - is a crash no results file is written for, and is said beside the case.
    /// </summary>
    [Fact]
    public void AThrownErrorACaseWasSaidThrough_NamesNoTest_AndAProcessThatDiedOfItIsSaid()
    {
        var gathering = new ResultDigest.Gathering();
        var thrown = new ExceptionRecord("java.lang.IllegalStateException", "no record", [new StackFrame("at app.Store.load(Store.java:14)", "Store.java", 14)], []);
        var printed = new TestFailure("jvm", "java.lang.IllegalStateException: no record", Message: "no record", Exception: thrown, Outcome: "errored");

        gathering.Add(new TestFailure("junit", "app.StoreTest.loads", Name: "loads", Class: "app.StoreTest", Message: "no record", Exception: thrown), printed);
        gathering.Beside(
            new PhaseFindings(
                [],
                [
                    printed with { Log = (12, 14) },
                    printed with { Outcome = "crashed", Log = (30, 32) },
                ]),
            file => file);

        var digest = gathering.Digest();

        Assert.Equal(
            [("app.StoreTest.loads", "failed", ((int, int)?)(12, 14)), ("java.lang.IllegalStateException: no record", "crashed", (30, 32))],
            digest.Failures.Select(failure => (failure.Id, failure.Outcome, failure.Log)));
        Assert.Equal((1, 1), (digest.Failed, digest.Crashed));
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

        var model = ResultDigest.Handed(digest, [], PhaseParsers.AsRead, logHere: true).Digest!;

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
