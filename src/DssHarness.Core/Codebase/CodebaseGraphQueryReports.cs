using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Codebase;

/// <summary>
/// Turns what <c>query-codebase-graph</c> read into what it prints: the answer an agent would be given, and under
/// <c>--json</c> its one document, with the same members on every exit - the run, the view, the commit, the operation and
/// the answer - so a script reads one shape whatever happened.
/// </summary>
public static class CodebaseGraphQueryReports
{
    /// <summary>The outcome as the command answers with it: its document under <c>--json</c>, its answer otherwise.</summary>
    /// <param name="outcome">How the command ended.</param>
    /// <param name="run">The run its graph is of.</param>
    /// <param name="view">The view answered - <c>base</c> or an agent - where one was read; null otherwise.</param>
    /// <param name="head">The commit the tree's HEAD named, where one was read.</param>
    /// <param name="op">The operation asked, where one was chosen.</param>
    /// <param name="json">Whether the answer is the command's document.</param>
    /// <param name="answer">The answer an agent would be given, where the command read one.</param>
    public static CommandOutcome Render(CommandOutcome outcome, string run, string? view, string? head, string? op, bool json, string? answer = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return json
            ? outcome with { Data = [Document(outcome.ExitCode, outcome.Message, run, view, head, op, answer)], Quiet = true }
            : outcome;
    }

    /// <summary>The document the command answers with where it reads no graph: nothing of a view, no answer.</summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary) => Document(exitCode, summary, run: null, view: null, head: null, op: null, answer: null);

    private static string Document(int exitCode, string summary, string? run, string? view, string? head, string? op, string? answer)
        => ReportJson.Stopped(
            exitCode,
            summary,
            new JsonObject
            {
                ["run"] = run,
                ["view"] = view,
                ["head"] = head,
                ["op"] = op,
                ["answer"] = answer,
            });
}
