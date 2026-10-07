using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Mutations;

/// <summary>How many workers a leg's sweep runs, and why it runs fewer than it wanted where it does.</summary>
/// <param name="Count">How many it runs: none where not even its first worker fits the room.</param>
/// <param name="Wanted">
/// How many it wanted: <c>mutations.workers</c>, never more than it has arms to drive, and one in a WSL distribution.
/// </param>
/// <param name="Fewer">
/// Why it runs fewer than it wanted, as the leg's detail says it - what is free, and what the workers need - or
/// <see langword="null"/> where it runs every worker it wanted.
/// </param>
/// <param name="Unchecked">
/// Why the room was not checked, where it could not be measured, so the sweep runs every worker it wanted unchecked; or
/// <see langword="null"/> where it was checked.
/// </param>
public sealed record WorkerPlan(int Count, int Wanted, string? Fewer, string? Unchecked);

/// <summary>
/// How many workers a leg's sweep can run in the room its machine has: each worker a copy of the tree with a build
/// directory of its own, which stays on disk between sweeps, so its build is warm the next time.
/// </summary>
/// <remarks>
/// <para>
/// A sweep already runs beside the other legs of its command, on a machine that may be a small one, so it wants no more
/// workers than <c>mutations.workers</c> - two where left out - and never more than it has arms. A WSL distribution
/// cannot see the memory Windows has, and its sweep is admitted as one heavy leg for the whole of it, so it runs one.
/// </para>
/// <para>
/// A worker needs its copy of the tree, where it holds none yet, and what its build is expected to come to, less what
/// its build directory already holds where its last build recorded that - the same reckoning a leg's own build is
/// placed by. Workers are counted in order, each beside the ones before it: a sweep runs the workers that fit, saying
/// it runs fewer, and none where its first does not, which turns the leg away as a leg that does not fit is.
/// </para>
/// </remarks>
public static class WorkerRoom
{
    /// <summary>How many workers a sweep wants: <paramref name="cap"/>, no more than <paramref name="arms"/>, and one in WSL.</summary>
    /// <param name="cap">At most how many: <c>mutations.workers</c>.</param>
    /// <param name="arms">How many arms the sweep drives on the leg.</param>
    /// <param name="wsl">Whether the leg runs in a WSL distribution.</param>
    public static int Wanted(int cap, int arms, bool wsl)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cap);
        ArgumentOutOfRangeException.ThrowIfNegative(arms);

        return Math.Min(wsl ? 1 : cap, arms);
    }

    /// <summary>
    /// What one worker still needs of the room: the copy of the tree where it holds none yet, and what its build is
    /// expected to come to - all of it where its build directory is not there, what its last build there did not reach
    /// where that build recorded what it came to, and nothing more where no build of this version recorded it, which
    /// holds an amount nothing measured.
    /// </summary>
    /// <param name="sourceBytes">What the tree's copy comes to, as the sweep's one reading of the tree counts it.</param>
    /// <param name="copyPresent">Whether the worker's copy of the tree is there already.</param>
    /// <param name="expectedBuildBytes">What a build of the leg is expected to come to, or <see langword="null"/> where nothing says.</param>
    /// <param name="buildPresent">Whether the worker's build directory is there already.</param>
    /// <param name="recordedBuildBytes">What the worker's last build recorded its directory came to, or <see langword="null"/>.</param>
    public static long Need(long sourceBytes, bool copyPresent, long? expectedBuildBytes, bool buildPresent, long? recordedBuildBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedBuildBytes ?? 0, nameof(expectedBuildBytes));

        var copy = copyPresent ? 0 : sourceBytes;
        var build = expectedBuildBytes is not { } expected ? 0
            : !buildPresent ? expected
            : recordedBuildBytes is { } held ? Math.Max(0, expected - held)
            : 0;

        return copy + build;
    }

    /// <summary>
    /// How many of <paramref name="needs"/>'s workers fit <paramref name="room"/>: each, in order, beside the ones before
    /// it; or all of them, unchecked, where the room could not be measured.
    /// </summary>
    /// <param name="needs">What each worker the sweep wants still needs, in the order the workers are numbered.</param>
    /// <param name="room">The room where the workers are kept, or <see langword="null"/> where it could not be measured.</param>
    /// <param name="unmeasured">Why it could not be measured, where it could not.</param>
    /// <param name="source">What said how much a worker's build comes to, as a line says it, or <see langword="null"/> where nothing does.</param>
    public static WorkerPlan Plan(IReadOnlyList<long> needs, DiskSpace? room, string? unmeasured, string? source)
    {
        ArgumentNullException.ThrowIfNull(needs);

        var wanted = needs.Count;

        if (wanted == 0)
        {
            return new WorkerPlan(0, 0, null, null);
        }

        if (room is null)
        {
            return new WorkerPlan(wanted, wanted, null, $"the room for its workers could not be measured: {unmeasured ?? "no reason was given"}");
        }

        var taken = 0L;
        var fitting = 0;

        while (fitting < wanted && taken + needs[fitting] <= room.FreeBytes)
        {
            taken += needs[fitting];
            fitting++;
        }

        if (fitting == wanted)
        {
            return new WorkerPlan(wanted, wanted, null, null);
        }

        var reckoned = source is null ? string.Empty : $", each worker's build {source}";

        return new WorkerPlan(
            fitting,
            wanted,
            fitting == 0
                ? $"{DiskSpace.Size(room.FreeBytes)} free on '{room.Filesystem}', and its first worker needs ~{DiskSpace.Size(needs[0])}{reckoned}"
                : $"{fitting} of {wanted} workers: {DiskSpace.Size(room.FreeBytes)} free on '{room.Filesystem}', and {fitting + 1} need "
                    + $"~{DiskSpace.Size(taken + needs[fitting])}{reckoned}",
            null);
    }
}
