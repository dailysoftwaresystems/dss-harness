using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RepoHarness.Core.FileSystem;

/// <summary>What a directory entry is, as far as removing it as a link goes.</summary>
internal enum JunctionKind
{
    /// <summary>Not a junction: a directory, a file, a symbolic link, or a link of another kind.</summary>
    None,

    /// <summary>A directory junction, which is removed as the link it is.</summary>
    Junction,

    /// <summary>A volume mounted on the directory: the same kind of link as a junction, naming a volume.</summary>
    MountedVolume,
}

/// <summary>
/// Directory junctions, told from every other kind of entry by the tag Windows keeps on the entry itself, and removed
/// as links: removing one never touches what it leads to.
/// </summary>
/// <remarks>
/// Measured with git 2.55.0.windows.5: removing a worktree leaves every junction in it, and every directory above
/// one, while reporting the worktree removed, its .git file and git's record of it already deleted. A symbolic link
/// to a directory, and one to a file, it removes. And the runtime's recursive delete removes a junction but reports
/// it as refused - it unmounts every entry with this tag before removing it, and a junction is no mounted volume
/// - leaving the directories above it. So a junction is removed here first, by the call that removes an empty
/// directory, which removes a junction as the link it is.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsJunctions
{
    /// <summary>IO_REPARSE_TAG_MOUNT_POINT, the tag of a junction and of a volume mounted on a directory.</summary>
    internal const uint MountPointTag = 0xA0000003;

    /// <summary>FILE_READ_ATTRIBUTES, all the access asking for an entry's tag needs.</summary>
    private const uint ReadAttributes = 0x00000080;

    /// <summary>FILE_ATTRIBUTE_REPARSE_POINT.</summary>
    private const uint ReparsePointAttribute = 0x00000400;

    /// <summary>FileAttributeTagInfo, the information class holding an entry's attributes and tag.</summary>
    private const int AttributeTagInformation = 9;

    /// <summary>What the entry at <paramref name="path"/> is, asked of the entry itself and never of what it leads to.</summary>
    /// <param name="path">A fully qualified path.</param>
    /// <exception cref="IOException">The entry could not be opened, or its tag read.</exception>
    public static JunctionKind KindOf(string path)
    {
        using (var handle = Win32Files.Open(path, ReadAttributes, Win32Files.BackupSemantics))
        {
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, AttributeTagInformation, out var tag, (uint)Marshal.SizeOf<AttributeTag>()))
            {
                throw Refused(path, Marshal.GetLastPInvokeError());
            }

            if ((tag.FileAttributes & ReparsePointAttribute) == 0 || tag.ReparseTag != MountPointTag)
            {
                return JunctionKind.None;
            }
        }

        return NamesAVolume(new DirectoryInfo(path).LinkTarget) ? JunctionKind.MountedVolume : JunctionKind.Junction;
    }

    /// <summary>Removes the junction at <paramref name="path"/>: the link, never what it leads to.</summary>
    /// <param name="path">A fully qualified path to a junction.</param>
    /// <exception cref="IOException">Windows refused, saying why.</exception>
    public static void Remove(string path)
    {
        if (!RemoveDirectoryW(Win32Files.Extended(path)))
        {
            throw Refused(path, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Whether a mount point's target names a volume rather than a directory: a volume mounted on a directory, which
    /// removing would unmount.
    /// </summary>
    /// <param name="target">What the mount point leads to, as the runtime reads it.</param>
    internal static bool NamesAVolume(string? target)
        => target is not null
            && (target.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith(@"\??\Volume{", StringComparison.OrdinalIgnoreCase)
                || target.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase));

    private static IOException Refused(string path, int error) => new($"'{path}': {new Win32Exception(error).Message}");

    /// <summary>FILE_ATTRIBUTE_TAG_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out AttributeTag information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectoryW(string path);
}
