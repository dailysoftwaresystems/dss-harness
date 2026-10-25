using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The syncs of a run's codebase graph the engine makes of its own, each under the claim its step already holds and none a
/// step: a reason step's view where it starts - the agent's it works in, the base for one that reads; the base by the paths
/// the run's folds changed that the graph has not taken yet, after each fold and deletion and before its next attempt
/// does anything; the target's view after a refresh or a rebase, where it has one; and the view of each agent that is no
/// longer live dropped. Each only where the run has a graph, and none fails its step - the graph is a map - but one that
/// fails is said: a line of <c>sync.jsonl</c> that says why, and a warning that says what takes it up again.
/// </summary>
/// <param name="graph">Syncs the graph.</param>
/// <param name="store">Reads an agent's record and the run's ledger of its folds.</param>
/// <param name="ledger">Records which folds the graph took.</param>
/// <param name="contextLoader">Finds the repository and its configuration, for a step whose runner has not.</param>
/// <param name="platform">This machine.</param>
/// <param name="fileSystem">Says whether the run has a graph.</param>
/// <param name="output">Hears a sync that failed.</param>
/// <param name="time">The clock a failed sync's line is written by.</param>
public sealed class FlowGraphSyncs(
    ICodebaseGraphService graph,
    OrchestrationStore store,
    FoldLedger ledger,
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IFileSystem fileSystem,
    IHarnessOutput output,
    TimeProvider time)
{
    /// <summary>
    /// A whole sync of a tree, which takes every file it lists: one it could not read is counted in its line of
    /// <c>sync.jsonl</c> (<see cref="CodeGraphSyncLine.Unreadable"/>), since no step answers for the engine's own syncs.
    /// </summary>
    private async Task<bool> Whole(CodebaseGraphSync request, CancellationToken cancellationToken)
    {
        _ = await graph.SyncAsync(request, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>What a view's sync that failed is taken up by.</summary>
    private const string ViewAgain = "the next sync of that view brings it up to date";

    /// <summary>
    /// The commit a step's tree's work started from, which change-first compares the tree with (value 371): an agent's
    /// worktree's base as its record says it - where its worktree was made, or where its last refresh or rebase brought it -
    /// and for the main checkout, which an agent that reads and a step with no agent work in, the commit the run started
    /// from. Null where neither is recorded: a worktree not made yet, a run started beside the tree's history.
    /// </summary>
    /// <param name="agent">The record of the agent the step works in; null for a step with none.</param>
    /// <param name="run">The run's context.</param>
    public static string? ChangesBase(AgentRecord? agent, FlowRunContext run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return agent is { Reads: false } ? agent.Base : run.StartCommit;
    }

    /// <summary>
    /// A reason step starts: its view is synced by a whole listing - the agent's it works in, the base for one that reads -
    /// where the run has a graph, so a person's change of the main tree and the agent's own work since its last step are in
    /// what it is served.
    /// </summary>
    /// <param name="run">The step's run.</param>
    /// <param name="cancellationToken">Stops the sync, and the step with it.</param>
    public async Task AtStartAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Place is not { } given)
        {
            return;
        }

        FlowRunPlace place;
        HarnessContext context;

        // The repository and its configuration are found again for the hook alone: one that does not read is said, and
        // the step goes on to find it as its own runner does.
        try
        {
            (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Said(run, given.Run, layout: null, run.WorksIn ?? CodebaseGraphService.BaseView, $"{run.Instance}:start", ex, "synced", ViewAgain);
            return;
        }

        await SyncAsync(run, place, context, run.WorksIn, $"{run.Instance}:start", Whole, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A fold or a deletion was settled, or an attempt of one starts: the base is synced by every path the run's folds
    /// changed in the main tree that the graph has not taken yet - this fold's, and any an earlier sync left, failed or
    /// stopped or cut off by a crash - and each fold it took is recorded so, where the run has a graph.
    /// </summary>
    /// <param name="run">The step's run.</param>
    /// <param name="place">The run's place.</param>
    /// <param name="context">The repository's context.</param>
    /// <param name="cancellationToken">Stops the sync; what it owes stays owed.</param>
    public async Task AfterFoldAsync(FlowStepRun run, FlowRunPlace place, HarnessContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(context);

        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var why = $"{run.Instance}:{run.Step.Type}";

        // A run with no graph owes it nothing: its first sync lists the main tree whole.
        if (!fileSystem.FileExists(layout.GraphManifestFile))
        {
            return;
        }

        IReadOnlyList<FlowFoldEntry> owed;

        try
        {
            owed = FoldLedger.Ungraphed(store.ReadFolds(layout));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Said(run, place.Run, layout, CodebaseGraphService.BaseView, why, ex, "synced", FoldAgain);
            return;
        }

        if (owed.Count == 0)
        {
            return;
        }

        var paths = FoldLedger.Changed(owed);

        // A graph whose manifest does not read takes no path: the folds stay owed, and it is said with why.
        async Task<bool> TakesAsync(CodebaseGraphSync request, CancellationToken token)
        {
            try
            {
                return await graph.SyncPathsAsync(request, paths, token).ConfigureAwait(false) is not null;
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
            {
                Said(run, place.Run, layout, CodebaseGraphService.BaseView, why, ex, "synced", WholeAgain);
                return false;
            }
        }

        if (paths.Count > 0 && !await SyncAsync(run, place, context, agent: null, why, TakesAsync, cancellationToken, FoldAgain).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            ledger.Graphed(layout, [.. owed.Select(entry => entry.Id)]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Said(run, place.Run, layout, CodebaseGraphService.BaseView, why, ex, "recorded as synced", FoldAgain);
        }
    }

    /// <summary>
    /// A refresh or a rebase moved <paramref name="agent"/>'s worktree: its view is synced by a whole listing of it, where
    /// the run has made one. The main tree is only read by either, so the base does not move.
    /// </summary>
    /// <param name="run">The step's run.</param>
    /// <param name="place">The run's place.</param>
    /// <param name="context">The repository's context.</param>
    /// <param name="agent">The agent whose worktree moved.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    public Task AfterMoveAsync(FlowStepRun run, FlowRunPlace place, HarnessContext context, string agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(context);

        return fileSystem.FileExists(OrchestratorLayout.Of(context.Layout, place.Run).GraphViewFile(agent))
            ? SyncAsync(run, place, context, agent, $"{run.Instance}:{run.Step.Type}", Whole, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>
    /// An agent was deleted: the view of every agent of the run that is no longer live leaves its graph, where the run has
    /// one - this one's, and any a drop that failed left - and what only it named is removed when the last sync under way
    /// ends. The engine's every other sync of the run's graph drops them too.
    /// </summary>
    /// <param name="run">The step's run.</param>
    /// <param name="place">The run's place.</param>
    /// <param name="context">The repository's context.</param>
    /// <param name="cancellationToken">Cancels the wait for a sync of a view.</param>
    public Task AfterDeleteAsync(FlowStepRun run, FlowRunPlace place, HarnessContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(context);

        // A run with no graph has no view to drop.
        return DropGoneAsync(run, place, OrchestratorLayout.Of(context.Layout, place.Run), cancellationToken);
    }

    /// <summary>What a base sync by a fold's paths that failed is taken up by.</summary>
    private const string FoldAgain = "the paths it owes are synced again by the next fold or deletion, and by this step's next attempt";

    /// <summary>What a base sync by a fold's paths of a graph whose manifest does not read is taken up by.</summary>
    private const string WholeAgain = "the paths the run's folds changed stay owed until a whole sync builds the graph again";

    /// <summary>
    /// Drops the view of each agent of the run whose record says it is no longer live - deleted, closed, or none - one
    /// at a time under that view's gate; one that fails is said, and the engine's next sync of the graph tries it again.
    /// </summary>
    private async Task DropGoneAsync(FlowStepRun run, FlowRunPlace place, OrchestratorLayout layout, CancellationToken cancellationToken)
    {
        const string Again = "the engine's next sync of the run's graph drops it again";
        var why = $"{run.Instance}:{run.Step.Type}";
        IReadOnlyList<string> views;

        try
        {
            views = store.GraphViews(layout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No view is named by a listing that did not read: it is said, with no line of any view's.
            Said(run, place.Run, layout: null, "of an agent no longer live", why, ex, "dropped", Again);
            return;
        }

        foreach (var agent in views)
        {
            try
            {
                if (store.ReadAgent(layout, agent) is not { State: AgentStates.Live })
                {
                    await graph.DropViewAsync(layout, agent, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Said(run, place.Run, layout, agent, why, ex, "dropped", Again);
            }
        }
    }

    /// <summary>
    /// One sync of the view of <paramref name="agent"/>'s worktree - the base where it names none, or its agent reads only -
    /// where the run has a graph and the agent is live, after the views of agents no longer live are dropped; one that
    /// fails is said, and never fails the step.
    /// </summary>
    /// <returns>
    /// Whether the sync was made and went through: a sync by paths of a graph whose manifest does not read takes nothing,
    /// said with why, and what it was given stays owed until the whole sync that builds the graph again.
    /// </returns>
    private async Task<bool> SyncAsync(
        FlowStepRun run,
        FlowRunPlace place,
        HarnessContext context,
        string? agent,
        string why,
        Func<CodebaseGraphSync, CancellationToken, Task<bool>> sync,
        CancellationToken cancellationToken,
        string again = ViewAgain)
    {
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);

        if (!fileSystem.FileExists(layout.GraphManifestFile))
        {
            return false;
        }

        await DropGoneAsync(run, place, layout, cancellationToken).ConfigureAwait(false);

        var request = new CodebaseGraphSync(context.Layout, layout, place.Definition.CodebaseGraph?.Shape ?? CodebaseGraphShape.Default, context.Config.Worktrees.Root, Rebuild: false, why);

        // The view it is asked of: the agent's until its record says it reads, and the base's then.
        var view = agent ?? CodebaseGraphService.BaseView;

        try
        {
            if (agent is not null)
            {
                // An agent that reads has no worktree: it reads the main tree, and its view is the base.
                switch (store.ReadAgent(layout, agent))
                {
                    case { State: AgentStates.Live } record when record.WorktreePath(context.Layout) is { } worktree:
                        request = request with { Worktree = new CodebaseGraphWorktree(agent, worktree) };
                        break;

                    case { State: AgentStates.Live, Reads: true }:
                        view = CodebaseGraphService.BaseView;
                        break;

                    default:
                        return false;
                }
            }

            return await sync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Said(run, place.Run, layout, view, why, ex, "synced", again);
            return false;
        }
    }

    /// <summary>
    /// What a sync or a drop of the engine's own that failed leaves: a line of <c>sync.jsonl</c> that says why - where the
    /// run's place was found - and a warning that says what takes it up again; its step goes on. A line that cannot be
    /// written is said in the warning alone.
    /// </summary>
    private void Said(FlowStepRun run, string runName, OrchestratorLayout? layout, string view, string why, Exception ex, string done, string again)
    {
        if (layout is not null)
        {
            try
            {
                store.AppendGraphSync(layout, new CodeGraphSyncLine(time.GetUtcNow(), view, why, 0, 0, 0, 0, 0, 0) { Failed = ex.Message });
            }
            catch (Exception unwritten) when (unwritten is not OperationCanceledException)
            {
                output.Warn(run.Step.Type ?? "graph", $"the codebase graph's sync log of run '{runName}' was not written: {unwritten.Message.TrimEnd('.')}.");
            }
        }

        output.Warn(run.Step.Type ?? "graph", $"the codebase graph's view '{view}' of run '{runName}' was not {done} ({why}): {ex.Message.TrimEnd('.')}. The step goes on; {again}.");
    }
}
