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
}

/// <summary>One constant part of a literal's value as its text writes it.</summary>
/// <param name="Start">Where it begins in the text.</param>
/// <param name="End">Where it ends: the first character after it.</param>
/// <param name="Line">The line it begins on, from 1: the part's own, for each part of a literal over lines.</param>
internal readonly record struct ScanLiteralPart(int Start, int End, int Line);

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
    /// How many characters of white space each of its lines begins with that are no part of its value - a heredoc's
    /// closing indentation, which PHP takes off every line - none past that many, and none of a part's that begins after
    /// a hole on its line.
    /// </summary>
    public int Indent { get; init; }

    /// <summary>Where its value begins: the start of its first line, where <see cref="Indent"/> is taken off too.</summary>
    public int Content { get; init; }

    /// <summary>Whether its closer never came - its line or the text ended first: its parts are what it holds, but it has no whole value.</summary>
    public bool LeftOpen { get; init; }

    /// <summary>Each constant part's value, decoded, with the line it begins on.</summary>
    /// <param name="source">The text the literal is written in.</param>
    public IEnumerable<(string Value, int Line)> Values(string source)
        => Parts.Select(part => (Decode(source, part.Start, part.End, Escapes, Indent, Indent > 0 && part.Start == Content), part.Line));

    /// <summary>Its whole value, decoded, where it is one closed string of one part and no holes; <see langword="null"/> where it is not.</summary>
    /// <param name="source">The text the literal is written in.</param>
    public string? Whole(string source)
        => Interpolated || Markup || LeftOpen || Parts.Count != 1 ? null : Decode(source, Parts[0].Start, Parts[0].End, Escapes, Indent, Indent > 0);

    /// <summary>
    /// The value the text from <paramref name="start"/> to <paramref name="end"/> writes, by <paramref name="escapes"/>:
    /// each escape decoded, each line break kept as written, and up to <paramref name="indent"/> blanks taken off the start
    /// of each line after a line break - and of the first, where <paramref name="lineStart"/>.
    /// </summary>
    internal static string Decode(string source, int start, int end, ScanEscapes escapes, int indent, bool lineStart)
    {
        // A value with nothing to decode is its text.
        var marker = escapes switch { ScanEscapes.None => (char?)null, ScanEscapes.Html => '&', _ => '\\' };

        if (indent == 0 && (marker is not { } escape || source.AsSpan(start, end - start).IndexOf(escape) < 0))
        {
            return source[start..end];
        }

        var value = new Decoded(end - start);
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
                value.Text(source.AsSpan(at, width));
                at += width;
                dedent = indent > 0;
                continue;
            }

            at = source[at] switch
            {
                '\\' when escapes is not (ScanEscapes.None or ScanEscapes.Html) && at + 1 < end => Escape(source, at, end, escapes, value),
                '&' when escapes == ScanEscapes.Html => Reference(source, at, end, value),
                _ => value.Char(source[at], at + 1),
            };
        }

        return value.ToString();
    }

    /// <summary>The escape whose backslash is at <paramref name="at"/>, decoded into <paramref name="value"/>; answers where the text goes on.</summary>
    private static int Escape(string source, int at, int end, ScanEscapes escapes, Decoded value)
    {
        var next = source[at + 1];
        var after = at + 2;

        switch (escapes)
        {
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

    /// <summary>Where the <c>}</c> closing the <c>{</c> at <paramref name="open"/> stands, past one hex digit at least and none but hex digits; 0 where none does.</summary>
    private static int Braced(string source, int open, int end)
    {
        var at = open + 1;

        while (at < end && char.IsAsciiHexDigit(source[at]))
        {
            at++;
        }

        return at > open + 1 && at < end && source[at] == '}' ? at : 0;
    }

    /// <summary>The character the hex digits from <paramref name="start"/> to the <c>}</c> at <paramref name="close"/> name, added; answers past the brace.</summary>
    private static int Braced(string source, int start, int close, Decoded value)
        => Hex(source, start, close, close - start) is { } code ? value.Code(code, close + 1) : value.Code(int.MaxValue, close + 1);

    /// <summary>
    /// The number the <paramref name="count"/> hex digits at <paramref name="at"/> write - one past every character's code
    /// read as past them all; <see langword="null"/> where fewer stand there.
    /// </summary>
    private static int? Hex(string source, int at, int end, int count)
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
    private static int Number(string source, int at, int end, int radix, int most, Decoded value, bool asByte, int past = -1)
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
    private static int Reference(string source, int at, int end, Decoded value)
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
    private sealed class Decoded(int capacity)
    {
        private readonly StringBuilder _value = new(capacity);
        private readonly List<byte> _bytes = [];

        /// <summary>Adds <paramref name="character"/>; answers <paramref name="past"/>, where the text goes on.</summary>
        public int Char(char character, int past)
        {
            Settle();
            _value.Append(character);
            return past;
        }

        /// <summary>Adds the character of the code <paramref name="code"/> - one no character has, the replacement character; answers <paramref name="past"/>.</summary>
        public int Code(int code, int past)
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
