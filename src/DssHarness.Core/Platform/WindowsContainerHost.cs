using Microsoft.Mxc.Sdk.V1;

namespace DssHarness.Core.Platform;

/// <summary>
/// What finds MXC's process container on a Windows machine (<see cref="IsolationPlatform"/>): whether MXC's library says
/// the machine has one that denies a place outright, and the library's version - a question of the machine's own, asked
/// only on Windows, and answered by a test of its own anywhere.
/// </summary>
/// <param name="Support">Whether the machine has the container, and the library's version - or why it has none.</param>
public sealed record WindowsContainerHost(Func<WindowsContainerSupport> Support)
{
    /// <summary>This machine's own answer, from MXC's library.</summary>
    public static WindowsContainerHost ThisMachine { get; } = new(Asked);

    /// <summary>The Windows builds MXC's process container needs, as a remedy and help say them.</summary>
    public const string Builds = "Windows 11 24H2 build 26100.9278 or later, or 26H2 build 26300.9550 or later";

    /// <summary>
    /// What MXC's library says of this machine: its process container among the backends it can use, able to deny a place
    /// outright - which a hidden place needs - with the library's version; or why not, the library's own words where it
    /// gives them.
    /// </summary>
    private static WindowsContainerSupport Asked()
    {
        try
        {
            var container = MxcPlatform.GetAvailableBackends().FirstOrDefault(backend => backend.Backend == ContainmentBackend.ProcessContainer);

            if (container is null)
            {
                var support = MxcPlatform.GetPlatformSupport();

                return WindowsContainerSupport.None(
                    $"MXC's process container is not available on this Windows{(support.Reason is { Length: > 0 } reason ? $": {reason.TrimEnd('.')}" : string.Empty)}");
            }

            if (container.Capabilities is not { } capabilities || !capabilities.Contains(BackendCapability.FilesystemDeniedPaths))
            {
                return WindowsContainerSupport.None("MXC's process container on this Windows cannot deny a place outright, which hiding a credential slot needs");
            }

            return new WindowsContainerSupport(MxcPlatform.NativeVersion, null);
        }
        catch (MxcException ex)
        {
            return WindowsContainerSupport.None($"MXC's library could not say whether this Windows has its process container: {ex.Message.TrimEnd('.')} ({ex.Code})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            return WindowsContainerSupport.None($"MXC's library could not be loaded on this Windows: {ex.Message.TrimEnd('.')}");
        }
    }
}

/// <summary>Whether a Windows machine has MXC's process container: the library's version where it has, why not where it has none.</summary>
/// <param name="Version">The version of MXC's library; null where there is no container.</param>
/// <param name="Unavailable">Why there is none, as the opening of a sentence; null where there is one.</param>
public sealed record WindowsContainerSupport(string? Version, string? Unavailable)
{
    /// <summary>No container, and why.</summary>
    /// <param name="why">Why, as the opening of a sentence.</param>
    public static WindowsContainerSupport None(string why) => new(null, why ?? throw new ArgumentNullException(nameof(why)));
}
