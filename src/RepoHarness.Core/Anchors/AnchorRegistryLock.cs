using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Anchors;

/// <summary>Serialises changes to one pair of registries across every DssHarness process on the machine.</summary>
public interface IAnchorRegistryLock
{
    /// <summary>
    /// Runs <paramref name="work"/> while holding the lock for <paramref name="registries"/>.
    /// </summary>
    /// <remarks>
    /// The work is synchronous on purpose, and must stay so: the lock is released by the thread that
    /// took it, and an await inside the work could resume on a different one.
    /// </remarks>
    /// <exception cref="HarnessException">Another process held the lock for longer than the wait allows.</exception>
    T RunExclusive<T>(AnchorRegistries registries, Func<T> work);
}

/// <summary>Reads of the two registries as one moment.</summary>
public static class AnchorRegistryReading
{
    /// <summary>
    /// Each registry's text as it stands, or <see langword="null"/> where it has no file, both read together while
    /// holding the lock.
    /// </summary>
    /// <remarks>
    /// One file needs no lock, since every write replaces a whole file in one rename. Two do: a change moves a row by
    /// writing one file and then the other, so the two read apart can hold that row in neither, which reads exactly
    /// like an anchor closed or lost, or in both, which reads as a duplicate.
    /// </remarks>
    /// <exception cref="HarnessException">Another process held the lock for longer than the wait allows.</exception>
    public static IReadOnlyList<(AnchorRegistry Registry, string? Text)> ReadTogether(
        this IAnchorRegistryLock registryLock,
        AnchorRegistries registries,
        IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(registryLock);
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(fileSystem);

        return registryLock.RunExclusive(registries, () => registries.All.Select(registry => (registry, registry.ReadText(fileSystem))).ToList());
    }
}

/// <inheritdoc cref="IAnchorRegistryLock"/>
/// <remarks>
/// A change reads both registries, decides, and writes them back, so two changes at once would each
/// write over the other and one would be lost without a word. A named mutex is the machine-wide lock
/// .NET supports on Windows, Linux and macOS alike; a named semaphore is not available on Linux or macOS.
/// Anchor changes take milliseconds, so the wait is short, and a lock still held after it is reported
/// rather than waited on indefinitely.
/// </remarks>
public sealed class NamedMutexAnchorRegistryLock(IHostPlatform platform, TimeSpan timeout) : IAnchorRegistryLock
{
    /// <summary>How long a change waits for another before refusing.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IHostPlatform _platform = platform;
    private readonly TimeSpan _timeout = timeout;

    public T RunExclusive<T>(AnchorRegistries registries, Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(work);

        using var mutex = MachineWideMutex.Open(NameFor(registries), "the anchor registries");

        if (!MachineWideMutex.Wait(mutex, _timeout))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"Another {ToolPackage.Id} process has held the anchor registries for {_timeout.TotalSeconds:0} seconds, "
                + "so nothing was read or changed. Run the command again once it has finished.");
        }

        try
        {
            return work();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>The lock's name: one per pair of registry files, however their paths are spelled.</summary>
    public string NameFor(AnchorRegistries registries)
    {
        ArgumentNullException.ThrowIfNull(registries);

        return MachineWideMutex.NameFor(
            "anchors",
            registries.All.Select(registry => registry.FullPath),
            _platform.PathComparison == StringComparison.OrdinalIgnoreCase);
    }
}
