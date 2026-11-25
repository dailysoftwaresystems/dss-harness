namespace DssHarness.Core.Processes;

/// <summary>
/// The one reading of what a child's end could not reach or end for the caller that says it: the stop's own data, worded
/// once by the process runner - in a result at the run's own end (<see cref="ProcessResult.Uncontained"/>), in the stop a
/// caller that gave up is thrown in place of one (<see cref="ProcessStoppedException.Uncontained"/>) - and said by the
/// caller that owns the operation's output, with its own context, once: a leg's phase under its leg, a probe under the
/// command it serves, a command a host ran under the leg or the command that ran it. A caller with no output of its own for
/// the operation is handed its caller's (<c>say</c>), and names what it ran. Nothing else says it, so nothing says it twice.
/// </summary>
public static class UnreachedSaying
{
    /// <summary>
    /// <paramref name="run"/>'s result, with what its child's end could not reach or end said through
    /// <paramref name="say"/> as <c>{what}: {what could not be reached or ended}</c>, however the run ended: from the
    /// result, or from the stop thrown in its place (<see cref="SayingStopUnreached"/>), which then goes on to the caller as
    /// it came. Read where the run is awaited, inside any wait over it, which ends at the caller's token without reading
    /// the stop.
    /// </summary>
    /// <param name="run">The run: a child of the process runner, or a command a host ran through it.</param>
    /// <param name="what">What ran, as the line names it: a leg, <c>'vswhere.exe'</c>, <c>'git fetch origin'</c>.</param>
    /// <param name="say">Says the line through the output of the caller that owns the operation.</param>
    /// <exception cref="ProcessStoppedException">The caller stopped the run; what the stop could not reach or end was said first.</exception>
    public static async Task<ProcessResult> SayingUnreached(this Task<ProcessResult> run, string what, Action<string> say)
    {
        var result = await run.SayingStopUnreached(what, say).ConfigureAwait(false);

        if (result.Uncontained is { } left)
        {
            say($"{what}: {left}");
        }

        return result;
    }

    /// <summary>
    /// <paramref name="run"/>'s result as it came, with what its child's end could not reach or end said through
    /// <paramref name="say"/> only where the caller stopped the run and no result is made: for a caller whose result
    /// carries the rest into a report of its own - an isolation's probe, whose trial its end could not reach is one that
    /// proved nothing.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="what">What ran, as the line names it.</param>
    /// <param name="say">Says the line through the output of the caller that owns the operation.</param>
    /// <exception cref="ProcessStoppedException">The caller stopped the run; what the stop could not reach or end was said first.</exception>
    public static async Task<ProcessResult> SayingStopUnreached(this Task<ProcessResult> run, string what, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(what);
        ArgumentNullException.ThrowIfNull(say);

        try
        {
            return await run.ConfigureAwait(false);
        }
        catch (ProcessStoppedException ex) when (ex.Uncontained is { } unreached)
        {
            say($"{what}: {unreached}");
            throw;
        }
    }
}
