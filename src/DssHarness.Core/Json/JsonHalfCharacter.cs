using System.Text;
using System.Text.Json;

namespace DssHarness.Core.Json;

/// <summary>
/// The one test of JSON that spells half a character: an escape of a lone surrogate, such as <c>\ud800</c> with no
/// <c>\udc00</c> to <c>\udfff</c> after it. JSON allows the escape and no text can hold what it names, so a parser takes
/// the document and every reader of the string then fails in words of its own - a configuration file and an answer from
/// the web alike are held to it before they are read.
/// </summary>
public static class JsonHalfCharacter
{
    /// <summary>
    /// The first key or value of <paramref name="json"/> that spells half a character, as it is written there, with the
    /// line it is on; <see langword="null"/> where every one is text, or <paramref name="json"/> is no JSON at all, which
    /// its parser then says as it says any text it cannot read.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="options">How it is read: comments and trailing commas where its reader allows them.</param>
    public static (int Line, string Spelt)? Find(string json, JsonReaderOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(json);

        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, options);

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.String))
                {
                    continue;
                }

                try
                {
                    _ = reader.GetString();
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    return (bytes.AsSpan(0, (int)reader.TokenStartIndex).Count((byte)'\n') + 1, Encoding.UTF8.GetString(reader.ValueSpan));
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
