using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Platform;

/// <summary>One process the machine was running when the table was read.</summary>
/// <param name="Id">Its process id.</param>
/// <param name="ParentId">
/// The id of the process that started it, where the platform reports one - and only where that is the very process this
/// reading lists under the id, or none is listed under it: which process started which is the platform's own relation, the
/// one the walk of a tree keeps (<see cref="Containment.EndTree"/>), decided as the table is read and never again above
/// it. On Linux and macOS it is the parent the system names now, which a child is handed on from before its parent's id can
/// be given out, so no start is compared. On Windows, which goes on naming a creator once it has gone and gives its id out
/// again, a process listed under the id that was made after the child (<see cref="CreationOrder"/>: by their sequence
/// numbers where both could be opened, by their creation times where either could not) is a later holder of the id, and
/// no parent is named.
/// </param>
/// <param name="Name">The program's name, without a directory or an extension.</param>
/// <param name="StartedUtc">
/// When it started, where the platform reports it. Recorded beside the id because ids are recycled:
/// on Windows a freed id was measured coming back after about a hundred allocations, and without the
/// start time a new process inherits whatever the harness believed about the old one.
/// </param>
/// <param name="CommandLine">
/// Its command line, where the platform exposes one. A process that does not expose it cannot be
/// matched against a build directory, which the report states rather than passing over.
/// </param>
/// <param name="Executable">
/// The full path of the program it runs, where the platform reports one: Windows' image path, the link
/// <c>/proc/&lt;id&gt;/exe</c> names on Linux, libproc's <c>proc_pidpath</c> on macOS - none for a process whose
/// program this user may not read, as another user's on Linux, or that ended. Read beside the command line because a
/// command line need not name its program by a path at all - on Linux a program a shell found on the PATH is started
/// under its bare name - and a process may write its own name and line over: node running Claude Code calls itself
/// <c>claude</c> on Linux and macOS, and only this still says node.
/// </param>
public sealed record SampledProcess(int Id, int? ParentId, string Name, DateTimeOffset? StartedUtc, string? CommandLine, string? Executable = null);

/// <summary>Reading the machine's process table.</summary>
/// <remarks>
/// A seam for the same reason file permissions are one: every platform answers this question, and
/// each answers it its own way — WMI on Windows, <c>/proc</c> on Linux, <c>ps</c> and libproc on macOS. Behind
/// the seam, so nothing above it observes the operating system, and so a test can say what the
/// machine was running without the machine having to be running it.
/// </remarks>
public interface IProcessTable
{
    /// <summary>
    /// Everything running on this machine, and why the reading is less than it should be when it is.
    /// </summary>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <remarks>
    /// Machine-wide, never limited to this process's tree: the contender a leg has to notice is one
    /// somebody started by hand, and that is in nobody's tree but their own.
    /// </remarks>
    Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>One reading of the machine's process table, and what it could not establish.</summary>
/// <param name="Processes">What was running, as far as this reading could tell.</param>
/// <param name="Degraded">
/// Why this reading cannot answer the question it is taken for, or <see langword="null"/> where it
/// can.
/// </param>
/// <remarks>
/// Carried rather than swallowed. The platform's own source is the only one on any of these systems
/// that reports a command line, and a command line is the whole of what contention is decided by:
/// fall back to what the runtime alone can see and every process matches nothing, so a machine
/// where the query is blocked reports "no contender" for every leg, for ever, without a word.
/// "Nobody looked" and "nothing was found" are different facts.
/// </remarks>
public sealed record ProcessTableReading(IReadOnlyList<SampledProcess> Processes, string? Degraded)
{
    /// <summary>
    /// The program the reading was read through, by its id - PowerShell on Windows, the <c>ps</c> whose listing it is on
    /// macOS - which lists itself among the processes this one started; <see langword="null"/> where the reading ran none
    /// (Linux reads <c>/proc</c> in place) or its runner named no id. The table started it and is its one owner, so a
    /// reader of the reading never counts it as anything this process started for anything else.
    /// </summary>
    public int? Reader { get; init; }
}

/// <summary>Builds the reader for the machine this is running on.</summary>
/// <remarks>
/// One of the few places that observes the operating system, alongside <see cref="HostPlatform"/>
/// and <see cref="FilePermissionsFactory"/>. Everything above it depends on
/// <see cref="IProcessTable"/> and never asks which system it is on.
/// </remarks>
public static class ProcessTableFactory
{
    /// <summary>The reader for this machine.</summary>
    /// <param name="platform">Says which machine this is.</param>
    /// <param name="processRunner">Starts the program a platform has to be asked through.</param>
    /// <param name="output">
    /// Where what a stop of that program could not reach or end is said; nowhere where none is given - the process runner's
    /// own reading, made while it ends a session, is never stopped.
    /// </param>
    public static IProcessTable Create(IHostPlatform platform, IProcessRunner processRunner, Output.IHarnessOutput? output = null)
        => new ProcessTable(platform, processRunner, output: output);
}

/// <inheritdoc cref="IProcessTable"/>
/// <param name="platform">Says which machine this is.</param>
/// <param name="processRunner">Starts the program a platform has to be asked through: PowerShell on Windows, ps on macOS.</param>
/// <param name="kernel">What the kernel says in place - Linux's <c>/proc</c>, macOS's libproc -; this machine's where none is given.</param>
/// <param name="output">
/// Where what a stop of PowerShell or ps could not reach or end is said, under the tool's name
/// (<see cref="UnreachedSaying"/>); nowhere where none is given.
/// </param>
public sealed class ProcessTable(IHostPlatform platform, IProcessRunner processRunner, IProcessKernel? kernel = null, Output.IHarnessOutput? output = null) : IProcessTable
{
    /// <summary>Says a line through the output, under the tool's name.</summary>
    private readonly Action<string> _say = line => output?.Warn(Hosts.ToolPackage.Command, line);

    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IProcessKernel _kernel = kernel ?? new ProcessKernel(platform);

    /// <summary>
    /// When a process a Windows reading lists was made - by its id and its listed creation time, as a file time - as the
    /// walk of a tree orders a creator and its child (<see cref="Containment.OrderOf(int, long)"/>): this machine's where
    /// it is Windows, and the creation time alone elsewhere, where no handle to a Windows process can be opened; a test's
    /// in its place, for a reading whose clock stepped.
    /// </summary>
    internal Func<int, long, CreationOrder> MadeAt { get; init; } = static (id, created)
        => OperatingSystem.IsWindows() ? Containment.OrderOf(id, created) : new CreationOrder(created, Sequence: null);

    /// <summary>Why a platform query failed, in one line, with what it said.</summary>
    private static string HostFailure(string what, ProcessResult result)
    {
        var said = result.StandardError.Trim();

        return said.Length == 0
            ? $"{what} (it exited {result.ExitCode})"
            : $"{what} (it exited {result.ExitCode}: {said.Split(Environment.NewLine)[0]})";
    }

    /// <summary>
    /// What the Windows query separates its fields with: the unit separator, which no process name
    /// or command line contains. Written as a code point rather than typed in as the byte, because a
    /// source file holding a control character is one git and grep read as binary.
    /// </summary>
    private const char UnitSeparator = (char)31;

    /// <inheritdoc/>
    public async Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var (table, problem, reader) = _platform.Current switch
        {
            PlatformId.Windows => await WindowsTableAsync(cancellationToken).ConfigureAwait(false),
            PlatformId.Linux => LinuxTable(),
            _ => await MacTableAsync(cancellationToken).ConfigureAwait(false),
        };

        if (table.Count > 0)
        {
            return new ProcessTableReading(table, null) { Reader = reader };
        }

        // The fallback still places a tool on the machine by name, which is worth keeping; what it
        // cannot do is say what that tool was pointed at, and that is what decides contention. So it
        // is returned with the reason attached rather than in place of one.
        return new ProcessTableReading(
            FromDiagnostics(),
            problem ?? "the process table came back empty, which no running machine's is") { Reader = reader };
    }


    /// <summary>
    /// The table from WMI, which is the only source on Windows that carries a command line. Asked
    /// through PowerShell because reading WMI in process would add a dependency the tool does not
    /// otherwise need, and the query is sent base64-encoded so no layer of quoting can change it.
    /// </summary>
    private async Task<(IReadOnlyList<SampledProcess> Table, string? Problem, int? Reader)> WindowsTableAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $s = [char]31
            Get-CimInstance Win32_Process | ForEach-Object {
                $created = ''
                if ($_.CreationDate) { $created = $_.CreationDate.ToUniversalTime().ToString('o') }
                $line = $_.CommandLine
                if ($line) { $line = $line -replace '[\r\n]+', ' ' } else { $line = '' }
                [string]::Join($s, @([string]$_.ProcessId, [string]$_.ParentProcessId, $created, [string]$_.Name, $line, [string]$_.ExecutablePath))
            }
            """;

        var result = await _processRunner.RunAsync(
            new ProcessRequest
            {
                FileName = "powershell",
                Arguments =
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                ],
            },
            cancellationToken).SayingUnreached("PowerShell reading the process table", _say).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return ([], HostFailure("the process table could not be read from WMI through PowerShell", result), result.ProcessId);
        }

        var processes = new List<SampledProcess>();

        foreach (var line in Lines(result.StandardOutput))
        {
            var fields = line.Split(UnitSeparator);

            if (fields.Length < 5 || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            processes.Add(new SampledProcess(
                id,
                int.TryParse(fields[1], CultureInfo.InvariantCulture, out var parent) ? parent : null,
                CleanName(fields[3]),
                DateTimeOffset.TryParse(fields[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var started) ? started : null,
                fields[4].Length == 0 ? null : fields[4],
                fields.Length > 5 && fields[5].Length > 0 ? fields[5] : null));
        }

        return (WithTheirCreators(processes), null, result.ProcessId);
    }

    /// <summary>
    /// <paramref name="processes"/>, as a Windows reading lists them, each naming as its parent only the process that made
    /// it (<see cref="SampledProcess.ParentId"/>): Windows goes on naming a creator once it has gone, and gives its id out
    /// again, so a process listed under that id and made after the child (<see cref="CreationOrder"/>) is a later holder of
    /// it, and none is named. Ordered as the walk of a tree orders a creator and its child, the clock deciding only where a
    /// sequence number could not be read; a process the reading could not date keeps the parent it names, as nothing
    /// tells it from that parent's.
    /// </summary>
    private List<SampledProcess> WithTheirCreators(List<SampledProcess> processes)
    {
        var listed = processes.GroupBy(process => process.Id).ToDictionary(same => same.Key, same => same.First());
        var made = new Dictionary<int, CreationOrder?>();

        CreationOrder? MadeOf(int id)
        {
            if (!made.TryGetValue(id, out var order))
            {
                order = listed.TryGetValue(id, out var process) && process.StartedUtc is { } started ? MadeAt(id, started.ToFileTime()) : null;
                made[id] = order;
            }

            return order;
        }

        return [.. processes.Select(process => process.ParentId is { } parent
            && listed.ContainsKey(parent)
            && MadeOf(process.Id) is { } child
            && MadeOf(parent) is { } creator
            && child.MadeBefore(creator)
                ? process with { ParentId = null }
                : process)];
    }

    /// <summary>
    /// The table from <c>/proc</c>, read in place through the kernel's seam: the kernel already publishes every field
    /// needed, so no other program has to be installed for a leg to know what else is running.
    /// </summary>
    private (IReadOnlyList<SampledProcess> Table, string? Problem, int? Reader) LinuxTable()
    {
        var processes = new List<SampledProcess>();

        foreach (var id in _kernel.Listed())
        {
            try
            {
                var stat = _kernel.Stat(id);

                var fields = ProcStat.FieldsAfterName(stat);
                if (fields is null)
                {
                    continue;
                }

                var name = ProcStat.Name(stat) ?? id.ToString(CultureInfo.InvariantCulture);

                int? parent = ProcStat.Number(fields, ProcStat.ParentField) is { } ppid ? (int)ppid : null;

                // Ticks since boot, counted from the epoch rather than from a boot time worked out
                // from the clock as it is now. What this is for is telling one process from the next
                // holder of its id between two samples, and a boot time derived from the wall clock
                // moves for every live process the moment that clock steps — which fragments one
                // contender into two and makes a report say it came and went when it never left.
                DateTimeOffset? started = ProcStat.Number(fields, ProcStat.StartTicksField) is { } ticks
                    ? DateTimeOffset.UnixEpoch.AddSeconds(ticks / ProcStat.TicksPerSecond)
                    : null;

                processes.Add(new SampledProcess(id, parent, CleanName(name), started, _kernel.CommandLine(id), _kernel.Program(id)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The process exited between the listing and the read, which is ordinary at this
                // rate of change; the other entries in this sample are still what was running.
            }
        }

        return (processes, null, null);
    }

    /// <summary>
    /// The table from <c>ps</c>, which is where macOS publishes another process's command line, and the program each
    /// runs from libproc. <c>lstart</c> rather than elapsed time, because an identity must not shift between samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked twice, because <c>ps</c> separates its columns with spaces and the path of a program may hold one: in the
    /// one listing nothing says where a program ends and its first argument begins, and named by the first word of its
    /// line a tool kept under <c>/Volumes/My Disk</c> was a process called <c>My</c> - which an update of that tool
    /// then did not count as running. The second listing holds the name alone, last on its line. A process that
    /// started between the two is in one of them only, and is named by its line's first word, as all were.
    /// </para>
    /// <para>
    /// That name is the first word the process was started with - <c>claude</c> for a claude typed in a shell, <c>-zsh</c>
    /// for a login shell - and never the program it runs, which only libproc says (<see cref="IProcessKernel.Program"/>):
    /// read from ps's name, every claude a shell started was one whose program could not be read.
    /// </para>
    /// </remarks>
    private async Task<(IReadOnlyList<SampledProcess> Table, string? Problem, int? Reader)> MacTableAsync(CancellationToken cancellationToken)
    {
        // C, so the day and month names are the ones the format below reads. A machine set to another language
        // would otherwise leave every start time unknown.
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

        var named = await _processRunner.RunAsync(
            new ProcessRequest { FileName = "ps", Arguments = ["-A", "-ww", "-o", "pid=,comm="], Environment = environment },
            cancellationToken).SayingUnreached("'ps -A -ww -o pid=,comm=' reading the process table", _say).ConfigureAwait(false);

        if (!named.Succeeded)
        {
            return ([], HostFailure("the process table could not be read from ps", named), null);
        }

        var names = new Dictionary<int, string>();

        foreach (var line in Lines(named.StandardOutput))
        {
            var (fields, name) = SplitLeading(line, 1);

            if (fields.Count == 1 && name.Length > 0 && int.TryParse(fields[0], CultureInfo.InvariantCulture, out var id))
            {
                names[id] = name;
            }
        }

        var result = await _processRunner.RunAsync(
            new ProcessRequest { FileName = "ps", Arguments = ["-A", "-ww", "-o", "pid=,ppid=,lstart=,args="], Environment = environment },
            cancellationToken).SayingUnreached("'ps -A -ww -o pid=,ppid=,lstart=,args=' reading the process table", _say).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return ([], HostFailure("the process table could not be read from ps", result), result.ProcessId);
        }

        var processes = new List<SampledProcess>();

        foreach (var line in Lines(result.StandardOutput))
        {
            // pid, ppid and the five words of lstart are fixed; everything after them is the
            // command line, spaces and all.
            var (fields, commandLine) = SplitLeading(line, 7);

            if (fields.Count < 7 || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var started = DateTimeOffset.TryParseExact(
                string.Join(' ', fields.Skip(2).Take(5)),
                ["ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
                out var startedAt)
                ? startedAt
                : (DateTimeOffset?)null;

            names.TryGetValue(id, out var itsName);

            processes.Add(new SampledProcess(
                id,
                int.TryParse(fields[1], CultureInfo.InvariantCulture, out var parent) ? parent : null,
                CleanName(itsName ?? FirstToken(commandLine)),
                started,
                commandLine.Length == 0 ? null : commandLine,
                _kernel.Program(id)));
        }

        return (processes, null, result.ProcessId);
    }

    /// <summary>
    /// What the runtime alone can see: the id and the name of each process this user may open, and
    /// no start time for any of them. Used only when the platform's own source failed, and reported
    /// with the limit that carries.
    /// </summary>
    private static IReadOnlyList<SampledProcess> FromDiagnostics()
    {
        var processes = new List<SampledProcess>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                // No start time at all, rather than one worked out from the clock. Process.StartTime
                // on Linux is ticks since boot added to a boot time derived from the clock as it is
                // now, so a clock that steps hands the same process a new identity between two
                // samples and splits one contender into two. Unknown is the honest answer, and what
                // reads these knows what to do with it: ProcessSamplingSession names such a process
                // by its id alone in every sample and says so among the report's limits.
                processes.Add(new SampledProcess(process.Id, null, CleanName(process.ProcessName), null, null));
            }
            catch (InvalidOperationException)
            {
                // It exited while the list was being built.
            }
            finally
            {
                process.Dispose();
            }
        }

        return processes;
    }

    private static IEnumerable<string> Lines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);

    /// <summary>The first <paramref name="count"/> whitespace-separated tokens, and the rest of the line unsplit.</summary>
    private static (IReadOnlyList<string> Fields, string Remainder) SplitLeading(string line, int count)
    {
        var fields = new List<string>(count);
        var index = 0;

        while (fields.Count < count && index < line.Length)
        {
            while (index < line.Length && line[index] == ' ')
            {
                index++;
            }

            var start = index;

            while (index < line.Length && line[index] != ' ')
            {
                index++;
            }

            if (index > start)
            {
                fields.Add(line[start..index]);
            }
        }

        return (fields, line[Math.Min(index, line.Length)..].Trim());
    }

    private static string FirstToken(string commandLine)
    {
        var space = commandLine.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? commandLine : commandLine[..space];
    }

    /// <summary>A program's name without its directory or extension, which is how tools are configured.</summary>
    private static string CleanName(string name)
        => name.Length == 0 ? string.Empty : Path.GetFileNameWithoutExtension(name.Trim());
}
