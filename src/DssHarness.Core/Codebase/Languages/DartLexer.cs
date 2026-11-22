using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The lexical rules of Dart for the structural scanner: comments (<c>//</c>, <c>///</c>, and <c>/* */</c>, which nest)
/// and a first line's script tag (<c>#!</c>) kept apart; a string - in single or double quotes, triple-quoted over
/// lines, raw (<c>r'...'</c>) - as one token with every interpolation it holds (<c>$name</c>, <c>${...}</c> with braces
/// and strings of its own), never read as code, and where its value's constant parts are written - between its quotes,
/// a triple-quoted one's past a first line of white space alone, and its holes - and how they are decoded
/// (<see cref="ScanLiteral"/>): a raw one's as written, any other's by Dart's escapes; names that may hold <c>$</c>;
/// numbers with their separators; <c>?..</c>, <c>...?</c>, <c>...</c>, <c>??=</c>, <c>?.</c>, <c>..</c>, <c>=&gt;</c>
/// and <c>??</c> whole and every other punctuator alone, so <c>&gt;&gt;</c> closes two lists of type arguments. Where a
/// string's interpolations hold code (<c>$name</c>, <c>${...}</c>), the lexer says where, so that code is read in its
/// turn as a text of its own.
/// </summary>
internal static class DartLexer
{
    /// <summary>The punctuators read whole, the longest first.</summary>
    private static readonly string[] Whole = ["?..", "...?", "...", "??=", "?.", "..", "=>", "??"];

    /// <summary>Scans <paramref name="text"/> by Dart's rules.</summary>
    /// <param name="text">A file's text.</param>
    public static InterpolatedText Scan(CodeText text) => Scan(text, 0, text.Length, text.LineIndex);

    /// <summary>
    /// Scans the part of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/> by Dart's rules,
    /// its tokens placed in the whole text: an interpolation's code, read where it stands.
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
            var at = Shebang(text, start, end);

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
                else if (character == 'r' && at + 1 < end && text[at + 1] is '\'' or '"')
                {
                    at = Literal(at, at + 1, raw: true, end);
                }
                else if (IsNameStart(character))
                {
                    var past = at + 1;

                    while (past < end && IsNamePart(text[past]))
                    {
                        past++;
                    }

                    Add(ScanTokenKind.Name, at, past);
                    at = past;
                }
                else if (char.IsAsciiDigit(character) || (character == '.' && at + 1 < end && char.IsAsciiDigit(text[at + 1])))
                {
                    at = Number(text, at, end);
                }
                else if (character is '\'' or '"')
                {
                    at = Literal(at, at, raw: false, end);
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

        /// <summary>Dart's comments: <c>//</c> and <c>///</c> to their line's end, <c>/* */</c> past its closer, as they nest (<see cref="BlockEnd"/>).</summary>
        protected override long CommentEnd(long at, long end)
            => at + 1 >= end || text[at] != '/' ? at
                : text[at + 1] == '/' ? LineEnd(text, at, end)
                : text[at + 1] == '*' ? BlockEnd(at, end)
                : at;

        /// <summary>Past the block comment that opens at <paramref name="at"/>: each <c>/*</c> inside it opens one more, as Dart's nest.</summary>
        private long BlockEnd(long at, long end)
        {
            var depth = 0;

            while (at < end)
            {
                if (text[at] == '/' && at + 1 < end && text[at + 1] == '*')
                {
                    depth++;
                    at += 2;
                }
                else if (text[at] == '*' && at + 1 < end && text[at + 1] == '/')
                {
                    at += 2;

                    if (--depth == 0)
                    {
                        return at;
                    }
                }
                else
                {
                    at++;
                }
            }

            return end;
        }

        /// <summary>
        /// A string whose first quote is at <paramref name="quote"/>, a raw one's <c>r</c> at <paramref name="start"/>: to its
        /// closing quote or quotes, past every interpolation it holds - each <c>${</c> code to its own <c>}</c>, which may hold
        /// braces, comments and strings of their own, and each bare <c>$name</c> (<see cref="ScanLexing.BareHole"/>) - read
        /// with a stack of what it is in, never a call into itself, so no nesting reaches past the reader's stack; its value's
        /// parts between its quotes and its holes, a raw one's as written. A string of one line left open ends at its line,
        /// and what holds it with it. A string a scan of the text before this one read is taken as it was read
        /// (<see cref="ScanCloses"/>), and passed over at once; what this reading finds of it, and of each string it holds,
        /// is kept for the scans of its code.
        /// </summary>
        private long Literal(long start, long quote, bool raw, long end)
        {
            if (Closes.Of(start, end) is { Tags: null } read)
            {
                KeepLiteral(start, new ScanLiteral(read.Parts, raw ? ScanEscapes.None : ScanEscapes.Dart) { Interpolated = read.Holes, LeftOpen = read.LeftOpen });

                foreach (var (from, to) in read.Spans)
                {
                    Interpolated(start, from, to);
                }

                Add(ScanTokenKind.Text, start, read.End);
                return read.End;
            }

            var literals = new LiteralStack<Quote>(this, start);
            var at = Open(literals, start, quote, raw, end);

            while (at < end && literals.Open)
            {
                var character = text[at];

                if (literals.InCode)
                {
                    if (literals.Past(character, at, end) is var past && past > at)
                    {
                        at = past;
                    }
                    else if (character is '\'' or '"')
                    {
                        // A raw string's token begins at its r, as a scan of this code reads it.
                        var nestedRaw = at > 0 && text[at - 1] == 'r' && (at < 2 || !IsNamePart(text[at - 2]));
                        at = Open(literals, nestedRaw ? at - 1 : at, at, nestedRaw, end);
                    }
                    else
                    {
                        at++;
                    }

                    continue;
                }

                var top = literals.Mark;

                if (!top.Raw && character == '\\')
                {
                    at += 2;
                }
                else if (!top.Raw && character == '$' && at + 1 < end && text[at + 1] == '{')
                {
                    literals.OpenCode(at, at + 2);
                    at += 2;
                }
                else if (!top.Raw && character == '$' && BareHole(text, at, end, variable: false) is { } hole)
                {
                    // A bare $name: a run of code of its own, a value the string reads.
                    literals.Hole(at, hole.Start, hole.End, hole.Past);
                    at = hole.Past;
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
            KeepLiteral(start, new ScanLiteral(literals.Parts, raw ? ScanEscapes.None : ScanEscapes.Dart) { Interpolated = literals.Holes, LeftOpen = literals.Open });
            Closes.Keep(start, new ScanLiteralRead(at, literals.Parts, literals.Spans) { Holes = literals.Holes, LeftOpen = literals.Open });
            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>
        /// Opens the string whose token begins at <paramref name="start"/> - a raw one's at its r - and whose first quote is at
        /// <paramref name="quote"/> on <paramref name="literals"/>; answers where its text begins - a triple-quoted one's past
        /// a first line of white space alone, which Dart leaves out of its value.
        /// </summary>
        private long Open(LiteralStack<Quote> literals, long start, long quote, bool raw, long end)
        {
            var mark = text[quote];
            var triple = quote + 2 < end && text[quote + 1] == mark && text[quote + 2] == mark;
            var content = quote + (triple ? 3 : 1);

            literals.OpenLiteral(new Quote(mark, triple, raw), start, triple ? PastBlankFirstLine(content, end) : content);
            return content;
        }

        /// <summary>
        /// Past the first line of a triple-quoted string from <paramref name="at"/> where it holds nothing but white space -
        /// each blank possibly after a backslash - and its line break: where the string's value begins, as Dart reads it;
        /// <paramref name="at"/> where the line holds anything else.
        /// </summary>
        private long PastBlankFirstLine(long at, long end)
        {
            var index = at;

            while (index < end && (text[index] is ' ' or '\t' || (text[index] == '\\' && index + 1 < end && text[index + 1] is ' ' or '\t' or '\r' or '\n')))
            {
                index++;
            }

            return LineBreakAt(text, index, end) is var width and > 0 ? index + width : at;
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

    /// <summary>A string's quote, whether it is three of them, and whether it is raw.</summary>
    private readonly record struct Quote(char Mark, bool Triple, bool Raw);
}

