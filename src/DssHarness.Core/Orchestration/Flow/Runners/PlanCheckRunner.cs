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
/// round before built under those names, are held to none. An item's <c>settles</c> names one of the issues the run
/// owes its next round whose ids the engine hands the check (<c>settleable</c>), or is refused; and a check handed those
/// issues whole (<c>next</c>) - the engine hands them to every check whose list the next round opens with - holds that
/// list to them as well, each settled, the settling items first: its own items, or, where its pieces join a round it is
/// handed (<c>round</c>), that round joined with them. The rule is this step's alone; which checks apply it is the
/// engine's. The same check reads the answer of a reason step whose items it will read, at the answer itself
/// (<see cref="AnswerProblemsAsync"/>), so the agent puts right in its own conversation what this step would find.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="validator">Checks the items.</param>
public sealed class PlanCheckRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, PlanValidator validator) : IFlowStepTypeRunner, IFlowAnswerCheckRunner
{
    /// <inheritdoc/>
    public string Type => "validate-plan";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var found = Checked(run, run.Read, context, place);

        return found.Refused is { } refused
            ? FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, refused)
            : FlowStepResult.Ended(
                passed: true,
                HarnessExit.Success,
                output: new JsonObject
                {
                    ["ok"] = found.Problems.Count == 0,
                    ["problems"] = new JsonArray([.. found.Problems.Select(problem => (JsonNode)problem)]),
                    ["items"] = found.Items!.DeepClone(),
                });
    }

    /// <summary>
    /// What this step, run as <paramref name="check"/> is with a reason step's answer in its read, would find wrong with that
    /// answer - by the one check its own run makes. None where the answer gives no work item: whether a list of none is a
    /// plan's problem is the check's own moment's, for its <c>when</c> and the loop around it decide whether it reads that
    /// answer at all - a diagnosis that is no fix and a self-audit that leaves nothing give none. None, too, where the check
    /// would refuse what it is handed, which it says at its own moment.
    /// </summary>
    /// <param name="check">The check's run, as the engine would hand it the run as it stands, with what it reads of the answer.</param>
    /// <param name="cancellationToken">Stops the loading of the repository's context.</param>
    public async Task<IReadOnlyList<string>> AnswerProblemsAsync(FlowStepRun check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);

        if (check.Read is not { } read || !read.TryGetValue("items", out var given) || given.ToNode() is not JsonArray { Count: > 0 })
        {
            return [];
        }

        var (place, context) = await FlowStepRunners.PlaceOf(check, contextLoader, platform, cancellationToken).ConfigureAwait(false);

        return Checked(check, read, context, place) is { Refused: null } found ? found.Problems : [];
    }

    /// <summary>
    /// What the check finds of what <paramref name="given"/> hands it: every problem, and the items it checked - its own and
    /// those plan updates owe the round it opens, the settling items first; or why it cannot check them at all, where its
    /// items yield no list or its scope no pieces. The one check the step's own run and an answer read ahead of it make.
    /// </summary>
    /// <param name="run">The check's run, whose instance a refusal names.</param>
    /// <param name="given">What the engine read for the check, by key.</param>
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="place">The run the check belongs to.</param>
    private (IReadOnlyList<string> Problems, JsonArray? Items, string? Refused) Checked(FlowStepRun run, IReadOnlyDictionary<string, FlowValue>? given, HarnessContext context, FlowRunPlace place)
    {
        if (given is not { } read || !read.TryGetValue("items", out var yielded) || yielded.IsMissing || yielded.ToNode() is not JsonArray items)
        {
            return ([], null, $"'{run.Instance}' checks the work items its items yields, and it yields no list");
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
        // list is no update, and nothing more is checked; anything but a list is no pieces at all. Where they join a
        // round the engine hands the check, that round joined with them is the list it opens with once the update is
        // placed.
        IReadOnlyList<JsonNode?>? joinedRound = null;

        if (read.TryGetValue("scope", out var scope) && !scope.IsMissing && scope.ToNode() is { } update)
        {
            if (update is not JsonArray pieces)
            {
                return ([], null, $"'{run.Instance}' is given the pieces of a plan update its scope yields, and it yields no list");
            }

            if (pieces.Count > 0)
            {
                IReadOnlyList<JsonNode?>? round = read.TryGetValue("round", out var joining) && !joining.IsMissing && joining.ToNode() is JsonArray joiningList ? [.. joiningList] : null;

                problems.AddRange(validator.CheckPieces([.. pieces], [.. items], round, Names(read, "known"), context, place.Run, place.Definition, out var joined, carried));
                joinedRound = round is null ? null : joined;
            }
        }

        // The ids of the issues the run owes its next round, where the engine hands them - to every check whose items a
        // round opens with: an item may name one of them in settles and no other, for a round built on another would
        // settle nothing; a check handed none - a recovery's - refuses every settles. And the issues themselves, where
        // the engine hands them - to every check whose list the next round opens with - which the check holds that list
        // to: each named in an item's settles, none left unsettled, and the items that settle them ordered first - the
        // run's defects taken from its own rounds are built before the work that came after them. The list is its own,
        // or, where its pieces join a round it is handed, that round joined with them: the one the round opens with.
        var settleable = Names(read, "settleable");
        List<JsonObject> owedNext = read.TryGetValue("next", out var owedNow) && !owedNow.IsMissing && owedNow.ToNode() is JsonArray owedList
            ? [.. owedList.Select(issue => issue as JsonObject).OfType<JsonObject>()]
            : [];
        IReadOnlyList<JsonObject> held = [.. checkedItems.Select(item => item as JsonObject).OfType<JsonObject>()];
        IReadOnlyList<JsonObject> opensWith = joinedRound is null ? held : [.. joinedRound.Select(item => item as JsonObject).OfType<JsonObject>()];
        string? Settling(JsonObject item) => TextOf(item["settles"]) is { Length: > 0 } settles ? settles : null;
        List<string> nextProblems =
        [
            .. held
                .Where(item => Settling(item) is { } names && !settleable.Contains(names, StringComparer.Ordinal))
                .Select(item => FlowWorkItems.NameOf(item) is { } named
                    ? $"the item '{named}' settles '{Settling(item)}', which is not an issue the run owes its next round"
                    : $"an item settles '{Settling(item)}', which is not an issue the run owes its next round"),
            .. owedNext
                .Where(issue => !opensWith.Any(item => Settling(item) is { } names && string.Equals(names, TextOf(issue["id"]), StringComparison.Ordinal)))
                .Select(issue => $"the issue '{TextOf(issue["id"])}' ({TextOf(issue["title"])}) is owed the next round, and no work item settles it"),
        ];
        problems.AddRange(nextProblems);

        // The items that settle an owed issue come first, their own order kept: what the run took from its own rounds
        // is built before the work planned around it, whatever order the round's own list held.
        if (nextProblems.Count == 0 && owedNext.Count > 0)
        {
            checkedItems = new JsonArray(
                [.. checkedItems
                    .Select(item => item?.DeepClone())
                    .OrderBy(item => item is JsonObject held && Settling(held) is { Length: > 0 } ? 0 : 1)]);
        }

        // A problem two of its rules find - a pair of items that meet, found in the plan's list and again in the round the
        // pieces join - is said once.
        return ([.. problems.Distinct(StringComparer.Ordinal)], checkedItems, null);
    }

    /// <summary>The names the engine hands the step under <paramref name="key"/>, each a text; none where it hands none.</summary>
    private static IReadOnlyList<string> Names(IReadOnlyDictionary<string, FlowValue> read, string key)
        => read.TryGetValue(key, out var given) && !given.IsMissing && given.ToNode() is JsonArray list
            ? [.. list.Select(name => name is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>()]
            : [];

    /// <summary>A value of an issue or an item as text; null where it is none, or not text.</summary>
    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
