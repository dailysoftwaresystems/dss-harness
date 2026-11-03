using DssHarness.Core.Agents;

namespace DssHarness.Tests;

/// <summary>
/// The agent runner as a fake for a service whose steps run no agent of their own: it answers that the run never
/// began, and holds together as a result must.
/// </summary>
public sealed class OfNoStep : IAgentRunner
{
    /// <inheritdoc/>
    public Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
        => Task.FromResult(new AgentRunResult(AgentRunStatus.NotStarted, AgentNotStartedBecause.ConfigurationWrong, Error: "this test's service runs no agent"));

    /// <inheritdoc/>
    public async IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = await RunAsync(step, null, cancellationToken);
        yield break;
    }
}
