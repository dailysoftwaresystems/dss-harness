using System.Globalization;
using System.Text.Json;

namespace DssHarness.Core.Json;

/// <summary>
/// Answers a query read by <see cref="JsonPathParser"/> for one document, by RFC 9535's semantics: a segment's selectors in
/// turn for each value it is given, a descendant segment's for the value and each beneath it - itself first, then each
/// child's in the document's order - and a filter's comparisons, logic and functions as the standard defines them.
/// </summary>
internal static class JsonPathEvaluator
{
    /// <summary>Adds what <paramref name="query"/> selects - from <paramref name="current"/> or <paramref name="root"/> - to <paramref name="selected"/>.</summary>
    public static void Select(JsonPathQuery query, JsonElement current, JsonElement root, List<JsonElement> selected)
    {
        List<JsonElement> values = [query.Relative ? current : root];

        foreach (var segment in query.Segments)
        {
            var next = new List<JsonElement>();

            foreach (var value in values)
            {
                if (segment.Descendant)
                {
                    foreach (var beneath in SelfAndBeneath(value))
                    {
                        SelectEach(segment.Selectors, beneath, root, next);
                    }
                }
                else
                {
                    SelectEach(segment.Selectors, value, root, next);
                }
            }

            values = next;
        }

        selected.AddRange(values);
    }

    /// <summary>
    /// <paramref name="value"/> and every value beneath it, each before what is beneath it and an array's or object's
    /// children in the document's order: walked by a stack of its own, for a document nests as deep as it likes.
    /// </summary>
    private static IEnumerable<JsonElement> SelfAndBeneath(JsonElement value)
    {
        var waiting = new Stack<JsonElement>();
        waiting.Push(value);

        while (waiting.TryPop(out var at))
        {
            yield return at;

            var children = Children(at);

            for (var index = children.Count - 1; index >= 0; index--)
            {
                waiting.Push(children[index]);
            }
        }
    }

    /// <summary>The elements of an array, the members' values of an object, nothing of any other value.</summary>
    private static List<JsonElement> Children(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => [.. value.EnumerateArray()],
        JsonValueKind.Object => [.. value.EnumerateObject().Select(member => member.Value)],
        _ => [],
    };

    private static void SelectEach(IReadOnlyList<JsonPathSelector> selectors, JsonElement value, JsonElement root, List<JsonElement> selected)
    {
        foreach (var selector in selectors)
        {
            switch (selector)
            {
                case JsonPathSelector.Name name when value.ValueKind == JsonValueKind.Object:
                    foreach (var member in value.EnumerateObject())
                    {
                        if (member.NameEquals(name.Member))
                        {
                            selected.Add(member.Value);
                            break;
                        }
                    }

                    break;

                case JsonPathSelector.Wildcard:
                    selected.AddRange(Children(value));
                    break;

                case JsonPathSelector.Index index when value.ValueKind == JsonValueKind.Array:
                    var length = value.GetArrayLength();
                    var at = index.At >= 0 ? index.At : length + index.At;

                    if (at >= 0 && at < length)
                    {
                        selected.Add(value[(int)at]);
                    }

                    break;

                case JsonPathSelector.Slice slice when value.ValueKind == JsonValueKind.Array:
                    Slice(slice, value, selected);
                    break;

                case JsonPathSelector.Filter filter:
                    foreach (var child in Children(value))
                    {
                        if (Holds(filter.Test, child, root))
                        {
                            selected.Add(child);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>The elements a slice selects, by the standard's bounds: each bound counted from the end where negative, then held inside the array.</summary>
    private static void Slice(JsonPathSelector.Slice slice, JsonElement array, List<JsonElement> selected)
    {
        var step = slice.Step ?? 1;

        if (step == 0)
        {
            return;
        }

        long length = array.GetArrayLength();

        long Normalized(long bound) => bound >= 0 ? bound : length + bound;

        if (step > 0)
        {
            var lower = Math.Clamp(Normalized(slice.Start ?? 0), 0, length);
            var upper = Math.Clamp(Normalized(slice.End ?? length), 0, length);

            for (var at = lower; at < upper; at += step)
            {
                selected.Add(array[(int)at]);
            }
        }
        else
        {
            var upper = Math.Clamp(Normalized(slice.Start ?? length - 1), -1, length - 1);
            var lower = Math.Clamp(Normalized(slice.End ?? -length - 1), -1, length - 1);

            for (var at = upper; lower < at; at += step)
            {
                selected.Add(array[(int)at]);
            }
        }
    }

    /// <summary>Whether <paramref name="test"/> holds for <paramref name="current"/>, in <paramref name="root"/>.</summary>
    private static bool Holds(JsonPathLogical test, JsonElement current, JsonElement root) => test switch
    {
        JsonPathLogical.Or or => or.Parts.Any(part => Holds(part, current, root)),
        JsonPathLogical.And and => and.Parts.All(part => Holds(part, current, root)),
        JsonPathLogical.Not not => !Holds(not.Part, current, root),
        JsonPathLogical.Exists exists => Nodes(exists.Query, current, root).Count > 0,
        JsonPathLogical.Test function => Truth(Call(function.Function, current, root)),
        JsonPathLogical.Comparison comparison => Compare(Value(comparison.Left, current, root), comparison.Operator, Value(comparison.Right, current, root)),
        _ => throw new InvalidOperationException($"A filter's expression is one the parser makes, not {test.GetType().Name}."),
    };

    private static List<JsonElement> Nodes(JsonPathQuery query, JsonElement current, JsonElement root)
    {
        var selected = new List<JsonElement>();
        Select(query, current, root, selected);
        return selected;
    }

    /// <summary>A comparable's value: a literal, a singular query's one value, or a function's; <see langword="null"/> for nothing.</summary>
    private static JsonElement? Value(JsonPathComparable comparable, JsonElement current, JsonElement root) => comparable switch
    {
        JsonPathComparable.Literal literal => literal.Value,
        JsonPathComparable.Query query => Nodes(query.Singular, current, root) is [var one] ? one : null,
        JsonPathComparable.Function function => Call(function.Call, current, root).Value,
        _ => throw new InvalidOperationException($"A comparable is one the parser makes, not {comparable.GetType().Name}."),
    };

    /// <summary>A function's answer, of its type: a value or nothing, true or false, or a list of values.</summary>
    private static Answer Call(JsonPathFunction function, JsonElement current, JsonElement root)
    {
        switch (function.Name)
        {
            case "length":
                return Argument(function.Arguments[0], current, root).Value is { } measured
                    ? measured.ValueKind switch
                    {
                        JsonValueKind.String => Answer.Of(Number(CodePoints(measured.GetString()!))),
                        JsonValueKind.Array => Answer.Of(Number(measured.GetArrayLength())),
                        JsonValueKind.Object => Answer.Of(Number(measured.EnumerateObject().Count())),
                        _ => Answer.Nothing,
                    }
                    : Answer.Nothing;

            case "count":
                return Answer.Of(Number(Argument(function.Arguments[0], current, root).Nodes!.Count));

            case "value":
                return Argument(function.Arguments[0], current, root).Nodes is [var single] ? Answer.Of(single) : Answer.Nothing;

            case "match":
            case "search":
                var text = Argument(function.Arguments[0], current, root).Value;
                var pattern = Argument(function.Arguments[1], current, root).Value;

                return Answer.Logical(
                    text is { ValueKind: JsonValueKind.String } subject
                    && pattern is { ValueKind: JsonValueKind.String } expression
                    && IRegexp.Matches(expression.GetString()!, subject.GetString()!, whole: function.Name == "match"));

            default:
                throw new InvalidOperationException($"'{function.Name}' is a function the parser takes, and is answered here.");
        }
    }

    /// <summary>What a function is given, as the type it takes it: a literal's or a singular query's value, a query's values, or a logical answer.</summary>
    private static Answer Argument(JsonPathArgument argument, JsonElement current, JsonElement root) => argument switch
    {
        JsonPathArgument.Literal literal => Answer.Of(literal.Value),
        JsonPathArgument.Query query => Answer.Listing(Nodes(query.Of, current, root)),
        JsonPathArgument.Logical logical => Answer.Logical(Holds(logical.Expression, current, root)),
        JsonPathArgument.Function function => Call(function.Call, current, root),
        _ => throw new InvalidOperationException($"An argument is one the parser makes, not {argument.GetType().Name}."),
    };

    /// <summary>A test's truth: a logical answer as it is, a list of values where it is not empty.</summary>
    private static bool Truth(Answer answer) => answer.Nodes is { } nodes ? nodes.Count > 0 : answer.Truth;

    private static JsonElement Number(int value) => JsonSerializer.SerializeToElement(value);

    /// <summary>How many Unicode scalar values <paramref name="text"/> holds: a pair of surrogates is one.</summary>
    private static int CodePoints(string text)
    {
        var count = 0;

        for (var at = 0; at < text.Length; at++)
        {
            if (char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]))
            {
                at++;
            }

            count++;
        }

        return count;
    }

    /// <summary>A comparison by the standard: nothing equals nothing alone; order is numbers' and strings' alone.</summary>
    private static bool Compare(JsonElement? left, string comparison, JsonElement? right) => comparison switch
    {
        "==" => Equal(left, right),
        "!=" => !Equal(left, right),
        "<" => Less(left, right),
        ">" => Less(right, left),
        "<=" => Less(left, right) || Equal(left, right),
        ">=" => Less(right, left) || Equal(left, right),
        _ => throw new InvalidOperationException($"'{comparison}' is no comparison the parser takes."),
    };

    private static bool Equal(JsonElement? left, JsonElement? right)
        => left is null || right is null ? left is null && right is null : Equal(left.Value, right.Value);

    private static bool Equal(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Number:
                return NumberOf(left) == NumberOf(right);

            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);

            case JsonValueKind.Array:
                return left.GetArrayLength() == right.GetArrayLength() && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => Equal(pair.First, pair.Second));

            case JsonValueKind.Object:
                if (left.GetPropertyCount() != right.GetPropertyCount())
                {
                    return false;
                }

                // As many members, and each found by its name, never sought among the rest: a name given twice is its last
                // value's, as a reader of the object takes it.
                var named = Members(left);
                var others = Members(right);

                return named.Count == others.Count && named.All(member => others.TryGetValue(member.Key, out var other) && Equal(member.Value, other));

            default:
                // true, false and null: one value each, the kind alone says which.
                return true;
        }
    }

    private static Dictionary<string, JsonElement> Members(JsonElement value)
    {
        var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var member in value.EnumerateObject())
        {
            members[member.Name] = member.Value;
        }

        return members;
    }

    private static bool Less(JsonElement? left, JsonElement? right)
    {
        if (left is not { } a || right is not { } b || a.ValueKind != b.ValueKind)
        {
            return false;
        }

        return a.ValueKind switch
        {
            JsonValueKind.Number => NumberOf(a) < NumberOf(b),
            JsonValueKind.String => CompareScalars(a.GetString()!, b.GetString()!) < 0,
            _ => false,
        };
    }

    /// <summary>A number's value as a double, as I-JSON holds JSON's numbers - a magnitude past one infinite, as JSON's is unbounded.</summary>
    private static double NumberOf(JsonElement number)
        => number.TryGetDouble(out var value) ? value : double.Parse(number.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>Two strings compared by their Unicode scalar values, as the standard orders strings - not by UTF-16's code units.</summary>
    private static int CompareScalars(string left, string right)
    {
        var a = left.EnumerateRunes().GetEnumerator();
        var b = right.EnumerateRunes().GetEnumerator();

        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();

            if (!hasA || !hasB)
            {
                return hasA ? 1 : hasB ? -1 : 0;
            }

            var order = a.Current.Value.CompareTo(b.Current.Value);

            if (order != 0)
            {
                return order;
            }
        }
    }

    /// <summary>A function's answer: a value or nothing, a truth, or a list of values - one of the three, by its type.</summary>
    private readonly record struct Answer(JsonElement? Value, bool Truth, IReadOnlyList<JsonElement>? Nodes)
    {
        public static Answer Nothing => default;

        public static Answer Of(JsonElement value) => new(value, false, null);

        public static Answer Logical(bool truth) => new(null, truth, null);

        public static Answer Listing(IReadOnlyList<JsonElement> nodes) => new(nodes is [var one] ? one : null, false, nodes);
    }
}
