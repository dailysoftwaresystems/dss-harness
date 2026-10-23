using DssHarness.Core.Platform;

namespace DssHarness.Core.Processes;

/// <summary>
/// Ends a session a contained child led on Linux and macOS, or the process group a child apart from the terminal led, that
/// the process which started it left running - killed before it could end it, or a group still holding what the child
/// left at its own end - the way that process ends a contained child's session: every
/// process still in the session or the group, and while its leader stands every one in its tree, asked to end, and what
/// still stands once the grace is spent made to.
/// </summary>
/// <remarks>
/// Windows has no such session or group to end: a child there is held in a job that ends every member when the last
/// handle to it closes, the killed process's among them, and nothing is recorded for one to be ended here.
/// </remarks>
/// <param name="platform">The system this runs on.</param>
/// <param name="table">The machine's process table, which the session's members are read from.</param>
public sealed class LeftSessions(IHostPlatform platform, IProcessTable table)
{
    private readonly IHostPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));

    private readonly IProcessTable _table = table ?? throw new ArgumentNullException(nameof(table));

    /// <summary>
    /// Ends the session or the group <paramref name="leader"/> led, with everything in it - its leader standing or not:
    /// no later process is given the id a session or a group still carries.
    /// </summary>
    /// <param name="leader">The id of its leader, which is the session's or the group's.</param>
    /// <param name="group">Whether it led a process group within its starter's session, rather than a session of its own.</param>
    /// <returns>Whether anything was still in it to end, and what could not be reached, and why.</returns>
    /// <exception cref="PlatformNotSupportedException">This is Windows, whose jobs end their children with their owner.</exception>
    public Task<HeldEnding> EndAsync(int leader, bool group)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(leader, 1);

        if (_platform.Current == PlatformId.Windows)
        {
            throw new PlatformNotSupportedException(
                $"{Noun(group)} {leader} cannot be ended on Windows, whose bound and contained children are held in a job that ends them with the process that held it, so none is ever left running for a sweep to end");
        }

        return Containment.EndSessionAsync(leader, _table.ReadAsync, ProcessRunner.SessionGrace, group);
    }

    /// <summary>What a child leads, as every line that names one calls it: a session of its own, or a process group within its starter's.</summary>
    /// <param name="group">Whether it is a process group.</param>
    public static string Noun(bool group) => group ? "process group" : "session";
}
