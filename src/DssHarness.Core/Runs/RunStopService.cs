using System.Globalization;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Runs;

/// <summary>
/// <c>stop-run &lt;run&gt;</c>: stops a running build, test, run or mutation sweep from another terminal - asked of its
/// dispatcher, which takes the request at its next look and stops as an interrupt stops it, and waited for until it has
/// given up its records; or, with <c>--now</c>, its process ended where it stands, with all it started.
/// </summary>
/// <remarks>
/// The run is found by its id in the tree it ran in - its records are that tree's own - and its dispatcher by the owner
/// file it keeps beside them while it runs. A run is said to have stopped only where its dispatcher took the stop
/// (<see cref="RunStops.TakenBack"/>) or was ended: one that ended on its own first is nothing this command stopped. Every
/// exit names the stop record that stands, in its text and in its document (<see cref="RunStopReports"/>): the request
/// while it is asked, the run's record of it once taken, and never a request the command took back.
/// </remarks>
/// <param name="contextLoader">Finds the tree the run ran in, and its layout.</param>
/// <param name="logOwnership">The run's owner, and an abandoned one released.</param>
/// <param name="stops">Asks the stop, and takes it back.</param>
/// <param name="identity">This process, and how the dispatcher is told and described.</param>
/// <param name="fileSystem">Whether the run's records are there.</param>
/// <param name="clock">How long the stop is waited for.</param>
public sealed class RunStopService(
    IHarnessContextLoader contextLoader,
    LogOwnership logOwnership,
    RunStops stops,
    IProcessIdentity identity,
    IFileSystem fileSystem,
    TimeProvider? clock = null)
{
    /// <summary>The command's name.</summary>
    public const string CommandName = "stop-run";

    /// <summary>How long the dispatcher is given to stop and give up its records once asked, and its process to go once ended.</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(60);

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly LogOwnership _logOwnership = logOwnership;
    private readonly RunStops _stops = stops;
    private readonly IProcessIdentity _identity = identity;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Stops the run <paramref name="run"/> names, in the tree <paramref name="startDirectory"/> is in.</summary>
    /// <param name="startDirectory">A directory of the tree the run ran in.</param>
    /// <param name="run">The run's id, as its first line said it.</param>
    /// <param name="now">Whether its dispatcher's process is ended where it stands, without asking it.</param>
    /// <param name="json">Whether the answer is the command's document.</param>
    /// <param name="cancellationToken">Stops the waiting; a stop asked stays asked.</param>
    public async Task<CommandOutcome> StopAsync(string startDirectory, string run, bool now, bool json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (!RunId.TryParse(run, out var id))
        {
            return RunStopReports.Render(
                CommandOutcome.Usage($"'{run}' is no run: a run is named as the line its command began with names it, 'run 20261022-035935-e6571255'"),
                report: null,
                json);
        }

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var runDirectory = context.Layout.RunDirectory(id.Value);
        var report = new RunStopReport(id.Value, runDirectory);

        if (_logOwnership.Owner(runDirectory) is not { } owner)
        {
            // Nothing holds it: what an ended run's stop left behind it goes with it.
            _stops.Forget(runDirectory, CommandName);

            return RunStopReports.Render(
                _fileSystem.DirectoryExists(runDirectory)
                    ? CommandOutcome.Refused($"run {id.Value} has ended: nothing holds '{runDirectory}', so there is nothing to stop")
                    : CommandOutcome.Refused(
                        $"no run {id.Value} was begun in '{context.Layout.RunsDirectory}': a run's records are kept in the tree it ran in, so stop it from there ('--directory')"),
                report,
                json);
        }

        report = report with { Dispatcher = owner.Describe(_identity) };

        if (!_identity.Stands(owner.Machine, owner.ProcessId, owner.ProcessStamp))
        {
            return Abandoned(report, json);
        }

        return now
            ? await EndNowAsync(owner, report, json, cancellationToken).ConfigureAwait(false)
            : await AskAsync(report, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The run's dispatcher has ended without giving up its records - killed, or stopped with its machine - so there is
    /// nothing to stop: its claim released here, as the next run in its tree would release it, with any stop asked of it.
    /// </summary>
    private CommandOutcome Abandoned(RunStopReport report, bool json)
    {
        var released = ReleaseClaim(report.RunDirectory);
        var left = StillAsked(report);

        return RunStopReports.Render(
            CommandOutcome.Refused(
                $"run {report.Run}'s dispatcher ({report.Dispatcher}) has ended without giving up its records, so there is nothing to stop; {released}",
                left.Record is { } record ? [$"stop: {record}"] : []),
            left,
            json);
    }

    /// <summary>
    /// The report once the stop it asked was taken back: the request is named only where it could not be removed, and
    /// stays, as said where that was met - never a file the command has removed.
    /// </summary>
    private RunStopReport StillAsked(RunStopReport report)
        => report.Record is { } file && _fileSystem.KindOf(file) != PathKind.None ? report : report with { Record = null };

    /// <summary>
    /// Releases the claim of a run whose dispatcher ended without giving it up, with any stop asked of it
    /// (<see cref="RunStops.ReleaseAbandoned"/>), and says whether it is gone: one that could not be judged or removed is
    /// said where it was met, and stays.
    /// </summary>
    private string ReleaseClaim(string runDirectory)
    {
        _ = _stops.ReleaseAbandoned(runDirectory, CommandName);

        return _fileSystem.KindOf(LogOwnership.OwnerFile(runDirectory)) == PathKind.None
            ? "its claim was released"
            : "its claim could not be released, as said above";
    }

    /// <summary>The stop asked of the dispatcher, and waited for until it has given up its records.</summary>
    private async Task<CommandOutcome> AskAsync(RunStopReport report, bool json, CancellationToken cancellationToken)
    {
        var runDirectory = report.RunDirectory;
        _stops.Ask(runDirectory);

        var file = RunStops.FileOf(runDirectory);
        report = report with { Record = file };
        var until = _clock.GetUtcNow() + StopWait;

        while (_clock.GetUtcNow() < until)
        {
            var owner = _logOwnership.Owner(runDirectory);

            if (owner is not null && !_identity.Stands(owner.Machine, owner.ProcessId, owner.ProcessStamp))
            {
                // Ended without giving its records up - killed, or stopped with its machine - while it was waited for: its
                // claim is released here, as the next run in its tree would release it, and it stopped only where it took
                // the stop first.
                if (!_stops.TakenBack(runDirectory, CommandName))
                {
                    return Abandoned(report, json);
                }

                var record = RunStops.TakenFileOf(runDirectory);

                return RunStopReports.Render(
                    CommandOutcome.Ok(
                        $"run {report.Run} stopped: its dispatcher ({report.Dispatcher}) took the stop and ended without giving up its records; {ReleaseClaim(runDirectory)}",
                        [$"logs: {runDirectory}", $"stop: {record}"]),
                    report with { Stopped = true, Record = record },
                    json);
            }

            if (owner is null)
            {
                // Gone: stopped only where it took a stop - this one, or another asked beside it - as the run's records
                // say. A request it never took is taken back, for nothing will take it now.
                var taken = RunStops.TakenFileOf(runDirectory);

                if (_stops.TakenBack(runDirectory, CommandName))
                {
                    return RunStopReports.Render(
                        CommandOutcome.Ok(
                            $"run {report.Run} stopped: its dispatcher ({report.Dispatcher}) took the stop and gave up its records",
                            [$"logs: {runDirectory}", $"stop: {taken}"]),
                        report with { Stopped = true, Record = taken },
                        json);
                }

                var left = StillAsked(report);

                return RunStopReports.Render(
                    CommandOutcome.Refused(
                        $"run {report.Run} ended before its dispatcher ({report.Dispatcher}) took the stop, so nothing was stopped; "
                            + (left.Record is null ? "the stop was taken back" : "the stop could not be taken back, as said above"),
                        left.Record is { } request ? [$"logs: {runDirectory}", $"stop: {request}"] : [$"logs: {runDirectory}"]),
                    left,
                    json);
            }

            try
            {
                await Task.Delay(StopCadence.WatchEvery, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Said as it stands: taken, the run is stopping, and the run's record says so; not yet, the stop stays
                // asked for the dispatcher's next look.
                return _stops.IsTaken(runDirectory, CommandName)
                    ? RunStopReports.Render(
                        CommandOutcome.Failed(
                            HarnessExit.Cancelled,
                            $"waiting for run {report.Run} to stop was cancelled; its dispatcher took the stop and is stopping",
                            [$"stop: {RunStops.TakenFileOf(runDirectory)}"]),
                        report with { Record = RunStops.TakenFileOf(runDirectory) },
                        json)
                    : RunStopReports.Render(
                        CommandOutcome.Failed(
                            HarnessExit.Cancelled,
                            $"waiting for run {report.Run} to stop was cancelled; the stop stays asked, and its dispatcher takes it at its next look",
                            [$"stop: {file}"]),
                        report,
                        json);
            }
        }

        var waited = StopWait.TotalSeconds.ToString(CultureInfo.InvariantCulture);

        // Taken, the dispatcher is still stopping - a set-up's down line among what it waits for - and --now would end it
        // part way through; not yet taken, it has not looked, and the stop stays asked.
        return _stops.IsTaken(runDirectory, CommandName)
            ? RunStopReports.Render(
                CommandOutcome.Refused(
                    $"run {report.Run}'s dispatcher ({report.Dispatcher}) took the stop and is still stopping after {waited} s",
                    [$"{CommandName} {report.Run} --now ends its process where it stands, part way through its stop", $"stop: {RunStops.TakenFileOf(runDirectory)}"]),
                report with { Record = RunStops.TakenFileOf(runDirectory) },
                json)
            : RunStopReports.Render(
                CommandOutcome.Refused(
                    $"run {report.Run}'s dispatcher ({report.Dispatcher}) did not take the stop within {waited} s; the stop stays asked",
                    [$"{CommandName} {report.Run} --now ends its process where it stands", $"stop: {file}"]),
                report,
                json);
    }

    /// <summary>
    /// <c>--now</c>: the dispatcher's process ended where it stands, with all it started (<see cref="ProcessHolders.EndWhereItStandsAsync"/>),
    /// then its claim released, as the next run in its tree would release it.
    /// </summary>
    private async Task<CommandOutcome> EndNowAsync(LogOwner owner, RunStopReport report, bool json, CancellationToken cancellationToken)
    {
        var ending = await _identity.EndWhereItStandsAsync(owner.Machine, owner.ProcessId, owner.ProcessStamp, StopWait, cancellationToken).ConfigureAwait(false);

        if (ending.NothingMoreToDo($"run {report.Run}'s dispatcher", report.Dispatcher!, "owner file", $"{CommandName} {report.Run}", StopWait) is { } nothingMore)
        {
            return RunStopReports.Render(nothingMore, report, json);
        }

        if (ending.How == ProcessEnded.AlreadyGone)
        {
            return Abandoned(report, json);
        }

        return RunStopReports.Render(
            CommandOutcome.Ok(
                $"ended run {report.Run}'s dispatcher ({report.Dispatcher}) where it stood, with all it started; {ReleaseClaim(report.RunDirectory)}",
                [$"what it left is what a killed run leaves: its ledger was never written, and its logs are at '{report.RunDirectory}'"]),
            report with { Stopped = true },
            json);
    }
}
