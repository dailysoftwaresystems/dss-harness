using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Runs;

/// <summary>
/// What a run of legs reached: every line of its ledger, the code its caller exits with and the line it ends on, and
/// where its records are - the answer of a command that runs legs, in the terms its caller answers in.
/// </summary>
/// <param name="Command">The command the run reports under.</param>
/// <param name="Run">The run, begun before anything could refuse it.</param>
/// <param name="RunDirectory">Where the run keeps its records, once it has a directory; <see langword="null"/> before.</param>
/// <param name="Report">The ledger, every leg's line in it.</param>
/// <param name="Entries">The ledger's lines, one per leg.</param>
/// <param name="ExitCode">The code its caller exits with.</param>
/// <param name="Message">The line its caller ends on.</param>
/// <param name="Cancelled">Whether the run was interrupted before it finished.</param>
/// <param name="Unfinished">The legs that reached no verdict, by name.</param>
/// <param name="Details">What the table form says beneath the line.</param>
/// <param name="Stopped">Whether something other than the run's legs ended it, which its document says.</param>
/// <param name="MutationRecord">
/// The directory this run's verdicts of its mutation arms are recorded in, where it records them;
/// <see langword="null"/> for a command that records none.
/// </param>
public sealed record LegRunResult(
    string Command,
    RunId Run,
    string? RunDirectory,
    LedgerReport Report,
    IReadOnlyList<LedgerLine> Entries,
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
    /// marks the run whose logs another run owns, which a caller that can try again waits out.
    /// </summary>
    public OutcomeCause Cause { get; init; }

    /// <summary>
    /// The answer a command gives of this run: asked for data, the ledger is the whole of standard output whatever
    /// ended the run; as text, a table of what reached a verdict and the line it ends on.
    /// </summary>
    /// <param name="json">Whether the ledger was asked for as data.</param>
    /// <param name="shown">How the document's reader is told a path or a line of the harness's own.</param>
    /// <returns>The command's outcome.</returns>
    public CommandOutcome ToOutcome(bool json, Func<string, string>? shown = null)
        => json
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
