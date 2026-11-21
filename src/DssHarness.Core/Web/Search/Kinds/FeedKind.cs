using DssHarness.Core.Configuration;

namespace DssHarness.Core.Web.Search.Kinds;

/// <summary><c>feed</c>: a search that answers an RSS or Atom feed, asked at an address its template fills, each item read as a result.</summary>
public sealed class FeedKind : IWebSearchKind
{
    /// <inheritdoc/>
    public string Kind => "feed";

    /// <inheritdoc/>
    public int Version => 2;

    /// <inheritdoc/>
    public IReadOnlyList<KeyDescription> Keys { get; } =
    [
        new("url", "the address asked: a template whose {query} is the query, {limit} how many results a page holds (pageSize) where the search takes one, {page} the page asked and {start} how many results come before it; one that names neither {page} nor {start} asks one page") { Required = true },
        new("firstPage", "the number the source counts its first page as, which {page} is filled from: 1 where none is said, or 0"),
    ];

    /// <inheritdoc/>
    public string Help => "asks a search that answers a feed - RSS 2.0, RSS 1.0 or Atom - at the address url fills, and reads each item's title, link and summary as a result.";

    /// <inheritdoc/>
    public IReadOnlyList<string> Problems(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.TextProblem("url") is { } text)
        {
            return [text];
        }

        if (source.Text("url") is not { } url)
        {
            return [];
        }

        if (WebRequestTemplate.AddressProblem("url", url, [], needsQuery: true) is { } problem)
        {
            return [problem];
        }

        if (source.Value("firstPage") is { } first && (first.ValueKind != System.Text.Json.JsonValueKind.Number || !first.TryGetInt32(out var counted) || counted is not (0 or 1)))
        {
            return [$"'firstPage' is 1 or 0, the number the source counts its first page as, and is {first.GetRawText()}"];
        }

        return source.Has("firstPage") && !WebRequestTemplate.Parse(url).Names.Contains(WebRequestTemplate.Page)
            ? [$"'firstPage' says what {{{WebRequestTemplate.Page}}} counts from, and 'url' names no {{{WebRequestTemplate.Page}}}"]
            : [];
    }

    /// <inheritdoc/>
    public IReadOnlyList<WebSearchRequest.Http> Lookups(WebSearchSource source) => [];

    /// <inheritdoc/>
    public WebSearchFound Found(WebSearchSource source, IReadOnlyList<WebSearchResponse.Http> answers) => WebSearchFound.None;

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

        var url = WebRequestTemplate.Parse(source.Text("url")!);

        return page > 1 && !url.Pages
            ? new WebSearchRequest.OnePage($"its address names no {{{WebRequestTemplate.Page}}} and no {{{WebRequestTemplate.Start}}}, so it answers one page, the first")
            : WebSearchRequest.Http.Get(url.Address(query, new WebSearchPage(page, source.PageSize, source.Integer("firstPage") ?? 1), WebSearchFound.None.Values), FeedItems.Accept);
    }

    /// <inheritdoc/>
    public WebSearchAnswer Read(WebSearchSource source, WebSearchFound found, WebSearchResponse response)
        => response is WebSearchResponse.Http answer
            ? FeedItems.Read(answer)
            : throw new ArgumentException($"A source of kind '{Kind}' is answered by its endpoint, never by an index.", nameof(response));
}
