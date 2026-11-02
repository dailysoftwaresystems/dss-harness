using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// <see cref="HeldDirectory"/> on Windows: every entry opened or made relative to a directory's handle by
/// <c>NtCreateFile</c> (its object attributes' root directory), as itself (<c>FILE_OPEN_REPARSE_POINT</c>), then asked
/// what it is by its handle (<c>FileAttributeTagInfo</c>) - a name surrogate, a symbolic link or a junction, being a link -
/// and listed, removed and renamed through handles alone: <c>FileFullDirectoryInfo</c>, <c>FileDispositionInfoEx</c>,
/// and a rename naming the directory it lands in by its handle (<c>NtSetInformationFile</c>'s
/// <c>FileRenameInformationEx</c>, which Win32's own call does not pass on with a relative name).
/// </summary>
/// <remarks>
/// A name is found as Win32 finds one, whatever its letter case (<c>OBJ_CASE_INSENSITIVE</c>). A volume that cannot take a
/// removal or a rename as POSIX does - FAT, a share - is given Windows' own of the same class, through the same handles.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHeld : IHeldNative
{
    private const uint ListDirectory = 0x1;
    private const uint Traverse = 0x20;
    private const uint ReadAttributes = 0x80;
    private const uint WriteAttributes = 0x100;
    private const uint Delete = 0x10000;
    private const uint Synchronize = 0x100000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;

    private const uint ShareAll = 7;
    private const uint ShareReadDelete = 1 | 4;

    private const uint OpenExisting = 1;
    private const uint CreateNew = 2;

    private const uint DirectoryFile = 0x1;
    private const uint SequentialOnly = 0x4;
    private const uint SynchronousIo = 0x20;
    private const uint NonDirectoryFile = 0x40;
    private const uint ForBackup = 0x4000;
    private const uint OpenReparsePoint = 0x200000;

    private const uint CaseInsensitive = 0x40;

    private const uint DirectoryAttribute = 0x10;
    private const uint ReparsePointAttribute = 0x400;
    private const uint ReadOnlyAttribute = 0x1;
    private const uint NormalAttribute = 0x80;

    /// <summary>The bit of a reparse tag that says it stands for another named entity: a symbolic link or a junction.</summary>
    private const uint NameSurrogate = 0x20000000;

    private const int FileBasicInfo = 0;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private const int FileFullDirectoryInfo = 14;
    private const int FileFullDirectoryRestartInfo = 15;
    private const int FileDispositionInfoEx = 21;

    /// <summary>The native information classes of a rename: FileRenameInformation and FileRenameInformationEx.</summary>
    private const int FileRenameInformation = 10;
    private const int FileRenameInformationEx = 65;

    /// <summary>FILE_DISPOSITION_FLAG_DELETE, _POSIX_SEMANTICS and _IGNORE_READONLY_ATTRIBUTE.</summary>
    private const uint DeletePosix = 0x1 | 0x2 | 0x10;

    /// <summary>FILE_RENAME_FLAG_REPLACE_IF_EXISTS and FILE_RENAME_FLAG_POSIX_SEMANTICS.</summary>
    private const uint ReplaceFlag = 0x1;
    private const uint PosixFlag = 0x2;

    private const int FileNotFound = 2;
    private const int PathNotFound = 3;
    private const int AccessDenied = 5;
    private const int NoMoreFiles = 18;
    private const int FileExists = 80;
    private const int DirectoryNotEmpty = 145;
    private const int AlreadyExists = 183;
    private const int NotADirectoryError = 267;

    /// <summary>What a volume or a Windows that cannot take a call's class answers: ERROR_INVALID_FUNCTION, ERROR_NOT_SUPPORTED, ERROR_INVALID_PARAMETER.</summary>
    private static readonly int[] Unsupported = [1, 50, 87];

    /// <inheritdoc/>
    public SafeFileHandle OpenAnchor(string path)
    {
        var handle = CreateFileW(Win32Files.Extended(Path.GetFullPath(path)), ListDirectory | Traverse | ReadAttributes | Synchronize, ShareAll, IntPtr.Zero, 3, Win32Files.BackupSemantics, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failed(error, path);
        }

        return handle;
    }

    /// <inheritdoc/>
    public HeldOpen OpenDirectory(SafeFileHandle parent, string name, string path)
    {
        var status = Create(parent, name, ListDirectory | Traverse | ReadAttributes | Synchronize, ShareAll, OpenExisting, OpenReparsePoint | SynchronousIo | ForBackup, out var handle);

        if (status != 0)
        {
            handle.Dispose();
            var error = RtlNtStatusToDosError(status);

            return error is FileNotFound or PathNotFound ? HeldOpen.Missing : throw Failed(error, path);
        }

        var (attributes, tag) = Tag(handle, path);

        if (IsLink(attributes, tag) || (attributes & DirectoryAttribute) == 0)
        {
            handle.Dispose();
            return HeldOpen.Not(IsLink(attributes, tag) ? PathKind.Link : PathKind.File);
        }

        return HeldOpen.Of(handle);
    }

    /// <inheritdoc/>
    public SafeFileHandle MakeDirectory(SafeFileHandle parent, string name, string path)
    {
        var status = Create(parent, name, ListDirectory | Traverse | ReadAttributes | Synchronize, ShareAll, CreateNew, DirectoryFile | OpenReparsePoint | SynchronousIo | ForBackup, out var handle);

        if (status == 0)
        {
            return handle;
        }

        handle.Dispose();
        var error = RtlNtStatusToDosError(status);

        if (error is not (FileExists or AlreadyExists))
        {
            throw Failed(error, path);
        }

        // Made by someone else in the meantime: opened as any directory on the way is, a link put there refused.
        var opened = OpenDirectory(parent, name, path);

        return opened.Handle ?? throw HeldDirectory.NotADirectory(opened.Kind, path, path);
    }

    /// <inheritdoc/>
    public PathKind KindOf(SafeFileHandle parent, string name, string path)
    {
        var status = Create(parent, name, ReadAttributes | Synchronize, ShareAll, OpenExisting, OpenReparsePoint | SynchronousIo | ForBackup, out var handle);

        using (handle)
        {
            if (status != 0)
            {
                var error = RtlNtStatusToDosError(status);

                return error is FileNotFound or PathNotFound ? PathKind.None : throw Failed(error, path);
            }

            var (attributes, tag) = Tag(handle, path);

            return IsLink(attributes, tag) ? PathKind.Link
                : (attributes & DirectoryAttribute) != 0 ? PathKind.Directory
                : PathKind.File;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<HeldName> Entries(SafeFileHandle directory, string path)
    {
        var entries = new List<HeldName>();
        var buffer = new byte[64 * 1024];
        var restart = true;

        while (true)
        {
            if (!GetFileInformationByHandleEx(directory, restart ? FileFullDirectoryRestartInfo : FileFullDirectoryInfo, buffer, (uint)buffer.Length))
            {
                var error = Marshal.GetLastPInvokeError();

                return error == NoMoreFiles ? entries : throw Failed(error, path);
            }

            restart = false;

            // FILE_FULL_DIR_INFO: the next entry's offset, its attributes at 56, its name's length in bytes at 60, the
            // reparse tag of a reparse point where its extended attributes' size is kept at 64, its name from 68.
            for (var at = 0; ; )
            {
                var next = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(at));
                var attributes = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 56));
                var length = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(at + 60));
                var tag = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 64));
                var name = Encoding.Unicode.GetString(buffer, at + 68, length);

                if (name is not ("." or ".."))
                {
                    entries.Add(new HeldName(
                        name,
                        IsLink(attributes, tag) ? PathKind.Link : (attributes & DirectoryAttribute) != 0 ? PathKind.Directory : PathKind.File));
                }

                if (next == 0)
                {
                    break;
                }

                at += next;
            }
        }
    }

    /// <inheritdoc/>
    public SafeFileHandle OpenFile(SafeFileHandle parent, string name, string path)
    {
        var status = Create(parent, name, GenericRead | ReadAttributes | Synchronize, ShareAll, OpenExisting, NonDirectoryFile | OpenReparsePoint | SynchronousIo | SequentialOnly, out var handle);

        if (status != 0)
        {
            handle.Dispose();
            throw Failed(RtlNtStatusToDosError(status), path);
        }

        var (attributes, tag) = Tag(handle, path);

        if (IsLink(attributes, tag))
        {
            handle.Dispose();
            throw new LinkOnTheWayException(path, path);
        }

        return handle;
    }

    /// <inheritdoc/>
    public SafeFileHandle CreateFile(SafeFileHandle parent, string name, string path)
    {
        var status = Create(parent, name, GenericWrite | Delete | ReadAttributes | Synchronize, ShareReadDelete, CreateNew, NonDirectoryFile | SynchronousIo, out var handle);

        if (status != 0)
        {
            handle.Dispose();
            throw Failed(RtlNtStatusToDosError(status), path);
        }

        return handle;
    }

    /// <inheritdoc/>
    public void KeepMode(SafeFileHandle parent, string name, SafeFileHandle written)
    {
        // Nothing to keep: a file written in place of another takes its directory's access, as the one it replaces did.
    }

    /// <inheritdoc/>
    public void Replace(SafeFileHandle parent, string temporary, SafeFileHandle written, string name, string path)
        => RenameHandle(written, parent, name, replace: true, path);

    /// <inheritdoc/>
    public void Rename(SafeFileHandle fromParent, string fromName, SafeFileHandle toParent, string toName, bool replace, string from, string to)
    {
        var status = Create(fromParent, fromName, Delete | ReadAttributes | Synchronize, ShareAll, OpenExisting, OpenReparsePoint | SynchronousIo | ForBackup, out var handle);

        using (handle)
        {
            if (status != 0)
            {
                throw Failed(RtlNtStatusToDosError(status), from);
            }

            RenameHandle(handle, toParent, toName, replace, to);
        }
    }

    /// <inheritdoc/>
    public void Remove(SafeFileHandle parent, string name, bool directory, string path)
    {
        var status = Create(parent, name, Delete | ReadAttributes | WriteAttributes | Synchronize, ShareAll, OpenExisting, OpenReparsePoint | SynchronousIo | ForBackup, out var handle);

        using (handle)
        {
            if (status != 0)
            {
                var error = RtlNtStatusToDosError(status);

                throw error is FileNotFound or PathNotFound ? new FileNotFoundException($"Could not find '{path}'.", path) : Failed(error, path);
            }

            var (attributes, tag) = Tag(handle, path);
            var isDirectory = (attributes & DirectoryAttribute) != 0 && !IsLink(attributes, tag);

            // A volume mounted on a directory is the same kind of entry as a junction, and removing it would unmount it:
            // never done here. Only one who administers the machine mounts a volume, so what it names is read by its path.
            if (tag == WindowsJunctions.MountPointTag && (attributes & ReparsePointAttribute) != 0 && WindowsJunctions.NamesAVolume(new DirectoryInfo(path).LinkTarget))
            {
                throw new JunctionRemovalException(path, "a volume is mounted there, and it is never unmounted here", []);
            }

            if (isDirectory != directory)
            {
                throw isDirectory ? new HeldIsADirectoryException(path) : new HeldNotADirectoryException(path);
            }

            var info = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(info, DeletePosix);

            if (SetFileInformationByHandle(handle, FileDispositionInfoEx, info, (uint)info.Length))
            {
                return;
            }

            var error2 = Marshal.GetLastPInvokeError();

            if (error2 == DirectoryNotEmpty)
            {
                throw new HeldNotEmptyException(path);
            }

            if (!Unsupported.Contains(error2))
            {
                throw Failed(error2, path);
            }

            // A volume that takes no POSIX removal: the read-only mark cleared through the handle, then Windows' own.
            if ((attributes & ReadOnlyAttribute) != 0)
            {
                var basic = new byte[40];
                BinaryPrimitives.WriteUInt32LittleEndian(basic.AsSpan(32), (attributes & ~ReadOnlyAttribute) == 0 ? NormalAttribute : attributes & ~ReadOnlyAttribute);

                if (!SetFileInformationByHandle(handle, FileBasicInfo, basic, (uint)basic.Length))
                {
                    throw Failed(Marshal.GetLastPInvokeError(), path);
                }
            }

            if (!SetFileInformationByHandle(handle, FileDispositionInfo, [1], 1))
            {
                var error3 = Marshal.GetLastPInvokeError();
                throw error3 == DirectoryNotEmpty ? new HeldNotEmptyException(path) : Failed(error3, path);
            }
        }
    }

    /// <summary>
    /// Renames what <paramref name="handle"/> holds to <paramref name="name"/> in the directory <paramref name="into"/> holds,
    /// replacing what is there only where <paramref name="replace"/> - a link there replaced as itself.
    /// </summary>
    private static void RenameHandle(SafeFileHandle handle, SafeFileHandle into, string name, bool replace, string path)
    {
        var added = false;

        try
        {
            into.DangerousAddRef(ref added);

            var error = Rename(handle, into.DangerousGetHandle(), name, FileRenameInformationEx, (replace ? ReplaceFlag : 0) | PosixFlag);

            if (error != 0 && Unsupported.Contains(error))
            {
                error = Rename(handle, into.DangerousGetHandle(), name, FileRenameInformation, replace ? 1u : 0u);
            }

            if (error is AlreadyExists or FileExists && !replace)
            {
                throw new IOException($"'{path}' is there already, and it is not replaced");
            }

            if (error != 0)
            {
                throw Failed(error, path);
            }
        }
        finally
        {
            if (added)
            {
                into.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// NtSetInformationFile's rename of <paramref name="handle"/>: FILE_RENAME_INFORMATION - the flags, or the replace flag,
    /// padded to a pointer; the root directory; the name's length in bytes; the name, ended by a null. Called natively, as
    /// Win32's SetFileInformationByHandle makes a relative name a full path of the current directory's and refuses it beside
    /// a root directory. The error Windows gave, or 0 where it was done.
    /// </summary>
    private static int Rename(SafeFileHandle handle, IntPtr root, string name, int informationClass, uint flags)
    {
        var bytes = Encoding.Unicode.GetBytes(name);
        var named = IntPtr.Size * 2;
        var info = new byte[named + sizeof(uint) + bytes.Length + sizeof(char)];
        BinaryPrimitives.WriteUInt32LittleEndian(info, flags);

        if (IntPtr.Size == 8)
        {
            BinaryPrimitives.WriteInt64LittleEndian(info.AsSpan(IntPtr.Size), root.ToInt64());
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(IntPtr.Size), root.ToInt32());
        }

        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(named), bytes.Length);
        bytes.CopyTo(info, named + sizeof(uint));

        var status = NtSetInformationFile(handle, out _, info, (uint)info.Length, informationClass);

        return status == 0 ? 0 : RtlNtStatusToDosError(status);
    }

    /// <summary>A handle's attributes and its reparse tag.</summary>
    private static (uint Attributes, uint Tag) Tag(SafeFileHandle handle, string path)
    {
        var info = new byte[8];

        return GetFileInformationByHandleEx(handle, FileAttributeTagInfo, info, (uint)info.Length)
            ? (BinaryPrimitives.ReadUInt32LittleEndian(info), BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(4)))
            : throw Failed(Marshal.GetLastPInvokeError(), path);
    }

    /// <summary>Whether what has <paramref name="attributes"/> and <paramref name="tag"/> is a link: a reparse point that stands for another named entity.</summary>
    private static bool IsLink(uint attributes, uint tag) => (attributes & ReparsePointAttribute) != 0 && (tag & NameSurrogate) != 0;

    /// <summary>What a failed Windows call on <paramref name="path"/> raises, as the runtime's own file calls raise it.</summary>
    private static Exception Failed(int error, string path)
    {
        var words = new Win32Exception(error).Message;

        return error switch
        {
            FileNotFound => new FileNotFoundException($"Could not find '{path}': {words}", path),
            PathNotFound or NotADirectoryError => new DirectoryNotFoundException($"Could not find a part of the path '{path}': {words}"),
            AccessDenied => new UnauthorizedAccessException($"Access to the path '{path}' is denied: {words}"),
            _ => new IOException($"'{path}': {words}", error),
        };
    }

    /// <summary>NtCreateFile of <paramref name="name"/> in the directory <paramref name="parent"/> holds; the NTSTATUS it answered.</summary>
    private static int Create(SafeFileHandle parent, string name, uint access, uint share, uint disposition, uint options, out SafeFileHandle handle)
    {
        var added = false;
        var text = Marshal.StringToHGlobalUni(name);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());

        try
        {
            parent.DangerousAddRef(ref added);

            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = text }, unicode, fDeleteOld: false);

            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicode,
                Attributes = CaseInsensitive,
            };

            return NtCreateFile(out handle, access, ref attributes, out _, IntPtr.Zero, NormalAttribute, share, disposition, options, IntPtr.Zero, 0);
        }
        finally
        {
            if (added)
            {
                parent.DangerousRelease();
            }

            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(text);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtCreateFile(
        out SafeFileHandle handle,
        uint access,
        ref ObjectAttributes attributes,
        out IoStatusBlock status,
        IntPtr allocationSize,
        uint fileAttributes,
        uint share,
        uint disposition,
        uint options,
        IntPtr extendedAttributes,
        uint extendedAttributesLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, byte[] information, uint length, int informationClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, byte[] information, uint size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
