using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Configuration;
using DssHarness.Core.Output;
using DssHarness.Core.Results;
using DssHarness.Core.Web.Index;
using DssHarness.Core.Web.Search;
using DssHarness.Core.Web.Search.Kinds;
using Microsoft.Extensions.DependencyInjection;
using static DssHarness.Tests.WebSearchFixtures;

namespace DssHarness.Tests;

/// <summary>
/// The web search's kinds and their registry: built from the one scan the container makes, refusing two of one name; a
/// source held to its kind's keys; and <c>sitemap</c>'s keys and its answer from an index.
/// </summary>
public sealed class WebSearchKindsTests
{
    /// <summary>The container holds the four kinds the core implements, found by the one scan, and its registry builds.</summary>
    [Fact]
    public void TheContainer_HoldsEveryKind()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var kinds = provider.GetRequiredService<WebSearchKinds>();

        Assert.Equal(["feed", "json-api", "opensearch", "sitemap"], kinds.All.Select(kind => kind.Kind));
        Assert.Equal(WebSearchKinds.Implementations.Count, provider.GetServices<IWebSearchKind>().Count());
        Assert.Equal(kinds.All.Select(kind => kind.Kind), WebSearchKinds.OfThisBuild.All.Select(kind => kind.Kind));
        Assert.NotNull(provider.GetRequiredService<WebIndexes>());
    }

    /// <summary>Two kinds of one name are refused when the registry is built, never decided by an order.</summary>
    [Fact]
    public void TwoKindsOfOneName_AreRefused()
    {
        var refused = Assert.Throws<HarnessException>(() => new WebSearchKinds([new FeedKind(), new Named("feed")]));

        Assert.Equal(HarnessExit.InternalError, refused.ExitCode);
        Assert.Contains("two implementations of the web search's contract are named 'feed'", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A kind is refused for a name a source cannot write, a version below one, a key every source has or one taken twice, and saying nothing of itself.</summary>
    [Theory]
    [InlineData("Bad_Name", 1, "k", "help", "is not a kind's name: lower-case letters and digits joined by single hyphens")]
    [InlineData("bad-", 1, "k", "help", "is not a kind's name")]
    [InlineData("good", 0, "k", "help", "is at version 0")]
    [InlineData("good", 1, "perMinute", "help", "takes the key 'perMinute', which is every source's own")]
    [InlineData("good", 1, "twice", "help", "takes the key 'twice' twice")]
    [InlineData("good", 1, "k", " ", "says nothing of itself")]
    public void AKindThatDoesNotHold_IsRefused(string name, int version, string key, string help, string problem)
    {
        var keys = key == "twice" ? new KeyDescription[] { new(key, "a"), new(key, "b") } : [new(key, "a")];

        var refused = Assert.Throws<HarnessException>(() => new WebSearchKinds([new Named(name, version, keys, help)]));

        Assert.Contains(problem, refused.Message, StringComparison.Ordinal);
    }

    /// <summary>A source names a kind there is, gives the keys it requires and none it does not take.</summary>
    [Theory]
    [InlineData("""{ "url": "https://a.test/?q={query}" }""", "'kind' is missing: a source names its kind, one of feed, json-api, opensearch, sitemap")]
    [InlineData("""{ "kind": 3 }""", "'kind' is the name of a kind, one of feed, json-api, opensearch, sitemap")]
    [InlineData("""{ "kind": "duckduckgo" }""", "'kind' is 'duckduckgo', which is no kind; the kinds are feed, json-api, opensearch, sitemap")]
    [InlineData("""{ "kind": "json-api", "url": "https://a.test/?q={query}", "items": "$.i", "title": "{@.t}" }""", "'link' is missing: a source of kind 'json-api' gives it")]
    [InlineData("""{ "kind": "sitemap", "site": "https://a.test", "sitemaps": [] }""", "'sitemaps' is no key a source of kind 'sitemap' takes; it takes kind, tags, description, terms, enabled, perMinute, perDay, credential, site, sitemap, include, exclude, maxPages, refreshDays")]
    public void ASource_IsHeldToItsKind(string keys, string problem)
        => Assert.Contains(WebSearchKinds.OfThisBuild.Problems(Source("s", keys)), said => said.StartsWith(problem, StringComparison.Ordinal));

    /// <summary>A source's own keys, in any case, are no kind's problem; its kind is found in any case.</summary>
    [Fact]
    public void ASourcesOwnKeys_AreTaken()
        => Assert.Empty(WebSearchKinds.OfThisBuild.Problems(Source("s", """
            { "Kind": "Feed", "tags": ["blogs"], "description": "d", "enabled": true, "perMinute": 5, "perDay": 100, "credential": { "from": "gh" }, "URL": "https://a.test/?q={query}" }
            """)));

    /// <summary>
    /// The values of every source's own keys are held where its kind's are, whatever its kind - or with none: names for
    /// tags, a text that says something, a truth, paces within their bounds, a credential of one of its two shapes.
    /// </summary>
    [Theory]
    [InlineData("""{ "tags": "docs" }""", "'tags' is a list of names a step's search may choose the source by, each lower-case letters and digits joined by single hyphens")]
    [InlineData("""{ "tags": ["Docs"] }""", "'tags' is a list of names")]
    [InlineData("""{ "tags": ["two--hyphens"] }""", "'tags' is a list of names")]
    [InlineData("""{ "url": "https://a.test/?q={query}", "URL": "https://b.test/?q={query}" }""", "'url' is given twice, as 'url' and 'URL': a key is one whatever its case, and nothing could say which value was meant")]
    [InlineData("""{ "credential": { "from": "env", "From": "env", "name": "KEY", "query": "key" } }""", "'credential.from' is given twice, as 'credential.from' and 'credential.From'")]
    [InlineData("""{ "description": 5 }""", "'description' is a text, and is 5")]
    [InlineData("""{ "description": " " }""", "'description' says nothing; it is what the agent is told the source is for")]
    [InlineData("""{ "enabled": "no" }""", "'enabled' is true or false, and is \"no\"")]
    [InlineData("""{ "perMinute": 0 }""", "'perMinute' is a whole number from 1 to 600, and is 0")]
    [InlineData("""{ "perMinute": 601 }""", "'perMinute' is a whole number from 1 to 600, and is 601")]
    [InlineData("""{ "perMinute": "10" }""", "'perMinute' is a whole number from 1 to 600, and is \"10\"")]
    [InlineData("""{ "perDay": 0 }""", "'perDay' is a whole number from 1 to 1,000,000, and is 0")]
    [InlineData("""{ "credential": "KEY" }""", "'credential' is an object: a key to send it")]
    [InlineData("""{ "credential": { "from": "vault" } }""", "'credential.from' is env, a variable of the .env slot, or gh, the machine's gh login")]
    [InlineData("""{ "credential": { "from": "env", "query": "key" } }""", "'credential.name' is missing: the variable of the .env slot that holds the key")]
    [InlineData("""{ "credential": { "from": "env", "name": "my_key", "query": "key" } }""", "'credential.name' is a variable's name, [A-Z][A-Z0-9_]*, and is 'my_key'")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY" } }""", "'credential' says where the key is sent: 'query', a parameter, or 'header', a header")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY", "query": "key", "header": "X-Key" } }""", "'credential' is sent as a query parameter or as a header, and names both")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY", "query": "a&b" } }""", "'credential.query' is a query parameter's name")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY", "header": "X Key" } }""", "'credential.header' is a header's name, and is 'X Key'")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY", "header": "User-Agent" } }""", "'credential.header' is User-Agent, which the request itself says")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY", "header": "Allow" } }""", "'credential.header' is 'Allow', which a body says of itself and no request carries")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY", "header": "X-Key", "format": "Token {key}" } }""", "'credential.format' writes what is sent, naming {credential} for the key and no other name, and is 'Token {key}'")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY", "header": "X-Key", "format": "fixed" } }""", "'credential.format' writes what is sent")]
    [InlineData("""{ "credential": { "from": "gh", "name": "KEY" } }""", "'credential' takes from, host, query, header, format from gh, and not 'name'")]
    [InlineData("""{ "credential": { "from": "gh", "host": "https://github.com" } }""", "'credential.host' is the host the machine's gh login is for, as github.com, and is 'https://github.com'")]
    [InlineData("""{ "credential": { "from": "env", "name": "KEY", "query": "key", "host": "github.com" } }""", "'credential' takes from, name, query, header, format from env, and not 'host'")]
    [InlineData("""{ "credential": { "from": "env", "name": 5, "query": "key" } }""", "'credential.name' is a text, and is 5")]
    public void ASourcesOwnKeys_AreHeld(string overrides, string problem)
    {
        foreach (var kind in new[] { """{ "kind": "feed", "url": "https://a.test/?q={query}" }""", """{ "kind": "nothing" }""", """{ "url": "https://a.test/?q={query}" }""" })
        {
            var keys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(kind)!;

            foreach (var (key, value) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(overrides)!)
            {
                keys[key] = value;
            }

            Assert.Contains(WebSearchKinds.OfThisBuild.Problems(Source("s", System.Text.Json.JsonSerializer.Serialize(keys))), said => said.StartsWith(problem, StringComparison.Ordinal));
        }
    }

    /// <summary>An index's titles and snippets are its own text, never read as markup again.</summary>
    [Fact]
    public void AnIndexsText_IsNeverReadAsMarkup()
    {
        var hit = new DssHarness.Core.Web.Index.WebIndexHit("https://developer.example.test/script", "<script>: The Script element", "Use the <script> element & &lt; more", 1);
        var answer = new SitemapKind().Read(
            Source("site", """{ "kind": "sitemap", "site": "https://developer.example.test" }"""),
            WebSearchFound.None,
            new WebSearchResponse.Indexed(new DssHarness.Core.Web.Index.WebIndexAnswer("https://developer.example.test", [hit], 1, 1, false, null, null)));

        Assert.Equal([new WebSearchResult("<script>: The Script element", "https://developer.example.test/script", "Use the <script> element & &lt; more")], answer.Results);
    }

    /// <summary>A source's pace on this machine is what it says, and 30 requests a minute where it says none.</summary>
    [Fact]
    public void ASourcesPace_IsWhatItSays()
        => Assert.Equal((30, 6), (Source("s", """{ "kind": "feed" }""").PerMinute, Source("s", """{ "kind": "feed", "perMinute": 6 }""").PerMinute));

    /// <summary>A credential's place: a slot's variable where it says, the gh login as GitHub's bearer token unless it says otherwise; its keys in any case.</summary>
    [Fact]
    public void ACredential_IsSentWhereItSays()
    {
        static WebSearchCredential Of(string json) => WebSearchCredential.Of(System.Text.Json.JsonDocument.Parse(json).RootElement);

        Assert.Empty(WebSearchCredential.Problems(System.Text.Json.JsonDocument.Parse("""{ "From": "ENV", "Name": "KEY", "Header": "X-Key" }""").RootElement));
        Assert.Equal(new WebSearchCredential("env", "KEY", null, "X-Key", "{credential}"), Of("""{ "From": "ENV", "Name": "KEY", "Header": "X-Key" }"""));

        Assert.Equal(new WebSearchCredential("env", "KEY", "key", null, "{credential}"), Of("""{ "from": "env", "name": "KEY", "query": "key" }"""));
        Assert.Equal(new WebSearchCredential("env", "KEY", null, "X-Key", "Token {credential}"), Of("""{ "from": "env", "name": "KEY", "header": "X-Key", "format": "Token {credential}" }"""));
        Assert.Equal(new WebSearchCredential("gh", null, null, "Authorization", "Bearer {credential}", "github.com"), Of("""{ "from": "gh" }"""));
        Assert.Equal(new WebSearchCredential("gh", null, "access_token", null, "{credential}", "ghe.example.test"), Of("""{ "from": "gh", "query": "access_token", "host": "GHE.example.test" }"""));

        // A gh login is sent to its host or that host's API alone; a slot's key to whatever its source first asks.
        Assert.Equal(["github.com", "api.github.com"], Of("""{ "from": "gh" }""").Fetched("t").Hosts);
        Assert.Null(Of("""{ "from": "env", "name": "KEY", "query": "key" }""").Fetched("t").Hosts);
    }

    /// <summary>A sitemap source's keys are held: a site's scheme and host alone, an address for its sitemap, patterns, bounds.</summary>
    [Theory]
    [InlineData("""{ "site": "https://a.test/docs" }""", "'site' is a site's scheme and host alone, as https://a.test, and 'https://a.test/docs' names a path; say the pages read by include")]
    [InlineData("""{ "site": "a.test" }""", "'site' is an http or https address, written whole, and is 'a.test'")]
    [InlineData("""{ "sitemap": "/sitemap.xml" }""", "'sitemap' is an http or https address, written whole")]
    [InlineData("""{ "include": "/guides/**" }""", "'include' is a list of patterns of paths, as [\"/guides/**\"]")]
    [InlineData("""{ "exclude": ["/a/../b"] }""", "'exclude' holds '/a/../b', which holds a '..' segment")]
    [InlineData("""{ "maxPages": 20001 }""", "'maxPages' is a whole number from 1 to 20,000, and is 20001")]
    [InlineData("""{ "maxPages": 0 }""", "'maxPages' is a whole number from 1 to 20,000, and is 0")]
    [InlineData("""{ "refreshDays": 1.5 }""", "'refreshDays' is a whole number from 1 to 365, and is 1.5")]
    public void ASitemapSourcesKeys_AreHeld(string overrides, string problem)
    {
        var keys = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>("""{ "kind": "sitemap", "site": "https://a.test" }""")!;

        foreach (var (key, value) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(overrides)!)
        {
            keys[key] = value;
        }

        Assert.Contains(new SitemapKind().Problems(Source("s", System.Text.Json.JsonSerializer.Serialize(keys))), said => said.StartsWith(problem, StringComparison.Ordinal));
    }

    /// <summary>A sitemap source's settings: its site's scheme and host, its patterns, and the defaults where it says none.</summary>
    [Fact]
    public void ASitemapSourcesSettings_TakeTheirDefaults()
    {
        var full = SitemapKind.Settings(Source("gov", """{ "kind": "sitemap", "site": "https://Digital.gov/", "sitemap": "https://digital.gov/sitemap.xml", "include": ["/guides/**"], "exclude": ["/guides/old/**"], "maxPages": 1500, "refreshDays": 14, "perMinute": 6 }"""));

        Assert.Equal(("https://digital.gov", "https://digital.gov/sitemap.xml", 1500, TimeSpan.FromDays(14)), (full.Site, full.Sitemap, full.MaxPages, full.Refresh));
        Assert.True(full.Reads("/guides/web/"));
        Assert.False(full.Reads("/guides/old/x"));
        Assert.False(full.Reads("/blog/x"));
        Assert.False(full.Reads("/"));

        var bare = SitemapKind.Settings(Source("mdn", """{ "kind": "sitemap", "site": "https://developer.example.test" }"""));

        Assert.Equal(((string?)null, SitemapKind.DefaultMaxPages, TimeSpan.FromDays(SitemapKind.DefaultRefreshDays)), (bare.Sitemap, bare.MaxPages, bare.Refresh));
        Assert.True(bare.Reads("/"));
        Assert.True(bare.Reads("/a//b"));
    }

    /// <summary>A sitemap source asks its index, and reads its answer: hits, how much was held, and why it holds none.</summary>
    [Fact]
    public void ASitemapSource_IsAnsweredByItsIndex()
    {
        var kind = new SitemapKind();
        var source = Source("gov", """{ "kind": "sitemap", "site": "https://digital.gov" }""");

        var request = Assert.IsType<WebSearchRequest.Indexed>(kind.Request(source, WebSearchFound.None, "forms", 4));

        Assert.Equal(("forms", 4, SitemapKind.Settings(source).Fingerprint), (request.Query, request.Limit, request.Settings.Fingerprint));
        Assert.Empty(kind.Lookups(source));

        var hits = kind.Read(source, WebSearchFound.None, Indexed(new("https://digital.gov", [new("https://digital.gov/guides/forms", "Forms", "How to build forms.", 3.2)], 240, 1904, true, null, null)));

        Assert.Equal(WebSearchOutcome.Ok, hits.Outcome);
        Assert.Equal(new WebSearchResult("Forms", "https://digital.gov/guides/forms", "How to build forms."), Assert.Single(hits.Results));
        Assert.Equal("indexed 240 of 1,904 pages of digital.gov; the rest are being read", hits.Held);

        var reading = kind.Read(source, WebSearchFound.None, Indexed(new("https://digital.gov", [], 0, null, true, null, null)));
        Assert.Equal((WebSearchOutcome.NoResults, "indexed 0 pages of digital.gov; its sitemaps are being read"), (reading.Outcome, reading.Held));

        var paused = new DateTimeOffset(2026, 10, 25, 14, 32, 0, TimeSpan.Zero);
        var limited = kind.Read(source, WebSearchFound.None, Indexed(new("https://digital.gov", [], 0, null, false, null, paused) { PausedFor = DssHarness.Core.Web.Index.SitemapFill.SiteLimited }));
        Assert.Equal((WebSearchOutcome.RateLimited, paused), (limited.Outcome, limited.Until));
        Assert.StartsWith("the site asks to be left alone until ", limited.Why, StringComparison.Ordinal);

        // Held off after one stopped short: the hold is what the agent waits for, the problem said beside it.
        var heldAfter = kind.Read(source, WebSearchFound.None, Indexed(new("https://digital.gov", [], 0, null, false, "its robots.txt could not be read", paused) { PausedFor = DssHarness.Core.Web.Index.SitemapFill.SiteLimited }));
        Assert.Equal((WebSearchOutcome.RateLimited, paused), (heldAfter.Outcome, heldAfter.Until));
        Assert.EndsWith("no fill has ended whole since one stopped short: its robots.txt could not be read", heldAfter.Held, StringComparison.Ordinal);

        var failed = kind.Read(source, WebSearchFound.None, Indexed(new("https://digital.gov", [], 0, null, false, "its robots.txt could not be read", paused)));
        Assert.Equal((WebSearchOutcome.Failed, "its index holds no page: its robots.txt could not be read"), (failed.Outcome, failed.Why));

        Assert.Throws<ArgumentException>(() => kind.Read(source, WebSearchFound.None, Answer("{}")));
    }

    private static WebSearchResponse.Indexed Indexed(WebIndexAnswer answer) => new(answer);

    /// <summary>A kind of a given name, version, keys and help: what a registry is built from to be refused.</summary>
    private sealed class Named(string kind, int version = 1, IReadOnlyList<KeyDescription>? keys = null, string help = "help") : IWebSearchKind
    {
        public string Kind => kind;

        public int Version => version;

        public IReadOnlyList<KeyDescription> Keys => keys ?? [];

        public string Help => help;

        public IReadOnlyList<string> Problems(WebSearchSource source) => [];

        public IReadOnlyList<WebSearchRequest.Http> Lookups(WebSearchSource source) => [];

        public WebSearchFound Found(WebSearchSource source, IReadOnlyList<WebSearchResponse.Http> answers) => WebSearchFound.None;

        public WebSearchRequest Request(WebSearchSource source, WebSearchFound found, string query, int limit) => WebSearchRequest.Http.Get("https://a.test/", "application/json");

        public WebSearchAnswer Read(WebSearchSource source, WebSearchFound found, WebSearchResponse response) => WebSearchAnswer.Read([]);
    }
}
