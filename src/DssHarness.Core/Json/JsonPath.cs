using System.Text.Json;

namespace DssHarness.Core.Json;

/// <summary>
/// A JSONPath query as RFC 9535 defines it, read once and asked of many documents: <c>$.results</c>,
/// <c>$.resources[?@['@type']=='SearchQueryService']['@id']</c>, and, relative to one value, <c>@.title</c>. The whole of
/// the standard - every segment and selector, filters with their comparisons and logic, the five functions with their
/// types checked as the query is read, and I-Regexp (RFC 9485) for <c>match</c> and <c>search</c> - held to the
/// standard's own compliance suite, which a test runs in full.
/// </summary>
/// <remarks>
/// Our own rather than a package's: the one complete implementation for .NET, JsonPath.Net, put its binary releases under
/// a maintenance fee of its own from its version 3, and an older version would be a pin on a line nobody mends. A query is
/// asked of <see cref="JsonElement"/>s, which never change under it, and selects the values a document holds - no copy -
/// in the standard's order: a segment's selectors in turn for each value it was given, an object's members as the
/// document holds them.
/// </remarks>
public sealed class JsonPath
{
    private readonly JsonPathQuery _query;

    private JsonPath(string text, JsonPathQuery query)
    {
        Text = text;
        _query = query;
    }

    /// <summary>The query as it was written.</summary>
    public string Text { get; }

    /// <summary>Whether it begins at the value it is asked of (<c>@</c>) rather than at a document's root (<c>$</c>).</summary>
    public bool Relative => _query.Relative;

    /// <summary>
    /// Whether it is a singular query as RFC 9535 defines one: names and indexes alone, one at a time, so that it selects
    /// at most one value whatever the document.
    /// </summary>
    public bool IsSingular => _query.IsSingular;

    /// <summary>Reads <paramref name="text"/>, a query from a document's root (<c>$</c>).</summary>
    /// <param name="text">The query.</param>
    /// <exception cref="JsonPathException">It is no query RFC 9535 reads, or a function in it is not given what it takes.</exception>
    public static JsonPath Parse(string text) => new(text, JsonPathParser.Read(text, relative: false));

    /// <summary>Reads <paramref name="text"/>, a query from the value it is asked of (<c>@</c>), as a filter's are.</summary>
    /// <param name="text">The query.</param>
    /// <exception cref="JsonPathException">It is no query RFC 9535 reads, or a function in it is not given what it takes.</exception>
    public static JsonPath ParseRelative(string text) => new(text, JsonPathParser.Read(text, relative: true));

    /// <summary>Why <paramref name="text"/> is no query, or <see langword="null"/> where it is one.</summary>
    /// <param name="text">The query.</param>
    /// <param name="relative">Whether it is to begin at <c>@</c> rather than <c>$</c>.</param>
    public static string? Problem(string? text, bool relative = false)
    {
        if (text is null)
        {
            return "is not given";
        }

        try
        {
            JsonPathParser.Read(text, relative);
            return null;
        }
        catch (JsonPathException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>The values the query selects in <paramref name="root"/>, in the standard's order.</summary>
    /// <param name="root">The document, which <c>$</c> names, and <c>@</c> too for a relative query.</param>
    public IReadOnlyList<JsonElement> Select(JsonElement root) => Select(root, root);

    /// <summary>The values the query selects from <paramref name="current"/> - <c>@</c> - in <paramref name="root"/>, <c>$</c>.</summary>
    /// <param name="current">The value a relative query begins at.</param>
    /// <param name="root">The document it is in, which a <c>$</c> in a filter names.</param>
    public IReadOnlyList<JsonElement> Select(JsonElement current, JsonElement root)
    {
        var selected = new List<JsonElement>();
        JsonPathEvaluator.Select(_query, current, root, selected);
        return selected;
    }

    /// <summary>The query as it was written.</summary>
    public override string ToString() => Text;
}

/// <summary>A text that is no JSONPath query, with where it stops being one.</summary>
public sealed class JsonPathException : FormatException
{
    /// <summary>A query refused for <paramref name="message"/>, at character <paramref name="position"/>.</summary>
    /// <param name="message">Why, said whole.</param>
    /// <param name="position">The character it stops being a query at, from 0.</param>
    public JsonPathException(string message, int position)
        : base(message)
    {
        Position = position;
    }

    /// <summary>The character the query stops being one at, from 0.</summary>
    public int Position { get; }
}
