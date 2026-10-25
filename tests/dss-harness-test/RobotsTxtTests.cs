using DssHarness.Core.Web.Index;

namespace DssHarness.Tests;

/// <summary>A site's robots.txt as RFC 9309 reads it for this tool: its own group or every crawler's, the longest rule deciding, an allow winning a tie.</summary>
public sealed class RobotsTxtTests
{
    private const string Agent = "dssharness";

    /// <summary>The group that names this tool is its own, and every crawler's is not read beside it; groups that name it twice are one.</summary>
    [Fact]
    public void TheGroupNamingThisTool_IsItsOwn()
    {
        var robots = RobotsTxt.Read("""
            User-agent: *
            Disallow: /

            User-agent: other-bot
            User-agent: DSSHarness/2.0
            Disallow: /private/
            Crawl-delay: 3

            User-agent: dssharness
            Disallow: /drafts/
            """, Agent);

        Assert.True(robots.Allows("/guides/x"));
        Assert.False(robots.Allows("/private/x"));
        Assert.False(robots.Allows("/drafts/y"));
        Assert.Equal(TimeSpan.FromSeconds(3), robots.CrawlDelay);
    }

    /// <summary>A crawl delay is the first that reads as a number of seconds, none below zero, no longer than a day.</summary>
    [Theory]
    [InlineData("Crawl-delay: 1000000", 86400.0)]
    [InlineData("Crawl-delay: soon\nCrawl-delay: 5", 5.0)]
    [InlineData("Crawl-delay: -5", null)]
    public void ACrawlDelay_IsReadAsItCanBe(string lines, double? seconds)
        => Assert.Equal(seconds is { } said ? TimeSpan.FromSeconds(said) : null, RobotsTxt.Read($"User-agent: *\n{lines}\n", Agent).CrawlDelay);

    /// <summary>A line ends at a carriage return as at a line feed, or both.</summary>
    [Theory]
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void ALine_EndsAtEitherEnd(string end)
        => Assert.False(RobotsTxt.Read($"User-agent: *{end}Disallow: /private{end}", Agent).Allows("/private/x"));

    /// <summary>User-agent lines one after another open one group, whichever of them names this tool.</summary>
    [Fact]
    public void AgentsOneAfterAnother_AreOneGroup()
        => Assert.False(RobotsTxt.Read("User-agent: dssharness\nUser-agent: other-bot\nDisallow: /private/\n", Agent).Allows("/private/x"));

    /// <summary>With no group of its own, every crawler's is read.</summary>
    [Fact]
    public void WithNoGroupOfItsOwn_EveryCrawlersIsRead()
    {
        var robots = RobotsTxt.Read("User-agent: *\nDisallow: /search\nCrawl-delay: 10\n", Agent);

        Assert.False(robots.Allows("/search?q=x"));
        Assert.True(robots.Allows("/guides"));
        Assert.Equal(TimeSpan.FromSeconds(10), robots.CrawlDelay);
    }

    /// <summary>The longest rule that matches decides; an allow and a disallow of one length, the allow.</summary>
    [Theory]
    [InlineData("/docs/api/x", false)]
    [InlineData("/docs/api/public/x", true)]
    [InlineData("/docs/guide", true)]
    [InlineData("/tie", true)]
    public void TheLongestRule_Decides(string path, bool allowed)
    {
        var robots = RobotsTxt.Read("""
            User-agent: *
            Disallow: /docs/api/
            Allow: /docs/api/public/
            Disallow: /tie
            Allow: /tie
            """, Agent);

        Assert.Equal(allowed, robots.Allows(path));
    }

    /// <summary>'*' matches any run of characters, and a '$' at a rule's end the path's end; a rule matches where the path begins.</summary>
    [Theory]
    [InlineData("/a/b.pdf", false)]
    [InlineData("/a/b.pdf?x=1", true)]
    [InlineData("/print/x/page", false)]
    [InlineData("/x/print", true)]
    [InlineData("/ab", true)]
    [InlineData("/aab", false)]
    [InlineData("/abab", false)]
    [InlineData("/x/print/page", true)]
    [InlineData("/exact", false)]
    [InlineData("/exactly", true)]
    public void Wildcards_Match(string path, bool allowed)
    {
        var robots = RobotsTxt.Read("User-agent: *\nDisallow: /*.pdf$\nDisallow: /print*/page\nDisallow: /a*ab$\nDisallow: /*ab*ab\nDisallow: /exact$\n", Agent);

        Assert.Equal(allowed, robots.Allows(path));
    }

    /// <summary>A path and a rule are compared percent-encoded alike: a character past ASCII as its UTF-8, an encoding's digits in either case.</summary>
    [Theory]
    [InlineData("/café/menu")]
    [InlineData("/caf%c3%a9/menu")]
    [InlineData("/caf%C3%A9/menu")]
    public void PercentEncodings_AreComparedAlike(string path)
        => Assert.False(RobotsTxt.Read("User-agent: *\nDisallow: /café/\n", Agent).Allows(path));

    /// <summary>An empty Disallow refuses nothing; robots.txt itself is always allowed; a comment and an unknown record are passed over.</summary>
    [Fact]
    public void AnEmptyDisallow_RefusesNothing()
    {
        Assert.True(RobotsTxt.Read("User-agent: *\nDisallow:\n", Agent).Allows("/anything"));
        Assert.True(RobotsTxt.Read("User-agent: *\nDisallow: /\n", Agent).Allows("/robots.txt"));

        var robots = RobotsTxt.Read("User-agent: * # every\nNoindex: /x\nDisallow: /a # not a\n", Agent);
        Assert.False(robots.Allows("/a/b"));
        Assert.True(robots.Allows("/x"));
    }

    /// <summary>Sitemaps are named anywhere, in no group, in their order; a crawl delay that is no number is none.</summary>
    [Fact]
    public void Sitemaps_AreNamedAnywhere()
    {
        var robots = RobotsTxt.Read("Sitemap: https://a.test/one.xml\nUser-agent: *\nCrawl-delay: soon\nSitemap: https://a.test/two.xml.gz\nDisallow: /x\n", Agent);

        Assert.Equal(["https://a.test/one.xml", "https://a.test/two.xml.gz"], robots.Sitemaps);
        Assert.Null(robots.CrawlDelay);
        Assert.False(robots.Allows("/x"));
    }

    /// <summary>A rule before any group, and a file of no group, refuse nothing.</summary>
    [Fact]
    public void ARuleBeforeAnyGroup_RefusesNothing()
    {
        Assert.True(RobotsTxt.Read("Disallow: /\n", Agent).Allows("/x"));
        Assert.True(RobotsTxt.AllowAll.Allows("/x"));
    }

    /// <summary>A rule of many stars matches in time, a miss included.</summary>
    [Fact]
    public void ARuleOfManyStars_MatchesInTime()
        => Assert.True(RobotsTxt.Matches("/" + string.Concat(Enumerable.Repeat("*a", 50)) + "b$", "/" + new string('a', 5000) + "b"));

    /// <summary>Rules a site writes as long as it likes decide an address in time - each rule one pass of the path.</summary>
    [Fact]
    public void ManyLongRules_DecideInTime()
    {
        var robots = RobotsTxt.Read("User-agent: *\n" + string.Concat(Enumerable.Range(0, 2_000).Select(at => $"Disallow: /*{new string('a', 200)}{at}b\n")), Agent);
        var path = "/" + new string('a', 2_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (var at = 0; at < 50; at++)
        {
            Assert.True(robots.Allows(path));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.False(robots.Allows(path + "0b"));
    }

    /// <summary>A robots.txt is read to its most, the last whole line within it, whichever end its lines have: a rule past it is none.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    public void ARobotsTxt_IsReadToItsMost(string end)
    {
        var robots = RobotsTxt.Read($"User-agent: *{end}Disallow: /early{end}# {new string('x', RobotsTxt.MostRead)}{end}Disallow: /late{end}", Agent);

        Assert.False(robots.Allows("/early"));
        Assert.True(robots.Allows("/late"));

        // A line the most cuts is no rule: what it would refuse cut short, it does not.
        var head = $"User-agent: *{end}# ";
        var cut = RobotsTxt.Read(head + new string('x', RobotsTxt.MostRead - head.Length - 13) + $"{end}Disallow: /cutting{end}", Agent);
        Assert.True(cut.Allows("/cat"));
    }
}
