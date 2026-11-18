using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

/// <summary>
/// The one owner of an answer's pages: whole entries a page at an operation's size, within a character bound a page; the
/// header's words on a middle and the last page and none on one; exact totals; a page past the last refused with its
/// range; a block split across pages said to go on; and what the first page keeps for the later ones.
/// </summary>
public sealed class GraphPagesTests
{
    /// <summary>A section's size holds whole entries a page, and the totals a header says are counted on the whole answer.</summary>
    [Fact]
    public void ASectionHoldsWholeEntriesAPage_AndTheTotalsAreExact()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 45).Select(at => new GraphEntry($"node {at}"))], 20);

        var first = GraphPages.Assemble([section], [], [], 1, "find Area", 24 * 1024);
        var second = GraphPages.Assemble([section], [], [], 2, "find Area", 24 * 1024);
        var third = GraphPages.Assemble([section], [], [], 3, "find Area", 24 * 1024);

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

        var whole = GraphPages.Assemble([section], ["outline of a.cs (2 declaration(s)):"], ["1 node(s) left out: in files your reads do not reach"], 1, "outline a.cs", 24 * 1024);

        Assert.Null(whole.Refusal);
        Assert.Equal((1, 1, 2), (whole.Page, whole.Pages, whole.Total));
        Assert.Equal("outline of a.cs (2 declaration(s)):\none\ntwo\n1 node(s) left out: in files your reads do not reach", whole.Text);
    }

    /// <summary>An answer of no entries is one page of its leading lines and its notes.</summary>
    [Fact]
    public void AnAnswerOfNoEntries_IsOnePageOfItsLinesAndNotes()
    {
        var answer = GraphPages.Assemble([], ["no node matched"], ["text: no line holds 'Okapi'"], 1, "find Okapi", 24 * 1024);

        Assert.Null(answer.Refusal);
        Assert.Equal((1, 1, 0), (answer.Page, answer.Pages, answer.Total));
        Assert.Equal("no node matched\ntext: no line holds 'Okapi'", answer.Text);
    }

    /// <summary>A page past the last is refused, with the answer's range.</summary>
    [Fact]
    public void APagePastTheLast_IsRefusedWithItsRange()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 45).Select(at => new GraphEntry($"node {at}"))], 20);
        var empty = GraphPages.Assemble([], ["no node matched"], [], 2, "find Okapi", 24 * 1024);

        var refused = GraphPages.Assemble([section], [], [], 4, "find TaxPolicy", 24 * 1024);
        var last = GraphPages.Assemble([section], [], [], 3, "find TaxPolicy", 24 * 1024);

        Assert.Equal("page 4 of 'find TaxPolicy': it has 3 pages (1 to 3)", refused.Text);
        Assert.Equal("past the last", refused.Refusal);
        Assert.Equal("page 2 of 'find Okapi': it has 1 page (1 to 1)", empty.Text);
        Assert.Null(last.Refusal);
    }

    /// <summary>Each page of a section holds its caption, so a page read alone names what its entries are of.</summary>
    [Fact]
    public void EachPageHoldsItsSectionsCaption()
    {
        var callers = new GraphSection("callers (40):", [.. Enumerable.Range(1, 40).Select(at => new GraphEntry($"caller {at}"))], 15);
        var text = new GraphSection("text: lines that hold 'Area'", [.. Enumerable.Range(1, 22).Select(at => new GraphEntry($"hit {at}"))], 20);

        var first = GraphPages.Assemble([callers, text], ["the card"], [], 1, "node Area", 24 * 1024);
        var second = GraphPages.Assemble([callers, text], ["the card"], [], 2, "node Area", 24 * 1024);
        var last = GraphPages.Assemble([callers, text], ["the card"], [], 3, "node Area", 24 * 1024);

        // Pages hold of every section what its size leaves: the callers' three pages at 15, the hits' two at 20.
        Assert.Equal((3, 62), (first.Pages, first.Total));
        Assert.Equal("page 1 of 3 · 62 results · ask again with page: 2 for the next\nthe card\ncallers (40):\n" + string.Join('\n', Enumerable.Range(1, 15).Select(at => $"caller {at}")) + "\ntext: lines that hold 'Area'\n" + string.Join('\n', Enumerable.Range(1, 20).Select(at => $"hit {at}")), first.Text);
        Assert.Equal("page 2 of 3 · 62 results · ask again with page: 3 for the next\nthe card\ncallers (40):\n" + string.Join('\n', Enumerable.Range(16, 15).Select(at => $"caller {at}")) + "\ntext: lines that hold 'Area'\nhit 21\nhit 22", second.Text);
        Assert.Equal("page 3 of 3 · the last\nthe card\ncallers (40):\n" + string.Join('\n', Enumerable.Range(31, 10).Select(at => $"caller {at}")), last.Text);
    }

    /// <summary>A page ends at a whole entry where the character bound leaves no room for the next one, and nothing is left out.</summary>
    [Fact]
    public void APageEndsAtAWholeEntry_AtItsCharacterBound_AndNothingIsLeftOut()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 10).Select(at => new GraphEntry(new string('n', 400)))], 20);
        var first = GraphPages.Assemble([section], [], [], 1, "body Demo.Long", 4 * 1024);
        var read = new List<string>(first.Entries.Select(entry => entry.Text));

        for (var page = 2; page <= first.Pages; page++)
        {
            read.AddRange(GraphPages.Assemble([section], [], [], page, "body Demo.Long", 4 * 1024).Entries.Select(entry => entry.Text));
        }

        // No room is left for any entry, so each page holds one whole: the pages hold all ten, none cut.
        Assert.Equal(10, first.Pages);
        Assert.Equal(10, read.Count);
        Assert.All(read, line => Assert.Equal(400, line.Length));
    }

    /// <summary>A block of served lines split across pages is said to go on, on every page that ends inside it.</summary>
    [Fact]
    public void ABlockSplitAcrossPages_SaysItGoesOn()
    {
        var entries = new List<GraphEntry> { new("Demo.Long src/Long.cs:1-2 #0123456a", Lines: 0, GoesOn: "Demo.Long", Node: "csharp:Demo.Long") };
        entries.AddRange(Enumerable.Range(1, 8).Select(at => new GraphEntry($"{at}\tline {at}", Lines: 1, GoesOn: "Demo.Long", Node: "csharp:Demo.Long")));

        var section = new GraphSection(null, entries, 3);
        var second = GraphPages.Assemble([section], [], [], 2, "body Demo.Long", 24 * 1024);

        Assert.Equal("… Demo.Long goes on", second.Text.Split('\n')[^1]);
        Assert.Equal(["csharp:Demo.Long"], second.Entries.Where(entry => entry.Lines > 0).Select(entry => entry.Node).Distinct().ToList());
    }

    /// <summary>A page that ends where another node's block begins says nothing goes on.</summary>
    [Fact]
    public void APageThatEndsAtANewBlock_SaysNothingGoesOn()
    {
        var entries = new List<GraphEntry>
        {
            new("A src/A.cs:1-2 #0123456a", Lines: 0, GoesOn: "A", Node: "csharp:A"),
            new("1\tline 1", Lines: 1, GoesOn: "A", Node: "csharp:A"),
            new("2\tline 2", Lines: 1, GoesOn: "A", Node: "csharp:A"),
            new("B src/B.cs:1-2 #0123456b", Lines: 0, GoesOn: "B", Node: "csharp:B"),
            new("3\tline 3", Lines: 1, GoesOn: "B", Node: "csharp:B"),
        };

        var second = GraphPages.Assemble([new GraphSection(null, entries, 2)], [], [], 2, "body A", 24 * 1024);

        Assert.DoesNotContain("goes on", second.Text, StringComparison.Ordinal);
    }

    /// <summary>The notes an answer ends with are said on its last page alone, and its leading lines on every page.</summary>
    [Fact]
    public void TheTrailingNotesAreSaidOnTheLastPage_TheLeadingLinesOnEveryPage()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 45).Select(at => new GraphEntry($"node {at}"))], 20);

        var first = GraphPages.Assemble([section], ["the card"], ["1 node(s) left out: in files your reads do not reach"], 1, "node Area", 24 * 1024);
        var last = GraphPages.Assemble([section], ["the card"], ["1 node(s) left out: in files your reads do not reach"], 3, "node Area", 24 * 1024);

        Assert.DoesNotContain("left out", first.Text, StringComparison.Ordinal);
        Assert.StartsWith("the card", last.Text.Split('\n')[1], StringComparison.Ordinal);
        Assert.EndsWith("1 node(s) left out: in files your reads do not reach", last.Text, StringComparison.Ordinal);
    }

    /// <summary>A page made again because the graph changed since page 1 says so in its header, on a middle page and on the last.</summary>
    [Fact]
    public void ARecountedPage_SaysSoInItsHeader()
    {
        var section = new GraphSection(null, [.. Enumerable.Range(1, 45).Select(at => new GraphEntry($"node {at}"))], 20);

        var middle = GraphPages.Assemble([section], [], [], 2, "find Area", 24 * 1024, recounted: true);
        var last = GraphPages.Assemble([section], [], [], 3, "find Area", 24 * 1024, recounted: true);

        Assert.Equal("page 2 of 3 · the graph changed since page 1: pages recounted\n" + string.Join('\n', Enumerable.Range(21, 20).Select(at => $"node {at}")), middle.Text);
        Assert.Equal("page 3 of 3 · the last · the graph changed since page 1: pages recounted\n" + string.Join('\n', Enumerable.Range(41, 5).Select(at => $"node {at}")), last.Text);
    }

    /// <summary>An entry that counts no lines rides on whatever page holds it, and the totals do not count it.</summary>
    [Fact]
    public void AnEntryThatCountsNoLines_RidesOnItsPage_AndIsNotAResult()
    {
        var entries = new List<GraphEntry> { new("the head of the block", Lines: 0), new("1\tline 1", Lines: 1), new("2\tline 2", Lines: 1) };

        var answer = GraphPages.Assemble([new GraphSection(null, entries, 2)], [], [], 1, "body A", 24 * 1024);

        Assert.Equal((1, 1, 2), (answer.Page, answer.Pages, answer.Total));
        Assert.Equal(3, answer.Entries.Count);
    }

    /// <summary>A section of no size, an entry of more than no lines below none, and an empty entry are refused.</summary>
    [Fact]
    public void AShapeThatIsNoAnswer_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => GraphPages.Assemble([new GraphSection(null, [], 0)], [], [], 1, "find x", 24 * 1024));
        Assert.Throws<ArgumentException>(() => GraphPages.Assemble([new GraphSection(null, [new("one", Lines: -1)], 2)], [], [], 1, "find x", 24 * 1024));
        Assert.Throws<ArgumentException>(() => GraphPages.Assemble([new GraphSection(null, [new("")], 2)], [], [], 1, "find x", 24 * 1024));
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphPages.Assemble([], [], [], 0, "find x", 24 * 1024));
    }
}
