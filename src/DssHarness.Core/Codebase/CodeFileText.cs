using System.Text;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The core's rules for a file's bytes, which hold before any implementation reads it: what is binary, what is a Git LFS
/// pointer, which encodings are read, and how a text is cut into the lines every span counts. The one place each rule
/// is, so the graph, an answer that reads a body fresh and an edit all read a file alike.
/// </summary>
public static class CodeFileText
{
    /// <summary>
    /// How many of a file's first bytes are given as text to a claim's content test: the window a file is binary by a NUL
    /// in (<see cref="BinaryContent.Window"/>), so a test is given what was looked at and nothing more.
    /// </summary>
    public const int ProbeBytes = BinaryContent.Window;

    /// <summary>How a Git LFS pointer begins: a file of a few lines that stands for one git keeps elsewhere.</summary>
    private static readonly byte[] LfsPointer = "version https://git-lfs.github.com/spec/"u8.ToArray();

    /// <summary>The longest a Git LFS pointer is, in bytes: a file that begins like one and is longer is a file of its own.</summary>
    private const int MostLfsPointer = 1024;

    /// <summary>An encoding of UTF-16 that raises on what forms no character, little-endian.</summary>
    private static readonly UnicodeEncoding Utf16Little = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>An encoding of UTF-16 that raises on what forms no character, big-endian.</summary>
    private static readonly UnicodeEncoding Utf16Big = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    /// <summary>
    /// What <paramref name="bytes"/>, a file's whole content, are to the graph: a Git LFS pointer and a file with a NUL in
    /// its first <see cref="ProbeBytes"/> bytes are binary; UTF-16 is read by its mark, and UTF-8 with or without its
    /// mark; anything else is unreadable, with why.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    public static CodeFileDecoding Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(LfsPointer) && bytes.Length <= MostLfsPointer)
        {
            return CodeFileDecoding.Refused(CodeFileStates.Binary, "a Git LFS pointer, whose file git keeps elsewhere");
        }

        // UTF-16 holds a NUL in every character of ASCII, so its mark is read before the NUL that makes a file binary; a
        // UTF-32 mark, which begins as UTF-16's does, is no UTF-16 and falls to the NUL below.
        var utf32 = bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]);

        if (!utf32 && (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])))
        {
            var encoding = bytes[0] == 0xFF ? Utf16Little : Utf16Big;

            try
            {
                return CodeFileDecoding.Read(encoding.GetString(bytes[2..]));
            }
            catch (DecoderFallbackException)
            {
                return CodeFileDecoding.Refused(CodeFileStates.Unreadable, "it begins with UTF-16's mark and is not UTF-16 after it");
            }
        }

        if (BinaryContent.Holds(bytes))
        {
            return CodeFileDecoding.Refused(CodeFileStates.Binary, "a NUL among its first bytes");
        }

        var body = Utf8Text.OpensWithByteOrderMark(bytes) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;

        return Utf8Text.TryDecode(body, out var text, out var problem)
            ? CodeFileDecoding.Read(text)
            : CodeFileDecoding.Refused(CodeFileStates.Unreadable, $"it {problem}, and is not UTF-16 by its mark either");
    }

    /// <summary>
    /// <paramref name="text"/>'s lines, without their endings: cut at each CR LF, LF and CR, a final ending closing the
    /// last line rather than opening another. An empty text is one empty line, so every file has a line 1.
    /// </summary>
    /// <remarks>
    /// Deliberately not the output's set of line breaks (<see cref="Output.TextLines"/>, which also breaks at a form feed,
    /// NEL and the Unicode separators): a span here is a line number as an editor, git and a compiler's diagnostics count
    /// it, and those count CR, LF and CR LF alone - a form feed in a C file is whitespace on its line.
    /// </remarks>
    /// <param name="text">A file's text.</param>
    public static IReadOnlyList<string> Lines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var lines = new List<string>();
        var start = 0;

        for (var at = 0; at < text.Length; at++)
        {
            if (text[at] is '\r' or '\n')
            {
                lines.Add(text[start..at]);

                if (text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n')
                {
                    at++;
                }

                start = at + 1;
            }
        }

        if (start < text.Length || lines.Count == 0)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>The first <see cref="ProbeBytes"/> bytes of a file's text, as a claim's content test is given them.</summary>
    /// <param name="text">The file's decoded text.</param>
    public static string Probe(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Cut by characters as near the bytes as a text can be cut, never inside a surrogate pair.
        var length = Math.Min(text.Length, ProbeBytes);

        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
}

/// <summary>What a file's bytes are to the graph: its text, or the state it is listed with and why.</summary>
/// <param name="Text">The decoded text; <see langword="null"/> where it is not read.</param>
/// <param name="State">The state it is listed with where it is not read (<see cref="CodeFileStates"/>); <see langword="null"/> where it is.</param>
/// <param name="Why">Why it is not read.</param>
public sealed record CodeFileDecoding(string? Text, string? State, string? Why)
{
    /// <summary>A file read as <paramref name="text"/>.</summary>
    internal static CodeFileDecoding Read(string text) => new(text, null, null);

    /// <summary>A file not read, listed as <paramref name="state"/> for <paramref name="why"/>.</summary>
    internal static CodeFileDecoding Refused(string state, string why) => new(null, state, why);
}
