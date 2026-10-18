namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Where the names of a workflow's steps live: how a name is looked up - among siblings, then the scopes around it
/// - which names are looked up that way and which name a sibling only, what a group's member is called from outside
/// it, and what a step may not be named.
/// </summary>
/// <remarks>
/// <para>
/// Pure, over the definition as it was read. The engine's later reads - an expression's, a template's, a
/// <c>resume</c>, an <c>in</c>, an <c>of</c>, an <c>others</c>, a <c>then</c> - all ask here, so that one owner
/// answers where a name is; which visit of a step a name resolves to is the scheduler's own fact, built on these
/// rules.
/// </para>
/// <para>
/// The reserved names - the roots a read may start with, the engine's facts of a step, and the literals - are one
/// list, <see cref="WorkflowExpressions.ReservedNames"/>, written once there; this class owns that a step may be
/// named none of them.
/// </para>
/// </remarks>
public static class FlowScope
{
    /// <summary>Whether <paramref name="name"/> is one a step may not have: a reserved name, read once from the one list that holds them.</summary>
    /// <param name="name">A step's name.</param>
    public static bool IsReserved(string name) => WorkflowExpressions.IsReserved(name);

    /// <summary>
    /// A bare name looked up as most names are: among the siblings of <paramref name="path"/> - its own group's
    /// members - then the members of each group around it, outward to the flow's top; the nearest scope's holds.
    /// </summary>
    /// <param name="name">The bare name, as written.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="path">The step the name is read from: its path among the flow, a member's under its group's.</param>
    public static WorkflowStep? Resolve(string name, WorkflowDefinition definition, string path)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var scope = path.Contains('.') ? path[..path.LastIndexOf('.')] : null;

        while (true)
        {
            var siblings = scope is null ? definition.Steps : Chain(scope, definition)?[^1].Steps;

            if (Sibling(name, siblings) is { } found)
            {
                return found;
            }

            if (scope is null)
            {
                return null;
            }

            scope = scope.Contains('.') ? scope[..scope.LastIndexOf('.')] : null;
        }
    }

    /// <summary>
    /// A name a sibling only: the way <c>dependsOn</c> and <c>on.step</c> are read. A dependency is an earlier
    /// sibling, a handler and its source are instances of one scope, and neither is looked for anywhere else.
    /// </summary>
    /// <param name="name">The name, as written.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="path">The step the name is read from.</param>
    public static WorkflowStep? SiblingOf(string name, WorkflowDefinition definition, string path)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var scope = path.Contains('.') ? path[..path.LastIndexOf('.')] : null;
        var siblings = scope is null ? definition.Steps : Chain(scope, definition)?[^1].Steps;

        return Sibling(name, siblings);
    }

    /// <summary>
    /// What a group's member is called from outside it, where the path names one: a block's member is the member, a
    /// <c>repeat</c>'s the member's last iteration, and a <c>forEach</c>'s member is nothing - it would be one of
    /// many, and nothing says which.
    /// </summary>
    /// <param name="path">The dotted path, as written from outside the group.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="why">Why nothing was named, where nothing was: a <c>forEach</c> holds the member, or no step holds the path.</param>
    public static WorkflowStep? FromOutside(string path, WorkflowDefinition definition, out string? why)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var walked = Chain(path, definition);

        if (walked is null || walked.Count < 2)
        {
            why = walked is null ? "no step holds the path" : "the path names no group's member";
            return null;
        }

        // A forEach refuses anywhere along the path, not only at its last step: a path that crosses one names
        // one instance per iteration, and nothing says which.
        foreach (var group in walked[..^1])
        {
            if (group.ForEach is not null)
            {
                why = $"{group.Name} holds a forEach";
                return null;
            }
        }

        why = null;
        return walked[^1];
    }

    /// <summary>
    /// Every name an inner step reuses that it can see from where it is - a step beside a group around it, or that
    /// group, declared before or after - as the paths that reuse them; and the recovery's steps are of the flow's
    /// top level, reusing none of its names either. No shadowing: one path must say one thing.
    /// </summary>
    /// <param name="definition">The workflow, as it was read.</param>
    public static IReadOnlyList<(string Path, string Name)> Shadowings(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var shadowings = new List<(string Path, string Name)>();

        Walk(definition.Steps, parent: null, visible: [], shadowings);

        // The recovery's steps are of the flow's top level: every name it can see, they may not reuse.
        if (definition.Recover is { } recover && recover.Steps.Count > 0)
        {
            Walk(recover.Steps, parent: null, visible: [.. definition.Steps.Select(step => step.Name)], shadowings);
        }

        return shadowings;
    }

    /// <summary>One step's own name among its siblings, where it is there; the last declaration wins.</summary>
    private static WorkflowStep? Sibling(string name, IReadOnlyList<WorkflowStep>? siblings)
        => siblings?.LastOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal));

    /// <summary>The steps a path names, from the flow's top to its last part; <see langword="null"/> where a part names nothing.</summary>
    private static List<WorkflowStep>? Chain(string path, WorkflowDefinition definition)
    {
        var siblings = definition.Steps;
        var walked = new List<WorkflowStep>();

        foreach (var part in path.Split('.'))
        {
            if (Sibling(part, siblings) is not { } step)
            {
                return null;
            }

            walked.Add(step);
            siblings = step.Steps;
        }

        return walked;
    }

    /// <summary>Walks the declaration, naming each step by its path and reporting every name it reuses that it can see.</summary>
    /// <remarks>
    /// A step's own siblings are not names it shadows: they are its own scope, and their uniqueness is another rule.
    /// What an inner scope may not reuse is every name visible around it - each ancestor's members and each
    /// ancestor's own name - for they would answer a bare name that already answers at the inner scope's edge.
    /// </remarks>
    private static void Walk(IReadOnlyList<WorkflowStep> siblings, string? parent, IReadOnlyList<string> visible, List<(string Path, string Name)> shadowings)
    {
        foreach (var step in siblings)
        {
            var path = parent is null ? step.Name : $"{parent}.{step.Name}";

            if (visible.Contains(step.Name, StringComparer.Ordinal))
            {
                shadowings.Add((path, step.Name));
            }

            if (step.Steps.Count > 0)
            {
                var around = new List<string>(visible);

                foreach (var beside in siblings)
                {
                    if (!around.Contains(beside.Name, StringComparer.Ordinal))
                    {
                        around.Add(beside.Name);
                    }
                }

                around.Add(step.Name);

                Walk(step.Steps, path, around, shadowings);
            }
        }
    }
}
