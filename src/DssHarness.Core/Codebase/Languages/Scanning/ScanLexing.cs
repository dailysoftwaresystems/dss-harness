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

    public List<ScanComment> Comments { get; } = [];

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
        => Tokens.Add(new ScanToken(kind, start, end - start, lines.LineAt(start), lines.LineAt(Math.Max(start, end - 1)), branch));

    /// <summary>A comment from <paramref name="start"/> to <paramref name="end"/>, kept apart; answers where the text goes on.</summary>
    protected int Comment(int start, int end)
    {
        Comments.Add(new ScanComment(start, end - start, lines.LineAt(start), lines.LineAt(Math.Max(start, end - 1))));
        return end;
    }
}
