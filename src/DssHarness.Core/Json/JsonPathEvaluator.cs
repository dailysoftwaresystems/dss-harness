using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Walks;

namespace DssHarness.Core.Json;

/// <summary>
/// Answers a query read by <see cref="JsonPathParser"/> for one document, by RFC 9535's semantics: a segment's selectors in
/// turn for each value it is given, a descendant segment's for the value and each beneath it - itself first, then each
/// child's in the document's order - and a filter's comparisons, logic and functions as the standard defines them.
/// </summary>
/// <remarks>
/// A query nests as its parser read it - a filter's query holding a filter of its own, a call given a call - and is answered
/// as it nests, each nested answer a frame on a stack of its own (<see cref="Frame{T}"/>), never the thread's; a document
/// is gone down, and two values compared, on stacks of their own as well. So neither a query nor a document is answered
/// only to some depth.
/// </remarks>
internal static class JsonPathEvaluator
{
    /// <summary>Adds what <paramref name="query"/> selects - from <paramref name="current"/> or <paramref name="root"/> - to <paramref name="selected"/>.</summary>
    public static void Select(JsonPathQuery query, JsonElement current, JsonElement root, List<JsonElement> selected)
        => selected.AddRange(Nodes(query, current, root).Run());

    /// <summary>What <paramref name="query"/> selects - from <paramref name="current"/> or <paramref name="root"/> - in the standard's order.</summary>
    private static Frame<List<JsonElement>> Nodes(JsonPathQuery query, JsonElement current, JsonElement root)
        => new(self => Selecting(self, query, current, root));

    private static IEnumerable<Frame> Selecting(Frame<List<JsonElement>> self, JsonPathQuery query, JsonElement current, JsonElement root)
    {
        List<JsonElement> values = [query.Relative ? current : root];

        foreach (var segment in query.Segments)
        {
            var next = new List<JsonElement>();

            foreach (var value in values)
            {
                IEnumerable<JsonElement> given = segment.Descendant ? SelfAndBeneath(value) : [value];

                foreach (var each in given)
                {
                    foreach (var selector in segment.Selectors)
                    {
                        if (selector is not JsonPathSelector.Filter filter)
                        {
                            SelectOne(selector, each, next);
                            continue;
                        }

                        foreach (var child in Children(each))
                        {
                            var holds = Holds(filter.Test, child, root);
                            yield return holds;

                            if (holds.Result)
                            {
                                next.Add(child);
                            }
                        }
                    }
                }
            }

            values = next;
        }

        self.Give(values);
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

    /// <summary>Adds what one selector that is no filter selects of <paramref name="value"/> to <paramref name="selected"/>.</summary>
    private static void SelectOne(JsonPathSelector selector, JsonElement value, List<JsonElement> selected)
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
    private static Frame<bool> Holds(JsonPathLogical test, JsonElement current, JsonElement root)
        => new(self => Holding(self, test, current, root));

    private static IEnumerable<Frame> Holding(Frame<bool> self, JsonPathLogical test, JsonElement current, JsonElement root)
    {
        switch (test)
        {
            case JsonPathLogical.Or or:
                foreach (var part in or.Parts)
                {
                    var holds = Holds(part, current, root);
                    yield return holds;

                    if (holds.Result)
                    {
                        self.Give(true);
                        yield break;
                    }
                }

                self.Give(false);
                yield break;

            case JsonPathLogical.And and:
                foreach (var part in and.Parts)
                {
                    var holds = Holds(part, current, root);
                    yield return holds;

                    if (!holds.Result)
                    {
                        self.Give(false);
                        yield break;
                    }
                }

                self.Give(true);
                yield break;

            case JsonPathLogical.Not not:
                var negated = Holds(not.Part, current, root);
                yield return negated;

                self.Give(!negated.Result);
                yield break;

            case JsonPathLogical.Exists exists:
                var selected = Nodes(exists.Query, current, root);
                yield return selected;

                self.Give(selected.Result.Count > 0);
                yield break;

            case JsonPathLogical.Test function:
                var answered = Function(function.Function, current, root);
                yield return answered;

                self.Give(Truth(answered.Result));
                yield break;

            case JsonPathLogical.Comparison comparison:
                var left = Value(comparison.Left, current, root);
                yield return left;

                var right = Value(comparison.Right, current, root);
                yield return right;

                self.Give(Compare(left.Result, comparison.Operator, right.Result));
                yield break;

            default:
                throw new InvalidOperationException($"A filter's expression is one the parser makes, not {test.GetType().Name}.");
        }
    }

    /// <summary>A comparable's value: a literal, a singular query's one value, or a function's; <see langword="null"/> for nothing.</summary>
    private static Frame<JsonElement?> Value(JsonPathComparable comparable, JsonElement current, JsonElement root)
        => new(self => Valuing(self, comparable, current, root));

    private static IEnumerable<Frame> Valuing(Frame<JsonElement?> self, JsonPathComparable comparable, JsonElement current, JsonElement root)
    {
        switch (comparable)
        {
            case JsonPathComparable.Literal literal:
                self.Give(literal.Value);
                yield break;

            case JsonPathComparable.Query query:
                var selected = Nodes(query.Singular, current, root);
                yield return selected;

                self.Give(selected.Result is [var one] ? one : null);
                yield break;

            case JsonPathComparable.Function function:
                var answered = Function(function.Call, current, root);
                yield return answered;

                self.Give(answered.Result.Value);
                yield break;

            default:
                throw new InvalidOperationException($"A comparable is one the parser makes, not {comparable.GetType().Name}.");
        }
    }

    /// <summary>A function's answer, of its type: a value or nothing, true or false, or a list of values.</summary>
    private static Frame<Answer> Function(JsonPathFunction function, JsonElement current, JsonElement root)
        => new(self => Calling(self, function, current, root));

    private static IEnumerable<Frame> Calling(Frame<Answer> self, JsonPathFunction function, JsonElement current, JsonElement root)
    {
        // Each argument the function takes, answered in turn, as the type the function takes it.
        var given = new List<Answer>();

        foreach (var argument in function.Arguments)
        {
            var answered = Argument(argument, current, root);
            yield return answered;

            given.Add(answered.Result);
        }

        switch (function.Name)
        {
            case "length":
                self.Give(given[0].Value is { } measured
                    ? measured.ValueKind switch
                    {
                        JsonValueKind.String => Answer.Of(Number(CodePoints(measured.GetString()!))),
                        JsonValueKind.Array => Answer.Of(Number(measured.GetArrayLength())),
                        JsonValueKind.Object => Answer.Of(Number(measured.EnumerateObject().Count())),
                        _ => Answer.Nothing,
                    }
                    : Answer.Nothing);
                break;

            case "count":
                self.Give(Answer.Of(Number(given[0].Nodes!.Count)));
                break;

            case "value":
                self.Give(given[0].Nodes is [var single] ? Answer.Of(single) : Answer.Nothing);
                break;

            case "match":
            case "search":
                self.Give(Answer.Logical(
                    given[0].Value is { ValueKind: JsonValueKind.String } subject
                    && given[1].Value is { ValueKind: JsonValueKind.String } expression
                    && IRegexp.Matches(expression.GetString()!, subject.GetString()!, whole: function.Name == "match")));
                break;

            default:
                throw new InvalidOperationException($"'{function.Name}' is a function the parser takes, and is answered here.");
        }
    }

    /// <summary>What a function is given, as the type it takes it: a literal's or a singular query's value, a query's values, or a logical answer.</summary>
    private static Frame<Answer> Argument(JsonPathArgument argument, JsonElement current, JsonElement root)
        => new(self => Arguing(self, argument, current, root));

    private static IEnumerable<Frame> Arguing(Frame<Answer> self, JsonPathArgument argument, JsonElement current, JsonElement root)
    {
        switch (argument)
        {
            case JsonPathArgument.Literal literal:
                self.Give(Answer.Of(literal.Value));
                yield break;

            case JsonPathArgument.Query query:
                var selected = Nodes(query.Of, current, root);
                yield return selected;

                self.Give(Answer.Listing(selected.Result));
                yield break;

            case JsonPathArgument.Logical logical:
                var holds = Holds(logical.Expression, current, root);
                yield return holds;

                self.Give(Answer.Logical(holds.Result));
                yield break;

            case JsonPathArgument.Function function:
                var answered = Function(function.Call, current, root);
                yield return answered;

                self.Give(answered.Result);
                yield break;

            default:
                throw new InvalidOperationException($"An argument is one the parser makes, not {argument.GetType().Name}.");
        }
    }

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

    /// <summary>
    /// Whether two values are equal by the standard: numbers by their value, strings by their characters, arrays item by
    /// item, objects member by member, found by name. Gone down on a stack of its own, every pair of values still to compare
    /// held there, so values of any depth are compared.
    /// </summary>
    private static bool Equal(JsonElement left, JsonElement right)
    {
        var pairs = new Stack<(JsonElement Left, JsonElement Right)>();
        pairs.Push((left, right));

        while (pairs.TryPop(out var pair))
        {
            var (one, other) = pair;

            if (one.ValueKind != other.ValueKind)
            {
                return false;
            }

            switch (one.ValueKind)
            {
                case JsonValueKind.Number when NumberOf(one) != NumberOf(other):
                case JsonValueKind.String when !string.Equals(one.GetString(), other.GetString(), StringComparison.Ordinal):
                    return false;

                case JsonValueKind.Array:
                    if (one.GetArrayLength() != other.GetArrayLength())
                    {
                        return false;
                    }

                    foreach (var (first, second) in one.EnumerateArray().Zip(other.EnumerateArray()))
                    {
                        pairs.Push((first, second));
                    }

                    break;

                case JsonValueKind.Object:
                    if (one.GetPropertyCount() != other.GetPropertyCount())
                    {
                        return false;
                    }

                    // As many members, and each found by its name, never sought among the rest: a name given twice is its last
                    // value's, as a reader of the object takes it.
                    var named = Members(one);
                    var others = Members(other);

                    if (named.Count != others.Count)
                    {
                        return false;
                    }

                    foreach (var (name, value) in named)
                    {
                        if (!others.TryGetValue(name, out var otherValue))
                        {
                            return false;
                        }

                        pairs.Push((value, otherValue));
                    }

                    break;

                // true, false and null: one value each, the kind alone says which; a number or a string the cases above did
                // not refuse is equal.
            }
        }

        return true;
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
