using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The lexical rules of Dart for the structural scanner: comments (<c>//</c>, <c>///</c>, and <c>/* */</c>, which nest)
/// kept apart; a string - in single or double quotes, triple-quoted over lines, raw (<c>r'...'</c>) - as one token with
/// every interpolation it holds (<c>$name</c>, <c>${...}</c> with braces and strings of its own), never read as code;
/// names that may hold <c>$</c>; numbers with their separators; <c>?..</c>, <c>...?</c>, <c>...</c>, <c>??=</c>,
/// <c>?.</c>, <c>..</c>, <c>=&gt;</c> and <c>??</c> whole and every other punctuator alone, so <c>&gt;&gt;</c> closes two
/// lists of type arguments. Where a string's interpolations hold code (<c>${...}</c>), the lexer says where, so that
/// code is read in its turn as a text of its own.
/// </summary>
internal static class DartLexer
{
    /// <summary>The punctuators read whole, the longest first.</summary>
    private static readonly string[] Whole = ["?..", "...?", "...", "??=", "?.", "..", "=>", "??"];

    /// <summary>Scans <paramref name="text"/> by Dart's rules.</summary>
    /// <param name="text">A file's text.</param>
    public static DartText Scan(string text) => Scan(text, 0, text.Length, CodeFileText.LineIndex(text));

    /// <summary>
    /// Scans the part of <paramref name="text"/> from <paramref name="start"/> to <paramref name="end"/> by Dart's rules,
    /// its tokens placed in the whole text: an interpolation's code, read where it stands.
    /// </summary>
    /// <param name="text">A file's text.</param>
    /// <param name="start">Where the part begins.</param>
    /// <param name="end">Where it ends.</param>
    /// <param name="lines">The text's lines.</param>
    public static DartText Scan(string text, int start, int end, CodeLineIndex lines)
    {
        var lexing = new Lexing(text, lines);
        lexing.Run(start, end);
        return new DartText(new ScannedText(text, lexing.Tokens, lexing.Comments), lexing.Interpolations, lines);
    }

    /// <summary>One run of the lexer over a text.</summary>
    private sealed class Lexing(string text, CodeLineIndex lines) : ScanLexing(lines)
    {
        /// <summary>For each string that holds code, by where it begins: where each <c>${...}</c> it holds directly begins and ends.</summary>
        public Dictionary<int, List<(int Start, int End)>> Interpolations { get; } = [];

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
                else if (character == '/' && at + 1 < end && text[at + 1] == '/')
                {
                    var close = at;

                    while (close < end && LineBreakAt(text, close, end) == 0)
                    {
                        close++;
                    }

                    at = Comment(at, close);
                }
                else if (character == '/' && at + 1 < end && text[at + 1] == '*')
                {
                    at = Comment(at, BlockEnd(at, end));
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
                    at = Number(at, end);
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

        /// <summary>Past the block comment that opens at <paramref name="at"/>: each <c>/*</c> inside it opens one more, as Dart's nest.</summary>
        private int BlockEnd(int at, int end)
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

        /// <summary>A number: digits, letters of a hexadecimal one, separators, a fraction's point before a digit and an exponent's sign.</summary>
        private int Number(int start, int end)
        {
            var at = start;

            while (at < end)
            {
                if (at + 1 < end && text[at] is 'e' or 'E' && text[at + 1] is '+' or '-' && !text.AsSpan(start, at - start).Contains('x'))
                {
                    at += 2;
                }
                else if (text[at] == '.' && at + 1 < end && char.IsAsciiDigit(text[at + 1]))
                {
                    at++;
                }
                else if (IsNamePart(text[at]) && text[at] != '$')
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

        /// <summary>
        /// A string whose first quote is at <paramref name="quote"/>, a raw one's <c>r</c> at <paramref name="start"/>: to its
        /// closing quote or quotes, past every interpolation it holds - each <c>${</c> code to its own <c>}</c>, which may hold
        /// braces, comments and strings of their own - read with a stack of what it is in, never a call into itself, so no
        /// nesting reaches past the reader's stack. A string of one line left open ends at its line, and what holds it with it.
        /// </summary>
        private int Literal(int start, int quote, bool raw, int end)
        {
            var frames = new List<Frame>();
            var at = Open(frames, quote, raw, end);

            // Where the interpolation the string holds directly, that the reading is in, began; -1 outside one.
            var code = -1;

            while (at < end && frames.Count > 0)
            {
                var top = frames[^1];
                var character = text[at];

                if (top.Code)
                {
                    if (character == '{')
                    {
                        frames[^1] = top with { Depth = top.Depth + 1 };
                        at++;
                    }
                    else if (character == '}')
                    {
                        if (top.Depth == 0)
                        {
                            frames.RemoveAt(frames.Count - 1);

                            if (frames.Count == 1)
                            {
                                Interpolated(start, code, at);
                                code = -1;
                            }
                        }
                        else
                        {
                            frames[^1] = top with { Depth = top.Depth - 1 };
                        }

                        at++;
                    }
                    else if (character == '/' && at + 1 < end && text[at + 1] == '*')
                    {
                        at = BlockEnd(at, end);
                    }
                    else if (character is '\'' or '"')
                    {
                        at = Open(frames, at, raw: at > 0 && text[at - 1] == 'r' && (at < 2 || !IsNamePart(text[at - 2])), end);
                    }
                    else
                    {
                        at++;
                    }

                    continue;
                }

                if (!top.Raw && character == '\\')
                {
                    at += 2;
                }
                else if (!top.Raw && character == '$' && at + 1 < end && text[at + 1] == '{')
                {
                    frames.Add(new Frame(Mark: '\0', Triple: false, Raw: false, Code: true, Depth: 0));
                    at += 2;

                    if (frames.Count == 2)
                    {
                        code = at;
                    }
                }
                else if (character == top.Mark && (!top.Triple || (at + 2 < end && text[at + 1] == character && text[at + 2] == character)))
                {
                    frames.RemoveAt(frames.Count - 1);
                    at += top.Triple ? 3 : 1;
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

            // An interpolation the text ends in is read to where the string ends.
            if (code >= 0)
            {
                Interpolated(start, code, at);
            }

            Add(ScanTokenKind.Text, start, at);
            return at;
        }

        /// <summary>Records that the string beginning at <paramref name="literal"/> holds code from <paramref name="start"/> to <paramref name="end"/>.</summary>
        private void Interpolated(int literal, int start, int end)
        {
            if (end > start)
            {
                (Interpolations.TryGetValue(literal, out var spans) ? spans : Interpolations[literal] = []).Add((start, end));
            }
        }

        /// <summary>Opens the string whose first quote is at <paramref name="quote"/> on <paramref name="frames"/>; answers where its text begins.</summary>
        private int Open(List<Frame> frames, int quote, bool raw, int end)
        {
            var mark = text[quote];
            var triple = quote + 2 < end && text[quote + 1] == mark && text[quote + 2] == mark;

            frames.Add(new Frame(mark, triple, raw, Code: false, Depth: 0));
            return quote + (triple ? 3 : 1);
        }

        /// <summary>How long the punctuator at <paramref name="at"/> is: one of <see cref="Whole"/>, else one character; 0 where none is.</summary>
        private int Punctuator(int at, int end)
        {
            foreach (var whole in Whole)
            {
                if (at + whole.Length <= end && string.CompareOrdinal(text, at, whole, 0, whole.Length) == 0)
                {
                    return whole.Length;
                }
            }

            return Alone(text[at]);
        }
    }

    /// <summary>What a string's reading is in: a string, by its quote, or an interpolation's code, by how many braces it holds open.</summary>
    private readonly record struct Frame(char Mark, bool Triple, bool Raw, bool Code, int Depth);
}

/// <summary>A Dart text the lexer has scanned: its tokens, and where the strings among them hold code.</summary>
/// <param name="Scan">The tokens, comments apart and brackets matched.</param>
/// <param name="Interpolations">For each string that holds code, by where it begins: where each <c>${...}</c> it holds directly begins and ends.</param>
/// <param name="Lines">The text's lines.</param>
internal sealed record DartText(ScannedText Scan, IReadOnlyDictionary<int, List<(int Start, int End)>> Interpolations, CodeLineIndex Lines);
