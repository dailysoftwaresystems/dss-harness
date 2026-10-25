using DssHarness.Core.Web.Index;

namespace DssHarness.Core.Web.Search;

/// <summary>What a kind asks for a query, or for a lookup: a request to its source's endpoint, or a search of the harness's own index of it.</summary>
public abstract record WebSearchRequest
{
    private WebSearchRequest()
    {
    }

    /// <summary>A request to the source's own endpoint, which the harness's fetch sends under its policy.</summary>
    /// <param name="Method"><c>GET</c>, or <c>POST</c> with <paramref name="Body"/>.</param>
    /// <param name="Address">The address, filled.</param>
    /// <param name="Headers">
    /// The headers the kind sends, <c>Accept</c> among them and a body's <c>Content-Type</c>; the fetch adds its own
    /// <c>User-Agent</c>, and the search a source's key.
    /// </param>
    /// <param name="Body">A <c>POST</c>'s body, JSON; <see langword="null"/> for a <c>GET</c>.</param>
    public sealed record Http(string Method, string Address, IReadOnlyDictionary<string, string> Headers, string? Body) : WebSearchRequest
    {
        /// <summary>The headers the request itself says - which tool asks, of which host, how much and of what type - which no source sends of its own.</summary>
        public static IReadOnlyList<string> OwnHeaders { get; } = ["User-Agent", "Host", "Content-Length", "Content-Type"];

        /// <summary>The headers that carry a key, which a source sends only as its <c>credential</c>, kept secret.</summary>
        public static IReadOnlyList<string> KeyHeaders { get; } = ["Authorization", "Proxy-Authorization", "Cookie"];

        /// <summary>A <c>GET</c> of <paramref name="address"/>, accepting <paramref name="accept"/>.</summary>
        public static Http Get(string address, string accept) => new("GET", address, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = accept }, null);

        /// <summary>Whether <paramref name="name"/> is a header's name: one or more of the characters RFC 9110 lets a token hold.</summary>
        /// <param name="name">The name.</param>
        public static bool IsHeaderName(string? name)
            => name is { Length: > 0 } && name.All(character => character is > ' ' and < (char)0x7F && !"()<>@,;:\\\"/[]?={}".Contains(character));
    }

    /// <summary>A search of the harness's own index of the source: its pages, read through its sitemaps, as its settings say.</summary>
    /// <param name="Query">The query.</param>
    /// <param name="Limit">The most results it gives.</param>
    /// <param name="Settings">What the index reads, as the source's keys say it.</param>
    public sealed record Indexed(string Query, int Limit, SitemapSettings Settings) : WebSearchRequest;
}

/// <summary>What a request was answered: the endpoint's response, or the index's.</summary>
public abstract record WebSearchResponse
{
    private WebSearchResponse()
    {
    }

    /// <summary>The endpoint's response, read whole.</summary>
    /// <param name="Address">The address that answered, its redirects followed.</param>
    /// <param name="Status">Its status.</param>
    /// <param name="Type">Its type; <see langword="null"/> where it said none.</param>
    /// <param name="Headers">Its headers, by name in any case, each one's values joined by commas.</param>
    /// <param name="Body">Its body, decoded.</param>
    /// <param name="At">When it was read.</param>
    public sealed record Http(string Address, int Status, string? Type, IReadOnlyDictionary<string, string> Headers, string Body, DateTimeOffset At) : WebSearchResponse
    {
        /// <summary>The header <paramref name="name"/>, in any case; <see langword="null"/> where it is not there.</summary>
        /// <param name="name">The header's name.</param>
        public string? Header(string name) => WebRateLimit.Header(Headers, name);
    }

    /// <summary>What the harness's own index of the source answered.</summary>
    /// <param name="Answer">The index's answer.</param>
    public sealed record Indexed(WebIndexAnswer Answer) : WebSearchResponse;
}

/// <summary>
/// What a source's lookups found - the values a kind reads once a run before its first request, as a discovered address
/// or an OpenSearch description's template - or why they found nothing, which the source then answers every query of the run.
/// </summary>
public sealed record WebSearchFound
{
    private WebSearchFound(IReadOnlyDictionary<string, string> values, WebSearchAnswer? failure)
    {
        Values = values;
        Failure = failure;
        Until = failure?.Until;
    }

    /// <summary>Nothing to find: a kind with no lookups.</summary>
    public static WebSearchFound None { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal), null);

    /// <summary>The values found, by name.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>Why the lookups found nothing; <see langword="null"/> where they found what they sought.</summary>
    public WebSearchAnswer? Failure { get; }

    /// <summary>
    /// When the source may be asked again, where a lookup's answer asked to be left alone or said its requests are spent;
    /// <see langword="null"/> where none did. Its first request waits for it, as any of its requests would.
    /// </summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>The values <paramref name="values"/>.</summary>
    public static WebSearchFound Of(IReadOnlyDictionary<string, string> values) => new(values, null);

    /// <summary>Nothing found, as <paramref name="failure"/> says.</summary>
    public static WebSearchFound FailedWith(WebSearchAnswer failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(new Dictionary<string, string>(StringComparer.Ordinal), failure);
    }

    /// <summary>The value found under <paramref name="name"/>.</summary>
    /// <exception cref="InvalidOperationException">None was: a kind asked for a value its lookups never give.</exception>
    public string this[string name]
        => Values.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"No lookup of this source found '{name}'.");
}
