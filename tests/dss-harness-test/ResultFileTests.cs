using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The structured results a tool writes as data, read by their own shape as a stream: a TRX, a JUnit XML, Dart's JSON
/// stream and a SARIF log each give the typed failures and diagnostics they hold, their places as the tool wrote them; a
/// skipped test is no failure; a file no form owns is said with a reason, never guessed at; and a file a crash cut short,
/// or of a shape the form does not hold, gives what it held before the break and says the break - never throws.
/// </summary>
public sealed class ResultFileTests
{
    /// <summary>A TRX as <c>dotnet test --logger trx</c> writes it: a failed result with its message and its stack, a passed one, a skipped one.</summary>
    private const string Trx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="2ce50b0c-cc33-4785-92a6-9e0f4f5a3a06" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" name="name@machine 2026-11-23 10:00:00">
          <Results>
            <UnitTestResult executionId="11111111-1111-1111-1111-111111111111" testId="22222222-2222-2222-2222-222222222222" testName="DssHarness.Tests.Demo.One" computerName="machine" duration="00:00:01.2500000" startTime="2026-11-23T10:00:01.0000000+00:00" endTime="2026-11-23T10:00:02.2500000+00:00" testType="13cdc9d9-ddb5-4fa4-a97d-d965ccfc5a4b" outcome="Failed" testListId="8c84fa94-04c1-424b-9868-57aaf7b0e5e2" relativeResultsDirectory="11111111-1111-1111-1111-111111111111">
              <Output>
                <ErrorInfo>
                  <Message>Assert.Contains() Failure: Sub-string not found&#xD;Collection: ["one"]</Message>
                  <StackTrace>  at DssHarness.Tests.Demo.One() in D:/src/tests/Demo.cs:line 12&#xD;  at DssHarness.Tests.Demo.Runner() in D:/src/tests/Runner.cs:line 30</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult executionId="33333333-3333-3333-3333-333333333333" testId="44444444-4444-4444-4444-444444444444" testName="DssHarness.Tests.Demo.Two" duration="00:00:00.5000000" outcome="Passed" />
            <UnitTestResult executionId="55555555-5555-5555-5555-555555555555" testId="66666666-6666-6666-6666-666666666666" testName="DssHarness.Tests.Demo.Three" duration="00:00:00.1000000" outcome="NotExecuted" />
          </Results>
        </TestRun>
        """;

    /// <summary>A JUnit XML in the form the family shares: a failed case with what its assertion said, a passing one.</summary>
    private const string JUnit = """
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuites tests="2" failures="1">
          <testsuite name="demo" tests="2" failures="1" errors="0" time="0.01" timestamp="2026-11-23T10:00:00">
            <testcase classname="demo" name="one" time="0.006"><failure message="the charge was wrong" type="assert"></failure></testcase>
            <testcase classname="demo" name="two" time="0.004"></testcase>
          </testsuite>
        </testsuites>
        """;

    /// <summary>Dart's JSON stream as <c>--file-reporter json</c> writes it: a failing test with its error, and an unowned error.</summary>
    private const string Dart = """
        {"type":"testStart","test":{"id":1,"name":"charge test","suiteID":0,"metadata":{},"url":"file:///d:/src/test/charge_test.dart","line":7,"column":3},"time":"2026-11-23T10:00:00.000Z"}
        {"type":"error","testID":1,"error":"Expected: 3\n  Actual: 4","stackTrace":"#0 main (d:/src/test/charge_test.dart:8:5)\n#1 _main (d:/src/test/charge_test.dart:3:9)","isFailure":true,"time":"2026-11-23T10:00:00.250Z"}
        {"type":"testDone","testID":1,"result":"failure","hidden":false,"time":"2026-11-23T10:00:00.250Z"}
        {"type":"error","testID":null,"error":"Failed to load \"suite_test.dart\": compilation failed","stackTrace":null,"isFailure":false,"time":"2026-11-23T10:00:01.000Z"}
        {"type":"done","success":false,"time":"2026-11-23T10:00:01.000Z"}
        """;

    /// <summary>A SARIF 2.1 log as csc writes it with <c>-errorlog:x,version=2.1</c>: an error, and a result naming no level.</summary>
    private const string Sarif = """
        {
          "$schema": "https://json.schemastore.org/sarif-2.1.0.json",
          "version": "2.1.0",
          "runs": [
            {
              "tool": { "driver": { "name": "Microsoft Visual C# Compiler", "version": "5.0" } },
              "results": [
                {
                  "ruleId": "CS0103",
                  "level": "error",
                  "message": { "text": "The name 'x' does not exist in the current context." },
                  "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///d:/src/Program.cs" }, "region": { "startLine": 12, "startColumn": 35, "endLine": 12, "endColumn": 36 } } } ]
                },
                {
                  "ruleId": "CS0219",
                  "message": { "text": "The variable 'y' is assigned but its value is never used" },
                  "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src/Program.cs" }, "region": { "startLine": 20, "startColumn": 13 } } } ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>A SARIF 1.0 log, the form csc writes with a bare <c>-errorlog</c>: the message a string, the place a result file.</summary>
    private const string Sarif1 = """
        {
          "$schema": "http://json.schemastore.org/sarif-1.0.0",
          "version": "1.0.0",
          "runs": [
            {
              "tool": { "name": "Microsoft (R) Visual C# Compiler", "version": "4.8.0" },
              "results": [
                {
                  "ruleId": "CS0103",
                  "level": "error",
                  "message": "The name 'x' does not exist in the current context",
                  "locations": [ { "resultFile": { "uri": "file:///d:/src/Program.cs", "region": { "startLine": 12, "startColumn": 35 } } } ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// A TRX is read by its root, a result at a time: a failed one with its message, its stack read by the .NET frame
    /// owner and its duration; a passed one and a skipped one - <c>NotExecuted</c> - are no failures. Its place is none
    /// before the digest is read against a tree, which takes it from the top frame the tree holds.
    /// </summary>
    [Fact]
    public void ATrx_IsReadByItsRoot_ItsFailuresTheirMessagesAndStacks_ASkipNoFailure()
    {
        var digest = Digest(Trx, out var problem);

        Assert.Null(problem);
        Assert.Empty(digest.Diagnostics);

        var one = Assert.Single(digest.Failures);

        Assert.Equal(("trx", "DssHarness.Tests.Demo.One", "One", "DssHarness.Tests.Demo", "failed"), (one.Framework, one.Id, one.Name, one.Class, one.Outcome));
        Assert.Equal(1.25, one.Duration);
        Assert.Equal("Assert.Contains() Failure: Sub-string not found\rCollection: [\"one\"]", one.Message);
        Assert.Equal(
            [("D:/src/tests/Demo.cs", (int?)12, "DssHarness.Tests.Demo.One()"), ("D:/src/tests/Runner.cs", 30, "DssHarness.Tests.Demo.Runner()")],
            one.Stack.Select(frame => (frame.File, frame.Line, frame.Symbol)));
        Assert.Null(one.File);
        Assert.Equal((1, 0, 0, 0), (digest.Failed, digest.Errored, digest.TimedOut, digest.Crashed));
    }

    /// <summary>
    /// A TRX's other failing outcomes are read for what they are - an error errored, a timeout timed out, an abort and a
    /// lost test host crashed - and a data-driven test by each of its rows, never by the row-holding result again.
    /// </summary>
    [Fact]
    public void ATrxsOtherOutcomes_AreReadForWhatTheyAre_ADataDrivenTestByItsRows()
    {
        const string Outcomes = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
              <UnitTestResult testName="N.Errored" outcome="Error" />
              <UnitTestResult testName="N.Slow" outcome="Timeout" />
              <UnitTestResult testName="N.Aborted" outcome="Aborted" />
              <UnitTestResult testName="N.Lost" outcome="Disconnected" />
              <UnitTestResult testName="N.Inconclusive" outcome="Inconclusive" />
              <UnitTestResult testName="N.Rows" outcome="Failed">
                <InnerResults>
                  <UnitTestResult testName="N.Rows (1)" outcome="Passed" />
                  <UnitTestResult testName="N.Rows (2)" outcome="Failed"><Output><ErrorInfo><Message>row two</Message></ErrorInfo></Output></UnitTestResult>
                </InnerResults>
              </UnitTestResult>
            </Results></TestRun>
            """;

        var digest = Digest(Outcomes, out var problem);

        Assert.Null(problem);
        Assert.Equal(
            [("N.Errored", "errored"), ("N.Slow", "timedOut"), ("N.Aborted", "crashed"), ("N.Lost", "crashed"), ("N.Rows (2)", "failed")],
            digest.Failures.Select(failure => (failure.Id, failure.Outcome)));
        Assert.Equal("row two", digest.Failures[^1].Message);
    }

    /// <summary>A JUnit XML is read through the mutation arm's reader, a case at a time: its failed case said, its passing one not.</summary>
    [Fact]
    public void AJUnitReport_IsReadThroughTheMutatorArmsReader_ItsFailuresSaid()
    {
        var digest = Digest(JUnit, out var problem);

        Assert.Null(problem);

        var one = Assert.Single(digest.Failures);

        Assert.Equal(("junit", "demo.one", "one", "demo", "failed", "the charge was wrong", 0.006), (one.Framework, one.Id, one.Name, one.Class, one.Outcome, one.Message, one.Duration));
    }

    /// <summary>
    /// A file's text is cut into lines by the one rule of where lines end, whatever ending its writer used: a failure's
    /// body whose lines a carriage return alone ends says its first line, never the body run together.
    /// </summary>
    [Fact]
    public void AFilesLines_EndWhereTheOneRuleEndsThem_ACarriageReturnAloneAmongThem()
    {
        var digest = Digest(
            "<testsuites><testsuite name=\"demo\"><testcase classname=\"demo\" name=\"one\"><failure>expected 2 records&#xD;found 3&#xD;</failure></testcase></testsuite></testsuites>",
            out var problem);

        Assert.Null(problem);
        Assert.Equal("expected 2 records", Assert.Single(digest.Failures).Message);
    }

    /// <summary>
    /// What a JUnit case's own test printed is read by the phase's parsers: a case that says only how its test ended takes
    /// its message and its place from the first failure its output holds - every other failure there said as its own -
    /// and where its output holds nothing typed, its message is all its test printed; a case with no output keeps the word
    /// its test ended in.
    /// </summary>
    [Fact]
    public void AJUnitCasesOwnOutput_IsReadByThePhasesParsers_ACaseSayingOnlyHowItEndedTakingItsMessage()
    {
        var gtest = string.Join(
            "&#10;",
            "[ RUN      ] StoreTest.LoadsItsRecords",
            "/home/runner/work/app/app/tests/store_test.cc:14: Failure",
            "Expected equality of these values:",
            "  store.size()",
            "    Which is: 3",
            "  2",
            "[  FAILED  ] StoreTest.LoadsItsRecords (0 ms)",
            "[ RUN      ] Sizes/ParserTest.ReadsItsForm/1",
            "/home/runner/work/app/app/tests/parser_test.cc:22: Failure",
            "Value of: parser.Read(GetParam())",
            "  Actual: false",
            "Expected: true",
            "[  FAILED  ] Sizes/ParserTest.ReadsItsForm/1, where GetParam() = 3 (1 ms)",
            string.Empty);

        var digest = Digest(
            $"""
            <testsuite name="(empty)" tests="3" failures="3">
              <testcase name="folds_duplicates" classname="folds_duplicates" status="fail"><failure message="Failed"/><system-out>check.c:9: expected 2 records, found 3&#10;</system-out></testcase>
              <testcase name="store_tests" classname="store_tests" status="fail"><failure message="Failed"/><system-out>{gtest}</system-out></testcase>
              <testcase name="parses_input" classname="parses_input" status="fail"><failure message="SEGFAULT"/><system-out></system-out></testcase>
            </testsuite>
            """,
            out var problem,
            PhaseParsers.ForTests(new Core.Configuration.HarnessConfig(), "ctest", null));

        Assert.Null(problem);
        Assert.Equal(
            [("folds_duplicates", "failed"), ("store_tests", "failed"), ("Sizes/ParserTest.ReadsItsForm/1", "failed"), ("parses_input", "crashed")],
            digest.Failures.Select(failure => (failure.Id, failure.Outcome)));
        Assert.Equal(("check.c:9: expected 2 records, found 3", (string?)null), (digest.Failures[0].Message, digest.Failures[0].File));
        Assert.StartsWith("Expected equality of these values:", digest.Failures[1].Message, StringComparison.Ordinal);
        Assert.Equal(("/home/runner/work/app/app/tests/store_test.cc", (int?)14), (digest.Failures[1].File, digest.Failures[1].Line));
        Assert.Equal(("/home/runner/work/app/app/tests/parser_test.cc", (int?)22, (((int, int)?)null)), (digest.Failures[2].File, digest.Failures[2].Line, digest.Failures[2].Log));
        Assert.Equal("SEGFAULT", digest.Failures[3].Message);
    }

    /// <summary>
    /// A JUnit failure whose body holds a stack has it read by its runtime's own form, as the phase's parsers read it: the
    /// thrown error with its frames is the failure's, its message the one the runner gave it - never a second failure.
    /// </summary>
    [Fact]
    public void AJUnitFailuresBody_HoldingAStack_IsReadAsThatFailuresThrownError()
    {
        var digest = Digest(
            """
            <testsuites><testsuite name="store"><testcase classname="store adds" name="store adds" time="0.002"><failure message="expected 2 to be 3" type="AssertionError">AssertionError: expected 2 to be 3
                at Object.&lt;anonymous&gt; (/home/u/app/test/store.test.js:5:13)
                at processTicksAndRejections (node:internal/process/task_queues:95:5)</failure></testcase></testsuite></testsuites>
            """,
            out var problem,
            PhaseParsers.ForTests(new Core.Configuration.HarnessConfig(), "node", null));

        Assert.Null(problem);

        var one = Assert.Single(digest.Failures);

        Assert.Equal(("store adds", "expected 2 to be 3", "AssertionError"), (one.Id, one.Message, one.Exception?.Type));
        Assert.Equal(2, one.Exception!.Stack.Count);
        Assert.Equal(("/home/u/app/test/store.test.js", (int?)5), (one.Exception.Stack[0].File, one.Exception.Stack[0].Line));
        Assert.Empty(one.Stack);
    }

    /// <summary>
    /// A red CTest run is digested from its JUnit file and its console together, each failure once: a case its console
    /// names too is the file's, given its range in the log; a case saying only how it ended takes its message from what
    /// its test printed; and a test the console names that the file holds nothing of - one that hung after the file was
    /// cut - is said from the console.
    /// </summary>
    [Fact]
    public void ARedCTestRun_IsDigestedFromItsJUnitAndItsConsole_EachFailureOnce()
    {
        using var temp = new TempDirectory();
        var parsers = PhaseParsers.ForTests(new Core.Configuration.HarnessConfig(), "ctest", null);
        var report = System.Text.Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="(empty)" tests="3" failures="2">
              <testcase name="reads_records" classname="reads_records" time="0.0008" status="run"><system-out></system-out></testcase>
              <testcase name="folds_duplicates" classname="folds_duplicates" time="0.0008" status="fail"><failure message="Failed"/><system-out>check.c:9: expected 2 records, found 3&#10;</system-out></testcase>
              <testcase name="parses_input" classname="parses_input" time="0.0133" status="fail"><failure message="SEGFAULT"/><system-out></system-out></testcase>
            </testsuite>
            """);
        var console = string.Join(
            "\n",
            "Test project /home/runner/work/app/app/build",
            "    Start 1: reads_records",
            "1/4 Test #1: reads_records ....................   Passed    0.00 sec",
            "    Start 2: folds_duplicates",
            "2/4 Test #2: folds_duplicates .................***Failed    0.00 sec",
            "check.c:9: expected 2 records, found 3",
            "    Start 3: parses_input",
            "3/4 Test #3: parses_input .....................***Exception: SegFault  0.01 sec",
            "    Start 4: waits_for_lock",
            "4/4 Test #4: waits_for_lock ...................***Timeout   3.00 sec",
            string.Empty,
            "25% tests passed, 3 tests failed out of 4",
            string.Empty,
            "The following tests FAILED:",
            "\t  2 - folds_duplicates (Failed)",
            "\t  3 - parses_input (SEGFAULT)",
            "\t  4 - waits_for_lock (Timeout)",
            "Errors while running CTest",
            string.Empty);

        var digest = PhaseDigest.Of(
            Core.Execution.PhaseOutput.Of(console),
            parsers,
            new DigestTree(temp.Path, null, new HarnessFactory().FileSystem),
            into => ResultFiles.Read(() => new MemoryStream(report, writable: false), into, parsers) is null)!;

        Assert.Equal(
            [("folds_duplicates", "failed", "check.c:9: expected 2 records, found 3", ((int, int)?)(5, 6)), ("parses_input", "crashed", "SEGFAULT", (8, 8)), ("waits_for_lock", "timedOut", "Timeout", (10, 11))],
            digest.Failures.Select(failure => (failure.Id, failure.Outcome, failure.Message!, failure.Log)));
        Assert.Equal((1, 1, 1), (digest.Failed, digest.Crashed, digest.TimedOut));
        Assert.Equal((0, 0), (digest.Errors, digest.UnparsedCount));
    }

    /// <summary>
    /// A case CTest wrote failed as CTest says it ended - a timeout timed out, a signal, a fault or an abort crashed, a
    /// failure failed - read from the words its console says them in; a case another runner wrote failed, whatever its
    /// assertion said. A case whose class is its own name, as CTest writes every one, is named once - a name holding a
    /// dot too - and one of a class of its own is named by its class and its name.
    /// </summary>
    [Fact]
    public void ACaseCTestWrote_EndedAsCTestSaysIt_AndAnotherRunnersFailed()
    {
        var digest = Digest(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="(empty)" tests="6" failures="5">
              <testcase name="folds" classname="folds" time="0.0008" status="fail"><failure message="Failed"/></testcase>
              <testcase name="parses" classname="parses" time="0.0133" status="fail"><failure message="SEGFAULT"/></testcase>
              <testcase name="waits" classname="waits" time="3.0016" status="fail"><failure message="Timeout"/></testcase>
              <testcase name="aborts" classname="aborts" time="0.0010" status="fail"><failure message="Child aborted"/></testcase>
              <testcase name="unit.parser" classname="unit.parser" time="0.0010" status="fail"><failure message="Failed"/></testcase>
              <testcase name="keeps" classname="keeps" time="0.0011" status="run"/>
              <testcase name="Waits" classname="Store" time="0.0020" status="run" result="completed"><failure message="Timeout of 5s was exceeded"/></testcase>
            </testsuite>
            """,
            out var problem);

        Assert.Null(problem);
        Assert.Equal(
            [("folds", "failed"), ("parses", "crashed"), ("waits", "timedOut"), ("aborts", "crashed"), ("unit.parser", "failed"), ("Store.Waits", "failed")],
            digest.Failures.Select(failure => (failure.Id, failure.Outcome)));
        Assert.Equal(
            [("folds", null), ("unit.parser", null), ("Waits", "Store")],
            digest.Failures.Where(failure => failure.Outcome == "failed").Select(failure => (failure.Name, failure.Class)));
        Assert.Equal((3, 1, 2), (digest.Failed, digest.TimedOut, digest.Crashed));
    }

    /// <summary>
    /// Dart's stream is read a line at a time: a failing test with its error event as its message and its stack - a failed
    /// expectation's stack the failure's own, held once - its place the path the test's file URI names, and an error no
    /// test owns as a diagnostic.
    /// </summary>
    [Fact]
    public void ADartStream_IsReadByItsLines_ItsFailuresAndItsUnownedErrors()
    {
        var digest = Digest(Dart, out var problem);

        Assert.Null(problem);

        var one = Assert.Single(digest.Failures);

        Assert.Equal(("dart", "charge test", "failed", "Expected: 3\n  Actual: 4"), (one.Framework, one.Id, one.Outcome, one.Message));
        Assert.Equal(("d:/src/test/charge_test.dart", (int?)7), (one.File, one.Line));
        Assert.Equal([("d:/src/test/charge_test.dart", (int?)8, "main"), ("d:/src/test/charge_test.dart", 3, "_main")], one.Stack.Select(frame => (frame.File, frame.Line, frame.Symbol)));
        Assert.Null(one.Exception);

        var unowned = Assert.Single(digest.Diagnostics);

        Assert.Equal(("dart", "error", "Failed to load \"suite_test.dart\": compilation failed"), (unowned.Tool, unowned.Severity, unowned.Message));
    }

    /// <summary>
    /// A Dart test that met an error after it ended in success failed, as the protocol says: it is read as errored, with
    /// that error its message, in the order it was found failed - after one that failed as it ended.
    /// </summary>
    [Fact]
    public void ADartTestThatMetAnErrorAfterItPassed_Failed()
    {
        const string Late = """
            {"type":"testStart","test":{"id":1,"name":"keeps records","url":"file:///d:/src/test/store_test.dart","line":3},"time":"t"}
            {"type":"testDone","testID":1,"result":"success","hidden":false,"time":"t"}
            {"type":"testStart","test":{"id":2,"name":"charges","url":"file:///d:/src/test/charge_test.dart","line":7},"time":"t"}
            {"type":"testDone","testID":2,"result":"failure","hidden":false,"time":"t"}
            {"type":"error","testID":1,"error":"This test failed after it had already completed.","stackTrace":"","isFailure":false,"time":"t"}
            """;

        var digest = Digest(Late, out var problem);

        Assert.Null(problem);
        Assert.Equal([("charges", "failed", (string?)null), ("keeps records", "errored", "This test failed after it had already completed.")], digest.Failures.Select(failure => (failure.Id, failure.Outcome, failure.Message)));
        Assert.Equal("error", digest.Failures[1].Exception!.Type);
    }

    /// <summary>
    /// A Dart stream a stop cut short gives every failure before its torn line and says the line - counted, the first
    /// named - as it does an event whose test it cannot name; the rest of the stream is read on.
    /// </summary>
    [Fact]
    public void ADartStreamCutShort_GivesWhatItHeld_AndSaysItsTornLines()
    {
        var torn = string.Join("\n", Dart.Split('\n').Take(3)) + "\n{\"type\":\"testStart\",\"test\":{\"id\":\"two\"}}\n{\"type\":\"testDone\",\"testID\":2,\"res";

        var digest = Digest(torn, out var problem);

        Assert.Equal("2 of its lines are no event of the stream, the first line 4", problem);
        Assert.Equal(["charge test"], digest.Failures.Select(failure => failure.Id));
    }

    /// <summary>
    /// A SARIF 2.1 log is read by its runs, each result a diagnostic: the driver's name, the level - warning where it names
    /// none, as SARIF says - the rule, the message, its place - a file URI the path it names - and its range.
    /// </summary>
    [Fact]
    public void ASarifLog_IsReadByItsRuns_ItsDiagnosticsTyped_ALevelLeftOutAWarning()
    {
        var digest = Digest(Sarif, out var problem);

        Assert.Null(problem);
        Assert.Empty(digest.Failures);
        Assert.Equal(2, digest.Diagnostics.Count);

        var one = digest.Diagnostics[0];

        Assert.Equal(("Microsoft Visual C# Compiler", "error", "CS0103", "The name 'x' does not exist in the current context.", "d:/src/Program.cs", (int?)12, (int?)35, (int?)12, (int?)36), (one.Tool, one.Severity, one.Code, one.Message, one.File, one.Line, one.Column, one.EndLine, one.EndColumn));
        Assert.Same(digest.Diagnostics[0], digest.First);

        var unlevelled = digest.Diagnostics[1];

        Assert.Equal(("warning", "src/Program.cs", (int?)20, (int?)null), (unlevelled.Severity, unlevelled.File, unlevelled.Line, unlevelled.EndLine));
        Assert.Equal((1, 1, 0), (digest.Errors, digest.Warnings, digest.Notes));
    }

    /// <summary>A SARIF 1.0 log - csc's own form - is read as the 2.1 one is: its tool's name, its message as a string, its place in a result file.</summary>
    [Fact]
    public void ASarif1Log_IsReadAsThe21OneIs()
    {
        var digest = Digest(Sarif1, out var problem);

        Assert.Null(problem);

        var one = Assert.Single(digest.Diagnostics);

        Assert.Equal(("Microsoft (R) Visual C# Compiler", "error", "CS0103", "The name 'x' does not exist in the current context", "d:/src/Program.cs", (int?)12, (int?)35), (one.Tool, one.Severity, one.Code, one.Message, one.File, one.Line, one.Column));
    }

    /// <summary>
    /// A SARIF member of another shape than the form's is read as absent, never thrown over: a level that is a number is
    /// the default, a line that is text names no line, a message that is a number says none - and a result saying no
    /// message is said among the unparsed lines as one not held, never dropped unsaid.
    /// </summary>
    [Fact]
    public void ASarifMemberOfAnotherShape_IsReadAsAbsent_NeverThrownOver()
    {
        const string Shapeless = """
            {"version":"2.1.0","runs":[{"tool":{"driver":"csc"},"results":[
              {"ruleId":"CS1","level":3,"message":{"text":"one"},"locations":[{"physicalLocation":{"region":{"startLine":"twelve"}}}]},
              {"ruleId":7,"message":42}
            ]}]}
            """;

        var digest = Digest(Shapeless, out var problem);

        Assert.Null(problem);

        var one = Assert.Single(digest.Diagnostics);

        Assert.Equal(("sarif", "warning", "CS1", "one", (string?)null, (int?)null), (one.Tool, one.Severity, one.Code, one.Message, one.File, one.Line));
        Assert.Equal(["sarif: (no message) - not held, for a diagnostic says no message"], digest.Unparsed);
    }

    /// <summary>
    /// A results file a crash cut short gives every finding before the break and says where it broke - an XML form a case
    /// at a time, a SARIF log a result at a time - never throws.
    /// </summary>
    [Fact]
    public void AResultsFileCutShort_GivesWhatItHeld_AndSaysTheBreak()
    {
        var xml = Digest(JUnit[..JUnit.IndexOf("<testcase classname=\"demo\" name=\"two\"", StringComparison.Ordinal)], out var xmlProblem);

        Assert.Equal(["demo.one"], xml.Failures.Select(failure => failure.Id));
        Assert.StartsWith("it is no XML: ", xmlProblem, StringComparison.Ordinal);

        var json = Digest(Sarif[..Sarif.IndexOf("\"ruleId\": \"CS0219\"", StringComparison.Ordinal)], out var jsonProblem);

        Assert.Equal(["CS0103"], json.Diagnostics.Select(diagnostic => diagnostic.Code));
        Assert.StartsWith("it is no whole JSON document: ", jsonProblem, StringComparison.Ordinal);
    }

    /// <summary>
    /// A SARIF log is read from its stream a result at a time, never held whole: a result longer than the reader's block is
    /// taken whole as it is met, and a run whose results come before its tool is read once more for them, so each result
    /// names its own run's tool - a run naming none said as SARIF's - in the order the log holds them.
    /// </summary>
    [Fact]
    public void ASarifLog_IsReadAResultAtATime_EachNamingItsRunsTool_InTheLogsOrder()
    {
        var wide = new string('x', 40_000);
        var log = """
            {"version":"2.1.0","runs":[
              {"tool":{"driver":{"name":"first"}},"results":[{"ruleId":"A1","level":"error","message":{"text":"WIDE"}}]},
              {"results":[{"ruleId":"B1","level":"error","message":{"text":"b one"}},{"ruleId":"B2","message":{"text":"b two"}}],"tool":{"driver":{"name":"second"}}},
              {"tool":{"name":"third"},"results":[{"ruleId":"C1","level":"note","message":"c one"}]},
              {"results":[{"ruleId":"D1","level":"error","message":{"text":"d one"}}]}
            ]}
            """.Replace("WIDE", wide, StringComparison.Ordinal);

        var digest = Digest(log, out var problem);

        Assert.Null(problem);
        Assert.Equal(
            [("first", "A1", "error"), ("second", "B1", "error"), ("second", "B2", "warning"), ("third", "C1", "note"), ("sarif", "D1", "error")],
            digest.Diagnostics.Select(diagnostic => (diagnostic.Tool, diagnostic.Code!, diagnostic.Severity)));
        Assert.Equal(wide, digest.Diagnostics[0].Message);
    }

    /// <summary>A file no form owns is said with its reason, never guessed at: no XML and no JSON, an XML root no form owns, JSON of no form's shape.</summary>
    [Theory]
    [InlineData("some output a tool printed", "it is no XML and no JSON")]
    [InlineData("<report><case name='one'/></report>", "its root is 'report', and none of the results forms owns it")]
    [InlineData("{\"answer\": 42}", "it is JSON, and none of the results forms owns it")]
    public void AFileNoFormOwns_IsSaidWithItsReason(string text, string because)
    {
        var digest = Digest(text, out var problem);

        Assert.False(digest.HoldsAFinding);
        Assert.Equal(because, problem);
    }

    /// <summary>A results file that declares a document type is refused as the mutation arm's reports are: no reader reaches for other files.</summary>
    [Fact]
    public void AResultsFileDeclaringADocumentType_IsRefused()
    {
        var digest = Digest("<!DOCTYPE testsuites [<!ENTITY xxe \"file\">]><testsuites/>", out var problem);

        Assert.False(digest.HoldsAFinding);
        Assert.Equal("it declares a document type, which no report needs and this never reads", problem);
    }

    /// <summary>
    /// What a test invocation's results name in its run's results directory is read as one digest - every file's findings
    /// gathered before the bounds, so the counts are over all of them - through the mask the phase's log is written
    /// through, and what a path could not give said beside it, masked too.
    /// </summary>
    [Fact]
    public void AnInvocationsResults_AreReadAsOneDigest_ThroughThePhasesMask()
    {
        const string Secret = "sk-secret-one";
        using var temp = new TempDirectory();
        var results = temp.Combine("results");
        temp.WriteFile(Path.Combine("results", "a.xml"), Report("one", $"saw {Secret}"));
        temp.WriteFile(Path.Combine("results", "b.xml"), Report("two", "plain"));
        var paths = new Core.Execution.LegPaths(temp.Path, temp.Combine("build")) { ResultsDirectory = results };
        var gathering = new ResultDigest.Gathering();

        var read = Core.Testing.TestResults.Read(
            new HarnessFactory().FileSystem,
            ["{resultsDir}/*.xml", $"{Secret}.xml"],
            paths,
            results,
            [],
            gathering,
            new Masking(Secret),
            TestContext.Current.CancellationToken);
        var digest = gathering.Digest();

        Assert.True(read);
        Assert.Equal(
            [("demo.one", "saw ***"), ("demo.two", "plain")],
            digest.Failures.Select(failure => (failure.Id, failure.Message)));
        Assert.Equal(2, digest.Failed);
        Assert.Equal(["results: '***.xml' named no file"], digest.Unparsed);

        static string Report(string name, string message) => $"""
            <testsuites><testsuite name="demo"><testcase classname="demo" name="{name}"><failure message="{message}"/></testcase></testsuite></testsuites>
            """;
    }

    /// <summary>
    /// What the results file <paramref name="text"/> holds, read by the one reader of a results file
    /// (<see cref="ResultFiles.Read"/>) into a gathering of its own, and what could not be read of it.
    /// </summary>
    private static ResultDigest Digest(string text, out string? problem, IReadOnlyList<IResultParser>? parsers = null)
    {
        var gathering = new ResultDigest.Gathering();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        problem = ResultFiles.Read(() => new MemoryStream(bytes, writable: false), gathering, parsers ?? []);
        return gathering.Digest();
    }
}
