using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DssHarness.Core.Results;
using DssHarness.Core.Text;

namespace DssHarness.Core.Configuration;

/// <summary>
/// The <c>results</c> section of <c>config.json</c>: how a phase's or a job's output is digested. Its matchers read
/// tools the built-in parsers do not: one a runner's or a test invocation's <c>matchers</c> key names reads that runner's
/// steps or that invocation's tests, and one no runner or invocation names reads every build, test and runner phase; a
/// CI job's log is read by every matcher.
/// </summary>
public sealed class ResultsConfig
{
    /// <summary>
    /// The problem matchers, each compiled and checked at load: a matcher that does not read refuses the
    /// configuration before anything runs with it.
    /// </summary>
    public List<ProblemMatcher> Matchers { get; init; } = [];
}

/// <summary>
/// One problem matcher of <c>results.matchers</c>: a regex with named groups that reads another tool's diagnostics
/// from its output lines, so no tool is hardcoded. Its shape follows the matchers editors and CI take: a pattern
/// whose named groups say where the file, the line, the column, the code and the message are; an <c>end</c> pattern
/// where a diagnostic runs over the lines after its match; and a <c>stack</c> where the tool prints a chain under it.
/// </summary>
/// <param name="Name">The matcher's name: the tool its diagnostics name, and what a runner's or an invocation's <c>matchers</c> key names it by.</param>
/// <param name="Pattern">The regex, with the named groups it names beside <paramref name="File"/>, <paramref name="Line"/> and the rest.</param>
/// <param name="Severity">
/// The severity every match has (<c>error</c>, <c>warning</c>, <c>note</c>), or a named group that holds it - its text read
/// as editors read it, case ignored (<c>warn</c> a warning, <c>info</c> and <c>hint</c> a note, anything else an error);
/// null for an error.
/// </param>
/// <param name="File">The named group holding the file; null where the tool names none.</param>
/// <param name="Line">The named group holding the line; null where the tool names none.</param>
/// <param name="Column">The named group holding the column; null where the tool names none.</param>
/// <param name="Code">The named group holding the tool's code; null where it gives none.</param>
/// <param name="Message">The named group holding the message; null where the whole match is the message.</param>
/// <param name="End">Where a diagnostic runs over the lines after its match: the regex of its last line, every line to it part of its record; null where it is its one line.</param>
/// <param name="Stack">Where the tool prints a stack under its match: the frame pattern and where the stack ends; null where it prints none.</param>
public sealed record ProblemMatcher(
    string Name,
    string Pattern,
    string? Severity = null,
    string? File = null,
    string? Line = null,
    string? Column = null,
    string? Code = null,
    string? Message = null,
    string? End = null,
    MatcherStack? Stack = null)
{
    /// <summary>The named groups a matcher's frame pattern reads, of which it names at least one.</summary>
    public static readonly IReadOnlyList<string> FrameGroups = ["file", "line", "column", "symbol", "module"];

    /// <summary>
    /// Why this matcher does not read; null where it does. Its patterns are compiled as every pattern the tool runs is -
    /// linear in the text they read, with no timeout (<see cref="LinearRegex"/>) - and one that holds what that matcher
    /// lacks is refused naming it, in the words the compiler gave, as any other pattern a configuration declares is
    /// (<see cref="HarnessConfigValidator.PatternRefused"/>); then the groups it names are checked.
    /// </summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return "a matcher names no name";
        }

        if (PatternRefusal is { } refused)
        {
            return HarnessConfigValidator.PatternRefused($"matcher '{Name}': its pattern", refused);
        }

        var found = Compiled!.GetGroupNames();

        foreach (var named in new[] { File, Line, Column, Code, Message })
        {
            if (named is { } group && !found.Contains(group, StringComparer.Ordinal))
            {
                return $"matcher '{Name}': its pattern names no group '{group}'";
            }
        }

        // The severity is a literal severity or a named group; being the one is being neither.
        if (Severity is { } severity && !Diagnostic.Severities.Contains(severity, StringComparer.Ordinal) && !found.Contains(severity, StringComparer.Ordinal))
        {
            return $"matcher '{Name}': its severity '{severity}' is neither a severity nor a group its pattern names";
        }

        if (EndRefusal is { } endRefused)
        {
            return HarnessConfigValidator.PatternRefused($"matcher '{Name}': its end pattern", endRefused);
        }

        if (Stack is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(Stack.Frame))
        {
            return $"matcher '{Name}': its stack names no frame pattern";
        }

        if (FrameRefusal is { } frameRefused)
        {
            return HarnessConfigValidator.PatternRefused($"matcher '{Name}': its stack's frame pattern", frameRefused);
        }

        if (!FrameCompiled!.GetGroupNames().Any(group => FrameGroups.Contains(group, StringComparer.Ordinal)))
        {
            return $"matcher '{Name}': its stack's frame pattern names none of the groups a frame reads ({string.Join(", ", FrameGroups)})";
        }

        return UntilRefusal is { } untilRefused
            ? HarnessConfigValidator.PatternRefused($"matcher '{Name}': its stack's until pattern", untilRefused)
            : null;
    }

    /// <summary>The matcher's pattern, compiled; null where it is refused, or before <see cref="Compile"/>.</summary>
    [JsonIgnore]
    public Regex? Compiled { get; private set; }

    /// <summary>The matcher's end pattern, compiled; null where it names none, where it is refused, or before <see cref="Compile"/>.</summary>
    [JsonIgnore]
    public Regex? EndCompiled { get; private set; }

    /// <summary>The matcher's frame pattern, compiled; null where it names no stack, where it is refused, or before <see cref="Compile"/>.</summary>
    [JsonIgnore]
    public Regex? FrameCompiled { get; private set; }

    /// <summary>The matcher's until pattern, compiled; null where it names none, where it is refused, or before <see cref="Compile"/>.</summary>
    [JsonIgnore]
    public Regex? UntilCompiled { get; private set; }

    /// <summary>Why the matcher's pattern is refused, in the linear matcher's words; null where it compiled.</summary>
    private string? PatternRefusal { get; set; }

    /// <summary>Why the matcher's end pattern is refused; null where it compiled or there is none.</summary>
    private string? EndRefusal { get; set; }

    /// <summary>Why the matcher's frame pattern is refused; null where it compiled or there is none.</summary>
    private string? FrameRefusal { get; set; }

    /// <summary>Why the matcher's until pattern is refused; null where it compiled or there is none.</summary>
    private string? UntilRefusal { get; set; }

    /// <summary>Compiles the matcher's patterns through the one maker of every pattern (<see cref="LinearRegex"/>); called at load, before any output is read.</summary>
    public void Compile()
    {
        (Compiled, PatternRefusal) = Made(Pattern);
        (EndCompiled, EndRefusal) = End is { } end ? Made(end) : (null, null);
        (FrameCompiled, FrameRefusal) = Stack?.Frame is { Length: > 0 } frame ? Made(frame) : (null, null);
        (UntilCompiled, UntilRefusal) = Stack?.Until is { } until ? Made(until) : (null, null);

        static (Regex? Regex, string? Refusal) Made(string pattern)
            => LinearRegex.TryCompile(pattern, RegexOptions.None, out var regex, out var refusal) ? (regex, null) : (null, refusal);
    }
}

/// <summary>
/// The stack a matcher reads under its match: on the lines after it, each line its frame pattern matches is a frame -
/// its named groups a frame's <c>file</c>, <c>line</c>, <c>column</c>, <c>symbol</c> and <c>module</c> - until a line
/// <c>until</c> matches, which ends it and is part of it, or a line that matches no frame.
/// </summary>
/// <param name="Frame">The regex of one frame.</param>
/// <param name="Until">The regex of the line the stack ends at; null where it ends at the first line no frame matches.</param>
public sealed record MatcherStack(string Frame, string? Until = null);
