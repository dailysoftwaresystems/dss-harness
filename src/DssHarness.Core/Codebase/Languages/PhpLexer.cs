using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The lexical rules of PHP for the structural scanner: code only between <c>&lt;?php</c>, <c>&lt;?=</c> or a short
/// <c>&lt;?</c> and <c>?&gt;</c> - kept as a token, since it ends a statement - the rest of the file text no token holds;
/// comments (<c>//</c>, <c>#</c>, <c>/* */</c>) kept apart, a line's ending at its line or at <c>?&gt;</c>; an attribute's
/// <c>#[</c> a <c>#</c> before its bracket; a string - in single or double quotes, in backticks, a heredoc or a nowdoc - as
/// one token, never read as code, but where it holds code (<c>{$...}</c>, <c>${...}</c>, a bare <c>$name</c>), which the
/// lexer says, so it is read in its turn as a text of its own, and where its value's constant parts are written - between
/// its quotes or its labels and its holes, a heredoc's and a nowdoc's closing indentation no part of them - and how they
/// are decoded (<see cref="ScanLiteral"/>): by the escapes its form reads, a nowdoc's as written; a variable
/// (<c>$name</c>, <c>$$name</c>) one name; a name with PHP's separator
/// of namespaces (<c>App\Models\User</c>, <c>\strlen</c>) one name; numbers with their separators; and <c>?-&gt;</c>,
/// <c>-&gt;</c>, <c>::</c>, <c>=&gt;</c>, <c>...</c> and the comparisons and assignments of more than one character whole.
/// </summary>
internal static class PhpLexer
{
    /// <summary>The name after whose call nothing is code: PHP stops compiling there.</summary>
    private const string HaltCompiler = "__halt_compiler";

    /// <summary>The punctuators read whole, the longest first.</summary>
    private static readonly string[] Whole =
    [
        "?->", "<=>", "===", "!==", "**=", "??=", "...", "->", "::", "=>", "??", "==", "!=", "<>", "<=", ">=", "**", ".=", "+=", "-=", "*=", "/=", "%=",
        "&=", "|=", "^=", "&&", "||", "?>",
    ];

    /// <summary>Scans <paramref name="text"/> by PHP's rules, from its text before the first open tag.</summary>
    /// <param name="text">A file's text.</param>
    public static InterpolatedText Scan(string text) => Scan(text, 0, text.Length, CodeFileText.LineIndex(text), code: false);

    /// <summary>
    /// Scans the part of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/> by PHP's rules, as
    /// code, its tokens placed in the whole text: an interpolation's code, read where it stands.
    /// </summary>
    /// <param name="text">A file's text.</param>
    /// <param name="start">Where the part begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="lines">The text's lines.</param>
    /// <param name="closes">
    /// What the scans of the text before this one read of its literals - the <see cref="InterpolatedText.Closes"/> of the
    /// scan whose literal holds the part - so each literal the part holds that they read is passed over at once; none for
    /// a scan of its own.
    /// </param>
    public static InterpolatedText Scan(string text, int start, int end, CodeLineIndex lines, ScanCloses? closes = null) => Scan(text, start, end, lines, code: true, closes);

    private static InterpolatedText Scan(string text, int start, int end, CodeLineIndex lines, bool code, ScanCloses? closes = null)
    {
        var lexing = new Lexing(text, lines, closes);
        lexing.Closes.Scanned(start, end);
        lexing.Run(start, end, code);
        return new InterpolatedText(new ScannedText(text, lexing.Tokens, lexing.Comments, closer: "?>", literals: lexing.Literals), lexing.Interpolations, lines) { Closes = lexing.Closes };
    }

    /// <summary>Whether <paramref name="character"/> may begin a PHP name: a letter - any past ASCII among them - or <c>_</c>, and no <c>$</c>, which only a variable's name is written after.</summary>
    private static bool StartsName(char character) => char.IsLetter(character) || character == '_' || character > '\u007f';

    /// <summary>Whether <paramref name="character"/> may stand in a PHP name past its first: a letter, a digit or <c>_</c>.</summary>
    private static bool InName(char character) => char.IsLetterOrDigit(character) || character == '_' || character > '\u007f';

    /// <summary>One run of the lexer over a text.</summary>
    private sealed class Lexing(string text, CodeLineIndex lines, ScanCloses? closes) : ScanLexing(lines, closes)
    {
        public void Run(int start, int end, bool code)
        {
            var at = code ? start : Opened(start, end);

            while (at < end)
            {
                var character = text[at];

                if (char.IsWhiteSpace(character))
                {
                    at++;
                }
                else if (character == '?' && at + 1 < end && text[at + 1] == '>')
                {
                    // The code ends here, and its statement with it: what follows is text up to the next open tag.
                    Add(ScanTokenKind.Punctuation, at, at + 2);
                    at = Opened(at + 2, end);
                }
                else if (character == '#' && at + 1 < end && text[at + 1] == '[')
                {
                    Add(ScanTokenKind.Punctuation, at, at + 1);
                    at++;
                }
                else if (CommentEnd(at, end) is var comment && comment > at)
                {
                    at = Comment(at, comment);
                }
                else if (character == '<' && at + 2 < end && text[at + 1] == '<' && text[at + 2] == '<' && Heredoc(at, end) is var past and > 0)
                {
                    at = past;
                }
                else if (character == '$' && at + 1 < end && (StartsName(text[at + 1]) || text[at + 1] == '$'))
                {
                    var name = at + 1;

                    while (name < end && text[name] == '$')
                    {
                        name++;
                    }

                    at = name < end && StartsName(text[name]) ? Name(at, name, end) : Punctuator(at, end);
                }
                else if (StartsName(character) || (character == '\\' && at + 1 < end && StartsName(text[at + 1])))
                {
                    var name = at;
                    at = Name(at, character == '\\' ? at + 1 : at, end);

                    // __halt_compiler(); ends the code, and the file's text with it: what follows is data - a phar's archive.
                    if (at - name == HaltCompiler.Length && string.Compare(text, name, HaltCompiler, 0, HaltCompiler.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        at = Halted(at, end);
                    }
                }
                else if (char.IsAsciiDigit(character) || (character == '.' && at + 1 < end && char.IsAsciiDigit(text[at + 1])))
                {
                    at = Number(text, at, end);
                }
                else if (character is '\'' or '"' or '`')
                {
                    at = Literal(at, at + 1, character, end, end);
                }
                else
                {
                    at = Punctuator(at, end);
                }
            }
        }

        /// <summary>Past the text from <paramref name="at"/> to the next open tag - <c>&lt;?php</c>, <c>&lt;?=</c>, or <c>&lt;?</c> before white space - where the code begins; the end where none is.</summary>
        private int Opened(int at, int end)
        {
            for (var open = text.IndexOf("<?", at, end - at, StringComparison.Ordinal); open >= 0; open = text.IndexOf("<?", open + 2, end - open - 2, StringComparison.Ordinal))
            {
                var after = open + 2;

                if (after + 3 <= end && string.Compare(text, after, "php", 0, 3, StringComparison.OrdinalIgnoreCase) == 0 && (after + 3 == end || char.IsWhiteSpace(text[after + 3])))
                {
                    return after + 3;
                }

                if (after < end && text[after] == '=')
                {
                    return after + 1;
                }

                if (after == end || char.IsWhiteSpace(text[after]))
                {
                    return after;
                }
            }

            return end;
        }

        /// <summary>
        /// Past the parentheses and the <c>;</c> or <c>?&gt;</c> after <c>__halt_compiler</c> at <paramref name="at"/>, each a
        /// token: the end, where nothing after is read; <paramref name="at"/> where they are not there, and reading goes on.
        /// </summary>
        private int Halted(int at, int end)
        {
            var index = at;
            var tokens = Tokens.Count;

            foreach (var expected in (string[])["(", ")"])
            {
                while (index < end && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                if (index >= end || text[index] != expected[0])
                {
                    Tokens.RemoveRange(tokens, Tokens.Count - tokens);
                    return at;
                }

                Add(ScanTokenKind.Punctuation, index, index + 1);
                index++;
            }

            while (index < end && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index < end && text[index] == ';')
            {
                Add(ScanTokenKind.Punctuation, index, index + 1);
                return end;
            }

            if (index + 1 < end && text[index] == '?' && text[index + 1] == '>')
            {
                Add(ScanTokenKind.Punctuation, index, index + 2);
                return end;
            }

            Tokens.RemoveRange(tokens, Tokens.Count - tokens);
            return at;
        }

        /// <summary>
        /// PHP's comments: <c>#</c> - but an attribute's <c>#[</c> - and <c>//</c> to their line's end or the <c>?&gt;</c>
        /// before it (<see cref="LineCommentEnd"/>), <c>/* */</c> past its closer - in an interpolation's code too, which
        /// PHP reads as any code.
        /// </summary>
        protected override int CommentEnd(int at, int end)
            => at >= end ? at
                : (text[at] == '#' && !(at + 1 < end && text[at + 1] == '[')) || (text[at] == '/' && at + 1 < end && text[at + 1] == '/') ? LineCommentEnd(at, end)
                : text[at] == '/' && at + 1 < end && text[at + 1] == '*' ? (text.IndexOf("*/", at + 2, end - at - 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : end)
                : at;

        /// <summary>Where a line comment at <paramref name="at"/> ends: at its line's break, or before the <c>?&gt;</c> that ends the code.</summary>
        private int LineCommentEnd(int at, int end)
        {
            var lineEnd = LineEnd(text, at, end);
            var close = text.IndexOf("?>", at, lineEnd - at, StringComparison.Ordinal);
            return close >= 0 ? close : lineEnd;
        }

        /// <summary>
        /// A name from <paramref name="start"/>, its first character at <paramref name="first"/>: a variable's <c>$</c>s before
        /// it, and each further part a <c>\</c> joins (<c>App\Models\User</c>), but one before a group's brace
        /// (<c>use App\{A, B}</c>), which ends it there. Answers where the text goes on.
        /// </summary>
        private int Name(int start, int first, int end)
        {
            var at = first + 1;

            while (true)
            {
                while (at < end && InName(text[at]))
                {
                    at++;
                }

                if (text[start] != '$' && at + 1 < end && text[at] == '\\' && StartsName(text[at + 1]))
                {
                    at += 2;
                    continue;
                }

                break;
            }

            Add(ScanTokenKind.Name, start, at);
            return at;
        }

        /// <summary>A punctuator at <paramref name="at"/>, one of <see cref="Whole"/> or one character; a character no token begins with passed over. Answers where the text goes on.</summary>
        private int Punctuator(int at, int end)
        {
            foreach (var whole in Whole)
            {
                if (at + whole.Length <= end && string.CompareOrdinal(text, at, whole, 0, whole.Length) == 0)
                {
                    Add(ScanTokenKind.Punctuation, at, at + whole.Length);
                    return at + whole.Length;
                }
            }

            if (Alone(text[at]) > 0)
            {
                Add(ScanTokenKind.Punctuation, at, at + 1);
            }

            return at + 1;
        }

        /// <summary>
        /// A heredoc or a nowdoc at <paramref name="at"/>'s <c>&lt;&lt;&lt;</c>: its label - bare, in double quotes, or a
        /// nowdoc's in single ones - and its lines to the one that begins, past white space, with the label and no name's
        /// character after it, one token; a heredoc's interpolations said as a double-quoted string's are. Answers where the
        /// text goes on; 0 where no label follows, so the <c>&lt;</c>s are punctuators. One a scan of the text before this
        /// one read is taken as it was read (<see cref="ScanCloses"/>).
        /// </summary>
        private int Heredoc(int at, int end)
        {
            var label = at + 3;

            while (label < end && text[label] is ' ' or '\t')
            {
                label++;
            }

            var quote = label < end && text[label] is '\'' or '"' ? text[label] : '\0';

            if (Closes.Of(at, end) is { Tags: null } read)
            {
                KeepLiteral(at, new ScanLiteral(read.Parts, quote == '\'' ? ScanEscapes.None : ScanEscapes.PhpHeredoc)
                {
                    Interpolated = read.Holes,
                    Indent = read.Indent,
                    Content = read.Content,
                    LeftOpen = read.LeftOpen,
                });

                foreach (var (from, to) in read.Spans)
                {
                    Interpolated(at, from, to);
                }

                Add(ScanTokenKind.Text, at, read.End);
                return read.End;
            }
            var name = quote == '\0' ? label : label + 1;

            if (name >= end || !StartsName(text[name]))
            {
                return 0;
            }

            var past = name;

            while (past < end && InName(text[past]))
            {
                past++;
            }

            var written = text[name..past];

            if (quote != '\0')
            {
                if (past >= end || text[past] != quote)
                {
                    return 0;
                }

                past++;
            }

            if (LineBreakAt(text, past, end) == 0)
            {
                return 0;
            }

            var body = past + LineBreakAt(text, past, end);
            var close = end;
            var closeEnd = end;
            var indent = 0;

            for (var line = body; line < end;)
            {
                var first = line;

                while (first < end && text[first] is ' ' or '\t')
                {
                    first++;
                }

                if (first + written.Length <= end && string.CompareOrdinal(text, first, written, 0, written.Length) == 0
                    && (first + written.Length == end || !InName(text[first + written.Length])))
                {
                    close = line;
                    closeEnd = first + written.Length;
                    indent = first - line;
                    break;
                }

                var lineEnd = LineEnd(text, line, end);
                line = lineEnd + LineBreakAt(text, lineEnd, end);

                if (lineEnd >= end)
                {
                    break;
                }
            }

            // The value runs to the line break before the closing label's line, the closing label's indentation taken
            // off each of its lines.
            var content = close > body && close < end ? close - BreakBefore(close, body) : close;

            if (quote == '\'')
            {
                var parts = (ScanLiteralPart[])[Part(body, content)];
                KeepLiteral(at, new ScanLiteral(parts, ScanEscapes.None) { Indent = indent, Content = body, LeftOpen = close == end });
                Closes.Keep(at, new ScanLiteralRead(closeEnd, parts, []) { Indent = indent, Content = body, LeftOpen = close == end });
                Add(ScanTokenKind.Text, at, closeEnd);
                return closeEnd;
            }

            Literal(at, body, '\0', content, end, indent, leftOpen: close == end, closeEnd);
            Add(ScanTokenKind.Text, at, closeEnd);
            return closeEnd;
        }

        /// <summary>How wide the line break that ends just before <paramref name="at"/> is, no further back than <paramref name="floor"/>: 2 for CR LF, 1 for CR or LF, 0 where none does.</summary>
        private int BreakBefore(int at, int floor)
            => at - 2 >= floor && text[at - 2] == '\r' && text[at - 1] == '\n' ? 2
                : at - 1 >= floor && text[at - 1] is '\r' or '\n' ? 1
                : 0;

        /// <summary>
        /// A string beginning at <paramref name="start"/>, its text at <paramref name="from"/>: in single quotes to the one
        /// that closes it, only <c>\\</c> and <c>\'</c> escaped; in double quotes or backticks - or a heredoc's lines, to
        /// <paramref name="stop"/>, where <paramref name="mark"/> is none, <paramref name="indent"/> blanks of each line no
        /// part of its value - to its closing quote, past each escape and each interpolation whose code it says
        /// (<c>{$...}</c> from its <c>$</c>, <c>${...}</c> after its brace, a bare <c>$name</c> as
        /// <see cref="ScanLexing.BareHole"/> reads it), code that may hold braces, comments and strings of its own, read
        /// with a stack of what it is in; its value's parts between its quotes and its holes. A heredoc's token is its
        /// caller's, ending at <paramref name="closeEnd"/>, and so is whether its label closed it
        /// (<paramref name="leftOpen"/>); any other's is added. A string a scan of the text before this one read is taken
        /// as it was read (<see cref="ScanCloses"/>), and passed over at once; what this reading finds of it, and of each
        /// string its code holds, is kept for the scans of that code. Answers where the text goes on.
        /// </summary>
        private int Literal(int start, int from, char mark, int stop, int end, int indent = 0, bool leftOpen = false, int closeEnd = -1)
        {
            if (mark != '\0' && Closes.Of(start, end) is { Tags: null } read)
            {
                KeepLiteral(start, mark == '\''
                    ? new ScanLiteral(read.Parts, ScanEscapes.PhpSingle) { LeftOpen = read.LeftOpen }
                    : new ScanLiteral(read.Parts, mark == '"' ? ScanEscapes.PhpDouble : ScanEscapes.PhpBacktick) { Interpolated = read.Holes, Content = from, LeftOpen = read.LeftOpen });

                foreach (var (code, past) in read.Spans)
                {
                    Interpolated(start, code, past);
                }

                Add(ScanTokenKind.Text, start, read.End);
                return read.End;
            }

            if (mark == '\'')
            {
                var at = from;

                while (at < end && text[at] != '\'')
                {
                    at += text[at] == '\\' && at + 1 < end ? 2 : 1;
                }

                KeepLiteral(start, new ScanLiteral([Part(from, Math.Min(at, end))], ScanEscapes.PhpSingle) { LeftOpen = at >= end });
                at = Math.Min(at + 1, end);
                Add(ScanTokenKind.Text, start, at);
                return at;
            }

            var literals = new LiteralStack<char>(this, start);
            literals.OpenLiteral(mark, start, from);
            var index = from;

            while (index < stop && literals.Open)
            {
                var character = text[index];

                if (literals.InCode)
                {
                    if (literals.Past(character, index, stop) is var past && past > index)
                    {
                        index = past;
                        continue;
                    }

                    if (character is '\'' or '"')
                    {
                        literals.OpenLiteral(character, index, index + 1);
                    }

                    index++;
                    continue;
                }

                var top = literals.Mark;

                if (character == '\\')
                {
                    index += 2;
                }
                else if (top != '\'' && character == '$' && index + 1 < stop && text[index + 1] == '{' && VariableNamed(index + 2, stop) is var named and > 0)
                {
                    // ${name}: the variable $name, read by its name - no code the graph reads.
                    literals.Hole(index, named);
                    index = named;
                }
                else if (top != '\'' && ((character == '{' && index + 1 < stop && text[index + 1] == '$') || (character == '$' && index + 1 < stop && text[index + 1] == '{')))
                {
                    // {$x}: the $ is the code's own; ${x}: the code is after the brace.
                    literals.OpenCode(index, character == '{' ? index + 1 : index + 2);
                    index += character == '{' ? 1 : 2;
                }
                else if (top != '\'' && character == '$' && BareHole(text, index, stop, variable: true) is { } hole)
                {
                    // A bare $name: a run of code of its own, a variable the string reads.
                    literals.Hole(index, hole.Start, hole.End, hole.Past);
                    index = hole.Past;
                }
                else if (top != '\0' && character == top)
                {
                    literals.CloseLiteral(index, index + 1);
                    index++;
                }
                else
                {
                    index++;
                }
            }

            index = Math.Min(index, stop);
            literals.End(index);

            var escapes = mark switch { '"' => ScanEscapes.PhpDouble, '`' => ScanEscapes.PhpBacktick, _ => ScanEscapes.PhpHeredoc };
            var open = mark == '\0' ? leftOpen : literals.Open;
            KeepLiteral(start, new ScanLiteral(literals.Parts, escapes) { Interpolated = literals.Holes, Indent = indent, Content = from, LeftOpen = open });
            Closes.Keep(start, new ScanLiteralRead(mark == '\0' ? closeEnd : index, literals.Parts, literals.Spans) { Holes = literals.Holes, Indent = indent, Content = from, LeftOpen = open });

            if (mark != '\0')
            {
                Add(ScanTokenKind.Text, start, index);
            }

            return index;
        }

        /// <summary>Past the <c>}</c> of a <c>${name}</c> whose name begins at <paramref name="at"/>: a name alone, then the brace; 0 where anything else stands.</summary>
        private int VariableNamed(int at, int stop)
        {
            if (at >= stop || !StartsName(text[at]))
            {
                return 0;
            }

            var past = at + 1;

            while (past < stop && InName(text[past]))
            {
                past++;
            }

            return past < stop && text[past] == '}' ? past + 1 : 0;
        }
    }
}
