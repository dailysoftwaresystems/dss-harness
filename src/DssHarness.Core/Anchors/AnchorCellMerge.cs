using DssHarness.Core.Configuration;
using DssHarness.Core.Results;

namespace DssHarness.Core.Anchors;

/// <summary>
/// A row's five cells as a registry stores them - a priority normalised, a status as it is written, each text on one line
/// with its pipes plain, as the parser gives a stored cell back: what every comparison of a declared cell with a stored one
/// compares, so how a declared cell compares with a stored one is one rule.
/// </summary>
/// <param name="Priority">The priority band; null where a declaration declares none.</param>
/// <param name="Status">The status cell.</param>
/// <param name="Trigger">The trigger.</param>
/// <param name="ClosingWork">The closing work.</param>
/// <param name="CrossRefs">The cross-refs.</param>
public sealed record AnchorRowCells(string? Priority, string Status, string Trigger, string ClosingWork, string CrossRefs)
{
    /// <summary>The cells <paramref name="row"/> holds.</summary>
    /// <param name="row">A stored row.</param>
    public static AnchorRowCells Of(AnchorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new(row.Priority, row.Status, row.Trigger, row.ClosingWork, row.CrossRefs);
    }

    /// <summary>
    /// The cells <paramref name="declared"/> declares, as a row would store them; one whose status or priority reads as none
    /// is refused, in the words every write of a row uses.
    /// </summary>
    /// <param name="declared">A declared row.</param>
    /// <exception cref="HarnessException">Its status or its priority is not one (<see cref="HarnessExit.UsageError"/>).</exception>
    public static AnchorRowCells Of(AnchorRowDeclaration declared)
    {
        ArgumentNullException.ThrowIfNull(declared);

        return new(
            declared.Priority is null ? null : AnchorRegistryService.RequirePriority(declared.Priority),
            AnchorStatus.Render(AnchorRegistryService.RequireStatus(declared.Status)),
            AnchorCells.Flatten(declared.Trigger),
            AnchorCells.Flatten(declared.ClosingWork),
            AnchorCells.Flatten(declared.CrossRefs));
    }

    /// <summary>The cell <paramref name="cell"/> names.</summary>
    /// <param name="cell">One of <see cref="AnchorCellNames.All"/>.</param>
    public string? this[string cell] => cell switch
    {
        AnchorCellNames.Priority => Priority,
        AnchorCellNames.Status => Status,
        AnchorCellNames.Trigger => Trigger,
        AnchorCellNames.Closing => ClosingWork,
        AnchorCellNames.CrossRefs => CrossRefs,
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, "Not a cell of an anchor's row."),
    };
}

/// <summary>How one cell of a row an earlier fold of the agent applied comes out, weighed three ways.</summary>
public enum AnchorCellResolution
{
    /// <summary>Neither the registries nor the agent changed it since: left as it is.</summary>
    Unchanged,

    /// <summary>
    /// Only the agent changed it: the agent's is written - unless it carries the row's verdict and writing it beside the
    /// registries' change of the other cell that carries it would have the row state two verdicts, which is <see cref="Both"/>.
    /// </summary>
    Agents,

    /// <summary>
    /// Only the registries changed it: theirs is kept, no consent asked, and said - unless it carries the row's verdict and
    /// keeping it beside the agent's change of the other cell that carries it would have the row state two verdicts, which
    /// is <see cref="Both"/>.
    /// </summary>
    Kept,

    /// <summary>Both changed it, to the same: left as it is.</summary>
    Alike,

    /// <summary>
    /// Both changed it, each otherwise - or it carries the row's verdict, of which the registries changed one cell and the
    /// agent the other, so that the row taken a cell from each would state two verdicts (<see cref="AnchorCellWeighed.Verdict"/>):
    /// the agent's is written over the registries' only where it is named.
    /// </summary>
    Both,
}

/// <summary>One cell weighed three ways, with its three versions as a row stores them.</summary>
/// <param name="Cell">One of <see cref="AnchorCellNames.All"/>.</param>
/// <param name="Resolution">How it comes out.</param>
/// <param name="Earlier">What the agent declared at its earlier fold; null for a priority it declared none of.</param>
/// <param name="Registries">What the registries hold.</param>
/// <param name="Agent">What the agent declares now; null for a priority it declares none of.</param>
public sealed record AnchorCellWeighed(string Cell, AnchorCellResolution Resolution, string? Earlier, string? Registries, string? Agent)
{
    /// <summary>
    /// Where the cell is <see cref="AnchorCellResolution.Both"/> because it carries the row's verdict - weighed on its own it
    /// would have been kept or the agent's, and the row taken a cell from each side would state two verdicts - the two it
    /// would state, as <see cref="AnchorStatus.SplitVerdict"/> says them; null for a cell weighed on its own.
    /// </summary>
    public string? Verdict { get; init; }
}

/// <summary>
/// A row an earlier fold of the agent applied, weighed cell by cell against what the registries took since: each of its
/// five cells three ways, what that fold recorded as the agent's declaration the ancestor - the cells the registries changed
/// and the agent did not kept, those only the agent changed written, those both changed alike left, and those both changed
/// otherwise written over the registries' only where it is named - and the two cells that carry the row's verdict weighed as
/// the one decision they state. Pure: it reads and writes nothing.
/// </summary>
/// <remarks>
/// <para>
/// The ancestor is the declaration, never what the registries held after that fold: where a cell kept the registries'
/// text, the agent's file still holds the earlier text, and taking the registries' as the ancestor would read that
/// unchanged file as the agent's change and write it over theirs. A declaration with no priority declares none: never the
/// agent's change; an earlier one with none gives nothing to say the registries changed it from.
/// </para>
/// <para>
/// The verdict is one decision. Where a Trigger carries its row's verdict (<see cref="AnchorSettings.TriggerCarriesVerdict"/>),
/// the status and the trigger state it together (<see cref="VerdictCells"/>), and are never taken one from each side where
/// that would state two: where the registries changed one of them and the agent the other - one kept, the other the agent's
/// alone or both changed - and the row merged cell by cell would state two verdicts though the agent's declaration states
/// one, both are <see cref="AnchorCellResolution.Both"/>. Each is then written only where it is named, so the row states
/// the agent's one verdict once both are, and nothing of it is written while either is not: the registries' verdict
/// stands, never a row stating two. A row the registries reopened that the agent closes again with its trigger, its status
/// file closed as it was, is the case this is for, and its mirror, a row the registries closed that the agent keeps open.
/// Where the agent's declaration states two verdicts itself, there is no one decision to take: its cells are weighed here on
/// their own, and the command that applies it refuses the row before weighing it at all, whatever the row taken one from
/// each side would state. Where a Trigger carries no verdict, the status alone states
/// it and is weighed as any cell is: a status the agent's file holds as it declared it at its earlier fold is no decision
/// of the agent's, so the registries' change of it is kept, whatever the agent's trigger says.
/// </para>
/// </remarks>
public static class AnchorCellMerge
{
    /// <summary>
    /// The cells that state a row's verdict where its Trigger carries one too (<see cref="AnchorSettings.TriggerCarriesVerdict"/>):
    /// the status and the trigger, weighed as one decision.
    /// </summary>
    public static IReadOnlyList<string> VerdictCells { get; } = [AnchorCellNames.Status, AnchorCellNames.Trigger];

    /// <summary>The other of the two <see cref="VerdictCells"/>: the trigger of the status, the status of the trigger.</summary>
    /// <param name="cell">One of <see cref="VerdictCells"/>.</param>
    public static string OtherVerdictCell(string cell) => cell switch
    {
        AnchorCellNames.Status => AnchorCellNames.Trigger,
        AnchorCellNames.Trigger => AnchorCellNames.Status,
        _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, "Not a cell that carries a row's verdict."),
    };

    /// <summary>Each of the five cells of a row weighed three ways, the two that carry its verdict as one decision.</summary>
    /// <param name="declared">What the agent declares now.</param>
    /// <param name="earlier">What it declared at its earlier fold.</param>
    /// <param name="existing">What the registries hold.</param>
    /// <param name="settings">The repository's anchor settings, which say whether a Trigger carries its row's verdict.</param>
    public static IReadOnlyList<AnchorCellWeighed> Of(AnchorRowCells declared, AnchorRowCells earlier, AnchorRowCells existing, AnchorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(settings);

        List<AnchorCellWeighed> weighed = [.. AnchorCellNames.All.Select(cell =>
        {
            var (before, theirs, ours) = (earlier[cell], existing[cell], declared[cell]);
            var registries = before is not null && !string.Equals(theirs, before, StringComparison.Ordinal);
            var agent = ours is not null && !string.Equals(ours, before, StringComparison.Ordinal);

            var resolution = (registries, agent) switch
            {
                (false, false) => AnchorCellResolution.Unchanged,
                (false, true) => AnchorCellResolution.Agents,
                (true, false) => AnchorCellResolution.Kept,
                _ when string.Equals(ours, theirs, StringComparison.Ordinal) => AnchorCellResolution.Alike,
                _ => AnchorCellResolution.Both,
            };

            return new AnchorCellWeighed(cell, resolution, before, theirs, ours);
        })];

        return OneVerdict(weighed, declared, settings);
    }

    /// <summary>
    /// The row the registries are to hold: the agent's cell where it changed it - alone, or where both did otherwise, which
    /// the command that applies it refuses until named - and where it carries the row's verdict weighed as both changed,
    /// named the same way; the registries' everywhere else, as they hold it. A priority not the agent's is declared as none,
    /// so the registries' stands.
    /// </summary>
    /// <param name="declared">What the agent declares now.</param>
    /// <param name="existing">The row the registries hold.</param>
    /// <param name="weighed">Its cells weighed (<see cref="Of"/>).</param>
    public static AnchorRowDeclaration Effective(AnchorRowDeclaration declared, AnchorRow existing, IReadOnlyList<AnchorCellWeighed> weighed)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(weighed);

        bool Agents(string cell) => TakesTheAgents(weighed.First(one => one.Cell == cell).Resolution);

        return new AnchorRowDeclaration(
            declared.Id,
            Agents(AnchorCellNames.Status) ? declared.Status : existing.Status,
            Agents(AnchorCellNames.Trigger) ? declared.Trigger : existing.Trigger,
            Agents(AnchorCellNames.Closing) ? declared.ClosingWork : existing.ClosingWork,
            Agents(AnchorCellNames.CrossRefs) ? declared.CrossRefs : existing.CrossRefs)
        {
            Priority = Agents(AnchorCellNames.Priority) ? declared.Priority : null,
        };
    }

    /// <summary>Whether the row the registries are to hold takes a cell so resolved from the agent: one it changed, alone or with them.</summary>
    private static bool TakesTheAgents(AnchorCellResolution resolution) => resolution is AnchorCellResolution.Agents or AnchorCellResolution.Both;

    /// <summary>
    /// The cells weighed, with the two that carry the row's verdict made <see cref="AnchorCellResolution.Both"/> where the
    /// registries changed one and the agent the other, and the row merged cell by cell would state two verdicts though the
    /// agent's declaration states one; as weighed otherwise - always where a Trigger carries no verdict, since no row then
    /// states two.
    /// </summary>
    private static List<AnchorCellWeighed> OneVerdict(List<AnchorCellWeighed> weighed, AnchorRowCells declared, AnchorSettings settings)
    {
        var (status, trigger) = (weighed.Single(cell => cell.Cell == AnchorCellNames.Status), weighed.Single(cell => cell.Cell == AnchorCellNames.Trigger));
        string Merged(AnchorCellWeighed cell) => TakesTheAgents(cell.Resolution) ? cell.Agent! : cell.Registries!;

        var oneEach = (status.Resolution, trigger.Resolution) is (AnchorCellResolution.Kept, AnchorCellResolution.Agents or AnchorCellResolution.Both)
            or (AnchorCellResolution.Agents or AnchorCellResolution.Both, AnchorCellResolution.Kept);

        if (!oneEach
            || AnchorStatus.SplitVerdict(declared.Status, declared.Trigger, settings) is not null
            || AnchorStatus.SplitVerdict(Merged(status), Merged(trigger), settings) is not { } split)
        {
            return weighed;
        }

        return [.. weighed.Select(cell => VerdictCells.Contains(cell.Cell, StringComparer.Ordinal) && cell.Resolution != AnchorCellResolution.Both
            ? cell with { Resolution = AnchorCellResolution.Both, Verdict = split }
            : cell)];
    }
}
