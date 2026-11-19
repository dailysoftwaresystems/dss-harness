using DssHarness.Core.Waiting;

namespace DssHarness.Core.Agents.ClaudeCode;

/// <summary>
/// How finding, installing and updating Claude Code wait on what they start - npm, node, the program's own version, the
/// following of a path's links, the change itself: to its end, however long it takes - an install through a slow proxy
/// may take twenty minutes - told every <see cref="Waits.SaidEvery"/> as every wait of the harness is
/// (<see cref="Waits.ForAsync"/>), and ended by the caller's token alone, never by a time of the tool's own.
/// </summary>
internal static class ClaudeCodeWaits
{
    /// <summary>
    /// <paramref name="work"/>'s answer, awaited to its end and told as <paramref name="subject"/> while it lasts; the
    /// caller's cancellation raised where it ended the wait.
    /// </summary>
    /// <typeparam name="T">What the work answers.</typeparam>
    /// <param name="work">The work, given the token that cancels it.</param>
    /// <param name="subject">What is waited for, as a line names it after <c>waits for</c>: <c>'claude' to answer its --version</c>.</param>
    /// <param name="tell">Told each line of the wait; nothing is told where none is given.</param>
    /// <param name="clock">Measures the wait.</param>
    /// <param name="cancellationToken">Ends the wait, cancelling the work.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> ended the wait.</exception>
    public static async Task<T> ToItsEndAsync<T>(Func<CancellationToken, Task<T>> work, string subject, Action<WaitTold>? tell, TimeProvider clock, CancellationToken cancellationToken)
    {
        var ended = await Waits.ForAsync(work, new WaitWatch(subject, tell ?? (_ => { }), Bound: null, clock), cancellationToken).ConfigureAwait(false);

        // No bound is given, so a wait the work did not end is the caller's token's.
        if (ended.Kind != WaitEndKind.Done)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return ended.Value!;
    }
}
