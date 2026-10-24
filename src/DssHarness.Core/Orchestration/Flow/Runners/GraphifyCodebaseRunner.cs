using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Codebase;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The <c>graphify-codebase</c> step: one sync of the run's one codebase graph, wherever the flow puts it - each instance,
/// visit and retry one more, a sync of a tree that did not change reading nothing - of the base, or with <c>in</c> of that
/// agent's view. Its output is that sync's. A graph is a map, never red over what it holds: the step fails over a file it
/// could not read only where its <c>failOn: incomplete</c> says so (<see cref="HarnessExit.CommandFailed"/>). It is refused
/// (<see cref="HarnessExit.Refused"/>) where the graph's paths would not fit under the run's directory, or the agent it
/// names has no worktree to list; and it fails where git could not be started (<see cref="HarnessExit.ToolMissing"/>) or
/// failed to list the tree (<see cref="HarnessExit.CommandFailed"/>).
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="graph">Syncs the graph.</param>
/// <param name="pathBudget">Checks the graph's longest path fits under the run's directory.</param>
/// <param name="extractors">The implementations of the contract, whose names and versions the graph's longest path holds.</param>
/// <param name="store">Reads the record of the agent a step names with <c>in</c>.</param>
public sealed class GraphifyCodebaseRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    ICodebaseGraphService graph,
    IPathBudget pathBudget,
    CodeGraphExtractors extractors,
    OrchestrationStore store) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "graphify-codebase";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);

        if (Budget(layout, place, context.Config.Worktrees) is { } refusal)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, refusal);
        }

        var request = new CodebaseGraphSync(context.Layout, layout, place.Definition.CodebaseGraph?.Shape ?? CodebaseGraphShape.Default, context.Config.Worktrees.Root, run.Step.Rebuild == true, run.Instance);

        if (run.Step.In is { } named)
        {
            // The view of the worktree of the agent the step's create-agent made, which the step waited for; an agent that
            // only reads has none, and reads the main tree: its view is the base.
            if (run.WorksIn is not { } agent)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{run.Instance}' syncs the view of the agent '{named}' makes, and the run has made none for it");
            }

            switch (store.ReadAgent(layout, agent))
            {
                case { State: AgentStates.Live } record when record.WorktreePath(context.Layout) is { } worktree:
                    request = request with { Worktree = new CodebaseGraphWorktree(agent, worktree) };
                    break;

                case { State: AgentStates.Live, Reads: true }:
                    break;

                default:
                    return FlowStepResult.Ended(passed: false, HarnessExit.Refused, $"'{run.Instance}' syncs the view of agent '{agent}' of '{place.Run}', which has no worktree to list. Nothing was synced.");
            }
        }

        var synced = await graph.SyncAsync(request, cancellationToken).ConfigureAwait(false);

        var output = Output(synced);

        if (run.Step.FailOn == WorkflowStepTypes.GraphIncomplete && synced.Unreadable is { Count: > 0 } unreadable)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.CommandFailed,
                $"the graph has {ReportText.Counted(unreadable.Count, "file")} it could not read, and the step says failOn: incomplete: {ReportText.Listed([.. unreadable.Select(file => $"{file.Path} ({file.Why})")])}",
                output);
        }

        return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: output);
    }

    /// <summary>
    /// Why the graph's longest path would not fit under the run's directory - an extract's, whose shape is fixed: the
    /// extracts directory, a blob's first two characters, the blob, its path's digest, the longest name and version of an
    /// implementation - or <see langword="null"/> where it fits, as a run's other paths are checked.
    /// </summary>
    private string? Budget(OrchestratorLayout run, FlowRunPlace place, Configuration.WorktreeSettings settings)
    {
        var blob = new string('0', place.StartCommit?.Length ?? 64);
        var language = extractors.All.MaxBy(extractor => extractor.Language.Length)!;
        var version = extractors.All.Max(extractor => extractor.Version);
        var longest = $"{OrchestratorLayout.GraphExtractsDirectoryName}/{CodeGraphIds.ExtractName(blob, "x", language.Language, version)}";
        var budget = pathBudget.Check(run.GraphDirectory, longest.Length, settings.PathBudgetMargin, settings.PathLimit);

        return budget.IsWithinBudget
            ? null
            : $"the run's codebase graph keeps paths of {longest.Length.ToString(CultureInfo.InvariantCulture)} characters under '{run.GraphDirectory}', and they would not fit: {budget.Describe(run.GraphDirectory)} Nothing was synced.";
    }

    /// <summary>The step's output: the sync's, as its type's built-in schema names it.</summary>
    private static JsonObject Output(CodebaseGraphSynced synced)
        => new()
        {
            ["graph"] = synced.Graph,
            ["view"] = synced.View,
            ["commit"] = synced.Commit,
            ["dirty"] = synced.Dirty,
            ["files"] = synced.Files,
            ["extracted"] = synced.Extracted,
            ["reused"] = synced.Reused,
            ["removed"] = synced.Removed,
            ["nodes"] = synced.Nodes,
            ["edges"] = synced.Edges,
            ["languages"] = new JsonArray([.. synced.Languages.Select(language => (JsonNode)new JsonObject
            {
                ["language"] = language.Language,
                ["files"] = language.Files,
                ["nodes"] = language.Nodes,
            })]),
            ["skipped"] = new JsonArray([.. synced.Skipped.Select(file => (JsonNode)new JsonObject
            {
                ["path"] = file.Path,
                ["state"] = file.State,
                ["why"] = file.Why,
            })]),
            ["seconds"] = synced.Seconds,
        };
}
