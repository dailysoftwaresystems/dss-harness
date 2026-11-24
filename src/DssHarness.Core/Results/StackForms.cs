using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Results;

/// <summary>
/// The frames and thrown errors of the runtimes whose stacks are read outside a log as well as in one: .NET's - a
/// process's, a test framework's, a results file's, the engine's own exception - and Dart's, the VM's and the terse form
/// its packages print. The one owner of each form: every reader of a .NET or a Dart frame reads it here.
/// </summary>
public static class StackForms
{
    private static readonly Regex DotNetFrame = LinearRegex.Compile(@"^\s*at (?<symbol>[^\s(]+)\((?<parameters>[^():]* [^():]*|[^():.\s]*)\)(?: in (?<file>.+):(?:line )?(?<line>\d+))?\s*$");

    private static readonly Regex DotNetAsync = LinearRegex.Compile(@"^\s*--- End of stack trace from previous location(?: where exception was thrown)? ---\s*$");

    private static readonly Regex InnerStack = LinearRegex.Compile(@"^\s*----- Inner Stack Trace(?: #\d+ \([^)]*\))? -----\s*$");

    private static readonly Regex FrameworkThrown = LinearRegex.Compile(@"^\s*(?<dashes>(?:----)*)\s*(?<type>[A-Za-z_][\w.`+]*) : (?<message>.*)$");

    private static readonly Regex DartVmFrame = LinearRegex.Compile(@"^#\d+\s+(?<symbol>.+?) \((?<location>.+?)(?::(?<line>\d+)(?::(?<col>\d+))?)?\)$");

    private static readonly Regex DartTerseFrame = LinearRegex.Compile(@"^\s*(?<location>(?:package|dart):\S+|\S+\.dart)(?: (?<line>\d+)(?::(?<col>\d+))?)?\s+(?<member>\S.*)$");

    /// <summary>
    /// The .NET frame <paramref name="line"/> is: <c>at N.C.M(Type name)</c>, its symbol the method with its parameters as
    /// printed, so an overload is told apart, with <c>in path:line N</c> - or, as
    /// Microsoft.Testing.Platform writes it, <c>in path:N</c> - where the runtime knew the place; and an awaited boundary,
    /// <c>--- End of stack trace from previous location ---</c>, as a frame of kind <c>async</c>. Null where the line is
    /// neither - a JVM's frame among them, whose parentheses hold a source file's name and not a parameter list.
    /// </summary>
    /// <param name="line">The line as printed.</param>
    public static StackFrame? DotNetFrameOf(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (DotNetAsync.IsMatch(line))
        {
            return new StackFrame(line.Trim(), Kind: "async");
        }

        if (DotNetFrame.Match(line) is not { Success: true } frame || frame.Groups["parameters"].Value is "Native Method" or "Unknown Source")
        {
            return null;
        }

        return new StackFrame(line.Trim(), ParsedText.Text(frame.Groups["file"]), ParsedText.Whole(frame.Groups["line"]), Symbol: $"{frame.Groups["symbol"].Value}({frame.Groups["parameters"].Value})");
    }

    /// <summary>
    /// The .NET frames of a stack printed alone - a results file's <c>StackTrace</c>, an exception's own: every frame and
    /// awaited boundary, in the order printed; the markers between an exception's stack and its inner one's are no frames.
    /// </summary>
    /// <param name="lines">The stack's lines.</param>
    public static IReadOnlyList<StackFrame> DotNetFramesOf(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return [.. lines.Select(DotNetFrameOf).OfType<StackFrame>()];
    }

    /// <summary>MSTest's head for a thrown error: <c>Test method X threw exception:</c>, the error's own line under it.</summary>
    private static readonly Regex MsTestHead = LinearRegex.Compile(@"^\s*Test method .+ threw exception[:.]?\s*$");

    /// <summary>One error of the line MSTest folds its inner ones onto after <c> ---&gt; </c>: <c>Type: message</c>, no space before the colon.</summary>
    private static readonly Regex MsTestThrown = LinearRegex.Compile(@"^\s*(?<type>[A-Za-z_][\w.`+]*)\s*:\s*(?<message>.*)$");

    /// <summary>
    /// The thrown .NET error a test framework said in a message and a stack, as xUnit and NUnit write it: the message's
    /// first line <c>Type : message</c>, each inner exception a line <c>---- Type : message</c> under it (four dashes a
    /// level deeper), and the stack the outer exception's frames, each inner one's after a <c>----- Inner Stack Trace
    /// -----</c> line. MSTest says one by its head - <c>Test method X threw exception:</c> - with the error's own line
    /// under it and each inner exception folded onto it after <c> ---&gt; </c>, the stack in the order the runtime
    /// printed it. Null where the message is no thrown error's - an assertion's.
    /// </summary>
    /// <param name="message">The message's lines.</param>
    /// <param name="stack">The stack's lines.</param>
    public static ExceptionRecord? DotNetThrownOf(IReadOnlyList<string> message, IReadOnlyList<string> stack)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(stack);

        var thrown = new ThrownLevels();
        var lines = (string[])[.. message];

        if (lines.Length > 0 && MsTestHead.IsMatch(lines[0]))
        {
            lines = lines[1..];

            // MSTest folds the error and each inner one onto one line, ` ---> ` between them; the first segment is the
            // error's, each after it the cause of the one before.
            var folded = lines.Length > 0 ? lines[0].Split(" ---> ") : [];

            if (folded.Length == 0 || !folded.All(segment => MsTestThrown.Match(segment) is { Success: true }))
            {
                return null;
            }

            foreach (var segment in folded)
            {
                var said = MsTestThrown.Match(segment);
                thrown.Add(said.Groups["type"].Value, said.Groups["message"].Value);
            }

            lines = lines[1..];
        }

        foreach (var line in lines)
        {
            if (FrameworkThrown.Match(line) is { Success: true } said && said.Groups["dashes"].Length / 4 == thrown.Count)
            {
                thrown.Add(said.Groups["type"].Value, said.Groups["message"].Value);
            }
            else if (thrown.Count == 0)
            {
                return null;
            }
            else
            {
                thrown.Continue(line);
            }
        }

        if (thrown.Count == 0)
        {
            return null;
        }

        thrown.At = 0;

        foreach (var line in stack)
        {
            if (InnerStack.IsMatch(line))
            {
                thrown.At = Math.Min(thrown.At + 1, thrown.Count - 1);
            }
            else if (DotNetFrameOf(line) is { } frame)
            {
                thrown.Frame(frame);
            }
        }

        return thrown.Record();
    }

    /// <summary>
    /// The Dart frame <paramref name="line"/> is, in either form the VM and its packages print: the VM's <c>#0 f
    /// (location:line:col)</c>, its <c>&lt;asynchronous suspension&gt;</c> as a frame of kind <c>async</c>, and the terse
    /// form <c>package:stack_trace</c> and <c>dart test</c> print, <c>location line:col  member</c>. A <c>file:</c> URI is
    /// read as the path it names; one of the runtime's own (<c>dart:</c>, <c>package:</c>) is kept as written, with its
    /// package as the frame's module. Null where the line is neither.
    /// </summary>
    /// <param name="line">The line as printed.</param>
    public static StackFrame? DartFrameOf(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var text = line.Trim();

        if (text == "<asynchronous suspension>")
        {
            return new StackFrame(text, Kind: "async");
        }

        var found = DartVmFrame.Match(text) is { Success: true } vm ? vm : DartTerseFrame.Match(line);

        if (!found.Success)
        {
            return null;
        }

        var location = found.Groups["location"].Value;

        return new StackFrame(
            text,
            ParsedText.FileOf(location),
            ParsedText.Whole(found.Groups["line"]),
            ParsedText.Whole(found.Groups["col"]),
            ParsedText.ModuleOf(location),
            ParsedText.Text(found.Groups["symbol"]) ?? ParsedText.Text(found.Groups["member"]));
    }

    /// <summary>The Dart frames of a stack printed alone - a JSON reporter event's <c>stackTrace</c>: every frame of either form, in the order printed.</summary>
    /// <param name="lines">The stack's lines.</param>
    public static IReadOnlyList<StackFrame> DartFramesOf(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return [.. lines.Select(DartFrameOf).OfType<StackFrame>()];
    }

    /// <summary>
    /// A Dart error as its lines say it, with <paramref name="frames"/> its stack: its type what its first line says before
    /// its first <c>: </c>, as Dart prints it (<c>Bad state</c>, <c>RangeError (index)</c>), else that whole line; its
    /// message the rest, every line of it. Null where its lines say nothing. The one reading of a Dart error, whichever
    /// printed it - the VM's <c>Unhandled exception:</c>, <c>dart test</c>'s console, its JSON reporter's event.
    /// </summary>
    /// <param name="lines">The error's lines, as printed.</param>
    /// <param name="frames">Its frames.</param>
    public static ExceptionRecord? DartThrownOf(IEnumerable<string> lines, IReadOnlyList<StackFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(frames);

        if (ParsedText.Joined(lines) is not { } said || said.Split('\n')[0] is not { Length: > 0 } first)
        {
            return null;
        }

        var colon = first.IndexOf(": ", StringComparison.Ordinal);

        return new ExceptionRecord(colon > 0 ? first[..colon] : first, colon > 0 ? said[(colon + 2)..] : string.Empty, frames, []);
    }

    /// <summary>
    /// Whether a Dart failure's top frame is a test framework's own - <c>package:matcher</c>'s <c>expect</c>,
    /// <c>package:test_api</c>'s <c>fail</c>, <c>package:checks</c>' checks: a failed expectation, where any other top frame
    /// is a thrown error's.
    /// </summary>
    /// <param name="top">The failure's first frame.</param>
    public static bool IsDartExpectation(StackFrame top)
    {
        ArgumentNullException.ThrowIfNull(top);

        return top.Module is "package:matcher" or "package:test_api" or "package:test" or "package:checks";
    }
}

/// <summary>
/// A thrown error being read with its inner ones: each level's type, message and frames, the frames going to the level
/// the reading is at - the runtime's own form printing the innermost's frames first and stepping out at each <c>---
/// End of inner exception stack trace ---</c>, a test framework's printing the outermost's first and stepping in at each
/// inner stack's marker.
/// </summary>
internal sealed class ThrownLevels
{
    private readonly List<(string Type, List<string> Message, List<StackFrame> Frames)> _levels = [];

    /// <summary>How many levels it holds.</summary>
    public int Count => _levels.Count;

    /// <summary>The level frames go to.</summary>
    public int At { get; set; }

    /// <summary>Adds the next inner level, and goes to it.</summary>
    public void Add(string type, string message)
    {
        _levels.Add((type, [message], []));
        At = _levels.Count - 1;
    }

    /// <summary>A further line of the last level's message.</summary>
    public void Continue(string line) => _levels[^1].Message.Add(line);

    /// <summary>A frame of the level it is at.</summary>
    public void Frame(StackFrame frame) => _levels[At].Frames.Add(frame);

    /// <summary>The whole error: the outermost level, each level the cause of the one above it.</summary>
    public ExceptionRecord Record()
    {
        ExceptionRecord? inner = null;

        for (var level = _levels.Count - 1; level >= 0; level--)
        {
            var (type, message, frames) = _levels[level];
            inner = new ExceptionRecord(type, ParsedText.Joined(message) ?? string.Empty, [.. frames], inner is null ? [] : [inner]);
        }

        return inner ?? throw new InvalidOperationException("A thrown error holds no level.");
    }
}

/// <summary>
/// A .NET stack trace as a process prints it: <c>Unhandled exception. Type: message</c> for one that ended the process,
/// with its message's further lines under it, or a <c>Namespace.Type: message</c> line a frame follows; each inner
/// exception a <c> ---&gt; Type: message</c> line, the innermost's frames first and <c>--- End of inner exception stack
/// trace ---</c> stepping out to the next; an awaited boundary a frame of kind <c>async</c>. One that ended the process has
/// <c>crashed</c> it; any other is <c>errored</c>. A logged <c>Type: message</c> line is decided by the line after it alone
/// - a frame or an inner exception's <c> ---&gt; </c> make it a thrown error's, any other line gives it back - so the
/// reading never holds more than that one line.
/// </summary>
internal sealed class DotNetStackParser : IBuiltInParser
{
    private static readonly Regex Unhandled = LinearRegex.Compile(@"^Unhandled exception\. (?<type>[A-Za-z_][\w.`+]*): (?<message>.*)$");

    private static readonly Regex Thrown = LinearRegex.Compile(@"^\s*(?<type>(?:[A-Za-z_][\w`+]*\.)+[A-Za-z_][\w`+]*(?:Exception|Error))(?: : |: )(?<message>.*)$");

    private static readonly Regex Inner = LinearRegex.Compile(@"^\s*---> (?<type>[A-Za-z_][\w.`+]*): (?<message>.*)$");

    private static readonly Regex InnerEnd = LinearRegex.Compile(@"^\s*--- End of inner exception stack trace ---\s*$");

    private static readonly Regex OtherFrame = LinearRegex.Compile(@"^\s+at \S");

    public string Name => "dotnet-stack";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private ThrownLevels? _thrown;
        private bool _crashed;
        private bool _framed;
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_thrown is { } thrown)
            {
                if (StackForms.DotNetFrameOf(text) is { } frame)
                {
                    thrown.Frame(frame);
                    return Framed(line);
                }

                if (Inner.Match(text) is { Success: true } inner)
                {
                    thrown.Add(inner.Groups["type"].Value, inner.Groups["message"].Value);
                    return Framed(line);
                }

                if (_framed && InnerEnd.IsMatch(text))
                {
                    thrown.At = Math.Max(thrown.At - 1, 0);
                    return Framed(line);
                }

                if (_crashed && !_framed && text.Trim().Length > 0 && !Thrown.IsMatch(text) && !OtherFrame.IsMatch(text))
                {
                    // The message's own further lines, which the runtime prints under the line that says the error ended
                    // the process, before its first frame.
                    thrown.Continue(text);
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (!_framed && !_crashed)
                {
                    // A logged head is a thrown error's only where the runtime's own next line follows it: this one says
                    // it was not, and nothing is held past it.
                    _thrown = null;
                    return LineTaken.Left;
                }

                Close(sink);
            }

            if (Unhandled.Match(text) is { Success: true } unhandled)
            {
                Open(line, unhandled, crashed: true);
                return LineTaken.Took;
            }

            if (Thrown.Match(text) is { Success: true } bare)
            {
                Open(line, bare, crashed: false);
                return LineTaken.Held;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            if (_thrown is not null && !_framed && !_crashed)
            {
                _thrown = null;
                return LineTaken.Left;
            }

            Close(sink);
            return LineTaken.Took;
        }

        private LineTaken Framed(NumberedLine line)
        {
            _framed = true;
            _to = line.Last;
            return LineTaken.Took;
        }

        private void Open(NumberedLine line, Match head, bool crashed)
        {
            _thrown = new ThrownLevels();
            _thrown.Add(head.Groups["type"].Value, head.Groups["message"].Value);
            _crashed = crashed;
            _framed = false;
            _from = line.Number;
            _to = line.Last;
        }

        private void Close(ResultSink sink)
        {
            if (_thrown is not { } thrown)
            {
                return;
            }

            var record = thrown.Record();

            sink.Add(new TestFailure(
                "dotnet",
                ParsedText.Headed(record.Type, record.Message),
                Message: record.Message.Length > 0 ? record.Message : null,
                Exception: record,
                Outcome: _crashed ? "crashed" : "errored",
                Log: (_from, _to)));
            _thrown = null;
        }
    }
}

/// <summary>
/// A JVM stack trace: <c>Exception in thread "main" type: message</c> for one that ended a thread, or a
/// <c>package.Type: message</c> line a frame follows; frames <c>at p.C.m(File.java:12)</c>, qualified by their class
/// loader and module as <c>at java.base/p.C.m(...)</c> or <c>at app//p.C.m(...)</c> since Java 9, and
/// <c>(Native Method)</c> and <c>(Unknown Source)</c> naming no line; each <c>Caused by:</c> the cause of the error
/// above it, its <c>... N more</c> the last N frames of the error it caused, which the JVM leaves out where they are the
/// same; a <c>Suppressed:</c> block a part of the record, its frames no one's. A logged <c>package.Type: message</c> line
/// is decided by the line after it alone - a frame or a <c>Caused by:</c> make it a thrown error's, any other line gives it
/// back - so the reading never holds more than that one line.
/// </summary>
/// <remarks>
/// A JVM frame names its source file by its name alone (<c>Runner.java</c>), never by a path: a name is no place in any
/// tree, so a frame names no file and is in no tree - its line and its symbol, with the class's package, say where it is.
/// </remarks>
internal sealed class JvmStackParser : IBuiltInParser
{
    private static readonly Regex InThread = LinearRegex.Compile(@"^Exception in thread ""[^""]*"" (?<type>[\w.$]+)(?:: (?<message>.*))?$");

    private static readonly Regex Thrown = LinearRegex.Compile(@"^(?<type>(?:[a-z_][\w$]*\.)+[A-Z][\w$]*(?:Exception|Error|Throwable))(?:: (?<message>.*))?$");

    private static readonly Regex Frame = LinearRegex.Compile(@"^\s+at (?:(?<prefix>[^\s(]*)/)?(?<symbol>[^\s/(]+)\((?<location>[^()]*)\)$");

    private static readonly Regex Place = LinearRegex.Compile(@"^[^:]+\.\w+(?::(?<line>\d+))?$");

    private static readonly Regex CausedBy = LinearRegex.Compile(@"^Caused by: (?<type>[\w.$]+)(?:: (?<message>.*))?$");

    private static readonly Regex More = LinearRegex.Compile(@"^\s+\.\.\. (?<more>\d+) (?:more|common frames omitted)$");

    private static readonly Regex Suppressed = LinearRegex.Compile(@"^(?<indent>\s+)Suppressed: .+$");

    public string Name => "jvm-stack";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private readonly List<(string Type, List<string> Message, List<StackFrame> Frames)> _levels = [];
        private bool _crashed;
        private bool _framed;
        private int _from;
        private int _to;
        private int? _suppressed;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_levels.Count > 0)
            {
                if (_suppressed is { } outer)
                {
                    if (text.Length - text.TrimStart().Length > outer && text.Trim().Length > 0)
                    {
                        return Framed(line);
                    }

                    _suppressed = null;
                }

                if (Frame.Match(text) is { Success: true } frame)
                {
                    var place = Place.Match(frame.Groups["location"].Value);

                    _levels[^1].Frames.Add(new StackFrame(
                        text.Trim(),
                        Line: place.Success ? ParsedText.Whole(place.Groups["line"]) : null,
                        Module: frame.Groups["prefix"].Value.Trim('/') is { Length: > 0 } module ? module : null,
                        Symbol: frame.Groups["symbol"].Value));
                    return Framed(line);
                }

                if (CausedBy.Match(text) is { Success: true } cause)
                {
                    _levels.Add((cause.Groups["type"].Value, [cause.Groups["message"].Value], []));
                    return Framed(line);
                }

                if (_framed && More.Match(text) is { Success: true } more)
                {
                    if (_levels.Count > 1 && ParsedText.Whole(more.Groups["more"]) is { } shared)
                    {
                        var caused = _levels[^2].Frames;
                        _levels[^1].Frames.AddRange(caused.Skip(Math.Max(caused.Count - shared, 0)));
                    }

                    return Framed(line);
                }

                if (_framed && Suppressed.Match(text) is { Success: true } suppressed)
                {
                    _suppressed = suppressed.Groups["indent"].Length;
                    return Framed(line);
                }

                if (_crashed && !_framed && text.Trim().Length > 0 && !Thrown.IsMatch(text))
                {
                    // The message's own further lines, which the JVM prints under the line that says the error ended a
                    // thread, before its first frame.
                    _levels[^1].Message.Add(text);
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (!_framed && !_crashed)
                {
                    // A logged head is a thrown error's only where the JVM's own next line follows it: this one says it was
                    // not, and nothing is held past it.
                    _levels.Clear();
                    return LineTaken.Left;
                }

                Close(sink);
            }

            if (InThread.Match(text) is { Success: true } inThread)
            {
                Open(line, inThread, crashed: true);
                return LineTaken.Took;
            }

            if (Thrown.Match(text) is { Success: true } bare)
            {
                Open(line, bare, crashed: false);
                return LineTaken.Held;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            if (_levels.Count > 0 && !_framed && !_crashed)
            {
                _levels.Clear();
                return LineTaken.Left;
            }

            Close(sink);
            return LineTaken.Took;
        }

        private LineTaken Framed(NumberedLine line)
        {
            _framed = true;
            _to = line.Last;
            return LineTaken.Took;
        }

        private void Open(NumberedLine line, Match head, bool crashed)
        {
            _levels.Add((head.Groups["type"].Value, [head.Groups["message"].Value], []));
            _crashed = crashed;
            _framed = false;
            _suppressed = null;
            _from = line.Number;
            _to = line.Last;
        }

        private void Close(ResultSink sink)
        {
            if (_levels.Count == 0)
            {
                return;
            }

            ExceptionRecord? cause = null;

            for (var level = _levels.Count - 1; level >= 0; level--)
            {
                var (type, message, frames) = _levels[level];
                cause = new ExceptionRecord(type, ParsedText.Joined(message) ?? string.Empty, [.. frames], cause is null ? [] : [cause]);
            }

            sink.Add(new TestFailure(
                "jvm",
                ParsedText.Headed(cause!.Type, cause.Message),
                Message: cause.Message.Length > 0 ? cause.Message : null,
                Exception: cause,
                Outcome: _crashed ? "crashed" : "errored",
                Log: (_from, _to)));
            _levels.Clear();
            _suppressed = null;
        }
    }
}

/// <summary>
/// A Python traceback: <c>Traceback (most recent call last):</c>, its frames <c>File "path", line N, in f</c> - a
/// module's own <c>&lt;module&gt;</c> among them, a syntax error's with no function - each with its source and caret
/// lines, and the error line last, <c>Type: message</c> or the type alone where the error says no message; frames read
/// innermost first. Tracebacks chained by <c>During handling of the above exception</c> or <c>The above exception was
/// the direct cause</c> are one error: the last printed the one raised, each before it the cause of the one after. A
/// traceback no error line ends is given back. A chain is said as soon as the line after it says it is whole. Whether the
/// error raised ended its program is decided by how the program ended, never by a line printed after the traceback, which
/// may as well be another program's - a forge's, make's, a shell's: one whose traceback runs through a module's top level
/// (<c>&lt;module&gt;</c>), as the error that ends a script, <c>-m</c> or <c>-c</c> does, has <c>crashed</c> the program
/// where it ended with a code not 0 or how it ended is not known, and has <c>errored</c> where it ended 0, for a handler at
/// the top level caught and logged it and the program ran on; one that does not, caught and printed inside a function or a
/// thread's, has <c>errored</c>. How the program ended is known as the reading ends (<see cref="ResultSink.Exit"/>), so it
/// decides there the chains it said, holding none of them while it reads.
/// </summary>
internal sealed class PythonStackParser : IBuiltInParser
{
    /// <summary>The function Python names a module's top level by, in a frame: what an error that left the program ran through.</summary>
    private const string ModuleLevel = "<module>";

    private static readonly Regex Head = LinearRegex.Compile(@"^Traceback \(most recent call last\):$");

    private static readonly Regex Frame = LinearRegex.Compile(@"^  File ""(?<file>[^""]+)"", line (?<line>\d+)(?:, in (?<symbol>.+))?$");

    private static readonly Regex Source = LinearRegex.Compile(@"^(?:    .*|  \[Previous line repeated \d+ more times?\])$");

    private static readonly Regex Thrown = LinearRegex.Compile(@"^(?<type>[A-Za-z_][\w.]*)(?:: (?<message>.*))?$");

    private static readonly Regex Chained = LinearRegex.Compile(@"^(?:During handling of the above exception, another exception occurred:|The above exception was the direct cause of the following exception:)$");

    public string Name => "python-stack";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private readonly List<ExceptionRecord> _chain = [];
        private List<StackFrame>? _frames;
        private bool _chained;
        private bool _blank;
        private int _from;
        private int _to;

        /// <summary>The first of the chains this reading said since it began or last ended, by its sink's order: those its program's end decides.</summary>
        private int _undecided;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_frames is { } frames)
            {
                if (Frame.Match(text) is { Success: true } frame)
                {
                    frames.Add(new StackFrame(text.Trim(), frame.Groups["file"].Value, ParsedText.Whole(frame.Groups["line"]), Symbol: ParsedText.Text(frame.Groups["symbol"])));
                    return LineTaken.Held;
                }

                if (frames.Count > 0 && Source.IsMatch(text))
                {
                    return LineTaken.Held;
                }

                if (frames.Count > 0 && Thrown.Match(text) is { Success: true } thrown)
                {
                    frames.Reverse();
                    _chain.Add(new ExceptionRecord(thrown.Groups["type"].Value, thrown.Groups["message"].Value, [.. frames], []));
                    _frames = null;
                    _chained = false;
                    _to = line.Last;
                    return LineTaken.Took;
                }

                // A traceback no error line ended is given back, and the chain before it is whole.
                _frames = null;
                Close(sink);
                return LineTaken.Left;
            }

            if (_chain.Count > 0)
            {
                if (text.Trim().Length == 0 && !_blank)
                {
                    // The one blank line Python prints between the parts of a chain: the line after it says whether the
                    // chain goes on.
                    _blank = true;
                    return LineTaken.Held;
                }

                var blank = _blank;
                _blank = false;

                if (!_chained && Chained.IsMatch(text))
                {
                    _chained = true;
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (!_chained || !Head.IsMatch(text))
                {
                    // The chain is whole, and said: what this line is decides nothing of it.
                    Close(sink);

                    if (blank)
                    {
                        // The blank line was no part of the chain: it is given back, and this line offered again on its own.
                        return LineTaken.Left;
                    }
                }
            }

            if (Head.IsMatch(text))
            {
                if (_chain.Count == 0)
                {
                    _from = line.Number;
                }

                _frames = [];
                return LineTaken.Held;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            _frames = null;
            Close(sink);

            // The program ended 0: an error through its top level that it printed was caught and logged by a handler
            // there, and the program ran on.
            if (sink.Exit == 0)
            {
                sink.Decide(_undecided, said => said.Outcome == "crashed" ? said with { Outcome = "errored" } : said);
            }

            _undecided = sink.FailuresSaid;
            return LineTaken.Left;
        }

        /// <summary>
        /// Says the chain read: <c>crashed</c> where the error it raised ran through a module's top level, until the
        /// program's end says otherwise (<see cref="End"/>), and <c>errored</c> where it did not - caught and printed
        /// inside a function, a thread's.
        /// </summary>
        private void Close(ResultSink sink)
        {
            _chained = false;
            _blank = false;

            if (_chain.Count == 0)
            {
                return;
            }

            ExceptionRecord? cause = null;

            foreach (var part in _chain)
            {
                cause = part with { CausedBy = cause is null ? [] : [cause] };
            }

            sink.Add(new TestFailure(
                "python",
                ParsedText.Headed(cause!.Type, cause.Message),
                Message: cause.Message.Length > 0 ? cause.Message : null,
                Exception: cause,
                Outcome: cause.Stack.Any(frame => frame.Symbol == ModuleLevel) ? "crashed" : "errored",
                Log: (_from, _to)));
            _chain.Clear();
        }
    }
}

/// <summary>
/// V8's stack trace, Node's and the browsers': the error line - <c>TypeError: message</c>, with Node's code as in
/// <c>TypeError [ERR_INVALID_ARG_VALUE]: message</c> - then its frames: <c>at f (path:line:col)</c>, <c>at new C
/// (...)</c>, <c>at async f (...)</c> (a frame of kind <c>async</c>, its call awaited) and <c>at path:line:col</c>. The
/// properties Node prints after it in braces, the line that says which frames it shares with its cause, and its
/// <c>Node.js vN</c> line, are part of the record; the error's cause among those properties - <c>[cause]: Type: message</c>
/// and the frames under it, a cause of its own printed inside its braces, indented further - is the record's cause, each
/// nested one the cause of the one it is printed inside. An error that ended Node is printed under its header - where it was
/// thrown, the source line, a caret under it and a blank line - which says the line after it is that error's head: its
/// message's further lines are read up to its first frame, and it has <c>crashed</c> the program. A logged error line,
/// with no header above it, has <c>errored</c>; it is decided by the line after it alone - a frame makes it a stack trace,
/// any other line gives it back - so the reading never holds more than that one line.
/// </summary>
internal sealed class V8StackParser : IBuiltInParser
{
    private static readonly Regex Thrown = LinearRegex.Compile(@"^(?:Uncaught )?(?<type>(?:[A-Z][\w$]*)?(?:Error|Exception))(?: \[(?<code>[A-Z][A-Z0-9_]*)\])?: (?<message>.*)$");

    private static readonly Regex Caret = LinearRegex.Compile(@"^\s*\^+\s*$");

    private static readonly Regex SharedWithCause = LinearRegex.Compile(@"^\s+\.\.\. \d+ lines? matching cause stack trace \.\.\.$");

    private static readonly Regex Cause = LinearRegex.Compile(@"^(?<indent> +)\[cause\]: (?<thrown>.+)$");

    private static readonly Regex Called = LinearRegex.Compile(@"^\s+at (?<async>async )?(?<new>new )?(?<symbol>.+?) \((?<location>.+)\)$");

    private static readonly Regex Anonymous = LinearRegex.Compile(@"^\s+at (?<async>async )?(?<location>[^\s()]+)$");

    private static readonly Regex Place = LinearRegex.Compile(@"^(?<file>.+):(?<line>\d+):(?<col>\d+)$");

    private static readonly Regex Trailer = LinearRegex.Compile(@"^Node\.js v\d+(?:\.\d+)*$");

    public string Name => "v8-stack";

    public IResultReading Begin() => new Reading();

    /// <summary>The V8 frame <paramref name="text"/> is; null where it is none.</summary>
    /// <param name="text">The line as printed.</param>
    internal static StackFrame? FrameOf(string text)
    {
        var called = Called.Match(text);
        var found = called.Success ? called : Anonymous.Match(text);

        if (!found.Success)
        {
            return null;
        }

        var place = Place.Match(found.Groups["location"].Value);
        var file = place.Success ? place.Groups["file"].Value : null;

        return new StackFrame(
            text.Trim(),
            ParsedText.FileOf(file),
            place.Success ? ParsedText.Whole(place.Groups["line"]) : null,
            place.Success ? ParsedText.Whole(place.Groups["col"]) : null,
            ParsedText.ModuleOf(file),
            called.Success ? $"{called.Groups["new"].Value}{called.Groups["symbol"].Value}" : null,
            found.Groups["async"].Success ? "async" : "call");
    }

    private sealed class Reading : IResultReading
    {
        private string? _type;
        private string? _code;
        private readonly List<string> _message = [];
        private readonly List<StackFrame> _frames = [];
        private readonly List<(int Indent, string Type, string Message, List<StackFrame> Frames)> _causes = [];
        private int _from;
        private int _to;
        private bool _uncaught;
        private bool _properties;
        private bool _closed;
        private int? _caret;
        private int? _headAt;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_type is not null)
            {
                if (_properties)
                {
                    _properties = text != "}";
                    _to = line.Last;
                    Property(text);
                    return LineTaken.Took;
                }

                // Node prints the error's own properties in braces, opened at the end of its last frame.
                var opens = text.EndsWith(" {", StringComparison.Ordinal);

                if (FrameOf(opens ? text[..^2] : text) is { } frame)
                {
                    _frames.Add(frame);
                    _properties = opens;
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (_frames.Count > 0 && SharedWithCause.IsMatch(text))
                {
                    // Node's word for the frames the error shares with its cause, which it prints under the cause.
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (_frames.Count > 0 || (_uncaught && Trailer.IsMatch(text)))
                {
                    Close(sink);
                }
                else if (_uncaught)
                {
                    // The message's own further lines, which Node prints under the head of the error that ended it, before
                    // its first frame.
                    _message.Add(text);
                    _to = line.Last;
                    return LineTaken.Took;
                }
                else
                {
                    // A logged head is a stack trace's only where a frame follows it: this line says it was not, and
                    // nothing is held past it.
                    Reset();
                    return LineTaken.Left;
                }
            }

            if (_closed)
            {
                if (Trailer.IsMatch(text))
                {
                    _closed = false;
                    return LineTaken.Took;
                }

                _closed = text.Trim().Length == 0;
            }

            // Node's header above the error that ended it - where it was thrown, its source line, a caret under it and a
            // blank line - says the line after it is that error's head.
            if (Caret.IsMatch(text))
            {
                _caret = line.Last;
            }
            else if (text.Trim().Length == 0 && _caret == line.Number - 1)
            {
                _headAt = line.Last + 1;
            }

            if (Thrown.Match(text) is { Success: true } thrown)
            {
                _type = thrown.Groups["type"].Value;
                _code = ParsedText.Text(thrown.Groups["code"]);
                _message.Add(thrown.Groups["message"].Value);
                _from = line.Number;
                _to = line.Last;
                _closed = false;
                _uncaught = line.Number == _headAt;
                return _uncaught ? LineTaken.Took : LineTaken.Held;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            _closed = false;

            if (_type is not null && _frames.Count == 0 && !_uncaught)
            {
                Reset();
                return LineTaken.Left;
            }

            Close(sink);
            return LineTaken.Took;
        }

        /// <summary>
        /// A line of the properties Node prints in braces after the error's frames: a cause's head opens it under the cause
        /// it is printed inside - the one less indented than it - and a frame is the innermost open cause's.
        /// </summary>
        private void Property(string text)
        {
            if (Cause.Match(text) is { Success: true } cause && Thrown.Match(cause.Groups["thrown"].Value) is { Success: true } thrown)
            {
                var indent = cause.Groups["indent"].Length;

                while (_causes.Count > 0 && _causes[^1].Indent >= indent)
                {
                    _causes.RemoveAt(_causes.Count - 1);
                }

                _causes.Add((indent, thrown.Groups["type"].Value, thrown.Groups["message"].Value, []));
                return;
            }

            if (_causes.Count > 0 && FrameOf(text.EndsWith(" {", StringComparison.Ordinal) ? text[..^2] : text) is { } frame)
            {
                _causes[^1].Frames.Add(frame);
            }
        }

        private void Close(ResultSink sink)
        {
            if (_type is { } type)
            {
                var message = ParsedText.Joined(_message) ?? string.Empty;
                ExceptionRecord? cause = null;

                for (var at = _causes.Count - 1; at >= 0; at--)
                {
                    cause = new ExceptionRecord(_causes[at].Type, _causes[at].Message, [.. _causes[at].Frames], cause is null ? [] : [cause]);
                }

                sink.Add(new TestFailure(
                    "node",
                    ParsedText.Headed(_code is null ? type : $"{type} [{_code}]", message),
                    Message: message.Length > 0 ? message : null,
                    Exception: new ExceptionRecord(type, message, [.. _frames], cause is null ? [] : [cause]),
                    Outcome: _uncaught ? "crashed" : "errored",
                    Log: (_from, _to)));
                _closed = true;
            }

            Reset();
        }

        private void Reset()
        {
            _type = null;
            _code = null;
            _message.Clear();
            _frames.Clear();
            _causes.Clear();
            _properties = false;
            _uncaught = false;
        }
    }
}

/// <summary>
/// Dart's stack trace: <c>Unhandled exception:</c>, the error's lines under it, and its frames - the VM's <c>#0 f
/// (location:line:col)</c> with <c>&lt;asynchronous suspension&gt;</c> between awaited ones, or the terse form
/// <c>package:stack_trace</c> prints (<see cref="StackForms.DartFrameOf"/>). The error is read by the one reading of a
/// Dart error (<see cref="StackForms.DartThrownOf"/>). An error that ended the isolate has <c>crashed</c> it.
/// </summary>
internal sealed class DartStackParser : IBuiltInParser
{
    public string Name => "dart-stack";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private List<string>? _error;
        private readonly List<StackFrame> _frames = [];
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_error is { } error)
            {
                if (StackForms.DartFrameOf(text) is { } frame)
                {
                    _frames.Add(frame);
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (_frames.Count == 0 && text.Trim().Length > 0)
                {
                    error.Add(text);
                    _to = line.Last;
                    return LineTaken.Took;
                }

                Close(sink);
            }

            if (text == "Unhandled exception:")
            {
                _error = [];
                _from = line.Number;
                _to = line.Last;
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            return LineTaken.Took;
        }

        private void Close(ResultSink sink)
        {
            if (_error is not { } error)
            {
                return;
            }

            var said = ParsedText.Joined(error) ?? string.Empty;
            var first = said.Split('\n')[0];
            var record = StackForms.DartThrownOf(error, [.. _frames]);

            sink.Add(new TestFailure(
                "dart",
                first.Length > 0 ? first : "Unhandled exception",
                Message: said.Length > 0 ? said : null,
                Exception: record,
                Stack: record is null ? [.. _frames] : null,
                Outcome: "crashed",
                Log: (_from, _to)));
            _error = null;
            _frames.Clear();
        }
    }
}

/// <summary>
/// Go's output: <c>go test</c>'s console - each <c>--- FAIL: TestName (0.00s)</c> with the lines its test logged under
/// it, the first naming its place as <c>file.go:12: message</c> - and, run verbose, the stream as it happens:
/// <c>=== RUN</c> naming the test, <c>=== CONT</c> and <c>=== NAME</c> naming it again as its output resumes
/// (<c>=== PAUSE</c> ends it for then), each test's indented logged lines under its <c>===</c> line, which its result line
/// says none of again. A test's result line is read at any indent - Go nests a subtest's under its parent's,
/// <c>    --- PASS: TestAdd/one</c>, where it prints them after the parent's - and is never a logged line: a
/// <c>--- FAIL</c> opens that test's failure, its message and place the first line it logged that names its place - the
/// stream's first, or the first under its result line where nothing streamed - and its log range from the first line it
/// logged through the last read of it, so every line it logged is one read away and nothing held grows with how many it
/// logs; a <c>--- PASS</c> or <c>--- SKIP</c> ends the test, and what was held of its stream is dropped. The failed tests
/// open at once are held by their indent, the innermost on top: a nested <c>--- FAIL</c> opens its subtest above its
/// parent, a line indented past a test's result line is that test's - a parent's own line printed after its subtest's
/// block among them, and a line of whitespace alone, as Go prints a message's empty line (testify's before its diff) -
/// and a line at or above it ends it, so each test is decided at its own end. A test whose subtest
/// failed and that logged nothing of its own by its end is said by its subtest, whichever order Go prints their results in.
/// What is held of a test's stream - where it begins and its first line that names its place - is held only while it
/// runs: dropped at its own result line, and at its package's result line (<c>ok</c>, <c>FAIL</c>, <c>?</c>), which ends
/// every test of the package - one whose binary was killed before it said its result among them - so no hold outlives
/// the test it is of. A package that did not build is said by its own block: the <c># pkg [variant]</c> line Go prints ahead of its
/// compiler's words, read whenever one comes and held for the line under it - a compiler's line <c>file.go:L:C:
/// message</c>, read as a diagnostic of the package whose head was the last, its project the package's import path -
/// and the <c>FAIL pkg [build failed]</c> line, which Go prints later and in the packages' order, said only where that
/// package's own block said nothing, so neither another package's block nor its failing tests silence it, and a block
/// that said why is never said again. A panic: <c>panic: message</c> (or the runtime's <c>fatal error: message</c>), the
/// signal line under it, and the panicking goroutine's stack - each frame a function line, <c>main.f(...)</c> or
/// <c>main.(*T).M(args)</c>, then its place, <c>\tpath:line +0x1d</c>, an inlined frame's with no offset; <c>created
/// by</c> a frame too. Every other goroutine's stack is part of the record; a panic inside a test has crashed that test.
/// A panic no goroutine follows is given back. <c>go test</c> prints a package's result after its output -
/// <c>FAIL\texample.com/mod/store\t0.01s</c> - so the failures read since the last such line are said with it, each
/// naming the package by its import path as its class: the place a test logged is printed relative to that package's
/// directory, which only the tree's module that owns the import path places
/// (<see cref="ResultDigest.WithInTree(DigestTree)"/>), and a failure no such line followed is said with no class. The
/// bare <c>FAIL</c> and <c>exit status N</c> say only what was said before them.
/// </summary>
internal sealed class GoPanicParser : IBuiltInParser
{
    /// <summary>The framework a failure Go's output said names: what places its file through its package (<see cref="ResultDigest.WithInTree(DigestTree)"/>).</summary>
    internal const string Framework = "go";

    /// <summary>A test's result line, at the indent Go nests it at: <c>--- FAIL: TestName (0.00s)</c>, <c>--- PASS</c>, <c>--- SKIP</c>.</summary>
    private static readonly Regex Result = LinearRegex.Compile(@"^(?<indent>\s*)--- (?<result>FAIL|PASS|SKIP): (?<name>\S+) \((?<seconds>\d+(?:\.\d+)?)s\)$");

    private static readonly Regex Logged = LinearRegex.Compile(@"^\s+(?<file>[^\s:]+\.go):(?<line>\d+): (?<message>.*)$");

    /// <summary>The stream's markers, as <c>go test -v</c> names the test whose output follows: a run begun, continued or named again.</summary>
    private static readonly Regex Streamed = LinearRegex.Compile(@"^=== (?:RUN|CONT|NAME)\s+(?<name>\S+)$");

    /// <summary>The stream's marker that pauses a parallel test: no output of its follows until it is named again.</summary>
    private static readonly Regex Paused = LinearRegex.Compile(@"^=== PAUSE\s+\S+$");

    /// <summary>A package's head, as Go prints it ahead of what its build said: <c># pkg</c>, the test variant in brackets where one is named.</summary>
    private static readonly Regex Built = LinearRegex.Compile(@"^# (?<package>\S+)(?: \[[^\]]+\])?$");

    /// <summary>A line of Go's compiler, under a package's head: <c>file.go:L:C: message</c>, a drive's path included.</summary>
    private static readonly Regex Compiled = LinearRegex.Compile(@"^(?<file>(?:[A-Za-z]:)?[^:\s]*\.go):(?<line>\d+):(?<col>\d+): (?<message>.+)$");

    /// <summary>
    /// A package's result line, as <c>go test</c> prints it after the package's output, a tab after the result:
    /// <c>ok  \tpkg\t0.01s</c>, <c>FAIL\tpkg\t0.01s</c>, <c>FAIL\tpkg [build failed]</c>, <c>?   \tpkg\t[no test files]</c>.
    /// </summary>
    private static readonly Regex Package = LinearRegex.Compile(@"^(?<result>FAIL|ok  |\?   )\t(?<package>\S+)(?:\t.*|\s+\[(?<reason>[^\]]+)\])$");

    private static readonly Regex Summary = LinearRegex.Compile(@"^(?:FAIL|exit status \d+)$");

    private static readonly Regex Panic = LinearRegex.Compile(@"^(?<kind>panic|fatal error): (?<message>.+?)(?: \[recovered\])?$");

    private static readonly Regex Nested = LinearRegex.Compile(@"^\tpanic: .+$");

    private static readonly Regex Signal = LinearRegex.Compile(@"^\[signal .+\]$");

    private static readonly Regex Goroutine = LinearRegex.Compile(@"^goroutine \d+ \[[^\]]+\]:$");

    private static readonly Regex Function = LinearRegex.Compile(@"^(?:created by (?<created>\S+)(?: in goroutine \d+)?|(?<symbol>\S+)\([^()]*\))$");

    private static readonly Regex Place = LinearRegex.Compile(@"^\t(?<file>.+):(?<line>\d+)(?: \+0x[0-9a-f]+)?$");

    public string Name => "go-panic";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        /// <summary>The tests whose result lines said they failed and that no line has ended yet, the innermost on top.</summary>
        private readonly Stack<Failing> _tests = new();
        private string? _kind;
        private readonly List<string> _message = [];
        private bool _blank;
        private bool _dumped;
        private int _goroutines;
        private (string Symbol, string Text)? _function;
        private readonly List<StackFrame> _frames = [];
        private readonly List<TestFailure> _package = [];
        private readonly HashSet<string> _failedParents = new(StringComparer.Ordinal);
        private int _from;
        private int _to;
        private string? _running;

        /// <summary>Of each test running, by its name, what is held of the lines it streamed: where they begin, and the first that names its place, as printed.</summary>
        private readonly Dictionary<string, (int From, string? Placed)> _streamed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _blocks = new(StringComparer.Ordinal);
        private (string Package, int Head, bool First, bool Decided)? _building;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_kind is not null && ReadPanic(line) is { } read)
            {
                return read;
            }

            // The line under a package's head says whether its compiler said anything: where it is no compiler's line, the
            // head is given back and this line offered again on its own. The head still names the block its package's
            // result line asks of.
            if (_building is { Decided: false } undecided && !Compiled.IsMatch(text))
            {
                _building = undecided with { Decided = true };
                return LineTaken.Left;
            }

            var result = Result.Match(text);
            var indent = text.Length - text.TrimStart().Length;

            if (!Panic.IsMatch(text))
            {
                // Each open test this line is not inside has ended - the line is at or above its result line's indent - the
                // innermost first, each decided at its own end; a panic is the innermost's. A line of whitespace alone is
                // measured as any other: Go indents every line of a test's block, a message's empty line - testify's
                // before its diff - printed as its indent, so one indented past a test's result line is still the test's.
                while (_tests.TryPeek(out var ended) && indent <= ended.Indent)
                {
                    CloseTest();
                }
            }

            if (_tests.TryPeek(out var test) && indent > test.Indent)
            {
                if (!result.Success)
                {
                    // A line the test logged, printed under its result line - before a subtest's block or after it: in its
                    // failure's log range.
                    Note(test, text);
                    test.To = line.Last;
                    return LineTaken.Took;
                }

                if (result.Groups["result"].Value != "FAIL")
                {
                    // A subtest that passed or was skipped, its result nested under its parent's: the end of the
                    // subtest, and no line its parent logged.
                    _streamed.Remove(result.Groups["name"].Value);
                    test.To = line.Last;
                    return LineTaken.Took;
                }

                // A subtest that failed, open above its parent until its own end: the parent is decided at its own end,
                // said by the subtest where it logged nothing of its own by then.
                test.SubtestFailed = true;
            }

            if (result.Success)
            {
                var name = result.Groups["name"].Value;
                (int From, string? Placed)? streamed = _streamed.Remove(name, out var held) ? held : null;

                _running = null;

                if (result.Groups["result"].Value != "FAIL")
                {
                    // The test ended: what it streamed is no failure's.
                    return LineTaken.Took;
                }

                // A test whose subtest failed, its result printed after the subtest's, that logged nothing of its own is
                // said by its subtest.
                if (streamed is null && _failedParents.Contains(name))
                {
                    return LineTaken.Took;
                }

                // The first line the stream logged under the test's === line that names its place is its failure's message
                // and place, and its log range runs from the first line it logged, so every line it logged is one read away.
                var failing = new Failing(
                    name,
                    result.Groups["indent"].Length,
                    double.TryParse(result.Groups["seconds"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ? seconds : null,
                    streamed?.From ?? line.Number,
                    line.Last);

                if (streamed is { } logged)
                {
                    failing.Logs = true;

                    if (logged.Placed is { } placed)
                    {
                        Note(failing, placed);
                    }
                }

                _tests.Push(failing);
                return LineTaken.Took;
            }

            if (Panic.Match(text) is { Success: true } panic)
            {
                _kind = panic.Groups["kind"].Value;
                _message.Add(panic.Groups["message"].Value);
                _dumped = false;
                _goroutines = 0;
                _from = _tests.TryPeek(out var panicking) ? panicking.From : line.Number;
                _to = line.Last;
                return LineTaken.Held;
            }

            if (Paused.IsMatch(text))
            {
                _running = null;
                return LineTaken.Took;
            }

            if (Streamed.Match(text) is { Success: true } running)
            {
                _running = running.Groups["name"].Value;
                return LineTaken.Took;
            }

            if (_running is { } streaming && text.Length - text.TrimStart().Length > 0 && text.Trim().Length > 0)
            {
                // The stream logs a test's own lines as it runs, under the === line that named it: of them only where they
                // begin and the first that names its place are held until its result line ends it - however many it logs,
                // the rest are one read away in its failure's log range.
                if (!_streamed.TryGetValue(streaming, out var logged))
                {
                    _streamed[streaming] = logged = (line.Number, null);
                }

                if (logged.Placed is null && Logged.IsMatch(text))
                {
                    _streamed[streaming] = logged with { Placed = text };
                }

                return LineTaken.Took;
            }

            if (Built.Match(text) is { Success: true } built)
            {
                var head = built.Groups["package"].Value;

                _blocks.TryAdd(head, false);
                _building = (head, line.Number, First: true, Decided: false);
                return LineTaken.Held;
            }

            if (_building is { } building && Compiled.Match(text) is { Success: true } compiled)
            {
                sink.Add(new Diagnostic(
                    Framework,
                    "error",
                    null,
                    compiled.Groups["message"].Value,
                    compiled.Groups["file"].Value,
                    ParsedText.Whole(compiled.Groups["line"]),
                    ParsedText.Whole(compiled.Groups["col"]),
                    Project: building.Package,
                    Log: (building.First ? building.Head : line.Number, line.Last)));
                _blocks[building.Package] = true;
                _building = building with { First = false, Decided = true };
                return LineTaken.Took;
            }

            if (Package.Match(text) is { Success: true } package)
            {
                var name = package.Groups["package"].Value;

                Said(sink, name);
                EndPackage();

                // A package that did not build is said where its own block said nothing of why - never where another
                // package's block or test said plenty.
                var blockSaid = _blocks.Remove(name, out var said) && said;

                if (package.Groups["reason"].Success && !blockSaid)
                {
                    sink.Add(new TestFailure(Framework, name, Message: package.Groups["reason"].Value, Outcome: "errored", Log: (line.Number, line.Last)));
                }

                if (_building?.Package == name)
                {
                    _building = null;
                }

                return LineTaken.Took;
            }

            return Summary.IsMatch(text) && sink.Said + _package.Count > 0 ? LineTaken.Took : LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            var given = _kind is not null && !_dumped;

            if (given)
            {
                ResetPanic();
            }
            else
            {
                ClosePanic();
            }

            CloseTests();
            Said(sink, null);
            EndPackage();
            _blocks.Clear();
            _building = null;
            return given ? LineTaken.Left : LineTaken.Took;
        }

        /// <summary>
        /// Says the failures read since the last package's result line, each naming <paramref name="package"/> - the import
        /// path that line names - as its class; none where the output ended with no such line.
        /// </summary>
        private void Said(ResultSink sink, string? package)
        {
            foreach (var failure in _package)
            {
                sink.Add(failure with { Class = package });
            }

            _package.Clear();
            _failedParents.Clear();
        }

        /// <summary>A package's tests ended, whether or not each said its result: what they streamed is dropped, and none streams on.</summary>
        private void EndPackage()
        {
            _streamed.Clear();
            _running = null;
        }

        /// <summary>
        /// Holds <paramref name="failure"/> until its package's result line names its class, and notes every test above
        /// the one it names - <c>A</c> and <c>A/b</c> of <c>A/b/c</c> - as one whose subtest failed.
        /// </summary>
        private void Pending(TestFailure failure, string? test)
        {
            _package.Add(failure);

            for (var slash = test?.IndexOf('/') ?? -1; slash > 0; slash = test!.IndexOf('/', slash + 1))
            {
                _failedParents.Add(test![..slash]);
            }
        }

        /// <summary>What the open panic made of the next line; null where the panic ended before it.</summary>
        private LineTaken? ReadPanic(NumberedLine line)
        {
            var text = line.Text;

            if (Goroutine.IsMatch(text))
            {
                _dumped = true;
                _goroutines++;
                _to = line.Last;
                return LineTaken.Took;
            }

            if (!_dumped)
            {
                if (!_blank && (Nested.IsMatch(text) || Signal.IsMatch(text)))
                {
                    _message.Add(text.Trim());
                    _to = line.Last;
                    return LineTaken.Held;
                }

                if (!_blank && text.Trim().Length == 0)
                {
                    // The one blank line Go prints between a panic's lines and the goroutine that panicked.
                    _blank = true;
                    return LineTaken.Held;
                }

                // A panic is said with the goroutines it dumps: none followed, so the lines were no panic's.
                ResetPanic();
                return LineTaken.Left;
            }

            if (Function.Match(text) is { Success: true } function)
            {
                _function = (ParsedText.Text(function.Groups["symbol"]) ?? function.Groups["created"].Value, text.Trim());
                _to = line.Last;
                return LineTaken.Took;
            }

            if (_function is { } called && Place.Match(text) is { Success: true } place)
            {
                if (_goroutines == 1)
                {
                    _frames.Add(new StackFrame($"{called.Text} {text.Trim()}", place.Groups["file"].Value, ParsedText.Whole(place.Groups["line"]), Symbol: called.Symbol));
                }

                _function = null;
                _to = line.Last;
                return LineTaken.Took;
            }

            if (text.Trim().Length == 0)
            {
                return LineTaken.Took;
            }

            ClosePanic();
            return null;
        }

        /// <summary>
        /// Notes a line <paramref name="test"/> logged, as printed: the first that names its place is its failure's message
        /// and place, and every other is one read away in its log range, never held.
        /// </summary>
        private static void Note(Failing test, string text)
        {
            test.Logs = true;

            if (test.Placed is null && Logged.Match(text) is { Success: true } placed)
            {
                test.Placed = text.Trim();
                test.File = placed.Groups["file"].Value;
                test.Line = ParsedText.Whole(placed.Groups["line"]);
            }
        }

        /// <summary>
        /// The innermost open test has ended: said with what it logged - or, where it logged nothing of its own and a subtest
        /// of it failed, said by that subtest.
        /// </summary>
        private void CloseTest()
        {
            var test = _tests.Pop();

            if (!test.Logs && test.SubtestFailed)
            {
                return;
            }

            Pending(
                new TestFailure(
                    Framework,
                    test.Name,
                    Message: test.Placed,
                    File: test.File,
                    Line: test.Line,
                    Duration: test.Seconds,
                    Log: (test.From, test.To)),
                test.Name);
        }

        /// <summary>Every open test has ended, the innermost first.</summary>
        private void CloseTests()
        {
            while (_tests.Count > 0)
            {
                CloseTest();
            }
        }

        /// <summary>
        /// Says the open panic, as the failure of the innermost open test where one is - which it ended, and with it every
        /// test still open around it.
        /// </summary>
        private void ClosePanic()
        {
            if (_kind is { } kind)
            {
                var message = ParsedText.Joined(_message) ?? string.Empty;
                var panicked = _tests.TryPop(out var test) ? test : null;

                Pending(
                    new TestFailure(
                        Framework,
                        panicked?.Name ?? ParsedText.Headed(kind, _message[0]),
                        Message: message,
                        Exception: new ExceptionRecord(kind, message, [.. _frames], []),
                        Duration: panicked?.Seconds,
                        Outcome: "crashed",
                        Log: (_from, _to)),
                    panicked?.Name);
                CloseTests();
            }

            ResetPanic();
        }

        private void ResetPanic()
        {
            _kind = null;
            _message.Clear();
            _frames.Clear();
            _function = null;
            _dumped = false;
            _blank = false;
        }
    }

    /// <summary>
    /// A test whose result line said it failed, open until a line at or above that line's indent ends it: what is held of
    /// it - where its lines run, whether it logged one of its own, the first that names its place, and whether a subtest
    /// of it failed - and never the lines themselves.
    /// </summary>
    /// <param name="name">The test's name, as its result line says it.</param>
    /// <param name="indent">The indent its result line is printed at.</param>
    /// <param name="seconds">How long it ran, as its result line says.</param>
    /// <param name="from">The first line of its log range: the first it streamed, or its result line.</param>
    /// <param name="to">The last line of its log range so far.</param>
    private sealed class Failing(string name, int indent, double? seconds, int from, int to)
    {
        public string Name { get; } = name;

        public int Indent { get; } = indent;

        public double? Seconds { get; } = seconds;

        public int From { get; } = from;

        public int To { get; set; } = to;

        /// <summary>Whether it logged a line of its own.</summary>
        public bool Logs { get; set; }

        /// <summary>The first line it logged that names its place, trimmed: its failure's message.</summary>
        public string? Placed { get; set; }

        public string? File { get; set; }

        public int? Line { get; set; }

        /// <summary>Whether a subtest of it failed, its result nested under this one's.</summary>
        public bool SubtestFailed { get; set; }
    }
}

/// <summary>
/// Rust's output: a panic, <c>thread 'name' panicked at path:line:col:</c> with its message on the lines under it (and the
/// form before Rust 1.73, <c>panicked at 'message', path:line:col</c>), its <c>note:</c> lines, and the backtrace under
/// <c>stack backtrace:</c> - each frame <c>N: symbol</c> with its place on the <c>at path:line:col</c> line under it; and
/// <c>cargo test</c>'s console around it. <c>cargo test</c> names each test's thread for its test, so a panic on a thread
/// other than <c>main</c> has failed that test, and one on <c>main</c> has crashed the program. <c>test name ... FAILED</c>,
/// the <c>failures:</c> list and <c>test result: FAILED</c> say only what the panics said, and a failed test no panic
/// said is said from them; <c>error: test failed</c> is said only where nothing before it said why.
/// </summary>
internal sealed class RustPanicParser : IBuiltInParser
{
    private static readonly Regex Panicked = LinearRegex.Compile(@"^thread '(?<thread>[^']*)' panicked at (?<file>.+?):(?<line>\d+):(?<col>\d+):$");

    private static readonly Regex PanickedBefore = LinearRegex.Compile(@"^thread '(?<thread>[^']*)' panicked at '(?<message>.*)', (?<file>.+?):(?<line>\d+):(?<col>\d+)$");

    private static readonly Regex Note = LinearRegex.Compile(@"^(?:note: .+|stack backtrace:)$");

    private static readonly Regex Frame = LinearRegex.Compile(@"^\s+\d+: (?:0x[0-9a-f]+ - )?(?<symbol>.+)$");

    private static readonly Regex Place = LinearRegex.Compile(@"^\s+at (?<file>.+?):(?<line>\d+)(?::(?<col>\d+))?$");

    private static readonly Regex Outcome = LinearRegex.Compile(@"^test (?<name>\S+) \.\.\. FAILED$");

    private static readonly Regex Listed = LinearRegex.Compile(@"^    (?<name>\S+)$");

    private static readonly Regex Result = LinearRegex.Compile(@"^test result: FAILED\. .+$");

    private static readonly Regex CargoFailed = LinearRegex.Compile(@"^error: (?<message>test failed, to rerun pass .+|\d+ targets? failed:)$");

    public string Name => "rust-panic";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private Match? _head;
        private readonly List<string> _message = [];
        private readonly List<StackFrame> _frames = [];
        private bool _noted;
        private int _from;
        private int _to;
        private readonly Dictionary<string, int> _failed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _said = new(StringComparer.Ordinal);
        private bool _listing;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_head is not null)
            {
                if (Note.IsMatch(text))
                {
                    _noted = true;
                    return Took(line);
                }

                if (_noted && Frame.Match(text) is { Success: true } frame)
                {
                    _frames.Add(new StackFrame(text.Trim(), Symbol: frame.Groups["symbol"].Value.Trim()));
                    return Took(line);
                }

                if (_noted && _frames.Count > 0 && Place.Match(text) is { Success: true } place)
                {
                    _frames[^1] = _frames[^1] with
                    {
                        Text = $"{_frames[^1].Text} {text.Trim()}",
                        File = place.Groups["file"].Value,
                        Line = ParsedText.Whole(place.Groups["line"]),
                        Column = ParsedText.Whole(place.Groups["col"]),
                    };
                    return Took(line);
                }

                if (!_noted && text.Trim().Length > 0)
                {
                    _message.Add(text);
                    return Took(line);
                }

                ClosePanic(sink);
            }

            if (Panicked.Match(text) is { Success: true } panicked)
            {
                OpenPanic(line, panicked);
                return LineTaken.Took;
            }

            if (PanickedBefore.Match(text) is { Success: true } before)
            {
                OpenPanic(line, before);
                _message.Add(before.Groups["message"].Value);
                return LineTaken.Took;
            }

            if (Outcome.Match(text) is { Success: true } outcome)
            {
                _failed.TryAdd(outcome.Groups["name"].Value, line.Number);
                return LineTaken.Took;
            }

            if (text == "failures:")
            {
                _listing = true;
                return LineTaken.Took;
            }

            if (_listing && Listed.Match(text) is { Success: true } listed && (_failed.ContainsKey(listed.Groups["name"].Value) || _said.Contains(listed.Groups["name"].Value)))
            {
                return LineTaken.Took;
            }

            _listing &= text.Trim().Length == 0;

            if (Result.IsMatch(text))
            {
                SayUnsaid(sink);
                return LineTaken.Took;
            }

            if (CargoFailed.Match(text) is { Success: true } cargo)
            {
                SayUnsaid(sink);

                if (sink.Said == 0)
                {
                    sink.Add(new Diagnostic("cargo", "error", null, cargo.Groups["message"].Value, Log: (line.Number, line.Last)));
                }

                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            ClosePanic(sink);
            SayUnsaid(sink);
            _listing = false;
            return LineTaken.Took;
        }

        private LineTaken Took(NumberedLine line)
        {
            _to = line.Last;
            return LineTaken.Took;
        }

        private void OpenPanic(NumberedLine line, Match head)
        {
            _head = head;
            _noted = false;
            _from = line.Number;
            _to = line.Last;
        }

        private void ClosePanic(ResultSink sink)
        {
            if (_head is not { } head)
            {
                return;
            }

            var thread = head.Groups["thread"].Value;
            var message = ParsedText.Joined(_message) ?? string.Empty;
            var test = thread is "main" or "<unnamed>" ? null : thread;
            var file = head.Groups["file"].Value;

            sink.Add(new TestFailure(
                "rust",
                test ?? $"panicked at {file}:{head.Groups["line"].Value}:{head.Groups["col"].Value}",
                Message: message.Length > 0 ? message : null,
                File: file,
                Line: ParsedText.Whole(head.Groups["line"]),
                Exception: new ExceptionRecord("panic", message, [.. _frames], []),
                Outcome: test is null ? "crashed" : "failed",
                Log: (_from, _to)));

            if (test is not null)
            {
                _said.Add(test);
            }

            _head = null;
            _message.Clear();
            _frames.Clear();
        }

        private void SayUnsaid(ResultSink sink)
        {
            foreach (var (name, at) in _failed.Where(failed => !_said.Contains(failed.Key)).OrderBy(failed => failed.Value).ToList())
            {
                sink.Add(new TestFailure("rust", name, Log: (at, at)));
                _said.Add(name);
            }

            _failed.Clear();
        }
    }
}

/// <summary>
/// The sanitizers' reports: AddressSanitizer's and LeakSanitizer's <c>==pid==ERROR: KindSanitizer: message</c>,
/// MemorySanitizer's <c>==pid==WARNING: MemorySanitizer: message</c> and ThreadSanitizer's <c>WARNING: ThreadSanitizer:
/// message</c>, read over the report's own lines - its frames, blank and indented lines, the runtime's <c>==pid==</c>
/// lines, the sentences between its stacks (<c>READ of size</c>, <c>0x... is located</c>, <c>freed by thread T0 here:</c>,
/// <c>Thread T1 created by</c>, a leak's, <c>HINT:</c>), the registers' values, its <c>SUMMARY:</c> and the shadow bytes
/// under it - to the line the sanitizer halts the program with, or to the first line that is none of them, which ends it
/// and is offered again to every other reading, so a report printed with no summary and no halt line holds nothing of
/// what the program printed after it; and UndefinedBehaviorSanitizer's <c>path:line:col: runtime error: message</c>,
/// with its stack where one was asked for and its <c>SUMMARY:</c>. A report's head ends the report open before it. The
/// report's first stack is its stack - each frame <c>#N 0x... in symbol
/// path:line[:col]</c>, <c>(module+0x...)</c> where only the binary is known - and the stacks after it (where the memory
/// was allocated, freed, last written, where its value was made) are part of the record. A report the sanitizer ended
/// the program over - closed by the line it halts with, AddressSanitizer's <c>==pid==ABORTING</c>, MemorySanitizer's
/// <c>Exiting</c>, the last line of its report - has <c>crashed</c> it; every other - one the program recovered from and ran
/// on, a warning, a runtime error UBSan printed no halt line for - has <c>errored</c> it. A report that ended the program
/// inside a test a runner said began is that test's failure (<see cref="ResultSink.AddCrash"/>), and so is the last one
/// said inside a test its runner never saw end, for the program died in it (<see cref="ResultSink.Died"/>).
/// </summary>
internal sealed class SanitizerParser : IBuiltInParser
{
    private static readonly Regex Error = LinearRegex.Compile(@"^==\d+==ERROR: (?<kind>\w+Sanitizer): (?<message>.+)$");

    private static readonly Regex Warning = LinearRegex.Compile(@"^(?:==\d+==)?WARNING: (?<kind>ThreadSanitizer|MemorySanitizer): (?<message>.+)$");

    private static readonly Regex Runtime = LinearRegex.Compile(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+): runtime error: (?<message>.+)$");

    private static readonly Regex Frame = LinearRegex.Compile(@"^\s+#\d+ (?:0x[0-9a-f]+(?: in)? )?(?<rest>.+)$");

    private static readonly Regex BuildId = LinearRegex.Compile(@"^(?<rest>.*?) \(BuildId: [0-9a-f]+\)$");

    private static readonly Regex Module = LinearRegex.Compile(@"^(?<rest>.*?) ?\((?<module>[^()]+)\+0x[0-9a-f]+\)$");

    private static readonly Regex Placed = LinearRegex.Compile(@"^(?<symbol>.+) (?<file>\S+?):(?<line>\d+)(?::(?<col>\d+))?$");

    private static readonly Regex Summary = LinearRegex.Compile(@"^SUMMARY: \w+Sanitizer: .+$");

    private static readonly Regex Aborting = LinearRegex.Compile(@"^==\d+==ABORTING$");

    /// <summary>
    /// The line MemorySanitizer halts the program with, the last of its report: under its <c>SUMMARY:</c>, or under its
    /// stacks where it was told to print no summary.
    /// </summary>
    private const string MemoryHalt = "Exiting";

    /// <summary>The shadow bytes a report prints around the address it names, their legend, and every indented line of it.</summary>
    private static readonly Regex Shadow = LinearRegex.Compile(@"^(?:Shadow byte.*|=>0x.*|\s+\S.*)$");

    /// <summary>A line the sanitizer's runtime prints under its own <c>==pid==</c> prefix: the signal's cause, a hint, the registers' head.</summary>
    private static readonly Regex Prefixed = LinearRegex.Compile(@"^==\d+==");

    /// <summary>
    /// The sentences a report prints at the start of a line between its stacks: the access, where the address lies, whose
    /// stack the next is - allocated, freed, a thread created - a leak's, a hint, and that no more is known.
    /// </summary>
    private static readonly Regex Sentence = LinearRegex.Compile(@"^(?:(?:READ|WRITE) of size \d+ at |(?:Address )?0x[0-9a-f]+ is |(?:previously allocated|allocated|freed) by thread T\d+ here:$|Thread T\d+ .*created by |(?:Direct|Indirect) leak of |HINT: |\w+Sanitizer can not provide additional info\.$)");

    /// <summary>A line of the registers' values a report prints on a deadly signal: <c>rax = 0x...  rbx = 0x...</c>.</summary>
    private static readonly Regex Registers = LinearRegex.Compile(@"^(?:\s*\w+ = 0x[0-9a-f]+)+\s*$");

    public string Name => "sanitizer";

    public IResultReading Begin() => new Reading();

    /// <summary>A sanitizer's frame: its symbol, its place where the symbolizer knew it, its binary where it knew only that.</summary>
    /// <param name="text">The line as printed.</param>
    internal static StackFrame? FrameOf(string text)
    {
        if (Frame.Match(text) is not { Success: true } frame)
        {
            return null;
        }

        var rest = frame.Groups["rest"].Value;
        rest = BuildId.Match(rest) is { Success: true } built ? built.Groups["rest"].Value : rest;

        string? module = null;

        if (Module.Match(rest) is { Success: true } inModule)
        {
            module = inModule.Groups["module"].Value;
            rest = inModule.Groups["rest"].Value;
        }

        return Placed.Match(rest) is { Success: true } placed
            ? new StackFrame(text.Trim(), placed.Groups["file"].Value, ParsedText.Whole(placed.Groups["line"]), ParsedText.Whole(placed.Groups["col"]), module, placed.Groups["symbol"].Value.Trim())
            : new StackFrame(text.Trim(), Module: module, Symbol: rest.Trim() is { Length: > 0 } symbol ? symbol : null);
    }

    private sealed class Reading : IResultReading
    {
        private string? _kind;
        private string? _message;
        private string? _file;
        private int? _line;
        private readonly List<StackFrame> _frames = [];
        private bool _stacked;
        private bool _summary;
        private bool _runtime;
        private bool _aborted;
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_kind is not null)
            {
                if (Halted(text))
                {
                    _aborted = true;
                    _to = line.Last;
                    Close(sink);
                    return LineTaken.Took;
                }

                if (!_summary && FrameOf(text) is { } frame)
                {
                    if (!_stacked)
                    {
                        _frames.Add(frame);
                    }

                    return Took(line);
                }

                _stacked |= _frames.Count > 0;

                if (!_summary && Summary.IsMatch(text))
                {
                    _summary = true;
                    _to = line.Last;

                    if (_runtime)
                    {
                        Close(sink);
                    }

                    return LineTaken.Took;
                }

                // A line of the report's own goes on with it - after its summary its shadow bytes, before it any line of the
                // report's forms - and any other ends it, offered again to every other reading: a report the program ran on
                // from, printing no summary and no halt line, holds none of the lines after it.
                if (!Heads(text) && (_summary ? Shadow.IsMatch(text) : !_runtime && Own(text)))
                {
                    return Took(line);
                }

                Close(sink);
            }

            if (Error.Match(text) is { Success: true } error)
            {
                Open(line, error.Groups["kind"].Value, error.Groups["message"].Value, runtime: false);
                return LineTaken.Took;
            }

            if (Warning.Match(text) is { Success: true } warning)
            {
                Open(line, warning.Groups["kind"].Value, warning.Groups["message"].Value, runtime: false);
                return LineTaken.Took;
            }

            if (Runtime.Match(text) is { Success: true } runtime)
            {
                Open(line, "UndefinedBehaviorSanitizer", runtime.Groups["message"].Value, runtime: true);
                _file = runtime.Groups["file"].Value;
                _line = ParsedText.Whole(runtime.Groups["line"]);
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            return LineTaken.Took;
        }

        private LineTaken Took(NumberedLine line)
        {
            _to = line.Last;
            return LineTaken.Took;
        }

        /// <summary>Whether a line is the one the open report's sanitizer halts the program with.</summary>
        private bool Halted(string text)
            => Aborting.IsMatch(text) || (_kind == "MemorySanitizer" && string.Equals(text, MemoryHalt, StringComparison.Ordinal));

        /// <summary>Whether a line heads a report of its own: the next report, never a line of the one open.</summary>
        private static bool Heads(string text) => Error.IsMatch(text) || Warning.IsMatch(text) || Runtime.IsMatch(text);

        /// <summary>
        /// Whether a line is one a report prints before its summary besides its frames: a blank one, an indented one - a
        /// thread's access, where a value was made, a frame's objects, the shadow bytes - one under the runtime's
        /// <c>==pid==</c> prefix, one of the sentences between its stacks, or the registers' values.
        /// </summary>
        private static bool Own(string text)
            => text.Trim().Length == 0 || Shadow.IsMatch(text) || Prefixed.IsMatch(text) || Sentence.IsMatch(text) || Registers.IsMatch(text);

        private void Open(NumberedLine line, string kind, string message, bool runtime)
        {
            _kind = kind;
            _message = message;
            _runtime = runtime;
            _stacked = false;
            _summary = false;
            _aborted = false;
            _file = null;
            _line = null;
            _from = line.Number;
            _to = line.Last;
        }

        private void Close(ResultSink sink)
        {
            if (_kind is { } kind)
            {
                sink.AddCrash(new TestFailure(
                    "sanitizer",
                    $"{kind}: {_message}",
                    Message: _message,
                    File: _file,
                    Line: _line,
                    Exception: new ExceptionRecord(kind, _message!, [.. _frames], []),
                    Outcome: _aborted ? "crashed" : "errored",
                    Log: (_from, _to)));
            }

            _kind = null;
            _frames.Clear();
        }
    }
}

/// <summary>
/// A native program's crash as a debugger or the C runtime says it: gdb's <c>Program received signal SIGSEGV,
/// Segmentation fault.</c> with the stop's place and source line under it and the <c>bt</c> it printed - each frame <c>#N
/// [0x... in] symbol (args) at path:line</c>, or <c>from library</c> where only the binary is known - and lldb's <c>*
/// thread #1, ..., stop reason = signal SIGSEGV: ...</c> with its <c>frame #N: 0x... module`symbol at file:line:col</c>
/// frames, the stop's own frame and its source listing taken as the record's and its <c>bt</c> the stack. The C runtime's
/// failed assertion - glibc's <c>program: file:line: function: Assertion `expr' failed.</c>, the Microsoft runtime's
/// <c>Assertion failed: expr, file path, line N</c> - is the crash's reason, and its place the failure's: said with the
/// debugger's report that follows it, or alone where none does. Every such crash has <c>crashed</c> the program, and one
/// read inside a test a runner said began is that test's failure (<see cref="ResultSink.AddCrash"/>).
/// </summary>
/// <remarks>
/// lldb names a frame's source file by its name alone, as the JVM does (<see cref="JvmStackParser"/>): such a frame names
/// no file, only its line, its column and its binary.
/// </remarks>
internal sealed class BacktraceParser : IBuiltInParser
{
    private static readonly Regex GlibcAssertion = LinearRegex.Compile(@"^[^\s:]+: (?<file>.+?):(?<line>\d+): (?<function>.+): Assertion `(?<expression>.+)' failed\.$");

    private static readonly Regex RuntimeAssertion = LinearRegex.Compile(@"^Assertion failed: (?<expression>.+), file (?<file>.+), line (?<line>\d+)$");

    private static readonly Regex GdbSignal = LinearRegex.Compile(@"^(?:Program|Thread \d+ ""[^""]*"") received signal (?<type>\w+), (?<message>.+?)\.?$");

    private static readonly Regex LldbStop = LinearRegex.Compile(@"^\* thread #\d+(?:, [^,]+)*?, stop reason = (?:signal (?<signal>\w+)(?:: (?<said>.+))?|(?<type>[A-Z_]+)(?: \((?<detail>.+)\))?|(?<other>.+))$");

    private static readonly Regex GdbFrame = LinearRegex.Compile(@"^#\d+ +(?:0x[0-9a-f]+ in )?(?<symbol>\S.*?) \((?<arguments>.*)\)(?: at (?<file>\S.*):(?<line>\d+)| from (?<module>\S+))?$");

    private static readonly Regex LldbFrame = LinearRegex.Compile(@"^\s+(?:\* )?frame #\d+: 0x[0-9a-f]+ (?<module>[^`\s]+)`(?<symbol>.+?)(?: at [^:\s]+:(?<line>\d+)(?::(?<col>\d+))?)?$");

    private static readonly Regex Stopped = LinearRegex.Compile(@"^(?:0x[0-9a-f]+ in .+|\S+ \(.*\) at .+:\d+|\d+\t.*|warning: \d+\t.+|\((?:gdb|lldb)\) .*|(?:->| ) +\d+(?: +\t.*)?|\s+\t.*)$");

    public string Name => "backtrace";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private string? _framework;
        private string? _type;
        private string? _message;
        private string? _assertion;
        private string? _file;
        private int? _line;
        private readonly List<StackFrame> _frames = [];
        private int _from;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_framework is not null || _assertion is not null)
            {
                if (Read(line) is { } taken)
                {
                    return taken;
                }

                Close(sink);
            }

            if (GlibcAssertion.Match(text) is { Success: true } glibc)
            {
                Assert(line, $"{glibc.Groups["function"].Value}: Assertion `{glibc.Groups["expression"].Value}' failed.", glibc);
                return LineTaken.Took;
            }

            if (RuntimeAssertion.Match(text) is { Success: true } runtime)
            {
                Assert(line, $"Assertion failed: {runtime.Groups["expression"].Value}", runtime);
                return LineTaken.Took;
            }

            return Opened(line) ? LineTaken.Took : LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            return LineTaken.Took;
        }

        /// <summary>What the open crash made of the next line; null where the crash ended before it.</summary>
        private LineTaken? Read(NumberedLine line)
        {
            var text = line.Text;

            if (_framework is null)
            {
                // A failed assertion waits for the debugger's word on the signal it raised, over blank lines.
                return text.Trim().Length == 0 ? LineTaken.Took : Opened(line) ? LineTaken.Took : null;
            }

            if (GdbFrame.Match(text) is { Success: true } gdb)
            {
                _frames.Add(new StackFrame(text.Trim(), ParsedText.Text(gdb.Groups["file"]), ParsedText.Whole(gdb.Groups["line"]), Module: ParsedText.Text(gdb.Groups["module"]), Symbol: gdb.Groups["symbol"].Value));
                return Took(line);
            }

            if (LldbFrame.Match(text) is { Success: true } lldb)
            {
                _frames.Add(new StackFrame(text.Trim(), Line: ParsedText.Whole(lldb.Groups["line"]), Column: ParsedText.Whole(lldb.Groups["col"]), Module: lldb.Groups["module"].Value, Symbol: lldb.Groups["symbol"].Value));
                return Took(line);
            }

            if (LldbStop.IsMatch(text))
            {
                // lldb's backtrace says the stop again over the whole stack: the stop's own frame was its first.
                _frames.Clear();
                return Took(line);
            }

            return Stopped.IsMatch(text) ? Took(line) : null;
        }

        private LineTaken Took(NumberedLine line)
        {
            _to = line.Last;
            return LineTaken.Took;
        }

        private void Assert(NumberedLine line, string assertion, Match found)
        {
            _assertion = assertion;
            _file = found.Groups["file"].Value;
            _line = ParsedText.Whole(found.Groups["line"]);
            _from = line.Number;
            _to = line.Last;
        }

        /// <summary>Whether <paramref name="line"/> is a debugger's stop: where it is, the crash is opened.</summary>
        private bool Opened(NumberedLine line)
        {
            var text = line.Text;

            if (GdbSignal.Match(text) is { Success: true } gdb)
            {
                Open(line, "gdb", gdb.Groups["type"].Value, gdb.Groups["message"].Value);
                return true;
            }

            if (LldbStop.Match(text) is not { Success: true } lldb)
            {
                return false;
            }

            if (lldb.Groups["signal"].Success)
            {
                Open(line, "lldb", lldb.Groups["signal"].Value, ParsedText.Text(lldb.Groups["said"]) ?? string.Empty);
            }
            else if (lldb.Groups["type"].Success)
            {
                Open(line, "lldb", lldb.Groups["type"].Value, ParsedText.Text(lldb.Groups["detail"]) ?? string.Empty);
            }
            else
            {
                Open(line, "lldb", "stop", lldb.Groups["other"].Value);
            }

            return true;
        }

        private void Open(NumberedLine line, string framework, string type, string message)
        {
            _from = _assertion is null ? line.Number : _from;
            _to = line.Last;
            _framework = framework;
            _type = type;
            _message = message;
            _frames.Clear();
        }

        private void Close(ResultSink sink)
        {
            if (_framework is { } framework)
            {
                var message = _assertion is { } assertion ? $"{_message}\n{assertion}".Trim() : _message!;

                sink.AddCrash(new TestFailure(
                    framework,
                    ParsedText.Headed(_type!, _message!),
                    Message: message.Length > 0 ? message : null,
                    File: _file,
                    Line: _line,
                    Exception: new ExceptionRecord(_type!, message, [.. _frames], []),
                    Outcome: "crashed",
                    Log: (_from, _to)));
            }
            else if (_assertion is { } assertion)
            {
                sink.AddCrash(new TestFailure("assert", assertion, Message: assertion, File: _file, Line: _line, Outcome: "crashed", Log: (_from, _to)));
            }

            _framework = null;
            _type = null;
            _message = null;
            _assertion = null;
            _file = null;
            _line = null;
            _frames.Clear();
        }
    }
}
