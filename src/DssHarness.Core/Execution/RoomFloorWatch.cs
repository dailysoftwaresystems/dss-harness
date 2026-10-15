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
    private readonly Lock _reading = new();
    private readonly HashSet<string> _unread = new(StringComparer.Ordinal);
    private readonly IFileSystem _fileSystem;
    private readonly RoomFloor _floor;
    private readonly IReadOnlyList<(string Path, string Where)> _filled;
    private readonly Action<string> _say;
    private Task _watching = Task.CompletedTask;
    private string? _why;
    private bool _disposed;

    private RoomFloorWatch(IFileSystem fileSystem, RoomFloor floor, string buildDirectory, Action<string> say, CancellationToken cancellationToken)
    {
        _fileSystem = fileSystem;
        _floor = floor;
        _filled = [(buildDirectory, string.Empty), .. floor.Also];
        _say = say;
        _done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Stopping = _stopping.Token;
    }

    /// <summary>
    /// Cancelled once a filesystem the build fills has less room than its floor; <see cref="Why"/> then says so. Read as
    /// well once the watch has ended, when nothing cancels it any more.
    /// </summary>
    public CancellationToken Stopping { get; }

    /// <summary>Why the build was stopped, as its line says it; <see langword="null"/> while it was not.</summary>
    public string? Why
    {
        get
        {
            lock (_reading)
            {
                return _why;
            }
        }
    }

    /// <summary>
    /// Starts watching the filesystems <paramref name="buildDirectory"/>'s build fills, having read each once already:
    /// a build that starts under its floor is stopped before it writes anything.
    /// </summary>
    /// <param name="fileSystem">Reads the room.</param>
    /// <param name="floor">The least room the build leaves free, the filesystems it fills beside its own, and whose it is.</param>
    /// <param name="buildDirectory">Where it builds.</param>
    /// <param name="wait">Waits between readings.</param>
    /// <param name="say">Says what the build is not stopped for, and why.</param>
    /// <param name="cancellationToken">Ends the watch, with the build.</param>
    public static RoomFloorWatch Start(
        IFileSystem fileSystem,
        RoomFloor floor,
        string buildDirectory,
        Func<TimeSpan, CancellationToken, Task> wait,
        Action<string> say,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(floor);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(say);

        var watch = new RoomFloorWatch(fileSystem, floor, buildDirectory, say, cancellationToken);

        if (floor.Unwatched is { } unwatched)
        {
            say(unwatched);
        }

        // Read once before anything is built, here and not in the loop's first turn, so a build that starts under its
        // floor is known to be stopped before its first phase is.
        if (watch.Under())
        {
            return watch;
        }

        watch._watching = watch.WatchAsync(wait);

        return watch;
    }

    /// <summary>
    /// Why the build is to be stopped for room, read again now where no reading has said so yet: a build that failed
    /// between two readings - a disk filled faster than the next one came, or one that stopped it as the build ended - failed
    /// for want of room, which says nothing about its code. <see langword="null"/> where every filesystem it fills has room,
    /// and, without reading any, once the watch has ended: its build has gone past what fills a disk.
    /// </summary>
    public string? Now()
    {
        Under();
        return Why;
    }

    /// <summary>
    /// Stops watching; the build it watched has ended, or gone past what fills a disk. From here no room is read and nothing
    /// is stopped, whoever asks; ended twice, it ends once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_reading)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await _done.CancelAsync().ConfigureAwait(false);
        await _watching.ConfigureAwait(false);

        _done.Dispose();
        _stopping.Dispose();
    }

    private async Task WatchAsync(Func<TimeSpan, CancellationToken, Task> wait)
    {
        try
        {
            do
            {
                await wait(Every, _done.Token).ConfigureAwait(false);
            }
            while (!Under());
        }
        catch (OperationCanceledException) when (_done.IsCancellationRequested)
        {
            // The build ended, or the run was stopped: there is nothing left to watch.
        }
    }

    /// <summary>
    /// Reads each filesystem the build fills, and stops the build where one has less room than its floor; whether it is
    /// stopped. One reading at a time: the watch's own and one a build that ended asks for meet here, and neither reads once
    /// the watch has ended.
    /// </summary>
    private bool Under()
    {
        lock (_reading)
        {
            if (_why is not null)
            {
                return true;
            }

            if (_disposed)
            {
                return false;
            }

            foreach (var (path, where) in _filled)
            {
                var (space, unmeasured) = DiskSpace.Measure(_fileSystem, path);

                if (space is null)
                {
                    if (_unread.Add(path))
                    {
                        _say($"the room on '{path}'{where} could not be read, so the build is not stopped for want of it: {unmeasured}");
                    }

                    continue;
                }

                if (space.FreeBytes < _floor.Bytes)
                {
                    _why = $"stopped with {DiskSpace.Size(space.FreeBytes)} free on '{space.Filesystem}'{where}, under the {DiskSpace.Size(_floor.Bytes)} "
                        + $"admission.minFreeGiB keeps free for every leg there; what it built is left for '{ToolPackage.Command} clean --legs {_floor.Leg}'";
                    _stopping.Cancel();

                    return true;
                }
            }

            return false;
        }
    }
}
