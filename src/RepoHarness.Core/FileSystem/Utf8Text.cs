using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace RepoHarness.Core.FileSystem;

/// <summary>
/// The one strict reader and writer of UTF-8 text: bytes that form no UTF-8 character are refused, naming the first,
/// never read as U+FFFD in its place.
/// </summary>
/// <remarks>
/// Strictly, because what a reader of a file the user wrote reads is kept or written back: a byte that forms no
/// character, read leniently, is a character nobody wrote, and written back is another byte - the file changed where
/// nobody changed it, with nothing said.
/// </remarks>
public static class Utf8Text
{
    /// <summary>An encoding that raises on bytes forming no UTF-8 character, and on a character no UTF-8 can hold.</summary>
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Whether <paramref name="bytes"/> open with a UTF-8 byte order mark.</summary>
    /// <param name="bytes">The bytes, as a file holds them.</param>
    public static bool OpensWithByteOrderMark(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Encoding.UTF8.Preamble);

    /// <summary>
    /// The text <paramref name="bytes"/> spell as UTF-8, or why they spell none, as what follows the name of their file:
    /// <c>is not UTF-8: the byte at offset 2 (0xE9) does not form a UTF-8 character</c>.
    /// </summary>
    /// <param name="bytes">The bytes to read.</param>
    /// <param name="text">The text, or <see langword="null"/> where the bytes are not UTF-8.</param>
    /// <param name="problem">Why they are not, or <see langword="null"/> where they are.</param>
    /// <param name="offset">Where <paramref name="bytes"/> stand in their file, which the offset a problem names counts from.</param>
    /// <returns>Whether the bytes are UTF-8.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out string? text, [NotNullWhen(false)] out string? problem, int offset = 0)
    {
        try
        {
            text = Strict.GetString(bytes);
            problem = null;

            return true;
        }
        catch (DecoderFallbackException ex)
        {
            text = null;
            problem = string.Create(
                CultureInfo.InvariantCulture,
                $"is not UTF-8: the byte at offset {offset + ex.Index} (0x{ex.BytesUnknown?.FirstOrDefault() ?? 0:X2}) does not form a UTF-8 character");

            return false;
        }
    }

    /// <summary>The UTF-8 bytes of <paramref name="text"/>, with no byte order mark.</summary>
    /// <param name="text">The text.</param>
    /// <exception cref="EncoderFallbackException">The text holds a lone surrogate, which no UTF-8 can hold.</exception>
    public static byte[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Strict.GetBytes(text);
    }
}
