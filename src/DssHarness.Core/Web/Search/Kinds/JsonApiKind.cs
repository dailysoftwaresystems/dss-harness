using System.Text;
using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Json;

namespace DssHarness.Core.Web.Search.Kinds;

/// <summary>
/// <c>json-api</c>: a search API that answers JSON, asked at an address its template fills - <c>{query}</c>, <c>{limit}</c>,
/// and any name <c>discover</c> reads once a run from another JSON document - and read by RFC 9535 JSONPath queries: the
/// array of results, each one's title, address and snippet by templates of queries of the item, how many it holds, and a
/// pause it asks for in its body.
/// </summary>
public sealed class JsonApiKind : IWebSearchKind
{
    /// <inheritdoc/>
    public string Kind => "json-api";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public IReadOnlyList<KeyDescription> Keys { get; } =
    [
        new("url", "the address asked: a template whose {query} is the query, {limit} the most results asked, and any other name in braces a name discover reads") { Required = true },
        new("method", "GET, where none is said, or POST with body"),
        new("body", "a POST's body, JSON: each text in it a template as url's, and a text that is {limit} alone the number"),
        new("headers", "headers to send, by name: never User-Agent, a key or what the request itself says"),
        .. JsonSearchItems.Keys(required: true),
        new("backoff", "a JSONPath query of the answer that finds a number of seconds the source asks to be left alone"),
        new("discover", "names whose values are read once a run from another JSON document: each name's url, an address, and path, a JSONPath query of it"),
    ];

    /// <inheritdoc/>
    public string Help => "asks a search API that answers JSON, at the address url fills, and reads its answer by JSONPath queries (RFC 9535): "
        + "items finds the array of results, and title, link and snippet are templates of queries of each item in braces - "
        + "{@.title}, https://stackoverflow.com/q/{@.question_id} - link the first of several whose queries all answer. An address "
        + "to be found, as NuGet's search, is read once a run by discover from the document that names it.";

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

        if (answers.Count != discovered.Count)
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

            if (WebSearchStatus.Before(answer, out var pause) is { } early)
            {
                return WebSearchFound.FailedWith(early.Of(what));
            }

            until = WebRateLimit.Later(until, pause);

            try
            {
                using var document = JsonDocument.Parse(answer.Body);

                if (JsonPath.Parse(path).Select(document.RootElement).Select(WebItemTemplate.TextOf).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) is not { } value)
                {
                    return WebSearchFound.FailedWith(WebSearchAnswer.Changed($"'{path}' finds no text in it").Of(what));
                }

                values[name] = value;
            }
            catch (JsonException ex)
            {
                return WebSearchFound.FailedWith(WebSearchAnswer.Changed($"it is no JSON: {ex.Message}").Of(what));
            }
        }

        return WebSearchFound.Of(values) with { Until = until };
    }

    /// <inheritdoc/>
    public WebSearchRequest Request(WebSearchSource source, WebSearchFound found, string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(found);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json" };

        if (source.Value("headers") is { ValueKind: JsonValueKind.Object } given)
        {
            foreach (var header in given.EnumerateObject())
            {
                headers[header.Name] = header.Value.GetString()!;
            }
        }

        var address = WebRequestTemplate.Parse(source.Text("url")!).Address(query, limit, found.Values);

        if (source.Value("body") is not { } body)
        {
            return new WebSearchRequest.Http("GET", address, headers, null);
        }

        headers["Content-Type"] = "application/json";

        return new WebSearchRequest.Http("POST", address, headers, Filled(body, query, limit, found.Values));
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

            if (!LegPathNames.IsName(entry.Name) || entry.Name is WebRequestTemplate.Query or WebRequestTemplate.Limit)
            {
                problems.Add($"'{said}' is no name a template fills: a name {LegPathNames.NameRule}, and neither query nor limit");
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

    /// <summary>Every text in <paramref name="value"/>, member names apart.</summary>
    private static IEnumerable<string> Texts(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString()!],
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Texts),
        JsonValueKind.Object => value.EnumerateObject().SelectMany(member => Texts(member.Value)),
        _ => [],
    };

    /// <summary><paramref name="body"/> with each text filled as a template - a text that is <c>{limit}</c> alone the number - written as JSON.</summary>
    private static string Filled(JsonElement body, string query, int limit, IReadOnlyDictionary<string, string> found)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, body);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        void Write(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var template = WebRequestTemplate.Parse(value.GetString()!);

                    if (template.IsLimitAlone)
                    {
                        writer.WriteNumberValue(limit);
                    }
                    else
                    {
                        writer.WriteStringValue(template.Plain(query, limit, found));
                    }

                    break;

                case JsonValueKind.Array:
                    writer.WriteStartArray();

                    foreach (var item in value.EnumerateArray())
                    {
                        Write(writer, item);
                    }

                    writer.WriteEndArray();
                    break;

                case JsonValueKind.Object:
                    writer.WriteStartObject();

                    foreach (var member in value.EnumerateObject())
                    {
                        writer.WritePropertyName(member.Name);
                        Write(writer, member.Value);
                    }

                    writer.WriteEndObject();
                    break;

                default:
                    value.WriteTo(writer);
                    break;
            }
        }
    }
}
