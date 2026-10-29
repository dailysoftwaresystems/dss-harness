namespace DssHarness.Core.Orchestration.Flow;

using DssHarness.Core.Results;

/// <summary>
/// The runner a step of a type no runner serves yet is given to (<see cref="FlowStepRunners"/>): the rest of the step
/// runners - the machine steps over the services they call, a reason step over the agent clients - are later slices,
/// and until each lands a step of its type is refused loudly, saying so, for a run that silently passed a step over
/// would be worse than one that says what it cannot do. A test or a front end registers its own runner beside the
/// engine, as the engine's own tests do.
/// </summary>
public sealed class NoStepRunner : IFlowStepRunner
{
    /// <summary>Refuses the step, loudly: no runner of its type is built yet.</summary>
    public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        => Task.FromResult(new FlowStepResult
        {
            Asked = null,
            Passed = false,
            Exit = HarnessExit.ToolMissing,
            Error = $"no runner serves a step of type '{run.Step.Type ?? "group"}' yet: the rest of the step runners and the agent clients are later slices of the engine, and this step was not run",
        });

    /// <summary>No handoff turn: no agent ran.</summary>
    public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>No account: no agent ran, and nothing was streamed.</summary>
    public string? Account(FlowStepRun run) => null;
}

