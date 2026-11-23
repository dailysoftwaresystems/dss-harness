using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Execution;

/// <summary>
/// The dispatch queue as the <c>queue</c> command says it: each class's capacity, its holders and its waiting units, by
/// tenant, and what the fair order rests on - each waiting unit's cost and the share it waits for - in text or in the
/// command's one JSON document, which every exit answers in.
/// </summary>
public static class QueueReports
{
    /// <summary>The command's name.</summary>
    public const string CommandName = "queue";

    /// <summary>
    /// The queue as the command answers it: the units holding and waiting, counted, as the line it ends on; then, as text,
    /// one block a class and a line a unit, or, where <paramref name="json"/>, the command's document whole.
    /// </summary>
    /// <param name="classes">The queue's classes, as the use case read them.</param>
    /// <param name="json">Whether the command was asked for its document.</param>
    public static CommandOutcome Render(IReadOnlyList<DispatchClass> classes, bool json)
    {
        ArgumentNullException.ThrowIfNull(classes);

        var summary = $"{ReportText.Counted(classes.Sum(@class => @class.Holders.Count), "unit")} holding and "
            + $"{ReportText.Counted(classes.Sum(@class => @class.Waiting.Count), "unit")} waiting, in "
            + ReportText.Counted(classes.Count, "class");

        if (json)
        {
            return CommandOutcome.Ok(summary) with { Data = [Document(HarnessExit.Success, summary, classes)], Quiet = true };
        }

        var lines = new List<string>();

        foreach (var @class in classes)
        {
            lines.Add($"{@class.Class}: {ReportText.Counted(@class.Capacity, "unit")} at once");

            foreach (var holder in @class.Holders)
            {
                lines.Add($"  holding (tenant '{holder.Tenant}'): {holder.Said}");
            }

            foreach (var waiting in @class.Waiting)
            {
                var waited = waiting.Waited is { } wait ? $", waited {LedgerReport.SaidDuration(wait)}" : string.Empty;

                lines.Add($"  waiting (tenant '{waiting.Tenant}', cost {Number(waiting.Cost)}, weight {Number(waiting.Weight)}, priority {waiting.Priority.ToString(CultureInfo.InvariantCulture)}{waited}): {waiting.Said}");
            }
        }

        return CommandOutcome.Ok(summary) with { Data = lines };
    }

    /// <summary>
    /// The command's document where it stopped before it read the queue - its command line refused, a failure it threw -
    /// in the one shape every exit answers in, holding no classes.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line the command ends on.</param>
    public static string Stopped(int exitCode, string summary) => Document(exitCode, summary, null);

    /// <summary>The queue as one JSON document, its shape held on every exit.</summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line the command ends on.</param>
    /// <param name="classes">The queue's classes, where it was read; null where the command stopped first.</param>
    private static string Document(int exitCode, string summary, IReadOnlyList<DispatchClass>? classes)
        => ReportJson.Stopped(
            exitCode,
            summary,
            new JsonObject
            {
                ["queue"] = classes is null ? null : new JsonArray([.. classes.Select(@class => (JsonNode)new JsonObject
                {
                    ["class"] = @class.Class,
                    ["capacity"] = @class.Capacity,
                    ["holding"] = new JsonArray([.. @class.Holders.Select(Unit)]),
                    ["waiting"] = new JsonArray([.. @class.Waiting.Select(Unit)]),
                })]),
            });

    /// <summary>One unit as the document says it.</summary>
    private static JsonObject Unit(DispatchUnit unit) => new()
    {
        ["tenant"] = unit.Tenant,
        ["weight"] = unit.Weight,
        ["priority"] = unit.Priority,
        ["cost"] = unit.Cost,
        ["waited"] = unit.Waited is { } wait ? LedgerReport.SaidDuration(wait) : null,
        ["unit"] = unit.Said,
    };

    /// <summary>A number as a line says it, the same on every machine.</summary>
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
