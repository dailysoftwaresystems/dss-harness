using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// One argument of the command a run request names: text, or bytes that cross as their base64 text. The host reads every
/// argument as text, so the command there reads a file's content as it always did.
/// </summary>
/// <remarks>
/// Bytes are encoded into the request as the request is written (<see cref="HostAgentProtocol.Input"/>), a piece at a time,
/// so a file a sync carries is never held as text on the machine that carries it. Held as text, a batch's files crossed as
/// their base64 inside the batch's JSON inside the request's, and escaping the one inside the other had the serializer rent
/// a buffer six times as long, which the shared pool then kept: a consumer's first sync of 85 MiB left 3.2 GiB held.
/// </remarks>
[JsonConverter(typeof(HostArgumentConverter))]
public sealed record HostArgument
{
    private readonly string? _text;
    private readonly ReadOnlyMemory<byte> _bytes;

    private HostArgument(string? text, ReadOnlyMemory<byte> bytes)
    {
        _text = text;
        _bytes = bytes;
    }

    /// <summary><paramref name="text"/>, as the command reads it.</summary>
    /// <param name="text">The argument.</param>
    public static HostArgument Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new(text, default);
    }

    /// <summary><paramref name="bytes"/>, which the command reads as their base64 text.</summary>
    /// <param name="bytes">What the argument carries: a file's content.</param>
    public static HostArgument Carrying(ReadOnlyMemory<byte> bytes) => new(null, bytes);

    /// <summary><paramref name="text"/>, as the command reads it.</summary>
    /// <param name="text">The argument.</param>
    public static implicit operator HostArgument(string text) => Of(text);

    /// <summary>
    /// The argument as the command reads it: its text, or the base64 text of what it carries, which is made here, whole,
    /// and so is for a host, which has every argument as text already, and for tests.
    /// </summary>
    public string Text => _text ?? Convert.ToBase64String(_bytes.Span);

    /// <summary>Writes the argument as one JSON string, what it carries encoded a piece at a time.</summary>
    /// <param name="writer">Where the request is being written.</param>
    internal void WriteTo(Utf8JsonWriter writer)
    {
        if (_text is not null)
        {
            writer.WriteStringValue(_text);
            return;
        }

        var bytes = _bytes.Span;

        if (bytes.IsEmpty)
        {
            writer.WriteBase64StringValue(bytes);
            return;
        }

        for (var at = 0; at < bytes.Length; at += HostAgentProtocol.CarriedPiece)
        {
            var piece = bytes.Slice(at, Math.Min(HostAgentProtocol.CarriedPiece, bytes.Length - at));
            writer.WriteBase64StringSegment(piece, isFinalSegment: at + piece.Length >= bytes.Length);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => Text;
}

/// <summary>Reads an argument as the text it is, and writes one as <see cref="HostArgument.WriteTo"/> does.</summary>
public sealed class HostArgumentConverter : JsonConverter<HostArgument>
{
    /// <inheritdoc/>
    public override HostArgument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? HostArgument.Of(reader.GetString()!)
            : throw new JsonException($"An argument is a string, and {reader.TokenType} was found.");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, HostArgument value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        value.WriteTo(writer);
    }
}
