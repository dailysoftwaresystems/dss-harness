using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents.SideTasks;

/// <summary>
/// A side-task's own codebase graph, under its records as a run's is under its own (part 03, "The graph"): seeded from
/// the newest graph of the repository's runs or side-tasks at its opening, then incremental, synced again after each of
/// the side-task's writes. Its agent is given the <c>graph</c> tool with every operation and no narrowing, the repository
/// map with its first message, and a context pack made from each message the person types.
/// </summary>
public sealed class SideTaskGraph
{
    private readonly ICodebaseGraphService graph;
    private readonly IFileSystem files;
    private readonly IHostPlatform platform;
    private readonly IGitClient git;
    private readonly OrchestrationStore store;
    private readonly TimeProvider clock;
    private readonly CodebaseGraphSync request;
    private readonly SideTaskLayout layout;
    private readonly string map;
    private readonly string mapMeta;
    private CodebaseGraphQueries? queries;

    /// <summary>The side-task's graph over its records, not yet opened.</summary>
    /// <param name="graph">The one graph service every graph is read and written by.</param>
    /// <param name="files">The file system its records are written with.</param>
    /// <param name="platform">This machine.</param>
    /// <param name="git">Answers a body's file, as the tool reads it fresh.</param>
    /// <param name="store">Keeps the graph's lines beside the side-task's records.</param>
    /// <param name="clock">The clock its reads and writes are timed by.</param>
    /// <param name="repository">The repository whose main checkout is the graph's base.</param>
    /// <param name="layout">The side-task's own layout.</param>
    /// <param name="worktreesRoot">The configured worktrees root, which the tool's state paths name.</param>
    public SideTaskGraph(
        ICodebaseGraphService graph,
        IFileSystem files,
        IHostPlatform platform,
        IGitClient git,
        OrchestrationStore store,
        TimeProvider clock,
        HarnessLayout repository,
        SideTaskLayout layout,
        string worktreesRoot)
    {
        this.graph = graph;
        this.files = files;
        this.platform = platform;
        this.git = git;
        this.store = store;
        this.clock = clock;
        this.layout = layout;
        map = layout.MapFile;
        mapMeta = layout.MapMetaFile;
        request = new CodebaseGraphSync(repository, layout.AsGraph, CodebaseGraphShape.Default, worktreesRoot, Rebuild: false, $"the side-task '{layout.Name}' opened");
    }

    /// <summary>
    /// Opens the graph: the whole first sync, seeded from the newest of the repository, then the repository map made once
    /// from the base as it stands and kept - read as it is by every message after, a cache, while the pack and the tool
    /// say what the tree is now.
    /// </summary>
    /// <param name="cancellationToken">Stops the sync.</param>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await graph.SyncAsync(request, cancellationToken).ConfigureAwait(false);
        queries = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (!files.FileExists(map) && queries is { })
        {
            var (text, meta) = CodebaseRepositoryMap.Make(queries, terms: string.Empty, CodebaseGraphShape.Default.MapTokens);
            files.CreateDirectory(Path.GetDirectoryName(map)!);
            files.WriteAllTextAtomic(map, text);
            files.WriteAllTextAtomic(mapMeta, meta.ToJsonString(JsonStateFile.Options));
        }
    }

    /// <summary>
    /// What the side-task's agent is given its graph as, where the graph holds a view: the tool offered by the runtime
    /// among the harness's own, every operation - <c>edit</c> among them - and no narrowing, each of its calls recorded
    /// in the side-task's graph lines, and the graph synced after each of its writes. Null where the side-task's graph
    /// holds nothing yet, and nothing is served.
    /// </summary>
    public AgentGraphSupport? Support => queries is null
        ? null
        : new AgentGraphSupport(
            Tool(queries),
            line => store.AppendGraphStepLine(layout.Directory, line),
            (path, cancellationToken) => AfterWriteAsync(path, cancellationToken));

    /// <summary>
    /// The person's message as the side-task's agent is given it: the repository map at its head where it is the first,
    /// the context pack made from the message at the head of each.
    /// </summary>
    /// <param name="message">The person's message, whole.</param>
    /// <param name="first">Whether it is the side-task's first.</param>
    public string Front(string message, bool first)
    {
        var front = string.Empty;

        if (first && files.FileExists(map))
        {
            front += $"<repository-map>\n{files.ReadAllText(map)}\n</repository-map>\n\n";
        }

        if (queries is { } view)
        {
            var pack = CodebaseContextPack.Make(view, new ContextPackRequest(message, _ => true));

            if (pack.Text.Length > 0)
            {
                front += $"<codebase>\n{pack.Text}\n</codebase>\n\n";
            }
        }

        return front + message;
    }

    /// <summary>Syncs the graph by the path a write of the side-task's changed, its records kept whole.</summary>
    /// <param name="path">The path written, absolute.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    public async Task AfterWriteAsync(string path, CancellationToken cancellationToken)
    {
        if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(request.Repository.MainCheckoutRoot), platform.PathComparison) is false)
        {
            return;
        }

        var relative = Path.GetRelativePath(request.Repository.MainCheckoutRoot, path).Replace('\\', '/');

        try
        {
            await graph.SyncPathsAsync(request, [relative], cancellationToken).ConfigureAwait(false);
            queries = await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Results.HarnessException)
        {
            // A sync that cannot be made says nothing to the agent: the graph's records stand as they were, and the tool
            // reads a body fresh against its hash either way.
        }
    }

    /// <summary>The view as it stands on disk, read only; null where the side-task has no graph yet.</summary>
    private async Task<CodebaseGraphQueries?> ReadAsync(CancellationToken cancellationToken)
        => await graph.ReadOnlyAsync(layout.AsGraph, null, cancellationToken).ConfigureAwait(false) is { } read
            ? new CodebaseGraphQueries(read)
            : null;

    /// <summary>The tool over <paramref name="view"/>: every operation, no narrowing, a file re-extracted where an edit moved it.</summary>
    private CodebaseGraphTool Tool(CodebaseGraphQueries view)
    {
        var never = CodebaseGraphService.NeverListed(request.WorktreesRoot);

        return new CodebaseGraphTool(
            view,
            new AgentFiles(git, files, clock),
            files,
            ConfinedPaths.Within(files, platform, view.Tree),
            path => !PathPatterns.Matches(never, path, platform.PathComparison),
            _ => AgentPolicyAnswer.Allow,
            async (path, cancellationToken) => new CodebaseGraphQueries((await graph.CheckAsync(request, [path], cancellationToken).ConfigureAwait(false)).Read));
    }
}
