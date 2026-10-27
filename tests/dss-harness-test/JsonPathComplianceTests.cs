using System.Text.Json;
using DssHarness.Core.Json;

namespace DssHarness.Tests;

/// <summary>
/// The tool's own JSONPath asked every case of RFC 9535's compliance test suite (<c>fixtures/jsonpath-cts</c>): a selector
/// the suite says is none is refused, and every other selects what the suite says, in its order or one it allows.
/// </summary>
public sealed class JsonPathComplianceTests
{
    private static readonly Lazy<Dictionary<string, JsonElement>> Suite = new(() =>
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Prose.RepositoryRoot(), "tests", "dss-harness-test", "fixtures", "jsonpath-cts", "cts.json")));

        return document.RootElement.GetProperty("tests").EnumerateArray()
            .ToDictionary(test => test.GetProperty("name").GetString()!, test => test.Clone(), StringComparer.Ordinal);
    });

    /// <summary>Every case's name.</summary>
    public static TheoryData<string> Cases()
    {
        var cases = new TheoryData<string>();

        foreach (var name in Suite.Value.Keys)
        {
            cases.Add(name);
        }

        return cases;
    }

    /// <summary>The suite is there whole: as many cases as it had when it was taken.</summary>
    [Fact]
    public void TheSuite_IsThereWhole() => Assert.Equal(715, Suite.Value.Count);

    /// <summary>Each case is answered as the suite says.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void EachCase_IsAnsweredAsTheSuiteSays(string name)
    {
        var test = Suite.Value[name];
        var selector = test.GetProperty("selector").GetString()!;

        if (test.TryGetProperty("invalid_selector", out var invalid) && invalid.GetBoolean())
        {
            Assert.NotNull(JsonPath.Problem(selector));
            Assert.Throws<JsonPathException>(() => JsonPath.Parse(selector));
            return;
        }

        Assert.Null(JsonPath.Problem(selector));

        var selected = JsonPath.Parse(selector).Select(test.GetProperty("document"));

        var allowed = test.TryGetProperty("result", out var result)
            ? [result]
            : test.GetProperty("results").EnumerateArray().ToList();

        Assert.True(
            allowed.Any(expected => Same(expected, selected)),
            $"'{selector}' selected [{string.Join(", ", selected.Select(value => value.GetRawText()))}], and the suite allows {string.Join(" or ", allowed.Select(expected => expected.GetRawText()))}");
    }

    private static bool Same(JsonElement expected, IReadOnlyList<JsonElement> selected)
        => expected.GetArrayLength() == selected.Count && expected.EnumerateArray().Zip(selected).All(pair => JsonElement.DeepEquals(pair.First, pair.Second));
}
