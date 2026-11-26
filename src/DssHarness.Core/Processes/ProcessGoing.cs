using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Processes;

/// <summary>
/// Whether a child is going: ended, or in its exit - asked of the system the moment it is wanted, never waited on. Each
/// system marks a process as exiting before it closes what the process held open, so a child whose input closed with its
/// exit already reads as going when that is seen, and one that closed its input itself and runs on does not:
/// <list type="bullet">
/// <item>Windows sets a process's delete flag as its last thread exits, before its handles are run down
/// (<c>NtQueryInformationProcess</c>'s extended basic information, <c>IsProcessDeleting</c>);</item>
/// <item>Linux marks each thread <c>PF_EXITING</c> as it enters its exit, before the files its threads share are closed with
/// the last of them (<c>/proc/&lt;pid&gt;/task/*/stat</c>), and a thread that has ended is a zombie or dead;</item>
/// <item>macOS marks a process <c>P_LEXIT</c> as it enters its exit, before its descriptors are freed, which libproc's
/// short information gives as <c>PROC_FLAG_INEXIT</c>, and an ended one is a zombie.</item>
/// </list>
/// Whether a process has ended - gone, or a zombie, whose every descriptor each system has closed before it is one - is
/// asked here too (<see cref="HasEnded"/>), on Linux and macOS, and whether a process group has (<see cref="GroupHasEnded(Func{IReadOnlyList{int}}, Func{bool}, Func{int, bool})"/>):
/// Windows tells it by a process's handle, signalled only once its handle table has run down.
/// </summary>
/// <remarks>
/// macOS takes a process that enters its exit off its list of live processes, onto its list of zombies, and marks it dead
/// to every reference before its threads have closed its descriptors (xnu's <c>proc_prepareexit</c>), and calls it a zombie
/// only once they are closed (<c>proc_exit</c>). All that while nothing can take a reference to it - the C library's
/// <c>kill</c> answers it as it answers a zombie, a group's signal passes over it, and the runtime cannot read its start -
/// yet it still holds what it held. So it is read here by the one lookup that finds it then: libproc's information asked to
/// find a zombie too, which looks the id up in the table every listed process is in, a zombie and one in its exit among
/// them (<see cref="MacRead"/>), so it is never taken for ended before it is one.
/// </remarks>
internal static class ProcessGoing
{
    /// <summary><c>PF_EXITING</c>: the thread has entered its exit.</summary>
    private const uint LinuxExiting = 0x0000_0004;

    /// <summary><c>PROC_FLAG_INEXIT</c>: the process has entered its exit.</summary>
    private const uint MacExiting = 0x0000_0004;

    /// <summary><c>SZOMB</c>: the process has ended, and is not yet reaped.</summary>
    private const uint MacZombie = 5;

    /// <summary><c>PROC_PIDT_SHORTBSDINFO</c>: libproc's short information of a process, which any user may ask of any process.</summary>
    private const int MacShortInformation = 13;

    /// <summary>The size of <c>struct proc_bsdshortinfo</c>, all of it read.</summary>
    private const int MacShortSize = 64;

    /// <summary><c>PROC_PIDTBSDINFO</c>: libproc's information of a process, its start among it, which a process of this user's alone is asked.</summary>
    private const int MacInformation = 3;

    /// <summary>The size of <c>struct proc_bsdinfo</c>, all of it read.</summary>
    private const int MacInformationSize = 136;

    /// <summary>Where <c>pbsi_ppid</c> lies in the short information, after <c>pbsi_pid</c>, which opens it.</summary>
    private const int MacShortParent = 4;

    /// <summary>Where <c>pbsi_pgid</c> lies in the short information.</summary>
    private const int MacShortGroup = 8;

    /// <summary>Where <c>pbsi_status</c> lies in the short information.</summary>
    private const int MacShortState = 12;

    /// <summary>Where <c>pbsi_flags</c> lies in the short information, after the sixteen characters of <c>pbsi_comm</c>.</summary>
    private const int MacShortFlags = 32;

    /// <summary>Where <c>pbi_pid</c> lies in the information, after <c>pbi_flags</c>, <c>pbi_status</c> and <c>pbi_xstatus</c>.</summary>
    private const int MacPid = 12;

    /// <summary>Where <c>pbi_start_tvsec</c> lies in the information: after twelve words, <c>pbi_comm</c>, <c>pbi_name</c> and six more words.</summary>
    private const int MacStartSeconds = 120;

    /// <summary>Where <c>pbi_start_tvusec</c> lies in the information.</summary>
    private const int MacStartMicroseconds = 128;

    /// <summary>ESRCH: no process carries the id.</summary>
    private const int NoSuchProcess = 3;

    /// <summary>The microseconds in a second, which macOS's record of a start counts beside its seconds (<see cref="MacStarted"/>).</summary>
    internal const ulong MicrosecondsInASecond = 1_000_000;

    /// <summary><c>IsProcessDeleting</c> in the extended basic information's flags.</summary>
    private const uint WindowsDeleting = 0x4;

    /// <summary>
    /// Whether <paramref name="process"/>, a child of this process, is going: ended, or in its exit. Read before and
    /// after the system is asked, so an id the runtime reaped and the system gave another process between the two is
    /// never read as the child's.
    /// </summary>
    /// <param name="process">The child.</param>
    public static bool IsGoing(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (Ended(process))
        {
            return true;
        }

        var exiting = InItsExit(process);

        return Ended(process) || exiting;
    }

    /// <summary>
    /// Whether the process carrying <paramref name="processId"/> has ended, on Linux and macOS: none carries the id, or it
    /// is a zombie - on Linux every thread of it a zombie or dead, the files they share closed with the last of them; on
    /// macOS <c>SZOMB</c>, its descriptors freed before it became one, and one in its exit, which no signal reaches and
    /// nothing can take a reference to, still not ended (<see cref="MacRead"/>). A zombie is ended whether or not anything ever reaps
    /// it. Whether the process carrying the id is the one a caller means is the caller's to tell, by its start.
    /// </summary>
    /// <param name="processId">The process's id.</param>
    public static bool HasEnded(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            return EveryThread(processId, ThreadEnded);
        }

        return OperatingSystem.IsMacOS() && MacRead(processId) is { } known && (!known.Listed || known.State == MacZombie);
    }

    /// <summary>
    /// Whether a process group has ended, on this system, as
    /// <see cref="GroupHasEnded(PlatformId, Func{IReadOnlyList{int}}, Func{bool}, Func{int, bool})"/> says.
    /// </summary>
    /// <param name="listed">Reads the processes the system lists in the group now; <see langword="null"/> where it would not list them.</param>
    /// <param name="signalled">Whether anything in the group can be signalled, asked now.</param>
    /// <param name="ended">Whether a listed process has ended as its caller counts it: as the system says (<see cref="HasEnded"/>) at least.</param>
    public static bool GroupHasEnded(Func<IReadOnlyList<int>?> listed, Func<bool> signalled, Func<int, bool> ended)
        => GroupHasEnded(OperatingSystem.IsMacOS() ? PlatformId.MacOs : PlatformId.Linux, listed, signalled, ended);

    /// <summary>
    /// Whether a process group has ended, on Linux and macOS - every process the system lists in it has
    /// (<see cref="HasEnded"/>), so a zombie nobody reaps holds it for none:
    /// <list type="bullet">
    /// <item>on Linux, too, once nothing in it can be signalled (<paramref name="signalled"/>), which is so only once every
    /// member is reaped; an empty listing of a group that can still be signalled is no end, since <c>/proc</c> may hide a
    /// member - one of another user's under <c>hidepid</c>;</item>
    /// <item>on macOS by its listing alone, an empty one among them: libproc's listing walks every process, a zombie and one in
    /// its exit among them, hiding none, and the group's signal passes over a zombie and one in its exit, so a group whose
    /// members are still closing what they held cannot be signalled - the signal decides only where the listing could not
    /// be read.</item>
    /// </list>
    /// </summary>
    /// <param name="system">The system it runs on.</param>
    /// <param name="listed">Reads the processes the system lists in the group now - read only where they decide; <see langword="null"/> where it would not list them.</param>
    /// <param name="signalled">Whether anything in the group can be signalled, asked now - asked only where it decides.</param>
    /// <param name="ended">Whether a listed process has ended: <see cref="HasEnded"/>, or a test's.</param>
    internal static bool GroupHasEnded(PlatformId system, Func<IReadOnlyList<int>?> listed, Func<bool> signalled, Func<int, bool> ended)
    {
        ArgumentNullException.ThrowIfNull(listed);
        ArgumentNullException.ThrowIfNull(signalled);
        ArgumentNullException.ThrowIfNull(ended);

        if (system == PlatformId.MacOs)
        {
            return listed() is { } members ? members.All(ended) : !signalled();
        }

        return !signalled() || (listed() is { Count: > 0 } found && found.All(ended));
    }

    /// <summary>Whether the runtime has seen the child end.</summary>
    private static bool Ended(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // No process is associated any more: it has gone.
            return true;
        }
    }

    /// <summary>Whether the system marks the child as in its exit: what it says on each system, as the type's remarks give.</summary>
    private static bool InItsExit(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return Deleting(process);
        }

        if (OperatingSystem.IsLinux())
        {
            return EveryThread(process.Id, ThreadExiting);
        }

        return OperatingSystem.IsMacOS()
            && MacRead(process.Id) is { } known
            && (!known.Listed || (known.Flags & MacExiting) != 0 || known.State == MacZombie);
    }

    /// <summary>Whether Windows has set the process's delete flag, which its last thread sets as it exits.</summary>
    private static bool Deleting(Process process)
    {
        var information = new ExtendedBasicInformation { Size = (nuint)Marshal.SizeOf<ExtendedBasicInformation>() };

        try
        {
            return NtQueryInformationProcess(process.SafeHandle, 0, ref information, (uint)Marshal.SizeOf<ExtendedBasicInformation>(), out _) == 0
                && (information.Flags & WindowsDeleting) != 0;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether every thread of the process with <paramref name="processId"/> is as <paramref name="thread"/> says of its
    /// <c>stat</c> line, on Linux - the files the threads share close only with the last of them - or none is left at all.
    /// </summary>
    private static bool EveryThread(int processId, Func<string, bool> thread)
    {
        var tasks = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/task";

        try
        {
            foreach (var task in Directory.EnumerateDirectories(tasks))
            {
                string stat;

                try
                {
                    stat = File.ReadAllText(Path.Combine(task, "stat"));
                }
                catch (IOException)
                {
                    // The thread has gone since it was listed: its entry is not there, or no process answers for it.
                    continue;
                }

                if (!thread(stat))
                {
                    return false;
                }
            }

            return true;
        }
        catch (DirectoryNotFoundException)
        {
            // The process has gone.
            return true;
        }
    }

    /// <summary>
    /// Whether a thread's <c>stat</c> line says it has ended - a zombie, or dead - or has entered its exit: the third field,
    /// its state, and the ninth, its flags, counted after the name in parentheses, which may hold spaces and parentheses
    /// of its own.
    /// </summary>
    /// <param name="stat">The line.</param>
    internal static bool ThreadExiting(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);

        var fields = ProcStat.FieldsAfterName(stat);

        return fields is { Length: > 6 }
            && (Ended(fields)
                || (uint.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var flags) && (flags & LinuxExiting) != 0));
    }

    /// <summary>
    /// Whether a thread's <c>stat</c> line says it has ended - a zombie, or dead - which it is only once it has let go of
    /// the files its process's threads share: its state, the third field, counted after the name in parentheses.
    /// </summary>
    /// <param name="stat">The line.</param>
    internal static bool ThreadEnded(string stat)
    {
        ArgumentNullException.ThrowIfNull(stat);

        return ProcStat.FieldsAfterName(stat) is { Length: > 0 } fields && Ended(fields);
    }

    /// <summary>Whether a thread's state, the first of the fields after its name, is a zombie's or a dead one's.</summary>
    private static bool Ended(string[] fields) => fields[0] is "Z" or "X" or "x";

    /// <summary>
    /// What macOS keeps of the process with <paramref name="processId"/>, read now in one lookup of its id - whether it is
    /// listed at all, and its state, flags, parent and group - or <see langword="null"/> where it could not be asked, or
    /// answered of another process, which says nothing of it.
    /// </summary>
    /// <remarks>
    /// libproc's short information, asked to find a zombie too (any argument but zero): xnu's <c>proc_pidinfo</c> looks the
    /// id up as <c>proc_find</c> does and, where that refuses - a zombie, and one in its exit, both already marked dead to
    /// every reference - as <c>proc_find_zombref</c> does, which finds what is so marked. Each process listed - in the
    /// table of live processes or of zombies, which libproc's listing walks - is in the one table of ids that lookup reads,
    /// from the moment it is listed to the moment it is reaped, when it leaves both at once; so a process this finds no
    /// trace of is one the listing does not list. Asked of any user's process, and with no table walked: one lookup.
    /// </remarks>
    /// <param name="processId">The process's id.</param>
    internal static MacReading? MacRead(int processId)
    {
        var bytes = new byte[MacShortSize];
        var read = ProcPidInfo(processId, MacShortInformation, 1, bytes, bytes.Length);

        if (read != bytes.Length)
        {
            return read == 0 && Marshal.GetLastPInvokeError() == NoSuchProcess ? new MacReading(false, 0, 0, 0, 0) : null;
        }

        return BitConverter.ToInt32(bytes, 0) == processId
            ? new MacReading(
                true,
                BitConverter.ToUInt32(bytes, MacShortState),
                BitConverter.ToUInt32(bytes, MacShortFlags),
                BitConverter.ToInt32(bytes, MacShortParent),
                BitConverter.ToInt32(bytes, MacShortGroup))
            : null;
    }

    /// <summary>
    /// When macOS records the process with <paramref name="processId"/> started - seconds and microseconds since 1970 - read
    /// now, through its exit and while it is a zombie alike, until it is reaped (<see cref="MacRead"/>); <see langword="null"/>
    /// where no process carries the id, or this user may not ask it of the one that does.
    /// </summary>
    /// <param name="processId">The process's id.</param>
    internal static (ulong Seconds, ulong Microseconds)? MacStarted(int processId)
    {
        var bytes = new byte[MacInformationSize];

        return ProcPidInfo(processId, MacInformation, 1, bytes, bytes.Length) == bytes.Length && BitConverter.ToInt32(bytes, MacPid) == processId
            ? (BitConverter.ToUInt64(bytes, MacStartSeconds), BitConverter.ToUInt64(bytes, MacStartMicroseconds))
            : null;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int informationClass, ref ExtendedBasicInformation information, uint length, out uint returned);

    /// <summary><c>proc_pidinfo</c>: one kind of libproc's information of a process, in bytes written; none where it could not be read, the error number said.</summary>
    [DllImport("libc", EntryPoint = "proc_pidinfo", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ProcPidInfo(int processId, int flavor, ulong argument, [Out] byte[] buffer, int bufferSize);

    /// <summary>
    /// <c>PROCESS_EXTENDED_BASIC_INFORMATION</c>: its size, then <c>PROCESS_BASIC_INFORMATION</c>, then the flags
    /// <c>IsProcessDeleting</c> is among.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedBasicInformation
    {
        public nuint Size;
        public int ExitStatus;
        public IntPtr PebBaseAddress;
        public nuint AffinityMask;
        public int BasePriority;
        public nuint UniqueProcessId;
        public nuint InheritedFromUniqueProcessId;
        public uint Flags;
    }
}

/// <summary>What macOS keeps of one process, as libproc's short information gives it (<see cref="ProcessGoing.MacRead"/>).</summary>
/// <param name="Listed">Whether the system lists a process under the id at all: live, in its exit, or a zombie not yet reaped.</param>
/// <param name="State">Its state, <c>p_stat</c>: <c>SZOMB</c> once it has ended.</param>
/// <param name="Flags">Its flags, <c>PROC_FLAG_INEXIT</c> among them once it has entered its exit.</param>
/// <param name="Parent">The id of its parent, as the system keeps it now: the process it was handed to once its own parent went.</param>
/// <param name="Group">The id of the process group it is in.</param>
internal readonly record struct MacReading(bool Listed, uint State, uint Flags, int Parent, int Group);
