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
}

/// <summary>
/// The runner facts this build serves the engine with: no runner writes the tree, for no <c>run</c> step is served
/// yet. Where a workflow names a <c>run</c> step whose runner would write the tree, the engine would under-claim - so
/// the configuration's own facts (each runner's <c>writesTree</c>) replace this in the slice that lands the <c>run</c>
/// step's runner, before it serves a step.
/// </summary>
public sealed class NoRunnerFacts : IFlowRunnerFacts
{
    /// <summary>No runner writes the tree: no <c>run</c> step is served.</summary>
    public bool WritesTree(string runner) => false;
}
