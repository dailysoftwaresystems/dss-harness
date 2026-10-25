using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Web;
using DssHarness.Core.Web.Index;
using DssHarness.Core.Web.Search;
using DssHarness.Core.Web.Search.Kinds;

namespace DssHarness.Tests;

/// <summary>
/// A site's index filled through the harness's fetch over an endpoint a test answers: robots.txt read and obeyed, its
/// sitemaps (an index, gzip) read for the pages the source includes, each request at the slower of the crawl delay and the
/// source's pace, a search answering from what is held and saying how much, a refill reading what moved first, and a
/// site's limit holding every fill off.
/// </summary>
public sealed class WebIndexesTests
{
    private const string Site = "https://docs.example.test";

    private static readonly IFileSystem Files = new PhysicalFileSystem(FilePermissionsFactory.Create());

    /// <summary>A gate that allows every address, records none, and gives every request its turn at once.</summary>
    private static readonly WebFetchGate Open = WebFetchGate.Open with { Pace = _ => Task.FromResult<DateTimeOffset?>(null) };

    /// <summary>
    /// A first search begins the fill and says the sitemaps are being read; once filled, the pages robots.txt allows and the
    /// source includes are found - none other asked - each request after the first at the crawl delay, every request a line of the fill's record.
    /// </summary>
    [Fact]
    public async Task AFirstSearch_FillsTheIndex_AsTheSiteAllows()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        var first = harness.Indexes.Search(directory.Path, settings, "tokenizer", 5, Open);

        Assert.True(first.Filling);
        Assert.Empty(first.Hits);

        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "tokenizer", 5, Open);

        Assert.False(answer.Filling);
        Assert.Equal(2, answer.Held);
        Assert.Equal(2, answer.Known);
        Assert.Null(answer.Problem);
        Assert.Equal("indexed 2 of 2 pages of docs.example.test", answer.Said);
        Assert.Equal([$"{Site}/guides/alpha", $"{Site}/guides/beta"], answer.Hits.Select(hit => hit.Address));
        Assert.Equal("Alpha guide", answer.Hits[0].Title);

        Assert.Equal(
            ["/robots.txt", "/sitemap-index.xml", "/sitemap-guides.xml.gz", "/sitemap-other.xml", "/guides/alpha", "/guides/beta"],
            site.Asked.Select(asked => asked.Address.AbsolutePath));
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(4), 5), harness.Waits);
        Assert.Equal(6, File.ReadAllLines(Path.Combine(directory.Path, SitemapFill.RecordFileName)).Length);
        Assert.All(site.Asked, asked => Assert.StartsWith("dssharness/", asked.UserAgent, StringComparison.Ordinal));
    }

    /// <summary>
    /// The source's turn is the run's gate's to give, asked before every request - a redirect's too - once the request is
    /// allowed; a gate that gives none is refused, for a fill is never unpaced.
    /// </summary>
    [Fact]
    public async Task TheSourcesTurn_IsAskedBeforeEveryRequest()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        site.Redirects["/guides/beta"] = "/guides/beta-moved";
        using var harness = new Harness(site);
        var turns = 0;
        var gate = Open with
        {
            Pace = _ =>
            {
                Interlocked.Increment(ref turns);
                return Task.FromResult<DateTimeOffset?>(null);
            },
        };

        harness.Indexes.Search(directory.Path, Settings(include: ["/guides/**"]), "x", 5, gate);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(site.Asked.Count, turns);
        Assert.Equal(7, turns);
        Assert.Throws<ArgumentException>(() => harness.Indexes.Search(directory.Path, Settings(), "x", 5, WebFetchGate.Open));
    }

    /// <summary>An index holds at most maxPages, the first its sitemaps list; with no pattern, every page the site allows.</summary>
    [Fact]
    public async Task AnIndex_HoldsAtMostMaxPages()
    {
        using var directory = new TempDirectory();
        using var harness = new Harness(new SiteDouble());

        harness.Indexes.Search(directory.Path, Settings(maxPages: 1), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(maxPages: 1), "guide", 5, Open);

        Assert.Equal((1, (int?)1), (answer.Held, answer.Known));
        Assert.Equal([$"{Site}/guides/alpha"], answer.Hits.Select(hit => hit.Address));
    }

    /// <summary>A page the gate refuses is never asked, and never held.</summary>
    [Fact]
    public async Task APageTheGateRefuses_IsNeverAsked()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var gate = Open with
        {
            Decide = address => Task.FromResult(address.EndsWith("/beta", StringComparison.Ordinal) ? new AgentPolicyAnswer.Denied("Web(x)", AgentPolicySource.Config, "refused") : AgentPolicyAnswer.Allow),
        };

        harness.Indexes.Search(directory.Path, Settings(include: ["/guides/**"]), "x", 5, gate);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.DoesNotContain(site.Asked, asked => asked.Address.AbsolutePath == "/guides/beta");
        Assert.Equal(1, harness.Indexes.Search(directory.Path, Settings(include: ["/guides/**"]), "x", 5, gate).Held);
    }

    /// <summary>A refill past refreshDays reads the page whose lastmod moved first, then a new one; one unmoved is not read, one no longer listed is dropped.</summary>
    [Fact]
    public async Task ARefill_ReadsWhatMovedFirst()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.False(harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Filling);

        site.Guides = """
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://docs.example.test/guides/gamma</loc><lastmod>2026-03-01</lastmod></url>
              <url><loc>https://docs.example.test/guides/beta</loc><lastmod>2026-02-02</lastmod></url>
            </urlset>
            """;
        site.Asked.Clear();
        harness.Clock.Advance(TimeSpan.FromDays(8));

        Assert.True(harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Filling);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(["/guides/beta", "/guides/gamma"], site.Asked.Select(asked => asked.Address.AbsolutePath).Where(path => path.StartsWith("/guides/", StringComparison.Ordinal)));

        var answer = harness.Indexes.Search(directory.Path, settings, "guide", 5, Open);
        Assert.Equal([$"{Site}/guides/beta", $"{Site}/guides/gamma"], answer.Hits.Select(hit => hit.Address).Order(StringComparer.Ordinal));
    }

    /// <summary>A site that asks to be left alone ends the fill, keeps what was read, and holds every fill off until the time it gave.</summary>
    [Fact]
    public async Task ASitesLimit_HoldsTheFillOff()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble { LimitedAt = "/guides/beta" };
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "alpha", 5, Open);

        Assert.False(answer.Filling);
        Assert.Equal(1, answer.Held);
        Assert.Equal(harness.Clock.GetUtcNow() + TimeSpan.FromSeconds(600), answer.PausedUntil);
        Assert.Equal("Alpha guide", Assert.Single(answer.Hits).Title);

        site.Asked.Clear();
        harness.Clock.Advance(TimeSpan.FromSeconds(601));
        site.LimitedAt = null;

        Assert.True(harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Filling);
        await harness.Indexes.FilledAsync(directory.Path);
        Assert.Equal(2, harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Held);
    }

    /// <summary>A robots.txt that cannot be read reads nothing of the site, and says why; one that is not there refuses nothing.</summary>
    [Theory]
    [InlineData(500, 0, "its robots.txt could not be read (it answered 500), and a site that cannot say what it allows is read nothing of (RFC 9309, 2.3.1.4)")]
    [InlineData(404, 3, null)]
    [InlineData(403, 3, null)]
    [InlineData(401, 3, null)]
    public async Task ARobotsTxtThatCannotBeRead_ReadsNothing(int status, int held, string? problem)
    {
        using var directory = new TempDirectory();
        using var harness = new Harness(new SiteDouble { RobotsStatus = status });

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);

        Assert.Equal(held, answer.Held);
        Assert.Equal(problem, answer.Problem);
        Assert.False(answer.Filling);

        if (problem is not null)
        {
            var told = new SitemapKind().Read(WebSearchFixtures.Source("site", $$"""{ "kind": "sitemap", "site": "{{Site}}" }"""), WebSearchFound.None, new WebSearchResponse.Indexed(answer));
            Assert.Equal(WebSearchOutcome.Failed, told.Outcome);
            Assert.Equal($"its index holds no page: {problem}", told.Why);
        }
    }

    /// <summary>No sitemap that can be read is a fill stopped, saying the first that could not.</summary>
    [Fact]
    public async Task NoSitemapThatCanBeRead_StopsTheFill()
    {
        using var directory = new TempDirectory();
        using var harness = new Harness(new SiteDouble { IndexStatus = 404 });

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal("no sitemap of it could be read, the first 'https://docs.example.test/sitemap-index.xml': it answered 404", harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open).Problem);
    }

    /// <summary>
    /// What the site says is HTML is read as sent: its robots.txt's rules and crawl delay held - never reduced to a page's
    /// text, which would refuse nothing - and a sitemap's pages listed.
    /// </summary>
    [Fact]
    public async Task WhatTheSiteSaysIsHtml_IsReadAsSent()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble { RobotsType = "text/html", OtherType = "text/html", IndexType = "text/html" };
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(), "guide", 5, Open);

        Assert.Null(answer.Problem);
        Assert.Equal(3, answer.Held);
        Assert.DoesNotContain(site.Asked, asked => asked.Address.AbsolutePath.StartsWith("/private/", StringComparison.Ordinal));
        Assert.All(harness.Waits, wait => Assert.Equal(TimeSpan.FromSeconds(4), wait));
    }

    /// <summary>
    /// A refill reads what is due alone, in its order - a page not yet read before one with no lastmod read longer ago than
    /// the index may be old - and never one whose lastmod has not moved.
    /// </summary>
    [Fact]
    public async Task ARefill_ReadsWhatIsDue_AndNothingElse()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Guides = site.Guides.Replace("</urlset>", "  <url><loc>https://docs.example.test/guides/gamma</loc><lastmod>2026-03-01</lastmod></url>\n</urlset>", StringComparison.Ordinal);
        site.Asked.Clear();
        harness.Clock.Advance(TimeSpan.FromDays(8));

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(["/guides/gamma", "/blog/zeta"], PagesAsked(site));
        Assert.Equal(4, harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open).Held);
    }

    /// <summary>A page that answers it is gone is dropped, though its sitemap still lists it.</summary>
    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task APageThatIsGone_IsDropped(int status)
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Guides = site.Guides.Replace("alpha</loc><lastmod>2026-01-01", "alpha</loc><lastmod>2026-05-01", StringComparison.Ordinal);
        site.Statuses["/guides/alpha"] = status;
        harness.Clock.Advance(TimeSpan.FromDays(8));

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "tokenizer", 5, Open);

        Assert.Equal(1, answer.Held);
        Assert.Equal([$"{Site}/guides/beta"], answer.Hits.Select(hit => hit.Address));

        // Remembered as one that could not be read, it is not asked again before its time, though the index is filled again.
        site.Asked.Clear();
        harness.Indexes.Search(directory.Path, Settings(include: ["/guides/**"], maxPages: 99), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Empty(PagesAsked(site));
    }

    /// <summary>A page that could not be read, and that the site no longer lists, is forgotten.</summary>
    [Fact]
    public async Task APageThatCouldNotBeRead_AndIsNoLongerListed_IsForgotten()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        site.Statuses["/guides/beta"] = 503;
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Guides = site.Guides.Replace("<url><loc>https://docs.example.test/guides/beta</loc><lastmod>2026-02-01</lastmod></url>", string.Empty, StringComparison.Ordinal);
        harness.Clock.Advance(TimeSpan.FromDays(8));

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var index = new WebIndex(Files, directory.Path, Site);
        index.Refresh();

        Assert.Empty(index.Failures());
    }

    /// <summary>A listing read whole that lists no page the source may read drops every page held, and says why.</summary>
    [Fact]
    public async Task AWholeListingOfNoPageItMayRead_DropsEveryPageHeld()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);
        Assert.Equal(2, harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Held);

        site.Guides = """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"></urlset>""";
        harness.Clock.Advance(TimeSpan.FromDays(8));

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        Assert.Equal((0, (int?)0), (answer.Held, answer.Known));
        Assert.Equal("its sitemaps list 1 page(s), none the source may read: 1 left unread by its robots.txt or the source's include and exclude", answer.Problem);
    }

    /// <summary>
    /// A listing read short still holds the index to maxPages: beside the pages held it lists, of those it does not list the
    /// ones read last are kept.
    /// </summary>
    [Fact]
    public async Task AListingReadShort_HoldsTheIndexToItsMost()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        site.Answering = path =>
        {
            if (path == "/guides/beta")
            {
                harness.Clock.Advance(TimeSpan.FromMinutes(1));
            }
        };

        harness.Indexes.Search(directory.Path, Settings(maxPages: 3), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);
        Assert.Equal(3, harness.Indexes.Search(directory.Path, Settings(maxPages: 3), "x", 5, Open).Held);

        site.Statuses["/sitemap-guides.xml.gz"] = 503;

        harness.Indexes.Search(directory.Path, Settings(maxPages: 2), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var index = new WebIndex(Files, directory.Path, Site);
        index.Refresh();

        Assert.Equal([$"{Site}/blog/zeta", $"{Site}/guides/beta"], index.Pages().Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A sitemap that fails for longer than the index may be old costs the pages held their place: the pages the listing
    /// names are read, the index following it still.
    /// </summary>
    [Fact]
    public async Task ASitemapFailingPastTheIndexsAge_GivesThePagesHeldUpToTheListing()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var settings = Settings(maxPages: 2);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Statuses["/sitemap-guides.xml.gz"] = 503;
        harness.Clock.Advance(TimeSpan.FromDays(8));
        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);
        Assert.DoesNotContain("/blog/zeta", PagesAsked(site));

        harness.Clock.Advance(TimeSpan.FromDays(8));
        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var index = new WebIndex(Files, directory.Path, Site);
        index.Refresh();

        Assert.Equal(2, index.Held);
        Assert.Contains($"{Site}/blog/zeta", index.Pages().Keys);
    }

    /// <summary>
    /// A listing short by a rule whose pages could not be read is said as the pages', as a whole listing's are, and asked
    /// again soon - not held off for the rule.
    /// </summary>
    [Fact]
    public async Task AListingShortByARule_WhosePagesFailed_SaysThePages()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble { Robots = "User-agent: *\nDisallow: /private/\nDisallow: /sitemap-other\nSitemap: https://docs.example.test/sitemap-index.xml\n" };
        site.Statuses["/guides/alpha"] = 503;
        site.Statuses["/guides/beta"] = 503;
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        Assert.StartsWith($"none of the 2 page(s) its sitemaps list that it may read could be read, the first '{Site}/guides/alpha': it answered 503", answer.Problem, StringComparison.Ordinal);
        Assert.Equal(harness.Clock.GetUtcNow() + WebIndexState.AfterProblem, answer.PausedUntil);
    }

    /// <summary>A sitemap unread for once costs none of what the index holds: no page held is dropped, and none is read in its place.</summary>
    [Fact]
    public async Task ASitemapUnreadForOnce_CostsNoneOfWhatTheIndexHolds()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var settings = Settings(maxPages: 2);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Statuses["/sitemap-guides.xml.gz"] = 503;
        harness.Clock.Advance(TimeSpan.FromDays(8));
        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var index = new WebIndex(Files, directory.Path, Site);
        index.Refresh();

        Assert.Equal([$"{Site}/guides/alpha", $"{Site}/guides/beta"], index.Pages().Keys.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("/blog/zeta", PagesAsked(site));
    }

    /// <summary>
    /// A listing cut at maxPages is whole, however many sitemaps wait behind it; one short by a rule - a sitemap its
    /// robots.txt leaves unread - ends, saying so, and is read again when the index is old.
    /// </summary>
    [Fact]
    public async Task AListingCutAtMaxPages_IsWhole_AndOneShortByARule_Ends()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble
        {
            Index = $"""<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><sitemap><loc>https://docs.example.test/sitemap-guides.xml.gz</loc></sitemap>{string.Concat(Enumerable.Range(0, SitemapFill.MostSitemaps + 50).Select(at => $"<sitemap><loc>https://docs.example.test/empty-{at}.xml</loc></sitemap>"))}</sitemapindex>""",
        };
        using var harness = new Harness(site);
        var settings = Settings(maxPages: 2);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Null(harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Problem);

        using var other = new TempDirectory();
        var ruled = new SiteDouble { Robots = "User-agent: *\nDisallow: /private/\nDisallow: /sitemap-other\nSitemap: https://docs.example.test/sitemap-index.xml\n" };
        using var rules = new Harness(ruled);

        rules.Indexes.Search(other.Path, Settings(), "x", 5, Open);
        await rules.Indexes.FilledAsync(other.Path);

        rules.Clock.Advance(TimeSpan.FromHours(2));
        var answer = rules.Indexes.Search(other.Path, Settings(), "x", 5, Open);

        Assert.False(answer.Filling);
        Assert.Equal(2, answer.Held);
        Assert.StartsWith("1 of its sitemaps the source may not read, the first 'https://docs.example.test/sitemap-other.xml': ", answer.Problem, StringComparison.Ordinal);
    }

    /// <summary>A fill that finds its record past its most begins it anew: the record holds the latest fills, never every one.</summary>
    [Fact]
    public async Task AFillsRecord_IsBegunAnewPastItsMost()
    {
        using var directory = new TempDirectory();
        var record = Path.Combine(directory.Path, SitemapFill.RecordFileName);
        File.WriteAllText(record, string.Concat(Enumerable.Repeat(new string('x', 1023) + "\n", (int)(SitemapFill.MostRecordBytes / 1024) + 1)));
        using var harness = new Harness(new SiteDouble());

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var lines = File.ReadAllLines(record);
        Assert.DoesNotContain(lines, line => line.StartsWith('x'));
        Assert.Contains(lines, line => line.Contains("/robots.txt", StringComparison.Ordinal));
    }

    /// <summary>A sitemap longer than the fetch's own most is read to the sitemaps protocol's.</summary>
    [Fact]
    public async Task ASitemapLongerThanTheFetchsOwnMost_IsReadToTheProtocols()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        site.Guides = site.Guides.Replace("</urlset>", $"<!-- {new string('x', (int)HarnessFetch.MostBytes)} --></urlset>", StringComparison.Ordinal);
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        Assert.Equal((2, (string?)null), (answer.Held, answer.Problem));
    }

    /// <summary>
    /// A fill that could ask no page - each waiting, as one that could not be read does, a refused one among them - says the
    /// first's own failure, whatever stopped a fill between, and waits for the first to be asked again.
    /// </summary>
    [Fact]
    public async Task AFillThatCouldAskNoPage_SaysTheFirstsFailure_AndWaitsForTheFirst()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        site.Redirects["/guides/alpha"] = "/private/alpha";
        site.Statuses["/guides/beta"] = 404;
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);
        var first = harness.Clock.GetUtcNow();

        // The second fails later than the first: the wait is the first's.
        site.Answering = path =>
        {
            if (path == "/guides/beta")
            {
                harness.Clock.Advance(TimeSpan.FromMinutes(1));
            }
        };

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        // A fill between that stops short for another reason.
        site.RobotsStatus = 503;
        harness.Clock.Advance(TimeSpan.FromHours(2));
        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.RobotsStatus = 200;
        harness.Clock.Advance(TimeSpan.FromHours(2));
        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "x", 5, Open);

        Assert.Equal(2, PagesAsked(site).Count(path => path.StartsWith("/guides/", StringComparison.Ordinal)));
        Assert.StartsWith($"none of the 2 page(s) its sitemaps list that it may read could be read, the first '{Site}/guides/alpha': refused: ", answer.Problem, StringComparison.Ordinal);
        Assert.EndsWith("; each waits as long as a page read does before it is asked again", answer.Problem, StringComparison.Ordinal);
        Assert.Equal(first + TimeSpan.FromDays(7), answer.PausedUntil);
    }

    /// <summary>
    /// A listing read short keeps the pages held it does not list within maxPages, never giving their room to a failure it
    /// does not list, which holds no page and is forgotten.
    /// </summary>
    [Fact]
    public async Task AListingReadShort_KeepsPagesHeld_BeforeFailuresItDoesNotList()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble
        {
            Guides = """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://docs.example.test/guides/alpha</loc></url></urlset>""",
            Other = """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://docs.example.test/blog/zeta</loc></url><url><loc>https://docs.example.test/blog/broken</loc></url></urlset>""",
        };
        site.Statuses["/blog/broken"] = 500;
        using var harness = new Harness(site);
        var settings = Settings(maxPages: 3);

        // The page that could not be read fails after the pages held were read.
        site.Answering = path =>
        {
            if (path == "/blog/broken")
            {
                harness.Clock.Advance(TimeSpan.FromMinutes(1));
            }
        };

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Guides = """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://docs.example.test/guides/alpha</loc></url><url><loc>https://docs.example.test/guides/beta</loc></url></urlset>""";
        site.Statuses["/sitemap-other.xml"] = 503;
        harness.Clock.Advance(TimeSpan.FromDays(8));
        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var index = new WebIndex(Files, directory.Path, Site);
        index.Refresh();

        Assert.Equal([$"{Site}/blog/zeta", $"{Site}/guides/alpha", $"{Site}/guides/beta"], index.Pages().Keys.Order(StringComparer.Ordinal));
        Assert.Empty(index.Failures());
    }

    /// <summary>
    /// A listing whose sitemaps are more than a fill reads - named by an index or by robots.txt - is not read whole: the
    /// pages held it does not list are kept.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SitemapsPastTheMostAFillReads_AreAListingNotReadWhole(bool byIndex)
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var empties = Enumerable.Range(0, SitemapFill.MostSitemaps).Select(at => $"https://docs.example.test/empty-{at}.xml").ToList();

        if (byIndex)
        {
            site.Index = $"""<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(empties.Select(empty => $"<sitemap><loc>{empty}</loc></sitemap>"))}<sitemap><loc>https://docs.example.test/sitemap-guides.xml.gz</loc></sitemap></sitemapindex>""";
        }
        else
        {
            site.Robots = $"User-agent: *\n{string.Concat(empties.Select(empty => $"Sitemap: {empty}\n"))}Sitemap: https://docs.example.test/sitemap-guides.xml.gz\n";
        }

        harness.Clock.Advance(TimeSpan.FromDays(8));
        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        Assert.Equal(2, answer.Held);
        Assert.EndsWith($"; it names more sitemaps than the {SitemapFill.MostSitemaps} a fill reads, and those past them were not read", answer.Problem, StringComparison.Ordinal);
    }

    /// <summary>A robots.txt longer than the fetch reads is read to its head: its first rules held, the line its cut ends in none.</summary>
    [Fact]
    public async Task ARobotsTxtLongerThanTheFetchReads_IsReadToItsHead()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble { Robots = $"User-agent: *\nDisallow: /guides/alpha\n# {new string('x', (int)HarnessFetch.MostBytes)}\nDisallow: /\n" };
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Null(harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Problem);
        Assert.Equal(["/guides/beta"], PagesAsked(site));
    }

    /// <summary>
    /// A count is kept, and said, of the settings it was counted under alone: settings changed and changed back count
    /// afresh, saying none until they have.
    /// </summary>
    [Fact]
    public async Task ACount_IsKeptOfItsOwnSettingsAlone()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var blog = Settings(include: ["/blog/**"]);

        harness.Indexes.Search(directory.Path, blog, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Statuses["/sitemap-other.xml"] = 503;
        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);
        Assert.Equal(2, harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open).Known);

        Assert.Null(harness.Indexes.Search(directory.Path, blog, "x", 5, Open).Known);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(0, harness.Indexes.Search(directory.Path, blog, "x", 5, Open).Known);
    }

    /// <summary>A fill held off before it gets as far as one that stopped short leaves why that one did, and says it is held off.</summary>
    [Fact]
    public async Task AFillHeldOff_LeavesWhyOneStoppedShort()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        site.Statuses["/sitemap-other.xml"] = 503;
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.LimitedAt = "/robots.txt";
        harness.Clock.Advance(TimeSpan.FromHours(2));

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);

        Assert.False(answer.Filling);
        Assert.StartsWith("1 of its sitemaps could not be read", answer.Problem, StringComparison.Ordinal);
        Assert.Contains("; reading it is held off: the site asks to be left alone until ", answer.Said, StringComparison.Ordinal);
    }

    /// <summary>Sitemaps that list no page the source may read stop the fill short, saying what kept each from it.</summary>
    [Fact]
    public async Task SitemapsThatListNoPageItMayRead_StopTheFillShort_SayingWhy()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble
        {
            Guides = """<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>https://elsewhere.example.test/guides/x</loc></url><url><loc>https://docs.example.test/private/y</loc></url></urlset>""",
        };
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "x", 5, Open);

        Assert.Equal(0, answer.Held);
        Assert.Equal($"its sitemaps list 3 page(s), none the source may read: 1 of another site than '{Site}', 2 left unread by its robots.txt or the source's include and exclude", answer.Problem);
        Assert.Empty(PagesAsked(site));

        // Beside a sitemap that could not be read, each is said.
        site.Statuses["/sitemap-other.xml"] = 503;
        harness.Clock.Advance(TimeSpan.FromHours(2));

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(
            $"1 of its sitemaps could not be read, the first '{Site}/sitemap-other.xml': it answered 503; and its sitemaps list 2 page(s), none the source may read: 1 of another site than '{Site}', 1 left unread by its robots.txt or the source's include and exclude",
            harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Problem);
    }

    /// <summary>
    /// A fill none of whose pages could be read stops short, saying the first - gone, failed, or refused where a redirect
    /// led - however whole its reading of them.
    /// </summary>
    [Theory]
    [InlineData(404, "it answered 404")]
    [InlineData(400, "it answered 400")]
    [InlineData(null, "refused: 'https://docs.example.test/private/alpha' is an address the site's robots.txt")]
    public async Task AFillThatCouldReadNoPage_StopsShort_SayingTheFirst(int? alpha, string why)
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();

        if (alpha is { } status)
        {
            site.Statuses["/guides/alpha"] = status;
        }
        else
        {
            site.Redirects["/guides/alpha"] = "/private/alpha";
        }

        site.Statuses["/guides/beta"] = 400;
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.StartsWith(
            $"none of the 2 page(s) its sitemaps list that it may read could be read, the first '{Site}/guides/alpha': {why}",
            harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Problem,
            StringComparison.Ordinal);
    }

    /// <summary>A robots.txt and sitemaps their server calls bytes are text by their names, and read.</summary>
    [Fact]
    public async Task ARobotsTxtAndSitemapsSaidToBeBytes_AreRead()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble { RobotsType = "application/octet-stream", OtherType = "binary/octet-stream", IndexType = "application/octet-stream" };
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(), "guide", 5, Open);

        Assert.Null(answer.Problem);
        Assert.Equal(3, answer.Held);
        Assert.DoesNotContain(site.Asked, asked => asked.Address.AbsolutePath.StartsWith("/private/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Settings changed count what they read: the pages held that they read, and of a listing read short under them, only
    /// what it listed - never the count a whole listing gave under the settings before.
    /// </summary>
    [Fact]
    public async Task ChangedSettings_CountWhatTheyRead()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);
        Assert.Equal((3, (int?)3), (harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open) is var whole ? (whole.Held, whole.Known) : default));

        site.Statuses["/sitemap-other.xml"] = 503;
        var narrowed = Settings(include: ["/guides/**"]);

        Assert.Equal(2, harness.Indexes.Search(directory.Path, narrowed, "x", 5, Open).Held);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, narrowed, "x", 5, Open);
        Assert.Equal((2, (int?)2), (answer.Held, answer.Known));

        // A page the settings no longer read is gone, a listing read short or not.
        var index = new WebIndex(Files, directory.Path, Site);
        index.Refresh();
        Assert.DoesNotContain($"{Site}/blog/zeta", index.Pages().Keys);
    }

    /// <summary>A listing read whole is what the site lists now: one that lists fewer pages than before is known to list fewer.</summary>
    [Fact]
    public async Task AWholeListingOfFewerPages_IsWhatIsKnown()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);
        var before = harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Known;

        site.Guides = site.Guides.Replace("<url><loc>https://docs.example.test/guides/beta</loc><lastmod>2026-02-01</lastmod></url>", string.Empty, StringComparison.Ordinal);
        harness.Clock.Advance(TimeSpan.FromDays(8));

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal((2, 1), (before, harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Known));
    }

    /// <summary>A page read, or one that answers otherwise than with no answer or a refusal, ends a run of them: ten in a row alone stop a fill.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(400)]
    public async Task APageBetweenFailures_EndsTheirRow(int? between)
    {
        using var directory = new TempDirectory();
        var middle = between is null ? "alpha" : "mid";
        var pages = Enumerable.Range(0, 9).Select(at => $"p{at}").Append(middle).Concat(Enumerable.Range(9, 9).Select(at => $"p{at}")).Append("beta").ToList();
        var site = new SiteDouble
        {
            Guides = $"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(pages.Select(page => $"<url><loc>https://docs.example.test/guides/{page}</loc></url>"))}</urlset>""",
        };

        foreach (var page in pages.Where(page => page.StartsWith('p')))
        {
            site.Statuses[$"/guides/{page}"] = 503;
        }

        if (between is { } status)
        {
            site.Statuses["/guides/mid"] = status;
        }

        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Null(harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Problem);
        Assert.Equal(pages.Select(page => $"/guides/{page}"), PagesAsked(site));
    }

    /// <summary>Pages that in a row give no answer, or refuse this tool, stop the fill, which says the last and is held off.</summary>
    [Theory]
    [InlineData(503)]
    [InlineData(403)]
    [InlineData(401)]
    public async Task PagesThatGiveNoAnswerInARow_StopTheFill(int status)
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble
        {
            Guides = $"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(Enumerable.Range(0, 12).Select(at => $"<url><loc>https://docs.example.test/guides/p{at}</loc></url>"))}</urlset>""",
        };

        foreach (var at in Enumerable.Range(0, 12))
        {
            site.Statuses[$"/guides/p{at}"] = status;
        }

        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "x", 5, Open);

        Assert.Equal($"{SitemapFill.MostFailuresInARow} of its pages in a row gave no answer or refused this tool, the last 'https://docs.example.test/guides/p9': it answered {status}", answer.Problem);
        Assert.Equal(Enumerable.Range(0, SitemapFill.MostFailuresInARow).Select(at => $"/guides/p{at}"), PagesAsked(site));
        Assert.Equal(harness.Clock.GetUtcNow() + WebIndexState.AfterProblem, answer.PausedUntil);
    }

    /// <summary>A fill writes its index's file anew once the lines replaced and gone outnumber its pages, raising its generation.</summary>
    [Fact]
    public async Task AFill_WritesItsFileAnew_OnceStaleLinesOutnumberItsPages()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var stale = WebIndex.PageLine($"{Site}/guides/alpha", "2026-01-01", harness.Clock.GetUtcNow(), new WebPageParts("Alpha guide", [], "the tokenizer"));
        var pages = Path.Combine(directory.Path, WebIndex.PagesFileName);

        File.WriteAllText(pages, string.Concat(Enumerable.Repeat(stale + "\n", SitemapFill.MostStaleLines + 10)));

        harness.Indexes.Search(directory.Path, Settings(include: ["/guides/**"]), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var state = WebIndexState.Read(Files, directory.Path, Site);

        Assert.Equal(1, state.Generation);
        Assert.NotNull(state.FilledAt);
        Assert.Equal(2, File.ReadAllLines(pages).Length);
        Assert.Equal(["/guides/beta"], PagesAsked(site));
        Assert.Equal(2, harness.Indexes.Search(directory.Path, Settings(include: ["/guides/**"]), "tokenizer", 5, Open).Hits.Count);
    }

    /// <summary>
    /// The pages that could not be read are lines kept, as the pages held are: the file is written anew once the lines
    /// replaced outnumber both, never for the pages that wait.
    /// </summary>
    [Fact]
    public async Task PagesThatCouldNotBeRead_AreLinesKept()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble
        {
            Guides = $"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(Enumerable.Range(0, 100).Select(at => $"<url><loc>https://docs.example.test/guides/f{at}</loc></url>"))}</urlset>""",
        };
        using var harness = new Harness(site);
        var now = harness.Clock.GetUtcNow();
        var lines = Enumerable.Range(0, 30).Select(at => WebIndex.PageLine($"{Site}/old/o{at}", null, now, new WebPageParts("Old", [], "old")))
            .Concat(Enumerable.Range(0, 30).Select(at => WebIndex.GoneLine($"{Site}/old/o{at}")))
            .Concat(Enumerable.Range(0, 100).Select(at => WebIndex.FailedLine($"{Site}/guides/f{at}", now, "it answered 503")));

        File.WriteAllText(Path.Combine(directory.Path, WebIndex.PagesFileName), string.Concat(lines.Select(line => line + "\n")));

        harness.Indexes.Search(directory.Path, Settings(maxPages: 200), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var state = WebIndexState.Read(Files, directory.Path, Site);

        Assert.Equal((0, (string?)null), (state.Generation, state.Problem));
        Assert.NotNull(state.FilledAt);
        Assert.Equal(["/blog/zeta"], PagesAsked(site));
    }

    /// <summary>A fill begun before the index is old - its settings changed - reads nothing that is not due: no page that aged, none whose lastmod went unsaid.</summary>
    [Fact]
    public async Task AnEarlyRefill_ReadsNothingThatIsNotDue()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Guides = site.Guides.Replace("<lastmod>2026-01-01</lastmod>", string.Empty, StringComparison.Ordinal);
        site.Asked.Clear();

        harness.Indexes.Search(directory.Path, Settings(maxPages: 99), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Empty(PagesAsked(site));
    }

    /// <summary>A page held that could not be read again waits as long as one read does, though it moved or aged.</summary>
    [Fact]
    public async Task APageHeldThatCouldNotBeReadAgain_Waits()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Guides = site.Guides.Replace("alpha</loc><lastmod>2026-01-01", "alpha</loc><lastmod>2026-05-01", StringComparison.Ordinal);
        site.Statuses["/guides/alpha"] = 503;
        site.Statuses["/blog/zeta"] = 503;
        site.Asked.Clear();
        harness.Clock.Advance(TimeSpan.FromDays(8));

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(["/guides/alpha", "/blog/zeta"], PagesAsked(site));

        site.Asked.Clear();

        harness.Indexes.Search(directory.Path, Settings(maxPages: 99), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Empty(PagesAsked(site));
        Assert.Equal(3, harness.Indexes.Search(directory.Path, Settings(maxPages: 99), "x", 5, Open).Held);
    }

    /// <summary>
    /// The crawl delay runs from the last request - the time an answer took counted toward it - and a short pause the site
    /// says its requests are spent for is waited out with it.
    /// </summary>
    [Fact]
    public async Task TheCrawlDelay_RunsFromTheLastRequest_AndAShortSpentPauseIsWaitedOut()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        site.Spent["/guides/alpha"] = 2;
        using var harness = new Harness(site);
        site.Answering = _ => harness.Clock.Advance(TimeSpan.FromSeconds(3));
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal([1.0, 1.0, 1.0, 1.0, 2.0], harness.Waits.Select(wait => wait.TotalSeconds));
        Assert.Equal(2, harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Held);
    }

    /// <summary>A sitemap the source names is the one read: robots.txt's and the default are passed by.</summary>
    [Fact]
    public async Task TheSitemapTheSourceNames_IsTheOneRead()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings() with { Sitemap = $"{Site}/sitemap-other.xml" }, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(["/robots.txt", "/sitemap-other.xml", "/blog/zeta"], site.Asked.Select(asked => asked.Address.AbsolutePath));
    }

    /// <summary>A fill reads at most so many sitemaps, however many an index names.</summary>
    [Fact]
    public async Task AFill_ReadsAtMostItsSitemaps()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble
        {
            Index = $"""<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(Enumerable.Range(0, 150).Select(at => $"<sitemap><loc>https://docs.example.test/sitemap-n{at}.xml</loc></sitemap>"))}</sitemapindex>""",
        };
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(SitemapFill.MostSitemaps, site.Asked.Count(asked => asked.Address.AbsolutePath.StartsWith("/sitemap", StringComparison.Ordinal)));
    }

    /// <summary>A source whose site changed is answered by the index of its new site alone, never by the old one's pages.</summary>
    [Fact]
    public async Task ASourceWhoseSiteChanged_IsAnsweredByItsNewSiteAlone()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Robots = "User-agent: *\nSitemap: https://new.example.test/sitemap-guides.xml.gz\n";
        site.Guides = """
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://new.example.test/guides/alpha</loc></url>
            </urlset>
            """;
        var settings = Settings() with { Site = "https://new.example.test" };

        var answer = harness.Indexes.Search(directory.Path, settings, "guide", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal((0, true), (answer.Held, answer.Filling));
        Assert.Empty(answer.Hits);

        // What the new site's fill read is the index its searches read.
        var after = harness.Indexes.Search(directory.Path, settings, "tokenizer", 5, Open);
        Assert.Equal(["https://new.example.test/guides/alpha"], after.Hits.Select(hit => hit.Address));
    }

    /// <summary>A fill that fails is said where every search of the index reads it, and the index is held off a while.</summary>
    [Fact]
    public async Task AFillThatFails_IsSaid_AndHeldOff()
    {
        using var directory = new TempDirectory();
        using var harness = new Harness(new SiteDouble()) { WaitFails = new IOException("the disk is full") };

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);

        Assert.Equal(("its fill failed: the disk is full", (DateTimeOffset?)(harness.Clock.GetUtcNow() + WebIndexState.AfterProblem)), (answer.Problem, answer.PausedUntil));
    }

    /// <summary>
    /// A search while the index is filled answers from what it holds so far and says the rest are being read; a fill
    /// stopped with its process keeps what it read, and the next process's first search goes on from there.
    /// </summary>
    [Fact]
    public async Task AFillStopped_KeepsWhatItRead_AndTheNextProcessGoesOn()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        var settings = Settings(include: ["/guides/**"]);

        using (var harness = new Harness(site) { HoldAt = 5 })
        {
            harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
            await harness.Holding.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            var during = harness.Indexes.Search(directory.Path, settings, "tokenizer", 5, Open);

            Assert.True(during.Filling);
            Assert.Equal("indexed 1 of 2 pages of docs.example.test; the rest are being read", during.Said);
            Assert.Equal([$"{Site}/guides/alpha"], during.Hits.Select(hit => hit.Address));

            harness.Indexes.Dispose();

            Assert.True(harness.Indexes.FilledAsync(directory.Path).IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => harness.Indexes.Search(directory.Path, settings, "x", 5, Open));
        }

        site.Asked.Clear();

        using var next = new Harness(site);

        Assert.True(next.Indexes.Search(directory.Path, settings, "x", 5, Open).Filling);
        await next.Indexes.FilledAsync(directory.Path);

        Assert.Equal(["/guides/beta"], PagesAsked(site));
        Assert.Equal(2, next.Indexes.Search(directory.Path, settings, "x", 5, Open).Held);
    }

    /// <summary>
    /// A sitemap that cannot be read leaves every page held as it was - none dropped for a listing not read whole - and the
    /// fill says so and is read again after a while.
    /// </summary>
    [Fact]
    public async Task AListingNotReadWhole_DropsNothing_AndIsReadAgainLater()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        site.Statuses["/sitemap-other.xml"] = 503;
        harness.Clock.Advance(TimeSpan.FromDays(8));

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(), "zeta", 5, Open);

        Assert.Equal(3, answer.Held);
        Assert.Equal([$"{Site}/blog/zeta"], answer.Hits.Select(hit => hit.Address));
        Assert.StartsWith("indexed 3 of 3 pages of docs.example.test; no fill has ended whole since one stopped short: 1 of its sitemaps could not be read", answer.Said, StringComparison.Ordinal);
        Assert.Equal("1 of its sitemaps could not be read, the first 'https://docs.example.test/sitemap-other.xml': it answered 503; the pages it holds that the rest list are kept, while it holds no more than maxPages", answer.Problem);
        Assert.Equal(harness.Clock.GetUtcNow() + WebIndexState.AfterProblem, answer.PausedUntil);
        Assert.False(answer.Filling);

        harness.Clock.Advance(WebIndexState.AfterProblem);
        Assert.True(harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open).Filling);
    }

    /// <summary>
    /// A redirect is a request as any other: asked at the fill's pace, and held to robots.txt and the source's patterns
    /// before it is asked - a page that redirects where the site forbids is never read there.
    /// </summary>
    [Fact]
    public async Task ARedirect_IsPaced_AndHeldToRobotsTxtBeforeItIsAsked()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        site.Redirects["/guides/alpha"] = "/private/alpha";
        site.Redirects["/guides/beta"] = "/guides/beta-moved";
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        Assert.Equal(["/guides/alpha", "/guides/beta", "/guides/beta-moved"], PagesAsked(site));
        Assert.Equal(Enumerable.Repeat(TimeSpan.FromSeconds(4), 6), harness.Waits);

        var answer = harness.Indexes.Search(directory.Path, settings, "tokenizer", 5, Open);

        Assert.Equal(1, answer.Held);
        Assert.Equal([$"{Site}/guides/beta"], answer.Hits.Select(hit => hit.Address));
    }

    /// <summary>A good answer that says the site's requests are spent for longer than the fill's pace keeps what it gave, and holds the fill off until the reset.</summary>
    [Fact]
    public async Task ASiteThatSaysItsRequestsAreSpent_HoldsTheFillOff()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        site.Spent["/guides/alpha"] = 600;
        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "tokenizer", 5, Open);

        Assert.Equal(["/guides/alpha"], PagesAsked(site));
        Assert.Equal(1, answer.Held);
        Assert.Equal((harness.Clock.GetUtcNow() + TimeSpan.FromSeconds(600), SitemapFill.SiteSpent), (answer.PausedUntil, answer.PausedFor));
        Assert.Null(answer.Problem);
        Assert.Contains($"{SitemapFill.SiteSpent} until {WebRateLimit.Said(harness.Clock.GetUtcNow() + TimeSpan.FromSeconds(600))}", File.ReadAllLines(Path.Combine(directory.Path, SitemapFill.RecordFileName))[^1], StringComparison.Ordinal);

        // Once the reset passes, the next fill goes on, and nothing is said to hold it off.
        harness.Clock.Advance(TimeSpan.FromSeconds(601));
        site.Spent.Clear();

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var after = harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        Assert.Equal((2, (DateTimeOffset?)null, (string?)null), (after.Held, after.PausedUntil, after.PausedFor));
    }

    /// <summary>The run's gate is asked for the source's turn before each request; spent, it holds the fill off until the time it gave, nothing more asked.</summary>
    [Fact]
    public async Task TheRunGatesPace_HoldsTheFillOff()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble();
        using var harness = new Harness(site);
        var until = harness.Clock.GetUtcNow() + TimeSpan.FromHours(5);
        var turns = 0;
        var gate = Open with { Pace = _ => Task.FromResult(Interlocked.Increment(ref turns) >= 4 ? until : (DateTimeOffset?)null) };

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, gate);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(), "x", 5, gate);

        Assert.Equal(["/robots.txt", "/sitemap-index.xml", "/sitemap-guides.xml.gz"], site.Asked.Select(asked => asked.Address.AbsolutePath));
        Assert.Equal((until, (string?)null, false, SitemapFill.TurnSpent), (answer.PausedUntil, answer.Problem, answer.Filling, answer.PausedFor));

        // An index that holds nothing yet says why it waits, as it was held off.
        var told = new SitemapKind().Read(WebSearchFixtures.Source("site", $$"""{ "kind": "sitemap", "site": "{{Site}}" }"""), WebSearchFound.None, new WebSearchResponse.Indexed(answer));
        Assert.Equal((WebSearchOutcome.RateLimited, $"{SitemapFill.TurnSpent} until {WebRateLimit.Said(until)}, and its index holds no page yet"), (told.Outcome, told.Why));
    }

    /// <summary>
    /// A site that asks to be left alone where robots.txt or a sitemap is asked holds the fill off there, nothing more
    /// asked, until the time it gave - a date included - never longer than a day.
    /// </summary>
    [Theory]
    [InlineData("/robots.txt", "600", 600)]
    [InlineData("/sitemap-index.xml", "Sun, 25 Oct 2026 12:30:00 GMT", 1800)]
    [InlineData("/sitemap-guides.xml.gz", "Thu, 25 Oct 2096 12:00:00 GMT", 86400)]
    public async Task ASitesLimitAnywhere_HoldsTheFillOff(string at, string retryAfter, int seconds)
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble { LimitedAt = at, RetryAfter = retryAfter };
        using var harness = new Harness(site);

        harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);

        Assert.Equal(at, site.Asked[^1].Address.AbsolutePath);
        Assert.Equal((harness.Clock.GetUtcNow() + TimeSpan.FromSeconds(seconds), SitemapFill.SiteLimited, 0), (answer.PausedUntil, answer.PausedFor, answer.Held));
    }

    /// <summary>
    /// A sitemap of the site's own is held to its robots.txt, as a page is, unless robots.txt names it; a site that
    /// refuses every sitemap has nothing read, by its own rule, and is asked again when the index is old.
    /// </summary>
    [Fact]
    public async Task ASitemapRobotsTxtDoesNotName_IsHeldToIt()
    {
        using var directory = new TempDirectory();
        var named = new SiteDouble { Robots = "User-agent: *\nDisallow: /sitemap-other\nSitemap: https://docs.example.test/sitemap-index.xml\n" };
        using (var harness = new Harness(named))
        {
            harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open);
            await harness.Indexes.FilledAsync(directory.Path);

            Assert.DoesNotContain(named.Asked, asked => asked.Address.AbsolutePath == "/sitemap-other.xml");
            Assert.Contains(named.Asked, asked => asked.Address.AbsolutePath == "/sitemap-index.xml");
        }

        using var other = new TempDirectory();
        var unnamed = new SiteDouble { Robots = "User-agent: *\nDisallow: /sitemap\n" };
        using var refused = new Harness(unnamed);

        refused.Indexes.Search(other.Path, Settings(), "x", 5, Open);
        await refused.Indexes.FilledAsync(other.Path);

        Assert.Equal(["/robots.txt"], unnamed.Asked.Select(asked => asked.Address.AbsolutePath));
        var nothing = refused.Indexes.Search(other.Path, Settings(), "x", 5, Open);
        Assert.StartsWith("1 of its sitemaps the source may not read, the first 'https://docs.example.test/sitemap.xml': 'https://docs.example.test/sitemap.xml' is an address the site's robots.txt", nothing.Problem, StringComparison.Ordinal);
        Assert.Equal(refused.Clock.GetUtcNow() + TimeSpan.FromDays(7), nothing.PausedUntil);

        // One robots.txt names is read though its rules would hold it, and the sitemaps it lists are held to them.
        using var third = new TempDirectory();
        var disallowed = new SiteDouble { Robots = "User-agent: *\nDisallow: /sitemap\nSitemap: https://docs.example.test/sitemap-index.xml\n" };
        using (var harness = new Harness(disallowed))
        {
            harness.Indexes.Search(third.Path, Settings(), "x", 5, Open);
            await harness.Indexes.FilledAsync(third.Path);

            Assert.Equal(["/robots.txt", "/sitemap-index.xml"], disallowed.Asked.Select(asked => asked.Address.AbsolutePath));
        }

        // One off the site is not the site's to refuse.
        using var fourth = new TempDirectory();
        var offSite = new SiteDouble { Robots = "User-agent: *\nDisallow: /sitemap\n" };
        using var cdn = new Harness(offSite);

        cdn.Indexes.Search(fourth.Path, Settings() with { Sitemap = "https://cdn.example.test/sitemap.xml" }, "x", 5, Open);
        await cdn.Indexes.FilledAsync(fourth.Path);

        Assert.Contains(offSite.Asked, asked => asked.Address.Host == "cdn.example.test" && asked.Address.AbsolutePath == "/sitemap.xml");
    }

    /// <summary>
    /// A page that could not be read is remembered, and waits as long as a page read does before it is asked again, and is
    /// asked last: pages that refuse this tool in a row stop one fill, never every fill after it.
    /// </summary>
    [Fact]
    public async Task PagesThatCouldNotBeRead_WaitAndGoLast()
    {
        using var directory = new TempDirectory();
        var site = new SiteDouble
        {
            Guides = $"""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">{string.Concat(Enumerable.Range(0, 12).Select(at => $"<url><loc>https://docs.example.test/guides/p{at}</loc></url>"))}<url><loc>https://docs.example.test/guides/alpha</loc></url></urlset>""",
        };

        foreach (var at in Enumerable.Range(0, 12))
        {
            site.Statuses[$"/guides/p{at}"] = 403;
        }

        using var harness = new Harness(site);
        var settings = Settings(include: ["/guides/**"]);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);
        Assert.Equal(0, harness.Indexes.Search(directory.Path, settings, "x", 5, Open).Held);

        site.Asked.Clear();
        harness.Clock.Advance(WebIndexState.AfterProblem);

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, settings, "tokenizer", 5, Open);

        Assert.Equal(["/guides/p10", "/guides/p11", "/guides/alpha"], PagesAsked(site));
        Assert.Equal((1, (string?)null), (answer.Held, answer.Problem));

        site.Asked.Clear();
        harness.Clock.Advance(TimeSpan.FromDays(7));

        harness.Indexes.Search(directory.Path, settings, "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        // Read again in their order once they waited: the page held first, then those that could not be read.
        Assert.Equal(["/guides/alpha", .. Enumerable.Range(0, 10).Select(at => $"/guides/p{at}")], PagesAsked(site));
    }

    /// <summary>A source whose settings changed is filled again at once, and answered by them meanwhile: a page they no longer read is no hit.</summary>
    [Fact]
    public async Task ASourceWhoseSettingsChanged_IsFilledAgainAtOnce_AndAnsweredByThem()
    {
        using var directory = new TempDirectory();
        using var harness = new Harness(new SiteDouble());

        harness.Indexes.Search(directory.Path, Settings(include: ["/guides/**"]), "x", 5, Open);
        await harness.Indexes.FilledAsync(directory.Path);

        var blog = Settings(include: ["/blog/**"]);
        var meanwhile = harness.Indexes.Search(directory.Path, blog, "guide", 5, Open);

        Assert.True(meanwhile.Filling);
        Assert.DoesNotContain(meanwhile.Hits, hit => hit.Address.Contains("/guides/", StringComparison.Ordinal));

        await harness.Indexes.FilledAsync(directory.Path);

        var answer = harness.Indexes.Search(directory.Path, blog, "guide", 5, Open);

        Assert.Equal((1, false), (answer.Held, answer.Filling));
        Assert.Equal([$"{Site}/blog/zeta"], answer.Hits.Select(hit => hit.Address));
    }

    /// <summary>One fill of an index at a time: a second claim is refused while the first holds it, and taken once it is let go.</summary>
    [Fact]
    public void OneFill_AtATime()
    {
        using var directory = new TempDirectory();
        using var harness = new Harness(new SiteDouble());

        using (var first = harness.Fill.Claim(directory.Path))
        {
            Assert.NotNull(first);
            Assert.Null(harness.Fill.Claim(directory.Path));
            Assert.True(harness.Indexes.Search(directory.Path, Settings(), "x", 5, Open).Filling);
        }

        using var again = harness.Fill.Claim(directory.Path);
        Assert.NotNull(again);
    }

    /// <summary>The index of a source is kept under the repository's orchestrators, beside every orchestrator, by the source's name.</summary>
    [Fact]
    public void AnIndex_IsKeptUnderTheOrchestrators()
    {
        using var directory = new TempDirectory();
        var layout = new HarnessLayout(directory.Path, directory.Path);

        Assert.Equal(Path.Combine(layout.OrchestratorsDirectory, ".web-index", "digital-gov"), OrchestratorLayout.WebIndexDirectory(layout, "digital-gov"));
        Assert.Throws<ArgumentException>(() => OrchestratorLayout.WebIndexDirectory(layout, "../x"));

        // Two names that differ only in case would be one directory on Windows and macOS: a name is lower-case.
        Assert.Throws<ArgumentException>(() => OrchestratorLayout.WebIndexDirectory(layout, "Digital-gov"));
        Assert.Throws<ArgumentException>(() => OrchestratorLayout.WebIndexDirectory(layout, "digital_gov"));
        Assert.NotNull(OrchestrationRules.NameProblem(OrchestratorLayout.WebIndexesDirectoryName));
    }

    private static SitemapSettings Settings(string[]? include = null, int maxPages = 100)
        => new(Site, null, [.. (include ?? []).Select(GlobPattern.Parse)], [GlobPattern.Parse("/guides/hidden/**")], maxPages, TimeSpan.FromDays(7));

    /// <summary>The pages the site was asked for, in their order: every address but its robots.txt and its sitemaps.</summary>
    private static List<string> PagesAsked(SiteDouble site)
    {
        lock (site.Asked)
        {
            return [.. site.Asked.Select(asked => asked.Address.AbsolutePath).Where(path => path != "/robots.txt" && !path.StartsWith("/sitemap", StringComparison.Ordinal))];
        }
    }

    /// <summary>The fill and the indexes over a site a test answers, on a clock that moves when told, every wait recorded.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly HarnessFetch _fetch;

        public Harness(SiteDouble site)
        {
            _fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, site);
            Fill = new SitemapFill(Files, _fetch, Clock, (wait, cancellationToken) =>
            {
                if (WaitFails is { } fails)
                {
                    return Task.FromException(fails);
                }

                lock (Waits)
                {
                    Waits.Add(wait);

                    // Held at that wait until the fill is stopped, as a process stopped mid-fill is.
                    if (Waits.Count == HoldAt)
                    {
                        Holding.TrySetResult();
                        return Task.Delay(Timeout.Infinite, cancellationToken);
                    }
                }

                return Task.CompletedTask;
            });
            Indexes = new WebIndexes(Files, Fill, Clock);
        }

        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

        public List<TimeSpan> Waits { get; } = [];

        /// <summary>The wait, counted from 1, the fill is held at until it is stopped; none where it is never held.</summary>
        public int? HoldAt { get; init; }

        /// <summary>What every wait fails with, as a fill that meets what nothing foresaw; none where waits are waited.</summary>
        public Exception? WaitFails { get; init; }

        /// <summary>Set once the fill is held.</summary>
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SitemapFill Fill { get; }

        public WebIndexes Indexes { get; }

        public void Dispose()
        {
            Indexes.Dispose();
            _fetch.Dispose();
        }
    }

    /// <summary>
    /// A site: robots.txt, an index of two sitemaps - one gzip - and its pages; a path may be answered another status, a
    /// redirect, or a good answer that says its requests are spent.
    /// </summary>
    private sealed class SiteDouble : HttpMessageHandler
    {
        public List<WebSent> Asked { get; } = [];

        public int RobotsStatus { get; set; } = 200;

        public string RobotsType { get; init; } = "text/plain";

        public int IndexStatus { get; init; } = 200;

        public string OtherType { get; init; } = "text/xml";

        public string IndexType { get; init; } = "application/xml";

        /// <summary>The sitemap index answered in place of the site's own; none where it is the site's.</summary>
        public string? Index { get; set; }

        /// <summary>Told each path as it is answered, before the answer is made: where a test moves its clock, as an answer takes time.</summary>
        public Action<string>? Answering { get; set; }

        public string? LimitedAt { get; set; }

        /// <summary>What a limited path's Retry-After says.</summary>
        public string RetryAfter { get; init; } = "600";

        public string Robots { get; set; } = "User-agent: *\nDisallow: /private/\nCrawl-delay: 4\nSitemap: https://docs.example.test/sitemap-index.xml\n";

        public Dictionary<string, int> Statuses { get; } = [];

        public Dictionary<string, string> Redirects { get; } = [];

        /// <summary>Paths answered with their page and X-RateLimit-Remaining: 0, resetting in so many seconds.</summary>
        public Dictionary<string, int> Spent { get; } = [];

        public string Other { get; set; } = """
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://docs.example.test/blog/zeta</loc></url>
            </urlset>
            """;

        public string Guides { get; set; } = """
            <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
              <url><loc>https://docs.example.test/guides/alpha</loc><lastmod>2026-01-01</lastmod></url>
              <url><loc>https://docs.example.test/guides/beta</loc><lastmod>2026-02-01</lastmod></url>
              <url><loc>https://docs.example.test/private/secret</loc></url>
              <url><loc>https://docs.example.test/guides/hidden/x</loc></url>
              <url><loc>https://elsewhere.example.test/guides/x</loc></url>
            </urlset>
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(new WebSent(request.RequestUri!, request.Headers.UserAgent.ToString(), false, false));
            }

            var path = request.RequestUri!.AbsolutePath;

            Answering?.Invoke(path);

            if (path == LimitedAt)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down", Encoding.UTF8, "text/plain"), RequestMessage = request };
                limited.Headers.TryAddWithoutValidation("Retry-After", RetryAfter);
                return Task.FromResult(limited);
            }

            if (Statuses.TryGetValue(path, out var status))
            {
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("no", Encoding.UTF8, "text/plain"), RequestMessage = request });
            }

            if (Redirects.TryGetValue(path, out var location))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri(location, UriKind.Relative) }, RequestMessage = request });
            }

            var response = path switch
            {
                "/robots.txt" when RobotsStatus != 200 => new HttpResponseMessage((HttpStatusCode)RobotsStatus) { Content = new StringContent("no", Encoding.UTF8, "text/plain") },
                "/robots.txt" => Text(Robots, RobotsType),
                "/sitemap-index.xml" when IndexStatus != 200 => new HttpResponseMessage((HttpStatusCode)IndexStatus) { Content = new StringContent("missing", Encoding.UTF8, "text/plain") },
                "/sitemap-index.xml" or "/sitemap.xml" => Text(Index ?? """
                    <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                      <sitemap><loc>https://docs.example.test/sitemap-guides.xml.gz</loc></sitemap>
                      <sitemap><loc>https://docs.example.test/sitemap-other.xml</loc></sitemap>
                    </sitemapindex>
                    """, IndexType),
                "/sitemap-guides.xml.gz" => Gzip(Guides),
                "/sitemap-other.xml" => Text(Other, OtherType),
                "/guides/alpha" => Text("<html><head><title>Alpha guide</title></head><body><h2>Tokenizer</h2><p>How the tokenizer splits a guide's text.</p></body></html>", "text/html"),
                "/guides/beta" or "/guides/beta-moved" => Text("<html><head><title>Beta guide</title></head><body><p>The tokenizer, once more, in a guide.</p></body></html>", "text/html"),
                "/guides/gamma" => Text("<html><head><title>Gamma guide</title></head><body><p>A new guide.</p></body></html>", "text/html"),
                "/blog/zeta" => Text("<html><head><title>Zeta</title></head><body><p>A guide of the blog.</p></body></html>", "text/html"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("none", Encoding.UTF8, "text/plain") },
            };

            if (Spent.TryGetValue(path, out var reset))
            {
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Text(string text, string type) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type) };

        private static HttpResponseMessage Gzip(string text)
        {
            using var held = new MemoryStream();

            using (var gzip = new GZipStream(held, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(text));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(held.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/x-gzip") } } };
        }
    }
}
