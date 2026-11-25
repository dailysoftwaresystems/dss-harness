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

                if (longest == 3)
                {
                    var said = new StringBuilder();
                    var self = Environment.ProcessId;

                    ThreadPool.GetMinThreads(out var minWorkers, out _);
                    said.AppendLine($"[hang-watch {DateTime.UtcNow:HH:mm:ss}] this pid {self}; pool threads {ThreadPool.ThreadCount} (min {minWorkers}), queued {ThreadPool.PendingWorkItemCount}; this process, its children and every process standing stopped (4), a zombie (5) or in its exit (flag 0x4):");

                    foreach (var process in table.Where(process => process.Id == self || process.Parent == self || process.State is Stopped or Zombie || (process.Flags & InExit) != 0))
                    {
                        said.AppendLine($"[hang-watch]   pid {process.Id} ppid {process.Parent} pgid {process.Group} state {process.State} flags 0x{process.Flags:x} {process.Name}");
                    }

                    Console.Error.Write(said.ToString());

                    var stopped = string.Join(' ', standing.Keys.Select(key => key.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    var output = Path.Combine(Path.GetTempPath(), $"hang-watch-{self}");

                    Directory.CreateDirectory(output);
                    Run($"export PATH=/usr/bin:/bin:/usr/sbin:/sbin; sample {self} 3 -mayDie -file {output}/sample.txt > {output}/sample-run.txt 2>&1; "
                        + $"for id in {stopped}; do echo \"== lsof -p $id\"; lsof -p $id; done > {output}/lsof.txt 2>&1; "
                        + $"echo \"== pipes of {self}\" >> {output}/lsof.txt; lsof -p {self} 2>&1 | grep -i -E 'pipe|PIPE' >> {output}/lsof.txt");

                    foreach (var file in new[] { "sample-run.txt", "lsof.txt", "sample.txt" })
                    {
                        var path = Path.Combine(output, file);
                        Console.Error.WriteLine($"[hang-watch] ===== {file} =====");
                        Console.Error.WriteLine(File.Exists(path) ? File.ReadAllText(path) : "(none)");
                    }

                    Console.Error.Flush();
                }

                // The told waits of five minutes have been written by now: the run is ended, so its log is kept at once.
                if (longest == 7)
                {
                    Console.Error.Flush();
                    Environment.FailFast("hang-watch: a process stood stopped, a zombie or in its exit for seven minutes");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[hang-watch] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Runs <paramref name="script"/> under /bin/sh by posix_spawn - never through the runtime's own start - and waits for it.</summary>
    private static void Run(string script)
    {
        var arguments = new[] { "/bin/sh", "-c", script }.Select(Marshal.StringToHGlobalAnsi).Append(IntPtr.Zero).ToArray();

        try
        {
            if (posix_spawn(out var id, "/bin/sh", IntPtr.Zero, IntPtr.Zero, arguments, [IntPtr.Zero]) == 0)
            {
                _ = waitpid(id, out _, 0);
            }
        }
        finally
        {
            foreach (var argument in arguments.Where(argument => argument != IntPtr.Zero))
            {
                Marshal.FreeHGlobal(argument);
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
    private static extern int posix_spawn(out int id, string path, IntPtr fileActions, IntPtr attributes, IntPtr[] arguments, IntPtr[] environment);

    [DllImport("libc")]
    private static extern int waitpid(int id, out int status, int options);

    [DllImport("libc")]
    private static extern int proc_listpids(uint type, uint typeInfo, int[] buffer, int bufferSize);

    [DllImport("libc")]
    private static extern int proc_pidinfo(int processId, int flavor, ulong argument, byte[] buffer, int bufferSize);
}
