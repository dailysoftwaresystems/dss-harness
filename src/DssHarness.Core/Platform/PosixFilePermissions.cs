using System.Runtime.Versioning;

namespace DssHarness.Core.Platform;

/// <summary>
/// Owner-only access and execute bits on Linux and macOS, which share the Unix mode model.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class PosixFilePermissions : IFilePermissions
{
    private const UnixFileMode AnyExecute =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Any access at all for the group or for others: what ssh calls an unprotected key.</summary>
    private const UnixFileMode GroupOrOtherAccess =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Write access for the group or for others: what lets another user rewrite a configuration file.</summary>
    private const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;

    public void ProtectSecret(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public bool IsExecutable(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Without this, a non-executable file that happens to be called "git" earlier on
        // PATH would be reported as git being installed, and every later invocation would
        // fail with a permission error that points nowhere near the cause.
        return File.Exists(path) && (File.GetUnixFileMode(path) & AnyExecute) != 0;
    }

    public void SetExecutable(string path, bool executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var mode = File.GetUnixFileMode(path);
        var wanted = Executable(mode, executable);

        if (wanted != mode)
        {
            File.SetUnixFileMode(path, wanted);
        }
    }

    /// <summary>
    /// <paramref name="mode"/> made one a file that can be run has, or one that cannot: whoever may read it may run it, and
    /// its owner always - what git itself writes for a file it holds as one that can be run, and no wider than the file
    /// already was - or every execute bit cleared. The one rule of it, whether a file is reached by its path or its handle.
    /// </summary>
    /// <param name="mode">The file's mode.</param>
    /// <param name="executable">Whether it is to be one that can be run.</param>
    public static UnixFileMode Executable(UnixFileMode mode, bool executable)
        => executable
            ? mode
                | UnixFileMode.UserExecute
                | ((mode & UnixFileMode.GroupRead) != 0 ? UnixFileMode.GroupExecute : UnixFileMode.None)
                | ((mode & UnixFileMode.OtherRead) != 0 ? UnixFileMode.OtherExecute : UnixFileMode.None)
            : mode & ~AnyExecute;

    public void CopyMode(string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);

        try
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing is there to keep the mode of.
        }
    }

    public bool IsPrivate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) && (File.GetUnixFileMode(path) & GroupOrOtherAccess) == 0;
    }

    public bool IsWritableByOthers(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.Exists(path) && (File.GetUnixFileMode(path) & GroupOrOtherWrite) != 0;
    }
}
