using System.Runtime.InteropServices;

namespace DssHarness.Core.Processes;

/// <summary>
/// Linux's Landlock, as the contained step asks it before it becomes the isolation's mechanism: a domain of the step's own
/// that scopes abstract Unix sockets and signals, so no process inside connects to an abstract socket a process outside
/// listens on - a session's message bus, a display server - nor signals a process outside. Every process the step becomes
/// and starts stays in the domain. Kernels before Linux 6.12 have no such scope (Landlock's sixth version); the probe asks
/// the version once (<see cref="Version"/>), and the records say what is left where there is none.
/// </summary>
public static class Landlock
{
    /// <summary>The first version of Landlock with the scope of abstract Unix sockets and signals.</summary>
    public const int ScopedVersion = 6;

    /// <summary>landlock_create_ruleset's number, the same on every architecture Linux gives it.</summary>
    private const long CreateRuleset = 444;

    /// <summary>landlock_restrict_self's number, the same on every architecture Linux gives it.</summary>
    private const long RestrictSelf = 446;

    /// <summary>LANDLOCK_CREATE_RULESET_VERSION: landlock_create_ruleset answers the kernel's version of Landlock.</summary>
    private const uint VersionFlag = 1;

    /// <summary>LANDLOCK_SCOPE_ABSTRACT_UNIX_SOCKET and LANDLOCK_SCOPE_SIGNAL.</summary>
    private const ulong AbstractSocketsAndSignals = 0b11;

    /// <summary>PR_SET_NO_NEW_PRIVS, which a domain is entered only under without the right to administer the system.</summary>
    private const int NoNewPrivileges = 38;

    /// <summary>The kernel's version of Landlock; 0 where it has none, or this is no Linux.</summary>
    public static int Version()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }

        try
        {
            var version = Syscall(CreateRuleset, IntPtr.Zero, UIntPtr.Zero, VersionFlag);
            return version > 0 ? (int)version : 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Enters this thread - the one the step becomes the mechanism from - into a domain that scopes abstract Unix sockets
    /// and signals, no file rule among it.
    /// </summary>
    /// <returns>0 where the domain was entered; else the error number the kernel gave.</returns>
    public static int Scope()
    {
        var attributes = new RulesetAttributes { HandledAccessFs = 0, HandledAccessNet = 0, Scoped = AbstractSocketsAndSignals };
        var size = Marshal.SizeOf<RulesetAttributes>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(attributes, buffer, fDeleteOld: false);
            var ruleset = Syscall(CreateRuleset, buffer, (UIntPtr)size, 0);

            if (ruleset < 0)
            {
                return Marshal.GetLastPInvokeError();
            }

            try
            {
                if (Prctl(NoNewPrivileges, 1, 0, 0, 0) != 0 || Syscall(RestrictSelf, (IntPtr)ruleset, UIntPtr.Zero, 0) != 0)
                {
                    return Marshal.GetLastPInvokeError();
                }

                return 0;
            }
            finally
            {
                _ = Close((int)ruleset);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>struct landlock_ruleset_attr, as Landlock's sixth version reads it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RulesetAttributes
    {
        public ulong HandledAccessFs;
        public ulong HandledAccessNet;
        public ulong Scoped;
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long Syscall(long number, IntPtr first, UIntPtr second, uint third);

    [DllImport("libc", EntryPoint = "prctl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Prctl(int option, ulong second, ulong third, ulong fourth, ulong fifth);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Close(int descriptor);
}
