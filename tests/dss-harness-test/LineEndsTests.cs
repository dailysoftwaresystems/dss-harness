using DssHarness.Core.Codebase;
using DssHarness.Core.Text;

namespace DssHarness.Tests;

/// <summary>
/// The one rule of where a text's lines end: at CR LF, LF and CR alike, an ending the text ends with closing its last line,
/// and an empty text one empty line - the rule the graph's lines of a file and a text's streamed pages are cut by.
/// </summary>
public sealed class LineEndsTests
{
    /// <summary>
    /// A text is cut at each CR LF, LF and lone CR, each line with where it begins, where its text ends and where its ending
    /// ends: a CR LF is one ending of two characters, never a line between them.
    /// </summary>
    [Fact]
    public void ALine_EndsAtCrLf_Lf_AndALoneCr()
    {
        const string Text = "a\r\nbb\rc\n\nd";

        Assert.Equal(
            [(0, 1, 3), (3, 5, 6), (6, 7, 8), (8, 8, 9), (9, 10, 10)],
            LineEnds.Spans(Text, (start, end, next) => (start, end, next)));
    }

    /// <summary>An ending the text ends with closes its last line and opens none, and an empty text is one empty line.</summary>
    [Fact]
    public void AFinalEnding_ClosesTheLastLine_AndAnEmptyTextIsOneEmptyLine()
    {
        Assert.Equal(["a", "b"], LineEnds.Spans("a\nb\r\n", (start, end, _) => "a\nb\r\n"[start..end]));
        Assert.Equal([(0, 0, 0)], LineEnds.Spans(string.Empty, (start, end, next) => (start, end, next)));
        Assert.Equal([(0, 0, 1)], LineEnds.Spans("\r", (start, end, next) => (start, end, next)));
    }

    /// <summary>
    /// Only a carriage return and a line feed end a line, and a CR LF is the one ending of two characters: the parts a
    /// streamed reader asks of the rule, character by character.
    /// </summary>
    [Fact]
    public void TheRulesParts_SayWhatEndsALine_AndWhatTwoCharactersAreOneEnding()
    {
        Assert.Equal([true, true, false, false], new[] { '\r', '\n', '\u2028', '\u0085' }.Select(LineEnds.Ends));
        Assert.Equal(3, LineEnds.IndexOf("abc\rd\n"));
        Assert.Equal(-1, LineEnds.IndexOf("abc"));
        Assert.True(LineEnds.Joined('\r', '\n'));
        Assert.False(LineEnds.Joined('\n', '\r'));
        Assert.False(LineEnds.Joined('\n', '\n'));
    }

    /// <summary>The graph's lines of a file are this rule's: a file's spans are the rule's spans, so a page's line numbers are the graph's.</summary>
    [Fact]
    public void TheGraphsLinesOfAFile_AreTheRulesLines()
    {
        const string Text = "first\r\nsecond\rthird\n\nlast";

        Assert.Equal(
            LineEnds.Spans(Text, (start, end, next) => new CodeLineSpan(start, end, next)),
            CodeFileText.Spans(Text));
        Assert.Equal(["first", "second", "third", string.Empty, "last"], CodeFileText.Lines(Text));
    }
}
