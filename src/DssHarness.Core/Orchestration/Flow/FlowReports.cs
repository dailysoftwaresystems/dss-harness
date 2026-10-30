using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>The exit codes of a session of a run, this command family's own.</summary>
public static class FlowExit
{
    /// <summary>
    /// A question waits: a step's, or the budget's at its reserve. The run's family's own code, which a wrapper that
    /// retries 21 would otherwise loop on an unanswered question.
    /// </summary>
    public const int Paused = 1;
}

/// <summary>One question a session answered: the answer, who gave it, and what the engine did with it.</summary>
/// <param name="Question">The question's id.</param>
/// <param name="Answer">The answer given.</param>
/// <param name="By">Who gave it: <c>person</c>, <c>configuration</c> or <c>input</c>.</param>
/// <param name="Through">How a person gave it - <c>answer-orchestrator</c> or <c>front-end</c>; where the configuration says so, for the configuration's; null for every other.</param>
/// <param name="Did">What the engine did with it; null where it has not applied it yet.</param>
public sealed record FlowAnswered(string Question, string Answer, string? By, string? Through, string? Did);

/// <summary>One handoff a safe pause left: the step that handed off, who wrote it, and its file.</summary>
/// <param name="Instance">The step instance that handed off.</param>
/// <param name="By">Who wrote it: <c>agent</c> or <c>engine</c>.</param>
/// <param name="File">The handoff's file, in the step's own directory.</param>
public sealed record FlowHandoffNamed(string Instance, string By, string File);

/// <summary>One record a run's command wrote or took, by what it is and where it lands.</summary>
/// <param name="What">What it is, as the document's <c>records</c> member names it: <c>state</c>, <c>status</c>, <c>report</c>, <c>stop</c> and the rest.</param>
/// <param name="Path">Where it is.</param>
public sealed record FlowRecordPath(string What, string Path);

/// <summary>
/// What one command of a run ended with, as the run's front end reads it and <c>--json</c> answers it - a session's
/// end, a pause or a stop asked of an engine, an answer handed to one: its status, its questions, what it answered, what
/// it left open for a person, what it spent, and every record it wrote or took by its path.
/// </summary>
/// <param name="Status">How the run stands: <c>Done</c>, <c>Failed</c>, <c>Paused</c>, <c>Stopped</c> or <c>Running</c>; null where the command did not reach a run's state.</param>
/// <param name="Run">The run's name; null where no run was reached.</param>
/// <param name="Summary">The command's end in words.</param>
/// <param name="Questions">The questions the run leaves open, in the order raised, each with its answers and its recommendation.</param>
/// <param name="Answered">The questions this command answered, each with who answered and what the engine did.</param>
/// <param name="PassedOver">The instances whose failures the run passed over, in the order they ended.</param>
/// <param name="Handoffs">The handoffs a safe pause wrote, each by its step, who wrote it and its file.</param>
/// <param name="LeftForAPerson">What is left for a person, tied to no step's name: each pull request the run opened or found open, to merge, the paths its folds changed that git ignores, never committed, each question still open with its recommendation, and what each of the run's loops left.</param>
/// <param name="Spent">What the run has spent of its budget: usd, tokens and minutes; null where the command did not reach a run's state.</param>
/// <param name="ExitCode">The command's exit code.</param>
/// <param name="Records">Every record the command wrote or took, by what it is and where it is.</param>
public sealed record FlowRunResult(
    string? Status,
    string? Run,
    string Summary,
    IReadOnlyList<FlowQuestionEntry> Questions,
    IReadOnlyList<FlowAnswered> Answered,
    IReadOnlyList<string> PassedOver,
    IReadOnlyList<FlowHandoffNamed> Handoffs,
    IReadOnlyList<string> LeftForAPerson,
    FlowBudgetRecord? Spent,
    int ExitCode,
    IReadOnlyList<FlowRecordPath> Records)
{
    /// <summary>A command of a run that reached no run's state: its code and its line, and nothing else.</summary>
    /// <param name="exitCode">The code it exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static FlowRunResult Empty(int exitCode, string summary) => new(null, null, summary, [], [], [], [], [], null, exitCode, []);

    /// <summary>The lines that name each record, as the command's text names them.</summary>
    public IEnumerable<string> RecordLines => Records.Select(record => $"{record.What}: {record.Path}");
}

/// <summary>
/// The report of a session, <c>report.md</c> and <c>report.json</c>, written by the engine at the end of every
/// session - done, failed, paused or stopped, and once more where it stayed to record after a pause - from the run's
/// records: each step with its state, visit and attempts, the reason of each step that was skipped, not run or passed
/// over, each failure a handler took, what each loop left, each handoff with who wrote it and its file, every question
/// with its problem, the recommendation it was shown with, and each answer it was given - who gave it, what the engine
/// did, and whether a later one replaced it - what the run has spent of its budget, and what is left for a person. No
/// step type writes it, so no failed step can stand in front of it. The <c>--json</c> document of each command of a run
/// is shaped here too (<see cref="Document"/>), so the report, a session's answer and a stopped command's answer hold
/// one shape.
/// </summary>
public static class FlowReports
{
    /// <summary>Writes the report of a session, markdown and one JSON document beside it.</summary>
    /// <param name="fileSystem">Writes the two files, atomically.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="flow">The run's state in one file, as the session left it.</param>
    /// <param name="questions">The run's questions and answers, as the session left them.</param>
    /// <param name="summary">The session in words.</param>
    /// <param name="exitCode">The session's exit code.</param>
    /// <param name="endedAt">When the session ended.</param>
    /// <param name="updates">The run's plan updates, as the session left them.</param>
    /// <returns>What is left for a person, the report's last section, one line each.</returns>
    public static IReadOnlyList<string> Write(
        IFileSystem fileSystem,
        OrchestratorLayout layout,
        FlowFile flow,
        FlowQuestionsFile? questions,
        string summary,
        int exitCode,
        DateTimeOffset endedAt,
        FlowUpdatesFile? updates = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        var left = LeftForAPerson(layout, flow, questions, updates);
        var handoffs = HandoffsOf(layout, flow);

        var json = new JsonObject
        {
            ["schema"] = 1,
            ["run"] = flow.Run,
            ["definition"] = flow.Definition,
            ["status"] = flow.Status,
            ["summary"] = summary,
            ["exitCode"] = exitCode,
            ["endedAt"] = FlowTimeConverter.Written(endedAt),
            ["spent"] = SpentOf(flow.Spent),
            ["steps"] = new JsonArray([.. flow.Steps.Select(StepOf)]),
            ["handoffs"] = new JsonArray([.. handoffs.Select(HandoffOf)]),
            ["questions"] = questions is null ? [] : new JsonArray([.. questions.Questions.Select(QuestionOf)]),
            ["updates"] = updates is null ? [] : new JsonArray([.. updates.Updates.Select(UpdateOf)]),
            ["leftForAPerson"] = new JsonArray([.. left.Select(line => JsonValue.Create(line))]),
        };

        JsonStateFile.Write(fileSystem, layout.ReportJsonFile, json);
        fileSystem.WriteAllTextAtomic(layout.ReportFile, MarkdownOf(flow, questions, handoffs, summary, exitCode, left, updates) + "\n");

        return left;
    }

    /// <summary>
    /// The <c>--json</c> document of a command of a run - a session, a pause, a stop, an answer handed over, and the
    /// same command stopped before it reached a run (<paramref name="result"/> empty): its code and its line, then the
    /// run's own members, every one written whether or not it holds anything, so a script reads one shape whatever happened.
    /// </summary>
    /// <param name="result">What the command ended with.</param>
    public static string Document(FlowRunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var records = new JsonObject();

        foreach (var record in result.Records)
        {
            records[record.What] = record.Path;
        }

        return ReportJson.Stopped(
            result.ExitCode,
            result.Summary,
            new JsonObject
            {
                ["run"] = result.Run,
                ["status"] = result.Status,
                ["questions"] = new JsonArray([.. result.Questions.Select(QuestionOf)]),
                ["answered"] = new JsonArray([.. result.Answered.Select(answered => new JsonObject
                {
                    ["question"] = answered.Question,
                    ["answer"] = answered.Answer,
                    ["by"] = answered.By,
                    ["through"] = answered.Through,
                    ["did"] = answered.Did,
                })]),
                ["passedOver"] = new JsonArray([.. result.PassedOver.Select(id => JsonValue.Create(id))]),
                ["handoffs"] = new JsonArray([.. result.Handoffs.Select(HandoffOf)]),
                ["leftForAPerson"] = new JsonArray([.. result.LeftForAPerson.Select(line => JsonValue.Create(line))]),
                ["spent"] = result.Spent is { } spent ? SpentOf(spent) : null,
                ["records"] = records,
            });
    }

    /// <summary>
    /// The <c>--json</c> document of the two ask commands - <c>ask-orchestrator-status</c> and <c>ask-orchestrator</c> -
    /// with the facts and the answer apart, so a caller that wants no model's words reads the facts alone: the facts as
    /// text, the answer where a model worded them and why not where none did, what the answer cost, and where the call's
    /// own record is.
    /// </summary>
    /// <param name="facts">The engine's facts, as text.</param>
    /// <param name="answer">The reporter's answer; null where none worded them.</param>
    /// <param name="whyNot">Why no model worded them; null where one did.</param>
    /// <param name="model">The model that served the reporter, where one did.</param>
    /// <param name="usd">What the answer cost; null where nothing is known.</param>
    /// <param name="tokens">The tokens its clients reported; null where nothing is known.</param>
    /// <param name="record">Where the call's own record is, under the run's <c>asks/</c>.</param>
    public static JsonObject AskDocument(string facts, string? answer, string? whyNot, string? model, double? usd, long? tokens, string record)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(record);

        return new JsonObject
        {
            ["facts"] = facts,
            ["worded"] = answer is not null,
            ["answer"] = answer,
            ["whyNot"] = whyNot,
            ["cost"] = model is null && usd is null && tokens is null
                ? null
                : new JsonObject
                {
                    ["model"] = model,
                    ["usd"] = usd,
                    ["tokens"] = tokens,
                },
            ["record"] = record,
        };
    }

    /// <summary>
    /// The ask document a stopped-early answer holds: the same members, holding nothing but the code it exits with and
    /// the line it ends on - the shape every answer of the two commands keeps.
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    public static string AskStopped(int exitCode, string summary)
        => ReportJson.Stopped(exitCode, summary, AskDocument(string.Empty, null, null, null, null, null, string.Empty));

    /// <summary>Every handoff a run's steps hold, each by its step, who wrote it and its file in the step's own directory.</summary>
    /// <param name="layout">The run's layout.</param>
    /// <param name="flow">The run's state in one file.</param>
    public static IReadOnlyList<FlowHandoffNamed> HandoffsOf(OrchestratorLayout layout, FlowFile flow)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(flow);

        return
        [
            .. flow.Steps
                .Where(step => step.Handoff is not null)
                .Select(step => new FlowHandoffNamed(
                    step.Instance,
                    step.Handoff!.By,
                    layout.InstanceHandoffFile(step.Instance, step.Agent))),
        ];
    }

    /// <summary>
    /// What is left for a person, tied to no step's name: each pull request a <c>pull-request</c> step opened or found
    /// open, by its address, which the run did not merge and will not; the paths its folds changed that git ignores, as
    /// its <c>commit</c> steps answered them (<c>ignored</c>) and none of them took since, which the run never commits;
    /// each question still open, with its recommendation and the way to answer it; each plan update that waits or was
    /// refused, so what became of a change a person asked for is never lost; and what each of the run's loops left.
    /// </summary>
    /// <param name="layout">The run's layout.</param>
    /// <param name="flow">The run's state in one file.</param>
    /// <param name="questions">The run's questions and answers.</param>
    /// <param name="updates">The run's plan updates.</param>
    public static IReadOnlyList<string> LeftForAPerson(OrchestratorLayout layout, FlowFile flow, FlowQuestionsFile? questions, FlowUpdatesFile? updates = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(flow);

        var left = new List<string>();

        foreach (var url in flow.Steps
            .Where(step => step.Type == "pull-request")
            .Select(step => step.Output?["url"])
            .OfType<JsonValue>()
            .Where(url => url.GetValueKind() == JsonValueKind.String)
            .Select(url => url.GetValue<string>())
            .Distinct(StringComparer.Ordinal))
        {
            left.Add($"pull request {url} is open: the run did not merge it and will not - merging it is a person's");
        }

        var commits = flow.Steps.Where(step => step.Type == "commit").ToList();
        var taken = commits.SelectMany(step => TextsOf(step.Output?["paths"])).ToHashSet(StringComparer.Ordinal);
        List<string> ignored = [.. commits.SelectMany(step => TextsOf(step.Output?["ignored"])).Distinct(StringComparer.Ordinal).Where(path => !taken.Contains(path))];

        if (ignored.Count > 0)
        {
            left.Add(
                $"{ReportText.Counted(ignored.Count, "path")} folded, ignored by git, never committed: {ReportText.Listed([.. ignored.Select(path => $"'{path}'")])} - "
                + "the run commits no path git ignores, and committing one, or leaving it out, is a person's");
        }

        foreach (var question in questions?.Open ?? [])
        {
            left.Add(
                $"question {question.Id} ({question.Kind}) is open: {question.ProblemShown?.Happened ?? question.Kind}; {RecommendationOf(question)} - "
                + $"answer with answer-orchestrator {layout.Name} <answer> --question {question.Id}");
        }

        foreach (var update in updates?.Updates.Where(update => update.State is FlowUpdateStates.Refused or FlowUpdateStates.Queued or FlowUpdateStates.UnderWay) ?? [])
        {
            left.Add(update.State switch
            {
                FlowUpdateStates.Queued => $"plan update {update.Id} ({(update.Cause == FlowUpdateCauses.ChangedAnswer ? "a changed answer" : "an amendment")}) is recorded and waits for the run to be gone on with",
                FlowUpdateStates.UnderWay => $"plan update {update.Id} ({(update.Cause == FlowUpdateCauses.ChangedAnswer ? "a changed answer" : "an amendment")}) is under way: the run is held while '{update.Step ?? "its planning step"}' plans it",
                _ => $"plan update {update.Id} ({(update.Cause == FlowUpdateCauses.ChangedAnswer ? "a changed answer" : "an amendment")}) was refused: {update.Why}",
            });
        }

        foreach (var step in flow.Steps.Where(step => step.Leftover is not null))
        {
            left.Add($"loop {step.Instance} left: {FlowValue.Of(step.Leftover!.DeepClone()).AsTemplateText()}");
        }

        return left;
    }

    /// <summary>The texts a step's output holds in a list - a commit's paths or ignored paths - in order; none where it holds none.</summary>
    private static IEnumerable<string> TextsOf(JsonNode? list)
        => list is JsonArray texts
            ? texts.OfType<JsonValue>().Where(text => text.GetValueKind() == JsonValueKind.String).Select(text => text.GetValue<string>())
            : [];

    /// <summary>A question's recommendation in words: the answer recommended and why, or that none was sought and why.</summary>
    private static string RecommendationOf(FlowQuestionEntry question)
        => question.Recommendation switch
        {
            { State: "given", Answer: { } answer } given => $"recommended: '{answer}'{(given.Why is { } why ? $", {why}" : string.Empty)}",
            { Why: { } why } => why,
            _ => "no recommendation was sought",
        };

    /// <summary>What a run spent, as the documents hold it.</summary>
    private static JsonObject SpentOf(FlowBudgetRecord spent) => new()
    {
        ["usd"] = spent.Usd,
        ["tokens"] = spent.Tokens,
        ["minutes"] = spent.Minutes,
    };

    /// <summary>One handoff, as the documents hold it.</summary>
    private static JsonObject HandoffOf(FlowHandoffNamed handoff) => new()
    {
        ["instance"] = handoff.Instance,
        ["by"] = handoff.By,
        ["file"] = handoff.File,
    };

    /// <summary>One plan update as the report's JSON document holds it: what raised it, word for word, and where it stands.</summary>
    private static JsonObject UpdateOf(FlowUpdateEntry update) => new()
    {
        ["id"] = update.Id,
        ["cause"] = update.Cause,
        ["state"] = update.State,
        ["takenAt"] = FlowTimeConverter.Written(update.TakenAt),
        ["amendment"] = update.Amendment,
        ["through"] = update.Through,
        ["question"] = update.Question,
        ["fromAnswer"] = update.FromAnswer,
        ["toAnswer"] = update.ToAnswer,
        ["text"] = update.Text,
        ["step"] = update.Step,
        ["then"] = update.Then,
        ["why"] = update.Why,
        ["pieces"] = update.ThePieces.Count == 0 ? null : new JsonArray([.. update.ThePieces.Select(PieceOf)]),
        ["told"] = update.Told is { Count: > 0 } told ? new JsonArray([.. told.Select(one => new JsonObject
        {
            ["who"] = one.Who,
            ["what"] = one.What,
            ["how"] = one.How,
        })]) : null,
        ["notConcerned"] = update.NotConcerned is { Count: > 0 } concerned ? new JsonArray([.. concerned.Select(one => (JsonNode?)one)]) : null,
        ["notDelivered"] = update.NotDelivered is { Count: > 0 } not ? new JsonArray([.. not.Select(one => new JsonObject
        {
            ["for"] = one.For,
            ["why"] = one.Why,
        })]) : null,
        ["addedIteration"] = update.AddedIteration,
    };

    /// <summary>One piece of a plan update as the report's JSON document holds it: its kind, its work item, and where it stands.</summary>
    private static JsonObject PieceOf(FlowUpdatePiece piece) => new()
    {
        ["kind"] = piece.Kind,
        ["item"] = piece.ItemName(),
        ["for"] = piece.For,
        ["placed"] = piece.Placed,
        ["owedTo"] = piece.OwedTo,
        ["carriedBy"] = piece.CarriedBy,
        ["ended"] = piece.Ended,
    };

    /// <summary>
    /// An iteration an update added, as the report says it - <c>round 3 of 'rounds'</c> - read of its own id, which
    /// names the loop and the iteration.
    /// </summary>
    /// <param name="iteration">The iteration's id, as the update's entry holds it.</param>
    internal static string IterationSaidOf(string iteration)
    {
        var at = iteration.LastIndexOf('#');

        return at > 0 && int.TryParse(iteration[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var round)
            ? $"round {round.ToString(CultureInfo.InvariantCulture)} of '{iteration[..at]}'"
            : $"'{iteration}'";
    }

    /// <summary>One step as the report's JSON document holds it.</summary>
    private static JsonObject StepOf(FlowStepRecord step) => new()
    {
        ["instance"] = step.Instance,
        ["state"] = step.State,
        ["attempt"] = step.Attempt,
        ["visit"] = step.Visit,
        ["exit"] = step.Exit,
        ["error"] = step.Error,
        ["skippedBecause"] = step.SkippedBecause,
        ["notRunBecause"] = step.NotRunBecause,
        ["passedOver"] = step.Failure == "passedOver",
        ["handledBy"] = step.HandledBy,
        ["leftover"] = step.Leftover?.DeepClone(),
    };

    /// <summary>One question as the documents hold it: its problem, its options, its recommendation and every answer it was given.</summary>
    private static JsonObject QuestionOf(FlowQuestionEntry question) => new()
    {
        ["id"] = question.Id,
        ["state"] = question.State,
        ["kind"] = question.Kind,
        ["problem"] = question.ProblemShown?.Happened,
        ["options"] = new JsonArray([.. question.Options.Select(option => new JsonObject
        {
            ["answer"] = option.Answer,
            ["does"] = option.Does,
            ["text"] = option.Text,
        })]),
        ["recommendation"] = question.Recommendation is { } recommendation
            ? new JsonObject
            {
                ["state"] = recommendation.State,
                ["answer"] = recommendation.Answer,
                ["why"] = recommendation.Why,
                ["by"] = recommendation.By,
            }
            : null,
        ["answers"] = new JsonArray([.. question.Answers.Select(answer => new JsonObject
        {
            ["answer"] = answer.Answer,
            ["text"] = answer.Text,
            ["by"] = answer.By?.Who,
            ["through"] = answer.By?.Through,
            ["where"] = answer.By?.Where,
            ["did"] = answer.Did,
            ["replacedAt"] = answer.ReplacedAt is { } replaced ? FlowTimeConverter.Written(replaced) : null,
        })]),
    };

    /// <summary>The report as a person reads it.</summary>
    private static string MarkdownOf(FlowFile flow, FlowQuestionsFile? questions, IReadOnlyList<FlowHandoffNamed> handoffs, string summary, int exitCode, IReadOnlyList<string> left, FlowUpdatesFile? updates = null)
    {
        var text = new List<string>
        {
            $"# Run '{flow.Run}' - {flow.Definition}",
            $"{summary} (exit {exitCode.ToString(CultureInfo.InvariantCulture)})",
            string.Empty,
            "## Steps",
        };

        foreach (var step in flow.Steps)
        {
            var said = $"- {step.Instance}: {step.State.ToLower(CultureInfo.InvariantCulture)}{(step.Visit > 1 ? $" (visit {step.Visit.ToString(CultureInfo.InvariantCulture)})" : string.Empty)}";
            var why = step.SkippedBecause ?? step.NotRunBecause
                ?? (step.Failure == "passedOver" ? "its failure was passed over (onFailure: continue)" : null)
                ?? (step.HandledBy is not null ? $"its failure was handled by {step.HandledBy}" : null)
                ?? step.Error;
            text.Add(why is null ? said : $"{said} - {why}");
        }

        var handled = flow.Steps.Where(step => step.HandledBy is not null).ToList();

        if (handled.Count > 0)
        {
            text.Add(string.Empty);
            text.Add("## Failures a handler took");
            text.AddRange(handled.Select(step => $"- {step.Step}, by {step.HandledBy}"));
        }

        var leftover = flow.Steps.Where(step => step.Leftover is not null).ToList();

        if (leftover.Count > 0)
        {
            text.Add(string.Empty);
            text.Add("## What the run's loops left");
            text.AddRange(leftover.Select(step => $"- {step.Instance}: {FlowValue.Of(step.Leftover!.DeepClone()).AsTemplateText()}"));
        }

        if (handoffs.Count > 0)
        {
            text.Add(string.Empty);
            text.Add("## Handoffs");
            text.AddRange(handoffs.Select(handoff => $"- {handoff.Instance}, written by the {handoff.By}: {handoff.File}"));
        }

        if (questions?.Questions.Count > 0)
        {
            text.Add(string.Empty);
            text.Add("## Questions");

            foreach (var question in questions.Questions)
            {
                text.Add($"- {question.Id} ({question.Kind}, {question.State}): {question.ProblemShown?.Happened ?? question.Kind}");
                text.Add($"  - {RecommendationOf(question)}");

                foreach (var answer in question.Answers)
                {
                    var replaced = answer.ReplacedAt is { } at ? $" (replaced at {FlowTimeConverter.Written(at)})" : string.Empty;
                    var did = answer.Did is { } done ? $"; {done}" : string.Empty;
                    var update = answer.Update is { } named ? $"; {named}" : string.Empty;
                    text.Add($"  - answered '{answer.Answer}' by {QuestionsText.Who(answer.By)}{(answer.Text is { } given ? $": \"{given}\"" : string.Empty)}{did}{update}{replaced}");
                }
            }
        }

        if (updates?.Updates.Count > 0)
        {
            text.Add(string.Empty);
            text.Add("## Plan updates");

            foreach (var update in updates.Updates)
            {
                var why = update.Why is { } held ? $" - {held}" : string.Empty;
                var step = update.State == FlowUpdateStates.UnderWay && update.Step is { } planning ? $" by '{planning}'" : string.Empty;

                text.Add(update.Cause == FlowUpdateCauses.ChangedAnswer
                    ? $"- {update.Id} (changed answer of {update.Question}, '{update.FromAnswer}' to '{update.ToAnswer}'), taken at {FlowTimeConverter.Written(update.TakenAt)}: {update.State}{step}{why}"
                    : $"- {update.Id} (amendment), taken at {FlowTimeConverter.Written(update.TakenAt)}: {update.State}{step}{why}{(update.Amendment is { } amendment ? $"\n  > {amendment.Replace("\n", "\n  > ")}" : string.Empty)}");

                foreach (var piece in update.ThePieces.Where(piece => piece.Kind != FlowUpdatePieceKinds.Told))
                {
                    // Each piece that carries work out: where it was carried out, and how that ended.
                    var where = piece.CarriedBy is { } carried
                        ? $"carried out by '{carried}'"
                        : piece.Placed is { } placed ? $"placed in '{placed}'" : $"owed to {piece.OwedTo}";
                    text.Add($"  - {piece.Kind} '{piece.ItemName()}': {where}{(piece.Ended is { } ended ? $", {ended}" : ", still under way")}");
                }

                if (update.AddedIteration is { } added)
                {
                    text.Add($"  - {IterationSaidOf(added)} was added for update {update.Id}");
                }

                foreach (var one in update.Told ?? [])
                {
                    text.Add($"  - told '{one.Who}' {one.What}, {one.How}");
                }

                if (update.NotConcerned is { Count: > 0 } concerned)
                {
                    text.Add($"  - not concerned: {string.Join(", ", concerned.Select(id => $"'{id}'"))}");
                }

                foreach (var not in update.NotDelivered ?? [])
                {
                    text.Add($"  - not delivered: {not.Why}");
                }
            }
        }

        text.Add(string.Empty);
        text.Add("## Spent");
        text.Add($"- {flow.Spent.Usd.ToString("0.00", CultureInfo.InvariantCulture)} USD, {flow.Spent.Tokens.ToString(CultureInfo.InvariantCulture)} tokens, {flow.Spent.Minutes.ToString("0.0", CultureInfo.InvariantCulture)} minutes of reasoning");

        if (left.Count > 0)
        {
            text.Add(string.Empty);
            text.Add("## What is left for a person");
            text.AddRange(left.Select(line => $"- {line}"));
        }

        return string.Join("\n", text);
    }
}
