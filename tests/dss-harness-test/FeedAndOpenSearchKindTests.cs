using DssHarness.Core.Web.Search;
using DssHarness.Core.Web.Search.Kinds;
using static DssHarness.Tests.WebSearchFixtures;

namespace DssHarness.Tests;

/// <summary>
/// <c>feed</c> and <c>opensearch</c> over stored answers: RSS 2.0, RSS 1.0 and Atom read into results, an OpenSearch
/// description's template taken where it answers a feed or JSON and refused where it offers only a results page.
/// </summary>
public sealed class FeedAndOpenSearchKindTests
{
    private const string Feed = """{ "kind": "feed", "url": "https://feeds.example.test/search?q={query}&n={limit}" }""";

    private const string OpenSearch = """{ "kind": "opensearch", "url": "https://search.example.test/opensearch.xml" }""";

    private const string OpenSearchJson = """{ "kind": "opensearch", "url": "https://json.example.test/opensearch.xml", "items": "$.hits", "title": "{@.name}", "link": "{@.href}" }""";

    private static readonly FeedKind FeedReader = new();

    private static readonly OpenSearchKind Described = new();

    /// <summary>RSS 2.0: each item's title and link, a guid that is an address for a missing link, a relative link read against the feed, markup reduced, the total read.</summary>
    [Fact]
    public void Rss2_IsRead()
    {
        var answer = ReadFeed(Stored("rss2.xml"));

        Assert.Equal(WebSearchOutcome.Ok, answer.Outcome);
        Assert.Equal(
            [
                new WebSearchResult("Writing a recursive descent parser", "https://feeds.example.test/posts/parser", "How to write one & test it."),
                new WebSearchResult("Parser combinators", "https://feeds.example.test/posts/combinators", "Combinators compose."),
                new WebSearchResult("Relative link", "https://feeds.example.test/posts/relative", string.Empty),
            ],
            answer.Results);
        Assert.Equal(1, answer.LeftOut);
        Assert.Equal(87, answer.Total);
    }

    /// <summary>Atom: the alternate link before another, an html title reduced, an xhtml title's text, a summary else the content.</summary>
    [Fact]
    public void Atom_IsRead()
    {
        var answer = ReadFeed(Stored("atom.xml"));

        Assert.Equal(
            [
                new WebSearchResult("Tokens & lexers", "https://atom.example.test/entries/1", "Splitting text into tokens."),
                new WebSearchResult("Grammars explained", "https://atom.example.test/entries/2", "What a grammar is."),
            ],
            answer.Results);
        Assert.Equal(2, answer.Total);
    }

    /// <summary>RSS 1.0 is read too.</summary>
    [Fact]
    public void Rss1_IsRead()
        => Assert.Equal([new WebSearchResult("First item", "https://rdf.example.test/one", "The first.")], ReadFeed(Stored("rss1.xml")).Results);

    /// <summary>A feed with no item is no results.</summary>
    [Fact]
    public void AFeedWithNoItem_IsNoResults() => Assert.Equal(WebSearchOutcome.NoResults, ReadFeed(Stored("empty-feed.xml")).Outcome);

    /// <summary>An answer that is no XML, or no feed, or whose every item names no address is a change.</summary>
    [Theory]
    [InlineData("{\"results\":[]}", "its answer is no XML: ")]
    [InlineData("<html><body>Search</body></html>", "its answer is no RSS or Atom feed: its root is <html>")]
    [InlineData("<rss version=\"2.0\"><channel><item><title>A</title></item><item><link>https://a.test/</link></item></channel></rss>", "none of its 2 items gave a title and an address")]
    public void AnAnswerThatIsNoFeed_IsAChange(string body, string why)
    {
        var answer = ReadFeed(body);

        Assert.Equal(WebSearchOutcome.Changed, answer.Outcome);
        Assert.StartsWith(why, answer.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document type is never acted on: an entity it declares - one that names a file, or one that holds text - is not
    /// expanded, and what it names is never read.
    /// </summary>
    [Theory]
    [InlineData("<!ENTITY secret SYSTEM \"file:///etc/passwd\">")]
    [InlineData("<!ENTITY secret \"expanded\">")]
    public void ADocumentType_IsNeverActedOn(string entity)
    {
        var answer = ReadFeed($"""
            <?xml version="1.0"?>
            <!DOCTYPE rss [ {entity} ]>
            <rss version="2.0"><channel><item><title>&secret;</title><link>https://a.test/</link></item></channel></rss>
            """);

        Assert.Equal(WebSearchOutcome.Changed, answer.Outcome);
        Assert.Contains("secret", answer.Why, StringComparison.Ordinal);
        Assert.DoesNotContain("expanded", answer.Why, StringComparison.Ordinal);
    }

    /// <summary>XML nested deeper than any feed is is no feed, read no further.</summary>
    [Fact]
    public void XmlNestedPastItsDepth_IsNoFeed()
    {
        var deep = string.Concat(Enumerable.Repeat("<a>", 200)) + string.Concat(Enumerable.Repeat("</a>", 200));
        var answer = ReadFeed($"""<rss version="2.0"><channel><item><title>{deep}</title><link>https://a.test/</link></item></channel></rss>""");

        Assert.Equal(WebSearchOutcome.Changed, answer.Outcome);
        Assert.Contains("nest deeper than 64", answer.Why, StringComparison.Ordinal);
        Assert.Equal(WebSearchOutcome.Ok, ReadFeed($"""<rss version="2.0"><channel><item><title>{string.Concat(Enumerable.Repeat("<a>", 50))}T{string.Concat(Enumerable.Repeat("</a>", 50))}</title><link>https://a.test/</link></item></channel></rss>""").Outcome);
    }

    /// <summary>A good feed whose headers say nothing remains keeps its items, and holds the source until the reset.</summary>
    [Fact]
    public void AGoodFeedWithNothingRemaining_KeepsItsItems_AndAPause()
    {
        var answer = FeedReader.Read(Source("f", Feed), WebSearchFound.None, Answer(
            """<rss version="2.0"><channel><item><title>A</title><link>https://a.test/1</link></item></channel></rss>""",
            type: "application/rss+xml",
            headers: Headers(("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", "45"))));

        Assert.Equal((WebSearchOutcome.Ok, (DateTimeOffset?)(At + TimeSpan.FromSeconds(45))), (answer.Outcome, answer.Until));
    }

    /// <summary>
    /// An Atom text construct and an entry's content are read as their type says: text - the type where none is said -
    /// and another text/ type as they stand, never markup; html and text/html as markup; xhtml and an XML type as their
    /// markup, a block apart from the next; another media type as none.
    /// </summary>
    [Fact]
    public void AnAtomText_IsReadAsItsTypeSays()
    {
        var answer = ReadFeed("""
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><title type="text">Why does a &lt;div&gt; collapse?</title><link href="https://a.test/1"/><summary>Use &amp;lt; in HTML</summary></entry>
              <entry><title>&lt;script&gt; tag runs twice</title><link href="https://a.test/2"/></entry>
              <entry><title type="html">&lt;b&gt;Bold&lt;/b&gt; move</title><link href="https://a.test/3"/><summary type="HTML">One &amp;amp; two</summary></entry>
              <entry><title>T4</title><link href="https://a.test/4"/><content type="Text/HTML; charset=utf-8">&lt;p&gt;Hello &lt;b&gt;world&lt;/b&gt;&lt;/p&gt;</content></entry>
              <entry><title>T5</title><link href="https://a.test/5"/><summary type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>One</p><p>Two &lt; 3</p></div></summary></entry>
              <entry><title>T6</title><link href="https://a.test/6"/><content type="application/xhtml+xml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Six</p><p>Seven</p></div></content></entry>
              <entry><title>T7</title><link href="https://a.test/7"/><content type="text/plain">a &lt;b&gt; c</content></entry>
              <entry><title>T8</title><link href="https://a.test/8"/><content type="image/png">iVBORw0KGgo=</content></entry>
              <entry><title>T9</title><link href="https://a.test/9"/><summary type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><b>sea</b><b>rch</b><p><![CDATA[a <b> c]]></p></div></summary></entry>
            </feed>
            """);

        Assert.Equal(
            [
                new WebSearchResult("Why does a <div> collapse?", "https://a.test/1", "Use &lt; in HTML"),
                new WebSearchResult("<script> tag runs twice", "https://a.test/2", string.Empty),
                new WebSearchResult("Bold move", "https://a.test/3", "One & two"),
                new WebSearchResult("T4", "https://a.test/4", "Hello world"),
                new WebSearchResult("T5", "https://a.test/5", "One Two < 3"),
                new WebSearchResult("T6", "https://a.test/6", "Six Seven"),
                new WebSearchResult("T7", "https://a.test/7", "a <b> c"),
                new WebSearchResult("T8", "https://a.test/8", string.Empty),
                new WebSearchResult("T9", "https://a.test/9", "search a <b> c"),
            ],
            answer.Results);
    }

    /// <summary>An RSS item's title and description are markup, in RSS 2.0 and RSS 1.0 alike, each reduced to its text.</summary>
    [Fact]
    public void AnRssItemsFields_AreMarkup()
    {
        var two = ReadFeed("""<rss version="2.0"><channel><item><title>Tom &amp;amp; Jerry</title><link>https://a.test/1</link><description>&lt;p&gt;One &lt;b&gt;bold&lt;/b&gt; word&lt;o:p&gt;&lt;/o:p&gt;&lt;/p&gt;</description></item></channel></rss>""");
        var one = ReadFeed("""
            <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns="http://purl.org/rss/1.0/">
              <item rdf:about="https://a.test/2"><title>A &amp;amp; B</title><link>https://a.test/2</link><description>&lt;i&gt;c&lt;/i&gt;</description></item>
            </rdf:RDF>
            """);

        Assert.Equal([new WebSearchResult("Tom & Jerry", "https://a.test/1", "One bold word")], two.Results);
        Assert.Equal([new WebSearchResult("A & B", "https://a.test/2", "c")], one.Results);
    }

    /// <summary>A feed is read to its most items.</summary>
    [Fact]
    public void AFeed_IsReadToItsMostItems()
        => Assert.Equal(WebSearchAnswer.MostResults, ReadFeed($"""<rss version="2.0"><channel>{string.Concat(Enumerable.Range(0, 150).Select(at => $"<item><title>T{at}</title><link>https://a.test/{at}</link></item>"))}</channel></rss>""").Results.Count);

    /// <summary>An item's guid is its address unless it says it is not one.</summary>
    [Fact]
    public void AGuid_IsAnAddressUnlessItSaysNot()
    {
        var answer = ReadFeed("""<rss version="2.0"><channel><item><title>A</title><guid isPermaLink="false">https://a.test/1</guid></item><item><title>B</title><guid>https://a.test/2</guid></item></channel></rss>""");

        Assert.Equal(["https://a.test/2"], answer.Results.Select(result => result.Link));
        Assert.Equal(1, answer.LeftOut);
    }

    /// <summary>A feed's status is read first: a limit, a failure.</summary>
    [Fact]
    public void AFeedsStatus_IsReadFirst()
    {
        Assert.Equal(WebSearchOutcome.RateLimited, FeedReader.Read(Source("f", Feed), WebSearchFound.None, Answer(string.Empty, 429, "text/plain")).Outcome);
        Assert.Equal(WebSearchOutcome.Failed, FeedReader.Read(Source("f", Feed), WebSearchFound.None, Answer("gone", 410, "text/plain")).Outcome);
    }

    /// <summary>A feed's request fills its address and asks for a feed.</summary>
    [Fact]
    public void AFeedsRequest_FillsItsAddress()
    {
        var request = Assert.IsType<WebSearchRequest.Http>(FeedReader.Request(Source("f", Feed), WebSearchFound.None, "a b", 3));

        Assert.Equal("https://feeds.example.test/search?q=a%20b&n=3", request.Address);
        Assert.StartsWith("application/atom+xml", request.Headers["Accept"], StringComparison.Ordinal);
    }

    /// <summary>A feed's url is held: a template naming the query, an http or https address once filled.</summary>
    [Theory]
    [InlineData("""{ "kind": "feed", "url": "https://a.test/feed" }""", "'url' never names {query}, so no query would reach the source")]
    [InlineData("""{ "kind": "feed", "url": "https://a.test/feed?q={query}&s={site}" }""", "'url' names '{site}', which nothing fills: {query}, {limit}")]
    [InlineData("""{ "kind": "feed", "url": "file:///feed?q={query}" }""", "'url' is an http or https address once filled")]
    [InlineData("""{ "kind": "feed", "url": ["x"] }""", "'url' is a text")]
    [InlineData("""{ "kind": "feed" }""", "'url' is missing: a source of kind 'feed' gives it")]
    [InlineData("""{ "kind": "feed", "url": "https://a.test/?q={query}", "items": "$.x" }""", "'items' is no key a source of kind 'feed' takes")]
    public void AFeedsUrl_IsHeld(string keys, string problem)
        => Assert.Contains(WebSearchKinds.OfThisBuild.Problems(Source("f", keys)), said => said.StartsWith(problem, StringComparison.Ordinal));

    /// <summary>A description's Atom template is taken before its RSS one, its parameters filled: the query, the count, the offset it names, what it may leave out left out.</summary>
    [Fact]
    public void ADescription_GivesItsAtomTemplate_Filled()
    {
        var source = Source("os", OpenSearch);

        Assert.Empty(WebSearchKinds.OfThisBuild.Problems(source));

        var lookup = Assert.Single(Described.Lookups(source));
        Assert.Equal("https://search.example.test/opensearch.xml", lookup.Address);
        Assert.StartsWith("application/opensearchdescription+xml", lookup.Headers["Accept"], StringComparison.Ordinal);

        var found = Described.Found(source, [Answer(Stored("opensearch-feeds.xml"), type: "application/opensearchdescription+xml")]);

        Assert.Null(found.Failure);
        Assert.Null(found.Until);
        Assert.Equal("application/atom+xml", found["type"]);

        var spent = Described.Found(source, [Answer(Stored("opensearch-feeds.xml"), type: "application/opensearchdescription+xml", headers: Headers(("RateLimit", "\"default\";r=0;t=30")))]);
        Assert.Equal(At + TimeSpan.FromSeconds(30), spent.Until);

        var request = Assert.IsType<WebSearchRequest.Http>(Described.Request(source, found, "lexer & parser", 7));

        Assert.Equal("https://search.example.test/atom?q=lexer%20%26%20parser&n=7&from=0&lang=*&x=", request.Address);
        Assert.Equal("application/atom+xml", request.Headers["Accept"]);

        Assert.Equal(2, Described.Read(source, found, Answer(Stored("atom.xml"), type: "application/atom+xml")).Results.Count);
    }

    /// <summary>A description that offers only a results page for people is refused, never read.</summary>
    [Fact]
    public void ADescriptionOfAResultsPageAlone_IsRefused()
    {
        var found = Described.Found(Source("os", OpenSearch), [Answer(Stored("opensearch-html-only.xml"))]);

        Assert.Equal(WebSearchOutcome.Refused, found.Failure!.Outcome);
        Assert.Equal("its description, 'https://search.example.test/opensearch.xml': it offers its results as text/html alone: a results page for people, which this tool never reads", found.Failure.Why);
    }

    /// <summary>A JSON template is taken where the source says how to read it - a feed's that requires a parameter this tool does not know is passed over - and read by its paths.</summary>
    [Fact]
    public void AJsonTemplate_IsTakenWhereTheSourceReadsIt()
    {
        var source = Source("os", OpenSearchJson);

        Assert.Empty(WebSearchKinds.OfThisBuild.Problems(source));

        var found = Described.Found(source, [Answer(Stored("opensearch-json.xml"))]);

        Assert.Equal("application/json", found["type"]);
        Assert.Equal("https://json.example.test/api?q=x&limit=2", Assert.IsType<WebSearchRequest.Http>(Described.Request(source, found, "x", 2)).Address);

        var answer = Described.Read(source, found, Answer("""{ "hits": [ { "name": "One", "href": "https://json.example.test/1" } ] }"""));
        Assert.Equal("One", Assert.Single(answer.Results).Title);
    }

    /// <summary>A JSON template the source gives no paths to read is a failure the source is told of; a description with no template, or none it can fill, a change.</summary>
    [Theory]
    [InlineData("opensearch-json.xml", WebSearchOutcome.Failed, "it offers its results as JSON, and the source gives no items, title and link to read them by")]
    [InlineData(null, WebSearchOutcome.Changed, "it names no template for results that this tool can fill")]
    public void ADescriptionWithNothingToTake_IsToldWhy(string? stored, WebSearchOutcome outcome, string why)
    {
        var body = stored is null
            ? """<OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/"><Url type="application/rss+xml" template="https://a.test/?x={other}"/></OpenSearchDescription>"""
            : Stored(stored);

        var found = Described.Found(Source("os", OpenSearch), [Answer(body)]);

        Assert.Equal(outcome, found.Failure!.Outcome);
        Assert.EndsWith(why, found.Failure.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a description says is the site's to say, and is held before it is used: a type that is no media type, a
    /// template that is no web address once filled, and an offset that is no number are passed over.
    /// </summary>
    [Fact]
    public void WhatADescriptionSays_IsHeldBeforeItIsUsed()
    {
        var found = Described.Found(Source("os", OpenSearch), [Answer("""
            <OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/">
              <Url type="application/atom+xml" template="javascript:alert('{searchTerms}')"/>
              <Url type="application/rss bad+xml" template="https://a.test/bad?q={searchTerms}"/>
              <Url type="application/rss+xml" indexOffset="x&amp;y=1" template="https://a.test/rss?q={searchTerms}&amp;from={startIndex}"/>
            </OpenSearchDescription>
            """)]);

        Assert.Null(found.Failure);
        Assert.Equal(("application/rss+xml", "1"), (found["type"], found["indexOffset"]));
        Assert.Equal("https://a.test/rss?q=x&from=1", Assert.IsType<WebSearchRequest.Http>(Described.Request(Source("os", OpenSearch), found, "x", 3)).Address);
    }

    /// <summary>
    /// A template is taken where it is one: a feed's before JSON's, never one that asks no query, never a suggestions
    /// service's, never one whose type is no media type.
    /// </summary>
    [Theory]
    [InlineData("""<Url type="application/atom+xml" template="https://a.test/atom?q={searchTerms}"/><Url type="application/json" template="https://a.test/json?q={searchTerms}"/>""", "application/atom+xml")]
    [InlineData("""<Url type="application/atom+xml" template="https://a.test/atom?q=fixed"/>""", null)]
    [InlineData("""<Url type="application/x-suggestions+json" template="https://a.test/s?q={searchTerms}"/>""", null)]
    [InlineData("""<Url type="application/a b+json" template="https://a.test/j?q={searchTerms}"/>""", null)]
    public void ATemplate_IsTakenWhereItIsOne(string urls, string? taken)
    {
        var found = Described.Found(Source("os", OpenSearchJson), [Answer($"""<OpenSearchDescription xmlns="http://a9.com/-/spec/opensearch/1.1/">{urls}</OpenSearchDescription>""")]);

        Assert.Equal(taken, found.Failure is null ? found["type"] : null);
    }

    /// <summary>A description that is no XML, or no description, is a change; one its site will not give, a failure.</summary>
    [Theory]
    [InlineData("not xml", 200, WebSearchOutcome.Changed, "it is no XML: ")]
    [InlineData("<html/>", 200, WebSearchOutcome.Changed, "it is no OpenSearch description: its root is <html>")]
    [InlineData("", 404, WebSearchOutcome.Failed, "it answered 404")]
    public void ADescriptionThatCannotBeRead_IsToldWhy(string body, int status, WebSearchOutcome outcome, string why)
    {
        var found = Described.Found(Source("os", OpenSearch), [Answer(body, status)]);

        Assert.Equal(outcome, found.Failure!.Outcome);
        Assert.Contains(why, found.Failure.Why, StringComparison.Ordinal);
    }

    /// <summary>An opensearch source's keys are held: a description's address, and items, title and link together.</summary>
    [Theory]
    [InlineData("""{ "kind": "opensearch", "url": "/opensearch.xml" }""", "'url' is an http or https address, written whole")]
    [InlineData("""{ "kind": "opensearch", "url": "https://a.test/os.xml", "items": "$.hits" }""", "'items' is given without the rest of items, title and link")]
    [InlineData("""{ "kind": "opensearch", "url": "https://a.test/os.xml", "items": "$.hits", "title": "{@.t}" }""", "'items', 'title' are given without the rest of items, title and link")]
    public void AnOpenSearchSourcesKeys_AreHeld(string keys, string problem)
        => Assert.Contains(WebSearchKinds.OfThisBuild.Problems(Source("os", keys)), said => said.StartsWith(problem, StringComparison.Ordinal));

    private static WebSearchAnswer ReadFeed(string body)
        => FeedReader.Read(Source("f", Feed), WebSearchFound.None, Answer(body, type: "application/rss+xml", address: "https://feeds.example.test/search?q=x"));
}
