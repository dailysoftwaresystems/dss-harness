using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Codebase;

/// <summary>
/// A name as the graph's records keep it: whole, or as the characters it begins with of a name the record holds before
/// it - its parent's, the node that holds it - and the rest. A node nested any depth names its whole path, so a record that
/// wrote each name whole would grow with the square of the depth; kept so, each name costs what its own part adds, and the
/// whole is made again where the record is read. The one owner of how a record writes a name against another and reads it
/// back (<see cref="Against(string, ReadOnlySpan{ValueTuple{string, string}})"/>, <see cref="Text"/>).
/// </summary>
/// <remarks>
/// Written as a JSON string where it is whole; as <c>[kept, "rest"]</c> where it begins with the same field of its base;
/// as <c>["field", kept, "rest"]</c> where it begins with another of its base's names; as <c>[line, kept, "rest"]</c> where
/// its base is the record's line of that place. A name is written against a base only where that is shorter than the name
/// whole, and never between the two halves of a surrogate pair.
/// </remarks>
/// <param name="Rest">What follows the characters kept: the whole name where none is.</param>
/// <param name="Kept">How many of the base's first characters the name begins with; 0 where it is whole.</param>
/// <param name="Field">Which of the base's names it begins with, where that is not the field it is itself; <see langword="null"/> for the same field.</param>
/// <param name="At">The place, among the record's lines, of the one it begins with the name of, where the base is a line; <see langword="null"/> where it is not.</param>
[JsonConverter(typeof(StoredNameJson))]
internal readonly record struct StoredName(string Rest, int Kept = 0, string? Field = null, int? At = null)
{
    /// <summary>
    /// <paramref name="name"/> written against the base among <paramref name="bases"/> it shares the most first characters
    /// with - each a field of the base and that field's name, the first the name's own field - or whole, where none shares
    /// enough to be shorter written so.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="bases">The base's names, each with the field it is; a name the base does not have as <see langword="null"/>.</param>
    public static StoredName Against(string name, ReadOnlySpan<(string Field, string? Name)> bases)
    {
        ArgumentNullException.ThrowIfNull(name);

        var best = new StoredName(name);
        var saved = 0;

        for (var at = 0; at < bases.Length; at++)
        {
            if (bases[at].Name is not { } other)
            {
                continue;
            }

            var kept = SharedLength(name, other);
            var field = at == 0 ? null : bases[at].Field;

            // What writing it against the base costs past its rest: the kept count and the brackets around them, and the
            // field it begins with where that is not its own.
            var cost = Digits(kept) + 3 + (field is null ? 0 : field.Length + 3);

            if (kept - cost > saved)
            {
                (best, saved) = (new StoredName(name[kept..], kept, field), kept - cost);
            }
        }

        return best;
    }

    /// <summary><paramref name="name"/> written against the name of the record's line at <paramref name="at"/>, <paramref name="other"/>, or whole where that is no shorter.</summary>
    /// <param name="name">The name.</param>
    /// <param name="at">The place of the line it is written against.</param>
    /// <param name="other">That line's name.</param>
    public static StoredName Against(string name, int at, string other)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(other);

        var kept = SharedLength(name, other);
        return kept > Digits(kept) + Digits(at) + 4 ? new StoredName(name[kept..], kept, At: at) : new StoredName(name);
    }

    /// <summary>
    /// The name, made again from the base it is written against - <paramref name="base"/>, the base's name it begins with -
    /// the very base where it is all of it, so a name that is its parent's holds no second copy of it.
    /// </summary>
    /// <param name="base">The base's name it begins with; none where the base has no such name.</param>
    /// <exception cref="JsonException">It keeps more characters than the base has, or keeps some of no base.</exception>
    public string Text(string? @base)
    {
        if (Kept == 0)
        {
            return Rest;
        }

        if (@base is null || Kept > @base.Length)
        {
            throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"a name keeps {Kept} characters of a name {(@base is null ? "that is not there" : $"of {@base.Length}")}"));
        }

        return Rest.Length == 0 && Kept == @base.Length ? @base : string.Concat(@base.AsSpan(0, Kept), Rest);
    }

    /// <summary>
    /// Whether the name at <paramref name="at"/>, <paramref name="name"/>, may be written against <paramref name="other"/>,
    /// the name at <paramref name="under"/>, where what a record relates its names by may come round - a scope a parent of
    /// its parent, a node held by what it holds: the other is shorter, or as long and before it, so no two names are ever
    /// written against each other however the record's names relate, and every name reads back.
    /// </summary>
    /// <param name="other">The name it would be written against.</param>
    /// <param name="under">That name's place among the record's.</param>
    /// <param name="name">The name.</param>
    /// <param name="at">Its place.</param>
    public static bool Precedes(string other, int under, string name, int at)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(name);
        return other.Length < name.Length || (other.Length == name.Length && under < at);
    }

    /// <summary>How many first characters <paramref name="name"/> shares with <paramref name="other"/>, never parting a surrogate pair.</summary>
    private static int SharedLength(string name, string other)
    {
        var kept = name.AsSpan().CommonPrefixLength(other);
        return kept > 0 && char.IsHighSurrogate(name[kept - 1]) ? kept - 1 : kept;
    }

    private static int Digits(int value)
    {
        var digits = 1;

        for (; value >= 10; value /= 10)
        {
            digits++;
        }

        return digits;
    }

    /// <summary>
    /// Makes each of <paramref name="count"/> items after the item it is written against, where it is written against one:
    /// in an order where every item's base is made before it - the bases followed on a stack of their own, however long
    /// their chain, and each item made once.
    /// </summary>
    /// <param name="count">How many items there are.</param>
    /// <param name="baseOf">The place of the item an item is written against, or none.</param>
    /// <param name="make">Makes the item at a place, once its base is made.</param>
    /// <param name="what">What an item is, in the words a refusal names it by.</param>
    /// <exception cref="JsonException">An item's base is no item, or the bases come round to an item again.</exception>
    public static void InBaseOrder(int count, Func<int, int?> baseOf, Action<int> make, string what)
    {
        ArgumentNullException.ThrowIfNull(baseOf);
        ArgumentNullException.ThrowIfNull(make);

        // 1 while an item waits on its base, 2 once it is made.
        var state = new byte[count];
        var waiting = new Stack<int>();

        for (var start = 0; start < count; start++)
        {
            if (state[start] == 2)
            {
                continue;
            }

            waiting.Push(start);
            state[start] = 1;

            while (waiting.TryPeek(out var at))
            {
                if (baseOf(at) is { } under && (under < 0 || under >= count))
                {
                    throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"{what} {at} is written against {what} {under}, and there are {count}"));
                }

                if (baseOf(at) is { } next && state[next] != 2)
                {
                    if (state[next] == 1)
                    {
                        throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"{what} {at} is written against {what} {next}, which is written against it in turn"));
                    }

                    state[next] = 1;
                    waiting.Push(next);
                    continue;
                }

                make(at);
                state[at] = 2;
                waiting.Pop();
            }
        }
    }
}

/// <summary>Reads and writes a <see cref="StoredName"/> in the forms it is written in.</summary>
internal sealed class StoredNameJson : JsonConverter<StoredName>
{
    /// <inheritdoc/>
    public override StoredName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new StoredName(reader.GetString()!);
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("a name is neither a string nor the list of what it keeps of another and the rest");
        }

        string? field = null;
        int? at = null;
        Read(ref reader);

        if (reader.TokenType == JsonTokenType.String)
        {
            field = reader.GetString();
            Read(ref reader);
        }

        var first = Number(ref reader);
        Read(ref reader);

        if (reader.TokenType == JsonTokenType.Number)
        {
            if (field is not null)
            {
                throw new JsonException("a name keeps characters of another field and of a line at once");
            }

            at = first;
            first = Number(ref reader);
            Read(ref reader);
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("a name written against another holds no rest");
        }

        var rest = reader.GetString()!;
        Read(ref reader);

        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException("a name written against another holds more than what it keeps and the rest");
        }

        if (first < 1 || at < 0)
        {
            throw new JsonException("a name written against another keeps none of it, or names a line before the first");
        }

        return new StoredName(rest, first, field, at);
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, StoredName value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value.Kept == 0)
        {
            writer.WriteStringValue(value.Rest);
            return;
        }

        writer.WriteStartArray();

        if (value.Field is { } field)
        {
            writer.WriteStringValue(field);
        }
        else if (value.At is { } at)
        {
            writer.WriteNumberValue(at);
        }

        writer.WriteNumberValue(value.Kept);
        writer.WriteStringValue(value.Rest);
        writer.WriteEndArray();
    }

    private static void Read(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException("a name ends before its list does");
        }
    }

    private static int Number(ref Utf8JsonReader reader)
        => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) ? number : throw new JsonException("a name written against another keeps no count of characters");
}
