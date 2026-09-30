using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using RepoHarness.Core.Platform;

namespace RepoHarness.Core.Execution;

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
/// disagree with the other about which holder is gone. A machine's heavy-leg slots are told by
/// <see cref="IProcessIdentity.IsAlive"/> alone, every entry of their record being this machine's whatever name it
/// carries; they name their holders, and say what was reclaimed, as the other two do.
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

    /// <summary>A holder as a refusal names it: <c>HOST pid 12, run R, since 2026-09-30 16:32:14Z</c>.</summary>
    /// <param name="machine">The machine it runs on.</param>
    /// <param name="processId">Its process id.</param>
    /// <param name="runId">Its run.</param>
    /// <param name="since">When it took what it holds, or asked for it.</param>
    public static string Describe(string machine, int processId, string runId, DateTimeOffset since)
        => string.Create(CultureInfo.InvariantCulture, $"{machine} pid {processId}, run {runId}, since {since:u}");

    /// <summary>What is said when a dead holder's <paramref name="what"/> is taken back.</summary>
    /// <param name="what">What it held, as a line names it.</param>
    /// <param name="holder">The holder, as <see cref="Describe"/> names it.</param>
    public static string Reclaimed(string what, string holder) => $"Reclaimed {what} from {holder}, which is no longer running.";

    /// <summary>What is said when <c>--force-lock</c> takes <paramref name="what"/> from a holder that may still run.</summary>
    /// <param name="what">What it held, as a line names it.</param>
    /// <param name="holder">The holder, as <see cref="Describe"/> names it.</param>
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
            : " (recorded on another machine, or on this one under an earlier name, which cannot be asked whether it still runs; --force-lock takes it)";

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
}
