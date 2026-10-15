using RepoHarness.Core.Output;

namespace RepoHarness.Tests;

/// <summary>
/// Every line that counts something says the count through one method, so one wording reads true of one thing and
/// of many, whichever command writes it. These pin that wording.
/// </summary>
public sealed class ReportTextTests
{
    [Theory]
    [InlineData(0, "path", "0 path(s)")]
    [InlineData(1, "arm", "1 arm(s)")]
    [InlineData(3, "mutation worker", "3 mutation worker(s)")]
    [InlineData(2, "buildOutput", "2 buildOutput(s)")]
    public void ACount_IsItsNumberThenWhatIsCountedWithSInBrackets(int count, string noun, string said)
        => Assert.Equal(said, ReportText.Counted(count, noun));

    [Theory]
    [InlineData(2, "copy", "2 copy(ies)")]
    [InlineData(1, "leg copy", "1 leg copy(ies)")]
    [InlineData(4, "directory", "4 directory(ies)")]
    public void ACountOfWhatEndsInAConsonantAndY_SaysIesInBrackets(int count, string noun, string said)
        => Assert.Equal(said, ReportText.Counted(count, noun));

    /// <summary>A vowel before the <c>y</c> leaves the word whole in its plural, and so does a <c>y</c> with nothing before it.</summary>
    [Theory]
    [InlineData(2, "key", "2 key(s)")]
    [InlineData(5, "day", "5 day(s)")]
    [InlineData(1, "y", "1 y(s)")]
    public void ACountOfWhatEndsInYAfterNoConsonant_SaysSInBrackets(int count, string noun, string said)
        => Assert.Equal(said, ReportText.Counted(count, noun));

    [Theory]
    [InlineData(3, "process", "3 process(es)")]
    [InlineData(2, "suffix", "2 suffix(es)")]
    [InlineData(2, "buzz", "2 buzz(es)")]
    [InlineData(2, "branch", "2 branch(es)")]
    [InlineData(2, "stash", "2 stash(es)")]
    public void ACountOfWhatEndsInAHiss_SaysEsInBrackets(int count, string noun, string said)
        => Assert.Equal(said, ReportText.Counted(count, noun));

    /// <summary>A number a reader may paste into a search or a script has no separator in it, however large it is.</summary>
    [Fact]
    public void ACount_IsWrittenInDigitsAloneHoweverLargeItIs()
    {
        Assert.Equal("1234567 file(s)", ReportText.Counted(1234567, "file"));
        Assert.Equal("5000000000 byte(s)", ReportText.Counted(5_000_000_000, "byte"));
    }

    /// <summary>Nothing failed: no line.</summary>
    [Fact]
    public void NothingFailed_IsSaidInNoLine()
        => Assert.Empty(ReportText.FailedInOneLine([], "path", (_, _) => "one", (_, _, _) => "many"));

    /// <summary>One item that failed is said as the line for one says it, given the item and why.</summary>
    [Fact]
    public void OneItemThatFailed_IsSaidAsTheLineForOneSaysIt()
        => Assert.Equal(
            ["'a.txt' failed: full"],
            ReportText.FailedInOneLine([("a.txt", "full")], "path", (item, why) => $"'{item}' failed: {why}", (_, _, _) => "many"));

    /// <summary>
    /// Many items that failed for one reason are said in one line: how many, as many as a line names and how many more,
    /// and that reason alone.
    /// </summary>
    [Fact]
    public void ManyItemsThatFailedForOneReason_AreSaidInOneLine_WithThatReason()
        => Assert.Equal(
            ["5 path(s) failed - a, b, c and 2 more: full"],
            ReportText.FailedInOneLine(
                [("a", "full"), ("b", "full"), ("c", "full"), ("d", "full"), ("e", "full")],
                "path",
                (_, _) => "one",
                (counted, items, why) => $"{counted} failed - {items}: {why}"));

    /// <summary>Many items that failed for different reasons say the first, and how many other reasons there were.</summary>
    [Fact]
    public void ManyItemsThatFailedForDifferentReasons_SayTheFirst_AndHowManyOthers()
        => Assert.Equal(
            ["3 kept copy(ies) failed - a, b, c: full; and 1 other reason(s)"],
            ReportText.FailedInOneLine(
                [("a", "full"), ("b", "held"), ("c", "full")],
                "kept copy",
                (_, _) => "one",
                (counted, items, why) => $"{counted} failed - {items}: {why}"));

    [Fact]
    public void ACountOfNothingNamed_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => ReportText.Counted(1, null!));
        Assert.Throws<ArgumentException>(() => ReportText.Counted(1, string.Empty));
    }
}
