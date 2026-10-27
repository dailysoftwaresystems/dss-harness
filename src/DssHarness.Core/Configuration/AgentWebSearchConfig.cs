using System.Text.Json;

namespace DssHarness.Core.Configuration;

/// <summary>
/// The harness's own web search, as the configuration declares it: its sources by name, what a step that names none
/// searches, and its bounds. Each source is kept as it is written - its kind decides which keys it takes - and read by
/// <see cref="Web.Search.WebSearchSources"/>, which lays the repository's entries over the built-in ones key by key.
/// Left out, the built-in sources and the default bounds hold.
/// </summary>
public sealed class AgentWebSearchConfig
{
    /// <summary>
    /// Each source by its name, its keys as written: a built-in's name changes that entry key by key, <c>enabled: false</c>
    /// turning it off; a new name adds a source.
    /// </summary>
    public Dictionary<string, JsonElement>? Sources { get; init; }

    /// <summary>Names of sources or tags: what a reason step that says no <c>search</c> searches, those turned on.</summary>
    public List<string>? Defaults { get; init; }

    /// <summary>
    /// The most requests of sources one step makes: <see cref="Web.Search.WebSearchSources.DefaultMaxPerStep"/> where left
    /// out, and at most <see cref="Web.Search.WebSearchSources.MostMaxPerStep"/>.
    /// </summary>
    public int? MaxPerStep { get; init; }

    /// <summary>
    /// The most results a source gives one search: <see cref="Web.Search.WebSearchSources.DefaultResultsPerSource"/> where
    /// left out, and at most <see cref="Web.Search.WebSearchSources.MostResultsPerSource"/>.
    /// </summary>
    public int? ResultsPerSource { get; init; }
}
