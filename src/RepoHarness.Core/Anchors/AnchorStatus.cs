using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Anchors;

/// <summary>The states an anchor can be in.</summary>
public enum AnchorState
{
    /// <summary>Live work that can be picked up now.</summary>
    Open,

    /// <summary>Live work waiting on a trigger.</summary>
    Gated,

    /// <summary>Live debt that existed before anyone wrote it down.</summary>
    Disclosed,

    /// <summary>Finished.</summary>
    Closed,
}

/// <summary>
/// How a status is spelled in a registry, and the one test that decides whether a row is closed.
/// </summary>
/// <remarks>
/// The Status cell is the only verdict that decides whether a row is closed: nothing is inferred from the prose
/// beside it, even where a registry states the verdict in its Trigger too. Such a Trigger is read for two things
/// alone: whether it states the verdict its Status does, and whether a closure is bookkeeping.
/// Each status is written glyph first and word second. The glyph is what the closed test reads,
/// because a test on the first character of the cell has one answer however the rest is phrased;
/// the word is there for the person reading the table.
/// </remarks>
public static class AnchorStatus
{
    /// <summary>The glyph that opens a closed Status cell.</summary>
    public const string ClosedMark = "✅";

    /// <summary>The glyph that opens a disclosed Status cell.</summary>
    public const string DisclosedMark = "🔵";

    /// <summary>
    /// The glyph that, after the closed mark, says a closure only repairs the mark of work done before the change that
    /// closes it: see <see cref="IsBookkeepingClosure"/>.
    /// </summary>
    public const string BookkeepingMark = "🧾";

    /// <summary>
    /// The variation selector that asks for a character's emoji presentation, which some keyboards type after an emoji:
    /// invisible, and part of the mark it follows.
    /// </summary>
    private const char EmojiPresentation = '\uFE0F';

    private static readonly (AnchorState State, string Word, string Cell)[] Spellings =
    [
        (AnchorState.Open, "open", "🟠 OPEN"),
        (AnchorState.Gated, "gated", "⏳ GATED"),
        (AnchorState.Disclosed, "disclosed", "🔵 DISCLOSED"),
        (AnchorState.Closed, "closed", ClosedMark + " CLOSED"),
    ];

    /// <summary>The words a command accepts for a status.</summary>
    public static IReadOnlyList<string> Words { get; } = [.. Spellings.Select(spelling => spelling.Word)];

    /// <summary>Every status exactly as a registry spells it.</summary>
    public static IReadOnlyList<string> Cells { get; } = [.. Spellings.Select(spelling => spelling.Cell)];

    /// <summary>The Status cell for <paramref name="state"/>.</summary>
    public static string Render(AnchorState state) => Spellings.First(spelling => spelling.State == state).Cell;

    /// <summary>
    /// Reads a status given as its word, in any case, or exactly as a registry spells it. Nothing
    /// else is accepted: a synonym is one more spelling every reader has to know.
    /// </summary>
    public static bool TryParse(string? value, out AnchorState state)
    {
        var text = (value ?? string.Empty).Trim();
        var word = text.TrimStart('*', '_', ' ').ToLowerInvariant();

        foreach (var spelling in Spellings)
        {
            if (word == spelling.Word || text == spelling.Cell)
            {
                state = spelling.State;
                return true;
            }
        }

        state = default;
        return false;
    }

    /// <summary>
    /// How a Status cell and a Trigger cell state different verdicts, where <paramref name="settings"/> hold a Trigger
    /// to its row's verdict (<see cref="AnchorSettings.TriggerCarriesVerdict"/>) - one reads closed, as
    /// <see cref="IsClosed"/> reads a cell, and the other does not - or <see langword="null"/> where they agree, or
    /// the Trigger carries no verdict.
    /// </summary>
    /// <param name="status">The Status cell.</param>
    /// <param name="trigger">The Trigger cell.</param>
    /// <param name="settings">The repository's anchor settings.</param>
    public static string? SplitVerdict(string status, string trigger, AnchorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(settings);

        var closed = IsClosed(status);

        if (!settings.TriggerCarriesVerdict || closed == IsClosed(trigger))
        {
            return null;
        }

        return closed
            ? $"the Status reads closed, and the Trigger does not open with the closed mark, {ClosedMark}"
            : $"the Trigger opens with the closed mark, {ClosedMark}, and the Status does not read closed";
    }

    /// <summary>Whether a Status cell reads closed: it opens with the closed mark, ignoring emphasis.</summary>
    /// <remarks>
    /// The closed spelling is defined and everything else is open, so a glyph nobody anticipated
    /// reads as open. A row wrongly read as open stays visible as work; a row wrongly read as
    /// closed disappears from every count.
    /// </remarks>
    public static bool IsClosed(string cell) => Lead(cell).StartsWith(ClosedMark, StringComparison.Ordinal);

    /// <summary>Whether a Status cell reads disclosed: it opens with the disclosed mark.</summary>
    public static bool IsDisclosed(string cell) => Lead(cell).StartsWith(DisclosedMark, StringComparison.Ordinal);

    /// <summary>
    /// Whether a Trigger says its row's closure is bookkeeping, where <paramref name="settings"/> hold a Trigger to its
    /// row's verdict (<see cref="AnchorSettings.TriggerCarriesVerdict"/>): it opens with the bookkeeping pair, the
    /// closed mark and then the bookkeeping mark, ignoring emphasis before them and whitespace between them. The mirror
    /// of the disclosed mark on the closed side: the work was done before the change that closes the row, which only
    /// repairs its mark. Where a Trigger carries no verdict, nothing is read from it.
    /// </summary>
    /// <remarks>
    /// Leading position only, as for every mark: a pair anywhere in the prose could be claimed by a row that merely
    /// mentions bookkeeping. The closed mark comes first, so a Trigger opening with the pair reads closed as
    /// <see cref="IsClosed"/> reads it, and it is the closed mark with the invisible selector a keyboard may type after
    /// it too (<see cref="EmojiPresentation"/>): read as anything else, the pair its writer meant would go uncounted, and
    /// the closure credited.
    /// </remarks>
    /// <param name="trigger">The Trigger cell.</param>
    /// <param name="settings">The repository's anchor settings.</param>
    public static bool IsBookkeepingClosure(string trigger, AnchorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(settings);

        return settings.TriggerCarriesVerdict
            && IsClosed(trigger)
            && Lead(trigger)[ClosedMark.Length..].TrimStart(EmojiPresentation).TrimStart().StartsWith(BookkeepingMark, StringComparison.Ordinal);
    }

    /// <summary>Whether a Status cell is exactly one of the registry's spellings.</summary>
    public static bool IsCanonical(string cell) => Cells.Contains(cell.Trim(), StringComparer.Ordinal);

    private static string Lead(string cell) => cell.TrimStart().TrimStart('*', '_', ' ');
}
