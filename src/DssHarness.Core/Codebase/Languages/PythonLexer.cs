using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The lexical rules of Python for the structural scanner: comments (<c>#</c> to their line's end, a first line's shebang
/// among them) kept apart; a string - in single or double quotes, triple-quoted over lines, with a prefix (<c>r</c>, <c>b</c>,
/// <c>f</c>, <c>u</c>, and <c>rb</c>, <c>fr</c> and the rest of their combinations, in any case) - as one token with every
/// replacement field an f-string holds (<c>{name}</c>, <c>{f(x)!r:&gt;10}</c>), never read as code, and where its value's
/// constant parts are written - between its quotes and its fields, a field's conversion and format its own - and how they are
/// decoded (<see cref="ScanEscapes.Python"/>): a raw one's as written, any other's by Python's escapes; names that may hold
/// <c>$</c>; numbers with their separators; <c>**=</c>, <c>//=</c>, <c>&lt;&lt;=</c>, <c>&gt;&gt;=</c>, <c>...</c>,
/// <c>**</c>, <c>//</c>, <c>&lt;&lt;</c>, <c>&gt;&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>, <c>==</c>, <c>!=</c>, <c>-&gt;</c>,
/// <c>:=</c> and the assigning <c>+=</c> and the rest whole, and every other punctuator alone; a backslash that continues a
/// line no token. A line's indentation is no token either: each token says its line, and the reader finds its column from
/// where it begins. Where an f-string's fields hold code, the lexer says where, so that code is read in its turn as a text of
/// its own.
/// </summary>
internal static class PythonLexer
{
    /// <summary>The punctuators read whole, the longest first.</summary>
    private static readonly string[] Whole = ["**=", "//=", "<<=", ">>=", "...", "**", "//", "<<", ">>", "<=", ">=", "==", "!=", "->", ":=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "@="];

    /// <summary>The letter combinations a string may be prefixed by, in lower case: raw, bytes, formatted, unicode, and raw with bytes or a format. A prefix is looked up as it is written in lower case.</summary>
    private static readonly HashSet<string> Prefixes = new(StringComparer.Ordinal) { "r", "b", "f", "u", "rb", "br", "fr", "rf" };

    /// <summary>Scans <paramref name="text"/> by Python's rules.</summary>
    /// <param name="text">A file's text.</param>
    public static InterpolatedText Scan(CodeText text) => Scan(text, 0, text.Length, text.LineIndex);

    /// <summary>
    /// Scans the part of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/> by Python's rules,
    /// its tokens placed in the whole text: an f-string's field, read where it stands.
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
    public static InterpolatedText Scan(CodeText text, long start, long end, CodeLineIndex lines, ScanCloses? closes = null)
    {
        var lexing = new Lexing(text, lines, closes);
        lexing.Closes.Scanned(start, end);
        lexing.Run(start, end);
        return new InterpolatedText(new ScannedText(text, lexing.Tokens, lexing.Comments, literals: lexing.Literals), lexing.Interpolations, lines) { Closes = lexing.Closes };
    }

    /// <summary>One run of the lexer over a text.</summary>
    private sealed class Lexing(CodeText text, CodeLineIndex lines, ScanCloses? closes) : ScanLexing(lines, closes)
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
                else if (IsNameStart(character))
                {
                    var past = at + 1;

                    while (past < end && IsNamePart(text[past]))
                    {
                        past++;
                    }

                    // A string's prefix: its letters one of the combinations, and a quote right after them.
                    var lower = text.Slice(at, past).ToLowerInvariant();

                    if (Prefixes.Contains(lower) && past < end && text[past] is '\'' or '"')
                    {
                        at = Literal(at, past, raw: lower.Contains('r'), formatted: lower.Contains('f'), end);
                    }
                    else
                    {
                        Add(ScanTokenKind.Name, at, past);
                        at = past;
                    }
                }
                else if (char.IsAsciiDigit(character) || (character == '.' && at + 1 < end && char.IsAsciiDigit(text[at + 1])))
                {
                    at = Number(text, at, end);
                }
                else if (character is '\'' or '"')
                {
                    at = Literal(at, at, raw: false, formatted: false, end);
                }
                else if (Punctuator(at, end) is var length and > 0)
                {
                    // A backslash that continues a line is no token: the statement goes on at the next line's first token.
                    if (character != '\\')
                    {
                        Add(ScanTokenKind.Punctuation, at, at + length);
                    }

                    at += length;
                }
                else
                {
                    // A character no token begins with - a control character, a surrogate apart from its pair - is passed over.
                    at++;
                }
            }
        }

        /// <summary>Python's comments: a <c>#</c> to its line's end, a first line's shebang among them.</summary>
        protected override long CommentEnd(long at, long end)
            => at < end && text[at] == '#' ? LineEnd(text, at, end) : at;

        /// <summary>
        /// A string whose token begins at <paramref name="start"/> - its prefix, where it has one - and whose first quote is
        /// at <paramref name="quote"/>: to its closing quote or quotes, past every field an f-string holds - each <c>{</c>
        /// code to its own <c>}</c>, or to the <c>!</c> of a conversion or the <c>:</c> of a format after it, which are passed
        /// over to the brace that closes the field, fields of their own among them, none of them the value's - read with a
        /// stack of what it is in, never a call into itself, so no nesting reaches past the reader's stack; its value's parts
        /// between its quotes and its fields, a raw one's as written. A string of one line left open ends at its line, and
        /// what holds it with it. A string a scan of the text before this one read is taken as it was read
        /// (<see cref="ScanCloses"/>), and passed over at once; what this reading finds of it, and of each string it holds, is
        /// kept for the scans of its code.
        /// </summary>
        private long Literal(long start, long quote, bool raw, bool formatted, long end)
        {
            if (Closes.Of(start, end) is { Tags: null } read)
            {
                var escapes = raw ? ScanEscapes.None : ScanEscapes.Python;
                KeepLiteral(start, new ScanLiteral(read.Parts, escapes) { Interpolated = read.Holes, LeftOpen = read.LeftOpen });

                foreach (var (from, to) in read.Spans)
                {
                    Interpolated(start, from, to);
                }

                Add(ScanTokenKind.Text, start, read.End);
                return read.End;
            }

            var literals = new LiteralStack<Quote>(this, start);
            var at = Open(literals, start, quote, raw, formatted, end);

            // How many parentheses and brackets each field's code the reading is in holds open - or held, the outermost first,
            // one for each code the stack has open - so the colon of a slice, a lambda's or a dictionary's inside them is no
            // format's: a field's own end stands at none. Braces the stack counts itself (<see cref="LiteralStack{TMark}.Depth"/>).
            var grouped = new List<int>();

            // The field's tail once its code ended short of its closer - its conversion, its format, which may hold fields of its
            // own - to the brace that closes the field: none of it the value's constant text, and none of it code read here.
            var tail = -1;

            while (at < end && literals.Open)
            {
                var character = text[at];

                if (tail >= 0)
                {
                    if (character == '{')
                    {
                        tail++;
                    }
                    else if (character == '}')
                    {
                        if (tail == 0)
                        {
                            tail = -1;
                            literals.CloseCode(at);
                        }
                        else
                        {
                            tail--;
                        }
                    }
                    else if (!literals.Mark.Triple && LineBreakAt(text, at, end) > 0)
                    {
                        break;
                    }

                    at++;
                    continue;
                }

                if (literals.InCode)
                {
                    // A brace: one the code holds open, or the field's closer - the stack's to end, which ends what it held open too.
                    if (character is '{' or '}')
                    {
                        var closes = character == '}' && literals.Depth == 0;

                        at = literals.Past(character, at, end);

                        if (closes)
                        {
                            grouped.RemoveAt(grouped.Count - 1);
                        }

                        continue;
                    }

                    // A comment in the field's code: none of it.
                    if (CommentEnd(at, end) is var comment && comment > at)
                    {
                        at = comment;
                        continue;
                    }

                    if (character is '(' or '[')
                    {
                        grouped[^1]++;
                        at++;
                        continue;
                    }

                    if (character is ')' or ']')
                    {
                        grouped[^1] = Math.Max(0, grouped[^1] - 1);
                        at++;
                        continue;
                    }

                    if (character is '\'' or '"')
                    {
                        var nested = NestedPrefix(at);

                        if (nested is { } prefixed)
                        {
                            at = Open(literals, prefixed.Start, at, prefixed.Raw, prefixed.Formatted, end);
                            continue;
                        }

                        at = Open(literals, at, at, raw: false, formatted: false, end);
                        continue;
                    }

                    // The field's own end short of its closer: a conversion, or a format after it.
                    if (grouped[^1] == 0 && literals.Depth == 0 && ((character == '!' && at + 1 < end && text[at + 1] is 'r' or 's' or 'a' && (at + 2 >= end || text[at + 2] is not '=')) || character == ':'))
                    {
                        literals.EndCode(at);
                        grouped.RemoveAt(grouped.Count - 1);
                        tail = 0;
                        continue;
                    }

                    at++;
                    continue;
                }

                var top = literals.Mark;

                if (!top.Raw && character == '\\' && at + 1 < end)
                {
                    at += LineBreakAt(text, at + 1, end) is var width and > 0 ? 1 + width : 2;
                }
                else if (top.Formatted && character == '{' && at + 1 < end && text[at + 1] == '{')
                {
                    // An escaped brace: a brace of the value, and no field.
                    at += 2;
                }
                else if (top.Formatted && character == '}' && at + 1 < end && text[at + 1] == '}')
                {
                    at += 2;
                }
                else if (top.Formatted && character == '{')
                {
                    literals.OpenCode(at, at + 1);
                    grouped.Add(0);
                    at++;
                }
                else if (character == top.Mark && (!top.Triple || (at + 2 < end && text[at + 1] == character && text[at + 2] == character)))
                {
                    var past = at + (top.Triple ? 3 : 1);
                    literals.CloseLiteral(at, past);
                    at = past;
                }
                else if (!top.Triple && LineBreakAt(text, at, end) > 0)
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
            KeepLiteral(start, new ScanLiteral(literals.Parts, raw ? ScanEscapes.None : ScanEscapes.Python) { Interpolated = literals.Holes, LeftOpen = literals.Open });
            Closes.Keep(start, new ScanLiteralRead(at, literals.Parts, literals.Spans) { Holes = literals.Holes, LeftOpen = literals.Open });
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>
        /// Opens the string whose token begins at <paramref name="start"/> and whose first quote is at <paramref name="quote"/>
        /// on <paramref name="literals"/>; answers where its text begins.
        /// </summary>
        private long Open(LiteralStack<Quote> literals, long start, long quote, bool raw, bool formatted, long end)
        {
            var mark = text[quote];
            var triple = quote + 2 < end && text[quote + 1] == mark && text[quote + 2] == mark;
            var content = quote + (triple ? 3 : 1);

            literals.OpenLiteral(new Quote(mark, triple, raw, formatted), start, content);
            return content;
        }

        /// <summary>
        /// The string a nested one's prefix names where the quote at <paramref name="at"/> follows one - the letters right
        /// before it, read back to where the name begins: where its token begins and whether it is raw or formatted; <see langword="null"/>
        /// where the quote follows none, as a scan of this code reads it.
        /// </summary>
        private (long Start, bool Raw, bool Formatted)? NestedPrefix(long at)
        {
            var begin = at;

            while (begin > 0 && char.IsAsciiLetter(text[begin - 1]) && at - begin < 2)
            {
                begin--;
            }

            if (begin == at || (begin > 0 && IsNamePart(text[begin - 1])) || !Prefixes.Contains(text.Slice(begin, at).ToLowerInvariant()))
            {
                return null;
            }

            var lower = text.Slice(begin, at).ToLowerInvariant();
            return (begin, lower.Contains('r'), lower.Contains('f'));
        }

        /// <summary>How long the punctuator at <paramref name="at"/> is: one of <see cref="Whole"/>, else one character; 0 where none is.</summary>
        private long Punctuator(long at, long end)
        {
            foreach (var whole in Whole)
            {
                if (at + whole.Length <= end && text.At(at, whole))
                {
                    return whole.Length;
                }
            }

            return Alone(text[at]);
        }
    }

    /// <summary>A string's quote, whether it is three of them, whether it is raw, and whether an f-string.</summary>
    private readonly record struct Quote(char Mark, bool Triple, bool Raw, bool Formatted);
}
