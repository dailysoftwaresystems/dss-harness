using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The thresholds a report's signals are computed by, every one a key of <c>defaults.report</c> in
/// <c>config.json</c> and shipped with these values: a queue wait counts where it holds more than this share of the
/// step's own time, a cache share counts where it holds less under it on an attempt whose input tokens reach beyond
/// it, a graph page count names a step whose read of one answer reaches past it, and the lists the signals name are
/// bounded by the count.
/// </summary>
/// <param name="Top">How many of the slowest, the most expensive and the most retried steps the signals name.</param>
/// <param name="QueueSharePercent">The share of a step's own time a queue wait must pass to be a signal, in percent.</param>
/// <param name="CacheSharePercent">The share of an attempt's input tokens read from the cache under which it is a signal, in percent.</param>
/// <param name="CacheInputTokens">The input tokens an attempt must reach before its cache share is a signal.</param>
/// <param name="GraphPageDepth">The page a step's read of one graph answer must stay within; a deeper one is a signal, for a query that wide asks too much.</param>
public sealed record FlowReportThresholds(
    int Top = 5,
    double QueueSharePercent = 10,
    double CacheSharePercent = 30,
    long CacheInputTokens = 50_000,
    int GraphPageDepth = 3)
{
    /// <summary>
    /// Each key of <c>defaults.report</c>, with its shipped value and the values it takes, as help says them - read from
    /// this record's own defaults and <see cref="Problem"/>'s bounds, never written twice.
    /// </summary>
    public static IReadOnlyList<(string Key, string Default, string Takes)> Keys { get; } =
    [
        ("top", new FlowReportThresholds().Top.ToString(CultureInfo.InvariantCulture), "a count from 1"),
        ("queueSharePercent", new FlowReportThresholds().QueueSharePercent.ToString(CultureInfo.InvariantCulture), "a percent from 0 to 100"),
        ("cacheSharePercent", new FlowReportThresholds().CacheSharePercent.ToString(CultureInfo.InvariantCulture), "a percent from 0 to 100"),
        ("cacheInputTokens", new FlowReportThresholds().CacheInputTokens.ToString(CultureInfo.InvariantCulture), "a count of tokens from 0"),
        ("graphPageDepth", new FlowReportThresholds().GraphPageDepth.ToString(CultureInfo.InvariantCulture), "a count of pages from 1"),
    ];

    /// <summary>What is wrong with these thresholds; null when nothing is.</summary>
    public string? Problem()
        => Top < 1 ? $"top is {Top.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : QueueSharePercent is < 0 or > 100 || double.IsNaN(QueueSharePercent) ? $"a queue share of {QueueSharePercent.ToString(CultureInfo.InvariantCulture)}%"
            : CacheSharePercent is < 0 or > 100 || double.IsNaN(CacheSharePercent) ? $"a cache share of {CacheSharePercent.ToString(CultureInfo.InvariantCulture)}%"
            : CacheInputTokens < 0 ? $"a cache token bound of {CacheInputTokens.ToString(CultureInfo.InvariantCulture)}"
            : GraphPageDepth < 1 ? $"a graph page depth of {GraphPageDepth.ToString(CultureInfo.InvariantCulture)}, and the first page is 1"
            : null;

    /// <summary>
    /// The thresholds a run's report is computed by - the one owner every report reads them from, the session's end, the
    /// read verb and the analysis alike: the configuration's <c>defaults.report</c>, each key it leaves out at its shipped
    /// value, with the count <c>--top</c> asks for in place of its <c>top</c> where one is given.
    /// </summary>
    /// <param name="config">The repository's configuration.</param>
    /// <param name="top">The count <c>--top</c> asked for; null where none was.</param>
    public static FlowReportThresholds Of(Configuration.HarnessConfig config, int? top = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var configured = config.Defaults.Report ?? new FlowReportThresholds();
        return top is { } count ? configured with { Top = count } : configured;
    }
}

/// <summary>
/// The records a report is built from, and nothing else: the run's state in one file, its questions and answers, its
/// plan updates, its events, the closure ledgers of its item agents, the issues it owed a later round and the
/// definition it ran - all read from the run's own directory, never asked of a model or a host - and how the reader
/// asked for it.
/// </summary>
/// <param name="Layout">The run's own paths, where its records and its handoffs are.</param>
/// <param name="Flow">The run's state, <c>flow.json</c>.</param>
/// <param name="Questions">The run's questions and answers; null where the run holds none.</param>
/// <param name="Updates">The run's plan updates; null where the run holds none.</param>
/// <param name="Events">The run's events, in the order they were written.</param>
/// <param name="Ledgers">Each item agent's closure ledger, by its agent; empty where the run holds none.</param>
/// <param name="Definition">The definition the run ran, frozen as it began; null where it cannot be read - the report then names no critical path, and says why.</param>
/// <param name="Thresholds">The thresholds its signals are computed by (<see cref="FlowReportThresholds.Of"/>).</param>
/// <param name="EventsTorn">Whether the events file's last line was torn - a writer killed mid-line - and set aside.</param>
/// <param name="By">What the dollars are grouped by: <c>model</c> (where null), <c>provider</c>, <c>step</c>, <c>agent</c> or <c>type</c>.</param>
/// <param name="Steps">The instances whose rows alone the steps table keeps; null for every one.</param>
/// <param name="NextRound">The issues the run owed a later round, <c>next-round.json</c>, each with the item that settled it; null where the run owed none.</param>
/// <param name="DefinitionWhy">Why the definition could not be read, where the file is there and does not read; null where it reads or is not there.</param>
public sealed record FlowRunReportSource(
    OrchestratorLayout Layout,
    FlowFile Flow,
    FlowQuestionsFile? Questions = null,
    FlowUpdatesFile? Updates = null,
    IReadOnlyList<FlowEvent>? Events = null,
    IReadOnlyList<(string Agent, FlowClosureLedgerFile Ledger)>? Ledgers = null,
    WorkflowDefinition? Definition = null,
    FlowReportThresholds? Thresholds = null,
    bool EventsTorn = false,
    string? By = null,
    IReadOnlySet<string>? Steps = null,
    FlowNextRoundFile? NextRound = null,
    string? DefinitionWhy = null);

/// <summary>
/// The report of a run, built from its records alone (<see cref="FlowRunReport.Of"/>): what the run was and how it
/// stands, what it spent, one row per step with its waits and its run, the chain of steps a faster run must shorten,
/// every failed attempt, every question a person answered, what its reviews converged to and the deterministic flags
/// a reader starts from. The same records give the same bytes - the steps in the order the run made them, every row of
/// a list with an id of its own (<c>id</c>: a step's and a link's its instance, a failed attempt's
/// <c>&lt;instance&gt;@&lt;attempt&gt;</c>, a signal's <c>&lt;kind&gt;#&lt;n&gt;</c>, a question's its own, a dollars
/// group's its name, a convergence row's its list, the item, step or agent that said it and what it is of), every number in one fixed form, times as the
/// records keep them - and the report never says now: a run still going is said so at the time of its last record,
/// marked partial. Every form - text, JSON, Markdown - holds every section. No step type writes it and no model words it; the session's end writes it
/// (<see cref="FlowReports.Write"/>) and <c>read-orchestrator-report</c> answers it.
/// </summary>
public static partial class FlowRunReport
{
    /// <summary>The report's own schema: what a reader of its document, the analyst among them, reads it by.</summary>
    public const int Schema = 2;

    /// <summary>What the dollars may be grouped by, the model's first.</summary>
    public static IReadOnlyList<string> Groupings { get; } = ["model", "provider", "step", "agent", "type"];

    /// <summary>The report of <paramref name="source"/>'s run, from its records and nothing else.</summary>
    /// <param name="source">The run's records.</param>
    public static JsonObject Of(FlowRunReportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var thresholds = source.Thresholds ?? new FlowReportThresholds();
        var steps = source.Flow.Steps;
        var at = LastRecordOf(source);
        var partial = source.Flow.Status is "Running";
        var notes = new JsonArray();

        if (source.EventsTorn)
        {
            notes.Add($"the last line of '{source.Layout.EventsFile}' was torn - a writer stopped mid-line - and is not read: the report is as of the event before it");
        }

        if (source.Definition is null)
        {
            notes.Add(source.DefinitionWhy is { } why
                ? $"'{source.Layout.DefinitionFile}' does not read: {why} - so the report names no critical path: the steps' dependencies are the definition's"
                : $"'{source.Layout.DefinitionFile}' is not there, so the report names no critical path: the steps' dependencies are the definition's");
        }

        return new JsonObject
        {
            ["schema"] = Schema,
            ["run"] = source.Flow.Run,
            ["definition"] = source.Flow.Definition,
            ["status"] = source.Flow.Status,
            ["partial"] = partial,
            ["at"] = at is { } when ? FlowTimeConverter.Written(when) : null,
            ["notes"] = notes,
            ["theRun"] = TheRun(source, at),
            ["totals"] = Totals(source),
            ["steps"] = new JsonArray([.. steps.Where(step => source.Steps is null || source.Steps.Contains(step.Instance)).Select(step => RowOf(step, source.Questions))]),
            ["criticalPath"] = CriticalPathOf(source, steps, at),
            ["errors"] = new JsonArray([.. ErrorsOf(source)]),
            ["people"] = new JsonArray([.. PeopleOf(source, at)]),
            ["convergence"] = ConvergenceOf(source),
            ["signals"] = new JsonArray([.. Numbered(SignalsOf(source, steps, thresholds, at)).Select(signal => new JsonObject
            {
                ["id"] = signal.Id,
                ["kind"] = signal.Kind,
                ["steps"] = new JsonArray([.. signal.Steps.Select(id => JsonValue.Create(id))]),
                ["said"] = signal.Said,
            })]),
            ["updates"] = source.Updates is null ? [] : new JsonArray([.. source.Updates.Updates.Select(UpdateOf)]),
            ["leftForAPerson"] = new JsonArray([.. LeftForAPerson(source).Select(line => JsonValue.Create(line))]),
        };
    }

    /// <summary>The report as text for a terminal: every section, the tables aligned, one line per row.</summary>
    /// <param name="report">The report's document, as <see cref="Of"/> answers it.</param>
    public static string Text(JsonNode report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            $"run {report["run"]!.GetValue<string>()} of {report["definition"]!.GetValue<string>()}: {report["status"]!.GetValue<string>()}"
                + (report["partial"]!.GetValue<bool>() ? $" (still going, at its last record {report["at"]?.GetValue<string>()})" : string.Empty),
        };

        foreach (var section in Sections(report))
        {
            lines.Add(string.Empty);

            if (section.Table is { } table)
            {
                lines.Add($"{section.Title}:");
                lines.Add(Table(table.Head, table.Rows));
                continue;
            }

            lines.Add($"{section.Title}:");
            lines.AddRange(section.Lines.Select(line => $"  {line}"));
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
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Every section of the report into <paramref name="lines"/> as Markdown, each a heading with its rows: the one
    /// rendering the read verb's Markdown and the session's own <c>report.md</c> share.
    /// </summary>
    /// <param name="report">The report's document, as <see cref="Of"/> answers it.</param>
    /// <param name="lines">The lines so far, appended to.</param>
    /// <param name="updatesAndLeft">Whether the plan updates and what is left for a person are said here; the session's own report says both in full itself.</param>
    internal static void MarkdownCore(JsonNode report, List<string> lines, bool updatesAndLeft = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(lines);

        foreach (var section in Sections(report, updatesAndLeft))
        {
            lines.Add(string.Empty);
            lines.Add($"## {char.ToUpperInvariant(section.Title[0])}{section.Title[1..]}");
            lines.Add(string.Empty);

            if (section.Table is { } table)
            {
                lines.Add($"| {string.Join(" | ", table.Head)} |");
                lines.Add($"|{string.Concat(table.Head.Select(_ => "---|"))}");
                lines.AddRange(table.Rows.Select(row => $"| {string.Join(" | ", row.Select(cell => cell.Replace("|", "\\|", StringComparison.Ordinal)))} |"));
                continue;
            }

            lines.AddRange(section.Lines.Select(line => $"- {line}"));
        }
    }

    /// <summary>One section of a rendered report: its title, and its lines or its table.</summary>
    private sealed record Section(string Title, IReadOnlyList<string> Lines, (IReadOnlyList<string> Head, IReadOnlyList<string[]> Rows)? Table = null);

    /// <summary>The report's sections as every rendering says them, in order, an empty one left out.</summary>
    /// <param name="report">The report's document.</param>
    /// <param name="updatesAndLeft">Whether the plan updates and what is left for a person are among them.</param>
    private static IEnumerable<Section> Sections(JsonNode report, bool updatesAndLeft = true)
    {
        if (report["notes"] is JsonArray { Count: > 0 } notes)
        {
            yield return new("notes", [.. notes.Select(note => note!.GetValue<string>())]);
        }

        yield return new("the run", [.. ((JsonObject)report["theRun"]!).Where(member => member.Value is not null).Select(member => $"{member.Key}: {SaidAs(member.Key, member.Value)}")]);

        var totals = (JsonObject)report["totals"]!;
        var totalsSaid = new List<string>();

        foreach (var (key, value) in totals.Where(member => member.Value is not null))
        {
            switch (value)
            {
                case JsonObject tokens when key == "tokens":
                    totalsSaid.Add($"tokens: {string.Join(", ", tokens.Select(kind => $"{Said(kind.Value)} {kind.Key}"))}");
                    break;

                case JsonObject dollars when key == "dollarsBy":
                    totalsSaid.Add($"dollars by {dollars["key"]!.GetValue<string>()}:");
                    totalsSaid.AddRange(dollars["groups"]!.AsArray().Select(group => $"  {group!["group"]!.GetValue<string>()}: {UsdOf(group["usd"]!.GetValue<double>())}, {Said(group["tokens"])} tokens"));
                    break;

                default:
                    totalsSaid.Add($"{key}: {SaidAs(key, value)}");
                    break;
            }
        }

        yield return new("totals", totalsSaid);

        string[] head = ["step", "type", "state", "tries", "waited", "ran", "model", "tokens", "usd", "exit", "error", "handler", "skipped"];
        yield return new("steps", [], (head, [.. report["steps"]!.AsArray().Select(step => head.Select(column => SaidAs(column, step![column])).ToArray())]));

        if (report["criticalPath"] is JsonArray { Count: > 0 } critical)
        {
            yield return new("critical path (what a faster run must shorten)", [.. critical.Select(link => $"{link!["step"]!.GetValue<string>()}: {SecondsOf(link["seconds"]!.GetValue<double>())} s" + (link["waited"] is { } waited ? $", waited {waited.GetValue<string>()}" : string.Empty))]);
        }

        if (report["errors"] is JsonArray { Count: > 0 } errors)
        {
            yield return new("errors", [.. errors.Select(error => ErrorSaid(error!))]);
        }

        if (report["people"] is JsonArray { Count: > 0 } people)
        {
            yield return new("questions people were asked", [.. people.Select(person => Said(person))]);
        }

        var convergence = (JsonObject)report["convergence"]!;
        var converged = new List<string>();
        converged.AddRange((convergence["loopsLeft"]?.AsArray() ?? []).Select(loop => $"{loop!["step"]!.GetValue<string>()} left {loop["left"]?.GetValue<string>()}"));
        converged.AddRange((convergence["classesClosed"]?.AsArray() ?? []).Select(closed => $"'{Said(closed!["class"])}': {Said(closed["claim"])}"));
        converged.AddRange((convergence["items"]?.AsArray() ?? []).Select(item => $"item {FlowClosureItem.In(item).Said()}"));
        converged.AddRange((convergence["classesReopened"]?.AsArray() ?? []).Select(reopened => $"'{Said(reopened!["class"])}', reopened {Said(reopened["reopens"])} time(s)"));
        converged.AddRange((convergence["hotspots"]?.AsArray() ?? []).Select(hotspot => $"'{Said(hotspot!["class"])}' home '{Said(hotspot["path"])}', touched {Said(hotspot["touches"])} time(s)"));
        converged.AddRange((convergence["nextRound"]?.AsArray() ?? []).Select(issue => $"issue {Said(issue!["said"])}"));

        if (converged.Count > 0)
        {
            yield return new("convergence", converged);
        }

        if (report["signals"] is JsonArray { Count: > 0 } signals)
        {
            yield return new("signals", [.. signals.Select(signal => $"{signal!["id"]!.GetValue<string>()}: {signal["said"]!.GetValue<string>()}")]);
        }

        if (!updatesAndLeft)
        {
            yield break;
        }

        if (report["updates"] is JsonArray { Count: > 0 } updates)
        {
            yield return new("plan updates", [.. updates.Select(update => Said(update))]);
        }

        if (report["leftForAPerson"] is JsonArray { Count: > 0 } left)
        {
            yield return new("what is left for a person", [.. left.Select(line => line!.GetValue<string>())]);
        }
    }

    /// <summary>One aligned table over <paramref name="head"/> and <paramref name="rows"/>, its columns padded to their widest.</summary>
    private static string Table(IReadOnlyList<string> head, IReadOnlyList<string[]> rows)
    {
        var widths = head.Select((_, at) => Math.Max(head[at].Length, rows.Count == 0 ? 0 : rows.Max(row => row[at].Length))).ToList();
        return string.Join("\n", [string.Join("  ", head.Select((cell, at) => cell.PadRight(widths[at]))).TrimEnd(), .. rows.Select(row => string.Join("  ", row.Select((cell, at) => cell.PadRight(widths[at]))).TrimEnd())]);
    }

    /// <summary>One failed attempt said as a person reads it: its id, how it ended and why, what threw, its first error, and what handled the step.</summary>
    private static string ErrorSaid(JsonNode error)
        => $"{error["id"]!.GetValue<string>()}"
            + (error["exit"] is { } exit ? $", exit {Said(exit)}" : string.Empty)
            + (error["cause"] is { } cause ? $" ({cause.GetValue<string>()})" : string.Empty)
            + (error["error"] is { } said ? $": {said.GetValue<string>()}" : string.Empty)
            + (error["exception"] is JsonObject thrown ? $"; threw {thrown["type"]?.GetValue<string>()}: {thrown["message"]?.GetValue<string>()}" : string.Empty)
            + (error["firstError"] is JsonObject first ? $"; first error {Said(first)}" : string.Empty)
            + (error["handledBy"] is { } by ? $" (handled by {by.GetValue<string>()})" : string.Empty)
            + (error["question"] is { } asked ? $", asked as {asked.GetValue<string>()}" : string.Empty);

    /// <summary>How the member <paramref name="key"/> is said: a cost in dollars with its currency, a time in seconds with one decimal, anything else as it is.</summary>
    private static string SaidAs(string key, JsonNode? node)
        => node is JsonValue value && value.TryGetValue<double>(out var number)
            ? key == "usd" ? UsdOf(number)
                : key.EndsWith("Seconds", StringComparison.Ordinal) ? $"{SecondsOf(number)} s"
                : Said(node)
            : Said(node);

    /// <summary>
    /// How a value of the document is said as one line of text, whatever it holds: an object as its members, each by its
    /// name and its value, a member that holds nothing - null or an empty list - left out, so no name is said without one.
    /// </summary>
    private static string Said(JsonNode? node)
        => node switch
        {
            null => "-",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<long>(out var whole) => whole.ToString(CultureInfo.InvariantCulture),
            JsonValue value when value.TryGetValue<double>(out var number) => number.ToString("0.0###", CultureInfo.InvariantCulture),
            JsonValue value => value.ToJsonString(ReportJson.Options),
            JsonArray array => string.Join("; ", array.Select(Said)),
            JsonObject budget when budget.ContainsKey("usd") && budget.ContainsKey("tokens") && budget.ContainsKey("minutes") => BudgetSaid(budget),
            JsonObject members => string.Join("; ", members.Where(member => member.Value is not (null or JsonArray { Count: 0 })).Select(member => $"{member.Key} {SaidAs(member.Key, member.Value)}")),
            _ => node.ToJsonString(ReportJson.Options),
        };

    /// <summary>The run's budget said as a person reads it: the dollars as <see cref="FlowDollars"/> says them, with the tokens and the minutes.</summary>
    private static string BudgetSaid(JsonObject budget)
        => (budget["uncounted"] is { } uncounted
                ? $"at least {UsdOf(budget["usd"]!.GetValue<double>())} (the rest is not known: {uncounted.GetValue<string>()})"
                : UsdOf(budget["usd"]!.GetValue<double>()))
            + $", {budget["tokens"]!.GetValue<long>().ToString(CultureInfo.InvariantCulture)} tokens, {budget["minutes"]!.GetValue<double>().ToString("0.0", CultureInfo.InvariantCulture)} minutes of reasoning";

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

    /// <summary>
    /// The run's own section: its sessions, its times, its budget. Its active time is the union of every attempt's runs -
    /// the time at least one step's work ran; its pause is the time between its sessions, from the last record of one to
    /// the start of the next; its wait for a person is each question's (<see cref="PersonWaitOf"/>), summed.
    /// </summary>
    private static JsonObject TheRun(FlowRunReportSource source, DateTimeOffset? at)
    {
        var events = source.Events ?? [];
        var began = events.FirstOrDefault(@event => @event.Kind == FlowEvent.KindOf(FlowChangeKind.SessionBegan))?.At ?? source.Flow.StartedAt;
        var starts = new[] { FlowEvent.KindOf(FlowChangeKind.SessionBegan), FlowEvent.KindOf(FlowChangeKind.Resumed) };
        var sessions = events.Count(@event => starts.Contains(@event.Kind, StringComparer.Ordinal));
        var wall = at is { } end && end > began ? Seconds((end - began).TotalSeconds) : null;
        var paused = 0.0;

        for (var index = 1; index < events.Count; index++)
        {
            if (starts.Contains(events[index].Kind, StringComparer.Ordinal) && events[index].At > events[index - 1].At)
            {
                paused += (events[index].At - events[index - 1].At).TotalSeconds;
            }
        }

        var person = (source.Questions?.Questions ?? []).Sum(question => PersonWaitOf(question, at)?.TotalSeconds ?? 0);

        return new JsonObject
        {
            ["request"] = source.Flow.Inputs.Count == 0 ? null : string.Join(", ", source.Flow.Inputs.Select(input => $"{input.Key}={input.Value}")),
            ["sessions"] = sessions,
            ["beganAt"] = FlowTimeConverter.Written(began),
            ["wallSeconds"] = wall,
            ["activeSeconds"] = ActiveSecondsOf(source.Flow.Steps, at),
            ["pausedSeconds"] = paused > 0 ? Seconds(paused) : null,
            ["waitingForAPersonSeconds"] = person > 0 ? Seconds(person) : null,
            ["budget"] = SpentOf(source.Flow.Spent with { Usd = Math.Round(source.Flow.Spent.Usd, 4, MidpointRounding.AwayFromZero) }),
        };
    }

    /// <summary>
    /// The time <paramref name="question"/> waited for a person: from when it was raised to its first answer, or - still
    /// open - to the run's last record; null where it waited nothing. The one rule the run's total, each question's row
    /// and the longest wait's signal read.
    /// </summary>
    private static TimeSpan? PersonWaitOf(FlowQuestionEntry question, DateTimeOffset? at)
        => (question.Answers.Count > 0 ? question.Answers[0].At : at) is { } ended && ended > question.RaisedAt ? ended - question.RaisedAt : null;

    /// <summary>
    /// The union of every attempt's runs - the time at least one step's work ran - in seconds; a run still open counted to
    /// the run's last record, and a step whose measure keeps no runs by its own start and end; null where none ran.
    /// </summary>
    private static double? ActiveSecondsOf(IReadOnlyList<FlowStepRecord> steps, DateTimeOffset? at)
    {
        var spans = steps
            .Where(step => !step.Group)
            .SelectMany(step => step.Measure?.Attempts?.SelectMany(attempt => attempt.Runs ?? []).ToList() is { Count: > 0 } runs
                ? runs.Select(run => (Began: run.StartedAt, Ended: run.FinishedAt ?? at ?? run.StartedAt))
                : step.StartedAt is { } began && step.FinishedAt is { } ended ? [(Began: began, Ended: ended)] : [])
            .Where(span => span.Ended > span.Began)
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

    /// <summary>What the run spent and did, summed over its steps, with its dollars grouped as the reader asked.</summary>
    private static JsonObject Totals(FlowRunReportSource source)
    {
        var steps = source.Flow.Steps;
        var attempts = steps.SelectMany(step => step.Measure?.Attempts ?? []).ToList();
        var tokens = attempts.Where(attempt => attempt.Tokens is not null).Select(attempt => attempt.Tokens!).ToList();
        var input = tokens.Sum(use => use.InputTokens);
        var cacheRead = tokens.Sum(use => use.CacheReadTokens);

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
            ["dollarsBy"] = DollarsBy(steps, source.By ?? Groupings[0]),
            ["usd"] = Math.Round(steps.Sum(step => (step.Measure?.Totals?.Usd ?? 0) + (step.Measure?.Advice?.Usd ?? 0)), 4, MidpointRounding.AwayFromZero),
            ["uncounted"] = source.Flow.Spent.Uncounted,
            ["agents"] = steps.Where(step => step.Agent is { Length: > 0 }).Select(step => step.Agent!).Distinct(StringComparer.Ordinal).Count(),
            ["stepsRun"] = steps.Count(step => step.State == nameof(FlowInstanceState.Done)),
            ["stepsSkipped"] = steps.Count(step => step.SkippedBecause is not null || step.NotRunBecause is not null),
            ["stepsFailed"] = steps.Count(step => step.State == nameof(FlowInstanceState.Failed)),
            ["retries"] = Math.Max(0, attempts.Count - steps.Count(step => step.Measure?.Attempts is { Count: > 0 })),
            ["fallbacks"] = attempts.Count(attempt => attempt.FallbackFrom is not null),
            ["graph"] = GraphOf(steps),
        };
    }

    /// <summary>
    /// What the run's reasoning cost, grouped by <paramref name="by"/>: each part of each attempt by the model that served
    /// it (<see cref="FlowStepAttempt.Parts"/>; an attempt a build kept no parts for as one part of its own model), and each
    /// step's advice by the advisor's model - every dollar the totals count, once, in one group.
    /// </summary>
    /// <param name="steps">The run's steps.</param>
    /// <param name="by">What the dollars are grouped by, one of <see cref="Groupings"/>.</param>
    private static JsonObject DollarsBy(IReadOnlyList<FlowStepRecord> steps, string by)
    {
        var parts = steps.SelectMany(step =>
            (step.Measure?.Attempts ?? []).SelectMany(attempt => attempt.Parts is { Count: > 0 } kept
                ? kept.Select(part => (Step: step, Model: part.Model, Provider: part.Provider, Usd: part.Usd ?? 0, Tokens: part.Tokens?.AllTokens ?? 0))
                : [(Step: step, attempt.Model, attempt.Provider, Usd: attempt.Usd ?? 0, Tokens: attempt.Tokens?.AllTokens ?? 0)])
            .Concat(step.Measure?.Advice is { } advice ? [(Step: step, advice.Model, Provider: (string?)null, Usd: advice.Usd ?? 0, Tokens: advice.Tokens ?? 0)] : []));

        return new JsonObject
        {
            ["key"] = by,
            ["groups"] = new JsonArray([.. parts
                .Where(part => part.Usd > 0 || part.Tokens > 0)
                .GroupBy(part => by switch
                {
                    "step" => part.Step.Instance,
                    "agent" => part.Step.Agent ?? "-",
                    "provider" => part.Provider ?? "-",
                    "type" => part.Step.Type ?? "-",
                    _ => part.Model ?? "-",
                })
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => (JsonNode)new JsonObject
                {
                    ["id"] = group.Key,
                    ["group"] = group.Key,
                    ["usd"] = Math.Round(group.Sum(part => part.Usd), 4, MidpointRounding.AwayFromZero),
                    ["tokens"] = group.Sum(part => part.Tokens),
                })]),
        };
    }

    /// <summary>
    /// The run's own figures of its codebase graph, as the report's JSON document holds them: what every reason step's
    /// graph gave and saved, summed - the steps' own figures are on their steps. Null where no step was given a graph.
    /// </summary>
    /// <param name="steps">The run's step records.</param>
    private static JsonObject? GraphOf(IReadOnlyList<FlowStepRecord> steps)
    {
        var served = steps.Where(step => step.Graph is not null).ToList();

        return served.Count == 0
            ? null
            : new JsonObject
            {
                ["steps"] = served.Count,
                ["packTokens"] = served.Sum(step => step.Graph?["pack"]?["tokens"]?.GetValue<int>() ?? 0),
                ["calls"] = served.Sum(step => step.Graph?["calls"]?.GetValue<int>() ?? 0),
                ["servedTokens"] = served.Sum(step => step.Graph?["servedTokens"]?.GetValue<int>() ?? 0),
                ["bodies"] = served.Sum(step => step.Graph?["bodies"]?.GetValue<int>() ?? 0),
                ["textHits"] = served.Sum(step => step.Graph?["textHits"]?.GetValue<int>() ?? 0),
                ["edits"] = new JsonObject
                {
                    ["made"] = served.Sum(step => step.Graph?["edits"]?["made"]?.GetValue<int>() ?? 0),
                    ["refused"] = served.Sum(step => step.Graph?["edits"]?["refused"]?.GetValue<int>() ?? 0),
                },
                ["wholeFileReads"] = new JsonObject
                {
                    ["count"] = served.Sum(step => step.Graph?["wholeFileReads"]?["count"]?.GetValue<int>() ?? 0),
                    ["tokens"] = served.Sum(step => step.Graph?["wholeFileReads"]?["tokens"]?.GetValue<int>() ?? 0),
                },
                ["deniedReads"] = served.Sum(step => step.Graph?["deniedReads"]?.GetValue<int>() ?? 0),
                ["escapes"] = new JsonObject
                {
                    ["count"] = served.Sum(step => step.Graph?["escapes"]?["count"]?.GetValue<int>() ?? 0),
                    ["tokens"] = served.Sum(step => step.Graph?["escapes"]?["tokens"]?.GetValue<int>() ?? 0),
                    ["lowConfidence"] = served.Sum(step => step.Graph?["escapes"]?["lowConfidence"]?.GetValue<int>() ?? 0),
                    ["stated"] = served.Sum(step => step.Graph?["escapes"]?["stated"]?.GetValue<int>() ?? 0),
                },
            };
    }

    /// <summary>One row of the steps table, its id the instance's: the instance's own numbers, said shortly, with how it ended or why it did not run.</summary>
    private static JsonObject RowOf(FlowStepRecord step, FlowQuestionsFile? questions)
    {
        var measure = step.Measure;

        return new JsonObject
        {
            ["id"] = step.Instance,
            ["step"] = step.Instance,
            ["type"] = step.Type,
            ["state"] = step.State,
            ["tries"] = measure?.Attempts is { Count: > 1 } count ? count.Count : null,
            ["waited"] = measure?.Waited is { Count: > 0 } waited ? string.Join(", ", waited.Select(WaitSaid)) : null,
            ["ran"] = measure is { Ran: { } ran } ? SecondsOf(ran) : null,
            ["model"] = measure?.Attempts is { Count: > 0 } attempts ? attempts[^1].Model : null,
            ["tokens"] = measure?.Totals?.Tokens?.AllTokens,
            ["usd"] = measure?.Totals?.Usd is not null || measure?.Advice?.Usd is not null ? Math.Round((measure?.Totals?.Usd ?? 0) + (measure?.Advice?.Usd ?? 0), 4, MidpointRounding.AwayFromZero) : null,
            ["exit"] = step.Exit,
            ["error"] = step.Error,
            ["handler"] = step.HandledBy,
            ["skipped"] = step.SkippedBecause ?? step.NotRunBecause ?? (step.Failure == "passedOver" ? "passed over" : null),
            ["graph"] = step.Graph?.DeepClone(),
            ["pausedOn"] = step.Question is { } waiting && questions is not null && questions.Of(waiting) is { State: "open" } ? waiting : null,
        };
    }

    /// <summary>
    /// The chain of <c>dependsOn</c> whose times add to the most a faster run must shorten, step by step with what each
    /// waited for: the longest road through the run's ended instances, each weighed by its own work - its runs and its
    /// waits for room, never a person's wait or a pause. An instance is read as its declaration (<see cref="FlowInstanceId.PathOf"/>)
    /// and linked from the instances in its own scope its declaration names - the latest visit of each that ended before
    /// it began, a group named read as every instance inside that visit - from what its group was linked from where nothing
    /// in its scope comes before it, from the iteration before its own in a <c>repeat</c>, from the instance whose ending
    /// raised it, and - a visit after the first - from what ended last in its scope before it, which sent it back. The
    /// road is walked once in the order of those links, each link read once (a group's instances once for every link to
    /// it), so the walk is linear in the run's instances and links. Ties read one way, by instance id. Empty where the
    /// definition cannot be read - the report's notes say so - or nothing ended.
    /// </summary>
    private static JsonArray CriticalPathOf(FlowRunReportSource source, IReadOnlyList<FlowStepRecord> steps, DateTimeOffset? at)
    {
        var path = new JsonArray();

        if (source.Definition is not { } definition)
        {
            return path;
        }

        var records = new Dictionary<string, FlowStepRecord>(StringComparer.Ordinal);

        foreach (var step in steps.Where(step => FlowInstanceId.IsWellFormed(step.Instance)))
        {
            records[step.Instance] = step;
        }

        var leaves = records.Values.Where(step => !step.Group && step.StartedAt is not null && step.FinishedAt is not null).ToDictionary(step => step.Instance, StringComparer.Ordinal);

        // Each scope's ended instances, and each step's in its scope (a newline is in no instance id), in the order they
        // ended; and each group's ended leaves - every instance indexed once.
        var ended = new Dictionary<string, List<FlowStepRecord>>(StringComparer.Ordinal);
        var endedByName = new Dictionary<string, List<FlowStepRecord>>(StringComparer.Ordinal);

        foreach (var step in records.Values.Where(step => step.FinishedAt is not null))
        {
            var (scope, segment) = Split(step.Instance);

            foreach (var (index, key) in new[] { (ended, scope), (endedByName, $"{scope}\n{FlowInstanceId.PathOf(segment)}") })
            {
                if (!index.TryGetValue(key, out var list))
                {
                    index[key] = list = [];
                }

                list.Add(step);
            }
        }

        foreach (var list in ended.Values.Concat(endedByName.Values))
        {
            list.Sort((one, other) => one.FinishedAt!.Value.CompareTo(other.FinishedAt!.Value) is var by and not 0 ? by : string.CompareOrdinal(one.Instance, other.Instance));
        }

        var inside = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var leaf in leaves.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var group in GroupsAround(leaf))
            {
                if (!inside.TryGetValue(group, out var held))
                {
                    inside[group] = held = [];
                }

                held.Add(leaf);
            }
        }

        // The links: each leaf from what came before it, a group's leaves read through one node of its own, made once.
        var before = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var groupNodes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var leaf in leaves.Values)
        {
            before[leaf.Instance] = [.. PredecessorsOf(leaf.Instance, leaf.StartedAt!.Value).OfType<string>().Distinct(StringComparer.Ordinal)];
        }

        // The longest road ending at each node, walked in the order of the links (Kahn's): a node is weighed once all it
        // comes after are; a cycle a damaged record could leave is never walked.
        var after = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var waiting = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (node, predecessors) in before)
        {
            waiting[node] = predecessors.Count;

            foreach (var predecessor in predecessors)
            {
                if (!after.TryGetValue(predecessor, out var next))
                {
                    after[predecessor] = next = [];
                }

                next.Add(node);
            }
        }

        var ready = new SortedSet<string>(before.Keys.Where(node => waiting[node] == 0), StringComparer.Ordinal);
        var best = new Dictionary<string, (double Weight, string? From)>(StringComparer.Ordinal);

        while (ready.Count > 0)
        {
            var node = ready.Min!;
            ready.Remove(node);

            var from = before[node]
                .OrderByDescending(id => best[id].Weight)
                .ThenBy(id => id, StringComparer.Ordinal)
                .Select(id => (string?)id)
                .FirstOrDefault();

            best[node] = ((from is null ? 0 : best[from].Weight) + (leaves.ContainsKey(node) ? WeightOf(leaves[node], at) : 0), from);

            foreach (var next in after.GetValueOrDefault(node) ?? [])
            {
                if (--waiting[next] == 0)
                {
                    ready.Add(next);
                }
            }
        }

        var reached = best.Where(pair => leaves.ContainsKey(pair.Key)).ToList();

        if (reached.Count == 0)
        {
            return path;
        }

        var chain = new List<string>();

        for (var node = reached.OrderByDescending(pair => pair.Value.Weight).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key; node is not null; node = best[node].From)
        {
            if (leaves.ContainsKey(node))
            {
                chain.Add(node);
            }
        }

        chain.Reverse();

        foreach (var instance in chain)
        {
            var measure = leaves[instance].Measure;

            path.Add(new JsonObject
            {
                ["id"] = instance,
                ["step"] = instance,
                ["seconds"] = Seconds(WeightOf(leaves[instance], at)),
                ["waited"] = measure?.Waited is { Count: > 0 } waits
                    ? string.Join(", ", waits.OrderByDescending(wait => wait.Seconds).ThenBy(wait => wait.Cause, StringComparer.Ordinal).Select(WaitSaid))
                    : null,
            });
        }

        return path;

        // The instances - and the group nodes - that come before one, as its record, its declaration and its scope say;
        // null for one that is no node of the road, an instance that never started.
        IEnumerable<string?> PredecessorsOf(string instance, DateTimeOffset began)
        {
            var (scope, _) = Split(instance);

            // The instance whose ending raised it - a handler's source, the step whose failure a recovery took.
            if (records.TryGetValue(instance, out var record) && record.RaisedBy is { } raiser && records.TryGetValue(raiser, out var raised) && raised.FinishedAt <= began)
            {
                yield return NodeOf(raiser);
            }

            // A visit after the first was sent back to: what ended last in its own scope before it began sent it.
            if (records.ContainsKey(instance) && FlowInstanceId.VisitOf(instance) > 1 && LastEndedIn(ended.GetValueOrDefault(scope), began, except: instance) is { } sender)
            {
                yield return NodeOf(sender);
            }

            var declaration = DeclarationOf(definition, FlowInstanceId.PathOf(instance));
            var found = false;

            foreach (var dependency in declaration?.DependsOn ?? [])
            {
                if (LastEndedIn(endedByName.GetValueOrDefault($"{scope}\n{dependency}"), began) is { } named)
                {
                    found = true;
                    yield return NodeOf(named);
                }
            }

            if (found || scope.Length == 0)
            {
                yield break;
            }

            // First in its scope: what came before its group's instance - the iteration before its own in a repeat, or
            // what the group was linked from.
            if (FlowInstanceId.IterationOf(scope) is > 1 and var iteration)
            {
                yield return GroupNode(FlowInstanceId.WithIteration(scope, iteration - 1));
                yield break;
            }

            foreach (var one in PredecessorsOf(scope, began))
            {
                yield return one;
            }
        }

        // An ended instance as a node of the road: a leaf as itself, a group as the node its leaves all come before; null
        // for one that never started, which no road runs through.
        string? NodeOf(string instance)
            => records[instance].Group ? GroupNode(instance) : leaves.ContainsKey(instance) ? instance : null;

        // The node every ended leaf inside <group> comes before, made once.
        string GroupNode(string group)
        {
            // A space is in no instance id, so a group's node is never taken for an instance.
            var node = $"{group} *";

            if (groupNodes.Add(node))
            {
                before[node] = inside.GetValueOrDefault(group) ?? [];
            }

            return node;
        }

        // The instance of <list> - in the order they ended - other than <except> that ended last at or before <began>:
        // a step's latest visit, or what sent a visit back.
        static string? LastEndedIn(List<FlowStepRecord>? list, DateTimeOffset began, string? except = null)
        {
            if (list is null)
            {
                return null;
            }

            // The first that ended after <began>, by halves; every one before it ended at or before.
            var (low, high) = (0, list.Count);

            while (low < high)
            {
                var middle = (low + high) / 2;
                (low, high) = list[middle].FinishedAt <= began ? (middle + 1, high) : (low, middle);
            }

            return low > 0 && list[low - 1].Instance == except ? (low > 1 ? list[low - 2].Instance : null)
                : low > 0 ? list[low - 1].Instance
                : null;
        }
    }

    /// <summary>
    /// Every group instance around the instance <paramref name="instance"/>, from the outermost: a block's, a loop's, and
    /// each loop's item or iteration - <c>rounds</c>, <c>rounds#2</c>, <c>rounds#2.items</c>, <c>rounds#2.items[0]</c>
    /// around <c>rounds#2.items[0].fix</c>.
    /// </summary>
    private static IEnumerable<string> GroupsAround(string instance)
    {
        var segments = instance.Split('.');
        var prefix = string.Empty;

        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            var joined = prefix.Length == 0 ? string.Empty : $"{prefix}.";
            var mark = segment.IndexOfAny(['[', '#']);

            if (mark >= 0)
            {
                yield return $"{joined}{segment[..mark]}";
            }

            yield return prefix = $"{joined}{segment}";
        }
    }

    /// <summary>An instance id's scope - the group instance around it, empty at the top - and its own last segment.</summary>
    private static (string Scope, string Segment) Split(string instance)
        => instance.LastIndexOf('.') is var at and >= 0 ? (instance[..at], instance[(at + 1)..]) : (string.Empty, instance);

    /// <summary>The declaration at <paramref name="path"/> (group names joined by '.'), or null where the definition holds none.</summary>
    private static WorkflowStep? DeclarationOf(WorkflowDefinition definition, string path)
    {
        IReadOnlyList<WorkflowStep> steps = definition.Steps;
        WorkflowStep? found = null;

        foreach (var name in path.Split('.'))
        {
            found = steps.FirstOrDefault(step => step.Name == name);

            if (found is null)
            {
                return null;
            }

            steps = found.Steps ?? [];
        }

        return found;
    }

    /// <summary>
    /// The time an ended instance's own work took, as the critical path weighs it: its runs and its waits for room - the
    /// run's and the machine's pace - never a person's wait or a pause; its wall time where its measure keeps no runs.
    /// </summary>
    private static double WeightOf(FlowStepRecord step, DateTimeOffset? at)
        => step.Measure is { Attempts: { Count: > 0 } attempts } measure && attempts.Any(attempt => attempt.Runs is { Count: > 0 })
            ? attempts.Sum(attempt => (attempt.Runs ?? []).Sum(run => ((run.FinishedAt ?? at ?? run.StartedAt) - run.StartedAt).TotalSeconds))
                + (measure.Waited ?? []).Where(wait => FlowWaits.ForRoom(wait.Cause)).Sum(wait => wait.Seconds)
            : (step.FinishedAt!.Value - step.StartedAt!.Value).TotalSeconds;

    /// <summary>
    /// Every attempt that failed, each with its own id (<c>&lt;instance&gt;@&lt;attempt&gt;</c>): its code, why it ended, its
    /// error, the exception it threw where one did - its type, message, stack and causes as the attempt keeps them - the
    /// first error its digests read as it ended, what handled the step, and the question its ending raised - each the
    /// attempt's own, as its entry keeps it, never the step's last attempt's or its newest question.
    /// </summary>
    private static IEnumerable<JsonObject> ErrorsOf(FlowRunReportSource source)
        => source.Flow.Steps
            .SelectMany(step => (step.Measure?.Attempts ?? []).Where(attempt => attempt.Outcome == nameof(FlowInstanceState.Failed))
                .Select(attempt => (step, attempt)))
            .Select(failed => new JsonObject
            {
                ["id"] = $"{failed.step.Instance}@{failed.attempt.N.ToString(CultureInfo.InvariantCulture)}",
                ["step"] = failed.step.Instance,
                ["attempt"] = failed.attempt.N,
                ["exit"] = failed.attempt.Exit,
                ["cause"] = failed.attempt.Cause,
                ["error"] = failed.attempt.Error ?? failed.step.Error,
                ["exception"] = failed.attempt.Exception is { } thrown ? System.Text.Json.JsonSerializer.SerializeToNode(thrown, FileSystem.JsonStateFile.Options) : null,
                ["firstError"] = failed.attempt.FirstError?.DeepClone(),
                ["handledBy"] = failed.step.HandledBy,
                ["question"] = failed.attempt.Question,
            });

    /// <summary>Every question a person was asked: when, how long it waited (<see cref="PersonWaitOf"/>), the recommendation it was shown, and its first answer.</summary>
    private static IEnumerable<JsonObject> PeopleOf(FlowRunReportSource source, DateTimeOffset? at)
        => (source.Questions?.Questions ?? [])
            .OrderBy(question => question.Id, StringComparer.Ordinal)
            .Select(question =>
            {
                var answer = question.Answers.Count > 0 ? question.Answers[0] : null;

                return new JsonObject
                {
                    ["id"] = question.Id,
                    ["question"] = question.Id,
                    ["kind"] = question.Kind,
                    ["askedAt"] = FlowTimeConverter.Written(question.RaisedAt),
                    ["waitedSeconds"] = PersonWaitOf(question, at) is { } waited ? Seconds(waited.TotalSeconds) : null,
                    ["answered"] = answer?.Answer,
                    ["by"] = answer is { } saidBy ? QuestionsText.Who(saidBy.By) : null,
                    ["recommendation"] = question.Recommendation is { } recommended ? $"{recommended.State}: {recommended.Why}" : null,
                };
            });

    /// <summary>
    /// What the run's reviews converged to: its loops' leftovers, the classes its work closed, each item's counts, the
    /// classes its reviews reopened, its hotspots and the issues it carried to a later round - each row with an id of its
    /// own, one across the section: its list, the item, step or agent that said it, and what it is of
    /// (<c>hotspots:&lt;item&gt;:&lt;class&gt;:&lt;path&gt;</c>), an item's class among its counts the item's and its class
    /// (<c>items:&lt;item&gt;:&lt;class&gt;</c>).
    /// </summary>
    private static JsonObject ConvergenceOf(FlowRunReportSource source)
    {
        var convergence = new JsonObject();

        var leftovers = source.Flow.Steps.Where(step => step.Leftover is not null).ToList();

        if (leftovers.Count > 0)
        {
            convergence["loopsLeft"] = new JsonArray([.. leftovers.Select(step => new JsonObject
            {
                ["id"] = $"loopsLeft:{step.Instance}",
                ["step"] = step.Instance,
                ["left"] = FlowValue.Of(step.Leftover!.DeepClone()).AsTemplateText(),
            })]);
        }

        // What the run's items closed, so a repository can see whether its rounds converge: each class's claim as its
        // item's newest class-closure check judged the record, gathered by the one owner the run's words read too.
        var closing = ClosingOf(source);
        var closed = FlowClosureRecords.RecordsOf(closing)
            .SelectMany(record => record.Record.Select(entry => (record.Item, Class: TextOf(entry["class"]), Claim: TextOf(entry["claim"]))))
            .ToList();

        if (closed.Count > 0)
        {
            convergence["classesClosed"] = new JsonArray([.. closed.Select(@class => new JsonObject
            {
                ["id"] = $"classesClosed:{@class.Item}:{@class.Class}",
                ["item"] = @class.Item,
                ["class"] = @class.Class,
                ["claim"] = @class.Claim,
            })]);
        }

        // What each item's work did to its classes, by the same owner and in the same words as the run's {run.counts}.
        var items = FlowClosureRecords.ItemsOf(closing);

        if (items.Count > 0)
        {
            convergence["items"] = new JsonArray([.. items.Select(item =>
            {
                var row = item.ToJson();
                row.Insert(0, "id", $"items:{item.Item}");

                foreach (var @class in row["classes"]!.AsArray().OfType<JsonObject>())
                {
                    @class.Insert(0, "id", $"items:{item.Item}:{TextOf(@class["class"])}");
                }

                return row;
            })]);
        }

        // The classes the reviews reopened, by each item's ledger - the one count of them, read by the signals too - in
        // the order of their agents and classes, whatever order the ledgers' files are listed in.
        var reopened = (source.Ledgers ?? [])
            .SelectMany(ledger => ledger.Ledger.Classes.Select(entry => (ledger.Agent, entry.Class, Reopens: entry.Reopens.ToString(CultureInfo.InvariantCulture))))
            .OrderBy(entry => entry.Agent, StringComparer.Ordinal)
            .ThenBy(entry => entry.Class, StringComparer.Ordinal)
            .ToList();

        if (reopened.Count > 0)
        {
            convergence["classesReopened"] = new JsonArray([.. reopened.Select(@class => new JsonObject
            {
                ["id"] = $"classesReopened:{@class.Agent}:{@class.Class}",
                ["agent"] = @class.Agent,
                ["class"] = @class.Class,
                ["reopens"] = @class.Reopens,
            })]);
        }

        var hotspots = FlowClosureRecords.HotspotsOf(closing);

        if (hotspots.Count > 0)
        {
            convergence["hotspots"] = new JsonArray([.. hotspots.Select(hotspot => new JsonObject
            {
                ["id"] = $"hotspots:{hotspot.Item}:{hotspot.Hotspot.Class}:{hotspot.Hotspot.Path}",
                ["item"] = hotspot.Item,
                ["class"] = hotspot.Hotspot.Class,
                ["path"] = hotspot.Hotspot.Path,
                ["touches"] = hotspot.Hotspot.Touches.ToString(CultureInfo.InvariantCulture),
            })]);
        }

        // Each issue the run carried to a later round, as its list of them says what became of it: fixed there by the work
        // item that settled it, or left owed.
        if (source.NextRound is { Issues.Count: > 0 } next)
        {
            convergence["nextRound"] = new JsonArray([.. next.Issues.Select(issue => (JsonNode?)new JsonObject
            {
                ["id"] = $"nextRound:{issue.Id}",
                ["issue"] = issue.Id,
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
            JsonValue value when value.TryGetValue<double>(out var fractional) => fractional.ToString("0.0###", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

    /// <summary>Each signal with its id: its kind and its place among that kind's signals, from 1.</summary>
    private static IEnumerable<(string Id, string Kind, IReadOnlyList<string> Steps, string Said)> Numbered(IEnumerable<FlowReportSignal> signals)
    {
        var counted = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var signal in signals)
        {
            var n = counted[signal.Kind] = counted.GetValueOrDefault(signal.Kind) + 1;
            yield return ($"{signal.Kind}#{n.ToString(CultureInfo.InvariantCulture)}", signal.Kind, signal.Steps, signal.Said);
        }
    }

    /// <summary>The deterministic flags a reader starts from, each naming its steps and its numbers.</summary>
    private static IEnumerable<FlowReportSignal> SignalsOf(FlowRunReportSource source, IReadOnlyList<FlowStepRecord> steps, FlowReportThresholds thresholds, DateTimeOffset? at)
    {
        // A step's own time is its wall time where its records hold both ends, else what its measure sums - the same
        // measure every form of the report reads.
        var whole = steps.Where(step => !step.Group).Select(step => (Step: step, Whole: step.StartedAt is { } began && step.FinishedAt is { } ended && ended > began
            ? (ended - began).TotalSeconds
            : (step.Measure?.Ran ?? 0) + (step.Measure?.Waited?.Sum(wait => wait.Seconds) ?? 0) + (step.Measure?.Paused ?? 0))).ToList();

        foreach (var slow in whole.OrderByDescending(step => step.Whole).ThenBy(step => step.Step.Instance, StringComparer.Ordinal).Take(thresholds.Top).Where(step => step.Whole > 0))
        {
            yield return new("slowestStep", [slow.Step.Instance], $"{slow.Step.Instance} took {SecondsOf(slow.Whole)} s of run time");
        }

        foreach (var dear in steps.OrderByDescending(CostOf).ThenBy(step => step.Instance, StringComparer.Ordinal).Take(thresholds.Top).Where(step => CostOf(step) > 0))
        {
            yield return new("mostExpensiveStep", [dear.Instance], $"{dear.Instance} cost {UsdOf(CostOf(dear))}");
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
            .Select(question => (question, Waited: PersonWaitOf(question, at)))
            .Where(waited => waited.Waited is not null)
            .OrderByDescending(waited => waited.Waited!.Value)
            .ThenBy(waited => waited.question.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (longestPerson.question is { } person)
        {
            yield return new("longestWaitForAPerson", [person.Id], $"{person.Id} waited {SecondsOf(longestPerson.Waited!.Value.TotalSeconds)} s for its answer");
        }

        foreach (var queued in whole.Where(step => step.Whole > 0 && (step.Step.Measure?.Waited ?? []).Any(wait => FlowWaits.ForRoom(wait.Cause) && wait.Seconds / step.Whole * 100 > thresholds.QueueSharePercent)).OrderBy(step => step.Step.Instance, StringComparer.Ordinal))
        {
            var waits = string.Join(", ", queued.Step.Measure!.Waited!.Where(wait => FlowWaits.ForRoom(wait.Cause)).Select(WaitSaid));
            yield return new("queueWait", [queued.Step.Instance], $"{queued.Step.Instance} waited {waits}, over {thresholds.QueueSharePercent.ToString(CultureInfo.InvariantCulture)}% of its {SecondsOf(queued.Whole)} s");
        }

        foreach (var cold in steps.SelectMany(step => (step.Measure?.Attempts ?? []).Select(attempt => (step, attempt)))
                     .Where(attempt => attempt.attempt.Tokens is { } tokens && tokens.InputTokens + tokens.CacheReadTokens > thresholds.CacheInputTokens && ShareOf(tokens.CacheReadTokens, tokens.InputTokens + tokens.CacheReadTokens) < thresholds.CacheSharePercent)
                     .OrderBy(attempt => attempt.step.Instance, StringComparer.Ordinal)
                     .ThenBy(attempt => attempt.attempt.N))
        {
            var tokens = cold.attempt.Tokens!;
            yield return new("cacheShare", [cold.step.Instance], $"{cold.step.Instance} attempt {cold.attempt.N.ToString(CultureInfo.InvariantCulture)} read {ShareOf(tokens.CacheReadTokens, tokens.InputTokens + tokens.CacheReadTokens).ToString("0.0", CultureInfo.InvariantCulture)}% of its input from the cache ({tokens.CacheReadTokens.ToString(CultureInfo.InvariantCulture)} of {(tokens.InputTokens + tokens.CacheReadTokens).ToString(CultureInfo.InvariantCulture)} tokens)");
        }

        // A step that read past the depth of one graph answer: its query was too wide, the pages it walked saying so.
        // The step's deepest is the deepest of its attempts, each attempt's own measure naming its own.
        foreach (var wide in steps
                     .Select(step => (step, Deep: (step.Measure?.Attempts ?? []).Select(attempt => attempt.GraphPages?.Deepest).OfType<FlowStepGraphAsk>().OrderByDescending(ask => ask.Page).ThenBy(ask => ask.Op, StringComparer.Ordinal).FirstOrDefault()))
                     .Where(step => step.Deep?.Page > thresholds.GraphPageDepth)
                     .OrderBy(step => step.step.Instance, StringComparer.Ordinal))
        {
            var asked = (wide.step.Measure?.Attempts ?? []).Sum(attempt => attempt.GraphPages?.Asked ?? 0);
            yield return new(
                "wideGraphQuery",
                [wide.step.Instance],
                $"{wide.step.Instance} read to page {wide.Deep!.Page.ToString(CultureInfo.InvariantCulture)} of one graph answer ('{wide.Deep.Op}'), past {thresholds.GraphPageDepth.ToString(CultureInfo.InvariantCulture)}, asking {ReportText.Counted(wide.Deep.Asked, "page")} of it and {ReportText.Counted(asked, "page")} in all past the first");
        }

        foreach (var unread in steps.SelectMany(step => (step.Measure?.Attempts ?? []).SelectMany(attempt => (attempt.Legs ?? []).Where(leg => leg.Unparsed > 0).Select(leg => (step, attempt, leg))))
                     .OrderBy(unread => unread.step.Instance, StringComparer.Ordinal)
                     .ThenBy(unread => unread.attempt.N)
                     .ThenBy(unread => unread.leg.Leg, StringComparer.Ordinal))
        {
            yield return new("unparsedOutput", [unread.step.Instance], $"{unread.step.Instance} attempt {unread.attempt.N.ToString(CultureInfo.InvariantCulture)}: leg '{unread.leg.Leg}' printed {unread.leg.Unparsed!.Value.ToString(CultureInfo.InvariantCulture)} line(s) that look like errors and no parser read");
        }

        if ((source.Questions?.Questions ?? []).Any(question => question.Kind == "loopBound"))
        {
            yield return new("reviewAtItsBound", [], "a loop reached its bound and asked");
        }

        foreach (var twice in (source.Ledgers ?? [])
                     .SelectMany(ledger => ledger.Ledger.Classes.Where(@class => FlowClosureReopens.AsksItsHomeAudit(@class.Reopens)).Select(@class => (ledger.Agent, @class)))
                     .OrderBy(twice => twice.Agent, StringComparer.Ordinal)
                     .ThenBy(twice => twice.@class.Class, StringComparer.Ordinal))
        {
            yield return new("classReopenedTwice", [], $"'{twice.@class.Class}' of agent '{twice.Agent}' was reopened {twice.@class.Reopens.ToString(CultureInfo.InvariantCulture)} times");
        }

        foreach (var hotspot in steps.Where(step => step.Type == FlowClosureRecords.CheckType && step.Output?["hotspots"] is JsonArray { Count: > 0 }))
        {
            yield return new("hotspot", [hotspot.Instance], $"{hotspot.Instance} named {hotspot.Output!["hotspots"]!.AsArray().Count.ToString(CultureInfo.InvariantCulture)} hotspot(s)");
        }

        if (source.Flow.Spent.Uncounted is { } uncounted)
        {
            yield return new("uncountedSpend", [], $"the run's dollars are not known past {UsdOf(source.Flow.Spent.Usd)}: {uncounted}");
        }

        static double CostOf(FlowStepRecord step) => (step.Measure?.Totals?.Usd ?? 0) + (step.Measure?.Advice?.Usd ?? 0);
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

    /// <summary>
    /// One wait as every row says it: its seconds on its cause and - where the cause is the machine's queue - the class
    /// of the queue and each tenant it waited behind, so a reader can tell its own run's units from another
    /// repository's.
    /// </summary>
    private static string WaitSaid(FlowStepWait wait)
        => $"{SecondsOf(wait.Seconds)} s on {wait.Cause}"
            + (wait.Ahead is { Count: > 0 } ahead
                ? $" ({string.Join(", ", ahead.Select(standing => $"{QueueReports.NameOf(standing.Class)}, behind {string.Join(", ", standing.Tenants.Select(tenant => $"'{tenant}'"))}"))})"
                : string.Empty);

    /// <summary>Dollars said with four decimals and the currency, the one form every cost in a report's words takes.</summary>
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
