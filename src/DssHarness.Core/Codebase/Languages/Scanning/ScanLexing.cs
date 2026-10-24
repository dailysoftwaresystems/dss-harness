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

    /// <summary>A comment from <paramref name="start"/> to <paramref name="end"/>, kept apart; answers where the text goes on.</summary>
    protected int Comment(int start, int end)
    {
        Comments.Add(new ScanComment(start, end - start, Lines.LineAt(start), Lines.LineAt(Math.Max(start, end - 1))));
        return end;
    }

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
    /// A number at <paramref name="start"/>, as Dart and JavaScript write one: digits, the letters of a hexadecimal, a
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
/// A scanned text whose literals may hold code - Dart's and JavaScript's interpolations, a JSX element's expressions -
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
