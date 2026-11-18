using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Runs;

/// <summary>What removing a build directory came to.</summary>
/// <param name="Bytes">What was removed: what the directory held, and what earlier removals of it had left aside.</param>
/// <param name="Moved">Whether the directory was there, and was moved out of a build's way to be removed.</param>
/// <param name="HeldBy">The run holding it, as a refusal names one, where one did; nothing of the directory was moved then.</param>
public sealed record BuildDirectoryRemoved(long Bytes, bool Moved, string? HeldBy)
{
    /// <summary>Whether anything was removed: the directory, or what earlier removals of it had left aside.</summary>
    public bool Removed => Moved || Bytes > 0;
}

/// <summary>
/// Removes a leg's build directory on this machine, the one way it is removed: renamed aside while no run holds the lock a
/// build of it takes - under the mutex a run needs to take one, so no build can start in between, and writing nothing on a
/// disk that may be full - into an aside of this process's own (<see cref="RemovalAside.Of(string, int)"/>), and removed
/// from there by this process alone. <c>clean --legs</c> removes each leg's own this way, and a leg short of room another
/// tree's idle one (<see cref="IdleBuilds"/>).
/// </summary>
/// <remarks>
/// Two processes may remove one build directory at once - a clean of it, and another tree's leg making room - and only one
/// of them finds it to move: each deletes only what it moved, so neither counts the room the other freed, nor fails on
/// entries the other took. What an earlier removal left aside - one that stopped part way, or a build's before this one,
/// named by no process - is finished by the next removal of the directory, taken first into that removal's own aside: a
/// rename, which only one removal can make, and never of one a process still running is removing.
/// </remarks>
/// <param name="runLock">
/// Reads which run holds a build of the directory; its process names this removal's asides, and tells whether the process
/// that named another still runs, as it tells a lock's holders.
/// </param>
/// <param name="fileSystem">Moves and removes it.</param>
public sealed class BuildDirectoryRemoval(RunLock runLock, IFileSystem fileSystem)
{
    private readonly RunLock _runLock = runLock;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessIdentity _identity = runLock.Identity;

    /// <summary>
    /// Removes <paramref name="directory"/>, a build directory below <paramref name="root"/>, where no run holds what
    /// <paramref name="building"/> asks of <paramref name="layout"/>'s lock; said as held, and nothing of it moved, where one
    /// does. What earlier removals left aside is removed first, whether or not one does: nothing builds in an aside.
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

        var own = AsideOf(directory);

        // This process's own first - one of its removals of the directory that stopped part way - then each an ended removal
        // left, taken into this one's own and removed from there.
        var removed = Delete(_fileSystem, root, own);

        foreach (var left in LeftAside(directory))
        {
            if (TakenOver(root, left, own))
            {
                removed += Delete(_fileSystem, root, own);
            }
        }

        var moved = false;
        var holder = _runLock.HeldBy(layout, building, () =>
        {
            if (_fileSystem.DirectoryExists(directory))
            {
                UnfollowedFiles.Move(_fileSystem, root, directory, own, replace: false);
                moved = true;
            }
        });

        if (holder is not null)
        {
            return new BuildDirectoryRemoved(removed, Moved: false, holder);
        }

        if (moved)
        {
            removed += Delete(_fileSystem, root, own);
        }

        return new BuildDirectoryRemoved(removed, moved, null);
    }

    /// <summary>The aside this process moves <paramref name="directory"/> to as it removes it, where what it could not finish is left.</summary>
    /// <param name="directory">The build directory.</param>
    public string AsideOf(string directory) => RemovalAside.Of(directory, _identity.CurrentId);

    /// <summary>
    /// What earlier removals of <paramref name="directory"/> left aside and no process is removing now: the aside named by no
    /// process, and each named by one no process of this machine carries - never this process's own, nor one a process still
    /// running is removing, which that process alone deletes. In the order of their names; none where the directory's
    /// parent is not there.
    /// </summary>
    /// <param name="directory">The build directory.</param>
    /// <exception cref="IOException">The directory's parent could not be looked in.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of leave.</exception>
    public IReadOnlyList<string> LeftAside(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var whole = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var parent = Path.GetDirectoryName(whole)!;
        var name = Path.GetFileName(whole);

        if (!_fileSystem.DirectoryExists(parent))
        {
            return [];
        }

        return
        [
            .. _fileSystem.EnumerateDirectories(parent)
                .Where(aside => Path.GetFileName(Path.TrimEndingDirectorySeparator(aside)) is var found
                    && string.Equals(RemovalAside.Was(found), name, StringComparison.Ordinal)
                    && (RemovalAside.By(found) is not { } by || (by != _identity.CurrentId && !_identity.IsAlive(by, stamp: null))))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Whether <paramref name="left"/> was taken into <paramref name="own"/>: renamed there whole, which only one removal can
    /// do - one another removal took first is no longer there, and is that removal's to finish.
    /// </summary>
    /// <exception cref="IOException">It is still there, and could not be moved.</exception>
    private bool TakenOver(string root, string left, string own)
    {
        try
        {
            UnfollowedFiles.Move(_fileSystem, root, left, own, replace: false);
            return true;
        }
        catch (IOException) when (!_fileSystem.DirectoryExists(left))
        {
            return false;
        }
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
