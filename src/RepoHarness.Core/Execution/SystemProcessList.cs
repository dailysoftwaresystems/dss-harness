using System.Runtime.InteropServices;

namespace RepoHarness.Core.Execution;

/// <summary>
/// When a process on this Windows machine started, read from the list Windows keeps of every process for whoever asks.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.Process.StartTime"/> opens the process, which one of another account refuses to a
/// process not running elevated - a service's, one started elevated: about a third of those running on a developer's
/// machine, measured. The list asks nothing of any process, so what started when is known of each of them. Windows only,
/// as its callers make sure.
/// </remarks>
internal static class SystemProcessList
{
    /// <summary><c>SystemProcessInformation</c>: every process, one entry after another.</summary>
    private const int ProcessInformation = 5;

    /// <summary><c>STATUS_INFO_LENGTH_MISMATCH</c>: the list outgrew the room given for it.</summary>
    private const int LengthMismatch = unchecked((int)0xC0000004);

    /// <summary>Where an entry keeps its process's creation time, whatever the pointer size.</summary>
    private const int CreateTimeOffset = 0x20;

    /// <summary>
    /// The start of the process carrying <paramref name="processId"/> as <see cref="System.Diagnostics.Process.StartTime"/>
    /// gives it - the same instant, converted the same way - as its ticks; <see langword="null"/> where no process
    /// carries the id, or the list could not be read.
    /// </summary>
    /// <param name="processId">The process id.</param>
    public static long? StartTicks(int processId)
    {
        var length = 1 << 20;

        // The list grows while it is read on a machine starting processes, so the room is grown and it is read again;
        // a list that outgrows every room given is left unread rather than read forever.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var list = Marshal.AllocHGlobal(length);

            try
            {
                var status = NtQuerySystemInformation(ProcessInformation, list, length, out var needed);

                if (status == LengthMismatch)
                {
                    length = Math.Max(length * 2, needed + (1 << 16));
                    continue;
                }

                return status < 0 ? null : Find(list, processId);
            }
            finally
            {
                Marshal.FreeHGlobal(list);
            }
        }

        return null;
    }

    /// <summary>The start of the entry for <paramref name="processId"/>, walking the list entry by entry.</summary>
    private static long? Find(IntPtr list, int processId)
    {
        // SYSTEM_PROCESS_INFORMATION: the process id follows the image name and the priority, both of which a pointer's
        // size moves.
        var idOffset = IntPtr.Size == 8 ? 0x50 : 0x44;
        var entry = list;

        while (true)
        {
            if (Marshal.ReadIntPtr(entry, idOffset) == processId)
            {
                return DateTime.FromFileTime(Marshal.ReadInt64(entry, CreateTimeOffset)).Ticks;
            }

            var next = Marshal.ReadInt32(entry);

            if (next == 0)
            {
                return null;
            }

            entry += next;
        }
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int needed);
}
