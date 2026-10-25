using System.Text.Json;
using DssHarness.Core.Web;
using DssHarness.Core.Web.Search;
using DssHarness.Core.Web.Search.Kinds;
using static DssHarness.Tests.WebSearchFixtures;

namespace DssHarness.Tests;

/// <summary>
/// <c>json-api</c> over stored answers in the shapes the sources document: results read by JSONPath, none told as none, a
/// limit, a failure and a change each told as what it is - never an empty list for a failure - and its keys held.
/// </summary>
public sealed class JsonApiKindTests
{
    private const string StackOverflow = """
        {
          "kind": "json-api",
          "url": "https://api.stackexchange.com/2.3/search/excerpts?order=desc&sort=relevance&q={query}&site=stackoverflow&pagesize={limit}",
          "items": "$.items",
          "title": "{@.title}",
          "link": "https://stackoverflow.com/q/{@.question_id}",
          "snippet": "{@.excerpt}",
          "markup": ["title", "snippet"],
          "backoff": "$.backoff"
        }
        """;

    private const string GitHub = """
        {
          "kind": "json-api",
          "url": "https://api.github.com/search/repositories?q={query}&per_page={limit}",
          "headers": { "Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28" },
          "items": "$.items",
          "title": "{@.full_name}",
          "link": "{@.html_url}",
          "snippet": "{@.description}",
          "total": "$.total_count"
        }
        """;

    private const string NuGet = """
        {
          "kind": "json-api",
          "url": "{search}?q={query}&take={limit}",
          "discover": { "search": { "url": "https://example-packages.test/v3/index.json", "path": "$.resources[?@['@type']=='SearchQueryService']['@id']" } },
          "items": "$.data",
          "title": "{@.id}",
          "link": "https://www.nuget.org/packages/{@.id}",
          "snippet": "{@.description}",
          "total": "$.totalHits"
        }
        """;

    private static readonly JsonApiKind Kind = new();

    /// <summary>Stack Exchange's excerpts: titles and snippets reduced to text, a link made of an id, the body's backoff kept as a pause.</summary>
    [Fact]
    public void StackExchangesExcerpts_AreReadIntoResults_ItsBackoffAPause()
    {
        var answer = Read(StackOverflow, Stored("stackexchange-excerpts.json"));

        Assert.Equal(WebSearchOutcome.Ok, answer.Outcome);
        Assert.Equal(
            [
                new WebSearchResult("Grouping a List<T> by key with LINQ", "https://stackoverflow.com/q/1010101", "How do I group a List<T> by a key and count each group & keep the order?"),
                new WebSearchResult("Counting groups", "https://stackoverflow.com/q/2020202", "Use GroupBy and then Count()."),
            ],
            answer.Results);
        Assert.Equal(At + TimeSpan.FromSeconds(10), answer.Until);
        Assert.Null(answer.Total);
        Assert.Equal(0, answer.LeftOut);
    }

    /// <summary>GitHub's search: its total read, a description written in plain text kept as written, an item with none given no snippet.</summary>
    [Fact]
    public void GitHubsSearch_IsReadWithItsTotal_PlainTextKept()
    {
        var answer = Read(GitHub, Stored("github-search-repositories.json"));

        Assert.Equal(WebSearchOutcome.Ok, answer.Outcome);
        Assert.Equal(1234, answer.Total);
        Assert.Equal(
            [
                new WebSearchResult("example-org/fast-json", "https://github.com/example-org/fast-json", "A fast JSON reader for IAsyncEnumerable<T> streams"),
                new WebSearchResult("example-org/no-description", "https://github.com/example-org/no-description", string.Empty),
            ],
            answer.Results);
    }

    /// <summary>The documented shapes of the other sources each read: a nested link, a name made a link, a list of links, a link made of two values.</summary>
    [Theory]
    [InlineData("npm-search.json", """{ "kind": "json-api", "url": "https://registry.example.test/-/v1/search?text={query}&size={limit}", "items": "$.objects", "title": "{@.package.name}", "link": "{@.package.links.npm}", "snippet": "{@.package.description}", "total": "$.total" }""", "fast-glob-example|https://www.npmjs.com/package/fast-glob-example|A glob matcher")]
    [InlineData("wikipedia-search.json", """{ "kind": "json-api", "url": "https://en.wikipedia.org/w/rest.php/v1/search/page?q={query}&limit={limit}", "items": "$.pages", "title": "{@.title}", "link": "https://en.wikipedia.org/wiki/{@.key}", "snippet": "{@.excerpt}", "markup": ["snippet"] }""", "Binary search|https://en.wikipedia.org/wiki/Binary_search|In computer science, binary search finds a target in a sorted array")]
    [InlineData("hackernews-search.json", """{ "kind": "json-api", "url": "https://hn.algolia.com/api/v1/search?query={query}&tags=story&hitsPerPage={limit}", "items": "$.hits", "title": "{@.title}", "link": ["{@.url}", "https://news.ycombinator.com/item?id={@.objectID}"], "snippet": "{@.story_text}", "markup": ["snippet"] }""", "Writing a parser by hand|https://blog.example.test/parser|;Ask HN: How do you test parsers?|https://news.ycombinator.com/item?id=41000002|I keep breaking my parser.")]
    [InlineData("readthedocs-search.json", """{ "kind": "json-api", "url": "https://readthedocs.org/api/v3/search/?q={query}", "items": "$.results", "title": "{@.title}", "link": "{@.domain}{@.path}", "snippet": "{@.blocks[0].highlights.content[0]}", "total": "$.count", "markup": ["snippet"] }""", "Configuration|https://example-docs.readthedocs.io/en/stable/config.html|Every option, with its default.")]
    [InlineData("federal-register.json", """{ "kind": "json-api", "url": "https://www.example-register.test/api/v1/documents.json?conditions[term]={query}&per_page={limit}&order=relevance", "items": "$.results", "title": "{@.title}", "link": "{@.html_url}", "snippet": "{@.abstract}", "total": "$.count" }""", "Accessibility of web content|https://www.example-register.test/documents/2026/01/01/2026-00001/accessibility|This rule sets the standard for web content.;Notice on mobile apps|https://www.example-register.test/documents/2026/01/02/2026-00002/mobile|")]
    public void EachDocumentedShape_IsRead(string stored, string keys, string expected)
    {
        Assert.Empty(WebSearchKinds.OfThisBuild.Problems(Source("source", keys)));

        var answer = Read(keys, Stored(stored));

        Assert.Equal(WebSearchOutcome.Ok, answer.Outcome);
        Assert.Equal(expected, string.Join(";", answer.Results.Select(result => $"{result.Title}|{result.Link}|{result.Snippet}")));
    }

    /// <summary>An answer with an empty array of results is no results, its total read beside it.</summary>
    [Fact]
    public void AnEmptyArray_IsNoResults()
    {
        var answer = Read("""{ "kind": "json-api", "url": "https://api.example.test/?q={query}", "items": "$.results", "title": "{@.title}", "link": "{@.url}", "total": "$.count" }""", Stored("empty-results.json"));

        Assert.Equal(WebSearchOutcome.NoResults, answer.Outcome);
        Assert.Empty(answer.Results);
        Assert.Equal(0, answer.Total);
        Assert.Null(answer.Why);
    }

    /// <summary>
    /// A rate limit answers the query - 429; 403 or 503 with Retry-After, as GitHub's secondary limits answer - until the
    /// time it gave, never later than a day.
    /// </summary>
    [Theory]
    [InlineData(429, "Retry-After", "120", 120)]
    [InlineData(503, "Retry-After", "Sun, 25 Oct 2026 12:30:00 GMT", 1800)]
    [InlineData(403, "Retry-After", "60", 60)]
    [InlineData(429, null, null, 60)]
    [InlineData(429, "Retry-After", "999999999", 86400)]
    [InlineData(429, "Retry-After", "Thu, 25 Oct 2096 12:00:00 GMT", 86400)]
    [InlineData(403, "X-RateLimit-Remaining", "0", 60)]
    public void ARateLimit_IsToldWithItsTime(int status, string? header, string? value, int seconds)
    {
        var headers = header is null ? Headers() : Headers((header, value!));
        var answer = Kind.Read(Source("so", StackOverflow), WebSearchFound.None, Answer(Stored("stackexchange-throttle.json"), status, headers: headers));

        Assert.Equal(WebSearchOutcome.RateLimited, answer.Outcome);
        Assert.Equal(At + TimeSpan.FromSeconds(seconds), answer.Until);
        Assert.Empty(answer.Results);
        Assert.Contains("asks to be left alone", answer.Why, StringComparison.Ordinal);

        // A source that says not for how long is told so, and left a minute.
        Assert.Equal(header is null, answer.Why!.Contains("saying not for how long", StringComparison.Ordinal));
    }

    /// <summary>A header is read by its name in any case, whatever dictionary its answer comes in.</summary>
    [Fact]
    public void AHeader_IsReadInAnyCase()
        => Assert.Equal(At + TimeSpan.FromSeconds(30), WebRateLimit.Read(429, new Dictionary<string, string>(StringComparer.Ordinal) { ["retry-after"] = "30" }, At)!.Until);

    /// <summary>
    /// GitHub's limit - a 403 with nothing remaining and a reset in seconds since 1970 - is a limit until that time; a
    /// reset past a day from now is held to a day.
    /// </summary>
    [Fact]
    public void A403WithNothingRemaining_IsALimitUntilItsReset()
    {
        foreach (var (reset, until) in new[] { (At + TimeSpan.FromMinutes(7), At + TimeSpan.FromMinutes(7)), (At + TimeSpan.FromDays(3650), At + TimeSpan.FromDays(1)) })
        {
            var answer = Kind.Read(Source("gh", GitHub), WebSearchFound.None, Answer("""{"message":"API rate limit exceeded"}""", 403, headers: Headers(("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)))));

            Assert.Equal(WebSearchOutcome.RateLimited, answer.Outcome);
            Assert.Equal(until, answer.Until);
        }
    }

    /// <summary>A good answer whose headers say nothing remains keeps its results, and holds the source until the reset.</summary>
    [Theory]
    [InlineData("X-RateLimit-Remaining", "0", "X-RateLimit-Reset", "45", 45)]
    [InlineData("RateLimit", "\"default\";r=0;t=30", null, null, 30)]
    [InlineData("RateLimit", "limit=10, remaining=0, reset=20", null, null, 20)]
    [InlineData("RateLimit", "limit=10, remaining=0", "RateLimit-Reset", "25", 25)]
    public void AGoodAnswerWithNothingRemaining_KeepsItsResults_AndAPause(string header, string value, string? reset, string? resetValue, int seconds)
    {
        var headers = reset is null ? Headers((header, value)) : Headers((header, value), (reset, resetValue!));
        var answer = Kind.Read(Source("gh", GitHub), WebSearchFound.None, Answer(Stored("github-search-repositories.json"), headers: headers));

        Assert.Equal(WebSearchOutcome.Ok, answer.Outcome);
        Assert.Equal(2, answer.Results.Count);
        Assert.Equal(At + TimeSpan.FromSeconds(seconds), answer.Until);
    }

    /// <summary>Headers that say some remain hold nothing back.</summary>
    [Fact]
    public void HeadersThatSaySomeRemain_HoldNothingBack()
    {
        var answer = Kind.Read(Source("gh", GitHub), WebSearchFound.None, Answer(Stored("github-search-repositories.json"), headers: Headers(("X-RateLimit-Remaining", "9"), ("RateLimit", "\"default\";r=4;t=30"))));

        Assert.Null(answer.Until);
    }

    /// <summary>Another status fails the query, its body's own words told: a 503 with no Retry-After, a 400, a 500.</summary>
    [Theory]
    [InlineData(503)]
    [InlineData(400)]
    [InlineData(500)]
    public void AnotherStatus_FailsWithTheBodysWords(int status)
    {
        var answer = Kind.Read(Source("so", StackOverflow), WebSearchFound.None, Answer(Stored("stackexchange-throttle.json"), status));

        Assert.Equal(WebSearchOutcome.Failed, answer.Outcome);
        Assert.StartsWith($"it answered {status}: ", answer.Why, StringComparison.Ordinal);
        Assert.Contains("throttle_violation", answer.Why, StringComparison.Ordinal);
        Assert.Empty(answer.Results);
    }

    /// <summary>A failure's body is told cut short, at a word.</summary>
    [Fact]
    public void AFailuresLongBody_IsCut()
    {
        var answer = Kind.Read(Source("so", StackOverflow), WebSearchFound.None, Answer(string.Concat(Enumerable.Repeat("word ", 400)), 500, "text/plain"));

        Assert.EndsWith("word…", answer.Why, StringComparison.Ordinal);
        Assert.True(answer.Why!.Length <= "it answered 500: ".Length + 200);
    }

    /// <summary>A pause an answer's body asks for must be one: none at zero; a total below zero is none.</summary>
    [Fact]
    public void ABackoffOfNothing_AndATotalBelowZero_AreNone()
    {
        var answer = Read(
            """{ "kind": "json-api", "url": "https://a.test/?q={query}", "items": "$.items", "title": "{@.t}", "link": "{@.u}", "backoff": "$.backoff", "total": "$.total" }""",
            """{ "items": [ { "t": "T", "u": "https://a.test/x" } ], "backoff": 0, "total": -5 }""");

        Assert.Equal((WebSearchOutcome.Ok, (DateTimeOffset?)null, (long?)null), (answer.Outcome, answer.Until, answer.Total));
    }

    /// <summary>
    /// An answer is read to its most: a hundred items, each title cut as a snippet is, and no field filled past what is
    /// read of one - a link that reads the answer's root, as long as it likes, is no address.
    /// </summary>
    [Fact]
    public void AnAnswer_IsReadToItsMost()
    {
        const string Keys = """{ "kind": "json-api", "url": "https://a.test/?q={query}", "items": "$.items", "title": "{@.t}", "link": "{@.u}" }""";
        var many = Read(Keys, JsonSerializer.Serialize(new { items = Enumerable.Range(0, 150).Select(at => new { t = new string('T', 1000), u = $"https://a.test/{at}" }) }));

        Assert.Equal(WebSearchAnswer.MostResults, many.Results.Count);
        Assert.All(many.Results, result => Assert.True(result.Title.Length <= WebSearchResult.MostTitle));

        var rooted = Read(
            """{ "kind": "json-api", "url": "https://a.test/?q={query}", "items": "$.items", "title": "{@.t}", "link": "{$.base}{@.p}" }""",
            JsonSerializer.Serialize(new { @base = "https://a.test/" + new string('x', 50_000), items = new[] { new { t = "T", p = "/1" } } }));

        Assert.Equal(WebSearchOutcome.Changed, rooted.Outcome);
        Assert.StartsWith("none of its 1 results gave a title", rooted.Why, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { @base = new string('x', 50_000) }));
        Assert.Equal(WebPageText.MostFieldRead, WebItemTemplate.Parse("{$.base}{$.base}").Fill(document.RootElement, document.RootElement)!.Length);
    }

    /// <summary>discover takes the first text its path finds that says something.</summary>
    [Fact]
    public void Discover_TakesTheFirstTextThatSaysSomething()
    {
        var source = Source("s", """{ "kind": "json-api", "url": "{s}?q={query}", "items": "$.i", "title": "{@.t}", "link": "{@.u}", "discover": { "s": { "url": "https://a.test/index.json", "path": "$.a[*]" } } }""");

        Assert.Empty(WebSearchKinds.OfThisBuild.Problems(source));
        Assert.Equal("https://x.test/q", Kind.Found(source, [Answer("""{ "a": ["", " ", "https://x.test/q"] }""")])["s"]);
    }

    /// <summary>A body that is no JSON, an items path that finds nothing or no array, and items none of which read are each a change - never no results.</summary>
    [Theory]
    [InlineData("<html><body>Maintenance</body></html>", "$.items", "{@.title}", "its answer is no JSON")]
    [InlineData("{\"results\":[]}", "$.items", "{@.title}", "'$.items' finds nothing in its answer, where it finds the array of results")]
    [InlineData("{\"items\":{\"a\":1}}", "$.items", "{@.title}", "'$.items' finds object in its answer, where it finds the array of results")]
    [InlineData("{\"items\":[{\"name\":\"a\"},{\"name\":\"b\"}]}", "$.items", "{@.title}", "none of its 2 results gave a title by '{@.title}' and an address by 'https://example.test/{@.id}'")]
    public void AnAnswerThatCannotBeRead_IsAChange(string body, string items, string title, string why)
    {
        var keys = $$"""{ "kind": "json-api", "url": "https://api.example.test/?q={query}", "items": "{{items}}", "title": "{{title}}", "link": "https://example.test/{@.id}" }""";
        var answer = Read(keys, body);

        Assert.Equal(WebSearchOutcome.Changed, answer.Outcome);
        Assert.StartsWith(why, answer.Why, StringComparison.Ordinal);
        Assert.Empty(answer.Results);
    }

    /// <summary>An item with no title or no address is left out and counted, the others read.</summary>
    [Fact]
    public void AnItemWithNoAddress_IsLeftOutAndCounted()
    {
        var answer = Read(
            """{ "kind": "json-api", "url": "https://api.example.test/?q={query}", "items": "$.items", "title": "{@.title}", "link": "{@.url}" }""",
            """{ "items": [ { "title": "One", "url": "https://one.example.test/" }, { "title": "Two" }, { "url": "https://three.example.test/" }, { "title": "Four", "url": "mailto:someone@example.test" } ] }""");

        Assert.Equal(WebSearchOutcome.Ok, answer.Outcome);
        Assert.Equal("One", Assert.Single(answer.Results).Title);
        Assert.Equal(3, answer.LeftOut);
    }

    /// <summary>A snippet is cut to its most, at a blank, marked where it was cut.</summary>
    [Fact]
    public void ALongSnippet_IsCut()
    {
        var words = string.Join(' ', Enumerable.Repeat("word", 200));
        var answer = Read(
            """{ "kind": "json-api", "url": "https://api.example.test/?q={query}", "items": "$.items", "title": "{@.t}", "link": "{@.u}", "snippet": "{@.s}" }""",
            JsonSerializer.Serialize(new { items = new[] { new { t = "T", u = "https://example.test/", s = words } } }));

        var snippet = Assert.Single(answer.Results).Snippet;

        Assert.True(snippet.Length <= WebSearchResult.MostSnippet, snippet);
        Assert.EndsWith("word…", snippet, StringComparison.Ordinal);
    }

    /// <summary>A GET fills its address - the query escaped, the limit a number - and asks for JSON, with the source's headers.</summary>
    [Fact]
    public void AGet_FillsItsAddress_AndSendsItsHeaders()
    {
        var request = Assert.IsType<WebSearchRequest.Http>(Kind.Request(Source("gh", GitHub), WebSearchFound.None, "c# & linq/x", 5));

        Assert.Equal("GET", request.Method);
        Assert.Equal("https://api.github.com/search/repositories?q=c%23%20%26%20linq%2Fx&per_page=5", request.Address);
        Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
        Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
        Assert.Null(request.Body);
    }

    /// <summary>A POST fills its body's texts - the query as it is, a text that is the limit alone a number - and sends JSON.</summary>
    [Fact]
    public void APost_FillsItsBody()
    {
        var source = Source("post", """
            { "kind": "json-api", "url": "https://api.example.test/search", "method": "POST",
              "body": { "q": "{query}", "size": "{limit}", "label": "top {limit}", "filters": ["{query}", 3, true, null] },
              "items": "$.hits", "title": "{@.t}", "link": "{@.u}" }
            """);

        Assert.Empty(WebSearchKinds.OfThisBuild.Problems(source));

        var request = Assert.IsType<WebSearchRequest.Http>(Kind.Request(source, WebSearchFound.None, "say \"hi\"", 4));

        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.example.test/search", request.Address);
        using var body = JsonDocument.Parse(request.Body!);
        var sent = body.RootElement;

        Assert.Equal("say \"hi\"", sent.GetProperty("q").GetString());
        Assert.Equal(JsonValueKind.Number, sent.GetProperty("size").ValueKind);
        Assert.Equal(4, sent.GetProperty("size").GetInt32());
        Assert.Equal("top 4", sent.GetProperty("label").GetString());
        var filters = sent.GetProperty("filters");

        Assert.Equal("say \"hi\"", filters[0].GetString());
        Assert.Equal((3, JsonValueKind.True, JsonValueKind.Null), (filters[1].GetInt32(), filters[2].ValueKind, filters[3].ValueKind));
        Assert.Equal(("application/json", "application/json"), (request.Headers["Accept"], request.Headers["Content-Type"]));
        Assert.False(Assert.IsType<WebSearchRequest.Http>(Kind.Request(Source("so", StackOverflow), WebSearchFound.None, "x", 1)).Headers.ContainsKey("Content-Type"));
    }

    /// <summary>discover reads its document once a run: the address found fills the template, the first of several taken.</summary>
    [Fact]
    public void Discover_FindsTheAddress_AndTheRequestTakesIt()
    {
        var source = Source("nuget", NuGet);

        Assert.Empty(WebSearchKinds.OfThisBuild.Problems(source));

        var lookup = Assert.Single(Kind.Lookups(source));
        Assert.Equal(("GET", "https://example-packages.test/v3/index.json", "application/json"), (lookup.Method, lookup.Address, lookup.Headers["Accept"]));

        var found = Kind.Found(source, [Answer(Stored("nuget-index.json"), address: lookup.Address)]);

        Assert.Null(found.Failure);
        Assert.Null(found.Until);

        // Its keys are read in any case, as the configuration's are.
        var shouted = Source("nuget", NuGet.Replace("\"url\": \"https://example-packages.test/v3/index.json\"", "\"URL\": \"https://example-packages.test/v3/index.json\"", StringComparison.Ordinal).Replace("\"path\":", "\"Path\":", StringComparison.Ordinal));
        Assert.Empty(WebSearchKinds.OfThisBuild.Problems(shouted));
        Assert.Equal(lookup.Address, Assert.Single(Kind.Lookups(shouted)).Address);
        Assert.Equal("https://search-primary.example-packages.test/query", found["search"]);

        // A lookup whose answer says nothing remains still finds, and holds the source until its reset.
        var spent = Kind.Found(source, [Answer(Stored("nuget-index.json"), headers: Headers(("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", "45")), address: lookup.Address)]);
        Assert.Equal(("https://search-primary.example-packages.test/query", At + TimeSpan.FromSeconds(45)), (spent["search"], spent.Until));

        var request = Assert.IsType<WebSearchRequest.Http>(Kind.Request(source, found, "json reader", 5));
        Assert.Equal("https://search-primary.example-packages.test/query?q=json%20reader&take=5", request.Address);

        var answer = Kind.Read(source, found, Answer(Stored("nuget-search.json")));
        Assert.Equal(["Example.Json", "Example.Json.Extensions"], answer.Results.Select(result => result.Title));
        Assert.Equal("https://www.nuget.org/packages/Example.Json", answer.Results[0].Link);
        Assert.Equal(2, answer.Total);
    }

    /// <summary>A document discover cannot read is the source's answer to every query of the run: a failure, a change, a limit, each said with what was looked up.</summary>
    [Theory]
    [InlineData("{}", 404, WebSearchOutcome.Failed, "its 'search', looked up at 'https://example-packages.test/v3/index.json': it answered 404")]
    [InlineData("not json", 200, WebSearchOutcome.Changed, "its 'search', looked up at 'https://example-packages.test/v3/index.json': it is no JSON")]
    [InlineData("{\"resources\":[]}", 200, WebSearchOutcome.Changed, "its 'search', looked up at 'https://example-packages.test/v3/index.json': '$.resources[?@['@type']=='SearchQueryService']['@id']' finds no text in it")]
    [InlineData("{}", 429, WebSearchOutcome.RateLimited, "its 'search', looked up at 'https://example-packages.test/v3/index.json': it answered 429")]
    public void ADocumentDiscoverCannotRead_IsTheSourcesAnswer(string body, int status, WebSearchOutcome outcome, string why)
    {
        var found = Kind.Found(Source("nuget", NuGet), [Answer(body, status)]);

        Assert.Equal(outcome, found.Failure!.Outcome);
        Assert.StartsWith(why, found.Failure.Why, StringComparison.Ordinal);

        // A limit holds the source as the found values' pause: a minute, where it said not how long.
        Assert.Equal(outcome == WebSearchOutcome.RateLimited ? At + TimeSpan.FromMinutes(1) : null, found.Until);
    }

    /// <summary>Each key's value is held, each problem naming its key.</summary>
    [Theory]
    [InlineData("""{ "url": "https://a.test/?q=x" }""", "neither 'url' nor 'body' names {query}, so no query would reach the source")]
    [InlineData("""{ "url": "https://a.test/?q={query}&x={other}" }""", "'url' names '{other}', which nothing fills: {query}, {limit}")]
    [InlineData("""{ "url": "https://a.test/?q={query" }""", "'url' is no template: the placeholder at column 19 of line 1 is never closed")]
    [InlineData("""{ "url": "ftp://a.test/?q={query}" }""", "'url' is an http or https address once filled, and 'ftp://a.test/?q={query}' is not")]
    [InlineData("""{ "url": 5 }""", "'url' is a text, and is 5")]
    [InlineData("""{ "method": "PUT" }""", "'method' is GET or POST, and is 'PUT'")]
    [InlineData("""{ "method": "POST" }""", "'body' is missing: a POST sends one")]
    [InlineData("""{ "body": { "q": "{query}" } }""", "'body' is sent with a POST alone, and 'method' is GET")]
    [InlineData("""{ "headers": { "User-Agent": "me" } }""", "'headers' names User-Agent, which a source never sends of its own: the request itself says it")]
    [InlineData("""{ "headers": { "content-type": "text/plain" } }""", "'headers' names Content-Type, which a source never sends of its own: the request itself says it")]
    [InlineData("""{ "headers": { "authorization": "Bearer x" } }""", "'headers' names Authorization, which carries a key: a key is sent as the source's credential, kept secret")]
    [InlineData("""{ "headers": { "Bad Name": "x" } }""", "'headers' names 'Bad Name', which is no header's name")]
    [InlineData("""{ "headers": { "a{b": "x" } }""", "'headers' names 'a{b', which is no header's name")]
    [InlineData("""{ "headers": { "Proxy-Authorization": "x" } }""", "'headers' names Proxy-Authorization, which carries a key")]
    [InlineData("""{ "backoff": "$..b" }""", "'backoff' may find several values, and finds one")]
    [InlineData("""{ "discover": { "9s": { "url": "https://a.test/", "path": "$.a" } } }""", "'discover.9s' is no name a template fills")]
    [InlineData("""{ "discover": { "s": { "url": "ftp://a.test/", "path": "$.a" } } }""", "'discover.s.url' is the http or https address of the document it reads")]
    [InlineData("""{ "headers": { "X-Api": "1", "x-api": "2" } }""", "'headers.X-Api' is given twice, as 'headers.X-Api' and 'headers.x-api'")]
    [InlineData("""{ "url": "https://a.test/?q={query}&s={s}", "discover": { "s": { "url": "https://a.test/", "URL": "https://b.test/", "path": "$.a" } } }""", "'discover.s.url' is given twice")]
    [InlineData("""{ "headers": ["x"] }""", "'headers' is an object of texts, each a header's value by its name")]
    [InlineData("""{ "items": "$.items[" }""", "'items' is no JSONPath query of the answer: ")]
    [InlineData("""{ "items": "$.items[*]" }""", "'items' may find several values, and finds one: names and indexes alone, as $.results")]
    [InlineData("""{ "title": "{@.title" }""", "'title' is no template: the placeholder at column 1 of line 1 is never closed")]
    [InlineData("""{ "title": "Fixed" }""", "'title' is no template: it reads no value of the item, and a template reads at least one, as {@.title}")]
    [InlineData("""{ "title": "{@.a[}" }""", "'title' is no template: '{@.a[}' is no JSONPath query of the item ('@') or the answer ('$'): ")]
    [InlineData("""{ "link": [] }""", "'link' is a template, or a list of one or more")]
    [InlineData("""{ "link": ["{@.u}", "x"] }""", "'link' is no template: it reads no value of the item")]
    [InlineData("""{ "total": "$..count" }""", "'total' may find several values, and finds one")]
    [InlineData("""{ "backoff": "nope" }""", "'backoff' is no JSONPath query of the answer: ")]
    [InlineData("""{ "discover": { "query": { "url": "https://a.test/", "path": "$.a" } } }""", "'discover.query' is no name a template fills")]
    [InlineData("""{ "discover": { "s": { "url": "https://a.test/", "path": "$.a" } } }""", "'discover' reads 's', which neither 'url' nor 'body' names")]
    [InlineData("""{ "discover": { "s": { "path": "$.a" } } }""", "'discover.s.url' is the http or https address of the document it reads, written whole")]
    [InlineData("""{ "discover": { "s": { "url": "https://a.test/", "path": "$[" } } }""", "'discover.s.path' is no JSONPath query of the document: ")]
    [InlineData("""{ "discover": { "s": { "url": "https://a.test/", "path": "$.a", "x": 1 } } }""", "'discover.s' takes url and path, and not 'x'")]
    [InlineData("""{ "discover": [] }""", "'discover' is an object: each name it reads, with that name's url and path")]
    public void EachKeysValue_IsHeld(string overrides, string problem)
    {
        var keys = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{ "kind": "json-api", "url": "https://a.test/?q={query}", "items": "$.items", "title": "{@.t}", "link": "{@.u}" }""")!;

        foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(overrides)!)
        {
            keys[key] = value;
        }

        var problems = Kind.Problems(Source("source", JsonSerializer.Serialize(keys)));

        Assert.Contains(problems, said => said.StartsWith(problem, StringComparison.Ordinal));
    }

    /// <summary>
    /// A name discover reads twice is refused, never raised: either value could fill its placeholder. Names are matched
    /// exactly, as a placeholder's are, so two that differ in case are two names.
    /// </summary>
    [Fact]
    public void ADiscoverNameGivenTwice_IsRefused()
    {
        const string Twice = """
            { "kind": "json-api", "url": "https://a.test/?q={query}&s={s}", "items": "$.items", "title": "{@.t}", "link": "{@.u}",
              "discover": { "s": { "url": "https://a.test/", "path": "$.a" }, "s": { "url": "https://b.test/", "path": "$.b" } } }
            """;

        Assert.Equal(["'discover.s' is given twice, and nothing could say which value was meant"], WebSearchKinds.OfThisBuild.Problems(Source("source", Twice)));

        const string Cased = """
            { "kind": "json-api", "url": "https://a.test/?q={query}&s={s}&t={S}", "items": "$.items", "title": "{@.t}", "link": "{@.u}",
              "discover": { "s": { "url": "https://a.test/", "path": "$.a" }, "S": { "url": "https://b.test/", "path": "$.b" } } }
            """;

        Assert.Empty(WebSearchKinds.OfThisBuild.Problems(Source("source", Cased)));
    }

    /// <summary>A source whose keys all hold has no problem.</summary>
    [Theory]
    [InlineData(StackOverflow)]
    [InlineData(GitHub)]
    [InlineData(NuGet)]
    public void AWholeSource_HasNoProblem(string keys) => Assert.Empty(WebSearchKinds.OfThisBuild.Problems(Source("source", keys)));

    /// <summary>
    /// A field is text as it stands - plain text that names an element kept as written - unless the source says it is
    /// markup, field by field; one that says it in any other shape is refused.
    /// </summary>
    [Fact]
    public void AField_IsMarkupWhereTheSourceSaysItIs_AndNowhereElse()
    {
        const string Body = """{ "items": [ { "name": "charts <canvas> kit", "about": "<b>Simple</b> charts &amp; more", "url": "https://a.test/1" } ] }""";
        const string Keys = """{ "kind": "json-api", "url": "https://a.test/?q={query}", "items": "$.items", "title": "{@.name}", "link": "{@.url}", "snippet": "{@.about}" """;

        Assert.Equal(new WebSearchResult("charts <canvas> kit", "https://a.test/1", "<b>Simple</b> charts &amp; more"), Assert.Single(Read(Keys + "}", Body).Results));
        Assert.Equal(new WebSearchResult("charts <canvas> kit", "https://a.test/1", "Simple charts & more"), Assert.Single(Read(Keys + """, "markup": ["snippet"] }""", Body).Results));
        Assert.Equal(new WebSearchResult("charts", "https://a.test/1", "<b>Simple</b> charts &amp; more"), Assert.Single(Read(Keys + """, "markup": ["title"] }""", Body).Results));

        foreach (var shape in new[] { "true", "\"snippet\"", "[\"body\"]", "[1]" })
        {
            Assert.Contains($"'markup' is a list of the fields that are HTML, of title and snippet, and is {shape}", WebSearchKinds.OfThisBuild.Problems(Source("source", Keys + $", \"markup\": {shape} }}")));
        }
    }

    /// <summary>A failure's body is read as its type says: an error page of HTML to its text, any other body - JSON among them - as it stands.</summary>
    [Fact]
    public void AFailuresBody_IsReadAsItsTypeSays()
    {
        var page = Kind.Read(Source("source", GitHub), WebSearchFound.None, Answer("<html><body><h1>Down</h1><p>for &amp; maintenance</p></body></html>", 503, "text/html"));
        var json = Kind.Read(Source("source", GitHub), WebSearchFound.None, Answer("""{"message":"<script> is refused &amp; logged"}""", 400));

        Assert.Equal("it answered 503: Down for & maintenance", page.Why);
        Assert.Equal("""it answered 400: {"message":"<script> is refused &amp; logged"}""", json.Why);
    }

    /// <summary>The kind is answered by its endpoint alone: an index's answer is a defect of the caller's.</summary>
    [Fact]
    public void AnIndexsAnswer_IsRefused()
        => Assert.Throws<ArgumentException>(() => Kind.Read(Source("so", StackOverflow), WebSearchFound.None, new WebSearchResponse.Indexed(new("https://a.test", [], 0, null, false, null, null))));

    private static WebSearchAnswer Read(string keys, string body) => Kind.Read(Source("source", keys), WebSearchFound.None, Answer(body));
}
