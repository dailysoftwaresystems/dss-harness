namespace RepoHarness.Core.FileSystem;

/// <summary>
/// The one test of a directory holding nothing, and the one walk that removes a directory a deletion emptied with each
/// parent it empties in turn: every tree that removes what it emptied asks the same question the same way.
/// </summary>
public static class EmptyDirectories
{
    /// <summary>
    /// Whether <paramref name="directory"/> holds nothing: no file, no directory, and no link to either - a directory
    /// holding nothing but a link is not empty, since removing it would take what nothing weighed.
    /// </summary>
    /// <param name="fileSystem">Lists the directory.</param>
    /// <param name="directory">The directory, which must exist.</param>
    public static bool IsEmpty(this IFileSystem fileSystem, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return !fileSystem.EnumerateFiles(directory, recursive: false).Any() && !fileSystem.EnumerateDirectories(directory).Any();
    }

    /// <summary>
    /// Removes <paramref name="directory"/> where it holds nothing, then each parent that leaves holding nothing, up to -
    /// never including - <paramref name="root"/>; stops at the first that holds something or is not there.
    /// </summary>
    /// <param name="fileSystem">Lists and removes the directories.</param>
    /// <param name="root">The tree the walk never leaves, and never removes.</param>
    /// <param name="directory">Where to start; nothing is removed where it is null or outside the tree.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    /// <returns>The directories removed, deepest first.</returns>
    public static IReadOnlyList<string> RemoveEmptiedDirectories(this IFileSystem fileSystem, string root, string? directory, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var removed = new List<string>();

        while (directory is not null
            && PathContainment.IsStrictlyInside(root, directory, comparison)
            && fileSystem.DirectoryExists(directory)
            && fileSystem.IsEmpty(directory))
        {
            fileSystem.DeleteDirectory(directory);
            removed.Add(directory);
            directory = Path.GetDirectoryName(directory);
        }

        return removed;
    }
}
