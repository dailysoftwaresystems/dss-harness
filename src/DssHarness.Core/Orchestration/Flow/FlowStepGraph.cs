using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A reason step's serving of the run's codebase graph (part 08, "Serving it"): the view the engine syncs at the step's
/// start - the agent's it works in, the base for one that reads - read whole, its context pack made at its start and
/// given as the <c>&lt;codebase&gt;</c> part of its first user message, the run's repository map - made once from the
/// base as it stands when the first graphed reason step is given its prompt, and read as it is by every step after - as
/// the <c>&lt;repository-map&gt;</c> part ahead of it, the one tool <c>graph</c> offered with the operations and the
/// narrowing of reads its step says, each call and each escape of the narrowing recorded in its
/// <c>graph.jsonl</c>, and its working set written and its figures gathered when it ends. Where the run's graph was
/// never built - every graphify step upstream failed, was skipped or was passed over - the pack is the sentence for a
/// step that left no output, and the tool answers the same.
/// </summary>
public sealed class FlowStepGraph(string mode, string from, string view, int tokens, string? map, ContextPack? pack, string? missing)
{
    /// <summary>The pack's seed of a step that names none: what its task names.</summary>
    public const string FromTask = "task";

    /// <summary>What the pack was given as, where the step named a step in place of the work the run holds.</summary>
    private const string FromTaskDefault = FromTask;

    /// <summary>
    /// Makes the serving of the step <paramref name="run"/>: its mode resolved by its chain of work's own graph keys and
    /// the graphify steps of its workflow, the view the engine syncs at its start read whole, the pack made from its
    /// filled prompt and what its step says of its work, and the tool offered with the operations and the narrowing of
    /// reads its step says - the step's prompt line the first line of its <c>graph.jsonl</c>. Null where the step's mode
    /// is <c>off</c>: it is served nothing.
    /// </summary>
    /// <param name="graph">The one graph service every graph is read and written by.</param>
    /// <param name="store">Reads the run's records and keeps the step's graph lines and working set.</param>
    /// <param name="fileSystem">The file system the graph's records are read on.</param>
    /// <param name="platform">This machine.</param>
    /// <param name="git">Answers a search's files, as the tool reads them.</param>
    /// <param name="clock">Times the tool's searches.</param>
    /// <param name="run">The step, its instance and its scope.</param>
    /// <param name="place">The run's place.</param>
    /// <param name="context">The repository's context.</param>
    /// <param name="definition">The agent definition the step's chain of work works by; null where its root names none.</param>
    /// <param name="root">The step at the root of the step's chain of work (<see cref="ResumeChain.Root"/>): its graph keys decide the mode, for a step that goes on with another asks the graph that step's conversation holds.</param>
    /// <param name="agent">The agent the step works as.</param>
    /// <param name="record">The agent's record, whose worktree names its view.</param>
    /// <param name="ground">The step's ground, whose capabilities name what the tool may do and whose rules every answer is held to.</param>
    /// <param name="prompt">The step's filled prompt, the task text the pack is seeded from.</param>
    /// <param name="cancellationToken">Stops the reading of the view.</param>
    public static async Task<FlowStepGraph?> OfAsync(
        ICodebaseGraphService graph,
        OrchestrationStore store,
        IFileSystem fileSystem,
        IHostPlatform platform,
        IGitClient? git,
        TimeProvider clock,
        FlowStepRun run,
        FlowRunPlace place,
        HarnessContext context,
        AgentDefinition? definition,
        WorkflowStep root,
        string agent,
        AgentRecord record,
        Agents.Policy.AgentPolicyGround ground,
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ground);

        var mode = WorkflowStep.GraphOf(root, definition, GraphifyUpstream(place.Definition, FlowInstanceId.PathOf(run.Instance)));

        if (mode == "off")
        {
            return null;
        }

        // The view the step is served: the agent's where it has a worktree, the base where it reads. The engine syncs it
        // at the step's start (FlowGraphSyncs.AtStartAsync); what is read here is what that left.
        var viewName = record.Reads || record.WorktreePath(context.Layout) is null ? null : agent;
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        CodebaseGraphRead? read = null;

        try
        {
            read = await graph.ReadOnlyAsync(layout, viewName, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException)
        {
            // A graph whose records do not read is no graph: the step is served the sentence for a step that left no
            // output, as it is where none was built, and the run goes on from a missing graph as from any missing output.
        }

        var from = root.GraphFrom ?? FromTaskDefault;
        var tokens = root.GraphPromptTokens ?? WorkflowStep.DefaultGraphPromptTokens;
        var narrowed = (root.GraphReads ?? WorkflowStep.GraphReadsNarrow) == WorkflowStep.GraphReadsNarrow;

        if (read is null)
        {
            var missing = MissingOutputOf(store, layout, place.Definition, run.Instance);
            var gone = new FlowStepGraph(mode, from, viewName ?? CodebaseGraphService.BaseView, tokens, map: null, null, missing)
            {
                section = missing,
                support = mode == "tools" ? new AgentGraphSupport(Tool: null) { Gone = missing } : null,
            };

            store.AppendGraphStepLine(layout.InstanceDirectory(run.Instance, agent), gone.PromptLine());
            return gone;
        }

        var queries = new CodebaseGraphQueries(read);
        var tree = read.Tree;
        var policy = new AgentPolicy(fileSystem, platform, ground);
        var request = new CodebaseGraphSync(context.Layout, layout, place.Definition.CodebaseGraph?.Shape ?? CodebaseGraphShape.Default, context.Config.Worktrees.Root, Rebuild: false, $"{run.Instance}:graph");

        // The run's repository map: made once, from the base as it stands, and read as it is by every step after - a
        // resumed run's included, for the base moves as folds land and the map does not (part 08, "The repository map").
        var map = await MapOfAsync(graph, store, fileSystem, layout, run, request.Shape.MapTokens, cancellationToken).ConfigureAwait(false);

        // The pack's seeds: the working sets of the work the run holds - or, where the step names one, that step's in
        // their place - and what the work changed, under graphFrom: changes, leading the pack.
        var worked = WorkedOf(store, layout, place.Definition, run, from);
        CodebaseGraphChanges? changes = null;
        string? changesUnread = null;

        if (tokens > 0 && (record.Reads ? place.StartCommit : record.Base) is { } against)
        {
            try
            {
                changes = from == WorkflowStep.GraphFromChanges
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
                new ContextPackRequest(prompt, relative => policy.Read(Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar))).Refusal() is null)
                {
                    From = from == WorkflowStep.GraphFromChanges ? ContextPackFrom.Changes : ContextPackFrom.Task,
                    Tokens = tokens,
                    Writes = policy.StepWrites,
                    Worked = worked,
                    Changes = changes,
                    ChangesUnread = changesUnread,
                })
            : null;

        var made = new FlowStepGraph(mode, from, read.View, tokens, map, pack, missing: null)
        {
            section = pack is { Text.Length: > 0 } given ? $"{CodebaseOpen}{given.Text}\n{CodebaseClose}" : string.Empty,
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
                path => policy.Read(Path.Combine(tree, path.Replace('/', Path.DirectorySeparatorChar))).Refusal() is null && !PathPatterns.Matches(never, path, platform.PathComparison),
                path => policy.Write(Path.Combine(tree, path.Replace('/', Path.DirectorySeparatorChar))),
                async (path, token) => new CodebaseGraphQueries((await graph.CheckAsync(request, [path], token).ConfigureAwait(false)).Read));

            made.support = new AgentGraphSupport(
                tool,
                line => store.AppendGraphStepLine(layout.InstanceDirectory(run.Instance, agent), line),
                (path, token) => AfterWriteAsync(graph, request, path, token),
                operations,
                Narrow: narrowed);
        }

        store.AppendGraphStepLine(layout.InstanceDirectory(run.Instance, agent), made.PromptLine());
        return made;
    }

    /// <summary>The tag the <c>&lt;codebase&gt;</c> part of the step's first message opens with.</summary>
    private const string CodebaseOpen = "<codebase>\n";

    /// <summary>The tag it closes with.</summary>
    private const string CodebaseClose = "</codebase>";

    /// <summary>The step's use of the graph, its default resolved: <c>off</c>, <c>map</c> or <c>tools</c>.</summary>
    public string Mode { get; } = mode;

    /// <summary>Where the pack starts from: <c>task</c>, <c>changes</c>, or the step whose working set seeds it.</summary>
    public string From { get; } = from;

    /// <summary>The view the step is served: <c>base</c>, or the agent's name.</summary>
    public string View { get; } = view;

    /// <summary>The pack's cap, in estimated tokens.</summary>
    public int Tokens { get; } = tokens;

    /// <summary>
    /// The run's repository map, the <c>&lt;repository-map&gt;</c> part ahead of the <c>&lt;codebase&gt;</c> part of the
    /// step's first user message: made once from the base as it stood when the run's first graphed reason step was given
    /// its prompt, and read as it is by every step after; null where the workflow's <c>mapTokens</c> is 0 or the run's
    /// graph was never built.
    /// </summary>
    public string? Map { get; } = map;

    /// <summary>The map's estimated tokens; 0 where none was given.</summary>
    public int MapTokens => Map is null ? 0 : EstimatedTokens.Of(Map);

    /// <summary>The pack the step was given; null where none was.</summary>
    public ContextPack? Pack { get; } = pack;

    /// <summary>
    /// The changes the conversation the step goes on with is told of the codebase (part 08, "Deltas, never a rewrite of
    /// what was sent"): the declarations its view changed since what that conversation was told of them - its pack and
    /// every delta after it - and the nodes of its own pack the conversation was not given - in the one new user message,
    /// never a map or a pack again whole. Null where the step was given no pack.
    /// </summary>
    /// <param name="told">The codebase parts the conversation holds, in the order it was given them: its pack, then each delta after it.</param>
    public string? ChangesSince(IReadOnlyList<string> told)
        => view is null || Pack is null ? null : CodebaseConversationChanges.Make(view, Pack, told, Tokens);

    /// <summary>Why no pack was given, where none was - the sentence for a step that left no output; null where one was.</summary>
    public string? Missing { get; } = missing;

    /// <summary>The tool, the narrowing and the recording of the step's graph lines, offered where its mode is <c>tools</c>; null otherwise.</summary>
    public AgentGraphSupport? Support => support;

    private AgentGraphSupport? support;

    /// <summary>The view the step is served, as its start's sync left it; null where the run's graph did not read.</summary>
    private CodebaseGraphQueries? view;

    /// <summary>
    /// The step's codebase section, the <c>&lt;codebase&gt;</c> part of its first user message: its context pack, or the
    /// sentence for a graph the run never built; empty where the cap is 0.
    /// </summary>
    public string Section => section;

    private string section = string.Empty;

    /// <summary>
    /// The step's prompt line, the first line of its <c>graph.jsonl</c>: what the map and the pack it was given cost, or
    /// why none was.
    /// </summary>
    public GraphPromptLine PromptLine()
        => new(MapTokens, Pack?.Seeds.Count ?? 0, Pack?.Nodes.Count ?? 0, Pack?.Tokens ?? 0, Pack?.LeftOut ?? 0, Missing is null ? null : Missing);

    /// <summary>
    /// What the step's graph gave and what it saved, said as its <c>step.json</c> holds it (part 08): its mode, its view,
    /// what the map and the pack cost, how many of the pack's nodes the step then used, what the tool served, and what
    /// the agent still read whole, counted from its <c>graph.jsonl</c> and its stored stream. Its working set is written
    /// beside its output first (<c>working-set.json</c>, value 372), the seeds of the packs of the steps after it.
    /// </summary>
    /// <param name="graph">The one graph service every graph is read and written by, for the view as it stands at the step's end.</param>
    /// <param name="store">Reads the step's stored stream and graph lines, and writes its working set.</param>
    /// <param name="fileSystem">The file system the files the step wrote are read on, as it left them.</param>
    /// <param name="pathComparison">How the platform compares paths.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="instance">The step instance's id.</param>
    /// <param name="agent">The agent the step works as, whose directory keeps the step's records.</param>
    /// <param name="cancellationToken">Stops the reading of the view.</param>
    public JsonObject? Ended(ICodebaseGraphService graph, OrchestrationStore store, IFileSystem fileSystem, StringComparison pathComparison, OrchestratorLayout layout, string instance, string agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);

        var directory = layout.InstanceDirectory(instance, agent);
        IReadOnlyList<Agents.AgentEvent> stream = [];
        IReadOnlyList<GraphStepLine> lines = [];

        try
        {
            stream = store.ReadStepStream(directory);
            lines = store.ReadGraphStepLines(directory);
        }
        catch (HarnessException)
        {
            // A record of the step's that does not read is refused elsewhere; its figures are gathered from what does.
        }

        // The view as it stands at the step's end, for the working set's nodes; where it cannot be read - or was never
        // built - the working set is made of no view, and every node it holds is gone.
        CodebaseGraphQueries? view = null;

        try
        {
            if (graph.ReadOnlyAsync(layout, View == CodebaseGraphService.BaseView ? null : View, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult() is { } end)
            {
                view = new CodebaseGraphQueries(end);
            }
        }
        catch (HarnessException)
        {
        }

        var working = view is null ? new CodebaseWorkingSet([]) : CodebaseWorkingSet.Make(view, stream, lines, fileSystem, pathComparison);

        try
        {
            store.WriteWorkingSet(directory, working);
        }
        catch (HarnessException)
        {
            // A working set that cannot be written says nothing of the step's ending: the pack of the steps after it is
            // seeded from what is there.
        }

        var calls = lines.OfType<GraphCallLine>().ToList();
        var escapes = lines.OfType<GraphEscapeLine>().ToList();
        var denied = lines.OfType<GraphDeniedLine>().ToList();
        var whole = WholeFileReads(stream);
        var used = Pack is null ? 0 : working.Nodes.Count(node => !node.Gone && Pack.Nodes.Contains(node.Id, StringComparer.Ordinal));

        return new JsonObject
        {
            ["mode"] = Mode,
            ["from"] = From,
            ["view"] = View,
            ["mapTokens"] = MapTokens,
            ["pack"] = new JsonObject
            {
                ["seeds"] = Pack?.Seeds.Count ?? 0,
                ["nodes"] = Pack?.Nodes.Count ?? 0,
                ["tokens"] = Pack?.Tokens ?? 0,
                ["used"] = used,
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

    /// <summary>The whole-file reads the step's stored stream holds - <c>view_file</c> with neither a start nor an end - how many, and their tokens.</summary>
    /// <param name="stream">The step's stored stream, every attempt's.</param>
    private static (int Count, int Tokens) WholeFileReads(IReadOnlyList<Agents.AgentEvent> stream)
    {
        // A call is named by its start, or by the message of the model that made it, as the working set reads them.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var @event in stream)
        {
            switch (@event)
            {
                case Agents.ToolCallStarted started:
                    names[started.Id] = started.Tool;
                    break;

                case Agents.AssistantMessage message:
                    foreach (var made in message.Calls)
                    {
                        names[made.Id] = made.Name;
                    }

                    break;
            }
        }

        var count = 0;
        var tokens = 0;

        foreach (var finished in stream.OfType<Agents.ToolCallFinished>())
        {
            if (!finished.Ok
                || !names.TryGetValue(finished.Id, out var tool)
                || (ClaudeCodeTools.ServedAs(tool) ?? tool) != AgentTools.ViewFile
                || JsonNode.Parse(finished.Input) is not JsonObject input
                || input["start_line"] is not null
                || input["end_line"] is not null)
            {
                continue;
            }

            count++;
            tokens += EstimatedTokens.Of(finished.Result);
        }

        return (count, tokens);
    }

    /// <summary>
    /// The working sets the step's pack is seeded from, by its <c>graphFrom</c>: those of the work the run holds for it,
    /// or - where it names one - the named step's newest instance that began, in their place.
    /// </summary>
    private static IReadOnlyList<ContextPackWorked> WorkedOf(OrchestrationStore store, OrchestratorLayout layout, WorkflowDefinition definition, FlowStepRun run, string from)
    {
        if (from is not (WorkflowStep.GraphFromChanges or FromTaskDefault))
        {
            // The step names a reason step of its scope: the working set of that step's newest instance that began, in
            // place of the work the run holds.
            var named = SiblingPathOf(definition, FlowInstanceId.PathOf(run.Instance), from);

            if (named is { } path && NewestOf(store, layout, record => string.Equals(record.Step, path, StringComparison.Ordinal) && record.State != nameof(FlowInstanceState.NotStarted)) is { } seeded)
            {
                List<CodebaseWorkingSet> sets = [.. new[] { store.ReadWorkingSet(layout.InstanceDirectory(seeded.Instance, seeded.Agent)) }.OfType<CodebaseWorkingSet>()];
                return CodebaseWorkingSet.Seeds(sets);
            }

            return [];
        }

        return run.WorkedFrom is [_, ..] worked ? FlowWorkingSets.SeedsOf(store, layout, worked) : [];
    }

    /// <summary>
    /// The path of the step <paramref name="name"/> declares beside the step at <paramref name="path"/>, in its scope:
    /// its sibling's, or its own where it names itself.
    /// </summary>
    private static string? SiblingPathOf(WorkflowDefinition definition, string path, string name)
    {
        var prefix = path.LastIndexOf('.') is > 0 and var dot ? path[..(dot + 1)] : string.Empty;
        return FlowScope.Declared(definition).FirstOrDefault(one => one.Path == prefix + name) is { Step: not null } sibling ? sibling.Path : null;
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
    /// Whether a <c>graphify-codebase</c> step is upstream of the step at <paramref name="path"/> - one its waits reach,
    /// through its own dependencies and those of every group around it (<see cref="FlowGraph.Waits"/>) - or, where the
    /// step is the recovery's own, one is declared, for a graph made anywhere has ended by the time the recovery runs.
    /// The definition's check (V53) holds the same question over its own shape; this holds it as the run stands.
    /// </summary>
    private static bool GraphifyUpstream(WorkflowDefinition definition, string path)
    {
        if (path.StartsWith(FlowScope.Recovery + ".", StringComparison.Ordinal))
        {
            return FlowScope.Declared(definition).Any(one => one.Step.Type == "graphify-codebase");
        }

        return FlowScope.Declared(definition)
            .Where(one => one.Step.Type == "graphify-codebase")
            .Any(one => FlowGraph.Waits(definition, path, one.Path));
    }

    /// <summary>
    /// The run's repository map: <c>graph/map.md</c> read as it stands where it is there, made from the base as it stands
    /// where it is not - once, whatever the steps after find, for the base moves as folds land and the map does not - and
    /// null where the workflow asks for no map. What it was made from is kept beside it in <c>graph/map.json</c>.
    /// </summary>
    /// <param name="graph">The one graph service every graph is read and written by, for the base as it stands.</param>
    /// <param name="store">Reads the run's records, for the run's task terms.</param>
    /// <param name="fileSystem">The file system the map is kept on.</param>
    /// <param name="layout">The run's own paths.</param>
    /// <param name="run">The step, whose issue names terms where it triages one.</param>
    /// <param name="mapTokens">The workflow's <c>codebaseGraph.mapTokens</c>; 0 asks for no map.</param>
    /// <param name="cancellationToken">Stops the reading of the base.</param>
    private static async Task<string?> MapOfAsync(ICodebaseGraphService graph, OrchestrationStore store, IFileSystem fileSystem, OrchestratorLayout layout, FlowStepRun run, int mapTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(run);

        if (mapTokens <= 0)
        {
            return null;
        }

        if (fileSystem.FileExists(layout.GraphMapFile))
        {
            return fileSystem.ReadAllText(layout.GraphMapFile);
        }

        CodebaseGraphRead? @base = null;

        try
        {
            @base = await graph.ReadOnlyAsync(layout, null, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException)
        {
            // A base that does not read beside a view that does gives this step no map: the pack and the tool say what
            // the tree is, and the step after this one makes the map where the base reads again.
        }

        if (@base is null)
        {
            return null;
        }

        // The run's task terms: the words of its inputs - the request, a plan it was given - and of the issue it triages,
        // split as a name is inside the maker.
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
        var (text, meta) = CodebaseRepositoryMap.Make(new CodebaseGraphQueries(@base), terms, mapTokens);

        fileSystem.CreateDirectory(layout.GraphDirectory);
        fileSystem.WriteAllTextAtomic(layout.GraphMapFile, text);
        fileSystem.WriteAllTextAtomic(layout.GraphMapMetaFile, meta.ToJsonString(JsonStateFile.Options));

        return text;
    }

    /// <summary>
    /// Syncs the graph by the path a write of the step's changed, its records kept whole: the tool checks its view again
    /// before it answers either way, and reads a body fresh against its hash.
    /// </summary>
    private static async Task AfterWriteAsync(ICodebaseGraphService graph, CodebaseGraphSync request, string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(request.Repository.MainCheckoutRoot), StringComparison.Ordinal))
            {
                return;
            }

            await graph.SyncPathsAsync(request, [Path.GetRelativePath(request.Repository.MainCheckoutRoot, path).Replace('\\', '/')], cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException)
        {
            // A sync that cannot be made says nothing to the agent: the graph's records stand as they were, and the tool
            // checks its view again before it answers either way.
        }
    }
}
