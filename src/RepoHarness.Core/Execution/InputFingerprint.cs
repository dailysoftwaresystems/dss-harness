using System.Security.Cryptography;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

/// <summary>One input file as it stood at one moment.</summary>
/// <param name="Path">The file, relative to the tree and always spelled with forward slashes.</param>
/// <param name="Length">Its size in bytes, or <see cref="InputFingerprint.AbsentLength"/> when it was not there.</param>
/// <param name="Content">A hash of its bytes, or <see cref="InputFingerprint.AbsentContent"/> when it was not there.</param>
/// <remarks>
/// Content and size decide whether it changed, never a timestamp. Change is detected by equality,
/// which a clock cannot distort because both readings carry the same distortion; one host's wall
/// clock steps forward by about 25 seconds every few seconds, and those steps reach file
/// modification times. The times it was last written and created are read with it, and compared
/// for equality alone: a watch that can be told of a write made before it began asks them whether a
/// file still stands as the work found it.
/// </remarks>
public sealed record FileFingerprint(string Path, long Length, string Content)
{
    /// <summary>
    /// When it was last written, as read with it; <see langword="null"/> where it was not there or
    /// the time could not be read.
    /// </summary>
    public DateTime? Written { get; init; }

    /// <summary>
    /// When it was created, as read with it; <see langword="null"/> where it was not there or the
    /// time could not be read.
    /// </summary>
    public DateTime? Created { get; init; }

    /// <summary>What the file holds, as everything that compares files compares it.</summary>
    public FileContent Identity => new(Length, Content);
}

/// <summary>An input file that could not be fingerprinted, and why.</summary>
/// <param name="Path">The file, relative to the tree.</param>
/// <param name="Reason">What stopped the reading.</param>
public sealed record UnreadableInput(string Path, string Reason);

/// <summary>
/// Every input as it stood at one moment, together with whatever could not be read. The two are
/// kept apart so that an unreadable snapshot can never be compared as though it were complete.
/// </summary>
/// <param name="Files">Each input's content and size.</param>
/// <param name="Unreadable">Inputs whose bytes could not be read at all.</param>
public sealed record InputSnapshot(IReadOnlyList<FileFingerprint> Files, IReadOnlyList<UnreadableInput> Unreadable)
{
    /// <summary>Whether every declared input was measured. A snapshot that was not is never clean.</summary>
    public bool Complete => Unreadable.Count == 0;
}

/// <summary>What comparing two snapshots established.</summary>
public enum InputChange
{
    /// <summary>Every input held still for the whole phase.</summary>
    Unchanged,

    /// <summary>At least one input changed while the phase ran.</summary>
    Moved,

    /// <summary>Whether they held still could not be established.</summary>
    Unmeasured,
}

/// <summary>What the before and after snapshots, and the watch between them, said together.</summary>
/// <param name="Change">Whether the inputs held still, moved, or could not be measured.</param>
/// <param name="Changed">The inputs that moved, or the ones that could not be measured.</param>
/// <param name="Detail">The sentence the ledger shows.</param>
public sealed record InputComparison(InputChange Change, IReadOnlyList<string> Changed, string Detail)
{
    /// <summary>
    /// The verdict this forces on the leg, or <see langword="null"/> when the inputs held still.
    /// A leg whose inputs moved is not reported as failed even if its tests failed, because what
    /// failed was a tree that never existed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A change this build does not know how to weigh. Raised rather than absorbed by a default
    /// arm: this is the one type whose whole purpose is that an unestablished measurement never
    /// reads as clean, and a silent <see langword="null"/> for a case added later is exactly that.
    /// </exception>
    public ReachedVerdict? Verdict() => Change switch
    {
        InputChange.Unchanged => null,
        InputChange.Moved => ReachedVerdict.Of(LegVerdict.InputsMoved, Detail),
        InputChange.Unmeasured => ReachedVerdict.Of(LegVerdict.Unmeasured, Detail),
        _ => throw new ArgumentOutOfRangeException(nameof(Change), Change, "This build cannot weigh that input change."),
    };
}

/// <summary>
/// Fingerprints the files a leg's tests read, before they start and after they end, and watches
/// them while they run.
/// </summary>
/// <remarks>
/// Edit a file mid-run and some tests see the old one and some the new, and the report describes a
/// tree that never existed. This was measured: eight failures, all passing seconds later on the
/// unchanged tree, because a configuration file was rewritten while the suite ran. The watch exists
/// because two snapshots alone cannot see an edit that was reverted before the suite finished.
/// </remarks>
public sealed class InputFingerprint
{
    /// <summary>The size recorded for an input that was not there.</summary>
    public const long AbsentLength = -1;

    /// <summary>
    /// The content recorded for an input that was not there. Absence is a measurement, and it
    /// compares equal to absence: a file missing before and after did not move. It is unreadability,
    /// not absence, that leaves the question open.
    /// </summary>
    public const string AbsentContent = "absent";

    private readonly IFileSystem _fileSystem;
    private readonly IHostPlatform _platform;
    private readonly IWatchKind _watchKind;

    /// <summary>Fingerprints and watches as this machine does.</summary>
    /// <param name="fileSystem">Reads the files.</param>
    /// <param name="platform">The machine: how its paths compare, and what its watches can be told of.</param>
    public InputFingerprint(IFileSystem fileSystem, IHostPlatform platform)
        : this(fileSystem, platform, new FileSystemWatcherKind())
    {
    }

    /// <summary>Fingerprints as this machine does, and watches through <paramref name="watchKind"/>.</summary>
    /// <param name="fileSystem">Reads the files.</param>
    /// <param name="platform">The machine: how its paths compare, and what its watches can be told of.</param>
    /// <param name="watchKind">What watches the directories a watch names.</param>
    internal InputFingerprint(IFileSystem fileSystem, IHostPlatform platform, IWatchKind watchKind)
    {
        _fileSystem = fileSystem;
        _platform = platform;
        _watchKind = watchKind;
    }

    /// <summary>
    /// Fingerprints <paramref name="inputs"/> under <paramref name="root"/> by content and size, on
    /// the host that runs them.
    /// </summary>
    /// <param name="root">The tree the paths are relative to.</param>
    /// <param name="inputs">The inputs, relative to the tree; the caller resolves whatever globbed them.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<InputSnapshot> TakeAsync(
        string root,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(inputs);

        var files = new List<FileFingerprint>();
        var unreadable = new List<UnreadableInput>();

        foreach (var input in inputs.Distinct(Comparer).OrderBy(input => input, Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Normalize(input);
            var full = Path.Combine(root, input);

            if (!_fileSystem.FileExists(full))
            {
                files.Add(new FileFingerprint(relative, AbsentLength, AbsentContent));
                continue;
            }

            try
            {
                files.Add(await FingerprintAsync(relative, full, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Recorded, never passed over. An input that could not be read is what makes the
                // whole snapshot unusable, and a snapshot short of one file would otherwise compare
                // equal to another snapshot short of the same file.
                unreadable.Add(new UnreadableInput(relative, ex.Message));
            }
        }

        return new InputSnapshot(files, unreadable);
    }

    /// <summary>
    /// Watches <paramref name="inputs"/> under <paramref name="root"/> for the life of the returned
    /// object, so that an edit made and undone while the suite ran is still caught.
    /// </summary>
    /// <param name="root">The tree the paths are relative to.</param>
    /// <param name="inputs">The inputs, relative to the tree.</param>
    /// <param name="found">The inputs as the work found them: the snapshot the work is compared against.</param>
    /// <remarks>
    /// A watch on macOS can be told of a write made before it began, and a file it is told of there
    /// counts only where it no longer stands as <paramref name="found"/> read it: see
    /// <see cref="InputWatch.ReportsEarlierWrites"/>.
    /// </remarks>
    /// <exception cref="InputsUnwatchedException">The watch could not start: it holds nothing, and the work it was for is not to begin.</exception>
    public InputWatch Watch(string root, IReadOnlyList<string> inputs, InputSnapshot found) => Watch(root, inputs, found, heardElsewhere: null);

    /// <summary>
    /// Watches <paramref name="inputs"/> as <see cref="Watch(string, IReadOnlyList{string}, InputSnapshot)"/> does, telling
    /// <paramref name="heardElsewhere"/> of each path the watch hears of that no input is.
    /// </summary>
    /// <param name="root">The tree the paths are relative to.</param>
    /// <param name="inputs">The inputs, relative to the tree.</param>
    /// <param name="found">The inputs as the work found them.</param>
    /// <param name="heardElsewhere">Told of each path no input is, relative to the tree, as the watch hears of it.</param>
    /// <exception cref="InputsUnwatchedException">The watch could not start.</exception>
    internal InputWatch Watch(string root, IReadOnlyList<string> inputs, InputSnapshot found, Action<string>? heardElsewhere)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(found);

        return new InputWatch(
            root,
            inputs.Select(Normalize),
            Comparer,
            _watchKind,
            reportsEarlierWrites: _platform.Current == PlatformId.MacOs,
            found.Files,
            heardElsewhere);
    }

    /// <summary>
    /// What <paramref name="before"/>, <paramref name="after"/> and <paramref name="watch"/> say
    /// together. An incomplete snapshot, or a watch that lost events or was itself lost while the
    /// work ran, yields <c>unmeasured</c>: there is no escape hatch, because a command that rewrites
    /// its own inputs is a build step, not a test. A watch that could not start never reaches here:
    /// its work was not begun (<see cref="InputsUnwatchedException"/>).
    /// </summary>
    /// <param name="before">The snapshot taken before the phase.</param>
    /// <param name="after">The snapshot taken after it.</param>
    /// <param name="watch">The watch kept while it ran, when one was kept.</param>
    public static InputComparison Compare(InputSnapshot before, InputSnapshot after, InputWatch? watch = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var unmeasured = before.Unreadable
            .Concat(after.Unreadable)
            .Select(entry => entry.Path)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        if (unmeasured.Count > 0)
        {
            return new InputComparison(
                InputChange.Unmeasured,
                unmeasured,
                $"{Describe(unmeasured.Count, "input")} could not be read: {ReportText.Listed(unmeasured)}");
        }

        if (watch?.Failure is { } failure)
        {
            return new InputComparison(
                InputChange.Unmeasured,
                [],
                $"the inputs could not be watched while the phase ran: {failure}");
        }

        var moved = Differences(before, after)
            .Concat(watch?.Changed ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return moved.Count == 0
            ? new InputComparison(InputChange.Unchanged, [], string.Empty)
            : new InputComparison(
                InputChange.Moved,
                moved,
                $"{Describe(moved.Count, "input")} changed: {ReportText.Listed(moved)}");
    }

    private static IEnumerable<string> Differences(InputSnapshot before, InputSnapshot after)
    {
        var start = before.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var end = after.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);

        foreach (var (path, first) in start)
        {
            // A file the second snapshot never looked at is a change in what was measured, not
            // evidence that nothing happened to it.
            if (!end.TryGetValue(path, out var last) || last.Identity != first.Identity)
            {
                yield return path;
            }
        }

        foreach (var path in end.Keys.Where(path => !start.ContainsKey(path)))
        {
            yield return path;
        }
    }

    private async Task<FileFingerprint> FingerprintAsync(string relative, string full, CancellationToken cancellationToken)
    {
        // The same hash a sync compares trees by, so "the same file" means one thing throughout: a
        // second implementation would eventually answer differently about one file, and the two
        // places that ask — what to transfer, and whether the inputs held still — would disagree.
        var content = await FileContentHash.OfAsync(_fileSystem, full, cancellationToken).ConfigureAwait(false);

        return new FileFingerprint(relative, content.Length, content.Content)
        {
            Written = TimeOf(_fileSystem.LastWriteTimeUtc, full),
            Created = TimeOf(_fileSystem.CreationTimeUtc, full),
        };
    }

    /// <summary>
    /// What <paramref name="read"/> says of <paramref name="path"/>, or <see langword="null"/> where it
    /// cannot say: a reading without a time confirms nothing, and is never taken for one that held still.
    /// </summary>
    private static DateTime? TimeOf(Func<string, DateTime> read, string path)
    {
        try
        {
            return read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Describe(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>Forward slashes everywhere, so one input is one name in the report on every platform.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>How input paths compare: as this platform compares paths, since that is what decides whether two names are one file.</summary>
    private StringComparer Comparer => _platform.PathComparer();
}

/// <summary>
/// Watches the declared inputs for the life of this object and records every one that changed.
/// </summary>
/// <remarks>
/// Two snapshots cannot see an edit that was undone before the second one was taken, and that is
/// the shape the measured failure took: a configuration file rewritten while the suite ran and
/// restored before it ended. What the watch could not see is reported as <c>unmeasured</c> rather
/// than as nothing found, for the reason an unreadable snapshot is.
/// <para>
/// Which directories to watch is decided here, once; what watches them is an <see cref="IWatchKind"/>,
/// and this is what it tells. A watch that cannot start is never made: the failure leaves, with
/// nothing held, before the work it was for begins.
/// </para>
/// </remarks>
public sealed class InputWatch : IDisposable, IWatchSink
{
    /// <summary>
    /// How many directories are watched at most. Past it, the top-level directories holding the most
    /// directories of inputs are watched all the way down instead, one watch each, and a tree whose
    /// inputs lie in more top-level directories than this - the root counted among them where it
    /// holds one itself - is watched whole: a watch per directory would spend handles to no purpose.
    /// </summary>
    public const int MostDirectoriesWatched = 64;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _tracked;
    private readonly Dictionary<string, FileFingerprint> _found;
    private readonly Dictionary<string, List<string>> _below;
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly IReadOnlyList<(string Directory, bool Recursive)> _targets;
    private readonly IDisposable _watching;
    private readonly Action<string>? _heardElsewhere;
    private string? _failure;

    /// <summary>Watches <paramref name="inputs"/> under <paramref name="root"/>.</summary>
    /// <param name="root">The tree the paths are relative to.</param>
    /// <param name="inputs">The inputs, relative to the tree, as the configuration declared them.</param>
    /// <param name="comparer">How input paths compare on this platform.</param>
    /// <param name="kind">What watches the directories this names.</param>
    /// <param name="reportsEarlierWrites">Whether the watch can be told of a write made before it began.</param>
    /// <param name="found">The inputs as the work found them.</param>
    /// <param name="heardElsewhere">
    /// Told of each path the watch hears of that no input is, relative to the tree, under the watch's lock: what watching
    /// only where the inputs are keeps down. Never kept here, where a long build's every file would be.
    /// </param>
    /// <exception cref="InputsUnwatchedException">
    /// The tree is not there, no directory of an input is, or <paramref name="kind"/> could not watch one: nothing is held.
    /// </exception>
    internal InputWatch(
        string root,
        IEnumerable<string> inputs,
        StringComparer comparer,
        IWatchKind kind,
        bool reportsEarlierWrites = false,
        IReadOnlyList<FileFingerprint>? found = null,
        Action<string>? heardElsewhere = null)
    {
        ReportsEarlierWrites = reportsEarlierWrites;
        _heardElsewhere = heardElsewhere;
        _found = (found ?? [])
            .GroupBy(file => file.Path, comparer)
            .ToDictionary(group => group.Key, group => group.First(), comparer);
        _root = Path.GetFullPath(root);
        _tracked = new Dictionary<string, string>(comparer);
        _below = new Dictionary<string, List<string>>(comparer);

        foreach (var input in inputs)
        {
            _tracked[input] = input;
        }

        // Each directory that holds an input or lies above one, with the inputs in or below it: what a
        // directory going, arriving or changing its name takes with it.
        foreach (var input in _tracked.Values)
        {
            for (var at = input.LastIndexOf('/'); at > 0; at = input.LastIndexOf('/', at - 1))
            {
                var directory = input[..at];

                if (!_below.TryGetValue(directory, out var inputsBelow))
                {
                    _below[directory] = inputsBelow = [];
                }

                inputsBelow.Add(input);
            }
        }

        if (!Directory.Exists(_root))
        {
            // Nothing to watch and nothing watched. Left unsaid this would answer every question
            // about the span with "nothing changed", which is the one answer it has not earned.
            throw InputsUnwatchedException.NoTree(_root);
        }

        _targets = [.. Targets()];

        if (_targets.Count == 0)
        {
            throw InputsUnwatchedException.NoDirectory(_tracked.Count, _root);
        }

        // Last, with everything it tells of in place: a kind may tell of something before it returns.
        _watching = kind.Start(_targets, this);
    }

    /// <summary>
    /// The directories to watch, and whether each is watched all the way down: each directory that
    /// holds a tracked input, the root among them, for its own files only - and, past
    /// <see cref="MostDirectoriesWatched"/>, the top-level directories holding the most of them all
    /// the way down instead, until the rest fit.
    /// </summary>
    /// <remarks>
    /// Not one watch over the whole tree, nor over a whole top-level directory. A build writes its
    /// objects into <c>build/&lt;variant&gt;</c>, and other trees build below the worktrees root - a
    /// directory init keeps in git by a placeholder, so it holds a tracked input. Each is inside the
    /// tree and not tracked, so a watch reaching down into one receives every object file, dependency
    /// file and generated header those builds emit. That overflows the operating system's buffer, and
    /// an overflow is reported as a watch that cannot be trusted - so the work it watched reports
    /// <c>unmeasured</c>, for nothing it did. A guard that fails work it cannot fault is worse than the
    /// one it replaced.
    /// <para>
    /// Watching only the directories the inputs are in excludes the build directory, <c>.git</c>,
    /// the worktrees root's trees and every other untracked directory by construction, rather than by
    /// a list of names that would have to be kept correct. Watched all the way down past the limit,
    /// a top-level directory takes in whatever is built below it again.
    /// </para>
    /// </remarks>
    private IEnumerable<(string Directory, bool Recursive)> Targets()
    {
        var directories = _tracked.Keys
            .Select(input => input.LastIndexOf('/') is var at and > 0 ? input[..at] : string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        static string TopOf(string directory) => directory.IndexOf('/', StringComparison.Ordinal) is var at and > 0 ? directory[..at] : directory;

        // The widest first: each taken whole saves a watch for every directory of inputs below it but one.
        var whole = new HashSet<string>(StringComparer.Ordinal);
        var count = directories.Count;

        foreach (var group in directories
            .Where(directory => directory.Length > 0)
            .GroupBy(TopOf, StringComparer.Ordinal)
            .Select(group => (Top: group.Key, Count: group.Count()))
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.Top, StringComparer.Ordinal))
        {
            if (count <= MostDirectoriesWatched)
            {
                break;
            }

            whole.Add(group.Top);
            count -= group.Count - 1;
        }

        if (count > MostDirectoriesWatched)
        {
            yield return (_root, true);
            yield break;
        }

        var targets = directories
            .Where(directory => !whole.Contains(TopOf(directory)))
            .Select(directory => (Directory: directory, Recursive: false))
            .Concat(whole.Select(top => (Directory: top, Recursive: true)))
            .OrderBy(target => target.Directory, StringComparer.Ordinal);

        foreach (var (directory, recursive) in targets)
        {
            var full = directory.Length == 0 ? _root : Path.Combine(_root, directory.Replace('/', Path.DirectorySeparatorChar));

            // A directory that is not there cannot be watched. Its inputs are absent, which the
            // fingerprints on either side of the work report on their own.
            if (Directory.Exists(full))
            {
                yield return (full, recursive);
            }
        }
    }

    /// <summary>Whether this watch can be told of a write made before it began, as one on macOS can.</summary>
    /// <remarks>
    /// macOS delivers file events through a service that numbers each one as it reads it, and a watch
    /// takes the events numbered after the one current when it started, so a write made a moment before
    /// is sometimes delivered to a watch that did not exist when it was made. Counted, it read as an
    /// input moving under work that had not begun: on a CI run, a test leg whose fixture was written
    /// just before it ran. So each file such a watch is told of is looked at as it is told: one that
    /// stands as the work found it - there, the same size, and written and created when it was - is one
    /// it was told of late, and counts for nothing; any other counts. Looked at then, and not once the
    /// work is done: a file moved aside and put back reads the same at both ends, and was something else
    /// while the work ran. What goes unseen is a change undone before word of it is looked at, and one of
    /// the same size undone in place by a tool that also puts the old time back. A file the work found
    /// absent, or whose times could not be read, is never taken for one that stands as it was found.
    /// </remarks>
    public bool ReportsEarlierWrites { get; }

    /// <summary>The inputs seen changing, in the spelling the configuration declared them in.</summary>
    public IReadOnlyList<string> Changed
    {
        get
        {
            lock (_gate)
            {
                return [.. _changed.OrderBy(path => path, StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>Each directory watched, and whether all the way down, in the order of their paths.</summary>
    internal IReadOnlyList<(string Directory, bool Recursive)> Watched
        => [.. _targets.OrderBy(watched => watched.Directory, StringComparer.Ordinal)];

    /// <summary>
    /// Why the watch can no longer be trusted - events were lost, or the watch itself was, while the
    /// work ran - or <see langword="null"/> when it can.
    /// </summary>
    public string? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    /// <summary>Stops watching. The changes already recorded stay readable.</summary>
    public void Dispose() => _watching.Dispose();

    /// <inheritdoc/>
    void IWatchSink.Written(string fullPath) => Record(fullPath);

    /// <inheritdoc/>
    void IWatchSink.Named(string fullPath) => Named(fullPath);

    /// <inheritdoc/>
    void IWatchSink.Lost(string why)
    {
        lock (_gate)
        {
            _failure ??= why;
        }
    }

    /// <summary>
    /// Counts what the entry at <paramref name="fullPath"/> arriving, going or changing its name changed: the input
    /// it is, as <see cref="Record"/> counts one, and - where it is a directory that holds inputs or lies above
    /// some - every input in or below it, since each went or arrived with it.
    /// </summary>
    /// <remarks>
    /// Only for a name: a directory merely written to - as one is whenever a file is made in it - took none of its
    /// inputs anywhere, and a build writing beside sources would otherwise count every one of them.
    /// </remarks>
    /// <param name="fullPath">The entry the platform said arrived, went or was renamed.</param>
    internal void Named(string fullPath)
    {
        Record(fullPath);

        if (Relative(fullPath) is not { } relative)
        {
            return;
        }

        // The tree itself is above every input.
        IEnumerable<string> inputs = relative == "."
            ? _tracked.Values
            : _below.TryGetValue(relative, out var below) ? below : [];

        foreach (var input in inputs)
        {
            Count(input);
        }
    }

    /// <summary>
    /// Counts the input at <paramref name="fullPath"/> as changed, as word of any change to it does: where
    /// the watch can be told of a write made before it began, only once the file no longer stands as the
    /// work found it.
    /// </summary>
    /// <param name="fullPath">The file the platform said changed.</param>
    internal void Record(string fullPath)
    {
        if (Relative(fullPath) is not { } relative)
        {
            return;
        }

        string? declared;

        lock (_gate)
        {
            if (!_tracked.TryGetValue(relative, out declared))
            {
                _heardElsewhere?.Invoke(relative);
                return;
            }
        }

        Count(declared);
    }

    /// <summary><paramref name="fullPath"/> as the inputs are spelt, relative to the tree; <see langword="null"/> where it is no path.</summary>
    private string? Relative(string fullPath)
    {
        try
        {
            return Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Counts the input <paramref name="declared"/> as changed: where the watch can be told of a write made before
    /// it began, only once the file no longer stands as the work found it.
    /// </summary>
    private void Count(string declared)
    {
        lock (_gate)
        {
            if (_changed.Contains(declared))
            {
                return;
            }
        }

        if (ReportsEarlierWrites && AsFound(declared))
        {
            return;
        }

        lock (_gate)
        {
            _changed.Add(declared);
        }
    }

    /// <summary>
    /// Whether the input <paramref name="declared"/> stands now as the work found it: there, the same
    /// size, and written and created when it was.
    /// </summary>
    private bool AsFound(string declared)
    {
        if (!_found.TryGetValue(declared, out var found)
            || found.Length == InputFingerprint.AbsentLength
            || found.Written is not { } written
            || found.Created is not { } created)
        {
            return false;
        }

        try
        {
            var now = new FileInfo(Path.Combine(_root, declared));

            return now.Exists
                && now.Length == found.Length
                && now.LastWriteTimeUtc == written
                && now.CreationTimeUtc == created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
