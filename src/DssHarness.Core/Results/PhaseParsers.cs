using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;
using DssHarness.Core.Text;

namespace DssHarness.Core.Results;

/// <summary>
/// Which parsers a phase's log is read by, and the one pass that reads it: one owner of each. A build's parsers are
/// chosen by what the leg builds - its project type, and for a CMake build the compiler its toolchain declares
/// (<see cref="ToolchainConfig.CompilerId"/>) - a test's by its runner, a runner step's and a CI job's by every form
/// their output may hold. A configured matcher reads the phases of the runners and invocations that name it, and one
/// no runner or invocation names reads every phase: a parser never reads output it was not chosen for.
/// </summary>
public static class PhaseParsers
{
    /// <summary>The stack forms of every runtime, for output whose program is not known from the outside.</summary>
    private static readonly string[] StackPlan = ["dotnet-stack", "jvm-stack", "python-stack", "v8-stack", "dart-stack", "go-panic", "rust-panic", "sanitizer", "backtrace"];

    /// <summary>
    /// Every form a CI job's log may hold - the test consoles, the build tools and the stack forms: a job holds whatever
    /// steps its workflow wrote.
    /// </summary>
    private static readonly string[] CiPlan =
    [
        "dotnet-test", "gtest", "ctest", "dart-test", "jest", "vitest",
        "msbuild", "cc", "ld", "cmake", "ninja", "tsc", "eslint", "dart",
        .. StackPlan,
    ];

    /// <summary>
    /// The parsers a build phase's log is read by: MSBuild's for a .NET build, the Dart tools' for a Dart one, and for a
    /// CMake build the compiler's its toolchain declares - MSVC's canonical form with CMake and Ninja, gcc's or clang's
    /// with the linker, CMake and Ninja - or, where it declares none or more than one, every C compiler's form, the
    /// diagnostics said as the C compiler's (<c>cc</c>). The configured matchers no runner or invocation names.
    /// </summary>
    /// <param name="config">The configuration the build runs under.</param>
    /// <param name="projectType">The project's type: <c>cmake</c>, <c>dotnet</c> or <c>dart</c>.</param>
    /// <param name="toolchain">The toolchain the leg's variant resolved, or null where none did.</param>
    public static IReadOnlyList<IResultParser> ForBuild(HarnessConfig config, string projectType, string? toolchain)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(projectType);

        string[] plan = projectType switch
        {
            "dotnet" => ["msbuild"],
            "dart" => ["dart"],
            _ => CompilerPlan(toolchain is not null && config.Toolchains.TryGetValue(toolchain, out var declared) ? declared.CompilerId.Values : []),
        };

        return Chosen(plan, MatchersFor(config, []));
    }

    /// <summary>
    /// The parsers a test phase's log is read by, for the runner the invocation started: its console's form and the
    /// stack form of the runtime its tests run on - CTest's with GoogleTest's console, the sanitizers' reports and
    /// CMake's (which CTest prints where a test's script fails); <c>dotnet test</c>'s with .NET's stacks and MSBuild's,
    /// for it builds before it runs; <c>dart test</c>'s and <c>flutter test</c>'s with Dart's stacks and its compiler's;
    /// Python's, Node's (jest, vitest, V8's stacks, tsc, eslint), Go's, Rust's and the JVM's. A runner none of these is
    /// - a test binary started directly, a script - is read by GoogleTest's console and every runtime's stack form. The
    /// configured matchers the invocation names, and those no runner or invocation names.
    /// </summary>
    /// <param name="config">The configuration the tests run under.</param>
    /// <param name="runner">The runner the invocation started, as a path or a name.</param>
    /// <param name="matchers">The matchers the invocation names; null or empty where it names none.</param>
    public static IReadOnlyList<IResultParser> ForTests(HarnessConfig config, string runner, IReadOnlyList<string>? matchers)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(runner);

        string[] plan = ProgramOf(runner) switch
        {
            "ctest" => ["ctest", "gtest", "sanitizer", "backtrace", "cmake"],
            "dotnet" => ["dotnet-test", "dotnet-stack", "msbuild"],
            "dart" or "flutter" => ["dart-test", "dart-stack", "dart"],
            "python" or "py" or "pytest" => ["python-stack"],
            "node" or "npm" or "npx" or "yarn" or "pnpm" or "jest" or "vitest" => ["jest", "vitest", "v8-stack", "tsc", "eslint"],
            "go" => ["go-panic"],
            "cargo" => ["rust-panic"],
            "mvn" or "mvnw" or "gradle" or "gradlew" or "java" => ["jvm-stack"],
            _ => ["gtest", .. StackPlan],
        };

        return Chosen(plan, MatchersFor(config, matchers ?? []));
    }

    /// <summary>
    /// The parsers a runner step's log is read by: every runtime's stack form, for the program a step starts is not known
    /// from the outside; the configured matchers the runner names, and those no runner or invocation names.
    /// </summary>
    /// <param name="config">The configuration the runner runs under.</param>
    /// <param name="runner">The runner.</param>
    public static IReadOnlyList<IResultParser> ForRun(HarnessConfig config, RunnerConfig runner)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(runner);

        return Chosen(StackPlan, MatchersFor(config, runner.Matchers ?? []));
    }

    /// <summary>
    /// The parsers a CI job's log is read by: its steps are not known from the outside - a job holds whatever steps the
    /// workflow wrote - so every built-in form a log holds is chosen, the test consoles first, then the build tools and
    /// every runtime's stack form, and every configured matcher. The forge's own annotations are no form of the log's.
    /// </summary>
    /// <param name="matchers">The configured matchers: a CI job's log is read by every one.</param>
    public static IReadOnlyList<IResultParser> ForCi(IReadOnlyList<ProblemMatcher> matchers)
    {
        ArgumentNullException.ThrowIfNull(matchers);

        return Chosen(CiPlan, matchers);
    }

    /// <summary>
    /// The digest of a log's lines, numbered from 1 as they stream, by the parsers chosen for it
    /// (<see cref="DigestOf(IEnumerable{NumberedLine}, IReadOnlyList{IResultParser})"/>).
    /// </summary>
    /// <param name="lines">The log's lines, in the order printed.</param>
    /// <param name="parsers">The parsers chosen for it.</param>
    public static ResultDigest? DigestOf(IEnumerable<string> lines, IReadOnlyList<IResultParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return DigestOf(Numbered(lines), parsers);
    }

    /// <summary>
    /// The digest of a log by the parsers chosen for it (<see cref="FindingsOf"/>), made as it streams by the one owner of
    /// a digest's bounds (<see cref="ResultDigest.Gathering"/>): each line no parser took that looks like an error is
    /// gathered as the pass meets it - every one counted, the first <see cref="ResultDigest.Bound"/> held - and the
    /// findings, merged in the order of their lines, after it. Null where no parser was chosen, or where the log held
    /// nothing any of them said and nothing that looks like an error.
    /// </summary>
    /// <param name="lines">The log's lines, in the order printed, each with the numbers its file gives it.</param>
    /// <param name="parsers">The parsers chosen for it.</param>
    public static ResultDigest? DigestOf(IEnumerable<NumberedLine> lines, IReadOnlyList<IResultParser> parsers)
    {
        var gathering = new ResultDigest.Gathering();

        if (FindingsOf(lines, parsers, line => gathering.Unparsed(line.Text)) is not { } found)
        {
            return null;
        }

        foreach (var diagnostic in found.Diagnostics)
        {
            gathering.Add(diagnostic);
        }

        foreach (var failure in found.Failures)
        {
            gathering.Add(failure);
        }

        var digest = gathering.Digest();

        return digest.HoldsAFinding || digest.UnparsedCount > 0 ? digest : null;
    }

    /// <summary>
    /// What the parsers chosen for a log found in it, read in one pass as it streams - never held whole. Each line, its
    /// terminal escapes taken off, is offered first to the reading that has a record open, then to each chosen parser's
    /// reading in the order chosen, until one takes it or holds it undecided; a reading that gives held lines back has
    /// them offered to the others, never to itself again, and the line that ended its hold offered again as a line of
    /// its own - to it as well, for that line may begin its next record - so every line is one reading's at most and is
    /// read a bounded number of times. The findings of every reading are merged in the order of the lines they begin at, each with its
    /// log range as the file numbers its lines. A line no reading took that holds an error's marker - <c>error:</c>,
    /// <c>error CODE:</c>, <c>FAILED</c>, <c>fatal:</c>, <c>panic:</c>, a thrown error's <c>Type: </c> - is handed to
    /// <paramref name="unparsed"/> as the pass meets it, every one, for the digest's owner to count and bound: what the
    /// parsers did not understand. Null where no parser was chosen, for a log no rule names is a log left as it stands.
    /// </summary>
    /// <param name="lines">The log's lines, in the order printed, each with the numbers its file gives it.</param>
    /// <param name="parsers">The parsers chosen for it.</param>
    /// <param name="unparsed">Takes each line no reading took that looks like an error, in the order the pass met it.</param>
    public static PhaseFindings? FindingsOf(IEnumerable<NumberedLine> lines, IReadOnlyList<IResultParser> parsers, Action<NumberedLine> unparsed)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(parsers);
        ArgumentNullException.ThrowIfNull(unparsed);

        if (parsers.Count == 0)
        {
            return null;
        }

        var pass = new OnePass(parsers, unparsed);

        foreach (var line in lines)
        {
            pass.Read(line with { Text = AsRead(line.Text) });
        }

        return pass.Finish();
    }

    /// <summary>
    /// A log's line as the pass reads it (<see cref="FindingsOf"/>) - its terminal escapes taken off, and a carriage return
    /// at its end - so a raw line handed beside a digest is matched to the digest's own (<see cref="ResultDigest.Handed"/>).
    /// </summary>
    /// <param name="line">The line, as the log holds it.</param>
    public static string AsRead(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return TerminalText.Plain(line).TrimEnd('\r');
    }

    /// <summary>Whether <paramref name="line"/> holds an error's marker, as a line no parser took is judged.</summary>
    /// <param name="line">The line, its terminal escapes taken off.</param>
    internal static bool LooksLikeError(string line) => ErrorMarker.IsMatch(line);

    /// <summary>
    /// The markers of an error, as words and never as parts of a name: <c>error:</c>, <c>Error:</c> and <c>error CODE:</c>
    /// after a separator; <c>FAILED</c>; <c>fatal:</c> and <c>fatal error:</c>; a <c>panic:</c> that begins its line; a
    /// thrown error's <c>TypeError: </c> or <c>SomeException: </c>; and Python's traceback's head.
    /// </summary>
    private static readonly Regex ErrorMarker = LinearRegex.Compile(
        @"(?:^|[\s:=\[\](])(?:error|Error|ERROR)(?: [A-Z]+\d+)?:|\bFAILED\b|(?:^|[\s:=])(?:fatal|Fatal|FATAL)(?: error)?:|^panic: |\w(?:Error|Exception)(?: \[[A-Z0-9_]+\])?: |^Traceback \(most recent call last\):$");

    /// <summary>The parsers a CMake build's log is read by, for the compiler ids its toolchain declares.</summary>
    /// <param name="ids">The CMake compiler ids the toolchain declares, by language.</param>
    private static string[] CompilerPlan(IEnumerable<string> ids)
    {
        var declared = ids.Distinct(StringComparer.Ordinal).ToList();

        return declared switch
        {
            ["MSVC"] => ["msbuild", "cmake", "ninja"],
            ["GNU"] => ["gcc", "ld", "cmake", "ninja"],
            _ when declared.Count > 0 && declared.All(id => id is "Clang" or "AppleClang") => ["clang", "ld", "cmake", "ninja"],
            _ => ["msbuild", "cc", "ld", "cmake", "ninja"],
        };
    }

    /// <summary>
    /// The program a runner starts, as its name is matched: the last part of its path, either separator, lower-cased,
    /// with a launcher's extension (<c>.exe</c>, <c>.cmd</c>, <c>.bat</c>, <c>.ps1</c>, <c>.sh</c>) taken off and a
    /// Python's version (<c>python3.12</c>) read as <c>python</c>.
    /// </summary>
    /// <param name="runner">The runner as an invocation names it.</param>
    internal static string ProgramOf(string runner)
    {
        var name = runner[(runner.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();

        foreach (var extension in (string[])[".exe", ".cmd", ".bat", ".ps1", ".sh"])
        {
            if (name.EndsWith(extension, StringComparison.Ordinal))
            {
                name = name[..^extension.Length];
                break;
            }
        }

        return name.StartsWith("python", StringComparison.Ordinal) && name["python".Length..].All(character => char.IsAsciiDigit(character) || character == '.') ? "python" : name;
    }

    /// <summary>
    /// The configured matchers a phase is read by: those <paramref name="named"/> names, and every one no runner or test
    /// invocation of the configuration names.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="named">The matchers the phase's runner or invocation names.</param>
    private static IEnumerable<ProblemMatcher> MatchersFor(HarnessConfig config, IReadOnlyCollection<string> named)
    {
        var namedAnywhere = config.PredefinedRunners.Values.SelectMany(runner => runner.Matchers ?? [])
            .Concat(config.Projects.Select(project => project.Test).Concat(config.Legs.Values.Select(leg => leg.Test))
                .OfType<TestConfig>()
                .SelectMany(test => new[] { test.All, test.Windows, test.Linux, test.Macos })
                .OfType<TestInvocation>()
                .SelectMany(invocation => invocation.Matchers ?? []))
            .ToHashSet(StringComparer.Ordinal);

        return config.Results.Matchers.Where(matcher => named.Contains(matcher.Name, StringComparer.Ordinal) || !namedAnywhere.Contains(matcher.Name));
    }

    /// <summary>The built-in parsers <paramref name="plan"/> names, in its order, then a parser of each compiled matcher.</summary>
    private static IReadOnlyList<IResultParser> Chosen(IEnumerable<string> plan, IEnumerable<ProblemMatcher> matchers)
        => [.. plan.Select(ResultParsers.Named), .. matchers.Where(matcher => matcher.Compiled is not null).Select(matcher => new MatcherParser(matcher))];

    /// <summary>The lines numbered from 1, in the order they stream.</summary>
    /// <param name="lines">The lines.</param>
    internal static IEnumerable<NumberedLine> Numbered(IEnumerable<string> lines)
    {
        var number = 0;

        foreach (var line in lines)
        {
            number++;
            yield return new NumberedLine(number, number, line);
        }
    }

    /// <summary>
    /// One pass of the chosen parsers over one log: the reading with a record open, the lines it holds undecided, the
    /// lines given back and waiting to be offered again, and where the lines no reading took that look like errors go.
    /// </summary>
    private sealed class OnePass
    {
        private readonly IResultReading[] _readings;
        private readonly ResultSink[] _sinks;
        private readonly List<Offered> _held = [];
        private readonly Stack<Offered> _given = new();
        private readonly Action<NumberedLine> _unparsed;
        private int _active = -1;

        public OnePass(IReadOnlyList<IResultParser> parsers, Action<NumberedLine> unparsed)
        {
            var shared = new SharedReading();

            _readings = [.. parsers.Select(parser => parser.Begin())];
            _sinks = [.. parsers.Select(_ => new ResultSink(shared))];
            _unparsed = unparsed;
        }

        /// <summary>Reads the next line of the log.</summary>
        public void Read(NumberedLine line)
        {
            Offer(new Offered(line, null));
            OfferGiven();
        }

        /// <summary>
        /// The log ended: a reading that holds lines undecided says whether they are its - lines it gives back are offered
        /// to the others, never to it again - and then every reading not yet ended finishes what it has open, the last
        /// chosen first, so a reading chosen ahead of the forms it sums up (a runner's console ahead of its tests' own)
        /// ends knowing what they said, whichever of them read the last line. Each reading ends once.
        /// </summary>
        public PhaseFindings Finish()
        {
            var ended = new bool[_readings.Length];

            while (_active >= 0 && _held.Count > 0)
            {
                var ending = _active;

                ended[ending] = true;

                if (_readings[ending].End(_sinks[ending]) == LineTaken.Left && _held.Count > 0)
                {
                    GiveBack(null);
                    OfferGiven();
                }
                else
                {
                    _held.Clear();
                    _active = -1;
                }
            }

            for (var at = _readings.Length - 1; at >= 0; at--)
            {
                if (!ended[at])
                {
                    _readings[at].End(_sinks[at]);
                }
            }

            var diagnostics = InLogOrder(_sinks.Select(sink => (IReadOnlyList<Diagnostic>)sink.Diagnostics), diagnostic => diagnostic.Log);
            var failures = InLogOrder(_sinks.Select(sink => (IReadOnlyList<TestFailure>)sink.Failures), failure => failure.Log);

            return new PhaseFindings(diagnostics, failures);
        }

        /// <summary>
        /// Offers one line: to the reading with a record open where it has not refused the line, and otherwise to every
        /// reading that has not, in the order chosen, until one takes it or holds it.
        /// </summary>
        private void Offer(Offered offered)
        {
            if (_active >= 0)
            {
                var taken = offered.Refused?.Contains(_active) == true
                    ? LineTaken.Left
                    : _readings[_active].Offer(offered.Line, _sinks[_active]);

                switch (taken)
                {
                    case LineTaken.Took:
                        _held.Clear();
                        return;
                    case LineTaken.Held:
                        _held.Add(offered);
                        return;
                    default:
                        GiveBack(offered, ended: _held.Count > 0);
                        return;
                }
            }

            for (var at = 0; at < _readings.Length; at++)
            {
                if (offered.Refused?.Contains(at) == true)
                {
                    continue;
                }

                switch (_readings[at].Offer(offered.Line, _sinks[at]))
                {
                    case LineTaken.Took:
                        _active = at;
                        return;
                    case LineTaken.Held:
                        _active = at;
                        _held.Add(offered);
                        return;
                }
            }

            if (LooksLikeError(offered.Line.Text))
            {
                _unparsed(offered.Line);
            }
        }

        /// <summary>
        /// The reading with a record open gave back what it held, and <paramref name="current"/> with it: each line waits to
        /// be offered again, in the order printed and ahead of every line after it. A held line is refused by that reading;
        /// so is <paramref name="current"/>, unless it <paramref name="ended"/> a hold - the line that says the held lines
        /// were not the reading's says nothing of itself, and may begin the reading's next record, so it is offered again as a
        /// line of its own, to that reading as well.
        /// </summary>
        private void GiveBack(Offered? current, bool ended = false)
        {
            var refusing = _active;
            var held = _held.ToArray();

            _held.Clear();
            _active = -1;

            if (current is { } line)
            {
                _given.Push(ended ? line : line with { Refused = [.. line.Refused ?? [], refusing] });
            }

            for (var at = held.Length - 1; at >= 0; at--)
            {
                _given.Push(held[at] with { Refused = [.. held[at].Refused ?? [], refusing] });
            }
        }

        /// <summary>Offers again every line given back, in the order printed.</summary>
        private void OfferGiven()
        {
            while (_given.TryPop(out var again))
            {
                Offer(again);
            }
        }

        /// <summary>
        /// The findings of every reading in the order of the lines they begin at: the readings' own orders kept where two
        /// begin at one line, and a finding read from no line after every one that was.
        /// </summary>
        private static List<T> InLogOrder<T>(IEnumerable<IReadOnlyList<T>> read, Func<T, (int From, int To)?> log)
            => [.. read
                .SelectMany((found, reading) => found.Select((finding, order) => (Finding: finding, Reading: reading, Order: order)))
                .OrderBy(entry => log(entry.Finding)?.From ?? int.MaxValue)
                .ThenBy(entry => entry.Reading)
                .ThenBy(entry => entry.Order)
                .Select(entry => entry.Finding)];

        /// <summary>A line offered, with the readings that refused it - those that gave it back.</summary>
        private readonly record struct Offered(NumberedLine Line, HashSet<int>? Refused);
    }
}

/// <summary>
/// What the parsers chosen for a log found in it (<see cref="PhaseParsers.FindingsOf"/>), before a digest folds and
/// bounds them: what a producer that reads more than the log - a forge's annotations beside a job's log - merges with
/// before the one digest is made of them all. The lines no parser took went where the pass was told, as it met them.
/// </summary>
/// <param name="Diagnostics">The diagnostics, in the order of the lines they begin at, each with its log range as the file numbers its lines.</param>
/// <param name="Failures">The test failures, in the order of the lines they begin at.</param>
public sealed record PhaseFindings(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures);

/// <summary>
/// One configured problem matcher as a parser reads: a line its pattern matches is a diagnostic - its severity a literal,
/// or a named group's text mapped as editors map it, its file, line, column, code and message the groups it names - with,
/// where the matcher says <c>end</c>, the lines after it to the one <c>end</c> matches as part of its record, and where it
/// says <c>stack</c>, the lines after its match its frames, until <c>until</c> matches or a line matches no frame. Its log
/// range runs from its match to the last line it read.
/// </summary>
internal sealed class MatcherParser(ProblemMatcher matcher) : IResultParser
{
    public string Name => matcher.Name;

    public IResultReading Begin() => new Reading(matcher);

    /// <summary>
    /// The severity a group's text says, as editors' problem matchers read it, case ignored: <c>error</c>, <c>err</c>,
    /// <c>fatal</c>, <c>fatal error</c> and <c>critical</c> an error; <c>warning</c> and <c>warn</c> a warning; <c>note</c>,
    /// <c>info</c>, <c>information</c>, <c>hint</c> and <c>notice</c> a note; any other text an error, as an editor reads it.
    /// </summary>
    /// <param name="said">The group's text.</param>
    internal static string SeverityOf(string said)
        => said.Trim().ToLowerInvariant() switch
        {
            "warning" or "warn" => "warning",
            "note" or "info" or "information" or "hint" or "notice" => "note",
            _ => "error",
        };

    private sealed class Reading(ProblemMatcher matcher) : IResultReading
    {
        private Diagnostic? _open;
        private readonly List<StackFrame> _frames = [];
        private bool _ending;
        private int _to;

        public LineTaken Offer(NumberedLine line, ResultSink sink)
        {
            if (_open is not null)
            {
                if (_ending)
                {
                    _to = line.Last;
                    _ending = !matcher.EndCompiled!.IsMatch(line.Text);
                    return LineTaken.Took;
                }

                if (matcher.UntilCompiled?.IsMatch(line.Text) == true)
                {
                    _to = line.Last;
                    Close(sink);
                    return LineTaken.Took;
                }

                if (matcher.Stack is not null && FrameOf(line.Text) is { } frame)
                {
                    _frames.Add(frame);
                    _to = line.Last;
                    return LineTaken.Took;
                }

                Close(sink);
            }

            if (matcher.Compiled!.Match(line.Text) is not { Success: true } found)
            {
                return LineTaken.Left;
            }

            string? Group(string? name)
                => name is not null && found.Groups[name] is { Success: true } group && group.Value.Trim().Length > 0 ? group.Value.Trim() : null;

            var severity = matcher.Severity is null ? "error"
                : Diagnostic.Severities.Contains(matcher.Severity, StringComparer.Ordinal) ? matcher.Severity
                : Group(matcher.Severity) is { } said ? SeverityOf(said)
                : "error";

            _open = new Diagnostic(
                matcher.Name,
                severity,
                Group(matcher.Code),
                Group(matcher.Message) ?? found.Value.Trim(),
                Group(matcher.File),
                Whole(Group(matcher.Line)),
                Whole(Group(matcher.Column)),
                Log: (line.Number, line.Last));
            _to = line.Last;
            _ending = matcher.EndCompiled is not null;

            if (!_ending && matcher.Stack is null)
            {
                Close(sink);
            }

            return LineTaken.Took;
        }

        public LineTaken End(ResultSink sink)
        {
            Close(sink);
            return LineTaken.Took;
        }

        private StackFrame? FrameOf(string text)
        {
            if (matcher.FrameCompiled!.Match(text) is not { Success: true } frame)
            {
                return null;
            }

            string? Group(string name) => frame.Groups[name] is { Success: true } group && group.Value.Trim().Length > 0 ? group.Value.Trim() : null;

            return new StackFrame(text.Trim(), Group("file"), Whole(Group("line")), Whole(Group("column")), Group("module"), Group("symbol"));
        }

        private void Close(ResultSink sink)
        {
            if (_open is { } open)
            {
                sink.Add(open with { Stack = [.. _frames], Log = (open.Log!.Value.From, _to) });
            }

            _open = null;
            _frames.Clear();
            _ending = false;
        }

        /// <summary>A line or a column as the matcher's group captured it: a whole number from 1, or none.</summary>
        private static int? Whole(string? said)
            => said is not null && int.TryParse(said, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }
}
