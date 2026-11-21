using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The lexical rules of JavaScript and TypeScript for the structural scanner: comments (<c>//</c>, <c>/* */</c>, a first
/// line's <c>#!</c>) kept apart; a string in single or double quotes, a template literal with every <c>${...}</c> it
/// holds (braces, strings and templates of its own inside), and a regular expression literal - told from a division by
/// the token before it - each one token, never read as code, a string's and a template's value said by where its
/// constant parts are written, decoded by JavaScript's escapes (<see cref="ScanLiteral"/>), a regular expression's none;
/// names that may hold <c>$</c>, and <c>#private</c> ones; numbers with their separators and suffixes; <c>?.</c>,
/// <c>??=</c>, <c>??</c>, <c>...</c>, <c>=&gt;</c>, the equalities, <c>&amp;&amp;</c>, <c>||</c> and <c>**</c> whole
/// and every other punctuator alone, so <c>&gt;&gt;</c> closes two lists of type arguments. In markup (<c>.jsx</c>,
/// <c>.tsx</c>) an element - its tags, a component's type arguments, attributes and text - is one token, whose
/// <c>{...}</c> alone is code, and whose attributes' quoted texts, their character references decoded, are the strings
/// it holds. Where a literal holds code, the lexer says where, so that code is read in its turn as a text of its own,
/// and where each tag an element opens is named.
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
    public static InterpolatedText Scan(CodeText text, bool markup) => Scan(text, 0, text.Length, text.LineIndex, markup);

    /// <summary>
    /// Scans the part of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/>, its tokens
    /// placed in the whole text: a literal's code, read where it stands.
    /// </summary>
    /// <param name="text">A file's text.</param>
    /// <param name="start">Where the part begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="lines">The text's lines.</param>
    /// <param name="markup">Whether the file may hold JSX.</param>
    /// <param name="closes">
    /// What the scans of the text before this one read of its literals and elements - the
    /// <see cref="InterpolatedText.Closes"/> of the scan whose literal or element holds the part - so each one the part
    /// holds that they read is passed over at once; none for a scan of its own.
    /// </param>
    public static InterpolatedText Scan(CodeText text, long start, long end, CodeLineIndex lines, bool markup, ScanCloses? closes = null)
    {
        var lexing = new Lexing(text, lines, markup, closes);
        lexing.Closes.Scanned(start, end);
        lexing.Run(start, end);
        return new InterpolatedText(new ScannedText(text, lexing.Tokens, lexing.Comments, literals: lexing.Literals), lexing.Interpolations, lines, lexing.Elements) { Closes = lexing.Closes };
    }

    /// <summary>
    /// What a run of the lexer is reading, innermost on top of one stack of its own: code - the text's, or what an
    /// element's <c>{...}</c> holds - or an element. An element in code, code in an element's braces and an element in
    /// that code again are frames of the stack, never a call into the lexer from inside its reading, so markup and code
    /// nested any depth are read whole, and no nesting reaches past the reader's stack.
    /// </summary>
    private abstract class Reading;

    /// <summary>
    /// Code being read from <see cref="At"/> to <see cref="End"/> by <see cref="Lexing"/>, whose tokens it adds: the
    /// text's own, or - where <see cref="Element"/> is one - what that element's brace at <see cref="From"/> opens, to
    /// the <c>}</c> no brace it read opened.
    /// </summary>
    private sealed class CodeReading(Lexing lexing, long at, long end, ElementReading? element, long from) : Reading
    {
        public Lexing Lexing { get; } = lexing;

        public long At { get; set; } = at;

        public long End { get; } = end;

        public ElementReading? Element { get; } = element;

        public long From { get; } = from;

        /// <summary>How many braces it read are open.</summary>
        public int Braces { get; set; }
    }

    /// <summary>
    /// A JSX element being read from its <c>&lt;</c> at <see cref="Start"/>: the token of <see cref="Lexing"/>, in the
    /// code <see cref="Code"/> reads, which goes on past it; where it stands, what it read so far and which part of a tag
    /// it is in.
    /// </summary>
    private sealed class ElementReading(Lexing lexing, CodeReading code, long start, long end) : Reading
    {
        public Lexing Lexing { get; } = lexing;

        public CodeReading Code { get; } = code;

        public long Start { get; } = start;

        public long End { get; } = end;

        public long At { get; set; } = start;

        public ElementPart Part { get; set; } = ElementPart.Tag;

        /// <summary>Where the name of each tag it opens stands.</summary>
        public List<(long Start, long End)> Tags { get; } = [];

        /// <summary>Its attributes' strings.</summary>
        public List<ScanLiteralPart> Strings { get; } = [];

        /// <summary>Where each run of code its braces hold begins and ends, none empty.</summary>
        public List<(long Start, long End)> Spans { get; } = [];

        /// <summary>How many of its tags are open.</summary>
        public int Open { get; set; }

        /// <summary>Whether the tag being read closes one.</summary>
        public bool ClosingTag { get; set; }

        /// <summary>Whether the tag being read closes itself, as its last <c>/&gt;</c> says.</summary>
        public bool SelfClosing { get; set; }
    }

    /// <summary>Where an element's reading stands: at a tag's <c>&lt;</c>, in a tag's attributes, or in the children after it.</summary>
    private enum ElementPart
    {
        Tag,
        Attributes,
        Children,
    }

    /// <summary>One run of the lexer over a text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="lines">Its lines.</param>
    /// <param name="markup">Whether it may hold JSX.</param>
    /// <param name="closes">What the scans of the text read of its literals and elements; a fresh index where none is given.</param>
    private sealed class Lexing(CodeText text, CodeLineIndex lines, bool markup, ScanCloses? closes) : ScanLexing(lines, closes)
    {
        /// <summary>For each JSX element, by where it begins: where the name of each tag it opens stands.</summary>
        public Dictionary<long, List<(long Start, long End)>> Elements { get; } = [];

        /// <summary>
        /// Lexes from <paramref name="start"/> to <paramref name="end"/>: the code, each element it holds, the code each
        /// element's braces hold, and on, as frames of one stack (<see cref="Reading"/>) - the tokens this run's own, those
        /// of an element's code a run's of their own, which only find where the code ends.
        /// </summary>
        public void Run(long start, long end)
        {
            var readings = new Stack<Reading>();
            readings.Push(new CodeReading(this, Shebang(text, start, end), end, element: null, start));

            while (readings.TryPeek(out var top))
            {
                if (top is CodeReading code)
                {
                    if (code.Lexing.Step(code, readings) is { } close)
                    {
                        readings.Pop();
                        code.Element?.Lexing.Coded(code.Element, code.From, close);
                    }
                }
                else if (top is ElementReading element && element.Lexing.Step(element, readings))
                {
                    readings.Pop();
                    element.Code.At = element.At;
                }
            }
        }

        /// <summary>
        /// Reads one token of <paramref name="code"/>, or begins an element it holds on <paramref name="readings"/>; answers
        /// where the code ends once it does - at the <c>}</c> that closes an element's code, or at its end - and else none.
        /// </summary>
        private long? Step(CodeReading code, Stack<Reading> readings)
        {
            var (at, end) = (code.At, code.End);

            if (at >= end)
            {
                return end;
            }

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
                if (Taken(at, end) is not { } past)
                {
                    // Read on the stack; the code goes on past it once it is read.
                    readings.Push(new ElementReading(this, code, at, end));
                    return null;
                }

                at = past;
            }
            else if (Punctuator(at, end) is var length and > 0)
            {
                if (code.Element is not null && character == '}' && code.Braces == 0)
                {
                    return at;
                }

                code.Braces += character == '{' && length == 1 ? 1 : character == '}' ? -1 : 0;
                Add(ScanTokenKind.Punctuation, at, at + length);
                at += length;
            }
            else
            {
                // A character no token begins with - a control character, a surrogate apart from its pair - is passed over.
                at++;
            }

            code.At = at;
            return null;
        }

        /// <summary>JavaScript's comments: <c>//</c> to its line's end, <c>/* */</c> past its closer - to the end where none closes it.</summary>
        protected override long CommentEnd(long at, long end)
            => at + 1 >= end || text[at] != '/' ? at
                : text[at + 1] == '/' ? LineEnd(text, at, end)
                : text[at + 1] == '*' ? (text.Find("*/", at + 2, end) is var close and >= 0 ? close + 2 : end)
                : at;

        /// <summary>A name from <paramref name="start"/> - a private one's <c>#</c> with it - its characters from <paramref name="from"/>.</summary>
        private long Name(long start, long from, long end)
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

            bool Written(ScanToken token, string value) => text.Is(token.Start, token.Length, value);

            var before = Tokens.Count > 1 ? Tokens[^2] : default;
            var adjoins = Tokens.Count > 1 && before.End == last.Start;

            return last.Kind switch
            {
                ScanTokenKind.Name => !Openers.Contains(text.Substring(last.Start, last.Length)) || (Tokens.Count > 1 && (Written(before, ".") || Written(before, "?."))),
                ScanTokenKind.Number or ScanTokenKind.Text => true,
                _ => Written(last, ")") || Written(last, "]")
                    || ((Written(last, "+") || Written(last, "-")) && adjoins && text[before.Start] == text[last.Start] && before.Length == 1)
                    || (Written(last, "!") && adjoins && (before.Kind == ScanTokenKind.Name || text[before.Start] is ')' or ']')),
            };
        }

        /// <summary>A string in single or double quotes from <paramref name="start"/>: to its closing quote, past escapes, its value what the quotes hold; to the line's end where none closes it.</summary>
        private long Quoted(long start, long end)
        {
            var quote = text[start];
            var at = start + 1;

            while (at < end && text[at] != quote && LineBreakAt(text, at, end) == 0)
            {
                at += text[at] == '\\' && at + 1 < end ? 1 + Math.Max(1, LineBreakAt(text, at + 1, end)) : 1;
            }

            at = Math.Min(at, end);
            var closed = at < end && text[at] == quote;
            KeepLiteral(start, new ScanLiteral([Part(start + 1, at)], ScanEscapes.JavaScript) { LeftOpen = !closed });
            at = closed ? at + 1 : at;
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>
        /// A template literal from <paramref name="start"/>, to its closing backquote: each <c>${...}</c> it holds directly
        /// recorded as code, which may hold braces, comments, strings and templates of its own - one stack of what is open -
        /// and its value's parts between its backquotes and its holes. One a scan of the text before this one read is taken
        /// as it was read (<see cref="ScanCloses"/>), and passed over at once; what this reading finds of it, and of each
        /// template its code holds, is kept for the scans of that code.
        /// </summary>
        private long Template(long start, long end)
        {
            if (Closes.Of(start, end) is { Tags: null } read)
            {
                KeepLiteral(start, new ScanLiteral(read.Parts, RawTagged() ? ScanEscapes.None : ScanEscapes.JavaScript) { Interpolated = read.Holes, LeftOpen = read.LeftOpen });

                foreach (var (from, to) in read.Spans)
                {
                    Interpolated(start, from, to);
                }

                Add(ScanTokenKind.Text, start, read.End);
                return read.End;
            }

            var literals = new LiteralStack<char>(this, start);
            literals.OpenLiteral('`', start, start + 1);
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
                        literals.CloseLiteral(at, at + 1);
                        at++;
                    }
                    else if (character == '$' && at + 1 < end && text[at + 1] == '{')
                    {
                        literals.OpenCode(at, at + 2);
                        at += 2;
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
                    literals.OpenLiteral('`', at, at + 1);
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
            KeepLiteral(start, new ScanLiteral(literals.Parts, RawTagged() ? ScanEscapes.None : ScanEscapes.JavaScript) { Interpolated = literals.Holes, LeftOpen = literals.Open });
            Closes.Keep(start, new ScanLiteralRead(at, literals.Parts, literals.Spans) { Holes = literals.Holes, LeftOpen = literals.Open });
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>
        /// Whether the tokens read last are the standard library's <c>String.raw</c> - no member of something else - whose
        /// template's value is its text as written, no escape read in it.
        /// </summary>
        private bool RawTagged()
            => Tokens.Count >= 3
                && text.Is(Tokens[^1].Start, Tokens[^1].Length, "raw")
                && text.Is(Tokens[^2].Start, Tokens[^2].Length, ".")
                && text.Is(Tokens[^3].Start, Tokens[^3].Length, "String")
                && (Tokens.Count == 3 || !(text.Is(Tokens[^4].Start, Tokens[^4].Length, ".") || text.Is(Tokens[^4].Start, Tokens[^4].Length, "?.")));

        /// <summary>Past the string in quotes at <paramref name="start"/>, inside a template's code: no token of it is kept.</summary>
        private long Skipped(long start, long end)
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
        private long Expression(long start, long end)
        {
            var at = ExpressionEnd(start, end);
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>Past the regular expression literal from <paramref name="start"/>: to its closing slash outside a class, past escapes, then its flags; to the line's end where none closes it.</summary>
        private long ExpressionEnd(long start, long end)
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
        private bool OpensElement(long at, long end)
        {
            if (at + 1 >= end || !(IsNameStart(text[at + 1]) || text[at + 1] == '>'))
            {
                return false;
            }

            var past = at + 1;

            // <const T, ...>: a type parameter's modifier.
            if (text.At(past, "const") && past + 5 < end && char.IsWhiteSpace(text[past + 5]))
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
            return !(next < end && text[next] == ',') && !(next + 7 <= end && text.At(next, "extends") && next > past)
                && !(next < end && text[next] == '>' && SignatureFollows(next + 1, end) && !ClosedAfter(past, end, text.Slice(name, named)));
        }

        /// <summary>
        /// Past the type arguments a component's tag writes after its name at <paramref name="at"/> - TypeScript's
        /// <c>&lt;Table&lt;User&gt; /&gt;</c> - past white space, its angle brackets balanced and an arrow's <c>=&gt;</c> and
        /// the strings within passed over: none of the tag's own <c>&gt;</c>; <paramref name="at"/> where no list begins
        /// there, or the text ends before one closes.
        /// </summary>
        private long TypeArgumentsPast(long at, long end)
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
                    var close = text.Find(text[index], index + 1, end);

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
        private bool ClosedAfter(long at, long end, string name)
        {
            for (var close = text.Find("</" + name, at, end); close >= 0;
                close = close + 1 < end ? text.Find("</" + name, close + 1, end) : -1)
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
        private bool SignatureFollows(long at, long end)
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
        /// Reads on the JSX <paramref name="element"/> - one token to the end of its closing tag, a component's type
        /// arguments after its name (<see cref="TypeArgumentsPast"/>), its attributes, its text and the elements it holds
        /// with it - recording where the name of each tag it opens stands, to the next <c>{</c>, whose code is begun on
        /// <paramref name="readings"/> to be read before it goes on (<see cref="Coded"/>), or to its end; answers whether
        /// it is read.
        /// </summary>
        private bool Step(ElementReading element, Stack<Reading> readings)
        {
            var end = element.End;
            var at = element.At;

            switch (element.Part)
            {
                case ElementPart.Tag:
                    if (at >= end)
                    {
                        return Read(element);
                    }

                    // A tag: at a '<'.
                    element.ClosingTag = at + 1 < end && text[at + 1] == '/';
                    element.SelfClosing = false;
                    at += element.ClosingTag ? 2 : 1;
                    var name = at;

                    while (at < end && (IsNamePart(text[at]) || text[at] is '.' or '-' or ':'))
                    {
                        at++;
                    }

                    if (!element.ClosingTag && at > name)
                    {
                        element.Tags.Add((name, at));
                        at = TypeArgumentsPast(at, end);
                    }

                    element.At = at;
                    element.Part = ElementPart.Attributes;
                    return false;

                case ElementPart.Attributes:
                    while (at < end && text[at] != '>')
                    {
                        if (text[at] == '{')
                        {
                            Begin(element, at + 1, readings);
                            return false;
                        }

                        if (text[at] is '\'' or '"')
                        {
                            // An attribute's string: a string the element holds.
                            var close = text.Find(text[at], at + 1, end);
                            element.Strings.Add(Part(at + 1, close < 0 ? end : close));
                            at = close < 0 ? end : close + 1;
                        }
                        else
                        {
                            element.SelfClosing = text[at] == '/' && at + 1 < end && text[at + 1] == '>';
                            at++;
                        }
                    }

                    element.At = Math.Min(at + 1, end);
                    element.Open += element.ClosingTag ? -1 : element.SelfClosing ? 0 : 1;

                    if (element.Open <= 0)
                    {
                        return Read(element);
                    }

                    element.Part = ElementPart.Children;
                    return false;

                default:
                    // The element's children: text, code, and the next tag.
                    while (at < end && text[at] != '<')
                    {
                        if (text[at] == '{')
                        {
                            Begin(element, at + 1, readings);
                            return false;
                        }

                        at++;
                    }

                    element.At = at;
                    element.Part = ElementPart.Tag;
                    return false;
            }
        }

        /// <summary>
        /// Begins on <paramref name="readings"/> the code <paramref name="element"/>'s <c>{</c> opens at
        /// <paramref name="from"/>: read as code - its strings, templates and elements of its own with it - by a run of its
        /// own, which only finds the <c>}</c> that closes it.
        /// </summary>
        private void Begin(ElementReading element, long from, Stack<Reading> readings)
        {
            element.At = from;
            readings.Push(new CodeReading(new Lexing(text, Lines, markup, Closes), from, element.End, element, from));
        }

        /// <summary>The code <paramref name="element"/>'s brace opened at <paramref name="from"/>, read to the <c>}</c> at <paramref name="close"/>: recorded as its code, and the element read on past that brace.</summary>
        private void Coded(ElementReading element, long from, long close)
        {
            Interpolated(element.Start, from, close);

            if (close > from)
            {
                element.Spans.Add((from, close));
            }

            element.At = Math.Min(close + 1, element.End);
        }

        /// <summary>
        /// <paramref name="element"/>, read: its token, the name of each tag it opens, and its strings - markup is no
        /// string, and what it holds as strings are its attributes' values, its text and tags none of them - kept for the
        /// scans of its code; answers that it is read.
        /// </summary>
        private bool Read(ElementReading element)
        {
            var at = Math.Min(element.At, element.End);

            if (element.Tags.Count > 0)
            {
                Elements[element.Start] = element.Tags;
            }

            KeepLiteral(element.Start, new ScanLiteral(element.Strings, ScanEscapes.Html) { Markup = true });
            Closes.Keep(element.Start, new ScanLiteralRead(at, element.Strings, element.Spans) { Tags = element.Tags });
            Add(ScanTokenKind.Text, element.Start, at);
            element.At = at;
            return true;
        }

        /// <summary>
        /// Where the element at <paramref name="start"/> ends, where a scan of the text before this one read it
        /// (<see cref="ScanCloses"/>): taken as it was read - its token, its tags, its strings and its code - and passed
        /// over at once; none where none did, and it is read.
        /// </summary>
        private long? Taken(long start, long end)
        {
            var read = Closes.Of(start, end);

            if (read?.Tags is not { } tags)
            {
                return null;
            }

            foreach (var (from, to) in read.Spans)
            {
                Interpolated(start, from, to);
            }

            if (tags.Count > 0)
            {
                Elements[start] = [.. tags];
            }

            KeepLiteral(start, new ScanLiteral(read.Parts, ScanEscapes.Html) { Markup = true });
            Add(ScanTokenKind.Text, start, read.End);
            return read.End;
        }

        /// <summary>How long the punctuator at <paramref name="at"/> is: one of <see cref="Whole"/> - <c>?.</c> not before a digit, where it is a condition's - else one character; 0 where none is.</summary>
        private long Punctuator(long at, long end)
        {
            foreach (var whole in Whole)
            {
                if (at + whole.Length <= end && text.At(at, whole)
                    && !(whole == "?." && at + 2 < end && char.IsAsciiDigit(text[at + 2])))
                {
                    return whole.Length;
                }
            }

            return Alone(text[at]);
        }
    }
}
