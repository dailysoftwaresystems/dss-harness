using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Application;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Codebase;

/// <summary>One read of a run's codebase graph a person asked for: the view, the operation, and what it reads.</summary>
/// <param name="Run">The run whose graph it is, as its orchestrator is named.</param>
/// <param name="Agent">The agent whose view to read; <see langword="null"/> for the base.</param>
public sealed record CodebaseGraphQuery(string Run, string? Agent)
{
    /// <summary>A <c>find</c>: the words to match.</summary>
    public string? Find { get; init; }

    /// <summary>A <c>node</c>: the symbol to card.</summary>
    public string? Node { get; init; }

    /// <summary>A <c>related</c>: the symbol to walk from.</summary>
    public string? Related { get; init; }

    /// <summary>A <c>related</c>'s relation (<see cref="GraphRelations.All"/>).</summary>
    public string? Relation { get; init; }

    /// <summary>A <c>related</c>'s depth, 1 to 3.</summary>
    public int? Depth { get; init; }

    /// <summary>Whether <c>--outline</c> was given.</summary>
    public bool Outline { get; init; }

    /// <summary>A <c>--outline</c>'s file or symbol; <see langword="null"/> for the repository's.</summary>
    public string? OutlineOf { get; init; }

    /// <summary>A <c>body</c>: the symbols to read, in the order given.</summary>
    public IReadOnlyList<string>? Body { get; init; }
}

/// <summary>
/// <c>query-codebase-graph &lt;run&gt;</c>: runs one of the <c>graph</c> tool's read operations on a run's graph - the base,
/// or the view of <c>--agent</c> - and prints the answer an agent would be given, so what was served can be checked by
/// hand. It reads only: no sync, no lock, nothing written; a view not made yet is said, and the base is answered. No host
/// is ever asked to run it (part 04).
/// </summary>
/// <param name="contextLoader">Finds the repository the run's graph is of.</param>
/// <param name="graph">The run's graph, read only.</param>
/// <param name="gitClient">Lists a tree for the text search a fallback runs.</param>
/// <param name="fileSystem">Reads a body fresh from the tree.</param>
/// <param name="platform">How the tree compares names, for what the graph never lists.</param>
/// <param name="time">Times the text search's pattern.</param>
public sealed class CodebaseGraphQueryService(
    IHarnessContextLoader contextLoader,
    ICodebaseGraphService graph,
    IGitClient gitClient,
    IFileSystem fileSystem,
    IHostPlatform platform,
    TimeProvider time) : IUseCase
{
    /// <summary>The command's name.</summary>
    public const string CommandName = "query-codebase-graph";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly ICodebaseGraphService _graph = graph;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly TimeProvider _time = time;

    /// <summary>Runs <paramref name="query"/> on the run's graph in the tree <paramref name="startDirectory"/> is in.</summary>
    /// <param name="startDirectory">A directory of the repository the run's graph is of.</param>
    /// <param name="query">The view, the operation, and what it reads.</param>
    /// <param name="json">Whether the answer is the command's document.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public async Task<CommandOutcome> QueryAsync(string startDirectory, CodebaseGraphQuery query, bool json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // A run and an agent are named as every verb names them: a name that is no name is a usage error, never a path.
        if ((OrchestrationRules.NameProblem(query.Run) ?? (query.Agent is { } agent ? OrchestrationRules.NameProblem(agent) : null)) is { } badName)
        {
            return CodebaseGraphQueryReports.Render(CommandOutcome.Usage(badName), query.Run, view: null, head: null, op: null, json);
        }

        if (query.OutlineOf is { } outline && string.IsNullOrWhiteSpace(outline))
        {
            return CodebaseGraphQueryReports.Render(CommandOutcome.Usage("--outline's value is empty: give a file, a symbol, or nothing for the repository"), query.Run, null, null, op: "outline", json);
        }

        if (Input(query) is not { } chosen)
        {
            return CodebaseGraphQueryReports.Render(
                CommandOutcome.Usage("give exactly one of --find, --node, --related, --outline or --body"),
                query.Run,
                view: null,
                head: null,
                op: null,
                json);
        }

        var (op, input) = chosen;

        if (op != GraphOp.Related && (query.Relation is not null || query.Depth is not null))
        {
            return CodebaseGraphQueryReports.Render(CommandOutcome.Usage("--relation and --depth are --related's: give them with --related"), query.Run, null, null, Name(op), json);
        }

        if (op == GraphOp.Related && query.Relation is null)
        {
            return CodebaseGraphQueryReports.Render(CommandOutcome.Usage($"--related needs --relation: one of {string.Join(", ", GraphRelations.All)}"), query.Run, null, null, op: "related", json);
        }

        // A misuse is told before anything is read, as the tool tells it: a run with no graph never hides one.
        if (CodebaseGraphTool.Misused(input, op) is { } misused)
        {
            return CodebaseGraphQueryReports.Render(CommandOutcome.Usage(misused), query.Run, null, null, Name(op), json);
        }

        // A run's records are the main checkout's, wherever the verb is typed: an agent's worktree has a configuration of its own.
        var context = await MainTree.LoadAsync(_contextLoader, _platform, startDirectory, cancellationToken).ConfigureAwait(false);
        var run = OrchestratorLayout.Of(context.Layout, query.Run);
        var said = string.Empty;
        var view = query.Agent;

        CodebaseGraphRead? read;

        try
        {
            read = await _graph.ReadOnlyAsync(run, query.Agent, cancellationToken).ConfigureAwait(false);

            if (read is null && query.Agent is not null)
            {
                // The view is not made yet: say so, and answer the base, so the reader is never left with nothing where the base stands.
                read = await _graph.ReadOnlyAsync(run, null, cancellationToken).ConfigureAwait(false);

                if (read is not null)
                {
                    said = $"the view of '{query.Agent}' is not made yet; the base is answered. ";
                    view = CodebaseGraphService.BaseView;
                }
            }
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            // A record that is there and does not read is said with why: what an agent was served cannot be checked against it.
            return CodebaseGraphQueryReports.Render(CommandOutcome.Refused(ex.Message), query.Run, view: null, head: null, op: Name(op), json);
        }

        if (read is null)
        {
            return CodebaseGraphQueryReports.Render(
                CommandOutcome.Refused($"run '{query.Run}' has no codebase graph{(query.Agent is null ? string.Empty : $" with a view of '{query.Agent}'")} in '{run.GraphDirectory}'"),
                query.Run,
                view: null,
                head: null,
                op: Name(op),
                json);
        }

        var queries = new CodebaseGraphQueries(read);

        // --outline takes a file or a symbol: a file where the view holds one at it, or where it names no node and is written as
        // a file is; a symbol otherwise.
        if (op == GraphOp.Outline && query.OutlineOf is { } outlineOf)
        {
            input.Remove("symbol");

            if (queries.HeldPath(outlineOf) is { } file)
            {
                input["path"] = file;
            }
            else if (queries.Resolve(outlineOf) is GraphResolve.NotFound && CodebaseGraphQueries.NamesAFile(outlineOf))
            {
                input["path"] = outlineOf;
            }
            else
            {
                input["symbol"] = outlineOf;
            }
        }

        // A person reads what the graph lists: never the credential slots or the tool's own state, which a text search would otherwise reach.
        var never = CodebaseGraphService.NeverListed(context.Config.Worktrees.Root);
        var files = new AgentFiles(_gitClient, _fileSystem, _time);
        var tool = new CodebaseGraphTool(queries, files, _fileSystem, path => !PathPatterns.Matches(never, path, _platform.PathComparison));
        var answer = await tool.RunAsync(input, new HashSet<GraphOp>(CodebaseGraphTool.Read), cancellationToken).ConfigureAwait(false);
        var text = said + answer.Text;

        var outcome = answer.Outcome == GraphToolOutcome.Usage
            ? CommandOutcome.Usage(text)
            : CommandOutcome.Ok($"graph {Name(op)} of run '{query.Run}' ({view ?? CodebaseGraphService.BaseView})") with { Data = [text], Quiet = true };

        return CodebaseGraphQueryReports.Render(outcome, query.Run, view ?? read.View, read.Head, Name(op), json, text);
    }

    private static (GraphOp Op, JsonObject Input)? Input(CodebaseGraphQuery query)
    {
        var chosen = new List<(GraphOp Op, JsonObject Input)>();

        if (query.Find is { } find)
        {
            chosen.Add((GraphOp.Find, new JsonObject { ["op"] = "find", ["query"] = find }));
        }

        if (query.Node is { } node)
        {
            chosen.Add((GraphOp.Node, new JsonObject { ["op"] = "node", ["symbol"] = node }));
        }

        if (query.Related is { } related)
        {
            var input = new JsonObject { ["op"] = "related", ["symbol"] = related };

            if (query.Relation is { } relation)
            {
                input["relation"] = relation;
            }

            if (query.Depth is { } depth)
            {
                input["depth"] = depth;
            }

            chosen.Add((GraphOp.Related, input));
        }

        if (query.Outline)
        {
            var input = new JsonObject { ["op"] = "outline" };

            if (query.OutlineOf is { } outlineOf)
            {
                input["symbol"] = outlineOf;
            }

            chosen.Add((GraphOp.Outline, input));
        }

        if (query.Body is { Count: > 0 } body)
        {
            chosen.Add((GraphOp.Body, new JsonObject { ["op"] = "body", ["symbols"] = new JsonArray([.. body.Select(symbol => (JsonNode)symbol)]) }));
        }

        return chosen.Count == 1 ? chosen[0] : null;
    }

    private static string Name(GraphOp op) => CodebaseGraphTool.NameOf(op);
}
