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
/// <item>macOS marks a process <c>P_LEXIT</c> as it enters its exit, before its descriptors are freed, which
/// <c>sysctl</c>'s <c>kinfo_proc</c> gives as <c>P_WEXIT</c>, and an ended one is a zombie.</item>
/// </list>
/// Whether a process has ended - gone, or a zombie, whose every descriptor each system has closed before it is one - is
/// asked here too (<see cref="HasEnded"/>), on Linux and macOS: Windows tells it by a process's handle, signalled only once
/// its handle table has run down.
/// </summary>
internal static class ProcessGoing
{
    /// <summary><c>PF_EXITING</c>: the thread has entered its exit.</summary>
    private const uint LinuxExiting = 0x0000_0004;

    /// <summary><c>P_WEXIT</c>: the process has entered its exit.</summary>
    private const int MacExiting = 0x0000_2000;

    /// <summary><c>SZOMB</c>: the process has ended, and is not yet reaped.</summary>
    private const byte MacZombie = 5;

    /// <summary>Where <c>p_flag</c> lies in <c>kinfo_proc</c>, after its first two pointers' union and two more pointers.</summary>
    private const int MacFlagOffset = 32;

    /// <summary>Where <c>p_stat</c> lies in <c>kinfo_proc</c>.</summary>
    private const int MacStatOffset = 36;

    /// <summary>Where <c>p_pid</c> lies in <c>kinfo_proc</c>.</summary>
    private const int MacPidOffset = 40;

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
    /// macOS <c>SZOMB</c>, its descriptors freed before it became one. A zombie is ended whether or not anything ever reaps
    /// it. Whether the process carrying the id is the one a caller means is the caller's to tell, by its start.
    /// </summary>
    /// <param name="processId">The process's id.</param>
    public static bool HasEnded(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            return EveryThread(processId, ThreadEnded);
        }

        return OperatingSystem.IsMacOS() && MacProcess(processId) is { } known && (!known.Listed || known.Stat == MacZombie);
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
            && MacProcess(process.Id) is { } known
            && (!known.Listed || (known.Flags & MacExiting) != 0 || known.Stat == MacZombie);
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
    /// What macOS keeps of the process with <paramref name="processId"/> - whether it knows it at all, and its flags and
    /// state - or <see langword="null"/> where it could not be asked, or answered with a record that is not that process's,
    /// which says nothing of it.
    /// </summary>
    private static (bool Listed, int Flags, byte Stat)? MacProcess(int processId)
    {
        // CTL_KERN, KERN_PROC, KERN_PROC_PID.
        int[] name = [1, 14, 1, processId];
        var size = (nuint)0;

        if (sysctl(name, (uint)name.Length, null, ref size, IntPtr.Zero, 0) != 0)
        {
            return null;
        }

        if (size == 0)
        {
            return (false, 0, 0);
        }

        var bytes = new byte[(int)size];

        if (sysctl(name, (uint)name.Length, bytes, ref size, IntPtr.Zero, 0) != 0)
        {
            return null;
        }

        if (size == 0)
        {
            return (false, 0, 0);
        }

        if ((int)size < MacPidOffset + sizeof(int) || BitConverter.ToInt32(bytes, MacPidOffset) != processId)
        {
            return null;
        }

        return (true, BitConverter.ToInt32(bytes, MacFlagOffset), bytes[MacStatOffset]);
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int informationClass, ref ExtendedBasicInformation information, uint length, out uint returned);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int sysctl(int[] name, uint nameLength, [Out] byte[]? old, ref nuint oldLength, IntPtr @new, nuint newLength);

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
