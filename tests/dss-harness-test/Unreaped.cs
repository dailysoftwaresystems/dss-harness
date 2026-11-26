using System.Runtime.InteropServices;

namespace DssHarness.Tests;

/// <summary>
/// Processes started by the C library's own call, on Linux and macOS, which no runtime reaps - a zombie of one stays until
/// this process asks for it (<see cref="Reap"/>) - and, where a group is named, started in that group: a member of a group
/// that is no descendant of its leader, as a process the leader never started is.
/// </summary>
internal static class Unreaped
{
    /// <summary>The flag that has the spawn place its child in the group its attributes name: POSIX_SPAWN_SETPGROUP, on Linux and macOS alike.</summary>
    private const short SetGroup = 0x02;

    /// <summary>The room the spawn's attributes are made in: more than the C library of Linux or macOS keeps in them.</summary>
    private const int AttributesRoom = 1024;

    /// <summary>Starts <paramref name="program"/> with <paramref name="arguments"/> - its own name first - in <paramref name="group"/> where one is named.</summary>
    /// <param name="program">The program's path.</param>
    /// <param name="group">The group it starts in, or <see langword="null"/> for this process's own.</param>
    /// <param name="arguments">Its arguments, its own name first.</param>
    /// <returns>Its id.</returns>
    public static int Spawn(string program, int? group, params string[] arguments)
    {
        var spelled = arguments.Select(Marshal.StringToHGlobalAnsi).Append(IntPtr.Zero).ToArray();
        var attributes = IntPtr.Zero;

        try
        {
            if (group is { } joined)
            {
                attributes = Marshal.AllocHGlobal(AttributesRoom);

                Assert.Equal(0, AttributesInit(attributes));
                Assert.Equal(0, AttributesSetFlags(attributes, SetGroup));
                Assert.Equal(0, AttributesSetGroup(attributes, joined));
            }

            var refused = PosixSpawn(out var id, program, IntPtr.Zero, attributes, spelled, [IntPtr.Zero]);

            Assert.Equal(0, refused);
            return id;
        }
        finally
        {
            if (attributes != IntPtr.Zero)
            {
                _ = AttributesDestroy(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            foreach (var argument in spelled.Where(argument => argument != IntPtr.Zero))
            {
                Marshal.FreeHGlobal(argument);
            }
        }
    }

    /// <summary>Kills the process <paramref name="id"/> names, where it still runs, and reaps it.</summary>
    /// <param name="id">Its id.</param>
    public static void Reap(int id)
    {
        _ = Kill(id, 9);
        _ = WaitPid(id, out _, 0);
    }

    [DllImport("libc", EntryPoint = "posix_spawn", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int PosixSpawn(out int id, string path, IntPtr fileActions, IntPtr attributes, IntPtr[] arguments, IntPtr[] environment);

    [DllImport("libc", EntryPoint = "posix_spawnattr_init")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int AttributesInit(IntPtr attributes);

    [DllImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int AttributesSetFlags(IntPtr attributes, short flags);

    [DllImport("libc", EntryPoint = "posix_spawnattr_setpgroup")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int AttributesSetGroup(IntPtr attributes, int group);

    [DllImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int AttributesDestroy(IntPtr attributes);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Kill(int id, int signal);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int WaitPid(int id, out int status, int options);
}
