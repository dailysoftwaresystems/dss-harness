using System.Globalization;
using System.Xml.Linq;
using DssHarness.Core.Execution;
using DssHarness.Core.Json;
using DssHarness.Core.Mutations;

namespace DssHarness.Core.Results;

/// <summary>
/// The structured results a tool writes as data, read where they are: one owner of the forms the tools the tool runs
/// can be asked to leave behind - a TRX (<c>dotnet test --logger trx</c>, xUnit v3 <c>--report-trx</c>), the JUnit XML
/// family (ctest <c>--output-junit</c>, gtest <c>--gtest_output=xml</c>, Catch2, jest-junit, vitest), Dart's
/// <c>--reporter json</c> stream, and a SARIF log (csc <c>-errorlog</c>, gcc 13 and later
/// <c>-fdiagnostics-format=sarif-file</c>, clang <c>-fdiagnostics-format=sarif</c>). A file a tool wrote as data is
/// read before any log line is parsed, for what a file holds is typed already; what it does not hold no parser of
/// lines invents. Text read from these files is masked through the mask it is given, because a copied file is not
/// masked as a log line is.
/// </summary>
public static class ResultFiles
{
    /// <summary>The lines of a stack a file held as data, whatever newlines it was written with.</summary>
    /// <param name="stack">The stack's text.</param>
    internal static IReadOnlyList<string> LinesOf(string stack)
        => stack.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    /// <summary>
    /// Reads <paramref name="text"/> as the results file it is: recognized by its own shape - an XML root a form owns,
    /// or JSON of one form's shape, never by its name - and digested into the diagnostics and failures it holds. Null,
    /// with <paramref name="problem"/> saying why, where it is none of the forms.
    /// </summary>
    /// <param name="text">The file's text, whole.</param>
    /// <param name="mask">The mask its messages are masked through; null to read them as the log is.</param>
    /// <param name="problem">Why it is none of the forms, or null where it is one.</param>
    public static ResultDigest? Digest(string text, ILineMask? mask, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(text);

        problem = null;
        var trimmed = text.TrimStart();

        if (trimmed.StartsWith('<'))
        {
            return Xml(text, mask, out problem);
        }

        if (trimmed.StartsWith('{'))
        {
            return Json(text, mask, out problem);
        }

        problem = "it is no XML and no JSON";
        return null;
    }

    /// <summary>The XML forms: a TRX by its root, a JUnit report by the mutation arm's reader, which owns that form.</summary>
    private static ResultDigest? Xml(string text, ILineMask? mask, out string? problem)
    {
        problem = null;
        XDocument document;

        try
        {
            using var reader = System.Xml.XmlReader.Create(
                new StringReader(text),
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });

            document = XDocument.Load(reader);
        }
        catch (System.Xml.XmlException ex)
        {
            problem = $"it is no XML: {ex.Message}";
            return null;
        }

        if (document.Root is not { } root)
        {
            problem = "it holds no root";
            return null;
        }

        if (root.Name.LocalName == "TestRun")
        {
            return ResultDigest.Of([], [.. TrxFailures(root, mask)], []);
        }

        if (root.Name.LocalName is "testsuites" or "testsuite")
        {
            if (JUnitReport.Read(text, out problem) is not { } report)
            {
                return null;
            }

            return ResultDigest.Of(
                [],
                [.. report.Cases.Where(@case => @case.Ran && @case.Red).Select(@case =>
                {
                    var split = @case.Id.LastIndexOf('.');

                    return new TestFailure(
                        "junit",
                        @case.Id,
                        Name: split > 0 ? @case.Id[(split + 1)..] : null,
                        Class: split > 0 ? @case.Id[..split] : null,
                        Message: @case.Message is { } said ? mask?.Redact(said) ?? said : null,
                        File: @case.File,
                        Line: @case.Line,
                        Duration: @case.Time,
                        Outcome: @case.Outcome ?? "failed");
                })],
                []);
        }

        problem = $"its root is '{root.Name.LocalName}', and none of the results forms owns it";
        return null;
    }

    /// <summary>The JSON forms: Dart's reporter stream by its lines, a SARIF log by its runs.</summary>
    private static ResultDigest? Json(string text, ILineMask? mask, out string? problem)
    {
        problem = null;

        if (TrxDartJson.IsDartStream(text))
        {
            var (diagnostics, failures) = TrxDartJson.Read(text, mask);

            return ResultDigest.Of(diagnostics, failures, []);
        }

        if (text.Contains("\"runs\"", StringComparison.Ordinal) && JsonDepth.ParseNode(text) is System.Text.Json.Nodes.JsonObject { } whole)
        {
            return SarifDigest(whole, mask, out problem);
        }

        problem = "it is JSON, and none of the results forms owns it";
        return null;
    }

    /// <summary>A TRX's failed results: each <c>UnitTestResult</c> that did not pass, its message and its stack.</summary>
    private static IEnumerable<TestFailure> TrxFailures(XElement root, ILineMask? mask)
    {
        foreach (var result in root.Descendants().Where(element => element.Name.LocalName == "UnitTestResult"))
        {
            var outcome = (string?)result.Attribute("outcome");

            if (outcome is not ("Failed" or "NotExecuted"))
            {
                continue;
            }

            var id = (string?)result.Attribute("testName") ?? string.Empty;
            var error = result.Descendants().FirstOrDefault(element => element.Name.LocalName == "ErrorInfo");
            var said = error?.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value.Trim();
            var stack = error?.Descendants().FirstOrDefault(element => element.Name.LocalName == "StackTrace")?.Value;
            IReadOnlyList<StackFrame> frames = stack is null ? [] : DotNetStackParser.FramesOf(ResultFiles.LinesOf(stack));
            var placed = frames.FirstOrDefault(frame => frame.File is not null);
            var split = id.LastIndexOf('.');

            yield return new TestFailure(
                "trx",
                id,
                Name: split > 0 ? id[(split + 1)..] : null,
                Class: split > 0 ? id[..split] : null,
                Message: said is { Length: > 0 } message ? mask?.Redact(message) ?? message : null,
                File: placed?.File,
                Line: placed?.Line,
                Duration: TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var took) && took >= TimeSpan.Zero ? took.TotalSeconds : null,
                Outcome: outcome == "Failed" ? "failed" : "errored")
            {
                Stack = frames,
            };
        }
    }

    /// <summary>A SARIF log's results, each a diagnostic: its tool's name, its level, its rule, its message and its place.</summary>
    private static ResultDigest? SarifDigest(System.Text.Json.Nodes.JsonObject log, ILineMask? mask, out string? problem)
    {
        problem = null;

        if (log["runs"]?.AsArray() is not { } runs)
        {
            problem = "it holds no runs";
            return null;
        }

        var diagnostics = new List<Diagnostic>();

        foreach (var run in runs.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            var tool = run["tool"]?["driver"]?["name"]?.GetValue<string>() is { Length: > 0 } named ? named : "sarif";

            foreach (var found in run["results"]?.AsArray().OfType<System.Text.Json.Nodes.JsonObject>() ?? [])
            {
                var said = found["message"]?["text"]?.GetValue<string>();
                var place = found["locations"]?.AsArray().OfType<System.Text.Json.Nodes.JsonObject>()
                    .Select(location => location["physicalLocation"]).OfType<System.Text.Json.Nodes.JsonObject>()
                    .FirstOrDefault();

                if (said is null && place is null)
                {
                    continue;
                }

                var region = place?["region"]?.AsObject();
                var uri = place?["artifactLocation"]?["uri"]?.GetValue<string>();

                diagnostics.Add(new(
                    tool,
                    found["level"]?.GetValue<string>() is "error" or "warning" or "note" ? found["level"]!.GetValue<string>() : "note",
                    found["ruleId"]?.GetValue<string>(),
                    mask?.Redact(said ?? string.Empty) ?? said ?? string.Empty,
                    uri is null ? null : uri.StartsWith("file:///", StringComparison.Ordinal) ? uri["file:///".Length..] : uri,
                    region?["startLine"]?.GetValue<int>(),
                    region?["startColumn"]?.GetValue<int>(),
                    region?["endLine"]?.GetValue<int>(),
                    region?["endColumn"]?.GetValue<int>()));
            }
        }

        return ResultDigest.Of(diagnostics, [], []);
    }
}

/// <summary>Dart's <c>--reporter json</c> stream: one JSON object a line, the events of a run, read for its failures.</summary>
internal static class TrxDartJson
{
    /// <summary>Whether <paramref name="text"/> is the stream: its first line an object with a <c>type</c> member.</summary>
    public static bool IsDartStream(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var said = line.TrimEnd('\r').Trim();

            if (said.Length == 0)
            {
                continue;
            }

            if (!said.StartsWith('{'))
            {
                return false;
            }

            try
            {
                return JsonDepth.ParseNode(said) is System.Text.Json.Nodes.JsonObject held && held.ContainsKey("type");
            }
            catch (System.Text.Json.JsonException)
            {
                // A JSON form's first line cut short, or a JSON file of another shape: no line of a stream's is read alone.
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// The stream's failures and its errors: every test that did not pass, with the error event its own as its message
    /// and its stack, and the errors no test owns - a compilation, a harness failure - as diagnostics.
    /// </summary>
    /// <param name="text">The stream, whole.</param>
    /// <param name="mask">The mask its messages are masked through; null to read them as the log is.</param>
    public static (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures) Read(string text, ILineMask? mask)
    {
        var tests = new Dictionary<int, (string Name, string? Url, int? Line)>();
        var errors = new Dictionary<int, List<(string Error, string? Stack, bool Failure)>>();
        var failed = new List<(int Id, string Result)>();
        var unowned = new List<(string Error, string? Stack)>();
        var failures = new List<TestFailure>();

        foreach (var line in text.Split('\n'))
        {
            var said = line.TrimEnd('\r').Trim();

            if (said.Length == 0 || JsonDepth.ParseNode(said) is not System.Text.Json.Nodes.JsonObject held)
            {
                continue;
            }

            switch (held["type"]?.GetValue<string>())
            {
                case "testStart" when held["test"] is { } test && test["id"] is { } id:
                    tests[id.GetValue<int>()] = (
                        test["name"]?.GetValue<string>() ?? string.Empty,
                        test["url"]?.GetValue<string>(),
                        test["line"]?.GetValue<int>());
                    break;

                case "testDone" when held["result"]?.GetValue<string>() is { } result and not "success":
                    failed.Add((held["testID"]?.GetValue<int>() ?? -1, result));
                    break;

                case "error":
                    var error = held["error"]?.GetValue<string>() ?? string.Empty;
                    var stack = held["stackTrace"]?.GetValue<string>();

                    if (held["testID"]?.GetValue<int>() is { } own && tests.ContainsKey(own))
                    {
                        (errors.TryGetValue(own, out var list) ? list : errors[own] = []).Add((error, stack, held["isFailure"]?.GetValue<bool>() ?? false));
                    }
                    else
                    {
                        unowned.Add((error, stack));
                    }

                    break;

                default:
                    break;
            }
        }

        foreach (var (id, result) in failed)
        {
            if (!tests.TryGetValue(id, out var test))
            {
                continue;
            }

            var own = errors.GetValueOrDefault(id) is [var first, ..] ? first : ((string Error, string? Stack, bool Failure)?)null;
            var frames = own?.Stack is { } thrown ? DartStackParser.FramesOf(ResultFiles.LinesOf(thrown)) : [];

            failures.Add(new TestFailure(
                "dart",
                test.Name,
                Message: own is { } said ? mask?.Redact(said.Error) ?? said.Error : null,
                File: test.Url?.StartsWith("file:///", StringComparison.Ordinal) is true ? test.Url["file:///".Length..] : null,
                Line: test.Line,
                Exception: own is { Failure: false } unexpected
                    ? new ExceptionRecord("error", mask?.Redact(unexpected.Error) ?? unexpected.Error, frames, [])
                    : null)
            {
                Stack = frames,
                Outcome = result == "error" ? "errored" : "failed",
            });
        }

        return (
            [.. unowned.Select(error => new Diagnostic("dart", "error", Code: null, mask?.Redact(error.Error) ?? error.Error))],
            failures);
    }
}
