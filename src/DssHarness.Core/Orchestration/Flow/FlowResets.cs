namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What a redirect sends the run back over: the step it goes to, and every step of that step's scope that depends on it,
/// directly or through others - a group with all that is in it. A handler is never among them, for its next visit is made
/// when it is next raised, and neither is a step that runs in the always phase, which runs once in a session and is
/// never gone back to. The one rule the engine resets by - a handler's <c>then</c>, and the recovery sending the run back
/// to the step it took - and the facts of a run count what a redirect can make again by.
/// </summary>
public static class FlowResets
{
    /// <summary>
    /// The declarations of one scope a redirect to <paramref name="target"/> resets, in the order the engine collects
    /// them: the target first, then each step that depends on one already collected, depth first.
    /// </summary>
    /// <param name="declarations">The steps the scope declares.</param>
    /// <param name="target">The step the redirect goes to, one of <paramref name="declarations"/>.</param>
    /// <param name="alwaysAround">Whether a group around the scope says <c>always</c>, which makes every step of it one that runs in the always phase.</param>
    public static IReadOnlyList<WorkflowStep> Of(IReadOnlyList<WorkflowStep> declarations, WorkflowStep target, bool alwaysAround)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(target);

        var set = new List<WorkflowStep>();
        Collect(target);

        return set;

        void Collect(WorkflowStep declaration)
        {
            if (set.Any(held => held.Name == declaration.Name) || declaration.On is not null || declaration.Always == true || alwaysAround)
            {
                return;
            }

            set.Add(declaration);

            foreach (var dependent in declarations.Where(dependent => dependent.DependsOn.Contains(declaration.Name, StringComparer.Ordinal)))
            {
                Collect(dependent);
            }
        }
    }
}