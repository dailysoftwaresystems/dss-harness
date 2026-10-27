using DssHarness.Core.Output;

namespace DssHarness.Tests;

/// <summary>A text cut to a number of characters from its start or its end, never inside a surrogate pair.</summary>
public sealed class TextCutTests
{
    /// <summary>A head keeps at most its characters, one fewer where the last would be half of a pair; a text no longer is whole.</summary>
    [Fact]
    public void AHead_NeverEndsInsideAPair()
    {
        Assert.Equal("ab", TextCut.Head("abc", 2));
        Assert.Equal("abc", TextCut.Head("abc", 3));
        Assert.Equal("abc", TextCut.Head("abc", 9));
        Assert.Equal(string.Empty, TextCut.Head("abc", 0));
        Assert.Equal("a", TextCut.Head("a😀b", 2));
        Assert.Equal("a😀", TextCut.Head("a😀b", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextCut.Head("abc", -1));
    }

    /// <summary>A tail keeps at most its characters, one fewer where the first would be half of a pair; a text no longer is whole.</summary>
    [Fact]
    public void ATail_NeverBeginsInsideAPair()
    {
        Assert.Equal("bc", TextCut.Tail("abc", 2));
        Assert.Equal("abc", TextCut.Tail("abc", 9));
        Assert.Equal(string.Empty, TextCut.Tail("abc", 0));
        Assert.Equal("b", TextCut.Tail("a😀b", 2));
        Assert.Equal("😀b", TextCut.Tail("a😀b", 3));
    }
}
