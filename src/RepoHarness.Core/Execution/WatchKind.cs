namespace RepoHarness.Core.Execution;

/// <summary>
/// What watches directories for a span of work, as one operating system's means of watching does it. Which directories
/// to watch is decided once, by the watch that asks; how each is watched, and what watching it takes from the machine, is
/// the kind's.
/// </summary>
internal interface IWatchKind
{
    /// <summary>
    /// How many directories a watch through this kind takes at most, or <see langword="null"/> where it has no bound of
    /// its own. Past a bound, the watch takes some directories all the way down instead; with none, every directory that
    /// holds an input is watched for itself, and none all the way down.
    /// </summary>
    int? MostDirectories { get; }

    /// <summary>
    /// Watches every one of <paramref name="targets"/> until what it returns is disposed, telling
    /// <paramref name="sink"/> of what happens in them - from other threads, and possibly before this returns.
    /// </summary>
    /// <param name="targets">Each directory to watch, by its full path, and whether all the way down.</param>
    /// <param name="sink">Told of each thing heard.</param>
    /// <returns>What stops the watching when disposed. What was already told stays told.</returns>
    /// <exception cref="InputsUnwatchedException">
    /// One of them could not be watched. Whatever had been taken for the others is released before this leaves, so a
    /// watch that could not start holds nothing.
    /// </exception>
    IDisposable Start(IReadOnlyList<(string Directory, bool Recursive)> targets, IWatchSink sink);
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
