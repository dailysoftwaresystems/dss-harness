using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using DssHarness.Core.Json;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A JSON file as a project file's implementation reads it: its values, each with the line it begins on as the core
/// counts lines (<see cref="CodeFileText.Lines"/>) - what <see cref="JsonDocument"/> does not keep - read once, however
/// deep it nests, on a stack of the reader's own, and the line it stops at where it is no JSON. The text is read as its
/// UTF-8 bytes a block at a time (<see cref="CodeText.Utf8"/>), never all at once: what the read holds is the values it
/// keeps.
/// </summary>
internal static class JsonOutline
{
    /// <summary>What a file that is no object gives, as an extractor's help says it, under its short name.</summary>
    public static CodeGraphLimit Unread { get; } = new("non-object-files", "a file that is no JSON object - one that does not parse or holds another value: nothing in it is read, the line it stops at marked dynamic");

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
    public static JsonOutlineValue? Load(CodeText text, bool lenient, out JsonOutlineStop? stop)
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
    public static JsonOutlineValue? LoadAny(CodeText text, bool lenient, out JsonOutlineStop? stop)
    {
        ArgumentNullException.ThrowIfNull(text);

        var skipped = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        var lines = text.LineIndex;
        stop = null;

        if (HoldsNoValue(text, skipped, lenient))
        {
            return null;
        }

        // As deep as the file nests (JsonDepth): the values are read on a stack of the reader's own, not the call's.
        using var tokens = new Tokens(text.Utf8(skipped), JsonDepth.Reader with
        {
            CommentHandling = lenient ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
            AllowTrailingCommas = lenient,
        });

        try
        {
            if (!tokens.Read())
            {
                return null;
            }

            var value = Value(tokens, lines, skipped);

            if (tokens.Read())
            {
                stop = new JsonOutlineStop(lines.LineAt(tokens.Character + skipped), NoJson) { Detail = "a second value follows the first" };
                return null;
            }

            return value;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            stop = new JsonOutlineStop(lines.LineAt(tokens.Stopped + skipped), NoJson) { Detail = Reason(exception.Message) };
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="text"/> from <paramref name="from"/> on holds no value at all: white space alone, and where
    /// comments are allowed, comments - which a reader refuses as holding no token, though nothing in it stops reading as
    /// JSON.
    /// </summary>
    private static bool HoldsNoValue(CodeText text, long from, bool lenient)
    {
        for (var at = from; at < text.Length;)
        {
            if (char.IsWhiteSpace(text[at]))
            {
                at++;
            }
            else if (lenient && text.At(at, "//"))
            {
                var end = text.FindAny("\r\n", at, text.Length);
                at = end < 0 ? text.Length : end;
            }
            else if (lenient && text.At(at, "/*"))
            {
                var end = text.IndexOf("*/", at + 2);

                if (end < 0)
                {
                    // A comment never closed stops reading: the reader says where.
                    return false;
                }

                at = end + 2;
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
    /// The value the tokens stand at the first token of, read to its last: each object and array it holds opened on a
    /// stack of its own and closed where the reader closes it, so no nesting of the file is recursed through.
    /// </summary>
    private static JsonOutlineValue Value(Tokens tokens, CodeLineIndex lines, int skipped)
    {
        // Each object or array open, with the line it begins on, what it holds so far, and the name and line of the property
        // it is the value of in the one that holds it.
        var open = new Stack<Open>();
        JsonOutlineValue? done = null;
        string? name = null;
        var nameLine = 0;

        while (true)
        {
            var line = lines.LineAt(tokens.Character + skipped);
            JsonOutlineValue? value = null;

            switch (tokens.Type)
            {
                case JsonTokenType.PropertyName:
                    name = tokens.Text!;
                    nameLine = line;
                    break;

                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    open.Push(new Open(tokens.Type == JsonTokenType.StartObject, line, name, nameLine));
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
                    value = new JsonOutlineValue(JsonValueKind.String, line, tokens.Text, null, null) { End = line };
                    break;

                default:
                    // The raw text of a number, a true, a false or a nothing, as the file writes it: a data file's key holds
                    // it as its value.
                    value = new JsonOutlineValue(tokens.Type switch
                    {
                        JsonTokenType.Number => JsonValueKind.Number,
                        JsonTokenType.True => JsonValueKind.True,
                        JsonTokenType.False => JsonValueKind.False,
                        _ => JsonValueKind.Null,
                    }, line, tokens.Text is { Length: > 0 } raw ? raw : null, null, null) { End = line };
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

            if (!tokens.Read())
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
    /// The tokens of a text's UTF-8 bytes as <see cref="Utf8JsonReader"/> reads them, the bytes read from a stream a block
    /// at a time - the reader's state carried from one block to the next, as its documentation reads a stream - so no more
    /// of the text is held than a block and the token it may cut short, which a block twice as long is read for. Each token
    /// is told with the character it begins at, counted once as the reader moves on; where the reader stops, its reason is
    /// given after the tokens before it, with the character it stopped at.
    /// </summary>
    /// <param name="bytes">The text's bytes, after the byte order mark it may begin with.</param>
    /// <param name="options">How the reader reads.</param>
    private sealed class Tokens(Stream bytes, JsonReaderOptions options) : IDisposable
    {
        /// <summary>How many bytes a block holds first.</summary>
        private const int BlockBytes = 16 * 1024;

        private readonly Queue<(JsonTokenType Type, long Character, string? Text)> _read = new();
        private byte[] _buffer = new byte[BlockBytes];
        private JsonReaderState _state = new(options);

        // The byte of the stream the buffer begins at, where its bytes the reader has yet to consume begin, and how many it holds.
        private long _base;
        private int _from;
        private int _held;
        private bool _final;
        private bool _ended;

        // The byte the last token begins at - the buffer keeps it, so a stop before the next is counted from it - and its character.
        private long _countedByte;
        private long _countedCharacter;

        private ExceptionDispatchInfo? _failed;
        private long _failedAt;

        /// <summary>What the token read is.</summary>
        public JsonTokenType Type { get; private set; }

        /// <summary>A name's and a string's value; a number's, a true's, a false's and a nothing's raw text; an object's and an array's bracket none.</summary>
        public string? Text { get; private set; }

        /// <summary>The character the token read begins at, after the byte order mark.</summary>
        public long Character { get; private set; }

        /// <summary>The character the reading stands at: the last token's, or where the reader stopped.</summary>
        public long Stopped { get; private set; }

        /// <summary>The next token, read into <see cref="Type"/>, <see cref="Text"/> and <see cref="Character"/>; <see langword="false"/> where the text holds no more.</summary>
        public bool Read()
        {
            (JsonTokenType Type, long Character, string? Text) token;

            while (!_read.TryDequeue(out token))
            {
                if (_failed is not null)
                {
                    Stopped = _failedAt;
                    _failed.Throw();
                }

                if (_ended)
                {
                    return false;
                }

                Fill();
            }

            (Type, Character, Text) = token;
            Stopped = Character;
            return true;
        }

        /// <inheritdoc/>
        public void Dispose() => bytes.Dispose();

        /// <summary>
        /// The tokens of the next block: the bytes before the last token's start let go, the buffer twice as long where what
        /// it holds is one token it cut short, filled from the stream and read from the reader's state on.
        /// </summary>
        private void Fill()
        {
            var keep = (int)Math.Min(_from, _countedByte - _base);

            if (keep > 0)
            {
                Buffer.BlockCopy(_buffer, keep, _buffer, 0, _held - keep);
                (_base, _from, _held) = (_base + keep, _from - keep, _held - keep);
            }

            if (_held == _buffer.Length)
            {
                Array.Resize(ref _buffer, _buffer.Length * 2);
            }

            while (!_final && _held < _buffer.Length)
            {
                var read = bytes.Read(_buffer, _held, _buffer.Length - _held);
                _final = read == 0;
                _held += read;
            }

            var reader = new Utf8JsonReader(_buffer.AsSpan(_from, _held - _from), _final, _state);
            var any = false;

            try
            {
                while (reader.Read())
                {
                    any = true;
                    var character = CharacterAt(_from + (int)reader.TokenStartIndex);
                    var text = reader.TokenType switch
                    {
                        JsonTokenType.PropertyName or JsonTokenType.String => reader.GetString(),
                        JsonTokenType.StartObject or JsonTokenType.EndObject or JsonTokenType.StartArray or JsonTokenType.EndArray => null,
                        _ => Encoding.UTF8.GetString(reader.ValueSpan),
                    };

                    _read.Enqueue((reader.TokenType, character, text));
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
            {
                // Where the reader stopped: the token it began, or the last one it read where it stopped before beginning
                // one in this block - the place a single reader of the whole text would say.
                var at = any || reader.TokenStartIndex > 0 ? _from + (int)reader.TokenStartIndex : (int)(_countedByte - _base);
                _failedAt = CharacterAt(Math.Min(at, Math.Max(_held - 1, 0)));
                _failed = ExceptionDispatchInfo.Capture(exception);
                return;
            }

            _from += (int)reader.BytesConsumed;
            _state = reader.CurrentState;
            _ended = _final;
        }

        /// <summary>The character the byte at <paramref name="at"/> in the buffer begins, the bytes since the last counted once.</summary>
        private long CharacterAt(int at)
        {
            var counted = (int)(_countedByte - _base);

            if (at > counted)
            {
                _countedCharacter += Encoding.UTF8.GetCharCount(_buffer, counted, at - counted);
                _countedByte = _base + at;
            }

            return _countedCharacter;
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
