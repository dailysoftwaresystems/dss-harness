using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Execution;

/// <summary>
/// What a leg's build directory held, whether it was removed, and the room on its filesystem - with what the mutation
/// workers kept beside its tree held, where a clean dealt with any.
/// </summary>
/// <param name="Directory">The build directory, on the machine that holds it.</param>
/// <param name="BuildBytes">
/// What it held: what was removed, or, where nothing was, what is there - 0 where it was not measured, and this says the
/// leg's mutation workers alone.
/// </param>
/// <param name="Removed">Whether it was removed.</param>
/// <param name="Disk">
/// The room on its filesystem - after the removal, of the leg's mutation workers too, where there was one - or
/// <see langword="null"/> where it could not be measured.
/// </param>
public sealed record BuildSpace(string Directory, long BuildBytes, bool Removed, DiskSpace? Disk)
{
    /// <summary>
    /// What the mutation workers kept beside the leg's tree held, with what an earlier removal of them left aside: what
    /// was removed of them - 0 where every one there was kept - or, in a dry run, what is there.
    /// <see langword="null"/> where the leg keeps none. Carried wherever anything named as a worker of the leg's was
    /// found: a leg whose build directory was not measured - a link, locked, not removable, or on a host holding no copy
    /// of the tree - has a space that says this alone, nothing of the directory and no room.
    /// </summary>
    public long? WorkerBytes { get; init; }
}
