using System.Globalization;
using DssHarness.Core.Hosts;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// A stream a host wrote is read past the line that marks where what was asked of it begins: whatever a shell's startup
/// files printed before it is the host's, and not the answer.
/// </summary>
public sealed class MarkedTextTests
{
    private const string Mark = "dssharness-0123456789abcdef";

    /// <summary>
    /// The text after the first line that carries the mark - a line it ends, whatever a startup file glued ahead of it, and
    /// whatever blanks cmd left after it; the whole text where no line carries it; nothing where the mark ends it; and
    /// everything after the first, a later mark line among it.
    /// </summary>
    [Fact]
    public void Since_IsWhatFollowsTheFirstLineTheMarkEnds()
    {
        Assert.Equal("answer\n", MarkedText.Since($"emsdk_env: setting up\n{Mark}\nanswer\n", Mark));
        Assert.Equal("answer\r\n", MarkedText.Since($"\u001b]0;prompt\u0007{Mark} \r\nanswer\r\n", Mark));
        Assert.Equal("ssh: connect to host refused\n", MarkedText.Since("ssh: connect to host refused\n", Mark));
        Assert.Equal(string.Empty, MarkedText.Since($"noise\n{Mark}", Mark));
        Assert.Equal($"first\n{Mark}\nsecond\n", MarkedText.Since($"{Mark}\nfirst\n{Mark}\nsecond\n", Mark));
    }

    /// <summary>
    /// What a stream keeps is what it handed on as it arrived, line for line, by the one rule both read it with: before the
    /// mark, after it, a mark line twice, a mark glued to a startup file's last write, and no mark at all.
    /// </summary>
    [Theory]
    [InlineData("emsdk_env: setting up\n{0}\nanswer\n")]
    [InlineData("{0}\nfirst\n{0}\nsecond\n")]
    [InlineData("+ echo {0}\n{0}\nanswer\n")]
    [InlineData("prompt{0}\nanswer\nmore\n")]
    [InlineData("ssh: connect to host refused\n")]
    public void WhatAStreamKeeps_IsWhatItHandedOn(string shape)
    {
        var text = string.Format(CultureInfo.InvariantCulture, shape, Mark);
        var handedOn = new List<string>();
        var gate = new MarkedLines(Mark, handedOn.Add);

        foreach (var line in text.Split('\n')[..^1])
        {
            gate.Line(line);
        }

        gate.Ended();

        Assert.Equal(MarkedText.Since(text, Mark).Split('\n')[..^1], handedOn);
    }

    /// <summary>
    /// The line a POSIX shell is handed, run by each such shell this machine has - tcsh among them where it is there, as on
    /// macOS - and by bash and zsh tracing every command as <c>set -x</c> in a startup file makes them: each stream opens
    /// with the mark as a line of its own, no trace of a command reads as one, and no file is left where it ran.
    /// </summary>
    [Fact]
    public async Task AMarkedLine_RunByEachPosixShellHere_OpensBothStreamsWithTheMark_AndLeavesNothingBehind()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The account's shell an ssh server starts on Windows is cmd or PowerShell.");

        var line = RemoteCommandLine.Marked("ls /no/such/directory", Mark, RemoteShell.Standard);
        var shells = new[]
            {
                ("/bin/sh", "-c"),
                ("/bin/bash", "-c"),
                ("/bin/bash", "-xc"),
                ("/bin/zsh", "-fc"),
                ("/bin/zsh", "-fxc"),
                ("/bin/tcsh", "-fc"),
                ("/bin/csh", "-fc"),
            }
            .Where(shell => File.Exists(shell.Item1))
            .ToList();
        var processes = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);

        foreach (var (shell, flags) in shells)
        {
            using var temp = new TempDirectory();
            var result = await processes.RunAsync(
                new ProcessRequest { FileName = shell, Arguments = [flags, line], WorkingDirectory = temp.Path },
                TestContext.Current.CancellationToken);

            Assert.True(result.StandardOutput.Split('\n')[0] == Mark, $"{shell} {flags} wrote '{result.StandardOutput}' to standard output");
            Assert.True(
                result.StandardError.Split('\n').TakeWhile(each => each != Mark).All(each => !MarkedText.IsMarkLine(each, Mark)),
                $"{shell} {flags} wrote '{result.StandardError}' to standard error");
            Assert.Contains("no/such/directory", MarkedText.Since(result.StandardError, Mark), StringComparison.Ordinal);
            Assert.NotEqual(result.StandardError, MarkedText.Since(result.StandardError, Mark));
            Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
        }

        Assert.NotEmpty(shells);
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
