namespace DssHarness.Core.Execution;

/// <summary>
/// How often a long-running command looks for what another process asks of it - a stop above all - and how often the
/// process that asked looks whether it has been heard: one cadence for every such pair, an orchestrator's run and the
/// commands that pause, stop or answer it, a leg run and <c>stop-run</c>, so none hears sooner or later than another.
/// </summary>
public static class StopCadence
{
    /// <summary>How often a running command looks for what is asked of it: every 2 s.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    /// <summary>How often the process that asked looks whether it has been heard: every 200 ms.</summary>
    public static readonly TimeSpan WatchEvery = TimeSpan.FromMilliseconds(200);
}
