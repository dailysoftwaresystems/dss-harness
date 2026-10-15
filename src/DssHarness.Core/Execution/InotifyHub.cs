using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

/// <summary>
/// One inotify instance for a whole process, which every span of work on Linux watches through: made at the first watch
/// and kept until the process ends, read by one background thread. .NET's own watcher takes an instance for each
/// directory it watches, and three legs at once took 120 of a user's 128.
/// </summary>
/// <remarks>
/// The kernel gives one descriptor for a directory however often, and under whatever name, it is added; so the hub
/// keeps, for each descriptor, the spans watching it, each with the path it named, and removes the descriptor once
/// the last of them lets go. What the instance tells is handed to each span outside the hub's lock, and never to one
/// let go of. The instance is made close-on-exec, so no program a leg starts holds it. It goes when the floor this
/// tool runs on is a .NET whose own watcher shares one instance; the seam it stands behind stays.
/// </remarks>
internal sealed class InotifyHub
{
    /// <summary>
    /// How much one read takes: room for a record whatever its name, many times over. A queue drained this way keeps up
    /// with what a build writes beside the inputs, which shallow watches hear little of.
    /// </summary>
    private const int ReadBytes = 64 * 1024;

    private readonly IInotifyCalls _calls;
    private readonly Func<InotifyUse> _use;
    private readonly Lock _gate = new();
    private readonly Dictionary<int, List<(Span Span, string Directory, bool NamesOnly)>> _watching = [];
    private int _instance = -1;

    /// <summary>A hub over <paramref name="calls"/>, which makes no instance until something is watched.</summary>
    /// <param name="calls">The calls the instance is made, watched and read through.</param>
    /// <param name="use">What this user's instances and watches are, and who holds them: read when a limit is met.</param>
    public InotifyHub(IInotifyCalls calls, Func<InotifyUse> use)
    {
        _calls = calls;
        _use = use;
    }

    /// <summary>The instance's descriptor, or -1 while there is none.</summary>
    internal int Instance
    {
        get
        {
            lock (_gate)
            {
                return _instance;
            }
        }
    }

    /// <summary>
    /// Watches each of <paramref name="targets"/>' directories for its own entries - or, where only names are wanted, for
    /// the directories in it alone - and the directory itself going or moving, until what this returns is disposed,
    /// telling <paramref name="sink"/>.
    /// </summary>
    /// <param name="targets">Each directory, by its full path, and whether only the names of the directories in it are wanted: none all the way down.</param>
    /// <param name="sink">Told of what happens in them.</param>
    /// <returns>The span's watches: disposed, it is told nothing more, and each descriptor no span still watches is removed.</returns>
    /// <exception cref="InputsUnwatchedException">
    /// The instance could not be made, or a directory not watched: every watch this call added is removed first. This
    /// user's instances all taken, or its watches run out, are said with the limit and who holds them.
    /// </exception>
    public IDisposable Watch(IReadOnlyList<WatchTarget> targets, IWatchSink sink)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(sink);

        var span = new Span(this, sink);
        var refusal = Watched(span, targets);

        if (refusal is not { } refused)
        {
            return span;
        }

        // Outside the lock: who holds this user's instances and watches is read from every process's entries.
        throw refused switch
        {
            (InotifyErrors.InstancesTaken, null) => InputsUnwatchedException.InstancesTaken(_use()),
            (InotifyErrors.WatchesRanOut, not null) => InputsUnwatchedException.WatchesRanOut(_use(), targets.Count),
            (var error, null) => InputsUnwatchedException.BySystem($"inotify_init1: {InotifyErrors.Said(error)}"),
            (var error, { } directory) => InputsUnwatchedException.BySystem($"'{ReportText.Printable(directory)}': {InotifyErrors.Said(error)}"),
        };
    }

    /// <summary>
    /// Adds <paramref name="targets"/> to the instance for <paramref name="span"/>, making the instance first where
    /// there is none; or, where a call fails, removes what it added and answers the error and the directory refused -
    /// none where it was the instance that could not be made.
    /// </summary>
    private (int Error, string? Directory)? Watched(Span span, IReadOnlyList<WatchTarget> targets)
    {
        lock (_gate)
        {
            if (_instance < 0)
            {
                var instance = _calls.Init(InotifyBits.CloseOnExec, out var error);

                if (instance < 0)
                {
                    return (error, null);
                }

                _instance = instance;
                StartReading(instance);
            }

            foreach (var target in targets)
            {
                // One mask for every directory, whatever is wanted of it: the kernel keeps one for a directory however
                // many spans name it. What a span wants only names of is told it alone.
                var descriptor = _calls.AddWatch(_instance, target.Directory, InotifyBits.Watched, out var error);

                if (descriptor < 0)
                {
                    LetGoLocked(span);
                    return (error, target.Directory);
                }

                if (!_watching.TryGetValue(descriptor, out var spans))
                {
                    _watching[descriptor] = spans = [];
                }

                spans.Add((span, target.Directory, target.Depth == WatchDepth.NamesOnly));
                span.Descriptors.Add(descriptor);
            }

            return null;
        }
    }

    /// <summary>Lets go of <paramref name="span"/>'s watches: each descriptor no other span watches is removed.</summary>
    private void LetGo(Span span)
    {
        lock (_gate)
        {
            LetGoLocked(span);
        }
    }

    /// <summary><see cref="LetGo"/>, under the hub's lock.</summary>
    private void LetGoLocked(Span span)
    {
        foreach (var descriptor in span.Descriptors)
        {
            // Gone already where the hub let go of it - its directory went - or where the instance broke.
            if (!_watching.TryGetValue(descriptor, out var spans))
            {
                continue;
            }

            spans.RemoveAll(watching => watching.Span == span);

            if (spans.Count == 0)
            {
                _watching.Remove(descriptor);
                _calls.RemoveWatch(_instance, descriptor, out _);
            }
        }

        span.Descriptors.Clear();
    }

    private void StartReading(int instance)
    {
        // A background thread, blocked in read for as long as the process lives: nothing need stop it.
        var reading = new Thread(() => Read(instance)) { IsBackground = true, Name = "inotify" };
        reading.Start();
    }

    /// <summary>Reads the instance until a read fails, handing each event over as it comes.</summary>
    private void Read(int instance)
    {
        var buffer = new byte[ReadBytes];

        while (true)
        {
            var read = _calls.Read(instance, buffer, out var error);

            if (read < 0 && error == InotifyErrors.Interrupted)
            {
                continue;
            }

            if (read <= 0)
            {
                Broken(instance, read < 0
                    ? $"reading this process's inotify instance failed: {InotifyErrors.Said(error)}"
                    : "this process's inotify instance gave nothing to read");
                return;
            }

            var records = InotifyEvents.Parse(buffer.AsSpan(0, read));
            var told = new List<(Span Span, Action<IWatchSink> Tell)>();

            lock (_gate)
            {
                foreach (var heard in records.Events)
                {
                    Told(heard, told);
                }

                if (records.CutShort)
                {
                    TellEverySpan("a record inotify gave ran past what was read, so what it said was lost", told);
                }
            }

            foreach (var (span, tell) in told)
            {
                span.Tell(tell);
            }
        }
    }

    /// <summary>What <paramref name="heard"/> tells, and whom, added to <paramref name="told"/>. Under the hub's lock.</summary>
    private void Told(InotifyEvent heard, List<(Span Span, Action<IWatchSink> Tell)> told)
    {
        if (heard.Says(InotifyBits.QueueOverflow))
        {
            TellEverySpan("this process's inotify queue overflowed (fs.inotify.max_queued_events), so events were lost", told);
            return;
        }

        // A descriptor the hub let go of, or never had: nothing watches it, and nothing was missed by a span that does.
        if (!_watching.TryGetValue(heard.Descriptor, out var spans))
        {
            return;
        }

        // The directory itself went, moved, or its watch went with its file system: it took every entry with it, and
        // the descriptor watches nothing a span named any more.
        if (heard.Says(InotifyBits.DeleteSelf | InotifyBits.MoveSelf | InotifyBits.Ignored))
        {
            foreach (var (span, directory, _) in spans)
            {
                told.Add((span, sink => sink.Named(directory)));
            }

            _watching.Remove(heard.Descriptor);

            // Still the instance's where the directory only moved; the kernel lets go of it itself where it went.
            if (heard.Says(InotifyBits.MoveSelf))
            {
                _calls.RemoveWatch(_instance, heard.Descriptor, out _);
            }

            return;
        }

        var named = heard.Says(InotifyBits.Create | InotifyBits.Delete | InotifyBits.MovedFrom | InotifyBits.MovedTo);
        var written = heard.Says(InotifyBits.Modify | InotifyBits.Attrib);

        foreach (var (span, directory, namesOnly) in spans)
        {
            // Of a directory above inputs that holds none, only the directories in it are wanted.
            if (namesOnly && !heard.Says(InotifyBits.IsDirectory))
            {
                continue;
            }

            if (heard.NameUnreadable)
            {
                told.Add((span, sink => sink.Lost($"inotify named an entry of '{ReportText.Printable(directory)}' in bytes that are no UTF-8, so what it said of it is lost")));
                continue;
            }

            var path = heard.Name.Length == 0 ? directory : Path.Combine(directory, heard.Name);

            if (named)
            {
                told.Add((span, sink => sink.Named(path)));
            }
            else if (written)
            {
                told.Add((span, sink => sink.Written(path)));
            }
        }
    }

    /// <summary>Tells every span <paramref name="why"/> it was lost. Under the hub's lock.</summary>
    private void TellEverySpan(string why, List<(Span Span, Action<IWatchSink> Tell)> told)
    {
        foreach (var span in _watching.Values.SelectMany(spans => spans).Select(watching => watching.Span).Distinct())
        {
            told.Add((span, sink => sink.Lost(why)));
        }
    }

    /// <summary>
    /// The instance can no longer be read: every span watching through it is lost, and it is let go, so the next watch
    /// makes another.
    /// </summary>
    private void Broken(int instance, string why)
    {
        var told = new List<(Span Span, Action<IWatchSink> Tell)>();

        lock (_gate)
        {
            TellEverySpan(why, told);
            _watching.Clear();
            _instance = -1;
            _calls.Close(instance);
        }

        foreach (var (span, tell) in told)
        {
            span.Tell(tell);
        }
    }

    /// <summary>One watch's directories, and whom to tell of them until it is let go.</summary>
    private sealed class Span(InotifyHub hub, IWatchSink sink) : IDisposable
    {
        private readonly Lock _telling = new();
        private bool _disposed;

        /// <summary>The descriptors it added, in order. Under the hub's lock.</summary>
        public List<int> Descriptors { get; } = [];

        /// <summary>Tells the sink, unless the span was let go: once that has returned, nothing is told.</summary>
        public void Tell(Action<IWatchSink> tell)
        {
            lock (_telling)
            {
                if (!_disposed)
                {
                    tell(sink);
                }
            }
        }

        public void Dispose()
        {
            lock (_telling)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            hub.LetGo(this);
        }
    }
}
