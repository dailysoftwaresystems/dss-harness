using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The engine's facts about a run at a moment (part 04, "Asking a run about itself"): computed from the run's records
/// and never guessed - how the run stands, what is running, what it waits for, what is done, what is left at least and
/// at most, and when it may finish as a range from this run's own times. Pure: it reads nothing, holds nothing and
/// changes nothing; the records are read by the same store methods <c>list-orchestrator</c> reads and handed to it,
/// with the moment they were read.
/// </summary>
/// <remarks>
/// <para>
/// What is left counts at least the instances that exist and have not ended, and at most those and every one a loop or
/// a handler can still add at its bound - a loop without a numeric bound, and a <c>forEach</c> not yet expanded, are
/// bounded only by the run's own bound of instances, and the facts say which bound gave the number.
/// </para>
/// <para>
/// The estimate rests on this run alone: of each step of the definition, the shortest and the longest time an instance
/// of it took to end <c>Done</c>. Its lower end lays the instances that are left along what depends on what - so work
/// that runs side by side is counted once - each at its step's shortest time, and a step in progress with what is left
/// of that; its upper end sums every instance that is left and every one a loop or a handler can add, each at its
/// step's longest, one after another. A step none of whose instances has ended <c>Done</c> has no time to go by: its
/// instances are counted and named, and are in neither end. It leaves out, and says that it does, the time a person
/// takes to answer and a wait for a claim or for room. Where nothing has ended, or the run has ended, there is no
/// estimate, and the facts say which.
/// </para>
/// </remarks>
public static class FlowStatus
{
    /// <summary>The width of a row's label, as the facts line their rows up under one another.</summary>
    private const int LabelWidth = 10;

    /// <summary>
    /// The facts of the run, one line each: a header naming the moment they were read, then a row for what is running,
    /// what it waits for, what is done, what is left and when it may finish.
    /// </summary>
    /// <param name="definition">The workflow the run began with, frozen as it ran: its loops' and handlers' bounds.</param>
    /// <param name="flow">The run's state in one file.</param>
    /// <param name="questions">The run's questions and answers; null where it holds none.</param>
    /// <param name="updates">The run's plan updates; null where it holds none.</param>
    /// <param name="engine">What the run's engine is doing, as its owner says it.</param>
    /// <param name="asOf">The moment the records were read: the facts' own stamp.</param>
    public static IReadOnlyList<string> Facts(
        WorkflowDefinition definition,
        FlowFile flow,
        FlowQuestionsFile? questions,
        FlowUpdatesFile? updates,
        FlowEngineState engine,
        DateTimeOffset asOf)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(engine);

        var rows = new List<(string Label, IReadOnlyList<string> Lines)>
        {
            ("running", Running(flow, asOf)),
            ("waits", Waits(flow, questions, updates)),
            ("done", Done(definition, flow)),
            ("left", Left(definition, flow, updates)),
            ("estimate", Estimate(definition, flow, questions, updates, asOf)),
        };

        return [Header(flow, engine, asOf), .. Rows(rows)];
    }

    /// <summary>The header: the run, the workflow, the moment, how the run stands and for how long, the pause where one holds it, and its engine.</summary>
    private static string Header(FlowFile flow, FlowEngineState engine, DateTimeOffset asOf)
    {
        var ended = Ended(flow.Status);
        var span = ended
            ? flow.Steps.Select(step => step.FinishedAt).OfType<DateTimeOffset>().DefaultIfEmpty(flow.StartedAt).Max() - flow.StartedAt
            : asOf - flow.StartedAt;

        return $"run '{flow.Run}' (workflow '{flow.Definition}'), as of {Clock(asOf)}: {flow.Status.ToLower(CultureInfo.InvariantCulture)}, "
            + (ended ? "ran for " : "for ") + Span(span)
            + (flow.Pause is { } pause ? $"; paused by a safe pause ({pause.Cause}) since {Clock(pause.At)}" : string.Empty)
            + $"; {engine.Said}";
    }

    /// <summary>What is running: each step instance in progress, its type, its agent, a reason step's model, its attempt, and for how long.</summary>
    private static IReadOnlyList<string> Running(FlowFile flow, DateTimeOffset asOf)
    {
        var running = flow.Steps.Where(step => !step.Group && step.State == nameof(FlowInstanceState.Running)).ToList();

        return running.Count == 0
            ? ["nothing is running"]
            : [.. running.Select(step => $"{step.Instance} ({step.Type ?? "step"}{(step.Agent is { } agent ? $", agent '{agent}'" : string.Empty)}{(step.Model is { } model ? $", model {model}" : string.Empty)}, "
                + $"attempt {step.Attempt.ToString(CultureInfo.InvariantCulture)}) for {Span(step.StartedAt is { } began ? asOf - began : TimeSpan.Zero)}")];
    }

    /// <summary>
    /// What the run waits for: each open question with since when and the command that answers it, a plan update under
    /// way or queued, each instance that waits on another or on a claim or room, and a <c>ci-wait</c> with the commit it
    /// watches.
    /// </summary>
    private static IReadOnlyList<string> Waits(FlowFile flow, FlowQuestionsFile? questions, FlowUpdatesFile? updates)
    {
        var waits = new List<string>();

        foreach (var question in questions?.Open ?? [])
        {
            waits.Add($"question {question.Id} ({question.Kind}) since {Clock(question.RaisedAt)}: "
                + OrchestrationReports.Line(FlowService.AnswerCommand, flow.Run, "<answer>", "--question", question.Id));
        }

        foreach (var update in updates?.Updates ?? [])
        {
            if (update.State == FlowUpdateStates.UnderWay)
            {
                waits.Add($"plan update {update.Id} under way: the run is held while '{update.Step ?? "its planning step"}' plans it");
            }
            else if (update.State == FlowUpdateStates.Queued)
            {
                waits.Add($"plan update {update.Id} is recorded and waits for the run to be gone on with");
            }
        }

        foreach (var step in flow.Steps.Where(step => LeftStates.Contains(step.State) && step.WaitsOn is { Count: > 0 }))
        {
            waits.Add($"{step.Instance} waits for {string.Join(", ", step.WaitsOn)}");
        }

        var pushed = flow.Steps
            .Where(step => step.Type == "push" && step.Output?["sha"] is JsonValue { } value && value.TryGetValue<string>(out var sha) && sha is { Length: > 0 })
            .Select(step => step.Output!["sha"]!.GetValue<string>())
            .LastOrDefault();

        foreach (var watch in flow.Steps.Where(step => !step.Group && step.State == nameof(FlowInstanceState.Running) && step.Type == "ci-wait"))
        {
            waits.Add($"{watch.Instance} is reading the checks of {(pushed is { } commit ? $"commit {commit}" : "the commit the run last pushed, which no push step of this run has named yet")}");
        }

        return waits.Count == 0 ? ["nothing"] : waits;
    }

    /// <summary>What is done: the instances that ended, the failures a handler took, the failures passed over, and what was spent of the budget.</summary>
    private static IReadOnlyList<string> Done(WorkflowDefinition definition, FlowFile flow)
    {
        var ended = flow.Steps.Count(step => EndedStates.Contains(step.State));
        var handled = flow.Steps.Count(step => step.HandledBy is not null);
        var passedOver = flow.Steps.Count(step => step.Failure == "passedOver");
        var budget = definition.Budget?.Usd;

        return
        [
            $"{Count(ended, "step instance")} ended"
                + (handled > 0 ? $", {Count(handled, "failure")} a handler took" : string.Empty)
                + (passedOver > 0 ? $", {Count(passedOver, "failure")} passed over" : string.Empty)
                + $"; {flow.Spent.Usd.ToString("0.00", CultureInfo.InvariantCulture)}{(budget is { } named ? $" of {named.ToString("0.00", CultureInfo.InvariantCulture)}" : string.Empty)} USD, "
                + $"{flow.Spent.Tokens.ToString(CultureInfo.InvariantCulture)} tokens spent",
        ];
    }

    /// <summary>
    /// What is left, at least and at most, with the bound that gave the most - and what may still come named: a
    /// <c>forEach</c> not yet expanded, a piece of a plan update not carried out to an end, and an iteration an update
    /// owes its loop and has not been given.
    /// </summary>
    private static IReadOnlyList<string> Left(WorkflowDefinition definition, FlowFile flow, FlowUpdatesFile? updates)
    {
        var left = flow.Steps.Where(step => LeftStates.Contains(step.State)).ToList();
        var numeric = AdditionsOf(definition, flow, updates, left, out var unbounded, out var forEach);
        var bound = definition.MaxInstances ?? WorkflowDefinition.DefaultMaxInstances;
        var room = Math.Max(0, bound - flow.Steps.Count);

        // A loop without a numeric bound of its own, and a forEach not yet expanded, are bounded only by the run's own
        // bound of instances: where either is open, the run's bound is what caps the most.
        var most = left.Count + (unbounded || numeric > room ? room : numeric);
        var said = numeric == 0 && !unbounded
            ? "nothing more can come"
            : unbounded || numeric > room
                ? $"the loops' bounds and the run's own bound of at most {bound.ToString(CultureInfo.InvariantCulture)} instances"
                : "the loops' bounds";

        var line = $"at least {Count(left.Count, "step instance")}, at most {Count(most, "step instance")} ({said})";

        foreach (var group in forEach)
        {
            line += $"; the items of '{group}' are not drawn yet";
        }

        var pieces = (updates?.Updates ?? [])
            .Where(update => update.State == FlowUpdateStates.Done)
            .SelectMany(update => update.ThePieces.Where(piece => piece.Kind != FlowUpdatePieceKinds.Told && piece.Ended is null).Select(piece => (update.Id, piece)))
            .ToList();

        if (pieces.Count > 0)
        {
            line += $"; {Count(pieces.Count, "piece")} of plan updates not carried out to an end yet";
        }

        foreach (var owed in (updates?.Updates ?? []).Where(update => update.AddedIteration is { } added && flow.Steps.All(step => !string.Equals(step.Instance, added, StringComparison.Ordinal))))
        {
            line += $"; update {owed.Id} owes '{owed.AddedIteration}' to its loop, and it is not made yet";
        }

        return [line];
    }

    /// <summary>When the run may finish, as a range from this run's own times - or why there is none.</summary>
    private static IReadOnlyList<string> Estimate(WorkflowDefinition definition, FlowFile flow, FlowQuestionsFile? questions, FlowUpdatesFile? updates, DateTimeOffset asOf)
    {
        if (Ended(flow.Status))
        {
            return ["none: the run has ended"];
        }

        var left = flow.Steps.Where(step => LeftStates.Contains(step.State)).ToList();

        if (left.Count == 0)
        {
            return ["none: nothing is left"];
        }

        var times = TimesOf(flow);

        if (times.Count == 0)
        {
            return ["none: no step instance has ended Done in this run yet, so there is no time of its own to go by"];
        }

        // A step none of whose instances has ended Done has no time to go by: its instances are counted and named, and
        // are in neither end.
        var named = left.Select(step => PathOf(step.Instance)).Where(path => !times.TryGetValue(path, out var time) || time.Longest <= TimeSpan.Zero).Distinct(StringComparer.Ordinal).ToList();
        var measured = left.Where(step => times.TryGetValue(PathOf(step.Instance), out var time) && time.Longest > TimeSpan.Zero).ToList();

        if (measured.Count == 0)
        {
            return [$"none: every step instance that is left is of a step that has ended no instance Done in this run{Named(named)}"];
        }

        var done = flow.Steps.Count(step => step.State == nameof(FlowInstanceState.Done));
        var loops = LoopAdditionsOf(definition, flow, updates, left, out var unbounded, out _);
        var line = $"between {Span(ChainOf(definition, measured, times, asOf))} and {Span(AllOf(definition, loops, left, times, asOf))} more, "
            + $"from the {Count(done, "step instance")} that ended Done in this run";

        if (unbounded)
        {
            line += "; a loop still open has no numeric bound of its own, and the time it may yet take is not in the estimate";
        }

        if (named.Count > 0)
        {
            line += named.Count == 1
                ? $"; 1 step of what is left has not run to an end in this run and is not in the estimate{Named(named)}"
                : $"; {named.Count.ToString(CultureInfo.InvariantCulture)} steps of what is left have not run to an end in this run and are not in the estimate{Named(named)}";
        }

        var open = (questions?.Open ?? []).Select(question => question.Id).ToList();

        if (open.Count > 0)
        {
            line += $"; not counting the time a person takes to answer {string.Join(", ", open)}";
        }

        if (flow.Steps.Any(step => LeftStates.Contains(step.State) && step.WaitsOn?.Any(wait => wait.Contains("claim", StringComparison.Ordinal) || wait.Contains("room", StringComparison.Ordinal) || wait.Contains("quiet", StringComparison.Ordinal)) == true))
        {
            line += "; not counting a wait for a claim, a counter or room";
        }

        return [line];
    }

    /// <summary>The shortest and the longest time instances of each step of the definition took to end Done in this run, by the step's declaration path.</summary>
    private static Dictionary<string, (TimeSpan Shortest, TimeSpan Longest)> TimesOf(FlowFile flow)
    {
        var times = new Dictionary<string, (TimeSpan Shortest, TimeSpan Longest)>(StringComparer.Ordinal);

        foreach (var step in flow.Steps.Where(step => step.State == nameof(FlowInstanceState.Done) && step.StartedAt is not null && step.FinishedAt is not null))
        {
            var took = step.FinishedAt!.Value - step.StartedAt!.Value;

            if (took < TimeSpan.Zero)
            {
                took = TimeSpan.Zero;
            }

            var path = PathOf(step.Instance);
            times[path] = times.TryGetValue(path, out var held)
                ? (TimeSpan.FromTicks(Math.Min(held.Shortest.Ticks, took.Ticks)), TimeSpan.FromTicks(Math.Max(held.Longest.Ticks, took.Ticks)))
                : (took, took);
        }

        return times;
    }

    /// <summary>
    /// The estimate's lower end: the longest chain of the instances that are left and measured, laid along what depends
    /// on what - a chain runs through two instances of one container where the one waits for the other, and through two
    /// iterations or visits of one step - each at its step's shortest time, and a step in progress with what is left of
    /// that time, and with nothing where it has already run longer.
    /// </summary>
    private static TimeSpan ChainOf(
        WorkflowDefinition definition,
        IReadOnlyList<FlowStepRecord> measured,
        IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times,
        DateTimeOffset asOf)
    {
        var waits = new Dictionary<string, bool>(StringComparer.Ordinal);
        var weights = measured.ToDictionary(step => step.Instance, step => LeftOf(step, times, shortest: true, asOf), StringComparer.Ordinal);
        var chains = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        TimeSpan Chain(FlowStepRecord step, ISet<string> along)
        {
            if (chains.TryGetValue(step.Instance, out var held))
            {
                return held;
            }

            // The declarations draw no cycle; a ring can only come of two visits of one step, and a lower bound is kept
            // by understating, never by counting a ring twice.
            if (!along.Add(step.Instance))
            {
                return TimeSpan.Zero;
            }

            var most = TimeSpan.Zero;

            foreach (var other in measured.Where(other => other.Instance != step.Instance && Blocks(definition, step, other, waits)))
            {
                var chain = Chain(other, along);
                most = chain > most ? chain : most;
            }

            along.Remove(step.Instance);
            chains[step.Instance] = weights[step.Instance] + most;

            return chains[step.Instance];
        }

        return measured.Max(step => Chain(step, new HashSet<string>(StringComparer.Ordinal)));
    }

    /// <summary>
    /// The estimate's upper end: every instance that is left and measured at its step's longest time, one after another,
    /// and every one a loop or a handler can still add at its numeric bound, each at the longest of its group's steps -
    /// unmeasured steps add nothing and are named beside the range.
    /// </summary>
    private static TimeSpan AllOf(
        WorkflowDefinition definition,
        IReadOnlyList<(string Path, int Iterations)> loops,
        IReadOnlyList<FlowStepRecord> left,
        IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times,
        DateTimeOffset asOf)
    {
        var all = left.Where(step => times.TryGetValue(PathOf(step.Instance), out var time) && time.Longest > TimeSpan.Zero)
            .Aggregate(TimeSpan.Zero, (whole, step) => whole + LeftOf(step, times, shortest: false, asOf));

        foreach (var (path, iterations) in loops)
        {
            var longest = LongestUnder(definition, path, times);

            if (longest > 0)
            {
                all += TimeSpan.FromTicks(longest * iterations);
            }
        }

        return all;
    }

    /// <summary>What one instance adds to an end of the estimate: its step's shortest or longest time, less what it has already run where it is in progress, and never below naught.</summary>
    private static TimeSpan LeftOf(FlowStepRecord step, IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times, bool shortest, DateTimeOffset asOf)
    {
        var whole = shortest ? times[PathOf(step.Instance)].Shortest : times[PathOf(step.Instance)].Longest;

        return step.State == nameof(FlowInstanceState.Running) && step.StartedAt is { } began
            ? TimeSpan.FromTicks(Math.Max(0, whole.Ticks - (asOf - began).Ticks))
            : whole;
    }

    /// <summary>Whether <paramref name="step"/> starts only after <paramref name="other"/> ends: by the declaration's own edges within one container, or by being a later iteration or visit of one step.</summary>
    private static bool Blocks(WorkflowDefinition definition, FlowStepRecord step, FlowStepRecord other, Dictionary<string, bool> memo)
    {
        if (ContainerOf(step.Instance) != ContainerOf(other.Instance))
        {
            return LaterOf(step.Instance, other.Instance) == true;
        }

        var key = $"{PathOf(step.Instance)}<-{PathOf(other.Instance)}";

        if (!memo.TryGetValue(key, out var waits))
        {
            waits = FlowGraph.Waits(definition, PathOf(step.Instance), PathOf(other.Instance));
            memo[key] = waits;
        }

        return waits || LaterOf(step.Instance, other.Instance) == true;
    }

    /// <summary>
    /// Whether one id is a later iteration or visit of the same step as the other - <c>rounds#3</c> after
    /// <c>rounds#2</c>, <c>gate+2</c> after <c>gate</c> - read at the first segment the two spell differently; null
    /// where they are not two of one step.
    /// </summary>
    private static bool? LaterOf(string one, string other)
    {
        if (!FlowInstanceId.SameStep(one, other))
        {
            return null;
        }

        var segments = one.Split('.');
        var others = other.Split('.');

        for (var at = 0; at < Math.Min(segments.Length, others.Length); at++)
        {
            if (string.Equals(segments[at], others[at], StringComparison.Ordinal))
            {
                continue;
            }

            return MarksOf(segments[at]) is { } mine && MarksOf(others[at]) is { } theirs && Later(mine.Visit, mine.Iteration, theirs.Visit, theirs.Iteration);
        }

        return false;

        static bool Later(int visit, int iteration, int otherVisit, int otherIteration)
            => visit != otherVisit ? visit > otherVisit : iteration > otherIteration;
    }

    /// <summary>One segment's marks: its visit, from 1, and its iteration, where it holds one; null where the segment holds an item, which no ordering of two instances reads.</summary>
    private static (int Visit, int Iteration)? MarksOf(string segment)
    {
        if (segment.Contains('['))
        {
            return null;
        }

        var visit = 1;
        var iteration = 0;

        for (var at = 0; at < segment.Length;)
        {
            var mark = segment[at];

            if (mark is not ('+' or '#'))
            {
                at++;
                continue;
            }

            var end = segment.IndexOfAny(['+', '#'], at + 1) is >= 0 and var next ? next : segment.Length;

            if (!int.TryParse(segment.AsSpan(at + 1, end - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                return null;
            }

            if (mark == '+')
            {
                visit = count;
            }
            else
            {
                iteration = count;
            }

            at = end;
        }

        return (visit, iteration);
    }

    /// <summary>
    /// Every instance a loop or a handler can still add at its bound, with each loop's group named and the iterations
    /// or visits left to it: for each open repeat, the iterations left to its bound; for each handler whose source is
    /// still under way, the visits left to its bound - each times the steps its group holds. Loops without a numeric
    /// bound, and a <c>forEach</c> not yet expanded, say so through <paramref name="unbounded"/> and
    /// <paramref name="forEach"/>.
    /// </summary>
    private static int AdditionsOf(
        WorkflowDefinition definition,
        FlowFile flow,
        FlowUpdatesFile? updates,
        IReadOnlyList<FlowStepRecord> left,
        out bool unbounded,
        out IReadOnlyList<string> forEach)
    {
        var additions = 0;

        foreach (var (path, iterations) in LoopAdditionsOf(definition, flow, updates, left, out unbounded, out forEach))
        {
            additions += iterations * (StepOf(definition, path) is { } step ? CountedUnder(step) : 1);
        }

        return additions;
    }

    /// <summary>
    /// Each loop and handler still open with the iterations or visits left to its bound, and where a loop has no numeric
    /// bound of its own, or a <c>forEach</c> has not been expanded, that said: the run's own bound of instances is the
    /// only one left.
    /// </summary>
    private static IReadOnlyList<(string Path, int Iterations)> LoopAdditionsOf(
        WorkflowDefinition definition,
        FlowFile flow,
        FlowUpdatesFile? updates,
        IReadOnlyList<FlowStepRecord> left,
        out bool unbounded,
        out IReadOnlyList<string> forEach)
    {
        unbounded = false;
        var groups = new List<string>();
        var loops = new List<(string Path, int Iterations)>();

        foreach (var (path, step) in WorkflowFiles.Steps(definition))
        {
            var open = left.Where(held => PathOf(held.Instance) == path).ToList();

            if (open.Count == 0)
            {
                continue;
            }

            if (step.Repeat is { } repeat)
            {
                var added = (updates?.Updates ?? []).Count(update => update.AddedIteration is { } iteration && PathOf(iteration) == path);
                var numeric = repeat.MaxIterations
                    ?? (repeat.Rounds is { } rounds && !repeat.BlockersUnbounded ? rounds + (repeat.Blocking is null ? 0 : repeat.BlockersLimitRounds ?? 3) : (int?)null);

                if (numeric is { } bound)
                {
                    var newest = open.Select(held => held.Iteration ?? 0).DefaultIfEmpty(0).Max();
                    var remaining = Math.Max(0, bound + added - newest);

                    if (remaining > 0)
                    {
                        loops.Add((path, remaining));
                    }
                }
                else
                {
                    // The loop's bound is its until alone, or its blockers unbounded: nothing numeric stops it.
                    unbounded = true;
                }
            }

            if (step.ForEach is not null)
            {
                var children = flow.Steps.Count(held => open.Any(group => ContainerOf(held.Instance) == group.Instance));

                if (children == 0)
                {
                    groups.Add(path);
                    unbounded = true;
                }
            }

            if (step.On is { } raised
                && SiblingOf(definition, path, raised.Step) is { } source
                && left.Any(held => PathOf(held.Instance) == source))
            {
                var visits = flow.Steps.Count(held => PathOf(held.Instance) == path);
                var remaining = Math.Max(0, (step.MaxVisits ?? 3) - visits);

                if (remaining > 0)
                {
                    loops.Add((path, remaining));
                }
            }
        }

        forEach = groups;

        return loops;
    }

    /// <summary>How many instances one iteration or visit of a group holds: its own record and every step declared under it.</summary>
    private static int CountedUnder(WorkflowStep step)
        => 1 + step.Steps.Sum(CountedUnder);

    /// <summary>The longest time one iteration or visit of the group at <paramref name="path"/> holds, at the longest of each step declared under it; naught where one of them has no time to go by.</summary>
    private static long LongestUnder(WorkflowDefinition definition, string path, IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times)
    {
        if (StepOf(definition, path) is not { } step)
        {
            return 0;
        }

        var longest = times.TryGetValue(path, out var own) ? own.Longest.Ticks : 0;

        foreach (var child in step.Steps)
        {
            var under = LongestUnder(definition, $"{path}.{child.Name}", times);

            if (under == 0)
            {
                return 0;
            }

            longest += under;
        }

        return longest;
    }

    /// <summary>The step a declaration path names; null where the definition declares no step at it.</summary>
    private static WorkflowStep? StepOf(WorkflowDefinition definition, string path)
        => WorkflowFiles.Steps(definition).FirstOrDefault(held => held.Path == path).Step;

    /// <summary>The path of the sibling <paramref name="name"/> of the step at <paramref name="path"/>, as its own <c>on</c> resolves it; null where none is declared beside it.</summary>
    private static string? SiblingOf(WorkflowDefinition definition, string path, string name)
    {
        var container = path.LastIndexOf('.') is >= 0 and var at ? path[..at] : null;

        return WorkflowFiles.Steps(definition).FirstOrDefault(held => held.Step.Name == name && (held.Path.LastIndexOf('.') is >= 0 and var other ? held.Path[..other] : null) == container).Path;
    }

    /// <summary>The declaration's path of an instance id: each segment's name, its marks gone.</summary>
    internal static string PathOf(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return string.Join(".", id.Split('.').Select(segment => segment.IndexOfAny(['+', '#', '[']) is >= 0 and var first ? segment[..first] : segment));
    }

    /// <summary>The container of an instance id: everything before its last segment, marks and all - the group instance its step runs in.</summary>
    private static string ContainerOf(string id)
        => id.LastIndexOf('.') is >= 0 and var at ? id[..at] : string.Empty;

    /// <summary>A count as the facts say it: the noun pluralized where the count is not one.</summary>
    private static string Count(long count, string noun)
        => $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? string.Empty : "s")}";

    /// <summary>A moment as the facts stamp one: UTC, to the second.</summary>
    private static string Clock(DateTimeOffset moment)
        => moment.ToUniversalTime().ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A duration as the facts write it: the ledger's own wording, whole seconds, and <c>0s</c> where it is shorter than a second.</summary>
    private static string Span(TimeSpan span)
        => LedgerReport.FormatDuration(span) is { Length: > 0 } said ? said : "0s";

    /// <summary>The steps named, where the facts name them: at most five, each by its declaration path, and a count where there are more.</summary>
    private static string Named(IReadOnlyList<string> paths)
        => paths.Count <= 5 ? $": {string.Join(", ", paths)}" : $": {string.Join(", ", paths.Take(5))} and {(paths.Count - 5).ToString(CultureInfo.InvariantCulture)} more";

    /// <summary>Whether the run's status is one it has ended in.</summary>
    private static bool Ended(string status)
        => status is nameof(FlowRunStatus.Done) or nameof(FlowRunStatus.Failed) or nameof(FlowRunStatus.Stopped);

    /// <summary>The states an instance has not ended in.</summary>
    private static readonly IReadOnlyList<string> LeftStates =
        [nameof(FlowInstanceState.NotStarted), nameof(FlowInstanceState.Running), nameof(FlowInstanceState.Paused)];

    /// <summary>The states an instance has ended in.</summary>
    private static readonly IReadOnlyList<string> EndedStates =
        [nameof(FlowInstanceState.Done), nameof(FlowInstanceState.Failed), nameof(FlowInstanceState.Skipped), nameof(FlowInstanceState.Stopped), nameof(FlowInstanceState.NotRun)];

    /// <summary>The rows under the header: each label once, its lines under it, the rest indented to the label's width.</summary>
    private static IReadOnlyList<string> Rows(IReadOnlyList<(string Label, IReadOnlyList<string> Lines)> rows)
    {
        var lines = new List<string>();

        foreach (var (label, held) in rows)
        {
            for (var at = 0; at < held.Count; at++)
            {
                lines.Add(at == 0 ? $"{label.PadRight(LabelWidth)}{held[at]}" : $"{new string(' ', LabelWidth)}{held[at]}");
            }
        }

        return lines;
    }
}
