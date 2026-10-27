using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The lexical rules of JavaScript and TypeScript for the structural scanner: comments (<c>//</c>, <c>/* */</c>, a first
/// line's <c>#!</c>) kept apart; a string in single or double quotes, a template literal with every <c>${...}</c> it
/// holds (braces, strings and templates of its own inside), and a regular expression literal - told from a division by
/// the token before it - each one token, never read as code; names that may hold <c>$</c>, and <c>#private</c> ones;
/// numbers with their separators and suffixes; <c>?.</c>, <c>??=</c>, <c>??</c>, <c>...</c>, <c>=&gt;</c>, the
/// equalities, <c>&amp;&amp;</c>, <c>||</c> and <c>**</c> whole and every other punctuator alone, so <c>&gt;&gt;</c> closes two
/// lists of type arguments. In markup (<c>.jsx</c>, <c>.tsx</c>) an element - its tags, a component's type arguments,
/// attributes and text - is one token, whose <c>{...}</c> alone is code. Where a literal holds code, the lexer says
/// where, so that code is read in its turn as a text of its own, and where each tag an element opens is named.
/// </summary>
internal static class JsLexer
{
    /// <summary>The punctuators read whole, the longest first.</summary>
    private static readonly string[] Whole = ["===", "!==", "??=", "&&=", "||=", "**=", "...", "?.", "??", "=>", "==", "!=", "&&", "||", "**"];

    /// <summary>The words after which an expression begins, so that a <c>/</c> or a <c>&lt;</c> after one opens a literal: no operand ends with one.</summary>
    private static readonly HashSet<string> Openers = new(StringComparer.Ordinal)
    {
        "return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else", "yield", "await", "default", "extends",
    };

    /// <summary>Scans <paramref name="text"/> by JavaScript's rules - TypeScript's are the same - its markup read where <paramref name="markup"/>.</summary>
    /// <param name="text">A file's text.</param>
    /// <param name="markup">Whether the file may hold JSX: a <c>.jsx</c> or <c>.tsx</c> one.</param>
    public static InterpolatedText Scan(string text, bool markup) => Scan(text, 0, text.Length, CodeFileText.LineIndex(text), markup);

    /// <summary>
    /// Scans the part of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/>, its tokens
    /// placed in the whole text: a literal's code, read where it stands.
    /// </summary>
    /// <param name="text">A file's text.</param>
    /// <param name="start">Where the part begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="lines">The text's lines.</param>
    /// <param name="markup">Whether the file may hold JSX.</param>
    public static InterpolatedText Scan(string text, int start, int end, CodeLineIndex lines, bool markup)
    {
        var lexing = new Lexing(text, lines, markup, depth: 0);
        lexing.Run(start, end);
        return new InterpolatedText(new ScannedText(text, lexing.Tokens, lexing.Comments), lexing.Interpolations, lines, lexing.Elements);
    }

    /// <summary>One run of the lexer over a text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="lines">Its lines.</param>
    /// <param name="markup">Whether it may hold JSX.</param>
    /// <param name="depth">How many elements' code it is read inside: none past <see cref="ScanFrame.MostDepth"/>.</param>
    private sealed class Lexing(string text, CodeLineIndex lines, bool markup, int depth) : ScanLexing(lines)
    {
        /// <summary>For each JSX element, by where it begins: where the name of each tag it opens stands.</summary>
        public Dictionary<int, List<(int Start, int End)>> Elements { get; } = [];

        /// <summary>
        /// Lexes from <paramref name="start"/> to <paramref name="end"/>; where <paramref name="closing"/>, stops at the
        /// first <c>}</c> no brace it read opened, and answers where that stands - <paramref name="end"/> where none does.
        /// </summary>
        public int Run(int start, int end, bool closing = false)
        {
            var at = Shebang(text, start, end);
            var braces = 0;

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
                else if (character is '\'' or '"')
                {
                    at = Quoted(at, end);
                }
                else if (character == '`')
                {
                    at = Template(at, end);
                }
                else if (character == '/' && !EndsOperand())
                {
                    at = Expression(at, end);
                }
                else if (character == '#' && at + 1 < end && IsNameStart(text[at + 1]))
                {
                    at = Name(at, at + 1, end);
                }
                else if (IsNameStart(character))
                {
                    at = Name(at, at, end);
                }
                else if (char.IsAsciiDigit(character) || (character == '.' && at + 1 < end && char.IsAsciiDigit(text[at + 1])))
                {
                    at = Number(text, at, end);
                }
                else if (character == '<' && markup && !EndsOperand() && OpensElement(at, end))
                {
                    at = Element(at, end);
                }
                else if (Punctuator(at, end) is var length and > 0)
                {
                    if (closing && character == '}' && braces-- == 0)
                    {
                        return at;
                    }

                    braces += character == '{' && length == 1 ? 1 : 0;
                    Add(ScanTokenKind.Punctuation, at, at + length);
                    at += length;
                }
                else
                {
                    // A character no token begins with - a control character, a surrogate apart from its pair - is passed over.
                    at++;
                }
            }

            return end;
        }

        /// <summary>JavaScript's comments: <c>//</c> to its line's end, <c>/* */</c> past its closer - to the end where none closes it.</summary>
        protected override int CommentEnd(int at, int end)
            => at + 1 >= end || text[at] != '/' ? at
                : text[at + 1] == '/' ? LineEnd(text, at, end)
                : text[at + 1] == '*' ? (text.IndexOf("*/", at + 2, end - at - 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : end)
                : at;

        /// <summary>A name from <paramref name="start"/> - a private one's <c>#</c> with it - its characters from <paramref name="from"/>.</summary>
        private int Name(int start, int from, int end)
        {
            var past = from + 1;

            while (past < end && IsNamePart(text[past]))
            {
                past++;
            }

            Add(ScanTokenKind.Name, start, past);
            return past;
        }

        /// <summary>
        /// Whether the last token read ends an operand, so that a <c>/</c> after it divides and a <c>&lt;</c> compares: a
        /// name but a word after which an expression begins - a member's name whatever word it spells - a number, a
        /// literal, a group's closer, a postfix <c>++</c> or <c>--</c>, TypeScript's non-null assertion written against
        /// its operand; a block's brace does not, a statement beginning after it.
        /// </summary>
        private bool EndsOperand()
        {
            if (Tokens.Count == 0)
            {
                return false;
            }

            var last = Tokens[^1];
            var written = text.AsSpan(last.Start, last.Length);

            var before = Tokens.Count > 1 ? Tokens[^2] : default;
            var adjoins = Tokens.Count > 1 && before.End == last.Start;

            return last.Kind switch
            {
                ScanTokenKind.Name => !Openers.Contains(written.ToString()) || (Tokens.Count > 1 && text.AsSpan(before.Start, before.Length) is "." or "?."),
                ScanTokenKind.Number or ScanTokenKind.Text => true,
                _ => written is ")" or "]"
                    || (written is "+" or "-" && adjoins && text[before.Start] == written[0] && before.Length == 1)
                    || (written is "!" && adjoins && (before.Kind == ScanTokenKind.Name || text[before.Start] is ')' or ']')),
            };
        }

        /// <summary>A string in single or double quotes from <paramref name="start"/>: to its closing quote, past escapes; to the line's end where none closes it.</summary>
        private int Quoted(int start, int end)
        {
            var quote = text[start];
            var at = start + 1;

            while (at < end && text[at] != quote && LineBreakAt(text, at, end) == 0)
            {
                at += text[at] == '\\' && at + 1 < end ? 1 + Math.Max(1, LineBreakAt(text, at + 1, end)) : 1;
            }

            at = Math.Min(at < end && text[at] == quote ? at + 1 : at, end);
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>
        /// A template literal from <paramref name="start"/>, to its closing backquote: each <c>${...}</c> it holds directly
        /// recorded as code, which may hold braces, comments, strings and templates of its own - one stack of what is open.
        /// </summary>
        private int Template(int start, int end)
        {
            var literals = new LiteralStack<char>(this, start);
            literals.OpenLiteral('`');
            var at = start + 1;

            // The last character of the code read, which tells a regular expression's slash from a division's.
            var last = '{';

            while (at < end && literals.Open)
            {
                var character = text[at];

                if (!literals.InCode)
                {
                    if (character == '\\')
                    {
                        at += 2;
                    }
                    else if (character == '`')
                    {
                        literals.CloseLiteral();
                        at++;
                    }
                    else if (character == '$' && at + 1 < end && text[at + 1] == '{')
                    {
                        at += 2;
                        literals.OpenCode(at);
                        last = '{';
                    }
                    else
                    {
                        at++;
                    }
                }
                else if (literals.Past(character, at, end) is var past && past > at)
                {
                    // A comment leaves what the code last read as it was; a brace is what it last read.
                    last = character is '{' or '}' ? character : last;
                    at = past;
                }
                else if (character is '\'' or '"')
                {
                    at = Skipped(at, end);
                    last = character;
                }
                else if (character == '`')
                {
                    literals.OpenLiteral('`');
                    at++;
                    last = character;
                }
                else if (character == '/' && !(IsNamePart(last) || last is ')' or ']' or '\'' or '"' or '`'))
                {
                    at = ExpressionEnd(at, end);
                    last = '/';
                }
                else
                {
                    last = char.IsWhiteSpace(character) ? last : character;
                    at++;
                }
            }

            at = Math.Min(at, end);
            literals.End(at);
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>Past the string in quotes at <paramref name="start"/>, inside a template's code: no token of it is kept.</summary>
        private int Skipped(int start, int end)
        {
            var quote = text[start];
            var at = start + 1;

            while (at < end && text[at] != quote && LineBreakAt(text, at, end) == 0)
            {
                at += text[at] == '\\' ? 2 : 1;
            }

            return Math.Min(at + 1, end);
        }

        /// <summary>A regular expression literal from <paramref name="start"/>: one token to its end (<see cref="ExpressionEnd"/>).</summary>
        private int Expression(int start, int end)
        {
            var at = ExpressionEnd(start, end);
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>Past the regular expression literal from <paramref name="start"/>: to its closing slash outside a class, past escapes, then its flags; to the line's end where none closes it.</summary>
        private int ExpressionEnd(int start, int end)
        {
            var at = start + 1;
            var inClass = false;

            while (at < end && LineBreakAt(text, at, end) == 0 && (inClass || text[at] != '/'))
            {
                inClass = text[at] == '[' || (inClass && text[at] != ']');
                at += text[at] == '\\' ? 2 : 1;
            }

            if (at < end && text[at] == '/')
            {
                at++;

                while (at < end && IsNamePart(text[at]))
                {
                    at++;
                }
            }

            return Math.Min(at, end);
        }

        /// <summary>
        /// Whether the <c>&lt;</c> at <paramref name="at"/> opens an element: a tag's name or a fragment's <c>&gt;</c>
        /// follows, and no list of type parameters - <c>&lt;T,&gt;</c>, <c>&lt;T extends U&gt;</c>, <c>&lt;const T,&gt;</c>,
        /// or <c>&lt;T&gt;</c> before parameters an arrow or a return type follows - which a <c>.tsx</c> file writes before a
        /// generic function's or a function type's parameters.
        /// </summary>
        private bool OpensElement(int at, int end)
        {
            if (at + 1 >= end || !(IsNameStart(text[at + 1]) || text[at + 1] == '>'))
            {
                return false;
            }

            var past = at + 1;

            // <const T, ...>: a type parameter's modifier.
            if (string.CompareOrdinal(text, past, "const", 0, 5) == 0 && past + 5 < end && char.IsWhiteSpace(text[past + 5]))
            {
                past += 5;

                while (past < end && char.IsWhiteSpace(text[past]))
                {
                    past++;
                }
            }

            var name = past;

            while (past < end && (IsNamePart(text[past]) || text[past] is '.' or '-' or ':'))
            {
                past++;
            }

            var named = past;
            past = TypeArgumentsPast(past, end);
            var next = past;

            while (next < end && char.IsWhiteSpace(text[next]))
            {
                next++;
            }

            // <T>(x: T) => T is a signature, unless a closing tag of its name follows: <small>(optional): </small>.
            return !(next < end && text[next] == ',') && !(next + 7 <= end && string.CompareOrdinal(text, next, "extends", 0, 7) == 0 && next > past)
                && !(next < end && text[next] == '>' && SignatureFollows(next + 1, end) && !ClosedAfter(past, end, text[name..named]));
        }

        /// <summary>
        /// Past the type arguments a component's tag writes after its name at <paramref name="at"/> - TypeScript's
        /// <c>&lt;Table&lt;User&gt; /&gt;</c> - past white space, its angle brackets balanced and an arrow's <c>=&gt;</c> and
        /// the strings within passed over: none of the tag's own <c>&gt;</c>; <paramref name="at"/> where no list begins
        /// there, or the text ends before one closes.
        /// </summary>
        private int TypeArgumentsPast(int at, int end)
        {
            var less = at;

            while (less < end && char.IsWhiteSpace(text[less]))
            {
                less++;
            }

            if (less >= end || text[less] != '<')
            {
                return at;
            }

            var depth = 0;

            for (var index = less; index < end; index++)
            {
                if (text[index] == '<')
                {
                    depth++;
                }
                else if (text[index] == '>' && text[index - 1] != '=' && --depth == 0)
                {
                    return index + 1;
                }
                else if (text[index] is '\'' or '"' or '`')
                {
                    var close = text.IndexOf(text[index], index + 1, end - index - 1);

                    if (close < 0)
                    {
                        return at;
                    }

                    index = close;
                }
            }

            return at;
        }

        /// <summary>Whether a closing tag of <paramref name="name"/> - that name whole, <c>&lt;/T&gt;</c> and no <c>&lt;/Table&gt;</c> - is written from <paramref name="at"/>.</summary>
        private bool ClosedAfter(int at, int end, string name)
        {
            for (var close = text.IndexOf("</" + name, at, end - at, StringComparison.Ordinal); close >= 0;
                close = text.IndexOf("</" + name, close + 1, end - close - 1, StringComparison.Ordinal))
            {
                var after = close + 2 + name.Length;

                if (after < end && (text[after] == '>' || char.IsWhiteSpace(text[after])))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a function's parameters begin at <paramref name="at"/>, past white space: a parenthesis whose match an
        /// arrow or a return type's colon follows - <c>&lt;T&gt;(item: T) =&gt; string</c> - and no element's text does.
        /// </summary>
        private bool SignatureFollows(int at, int end)
        {
            while (at < end && char.IsWhiteSpace(text[at]))
            {
                at++;
            }

            if (at >= end || text[at] != '(')
            {
                return false;
            }

            var depth = 0;

            for (; at < end; at++)
            {
                if (text[at] is '(')
                {
                    depth++;
                }
                else if (text[at] is ')' && --depth == 0)
                {
                    var after = at + 1;

                    while (after < end && char.IsWhiteSpace(text[after]))
                    {
                        after++;
                    }

                    return after < end && (text[after] == ':' || (text[after] == '=' && after + 1 < end && text[after + 1] == '>'));
                }
            }

            return false;
        }

        /// <summary>
        /// A JSX element from <paramref name="start"/>, one token to the end of its closing tag - a component's type
        /// arguments after its name (<see cref="TypeArgumentsPast"/>), its attributes, its text and the elements it holds
        /// with it - recording where each <c>{...}</c> it holds is code and where the name of each tag it opens stands.
        /// </summary>
        private int Element(int start, int end)
        {
            var tags = new List<(int Start, int End)>();
            var open = 0;
            var at = start;

            while (at < end)
            {
                // A tag: at a '<'.
                var closingTag = at + 1 < end && text[at + 1] == '/';
                at += closingTag ? 2 : 1;
                var name = at;

                while (at < end && (IsNamePart(text[at]) || text[at] is '.' or '-' or ':'))
                {
                    at++;
                }

                if (!closingTag && at > name)
                {
                    tags.Add((name, at));
                    at = TypeArgumentsPast(at, end);
                }

                var selfClosing = false;

                while (at < end && text[at] != '>')
                {
                    if (text[at] == '{')
                    {
                        at = Code(start, at + 1, end);
                    }
                    else if (text[at] is '\'' or '"')
                    {
                        var close = text.IndexOf(text[at], at + 1, end - at - 1);
                        at = close < 0 ? end : close + 1;
                    }
                    else
                    {
                        selfClosing = text[at] == '/' && at + 1 < end && text[at + 1] == '>';
                        at++;
                    }
                }

                at = Math.Min(at + 1, end);
                open += closingTag ? -1 : selfClosing ? 0 : 1;

                if (open <= 0)
                {
                    break;
                }

                // The element's children: text, code, and the next tag.
                while (at < end && text[at] != '<')
                {
                    at = text[at] == '{' ? Code(start, at + 1, end) : at + 1;
                }
            }

            at = Math.Min(at, end);

            if (tags.Count > 0)
            {
                Elements[start] = tags;
            }

            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>
        /// The code an element's <c>{</c> opens at <paramref name="from"/>, recorded as code of the element at
        /// <paramref name="element"/>: to the <c>}</c> that closes it, found by reading it as code - its strings,
        /// templates and elements of its own with it; answers past that brace.
        /// </summary>
        private int Code(int element, int from, int end)
        {
            if (depth >= ScanFrame.MostDepth)
            {
                return end;
            }

            var close = new Lexing(text, Lines, markup, depth + 1).Run(from, end, closing: true);
            Interpolated(element, from, close);
            return Math.Min(close + 1, end);
        }

        /// <summary>How long the punctuator at <paramref name="at"/> is: one of <see cref="Whole"/> - <c>?.</c> not before a digit, where it is a condition's - else one character; 0 where none is.</summary>
        private int Punctuator(int at, int end)
        {
            foreach (var whole in Whole)
            {
                if (at + whole.Length <= end && string.CompareOrdinal(text, at, whole, 0, whole.Length) == 0
                    && !(whole == "?." && at + 2 < end && char.IsAsciiDigit(text[at + 2])))
                {
                    return whole.Length;
                }
            }

            return Alone(text[at]);
        }
    }
}
