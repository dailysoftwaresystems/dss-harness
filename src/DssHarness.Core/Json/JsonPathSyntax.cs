using System.Text.Json;

namespace DssHarness.Core.Json;

/// <summary>A query read: whether it begins at <c>@</c> or <c>$</c>, and its segments in order.</summary>
/// <param name="Relative">Whether it begins at the value it is asked of.</param>
/// <param name="Segments">Its segments.</param>
internal sealed record JsonPathQuery(bool Relative, IReadOnlyList<JsonPathSegment> Segments)
{
    /// <summary>Whether every segment is singular: a query that selects at most one value.</summary>
    public bool IsSingular => Segments.All(segment => segment.Singular);
}

/// <summary>One segment: its selectors, applied to each value it is given, or to each and every value beneath it.</summary>
/// <param name="Descendant">Whether it is a descendant segment (<c>..</c>).</param>
/// <param name="Selectors">Its selectors, in order.</param>
/// <param name="Singular">Whether it is a name or an index segment as a singular query is made of.</param>
internal sealed record JsonPathSegment(bool Descendant, IReadOnlyList<JsonPathSelector> Selectors, bool Singular);

/// <summary>One selector of a segment.</summary>
internal abstract record JsonPathSelector
{
    /// <summary>The member of an object of this name.</summary>
    internal sealed record Name(string Member) : JsonPathSelector;

    /// <summary>Every element of an array, every member of an object.</summary>
    internal sealed record Wildcard : JsonPathSelector;

    /// <summary>The element of an array at this index, counted from its end where negative.</summary>
    internal sealed record Index(long At) : JsonPathSelector;

    /// <summary>The elements of an array from <paramref name="Start"/> up to <paramref name="End"/>, <paramref name="Step"/> apart.</summary>
    internal sealed record Slice(long? Start, long? End, long? Step) : JsonPathSelector;

    /// <summary>Every element of an array, every member of an object, for which the test holds.</summary>
    internal sealed record Filter(JsonPathLogical Test) : JsonPathSelector;
}

/// <summary>A filter's logical expression.</summary>
internal abstract record JsonPathLogical
{
    /// <summary>Holds where any of its parts does.</summary>
    internal sealed record Or(IReadOnlyList<JsonPathLogical> Parts) : JsonPathLogical;

    /// <summary>Holds where every one of its parts does.</summary>
    internal sealed record And(IReadOnlyList<JsonPathLogical> Parts) : JsonPathLogical;

    /// <summary>Holds where its part does not.</summary>
    internal sealed record Not(JsonPathLogical Part) : JsonPathLogical;

    /// <summary>Two values compared.</summary>
    internal sealed record Comparison(JsonPathComparable Left, string Operator, JsonPathComparable Right) : JsonPathLogical;

    /// <summary>Holds where the query selects anything.</summary>
    internal sealed record Exists(JsonPathQuery Query) : JsonPathLogical;

    /// <summary>A function's answer, logical, or a list of values that holds where it is not empty.</summary>
    internal sealed record Test(JsonPathFunction Function) : JsonPathLogical;
}

/// <summary>A value a comparison compares.</summary>
internal abstract record JsonPathComparable
{
    /// <summary>A literal.</summary>
    internal sealed record Literal(JsonElement Value) : JsonPathComparable;

    /// <summary>A singular query's value, or nothing.</summary>
    internal sealed record Query(JsonPathQuery Singular) : JsonPathComparable;

    /// <summary>A function whose answer is a value, or nothing.</summary>
    internal sealed record Function(JsonPathFunction Call) : JsonPathComparable;
}

/// <summary>The three types of RFC 9535's function extensions.</summary>
internal enum JsonPathType
{
    /// <summary>A JSON value, or nothing.</summary>
    Value,

    /// <summary>True or false.</summary>
    Logical,

    /// <summary>A list of values.</summary>
    Nodes,
}

/// <summary>A function called: which, with what, and the type of what it answers.</summary>
/// <param name="Name">The function's name.</param>
/// <param name="Arguments">What it is given.</param>
/// <param name="Result">The type of its answer.</param>
internal sealed record JsonPathFunction(string Name, IReadOnlyList<JsonPathArgument> Arguments, JsonPathType Result);

/// <summary>What a function is given.</summary>
internal abstract record JsonPathArgument
{
    /// <summary>A literal.</summary>
    internal sealed record Literal(JsonElement Value) : JsonPathArgument;

    /// <summary>A query: its values, or the one value of a singular query.</summary>
    internal sealed record Query(JsonPathQuery Of) : JsonPathArgument;

    /// <summary>A logical expression.</summary>
    internal sealed record Logical(JsonPathLogical Expression) : JsonPathArgument;

    /// <summary>Another function's answer.</summary>
    internal sealed record Function(JsonPathFunction Call) : JsonPathArgument;
}
