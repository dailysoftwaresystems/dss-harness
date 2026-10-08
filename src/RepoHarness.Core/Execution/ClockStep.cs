namespace RepoHarness.Core.Execution;

/// <summary>
/// What tells that the clock stepped, or the host slept, across a window timed on both clocks: how far wall-clock time
/// moved from monotonic time, and whether that is past what the configuration tolerates
/// (<c>defaults.clockStepToleranceMilliseconds</c>). One rule for whoever times a window: a phase across itself, and a
/// sweep's run, which may be stopped before its phase has answered.
/// </summary>
public static class ClockStep
{
    /// <summary>
    /// How far wall-clock time moved from monotonic time across one window: a step forward, a step back, or a host that
    /// slept in the middle of it.
    /// </summary>
    /// <param name="wall">How long the window took by the wall clock.</param>
    /// <param name="monotonic">How long it took by the monotonic clock.</param>
    public static TimeSpan Drift(TimeSpan wall, TimeSpan monotonic) => (wall - monotonic).Duration();

    /// <summary>
    /// Whether <paramref name="drift"/> is a step: past <paramref name="toleranceMilliseconds"/>. Zero or less watches
    /// for none, since drift below a millisecond is ordinary and flagging it would mark every window suspect.
    /// </summary>
    /// <param name="drift">What <see cref="Drift"/> measured.</param>
    /// <param name="toleranceMilliseconds">How far the clocks may disagree before it is a step.</param>
    public static bool IsPast(TimeSpan drift, int toleranceMilliseconds)
        => toleranceMilliseconds > 0 && drift > TimeSpan.FromMilliseconds(toleranceMilliseconds);
}
