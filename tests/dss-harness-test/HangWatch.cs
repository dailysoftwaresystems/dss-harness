using System.Runtime.InteropServices;
using System.Text;
using DssHarness.Core.Waiting;

namespace DssHarness.Tests;

/// <summary>
/// TEMPORARY, macOS on CI alone: says what a hung shard is stuck on. Every wait told through the operation's teller is
/// written to the error stream, and the kernel's table of this user's processes - read in this process through libproc,
/// starting nothing - is written once a process of this user has stood stopped, a zombie or in its exit for three looks a
/// minute apart, and again at seven, when the run is ended so the job's log is kept at once.
/// </summary>
internal static class HangWatch
{
    private const int ShortInformation = 13;
    private const int ShortSize = 64;
    private const uint Stopped = 4;
    private const uint Zombie = 5;
    private const uint InExit = 0x4;

    public static void Start()
    {
        if (!OperatingSystem.IsMacOS() || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
        {
            return;
        }

        _ = Waits.TellingTo(told => Console.Error.WriteLine($"[hang-watch told {DateTime.UtcNow:HH:mm:ss}] {told.Line()}"));

        new Thread(Loop) { IsBackground = true, Name = "hang-watch" }.Start();
    }

    private static void Loop()
    {
        var uid = getuid();
        var standing = new Dictionary<(int Id, uint State, uint Flags), int>();

        while (true)
        {
            Thread.Sleep(TimeSpan.FromSeconds(60));

            try
            {
                var table = Table(uid);
                var now = new HashSet<(int, uint, uint)>();

                foreach (var process in table.Where(process => process.State == Stopped || process.State == Zombie || (process.Flags & InExit) != 0))
                {
                    var key = (process.Id, process.State, process.Flags);
                    now.Add(key);
                    standing[key] = standing.GetValueOrDefault(key) + 1;
                }

                foreach (var gone in standing.Keys.Where(key => !now.Contains(key)).ToList())
                {
                    standing.Remove(gone);
                }

                var longest = standing.Count == 0 ? 0 : standing.Values.Max();

                if (longest is 3 or 7)
                {
                    var said = new StringBuilder();

                    ThreadPool.GetMinThreads(out var minWorkers, out _);
                    said.AppendLine($"[hang-watch {DateTime.UtcNow:HH:mm:ss}] this pid {Environment.ProcessId}; pool threads {ThreadPool.ThreadCount} (min {minWorkers}), queued {ThreadPool.PendingWorkItemCount}; processes of uid {uid} (state 4 stopped, 5 zombie; flag 0x4 in exit):");

                    foreach (var process in table)
                    {
                        said.AppendLine($"[hang-watch]   pid {process.Id} ppid {process.Parent} pgid {process.Group} state {process.State} flags 0x{process.Flags:x} {process.Name}");
                    }

                    Console.Error.Write(said.ToString());

                    // The told waits of five minutes have been written by the second table: the run is ended, so its log is kept now.
                    if (longest == 7)
                    {
                        Console.Error.Flush();
                        Environment.FailFast("hang-watch: a process stood stopped, a zombie or in its exit for seven minutes");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[hang-watch] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static List<(int Id, int Parent, int Group, uint State, uint Flags, string Name)> Table(uint uid)
    {
        var room = new int[16384];
        var listed = proc_listpids(1, 0, room, room.Length * sizeof(int));
        var found = new List<(int, int, int, uint, uint, string)>();
        var bytes = new byte[ShortSize];

        foreach (var id in room.Take(Math.Max(listed, 0) / sizeof(int)).Where(id => id > 0))
        {
            if (proc_pidinfo(id, ShortInformation, 1, bytes, bytes.Length) != bytes.Length || BitConverter.ToUInt32(bytes, 36) != uid)
            {
                continue;
            }

            var name = Encoding.UTF8.GetString(bytes, 16, 16).TrimEnd('\0');
            found.Add((id, BitConverter.ToInt32(bytes, 4), BitConverter.ToInt32(bytes, 8), BitConverter.ToUInt32(bytes, 12), BitConverter.ToUInt32(bytes, 32), name));
        }

        return found;
    }

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern int proc_listpids(uint type, uint typeInfo, int[] buffer, int bufferSize);

    [DllImport("libc")]
    private static extern int proc_pidinfo(int processId, int flavor, ulong argument, byte[] buffer, int bufferSize);
}
