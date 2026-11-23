using System.Globalization;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// This digest with every frame of every stack it holds marked by whether its file is inside <paramref name="root"/>:
    /// a rooted file under it, or a relative one, which a tool run in the tree names by its place there. The one place
    /// a frame is marked, so every reader of a digest reads one mark.
    /// </summary>
    /// <param name="root">The tree the phase ran in.</param>
    public ResultDigest WithInTree(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return this with
        {
            Diagnostics = [.. Diagnostics.Select(diagnostic => diagnostic with { Stack = Marked(diagnostic.Stack) })],
            Failures = [.. Failures.Select(failure => failure with
            {
                Stack = Marked(failure.Stack),
                Exception = failure.Exception is { } thrown ? thrown with { Stack = Marked(thrown.Stack), CausedBy = [.. thrown.CausedBy.Select(CauseMarked)] } : null,
            })],
            First = First is { } first ? first with { Stack = Marked(first.Stack) } : null,
        };

        ExceptionRecord CauseMarked(ExceptionRecord cause)
            => cause with { Stack = Marked(cause.Stack), CausedBy = [.. cause.CausedBy.Select(CauseMarked)] };

        IReadOnlyList<StackFrame> Marked(IReadOnlyList<StackFrame>? stack)
            => stack is null ? [] : [.. stack.Select(frame => frame with { InTree = Inside(frame.File) })];

        bool Inside(string? file)
            => file is null
                || !DssHarness.Core.Platform.PlatformPaths.IsRootedOnAnyPlatform(file)
                || DssHarness.Core.FileSystem.PathContainment.IsStrictlyInside(root, file, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first error said as one line - the ledger's, a red check's and a continuation record's: what printed it,
    /// its code, its place and its message's first line, the count of its printings where it printed more than once.
    /// Null where the digest holds no error and no failure.
    /// </summary>
    public string? FirstSaid()
        => First is { } first ? Said(first)
            : Failures.FirstOrDefault(failure => failure.Outcome is "failed" or "errored") is { } failure
                ? $"{failure.Framework} {failure.Id}{(failure.File is { } file ? $" ({file}{(failure.Line is { } line ? $":{line.ToString(CultureInfo.InvariantCulture)}" : string.Empty)})" : string.Empty)}: {FirstLine(failure.Message)}"
            : null;

    /// <summary>
    /// The digest as a model reads it: the counts, the first error said whole with its stack rendered to its frames in
    /// the tree, the next distinct errors to four in all - one line each - every test failure of the four with its
    /// rendered stack, the rest counted by code and by file, and the lines no parser held. A rule of rendering, never
    /// of the record: the whole digest stays in the ledger's own <c>--json</c> (<see cref="Document"/>).
    /// </summary>
    public JsonObject ForAModel()
    {
        var said = Diagnostics.Where(diagnostic => diagnostic.Severity == "error").ToList();
        var first = First ?? (said.Count > 0 ? said[0] : null);

        // The next distinct errors after the first, four in all with it - a rule of rendering, never of the record.
        var more = (first is null ? said : said.Where(diagnostic => !diagnostic.SameAs(first))).Take(first is null ? 4 : 3).ToList();

        return new JsonObject
        {
            ["errors"] = Errors,
            ["warnings"] = Warnings,
            ["notes"] = Notes,
            ["failed"] = Failed,
            ["errored"] = Errored,
            ["timedOut"] = TimedOut,
            ["crashed"] = Crashed,
            ["first"] = first is not null || Failures.Count > 0
                ? OneOf(first, Failures.FirstOrDefault())
                : null,
            ["diagnostics"] = new JsonArray([.. more.Select(diagnostic => (JsonNode)new JsonObject
            {
                ["said"] = Said(diagnostic),
                ["count"] = diagnostic.Count,
                ["log"] = LogOf(diagnostic),
            })]),
            ["failures"] = new JsonArray([.. Failures.Take(Bound > 4 ? 4 : Bound).Select(failure => (JsonNode)new JsonObject
            {
                ["framework"] = failure.Framework,
                ["id"] = failure.Id,
                ["message"] = FirstLine(failure.Message),
                ["file"] = failure.File,
                ["line"] = failure.Line,
                ["outcome"] = failure.Outcome,
                ["stack"] = Rendered(failure.Exception?.Stack ?? failure.Stack),
            })]),
            ["byCode"] = new JsonArray([.. Diagnostics
                .Where(diagnostic => diagnostic.Code is not null)
                .GroupBy(diagnostic => diagnostic.Code!, StringComparer.Ordinal)
                .Select(group => (JsonNode)new JsonObject { ["of"] = group.Key, ["count"] = group.Sum(diagnostic => diagnostic.Count) })]),
            ["byFile"] = new JsonArray([.. Diagnostics
                .Where(diagnostic => diagnostic.File is not null)
                .GroupBy(diagnostic => diagnostic.File!, StringComparer.Ordinal)
                .Select(group => (JsonNode)new JsonObject { ["of"] = group.Key, ["count"] = group.Sum(diagnostic => diagnostic.Count) })]),
            ["unparsed"] = new JsonArray([.. (Unparsed ?? []).Select(line => (JsonNode?)line)]),
        };

        JsonObject? OneOf(Diagnostic? diagnostic, TestFailure? failure)
            => diagnostic is not null
                ? new JsonObject
                {
                    ["kind"] = "diagnostic",
                    ["tool"] = diagnostic.Tool,
                    ["code"] = diagnostic.Code,
                    ["severity"] = diagnostic.Severity,
                    ["message"] = FirstLine(diagnostic.Message),
                    ["file"] = diagnostic.File,
                    ["line"] = diagnostic.Line,
                    ["column"] = diagnostic.Column,
                    ["count"] = diagnostic.Count,
                    ["log"] = LogOf(diagnostic),
                    ["stack"] = Rendered(diagnostic.Stack),
                    ["said"] = Said(diagnostic),
                }
                : failure is not null
                    ? new JsonObject
                    {
                        ["kind"] = "failure",
                        ["framework"] = failure.Framework,
                        ["id"] = failure.Id,
                        ["message"] = FirstLine(failure.Message),
                        ["file"] = failure.File,
                        ["line"] = failure.Line,
                        ["outcome"] = failure.Outcome,
                        ["stack"] = Rendered(failure.Exception?.Stack ?? failure.Stack),
                        ["said"] = FirstSaid(),
                    }
                    : null;
    }

    /// <summary>The digest as one JSON document, whole: every diagnostic and failure it holds, fully, with its counts - the ledger's <c>--json</c> and the GUI's.</summary>
    public JsonObject Document()
        => new()
        {
            ["errors"] = Errors,
            ["warnings"] = Warnings,
            ["notes"] = Notes,
            ["failed"] = Failed,
            ["errored"] = Errored,
            ["timedOut"] = TimedOut,
            ["crashed"] = Crashed,
            ["diagnostics"] = new JsonArray([.. Diagnostics.Select(diagnostic => (JsonNode)new JsonObject
            {
                ["tool"] = diagnostic.Tool,
                ["severity"] = diagnostic.Severity,
                ["code"] = diagnostic.Code,
                ["message"] = diagnostic.Message,
                ["file"] = diagnostic.File,
                ["line"] = diagnostic.Line,
                ["column"] = diagnostic.Column,
                ["endLine"] = diagnostic.EndLine,
                ["endColumn"] = diagnostic.EndColumn,
                ["symbol"] = diagnostic.Symbol,
                ["project"] = diagnostic.Project,
                ["count"] = diagnostic.Count,
                ["stack"] = Frames(diagnostic.Stack),
                ["log"] = LogOf(diagnostic),
            })]),
            ["failures"] = new JsonArray([.. Failures.Select(failure => (JsonNode)new JsonObject
            {
                ["framework"] = failure.Framework,
                ["id"] = failure.Id,
                ["name"] = failure.Name,
                ["class"] = failure.Class,
                ["method"] = failure.Method,
                ["message"] = failure.Message,
                ["expected"] = failure.Expected,
                ["actual"] = failure.Actual,
                ["file"] = failure.File,
                ["line"] = failure.Line,
                ["outcome"] = failure.Outcome,
                ["duration"] = failure.Duration,
                ["stack"] = Frames(failure.Exception?.Stack ?? failure.Stack),
                ["exception"] = failure.Exception is { } thrown
                    ? new JsonObject
                    {
                        ["type"] = thrown.Type,
                        ["message"] = thrown.Message,
                        ["stack"] = Frames(thrown.Stack),
                        ["causedBy"] = new JsonArray([.. thrown.CausedBy.Select(cause => (JsonNode)new JsonObject
                        {
                            ["type"] = cause.Type,
                            ["message"] = cause.Message,
                            ["stack"] = Frames(cause.Stack),
                        })]),
                    }
                    : null,
            })]),
            ["unparsed"] = new JsonArray([.. (Unparsed ?? []).Select(line => (JsonNode?)line)]),
        };

    /// <summary>A stack as one JSON document holds it: every frame whole - its text, its place, its symbol, its module, its kind and whether its file is in the tree.</summary>
    private static JsonArray? Frames(IReadOnlyList<StackFrame>? stack)
        => stack is null || stack.Count == 0
            ? null
            : new JsonArray([.. stack.Select(frame => (JsonNode)new JsonObject
            {
                ["text"] = frame.Text,
                ["file"] = frame.File,
                ["line"] = frame.Line,
                ["column"] = frame.Column,
                ["module"] = frame.Module,
                ["symbol"] = frame.Symbol,
                ["kind"] = frame.Kind,
                ["inTree"] = frame.InTree,
            })]);

    /// <summary>A diagnostic said as one line: its tool, its code, its place and its message's first line.</summary>
    private static string Said(Diagnostic diagnostic)
        => $"{diagnostic.Tool}{(diagnostic.Code is { } code ? $" {code}" : string.Empty)}"
            + (diagnostic.File is { } file
                ? $" {file}{(diagnostic.Line is { } line ? $"({line.ToString(CultureInfo.InvariantCulture)}{(diagnostic.Column is { } column ? $",{column.ToString(CultureInfo.InvariantCulture)}" : string.Empty)})" : string.Empty)}"
                : string.Empty)
            + $": {FirstLine(diagnostic.Message)}"
            + (diagnostic.Count > 1 ? $" (printed {diagnostic.Count.ToString(CultureInfo.InvariantCulture)} times)" : string.Empty);

    /// <summary>The first line of a message, trimmed: the line a one-line saying says.</summary>
    private static string? FirstLine(string? message)
        => message?.Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0);

    /// <summary>A diagnostic's log range, as the schema's list of whole numbers says it.</summary>
    private static JsonArray? LogOf(Diagnostic diagnostic)
        => diagnostic.Log is { } log ? new JsonArray(log.From, log.To) : null;

    /// <summary>
    /// A stack as a model reads it: every frame inside the tree, the one frame on each side where it crosses out of
    /// it, and a line for each run of frames left out - which module or package held them, where the frames name one.
    /// </summary>
    private static JsonArray Rendered(IReadOnlyList<StackFrame>? stack)
    {
        var lines = new JsonArray();
        var run = new List<StackFrame>();

        foreach (var frame in stack ?? [])
        {
            if (frame.InTree)
            {
                Flush();
                lines.Add(frame.Text.Trim());
            }
            else
            {
                run.Add(frame);
            }
        }

        Flush();
        return lines;

        void Flush()
        {
            if (run.Count == 0)
            {
                return;
            }

            if (run.Count <= 2)
            {
                foreach (var frame in run)
                {
                    lines.Add(frame.Text.Trim());
                }
            }
            else
            {
                lines.Add(run[0].Text.Trim());
                lines.Add($"... {(run.Count - 2).ToString(CultureInfo.InvariantCulture)} frames in {run.FirstOrDefault(frame => frame.Module is not null)?.Module ?? "outside the tree"}");
                lines.Add(run[^1].Text.Trim());
            }

            run.Clear();
        }
    }
}
