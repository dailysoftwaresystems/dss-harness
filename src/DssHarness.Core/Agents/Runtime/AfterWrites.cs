using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Codebase;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// What an agent wrote, as what follows its writes reads it: the paths a tool of its wrote, moved or deleted, or a command,
/// which may have written anything in the tree.
/// </summary>
public sealed record AgentGraphWrite
{
    private AgentGraphWrite(string tool, IReadOnlyList<string>? paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);

        Tool = tool;
        Paths = paths;
    }

    /// <summary>The tool that wrote or ran, as the agent called it: <c>create_file</c>, <c>Edit</c>, <c>Bash</c>.</summary>
    public string Tool { get; }

    /// <summary>
    /// The paths it wrote, absolute - a file written, moved or deleted, or a directory moved or deleted whole, at both ends of
    /// a move; null for a command, which may have written anything in the tree.
    /// </summary>
    public IReadOnlyList<string>? Paths { get; }

    /// <summary>A write of <paramref name="paths"/> by <paramref name="tool"/>.</summary>
    /// <param name="tool">The tool, as the agent called it.</param>
    /// <param name="paths">The paths it wrote, absolute.</param>
    public static AgentGraphWrite Of(string tool, params IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return new(tool, paths);
    }

    /// <summary>A command <paramref name="tool"/> ran, which may have written anything in the tree.</summary>
    /// <param name="tool">The tool, as the agent called it.</param>
    public static AgentGraphWrite Command(string tool) => new(tool, null);
}

/// <summary>
/// What follows each write and each command of an agent's run, on every client alike - the runtime's own file tools and
/// <c>run_command</c>, and Claude Code's own <c>Write</c>, <c>Edit</c>, <c>NotebookEdit</c> and <c>Bash</c>: the run's codebase
/// graph synced by what it wrote, and a side-task's edit of the configuration said where a run is working over the tree.
/// </summary>
public static class AfterWrites
{
    /// <summary>
    /// Follows <paramref name="write"/>, a write or a command of <paramref name="run"/>'s that ran: the run's graph synced by
    /// it where the run is given one (<see cref="AgentGraphSupport.AfterWriteAsync"/>), never stopped part way, and - for a
    /// side-task's write of <c>config.json</c> while a run is working over the tree - the chat told where the edit reaches.
    /// </summary>
    /// <param name="run">The run that wrote.</param>
    /// <param name="write">What it wrote.</param>
    /// <param name="events">Where the notice is emitted.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    public static async Task FollowAsync(AgentRun run, AgentGraphWrite write, IAgentEventSink events, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(events);

        if (run.Graph is { AfterWriteAsync: { } sync })
        {
            await sync(write, CancellationToken.None).ConfigureAwait(false);
        }

        if (ConfigurationNotice(run, write, comparison) is { } notice)
        {
            await events.EmitAsync(new Notice(notice)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a side-task's chat is told where <paramref name="write"/> wrote the repository's <c>config.json</c> while a run is
    /// working over the tree: the edit reaches the run's steps that start after it, and never one under way, for the agents'
    /// configuration is read when each step starts, as its prompts are. Null where the run is no side-task's, no run is
    /// live, or the write is of another file - and for a command, whose writes are not named.
    /// </summary>
    /// <param name="run">The run that wrote.</param>
    /// <param name="write">What it wrote.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    internal static string? ConfigurationNotice(AgentRun run, AgentGraphWrite write, StringComparison comparison)
    {
        if (run.Ground is not { SideTask: true, LiveRun: { } liveRun } || write.Paths is not { Count: > 0 } paths)
        {
            return null;
        }

        var config = new Repository.HarnessLayout(run.Workspace.MainCheckout, run.Workspace.MainCheckout).ConfigFile;

        return paths.Any(path => FileSystem.PathContainment.AreSame(path, config, comparison)) && liveRun() is { } live
            ? $"run '{live}' is working over this tree: the edit of config.json reaches its steps that start after it, and never one under way"
            : null;
    }

    /// <summary>
    /// The one sync of a graph after an agent's write or command: the tree of <paramref name="request"/> - the agent's
    /// worktree where the request names one, else the main checkout - synced by the paths <paramref name="write"/> wrote
    /// beneath it (a directory naming every file beneath it, <see cref="ICodebaseGraphService.SyncPathsAsync"/>), or whole for a
    /// command; then <paramref name="tool"/>, where one answers from that view, made to answer from the view the sync left.
    /// A write outside the tree syncs nothing. A sync that cannot be made - a record that does not read, git that fails, a
    /// disk that refuses - says nothing to the agent, whose write stands as written: the graph's records stand as they were,
    /// and the tool reads a body fresh against its hash either way.
    /// </summary>
    /// <param name="graph">The one graph service every graph is read and written by.</param>
    /// <param name="request">The tree's sync, its <see cref="CodebaseGraphSync.Why"/> naming the write.</param>
    /// <param name="write">What the agent wrote.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    /// <param name="tool">The graph tool answering from the tree's view; null where none does.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    /// <returns>The view as the sync left it; null where nothing was synced.</returns>
    public static async Task<CodebaseGraphQueries?> SyncGraphAsync(ICodebaseGraphService graph, CodebaseGraphSync request, AgentGraphWrite write, StringComparison comparison, CodebaseGraphTool? tool, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(write);

        var tree = request.Worktree?.Path ?? request.Repository.MainCheckoutRoot;
        IReadOnlyList<string>? paths = null;

        if (write.Paths is { } written)
        {
            paths = [.. written.Select(path => AgentPolicy.Beneath(tree, path, comparison)).OfType<string>().Distinct(StringComparer.Ordinal)];

            if (paths.Count == 0)
            {
                return null;
            }
        }

        try
        {
            if (paths is null)
            {
                await graph.SyncAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else if (await graph.SyncPathsAsync(request, paths, cancellationToken).ConfigureAwait(false) is null)
            {
                return null;
            }

            // The view as this process now holds it, no file named: what the sync just made, read with no git asked again.
            async Task<CodebaseGraphQueries> SyncedAsync(CancellationToken token)
                => new((await graph.CheckAsync(request, [], token).ConfigureAwait(false)).Read);

            if (tool is null)
            {
                return await SyncedAsync(cancellationToken).ConfigureAwait(false);
            }

            await tool.RefreshAsync(SyncedAsync, cancellationToken).ConfigureAwait(false);
            return tool.Queries;
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
