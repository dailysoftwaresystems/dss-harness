using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The thresholds a report's signals are computed by, every one a key of <c>defaults.report</c> in
/// <c>config.json</c> and shipped with these values: a queue wait counts where it holds more than this share of the
/// step's own time, a cache share counts where it holds less under it on an attempt whose input tokens reach beyond
/// it, and the lists the signals name are bounded by the count.
/// </summary>
/// <param name="Top">How many of the slowest, the most expensive and the most retried steps the signals name.</param>
/// <param name="QueueSharePercent">The share of a step's own time a queue wait must pass to be a signal, in percent.</param>
/// <param name="CacheSharePercent">The share of an attempt's input tokens read from the cache under which it is a signal, in percent.</param>
/// <param name="CacheInputTokens">The input tokens an attempt must reach before its cache share is a signal.</param>
public sealed record FlowReportThresholds(
    int Top = 5,
    double QueueSharePercent = 10,
    double CacheSharePercent = 30,
    long CacheInputTokens = 50_000)
{
    /// <summary>What is wrong with these thresholds; null when nothing is.</summary>
    public string? Problem()
        => Top < 1 ? $"top is {Top.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : QueueSharePercent is < 0 or > 100 ? $"a queue share of {QueueSharePercent.ToString(CultureInfo.InvariantCulture)}%"
            : CacheSharePercent is < 0 or > 100 ? $"a cache share of {CacheSharePercent.ToString(CultureInfo.InvariantCulture)}%"
            : CacheInputTokens < 0 ? $"a cache token bound of {CacheInputTokens.ToString(CultureInfo.InvariantCulture)}"
            : null;
}

/// <summary>
/// The records a report is built from, and nothing else: the run's state in one file, its questions and answers, its
/// plan updates, its events, the closure ledgers of its item agents, the issues it owed a later round and the
/// definition it ran - all read from the run's own directory, never asked of a model or a host.
/// </summary>
/// <param name="Layout">The run's own paths, where its records and its handoffs are.</param>
/// <param name="Flow">The run's state, <c>flow.json</c>.</param>
/// <param name="Questions">The run's questions and answers; null where the run holds none.</param>
/// <param name="Updates">The run's plan updates; null where the run holds none.</param>
/// <param name="Events">The run's events, in the order they were written.</param>
/// <param name="Ledgers">Each item agent's closure ledger, by its agent; empty where the run holds none.</param>
/// <param name="Definition">The definition the run ran, frozen as it began; null where it cannot be read - the report then names no critical path.</param>
/// <param name="Thresholds">The thresholds its signals are computed by.</param>
/// <param name="NextRound">The issues the run owed a later round, <c>next-round.json</c>, each with the item that settled it; null where the run owed none.</param>
public sealed record FlowRunReportSource(
    OrchestratorLayout Layout,
    FlowFile Flow,
    FlowQuestionsFile? Questions = null,
    FlowUpdatesFile? Updates = null,
    IReadOnlyList<FlowEvent>? Events = null,
    IReadOnlyList<(string Agent, FlowClosureLedgerFile Ledger)>? Ledgers = null,
    WorkflowDefinition? Definition = null,
    FlowReportThresholds? Thresholds = null,
    FlowNextRoundFile? NextRound = null);

/// <summary>
/// The report of a run, built from its records alone (<see cref="FlowRunReport.Of"/>): what the run was and how it
/// stands, what it spent, one row per step with its waits and its run, the chain of steps a faster run must shorten,
/// every failed attempt, every question a person answered, what its reviews converged to and the deterministic flags
/// a reader starts from. The same records give the same bytes - the steps in the run's own order with ties by
/// instance id, every number in one fixed form, times as the records keep them - and the report never says now: a run
/// still going is said so at the time of its last record, marked partial. No step type writes it and no model words
/// it; the session's end writes it (<see cref="FlowReports.Write"/>) and <c>read-orchestrator-report</c> answers it.
/// </summary>
public static class FlowRunReport
{
    /// <summary>The report of <paramref name="source"/>'s run, from its records and nothing else.</summary>
    /// <param name="source">The run's records.</param>
    public static JsonNode Of(FlowRunReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var thresholds = source.Thresholds ?? new FlowReportThresholds();
        var steps = source.Flow.Steps.OrderBy(step => step.Instance, StringComparer.Ordinal).ToList();
        var at = LastRecordOf(source);
        var partial = source.Flow.Status is "Running";

        return new JsonObject
        {
            ["schema"] = 1,
            ["run"] = source.Flow.Run,
            ["definition"] = source.Flow.Definition,
            ["status"] = source.Flow.Status,
            ["partial"] = partial,
            ["at"] = at is { } when ? FlowTimeConverter.Written(when) : null,
            ["theRun"] = TheRun(source, steps, at),
            ["totals"] = Totals(source, steps),
            ["steps"] = new JsonArray([.. steps.Select(step => RowOf(step, source.Questions))]),
            ["criticalPath"] = CriticalPathOf(source, steps),
            ["errors"] = new JsonArray([.. ErrorsOf(source)]),
            ["people"] = new JsonArray([.. PeopleOf(source, at)]),
            ["convergence"] = ConvergenceOf(source),
            ["signals"] = new JsonArray([.. SignalsOf(source, steps, thresholds, at).Select(signal => new JsonObject
            {
                ["kind"] = signal.Kind,
                ["steps"] = new JsonArray([.. signal.Steps.Select(id => JsonValue.Create(id))]),
                ["said"] = signal.Said,
            })]),
            ["updates"] = source.Updates is null ? [] : new JsonArray([.. source.Updates.Updates.Select(UpdateOf)]),
            ["leftForAPerson"] = new JsonArray([.. LeftForAPerson(source).Select(line => JsonValue.Create(line))]),
        };
    }

    /// <summary>The report as text for a terminal: the sections as aligned tables, one line per row.</summary>
    /// <param name="report">The report's document, as <see cref="Of"/> answers it.</param>
    public static string Text(JsonNode report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            $"run {report["run"]!.GetValue<string>()} of {report["definition"]!.GetValue<string>()}: {report["status"]!.GetValue<string>()}"
                + (report["partial"]!.GetValue<bool>() ? $" (still going, at its last record {report["at"]?.GetValue<string>()})" : string.Empty),
            string.Empty,
            "the run:",
        };

        foreach (var member in ((JsonObject)report["theRun"]!).Where(member => member.Value is not null))
        {
            lines.Add($"  {member.Key}: {Said(member.Value)}");
        }

        lines.Add(string.Empty);
        lines.Add("totals:");

        foreach (var member in ((JsonObject)report["totals"]!)
                     .Where(member => member.Value is JsonValue or null and not JsonArray))
        {
            if (member.Value is JsonValue)
            {
                lines.Add($"  {member.Key}: {Said(member.Value)}");
            }
        }

        lines.Add(string.Empty);
        lines.Add(Table(["step", "type", "state", "tries", "waited", "ran", "model", "tokens", "usd", "exit"], Rows(report)));

        var critical = report["criticalPath"]!.AsArray();

        if (critical.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("critical path (what a faster run must shorten):");

            foreach (var link in critical)
            {
                lines.Add($"  {link!["step"]!.GetValue<string>()} - {link["waited"]?.GetValue<string>()}");
            }
        }

        foreach (var (name, member) in new[] { ("errors", "errors:"), ("people", "questions people answered:"), ("signals", "signals:") })
        {
            var array = report[name]!.AsArray();

            if (array.Count == 0)
            {
                continue;
            }

            lines.Add(string.Empty);
            lines.Add(member);

            foreach (var one in array)
            {
                lines.Add(name == "errors" ? $"  {ErrorSaid(one!)}" : $"  {Said(one)}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>The report as Markdown, for a pull request, a ticket or the clipboard.</summary>
    /// <param name="report">The report's document, as <see cref="Of"/> answers it.</param>
    public static string Markdown(JsonNode report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            $"# Run '{report["run"]!.GetValue<string>()}' - {report["definition"]!.GetValue<string>()}",
            string.Empty,
            $"{report["status"]!.GetValue<string>()}"
                + (report["partial"]!.GetValue<bool>() ? ", still going - said at the time of its last record" : string.Empty)
                + $", at {report["at"]?.GetValue<string>()}",
        };

        MarkdownCore(report, lines);

        foreach (var (member, said) in new[] { ("people", "## People"), ("updates", "## Plan updates") })
        {
            var array = report[member]!.AsArray();

            if (array.Count == 0)
            {
                continue;
            }

            lines.Add(string.Empty);
            lines.Add(said);
            lines.Add(string.Empty);

            foreach (var one in array)
            {
                lines.Add($"- {Said(one)}");
            }
        }

        var left = report["leftForAPerson"]!.AsArray();

        if (left.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## What is left for a person");
            lines.Add(string.Empty);

            foreach (var line in left)
            {
                lines.Add($"- {line!.GetValue<string>()}");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// The report's core sections into <paramref name="lines"/> - the run, the totals, the steps, the critical path,
    /// the errors, the convergence and the signals - which every form of the report holds, the session's own included.
    /// </summary>
    /// <param name="report">The report's document, as <see cref="Of"/> answers it.</param>
    /// <param name="lines">The lines so far, appended to.</param>
    internal static void MarkdownCore(JsonNode report, List<string> lines)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(lines);

        lines.Add(string.Empty);
        lines.Add("## The run");
        lines.Add(string.Empty);

        foreach (var member in ((JsonObject)report["theRun"]!).Where(member => member.Value is not null))
        {
            lines.Add($"- {member.Key}: {Said(member.Value)}");
        }

        lines.Add(string.Empty);
        lines.Add("## Totals");
        lines.Add(string.Empty);

        foreach (var member in ((JsonObject)report["totals"]!).Where(member => member.Value is not null))
        {
            if (member.Value is JsonValue)
            {
                lines.Add($"- {member.Key}: {Said(member.Value)}");
            }
            else if (member.Value is JsonArray rows)
            {
                lines.Add($"- {member.Key}:");

                foreach (var row in rows)
                {
                    lines.Add($"  - {Said(row)}");
                }
            }
        }

        lines.Add(string.Empty);
        lines.Add("## Steps");
        lines.Add(string.Empty);
        lines.Add("| step | type | state | tries | waited | ran | model | tokens | usd | exit |");
        lines.Add("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var row in Rows(report))
        {
            lines.Add($"| {string.Join(" | ", row.Select(cell => cell.Replace("|", "\\|")))} |");
        }

        var critical = report["criticalPath"]!.AsArray();

        if (critical.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## Critical path");
            lines.Add(string.Empty);

            foreach (var link in critical)
            {
                lines.Add($"- `{link!["step"]!.GetValue<string>()}` waited {link["waited"]?.GetValue<string>() ?? "nothing"} ({Said(link["seconds"])}s)");
            }
        }

        foreach (var (member, said) in new[] { ("errors", "## Errors"), ("signals", "## Signals") })
        {
            var array = report[member]!.AsArray();

            if (array.Count == 0)
            {
                continue;
            }

            lines.Add(string.Empty);
            lines.Add(said);
            lines.Add(string.Empty);

            foreach (var one in array)
            {
                lines.Add(member == "errors" ? $"- {ErrorSaid(one!)}" : $"- {Said(one)}");
            }
        }

        var convergence = (JsonObject)report["convergence"]!;

        if (convergence.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## Convergence");
            lines.Add(string.Empty);

            foreach (var loop in convergence["loopsLeft"]?.AsArray() ?? [])
            {
                lines.Add($"- {loop!["step"]!.GetValue<string>()} left {loop["left"]?.GetValue<string>()}");
            }

            foreach (var @class in convergence["classesClosed"]?.AsArray() ?? [])
            {
                lines.Add($"- '{@class!["class"]?.GetValue<string>()}': {@class["claim"]?.GetValue<string>()}");
            }

            foreach (var item in convergence["items"]?.AsArray() ?? [])
            {
                lines.Add($"- item {FlowClosureItem.In(item).Said()}");
            }

            foreach (var @class in convergence["classesReopened"]?.AsArray() ?? [])
            {
                lines.Add($"- '{@class!["class"]?.GetValue<string>()}', reopened {@class["reopens"]?.GetValue<string>()} time(s)");
            }

            foreach (var hotspot in convergence["hotspots"]?.AsArray() ?? [])
            {
                lines.Add($"- '{hotspot!["class"]?.GetValue<string>()}' home '{hotspot["path"]?.GetValue<string>()}', touched {hotspot["touches"]?.GetValue<string>()} time(s)");
            }

            foreach (var issue in convergence["nextRound"]?.AsArray() ?? [])
            {
                lines.Add($"- issue {issue!["said"]?.GetValue<string>()}");
            }
        }
    }

    /// <summary>The rows of the steps table, as both the text and the Markdown forms answer them.</summary>
    /// <param name="report">The report's document, as <see cref="Of"/> answers it.</param>
    private static List<string[]> Rows(JsonNode report)
    {
        var rows = new List<string[]>();

        foreach (var step in report["steps"]!.AsArray())
        {
            rows.Add(
            [
                step!["step"]!.GetValue<string>(),
                Said(step["type"]),
                step["state"]!.GetValue<string>(),
                Said(step["tries"]),
                Said(step["waited"]),
                Said(step["ran"]),
                Said(step["model"]),
                Said(step["tokens"]),
                Said(step["usd"]),
                Said(step["exit"]),
            ]);
        }

        return rows;
    }

    /// <summary>One aligned table over <paramref name="head"/> and <paramref name="rows"/>, its columns padded to their widest.</summary>
    private static string Table(IReadOnlyList<string> head, IReadOnlyList<string[]> rows)
    {
        var widths = head.Select((_, at) => Math.Max(head[at].Length, rows.Count == 0 ? 0 : rows.Max(row => row[at].Length))).ToList();
        return string.Join("\n", [string.Join("  ", head.Select((cell, at) => cell.PadRight(widths[at]))), .. rows.Select(row => string.Join("  ", row.Select((cell, at) => cell.PadRight(widths[at]))))]);
    }

    /// <summary>One failed attempt said as a person reads it: the step, the attempt, how it ended, and what handled the step.</summary>
    private static string ErrorSaid(JsonNode error)
        => $"{error["step"]?.GetValue<string>()}, attempt {Said(error["attempt"])}"
            + (error["exit"] is { } exit ? $", exit {Said(exit)}" : string.Empty)
            + (error["error"] is { } said ? $": {said.GetValue<string>()}" : string.Empty)
            + (error["handledBy"] is { } by ? $" (handled by {by.GetValue<string>()})" : string.Empty)
            + (error["question"] is { } asked ? $", asked as {asked.GetValue<string>()}" : string.Empty);

    /// <summary>How a value of the document is said as one line of text, whatever it holds.</summary>
    private static string Said(JsonNode? node)
        => node switch
        {
            null => "-",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<double>(out var number) => number.ToString("0.0##", CultureInfo.InvariantCulture),
            JsonValue value => value.ToJsonString(ReportJson.Options),
            JsonArray array => string.Join("; ", array.Select(Said)),
            JsonObject budget when budget.ContainsKey("usd") && budget.ContainsKey("tokens") && budget.ContainsKey("minutes") => BudgetSaid(budget),
            JsonObject _ => string.Join("; ", ((JsonObject)node).Select(member => $"{member.Key} {Said(member.Value)}")),
            _ => node.ToJsonString(ReportJson.Options),
        };

    /// <summary>The run's budget said as a person reads it: the dollars as <see cref="FlowDollars"/> says them, with the tokens and the minutes.</summary>
    private static string BudgetSaid(JsonObject budget)
        => budget["uncounted"] is { } uncounted
            ? $"at least {budget["usd"]!.GetValue<double>().ToString("0.00##", CultureInfo.InvariantCulture)} USD (the rest is not known: {uncounted.GetValue<string>()}), {budget["tokens"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture)} tokens, {budget["minutes"]!.GetValue<double>().ToString("0.0", CultureInfo.InvariantCulture)} minutes of reasoning"
            : $"{budget["usd"]!.GetValue<double>().ToString("0.00##", CultureInfo.InvariantCulture)} USD, {budget["tokens"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture)} tokens, {budget["minutes"]!.GetValue<double>().ToString("0.0", CultureInfo.InvariantCulture)} minutes of reasoning";

    /// <summary>When the run's records were last written: its last event's time, else its newest step's end, else its start.</summary>
    private static DateTimeOffset? LastRecordOf(FlowRunReportSource source)
    {
        if (source.Events is { Count: > 0 } events && events[^1].At is { } last)
        {
            return last;
        }

        var ends = source.Flow.Steps.Where(step => step.FinishedAt is not null).Select(step => step.FinishedAt!.Value).ToList();

        return ends.Count > 0 ? ends.Max() : source.Flow.StartedAt;
    }

    /// <summary>The run's own section: its sessions, its times, its budget.</summary>
    private static JsonObject TheRun(FlowRunReportSource source, IReadOnlyList<FlowStepRecord> steps, DateTimeOffset? at)
    {
        var events = source.Events ?? [];
        var began = events.FirstOrDefault(@event => @event.Kind == FlowEvent.KindOf(FlowChangeKind.SessionBegan))?.At ?? source.Flow.StartedAt;
        var sessions = events.Count(@event => @event.Kind == FlowEvent.KindOf(FlowChangeKind.SessionBegan))
            + events.Count(@event => @event.Kind == FlowEvent.KindOf(FlowChangeKind.Resumed));
        var wall = at is { } end && end > began ? Seconds((end - began).TotalSeconds) : null;
        var active = ActiveSecondsOf(steps);
        var paused = steps.Sum(step => step.Measure?.Paused ?? 0);
        var person = (source.Questions?.Questions ?? []).Sum(question => question.Answers.Count > 0 && question.Answers[0].At > question.RaisedAt
            ? (question.Answers[0].At - question.RaisedAt).TotalSeconds
            : 0);

        return new JsonObject
        {
            ["request"] = source.Flow.Inputs.Count == 0 ? null : string.Join(", ", source.Flow.Inputs.Select(input => $"{input.Key}={input.Value}")),
            ["sessions"] = sessions,
            ["beganAt"] = FlowTimeConverter.Written(began),
            ["wallSeconds"] = wall,
            ["activeSeconds"] = active,
            ["pausedSeconds"] = paused > 0 ? Seconds(paused) : null,
            ["waitingForAPersonSeconds"] = person > 0 ? Seconds(person) : null,
            ["budget"] = SpentOf(source.Flow.Spent),
        };
    }

    /// <summary>The union of the run's step intervals - the time at least one step was running - in seconds; null where none ran.</summary>
    private static double? ActiveSecondsOf(IReadOnlyList<FlowStepRecord> steps)
    {
        var spans = steps
            .Where(step => step.StartedAt is { } began && step.FinishedAt is { } ended && ended > began)
            .Select(step => (Began: step.StartedAt!.Value, Ended: step.FinishedAt!.Value))
            .OrderBy(span => span.Began)
            .ToList();

        if (spans.Count == 0)
        {
            return null;
        }

        var total = 0.0;
        var (from, to) = spans[0];

        foreach (var span in spans.Skip(1))
        {
            if (span.Began > to)
            {
                total += (to - from).TotalSeconds;
                (from, to) = span;
            }
            else if (span.Ended > to)
            {
                to = span.Ended;
            }
        }

        return Seconds(total + (to - from).TotalSeconds);
    }

    /// <summary>What the run spent and did, summed over its steps and split by what served them.</summary>
    private static JsonObject Totals(FlowRunReportSource source, IReadOnlyList<FlowStepRecord> steps)
    {
        var attempts = steps.SelectMany(step => step.Measure?.Attempts ?? []).ToList();
        var tokens = attempts.Where(attempt => attempt.Tokens is not null).Select(attempt => attempt.Tokens!).ToList();
        var input = tokens.Sum(use => use.InputTokens);
        var cacheRead = tokens.Sum(use => use.CacheReadTokens);
        var byModel = attempts
            .Where(attempt => attempt.Usd > 0)
            .GroupBy(attempt => $"{attempt.Provider ?? "-"}|{attempt.Model ?? "-"}")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new JsonObject
            {
                ["provider"] = group.First().Provider,
                ["model"] = group.First().Model,
                ["usd"] = UsdOf(group.Sum(attempt => attempt.Usd ?? 0)),
                ["tokens"] = group.Sum(attempt => attempt.Tokens?.AllTokens ?? 0),
            })
            .ToList();

        return new JsonObject
        {
            ["tokens"] = tokens.Count == 0 ? null : new JsonObject
            {
                ["input"] = input,
                ["output"] = tokens.Sum(use => use.OutputTokens),
                ["cacheRead"] = cacheRead,
                ["cacheWrite"] = tokens.Sum(use => use.CacheWriteTokens),
                ["cacheWrite1h"] = tokens.Sum(use => use.CacheWrite1hTokens),
                ["searches"] = tokens.Sum(use => use.Searches),
            },
            ["cacheSharePercent"] = input + cacheRead > 0 ? ShareOf(cacheRead, input + cacheRead) : null,
            ["dollarsByModel"] = new JsonArray([.. byModel]),
            ["usd"] = Math.Round(steps.Sum(step => (step.Measure?.Totals?.Usd ?? 0) + (step.Measure?.Advice?.Usd ?? 0)), 4, MidpointRounding.AwayFromZero),
            ["uncounted"] = source.Flow.Spent.Uncounted,
            ["agents"] = steps.Where(step => step.Agent is { Length: > 0 }).Select(step => step.Agent!).Distinct(StringComparer.Ordinal).Count(),
            ["stepsRun"] = steps.Count(step => step.State == nameof(FlowInstanceState.Done)),
            ["stepsSkipped"] = steps.Count(step => step.SkippedBecause is not null || step.NotRunBecause is not null),
            ["stepsFailed"] = steps.Count(step => step.State == nameof(FlowInstanceState.Failed)),
            ["retries"] = Math.Max(0, attempts.Count - steps.Count(step => step.Measure?.Attempts is { Count: > 0 })),
            ["fallbacks"] = attempts.Count(attempt => attempt.FallbackFrom is not null),
        };
    }

    /// <summary>One row of the steps table: the instance's own numbers, said shortly.</summary>
    private static JsonObject RowOf(FlowStepRecord step, FlowQuestionsFile? questions)
    {
        var measure = step.Measure;

        return new JsonObject
        {
            ["step"] = step.Instance,
            ["type"] = step.Type,
            ["state"] = step.State,
            ["tries"] = measure?.Attempts is { Count: > 1 } count ? count.Count : null,
            ["waited"] = measure?.Waited is { Count: > 0 } waited ? string.Join(", ", waited.Select(wait => $"{SecondsOf(wait.Seconds)} on {wait.Cause}")) : null,
            ["ran"] = measure is { Ran: { } ran } ? SecondsOf(ran) : null,
            ["model"] = measure?.Attempts is { Count: > 0 } attempts ? attempts[^1].Model : null,
            ["tokens"] = measure?.Totals?.Tokens?.AllTokens,
            ["usd"] = measure?.Totals?.Usd is { } usd || measure?.Advice?.Usd is { } advised ? UsdOf((measure?.Totals?.Usd ?? 0) + (measure?.Advice?.Usd ?? 0)) : null,
            ["exit"] = step.Exit,
            ["error"] = step.Error,
            ["handler"] = step.HandledBy,
            ["pausedOn"] = step.Question is { } waiting && questions is not null && questions.Of(waiting) is { State: "open" } open ? waiting : null,
        };
    }

    /// <summary>
    /// The chain of <c>dependsOn</c> whose times add to the run's active time, step by step with what each waited
    /// for: the longest road through the run's instances by each one's wall time, its links only where a step's
    /// declaration names its neighbour in its own scope - what a faster run must shorten. Ties read one way, by
    /// instance id. Empty where the definition cannot be read, or nothing ended.
    /// </summary>
    private static JsonArray CriticalPathOf(FlowRunReportSource source, IReadOnlyList<FlowStepRecord> steps)
    {
        var path = new JsonArray();

        if (source.Definition is not { } definition)
        {
            return path;
        }

        // Each ended leaf instance by its id, its wall time, and the instances its declaration names in its own scope.
        var ended = steps
            .Where(step => step.StartedAt is { } && step.FinishedAt is { } && !step.Group)
            .ToDictionary(step => step.Instance, StringComparer.Ordinal);
        var weights = ended.ToDictionary(step => step.Key, step => (step.Value.FinishedAt!.Value - step.Value.StartedAt!.Value).TotalSeconds, StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var step in ended.Values)
        {
            var (scope, name) = SplitInstance(step.Instance);

            if (DeclareOf(definition, scope, name) is not { } declaration)
            {
                continue;
            }

            foreach (var dependency in declaration.DependsOn)
            {
                var before = string.IsNullOrEmpty(scope) ? dependency : $"{scope}.{dependency}";

                if (!ReferenceEquals(ended.GetValueOrDefault(before), step) && ended.ContainsKey(before))
                {
                    (edges.TryGetValue(before, out var after) ? after : edges[before] = []).Add(step.Instance);
                }
            }
        }

        // The longest road from each instance, remembered as it is walked; a cycle - which the validator refuses, and
        // a damaged record can still leave - is cut where it is met.
        var best = new Dictionary<string, (double Weight, string? Next)>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        foreach (var instance in ended.Keys.Order(StringComparer.Ordinal))
        {
            _ = visiting.Add(instance);
            _ = Walk(instance);
            _ = visiting.Remove(instance);
        }

        var start = best.OrderByDescending(pair => pair.Value.Weight).ThenBy(pair => pair.Key, StringComparer.Ordinal).FirstOrDefault().Key;

        for (var instance = start; instance is not null; instance = best[instance].Next)
        {
            var measure = ended[instance].Measure;
            var waitedOn = measure?.Waited is { Count: > 0 } waits
                ? string.Join(", ", waits.OrderByDescending(wait => wait.Seconds).Select(wait => $"{SecondsOf(wait.Seconds)} on {wait.Cause}"))
                : null;

            path.Add(new JsonObject
            {
                ["step"] = instance,
                ["seconds"] = Seconds(weights[instance]),
                ["waited"] = waitedOn,
            });
        }

        return path;

        double Walk(string instance)
        {
            if (best.TryGetValue(instance, out var reached))
            {
                return reached.Weight;
            }

            best[instance] = (weights[instance], null);
            var weight = weights[instance];
            string? next = null;

            if (edges.TryGetValue(instance, out var followers))
            {
                foreach (var follower in followers.Order(StringComparer.Ordinal))
                {
                    if (!visiting.Add(follower))
                    {
                        continue;
                    }

                    var through = weights[instance] + Walk(follower);
                    _ = visiting.Remove(follower);

                    if (through > weight)
                    {
                        (weight, next) = (through, follower);
                    }
                }
            }

            best[instance] = (weight, next);
            return weight;
        }

        (string Scope, string Name) SplitInstance(string instance)
        {
            var at = instance.LastIndexOf('.');

            return at < 0 ? (string.Empty, instance) : (instance[..at], instance[(at + 1)..]);
        }

        static WorkflowStep? DeclareOf(WorkflowDefinition definition, string scope, string name)
            => StepsOf(definition, scope).FirstOrDefault(step => step.Name == name);

        static IReadOnlyList<WorkflowStep> StepsOf(WorkflowDefinition definition, string scope)
        {
            var steps = definition.Steps;

            foreach (var part in scope.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var group = steps.FirstOrDefault(step => step.Name == part && step.Steps is { Count: > 0 });

                if (group?.Steps is not { Count: > 0 } nested)
                {
                    return [];
                }

                steps = nested;
            }

            return steps;
        }
    }

    /// <summary>Every attempt that failed: the step, its number, its code and its error, and what handled the step.</summary>
    private static IEnumerable<JsonObject> ErrorsOf(FlowRunReportSource source)
        => source.Flow.Steps
            .SelectMany(step => (step.Measure?.Attempts ?? []).Where(attempt => attempt.Outcome == nameof(FlowInstanceState.Failed))
                .Select(attempt => (step, attempt)))
            .OrderBy(failed => failed.step.Instance, StringComparer.Ordinal)
            .ThenBy(failed => failed.attempt.N)
            .Select(failed => new JsonObject
            {
                ["step"] = failed.step.Instance,
                ["attempt"] = failed.attempt.N,
                ["exit"] = failed.attempt.Exit,
                ["error"] = failed.attempt.Error ?? failed.step.Error,
                ["handledBy"] = failed.step.HandledBy,
                ["question"] = failed.step.Question,
            });

    /// <summary>Every question a person was asked: when, how long it waited, the recommendation it was shown, and what was answered.</summary>
    private static IEnumerable<JsonObject> PeopleOf(FlowRunReportSource source, DateTimeOffset? at)
        => (source.Questions?.Questions ?? [])
            .OrderBy(question => question.Id, StringComparer.Ordinal)
            .Select(question =>
            {
                var answer = question.Answers.LastOrDefault();
                var waited = answer is not null ? answer.At - question.RaisedAt : at - question.RaisedAt;

                return new JsonObject
                {
                    ["question"] = question.Id,
                    ["kind"] = question.Kind,
                    ["askedAt"] = FlowTimeConverter.Written(question.RaisedAt),
                    ["waitedSeconds"] = waited is { } span && span > TimeSpan.Zero ? Seconds(span.TotalSeconds) : null,
                    ["answered"] = answer?.Answer,
                    ["by"] = answer is { } saidBy ? QuestionsText.Who(saidBy.By) : null,
                    ["recommendation"] = question.Recommendation is { } recommended ? $"{recommended.State}: {recommended.Why}" : null,
                };
            });

    /// <summary>What the run's reviews converged to: its loops' leftovers, the classes its work closed, the classes its reviews reopened and its hotspots.</summary>
    private static JsonObject ConvergenceOf(FlowRunReportSource source)
    {
        var convergence = new JsonObject();

        var leftovers = source.Flow.Steps.Where(step => step.Leftover is not null).ToList();

        if (leftovers.Count > 0)
        {
            convergence["loopsLeft"] = new JsonArray([.. leftovers.Select(step => new JsonObject
            {
                ["step"] = step.Instance,
                ["left"] = FlowValue.Of(step.Leftover!.DeepClone()).AsTemplateText(),
            })]);
        }

        // What the run's items closed, so a repository can see whether its rounds converge: each item's record as its
        // newest closure check judged it, gathered by the one owner the run's words read too.
        var closing = ClosingOf(source);
        var closed = FlowClosureRecords.RecordsOf(closing)
            .SelectMany(record => record)
            .Select(entry => (Class: TextOf(entry["class"]), Claim: TextOf(entry["claim"])))
            .ToList();

        if (closed.Count > 0)
        {
            convergence["classesClosed"] = new JsonArray([.. closed.Select(@class => new JsonObject
            {
                ["class"] = @class.Class,
                ["claim"] = @class.Claim,
            })]);
        }

        // What each item's work did to its classes, by the same owner and in the same words as the run's {run.counts}.
        var items = FlowClosureRecords.ItemsOf(closing);

        if (items.Count > 0)
        {
            convergence["items"] = new JsonArray([.. items.Select(item => (JsonNode?)item.ToJson())]);
        }

        // The classes the reviews reopened, by each item's ledger - the one count of them, read by the signals too.
        var reopened = (source.Ledgers ?? [])
            .SelectMany(ledger => ledger.Ledger.Classes)
            .Select(entry => (Class: entry.Class, Reopens: entry.Reopens.ToString(CultureInfo.InvariantCulture)))
            .ToList();

        if (reopened.Count > 0)
        {
            convergence["classesReopened"] = new JsonArray([.. reopened.Select(@class => new JsonObject
            {
                ["class"] = @class.Class,
                ["reopens"] = @class.Reopens,
            })]);
        }

        var hotspots = FlowClosureRecords.HotspotsOf(closing)
            .Select(hotspot => (hotspot.Class, hotspot.Path, Touches: hotspot.Touches.ToString(CultureInfo.InvariantCulture)))
            .ToList();

        if (hotspots.Count > 0)
        {
            convergence["hotspots"] = new JsonArray([.. hotspots.Select(hotspot => new JsonObject
            {
                ["class"] = hotspot.Class,
                ["path"] = hotspot.Path,
                ["touches"] = hotspot.Touches,
            })]);
        }

        // Each issue the run carried to a later round, as its list of them says what became of it: fixed there by the work
        // item that settled it, or left owed.
        if (source.NextRound is { Issues.Count: > 0 } next)
        {
            convergence["nextRound"] = new JsonArray([.. next.Issues.Select(issue => (JsonNode?)new JsonObject
            {
                ["id"] = issue.Id,
                ["said"] = issue.Said(),
                ["settledBy"] = issue.SettledBy,
            })]);
        }

        return convergence;
    }

    /// <summary>The run's steps as the one owner of what its closure checks and routings say gathers them (<see cref="FlowClosureRecords"/>).</summary>
    private static IReadOnlyList<FlowClosureRecords.Step> ClosingOf(FlowRunReportSource source)
        => [.. source.Flow.Steps.Select(step => new FlowClosureRecords.Step(step.Instance, step.Group ? null : step.Type, step.State == nameof(FlowInstanceState.Done), step.Output))];

    /// <summary>One JSON value's text, where it is a string or a number; an empty string otherwise.</summary>
    private static string TextOf(JsonNode? node)
        => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            JsonValue value when value.TryGetValue<double>(out var fractional) => fractional.ToString("0.0##", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

    /// <summary>The deterministic flags a reader starts from, each naming its steps and its numbers.</summary>
    private static IEnumerable<FlowReportSignal> SignalsOf(FlowRunReportSource source, IReadOnlyList<FlowStepRecord> steps, FlowReportThresholds thresholds, DateTimeOffset? at)
    {
        // A step's own time is its wall time where its records hold both ends, else what its measure sums - the same
        // measure every form of the report reads.
        var whole = steps.Select(step => (Step: step, Whole: step.StartedAt is { } began && step.FinishedAt is { } ended && ended > began
            ? (ended - began).TotalSeconds
            : (step.Measure?.Ran ?? 0) + (step.Measure?.Waited?.Sum(wait => wait.Seconds) ?? 0) + (step.Measure?.Paused ?? 0))).ToList();

        foreach (var slow in whole.OrderByDescending(step => step.Whole).ThenBy(step => step.Step.Instance, StringComparer.Ordinal).Take(thresholds.Top).Where(step => step.Whole > 0))
        {
            yield return new("slowestStep", [slow.Step.Instance], $"{slow.Step.Instance} took {SecondsOf(slow.Whole)} of run time");
        }

        foreach (var dear in steps.OrderByDescending(step => (step.Measure?.Totals?.Usd ?? 0) + (step.Measure?.Advice?.Usd ?? 0)).ThenBy(step => step.Instance, StringComparer.Ordinal).Take(thresholds.Top).Where(step => (step.Measure?.Totals?.Usd ?? 0) + (step.Measure?.Advice?.Usd ?? 0) > 0))
        {
            yield return new("mostExpensiveStep", [dear.Instance], $"{dear.Instance} cost {UsdOf((dear.Measure?.Totals?.Usd ?? 0) + (dear.Measure?.Advice?.Usd ?? 0))}");
        }

        foreach (var retried in steps.Where(step => step.Measure?.Attempts is { Count: > 1 }).OrderByDescending(step => step.Measure!.Attempts!.Count).ThenBy(step => step.Instance, StringComparer.Ordinal).Take(thresholds.Top))
        {
            yield return new("retriedStep", [retried.Instance], $"{retried.Instance} ran {retried.Measure!.Attempts!.Count.ToString(CultureInfo.InvariantCulture)} attempts");
        }

        foreach (var moved in steps.SelectMany(step => (step.Measure?.Attempts ?? []).Where(attempt => attempt.FallbackFrom is not null).Select(attempt => (step, attempt))).OrderBy(moved => moved.step.Instance, StringComparer.Ordinal).ThenBy(moved => moved.attempt.N))
        {
            yield return new("fallback", [moved.step.Instance], $"{moved.step.Instance} attempt {moved.attempt.N.ToString(CultureInfo.InvariantCulture)} moved from {moved.attempt.FallbackFrom} to {moved.attempt.Model}: {moved.attempt.FallbackReason}");
        }

        var longestPerson = (source.Questions?.Questions ?? [])
            .Select(question => (question, Waited: question.Answers.Count > 0 && question.Answers[0].At > question.RaisedAt
                ? question.Answers[0].At - question.RaisedAt
                : at - question.RaisedAt))
            .Where(waited => waited.Waited is { } span && span > TimeSpan.Zero)
            .OrderByDescending(waited => waited.Waited!.Value)
            .ThenBy(waited => waited.question.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (longestPerson.question is { } person)
        {
            yield return new("longestWaitForAPerson", [person.Id], $"{person.Id} waited {SecondsOf(longestPerson.Waited!.Value.TotalSeconds)} for its answer");
        }

        foreach (var queued in whole.Where(step => step.Whole > 0 && (step.Step.Measure?.Waited ?? []).Any(wait => FlowWaits.ForRoom(wait.Cause) && wait.Seconds / step.Whole * 100 > thresholds.QueueSharePercent)).OrderBy(step => step.Step.Instance, StringComparer.Ordinal))
        {
            var waits = string.Join(", ", queued.Step.Measure!.Waited!.Where(wait => FlowWaits.ForRoom(wait.Cause)).Select(wait => $"{SecondsOf(wait.Seconds)} on {wait.Cause}"));
            yield return new("queueWait", [queued.Step.Instance], $"{queued.Step.Instance} waited {waits}, over {thresholds.QueueSharePercent.ToString(CultureInfo.InvariantCulture)}% of its {SecondsOf(queued.Whole)}");
        }

        foreach (var cold in steps.SelectMany(step => (step.Measure?.Attempts ?? []).Select(attempt => (step, attempt)))
                     .Where(attempt => attempt.attempt.Tokens is { } tokens && tokens.InputTokens + tokens.CacheReadTokens > thresholds.CacheInputTokens && ShareOf(tokens.CacheReadTokens, tokens.InputTokens + tokens.CacheReadTokens) < thresholds.CacheSharePercent)
                     .OrderBy(attempt => attempt.step.Instance, StringComparer.Ordinal))
        {
            var tokens = cold.attempt.Tokens!;
            yield return new("cacheShare", [cold.step.Instance], $"{cold.step.Instance} attempt {cold.attempt.N.ToString(CultureInfo.InvariantCulture)} read {ShareOf(tokens.CacheReadTokens, tokens.InputTokens + tokens.CacheReadTokens)} of its input from the cache ({tokens.CacheReadTokens.ToString(CultureInfo.InvariantCulture)} of {(tokens.InputTokens + tokens.CacheReadTokens).ToString(CultureInfo.InvariantCulture)} tokens)");
        }

        if ((source.Questions?.Questions ?? []).Any(question => question.Kind == "loopBound"))
        {
            yield return new("reviewAtItsBound", [], "a loop reached its bound and asked");
        }

        foreach (var twice in (source.Ledgers ?? []).SelectMany(ledger => ledger.Ledger.Classes.Where(@class => FlowClosureReopens.AsksItsHomeAudit(@class.Reopens)).Select(@class => (ledger.Agent, @class))))
        {
            yield return new("classReopenedTwice", [], $"'{twice.@class.Class}' was reopened {twice.@class.Reopens.ToString(CultureInfo.InvariantCulture)} times");
        }

        foreach (var hotspot in steps.Where(step => step.Type == FlowClosureRecords.CheckType && step.Output?["hotspots"] is JsonArray { Count: > 0 }))
        {
            yield return new("hotspot", [hotspot.Instance], $"{hotspot.Instance} named {hotspot.Output!["hotspots"]!.AsArray().Count.ToString(CultureInfo.InvariantCulture)} hotspot(s)");
        }

        if (source.Flow.Spent.Uncounted is { } uncounted)
        {
            yield return new("uncountedSpend", [], $"the run's dollars are not known past {UsdOf(source.Flow.Spent.Usd)}: {uncounted}");
        }
    }

    /// <summary>What is left for a person, as the session's end answers it - one line each.</summary>
    private static IReadOnlyList<string> LeftForAPerson(FlowRunReportSource source)
        => FlowReports.LeftForAPerson(source.Layout, source.Flow, source.Questions, source.Updates).ToList();

    /// <summary>What the run has spent of its budget, as every document says it.</summary>
    private static JsonObject SpentOf(FlowBudgetRecord spent)
        => FlowReports.SpentOf(spent);

    /// <summary>One plan update, as every document says it.</summary>
    private static JsonObject UpdateOf(FlowUpdateEntry update)
        => FlowReports.UpdateOf(update);

    /// <summary>Seconds rounded to one decimal - the one form every duration in a report's numbers takes.</summary>
    private static double? Seconds(double seconds)
        => Math.Round(seconds, 1, MidpointRounding.AwayFromZero);

    /// <summary>Seconds said with one decimal, the one form every duration in a report's words takes.</summary>
    private static string SecondsOf(double seconds)
        => seconds.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Dollars said with four decimals and the currency, the one form every cost in a report takes.</summary>
    private static string UsdOf(double usd)
        => $"{usd.ToString("0.0000", CultureInfo.InvariantCulture)} USD";

    /// <summary>A share said with one decimal, in percent.</summary>
    private static double ShareOf(long part, long whole)
        => Math.Round(part * 100.0 / whole, 1, MidpointRounding.AwayFromZero);
}

/// <summary>One deterministic flag a report's reader starts from, naming its steps and its numbers.</summary>
/// <param name="Kind">Which flag it is, as the report's <c>signals</c> hold it.</param>
/// <param name="Steps">The instances it names; empty where it names none.</param>
/// <param name="Said">The flag in words, with its numbers.</param>
public sealed record FlowReportSignal(string Kind, IReadOnlyList<string> Steps, string Said);
