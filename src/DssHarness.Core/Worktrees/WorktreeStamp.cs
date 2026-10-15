using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Worktrees;

/// <summary>
/// Which git worktree a directory is, as opposed to where it is: told by the <c>commondir</c> file in the worktree's own
/// git directory, which git writes once, when the worktree is made. A new worktree made at the same path gets a new one;
/// repairing a worktree, moving it, or rewriting its .git file leaves it as it is.
/// </summary>
/// <remarks>
/// The file's creation and last-write times, as ticks. The file's id would say it more surely, and the runtime reads no
/// file's id on every platform; the two times are compared for equality only, and a worktree is made where another was
/// only by hand, since an agent's name is never used twice.
/// </remarks>
public static class WorktreeStamp
{
    /// <summary>The file in a worktree's git directory the stamp is taken from.</summary>
    public const string FileName = "commondir";

    /// <summary>The stamp of the worktree whose own git directory is <paramref name="administrativeDirectory"/>; null where the directory holds no such file.</summary>
    /// <param name="fileSystem">Reads the file's times.</param>
    /// <param name="administrativeDirectory">The worktree's own git directory, as git names it.</param>
    /// <exception cref="IOException">The file is there, or may be, and its times could not be read: never read as no stamp.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read them.</exception>
    public static string? Of(IFileSystem fileSystem, string administrativeDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(administrativeDirectory);

        var file = Path.Combine(administrativeDirectory, FileName);

        return fileSystem.KindOf(file) == PathKind.File
            ? $"{fileSystem.CreationTimeUtc(file).Ticks}:{fileSystem.LastWriteTimeUtc(file).Ticks}"
            : null;
    }
}
