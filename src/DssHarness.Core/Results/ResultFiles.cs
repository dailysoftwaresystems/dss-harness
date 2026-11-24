using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using DssHarness.Core.Json;
using DssHarness.Core.Mutations;
using DssHarness.Core.Text;

namespace DssHarness.Core.Results;

/// <summary>
/// The structured results a tool writes as data, read where they are: one owner of the forms the tools the tool runs
/// can be asked to leave behind - a TRX (<c>dotnet test --logger trx</c>, xUnit v3 <c>--report-trx</c>), the JUnit XML
/// family (ctest <c>--output-junit</c>, gtest <c>--gtest_output=xml</c>, Catch2, jest-junit, vitest), Dart's
/// <c>--reporter json</c> stream, and a SARIF log (csc <c>-errorlog</c> in its 1.0 and 2.1 forms, gcc 13 and later
/// <c>-fdiagnostics-format=sarif-file</c>, clang <c>-fdiagnostics-format=sarif</c>). A file a tool wrote as data is
/// gathered ahead of the phase's log, for what a file holds is typed already, and the log is read beside it for what it
/// lacks (<see cref="PhaseDigest"/>); what a JUnit case's own test printed - its failure's body, its <c>system-out</c> and
/// <c>system-err</c> - is read by the phase's parsers, a stack by its runtime's own form (<see cref="JUnitFailures"/>).
/// Each is read as a stream - an XML form a test result at a time, the Dart stream a line at a time, a SARIF log a
/// result at a time - so no file is held whole for its size, and each is read to its end: a file a crash cut short gives
/// every finding before the break, and the break is said, never thrown. Its places are read as a log's are, by one owner
/// (<see cref="ParsedText.FileOf"/>): a <c>file:</c> URI as the path it names, any other text as the tool wrote it - and
/// read against the tree in one place (<see cref="ResultDigest.WithInTree(Func{string, string?}, Func{string, string, string?})"/>); its texts are
/// masked as they are gathered (<see cref="ResultDigest.Gathering.Through"/>).
/// </summary>
public static class ResultFiles
{

    /// <summary>
    /// Reads the results file <paramref name="open"/> opens, by its own shape - an XML root a form owns, or JSON of one
    /// form's shape, never by its name - gathering every diagnostic and failure it holds into <paramref name="into"/>, and
    /// answers what could not be read of it: why it is none of the forms, or where it broke off; <see langword="null"/>
    /// where it was read whole.
    /// </summary>
    /// <param name="open">Opens the file; opened again where a form is read from its start a second way.</param>
    /// <param name="into">Gathers what the file holds.</param>
    /// <param name="parsers">
    /// The parsers chosen for the phase that wrote it, which read what a JUnit case's own test printed
    /// (<see cref="JUnitFailures"/>); none where no phase is known, and a case's output is then read by none.
    /// </param>
    /// <exception cref="IOException">The file could not be read.</exception>
    public static string? Read(Func<Stream> open, ResultDigest.Gathering into, IReadOnlyList<IResultParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(parsers);

        using var text = new StreamReader(open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (text.Peek() is var next && next >= 0 && char.IsWhiteSpace((char)next))
        {
            text.Read();
        }

        return text.Peek() switch
        {
            '<' => Xml(text, () => new StreamReader(open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true), into, parsers),
            '{' => Json(text, open, into),
            _ => "it is no XML and no JSON",
        };
    }

    /// <summary>The XML forms: a TRX by its root, a JUnit report by the mutation arm's reader, which owns that form - each through the one guarded reader.</summary>
    private static string? Xml(TextReader text, Func<TextReader> open, ResultDigest.Gathering into, IReadOnlyList<IResultParser> parsers)
    {
        try
        {
            using var reader = JUnitReport.ReaderOf(text);

            switch (JUnitReport.RootOf(reader))
            {
                case "TestRun":
                    Trx(reader, into);
                    return null;

                case "testsuites" or "testsuite":
                    JUnitReport.CasesOf(reader, @case =>
                    {
                        if (@case is { Ran: true, Red: true })
                        {
                            JUnitFailures(@case, parsers, into);
                        }
                    });

                    return null;

                case var root:
                    return $"its root is '{root}', and none of the results forms owns it";
            }
        }
        catch (XmlException ex)
        {
            return JUnitReport.Unread(ex, open);
        }
    }

    /// <summary>
    /// A JUnit case that failed, gathered (<see cref="JUnitFailure"/>) with what its own test printed
    /// (<see cref="JUnitCase.Output"/>: its failure's body, its <c>system-out</c>, its <c>system-err</c>) read by the
    /// phase's parsers, as the lines its test printed - none of them a log's, so nothing read there has a log range. The
    /// case is said through one failure they find (<see cref="ResultDigest.Gathering.SaidThroughOf"/>: the crash its
    /// program died of where it crashed or timed out, else the first) and takes from it what it lacks
    /// (<see cref="ResultDigest.Gathering.SaidThrough"/>): that failure's stack and its thrown error - a body's stack read by
    /// its runtime's own stack form - its place where the case names none, what was expected and what there was, its
    /// message where the case says only how its test ended, and its crash where the case says its test merely failed and
    /// the failure is the crash its program died of, for that test and the case are one failure; the case is gathered said
    /// through it, so the log's printing of it is no second failure. Where they find no failure, the case takes its place
    /// from the first error they find (<see cref="ResultDigest.Gathering.PlacedBy"/>); where they find neither and the case
    /// says only how its test ended, its message is what its test printed, whole. Every other failure and every diagnostic
    /// they find is gathered as its own: what the test printed is never set aside. Each failure read there is the case's
    /// test's printing, so the log's printing of it under that test's output is its own, whatever order the console printed
    /// the tests in. A case said through none - the file kept none of its test's output, or nothing typed in it - is said
    /// through what the console printed under that test's own output, by the same rule
    /// (<see cref="ResultDigest.Gathering.Beside"/>).
    /// </summary>
    private static void JUnitFailures(JUnitCase @case, IReadOnlyList<IResultParser> parsers, ResultDigest.Gathering into)
    {
        var failure = JUnitFailure(@case);
        // How the program that printed a case's own output ended is not known: no results file says it.
        var found = @case.Output.Count == 0 ? null : PhaseParsers.FindingsOf(PhaseParsers.Numbered(@case.Output), parsers, _ => { }, () => null);
        List<TestFailure> failures = [.. (found?.Failures ?? []).Select(printed => printed with { Log = null })];
        List<Diagnostic> diagnostics = [.. (found?.Diagnostics ?? []).Select(printed => printed with { Log = null })];
        var through = ResultDigest.Gathering.SaidThroughOf(failures, failure.Outcome);
        var untold = @case.SaysOnlyHowItEnded;

        if (through is not null)
        {
            failure = ResultDigest.Gathering.SaidThrough(failure, through, untold);
        }
        else if (diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == "error") is { } error)
        {
            failure = ResultDigest.Gathering.PlacedBy(failure, error);
        }
        else if (untold && string.Join("\n", @case.Output).Trim() is { Length: > 0 } printed)
        {
            failure = failure with { Message = printed };
            untold = false;
        }

        into.Add(failure, through, untold);

        foreach (var diagnostic in diagnostics)
        {
            into.Add(diagnostic);
        }

        foreach (var other in failures)
        {
            if (!ReferenceEquals(other, through))
            {
                into.AddPrintedBy(other, failure.Id);
            }
        }
    }

    /// <summary>
    /// A JUnit case that failed, as a failure: its name split at its class, what it said, where, and how long. A case
    /// whose class is its own name - CTest names each test so, a name that holds a dot as much as one that does not - is
    /// named once, with no class: the report's reader keeps the case's id as an arm declares it (<c>classname.name</c>),
    /// and the digest names the test as the person who wrote it does.
    /// </summary>
    private static TestFailure JUnitFailure(JUnitCase @case)
    {
        var id = @case.Id;
        var half = (id.Length - 1) / 2;
        var once = half > 0 && id.Length % 2 == 1 && id[half] == '.' && id.AsSpan(0, half).SequenceEqual(id.AsSpan(half + 1));
        var named = once ? id[..half] : id;
        var split = once ? -1 : named.LastIndexOf('.');

        return new TestFailure(
            "junit",
            named,
            Name: once ? named : split > 0 ? named[(split + 1)..] : null,
            Class: split > 0 ? named[..split] : null,
            Message: ParsedText.Message(@case.Message),
            File: @case.File,
            Line: @case.Line,
            Duration: @case.Time,
            Outcome: @case.Outcome ?? "failed");
    }

    /// <summary>
    /// A TRX's results, one <c>UnitTestResult</c> held at a time: each that failed - <c>Failed</c>, <c>Error</c>,
    /// <c>Timeout</c>, <c>Aborted</c> or <c>Disconnected</c> - with its message and its stack, read by the one owner of the
    /// .NET forms (<see cref="StackForms"/>): where the message is a thrown error's, as a test framework writes one with
    /// its inner exceptions under it, that error's record holds the stack, each inner one's frames under it; an
    /// assertion's stack is its own. One with inner results (a data-driven test's rows) is read by each row. A test not
    /// run - <c>NotExecuted</c>, a skip - is no failure.
    /// </summary>
    private static void Trx(XmlReader reader, ResultDigest.Gathering into)
    {
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "UnitTestResult")
            {
                foreach (var failure in TrxFailures((XElement)XNode.ReadFrom(reader)))
                {
                    into.Add(failure);
                }
            }
            else
            {
                reader.Read();
            }
        }
    }

    /// <summary>One TRX result's failures: its own where it holds no rows, else each row's.</summary>
    private static IEnumerable<TestFailure> TrxFailures(XElement result)
    {
        var rows = result.Elements().Where(element => element.Name.LocalName == "InnerResults")
            .SelectMany(inner => inner.Elements().Where(element => element.Name.LocalName == "UnitTestResult"))
            .ToList();

        if (rows.Count > 0)
        {
            foreach (var failure in rows.SelectMany(TrxFailures))
            {
                yield return failure;
            }

            yield break;
        }

        var outcome = (string?)result.Attribute("outcome") switch
        {
            "Failed" => "failed",
            "Error" => "errored",
            "Timeout" => "timedOut",
            "Aborted" or "Disconnected" => "crashed",
            _ => null,
        };

        if (outcome is null)
        {
            yield break;
        }

        var id = (string?)result.Attribute("testName") ?? string.Empty;
        var error = result.Descendants().FirstOrDefault(element => element.Name.LocalName == "ErrorInfo");
        var said = error?.Elements().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value.Trim();
        var stack = error?.Elements().FirstOrDefault(element => element.Name.LocalName == "StackTrace")?.Value;
        var split = id.LastIndexOf('.');
        var thrown = said is null ? null : StackForms.DotNetThrownOf(LineEnds.Lines(said), stack is null ? [] : LineEnds.Lines(stack));

        yield return new TestFailure(
            "trx",
            id,
            Name: split > 0 ? id[(split + 1)..] : null,
            Class: split > 0 ? id[..split] : null,
            Message: ParsedText.Message(said),
            Exception: thrown,
            Stack: thrown is null && stack is not null ? StackForms.DotNetFramesOf(LineEnds.Lines(stack)) : [],
            Duration: TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var took) && took >= TimeSpan.Zero ? took.TotalSeconds : null,
            Outcome: outcome);
    }

    /// <summary>
    /// The JSON forms: Dart's reporter stream by its first line - an object with a <c>type</c> member - read on a line at
    /// a time; anything else as a SARIF log, read from its stream a result at a time (<see cref="SarifLog"/>).
    /// </summary>
    private static string? Json(TextReader text, Func<Stream> open, ResultDigest.Gathering into)
    {
        var first = text.ReadLine() ?? string.Empty;

        if (Line(first) is JsonObject { } line && line.ContainsKey("type"))
        {
            return DartStream.Read(line, text, into);
        }

        return SarifLog.Read(open, into);
    }

    /// <summary>One line read as a JSON document, or <see langword="null"/> where it is none - a line a stop cut short, or one of a document laid over several.</summary>
    private static JsonNode? Line(string line)
    {
        try
        {
            return line.Trim().Length == 0 ? null : JsonDepth.ParseNode(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A SARIF log, read from its stream a result at a time and never held whole, however large: each run's tool and each
    /// of its results taken whole as it is met, every other member passed by (<see cref="JsonTokens"/>). Each result a
    /// diagnostic: its tool's name, its level - <c>warning</c> where it names none, as SARIF says - its rule, its message and
    /// its place, a <c>file:</c> URI the path it names, in the 2.1 form and the 1.0 form csc writes by default. A run whose
    /// results come before its tool is read once more for them, its tool then known, so every result names its tool and
    /// the log's order is kept. A member of another shape than the form's is read as absent, never thrown over; a log a
    /// stop cut short gives every result before the break, and says the break.
    /// </summary>
    private static class SarifLog
    {
        /// <summary>What a JSON document no results form owns is said as.</summary>
        private const string Unowned = "it is JSON, and none of the results forms owns it";

        /// <summary>The tool a result is said as where its run names none.</summary>
        private const string NoTool = "sarif";

        /// <summary>Reads the log <paramref name="open"/> opens into <paramref name="into"/>, and answers what could not be read of it.</summary>
        public static string? Read(Func<Stream> open, ResultDigest.Gathering into)
        {
            var tools = new List<string?>();

            try
            {
                int? deferred;

                using (var stream = open())
                {
                    if (Pass(new JsonTokens(stream), tools, into, from: null, out deferred) is { } problem)
                    {
                        return problem;
                    }
                }

                if (deferred is { } first)
                {
                    using var again = open();
                    Pass(new JsonTokens(again), tools, into, first, out _);
                }

                return null;
            }
            catch (JsonException ex)
            {
                return $"it is no whole JSON document: {ex.Message}";
            }
        }

        /// <summary>
        /// One pass over the log: in the first (<paramref name="from"/> null), every run's tool, and the results of each run
        /// up to the first whose results came before its tool - that run's index given back as <paramref name="deferred"/>;
        /// in the second, the results of every run from <paramref name="from"/> on, every tool known by then.
        /// </summary>
        private static string? Pass(JsonTokens tokens, List<string?> tools, ResultDigest.Gathering into, int? from, out int? deferred)
        {
            deferred = null;

            if (!tokens.Read() || tokens.TokenType != JsonTokenType.StartObject)
            {
                return Unowned;
            }

            var runs = false;

            while (tokens.Read() && tokens.TokenType == JsonTokenType.PropertyName)
            {
                var name = tokens.GetString();

                tokens.Read();

                if (!string.Equals(name, "runs", StringComparison.Ordinal))
                {
                    tokens.Skip();
                    continue;
                }

                if (tokens.TokenType != JsonTokenType.StartArray)
                {
                    return "it holds no list of runs";
                }

                runs = true;

                var index = -1;

                while (tokens.Read() && tokens.TokenType != JsonTokenType.EndArray)
                {
                    if (tokens.TokenType != JsonTokenType.StartObject)
                    {
                        tokens.Skip();
                        continue;
                    }

                    index++;

                    if (tools.Count <= index)
                    {
                        tools.Add(null);
                    }

                    Run(tokens, index, tools, into, from, ref deferred);
                }
            }

            return runs ? null : Unowned;
        }

        /// <summary>One run, its first token read: its tool in the first pass, its results where this pass reads them.</summary>
        private static void Run(JsonTokens tokens, int index, List<string?> tools, ResultDigest.Gathering into, int? from, ref int? deferred)
        {
            var toolMet = false;

            while (tokens.Read() && tokens.TokenType == JsonTokenType.PropertyName)
            {
                var name = tokens.GetString();

                tokens.Read();

                if (from is null && string.Equals(name, "tool", StringComparison.Ordinal))
                {
                    var tool = tokens.TakeNode();

                    tools[index] = TextOf(Member(Member(tool, "driver"), "name")) ?? TextOf(Member(tool, "name"));
                    toolMet = true;
                }
                else if (string.Equals(name, "results", StringComparison.Ordinal) && tokens.TokenType == JsonTokenType.StartArray
                    && (from is { } again ? index >= again : deferred is null && toolMet))
                {
                    Results(tokens, tools[index] ?? NoTool, into);
                }
                else
                {
                    if (from is null && deferred is null && !toolMet && string.Equals(name, "results", StringComparison.Ordinal))
                    {
                        deferred = index;
                    }

                    tokens.Skip();
                }
            }
        }

        /// <summary>A run's results, their array's first token read: each taken whole and said as a diagnostic of <paramref name="tool"/>.</summary>
        private static void Results(JsonTokens tokens, string tool, ResultDigest.Gathering into)
        {
            while (tokens.Read() && tokens.TokenType != JsonTokenType.EndArray)
            {
                if (tokens.TokenType != JsonTokenType.StartObject)
                {
                    tokens.Skip();
                    continue;
                }

                var found = tokens.TakeNode();
                var place = (Member(found, "locations") as JsonArray ?? []).OfType<JsonObject>()
                    .Select(location => Member(location, "physicalLocation") ?? Member(location, "resultFile"))
                    .OfType<JsonObject>()
                    .FirstOrDefault();
                var region = Member(place, "region");

                into.Add(new Diagnostic(
                    tool,
                    TextOf(Member(found, "level")) switch
                    {
                        "error" => "error",
                        "note" or "none" or "pass" or "notApplicable" or "informational" => "note",
                        _ => "warning",
                    },
                    TextOf(Member(found, "ruleId")),
                    TextOf(Member(found, "message")) ?? TextOf(Member(Member(found, "message"), "text")) ?? string.Empty,
                    ParsedText.FileOf(TextOf(Member(Member(place, "artifactLocation"), "uri")) ?? TextOf(Member(place, "uri"))),
                    WholeOf(Member(region, "startLine")),
                    WholeOf(Member(region, "startColumn")),
                    WholeOf(Member(region, "endLine")),
                    WholeOf(Member(region, "endColumn"))));
            }
        }
    }

    /// <summary><paramref name="node"/>'s member <paramref name="name"/>, where it is an object; <see langword="null"/> where it is anything else.</summary>
    internal static JsonNode? Member(JsonNode? node, string name) => node is JsonObject held ? held[name] : null;

    /// <summary><paramref name="node"/>'s text, where it is a JSON string; <see langword="null"/> where it is anything else.</summary>
    internal static string? TextOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary><paramref name="node"/>'s whole number, where it is one an int holds; <see langword="null"/> where it is anything else.</summary>
    internal static int? WholeOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary><paramref name="node"/>'s flag, where it is a JSON true or false; <see langword="null"/> where it is anything else.</summary>
    internal static bool? FlagOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    /// <summary>
    /// Dart's <c>--reporter json</c> stream: one JSON object a line, the events of a run, read a line at a time for its
    /// failures - a test that ended other than in success, and one that met an error after it ended, which the protocol
    /// says failed - and every line that is no event said by count and the first of them, never thrown over: a stream a stop
    /// cut short gives every failure before its torn line.
    /// </summary>
    private static class DartStream
    {
        /// <summary>
        /// Reads the stream from its <paramref name="first"/> event on through <paramref name="text"/>: every test that did
        /// not pass with the error event its own as its message and its stack, in the order each was found failed, and the
        /// errors no test owns - a compilation, a harness failure - as diagnostics. Answers what could not be read of it.
        /// </summary>
        public static string? Read(JsonObject first, TextReader text, ResultDigest.Gathering into)
        {
            // Every test started is kept to the stream's end, for an error may name it after it ended.
            var tests = new Dictionary<int, (string Name, string? Url, int? Line)>();
            var errors = new Dictionary<int, (string Error, string? Stack, bool Failure)>();
            var ended = new Dictionary<int, string>();
            var failed = new List<int>();
            var unread = 0;
            var number = 1;
            int? firstUnread = null;

            Read(first, 1);

            while (text.ReadLine() is { } line)
            {
                number++;

                if (line.Trim().Length > 0)
                {
                    Read(Line(line) as JsonObject, number);
                }
            }

            foreach (var id in failed)
            {
                into.Add(Failure(tests[id], ended[id], errors.GetValueOrDefault(id)));
            }

            return unread == 0
                ? null
                : $"{unread.ToString(CultureInfo.InvariantCulture)} of its lines {(unread == 1 ? "is" : "are")} no event of the stream, the first line {firstUnread!.Value.ToString(CultureInfo.InvariantCulture)}";

            // A line that is no event, or an event whose test it cannot name, is counted and the first said.
            void Read(JsonObject? held, int at)
            {
                if (held is null || !Event(held))
                {
                    unread++;
                    firstUnread ??= at;
                }
            }

            bool Event(JsonObject held)
            {
                switch (TextOf(held["type"]))
                {
                    case "testStart":
                        if (WholeOf(Member(held["test"], "id")) is not { } id)
                        {
                            return false;
                        }

                        tests[id] = (TextOf(Member(held["test"], "name")) ?? string.Empty, TextOf(Member(held["test"], "url")), WholeOf(Member(held["test"], "line")));
                        return true;

                    case "error":
                        var error = TextOf(held["error"]) ?? string.Empty;
                        var stack = TextOf(held["stackTrace"]);

                        if (WholeOf(held["testID"]) is { } own && tests.ContainsKey(own))
                        {
                            // The first error a test met is its failure's; any after it are the same failure's echoes. One
                            // that came after the test ended in success fails it, as the protocol says.
                            errors.TryAdd(own, (error, stack, FlagOf(held["isFailure"]) ?? false));

                            if (ended.TryGetValue(own, out var result) && result == "success")
                            {
                                ended[own] = "error";
                                failed.Add(own);
                            }
                        }
                        else
                        {
                            into.Add(new Diagnostic("dart", "error", Code: null, error));
                        }

                        return true;

                    case "testDone":
                        if (WholeOf(held["testID"]) is not { } done || !tests.ContainsKey(done))
                        {
                            return false;
                        }

                        var outcome = TextOf(held["result"]) ?? "error";
                        ended[done] = outcome;

                        if (outcome != "success")
                        {
                            failed.Add(done);
                        }

                        return true;

                    default:
                        return true;
                }
            }
        }

        /// <summary>
        /// A test that did not pass: its name, its place, and the error its own, its lines cut by the one rule of where
        /// lines end - a failed expectation's message what it expected and what there was, its stack the failure's; an
        /// unexpected error's read by the one reading of a Dart error (<see cref="StackForms.DartThrownOf"/>), its stack
        /// its record's - so the same test reads the same from the console and from the reporter.
        /// </summary>
        private static TestFailure Failure((string Name, string? Url, int? Line) test, string result, (string Error, string? Stack, bool Failure) own)
        {
            IReadOnlyList<StackFrame> frames = own.Stack is { } stack ? StackForms.DartFramesOf(LineEnds.Lines(stack)) : [];
            List<string> lines = own.Error is { } error ? LineEnds.Lines(error) : [];
            var thrown = own.Failure ? null : StackForms.DartThrownOf(lines, frames);
            var (expected, actual) = thrown is null ? ConsoleText.ValuesOf(lines) : (null, null);

            return new TestFailure(
                "dart",
                test.Name,
                Message: ParsedText.Joined(lines),
                Expected: expected,
                Actual: actual,
                File: ParsedText.FileOf(test.Url),
                Line: test.Line,
                Exception: thrown,
                Stack: thrown is null ? frames : [],
                Outcome: result == "error" ? "errored" : "failed");
        }
    }
}
