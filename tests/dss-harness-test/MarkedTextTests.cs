using DssHarness.Core.Hosts;

namespace DssHarness.Tests;

/// <summary>
/// A stream a host wrote is read past the line that marks where what was asked of it begins: whatever a shell's startup
/// files printed before it is the host's, and not the answer.
/// </summary>
public sealed class MarkedTextTests
{
    private const string Mark = "dssharness-0123456789abcdef";

    /// <summary>
    /// The text after the last line that carries the mark - a line it ends, whatever a startup file glued ahead of it, and
    /// whatever blanks cmd left after it; the whole text where no line carries it; and nothing where the mark ends it.
    /// </summary>
    [Fact]
    public void Since_IsWhatFollowsTheLastLineTheMarkEnds()
    {
        Assert.Equal("answer\n", MarkedText.Since($"emsdk_env: setting up\n{Mark}\nanswer\n", Mark));
        Assert.Equal("answer\r\n", MarkedText.Since($"\u001b]0;prompt\u0007{Mark} \r\nanswer\r\n", Mark));
        Assert.Equal("ssh: connect to host refused\n", MarkedText.Since("ssh: connect to host refused\n", Mark));
        Assert.Equal(string.Empty, MarkedText.Since($"noise\n{Mark}", Mark));
        Assert.Equal("second\n", MarkedText.Since($"{Mark}\nfirst\n{Mark}\nsecond\n", Mark));
    }

    /// <summary>
    /// PowerShell's own error for a command it could not run quotes the command line, the mark in it twice: that line is
    /// no mark line, and cutting there lost the very words that said what went wrong.
    /// </summary>
    [Fact]
    public void Since_KeepsAPowerShellErrorThatQuotesTheMarkedLine()
    {
        var error = $"{Mark}\r\n"
            + "foo : The term 'foo' is not recognized as the name of a cmdlet\r\n"
            + $"At line:1 char:60\r\n+ echo {Mark}; [Console]::Error.WriteLine('{Mark}'); foo\r\n"
            + "+ CategoryInfo          : ObjectNotFound: (foo:String) [], CommandNotFoundException\r\n";

        Assert.Equal(error[(Mark.Length + 2)..], MarkedText.Since(error, Mark));
    }

    /// <summary>
    /// Lines before the mark are held and dropped once it comes, and the ones after handed on as they arrive; where it
    /// never comes, the held lines are handed on once the stream has ended, and only once.
    /// </summary>
    [Fact]
    public void MarkedLines_HoldUntilTheMark_AndHandOnWhatNoMarkCameFor_Once()
    {
        var marked = new List<string>();
        var gate = new MarkedLines(Mark, marked.Add);

        foreach (var line in new[] { "emsdk_env: setting up", $"prompt{Mark}", "answer" })
        {
            gate.Line(line);
        }

        gate.Ended();

        var unmarked = new List<string>();
        var held = new MarkedLines(Mark, unmarked.Add);
        held.Line("ssh: connect to host refused");

        Assert.Empty(unmarked);

        held.Ended();
        held.Ended();

        Assert.Equal(["answer"], marked);
        Assert.Equal(["ssh: connect to host refused"], unmarked);
        Assert.Null(MarkedLines.For(Mark, null));
    }
}
