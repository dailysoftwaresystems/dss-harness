using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// When a step that ended incomplete is run again, and how long it waits first: the plan's retry table, a pure
/// function of the ending and the attempt - one owner, beside the coordinator that waits by it. Each row's own waits
/// are the most a step is given: <c>retries</c> and the workflow's <c>maxRetries</c> can shorten a row and never
/// lengthen it, and a reasoning step is never retried at step level at all, for its client owns its transient errors.
/// </summary>
/// <remarks>
/// <para>
/// An incomplete step is one thing; a failed one is another. The table names the endings whose remedy is to try again
/// the same step - an incomplete run (21), a leg's inputs that moved or could not be measured (3), a build directory
/// another process used (4), a leg not admitted (7), and a refusal whose typed cause is a held lock (13 with
/// <see cref="OutcomeCause.LockHeld"/>, a dry run that met a run holding a tree the step takes among them). Every
/// other code - any other 13, and 1, 2, 5, 6, 8, 10, 11, 12, 14, 15, 20, 70 - is a failure or a refusal running again
/// does not change, and no retry is given it.
/// </para>
/// </remarks>
public static class FlowRetry
{
    /// <summary>Whether an ending with <paramref name="exit"/> and <paramref name="cause"/> is run again: the table's own rows, and nothing else.</summary>
    /// <param name="exit">The code the step's own work ended with.</param>
    /// <param name="cause">The typed cause, where the code alone cannot say which kind of refusal it was.</param>
    public static bool RunsAgain(int exit, OutcomeCause cause) => Wait(exit, cause, attempt: 0) is not null;

    /// <summary>
    /// How long the <paramref name="attempt"/>-th retry of an ending waits before it runs - <see cref="TimeSpan.Zero"/>
    /// for one that runs at once - or <see langword="null"/> where the table gives that ending no retry, or no wait past
    /// the ones it already gave.
    /// </summary>
    /// <param name="exit">The code the step's own work ended with.</param>
    /// <param name="cause">The typed cause, where the code alone cannot say which kind of refusal it was.</param>
    /// <param name="attempt">Which retry: 0 for the first, 1 for the second, and so on.</param>
    public static TimeSpan? Wait(int exit, OutcomeCause cause, int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);

        var waits = exit switch
        {
            // Incomplete: again at once, then after 5 s, 10 s.
            HarnessExit.Incomplete => [TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],

            // A leg's inputs moved or could not be measured: after 10 s, 30 s, 60 s - the tree settles.
            LegExit.InputsMoved => [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)],

            // A build directory another process used, or a leg not admitted: after 30 s, 60 s, 120 s.
            LegExit.Contended or LegExit.NotAdmitted => [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)],

            // A refusal whose only obstacle is a held lock: after 5 s and up to 60 s, doubling.
            HarnessExit.Refused when cause == OutcomeCause.LockHeld
                => [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60)],

            // Every other code: no retry.
            _ => Array.Empty<TimeSpan>(),
        };

        return attempt < waits.Length ? waits[attempt] : null;
    }
}
