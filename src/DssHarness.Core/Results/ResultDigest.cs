using System.Globalization;

namespace DssHarness.Core.Results;

/// <summary>
/// One diagnostic a compiler, a build tool or a linter printed: what is wrong, where, how bad it is, and the chain
/// that led to it - typed once so a model reads the error and never searches a log for it. The path is relative to
/// the tree where the file is inside it; the symbol is the declaration the graph finds at that line, where it holds
/// the file.
/// </summary>
/// <param name="Tool">The tool that printed it: <c>msbuild</c>, <c>csc</c>, <c>msvc</c>, <c>gcc</c>, <c>clang</c>, <c>cmake</c>, <c>ninja</c>, <c>link</c>, <c>ld</c>, <c>tsc</c>, <c>eslint</c>, <c>dart</c>, or a configured matcher's name.</param>
/// <param name="Severity">How bad it is: <c>error</c>, <c>warning</c> or <c>note</c>.</param>
/// <param name="Code">The tool's code for it (<c>CS0103</c>, <c>C2065</c>, <c>-Wunused-variable</c>, <c>TS2304</c>, <c>LNK2019</c>); null where the tool gives none.</param>
/// <param name="Message">The message, one line, masked; never empty.</param>
/// <param name="File">The file it names, relative to the tree where it is inside it; null where the tool names none.</param>
/// <param name="Line">The line it names; null where it names none.</param>
/// <param name="Column">The column it names; null where it names none.</param>
/// <param name="EndLine">The last line it names, where the tool gives a range; null where it does not.</param>
/// <param name="EndColumn">The last column it names, where the tool gives a range; null where it does not.</param>
/// <param name="Symbol">The declaration enclosing that line, from the graph, where it holds the file: its class, its method, the node's handle; null where the graph does not.</param>
/// <param name="Project">The project or target the tool named; null where it names none.</param>
/// <param name="Count">How many times the same diagnostic was printed - once per translation unit or per target framework, folded into one; 1 where it was printed once.</param>
/// <param name="Stack">The chain the tool printed that led to it, as frames, innermost first; empty where it printed none, as MSBuild's own errors are.</param>
/// <param name="Log">The line range of the log it was read from (from, to), so the raw text is one read away; null where it was read from no log.</param>
public sealed record Diagnostic(
    string Tool,
    string Severity,
    string? Code,
    string Message,
    string? File = null,
    int? Line = null,
    int? Column = null,
    int? EndLine = null,
    int? EndColumn = null,
    string? Symbol = null,
    string? Project = null,
    int Count = 1,
    IReadOnlyList<StackFrame>? Stack = null,
    (int From, int To)? Log = null)
{
    /// <summary>The severities a diagnostic has.</summary>
    public static readonly IReadOnlyList<string> Severities = ["error", "warning", "note"];

    /// <summary>What is wrong with this diagnostic; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Tool) ? "a diagnostic names no tool"
            : !Severities.Contains(Severity, StringComparer.Ordinal) ? $"'{Severity}' is not a severity a diagnostic has"
            : string.IsNullOrWhiteSpace(Message) ? "a diagnostic says no message"
            : Line is < 1 ? $"a diagnostic names line {Line?.ToString(CultureInfo.InvariantCulture)}"
            : Column is < 1 ? $"a diagnostic names column {Column?.ToString(CultureInfo.InvariantCulture)}"
            : Count < 1 ? $"a diagnostic was printed {Count.ToString(CultureInfo.InvariantCulture)} times"
            : (Stack ?? []).Any(frame => frame.Problem() is { } wrong) ? (Stack ?? []).First(frame => frame.Problem() is not null).Problem()
            : null;

    /// <summary>Whether this and <paramref name="other"/> are the same diagnostic printed again - the same tool, code, message and place.</summary>
    /// <param name="other">The other printing.</param>
    public bool SameAs(Diagnostic other)
        => string.Equals(Tool, other.Tool, StringComparison.Ordinal)
            && string.Equals(Code, other.Code, StringComparison.Ordinal)
            && string.Equals(Message, other.Message, StringComparison.Ordinal)
            && string.Equals(File, other.File, StringComparison.Ordinal)
            && Line == other.Line
            && Column == other.Column;
}

/// <summary>
/// One test that failed: its identity as the runner gives it, the assertion as the framework reports it, and - where
/// the failure is a thrown error - the exception with its stack and its causes, typed once for every framework.
/// </summary>
/// <param name="Framework">The framework that ran it: <c>xunit</c>, <c>mstest</c>, <c>nunit</c>, <c>gtest</c>, <c>catch2</c>, <c>ctest</c>, <c>dart</c>, <c>jest</c>, <c>vitest</c>, <c>junit</c>, or a matcher's name.</param>
/// <param name="Id">The test's id as the runner gives it (its fully-qualified name or its filter form); never empty.</param>
/// <param name="Name">The test's own name; null where the runner gives only the id.</param>
/// <param name="Class">The test's class or suite; null where the runner gives none.</param>
/// <param name="Method">The test's method; null where the runner gives none.</param>
/// <param name="Message">The assertion's message as the framework reports it, masked; null where it gives none.</param>
/// <param name="Expected">What was expected, where the framework says it apart; null where it does not.</param>
/// <param name="Actual">What there was, where the framework says it apart; null where it does not.</param>
/// <param name="File">The assertion's place, or the top frame inside the tree; null where neither names one.</param>
/// <param name="Line">The assertion's line; null where none was named.</param>
/// <param name="Exception">Where the failure is a thrown error: its record; null for a plain assertion.</param>
/// <param name="Stack">The stack trace as the runner printed it, in the order it printed it, every frame marked <see cref="StackFrame.InTree"/>; empty where none was printed.</param>
/// <param name="Duration">How long the test took, in seconds; null where the runner does not say.</param>
/// <param name="Outcome">How it failed: <c>failed</c>, <c>errored</c>, <c>timedOut</c> or <c>crashed</c>.</param>
/// <param name="Shard">The shard that ran it as <c>i</c> of <c>n</c>; null where the leg ran unsharded.</param>
public sealed record TestFailure(
    string Framework,
    string Id,
    string? Name = null,
    string? Class = null,
    string? Method = null,
    string? Message = null,
    string? Expected = null,
    string? Actual = null,
    string? File = null,
    int? Line = null,
    ExceptionRecord? Exception = null,
    IReadOnlyList<StackFrame>? Stack = null,
    double? Duration = null,
    string Outcome = "failed",
    (int Of, int In)? Shard = null)
{
    /// <summary>The outcomes a failure has.</summary>
    public static readonly IReadOnlyList<string> Outcomes = ["failed", "errored", "timedOut", "crashed"];

    /// <summary>What is wrong with this failure; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Framework) ? "a failure names no framework"
            : string.IsNullOrWhiteSpace(Id) ? "a failure names no test"
            : Line is < 1 ? $"a failure names line {Line?.ToString(CultureInfo.InvariantCulture)}"
            : !Outcomes.Contains(Outcome, StringComparer.Ordinal) ? $"'{Outcome}' is not an outcome a failure has"
            : Exception?.Problem() is { } wrong ? wrong
            : (Stack ?? []).Any(frame => frame.Problem() is { } frameWrong) ? (Stack ?? []).First(frame => frame.Problem() is not null).Problem()
            : null;
}

/// <summary>
/// What a phase's or a job's output digested to: the diagnostics and the test failures, typed, bounded, with the
/// counts of each, the first error in the order the tool met it, and whether output the parsers did not understand
/// remains. Nothing in a digest is a guess: what no parser held stays <paramref name="Unparsed"/>'s lines.
/// </summary>
/// <param name="Diagnostics">The diagnostics, in the order the tool met them, folded once each, the first 50.</param>
/// <param name="Failures">The test failures, in the order the runner met them, the first 50.</param>
/// <param name="Errors">How many diagnostics of severity <c>error</c> the whole output printed, folded ones counted once each.</param>
/// <param name="Warnings">How many of severity <c>warning</c>.</param>
/// <param name="Notes">How many of severity <c>note</c>.</param>
/// <param name="Failed">How many test failures of outcome <c>failed</c>.</param>
/// <param name="Errored">How many of outcome <c>errored</c>.</param>
/// <param name="TimedOut">How many of outcome <c>timedOut</c>.</param>
/// <param name="Crashed">How many of outcome <c>crashed</c>.</param>
/// <param name="First">The first error in the order the tool met it - the one a reader starts from; null where there was none.</param>
/// <param name="Unparsed">The lines that look like errors and matched nothing, at most 50, masked - what the parsers did not understand; empty where they understood everything.</param>
public sealed record ResultDigest(
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<TestFailure> Failures,
    int Errors,
    int Warnings,
    int Notes,
    int Failed,
    int Errored,
    int TimedOut,
    int Crashed,
    Diagnostic? First = null,
    IReadOnlyList<string>? Unparsed = null)
{
    /// <summary>How many diagnostics and failures a digest holds.</summary>
    public const int Bound = 50;

    /// <summary>What is wrong with this digest; null when nothing is.</summary>
    public string? Problem()
        => Diagnostics.Any(diagnostic => diagnostic.Problem() is { } wrong) ? Diagnostics.First(diagnostic => diagnostic.Problem() is not null).Problem()
            : Failures.Any(failure => failure.Problem() is { } wrong) ? Failures.First(failure => failure.Problem() is not null).Problem()
            : First is { } first && !Diagnostics.Contains(first) ? "its first error is not one of its diagnostics"
            : Diagnostics.Count > Bound || Failures.Count > Bound ? $"it holds over {Bound.ToString(CultureInfo.InvariantCulture)} diagnostics or failures"
            : null;

    /// <summary>Nothing, digested: the empty digest an output with nothing to parse answers.</summary>
    public static ResultDigest None { get; } = new([], [], 0, 0, 0, 0, 0, 0, 0, null, []);

    /// <summary>
    /// The digest of <paramref name="diagnostics"/> and <paramref name="failures"/>: each folded once, held to
    /// <see cref="Bound"/>, the counts over the whole of them, the first error in the order met, and
    /// <paramref name="unparsed"/> as given.
    /// </summary>
    /// <param name="diagnostics">The diagnostics in the order the tool met them.</param>
    /// <param name="failures">The failures in the order the runner met them.</param>
    /// <param name="unparsed">The lines no parser held.</param>
    public static ResultDigest Of(IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<TestFailure> failures, IReadOnlyList<string> unparsed)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(unparsed);

        // The same diagnostic printed again - once per translation unit or per target framework - is one entry with
        // its count, the first place it named.
        var folded = new List<Diagnostic>();

        foreach (var diagnostic in diagnostics)
        {
            var same = folded.FirstOrDefault(held => held.SameAs(diagnostic));

            if (same is { } again)
            {
                folded.Remove(again);
                folded.Add(again with { Count = again.Count + 1 });
            }
            else
            {
                folded.Add(diagnostic);
            }
        }

        return new ResultDigest(
            [.. folded.Take(Bound)],
            [.. failures.Take(Bound)],
            folded.Count(diagnostic => diagnostic.Severity == "error"),
            folded.Count(diagnostic => diagnostic.Severity == "warning"),
            folded.Count(diagnostic => diagnostic.Severity == "note"),
            failures.Count(failure => failure.Outcome == "failed"),
            failures.Count(failure => failure.Outcome == "errored"),
            failures.Count(failure => failure.Outcome == "timedOut"),
            failures.Count(failure => failure.Outcome == "crashed"),
            folded.FirstOrDefault(diagnostic => diagnostic.Severity == "error"),
            [.. unparsed.Take(Bound)]);
    }
}
