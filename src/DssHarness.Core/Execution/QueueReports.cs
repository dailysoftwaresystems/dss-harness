using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Execution;

/// <summary>
/// The dispatch queue as the <c>queue</c> command says it: each class's capacity, its holders and its waiting units, by
/// tenant, the waiting ones in the order the queue serves them, and what that order rests on - each waiting unit's cost,
/// the share its tenant is credited each round and its tenant's deficit - in text or in the command's one JSON document,
/// which every exit answers in.
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
            var capacity = @class.Capacity is { } count
                ? $"{ReportText.Counted(count, "unit")} at once"
                : "no section declares admission, so every unit at once";

            lines.Add($"{NameOf(@class.Class)}: {capacity}{(@class.Asked ? string.Empty : ", asked by no unit yet")}");

            foreach (var holder in @class.Holders)
            {
                lines.Add($"  holding ({Weighed(holder)}): {holder.Said}");
            }

            foreach (var (waiting, at) in @class.Waiting.Select((waiting, at) => (waiting, at + 1)))
            {
                var waited = waiting.Waited is { } wait ? $", waited {LedgerReport.SaidDuration(wait)}" : string.Empty;

                lines.Add($"  waiting {at.ToString(CultureInfo.InvariantCulture)} ({Weighed(waiting)}{waited}): {waiting.Said}");
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

    /// <summary>A class as the command names it: <c>heavy</c>, <c>process</c>, <c>agent</c>.</summary>
    /// <param name="name">The class.</param>
    public static string NameOf(DispatchClassName name) => JsonNamingPolicy.CamelCase.ConvertName(name.ToString());

    /// <summary>What a unit is weighed by, as its line says it: its tenant and what the fair order rests on, or that an older build asked it.</summary>
    private static string Weighed(DispatchUnit unit)
    {
        if (unit.Queued is not { } queued)
        {
            return "asked by an older build, which admits it in the order the line was asked in";
        }

        var said = $"tenant '{queued.Tenant}', cost {Number(queued.Cost)}";

        if (unit.Share is { } share)
        {
            said += $", share {(double.IsFinite(share) ? Number(share) : "past any cost")}, deficit {Number(unit.Deficit ?? 0)}";
        }

        return said + $", weight {Number(queued.Weight)}, priority {queued.Priority.ToString(CultureInfo.InvariantCulture)}";
    }

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
                    ["class"] = NameOf(@class.Class),
                    ["capacity"] = @class.Capacity,
                    ["asked"] = @class.Asked,
                    ["holding"] = new JsonArray([.. @class.Holders.Select(Unit)]),
                    ["waiting"] = new JsonArray([.. @class.Waiting.Select(Unit)]),
                })]),
            });

    /// <summary>
    /// One unit as the document says it: its entry's own fields, and what the fair order weighs it by - its share null where
    /// it holds, an older build asked it, or the share is past any number a document holds: an aging bound of no minutes,
    /// or more doublings than a number holds, under which a debt is gone in one round.
    /// </summary>
    private static JsonObject Unit(DispatchUnit unit) => new()
    {
        ["tenant"] = unit.Queued?.Tenant,
        ["olderBuild"] = unit.Queued is null,
        ["weight"] = unit.Queued?.Weight,
        ["priority"] = unit.Queued?.Priority,
        ["cost"] = unit.Queued?.Cost,
        ["share"] = unit.Share is { } share && double.IsFinite(share) ? share : null,
        ["deficit"] = unit.Deficit,
        ["heldSinceUtc"] = unit.Queued?.HeldSinceUtc,
        ["waitedSeconds"] = unit.Waited is { } wait ? LedgerReport.Seconds(wait) : null,
        ["askedUtc"] = unit.Entry.AskedUtc,
        ["command"] = unit.Entry.Command,
        ["leg"] = unit.Entry.Leg,
        ["host"] = unit.Entry.Host,
        ["tree"] = unit.Entry.Tree,
        ["variant"] = unit.Entry.Variant,
        ["processId"] = unit.Entry.ProcessId,
        ["runId"] = unit.Entry.RunId,
        ["unit"] = unit.Said,
    };

    /// <summary>A number as a line says it, the same on every machine.</summary>
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
