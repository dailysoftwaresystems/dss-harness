using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// Opening a file or directory as Windows itself sees it, for the questions the runtime has no call for: whether
/// another program holds an entry (<see cref="WindowsHolds"/>), and what kind of link one is
/// (<see cref="WindowsJunctions"/>) - and replacing a file as POSIX does (<see cref="TryReplace(string, string)"/>). One definition of
/// how an entry is opened, so none of them can open it differently.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32Files
{
    /// <summary>FILE_SHARE_READ, FILE_SHARE_WRITE and FILE_SHARE_DELETE: a handle that refuses nothing another does.</summary>
    internal const uint ShareAll = 0x00000007;

    /// <summary>FILE_FLAG_BACKUP_SEMANTICS, without which a directory cannot be opened.</summary>
    internal const uint BackupSemantics = 0x02000000;

    /// <summary>FILE_FLAG_OPEN_REPARSE_POINT: a link is opened as itself, never as what it leads to.</summary>
    internal const uint OpenReparsePoint = 0x00200000;

    /// <summary>OPEN_EXISTING.</summary>
    private const uint OpenExisting = 3;

    /// <summary>DELETE and SYNCHRONIZE: what renaming a file through its handle asks for.</summary>
    private const uint RenameAccess = 0x00010000 | 0x00100000;

    /// <summary>FileRenameInfoEx, the class of SetFileInformationByHandle that takes a rename's flags.</summary>
    private const int FileRenameInfoEx = 22;

    /// <summary>FILE_RENAME_FLAG_REPLACE_IF_EXISTS and FILE_RENAME_FLAG_POSIX_SEMANTICS.</summary>
    private const uint ReplacePosix = 0x1 | 0x2;

    /// <summary>
    /// ERROR_INVALID_FUNCTION, ERROR_NOT_SUPPORTED and ERROR_INVALID_PARAMETER: what a volume or a Windows that cannot rename
    /// as POSIX does answers; and ERROR_NOT_SAME_DEVICE, a file to go to another volume, which no rename takes it to.
    /// </summary>
    private static readonly int[] Unsupported = [1, 17, 50, 87];

    /// <summary>
    /// Opens <paramref name="path"/> itself, sharing everything, a link as the link. The handle is invalid where it
    /// could not be opened, with the reason in <see cref="Marshal.GetLastPInvokeError"/>.
    /// </summary>
    /// <param name="path">A fully qualified path.</param>
    /// <param name="access">The access asked for.</param>
    /// <param name="flags">Flags beside <see cref="OpenReparsePoint"/>, which is always given.</param>
    internal static SafeFileHandle Open(string path, uint access, uint flags)
        => CreateFileW(Extended(path), access, ShareAll, IntPtr.Zero, OpenExisting, flags | OpenReparsePoint, IntPtr.Zero);

    /// <summary>
    /// <paramref name="path"/> as Windows reads it past 260 characters, as the runtime's own calls write it: a
    /// worktree's build directory holds paths that long. A drive path and a UNC path are given the prefix each
    /// takes; a path that already names a device, as either prefix does, is left as it is.
    /// </summary>
    /// <param name="path">A fully qualified path.</param>
    /// <remarks>
    /// The prefix turns off everything Windows would otherwise do to a path, so what it would have done is done here
    /// first: a <c>.</c> or <c>..</c> part is resolved and a forward slash made a backslash. Left in, a path spelt
    /// with <c>..</c> - a sibling of a tree, named from inside it - is refused as a name that is not one. Nothing
    /// else is changed, a name ending in a dot or a space among it, which only the prefix reaches.
    /// </remarks>
    internal static string Extended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return path;
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        var parts = new List<string>();

        foreach (var part in path[root.Length..].Split('\\', '/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(part);
        }

        var whole = root.Replace('/', '\\').TrimEnd('\\') + '\\' + string.Join('\\', parts);

        return whole.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + whole[2..] : @"\\?\" + whole;
    }

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/> as POSIX does: the name moves to the new file
    /// while whoever holds the old one open - sharing its deletion, as every read of a file here does - goes on reading the
    /// old one. Windows' own replace is refused while the old file is open at all, however it is shared, so a record
    /// streamed by one reader could not be written by another while it was read.
    /// </summary>
    /// <param name="source">The file to put in place, fully qualified.</param>
    /// <param name="destination">Where to put it, fully qualified.</param>
    /// <returns>
    /// False where the volume or this Windows cannot rename so - a FAT volume, a share - or the file is to go to another
    /// volume, for the caller to replace it Windows' own way, which copies it there.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">Windows refused the rename, as a file held without its deletion shared is.</exception>
    /// <exception cref="IOException">It failed for any other reason.</exception>
    internal static bool TryReplace(string source, string destination) => TryReplace(source, destination, Rename);

    /// <summary>
    /// <see cref="TryReplace(string, string)"/>, renaming through <paramref name="rename"/> - which sets the rename's
    /// information on the source's handle and answers the error Windows gave, or 0 where it was done - a test's where it
    /// stands in for Windows' own.
    /// </summary>
    internal static bool TryReplace(string source, string destination, Func<SafeFileHandle, byte[], int> rename)
    {
        ArgumentNullException.ThrowIfNull(rename);

        using var handle = Open(source, RenameAccess, 0);

        if (handle.IsInvalid)
        {
            throw Failed(Marshal.GetLastPInvokeError(), source);
        }

        // FILE_RENAME_INFO: the flags, padded to a pointer; the root directory, none; the name's length in bytes; the name,
        // ended by a null.
        var name = Encoding.Unicode.GetBytes(Extended(destination));
        var named = IntPtr.Size * 2;
        var info = new byte[named + sizeof(uint) + name.Length + sizeof(char)];
        BinaryPrimitives.WriteUInt32LittleEndian(info, ReplacePosix);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(named), name.Length);
        name.CopyTo(info, named + sizeof(uint));

        var error = rename(handle, info);

        if (error == 0)
        {
            return true;
        }

        return Unsupported.Contains(error) ? false : throw Failed(error, destination);
    }

    /// <summary>Windows' own rename through <paramref name="handle"/>: the error it gave, or 0 where it was done.</summary>
    private static int Rename(SafeFileHandle handle, byte[] info)
        => SetFileInformationByHandle(handle, FileRenameInfoEx, info, (uint)info.Length) ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>What a failed Windows call on <paramref name="path"/> raises, as the runtime's own file calls raise it.</summary>
    private static Exception Failed(int error, string path)
    {
        var message = $"{new Win32Exception(error).Message} : '{path}'";

        // ERROR_ACCESS_DENIED, as File.Move raises it.
        return error == 5 ? new UnauthorizedAccessException(message) : new IOException(message, error);
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
