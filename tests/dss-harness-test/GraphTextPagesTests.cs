using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

/// <summary>
/// The pages of a graph text a step is given unasked - its pack, its changes, the run's map - are laid whole as they are
/// made: every line is laid before any page is written, so every page's last line knows how many pages there are, the
/// first as well as the last, whichever page is asked first.
/// </summary>
public sealed class GraphTextPagesTests
{
    /// <summary>
    /// A text of three pages names three on every page, page 1 among them, asked first and alone: no page is ever written
    /// before the text's total is known, so none is frozen without it. Red where a page is written as it is laid, before
    /// the lines after it are read.
    /// </summary>
    [Fact]
    public void AThreePageText_NamesItsTotalOnEveryPage_ThePageAskedFirstAmongThem()
    {
        // Each line costs its six bytes and its line's end, and each page's first line its eleven and its end: a room of
        // three lines past the first line holds three a page, so nine lines are three pages.
        var lines = Enumerable.Range(1, 9).Select(at => new GraphTextLine(null, $"line {at}")).ToList();
        var pages = new GraphTextPages(lines, room: 12 + (3 * 7), lastLineRoom: 0, number => $"text page {number}", end => $"page {end.Page} of {end.Pages}{(end.Last ? " · the last" : " · more after it")}");

        var first = pages.Page(1);

        Assert.Equal("text page 1\nline 1\nline 2\nline 3\npage 1 of 3 · more after it", first?.Text);
        Assert.Equal("text page 2\nline 4\nline 5\nline 6\npage 2 of 3 · more after it", pages.Page(2)?.Text);
        Assert.Equal("text page 3\nline 7\nline 8\nline 9\npage 3 of 3 · the last", pages.Page(3)?.Text);
        Assert.Equal([false, false, true], new[] { first, pages.Page(2), pages.Page(3) }.Select(page => page!.Last));
        Assert.Null(pages.Page(4));
        Assert.Equal(3, pages.Count);
        Assert.Equal([first!, pages.Page(2)!, pages.Page(3)!], pages.All);
        Assert.Equal(lines, pages.All.SelectMany(page => page.Lines));
    }
}
