using System.Text.Json.Nodes;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The <c>route-findings</c> step: the review's findings, routed by the class of each - the one owner of the routing.
/// Every <c>introduced</c> finding goes back as itself; the <c>reopened</c> findings of one class go back as one
/// send-back, the class with all of them, ordered "run the neighbour sweep for the whole class" - never the one
/// finding alone; and a class reopened a second time goes to <c>homeAudit</c> instead, for a second send-back on one
/// class means the first fix was wrong at its root, and the next action is the home audit. Which rounds reopened each
/// class the item's ledger keeps, <c>closure-ledger.json</c> in its agent's directory, written here and read by the
/// <c>class-closure</c> check - so the count lives in the engine, across the fix loop's iterations and across a
/// resume, where no reviewer could carry it: reviewers are fresh each round. A round is counted once, by the instance
/// that routed it, however often that instance runs. Its answer is an output - <c>{rework, homeAudit, reopened}</c> -
/// never a failure but for findings it cannot route (<see cref="FindingRoutes.Problems"/>), which the reviewers' own
/// answers were held to before (<see cref="FindingRoutes.HeldTo"/>).
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads and writes the item's closure ledger.</param>
public sealed class RouteFindingsStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "route-findings";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);

        if (run.Read is not { } read || !read.TryGetValue("findings", out var given) || given.IsMissing || given.ToNode() is not JsonArray findings)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' routes the findings its findings yields, and it yields no list");
        }

        var layout = OrchestratorLayout.Of(context.Layout, place.Run);

        if ((run.WorksIn ?? run.Target) is not { } agent)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, $"'{run.Instance}' is a {Type} step, and it names no agent whose item's closure ledger it keeps");
        }

        // The classes a reopened finding may name: the item's planned classes and those its record added, exactly.
        var classes = FindingRoutes.ClassesOf(ReadOf(read, "classes"), ReadOf(read, "record"));

        if (FindingRoutes.Problems(findings, classes) is [_, ..] problems)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.ConfigInvalid,
                $"'{run.Instance}' cannot route the review's findings: {string.Join("; ", problems)}. Nothing was routed and the item's ledger was not changed");
        }

        // The findings of one class this round, first to last, and every introduced finding as it was reported: a
        // send-back is the finding the diff made, or the class whole.
        List<JsonNode> rework = [];
        Dictionary<string, List<JsonObject>> reopened = new(StringComparer.Ordinal);

        foreach (var finding in findings.OfType<JsonObject>())
        {
            if (FindingRoutes.ClassOf(finding) == FindingRoutes.Introduced)
            {
                rework.Add(finding.DeepClone());
                continue;
            }

            var id = FindingRoutes.ReopensOf(finding)!;

            if (!reopened.TryGetValue(id, out var ofClass))
            {
                ofClass = [];
                reopened[id] = ofClass;
            }

            ofClass.Add(finding);
        }

        // The round counted by the routing that made it: an instance already counted for a class - run again after a
        // crash, a stop or a resume - counts nothing more, so a first reopening is never read as a second.
        var stored = store.ReadClosureLedger(layout, agent);
        var counted = (stored ?? new FlowClosureLedgerFile(1, agent, [])).Counting(run.Instance, reopened.Keys);

        if (stored is null ? counted.Classes.Count > 0 : !counted.Classes.SequenceEqual(stored.Classes, FlowClosureReopensEquality.Instance))
        {
            store.WriteClosureLedger(layout, agent, counted);
        }

        List<JsonObject> audits = [];
        List<JsonObject> sendBacks = [];

        foreach (var (id, ofClass) in reopened)
        {
            var rounds = counted.Classes.First(entry => string.Equals(entry.Class, id, StringComparison.Ordinal)).Reopens;

            // One send-back a round: the class whole, graded by the worst of its findings, its findings all in it.
            var sendBack = FindingRoutes.SendBack(id, rounds, ofClass);

            if (FlowClosureReopens.AsksItsHomeAudit(rounds))
            {
                audits.Add(sendBack);
            }
            else
            {
                sendBacks.Add(sendBack);
            }
        }

        return FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: new JsonObject
            {
                ["rework"] = new JsonArray([.. rework, .. sendBacks]),
                ["homeAudit"] = new JsonArray([.. audits]),
                ["reopened"] = new JsonArray([.. counted.Classes.Select(entry => (JsonNode)new JsonObject
                {
                    [FindingRoutes.ClassMember] = entry.Class,
                    [FindingRoutes.RoundsMember] = entry.Reopens,
                })]),
            });
    }

    /// <summary>What the engine read for <paramref name="key"/> where the step started; null where it read nothing.</summary>
    private static JsonNode? ReadOf(IReadOnlyDictionary<string, FlowValue> read, string key)
        => read.TryGetValue(key, out var value) && !value.IsMissing ? value.ToNode() : null;

    /// <summary>Two ledger entries the same: one class, counted by the same routings in the same order.</summary>
    private sealed class FlowClosureReopensEquality : IEqualityComparer<FlowClosureReopens>
    {
        public static readonly FlowClosureReopensEquality Instance = new();

        public bool Equals(FlowClosureReopens? one, FlowClosureReopens? other)
            => one is not null && other is not null && string.Equals(one.Class, other.Class, StringComparison.Ordinal) && one.By.SequenceEqual(other.By, StringComparer.Ordinal);

        public int GetHashCode(FlowClosureReopens entry) => StringComparer.Ordinal.GetHashCode(entry.Class);
    }
}

/// <summary>
/// The one owner of what a review's finding must say to be routed by its class, and of how a routed class is sent back:
/// a finding is an object, classed <see cref="Introduced"/> or <see cref="Reopened"/>, graded on <see cref="Severities"/>,
/// and a reopened one names, in <c>reopens</c>, one class of the item's - exactly as the item's plan or its record spells
/// it, compared ordinally on every machine, for a class id is no path. The <c>route-findings</c> step refuses findings
/// that do not; and the reviewers whose answers it routes are held to the same rules at their answer
/// (<see cref="HeldTo"/>), where a wrong one is asked again in the same conversation as a schema's repair is - never sent
/// to the recovery, which cannot change a reviewer's answer.
/// </summary>
public static class FindingRoutes
{
    /// <summary>The class of a finding the diff made.</summary>
    public const string Introduced = "introduced";

    /// <summary>The class of a finding that is another instance of a class the item closes.</summary>
    public const string Reopened = "reopened";

    /// <summary>The member a class send-back names its class by - never a finding's own <c>class</c>, which says introduced or reopened.</summary>
    public const string SendBackMember = "reopenedClass";

    /// <summary>The member a class's count of reopening rounds is said by, in a send-back and in the counts.</summary>
    public const string RoundsMember = "rounds";

    /// <summary>The member each count of the route's <c>reopened</c> names its class by.</summary>
    public const string ClassMember = "class";

    /// <summary>The severities a finding is graded on, worst first: how a class's send-back is graded, by the worst of its findings.</summary>
    public static readonly IReadOnlyList<string> Severities = ["critical", "high", "medium", "low", "nit"];

    /// <summary>
    /// The class ids a reopened finding may name: the id of each class <paramref name="classes"/> - the item's plan - declares,
    /// and the class of each entry of <paramref name="record"/> - the builder's record, which adds those it found - read as
    /// the class-closure check reads both (<see cref="PlannedClass.In"/>, <see cref="ClassClosureRecord.In"/>), each once.
    /// </summary>
    /// <param name="classes">The item's planned classes; null where none were read.</param>
    /// <param name="record">The builder's record; null where none was read.</param>
    public static IReadOnlyList<string> ClassesOf(JsonNode? classes, JsonNode? record)
        => [.. (classes as JsonArray ?? []).Select(PlannedClass.In).OfType<PlannedClass>().Select(planned => planned.Id)
            .Concat((record as JsonArray ?? []).Select(ClassClosureRecord.In).OfType<ClassClosureRecord>().Select(entry => entry.Class))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Why <paramref name="findings"/> cannot be routed, each problem a path in the list and what is wrong there, every one at
    /// once; empty where each finding can be.
    /// </summary>
    /// <param name="findings">The findings, as the review gave them.</param>
    /// <param name="classes">The class ids a reopened finding may name (<see cref="ClassesOf"/>).</param>
    public static IReadOnlyList<string> Problems(JsonNode? findings, IReadOnlyList<string> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);

        if (findings is not JsonArray list)
        {
            return ["is no list of findings"];
        }

        var problems = new List<string>();
        var named = classes.Count == 0 ? "the item has none" : $"the item's are {string.Join(", ", classes.Select(id => $"'{id}'"))}";

        for (var at = 0; at < list.Count; at++)
        {
            var place = $"[{at.ToString(System.Globalization.CultureInfo.InvariantCulture)}]";

            if (list[at] is not JsonObject finding)
            {
                problems.Add($"{place}: is no finding: a finding is an object");
                continue;
            }

            var @class = ClassOf(finding);

            if (@class is not (Introduced or Reopened))
            {
                problems.Add($"{place}.class: '{@class ?? "none"}' is no class of a finding: every finding is {Introduced} or {Reopened}");
            }
            else if (@class == Reopened && ReopensOf(finding) is not { } reopens)
            {
                problems.Add($"{place}.reopens: names no class: a reopened finding names the class it reopens, exactly, and {named}");
            }
            else if (@class == Reopened && !classes.Contains(ReopensOf(finding)!, StringComparer.Ordinal))
            {
                problems.Add($"{place}.reopens: '{ReopensOf(finding)}' is no class of the item: {named}; name one exactly, or class the finding {Introduced}");
            }

            if (TextOf(finding["severity"]) is not { } severity || !Severities.Contains(severity, StringComparer.Ordinal))
            {
                problems.Add($"{place}.severity: '{TextOf(finding["severity"]) ?? "none"}' is not on the scale a finding is graded on: {string.Join(", ", Severities)}");
            }
        }

        return problems;
    }

    /// <summary>
    /// What a reviewer's answer is held to where a <c>route-findings</c> step routes it: the routed member of its output -
    /// <paramref name="member"/>, the findings - by <see cref="Problems"/>, each problem a path in the output, so the gate
    /// asks the reviewer again in its own conversation, as a schema's problem is asked.
    /// </summary>
    /// <param name="member">The member of the reviewer's output the route reads.</param>
    /// <param name="classes">The class ids a reopened finding may name (<see cref="ClassesOf"/>).</param>
    public static Func<JsonNode, IReadOnlyList<string>> HeldTo(string member, IReadOnlyList<string> classes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        ArgumentNullException.ThrowIfNull(classes);

        return output => output is JsonObject answer && answer[member] is JsonArray findings
            ? [.. Problems(findings, classes).Select(problem => $"{member}{problem}")]
            : [];
    }

    /// <summary>
    /// One class sent back, first or later reopening alike: the class by <see cref="SendBackMember"/>, how many rounds
    /// reopened it, graded by the worst of its findings, and every finding of the round in it.
    /// </summary>
    /// <param name="id">The class.</param>
    /// <param name="rounds">How many rounds have reopened it, this one among them.</param>
    /// <param name="findings">The round's findings of it, in the order the review gave them.</param>
    public static JsonObject SendBack(string id, int rounds, IReadOnlyList<JsonObject> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        return new JsonObject
        {
            [SendBackMember] = id,
            [RoundsMember] = rounds,
            ["severity"] = WorstOf(findings),
            ["findings"] = new JsonArray([.. findings.Select(finding => finding.DeepClone())]),
        };
    }

    /// <summary>A finding's class, as it says it; null where it says none as text.</summary>
    /// <param name="finding">The finding.</param>
    public static string? ClassOf(JsonObject finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return TextOf(finding["class"]);
    }

    /// <summary>The class a reopened finding reopens; null where it names none.</summary>
    /// <param name="finding">The finding.</param>
    public static string? ReopensOf(JsonObject finding)
    {
        ArgumentNullException.ThrowIfNull(finding);

        return TextOf(finding["reopens"]) is { } named && !string.IsNullOrWhiteSpace(named) ? named : null;
    }

    /// <summary>The worst severity of <paramref name="findings"/>, each on the scale: how a class's send-back is graded, for the loop's blocking to read.</summary>
    private static string WorstOf(IReadOnlyList<JsonObject> findings)
        => Severities[findings.Select(finding => RankOf(TextOf(finding["severity"]))).Where(at => at >= 0).DefaultIfEmpty(Severities.Count - 1).Min()];

    /// <summary>Where <paramref name="severity"/> stands on <see cref="Severities"/>, worst first; -1 where it is not on it.</summary>
    private static int RankOf(string? severity)
    {
        for (var at = 0; at < Severities.Count; at++)
        {
            if (string.Equals(Severities[at], severity, StringComparison.Ordinal))
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>A value of a finding as text; null where it is none, or not text.</summary>
    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
