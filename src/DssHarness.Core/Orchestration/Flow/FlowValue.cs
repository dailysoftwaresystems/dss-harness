using System.Globalization;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What a read yields while a run works: a scalar, a list, an object, <see langword="null"/> - or Missing, the value of
/// a name that reads nothing (a step that was skipped, a property an output does not hold), which
/// <c>??</c> and <c>empty(...)</c> are for and no other operator takes.
/// </summary>
/// <remarks>
/// <para>
/// One value shape for every read the engine makes - a <c>when</c>, an <c>until</c>, a <c>forEach</c>, a fact of a step -
/// so that how a value behaves is said once. A step's output is held as it was answered, as JSON; the scalars come out
/// of it as they are read.
/// </para>
/// <para>
/// Missing is a value and not an error: a flow is written to read steps that may not have run, and what such a read
/// yields is nothing, which a template spells and an expression can ask about. An operator that needs something
/// definite - a boolean, a scalar to compare, a list to look into - refuses Missing, as it refuses a value of the wrong
/// kind, by throwing <see cref="UnresolvedReadException"/>: the step that read it failed with 12, and the definition is
/// where the fix is.
/// </para>
/// </remarks>
public readonly record struct FlowValue
{
    /// <summary>The one value that marks a read of nothing: never a value a run holds, only the marker.</summary>
    private sealed class Nothing;

    /// <summary>The marker itself, so the one Missing value and it are the same object.</summary>
    private static readonly Nothing NothingAtAll = new();

    /// <summary>The value of a name that reads nothing.</summary>
    public static FlowValue Missing { get; } = new(NothingAtAll);

    /// <summary>The JSON <see langword="null"/>.</summary>
    public static FlowValue Null { get; } = new(null);

    /// <summary>A value as the run holds it: a scalar, a JSON array or object, or the missing marker.</summary>
    public static FlowValue Of(object? value) => value switch
    {
        null => Null,
        Nothing marker when ReferenceEquals(marker, NothingAtAll) => Missing,
        bool or long or double or string or JsonArray or JsonObject => new FlowValue(value),
        JsonValue scalar when AsScalar(scalar) is { } plain => new FlowValue(plain),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "A value the engine reads is a scalar, a JSON array or a JSON object."),
    };

    private readonly object? _value;

    private FlowValue(object? value)
    {
        _value = value;
    }

    /// <summary>Whether this value is Missing: the value of a name that reads nothing.</summary>
    public bool IsMissing => _value is Nothing;

    /// <summary>Whether this value is the JSON <see langword="null"/>.</summary>
    public bool IsNull => _value is null;

    /// <summary>Whether this value is a scalar one may compare: a boolean, a number or a text, beside null.</summary>
    public bool IsScalar => _value is null or bool or long or double or string;

    /// <summary>Whether this value holds nothing: Missing, null, an empty text, an empty list or an empty object.</summary>
    public bool HoldsNothing => _value switch
    {
        Nothing or null => true,
        string text => text.Length == 0,
        JsonArray list => list.Count == 0,
        JsonObject map => map.Count == 0,
        _ => false,
    };

    /// <summary>
    /// This value as a boolean, where it is one and only where it is one: nothing is read as true or as false but
    /// <see langword="true"/> and <see langword="false"/>, so a value that is neither - Missing and null among them -
    /// refuses the read.
    /// </summary>
    /// <param name="written">The value as the expression wrote it, named in the refusal.</param>
    public bool AsBoolean(string written)
        => _value is bool truth ? truth : throw new UnresolvedReadException($"'{written}' is {InWords()}, and nothing is read as true or as false but true and false");

    /// <summary>
    /// This value as a list, where it is one; anything else - Missing and null among them - refuses the read, for
    /// <c>any(...)</c> looks into a list.
    /// </summary>
    /// <param name="written">The value as the expression wrote it, named in the refusal.</param>
    public JsonArray AsList(string written)
        => _value is JsonArray list ? list : throw new UnresolvedReadException($"'{written}' is {InWords()}, and any looks into a list");

    /// <summary>
    /// This value as a scalar, where it is one or is <see langword="null"/>: a comparison compares two scalars, or null,
    /// and a value that is neither - Missing, a list, an object - refuses the read.
    /// </summary>
    /// <param name="written">The value as the expression wrote it, named in the refusal.</param>
    public object? AsScalar(string written)
        => IsScalar ? _value : throw new UnresolvedReadException($"'{written}' is {InWords()}, and a comparison compares two scalars, or null");

    /// <summary>The value under <paramref name="key"/>, where this is an object that holds it; Missing otherwise.</summary>
    /// <param name="key">A member's name.</param>
    public FlowValue Member(string key)
        => _value is JsonObject map && map.ContainsKey(key) ? Of(map[key]) : Missing;

    /// <summary>The value at <paramref name="index"/>, where this is a list long enough; Missing otherwise.</summary>
    /// <param name="index">A place in the list, from 0.</param>
    public FlowValue At(int index)
        => _value is JsonArray { Count: > 0 } list && index >= 0 && index < list.Count ? Of(list[index]) : Missing;

    /// <summary>
    /// Whether two scalars are one: null equals only null, numbers compare as numbers however each is written, texts
    /// compare Ordinal, and values of different kinds are unequal - never an error, for a comparison of unlike values is
    /// a question, and its answer is no.
    /// </summary>
    /// <param name="left">One value; each is a scalar or null, or the read was refused before this is asked.</param>
    /// <param name="right">The other value.</param>
    public static bool ScalarsEqual(object? left, object? right)
        => (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (long whole, long otherWhole) => whole == otherWhole,
            (double or long, double or long) => Convert.ToDouble(left, CultureInfo.InvariantCulture) == Convert.ToDouble(right, CultureInfo.InvariantCulture),
            (bool truth, bool otherTruth) => truth == otherTruth,
            (string text, string otherText) => string.Equals(text, otherText, StringComparison.Ordinal),
            _ => false,
        };

    /// <summary>The value as JSON, as the run holds it; <see langword="null"/> for Missing.</summary>
    public JsonNode? ToNode()
        => _value switch
        {
            null or Nothing => null,
            JsonNode node => node.DeepClone(),
            bool truth => JsonValue.Create(truth),
            long whole => JsonValue.Create(whole),
            double fraction => JsonValue.Create(fraction),
            string text => JsonValue.Create(text),
            _ => null,
        };

    /// <summary>This value in words, as a refusal names what it was given: <c>a list</c>, <c>a text</c>, <c>nothing</c>.</summary>
    public string InWords() => _value switch
    {
        Nothing => "nothing",
        null => "null",
        bool => "a boolean",
        long or double => "a number",
        string => "a text",
        JsonArray => "a list",
        JsonObject => "an object",
        _ => "a value",
    };

    /// <summary>A JSON value as one of the scalars the engine reads, or <see langword="null"/> where it holds none.</summary>
    private static object? AsScalar(JsonValue scalar)
        => scalar.TryGetValue<bool>(out var truth) ? truth
            : scalar.TryGetValue<long>(out var whole) ? whole
            : scalar.TryGetValue<int>(out var small) ? (long)small
            : scalar.TryGetValue<double>(out var fraction) ? fraction
            : scalar.TryGetValue<string>(out var text) ? text
            : null;

    /// <summary>
    /// This value as a template fills it: a scalar as its text - a number as it is written, a boolean as true or
    /// false, null as <c>null</c> - and a list or an object as JSON.
    /// </summary>
    public string AsTemplateText()
        => _value switch
        {
            null => "null",
            Nothing => "null",
            string text => text,
            bool truth => truth ? "true" : "false",
            long whole => whole.ToString(System.Globalization.CultureInfo.InvariantCulture),
            double fraction => fraction.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonNode node => node.ToJsonString(),
            _ => string.Empty,
        };
}

/// <summary>
/// An expression the run could not read: an operator met a value it does not take - a Missing name where a value was
/// needed, a text where a boolean was - and the step that read it failed with 12. The definition is where the fix is,
/// and the message says the part that could not be read.
/// </summary>
public sealed class UnresolvedReadException(string message) : Exception(message);
