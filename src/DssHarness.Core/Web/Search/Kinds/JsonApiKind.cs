using System.Text;
using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Json;

namespace DssHarness.Core.Web.Search.Kinds;

/// <summary>
/// <c>json-api</c>: a search API that answers JSON, asked at an address its template fills - <c>{query}</c>, <c>{limit}</c>,
/// <c>{page}</c>, <c>{start}</c>, and any name <c>discover</c> reads from another JSON document, kept for the run once found -
/// and read by RFC 9535 JSONPath queries: the array of results, every one of the page, each one's title, address and
/// snippet by templates of queries of the item, how many it holds, and a pause it asks for in its body. A page past the
/// first is asked by its number where its templates name <c>{page}</c> or <c>{start}</c>; one that names neither answers
/// one page.
/// </summary>
public sealed class JsonApiKind : IWebSearchKind
{
    /// <inheritdoc/>
    public string Kind => "json-api";

    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public IReadOnlyList<KeyDescription> Keys { get; } =
    [
        new("url", "the address asked: a template whose {query} is the query, {limit} how many results a page holds (pageSize), {page} the page asked, as the source counts its pages, {start} how many results come before it, and any other name in braces a name discover reads; one that names neither {page} nor {start} asks one page") { Required = true },
        new("firstPage", "the number the source counts its first page as, which {page} is filled from: 1 where none is said, or 0"),
        new("method", "GET, where none is said, or POST with body"),
        new("body", "a POST's body, JSON: each text in it a template as url's, and a text that is {limit}, {page} or {start} alone the number"),
        new("headers", "headers to send, by name: never User-Agent, a key, what the request itself says or what a body says of itself"),
        .. JsonSearchItems.Keys(required: true),
        new("backoff", "a JSONPath query of the answer that finds a number of seconds the source asks to be left alone"),
        new("discover", "names whose values are read from another JSON document, kept for the run once found: each name's url, an address, and path, a JSONPath query of it"),
    ];

    /// <inheritdoc/>
    public string Help => "asks a search API that answers JSON, at the address url fills, and reads its answer by JSONPath queries (RFC 9535): "
        + "items finds the array of results, and title, link and snippet are templates of queries of each item in braces - "
        + "{@.title}, https://stackoverflow.com/q/{@.question_id} - link the first of several whose queries all answer. An address "
        + "to be found, as NuGet's search, is read by discover from the document that names it, kept for the run once found.";

    /// <inheritdoc/>
    public IReadOnlyList<string> Problems(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();
        var discovered = DiscoverProblems(source, problems);
        var post = source.Text("method") == "POST";

        if (source.TextProblem("method") is { } methodText)
        {
            problems.Add(methodText);
        }
        else if (source.Text("method") is { } method and not ("GET" or "POST"))
        {
            problems.Add($"'method' is GET or POST, and is '{method}'");
        }

        if (post && !source.Has("body"))
        {
            problems.Add("'body' is missing: a POST sends one");
        }
        else if (!post && source.Has("body"))
        {
            problems.Add("'body' is sent with a POST alone, and 'method' is GET");
        }

        var named = new List<string>();

        // Whether every template - url's and each text of body's - was read, so what they name together is known.
        var read = source.Text("url") is not null;

        if (source.TextProblem("url") is { } urlText)
        {
            problems.Add(urlText);
        }
        else if (source.Text("url") is { } url)
        {
            if (WebRequestTemplate.Problem(url, discovered, needsQuery: false) is not null)
            {
                read = false;
            }
            else
            {
                named.AddRange(WebRequestTemplate.Parse(url).Names);
            }

            if (WebRequestTemplate.AddressProblem("url", url, discovered, needsQuery: false) is { } problem)
            {
                problems.Add(problem);
            }
        }

        if (source.Value("body") is { } body)
        {
            foreach (var text in Texts(body))
            {
                if (WebRequestTemplate.Problem(text, discovered, needsQuery: false) is { } problem)
                {
                    problems.Add($"'body' holds '{text}', which {problem}");
                    read = false;
                }
                else
                {
                    named.AddRange(WebRequestTemplate.Parse(text).Names);
                }
            }
        }

        if (read && !named.Contains(WebRequestTemplate.Query))
        {
            problems.Add($"neither 'url' nor 'body' names {{{WebRequestTemplate.Query}}}, so no query would reach the source");
        }

        if (read)
        {
            problems.AddRange(discovered.Where(name => !named.Contains(name)).Select(name => $"'discover' reads '{name}', which neither 'url' nor 'body' names"));
        }

        if (source.Value("firstPage") is { } first && (first.ValueKind != JsonValueKind.Number || !first.TryGetInt32(out var counted) || counted is not (0 or 1)))
        {
            problems.Add($"'firstPage' is 1 or 0, the number the source counts its first page as, and is {first.GetRawText()}");
        }
        else if (read && source.Has("firstPage") && !named.Contains(WebRequestTemplate.Page))
        {
            problems.Add($"'firstPage' says what {{{WebRequestTemplate.Page}}} counts from, and neither 'url' nor 'body' names {{{WebRequestTemplate.Page}}}");
        }

        if (source.Value("headers") is { } headers)
        {
            if (headers.ValueKind != JsonValueKind.Object || headers.EnumerateObject().Any(header => header.Value.ValueKind != JsonValueKind.String))
            {
                problems.Add("'headers' is an object of texts, each a header's value by its name");
            }
            else
            {
                // A header's name is one whatever its case, as HTTP reads it.
                if (JsonObjectKeys.TwiceProblem(headers, "headers.") is { } twice)
                {
                    problems.Add(twice);
                }

                foreach (var header in headers.EnumerateObject())
                {
                    if (WebSearchRequest.Http.OwnHeaders.FirstOrDefault(name => string.Equals(name, header.Name, StringComparison.OrdinalIgnoreCase)) is { } own)
                    {
                        problems.Add($"'headers' names {own}, which a source never sends of its own: the request itself says it");
                    }
                    else if (WebSearchRequest.Http.KeyHeaders.FirstOrDefault(name => string.Equals(name, header.Name, StringComparison.OrdinalIgnoreCase)) is { } key)
                    {
                        problems.Add($"'headers' names {key}, which carries a key: a key is sent as the source's credential, kept secret");
                    }
                    else if (!WebSearchRequest.Http.IsHeaderName(header.Name))
                    {
                        problems.Add($"'headers' names '{header.Name}', which is no header's name");
                    }
                    else if (!WebSearchRequest.Http.IsRequestHeader(header.Name))
                    {
                        problems.Add($"'headers' names '{header.Name}', which a body says of itself and no request carries");
                    }
                }
            }
        }

        problems.AddRange(JsonSearchItems.Problems(source));

        if (source.TextProblem("backoff") is { } backoffText)
        {
            problems.Add(backoffText);
        }
        else if (source.Text("backoff") is { } backoff && JsonSearchItems.PathProblem(backoff, "backoff", singular: true) is { } backoffProblem)
        {
            problems.Add(backoffProblem);
        }

        return problems;
    }

    /// <inheritdoc/>
    public IReadOnlyList<WebSearchRequest.Http> Lookups(WebSearchSource source)
        => [.. Discovered(source).Select(discover => WebSearchRequest.Http.Get(discover.Url, "application/json"))];

    /// <inheritdoc/>
    public WebSearchFound Found(WebSearchSource source, IReadOnlyList<WebSearchResponse.Http> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);

        var discovered = Discovered(source);

        if (answers.Count > discovered.Count)
        {
            throw new ArgumentException($"'{source.Name}' looks up {discovered.Count} document(s), and is given {answers.Count} answer(s).", nameof(answers));
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        DateTimeOffset? until = null;

        for (var at = 0; at < answers.Count; at++)
        {
            var (name, url, path) = discovered[at];
            var answer = answers[at];
            var what = $"its '{name}', looked up at '{url}'";

            // A document that cannot be used still holds the source for every pause the answers read so far asked for.
            if (WebSearchStatus.Before(answer, out var pause) is { } early)
            {
                return WebSearchFound.FailedWith(early.Of(what) with { Until = WebRateLimit.Later(until, early.Until) });
            }

            until = WebRateLimit.Later(until, pause);

            try
            {
                using var document = JsonSearchItems.AnswerOf(answer.Body);

                if (JsonPath.Parse(path).Select(document.RootElement).Select(WebItemTemplate.TextOf).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) is not { } value)
                {
                    return WebSearchFound.FailedWith(WebSearchAnswer.Changed($"'{path}' finds no text in it").Of(what) with { Until = until });
                }

                values[name] = value;
            }
            catch (JsonException ex)
            {
                return WebSearchFound.FailedWith(WebSearchAnswer.Changed($"it is no JSON: {ex.Message}").Of(what) with { Until = until });
            }
        }

        // Fewer answers than lookups end at one whose status failed, which returned above.
        if (answers.Count < discovered.Count)
        {
            throw new ArgumentException($"'{source.Name}' looks up {discovered.Count} document(s), and is given {answers.Count} answer(s), the last of which did not fail.", nameof(answers));
        }

        return WebSearchFound.Of(values) with { Until = until };
    }

    /// <inheritdoc/>
    public string? KeyOrigin(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return WebRequestTemplate.Parse(source.Text("url")!).Origin();
    }

    /// <inheritdoc/>
    public WebSearchRequest Request(WebSearchSource source, WebSearchFound found, string query, int page)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(found);

        var url = WebRequestTemplate.Parse(source.Text("url")!);
        var body = source.Value("body");

        if (page > 1 && !url.Pages && (body is not { } texts || !Texts(texts).Any(text => WebRequestTemplate.Parse(text).Pages)))
        {
            return new WebSearchRequest.OnePage($"its address names no {{{WebRequestTemplate.Page}}} and no {{{WebRequestTemplate.Start}}}, so it answers one page, the first");
        }

        var asked = new WebSearchPage(page, source.PageSize, source.Integer("firstPage") ?? 1);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };

        if (source.Value("headers") is { ValueKind: JsonValueKind.Object } given)
        {
            foreach (var header in given.EnumerateObject())
            {
                headers[header.Name] = header.Value.GetString()!;
            }
        }

        var address = url.Address(query, asked, found.Values);

        if (body is not { } sent)
        {
            return new WebSearchRequest.Http("GET", address, headers, null);
        }

        headers["Content-Type"] = "application/json";

        return new WebSearchRequest.Http("POST", address, headers, Filled(sent, query, asked, found.Values));
    }

    /// <inheritdoc/>
    public WebSearchAnswer Read(WebSearchSource source, WebSearchFound found, WebSearchResponse response)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (response is not WebSearchResponse.Http answer)
        {
            throw new ArgumentException($"A source of kind '{Kind}' is answered by its endpoint, never by an index.", nameof(response));
        }

        var items = JsonSearchItems.Of(source) ?? throw new InvalidOperationException($"'{source.Name}' gives no items, title and link, which its kind requires.");

        return items.Read(answer, source.Text("backoff") is { } backoff ? JsonPath.Parse(backoff) : null);
    }

    /// <summary>The names <c>discover</c> reads, each with its document's address and its query, in their order; problems added to <paramref name="problems"/>.</summary>
    private static List<string> DiscoverProblems(WebSearchSource source, List<string> problems)
    {
        var names = new List<string>();

        if (source.Value("discover") is not { } discover)
        {
            return names;
        }

        if (discover.ValueKind != JsonValueKind.Object)
        {
            problems.Add("'discover' is an object: each name it reads, with that name's url and path");
            return names;
        }

        // A name a template fills is matched exactly, as its placeholder is: one given twice could fill it either way.
        if (JsonObjectKeys.TwiceProblem(discover, "discover.", StringComparer.Ordinal) is { } given)
        {
            problems.Add(given);
        }

        foreach (var entry in discover.EnumerateObject())
        {
            var said = $"discover.{entry.Name}";

            if (!LegPathNames.IsName(entry.Name) || WebRequestTemplate.Filled.Contains(entry.Name))
            {
                problems.Add($"'{said}' is no name a template fills: a name {LegPathNames.NameRule}, and none of {string.Join(", ", WebRequestTemplate.Filled)}");
                continue;
            }

            if (names.Contains(entry.Name, StringComparer.Ordinal))
            {
                continue;
            }

            names.Add(entry.Name);

            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"'{said}' is an object of url and path");
                continue;
            }

            var keys = entry.Value.EnumerateObject().Select(member => member.Name).ToList();

            problems.AddRange(keys.Where(key => !key.Equals("url", StringComparison.OrdinalIgnoreCase) && !key.Equals("path", StringComparison.OrdinalIgnoreCase)).Select(key => $"'{said}' takes url and path, and not '{key}'"));

            if (JsonObjectKeys.TwiceProblem(entry.Value, $"discover.{entry.Name}.") is { } twice)
            {
                problems.Add(twice);
            }

            if (!WebAddresses.IsWeb(JsonObjectKeys.Text(entry.Value, "url")))
            {
                problems.Add($"'{said}.url' is the http or https address of the document it reads, written whole");
            }

            if (JsonObjectKeys.Text(entry.Value, "path") is not { } path)
            {
                problems.Add($"'{said}.path' is a JSONPath query of the document, finding the name's value");
            }
            else if (JsonPath.Problem(path) is { } problem)
            {
                problems.Add($"'{said}.path' is no JSONPath query of the document: {problem}");
            }
        }

        return names;
    }

    /// <summary>The documents <c>discover</c> reads, in their order: each name, its address and its query.</summary>
    private static List<(string Name, string Url, string Path)> Discovered(WebSearchSource source)
        => source.Value("discover") is { ValueKind: JsonValueKind.Object } discover
            ? [.. discover.EnumerateObject().Select(entry => (entry.Name, JsonObjectKeys.Text(entry.Value, "url")!, JsonObjectKeys.Text(entry.Value, "path")!))]
            : [];

    /// <summary>Every text in <paramref name="value"/>, member names apart, in document order, by the one walk of a document (<see cref="JsonWalk"/>), which goes down a nesting of any depth.</summary>
    private static IEnumerable<string> Texts(JsonElement value)
        => JsonWalk.Descendants(value).Where(place => place.Element.ValueKind == JsonValueKind.String).Select(place => place.Element.GetString()!);

    /// <summary>
    /// <paramref name="body"/> with each text filled as a template - a text that is <c>{limit}</c>, <c>{page}</c> or
    /// <c>{start}</c> alone the number - written as JSON: each place in document order, by the one walk of a document
    /// (<see cref="JsonWalk"/>), an object or array closed once the walk has left it - no depth is too deep for it.
    /// </summary>
    private static string Filled(JsonElement body, string query, WebSearchPage page, IReadOnlyDictionary<string, string> found)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, JsonDepth.Writer))
        {
            var open = new Stack<JsonValueKind>();

            foreach (var (place, value) in JsonWalk.Descendants(body))
            {
                while (open.Count > place.Depth)
                {
                    End(writer, open.Pop());
                }

                if (place.Name is { } name)
                {
                    writer.WritePropertyName(name);
                }

                switch (value.ValueKind)
                {
                    case JsonValueKind.String:
                        var template = WebRequestTemplate.Parse(value.GetString()!);

                        if (template.IsNumberAlone)
                        {
                            writer.WriteRawValue(template.Plain(query, page, found));
                        }
                        else
                        {
                            writer.WriteStringValue(template.Plain(query, page, found));
                        }

                        break;

                    case JsonValueKind.Array:
                        writer.WriteStartArray();
                        open.Push(JsonValueKind.Array);
                        break;

                    case JsonValueKind.Object:
                        writer.WriteStartObject();
                        open.Push(JsonValueKind.Object);
                        break;

                    default:
                        value.WriteTo(writer);
                        break;
                }
            }

            while (open.Count > 0)
            {
                End(writer, open.Pop());
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        static void End(Utf8JsonWriter writer, JsonValueKind kind)
        {
            if (kind == JsonValueKind.Array)
            {
                writer.WriteEndArray();
            }
            else
            {
                writer.WriteEndObject();
            }
        }
    }
}
