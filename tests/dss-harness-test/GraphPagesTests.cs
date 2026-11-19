using DssHarness.Core.Codebase;
using DssHarness.Core.Output;

namespace DssHarness.Tests;

/// <summary>
/// The one owner of an answer's pages: whole entries a page at an operation's size, within a character bound a page; the
/// header's words on a middle and the last page and none on one; exact totals; a page past the last refused with its
/// range; a block split across pages said to go on, its head on the page that serves its first line; and an entry wider
/// than a page given in pieces, every page within the bound.
/// </summary>
public sealed class GraphPagesTests
{
    private const int Most = 24 * 1024;

    /// <summary>A section's size holds whole entries a page, and the totals a header says are counted on the whole answer.</summary>
    [Fact]
    public void ASectionHoldsWholeEntriesAPage_AndTheTotalsAreExact()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 45).Select(at => new GraphEntry($"node {at}"))], 20);

        var first = GraphPages.Assemble([section], [], [], 1, "find Area", Most);
        var second = GraphPages.Assemble([section], [], [], 2, "find Area", Most);
        var third = GraphPages.Assemble([section], [], [], 3, "find Area", Most);

        Assert.Equal(
            "page 1 of 3 · 45 results · ask again with page: 2 for the next\n" + string.Join('\n', Enumerable.Range(1, 20).Select(at => $"node {at}")),
            first.Text);
        Assert.Equal("page 2 of 3 · 45 results · ask again with page: 3 for the next\n" + string.Join('\n', Enumerable.Range(21, 20).Select(at => $"node {at}")), second.Text);
        Assert.Equal("page 3 of 3 · the last\n" + string.Join('\n', Enumerable.Range(41, 5).Select(at => $"node {at}")), third.Text);
        Assert.Equal((1, 3, 45), (first.Page, first.Pages, first.Total));
        Assert.Equal((3, 3, 45), (third.Page, third.Pages, third.Total));
        Assert.Equal([.. Enumerable.Range(21, 20).Select(at => $"node {at}")], second.Entries.Select(entry => entry.Text));
    }

    /// <summary>An answer that holds one page has no header at all, and nothing is left out of it.</summary>
    [Fact]
    public void AnAnswerOfOnePage_HasNoHeader_AndHoldsAllOfIt()
    {
        var section = new GraphSection(null, [new("one"), new("two")], 20);

        var whole = GraphPages.Assemble([section], ["outline of a.cs (2 declaration(s)):"], ["1 node(s) left out: in files your reads do not reach"], 1, "outline a.cs", Most);

        Assert.Null(whole.Refusal);
        Assert.Equal((1, 1, 2), (whole.Page, whole.Pages, whole.Total));
        Assert.Equal("outline of a.cs (2 declaration(s)):\none\ntwo\n1 node(s) left out: in files your reads do not reach", whole.Text);
    }

    /// <summary>An answer of no entries is one page of its leading lines and its notes.</summary>
    [Fact]
    public void AnAnswerOfNoEntries_IsOnePageOfItsLinesAndNotes()
    {
        var answer = GraphPages.Assemble([], ["no node matched"], ["text: no line holds 'Okapi'"], 1, "find Okapi", Most);

        Assert.Null(answer.Refusal);
        Assert.Equal((1, 1, 0), (answer.Page, answer.Pages, answer.Total));
        Assert.Equal("no node matched\ntext: no line holds 'Okapi'", answer.Text);
    }

    /// <summary>A page past the last is refused, with the answer's range - and, where the page was made again since page 1, with why.</summary>
    [Fact]
    public void APagePastTheLast_IsRefusedWithItsRange_AndARecountSaysTheGraphChanged()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 45).Select(at => new GraphEntry($"node {at}"))], 20);
        var empty = GraphPages.Assemble([], ["no node matched"], [], 2, "find Okapi", Most);

        var refused = GraphPages.Assemble([section], [], [], 4, "find TaxPolicy", Most);
        var last = GraphPages.Assemble([section], [], [], 3, "find TaxPolicy", Most);
        var shrunk = GraphPages.Assemble([section], [], [], 4, "find TaxPolicy", Most, GraphPages.Recounted);

        Assert.Equal("page 4 of 'find TaxPolicy': it has 3 pages (1 to 3)", refused.Text);
        Assert.Equal("past the last", refused.Refusal);
        Assert.Empty(refused.Sections);
        Assert.Equal("page 2 of 'find Okapi': it has 1 page (1 to 1)", empty.Text);
        Assert.Null(last.Refusal);

        // A model that read "page 1 of 4" is told why the answer has three pages now.
        Assert.Equal("page 4 of 'find TaxPolicy': it has 3 pages (1 to 3) · the graph changed since page 1: pages recounted", shrunk.Text);
        Assert.Equal(GraphPages.Recounted, shrunk.Note);
    }

    /// <summary>Each page of a section holds its caption, so a page read alone names what its entries are of.</summary>
    [Fact]
    public void EachPageHoldsItsSectionsCaption()
    {
        var callers = new GraphSection("callers (40):", [.. Enumerable.Range(1, 40).Select(at => new GraphEntry($"caller {at}"))], 15);
        var text = new GraphSection("text: lines that hold 'Area'", [.. Enumerable.Range(1, 22).Select(at => new GraphEntry($"hit {at}"))], 20);

        var first = GraphPages.Assemble([callers, text], ["the card"], [], 1, "node Area", Most);
        var second = GraphPages.Assemble([callers, text], ["the card"], [], 2, "node Area", Most);
        var last = GraphPages.Assemble([callers, text], ["the card"], [], 3, "node Area", Most);

        // Pages hold of every section what its size leaves: the callers' three pages at 15, the hits' two at 20.
        Assert.Equal((3, 62), (first.Pages, first.Total));
        Assert.Equal("page 1 of 3 · 62 results · ask again with page: 2 for the next\nthe card\ncallers (40):\n" + string.Join('\n', Enumerable.Range(1, 15).Select(at => $"caller {at}")) + "\ntext: lines that hold 'Area'\n" + string.Join('\n', Enumerable.Range(1, 20).Select(at => $"hit {at}")), first.Text);
        Assert.Equal("page 2 of 3 · 62 results · ask again with page: 3 for the next\nthe card\ncallers (40):\n" + string.Join('\n', Enumerable.Range(16, 15).Select(at => $"caller {at}")) + "\ntext: lines that hold 'Area'\nhit 21\nhit 22", second.Text);
        Assert.Equal("page 3 of 3 · the last\nthe card\ncallers (40):\n" + string.Join('\n', Enumerable.Range(31, 10).Select(at => $"caller {at}")), last.Text);
        Assert.Equal([new GraphPageSection("callers (40):", [.. Enumerable.Range(31, 10).Select(at => new GraphEntry($"caller {at}"))])], last.Sections, new SectionsAlike());
    }

    /// <summary>
    /// A page ends at a whole entry where the character bound leaves no room for the next one, and nothing is left out:
    /// read in order, the pages hold every entry once.
    /// </summary>
    [Fact]
    public void APageEndsAtAWholeEntry_AtItsCharacterBound_AndNothingIsLeftOut()
    {
        var entries = Enumerable.Range(0, 10).Select(at => new GraphEntry((char)('a' + at) + new string('n', 399))).ToList();
        var section = new GraphSection(null, entries, 20);

        // A thousand characters of room hold two entries of four hundred and one, never a third.
        var pages = Enumerable.Range(1, 5).Select(page => GraphPages.Assemble([section], [], [], page, "body Demo.Long", GraphPages.ReservedCharacters + 1000)).ToList();

        Assert.All(pages, page => Assert.Equal((5, 10), (page.Pages, page.Total)));
        Assert.All(pages, page => Assert.Equal(2, page.Entries.Count));
        Assert.Equal(entries.Select(entry => entry.Text), pages.SelectMany(page => page.Entries).Select(entry => entry.Text));
    }

    /// <summary>
    /// The leading lines an answer says on every page take their room on each: a page of long leading lines holds fewer
    /// entries, and never passes the characters an answer holds.
    /// </summary>
    [Fact]
    public void TheLeadingLinesTakeTheirRoomOnEveryPage()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(0, 40).Select(at => new GraphEntry($"{at:D3}" + new string('e', 197)))], 100);
        var leading = new string('l', 6000);

        var first = GraphPages.Assemble([section], [leading], [], 1, "outline a.cs", 8 * 1024);

        // What is left of 8 KiB once the reserve and the leading line are taken is under the least room a page gives: one
        // entry a page.
        Assert.Equal(40, first.Pages);
        Assert.True(first.Text.Length <= 8 * 1024, $"{first.Text.Length} characters");
        Assert.Equal($"page 1 of 40 · 40 results · ask again with page: 2 for the next\n{leading}\n000{new string('e', 197)}", first.Text);
    }

    /// <summary>
    /// A block of served lines split across pages is said to go on, on every page that ends inside it, and the last page
    /// says nothing goes on.
    /// </summary>
    [Fact]
    public void ABlockSplitAcrossPages_SaysItGoesOn()
    {
        var entries = new List<GraphEntry> { new("Demo.Long src/Long.cs:1-8 #0123456a", Lines: 0, GoesOn: "Demo.Long", Node: "csharp:Demo.Long") };
        entries.AddRange(Enumerable.Range(1, 8).Select(at => new GraphEntry($"{at}\tline {at}", Lines: 1, GoesOn: "Demo.Long", Node: "csharp:Demo.Long")));

        var section = new GraphSection(null, entries, 3);
        var pages = Enumerable.Range(1, 3).Select(page => GraphPages.Assemble([section], [], [], page, "body Demo.Long", Most)).ToList();

        Assert.Equal("page 1 of 3 · 8 results · ask again with page: 2 for the next\nDemo.Long src/Long.cs:1-8 #0123456a\n1\tline 1\n2\tline 2\n3\tline 3\n… Demo.Long goes on", pages[0].Text);
        Assert.Equal("page 2 of 3 · 8 results · ask again with page: 3 for the next\n4\tline 4\n5\tline 5\n6\tline 6\n… Demo.Long goes on", pages[1].Text);
        Assert.Equal("page 3 of 3 · the last\n7\tline 7\n8\tline 8", pages[2].Text);
    }

    /// <summary>
    /// A section the page ends inside a block of - a body's, its lines' size spent - says the block goes on right after
    /// what the page gave of it, though a later section - the body's text search - still has entries on the page: the
    /// page that holds the hits never reads as the end of the block.
    /// </summary>
    [Fact]
    public void ASectionThePageEndsInsideABlockOf_SaysItGoesOn_BeforeTheSectionsAfterIt()
    {
        var block = new List<GraphEntry> { new("A src/A.cs:1-5 #0123456a", Lines: 0, GoesOn: "A", Node: "csharp:A") };
        block.AddRange(Enumerable.Range(1, 5).Select(at => new GraphEntry($"{at}\tline {at}", Lines: 1, GoesOn: "A", Node: "csharp:A", File: "src/A.cs")));
        var hits = new GraphSection("text: lines that hold 'A'", [.. Enumerable.Range(1, 2).Select(at => new GraphEntry($"  src/B.cs:{at} A", Hit: true))], 20);
        GraphSection[] sections = [new(null, block, 3), hits];

        var first = GraphPages.Assemble(sections, [], [], 1, "body A", Most);
        var second = GraphPages.Assemble(sections, [], [], 2, "body A", Most);

        Assert.Equal(
            "page 1 of 2 · 7 results · ask again with page: 2 for the next\nA src/A.cs:1-5 #0123456a\n1\tline 1\n2\tline 2\n3\tline 3\n… A goes on\ntext: lines that hold 'A'\n  src/B.cs:1 A\n  src/B.cs:2 A",
            first.Text);
        Assert.Equal("page 2 of 2 · the last\n4\tline 4\n5\tline 5", second.Text);
    }

    /// <summary>
    /// A block's head goes on the page that serves the block's first line: a page whose size is spent ends before the
    /// next node's head, saying nothing goes on, and the next page begins with the head, so the page that serves a node's
    /// lines names its node, its path and its hash.
    /// </summary>
    [Fact]
    public void ABlocksHead_GoesOnThePageOfItsFirstLine_AndAPageEndingBetweenBlocksSaysNothingGoesOn()
    {
        var entries = new List<GraphEntry>
        {
            new("A src/A.cs:1-2 #0123456a", Lines: 0, GoesOn: "A", Node: "csharp:A"),
            new("1\tline 1", Lines: 1, GoesOn: "A", Node: "csharp:A"),
            new("2\tline 2", Lines: 1, GoesOn: "A", Node: "csharp:A"),
            new("B src/B.cs:3-3 #0123456b", Lines: 0, GoesOn: "B", Node: "csharp:B"),
            new("3\tline 3", Lines: 1, GoesOn: "B", Node: "csharp:B"),
        };

        var first = GraphPages.Assemble([new GraphSection(null, entries, 2)], [], [], 1, "body A, B", Most);
        var second = GraphPages.Assemble([new GraphSection(null, entries, 2)], [], [], 2, "body A, B", Most);

        Assert.Equal("page 1 of 2 · 3 results · ask again with page: 2 for the next\nA src/A.cs:1-2 #0123456a\n1\tline 1\n2\tline 2", first.Text);
        Assert.Equal("page 2 of 2 · the last\nB src/B.cs:3-3 #0123456b\n3\tline 3", second.Text);
    }

    /// <summary>
    /// A block is its node's: two nodes of one qualified name - two overloads - are two blocks, so a page that ends where
    /// the second begins says nothing goes on.
    /// </summary>
    [Fact]
    public void TwoNodesOfOneName_AreTwoBlocks()
    {
        var entries = new List<GraphEntry>
        {
            new("Shop.Cart.Pay src/Cart.cs:1-2 #0123456a", Lines: 0, GoesOn: "Shop.Cart.Pay", Node: "csharp:Shop.Cart.Pay(int)"),
            new("1\tpay an int", Lines: 1, GoesOn: "Shop.Cart.Pay", Node: "csharp:Shop.Cart.Pay(int)"),
            new("2\t}", Lines: 1, GoesOn: "Shop.Cart.Pay", Node: "csharp:Shop.Cart.Pay(int)"),
            new("Shop.Cart.Pay src/Cart.cs:3-3 #0123456b", Lines: 0, GoesOn: "Shop.Cart.Pay", Node: "csharp:Shop.Cart.Pay(string)"),
            new("3\tpay a string", Lines: 1, GoesOn: "Shop.Cart.Pay", Node: "csharp:Shop.Cart.Pay(string)"),
        };

        var first = GraphPages.Assemble([new GraphSection(null, entries, 2)], [], [], 1, "body Pay", Most);

        Assert.Equal("page 1 of 2 · 3 results · ask again with page: 2 for the next\nShop.Cart.Pay src/Cart.cs:1-2 #0123456a\n1\tpay an int\n2\t}", first.Text);
    }

    /// <summary>
    /// An entry wider than a whole page - one line of a minified bundle - is given in pieces, a page each, every page within
    /// the characters an answer holds: each piece marked where the line goes on or went on, the page that ends inside it
    /// saying so, the pieces read in order giving the line whole, never a surrogate pair parted, and the line counted once.
    /// </summary>
    [Fact]
    public void AnEntryWiderThanAPage_IsGivenInPiecesAPage_EveryPageWithinTheBound()
    {
        // Every character a pair: a cut anywhere but between two pairs would part one.
        var line = "1\t" + string.Concat(Enumerable.Repeat("\U0001F600", 50_000));
        var entries = new List<GraphEntry>
        {
            new("app.min.js public/app.min.js:1-1", Lines: 0, GoesOn: "public/app.min.js", Node: "javascript:public/app.min.js"),
            new(line, Lines: 1, GoesOn: "public/app.min.js", Node: "javascript:public/app.min.js", File: "public/app.min.js"),
            new("Next src/Next.js:1-1", Lines: 0, GoesOn: "Next", Node: "javascript:Next"),
            new("1\tnext();", Lines: 1, GoesOn: "Next", Node: "javascript:Next", File: "src/Next.js"),
        };
        var section = new GraphSection(null, entries, 600);

        var first = GraphPages.Assemble([section], [], [], 1, "body app.min.js, Next", Most);
        var pages = Enumerable.Range(1, first.Pages).Select(page => GraphPages.Assemble([section], [], [], page, "body app.min.js, Next", Most)).ToList();
        var pieces = pages.SelectMany(page => page.Entries).Where(entry => entry.Node == "javascript:public/app.min.js" && entry.File is not null).Select(entry => entry.Text).ToList();

        Assert.Equal(5, first.Pages);
        Assert.All(pages, page => Assert.True(page.Text.Length <= Most, $"page {page.Page} holds {page.Text.Length} characters"));
        Assert.All(pages.Take(4), page => Assert.Equal($"{TextCut.Ellipsis} public/app.min.js goes on", page.Text.Split('\n')[^1]));
        Assert.Equal(pieces.Count, first.Pages);
        Assert.Equal(line, string.Concat(pieces.Select((piece, at) => piece[(at == 0 ? 0 : 1)..(at == pieces.Count - 1 ? piece.Length : piece.Length - 1)])));
        Assert.All(pieces, piece => Assert.False(char.IsHighSurrogate(piece[^1]) || char.IsLowSurrogate(piece[0]), "a piece parts a surrogate pair"));
        Assert.All(pieces.Skip(1), piece => Assert.Equal(TextCut.Ellipsis, piece[0]));
        Assert.All(pieces.SkipLast(1), piece => Assert.Equal(TextCut.Ellipsis, piece[^1]));

        // The line is one result, counted on its first piece alone; the next node's head and line follow its last piece.
        Assert.Equal(2, first.Total);
        Assert.Equal(2, pages.SelectMany(page => page.Entries).Sum(entry => entry.Lines));
        Assert.Equal(["Next src/Next.js:1-1", "1\tnext();"], pages[^1].Text.Split('\n')[^2..]);
    }

    /// <summary>The notes an answer ends with are said on its last page alone, and its leading lines on every page.</summary>
    [Fact]
    public void TheTrailingNotesAreSaidOnTheLastPage_TheLeadingLinesOnEveryPage()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 45).Select(at => new GraphEntry($"node {at}"))], 20);
        const string Note = "1 node(s) left out: in files your reads do not reach";

        var pages = Enumerable.Range(1, 3).Select(page => GraphPages.Assemble([section], ["the card"], [Note], page, "node Area", Most)).ToList();

        Assert.Equal("page 1 of 3 · 45 results · ask again with page: 2 for the next\nthe card\n" + string.Join('\n', Enumerable.Range(1, 20).Select(at => $"node {at}")), pages[0].Text);
        Assert.Equal("page 2 of 3 · 45 results · ask again with page: 3 for the next\nthe card\n" + string.Join('\n', Enumerable.Range(21, 20).Select(at => $"node {at}")), pages[1].Text);
        Assert.Equal("page 3 of 3 · the last\nthe card\n" + string.Join('\n', Enumerable.Range(41, 5).Select(at => $"node {at}")) + "\n" + Note, pages[2].Text);
    }

    /// <summary>
    /// The notes an answer ends with are held within a page's room as any entry is: notes past what the last entries'
    /// page holds go on to the pages after it, a note wider than a page in pieces, every character of every note given
    /// and no page past its bound; the notes count toward no total and are none of the page's sections.
    /// </summary>
    [Fact]
    public void TheTrailingNotes_ArePagedWithinTheRoom_EveryNoteGiven()
    {
        const int Small = GraphPages.ReservedCharacters + 1000;
        var section = new GraphSection(null, [.. Enumerable.Range(1, 3).Select(at => new GraphEntry($"node {at}"))], 20);
        List<string> notes = [.. Enumerable.Range(1, 40).Select(at => $"note {at:D2} " + new string('n', 80)), "wide " + new string('w', 2500)];

        var first = GraphPages.Assemble([section], [], notes, 1, "find node", Small);
        var pages = Enumerable.Range(1, first.Pages).Select(page => GraphPages.Assemble([section], [], notes, page, "find node", Small)).ToList();
        var lines = pages.SelectMany(page => page.Text.Split('\n').Skip(1)).Where(line => line != "… the line goes on").ToList();
        var wide = lines.Where(line => line.StartsWith("wide ", StringComparison.Ordinal) || line.StartsWith('…')).ToList();

        Assert.True(first.Pages > 2, "the notes took no page of their own");
        Assert.All(pages, page => Assert.True(page.Text.Length <= Small, "a page passed its bound"));
        Assert.All(pages, page => Assert.Equal(3, page.Total));
        Assert.Equal(["node 1", "node 2", "node 3"], pages.SelectMany(page => page.Sections).SelectMany(section => section.Entries).Select(entry => entry.Text));
        Assert.Equal(notes.Take(40), lines.Where(line => line.StartsWith("note ", StringComparison.Ordinal)));
        Assert.True(wide.Count > 1, "a note wider than a page was not given in pieces");
        Assert.Equal(notes[^1], string.Concat(wide.Select((piece, at) => piece[(at == 0 ? 0 : 1)..(at == wide.Count - 1 ? piece.Length : piece.Length - 1)])));
    }

    /// <summary>
    /// A page made again because the graph changed since page 1 says so in its header, after the counts and the way to the
    /// next page, on a middle page and on the last; one with no result of page 1 kept says that, with why.
    /// </summary>
    [Fact]
    public void ARecountedPage_SaysSoInItsHeader()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 45).Select(at => new GraphEntry($"node {at}"))], 20);

        var middle = GraphPages.Assemble([section], [], [], 2, "find Area", Most, GraphPages.Recounted);
        var last = GraphPages.Assemble([section], [], [], 3, "find Area", Most, GraphPages.Recounted);
        var lost = GraphPages.Assemble([section], [], [], 2, "find Area", Most, GraphPages.NotKept("it was kept under other read rules"));

        Assert.Equal("page 2 of 3 · 45 results · ask again with page: 3 for the next · the graph changed since page 1: pages recounted\n" + string.Join('\n', Enumerable.Range(21, 20).Select(at => $"node {at}")), middle.Text);
        Assert.Equal("page 3 of 3 · the last · the graph changed since page 1: pages recounted\n" + string.Join('\n', Enumerable.Range(41, 5).Select(at => $"node {at}")), last.Text);
        Assert.Equal("page 2 of 3 · 45 results · ask again with page: 3 for the next · page 1 of this ask is not kept (it was kept under other read rules): pages counted on the graph as it stands", lost.Text.Split('\n')[0]);
        Assert.Equal("page 1 of this ask is not kept: pages counted on the graph as it stands", GraphPages.NotKept(null));
    }

    /// <summary>An entry that counts no lines rides on whatever page holds it, and the totals do not count it.</summary>
    [Fact]
    public void AnEntryThatCountsNoLines_RidesOnItsPage_AndIsNotAResult()
    {
        var entries = new List<GraphEntry> { new("the head of the block", Lines: 0), new("1\tline 1", Lines: 1), new("2\tline 2", Lines: 1) };

        var answer = GraphPages.Assemble([new GraphSection(null, entries, 2)], [], [], 1, "body A", Most);

        Assert.Equal((1, 1, 2), (answer.Page, answer.Pages, answer.Total));
        Assert.Equal(entries.Select(entry => entry.Text), answer.Entries.Select(entry => entry.Text));
    }

    /// <summary>A section of no size, an entry of more than no lines below none, an empty entry and a caption of two lines are refused.</summary>
    [Fact]
    public void AShapeThatIsNoAnswer_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => GraphPages.Assemble([new GraphSection(null, [], 0)], [], [], 1, "find x", Most));
        Assert.Throws<ArgumentException>(() => GraphPages.Assemble([new GraphSection(null, [new("one", Lines: -1)], 2)], [], [], 1, "find x", Most));
        Assert.Throws<ArgumentException>(() => GraphPages.Assemble([new GraphSection(null, [new("")], 2)], [], [], 1, "find x", Most));
        Assert.Throws<ArgumentException>(() => GraphPages.Assemble([new GraphSection("two\nlines", [new("one")], 2)], [], [], 1, "find x", Most));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphPages.Assemble([], [], [], 0, "find x", Most));
    }

    /// <summary>Two sections of a page alike: the same caption and the same entries in order.</summary>
    private sealed class SectionsAlike : IEqualityComparer<GraphPageSection>
    {
        public bool Equals(GraphPageSection? x, GraphPageSection? y)
            => x is not null && y is not null && x.Caption == y.Caption && x.Entries.SequenceEqual(y.Entries);

        public int GetHashCode(GraphPageSection obj) => obj.Caption?.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
