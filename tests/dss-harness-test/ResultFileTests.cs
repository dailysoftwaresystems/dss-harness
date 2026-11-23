using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The structured results a tool writes as data, read by their own shape: a TRX, a JUnit XML, Dart's JSON stream and
/// a SARIF log each give the typed failures and diagnostics they hold - masked through the mask they are given - and
/// a file no form owns is refused with a reason, never guessed at.
/// </summary>
public sealed class ResultFileTests
{
    /// <summary>A TRX as <c>dotnet test --logger trx</c> writes it: a failed result with its message and its stack.</summary>
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

    /// <summary>A JUnit XML as ctest writes it with <c>--output-junit</c>.</summary>
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
        {"type":"testDone","testID":1,"result":"failure","hidden":false,"time":"2026-11-23T10:00:00.250Z"}
        {"type":"error","testID":1,"error":"Expected: 3\n  Actual: 4","stackTrace":"#0 main (d:/src/test/charge_test.dart:8:5)\n#1 _main (d:/src/test/charge_test.dart:3:9)","isFailure":true,"time":"2026-11-23T10:00:00.250Z"}
        {"type":"error","testID":null,"error":"Failed to load \"suite_test.dart\": compilation failed","stackTrace":null,"isFailure":false,"time":"2026-11-23T10:00:01.000Z"}
        {"type":"done","success":false,"time":"2026-11-23T10:00:01.000Z"}
        """;

    /// <summary>A SARIF log as csc writes it with <c>-errorlog</c>.</summary>
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
                  "level": "warning",
                  "message": { "text": "The variable 'y' is assigned but its value is never used" },
                  "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "file:///d:/src/Program.cs" }, "region": { "startLine": 20, "startColumn": 13 } } } ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ATrx_IsReadByItsRoot_ItsFailuresTheirMessagesStacksAndPlaces()
    {
        var digest = ResultFiles.Digest(Trx, null, out var problem);

        Assert.Null(problem);
        Assert.NotNull(digest);
        Assert.Empty(digest!.Diagnostics);
        Assert.Equal(2, digest.Failures.Count);

        var one = digest.Failures[0];

        Assert.Equal(("trx", "DssHarness.Tests.Demo.One", "One", "DssHarness.Tests.Demo", "failed"), (one.Framework, one.Id, one.Name, one.Class, one.Outcome));
        Assert.Equal(1.25, one.Duration);
        Assert.Contains("Sub-string not found", one.Message, StringComparison.Ordinal);
        Assert.Equal(2, one.Stack!.Count);
        Assert.Equal(("D:/src/tests/Demo.cs", 12, "DssHarness.Tests.Demo.One()"), (one.File, one.Line, one.Stack[0].Symbol));
        Assert.Equal(30, one.Stack[1].Line);

        var notRun = digest.Failures[1];

        Assert.Equal(("DssHarness.Tests.Demo.Three", "errored"), (notRun.Id, notRun.Outcome));
    }

    [Fact]
    public void AJUnitReport_IsReadThroughTheMutatorArmsReader_ItsFailuresSaid()
    {
        var digest = ResultFiles.Digest(JUnit, null, out var problem);

        Assert.Null(problem);
        Assert.NotNull(digest);

        var one = Assert.Single(digest!.Failures);

        Assert.Equal(("junit", "demo.one", "one", "demo", "failed", "the charge was wrong", 0.006), (one.Framework, one.Id, one.Name, one.Class, one.Outcome, one.Message, one.Duration));
    }

    [Fact]
    public void ADartStream_IsReadByItsLines_ItsFailuresAndItsUnownedErrors()
    {
        var digest = ResultFiles.Digest(Dart, null, out var problem);

        Assert.Null(problem);
        Assert.NotNull(digest);

        var one = Assert.Single(digest!.Failures);

        Assert.Equal(("dart", "charge test", "failed"), (one.Framework, one.Id, one.Outcome));
        Assert.Contains("Expected: 3", one.Message, StringComparison.Ordinal);
        Assert.Equal(("d:/src/test/charge_test.dart", 7), (one.File, one.Line));
        Assert.Equal(2, one.Stack!.Count);
        Assert.Equal(("d:/src/test/charge_test.dart", 8, "main"), (one.Stack[0].File, one.Stack[0].Line, one.Stack[0].Symbol));
        Assert.Null(one.Exception);

        var unowned = Assert.Single(digest.Diagnostics);

        Assert.Equal(("dart", "error"), (unowned.Tool, unowned.Severity));
        Assert.Contains("compilation failed", unowned.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASarifLog_IsReadByItsRuns_ItsDiagnosticsTyped()
    {
        var digest = ResultFiles.Digest(Sarif, null, out var problem);

        Assert.Null(problem);
        Assert.NotNull(digest);
        Assert.Empty(digest!.Failures);
        Assert.Equal(2, digest.Diagnostics.Count);

        var one = digest.Diagnostics[0];

        Assert.Equal(("Microsoft Visual C# Compiler", "error", "CS0103", "d:/src/Program.cs", 12, 35, 12, 36), (one.Tool, one.Severity, one.Code, one.File, one.Line, one.Column, one.EndLine, one.EndColumn));
        Assert.Contains("does not exist", one.Message, StringComparison.Ordinal);
        Assert.Equal(digest.Diagnostics[0], digest.First);

        var warning = digest.Diagnostics[1];

        Assert.Equal("warning", warning.Severity);
        Assert.Null(warning.EndLine);
    }

    /// <summary>Text read from a results file is masked through the mask it is given: a copied file is not masked as a log line is.</summary>
    [Fact]
    public void AResultsFile_IsMaskedThroughTheMaskItIsGiven()
    {
        var digest = ResultFiles.Digest(JUnit.Replace("the charge was wrong", "the key sk-secret-one was wrong"), new FixedMask("***"), out _);

        Assert.NotNull(digest);
        Assert.Contains("the key *** was wrong", Assert.Single(digest!.Failures).Message, StringComparison.Ordinal);
    }

    /// <summary>A file no form owns is refused with a reason, never guessed at: no XML and no JSON, an XML root no form owns, JSON of no form's shape.</summary>
    [Theory]
    [InlineData("some output a tool printed", "it is no XML and no JSON")]
    [InlineData("<report><case name='one'/></report>", "none of the results forms owns it")]
    [InlineData("{\"answer\": 42}", "none of the results forms owns it")]
    public void AFileNoFormOwns_IsRefusedWithItsReason(string text, string because)
    {
        var digest = ResultFiles.Digest(text, null, out var problem);

        Assert.Null(digest);
        Assert.Contains(because, problem, StringComparison.Ordinal);
    }

    /// <summary>A results file that declares a document type is refused as the mutation arm's reports are: no reader reaches for other files.</summary>
    [Fact]
    public void AResultsFileDeclaringADocumentType_IsRefused()
    {
        var digest = ResultFiles.Digest("<!DOCTYPE testsuites [<!ENTITY xxe \"file\">]><testsuites/>", null, out var problem);

        Assert.Null(digest);
        Assert.NotNull(problem);
    }

    /// <summary>One mask over fixed text, for what the reading does with it.</summary>
    private sealed class FixedMask(string replacement) : ILineMask
    {
        public string Redact(string? text)
            => text?.Replace("sk-secret-one", replacement, StringComparison.Ordinal) ?? string.Empty;

        public int Cut(string text) => text.Length;
    }
}
