using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Text;

namespace DssHarness.Core.Results;

/// <summary>What a reading made of one line it was offered.</summary>
public enum LineTaken
{
    /// <summary>
    /// Not the reading's. Every line it held undecided before this one is not its either, and is given back to be
    /// offered to the other readings; the record it held them for is no record. Where it held lines, this line - which
    /// says only that they were not the reading's - is offered again as a line of its own, to the reading as well, which
    /// answers it as from a fresh start: it may begin the reading's next record. A record it took lines for may stay open
    /// past it - a test a runner said began, whose own output other readings read - and is finished when the reading says.
    /// </summary>
    Left,

    /// <summary>Held undecided: the reading is offered the next line first, and says with it whether the held lines are its.</summary>
    Held,

    /// <summary>The reading's, with every line it held undecided before it.</summary>
    Took,
}

/// <summary>
/// One parser of a tool's or a framework's output: it reads a log a line at a time as the log streams - never the log
/// whole - and says of each line whether it is its, holding only the record it has open. What it does not understand
/// it leaves, so the line is offered to the next parser or said unparsed: never a guess.
/// </summary>
public interface IResultParser
{
    /// <summary>The parser's name, as the choice of a phase's parsers names it.</summary>
    string Name { get; }

    /// <summary>A reading of one log, from its first line: the parser's state for that log alone.</summary>
    IResultReading Begin();
}

/// <summary>One parser's reading of one log, fed the lines it is offered in the order they were printed.</summary>
public interface IResultReading
{
    /// <summary>Offers the next line: the reading says whether it is its, and says what it finished into <paramref name="sink"/>.</summary>
    /// <param name="line">The line, with its number in the log.</param>
    /// <param name="sink">Where the reading says the diagnostics and failures it finishes.</param>
    LineTaken Offer(NumberedLine line, ResultSink sink);

    /// <summary>
    /// The log ended: what the reading has open it finishes into <paramref name="sink"/>. <see cref="LineTaken.Took"/>
    /// where the lines it held undecided were its after all, <see cref="LineTaken.Left"/> where they are given back.
    /// Asked more than once, and fed lines again after it, a reading answers as from a fresh start.
    /// </summary>
    /// <param name="sink">Where the reading says what it finishes.</param>
    LineTaken End(ResultSink sink);
}

/// <summary>A built-in parser: found by the core's one scan, made with no argument, and chosen by its name.</summary>
public interface IBuiltInParser : IResultParser;

/// <summary>
/// Where one reading says what it read, in the order it finished each - and what every reading of one log shares: the
/// build edge a log is in where a build tool said one, so a diagnostic printed inside it names it, the test it is in where
/// a runner said one began, so a crash a report says inside it is its, and how many errors and failures the log has said
/// so far, so a line that only says a build failed is said only where nothing said why.
/// </summary>
public sealed class ResultSink
{
    private readonly SharedReading _shared;

    /// <summary>A sink of its own for one reading, over what every reading of the log shares.</summary>
    internal ResultSink(SharedReading shared) => _shared = shared;

    /// <summary>The diagnostics the reading said, in the order of the lines they begin at.</summary>
    internal List<Diagnostic> Diagnostics { get; } = [];

    /// <summary>The failures the reading said, in the order of the lines they begin at.</summary>
    internal List<TestFailure> Failures { get; } = [];

    /// <summary>
    /// The build edge the log is in - the outputs a build tool said it was making where it printed what made them fail
    /// - or null where it is in none. A diagnostic said while it is set, naming no project of its own, names it.
    /// </summary>
    public string? Target
    {
        get => _shared.Target;
        set => _shared.Target = value;
    }

    /// <summary>
    /// The test the log is in - one a runner said began and has not said ended - or null where it is in none: a crash a
    /// report says while it is set is that test's (<see cref="AddCrash"/>). A runner's reading sets it as it reads a test
    /// begin and clears it as the test ends; a line a runner of tests prints of its own - CTest's next test - ends it too.
    /// </summary>
    public OpenTest? Test
    {
        get => _shared.Test;
        set => _shared.Test = value;
    }

    /// <summary>How many errors and failures every reading of the log has said so far.</summary>
    public int Said => _shared.Said;

    /// <summary>Says <paramref name="diagnostic"/>, naming the build edge the log is in where it names no project of its own.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    public void Add(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (diagnostic.Severity == "error")
        {
            _shared.Said++;

            if (diagnostic.Log is { } log)
            {
                _shared.SaidAt.Add(log.From);
            }
        }

        Diagnostics.Add(diagnostic.Project is null && _shared.Target is { } target ? diagnostic with { Project = target } : diagnostic);
    }

    /// <summary>Says <paramref name="failure"/>.</summary>
    /// <param name="failure">The failure.</param>
    public void Add(TestFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        _shared.Said++;

        if (failure.Log is { } log)
        {
            _shared.SaidAt.Add(log.From);
        }

        Failures.Add(failure);
    }

    /// <summary>
    /// Says a crash a report read - a sanitizer's, a debugger's, the C runtime's failed assertion. Where the log is in a test
    /// (<see cref="Test"/>), the crash is that test's failure: named for the test, its message the report's own words -
    /// its error's type ahead of what it said - its place, stack and log range the report's, and the test marked as said by
    /// it, so the runner that began it says it no second time; a crash that ended its program ends the test too. Where the
    /// log is in none, the crash is said as the report's own.
    /// </summary>
    /// <param name="crash">The crash, as its report's reader read it.</param>
    public void AddCrash(TestFailure crash)
    {
        ArgumentNullException.ThrowIfNull(crash);

        if (_shared.Test is not { } test)
        {
            Add(crash);
            return;
        }

        test.Said = true;

        if (crash.Outcome == "crashed")
        {
            _shared.Test = null;
        }

        Add(crash with
        {
            Framework = test.Framework,
            Id = test.Id,
            Class = test.Class,
            Method = test.Method,
            Message = crash.Message is not { } said ? crash.Id : crash.Exception is { } thrown ? $"{thrown.Type}: {said}" : said,
        });
    }

    /// <summary>
    /// Whether any reading of the log said an error or a failure that begins at a line from <paramref name="from"/> to
    /// <paramref name="to"/>: what a summary of those lines - a runner's line for a test whose own output said why it
    /// failed - asks before it says anything.
    /// </summary>
    /// <param name="from">The first line.</param>
    /// <param name="to">The last line.</param>
    public bool SaidWithin(int from, int to)
    {
        if (from > to)
        {
            return false;
        }

        using var said = _shared.SaidAt.GetViewBetween(from, to).GetEnumerator();
        return said.MoveNext();
    }
}

/// <summary>
/// A test a runner said began and has not said ended (<see cref="ResultSink.Test"/>): its identity, which a crash read
/// while it runs is said under, and whether one was.
/// </summary>
/// <param name="framework">The framework that runs it.</param>
/// <param name="id">Its id as the runner gives it.</param>
/// <param name="class">Its class or suite; null where the runner gives none.</param>
/// <param name="method">Its method; null where the runner gives none.</param>
public sealed class OpenTest(string framework, string id, string? @class, string? method)
{
    /// <summary>The framework that runs it.</summary>
    public string Framework { get; } = framework;

    /// <summary>Its id as the runner gives it.</summary>
    public string Id { get; } = id;

    /// <summary>Its class or suite; null where the runner gives none.</summary>
    public string? Class { get; } = @class;

    /// <summary>Its method; null where the runner gives none.</summary>
    public string? Method { get; } = method;

    /// <summary>Whether a crash read while it ran was said as its failure.</summary>
    public bool Said { get; internal set; }
}

/// <summary>What every reading of one log shares.</summary>
internal sealed class SharedReading
{
    /// <summary>The build edge the log is in, or null.</summary>
    public string? Target { get; set; }

    /// <summary>The test the log is in, or null.</summary>
    public OpenTest? Test { get; set; }

    /// <summary>How many errors and failures have been said.</summary>
    public int Said { get; set; }

    /// <summary>The lines the errors and failures said begin at.</summary>
    public SortedSet<int> SaidAt { get; } = [];
}

/// <summary>
/// The built-in parsers, one owner of the forms the tools print at the versions the tool's CI runs, each a class found
/// by the core's one scan and chosen by its name (<see cref="PhaseParsers"/>): the build tools' and compilers' forms
/// here, the runtimes' stack traces (<see cref="StackForms"/>) and the test runners' consoles beside them.
/// </summary>
public static class ResultParsers
{
    /// <summary>Every built-in parser, by its name.</summary>
    public static IReadOnlyList<IResultParser> BuiltIn => [.. CoreImplementations<IBuiltInParser>.OfThisBuild.OrderBy(parser => parser.Name, StringComparer.Ordinal)];

    /// <summary>The built-in parser named <paramref name="name"/>; null where none is.</summary>
    /// <param name="name">The parser's name.</param>
    public static IResultParser? Of(string name)
        => CoreImplementations<IBuiltInParser>.OfThisBuild.FirstOrDefault(parser => string.Equals(parser.Name, name, StringComparison.Ordinal));

    /// <summary>The built-in parser named <paramref name="name"/>, which the code itself names: its absence is a bug.</summary>
    /// <param name="name">The parser's name.</param>
    internal static IResultParser Named(string name)
        => Of(name) ?? throw new InvalidOperationException($"No built-in parser is named '{name}'.");
}

/// <summary>What the parsers share in reading a line's parts: a captured number, a place a frame names.</summary>
internal static class ParsedText
{
    /// <summary>The whole number <paramref name="group"/> captured; null where it captured none, or one no line or column can be.</summary>
    /// <param name="group">The group.</param>
    public static int? Whole(Group group)
        => group.Success && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

    /// <summary>The text <paramref name="group"/> captured, trimmed; null where it captured nothing.</summary>
    /// <param name="group">The group.</param>
    public static string? Text(Group group)
        => group.Success && group.Value.Trim() is { Length: > 0 } text ? text : null;

    /// <summary>
    /// The file a frame or a diagnostic names, as the tool gave it: a <c>file:</c> URI as the path it names
    /// (<see cref="Platform.PlatformPaths.AsAPath"/>), any other text as written - a runtime's own module
    /// (<c>dart:core</c>, <c>node:internal/...</c>, <c>package:x/y.dart</c>) stays the name it is.
    /// </summary>
    /// <param name="location">The location as printed.</param>
    public static string? FileOf(string? location)
        => location is null ? null : Platform.PlatformPaths.AsAPath(location) ?? location;

    /// <summary>The scheme-and-package a location names where it names one of a runtime's own: <c>dart:core</c>, <c>package:test_api</c>, <c>node:internal</c>.</summary>
    /// <param name="location">The location as printed.</param>
    public static string? ModuleOf(string? location)
    {
        if (location is null || Platform.PlatformPaths.AsAPath(location) is not null)
        {
            return null;
        }

        var end = location.IndexOfAny(['/', '\\'], location.IndexOf(':', StringComparison.Ordinal) + 1);
        return end > 0 ? location[..end] : location;
    }

    /// <summary>
    /// A thrown error said in one line, as a failure's id says it: <c>Type: the message's first line</c>, or the type alone
    /// where the error said no message.
    /// </summary>
    /// <param name="type">The error's type.</param>
    /// <param name="message">Its message; empty where it said none.</param>
    public static string Headed(string type, string message)
        => message.Split('\n')[0].Trim() is { Length: > 0 } first ? $"{type}: {first}" : type;

    /// <summary>The lines of a message, each trimmed at its end, joined by newlines, the blank ones at either end left out; null where none holds anything.</summary>
    /// <param name="lines">The message's lines.</param>
    public static string? Joined(IEnumerable<string> lines)
    {
        var said = string.Join('\n', lines.Select(line => line.TrimEnd())).Trim('\n');
        return said.Trim().Length == 0 ? null : said;
    }
}

/// <summary>
/// MSBuild's canonical form, read for every tool that prints it: <c>origin : [fatal] error|warning CODE: text [project]</c>,
/// the origin a file with its place - <c>path(line)</c>, <c>path(line,col)</c>, <c>path(line-line)</c>,
/// <c>path(line,col-col)</c>, <c>path(line,col,line,col)</c> - or a tool's or a project's name. MSBuild and csc print it,
/// and so do MSVC's cl and link and tsc with <c>--pretty false</c>; each diagnostic's tool is the one its code belongs to -
/// <c>C</c> and <c>D</c> codes cl's (<c>msvc</c>), <c>LNK</c> link's, <c>TS</c> tsc's, every other MSBuild's. clang-cl
/// writes its place the same way with no code, and is read with clang's own form (<see cref="GnuFormParser"/>), which
/// every choice that may read clang-cl's output puts ahead of this one.
/// An MSVC note under an error is its: <c>see reference to ... being compiled</c> a frame of its instantiation chain,
/// any other a line of its record. MSBuild's summary after <c>Build FAILED.</c> repeats what was said, and is taken
/// with it; link's <c>LNK1120</c>, a count of the unresolved externals said before it, is said only where none was. The
/// project in brackets is the project's file: what MSBuild writes after it for one inner build of a project that targets
/// several frameworks (<c>App.csproj::TargetFramework=net10.0</c>) is the build's, not the file's (<see cref="ProjectOf"/>).
/// </summary>
internal sealed class MsBuildParser : IBuiltInParser
{
    private static readonly Regex Placed = LinearRegex.Compile(
        @"^\s*(?<file>.+)\((?<place>\d+(?:[,-]\d+){0,3})\)\s*:\s*(?<fatal>fatal\s+)?(?<severity>error|warning|note)(?:\s+(?<code>[A-Za-z]+\d+))?\s*:\s*(?<message>.*?)(?:\s+\[(?<project>[^\[\]]+)\])?\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex Unplaced = LinearRegex.Compile(
        @"^\s*(?<origin>(?:[A-Za-z]:)?[^:]+?)\s+:\s+(?<fatal>fatal\s+|command line\s+)?(?<severity>error|warning)(?:\s+(?<code>[A-Za-z]+\d+))?\s*:\s*(?<message>.*?)(?:\s+\[(?<project>[^\[\]]+)\])?\s*$",
        RegexOptions.IgnoreCase);

    private static readonly Regex Summary = LinearRegex.Compile(@"^\s*Build (?:FAILED|succeeded)\.\s*$");

    /// <summary>
    /// The project file a diagnostic's brackets name: the text before the <c>::</c> MSBuild writes ahead of an inner build's
    /// properties - no path holds two colons running - else the whole; null where the brackets are absent or blank.
    /// </summary>
    /// <param name="project">The bracketed project as the line holds it.</param>
    private static string? ProjectOf(Group project)
        => ParsedText.Text(project) is { } said && said.IndexOf("::", StringComparison.Ordinal) is var inner and > 0 ? said[..inner].TrimEnd() : ParsedText.Text(project);

    private static readonly Regex SummaryLine = LinearRegex.Compile(@"^(?:\s+\d+ (?:Warning|Error)\(s\)|Time Elapsed [\d:.,]+|\s*""[^""]+"" \(.+\) \(\d+\) ->|\s*\(\w+ target\) ->)\s*$");

    private static readonly Regex Instantiation = LinearRegex.Compile(@"^see reference to .* (?:instantiation|being compiled)");

    private static readonly Regex WithBlock = LinearRegex.Compile(@"^\s+(?:with|and|\[|\]|[\w:<>]+\s*=.*)\s*$");

    /// <summary>The code link gives the count of the unresolved externals it said before it.</summary>
    private const string UnresolvedCount = "LNK1120";

    public string Name => "msbuild";

    public IResultReading Begin() => new Reading();

    /// <summary>The tool a canonical diagnostic's code belongs to.</summary>
    internal static string ToolOf(string? code)
        => code is null ? "msbuild"
            : code.StartsWith("LNK", StringComparison.OrdinalIgnoreCase) ? "link"
            : code.StartsWith("TS", StringComparison.OrdinalIgnoreCase) && code.Length > 2 && char.IsAsciiDigit(code[2]) ? "tsc"
            : code.Length > 1 && code[0] is 'C' or 'D' && char.IsAsciiDigit(code[1]) ? "msvc"
            : "msbuild";

    /// <summary>A place in parentheses as the canonical form writes it: its line, its column, its last line and its last column where given.</summary>
    internal static (int? Line, int? Column, int? EndLine, int? EndColumn) PlaceOf(string place)
    {
        static int? Number(string text)
            => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;

        var commas = place.Split(',');

        return commas.Length switch
        {
            1 when place.Split('-') is [var line, var last] => (Number(line), null, Number(last), null),
            1 => (Number(place), null, null, null),
            2 when commas[1].Split('-') is [var column, var lastColumn] => (Number(commas[0]), Number(column), null, Number(lastColumn)),
            2 => (Number(commas[0]), Number(commas[1]), null, null),
            4 => (Number(commas[0]), Number(commas[1]), Number(commas[2]), Number(commas[3])),
            _ => (Number(commas[0]), null, null, null),
        };
    }

    private sealed class Reading : IResultReading
    {
        private readonly HashSet<string> _said = new(StringComparer.Ordinal);
        private Diagnostic? _open;
        private List<StackFrame> _frames = [];
        private int _to;
        private bool _summary;
        private bool _errorSaid;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            if (_summary)
            {
                if (SummaryLine.IsMatch(line.Text) || line.Text.Trim().Length == 0)
                {
                    _summary = !line.Text.StartsWith("Time Elapsed", StringComparison.Ordinal);
                    return LineTaken.Took;
                }

                if (Read(line) is { } repeated)
                {
                    // MSBuild's summary repeats what it said: a diagnostic said before is taken with it, one not said
                    // before - its first printing lost - is said here.
                    if (_said.Add(Key(repeated)))
                    {
                        sink.Add(repeated);
                    }

                    return LineTaken.Took;
                }

                _summary = false;
            }

            if (Summary.IsMatch(line.Text))
            {
                Close(sink);
                _summary = true;
                return LineTaken.Took;
            }

            if (_open is not null && Placed.Match(line.Text) is { Success: true } note && note.Groups["severity"].Value.Equals("note", StringComparison.OrdinalIgnoreCase))
            {
                var (noteLine, noteColumn, _, _) = PlaceOf(note.Groups["place"].Value);

                if (Instantiation.IsMatch(note.Groups["message"].Value))
                {
                    _frames.Add(new StackFrame(line.Text.Trim(), note.Groups["file"].Value.Trim(), noteLine, noteColumn, Kind: "instantiation"));
                }

                _to = line.Last;
                return LineTaken.Took;
            }

            if (_open is not null && WithBlock.IsMatch(line.Text))
            {
                _to = line.Last;
                return LineTaken.Took;
            }

            Close(sink);

            if (Read(line) is not { } read)
            {
                return LineTaken.Left;
            }

            if (read.Code is UnresolvedCount && _errorSaid)
            {
                // link's count of the unresolved externals it said already: no error of its own.
                _errorSaid = false;
                return LineTaken.Took;
            }

            _open = read;
            _to = line.Last;
            return LineTaken.Took;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            _summary = false;
            return LineTaken.Left;
        }

        private void Close(ResultSink sink)
        {
            if (_open is not { } open)
            {
                return;
            }

            var said = open with { Stack = _frames, Log = (open.Log!.Value.From, _to) };

            _said.Add(Key(said));
            sink.Add(said);
            _errorSaid |= said.Severity == "error";
            _open = null;
            _frames = [];
        }

        private static string Key(Diagnostic diagnostic)
            => string.Join('\u0001', diagnostic.Tool, diagnostic.Severity, diagnostic.Code, diagnostic.Message, diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Project);

        private static Diagnostic? Read(NumberedLine line)
        {
            if (Placed.Match(line.Text) is { Success: true } placed)
            {
                var (at, column, endLine, endColumn) = PlaceOf(placed.Groups["place"].Value);
                var code = ParsedText.Text(placed.Groups["code"]);

                return new Diagnostic(
                    ToolOf(code),
                    placed.Groups["severity"].Value.ToLowerInvariant(),
                    code,
                    Message(placed),
                    placed.Groups["file"].Value.Trim(),
                    at,
                    column,
                    endLine,
                    endColumn,
                    Project: ProjectOf(placed.Groups["project"]),
                    Log: (line.Number, line.Last));
            }

            if (Unplaced.Match(line.Text) is { Success: true } unplaced)
            {
                var code = ParsedText.Text(unplaced.Groups["code"]);

                return new Diagnostic(
                    ToolOf(code),
                    unplaced.Groups["severity"].Value.ToLowerInvariant(),
                    code,
                    Message(unplaced),
                    Project: ProjectOf(unplaced.Groups["project"]) ?? unplaced.Groups["origin"].Value.Trim(),
                    Log: (line.Number, line.Last));
            }

            return null;

            static string Message(Match found)
                => ParsedText.Text(found.Groups["message"]) ?? found.Value.Trim();
        }
    }
}

/// <summary>
/// gcc's and clang's form, read as one: <c>path:line[:col]: [fatal] error|warning|note: message [-Wflag]</c> - and the place
/// written as MSVC writes one, <c>path(line,col): error: message [-Wflag]</c>, as clang prints it where it is clang-cl - and
/// the driver's own <c>gcc: error: ...</c>. The chain that led to an error is its stack, innermost first, in the order
/// printed: the include chain above it (<c>In file included from</c>, gcc's indented <c>from</c> lines), gcc's
/// instantiation chain above it (<c>In instantiation of</c>, <c>required from</c>), and the notes under it that carry a
/// chain - clang's <c>in instantiation of ... requested here</c> and <c>expanded from macro</c>, gcc's <c>in expansion of
/// macro</c>. Every other note, the source and caret lines, and the trailers (<c>N errors generated.</c>,
/// <c>compilation terminated.</c>) are lines of the record they follow, never diagnostics of their own. The source and
/// caret lines are read as clang prints them, with the line-number gutter and, as clang before 17 printed them, with
/// none: a bare line under a diagnostic or a note is held for the one line after it, which says by its caret whether it
/// was the source. The flag that
/// names a diagnostic is its code, <c>-Werror=x</c> and <c>-Werror,-Wx</c> read as the <c>-Wx</c> they promote.
/// </summary>
internal abstract class GnuFormParser : IBuiltInParser
{
    private static readonly Regex Included = LinearRegex.Compile(@"^(?:In file included from|(?<from>\s+from)) (?<file>.+?):(?<line>\d+)(?::(?<col>\d+))?[:,]$");

    private static readonly Regex Context = LinearRegex.Compile(@"^(?<file>.+?): (?<context>In .+|At global scope|At top level):$");

    private static readonly Regex Required = LinearRegex.Compile(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+):\s+(?<what>required (?:from|by) .+|recursively required .+|in .constexpr. expansion of .+)$");

    private static readonly Regex Said = LinearRegex.Compile(
        @"^(?<file>.+?):(?<line>\d+):(?:(?<col>\d+):)?\s+(?<severity>fatal error|error|warning|note):\s+(?<message>.*?)(?:\s+\[(?<flag>-W[^\]\s]*)\])?$");

    /// <summary>
    /// clang's place written as MSVC writes one, as clang-cl prints it: <c>path(line[,col]): severity: message [-Wflag]</c>,
    /// its severity never followed by a code, which cl's own lines carry.
    /// </summary>
    private static readonly Regex SaidAsMsvc = LinearRegex.Compile(
        @"^(?<file>.+?)\((?<line>\d+)(?:,(?<col>\d+))?\) ?:\s+(?<severity>fatal error|error|warning|note):\s+(?<message>.*?)(?:\s+\[(?<flag>-W[^\]\s]*)\])?$");

    private static readonly Regex Driver = LinearRegex.Compile(
        @"^(?:\S*[/\\])?(?:[\w.]+-)*(?:gcc|g\+\+|cc|c\+\+|clang|clang\+\+|cc1|cc1plus|cc1obj|lto1)(?:-\d+(?:\.\d+)*)?(?:\.exe)?: (?<severity>fatal error|error|warning|note|internal compiler error): (?<message>.+)$");

    private static readonly Regex Excerpt = LinearRegex.Compile(@"^(?:\s*\d*\s*\|.*|\s*\+\+\+ \|.*|\s+[\^~][\^~\s]*)$");

    /// <summary>The caret line under a source line, wherever under it clang printed it: with the gutter or, as clang before 17 printed it, with none.</summary>
    private static readonly Regex Caret = LinearRegex.Compile(@"^\s*[\^~][\^~\s]*$");

    private static readonly Regex Trailer = LinearRegex.Compile(@"^(?:\d+ (?:error|warning)s? generated\.|compilation terminated\.|(?:\S*[/\\])?(?:cc1\w*|lto1)(?:\.exe)?: (?:some|all) warnings being treated as errors)$");

    private static readonly Regex Chained = LinearRegex.Compile(@"(?:in instantiation of .* requested here|required from|expanded from macro|in expansion of macro)");

    /// <summary>The tool every diagnostic this reading says is said to come from.</summary>
    protected abstract string Tool { get; }

    public abstract string Name { get; }

    public IResultReading Begin() => new Reading(Tool);

    private sealed class Reading(string tool) : IResultReading
    {
        private readonly List<StackFrame> _chain = [];
        private int? _from;
        private int? _included;
        private int _includes;
        private Diagnostic? _open;
        private List<StackFrame> _frames = [];
        private int _to;
        private bool _saidSinceTrailer;
        private bool _source;
        private bool _candidate;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_open is not null)
            {
                if (_candidate)
                {
                    // The line under the one held, which says by its caret whether that was the source clang printed
                    // with no gutter: it was, and both are the record's, or the held line was no source line, and both
                    // are offered again - the held one to the others, this one to every reading as from a fresh start.
                    _candidate = false;

                    if (Caret.IsMatch(text))
                    {
                        _source = false;
                        _to = line.Last;
                        return LineTaken.Took;
                    }

                    Close(sink);
                    return LineTaken.Left;
                }

                if (Excerpt.IsMatch(text))
                {
                    _source = false;
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (SaidOf(text) is { Success: true } note && note.Groups["severity"].Value == "note")
                {
                    if (Chained.IsMatch(note.Groups["message"].Value))
                    {
                        _frames.Add(new StackFrame(
                            text.Trim(),
                            note.Groups["file"].Value,
                            ParsedText.Whole(note.Groups["line"]),
                            ParsedText.Whole(note.Groups["col"]),
                            Kind: note.Groups["message"].Value.Contains("macro", StringComparison.Ordinal) ? "macro" : "instantiation"));
                    }

                    _source = true;
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (Trailer.IsMatch(text))
                {
                    Close(sink);
                    _saidSinceTrailer = false;
                    return LineTaken.Took;
                }

                if (_source)
                {
                    // The line under a diagnostic or a note, which clang before 17 printed with no gutter: held for
                    // the one line after it, the caret under it, which says whether it is the source.
                    _source = false;
                    _candidate = true;
                    return LineTaken.Held;
                }

                Close(sink);
            }

            if (Included.Match(text) is { Success: true } included)
            {
                // Innermost first, as every chain is held: gcc prints the include the file is in first and each
                // includer after it (`from ...`), clang each includer before the include it holds.
                var frame = new StackFrame(text.Trim(), included.Groups["file"].Value, ParsedText.Whole(included.Groups["line"]), ParsedText.Whole(included.Groups["col"]), Kind: "include");

                _from ??= line.Number;
                _included = included.Groups["from"].Success && _included is { } after ? after + 1 : _chain.Count - _includes;
                _chain.Insert(_included.Value, frame);
                _includes++;
                return LineTaken.Held;
            }

            if (Context.Match(text) is { Success: true } context)
            {
                Chain(line, context.Groups["context"].Value.StartsWith("In instantiation of", StringComparison.Ordinal)
                    ? new StackFrame(text.Trim(), context.Groups["file"].Value, Kind: "instantiation")
                    : null);
                return LineTaken.Held;
            }

            if (Required.Match(text) is { Success: true } required)
            {
                Chain(line, new StackFrame(text.Trim(), required.Groups["file"].Value, ParsedText.Whole(required.Groups["line"]), ParsedText.Whole(required.Groups["col"]), Kind: "instantiation"));
                return LineTaken.Held;
            }

            if (SaidOf(text) is { Success: true } said)
            {
                Open(line, new Diagnostic(
                    tool,
                    said.Groups["severity"].Value == "fatal error" ? "error" : said.Groups["severity"].Value,
                    CodeOf(said.Groups["flag"]),
                    ParsedText.Text(said.Groups["message"]) ?? text.Trim(),
                    said.Groups["file"].Value,
                    ParsedText.Whole(said.Groups["line"]),
                    ParsedText.Whole(said.Groups["col"])));
                return LineTaken.Took;
            }

            if (Driver.Match(text) is { Success: true } driver && !driver.Groups["message"].Value.StartsWith("linker command failed", StringComparison.Ordinal))
            {
                Open(line, new Diagnostic(
                    tool,
                    driver.Groups["severity"].Value is "note" or "warning" ? driver.Groups["severity"].Value : "error",
                    null,
                    driver.Groups["message"].Value.Trim()));
                return LineTaken.Took;
            }

            // A chain the compiler began and no diagnostic ended is not this reading's: it is given back, and the line that
            // ended it is offered again on its own.
            if (_from is not null)
            {
                Unchain();
                return LineTaken.Left;
            }

            if (_saidSinceTrailer && Trailer.IsMatch(text))
            {
                _saidSinceTrailer = false;
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            Unchain();
            return LineTaken.Left;
        }

        private void Chain(NumberedLine line, StackFrame? frame)
        {
            _from ??= line.Number;
            _included = null;
            _includes = 0;

            if (frame is not null)
            {
                _chain.Add(frame);
            }
        }

        private void Unchain()
        {
            _chain.Clear();
            _from = null;
            _included = null;
            _includes = 0;
        }

        private void Open(NumberedLine line, Diagnostic diagnostic)
        {
            _open = diagnostic with { Log = (_from ?? line.Number, line.Last) };
            _frames = [.. _chain];
            _to = line.Last;
            _source = true;
            Unchain();
        }

        private void Close(ResultSink sink)
        {
            if (_open is not { } open)
            {
                return;
            }

            sink.Add(open with { Stack = _frames, Log = (open.Log!.Value.From, _to) });
            _saidSinceTrailer = true;
            _open = null;
            _frames = [];
            _source = false;
            _candidate = false;
        }

        /// <summary>The diagnostic <paramref name="text"/> says, its place in either form clang prints: its own, or MSVC's as clang-cl writes it.</summary>
        private static Match SaidOf(string text)
            => Said.Match(text) is { Success: true } said ? said : SaidAsMsvc.Match(text);

        /// <summary>The flag that names a diagnostic: <c>-Wx</c>, also where <c>-Werror=x</c> or <c>-Werror,-Wx</c> promoted it.</summary>
        private static string? CodeOf(Group flag)
        {
            if (!flag.Success)
            {
                return null;
            }

            var named = flag.Value;

            if (named.StartsWith("-Werror=", StringComparison.Ordinal))
            {
                return $"-W{named["-Werror=".Length..]}";
            }

            return named.StartsWith("-Werror,", StringComparison.Ordinal) ? named["-Werror,".Length..] : named;
        }
    }
}

/// <summary>gcc's form, its diagnostics gcc's: chosen where the leg's toolchain is GNU.</summary>
internal sealed class GccParser : GnuFormParser
{
    protected override string Tool => "gcc";

    public override string Name => "gcc";
}

/// <summary>clang's form, its diagnostics clang's: chosen where the leg's toolchain is Clang or AppleClang.</summary>
internal sealed class ClangParser : GnuFormParser
{
    protected override string Tool => "clang";

    public override string Name => "clang";
}

/// <summary>gcc's and clang's shared form where which of the two printed it is not known - a CI job's log, a toolchain that declares no compiler: their diagnostics <c>cc</c>'s.</summary>
internal sealed class CcParser : GnuFormParser
{
    protected override string Tool => "cc";

    public override string Name => "cc";
}

/// <summary>
/// The linkers' forms but link's own (which is MSBuild's canonical form): GNU ld's <c>file:(section): undefined reference
/// to 'f'</c> - with the line where the object holds it, <c>file:line:(section)</c>, and behind the object where ld names it
/// first, as MinGW's does - under its <c>in function</c> line, its own <c>ld: ...</c> lines behind its path, a drive's
/// among them, gold's, lld's <c>ld.lld: error: undefined
/// symbol: f</c> with the <c>&gt;&gt;&gt; referenced by</c> lines that place it, mold's, and Apple ld's <c>Undefined
/// symbols for architecture</c> list with its <c>ld: warning:</c>. A line that only says the link failed - collect2's
/// <c>ld returned 1 exit status</c>, a compiler driver's <c>linker command failed</c>, Apple ld's <c>symbol(s) not
/// found</c> - is said only where no error before it said why.
/// </summary>
internal sealed class LinkerParser : IBuiltInParser
{
    private static readonly Regex Own = LinearRegex.Compile(
        @"^(?:(?:[A-Za-z]:)?[^\s:]*[/\\])?(?:[\w.]+-)?(?:ld(?:\.bfd|\.gold|\.lld)?|ld64(?:\.lld)?|lld-link|wasm-ld|mold|gold)(?:\.exe)?: (?<rest>.+)$");

    private static readonly Regex Severity = LinearRegex.Compile(@"^(?<severity>error|warning): (?<message>.+)$");

    private static readonly Regex InFunction = LinearRegex.Compile(@"^(?<object>.+): in function [`'‘](?<function>.+)['’]:$");

    /// <summary>
    /// A reference GNU ld places: <c>file:(section+offset): message</c>, <c>file:line:(section+offset): message</c> where the
    /// object holds line information - a <c>-g</c> build's - and <c>file:line: message</c> as older binutils printed that,
    /// each behind its object where ld names it first (<c>object:file:(section+offset)</c>, as a PE object's is). A
    /// path may begin with a drive; the object holds a separator or a dot, the file a character no line number holds, so
    /// each is read one way.
    /// </summary>
    private static readonly Regex Reference = LinearRegex.Compile(
        @"^(?:(?<object>(?:[A-Za-z]:)?[^:]*[/\\.][^:]*):)?(?<file>(?:[A-Za-z]:)?[^:]*[^:\d][^:]*)(?::(?<line>\d+)(?::\([^)]*\))?|:\([^)]*\)): (?<message>(?:undefined reference to|multiple definition of|relocation truncated) .+)$");

    private static readonly Regex Referenced = LinearRegex.Compile(@"^>>> (?:referenced by|defined at) (?<file>.+?)(?::(?<line>\d+))?(?: \(.+\))?$");

    private static readonly Regex Continued = LinearRegex.Compile(@"^>>> ");

    private static readonly Regex Collect2 = LinearRegex.Compile(@"^(?:\S*[/\\])?collect2(?:\.exe)?: error: (?<message>.+)$");

    private static readonly Regex DriverFailed = LinearRegex.Compile(@"^(?:\S*[/\\])?(?:[\w.]+-)*(?:clang|clang\+\+|gcc|g\+\+|cc|c\+\+)(?:-\d+(?:\.\d+)*)?(?:\.exe)?: error: (?<message>linker command failed.*)$");

    private static readonly Regex Undefined = LinearRegex.Compile(@"^Undefined symbols for architecture (?<arch>\S+):$");

    private static readonly Regex UndefinedSymbol = LinearRegex.Compile(@"^\s+""(?<symbol>[^""]+)"", referenced from:$");

    private static readonly Regex ReferencedFrom = LinearRegex.Compile(@"^\s{6,}\S.*$");

    private static readonly Regex NotFound = LinearRegex.Compile(@"^symbol\(s\) not found for architecture \S+$");

    public string Name => "ld";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private Diagnostic? _open;
        private int _to;
        private bool _errorSaid;
        private bool _apple;
        private int? _held;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_open is not null && Continued.IsMatch(text))
            {
                if (_open.File is null && Referenced.Match(text) is { Success: true } referenced)
                {
                    _open = _open with { File = referenced.Groups["file"].Value, Line = ParsedText.Whole(referenced.Groups["line"]) };
                }

                _to = line.Last;
                return LineTaken.Took;
            }

            if (_apple && _open is not null && ReferencedFrom.IsMatch(text))
            {
                _to = line.Last;
                return LineTaken.Took;
            }

            Close(sink);

            var own = Own.Match(text);
            var reference = Reference.Match(own.Success ? own.Groups["rest"].Value : text);

            if (_held is not null && !reference.Success)
            {
                // An `in function` line no reference followed was no reference's: it is given back, and this line offered
                // again on its own.
                _held = null;
                return LineTaken.Left;
            }

            if (reference.Success)
            {
                // A reference, behind the linker's name or with none ahead of it: under the function a line before named,
                // the line its object's place begins.
                Open(line, new Diagnostic("ld", "error", null, reference.Groups["message"].Value.Trim(), reference.Groups["file"].Value, ParsedText.Whole(reference.Groups["line"])));
                return LineTaken.Took;
            }

            if (_apple && UndefinedSymbol.Match(text) is { Success: true } symbol)
            {
                Open(line, new Diagnostic("ld", "error", null, $"undefined symbol: {symbol.Groups["symbol"].Value}"));
                return LineTaken.Took;
            }

            _apple = false;

            if (Undefined.IsMatch(text))
            {
                _apple = true;
                return LineTaken.Took;
            }

            if (Collect2.Match(text) is { Success: true } collect2)
            {
                return Failed(line, collect2.Groups["message"].Value, sink);
            }

            if (DriverFailed.Match(text) is { Success: true } driver)
            {
                return Failed(line, driver.Groups["message"].Value, sink);
            }

            if (!own.Success)
            {
                return LineTaken.Left;
            }

            var rest = own.Groups["rest"].Value;

            if (InFunction.IsMatch(rest))
            {
                // The object and function the references under it are in: the next line says whether one does.
                _held = line.Number;
                return LineTaken.Held;
            }

            if (Severity.Match(rest) is { Success: true } severity)
            {
                if (severity.Groups["severity"].Value == "error" && NotFound.IsMatch(severity.Groups["message"].Value))
                {
                    return Failed(line, severity.Groups["message"].Value, sink);
                }

                Open(line, new Diagnostic("ld", severity.Groups["severity"].Value, null, severity.Groups["message"].Value.Trim()));
                return LineTaken.Took;
            }

            if (NotFound.IsMatch(rest))
            {
                return Failed(line, rest, sink);
            }

            Open(line, new Diagnostic("ld", "error", null, rest.Trim()));
            return LineTaken.Took;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            _apple = false;
            _held = null;
            return LineTaken.Left;
        }

        /// <summary>A line that only says the link failed: taken with the errors before it, or said where none said why.</summary>
        private LineTaken Failed(NumberedLine line, string message, ResultSink sink)
        {
            if (_errorSaid)
            {
                _errorSaid = false;
                return LineTaken.Took;
            }

            sink.Add(new Diagnostic("ld", "error", null, message.Trim(), Log: (line.Number, line.Last)));
            return LineTaken.Took;
        }

        private void Open(NumberedLine line, Diagnostic diagnostic)
        {
            _open = diagnostic with { Log = (_held ?? line.Number, line.Last) };
            _to = line.Last;
            _held = null;
        }

        private void Close(ResultSink sink)
        {
            if (_open is not { } open)
            {
                return;
            }

            sink.Add(open with { Log = (open.Log!.Value.From, _to) });
            _errorSaid |= open.Severity == "error";
            _open = null;
        }
    }
}

/// <summary>
/// CMake's form: <c>CMake Error at path:line (command):</c>, <c>CMake Error in path:</c>, <c>CMake Error: message</c> and
/// their warnings, with the message block under it - its indented lines, over the blank lines between its paragraphs, as
/// one line, the command the head names said ahead of it (<c>add_subdirectory: add_subdirectory given source ...</c>),
/// for a diagnostic's project is the project or target a build names, never a command - and the
/// <c>Call Stack (most recent call first):</c> under it as the error's stack, each frame's command its symbol.
/// <c>-- Configuring incomplete, errors occurred!</c>, said after what it counts, is taken with it.
/// </summary>
internal sealed class CMakeParser : IBuiltInParser
{
    private const string Severities = @"(?<severity>Error|Warning|Deprecation Warning|Deprecation Error|Warning \(dev\)|Error \(dev\))";

    private static readonly Regex At = LinearRegex.Compile($@"^CMake {Severities} at (?<file>.+):(?<line>\d+) \((?<command>[^)]*)\):\s*$");

    private static readonly Regex In = LinearRegex.Compile($@"^CMake {Severities} in (?<file>.+):\s*$");

    private static readonly Regex Bare = LinearRegex.Compile($@"^CMake {Severities}:\s*(?<message>.*)$");

    private static readonly Regex CallStack = LinearRegex.Compile(@"^Call Stack \(most recent call first\):$");

    private static readonly Regex Frame = LinearRegex.Compile(@"^\s+(?<file>.+):(?<line>\d+) \((?<command>[^)]*)\)$");

    private static readonly Regex Incomplete = LinearRegex.Compile(@"^-- (?:Configuring|Generating) incomplete, errors occurred!$");

    public string Name => "cmake";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private Diagnostic? _open;
        private string? _command;
        private readonly List<string> _message = [];
        private readonly List<StackFrame> _frames = [];
        private bool _stack;
        private bool _blank;
        private int _to;
        private bool _errorSaid;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_open is not null)
            {
                if (text.Trim().Length == 0)
                {
                    if (!_blank)
                    {
                        // A blank line between the block's paragraphs, or the first of the two that end it: the line after
                        // it says which.
                        _blank = true;
                        return LineTaken.Held;
                    }

                    // The second blank line in a row, which ends the block, with the one before it.
                    Close(sink);
                    return LineTaken.Took;
                }

                _blank = false;

                if (CallStack.IsMatch(text))
                {
                    _stack = true;
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (_stack && Frame.Match(text) is { Success: true } frame)
                {
                    _frames.Add(new StackFrame(text.Trim(), frame.Groups["file"].Value, ParsedText.Whole(frame.Groups["line"]), Symbol: frame.Groups["command"].Value));
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (!_stack && text.StartsWith("  ", StringComparison.Ordinal))
                {
                    _message.Add(text.Trim());
                    _to = line.Last;
                    return LineTaken.Took;
                }

                Close(sink);
            }

            if (_errorSaid && Incomplete.IsMatch(text))
            {
                _errorSaid = false;
                return LineTaken.Took;
            }

            if (At.Match(text) is { Success: true } at)
            {
                Open(line, at, ParsedText.Text(at.Groups["file"]), ParsedText.Whole(at.Groups["line"]), ParsedText.Text(at.Groups["command"]));
                return LineTaken.Took;
            }

            if (In.Match(text) is { Success: true } inFile)
            {
                Open(line, inFile, ParsedText.Text(inFile.Groups["file"]), null, null);
                return LineTaken.Took;
            }

            if (Bare.Match(text) is { Success: true } bare)
            {
                Open(line, bare, null, null, null);

                if (ParsedText.Text(bare.Groups["message"]) is { } inline)
                {
                    _message.Add(inline);
                }

                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            return LineTaken.Left;
        }

        private void Open(NumberedLine line, Match head, string? file, int? at, string? command)
        {
            _open = new Diagnostic(
                "cmake",
                head.Groups["severity"].Value.Contains("Error", StringComparison.Ordinal) ? "error" : "warning",
                null,
                head.Value.Trim(),
                file,
                at,
                Log: (line.Number, line.Last));
            _command = command;
            _to = line.Last;
        }

        private void Close(ResultSink sink)
        {
            if (_open is not { } open)
            {
                return;
            }

            // The block CMake printed, the command the head named ahead of it; the head itself where no block followed,
            // which names the command already.
            sink.Add(open with
            {
                Message = _message.Count == 0 ? open.Message
                    : _command is { } command ? $"{command}: {string.Join(' ', _message)}"
                    : string.Join(' ', _message),
                Stack = [.. _frames],
                Log = (open.Log!.Value.From, _to),
            });
            _errorSaid |= open.Severity == "error";
            _open = null;
            _command = null;
            _message.Clear();
            _frames.Clear();
            _stack = false;
            _blank = false;
        }
    }
}

/// <summary>
/// Ninja's form: a <c>FAILED: outputs</c> block, the command it ran on the line under it, and what that command printed -
/// the edge every diagnostic said inside it names as its project. An edge is a diagnostic of its own only where nothing
/// inside it said an error, for then it is all that says what failed. Ninja's own <c>ninja: error:</c> is an error;
/// <c>ninja: build stopped</c> and its progress lines end an edge.
/// </summary>
internal sealed class NinjaParser : IBuiltInParser
{
    private static readonly Regex Failed = LinearRegex.Compile(@"^FAILED: (?:\[code=\d+\] )?(?<outputs>.+?)\s*$");

    private static readonly Regex Own = LinearRegex.Compile(@"^ninja: (?<severity>error|fatal|warning): (?<message>.+)$");

    private static readonly Regex Stopped = LinearRegex.Compile(@"^ninja: build stopped: .*$");

    private static readonly Regex Progress = LinearRegex.Compile(@"^\[\d+/\d+\] ");

    public string Name => "ninja";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private string? _edge;
        private int _from;
        private int _to;
        private int _saidBefore;
        private bool _command;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            if (_command)
            {
                _command = false;
                _to = line.Last;
                return LineTaken.Took;
            }

            if (Failed.Match(line.Text) is { Success: true } failed)
            {
                Close(sink);
                _edge = failed.Groups["outputs"].Value;
                _from = line.Number;
                _to = line.Last;
                _saidBefore = sink.Said;
                _command = true;
                sink.Target = _edge;
                return LineTaken.Took;
            }

            if (Stopped.IsMatch(line.Text) || Progress.IsMatch(line.Text))
            {
                Close(sink);
                return LineTaken.Took;
            }

            if (Own.Match(line.Text) is { Success: true } own)
            {
                sink.Add(new Diagnostic("ninja", own.Groups["severity"].Value == "warning" ? "warning" : "error", null, own.Groups["message"].Value.Trim(), Log: (line.Number, line.Last)));
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            _command = false;
            return LineTaken.Left;
        }

        private void Close(ResultSink sink)
        {
            if (_edge is not { } edge)
            {
                return;
            }

            sink.Target = null;

            if (sink.Said == _saidBefore)
            {
                sink.Add(new Diagnostic("ninja", "error", null, $"build of '{edge}' failed", Project: edge, Log: (_from, _to)));
            }

            _edge = null;
        }
    }
}

/// <summary>
/// tsc's forms: its default, <c>path:line:col - error TS2304: message</c>, with the lines under it - the message's own
/// continuation, the source excerpt and its tildes - as its record; <c>--pretty false</c>'s <c>path(line,col): error
/// TS2304: message</c>; and a diagnostic of no file, <c>error TS5058: message</c>. <c>Found N errors</c> and the table of
/// files after it are taken with what they count.
/// </summary>
internal sealed class TscParser : IBuiltInParser
{
    private static readonly Regex Pretty = LinearRegex.Compile(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+) - (?<severity>error|warning|message) (?<code>TS\d+): (?<message>.*)$");

    private static readonly Regex Plain = LinearRegex.Compile(@"^(?<file>.+)\((?<line>\d+),(?<col>\d+)\): (?<severity>error|warning|message) (?<code>TS\d+): (?<message>.*)$");

    private static readonly Regex Unplaced = LinearRegex.Compile(@"^(?<severity>error|warning|message) (?<code>TS\d+): (?<message>.*)$");

    private static readonly Regex Under = LinearRegex.Compile(@"^(?:\s.*|\d+\s.*|)$");

    private static readonly Regex Found = LinearRegex.Compile(@"^(?:Found \d+ errors?(?: in (?:\d+ files|the same file, starting at: .+|.+:\d+))?\.|Errors\s+Files|\s+\d+\s+\S.*:\d+)$");

    public string Name => "tsc";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private Diagnostic? _open;
        private int _to;
        private bool _found;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            if (Read(line) is { } read)
            {
                Close(sink);
                _open = read;
                _to = line.Last;
                return LineTaken.Took;
            }

            if (Found.IsMatch(line.Text) && (_open is not null || _found))
            {
                Close(sink);
                _found = true;
                return LineTaken.Took;
            }

            if (_open is not null && Under.IsMatch(line.Text))
            {
                _to = line.Last;
                return LineTaken.Took;
            }

            Close(sink);
            _found = false;
            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            _found = false;
            return LineTaken.Left;
        }

        private void Close(ResultSink sink)
        {
            if (_open is { } open)
            {
                sink.Add(open with { Log = (open.Log!.Value.From, _to) });
                _open = null;
            }
        }

        private static Diagnostic? Read(NumberedLine line)
        {
            var found = Pretty.Match(line.Text) is { Success: true } pretty ? pretty
                : Plain.Match(line.Text) is { Success: true } plain ? plain
                : Unplaced.Match(line.Text) is { Success: true } unplaced ? unplaced
                : null;

            return found is null
                ? null
                : new Diagnostic(
                    "tsc",
                    found.Groups["severity"].Value == "message" ? "note" : found.Groups["severity"].Value,
                    found.Groups["code"].Value,
                    ParsedText.Text(found.Groups["message"]) ?? line.Text.Trim(),
                    ParsedText.Text(found.Groups["file"]),
                    ParsedText.Whole(found.Groups["line"]),
                    ParsedText.Whole(found.Groups["col"]),
                    Log: (line.Number, line.Last));
        }
    }
}

/// <summary>
/// ESLint's two forms: stylish, its default - the file alone on a line, then each problem under it as
/// <c>line:col  error|warning  message  rule</c> - and compact, <c>path: line N, col N, Error - message (rule)</c>. The
/// rule is the code. Its counts (<c>✖ N problems</c>, <c>N problems</c>, what <c>--fix</c> could fix) are taken with what
/// they count.
/// </summary>
internal sealed class EslintParser : IBuiltInParser
{
    private static readonly Regex File = LinearRegex.Compile(@"^(?<file>(?:[A-Za-z]:[\\/]|/)\S.*?)\s*$");

    private static readonly Regex Problem = LinearRegex.Compile(@"^\s+(?<line>\d+):(?<col>\d+)\s+(?<severity>error|warning)\s+(?<message>.+?)(?:\s{2,}(?<rule>[@\w/.-]+))?\s*$");

    private static readonly Regex Compact = LinearRegex.Compile(@"^(?<file>.+?): line (?<line>\d+), col (?<col>\d+), (?<severity>Error|Warning) - (?<message>.+?)(?: \((?<rule>[@\w/.-]+)\))?$");

    private static readonly Regex Counted = LinearRegex.Compile(@"^(?:✖ \d+ problems? \(\d+ errors?, \d+ warnings?\)|\s+\d+ errors? and \d+ warnings? potentially fixable with the `--fix` option\.|\d+ problems?)$");

    public string Name => "eslint";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private string? _file;
        private bool _heading;
        private bool _said;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_file is { } file && Problem.Match(text) is { Success: true } problem)
            {
                sink.Add(new Diagnostic(
                    "eslint",
                    problem.Groups["severity"].Value,
                    ParsedText.Text(problem.Groups["rule"]),
                    problem.Groups["message"].Value.Trim(),
                    file,
                    ParsedText.Whole(problem.Groups["line"]),
                    ParsedText.Whole(problem.Groups["col"]),
                    Log: (line.Number, line.Last)));
                _said = true;
                _heading = false;
                return LineTaken.Took;
            }

            var heading = _heading;

            _file = null;
            _heading = false;

            if (heading)
            {
                // A file alone on a line no problem followed was no heading of stylish's: it is given back, and this line
                // offered again on its own.
                return LineTaken.Left;
            }

            if (Compact.Match(text) is { Success: true } compact)
            {
                sink.Add(new Diagnostic(
                    "eslint",
                    compact.Groups["severity"].Value.ToLowerInvariant(),
                    ParsedText.Text(compact.Groups["rule"]),
                    compact.Groups["message"].Value.Trim(),
                    compact.Groups["file"].Value,
                    ParsedText.Whole(compact.Groups["line"]),
                    ParsedText.Whole(compact.Groups["col"]),
                    Log: (line.Number, line.Last)));
                _said = true;
                return LineTaken.Took;
            }

            if (_said && Counted.IsMatch(text))
            {
                return LineTaken.Took;
            }

            if (File.Match(text) is { Success: true } named)
            {
                // A file alone on a line is stylish's heading only where a problem follows it: the next line says.
                _file = named.Groups["file"].Value;
                _heading = true;
                return LineTaken.Held;
            }

            _said &= text.Trim().Length == 0 || Counted.IsMatch(text);
            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            _file = null;
            _heading = false;
            _said = false;
            return LineTaken.Left;
        }
    }
}

/// <summary>
/// Dart's forms: the analyser's - <c>severity - path:line:col - message - code</c>, and its earlier
/// <c>severity • message • path:line:col • code</c> - and the front end's that <c>dart compile</c> and <c>dart test</c>
/// print, <c>path:line:col: Error: message</c>, with the lines under it (where the names it uses are from, the
/// <c>Try ...</c> hint, the source and its carets) as its record and a <c>Context:</c> under it a line of it. The
/// analyser's <c>info</c> and the front end's <c>Info</c> are notes. <c>Error: AOT compilation failed</c> and the
/// analyser's counts are said only where nothing before them said why.
/// </summary>
internal sealed class DartParser : IBuiltInParser
{
    private static readonly Regex Analysed = LinearRegex.Compile(@"^\s*(?<severity>error|warning|info) - (?<file>.+?):(?<line>\d+):(?<col>\d+) - (?<message>.+) - (?<code>[a-z0-9_]+)$");

    private static readonly Regex Bulleted = LinearRegex.Compile(@"^\s*(?<severity>error|warning|info|lint|hint) • (?<message>.+?) • (?<file>.+?):(?<line>\d+):(?<col>\d+) • (?<code>[a-z0-9_]+)$");

    private static readonly Regex Compiled = LinearRegex.Compile(@"^(?<file>.+?):(?<line>\d+):(?<col>\d+): (?<severity>Error|Warning|Info|Context): (?<message>.+)$");

    private static readonly Regex Under = LinearRegex.Compile(@"^(?:\s+\S.*|Try .+)$");

    private static readonly Regex Unplaced = LinearRegex.Compile(@"^Error: (?<message>.+)$");

    private static readonly Regex Failed = LinearRegex.Compile(@"^(?:Error: (?:AOT compilation failed|Compilation failed\.?)|Bad state: Generating AOT kernel dill failed!)$");

    private static readonly Regex Counts = LinearRegex.Compile(@"^(?:Analyzing .+\.\.\.|\d+ issues? found\.|No issues found!)$");

    public string Name => "dart";

    public IResultReading Begin() => new Reading();

    private sealed class Reading : IResultReading
    {
        private Diagnostic? _open;
        private int _to;
        private bool _errorSaid;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            var text = line.Text;

            if (_open is not null)
            {
                if (Compiled.Match(text) is { Success: true } context && context.Groups["severity"].Value == "Context")
                {
                    _to = line.Last;
                    return LineTaken.Took;
                }

                if (Under.IsMatch(text))
                {
                    _to = line.Last;
                    return LineTaken.Took;
                }

                Close(sink);
            }

            if (Analysed.Match(text) is { Success: true } analysed)
            {
                Say(sink, line, analysed, ParsedText.Text(analysed.Groups["code"]));
                return LineTaken.Took;
            }

            if (Bulleted.Match(text) is { Success: true } bulleted)
            {
                Say(sink, line, bulleted, ParsedText.Text(bulleted.Groups["code"]));
                return LineTaken.Took;
            }

            if (Compiled.Match(text) is { Success: true } compiled)
            {
                _open = new Diagnostic(
                    "dart",
                    SeverityOf(compiled.Groups["severity"].Value),
                    null,
                    compiled.Groups["message"].Value.Trim(),
                    compiled.Groups["file"].Value,
                    ParsedText.Whole(compiled.Groups["line"]),
                    ParsedText.Whole(compiled.Groups["col"]),
                    Log: (line.Number, line.Last));
                _to = line.Last;
                return LineTaken.Took;
            }

            if (Failed.IsMatch(text))
            {
                if (_errorSaid)
                {
                    return LineTaken.Took;
                }

                sink.Add(new Diagnostic("dart", "error", null, text.Trim(), Log: (line.Number, line.Last)));
                _errorSaid = true;
                return LineTaken.Took;
            }

            if (Counts.IsMatch(text))
            {
                return LineTaken.Took;
            }

            if (Unplaced.Match(text) is { Success: true } unplaced)
            {
                sink.Add(new Diagnostic("dart", "error", null, unplaced.Groups["message"].Value.Trim(), Log: (line.Number, line.Last)));
                _errorSaid = true;
                return LineTaken.Took;
            }

            return LineTaken.Left;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            return LineTaken.Left;
        }

        private void Say(ResultSink sink, NumberedLine line, Match found, string? code)
        {
            var severity = SeverityOf(found.Groups["severity"].Value);

            sink.Add(new Diagnostic(
                "dart",
                severity,
                code,
                found.Groups["message"].Value.Trim(),
                found.Groups["file"].Value,
                ParsedText.Whole(found.Groups["line"]),
                ParsedText.Whole(found.Groups["col"]),
                Log: (line.Number, line.Last)));
            _errorSaid |= severity == "error";
        }

        private void Close(ResultSink sink)
        {
            if (_open is { } open)
            {
                sink.Add(open with { Log = (open.Log!.Value.From, _to) });
                _errorSaid |= open.Severity == "error";
                _open = null;
            }
        }

        private static string SeverityOf(string said)
            => said.ToLowerInvariant() switch
            {
                "error" => "error",
                "warning" => "warning",
                _ => "note",
            };
    }
}
