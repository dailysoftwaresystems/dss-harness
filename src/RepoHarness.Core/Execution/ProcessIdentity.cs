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
/// That something is never a wall-clock instant. This repository does not trust a clock to order
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
    /// platform would not say. Recorded beside the id, and compared exactly: it holds no clock, so
    /// there is nothing for a tolerance to absorb.
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
            // Nothing carries that id, or this process may not ask about it. Told apart below.
            return Carried(processId);
        }

        return string.Equals(now, stamp, StringComparison.Ordinal);
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
    /// again. Read as its own ticks rather than converted to an instant: a conversion is the only way
    /// a stored value could still come back differently twice.
    /// </summary>
    private static string? StartTimeStamp(int processId)
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
            return null;
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
/// and how a refusal names it.
/// </summary>
/// <remarks>
/// One place for what the run lock, a log directory's owner and a machine's heavy-leg slots each decide the same way,
/// so no two of them can come to disagree about which holder is gone.
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
    /// <param name="since">When it took what it holds.</param>
    public static string Describe(string machine, int processId, string runId, DateTimeOffset since)
        => string.Create(CultureInfo.InvariantCulture, $"{machine} pid {processId}, run {runId}, since {since:u}");

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
