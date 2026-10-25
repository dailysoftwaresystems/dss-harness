using System.Text.Json;

namespace DssHarness.Core.Configuration;

/// <summary>
/// The members of a JSON object the configuration holds as it was written - a web-search source's keys, its credential's,
/// its discovered names' - read as the configuration reads its own keys: by name in any case, and a name written twice,
/// in one spelling or two, refused, for nothing could say which value was meant.
/// </summary>
public static class JsonObjectKeys
{
    /// <summary>The value of <paramref name="name"/> in <paramref name="value"/>, matched in any case; <see langword="null"/> where it is no object or holds none.</summary>
    /// <param name="value">The object.</param>
    /// <param name="name">The key.</param>
    public static JsonElement? Value(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var member in value.EnumerateObject())
        {
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return member.Value;
            }
        }

        return null;
    }

    /// <summary>The text <paramref name="name"/> holds in <paramref name="value"/>, matched in any case; <see langword="null"/> where it holds none.</summary>
    /// <param name="value">The object.</param>
    /// <param name="name">The key.</param>
    public static string? Text(JsonElement value, string name) => Value(value, name) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    /// <summary>
    /// Why <paramref name="value"/> holds a key twice - in two spellings that differ only in case, or in one - each said
    /// as <paramref name="prefix"/> and the key; <see langword="null"/> where it holds each once, or is no object.
    /// </summary>
    /// <param name="value">The object.</param>
    /// <param name="prefix">What leads each key as a problem names it, as <c>credential.</c>; empty at the top.</param>
    public static string? TwiceProblem(JsonElement value, string prefix) => TwiceProblem(value, prefix, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why <paramref name="value"/> holds a name twice, the names matched by <paramref name="names"/> - exactly, for
    /// members that are names a template fills rather than keys - each said as <paramref name="prefix"/> and the name;
    /// <see langword="null"/> where it holds each once, or is no object.
    /// </summary>
    /// <param name="value">The object.</param>
    /// <param name="prefix">What leads each name as a problem names it.</param>
    /// <param name="names">How two of its names are one.</param>
    public static string? TwiceProblem(JsonElement value, string prefix, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(names);

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var twice = value.EnumerateObject()
            .Select(member => member.Name)
            .GroupBy(name => name, names)
            .FirstOrDefault(group => group.Count() > 1);

        if (twice is null)
        {
            return null;
        }

        var spellings = twice.Distinct(StringComparer.Ordinal).ToList();

        return spellings.Count > 1
            ? $"'{prefix}{spellings[0]}' is given twice, as {string.Join(" and ", spellings.Select(spelling => $"'{prefix}{spelling}'"))}: a key is one whatever its case, and nothing could say which value was meant"
            : $"'{prefix}{spellings[0]}' is given twice, and nothing could say which value was meant";
    }
}
