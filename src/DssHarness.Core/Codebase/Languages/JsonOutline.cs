using System.Text;
using System.Text.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A JSON file as a project file's implementation reads it: its values, each with the line it begins on as the core
/// counts lines (<see cref="CodeFileText.Lines"/>) - what <see cref="JsonDocument"/> does not keep - read once, bounded
/// in depth, and the line it stops at where it is no JSON.
/// </summary>
internal static class JsonOutline
{
    /// <summary>What a file that is no object gives, as an extractor's help says it.</summary>
    public const string Unread = "a file that is no JSON object - one that does not parse, nests past the reader's bounds or holds another value: nothing in it is read, the line it stops at marked dynamic";

    /// <summary>How deep a project file's values are read: no project file nests past it.</summary>
    private const int MostDepth = 64;

    /// <summary>Why a file that stops parsing is no object.</summary>
    private const string NoJson = "the file stops reading as JSON here";

    /// <summary>Why a file whose value is another - an array, a string, nothing - is no object.</summary>
    private const string NoObject = "the file holds no JSON object";

    /// <summary>
    /// <paramref name="text"/>'s object - what a project file is - or <see langword="null"/> with <paramref name="stop"/>
    /// saying where and why it is none: the line it stops reading as JSON at, or the line a value that is no object begins
    /// on. Comments and trailing commas are allowed where <paramref name="lenient"/>, as <c>tsconfig.json</c> writes them;
    /// <paramref name="stop"/> is <see langword="null"/> where the whole of it is read.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="lenient">Whether comments and trailing commas are allowed.</param>
    /// <param name="stop">Where and why it is no object, where it is none.</param>
    public static JsonOutlineValue? Load(string text, bool lenient, out JsonOutlineStop? stop)
    {
        var skipped = text.StartsWith('﻿') ? 1 : 0;
        var bytes = Encoding.UTF8.GetBytes(text[skipped..]);
        var positions = new Positions(bytes, CodeFileText.LineIndex(text), skipped);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            CommentHandling = lenient ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
            AllowTrailingCommas = lenient,
            MaxDepth = MostDepth,
        });

        stop = null;

        try
        {
            if (!reader.Read())
            {
                stop = new JsonOutlineStop(1, NoObject);
                return null;
            }

            var value = Value(ref reader, positions);

            if (reader.Read())
            {
                stop = new JsonOutlineStop(positions.LineAt(reader.TokenStartIndex), NoJson);
                return null;
            }

            if (value.Kind != JsonValueKind.Object)
            {
                stop = new JsonOutlineStop(value.Line, NoObject);
                return null;
            }

            return value;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            stop = new JsonOutlineStop(positions.LineAt(Math.Min(reader.TokenStartIndex, Math.Max(bytes.Length - 1, 0))), NoJson);
            return null;
        }
    }

    /// <summary>The value the reader stands at the first token of, read to its last.</summary>
    private static JsonOutlineValue Value(ref Utf8JsonReader reader, Positions positions)
    {
        var line = positions.LineAt(reader.TokenStartIndex);

        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var properties = new List<JsonOutlineProperty>();

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString()!;
                    var at = positions.LineAt(reader.TokenStartIndex);
                    reader.Read();
                    properties.Add(new JsonOutlineProperty(name, at, Value(ref reader, positions)));
                }

                return new JsonOutlineValue(JsonValueKind.Object, line, null, properties, null);

            case JsonTokenType.StartArray:
                var items = new List<JsonOutlineValue>();

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    items.Add(Value(ref reader, positions));
                }

                return new JsonOutlineValue(JsonValueKind.Array, line, null, null, items);

            case JsonTokenType.String:
                return new JsonOutlineValue(JsonValueKind.String, line, reader.GetString(), null, null);

            default:
                return new JsonOutlineValue(reader.TokenType switch
                {
                    JsonTokenType.Number => JsonValueKind.Number,
                    JsonTokenType.True => JsonValueKind.True,
                    JsonTokenType.False => JsonValueKind.False,
                    _ => JsonValueKind.Null,
                }, line, null, null, null);
        }
    }

    /// <summary>
    /// The lines of the text whose bytes a reader reads, as the core counts them (<see cref="CodeFileText.LineIndex"/>): a
    /// byte's position told as the line its character is on, the characters before it counted once as the reader moves on.
    /// </summary>
    /// <param name="bytes">The text's bytes, after the byte order mark it may begin with.</param>
    /// <param name="lines">Where the text's lines begin.</param>
    /// <param name="skipped">The characters the text begins with that are not among <paramref name="bytes"/>: its byte order mark's.</param>
    private sealed class Positions(byte[] bytes, CodeLineIndex lines, int skipped)
    {
        private int _byte;
        private int _character;

        /// <summary>The line, from 1, the character at the byte <paramref name="position"/> begins is on.</summary>
        public int LineAt(long position)
        {
            var at = (int)Math.Clamp(position, 0, bytes.Length);

            if (at < _byte)
            {
                (_byte, _character) = (0, 0);
            }

            _character += Encoding.UTF8.GetCharCount(bytes, _byte, at - _byte);
            _byte = at;
            return lines.LineAt(_character + skipped);
        }
    }
}

/// <summary>Where a JSON file stops being the object a project file is, and why.</summary>
/// <param name="Line">The line, from 1.</param>
/// <param name="Why">Why it is no object there.</param>
internal sealed record JsonOutlineStop(int Line, string Why);

/// <summary>One value of a JSON file, with the line it begins on.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Line">The line it begins on, from 1.</param>
/// <param name="Text">A string's text; <see langword="null"/> for every other kind.</param>
/// <param name="Properties">An object's properties, in the order the file writes them; <see langword="null"/> for every other kind.</param>
/// <param name="Items">An array's items; <see langword="null"/> for every other kind.</param>
internal sealed record JsonOutlineValue(JsonValueKind Kind, int Line, string? Text, IReadOnlyList<JsonOutlineProperty>? Properties, IReadOnlyList<JsonOutlineValue>? Items)
{
    /// <summary>The value of an object's property <paramref name="name"/> - its last, as a JSON parser takes it - where it is an object that has one.</summary>
    public JsonOutlineValue? this[string name] => Properties?.LastOrDefault(property => property.Name == name)?.Value;

    /// <summary>Its text where it is a string that is not empty; <see langword="null"/> otherwise.</summary>
    public string? NonEmpty => Kind == JsonValueKind.String && !string.IsNullOrWhiteSpace(Text) ? Text : null;
}

/// <summary>One property of a JSON object, with the line its name is on.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Line">The line its name is on, from 1.</param>
/// <param name="Value">Its value.</param>
internal sealed record JsonOutlineProperty(string Name, int Line, JsonOutlineValue Value);
