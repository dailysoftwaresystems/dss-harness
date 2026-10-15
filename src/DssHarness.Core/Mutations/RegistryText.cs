using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;

namespace DssHarness.Core.Mutations;

/// <summary>What a line of an arms registry is.</summary>
public enum RegistryLineKind
{
    /// <summary>Nothing but blanks.</summary>
    Blank,

    /// <summary>A line whose first character that is not blank is <c>#</c>.</summary>
    Comment,

    /// <summary>Anything else: a row, whether or not it is one the registry reads.</summary>
    Row,
}

/// <summary>
/// A row of an arms registry as it is cut into fields: the one place a row's text is trimmed and split at <c>|</c>.
/// </summary>
/// <remarks>
/// Nothing is escaped, and whether the row is one the registry reads is not asked here: a row of any kind has a kind
/// and fields, and names an arm or names none.
/// </remarks>
public sealed class RegistryRow
{
    /// <summary>What stands between a row's fields.</summary>
    internal const char Separator = '|';

    /// <summary>The row's line, trimmed.</summary>
    private readonly string _text;

    internal RegistryRow(string trimmed)
    {
        _text = trimmed;

        var first = trimmed.Split(Separator, 3);

        Kind = first[0].Trim();
        Arm = first.Length > 1 && first[1].Trim() is { Length: > 0 } arm ? arm : null;
    }

    /// <summary>The row's kind: its text before the first <c>|</c>, trimmed.</summary>
    public string Kind { get; }

    /// <summary>
    /// The arm the row names - its second field, as the row spells it - or <see langword="null"/> where it has no second
    /// field or an empty one. Read whatever the row's kind, so a row of a kind the registry does not read still belongs
    /// to the arm it names.
    /// </summary>
    public string? Arm { get; }

    /// <summary>
    /// The row's fields, its kind first: the line cut at <c>|</c> into at most <paramref name="count"/> fields after the
    /// kind, each trimmed, the last taking the rest of the line with every <c>|</c> in it. A row with fewer gives those
    /// it has.
    /// </summary>
    /// <param name="count">How many fields the row's kind takes after the kind.</param>
    public string[] Fields(int count) => [.. _text.Split(Separator, count + 1).Select(field => field.Trim())];
}

/// <summary>One line of an arms registry as it stands: its text, and the ending it has.</summary>
public sealed class RegistryLine
{
    /// <summary>A line.</summary>
    /// <param name="text">The line, without its ending.</param>
    /// <param name="ending">The characters that end it, or nothing where it is the text's last and has none.</param>
    public RegistryLine(string text, string ending = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ending);

        Text = text;
        Ending = ending;

        var trimmed = text.Trim();

        Kind = trimmed.Length == 0 ? RegistryLineKind.Blank
            : trimmed.StartsWith('#') ? RegistryLineKind.Comment
            : RegistryLineKind.Row;
        Row = Kind == RegistryLineKind.Row ? new RegistryRow(trimmed) : null;
    }

    /// <summary>The line, without its ending.</summary>
    public string Text { get; }

    /// <summary>
    /// The characters that end the line, as the text holds them; empty for the text's last line, which nothing ends.
    /// </summary>
    public string Ending { get; }

    /// <summary>What the line is: trimmed, an empty one is blank and one that starts with <c>#</c> is a comment.</summary>
    public RegistryLineKind Kind { get; }

    /// <summary>The line as a row, or <see langword="null"/> where it is blank or a comment.</summary>
    public RegistryRow? Row { get; }

    /// <summary>The arm the line's row names, or <see langword="null"/> where it is no row or names none.</summary>
    public string? Arm => Row?.Arm;

    /// <summary>
    /// The line with the arm its row names spelled <paramref name="arm"/> in its place, and the line as it is where it
    /// names none: two rows that differ only in how each spells its arm are one text once both are spelled one way.
    /// </summary>
    /// <param name="arm">How the arm is to be spelled.</param>
    public string NamedAs(string arm)
    {
        ArgumentNullException.ThrowIfNull(arm);

        if (Arm is not { } named)
        {
            return Text;
        }

        // The arm is the row's second field, trimmed: the first thing after the first separator that is not blank.
        var at = Text.IndexOf(named, Text.IndexOf(RegistryRow.Separator) + 1, StringComparison.Ordinal);

        return string.Concat(Text.AsSpan(0, at), arm, Text.AsSpan(at + named.Length));
    }
}

/// <summary>
/// The one reader of an arms registry's text: its lines, each with the ending it has, so that a line's number is the
/// one an editor shows and the text can be written back as the bytes it was read from.
/// </summary>
/// <remarks>
/// A line ends where every line this tool reads does (<see cref="TextLines"/>) - a carriage return, a line feed, the
/// two together, a form feed, the next-line character, and Unicode's line and paragraph separators - and the text after
/// the last ending is a line too, empty where the text ends with one. So a text of no characters is one empty line, and
/// joining every line with its ending gives the text back.
/// </remarks>
public sealed class RegistryText
{
    /// <summary>The byte order mark, as a character a line may hold.</summary>
    private const char Mark = (char)0xFEFF;

    private RegistryText(string text, bool byteOrderMark)
    {
        Text = text;
        ByteOrderMark = byteOrderMark;
        Lines = Split(text);
    }

    /// <summary>The text, as its lines and their endings spell it, without the byte order mark its bytes open with.</summary>
    public string Text { get; }

    /// <summary>Whether the bytes the text was read from open with a UTF-8 byte order mark, which is no part of its first line.</summary>
    public bool ByteOrderMark { get; }

    /// <summary>The lines, in order: the line numbered <c>n</c> is at <c>n - 1</c>. Never empty.</summary>
    public IReadOnlyList<RegistryLine> Lines { get; }

    /// <summary>The lines of a text already decoded, as whoever decoded it left it.</summary>
    /// <param name="text">The registry's text.</param>
    public static RegistryText Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return new RegistryText(text, byteOrderMark: false);
    }

    /// <summary>The text <paramref name="lines"/> spell, one after another, each with the ending it is given.</summary>
    /// <param name="lines">The lines, in order.</param>
    /// <param name="byteOrderMark">Whether its bytes open with a UTF-8 byte order mark.</param>
    public static RegistryText Of(IEnumerable<RegistryLine> lines, bool byteOrderMark = false)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return new RegistryText(string.Concat(lines.Select(line => line.Text + line.Ending)), byteOrderMark);
    }

    /// <summary>
    /// Reads a registry from the bytes a tree holds: UTF-8 and nothing else (<see cref="Utf8Text"/>), a byte order mark
    /// at their start noted and left out of the text, and no line opening with another.
    /// </summary>
    /// <remarks>
    /// Strictly, because what is read here is written back: a byte that forms no character, read leniently, would be
    /// written as another, and the file changed where nobody changed it. And a line that opens with a byte order mark,
    /// written where it stands first - as it does once the lines above it are gone - would be read back as the text's own
    /// mark and a line without it: another text.
    /// </remarks>
    /// <param name="bytes">The bytes, as the tree holds them.</param>
    /// <param name="text">The text read, or <see langword="null"/> where the bytes are not UTF-8, or a line opens with a byte order mark.</param>
    /// <param name="unread">
    /// Why the bytes could not be read: of the text as a whole, as what follows the name of the file - <c>is not UTF-8:
    /// ...</c>; of one line, as what follows the line - <c>opens with a byte order mark, ...</c>. <see langword="null"/>
    /// where they were read.
    /// </param>
    /// <param name="line">The line <paramref name="unread"/> is of, or <see langword="null"/> where it is of the text as a whole, or the bytes were read.</param>
    /// <returns>Whether the bytes were read.</returns>
    public static bool TryRead(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out RegistryText? text, [NotNullWhen(false)] out string? unread, out int? line)
    {
        var marked = Utf8Text.OpensWithByteOrderMark(bytes);
        var start = marked ? Encoding.UTF8.Preamble.Length : 0;

        line = null;

        if (!Utf8Text.TryDecode(bytes[start..], out var decoded, out unread, start))
        {
            text = null;
            return false;
        }

        var read = new RegistryText(decoded, marked);

        if (read.Lines.Select((line, index) => (line.Text, Number: index + 1)).FirstOrDefault(line => line.Text.StartsWith(Mark)) is { Text: not null } opened)
        {
            text = null;
            line = opened.Number;
            unread = "opens with a byte order mark, which no line may open with: standing first, it would be read as the text's own";

            return false;
        }

        text = read;

        return true;
    }

    /// <summary>
    /// The bytes of the text: UTF-8, behind a byte order mark where the text was read from bytes that open with one.
    /// A text read from bytes gives those bytes.
    /// </summary>
    public byte[] ToBytes()
        => ByteOrderMark ? [.. Encoding.UTF8.Preamble, .. Utf8Text.Encode(Text)] : Utf8Text.Encode(Text);

    private static IReadOnlyList<RegistryLine> Split(string text)
        => [.. TextLines.Split(text).Select(line => new RegistryLine(line.Text, line.Ending))];
}
