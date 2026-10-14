using RepoHarness.Core.Execution;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Worktrees;

/// <summary>
/// One line of what removing the copies kept in a worktree - the mutation workers its sweeps made and the copies of it its
/// legs work in - says, in the family's own words, with what that line is to the deletion that would remove them. It is
/// exactly one of three: a copy a run or a sweep holds, which leaves the removal unfinished until that has ended; what
/// keeps the removal from finishing that no waiting ends - a copy that could not be told or removed, or copies that could
/// not be looked for - until it is put right; or what is only said, as a directory no sync made, which goes with the
/// worktree.
/// </summary>
public sealed record KeptCopyLine
{
    private KeptCopyLine(string line, string? heldPath, LockRequest? asked, LogOwner? claim, bool stops)
    {
        Line = line;
        HeldPath = heldPath;
        Asked = asked;
        Claim = claim;
        Stops = stops;
    }

    /// <summary>The line, as the deletion says it.</summary>
    public string Line { get; }

    /// <summary>Where the copy a run or a sweep holds is, where the line names one; <see langword="null"/> otherwise.</summary>
    public string? HeldPath { get; }

    /// <summary>
    /// What the run lock is asked for the run that holds the copy, as its removal asks it, where the lock is what holds a
    /// copy of its family - a leg copy; <see langword="null"/> where a claim of the family's own names the holder, as a
    /// sweep's claim names a mutation worker's, and where the line names no holder.
    /// </summary>
    public LockRequest? Asked { get; }

    /// <summary>
    /// What the claim of the family's own that holds the copy records of its holder - its machine, its process, its run
    /// and when it claimed the copy - where such a claim names the holder and could be read; <see langword="null"/> where
    /// the lock holds the copy (<see cref="Asked"/>), where the claim could not be read, and where the line names no holder.
    /// </summary>
    public LogOwner? Claim { get; }

    /// <summary>Whether it keeps the removal from finishing until it is put right, as no waiting does.</summary>
    public bool Stops { get; }

    /// <summary>A line that names nothing in the removal's way: what goes with the worktree, or what was removed.</summary>
    /// <param name="line">The line.</param>
    public static KeptCopyLine Said(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        return new(line, null, null, null, stops: false);
    }

    /// <summary>A line naming a copy a run or a sweep holds, with what holds it.</summary>
    /// <param name="path">Where the copy is.</param>
    /// <param name="line">The line.</param>
    /// <param name="asked">What the run lock is asked for its holder, where the lock holds a copy of its family; <see langword="null"/> where a claim does.</param>
    /// <param name="claim">What the claim that holds it records of its holder, where a claim holds it and could be read.</param>
    /// <exception cref="ArgumentException">Both the lock and a claim are said to hold it: a family's copy is held by one of them.</exception>
    public static KeptCopyLine Held(string path, string line, LockRequest? asked = null, LogOwner? claim = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        if (asked is not null && claim is not null)
        {
            throw new ArgumentException("A copy is held by the run lock or by a claim of its family's own, never both.", nameof(claim));
        }

        return new(line, path, asked, claim, stops: false);
    }

    /// <summary>A line naming what the removal cannot take as it stands: a copy that could not be told or removed, or copies that could not be looked for.</summary>
    /// <param name="line">The line.</param>
    public static KeptCopyLine Stopping(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        return new(line, null, null, null, stops: true);
    }

    /// <summary>
    /// What a deletion says of <paramref name="left"/>, a copy of a family kept in the worktree that its removal left, in
    /// the family's own words - the line it says now, and the line it says once the worktree went with it - as the removal's
    /// verdict on it makes both (<see cref="WorkerLeft.Verdict"/>, the one place that weighs what a copy was left as):
    /// refused-locked, a copy a run or a sweep holds, with what its claim records of the holder where a claim holds it;
    /// failed, what the removal cannot take as it stands; passed, somebody's, only said. The one place either line is
    /// worded, for every family.
    /// </summary>
    /// <param name="left">The copy left.</param>
    /// <param name="member">What a line calls one copy of its family, in the singular: <c>leg copy</c>, <c>mutation worker</c>.</param>
    /// <param name="again">The command that removes it once nothing keeps it, as a line quotes it.</param>
    /// <param name="asked">What the run lock is asked for its holder, where the lock holds a copy of its family; <see langword="null"/> where a claim does.</param>
    /// <exception cref="ArgumentOutOfRangeException">The verdict on it is none a copy left can have.</exception>
    public static (KeptCopyLine Told, string WentWithIt) Left(WorkerLeft left, string member, string again, LockRequest? asked = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        ArgumentException.ThrowIfNullOrWhiteSpace(again);

        var why = left.Why.TrimEnd('.');
        var forced = $"the {member} '{left.Path}' kept in it went with it, forced: {why}";

        return left.Verdict switch
        {
            LegVerdict.RefusedLocked => (Held(left.Path, $"left the {member} '{left.Path}' kept in it: {why}; {again} removes it once nothing holds it", asked, left.Claim), forced),
            LegVerdict.Failed => (Stopping($"left the {member} '{left.Path}' kept in it: {why}; {again} removes it once nothing stops it"), forced),
            LegVerdict.Passed => (Said($"left '{left.Path}', named as a {member} kept in it: {why}"), WorktreeReports.WentWithItsTree(left.Path)),
            var verdict => throw new ArgumentOutOfRangeException(nameof(left), verdict, "A copy left is held, in the removal's way, or only said; this build cannot tell which this one is."),
        };
    }
}
