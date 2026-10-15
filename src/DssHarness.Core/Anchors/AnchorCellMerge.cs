using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

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

    /// <summary>Only the agent changed it: the agent's is written.</summary>
    Agents,

    /// <summary>Only the registries changed it: theirs is kept, no consent asked, and said.</summary>
    Kept,

    /// <summary>Both changed it, to the same: left as it is.</summary>
    Alike,

    /// <summary>Both changed it, each otherwise: the agent's is written over the registries' only where it is named.</summary>
    Both,
}

/// <summary>One cell weighed three ways, with its three versions as a row stores them.</summary>
/// <param name="Cell">One of <see cref="AnchorCellNames.All"/>.</param>
/// <param name="Resolution">How it comes out.</param>
/// <param name="Earlier">What the agent declared at its earlier fold; null for a priority it declared none of.</param>
/// <param name="Registries">What the registries hold.</param>
/// <param name="Agent">What the agent declares now; null for a priority it declares none of.</param>
public sealed record AnchorCellWeighed(string Cell, AnchorCellResolution Resolution, string? Earlier, string? Registries, string? Agent);

/// <summary>
/// A row an earlier fold of the agent applied, weighed cell by cell against what the registries took since: each of its
/// five cells three ways, what that fold recorded as the agent's declaration the ancestor - the cells the registries changed
/// and the agent did not kept, those only the agent changed written, those both changed alike left, and those both changed
/// otherwise written over the registries' only where it is named. Pure: it reads and writes nothing.
/// </summary>
/// <remarks>
/// The ancestor is the declaration, never what the registries held after that fold: where a cell kept the registries'
/// text, the agent's file still holds the earlier text, and taking the registries' as the ancestor would read that
/// unchanged file as the agent's change and write it over theirs. A declaration with no priority declares none: never the
/// agent's change; an earlier one with none gives nothing to say the registries changed it from.
/// </remarks>
public static class AnchorCellMerge
{
    /// <summary>Each of the five cells of a row weighed three ways.</summary>
    /// <param name="declared">What the agent declares now.</param>
    /// <param name="earlier">What it declared at its earlier fold.</param>
    /// <param name="existing">What the registries hold.</param>
    public static IReadOnlyList<AnchorCellWeighed> Of(AnchorRowCells declared, AnchorRowCells earlier, AnchorRowCells existing)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(existing);

        return [.. AnchorCellNames.All.Select(cell =>
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
    }

    /// <summary>
    /// The row the registries are to hold: the agent's cell where it changed it - alone, or where both did otherwise, which
    /// the command that applies it refuses until named - and the registries' everywhere else, as they hold it. A priority
    /// not the agent's is declared as none, so the registries' stands.
    /// </summary>
    /// <param name="declared">What the agent declares now.</param>
    /// <param name="existing">The row the registries hold.</param>
    /// <param name="weighed">Its cells weighed (<see cref="Of"/>).</param>
    public static AnchorRowDeclaration Effective(AnchorRowDeclaration declared, AnchorRow existing, IReadOnlyList<AnchorCellWeighed> weighed)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(weighed);

        bool Agents(string cell) => weighed.First(one => one.Cell == cell).Resolution is AnchorCellResolution.Agents or AnchorCellResolution.Both;

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
}
