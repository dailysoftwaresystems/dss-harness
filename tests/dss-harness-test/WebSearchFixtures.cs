using System.Text.Json;
using DssHarness.Core.Web.Search;

namespace DssHarness.Tests;

/// <summary>
/// Stored answers of search sources (<c>fixtures/web-search</c>), in the shapes their owners document, and the sources and
/// responses the kinds' tests build from them.
/// </summary>
internal static class WebSearchFixtures
{
    /// <summary>When every stored answer was read.</summary>
    public static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The stored answer <paramref name="name"/>.</summary>
    public static string Stored(string name)
        => File.ReadAllText(Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "web-search", name));

    /// <summary>A source named <paramref name="name"/> whose keys are the JSON object <paramref name="keys"/>.</summary>
    public static WebSearchSource Source(string name, string keys)
    {
        using var document = JsonDocument.Parse(keys);
        return new WebSearchSource(name, document.RootElement);
    }

    /// <summary>An endpoint's answer: <paramref name="body"/>, of <paramref name="status"/> and <paramref name="type"/>, with <paramref name="headers"/>.</summary>
    public static WebSearchResponse.Http Answer(
        string body,
        int status = 200,
        string? type = "application/json",
        IReadOnlyDictionary<string, string>? headers = null,
        string address = "https://api.example.test/search")
        => new(address, status, type, headers ?? new Dictionary<string, string>(), body, At);

    /// <summary>Headers by name.</summary>
    public static IReadOnlyDictionary<string, string> Headers(params (string Name, string Value)[] headers)
        => headers.ToDictionary(header => header.Name, header => header.Value, StringComparer.OrdinalIgnoreCase);
}
