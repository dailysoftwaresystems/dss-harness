using DssHarness.Core.Pages;

namespace DssHarness.Tests;

/// <summary>
/// The one owner of an answer's pages over any entry: whole entries a page while the room lasts, every one reachable by its
/// page, asked in any order; an entry wider than a page given in pieces, each marked where it goes on and went on, the
/// page that ends inside one saying so; a result's block's head given again only where a page's room holds it; and a page
/// past the last refused with the answer's range.
/// </summary>
public sealed class PagesTests
{
    /// <summary>The room a page gives its entries in these answers.</summary>
    private const int Room = 1000;

    /// <summary>
    /// An answer of a thousand entries is pages of whole entries - each page as many as its room holds, the next entry
    /// beginning the next page - and every entry is on exactly one page, whatever page is asked first: the pages read in
    /// order give the answer whole, each entry the one the answer holds, never a piece of it.
    /// </summary>
    [Fact]
    public void ATextAnswerOfAThousandEntries_IsPagesOfWholeEntries_EveryOneReachable()
    {
        var entries = Enumerable.Range(0, 1000).Select(at => new Line($"entry {at:D4} " + new string('e', at % 37))).ToList();
        var layout = Paging.Lay([new PageSection<Line>(null, entries, int.MaxValue)], [], [], Room, Piece, Note);

        // Whole entries while the room lasts, each costing its characters and its line's end.
        var expected = new List<List<Line>> { new() };
        var left = Room;

        foreach (var entry in entries)
        {
            if (entry.Text.Length + 1 > left)
            {
                expected.Add([]);
                left = Room;
            }

            expected[^1].Add(entry);
            left -= entry.Text.Length + 1;
        }

        Assert.Equal((expected.Count, 1000L), (layout.Pages, layout.Total));

        // Asked last page first: a page is the layout's own, whichever was asked before it.
        foreach (var page in Enumerable.Range(1, layout.Pages).Reverse())
        {
            var answer = layout.Page(page, "find entries");
            var header = page == layout.Pages
                ? $"page {page} of {layout.Pages} · the last"
                : $"page {page} of {layout.Pages} · 1000 results · ask again with page: {page + 1} for the next";

            Assert.Null(answer.Refusal);
            Assert.Equal(header + "\n" + string.Join('\n', expected[page - 1].Select(entry => entry.Text)), answer.Text);
            Assert.Equal(expected[page - 1].Count, answer.Entries.Count);
            Assert.All(answer.Entries.Zip(expected[page - 1]), pair => Assert.Same(pair.Second, pair.First));
        }

        Assert.Equal(entries, Enumerable.Range(1, layout.Pages).SelectMany(page => layout.Page(page, "find entries").Entries));
    }

    /// <summary>
    /// An entry wider than a page is given in pieces, a page each: the first holding as much as the room holds with its
    /// mark, each later one beginning with the mark it went on from, the page that ends inside it saying the line goes on,
    /// and the last piece riding with the entries after it - the entry counted on its first piece alone.
    /// </summary>
    [Fact]
    public void AnEntryWiderThanAPage_IsGivenInPieces_EachMarked()
    {
        var wide = string.Concat(Enumerable.Range(0, 1000).Select(at => (char)('a' + (at % 26))));
        List<Line> entries = [new("before"), new(wide), new("after")];
        var layout = Paging.Lay([new PageSection<Line>(null, entries, int.MaxValue)], [], [], PageRoom.Least, Piece, Note);

        var pages = Enumerable.Range(1, layout.Pages).Select(page => layout.Page(page, "body wide")).ToList();

        Assert.Equal((5, 3L), (layout.Pages, layout.Total));
        Assert.Equal(
            [
                "page 1 of 5 · 3 results · ask again with page: 2 for the next\nbefore",
                $"page 2 of 5 · 3 results · ask again with page: 3 for the next\n{wide[..254]}…\n… the line goes on",
                $"page 3 of 5 · 3 results · ask again with page: 4 for the next\n…{wide[254..507]}…\n… the line goes on",
                $"page 4 of 5 · 3 results · ask again with page: 5 for the next\n…{wide[507..760]}…\n… the line goes on",
                $"page 5 of 5 · the last\n…{wide[760..]}\nafter",
            ],
            pages.Select(page => page.Text));

        // Counted on its first piece alone: the pieces count the entry once, and the answer's entries once each.
        Assert.Equal([1, 1, 0, 0, 0, 1], pages.SelectMany(page => page.Entries).Select(entry => entry.Counts));
    }

    /// <summary>
    /// A page past the last is refused with the answer's range - one page said as one - and with the note the page asked
    /// was given; nothing of the answer is on it.
    /// </summary>
    [Fact]
    public void APagePastTheLast_IsRefusedWithTheRange()
    {
        var entries = Enumerable.Range(0, 30).Select(at => new Line($"line {at:D2} " + new string('l', 90))).ToList();
        var layout = Paging.Lay([new PageSection<Line>(null, entries, int.MaxValue)], [], [], Room, Piece, Note);
        var single = Paging.Lay([new PageSection<Line>(null, [new("one")], int.MaxValue)], [], [], Room, Piece, Note);

        var refused = layout.Page(4, "read_result toolu_1");
        var noted = layout.Page(9, "read_result toolu_1", "the file changed");
        var alone = single.Page(2, "read_result toolu_2");

        Assert.Equal(("page 4 of 'read_result toolu_1': it has 3 pages (1 to 3)", Paging.PastTheLast, 4, 3, 30L), (refused.Text, refused.Refusal, refused.Page, refused.Pages, refused.Total));
        Assert.Empty(refused.Sections);
        Assert.Equal("page 9 of 'read_result toolu_1': it has 3 pages (1 to 3) · the file changed", noted.Text);
        Assert.Equal(("page 2 of 'read_result toolu_2': it has 1 page (1 to 1)", Paging.PastTheLast), (alone.Text, alone.Refusal));
        Assert.Null(layout.Page(3, "read_result toolu_1").Refusal);
    }

    /// <summary>
    /// A page that begins inside a result's block gives the block's head again only where its room holds it beside the
    /// least piece of the entry it goes on with - the one rule a text's pages give a head again by too: a head whose saying
    /// again leaves room for the entry after it is given, and one that leaves less than the least piece is not, the page
    /// beginning with the block's next line - so no page passes its room.
    /// </summary>
    [Fact]
    public void AResultsHeadGivenAgain_IsHeldBesideTheLeastPieceOfWhatFollows()
    {
        const int Room = 300;

        // A result's block: its head counts, the lines beneath it ride on it. Said again, a head of 280 characters costs 291
        // of the page's 300; one of 288 costs 299, leaving less than the least piece of the line after it.
        PageLayout<Blocked> Laid(string head)
            => Paging.Lay(
                [new PageSection<Blocked>(null, [new(head, 1), new("line 01", 0), new("line 02", 0), new("line 03", 0)], int.MaxValue)],
                [],
                [],
                Room,
                (entry, text, first) => entry with { Text = text, Counts = first ? entry.Counts : 0 },
                line => new Blocked(line, 0) { Block = null, BlockName = null });

        var (held, unheld) = (new string('h', 280), new string('u', 288));
        var given = Laid(held);
        var dropped = Laid(unheld);

        Assert.Equal(
            [
                $"page 1 of 2 · 1 results · ask again with page: 2 for the next\n{held}\nline 01\nline 02\n… H goes on",
                $"page 2 of 2 · the last\n{held} … goes on\nline 03",
            ],
            Enumerable.Range(1, given.Pages).Select(page => given.Page(page, "find H").Text));
        Assert.Equal(
            [
                $"page 1 of 2 · 1 results · ask again with page: 2 for the next\n{unheld}\nline 01\n… H goes on",
                "page 2 of 2 · the last\nline 02\nline 03",
            ],
            Enumerable.Range(1, dropped.Pages).Select(page => dropped.Page(page, "find H").Text));
        Assert.All(
            Enumerable.Range(1, given.Pages).Select(page => given.Page(page, "find H")).Concat(Enumerable.Range(1, dropped.Pages).Select(page => dropped.Page(page, "find H"))),
            page => Assert.InRange(page.Entries.Sum(entry => entry.Text.Length + 1), 1, Room));
    }

    /// <summary>The piece of an entry a page gives: its text, counting the entry on its first piece alone.</summary>
    private static Line Piece(Line entry, string text, bool first) => entry with { Text = text, Counts = first ? entry.Counts : 0 };

    /// <summary>An entry of a result's block named H: its head counts, the lines beneath it ride on it.</summary>
    /// <param name="Text">The line.</param>
    /// <param name="Counts">How much of its section it counts.</param>
    private sealed record Blocked(string Text, int Counts) : IPageEntry
    {
        /// <inheritdoc/>
        public string? Block { get; init; } = "h";

        /// <inheritdoc/>
        public string? BlockName { get; init; } = "H";
    }

    /// <summary>The entry of a trailing note, which counts nothing.</summary>
    private static Line Note(string line) => new(line, Counts: 0);

    /// <summary>An entry of a plain answer: a line, of no block.</summary>
    /// <param name="Text">The line.</param>
    /// <param name="Counts">How much of its section it counts.</param>
    private sealed record Line(string Text, int Counts = 1) : IPageEntry
    {
        /// <inheritdoc/>
        public string? Block => null;

        /// <inheritdoc/>
        public string? BlockName => null;
    }
}
