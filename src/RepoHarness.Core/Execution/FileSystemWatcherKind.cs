namespace RepoHarness.Core.Execution;

/// <summary>
/// Watches through .NET's own watcher, one for each directory: a handle, a buffer and a thread apiece, which is why a
/// watch through it bounds how many directories it takes (<see cref="MostDirectoriesWatched"/>). Windows and macOS
/// watch through it, and Linux where inotify cannot be called directly.
/// </summary>
public sealed class FileSystemWatcherKind : IWatchKind
{
    /// <summary>
    /// How many directories a watch through .NET's watcher takes at most: each watcher is a handle, a buffer and a
    /// thread. Past it, the top-level directories holding the most directories of inputs are watched all the way down
    /// instead, one watch each, and a tree whose inputs lie in more top-level directories than this - the root counted
    /// among them where it holds one itself - is watched whole: a watcher per directory would spend handles to no
    /// purpose. The bound is this kind's, and no watch's: a kind with none, as inotify's hub on Linux is, watches every
    /// directory of inputs for itself.
    /// </summary>
    public const int MostDirectoriesWatched = 64;

    /// <summary>
    /// The largest buffer the operating system accepts. Events arrive faster than they can be read
    /// during a build, and a buffer that overflows loses them silently, which this exists to avoid.
    /// </summary>
    private const int BufferBytes = 64 * 1024;

    private readonly Func<WatchTarget, IWatchSink, IDisposable> _watch;

    /// <summary>Watches through the runtime's own watcher.</summary>
    internal FileSystemWatcherKind()
        : this(Watch)
    {
    }

    /// <summary>Watches through what <paramref name="watch"/> makes.</summary>
    /// <param name="watch">Makes the one watcher of a directory, started; the runtime's own unless a test says otherwise.</param>
    internal FileSystemWatcherKind(Func<WatchTarget, IWatchSink, IDisposable> watch)
    {
        _watch = watch;
    }

    /// <inheritdoc/>
    public int? MostDirectories => MostDirectoriesWatched;

    /// <inheritdoc/>
    IDisposable IWatchKind.Start(IReadOnlyList<WatchTarget> targets, IWatchSink sink) => Start(targets, sink);

    /// <inheritdoc cref="IWatchKind.Start"/>
    internal IDisposable Start(IReadOnlyList<WatchTarget> targets, IWatchSink sink)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sink);

        var watchers = new List<IDisposable>(targets.Count);
        var started = false;

        try
        {
            foreach (var target in targets)
            {
                watchers.Add(_watch(target, sink));
            }

            started = true;
            return new Watching(watchers);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A machine that cannot watch this tree - one out of inotify instances, say - is told of before the work, in
            // the system's own words.
            throw InputsUnwatchedException.BySystem(ex);
        }
        finally
        {
            // Whatever stopped it: the watchers already made are let go before the failure leaves, so a watch that
            // could not start holds no handle, no buffer and no thread, and nobody is left to dispose of them.
            if (!started)
            {
                Release(watchers);
            }
        }
    }

    /// <summary>
    /// <paramref name="watcher"/>, its events handed on to <paramref name="sink"/>, started by <paramref name="start"/>:
    /// each entry made, removed or renamed - a directory's as well as a file's, since the inputs below a directory that
    /// went are gone with it - by name, under both names of a rename, and each written to as written.
    /// </summary>
    /// <remarks>
    /// An error the watcher raises while it starts refuses the start: .NET raises one on the starting thread, and returns,
    /// where the system will not watch the directory - on Linux, an inotify watch it could not add - so a start that
    /// handed it on as a watch lost would return as though it had started, the work would run, and the leg would be
    /// called unmeasured only once it was done, the watcher holding what it took all the while. One raised once the
    /// watcher has started loses the span, as it always did.
    /// </remarks>
    /// <param name="watcher">The watcher, not yet started.</param>
    /// <param name="sink">Told of what it hears.</param>
    /// <param name="start">Starts it: the runtime's own enables its events.</param>
    /// <exception cref="InputsUnwatchedException">It raised an error while it started: it is let go first.</exception>
    internal static FileSystemWatcher Started(FileSystemWatcher watcher, IWatchSink sink, Action<FileSystemWatcher> start)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(start);

        var gate = new Lock();
        var starting = true;
        Exception? refusal = null;

        try
        {
            watcher.Changed += (_, e) => sink.Written(e.FullPath);
            watcher.Created += (_, e) => sink.Named(e.FullPath);
            watcher.Deleted += (_, e) => sink.Named(e.FullPath);

            watcher.Renamed += (_, e) =>
            {
                // Both names, because a rename is a change to what left and to what arrived.
                sink.Named(e.OldFullPath);
                sink.Named(e.FullPath);
            };

            watcher.Error += (_, e) =>
            {
                lock (gate)
                {
                    if (starting)
                    {
                        refusal ??= e.GetException();
                        return;
                    }
                }

                sink.Lost(e.GetException().Message);
            };

            start(watcher);

            lock (gate)
            {
                starting = false;
            }

            return refusal is null ? watcher : throw InputsUnwatchedException.BySystem(refusal);
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The runtime's watcher over <paramref name="target"/>'s directory, started: for every entry in it, or, where only
    /// names are wanted, for the directories in it alone.
    /// </summary>
    private static FileSystemWatcher Watch(WatchTarget target, IWatchSink sink)
        => Started(
            new FileSystemWatcher(target.Directory)
            {
                IncludeSubdirectories = target.Recursive,
                InternalBufferSize = BufferBytes,
                NotifyFilter = target.NamesOnly
                    ? NotifyFilters.DirectoryName
                    : NotifyFilters.FileName
                        | NotifyFilters.DirectoryName
                        | NotifyFilters.LastWrite
                        | NotifyFilters.Size
                        | NotifyFilters.CreationTime,
            },
            sink,
            watcher => watcher.EnableRaisingEvents = true);

    private static void Release(IEnumerable<IDisposable> watchers)
    {
        foreach (var watcher in watchers)
        {
            try
            {
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Already gone, or the platform tore the watch down first; either way nothing is
                // lost, because what it heard was handed on as it was heard.
            }
        }
    }

    /// <summary>The watchers of one start, let go together.</summary>
    private sealed class Watching(IReadOnlyList<IDisposable> watchers) : IDisposable
    {
        public void Dispose() => Release(watchers);
    }
}
