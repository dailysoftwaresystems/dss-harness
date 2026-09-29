using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RepoHarness.Core.FileSystem;

/// <summary>
/// Opening a file or directory as Windows itself sees it, for the questions the runtime has no call for: whether
/// another program holds an entry (<see cref="WindowsHolds"/>), and what kind of link one is
/// (<see cref="WindowsJunctions"/>). One definition of how an entry is opened, so the two can never open it
/// differently.
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
