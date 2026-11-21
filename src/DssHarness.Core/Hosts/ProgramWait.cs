using DssHarness.Core.Output;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Hosts;

/// <summary>
/// The one way a program the harness asks a question of - a probe of a host, a developer environment's set-up, a listing,
/// an install - is waited for: to its own end, however long that takes, told every <see cref="Waits.SaidEvery"/> while it
/// lasts, and ended only by the caller's token. No program is cut off at a number of the code's own: a vcvarsall that takes
/// four minutes on a cold disk, a ctest listing forty thousand tests or an install of two hours is waited for, and only the
/// person, by stopping the command, or the bound they configured, which reaches the program through that token, ends it.
/// </summary>
public static class ProgramWait
{
    /// <summary>
    /// <paramref name="run"/>'s answer, awaited to its end through <see cref="Waits.ForAsync"/>: told every
    /// <see cref="Waits.SaidEvery"/> it runs past, through <paramref name="tell"/>.
    /// </summary>
    /// <typeparam name="T">What the program's run answers.</typeparam>
    /// <param name="run">Runs the program, given the token that ends it.</param>
    /// <param name="subject">What is waited for, as a line names it after <c>waits for</c>: <c>vcvarsall.bat to end</c>.</param>
    /// <param name="tell">Told where the wait stands, each time as <see cref="WaitTold.Line"/> says it.</param>
    /// <param name="clock">The clock the wait is measured on; the machine's own where none is given.</param>
    /// <param name="cancellationToken">Ends the wait and the program.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> ended the wait.</exception>
    public static async Task<T> ToItsEndAsync<T>(
        Func<CancellationToken, Task<T>> run,
        string subject,
        Action<WaitTold> tell,
        TimeProvider? clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(tell);

        var ended = await Waits
            .ForAsync(run, new WaitWatch(subject, tell, Bound: null, clock ?? TimeProvider.System), cancellationToken)
            .ConfigureAwait(false);

        return ended.Kind == WaitEndKind.Done ? ended.Value! : throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>
    /// How a wait is told by an owner that speaks through the harness's output: as a progress line of
    /// <paramref name="command"/>, or not at all where there is no output to speak through.
    /// </summary>
    /// <param name="output">The harness's output, or <see langword="null"/>.</param>
    /// <param name="command">The command the line is said under.</param>
    public static Action<WaitTold> Told(IHarnessOutput? output, string command)
        => told => output?.Info(command, told.Line());
}
