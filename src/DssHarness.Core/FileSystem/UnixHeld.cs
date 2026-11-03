using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// <see cref="HeldDirectory"/> on Linux and macOS: every entry opened, made, removed or renamed relative to a directory's
/// descriptor by the C library's <c>*at</c> calls, never by a path looked up again - <c>openat</c> with
/// <c>O_NOFOLLOW</c>, <c>mkdirat</c>, <c>unlinkat</c>, <c>renameat</c> (<c>renameat2</c>'s <c>RENAME_NOREPLACE</c> on
/// Linux, <c>renameatx_np</c>'s <c>RENAME_EXCL</c> on macOS, where a rename is to replace nothing) - and what a descriptor
/// holds read by the runtime's own System.Native (<c>SystemNative_FStat</c>), whose status keeps the type's bits as POSIX
/// writes them.
/// </summary>
/// <remarks>
/// The flags of <c>open</c> differ between the systems and, on Linux, between architectures: ARM's and POWER's
/// <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> are not the generic ones. <c>openat</c> takes the mode of a file it makes as a
/// C variadic argument, which a P/Invoke cannot pass on Apple's arm64 calling convention, so on macOS a file made is given
/// its mode through its descriptor once it is made (<c>fchmod</c>); nothing is written to it before.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixHeld : IHeldNative
{
    private const int ReadOnly = 0;
    private const int WriteOnly = 1;

    private const int NoSuchEntry = 2;
    private const int NoAccess = 13;
    private const int NotPermitted = 1;
    private const int Exists = 17;
    private const int NotADirectory = 20;
    private const int IsADirectory = 21;
    private const int Invalid = 22;

    /// <summary>S_IFMT, S_IFREG and S_IFDIR: the bits of a mode that say a file's type, and the two this reads.</summary>
    private const int TypeBits = 0xF000;
    private const int RegularType = 0x8000;
    private const int DirectoryType = 0x4000;

    private static readonly bool OnMac = OperatingSystem.IsMacOS();

    /// <summary>O_NONBLOCK: an open of a pipe never waits for a writer.</summary>
    private static readonly int NonBlocking = OnMac ? 0x4 : 0x800;

    private static readonly int Create = OnMac ? 0x200 : 0x40;

    private static readonly int Exclusive = OnMac ? 0x800 : 0x80;

    private static readonly int NoControllingTerminal = OnMac ? 0x20000 : 0x100;

    private static readonly int CloseOnExec = OnMac ? 0x1000000 : 0x80000;

    private static readonly bool ArmLayout = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm or Architecture.Ppc64le;

    private static readonly int OnlyDirectory = OnMac ? 0x100000 : ArmLayout ? 0x4000 : 0x10000;

    private static readonly int NoFollow = OnMac ? 0x100 : ArmLayout ? 0x8000 : 0x20000;

    private static readonly int RemoveDirectoryFlag = OnMac ? 0x80 : 0x200;

    private static readonly int Loop = OnMac ? 62 : 40;

    private static readonly int NotEmpty = OnMac ? 66 : 39;

    private static readonly int NotImplemented = OnMac ? 78 : 38;

    /// <summary>The mode a file this process makes is given, its umask applied: read once, from a file made as the runtime makes one.</summary>
    private static readonly Lazy<UnixFileMode> NewFileMode = new(MadeFileMode);

    /// <inheritdoc/>
    public SafeFileHandle OpenAnchor(string path)
    {
        var descriptor = Open(path, ReadOnly | OnlyDirectory | CloseOnExec);

        return descriptor >= 0 ? Owned(descriptor) : throw Failed(Marshal.GetLastPInvokeError(), path);
    }

    /// <inheritdoc/>
    public HeldOpen OpenDirectory(SafeFileHandle parent, string name, string path)
    {
        var descriptor = At(parent, name, ReadOnly | OnlyDirectory | NoFollow | CloseOnExec | NonBlocking);

        if (descriptor >= 0)
        {
            return HeldOpen.Of(Owned(descriptor));
        }

        var error = Marshal.GetLastPInvokeError();

        // Linux answers ENOTDIR for a link opened as a directory not to be followed, macOS ELOOP: either way, what is there is
        // told by opening it as itself.
        return error == NoSuchEntry ? HeldOpen.Missing
            : error == NotADirectory || error == Loop ? HeldOpen.Not(KindOf(parent, name, path))
            : throw Failed(error, path);
    }

    /// <inheritdoc/>
    public SafeFileHandle MakeDirectory(SafeFileHandle parent, string name, string path)
    {
        if (MakeDirectoryAt(parent, name, 0x1FF) != 0)
        {
            var error = Marshal.GetLastPInvokeError();

            if (error != Exists)
            {
                throw Failed(error, path);
            }
        }

        // Made, or made by someone else in the meantime: opened as any directory on the way is, a link put there refused.
        var opened = OpenDirectory(parent, name, path);

        return opened.Handle ?? throw HeldDirectory.NotADirectory(opened.Kind, path, path);
    }

    /// <inheritdoc/>
    public PathKind KindOf(SafeFileHandle parent, string name, string path)
    {
        var descriptor = At(parent, name, ReadOnly | NoFollow | NonBlocking | NoControllingTerminal | CloseOnExec);

        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();

            return error == NoSuchEntry ? PathKind.None
                : error == Loop ? PathKind.Link

                // A file this user may not open is a file, and one that refuses an open of its own - a socket - is one too.
                : error is NoAccess or NotPermitted or 6 ? PathKind.File
                : throw Failed(error, path);
        }

        using var handle = Owned(descriptor);

        return (Mode(handle, path) & TypeBits) == DirectoryType ? PathKind.Directory : PathKind.File;
    }

    /// <inheritdoc/>
    public IReadOnlyList<HeldName> Entries(SafeFileHandle directory, string path)
    {
        // On Linux the descriptor's own link under /proc names the directory it holds, whatever was put at its path since; on
        // macOS the path the system keeps for it is listed, and every entry it names is acted on in the directory held alone.
        var listed = OperatingSystem.IsLinux()
            ? $"/proc/self/fd/{directory.DangerousGetHandle().ToInt32()}"
            : OpenedFiles.FinalPath(directory);

        return HeldDirectory.Listed(listed, path);
    }

    /// <inheritdoc/>
    public SafeFileHandle OpenFile(SafeFileHandle parent, string name, string path)
    {
        var descriptor = At(parent, name, ReadOnly | NoFollow | NonBlocking | NoControllingTerminal | CloseOnExec);

        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();

            throw error == Loop ? new LinkOnTheWayException(path, path) : Failed(error, path);
        }

        var handle = Owned(descriptor);

        try
        {
            UnixFiles.RequireRegular(handle, path);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public SafeFileHandle CreateFile(SafeFileHandle parent, string name, string path)
    {
        var descriptor = At(parent, name, WriteOnly | Create | Exclusive | NoFollow | NoControllingTerminal | CloseOnExec, 0x1B6);

        if (descriptor < 0)
        {
            throw Failed(Marshal.GetLastPInvokeError(), path);
        }

        var handle = Owned(descriptor);

        if (OnMac)
        {
            File.SetUnixFileMode(handle, NewFileMode.Value);
        }

        return handle;
    }

    /// <inheritdoc/>
    public void KeepMode(SafeFileHandle parent, string name, SafeFileHandle written)
    {
        var descriptor = At(parent, name, ReadOnly | NoFollow | NonBlocking | NoControllingTerminal | CloseOnExec);

        // Nothing there, a link, or one this user may not read: nothing whose mode the file written keeps.
        if (descriptor < 0)
        {
            return;
        }

        using var replaced = Owned(descriptor);

        if ((Mode(replaced, name) & TypeBits) == RegularType)
        {
            File.SetUnixFileMode(written, File.GetUnixFileMode(replaced));
        }
    }

    /// <inheritdoc/>
    public void Replace(SafeFileHandle parent, string temporary, SafeFileHandle written, string name, string path)
    {
        if (RenameAt(parent, temporary, parent, name) != 0)
        {
            throw Failed(Marshal.GetLastPInvokeError(), path);
        }
    }

    /// <inheritdoc/>
    public void Rename(SafeFileHandle fromParent, string fromName, SafeFileHandle toParent, string toName, bool replace, string from, string to)
    {
        if (replace)
        {
            if (RenameAt(fromParent, fromName, toParent, toName) != 0)
            {
                throw Failed(Marshal.GetLastPInvokeError(), from);
            }

            return;
        }

        if (RenameExclusive(fromParent, fromName, toParent, toName) is { } error and not 0)
        {
            throw error == Exists ? new IOException($"'{to}' is there already, and it is not replaced") : Failed(error, from);
        }
    }

    /// <inheritdoc/>
    public void Remove(SafeFileHandle parent, string name, bool directory, string path)
    {
        if (UnlinkAt(parent, name, directory ? RemoveDirectoryFlag : 0) == 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();

        // macOS answers EPERM for a directory unlinked as a file, as it does for a file it will not let go: told apart by
        // what is there.
        throw error == NoSuchEntry ? new FileNotFoundException($"Could not find '{path}'.", path)
            : error == NotEmpty || error == Exists ? new HeldNotEmptyException(path)
            : error == IsADirectory || (OnMac && error == NotPermitted && !directory && KindOf(parent, name, path) == PathKind.Directory) ? new HeldIsADirectoryException(path)
            : error == NotADirectory && directory ? new HeldNotADirectoryException(path)
            : Failed(error, path);
    }

    /// <summary>
    /// Renames, replacing nothing: by <c>renameat2</c> on Linux and <c>renameatx_np</c> on macOS; where the C library or
    /// the file system has neither, by looking first, so the one thing the instant between reaches is an entry of the same
    /// held directory, never a place a link leads to. 0 where it was renamed; else the error.
    /// </summary>
    private int RenameExclusive(SafeFileHandle fromParent, string fromName, SafeFileHandle toParent, string toName)
    {
        try
        {
            var done = OnMac
                ? RenameX(Descriptor(fromParent), fromName, Descriptor(toParent), toName, 0x4)
                : Rename2(Descriptor(fromParent), fromName, Descriptor(toParent), toName, 1);

            if (done == 0)
            {
                return 0;
            }

            var error = Marshal.GetLastPInvokeError();

            if (error != Invalid && error != NotImplemented)
            {
                return error;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }

        if (KindOf(toParent, toName, toName) != PathKind.None)
        {
            return Exists;
        }

        return RenameAt(fromParent, fromName, toParent, toName) == 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>The mode of what <paramref name="handle"/> is open on.</summary>
    private static int Mode(SafeFileHandle handle, string path)
        => UnixFiles.StatusOf(handle) is { } mode ? mode : throw Failed(Marshal.GetLastPInvokeError(), path);

    /// <summary>
    /// What a failed call on <paramref name="path"/> raises, as the runtime's own file calls raise it: not found, refused, or
    /// any other fault with the system's own words.
    /// </summary>
    internal static Exception Failed(int error, string path)
    {
        var words = Marshal.GetPInvokeErrorMessage(error);

        return error switch
        {
            NoSuchEntry => new FileNotFoundException($"Could not find '{path}': {words}.", path),
            NotADirectory => new DirectoryNotFoundException($"Could not find a part of the path '{path}': {words}."),
            NoAccess or NotPermitted => new UnauthorizedAccessException($"Access to the path '{path}' is denied: {words}."),
            _ when error == Loop => new LinkOnTheWayException(path, path),
            _ => new IOException($"'{path}': {words}", error),
        };
    }

    /// <summary>The mode the runtime gives a file it makes here, the umask applied: a file made in the temporary directory, read and removed.</summary>
    private static UnixFileMode MadeFileMode()
    {
        var probe = Path.Combine(Path.GetTempPath(), "dssharness-mode-" + Guid.NewGuid().ToString("N"));

        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write))
            {
            }

            return File.GetUnixFileMode(probe);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private static SafeFileHandle Owned(int descriptor) => new((IntPtr)descriptor, ownsHandle: true);

    private static int Descriptor(SafeFileHandle handle) => handle.DangerousGetHandle().ToInt32();

    /// <summary>openat on <paramref name="parent"/>, holding it open through the call.</summary>
    private static int At(SafeFileHandle parent, string name, int flags, int mode = 0)
    {
        var added = false;

        try
        {
            parent.DangerousAddRef(ref added);
            return OpenAt(Descriptor(parent), name, flags, mode);
        }
        finally
        {
            if (added)
            {
                parent.DangerousRelease();
            }
        }
    }

    private static int MakeDirectoryAt(SafeFileHandle parent, string name, int mode) => MkdirAt(Descriptor(parent), name, mode);

    private static int UnlinkAt(SafeFileHandle parent, string name, int flags) => UnlinkAtNative(Descriptor(parent), name, flags);

    private static int RenameAt(SafeFileHandle fromParent, string fromName, SafeFileHandle toParent, string toName)
        => RenameAtNative(Descriptor(fromParent), fromName, Descriptor(toParent), toName);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, int mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int MkdirAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int UnlinkAtNative(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int RenameAtNative(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Rename2(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int RenameX(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
}
