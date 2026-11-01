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
    /// The file a launch writes holds bubblewrap - its program, its masks and whether it is scoped - and every part of the
    /// profile, and reads back as the same mechanism over the same profile.
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

        var (read, problem) = IsolatedShell.Read(IsolatedShell.Written(new ProcessIsolation(mechanism, profile)));

        Assert.Null(problem);

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
}
