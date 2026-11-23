using System.Globalization;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Results;

/// <summary>
/// One parser of a tool's or a framework's output lines: it reads the lines a phase or a job printed and answers the
/// diagnostics and test failures it holds, with the lines it left unheld - never a guess, for what it does not
/// understand it says so by leaving the line. Found by scan, chosen by what the phase ran: the toolchain the leg
/// resolved, the test runner of the invocation, and the matchers the configuration declares.
/// </summary>
public interface IResultParser
{
    /// <summary>The parser's name, as a matcher or a runner names it.</summary>
    string Name { get; }

    /// <summary>Reads <paramref name="lines"/> into what it holds.</summary>
    /// <param name="lines">The output lines, in the order printed, each unmasked as the log held it.</param>
    /// <returns>The diagnostics and failures it read, and the lines it left unheld.</returns>
    (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines);
}

/// <summary>
/// The built-in line parsers, one owner of the forms the tools the tool runs print at the versions its CI runs: the
/// .NET and C compilers, the CMake and Ninja that drive them, the linkers, the TypeScript and Dart and ESLint
/// checkers, and the stack traces the runtimes print wherever a test or a process throws. Each parser is chosen for
/// the output it reads and never runs over another's.
/// </summary>
public static class ResultParsers
{
    /// <summary>Every built-in parser, by its name.</summary>
    public static readonly IReadOnlyList<IResultParser> BuiltIn =
    [
        new MsBuildParser(), new MsvcParser(), new GccParser(), new ClangParser(), new CMakeParser(), new NinjaParser(),
        new LinkerParser(), new TscParser(), new DartAnalyserParser(), new DotNetStackParser(), new JvmStackParser(),
        new PythonStackParser(), new V8StackParser(), new DartStackParser(), new GoPanicParser(), new RustPanicParser(),
        new SanitizerParser(), new XUnitConsoleParser(), new GTestConsoleParser(), new CTestConsoleParser(),
    ];

    /// <summary>The parser named <paramref name="name"/>, where one of the built-ins is; null where none is (a configured matcher's, read by the configuration).</summary>
    /// <param name="name">The parser's name.</param>
    public static IResultParser? Of(string name)
        => BuiltIn.FirstOrDefault(parser => string.Equals(parser.Name, name, StringComparison.Ordinal));
}

/// <summary>Reads MSBuild's and csc's form: <c>path(line,col): error CS0103: message [project]</c>, folding the same error printed once per target framework.</summary>
internal sealed class MsBuildParser : IResultParser
{
    private static readonly Regex Form = new(@"^(?<file>[^(\s][^(:]*?)\((?<line>\d+)(?:,(?<col>\d+)(?:-(?<endline>\d+),(?<endcol>\d+))?)?\):\s+(?<severity>error|warning)\s+(?<code>[A-Z0-9]+):\s+(?<message>.+?)(?:\s+\[(?<project>[^\]]+)\])?$", RegexOptions.Compiled);

    public string Name => "msbuild";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                diagnostics.Add(new(
                    "msbuild",
                    found.Groups["severity"].Value,
                    found.Groups["code"].Value,
                    found.Groups["message"].Value,
                    found.Groups["file"].Value,
                    int.Parse(found.Groups["line"].Value, CultureInfo.InvariantCulture),
                    found.Groups["col"].Success ? int.Parse(found.Groups["col"].Value, CultureInfo.InvariantCulture) : null,
                    found.Groups["endline"].Success ? int.Parse(found.Groups["endline"].Value, CultureInfo.InvariantCulture) : null,
                    found.Groups["endcol"].Success ? int.Parse(found.Groups["endcol"].Value, CultureInfo.InvariantCulture) : null,
                    Project: found.Groups["project"].Success ? found.Groups["project"].Value : null,
                    Log: (at + 1, at + 1)));
            }
        }

        return (diagnostics, [], UnparsedOf(lines, Form));
    }

    internal static IReadOnlyList<string> UnparsedOf(IReadOnlyList<string> lines, Regex form)
        => [.. lines.Where(line => LooksLikeError(line) && !form.IsMatch(line))];

    private static bool LooksLikeError(string line)
        => line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("warning", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Failed", StringComparison.Ordinal);
}

/// <summary>Reads MSVC's form: <c>path(line): error C2065: message</c>, and the linker's <c>LNK</c> errors beside it.</summary>
internal sealed class MsvcParser : IResultParser
{
    private static readonly Regex Form = new(@"^(?<file>[^(\s][^(:]*?)\((?<line>\d+)\):\s+(?<severity>error|warning|note)\s+(?<code>[A-Z]\d+):\s+(?<message>.*)$", RegexOptions.Compiled);

    public string Name => "msvc";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                diagnostics.Add(new(
                    "msvc",
                    found.Groups["severity"].Value,
                    found.Groups["code"].Value,
                    found.Groups["message"].Value,
                    found.Groups["file"].Value,
                    int.Parse(found.Groups["line"].Value, CultureInfo.InvariantCulture),
                    Log: (at + 1, at + 1)));
            }
        }

        return (diagnostics, [], MsBuildParser.UnparsedOf(lines, Form));
    }
}

/// <summary>Reads gcc's form: <c>path:line:col: error: message [-Wflag]</c>, with the include and macro chain as the error's stack.</summary>
internal sealed class GccParser : IResultParser
{
    private const string Chain = @"^(?:In file included from|from)\s+(?<file>.+?):(?<line>\d+)(?::(?<col>\d+))?:?,?\s*$";

    private static readonly Regex Form = new(@"^(?<file>[^:\s][^:]*?):(?<line>\d+):(?<col>\d+):\s+(?<severity>error|warning|note):\s+(?<message>.+?)(?:\s+\[(?<code>[-\w]+)\])?$", RegexOptions.Compiled);
    private static readonly Regex Included = new(Chain, RegexOptions.Compiled);

    public string Name => "gcc";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                // The include chain an error closes: the `In file included from` lines before it, innermost first.
                var stack = new List<StackFrame>();

                for (var back = at - 1; back >= 0 && stack.Count < StackTrace.FrameBound && Included.IsMatch(lines[back]); back--)
                {
                    var frame = Included.Match(lines[back]);
                    stack.Add(new StackFrame(lines[back], frame.Groups["file"].Value, int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture), frame.Groups["col"].Success ? int.Parse(frame.Groups["col"].Value, CultureInfo.InvariantCulture) : null, Kind: "include"));
                }

                diagnostics.Add(new(
                    "gcc",
                    found.Groups["severity"].Value,
                    found.Groups["code"].Success ? found.Groups["code"].Value : null,
                    found.Groups["message"].Value,
                    found.Groups["file"].Value,
                    int.Parse(found.Groups["line"].Value, CultureInfo.InvariantCulture),
                    int.Parse(found.Groups["col"].Value, CultureInfo.InvariantCulture),
                    Stack: stack,
                    Log: (at + 1, at + 1)));
            }
        }

        return (diagnostics, [], MsBuildParser.UnparsedOf(lines, Form));
    }
}

/// <summary>Reads clang's form: gcc's, with the instantiation and macro chain as the error's stack.</summary>
internal sealed class ClangParser : IResultParser
{
    private static readonly Regex Form = GccForm();

    private static Regex GccForm()
        => new(@"^(?<file>[^:\s][^:]*?):(?<line>\d+):(?<col>\d+):\s+(?<severity>error|warning|note):\s+(?<message>.+?)(?:\s+\[(?<code>[-\w]+)\])?$", RegexOptions.Compiled);

    public string Name => "clang";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                diagnostics.Add(new(
                    "clang",
                    found.Groups["severity"].Value,
                    found.Groups["code"].Success ? found.Groups["code"].Value : null,
                    found.Groups["message"].Value,
                    found.Groups["file"].Value,
                    int.Parse(found.Groups["line"].Value, CultureInfo.InvariantCulture),
                    int.Parse(found.Groups["col"].Value, CultureInfo.InvariantCulture),
                    Log: (at + 1, at + 1)));
            }
        }

        return (diagnostics, [], MsBuildParser.UnparsedOf(lines, Form));
    }
}

/// <summary>Reads CMake's form: <c>CMake Error at path:line (command):</c> with the message block under it.</summary>
internal sealed class CMakeParser : IResultParser
{
    private static readonly Regex Form = new(@"^CMake (?<severity>Error|Warning)(?: at (?<file>[^:\s][^:]*?):(?<line>\d+)(?: \((?<command>\w+)\))?)?", RegexOptions.Compiled);

    public string Name => "cmake";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                var message = at + 1 < lines.Count ? lines[at + 1].Trim() : found.Value;

                diagnostics.Add(new(
                    "cmake",
                    found.Groups["severity"].Value == "Error" ? "error" : "warning",
                    Code: null,
                    message.Length == 0 ? line : message,
                    found.Groups["file"].Success ? found.Groups["file"].Value : null,
                    found.Groups["line"].Success ? int.Parse(found.Groups["line"].Value, CultureInfo.InvariantCulture) : null,
                    Project: found.Groups["command"].Success ? found.Groups["command"].Value : null,
                    Log: (at + 1, Math.Min(at + 2, lines.Count))));
            }
        }

        return (diagnostics, [], MsBuildParser.UnparsedOf(lines, Form));
    }
}

/// <summary>Reads Ninja's <c>FAILED:</c> block, naming the output whose diagnostics follow.</summary>
internal sealed class NinjaParser : IResultParser
{
    private static readonly Regex Form = new(@"^FAILED:\s*(?<output>.+)$", RegexOptions.Compiled);

    public string Name => "ninja";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                diagnostics.Add(new("ninja", "error", Code: null, $"build of '{found.Groups["output"].Value.Trim()}' failed", Log: (at + 1, at + 1)));
            }
        }

        return (diagnostics, [], [.. lines.Where(line => line.StartsWith("FAILED:", StringComparison.Ordinal) || (line.StartsWith("ninja: build stopped", StringComparison.Ordinal) ? false : LooksLikeError(line)))]);

        static bool LooksLikeError(string line)
            => line.Contains("error", StringComparison.OrdinalIgnoreCase) && !Form.IsMatch(line);
    }
}

/// <summary>Reads the linkers' forms: <c>undefined reference to</c>, <c>unresolved external symbol</c>, <c>ld: symbol(s) not found</c>.</summary>
internal sealed class LinkerParser : IResultParser
{
    private static readonly Regex Form = new(@"^(?:.*?(?<code>undefined reference to|unresolved external symbol|symbol\(s\) not found|duplicate symbol)\s*(?<message>.*)|ld:\s+(?<ldmessage>.*))$", RegexOptions.Compiled);

    public string Name => "link";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                var message = found.Groups["message"].Success ? $"{found.Groups["code"].Value} {found.Groups["message"].Value}".Trim() : found.Groups["ldmessage"].Value;

                diagnostics.Add(new("link", "error", Code: found.Groups["code"].Success ? "LNK-undefined" : "ld", message, Log: (at + 1, at + 1)));
            }
        }

        return (diagnostics, [], MsBuildParser.UnparsedOf(lines, Form));
    }
}

/// <summary>Reads tsc's form: <c>path(line,col): error TS2304: message</c>.</summary>
internal sealed class TscParser : IResultParser
{
    private static readonly Regex Form = new(@"^(?<file>[^(\s][^(:]*?)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error|warning)\s+(?<code>TS\d+):\s+(?<message>.*)$", RegexOptions.Compiled);

    public string Name => "tsc";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                diagnostics.Add(new(
                    "tsc",
                    found.Groups["severity"].Value,
                    found.Groups["code"].Value,
                    found.Groups["message"].Value,
                    found.Groups["file"].Value,
                    int.Parse(found.Groups["line"].Value, CultureInfo.InvariantCulture),
                    int.Parse(found.Groups["col"].Value, CultureInfo.InvariantCulture),
                    Log: (at + 1, at + 1)));
            }
        }

        return (diagnostics, [], MsBuildParser.UnparsedOf(lines, Form));
    }
}

/// <summary>Reads the Dart analyser's form: <c>error • message • path:line:col • code</c>.</summary>
internal sealed class DartAnalyserParser : IResultParser
{
    private static readonly Regex Form = new(@"^\s*(?<severity>error|warning|info)\s+•\s+(?<message>.+?)\s+•\s+(?<file>[^:•]+):(?<line>\d+):(?<col>\d+)\s+•\s+(?<code>[\w_]+)\s*$", RegexOptions.Compiled);

    public string Name => "dart";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var (line, at) in lines.Select((line, at) => (line, at)))
        {
            if (Form.Match(line) is { Success: true } found)
            {
                diagnostics.Add(new(
                    "dart",
                    found.Groups["severity"].Value == "info" ? "note" : found.Groups["severity"].Value,
                    found.Groups["code"].Value,
                    found.Groups["message"].Value,
                    found.Groups["file"].Value,
                    int.Parse(found.Groups["line"].Value, CultureInfo.InvariantCulture),
                    int.Parse(found.Groups["col"].Value, CultureInfo.InvariantCulture),
                    Log: (at + 1, at + 1)));
            }
        }

        return (diagnostics, [], MsBuildParser.UnparsedOf(lines, Form));
    }
}

/// <summary>Reads a .NET stack trace: <c>   at N.C.M() in path:line N</c>, with the exception line above and the inner-exception marker between.</summary>
internal sealed class DotNetStackParser : IResultParser
{
    private static readonly Regex Thrown = new(@"^(?<type>[\w.]*Exception(?:\w*)?): (?<message>.*)$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^\s+at (?<symbol>[\w.<>`+\[\],\s]+\([^)]*\))(?: in (?<file>.+?):line (?<line>\d+))?", RegexOptions.Compiled);
    private static readonly Regex Inner = new(@"^--- End of (?:inner exception )?stack trace(?: from previous location where the exception was thrown)? ---", RegexOptions.Compiled);

    public string Name => "dotnet-stack";

    /// <summary>
    /// The .NET frames of a stack printed alone - a TRX record's <c>StackTrace</c>, held as data: every line the frame
    /// form reads, in the order printed. The one owner of the form: the trace a console printed and the trace a file
    /// hold are the same lines.
    /// </summary>
    /// <param name="lines">The stack's lines.</param>
    internal static IReadOnlyList<StackFrame> FramesOf(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var frames = new List<StackFrame>();

        foreach (var line in lines)
        {
            if (Frame.Match(line) is { Success: true } frame)
            {
                frames.Add(new(line, frame.Groups["file"].Success ? frame.Groups["file"].Value : null, frame.Groups["line"].Success ? int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture) : null, Symbol: frame.Groups["symbol"].Value.Trim()));
            }
        }

        return frames;
    }

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (Thrown.Match(lines[at]) is not { Success: true } thrown)
            {
                continue;
            }

            var record = ExceptionRecordOf(lines, ref at, thrown);
            failures.Add(new("dotnet", $"{record.Type}: {record.Message}", Exception: record, Outcome: "errored", File: TopInTreeOf(record.Stack), Line: TopLineOf(record.Stack)));
        }

        return ([], failures, []);

        static string? TopInTreeOf(IReadOnlyList<StackFrame> stack)
            => stack.FirstOrDefault(frame => frame.InTree)?.File ?? stack.FirstOrDefault()?.File;

        static int? TopLineOf(IReadOnlyList<StackFrame> stack)
            => (stack.FirstOrDefault(frame => frame.InTree) ?? stack.FirstOrDefault())?.Line;
    }

    /// <summary>One thrown error and its causes, read from <paramref name="at"/> on; <paramref name="at"/> lands past the record.</summary>
    internal static ExceptionRecord ExceptionRecordOf(IReadOnlyList<string> lines, ref int at, Match thrown)
    {
        var stack = new List<StackFrame>();
        var causes = new List<ExceptionRecord>();
        var depth = 0;
        var last = at;

        for (var inner = at + 1; inner < lines.Count; inner++)
        {
            if (Frame.Match(lines[inner]) is { Success: true } frame)
            {
                stack.Add(new(lines[inner], frame.Groups["file"].Success ? frame.Groups["file"].Value : null, frame.Groups["line"].Success ? int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture) : null, Symbol: frame.Groups["symbol"].Value.Trim()));
                last = inner;
                continue;
            }

            if (Inner.IsMatch(lines[inner]) && inner + 1 < lines.Count && Thrown.Match(lines[inner + 1]) is { Success: true } cause && depth < ExceptionRecord.CauseBound)
            {
                depth++;
                last = inner;
                var innerAt = inner + 1;
                causes.Insert(0, ExceptionRecordOf(lines, ref innerAt, cause));
                inner = innerAt;
                last = Math.Max(last, innerAt);
                continue;
            }

            break;
        }

        at = last;
        return new ExceptionRecord(thrown.Groups["type"].Value, thrown.Groups["message"].Value, stack, causes);
    }
}

/// <summary>Reads a JVM stack trace: <c>at p.C.m(File.java:12)</c>, with <c>Caused by:</c> chains.</summary>
internal sealed class JvmStackParser : IResultParser
{
    private static readonly Regex Thrown = new(@"^(?<type>[\w.$]+(?:Exception|Error)): (?<message>.*)$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^\s+at (?<symbol>[\w.$]+\.[\w$<>]+)\((?<file>[^)]*?)(?::(?<line>\d+))?\)", RegexOptions.Compiled);
    private static readonly Regex Caused = new(@"^Caused by: (?<type>[\w.$]+(?:Exception|Error)): (?<message>.*)$", RegexOptions.Compiled);

    public string Name => "jvm-stack";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (Thrown.Match(lines[at]) is not { Success: true } thrown)
            {
                continue;
            }

            var stack = new List<StackFrame>();
            var causes = new List<ExceptionRecord>();

            for (var inner = at + 1; inner < lines.Count; inner++)
            {
                if (Frame.Match(lines[inner]) is { Success: true } frame)
                {
                    stack.Add(new(lines[inner], frame.Groups["file"].Value is { Length: > 0 } file ? file : null, frame.Groups["line"].Success ? int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture) : null, Symbol: frame.Groups["symbol"].Value));
                    continue;
                }

                if (Caused.IsMatch(lines[inner]))
                {
                    break;
                }

                if (stack.Count > 0)
                {
                    break;
                }
            }

            failures.Add(new("jvm", $"{thrown.Groups["type"].Value}: {thrown.Groups["message"].Value}", Exception: new ExceptionRecord(thrown.Groups["type"].Value, thrown.Groups["message"].Value, stack, causes), Outcome: "errored"));
        }

        return ([], failures, []);
    }
}

/// <summary>Reads a Python traceback: <c>Traceback (most recent call last):</c>, frames <c>File "path", line N, in f</c>, the error line last.</summary>
internal sealed class PythonStackParser : IResultParser
{
    private static readonly Regex Head = new(@"^Traceback \(most recent call last\):$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^\s+File ""(?<file>[^""]+)"", line (?<line>\d+), in (?<symbol>\w+)", RegexOptions.Compiled);
    private static readonly Regex Thrown = new(@"^(?<type>[\w.]+(?:Error|Exception|Interrupt|Exit)[\w.]*): (?<message>.*)$", RegexOptions.Compiled);

    public string Name => "python-stack";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (!Head.IsMatch(lines[at]))
            {
                continue;
            }

            var stack = new List<StackFrame>();

            for (var inner = at + 1; inner < lines.Count && Frame.IsMatch(lines[inner]); inner++)
            {
                var frame = Frame.Match(lines[inner]);
                stack.Add(new(lines[inner], frame.Groups["file"].Value, int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture), Symbol: frame.Groups["symbol"].Value));
            }

            var thrown = stack.Count > 0 && at + 1 + stack.Count < lines.Count && Thrown.Match(lines[at + 1 + stack.Count]) is { Success: true } error
                ? new ExceptionRecord(error.Groups["type"].Value, error.Groups["message"].Value, [.. stack.Reverse<StackFrame>()], [])
                : null;

            if (thrown is { } record)
            {
                failures.Add(new("python", $"{record.Type}: {record.Message}", Exception: record, Outcome: "errored"));
                at += stack.Count;
            }
        }

        return ([], failures, []);
    }
}

/// <summary>Reads V8's stack trace (Node and the browsers): <c>at f (/path/file.js:12:34)</c>, with the error line above.</summary>
internal sealed class V8StackParser : IResultParser
{
    private static readonly Regex Thrown = new(@"^(?<type>[\w]+(?:Error)?)?: (?<message>.*)$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^\s+at (?<symbol>[\w.$<>]+)?\s*\(?(?<file>[^()\s]+?):(?<line>\d+):(?<col>\d+)\)?", RegexOptions.Compiled);

    public string Name => "v8-stack";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (at + 1 >= lines.Count || Frame.Match(lines[at + 1]) is not { Success: true })
            {
                continue;
            }

            var thrown = Thrown.Match(lines[at]);

            if (!thrown.Success)
            {
                continue;
            }

            var stack = new List<StackFrame>();

            for (var inner = at + 1; inner < lines.Count && Frame.IsMatch(lines[inner]); inner++)
            {
                var frame = Frame.Match(lines[inner]);
                stack.Add(new(lines[inner], frame.Groups["file"].Value, int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture), int.Parse(frame.Groups["col"].Value, CultureInfo.InvariantCulture), Symbol: frame.Groups["symbol"].Success ? frame.Groups["symbol"].Value : null));
            }

            var type = thrown.Groups["type"].Success ? thrown.Groups["type"].Value : "Error";
            var record = new ExceptionRecord(type, thrown.Groups["message"].Value, stack, []);

            failures.Add(new("node", $"{type}: {thrown.Groups["message"].Value}", Exception: record, Outcome: "errored"));
            at += stack.Count;
        }

        return ([], failures, []);
    }
}

/// <summary>Reads Dart's stack form: <c>#0 f (file:line:col)</c>, with the error line above.</summary>
internal sealed class DartStackParser : IResultParser
{
    private static readonly Regex Thrown = new(@"^(?:Unhandled exception:\n)?(?<type>[\w.]+(?:Exception|Error))?:?\s*(?<message>.+)$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^#(?<n>\d+)\s+(?<symbol>[\w.<>]+)\s+\((?<file>.+?):(?<line>\d+):(?<col>\d+)\)", RegexOptions.Compiled);

    public string Name => "dart-stack";

    /// <summary>
    /// The Dart frames of a stack printed alone - a JSON reporter event's <c>stackTrace</c>, held as data: every
    /// <c>#n</c> line, in the order printed. The one owner of the form, as the console's and the file's are the same lines.
    /// </summary>
    /// <param name="lines">The stack's lines.</param>
    internal static IReadOnlyList<StackFrame> FramesOf(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var frames = new List<StackFrame>();

        foreach (var line in lines)
        {
            if (Frame.Match(line) is { Success: true } frame)
            {
                frames.Add(new(line, frame.Groups["file"].Value, int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture), int.Parse(frame.Groups["col"].Value, CultureInfo.InvariantCulture), Symbol: frame.Groups["symbol"].Value));
            }
        }

        return frames;
    }

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (at + 1 >= lines.Count || Frame.Match(lines[at + 1]) is not { Success: true })
            {
                continue;
            }

            var thrown = Thrown.Match(lines[at]);

            if (!thrown.Success)
            {
                continue;
            }

            var stack = new List<StackFrame>();

            for (var inner = at + 1; inner < lines.Count && Frame.IsMatch(lines[inner]); inner++)
            {
                var frame = Frame.Match(lines[inner]);
                stack.Add(new(lines[inner], frame.Groups["file"].Value, int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture), int.Parse(frame.Groups["col"].Value, CultureInfo.InvariantCulture), Symbol: frame.Groups["symbol"].Value));
            }

            var type = thrown.Groups["type"].Success ? thrown.Groups["type"].Value : "Exception";
            var record = new ExceptionRecord(type, thrown.Groups["message"].Value, stack, []);

            failures.Add(new("dart", $"{type}: {thrown.Groups["message"].Value}", Exception: record, Outcome: "errored"));
            at += stack.Count;
        }

        return ([], failures, []);
    }
}

/// <summary>Reads Go's panic: the <c>panic:</c> line with the goroutine's stack under it.</summary>
internal sealed class GoPanicParser : IResultParser
{
    private static readonly Regex Thrown = new(@"^panic:\s+(?<message>.*)$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^\s*(?<symbol>[\w./()+\[\]*]+\.(?:\(\*\w+\.)?\w+)\(.*?\)\s*$", RegexOptions.Compiled);
    private static readonly Regex Place = new(@"^\s*(?<file>[^:\s]+\.go):(?<line>\d+)\s", RegexOptions.Compiled);

    public string Name => "go-panic";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (Thrown.Match(lines[at]) is not { Success: true } thrown)
            {
                continue;
            }

            var stack = new List<StackFrame>();
            var symbol = (string?)null;

            for (var inner = at + 1; inner < lines.Count; inner++)
            {
                if (Frame.Match(lines[inner]) is { Success: true } frame)
                {
                    symbol = frame.Groups["symbol"].Value;
                    continue;
                }

                if (symbol is not null && Place.Match(lines[inner]) is { Success: true } place)
                {
                    stack.Add(new(lines[inner], place.Groups["file"].Value, int.Parse(place.Groups["line"].Value, CultureInfo.InvariantCulture), Symbol: symbol));
                    symbol = null;
                    continue;
                }

                if (stack.Count > 0)
                {
                    break;
                }
            }

            var record = new ExceptionRecord("panic", thrown.Groups["message"].Value, stack, []);

            failures.Add(new("go", $"panic: {thrown.Groups["message"].Value}", Exception: record, Outcome: "crashed"));
        }

        return ([], failures, []);
    }
}

/// <summary>Reads Rust's panic: <c>thread 'main' panicked at src/x.rs:12:5:</c> with its message and backtrace.</summary>
internal sealed class RustPanicParser : IResultParser
{
    private static readonly Regex Thrown = new(@"^thread '[^']+' panicked at (?<file>[^:]+):(?<line>\d+):(?<col>\d+):$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^\s+\d+:\s+0x[0-9a-f]+\s+-\s+(?<symbol>[\w:<>*]+)", RegexOptions.Compiled);

    public string Name => "rust-panic";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (Thrown.Match(lines[at]) is not { Success: true } thrown)
            {
                continue;
            }

            var message = at + 1 < lines.Count ? lines[at + 1].Trim() : thrown.Value;
            var stack = new List<StackFrame>();

            for (var inner = at + 2; inner < lines.Count && Frame.IsMatch(lines[inner]); inner++)
            {
                var frame = Frame.Match(lines[inner]);
                stack.Add(new(lines[inner], Symbol: frame.Groups["symbol"].Value));
            }

            var record = new ExceptionRecord("panicked", message, stack, []);

            failures.Add(new("rust", $"panicked at {thrown.Groups["file"].Value}:{thrown.Groups["line"].Value}: {message}", File: thrown.Groups["file"].Value, Line: int.Parse(thrown.Groups["line"].Value, CultureInfo.InvariantCulture), Exception: record, Outcome: "crashed"));
        }

        return ([], failures, []);
    }
}

/// <summary>Reads the sanitizers' report: <c>#0 0x55... in f /src/x.c:12</c>.</summary>
internal sealed class SanitizerParser : IResultParser
{
    private static readonly Regex Head = new(@"^==\d+==ERROR: (?<kind>[\w-]+): (?<message>.*)$", RegexOptions.Compiled);
    private static readonly Regex Frame = new(@"^\s+#\d+\s+0x[0-9a-f]+\s+in (?<symbol>[\w:<>.~]+)?\s+(?<file>[^:\s]+):(?<line>\d+)", RegexOptions.Compiled);

    public string Name => "sanitizer";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (Head.Match(lines[at]) is not { Success: true } head)
            {
                continue;
            }

            var stack = new List<StackFrame>();

            for (var inner = at + 1; inner < lines.Count && Frame.IsMatch(lines[inner]); inner++)
            {
                var frame = Frame.Match(lines[inner]);
                stack.Add(new(lines[inner], frame.Groups["file"].Value, int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture), Symbol: frame.Groups["symbol"].Success ? frame.Groups["symbol"].Value : null));
            }

            var record = new ExceptionRecord(head.Groups["kind"].Value, head.Groups["message"].Value, stack, []);

            failures.Add(new("sanitizer", $"{head.Groups["kind"].Value}: {head.Groups["message"].Value}", Exception: record, Outcome: "crashed"));
            at += stack.Count;
        }

        return ([], failures, []);
    }
}

/// <summary>Reads the xUnit console's failed-test form: <c>Failed DssHarness.Tests.X.Y [duration]</c> with its message and stack under it.</summary>
internal sealed class XUnitConsoleParser : IResultParser
{
    private static readonly Regex Failed = new(@"^\s+Failed\s+(?<id>[\w.+<>]+)\s+\[[\d.]+\s*(?<unit>ms|s)\]", RegexOptions.Compiled);
    private static readonly Regex Message = new(@"^\s+Error Message:\s*$", RegexOptions.Compiled);

    public string Name => "xunit-console";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (Failed.Match(lines[at]) is not { Success: true } failed)
            {
                continue;
            }

            var message = (string?)null;

            for (var inner = at + 1; inner < Math.Min(at + 6, lines.Count) && message is null; inner++)
            {
                if (Message.IsMatch(lines[inner]) && inner + 1 < lines.Count)
                {
                    message = lines[inner + 1].Trim();
                }
            }

            var id = failed.Groups["id"].Value;
            var parts = id.Split('.');
            failures.Add(new(
                "xunit",
                id,
                Name: parts.Length > 0 ? parts[^1] : id,
                Class: parts.Length > 2 ? string.Join('.', parts.Take(parts.Length - 1)) : null,
                Message: message,
                Outcome: "failed",
                Duration: failed.Groups["unit"].Value == "s" ? null : null));
        }

        return ([], failures, []);
    }
}

/// <summary>Reads gtest's failed-test form: <c>[  FAILED  ] Suite.Name (12 ms)</c> over its failure blocks.</summary>
internal sealed class GTestConsoleParser : IResultParser
{
    private static readonly Regex Failed = new(@"^\[\s+FAILED\s+\]\s+(?<suite>\w+)\.(?<name>\w+)", RegexOptions.Compiled);

    public string Name => "gtest-console";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        foreach (var line in lines)
        {
            if (Failed.Match(line) is { Success: true } failed)
            {
                failures.Add(new("gtest", $"{failed.Groups["suite"].Value}.{failed.Groups["name"].Value}", failed.Groups["name"].Value, failed.Groups["suite"].Value));
            }
        }

        return ([], failures, []);
    }
}

/// <summary>Reads ctest's failed list: <c>The following tests FAILED:</c> with <c>N - Name (Failed)</c> under it.</summary>
internal sealed class CTestConsoleParser : IResultParser
{
    private static readonly Regex Head = new(@"^The following tests FAILED:", RegexOptions.Compiled);
    private static readonly Regex One = new(@"^\s*\d+\s+-\s+(?<name>.+?)\s+\((?<outcome>Failed|Timeout|Not Run)\)", RegexOptions.Compiled);

    public string Name => "ctest-console";

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var failures = new List<TestFailure>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (!Head.IsMatch(lines[at]))
            {
                continue;
            }

            for (var inner = at + 1; inner < lines.Count && One.IsMatch(lines[inner]); inner++)
            {
                var one = One.Match(lines[inner]);
                var outcome = one.Groups["outcome"].Value == "Timeout" ? "timedOut" : "failed";

                failures.Add(new("ctest", one.Groups["name"].Value.Trim(), one.Groups["name"].Value.Trim(), Outcome: outcome));
            }
        }

        return ([], failures, []);
    }
}
