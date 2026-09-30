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

    public string MachineId => _machineId ??= ReadMachineId() is { Length: > 0 } id ? id : Environment.MachineName;

    private string? _machineId;

    /// <summary>The identifier this machine's system keeps for it, or <see langword="null"/> where it gives none.</summary>
    private static string? ReadMachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var cryptography = Microsoft.Win32.RegistryKey
                    .OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

                return (cryptography?.GetValue("MachineGuid") as string)?.Trim();
            }

            if (OperatingSystem.IsMacOS())
            {
                var uuid = new byte[16];
                var wait = new Timespec { Seconds = 5 };

                return GetHostUuid(uuid, ref wait) == 0 ? Convert.ToHexString(uuid) : null;
            }

            // systemd's, and before it D-Bus's, which a system without systemd keeps.
            return new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" }
                .Where(File.Exists)
                .Select(path => File.ReadAllText(path).Trim())
                .FirstOrDefault(id => id.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
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
