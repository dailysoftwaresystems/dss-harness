using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Web;
using DssHarness.Core.Web.Index;
using DssHarness.Core.Web.Search;

namespace DssHarness.Tests;

/// <summary>
/// The harness's own index of a site: pages as lines that only grow, read by each searcher from where it last looked;
/// BM25 over title, headings and text, weighted; a snippet around what was asked; written anew under a new generation.
/// </summary>
public sealed class WebIndexTests
{
    private const string Site = "https://docs.example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly IFileSystem Files = new PhysicalFileSystem(FilePermissionsFactory.Create());

    /// <summary>A page whose title names the term ranks above one whose text alone does; one with neither is not found.</summary>
    [Fact]
    public void ATitleMatch_RanksAboveATextMatch()
    {
        using var directory = new TempDirectory();
        Append(directory, Page("/a", "Configuring the tokenizer", [], "How to set it up."));
        Append(directory, Page("/b", "Other things", [], "Somewhere in here the tokenizer is mentioned once among many other words of a long page."));
        Append(directory, Page("/c", "Unrelated", [], "Nothing to see."));

        var index = Index(directory);
        index.Refresh();

        var hits = Hits(index, "Tokenizer", 10);

        Assert.Equal([$"{Site}/a", $"{Site}/b"], hits.Select(hit => hit.Address));
        Assert.True(hits[0].Score > hits[1].Score);
        Assert.Equal(3, index.Held);
    }

    /// <summary>A title's word counts thrice a text's: the page that names the term in its title ranks first, whatever their order.</summary>
    [Fact]
    public void ATitlesTerm_OutweighsATextsTerm()
    {
        using var directory = new TempDirectory();
        Append(directory, Page("/text", "other words here", [], "tokenizer"));
        Append(directory, Page("/title", "tokenizer", [], "other words here"));

        var index = Index(directory);
        index.Refresh();

        Assert.Equal([$"{Site}/title", $"{Site}/text"], Hits(index, "tokenizer", 2).Select(hit => hit.Address));
    }

    /// <summary>A page dropped counts for nothing: the index scores as though it was never held.</summary>
    [Fact]
    public void ADroppedPage_CountsForNothing()
    {
        using var dropped = new TempDirectory();
        Append(dropped, Page("/a", "x", [], "parser lexer"));
        Append(dropped, Page("/b", "y", [], "parser"));
        Append(dropped, Page("/c", "z", [], "lexer"));
        Append(dropped, WebIndex.GoneLine($"{Site}/b"));

        using var never = new TempDirectory();
        Append(never, Page("/a", "x", [], "parser lexer"));
        Append(never, Page("/c", "z", [], "lexer"));

        var one = Index(dropped);
        one.Refresh();
        var other = Index(never);
        other.Refresh();

        Assert.Equal(Hits(other, "parser", 5).Single().Score, Hits(one, "parser", 5).Single().Score, 9);
    }

    /// <summary>A line of another site - one whose address only begins as this site's does - is none of the index's.</summary>
    [Fact]
    public void ALineOfAnotherSite_IsNoneOfIts()
    {
        using var directory = new TempDirectory();
        Append(directory, Page("/a", "Parser", [], "parser"));
        Append(directory, WebIndex.PageLine($"{Site}x/b", null, At, new WebPageParts("Parser", [], "parser")));

        var index = Index(directory);
        index.Refresh();

        Assert.Equal(1, index.Held);
        Assert.Equal([$"{Site}/a"], Hits(index, "parser", 5).Select(hit => hit.Address));
    }

    /// <summary>A source's settings are one fingerprint: any of what it reads changes it; how often it is read again does not.</summary>
    [Fact]
    public void ASourcesSettings_AreOneFingerprint()
    {
        var settings = new SitemapSettings(Site, null, [], [], TimeSpan.FromDays(7));
        SitemapSettings[] changed =
        [
            settings with { Site = "https://other.example.test" },
            settings with { Sitemap = $"{Site}/s.xml" },
            settings with { Include = [GlobPattern.Parse("/a/**")] },
            settings with { Exclude = [GlobPattern.Parse("/a/**")] },
        ];

        Assert.Equal(changed.Length + 1, changed.Select(other => other.Fingerprint).Append(settings.Fingerprint).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(settings.Fingerprint, (settings with { Refresh = TimeSpan.FromDays(1) }).Fingerprint);
    }

    /// <summary>A heading counts more than the text, and every term of a query adds to a page's score.</summary>
    [Fact]
    public void HeadingsAndEveryTerm_Count()
    {
        using var directory = new TempDirectory();
        Append(directory, Page("/text", "One", [], "lexer and parser are both here"));
        Append(directory, Page("/heading", "Two", ["Lexer"], "nothing else"));
        Append(directory, Page("/one", "Three", [], "lexer only"));

        var index = Index(directory);
        index.Refresh();

        Assert.Equal($"{Site}/heading", Hits(index, "lexer", 1).Single().Address);
        Assert.Equal($"{Site}/text", Hits(index, "lexer parser", 1).Single().Address);
    }

    /// <summary>A searcher reads only what was added since it last looked, and a torn last line waits for the next look.</summary>
    [Fact]
    public void ASearcher_ReadsWhatWasAdded()
    {
        using var directory = new TempDirectory();
        Append(directory, Page("/a", "Alpha", [], "first"));

        var index = Index(directory);
        index.Refresh();
        Assert.Equal(1, index.Held);

        var torn = Page("/b", "Beta", [], "second");
        File.AppendAllText(Path.Combine(directory.Path, WebIndex.PagesFileName), torn[..20]);
        index.Refresh();
        Assert.Equal(1, index.Held);

        File.AppendAllText(Path.Combine(directory.Path, WebIndex.PagesFileName), torn[20..] + "\n");
        index.Refresh();
        Assert.Equal(2, index.Held);
        Assert.Equal($"{Site}/b", Hits(index, "second", 5).Single().Address);
    }

    /// <summary>A later line of an address replaces the earlier; a line that says it is gone drops it; a page of another site is none of this one's.</summary>
    [Fact]
    public void ALaterLine_Replaces_AndGoneDrops()
    {
        using var directory = new TempDirectory();
        Append(directory, Page("/a", "Old", [], "obsolete words"));
        Append(directory, Page("/a", "New", [], "fresh words"));
        Append(directory, Page("/b", "Bee", [], "fresh too"));
        Append(directory, WebIndex.GoneLine($"{Site}/b"));
        Append(directory, WebIndex.PageLine("https://elsewhere.test/x", null, At, new WebPageParts("Else", [], "fresh elsewhere")));

        var index = Index(directory);
        index.Refresh();

        Assert.Equal(1, index.Held);
        Assert.Equal(5, index.Lines);
        Assert.Empty(Hits(index, "obsolete", 5));
        Assert.Equal("New", Hits(index, "fresh", 5).Single().Title);
        Assert.Equal([$"{Site}/a"], index.Pages().Keys);
    }

    /// <summary>A snippet begins a little before the first term asked, at a blank, marked; a page with no title shows its first heading, else its address.</summary>
    [Fact]
    public void ASnippet_BeginsNearTheTerm()
    {
        using var directory = new TempDirectory();
        var lead = string.Join(' ', Enumerable.Repeat("filler", 40));
        Append(directory, Page("/a", string.Empty, ["The heading"], $"{lead} the needle is here and more follows."));
        Append(directory, Page("/b", string.Empty, [], "needle"));

        var index = Index(directory);
        index.Refresh();

        Append(directory, Page("/long", string.Empty, [], "needle " + string.Join(' ', Enumerable.Repeat("word", 500))));
        index.Refresh();

        var hits = Hits(index, "needle", 5);
        var first = hits.Single(hit => hit.Address == $"{Site}/a");
        var cut = hits.Single(hit => hit.Address == $"{Site}/long").Snippet;

        Assert.True(cut.Length <= WebIndex.SnippetLength);
        Assert.EndsWith("word…", cut, StringComparison.Ordinal);

        Assert.Equal("The heading", first.Title);
        Assert.StartsWith("…", first.Snippet, StringComparison.Ordinal);
        Assert.Contains("the needle is here", first.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("fill ", first.Snippet, StringComparison.Ordinal);
        Assert.Equal($"{Site}/b", hits.Single(hit => hit.Address == $"{Site}/b").Title);
    }

    /// <summary>A page's line keeps it whole: its title, every heading, all its text and why a page could not be read, however long each is.</summary>
    [Fact]
    public void APagesLine_KeepsItWhole()
    {
        var title = new string('t', 1000) + char.ConvertFromUtf32(0x1F600) + "rest";
        var headings = Enumerable.Range(0, 150).Select(at => new string('h', 400 + at)).ToList();
        var text = new string('x', 40_000);
        var line = WebIndex.PageLine($"{Site}/a", "2026-01-01", At, new WebPageParts(title, headings, text));

        using var document = System.Text.Json.JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal(text, root.GetProperty("text").GetString());
        Assert.Equal(title, root.GetProperty("title").GetString());
        Assert.Equal(headings, root.GetProperty("headings").EnumerateArray().Select(heading => heading.GetString()));
        Assert.Equal("2026-01-01", root.GetProperty("lastmod").GetString());

        var why = string.Join(' ', Enumerable.Repeat("it answered 503", 100));
        using var failed = System.Text.Json.JsonDocument.Parse(WebIndex.FailedLine($"{Site}/a", At, why));
        Assert.Equal(why, failed.RootElement.GetProperty("why").GetString());
    }

    /// <summary>
    /// A page of five hundred kilobytes is indexed whole: a term at the end of its text, in its hundred and fiftieth heading,
    /// and at the end of a title a thousand characters long each finds it, its title shown whole.
    /// </summary>
    [Fact]
    public void AFiveHundredKilobytePage_IsIndexedWhole()
    {
        using var directory = new TempDirectory();
        var title = string.Join(' ', Enumerable.Repeat("long", 200)) + " titled";
        var headings = Enumerable.Range(0, 150).Select(at => at == 149 ? "lastheading" : $"heading {at}").ToList();
        var text = string.Join(' ', Enumerable.Repeat("filler", 500 * 1024 / 7)) + " farthest";
        Append(directory, WebIndex.PageLine($"{Site}/a", null, At, new WebPageParts(title, headings, text)));
        Append(directory, Page("/b", "Other", [], "nothing of it"));

        var index = Index(directory);
        index.Refresh();

        Assert.True(text.Length > 500 * 1000);
        Assert.Equal($"{Site}/a", Assert.Single(Hits(index, "farthest", 5)).Address);
        Assert.Equal($"{Site}/a", Assert.Single(Hits(index, "lastheading", 5)).Address);

        var titled = Assert.Single(Hits(index, "titled", 5));
        Assert.Equal(title, titled.Title);
        Assert.Contains("farthest", Assert.Single(Hits(index, "farthest", 5)).Snippet, StringComparison.Ordinal);
    }

    /// <summary>A term two thousand characters long - a hash, a key, a run of digits - is a term, and finds its page.</summary>
    [Fact]
    public void ATwoThousandCharacterTerm_IsFindable()
    {
        using var directory = new TempDirectory();
        var term = string.Concat(Enumerable.Range(0, 2000).Select(at => (char)('a' + (at % 26))));
        Append(directory, Page("/hash", "Checksums", [], $"the checksum is {term} as written"));
        Append(directory, Page("/other", "Other", [], $"the checksum is {term[..1999]} short of it"));

        var index = Index(directory);
        index.Refresh();

        var hit = Assert.Single(Hits(index, term.ToUpperInvariant(), 5));
        Assert.Equal($"{Site}/hash", hit.Address);
        Assert.True(hit.Snippet.Length <= WebIndex.SnippetLength);
    }

    /// <summary>A search gives the page of its hits asked - from 1, of the size asked - and how many pages answer it in all, on every page of them.</summary>
    [Fact]
    public void ASearch_GivesThePageAsked_AndHowManyAnswerInAll()
    {
        using var directory = new TempDirectory();

        for (var at = 0; at < 25; at++)
        {
            Append(directory, Page($"/p{at}", $"Page {at}", [], string.Join(' ', Enumerable.Repeat("parser", at + 1))));
        }

        var index = Index(directory);
        index.Refresh();

        var pages = Enumerable.Range(1, 4).Select(number => index.Search("parser", new WebSearchPage(number, 10))).ToList();

        Assert.Equal([10, 10, 5, 0], pages.Select(page => page.Hits.Count));
        Assert.All(pages, page => Assert.Equal(25, page.Total));
        Assert.Equal(25, pages.SelectMany(page => page.Hits).Select(hit => hit.Address).Distinct(StringComparer.Ordinal).Count());
        var none = index.Search("zebra", new WebSearchPage(1, 10));
        Assert.Equal((0, 0), (none.Hits.Count, none.Total));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("parser", new WebSearchPage(0, 10)));
    }

    /// <summary>A search keeps to the pages it is told to: one the source's patterns no longer read is no hit, though it is held.</summary>
    [Fact]
    public void ASearch_KeepsToThePagesItIsToldTo()
    {
        using var directory = new TempDirectory();
        Append(directory, Page("/guides/a", "Parser guide", [], "parser"));
        Append(directory, Page("/blog/b", "Parser blog", [], "parser"));

        var index = Index(directory);
        index.Refresh();

        Assert.Equal(2, Hits(index, "parser", 5).Count);
        Assert.Equal([$"{Site}/blog/b"], Hits(index, "parser", 5, address => address.Contains("/blog/", StringComparison.Ordinal)).Select(hit => hit.Address));
        Assert.Equal(2, index.Held);

        // Each page is asked of once a search, however many of its terms were asked.
        var asked = new List<string>();
        Hits(index, "parser guide blog", 5, address =>
        {
            asked.Add(address);
            return true;
        });
        Assert.Equal([$"{Site}/blog/b", $"{Site}/guides/a"], asked.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A fill is due where none finished, where the last began after the last finished, where the last finished under
    /// other settings, and where it is older than their refresh - never while held off.
    /// </summary>
    [Fact]
    public void AFill_IsDue_AsItsStateSays()
    {
        var settings = new SitemapSettings(Site, null, [], [], TimeSpan.FromDays(7));
        var filled = new WebIndexState(Site, 0, 2, At, At, null, null) { Settings = settings.Fingerprint };

        Assert.False(filled.Due(At + TimeSpan.FromDays(1), settings));
        Assert.True((filled with { FilledAt = null }).Due(At, settings));
        Assert.True((filled with { FillStartedAt = At + TimeSpan.FromHours(1) }).Due(At + TimeSpan.FromHours(2), settings));
        Assert.True(filled.Due(At + TimeSpan.FromDays(1), settings with { Exclude = [GlobPattern.Parse("/old/**")] }));
        Assert.True(filled.Due(At + TimeSpan.FromDays(1), settings with { Include = [DssHarness.Core.Repository.GlobPattern.Parse("/guides/**")] }));
        Assert.False(filled.Due(At + TimeSpan.FromDays(1), settings with { Refresh = TimeSpan.FromDays(2) }));
        Assert.True(filled.Due(At + TimeSpan.FromDays(7), settings));
        var stopped = filled.Stopped("its robots.txt could not be read", At + TimeSpan.FromHours(1), settings.All) with { FilledAt = null };
        Assert.False(stopped.Due(At, settings));
        Assert.True(stopped.Due(At + TimeSpan.FromHours(1), settings));

        // A pause after a problem holds the settings it stopped under alone - how long a page waits among them - and one the
        // site asked for holds whatever the settings.
        Assert.True(stopped.Due(At, settings with { Exclude = [GlobPattern.Parse("/old/**")] }));
        Assert.True(stopped.Due(At, settings with { Refresh = TimeSpan.FromDays(1) }));
        Assert.False((stopped with { PausedFor = SitemapFill.SiteLimited }).Due(At, settings with { Exclude = [GlobPattern.Parse("/old/**")] }));

        // A fill stopped short is held off for no word of the site's, whatever held it off before.
        Assert.Null((filled with { PausedFor = SitemapFill.SiteLimited }).Stopped("p", At, settings.All).PausedFor);
    }

    /// <summary>Written anew, its held pages' last lines alone are kept and its generation raised; another searcher reads it again from its start.</summary>
    [Fact]
    public void Compacting_KeepsHeldPages_AndRaisesTheGeneration()
    {
        using var directory = new TempDirectory();
        new WebIndexState(Site, 4, 2, At, At, null, null).Write(Files, directory.Path);
        Append(directory, Page("/a", "One", [], "first"));
        Append(directory, Page("/a", "One again", [], "first again"));
        Append(directory, Page("/b", "Two", [], "second"));
        Append(directory, WebIndex.GoneLine($"{Site}/b"));
        Append(directory, Page("/c", "Three", [], "third"));

        var other = Index(directory);
        other.Refresh();

        var state = Index(directory).Compact();

        Assert.Equal(5, state.Generation);
        Assert.Equal(2, state.Known);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(directory.Path, WebIndex.PagesFileName)).Length);

        other.Refresh();
        Assert.Equal(2, other.Lines);
        Assert.Equal(2, other.Held);
        Assert.Equal("One again", Hits(other, "first", 5).Single().Title);
        Assert.Equal("Three", Hits(other, "third", 5).Single().Title);
    }

    /// <summary>
    /// A page that could not be read is remembered with when, a page held staying as it was; a later read or a line that
    /// says it is gone forgets it; and a file written anew keeps what it remembers.
    /// </summary>
    [Fact]
    public void APageThatCouldNotBeRead_IsRemembered_UntilReadOrGone()
    {
        using var directory = new TempDirectory();
        Append(directory, Page("/a", "One", [], "first"));
        Append(directory, WebIndex.FailedLine($"{Site}/a", At, "it answered 503"));
        Append(directory, WebIndex.FailedLine($"{Site}/b", At, "it answered 403"));
        Append(directory, WebIndex.FailedLine($"{Site}/c", At + TimeSpan.FromHours(1), "it answered 403"));
        Append(directory, Page("/c", "Three", [], "third"));
        Append(directory, WebIndex.FailedLine($"{Site}/d", At, "it answered 403"));
        Append(directory, WebIndex.GoneLine($"{Site}/d"));
        Append(directory, Page("/x", "Stale", [], "stale"));
        Append(directory, WebIndex.GoneLine($"{Site}/x"));

        var index = Index(directory);
        index.Refresh();

        Assert.Equal((2, 2), (index.Held, index.Failed));
        Assert.Equal(new Dictionary<string, (DateTimeOffset, string)> { [$"{Site}/a"] = (At, "it answered 503"), [$"{Site}/b"] = (At, "it answered 403") }, index.Failures());
        Assert.Equal("One", Hits(index, "first", 5).Single().Title);

        index.Compact();

        Assert.Equal(4, File.ReadAllLines(Path.Combine(directory.Path, WebIndex.PagesFileName)).Length);

        var again = Index(directory);
        again.Refresh();
        Assert.Equal((2, 2), (again.Held, again.Failed));
    }

    /// <summary>A query of no term the index holds, or of none at all, finds nothing; an empty index finds nothing.</summary>
    [Fact]
    public void AQueryOfNoKnownTerm_FindsNothing()
    {
        using var directory = new TempDirectory();
        var empty = Index(directory);
        empty.Refresh();
        Assert.Empty(Hits(empty, "anything", 5));

        Append(directory, Page("/a", "One", [], "first"));
        var index = Index(directory);
        index.Refresh();

        Assert.Empty(Hits(index, "zebra", 5));
        Assert.Empty(Hits(index, "!!", 5));
    }

    /// <summary>Terms are runs of letters and digits in lower case, any script's, every one however long.</summary>
    [Fact]
    public void Terms_AreRunsOfLettersAndDigits()
        => Assert.Equal(["über", "café", "x2", new string('a', 65), "日本語"], WebIndexTerms.Of($"Über-CAFÉ x2 {new string('a', 65)} 日本語").ToList());

    /// <summary>How much an index held is told as an agent reads it.</summary>
    [Theory]
    [InlineData(240, 1904, true, "indexed 240 of 1,904 pages of digital.gov; the rest are being read")]
    [InlineData(240, 1904, false, "indexed 240 of 1,904 pages of digital.gov")]
    [InlineData(0, null, true, "indexed 0 pages of digital.gov; its sitemaps are being read")]
    [InlineData(5, 5, true, "indexed 5 of 5 pages of digital.gov; they are being read again")]
    [InlineData(5, 3, false, "indexed 5 pages of digital.gov")]
    public void HowMuchItHeld_IsTold(int held, int? known, bool filling, string said)
        => Assert.Equal(said, new WebIndexAnswer("https://digital.gov", [], held, known, filling, null, null).Said);

    private static WebIndex Index(TempDirectory directory) => new(Files, directory.Path, Site);

    private static IReadOnlyList<WebIndexHit> Hits(WebIndex index, string query, int size, Func<string, bool>? keep = null)
        => index.Search(query, new WebSearchPage(1, size), keep).Hits;

    private static string Page(string path, string title, string[] headings, string text)
        => WebIndex.PageLine(Site + path, null, At, new WebPageParts(title, headings, text));

    private static void Append(TempDirectory directory, string line)
        => File.AppendAllText(Path.Combine(directory.Path, WebIndex.PagesFileName), line + "\n");
}
