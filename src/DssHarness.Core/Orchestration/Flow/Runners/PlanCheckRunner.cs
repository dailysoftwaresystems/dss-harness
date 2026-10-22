using System.Text.Json.Nodes;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The <c>validate-plan</c> step: the work items its <c>items</c> yields, checked by <see cref="PlanValidator"/>; its
/// answer is an output - <c>ok</c>, each problem as a line, and the items it checked - never a failure. The pieces of a
/// plan update its <c>scope</c> yields are checked with them by the plan update's slice: until then a step given pieces
/// fails, saying so, rather than pass them unchecked.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="validator">Checks the items.</param>
public sealed class PlanCheckRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, PlanValidator validator) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "validate-plan";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);

        if (run.Read is not { } read || !read.TryGetValue("items", out var given) || given.IsMissing || given.ToNode() is not JsonArray items)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' checks the work items its items yields, and it yields no list");
        }

        // Missing, null or an empty list is no update, and nothing more is checked; anything but a list is no pieces at all.
        if (read.TryGetValue("scope", out var scope) && !scope.IsMissing && scope.ToNode() is { } yielded)
        {
            if (yielded is not JsonArray pieces)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' is given the pieces of a plan update its scope yields, and it yields no list");
            }

            if (pieces.Count > 0)
            {
                return FlowStepResult.Ended(
                    passed: false,
                    HarnessExit.ToolMissing,
                    $"'{run.Instance}' is given the pieces of a plan update, which this build does not check yet: the plan update is a later slice of the engine, and the plan was not checked");
            }
        }

        var problems = validator.Check([.. items], context, place.Run, place.Definition);

        return FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: new JsonObject
            {
                ["ok"] = problems.Count == 0,
                ["problems"] = new JsonArray([.. problems.Select(problem => (JsonNode)problem)]),
                ["items"] = items.DeepClone(),
            });
    }
}
