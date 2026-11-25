using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using DssHarness.Core.Execution;
using DssHarness.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Tests;

/// <summary>
/// A process of Windows whose own security refuses its end to everyone, its owner included - as a program that guards
/// itself against being ended by other software of the same user does - and what a test needs beside one: a process whose
/// rights no process's security can refuse anything made one that any can (<see cref="Removed"/>), whether this process may
/// end one all the same, and its end, the refusal lifted first.
/// </summary>
/// <remarks>
/// A process holding the debug privilege enabled - an administrator's elevated process, as a CI runner's is - opens any
/// process whatever its security says, and .NET enables that privilege the first time a process holds it and asks
/// System.Diagnostics.Process of another: a test of a refusal runs where the privilege is gone from its token
/// (<see cref="ChildRun.UnprivilegedAsync"/>).
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class RefusedEnd
{
    /// <summary>The privilege that passes over every process's own security: SeDebugPrivilege.</summary>
    public const string DebugPrivilege = "SeDebugPrivilege";

    /// <summary>PROCESS_TERMINATE: the right a process's end takes.</summary>
    private const uint Terminate = 0x0001;

    /// <summary>WRITE_DAC: the right a process's own security is written with, its owner's whatever that security says.</summary>
    private const uint WriteSecurity = 0x0004_0000;

    /// <summary>DACL_SECURITY_INFORMATION: what of a security descriptor is written.</summary>
    private const uint AccessList = 0x4;

    /// <summary>SDDL_REVISION_1: the one revision of the security descriptor language.</summary>
    private const uint Revision = 1;

    /// <summary>TOKEN_ADJUST_PRIVILEGES and TOKEN_QUERY: the rights a token's privileges are removed and read with.</summary>
    private const uint AdjustAndQuery = 0x0020 | 0x0008;

    /// <summary>SE_PRIVILEGE_REMOVED: a privilege taken out of a token, never to be enabled again.</summary>
    private const uint PrivilegeRemoved = 0x4;

    /// <summary><c>TokenPrivileges</c>: the class of information a token's privileges are read as.</summary>
    private const int TokenPrivilegesClass = 3;

    /// <summary>ERROR_INSUFFICIENT_BUFFER: what a read with too little room answers, its length said.</summary>
    private const int InsufficientBuffer = 122;

    private static readonly ProcessIdentity Identity = new(new HostPlatform());

    /// <summary>
    /// Has this process's own security refuse PROCESS_TERMINATE to everyone, every other right left to its user and to the
    /// system - or, where <paramref name="everything"/>, refuse everyone every right, none left to its user, so no handle
    /// opened after even asks when it was created: a handle got before keeps what it was granted.
    /// </summary>
    /// <param name="everything">Whether every right is refused, not its end alone.</param>
    public static void Refuse(bool everything = false)
    {
        using var self = new SafeProcessHandle(GetCurrentProcess(), ownsHandle: false);

        Write(self, everything ? "D:(D;;0x1fffff;;;WD)(A;;0x1fffff;;;SY)" : $"D:(D;;0x1;;;WD)(A;;0x1fffff;;;{User})(A;;0x1fffff;;;SY)");
    }

    /// <summary>
    /// Whether this process may open the process <paramref name="id"/> names to end it: false where that process's own
    /// security refuses it - and true where this process's rights pass over any process's security, its debug privilege
    /// enabled, so that no process can refuse it.
    /// </summary>
    /// <param name="id">The process's id.</param>
    public static bool MayEnd(int id)
    {
        using var handle = OpenProcess(Terminate, inheritHandle: false, id);

        return !handle.IsInvalid;
    }

    /// <summary>
    /// Whether the very process <paramref name="process"/> still stands, as the list Windows keeps of every process
    /// names it: asked of nothing the process's own security can refuse.
    /// </summary>
    /// <param name="process">The process.</param>
    public static bool Stands(LearnedProcess process) => process.Stamp is { } stamp && Identity.IsSame(process.Id, stamp);

    /// <summary>
    /// Ends <paramref name="process"/>, with its tree, where it still stands - its refusal lifted first, by the right its
    /// owner holds whatever its security says, so its end is this process's to make.
    /// </summary>
    /// <param name="process">The process.</param>
    public static void End(LearnedProcess process)
    {
        if (!Stands(process))
        {
            return;
        }

        using (var handle = OpenProcess(WriteSecurity, inheritHandle: false, process.Id))
        {
            if (!handle.IsInvalid && Stands(process))
            {
                Write(handle, $"D:(A;;0x1fffff;;;{User})(A;;0x1fffff;;;SY)");
            }
        }

        process.TryEnding();
    }

    /// <summary>
    /// Removes <paramref name="privilege"/> from this process's own token, where the token holds it - for good: a privilege
    /// removed cannot be enabled again - and reads the token back: why it still holds it, or could not be read, where it
    /// does; <see langword="null"/> where the token holds it no more.
    /// </summary>
    /// <param name="privilege">The privilege, by its name: <see cref="DebugPrivilege"/>.</param>
    public static string? Removed(string privilege)
    {
        if (!LookupPrivilegeValueW(null, privilege, out var luid))
        {
            return $"{privilege} could not be looked up: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
        }

        if (!OpenProcessToken(GetCurrentProcess(), AdjustAndQuery, out var token))
        {
            return $"this process's token could not be opened: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
        }

        using (token)
        {
            var removal = new TokenPrivilege { Count = 1, Luid = luid, Attributes = PrivilegeRemoved };

            // A token that never held the privilege answers that not all were assigned: it holds it no more either way.
            if (!AdjustTokenPrivileges(token, disableAll: false, ref removal, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return $"{privilege} could not be removed from this process's token: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}";
            }

            try
            {
                return Holds(token, luid) ? $"this process's token still holds {privilege} once it was removed" : null;
            }
            catch (Win32Exception ex)
            {
                return $"this process's token could not be read back: {ex.Message}";
            }
        }
    }

    /// <summary>Whether this process's own token holds <paramref name="privilege"/>, enabled or not.</summary>
    /// <param name="privilege">The privilege, by its name.</param>
    /// <exception cref="Win32Exception">The privilege could not be looked up, or the token opened or read.</exception>
    public static bool Holds(string privilege)
    {
        if (!LookupPrivilegeValueW(null, privilege, out var luid) || !OpenProcessToken(GetCurrentProcess(), AdjustAndQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using (token)
        {
            return Holds(token, luid);
        }
    }

    /// <summary>The user this process runs as, by its SID.</summary>
    private static string User
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();

            return identity.User!.Value;
        }
    }

    /// <summary>Whether <paramref name="token"/> lists the privilege <paramref name="luid"/> names among its own, enabled or not.</summary>
    private static bool Holds(SafeAccessTokenHandle token, Luid luid)
    {
        if (!GetTokenInformation(token, TokenPrivilegesClass, IntPtr.Zero, 0, out var length) && Marshal.GetLastPInvokeError() != InsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var read = Marshal.AllocHGlobal(length);

        try
        {
            if (!GetTokenInformation(token, TokenPrivilegesClass, read, length, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // TOKEN_PRIVILEGES: a count, then that many LUID_AND_ATTRIBUTES of twelve bytes each.
            var count = Marshal.ReadInt32(read);

            for (var index = 0; index < count; index++)
            {
                var at = sizeof(int) + (index * 12);

                if (unchecked((uint)Marshal.ReadInt32(read, at)) == luid.Low && Marshal.ReadInt32(read, at + sizeof(int)) == luid.High)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(read);
        }
    }

    /// <summary>Writes the access list <paramref name="language"/> states as the security of the process <paramref name="process"/> holds.</summary>
    private static void Write(SafeProcessHandle process, string language)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(language, Revision, out var descriptor, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            if (!SetKernelObjectSecurity(process, AccessList, descriptor))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    /// <summary>A LUID: what a privilege is known by on this machine.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    /// <summary>TOKEN_PRIVILEGES holding one privilege and what is done to it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string language, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeProcessHandle process, uint information, IntPtr descriptor);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivilege state,
        uint length,
        IntPtr previous,
        IntPtr returned);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returned);
}
