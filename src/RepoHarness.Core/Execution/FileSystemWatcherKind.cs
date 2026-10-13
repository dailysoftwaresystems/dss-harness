namespace RepoHarness.Core.Execution;

/// <summary>
/// Watches through .NET's own watcher, one for each directory: a handle, a buffer and a thread apiece, which is why a
/// watch through it bounds how many directories it takes.
/// </summary>
/// <param name="watch">Makes the one watcher of a directory, started; the runtime's own unless a test says otherwise.</param>
internal sealed class FileSystemWatcherKind(Func<string, bool, IWatchSink, IDisposable> watch) : IWatchKind
{
    /// <summary>
    /// The largest buffer the operating system accepts. Events arrive faster than they can be read
    /// during a build, and a buffer that overflows loses them silently, which this exists to avoid.
    /// </summary>
    private const int BufferBytes = 64 * 1024;

    private readonly Func<string, bool, IWatchSink, IDisposable> _watch = watch;

    /// <summary>Watches through the runtime's own watcher.</summary>
    public FileSystemWatcherKind()
        : this(Watch)
    {
    }

    /// <inheritdoc/>
    public IDisposable Start(IReadOnlyList<(string Directory, bool Recursive)> targets, IWatchSink sink)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sink);

        var watchers = new List<IDisposable>(targets.Count);
        var started = false;

        try
        {
            foreach (var (directory, recursive) in targets)
            {
                watchers.Add(_watch(directory, recursive, sink));
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
    /// The runtime's watcher over <paramref name="directory"/>, started. It is told of a directory's own arrival, going
    /// and renaming as well as a file's, since the inputs below a directory that went are gone with it.
    /// </summary>
    private static FileSystemWatcher Watch(string directory, bool recursive, IWatchSink sink)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = recursive,
            InternalBufferSize = BufferBytes,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
        };

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

            watcher.Error += (_, e) => sink.Lost(e.GetException().Message);
            watcher.EnableRaisingEvents = true;

            return watcher;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
    }

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
