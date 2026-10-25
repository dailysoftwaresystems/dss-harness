using DssHarness.Core.Configuration;
using DssHarness.Core.Repository;
using DssHarness.Core.Web.Index;

namespace DssHarness.Core.Web.Search.Kinds;

/// <summary>
/// <c>sitemap</c>: a site with no search of its own for programs, searched in the harness's own index of it - its pages,
/// read through its sitemaps as its <c>robots.txt</c> allows, at its <c>Crawl-delay</c> and in the source's turn on this
/// machine (<see cref="SitemapFill"/>) - which answers from what it holds, and says how much.
/// </summary>
public sealed class SitemapKind : IWebSearchKind
{
    /// <summary>The most pages an index holds where the source says not.</summary>
    public const int DefaultMaxPages = 2_000;

    /// <summary>The most pages a source may ask an index to hold.</summary>
    public const int MostPages = 20_000;

    /// <summary>How old an index may be, in days, where the source says not.</summary>
    public const int DefaultRefreshDays = 7;

    /// <summary>The oldest a source may let its index be, in days.</summary>
    public const int MostRefreshDays = 365;

    /// <inheritdoc/>
    public string Kind => "sitemap";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public IReadOnlyList<KeyDescription> Keys { get; } =
    [
        new("site", "the site's address, its scheme and host alone, as https://digital.gov") { Required = true },
        new("sitemap", "the sitemap's address; where none is said, those robots.txt names, else /sitemap.xml"),
        new("include", "patterns of the paths of the pages read, as /guides/**; every page where none is said"),
        new("exclude", "patterns of the paths of pages never read"),
        new("maxPages", $"the most pages its index holds: {DefaultMaxPages:N0} where none is said, {MostPages:N0} at most"),
        new("refreshDays", $"how many days old its index may be before it is read again: {DefaultRefreshDays} where none is said"),
    ];

    /// <inheritdoc/>
    public string Help { get; } = "searches the harness's own index of a site that has no search for programs: its pages, read through its "
        + "sitemaps (gzip too) as its robots.txt allows, at its Crawl-delay and in the source's turn on this machine, and kept "
        + $"under {HarnessLayout.OrchestratorsDirectoryName}/{Orchestration.OrchestratorLayout.WebIndexesDirectoryName}/<source>/. The index is filled in the background when the source is first searched, "
        + "and read again past refreshDays, pages whose lastmod moved first; a search answers from what it holds, and says how much.";

    /// <inheritdoc/>
    public IReadOnlyList<string> Problems(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();

        if (source.AddressProblem("site") is { } site)
        {
            problems.Add(site);
        }
        else if (source.Text("site") is { } written && new Uri(written) is var uri && (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0))
        {
            problems.Add($"'site' is a site's scheme and host alone, as https://{uri.Host}, and '{written}' names a path; say the pages read by include");
        }

        if (source.AddressProblem("sitemap") is { } sitemap)
        {
            problems.Add(sitemap);
        }

        foreach (var key in new[] { "include", "exclude" })
        {
            if (!source.Has(key))
            {
                continue;
            }

            if (source.Value(key) is not { ValueKind: System.Text.Json.JsonValueKind.Array } || source.Texts(key) is not { } patterns)
            {
                problems.Add($"'{key}' is a list of patterns of paths, as [\"/guides/**\"]");
                continue;
            }

            problems.AddRange(patterns.Select(pattern => GlobPattern.Problem(pattern) is { } problem ? $"'{key}' holds '{pattern}', which {problem}" : null).OfType<string>());
        }

        if (source.IntegerProblem("maxPages", 1, MostPages) is { } maxPages)
        {
            problems.Add(maxPages);
        }

        if (source.IntegerProblem("refreshDays", 1, MostRefreshDays) is { } refreshDays)
        {
            problems.Add(refreshDays);
        }

        return problems;
    }

    /// <summary>What an index of <paramref name="source"/> reads, as its keys say it.</summary>
    /// <param name="source">A source of this kind, its keys held to <see cref="Problems"/>.</param>
    public static SitemapSettings Settings(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new SitemapSettings(
            new Uri(source.Text("site")!).GetLeftPart(UriPartial.Authority),
            source.Text("sitemap"),
            [.. (source.Texts("include") ?? []).Select(GlobPattern.Parse)],
            [.. (source.Texts("exclude") ?? []).Select(GlobPattern.Parse)],
            source.Integer("maxPages") ?? DefaultMaxPages,
            TimeSpan.FromDays(source.Integer("refreshDays") ?? DefaultRefreshDays));
    }

    /// <inheritdoc/>
    public IReadOnlyList<WebSearchRequest.Http> Lookups(WebSearchSource source) => [];

    /// <inheritdoc/>
    public WebSearchFound Found(WebSearchSource source, IReadOnlyList<WebSearchResponse.Http> answers) => WebSearchFound.None;

    /// <inheritdoc/>
    public WebSearchRequest Request(WebSearchSource source, WebSearchFound found, string query, int limit) => new WebSearchRequest.Indexed(query, limit, Settings(source));

    /// <inheritdoc/>
    public WebSearchAnswer Read(WebSearchSource source, WebSearchFound found, WebSearchResponse response)
    {
        if (response is not WebSearchResponse.Indexed { Answer: var index })
        {
            throw new ArgumentException($"A source of kind '{Kind}' is answered by the harness's index of it, never by an endpoint.", nameof(response));
        }

        if (index.Held == 0 && !index.Filling)
        {
            // Held off for the site's or the machine's word, not for a problem: asked again once the time it gave has passed.
            if (index.PausedUntil is { } until && index.PausedFor is { } why)
            {
                return WebSearchAnswer.Limited(until, $"{why} until {WebRateLimit.Said(until)}, and its index holds no page yet") with { Held = index.Said };
            }

            if (index.Problem is { } problem)
            {
                return WebSearchAnswer.Failed($"its index holds no page: {problem}") with { Held = index.Said };
            }
        }

        var results = index.Hits
            .Select(hit => WebSearchResult.Of(hit.Title, hit.Address, hit.Snippet, null))
            .OfType<WebSearchResult>()
            .ToList();

        return WebSearchAnswer.Read(results) with { Held = index.Said, LeftOut = index.Hits.Count - results.Count };
    }
}
