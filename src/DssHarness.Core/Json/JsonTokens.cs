using System.Text.Json;
using System.Text.Json.Nodes;

namespace DssHarness.Core.Json;

/// <summary>
/// A JSON document read from a stream a token at a time, at any depth (<see cref="JsonDepth.Reader"/>), never held whole:
/// the bytes held are the token being read and, where a reader takes one value whole (<see cref="TakeNode"/>) - one result
/// of a large log - that value's alone. A document a stop cut short gives every token before the break, and the break
/// is thrown as the reader's own <see cref="JsonException"/>.
/// </summary>
/// <param name="stream">The UTF-8 JSON, at the document's start; a byte order mark at its start is no part of it. Left open: its caller owns it.</param>
public sealed class JsonTokens(Stream stream)
{
    /// <summary>The bytes read at a time, and the room a buffer starts with: it grows only to hold one value taken whole.</summary>
    private const int Block = 16 << 10;

    private byte[] _buffer = new byte[Block];
    private int _start;
    private int _end;
    private int _held = -1;
    private bool _final;
    private bool _begun;
    private JsonReaderState _state = new(JsonDepth.Reader);
    private string? _text;
    private int _tokenStart;

    /// <summary>The kind of the token read last.</summary>
    public JsonTokenType TokenType { get; private set; } = JsonTokenType.None;

    /// <summary>How deep the token read last stands: 0 for the document's own value, 1 for its members or items, and so on.</summary>
    public int CurrentDepth { get; private set; }

    /// <summary>Reads the next token; false where the document has ended.</summary>
    /// <exception cref="JsonException">The text is no JSON from here on, or ends before its document does.</exception>
    public bool Read()
    {
        while (true)
        {
            var reader = new Utf8JsonReader(_buffer.AsSpan(_start, _end - _start), _final, _state);

            if (reader.Read())
            {
                TokenType = reader.TokenType;
                CurrentDepth = reader.CurrentDepth;
                _tokenStart = _start + (int)reader.TokenStartIndex;
                _text = TokenType is JsonTokenType.PropertyName or JsonTokenType.String ? reader.GetString() : null;
                _start += (int)reader.BytesConsumed;
                _state = reader.CurrentState;
                return true;
            }

            if (_final)
            {
                return false;
            }

            Fill();
        }
    }

    /// <summary>The text of the token read last, where it is a member's name or a string; null for any other.</summary>
    public string? GetString() => _text;

    /// <summary>
    /// Reads past the value whose first token was read last: an object's or an array's every token to its end, a single
    /// token's nothing. Nothing of it is held.
    /// </summary>
    public void Skip()
    {
        if (TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
        {
            return;
        }

        var depth = CurrentDepth;

        while (Read())
        {
            if (CurrentDepth == depth && TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The value whose first token was read last, taken whole as a node read at any depth (<see cref="JsonDepth.ParseUtf8"/>),
    /// the reader left past it: its bytes alone held while it is read.
    /// </summary>
    /// <exception cref="JsonException">The value is no JSON, or the text ends inside it.</exception>
    public JsonNode? TakeNode()
    {
        var from = _tokenStart;

        if (TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            _held = from;

            try
            {
                Skip();
            }
            finally
            {
                from = _held;
                _held = -1;
            }
        }

        return JsonDepth.ParseUtf8(_buffer.AsSpan(from, _start - from));
    }

    /// <summary>
    /// Reads more of the stream behind what is still wanted - the value being taken whole from its start, else the token
    /// not yet read - moving it to the buffer's start, and the buffer grown where that leaves no room.
    /// </summary>
    private void Fill()
    {
        var keep = _held >= 0 ? _held : _start;

        if (keep > 0)
        {
            Array.Copy(_buffer, keep, _buffer, 0, _end - keep);
            _start -= keep;
            _end -= keep;
            _tokenStart -= keep;

            if (_held >= 0)
            {
                _held -= keep;
            }
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = stream.Read(_buffer, _end, _buffer.Length - _end);

        if (!_begun)
        {
            _begun = true;

            // A byte order mark ahead of the document is the file's, never the JSON's.
            if (read >= 3 && _buffer[_end] == 0xEF && _buffer[_end + 1] == 0xBB && _buffer[_end + 2] == 0xBF)
            {
                _start += 3;
            }
        }

        _end += read;
        _final = read == 0;
    }
}
