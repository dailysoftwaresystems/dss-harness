using System.Diagnostics.CodeAnalysis;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// How many workers a leg's sweep runs, and why it runs fewer than it wanted where it does: some of them
/// (<see cref="Running"/>), or none (<see cref="None"/>), which always says why.
/// </summary>
public sealed record WorkerPlan
{
    private WorkerPlan(int count, string? fewer, string? unmeasured)
    {
        Count = count;
        Fewer = fewer;
        Unchecked = unmeasured;
    }

    /// <summary>How many it runs: none where not even its first worker fits the room, or the machine's path limit.</summary>
    public int Count { get; }

    /// <summary>
    /// Why it runs fewer than it wanted, as the leg's detail says it - how many of those it wanted, what is free and
    /// what the workers need, and the path limit the build of one more would pass - or <see langword="null"/> where it
    /// runs every worker it wanted.
    /// </summary>
    public string? Fewer { get; }

    /// <summary>
    /// Why the room was not checked, where it could not be measured, so the sweep runs unchecked every worker it wanted
    /// that is within the path limit; or <see langword="null"/> where it was checked.
    /// </summary>
    public string? Unchecked { get; }

    /// <summary>Whether it runs none, which turns its leg away for <see cref="Fewer"/>.</summary>
    [MemberNotNullWhen(true, nameof(Fewer))]
    public bool RunsNone => Count == 0;

    /// <summary>A sweep that runs <paramref name="count"/> workers.</summary>
    /// <param name="count">How many: at least one.</param>
    /// <param name="fewer">Why that is fewer than it wanted, or <see langword="null"/> where it is every one.</param>
    /// <param name="unmeasured">Why the room was not checked, or <see langword="null"/> where it was.</param>
    public static WorkerPlan Running(int count, string? fewer = null, string? unmeasured = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        return new(count, fewer, unmeasured);
    }

    /// <summary>A sweep that runs no worker, for <paramref name="why"/>.</summary>
    /// <param name="why">Why not even its first is run, as the leg's detail says it.</param>
    public static WorkerPlan None(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new(0, why, null);
    }
}

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
    /// <param name="sourceBytes">
    /// What the tree's copy comes to, as the sweep's one reading of the tree counts it: its files, and what git keeps
    /// there of its history.
    /// </param>
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
    /// How many of the <paramref name="wanted"/> workers a sweep runs: of those within the machine's path limit, whose
    /// <paramref name="needs"/> these are, the ones that fit <paramref name="room"/> - each, in order, beside the ones
    /// before it - or all of those, unchecked, where the room could not be measured. Where that is fewer than it wanted,
    /// the plan says how many of those it wanted, and what keeps each of the rest out: the room, the path limit, or both.
    /// </summary>
    /// <param name="needs">
    /// What each worker within the path limit still needs, in the order the workers are numbered: a worker's path grows
    /// only with its number, so those within the limit come first.
    /// </param>
    /// <param name="wanted">How many workers the sweep wanted: at least one, and no fewer than <paramref name="needs"/> holds.</param>
    /// <param name="beyond">
    /// Why the workers past those of <paramref name="needs"/> are not run - the path limit the build of the next would
    /// pass, as a line says it - where the sweep wanted more of them; <see langword="null"/> where it did not.
    /// </param>
    /// <param name="room">The room where the workers are kept, or <see langword="null"/> where it could not be measured.</param>
    /// <param name="unmeasured">Why it could not be measured, where it could not.</param>
    /// <param name="source">What said how much a worker's build comes to, as a line says it, or <see langword="null"/> where nothing does.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="beyond"/> is given where no worker is beyond the limit, or is not where one is.
    /// </exception>
    public static WorkerPlan Plan(IReadOnlyList<long> needs, int wanted, string? beyond, DiskSpace? room, string? unmeasured, string? source)
    {
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wanted);
        ArgumentOutOfRangeException.ThrowIfLessThan(wanted, needs.Count);

        var within = needs.Count;

        if (within < wanted == string.IsNullOrWhiteSpace(beyond))
        {
            throw new ArgumentException("Why the workers beyond the path limit are not run is said where there are any, and only there.", nameof(beyond));
        }

        if (within == 0)
        {
            return WorkerPlan.None(beyond!);
        }

        // How many of those wanted the path limit alone leaves, said where the room keeps none of them out.
        var limited = within < wanted ? $"{within} of {wanted} workers: {beyond}" : null;

        if (room is null)
        {
            return WorkerPlan.Running(within, limited, $"the room for its workers could not be measured: {unmeasured ?? "no reason was given"}");
        }

        var taken = 0L;
        var fitting = 0;

        while (fitting < within && taken + needs[fitting] <= room.FreeBytes)
        {
            taken += needs[fitting];
            fitting++;
        }

        if (fitting == within)
        {
            return WorkerPlan.Running(within, limited);
        }

        var reckoned = source is null ? null : $"each worker's build {source}";
        var past = within < wanted ? $"; and {beyond}" : string.Empty;

        return fitting == 0
            ? WorkerPlan.None(room.Against(DiskSpace.Needs("its first worker", needs[0], reckoned), beside: past))
            : WorkerPlan.Running(
                fitting,
                $"{fitting} of {wanted} workers: " + room.Against(DiskSpace.Needs($"running {fitting + 1}", taken + needs[fitting], reckoned), beside: past));
    }
}
