using System.Text;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>The syntaxes a style sheet is written in, each lexed by its own rules.</summary>
internal enum StyleSyntax
{
    /// <summary>CSS: <c>/* */</c> its one comment.</summary>
    Css,

    /// <summary>SCSS: <c>//</c> a comment too, <c>#{...}</c> an interpolation, <c>%name</c> a placeholder.</summary>
    Scss,

    /// <summary>Sass's indented syntax: SCSS's rules, its blocks told by indentation (<see cref="CssLexer.Braced"/>).</summary>
    Sass,

    /// <summary>Less: <c>//</c> a comment too, <c>@{name}</c> an interpolation.</summary>
    Less,
}

/// <summary>
/// The lexical rules of style sheets for the structural scanner: comments kept apart - <c>/* */</c>, and <c>//</c> to
/// its line's end in SCSS, Sass and Less, never in CSS; a string - in single or double quotes, to its own quote not
/// escaped, an unescaped line break ending it - as one token, never read as code; an unquoted <c>url(...)</c> one token
/// to its parenthesis, so a <c>//</c> it holds is no comment; an interpolation outside a string - SCSS's and Sass's
/// <c>#{...}</c>, Less's <c>@{name}</c> - one token too; and, wherever one stands, the code an interpolation holds said
/// where it is, so it is read in its turn as a text of its own, as PHP's <c>{$...}</c> and JavaScript's <c>${...}</c>
/// are. A name is CSS's identifier - escapes and characters past ASCII among its own, a custom property's <c>--</c>
/// with it - or one an at-rule (<c>@media</c>), a variable (<c>$x</c>, Less's <c>@x</c>) or a placeholder
/// (<c>%x</c>) writes; numbers carry their units; every other punctuator is alone.
/// </summary>
internal static class CssLexer
{
    /// <summary>Scans <paramref name="text"/> by the rules of <paramref name="syntax"/>.</summary>
    /// <param name="text">A sheet's text: braced, where it is written in the indented syntax (<see cref="Braced"/>).</param>
    /// <param name="syntax">What it is written in.</param>
    public static InterpolatedText Scan(string text, StyleSyntax syntax) => Scan(text, 0, text.Length, CodeFileText.LineIndex(text), syntax);

    /// <summary>
    /// Scans the part of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/> by the rules of
    /// <paramref name="syntax"/>, its tokens placed in the whole text: an interpolation's code, read where it stands.
    /// </summary>
    /// <param name="text">A sheet's text.</param>
    /// <param name="start">Where the part begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="lines">The text's lines.</param>
    /// <param name="syntax">What it is written in.</param>
    public static InterpolatedText Scan(string text, int start, int end, CodeLineIndex lines, StyleSyntax syntax)
    {
        var lexing = new Lexing(text, lines, syntax);
        lexing.Run(start, end);
        return new InterpolatedText(new ScannedText(text, lexing.Tokens, lexing.Comments), lexing.Interpolations, lines);
    }

    /// <summary>Whether <paramref name="syntax"/> writes <c>//</c> comments.</summary>
    private static bool LineComments(StyleSyntax syntax) => syntax is not StyleSyntax.Css;

    /// <summary>Whether <paramref name="character"/>, past ASCII, stands in a name: any but a surrogate, which stands in one only with its pair.</summary>
    private static bool Wide(char character) => character > '\u007f' && !char.IsSurrogate(character);

    /// <summary>
    /// How many characters at <paramref name="at"/> a name holds as one of its own past its first: a letter, a digit,
    /// <c>_</c>, <c>-</c>, a character past ASCII - a surrogate with its pair, two - or an escape, its backslash and what it
    /// escapes; 0 where none stands there.
    /// </summary>
    private static int NamePart(string text, int at, int end)
    {
        var character = text[at];

        if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-' || Wide(character))
        {
            return 1;
        }

        return Escape(text, at, end) is var escape and > 0 ? escape : Pair(text, at, end);
    }

    /// <summary>How many characters at <paramref name="at"/> may begin a name: a letter, <c>_</c>, a character past ASCII, or an escape; 0 where none stands there.</summary>
    private static int NameStart(string text, int at, int end)
    {
        var character = text[at];

        if (char.IsAsciiLetter(character) || character == '_' || Wide(character))
        {
            return 1;
        }

        return Escape(text, at, end) is var escape and > 0 ? escape : Pair(text, at, end);
    }

    /// <summary>2 where a surrogate pair stands at <paramref name="at"/>, else 0.</summary>
    private static int Pair(string text, int at, int end)
        => char.IsHighSurrogate(text[at]) && at + 1 < end && char.IsLowSurrogate(text[at + 1]) ? 2 : 0;

    /// <summary>
    /// How many characters the escape at <paramref name="at"/> is: a backslash and the character it escapes - a surrogate
    /// pair whole - or up to six hexadecimal digits and the one white space that may end them; 0 where none stands there, as
    /// a backslash before a line break is none.
    /// </summary>
    private static int Escape(string text, int at, int end)
    {
        if (text[at] != '\\' || at + 1 >= end || ScanLexing.LineBreakAt(text, at + 1, end) > 0)
        {
            return 0;
        }

        if (!char.IsAsciiHexDigit(text[at + 1]))
        {
            return char.IsSurrogate(text[at + 1]) ? (Pair(text, at + 1, end) is 2 ? 3 : 0) : 2;
        }

        var past = at + 1;

        while (past < end && past - at <= 6 && char.IsAsciiHexDigit(text[past]))
        {
            past++;
        }

        return past < end && text[past] is ' ' or '\t' ? past + 1 - at : past - at;
    }

    /// <summary>
    /// <paramref name="name"/> as CSS reads it, each escape the character it stands for: <c>md\:flex</c> is the class
    /// <c>md:flex</c> markup writes. A code point no character is reads as the replacement character.
    /// </summary>
    /// <param name="name">A name as a sheet writes it.</param>
    public static string Unescaped(string name)
    {
        if (!name.Contains('\\', StringComparison.Ordinal))
        {
            return name;
        }

        var read = new StringBuilder(name.Length);

        for (var at = 0; at < name.Length;)
        {
            var escape = Escape(name, at, name.Length);

            if (escape == 0)
            {
                read.Append(name[at]);
                at++;
                continue;
            }

            var escaped = name.AsSpan(at + 1, escape - 1).TrimEnd([' ', '\t']);

            if (char.IsAsciiHexDigit(escaped[0]))
            {
                var code = int.Parse(escaped, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
                read.Append(code is 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32(0xFFFD) : char.ConvertFromUtf32(code));
            }
            else
            {
                read.Append(escaped);
            }

            at += escape;
        }

        return read.ToString();
    }

    /// <summary>
    /// The indented syntax of a <c>.sass</c> sheet written in SCSS's braces, line for line - no line added, none taken
    /// away, so every token keeps the line it stands on: each statement a <c>;</c> ends or, where the lines after it are
    /// indented deeper, a <c>{</c> opens - the block those lines are - and the <c>}</c> of each block a dedent closes, each
    /// put where its line's code ends, before a comment that trails it. A statement goes on over the lines its brackets
    /// hold open and past a line its list leaves at a comma. A comment goes on over the lines indented deeper than its
    /// first, as the indented syntax reads it: each line a <c>//</c> goes on over written a comment of its own, and a
    /// <c>/*</c> to the line that closes it - closed after the last where none does.
    /// </summary>
    /// <param name="text">A <c>.sass</c> sheet's text.</param>
    public static string Braced(string text)
    {
        var spans = CodeFileText.Spans(text);
        var lines = new SassLine[spans.Count];

        for (var at = 0; at < spans.Count; at++)
        {
            var first = spans[at].Start;

            while (first < spans[at].End && text[first] is ' ' or '\t')
            {
                first++;
            }

            lines[at] = new SassLine(first == spans[at].End ? SassLineKind.Blank : SassLineKind.Code, first - spans[at].Start, first, spans[at].End);
        }

        for (var at = 0; at < lines.Length; at++)
        {
            if (lines[at].Kind == SassLineKind.Code && CommentOver(text, lines, at) is var last and >= 0)
            {
                at = last;
            }
        }

        for (var at = 0; at < lines.Length; at++)
        {
            if (lines[at].Kind == SassLineKind.Code)
            {
                lines[at] = CodeOf(text, lines[at]);
            }
        }

        var ends = Ends(lines);
        var braced = new StringBuilder(text.Length + (spans.Count * 2));

        for (var at = 0; at < spans.Count; at++)
        {
            var line = lines[at];
            var span = spans[at];

            if (line.Kind == SassLineKind.Silent)
            {
                braced.Append(text, span.Start, line.First - span.Start).Append("//").Append(text, line.First, span.End - line.First);
            }
            else if (line.Kind == SassLineKind.Code && ends.TryGetValue(at, out var written))
            {
                braced.Append(text, span.Start, line.End - span.Start).Append(written).Append(text, line.End, span.End - line.End);
            }
            else
            {
                braced.Append(text, span.Start, span.End - span.Start);
            }

            if (line.Closes)
            {
                braced.Append(" */");
            }

            braced.Append(text, span.End, span.Next - span.End);
        }

        return braced.ToString();
    }

    /// <summary>
    /// Where the line at <paramref name="at"/> begins a comment, the lines it goes on over marked as its own - every line
    /// indented deeper than its first a <c>//</c>'s, a <c>/*</c>'s to the line that closes it, and its last line closing it
    /// where none does - and the index of its last line; -1 where the line begins no comment.
    /// </summary>
    private static int CommentOver(string text, SassLine[] lines, int at)
    {
        var line = lines[at];
        var written = text.AsSpan(line.First, line.End - line.First);

        if (!written.StartsWith("//", StringComparison.Ordinal) && !written.StartsWith("/*", StringComparison.Ordinal))
        {
            return -1;
        }

        var silent = written[1] == '/';
        var closed = !silent && written[2..].Contains("*/", StringComparison.Ordinal);
        var last = at;
        lines[at] = line with { Kind = SassLineKind.Comment };

        for (var next = at + 1; !closed && next < lines.Length && (lines[next].Kind == SassLineKind.Blank || lines[next].Column > line.Column); next++)
        {
            if (lines[next].Kind == SassLineKind.Blank)
            {
                continue;
            }

            closed = !silent && text.AsSpan(lines[next].First, lines[next].End - lines[next].First).Contains("*/", StringComparison.Ordinal);
            lines[next] = lines[next] with { Kind = silent ? SassLineKind.Silent : SassLineKind.Comment };
            last = next;
        }

        if (!silent && !closed)
        {
            lines[last] = lines[last] with { Closes = true };
        }

        return last;
    }

    /// <summary>
    /// What each statement's last line has written where its code ends: <c>;</c> or <c>{</c>, and a <c>}</c> for each
    /// block the next statement's dedent closes - every block at the end.
    /// </summary>
    private static Dictionary<int, string> Ends(SassLine[] lines)
    {
        // Each statement: the line it begins on, and the line it ends on.
        var statements = new List<(int First, int Last)>();

        for (var at = 0; at < lines.Length; at++)
        {
            if (lines[at].Kind != SassLineKind.Code)
            {
                continue;
            }

            var first = at;
            var depth = lines[at].Depth;

            while ((depth > 0 || lines[at].Comma) && NextCode(lines, at) is var next and >= 0)
            {
                at = next;
                depth += lines[at].Depth;
            }

            statements.Add((first, at));
        }

        var ends = new Dictionary<int, string>();
        var open = new Stack<int>();

        for (var at = 0; at < statements.Count; at++)
        {
            var column = lines[statements[at].First].Column;
            var next = at + 1 < statements.Count ? lines[statements[at + 1].First].Column : -1;
            var written = new StringBuilder(next > column ? " {" : ";");

            if (next > column)
            {
                open.Push(column);
            }

            while (open.Count > 0 && open.Peek() >= next)
            {
                written.Append(" }");
                open.Pop();
            }

            ends[statements[at].Last] = written.ToString();
        }

        return ends;
    }

    /// <summary>The index of the next line of code after <paramref name="at"/>; -1 where none is.</summary>
    private static int NextCode(SassLine[] lines, int at)
    {
        for (var next = at + 1; next < lines.Length; next++)
        {
            if (lines[next].Kind == SassLineKind.Code)
            {
                return next;
            }
        }

        return -1;
    }

    /// <summary>
    /// <paramref name="line"/>, a line of code, with where its code ends - before a comment that trails it and the white
    /// space before that - whether it ends at a comma, and how many brackets it leaves open, less those it closes: a string
    /// and an unquoted <c>url(...)</c> passed over whole.
    /// </summary>
    private static SassLine CodeOf(string text, SassLine line)
    {
        var (first, end) = (line.First, line.End);
        var depth = 0;
        var at = first;
        var stop = end;

        while (at < end)
        {
            var character = text[at];

            if (character is '"' or '\'')
            {
                at++;

                while (at < end && text[at] != character)
                {
                    at += text[at] == '\\' ? 2 : 1;
                }

                at = Math.Min(at + 1, end);
                continue;
            }

            if (at + 4 <= end && string.Compare(text, at, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                && text.IndexOf(')', at, end - at) is var close and >= 0
                && text.AsSpan(at + 4, close - at - 4).TrimStart() is var argument
                && (argument.IsEmpty || argument[0] is not ('"' or '\'')))
            {
                at = close + 1;
                continue;
            }

            if (character == '/' && at + 1 < end && text[at + 1] is '/' or '*')
            {
                stop = at;
                break;
            }

            depth += character is '(' or '[' or '{' ? 1 : character is ')' or ']' or '}' ? -1 : 0;
            at++;
        }

        while (stop > first && text[stop - 1] is ' ' or '\t')
        {
            stop--;
        }

        return line with { End = stop, Comma = stop > first && text[stop - 1] == ',', Depth = depth };
    }

    /// <summary>What a line of the indented syntax is.</summary>
    private enum SassLineKind
    {
        /// <summary>White space alone.</summary>
        Blank,

        /// <summary>Code: a statement or a part of one.</summary>
        Code,

        /// <summary>A comment's first line, or a line of a <c>/*</c> comment after it.</summary>
        Comment,

        /// <summary>A line a <c>//</c> comment goes on over, written a comment of its own.</summary>
        Silent,
    }

    /// <summary>One line of the indented syntax, as <see cref="Braced"/> reads it.</summary>
    /// <param name="Kind">What it is.</param>
    /// <param name="Column">How far its first character is indented.</param>
    /// <param name="First">Where its first character stands.</param>
    /// <param name="End">Where its code ends: its text's end, but before a comment that trails its code.</param>
    private sealed record SassLine(SassLineKind Kind, int Column, int First, int End)
    {
        /// <summary>How many brackets its code leaves open, less those it closes.</summary>
        public int Depth { get; init; }

        /// <summary>Whether its code ends at a comma: a list that goes on to the next line.</summary>
        public bool Comma { get; init; }

        /// <summary>Whether it is the last line of a <c>/*</c> comment none of whose lines closes it, which it then closes.</summary>
        public bool Closes { get; init; }
    }


    /// <summary>One run of the lexer over a text.</summary>
    private sealed class Lexing(string text, CodeLineIndex lines, StyleSyntax syntax) : ScanLexing(lines)
    {
        public void Run(int start, int end)
        {
            var at = start;

            while (at < end)
            {
                var character = text[at];

                if (char.IsWhiteSpace(character))
                {
                    at++;
                }
                else if (CommentEnd(at, end) is var comment && comment > at)
                {
                    at = Comment(at, comment);
                }
                else if (character is '"' or '\'')
                {
                    at = Literal(at, at + 1, character, end);
                }
                else if (Hole(at, end))
                {
                    at = Literal(at, at, '\0', end);
                }
                else if (Url(at, end) is var url and > 0)
                {
                    at = Literal(at, url, ')', end);
                }
                else if (NameAt(at, end) is var past and > 0)
                {
                    Add(ScanTokenKind.Name, at, past);
                    at = past;
                }
                else if (char.IsAsciiDigit(character) || (character == '.' && at + 1 < end && char.IsAsciiDigit(text[at + 1])))
                {
                    at = Number(text, at, end);
                }
                else if (Alone(character) > 0)
                {
                    Add(ScanTokenKind.Punctuation, at, at + 1);
                    at++;
                }
                else
                {
                    // A character no token begins with - a control character, a surrogate apart from its pair - is passed over.
                    at++;
                }
            }
        }

        /// <summary>A sheet's comments: <c>/* */</c> past its closer, and <c>//</c> to its line's end where the syntax writes one.</summary>
        protected override int CommentEnd(int at, int end)
            => at + 1 >= end || text[at] != '/' ? at
                : text[at + 1] == '*' ? (text.IndexOf("*/", at + 2, end - at - 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : end)
                : text[at + 1] == '/' && LineComments(syntax) ? LineEnd(text, at, end)
                : at;

        /// <summary>Whether an interpolation opens at <paramref name="at"/>: SCSS's and Sass's <c>#{</c>, Less's <c>@{</c>.</summary>
        private bool Hole(int at, int end)
            => at + 1 < end && text[at + 1] == '{' && (syntax is StyleSyntax.Scss or StyleSyntax.Sass ? text[at] == '#' : syntax == StyleSyntax.Less && text[at] == '@');

        /// <summary>Where the text of an unquoted <c>url(</c> at <paramref name="at"/> begins; 0 where none stands there - a <c>url(</c> whose argument is a string is a function.</summary>
        private int Url(int at, int end)
        {
            if (at + 4 > end || string.Compare(text, at, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return 0;
            }

            var inner = at + 4;

            while (inner < end && char.IsWhiteSpace(text[inner]))
            {
                inner++;
            }

            return inner < end && text[inner] is '"' or '\'' ? 0 : at + 4;
        }

        /// <summary>
        /// Where the name at <paramref name="at"/> ends: an identifier - a <c>-</c> or <c>--</c> before it among it - an
        /// at-rule's or a Less variable's <c>@name</c>, a variable's <c>$name</c>, and in SCSS and Sass a placeholder's
        /// <c>%name</c>; 0 where none begins there.
        /// </summary>
        private int NameAt(int at, int end)
        {
            var first = at;

            if (text[at] is '@' or '$' || (text[at] == '%' && syntax is StyleSyntax.Scss or StyleSyntax.Sass))
            {
                first = at + 1;

                if (first < end && text[first] == '-')
                {
                    first++;
                }
            }
            else if (text[at] == '-')
            {
                first = at + 1;

                // A custom property's name, -- and what follows, a bare -- among them.
                if (first < end && text[first] == '-')
                {
                    return Rest(first + 1, end);
                }
            }

            if (first >= end || NameStart(text, first, end) is not (> 0 and var width))
            {
                return 0;
            }

            return Rest(first + width, end);
        }

        /// <summary>Where a name's characters from <paramref name="at"/> end.</summary>
        private int Rest(int at, int end)
        {
            while (at < end && NamePart(text, at, end) is var width and > 0)
            {
                at += width;
            }

            return at;
        }

        /// <summary>
        /// A string, an unquoted <c>url(...)</c> or an interpolation beginning at <paramref name="start"/>, its text at
        /// <paramref name="from"/>: to <paramref name="close"/> not escaped - a quote, a <c>url(</c>'s parenthesis, or for an
        /// interpolation outside a string (<c>'\0'</c>) the brace that closes its code - past each interpolation it holds,
        /// whose code may hold braces, comments and strings of their own, read with a stack of what it is in, never a call
        /// into itself, so no nesting reaches past the reader's stack. A line break ends one left open, and what holds it
        /// with it. Answers where the text goes on.
        /// </summary>
        private int Literal(int start, int from, char close, int end)
        {
            var literals = new LiteralStack<char>(this, start);
            literals.OpenLiteral(close, from);
            var at = from;

            if (close == '\0')
            {
                at += 2;
                literals.OpenCode(from, at);
            }

            while (at < end && literals.Open)
            {
                var character = text[at];

                if (literals.InCode)
                {
                    if (literals.Past(character, at, end) is var past && past > at)
                    {
                        at = past;
                    }
                    else if (character is '"' or '\'')
                    {
                        literals.OpenLiteral(character, at + 1);
                        at++;
                    }
                    else
                    {
                        at++;
                    }

                    continue;
                }

                var top = literals.Mark;

                if (top == '\0')
                {
                    // The interpolation's code closed: so is the token.
                    literals.CloseLiteral(at);
                }
                else if (character == '\\')
                {
                    at += 2;
                }
                else if (Hole(at, end))
                {
                    literals.OpenCode(at, at + 2);
                    at += 2;
                }
                else if (character == top)
                {
                    literals.CloseLiteral(at);
                    at++;
                }
                else if (LineBreakAt(text, at, end) > 0)
                {
                    break;
                }
                else
                {
                    at++;
                }
            }

            at = Math.Min(at, end);
            literals.End(at);
            Add(ScanTokenKind.Text, start, at);
            return at;
        }
    }
}
