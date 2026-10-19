using System.Text.Json;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Configuration;

/// <summary>
/// Deserializes every list of reference-type items in the configuration and in the state files, refusing a
/// <c>null</c> item: a member declared a <see cref="List{T}"/> or any list interface the serializer reads into one -
/// <see cref="IReadOnlyList{T}"/>, <see cref="IReadOnlyCollection{T}"/>, <see cref="IList{T}"/>,
/// <see cref="ICollection{T}"/> and <see cref="IEnumerable{T}"/>.
/// </summary>
/// <remarks>
/// The models declare their items non-nullable and the rest of the harness relies on it.
/// System.Text.Json enforces nullable annotations on properties but not on the items of
/// a collection, so <c>"legSets": { "gate": [null] }</c> would load and then fail far from
/// the file, as a crash in whatever first read the list, instead of as a configuration
/// error that names the line. Every shape of list is read here, not the concrete one alone: a
/// record declares its lists read-only, and a list the refusal did not reach would read a
/// <c>null</c> item back from a file the tool wrote.
/// </remarks>
public sealed class NonNullListConverter : JsonConverterFactory
{
    /// <summary>The lists read here: <see cref="List{T}"/>, and each list interface the serializer reads into one.</summary>
    private static readonly IReadOnlyList<Type> Lists =
        [typeof(List<>), typeof(IReadOnlyList<>), typeof(IReadOnlyCollection<>), typeof(IList<>), typeof(ICollection<>), typeof(IEnumerable<>)];

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return typeToConvert.IsGenericType
            && Lists.Contains(typeToConvert.GetGenericTypeDefinition())
            && !typeToConvert.GetGenericArguments()[0].IsValueType;
    }

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);

        return (JsonConverter)Activator.CreateInstance(
            typeof(NonNullListConverter<,>).MakeGenericType(typeToConvert, typeToConvert.GetGenericArguments()[0]))!;
    }
}

/// <summary>Reads and writes one list, refusing null items.</summary>
/// <typeparam name="TList">The list's declared type: a <see cref="List{T}"/>, or an interface one answers to.</typeparam>
/// <typeparam name="TItem">The list's item type.</typeparam>
public sealed class NonNullListConverter<TList, TItem> : JsonConverter<TList>
    where TList : IEnumerable<TItem>
    where TItem : class
{
    /// <inheritdoc />
    public override TList Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array, found {reader.TokenType}.");
        }

        // Read with this reader for the reason CaseInsensitiveDictionaryConverter gives: a
        // copy scoped to the item would report problems at lines counted from the item.
        var itemConverter = (JsonConverter<TItem>)options.GetConverter(typeof(TItem));
        var result = new List<TItem>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return (TList)(IEnumerable<TItem>)result;
            }

            var item = reader.TokenType == JsonTokenType.Null
                ? null
                : itemConverter.Read(ref reader, typeof(TItem), options);

            result.Add(item ?? throw new JsonException(
                $"Item {result.Count} of a list is null; a list in this file cannot contain null."));
        }

        throw new JsonException("Unexpected end of JSON while reading an array.");
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        TList value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartArray();

        foreach (var item in value)
        {
            JsonSerializer.Serialize(writer, item, options);
        }

        writer.WriteEndArray();
    }
}
