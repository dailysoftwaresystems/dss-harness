using System.Text.Json.Nodes;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The <c>validate-plan</c> step: the work items its <c>items</c> yields, checked by <see cref="PlanValidator"/>; its
/// answer is an output - <c>ok</c>, each problem as a line, and the items it checked - never a failure. The pieces of a
/// plan update its <c>scope</c> yields are checked with them, and with the items of the round they join; where it opens
/// a round, it checks one list - its own items and every work item plan updates owe it - and its output <c>items</c> is
/// that list, so nothing joins a round unchecked.
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

        var problems = new List<string>();
        var checkedItems = items;

        // The items plan updates owe the round this check opens, where it opens one: they join its own as one list,
        // checked together, and the group expands from what was checked. A work item is never added where the list
        // already holds one of its name - the round's expression had yielded it from the plan as updated.
        List<JsonNode?>? owed = null;

        if (read.TryGetValue("owed", out var owing) && !owing.IsMissing && owing.ToNode() is JsonArray owingList)
        {
            owed = [.. owingList.Where(item => item is JsonObject held
                && !items.Any(other => (other as JsonObject)?["name"]?.GetValue<string>() is { } named && string.Equals(named, held["name"]?.GetValue<string>(), StringComparison.Ordinal)))];
        }

        if (owed is not null)
        {
            if (items.Count > PlanValidator.MostItems)
            {
                problems.Add(PlanValidator.MostItemsProblem(items.Count));
            }

            checkedItems = new JsonArray([.. items.Select(item => item?.DeepClone()), .. owed.Select(item => item?.DeepClone())]);
            problems.AddRange(validator.Check([.. checkedItems], context, place.Run, place.Definition, mostItems: false));
        }
        else
        {
            problems.AddRange(validator.Check([.. items], context, place.Run, place.Definition));
        }

        // The pieces of a plan update, checked with the items where the step names them: Missing, null or an empty
        // list is no update, and nothing more is checked; anything but a list is no pieces at all.
        if (read.TryGetValue("scope", out var scope) && !scope.IsMissing && scope.ToNode() is { } yielded)
        {
            if (yielded is not JsonArray pieces)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' is given the pieces of a plan update its scope yields, and it yields no list");
            }

            if (pieces.Count > 0)
            {
                IReadOnlyList<JsonNode?>? round = read.TryGetValue("round", out var joining) && !joining.IsMissing && joining.ToNode() is JsonArray joiningList ? [.. joiningList] : null;
                var known = read.TryGetValue("known", out var had) && !had.IsMissing && had.ToNode() is JsonArray hadList
                    ? (IReadOnlyList<string>)[.. hadList.Select(name => (name as JsonValue)?.GetValue<string>()).OfType<string>()]
                    : [];

                problems.AddRange(validator.CheckPieces([.. pieces], [.. items], round, known, context, place.Run, place.Definition));
            }
        }

        return FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: new JsonObject
            {
                ["ok"] = problems.Count == 0,
                ["problems"] = new JsonArray([.. problems.Select(problem => (JsonNode)problem)]),
                ["items"] = checkedItems.DeepClone(),
            });
    }
}
