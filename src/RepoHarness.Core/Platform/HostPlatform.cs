using System.Runtime.InteropServices;

namespace RepoHarness.Core.Platform;

/// <inheritdoc cref="IHostPlatform"/>
public sealed class HostPlatform : IHostPlatform
{
    /// <summary>Windows refuses most paths beyond this length.</summary>
    public const int WindowsMaxPath = 260;

    public HostPlatform()
    {
        Current = ResolveCurrent();
    }

    public PlatformId Current { get; }

    public string PlatformKey => Current switch
    {
        PlatformId.Windows => PlatformNames.Windows,
        PlatformId.Linux => PlatformNames.Linux,
        PlatformId.MacOs => PlatformNames.MacOs,
        _ => throw new InvalidOperationException($"Unmapped platform '{Current}'."),
    };

    public string Processor { get; } = PlatformNames.ForArchitecture(RuntimeInformation.OSArchitecture);

    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public StringComparison PathComparison => Current == PlatformId.Windows
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public int? MaxPathLength => Current == PlatformId.Windows ? WindowsMaxPath : null;

    public string ExecutableName(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (Current != PlatformId.Windows)
        {
            return command;
        }

        return Path.HasExtension(command) ? command : command + ".exe";
    }

    public MachineIdentity MachineId => _machineId ??= ReadMachineId();

    private MachineIdentity? _machineId;

    /// <summary>The identifier this machine's system keeps for it; where it gives none, the machine's name and why.</summary>
    private static MachineIdentity ReadMachineId()
    {
        string byName;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var cryptography = Microsoft.Win32.RegistryKey
                    .OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

                if ((cryptography?.GetValue("MachineGuid") as string)?.Trim() is { Length: > 0 } guid)
                {
                    return new(guid, null);
                }

                byName = @"the registry holds no MachineGuid under HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography";
            }
            else if (OperatingSystem.IsMacOS())
            {
                var uuid = new byte[16];
                var wait = new Timespec { Seconds = 5 };

                if (GetHostUuid(uuid, ref wait) == 0)
                {
                    return new(Convert.ToHexString(uuid), null);
                }

                byName = $"gethostuuid failed with error {Marshal.GetLastPInvokeError()}";
            }
            else
            {
                // systemd's, and before it D-Bus's, which a system without systemd keeps.
                string[] kept = ["/etc/machine-id", "/var/lib/dbus/machine-id"];

                if (kept.Where(File.Exists).Select(path => File.ReadAllText(path).Trim()).FirstOrDefault(id => id.Length > 0) is { } id)
                {
                    return new(id, null);
                }

                byName = $"neither {kept[0]} nor {kept[1]} holds one";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException)
        {
            byName = ex.Message.TrimEnd('.');
        }

        return new(Environment.MachineName, byName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public nint Seconds;
        public nint Nanoseconds;
    }

    [DllImport("libc", EntryPoint = "gethostuuid", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int GetHostUuid(byte[] uuid, ref Timespec wait);

    private static PlatformId ResolveCurrent()
    {
        if (OperatingSystem.IsWindows())
        {
            return PlatformId.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return PlatformId.MacOs;
        }

        if (OperatingSystem.IsLinux())
        {
            return PlatformId.Linux;
        }

        throw new PlatformNotSupportedException(
            "DssHarness supports Windows, Linux and macOS only.");
    }
}
