using DssHarness.Core.Web;
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
    /// A loc of 2,048 characters is past the sitemaps protocol's - which are under 2,048 - and is refused by name, beside why;
    /// one of 2,047 is a page; and an address of any length that no sitemap holds is an address.
    /// </summary>
    [Fact]
    public void ALocOf2048_IsRefusedByName()
    {
        var under = "https://docs.example.test/" + new string('x', 2047 - 26);
        var at = under + "y";
        var read = SitemapDocument.Read($"""
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>{under}</loc></url>
              <url><loc>{at}</loc></url>
              <url><loc>https://docs.example.test/{new string('é', 400)}</loc></url>
            </urlset>
            """, Address);

        Assert.Equal([under, "https://docs.example.test/" + Uri.EscapeDataString(new string('é', 400))], read.Pages.Select(page => page.Address));
        Assert.Equal([(at, "its <loc> is 2,048 characters, and the sitemaps protocol's are under 2,048")], read.Refused);
        Assert.Equal("https://docs.example.test/" + new string('x', 10_000), WebAddresses.Absolute("/" + new string('x', 10_000), Address));
    }

    /// <summary>
    /// A sitemap holds at most the protocol's 50,000 entries: those past them are counted, unread, and said - a listing the
    /// fill then knows is not whole.
    /// </summary>
    [Fact]
    public void ASitemap_PastTheProtocolsEntries_CountsTheRest()
    {
        var many = SitemapDocument.Read($"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(Enumerable.Range(0, SitemapDocument.MostEntries + 3).Select(at => $"<url><loc>/p{at}</loc></url>"))}</urlset>""", Address);

        Assert.Equal((SitemapDocument.MostEntries, "https://docs.example.test/p49999", 3), (many.Pages.Count, many.Pages[^1].Address, many.Unread));

        var index = SitemapDocument.Read($"""<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(Enumerable.Range(0, SitemapDocument.MostEntries + 1).Select(at => $"<sitemap><loc>/s{at}.xml</loc></sitemap>"))}</sitemapindex>""", Address);
        Assert.Equal((SitemapDocument.MostEntries, 1), (index.Sitemaps.Count, index.Unread));

        var lines = SitemapDocument.Read(string.Concat(Enumerable.Range(0, SitemapDocument.MostEntries + 1).Select(at => $"https://docs.example.test/l{at}\n")), Address);
        Assert.Equal((SitemapDocument.MostEntries, 1), (lines.Pages.Count, lines.Unread));
    }

    /// <summary>
    /// A lastmod is read by W3C Datetime's grammar - a year, a day, a minute, a fraction of a second of any length, a
    /// nanosecond's among them - and one no grammar reads is no lastmod, its page listed all the same.
    /// </summary>
    [Fact]
    public void ANanosecondLastmod_IsRead()
    {
        var read = SitemapDocument.Read("""
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>/a</loc><lastmod>2026-10-25T12:00:00.123456789+00:00</lastmod></url>
              <url><loc>/b</loc><lastmod>2026</lastmod></url>
              <url><loc>/c</loc><lastmod>2026-10-25T12:00Z</lastmod></url>
              <url><loc>/d</loc><lastmod>yesterday</lastmod></url>
              <url><loc>/e</loc><lastmod>1111111111111111111111111111111</lastmod></url>
            </urlset>
            """, Address);

        Assert.Equal(
            [("2026-10-25T12:00:00.123456789+00:00"), "2026", "2026-10-25T12:00Z", null, null],
            read.Pages.Select(page => page.LastModified));
        Assert.Equal(5, read.Pages.Count);
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
    [InlineData("<html><body>Not found", "its root is <html>, where a sitemap's is <urlset> or <sitemapindex>")]
    [InlineData("", "it is no XML (")]
    public void WhatIsNoSitemap_SaysWhy(string text, string why)
        => Assert.StartsWith(why, SitemapDocument.Read(text, Address).Problem, StringComparison.Ordinal);
}
