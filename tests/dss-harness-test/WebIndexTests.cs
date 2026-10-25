using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Web;
using DssHarness.Core.Web.Index;

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

        var hits = index.Search("Tokenizer", 10);

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

        Assert.Equal([$"{Site}/title", $"{Site}/text"], index.Search("tokenizer", 2).Select(hit => hit.Address));
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

        Assert.Equal(other.Search("parser", 5).Single().Score, one.Search("parser", 5).Single().Score, 9);
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
        Assert.Equal([$"{Site}/a"], index.Search("parser", 5).Select(hit => hit.Address));
    }

    /// <summary>A source's settings are one fingerprint: any of what it reads changes it; how often it is read again does not.</summary>
    [Fact]
    public void ASourcesSettings_AreOneFingerprint()
    {
        var settings = new SitemapSettings(Site, null, [], [], 100, TimeSpan.FromDays(7));
        SitemapSettings[] changed =
        [
            settings with { Site = "https://other.example.test" },
            settings with { Sitemap = $"{Site}/s.xml" },
            settings with { Include = [GlobPattern.Parse("/a/**")] },
            settings with { Exclude = [GlobPattern.Parse("/a/**")] },
            settings with { MaxPages = 99 },
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

        Assert.Equal($"{Site}/heading", index.Search("lexer", 1).Single().Address);
        Assert.Equal($"{Site}/text", index.Search("lexer parser", 1).Single().Address);
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
        Assert.Equal($"{Site}/b", index.Search("second", 5).Single().Address);
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
        Assert.Empty(index.Search("obsolete", 5));
        Assert.Equal("New", index.Search("fresh", 5).Single().Title);
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

        var hits = index.Search("needle", 5);
        var first = hits.Single(hit => hit.Address == $"{Site}/a");
        var cut = hits.Single(hit => hit.Address == $"{Site}/long").Snippet;

        Assert.True(cut.Length <= WebIndex.MostField);
        Assert.EndsWith("word…", cut, StringComparison.Ordinal);

        Assert.Equal("The heading", first.Title);
        Assert.StartsWith("…", first.Snippet, StringComparison.Ordinal);
        Assert.Contains("the needle is here", first.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("fill ", first.Snippet, StringComparison.Ordinal);
        Assert.Equal($"{Site}/b", hits.Single(hit => hit.Address == $"{Site}/b").Title);
    }

    /// <summary>A page's text, its title and each of its first headings are kept to their most, never half a pair of surrogates.</summary>
    [Fact]
    public void APagesText_IsKeptToItsMost()
    {
        var title = new string('t', WebIndex.MostField - 1) + "\U0001F600" + "rest";
        var headings = Enumerable.Range(0, WebIndex.MostHeadings + 20).Select(at => new string('h', WebIndex.MostField + at)).ToList();
        var line = WebIndex.PageLine($"{Site}/a", "2026-01-01", At, new WebPageParts(title, headings, new string('x', WebIndex.MostCharacters + 500)));

        using var document = System.Text.Json.JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal(WebIndex.MostCharacters, root.GetProperty("text").GetString()!.Length);
        Assert.Equal(new string('t', WebIndex.MostField - 1), root.GetProperty("title").GetString());
        Assert.Equal(WebIndex.MostHeadings, root.GetProperty("headings").GetArrayLength());
        Assert.All(root.GetProperty("headings").EnumerateArray(), heading => Assert.Equal(WebIndex.MostField, heading.GetString()!.Length));
        Assert.Equal("2026-01-01", root.GetProperty("lastmod").GetString());

        using var failed = System.Text.Json.JsonDocument.Parse(WebIndex.FailedLine($"{Site}/a", At, new string('w', 1000)));
        Assert.Equal(WebIndex.MostField, failed.RootElement.GetProperty("why").GetString()!.Length);
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

        Assert.Equal(2, index.Search("parser", 5).Count);
        Assert.Equal([$"{Site}/blog/b"], index.Search("parser", 5, address => address.Contains("/blog/", StringComparison.Ordinal)).Select(hit => hit.Address));
        Assert.Equal(2, index.Held);

        // Each page is asked of once a search, however many of its terms were asked.
        var asked = new List<string>();
        index.Search("parser guide blog", 5, address =>
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
        var settings = new SitemapSettings(Site, null, [], [], 10, TimeSpan.FromDays(7));
        var filled = new WebIndexState(Site, 0, 2, At, At, null, null) { Settings = settings.Fingerprint };

        Assert.False(filled.Due(At + TimeSpan.FromDays(1), settings));
        Assert.True((filled with { FilledAt = null }).Due(At, settings));
        Assert.True((filled with { FillStartedAt = At + TimeSpan.FromHours(1) }).Due(At + TimeSpan.FromHours(2), settings));
        Assert.True(filled.Due(At + TimeSpan.FromDays(1), settings with { MaxPages = 11 }));
        Assert.True(filled.Due(At + TimeSpan.FromDays(1), settings with { Include = [DssHarness.Core.Repository.GlobPattern.Parse("/guides/**")] }));
        Assert.False(filled.Due(At + TimeSpan.FromDays(1), settings with { Refresh = TimeSpan.FromDays(2) }));
        Assert.True(filled.Due(At + TimeSpan.FromDays(7), settings));
        var stopped = filled.Stopped("its robots.txt could not be read", At + TimeSpan.FromHours(1), settings.All) with { FilledAt = null };
        Assert.False(stopped.Due(At, settings));
        Assert.True(stopped.Due(At + TimeSpan.FromHours(1), settings));

        // A pause after a problem holds the settings it stopped under alone - how long a page waits among them - and one the
        // site asked for holds whatever the settings.
        Assert.True(stopped.Due(At, settings with { MaxPages = 11 }));
        Assert.True(stopped.Due(At, settings with { Refresh = TimeSpan.FromDays(1) }));
        Assert.False((stopped with { PausedFor = SitemapFill.SiteLimited }).Due(At, settings with { MaxPages = 11 }));

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
        Assert.Equal("One again", other.Search("first", 5).Single().Title);
        Assert.Equal("Three", other.Search("third", 5).Single().Title);
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
        Assert.Equal("One", index.Search("first", 5).Single().Title);

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
        Assert.Empty(empty.Search("anything", 5));

        Append(directory, Page("/a", "One", [], "first"));
        var index = Index(directory);
        index.Refresh();

        Assert.Empty(index.Search("zebra", 5));
        Assert.Empty(index.Search("!!", 5));
    }

    /// <summary>Terms are runs of letters and digits in lower case, any script's, a run too long to be a word left out.</summary>
    [Fact]
    public void Terms_AreRunsOfLettersAndDigits()
        => Assert.Equal(["über", "café", "x2", "日本語"], WebIndexTerms.Of($"Über-CAFÉ x2 {new string('a', 65)} 日本語").ToList());

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

    private static string Page(string path, string title, string[] headings, string text)
        => WebIndex.PageLine(Site + path, null, At, new WebPageParts(title, headings, text));

    private static void Append(TempDirectory directory, string line)
        => File.AppendAllText(Path.Combine(directory.Path, WebIndex.PagesFileName), line + "\n");
}
