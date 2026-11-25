using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Tests;

/// <summary>
/// A process of Windows whose own security refuses its end to everyone, its owner included - as a program that guards
/// itself against being ended by other software of the same user does - and what a test needs beside one: whether this
/// process may end it all the same, and its end, the refusal lifted first.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RefusedEnd
{
    /// <summary>PROCESS_TERMINATE: the right a process's end takes.</summary>
    private const uint Terminate = 0x0001;

    /// <summary>WRITE_DAC: the right a process's own security is written with.</summary>
    private const uint WriteSecurity = 0x0004_0000;

    /// <summary>DACL_SECURITY_INFORMATION: what of a security descriptor is written.</summary>
    private const uint AccessList = 0x4;

    /// <summary>SDDL_REVISION_1: the one revision of the security descriptor language.</summary>
    private const uint Revision = 1;

    /// <summary>
    /// Has this process's own security refuse PROCESS_TERMINATE to everyone, every other right left to its user and to the
    /// system: a handle got before keeps what it was granted, and a handle opened after cannot end it.
    /// </summary>
    public static void Refuse()
    {
        using var self = new SafeProcessHandle(GetCurrentProcess(), ownsHandle: false);

        Write(self, $"D:(D;;0x1;;;WD)(A;;0x1fffff;;;{User})(A;;0x1fffff;;;SY)");
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

    /// <summary>Ends <paramref name="process"/>, with its tree, where it still runs - its refusal lifted first, so its end is this process's to make.</summary>
    /// <param name="process">The process.</param>
    public static void End(LearnedProcess process)
    {
        if (!process.Running)
        {
            return;
        }

        using (var handle = OpenProcess(WriteSecurity, inheritHandle: false, process.Id))
        {
            if (!handle.IsInvalid)
            {
                Write(handle, $"D:(A;;0x1fffff;;;{User})(A;;0x1fffff;;;SY)");
            }
        }

        process.TryEnding();
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
}
