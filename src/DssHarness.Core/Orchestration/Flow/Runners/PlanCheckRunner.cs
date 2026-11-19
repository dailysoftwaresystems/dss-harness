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
/// that list, so nothing joins a round unchecked. A work item is known by its name across the run, so no item a round
/// builds but an owed one, and no added piece, takes a name an instance of the work-item group carries already: a round
/// added to a work-item group with no loop around it builds the owed work alone, and its own items, which the group's
/// round before built under those names, are held to none.
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

        // The names every instance of the work-item group carries, over every round: the run knows a work item by its name
        // alone - its tests, its updates and its account are kept under it - so no round opens with one of them, and no
        // added piece takes one.
        var carried = Names(read, "carried");

        // The items plan updates owe the round this check opens, where it opens one: they join its own as one list,
        // checked together, and the group expands from what was checked. An owed work item replaces the one of its
        // name the round's own list holds - that list was written before the update that owes this one, and what the
        // update changed is the item it now owes - so the list holds one item of each name.
        List<JsonNode?>? owed = null;
        var opening = false;

        // Whether the round this check opens carries the owed work alone: one added to a work-item group with no loop
        // around it, whose own items the group's round before built, so it builds none of them again.
        var owedAlone = read.TryGetValue("owedAlone", out var alone) && alone.ToNode() is JsonValue flag && flag.TryGetValue<bool>(out var only) && only;

        if (read.TryGetValue("owed", out var owing) && !owing.IsMissing && owing.ToNode() is JsonArray owingList)
        {
            owed = [.. owingList.Where(item => item is JsonObject && FlowWorkItems.NameOf(item) is not null)];
            opening = true;
        }

        if (opening)
        {
            var owedNames = owed!.Select(FlowWorkItems.NameOf).OfType<string>().ToList();

            checkedItems = new JsonArray(
                [.. items
                    .Where(item => FlowWorkItems.NameOf(item) is not { } named || !owedNames.Contains(named, StringComparer.Ordinal))
                    .Select(item => item?.DeepClone()),
                 .. owed!.Select(item => item?.DeepClone())]);

            // An owed item may bear the name of one an earlier round carries: a changed or a removed piece names the item
            // it changes or takes back. The round's own items may not, where the round builds them: a round that carries
            // the owed work alone builds none of them, for the round before built them under those very names.
            problems.AddRange(validator.Check(
                [.. checkedItems],
                context,
                place.Run,
                place.Definition,
                carried: owedAlone ? [] : [.. carried.Where(name => !owedNames.Contains(name, StringComparer.Ordinal))]));
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

                problems.AddRange(validator.CheckPieces([.. pieces], [.. items], round, Names(read, "known"), context, place.Run, place.Definition, carried));
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

    /// <summary>The names the engine hands the step under <paramref name="key"/>, each a text; none where it hands none.</summary>
    private static IReadOnlyList<string> Names(IReadOnlyDictionary<string, FlowValue> read, string key)
        => read.TryGetValue(key, out var given) && !given.IsMissing && given.ToNode() is JsonArray list
            ? [.. list.Select(name => name is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>()]
            : [];
}
