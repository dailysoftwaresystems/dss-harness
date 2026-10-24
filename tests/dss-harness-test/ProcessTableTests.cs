using System.Globalization;
using DssHarness.Core.FileSystem;
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

        var reading = await new ProcessTable(HostDoubles.Platform(PlatformId.MacOs), ps, new ScriptedKernel()).ReadAsync(Token);

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
    /// On macOS a process carries the program libproc names for it, whatever ps calls it: ps's name is the first word the
    /// process was started with, which is <c>claude</c> for a claude typed in a shell and <c>-zsh</c> for a login shell -
    /// read as the program, every Homebrew or native claude was one whose program could not be read, and refused an
    /// update of npm's. A process libproc names no program for - one that ended, a zombie - carries none.
    /// </summary>
    [Fact]
    public async Task OnMacOs_AProcessCarriesTheProgramLibprocNames_WhateverPsCallsIt()
    {
        var ps = new ScriptedPs
        {
            Programs = """
                  501 /Volumes/My Disk/tools/dssharness
                  502 -zsh
                  503 claude
                  504 node
                """,
            Lines = """
                  501     1 Mon Oct  5 10:00:00 2026 /Volumes/My Disk/tools/dssharness run ci
                  502   501 Mon Oct  5 10:00:01 2026 -zsh
                  503   502 Mon Oct  5 10:00:02 2026 claude --resume
                  504   502 Mon Oct  5 10:00:03 2026 node /usr/local/bin/claude
                """,
        };
        var kernel = new ScriptedKernel
        {
            Programs =
            {
                [501] = "/Volumes/My Disk/tools/dssharness",
                [502] = "/bin/zsh",
                [503] = "/opt/homebrew/Caskroom/claude-code/2.1.300/claude",
            },
        };

        var reading = await new ProcessTable(HostDoubles.Platform(PlatformId.MacOs), ps, kernel).ReadAsync(Token);

        Assert.Null(reading.Degraded);
        Assert.Equal(
            [
                (501, "dssharness", "/Volumes/My Disk/tools/dssharness"),
                (502, "-zsh", "/bin/zsh"),
                (503, "claude", "/opt/homebrew/Caskroom/claude-code/2.1.300/claude"),
                (504, "node", (string?)null),
            ],
            reading.Processes.Select(process => (process.Id, process.Name, process.Executable)));
        Assert.Equal([501, 502, 503, 504], kernel.Asked);
    }

    /// <summary>
    /// On Linux the table is read from <c>/proc</c> through the kernel's seam, and each answer the kernel gives is carried
    /// as it is: a program another user's process runs, which this user may not read, is none; a zombie shows neither a
    /// command line nor a program; and a process that ended between the listing and the read is left out, the rest of
    /// the reading still what was running. Each once had no test, as /proc was read in place.
    /// </summary>
    [Fact]
    public async Task OnLinux_EachAnswerTheKernelGives_IsCarriedAsItIs()
    {
        var kernel = new ScriptedKernel
        {
            Ids = [1, 42, 43, 44],
            Stats =
            {
                [1] = Stat(1, "systemd", 'S', 0),
                [42] = Stat(42, "claude", 'S', 1),
                [44] = Stat(44, "sh", 'Z', 42),
            },
            Lines = { [1] = "/sbin/init splash", [42] = "claude" },
            Programs = { [42] = "/usr/bin/node" },
        };

        var reading = await new ProcessTable(HostDoubles.Platform(PlatformId.Linux), new ScriptedPs(), kernel).ReadAsync(Token);

        Assert.Null(reading.Degraded);
        Assert.Equal(
            [
                (1, (int?)0, "systemd", "/sbin/init splash", (string?)null),
                (42, 1, "claude", "claude", "/usr/bin/node"),
                (44, 42, "sh", null, null),
            ],
            reading.Processes.Select(process => (process.Id, process.ParentId, process.Name, process.CommandLine, process.Executable)));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1234), reading.Processes[0].StartedUtc);
    }

    /// <summary>
    /// The kernel's own reading of a <c>/proc</c> laid out as Linux lays it out: a program whose file was replaced since it
    /// started - <c> (deleted)</c> after its path, as the file an update replaces under a session is - is named by the path
    /// it was started from; a process whose link cannot be read, as a zombie's, a kernel thread's or one that ended, has
    /// none; an empty command line is none; what is no process's directory is not listed; and the stat of a process that
    /// ended - its directory gone - throws the IOException the table passes over it by.
    /// </summary>
    [Fact]
    public void TheKernelsReadingOfProc_NamesTheProgramStartedFrom_AndNoneWhereItCannotBeRead()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux's kernel writes the link; one that holds its mark is laid out where a link keeps its target as written.");

        using var proc = new TempDirectory();
        proc.WriteFile("7/stat", Stat(7, "claude", 'S', 1));
        proc.WriteFile("7/cmdline", "claude\0--resume\0");
        proc.WriteFile("8/stat", Stat(8, "sh", 'Z', 7));
        proc.WriteFile("8/cmdline", string.Empty);
        proc.WriteFile("self/stat", Stat(9, "test", 'R', 1));
        File.CreateSymbolicLink(proc.Combine("7", "exe"), "/usr/bin/node (deleted)");

        var kernel = new ProcessKernel(HostDoubles.Platform(PlatformId.Linux), proc.Path);

        Assert.Equal([7, 8], kernel.Listed().Order());
        Assert.Equal(("claude --resume", "/usr/bin/node"), (kernel.CommandLine(7), kernel.Program(7)));
        Assert.Equal(((string?)null, (string?)null), (kernel.CommandLine(8), kernel.Program(8)));
        Assert.Throws<DirectoryNotFoundException>(() => kernel.Stat(10));
    }

    /// <summary>
    /// This machine's kernel names no program for a process that is not there - ENOENT on Linux, ESRCH from libproc on
    /// macOS -, as it names none for one that ended: the answer the table carries as none, never a crash or a guess.
    /// </summary>
    [Fact]
    public void ThisMachinesKernel_NamesNoProgram_ForAProcessThatIsNotThere()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows names a process's program through WMI, pinned by OnWindows_AProcessCarriesItsImagePath_AsWmiReportsIt.");

        Assert.Null(new ProcessKernel(new HostPlatform()).Program(int.MaxValue));
    }

    /// <summary>
    /// On Linux the program another user's process runs is one this user may not read (EACCES), and the kernel's answer
    /// is none: the update then counts a claude among them as one that cannot be told apart from a session. macOS names
    /// another user's program, so this is Linux's alone.
    /// </summary>
    [Fact]
    public void OnLinux_ThisMachinesKernel_NamesNoProgram_ForAnotherUsersProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only Linux keeps another user's program from this user; macOS names it, Windows asks WMI.");
        Assert.SkipWhen(
            string.Equals(Uid("/proc/self/status"), "0", StringComparison.Ordinal) || string.Equals(Uid("/proc/self/status"), Uid("/proc/1/status"), StringComparison.Ordinal),
            "Process 1 is this user's here, or this user is root, so its program is one this user may read.");

        Assert.Null(new ProcessKernel(new HostPlatform()).Program(1));
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
    /// on Windows, the link /proc names on Linux, libproc's path on macOS -, each of the three systems by its own source.
    /// </summary>
    [Fact]
    public async Task ThisMachinesTable_CarriesTheProgramThisProcessRuns()
    {
        var platform = new HostPlatform();
        var reading = await new ProcessTable(platform, new ProcessRunner(platform, FilePermissionsFactory.Create(), CliRunner.CliProgram)).ReadAsync(Token);

        Assert.Null(reading.Degraded);
        PathAssert.Same(
            LinkPaths.Resolve(Environment.ProcessPath!),
            LinkPaths.Resolve(Assert.Single(reading.Processes, process => process.Id == Environment.ProcessId).Executable!));
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

        var reading = await new ProcessTable(HostDoubles.Platform(PlatformId.MacOs), ps, new ScriptedKernel()).ReadAsync(Token);

        Assert.Equal("the process table could not be read from ps (it exited 1: ps: illegal option)", reading.Degraded);
        Assert.All(reading.Processes, process => Assert.Null(process.CommandLine));
    }

    /// <summary>
    /// A <c>/proc/&lt;id&gt;/stat</c> line for a process called <paramref name="name"/> in <paramref name="state"/>, whose
    /// parent is <paramref name="parent"/>, started 1234 seconds after boot.
    /// </summary>
    private static string Stat(int id, string name, char state, int parent)
        => string.Create(CultureInfo.InvariantCulture, $"{id} ({name}) {state} {parent} {string.Join(' ', Enumerable.Repeat("0", 17))} 123400 0 0\n");

    /// <summary>The real user id a <c>/proc/&lt;id&gt;/status</c> file names.</summary>
    private static string Uid(string status)
        => File.ReadAllLines(status).First(line => line.StartsWith("Uid:", StringComparison.Ordinal)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1];

    /// <summary>
    /// A kernel that answers as a test says: the ids /proc lists, each one's stat - a process with none ended since it was
    /// listed -, command line and program, none where the test gives none. Keeps the ids it was asked the program of.
    /// </summary>
    private sealed class ScriptedKernel : IProcessKernel
    {
        public IReadOnlyList<int> Ids { get; init; } = [];

        public Dictionary<int, string> Stats { get; } = [];

        public Dictionary<int, string> Lines { get; } = [];

        public Dictionary<int, string> Programs { get; } = [];

        public List<int> Asked { get; } = [];

        public IReadOnlyList<int> Listed() => Ids;

        public string Stat(int id) => Stats.TryGetValue(id, out var stat) ? stat : throw new FileNotFoundException($"/proc/{id}/stat ended.");

        public string? CommandLine(int id) => Lines.GetValueOrDefault(id);

        public string? Program(int id)
        {
            Asked.Add(id);

            return Programs.GetValueOrDefault(id);
        }
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
