using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Configuration;

/// <summary>
/// A value of <c>config.json</c> written either as one bare string that applies on every platform, or as a map of
/// platform, or <c>all</c>, to the string that applies there.
/// </summary>
/// <remarks>
/// <para>
/// One owner for the shape, so every value that takes it is read, compared, chosen for a platform and written back the
/// same way: a build output (<see cref="BuildOutput"/>) and a set-up's command line (<see cref="SetUpLine"/>) are two
/// meanings of one shape, and a second copy of the reader would drift from the first in what it refuses.
/// </para>
/// <para>
/// Written as a bare string it applies everywhere. Written as a map it is keyed by platform, or by
/// <see cref="PlatformScope.Every"/> for the general case, which a platform's own key overrides.
/// </para>
/// </remarks>
public abstract class PerPlatformText : IEquatable<PerPlatformText>
{
    private readonly Dictionary<string, string> _values;

    /// <summary>A value of the shape, as one string or as a map.</summary>
    /// <param name="values">The strings, keyed by platform name or <see cref="PlatformScope.Every"/>.</param>
    /// <param name="plain">The bare string the value was written as, or <see langword="null"/> for a map.</param>
    /// <remarks>
    /// The comparer is fixed here rather than taken from the caller, so every value answers a platform lookup the same
    /// way however it was built, and two values declaring the same platforms compare equal whichever is asked first.
    /// </remarks>
    protected PerPlatformText(IEnumerable<KeyValuePair<string, string>> values, string? plain)
    {
        ArgumentNullException.ThrowIfNull(values);

        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        Plain = plain;
    }

    /// <summary>
    /// The bare string this value was written as, or <see langword="null"/> when it was written as a map of platform
    /// to string.
    /// </summary>
    /// <remarks>
    /// The string itself rather than a flag saying there is one, so the shape cannot disagree with the content. Held so
    /// the file is written back as it was read: a string applying everywhere would otherwise return as a map, turning
    /// every save into a diff against a file nobody edited.
    /// </remarks>
    public string? Plain { get; }

    /// <summary>The strings this value declares, keyed by platform or <see cref="PlatformScope.Every"/>.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>
    /// The string this value names on <paramref name="platformKey"/>, or <see langword="null"/> when it names none.
    /// </summary>
    /// <param name="platformKey">The platform asked about.</param>
    public string? For(string? platformKey) => PlatformScope.Select(Values, platformKey);

    /// <summary>Whether this value names a string on <paramref name="platformKey"/>.</summary>
    /// <param name="platformKey">The platform asked about.</param>
    public bool Covers(string? platformKey) => PlatformScope.Covers(Values, platformKey);

    /// <summary>
    /// Whether <paramref name="other"/> is a value of the same meaning naming the same strings for the same platforms.
    /// </summary>
    /// <remarks>
    /// Compared by what it declares rather than by reference, so a configuration saved and read back equals the one it
    /// came from. Platform names compare ignoring case, as they do everywhere else; the strings do not. Two meanings of
    /// the shape are never equal, whatever they hold: a build output is not a command line.
    /// </remarks>
    /// <param name="other">The value to compare with.</param>
    public bool Equals(PerPlatformText? other)
        => other is not null
            && other.GetType() == GetType()
            && _values.Count == other._values.Count
            && _values.All(entry
                => other._values.TryGetValue(entry.Key, out var value)
                && string.Equals(entry.Value, value, StringComparison.Ordinal));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PerPlatformText);

    /// <summary>Whether two values declare the same strings for the same platforms.</summary>
    /// <remarks>
    /// Defined because the type has value equality: left out, <c>==</c> would answer by reference while
    /// <see cref="Equals(PerPlatformText)"/> answered by content, and the two would disagree silently at whichever call
    /// site reached for the operator.
    /// </remarks>
    /// <param name="left">One value.</param>
    /// <param name="right">The other.</param>
    public static bool operator ==(PerPlatformText? left, PerPlatformText? right)
        => left is null ? right is null : left.Equals(right);

    /// <summary>Whether two values declare different strings.</summary>
    /// <param name="left">One value.</param>
    /// <param name="right">The other.</param>
    public static bool operator !=(PerPlatformText? left, PerPlatformText? right) => !(left == right);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        // Order-independent, because two maps declaring the same platforms in a different order are the same value and
        // a dictionary does not promise an order to begin with.
        var hash = _values.Count;

        foreach (var (platform, value) in _values)
        {
            hash ^= HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(platform),
                StringComparer.Ordinal.GetHashCode(value));
        }

        return hash;
    }

    /// <summary>The value as a refusal shows it: the bare string, or every platform it names.</summary>
    public override string ToString()
        => Plain ?? string.Join(", ", _values.Select(entry => $"{entry.Key}: {entry.Value}"));
}

/// <summary>
/// The words a <see cref="PerPlatformTextConverter{TText}"/> refuses a malformed value with, so each meaning of the
/// shape is refused in its own terms: <c>A buildOutputs entry is an empty path, which names no file.</c>
/// </summary>
/// <param name="Entry">What one value is called, capitalised, with its article: <c>A buildOutputs entry</c>.</param>
/// <param name="Value">What its string is: <c>path</c>.</param>
/// <param name="Names">What a string names: <c>file</c>.</param>
/// <param name="WhenNoneNamed">Why a map naming nothing is refused, as a clause ending the sentence.</param>
public sealed record PerPlatformWords(string Entry, string Value, string Names, string WhenNoneNamed);

/// <summary>
/// Reads a <see cref="PerPlatformText"/> written either as a bare string or as a map of platform to string, and writes
/// it back in the shape it was read.
/// </summary>
/// <remarks>
/// Read with the caller's reader rather than through <c>JsonSerializer.Deserialize</c>, for the reason
/// <see cref="NonNullListConverter{TItem}"/> and <see cref="CaseInsensitiveDictionaryConverter{TValue}"/> give: a copy
/// scoped to the value reports a problem at a line counted from the value's own start instead of from the top of the
/// file.
/// </remarks>
/// <typeparam name="TText">The meaning of the shape this reads.</typeparam>
public abstract class PerPlatformTextConverter<TText> : JsonConverter<TText>
    where TText : PerPlatformText
{
    /// <summary>The words a malformed value is refused with.</summary>
    protected abstract PerPlatformWords Words { get; }

    /// <summary>A value naming <paramref name="text"/> on every platform.</summary>
    /// <param name="text">The bare string, never blank.</param>
    protected abstract TText Everywhere(string text);

    /// <summary>A value naming one string per platform.</summary>
    /// <param name="values">The strings, keyed by platform, never empty.</param>
    protected abstract TText Keyed(Dictionary<string, string> values);

    /// <inheritdoc />
    public override TText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var words = Words;

        if (reader.TokenType == JsonTokenType.String)
        {
            // Refused here rather than left to mean nothing at all, which no value of this shape can do.
            return reader.GetString() is { } plain && !string.IsNullOrWhiteSpace(plain)
                ? Everywhere(plain)
                : throw new JsonException($"{words.Entry} is an empty {words.Value}, which names no {words.Names}.");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"{words.Entry} is a {words.Value}, or a mapping of platform to {words.Value}, found {reader.TokenType}.");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return values.Count > 0
                    ? Keyed(values)
                    : throw new JsonException($"{words.Entry} names no {words.Value} at all; {words.WhenNoneNamed}");
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Expected a platform name, found {reader.TokenType}.");
            }

            var platform = reader.GetString() ?? string.Empty;

            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"{words.Entry} for '{platform}' is not a {words.Value}.");
            }

            var value = reader.GetString();

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException(
                    $"{words.Entry} for '{platform}' is an empty {words.Value}, which names no {words.Names}.");
            }

            // Refused rather than resolved one way: two spellings of one platform are one key on this dictionary, so
            // the second would silently replace the first.
            if (!values.TryAdd(platform, value))
            {
                throw new JsonException(
                    $"{words.Entry} names platform '{platform}' twice; nothing could say which {words.Value} it meant.");
            }
        }

        throw new JsonException(
            $"Unexpected end of JSON while reading {char.ToLowerInvariant(words.Entry[0])}{words.Entry[1..]}.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TText value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.Plain is { } plain)
        {
            writer.WriteStringValue(plain);
            return;
        }

        writer.WriteStartObject();

        foreach (var (platform, text) in value.Values)
        {
            writer.WriteString(platform, text);
        }

        writer.WriteEndObject();
    }
}
