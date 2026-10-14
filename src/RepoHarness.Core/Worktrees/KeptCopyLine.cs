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
    private KeptCopyLine(string line, string? heldPath, LockRequest? asked, bool stops)
    {
        Line = line;
        HeldPath = heldPath;
        Asked = asked;
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

    /// <summary>Whether it keeps the removal from finishing until it is put right, as no waiting does.</summary>
    public bool Stops { get; }

    /// <summary>A line that names nothing in the removal's way: what goes with the worktree, or what was removed.</summary>
    /// <param name="line">The line.</param>
    public static KeptCopyLine Said(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        return new(line, null, null, stops: false);
    }

    /// <summary>A line naming a copy a run or a sweep holds, with what holds it.</summary>
    /// <param name="path">Where the copy is.</param>
    /// <param name="line">The line.</param>
    /// <param name="asked">What the run lock is asked for its holder, where the lock holds a copy of its family; <see langword="null"/> where a claim does.</param>
    public static KeptCopyLine Held(string path, string line, LockRequest? asked = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        return new(line, path, asked, stops: false);
    }

    /// <summary>A line naming what the removal cannot take as it stands: a copy that could not be told or removed, or copies that could not be looked for.</summary>
    /// <param name="line">The line.</param>
    public static KeptCopyLine Stopping(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        return new(line, null, null, stops: true);
    }

    /// <summary>
    /// The line its family says of <paramref name="left"/>, as what it was left as makes it: held, a copy a run or a sweep
    /// holds; not removed, what the removal cannot take as it stands; somebody's, only said - as the removal's verdict
    /// weighs each (<see cref="WorkersRemoval.Verdict"/>).
    /// </summary>
    /// <param name="left">The copy left.</param>
    /// <param name="line">The line its family says of it.</param>
    /// <param name="asked">What the run lock is asked for its holder, where the lock holds a copy of its family; <see langword="null"/> where a claim does.</param>
    public static KeptCopyLine Of(WorkerLeft left, string line, LockRequest? asked = null)
    {
        ArgumentNullException.ThrowIfNull(left);

        return left.As switch
        {
            WorkerLeftAs.Held => Held(left.Path, line, asked),
            WorkerLeftAs.NotRemoved => Stopping(line),
            _ => Said(line),
        };
    }
}
