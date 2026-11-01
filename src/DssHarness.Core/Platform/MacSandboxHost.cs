using System.Runtime.InteropServices;
using System.Text;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Platform;

/// <summary>
/// What finds macOS's sandbox on a machine (<see cref="IsolationPlatform"/>): whether a file is there, whether this process
/// already runs inside a sandbox, the version of macOS, and a path's real spelling - each a question of the machine's own,
/// asked only on macOS, and answered by a test of its own anywhere.
/// </summary>
/// <param name="FileThere">Whether a file is there, by its whole path.</param>
/// <param name="InsideASandbox">Whether this process already runs inside a sandbox, which macOS starts no other inside.</param>
/// <param name="MacOsVersion">The version of macOS.</param>
/// <param name="RealPath">A path's real spelling, as the sandbox meets it: every link along it followed.</param>
public sealed record MacSandboxHost(Func<string, bool> FileThere, Func<bool> InsideASandbox, Func<string> MacOsVersion, Func<string, string> RealPath)
{
    /// <summary>The longest path <c>realpath</c> writes on macOS, its end included.</summary>
    private const int MostPathBytes = 1024;

    /// <summary>This machine's own answers.</summary>
    public static MacSandboxHost ThisMachine { get; } = new(File.Exists, Sandboxed, () => Environment.OSVersion.Version.ToString(), Real);

    /// <summary>
    /// Whether this process runs inside a sandbox: <c>sandbox_check</c> of its own id with no operation answers one where it
    /// does - asked with its fixed arguments alone, which every calling convention passes alike. Not inside one where the
    /// system cannot be asked, so the probe's own trial says what holds.
    /// </summary>
    private static bool Sandboxed()
    {
        try
        {
            return SandboxCheck(Environment.ProcessId, IntPtr.Zero, 0) > 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="path"/>'s real spelling from the system's own <c>realpath</c>, which names each part as the file
    /// system holds it; where it cannot answer - a part not there, a path too long - every link followed as this tool
    /// follows them (<see cref="LinkPaths.Resolve"/>).
    /// </summary>
    private static string Real(string path)
    {
        try
        {
            var resolved = new byte[MostPathBytes];

            if (RealPathOf(Encoding.UTF8.GetBytes(path + "\0"), resolved) != IntPtr.Zero)
            {
                var end = Array.IndexOf(resolved, (byte)0);

                return Encoding.UTF8.GetString(resolved, 0, end < 0 ? resolved.Length : end);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // The system cannot be asked: this tool's own resolution answers.
        }

        return LinkPaths.Resolve(path);
    }

    [DllImport("libc", EntryPoint = "sandbox_check")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int SandboxCheck(int pid, IntPtr operation, int type);

    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr RealPathOf(byte[] path, byte[] resolved);
}
