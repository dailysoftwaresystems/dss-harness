namespace DssHarness.Core.FileSystem;

/// <summary>
/// What makes a file's content binary, as git itself has it - two rules, each asked here by every reader that follows it,
/// so no file is text to one such reader and binary to another. <see cref="Holds(ReadOnlySpan{byte})"/> is git's
/// reading rule (<c>buffer_is_binary</c>, what diff and grep skip): a NUL among the first <see cref="Window"/> bytes - the
/// anchor citation scan and the codebase graph ask it. <see cref="ForConversion"/> is git's conversion rule
/// (<c>convert_is_binary</c>, what <c>text=auto</c> leaves as it is): the line-ending rewrite asks it, since rewriting what
/// git calls binary would corrupt it.
/// </summary>
public static class BinaryContent
{
    /// <summary>How many of a file's first bytes the reading rule looks at for a NUL: git's own window.</summary>
    public const int Window = 8000;

    /// <summary>Whether <paramref name="content"/>, a file's bytes, is binary by git's reading rule.</summary>
    /// <param name="content">The file's bytes, or its first ones.</param>
    public static bool Holds(ReadOnlySpan<byte> content) => content[..Math.Min(content.Length, Window)].IndexOf((byte)0) >= 0;

    /// <summary>Whether <paramref name="text"/>, a file's decoded text, is binary by git's reading rule: a NUL among its first characters.</summary>
    /// <param name="text">The file's text.</param>
    public static bool Holds(ReadOnlySpan<char> text) => text[..Math.Min(text.Length, Window)].IndexOf('\0') >= 0;

    /// <summary>
    /// Whether <paramref name="content"/>, a whole file's bytes, is binary by git's conversion rule: a NUL anywhere, a
    /// carriage return no line feed follows, or more than one byte that prints nothing for every 128 that do - backspace,
    /// tab, escape and form feed print, a delete and every other control byte do not, a final end-of-file mark is not
    /// counted, and line endings are neither.
    /// </summary>
    /// <param name="content">The whole file's bytes.</param>
    public static bool ForConversion(ReadOnlySpan<byte> content)
    {
        long printable = 0;
        long nonPrintable = 0;

        for (var at = 0; at < content.Length; at++)
        {
            switch (content[at])
            {
                case (byte)'\r' when at + 1 < content.Length && content[at + 1] == (byte)'\n':
                    at++;
                    break;

                case (byte)'\r' or 0:
                    return true;

                case (byte)'\n':
                    break;

                case (byte)'\b' or (byte)'\t' or 0x1B or (byte)'\f':
                    printable++;
                    break;

                case 0x7F or < 0x20:
                    nonPrintable++;
                    break;

                default:
                    printable++;
                    break;
            }
        }

        if (content.Length > 0 && content[^1] == 0x1A)
        {
            nonPrintable--;
        }

        return (printable >> 7) < nonPrintable;
    }
}
