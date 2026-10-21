using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DssHarness.Core.Platform;

/// <summary>
/// What the kernel says of the machine's processes where this process reads it in place rather than asking another
/// program: the files of <c>/proc</c> on Linux, and on macOS libproc's answer of the program a process runs.
/// </summary>
/// <remarks>
/// A seam beside the table's own, for the answers the kernel gives that a running machine cannot be made to give on
/// demand: a program another user's process runs, which this user may not read; a process that ended between the
/// listing and the read; a zombie or a kernel thread, which runs none. A test scripts each, and the table's reading of
/// it is pinned on every system rather than only where the machine happens to hold one.
/// </remarks>
public interface IProcessKernel
{
    /// <summary>The id of every process <c>/proc</c> lists, in its order (Linux).</summary>
    IReadOnlyList<int> Listed();

    /// <summary>The text of the process's <c>/proc/&lt;id&gt;/stat</c> (Linux).</summary>
    /// <param name="id">The process.</param>
    /// <exception cref="IOException">The process ended since it was listed.</exception>
    /// <exception cref="UnauthorizedAccessException">This user may not read it.</exception>
    string Stat(int id);

    /// <summary>
    /// The process's command line, its arguments joined by spaces as it was started with them (Linux); null where it shows
    /// none - a kernel thread, a zombie - or ended, or may not be read.
    /// </summary>
    /// <param name="id">The process.</param>
    string? CommandLine(int id);

    /// <summary>
    /// The whole path of the program the process runs, links followed, as the kernel names it - the link
    /// <c>/proc/&lt;id&gt;/exe</c> names on Linux, <c>proc_pidpath</c> on macOS -; null where it will not say: another
    /// user's process this user may not read, one that ended, a zombie or a kernel thread.
    /// </summary>
    /// <param name="id">The process.</param>
    /// <remarks>
    /// Linux marks a program whose file was replaced or removed since it started by <c> (deleted)</c> after its path; the
    /// mark is taken off, since what is asked is where the program was started from - the file an update replaces under
    /// a session is the one that gets the mark.
    /// </remarks>
    string? Program(int id);
}

/// <inheritdoc cref="IProcessKernel"/>
public sealed class ProcessKernel : IProcessKernel
{
    /// <summary>What Linux writes after a program's path where its file was replaced or removed since it started.</summary>
    private const string DeletedMark = " (deleted)";

    /// <summary>The most <c>proc_pidpath</c> writes: libproc's <c>PROC_PIDPATHINFO_MAXSIZE</c>, four times a path's longest.</summary>
    private const int PathInfoSize = 4 * 1024;

    private readonly IHostPlatform _platform;
    private readonly string _proc;

    /// <summary>The kernel of the machine this is running on.</summary>
    /// <param name="platform">Says which system this is, and so which of the kernel's answers are read.</param>
    public ProcessKernel(IHostPlatform platform)
        : this(platform, "/proc")
    {
    }

    /// <summary>A kernel whose <c>/proc</c> is <paramref name="proc"/>: a tree a test lays out as Linux's.</summary>
    internal ProcessKernel(IHostPlatform platform, string proc)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(proc);

        _platform = platform;
        _proc = proc;
    }

    /// <inheritdoc/>
    public IReadOnlyList<int> Listed()
    {
        var ids = new List<int>();

        foreach (var directory in Directory.EnumerateDirectories(_proc))
        {
            if (int.TryParse(Path.GetFileName(directory), CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <inheritdoc/>
    public string Stat(int id) => File.ReadAllText(Path.Combine(Of(id), "stat"));

    /// <inheritdoc/>
    public string? CommandLine(int id)
    {
        try
        {
            // NUL-separated arguments, exactly as the process was started with, which is what makes this readable at
            // all: no shell has re-quoted it.
            var line = File.ReadAllText(Path.Combine(Of(id), "cmdline")).Replace('\0', ' ').Trim();

            return line.Length == 0 ? null : line;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public string? Program(int id)
        => _platform.Current switch
        {
            PlatformId.Linux => Linked(id),
            PlatformId.MacOs => LibProc(id),
            _ => throw new PlatformNotSupportedException("Windows names the program a process runs through WMI, beside its command line."),
        };

    /// <summary>The directory of the process's own files under <c>/proc</c>.</summary>
    private string Of(int id) => Path.Combine(_proc, id.ToString(CultureInfo.InvariantCulture));

    /// <summary>The program the link <c>/proc/&lt;id&gt;/exe</c> names, its mark taken off; null where the link cannot be read.</summary>
    private string? Linked(int id)
    {
        // The runtime answers no target where the link cannot be read - EACCES for another user's process, ENOENT for one
        // that ended, a zombie or a kernel thread (measured in WSL) -; one that throws for them instead is answered the same.
        try
        {
            return new FileInfo(Path.Combine(Of(id), "exe")).LinkTarget is { Length: > 0 } target
                ? target.EndsWith(DeletedMark, StringComparison.Ordinal) ? target[..^DeletedMark.Length] : target
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The program libproc names for the process; null where it names none.</summary>
    private static string? LibProc(int id)
    {
        var buffer = new byte[PathInfoSize];
        var length = ProcPidPath(id, buffer, (uint)buffer.Length);

        return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
    }

    [DllImport("libc", EntryPoint = "proc_pidpath", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ProcPidPath(int pid, byte[] buffer, uint bufferSize);
}
