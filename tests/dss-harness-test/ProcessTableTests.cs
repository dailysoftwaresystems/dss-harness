using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>How a machine's process table is read where another program has to be asked for it.</summary>
public sealed class ProcessTableTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// On macOS a process is named by the program it runs, read from a listing that holds the program alone: the path of
    /// a program may hold a space, and in the listing that carries the command line nothing says where the program ends.
    /// Named by the first word of that line, a tool kept under a volume whose name holds a space was a process called
    /// by half of that name, and an update of the tool did not count it as running. A process that started between the
    /// two listings is in one of them only, and is named by its line's first word.
    /// </summary>
    [Fact]
    public async Task OnMacOs_AProcessIsNamedByItsProgram_WhateverSpacesThePathToItHolds()
    {
        var ps = new ScriptedPs
        {
            Programs = """
                  501 /Volumes/My Disk/tools/dssharness
                  502 -zsh
                  504 /Applications/Some Editor.app/Contents/MacOS/Some Editor
                """,
            Lines = """
                  501     1 Mon Oct  5 10:00:00 2026 /Volumes/My Disk/tools/dssharness run ci --legs native
                  502   501 Mon Oct  5 10:00:01 2026 -zsh
                  503   501 Mon Oct  5 10:00:02 2026 /opt/homebrew/bin/ninja -C out
                  504     1 Mon Oct  5 10:00:03 2026 /Applications/Some Editor.app/Contents/MacOS/Some Editor
                """,
        };

        var reading = await new ProcessTable(HostDoubles.Platform(PlatformId.MacOs), ps).ReadAsync(Token);

        Assert.Null(reading.Degraded);
        Assert.Equal(
            [
                (501, (int?)1, "dssharness", "/Volumes/My Disk/tools/dssharness run ci --legs native"),
                (502, 501, "-zsh", "-zsh"),
                (503, 501, "ninja", "/opt/homebrew/bin/ninja -C out"),
                (504, 1, "Some Editor", "/Applications/Some Editor.app/Contents/MacOS/Some Editor"),
            ],
            reading.Processes.Select(process => (process.Id, process.ParentId, process.Name, process.CommandLine!)));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 5, 10, 0, 0))), reading.Processes[0].StartedUtc);
        Assert.Equal(["ps -A -ww -o pid=,comm=", "ps -A -ww -o pid=,ppid=,lstart=,args="], ps.Ran);
    }

    /// <summary>
    /// On macOS a process carries the program it runs as the listing of programs names it, where that is a whole path,
    /// and none where the listing names it otherwise - a login shell's <c>-zsh</c> is no path.
    /// </summary>
    [Fact]
    public async Task OnMacOs_AProcessCarriesTheProgramItRuns_WhereItIsAWholePath()
    {
        var ps = new ScriptedPs
        {
            Programs = """
                  501 /Volumes/My Disk/tools/dssharness
                  502 -zsh
                """,
            Lines = """
                  501     1 Mon Oct  5 10:00:00 2026 /Volumes/My Disk/tools/dssharness run ci
                  502   501 Mon Oct  5 10:00:01 2026 -zsh
                  503   501 Mon Oct  5 10:00:02 2026 node /usr/local/bin/claude
                """,
        };

        var reading = await new ProcessTable(HostDoubles.Platform(PlatformId.MacOs), ps).ReadAsync(Token);

        Assert.Equal(["/Volumes/My Disk/tools/dssharness", null, null], reading.Processes.Select(process => process.Executable));
    }

    /// <summary>
    /// On Windows a process carries the image path WMI reports, asked beside its command line - the image's own path,
    /// spelled whole whatever the line that started it said - and none where WMI reports none.
    /// </summary>
    [Fact]
    public async Task OnWindows_AProcessCarriesItsImagePath_AsWmiReportsIt()
    {
        const char separator = (char)31;
        var wmi = new ScriptedPowerShell
        {
            Output = string.Join(
                '\n',
                string.Join(separator, "4242", "1", string.Empty, "claude.exe", @"""C:\Users\me\AppData\Roaming\npm\\node_modules\@anthropic-ai\claude-code\bin\claude.exe"" --resume", @"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"),
                string.Join(separator, "4", "0", string.Empty, "System", string.Empty, string.Empty)),
        };

        var reading = await new ProcessTable(HostDoubles.Platform(PlatformId.Windows), wmi).ReadAsync(Token);

        Assert.Null(reading.Degraded);
        Assert.Equal(
            [(4242, @"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"), (4, (string?)null)],
            reading.Processes.Select(process => (process.Id, process.Executable)));
        Assert.Contains("$_.ExecutablePath", wmi.Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// This machine's own table carries the program this test process runs, as the runtime names it - WMI's image path
    /// on Windows, the link /proc names on Linux. macOS reads it from ps, whose listing the test above pins.
    /// </summary>
    [Fact]
    public async Task ThisMachinesTable_CarriesTheProgramThisProcessRuns()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "macOS names a program through ps, pinned by OnMacOs_AProcessCarriesTheProgramItRuns_WhereItIsAWholePath.");

        var platform = new HostPlatform();
        var reading = await new ProcessTable(platform, new ProcessRunner(platform, FilePermissionsFactory.Create())).ReadAsync(Token);

        Assert.Null(reading.Degraded);
        PathAssert.Same(Environment.ProcessPath!, Assert.Single(reading.Processes, process => process.Id == Environment.ProcessId).Executable!);
    }

    /// <summary>
    /// Either listing failing leaves the table unread, said as that with what ps said: a table named from half of what
    /// it takes would pass for a whole one.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnMacOs_EitherListingFailing_LeavesTheTableUnread_AndSaysWhy(bool programsFail)
    {
        var failed = new ProcessResult(1, string.Empty, "ps: illegal option\n", TimeSpan.Zero, TimedOut: false);
        var ps = new ScriptedPs { Programs = "  501 /bin/zsh", Lines = "  501     1 Mon Oct  5 10:00:00 2026 /bin/zsh" };

        if (programsFail)
        {
            ps.ProgramsFail = failed;
        }
        else
        {
            ps.LinesFail = failed;
        }

        var reading = await new ProcessTable(HostDoubles.Platform(PlatformId.MacOs), ps).ReadAsync(Token);

        Assert.Equal("the process table could not be read from ps (it exited 1: ps: illegal option)", reading.Degraded);
        Assert.All(reading.Processes, process => Assert.Null(process.CommandLine));
    }

    /// <summary>A PowerShell that answers the WMI query a Windows table is read from as a test says, keeping the script it was sent.</summary>
    private sealed class ScriptedPowerShell : IProcessRunner
    {
        public string Output { get; init; } = string.Empty;

        public string Script { get; private set; } = string.Empty;

        public string? FindExecutable(string command) => command;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(request.Arguments[^1]));

            return Task.FromResult(new ProcessResult(0, Output, string.Empty, TimeSpan.Zero, TimedOut: false));
        }
    }

    /// <summary>A <c>ps</c> that answers each of the two listings a macOS table is read from as a test says.</summary>
    private sealed class ScriptedPs : IProcessRunner
    {
        public string Programs { get; init; } = string.Empty;

        public string Lines { get; init; } = string.Empty;

        public ProcessResult? ProgramsFail { get; set; }

        public ProcessResult? LinesFail { get; set; }

        public List<string> Ran { get; } = [];

        public string? FindExecutable(string command) => command;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Ran.Add(string.Join(' ', [request.FileName, .. request.Arguments]));

            // Read in C, or the day and month names are whatever the machine's language makes them.
            Assert.Equal("C", request.Environment["LC_ALL"]);

            var programs = request.Arguments[^1] == "pid=,comm=";

            return Task.FromResult((programs ? ProgramsFail : LinesFail) ?? new ProcessResult(0, programs ? Programs : Lines, string.Empty, TimeSpan.Zero, TimedOut: false));
        }
    }
}
