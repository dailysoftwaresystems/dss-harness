using System.Text;
using AngleSharp.Html;

namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>How a literal's backslashes are read, by the language and the form that writes it: what its value is decoded by.</summary>
internal enum ScanEscapes
{
    /// <summary>None: the value is the text as written - a raw string's, a nowdoc's.</summary>
    None,

    /// <summary>
    /// C's and C++'s in a narrow string, a plain or a <c>u8</c> one: <c>\n</c> and the other letters, a character's
    /// <c>\u</c> and <c>\U</c>, an octal's and a <c>\x</c>'s bytes - a run of them read as UTF-8 - and a backslash before
    /// a line break joining the two lines; any other character after a backslash itself.
    /// </summary>
    C,

    /// <summary>C's and C++'s in a wide string - <c>L</c>, <c>u</c>, <c>U</c> - where an octal or a <c>\x</c> is a character's code, not a byte.</summary>
    CWide,

    /// <summary>
    /// JavaScript's and TypeScript's: <c>\n</c> and the other letters, an octal's, a <c>\x</c>'s and a <c>\u</c>'s
    /// characters, a backslash before a line break dropping both; any other character after a backslash itself.
    /// </summary>
    JavaScript,

    /// <summary>Dart's: <c>\n</c> and the other letters, a <c>\x</c>'s and a <c>\u</c>'s characters; any other character after a backslash itself, a <c>\$</c> a dollar.</summary>
    Dart,

    /// <summary>PHP's in single quotes: <c>\\</c> and <c>\'</c> alone, every other backslash kept.</summary>
    PhpSingle,

    /// <summary>
    /// PHP's in double quotes: <c>\n</c> and the other letters, <c>\\</c>, <c>\$</c>, <c>\"</c>, an octal's and a
    /// <c>\x</c>'s bytes - a run of them read as UTF-8 - and a <c>\u{...}</c>'s character; any other backslash kept.
    /// </summary>
    PhpDouble,

    /// <summary>PHP's in backticks: as in double quotes, a backtick escaped where a double quote is.</summary>
    PhpBacktick,

    /// <summary>PHP's in a heredoc: as in double quotes, no quote escaped.</summary>
    PhpHeredoc,

    /// <summary>Markup's - a JSX attribute's: no backslash escapes, and HTML's character references decoded.</summary>
    Html,

    /// <summary>
    /// A style sheet's, in a string, an unquoted <c>url(...)</c> and a name alike (<see cref="ScanLiteral.CssEscape"/>): up
    /// to six hexadecimal digits a character's code - none, a surrogate's or one past every character's the replacement
    /// character - and the one white space after them no part of the value; a backslash before a line break dropping both;
    /// any other character after a backslash itself, a space among them.
    /// </summary>
    Css,

    /// <summary>
    /// Python's, in a string no prefix makes raw, bytes or formatted: a backslash before a line break dropping both;
    /// <c>\\</c>, <c>\'</c> and <c>\"</c> the character, <c>\n</c> and the other letters, an octal of up to three digits,
    /// <c>\x</c> of two, <c>\u</c> of four and <c>\U</c> of eight, and <c>\N{...}</c> the character its name stands for, as CPython
    /// 3.15 reads one (<see cref="UnicodeNames.Python"/>); any other backslash kept, with the character after it, as Python keeps one
    /// it does not know - <c>"\d"</c> two characters. An escape Python refuses - a name Unicode has not, a <c>\N</c> with no name
    /// in braces, an escape short of its digits, a <c>\U</c> past every character - is kept as written and said refused
    /// (<see cref="ScanLiteralValue.RefusedLines"/>), its line for the reader to mark.
    /// </summary>
    Python,

    /// <summary>
    /// Python's in a bytes string no prefix makes raw: as in any other string, but an octal's and a <c>\x</c>'s bytes - a
    /// run of them read as UTF-8 - and <c>\u</c>, <c>\U</c> and <c>\N</c> no escape of bytes, each kept with its backslash,
    /// which Python warns of and never refuses.
    /// </summary>
    PythonBytes,

    /// <summary>Python's in an f-string or a t-string no prefix makes raw: as in any other string, and <c>{{</c> and <c>}}</c> a brace.</summary>
    PythonFormatted,

    /// <summary>Python's in an f-string or a t-string a prefix makes raw: the text as written, but <c>{{</c> and <c>}}</c> a brace.</summary>
    PythonRawFormatted,
}

/// <summary>One constant part of a literal's value as its text writes it.</summary>
/// <param name="Start">Where it begins in the text.</param>
/// <param name="End">Where it ends: the first character after it.</param>
/// <param name="Line">The line it begins on, from 1: the part's own, for each part of a literal over lines.</param>
internal readonly record struct ScanLiteralPart(long Start, long End, int Line)
{
    /// <summary>
    /// Where the text is written that the part's value goes on with as written, never decoded: a debugging field's source - an
    /// f-string's <c>{x=}</c> to its <c>=</c> and the blanks after it - which Python's value holds after the text before the
    /// field, <c>f"a{x=}"</c> holding <c>ax=</c>; <see langword="null"/> where the part goes on with none, as every other
    /// language's.
    /// </summary>
    public (long Start, long End)? Written { get; init; }
}

/// <summary>
/// One constant part of a literal's value, decoded (<see cref="ScanLiteral.Reads"/>): the value; the line it begins on; and the
/// lines its refused escapes stand on - kept as written in the value, since Python compiles none of them - one a refused escape,
/// in the order the text writes them, however many stand on one line; none where the part holds none. That a line is marked
/// once is the reader's rule, not the list's.
/// </summary>
/// <param name="Value">The part's value, decoded.</param>
/// <param name="Line">The line it begins on, from 1: the part's own, for each part of a literal over lines.</param>
/// <param name="RefusedLines">The lines, from 1, its refused escapes stand on: one for each escape, in order; none where the part holds none.</param>
internal readonly record struct ScanLiteralValue(string Value, int Line, IReadOnlyList<int> RefusedLines);

/// <summary>
/// One string literal as its lexer read it: where each constant part of its value is written - a plain string's one part,
/// between its opener and its closer whatever their form, a prefix, a raw string's delimiter, a triple quote or a
/// heredoc's label; an interpolated one's parts between its holes, each hole's sigil and closer its own - how its escapes
/// are decoded, and the indentation its lines carry that is no part of its value. The one place a literal's value is
/// decided, for every scanned language: what the lexer saw of its form, decoded by its language's escapes.
/// </summary>
/// <param name="Parts">Its constant parts, in order.</param>
/// <param name="Escapes">How its backslashes are read.</param>
internal sealed record ScanLiteral(IReadOnlyList<ScanLiteralPart> Parts, ScanEscapes Escapes)
{
    /// <summary>Whether it holds holes - code its value is made of - between its parts.</summary>
    public bool Interpolated { get; init; }

    /// <summary>Whether it is markup that holds strings - a JSX element and its attributes' - and no string itself: each part a string it holds.</summary>
    public bool Markup { get; init; }

    /// <summary>
    /// Where it is <see cref="Markup"/>, where the name of the attribute each part is the value of stands, by the part's place
    /// among <see cref="Parts"/> - an empty stretch where no name and <c>=</c> stand before it - as its lexer read the tag;
    /// <see langword="null"/> for a string.
    /// </summary>
    public IReadOnlyList<(long Start, long End)>? Names { get; init; }

    /// <summary>
    /// How many characters of white space each of its lines begins with that are no part of its value - a heredoc's
    /// closing indentation, which PHP takes off every line - none past that many, and none of a part's that begins after
    /// a hole on its line.
    /// </summary>
    public long Indent { get; init; }

    /// <summary>Where its value begins: the start of its first line, where <see cref="Indent"/> is taken off too.</summary>
    public long Content { get; init; }

    /// <summary>Whether its closer never came - its line or the text ended first: its parts are what it holds, but it has no whole value.</summary>
    public bool LeftOpen { get; init; }

    /// <summary>Each constant part's value, decoded, with the line it begins on.</summary>
    /// <param name="source">The text the literal is written in.</param>
    public IEnumerable<(string Value, int Line)> Values(CodeText source)
        => Reads(source).Select(value => (value.Value, value.Line));

    /// <summary>
    /// Each constant part's value, decoded, with the line it begins on and the lines its refused escapes stand on, one an escape
    /// (<see cref="ScanLiteralValue"/>): what a reader that marks a refusal, as Python's does, reads a literal by.
    /// </summary>
    /// <param name="source">The text the literal is written in.</param>
    public IEnumerable<ScanLiteralValue> Reads(CodeText source)
        => Parts.Select(part => Read(source, part, Indent > 0 && part.Start == Content));

    /// <summary>Its whole value, decoded, where it is one closed string of one part and no holes; <see langword="null"/> where it is not.</summary>
    /// <param name="source">The text the literal is written in.</param>
    public string? Whole(CodeText source)
        => Interpolated || Markup || LeftOpen || Parts.Count != 1 ? null : Read(source, Parts[0], Indent > 0).Value;

    /// <summary>
    /// The part <paramref name="part"/> decoded (<see cref="ScanLiteralValue"/>): its text by the literal's escapes - its
    /// first line's indentation taken off where <paramref name="lineStart"/> - and the text it goes on with as written
    /// (<see cref="ScanLiteralPart.Written"/>), which holds no escape of its own to refuse.
    /// </summary>
    private ScanLiteralValue Read(CodeText source, ScanLiteralPart part, bool lineStart)
    {
        var decoded = Decode(source, part.Start, part.End, Escapes, Indent, lineStart, out var refused);
        var value = part.Written is { } written ? decoded + source.Slice(written.Start, written.End) : decoded;

        return new ScanLiteralValue(value, part.Line, refused.Count == 0 ? [] : [.. refused.Select(source.LineIndex.LineAt)]);
    }

    /// <summary>
    /// The value the text from <paramref name="start"/> to <paramref name="end"/> writes, by <paramref name="escapes"/>:
    /// each escape decoded, each line break kept as written, and up to <paramref name="indent"/> blanks taken off the start
    /// of each line after a line break - and of the first, where <paramref name="lineStart"/>.
    /// </summary>
    internal static string Decode(CodeText source, long start, long end, ScanEscapes escapes, long indent, bool lineStart)
        => Decode(source, start, end, escapes, indent, lineStart, out _);

    /// <summary>
    /// The value the text from <paramref name="start"/> to <paramref name="end"/> writes, by <paramref name="escapes"/>:
    /// each escape decoded, each line break kept as written, and up to <paramref name="indent"/> blanks taken off the start
    /// of each line after a line break - and of the first, where <paramref name="lineStart"/>; and where each escape the language
    /// refuses stands, kept as written - Python's alone refuses any.
    /// </summary>
    /// <param name="source">The text.</param>
    /// <param name="start">Where the value's text begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="escapes">How its backslashes are read.</param>
    /// <param name="indent">How many blanks each line after a line break begins with that are no part of the value.</param>
    /// <param name="lineStart">Whether the first line's blanks are taken off too.</param>
    /// <param name="refused">Where the backslash of each refused escape stands, in order; none where the text holds none.</param>
    internal static string Decode(CodeText source, long start, long end, ScanEscapes escapes, long indent, bool lineStart, out IReadOnlyList<long> refused)
    {
        // A value with nothing to decode is its text.
        var markers = escapes switch
        {
            ScanEscapes.None => "",
            ScanEscapes.Html => "&",
            ScanEscapes.PythonFormatted => "\\{}",
            ScanEscapes.PythonRawFormatted => "{}",
            _ => "\\",
        };

        if (indent == 0 && (markers.Length == 0 || source.FindAny(markers, start, end) < 0))
        {
            refused = [];
            return source.Slice(start, end);
        }

        var value = new Decoded();
        var at = start;
        var dedent = lineStart;

        while (at < end)
        {
            if (dedent)
            {
                for (var taken = 0; taken < indent && at < end && source[at] is ' ' or '\t'; taken++)
                {
                    at++;
                }

                dedent = false;
                continue;
            }

            if (ScanLexing.LineBreakAt(source, at, end) is var width and > 0)
            {
                value.Text(source.Slice(at, at + width));
                at += width;
                dedent = indent > 0;
                continue;
            }

            at = source[at] switch
            {
                '\\' when escapes is not (ScanEscapes.None or ScanEscapes.Html or ScanEscapes.PythonRawFormatted) && at + 1 < end => Escape(source, at, end, escapes, value),
                '&' when escapes == ScanEscapes.Html => Reference(source, at, end, value),

                // A brace a formatted string doubles is one brace of its value.
                '{' or '}' when escapes is (ScanEscapes.PythonFormatted or ScanEscapes.PythonRawFormatted) && at + 1 < end && source[at + 1] == source[at] => value.Char(source[at], at + 2),
                _ => value.Char(source[at], at + 1),
            };
        }

        refused = value.Refused;
        return value.ToString();
    }

    /// <summary>
    /// How many characters the style sheet's escape at <paramref name="at"/> is, within <paramref name="end"/>: a backslash
    /// and the character it escapes - a surrogate pair whole - or up to six hexadecimal digits and the one white space that
    /// may end them, a CR LF one; 0 where none stands there, as a backslash before a line break, before a surrogate apart
    /// from its pair or at the end is none. The
    /// one reading of how long one is, which the style sheet's lexer reads a name by and <see cref="ScanEscapes.Css"/>
    /// decodes a value by.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="at">Where the backslash may stand.</param>
    /// <param name="end">Where the text read ends.</param>
    internal static int CssEscape(CodeText text, long at, long end)
    {
        if (at + 1 >= end || text[at] != '\\' || ScanLexing.LineBreakAt(text, at + 1, end) > 0)
        {
            return 0;
        }

        if (!char.IsAsciiHexDigit(text[at + 1]))
        {
            return !char.IsSurrogate(text[at + 1]) ? 2 : char.IsHighSurrogate(text[at + 1]) && at + 2 < end && char.IsLowSurrogate(text[at + 2]) ? 3 : 0;
        }

        var past = at + 1;

        while (past < end && past - at <= 6 && char.IsAsciiHexDigit(text[past]))
        {
            past++;
        }

        return (int)(past - at) + (past < end && text[past] is ' ' or '\t' or '\f' ? 1 : ScanLexing.LineBreakAt(text, past, end));
    }

    /// <summary>The style sheet's escape whose backslash is at <paramref name="at"/>, decoded into <paramref name="value"/>; answers where the text goes on.</summary>
    private static long CssEscaped(CodeText source, long at, long end, Decoded value)
    {
        if (ScanLexing.LineBreakAt(source, at + 1, end) is var continued and > 0)
        {
            return at + 1 + continued;
        }

        var width = CssEscape(source, at, end);

        if (width == 0)
        {
            // A backslash before a surrogate apart from its pair escapes nothing: the text goes on at what follows it.
            return at + 1;
        }

        if (!char.IsAsciiHexDigit(source[at + 1]))
        {
            value.Text(source.Slice(at + 1, at + width));
            return at + width;
        }

        var digits = at + 1;

        while (digits < at + width && char.IsAsciiHexDigit(source[digits]))
        {
            digits++;
        }

        var code = Hex(source, at + 1, digits, digits - at - 1) ?? 0;
        return value.Code(code is 0 or (>= 0xD800 and <= 0xDFFF) ? 0xFFFD : code, at + width);
    }

    /// <summary>The escape whose backslash is at <paramref name="at"/>, decoded into <paramref name="value"/>; answers where the text goes on.</summary>
    private static long Escape(CodeText source, long at, long end, ScanEscapes escapes, Decoded value)
    {
        var next = source[at + 1];
        var after = at + 2;

        switch (escapes)
        {
            case ScanEscapes.Css:
                return CssEscaped(source, at, end, value);

            case ScanEscapes.PhpSingle:
                return next is '\\' or '\'' ? value.Char(next, after) : value.Char('\\', at + 1);

            case ScanEscapes.PhpDouble or ScanEscapes.PhpBacktick or ScanEscapes.PhpHeredoc:
                return next switch
                {
                    'n' => value.Char('\n', after),
                    't' => value.Char('\t', after),
                    'r' => value.Char('\r', after),
                    'v' => value.Char('\v', after),
                    'e' => value.Char('\u001b', after),
                    'f' => value.Char('\f', after),
                    '\\' or '$' => value.Char(next, after),
                    '"' when escapes == ScanEscapes.PhpDouble => value.Char(next, after),
                    '`' when escapes == ScanEscapes.PhpBacktick => value.Char(next, after),
                    >= '0' and <= '7' => Number(source, at + 1, end, 8, 3, value, asByte: true),
                    'x' when after < end && char.IsAsciiHexDigit(source[after]) => Number(source, after, end, 16, 2, value, asByte: true),
                    'u' when after < end && source[after] == '{' && Braced(source, after, end) is var close and > 0 => Braced(source, after + 1, close, value),
                    _ => value.Char('\\', at + 1),
                };

            case ScanEscapes.C or ScanEscapes.CWide:
            {
                if (ScanLexing.LineBreakAt(source, at + 1, end) is var spliced and > 0)
                {
                    return at + 1 + spliced;
                }

                var bytes = escapes == ScanEscapes.C;

                return next switch
                {
                    'a' => value.Char('\a', after),
                    'b' => value.Char('\b', after),
                    'e' => value.Char('\u001b', after),
                    'f' => value.Char('\f', after),
                    'n' => value.Char('\n', after),
                    'r' => value.Char('\r', after),
                    't' => value.Char('\t', after),
                    'v' => value.Char('\v', after),
                    >= '0' and <= '7' => Number(source, at + 1, end, 8, 3, value, bytes),
                    'o' when after < end && source[after] == '{' && Braced(source, after, end) is var close and > 0 => Number(source, after + 1, close, 8, close - after - 1, value, bytes, past: close + 1),
                    'x' when after < end && source[after] == '{' && Braced(source, after, end) is var close and > 0 => Number(source, after + 1, close, 16, close - after - 1, value, bytes, past: close + 1),
                    'x' when after < end && char.IsAsciiHexDigit(source[after]) => Number(source, after, end, 16, int.MaxValue, value, bytes),
                    'u' when after < end && source[after] == '{' && Braced(source, after, end) is var close and > 0 => Braced(source, after + 1, close, value),
                    'u' when Hex(source, after, end, 4) is { } code => value.Code(code, after + 4),
                    'U' when Hex(source, after, end, 8) is { } code => value.Code(code, after + 8),
                    _ => value.Char(next, after),
                };
            }

            case ScanEscapes.JavaScript:
            {
                if (ScanLexing.LineBreakAt(source, at + 1, end) is var continued and > 0)
                {
                    return at + 1 + continued;
                }

                return next switch
                {
                    '\u2028' or '\u2029' => after,
                    'b' => value.Char('\b', after),
                    'f' => value.Char('\f', after),
                    'n' => value.Char('\n', after),
                    'r' => value.Char('\r', after),
                    't' => value.Char('\t', after),
                    'v' => value.Char('\v', after),
                    >= '0' and <= '7' => Number(source, at + 1, end, 8, next <= '3' ? 3 : 2, value, asByte: false),
                    'x' when Hex(source, after, end, 2) is { } code => value.Code(code, after + 2),
                    'u' when after < end && source[after] == '{' && Braced(source, after, end) is var close and > 0 => Braced(source, after + 1, close, value),
                    'u' when Hex(source, after, end, 4) is { } code => value.Code(code, after + 4),
                    _ => value.Char(next, after),
                };
            }

            case ScanEscapes.Python or ScanEscapes.PythonBytes or ScanEscapes.PythonFormatted:
            {
                if (ScanLexing.LineBreakAt(source, at + 1, end) is var spliced and > 0)
                {
                    return at + 1 + spliced;
                }

                var bytes = escapes == ScanEscapes.PythonBytes;

                return next switch
                {
                    '\\' or '\'' or '"' => value.Char(next, after),
                    'a' => value.Char('\a', after),
                    'b' => value.Char('\b', after),
                    'f' => value.Char('\f', after),
                    'n' => value.Char('\n', after),
                    'r' => value.Char('\r', after),
                    't' => value.Char('\t', after),
                    'v' => value.Char('\v', after),
                    >= '0' and <= '7' => Number(source, at + 1, end, 8, 3, value, asByte: bytes),
                    'x' when Hex(source, after, end, 2) is not null => Number(source, after, end, 16, 2, value, asByte: bytes),

                    // \x4 - in bytes too - and \u12: short of the digits Python writes them with, which it refuses as truncated.
                    'x' => Short(at, value),
                    'u' when !bytes && Hex(source, after, end, 4) is { } code => value.Code(code, after + 4),
                    'u' when !bytes => Short(at, value),
                    'U' when !bytes => Universal(source, at, after, end, value),

                    // \N{LATIN SMALL LETTER A} names a character as no number does: the one its name stands for, by Unicode's names and aliases as CPython 3.15 reads one.
                    'N' when !bytes => Named(source, at, after, end, value),

                    // An escape Python does not know - and in bytes \u, \U and \N - is kept: the backslash, and the character after it read as any.
                    _ => value.Char('\\', at + 1),
                };
            }

            default:
                return next switch
                {
                    'n' => value.Char('\n', after),
                    'r' => value.Char('\r', after),
                    'f' => value.Char('\f', after),
                    'b' => value.Char('\b', after),
                    't' => value.Char('\t', after),
                    'v' => value.Char('\v', after),
                    'x' when Hex(source, after, end, 2) is { } code => value.Code(code, after + 2),
                    'u' when after < end && source[after] == '{' && Braced(source, after, end) is var close and > 0 => Braced(source, after + 1, close, value),
                    'u' when Hex(source, after, end, 4) is { } code => value.Code(code, after + 4),
                    _ => value.Char(next, after),
                };
        }
    }

    /// <summary>
    /// The escape whose backslash is at <paramref name="at"/>, short of what Python writes it with - a <c>\N</c> with no brace, a
    /// <c>\x</c>, a <c>\u</c> or a <c>\U</c> before fewer hex digits than its own - which Python refuses: kept as it is, the backslash
    /// and then the character after it read as any, and said refused (<see cref="Decoded.Refuse"/>); answers where the text goes on,
    /// past the backslash.
    /// </summary>
    private static long Short(long at, Decoded value)
    {
        value.Refuse(at);
        return value.Char('\\', at + 1);
    }

    /// <summary>
    /// The <c>\U</c> whose backslash is at <paramref name="at"/>, its eight hex digits from <paramref name="after"/>: a code no greater
    /// than every character's is that character - a lone surrogate kept, as a <c>\u</c>'s is - and any other, or fewer digits than eight,
    /// Python refuses: the first kept as written to its last digit (<see cref="Refused"/>), the second as a short escape
    /// (<see cref="Short"/>).
    /// </summary>
    private static long Universal(CodeText source, long at, long after, long end, Decoded value)
        => Hex(source, after, end, 8) is not { } code ? Short(at, value) : code <= 0x10FFFF ? value.Code(code, after + 8) : Refused(source, at, after + 8, value);

    /// <summary>
    /// The <c>\N{...}</c> whose backslash is at <paramref name="at"/>, its brace at <paramref name="after"/> where one stands: the
    /// character its name stands for (<see cref="UnicodeNames.Python"/>), the name read to the first <c>}</c> of the part whatever stands
    /// before it - a line break included - as CPython's decoder reads one. A name Python refuses - none Unicode has, an empty one, or one
    /// no brace closes - is kept as written: to its brace (<see cref="Refused"/>), or as a short escape where the part closes none
    /// (<see cref="Short"/>).
    /// </summary>
    private static long Named(CodeText source, long at, long after, long end, Decoded value)
    {
        var close = after < end && source[after] == '{' ? source.Find('}', after, end) : -1;

        return close > after + 1 && UnicodeNames.Python(source.Slice(after + 1, close).AsSpan()) is { } code ? value.Code(code, close + 1)
            : close > 0 ? Refused(source, at, close + 1, value)
            : Short(at, value);
    }

    /// <summary>Where the <c>}</c> closing the <c>{</c> at <paramref name="open"/> stands, past one hex digit at least and none but hex digits; 0 where none does.</summary>
    private static long Braced(CodeText source, long open, long end)
    {
        var at = open + 1;

        while (at < end && char.IsAsciiHexDigit(source[at]))
        {
            at++;
        }

        return at > open + 1 && at < end && source[at] == '}' ? at : 0;
    }

    /// <summary>The text from <paramref name="start"/> to <paramref name="end"/> as written, added to <paramref name="value"/>; answers <paramref name="end"/>.</summary>
    private static long Kept(CodeText source, long start, long end, Decoded value)
    {
        value.Text(source.Slice(start, end));
        return end;
    }

    /// <summary>The text from <paramref name="start"/> to <paramref name="end"/> as written, an escape Python refuses - said refused (<see cref="Decoded.Refuse"/>); answers <paramref name="end"/>.</summary>
    private static long Refused(CodeText source, long start, long end, Decoded value)
    {
        value.Refuse(start);
        return Kept(source, start, end, value);
    }

    /// <summary>The character the hex digits from <paramref name="start"/> to the <c>}</c> at <paramref name="close"/> name, added; answers past the brace.</summary>
    private static long Braced(CodeText source, long start, long close, Decoded value)
        => Hex(source, start, close, close - start) is { } code ? value.Code(code, close + 1) : value.Code(int.MaxValue, close + 1);

    /// <summary>
    /// The number the <paramref name="count"/> hex digits at <paramref name="at"/> write - one past every character's code
    /// read as past them all; <see langword="null"/> where fewer stand there.
    /// </summary>
    private static int? Hex(CodeText source, long at, long end, long count)
    {
        if (count <= 0 || at + count > end)
        {
            return null;
        }

        var code = 0L;

        for (var index = at; index < at + count; index++)
        {
            if (!char.IsAsciiHexDigit(source[index]))
            {
                return null;
            }

            code = Math.Min((code * 16) + Digit(source[index]), int.MaxValue);
        }

        return (int)code;
    }

    /// <summary>The value of the digit <paramref name="digit"/>, decimal or hex.</summary>
    private static int Digit(char digit) => digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10;

    /// <summary>
    /// The number of at most <paramref name="most"/> digits of <paramref name="radix"/> from <paramref name="at"/>, added -
    /// a byte where <paramref name="asByte"/>, its low eight bits, else the character of its code; answers where the text
    /// goes on: past its digits, or <paramref name="past"/> where given.
    /// </summary>
    private static long Number(CodeText source, long at, long end, int radix, long most, Decoded value, bool asByte, long past = -1)
    {
        var code = 0L;
        var index = at;

        while (index < end && index - at < most && (radix == 8 ? source[index] is >= '0' and <= '7' : char.IsAsciiHexDigit(source[index])))
        {
            code = Math.Min((code * radix) + Digit(source[index]), int.MaxValue);
            index++;
        }

        if (asByte)
        {
            value.Byte((byte)(code & 0xFF));
        }
        else
        {
            value.Code((int)code, index);
        }

        return past >= 0 ? past : index;
    }

    /// <summary>
    /// The character reference whose <c>&amp;</c> is at <paramref name="at"/>, decoded where it is one, as HTML reads
    /// one: a name of ASCII letters and digits, or a <c>#</c> and a decimal number or an <c>x</c> and a hexadecimal one -
    /// of any length, a number past every character's the replacement character - then its <c>;</c>; answers where the
    /// text goes on. Only the run its grammar allows is read, so no text past it is searched for a <c>;</c>.
    /// </summary>
    private static long Reference(CodeText source, long at, long end, Decoded value)
    {
        var index = at + 1;

        if (index < end && source[index] == '#')
        {
            var hex = index + 1 < end && source[index + 1] is 'x' or 'X';
            var digits = index + (hex ? 2 : 1);
            var code = 0L;

            for (index = digits; index < end && (hex ? char.IsAsciiHexDigit(source[index]) : char.IsAsciiDigit(source[index])); index++)
            {
                code = Math.Min((code * (hex ? 16 : 10)) + Digit(source[index]), int.MaxValue);
            }

            return index > digits && index < end && source[index] == ';' ? value.Code((int)code, index + 1) : value.Char('&', at + 1);
        }

        while (index < end && char.IsAsciiLetterOrDigit(source[index]))
        {
            index++;
        }

        if (index > at + 1 && index < end && source[index] == ';' && HtmlEntityProvider.Resolver.GetSymbol(source.Substring(at + 1, index - at)) is { } symbol)
        {
            value.Text(symbol);
            return index + 1;
        }

        return value.Char('&', at + 1);
    }

    /// <summary>A value being decoded: its characters, and the bytes of a run of byte escapes, read as UTF-8 once the run ends.</summary>
    private sealed class Decoded
    {
        private readonly StringBuilder _value = new();
        private readonly List<byte> _bytes = [];

        /// <summary>Where the backslash of each escape the language refused stands, in the text decoded, in order; <see langword="null"/> until one is.</summary>
        private List<long>? _refusedAt;

        /// <summary>Where the backslash of each escape the language refuses stands, in the text decoded, in order; none where no escape was refused.</summary>
        public IReadOnlyList<long> Refused => _refusedAt ?? [];

        /// <summary>Says the escape whose backslash is at <paramref name="at"/> is one the language refuses, which keeps it as written.</summary>
        /// <param name="at">Where its backslash stands, in the text decoded.</param>
        public void Refuse(long at) => (_refusedAt ??= []).Add(at);

        /// <summary>Adds <paramref name="character"/>; answers <paramref name="past"/>, where the text goes on.</summary>
        public long Char(char character, long past)
        {
            Settle();
            _value.Append(character);
            return past;
        }

        /// <summary>Adds the character of the code <paramref name="code"/> - one no character has, the replacement character; answers <paramref name="past"/>.</summary>
        public long Code(int code, long past)
        {
            Settle();

            _ = code switch
            {
                >= 0xD800 and <= 0xDFFF => _value.Append((char)code),
                >= 0 and <= 0x10FFFF => _value.Append(char.ConvertFromUtf32(code)),
                _ => _value.Append('\uFFFD'),
            };

            return past;
        }

        /// <summary>Adds the text <paramref name="text"/>.</summary>
        public void Text(ReadOnlySpan<char> text)
        {
            Settle();
            _value.Append(text);
        }

        /// <summary>Adds one byte of a run of byte escapes.</summary>
        public void Byte(byte value) => _bytes.Add(value);

        public override string ToString()
        {
            Settle();
            return _value.ToString();
        }

        /// <summary>The bytes of the run that ended, read as UTF-8 - a byte no character begins the replacement character.</summary>
        private void Settle()
        {
            if (_bytes.Count > 0)
            {
                _value.Append(Encoding.UTF8.GetString([.. _bytes]));
                _bytes.Clear();
            }
        }
    }
}
