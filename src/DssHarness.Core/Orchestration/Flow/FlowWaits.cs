namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The words a step that has not started is said to wait on in its record where no step it depends on holds it: the
/// admission writes each from here, and every reader of a run's records reads each by its word here, so the walk that
/// decides and the facts a run is asked about read one wait one way. Each holds a space, and a step's name never does,
/// so none is ever read as a step of the same name.
/// </summary>
public static class FlowWaits
{
    /// <summary>A claim another step holds, or one a step before it in the walk waits for.</summary>
    public const string Claim = "a claim";

    /// <summary>The run to be quiet: an <c>alone</c> step running, or one waiting for nothing else to run.</summary>
    public const string Quiet = "the run to be quiet";

    /// <summary>Room among the reasoning agents the run lets run at once.</summary>
    public const string ReasoningAgents = "reasoning agents";

    /// <summary>Room among the processes the run lets run at once.</summary>
    public const string ParallelProcesses = "parallel processes";

    /// <summary>Room among the agents the run lets be open at once.</summary>
    public const string OpenAgents = "open agents";

    /// <summary>
    /// Whether <paramref name="wait"/> is a wait for room - a claim, the run to be quiet, or a counter of the run's
    /// limits - which only the run's own pace ends, and whose length no step's time says.
    /// </summary>
    /// <param name="wait">One word of a record's <c>waitsOn</c>.</param>
    public static bool ForRoom(string wait)
        => wait is Claim or Quiet or ReasoningAgents or ParallelProcesses or OpenAgents;
}
