using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The engine's facts about a run at a moment: computed from the run's records
/// and never guessed - how the run stands, what is running, what it waits for, what is done, what is left at least and
/// at most, and when it may finish as a range from this run's own times. Pure: it reads nothing, holds nothing and
/// changes nothing; the records are read by the same store methods <c>list-orchestrator</c> reads and handed to it,
/// with the moment they were read.
/// </summary>
/// <remarks>
/// <para>
/// What is left counts at least the instances that exist and have not ended - a stopped one among them, which runs
/// again at the resume - and at most those and everything the run may still make by the engine's own bounds and rules
/// (<see cref="FlowComing"/>): each member of a scope still open not drawn yet, each loop's iterations, each handler's
/// visits with the steps its <c>then</c> sends the run back over, and each visit the recovery can still make for a
/// failure with the steps it sends the run back over - each bound widened by what the run's answers and plan updates
/// granted past it (<see cref="FlowGrants"/>). What no numeric bound holds - a <c>forEach</c> not yet expanded, a loop
/// with no numeric bound, a step whose pre-existing issues each raise a triage, a handler that sends the run back outside
/// its own group, a plan update still to be planned - is bounded only by the run's own bound of instances, and the
/// facts say which bound gave the number, what no numeric bound holds, and that a question open at a bound can be
/// answered with more past it.
/// </para>
/// <para>
/// The estimate rests on this run alone: of each step of the definition that works, the shortest and the longest time
/// an instance of it took to end <c>Done</c> - a group's own span is its members' time, and is never counted beside
/// them. Its lower end lays the instances that are left along what depends on what, as the engine orders them - so work
/// that runs side by side is counted once - each at its step's shortest time, and a step in progress with what is left
/// of that; its upper end sums every instance that is left and everything the run may still make, each at its longest,
/// one after another. A step none of whose instances has ended <c>Done</c> has no time to go by:
/// its instances are counted and named, and are in neither end. It leaves out, and says that it does, the time a person
/// takes to answer and a wait for room - a claim, the run to be quiet, a counter of its limits - as the admission that
/// wrote the wait classes it (<see cref="FlowWaits"/>). Where nothing has ended, or the run has ended - done or failed,
/// for a stopped run goes on at its resume - there is no estimate, and the facts say which.
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
            ("left", Left(definition, flow, questions, updates)),
            ("estimate", Estimate(definition, flow, questions, updates, asOf)),
        };

        return [Header(flow, engine, asOf), .. Rows(rows)];
    }

    /// <summary>The header: the run, the workflow, the moment, how the run stands and for how long, the pause where one holds it, and its engine.</summary>
    private static string Header(FlowFile flow, FlowEngineState engine, DateTimeOffset asOf)
    {
        var ended = HasEnded(flow.Status);
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
    /// What is left, at least and at most, with the bound that gave the most - and what may still come named: what no
    /// numeric bound holds, a question open at a bound, whose answer can grant more past it, a piece of a plan update not
    /// carried out to an end, and an iteration an update owes its loop and has not been given.
    /// </summary>
    private static IReadOnlyList<string> Left(WorkflowDefinition definition, FlowFile flow, FlowQuestionsFile? questions, FlowUpdatesFile? updates)
    {
        var left = flow.Steps.Where(step => LeftStates.Contains(step.State)).ToList();
        var coming = new FlowComing(definition, flow, left, questions, updates);
        var numeric = coming.Count;
        var unbounded = coming.Unbounded.Count > 0;
        var bound = definition.MaxInstances ?? WorkflowDefinition.DefaultMaxInstances;
        var room = (long)Math.Max(0, bound - flow.Steps.Count);

        // What no numeric bound holds is bounded only by the run's own bound of instances: where any is, the run's bound
        // is what caps the most. A question open at a bound is a person's to answer, and its more grants past the bound,
        // which no record holds yet.
        var most = left.Count + (unbounded || numeric > room ? room : numeric);
        var asking = (questions?.Open ?? []).Where(question => FlowGrants.Granting.Contains(question.Kind, StringComparer.Ordinal)).ToList();
        var said = numeric == 0 && !unbounded && asking.Count == 0
            ? "nothing more can come"
            : unbounded || numeric > room
                ? $"the engine's bounds and the run's own bound of at most {bound.ToString(CultureInfo.InvariantCulture)} instances"
                : "the engine's bounds";

        var line = $"at least {Count(left.Count, "step instance")}, at most {Count(most, "step instance")} ({said})";

        foreach (var why in coming.Unbounded)
        {
            line += $"; {why}";
        }

        foreach (var open in asking)
        {
            line += $"; question {open.Id} ({open.Kind}) can be answered with more past its bound";
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
            line += $"; update {owed.Id} added {FlowReports.IterationSaidOf(owed.AddedIteration!)}, and it is not made yet";
        }

        return [line];
    }

    /// <summary>
    /// When the run may finish, as a range from this run's own times - or why there is none. Only the steps that work
    /// are timed: a group's own record spans its members' work, which is counted in its members.
    /// </summary>
    private static IReadOnlyList<string> Estimate(WorkflowDefinition definition, FlowFile flow, FlowQuestionsFile? questions, FlowUpdatesFile? updates, DateTimeOffset asOf)
    {
        if (HasEnded(flow.Status))
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
        var working = left.Where(step => !step.Group).ToList();
        var named = working.Select(step => FlowInstanceId.PathOf(step.Instance)).Where(path => !times.TryGetValue(path, out var time) || time.Longest <= TimeSpan.Zero).Distinct(StringComparer.Ordinal).ToList();
        var measured = working.Where(step => times.TryGetValue(FlowInstanceId.PathOf(step.Instance), out var time) && time.Longest > TimeSpan.Zero).ToList();

        if (measured.Count == 0 && named.Count > 0)
        {
            return [$"none: every step instance that is left is of a step that has ended no instance Done in this run{Named(named)}"];
        }

        var done = flow.Steps.Count(step => step.State == nameof(FlowInstanceState.Done));
        var coming = new FlowComing(definition, flow, left, questions, updates);
        var least = measured.Count == 0 ? TimeSpan.Zero : ChainOf(definition, measured, times, asOf);
        var line = $"between {Span(least)} and {Span(AllOf(coming, measured, times, asOf))} more, "
            + $"from the {Count(done, "step instance")} that ended Done in this run";

        if (coming.Unbounded.Count > 0)
        {
            line += "; what no numeric bound holds is not in the estimate";
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

        // A wait for room - a claim, the run to be quiet, a counter of the run's limits - as the admission that wrote it
        // classes its own words (FlowWaits): only the run's pace ends it, and no step's time says how long it lasts.
        if (flow.Steps.Any(step => LeftStates.Contains(step.State) && step.WaitsOn?.Any(FlowWaits.ForRoom) == true))
        {
            line += "; not counting a wait for a claim, for the run to be quiet or for room under its limits";
        }

        return [line];
    }

    /// <summary>
    /// The shortest and the longest time instances of each step of the definition that works took to end Done in this
    /// run, by the step's declaration path - a group's own span left out, for its members' times are its time.
    /// </summary>
    private static Dictionary<string, (TimeSpan Shortest, TimeSpan Longest)> TimesOf(FlowFile flow)
    {
        var times = new Dictionary<string, (TimeSpan Shortest, TimeSpan Longest)>(StringComparer.Ordinal);

        foreach (var step in flow.Steps.Where(step => !step.Group && step.State == nameof(FlowInstanceState.Done) && step.StartedAt is not null && step.FinishedAt is not null))
        {
            var took = step.FinishedAt!.Value - step.StartedAt!.Value;

            if (took < TimeSpan.Zero)
            {
                took = TimeSpan.Zero;
            }

            var path = FlowInstanceId.PathOf(step.Instance);
            times[path] = times.TryGetValue(path, out var held)
                ? (TimeSpan.FromTicks(Math.Min(held.Shortest.Ticks, took.Ticks)), TimeSpan.FromTicks(Math.Max(held.Longest.Ticks, took.Ticks)))
                : (took, took);
        }

        return times;
    }

    /// <summary>
    /// The estimate's lower end: the longest chain of the instances that are left and measured, laid along what depends
    /// on what as the engine orders two instances (<see cref="Blocks"/>) - each at its step's shortest time, and a step
    /// in progress with what is left of that time, and with nothing where it has already run longer.
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
    /// and everything the run may still make, each unit at the longest one of it takes (<see cref="FlowComing.LongestOf"/>)
    /// as many times as it may come - a unit with no time to go by adds nothing.
    /// </summary>
    private static TimeSpan AllOf(
        FlowComing coming,
        IReadOnlyList<FlowStepRecord> measured,
        IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times,
        DateTimeOffset asOf)
    {
        var all = measured.Aggregate(0L, (whole, step) => FlowComing.Add(whole, LeftOf(step, times, shortest: false, asOf).Ticks));

        foreach (var unit in coming.Units)
        {
            all = FlowComing.Add(all, FlowComing.Times(unit.Times, coming.LongestOf(unit, times)));
        }

        return TimeSpan.FromTicks(Math.Min(all, TimeSpan.MaxValue.Ticks));
    }

    /// <summary>What one instance adds to an end of the estimate: its step's shortest or longest time, less what it has already run where it is in progress, and never below naught.</summary>
    private static TimeSpan LeftOf(FlowStepRecord step, IReadOnlyDictionary<string, (TimeSpan Shortest, TimeSpan Longest)> times, bool shortest, DateTimeOffset asOf)
    {
        var whole = shortest ? times[FlowInstanceId.PathOf(step.Instance)].Shortest : times[FlowInstanceId.PathOf(step.Instance)].Longest;

        return step.State == nameof(FlowInstanceState.Running) && step.StartedAt is { } began
            ? TimeSpan.FromTicks(Math.Max(0, whole.Ticks - (asOf - began).Ticks))
            : whole;
    }

    /// <summary>
    /// Whether <paramref name="step"/> starts only after <paramref name="other"/> ends, as the engine orders two
    /// instances: an earlier visit or iteration of a group both sit in holds it (<see cref="FlowInstanceId.Precedes"/>),
    /// and in the same group instances its declaration waits for the other's (<see cref="FlowGraph.Waits"/>); another
    /// item of a <c>forEach</c> runs beside it.
    /// </summary>
    private static bool Blocks(WorkflowDefinition definition, FlowStepRecord step, FlowStepRecord other, Dictionary<string, bool> memo)
    {
        if (FlowInstanceId.Precedes(other.Instance, step.Instance) is { } ordered)
        {
            return ordered;
        }

        var (waiter, target) = (FlowInstanceId.PathOf(step.Instance), FlowInstanceId.PathOf(other.Instance));
        var key = $"{waiter}<-{target}";

        if (!memo.TryGetValue(key, out var waits))
        {
            waits = FlowGraph.Waits(definition, waiter, target);
            memo[key] = waits;
        }

        return waits;
    }

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

    /// <summary>
    /// Whether the run's status is one it has ended in: done or failed. A stopped run goes on at its resume, and so is
    /// not ended.
    /// </summary>
    internal static bool HasEnded(string status)
        => status is nameof(FlowRunStatus.Done) or nameof(FlowRunStatus.Failed);

    /// <summary>The states an instance has not ended in: a stopped one among them, which runs again at the resume.</summary>
    private static readonly IReadOnlyList<string> LeftStates =
        [nameof(FlowInstanceState.NotStarted), nameof(FlowInstanceState.Running), nameof(FlowInstanceState.Paused), nameof(FlowInstanceState.Stopped)];

    /// <summary>The states an instance has ended in.</summary>
    private static readonly IReadOnlyList<string> EndedStates =
        [nameof(FlowInstanceState.Done), nameof(FlowInstanceState.Failed), nameof(FlowInstanceState.Skipped), nameof(FlowInstanceState.NotRun)];

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
