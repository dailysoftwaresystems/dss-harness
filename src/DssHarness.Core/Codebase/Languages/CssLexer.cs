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
    /// <param name="closes">Where the scan says each interpolation it reads closes, for the scans of their code after it; none where nothing scans it again.</param>
    public static InterpolatedText Scan(CodeText text, StyleSyntax syntax, Closes? closes = null) => Scan(text, 0, text.Length, text.LineIndex, syntax, closes);

    /// <summary>
    /// Scans the part of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/> by the rules of
    /// <paramref name="syntax"/>, its tokens placed in the whole text: an interpolation's code, read where it stands.
    /// </summary>
    /// <param name="text">A sheet's text.</param>
    /// <param name="start">Where the part begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="lines">The text's lines.</param>
    /// <param name="syntax">What it is written in.</param>
    /// <param name="closes">
    /// Where a scan of the text read before this one found each interpolation it holds closes: each this scan reads is
    /// passed over to its close at once, its code read by the scan of its own; none where nothing scanned it before.
    /// </param>
    public static InterpolatedText Scan(CodeText text, long start, long end, CodeLineIndex lines, StyleSyntax syntax, Closes? closes = null)
    {
        var lexing = new Lexing(text, lines, syntax, closes ?? new Closes());
        lexing.Run(start, end);
        return new InterpolatedText(new ScannedText(text, lexing.Tokens, lexing.Comments, literals: lexing.Literals), lexing.Interpolations, lines);
    }

    /// <summary>
    /// Where each brace an interpolation's code opens closes - the interpolation's own among them, and each nested in it to
    /// any depth - as the first scan of a sheet finds them: the scan of an interpolation's code, read in its turn, passes
    /// over each interpolation that code holds to its close at once, so a character of the sheet is read by the scan of
    /// its own interpolation and the first scan alone, never again by the scan of each interpolation outside it.
    /// </summary>
    public sealed class Closes
    {
        private readonly Dictionary<long, long> _at = [];

        /// <summary>Says the brace at <paramref name="open"/> closes at <paramref name="close"/> - where the reading ended, where nothing closes it.</summary>
        internal void Closed(long open, long close) => _at[open] = close;

        /// <summary>Where the brace at <paramref name="open"/> closes, as a scan before found it; -1 where none found it.</summary>
        internal long Of(long open) => _at.TryGetValue(open, out var close) ? close : -1;
    }

    /// <summary>Whether <paramref name="syntax"/> writes <c>//</c> comments.</summary>
    private static bool LineComments(StyleSyntax syntax) => syntax is not StyleSyntax.Css;

    /// <summary>
    /// Where the comment that begins at <paramref name="at"/> ends, within <paramref name="end"/>, by the rules of
    /// <paramref name="syntax"/>: a <c>/* */</c> past its closer - the end where none closes it - and a <c>//</c> at its
    /// line's end where the syntax writes one; <paramref name="at"/> where none begins there. The one reading of a sheet's
    /// comments, which its code, the code its interpolations hold and the indented syntax's lines are each read by.
    /// </summary>
    /// <param name="text">A sheet's text.</param>
    /// <param name="at">Where a comment may begin.</param>
    /// <param name="end">Where the text read ends.</param>
    /// <param name="syntax">What it is written in.</param>
    public static long CommentEnd(CodeText text, long at, long end, StyleSyntax syntax)
        => at + 1 >= end || text[at] != '/' ? at
            : text[at + 1] == '*' ? (text.Find("*/", at + 2, end) is var close and >= 0 ? close + 2 : end)
            : text[at + 1] == '/' && LineComments(syntax) ? LineEndOf(text, at, end)
            : at;

    /// <summary>Where the line <paramref name="at"/> is on ends, within <paramref name="end"/>: at its line break.</summary>
    private static long LineEndOf(CodeText text, long at, long end)
    {
        while (at < end && ScanLexing.LineBreakAt(text, at, end) == 0)
        {
            at++;
        }

        return at;
    }

    /// <summary>Whether <paramref name="character"/>, past ASCII, stands in a name: any but a surrogate, which stands in one only with its pair.</summary>
    private static bool Wide(char character) => character > '\u007f' && !char.IsSurrogate(character);

    /// <summary>
    /// How many characters at <paramref name="at"/> a name holds as one of its own past its first: a letter, a digit,
    /// <c>_</c>, <c>-</c>, a character past ASCII - a surrogate with its pair, two - or an escape, its backslash and what it
    /// escapes; 0 where none stands there.
    /// </summary>
    private static long NamePart(CodeText text, long at, long end)
    {
        var character = text[at];

        if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-' || Wide(character))
        {
            return 1;
        }

        return ScanLiteral.CssEscape(text, at, end) is var escape and > 0 ? escape : Pair(text, at, end);
    }

    /// <summary>How many characters at <paramref name="at"/> may begin a name: a letter, <c>_</c>, a character past ASCII, or an escape; 0 where none stands there.</summary>
    private static long NameStart(CodeText text, long at, long end)
    {
        var character = text[at];

        if (char.IsAsciiLetter(character) || character == '_' || Wide(character))
        {
            return 1;
        }

        return ScanLiteral.CssEscape(text, at, end) is var escape and > 0 ? escape : Pair(text, at, end);
    }

    /// <summary>2 where a surrogate pair stands at <paramref name="at"/>, else 0.</summary>
    private static long Pair(CodeText text, long at, long end)
        => char.IsHighSurrogate(text[at]) && at + 1 < end && char.IsLowSurrogate(text[at + 1]) ? 2 : 0;

    /// <summary>
    /// <paramref name="name"/> as CSS reads it, each escape the character it stands for (<see cref="ScanEscapes.Css"/>, the
    /// one rule a sheet's strings are decoded by too): <c>md\:flex</c> is the class <c>md:flex</c> markup writes, and
    /// <c>a\ b</c> the class <c>a b</c>.
    /// </summary>
    /// <param name="name">A name as a sheet writes it.</param>
    public static string Unescaped(string name) => ScanLiteral.Decode(name, 0, name.Length, ScanEscapes.Css, 0, lineStart: false);

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
    public static CodeText Braced(CodeText text)
    {
        var index = text.LineIndex;
        var lines = new SassLine[index.Count];

        for (var at = 0; at < lines.Length; at++)
        {
            var (start, end) = index.Span(text, at + 1);
            var first = start;

            while (first < end && text[first] is ' ' or '\t')
            {
                first++;
            }

            lines[at] = new SassLine(first == end ? SassLineKind.Blank : SassLineKind.Code, first - start, first, end);
        }

        for (var at = 0; at < lines.Length; at++)
        {
            if (lines[at].Kind == SassLineKind.Code && CommentOver(text, index, lines, at) is var last and >= 0)
            {
                at = last;
            }
        }

        for (var at = 0; at < lines.Length; at++)
        {
            if (lines[at].Kind == SassLineKind.Code)
            {
                lines[at] = CodeOf(text, index, lines[at]);
            }
        }

        var ends = Ends(lines);
        var braced = new CodeTextPieces();

        // Each line of the sheet a run or two of it, what the braces add written between them: no copy of its text.
        for (var at = 0; at < lines.Length; at++)
        {
            var line = lines[at];
            var (start, end) = index.Span(text, at + 1);
            var next = at + 1 < lines.Length ? index.StartOf(at + 2) : text.Length;

            if (line.Kind == SassLineKind.Silent)
            {
                braced.Run(start, line.First).Write("//").Run(line.First, end);
            }
            else if (line.Kind == SassLineKind.Code && ends.TryGetValue(at, out var written))
            {
                braced.Run(start, line.End).Write(written).Run(line.End, end);
            }
            else
            {
                braced.Run(start, end);
            }

            if (line.Closes)
            {
                braced.Write(" */");
            }

            braced.Run(end, next);
        }

        return text.Composed(braced.Pieces);
    }

    /// <summary>
    /// Where the line at <paramref name="at"/> begins a comment, as the lexer reads one
    /// (<see cref="CommentEnd(CodeText, long, long, StyleSyntax)"/>), the lines it goes on over marked as its own - every line
    /// indented deeper than its first a <c>//</c>'s, a <c>/*</c>'s to the line its closer stands on, and its last line
    /// closing it where none of them does - and the index of its last line; -1 where the line begins no comment.
    /// </summary>
    private static int CommentOver(CodeText text, CodeLineIndex index, SassLine[] lines, int at)
    {
        var line = lines[at];
        var end = CommentEnd(text, line.First, text.Length, StyleSyntax.Sass);

        if (end == line.First)
        {
            return -1;
        }

        var silent = text[line.First + 1] == '/';

        // The line a /* comment's closer stands on; -1 where nothing closes it.
        var closes = !silent && end - line.First >= 4 && text[end - 2] == '*' && text[end - 1] == '/' ? index.LineAt(end - 1) - 1 : -1;
        var closed = closes == at;
        var last = at;
        lines[at] = line with { Kind = SassLineKind.Comment };

        for (var next = at + 1; !closed && next < lines.Length && (lines[next].Kind == SassLineKind.Blank || lines[next].Column > line.Column); next++)
        {
            if (lines[next].Kind == SassLineKind.Blank)
            {
                continue;
            }

            closed = next == closes;
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
        var open = new Stack<long>();

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
    /// <paramref name="line"/>, a line of code, as the lexer reads it on its own: where its code ends - its last token's
    /// end, before a comment that trails it - whether it ends at a comma, and how many brackets it leaves open, less those
    /// it closes, so a string, an unquoted <c>url(...)</c>, an interpolation, a comment and an escaped character - a
    /// <c>\[</c>, a <c>\'</c> - are read as the sheet's reader reads them; blank where it holds no token.
    /// </summary>
    private static SassLine CodeOf(CodeText text, CodeLineIndex index, SassLine line)
    {
        var scan = Scan(text, line.First, line.End, index, StyleSyntax.Sass).Scan;

        if (scan.Count == 0)
        {
            return line with { Kind = SassLineKind.Blank };
        }

        var depth = 0;

        for (var at = 0; at < scan.Count; at++)
        {
            depth += scan.Is(at, "(") || scan.Is(at, "[") || scan.Is(at, "{") ? 1 : scan.Is(at, ")") || scan.Is(at, "]") || scan.Is(at, "}") ? -1 : 0;
        }

        return line with { End = scan[scan.Count - 1].End, Comma = scan.Is(scan.Count - 1, ","), Depth = depth };
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
    private sealed record SassLine(SassLineKind Kind, long Column, long First, long End)
    {
        /// <summary>How many brackets its code leaves open, less those it closes.</summary>
        public int Depth { get; init; }

        /// <summary>Whether its code ends at a comma: a list that goes on to the next line.</summary>
        public bool Comma { get; init; }

        /// <summary>Whether it is the last line of a <c>/*</c> comment none of whose lines closes it, which it then closes.</summary>
        public bool Closes { get; init; }
    }


    /// <summary>One run of the lexer over a text.</summary>
    private sealed class Lexing(CodeText text, CodeLineIndex lines, StyleSyntax syntax, Closes closes) : ScanLexing(lines)
    {
        public void Run(long start, long end)
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

        /// <summary>A sheet's comments, as <see cref="CssLexer.CommentEnd(CodeText, long, long, StyleSyntax)"/> reads them.</summary>
        protected override long CommentEnd(long at, long end) => CssLexer.CommentEnd(text, at, end, syntax);

        /// <summary>Whether an interpolation opens at <paramref name="at"/>: SCSS's and Sass's <c>#{</c>, Less's <c>@{</c>.</summary>
        private bool Hole(long at, long end)
            => at + 1 < end && text[at + 1] == '{' && (syntax is StyleSyntax.Scss or StyleSyntax.Sass ? text[at] == '#' : syntax == StyleSyntax.Less && text[at] == '@');

        /// <summary>Where the text of an unquoted <c>url(</c> at <paramref name="at"/> begins; 0 where none stands there - a <c>url(</c> whose argument is a string is a function.</summary>
        private long Url(long at, long end)
        {
            if (at + 4 > end || !text.At(at, "url(", StringComparison.OrdinalIgnoreCase))
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
        private long NameAt(long at, long end)
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
        private long Rest(long at, long end)
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
        /// into itself, so no nesting reaches past the reader's stack - and passed over to its close at once where a scan
        /// before this one found it (<see cref="Closes"/>), each brace's close said for the scans after. A line break not
        /// escaped - a CR LF one break - ends one left open, and what holds it with it. A string and a <c>url(...)</c> are kept as literals (<see cref="ScanLiteral"/>),
        /// each constant part between its opener, its interpolations and its closer, decoded by a sheet's escapes - a
        /// <c>url(...)</c>'s without the white space about its address - and an interpolation outside a string none. Answers
        /// where the text goes on.
        /// </summary>
        private long Literal(long start, long from, char close, long end)
        {
            var literals = new LiteralStack<char>(this, start);
            literals.OpenLiteral(close, from);
            var at = from;

            // The braces of the code being read still open, innermost last.
            var braces = new Stack<long>();

            if (close == '\0')
            {
                at += 2;
                literals.OpenCode(from, at);
                at = Opened(braces, from + 1, at, end);
            }

            while (at < end && literals.Open)
            {
                var character = text[at];

                if (literals.InCode)
                {
                    if (literals.Past(character, at, end) is var past && past > at)
                    {
                        if (past == at + 1 && character == '{')
                        {
                            braces.Push(at);
                        }
                        else if (past == at + 1 && character == '}' && braces.TryPop(out var open))
                        {
                            closes.Closed(open, at);
                        }

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
                    // The backslash and what it escapes: a line break whole, a CR LF one.
                    at += 1 + Math.Max(1, LineBreakAt(text, at + 1, end));
                }
                else if (Hole(at, end))
                {
                    literals.OpenCode(at, at + 2);
                    at = Opened(braces, at + 1, at + 2, end);
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

            // A brace nothing closed closes where the reading ended.
            while (braces.TryPop(out var open))
            {
                closes.Closed(open, at);
            }

            if (close != '\0')
            {
                KeepLiteral(start, new ScanLiteral(close == ')' ? Trimmed(literals.Parts) : literals.Parts, ScanEscapes.Css) { Interpolated = literals.Holes, LeftOpen = literals.Open });
            }

            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>
        /// The interpolation whose brace stands at <paramref name="brace"/> opened, its code at <paramref name="code"/>: where
        /// the reading goes on - at its close, where a scan before this one found it (<see cref="Closes"/>), so its code is
        /// read by the scan of its own alone; at its code where none did - its brace held open till its close is read.
        /// </summary>
        private long Opened(Stack<long> braces, long brace, long code, long end)
        {
            braces.Push(brace);
            return closes.Of(brace) is var close and >= 0 ? Math.Min(Math.Max(close, code), end) : code;
        }

        /// <summary>The parts of an unquoted <c>url(...)</c>, the white space before its address and after it taken off, which CSS reads as none of it.</summary>
        private List<ScanLiteralPart> Trimmed(IReadOnlyList<ScanLiteralPart> parts)
        {
            var trimmed = parts.ToList();

            if (trimmed.Count > 0)
            {
                var first = trimmed[0];
                var start = first.Start;

                while (start < first.End && char.IsWhiteSpace(text[start]))
                {
                    start++;
                }

                trimmed[0] = Part(start, first.End);
                var last = trimmed[^1];
                var stop = last.End;

                while (stop > last.Start && char.IsWhiteSpace(text[stop - 1]))
                {
                    stop--;
                }

                trimmed[^1] = last with { End = stop };
            }

            return trimmed;
        }
    }
}
