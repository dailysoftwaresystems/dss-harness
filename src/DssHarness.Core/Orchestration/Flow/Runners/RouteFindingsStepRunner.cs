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
/// class means the first fix was wrong at its root, and the next action is the home audit. How often each class was
/// reopened the item's ledger keeps, <c>closure-ledger.json</c> in its agent's directory, written here and read by the
/// <c>class-closure</c> check - so the count lives in the engine, across the fix loop's iterations and across a
/// resume, where no reviewer could carry it: reviewers are fresh each round. Its answer is an output -
/// <c>{rework, homeAudit, reopened}</c> - never a failure but for findings it cannot route.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads and writes the item's closure ledger.</param>
public sealed class RouteFindingsStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store) : IFlowStepTypeRunner
{
    /// <summary>The severities a finding takes, worst first: how a class's send-back is graded, by the worst of its findings.</summary>
    private static readonly IReadOnlyList<string> Severities = ["critical", "high", "medium", "low", "nit"];

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

        var ledger = store.ReadClosureLedger(layout, agent);

        // The findings of one class this round, first to last, and every introduced finding as it was reported: a
        // send-back is the finding the diff made, or the class whole.
        List<JsonNode> rework = [];
        Dictionary<string, List<JsonObject>> reopened = new(StringComparer.Ordinal);

        foreach (var node in findings)
        {
            if (node is not JsonObject finding)
            {
                continue;
            }

            var @class = TextOf(finding["class"]);

            if (@class == "introduced")
            {
                rework.Add(finding.DeepClone());
                continue;
            }

            if (@class != "reopened")
            {
                return FlowStepResult.Ended(
                    passed: false,
                    HarnessExit.ConfigInvalid,
                    $"'{run.Instance}' cannot route a finding whose class is '{@class ?? "none"}': every finding is introduced or reopened");
            }

            if (string.IsNullOrWhiteSpace(TextOf(finding["reopens"])))
            {
                return FlowStepResult.Ended(
                    passed: false,
                    HarnessExit.ConfigInvalid,
                    $"'{run.Instance}' cannot route a reopened finding that names no class it reopens: reopens holds the class id, as the item's classes name it");
            }

            var id = TextOf(finding["reopens"])!;

            if (!reopened.TryGetValue(id, out var ofClass))
            {
                ofClass = [];
                reopened[id] = ofClass;
            }

            ofClass.Add(finding);
        }

        var prior = ledger?.Classes.ToDictionary(entry => entry.Class, entry => entry.Reopens, StringComparer.Ordinal) ?? new Dictionary<string, int>(StringComparer.Ordinal);

        List<JsonObject> audits = [];
        List<JsonObject> sendBacks = [];

        foreach (var (id, ofClass) in reopened)
        {
            var count = (prior.TryGetValue(id, out var reopens) ? reopens : 0) + 1;
            prior[id] = count;

            // One send-back a round: the class whole, graded by the worst of its findings, its findings all in it.
            var sendBack = SendBackOf(id, count, ofClass);

            if (count >= 2)
            {
                audits.Add(sendBack);
            }
            else
            {
                sendBacks.Add(sendBack);
            }
        }

        // What a class's send-back says and carries, first or second reopening alike.
        JsonObject SendBackOf(string id, int count, List<JsonObject> ofClass)
            => new()
            {
                ["class"] = id,
                ["reopens"] = count,
                ["severity"] = WorstOf(ofClass),
                ["findings"] = new JsonArray([.. ofClass.Select(finding => finding.DeepClone())]),
            };

        // The ledger, written where this round moved it: one entry a class the run's reviews have reopened, ever.
        if (ledger is null || !ledger.Classes.Select(entry => (entry.Class, entry.Reopens)).SequenceEqual(prior.Select(pair => (pair.Key, pair.Value))))
        {
            store.WriteClosureLedger(
                layout,
                agent,
                new FlowClosureLedgerFile(1, agent, [.. prior.Select(pair => new FlowClosureReopens(pair.Key, pair.Value))]));
        }

        return FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: new JsonObject
            {
                ["rework"] = new JsonArray([.. rework, .. sendBacks]),
                ["homeAudit"] = new JsonArray([.. audits]),
                ["reopened"] = new JsonArray([.. prior.Select(pair => (JsonNode)new JsonObject
                {
                    ["class"] = pair.Key,
                    ["reopens"] = pair.Value,
                })]),
            });
    }

    /// <summary>The worst severity of <paramref name="findings"/>: how a class's send-back is graded, for the loop's blocking to read.</summary>
    private static string WorstOf(IReadOnlyList<JsonObject> findings)
    {
        var worst = Severities.Count - 1;

        foreach (var finding in findings)
        {
            var text = TextOf(finding["severity"]) ?? "nit";

            for (var at = 0; at < Severities.Count; at++)
            {
                if (string.Equals(Severities[at], text, StringComparison.Ordinal) && at < worst)
                {
                    worst = at;
                }
            }
        }

        return Severities[worst];
    }

    /// <summary>A value of a finding as text; null where it is none, or not text.</summary>
    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
