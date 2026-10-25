using DssHarness.Core.Web.Index;

namespace DssHarness.Tests;

/// <summary>A sitemap as the sitemaps protocol writes one: a urlset, an index of sitemaps, or a text list of addresses.</summary>
public sealed class SitemapDocumentTests
{
    private const string Address = "https://docs.example.test/sitemap.xml";

    /// <summary>A urlset gives each page's address and lastmod, a relative one read against the sitemap, an address that is none left out.</summary>
    [Fact]
    public void AUrlset_GivesItsPages()
    {
        var read = SitemapDocument.Read("""
            <?xml version="1.0" encoding="UTF-8"?>
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc> https://docs.example.test/a </loc><lastmod>2026-01-01</lastmod></url>
              <url><loc>/b</loc></url>
              <url><loc>mailto:x@example.test</loc></url>
              <url><lastmod>2026-01-02</lastmod></url>
              <url><loc>https://docs.example.test/c</loc><lastmod> </lastmod></url>
              <image><loc>https://docs.example.test/picture.png</loc></image>
            </urlset>
            """, Address);

        Assert.Null(read.Problem);
        Assert.Equal([("https://docs.example.test/a", (string?)"2026-01-01"), ("https://docs.example.test/b", null), ("https://docs.example.test/c", null)], read.Pages);
        Assert.Empty(read.Sitemaps);
    }

    /// <summary>
    /// A sitemap is read to the sitemaps protocol's most: 50,000 entries, an address of 2,048 characters - a longer one
    /// names none - and a lastmod no longer than a date and time is.
    /// </summary>
    [Fact]
    public void ASitemap_IsReadToItsMost()
    {
        var longest = "https://docs.example.test/" + new string('x', 2048 - 26);
        var read = SitemapDocument.Read($"""
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>{longest}</loc><lastmod>{new string('1', SitemapDocument.MostLastModified + 1)}</lastmod></url>
              <url><loc>{longest}y</loc></url>
              <url><loc>https://docs.example.test/{new string('é', 400)}</loc></url>
              <url><loc>/{new string('é', 400)}</loc></url>
            </urlset>
            """, Address);

        Assert.Equal([(longest, (string?)null)], read.Pages);

        var many = SitemapDocument.Read($"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(Enumerable.Range(0, SitemapDocument.MostEntries + 1).Select(at => $"<url><loc>/p{at}</loc></url>"))}</urlset>""", Address);

        Assert.Equal((SitemapDocument.MostEntries, "https://docs.example.test/p49999"), (many.Pages.Count, many.Pages[^1].Address));

        var index = SitemapDocument.Read($"""<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(Enumerable.Range(0, SitemapDocument.MostEntries + 1).Select(at => $"<sitemap><loc>/s{at}.xml</loc></sitemap>"))}</sitemapindex>""", Address);
        Assert.Equal(SitemapDocument.MostEntries, index.Sitemaps.Count);

        var lines = SitemapDocument.Read(string.Concat(Enumerable.Range(0, SitemapDocument.MostEntries + 1).Select(at => $"https://docs.example.test/l{at}\n")), Address);
        Assert.Equal(SitemapDocument.MostEntries, lines.Pages.Count);
    }

    /// <summary>
    /// A sitemap is read as it streams: of a urlset of many children that are no entries, or of a text of many lines that
    /// are no addresses, nothing is kept past what was read.
    /// </summary>
    [Fact]
    public void ASitemap_IsReadAsItStreams_KeepingNothingPastWhatItRead()
    {
        const int Many = 2_000_000;
        var children = $"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>/a</loc></url>{string.Concat(Enumerable.Repeat("<x/>", Many))}</urlset>""";
        var lines = string.Concat(Enumerable.Repeat("a\n", Many));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var read = SitemapDocument.Read(children, Address);
        var notLines = SitemapDocument.Read(lines, Address);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal([("https://docs.example.test/a", (string?)null)], read.Pages);
        Assert.StartsWith("it is no XML (", notLines.Problem, StringComparison.Ordinal);
        Assert.True(allocated < 32 * 1024 * 1024, $"allocated {allocated:N0} bytes");
    }

    /// <summary>An index names its sitemaps.</summary>
    [Fact]
    public void AnIndex_NamesItsSitemaps()
    {
        var read = SitemapDocument.Read("""
            <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <sitemap><loc>https://docs.example.test/one.xml.gz</loc></sitemap>
              <sitemap><loc>two.xml</loc></sitemap>
              <other><loc>https://docs.example.test/other.xml</loc></other>
            </sitemapindex>
            """, Address);

        Assert.Equal(["https://docs.example.test/one.xml.gz", "https://docs.example.test/two.xml"], read.Sitemaps);
        Assert.Empty(read.Pages);
    }

    /// <summary>A relative address read against a sitemap that is no web address is none.</summary>
    [Fact]
    public void ARelativeAddress_OfASitemapThatIsNoWebAddress_IsNone()
        => Assert.Empty(SitemapDocument.Read("""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>/b</loc></url></urlset>""", "file:///sitemap.xml").Pages);

    /// <summary>A text sitemap is an address a line.</summary>
    [Fact]
    public void AText_IsAnAddressALine()
        => Assert.Equal(
            [("https://docs.example.test/a", (string?)null), ("https://docs.example.test/b", null), ("https://docs.example.test/c", null)],
            SitemapDocument.Read("https://docs.example.test/a\r\n\r\nhttps://docs.example.test/b\rhttps://docs.example.test/c\n", Address).Pages);

    /// <summary>What is no sitemap says why.</summary>
    [Theory]
    [InlineData("<html><body/></html>", "its root is <html>, where a sitemap's is <urlset> or <sitemapindex>")]
    [InlineData("<html><body>Not found", "it is no XML (")]
    [InlineData("", "it is no XML (")]
    public void WhatIsNoSitemap_SaysWhy(string text, string why)
        => Assert.StartsWith(why, SitemapDocument.Read(text, Address).Problem, StringComparison.Ordinal);
}
