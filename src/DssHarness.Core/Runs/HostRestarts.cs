using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Runs;

/// <summary>
/// What each start of a leg's command on its host that failed before anything ran there said, in order - its check before
/// this machine takes its slot first (<see cref="RemoteLegRunner.CheckAsync"/>), then its run there - and the one rule by
/// which that is said on whatever the leg then reaches: on its line (<see cref="LegEntry.Retried"/>), or in the refusal or
/// failure that ended it with none. A failure a later start did not meet is never hidden: a leg that passed after a start
/// that failed still says the host's DssHarness failed there once.
/// </summary>
public sealed class HostRestarts
{
    private HostRestarts(IReadOnlyList<string> said) => Said = said;

    /// <summary>No start that failed before anything ran.</summary>
    public static HostRestarts None { get; } = new([]);

    /// <summary>What each such start said, in order.</summary>
    public IReadOnlyList<string> Said { get; }

    /// <summary>Whether any start failed so.</summary>
    public bool Any => Said.Count > 0;

    /// <summary>These, then the start that said <paramref name="said"/>.</summary>
    /// <param name="said">What the later start said as it failed.</param>
    public HostRestarts Then(string said)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(said);

        return new([.. Said, said]);
    }

    /// <summary><paramref name="entry"/>, its line saying these before anything it says of later starts.</summary>
    /// <param name="entry">The line the leg reached.</param>
    public LegEntry On(LegEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return Any ? entry with { Retried = [.. Said, .. entry.Retried] } : entry;
    }

    /// <summary>
    /// <paramref name="ex"/>, saying these: what ended a later start never leaves them behind - a host not reached the second
    /// time is still a host whose DssHarness failed the first.
    /// </summary>
    /// <param name="ex">What ended the leg with no line.</param>
    public HarnessException On(HarnessException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return Any
            ? new HarnessException(ex.ExitCode, $"{ex.Message} (retried {(Said.Count == 1 ? "once" : $"{Said.Count} times")}: {string.Join("; ", Said)})", ex)
            : ex;
    }
}

/// <summary>What a host's check of a leg before this machine takes its slot came to (<see cref="RemoteLegRunner.CheckAsync"/>).</summary>
/// <param name="Poisoned">
/// The leg's <c>poisoned</c> line, where the host's DssHarness failed as a defect as it checked, started once more: the leg
/// ends with it, as its run there would have ended; <see langword="null"/> where the host refused nothing.
/// </param>
/// <param name="Restarts">
/// What each start of the check that failed before anything ran said, which the leg's line says whatever the leg then
/// reaches.
/// </param>
public sealed record HostCheck(LegEntry? Poisoned, HostRestarts Restarts)
{
    /// <summary>A check that refused nothing, no start of it failing: what a leg nothing asks of before its slot comes to.</summary>
    public static HostCheck Passed { get; } = new(null, HostRestarts.None);
}
