using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// The one rule a child's output is cut into lines by, wherever it is read - from the child's pipes, or from a phase's log
/// - and the end of a stream that is all the runner keeps of one its caller reads line by line.
/// </summary>
public sealed class LineSplitterTests
{
    /// <summary>
    /// A line ends at a line feed, without the carriage return before it, and a last line with no line feed is still a line;
    /// a carriage return anywhere else is the line's own - however the text arrives, a character at a time or all at once.
    /// </summary>
    [Theory]
    [InlineData("a\nb\n", new[] { "a", "b" })]
    [InlineData("a\r\nb\r\n", new[] { "a", "b" })]
    [InlineData("a\nb", new[] { "a", "b" })]
    [InlineData("a\r\nb\r", new[] { "a", "b" })]
    [InlineData("50%\r100%\n", new[] { "50%\r100%" })]
    [InlineData("x\r\r\n\n", new[] { "x\r", "" })]
    [InlineData("", new string[0])]
    public void ALine_EndsAtItsLineFeed_WithoutTheCarriageReturnThatEndedIt(string text, string[] lines)
    {
        Assert.Equal(lines, Split(text, longest: null, whole: true));
        Assert.Equal(lines, Split(text, longest: null, whole: false));
    }

    /// <summary>
    /// With a longest line set, a line that runs longer is handed on in pieces of that many characters as soon as they
    /// arrive, and the rest once its line feed does; a line exactly that long, ended by a carriage return and a line feed,
    /// is one line and not itself and an empty one - which only the character after the carriage return can say.
    /// </summary>
    [Theory]
    [InlineData("abcdefghij\n", new[] { "abcd", "efgh", "ij" })]
    [InlineData("abcdefgh\n", new[] { "abcd", "efgh" })]
    [InlineData("abcd\r\nefgh\r\n", new[] { "abcd", "efgh" })]
    [InlineData("abcd\rxy\n", new[] { "abcd", "\rxy" })]
    [InlineData("abcd\r\r\n", new[] { "abcd", "\r" })]
    [InlineData("abcd\r", new[] { "abcd" })]
    [InlineData("abcdefghij", new[] { "abcd", "efgh", "ij" })]
    public void ALineLongerThanTheLongest_IsHandedOnInPieces(string text, string[] lines)
    {
        Assert.Equal(lines, Split(text, longest: 4, whole: true));
        Assert.Equal(lines, Split(text, longest: 4, whole: false));
    }

    /// <summary>A piece never ends between the two halves of a surrogate pair, which written apart would each read as a replacement character.</summary>
    [Fact]
    public void APiece_NeverPartsASurrogatePair()
    {
        var face = char.ConvertFromUtf32(0x1F600);

        var pieces = Split("abc" + face + "de\n", longest: 4, whole: false);

        Assert.Equal(["abc", face + "de"], pieces);
    }

    /// <summary>
    /// Where the caller says where a full line is cut, it is cut there, and what is left starts the next piece; an answer
    /// outside the line is no answer, and the line is cut where it is full.
    /// </summary>
    [Fact]
    public void AFullLine_IsCutWhereTheCallerSays()
    {
        Assert.Equal(["ab", "cdef", "gh"], Split("abcdefgh\n", longest: 4, whole: false, cut: full => full.StartsWith("ab", StringComparison.Ordinal) ? 2 : 4));
        Assert.Equal(["abcd", "efgh"], Split("abcdefgh\n", longest: 4, whole: false, cut: _ => 0));
        Assert.Equal(["abcd", "efgh"], Split("abcdefgh\n", longest: 4, whole: false, cut: _ => 9));
    }

    /// <summary>
    /// Where a carriage return is not a line's ending - a log written by a machine that ends its lines with a line feed
    /// alone - it is kept wherever it is, and never held past a full line.
    /// </summary>
    [Fact]
    public void WhereACarriageReturnEndsNothing_ItIsKept()
    {
        Assert.Equal(["a\r", "b"], Split("a\r\nb", longest: null, whole: true, carriageReturnEnds: false));
        Assert.Equal(["abcd", "\r"], Split("abcd\r\n", longest: 4, whole: true, carriageReturnEnds: false));
    }

    /// <summary>
    /// A line written down whole and read back is read as the pieces <see cref="LineSplitter.PiecesOf"/> says, wherever its
    /// own carriage returns fall and whichever ending the log writes after it: what a phase read as it ran is what a reader
    /// of its log reads.
    /// </summary>
    [Theory]
    [InlineData("abcdefghij", "\r\n")]
    [InlineData("abcd\r", "\r\n")]
    [InlineData("abcd\r", "\n")]
    [InlineData("abcdefgh\r\r", "\r\n")]
    [InlineData("abcdefgh\r\r", "\n")]
    [InlineData("ab\rcdefgh", "\r\n")]
    [InlineData("abc", "\r\n")]
    public void ALineReadBack_IsReadAsItsPieces(string line, string ending)
    {
        var read = Split(line + ending, longest: 4, whole: false, carriageReturnEnds: ending == "\r\n");

        Assert.Equal(LineSplitter.PiecesOf(line, 4), read);
    }

    /// <summary>
    /// The end of a stream is its last characters as they were written, whatever came before them and however it arrived;
    /// what starts with the second half of a surrogate pair whose first half was cut away starts after it.
    /// </summary>
    [Fact]
    public void TheEndOfAStream_IsItsLastCharacters()
    {
        var tail = new StreamTail(8);

        foreach (var chunk in new[] { "0123", "4567", "89ab", "cdef", "ghij" })
        {
            tail.Add(chunk);
        }

        Assert.Equal("cdefghij", tail.ToString());

        tail.Add("a very long write that is longer than the tail");
        Assert.Equal("the tail", tail.ToString());

        var shortStream = new StreamTail(8);
        shortStream.Add("ok\n");
        Assert.Equal("ok\n", shortStream.ToString());

        var face = char.ConvertFromUtf32(0x1F600);
        var parted = new StreamTail(3);
        parted.Add("ab" + face + "cd");
        Assert.Equal("cd", parted.ToString());

        var whole = new StreamTail(4);
        whole.Add("ab" + face + "cd");
        Assert.Equal(face + "cd", whole.ToString());
    }

    /// <summary>The lines <paramref name="text"/> is cut into, fed a character at a time where it is not fed <paramref name="whole"/>.</summary>
    private static List<string> Split(string text, int? longest, bool whole, Func<string, int>? cut = null, bool carriageReturnEnds = true)
    {
        var lines = new List<string>();
        var splitter = new LineSplitter(lines.Add, longest, cut, carriageReturnEnds);

        if (whole)
        {
            splitter.Add(text);
        }
        else
        {
            foreach (var character in text)
            {
                splitter.Add([character]);
            }
        }

        splitter.End();

        return lines;
    }
}
