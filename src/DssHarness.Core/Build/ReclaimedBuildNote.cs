using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Build;

/// <summary>
/// What a build directory another tree's leg removed to make room leaves where it was: one line, in a hidden file beside
/// it in the tree's build root - as a removal's aside is kept (<see cref="RemovalAside"/>) - saying when it was removed
/// and for which leg; the next build there says it as it starts from nothing, and removes it.
/// </summary>
/// <remarks>
/// Beside the directory, never in it: a directory there again, holding no build's record, would be read as a build nobody
/// recorded, and its need as nothing anyone measured. A note a build of an earlier version passes over stays until a build
/// of this one finds the directory there again, and removes it unsaid: the build it speaks of is no longer the last.
/// </remarks>
public static class ReclaimedBuildNote
{
    /// <summary>What follows the directory's own name in its note's, after a dot before it.</summary>
    public const string Suffix = ".reclaimed";

    /// <summary>Where the note of <paramref name="buildDirectory"/> is kept.</summary>
    /// <param name="buildDirectory">The build directory.</param>
    public static string PathOf(string buildDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buildDirectory);

        var whole = Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildDirectory));

        return Path.Combine(Path.GetDirectoryName(whole)!, "." + Path.GetFileName(whole) + Suffix);
    }

    /// <summary>Leaves <paramref name="said"/> where <paramref name="buildDirectory"/> was, for its next build to say.</summary>
    /// <param name="fileSystem">Writes the note.</param>
    /// <param name="buildDirectory">The build directory removed.</param>
    /// <param name="said">What its next build says: <c>its build directory was removed ... to make room for ...</c>.</param>
    /// <exception cref="IOException">The note could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of leave.</exception>
    public static void Write(IFileSystem fileSystem, string buildDirectory, string said)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(said);

        fileSystem.WriteAllTextAtomic(PathOf(buildDirectory), said.ReplaceLineEndings(" ") + "\n");
    }

    /// <summary>
    /// What the note of <paramref name="buildDirectory"/> says, where the directory is not there for a build to find, and the
    /// note removed: once said, it is said no more. <see langword="null"/> where there is none, where the directory is there
    /// again - the note is removed then, unsaid - or where it cannot be read, which says no more than none.
    /// </summary>
    /// <param name="fileSystem">Reads and removes the note.</param>
    /// <param name="buildDirectory">The build directory a build is about to fill.</param>
    public static string? Take(IFileSystem fileSystem, string buildDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var note = PathOf(buildDirectory);

        try
        {
            if (!fileSystem.FileExists(note))
            {
                return null;
            }

            var said = fileSystem.DirectoryExists(buildDirectory) ? null : fileSystem.ReadAllText(note).Trim();

            fileSystem.DeleteFile(note);

            return said is { Length: > 0 } ? said : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
