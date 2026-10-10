using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

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
