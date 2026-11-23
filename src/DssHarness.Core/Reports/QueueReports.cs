using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Reports;

/// <summary>
/// The dispatch queue as a command says it: each class's capacity, its holders and its waiting units, by tenant, and
/// what the fair order rests on - each waiting unit's cost and the share it waits for.
/// </summary>
public static class QueueReports
{
    /// <summary>The command's name.</summary>
    public const string CommandName = "queue";

    /// <summary>The queue as text: one block a class, a line a unit.</summary>
    /// <param name="classes">The queue's classes.</param>
    public static CommandOutcome Render(IReadOnlyList<DispatchClass> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);

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

        return new CommandOutcome(0, string.Join("\n", lines));
    }

    /// <summary>The queue as one JSON document, its shape held on every exit.</summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line the command ends on.</param>
    /// <param name="classes">The queue's classes, where it was read.</param>
    public static string Document(int exitCode, string summary, IReadOnlyList<DispatchClass>? classes)
        => ReportJson.Stopped(
            exitCode,
            summary,
            new JsonObject
            {
                ["queue"] = classes is null ? null : new JsonArray([.. classes.Select(@class => (JsonNode)new JsonObject
                {
                    ["class"] = @class.Class,
                    ["capacity"] = @class.Capacity,
                    ["holding"] = new JsonArray([.. @class.Holders.Select(unit => Unit(unit, waiting: false))]),
                    ["waiting"] = new JsonArray([.. @class.Waiting.Select(unit => Unit(unit, waiting: true))]),
                })]),
            });

    /// <summary>One unit as the document says it.</summary>
    private static JsonObject Unit(DispatchUnit unit, bool waiting) => new()
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
