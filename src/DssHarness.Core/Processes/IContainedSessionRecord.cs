namespace DssHarness.Core.Processes;

/// <summary>
/// Where a contained child is recorded while it runs, where its container would outlive the process that started it: on
/// Linux and macOS its session, which nothing ends when that process is killed, so that a later run of the tool over the
/// same records ends what a killed one left. A job object ends with its last handle, so on Windows nothing is recorded.
/// </summary>
/// <remarks>
/// The process runner, which alone knows the session and when it ends, calls it: once the child is placed in its session
/// and before it is said to have started, and again once the runner has ended the session. What the record is, and where
/// it is kept, is its writer's.
/// </remarks>
public interface IContainedSessionRecord
{
    /// <summary>Records the session <paramref name="leader"/> leads, running <paramref name="program"/>, written whole.</summary>
    /// <param name="leader">The child's process id, which is its session's.</param>
    /// <param name="program">The program the child runs, by its resolved path.</param>
    /// <exception cref="IOException">The record could not be written: the child is ended, and is a start that failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The record could not be written, for the same end.</exception>
    void Placed(int leader, string program);

    /// <summary>Takes the record back, once the process that started the child has ended its session.</summary>
    void Ended();
}
