namespace RepoHarness.Core.FileSystem;

/// <summary>
/// Where a directory is renamed to while it is removed: beside it, hidden, and named as nothing a build or a sync makes
/// is. A removal renames first, which is done at once and under whatever lock keeps a run out, and deletes after; one
/// that stops part way leaves the directory aside, for whatever next removes it to finish.
/// </summary>
public static class RemovalAside
{
    /// <summary>What follows the directory's own name in its aside's, after a dot before it.</summary>
    public const string Suffix = ".removing";

    /// <summary>Where <paramref name="directory"/> is renamed to while it is removed.</summary>
    /// <param name="directory">The directory.</param>
    public static string Of(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var whole = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        return Path.Combine(Path.GetDirectoryName(whole)!, "." + Path.GetFileName(whole) + Suffix);
    }

    /// <summary>
    /// The name of the directory an aside named <paramref name="name"/> was, or <see langword="null"/> where
    /// <paramref name="name"/> is no aside's.
    /// </summary>
    /// <param name="name">A directory's own name, with no path before it.</param>
    public static string? Was(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length > 1 + Suffix.Length && name[0] == '.' && name.EndsWith(Suffix, StringComparison.Ordinal)
            ? name[1..^Suffix.Length]
            : null;
    }
}
