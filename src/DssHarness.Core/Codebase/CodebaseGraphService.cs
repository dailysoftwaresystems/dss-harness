using System.Collections.Concurrent;
using System.Globalization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Codebase;

/// <summary>Syncs a run's one codebase graph with its trees: the main checkout, its base, and each agent's worktree, its view.</summary>
public interface ICodebaseGraphService
{
    /// <summary>
    /// Brings the run's graph of one tree up to date by a whole listing of it - the main checkout's, the base, or with an
    /// agent an agent's worktree's, its view, made against the base (the base first, where the run has none yet): reads
    /// only the files whose blob, claimant, claimant's version, or what <c>maxFileBytes</c> or <c>languages</c> decide of
    /// them changed since that tree was last synced - or since the newest earlier run's graph, where the run has none -
    /// those whose extract is gone, and those it could not read; a view reads none whose extract the base or an earlier
    /// sync of it made. Links again only what a change could have changed, where this process linked the tree before, and
    /// writes its records. Syncs of one tree wait for each other; syncs of different trees run side by side, a view's on
    /// one snapshot of the base.
    /// </summary>
    /// <param name="request">What to sync, how the workflow shapes it, and why.</param>
    /// <param name="cancellationToken">Stops the sync; what it wrote is the last whole manifest and the extracts before it.</param>
    /// <exception cref="HarnessException">
    /// git could not be started (<see cref="HarnessExit.ToolMissing"/>), or failed to list the tree
    /// (<see cref="HarnessExit.CommandFailed"/>); a record could not be read or written, by its cause.
    /// </exception>
    Task<CodebaseGraphSynced> SyncAsync(CodebaseGraphSync request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings the graph of one tree up to date by <paramref name="paths"/> alone, with no listing of the rest: git is asked
    /// what it holds of those paths, as a listing would say it, and each is kept, read again or taken out - the paths a
    /// fold wrote and deleted, as its verb reports them. <see langword="null"/> where that tree has no graph yet: there is
    /// nothing to bring up to date.
    /// </summary>
    /// <param name="request">What to sync, how the workflow shapes it, and why; never a rebuild.</param>
    /// <param name="paths">The paths, relative to the tree with forward separators.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    /// <exception cref="HarnessException">
    /// The tree's record does not read (<see cref="HarnessExit.Refused"/>), saying why: only a whole sync builds it again;
    /// git could not be started (<see cref="HarnessExit.ToolMissing"/>), or failed to list the paths
    /// (<see cref="HarnessExit.CommandFailed"/>); a record could not be read or written, by its cause.
    /// </exception>
    Task<CodebaseGraphSynced?> SyncPathsAsync(CodebaseGraphSync request, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// The check every answer makes of the files it names, before it is given: each file's size and time against what the
    /// tree's graph recorded - trusted only where that time is older than the tree's last whole sync, as git trusts an
    /// entry older than its index - a file either of them moved, whose time is not older, or that could not be read listed
    /// again by its path, as a sync by paths lists it, and one whose blob moved or that could not be read read again, its
    /// view updated and linked again, on the spot, where any changed - so what the answer is made from is each file
    /// as it is, and each extract it names is read before the check returns. A tree with no graph yet, or one this process
    /// holds neither a link nor a link result of, is synced whole first; a link or a submodule stands as it was listed.
    /// </summary>
    /// <param name="request">The tree, how the workflow shapes it, and why; never a rebuild.</param>
    /// <param name="paths">The files the answer names, relative to the tree with forward separators.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <exception cref="HarnessException">
    /// git could not be started (<see cref="HarnessExit.ToolMissing"/>), or failed to list or hash the tree
    /// (<see cref="HarnessExit.CommandFailed"/>); a record could not be read or written, by its cause.
    /// </exception>
    Task<CodebaseGraphChecked> CheckAsync(CodebaseGraphSync request, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Removes the view of <paramref name="agent"/>'s worktree from the run's graph, where it has one: the agent is gone.</summary>
    /// <param name="run">The run whose graph it is.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the wait for a sync of that view.</param>
    Task DropViewAsync(OrchestratorLayout run, string agent, CancellationToken cancellationToken = default);
}

/// <summary>One sync asked of a run's graph.</summary>
/// <param name="Repository">The repository whose main checkout is the graph's base, and whose other runs a first sync seeds from.</param>
/// <param name="Run">The run whose graph it is.</param>
/// <param name="Shape">What the workflow's <c>codebaseGraph</c> says.</param>
/// <param name="WorktreesRoot">The configured worktrees root, which the tool's state paths name.</param>
/// <param name="Rebuild">Whether every file of the tree is extracted again, no extract reused.</param>
/// <param name="Why">What asked for it: a step, by its instance; a fold; a step's start; an answer's check.</param>
public sealed record CodebaseGraphSync(HarnessLayout Repository, OrchestratorLayout Run, CodebaseGraphShape Shape, string WorktreesRoot, bool Rebuild, string Why)
{
    /// <summary>The agent's worktree whose view is synced; <see langword="null"/> for the base.</summary>
    public CodebaseGraphWorktree? Worktree { get; init; }

    /// <summary>The agent whose worktree's view is synced; <see langword="null"/> for the base.</summary>
    public string? Agent => Worktree?.Agent;
}

/// <summary>An agent's worktree, whose view of a run's graph is synced: the two named together, or neither.</summary>
public sealed record CodebaseGraphWorktree
{
    /// <summary>Names <paramref name="agent"/>'s worktree at <paramref name="path"/>.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="path">Its worktree, absolute.</param>
    public CodebaseGraphWorktree(string agent, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Agent = agent;
        Path = path;
    }

    /// <summary>The agent.</summary>
    public string Agent { get; }

    /// <summary>Its worktree, absolute.</summary>
    public string Path { get; }
}

/// <summary>What one sync of a graph did and left.</summary>
/// <param name="Graph">The graph's directory.</param>
/// <param name="View">The view synced: <c>base</c>, or the agent's name.</param>
/// <param name="Commit">The commit the tree's HEAD named; <see langword="null"/> where it named none yet.</param>
/// <param name="Dirty">Whether the tree held anything its HEAD does not.</param>
/// <param name="Files">How many files the view keeps.</param>
/// <param name="Listed">How many files the tree listed that the view keeps, every one looked at; a sync by paths, how many it was given.</param>
/// <param name="Hashed">How many files git was asked the blob of to know what changed; the listing of each file read, once the reads are done, is not counted.</param>
/// <param name="Extracted">How many files an implementation read.</param>
/// <param name="Reused">How many files kept an extract already made, or a state already found.</param>
/// <param name="Removed">How many paths left the view.</param>
/// <param name="Nodes">How many nodes the view holds.</param>
/// <param name="Edges">How many edges.</param>
/// <param name="Languages">For each implementation that read a file, how many it read and the nodes they hold.</param>
/// <param name="Skipped">Every file the view lists and holds no declaration of, with its state and why.</param>
/// <param name="Seconds">How long the sync took.</param>
public sealed record CodebaseGraphSynced(
    string Graph,
    string View,
    string? Commit,
    bool Dirty,
    int Files,
    int Listed,
    int Hashed,
    int Extracted,
    int Reused,
    int Removed,
    int Nodes,
    int Edges,
    IReadOnlyList<CodebaseGraphLanguage> Languages,
    IReadOnlyList<CodebaseGraphSkipped> Skipped,
    double Seconds)
{
    /// <summary>Why the graph was built again whole, where it was.</summary>
    public string? Rebuilt { get; init; }

    /// <summary>The run whose graph the sync started from, where the run had none.</summary>
    public string? SeededFrom { get; init; }

    /// <summary>How many references linking found, where the sync linked: every one where it linked whole, those a change could have changed otherwise.</summary>
    public int? Resolved { get; init; }

    /// <summary>The digest of the base's manifest the view stands on: the base's own for the base, the one a view was made against for a view.</summary>
    public string Base { get; init; } = string.Empty;

    /// <summary>
    /// The files the graph could not read - their state <see cref="CodeFileStates.Unreadable"/>, each with why: what makes it
    /// incomplete, an implementation's defect among them.
    /// </summary>
    public IReadOnlyList<CodebaseGraphSkipped> Unreadable => [.. Skipped.Where(file => file.State == CodeFileStates.Unreadable)];
}

/// <summary>What one check of the files an answer names found, and the view the answer is made from.</summary>
/// <param name="Read">The view as it is once the check is made: each file it names as it is.</param>
/// <param name="Checked">How many of the files named the view holds, each looked at.</param>
/// <param name="Hashed">
/// How many git was asked of again, as a sync by their paths lists them: those whose size or time moved, were never recorded,
/// are not older than the tree's last whole sync, or could not be read.
/// </param>
/// <param name="Extracted">How many an implementation read again: those whose blob moved, or that could not be read.</param>
/// <param name="Removed">How many were gone, and left the view.</param>
public sealed record CodebaseGraphChecked(CodebaseGraphRead Read, int Checked, int Hashed, int Extracted, int Removed);

/// <summary>
/// One view of a run's graph as a sync or a check left it in this process, which an answer is made from: each file it keeps
/// with its entry and extract, and its nodes and edges.
/// </summary>
public sealed class CodebaseGraphRead
{
    private readonly IReadOnlyDictionary<string, CodeGraphFile>? _files;
    private readonly IReadOnlyDictionary<string, FileExtract> _checked;
    private readonly Func<string, CodeGraphFileEntry, FileExtract?> _stored;
    private readonly Lazy<CodeGraphSymbolsFile> _symbols;

    internal CodebaseGraphRead(string view, string tree, string? head, IReadOnlyDictionary<string, CodeGraphFileEntry> files, IReadOnlyDictionary<string, CodeGraphFile>? linked, IReadOnlyDictionary<string, FileExtract> checkedExtracts, Func<CodeGraphSymbolsFile> symbols, Func<string, CodeGraphFileEntry, FileExtract?> stored)
    {
        View = view;
        Tree = tree;
        Head = head;
        Files = files;
        _files = linked;
        _checked = checkedExtracts;
        _stored = stored;
        _symbols = new Lazy<CodeGraphSymbolsFile>(symbols);
    }

    /// <summary>The view: <c>base</c>, or the agent's name.</summary>
    public string View { get; }

    /// <summary>The tree it is of, absolute.</summary>
    public string Tree { get; }

    /// <summary>The commit the tree's HEAD named when it was last listed; <see langword="null"/> where it named none.</summary>
    public string? Head { get; }

    /// <summary>Every file the view keeps, by its path, with its entry.</summary>
    public IReadOnlyDictionary<string, CodeGraphFileEntry> Files { get; }

    /// <summary>Its nodes, each with its handle in the view, and its edges.</summary>
    public CodeGraphSymbolsFile Symbols => _symbols.Value;

    /// <summary>
    /// The extract of the file at <paramref name="path"/> as the view holds it - every node with its lines and digest; null
    /// where it keeps none. Each file the check named was read before the check returned, and is answered as it was; any
    /// other the process does not hold is read from the graph when asked, and is null where a sync since took it out.
    /// </summary>
    /// <param name="path">The file's path, relative to the tree with forward separators.</param>
    public FileExtract? Extract(string path)
        => !Files.TryGetValue(path, out var entry) || entry.Extract is null ? null
            : _files is not null && _files.TryGetValue(path, out var file) && file.Extract is { } held ? held
            : _checked.TryGetValue(path, out var read) ? read
            : _stored(path, entry);
}

/// <summary>One implementation's part of a graph.</summary>
/// <param name="Language">Its name.</param>
/// <param name="Files">How many files it read.</param>
/// <param name="Nodes">How many nodes they hold, their files' own among them.</param>
public sealed record CodebaseGraphLanguage(string Language, int Files, int Nodes);

/// <summary>A file a graph lists and holds no declaration of.</summary>
/// <param name="Path">Its path, relative to the tree.</param>
/// <param name="State">Its state.</param>
/// <param name="Why">Why.</param>
public sealed record CodebaseGraphSkipped(string Path, string State, string? Why);

/// <summary>A sync of a run's codebase graph ended: what it listed, read, reused and removed.</summary>
/// <param name="Graph">The graph's directory.</param>
/// <param name="View">The view synced.</param>
/// <param name="Why">What asked for it.</param>
/// <param name="Listed">How many files the tree listed that the graph keeps.</param>
/// <param name="Extracted">How many files an implementation read.</param>
/// <param name="Reused">How many kept what was found before.</param>
/// <param name="Removed">How many paths left the graph.</param>
/// <param name="Seconds">How long it took.</param>
public sealed record GraphSynced(string Graph, string View, string Why, int Listed, int Extracted, int Reused, int Removed, double Seconds) : RunEvent;

/// <inheritdoc cref="ICodebaseGraphService"/>
/// <param name="gitClient">Lists the trees.</param>
/// <param name="fileSystem">Reads their files, and the graph's records.</param>
/// <param name="store">Reads and writes the graph's records, strictly.</param>
/// <param name="extractors">Every implementation of the contract.</param>
/// <param name="platform">How this machine compares paths.</param>
/// <param name="identity">The tool's version, which the manifest names.</param>
/// <param name="output">Hears each sync.</param>
/// <param name="time">The clock.</param>
public sealed class CodebaseGraphService(
    IGitClient gitClient,
    IFileSystem fileSystem,
    OrchestrationStore store,
    CodeGraphExtractors extractors,
    IHostPlatform platform,
    IToolIdentityProvider identity,
    IHarnessOutput output,
    TimeProvider time) : ICodebaseGraphService
{
    /// <summary>The view a sync of the main checkout is.</summary>
    public const string BaseView = "base";

    /// <summary>
    /// The share of the machine's processors a sync reads files on: one of every this many, so a build beside it is not
    /// starved - and at least one.
    /// </summary>
    public const int ReadShare = 2;

    /// <summary>What this process holds of each graph, by its directory.</summary>
    private readonly ConcurrentDictionary<string, Graph> _graphs = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<CodebaseGraphSynced> SyncAsync(CodebaseGraphSync request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var graph = GraphOf(request.Run);
        return graph.RunningAsync(() => SyncHeldAsync(graph, request, cancellationToken), this, request.Run, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<CodebaseGraphSynced?> SyncPathsAsync(CodebaseGraphSync request, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);

        var graph = GraphOf(request.Run);

        return graph.RunningAsync(
            async () =>
            {
                var gate = graph.GateOf(request.Agent);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                Tree? made;
                CodeGraphManifest? before = null;

                try
                {
                    var sync = new Sync(this, graph, request, cancellationToken);

                    if (sync.Current() is not { } current)
                    {
                        // A record that does not read is said with why: only a whole sync builds it again.
                        if (sync.Unread is { } unread)
                        {
                            throw unread;
                        }

                        return null;
                    }

                    before = request.Agent is null ? current.Manifest : null;
                    made = await sync.PathsAsync(current, paths).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }

                if (before is not null && made.Manifest is { } after && before.Digest != after.Digest)
                {
                    await RebaseViewsAsync(graph, request.Run, before, after, request.Why, cancellationToken).ConfigureAwait(false);
                }

                return made.Synced;
            },
            this,
            request.Run,
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<CodebaseGraphChecked> CheckAsync(CodebaseGraphSync request, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(paths);

        var graph = GraphOf(request.Run);

        return graph.RunningAsync(
            async () =>
            {
                var gate = graph.GateOf(request.Agent);
                (Tree Tree, CodebaseGraphChecked Checked) checkedOut;
                CodeGraphManifest? before;

                // A tree this process holds neither a link nor a link result of - none yet, one held as its records say,
                // or one a sync that failed left it holding nothing of - is synced first, and looked at again under its
                // gate, so the check answers from its own link: a view restored from its record is never answered from the
                // base's.
                for (; ; )
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                    try
                    {
                        var sync = new Sync(this, graph, request, cancellationToken);

                        if (sync.Current() is { } current && current is { Linked: not null } or { Symbols: not null })
                        {
                            before = request.Agent is null ? current.Manifest : null;
                            checkedOut = await sync.CheckAsync(current, paths).ConfigureAwait(false);
                            break;
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }

                    _ = await SyncHeldAsync(graph, request, cancellationToken).ConfigureAwait(false);
                }

                if (before is not null && checkedOut.Tree.Manifest is { } after && before.Digest != after.Digest)
                {
                    await RebaseViewsAsync(graph, request.Run, before, after, request.Why, cancellationToken).ConfigureAwait(false);
                }

                return checkedOut.Checked;
            },
            this,
            request.Run,
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DropViewAsync(OrchestratorLayout run, string agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);

        var graph = GraphOf(run);

        await graph.RunningAsync<object?>(
            async () =>
            {
                var gate = graph.GateOf(agent);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    _store.DeleteGraphView(run, agent);
                    graph.Views.TryRemove(agent, out _);
                    graph.Standings.TryRemove(agent, out _);
                    graph.Unswept();
                }
                finally
                {
                    gate.Release();
                }

                return null;
            },
            this,
            run,
            cancellationToken).ConfigureAwait(false);
    }

    private Graph GraphOf(OrchestratorLayout run) => _graphs.GetOrAdd(run.GraphDirectory, _ => new Graph());

    /// <summary>
    /// A whole sync of the request's tree, under its gate - the base's own first sync before it, where a view is asked and
    /// the run has no base - and, where the base moved, every view held against its new manifest after. No gate is held
    /// while another is waited for but a view's by the base's own sync of it alone, so no two syncs wait on each other.
    /// </summary>
    /// <param name="graph">What this process holds of the run's graph.</param>
    /// <param name="request">The sync.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    private async Task<CodebaseGraphSynced> SyncHeldAsync(Graph graph, CodebaseGraphSync request, CancellationToken cancellationToken)
    {
        if (request.Agent is { } agent)
        {
            // A view is made against the base: the run's first is made before the view's gate is taken, so no view's gate
            // is held while the base's sync holds every other view against what it made.
            var baseRequest = request with { Worktree = null, Rebuild = false };

            if (new Sync(this, graph, baseRequest, cancellationToken).Current() is null)
            {
                _ = await SyncBaseAsync(graph, baseRequest, firstOnly: true, cancellationToken).ConfigureAwait(false);
            }

            var gate = graph.GateOf(agent);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                return (await new Sync(this, graph, request, cancellationToken).WholeAsync().ConfigureAwait(false)).Synced;
            }
            finally
            {
                gate.Release();
            }
        }

        return (await SyncBaseAsync(graph, request, firstOnly: false, cancellationToken).ConfigureAwait(false)).Synced;
    }

    /// <summary>
    /// A whole sync of the base under its gate - or, where it is asked for a view's sake, none where another made the base
    /// while this one waited for the gate - and, where the base moved, every view held against its new manifest after.
    /// </summary>
    /// <param name="graph">What this process holds of the run's graph.</param>
    /// <param name="request">The sync of the base.</param>
    /// <param name="firstOnly">Whether the base is synced only where the run has none: a view's first sync needs one, not a newer one.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    private async Task<Tree> SyncBaseAsync(Graph graph, CodebaseGraphSync request, bool firstOnly, CancellationToken cancellationToken)
    {
        Tree made;
        CodeGraphManifest? before;

        await graph.BaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var sync = new Sync(this, graph, request, cancellationToken);

            if (firstOnly && sync.Current() is { } current)
            {
                return current;
            }

            before = graph.Base?.Manifest ?? ManifestOrNull(request.Run);
            made = await sync.WholeAsync().ConfigureAwait(false);
        }
        finally
        {
            graph.BaseGate.Release();
        }

        if (before is not null && made.Manifest is { } after && before.Digest != after.Digest)
        {
            await RebaseViewsAsync(graph, request.Run, before, after, request.Why, cancellationToken).ConfigureAwait(false);
        }

        return made;
    }

    /// <summary>
    /// Holds every view of the run against the base's new manifest, one at a time under its gate: a view's worktree did not
    /// change because the base moved, so its files are what they were, and its overlay is what of them the new base does not
    /// hold - the base's earlier entries among them, their extracts kept. A view made against a base this process no longer
    /// knows is left to its next sync, which lists its worktree; so is every view once a newer sync of the base holds them
    /// all, so none is put back on an older base. A view whose first sync is under way is among them: its gate is waited
    /// for, and its record held against the new base once written. A view that could not be held - its record not read or
    /// not written, or the views not listed - is said, a line of <c>sync.jsonl</c> and a warning, and left to its next
    /// sync: the base's sync stands as it went, and nothing it holds is let go but what it held of that view.
    /// </summary>
    private async Task RebaseViewsAsync(Graph graph, OrchestratorLayout run, CodeGraphManifest before, CodeGraphManifest after, string why, CancellationToken cancellationToken)
    {
        graph.Bases[before.Digest] = before;
        graph.Bases[after.Digest] = after;

        // The base each view stands on once held, as this rebase found it; where one is not known, every base is kept.
        var stand = new HashSet<string>(StringComparer.Ordinal);
        var known = true;
        IReadOnlyList<string> recorded;

        try
        {
            recorded = _store.GraphViews(run);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            // Each view this process knows is still held; one only its record names is left to its next sync.
            recorded = [];
            known = false;
            Unheld(run, null, why, ex);
        }

        foreach (var agent in recorded.Union(graph.ViewGates.Keys, StringComparer.Ordinal).ToList())
        {
            var gate = graph.GateOf(agent);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // A newer sync of the base holds every view against what it made, and keeps what they stand on: this
                // one's is no longer the base.
                if (graph.Base?.Base != after.Digest)
                {
                    return;
                }

                var view = ViewOrNull(run, agent);

                // A view with no record that reads stands on no base this rebase keeps: what this process held of it is
                // let go, and its next sync makes it again whole, as its record would have it.
                if (view is null)
                {
                    graph.Views.TryRemove(agent, out _);
                    continue;
                }

                if (view.Base == after.Digest || !graph.Bases.TryGetValue(view.Base, out var stood))
                {
                    stand.Add(view.Base);
                    continue;
                }

                var files = Whole(stood.Files, view);
                var (overlay, deleted) = Overlay(files, after.Files);
                var rebased = view with { Base = after.Digest, Files = overlay, Deleted = deleted };

                _store.WriteGraphView(run, rebased);

                if (graph.Views.TryGetValue(agent, out var tree))
                {
                    graph.Views[agent] = tree with { Base = after.Digest, View = rebased };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // What this process held of it is let go: its next sync takes it as its record says, and lists its worktree.
                graph.Views.TryRemove(agent, out _);
                known = false;
                Unheld(run, agent, why, ex);
            }
            finally
            {
                gate.Release();
            }
        }

        // A manifest no view stands on is kept no longer.
        foreach (var digest in known ? graph.Bases.Keys.Where(digest => digest != after.Digest && digest != graph.Base?.Base && !stand.Contains(digest)).ToList() : [])
        {
            graph.Bases.TryRemove(digest, out _);
        }
    }

    /// <summary>
    /// Says that <paramref name="agent"/>'s view was not held against the base's new manifest by the base's sync that
    /// <paramref name="why"/> asked for, and why: a line of <c>sync.jsonl</c> for the view, and a warning. Where no agent
    /// is named, the views were not listed, and those only their records name are said in a warning alone.
    /// </summary>
    private void Unheld(OrchestratorLayout run, string? agent, string why, Exception ex)
    {
        if (agent is null)
        {
            _output.Warn("graph", $"the codebase graph's views of run '{run.Name}' that this process does not hold stand as they were ({why}): they could not be listed to be held against the base's new manifest: {ex.Message.TrimEnd('.')}. The base's sync went through; each view's next whole sync makes it again against the base as it stands.");
            return;
        }

        var failed = $"it was not held against the base's new manifest: {ex.Message.TrimEnd('.')}";

        try
        {
            _store.AppendGraphSync(run, new CodeGraphSyncLine(_time.GetUtcNow(), agent, why, 0, 0, 0, 0, 0, 0) { Failed = failed });
        }
        catch (Exception unwritten) when (unwritten is not OperationCanceledException)
        {
            failed += $"; and its line of the sync log was not written: {unwritten.Message.TrimEnd('.')}";
        }

        _output.Warn("graph", $"the codebase graph's view '{agent}' of run '{run.Name}' stands as it was ({why}): {failed}. The base's sync went through; the view's next whole sync makes it again against the base as it stands.");
    }

    /// <summary>Every file a view holds: the base's of <paramref name="baseFiles"/>, less what it deleted, its overlay over them.</summary>
    private static Dictionary<string, CodeGraphFileEntry> Whole(IReadOnlyDictionary<string, CodeGraphFileEntry> baseFiles, CodeGraphView view)
    {
        var deleted = view.Deleted.ToHashSet(StringComparer.Ordinal);
        var files = baseFiles.Where(file => !deleted.Contains(file.Key)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);

        foreach (var (path, entry) in view.Files)
        {
            files[path] = entry;
        }

        return files;
    }

    /// <summary>What of <paramref name="files"/>, a view's every file, <paramref name="baseFiles"/> does not hold alike, and what of the base it does not hold.</summary>
    private static (IReadOnlyDictionary<string, CodeGraphFileEntry> Overlay, IReadOnlyList<string> Deleted) Overlay(IReadOnlyDictionary<string, CodeGraphFileEntry> files, IReadOnlyDictionary<string, CodeGraphFileEntry> baseFiles)
        => (new SortedDictionary<string, CodeGraphFileEntry>(
                files.Where(file => !baseFiles.TryGetValue(file.Key, out var held) || !Alike(held, file.Value)).ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            [.. baseFiles.Keys.Where(path => !files.ContainsKey(path)).Order(StringComparer.Ordinal)]);

    /// <summary>Whether two entries say one thing of a file: its blob and all that was found of it - when either was looked at aside.</summary>
    private static bool Alike(CodeGraphFileEntry one, CodeGraphFileEntry other) => one == other with { Mtime = one.Mtime };

    /// <summary>Whether two entries give the linker one file: the same extract, read by the same implementation, left out alike.</summary>
    private static bool SameFile(CodeGraphFileEntry one, CodeGraphFileEntry other)
        => one.Extract == other.Extract && one.Language == other.Language && (one.State == CodeFileStates.Generated) == (other.State == CodeFileStates.Generated);

    /// <summary>
    /// The view of <paramref name="agent"/>'s worktree, where the run has one that reads; null where it has none, or one that
    /// does not read - which its next sync makes again. One that could not be read at all is no record that does not read,
    /// and fails the sync.
    /// </summary>
    private CodeGraphView? ViewOrNull(OrchestratorLayout run, string agent) => ViewOrNull(run, agent, out _);

    /// <inheritdoc cref="ViewOrNull(OrchestratorLayout, string)"/>
    /// <param name="run">The run.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="unread">Why it does not read, where it is there and does not; null otherwise.</param>
    private CodeGraphView? ViewOrNull(OrchestratorLayout run, string agent, out HarnessException? unread)
    {
        unread = null;

        try
        {
            return _store.ReadGraphView(run, agent);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            unread = ex;
            return null;
        }
    }

    /// <summary>
    /// The run's manifest, where it has one that reads; null where it has none, or one that does not read - which a whole
    /// sync builds again, saying why. One that could not be read at all is no record that does not read, and fails the sync.
    /// </summary>
    private CodeGraphManifest? ManifestOrNull(OrchestratorLayout run) => ManifestOrNull(run, out _);

    /// <inheritdoc cref="ManifestOrNull(OrchestratorLayout)"/>
    /// <param name="run">The run.</param>
    /// <param name="unread">Why it does not read, where it is there and does not; null otherwise.</param>
    private CodeGraphManifest? ManifestOrNull(OrchestratorLayout run, out HarnessException? unread)
    {
        unread = null;

        try
        {
            return _store.ReadGraphManifest(run);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            unread = ex;
            return null;
        }
    }

    /// <summary>Removes every extract neither the base's manifest nor a view names: what a crash, an earlier state or a view gone left.</summary>
    private void RemoveUnnamed(OrchestratorLayout run)
    {
        if (!_fileSystem.DirectoryExists(run.GraphExtractsDirectory))
        {
            return;
        }

        var comparer = StringComparer.FromComparison(_platform.PathComparison);
        var named = new HashSet<string>(comparer);

        void Name(IEnumerable<CodeGraphFileEntry> entries)
        {
            foreach (var entry in entries.Where(entry => entry.Extract is not null))
            {
                named.Add(run.GraphExtractFile(entry.Extract!));
            }
        }

        // A record that does not read names nothing it can be trusted for: nothing is removed rather than something it needs.
        try
        {
            Name(_store.ReadGraphManifest(run)?.Files.Values ?? []);

            foreach (var agent in _store.GraphViews(run))
            {
                Name(_store.ReadGraphView(run, agent)?.Files.Values ?? []);
            }
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            return;
        }

        foreach (var file in _fileSystem.EnumerateFiles(run.GraphExtractsDirectory, recursive: true).Where(file => !named.Contains(file)).ToList())
        {
            _fileSystem.DeleteFile(file);
        }
    }

    /// <summary>What this process holds of one run's graph: the gate of each tree, the trees as their last syncs left them, and the syncs under way.</summary>
    private sealed class Graph
    {
        private readonly SemaphoreSlim _starting = new(1, 1);
        private int _running;

        /// <summary>
        /// Whether an extract may have been left that no record names: so at first, after a record was written in place of
        /// one that named an extract it does not, after a sync that did not end, and after a view was dropped.
        /// </summary>
        private int _unswept = 1;

        /// <summary>The base's gate: two syncs of it wait for each other.</summary>
        public SemaphoreSlim BaseGate { get; } = new(1, 1);

        /// <summary>Each view's gate, by its agent.</summary>
        public ConcurrentDictionary<string, SemaphoreSlim> ViewGates { get; } = new(StringComparer.Ordinal);

        private Tree? _base;

        /// <summary>The base as its last sync in this process left it; a view takes it whole, as one snapshot.</summary>
        public Tree? Base
        {
            get => Volatile.Read(ref _base);
            set => Volatile.Write(ref _base, value);
        }

        /// <summary>
        /// <paramref name="read"/>, the base as its records say, held as the base where this process holds none yet - else
        /// the one it holds: a view reads the base without its gate, and never puts back one older than a sync left.
        /// </summary>
        public Tree Adopt(Tree read) => Interlocked.CompareExchange(ref _base, read, null) ?? read;

        /// <summary>Each view as its last sync in this process left it, by its agent.</summary>
        public ConcurrentDictionary<string, Tree> Views { get; } = new(StringComparer.Ordinal);

        /// <summary>Each base manifest a view may stand on, by its digest: what a view is held against the next one from.</summary>
        public ConcurrentDictionary<string, CodeGraphManifest> Bases { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// For each tree, by its view, the size and time of each file a check found moved and its blob not: what the next
        /// check holds it to, so its file is hashed once.
        /// </summary>
        public ConcurrentDictionary<string, ConcurrentDictionary<string, (long Bytes, DateTime? Written, string? Blob)>> Standings { get; } = new(StringComparer.Ordinal);

        public SemaphoreSlim GateOf(string? agent) => agent is null ? BaseGate : ViewGates.GetOrAdd(agent, _ => new SemaphoreSlim(1, 1));

        /// <summary>Says that an extract a record named may be named by none now: the next sweep looks.</summary>
        public void Unswept() => Volatile.Write(ref _unswept, 1);

        /// <summary>
        /// Runs <paramref name="work"/> as one sync under way; the last to end, with none begun since, removes every
        /// extract no record names, where one may have been left - a sweep that fails is said, never fails the sync that
        /// ended, and is tried again by the next.
        /// </summary>
        public async Task<T> RunningAsync<T>(Func<Task<T>> work, CodebaseGraphService service, OrchestratorLayout run, CancellationToken cancellationToken)
        {
            await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _running);
            _starting.Release();

            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // What it read before it stopped is on disk, and no record may name it. A record that failed after
                // another was written may leave the records ahead of what this process holds, so the next sync takes each
                // tree as its records say; a stop never comes between a record and what the process holds of it.
                Unswept();

                if (ex is not OperationCanceledException)
                {
                    Volatile.Write(ref _base, null);
                    Views.Clear();
                }

                throw;
            }
            finally
            {
                if (Interlocked.Decrement(ref _running) == 0 && _starting.Wait(0))
                {
                    try
                    {
                        if (Volatile.Read(ref _running) == 0 && Interlocked.Exchange(ref _unswept, 0) == 1)
                        {
                            service.RemoveUnnamed(run);
                        }
                    }
                    catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
                    {
                        Unswept();
                        service._output.Warn("graph", $"the codebase graph of '{run.Name}' keeps extracts no record names, which were not removed: {ex.Message.TrimEnd('.')}. The next sync tries again.");
                    }
                    finally
                    {
                        _starting.Release();
                    }
                }
            }
        }
    }

    /// <summary>One tree's graph as a sync left it: its every file, the linker's files and link where it was linked in this process, and its record.</summary>
    /// <param name="Name">Its view: <c>base</c>, or the agent's name.</param>
    /// <param name="Path">The tree, absolute.</param>
    /// <param name="Entries">Every file it keeps.</param>
    /// <param name="Here">The paths whose entry was read in this tree: the ones whose size and time a check holds a file to.</param>
    /// <param name="Files">The linker's files, each with its extract; null where the tree was not linked in this process.</param>
    /// <param name="Linked">Its link, with what one made again from it needs; null where it was not linked in this process.</param>
    /// <param name="Base">The digest of the base's manifest it stands on.</param>
    private sealed record Tree(string Name, string Path, IReadOnlyDictionary<string, CodeGraphFileEntry> Entries, IReadOnlySet<string> Here, IReadOnlyDictionary<string, CodeGraphFile>? Files, CodeGraphLinked? Linked, string Base)
    {
        /// <summary>The base's manifest; null for a view.</summary>
        public CodeGraphManifest? Manifest { get; init; }

        /// <summary>A view's record; null for the base.</summary>
        public CodeGraphView? View { get; init; }

        /// <summary>The link result on disk, where it was read or written; null where neither.</summary>
        public CodeGraphSymbolsFile? Symbols { get; init; }

        /// <summary>
        /// When its record was written by its last whole sync in this process, as the file system stamped it: a check
        /// trusts a file's recorded size and time only where that time is older, as git trusts an entry older than its
        /// index - a file written in the same tick as its look, or after it, may have changed again with neither moving.
        /// Null where no whole sync of it wrote its record in this process.
        /// </summary>
        public DateTime? Written { get; init; }

        /// <summary>The commit the tree's HEAD named.</summary>
        public string? Head => Manifest?.Head ?? View?.Head;

        /// <summary>What the sync that left it answered.</summary>
        public CodebaseGraphSynced Synced { get; init; } = null!;
    }

    /// <summary>One sync of one tree, holding what it read so no fact is read twice.</summary>
    private sealed class Sync
    {
        private readonly CodebaseGraphService _service;
        private readonly Graph _graph;
        private readonly CodebaseGraphSync _request;
        private readonly CancellationToken _cancellationToken;
        private readonly long _started;
        private readonly string _tree;
        private readonly string _view;
        private readonly OrchestratorLayout _run;
        private readonly CodebaseGraphShape _shape;
        private readonly List<CodeGraphExtractorVersion> _versions;

        /// <summary>Each file the sync could not name or found gone before it was hashed, once, by the name it is said by.</summary>
        private readonly SortedDictionary<string, CodebaseGraphSkipped> _unnamed = new(StringComparer.Ordinal);

        /// <summary>The paths whose entry was kept, unread: one made again while linking is then counted extracted alone.</summary>
        private readonly HashSet<string> _reusedPaths = new(StringComparer.Ordinal);

        /// <summary>The extract of each file this sync read, by its path: linked from memory, never read back from disk.</summary>
        private readonly ConcurrentDictionary<string, FileExtract> _read = new(StringComparer.Ordinal);

        /// <summary>The credential slots and the tool's own state under the run's worktrees root: what the graph never lists.</summary>
        private readonly IReadOnlyList<string> _never;
        private int _extracted;
        private int _reused;
        private int _hashed;

        /// <summary>Where the tree's root itself leads, once a path was asked whether a link lies along it.</summary>
        private string? _reached;

        public Sync(CodebaseGraphService service, Graph graph, CodebaseGraphSync request, CancellationToken cancellationToken)
        {
            _service = service;
            _graph = graph;
            _request = request;
            _cancellationToken = cancellationToken;
            _started = service._time.GetTimestamp();
            _tree = request.Worktree?.Path ?? request.Repository.MainCheckoutRoot;
            _view = request.Agent ?? BaseView;
            _run = request.Run;
            _shape = request.Shape;
            _versions = [.. service._extractors.All.Select(extractor => new CodeGraphExtractorVersion(extractor.Language, extractor.Version))];
            _never = [.. HarnessLayout.SecretSlots, .. HarnessLayout.StatePaths(request.WorktreesRoot)];
        }

        /// <summary>Why the tree's record does not read, where <see cref="Current"/> found it there and not reading; null otherwise.</summary>
        public HarnessException? Unread { get; private set; }

        /// <summary>
        /// The tree as it stands in the graph: as this process last left it, else as its records say - the base's manifest,
        /// or a view's record over the base it stands on - and null where it has none, or one that does not read
        /// (<see cref="Unread"/> says why).
        /// </summary>
        public Tree? Current()
        {
            if (_request.Agent is not { } agent)
            {
                // This process is the one that writes the run's graph: what it holds is the graph as it stands.
                if (_graph.Base is { } held)
                {
                    return held;
                }

                var read = _service.ManifestOrNull(_run, out var unread);
                Unread = unread;

                return read is { } manifest
                    ? _graph.Adopt(new Tree(BaseView, _tree, manifest.Files, manifest.Files.Keys.ToHashSet(StringComparer.Ordinal), null, null, manifest.Digest) { Manifest = manifest })
                    : null;
            }

            if (_graph.Views.TryGetValue(agent, out var view) && view.Path == _tree)
            {
                return view;
            }

            var record = _service.ViewOrNull(_run, agent, out var refused);
            Unread = refused;

            if (record is null || record.Tree != _tree || BaseOf(record.Base) is not { } stood)
            {
                return null;
            }

            return new Tree(agent, _tree, Whole(stood.Files, record), record.Files.Keys.ToHashSet(StringComparer.Ordinal), null, null, record.Base) { View = record };
        }

        /// <summary>The base's manifest of <paramref name="digest"/>, where this process or the disk still holds it.</summary>
        private CodeGraphManifest? BaseOf(string digest)
            => _graph.Bases.TryGetValue(digest, out var known) ? known
                : (_graph.Base?.Manifest ?? _service.ManifestOrNull(_run)) is { } current && current.Digest == digest ? current
                : null;

        /// <summary>
        /// A whole sync: the tree listed, each file kept or read, linked again from what this process linked of it - or of the
        /// base, for a view it has not made yet - and its records written.
        /// </summary>
        public async Task<Tree> WholeAsync()
        {
            if (_request.Agent is null)
            {
                return await BaseAsync().ConfigureAwait(false);
            }

            var baseTree = new Sync(_service, _graph, _request with { Worktree = null }, _cancellationToken).Current()
                ?? throw new InvalidOperationException("a view is synced after its base");
            var before = _request.Rebuild ? null : Current();
            var (listed, head, dirty) = await ListAsync(paths: null).ConfigureAwait(false);
            var sameReading = SameReading(baseTree.Manifest!);
            var entries = new SortedDictionary<string, CodeGraphFileEntry>(StringComparer.Ordinal);
            var here = new HashSet<string>(StringComparer.Ordinal);
            var toRead = new List<(string Path, string Blob)>();

            foreach (var (path, file) in listed)
            {
                var was = before?.Entries.GetValueOrDefault(path);
                var held = baseTree.Entries.GetValueOrDefault(path);

                // What the view held of the file, else what the base holds: a file whose extract either made is not read.
                if ((Kept(path, file, was, sameReading) ?? Kept(path, file, held, sameReading)) is { } kept)
                {
                    entries[path] = kept;

                    if (before?.Here.Contains(path) == true && Alike(was!, kept))
                    {
                        here.Add(path);
                    }
                }
                else
                {
                    toRead.Add((path, file.Blob!));
                }
            }

            foreach (var (path, entry) in await ReadAsync(toRead).ConfigureAwait(false))
            {
                entries[path] = entry;
                here.Add(path);
            }

            var from = before?.Linked is not null ? before : baseTree.Linked is not null ? baseTree : null;
            var linked = await LinkAsync(entries, from).ConfigureAwait(false);
            var (overlay, deleted) = Overlay(entries, baseTree.Entries);
            var view = new CodeGraphView(CodeGraphManifest.CurrentSchema, _request.Agent, _tree, baseTree.Base, head, dirty, _service._time.GetUtcNow(), overlay, deleted);

            _service._store.WriteGraphView(_run, view);
            _graph.Bases[baseTree.Base] = baseTree.Manifest!;
            Replaced(before?.Entries, entries);

            var removed = before is null ? 0 : before.Entries.Keys.Count(path => !entries.ContainsKey(path));
            var tree = new Tree(_view, _tree, entries, here.Where(overlay.ContainsKey).ToHashSet(StringComparer.Ordinal), linked.Files, linked.Linked, baseTree.Base)
            {
                View = view,
                Written = Stamped(_run.GraphViewFile(_view)),
            };

            _graph.Views[_view] = tree;

            return tree with { Synced = Said(tree, entries, entries.Count, removed, linked.Linked, rebuilt: _request.Rebuild ? Rebuilding : null, seededFrom: null) };
        }

        /// <summary>
        /// A whole sync of the base: the main checkout listed against the run's manifest - or the newest earlier run's,
        /// where it has none - linked again from what this process linked of it where that is the manifest's, and its
        /// manifest and link result written.
        /// </summary>
        private async Task<Tree> BaseAsync()
        {
            var (previous, from, rebuilt, seededFrom) = Previous();
            var (listed, head, dirty) = await ListAsync(paths: null).ConfigureAwait(false);
            var sameReading = previous is not null && SameReading(previous);
            var entries = new SortedDictionary<string, CodeGraphFileEntry>(StringComparer.Ordinal);
            var toRead = new List<(string Path, string Blob)>();

            foreach (var (path, file) in listed)
            {
                var before = _request.Rebuild ? null : previous?.Files.GetValueOrDefault(path);

                if (Settled(path, file, before, sameReading, from) is { } kept)
                {
                    entries[path] = kept;

                    // A link or a submodule is listed without a read, and always kept: it counts as reused only where the
                    // graph had it, of that blob.
                    if (before is not null && before.Blob == kept.Blob)
                    {
                        _reusedPaths.Add(path);
                        _reused++;
                    }
                }
                else
                {
                    toRead.Add((path, file.Blob!));
                }
            }

            foreach (var (path, entry) in await ReadAsync(toRead).ConfigureAwait(false))
            {
                entries[path] = entry;
            }

            // A first sync seeded from another run's graph removed nothing from this run's: it had none.
            var removed = from is null ? previous?.Files.Keys.Count(path => !entries.ContainsKey(path)) ?? 0 : 0;

            return await WriteBaseAsync(previous, from is null && !_request.Rebuild, entries, head, dirty, entries.Count, removed, rebuilt, seededFrom, whole: true, written: null).ConfigureAwait(false);
        }

        /// <summary>
        /// The base linked - kept as it is where the manifest's digest did not change and its link result reads, linked again
        /// from what this process linked of the manifest it was otherwise - and its manifest and link result written.
        /// </summary>
        /// <param name="previous">The manifest the sync started from.</param>
        /// <param name="ownPrevious">Whether that is the run's own manifest, as this sync found it.</param>
        /// <param name="entries">Every file the base keeps now.</param>
        /// <param name="head">The commit the main checkout's HEAD named.</param>
        /// <param name="dirty">Whether it held anything its HEAD does not.</param>
        /// <param name="listed">How many files the sync listed, or was given.</param>
        /// <param name="removed">How many paths left the base.</param>
        /// <param name="rebuilt">Why it was built again whole, where it was.</param>
        /// <param name="seededFrom">The run it was seeded from, where it was.</param>
        /// <param name="whole">Whether the sync listed the whole tree, so its record's time is what a check holds a file's to.</param>
        /// <param name="written">For a sync that did not list the whole tree, the time its last whole sync's record was written.</param>
        private async Task<Tree> WriteBaseAsync(CodeGraphManifest? previous, bool ownPrevious, SortedDictionary<string, CodeGraphFileEntry> entries, string? head, bool dirty, int listed, int removed, string? rebuilt, string? seededFrom, bool whole, DateTime? written)
        {
            var files = new Dictionary<string, CodeGraphFileEntry>(entries, StringComparer.Ordinal);
            var digest = CodeGraphManifest.DigestOf(_versions, files);
            var held = _graph.Base is { } known && previous is not null && known.Base == previous.Digest && ownPrevious ? known : null;
            CodeGraphSymbolsFile? symbols = null;
            (IReadOnlyDictionary<string, CodeGraphFile>? Files, CodeGraphLinked? Linked) linked = (null, null);

            if (previous is not null && ownPrevious && previous.Digest == digest)
            {
                linked = held?.Linked is not null && held.Base == digest ? (held.Files, held.Linked) : (null, null);

                // What this process wrote of the manifest as it stands is what the disk holds: it is not read back.
                symbols = held?.Symbols is { } kept && kept.Graph == digest ? kept : Symbols(digest);
            }

            var made = false;

            if (symbols is null)
            {
                made = true;
                linked = await LinkAsync(entries, held?.Linked is not null ? held : null).ConfigureAwait(false);

                // An extract that did not read was made again while linking: the manifest names what was made.
                files = new Dictionary<string, CodeGraphFileEntry>(entries, StringComparer.Ordinal);
                digest = CodeGraphManifest.DigestOf(_versions, files);
                symbols = SymbolsOf(linked.Linked!, digest);
            }

            var manifest = new CodeGraphManifest(CodeGraphManifest.CurrentSchema, _service._identity.Current.Version, _versions, _tree, head, dirty, _service._time.GetUtcNow(), files, digest)
            {
                Languages = _shape.Languages,
            };

            _service._store.WriteGraphManifest(_run, manifest);
            Replaced(ownPrevious ? previous?.Files : null, files);

            // A link result made from the manifest as it stands is kept as it is; one made again is written after it.
            if (made)
            {
                _service._store.WriteGraphSymbols(_run, symbols);
            }

            var tree = new Tree(BaseView, _tree, files, files.Keys.ToHashSet(StringComparer.Ordinal), linked.Files, linked.Linked, digest)
            {
                Manifest = manifest,
                Symbols = symbols,
                Written = whole ? Stamped(_run.GraphManifestFile) : written,
            };

            _graph.Base = tree;

            return tree with { Synced = Said(tree, files, listed, removed, linked.Linked, rebuilt, seededFrom) };
        }

        /// <summary>
        /// A sync of <paramref name="paths"/> alone: git asked what it holds of them, as a listing would say it, and each kept,
        /// read again or taken out of the tree's graph; then linked again and its records written.
        /// </summary>
        public async Task<Tree> PathsAsync(Tree current, IReadOnlyList<string> paths)
        {
            var asked = new List<string>();

            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                // A path the graph's records cannot name is said, as a listing says it, and never asked of git.
                if (Unnameable(path))
                {
                    _ = Kept(new GitName(path, IsUtf8: true), _unnamed);
                    continue;
                }

                asked.Add(path);
            }

            var (listed, head, _) = await ListAsync(asked).ConfigureAwait(false);
            var changed = asked.Count(path => listed.TryGetValue(path, out var file) ? current.Entries.GetValueOrDefault(path)?.Blob != file.Blob : current.Entries.ContainsKey(path));

            return await ApplyAsync(current, asked.ToDictionary(path => path, path => listed.GetValueOrDefault(path), StringComparer.Ordinal), head, changed > 0, asked.Count).ConfigureAwait(false);
        }

        /// <summary>
        /// The check of <paramref name="paths"/>: each one the tree keeps looked at, and a file whose size or time moved - or
        /// was never recorded, is not older than the tree's last whole sync (<see cref="Tree.Written"/>), or could not be read
        /// - listed again as a sync by its path lists it; one whose blob moved read again, one that is a link now kept as one,
        /// one gone taken out, and one that could not be read read again, taken where it reads now and changing nothing where
        /// it still does not; the tree linked again and written where any changed, as dirty as git says those files are; then
        /// each extract the files name read, before any sync can take it out.
        /// </summary>
        public async Task<(Tree Tree, CodebaseGraphChecked Checked)> CheckAsync(Tree current, IReadOnlyList<string> paths)
        {
            var standings = _graph.Standings.GetOrAdd(_view, _ => new(StringComparer.Ordinal));

            // A link or a submodule the graph does not read stands as it was listed: there is no file of it to look at.
            var named = paths.Distinct(StringComparer.Ordinal)
                .Where(path => current.Entries.TryGetValue(path, out var entry) && entry.State is not (CodeFileStates.Link or CodeFileStates.Submodule))
                .ToList();
            var moved = new Dictionary<string, Listed?>(StringComparer.Ordinal);
            var toList = new List<(string Path, FileStanding Standing)>();

            foreach (var path in named)
            {
                var entry = current.Entries[path];
                var standing = _service._fileSystem.StandingOf(Absolute(path));

                if (standing is null)
                {
                    moved[path] = null;
                    continue;
                }

                // A file the tree could not read is read again by every check, as by every sync, whatever its blob.
                if (entry.State == CodeFileStates.Unreadable)
                {
                    toList.Add((path, standing));
                    continue;
                }

                // What was recorded of the file of this blob: by a check that found it moved and its blob not, else by its read.
                var recorded = standings.TryGetValue(path, out var seen) && seen.Blob == entry.Blob ? (seen.Bytes, seen.Written)
                    : current.Here.Contains(path) ? (entry.Bytes, entry.Mtime)
                    : default((long Bytes, DateTime? Written)?);

                // Trusted only where its time is older than the tree's last whole sync, as git trusts an entry older than
                // its index: one written in the tick it was looked at, or after, may have been written again since with
                // neither its size nor its time moving. The order only ever asks git again: what is trusted is equal.
                if (recorded is not { Written: { } at } stood || current.Written is not { } synced || at >= synced
                    || stood.Bytes != standing.Length || at != standing.Written)
                {
                    toList.Add((path, standing));
                }
            }

            // The one question every listing asks, asked of these paths alone: the index's blob where git finds a file clean -
            // which is no hash of what it holds where git would clean it otherwise - the one git gives its working file where
            // it changed, a link where one lies along it, and none where it went.
            var (listed, _, dirty) = await ListAsync([.. toList.Select(file => file.Path)], withHead: false).ConfigureAwait(false);
            var unread = new List<(string Path, string Blob)>();

            foreach (var (path, standing) in toList)
            {
                var entry = current.Entries[path];

                if (listed.GetValueOrDefault(path) is not { } file)
                {
                    moved[path] = null;
                }
                else if (file is not { Link: false, Submodule: false } || file.Blob != entry.Blob)
                {
                    moved[path] = file;
                }
                else if (entry.State == CodeFileStates.Unreadable)
                {
                    unread.Add((path, file.Blob!));
                }
                else
                {
                    standings[path] = (standing.Length, standing.Written, file.Blob);
                }
            }

            // A file the tree could not read, its blob as it was, is read again: the tree takes what reads now, and one that
            // still does not changes nothing and writes nothing.
            var reread = (await ReadAsync(unread).ConfigureAwait(false))
                .Where(file => file.Entry.State != CodeFileStates.Unreadable)
                .ToDictionary(file => file.Path, file => file.Entry, StringComparer.Ordinal);
            var tree = current;

            // The tree is as dirty as git says the files it was asked of are: a file read again, its blob as it was, is no change of it.
            if (moved.Count > 0 || reread.Count > 0)
            {
                tree = await ApplyAsync(current, moved, current.Head, dirty, moved.Count + reread.Count, reread).ConfigureAwait(false);
            }

            // The extracts the answer is made from are read while this check is a sync under way, which no sweep comes
            // between: one read after it returned could be gone. The read holds them once they are.
            var extracts = new Dictionary<string, FileExtract>(StringComparer.Ordinal);
            var read = _service.ReadOf(_run, tree, extracts);

            foreach (var path in named)
            {
                if (read.Extract(path) is { } extract)
                {
                    extracts[path] = extract;
                }
            }

            return (tree, new CodebaseGraphChecked(read, named.Count, toList.Count, _extracted, moved.Values.Count(file => file is null)));
        }

        /// <summary>
        /// The tree's graph with each of <paramref name="asked"/> made what git says of it - kept where its blob and reading
        /// did not change, read again where they did, taken out where git holds none - and each of <paramref name="read"/>
        /// taken as it was read already, linked again and written.
        /// </summary>
        private async Task<Tree> ApplyAsync(Tree current, IReadOnlyDictionary<string, Listed?> asked, string? head, bool dirty, int listed, IReadOnlyDictionary<string, CodeGraphFileEntry>? read = null)
        {
            var entries = new SortedDictionary<string, CodeGraphFileEntry>(current.Entries.ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal), StringComparer.Ordinal);
            var here = current.Here.ToHashSet(StringComparer.Ordinal);
            var toRead = new List<(string Path, string Blob)>();
            var removed = 0;
            var sameReading = current.Manifest is { } manifest ? SameReading(manifest) : BaseOf(current.Base) is { } stood && SameReading(stood);

            foreach (var (path, file) in asked)
            {
                if (file is null)
                {
                    removed += entries.Remove(path) ? 1 : 0;
                    here.Remove(path);
                    continue;
                }

                var baseEntry = current.View is not null ? BaseOf(current.Base)?.Files.GetValueOrDefault(path) : null;

                if ((Kept(path, file, entries.GetValueOrDefault(path), sameReading) ?? Kept(path, file, baseEntry, sameReading)) is { } kept)
                {
                    entries[path] = kept;
                }
                else
                {
                    toRead.Add((path, file.Blob!));
                }
            }

            var made = await ReadAsync(toRead).ConfigureAwait(false);

            foreach (var (path, entry) in made.Concat(read?.Select(file => (file.Key, file.Value)) ?? []))
            {
                entries[path] = entry;
                here.Add(path);
            }

            if (current.Manifest is { } before)
            {
                return await WriteBaseAsync(before, ownPrevious: true, entries, head ?? before.Head, before.Dirty || dirty, listed, removed, rebuilt: null, seededFrom: null, whole: false, current.Written).ConfigureAwait(false);
            }

            var baseTree = BaseOf(current.Base) ?? throw new InvalidOperationException("a view stands on a base this process holds");
            var linked = await LinkAsync(entries, current.Linked is not null ? current : null).ConfigureAwait(false);
            var (overlay, deleted) = Overlay(entries, baseTree.Files);
            var view = current.View! with { Head = head ?? current.View!.Head, Dirty = current.View!.Dirty || dirty, SyncedAt = _service._time.GetUtcNow(), Files = overlay, Deleted = deleted };

            _service._store.WriteGraphView(_run, view);
            Replaced(current.Entries, entries);

            var tree = new Tree(_view, _tree, entries, here.Where(overlay.ContainsKey).ToHashSet(StringComparer.Ordinal), linked.Files, linked.Linked, current.Base) { View = view, Written = current.Written };
            _graph.Views[_view] = tree;

            return tree with { Synced = Said(tree, entries, listed, removed, linked.Linked, rebuilt: null, seededFrom: null) };
        }

        /// <summary>What this sync answers and records - a line of <c>sync.jsonl</c> and a <see cref="GraphSynced"/> event - for <paramref name="tree"/>.</summary>
        private CodebaseGraphSynced Said(Tree tree, IReadOnlyDictionary<string, CodeGraphFileEntry> files, int listed, int removed, CodeGraphLinked? linked, string? rebuilt, string? seededFrom)
        {
            var symbols = tree.Symbols ?? (linked is not null ? SymbolsOf(linked, tree.Base) : null) ?? Symbols(tree.Base);
            var seconds = Math.Round(_service._time.GetElapsedTime(_started).TotalSeconds, 3);
            List<CodebaseGraphSkipped> skipped = [.. _unnamed.Values.Concat(files.Where(file => !Declares(file.Value)).Select(file => new CodebaseGraphSkipped(file.Key, file.Value.State, file.Value.Why)))];

            _service._store.AppendGraphSync(_run, new CodeGraphSyncLine(_service._time.GetUtcNow(), _view, _request.Why, listed, _hashed, _extracted, _reused, removed, seconds)
            {
                Rebuilt = rebuilt,
                SeededFrom = seededFrom,
                Resolved = linked?.References,
                Unreadable = skipped.Count(file => file.State == CodeFileStates.Unreadable),
            });
            _service._output.Publish(new GraphSynced(_run.GraphDirectory, _view, _request.Why, listed, _extracted, _reused, removed, seconds));

            return new CodebaseGraphSynced(
                _run.GraphDirectory,
                _view,
                tree.Head,
                tree.Manifest?.Dirty ?? tree.View?.Dirty ?? false,
                files.Count,
                listed,
                _hashed,
                _extracted,
                _reused,
                removed,
                symbols?.Nodes.Count ?? 0,
                symbols?.Edges.Count ?? 0,
                Languages(files, symbols),
                skipped,
                seconds)
            {
                Rebuilt = rebuilt,
                SeededFrom = seededFrom,
                Resolved = linked?.References,
                Base = tree.Base,
            };
        }

        /// <summary>
        /// Says to the next sweep that a record now holding <paramref name="after"/> was written in place of one holding
        /// <paramref name="before"/> that named an extract it does not - read again, removed, or no longer read - or in place
        /// of one this sync did not hold.
        /// </summary>
        private void Replaced(IReadOnlyDictionary<string, CodeGraphFileEntry>? before, IReadOnlyDictionary<string, CodeGraphFileEntry> after)
        {
            if (before is null || before.Any(file => file.Value.Extract is { } extract && after.GetValueOrDefault(file.Key)?.Extract != extract))
            {
                _graph.Unswept();
            }
        }

        /// <summary>Whether the tree is read as <paramref name="manifest"/>'s files were: by the same implementations at the same versions, under the same languages.</summary>
        private bool SameReading(CodeGraphManifest manifest)
            => manifest.Extractors.SequenceEqual(_versions)
                && (manifest.Languages ?? []).SequenceEqual(_shape.Languages ?? [], StringComparer.Ordinal)
                && (manifest.Languages is null) == (_shape.Languages is null);

        /// <summary>The entry of <paramref name="file"/> kept from <paramref name="before"/>, where it is of that blob and nothing else that decides it changed; null otherwise.</summary>
        private CodeGraphFileEntry? Kept(string path, Listed file, CodeGraphFileEntry? before, bool sameReading)
        {
            before = _request.Rebuild ? null : before;

            if (Settled(path, file, before, sameReading, null) is not { } kept)
            {
                return null;
            }

            if (before is not null && before.Blob == kept.Blob)
            {
                _reusedPaths.Add(path);
                _reused++;
            }

            return kept;
        }

        /// <summary>
        /// What the sync starts from: the run's own manifest; where it has none, the newest earlier run's that reads, whose
        /// extracts are copied as they are reused; and why the graph is built again whole, where it is.
        /// </summary>
        private (CodeGraphManifest? Manifest, OrchestratorLayout? From, string? Rebuilt, string? SeededFrom) Previous()
        {
            CodeGraphManifest? own;

            try
            {
                own = _service._store.ReadGraphManifest(_run);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
            {
                return (null, null, _request.Rebuild ? Rebuilding : $"its manifest did not read, and a graph is never guessed at: {ex.Message}", null);
            }

            // Asked to rebuild, the graph keeps nothing it read, and counts what left it all the same.
            if (_request.Rebuild || own is not null)
            {
                return (own, null, _request.Rebuild ? Rebuilding : null, null);
            }

            var seed = _service._store.Orchestrators(_request.Repository)
                .Where(other => !string.Equals(other.Name, _run.Name, StringComparison.Ordinal))
                .Select(other => (Run: other, Manifest: ReadOrNull(other)))
                .Where(other => other.Manifest is not null)
                .OrderByDescending(other => other.Manifest!.SyncedAt)
                .ThenBy(other => other.Run.Name, StringComparer.Ordinal)
                .FirstOrDefault();

            return seed.Manifest is { } seeded ? (seeded, seed.Run, null, seed.Run.Name) : (null, null, null, null);
        }

        /// <summary>Why a graph a step said to rebuild was built again whole.</summary>
        private const string Rebuilding = "the step said rebuild: every file is extracted again";

        /// <summary>
        /// Another run's manifest, where it has one that reads with this build's schema; null where it has none or one that
        /// does not, which seeds nothing. One that could not be read at all is no record that does not read, and fails the sync.
        /// </summary>
        private CodeGraphManifest? ReadOrNull(OrchestratorLayout other) => _service.ManifestOrNull(other);

        /// <summary>
        /// Every file of the tree the graph keeps - or of <paramref name="paths"/> alone - with its blob and what git records
        /// it as: the index's paths and objects, less what is gone, the changed and untracked ones with the blobs git gives
        /// their working files - less what no implementation is ever given, and what the workflow's patterns leave out. A
        /// name that is not UTF-8 names no file this tool opens, and is said as unreadable. A path whose file git does not look
        /// at - kept out of the checkout, as a sparse checkout or skip-worktree keeps it, or marked assume-unchanged - is in the
        /// graph where its file is there, hashed and read as it stands, and nowhere else; a repository inside the tree that git
        /// lists whole as untracked is not entered, as a submodule is not. What it could not name is said by this sync, and
        /// what it hashed counted.
        /// </summary>
        /// <param name="paths">The paths to list; null for the whole tree.</param>
        /// <param name="withHead">Whether the tree's HEAD is asked too: a check of files has no use for it.</param>
        private async Task<(SortedDictionary<string, Listed> Files, string? Head, bool Dirty)> ListAsync(IReadOnlyList<string>? paths, bool withHead = true)
        {
            var listing = await ListingOf(paths, withHead).ConfigureAwait(false);

            foreach (var (name, skipped) in listing.Unnamed)
            {
                _unnamed.TryAdd(name, skipped);
            }

            _hashed += listing.Hashed;
            return (listing.Files, listing.Head, listing.Dirty);
        }

        /// <summary>
        /// The listing <see cref="ListAsync"/> takes, and nothing said or counted of it: what a check after a read asks again.
        /// </summary>
        /// <param name="paths">The paths to list; null for the whole tree.</param>
        /// <param name="withHead">Whether the tree's HEAD is asked too: the listing's head is null where it is not.</param>
        private async Task<Listing> ListingOf(IReadOnlyList<string>? paths, bool withHead)
        {
            IReadOnlyList<GitIndexEntry> indexed;
            IReadOnlyList<GitWorkingFile> working;
            string? head;
            var unnamed = new SortedDictionary<string, CodebaseGraphSkipped>(StringComparer.Ordinal);

            // No path is no listing: an empty list of paths names the whole tree to git.
            if (paths is { Count: 0 })
            {
                return new Listing(new SortedDictionary<string, Listed>(StringComparer.Ordinal), null, false, unnamed, 0);
            }

            try
            {
                var git = _service._gitClient;
                indexed = paths is null ? await git.ListIndexAsync(_tree, _cancellationToken).ConfigureAwait(false) : await git.ListIndexAsync(_tree, paths, _cancellationToken).ConfigureAwait(false);
                working = paths is null ? await git.ListWorkingFilesAsync(_tree, _cancellationToken).ConfigureAwait(false) : await git.ListWorkingFilesAsync(_tree, paths, _cancellationToken).ConfigureAwait(false);
                head = withHead ? await git.ResolveCommitAsync(_tree, "HEAD", _cancellationToken).ConfigureAwait(false) : null;
            }
            catch (Exception ex) when (ex is HarnessException or ProgramStartException)
            {
                throw Unlisted(ex);
            }

            var files = new SortedDictionary<string, Listed>(StringComparer.Ordinal);
            var toHash = new List<string>();

            // Whether each directory a path git does not look at lies in stands, asked once a directory: a sparse checkout
            // leaves out whole directories, and their paths are looked at no further.
            var standing = new Dictionary<string, bool>(StringComparer.Ordinal);

            // A conflicted path is listed once for each of its stages, and taken once; by its bytes, since two names that
            // are no UTF-8 can decode to one text.
            foreach (var file in indexed.DistinctBy(entry => entry.Name.Quoted, StringComparer.Ordinal))
            {
                if (!Kept(file.Name, unnamed))
                {
                    continue;
                }

                // git does not look at the file of a path marked skip-worktree or assume-unchanged, so its index object says
                // nothing of what is there: an edit of it is in no listing. Where its directory does not stand, no file is
                // there, nor any link left there, and nothing more is asked of it; where it does, its file is, or a link
                // left there leading nowhere.
                if ((file.IsSkipWorktree || file.IsAssumedUnchanged) && !file.IsSubmodule && !file.IsLink)
                {
                    if (DirectoryStands(file.Name.Text, standing) && (_service._fileSystem.FileExists(Absolute(file.Name.Text)) || ThroughLink(file.Name.Text)))
                    {
                        files[file.Name.Text] = new Listed(null, Link: false, Submodule: false);
                        toHash.Add(file.Name.Text);
                    }

                    continue;
                }

                files[file.Name.Text] = new Listed(file.ObjectId, file.IsLink, file.IsSubmodule);
            }

            var present = working.Where(file => !file.Gone).Select(file => file.Name.Text).ToHashSet(StringComparer.Ordinal);

            foreach (var deleted in working.Where(file => file.Gone && !present.Contains(file.Name.Text)))
            {
                files.Remove(deleted.Name.Text);
            }

            // An untracked repository inside the tree is listed whole, by its directory's name.
            foreach (var file in working.Where(file => !file.Gone && file.Differs && !file.Name.Text.EndsWith('/') && Kept(file.Name, unnamed)))
            {
                var path = file.Name.Text;
                var known = files.GetValueOrDefault(path);

                if (known is { Submodule: true } or { Blob: null, Link: false })
                {
                    continue;
                }

                if (file.Mode == GitEntry.LinkMode)
                {
                    files[path] = new Listed(known?.Blob, Link: true, Submodule: false);
                    continue;
                }

                files[path] = new Listed(null, Link: false, Submodule: false);
                toHash.Add(path);
            }

            var (there, linked, gone) = Looked(toHash);

            foreach (var path in linked)
            {
                files[path] = new Listed(null, Link: true, Submodule: false);
            }

            var blobs = await HashAsync(there).ConfigureAwait(false);

            // A file gone since git listed it is not given to git to hash, nor one gone while git hashed it: it is listed
            // unreadable, as one gone before its read is.
            foreach (var path in gone.Concat(there.Where((_, at) => blobs[at] is null)))
            {
                files.Remove(path);
                unnamed.TryAdd(path, new CodebaseGraphSkipped(path, CodeFileStates.Unreadable, "it was gone when it was read"));
            }

            for (var at = 0; at < there.Count; at++)
            {
                if (blobs[at] is { } blob)
                {
                    files[there[at]] = files[there[at]] with { Blob = blob };
                }
            }

            // The tool's own state and the credential slots are no change of the tree, though git lists them where they are not ignored.
            return new Listing(files, head, working.Any(file => !Never(file.Name.Text)), unnamed, blobs.Count(blob => blob is not null));
        }

        /// <summary>The blob git gives each of <paramref name="paths"/>' working files; null for each that was gone when git looked.</summary>
        private async Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths)
        {
            try
            {
                return await _service._gitClient.HashWorkingFilesAsync(_tree, paths, _cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HarnessException or ProgramStartException)
            {
                throw Unlisted(ex);
            }
        }

        /// <summary>
        /// Whether the graph keeps the file git names <paramref name="name"/>: never one of the credential slots or the tool's
        /// own state - what the graph never extracts it never serves - only one the workflow's patterns graph, and only one
        /// this tool can open and its records can name; one of those it cannot is said in <paramref name="unnamed"/>.
        /// </summary>
        private bool Kept(GitName name, IDictionary<string, CodebaseGraphSkipped> unnamed)
        {
            if (Never(name.Text) || !Graphs(name.Text))
            {
                return false;
            }

            var why = !name.IsUtf8 ? "its name is not UTF-8, and this tool opens a file only by a UTF-8 name"
                : Unnameable(name.Text) ? NoRecordedName
                : null;

            if (why is null)
            {
                return true;
            }

            // Listed by the index and by status alike where it changed: said once.
            unnamed.TryAdd(name.Quoted, new CodebaseGraphSkipped(name.Quoted, CodeFileStates.Unreadable, why));
            return false;
        }

        /// <summary>Why a file written while it was read keeps nothing.</summary>
        private const string ChangedWhileRead = "it changed while it was read";

        /// <summary>
        /// How many files read past which the listing after the reads lists the tree whole: by their paths, each command
        /// line's worth of them has git read the whole index, so past two of them one listing of the tree costs less.
        /// </summary>
        private const int ListedWholePast = 2 * GitClient.CommandLineBatchSize;

        /// <summary>
        /// Whether the file at <paramref name="path"/> stands now as <paramref name="seen"/> says it stood when it was read:
        /// there, its size and its time as they were. One that cannot be looked at stands as nothing it was.
        /// </summary>
        private bool StandsAsSeen(string path, FileStanding seen)
        {
            try
            {
                return _service._fileSystem.StandingOf(Absolute(path)) is { } now && now.Length == seen.Length && now.Written == seen.Written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Why a file whose name its records cannot hold is not in the graph.</summary>
        private const string NoRecordedName = "its name is no path relative to the tree that every machine reads alike - a drive, a leading "
            + "slash or backslash, or a part empty, '.' or '..', by either slash - and the graph's records name a file only by one";

        /// <summary>
        /// Whether <paramref name="path"/> is a name the graph's records cannot hold (<see cref="OrchestrationRules.RelativePathProblem"/>):
        /// a file git holds by such a name on a machine that allows it is said, never written into a record that would not read back.
        /// </summary>
        private static bool Unnameable(string path) => OrchestrationRules.RelativePathProblem(path, "a file's path") is not null;

        /// <summary>
        /// Whether the directory <paramref name="path"/> lies in stands, asked of the file system once a directory and kept in
        /// <paramref name="standing"/>: the tree's root always does.
        /// </summary>
        private bool DirectoryStands(string path, Dictionary<string, bool> standing)
        {
            var slash = path.LastIndexOf('/');

            if (slash < 0)
            {
                return true;
            }

            var directory = path[..slash];

            if (!standing.TryGetValue(directory, out var stands))
            {
                stands = _service._fileSystem.DirectoryExists(Absolute(directory));
                standing[directory] = stands;
            }

            return stands;
        }

        /// <summary>Whether the workflow's patterns graph <paramref name="path"/>, as this machine's file system compares names.</summary>
        private bool Graphs(string path) => _shape.Graphs(path, _service._platform.PathComparison);

        /// <summary>Whether <paramref name="path"/> is in a credential slot or the tool's own state.</summary>
        private bool Never(string path)
            => PathPatterns.Matches(_never, path, _service._platform.PathComparison);

        /// <summary>
        /// Each of <paramref name="paths"/> as it stands before git is asked to hash it: a file there; one that leads out of
        /// what git names - a link, or a path a link lies along, a directory junction git walks into as a directory among
        /// them - which is listed a link and neither hashed nor read; or one gone. The look every listing - a check's among
        /// them - takes of a file it is to hash, so nothing is ever read through a link.
        /// </summary>
        private (List<string> There, List<string> Linked, List<string> Gone) Looked(IEnumerable<string> paths)
        {
            var there = new List<string>();
            var linked = new List<string>();
            var gone = new List<string>();

            foreach (var path in paths)
            {
                (ThroughLink(path) ? linked : _service._fileSystem.FileExists(Absolute(path)) ? there : gone).Add(path);
            }

            return (there, linked, gone);
        }

        /// <summary>
        /// Whether <paramref name="path"/> is a link, or is reached through one along it below the tree: what it leads to is
        /// not the file git names. One that cannot be followed is taken for one.
        /// </summary>
        private bool ThroughLink(string path)
        {
            try
            {
                _reached ??= _service._fileSystem.ResolveLinks(_tree);
                return LinkPaths.LeadsElsewhere(_service._fileSystem, _tree, _reached, path, _service._platform.PathComparison) is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        /// <summary>
        /// The entry of a file whose blob has not changed, kept as it was - its extract copied from the run it was seeded from
        /// - where nothing else that decides it changed: the implementation that claims it and its version, the largest
        /// file read; null where the file is to be read again.
        /// </summary>
        private CodeGraphFileEntry? Settled(string path, Listed file, CodeGraphFileEntry? before, bool sameReading, OrchestratorLayout? from)
        {
            if (file.Submodule)
            {
                return new CodeGraphFileEntry(file.Blob, CodeFileStates.Submodule, 0) { Why = "a submodule, which the graph does not enter" };
            }

            if (file.Link)
            {
                return new CodeGraphFileEntry(file.Blob, CodeFileStates.Link, 0) { Why = "a link, or a path one lies along, which the graph does not follow" };
            }

            if (before is null || before.Blob != file.Blob)
            {
                return null;
            }

            // A limit lowered under a file read, or found binary or ambiguous, lists it too large: a fresh graph would.
            if (before.State != CodeFileStates.TooLarge && before.Bytes > _shape.MaxFileBytes)
            {
                return null;
            }

            switch (before.State)
            {
                case CodeFileStates.Binary:
                    return before;

                case CodeFileStates.TooLarge:
                    return before.Bytes > _shape.MaxFileBytes ? before : null;

                case CodeFileStates.Ambiguous:
                    return sameReading ? before : null;

                case CodeFileStates.Parsed or CodeFileStates.Outlined or CodeFileStates.Listed or CodeFileStates.Generated:
                    // The same blob read by the same implementations under the same languages goes to the one it went to;
                    // otherwise its claimant is found again, where that needs no read of it.
                    var claimant = sameReading ? _service._extractors.Find(before.Language!) : Claimant(path);

                    if (claimant is null || claimant.Language != before.Language)
                    {
                        return null;
                    }

                    var name = CodeGraphIds.ExtractName(file.Blob!, path, claimant.Language, claimant.Version);

                    if (name != before.Extract || !Present(name, from))
                    {
                        return null;
                    }

                    // A file read before keeps its generated mark in its why; whether it is left out is the shape's now.
                    return before with { State = StateOf(claimant, generated: before.Why is not null) };

                default:
                    return null;
            }
        }

        /// <summary>
        /// The implementation that reads <paramref name="path"/> where that is known without reading it - the fallback, or
        /// the one claimant of its exact name or extension that asks no content test - and used; null where its content
        /// must be read to know.
        /// </summary>
        private ICodeGraphExtractor? Claimant(string path)
        {
            var claimants = _service._extractors.ClaimantsOf(path);

            var claimant = claimants.Count switch
            {
                0 => _service._extractors.Fallback,
                1 when claimants[0].Claim.ContentTest is null => claimants[0].Extractor,
                _ => null,
            };

            return claimant is null ? null : _shape.Uses(claimant.Language) ? claimant : _service._extractors.Fallback;
        }

        /// <summary>Whether the extract <paramref name="name"/> is in this run's graph - copied there from the run it was seeded from, where it is there.</summary>
        private bool Present(string name, OrchestratorLayout? from)
        {
            var own = _run.GraphExtractFile(name);

            if (_service._fileSystem.FileExists(own))
            {
                return true;
            }

            if (from is null || !_service._fileSystem.FileExists(from.GraphExtractFile(name)))
            {
                return false;
            }

            _service._fileSystem.CreateDirectory(Path.GetDirectoryName(own)!);
            _service._fileSystem.CopyFile(from.GraphExtractFile(name), own, overwrite: true);
            return true;
        }

        /// <summary>
        /// Reads each of <paramref name="files"/> and gives it to its implementation, in parallel on one of every
        /// <see cref="ReadShare"/> of the machine's processors; then lists every file whose bytes decided its entry once more,
        /// as a sync would, and keeps what was made of each only where the listing still gives it the blob it was read under,
        /// and the file, after that listing, still stands as its read saw it - an extract is never stored under a blob its
        /// bytes were not. A file whose size or time moved while it was read or after, or whose blob moved or that went by the
        /// listing after, is listed unreadable, which the next sync reads again.
        /// </summary>
        /// <remarks>
        /// The listing, not a hash of the file, is asked again because the blob a file is read under is the listing's: the
        /// index's where git finds the file clean - which, for one git would clean otherwise than the index holds it, is no hash
        /// of what it holds - and the one git gives its working file where it changed. Asking the one question twice compares
        /// like with like, and runs no clean filter over a file git found clean. A file truncated and written again whole, or
        /// grown past maxFileBytes and back, reads clean to git again though what was read of it was none of what git lists:
        /// its size or time after the listing is what tells. A file too large is not read, so not listed again, and is held to
        /// its size and time alone; an unreadable one keeps nothing.
        /// </remarks>
        private async Task<IReadOnlyList<(string Path, CodeGraphFileEntry Entry)>> ReadAsync(IReadOnlyList<(string Path, string Blob)> files)
        {
            var read = new ConcurrentBag<(string Path, string Blob, CodeGraphFileEntry Entry, CodeGraphStoredExtract? Stored, FileStanding? Seen)>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / ReadShare), CancellationToken = _cancellationToken };

            await Parallel.ForEachAsync(files, options, async (file, token) =>
            {
                var (entry, stored, seen) = await ReadOneAsync(file.Path, file.Blob, token).ConfigureAwait(false);
                read.Add((file.Path, file.Blob, entry, stored, seen));
            }).ConfigureAwait(false);

            var looked = read.Where(file => file.Entry.State is not (CodeFileStates.Unreadable or CodeFileStates.TooLarge)).ToList();
            var moved = new HashSet<string>(StringComparer.Ordinal);

            if (looked.Count > 0)
            {
                // By their paths, every command line's worth of them has git read the whole index once: past a few, the tree
                // is listed once, whole, and each looked up in it.
                var again = (await ListingOf(looked.Count > ListedWholePast ? null : [.. looked.Select(file => file.Path)], withHead: false).ConfigureAwait(false)).Files;
                moved.UnionWith(looked.Where(file => !again.TryGetValue(file.Path, out var now) || now.Link || now.Submodule || now.Blob != file.Blob).Select(file => file.Path));
            }

            moved.UnionWith(read.Where(file => file.Seen is { } seen && !moved.Contains(file.Path) && !StandsAsSeen(file.Path, seen)).Select(file => file.Path));

            var entries = new List<(string Path, CodeGraphFileEntry Entry)>(read.Count);

            foreach (var file in read)
            {
                if (moved.Contains(file.Path))
                {
                    entries.Add((file.Path, new CodeGraphFileEntry(file.Blob, CodeFileStates.Unreadable, 0) { Why = ChangedWhileRead }));
                    continue;
                }

                if (file.Stored is { } stored)
                {
                    var extract = _run.GraphExtractFile(file.Entry.Extract!);
                    _service._fileSystem.CreateDirectory(Path.GetDirectoryName(extract)!);
                    _service._store.WriteGraphExtract(extract, stored);
                    _read[file.Path] = stored.Extract;
                    _extracted++;
                }

                entries.Add((file.Path, file.Entry));
            }

            return entries;
        }

        /// <summary>
        /// One file read, by the core's rules first - its size, then its bytes, binary or undecodable, gone since it was
        /// listed - and then by the implementation that claims it, whose extract is completed with every node's digest: its
        /// entry, the extract to store once its blob is confirmed, and how the file stood when it was read, where its size
        /// or its bytes decided its entry.
        /// </summary>
        private async Task<(CodeGraphFileEntry Entry, CodeGraphStoredExtract? Stored, FileStanding? Seen)> ReadOneAsync(string path, string blob, CancellationToken token)
        {
            var absolute = Absolute(path);
            FileStanding? standing;
            byte[] bytes;

            try
            {
                standing = _service._fileSystem.StandingOf(absolute);

                if (standing is null)
                {
                    return (new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, 0) { Why = "it was gone when it was read" }, null, null);
                }

                if (standing.Length > _shape.MaxFileBytes)
                {
                    return (new CodeGraphFileEntry(blob, CodeFileStates.TooLarge, standing.Length) { Why = string.Create(CultureInfo.InvariantCulture, $"{standing.Length:N0} bytes, over maxFileBytes ({_shape.MaxFileBytes:N0})"), Mtime = standing.Written }, null, standing);
                }

                bytes = await _service._fileSystem.ReadAllBytesAsync(absolute, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, 0) { Why = "it was gone when it was read" }, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, 0) { Why = $"it could not be read: {ex.Message.TrimEnd('.')}" }, null, null);
            }

            // A write while it was read leaves bytes of no one content, though it may leave the file's blob as it was: its
            // size or time moved, or it read to another length than it stood at. Nothing is made of such bytes.
            if (bytes.Length != standing.Length || !StandsAsSeen(path, standing))
            {
                return (new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, 0) { Why = ChangedWhileRead }, null, null);
            }

            var decoded = CodeFileText.Decode(bytes);

            if (!decoded.IsRead)
            {
                return (new CodeGraphFileEntry(blob, decoded.State, bytes.Length) { Why = decoded.Why, Mtime = standing.Written }, null, standing);
            }

            var text = decoded.Text;
            var route = _service._extractors.Route(path, CodeFileText.Probe(text));

            if (route.Failed is { } failed)
            {
                return (new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, bytes.Length) { Why = failed, Mtime = standing.Written }, null, null);
            }

            if (route.Extractor is not { } claimant)
            {
                return (new CodeGraphFileEntry(blob, CodeFileStates.Ambiguous, bytes.Length) { Why = route.Ambiguous, Mtime = standing.Written }, null, standing);
            }

            var extractor = _shape.Uses(claimant.Language) ? claimant : _service._extractors.Fallback;
            var input = new CodeFileInput(path, text);
            var lines = input.Lines;
            FileExtract extract;

            try
            {
                extract = extractor.Extract(input).WithDigests(lines);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The contract's kit holds every implementation to answering for any text; one that throws is a defect of
                // it, said for the one file and never the end of the sync.
                return (new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, bytes.Length) { Why = $"its implementation '{extractor.Language}' failed on it: {ex.Message.TrimEnd('.')}", Mtime = standing.Written }, null, null);
            }

            var stored = new CodeGraphStoredExtract(CodeGraphManifest.CurrentSchema, extractor.Language, extractor.Version, blob, path, lines.Count, extract);

            if (stored.Problem() is { } wrong)
            {
                return (new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, bytes.Length) { Why = $"its implementation '{extractor.Language}' answered what is no extract: {wrong}", Mtime = standing.Written }, null, null);
            }

            // A file read keeps why it is generated, where its implementation said, in its why: the mark a later sync keeps.
            var entry = new CodeGraphFileEntry(blob, StateOf(extractor, extract.Generated is not null), bytes.Length)
            {
                Language = extractor.Language,
                Why = extract.Generated,
                Lines = lines.Count,
                Mtime = standing.Written,
                Extract = CodeGraphIds.ExtractName(blob, path, extractor.Language, extractor.Version),
            };

            return (entry, stored, standing);
        }

        /// <summary>The state a file read by <paramref name="extractor"/> is in.</summary>
        private string StateOf(ICodeGraphExtractor extractor, bool generated)
            => generated && _shape.ExcludeGenerated ? CodeFileStates.Generated
                : extractor.Claims.Fallback ? CodeFileStates.Listed
                : extractor.Outlines ? CodeFileStates.Outlined
                : CodeFileStates.Parsed;

        /// <summary>
        /// Links every file of <paramref name="entries"/> - from what <paramref name="from"/> linked, a file it held alike
        /// given as the very file it was, where it was linked in this process; whole otherwise - each other extract taken
        /// from this sync's reads or read back, one that does not read made again from its file, and a generated file's left
        /// out under <c>exclude</c>.
        /// </summary>
        private async Task<(IReadOnlyDictionary<string, CodeGraphFile> Files, CodeGraphLinked Linked)> LinkAsync(SortedDictionary<string, CodeGraphFileEntry> entries, Tree? from)
        {
            var files = new Dictionary<string, CodeGraphFile>(StringComparer.Ordinal);
            var toMake = new List<(string Path, string Blob)>();

            foreach (var (path, entry) in entries)
            {
                if (from?.Files is { } known && known.TryGetValue(path, out var held) && from.Entries.TryGetValue(path, out var was) && SameFile(was, entry))
                {
                    files[path] = held;
                    continue;
                }

                if (entry.Extract is not { } name)
                {
                    files[path] = new CodeGraphFile(path, entry.Language, null);
                    continue;
                }

                if ((_read.TryGetValue(path, out var read) ? read : ExtractOrNull(name, path)?.Extract) is not { } extract)
                {
                    toMake.Add((path, entry.Blob!));
                    continue;
                }

                files[path] = new CodeGraphFile(path, entry.Language, entry.State == CodeFileStates.Generated ? null : extract);
            }

            foreach (var (path, made) in await ReadAsync(toMake).ConfigureAwait(false))
            {
                entries[path] = made;

                // Read after all: counted as read, and no longer as kept.
                if (_reusedPaths.Remove(path))
                {
                    _reused--;
                }

                files[path] = new CodeGraphFile(path, made.Language, made.State == CodeFileStates.Generated ? null : _read.GetValueOrDefault(path));
            }

            List<CodeGraphFile> list = [.. entries.Keys.Select(path => files[path])];
            return (files, from?.Linked is { } linked ? CodeGraphLinker.Relink(linked, list) : CodeGraphLinker.Link(list));
        }

        /// <summary>The extract <paramref name="name"/> of <paramref name="path"/>, where it reads as one made for it; null where it does not.</summary>
        private CodeGraphStoredExtract? ExtractOrNull(string name, string path) => _service.ExtractOrNull(_run, name, path);

        /// <summary>
        /// The run's link result, where it reads and was made from the manifest of <paramref name="digest"/> by this build's
        /// linker; null where it is to be made again - none, one that does not read, or one of another manifest or linker.
        /// One that could not be read at all is no record that does not read, and fails the sync.
        /// </summary>
        private CodeGraphSymbolsFile? Symbols(string digest)
        {
            try
            {
                return _service._store.ReadGraphSymbols(_run) is { } symbols && symbols.Graph == digest && symbols.Linker == CodeGraphLinker.Revision ? symbols : null;
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
            {
                return null;
            }
        }

        /// <summary>For each implementation that read a file, how many it read and the nodes they hold.</summary>
        private static IReadOnlyList<CodebaseGraphLanguage> Languages(IReadOnlyDictionary<string, CodeGraphFileEntry> files, CodeGraphSymbolsFile? symbols)
        {
            var byPath = files.Where(file => file.Value.Language is not null).ToDictionary(file => file.Key, file => file.Value.Language!, StringComparer.Ordinal);

            return [.. byPath.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(language => new CodebaseGraphLanguage(
                language,
                byPath.Count(file => file.Value == language),
                symbols?.Nodes.Count(node => node.Id.StartsWith(language + ":", StringComparison.Ordinal)
                    || (node.Kind == CodeNodeKinds.File && byPath.GetValueOrDefault(node.Paths[0]) == language)) ?? 0))];
        }

        /// <summary>Whether the graph holds declarations of a file in <paramref name="entry"/>'s state.</summary>
        private static bool Declares(CodeGraphFileEntry entry) => entry.State is CodeFileStates.Parsed or CodeFileStates.Outlined or CodeFileStates.Listed;

        private string Absolute(string path) => Path.Combine(_tree, path.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// The time the file system stamped <paramref name="record"/> with, just written; null where it cannot be read, so
        /// a check trusts no file's size and time by it.
        /// </summary>
        private DateTime? Stamped(string record)
        {
            try
            {
                return _service._fileSystem.StandingOf(record)?.Written;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// What a listing that failed is said as: git that could not be started did not answer
        /// (<see cref="HarnessExit.ToolMissing"/>); git that ran and failed - refused, timed out, or answered what this build
        /// cannot read - keeps its own code and its words.
        /// </summary>
        private HarnessException Unlisted(Exception ex)
            => ex is HarnessException { ExitCode: not HarnessExit.ToolMissing } failed
                ? new(failed.ExitCode, $"git could not list '{_tree}' for the codebase graph: {failed.Message}", failed)
                : new(HarnessExit.ToolMissing, $"git did not answer while the codebase graph listed '{_tree}': {ex.Message}", ex);
    }

    /// <summary>The view <paramref name="tree"/> as an answer reads it, holding <paramref name="checkedExtracts"/> as the extracts of the files a check named.</summary>
    private CodebaseGraphRead ReadOf(OrchestratorLayout run, Tree tree, IReadOnlyDictionary<string, FileExtract> checkedExtracts)
        => new(
            tree.Name,
            tree.Path,
            tree.Head,
            tree.Entries,
            tree.Files,
            checkedExtracts,
            () => tree.Symbols ?? SymbolsOf(tree.Linked ?? throw new InvalidOperationException("a tree is answered from once it is linked, or holds its link result"), tree.Base),
            (path, entry) => ExtractOrNull(run, entry.Extract!, path)?.Extract);

    /// <summary>The link result of <paramref name="linked"/>, made from the manifest or the view of <paramref name="digest"/>, each node with its handle.</summary>
    private static CodeGraphSymbolsFile SymbolsOf(CodeGraphLinked linked, string digest)
    {
        var handles = CodeGraphIds.Handles(linked.Symbols.Select(symbol => symbol.Id));

        return new CodeGraphSymbolsFile(
            CodeGraphManifest.CurrentSchema,
            digest,
            CodeGraphLinker.Revision,
            [.. linked.Symbols.Select(symbol => new CodeGraphSymbolEntry(symbol.Id, handles[symbol.Id], symbol.Kind, symbol.Paths))],
            linked.Edges,
            linked.External,
            linked.Unresolved);
    }

    /// <summary>
    /// The extract <paramref name="name"/> of <paramref name="path"/> in <paramref name="run"/>'s graph, where it reads as the
    /// one that name is made of - its path, and the blob, the implementation and the version its name says - and null where
    /// it is none or does not, which is made again from its file. One that could not be read at all is no extract that does
    /// not read, and fails the sync.
    /// </summary>
    private CodeGraphStoredExtract? ExtractOrNull(OrchestratorLayout run, string name, string path)
    {
        try
        {
            return _store.ReadGraphExtract(run.GraphExtractFile(name)) is { } stored
                && stored.Path == path
                && CodeGraphIds.ExtractName(stored.Blob, stored.Path, stored.Language, stored.Version) == name
                ? stored
                : null;
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            return null;
        }
    }

    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly OrchestrationStore _store = store;
    private readonly CodeGraphExtractors _extractors = extractors;
    private readonly IHostPlatform _platform = platform;
    private readonly IToolIdentityProvider _identity = identity;
    private readonly IHarnessOutput _output = output;
    private readonly TimeProvider _time = time;

    /// <summary>One file of the tree as it was listed.</summary>
    /// <param name="Blob">The blob git gives its content, or the submodule's commit; null for a link it does not hold.</param>
    /// <param name="Link">Whether it is a link - git records it as one, or a link lies along its path on disk.</param>
    /// <param name="Submodule">Whether it is a submodule.</param>
    private sealed record Listed(string? Blob, bool Link, bool Submodule);

    /// <summary>What one listing of a tree found.</summary>
    /// <param name="Files">Each file the graph keeps, as it was listed.</param>
    /// <param name="Head">The tree's HEAD; null where it has no commit.</param>
    /// <param name="Dirty">Whether git listed anything that is not the tree's HEAD.</param>
    /// <param name="Unnamed">Each file it could not name, or found gone before git hashed it, by the name it is said by.</param>
    /// <param name="Hashed">How many working files git hashed.</param>
    private sealed record Listing(SortedDictionary<string, Listed> Files, string? Head, bool Dirty, IReadOnlyDictionary<string, CodebaseGraphSkipped> Unnamed, int Hashed);
}
