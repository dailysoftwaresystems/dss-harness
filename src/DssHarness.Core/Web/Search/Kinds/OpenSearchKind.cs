using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Web.Search.Kinds;

/// <summary>
/// <c>opensearch</c>: a site that describes its own search in an OpenSearch description, kept for the run once read, whose template
/// for results is taken where it answers a feed - Atom first, then RSS - or JSON the source says how to read; one that
/// offers only <c>text/html</c> is refused, for that is a results page for people.
/// </summary>
public sealed class OpenSearchKind : IWebSearchKind
{
    /// <summary>What the description is asked for as.</summary>
    private const string DescriptionTypes = "application/opensearchdescription+xml, application/xml;q=0.9, text/xml;q=0.8";

    /// <summary>The types of a template for results taken, in the order one is preferred.</summary>
    private static readonly string[] FeedTypes = ["application/atom+xml", "application/rss+xml", "application/rdf+xml"];

    /// <inheritdoc/>
    public string Kind => "opensearch";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public IReadOnlyList<KeyDescription> Keys { get; } =
    [
        new("url", "the address of the site's OpenSearch description, which every source's own description is not") { Required = true },
        .. JsonSearchItems.Keys(required: false).Select(key => key with { Meaning = $"where the template taken answers JSON: {key.Meaning}" }),
    ];

    /// <inheritdoc/>
    public string Help => "reads a site's OpenSearch description, at url, kept for the run once read, and takes its template for results where it answers a feed - Atom, "
        + "then RSS - or JSON, read by items, title and link as a json-api source's; a description that offers only text/html is "
        + "refused, for that is a results page for people.";

    /// <inheritdoc/>
    public IReadOnlyList<string> Problems(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();

        if (source.AddressProblem("url") is { } url)
        {
            problems.Add(url);
        }

        problems.AddRange(JsonSearchItems.Problems(source));

        var given = new[] { "items", "title", "link" }.Where(source.Has).ToList();

        if (given.Count is > 0 and < 3)
        {
            problems.Add($"'{string.Join("', '", given)}' {(given.Count == 1 ? "is" : "are")} given without the rest of items, title and link, which read a JSON answer together");
        }

        return problems;
    }

    /// <inheritdoc/>
    public IReadOnlyList<WebSearchRequest.Http> Lookups(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return [WebSearchRequest.Http.Get(source.Text("url")!, DescriptionTypes)];
    }

    /// <inheritdoc/>
    public WebSearchFound Found(WebSearchSource source, IReadOnlyList<WebSearchResponse.Http> answers)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (answers is not [var answer])
        {
            throw new ArgumentException($"'{source.Name}' looks up its description alone, and is given {answers.Count} answer(s).", nameof(answers));
        }

        var what = $"its description, '{source.Text("url")}'";

        if (WebSearchStatus.Before(answer, out var pause) is { } early)
        {
            return WebSearchFound.FailedWith(early.Of(what));
        }

        // A description that cannot be used still holds the source for the pause its answer asked for.
        WebSearchFound Failed(WebSearchAnswer failure) => WebSearchFound.FailedWith(failure.Of(what) with { Until = pause });

        XElement root;

        try
        {
            root = WebXml.Read(answer.Body).Root!;
        }
        catch (XmlException ex)
        {
            return Failed(WebSearchAnswer.Changed($"it is no XML: {ex.Message}"));
        }

        if (root.Name.LocalName != "OpenSearchDescription")
        {
            return Failed(WebSearchAnswer.Changed($"it is no OpenSearch description: its root is <{root.Name.LocalName}>"));
        }

        var templates = root.Elements()
            .Where(element => element.Name.LocalName == "Url" && Rels(element).Contains("results"))
            .Select(element => (Type: Type(element), Template: element.Attribute("template")?.Value, Element: element))
            .Where(url => url.Template is not null && MediaTypeHeaderValue.TryParse(url.Type, out _) && Usable(url.Template))
            .ToList();

        var json = JsonSearchItems.Of(source) is not null;

        var chosen = FeedTypes.Select(type => templates.FirstOrDefault(url => url.Type == type)).FirstOrDefault(url => url.Template is not null);

        if (chosen.Template is null && json)
        {
            chosen = templates.FirstOrDefault(url => IsJson(url.Type));
        }

        if (chosen.Template is null)
        {
            var offered = templates.Select(url => url.Type).Distinct(StringComparer.Ordinal).ToList();

            return Failed(
                offered.Count > 0 && offered.All(type => type == "text/html")
                    ? WebSearchAnswer.Refused("it offers its results as text/html alone: a results page for people, which this tool never reads")
                    : offered.Any(IsJson)
                        ? WebSearchAnswer.Failed("it offers its results as JSON, and the source gives no items, title and link to read them by")
                        : WebSearchAnswer.Changed(offered.Count == 0
                            ? "it names no template for results that this tool can fill"
                            : $"it offers its results as {string.Join(", ", offered)}, none a feed or JSON"));
        }

        return WebSearchFound.Of(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["template"] = chosen.Template,
            ["type"] = chosen.Type,
            ["indexOffset"] = Offset(chosen.Element, "indexOffset"),
            ["pageOffset"] = Offset(chosen.Element, "pageOffset"),
        }) with { Until = pause };
    }

    /// <inheritdoc/>
    public WebSearchRequest Request(WebSearchSource source, WebSearchFound found, string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(found);

        return WebSearchRequest.Http.Get(Filled(found["template"], query, limit, found["indexOffset"], found["pageOffset"]), found["type"]);
    }

    /// <inheritdoc/>
    public WebSearchAnswer Read(WebSearchSource source, WebSearchFound found, WebSearchResponse response)
    {
        ArgumentNullException.ThrowIfNull(found);

        if (response is not WebSearchResponse.Http answer)
        {
            throw new ArgumentException($"A source of kind '{Kind}' is answered by its endpoint, never by an index.", nameof(response));
        }

        return IsJson(found["type"])
            ? (JsonSearchItems.Of(source) ?? throw new InvalidOperationException($"'{source.Name}' took a JSON template with no items, title and link to read it by.")).Read(answer, null)
            : FeedItems.Read(answer);
    }

    private static string[] Rels(XElement url) => (url.Attribute("rel")?.Value ?? "results").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A template's type, its parameters dropped and its case made one.</summary>
    private static string Type(XElement url) => (url.Attribute("type")?.Value ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

    /// <summary>Whether <paramref name="type"/> is JSON of results: a suggestion's JSON is no results.</summary>
    private static bool IsJson(string type) => (type == "application/json" || type.EndsWith("+json", StringComparison.Ordinal)) && !type.Contains("suggestions", StringComparison.Ordinal);

    /// <summary>
    /// Whether a template can be filled: it names <c>searchTerms</c>, no parameter it requires is one this tool does not
    /// know, and filled it is an http or https address.
    /// </summary>
    private static bool Usable(string template)
    {
        var names = Parameters(template);

        return names.Any(parameter => parameter.Name == "searchTerms")
            && names.All(parameter => parameter.Optional || Known(parameter.Name))
            && WebAddresses.IsWeb(Filled(template, "q", 1, "1", "1"));
    }

    /// <summary>An offset a template's element says - a whole number - else 1, the specification's own.</summary>
    private static string Offset(XElement url, string name)
        => int.TryParse(url.Attribute(name)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ? offset.ToString(CultureInfo.InvariantCulture) : "1";

    private static bool Known(string name) => name is "searchTerms" or "count" or "startIndex" or "startPage" or "language" or "inputEncoding" or "outputEncoding";

    /// <summary>The parameters a template names in braces, each with whether it is optional (<c>?</c>).</summary>
    private static List<(string Name, bool Optional)> Parameters(string template)
    {
        var parameters = new List<(string, bool)>();
        var at = 0;

        while ((at = template.IndexOf('{', at)) >= 0)
        {
            var close = template.IndexOf('}', at);

            if (close < 0)
            {
                break;
            }

            var name = template[(at + 1)..close];
            parameters.Add((name.TrimEnd('?'), name.EndsWith('?')));
            at = close + 1;
        }

        return parameters;
    }

    /// <summary>The template filled: the query escaped, the count the limit, an offset where one is asked, and what it may leave out left out.</summary>
    private static string Filled(string template, string query, int limit, string indexOffset, string pageOffset)
    {
        var filled = new StringBuilder();
        var at = 0;

        while (at < template.Length)
        {
            var open = template.IndexOf('{', at);
            var close = open < 0 ? -1 : template.IndexOf('}', open);

            if (close < 0)
            {
                filled.Append(template, at, template.Length - at);
                break;
            }

            filled.Append(template, at, open - at);

            var name = template[(open + 1)..close].TrimEnd('?');

            filled.Append(name switch
            {
                "searchTerms" => Uri.EscapeDataString(query),
                "count" => limit.ToString(CultureInfo.InvariantCulture),
                "startIndex" => indexOffset,
                "startPage" => pageOffset,
                "language" => "*",
                "inputEncoding" or "outputEncoding" => "UTF-8",
                _ => string.Empty,
            });

            at = close + 1;
        }

        return filled.ToString();
    }
}
