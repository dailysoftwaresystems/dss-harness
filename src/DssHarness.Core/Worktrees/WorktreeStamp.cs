using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Worktrees;

/// <summary>
/// Which worktree a directory is, as opposed to where it is: told by a file in the worktree's own git directory that is
/// written once, when the worktree is made - a linked worktree's <c>commondir</c>, which git writes, and an agent's
/// repository's <c>objects/info/alternates</c>, which its making writes and nothing writes again. A new worktree made at the
/// same path gets a new one; repairing a linked worktree, moving it, or rewriting its .git file leaves it as it is.
/// </summary>
/// <remarks>
/// The file's creation and last-write times, as ticks. The file's id would say it more surely, and the runtime reads no
/// file's id on every platform; the two times are compared for equality only, and a worktree is made where another was
/// only by hand, since an agent's name is never used twice.
/// </remarks>
public static class WorktreeStamp
{
    /// <summary>The file in a linked worktree's git directory the stamp is taken from.</summary>
    public const string FileName = "commondir";

    /// <summary>The file in an agent's repository's git directory the stamp is taken from, relative to it.</summary>
    public static readonly string RepositoryFileName = Path.Combine("objects", "info", "alternates");

    /// <summary>The file the stamp of a worktree of the kind given is taken from, relative to its git directory.</summary>
    /// <param name="ownRepository">Whether the worktree is an agent's repository of its own rather than a linked worktree.</param>
    public static string FileOf(bool ownRepository) => ownRepository ? RepositoryFileName : FileName;

    /// <summary>
    /// The stamp of the worktree whose own git directory is <paramref name="administrativeDirectory"/>; null where the
    /// directory holds no such file.
    /// </summary>
    /// <param name="fileSystem">Reads the file's times.</param>
    /// <param name="administrativeDirectory">The worktree's own git directory, as git names it.</param>
    /// <param name="ownRepository">Whether the worktree is an agent's repository of its own rather than a linked worktree.</param>
    /// <exception cref="IOException">The file is there, or may be, and its times could not be read: never read as no stamp.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read them.</exception>
    public static string? Of(IFileSystem fileSystem, string administrativeDirectory, bool ownRepository)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(administrativeDirectory);

        var file = Path.Combine(administrativeDirectory, FileOf(ownRepository));

        return fileSystem.KindOf(file) == PathKind.File
            ? $"{fileSystem.CreationTimeUtc(file).Ticks}:{fileSystem.LastWriteTimeUtc(file).Ticks}"
            : null;
    }
}
