namespace DssHarness.Core.Orchestration.Flow;

using DssHarness.Core.Results;

/// <summary>
/// The runner this build of the tool serves a run's steps with: none. The step runners - a machine step over the
/// services it calls, a reason step over the agent clients - are later slices, and until they land a step of a run is
/// refused loudly, saying so, for a run that silently passed every step over would be worse than one that says what
/// it cannot do. A test or a front end registers its own runner beside the engine, as the engine's own tests do.
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
            Error = $"no runner serves a step of type '{run.Step.Type ?? "group"}' yet: the machine step runners and the agent clients are later slices of the engine, and this step was not run",
        });

    /// <summary>No handoff turn: no agent ran.</summary>
    public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}

/// <summary>
/// The runner facts this build serves the engine with: no runner writes the tree, for no runner is served. Where a
/// workflow names a <c>run</c> step whose runner would write the tree, the engine would under-claim - so the
/// configuration's own facts (which slice 17 wires, as the seam every fact is read through) replace this before the
/// first real runner lands.
/// </summary>
public sealed class NoRunnerFacts : IFlowRunnerFacts
{
    /// <summary>No runner writes the tree: none is served.</summary>
    public bool WritesTree(string runner) => false;
}
