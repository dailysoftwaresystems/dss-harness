using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The lexical rules of PHP for the structural scanner: code only between <c>&lt;?php</c>, <c>&lt;?=</c> or a short
/// <c>&lt;?</c> and <c>?&gt;</c> - kept as a token, since it ends a statement - the rest of the file text no token holds;
/// comments (<c>//</c>, <c>#</c>, <c>/* */</c>) kept apart, a line's ending at its line or at <c>?&gt;</c>; an attribute's
/// <c>#[</c> a <c>#</c> before its bracket; a string - in single or double quotes, in backticks, a heredoc or a nowdoc - as
/// one token, never read as code, but where it holds code (<c>{$...}</c>, <c>${...}</c>), which the lexer says, so it is
/// read in its turn as a text of its own; a variable (<c>$name</c>, <c>$$name</c>) one name; a name with PHP's separator
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
    public static InterpolatedText Scan(string text, int start, int end, CodeLineIndex lines) => Scan(text, start, end, lines, code: true);

    private static InterpolatedText Scan(string text, int start, int end, CodeLineIndex lines, bool code)
    {
        var lexing = new Lexing(text, lines);
        lexing.Run(start, end, code);
        return new InterpolatedText(new ScannedText(text, lexing.Tokens, lexing.Comments, closer: "?>"), lexing.Interpolations, lines);
    }

    /// <summary>Whether <paramref name="character"/> may begin a PHP name: a letter - any past ASCII among them - or <c>_</c>, and no <c>$</c>, which only a variable's name is written after.</summary>
    private static bool StartsName(char character) => char.IsLetter(character) || character == '_' || character > '\u007f';

    /// <summary>Whether <paramref name="character"/> may stand in a PHP name past its first: a letter, a digit or <c>_</c>.</summary>
    private static bool InName(char character) => char.IsLetterOrDigit(character) || character == '_' || character > '\u007f';

    /// <summary>One run of the lexer over a text.</summary>
    private sealed class Lexing(string text, CodeLineIndex lines) : ScanLexing(lines)
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
                else if (character == '#' || (character == '/' && at + 1 < end && text[at + 1] == '/'))
                {
                    at = Comment(at, LineCommentEnd(at, end));
                }
                else if (character == '/' && at + 1 < end && text[at + 1] == '*')
                {
                    at = Comment(at, text.IndexOf("*/", at + 2, end - at - 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : end);
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
        /// text goes on; 0 where no label follows, so the <c>&lt;</c>s are punctuators.
        /// </summary>
        private int Heredoc(int at, int end)
        {
            var label = at + 3;

            while (label < end && text[label] is ' ' or '\t')
            {
                label++;
            }

            var quote = label < end && text[label] is '\'' or '"' ? text[label] : '\0';
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
                    break;
                }

                var lineEnd = LineEnd(text, line, end);
                line = lineEnd + LineBreakAt(text, lineEnd, end);

                if (lineEnd >= end)
                {
                    break;
                }
            }

            if (quote == '\'')
            {
                Add(ScanTokenKind.Text, at, closeEnd);
                return closeEnd;
            }

            Literal(at, body, '\0', close, end);
            Add(ScanTokenKind.Text, at, closeEnd);
            return closeEnd;
        }

        /// <summary>
        /// A string beginning at <paramref name="start"/>, its text at <paramref name="from"/>: in single quotes to the one
        /// that closes it, only <c>\\</c> and <c>\'</c> escaped; in double quotes or backticks - or a heredoc's lines, to
        /// <paramref name="stop"/>, where <paramref name="mark"/> is none - to its closing quote, past each escape and each
        /// interpolation whose code it says (<c>{$...}</c> from its <c>$</c>, <c>${...}</c> after its brace), code that may
        /// hold braces and strings of its own, read with a stack of what it is in. A heredoc's token is its caller's; any
        /// other's is added. Answers where the text goes on.
        /// </summary>
        private int Literal(int start, int from, char mark, int stop, int end)
        {
            if (mark == '\'')
            {
                var at = from;

                while (at < end && text[at] != '\'')
                {
                    at += text[at] == '\\' && at + 1 < end ? 2 : 1;
                }

                at = Math.Min(at + 1, end);
                Add(ScanTokenKind.Text, start, at);
                return at;
            }

            var literals = new LiteralStack<char>(this, start);
            literals.OpenLiteral(mark);
            var index = from;

            while (index < stop && literals.Open)
            {
                var character = text[index];

                if (literals.InCode)
                {
                    if (character is '{' or '}')
                    {
                        literals.Brace(character == '{', index);
                    }
                    else if (character is '\'' or '"')
                    {
                        literals.OpenLiteral(character);
                    }

                    index++;
                    continue;
                }

                var top = literals.Mark;

                if (character == '\\')
                {
                    index += 2;
                }
                else if (top != '\'' && ((character == '{' && index + 1 < stop && text[index + 1] == '$') || (character == '$' && index + 1 < stop && text[index + 1] == '{')))
                {
                    // {$x}: the $ is the code's own; ${x}: the code is after the brace.
                    literals.OpenCode(character == '{' ? index + 1 : index + 2);
                    index += character == '{' ? 1 : 2;
                }
                else if (top != '\0' && character == top)
                {
                    literals.CloseLiteral();
                    index++;
                }
                else
                {
                    index++;
                }
            }

            index = Math.Min(index, stop);
            literals.End(index);

            if (mark != '\0')
            {
                Add(ScanTokenKind.Text, start, index);
            }

            return index;
        }
    }
}
