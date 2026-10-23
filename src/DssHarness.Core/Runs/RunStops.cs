using System.Globalization;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Runs;

/// <summary>
/// A stop asked of a running leg run by another process (<c>stop-run</c>): asked in <c>&lt;runs&gt;/&lt;run&gt;.stop.json</c>,
/// beside the run's directory and its owner file, and once its dispatcher takes it, recorded taken in the run's own
/// directory (<see cref="RunStops.TakenFileOf"/>).
/// </summary>
/// <param name="Schema">The record's version: 1.</param>
/// <param name="AskedUtc">When the stop was asked.</param>
/// <param name="Machine">The machine the process that asked runs on.</param>
/// <param name="ProcessId">The process that asked.</param>
/// <param name="ProcessStamp">What tells the process that asked from a later one given its id, where its platform says; left out where it says none.</param>
/// <param name="TakenUtc">When the run's dispatcher took the stop: said by the run's record of it alone, never by a request.</param>
public sealed record RunStopRequest(int Schema, DateTimeOffset AskedUtc, string Machine, int ProcessId, string? ProcessStamp = null, DateTimeOffset? TakenUtc = null)
{
    /// <summary>The version this build writes and reads.</summary>
    public const int Version = 1;

    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => Schema != Version ? string.Create(CultureInfo.InvariantCulture, $"its schema is {Schema}, and this build reads {Version}")
            : ProcessId < 1 || string.IsNullOrWhiteSpace(Machine) ? "it names no process as the one that asked"
            : null;
}

/// <summary>
/// The stop another process asks of a running leg run - a build, a test, a run or a mutation sweep - and the run's watch
/// for it: the one writer and the one reader of <see cref="RunStopRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// The run's dispatcher looks for its request every <see cref="StopCadence.PollEvery"/> from the moment it claims its directory to the
/// moment it gives it up (<see cref="Watch"/>), takes it - recorded taken in the run's own directory, and the request
/// removed - and stops as an interrupt stops it: each leg stopped where it stands, the ledger written with what was
/// reached, and the run ending interrupted. The record is the run's, not an asker's: every process that asked reads it
/// once the run's owner has gone (<see cref="TakenBack"/>), however many asked and whenever, and a run that ended on its
/// own before its next look is never said to have been stopped. The run's id is its own, so a request is never one for a
/// later run.
/// </para>
/// <para>
/// No request is left behind the run: one its dispatcher never took - asked too late, or again once it had taken one -
/// is taken back by its end (<see cref="RunStopWatch.EndThenAsync"/>), by a process that asked once the owner has gone,
/// and with the claim of a run found abandoned, whoever finds it so (<see cref="ReleaseAbandoned"/>,
/// <see cref="ReleaseAbandonedBeside"/>): an abandoned run's claim and its stop go together.
/// </para>
/// <para>
/// A request is read strictly, and one that does not read as one is said and removed, and stops nothing: a run is never
/// stopped on a record nobody can say who wrote.
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads, writes and removes the requests.</param>
/// <param name="identity">This process, the one that asks, and how another is described and told.</param>
/// <param name="output">Says a request that could not be read, recorded taken or removed, and the one a run takes.</param>
/// <param name="ownership">The runs' claims on their directories, which an abandoned run's stop goes with.</param>
/// <param name="clock">When a request is asked and taken, and how long a dispatcher waits between looks.</param>
public sealed class RunStops(IFileSystem fileSystem, IProcessIdentity identity, IHarnessOutput output, LogOwnership ownership, TimeProvider? clock = null)
{
    /// <summary>What a request's file adds to its run directory's path.</summary>
    public const string Suffix = ".stop.json";

    /// <summary>The run's record, in its own directory, that its dispatcher took a stop asked of it.</summary>
    public const string TakenFileName = "stopped.json";

    /// <summary>What a request is, as a line names it.</summary>
    public const string RecordKind = "the stop asked of a run";

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    private readonly IProcessIdentity _identity = identity ?? throw new ArgumentNullException(nameof(identity));

    private readonly IHarnessOutput _output = output ?? throw new ArgumentNullException(nameof(output));

    private readonly LogOwnership _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Where the stop asked of the run kept at <paramref name="runDirectory"/> is written: beside it, as its owner file is.</summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    public static string FileOf(string runDirectory) => DirectoryClaims.OwnerFile(runDirectory, Suffix);

    /// <summary>Where the run kept at <paramref name="runDirectory"/> records the stop its dispatcher took: among its records.</summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    public static string TakenFileOf(string runDirectory) => Path.Combine(runDirectory, TakenFileName);

    /// <summary>Asks the run kept at <paramref name="runDirectory"/> to stop, by this process: the request written whole.</summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    /// <returns>The request written.</returns>
    /// <exception cref="HarnessException">It could not be written: refused, naming the file.</exception>
    public RunStopRequest Ask(string runDirectory)
    {
        var request = new RunStopRequest(RunStopRequest.Version, _clock.GetUtcNow(), _identity.CurrentMachine, _identity.CurrentId, _identity.Current, TakenUtc: null);

        Write(FileOf(runDirectory), request, "Until it can be, the run cannot be asked to stop.");

        return request;
    }

    /// <summary>
    /// Takes the request for the run kept at <paramref name="runDirectory"/>: recorded taken among the run's records, for
    /// every process that asked to read, and the request removed. <see langword="null"/> where none is there, and where one
    /// does not read as a request, which is said, removed and stops nothing.
    /// </summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    /// <param name="command">The command taking it, which its lines name.</param>
    public RunStopRequest? Take(string runDirectory, string command)
    {
        var file = FileOf(runDirectory);

        if (Read(file, command) is not { } request)
        {
            return null;
        }

        var taken = request with { TakenUtc = _clock.GetUtcNow() };

        try
        {
            Write(TakenFileOf(runDirectory), taken, "The run stops all the same, and a process that asked hears that nothing was stopped.");
        }
        catch (HarnessException unrecorded)
        {
            _output.Warn(command, unrecorded.Message);
        }

        Remove(file, command);
        _output.Warn(command, $"{Asker(request)} asked this run to stop ({RunStopService.CommandName}): its legs are stopped where they stand, as an interrupt stops them.");

        return taken;
    }

    /// <summary>
    /// What a process that asked the run kept at <paramref name="runDirectory"/> to stop reads once the run's owner has
    /// gone: whether its dispatcher took a stop, as the run's records say. A request nothing took is taken back, so none
    /// stays behind the run; the record stays, the run's own.
    /// </summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    /// <param name="command">The command taking it back, which its lines name.</param>
    public bool TakenBack(string runDirectory, string command)
    {
        Forget(runDirectory, command);

        return IsTaken(runDirectory);
    }

    /// <summary>Whether the run kept at <paramref name="runDirectory"/> took a stop, as its records say: read only, nothing taken back.</summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    public bool IsTaken(string runDirectory) => _fileSystem.KindOf(TakenFileOf(runDirectory)) == PathKind.File;

    /// <summary>
    /// Takes back a request for the run kept at <paramref name="runDirectory"/> that nothing will take any more - its
    /// dispatcher ended, or found abandoned - so none stays behind the run.
    /// </summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    /// <param name="command">The command taking it back, which its lines name.</param>
    /// <remarks>
    /// Never fails what calls it - a run's end, the release of an abandoned claim: a request that cannot be removed is
    /// said, and left for the next to find it.
    /// </remarks>
    public void Forget(string runDirectory, string command) => Remove(FileOf(runDirectory), command);

    /// <summary>
    /// Releases the claim of the run kept at <paramref name="runDirectory"/> where its dispatcher, of this machine, ended
    /// without giving it up - said as <see cref="LogOwnership.ReleaseAbandonedRun"/> says it - and with it takes back any stop
    /// asked of the run, which nothing will take now. <see langword="null"/>, and nothing done, where no such run holds it.
    /// </summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    /// <param name="command">The command releasing it, which its lines name.</param>
    public LogOwner? ReleaseAbandoned(string runDirectory, string command)
    {
        if (_ownership.ReleaseAbandonedRun(runDirectory) is not { } released)
        {
            return null;
        }

        Forget(runDirectory, command);

        return released;
    }

    /// <summary>
    /// Releases the claims of the runs found abandoned beside <paramref name="runDirectory"/>, as
    /// <see cref="LogOwnership.ReleaseAbandoned"/> does, each with any stop asked of it.
    /// </summary>
    /// <param name="runDirectory">The directory of the run that looks.</param>
    /// <param name="directoryOf">Where a run's records are kept, by its id.</param>
    /// <param name="command">The command releasing them, which its lines name.</param>
    /// <returns>The runs found abandoned.</returns>
    public IReadOnlyList<LogOwner> ReleaseAbandonedBeside(string runDirectory, Func<string, string> directoryOf, string command)
    {
        ArgumentNullException.ThrowIfNull(directoryOf);

        var released = _ownership.ReleaseAbandoned(runDirectory);

        foreach (var abandoned in released)
        {
            Forget(directoryOf(abandoned.RunId), command);
        }

        return released;
    }

    /// <summary>
    /// Starts looking for the request for the run kept at <paramref name="runDirectory"/>, every <see cref="StopCadence.PollEvery"/> by
    /// this one's clock, until the watch is ended: its <see cref="RunStopWatch.Stopping"/> is cancelled once one is taken.
    /// </summary>
    /// <param name="runDirectory">The run's directory of records.</param>
    /// <param name="command">The command running the run, which its lines name.</param>
    /// <param name="cancellationToken">Ends the watch, with the run.</param>
    public RunStopWatch Watch(string runDirectory, string command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return RunStopWatch.Start(this, runDirectory, command, (wait, token) => Task.Delay(wait, _clock, token), cancellationToken);
    }

    /// <summary>The process that asked <paramref name="request"/>, as a line names it, never naming another machine.</summary>
    /// <param name="request">The request.</param>
    public string Asker(RunStopRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _identity.Name(request.Machine, request.ProcessId);
    }

    /// <summary>
    /// The request in <paramref name="file"/>; <see langword="null"/> where none is there, and where one does not read as a
    /// request, which is said and removed.
    /// </summary>
    private RunStopRequest? Read(string file, string command)
    {
        if (!MachineWideFile.TryRead<RunStopRequest>(_fileSystem, file, out var request, out var unread))
        {
            _output.Warn(command, $"'{file}' is {RecordKind}, and does not read as one: {unread.Message.TrimEnd('.')}. It was removed, and stops nothing.");
            Remove(file, command);

            return null;
        }

        if (request?.Problem() is { } problem)
        {
            _output.Warn(command, $"'{file}' is {RecordKind}, and does not read as one: {problem}. It was removed, and stops nothing.");
            Remove(file, command);

            return null;
        }

        return request;
    }

    /// <summary>Writes <paramref name="request"/> whole to <paramref name="file"/>; refused, naming it, where it cannot be.</summary>
    private void Write(string file, RunStopRequest request, string until)
        => MachineWideFile.Written(
            $"The stop request '{file}'",
            until,
            () => JsonStateFile.Write(_fileSystem, file, request));

    /// <summary>Removes a request, where one is there; one that cannot be removed is said, and left for the next to find it.</summary>
    private void Remove(string file, string command)
    {
        try
        {
            _fileSystem.DeleteFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.Warn(command, $"'{file}', {RecordKind}, could not be removed: {ex.Message.TrimEnd('.')}.");
        }
    }
}

/// <summary>
/// A running dispatcher's watch for the stop another process asks of its run (<see cref="RunStops"/>): looks every
/// <see cref="StopCadence.PollEvery"/>, and once a request is taken cancels <see cref="Stopping"/>, which the run is stopped by.
/// </summary>
public sealed class RunStopWatch
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _done;
    private readonly Lock _gate = new();
    private readonly RunStops _stops;
    private readonly string _runDirectory;
    private readonly string _command;
    private Task _watching = Task.CompletedTask;
    private RunStopRequest? _taken;
    private bool _ended;

    private RunStopWatch(RunStops stops, string runDirectory, string command, CancellationToken cancellationToken)
    {
        _stops = stops;
        _runDirectory = runDirectory;
        _command = command;
        _done = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Stopping = _stopping.Token;
    }

    /// <summary>Cancelled once a request is taken; <see cref="Taken"/> then says whose.</summary>
    public CancellationToken Stopping { get; }

    /// <summary>The request the run was stopped by; <see langword="null"/> while none was taken.</summary>
    public RunStopRequest? Taken
    {
        get
        {
            lock (_gate)
            {
                return _taken;
            }
        }
    }

    /// <summary>Starts the watch, having looked once already.</summary>
    internal static RunStopWatch Start(RunStops stops, string runDirectory, string command, Func<TimeSpan, CancellationToken, Task> wait, CancellationToken cancellationToken)
    {
        var watch = new RunStopWatch(stops, runDirectory, command, cancellationToken);

        if (!watch.Look())
        {
            watch._watching = watch.WatchAsync(wait);
        }

        return watch;
    }

    /// <summary>
    /// Ends the watch - the run has ended - takes back a request it never took (<see cref="RunStops.Forget"/>), and only
    /// then runs <paramref name="giveUp"/>, which gives up the run's directory: so no stop is ever taken by a run whose
    /// directory is given up, and one a process that asked reads as taken is one this run took. Ended twice, it ends once,
    /// and gives up the directory each time it is asked to.
    /// </summary>
    /// <param name="giveUp">Gives up the run's directory.</param>
    public async Task EndThenAsync(Func<Task> giveUp)
    {
        ArgumentNullException.ThrowIfNull(giveUp);

        var first = false;

        lock (_gate)
        {
            if (!_ended)
            {
                _ended = first = true;
            }
        }

        try
        {
            if (first)
            {
                await _done.CancelAsync().ConfigureAwait(false);
                await _watching.ConfigureAwait(false);

                _stops.Forget(_runDirectory, _command);

                _done.Dispose();
                _stopping.Dispose();
            }
        }
        finally
        {
            // The directory is given up however the watch ended: a run never keeps its records claimed past its end.
            await giveUp().ConfigureAwait(false);
        }
    }

    private async Task WatchAsync(Func<TimeSpan, CancellationToken, Task> wait)
    {
        try
        {
            do
            {
                await wait(StopCadence.PollEvery, _done.Token).ConfigureAwait(false);
            }
            while (!Look());
        }
        catch (OperationCanceledException) when (_done.IsCancellationRequested)
        {
            // The run ended, or was interrupted: there is nothing left to stop.
        }
    }

    /// <summary>Looks for the request and takes it where it is there; whether the run is to stop.</summary>
    private bool Look()
    {
        lock (_gate)
        {
            if (_taken is not null)
            {
                return true;
            }

            if (_ended)
            {
                return false;
            }

            if (_stops.Take(_runDirectory, _command) is not { } request)
            {
                return false;
            }

            _taken = request;
        }

        _stopping.Cancel();

        return true;
    }
}
