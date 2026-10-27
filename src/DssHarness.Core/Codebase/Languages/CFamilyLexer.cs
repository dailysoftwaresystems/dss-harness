using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The lexical rules of C and C++ for the structural scanner: comments (<c>//</c>, its line continued by a backslash,
/// and <c>/* */</c>) kept apart; string and character literals - with their prefixes, and C++'s raw strings - as one
/// token each, never read as code; a preprocessor line, its continuations and comments with it, as one directive,
/// marked where it opens, continues or ends a conditional; numbers with their separators; <c>::</c>, <c>-&gt;</c> and
/// <c>...</c> whole and every other punctuator alone, so <c>&gt;&gt;</c> closes two lists of type arguments.
/// </summary>
internal static class CFamilyLexer
{
    /// <summary>The prefixes a string or a character literal may carry.</summary>
    private static readonly HashSet<string> LiteralPrefixes = new(StringComparer.Ordinal) { "L", "u", "U", "u8" };

    /// <summary>The prefixes of a C++ raw string, before its quote.</summary>
    private static readonly HashSet<string> RawPrefixes = new(StringComparer.Ordinal) { "R", "LR", "uR", "UR", "u8R" };

    /// <summary>The longest delimiter a raw string may have.</summary>
    private const int MostRawDelimiter = 16;

    /// <summary>Scans <paramref name="text"/> by C's rules, and C++'s where <paramref name="cpp"/>.</summary>
    /// <param name="text">A file's text.</param>
    /// <param name="cpp">Whether it is C++, whose raw strings C does not have.</param>
    public static ScannedText Scan(string text, bool cpp)
    {
        var lines = CodeFileText.LineIndex(text);
        var lexing = new Lexing(text, lines, cpp, directive: false);
        lexing.Run(0, text.Length);
        return new ScannedText(text, lexing.Tokens, lexing.Comments);
    }

    /// <summary>
    /// The tokens of a directive's text from <paramref name="start"/> to <paramref name="end"/> - past its <c>#</c> and its
    /// name - scanned as code is, a <c>#</c> and <c>##</c> among them punctuators.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="lines">Where its lines begin.</param>
    /// <param name="start">Where the directive's text to read begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="cpp">Whether it is C++.</param>
    public static ScannedText ScanDirective(string text, CodeLineIndex lines, int start, int end, bool cpp)
    {
        var lexing = new Lexing(text, lines, cpp, directive: true);
        lexing.Run(start, end);
        return new ScannedText(text, lexing.Tokens, lexing.Comments);
    }

    /// <summary>
    /// The name of the directive at <paramref name="start"/> - the word after its <c>#</c> - and where the text after it
    /// begins; an empty name where none follows the <c>#</c>.
    /// </summary>
    public static (string Name, int After) DirectiveName(string text, int start, int end)
    {
        var at = start + 1;

        while (at < end && IsSpace(text, at, end, out var width))
        {
            at += width;
        }

        var name = at;

        while (at < end && ScanLexing.IsNamePart(text[at]))
        {
            at++;
        }

        return (text[name..at], at);
    }

    private static Dictionary<string, ScanBranch> Branches { get; } = new(StringComparer.Ordinal)
    {
        ["if"] = ScanBranch.Opens,
        ["ifdef"] = ScanBranch.Opens,
        ["ifndef"] = ScanBranch.Opens,
        ["elif"] = ScanBranch.Alternative,
        ["elifdef"] = ScanBranch.Alternative,
        ["elifndef"] = ScanBranch.Alternative,
        ["else"] = ScanBranch.Alternative,
        ["endif"] = ScanBranch.Closes,
    };

    /// <summary>
    /// Whether the text at <paramref name="at"/> is white space the code is not cut by - a blank, or a backslash that ends a
    /// line, which joins it to the next - and how wide it is. A line break is no such space.
    /// </summary>
    private static bool IsSpace(string text, int at, int end, out int width)
    {
        width = 1;

        if (text[at] is ' ' or '\t' or '\f' or '\v')
        {
            return true;
        }

        if (text[at] == '\\' && ScanLexing.LineBreakAt(text, at + 1, end) is var breaks and > 0)
        {
            width = 1 + breaks;
            return true;
        }

        return false;
    }

    /// <summary>One run of the lexer over a span of a text.</summary>
    private sealed class Lexing(string text, CodeLineIndex lines, bool cpp, bool directive) : ScanLexing(lines)
    {
        public void Run(int start, int end)
        {
            var at = start;

            // Whether nothing but white space and comments stands before this point on its line: where a # begins a directive.
            var lineStart = !directive;

            while (at < end)
            {
                var character = text[at];

                if (LineBreakAt(text, at, end) is var breaks and > 0)
                {
                    at += breaks;
                    lineStart = !directive;
                    continue;
                }

                if (IsSpace(text, at, end, out var width))
                {
                    at += width;
                    continue;
                }

                if (CommentEnd(at, end) is var comment && comment > at)
                {
                    at = Comment(at, comment);
                    continue;
                }

                if (character == '#' && lineStart)
                {
                    at = Directive(at, end);
                    lineStart = false;
                    continue;
                }

                lineStart = false;

                if (IsNameStart(character))
                {
                    at = Name(at, end);
                }
                else if (char.IsAsciiDigit(character) || (character == '.' && at + 1 < end && char.IsAsciiDigit(text[at + 1])))
                {
                    at = Number(at, end);
                }
                else if (character is '"' or '\'')
                {
                    at = Literal(at, at, end);
                }
                else if (Punctuator(at, end) is var length and > 0)
                {
                    Add(ScanTokenKind.Punctuation, at, at + length);
                    at += length;
                }
                else
                {
                    // A character no token begins with - a control character, a surrogate apart from its pair - is passed over.
                    at++;
                }
            }
        }

        /// <summary>C's comments: <c>//</c> to its line's end, a backslash continuing it (<see cref="LineEnd"/>), <c>/* */</c> past its closer.</summary>
        protected override int CommentEnd(int at, int end)
            => at + 1 >= end || text[at] != '/' ? at
                : text[at + 1] == '/' ? LineEnd(at, end)
                : text[at + 1] == '*' ? (text.IndexOf("*/", at + 2, end - at - 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : end)
                : at;

        /// <summary>Where the line <paramref name="at"/> is on ends: at a line break no backslash continues.</summary>
        private int LineEnd(int at, int end)
        {
            while (at < end)
            {
                if (text[at] == '\\' && LineBreakAt(text, at + 1, end) is var continued and > 0)
                {
                    at += 1 + continued;
                }
                else if (LineBreakAt(text, at, end) > 0)
                {
                    return at;
                }
                else
                {
                    at++;
                }
            }

            return end;
        }

        /// <summary>
        /// A preprocessor line from its <c>#</c>: to the line break no backslash continues and no comment or literal holds,
        /// its comments kept apart as every comment is.
        /// </summary>
        private int Directive(int start, int end)
        {
            var at = start + 1;

            while (at < end)
            {
                if (text[at] == '\\' && LineBreakAt(text, at + 1, end) is var continued and > 0)
                {
                    at += 1 + continued;
                }
                else if (LineBreakAt(text, at, end) > 0)
                {
                    break;
                }
                else if (CommentEnd(at, end) is var comment && comment > at)
                {
                    at = comment;
                }
                else if (text[at] is '"' or '\'' && !(text[at] == '\'' && IsNamePart(text[at - 1])))
                {
                    at = Quoted(at, text[at], end);
                }
                else
                {
                    at++;
                }
            }

            var (name, _) = DirectiveName(text, start, at);
            Add(ScanTokenKind.Directive, start, at, Branches.GetValueOrDefault(name));
            return at;
        }

        private int Name(int start, int end)
        {
            var at = start;

            while (at < end && IsNamePart(text[at]))
            {
                at++;
            }

            var name = text[start..at];

            if (at < end && text[at] == '"' && cpp && RawPrefixes.Contains(name) && Raw(start, at, end) is var raw and > 0)
            {
                return raw;
            }

            if (at < end && text[at] is '"' or '\'' && LiteralPrefixes.Contains(name))
            {
                return Literal(start, at, end);
            }

            Add(ScanTokenKind.Name, start, at);
            return at;
        }

        /// <summary>A number as the preprocessor reads one: digits, letters, dots, an exponent's sign and the separators between digits.</summary>
        private int Number(int start, int end)
        {
            var at = start;

            while (at < end)
            {
                if (at + 1 < end && text[at] is 'e' or 'E' or 'p' or 'P' && text[at + 1] is '+' or '-')
                {
                    at += 2;
                }
                else if (text[at] == '\'' && at + 1 < end && IsNamePart(text[at + 1]))
                {
                    at += 2;
                }
                else if (IsNamePart(text[at]) || text[at] == '.')
                {
                    at++;
                }
                else
                {
                    break;
                }
            }

            Add(ScanTokenKind.Number, start, at);
            return at;
        }

        /// <summary>A literal whose quote is at <paramref name="quote"/>, its prefix from <paramref name="start"/>.</summary>
        private int Literal(int start, int quote, int end)
        {
            var at = Quoted(quote, text[quote], end);
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>
        /// Past the literal whose quote is at <paramref name="quote"/>: to its closing quote, an escaped one and a line its
        /// backslash continues read past - or to the end of its line, where a literal left open ends.
        /// </summary>
        private int Quoted(int quote, char mark, int end)
        {
            var at = quote + 1;

            while (at < end)
            {
                if (text[at] == '\\')
                {
                    at += LineBreakAt(text, at + 1, end) is var continued and > 0 ? 1 + continued : 2;
                }
                else if (text[at] == mark)
                {
                    return at + 1;
                }
                else if (LineBreakAt(text, at, end) > 0)
                {
                    return at;
                }
                else
                {
                    at++;
                }
            }

            return Math.Min(at, end);
        }

        /// <summary>
        /// A C++ raw string whose prefix is at <paramref name="start"/> and quote at <paramref name="quote"/>: to its
        /// <c>)delimiter"</c>, over any line; 0 where what follows the quote is no delimiter and <c>(</c>.
        /// </summary>
        private int Raw(int start, int quote, int end)
        {
            var open = quote + 1;

            while (open < end && open - quote - 1 <= MostRawDelimiter && text[open] is not ('(' or ')' or '\\' or ' ' or '"' or '\t' or '\r' or '\n'))
            {
                open++;
            }

            if (open >= end || text[open] != '(' || open - quote - 1 > MostRawDelimiter)
            {
                return 0;
            }

            var closing = ")" + text[(quote + 1)..open] + "\"";
            var close = text.IndexOf(closing, open + 1, end - open - 1, StringComparison.Ordinal);
            var at = close < 0 ? end : close + closing.Length;

            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>How long the punctuator at <paramref name="at"/> is: <c>::</c>, <c>-&gt;</c>, <c>...</c> and, in a directive, <c>##</c> whole; 0 where no punctuator is.</summary>
        private int Punctuator(int at, int end)
        {
            var character = text[at];

            if (at + 2 < end && character == '.' && text[at + 1] == '.' && text[at + 2] == '.')
            {
                return 3;
            }

            if (at + 1 < end && ((character == ':' && text[at + 1] == ':') || (character == '-' && text[at + 1] == '>') || (directive && character == '#' && text[at + 1] == '#')))
            {
                return 2;
            }

            return Alone(character);
        }
    }
}
