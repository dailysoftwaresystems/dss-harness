using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// The isolated shell a program kept outside hands each shell command to: the isolation file carries the mechanism and the
/// whole profile and reads back as it was written; a file it cannot read, or one that names no mechanism that takes a
/// handed command, runs nothing, saying why; and the shell prefix Claude Code is given names this tool alone, or says why
/// it cannot.
/// </summary>
public sealed class IsolatedShellTests
{
    /// <summary>
    /// The file a launch writes holds bubblewrap - its program, its masks and whether it is scoped - every part of the
    /// profile and how its commands are handed over, and reads back as the same mechanism over the same profile, handed
    /// over the same way; one written with no handing reads back with none.
    /// </summary>
    [Fact]
    public void TheIsolationFile_ReadsBackAsTheMechanismAndTheWholeProfile()
    {
        var mechanism = new Bubblewrap("/usr/bin/bwrap", "0.9.0", [new IsolatedPath("/run/user/1000", Directory: true)]) { Scoped = true };
        var profile = new IsolationProfile(
            [new IsolatedPath("/home/me/.claude", Directory: true)],
            [new IsolatedWrite("/r/w", Directory: true, Writable: true), new IsolatedWrite("/r/w/.git", Directory: false, Writable: false)])
        {
            Reads = ["/r"],
            Reopened = ["/home/me/.claude/shell-snapshots"],
            Temp = "/c/tmp/abc",
            Environment = [new("TMPDIR", "/c/tmp/abc"), new("npm_config_cache", "/c/npm")],
            Sockets = ["/tmp/ssh-x/agent.1"],
        };

        var handing = new IsolatedShellHanding("/r/.orchestrators/r1/s1/claude-receipts", "/r/.orchestrators/r1/s1/claude-tmp") { Variables = ["CLAUDE_CODE_SHELL_PREFIX", IsolatedShell.FileVariable] };
        var (read, handed, problem) = IsolatedShell.Read(IsolatedShell.Written(new ProcessIsolation(mechanism, profile), handing));

        Assert.Null(problem);
        Assert.Equal((handing.Receipts, handing.ProgramTemp), (handed!.Receipts, handed.ProgramTemp));
        Assert.Equal(handing.Variables, handed.Variables);
        Assert.Null(IsolatedShell.Read(IsolatedShell.Written(new ProcessIsolation(mechanism, profile))).Handing);

        var bubblewrap = Assert.IsType<Bubblewrap>(read!.Mechanism);

        Assert.Equal((mechanism.Program, mechanism.Version, mechanism.Scoped), (bubblewrap.Program, bubblewrap.Version, bubblewrap.Scoped));
        Assert.Equal(mechanism.Masked, bubblewrap.Masked);
        Assert.Equal(profile.Hidden, read.Profile.Hidden);
        Assert.Equal(profile.Writes, read.Profile.Writes);
        Assert.Equal(profile.Reads, read.Profile.Reads);
        Assert.Equal(profile.Reopened, read.Profile.Reopened);
        Assert.Equal(profile.Sockets, read.Profile.Sockets);
        Assert.Equal(profile.Temp, read.Profile.Temp);
        Assert.Equal(profile.Environment, read.Profile.Environment);
    }

    /// <summary>
    /// A command handed over leaves the receipt at its head in the receipts' directory before it runs, and is given the one
    /// file it ends by writing its working directory to - made empty in Claude Code's own temporary directory, which no command
    /// writes, written alone, in place: bubblewrap binds that file alone. A second command whose file an earlier one left is
    /// given it again.
    /// </summary>
    [Fact]
    public void ACommandHandedOver_LeavesItsReceipt_AndIsGivenTheOneFileItEndsByWriting()
    {
        using var temp = new TempDirectory();
        var (handing, isolation) = Handed(temp);
        var receipt = IsolatedShell.NewReceipt();
        var end = handing.ProgramTemp + "/claude-1a2b-cwd";

        var (prepared, problem) = IsolatedShell.Prepared(isolation, handing, Line(IsolatedShell.Marked("echo hi", receipt), end));

        Assert.Null(problem);
        Assert.Equal([new IsolatedWrite(end, Directory: false, Writable: true)], prepared!.Profile.Writes);
        Assert.Equal(string.Empty, File.ReadAllText(end));
        Assert.Equal([receipt], Directory.GetFiles(handing.Receipts).Select(Path.GetFileName));

        var (program, arguments) = prepared.Mechanism.Command(prepared.Profile, IsolatedShell.Shell, ["-c", "true"]);

        Assert.Equal("/usr/bin/bwrap", program);
        Assert.Contains(string.Join('\n', ["--bind-try", end, end]), string.Join('\n', arguments), StringComparison.Ordinal);

        File.WriteAllText(end, "/r/w\n");
        var (again, unprepared) = IsolatedShell.Prepared(isolation, handing, Line(IsolatedShell.Marked("echo again", IsolatedShell.NewReceipt()), end));

        Assert.Null(unprepared);
        Assert.Equal([new IsolatedWrite(end, Directory: false, Writable: true)], again!.Profile.Writes);
        Assert.Equal(2, Directory.GetFiles(handing.Receipts).Length);
    }

    /// <summary>
    /// A command with no receipt at its head leaves none, and is still run; one handed with no handing named is run as it
    /// is; and one whose receipt cannot be written - its directory gone - is not run, saying why.
    /// </summary>
    [Fact]
    public void ACommandWithNoReceipt_LeavesNone_AndOneWhoseReceiptCannotBeWritten_IsNotRun()
    {
        using var temp = new TempDirectory();
        var (handing, isolation) = Handed(temp);

        var (unmarked, problem) = IsolatedShell.Prepared(isolation, handing, Line("echo hi", handing.ProgramTemp + "/claude-1a2b-cwd"));

        Assert.Null(problem);
        Assert.NotNull(unmarked);
        Assert.Empty(Directory.GetFiles(handing.Receipts));
        Assert.Same(isolation, IsolatedShell.Prepared(isolation, null, Line("echo hi", handing.ProgramTemp + "/claude-1a2b-cwd")).Isolation);

        Directory.Delete(handing.Receipts);
        var receipt = IsolatedShell.NewReceipt();
        var (refused, why) = IsolatedShell.Prepared(isolation, handing, Line(IsolatedShell.Marked("echo hi", receipt), handing.ProgramTemp + "/claude-1a2b-cwd"));

        Assert.Null(refused);
        Assert.StartsWith($"its receipt '{receipt}' could not be written in '{handing.Receipts}' (", why, StringComparison.Ordinal);
    }

    /// <summary>
    /// Neither the receipt nor the file a command ends by writing is made through a link standing at its place: the command
    /// is not run, saying why, and what the link leads to is left as it was.
    /// </summary>
    [Fact]
    public void ALinkAtTheReceiptsOrTheEndFilesPlace_IsNeverWrittenThrough()
    {
        using var temp = new TempDirectory();
        var (handing, isolation) = Handed(temp);
        var aimed = temp.Combine("aimed");
        File.WriteAllText(aimed, "kept");
        var end = handing.ProgramTemp + "/claude-1a2b-cwd";
        TestLinks.OrSkip(() => File.CreateSymbolicLink(end, aimed));

        var (endless, unmade) = IsolatedShell.Prepared(isolation, handing, Line(IsolatedShell.Marked("echo hi", IsolatedShell.NewReceipt()), end));

        Assert.Null(endless);
        Assert.StartsWith($"the file it ends by writing its working directory to, '{end}', could not be made (", unmade, StringComparison.Ordinal);

        var receipt = IsolatedShell.NewReceipt();
        File.CreateSymbolicLink(handing.Receipts + "/" + receipt, aimed);
        var (unreceipted, unwritten) = IsolatedShell.Prepared(isolation, handing, Line(IsolatedShell.Marked("echo hi", receipt), handing.ProgramTemp + "/claude-3c4d-cwd"));

        Assert.Null(unreceipted);
        Assert.StartsWith($"its receipt '{receipt}' could not be written in '{handing.Receipts}' (", unwritten, StringComparison.Ordinal);
        Assert.Equal("kept", File.ReadAllText(aimed));
    }

    /// <summary>
    /// The receipt is the engine's: given at a command's head as the shell's no-op, read back wherever the handed line
    /// quotes it - the first one, never one the command itself spells after it - and no receipt that is not 32 lowercase
    /// hexadecimal digits is put at a head.
    /// </summary>
    [Fact]
    public void AReceipt_IsPutAtTheHeadOfACommand_AndReadBackFromTheLineItIsHandedIn()
    {
        var receipt = IsolatedShell.NewReceipt();
        var other = IsolatedShell.NewReceipt();
        var marked = IsolatedShell.Marked($"echo ': dssharness-receipt {other};'", receipt);

        Assert.Equal($": dssharness-receipt {receipt}; echo ': dssharness-receipt {other};'", marked);
        Assert.Matches("^[0-9a-f]{32}$", receipt);
        Assert.Equal(receipt, IsolatedShell.ReceiptOf(Line(marked, "/t/claude-1-cwd")));
        Assert.Null(IsolatedShell.ReceiptOf(Line("echo hi", "/t/claude-1-cwd")));
        Assert.Throws<ArgumentException>(() => IsolatedShell.Marked("echo hi", "ABC"));
    }

    /// <summary>
    /// A command is run without the mark of its receipt - the first, where the line quotes it, and the one blank after it -
    /// so it finds its receipt in nothing it was started with; a receipt the command itself spells later stays as written,
    /// and a line with no mark is run as it is.
    /// </summary>
    [Fact]
    public void ACommand_IsRunWithoutTheMarkOfItsReceipt()
    {
        var receipt = IsolatedShell.NewReceipt();
        var other = IsolatedShell.NewReceipt();

        Assert.Equal(
            Line($"echo ': dssharness-receipt {other};'", "/t/claude-1-cwd"),
            IsolatedShell.Unmarked(Line(IsolatedShell.Marked($"echo ': dssharness-receipt {other};'", receipt), "/t/claude-1-cwd")));
        Assert.Equal("echo hi", IsolatedShell.Unmarked(IsolatedShell.Marked("echo hi", receipt)));
        Assert.Equal(Line("echo hi", "/t/claude-1-cwd"), IsolatedShell.Unmarked(Line("echo hi", "/t/claude-1-cwd")));
    }

    /// <summary>
    /// The file a command ends by writing is read as the shell reads the last word of its line - bare and escaped, single
    /// quoted, double quoted - and given only where it is named claude-&lt;hex&gt;-cwd directly in Claude Code's own temporary
    /// directory, the word holding nothing the shell would expand.
    /// </summary>
    [Theory]
    [InlineData("/s/t/claude-1a2b-cwd", "/s/t/claude-1a2b-cwd")]
    [InlineData("'/s/t/claude-1a2b-cwd'", "/s/t/claude-1a2b-cwd")]
    [InlineData("\"/s/t/claude-1a2b-cwd\"", "/s/t/claude-1a2b-cwd")]
    [InlineData("/s/t/claude\\-1a2b-cwd", "/s/t/claude-1a2b-cwd")]
    [InlineData("/s/t/claude-1a2b-cwd  ", "/s/t/claude-1a2b-cwd")]
    [InlineData("/s/t/sub/claude-1a2b-cwd", null)]
    [InlineData("/s/t/claude-xyz-cwd", null)]
    [InlineData("/s/t/../t/claude-1a2b-cwd", null)]
    [InlineData("/s/elsewhere/claude-1a2b-cwd", null)]
    [InlineData("$HOME/claude-1a2b-cwd", null)]
    [InlineData("\"/s/t/$x\"", null)]
    [InlineData("/s/t/claude-*-cwd", null)]
    [InlineData("'/s/t/claude-1a2b-cwd", null)]
    public void TheFileACommandEndsByWriting_IsReadAsTheShellReadsTheWord(string word, string? file)
    {
        Assert.Equal(file, IsolatedShell.EndFileOf("eval 'echo hi' < /dev/null && pwd -P >| " + word, "/s/t"));
        Assert.Null(IsolatedShell.EndFileOf("eval 'echo hi' < /dev/null", "/s/t"));
    }

    /// <summary>
    /// A word the shell would expand names no file even where its letters spell the one in Claude Code's own temporary
    /// directory - a directory whose name holds a dollar sign or a backquote, which the shell reads as a variable or a
    /// substitution unless they are quoted.
    /// </summary>
    [Theory]
    [InlineData("/s/$t/claude-1a2b-cwd", "/s/$t", null)]
    [InlineData("\"/s/$t/claude-1a2b-cwd\"", "/s/$t", null)]
    [InlineData("'/s/$t/claude-1a2b-cwd'", "/s/$t", "/s/$t/claude-1a2b-cwd")]
    [InlineData("/s/\\$t/claude-1a2b-cwd", "/s/$t", "/s/$t/claude-1a2b-cwd")]
    [InlineData("/s/`t`/claude-1a2b-cwd", "/s/`t`", null)]
    [InlineData("'/s/`t`/claude-1a2b-cwd'", "/s/`t`", "/s/`t`/claude-1a2b-cwd")]
    public void AWordTheShellWouldExpand_NamesNoFile_EvenWhereItsLettersSpellTheOne(string word, string programTemp, string? file)
        => Assert.Equal(file, IsolatedShell.EndFileOf("eval 'echo hi' < /dev/null && pwd -P >| " + word, programTemp));

    /// <summary>
    /// A file that does not read, an empty one, and one that names a mechanism that takes no command handed to it - MXC's,
    /// whose launches run whole inside - hold no isolation, each saying why.
    /// </summary>
    [Fact]
    public void AFileOfNoIsolation_IsRefused_SayingWhy()
    {
        Assert.StartsWith("its isolation file does not read (", IsolatedShell.Read("{").Problem, StringComparison.Ordinal);
        Assert.Equal("its isolation file is empty", IsolatedShell.Read("null").Problem);
        Assert.Equal(
            "its isolation file names 'mxc', no mechanism of this tool's that takes a command handed to it",
            IsolatedShell.Read("""{"mechanism":"mxc","version":"1.0.1","masked":[],"hidden":[],"writes":[],"reads":[],"reopened":[],"environment":[]}""").Problem);
    }

    /// <summary>
    /// A command handed over with no isolation file named runs nothing and ends 126, saying why on the error stream - on
    /// Windows, where nothing is handed a command, saying that.
    /// </summary>
    [Fact]
    public void ACommandWithNoIsolationFile_RunsNothing_AndSaysWhy()
    {
        using var error = new StringWriter();

        var code = IsolatedShell.Run(null, "echo hi", error);

        Assert.Equal(IsolatedShell.NotRun, code);
        Assert.Equal(
            OperatingSystem.IsWindows()
                ? "dssharness: the command was not run: nothing is handed a command on Windows, where a launch runs whole inside MXC's container."
                : $"dssharness: the command was not run: '{IsolatedShell.FileVariable}' names no isolation file ('').",
            error.ToString().TrimEnd());
    }

    /// <summary>
    /// Claude Code's shell prefix names this tool's own program and the option; a tool run by a runtime handed its assembly,
    /// or installed under a path holding " -", which Claude Code reads as an option's start, has none, saying why.
    /// </summary>
    [Fact]
    public void TheShellPrefix_NamesTheToolAlone_OrSaysWhyItCannot()
    {
        Assert.Equal(("/opt/tools/dssharness --isolated-shell", (string?)null), ClaudeShell.PrefixOf("/opt/tools/dssharness", []));
        Assert.Equal(
            ((string?)null, "this tool runs as '/usr/bin/dotnet' with its assembly handed to it, which Claude Code's shell prefix cannot name: run the installed tool"),
            ClaudeShell.PrefixOf("/usr/bin/dotnet", ["exec", "/opt/dssharness.dll"]));
        Assert.Equal(
            ((string?)null, "this tool's path '/opt/my -tools/dssharness' holds \" -\", which Claude Code's shell prefix reads as the start of an option: install the tool under a path without it"),
            ClaudeShell.PrefixOf("/opt/my -tools/dssharness", []));
    }

    /// <summary>
    /// A launch's handing in <paramref name="temp"/> - its receipts' directory and Claude Code's own temporary directory, each
    /// made, by their forward-slashed spellings as a POSIX shell names them - and bubblewrap over nothing.
    /// </summary>
    private static (IsolatedShellHanding Handing, ProcessIsolation Isolation) Handed(TempDirectory temp)
    {
        var root = temp.Path.Replace('\\', '/');
        var handing = new IsolatedShellHanding(root + "/claude-receipts", root + "/claude-tmp");
        Directory.CreateDirectory(handing.Receipts);
        Directory.CreateDirectory(handing.ProgramTemp);

        return (handing, new ProcessIsolation(new Bubblewrap("/usr/bin/bwrap", "0.9.0", []), IsolationProfile.Nothing));
    }

    /// <summary>The whole line Claude Code hands a command over in: its snapshot sourced, the command evaluated, then where it ended written to <paramref name="end"/>.</summary>
    private static string Line(string command, string end)
        => $"source /home/me/.claude/shell-snapshots/snapshot-bash-1.sh 2>/dev/null || true && eval '{command.Replace("'", "'\"'\"'", StringComparison.Ordinal)}' < /dev/null && pwd -P >| {end}";
}
