using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Runs;

/// <summary>What removing a build directory came to.</summary>
/// <param name="Bytes">What was removed: what the directory held, and what an earlier removal of it had left aside.</param>
/// <param name="Moved">Whether the directory was there, and was moved out of a build's way to be removed.</param>
/// <param name="HeldBy">The run holding it, as a refusal names one, where one did; nothing of the directory was moved then.</param>
public sealed record BuildDirectoryRemoved(long Bytes, bool Moved, string? HeldBy)
{
    /// <summary>Whether anything was removed: the directory, or what an earlier removal of it had left aside.</summary>
    public bool Removed => Moved || Bytes > 0;
}

/// <summary>
/// Removes a leg's build directory on this machine, the one way it is removed: what an earlier removal left aside first,
/// outside the lock, since nothing builds there; then the directory renamed aside while no run holds the lock a build of it
/// takes - under the mutex a run needs to take one, so no build can start in between, and writing nothing on a disk that
/// may be full - and removed from there. <c>clean --legs</c> removes each leg's own this way, and a leg short of room
/// another tree's idle one (<see cref="IdleBuilds"/>).
/// </summary>
/// <param name="runLock">Reads which run holds a build of the directory.</param>
/// <param name="fileSystem">Moves and removes it.</param>
public sealed class BuildDirectoryRemoval(RunLock runLock, IFileSystem fileSystem)
{
    private readonly RunLock _runLock = runLock;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>
    /// Removes <paramref name="directory"/>, a build directory below <paramref name="root"/>, where no run holds what
    /// <paramref name="building"/> asks of <paramref name="layout"/>'s lock; said as held, and nothing of it moved, where one
    /// does.
    /// </summary>
    /// <param name="layout">The repository whose lock file holds the builds of the directory.</param>
    /// <param name="root">The tree the directory is in, which nothing of the removal is followed out of.</param>
    /// <param name="directory">The build directory.</param>
    /// <param name="building">The lock a build of it takes (<see cref="PlacedLeg.BuildLock(RunId, string)"/>).</param>
    /// <exception cref="IOException">The directory, or what an earlier removal left aside, could not be moved or removed.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of leave.</exception>
    /// <exception cref="Results.HarnessException">The lock file could not be read.</exception>
    public BuildDirectoryRemoved Remove(HarnessLayout layout, string root, string directory, LockRequest building)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(building);

        var aside = RemovalAside.Of(directory);

        // Removed first, and outside the lock: nothing builds in a directory an earlier removal moved aside.
        var removed = Delete(_fileSystem, root, aside);

        var moved = false;
        var holder = _runLock.HeldBy(layout, building, () =>
        {
            if (_fileSystem.DirectoryExists(directory))
            {
                UnfollowedFiles.Move(_fileSystem, root, directory, aside, replace: false);
                moved = true;
            }
        });

        if (holder is not null)
        {
            return new BuildDirectoryRemoved(removed, Moved: false, holder);
        }

        if (moved)
        {
            removed += Delete(_fileSystem, root, aside);
        }

        return new BuildDirectoryRemoved(removed, moved, null);
    }

    /// <summary>
    /// Removes <paramref name="directory"/>, below <paramref name="root"/>, and says what it held: counted only once it is
    /// gone, so one that could not be removed is never among what a removal says it freed - it is still there, for the next
    /// to count.
    /// </summary>
    /// <param name="fileSystem">Measures and removes it.</param>
    /// <param name="root">The tree it is in, which nothing of the removal is followed out of.</param>
    /// <param name="directory">The directory; nothing where it is not there.</param>
    /// <exception cref="IOException">It could not be removed.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of leave.</exception>
    public static long Delete(IFileSystem fileSystem, string root, string directory)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var held = fileSystem.DirectorySize(directory);

        UnfollowedFiles.DeleteDirectory(fileSystem, root, directory);

        return held;
    }
}
