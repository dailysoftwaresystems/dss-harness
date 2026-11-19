using DssHarness.Core.Pages;

namespace DssHarness.Core.Codebase;

/// <summary>
/// One entry of an answer of the graph the pages hold: a line of it, and how much of its section's page it counts. An
/// entry is never cut: one a page has room for is given whole, the next page holding what follows it; one wider than a
/// whole page is given in pieces, a page each, each marked as going on (<see cref="Paging"/>). Its block is its node's,
/// told by <see cref="Node"/> and said by <see cref="GoesOn"/>; its JSON - what a kept result holds - is its members alone.
/// </summary>
/// <param name="Text">The entry's text, one line of the answer.</param>
/// <param name="Lines">
/// How much of its section's size the entry counts: one for a result, a node, a hit or a served line; none for a line
/// that only says something - a block's head, a gap a body left out, a note - which rides on whatever page holds it.
/// </param>
/// <param name="GoesOn">
/// Where the entry is one of a node's served lines, or the head of its block, that node's qualified name: what a page
/// that ends inside the block says goes on. The block itself is told by <paramref name="Node"/> as well, so two nodes of
/// one qualified name - two overloads - are two blocks, and a page that ends where another node begins says nothing.
/// </param>
/// <param name="Node">The node whose lines this entry gave, or whose block it heads, by its id; what a page's served nodes are read from.</param>
/// <param name="Hit">Whether the entry is a line a text search added where the graph was not sure.</param>
/// <param name="File">
/// The file, relative to the tree, whose lines the entry served, where it served any: what a page counts as viewed - the
/// files whose lines that page gave, and no other.
/// </param>
public sealed record GraphEntry(string Text, int Lines = 1, string? GoesOn = null, string? Node = null, bool Hit = false, string? File = null) : IPageEntry
{
    /// <inheritdoc/>
    int IPageEntry.Counts => Lines;

    /// <inheritdoc/>
    string? IPageEntry.Block => Node;

    /// <inheritdoc/>
    string? IPageEntry.BlockName => GoesOn;

    /// <summary>What is wrong with the entry; <see langword="null"/> where nothing is: the rules every entry keeps, then a served file's.</summary>
    public string? Problem()
        => IPageEntry.ProblemOf(this)
            ?? (File is { Length: 0 } ? "an entry that served a file's lines names the file" : null);

    /// <summary>Whether <paramref name="other"/> is of the same node's block as this entry: the same node, said by the same name.</summary>
    /// <param name="other">The entry beside it.</param>
    public bool SameBlock(GraphEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Paging.SameBlock(this, other);
    }
}

/// <summary>
/// The graph's use of the one owner of an answer's pages (<see cref="Paging"/>): no answer of the graph is cut, but each is
/// pages of whole entries, with the page said, any page asked by number, and the totals exact, every page within the
/// characters an answer holds. Every operation of the graph tool answers through it, and the text fallback's hits - each
/// line whole - and a body's served lines are entries as any other; an entry's later piece counts no line and is no hit,
/// so the totals and the hits are counted once.
/// </summary>
public static class GraphPages
{
    /// <summary>The characters held back from a page's entries for what surrounds them (<see cref="PageRoom.Reserved"/>).</summary>
    public const int ReservedCharacters = PageRoom.Reserved;

    /// <summary>The least room a page gives its entries, whatever its leading lines take (<see cref="PageRoom.Least"/>).</summary>
    public const int LeastRoom = PageRoom.Least;

    /// <summary>What a page made again because the graph the view answers from changed since page 1 says.</summary>
    public const string Recounted = "the graph changed since page 1: pages recounted";

    /// <summary>
    /// What a page past the first says where no result of page 1 is kept to read it from - none was kept, or what was kept
    /// does not read, and <paramref name="why"/> says which - so it is counted on the graph as it stands, and may not be a
    /// page of the result page 1 was.
    /// </summary>
    /// <param name="why">Why what was kept does not read; null where nothing was kept.</param>
    public static string NotKept(string? why)
        => why is null
            ? "page 1 of this ask is not kept: pages counted on the graph as it stands"
            : $"page 1 of this ask is not kept ({why}): pages counted on the graph as it stands";

    /// <inheritdoc cref="Paging.Unkept"/>
    public static string Unkept(string why) => Paging.Unkept(why);

    /// <summary>
    /// The answer's <paramref name="page"/>, from its sections: the page holds whole entries, each section up to its
    /// size and the whole within <paramref name="mostCharacters"/> less <see cref="ReservedCharacters"/> and the leading
    /// lines - an entry wider than that given in pieces, a page each; the notes the answer ends with follow its last
    /// entries, held within the same room, on as many pages as they take; a page of one holds the whole answer and no
    /// header; a page past the last is refused with the answer's range.
    /// </summary>
    /// <param name="sections">The answer's sections, in their order; each page holds of every section what its size leaves.</param>
    /// <param name="leading">The lines the answer begins with, said on every page.</param>
    /// <param name="trailing">The notes the answer ends with, each given whole - or in pieces where it is wider than a page - after every entry.</param>
    /// <param name="page">The page asked, from 1.</param>
    /// <param name="asked">How the answer names what was asked, for a page past the last to say.</param>
    /// <param name="mostCharacters">The most characters an answer may hold.</param>
    /// <param name="note">What the header says after its counts, and a refusal after its range (<see cref="PageAnswer{TEntry}.Note"/>).</param>
    public static PageAnswer<GraphEntry> Assemble(
        IReadOnlyList<PageSection<GraphEntry>> sections,
        IReadOnlyList<string> leading,
        IReadOnlyList<string> trailing,
        int page,
        string asked,
        int mostCharacters,
        string? note = null)
        => Lay(sections, leading, trailing, mostCharacters).Page(page, asked, note);

    /// <summary>
    /// The answer's pages, filled once from its sections as <see cref="Assemble"/> fills them (<see cref="Paging.Lay"/>):
    /// every page of it is given from what this fill found, so a later page is never filled again.
    /// </summary>
    /// <param name="sections">The answer's sections, in their order.</param>
    /// <param name="leading">The lines the answer begins with, said on every page.</param>
    /// <param name="trailing">The notes the answer ends with.</param>
    /// <param name="mostCharacters">The most characters an answer may hold.</param>
    public static PageLayout<GraphEntry> Lay(IReadOnlyList<PageSection<GraphEntry>> sections, IReadOnlyList<string> leading, IReadOnlyList<string> trailing, int mostCharacters)
    {
        ArgumentNullException.ThrowIfNull(leading);

        return Paging.Lay(sections, leading, trailing, PageRoom.For(mostCharacters, leading), Piece, line => new GraphEntry(line, Lines: 0));
    }

    /// <summary>
    /// The entry that carries one piece of <paramref name="entry"/>: the piece's text, counting the entry's lines and its
    /// hit on its first piece alone, so the totals and the hits are counted once.
    /// </summary>
    private static GraphEntry Piece(GraphEntry entry, string text, bool first)
        => entry with { Text = text, Lines = first ? entry.Lines : 0, Hit = first && entry.Hit };
}
