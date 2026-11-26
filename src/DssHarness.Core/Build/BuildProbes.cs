using DssHarness.Core.Processes;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Build;

/// <summary>
/// The one way a build's probe runs - a compiler asked its version, ninja asked its records or its dead outputs: to its
/// end, however long that takes, told every <see cref="Waits.SaidEvery"/> while it lasts, and ended only by the caller's
/// token - the build's own, which its step's bound and a stop cancel. A probe is never given a wall-clock budget of its
/// own: a compiler on a cold disk, or ninja reading the records of a build of forty thousand objects, is slow, not hung,
/// and a probe cut off at a number would say nothing true of the build it was to read.
/// </summary>
internal static class BuildProbes
{
    /// <summary>
    /// Runs <paramref name="request"/> to its end, telling <paramref name="told"/> where the wait stands as
    /// <paramref name="subject"/>.
    /// </summary>
    /// <param name="runner">Runs the program.</param>
    /// <param name="request">The probe, which sets no timeout of its own.</param>
    /// <param name="subject">What is waited for, as a line names it after <c>waits for</c>: <c>ninja's dependency records</c>.</param>
    /// <param name="what">What ran, as a line names it before what a stop of it could not reach or end.</param>
    /// <param name="say">
    /// Says, through the output of the caller that owns the operation, what a stop of the probe could not reach or end
    /// (<see cref="UnreachedSaying"/>).
    /// </param>
    /// <param name="told">Told where the wait stands, every <see cref="Waits.SaidEvery"/> it lasts; nothing where null.</param>
    /// <param name="clock">Times the wait.</param>
    /// <param name="cancellationToken">Ends the probe.</param>
    /// <returns>What the program did.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> ended the probe.</exception>
    public static async Task<ProcessResult> RunAsync(
        IProcessRunner runner,
        ProcessRequest request,
        string subject,
        string what,
        Action<string> say,
        Action<WaitTold>? told,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(clock);

        var ended = await Waits.ForAsync(
                token => runner.RunAsync(request, token).SayingUnreached(what, say),
                new WaitWatch(subject, told ?? (_ => { }), Bound: null, clock),
                cancellationToken)
            .ConfigureAwait(false);

        return ended.Kind == WaitEndKind.Done ? ended.Value! : throw new OperationCanceledException(cancellationToken);
    }
}
