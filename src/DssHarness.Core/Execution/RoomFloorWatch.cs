using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;

namespace DssHarness.Core.Execution;

/// <summary>
/// Watches the room a heavy leg's build leaves on each filesystem it fills, while it builds, and stops the build once one
/// has less than its floor (<see cref="RoomFloor"/>): a consumer's leg, placed where nothing said what its build needed,
/// filled a 47 GiB disk to 79 MiB under two other legs, and died there.
/// </summary>
/// <remarks>
/// <para>
/// Each filesystem is read as the watch starts, and again every <see cref="Every"/>: the room is the filesystem's own
/// count, so a reading costs nothing a build would notice. A room that cannot be read stops nothing - it is said once,
/// with why - as a leg is admitted without a room it could not read. Where its machine says so, a filesystem found under
/// the floor first has room made on it from other trees' idle builds (<see cref="RoomFloor.MakeRoom"/>), said, and is read
/// again: the build is stopped only where it is still under, and not while another leg makes room there, after which it
/// is read again.
/// </para>
/// <para>
/// Only those two readings make room. Judging a failure (<see cref="Now"/>) never does: it asks whether the build stood
/// under its floor at any reading since the work that failed began - room made since or not - and else reads the room as
/// it is, so the one question meant to tell a failure for want of room is never answered by first removing the want.
/// </para>
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
    private string? _madeSaid;
    private bool _disposed;

    // How many times a reading has found a filesystem under the floor, what the latest such reading would say of a failure
    // beside it, and whether room is being made for it now - outside the reading's lock, which a judgement of a failure
    // takes, so it waits on no removal.
    private long _shortages;
    private string? _short;
    private bool _making;

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
    /// Where the watch's readings stand now: taken as a piece of the build's work begins - its record, a phase - and given to
    /// <see cref="Now"/> once that work fails, so a reading that found a filesystem under the floor while it ran says why it
    /// failed, though room was made there since. Room being made now is for a shortage the work began beside, and counts.
    /// </summary>
    public long Mark
    {
        get
        {
            lock (_reading)
            {
                return _making ? _shortages - 1 : _shortages;
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
        if (watch.Under(makeRoom: true))
        {
            return watch;
        }

        watch._watching = watch.WatchAsync(wait);

        return watch;
    }

    /// <summary>
    /// Why the work of the build that began at <paramref name="since"/> (<see cref="Mark"/>) and has failed is to be read as
    /// stopped for room: a reading since found a filesystem it fills under its floor - room made there since or not - or one
    /// is under it now, read without making any. A build that failed between two readings - a disk filled faster than the
    /// next one came, or one that stopped it as the build ended - failed for want of room, which says nothing about its
    /// code. <see langword="null"/> where every filesystem it fills had room throughout and has it now, and, without reading
    /// any, once the watch has ended: its build has gone past what fills a disk.
    /// </summary>
    /// <param name="since">The watch's <see cref="Mark"/> as the work that failed began.</param>
    public string? Now(long since)
    {
        lock (_reading)
        {
            if (_why is not null)
            {
                return _why;
            }

            if (_disposed)
            {
                return null;
            }

            if (_shortages > since)
            {
                return _short;
            }
        }

        return Under(makeRoom: false) ? Why : null;
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
            while (!Under(makeRoom: true));
        }
        catch (OperationCanceledException) when (_done.IsCancellationRequested)
        {
            // The build ended, or the run was stopped: there is nothing left to watch.
        }
    }

    /// <summary>
    /// Reads each filesystem the build fills, and stops the build where one has less room than its floor - first making
    /// room there, where <paramref name="makeRoom"/> and its machine say so - and whether it is stopped. The watch's own
    /// readings make room, one at a time, the one taken as it starts among them; a judgement of a failure never does.
    /// Neither reads once the watch has ended.
    /// </summary>
    private bool Under(bool makeRoom)
    {
        foreach (var (path, where) in _filled)
        {
            DiskSpace space;

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

                var (read, unmeasured) = DiskSpace.Measure(_fileSystem, path);

                if (read is null)
                {
                    if (_unread.Add(path))
                    {
                        _say($"the room on '{path}'{where} could not be read, so the build is not stopped for want of it: {unmeasured}");
                    }

                    continue;
                }

                if (read.FreeBytes >= _floor.Bytes)
                {
                    continue;
                }

                space = read;
                _shortages++;
                _short = $"stopped: it failed while '{space.Filesystem}'{where} stood under the {DiskSpace.Size(_floor.Bytes)} admission.minFreeGiB "
                    + $"keeps free for every leg there, at {DiskSpace.Size(space.FreeBytes)} free as it was read, so the failure says nothing of its code; "
                    + $"what it built is left for '{ToolPackage.Command} clean --legs {_floor.Leg}'";

                if (!makeRoom || _floor.MakeRoom is null)
                {
                    return Stop(space, where);
                }

                _making = true;
            }

            // Made outside the lock: a removal of gigabytes takes as long as it takes, and a failure judged meanwhile is
            // judged by the shortage counted above.
            MadeRoom? made;
            DiskSpace? after;

            try
            {
                made = _floor.MakeRoom(space, _floor.Bytes - space.FreeBytes, _floor.Bytes - space.FreeBytes + _floor.Need);
                after = DiskSpace.Measure(_fileSystem, path).Space;
            }
            catch
            {
                lock (_reading)
                {
                    _making = false;
                }

                throw;
            }

            lock (_reading)
            {
                _making = false;

                // Said whenever it removed something, and otherwise only where it says something new: why nothing could be,
                // or the leg making room there already.
                if (made is not null && (made.Bytes > 0 || made.Said != _madeSaid))
                {
                    _say(made.Said);
                    _madeSaid = made.Said;
                }

                if (_why is not null)
                {
                    return true;
                }

                if (_disposed)
                {
                    return false;
                }

                // Not stopped while another leg makes room there: read again at the next reading, once it has.
                if (made is { Behind: true } || after is { } room && room.FreeBytes >= _floor.Bytes)
                {
                    continue;
                }

                return Stop(after ?? space, where);
            }
        }

        return false;
    }

    /// <summary>
    /// Stops the build for <paramref name="space"/> under the floor, as its line says it; called holding the reading's lock.
    /// A floor above the whole filesystem's size is said so, for no build there could ever leave it free.
    /// </summary>
    private bool Stop(DiskSpace space, string where)
    {
        _why = $"stopped with {DiskSpace.Size(space.FreeBytes)} free on '{space.Filesystem}'{where}, under the {DiskSpace.Size(_floor.Bytes)} "
            + "admission.minFreeGiB keeps free for every leg there"
            + (_floor.Bytes > space.TotalBytes
                ? $", which is more than the whole of '{space.Filesystem}' holds, {DiskSpace.Size(space.TotalBytes)}: lower admission.minFreeGiB, or no build there runs"
                : string.Empty)
            + $"; what it built is left for '{ToolPackage.Command} clean --legs {_floor.Leg}'";
        _stopping.Cancel();

        return true;
    }
}
