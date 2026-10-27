using DssHarness.Core.Codebase;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>One step instance whose working set a reason step is given, and the agent whose directory keeps it.</summary>
/// <param name="Instance">The instance's id, as <see cref="FlowInstanceId"/> spells it.</param>
/// <param name="Agent">The agent it works in; null where it works in none.</param>
public sealed record FlowWorkedFrom(string Instance, string? Agent);

/// <summary>
/// The working sets a reason step's context pack is seeded from (value 372): those of the step it goes on with, of each
/// step it depends on directly, and of its own earlier visits - so a round's <c>rework</c> starts from what the last
/// <c>rework</c> edited and what the reviewers read, and <c>mend</c> from what <c>implement</c> edited.
/// </summary>
public static class FlowWorkingSets
{
    /// <summary>
    /// The seeds the working sets of <paramref name="sources"/> give, in their order (<see cref="CodebaseWorkingSet.Seeds"/>):
    /// each kept in its instance's directory, a source that kept none giving none.
    /// </summary>
    /// <param name="store">Reads each working set.</param>
    /// <param name="layout">Where the run keeps its steps.</param>
    /// <param name="sources">The instances, as the coordinator gave them (<see cref="FlowStepRun.WorkedFrom"/>).</param>
    /// <exception cref="HarnessException">A working set kept does not read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public static IReadOnlyList<ContextPackWorked> SeedsOf(OrchestrationStore store, OrchestratorLayout layout, IReadOnlyList<FlowWorkedFrom> sources)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(sources);

        return CodebaseWorkingSet.Seeds(sources
            .Select(source => store.ReadWorkingSet(layout.InstanceDirectory(source.Instance, source.Agent)))
            .OfType<CodebaseWorkingSet>());
    }
}
