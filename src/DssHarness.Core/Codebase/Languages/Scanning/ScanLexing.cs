namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// One run of a scanned language's lexer over a text, what it reads alike for every language: the tokens and the
/// comments it collects, each placed on the text's lines, and the characters a name, a line break and a punctuator read
/// alone are made of.
/// </summary>
/// <param name="lines">The text's lines.</param>
internal abstract class ScanLexing(CodeLineIndex lines)
{
    public List<ScanToken> Tokens { get; } = [];

    /// <summary>The text's lines, which a run over a part of the text inside this one shares.</summary>
    protected CodeLineIndex Lines { get; } = lines;

    public List<ScanComment> Comments { get; } = [];

    /// <summary>For each literal that holds code, by where it begins: where each run of code it holds directly begins and ends.</summary>
    public Dictionary<int, List<(int Start, int End)>> Interpolations { get; } = [];

    /// <summary>Whether <paramref name="character"/> may begin a name: a letter - one past ASCII among them - <c>_</c> or <c>$</c>.</summary>
    public static bool IsNameStart(char character) => char.IsLetter(character) || character is '_' or '$';

    /// <summary>Whether <paramref name="character"/> may stand in a name past its first: a letter, a digit, <c>_</c> or <c>$</c>.</summary>
    public static bool IsNamePart(char character) => char.IsLetterOrDigit(character) || character is '_' or '$';

    /// <summary>How wide the line break at <paramref name="at"/> is: 2 for CR LF, 1 for CR or LF, 0 where none is.</summary>
    public static int LineBreakAt(string text, int at, int end)
        => at >= end ? 0
            : text[at] == '\r' ? (at + 1 < end && text[at + 1] == '\n' ? 2 : 1)
            : text[at] == '\n' ? 1
            : 0;

    /// <summary>
    /// How long the punctuator <paramref name="character"/> begins is where the language keeps none longer whole there:
    /// 1 for a printable ASCII character no name holds, else 0 - a control character, a surrogate apart from its pair,
    /// which no token begins with and the lexer passes over.
    /// </summary>
    protected static int Alone(char character) => character is > ' ' and < '\u007f' && !IsNamePart(character) ? 1 : 0;

    /// <summary>A token of <paramref name="kind"/> from <paramref name="start"/> to <paramref name="end"/>.</summary>
    protected void Add(ScanTokenKind kind, int start, int end, ScanBranch branch = ScanBranch.None)
        => Tokens.Add(new ScanToken(kind, start, end - start, Lines.LineAt(start), Lines.LineAt(Math.Max(start, end - 1)), branch));

    /// <summary>
    /// Where the comment that begins at <paramref name="at"/> ends, within <paramref name="end"/>, by the language's rules -
    /// a line comment at its line's end, a block comment past its closer; <paramref name="at"/> where none begins there.
    /// The one reading of the language's comments, by which its code and the code its literals hold
    /// (<see cref="LiteralStack{TMark}"/>) are both read: a quote in a comment there - <c>// don't</c> - opens no string,
    /// and a brace in one closes nothing.
    /// </summary>
    protected abstract int CommentEnd(int at, int end);

    /// <summary>A comment from <paramref name="start"/> to <paramref name="end"/>, kept apart; answers where the text goes on.</summary>
    protected int Comment(int start, int end)
    {
        Comments.Add(new ScanComment(start, end - start, Lines.LineAt(start), Lines.LineAt(Math.Max(start, end - 1))));
        return end;
    }

    /// <summary>
    /// Where the text's code begins from <paramref name="start"/>: past a first line's shebang (<c>#!</c>), which a
    /// script's interpreter reads and the lexer keeps as a comment - JavaScript's and Dart's; <paramref name="start"/>
    /// where it begins none, or the run is not the text's own from its start.
    /// </summary>
    protected int Shebang(string text, int start, int end)
        => start == 0 && text.StartsWith("#!", StringComparison.Ordinal) ? Comment(0, LineEnd(text, 0, end)) : start;

    /// <summary>Where the line <paramref name="at"/> is on ends, within <paramref name="end"/>: at its line break, which a comment that runs to it does not hold.</summary>
    protected static int LineEnd(string text, int at, int end)
    {
        while (at < end && LineBreakAt(text, at, end) == 0)
        {
            at++;
        }

        return at;
    }

    /// <summary>Records that the literal beginning at <paramref name="literal"/> holds code from <paramref name="start"/> to <paramref name="end"/>.</summary>
    protected void Interpolated(int literal, int start, int end)
    {
        if (end > start)
        {
            (Interpolations.TryGetValue(literal, out var spans) ? spans : Interpolations[literal] = []).Add((start, end));
        }
    }

    /// <summary>
    /// What a literal's reading is in, innermost last: the literal, an interpolation's code with how many braces it holds
    /// open, a literal that code holds, and on - one stack, never a call into itself, so no nesting reaches past the
    /// reader's stack - each run of code the literal holds directly recorded as its own where it ends.
    /// </summary>
    /// <typeparam name="TMark">What the lexer keeps of each literal it opens: its quote, whether it is raw, whether it is triple.</typeparam>
    /// <param name="lexing">The run the literal is read in.</param>
    /// <param name="literal">Where the literal begins.</param>
    protected sealed class LiteralStack<TMark>(ScanLexing lexing, int literal)
    {
        private readonly List<(TMark Mark, bool Code, int Depth)> _frames = [];

        /// <summary>Where the run of code the literal holds directly that the reading is in began; -1 outside one.</summary>
        private int _code = -1;

        /// <summary>Whether the literal is still open.</summary>
        public bool Open => _frames.Count > 0;

        /// <summary>Whether the reading is in an interpolation's code.</summary>
        public bool InCode => _frames[^1].Code;

        /// <summary>What the lexer keeps of the literal the reading is in.</summary>
        public TMark Mark => _frames[^1].Mark;

        /// <summary>Opens a literal: the one read, or one an interpolation's code holds.</summary>
        public void OpenLiteral(TMark mark) => _frames.Add((mark, false, 0));

        /// <summary>Closes the literal the reading is in.</summary>
        public void CloseLiteral() => _frames.RemoveAt(_frames.Count - 1);

        /// <summary>Opens an interpolation's code, which begins at <paramref name="at"/>.</summary>
        public void OpenCode(int at)
        {
            _frames.Add((default!, true, 0));

            if (_frames.Count == 2)
            {
                _code = at;
            }
        }

        /// <summary>
        /// Reads what an interpolation's code holds at <paramref name="at"/> - its first character <paramref name="character"/> -
        /// alike in every language: a comment, by the language's own rules (<see cref="CommentEnd"/>), passed over as none
        /// of the code's, so a quote in <c>// don't</c> opens no string and a brace in it closes nothing; a brace, held open
        /// or closing (<see cref="Brace"/>). Answers where the reading goes on; <paramref name="at"/> where neither stands
        /// there, and the lexer reads what does by its language's rules - a string, a template, a regular expression.
        /// </summary>
        public int Past(char character, int at, int end)
        {
            if (lexing.CommentEnd(at, end) is var comment && comment > at)
            {
                return comment;
            }

            if (character is '{' or '}')
            {
                Brace(character == '{', at);
                return at + 1;
            }

            return at;
        }

        /// <summary>
        /// A brace in an interpolation's code at <paramref name="at"/>: an opening one held open; a closing one closing the
        /// last held open, else the code itself - recorded where it is the literal's own.
        /// </summary>
        private void Brace(bool opens, int at)
        {
            var top = _frames[^1];

            if (opens || top.Depth > 0)
            {
                _frames[^1] = top with { Depth = top.Depth + (opens ? 1 : -1) };
                return;
            }

            _frames.RemoveAt(_frames.Count - 1);

            if (_frames.Count == 1)
            {
                lexing.Interpolated(literal, _code, at);
                _code = -1;
            }
        }

        /// <summary>Ends the reading at <paramref name="at"/>: an interpolation the text ends in is read to there.</summary>
        public void End(int at)
        {
            if (_code >= 0)
            {
                lexing.Interpolated(literal, _code, at);
            }
        }
    }

    /// <summary>
    /// A number at <paramref name="start"/>, as Dart, JavaScript and PHP write one: digits, the letters of a hexadecimal, a
    /// binary or an octal one and of a suffix, separators, a fraction's point before a digit, and an exponent's sign but in
    /// a hexadecimal one. Answers where the text goes on.
    /// </summary>
    protected int Number(string text, int start, int end)
    {
        var at = start;
        var hexadecimal = start + 1 < end && text[start] == '0' && text[start + 1] is 'x' or 'X';

        while (at < end)
        {
            if (!hexadecimal && at + 1 < end && text[at] is 'e' or 'E' && text[at + 1] is '+' or '-')
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
}

/// <summary>
/// A scanned text whose literals may hold code - Dart's, JavaScript's and PHP's interpolations, a JSX element's expressions -
/// which a reader scans in its turn as a text of its own.
/// </summary>
/// <param name="Scan">The tokens, comments apart and brackets matched.</param>
/// <param name="Interpolations">For each literal that holds code, by where it begins: where each run of code it holds directly begins and ends.</param>
/// <param name="Lines">The text's lines.</param>
/// <param name="Elements">For each JSX element, by where it begins: where the name of each tag it opens stands; none where the text holds no markup.</param>
internal sealed record InterpolatedText(
    ScannedText Scan,
    IReadOnlyDictionary<int, List<(int Start, int End)>> Interpolations,
    CodeLineIndex Lines,
    IReadOnlyDictionary<int, List<(int Start, int End)>>? Elements = null);
