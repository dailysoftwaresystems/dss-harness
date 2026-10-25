using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DssHarness.Core.Configuration;
using DssHarness.Core.Json;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// How a source's JSON answer is read into results, for every kind that reads one - <c>json-api</c>, and
/// <c>opensearch</c> where its template answers JSON: the array <c>items</c> finds, each item's <c>title</c>,
/// <c>link</c> and <c>snippet</c> by their templates, and <c>total</c>. One owner of the keys and of the reading.
/// </summary>
/// <param name="Items">The path to the array of results.</param>
/// <param name="Title">Each result's title.</param>
/// <param name="Links">Each result's address: the first of these whose paths all answer.</param>
/// <param name="Snippet">Each result's snippet; none where not said.</param>
/// <param name="Total">The path to how many results the source holds; none where not said.</param>
/// <param name="TitleMarkup">Whether each title is markup, reduced to its text; else text as it stands.</param>
/// <param name="SnippetMarkup">Whether each snippet is markup, reduced to its text; else text as it stands.</param>
public sealed record JsonSearchItems(JsonPath Items, WebItemTemplate Title, IReadOnlyList<WebItemTemplate> Links, WebItemTemplate? Snippet, JsonPath? Total, bool TitleMarkup, bool SnippetMarkup)
{
    /// <summary>The keys that say how an answer's results are read, each with what it is.</summary>
    /// <param name="required">Whether a source must give <c>items</c>, <c>title</c> and <c>link</c>.</param>
    public static IReadOnlyList<KeyDescription> Keys(bool required) =>
    [
        new("items", "a JSONPath query of the answer that finds the array of results, as $.results") { Required = required },
        new("title", "each result's title: a template of JSONPath queries of the item in braces, as {@.title}") { Required = required },
        new("link", "each result's address: a template as title's, or a list of them, the first whose queries all answer taken") { Required = required },
        new("snippet", "what each result says: a template as title's"),
        new("total", "a JSONPath query of the answer that finds how many results the source holds"),
        new("markup", "the fields that are HTML - an excerpt's highlights, its entities - each read as its text: a list of title, snippet or both; absent, both are text as they stand"),
    ];

    /// <summary>Why <paramref name="source"/>'s keys of an answer's results cannot be read; empty where they can, or none is given.</summary>
    /// <param name="source">The source.</param>
    public static IReadOnlyList<string> Problems(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();

        if (source.TextProblem("items") is { } itemsText)
        {
            problems.Add(itemsText);
        }
        else if (source.Text("items") is { } items && PathProblem(items, "items", singular: true) is { } itemsProblem)
        {
            problems.Add(itemsProblem);
        }

        foreach (var key in new[] { "title", "snippet" })
        {
            if (source.TextProblem(key) is { } text)
            {
                problems.Add(text);
            }
            else if (source.Text(key) is { } template && WebItemTemplate.Problem(template) is { } problem)
            {
                problems.Add($"'{key}' {problem}");
            }
        }

        if (source.Has("link"))
        {
            if (source.Texts("link") is not { Count: > 0 } links)
            {
                problems.Add("'link' is a template, or a list of one or more");
            }
            else
            {
                problems.AddRange(links.Select(WebItemTemplate.Problem).OfType<string>().Select(problem => $"'link' {problem}"));
            }
        }

        if (source.TextProblem("total") is { } totalText)
        {
            problems.Add(totalText);
        }
        else if (source.Text("total") is { } total && PathProblem(total, "total", singular: true) is { } totalProblem)
        {
            problems.Add(totalProblem);
        }

        if (source.Value("markup") is { } markup
            && (markup.ValueKind != JsonValueKind.Array || source.Texts("markup") is not { } fields || fields.Any(field => field is not ("title" or "snippet"))))
        {
            problems.Add($"'markup' is a list of the fields that are HTML, of title and snippet, and is {markup.GetRawText()}");
        }

        return problems;
    }

    /// <summary>Why <paramref name="path"/>, given as <paramref name="key"/>, is no JSONPath query of an answer - or not a singular one, where one must be.</summary>
    internal static string? PathProblem(string path, string key, bool singular)
    {
        if (JsonPath.Problem(path) is { } problem)
        {
            return $"'{key}' is no JSONPath query of the answer: {problem}";
        }

        return singular && !JsonPath.Parse(path).IsSingular
            ? $"'{key}' may find several values, and finds one: names and indexes alone, as $.results"
            : null;
    }

    /// <summary>The reading <paramref name="source"/>'s keys say; <see langword="null"/> where it gives no <c>items</c>.</summary>
    /// <param name="source">The source, its keys already held to <see cref="Problems"/>.</param>
    public static JsonSearchItems? Of(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Text("items") is not { } items || source.Text("title") is not { } title || source.Texts("link") is not { Count: > 0 } links
            ? null
            : new JsonSearchItems(
                JsonPath.Parse(items),
                WebItemTemplate.Parse(title),
                [.. links.Select(WebItemTemplate.Parse)],
                source.Text("snippet") is { } snippet ? WebItemTemplate.Parse(snippet) : null,
                source.Text("total") is { } total ? JsonPath.Parse(total) : null,
                source.Texts("markup")?.Contains("title") ?? false,
                source.Texts("markup")?.Contains("snippet") ?? false);
    }

    /// <summary>
    /// The results <paramref name="response"/> holds: a limit or a failure its status says, an answer that is no JSON or
    /// whose items are no array told as a change, an item with no title or address left out and counted - and every item
    /// left out told as a change, never as no results.
    /// </summary>
    /// <param name="response">The endpoint's response.</param>
    /// <param name="backoff">A path to a number of seconds the source asks to be left alone; none where not said.</param>
    public WebSearchAnswer Read(WebSearchResponse.Http response, JsonPath? backoff)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (WebSearchStatus.Before(response, out var pause) is { } early)
        {
            return early;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(response.Body);
        }
        catch (JsonException ex)
        {
            return WebSearchAnswer.Changed($"its answer is no JSON: {ex.Message}") with { Until = pause };
        }

        using (document)
        {
            var root = document.RootElement;
            var until = WebRateLimit.Later(pause, Backoff(backoff, root, response.At));
            var found = Items.Select(root);

            if (found is not [{ ValueKind: JsonValueKind.Array } array])
            {
                return WebSearchAnswer.Changed(found.Count == 0
                    ? $"'{Items}' finds nothing in its answer, where it finds the array of results"
                    : $"'{Items}' finds {found[0].ValueKind.ToString().ToLowerInvariant()} in its answer, where it finds the array of results") with { Until = until };
            }

            var results = new List<WebSearchResult>();
            var leftOut = 0;

            foreach (var item in array.EnumerateArray().Take(WebSearchAnswer.MostResults))
            {
                var title = Title.Fill(item, root);
                var snippet = Snippet?.Fill(item, root);

                // Markup where the source says a field is: a guess reads plain text that names an element as markup, and
                // markup that holds an element of its author's own as text.
                var result = WebSearchResult.Of(
                    TitleMarkup && title is not null ? WebPageText.OfMarkup(title) : title,
                    Links.Select(link => link.Fill(item, root)).FirstOrDefault(filled => filled is not null),
                    SnippetMarkup && snippet is not null ? WebPageText.OfMarkup(snippet) : snippet,
                    response.Address);

                if (result is null)
                {
                    leftOut++;
                }
                else
                {
                    results.Add(result);
                }
            }

            if (leftOut > 0 && results.Count == 0)
            {
                return WebSearchAnswer.Changed($"none of its {leftOut} results gave a title by '{Title.Text}' and an address by {string.Join(" or ", Links.Select(link => $"'{link.Text}'"))}") with { Until = until };
            }

            return WebSearchAnswer.Read(results) with { Total = Count(Total, root), Until = until, LeftOut = leftOut };
        }
    }

    /// <summary>How many results <paramref name="total"/> says the source holds: a whole number at it; <see langword="null"/> where it finds none.</summary>
    private static long? Count(JsonPath? total, JsonElement root)
        => total?.Select(root) is [{ ValueKind: JsonValueKind.Number } number] && number.TryGetInt64(out var count) && count >= 0 ? count : null;

    /// <summary>When a body's <paramref name="backoff"/> - a number of seconds - lets the source be asked again; <see langword="null"/> where it asks no pause.</summary>
    internal static DateTimeOffset? Backoff(JsonPath? backoff, JsonElement root, DateTimeOffset at)
        => backoff?.Select(root) is [{ ValueKind: JsonValueKind.Number } seconds] && seconds.TryGetDouble(out var wait) && wait > 0
            ? WebRateLimit.After(at, wait)
            : null;
}

/// <summary>
/// How a feed is read into results, for every kind that reads one - <c>feed</c>, and <c>opensearch</c> where its template
/// answers a feed: RSS 2.0, RSS 1.0 and Atom, each item's title, link and summary; an OpenSearch <c>totalResults</c> where
/// the feed holds one, read as <see cref="WebXml"/> reads XML.
/// </summary>
internal static class FeedItems
{
    /// <summary>The types a feed is asked for in, as a request's <c>Accept</c> says them.</summary>
    public const string Accept = "application/atom+xml, application/rss+xml, application/rdf+xml;q=0.9, application/xml;q=0.8, text/xml;q=0.8";

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Rss1 = "http://purl.org/rss/1.0/";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";

    /// <summary>
    /// The results the feed <paramref name="response"/> holds: a limit or a failure its status says, an answer that is no
    /// XML or no feed told as a change, an item with no title or address left out and counted, and every item left out
    /// told as a change.
    /// </summary>
    /// <param name="response">The endpoint's response.</param>
    public static WebSearchAnswer Read(WebSearchResponse.Http response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (WebSearchStatus.Before(response, out var pause) is { } early)
        {
            return early;
        }

        XElement root;

        try
        {
            root = WebXml.Read(response.Body).Root!;
        }
        catch (XmlException ex)
        {
            return WebSearchAnswer.Changed($"its answer is no XML: {ex.Message}") with { Until = pause };
        }

        (IEnumerable<XElement> Items, XElement? Total, Func<XElement, (string? Title, string? Link, string? Summary)> Read)? feed = root.Name switch
        {
            { LocalName: "rss", NamespaceName: "" } when root.Element("channel") is { } channel
                => (channel.Elements("item"), channel.Element(OpenSearch + "totalResults"), RssItem),
            var name when name == Atom + "feed"
                => (root.Elements(Atom + "entry"), root.Element(OpenSearch + "totalResults"), AtomEntry),
            var name when name == Rdf + "RDF"
                => (root.Elements(Rss1 + "item"), null, Rss1Item),
            _ => null,
        };

        if (feed is not { } read)
        {
            return WebSearchAnswer.Changed($"its answer is no RSS or Atom feed: its root is <{root.Name.LocalName}>{(root.Name.NamespaceName.Length > 0 ? $" of '{root.Name.NamespaceName}'" : string.Empty)}") with { Until = pause };
        }

        var results = new List<WebSearchResult>();
        var leftOut = 0;

        foreach (var item in read.Items.Take(WebSearchAnswer.MostResults))
        {
            var (title, link, summary) = read.Read(item);

            if (WebSearchResult.Of(title, link, summary, response.Address) is { } result)
            {
                results.Add(result);
            }
            else
            {
                leftOut++;
            }
        }

        if (leftOut > 0 && results.Count == 0)
        {
            return WebSearchAnswer.Changed($"none of its {leftOut} items gave a title and an address") with { Until = pause };
        }

        var total = long.TryParse(read.Total?.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : (long?)null;

        return WebSearchAnswer.Read(results) with { Total = total, Until = pause, LeftOut = leftOut };
    }

    private static (string?, string?, string?) RssItem(XElement item)
    {
        var link = item.Element("link")?.Value;

        // A guid is its item's address unless it says it is not one.
        if (string.IsNullOrWhiteSpace(link) && item.Element("guid") is { } guid && !string.Equals(guid.Attribute("isPermaLink")?.Value, "false", StringComparison.OrdinalIgnoreCase))
        {
            link = guid.Value;
        }

        return (Markup(item.Element("title")?.Value), link, Markup(item.Element("description")?.Value ?? item.Element(Content + "encoded")?.Value));
    }

    private static (string?, string?, string?) AtomEntry(XElement entry)
    {
        var link = entry.Elements(Atom + "link")
            .FirstOrDefault(candidate => candidate.Attribute("rel")?.Value is null or "alternate")?
            .Attribute("href")?.Value;

        return (Text(entry.Element(Atom + "title")), link, Text(entry.Element(Atom + "summary") ?? entry.Element(Atom + "content")));
    }

    private static (string?, string?, string?) Rss1Item(XElement item)
        => (Markup(item.Element(Rss1 + "title")?.Value), item.Element(Rss1 + "link")?.Value, Markup(item.Element(Rss1 + "description")?.Value));

    /// <summary>
    /// The text of an Atom text construct (RFC 4287, 3.1) or of an entry's content (4.1.3.3), as its type says:
    /// <c>html</c> and a <c>text/html</c> content are markup, reduced as a field is; <c>text</c> - its type where none is
    /// said - and any other <c>text/</c> content are text as they stand; <c>xhtml</c> and an XML content are elements of
    /// the feed, their markup reduced as a field is, so a block is never run into the next; a content of any other media
    /// type is base64 of something that is no text, and none.
    /// </summary>
    private static string? Text(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        // A media type is read without its parameters, and in any case, as RFC 2045 compares one.
        var type = element.Attribute("type")?.Value.Split(';')[0].Trim().ToLowerInvariant() ?? "text";

        return type switch
        {
            "text" => element.Value,
            "html" or "text/html" => Markup(element.Value),
            "xhtml" => Markup(Inner(element)),
            _ when type.EndsWith("+xml", StringComparison.Ordinal) || type.EndsWith("/xml", StringComparison.Ordinal) => Markup(Inner(element)),
            _ when type.StartsWith("text/", StringComparison.Ordinal) => element.Value,
            _ => null,
        };
    }

    /// <summary>
    /// The markup of the feed's own elements inside <paramref name="element"/>, as they stand: no blank added between two
    /// (as an indented writing of them adds one), and a CDATA section's text written as text, which HTML has no CDATA for.
    /// </summary>
    private static string Inner(XElement element)
    {
        if (element.DescendantNodes().Any(node => node is XCData))
        {
            element = new XElement(element);

            foreach (var data in element.DescendantNodes().OfType<XCData>().ToList())
            {
                data.ReplaceWith(new XText(data.Value));
            }
        }

        return string.Concat(element.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
    }

    /// <summary>A field that is markup by its format's word - an RSS item's title and description, an Atom html text - reduced to its text.</summary>
    private static string? Markup(string? field) => field is null ? null : WebPageText.OfMarkup(field);
}
