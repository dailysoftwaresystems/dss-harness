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
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A reason step's serving of the run's codebase graph: the view the engine syncs at the step's start - the agent's it
/// works in, the base for one that reads - read whole, every call of the graph service made through a request of that
/// view (<see cref="FlowGraphSyncs.RequestOf"/>); its context pack made at its start and given as the
/// <c>&lt;codebase&gt;</c> part of the first message of each launch that begins a conversation of its own, after the
/// run's repository map - made once for each ground of reads the run's steps share, from the base as it stands when the
/// first graphed reason step of that ground is given its prompt, and read as it is by every one after; the changes of the
/// codebase told in their place to a conversation gone on with; the one tool <c>graph</c> offered with the operations and
/// the narrowing of reads its step says; each launch's first message, each call and each escape of the narrowing recorded
/// in its <c>graph.jsonl</c>; and its working set written and its figures gathered when it ends. Where the run's graph was
/// never built - every graphify step upstream failed, was skipped or was passed over - the pack is the sentence for a
/// step that left no output, and the tool answers the same. A graph never fails its step: one that cannot be served is
/// said, and the step goes on without it.
/// </summary>
public sealed class FlowStepGraph
{
    /// <summary>Why a launch was given no pack, where its step asked for none.</summary>
    public const string NoPackAsked = "the step asked for no pack: its graphPromptTokens is 0";

    /// <summary>Why a launch was given no pack, where the pack its step asked for held nothing.</summary>
    public const string NoPackHeld = "the step's pack held nothing its view could give";

    /// <summary>Why a launch was given no pack, where it began from the engine's compaction of the conversation.</summary>
    public const string NoPackCompacted = "the launch began from the engine's compaction record, which holds no map and no pack";

    private readonly OrchestrationStore store;
    private readonly IHarnessOutput output;
    private readonly string lines;
    private readonly string instance;
    private readonly string? noPack;
    private AgentGraphSupport? support;
    private CodebaseGraphQueries? view;
    private string section = string.Empty;
    private string? changes;
    private AgentCodebaseGiven? given;

    private FlowStepGraph(string mode, string from, string viewName, int tokens, string? map, ContextPack? pack, string? missing, string? noPack, OrchestrationStore store, IHarnessOutput output, string lines, string instance)
    {
        Mode = mode;
        From = from;
        View = viewName;
        Tokens = tokens;
        Map = map;
        Pack = pack;
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
    /// <param name="prompt">The step's filled prompt, the task text the pack is seeded from.</param>
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
                : await ServedAsync(graph, store, fileSystem, platform, git, clock, output, run, place, context, layout, mode, root, agent, record, ground, prompt, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        // The view the step is served: the agent's where it has a worktree, the base where it reads. The engine syncs it
        // at the step's start (FlowGraphSyncs.AtStartAsync); what is read here is what that left, and every call made of the
        // service for the step - its changes, a check of a file the tool makes again - acts on the tree that view is of.
        var worktree = FlowGraphSyncs.WorktreeOf(agent, record, context.Layout);
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
        // the chain began.
        var from = run.Step.GraphFrom ?? root.GraphFrom ?? WorkflowStep.GraphFromTask;
        var tokens = run.Step.GraphPromptTokens ?? root.GraphPromptTokens ?? WorkflowStep.DefaultGraphPromptTokens;
        var narrowed = (root.GraphReads ?? WorkflowStep.GraphReadsNarrow) == WorkflowStep.GraphReadsNarrow;

        if (read is null)
        {
            var missing = MissingOutputOf(store, layout, place.Definition, run.Instance);

            return new FlowStepGraph(mode, from, worktree?.Agent ?? CodebaseGraphService.BaseView, tokens, map: null, pack: null, missing, noPack: null, store, output, directory, run.Instance)
            {
                section = missing,
                support = mode == "tools" ? new AgentGraphSupport(Tool: null) { Gone = missing } : null,
            };
        }

        var queries = new CodebaseGraphQueries(read);
        var tree = read.Tree;
        var policy = new AgentPolicy(fileSystem, platform, ground);

        bool Readable(string relative) => policy.Read(Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar))).Refusal() is null;

        var map = await MapOfAsync(graph, store, fileSystem, output, layout, run, request.Shape.MapTokens, ReadsOf(ground), Readable, cancellationToken).ConfigureAwait(false);

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
                    Writes = policy.StepWrites,
                    Worked = worked,
                    Changes = changed,
                    ChangesUnread = changesUnread,
                })
            : null;
        var given = pack is { Text.Length: > 0 } ? pack : null;

        var made = new FlowStepGraph(mode, from, read.View, tokens, map, given, missing: null, tokens == 0 ? NoPackAsked : given is null ? NoPackHeld : null, store, output, directory, run.Instance)
        {
            section = given?.Text ?? string.Empty,
            view = queries,
        };

        if (mode == "tools" && git is not null)
        {
            var operations = CodebaseGraphTool.Operations(ground.Capabilities.Contains(AgentCapability.Write), narrowed);
            var never = CodebaseGraphService.NeverListed(request.WorktreesRoot);
            var tool = new CodebaseGraphTool(
                queries,
                new AgentFiles(git, fileSystem, clock),
                fileSystem,
                ConfinedPaths.Within(fileSystem, platform, tree),
                path => Readable(path) && !PathPatterns.Matches(never, path, platform.PathComparison),
                path => policy.Write(Path.Combine(tree, path.Replace('/', Path.DirectorySeparatorChar))),
                async (path, token) => new CodebaseGraphQueries((await graph.CheckAsync(request, [path], token).ConfigureAwait(false)).Read),
                near: given?.Near);

            made.support = new AgentGraphSupport(
                tool,
                line => store.AppendGraphStepLine(directory, line),
                (written, token) => AfterWriteAsync(graph, request, run.Instance, platform.PathComparison, tool, written, token),
                operations,
                Narrow: narrowed);
        }

        return made;
    }

    /// <summary>The step's use of the graph, its default resolved: <c>off</c>, <c>map</c> or <c>tools</c>.</summary>
    public string Mode { get; }

    /// <summary>Where the pack starts from: <c>task</c>, <c>changes</c>, or the step whose working set seeds it.</summary>
    public string From { get; }

    /// <summary>The view the step is served: <c>base</c>, or the agent's name.</summary>
    public string View { get; }

    /// <summary>The pack's cap, in estimated tokens.</summary>
    public int Tokens { get; }

    /// <summary>
    /// The run's repository map of the step's ground of reads, the <c>&lt;repository-map&gt;</c> part ahead of the
    /// <c>&lt;codebase&gt;</c> part of the first message of each launch that begins a conversation of its own: made once
    /// from the base as it stood when the first graphed reason step of that ground was given its prompt, holding no
    /// declaration of a file the step may not read, and read as it is by every step of that ground after; null where the
    /// workflow's <c>mapTokens</c> is 0, the run's graph was never built, or the map could not be made or read.
    /// </summary>
    public string? Map { get; }

    /// <summary>The map's estimated tokens; 0 where there is none.</summary>
    public int MapTokens => Map is null ? 0 : EstimatedTokens.Of(Map);

    /// <summary>The pack the step was made; null where none was asked for, or it held nothing.</summary>
    public ContextPack? Pack { get; }

    /// <summary>Why no pack was given, where the run's graph was never built - the sentence for a step that left no output; null where it was.</summary>
    public string? Missing { get; }

    /// <summary>The tool, the narrowing and the recording of the step's graph lines, offered where its mode is <c>tools</c>; null otherwise.</summary>
    public AgentGraphSupport? Support => support;

    /// <summary>
    /// The step's codebase section, the <c>&lt;codebase&gt;</c> part's text (<see cref="AgentPromptParts"/> writes its
    /// tags): its context pack, or the sentence for a graph the run never built; empty where it was given no pack.
    /// </summary>
    public string Section => section;

    /// <summary>
    /// The changes the conversation the step goes on with is told of the codebase: the declarations its view changed
    /// since what that conversation was told of them - its pack and every delta after it - and the nodes of its own pack
    /// the conversation was not given - in the one new user message, never a map or a pack again whole. Null where the step
    /// was given no pack. Kept, so the line of the launch told them says what they cost.
    /// </summary>
    /// <param name="told">The codebase parts the conversation holds, in the order it was given them: its pack, then each delta after it.</param>
    public string? ChangesSince(IReadOnlyList<string> told)
    {
        changes = view is null || Pack is null ? null : CodebaseConversationChanges.Make(view, Pack, told, Tokens);
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException or InvalidOperationException)
        {
            output.Warn("reason", $"the graph line of '{instance}'s launch was not written: {ex.Message.TrimEnd('.')}. The launch goes on.");
        }
    }

    /// <summary>
    /// The line of a launch's first message that held <paramref name="given"/> of the graph: what the map and the pack it
    /// was given cost, or the changes it was told in their place, or why it was given no pack - the graph never built, a
    /// step that asked for none or whose pack held nothing, a compaction's record.
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
            return new GraphPromptLine(mapTokens, pack.Seeds.Count, pack.Nodes.Count, pack.Tokens, pack.LeftOut);
        }

        return new GraphPromptLine(mapTokens, 0, 0, 0, 0, Missing ?? noPack ?? NoPackCompacted);
    }

    /// <summary>
    /// What the step's graph gave and what it saved, said as its <c>step.json</c> holds it: its mode, its view, what the map
    /// and the pack - or the changes in their place - its last launch was given cost, how many of the pack's nodes the step
    /// then used, what the tool served, and what the agent still read whole, counted from its <c>graph.jsonl</c> and its
    /// stored stream. Its working set is written beside its output first (<c>working-set.json</c>), the seeds of the packs
    /// of the steps after it.
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
        catch (HarnessException)
        {
        }

        var working = end is null ? new CodebaseWorkingSet([]) : CodebaseWorkingSet.Make(end, stream, held, fileSystem, pathComparison);

        try
        {
            store.WriteWorkingSet(lines, working);
        }
        catch (HarnessException)
        {
            // A working set that cannot be written says nothing of the step's ending: the pack of the steps after it is
            // seeded from what is there.
        }

        var calls = held.OfType<GraphCallLine>().ToList();
        var escapes = held.OfType<GraphEscapeLine>().ToList();
        var denied = held.OfType<GraphDeniedLine>().ToList();
        var whole = CodebaseWorkingSet.WholeFileReads(stream);
        var prompted = PromptLineOf(given ?? AgentCodebaseGiven.None);
        var used = prompted.Nodes == 0 || Pack is null ? 0 : working.Nodes.Count(node => !node.Gone && Pack.Nodes.Contains(node.Id, StringComparer.Ordinal));

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
            ["deniedReads"] = denied.Count,
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
    /// The sentence for a step that left no output, as a prompt's placeholder is filled with it (part 03): the graphify
    /// step that was to make the run's graph, and why it left nothing - it has not run, it was skipped, or it failed.
    /// </summary>
    private static string MissingOutputOf(OrchestrationStore store, OrchestratorLayout layout, WorkflowDefinition definition, string instance)
    {
        var declared = FlowScope.Declared(definition).Where(one => one.Step.Type == "graphify-codebase").ToList();
        var newest = declared.Count > 0 ? NewestOf(store, layout, record => declared.Any(one => one.Path == record.Step)) : null;
        var named = newest?.Step ?? declared.FirstOrDefault().Step?.Name ?? "graphify-codebase";
        var why = newest is null ? "it has not run" : WhyOf(newest);

        return $"(step '{named}' left no output: {why})";
    }

    /// <summary>Why a step that left no output left none, as the sentence for it says: skipped, or failed.</summary>
    private static string WhyOf(FlowStepRecord record)
        => record.State switch
        {
            nameof(FlowInstanceState.Skipped) => $"skipped: {record.SkippedBecause ?? "its when read false"}",
            nameof(FlowInstanceState.Failed) when string.Equals(record.Failure, FlowStepRecord.NameOf(FlowFailure.PassedOver), StringComparison.Ordinal) => $"it failed and was passed over: {record.Error ?? "it failed"}",
            nameof(FlowInstanceState.Failed) => $"it failed: {record.Error ?? "it failed"}",
            nameof(FlowInstanceState.Stopped) => "it was stopped by a pause before it ended",
            nameof(FlowInstanceState.NotRun) => $"the run never reached it: {record.NotRunBecause ?? "its when read false"}",
            _ => $"it ended {record.State.ToLowerInvariant()}",
        };

    /// <summary>
    /// Whether the step at <paramref name="path"/> has a graph to be given: a <c>graphify-codebase</c> step upstream of it
    /// - one its waits reach, through its own dependencies and those of every group around it (<see cref="FlowGraph.Waits"/>)
    /// - or, where the step is the recovery's own, which runs wherever a failure is taken, a graphify step of the run that
    /// has ended done, as the run's records hold it now. The definition's check (V53) approximates the second by a graphify
    /// step being declared, for it has no run.
    /// </summary>
    private static bool GraphifyUpstream(OrchestrationStore store, OrchestratorLayout layout, WorkflowDefinition definition, string path)
    {
        var graphify = FlowScope.Declared(definition).Where(one => one.Step.Type == "graphify-codebase").Select(one => one.Path).ToList();

        if (path.StartsWith(FlowScope.Recovery + ".", StringComparison.Ordinal))
        {
            return NewestOf(store, layout, record => graphify.Contains(record.Step, StringComparer.Ordinal) && record.State == nameof(FlowInstanceState.Done)) is not null;
        }

        return graphify.Any(one => FlowGraph.Waits(definition, path, one));
    }

    /// <summary>
    /// What a step's reads are decided by beyond what every step of the run shares - the read rules of its ground, from the
    /// configuration, its definition and its step - named by a digest of them, sorted and once each: steps whose read rules
    /// are the same share one map; null where no read rule narrows the step's reads.
    /// </summary>
    private static string? ReadsOf(AgentPolicyGround ground)
    {
        var rules = ground.Restrictions
            .Select(restriction => restriction.Text)
            .Where(text => AgentRule.Problem(text) is null && AgentRule.Parse(text).Verb == AgentRuleVerb.Read)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return rules.Count == 0 ? null : FileContentHash.Of(Encoding.UTF8.GetBytes(string.Join('\n', rules)))[..16];
    }

    /// <summary>
    /// The run's repository map of the step's ground of reads: kept where it is there and read as it is, made from the base
    /// as it stands where it is not - once, whatever the steps after find, for the base moves as folds land and the map
    /// does not - holding no declaration of a file the step may not read; null where the workflow asks for no map, the base
    /// does not read, or the map could not be made or read, which is said - the step goes on without one.
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
    private static async Task<string?> MapOfAsync(
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
            return null;
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
            return null;
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
