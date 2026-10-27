using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace DssHarness.Core.FileSystem;

/// <summary>The encodings a text file is read in, and written back in: the one it was found in, its mark kept or left out as found.</summary>
public enum TextEncoding
{
    /// <summary>UTF-8, with no mark.</summary>
    Utf8,

    /// <summary>UTF-8, opening with its mark.</summary>
    Utf8WithMark,

    /// <summary>UTF-16, little-endian, opening with its mark.</summary>
    Utf16Little,

    /// <summary>UTF-16, big-endian, opening with its mark.</summary>
    Utf16Big,
}

/// <summary>
/// A file's text, with the encoding it was found in: the one rule for which encodings a text file is read in - UTF-16 by
/// its mark, UTF-8 with or without its mark, binary by a NUL among its first bytes - so the codebase graph, a file an
/// agent views and a file an agent edits read alike, and an edit writes a file back in the encoding, with the mark, it
/// was found in.
/// </summary>
/// <param name="Text">The decoded text, without the mark.</param>
/// <param name="Encoding">The encoding it was found in.</param>
public sealed record EncodedText(string Text, TextEncoding Encoding)
{
    /// <summary>Why a file whose bytes hold a NUL among their first ones is not read as text.</summary>
    public const string BinaryWhy = "a NUL among its first bytes";

    /// <summary>Why a file that opens with UTF-16's mark and is not UTF-16 after it is not read.</summary>
    public const string NotUtf16Why = "it begins with UTF-16's mark and is not UTF-16 after it";

    private static readonly UnicodeEncoding Utf16LittleStrict = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
    private static readonly UnicodeEncoding Utf16BigStrict = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// What <paramref name="bytes"/>, a file's whole content, read as: UTF-16 by its mark, which is read before the NUL that
    /// makes a file binary, since UTF-16 holds a NUL in every character of ASCII - a UTF-32 mark, which begins as UTF-16's
    /// does, is no UTF-16 and falls to that NUL; then binary by a NUL among the first <see cref="BinaryContent.Window"/>
    /// bytes; then UTF-8 with or without its mark; anything else is not read, with why.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    public static EncodedTextReading Read(ReadOnlySpan<byte> bytes)
    {
        var utf32 = bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]);

        if (!utf32 && (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])))
        {
            var little = bytes[0] == 0xFF;

            try
            {
                return EncodedTextReading.Of(new EncodedText(
                    (little ? Utf16LittleStrict : Utf16BigStrict).GetString(bytes[2..]),
                    little ? TextEncoding.Utf16Little : TextEncoding.Utf16Big));
            }
            catch (DecoderFallbackException)
            {
                return EncodedTextReading.Unread(binary: false, NotUtf16Why);
            }
        }

        if (BinaryContent.Holds(bytes))
        {
            return EncodedTextReading.Unread(binary: true, BinaryWhy);
        }

        var marked = Utf8Text.OpensWithByteOrderMark(bytes);
        var body = marked ? bytes[System.Text.Encoding.UTF8.Preamble.Length..] : bytes;

        return Utf8Text.TryDecode(body, out var text, out var problem)
            ? EncodedTextReading.Of(new EncodedText(text, marked ? TextEncoding.Utf8WithMark : TextEncoding.Utf8))
            : EncodedTextReading.Unread(binary: false, $"it {problem}, and is not UTF-16 by its mark either");
    }

    /// <summary><paramref name="text"/> as the bytes of a file in this text's encoding, its mark first where it was found with one.</summary>
    /// <param name="text">The text to write.</param>
    public byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Encoding switch
        {
            TextEncoding.Utf8 => Utf8Text.Encode(text),
            TextEncoding.Utf8WithMark => [.. System.Text.Encoding.UTF8.Preamble, .. Utf8Text.Encode(text)],
            TextEncoding.Utf16Little => [0xFF, 0xFE, .. Utf16LittleStrict.GetBytes(text)],
            TextEncoding.Utf16Big => [0xFE, 0xFF, .. Utf16BigStrict.GetBytes(text)],
            _ => throw new ArgumentOutOfRangeException(nameof(text), Encoding, "no encoding a text is read in"),
        };
    }
}

/// <summary>What a file's bytes read as: its text, or that it is binary or not read, and why - one or the other.</summary>
public sealed record EncodedTextReading
{
    private EncodedTextReading(EncodedText? text, bool binary, string? why)
    {
        Text = text;
        Binary = binary;
        Why = why;
    }

    /// <summary>The text, with its encoding; null where it is not read.</summary>
    public EncodedText? Text { get; }

    /// <summary>Whether it is not read for being binary.</summary>
    public bool Binary { get; }

    /// <summary>Why it is not read; null where it is.</summary>
    public string? Why { get; }

    /// <summary>Whether it was read as text: its text there, else why not.</summary>
    [MemberNotNullWhen(true, nameof(Text))]
    [MemberNotNullWhen(false, nameof(Why))]
    public bool IsRead => Text is not null;

    /// <summary>Bytes read as <paramref name="text"/>.</summary>
    internal static EncodedTextReading Of(EncodedText text) => new(text, false, null);

    /// <summary>Bytes not read, for <paramref name="why"/>.</summary>
    internal static EncodedTextReading Unread(bool binary, string why) => new(null, binary, why);
}
