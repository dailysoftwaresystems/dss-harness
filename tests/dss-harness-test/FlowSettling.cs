using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>How a test waits on a running session without sleeping: by a signal the session itself gives.</summary>
internal static class FlowSettling
{
    /// <summary>
    /// Waits until the running session has read every message posted before this call, and has walked its admission
    /// after each: an answer it refuses is read in its turn, after them. A message a step's runner posts from a
    /// continuation the test runs inline - a gate made without asynchronous continuations - is posted before the
    /// gate's completion returns, and so is among them. Bounded: a session that never reads it fails the test in 30 s
    /// rather than hang it.
    /// </summary>
    /// <param name="coordinator">The coordinator whose session runs.</param>
    /// <param name="cancellationToken">The test's own token.</param>
    public static async Task SettledAsync(this FlowCoordinator coordinator, CancellationToken cancellationToken)
        => Assert.Equal(FlowHandedOver.Refused, await coordinator.OfferAnswerAsync("q0", "go").WaitAsync(TimeSpan.FromSeconds(30), cancellationToken));
}
