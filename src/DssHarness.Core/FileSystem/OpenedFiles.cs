using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DssHarness.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// Where an open file stands, as each system names the file a handle holds - never by a path looked up again, which a link
/// put in place since would answer for: Windows by <c>GetFinalPathNameByHandleW</c>, its device prefix taken off; Linux by
/// the link <c>/proc/self/fd/&lt;descriptor&gt;</c>, its removal mark taken off (<see cref="ProcessKernel.Unmarked"/>);
/// macOS by libproc's <c>proc_pidfdinfo</c>, the path the kernel keeps for the descriptor's vnode. The one reader of it,
/// behind <see cref="IFileSystem.FinalPathOf"/>.
/// </summary>
/// <remarks>
/// On macOS <c>fcntl(F_GETPATH)</c> answers the same path, but <c>fcntl</c> takes its third argument as a C variadic one,
/// which a P/Invoke cannot pass on Apple's arm64 calling convention; <c>proc_pidfdinfo</c> has a fixed signature, and
/// libproc is read for a process's program already (<see cref="ProcessKernel"/>).
/// </remarks>
internal static class OpenedFiles
{
    /// <summary>FILE_NAME_NORMALIZED and VOLUME_NAME_DOS: the path as its drive letter or share names it, every link followed.</summary>
    private const uint NormalizedDosName = 0;

    /// <summary>The prefix Windows puts before a drive path it answers whole.</summary>
    private const string DevicePrefix = @"\\?\";

    /// <summary>The prefix Windows puts before a share's path it answers whole, which stands for the share's own <c>\\</c>.</summary>
    private const string SharePrefix = @"\\?\UNC\";

    /// <summary>PROC_PIDFDVNODEPATHINFO: what <c>proc_pidfdinfo</c> answers of a descriptor on a vnode, its path among it.</summary>
    private const int VnodePathInfo = 2;

    /// <summary>
    /// sizeof(struct vnode_fdinfowithpath): the descriptor's proc_fileinfo (24 bytes), the vnode's vnode_info (152 bytes)
    /// and its path, MAXPATHLEN (1024 bytes) long.
    /// </summary>
    private const int VnodePathInfoSize = 1200;

    /// <summary>Where the vnode's path begins in struct vnode_fdinfowithpath: past its proc_fileinfo and its vnode_info.</summary>
    private const int VnodePathOffset = 24 + 152;

    /// <summary>The whole path of the file <paramref name="handle"/> holds, as this system names it.</summary>
    /// <param name="handle">An open file's handle.</param>
    /// <exception cref="IOException">The system would not name it, or named no file.</exception>
    public static string FinalPath(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        var named = OperatingSystem.IsWindows() ? OnWindows(handle)
            : OperatingSystem.IsMacOS() ? OnMacOs(handle)
            : OnLinux(handle);

        return Path.IsPathFullyQualified(named)
            ? named
            : throw new IOException($"the open file is named '{named}', which is no whole path");
    }

    /// <summary>The path Windows names the file by, every link followed, without its device prefix.</summary>
    [SupportedOSPlatform("windows")]
    private static string OnWindows(SafeFileHandle handle)
    {
        var buffer = new char[512];

        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, NormalizedDosName);

            if (length == 0)
            {
                throw new IOException("Windows would not name the open file", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            // Too short, it answers the room the path needs, its NUL counted; else the path's length, without it.
            if (length >= buffer.Length)
            {
                buffer = new char[length];
                continue;
            }

            var named = new string(buffer, 0, (int)length);

            return named.StartsWith(SharePrefix, StringComparison.OrdinalIgnoreCase) ? @"\\" + named[SharePrefix.Length..]
                : named.StartsWith(DevicePrefix, StringComparison.Ordinal) ? named[DevicePrefix.Length..]
                : named;
        }
    }

    /// <summary>The path the link <c>/proc/self/fd/&lt;descriptor&gt;</c> names, its removal mark taken off.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string OnLinux(SafeFileHandle handle)
    {
        var descriptor = handle.DangerousGetHandle().ToInt32().ToString(CultureInfo.InvariantCulture);

        return new FileInfo(Path.Combine("/proc/self/fd", descriptor)).LinkTarget is { Length: > 0 } target
            ? ProcessKernel.Unmarked(target)
            : throw new IOException($"the link of descriptor {descriptor} under /proc/self/fd names nothing");
    }

    /// <summary>The path libproc keeps for the descriptor's vnode.</summary>
    [SupportedOSPlatform("macos")]
    private static string OnMacOs(SafeFileHandle handle)
    {
        var buffer = new byte[VnodePathInfoSize];
        var descriptor = handle.DangerousGetHandle().ToInt32();
        var written = ProcPidFdInfo(Environment.ProcessId, descriptor, VnodePathInfo, buffer, buffer.Length);

        if (written < VnodePathInfoSize)
        {
            throw new IOException($"libproc would not name the file descriptor {descriptor.ToString(CultureInfo.InvariantCulture)} is open on", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var path = buffer.AsSpan(VnodePathOffset);
        var end = path.IndexOf((byte)0);

        return Encoding.UTF8.GetString(end < 0 ? path : path[..end]);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    [DllImport("libc", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ProcPidFdInfo(int pid, int descriptor, int flavor, byte[] buffer, int size);
}
