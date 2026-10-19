using DssHarness.Core.Platform;

namespace DssHarness.Core.Processes;

/// <summary>
/// Ends a session a contained child led on Linux and macOS that the process which started it left running - killed before
/// it could end it - the way that process ends one: every process still in the session, and while its leader stands every
/// one in its tree, asked to end, and what still stands once the grace is spent made to.
/// </summary>
/// <remarks>
/// Windows has no such session to end: a contained child there is held in a job that ends every member when the last
/// handle to it closes, the killed process's among them, and nothing is recorded for one to be ended here.
/// </remarks>
/// <param name="platform">The system this runs on.</param>
/// <param name="table">The machine's process table, which the session's members are read from.</param>
public sealed class LeftSessions(IHostPlatform platform, IProcessTable table)
{
    private readonly IHostPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    private readonly IProcessTable _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>Ends the session <paramref name="leader"/> led, with everything in it.</summary>
    /// <param name="leader">The id of the session's leader, which is the session's.</param>
    /// <returns>What could not be reached, and why, or <see langword="null"/> where everything in the session was.</returns>
    /// <exception cref="PlatformNotSupportedException">This is Windows, whose jobs end their children with their owner.</exception>
    public Task<string?> EndAsync(int leader)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leader, 1);

        if (_platform.Current == PlatformId.Windows)
        {
            throw new PlatformNotSupportedException(
                $"session {leader} cannot be ended on Windows, whose contained children are held in a job that ends them with the process that held it, so none is ever left running for a sweep to end");
        }

        return Containment.EndSessionAsync(leader, _table.ReadAsync, ProcessRunner.SessionGrace);
    }
}
