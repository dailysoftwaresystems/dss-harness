using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// The files of one reading of a tree, found by a path relative to the tree as the tree's own file system compares names,
/// and named as the reading spells them: so a row, a build or a record that spells a file of the tree in another case on a
/// file system that does not tell them apart finds the one file the reading holds.
/// </summary>
/// <remarks>
/// Where the file system tells the cases apart, a path is found only as the reading spells it. Where two entries of the
/// reading differ only in case - which such a file system cannot hold - the first by ordinal order is the one found.
/// </remarks>
internal sealed class TreeFiles
{
    private readonly Dictionary<string, SyncEntry> _entries;

    /// <summary>The files <paramref name="reading"/> holds, found as <paramref name="names"/> compares names.</summary>
    /// <param name="reading">A reading of the tree: every file a sync carries, by size and hash.</param>
    /// <param name="names">How the tree's file system compares names.</param>
    public TreeFiles(SyncManifest reading, StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(names);

        Names = names;
        Comparison = StringComparer.IsWellKnownOrdinalComparer(names, out var ignoreCase) && ignoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _entries = new(names);

        foreach (var entry in reading.Entries.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            _entries.TryAdd(entry.Path, entry);
        }
    }

    /// <summary>How the tree's file system compares names (<see cref="FileSystem.PathCase.In"/>).</summary>
    public StringComparer Names { get; }

    /// <summary>How the tree's file system compares names, as a comparison of two paths.</summary>
    public StringComparison Comparison { get; }

    /// <summary>How many files the reading holds.</summary>
    public int Count => _entries.Count;

    /// <summary>The reading's entry for <paramref name="path"/>, or <see langword="null"/> where it holds none.</summary>
    /// <param name="path">A path relative to the tree, with forward separators.</param>
    public SyncEntry? Find(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return _entries.GetValueOrDefault(path);
    }

    /// <summary>What the reading says <paramref name="path"/> holds, or <see langword="null"/> where it holds no such file.</summary>
    /// <param name="path">A path relative to the tree, with forward separators.</param>
    public string? HashOf(string path) => Find(path)?.ContentHash;

    /// <summary>
    /// Each file of the reading <paramref name="patterns"/> covers (<see cref="Repository.PathPatterns.Matches"/>), by its
    /// path as the reading spells it, with what the reading says it holds, sorted by path: the patterns compared as this
    /// tree's file system compares names, like every path the reading is asked for.
    /// </summary>
    /// <param name="patterns">Paths and patterns relative to the tree, as a configuration writes them.</param>
    public IReadOnlyDictionary<string, string> Matching(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        return CanonicalJson.Sorted(_entries.Values
            .Where(entry => Repository.PathPatterns.Matches(patterns, entry.Path, Comparison))
            .ToDictionary(entry => entry.Path, entry => entry.ContentHash, StringComparer.Ordinal));
    }
}
