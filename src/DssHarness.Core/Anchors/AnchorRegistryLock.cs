using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Anchors;

/// <summary>
/// Serialises changes to one pair of registries, and reads of both, across every DssHarness process on the machine.
/// </summary>
public interface IAnchorRegistryLock
{
    /// <summary>
    /// Runs <paramref name="work"/> while holding the lock for <paramref name="registries"/>, waiting for the lock while
    /// another process holds it.
    /// </summary>
    /// <remarks>
    /// The work is synchronous: a read, a decision and a write of two files, which holds the lock only as long as they take.
    /// </remarks>
    /// <param name="registries">The pair of registries the lock is named for.</param>
    /// <param name="work">What is done while the lock is held.</param>
    /// <param name="cancellationToken">Ends the wait for the lock where it has not been taken; the work, once begun, ends as it ends.</param>
    /// <exception cref="HarnessException">
    /// The lock could not be opened: it belongs to another user (<see cref="HarnessExit.Refused"/>), or the system would
    /// not open it (<see cref="HarnessExit.HostUnavailable"/>); or another process held it past a bound the lock was given
    /// (<see cref="HarnessExit.Refused"/>). Where none was given, the lock is waited for while its holder lives.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> ended the wait, nothing read or changed.</exception>
    Task<T> RunExclusiveAsync<T>(AnchorRegistries registries, Func<T> work, CancellationToken cancellationToken);
}

/// <summary>Reads of the two registries as one moment.</summary>
public static class AnchorRegistryReading
{
    /// <summary>
    /// Each registry's text as it stands, or <see langword="null"/> where nothing is there, both read together while
    /// holding the lock: one per registry, in <see cref="AnchorRegistries.All"/> order.
    /// </summary>
    /// <remarks>
    /// One file needs no lock, since every write replaces a whole file in one rename. Two do: a change moves a row by
    /// writing one file and then the other, so the two read apart can hold that row in neither, which reads exactly
    /// like an anchor closed or lost, or in both, which reads as a duplicate.
    /// </remarks>
    /// <exception cref="HarnessException">
    /// The lock could not be taken, as <see cref="IAnchorRegistryLock.RunExclusiveAsync"/> says; or a registry's path holds a
    /// directory, or its file could not be read (<see cref="AnchorRegistry.ReadText"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> ended the wait for the lock.</exception>
    public static Task<List<(AnchorRegistry Registry, string? Text)>> ReadTogetherAsync(
        this IAnchorRegistryLock registryLock,
        AnchorRegistries registries,
        IFileSystem fileSystem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registryLock);
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(fileSystem);

        return registryLock.RunExclusiveAsync(
            registries,
            () => registries.All.Select(registry => (registry, registry.ReadText(fileSystem))).ToList(),
            cancellationToken);
    }
}

/// <inheritdoc cref="IAnchorRegistryLock"/>
/// <remarks>
/// <para>
/// A change reads both registries, decides, and writes them back, so two changes at once would each
/// write over the other and one would be lost without a word. A named mutex is the machine-wide lock
/// .NET supports on Windows, Linux and macOS alike; a named semaphore is not available on Linux or macOS.
/// </para>
/// <para>
/// The lock is waited for while its holder lives, through the hold every machine-wide lock of the tool is waited for by
/// (<see cref="MachineWideMutex.HoldAsync"/>): a holder that ends holding it lets it go as it ends - the system passes a
/// mutex its holder ended holding to the next to wait - so a wait ends when the holder does, however long its work. The
/// wait is told as it begins, naming what holds the lock, and every <see cref="Waits.SaidEvery"/> while it lasts
/// (<see cref="Waits.ForAsync"/>), so a change waiting on a long one reads as waiting, never as hung; the person's
/// interruption ends it, nothing read or changed.
/// </para>
/// </remarks>
public sealed class NamedMutexAnchorRegistryLock : IAnchorRegistryLock
{

    /// <summary>
    /// What a wait for the lock is said under: the registries' own name, as a leg's admission's wait is said under
    /// <c>admission</c>, since one lock serves every command that reads or changes them.
    /// </summary>
    public const string TellsAs = "anchors";

    /// <summary>What a wait for the lock is told as, after <c>waits for</c>, and what a refusal to open it names.</summary>
    private const string Subject = "the anchor registries";

    /// <summary>What the lock's holder is named as: a mutex says nothing of who holds it.</summary>
    private static readonly string HolderName = $"another {ToolPackage.Id} process";

    private readonly IHostPlatform _platform;
    private readonly IHarnessOutput? _output;
    private readonly TimeProvider _clock;
    private readonly TimeSpan? _bound;

    /// <summary>A lock waited for while its holder lives, each wait told through <paramref name="output"/>.</summary>
    /// <param name="platform">Says how this machine compares paths, which names the lock.</param>
    /// <param name="output">Tells a wait for the lock as it begins and every <see cref="Waits.SaidEvery"/> while it lasts.</param>
    /// <param name="clock">Measures a wait.</param>
    public NamedMutexAnchorRegistryLock(IHostPlatform platform, IHarnessOutput output, TimeProvider clock)
        : this(platform, output ?? throw new ArgumentNullException(nameof(output)), clock, bound: null)
    {
    }

    /// <summary>
    /// A lock that refuses once <paramref name="bound"/> has passed with the lock still held, telling nothing: the bound a
    /// test sets in place of a person's interruption, so a holder that never lets go is refused rather than waited for.
    /// </summary>
    /// <param name="platform">Says how this machine compares paths, which names the lock.</param>
    /// <param name="bound">How long a change or a read waits for the lock before refusing.</param>
    internal NamedMutexAnchorRegistryLock(IHostPlatform platform, TimeSpan bound)
        : this(platform, output: null, TimeProvider.System, bound)
    {
    }

    private NamedMutexAnchorRegistryLock(IHostPlatform platform, IHarnessOutput? output, TimeProvider clock, TimeSpan? bound)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(clock);

        _platform = platform;
        _output = output;
        _clock = clock;
        _bound = bound;
    }

    public async Task<T> RunExclusiveAsync<T>(AnchorRegistries registries, Func<T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registries);
        ArgumentNullException.ThrowIfNull(work);

        using var hold = await TakeAsync(NameFor(registries), cancellationToken).ConfigureAwait(false);

        return work();
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

    /// <summary>
    /// Takes the mutex <paramref name="name"/>: at once where nobody holds it; otherwise told as the wait begins, and
    /// waited for until its holder lets it go or ends, the person's interruption, or the lock's bound where it has one.
    /// </summary>
    /// <exception cref="HarnessException">The mutex could not be opened, or the lock's bound passed with it still held.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> ended the wait.</exception>
    private async Task<MachineWideHold> TakeAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (MachineWideMutex.TryHold(name, Subject) is { } free)
        {
            return free;
        }

        Tell(new WaitTold(Subject, TimeSpan.Zero, HolderName, null));

        var ended = await Waits.ForAsync(
            async stop => await MachineWideMutex.HoldAsync(name, Subject, Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false)
                ?? throw new OperationCanceledException(stop),
            new WaitWatch(Subject, Tell, _bound, _clock),
            cancellationToken).ConfigureAwait(false);

        return ended.Kind switch
        {
            WaitEndKind.Done => ended.Value!,
            WaitEndKind.Bound => throw new HarnessException(
                HarnessExit.Refused,
                $"Another {ToolPackage.Id} process has held the anchor registries for {_bound!.Value.TotalSeconds:0} seconds, "
                + "so nothing was read or changed. Run the command again once it has finished."),
            _ => throw new OperationCanceledException(cancellationToken),
        };
    }

    /// <summary>Says where a wait for the lock stands, in the one line every wait is told in.</summary>
    private void Tell(WaitTold told) => _output?.Info(TellsAs, told.Line());
}
