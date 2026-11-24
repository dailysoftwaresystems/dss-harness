using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Results;

/// <summary>What the test consoles share in reading a failure: its name's parts, its expected and actual values, its duration.</summary>
internal static class ConsoleText
{
    private static readonly Regex Expected = LinearRegex.Compile(@"^\s*Expected: ?(?<value>.*)$");

    private static readonly Regex Actual = LinearRegex.Compile(@"^\s*(?:Actual|But was|Received): +(?<value>.*)$");

    private static readonly Regex Pair = LinearRegex.Compile(@"Expected:<(?<expected>.*)>\. Actual:<(?<actual>.*)>\.");

    private static readonly Regex Span = LinearRegex.Compile(@"(?<amount>\d+(?:\.\d+)?) ?(?<unit>ms|s|m|h)\b");

    /// <summary>
    /// What a failure's message says was expected and what there was: xUnit's and NUnit's <c>Expected:</c> line with its
    /// <c>Actual:</c> or <c>But was:</c> one, jest's and dart test's <c>Expected:</c> with <c>Received:</c> or <c>Actual:</c>,
    /// MSTest's <c>Expected:&lt;x&gt;. Actual:&lt;y&gt;.</c>; each the first the message says, null where it says none.
    /// </summary>
    /// <param name="message">The message's lines.</param>
    public static (string? Expected, string? Actual) ValuesOf(IEnumerable<string> message)
    {
        string? expected = null;
        string? actual = null;

        foreach (var line in message)
        {
            if (expected is null && actual is null && Pair.Match(line) is { Success: true } pair)
            {
                return (pair.Groups["expected"].Value, pair.Groups["actual"].Value);
            }

            if (expected is null && Expected.Match(line) is { Success: true } said)
            {
                expected = said.Groups["value"].Value.Trim();
            }
            else if (actual is null && Actual.Match(line) is { Success: true } was)
            {
                actual = was.Groups["value"].Value.Trim();
            }
        }

        return (expected, actual);
    }

    /// <summary>
    /// A duration as a console says it, in seconds: <c>12 ms</c>, <c>2 s</c>, <c>1 m 3 s</c>, <c>7ms</c>, <c>1s 024ms</c>.
    /// Null where it says none, or only a bound (<c>&lt; 1 ms</c>).
    /// </summary>
    /// <param name="said">The duration as printed.</param>
    public static double? SecondsOf(string said)
    {
        if (said.Contains('<', StringComparison.Ordinal))
        {
            return null;
        }

        double? seconds = null;

        foreach (Match part in Span.Matches(said))
        {
            var amount = double.Parse(part.Groups["amount"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

            seconds = (seconds ?? 0) + part.Groups["unit"].Value switch
            {
                "ms" => amount / 1000,
                "s" => amount,
                "m" => amount * 60,
                _ => amount * 3600,
            };
        }

        return seconds;
    }

    /// <summary>
    /// A test's class and method from its fully-qualified name - <c>Ns.Class.Method(args)</c>, <c>Suite.Name/0</c> - and its
    /// own name, the method with its arguments; all null where the name holds no <c>.</c> before its arguments.
    /// </summary>
    /// <param name="id">The test's name as the runner gives it.</param>
    public static (string? Class, string? Method, string? Name) PartsOf(string id)
    {
        var arguments = id.IndexOf('(', StringComparison.Ordinal);
        var head = arguments > 0 ? id[..arguments] : id;
        var dot = head.LastIndexOf('.');

        return dot <= 0 || dot == head.Length - 1 ? (null, null, null) : (head[..dot], head[(dot + 1)..], id[(dot + 1)..]);
    }
}

/// <summary>
/// The console <c>dotnet test</c> prints, for every framework it runs (xUnit, MSTest, NUnit), in both of its platforms'
/// forms. VSTest's: each <c>  Failed Name [1 ms]</c> with its <c>  Error Message:</c> and <c>  Stack Trace:</c> blocks
/// under it (and a test's <c>Standard Output Messages:</c>), the passed and skipped tests, the run's summary, and
/// <c>The active test run was aborted</c> where the test host crashed. Microsoft.Testing.Platform's: each <c>failed Name
/// (7ms)</c> with its message indented by two and its frames by four, xUnit's <c>XunitException:</c> wrapper taken off
/// the message, and its <c>Test run summary</c>. A message that is a thrown error's (<c>Type : message</c>, its inner ones
/// under it) is that error's record, its stack read through the .NET frame owner (<see cref="StackForms"/>); the
/// <c>[xUnit.net ...]</c> lines the adapter prints say again what the blocks say, and are part of the run.
/// </summary>
internal sealed class DotNetTestParser : IBuiltInParser
{
    private static readonly Regex VsFailed = LinearRegex.Compile(@"^  Failed (?<id>.+) \[(?<duration>[^\[\]]+)\]$");

    private static readonly Regex VsOther = LinearRegex.Compile(@"^  (?:Passed|Skipped) .+ \[[^\[\]]+\]$");

    private static readonly Regex VsPart = LinearRegex.Compile(@"^  (?<part>Error Message|Stack Trace|Standard Output Messages|Standard Error Messages|Debug Trace|Additional Information):$");

    private static readonly Regex MtpFailed = LinearRegex.Compile(@"^failed (?<id>.+) \((?<duration>[^()]+)\)$");

    private static readonly Regex MtpOther = LinearRegex.Compile(@"^(?:passed|skipped) .+ \([^()]+\)$");

    private static readonly Regex MtpWrapper = LinearRegex.Compile(@"^(?:[\w.]+\.)?XunitException: (?<message>.*)$");

    private static readonly Regex StackMarker = LinearRegex.Compile(@"^\s*(?:----- Inner Stack Trace(?: #\d+ \([^)]*\))? -----|--- End of inner exception stack trace ---)\s*$");

    private static readonly Regex Adapter = LinearRegex.Compile(@"^\[xUnit\.net \d+:\d+:\d+\.\d+\]");

    private static readonly Regex Aborted = LinearRegex.Compile(@"^The active test run was aborted\. Reason: (?<reason>.+)$");

    private static readonly Regex Summary = LinearRegex.Compile(@"^(?:(?:Failed|Passed|Skipped)! +- Failed: +\d+, Passed: +\d+, Skipped: +\d+, Total: +\d+, Duration: .+|Test Run (?:Failed|Successful|Aborted)\.|Test run summary: .+)$");

    private static readonly Regex SummaryCount = LinearRegex.Compile(@"^\s*(?:Total tests|Passed|Failed|Skipped|Not Executed|total|failed|succeeded|skipped|error|duration): .+$|^ Total time: .+$");

    public string Name => "dotnet-test";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private string? _id;
        private string? _duration;
        private bool _mtp;
        private string? _part;
        private readonly List<string> _message = [];
        private readonly List<string> _stack = [];
        private bool _skipped;
        private bool _summary;
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_id is not null || _skipped)
            {
                if (ReadBlock(line) is { } taken)
                {
                    return taken;
                }

                Close(sink);
            }

            if (_summary && SummaryCount.IsMatch(text))
            {
                return LineTaken.Took;
            }

            _summary = false;

            if (VsFailed.Match(text) is { Success: true } vs)
            {
                Open(line, vs, mtp: false);
                return LineTaken.Took;
            }

            if (MtpFailed.Match(text) is { Success: true } mtp)
            {
                Open(line, mtp, mtp: true);
                return LineTaken.Took;
            }

            if (VsOther.IsMatch(text) || MtpOther.IsMatch(text))
            {
                _skipped = MtpOther.IsMatch(text);
                return LineTaken.Took;
            }

            if (Adapter.IsMatch(text))
            {
                return LineTaken.Took;
            }

            if (Aborted.Match(text) is { Success: true } aborted)
            {
                sink.Add(new TestFailure("dotnet", "test run", Message: aborted.Groups["reason"].Value, Outcome: "crashed", Log: (line.Number, line.Last)));
                return LineTaken.Took;
            }

            if (Summary.IsMatch(text))
            {
                _summary = true;
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            _summary = false;
            return LineTaken.Took;
        }

        /// <summary>What the open failure made of the next line; null where the failure ended before it.</summary>
        private LineTaken? ReadBlock(NumberedLine line)
        {
            var text = line.Text;

            if (_skipped)
            {
                // A skipped test's reason, indented under it.
                return text.StartsWith("  ", StringComparison.Ordinal) && text.Trim().Length > 0 ? LineTaken.Took : null;
            }

            if (_mtp)
            {
                if (StackForms.DotNetFrameOf(text) is not null || StackMarker.IsMatch(text))
                {
                    _part = "Stack Trace";
                    _stack.Add(text);
                    return Took(line);
                }

                if (_part is null && text.StartsWith("  ", StringComparison.Ordinal) && text.Trim().Length > 0)
                {
                    _message.Add(text[2..]);
                    return Took(line);
                }

                return null;
            }

            if (VsPart.Match(text) is { Success: true } part)
            {
                _part = part.Groups["part"].Value;
                return Took(line);
            }

            switch (_part)
            {
                case "Error Message" when text.Trim().Length > 0 && !VsFailed.IsMatch(text) && !VsOther.IsMatch(text) && !Summary.IsMatch(text):
                    _message.Add(text.StartsWith("   ", StringComparison.Ordinal) ? text[3..] : text);
                    return Took(line);
                case "Stack Trace" when StackForms.DotNetFrameOf(text) is not null || StackMarker.IsMatch(text):
                    _stack.Add(text);
                    return Took(line);
                case "Standard Output Messages" or "Standard Error Messages" or "Debug Trace" or "Additional Information" when text.Trim().Length > 0 && !VsFailed.IsMatch(text) && !VsOther.IsMatch(text):
                    return Took(line);
                default:
                    return null;
            }
        }

        private LineTaken Took(NumberedLine line)
        {
            _to = line.Last;
            return LineTaken.Took;
        }

        private void Open(NumberedLine line, Match head, bool mtp)
        {
            _id = head.Groups["id"].Value;
            _duration = head.Groups["duration"].Value;
            _mtp = mtp;
            _part = null;
            _from = line.Number;
            _to = line.Last;
        }

        private void Close(ResultSink sink)
        {
            if (_id is { } id)
            {
                if (_mtp && _message.Count > 0 && MtpWrapper.Match(_message[0]) is { Success: true } wrapped)
                {
                    _message[0] = wrapped.Groups["message"].Value;
                }

                var (type, method, name) = ConsoleText.PartsOf(id);
                var (expected, actual) = ConsoleText.ValuesOf(_message);

                sink.Add(new TestFailure(
                    "dotnet",
                    id,
                    name,
                    type,
                    method,
                    ParsedText.Joined(_message),
                    expected,
                    actual,
                    Exception: StackForms.DotNetThrownOf(_message, _stack),
                    Stack: StackForms.DotNetFramesOf(_stack),
                    Duration: _duration is { } duration ? ConsoleText.SecondsOf(duration) : null,
                    Log: (_from, _to)));
            }

            _id = null;
            _duration = null;
            _part = null;
            _skipped = false;
            _message.Clear();
            _stack.Clear();
        }
    }
}

/// <summary>
/// GoogleTest's console: each <c>[ RUN      ] Suite.Name</c> to its <c>[  FAILED  ] Suite.Name (N ms)</c>, the place of
/// each failure inside it - <c>path:line: Failure</c>, or <c>path(line): error: message</c> where MSVC built it - with the
/// lines of its message under it; a parameterized test's <c>Instance/Suite.Name/0</c> and its <c>, where GetParam() =
/// ...</c>. A test that a <c>[ RUN ]</c> opened and nothing closed has crashed. The summary repeats the failed tests, and
/// says one only where its own block did not.
/// </summary>
internal sealed class GTestParser : IBuiltInParser
{
    private static readonly Regex Run = LinearRegex.Compile(@"^\[ RUN      \] (?<id>\S+)$");

    private static readonly Regex Passed = LinearRegex.Compile(@"^\[ +(?:OK|SKIPPED) +\] (?<id>\S+)(?: \(\d+ ms\))?$");

    private static readonly Regex Failed = LinearRegex.Compile(@"^\[  FAILED  \] (?<id>[^\s,]+)(?:, where (?<where>.+?))?(?: \((?<ms>\d+) ms\))?$");

    private static readonly Regex Bar = LinearRegex.Compile(@"^\[(?:==========|----------|  PASSED  |  SKIPPED |  FAILED  )\] .*$|^ ?\d+ FAILED TESTS?$");

    private static readonly Regex Place = LinearRegex.Compile(@"^(?:(?<file>.+?):(?<line>\d+): Failure|(?<msvcfile>.+?)\((?<msvcline>\d+)\): error: (?<message>.*))$");

    public string Name => "gtest";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private string? _id;
        private string? _file;
        private int? _line;
        private bool _placed;
        private readonly List<string> _message = [];
        private readonly HashSet<string> _said = new(StringComparer.Ordinal);
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            // CTest's own line after a test GoogleTest never said the end of: the binary's output ended there, so the test
            // crashed, its output the lines before - none of CTest's a line of its message.
            if (_id is not null && CTestParser.Says(text))
            {
                Close(sink, "crashed", null);
                return LineTaken.Left;
            }

            if (Run.Match(text) is { Success: true } run)
            {
                Close(sink, "crashed", null);
                _id = run.Groups["id"].Value;
                _from = line.Number;
                _to = line.Last;
                return LineTaken.Took;
            }

            if (Failed.Match(text) is { Success: true } failed)
            {
                var id = failed.Groups["id"].Value;

                if (_id == id && failed.Groups["ms"].Success)
                {
                    _to = line.Last;
                    Close(sink, "failed", ParsedText.Whole(failed.Groups["ms"]) is { } ms ? ms / 1000.0 : 0);
                }
                else if (!_said.Contains(id) && _id != id)
                {
                    sink.Add(new TestFailure("gtest", id, Class: ConsoleText.PartsOf(id).Class, Method: ConsoleText.PartsOf(id).Method, Log: (line.Number, line.Last)));
                    _said.Add(id);
                }

                return LineTaken.Took;
            }

            if (Passed.Match(text) is { Success: true } passed)
            {
                if (_id == passed.Groups["id"].Value)
                {
                    _id = null;
                    _placed = false;
                    _message.Clear();
                }

                return LineTaken.Took;
            }

            if (Bar.IsMatch(text))
            {
                return LineTaken.Took;
            }

            if (_id is not null && Place.Match(text) is { Success: true } place)
            {
                if (_file is null)
                {
                    _file = ParsedText.Text(place.Groups["file"]) ?? place.Groups["msvcfile"].Value;
                    _line = ParsedText.Whole(place.Groups["line"]) ?? ParsedText.Whole(place.Groups["msvcline"]);
                }

                if (ParsedText.Text(place.Groups["message"]) is { } said)
                {
                    _message.Add(said);
                }

                _placed = true;
                _to = line.Last;
                return LineTaken.Took;
            }

            if (_id is not null && _placed)
            {
                _message.Add(text);
                _to = line.Last;
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink, "crashed", null);
            return LineTaken.Took;
        }

        private void Close(ResultSink sink, string outcome, double? seconds)
        {
            if (_id is { } id)
            {
                var (type, method, _) = ConsoleText.PartsOf(id);
                var (expected, actual) = ConsoleText.ValuesOf(_message);

                sink.Add(new TestFailure(
                    "gtest",
                    id,
                    Class: type,
                    Method: method,
                    Message: ParsedText.Joined(_message),
                    Expected: expected,
                    Actual: actual,
                    File: _file,
                    Line: _line,
                    Duration: seconds,
                    Outcome: outcome,
                    Log: (_from, _to)));
                _said.Add(id);
            }

            _id = null;
            _file = null;
            _line = null;
            _placed = false;
            _message.Clear();
        }
    }
}

/// <summary>
/// CTest's console: each test's status line, <c>3/5 Test #3: name ....***Failed  0.01 sec</c> - <c>Failed</c> a failure,
/// <c>Timeout</c> one timed out, <c>Not Run</c> one errored, a signal or an abort (<c>Exception: SegFault</c>,
/// <c>SEGFAULT</c>, <c>Subprocess aborted</c>, <c>Child aborted</c>) one crashed - and its failed list, <c>3 - name
/// (SEGFAULT)</c>. A failed test is said by its own output where that output said why (a gtest's failures, a sanitizer's
/// report, printed under it by <c>--output-on-failure</c>), and by its status line where nothing did; the failed list and
/// <c>Errors while running CTest</c> say only what was said, and a test only they name is said from them.
/// </summary>
internal sealed class CTestParser : IBuiltInParser
{
    private static readonly Regex Status = LinearRegex.Compile(@"^\s*\d+/\d+ +Test +#\d+: (?<name>.+?) \.*(?:\*\*\*| +)(?<status>[A-Za-z][^*]*?) +(?<seconds>\d+(?:\.\d+)?) sec$");

    private static readonly Regex Start = LinearRegex.Compile(@"^\s*Start +\d+: .+$");

    private static readonly Regex Listed = LinearRegex.Compile(@"^\s+\d+ - (?<name>.+) \((?<status>[^()]+)\)$");

    private static readonly Regex Summary = LinearRegex.Compile(@"^(?:\d+% tests passed, \d+ tests? failed out of \d+|Total Test time \(real\) = .+|The following tests FAILED:|Output from these tests are in: .+|Use ""--rerun-failed --output-on-failure"" to re-run the failed cases verbosely\.|Test project .+)$");

    private static readonly Regex Errors = LinearRegex.Compile(@"^Errors while running CTest$");

    public string Name => "ctest";

    public IResultReading Begin() => new Reading();

    /// <summary>
    /// Whether <paramref name="line"/> is one CTest itself prints - a test's start, its status, the summary, the line
    /// saying CTest met errors - and never a line of a test's own output.
    /// </summary>
    /// <param name="line">The line.</param>
    internal static bool Says(string line)
        => Status.IsMatch(line) || Start.IsMatch(line) || Summary.IsMatch(line) || Errors.IsMatch(line);

    /// <summary>How a failed test ended, from the status CTest says of it; null where it did not fail.</summary>
    internal static string? OutcomeOf(string status)
    {
        var said = status.Trim();

        return said.StartsWith("Passed", StringComparison.OrdinalIgnoreCase) || said.StartsWith("Disabled", StringComparison.OrdinalIgnoreCase) || said.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase) ? null
            : said.StartsWith("Timeout", StringComparison.OrdinalIgnoreCase) ? "timedOut"
            : said.StartsWith("Not Run", StringComparison.OrdinalIgnoreCase) ? "errored"
            : said.StartsWith("Failed", StringComparison.OrdinalIgnoreCase) ? "failed"
            : "crashed";
    }

    private sealed class Reading : IResultReading
    {
        private readonly List<(string Name, string Outcome, string Status, double? Seconds, int At, int Through)> _failed = [];
        private (string Name, string Outcome, string Status, double? Seconds, int At)? _open;
        private readonly HashSet<string> _known = new(StringComparer.Ordinal);
        private int? _errors;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (Status.Match(text) is { Success: true } status)
            {
                Shut(line.Number - 1);

                var name = status.Groups["name"].Value;
                _known.Add(name);

                if (OutcomeOf(status.Groups["status"].Value) is { } outcome)
                {
                    _open = (name, outcome, status.Groups["status"].Value.Trim(), double.TryParse(status.Groups["seconds"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ? seconds : null, line.Number);
                }

                return LineTaken.Took;
            }

            if (Start.IsMatch(text) || Summary.IsMatch(text))
            {
                Shut(line.Number - 1);
                return LineTaken.Took;
            }

            if (Listed.Match(text) is { Success: true } listed)
            {
                Shut(line.Number - 1);

                var name = listed.Groups["name"].Value;

                if (_known.Add(name))
                {
                    _failed.Add((name, OutcomeOf(listed.Groups["status"].Value) ?? "failed", listed.Groups["status"].Value, null, line.Number, line.Last));
                }

                return LineTaken.Took;
            }

            if (Errors.IsMatch(text))
            {
                Shut(line.Number - 1);
                _errors ??= line.Number;
                return LineTaken.Took;
            }

            if (text == "No tests were found!!!")
            {
                sink.Add(new Diagnostic("ctest", "error", null, text, Log: (line.Number, line.Last)));
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Shut(int.MaxValue);

            // A failed test is said by its status line only where nothing printed under it - its own output, up to the next
            // line CTest printed - said why it failed.
            foreach (var (name, outcome, status, seconds, at, through) in _failed)
            {
                if (!sink.SaidWithin(at + 1, through))
                {
                    sink.Add(new TestFailure(
                        "ctest",
                        name,
                        Message: outcome == "failed" && status == "Failed" ? null : status,
                        Duration: seconds,
                        Outcome: outcome,
                        Log: (at, at)));
                }
            }

            if (_errors is { } errors && sink.Said == 0)
            {
                sink.Add(new Diagnostic("ctest", "error", null, "Errors while running CTest", Log: (errors, errors)));
            }

            _failed.Clear();
            _known.Clear();
            _open = null;
            _errors = null;
            return LineTaken.Took;
        }

        /// <summary>The open failed test's output ends at <paramref name="through"/>: it waits, with that range, for the end.</summary>
        private void Shut(int through)
        {
            if (_open is { } open)
            {
                _failed.Add((open.Name, open.Outcome, open.Status, open.Seconds, open.At, Math.Max(through, open.At)));
            }

            _open = null;
        }
    }
}

/// <summary>
/// <c>dart test</c>'s console: each progress line <c>00:00 +0 -1: file: name [E]</c> opening a failure, the lines under
/// it - the matcher's <c>Expected:</c> and <c>Actual:</c>, or the thrown error's own words - and its stack in the terse
/// form (<see cref="StackForms.DartFrameOf"/>); a suite that failed to load (<c>loading file [E]</c>) has errored. The
/// progress lines, <c>Some tests failed.</c> and the <c>Failing tests:</c> list say only what was said.
/// </summary>
internal sealed class DartTestParser : IBuiltInParser
{
    private static readonly Regex Progress = LinearRegex.Compile(@"^\d+:\d+ \+\d+(?: ~\d+)?(?: -\d+)?: (?<rest>.*)$");

    private static readonly Regex Listed = LinearRegex.Compile(@"^  (?<name>\S.*)$");

    public string Name => "dart-test";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private string? _name;
        private readonly List<string> _message = [];
        private readonly List<StackFrame> _frames = [];
        private readonly HashSet<string> _said = new(StringComparer.Ordinal);
        private bool _listing;
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_name is not null)
            {
                if (text.StartsWith("  ", StringComparison.Ordinal) || text.Length == 0)
                {
                    if (StackForms.DartFrameOf(text) is { } frame)
                    {
                        _frames.Add(frame);
                    }
                    else if (text.Trim().Length > 0 && _frames.Count == 0)
                    {
                        _message.Add(text[2..]);
                    }

                    _to = text.Trim().Length > 0 ? line.Last : _to;
                    return LineTaken.Took;
                }

                Close(sink);
            }

            if (Progress.Match(text) is { Success: true } progress)
            {
                var rest = progress.Groups["rest"].Value;

                if (rest.EndsWith(" [E]", StringComparison.Ordinal))
                {
                    _name = rest[..^4];
                    _from = line.Number;
                    _to = line.Last;
                }

                _listing = false;
                return LineTaken.Took;
            }

            if (text == "Failing tests:")
            {
                _listing = true;
                return LineTaken.Took;
            }

            if (_listing && Listed.Match(text) is { Success: true } listed && _said.Contains(listed.Groups["name"].Value))
            {
                return LineTaken.Took;
            }

            _listing &= text.Length == 0;
            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            _listing = false;
            return LineTaken.Took;
        }

        private void Close(ResultSink sink)
        {
            if (_name is { } name)
            {
                var colon = name.IndexOf(": ", StringComparison.Ordinal);
                var loading = name.StartsWith("loading ", StringComparison.Ordinal);
                var (expected, actual) = ConsoleText.ValuesOf(_message);

                sink.Add(new TestFailure(
                    "dart",
                    name,
                    Name: colon > 0 && !loading ? name[(colon + 2)..] : null,
                    Class: colon > 0 && !loading ? name[..colon] : null,
                    Message: ParsedText.Joined(_message),
                    Expected: expected,
                    Actual: actual,
                    Stack: [.. _frames],
                    Outcome: loading ? "errored" : "failed",
                    Log: (_from, _to)));
                _said.Add(name);
            }

            _name = null;
            _message.Clear();
            _frames.Clear();
        }
    }
}

/// <summary>
/// Jest's console: <c>FAIL path</c> naming the file, each <c>  ● Suite › name</c> block under it - the matcher's words with
/// its <c>Expected:</c> and <c>Received:</c>, or a thrown error's line, the code excerpt, and the V8 frames - and the
/// summary. <c>  ● Console</c> blocks are a test's own output. A failure said again under <c>Summary of all failing
/// tests</c> is said once.
/// </summary>
internal sealed class JestParser : IBuiltInParser
{
    private static readonly Regex File = LinearRegex.Compile(@"^ ?(?:FAIL|PASS) +(?<file>\S+)(?: \(\d+(?:\.\d+)? m?s\))?$");

    private static readonly Regex Bullet = LinearRegex.Compile(@"^  ● (?<name>.+)$");

    private static readonly Regex Excerpt = LinearRegex.Compile(@"^\s+(?:>\s*)?\d+ \|.*$|^\s+\|.*$");

    private static readonly Regex Thrown = LinearRegex.Compile(@"^(?<type>[A-Z][\w$]*(?:Error|Exception))(?: \[[A-Z0-9_]+\])?: (?<message>.*)$");

    private static readonly Regex Summary = LinearRegex.Compile(@"^(?:(?:Test Suites|Tests|Snapshots|Time): .+|Ran all test suites.*|Summary of all failing tests)$");

    public string Name => "jest";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private string? _file;
        private string? _name;
        private bool _console;
        private readonly List<string> _message = [];
        private readonly List<StackFrame> _frames = [];
        private readonly HashSet<string> _said = new(StringComparer.Ordinal);
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_name is not null || _console)
            {
                if (Bullet.IsMatch(text) || (text.Length > 0 && !text.StartsWith("    ", StringComparison.Ordinal)))
                {
                    Close(sink);
                }
                else
                {
                    if (!_console)
                    {
                        Read(text);
                    }

                    _to = text.Length > 0 ? line.Last : _to;
                    return LineTaken.Took;
                }
            }

            if (File.Match(text) is { Success: true } file)
            {
                _file = file.Groups["file"].Value;
                return LineTaken.Took;
            }

            if (Bullet.Match(text) is { Success: true } bullet)
            {
                _console = bullet.Groups["name"].Value == "Console";
                _name = _console ? null : bullet.Groups["name"].Value;
                _from = line.Number;
                _to = line.Last;
                return LineTaken.Took;
            }

            return Summary.IsMatch(text) ? LineTaken.Took : LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            return LineTaken.Took;
        }

        private void Read(string text)
        {
            if (V8StackParser.FrameOf(text) is { } frame)
            {
                _frames.Add(frame);
            }
            else if (_frames.Count == 0 && !Excerpt.IsMatch(text))
            {
                _message.Add(text.Length >= 4 ? text[4..] : text);
            }
        }

        private void Close(ResultSink sink)
        {
            if (_name is { } name && _said.Add($"{_file}\n{name}"))
            {
                var message = ParsedText.Joined(_message);
                var (expected, actual) = ConsoleText.ValuesOf(_message);
                var thrown = message is not null && Thrown.Match(message.Split('\n')[0]) is { Success: true } error ? error : null;
                var separator = name.LastIndexOf(" › ", StringComparison.Ordinal);

                sink.Add(new TestFailure(
                    "jest",
                    name,
                    Name: separator > 0 ? name[(separator + 3)..] : name,
                    Class: _file,
                    Message: message,
                    Expected: expected,
                    Actual: actual,
                    Exception: thrown is null || message is null ? null : new ExceptionRecord(thrown.Groups["type"].Value, message[(message.IndexOf(": ", StringComparison.Ordinal) + 2)..], [.. _frames], []),
                    Stack: [.. _frames],
                    Log: (_from, _to)));
            }

            _name = null;
            _console = false;
            _message.Clear();
            _frames.Clear();
        }
    }
}

/// <summary>
/// Vitest's console: the run's <c> ❯ file (3 tests | 2 failed)</c> with a <c>× name</c> line for each failed test, then
/// under <c>Failed Tests</c> each <c> FAIL  file &gt; Suite &gt; name</c> block - its error's line, the <c>- Expected</c>
/// and <c>+ Received</c> diff, its frames <c> ❯ file:line:col</c> and the code excerpt - closed by its <c>⎯⎯⎯[1/2]⎯</c>
/// separator, and the summary. A file that failed to load, <c> FAIL  file [ file ]</c>, has errored. A test only a
/// <c>×</c> line names is said from it.
/// </summary>
internal sealed class VitestParser : IBuiltInParser
{
    private static readonly Regex Run = LinearRegex.Compile(@"^ (?:❯|✓|×) (?<file>\S+) \(\d+ tests?.*\).*$");

    private static readonly Regex Crossed = LinearRegex.Compile(@"^\s+× (?<name>.+?)(?: \d+(?:\.\d+)?m?s)?$");

    private static readonly Regex Said = LinearRegex.Compile(@"^\s+→ .+$");

    private static readonly Regex Failed = LinearRegex.Compile(@"^ FAIL  (?<file>\S+) (?:> (?<name>.+)|\[ .+ \])$");

    private static readonly Regex Frame = LinearRegex.Compile(@"^\s*❯ (?:(?<symbol>\S.*?) )?(?<file>\S+):(?<line>\d+):(?<col>\d+)$");

    private static readonly Regex Excerpt = LinearRegex.Compile(@"^\s+\d+\|.*$|^\s+\|.*$");

    private static readonly Regex Separator = LinearRegex.Compile(@"^⎯+(?:\[\d+/\d+\]⎯*| Failed (?:Tests?|Suites?) \d+ ⎯+)?$");

    private static readonly Regex Diff = LinearRegex.Compile(@"^(?<sign>[-+]) (?<value>.*)$");

    private static readonly Regex Thrown = LinearRegex.Compile(@"^(?<type>[A-Z][\w$]*(?:Error|Exception)): (?<message>.*)$");

    private static readonly Regex Summary = LinearRegex.Compile(@"^\s+(?:Test Files|Tests|Start at|Duration)  .+$");

    public string Name => "vitest";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private string? _runFile;
        private readonly List<(string File, string Name, int At)> _crossed = [];
        private (string File, string? Name)? _open;
        private readonly List<string> _message = [];
        private readonly List<StackFrame> _frames = [];
        private readonly HashSet<string> _said = new(StringComparer.Ordinal);
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_open is not null)
            {
                if (!Separator.IsMatch(text) && !Failed.IsMatch(text) && !Summary.IsMatch(text))
                {
                    if (Frame.Match(text) is { Success: true } frame)
                    {
                        _frames.Add(new StackFrame(text.Trim(), ParsedText.FileOf(frame.Groups["file"].Value), ParsedText.Whole(frame.Groups["line"]), ParsedText.Whole(frame.Groups["col"]), Symbol: ParsedText.Text(frame.Groups["symbol"])));
                    }
                    else if (_frames.Count == 0 && !Excerpt.IsMatch(text))
                    {
                        _message.Add(text);
                    }

                    _to = text.Trim().Length > 0 ? line.Last : _to;
                    return LineTaken.Took;
                }

                Close(sink);
            }

            if (Failed.Match(text) is { Success: true } failed)
            {
                _open = (failed.Groups["file"].Value, ParsedText.Text(failed.Groups["name"]));
                _from = line.Number;
                _to = line.Last;
                return LineTaken.Took;
            }

            if (Run.Match(text) is { Success: true } run)
            {
                _runFile = run.Groups["file"].Value;
                return LineTaken.Took;
            }

            if (_runFile is not null && Crossed.Match(text) is { Success: true } crossed)
            {
                _crossed.Add((_runFile, crossed.Groups["name"].Value, line.Number));
                return LineTaken.Took;
            }

            if (_crossed.Count > 0 && Said.IsMatch(text))
            {
                return LineTaken.Took;
            }

            if (Separator.IsMatch(text) || Summary.IsMatch(text))
            {
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);

            foreach (var (file, name, at) in _crossed.Where(crossed => !_said.Contains($"{crossed.File}\n{crossed.Name}")))
            {
                sink.Add(new TestFailure("vitest", name, Class: file, Log: (at, at)));
            }

            _crossed.Clear();
            _said.Clear();
            _runFile = null;
            return LineTaken.Took;
        }

        private void Close(ResultSink sink)
        {
            if (_open is { } open)
            {
                var message = ParsedText.Joined(_message);
                var thrown = message is not null && Thrown.Match(message.Split('\n')[0]) is { Success: true } error ? error : null;
                var (expected, actual) = DiffOf(_message);
                var separator = open.Name?.LastIndexOf(" > ", StringComparison.Ordinal) ?? -1;

                sink.Add(new TestFailure(
                    "vitest",
                    open.Name ?? open.File,
                    Name: open.Name is { } name ? (separator > 0 ? name[(separator + 3)..] : name) : null,
                    Class: open.File,
                    Message: message,
                    Expected: expected,
                    Actual: actual,
                    Exception: thrown is null || thrown.Groups["type"].Value == "AssertionError" ? null : new ExceptionRecord(thrown.Groups["type"].Value, thrown.Groups["message"].Value, [.. _frames], []),
                    Stack: [.. _frames],
                    Outcome: open.Name is null ? "errored" : "failed",
                    Log: (_from, _to)));
                _said.Add($"{open.File}\n{open.Name}");
            }

            _open = null;
            _message.Clear();
            _frames.Clear();
        }

        /// <summary>The first value under the diff's <c>- Expected</c> and <c>+ Received</c> headings; null where it shows none.</summary>
        private static (string? Expected, string? Actual) DiffOf(List<string> message)
        {
            var headed = message.FindIndex(line => line.Trim() == "+ Received");

            if (headed < 0)
            {
                return (null, null);
            }

            var values = message.Skip(headed + 1).Select(line => Diff.Match(line)).Where(diff => diff.Success).ToList();

            return (
                values.FirstOrDefault(diff => diff.Groups["sign"].Value == "-")?.Groups["value"].Value,
                values.FirstOrDefault(diff => diff.Groups["sign"].Value == "+")?.Groups["value"].Value);
        }
    }
}
