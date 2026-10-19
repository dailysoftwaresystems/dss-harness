namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Where the names of a workflow's steps live: how a name is looked up - among siblings, then the scopes around it
/// - which names are looked up that way and which name a sibling only, what a group's member is called from outside
/// it, how a step's name is spelt, and what a step may not be named.
/// </summary>
/// <remarks>
/// <para>
/// Pure, over the definition as it was read, and the one door every reader of a name goes through: the validator's
/// reads, dependencies, handlers, redirects and shadowings, the cycle walk's edges, and the engine's later reads - an
/// expression's, a template's, a <c>resume</c>, an <c>in</c>, an <c>of</c>, an <c>others</c>, a <c>then</c> - all
/// ask here, so that one owner answers where a name is. Which visit of a step a name resolves to is the scheduler's
/// own fact, built on these rules; whether what a name finds runs before the step that reads it is the validator's.
/// </para>
/// <para>
/// A name is looked up in a scope: the path of the group whose members are looked at first - the reader's own group,
/// or, where a group's loop reads what its iterations produced, the group itself - <see cref="Recovery"/> for the
/// recovery's own steps, and <see langword="null"/> for the flow's top level. Outward from a group is the scope it
/// sits in; outward from the recovery is the flow's top level, so its steps see every step of the flow, the top
/// level's by bare name and a member by its path. Where siblings share a name - a duplicate the validator refuses -
/// the first declared is the one a name finds.
/// </para>
/// <para>
/// The reserved names - the roots a read may start with, the engine's facts of a step, and the literals - are one
/// list, <see cref="WorkflowExpressions.ReservedNames"/>, written once there; this class owns that a step may be
/// named none of them.
/// </para>
/// </remarks>
public static class FlowScope
{
    /// <summary>
    /// The scope the recovery's own steps are declared in, and the first segment of every one of their paths
    /// (<c>recover.diagnose</c>): a reserved name, so no step of the flow can be read in its place.
    /// </summary>
    public const string Recovery = "recover";

    /// <summary>One step a name found, with its path among the flow: its name, a member's under its group's.</summary>
    /// <param name="Path">The step's path, as every refusal and every instance id spells it.</param>
    /// <param name="Step">The step as it was read.</param>
    public sealed record Located(string Path, WorkflowStep Step);

    /// <summary>Whether <paramref name="name"/> is one a step may not have: a reserved name, read once from the one list that holds them.</summary>
    /// <param name="name">A step's name.</param>
    public static bool IsReserved(string name) => WorkflowExpressions.IsReserved(name);

    /// <summary>
    /// Whether <paramref name="name"/> is spelt as a step's name is: a lower-case letter, then lower-case letters,
    /// digits and dashes, and nothing after them - not even a line's end, which a pattern anchored with <c>$</c>
    /// would let through. A dot separates the segments of a read and of an instance id, so a name holds none. A
    /// declared input's, param's and output's name is spelt the same way.
    /// </summary>
    /// <param name="name">The name, as written.</param>
    public static bool IsWellFormedName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Length > 0
            && name[0] is >= 'a' and <= 'z'
            && name.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    /// <summary>The scope a step at <paramref name="path"/> sits in: its group's path, <see cref="Recovery"/> for one of the recovery's own steps, or <see langword="null"/> at the flow's top level.</summary>
    /// <param name="path">A step's path among the flow.</param>
    public static string? ScopeOf(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var dot = path.LastIndexOf('.');
        return dot < 0 ? null : path[..dot];
    }

    /// <summary>
    /// A name a sibling only: the way <c>dependsOn</c> and <c>on.step</c> are read. A dependency is an earlier
    /// sibling, a handler and its source are instances of one scope, and neither is looked for anywhere else.
    /// </summary>
    /// <param name="name">The name, as written.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="scope">The scope the name is read in: the path of the group that declares the step naming it, <see cref="Recovery"/>, or <see langword="null"/> for the top level.</param>
    public static Located? SiblingOf(string name, WorkflowDefinition definition, string? scope)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return Member(name, scope, definition);
    }

    /// <summary>
    /// A bare name looked up as most names are: among the members of <paramref name="scope"/>, then the members of each
    /// scope around it, outward to the flow's top level; the nearest scope's holds. A member of a group the reader is
    /// not in is never found this way: from outside, it is read by its path (<see cref="Read"/>).
    /// </summary>
    /// <param name="name">The bare name, as written.</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="scope">The scope the name is read in (see the class's remarks).</param>
    public static Located? Resolve(string name, WorkflowDefinition definition, string? scope)
    {
        ArgumentNullException.ThrowIfNull(definition);

        while (true)
        {
            if (Member(name, scope, definition) is { } found)
            {
                return found;
            }

            if (scope is null)
            {
                return null;
            }

            scope = ScopeOf(scope);
        }
    }

    /// <summary>
    /// The step a read names, with the names after it - the members of its output, or the engine's facts of it - or
    /// <see langword="null"/> where it names none. The first name is looked up as <see cref="Resolve"/> looks a bare
    /// name up; each name after it that names a member of the step found so far goes down into it: a block's member is
    /// the member, and a <c>repeat</c>'s the member's last iteration. A <c>forEach</c>'s member read from outside it
    /// is nothing - it would be one of many, and nothing says which - and <paramref name="why"/> says so; from inside
    /// it, the member is the reader's own item's.
    /// </summary>
    /// <param name="names">The read's names in order, its indexes left out (<see cref="WorkflowExpressions.Read.Names"/>).</param>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="scope">The scope the read is made in (see the class's remarks).</param>
    /// <param name="why">Why nothing was named where a <c>forEach</c> holds the member; <see langword="null"/> otherwise.</param>
    public static (Located Step, IReadOnlyList<string> After)? Read(IReadOnlyList<string> names, WorkflowDefinition definition, string? scope, out string? why)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(definition);

        why = null;

        if (names.Count == 0 || Resolve(names[0], definition, scope) is not { } step)
        {
            return null;
        }

        var next = 1;

        while (next < names.Count && First(names[next], step.Step.Steps) is { } member)
        {
            if (step.Step.ForEach is not null && !Holds(step.Path, scope))
            {
                why = $"'{step.Path}' runs a forEach: read from outside it, a member is one of many, and nothing says which";
                return null;
            }

            step = new Located($"{step.Path}.{member.Name}", member);
            next++;
        }

        return (step, [.. names.Skip(next)]);
    }

    /// <summary>
    /// Every inner step that takes, by bare name, a name it can see from where it is - a step beside a group around
    /// it, or that group, declared before or after; for the recovery's steps, a step of the flow's top level - each
    /// with the path of the step it hides, the nearest first. No shadowing: one name must say one thing wherever it
    /// is read.
    /// </summary>
    /// <param name="definition">The workflow, as it was read.</param>
    public static IReadOnlyList<(string Path, string Shadowed)> Shadowings(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var shadowings = new List<(string Path, string Shadowed)>();

        foreach (var (path, step) in Declared(definition))
        {
            // A step's own siblings are its own scope, and their uniqueness is another rule: what it may not take is
            // a name that already answers one scope out from its own.
            if (ScopeOf(path) is { } scope && Resolve(step.Name, definition, ScopeOf(scope)) is { } hidden)
            {
                shadowings.Add((path, hidden.Path));
            }
        }

        return shadowings;
    }

    /// <summary>
    /// Every declared step with its path, in declaration order - a group before its members - the flow's steps first
    /// and then the recovery's, under <see cref="Recovery"/>.
    /// </summary>
    /// <param name="definition">The workflow, as it was read.</param>
    public static IEnumerable<(string Path, WorkflowStep Step)> Declared(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var declared = Walk(definition.Steps, scope: null);

        return definition.Recover is { } recover ? declared.Concat(Walk(recover.Steps, Recovery)) : declared;
    }

    /// <summary>Each step of <paramref name="steps"/> and of every group among them, with its path, a group before its members.</summary>
    private static IEnumerable<(string Path, WorkflowStep Step)> Walk(IReadOnlyList<WorkflowStep> steps, string? scope)
    {
        foreach (var step in steps)
        {
            var path = scope is null ? step.Name : $"{scope}.{step.Name}";

            yield return (path, step);

            foreach (var inner in Walk(step.Steps, path))
            {
                yield return inner;
            }
        }
    }

    /// <summary>The member of <paramref name="scope"/> named <paramref name="name"/>, with its path, where there is one.</summary>
    private static Located? Member(string name, string? scope, WorkflowDefinition definition)
        => First(name, MembersOf(scope, definition)) is { } member
            ? new Located(scope is null ? member.Name : $"{scope}.{member.Name}", member)
            : null;

    /// <summary>
    /// The steps declared directly in <paramref name="scope"/>: the flow's top level, the recovery's own steps, or a
    /// group's members, the group found by its path from the one or the other; none where the path names no group.
    /// </summary>
    private static IReadOnlyList<WorkflowStep> MembersOf(string? scope, WorkflowDefinition definition)
    {
        if (scope is null)
        {
            return definition.Steps;
        }

        var parts = scope.Split('.');
        var siblings = definition.Steps;
        var start = 0;

        if (parts[0] == Recovery && definition.Recover is { } recover)
        {
            siblings = recover.Steps;
            start = 1;
        }

        foreach (var part in parts[start..])
        {
            if (First(part, siblings) is not { } group)
            {
                return [];
            }

            siblings = group.Steps;
        }

        return siblings;
    }

    /// <summary>The first of <paramref name="steps"/> named <paramref name="name"/>, where one is.</summary>
    private static WorkflowStep? First(string name, IReadOnlyList<WorkflowStep> steps)
        => steps.FirstOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal));

    /// <summary>Whether the group at <paramref name="group"/> holds a reader in <paramref name="scope"/>: the scope is the group's own, or one inside it.</summary>
    private static bool Holds(string group, string? scope)
        => scope is not null && (scope == group || scope.StartsWith(group + ".", StringComparison.Ordinal));
}
