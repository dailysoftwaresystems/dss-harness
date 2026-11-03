using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// What .NET does not say on Linux and macOS: what an open file is - a pipe, a socket or a device reads as an ordinary
/// file by its attributes, and opening a pipe waits for a writer, past any stop - and which error refused an open.
/// Read by the runtime's own System.Native, shipped with every .NET on those systems: its stat of a descriptor
/// (<c>SystemNative_FStat</c>), whose mode keeps the type's bits as POSIX writes them, and its table of error numbers.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class UnixFiles
{
    /// <summary>S_IFMT: the bits of a mode that say a file's type.</summary>
    private const int TypeBits = 0xF000;

    /// <summary>S_IFREG: a regular file.</summary>
    private const int RegularFile = 0x8000;

    /// <summary>S_IFDIR: a directory, which an open refuses rather than waits on.</summary>
    private const int Directory = 0x4000;

    /// <summary>EAGAIN in System.Native's own numbering, the one every system's number is turned into: EWOULDBLOCK, which is EAGAIN.</summary>
    private const int WouldBlockError = 0x10006;

    /// <summary>O_RDONLY, O_NONBLOCK, O_NOCTTY and O_CLOEXEC, as each system spells them: an open that never waits on a pipe.</summary>
    private static readonly int ReadNotWaiting = OperatingSystem.IsMacOS() ? 0x4 | 0x20000 | 0x1000000 : 0x800 | 0x100 | 0x80000;

    /// <summary>
    /// Whether <paramref name="number"/> - this system's own error number, as .NET raises it for the <see cref="IOException"/>
    /// of a refused open (its <see cref="Exception.HResult"/>) - is EWOULDBLOCK: the lock an open sharing nothing asks for
    /// is held by another open, in this process or another. Read by the runtime's own table (System.Native's
    /// <c>SystemNative_ConvertErrorPlatformToPal</c>), as the number differs between systems - 11 on Linux, 35 on macOS.
    /// </summary>
    /// <param name="number">The error number.</param>
    public static bool WouldBlock(int number) => ConvertErrorPlatformToPal(number) == WouldBlockError;

    /// <summary>A file's type, by the type's bits of its mode, as a refusal says it.</summary>
    /// <param name="type">The type's bits.</param>
    public static string TypeOf(int type) => type switch
    {
        RegularFile => "a regular file",
        Directory => "a directory",
        0x1000 => "a pipe",
        0x2000 => "a character device",
        0x6000 => "a block device",
        0xA000 => "a link",
        0xC000 => "a socket",
        _ => "no regular file",
    };

    /// <summary>The mode of what <paramref name="handle"/> is open on, its type's bits among it; null where it could not be read, the error in <see cref="Marshal.GetLastPInvokeError"/>.</summary>
    /// <param name="handle">An open descriptor.</param>
    public static int? StatusOf(SafeFileHandle handle) => FStat(handle, out var status) == 0 ? status.Mode : null;

    /// <summary>
    /// Opens <paramref name="path"/> to read, its links followed, without ever waiting on a pipe: opened not to wait, then
    /// asked by its descriptor what it is - so a pipe put at the path between a look and the open is met here, never
    /// waited on - and refused where it is no regular file, before a byte is read. A directory is refused as the runtime
    /// refuses one.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The open file, which then reads as any file does.</returns>
    /// <exception cref="FileNotFoundException">Nothing is there.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory on the way is not there.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read it, or it is a directory.</exception>
    /// <exception cref="IOException">It is a pipe, a socket or a device, or could not be opened.</exception>
    public static SafeFileHandle OpenRegular(string path)
    {
        var descriptor = Open(path, ReadNotWaiting);

        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();

            // A socket refuses an open of its own (ENXIO): said as one, as a stat would say it.
            throw error == 6 ? new IOException($"'{path}' is a socket, which is no file to read") : UnixHeld.Failed(error, path);
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);

        try
        {
            RequireRegular(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Refuses what <paramref name="handle"/> is open on - opened not to wait - where it is no regular file, before a byte is
    /// read: a directory as the runtime refuses one, a pipe, a socket or a device said; and lets a regular file's reads wait
    /// as any file's do.
    /// </summary>
    /// <param name="handle">A descriptor opened with O_NONBLOCK.</param>
    /// <param name="path">The file, as a refusal names it.</param>
    /// <exception cref="UnauthorizedAccessException">It is a directory.</exception>
    /// <exception cref="IOException">It is no regular file, or what it is could not be read.</exception>
    public static void RequireRegular(SafeFileHandle handle, string path)
    {
        var mode = (StatusOf(handle) ?? throw UnixHeld.Failed(Marshal.GetLastPInvokeError(), path)) & TypeBits;

        if (mode == Directory)
        {
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        }

        if (mode != RegularFile)
        {
            throw new IOException($"'{path}' is {TypeOf(mode)}, which is no file to read");
        }

        if (SetNonBlocking(handle, 0) != 0)
        {
            throw UnixHeld.Failed(Marshal.GetLastPInvokeError(), path);
        }
    }

    /// <summary>The head of System.Native's FileStatus - its flags, then its mode - in room enough for the whole of it.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 512)]
    private struct Status
    {
        public int Flags;
        public int Mode;
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int FStat(SafeFileHandle descriptor, out Status status);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_FcntlSetIsNonBlocking", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SetNonBlocking(SafeFileHandle descriptor, int nonBlocking);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_ConvertErrorPlatformToPal")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ConvertErrorPlatformToPal(int number);
}
