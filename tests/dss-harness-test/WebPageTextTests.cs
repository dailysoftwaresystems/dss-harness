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
}
