using DssHarness.Core.Configuration;

namespace DssHarness.Core.Web.Search.Kinds;

/// <summary><c>feed</c>: a search that answers an RSS or Atom feed, asked at an address its template fills, each item read as a result.</summary>
public sealed class FeedKind : IWebSearchKind
{
    /// <inheritdoc/>
    public string Kind => "feed";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public IReadOnlyList<KeyDescription> Keys { get; } =
    [
        new("url", "the address asked: a template whose {query} is the query, and {limit} the most results asked where the search takes one") { Required = true },
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

        return WebRequestTemplate.AddressProblem("url", url, [], needsQuery: true) is { } problem ? [problem] : [];
    }

    /// <inheritdoc/>
    public IReadOnlyList<WebSearchRequest.Http> Lookups(WebSearchSource source) => [];

    /// <inheritdoc/>
    public WebSearchFound Found(WebSearchSource source, IReadOnlyList<WebSearchResponse.Http> answers) => WebSearchFound.None;

    /// <inheritdoc/>
    public WebSearchRequest Request(WebSearchSource source, WebSearchFound found, string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(source);

        return WebSearchRequest.Http.Get(WebRequestTemplate.Parse(source.Text("url")!).Address(query, limit, WebSearchFound.None.Values), FeedItems.Accept);
    }

    /// <inheritdoc/>
    public WebSearchAnswer Read(WebSearchSource source, WebSearchFound found, WebSearchResponse response)
        => response is WebSearchResponse.Http answer
            ? FeedItems.Read(answer)
            : throw new ArgumentException($"A source of kind '{Kind}' is answered by its endpoint, never by an index.", nameof(response));
}
