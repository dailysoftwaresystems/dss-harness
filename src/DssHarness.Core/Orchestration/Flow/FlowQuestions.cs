using DssHarness.Core.Orchestration;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One question a run raises: what was asked, of which step, with the answers it takes, and every answer it was
/// given - by a person or by the configuration - while the run held it. The run's one file of questions and answers
/// on disk is written from these; this is the question itself, as the coordinator holds it.
/// </summary>
/// <param name="Id">The question's id (<c>q1</c>, <c>q2</c>, in the order raised).</param>
/// <param name="Kind">The kind of question it is, one of <see cref="WorkflowValidator.QuestionKinds"/>.</param>
/// <param name="Instance">The instance that asks it, by its id.</param>
/// <param name="Problem">What the question says: the problem, in the engine's own words.</param>
/// <param name="Options">The answers it takes, spelled as the kind spells them.</param>
public sealed class FlowQuestion(string Id, string Kind, string Instance, string Problem, IReadOnlyList<string> Options)
{
    /// <summary>The question's id (<c>q1</c>, <c>q2</c>, in the order raised).</summary>
    public string Id { get; } = Id;

    /// <summary>The kind of question it is, one of <see cref="WorkflowValidator.QuestionKinds"/>.</summary>
    public string Kind { get; } = Kind;

    /// <summary>The instance that asks it, by its id.</summary>
    public string Instance { get; } = Instance;

    /// <summary>What the question says: the problem, in the engine's own words.</summary>
    public string Problem { get; } = Problem;

    /// <summary>The answers it takes, spelled as the kind spells them.</summary>
    public IReadOnlyList<string> Options { get; } = Options;

    /// <summary>Whether the question waits: <c>open</c>, <c>answered</c> where an answer settled it, or <c>withdrawn</c> where a redirect ran its step again before anybody answered.</summary>
    public string State { get; internal set; } = "open";

    /// <summary>When the question was raised.</summary>
    public DateTimeOffset RaisedAt { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>What the run tried about it before it asked; null where nothing had been tried.</summary>
    public string? Tried { get; internal set; }

    /// <summary>What the answer changes, as the run's person reads it; null where the run has not said.</summary>
    public string? Means { get; internal set; }

    /// <summary>Every answer it was given, in the order they were given, and who gave each.</summary>
    public IReadOnlyList<FlowAnswerEntry> Given => _given;

    /// <summary>
    /// The answers it takes as its record holds them: each with what it does and whether it takes a text - the one owner
    /// of which answer needs a text, which the engine and <c>answer-orchestrator</c> both read.
    /// </summary>
    public IReadOnlyList<FlowQuestionOption> Held { get; internal set; } = FlowQuestions.HeldOptionsOf(Kind, null, Options);

    /// <summary>
    /// The handler a <c>visitBound</c> question is about, by its name: what its <c>more</c> grants one more visit, and
    /// what a configured answer of it is counted for; null for every other kind.
    /// </summary>
    public string? Handler { get; internal set; }

    /// <summary>Who raised it: a step, about the instance it names, or the engine, about the run itself.</summary>
    public FlowQuestionRaisedBy RaisedBy { get; internal set; } = Instance == FlowQuestions.RunsOwn ? new("engine") : new("step", Instance);

    /// <summary>The questions an agent or a step's output wrote, where there are any; empty for the engine's own.</summary>
    public IReadOnlyList<FlowAgentQuestion> AgentQuestions { get; internal set; } = [];

    /// <summary>The recommendation the question carries, and who wrote it - or the plain statement that none was sought.</summary>
    public FlowRecommendation Recommendation { get; internal set; } = FlowRecommendation.NotSought;

    internal readonly List<FlowAnswerEntry> _given = [];

    /// <summary>
    /// The question as <c>questions.json</c> holds it: one half of the one pair of conversions between the question the
    /// engine holds and its record, which together lose nothing - <see cref="FromEntry"/> of what this writes is this
    /// question again, and its every answer with it.
    /// </summary>
    public FlowQuestionEntry ToEntry()
        => new(
            Id,
            State,
            Kind,
            RaisedAt,
            AgentQuestions,
            Held,
            [.. _given.Select(entry => new FlowAnswerRecord(
                entry.Answer,
                entry.At ?? RaisedAt,
                entry.Text,
                new FlowAnswerBy(entry.By, entry.Through, entry.Where, entry.Input),
                entry.Did,
                entry.ReplacedAt))],
            RaisedBy,
            new FlowQuestionProblem(Problem, Tried, Means),
            Recommendation,
            Handler);

    /// <summary>
    /// The question a record of <c>questions.json</c> holds, whole - its state, every answer it was given with who gave
    /// it and through what, the answers it takes as they were shown, the handler it is about: the other half of the
    /// pair, so a resume goes on with every question as the run left it, answered, withdrawn and open alike.
    /// </summary>
    /// <param name="entry">The record, read strictly.</param>
    public static FlowQuestion FromEntry(FlowQuestionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var raisedBy = entry.RaisedBy ?? new FlowQuestionRaisedBy("engine");
        var question = new FlowQuestion(
            entry.Id,
            entry.Kind,
            raisedBy.Step ?? FlowQuestions.RunsOwn,
            entry.ProblemShown?.Happened ?? string.Empty,
            [.. entry.Options.Select(option => option.Answer)])
        {
            State = entry.State,
            RaisedAt = entry.RaisedAt,
            Tried = entry.ProblemShown?.Tried,
            Means = entry.ProblemShown?.Means,
            Held = entry.Options,
            Handler = entry.Handler,
            RaisedBy = raisedBy,
            AgentQuestions = entry.Questions,
            Recommendation = entry.Recommendation ?? FlowRecommendation.NotSought,
        };

        question._given.AddRange(entry.Answers.Select(answer => new FlowAnswerEntry(
            answer.Answer,
            answer.Text,
            answer.By?.Who ?? "person",
            answer.By?.Where,
            answer.By?.Through,
            answer.At,
            answer.Did,
            answer.By?.Input,
            answer.ReplacedAt)));

        return question;
    }
}

/// <summary>One answer a question was given: what was answered, with what text, and who gave it.</summary>
/// <param name="Answer">The answer, as the question's options spell it.</param>
/// <param name="Text">The text given with it, where the answer takes one; <see langword="null"/> where none was.</param>
/// <param name="By">
/// Who gave it: <c>person</c>, or <c>configuration</c> - the engine gives a configured answer at once, in the place
/// of a person, and records it as every answer is recorded.
/// </param>
/// <param name="Where">
/// Where the answer is configured, a configuration's answer alone: <c>step 'fix'</c>, <c>group 'items'</c> or
/// <c>the orchestrator's answers</c>; <see langword="null"/> for a person's.
/// </param>
/// <param name="Through">How a person gave it: <c>answer-orchestrator</c> or <c>front-end</c>; null for every other answer.</param>
/// <param name="At">When it was given.</param>
/// <param name="Did">What the engine did with it; null where it has not been applied.</param>
/// <param name="Input">The entry of a pre-answer that held, where the run's input gave it; null for every other answer.</param>
/// <param name="ReplacedAt">When a later answer replaced it, where one did; null while it stands.</param>
public sealed record FlowAnswerEntry(
    string Answer,
    string? Text,
    string By,
    string? Where,
    string? Through = null,
    DateTimeOffset? At = null,
    string? Did = null,
    string? Input = null,
    DateTimeOffset? ReplacedAt = null);

/// <summary>
/// The question kinds' own rules, beside the coordinator that raises and answers them: which answers a kind offers a
/// step (one owner of the spellings: <see cref="WorkflowValidator.AnswersOf"/>, with what a step's own keys narrow),
/// which kinds no configuration may answer, what a <c>fail</c> answer ends a kind's step with, and where a counted
/// answer is counted.
/// </summary>
public static class FlowQuestions
{
    /// <summary>The kinds only a person answers, whatever a configuration says: a lost row and a cell both changed.</summary>
    public static readonly IReadOnlyList<string> PersonsKinds = ["foldLostRows", "foldOverCells"];

    /// <summary>What a question the run raises of itself - its budget's, which no step asks - names as the instance it is about.</summary>
    public const string RunsOwn = "run";

    /// <summary>
    /// The answers a question of <paramref name="kind"/> takes, as its record holds them: each with what it does, and
    /// whether it takes a text - a <c>pause</c> step's option its <c>text</c> names, and <c>direct</c>, whose text is the
    /// fix a person gives the recovery's next visit.
    /// </summary>
    /// <param name="kind">The question's kind.</param>
    /// <param name="step">The step it asks of, where one does; null for the run's own.</param>
    /// <param name="options">The answers it takes, spelled as the kind spells them.</param>
    public static IReadOnlyList<FlowQuestionOption> HeldOptionsOf(string kind, WorkflowStep? step, IReadOnlyList<string> options)
        => [.. options.Select(option => new FlowQuestionOption(option, FlowAnswerDoes.Of(option), TakesText(kind, step, option)))];

    /// <summary>Whether <paramref name="option"/> of a question of <paramref name="kind"/> on <paramref name="step"/> is answered with a text.</summary>
    private static bool TakesText(string kind, WorkflowStep? step, string option)
        => (kind == "pause" && (step?.Text.Contains(option, StringComparer.Ordinal) ?? false))
            || (kind == "recoveryBound" && option == "direct");

    /// <summary>
    /// The answers <paramref name="kind"/> offers <paramref name="step"/>: the kind's own from
    /// <see cref="WorkflowValidator.AnswersOf"/>, with <c>failure</c> decided by the step's own <c>skippable</c> and
    /// <c>pause</c> by the step's declared <c>options</c> - and <c>skip</c> offered only where the step says
    /// <c>skippable</c>, for whatever kind offers it.
    /// </summary>
    public static IReadOnlyList<string> OptionsOf(string kind, WorkflowStep? step)
    {
        List<string> Options()
        {
            switch (kind)
            {
                case "failure":
                    return step?.Skippable == true ? ["retry", "abort", "skip"] : ["retry", "abort"];
                case "pause":
                    return [.. (step?.Options ?? [])];
                case "ask":
                    // An agent's questions are answered with a text or left to the agent; the answer's own spelling
                    // is 31's to wire, and the list stands so the kind is not a crash in its owner's table.
                    return ["answer", "decide", "abort"];
                case "foldLostRows" or "foldOverCells":
                    // A person's alone: no configuration answers it, so the kind's answers live here and not among the
                    // ones a configuration may give.
                    return ["allow", "retry", "abort"];
                default:
                    return [.. WorkflowValidator.AnswersOf[kind]];
            }
        }

        // skip is a kind's answer, narrowed by the step's own skippable - never a word a pause step's options or the
        // failure question's own list happen to use, which say what they say.
        return kind is "pause" or "failure" || step?.Skippable is true ? Options() : [.. Options().Where(option => option != "skip")];
    }

    /// <summary>
    /// Whether a question of <paramref name="kind"/> asks of an ending that has already happened - a handler at its bound,
    /// the recovery at its bound, a redirect that would leave an agent open - rather than of work a step still has to do.
    /// The step it is about keeps the state it ended in while the question is open, and what depends on it waits on the
    /// answer; a step whose work asks is <see cref="FlowInstanceState.Paused"/> instead.
    /// </summary>
    public static bool AsksOfAnEnding(string kind)
        => kind is "visitBound" or "recoveryBound" or "openAgents";

    /// <summary>
    /// What a <c>fail</c> answer ends a step of <paramref name="kind"/> with: the code the table of codes gives it -
    /// 13 for the fold family, 20 for a reason step's limit, 2 where no check ran and 21 where checks were still
    /// running or ended unsettled - with the question's own text as its error.
    /// </summary>
    public static int FailCodeOf(string kind)
        => kind switch
        {
            "foldNewRows" or "foldLostRows" or "foldOverCells" or "foldTwoChanges" or "foldConflicts" or "foldOutside"
                => DssHarness.Core.Results.HarnessExit.Refused,
            "turnLimit" or "stepBudget" or "truncated" or "contextLimit"
                => DssHarness.Core.Results.HarnessExit.CommandFailed,
            "noChecks" => 2,
            "checksPending" or "checksUnsettled" => DssHarness.Core.Results.HarnessExit.Incomplete,
            _ => DssHarness.Core.Results.HarnessExit.CommandFailed,
        };

    /// <summary>
    /// Where a counted answer of <paramref name="kind"/> on <paramref name="instance"/> is counted: one instance of a
    /// repeat for a loop's bound, one step instance for the kinds its own work raises - an answer that lets the same
    /// thing go on is counted across the attempts it itself creates, for nothing a configuration answers would
    /// otherwise ever stop - one handler in its scope for a handler's bound, and the run alone for its budget, the one
    /// question no step asks. The engine counts by it as it answers, and a resume counts the answers
    /// <c>questions.json</c> holds by it again, so a count a configuration spent stays spent across sessions.
    /// </summary>
    /// <param name="kind">The question's kind.</param>
    /// <param name="instance">The instance the question is about, by its id; null for the run's own.</param>
    /// <param name="handler">The handler a handler's bound is about; null for every other kind.</param>
    public static string CountedAt(string kind, string? instance, string? handler)
        => $"{kind}|{(kind == "runBudget" || instance is null ? RunsOwn : instance)}{(handler is null ? string.Empty : $":{handler}")}";

    /// <summary>
    /// The consents a fold's dry run needs, one at a time: a person's first, for the matter a person alone can
    /// settle is the one to put to a person while the run can still be told the rest.
    /// </summary>
    public static ConsentKind FirstOf(IReadOnlyList<ConsentKind> consents)
    {
        if (consents.Count == 0)
        {
            throw new ArgumentException("a dry run that needs no consent asks no question", nameof(consents));
        }

        var persons = consents
            .Select((consent, at) => (consent, at))
            .Where(entry => entry.consent is ConsentKind.FoldLostRows or ConsentKind.FoldOverCells)
            .Select(entry => (ConsentKind?)entry.consent)
            .FirstOrDefault();

        return persons ?? consents[0];
    }

    /// <summary>The question kind a reason step's limit is, by the limit a runner reports.</summary>
    public static string LimitKindOf(string limit)
        => limit switch
        {
            "turns" => "turnLimit",
            "budget" => "stepBudget",
            "truncated" => "truncated",
            "context" => "contextLimit",
            _ => throw new ArgumentOutOfRangeException(nameof(limit), limit, "a runner reported a limit that is no limit of a reason step"),
        };

    /// <summary>The question kind a wait for checks is, by what the wait found.</summary>
    public static string ChecksKindOf(string found)
        => found switch
        {
            "none" => "noChecks",
            "pending" => "checksPending",
            "unsettled" => "checksUnsettled",
            _ => throw new ArgumentOutOfRangeException(nameof(found), found, "a runner reported a reading of checks that is no reading of them"),
        };
}
