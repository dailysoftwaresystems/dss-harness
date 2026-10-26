using DssHarness.Core.Output;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>A page's HTML reduced to the text a reader of it sees: its title, then its blocks a line each, what no reader sees left out.</summary>
public sealed class WebPageTextTests
{
    /// <summary>
    /// The title comes first, then a blank line; a block is a line of its own, an inline run joins its line, white space
    /// is collapsed, a preformatted block stands as it is, and entities are read; scripts, styles and hidden elements are
    /// left out; markup a browser reads past is read past.
    /// </summary>
    [Fact]
    public async Task APage_IsReducedToTheTextAReaderSees()
    {
        const string Html = """
            <html><head><title> The   Title </title><style>p { color: red }</style></head>
            <body><script>alert('run')</script><h1>Head</h1><p>One <b>bold</b>
              word.</p><div hidden>secret</div><span aria-hidden="true">icon</span><noscript>enable scripts</noscript>
            <pre>  a
                b</pre><ul><li>x</li><li>y &amp; z</li></ul><table><tr><td>cell</td><td>next</td></tr></table>
            <p>unclosed<p>next<template><p>never</p></template></body></html>
            """;

        Assert.Equal(
            "The Title\n\nHead\nOne bold word.\n  a\n    b\nx\ny & z\ncell next\nunclosed\nnext",
            await WebPageText.OfAsync(Html, TestContext.Current.CancellationToken));
    }

    /// <summary>A page nested deeper than any thread's stack holds is read all the same.</summary>
    [Fact]
    public async Task APageNestedDeep_IsRead()
    {
        const int Depth = 50_000;

        Assert.Equal("deep", await WebPageText.OfAsync(string.Concat(Enumerable.Repeat("<span>", Depth)) + "deep" + string.Concat(Enumerable.Repeat("</span>", Depth)), TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A preformatted block is its text as it stands, the markup inside it adding nothing but its own text - what is
    /// hidden there left out too - and one nested deeper than any thread's stack holds is read all the same.
    /// </summary>
    [Fact]
    public async Task APreformattedBlock_IsItsTextAsItStands_HoweverDeepItsMarkup()
    {
        const int Depth = 50_000;

        Assert.Equal(
            "before\na\n  bcd\ntail\nafter",
            await WebPageText.OfAsync("<p>before</p><pre>a\n  <div>b</div><table><tr><td>c</td><td>d</td></tr></table><span hidden>gone</span><script>x</script></pre>tail<p>after</p>", TestContext.Current.CancellationToken));
        Assert.Equal(
            "deep",
            await WebPageText.OfAsync("<pre>" + string.Concat(Enumerable.Repeat("<span>", Depth)) + "deep" + string.Concat(Enumerable.Repeat("</span>", Depth)) + "</pre>", TestContext.Current.CancellationToken));
    }

    /// <summary>A reading is stopped by its token in the parse, a page too small for the walk to look, and in the walk of its nodes.</summary>
    [Fact]
    public async Task AReading_IsStoppedByItsToken_InTheParseAndInTheWalk()
    {
        var document = await new AngleSharp.Html.Parser.HtmlParser().ParseDocumentAsync(string.Concat(Enumerable.Repeat("<p>x</p>", 5_000)), TestContext.Current.CancellationToken);
        using var stopped = new CancellationTokenSource();
        await stopped.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WebPageText.OfAsync("<p>x</p>", stopped.Token));
        Assert.Throws<OperationCanceledException>(() => WebPageText.Of(document, stopped.Token));
        Assert.Equal(string.Join("\n", Enumerable.Repeat("x", 5_000)), WebPageText.Of(document, TestContext.Current.CancellationToken));
    }

    /// <summary>A page with no title is its text alone; a fragment is read as a body.</summary>
    [Fact]
    public async Task APageWithNoTitle_IsItsTextAlone()
    {
        Assert.Equal("only this", await WebPageText.OfAsync("<p>only   this</p>", TestContext.Current.CancellationToken));
        Assert.Equal("plain words", await WebPageText.OfAsync("plain words", TestContext.Current.CancellationToken));
        Assert.Equal(string.Empty, await WebPageText.OfAsync(string.Empty, TestContext.Current.CancellationToken));
    }

    /// <summary>A field that is text is never read as markup: only its blanks are made single.</summary>
    [Theory]
    [InlineData("  a  <b>c</b>\n &amp; d ", "a <b>c</b> &amp; d")]
    [InlineData("Simple charts using the <canvas> tag", "Simple charts using the <canvas> tag")]
    [InlineData("A reader for IAsyncEnumerable<T> streams", "A reader for IAsyncEnumerable<T> streams")]
    public void AFieldThatIsText_IsNeverReadAsMarkup(string field, string text) => Assert.Equal(text, WebPageText.OfText(field));

    /// <summary>A field that is markup is reduced to the text a reader of it sees - highlights, entities, an element of its author's own - on one line.</summary>
    [Theory]
    [InlineData("How do I group a <span class=\"highlight\">List&lt;T&gt;</span>?", "How do I group a List<T>?")]
    [InlineData("Tom &amp; Jerry", "Tom & Jerry")]
    [InlineData("  plain   text\n here ", "plain text here")]
    [InlineData("<p>One</p><p>Two</p>", "One Two")]
    [InlineData("AT&T", "AT&T")]
    [InlineData("<p>Fixed <code>x</code> <g-emoji alias=\"tada\">!</g-emoji><o:p></o:p></p>", "Fixed x !")]
    public void AMarkupField_IsReducedToItsText(string field, string text) => Assert.Equal(text, WebPageText.OfMarkup(field));

    /// <summary>A page's parts: its title, each heading a reader sees in order, and its body's text.</summary>
    [Fact]
    public async Task APagesParts_AreItsTitleHeadingsAndText()
    {
        var parts = await WebPageText.PartsAsync(
            "<html><head><title> The   page </title></head><body><h1>First</h1><p>Body</p><div hidden><h2>Hidden</h2></div><h3>Third <em>one</em><span hidden>unseen</span></h3><h4> </h4><h2>Outer<div><h3>inner</h3></div></h2><h1>Getting<br>Started</h1><h2><table><tr><td>A</td><td>B</td></tr></table></h2></body></html>",
            TestContext.Current.CancellationToken);

        Assert.Equal("The page", parts.Title);
        Assert.Equal(["First", "Third one", "Outer inner", "Getting Started", "A B"], parts.Headings);
        Assert.Equal("First\nBody\nThird one\nOuter\ninner\nGetting\nStarted\nA B", parts.Text);
    }

    /// <summary>A field is read no further than its most, its cut marked: markup nested deep past it is never read.</summary>
    [Fact]
    public void AField_IsReadNoFurtherThanItsMost()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(WebPageText.OfMarkup(string.Concat(Enumerable.Repeat("<div>", 200_000)) + "x").Length <= WebPageText.MostFieldRead);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal(new string('x', WebPageText.MostFieldRead - 1) + TextCut.Ellipsis, WebPageText.OfMarkup(new string('x', WebPageText.MostFieldRead + 100)));
        Assert.Equal(new string('x', WebPageText.MostFieldRead - 1) + TextCut.Ellipsis, WebPageText.OfText(new string('x', WebPageText.MostFieldRead + 100)));
        Assert.Equal("a b" + TextCut.Ellipsis, WebPageText.OfMarkup("<p>a</p><p>b</p>" + new string(' ', WebPageText.MostFieldRead)));
        Assert.Equal(new string('x', WebPageText.MostFieldRead), WebPageText.OfText(new string('x', WebPageText.MostFieldRead)));
        Assert.Equal(string.Empty, WebPageText.OfMarkup("<p>" + new string(' ', WebPageText.MostFieldRead)));

        // Cut before a reference the cut would part, which a page's reader would make another character of.
        var parted = "<p>" + new string(' ', WebPageText.MostFieldRead - 1 - "<p>key abc&#x2".Length) + "key abc&#x2B;def</p>";
        Assert.Equal("key abc" + TextCut.Ellipsis, WebPageText.OfMarkup(parted));
        var words = "<p>" + new string(' ', WebPageText.MostFieldRead - 1 - "<p>Tom & Je".Length) + "Tom & Jerry</p>";
        Assert.Equal("Tom & Je" + TextCut.Ellipsis, WebPageText.OfMarkup(words));
        var tagged = "<p>" + new string(' ', WebPageText.MostFieldRead - 1 - "<p>key abc<".Length) + "key abc<wbr>def</p>";
        Assert.Equal("key abc" + TextCut.Ellipsis, WebPageText.OfMarkup(tagged));

        // Read to where the cut fell, in the order a page's reader reads it: a table's stray text before the table.
        const string Table = "<table><tr><td>zz</td></tr><span hidden>";
        const string Stray = "</span>ab<b>+c";
        var stray = Table + new string('y', WebPageText.MostFieldRead - 1 - Table.Length - Stray.Length) + Stray + "d</b></table>";
        Assert.Equal("ab+c" + TextCut.Ellipsis, WebPageText.OfMarkup(stray));

        // A cut that fell where no text is read reads nothing of the field.
        Assert.Equal(string.Empty, WebPageText.OfMarkup("<p>words</p><script>" + new string('x', WebPageText.MostFieldRead) + "</script>"));
    }

    /// <summary>A page's headings are read in the one walk of it, each node once, however deep its markup nests them.</summary>
    [Fact(Timeout = 30_000)]
    public async Task APagesHeadings_AreReadOnce_HoweverDeepTheyNest()
    {
        const int Deep = 60_000;
        var html = string.Concat(Enumerable.Repeat("<table><tr><td><h2>x</h2>", Deep));

        var parts = await WebPageText.PartsAsync(html, TestContext.Current.CancellationToken);

        Assert.Equal(Deep, parts.Headings.Count);
    }
}
