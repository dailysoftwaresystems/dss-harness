using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// The configuration's web search, resolved: every source - the built-in ones (<c>Templates/web-search-sources.json</c>)
/// with the repository's entries laid over them key by key, then the repository's own - what a step that names no
/// <c>search</c> searches, the bounds, and why any of it cannot be used (V56, and V55 for <c>defaults</c>). The one
/// owner of what the sources are: the configuration's check, a step's <c>search</c>, the slot's appointed names and help
/// all read them here.
/// </summary>
public sealed class WebSearchSources
{
    /// <summary>The most requests of sources one step makes where the configuration says none.</summary>
    public const int DefaultMaxPerStep = 60;

    /// <summary>The most <c>maxPerStep</c> may be.</summary>
    public const int MostMaxPerStep = 1000;

    /// <summary>The most results a source gives one search where the configuration says none.</summary>
    public const int DefaultResultsPerSource = 8;

    /// <summary>The most <c>resultsPerSource</c> may be.</summary>
    public const int MostResultsPerSource = 20;

    private const string MaxPerStepMeaning = "the most requests of sources one step makes";

    private const string ResultsPerSourceMeaning = "the most results a source gives one search";

    private static AgentWebSearchConfig? _builtIn;

    /// <summary>The keys of <c>agentWebSearch</c>, each with what it is: what help lists.</summary>
    public static IReadOnlyList<KeyDescription> Keys { get; } =
    [
        new("sources", $"each source by its name: its {string.Join(", ", WebSearchSource.OwnKeys.Select(key => key.Name))}, and its kind's own keys; a built-in's name changes it key by key, a new name adds a source"),
        new("defaults", "names of sources or tags a reason step that says no search searches, those turned on; absent, the built-in ones"),
        new("maxPerStep", $"{MaxPerStepMeaning}, from 1 to {MostMaxPerStep.ToString("N0", CultureInfo.InvariantCulture)}; {DefaultMaxPerStep} where none is said"),
        new("resultsPerSource", $"{ResultsPerSourceMeaning}, from 1 to {MostResultsPerSource}; {DefaultResultsPerSource} where none is said"),
    ];

    private WebSearchSources(IReadOnlyList<WebSearchSource> all, IReadOnlyList<string> defaults, int maxPerStep, int resultsPerSource, IReadOnlyList<string> problems)
    {
        All = all;
        Defaults = defaults;
        MaxPerStep = maxPerStep;
        ResultsPerSource = resultsPerSource;
        Problems = problems;
    }

    /// <summary>
    /// The section as the tool holds it, read from its embedded template: the built-in sources and the defaults, which a
    /// configuration without the section has and <c>init</c> writes into the configuration it creates.
    /// </summary>
    public static AgentWebSearchConfig BuiltIn => LazyInitializer.EnsureInitialized(ref _builtIn, ReadBuiltIn);

    /// <summary>Every source, the built-in ones first in the template's order, then the repository's own in theirs.</summary>
    public IReadOnlyList<WebSearchSource> All { get; }

    /// <summary>The names of sources or tags a step that names none searches: the configuration's, else the built-in ones.</summary>
    public IReadOnlyList<string> Defaults { get; }

    /// <summary>The most requests of sources one step makes.</summary>
    public int MaxPerStep { get; }

    /// <summary>The most results a source gives one search.</summary>
    public int ResultsPerSource { get; }

    /// <summary>Why the configuration's web search cannot be used, each problem one line naming where it is; empty where it can.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Every tag a source carries, in the order the sources first carry them.</summary>
    public IReadOnlyList<string> Tags => [.. All.SelectMany(source => source.Tags).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// The web search <paramref name="config"/> declares, resolved over the built-in sources and held to
    /// <paramref name="kinds"/>: each source to its kind's keys and every source's own, the names, tags, defaults and
    /// bounds to theirs.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="kinds">The kinds a source may name.</param>
    public static WebSearchSources Of(HarnessConfig config, WebSearchKinds kinds)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(kinds);

        var declared = config.AgentWebSearch;
        var problems = new List<string>();
        var entries = new List<(string Name, JsonObject Keys)>();

        foreach (var (name, keys) in BuiltIn.Sources ?? [])
        {
            entries.Add((name, (JsonObject)JsonNode.Parse(keys.GetRawText())!));
        }

        foreach (var (name, keys) in declared?.Sources ?? [])
        {
            var at = $"agentWebSearch.sources.{name}";

            if (!WebSearchSource.IsName(name))
            {
                problems.Add($"{at} is no source's name: a source's name names its index's directory, and is {WebSearchSource.NameRule}");
                continue;
            }

            if (keys.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{at} is an object of the source's keys, and is {keys.GetRawText()}");
                continue;
            }

            if (JsonObjectKeys.TwiceProblem(keys, string.Empty) is { } twice)
            {
                problems.Add($"{at}: {twice}");
                continue;
            }

            var index = entries.FindIndex(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));

            if (index < 0)
            {
                entries.Add((name, (JsonObject)JsonNode.Parse(keys.GetRawText())!));
                continue;
            }

            // Key by key over the built-in entry: a key the repository writes replaces the built-in's of that name,
            // whatever its case, and the rest stand.
            var merged = entries[index].Keys;

            foreach (var member in keys.EnumerateObject())
            {
                foreach (var held in merged.Select(pair => pair.Key).Where(key => string.Equals(key, member.Name, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    merged.Remove(held);
                }

                merged[member.Name] = JsonNode.Parse(member.Value.GetRawText());
            }
        }

        var all = entries.Select(entry => Source(entry.Name, entry.Keys)).ToList();

        foreach (var source in all)
        {
            var at = source.Kind is { } kind ? $"agentWebSearch.sources.{source.Name} ({kind})" : $"agentWebSearch.sources.{source.Name}";
            problems.AddRange(kinds.Problems(source).Select(problem => $"{at}: {problem}"));
        }

        foreach (var tag in all.SelectMany(source => source.Tags).Distinct(StringComparer.Ordinal).Where(tag => all.Any(source => source.Name == tag)))
        {
            problems.Add($"agentWebSearch: '{tag}' names both a source and a tag, and a step's search could not say which it means");
        }

        var maxPerStep = Within(declared?.MaxPerStep, DefaultMaxPerStep, MostMaxPerStep, "maxPerStep", MaxPerStepMeaning, problems);
        var resultsPerSource = Within(declared?.ResultsPerSource, DefaultResultsPerSource, MostResultsPerSource, "resultsPerSource", ResultsPerSourceMeaning, problems);
        var defaults = declared?.Defaults ?? BuiltIn.Defaults ?? [];

        HarnessConfigValidator.RefuseGivenEmpty(declared?.Defaults, "agentWebSearch.defaults", "name sources or tags", "the built-in defaults", problems);
        // A source turned off is left out of the defaults, never refused there: turning one off is one key of its own.
        problems.AddRange(new WebSearchSources(all, defaults, maxPerStep, resultsPerSource, [])
            .ListProblems(declared?.Defaults, "agentWebSearch.defaults", "agentWebSearch.defaults names", off: false));

        return new WebSearchSources(all, defaults, maxPerStep, resultsPerSource, problems);
    }

    /// <summary>The source named <paramref name="name"/>; <see langword="null"/> where none is.</summary>
    /// <param name="name">A source's name.</param>
    public WebSearchSource? Find(string? name) => All.FirstOrDefault(source => string.Equals(source.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// The sources <paramref name="names"/> - names of sources or tags, <see cref="Defaults"/> where none are given - pick,
    /// in the order they name them, each once and those turned on alone: a tag gives every source that carries it, in the
    /// configuration's order.
    /// </summary>
    /// <param name="names">A step's <c>search</c>; <see langword="null"/> where it names none.</param>
    public IReadOnlyList<WebSearchSource> Chosen(IReadOnlyList<string>? names)
    {
        var chosen = new List<WebSearchSource>();

        foreach (var name in names ?? Defaults)
        {
            var picked = Find(name) is { } source ? [source] : All.Where(candidate => candidate.Tags.Contains(name, StringComparer.Ordinal));

            chosen.AddRange(picked.Where(source => source.Enabled && !chosen.Contains(source)));
        }

        return chosen;
    }

    /// <summary>
    /// Why the list <paramref name="names"/> cannot choose sources (V55's rule of a list): a name given twice, a name that is
    /// no source or tag, and, where <paramref name="off"/>, a source turned off or a tag none of whose sources is on. Empty
    /// where it can, or where it is not given; a list given empty is refused where it is read, as every list whose absence
    /// means something is.
    /// </summary>
    /// <param name="names">The list; <see langword="null"/> where it is not given.</param>
    /// <param name="list">What the list is, as a problem begins: <c>the search of step 'research'</c>.</param>
    /// <param name="naming">What names each name, as a problem begins: <c>step 'research' searches</c>.</param>
    /// <param name="off">Whether a name of nothing turned on is refused: a step's search, which would ask it; never the defaults.</param>
    public IReadOnlyList<string> ListProblems(IReadOnlyList<string>? names, string list, string naming, bool off = true)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(naming);

        if (names is null)
        {
            return [];
        }

        var problems = new List<string>(TwiceProblems(names, list));

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            if (Find(name) is { } source)
            {
                if (off && !source.Enabled)
                {
                    problems.Add($"{naming} '{name}', which is turned off (enabled: false); turn it on, or name another");
                }
            }
            else if (All.Where(candidate => candidate.Tags.Contains(name, StringComparer.Ordinal)).ToList() is { Count: > 0 } tagged)
            {
                if (off && !tagged.Any(candidate => candidate.Enabled))
                {
                    problems.Add($"{naming} '{name}', a tag none of whose sources is on: {string.Join(", ", tagged.Select(candidate => candidate.Name))}");
                }
            }
            else
            {
                problems.Add($"{naming} '{name}', which is no source or tag of agentWebSearch; sources: {string.Join(", ", All.Select(candidate => candidate.Name))}; tags: {string.Join(", ", Tags)}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Why the list <paramref name="names"/> is no list of sources whatever the configuration holds: a name given twice. The
    /// part of <see cref="ListProblems"/> a definition is held to where no configuration is given.
    /// </summary>
    /// <param name="names">The list.</param>
    /// <param name="list">What the list is, as a problem begins.</param>
    public static IReadOnlyList<string> TwiceProblems(IReadOnlyList<string> names, string list)
    {
        ArgumentNullException.ThrowIfNull(names);

        return [.. names.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(twice => $"{list} names '{twice.Key}' more than once")];
    }

    private static WebSearchSource Source(string name, JsonObject keys)
    {
        using var document = JsonDocument.Parse(keys.ToJsonString());
        return new WebSearchSource(name, document.RootElement);
    }

    private static int Within(int? value, int fallback, int most, string key, string what, List<string> problems)
    {
        if (value is { } given && (given < 1 || given > most))
        {
            problems.Add($"agentWebSearch.{key} is {given.ToString(CultureInfo.InvariantCulture)}; it is {what}, from 1 to {most.ToString("N0", CultureInfo.InvariantCulture)}");
            return fallback;
        }

        return value ?? fallback;
    }

    private static AgentWebSearchConfig ReadBuiltIn()
    {
        using var stream = typeof(WebSearchSources).Assembly.GetManifestResourceStream("DssHarness.Core.WebSearchSources")
            ?? throw new InvalidOperationException("the built-in web search sources are not embedded in this build");

        return JsonSerializer.Deserialize<Template>(stream, JsonConfigOptions.Default)?.AgentWebSearch
            ?? throw new InvalidOperationException("the built-in web search sources do not read back");
    }

    private sealed class Template
    {
        public AgentWebSearchConfig? AgentWebSearch { get; init; }
    }
}
