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
/// A side-task's own codebase graph, under its records as a run's is under its own: seeded from
/// the newest graph of the repository's runs or side-tasks at its opening, then incremental, synced again after each of
/// its agent's writes and commands on either client (<see cref="AfterWrites"/>). Its agent is given the <c>graph</c> tool
/// with every operation its client serves and no narrowing, held to the side-task's own policy, and is pushed nothing of
/// the graph - pull, not push, as a step at <c>tools</c> is: each message reaches it as the person typed it, and the
/// repository map made at its opening and the context pack made from each message the person types are the pages the
/// tool's <c>map</c> and <c>pack</c> give, from page 1.
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
    private readonly Execution.ILineMask? mask;
    private int mapPages;
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
    /// <param name="mask">The secrets the side-task holds, as its records and answers are masked by: what its graph tool keeps of an answer holds none.</param>
    public SideTaskGraph(
        ICodebaseGraphService graph,
        IFileSystem files,
        IHostPlatform platform,
        IGitClient git,
        OrchestrationStore store,
        TimeProvider clock,
        HarnessLayout repository,
        SideTaskLayout layout,
        string worktreesRoot,
        Execution.ILineMask? mask = null)
    {
        this.graph = graph;
        this.files = files;
        this.platform = platform;
        this.git = git;
        this.store = store;
        this.clock = clock;
        this.layout = layout;
        this.mask = mask;
        request = new CodebaseGraphSync(repository, layout.AsGraph, CodebaseGraphShape.Default, worktreesRoot, Rebuild: false, $"the side-task '{layout.Name}' opened");
    }

    /// <summary>
    /// Opens the graph: the whole first sync, seeded from the newest of the repository, then the repository map made once
    /// from the base as it stands and kept - served as it is by the tool's <c>map</c> for the whole side-task, a cache,
    /// while the pack and the tool's other operations say what the tree is now - whole, in pages, kept by the one owner of
    /// a map's files as a run's is (<see cref="OrchestrationStore.MapOnce"/>).
    /// </summary>
    /// <param name="cancellationToken">Stops the sync.</param>
    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await graph.SyncAsync(request, cancellationToken).ConfigureAwait(false);
        queries = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (queries is { })
        {
            mapPages = store.MapOnce(layout.AsGraph, reads: null, () => CodebaseRepositoryMap.Make(queries, terms: string.Empty, CodebaseGraphShape.Default.MapTokens), cancellationToken).Pages;
        }
    }

    /// <summary>
    /// What the side-task's agent is given its graph as, where the graph holds a view: the tool offered by the runtime
    /// among the harness's own, every operation its client serves and no narrowing, what it reads and an edit writes
    /// asked of the side-task's own policy - each refusal recorded as every denial is - each of its calls recorded in the
    /// side-task's graph lines, and the graph synced after each of the agent's writes and commands, the tool answering
    /// from the view each sync leaves. Null where the side-task's graph holds nothing yet, and nothing is served.
    /// </summary>
    /// <param name="ground">The side-task's ground, as its turn is run under it: its policy is the tool's.</param>
    public AgentGraphSupport? Support(AgentPolicyGround ground)
    {
        ArgumentNullException.ThrowIfNull(ground);

        if (queries is null)
        {
            return null;
        }

        var tool = Tool(queries, new AgentPolicy(files, platform, ground), Orchestration.Flow.FlowStepGraph.ReadsOf(ground));

        // Every operation its client serves, pack and map among them, each from page 1, for nothing of either is pushed:
        // the tool held for the whole chat pages the last message's pack, and the one map the side-task keeps wherever it
        // has one, by the rule a step's tool is offered it by.
        return new AgentGraphSupport(
            tool,
            line => store.AppendGraphStepLine(layout.Directory, line),
            (written, cancellationToken) => AfterWriteAsync(tool, written, cancellationToken),
            CodebaseGraphTool.Operations(write: true, narrow: false, pack: true, mapPages: mapPages));
    }

    /// <summary>
    /// Makes the context pack of the person's message - each message, whichever shape its client takes it in - and keeps
    /// it: its pages, from page 1, for the tool's <c>pack</c>, and its nearness for the tool's <c>find</c>, whose ties it
    /// breaks, so the tool follows the last message. Nothing of it is pushed: the message reaches the agent as the person
    /// typed it, and the agent asks the tool for what of the graph it needs. Nothing is made where the side-task's graph
    /// holds nothing yet.
    /// </summary>
    /// <param name="message">The person's message, whole, as typed.</param>
    public void MakePack(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (queries is { } view)
        {
            var made = CodebaseContextPack.Make(view, new ContextPackRequest(message, _ => true));
            near = made.Near;
            pack = made;
        }
    }

    /// <summary>The context pack of the person's last message, whose pages the tool's <c>pack</c> gives, from page 1; null before any.</summary>
    private ContextPack? pack;

    /// <summary>
    /// How near each node is to the work of the person's last message, as its pack said it, read by the tool at each
    /// <c>find</c> - so a tool held for the whole chat, as Claude Code's one launch holds it, follows the last message as a
    /// tool made for each turn does; null before any.
    /// </summary>
    private GraphNearness? near;

    /// <summary>
    /// Syncs the graph by what a write or a command of the side-task's agent wrote (<see cref="AfterWrites.SyncGraphAsync"/>),
    /// its sync's record naming the write, and makes <paramref name="tool"/> and the context pack of the next message answer
    /// from the view it leaves.
    /// </summary>
    /// <param name="tool">The graph tool the side-task's agent is given.</param>
    /// <param name="written">What the agent wrote.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    public async Task AfterWriteAsync(CodebaseGraphTool tool, AgentGraphWrite written, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(written);

        if (await AfterWrites.SyncGraphAsync(graph, request with { Why = $"the side-task '{layout.Name}': {written.Tool}" }, written, platform.PathComparison, tool, cancellationToken).ConfigureAwait(false) is { } synced)
        {
            queries = synced;
        }
    }

    /// <summary>The view as it stands on disk, read only; null where the side-task has no graph yet.</summary>
    private async Task<CodebaseGraphQueries?> ReadAsync(CancellationToken cancellationToken)
        => await graph.ReadOnlyAsync(layout.AsGraph, null, cancellationToken).ConfigureAwait(false) is { } read
            ? new CodebaseGraphQueries(read)
            : null;

    /// <summary>
    /// The tool over <paramref name="view"/>: every operation, no narrowing, a file re-extracted where an edit moved it -
    /// what it reads and what an edit writes asked of <paramref name="policy"/>, the side-task's own, as the run's graph tool
    /// asks its step's, and never what no graph lists - its answers' pages kept in the side-task's own
    /// <c>graph-pages</c> for the whole chat, read back under the read rules <paramref name="reads"/> names alone.
    /// </summary>
    private CodebaseGraphTool Tool(CodebaseGraphQueries view, AgentPolicy policy, string? reads)
    {
        var never = CodebaseGraphService.NeverListed(request.WorktreesRoot);
        string Full(string path) => Path.Combine(view.Tree, path.Replace('/', Path.DirectorySeparatorChar));

        return new CodebaseGraphTool(
            view,
            new AgentFiles(git, files),
            files,
            ConfinedPaths.Within(files, platform, view.Tree),
            path => policy.Read(Full(path)).Refusal() is null && !PathPatterns.Matches(never, path, platform.PathComparison),
            path => policy.Write(Full(path)),
            async (path, cancellationToken) => new CodebaseGraphQueries((await graph.CheckAsync(request, [path], cancellationToken).ConfigureAwait(false)).Read),
            () => near,
            pages: new GraphPageStore(Path.Combine(layout.Directory, OrchestratorLayout.StepPagesDirectoryName), files, reads),
            mask: mask,
            pack: () => pack?.Paged,
            map: page => store.MapPage(layout.AsGraph, reads: null, mapPages, page));
    }
}
