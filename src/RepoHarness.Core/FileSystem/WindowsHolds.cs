using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RepoHarness.Core.FileSystem;

/// <summary>
/// Whether another program holds a file or directory so that Windows would not delete it, asked by opening it as
/// a deletion opens it and letting it go at once. Nothing is changed.
/// </summary>
/// <remarks>
/// Measured on Windows 11: a deletion is refused where a handle to the entry does not share deleting it - a
/// process's current directory, a file opened without <see cref="FileShare.Delete"/> - and, for a file, where a
/// program is running from it: its image shares deleting, and refuses writing. A watcher on a directory, and a
/// file opened sharing deletion and writing, hold nothing, and the deletion goes through them. Moving the directory
/// aside answers neither way: a watcher on a directory under it refuses the move, and a program running from it
/// does not.
/// <para>
/// What opening cannot tell apart is said as it is taken. A file another program has open sharing its deletion and
/// not its writing refuses the writing as a program running from it does, and is taken as held, though a deletion
/// would go through it. A read-only file refuses writing to anyone, so one a program is running from is not found.
/// And where Windows keeps a deleted file until every handle to it is closed - older versions do, and filesystems
/// other than NTFS - a handle sharing deletion still keeps the directory it is in from going, and is not found:
/// git then stops part way, as it did before anything was looked for.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsHolds
{
    /// <summary>DELETE, which a deletion opens an entry with.</summary>
    private const uint DeleteAccess = 0x00010000;

    /// <summary>FILE_WRITE_DATA, which the file a program is running from refuses.</summary>
    private const uint WriteDataAccess = 0x00000002;

    /// <summary>FILE_SHARE_READ, FILE_SHARE_WRITE and FILE_SHARE_DELETE: this handle refuses nothing another does.</summary>
    private const uint ShareAll = 0x00000007;

    /// <summary>OPEN_EXISTING.</summary>
    private const uint OpenExisting = 3;

    /// <summary>FILE_FLAG_BACKUP_SEMANTICS, without which a directory cannot be opened.</summary>
    private const uint BackupSemantics = 0x02000000;

    /// <summary>FILE_FLAG_OPEN_REPARSE_POINT: a link is opened as itself, which is what a deletion removes.</summary>
    private const uint OpenReparsePoint = 0x00200000;

    /// <summary>ERROR_ACCESS_DENIED.</summary>
    private const int AccessDenied = 5;

    /// <summary>ERROR_SHARING_VIOLATION: another handle refuses what this one asks for.</summary>
    private const int SharingViolation = 32;

    /// <summary>What Windows says of a sharing violation, in the language it speaks here.</summary>
    private static readonly string SharingViolationMessage = new Win32Exception(SharingViolation).Message;

    /// <summary>The entry at <paramref name="path"/> where another program holds it; otherwise <see langword="null"/>.</summary>
    /// <param name="path">A fully qualified path.</param>
    /// <param name="isDirectory">Whether it is a directory.</param>
    public static HeldEntry? Held(string path, bool isDirectory)
    {
        var error = isDirectory
            ? Open(path, DeleteAccess, BackupSemantics)
            : Open(path, DeleteAccess | WriteDataAccess, flags: 0);

        // A read-only file, or one this user may not write, refuses the writing alone, which is no hold: it is asked
        // again for its deletion only, as git deletes it once it has cleared the attribute.
        if (error == AccessDenied && !isDirectory)
        {
            error = Open(path, DeleteAccess, flags: 0);
        }

        return error == SharingViolation ? new HeldEntry(path, SharingViolationMessage) : null;
    }

    /// <summary>Opens <paramref name="path"/> sharing everything, and closes it; the error that refused it, or zero.</summary>
    private static int Open(string path, uint access, uint flags)
    {
        using var handle = CreateFileW(Extended(path), access, ShareAll, IntPtr.Zero, OpenExisting, flags | OpenReparsePoint, IntPtr.Zero);

        return handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    /// <summary>
    /// <paramref name="path"/> as Windows reads it past 260 characters, as the runtime's own calls write it: a
    /// worktree's build directory holds paths that long. A drive path and a UNC path are given the prefix each
    /// takes; a path that already names a device, as either prefix does, is left as it is.
    /// </summary>
    /// <param name="path">A fully qualified path.</param>
    internal static string Extended(string path)
        => path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
            : @"\\?\" + path;

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
