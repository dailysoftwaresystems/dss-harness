namespace DssHarness.Core.Platform;

/// <summary>
/// Chooses the permission model for the running platform. One of the few places that
/// observes the operating system, beside <see cref="HostPlatform"/> and the primitives
/// docs/architecture.md names under "The platform layer"; everything downstream depends
/// on <see cref="IFilePermissions"/>.
/// </summary>
public static class FilePermissionsFactory
{
    /// <summary>Creates the implementation matching the running platform.</summary>
    public static IFilePermissions Create()
    {
        // Written as an OperatingSystem guard rather than a PlatformId comparison so the
        // platform-compatibility analyser can prove the POSIX-only call site is unreachable
        // on Windows. Keeping the decision here is what stops that check spreading.
        if (OperatingSystem.IsWindows())
        {
            return new WindowsFilePermissions();
        }

        return new PosixFilePermissions();
    }
}
