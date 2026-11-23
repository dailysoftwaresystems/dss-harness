using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Results;

/// <summary>
/// Which parsers a phase's log is read by: one owner of the choice. A build's by the toolchain the leg resolved -
/// MSVC, gcc or clang each with the CMake and Ninja that drive them and the linker, MSBuild for a build nothing more
/// specific names - a test's by its runner, and a configured matcher everywhere, for <c>results.matchers</c> is the
/// way a tool this build does not know is read. A parser never runs over output it was not chosen for.
/// </summary>
public static class PhaseParsers
{
    /// <summary>The parsers a build phase's log is read by, for the toolchain the leg resolved.</summary>
    /// <param name="toolchain">The toolchain the leg's variant resolved, or null where none did.</param>
    /// <param name="matchers">The configured matchers, which apply to every phase.</param>
    public static IReadOnlyList<IResultParser> ForBuild(string? toolchain, IReadOnlyList<ProblemMatcher> matchers)
        => toolchain switch
        {
            "msvc" => With([ResultParsers.Of("msvc")!, ResultParsers.Of("link")!, ResultParsers.Of("cmake")!, ResultParsers.Of("ninja")!], matchers),
            "gcc" => With([ResultParsers.Of("gcc")!, ResultParsers.Of("link")!, ResultParsers.Of("cmake")!, ResultParsers.Of("ninja")!], matchers),
            "clang" => With([ResultParsers.Of("clang")!, ResultParsers.Of("link")!, ResultParsers.Of("cmake")!, ResultParsers.Of("ninja")!], matchers),
            _ => With([ResultParsers.Of("msbuild")!, ResultParsers.Of("cmake")!, ResultParsers.Of("ninja")!], matchers),
        };

    /// <summary>
    /// The parsers a test phase's log is read by, for the runner the invocation started: its console form where this
    /// build knows it, and the stack form of the runtime its tests run on.
    /// </summary>
    /// <param name="runner">The runner the invocation started, as a path or a name.</param>
    /// <param name="matchers">The configured matchers, which apply to every phase.</param>
    public static IReadOnlyList<IResultParser> ForTests(string runner, IReadOnlyList<ProblemMatcher> matchers)
    {
        var name = Path.GetFileNameWithoutExtension(runner) is { Length: > 0 } file ? file.ToLowerInvariant() : runner.ToLowerInvariant();

        var chosen = name switch
        {
            "ctest" => [ResultParsers.Of("ctest-console")!, ResultParsers.Of("cmake")!],
            "dotnet" => [ResultParsers.Of("xunit-console")!, ResultParsers.Of("dotnet-stack")!],
            "dart" => [ResultParsers.Of("dart-stack")!],
            _ => Array.Empty<IResultParser>(),
        };

        return With([.. chosen], matchers);
    }

    /// <summary>
    /// The digest of a phase's log lines by the parsers chosen for it: every chosen parser reads the same lines, their
    /// findings fold into one, and the unparsed lines are what the parsers themselves left - each parser's own rule of
    /// a line that looks like an error and matched nothing, with a line another parser's diagnostic already covers
    /// taken out, for one finding is said once. Null where no parser was chosen, for a log no rule names is a log left
    /// as it stands.
    /// </summary>
    /// <param name="lines">The phase's output lines, in the order printed.</param>
    /// <param name="parsers">The parsers chosen for the phase.</param>
    public static ResultDigest? DigestOf(IReadOnlyList<string> lines, IReadOnlyList<IResultParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (parsers.Count == 0)
        {
            return null;
        }

        var diagnostics = new List<Diagnostic>();
        var failures = new List<TestFailure>();
        var left = new List<string>();

        foreach (var parser in parsers)
        {
            var (read, failed, held) = parser.Read(lines);

            diagnostics.AddRange(read);
            failures.AddRange(failed);
            left.AddRange(held);
        }

        // The lines a diagnostic's log range covers are said already - one finding, said once, whatever else a
        // parser's own rule made of the same line.
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Log is { } log)
            {
                for (var at = log.From; at <= log.To && at <= lines.Count; at++)
                {
                    covered.Add(lines[at - 1]);
                }
            }
        }

        var unparsed = left.Distinct(StringComparer.Ordinal).Where(line => !covered.Contains(line)).ToList();

        return diagnostics.Count == 0 && failures.Count == 0 && unparsed.Count == 0 ? null : ResultDigest.Of(diagnostics, failures, unparsed);
    }

    private static IReadOnlyList<IResultParser> With(IReadOnlyList<IResultParser> chosen, IReadOnlyList<ProblemMatcher> matchers)
        => [.. chosen, .. matchers.Where(matcher => matcher.Compiled is not null).Select(matcher => new MatcherParser(matcher))];
}

/// <summary>
/// One configured problem matcher as a parser reads: every line its pattern matches is a diagnostic - its severity a
/// literal or a named group, its file, line, column, code and message the groups it names - and where the matcher
/// says a frame pattern, the lines after its match are its stack until its end matches or a line matches no frame.
/// </summary>
internal sealed class MatcherParser(ProblemMatcher matcher) : IResultParser
{
    /// <summary>The matcher's own compiled pattern, for the unparsed rule.</summary>
    public Regex Compiled => matcher.Compiled!;

    public string Name => matcher.Name;

    public (IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<TestFailure> Failures, IReadOnlyList<string> Unparsed) Read(IReadOnlyList<string> lines)
    {
        var diagnostics = new List<Diagnostic>();
        var frames = matcher.FrameCompiled!;

        for (var at = 0; at < lines.Count; at++)
        {
            if (Compiled.Match(lines[at]) is not { Success: true } found)
            {
                continue;
            }

            var stack = new List<StackFrame>();

            if (matcher.Frame is not null)
            {
                for (var inner = at + 1; inner < lines.Count; inner++)
                {
                    var said = lines[inner];

                    if (matcher.UntilCompiled is { } until && until.IsMatch(said))
                    {
                        break;
                    }

                    if (frames.Match(said) is { Success: true } frame)
                    {
                        stack.Add(new(
                            said,
                            frame.Groups["file"].Success ? frame.Groups["file"].Value : null,
                            frame.Groups["line"].Success ? int.Parse(frame.Groups["line"].Value, CultureInfo.InvariantCulture) : null,
                            frame.Groups["column"].Success ? int.Parse(frame.Groups["column"].Value, CultureInfo.InvariantCulture) : null,
                            frame.Groups["module"].Success ? frame.Groups["module"].Value : null,
                            frame.Groups["symbol"].Success ? frame.Groups["symbol"].Value : null));
                        at = inner;
                        continue;
                    }

                    break;
                }
            }

            string Group(string? name)
                => name is not null && found.Groups[name].Success ? found.Groups[name].Value : string.Empty;

            var severity = matcher.Severity is { } literal && Diagnostic.Severities.Contains(literal, StringComparer.Ordinal)
                ? literal
                : Group(matcher.Severity) is { Length: > 0 } byGroup ? byGroup : "error";

            diagnostics.Add(new(
                matcher.Name,
                severity,
                Group(matcher.Code) is { Length: > 0 } code ? code : null,
                Group(matcher.Message) is { Length: > 0 } message ? message : found.Value,
                Group(matcher.File) is { Length: > 0 } file ? file : null,
                int.TryParse(Group(matcher.Line), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) ? line : null,
                int.TryParse(Group(matcher.Column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) ? column : null,
                Stack: stack,
                Log: (at + 1, at + 1)));
        }

        return (diagnostics, [], []);
    }
}
