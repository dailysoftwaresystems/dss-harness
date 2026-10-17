using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Runs;

/// <summary>
/// What a run of legs reached: every line of its ledger, the code its caller exits with and the line it ends on, and
/// where its records are - the answer of a command that runs legs, in the terms its caller answers in.
/// </summary>
/// <param name="Command">The command the run reports under.</param>
/// <param name="Run">
/// The run, begun before anything could refuse it; <see langword="null"/> for a command that keeps no runs - clean, which
/// writes nothing on a machine before it has removed what it came to remove.
/// </param>
/// <param name="RunDirectory">Where the run keeps its records, once it has a directory; <see langword="null"/> before.</param>
/// <param name="Report">The ledger, every leg's line in it.</param>
/// <param name="ExitCode">The code its caller exits with.</param>
/// <param name="Message">The line its caller ends on.</param>
/// <param name="Cancelled">Whether the run was interrupted before it finished.</param>
/// <param name="Unfinished">
/// The legs still running when the run stopped, by name; always empty where something other than the run's legs ended
/// it (<paramref name="Stopped"/>), which ends it before any leg is left running.
/// </param>
/// <param name="Details">What the table form says beneath the line.</param>
/// <param name="Stopped">Whether something other than the run's legs ended it, which its document says.</param>
/// <param name="MutationRecord">
/// The directory this run's verdicts of its mutation arms are recorded in, where it records them;
/// <see langword="null"/> for a command that records none.
/// </param>
public sealed record LegRunResult(
    string Command,
    RunId? Run,
    string? RunDirectory,
    LedgerReport Report,
    int ExitCode,
    string Message,
    bool Cancelled,
    IReadOnlyList<string> Unfinished,
    IReadOnlyList<string> Details,
    bool Stopped = false,
    string? MutationRecord = null)
{
    /// <summary>
    /// Why the run ended as it did, where its exit code and its lines cannot say: <see cref="OutcomeCause.LockHeld"/>
    /// where its code is <see cref="HarnessExit.Refused"/> because a lock another run holds refused its legs and nothing
    /// else went wrong - every leg of its ledger that did not pass <see cref="LegVerdict.RefusedLocked"/>, as
    /// <see cref="LedgerReport.CauseGiven"/> reads it - which a caller that can try again waits out;
    /// <see cref="OutcomeCause.None"/> otherwise, a run where a leg failed beside one a lock refused and a run whose
    /// logs another run owns (<see cref="LegExit.LogHeld"/>) among them, which is pointed elsewhere and never retried.
    /// </summary>
    public OutcomeCause Cause { get; init; }

    /// <summary>
    /// The answer a command gives of this run: asked for data, the ledger is the whole of standard output whatever
    /// ended the run; as text, a table of what reached a verdict and the line it ends on.
    /// </summary>
    /// <param name="json">Whether the ledger was asked for as data.</param>
    /// <param name="shown">
    /// How the document's reader is told a path or a line of the harness's own: the session's own output's
    /// <see cref="Output.IHarnessOutput.Shown"/>, so a host answering another machine writes its home as <c>~</c> in the
    /// ledger that machine reads, as in every line it writes.
    /// </param>
    /// <returns>The command's outcome.</returns>
    public CommandOutcome ToOutcome(bool json, Func<string, string> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        return json
            ? new CommandOutcome(ExitCode, Message)
            {
                Data = [Stopped
                    ? Report.ToJson(ExitCode, Message, RunDirectory, shown, Run, MutationRecord)
                    : Report.ToJson(Cancelled, Unfinished, RunDirectory, shown, Run, MutationRecord)],

                // The document is the whole answer; one more line would be one more thing for a reader to strip.
                Quiet = true,
                Cause = Cause,
            }
            : new CommandOutcome(ExitCode, Message, Details) { Cause = Cause };
    }
}
