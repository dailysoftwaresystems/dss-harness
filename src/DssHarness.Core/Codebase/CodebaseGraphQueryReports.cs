using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Codebase;

/// <summary>
/// Turns what <c>query-codebase-graph</c> read into what it prints: the answer an agent would be given, and under
/// <c>--json</c> its one document, with the same members on every exit - the run, the view, the commit, the digest of the
/// graph answered from, the operation, the answer and its pages - so a script reads one shape whatever happened.
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
    /// <param name="pages">The pages of the answer, where the command read one; null otherwise.</param>
    /// <param name="graph">
    /// The digest of the graph the view answered from (<see cref="CodebaseGraphQueries.Graph"/>), where one was read: the
    /// verb keeps nothing between its calls, so a script that reads page after page compares it to know each page was
    /// counted on one graph.
    /// </param>
    public static CommandOutcome Render(CommandOutcome outcome, string run, string? view, string? head, string? op, bool json, string? answer = null, GraphPageAnswer? pages = null, string? graph = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return json
            ? outcome with { Data = [Document(outcome.ExitCode, outcome.Message, run, view, head, graph, op, answer, pages)], Quiet = true }
            : outcome;
    }

    /// <summary>The document the command answers with where it reads no graph: nothing of a view, no answer.</summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string Stopped(int exitCode, string summary) => Document(exitCode, summary, run: null, view: null, head: null, graph: null, op: null, answer: null, pages: null);

    /// <summary>
    /// The command's document. Each of a page's entries is said with what it counts toward the total - one for a result,
    /// a node, a hit or a served line, none for a line that only says something - the caption of the section it is of,
    /// and the node it gave lines of or heads the block of, so the entries of every page summed by what they count are the
    /// total, and a node's edges are told apart by their group.
    /// </summary>
    private static string Document(int exitCode, string summary, string? run, string? view, string? head, string? graph, string? op, string? answer, GraphPageAnswer? pages)
        => ReportJson.Stopped(
            exitCode,
            summary,
            new JsonObject
            {
                ["run"] = run,
                ["view"] = view,
                ["head"] = head,
                ["graph"] = graph,
                ["op"] = op,
                ["answer"] = answer,
                ["page"] = pages?.Page,
                ["pages"] = pages?.Pages,
                ["total"] = pages?.Total,
                ["entries"] = pages is null ? null : new JsonArray([.. pages.Sections.SelectMany(section => section.Entries.Select(entry => (JsonNode)Entry(section, entry)))]),
            });

    private static JsonObject Entry(GraphPageSection section, GraphEntry entry)
        => new()
        {
            ["text"] = entry.Text,
            ["counts"] = entry.Lines,
            ["section"] = section.Caption,
            ["node"] = entry.Node,
        };
}
