using DssHarness.Core.Hosts;

namespace DssHarness.Tests;

/// <summary>
/// The line an ssh server hands its shell is never quoted, so what it may hold is the whole defence:
/// a word any of sh, cmd or PowerShell would reinterpret must be refused before it is sent.
/// </summary>
public sealed class RemoteCommandLineTests
{
    [Fact]
    public void Join_PassesWords_EveryShellReadsLiterally()
    {
        Assert.Equal(
            "dotnet tool update --global DssHarness --version 1.2.3-beta+abc.1",
            RemoteCommandLine.Join(["dotnet", "tool", "update", "--global", "DssHarness", "--version", "1.2.3-beta+abc.1"], RemoteShell.Standard));

        Assert.Equal(
            ".dotnet/tools/dssharness host-agent",
            RemoteCommandLine.Join([".dotnet/tools/dssharness", "host-agent"], RemoteShell.Standard));

        Assert.Equal("ps -A -o comm=", RemoteCommandLine.Join(["ps", "-A", "-o", "comm="], RemoteShell.Standard));
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("quote\"inside")]
    [InlineData("it's")]
    [InlineData("$HOME")]
    [InlineData("%PATH%")]
    [InlineData("a;b")]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("`id`")]
    [InlineData("a\\b")]
    [InlineData("~/repo")]
    [InlineData("@arguments")]
    [InlineData("a,b")]
    [InlineData("a*")]
    [InlineData("")]
    public void Join_RefusesAWord_AShellCouldReinterpret(string word)
    {
        foreach (var shell in new[] { RemoteShell.Standard, RemoteShell.Cmd, RemoteShell.PowerShell }.Where(shell => word != "a\\b" || !RemoteCommandLine.IsWindows(shell)))
        {
            Assert.Throws<ArgumentException>(() => RemoteCommandLine.Join(["DssHarness", word], shell));
        }
    }

    /// <summary>
    /// A backslash is the one character a Windows shell reads literally that a POSIX one would consume, and a Windows
    /// path needs it: cmd and PowerShell take it, and a POSIX shell is never handed one.
    /// </summary>
    [Fact]
    public void Join_AcceptsABackslash_OnlyForWindowsShells_WhichNeedItInAPath()
    {
        Assert.Equal(
            @".dotnet\tools\DssHarness.exe host-agent",
            RemoteCommandLine.Join([@".dotnet\tools\DssHarness.exe", "host-agent"], RemoteShell.Cmd));
        Assert.Equal(
            @"C:\Python312\python3.exe --version",
            RemoteCommandLine.Join([@"C:\Python312\python3.exe", "--version"], RemoteShell.PowerShell));

        Assert.Throws<ArgumentException>(() => RemoteCommandLine.Join([@".dotnet\tools\DssHarness.exe"], RemoteShell.Standard));
        Assert.Equal([false, true, true], new[] { RemoteShell.Standard, RemoteShell.Cmd, RemoteShell.PowerShell }.Select(RemoteCommandLine.IsWindows));
    }

    /// <summary>
    /// cmd expands %COMSPEC%; a POSIX shell prints both words on one line, whatever its startup files printed before and
    /// in whatever case; and PowerShell prints each on a line of its own. A probe that printed nothing is no POSIX
    /// shell's answer.
    /// </summary>
    [Theory]
    [InlineData("C:\\WINDOWS\\system32\\cmd.exe C:\\WINDOWS\\system32\\cmd.exe\r\n", RemoteShell.Cmd)]
    [InlineData("%COMSPEC% %COMSPEC%\n", RemoteShell.Standard)]
    [InlineData("%COMSPEC%\r\n%COMSPEC%\r\n", RemoteShell.PowerShell)]
    [InlineData("%COMSPEC%\n%COMSPEC%\n", RemoteShell.PowerShell)]
    [InlineData("emsdk_env: setting up\n%COMSPEC% %COMSPEC%\n", RemoteShell.Standard)]
    [InlineData("%comspec% %comspec%\n", RemoteShell.Standard)]
    [InlineData("", RemoteShell.Cmd)]
    public void ReadShellProbe_TellsCmd_PowerShell_AndThePosixShellsApart(string output, RemoteShell expected)
    {
        Assert.Equal(expected, RemoteCommandLine.ReadShellProbe(output));
    }

    /// <summary>
    /// Each shell writes the mark on standard output and then on standard error, in its own syntax, before the command
    /// it was handed unchanged; and a mark no shell reads literally is refused, as any other word would be.
    /// </summary>
    [Theory]
    [InlineData(RemoteShell.Standard, "echo dssharness-ab12; echo dssharness-ab12 1>&2; dotnet --list-sdks")]
    [InlineData(RemoteShell.Cmd, "echo dssharness-ab12& echo dssharness-ab12 1>&2& dotnet --list-sdks")]
    [InlineData(RemoteShell.PowerShell, "echo dssharness-ab12; [Console]::Error.WriteLine('dssharness-ab12'); dotnet --list-sdks")]
    public void AMarkedLine_WritesTheMarkOnEachStream_BeforeTheCommand(RemoteShell shell, string expected)
    {
        Assert.Equal(expected, RemoteCommandLine.Marked("dotnet --list-sdks", "dssharness-ab12", shell));
        Assert.Throws<ArgumentException>(() => RemoteCommandLine.Marked("dotnet --list-sdks", "a;b", shell));
    }
}
