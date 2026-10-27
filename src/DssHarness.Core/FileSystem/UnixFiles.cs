using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// What .NET does not say on Linux and macOS: what a path is - a pipe, a socket or a device reads as an ordinary file by
/// its attributes, and opening a pipe waits for a writer, past any stop - and which error refused an open. Read by the
/// runtime's own System.Native, shipped with every .NET on those systems: its stat (<c>SystemNative_Stat</c>), whose mode
/// keeps the type's bits as POSIX writes them, and its table of error numbers.
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

    /// <summary>
    /// Whether <paramref name="number"/> - this system's own error number, as .NET raises it for the <see cref="IOException"/>
    /// of a refused open (its <see cref="Exception.HResult"/>) - is EWOULDBLOCK: the lock an open sharing nothing asks for
    /// is held by another open, in this process or another. Read by the runtime's own table (System.Native's
    /// <c>SystemNative_ConvertErrorPlatformToPal</c>), as the number differs between systems - 11 on Linux, 35 on macOS.
    /// </summary>
    /// <param name="number">The error number.</param>
    public static bool WouldBlock(int number) => ConvertErrorPlatformToPal(number) == WouldBlockError;

    /// <summary>
    /// What <paramref name="path"/> leads to where it is no regular file and no directory - a pipe, a socket or a device,
    /// said - or <see langword="null"/>: where it is one of those, or nothing could be read of it, which the open that
    /// follows says better.
    /// </summary>
    /// <param name="path">The path, followed through its links as an open follows them.</param>
    public static string? Special(string path)
    {
        if (Stat(path, out var status) != 0)
        {
            return null;
        }

        return (status.Mode & TypeBits) switch
        {
            RegularFile or Directory => null,
            0x1000 => "a pipe",
            0x2000 => "a character device",
            0x6000 => "a block device",
            0xC000 => "a socket",
            _ => "no regular file",
        };
    }

    /// <summary>The head of System.Native's FileStatus - its flags, then its mode - in room enough for the whole of it.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 512)]
    private struct Status
    {
        public int Flags;
        public int Mode;
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_Stat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out Status status);

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_ConvertErrorPlatformToPal")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ConvertErrorPlatformToPal(int number);
}
