using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;

namespace RepoHarness.Core.Execution;

/// <summary>
/// Watches the room a heavy leg's build leaves on each filesystem it fills, while it builds, and stops the build once one
/// has less than its floor (<see cref="RoomFloor"/>): a consumer's leg, placed where nothing said what its build needed,
/// filled a 47 GiB disk to 79 MiB under two other legs, and died there.
/// </summary>
/// <remarks>
/// Each filesystem is read as the watch starts, and again every <see cref="Every"/>: the room is the filesystem's own
/// count, so a reading costs nothing a build would notice. A room that cannot be read stops nothing - it is said once,
/// with why - as a leg is admitted without a room it could not read.
/// </remarks>
public sealed class RoomFloorWatch : IAsyncDisposable
{
    /// <summary>How often the room is read again while the build runs.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _done;
    private readonly HashSet<string> _unread = new(StringComparer.Ordinal);
    private Task _watching = Task.CompletedTask;
    private bool _disposed;

    private RoomFloorWatch(CancellationToken cancellationToken)
    {
        _done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    /// <summary>Cancelled once a filesystem the build fills has less room than its floor; <see cref="Why"/> then says so.</summary>
    public CancellationToken Stopping => _stopping.Token;

    /// <summary>Why the build was stopped, as its line says it; <see langword="null"/> while it was not.</summary>
    public string? Why { get; private set; }

    /// <summary>
    /// Starts watching the filesystems <paramref name="buildDirectory"/>'s build fills, having read each once already:
    /// a build that starts under its floor is stopped before it writes anything.
    /// </summary>
    /// <param name="fileSystem">Reads the room.</param>
    /// <param name="floor">The least room the build leaves free, and the filesystems it fills beside its own.</param>
    /// <param name="leg">The leg, as the command that cleans its build names it.</param>
    /// <param name="buildDirectory">Where it builds.</param>
    /// <param name="wait">Waits between readings.</param>
    /// <param name="say">Says what the build is not stopped for, and why.</param>
    /// <param name="cancellationToken">Ends the watch, with the build.</param>
    public static RoomFloorWatch Start(
        IFileSystem fileSystem,
        RoomFloor floor,
        string leg,
        string buildDirectory,
        Func<TimeSpan, CancellationToken, Task> wait,
        Action<string> say,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(say);

        var watch = new RoomFloorWatch(cancellationToken);

        if (floor.Unwatched is { } unwatched)
        {
            say(unwatched);
        }

        IReadOnlyList<(string Path, string Where)> filled = [(buildDirectory, string.Empty), .. floor.Also];

        // Read once before anything is built, here and not in the loop's first turn, so a build that starts under its
        // floor is known to be stopped before its first phase is.
        if (watch.Under(fileSystem, floor, leg, filled, say))
        {
            return watch;
        }

        watch._watching = watch.WatchAsync(fileSystem, floor, leg, filled, wait, say);

        return watch;
    }

    /// <summary>Stops watching; the build it watched has ended, or gone past what fills a disk.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _done.CancelAsync().ConfigureAwait(false);
        await _watching.ConfigureAwait(false);

        _done.Dispose();
        _stopping.Dispose();
    }

    private async Task WatchAsync(
        IFileSystem fileSystem,
        RoomFloor floor,
        string leg,
        IReadOnlyList<(string Path, string Where)> filled,
        Func<TimeSpan, CancellationToken, Task> wait,
        Action<string> say)
    {
        try
        {
            do
            {
                await wait(Every, _done.Token).ConfigureAwait(false);
            }
            while (!Under(fileSystem, floor, leg, filled, say));
        }
        catch (OperationCanceledException) when (_done.IsCancellationRequested)
        {
            // The build ended, or the run was stopped: there is nothing left to watch.
        }
    }

    /// <summary>
    /// Reads each filesystem the build fills, and stops the build where one has less room than its floor; whether it did.
    /// </summary>
    private bool Under(IFileSystem fileSystem, RoomFloor floor, string leg, IReadOnlyList<(string Path, string Where)> filled, Action<string> say)
    {
        foreach (var (path, where) in filled)
        {
            var (space, unmeasured) = DiskSpace.Measure(fileSystem, path);

            if (space is null)
            {
                if (_unread.Add(path))
                {
                    say($"the room on '{path}'{where} could not be read, so the build is not stopped for want of it: {unmeasured}");
                }

                continue;
            }

            if (space.FreeBytes < floor.Bytes)
            {
                Why = $"stopped with {DiskSpace.Size(space.FreeBytes)} free on '{space.Filesystem}'{where}, under the {DiskSpace.Size(floor.Bytes)} "
                    + $"admission.minFreeGiB keeps free for every leg there; what it built is left for '{ToolPackage.Command} clean --legs {leg}'";
                _stopping.Cancel();

                return true;
            }
        }

        return false;
    }
}
