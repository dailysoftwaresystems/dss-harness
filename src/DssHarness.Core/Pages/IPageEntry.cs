namespace DssHarness.Core.Pages;

/// <summary>
/// One entry of an answer the pages hold: a line of it, and how much of its section's page it counts. An entry is never
/// cut: one a page has room for is given whole, the next page holding what follows it; one wider than a whole page is
/// given in pieces, a page each, each marked as going on (<see cref="Paging"/>).
/// </summary>
public interface IPageEntry
{
    /// <summary>The entry's text, one line of the answer.</summary>
    string Text { get; }

    /// <summary>
    /// How much of its section's size the entry counts: one for a result or a line; none for a line that only says
    /// something - a block's head, a note - which rides on whatever page holds it.
    /// </summary>
    int Counts { get; }

    /// <summary>
    /// The block the entry belongs to, by what tells it apart from another of the same name; null where it is of none. Two
    /// entries are of one block where their <see cref="BlockName"/> and their block are the same.
    /// </summary>
    string? Block { get; }

    /// <summary>What a page that ends inside the entry's block says goes on; null where the entry is of no block.</summary>
    string? BlockName { get; }

    /// <summary>What is wrong with the entry; <see langword="null"/> where nothing is: by default the rules every entry keeps (<see cref="ProblemOf"/>).</summary>
    string? Problem() => ProblemOf(this);

    /// <summary>
    /// What is wrong with <paramref name="entry"/> by the rules every entry of a page keeps: one line, never empty - a page
    /// ends its text at its last line, so an empty one there would be lost - counting no fewer than none.
    /// </summary>
    /// <param name="entry">The entry.</param>
    static string? ProblemOf(IPageEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return string.IsNullOrEmpty(entry.Text) || entry.Text.Contains('\n') || entry.Text.Contains('\r') ? "an entry is one line, and never empty"
            : entry.Counts < 0 ? "an entry counts no fewer than no lines"
            : null;
    }
}
