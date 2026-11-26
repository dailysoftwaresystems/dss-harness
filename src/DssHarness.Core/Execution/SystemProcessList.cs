using System.Globalization;
using System.Runtime.InteropServices;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Execution;

/// <summary>One process as the list Windows keeps of every process holds it.</summary>
/// <param name="Id">Its id.</param>
/// <param name="ParentId">
/// The id of the process that created it, as it stood then: Windows never changes it, so once the creator has gone it may
/// name a later holder of that id - one created after the process, which no creator can be.
/// </param>
/// <param name="Created">When it was created, as a file time: what a handle to it says of its creation too.</param>
internal readonly record struct ListedProcess(int Id, int ParentId, long Created);

/// <summary>
/// The list Windows keeps of every process for whoever asks: when a process on this machine started, and every process
/// with the one that created it.
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
    internal const int LengthMismatch = unchecked((int)0xC0000004);

    /// <summary>Where an entry keeps its process's creation time, whatever the pointer size.</summary>
    internal const int CreateTimeOffset = 0x20;

    /// <summary>The room the list is first read into: a machine's list most often fits it at once.</summary>
    private const int FirstRoom = 1 << 20;

    /// <summary>The room given beyond what the list said it needs, for the processes that start before it is read again.</summary>
    internal const int GrowthRoom = 1 << 16;

    /// <summary>
    /// What the list is read into a room by: Windows' own <c>NtQuerySystemInformation</c>, or a test's in its place - its
    /// status, and the room it says the list needs.
    /// </summary>
    /// <param name="list">The room.</param>
    /// <param name="length">The room's size, in bytes.</param>
    /// <param name="needed">The size the list needs, where it says.</param>
    internal delegate int ListReading(IntPtr list, int length, out int needed);

    /// <summary>
    /// The start of the process carrying <paramref name="processId"/> as <see cref="System.Diagnostics.Process.StartTime"/>
    /// gives it - the same instant, converted the same way - as its ticks; <see langword="null"/> where no process
    /// carries the id, or the list could not be read.
    /// </summary>
    /// <param name="processId">The process id.</param>
    public static long? StartTicks(int processId)
        => StartTicks(processId, TheSystems);

    /// <summary>The start of the process carrying <paramref name="processId"/>, the list read through <paramref name="read"/>.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="read">Reads the list into a room.</param>
    internal static long? StartTicks(int processId, ListReading read)
        => Read(read, list => Entries(list).Where(entry => entry.Id == processId).Select(entry => (long?)DateTime.FromFileTime(entry.Created).Ticks).FirstOrDefault());

    /// <summary>
    /// Every process the list holds, each with the one that created it; <see langword="null"/> where the list could not be
    /// read.
    /// </summary>
    public static IReadOnlyList<ListedProcess>? Every() => Every(TheSystems);

    /// <summary>Every process the list holds, the list read through <paramref name="read"/>.</summary>
    /// <param name="read">Reads the list into a room.</param>
    internal static IReadOnlyList<ListedProcess>? Every(ListReading read)
        => Read<IReadOnlyList<ListedProcess>?>(read, list => [.. Entries(list)]);

    /// <summary>Windows' own reading of the list.</summary>
    private static int TheSystems(IntPtr list, int length, out int needed)
        => NtQuerySystemInformation(ProcessInformation, list, length, out needed);

    /// <summary>What <paramref name="walk"/> finds in the list read through <paramref name="read"/>; its default where the list could not be read.</summary>
    /// <remarks>
    /// The list grows while it is read on a machine starting processes, and Windows says so - <c>LENGTH_MISMATCH</c>, with
    /// the room it needs - a cause that passes by nature: the room is grown to what it says, and a little more, and the list read
    /// again for as long as it says it (<see cref="Retrying"/>), never a count of tries, which once left a machine starting
    /// processes fast unread. Any other status is the list's answer, and a failing one is the list unread.
    /// </remarks>
    /// <typeparam name="T">What the walk finds.</typeparam>
    /// <param name="read">Reads the list into a room.</param>
    /// <param name="walk">What is found in the list, read whole.</param>
    private static T? Read<T>(ListReading read, Func<IntPtr, T> walk)
    {
        ArgumentNullException.ThrowIfNull(read);

        var length = FirstRoom;

        // Nothing waits between two reads: the room the next read needs is what this one said, and growing it is the
        // whole of what passes. Told nowhere, since nothing here waits for anything a person would see.
        var ended = Retrying.RunAsync<T?>(
            (_, _) =>
            {
                var list = Marshal.AllocHGlobal(length);

                try
                {
                    var status = read(list, length, out var needed);

                    if (status != LengthMismatch)
                    {
                        return Task.FromResult<RetryTry<T?>>(new RetryTry<T?>.Done(status < 0 ? default : walk(list)));
                    }

                    // What the list said it needs, and room besides for what starts before the next read; a list that said
                    // no more than it was given is given half as much again.
                    length = checked(needed > length ? needed + GrowthRoom : length + (length / 2));

                    return Task.FromResult<RetryTry<T?>>(new RetryTry<T?>.Passing(
                        string.Create(CultureInfo.InvariantCulture, $"the list outgrew its room; read again into {length} bytes"),
                        TimeSpan.Zero));
                }
                finally
                {
                    Marshal.FreeHGlobal(list);
                }
            },
            new RetryWatch("the process list", _ => { }, TimeSpan.FromMilliseconds(1), null, TimeProvider.System),
            CancellationToken.None).GetAwaiter().GetResult();

        return ended.Value;
    }

    /// <summary>
    /// Where an entry keeps its process's id: <c>SYSTEM_PROCESS_INFORMATION</c>'s id follows the image name and the
    /// priority, both of which a pointer's size moves.
    /// </summary>
    internal static int IdOffset => IntPtr.Size == 8 ? 0x50 : 0x44;

    /// <summary>Where an entry keeps the id of the process that created its own: the field after its id.</summary>
    internal static int ParentIdOffset => IdOffset + IntPtr.Size;

    /// <summary>Every entry of the list, walked entry by entry, each read as the walk reaches it.</summary>
    private static IEnumerable<ListedProcess> Entries(IntPtr list)
    {
        var entry = list;

        while (true)
        {
            yield return new ListedProcess(
                (int)Marshal.ReadIntPtr(entry, IdOffset),
                (int)Marshal.ReadIntPtr(entry, ParentIdOffset),
                Marshal.ReadInt64(entry, CreateTimeOffset));

            var next = Marshal.ReadInt32(entry);

            if (next == 0)
            {
                yield break;
            }

            entry += next;
        }
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int needed);
}
