using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Web.Search;
using static DssHarness.Tests.WebSearchFixtures;

namespace DssHarness.Tests;

/// <summary>
/// The configuration's web search, resolved: the built-in sources, a repository's entries laid over them key by key, the
/// problems of a source, a name, a tag, the defaults and the bounds (V56, and V55 of the defaults), and the sources a
/// step's <c>search</c> picks.
/// </summary>
public sealed class WebSearchSourcesTests
{
    /// <summary>Every built-in source is named, holds as its kind reads it, and the defaults leave out the two that are not for every step.</summary>
    [Fact]
    public void TheBuiltInSources_HoldAndAreEveryOneNamed()
    {
        var sources = Of(null);

        Assert.Empty(sources.Problems);
        Assert.Equal(
            ["stackoverflow", "github-repos", "github-issues", "nuget", "npm", "pub-dev", "learn", "mdn", "wikipedia", "hackernews", "readthedocs"],
            sources.All.Select(source => source.Name));
        Assert.Equal(["qa", "code", "packages", "docs", "reference", "blogs"], sources.Defaults);
        Assert.Equal((WebSearchSources.DefaultMaxPerStep, WebSearchSources.DefaultResultsPerSource), (sources.MaxPerStep, sources.ResultsPerSource));
        Assert.Equal(
            ["stackoverflow", "github-repos", "github-issues", "nuget", "npm", "pub-dev", "learn", "wikipedia", "hackernews"],
            sources.Chosen(null).Select(source => source.Name));
        Assert.All(sources.All, source => Assert.False(string.IsNullOrWhiteSpace(source.Description) || string.IsNullOrWhiteSpace(source.Terms), source.Name));
        Assert.All(sources.All.Where(source => source.Name.StartsWith("github-", StringComparison.Ordinal)), source => Assert.Equal("gh", source.Credential?.From));
    }

    /// <summary>Each built-in source that answers JSON reads its owner's documented answer, as the built-in entry writes it.</summary>
    [Theory]
    [InlineData("stackoverflow", "stackexchange-excerpts.json", "Grouping a List<T> by key with LINQ|https://stackoverflow.com/q/1010101;Counting groups|https://stackoverflow.com/q/2020202", null)]
    [InlineData("github-repos", "github-search-repositories.json", "example-org/fast-json|https://github.com/example-org/fast-json;example-org/no-description|https://github.com/example-org/no-description", 1234L)]
    [InlineData("github-issues", "github-search-issues.json", "The server can deadlock registering a file watch|https://github.com/example-org/example-server/issues/70;Fix a deadlock on shutdown|https://github.com/example-org/example-client/pull/12", 4452L)]
    [InlineData("nuget", "nuget-search.json", "Example.Json|https://www.nuget.org/packages/Example.Json;Example.Json.Extensions|https://www.nuget.org/packages/Example.Json.Extensions", 2L)]
    [InlineData("npm", "npm-search.json", "fast-glob-example|https://www.npmjs.com/package/fast-glob-example", 1L)]
    [InlineData("pub-dev", "pubdev-search.json", "http_example|https://pub.dev/packages/http_example;http_example_retry|https://pub.dev/packages/http_example_retry", null)]
    [InlineData("learn", "learn-search.json", "Group results by key - C#|https://learn.microsoft.com/en-us/dotnet/csharp/linq/standard-query-operators/grouping-data;Enumerable.GroupBy Method|https://learn.microsoft.com/en-us/dotnet/api/system.linq.enumerable.groupby", 12107L)]
    [InlineData("wikipedia", "wikipedia-search.json", "Binary search|https://en.wikipedia.org/wiki/Binary_search", null)]
    [InlineData("hackernews", "hackernews-search.json", "Writing a parser by hand|https://blog.example.test/parser;Ask HN: How do you test parsers?|https://news.ycombinator.com/item?id=41000002", 2L)]
    [InlineData("readthedocs", "readthedocs-search.json", "Configuration|https://example-docs.readthedocs.io/en/stable/config.html", 1L)]
    public void EachBuiltInSource_ReadsItsOwnersAnswer(string name, string stored, string expected, long? total)
    {
        var source = Of(null).Find(name)!;
        var kind = WebSearchKinds.OfThisBuild.Find(source.Kind)!;
        var found = kind.Lookups(source) is { Count: > 0 } lookups
            ? kind.Found(source, [.. lookups.Select(lookup => Answer(Stored("nuget-index.json"), address: lookup.Address))])
            : WebSearchFound.None;

        Assert.Null(found.Failure);
        var request = Assert.IsType<WebSearchRequest.Http>(kind.Request(source, found, "group by", 2));
        Assert.Contains("group%20by", request.Address, StringComparison.Ordinal);

        var answer = kind.Read(source, found, Answer(Stored(stored), address: request.Address));

        Assert.Equal(WebSearchOutcome.Ok, answer.Outcome);
        Assert.Equal(expected, string.Join(";", answer.Results.Select(result => $"{result.Title}|{result.Link}")));
        Assert.Equal(total, answer.Total);
    }

    /// <summary>A step's search is given the sources its search picks, with the configuration's bounds and the keys it was given.</summary>
    [Fact]
    public void AStepsSearch_IsGivenItsSourcesAndBounds()
    {
        var sources = Of("""{ "maxPerStep": 5, "resultsPerSource": 2 }""");
        var keys = new Dictionary<string, WebSearchKey> { ["learn"] = new(null, "none") };

        var search = DssHarness.Core.Agents.AgentWebSearch.Of(sources, ["docs", "reference"], keys);

        Assert.Equal(["learn", "wikipedia"], search.Sources.Select(source => source.Name));
        Assert.Equal((2, 5), (search.ResultsPerSource, search.MaxPerStep));
        Assert.Same(keys, search.Keys);
        Assert.Equal(sources.Chosen(null), DssHarness.Core.Agents.AgentWebSearch.Of(sources, null, keys).Sources);
    }

    /// <summary>The built-in sitemap source is answered by its index, of the pages its entry names.</summary>
    [Fact]
    public void TheBuiltInSitemapSource_IsAnsweredByItsIndex()
    {
        var mdn = Of(null).Find("mdn")!;
        var request = Assert.IsType<WebSearchRequest.Indexed>(WebSearchKinds.OfThisBuild.Find(mdn.Kind)!.Request(mdn, WebSearchFound.None, "flexbox", 8));

        Assert.Equal(("flexbox", 8), (request.Query, request.Limit));
        Assert.Equal(("https://developer.mozilla.org", "https://developer.mozilla.org/sitemaps/en-us/sitemap.xml.gz", 6000), (request.Settings.Site, request.Settings.Sitemap, request.Settings.MaxPages));
        Assert.Equal(["en-US/docs/Web/**"], request.Settings.Include.Select(pattern => pattern.ToString()));
    }

    /// <summary>
    /// A repository's entry of a built-in's name changes it key by key, a key in another case replacing the built-in's;
    /// <c>enabled: false</c> turns one off; and an entry of a new name adds a source after the built-in ones.
    /// </summary>
    [Fact]
    public void ARepositorysEntry_ChangesABuiltIn_KeyByKey()
    {
        var sources = Of("""
            {
              "sources": {
                "stackoverflow": { "PERMINUTE": 5, "perDay": 10, "credential": { "from": "env", "name": "STACKEXCHANGE_KEY", "query": "key" } },
                "mdn": { "enabled": false },
                "gov-feed": { "kind": "feed", "tags": ["gov"], "url": "https://gov.example.test/feed?q={query}" }
              }
            }
            """);

        Assert.Empty(sources.Problems);

        var stackoverflow = sources.Find("stackoverflow")!;
        Assert.Equal((5, 10), (stackoverflow.PerMinute, stackoverflow.PerDay));
        Assert.Equal(["PERMINUTE"], stackoverflow.Keys.Where(key => key.Equals("perMinute", StringComparison.OrdinalIgnoreCase)));
        Assert.StartsWith("https://api.stackexchange.com/2.3/search/excerpts", stackoverflow.Text("url"), StringComparison.Ordinal);
        Assert.Equal(("env", "STACKEXCHANGE_KEY", "key"), (stackoverflow.Credential!.From, stackoverflow.Credential.Name, stackoverflow.Credential.Query));

        Assert.False(sources.Find("mdn")!.Enabled);
        Assert.Equal("gov-feed", sources.All[^1].Name);
        Assert.Equal(["gov-feed"], sources.Chosen(["gov", "mdn"]).Select(source => source.Name));
    }

    /// <summary>
    /// A step's list picks its sources in the order it names them - a tag's in the configuration's order - each once, and
    /// those turned on alone.
    /// </summary>
    [Fact]
    public void AStepsList_PicksSourcesInItsOrder_EachOnce()
    {
        var sources = Of("""{ "sources": { "learn": { "enabled": false } } }""");

        Assert.Equal(
            ["wikipedia", "nuget", "npm", "pub-dev", "stackoverflow"],
            sources.Chosen(["reference", "packages", "nuget", "stackoverflow"]).Select(source => source.Name));
        Assert.Empty(sources.Chosen(["learn"]));
    }

    /// <summary>Each source is held to its kind and every source's own keys, each problem naming where it is (V56), and nothing else is said.</summary>
    [Theory]
    [InlineData("""{ "sources": { "gov": { "kind": "json-api", "url": "https://gov.test/?q={query}", "title": "{@.t}", "link": "{@.u}" } } }""", "agentWebSearch.sources.gov (json-api): 'items' is missing: a source of kind 'json-api' gives it - a JSONPath query of the answer that finds the array of results, as $.results")]
    [InlineData("""{ "sources": { "gov": { "kind": "sitemap", "site": "https://gov.test", "url": "https://gov.test" } } }""", "agentWebSearch.sources.gov (sitemap): 'url' is no key a source of kind 'sitemap' takes; it takes kind, tags, description, terms, enabled, perMinute, perDay, credential, site, sitemap, include, exclude, maxPages, refreshDays")]
    [InlineData("""{ "sources": { "gov": { "kind": "duckduckgo" } } }""", "agentWebSearch.sources.gov (duckduckgo): 'kind' is 'duckduckgo', which is no kind; the kinds are feed, json-api, opensearch, sitemap")]
    [InlineData("""{ "sources": { "gov": { "url": "https://gov.test/?q={query}" } } }""", "agentWebSearch.sources.gov: 'kind' is missing: a source names its kind, one of feed, json-api, opensearch, sitemap")]
    [InlineData("""{ "sources": { "gov": 3 } }""", "agentWebSearch.sources.gov is an object of the source's keys, and is 3")]
    [InlineData("""{ "sources": { "Gov_Docs": { "kind": "feed", "url": "https://gov.test/?q={query}" } } }""", "agentWebSearch.sources.Gov_Docs is no source's name: a source's name names its index's directory, and is lower-case letters and digits joined by single hyphens")]
    [InlineData("""{ "sources": { "stackoverflow": { "perMinute": 5, "PerMinute": 6 } } }""", "agentWebSearch.sources.stackoverflow: 'perMinute' is given twice, as 'perMinute' and 'PerMinute': a key is one whatever its case, and nothing could say which value was meant")]
    [InlineData("""{ "sources": { "stackoverflow": { "perMinute": 0 } } }""", "agentWebSearch.sources.stackoverflow (json-api): 'perMinute' is a whole number from 1 to 600, and is 0")]
    [InlineData("""{ "sources": { "stackoverflow": { "terms": " " } } }""", "agentWebSearch.sources.stackoverflow (json-api): 'terms' says nothing; it is what its owner's terms say of programs asking it, which help says beside it")]
    [InlineData("""{ "sources": { "stackoverflow": { "tags": ["npm"] } } }""", "agentWebSearch: 'npm' names both a source and a tag, and a step's search could not say which it means")]
    [InlineData("""{ "maxPerStep": 0 }""", "agentWebSearch.maxPerStep is 0; it is the most requests of sources one step makes, from 1 to 1,000")]
    [InlineData("""{ "maxPerStep": 1001 }""", "agentWebSearch.maxPerStep is 1001; it is the most requests of sources one step makes, from 1 to 1,000")]
    [InlineData("""{ "resultsPerSource": 21 }""", "agentWebSearch.resultsPerSource is 21; it is the most results a source gives one search, from 1 to 20")]
    [InlineData("""{ "defaults": [] }""", "agentWebSearch.defaults is given empty, which is never read as the built-in defaults: name sources or tags, or leave the key out for the built-in defaults")]
    [InlineData("""{ "defaults": ["qa", "qa"] }""", "agentWebSearch.defaults names 'qa' more than once")]
    [InlineData("""{ "defaults": ["gov"] }""", "agentWebSearch.defaults names 'gov', which is no source or tag of agentWebSearch; sources: stackoverflow, github-repos, github-issues, nuget, npm, pub-dev, learn, mdn, wikipedia, hackernews, readthedocs; tags: qa, code, packages, dotnet, js, dart, docs, docs-web, reference, blogs, docs-rtd")]
    public void AWebSearchThatCannotBeUsed_IsRefused_NamingWhere(string section, string problem)
        => Assert.Equal([problem], Of(section).Problems);

    /// <summary>
    /// A source turned off is left out of the defaults, never refused there - turning one off is one key of its own - and a
    /// tag none of whose sources is on gives nothing.
    /// </summary>
    [Fact]
    public void TheDefaults_LeaveOutASourceTurnedOff()
    {
        var sources = Of("""{ "defaults": ["mdn", "docs-web", "qa"], "sources": { "mdn": { "enabled": false } } }""");

        Assert.Empty(sources.Problems);
        Assert.Equal(["stackoverflow"], sources.Chosen(null).Select(source => source.Name));
        Assert.Empty(Of("""{ "sources": { "hackernews": { "enabled": false } } }""").Problems);
    }

    /// <summary>The keys of agentWebSearch say every key a source takes of its own, read from the source's own list.</summary>
    [Fact]
    public void TheSectionsKeys_NameEverySourcesOwnKey()
        => Assert.Equal(
            $"each source by its name: its {string.Join(", ", WebSearchSource.OwnKeys.Select(key => key.Name))}, and its kind's own keys; a built-in's name changes it key by key, a new name adds a source",
            WebSearchSources.Keys.Single(key => key.Name == "sources").Meaning);

    /// <summary>A name given twice is refused whatever the configuration holds: the part a definition checked under no configuration is held to.</summary>
    [Fact]
    public void ANameGivenTwice_IsRefusedWithNoConfiguration()
    {
        Assert.Equal(["the search of step 'x' names 'a' more than once"], WebSearchSources.TwiceProblems(["a", "b", "a", "a"], "the search of step 'x'"));
        Assert.Empty(WebSearchSources.TwiceProblems(["a", "b", "A"], "the search of step 'x'"));
    }

    /// <summary>The configuration's web search, its section <paramref name="section"/> as JSON; the built-in one where none.</summary>
    internal static WebSearchSources Of(string? section)
        => WebSearchSources.Of(
            new HarnessConfig { AgentWebSearch = section is null ? null : JsonSerializer.Deserialize<AgentWebSearchConfig>(section, JsonConfigOptions.Default) },
            WebSearchKinds.OfThisBuild);
}
