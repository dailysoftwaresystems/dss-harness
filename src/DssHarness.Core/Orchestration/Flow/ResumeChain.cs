namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The chain of work a reason step goes on with (<c>resume</c>): the step at its root - the one that began the work,
/// whose agent definition, client, tier, model and <c>writes</c> every step that goes on with it inherits, naming none
/// of them itself - found as the engine finds the step a resume names: a reason step of the step's own scope or
/// one around it, through each resume in turn. The one owner of the walk, which the reason step's runner and the
/// definition's check both ask, so that what a step is checked as is what it runs as.
/// </summary>
public static class ResumeChain
{
    /// <summary>
    /// The step at the root of <paramref name="step"/>'s chain: <paramref name="step"/> itself where it begins its own
    /// work - it names no resume, or names itself, inside a repeat or on a later visit - and else the step the chain ends
    /// at. A resume that names no reason step of an enclosing scope, and a ring of resumes, both of which the
    /// definition's check refuses, end the walk where they stand.
    /// </summary>
    /// <param name="definition">The workflow the step is declared in.</param>
    /// <param name="path">The step's declaration path - an instance's is <see cref="FlowInstanceId.PathOf"/>.</param>
    /// <param name="step">The step, as declared at <paramref name="path"/>.</param>
    public static WorkflowStep Root(WorkflowDefinition definition, string path, WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(step);

        var seen = new HashSet<string>(StringComparer.Ordinal) { path };

        while (step.Resume is { } name
            && !string.Equals(name, step.Name, StringComparison.Ordinal)
            && ReasonStepNamed(name, definition, FlowScope.ScopeOf(path)) is { } found
            && seen.Add(found.Path))
        {
            (path, step) = (found.Path, found.Step);
        }

        return step;
    }

    /// <summary>
    /// The reason step <paramref name="name"/> names from <paramref name="scope"/>, as the engine finds the step a
    /// resume names: a member of the scope, else of each scope around it, outward to the flow's top level - a member of
    /// another type passed over; null where none is.
    /// </summary>
    private static FlowScope.Located? ReasonStepNamed(string name, WorkflowDefinition definition, string? scope)
    {
        while (true)
        {
            if (FlowScope.SiblingOf(name, definition, scope) is { Step.Type: "reason" } found)
            {
                return found;
            }

            if (scope is null)
            {
                return null;
            }

            scope = FlowScope.ScopeOf(scope);
        }
    }
}
