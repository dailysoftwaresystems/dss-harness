namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Everything a run may still make past the instances its records hold, counted by the engine's own bounds and rules,
/// so the most the facts say is left is a bound: in each scope still open - the flow's top level while the run goes on,
/// and each group, iteration, item and recovery visit not ended - every member not drawn yet, every handler that can
/// still be raised, to its bound (<see cref="WorkflowStep.VisitsAllowed"/>) widened by what was granted it, with the steps
/// its <c>then</c> sends the run back over (<see cref="FlowResets"/>) at each visit, and every member a failure of which
/// the recovery can take (<see cref="FlowScope.OnFailureOf"/>), to its bound (<see cref="FlowRecover.VisitsAllowed"/>)
/// widened by what was granted it, with the steps it sends the run back over at each visit; each repeat instance not
/// ended, past the iterations it made, to its bound (<see cref="FlowRepeat.Bound"/>) widened by what was granted it
/// (<see cref="FlowGrants"/>); and each piece of a plan update not carried out, one item of the work-item group. What
/// such a visit makes is counted whole - a group with all that is in it, its loops to their bounds and its own scope's
/// handlers and recovery - and what no numeric bound holds is named instead: a <c>forEach</c> whose items are not drawn,
/// a repeat with no numeric bound, a step whose pre-existing issues each raise a triage, a handler that sends the run
/// back outside its own group, a plan update still to be planned.
/// </summary>
internal sealed class FlowComing
{
    private readonly WorkflowDefinition _definition;
    private readonly FlowFile _flow;
    private readonly IReadOnlyList<FlowStepRecord> _left;
    private readonly IReadOnlyList<(string Question, string About, FlowGrant Grant, string? Handler)> _granted;
    private readonly Dictionary<string, long> _fresh = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _bodies = new(StringComparer.Ordinal);
    private readonly List<string> _unbounded = [];

    /// <summary>What may still come of the run whose records these are.</summary>
    /// <param name="definition">The workflow the run began with.</param>
    /// <param name="flow">The run's state in one file.</param>
    /// <param name="left">The instances not ended.</param>
    /// <param name="questions">The run's questions and answers; null where it holds none.</param>
    /// <param name="updates">The run's plan updates; null where it holds none.</param>
    public FlowComing(WorkflowDefinition definition, FlowFile flow, IReadOnlyList<FlowStepRecord> left, FlowQuestionsFile? questions, FlowUpdatesFile? updates)
    {
        _definition = definition;
        _flow = flow;
        _left = left;
        _granted = [.. FlowGrants.In(questions)];

        if (!FlowStatus.HasEnded(flow.Status))
        {
            ScopeAt(string.Empty, null);
        }

        foreach (var group in left.Where(held => held.Group))
        {
            Group(group, questions, updates);
        }

        Updates(updates);

        Count = Units.Aggregate(0L, (whole, unit) => Add(whole, Times(unit.Times, CountOf(unit))));
    }

    /// <summary>What one unit of what may come makes: one visit of a step, one iteration or item of a group, or one visit of the recovery.</summary>
    public enum Made
    {
        /// <summary>One visit of the step at the unit's path, with all that is in it.</summary>
        Visit,

        /// <summary>One iteration or item of the group at the unit's path: its own record and its members.</summary>
        Body,

        /// <summary>One visit of the recovery: its own record and its steps.</summary>
        Recovery,
    }

    /// <summary>One thing the run may still make, of which step, and how many times at most.</summary>
    /// <param name="Made">What one of it makes.</param>
    /// <param name="Path">The step's path; null for the recovery.</param>
    /// <param name="Times">How many times at most.</param>
    public readonly record struct Unit(Made Made, string? Path, long Times);

    /// <summary>Every unit of what may come, in the order found.</summary>
    public List<Unit> Units { get; } = [];

    /// <summary>How many instances the units make at most, every one counted whole.</summary>
    public long Count { get; }

    /// <summary>What no numeric bound holds, each said once: the run's own bound of instances is all that bounds it.</summary>
    public IReadOnlyList<string> Unbounded => _unbounded;

    /// <summary>
    /// The longest one unit takes by this run's own times: a step's longest, a group's members one after another, a
    /// repeat to its bound - naught where any of them has no time to go by.
    /// </summary>
    /// <param name="unit">The unit.</param>
    /// <param name="times">The shortest and longest time each step of the definition took to end Done, by its path.</param>
    public long LongestOf(Unit unit, IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times)
        => unit.Made switch
        {
            Made.Visit => VisitTime(unit.Path!, times),
            Made.Body => BodyTime(unit.Path, times),
            _ => BodyTime(FlowScope.Recovery, times),
        };

    /// <summary>Saturating addition, for a bound that only says at most.</summary>
    public static long Add(long one, long other) => one > long.MaxValue - other ? long.MaxValue : one + other;

    /// <summary>Saturating multiplication, for a bound that only says at most.</summary>
    public static long Times(long one, long other) => one == 0 || other == 0 ? 0 : one > long.MaxValue / other ? long.MaxValue : one * other;

    /// <summary>The container of an instance id: everything before its last segment - the group instance its step runs in.</summary>
    public static string ContainerOf(string id)
        => id.LastIndexOf('.') is >= 0 and var at ? id[..at] : string.Empty;

    /// <summary>The name of the step an instance id is of: its last segment without its marks.</summary>
    private static string NameOf(string id)
        => id[(id.LastIndexOf('.') + 1)..].Split(['+', '#', '['])[0];

    /// <summary>Whether <paramref name="id"/>'s last segment is an iteration of a repeat (<c>&lt;loop&gt;#&lt;n&gt;</c>).</summary>
    private static bool IsIteration(string id) => id[(id.LastIndexOf('.') + 1)..].Contains('#', StringComparison.Ordinal);

    /// <summary>Whether <paramref name="id"/>'s last segment is an item of a <c>forEach</c> (<c>&lt;group&gt;[&lt;i&gt;]</c>).</summary>
    private static bool IsItem(string id) => id[(id.LastIndexOf('.') + 1)..].Contains('[', StringComparison.Ordinal);

    /// <summary>Whether <paramref name="id"/> is one of the iterations the repeat instance <paramref name="loop"/> made, <c>&lt;loop&gt;#&lt;n&gt;</c>.</summary>
    private static bool IterationOf(string loop, string id)
        => id.Length > loop.Length + 1
            && id.StartsWith(loop, StringComparison.Ordinal)
            && id[loop.Length] == '#'
            && id[(loop.Length + 1)..].All(char.IsAsciiDigit);

    /// <summary>The path of <paramref name="member"/> declared in the scope at <paramref name="scope"/>.</summary>
    private static string PathIn(string? scope, WorkflowStep member) => scope is null ? member.Name : $"{scope}.{member.Name}";

    /// <summary>Says once that <paramref name="why"/> is held by no numeric bound.</summary>
    private void Unbound(string why)
    {
        if (!_unbounded.Contains(why, StringComparer.Ordinal))
        {
            _unbounded.Add(why);
        }
    }

    /// <summary>The steps declared in the scope at <paramref name="scope"/>: the flow's top level, the recovery's steps, or a group's members.</summary>
    private IReadOnlyList<WorkflowStep> MembersAt(string? scope)
        => scope is null ? _definition.Steps : FlowScope.At(scope, _definition)?.Step.Steps ?? [];

    /// <summary>Whether a group around the scope at <paramref name="scope"/> - its own or one it sits in - says <c>always</c>.</summary>
    private bool AlwaysAround(string? scope)
    {
        for (var at = scope; at is not null; at = FlowScope.ScopeOf(at))
        {
            if (FlowScope.At(at, _definition)?.Step.Always == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the schedule draws <paramref name="member"/> of the scope at <paramref name="scope"/>: never a handler, which
    /// its source's ending raises, nor a step a pre-existing issue raises, nor the step that plans a plan update.
    /// </summary>
    private bool Drawn(string? scope, WorkflowStep member)
        => member.On is null && member.Raised is null && !string.Equals(_definition.Amend?.Step, PathIn(scope, member), StringComparison.Ordinal);

    /// <summary>Whether the recovery can take a failure of the step at <paramref name="path"/>: the workflow declares one, and the policy that decides for the step is <c>recover</c>.</summary>
    private bool Recoverable(string path)
        => _definition.Recover is not null && FlowScope.OnFailureOf(path, _definition)?.Step.OnFailure == WorkflowStep.Recover;

    /// <summary>
    /// The steps a handler's <c>then</c> sends the run back over in the scope at <paramref name="scope"/>; none where it
    /// names none, and none - said to be held by no numeric bound - where it sends the run back outside its own group,
    /// whose next visit holds the handler again.
    /// </summary>
    private IReadOnlyList<WorkflowStep> ResetsOf(string? scope, WorkflowStep handler)
    {
        if (handler.Then is not { } then || FlowScope.Resolve(then, _definition, scope) is not { } target)
        {
            return [];
        }

        if (!string.Equals(FlowScope.ScopeOf(target.Path), scope, StringComparison.Ordinal))
        {
            Unbound($"'{PathIn(scope, handler)}' sends the run back to '{target.Path}', outside its own group");
            return [];
        }

        return FlowResets.Of(MembersAt(scope), target.Step, AlwaysAround(scope));
    }

    /// <summary>How many instances one <paramref name="unit"/> makes at most.</summary>
    private long CountOf(Unit unit)
        => unit.Made switch
        {
            Made.Visit => Fresh(unit.Path!),
            Made.Body => Add(1, Body(unit.Path)),
            _ => Add(1, Body(FlowScope.Recovery)),
        };

    /// <summary>
    /// How many instances one visit of the step at <paramref name="path"/> makes at most: itself, and for a group all that
    /// is in it - a repeat to its bound, each iteration its record and its members.
    /// </summary>
    private long Fresh(string path)
    {
        if (_fresh.TryGetValue(path, out var held))
        {
            return held;
        }

        var count = 1L;

        if (FlowScope.At(path, _definition)?.Step is { } step)
        {
            if (step.OnPreExisting is not null)
            {
                Unbound($"'{path}' raises a triage for each pre-existing issue it reports");
            }

            if (step.Repeat is { } repeat)
            {
                var bound = repeat.Bound(0);

                if (bound == int.MaxValue)
                {
                    Unbound($"'{path}' repeats with no numeric bound of its own");
                }
                else
                {
                    count = Add(1, Times(bound, Add(1, Body(path))));
                }
            }
            else if (step.ForEach is not null)
            {
                Unbound($"the items of '{path}' are not drawn yet");
            }
            else if (step.Steps.Count > 0)
            {
                count = Add(1, Body(path));
            }
        }

        _fresh[path] = count;
        return count;
    }

    /// <summary>
    /// How many instances the members of a scope at <paramref name="scope"/> just opened make at most: each member the
    /// schedule draws once, each handler to its bound with what its <c>then</c> sends the run back over at each visit, and
    /// each member the recovery can take to the recovery's bound, each visit of it with what it sends the run back over.
    /// </summary>
    private long Body(string? scope)
    {
        var key = scope ?? string.Empty;

        if (_bodies.TryGetValue(key, out var held))
        {
            return held;
        }

        _bodies[key] = 0;
        var members = MembersAt(scope);
        var count = 0L;

        foreach (var member in members)
        {
            var path = PathIn(scope, member);

            if (member.On is not null)
            {
                var again = ResetsOf(scope, member).Aggregate(0L, (whole, reset) => Add(whole, Fresh(PathIn(scope, reset))));
                count = Add(count, Times(member.VisitsAllowed, Add(Fresh(path), again)));
            }
            else if (Drawn(scope, member))
            {
                count = Add(count, Fresh(path));
            }

            if (Recoverable(path))
            {
                var again = FlowResets.Of(members, member, AlwaysAround(scope)).Aggregate(0L, (whole, reset) => Add(whole, Fresh(PathIn(scope, reset))));
                count = Add(count, Times(_definition.Recover!.VisitsAllowed, Add(Add(1, Body(FlowScope.Recovery)), again)));
            }
        }

        _bodies[key] = count;
        return count;
    }

    /// <summary>What a group instance not ended may still make: its own scope's, or a loop's iterations past those it made, or a <c>forEach</c>'s items not drawn.</summary>
    private void Group(FlowStepRecord group, FlowQuestionsFile? questions, FlowUpdatesFile? updates)
    {
        if (NameOf(group.Instance) == FlowScope.Recovery && group.RaisedBy is not null)
        {
            ScopeAt(group.Instance, FlowScope.Recovery);
            return;
        }

        var path = FlowInstanceId.PathOf(group.Instance);

        if (FlowScope.At(path, _definition)?.Step is not { } step)
        {
            return;
        }

        if (step.Repeat is { } repeat && !IsIteration(group.Instance))
        {
            // A repeat instance not ended is a loop of its own, beside the iterations it made: its bound widened by what
            // was granted it, past the iterations it has made.
            var bound = repeat.Bound(FlowGrants.IterationsOf(group.Instance, questions, updates));

            if (bound == int.MaxValue)
            {
                Unbound($"'{path}' repeats with no numeric bound of its own");
                return;
            }

            var made = _flow.Steps.Count(held => IterationOf(group.Instance, held.Instance));

            if (bound - made > 0)
            {
                Units.Add(new Unit(Made.Body, path, bound - made));
            }

            return;
        }

        if (step.ForEach is not null && !IsItem(group.Instance))
        {
            if (!_flow.Steps.Any(held => held.Instance.StartsWith(group.Instance + "[", StringComparison.Ordinal)))
            {
                Unbound($"the items of '{path}' are not drawn yet");
            }

            return;
        }

        ScopeAt(group.Instance, path);
    }

    /// <summary>
    /// What the scope <paramref name="container"/> - whose members the scope at <paramref name="scope"/> declares - may
    /// still make: each member not drawn there yet; each handler whose source can still end there, or that holds a
    /// granted visit not carried out yet, to its bound with what its <c>then</c> sends the run back over; and each member
    /// that can still end there and whose failure the recovery can take, to the recovery's bound with what it sends the run
    /// back over - what a visit sends the run back over being able to end again, until nothing more can.
    /// </summary>
    private void ScopeAt(string container, string? scope)
    {
        var members = MembersAt(scope);
        bool In(string id, WorkflowStep member) => ContainerOf(id) == container && NameOf(id) == member.Name;

        var reachable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var member in members)
        {
            if (_left.Any(held => In(held.Instance, member)))
            {
                reachable.Add(member.Name);
            }
            else if (Drawn(scope, member) && !_flow.Steps.Any(held => In(held.Instance, member)))
            {
                reachable.Add(member.Name);
                Units.Add(new Unit(Made.Visit, PathIn(scope, member), 1));
            }
        }

        var raised = new HashSet<string>(StringComparer.Ordinal);
        var recovered = new HashSet<string>(StringComparer.Ordinal);

        for (var changed = true; changed;)
        {
            changed = false;

            foreach (var handler in members.Where(member => member.On is not null && !raised.Contains(member.Name)))
            {
                var source = handler.On!.Step;
                var granted = _granted.Where(one => one.Grant == FlowGrant.Visit && one.Handler == handler.Name && ContainerOf(one.About) == container && NameOf(one.About) == source).ToList();
                var visits = _flow.Steps.Count(held => held.RaisedBy is not null && In(held.Instance, handler));
                var remaining = handler.VisitsAllowed + granted.Count - visits;
                var owed = granted.Any(one => _flow.Steps.Any(held => held.Instance == one.About && held.Question == one.Question));

                if (remaining <= 0 || !(reachable.Contains(source) || owed))
                {
                    continue;
                }

                raised.Add(handler.Name);
                reachable.Add(handler.Name);
                changed = true;
                Units.Add(new Unit(Made.Visit, PathIn(scope, handler), remaining));

                foreach (var reset in ResetsOf(scope, handler))
                {
                    reachable.Add(reset.Name);
                    Units.Add(new Unit(Made.Visit, PathIn(scope, reset), remaining));
                }
            }

            foreach (var member in members.Where(member => reachable.Contains(member.Name) && !recovered.Contains(member.Name) && Recoverable(PathIn(scope, member))))
            {
                var granted = _granted.Count(one => one.Grant == FlowGrant.Recovery && ContainerOf(one.About) == container && NameOf(one.About) == member.Name);
                var taken = _flow.Steps.Count(held => held.Group && NameOf(held.Instance) == FlowScope.Recovery && held.RaisedBy is { } source && In(source, member));
                var remaining = _definition.Recover!.VisitsAllowed + granted - taken;

                if (remaining <= 0)
                {
                    continue;
                }

                recovered.Add(member.Name);
                changed = true;
                Units.Add(new Unit(Made.Recovery, null, remaining));

                foreach (var reset in FlowResets.Of(members, member, AlwaysAround(scope)))
                {
                    reachable.Add(reset.Name);
                    Units.Add(new Unit(Made.Visit, PathIn(scope, reset), remaining));
                }
            }
        }

        // A step that can still end and reports pre-existing issues raises a triage for each, and their number is the
        // step's answer's to say.
        foreach (var member in members.Where(member => reachable.Contains(member.Name) && member.OnPreExisting is not null))
        {
            Unbound($"'{PathIn(scope, member)}' raises a triage for each pre-existing issue it reports");
        }
    }

    /// <summary>
    /// What plan updates may still make: each piece of one done that is not carried out to an end, one item of the
    /// work-item group; and one queued or under way, whatever its plan will hold, which no numeric bound holds.
    /// </summary>
    private void Updates(FlowUpdatesFile? updates)
    {
        foreach (var update in updates?.Updates ?? [])
        {
            if (update.State is FlowUpdateStates.Queued or FlowUpdateStates.UnderWay)
            {
                Unbound($"plan update {update.Id} is still to be planned");
            }
            else if (update.State == FlowUpdateStates.Done
                && _definition.Amend is { } amend
                && FlowUpdateGates.ParentOf(amend.WhenAddScope) is { } group
                && update.ThePieces.Count(piece => FlowUpdatePieceKinds.Carried.Contains(piece.Kind, StringComparer.Ordinal) && piece.Ended is null) is > 0 and var pieces)
            {
                Units.Add(new Unit(Made.Body, group, pieces));
            }
        }
    }

    /// <summary>The longest one visit of the step at <paramref name="path"/> takes: its own longest, or its members' one after another, a repeat's to its bound.</summary>
    private long VisitTime(string path, IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times)
    {
        if (FlowScope.At(path, _definition)?.Step is not { } step)
        {
            return 0;
        }

        if (step.Steps.Count == 0)
        {
            return times.TryGetValue(path, out var own) ? own.Longest.Ticks : 0;
        }

        if (step.ForEach is not null)
        {
            return 0;
        }

        if (step.Repeat is { } repeat)
        {
            var bound = repeat.Bound(0);
            return bound == int.MaxValue ? 0 : Times(bound, BodyTime(path, times));
        }

        return BodyTime(path, times);
    }

    /// <summary>The longest one iteration or item of the scope at <paramref name="scope"/> takes: each member the schedule draws, one after another - naught where one of them has no time to go by.</summary>
    private long BodyTime(string? scope, IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times)
    {
        var longest = 0L;

        foreach (var member in MembersAt(scope).Where(member => Drawn(scope, member)))
        {
            var one = VisitTime(PathIn(scope, member), times);

            if (one == 0)
            {
                return 0;
            }

            longest = Add(longest, one);
        }

        return longest;
    }
}
