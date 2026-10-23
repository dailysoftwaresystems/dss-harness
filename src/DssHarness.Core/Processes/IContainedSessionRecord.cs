namespace DssHarness.Core.Processes;

/// <summary>
/// Where a child held by its session or its process group is recorded while it runs, where that would outlive the process
/// that started it: on Linux and macOS a contained child's session, and the group of a child apart from the terminal,
/// neither of which anything ends when that process is killed, so that a later run of the tool over the same records ends
/// what a killed one left. A job object ends with its last handle, so on Windows nothing is recorded.
/// </summary>
/// <remarks>
/// The process runner, which alone knows the session or the group and when it ends, calls it: once the child is placed in
/// it and before it is said to have started, and again once the child has ended and nothing is left in it - a contained
/// child's session the runner ends; a group still holding what the child left at its own end keeps its record. What the
/// record is, and where it is kept, is its writer's.
/// </remarks>
public interface IContainedSessionRecord
{
    /// <summary>Records the session or the group <paramref name="leader"/> leads, running <paramref name="program"/>, written whole.</summary>
    /// <param name="leader">The child's process id, which is its session's or its group's.</param>
    /// <param name="program">The program the child runs, by its resolved path.</param>
    /// <param name="group">Whether the child leads a process group within its starter's session, rather than a session of its own.</param>
    /// <exception cref="IOException">The record could not be written: the child is ended, and is a start that failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The record could not be written, for the same end.</exception>
    void Placed(int leader, string program, bool group);

    /// <summary>Takes the record back, once the child has ended and nothing is left in its session or its group.</summary>
    void Ended();
}
