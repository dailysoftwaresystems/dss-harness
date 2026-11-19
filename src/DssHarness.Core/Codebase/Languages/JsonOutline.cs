using System.Text;
using System.Text.Json;
using DssHarness.Core.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A JSON file as a project file's implementation reads it: its values, each with the line it begins on as the core
/// counts lines (<see cref="CodeFileText.Lines"/>) - what <see cref="JsonDocument"/> does not keep - read once, however
/// deep it nests, on a stack of the reader's own, and the line it stops at where it is no JSON.
/// </summary>
internal static class JsonOutline
{
    /// <summary>What a file that is no object gives, as an extractor's help says it.</summary>
    public const string Unread = "a file that is no JSON object - one that does not parse or holds another value: nothing in it is read, the line it stops at marked dynamic";

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
        var value = LoadAny(text, lenient, out stop);

        if (value is null && stop is null)
        {
            stop = new JsonOutlineStop(1, NoObject);
        }
        else if (value is not null && value.Kind != JsonValueKind.Object)
        {
            stop = new JsonOutlineStop(value.Line, NoObject);
            return null;
        }

        return value;
    }

    /// <summary>
    /// <paramref name="text"/>'s value - whatever kind it holds, an array among them - or <see langword="null"/> with
    /// <paramref name="stop"/> saying where it stops reading as JSON, and why in the reader's own words: what a data file's
    /// reader takes, which no project file's shape is held to. A text that holds no value - empty, or comments and white
    /// space alone - is <see langword="null"/> with no stop: nothing stopped its reading.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="lenient">Whether comments and trailing commas are allowed.</param>
    /// <param name="stop">Where and why it is no JSON, where it is none.</param>
    public static JsonOutlineValue? LoadAny(string text, bool lenient, out JsonOutlineStop? stop)
    {
        var skipped = text.StartsWith('﻿') ? 1 : 0;
        var bytes = Encoding.UTF8.GetBytes(text[skipped..]);
        var positions = new Positions(bytes, CodeFileText.LineIndex(text), skipped);
        // As deep as the file nests (JsonDepth): the values are read on a stack of the reader's own, not the call's.
        var reader = new Utf8JsonReader(bytes, JsonDepth.Reader with
        {
            CommentHandling = lenient ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
            AllowTrailingCommas = lenient,
        });

        stop = null;

        if (HoldsNoValue(text.AsSpan(skipped), lenient))
        {
            return null;
        }

        try
        {
            if (!reader.Read())
            {
                return null;
            }

            var value = Value(ref reader, positions);

            if (reader.Read())
            {
                stop = new JsonOutlineStop(positions.LineAt(reader.TokenStartIndex), NoJson) { Detail = "a second value follows the first" };
                return null;
            }

            return value;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            stop = new JsonOutlineStop(positions.LineAt(Math.Min(reader.TokenStartIndex, Math.Max(bytes.Length - 1, 0))), NoJson) { Detail = Reason(exception.Message) };
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds no value at all: white space alone, and where comments are allowed, comments -
    /// which a reader refuses as holding no token, though nothing in it stops reading as JSON.
    /// </summary>
    private static bool HoldsNoValue(ReadOnlySpan<char> text, bool lenient)
    {
        for (var at = 0; at < text.Length;)
        {
            if (char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            else if (lenient && text[at..].StartsWith("//", StringComparison.Ordinal))
            {
                var end = text[at..].IndexOfAny('\r', '\n');
                at = end < 0 ? text.Length : at + end;
            }
            else if (lenient && text[at..].StartsWith("/*", StringComparison.Ordinal))
            {
                var end = text[(at + 2)..].IndexOf("*/", StringComparison.Ordinal);

                if (end < 0)
                {
                    // A comment never closed stops reading: the reader says where.
                    return false;
                }

                at += end + 4;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A reader's message, as a stop says why: its own words, without the place it counts from 0 in bytes - a stop names
    /// its line as the core counts lines - nor the sentence's end, which the stop's own gives.
    /// </summary>
    private static string Reason(string message)
    {
        var at = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return (at >= 0 ? message[..at] : message).Trim().TrimEnd('.');
    }

    /// <summary>
    /// The value the reader stands at the first token of, read to its last: each object and array it holds opened on a
    /// stack of its own and closed where the reader closes it, so no nesting of the file is recursed through.
    /// </summary>
    private static JsonOutlineValue Value(ref Utf8JsonReader reader, Positions positions)
    {
        // Each object or array open, with the line it begins on, what it holds so far, and the name and line of the property
        // it is the value of in the one that holds it.
        var open = new Stack<Open>();
        JsonOutlineValue? done = null;
        string? name = null;
        var nameLine = 0;

        while (true)
        {
            var line = positions.LineAt(reader.TokenStartIndex);
            JsonOutlineValue? value = null;

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    name = reader.GetString()!;
                    nameLine = line;
                    break;

                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    open.Push(new Open(reader.TokenType == JsonTokenType.StartObject, line, name, nameLine));
                    name = null;
                    break;

                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    var closed = open.Pop();
                    value = closed.Object
                        ? new JsonOutlineValue(JsonValueKind.Object, closed.Line, null, closed.Properties, null) { End = line }
                        : new JsonOutlineValue(JsonValueKind.Array, closed.Line, null, null, closed.Items) { End = line };
                    (name, nameLine) = (closed.Name, closed.NameLine);
                    break;

                case JsonTokenType.String:
                    value = new JsonOutlineValue(JsonValueKind.String, line, reader.GetString(), null, null) { End = line };
                    break;

                default:
                    // The raw text of a number, a true, a false or a nothing, as the file writes it: a data file's key holds
                    // it as its value.
                    var raw = Encoding.UTF8.GetString(reader.ValueSpan);
                    value = new JsonOutlineValue(reader.TokenType switch
                    {
                        JsonTokenType.Number => JsonValueKind.Number,
                        JsonTokenType.True => JsonValueKind.True,
                        JsonTokenType.False => JsonValueKind.False,
                        _ => JsonValueKind.Null,
                    }, line, raw.Length > 0 ? raw : null, null, null) { End = line };
                    break;
            }

            if (value is not null)
            {
                if (!open.TryPeek(out var holder))
                {
                    done = value;
                }
                else if (holder.Object)
                {
                    holder.Properties.Add(new JsonOutlineProperty(name!, nameLine, value));
                }
                else
                {
                    holder.Items.Add(value);
                }

                name = null;
            }

            if (done is not null)
            {
                return done;
            }

            if (!reader.Read())
            {
                // The reader says where a text ends before its value does.
                throw new JsonException("the text ends before its value does");
            }
        }
    }

    /// <summary>An object or an array the reader has opened: what it is, the line it begins on, what it holds so far, and the property of the one that holds it it is the value of.</summary>
    private sealed record Open(bool Object, int Line, string? Name, int NameLine)
    {
        public List<JsonOutlineProperty> Properties { get; } = [];

        public List<JsonOutlineValue> Items { get; } = [];
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
internal sealed record JsonOutlineStop(int Line, string Why)
{
    /// <summary>What the reader found there, in its own words, where it stopped reading the file as JSON; <see langword="null"/> where it read a value that is no object.</summary>
    public string? Detail { get; init; }
}

/// <summary>One value of a JSON file, with the line it begins on.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Line">The line it begins on, from 1.</param>
/// <param name="Text">A scalar's text as the file writes it - a string's, a number's, a true's; <see langword="null"/> for an object and an array.</param>
/// <param name="Properties">An object's properties, in the order the file writes them; <see langword="null"/> for every other kind.</param>
/// <param name="Items">An array's items; <see langword="null"/> for every other kind.</param>
internal sealed record JsonOutlineValue(JsonValueKind Kind, int Line, string? Text, IReadOnlyList<JsonOutlineProperty>? Properties, IReadOnlyList<JsonOutlineValue>? Items)
{
    /// <summary>The line it ends on, from 1: an object's or an array's closing bracket's, a scalar's own.</summary>
    public int End { get; init; }

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
