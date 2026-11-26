using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Pages;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A reason step's serving of the run's codebase graph: the view the engine syncs at the step's start - the agent's it
/// works in, the base for one that reads - read whole, every call of the graph service made through a request of that
/// view (<see cref="FlowGraphSyncs.RequestOf"/>); its context pack made at its start and the run's repository map of its
/// ground of reads - made once for each ground the run's steps share, from the base as it stands when the first graphed
/// reason step of that ground is given its prompt, and read as it is by every one after. Pull, not push: a step at
/// <c>tools</c> is pushed neither - the one tool <c>graph</c>, offered with the operations and the narrowing of reads its
/// step says, serves the map's and the pack's pages from page 1 when asked - unless its <c>graphPromptTokens</c> says a
/// size, which pushes page 1 of its pack; a step at <c>map</c>, which has no tool, is pushed both, the map ahead of the
/// pack, as the <c>&lt;repository-map&gt;</c> and <c>&lt;codebase&gt;</c> parts of the first message of each launch
/// that begins a conversation of its own (<see cref="WorkflowStep.GraphPromptTokensOf"/>). The changes of the codebase are
/// told to a conversation gone on with; each launch's first message, each call and each escape of the narrowing recorded
/// in its <c>graph.jsonl</c>; and its working set written and its figures gathered when it ends. Where the run's graph was
/// never built - every graphify step upstream failed, was skipped or was passed over - a pushed pack is the sentence for
/// a step that left no output, and the tool answers the same. A graph never fails its step: one that cannot be served is
/// said, and the step goes on without it.
/// </summary>
public sealed class FlowStepGraph
{
    /// <summary>
    /// How the run's repository maps are made and kept, as every help text that names them says it: one for each ground of
    /// reads, from the base as it stands when the first step of that ground is given its prompt (<see cref="MapOfAsync"/>,
    /// <see cref="OrchestrationStore.MapOnce"/>), at the files <see cref="OrchestratorLayout.GraphMapFileOf"/> and
    /// <see cref="OrchestratorLayout.GraphMapPageFileOf"/> name.
    /// </summary>
    public const string MapsKept = "made once for each ground of reads the run's steps share - steps whose read rules are the same share one, and no map lists a declaration of a file its steps may not read - from the base as it stands when the first graphed reason step of that ground is given its prompt, kept whole in pages in "
        + OrchestratorLayout.GraphMapFileName + " with the pages after it in map.2.md and on (map-<a digest of the read rules>.md and on for steps whose read rules narrow them)";

    /// <summary>Why a launch was given no pack, where its step asked for none.</summary>
    public const string NoPackAsked = "the step asked for no pack: its graphPromptTokens is 0";

    /// <summary>
    /// Why a launch was given no pack, where its step has the graph tool and says no size to push: its pack is asked of the
    /// tool's <c>pack</c>, from page 1.
    /// </summary>
    public const string NoPackPushed = "the step's graph is tools and its graphPromptTokens pushes no pack: it asks the graph tool's pack for its pages";

    /// <summary>Why a launch was given no pack, where the pack its step asked for held nothing.</summary>
    public const string NoPackHeld = "the step's pack held nothing its view could give";

    /// <summary>Why a launch was given no pack, where it began from the engine's compaction of the conversation.</summary>
    public const string NoPackCompacted = "the launch began from the engine's compaction record, which holds no map and no pack";

    /// <summary>
    /// Why a launch of a step whose pack pages are <paramref name="tokens"/> - <paramref name="pushed"/> of them pushed - is
    /// given no pack, the one place it is decided: <see cref="NoPackAsked"/> where no pack is made; <see cref="NoPackHeld"/>
    /// where the pack made held nothing, said before anything else, for its step is offered no <c>pack</c> to ask either; and
    /// <see cref="NoPackPushed"/> where the pack holds pages and the step pushes none, which it asks of the tool. Null where
    /// page 1 of a pack that holds something is pushed.
    /// </summary>
    /// <param name="tokens">The size of a page of the step's pack; 0 where none is made.</param>
    /// <param name="pushed">The size of page 1 pushed into the first message; 0 where none is.</param>
    /// <param name="held">Whether the pack made holds a page.</param>
    internal static string? NoPackOf(int tokens, int pushed, bool held)
        => tokens == 0 ? NoPackAsked : !held ? NoPackHeld : pushed == 0 ? NoPackPushed : null;

    /// <summary>
    /// Whether <paramref name="ex"/> is a record of the step's graph the disk would not keep or give back - a write or a
    /// read it refused, a record that would not read back - which the step's graph says and never fails its step over.
    /// </summary>
    private static bool Unkept(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidOperationException;

    private readonly OrchestrationStore store;
    private readonly IHarnessOutput output;
    private readonly string lines;
    private readonly string instance;
    private readonly string? noPack;
    private readonly ContextPack? made;
    private AgentGraphSupport? support;
    private CodebaseGraphQueries? view;
    private string section = string.Empty;
    private string? changes;
    private GraphTextPages? told;
    private AgentCodebaseGiven? given;

    private FlowStepGraph(string mode, string from, string viewName, int tokens, bool pushes, string? map, int mapPages, ContextPack? made, string? missing, string? noPack, OrchestrationStore store, IHarnessOutput output, string lines, string instance)
    {
        Mode = mode;
        From = from;
        View = viewName;
        Tokens = tokens;
        Pushes = pushes;

        // The map is pushed to a step at map alone: one at tools asks the tool's map for its pages.
        Map = mode == "map" ? map : null;
        MapPages = mapPages;
        this.made = made;
        Pack = made is { Text.Length: > 0 } ? made : null;
        Missing = missing;
        this.noPack = noPack;
        this.store = store;
        this.output = output;
        this.lines = lines;
        this.instance = instance;
    }

    /// <summary>
    /// Makes the serving of the step <paramref name="run"/>: its mode resolved by the graph key of the step its chain of
    /// work began with and the graphify steps of the run, the view the engine syncs at its start read whole, the map of its
    /// ground of reads made or read, the pack made from its filled prompt and what its step says of its work, and the tool
    /// offered with the operations and the narrowing of reads its step says. Null where the step's mode is <c>off</c>, or
    /// where its graph could not be served - said, and the step goes on without it.
    /// </summary>
    /// <param name="graph">The one graph service every graph is read and written by.</param>
    /// <param name="store">Reads the run's records and keeps the step's graph lines, its working set and the run's maps.</param>
    /// <param name="fileSystem">The file system the graph's records are read on.</param>
    /// <param name="platform">This machine.</param>
    /// <param name="git">Answers a search's files, as the tool reads them.</param>
    /// <param name="clock">Times the tool's searches.</param>
    /// <param name="output">Hears a graph that could not be served, or a line of it that could not be written.</param>
    /// <param name="run">The step, its instance, its declaration and its scope.</param>
    /// <param name="place">The run's place.</param>
    /// <param name="context">The repository's context.</param>
    /// <param name="definition">The agent definition the step's chain of work works by; null where its root names none.</param>
    /// <param name="root">
    /// The step at the root of the step's chain of work (<see cref="ResumeChain.Root"/>): its <c>graph</c> decides the mode
    /// and its <c>graphReads</c> the narrowing, for a step that goes on with another asks the graph that step's
    /// conversation holds; its <c>graphFrom</c> and <c>graphPromptTokens</c> stand where the step says none of its own.
    /// </param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="record">The agent's record, whose worktree names its view.</param>
    /// <param name="ground">The step's ground, whose capabilities name what the tool may do and whose rules every answer, the map's included, is held to.</param>
    /// <param name="prompt">The task text the pack is seeded from: what the run handed the step's prompt - its filled values, with a handoff - or the words an answer sends (<see cref="FlowStepRun.PromptValues"/>), never the template's own words.</param>
    /// <param name="mask">The secrets the run holds, as the step's records and answers are masked by: what the step's graph tool keeps of an answer holds none.</param>
    /// <param name="cancellationToken">Stops the reading of the view.</param>
    public static async Task<FlowStepGraph?> OfAsync(
        ICodebaseGraphService graph,
        OrchestrationStore store,
        IFileSystem fileSystem,
        IHostPlatform platform,
        IGitClient? git,
        TimeProvider clock,
        IHarnessOutput output,
        FlowStepRun run,
        FlowRunPlace place,
        HarnessContext context,
        AgentDefinition? definition,
        WorkflowStep root,
        string agent,
        AgentRecord record,
        AgentPolicyGround ground,
        string prompt,
        Execution.ILineMask? mask,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ground);

        try
        {
            var layout = OrchestratorLayout.Of(context.Layout, place.Run);
            var mode = WorkflowStep.GraphOf(root, definition, GraphifyUpstream(store, layout, place.Definition, run.DeclarationPath));

            return mode == "off"
                ? null
                : await ServedAsync(graph, store, fileSystem, platform, git, clock, output, run, place, context, layout, mode, root, agent, record, ground, prompt, mask, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The graph is a map: one that cannot be served - its records, its map, a line of it - is said, and the step
            // goes on without it, as the engine's own syncs of it go on.
            output.Warn("reason", $"the codebase graph of run '{place.Run}' was not served to '{run.Instance}': {ex.Message.TrimEnd('.')}. The step goes on without it.");
            return null;
        }
    }

    /// <summary>The serving of a step whose mode is <c>map</c> or <c>tools</c>.</summary>
    private static async Task<FlowStepGraph> ServedAsync(
        ICodebaseGraphService graph,
        OrchestrationStore store,
        IFileSystem fileSystem,
        IHostPlatform platform,
        IGitClient? git,
        TimeProvider clock,
        IHarnessOutput output,
        FlowStepRun run,
        FlowRunPlace place,
        HarnessContext context,
        OrchestratorLayout layout,
        string mode,
        WorkflowStep root,
        string agent,
        AgentRecord record,
        AgentPolicyGround ground,
        string prompt,
        Execution.ILineMask? mask,
        CancellationToken cancellationToken)
    {
        // The view the step is served: the agent's where it has a worktree, the base where it reads - its own tree's where
        // the step names the commit that tree starts at (FlowGraphSyncs.ViewsOwnTree). The engine syncs it at the step's
        // start (FlowGraphSyncs.AtStartAsync), or once its tree stands at that commit (FlowGraphSyncs.ReadiedAsync); what
        // is read here is what that left, and every call made of the
        // service for the step - its changes, a check of a file the tool makes again - acts on the tree that view is of.
        // The run's one shape, the same its syncs are made under (FlowUse.GraphOf).
        var worktree = FlowGraphSyncs.WorktreeOf(agent, record, context.Layout, run.Step);
        var request = FlowGraphSyncs.RequestOf(context, place, $"{run.Instance}:graph", worktree);
        var directory = layout.InstanceDirectory(run.Instance, agent);
        CodebaseGraphRead? read = null;

        try
        {
            read = await graph.ReadOnlyAsync(layout, worktree?.Agent, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException)
        {
            // A graph whose records do not read is no graph: the step is served the sentence for a step that left no
            // output, as it is where none was built, and the run goes on from a missing graph as from any missing output.
        }

        // The pack's own keys are the step's, its chain's root's where it says none: a step that goes on with another's
        // work shapes its own pack (V53), while the mode and the narrowing of reads are its conversation's, begun where
        // the chain began. Pushed is page 1's size in the first message, by the mode where neither says one; a step at
        // tools that pushes none still has its pack made - the tool's pack serves it from page 1, and find breaks its ties
        // by its nearness - at the size a pushed pack has where nothing says one.
        var from = run.Step.GraphFrom ?? root.GraphFrom ?? WorkflowStep.GraphFromTask;
        var pushed = WorkflowStep.GraphPromptTokensOf(mode, run.Step.GraphPromptTokens ?? root.GraphPromptTokens);
        var asking = mode == "tools" ? git : null;
        var serves = asking is not null;
        var tokens = pushed > 0 ? pushed : serves ? WorkflowStep.DefaultGraphPromptTokens : 0;
        var narrowed = (root.GraphReads ?? WorkflowStep.GraphReadsNarrow) == WorkflowStep.GraphReadsNarrow;

        if (read is null)
        {
            var missing = MissingOutputOf(store, layout, place.Definition, run.Instance);

            // The sentence stands where the pack would: pushed where the pack is, and the tool's answer to every call.
            return new FlowStepGraph(mode, from, worktree?.Agent ?? CodebaseGraphService.BaseView, tokens, pushed > 0, map: null, mapPages: 0, made: null, missing, noPack: null, store, output, directory, run.Instance)
            {
                section = pushed > 0 ? missing : string.Empty,
                support = mode == "tools" ? new AgentGraphSupport(Tool: null) { Gone = missing } : null,
            };
        }

        var queries = new CodebaseGraphQueries(read);
        var tree = read.Tree;
        var policy = new AgentPolicy(fileSystem, platform, ground);

        bool Readable(string relative) => policy.Read(Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar))).Refusal() is null;

        var reads = ReadsOf(ground);
        var (map, mapPages) = await MapOfAsync(graph, store, fileSystem, output, layout, run, request.Shape.MapTokens, reads, Readable, cancellationToken).ConfigureAwait(false);

        // The pack's seeds: the working sets of the work the run holds - or, where the step names one, that step's in
        // their place - and what the work changed, under graphFrom: changes, leading the pack.
        var worked = WorkedOf(store, layout, place.Definition, run, from);
        CodebaseGraphChanges? changed = null;
        string? changesUnread = null;

        if (tokens > 0 && FlowGraphSyncs.ChangesBase(record, place.StartCommit) is { } against)
        {
            try
            {
                changed = from == WorkflowStep.GraphFromChanges
                    ? await graph.ChangesAsync(request, read, against, cancellationToken).ConfigureAwait(false)
                    : new CodebaseGraphChanges(against, await graph.DifferingAsync(request, against, cancellationToken).ConfigureAwait(false), []);
            }
            catch (Exception ex) when (ex is HarnessException or InvalidOperationException)
            {
                changesUnread = from == WorkflowStep.GraphFromChanges ? ex.Message : null;
            }
        }
        else if (tokens > 0 && from == WorkflowStep.GraphFromChanges)
        {
            changesUnread = "the tree's base is not recorded";
        }

        var pack = tokens > 0
            ? CodebaseContextPack.Make(
                queries,
                new ContextPackRequest(prompt, Readable)
                {
                    From = from == WorkflowStep.GraphFromChanges ? ContextPackFrom.Changes : ContextPackFrom.Task,
                    Tokens = tokens,
                    Asks = serves,
                    Writes = policy.StepWrites,
                    Worked = worked,
                    Closure = ClosureOf(run),
                    Changes = changed,
                    ChangesUnread = changesUnread,
                })
            : null;
        var noPack = NoPackOf(tokens, pushed, held: pack is { Text.Length: > 0 });
        var served = new FlowStepGraph(mode, from, read.View, tokens, pushed > 0, map, mapPages, pack, missing: null, noPack, store, output, directory, run.Instance)
        {
            view = queries,
        };

        served.section = served.Pushes ? served.Pack?.Text ?? string.Empty : string.Empty;

        if (asking is not null)
        {
            // pack and map wherever there is a page of them the first message does not hold - every page of a pack not
            // pushed, from page 1, and every page of the map, which is pushed to no step with the tool; changes once the
            // step is told changes that go on past its message's (ChangesSince).
            var operations = CodebaseGraphTool.Operations(ground.Capabilities.Contains(AgentCapability.Write), narrowed, pack: served.Pack is { } held && (!served.Pushes || held.More), mapPages: mapPages);
            var never = CodebaseGraphService.NeverListed(request.WorktreesRoot);
            var tool = new CodebaseGraphTool(
                queries,
                new AgentFiles(asking, fileSystem),
                fileSystem,
                ConfinedPaths.Within(fileSystem, platform, tree),
                path => Readable(path) && !PathPatterns.Matches(never, path, platform.PathComparison),
                path => policy.Write(Path.Combine(tree, path.Replace('/', Path.DirectorySeparatorChar))),
                async (path, token) => new CodebaseGraphQueries((await graph.CheckAsync(request, [path], token).ConfigureAwait(false)).Read),
                near: () => served.made?.Near,
                pages: PagesOf(fileSystem, layout, run, directory, ground),
                mask: mask,
                pack: () => served.Pack?.Paged,
                changes: () => served.told,
                map: page => store.MapPage(layout, reads, mapPages, page));

            served.support = new AgentGraphSupport(
                tool,
                line => store.AppendGraphStepLine(directory, line),
                (written, token) => AfterWriteAsync(graph, request, run.Instance, platform.PathComparison, tool, written, token),
                operations,
                Narrow: narrowed);
        }

        return served;
    }

    /// <summary>
    /// Where the step's graph tool keeps the results its answers' pages read from: its own directory's
    /// <c>graph-pages</c> (<see cref="OrchestratorLayout.StepPagesDirectoryName"/>), which every attempt of the step and a
    /// resumed conversation read; and, where its own holds nothing for an ask under the graph's digest, those of the work
    /// its conversation goes on with (<see cref="FlowStepRun.GoesOnWith"/>) and no other - not a step it only depends on,
    /// whose conversation it never held - the newest first, the order that work went read back: a continuation record
    /// carries the newest's last iterations word for word, its pages among them. Each is read under the step's own read
    /// rules alone (<see cref="ReadsOf"/>), so no page gives a line of a file this step may not read.
    /// </summary>
    private static GraphPageStore PagesOf(IFileSystem fileSystem, OrchestratorLayout layout, FlowStepRun run, string directory, AgentPolicyGround ground)
        => new(
            Path.Combine(directory, OrchestratorLayout.StepPagesDirectoryName),
            fileSystem,
            ReadsOf(ground),
            [.. run.GoesOnWith.Reverse().Select(source => Path.Combine(layout.InstanceDirectory(source.Instance, source.Agent), OrchestratorLayout.StepPagesDirectoryName))]);

    /// <summary>
    /// Puts away the answers the conversation of the step whose records are in <paramref name="directory"/> kept - the pages
    /// its graph answers read from, and the pages of its calls' whole results (<see cref="Agents.Runtime.KeptResults"/>) -
    /// its directory whole (<see cref="KeptAnswers.PutAway"/>): what a step leaves once it is
    /// done and no later step's conversation goes on with
    /// its work (<see cref="ResumeChain.GoneOnWith"/>), so no page of them is asked again. One that cannot be removed is
    /// said, and the step's ending stands.
    /// </summary>
    /// <param name="fileSystem">The file system the pages are removed through.</param>
    /// <param name="output">Hears pages that could not be put away.</param>
    /// <param name="directory">The step instance's own directory.</param>
    /// <param name="instance">The step instance, as the warning names it.</param>
    public static void PutAwayPages(IFileSystem fileSystem, IHarnessOutput output, string directory, string instance)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (KeptAnswers.PutAway(Path.Combine(directory, OrchestratorLayout.StepPagesDirectoryName), fileSystem) is { } why)
        {
            output.Warn("reason", $"the pages '{instance}' kept were not put away: {why}. The step's ending stands, and they stay in its records.");
        }
    }

    /// <summary>
    /// Puts away the pages every step of the run <paramref name="flow"/> records kept, where the run is done: no step's
    /// conversation goes on any more, so the last of them - those a step that is done kept for a later step to go on from -
    /// are asked again by none. A run that is not done - paused, failed, stopped - keeps them, for a step of it may go on.
    /// </summary>
    /// <param name="fileSystem">The file system the pages are removed through.</param>
    /// <param name="output">Hears pages that could not be put away.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="flow">The run's record as its session left it.</param>
    public static void PutAwayRunPages(IFileSystem fileSystem, IHarnessOutput output, OrchestratorLayout layout, FlowFile flow)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(flow);

        if (!string.Equals(flow.Status, nameof(FlowRunStatus.Done), StringComparison.Ordinal))
        {
            return;
        }

        foreach (var step in flow.Steps)
        {
            PutAwayPages(fileSystem, output, layout.InstanceDirectory(step.Instance, step.Agent), step.Instance);
        }
    }

    /// <summary>The step's use of the graph, its default resolved: <c>off</c>, <c>map</c> or <c>tools</c>.</summary>
    public string Mode { get; }

    /// <summary>Where the pack starts from: <c>task</c>, <c>changes</c>, or the step whose working set seeds it.</summary>
    public string From { get; }

    /// <summary>The view the step is served: <c>base</c>, or the agent's name.</summary>
    public string View { get; }

    /// <summary>The size of a page of the pack, in estimated tokens - and of the changes a conversation gone on with is told; 0 where no pack is made.</summary>
    public int Tokens { get; }

    /// <summary>
    /// Whether page 1 of the step's pack - or, where the run's graph was never built, the sentence in its place - is pushed
    /// into the first message of each launch that begins a conversation of its own: its <c>graphPromptTokens</c> with its
    /// default resolved by its mode (<see cref="WorkflowStep.GraphPromptTokensOf"/>) above 0.
    /// </summary>
    public bool Pushes { get; }

    /// <summary>
    /// The run's repository map of the step's ground of reads, pushed as the <c>&lt;repository-map&gt;</c> part ahead of
    /// the <c>&lt;codebase&gt;</c> part of the first message of each launch that begins a conversation of its own, where
    /// the step's graph is <c>map</c>: made once from the base as it stood when the first graphed reason step of that
    /// ground was given its prompt, holding no declaration of a file the step may not read, and read as it is by every
    /// step of that ground after - its first page, whole as the map's pages are kept with it; null where the step's graph
    /// is <c>tools</c> - its tool's <c>map</c> serves every page, from page 1 (<see cref="MapPages"/>) - the workflow's
    /// <c>mapTokens</c> is 0, the run's graph was never built, or the map could not be made or read.
    /// </summary>
    public string? Map { get; }

    /// <summary>How many pages the run's repository map of the step's ground of reads holds, pushed or not; 0 where there is no map.</summary>
    public int MapPages { get; }

    /// <summary>The pushed map's estimated tokens; 0 where none is pushed.</summary>
    public int MapTokens => Map is null ? 0 : EstimatedTokens.Of(Map);

    /// <summary>
    /// The step's pack: its page 1 pushed into a conversation of its own where <see cref="Pushes"/> says, and its pages
    /// served by the tool's <c>pack</c>; null where none was made, or the pack made held nothing - its first line past
    /// its cap.
    /// </summary>
    public ContextPack? Pack { get; }

    /// <summary>Why no pack was given, where the run's graph was never built - the sentence for a step that left no output; null where it was.</summary>
    public string? Missing { get; }

    /// <summary>The tool, the narrowing and the recording of the step's graph lines, offered where its mode is <c>tools</c>; null otherwise.</summary>
    public AgentGraphSupport? Support => support;

    /// <summary>
    /// The step's codebase section, the <c>&lt;codebase&gt;</c> part's text (<see cref="AgentPromptParts"/> writes its
    /// tags): its context pack, or the sentence for a graph the run never built, where the step pushes its pack; empty
    /// where it pushes none, or was given no pack.
    /// </summary>
    public string Section => section;

    /// <summary>
    /// The changes the conversation the step goes on with is told of the codebase: the declarations its view changed
    /// since what that conversation was told of them - its pack and every delta after it - and, where the step pushes its
    /// pack, the nodes of its own pack the conversation was not given - in the one new user message, never a map or a pack
    /// again whole: their page 1, every one told, page by page, the graph tool's <c>changes</c> given where they go on
    /// past it. Made against the pack as it was made, so a pack that held nothing still tells the conversation what moved
    /// of what it was told; null where no pack was made, its view was not read, or the step pushes no pack and the
    /// conversation holds no declaration of the codebase (<see cref="CodebaseConversationChanges.HoldsAny"/>) - nothing it
    /// was told can have moved, and nothing is pushed of its own. Kept, so
    /// the line of the launch told them says what they cost.
    /// </summary>
    /// <param name="told">The codebase parts the conversation holds, in the order it was given them: its pack, then each delta after it.</param>
    public string? ChangesSince(IReadOnlyList<string> told)
    {
        ArgumentNullException.ThrowIfNull(told);

        var asks = support?.Tool is not null;
        var pages = view is null || made is null || (!Pushes && !CodebaseConversationChanges.HoldsAny(told)) ? null : CodebaseConversationChanges.Pages(view, Pushes ? made : null, told, Tokens, asks);

        changes = pages?.Page(1)!.Text;
        this.told = pages?.Page(1) is { Last: false } ? pages : null;

        if (this.told is not null && support is { Tool: not null, Operations: { } operations })
        {
            support = support with { Operations = new HashSet<GraphOp>(operations) { GraphOp.Changes } };
        }

        return changes;
    }

    /// <summary>
    /// A launch of the step was given its first message, holding <paramref name="given"/> of the graph: its line is written
    /// to the step's <c>graph.jsonl</c> before any line of its calls, and its figures are the step's (<see cref="Ended"/>)
    /// - the last launch's standing. A line that cannot be written is said, and the launch goes on.
    /// </summary>
    /// <param name="given">What of the graph the launch's first message holds, as <see cref="AgentPromptParts.CodebaseOf(AgentRun, bool)"/> decided it.</param>
    public void Given(AgentCodebaseGiven given)
    {
        ArgumentNullException.ThrowIfNull(given);

        this.given = given;

        try
        {
            store.AppendGraphStepLine(lines, PromptLineOf(given));
        }
        catch (Exception ex) when (ex is HarnessException || Unkept(ex))
        {
            output.Warn("reason", $"the graph line of '{instance}'s launch was not written: {ex.Message.TrimEnd('.')}. The launch goes on.");
        }
    }

    /// <summary>
    /// The line of a launch's first message that held <paramref name="given"/> of the graph: what the map and the pack it
    /// was pushed cost, or the changes it was told in their place, or why it was given no pack - the graph never built, a
    /// step that asked for none, one at tools that pushes none and asks the tool for its pages, one whose pack held
    /// nothing, a compaction's record.
    /// </summary>
    /// <param name="given">What of the graph the launch's first message holds.</param>
    public GraphPromptLine PromptLineOf(AgentCodebaseGiven given)
    {
        ArgumentNullException.ThrowIfNull(given);

        var mapTokens = given.Map ? MapTokens : 0;

        if (given.Changes && changes is { Length: > 0 } told)
        {
            return new GraphPromptLine(0, 0, 0, 0, 0, ChangesTokens: EstimatedTokens.Of(told));
        }

        if (given.Codebase && Pack is { } pack)
        {
            // Nothing of the pack is left out: what page 1 does not hold is on the pages after it.
            return new GraphPromptLine(mapTokens, pack.Seeds.Count, pack.Nodes.Count, pack.Tokens, 0);
        }

        return new GraphPromptLine(mapTokens, 0, 0, 0, 0, Missing ?? noPack ?? NoPackCompacted);
    }

    /// <summary>
    /// What the step's graph gave and what it saved, said as its <c>step.json</c> holds it: its mode, its view, what the map
    /// and the pack - or the changes in their place - its last launch was pushed cost, how many of the pushed pack's nodes
    /// the step then used, what the tool served - the pages of the map and the pack it was asked for among it, counted
    /// apart under <c>pulled</c>, the pack's with the nodes its pages listed and how many of them the step then used, by
    /// the one rule the pushed pack's are counted by (<see cref="CodebaseWorkingSet.Used"/>) - what the agent still read
    /// whole, and the whole reads the narrowing answered with an outline - how many, what reading them whole would have
    /// cost and what the outlines cost (<c>outlinedReads</c>) -
    /// counted from its <c>graph.jsonl</c> and its stored stream. A pack made and not pushed counts 0 under <c>pack</c>,
    /// for its first message held none of it. Its working set is written beside its output first
    /// (<c>working-set.json</c>), the seeds of the packs of the steps after it.
    /// </summary>
    /// <param name="graph">The one graph service every graph is read and written by, for the view as it stands at the step's end.</param>
    /// <param name="fileSystem">The file system the files the step wrote are read on, as it left them.</param>
    /// <param name="pathComparison">How the platform compares paths.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="cancellationToken">Stops the reading of the view.</param>
    public JsonObject? Ended(ICodebaseGraphService graph, IFileSystem fileSystem, StringComparison pathComparison, OrchestratorLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);

        IReadOnlyList<AgentEvent> stream = [];
        IReadOnlyList<GraphStepLine> held = [];

        try
        {
            stream = store.ReadStepStream(lines);
            held = store.ReadGraphStepLines(lines);
        }
        catch (HarnessException)
        {
            // A record of the step's that does not read is refused elsewhere; its figures are gathered from what does.
        }
        catch (Exception ex) when (Unkept(ex))
        {
            output.Warn("reason", $"the graph records of '{instance}' were not read: {ex.Message.TrimEnd('.')}. Its figures are gathered from what was.");
        }

        // The view as it stands at the step's end, for the working set's nodes; where it cannot be read - or was never
        // built - the working set is made of no view, and every node it holds is gone.
        CodebaseGraphQueries? end = null;

        try
        {
            if (graph.ReadOnlyAsync(layout, View == CodebaseGraphService.BaseView ? null : View, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult() is { } read)
            {
                end = new CodebaseGraphQueries(read);
            }
        }
        catch (Exception ex) when (ex is HarnessException || Unkept(ex))
        {
        }

        var working = end is null ? new CodebaseWorkingSet([]) : CodebaseWorkingSet.Make(end, stream, held, fileSystem, pathComparison);

        try
        {
            store.WriteWorkingSet(lines, working);
        }
        catch (Exception ex) when (ex is HarnessException || Unkept(ex))
        {
            // A working set that cannot be written says nothing of the step's ending: it is said, and the pack of the
            // steps after it is seeded from what is there.
            output.Warn("reason", $"the working set of '{instance}' was not written: {ex.Message.TrimEnd('.')}. The step's ending stands, and the packs of the steps after it are seeded from what is there.");
        }

        var calls = held.OfType<GraphCallLine>().ToList();
        var escapes = held.OfType<GraphEscapeLine>().ToList();
        var outlined = held.OfType<GraphOutlinedLine>().ToList();
        var whole = CodebaseWorkingSet.WholeFileReads(stream);
        var prompted = PromptLineOf(given ?? AgentCodebaseGiven.None);
        var used = prompted.Nodes == 0 || Pack is null ? 0 : working.Used(Pack.Nodes);

        // The pages of the map and the pack the tool was asked for and gave: a call each, its answer's tokens - among the
        // served tokens, as every call's are - and, of the pack's, the nodes they listed, each once, and how many of them
        // the step then used, by the rule the pushed pack's are counted by.
        List<GraphCallLine> Answered(GraphOp op) => [.. calls.Where(line => line.Op == CodebaseGraphTool.NameOf(op) && line.Outcome == GraphToolOutcome.Answered)];

        var mapPages = Answered(GraphOp.Map);
        var packPages = Answered(GraphOp.Pack);
        var listed = packPages.SelectMany(line => line.Listed ?? []).ToHashSet(StringComparer.Ordinal);

        return new JsonObject
        {
            ["mode"] = Mode,
            ["from"] = From,
            ["view"] = View,
            ["mapTokens"] = prompted.MapTokens,
            ["pack"] = new JsonObject
            {
                ["seeds"] = prompted.Seeds,
                ["nodes"] = prompted.Nodes,
                ["tokens"] = prompted.Tokens,
                ["used"] = used,
            },
            ["changesTokens"] = prompted.ChangesTokens,
            ["pulled"] = new JsonObject
            {
                ["map"] = new JsonObject { ["pages"] = mapPages.Count, ["tokens"] = mapPages.Sum(line => line.Tokens) },
                ["pack"] = new JsonObject
                {
                    ["pages"] = packPages.Count,
                    ["tokens"] = packPages.Sum(line => line.Tokens),
                    ["nodes"] = listed.Count,
                    ["used"] = working.Used(listed),
                },
            },
            ["calls"] = calls.Count,
            ["servedTokens"] = calls.Sum(line => line.Tokens),
            ["bodies"] = calls.Count(line => line.Op == CodebaseGraphTool.NameOf(GraphOp.Body) && line.Served.Count > 0),
            ["textHits"] = calls.Sum(line => line.Hits),
            ["edits"] = new JsonObject
            {
                ["made"] = calls.Count(line => line.Outcome == GraphToolOutcome.Wrote),
                ["refused"] = calls.Count(line => line.Op == CodebaseGraphTool.NameOf(GraphOp.Edit) && line.Outcome == GraphToolOutcome.Refused),
            },
            ["wholeFileReads"] = new JsonObject
            {
                ["count"] = whole.Count,
                ["tokens"] = whole.Tokens,
            },
            ["outlinedReads"] = new JsonObject
            {
                ["count"] = outlined.Count,
                ["tokens"] = outlined.Sum(line => line.Tokens),
                ["outlineTokens"] = outlined.Sum(line => line.OutlineTokens),
            },
            ["escapes"] = new JsonObject
            {
                ["count"] = escapes.Count,
                ["tokens"] = escapes.Sum(line => line.Tokens),
                ["lowConfidence"] = escapes.Count(line => line.Escape == GraphEscapeLine.LowConfidence),
                ["stated"] = escapes.Count(line => line.Escape == GraphEscapeLine.Stated),
            },
        };
    }

    /// <summary>
    /// Every site and home of the classes of the work item <paramref name="run"/> is an instance of, where the engine
    /// read them for it under <c>classes</c> because its prompt reads them (<c>{input.item.classes}</c>): each class's
    /// one home and every site of its list, as the pack's closure seed source takes them. Empty for a step that reads
    /// no classes, holds no work item, or whose item declares none - a member the shape does not hold reads nothing.
    /// </summary>
    private static IReadOnlyList<ContextPackClosure> ClosureOf(FlowStepRun run)
    {
        if (run.Read is not { } read || !read.TryGetValue("classes", out var classes) || classes.ToNode() is not JsonArray declared)
        {
            return [];
        }

        List<ContextPackClosure> sites = [];

        foreach (var item in declared.OfType<JsonObject>())
        {
            if (item["home"] is JsonObject home)
            {
                sites.Add(new(Text(home["path"]), Text(home["symbol"]), Home: true));
            }

            if (item["sites"] is JsonArray list)
            {
                sites.AddRange(list.OfType<JsonObject>().Select(site => new ContextPackClosure(Text(site["path"]), Text(site["symbol"]), Home: false)));
            }
        }

        return sites;

        static string Text(JsonNode? node)
            => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
    }

    /// <summary>
    /// The working sets the step's pack is seeded from, by its <c>graphFrom</c>: those of the work the run holds for it,
    /// or - where it names a step - that step's instance in the step's own work item and iteration, in their place.
    /// </summary>
    private static IReadOnlyList<ContextPackWorked> WorkedOf(OrchestrationStore store, OrchestratorLayout layout, WorkflowDefinition definition, FlowStepRun run, string from)
    {
        if (!WorkflowStep.GraphFromWords.Contains(from, StringComparer.Ordinal))
        {
            // The step names a reason step its scope sees - its own or one around it, found as the definition's check
            // finds it - and its instance in the step's own: the working set of that instance's newest visit that began.
            if (FlowScope.Resolve(from, definition, FlowScope.ScopeOf(run.DeclarationPath)) is { Step.Type: "reason" } named
                && SeedOf(store, layout, run, named.Path) is { } seeded)
            {
                List<CodebaseWorkingSet> sets = [.. new[] { store.ReadWorkingSet(layout.InstanceDirectory(seeded.Instance, seeded.Agent)) }.OfType<CodebaseWorkingSet>()];
                return CodebaseWorkingSet.Seeds(sets);
            }

            return [];
        }

        return run.WorkedFrom is [_, ..] worked ? FlowWorkingSets.SeedsOf(store, layout, worked) : [];
    }

    /// <summary>
    /// The instance of the step declared at <paramref name="path"/> whose working set seeds <paramref name="run"/>'s pack:
    /// one in the scope instance <paramref name="run"/> itself stands in - its own work item and iteration, never another's
    /// beside it - its newest visit that began, ended first among those that ended; null where the run holds none.
    /// </summary>
    private static FlowStepRecord? SeedOf(OrchestrationStore store, OrchestratorLayout layout, FlowStepRun run, string path)
    {
        // The id's segments end with the declaration's; a visit of the recovery taken inside a group holds the failing
        // scope's ahead of them. The scope the named step is declared in is matched by as many of the reader's own
        // segments - and the flow's top level by none.
        var id = run.Instance.Split('.');
        var declared = run.DeclarationPath.Split('.');
        var scope = FlowScope.ScopeOf(path) is { } named ? named.Split('.').Length : 0;
        var within = scope == 0 ? string.Empty : string.Join('.', id.Take(id.Length - declared.Length + scope)) + ".";

        return NewestOf(store, layout, record => string.Equals(record.Step, path, StringComparison.Ordinal)
            && record.State != nameof(FlowInstanceState.NotStarted)
            && record.Instance.StartsWith(within, StringComparison.Ordinal)
            && record.Instance.Count(one => one == '.') == within.Count(one => one == '.'));
    }

    /// <summary>
    /// The newest step record of the run that <paramref name="holds"/> names, ended first among those that ended; null
    /// where the run holds none. A <c>flow.json</c> that does not read is read as one that holds none, for the pack goes on.
    /// </summary>
    private static FlowStepRecord? NewestOf(OrchestrationStore store, OrchestratorLayout layout, Func<FlowStepRecord, bool> holds)
    {
        try
        {
            return store.ReadFlow(layout)?.Steps.Where(holds).OrderByDescending(record => record.FinishedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
        }
        catch (HarnessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The sentence for a step that left no output (<see cref="FlowNoOutput"/>), as a prompt's placeholder is filled with
    /// it: the graphify step that was to make the run's graph, and why it left nothing, by the state its newest visit's
    /// record stands in.
    /// </summary>
    private static string MissingOutputOf(OrchestrationStore store, OrchestratorLayout layout, WorkflowDefinition definition, string instance)
    {
        var declared = FlowScope.Declared(definition).Where(one => one.Step.Type == "graphify-codebase").ToList();
        var newest = declared.Count > 0 ? NewestOf(store, layout, record => declared.Any(one => one.Path == record.Step)) : null;
        var named = newest?.Step ?? declared.FirstOrDefault().Step?.Name ?? "graphify-codebase";

        return FlowNoOutput.Sentence(named, FlowNoOutput.Why(newest));
    }

    /// <summary>
    /// Whether the step at <paramref name="path"/> has a graph to be given: a <c>graphify-codebase</c> step upstream of it
    /// - one its waits reach, through its own dependencies and those of every group around it (<see cref="FlowGraph.Waits"/>)
    /// - or, where the step runs wherever the run takes it (<see cref="RunsWherever"/>) - the recovery's own, taken wherever
    /// a failure is, and a step raised by what another reports, never scheduled - a graphify step of the run that has ended
    /// done, as the run's records hold it now. The definition's check (V53) approximates the second by a graphify step being
    /// declared, for it has no run.
    /// </summary>
    private static bool GraphifyUpstream(OrchestrationStore store, OrchestratorLayout layout, WorkflowDefinition definition, string path)
    {
        var graphify = FlowScope.Declared(definition).Where(one => one.Step.Type == "graphify-codebase").Select(one => one.Path).ToList();

        if (RunsWherever(definition, path))
        {
            return NewestOf(store, layout, record => graphify.Contains(record.Step, StringComparer.Ordinal) && record.State == nameof(FlowInstanceState.Done)) is not null;
        }

        return graphify.Any(one => FlowGraph.Waits(definition, path, one));
    }

    /// <summary>
    /// Whether the step at <paramref name="path"/> runs wherever the run takes it, upstream of nothing it waits on: a step
    /// of the recovery, taken wherever a failure is, or one raised by what another step reports (<c>raised</c>), never
    /// scheduled. The one rule the graph a step is given and the definition's check (V53) read alike.
    /// </summary>
    /// <param name="definition">The workflow.</param>
    /// <param name="path">The step's declaration path.</param>
    internal static bool RunsWherever(WorkflowDefinition definition, string path)
        => path.StartsWith(FlowScope.Recovery + ".", StringComparison.Ordinal) || FlowScope.At(path, definition)?.Step.Raised is not null;

    /// <summary>
    /// What a step's reads are decided by beyond what every step of the run shares - the read rules of its ground, from the
    /// configuration, its definition and its step - named by a digest of them, sorted and once each: steps whose read rules
    /// are the same share one map, and read the pages one another's graph tool kept; null where no read rule narrows the
    /// step's reads. A side-task's ground is named by it too. The whole digest, never a part of it: two sets of rules
    /// whose digests begin alike are two keys, so no step reads a map or a page made under another's reads.
    /// </summary>
    /// <param name="ground">The step's ground, or a side-task's.</param>
    internal static string? ReadsOf(AgentPolicyGround ground)
    {
        var rules = ground.Restrictions
            .Select(restriction => restriction.Text)
            .Where(text => AgentRule.Problem(text) is null && AgentRule.Parse(text).Verb == AgentRuleVerb.Read)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return rules.Count == 0 ? null : FileContentHash.Of(Encoding.UTF8.GetBytes(string.Join('\n', rules)));
    }

    /// <summary>
    /// The run's repository map of the step's ground of reads: kept where it is there and read as it is, made from the base
    /// as it stands where it is not - once, whatever the steps after find, for the base moves as folds land and the map
    /// does not - whole, in pages, no line dropped, holding no declaration of a file the step may not read, and with it how
    /// many pages it holds; null where the workflow asks for no map, the base does not read, or the map could not be made or
    /// read, which is said - the step goes on without one.
    /// </summary>
    /// <param name="graph">The one graph service every graph is read and written by, for the base as it stands.</param>
    /// <param name="store">Keeps the run's maps, and reads its records for its task terms.</param>
    /// <param name="fileSystem">The file system the map is kept on.</param>
    /// <param name="output">Hears a map that could not be made or read.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="run">The step, whose issue names terms where it triages one.</param>
    /// <param name="mapTokens">The workflow's <c>codebaseGraph.mapTokens</c>; 0 asks for no map.</param>
    /// <param name="reads">The digest of the step's read rules (<see cref="ReadsOf"/>), which names its map.</param>
    /// <param name="readable">Whether the step may read a file, by its path relative to the tree.</param>
    /// <param name="cancellationToken">Stops the reading of the base.</param>
    private static async Task<(string? Map, int Pages)> MapOfAsync(
        ICodebaseGraphService graph,
        OrchestrationStore store,
        IFileSystem fileSystem,
        IHarnessOutput output,
        OrchestratorLayout layout,
        FlowStepRun run,
        int mapTokens,
        string? reads,
        Func<string, bool> readable,
        CancellationToken cancellationToken)
    {
        if (mapTokens <= 0)
        {
            return (null, 0);
        }

        try
        {
            CodebaseGraphRead? @base = null;

            if (!fileSystem.FileExists(layout.GraphMapFileOf(reads)))
            {
                try
                {
                    @base = await graph.ReadOnlyAsync(layout, null, cancellationToken).ConfigureAwait(false);
                }
                catch (HarnessException)
                {
                    // A base that does not read beside a view that does gives this step no map: the pack and the tool say
                    // what the tree is, and the step after this one makes the map where the base reads again.
                }
            }

            // The run's task terms: the words of its inputs - the request, a plan it was given - and of the issue it
            // triages, split as a name is inside the maker.
            IReadOnlyDictionary<string, string> inputs = new Dictionary<string, string>();

            try
            {
                inputs = store.ReadFlow(layout)?.Inputs ?? inputs;
            }
            catch (HarnessException)
            {
                // A run whose state does not read is refused elsewhere; its map is made of the tree alone.
            }

            var terms = string.Join("\n", inputs.Values.Append(run.Issue is { } issue ? $"{issue.Title}\n{issue.Description}" : string.Empty).Where(text => text.Length > 0));

            // Made or read under the map's own section, so steps that start together keep one map and are given one map's
            // bytes; another step that made it first is read as it made it.
            return store.MapOnce(layout, reads, () => @base is null ? null : CodebaseRepositoryMap.Make(new CodebaseGraphQueries(@base), terms, mapTokens, readable));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.Warn("reason", $"the repository map of run '{layout.Name}' was not made or read for '{run.Instance}': {ex.Message.TrimEnd('.')}. The step goes on without it; the next step tries again.");
            return (null, 0);
        }
    }

    /// <summary>
    /// Syncs the step's view by what a write or a command of its agent wrote (<see cref="AfterWrites.SyncGraphAsync"/>) - its
    /// tree the request's, the agent's worktree where it names one - its sync's record naming the step's instance and the
    /// tool, and makes the step's graph tool answer from the view it leaves.
    /// </summary>
    private static Task AfterWriteAsync(ICodebaseGraphService graph, CodebaseGraphSync request, string instance, StringComparison comparison, CodebaseGraphTool tool, AgentGraphWrite written, CancellationToken cancellationToken)
        => AfterWrites.SyncGraphAsync(graph, request with { Why = $"{instance}:{written.Tool}" }, written, comparison, tool, cancellationToken);
}
