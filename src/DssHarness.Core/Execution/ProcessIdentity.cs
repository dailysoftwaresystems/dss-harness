using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Execution;

/// <summary>
/// Whether the process that took something still holds it.
/// </summary>
/// <remarks>
/// Staleness is decided by liveness, never by a timeout: a timeout is a guess about how long honest
/// work takes, and it eventually breaks an honest run. A pid alone cannot answer it either, because
/// pids are recycled — on Windows a freed one was measured coming back after about a hundred
/// allocations — so something that tells one process from the next holder of its id is recorded
/// with it.
/// <para>
/// That something is never recomputed from the clock as it stands. This repository does not trust a clock to order
/// anything, for a reason it states elsewhere: one host it serves steps its clock forward by about
/// 25 seconds every few seconds. A start time that is recomputed from the current clock — which is
/// what Linux hands back, being ticks since boot added to a boot time derived from the clock as it
/// is now — moves for every live process the moment the clock steps, and every live holder then
/// reads as a recycled id at once.
/// </para>
/// </remarks>
public interface IProcessIdentity
{
    /// <summary>This process's id, recorded in whatever it takes.</summary>
    int CurrentId { get; }

    /// <summary>The machine this process runs on, recorded so a holder elsewhere is recognisable as elsewhere.</summary>
    string CurrentMachine { get; }

    /// <summary>
    /// What tells this process from another that inherits its id, or <see langword="null"/> when this
    /// platform would not say. Recorded beside the id. Compared exactly on Linux, whose stamp holds no
    /// clock; on Windows and macOS it is the start as the machine's local time, and a start read whole
    /// quarter hours apart, up to 26 hours, is the same start read under another time zone.
    /// </summary>
    string? Current { get; }

    /// <summary>
    /// Whether the process that recorded <paramref name="processId"/> and <paramref name="stamp"/> is
    /// still running.
    /// </summary>
    /// <param name="processId">The recorded id.</param>
    /// <param name="stamp">The recorded stamp, or <see langword="null"/> when none was recorded.</param>
    bool IsAlive(int processId, string? stamp);

    /// <summary>
    /// What tells the process now carrying <paramref name="processId"/> from another later given its id, as
    /// <see cref="Current"/> tells this one; <see langword="null"/> where nothing carries it or its stamp cannot be read.
    /// </summary>
    /// <param name="processId">The process's id.</param>
    string? StampOf(int processId);

    /// <summary>
    /// Whether the process carrying <paramref name="processId"/> now is the one recorded with <paramref name="stamp"/>:
    /// its stamp read, and the same start. Unlike <see cref="IsAlive"/>, never where its stamp cannot be read - for what
    /// acts on that process, not for what stands aside from it, taking another process for it is the worse mistake.
    /// </summary>
    /// <param name="processId">The recorded id.</param>
    /// <param name="stamp">The recorded stamp.</param>
    bool IsSame(int processId, string stamp);
}

/// <inheritdoc cref="IProcessIdentity"/>
/// <param name="platform">Which system this is, because only Linux publishes a clock-free start time.</param>
public sealed class ProcessIdentity(IHostPlatform platform) : IProcessIdentity
{
    private readonly IHostPlatform _platform = platform;

    private string? _current;
    private bool _read;

    public int CurrentId => Environment.ProcessId;

    public string CurrentMachine => Environment.MachineName;

    public string? Current
    {
        get
        {
            // Measured once. This process's own answer cannot change while it runs, and a later
            // reading that disagreed would be the very fault this class exists to rule out.
            if (!_read)
            {
                _current = Stamp(CurrentId);
                _read = true;
            }

            return _current;
        }
    }

    public bool IsAlive(int processId, string? stamp)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (stamp is not { Length: > 0 })
        {
            // Whoever recorded this could not be told apart from a later holder of its id, so the id
            // is all there is to go on. Reported as alive when something carries it: taking what a
            // live process holds is the worse mistake of the two.
            return Carried(processId);
        }

        var now = Stamp(processId);

        if (now is not { Length: > 0 })
        {
            // Nothing carries that id, or - off Windows, where every process's start can be read - this process may
            // not ask about it. Told apart below.
            return Carried(processId);
        }

        return string.Equals(now, stamp, StringComparison.Ordinal) || ZonesApart(now, stamp);
    }

    public string? StampOf(int processId) => processId > 0 ? Stamp(processId) : null;

    public bool IsSame(int processId, string stamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(stamp);

        return StampOf(processId) is { Length: > 0 } now && (string.Equals(now, stamp, StringComparison.Ordinal) || ZonesApart(now, stamp));
    }

    /// <summary>The finest step between two time zones' offsets: every zone's is a whole number of them.</summary>
    private static readonly TimeSpan ZoneStep = TimeSpan.FromMinutes(15);

    /// <summary>The farthest two time zones' offsets are apart: from UTC-12 to UTC+14.</summary>
    private static readonly TimeSpan ZonesSpan = TimeSpan.FromHours(26);

    /// <summary>
    /// Whether two start-time stamps are one start read under two time zones. Windows and macOS keep a process's start
    /// once, but hand it over converted to the local time of whoever asks, so a machine whose zone changed - a laptop
    /// set by where it is - reads a live process's start apart from the one recorded by exactly the difference between
    /// the two zones' offsets: a whole number of quarter hours, no more than a day and two hours. A later process given
    /// the same id would have had to start exactly that far apart, to the tick.
    /// </summary>
    private bool ZonesApart(string now, string recorded)
    {
        if (_platform.Current == PlatformId.Linux
            || !long.TryParse(now, NumberStyles.None, CultureInfo.InvariantCulture, out var nowTicks)
            || !long.TryParse(recorded, NumberStyles.None, CultureInfo.InvariantCulture, out var recordedTicks))
        {
            return false;
        }

        var apart = Math.Abs(nowTicks - recordedTicks);

        return apart <= ZonesSpan.Ticks && apart % ZoneStep.Ticks == 0;
    }

    /// <summary>
    /// What tells one process from the next holder of its id, or <see langword="null"/> when it could
    /// not be read.
    /// </summary>
    private string? Stamp(int processId)
        => _platform.Current == PlatformId.Linux ? LinuxStamp(processId) : StartTimeStamp(processId);

    /// <summary>
    /// The boot this machine is on and the tick within it that the process started. Neither moves
    /// when the clock does, so two readings of a live process always agree exactly.
    /// </summary>
    /// <remarks>
    /// The boot id is carried because ticks-since-boot start again at every boot, and a lock written
    /// before a restart would otherwise match a process started the same distance into the next one.
    /// </remarks>
    private static string? LinuxStamp(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/stat");

            if (ProcStat.Number(ProcStat.FieldsAfterName(stat), ProcStat.StartTicksField) is not { } ticks)
            {
                return null;
            }

            var boot = File.Exists(ProcStat.BootIdPath)
                ? File.ReadAllText(ProcStat.BootIdPath).Trim()
                : string.Empty;

            return $"{boot}:{ticks.ToString(CultureInfo.InvariantCulture)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The start time Windows and macOS record once, when the process is created, and never work out
    /// again, as its ticks. The runtime hands it over in the local time of the machine's zone as it is
    /// now, so a zone that changed moves it by whole quarter hours: <see cref="ZonesApart"/> reads that
    /// as the same start. Kept in that form, rather than turned back into an instant, so a stamp an
    /// earlier build recorded is still this build's, and one this build records is still an earlier one's.
    /// </summary>
    private string? StartTimeStamp(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            return process.StartTime.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // No process carries that id, so whoever recorded it has gone.
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // This user may not open it: a service's process, or one started elevated. On Windows its start is read
            // from the list Windows keeps of every process instead, so an id a dead holder left, taken since by such a
            // process, is never read as that holder. macOS keeps a process of another user's to itself, and its id is
            // only given out again once every other has been.
            return _platform.Current == PlatformId.Windows
                ? SystemProcessList.StartTicks(processId)?.ToString(CultureInfo.InvariantCulture)
                : null;
        }
    }

    /// <summary>
    /// Whether anything at all carries <paramref name="processId"/>, for when it cannot be told apart
    /// from a later holder of the same id.
    /// </summary>
    private static bool Carried(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
        {
            // It exists and this user may not ask it anything. Reported as alive: an unreadable
            // answer is never read as "it has gone", which would take what it holds.
            return true;
        }
    }
}

/// <summary>
/// What a record of the process that took something says of it, as this machine can tell: whether it still stands,
/// how a refusal names it, and what is said when it is reclaimed or taken.
/// </summary>
/// <remarks>
/// One place for what the run lock and a log directory's owner decide the same way - a holder on this machine stands
/// while its process runs, and one naming another machine until <c>--force-lock</c> takes it - so neither can come to
/// disagree with the other about which holder is gone. A machine's heavy-leg slots and the set-ups its legs hold are
/// told by <see cref="IProcessIdentity.IsAlive"/> alone, every entry of their records being this machine's whatever
/// name it carries; they name their holders, and say what was reclaimed, as the other two do.
/// </remarks>
public static class ProcessHolders
{
    /// <summary>
    /// Whether what the process <paramref name="processId"/> on <paramref name="machine"/> took still stands: on this
    /// machine, while that process runs; on another, until somebody takes it over, since nothing here can ask that
    /// machine whether it still runs.
    /// </summary>
    /// <param name="identity">This process, and how the liveness of another is told.</param>
    /// <param name="machine">The machine the record names.</param>
    /// <param name="processId">The process id it names.</param>
    /// <param name="stamp">The stamp it names, or <see langword="null"/> where it carries none.</param>
    public static bool Stands(this IProcessIdentity identity, string machine, int processId, string? stamp)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return !identity.IsHere(machine) || identity.IsAlive(processId, stamp);
    }

    /// <summary>Whether <paramref name="machine"/>, as a record names it, is the one this process runs on.</summary>
    /// <param name="identity">This process.</param>
    /// <param name="machine">The machine a record names.</param>
    public static bool IsHere(this IProcessIdentity identity, string machine)
    {
        ArgumentNullException.ThrowIfNull(identity);

        return string.Equals(machine, identity.CurrentMachine, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A holder as a line names it: <c>pid 12, run R, since 2026-09-30 16:32:14Z</c> - never by the name of the machine it
    /// runs on, which its record alone keeps: a host's lines are read on the machine that sent it work, and wherever that
    /// machine's output goes.
    /// </summary>
    /// <param name="processId">Its process id.</param>
    /// <param name="runId">Its run.</param>
    /// <param name="since">When it took what it holds, or asked for it.</param>
    public static string Describe(int processId, string runId, DateTimeOffset since)
        => string.Create(CultureInfo.InvariantCulture, $"{Pid(processId)}, {RunLine.Message(runId)}, since {since:u}");

    /// <summary>
    /// A process a record names, as a line names it: <c>pid 12</c> where the record names this machine, and
    /// <c>pid 12 on another machine</c> where it names another - never naming that machine, which its record alone keeps.
    /// </summary>
    /// <param name="identity">This process.</param>
    /// <param name="machine">The machine the record names.</param>
    /// <param name="processId">Its process id.</param>
    public static string Name(this IProcessIdentity identity, string machine, int processId)
        => $"{Pid(processId)}{identity.Elsewhere(machine)}";

    /// <summary>
    /// What a process's name adds where its record names another machine - <c> on another machine</c> - and nothing where
    /// it names this one: the one way every line says a process is another machine's, never naming that machine.
    /// </summary>
    /// <param name="identity">This process.</param>
    /// <param name="machine">The machine the record names.</param>
    public static string Elsewhere(this IProcessIdentity identity, string machine)
        => identity.IsHere(machine) ? string.Empty : " on another machine";

    /// <summary>A process of this machine by its id, as every line names one.</summary>
    private static string Pid(int processId) => string.Create(CultureInfo.InvariantCulture, $"pid {processId}");

    /// <summary>
    /// A holder whose record names <paramref name="machine"/>, as a line names it: as
    /// <see cref="Describe(int, string, DateTimeOffset)"/> does one on this machine, and one recorded on another said to be,
    /// without naming it: <c>pid 12 on another machine, run R, since 2026-09-30 16:32:14Z</c>.
    /// </summary>
    /// <param name="identity">This process.</param>
    /// <param name="machine">The machine the record names.</param>
    /// <param name="processId">Its process id.</param>
    /// <param name="runId">Its run.</param>
    /// <param name="since">When it took what it holds, or asked for it.</param>
    public static string Describe(this IProcessIdentity identity, string machine, int processId, string runId, DateTimeOffset since)
        => string.Create(CultureInfo.InvariantCulture, $"{identity.Name(machine, processId)}, {RunLine.Message(runId)}, since {since:u}");

    /// <summary>What is said when a dead holder's <paramref name="what"/> is taken back.</summary>
    /// <param name="what">What it held, as a line names it.</param>
    /// <param name="holder">The holder, as <see cref="Describe(IProcessIdentity, string, int, string, DateTimeOffset)"/> names it.</param>
    public static string Reclaimed(string what, string holder) => $"Reclaimed {what} from {holder}, which is no longer running.";

    /// <summary>What is said when <c>--force-lock</c> takes <paramref name="what"/> from a holder that may still run.</summary>
    /// <param name="what">What it held, as a line names it.</param>
    /// <param name="holder">The holder, as <see cref="Describe(IProcessIdentity, string, int, string, DateTimeOffset)"/> names it.</param>
    public static string TakenByForce(string what, string holder) => $"Taking {what} from {holder} because --force-lock was given.";

    /// <summary>
    /// What a refusal adds for a holder recorded on a machine by another name: nothing here can ask that machine whether
    /// it still runs - nor tell a machine from this one renamed since, as a Mac is by each network it joins - so the
    /// reader is told <c>--force-lock</c> is the answer once it does not, rather than waiting for ever.
    /// </summary>
    /// <param name="identity">This process.</param>
    /// <param name="machine">The machine the record names.</param>
    public static string ElsewhereNote(this IProcessIdentity identity, string machine)
        => identity.IsHere(machine)
            ? string.Empty
            : " (that machine - or this one, under an earlier name - cannot be asked whether it still runs; --force-lock takes it)";

    /// <summary>
    /// What a holder's description ends with where the record carries no stamp, which is one an older build wrote.
    /// Such a record is kept while anything at all carries its id, so it can outlive its run once that id comes back
    /// around to something else; saying so tells the reader <c>--force-lock</c> is the answer rather than waiting.
    /// </summary>
    /// <param name="stamp">The record's stamp.</param>
    public static string OlderBuildNote(string? stamp)
        => stamp is { Length: > 0 }
            ? string.Empty
            : " (recorded by an older build, so a reused id cannot be told from it; --force-lock takes it)";

    /// <summary>
    /// Ends the process a record names where it stands, with all it started, without asking it - a stop's <c>--now</c>,
    /// for a process that no longer answers: only one of this machine's, never this one, and only the process the record
    /// names by its start - then waits for it to go, at most <paramref name="wait"/>. One place for what every such stop
    /// decides, so no two can come to end a process the other would leave.
    /// </summary>
    /// <param name="identity">This process, and how another is told.</param>
    /// <param name="machine">The machine the record names.</param>
    /// <param name="processId">The process id it names.</param>
    /// <param name="stamp">The stamp it names, or <see langword="null"/> where it carries none.</param>
    /// <param name="wait">How long the process is given to go once it is ended.</param>
    /// <param name="cancellationToken">Stops the waiting: the caller's own interrupt, thrown as one, never read as the wait spent.</param>
    public static Task<ProcessEnding> EndWhereItStandsAsync(
        this IProcessIdentity identity,
        string machine,
        int processId,
        string? stamp,
        TimeSpan wait,
        CancellationToken cancellationToken = default)
        => identity.EndWhereItStandsWithAsync(machine, processId, stamp, wait, KillAndWaitAsync, cancellationToken);

    /// <summary>
    /// <see cref="EndWhereItStandsAsync(IProcessIdentity, string, int, string?, TimeSpan, CancellationToken)"/>, ending the
    /// process by <paramref name="killAndWait"/> - which ends it with its tree and waits for it to go until the token it is
    /// given is cancelled - a test's where it stands in for the machine's own.
    /// </summary>
    internal static async Task<ProcessEnding> EndWhereItStandsWithAsync(
        this IProcessIdentity identity,
        string machine,
        int processId,
        string? stamp,
        TimeSpan wait,
        Func<int, CancellationToken, Task> killAndWait,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(killAndWait);

        if (!identity.IsHere(machine))
        {
            return new ProcessEnding(ProcessEnded.Elsewhere);
        }

        if (processId == identity.CurrentId)
        {
            return new ProcessEnding(ProcessEnded.Itself);
        }

        if (stamp is not { } recorded || !identity.IsSame(processId, recorded))
        {
            return new ProcessEnding(identity.IsAlive(processId, stamp) ? ProcessEnded.CannotTell : ProcessEnded.AlreadyGone);
        }

        // A caller that has given up already ends nothing.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(wait);
            await killAndWait(processId, bound.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // It ended meanwhile.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProcessEnding(ProcessEnded.NotGone);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or AggregateException)
        {
            return new ProcessEnding(ProcessEnded.CouldNotEnd, ex.Message.TrimEnd('.'));
        }

        return new ProcessEnding(ProcessEnded.Ended);
    }

    /// <summary>The machine's own end of a process: it and its whole tree, then waited for until <paramref name="bound"/> is cancelled.</summary>
    private static async Task KillAndWaitAsync(int processId, CancellationToken bound)
    {
        using var process = System.Diagnostics.Process.GetProcessById(processId);
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(bound).ConfigureAwait(false);
    }

    /// <summary>
    /// What a stop's <c>--now</c> answers where ending its process left it nothing more to do
    /// (<see cref="EndWhereItStandsAsync"/>): nothing was done - the process is another machine's, this very one, or
    /// cannot be told to be the one recorded - or it was not ended, or did not go, and its record is kept. Null where it
    /// was ended or had already gone, which each stop goes on from in its own way. One wording for every such stop.
    /// </summary>
    /// <param name="ending">How ending it went.</param>
    /// <param name="holder">The process by its part, as a line names it: <c>run 20261022-035935-e6571255's dispatcher</c>.</param>
    /// <param name="name">The process as a line names it.</param>
    /// <param name="record">What records it as the holder, and is kept where it was not ended: <c>owner file</c>.</param>
    /// <param name="ask">The command that asks it to stop instead, without <c>--now</c>.</param>
    /// <param name="wait">How long it was given to go.</param>
    public static CommandOutcome? NothingMoreToDo(this ProcessEnding ending, string holder, string name, string record, string ask, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(ending);

        return ending.How switch
        {
            ProcessEnded.Elsewhere => CommandOutcome.Refused(
                $"{holder} runs on another machine ({name}); --now ends only a process of this machine. {ask} without --now asks it to stop, or end it on its own machine. Nothing was changed."),
            ProcessEnded.Itself => CommandOutcome.Refused($"{holder} is this very process; --now ends another process. Nothing was changed."),
            ProcessEnded.CannotTell => CommandOutcome.Refused(
                $"{name} cannot be told to be {holder} - its start cannot be read beside the one its {record} recorded - so it was not ended. Nothing was changed."),
            ProcessEnded.NotGone => CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"{name}, {holder}, was ended and had not gone within {wait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s; its {record} is kept"),
            ProcessEnded.CouldNotEnd => CommandOutcome.Failed(HarnessExit.CommandFailed, $"{name}, {holder}, could not be ended: {ending.Why}; its {record} is kept"),
            _ => null,
        };
    }
}

/// <summary>How ending a recorded process where it stood went (<see cref="ProcessHolders.EndWhereItStandsAsync"/>).</summary>
public enum ProcessEnded
{
    /// <summary>It was ended, and has gone.</summary>
    Ended,

    /// <summary>The record names another machine: nothing was done.</summary>
    Elsewhere,

    /// <summary>The record names this very process: nothing was done.</summary>
    Itself,

    /// <summary>The process the record names had already ended: nothing was done.</summary>
    AlreadyGone,

    /// <summary>A process carries the id, and cannot be told to be the one recorded - its start unreadable beside the record's: nothing was done.</summary>
    CannotTell,

    /// <summary>It was ended, and had not gone within the wait.</summary>
    NotGone,

    /// <summary>It could not be ended (<see cref="ProcessEnding.Why"/> says why).</summary>
    CouldNotEnd,
}

/// <summary>How ending a recorded process where it stood went, and why where it could not be ended.</summary>
/// <param name="How">How it went.</param>
/// <param name="Why">Why it could not be ended, where it could not; null otherwise.</param>
public sealed record ProcessEnding(ProcessEnded How, string? Why = null);
