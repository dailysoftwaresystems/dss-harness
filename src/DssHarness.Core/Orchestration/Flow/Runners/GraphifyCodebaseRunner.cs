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
/// visit and retry one more, a sync of a tree that did not change reading nothing. Its output is that sync's; a graph is a
/// map, never red, and fails only where its <c>failOn: incomplete</c> says a file it could not read fails it.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="graph">Syncs the graph.</param>
/// <param name="pathBudget">Checks the graph's longest path fits under the run's directory.</param>
/// <param name="extractors">The implementations of the contract, whose names and versions the graph's longest path holds.</param>
public sealed class GraphifyCodebaseRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    ICodebaseGraphService graph,
    IPathBudget pathBudget,
    CodeGraphExtractors extractors) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "graphify-codebase";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);

        // The check of the definition refuses it first (V52); a definition held in memory is never checked.
        if (run.Step.In is { } named)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, $"'{run.Instance}' names in: '{named}', and {WorkflowStepTypes.GraphViewsLater}. Nothing was synced.");
        }

        var layout = OrchestratorLayout.Of(context.Layout, place.Run);

        if (Budget(layout, place, context.Config.Worktrees) is { } refusal)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, refusal);
        }

        var shape = place.Definition.CodebaseGraph?.Shape ?? CodebaseGraphShape.Default;
        var synced = await graph.SyncAsync(
            new CodebaseGraphSync(context.Layout, layout, shape, context.Config.Worktrees.Root, run.Step.Rebuild == true, run.Instance),
            cancellationToken).ConfigureAwait(false);

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
