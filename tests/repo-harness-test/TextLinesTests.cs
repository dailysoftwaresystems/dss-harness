using RepoHarness.Core.Output;

namespace RepoHarness.Tests;

/// <summary>
/// Where a line ends: the one set of characters this tool divides lines at, which a registry's reader and a phase's last
/// lines both divide by.
/// </summary>
public sealed class TextLinesTests
{
    /// <summary>
    /// A line ends wherever <see cref="string.ReplaceLineEndings(string)"/> reads an ending and nowhere else - a carriage
    /// return and the line feed after it one ending, a carriage return the text ends with one of its own - and the text
    /// after the last ending is a line too, so joining the lines with their endings gives the text back.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("one")]
    [InlineData("one\n")]
    [InlineData("one\r")]
    [InlineData("\r")]
    [InlineData("one\r\ntwo\n\rthree")]
    [InlineData("form\ffeed\u0085next\u2028line\u2029paragraph")]
    [InlineData("tab\tvertical\vfile\u001Cgroup\u001Drecord\u001Eend no line\u00A0")]
    public void ALine_EndsWhereReplaceLineEndingsReadsAnEnding_AndTheLinesGiveTheTextBack(string text)
    {
        var lines = TextLines.Split(text);

        Assert.Equal(text.ReplaceLineEndings("\n").Split('\n'), lines.Select(line => line.Text));
        Assert.Equal(text, string.Concat(lines.Select(line => line.Text + line.Ending)));
        Assert.Equal(string.Empty, lines[^1].Ending);
        Assert.All(lines.SkipLast(1), line => Assert.Contains(line.Ending, new[] { "\r\n", "\r", "\n", "\f", "\u0085", "\u2028", "\u2029" }));
        Assert.Equal(text.AsSpan().ContainsAny(TextLines.Endings), lines.Count > 1);
    }
}
