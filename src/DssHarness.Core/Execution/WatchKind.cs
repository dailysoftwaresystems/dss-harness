namespace DssHarness.Core.Execution;

/// <summary>How a watched directory is watched.</summary>
internal enum WatchDepth
{
    /// <summary>For every entry in it, and nothing below them.</summary>
    Entries,

    /// <summary>
    /// For the directories in it arriving, going or changing their names alone: a directory above inputs that holds none
    /// itself, watched so that one of those going is heard - and nothing else of it, so what is written there crowds out
    /// nothing.
    /// </summary>
    NamesOnly,

    /// <summary>For every entry in it and below it.</summary>
    AllTheWayDown,
}

/// <summary>
/// One directory a watch puts on its kind, and how it is to be watched: made only as one of the three ways, so no target
/// is ever two of them at once.
/// </summary>
internal sealed record WatchTarget
{
    private WatchTarget(string directory, WatchDepth depth)
    {
        Directory = directory;
        Depth = depth;
    }

    /// <summary>The directory, by its full path.</summary>
    public string Directory { get; }

    /// <summary>How it is watched.</summary>
    public WatchDepth Depth { get; }

    /// <summary>A directory watched for every entry in it.</summary>
    /// <param name="directory">The directory, by its full path.</param>
    /// <returns>The target.</returns>
    public static WatchTarget Entries(string directory) => new(directory, WatchDepth.Entries);

    /// <summary>A directory watched for the names of the directories in it alone.</summary>
    /// <param name="directory">The directory, by its full path.</param>
    /// <returns>The target.</returns>
    public static WatchTarget NamesOnly(string directory) => new(directory, WatchDepth.NamesOnly);

    /// <summary>A directory watched for every entry in it and below it.</summary>
    /// <param name="directory">The directory, by its full path.</param>
    /// <returns>The target.</returns>
    public static WatchTarget AllTheWayDown(string directory) => new(directory, WatchDepth.AllTheWayDown);
}

/// <summary>
/// What watches directories for a span of work, as one operating system's means of watching does it. Which directories
/// to watch is decided once, by the watch that asks; how each is watched, and what watching it takes from the machine, is
/// the kind's.
/// </summary>
internal interface IWatchKind
{
    /// <summary>
    /// How many directories a watch through this kind takes at most besides the root's own - each holding inputs and each
    /// above them watched for the names in it alone, counted alike - or <see langword="null"/> where it has no bound of its
    /// own. Past a bound, the watch takes some directories all the way down instead; with none, every directory that holds
    /// an input, or lies above one, is watched for itself, and none all the way down.
    /// </summary>
    int? MostDirectories { get; }

    /// <summary>
    /// Watches every one of <paramref name="targets"/> until what it returns is disposed, telling
    /// <paramref name="sink"/> of what happens in them - from other threads, and possibly before this returns.
    /// </summary>
    /// <param name="targets">Each directory to watch, and how.</param>
    /// <param name="sink">Told of each thing heard.</param>
    /// <returns>What stops the watching when disposed. What was already told stays told.</returns>
    /// <exception cref="InputsUnwatchedException">
    /// One of them could not be watched. Whatever had been taken for the others is released before this leaves, so a
    /// watch that could not start holds nothing.
    /// </exception>
    IDisposable Start(IReadOnlyList<WatchTarget> targets, IWatchSink sink);
}

/// <summary>What a watch is told of, by whatever kind watches for it.</summary>
internal interface IWatchSink
{
    /// <summary>What an entry holds, or its attributes, changed.</summary>
    /// <param name="fullPath">The entry.</param>
    void Written(string fullPath);

    /// <summary>
    /// An entry, a file or a directory, arrived, went or was renamed - told once for each of a rename's two names - or
    /// a watched directory itself went.
    /// </summary>
    /// <param name="fullPath">The entry, or the watched directory.</param>
    void Named(string fullPath);

    /// <summary>Events were lost, or the watch is gone: the span can no longer be answered for.</summary>
    /// <param name="why">What was lost, in the words a verdict will carry.</param>
    void Lost(string why);
}
