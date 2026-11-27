using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>Who raised a question: a step (a <c>pause</c> step, or a step whose ending did), or the engine itself.</summary>
/// <param name="Who">Who raised it: <c>step</c> or <c>engine</c>.</param>
/// <param name="Step">The instance it is about, where one is; null for the run's own (<c>runBudget</c>).</param>
/// <param name="Agent">The agent that asked, where one did; null for every question this build raises.</param>
public sealed record FlowQuestionRaisedBy(string Who, string? Step = null, string? Agent = null);

/// <summary>The problem a question states: what happened, what was tried before it asked, and what the answer changes.</summary>
/// <param name="Happened">What happened, as the run's person reads it.</param>
/// <param name="Tried">What the run did about it before it asked; null where nothing had been tried.</param>
/// <param name="Means">What the answer changes: which steps wait on it, by name; null where none do.</param>
public sealed record FlowQuestionProblem(string Happened, string? Tried = null, string? Means = null);

/// <summary>One answer a question takes, with what it does and whether it needs a text.</summary>
/// <param name="Answer">The answer, spelled as it is answered.</param>
/// <param name="Does">What it does, one sentence.</param>
/// <param name="Text">Whether the answer needs a text given with it.</param>
public sealed record FlowQuestionOption(string Answer, string Does, bool Text = false);

/// <summary>
/// The recommendation a question carries: an answer, why, and who gave it - or the plain statement of where it stands
/// where there is none. Nothing is put in a person's place.
/// </summary>
/// <param name="State">
/// <c>given</c>; <c>sought</c> while the advisor runs; <c>deferred</c> where the question was raised while the run was
/// pausing - sought once the pause is complete - or a stop ended its advice or came first, sought again at the resume;
/// <c>cancelled</c> where an answer came while the advisor ran; <c>notObtained</c> where it was sought and none
/// was had; or <c>notSought</c> where none has been sought yet - a question its configuration or the run's input answered
/// at once, which no person was asked. A <c>notSought</c> an earlier build wrote with why none was sought for a question
/// still open - its orchestrator named no advisor, the advisor's kinds left the question's kind out, the budget's own -
/// is read as it stands, and the recommendation is sought at the next session that can (every question is recommended
/// before a person is asked it).
/// </param>
/// <param name="Answer">The answer recommended, where one was.</param>
/// <param name="Text">The text recommended with it, where the answer takes one.</param>
/// <param name="Why">Why that answer, by the bar the recommender was given.</param>
/// <param name="By">Who recommended it: <c>advisor</c> or <c>agent</c>.</param>
/// <param name="Agent">The agent that recommended it, where one did.</param>
/// <param name="Model">The model that served it, where one did.</param>
/// <param name="Advice">The directory under the asking step that holds how the advisor came to it.</param>
public sealed record FlowRecommendation(
    string State,
    string? Answer = null,
    string? Text = null,
    string? Why = null,
    string? By = null,
    string? Agent = null,
    string? Model = null,
    string? Advice = null)
{
    /// <summary>The recommendation a question carries until one is sought - and for good where it was answered at once, by its configuration or the run's input, asking no person.</summary>
    public static FlowRecommendation NotSought { get; } = new("notSought", Why: "no recommendation was sought");
}

/// <summary>Who gave an answer: a person (and through what), the configuration (and where), or the run's input.</summary>
/// <param name="Who"><c>person</c>, <c>configuration</c> or <c>input</c>.</param>
/// <param name="Through">How a person gave it: <c>answer-orchestrator</c> or <c>front-end</c>; null for every other.</param>
/// <param name="Where">Where the configuration says so, as the run's records name it; null for a person's and an input's.</param>
/// <param name="Input">The entry of a pre-answer that held, with the values it read and the words it quotes; null for every other.</param>
/// <param name="Inbox">
/// The id of the <c>inbox.json</c> entry a person's answer was handed over under, to an engine running the run: written
/// with the answer, so the entry is settled as long as the run is - a session that finds it in the inbox only empties it
/// from there. Null for every answer no inbox handed over.
/// </param>
public sealed record FlowAnswerBy(string Who, string? Through = null, string? Where = null, string? Input = null, string? Inbox = null);

/// <summary>
/// One answer handed over through <c>inbox.json</c> that the run did not take, as <c>questions.json</c> keeps it: the
/// entry's id, what it answered, when and why it was refused - written before the <c>answer</c> event that tells of it, so
/// the entry is settled as long as the run is, and a session that finds it in the inbox only empties it from there.
/// </summary>
/// <param name="Inbox">The id of the entry the answer was handed over under.</param>
/// <param name="Question">The question it named.</param>
/// <param name="Answer">The answer, as it was handed over.</param>
/// <param name="At">When the run refused it.</param>
/// <param name="Why">Why the run did not take it.</param>
public sealed record FlowRefusedAnswer(
    string Inbox,
    string Question,
    string Answer,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    string Why)
{
    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Inbox) ? "it names no inbox entry"
            : !FlowQuestionIds.IsWellFormed(Question) ? $"'{Question}' is not a question's id"
            : string.IsNullOrWhiteSpace(Answer) ? "its answer is empty"
            : string.IsNullOrWhiteSpace(Why) ? "it says not why it was refused"
            : null;
}

/// <summary>One answer a question was given, in order: the answer, the text with it, who gave it, and what the engine did.</summary>
/// <param name="Answer">The answer, spelled as the question's options spell it.</param>
/// <param name="Text">The text given with it, where it takes one.</param>
/// <param name="At">When it was given.</param>
/// <param name="By">Who gave it.</param>
/// <param name="Did">What the engine did with it; null where the answer has not been applied yet.</param>
/// <param name="ReplacedAt">When a later answer replaced it, where one did; null while it stands.</param>
/// <param name="Update">The id of the plan update that changed this answer, where one did; null for every answer a person gave as its first.</param>
/// <param name="Order">
/// Where the answer stands among every answer the run recorded, from 1, in the order it recorded them: what tells two
/// answers given in one second apart, which their times - kept to the second - cannot. Null where the record does not
/// say, which orders it before every answer that does.
/// </param>
public sealed record FlowAnswerRecord(
    string Answer,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    string? Text = null,
    FlowAnswerBy? By = null,
    string? Did = null,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? ReplacedAt = null,
    string? Update = null,
    int? Order = null);

/// <summary>
/// One question a run raised, as <c>questions.json</c> holds it: its id, kind and state, who raised it, the problem
/// as it was shown, the options each with what it does, the recommendation and who wrote it, and every answer it was
/// given in order.
/// </summary>
/// <param name="Id">The question's id: <c>q&lt;n&gt;</c>, in the order raised.</param>
/// <param name="State"><c>open</c>, <c>answered</c>, or <c>withdrawn</c> where a redirect ran its step again before anybody answered.</param>
/// <param name="Kind">The question's kind, which says which answers there are.</param>
/// <param name="RaisedAt">When it was raised.</param>
/// <param name="RaisedBy">Who raised it.</param>
/// <param name="ProblemShown">The problem as it was shown, written as the file's <c>problem</c> member.</param>
/// <param name="Questions">The questions an agent or a step's output wrote, where there are any; empty for the engine's own.</param>
/// <param name="Options">The answers it takes, each with what it does.</param>
/// <param name="Recommendation">The recommendation and who wrote it.</param>
/// <param name="Answers">Every answer it was given, in order.</param>
/// <param name="Handler">
/// The handler a <c>visitBound</c> question is about, by its name: what its <c>more</c> grants one more visit, and what a
/// configured answer of it is counted for; null for every other kind.
/// </param>
public sealed record FlowQuestionEntry(
    string Id,
    string State,
    string Kind,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset RaisedAt,
    IReadOnlyList<Agents.AgentQuestion> Questions,
    IReadOnlyList<FlowQuestionOption> Options,
    IReadOnlyList<FlowAnswerRecord> Answers,
    FlowQuestionRaisedBy? RaisedBy = null,
    [property: JsonPropertyName("problem")] FlowQuestionProblem? ProblemShown = null,
    FlowRecommendation? Recommendation = null,
    string? Handler = null)
{
    /// <summary>The states a question stands in.</summary>
    public static readonly IReadOnlyList<string> States = ["open", "answered", "withdrawn"];

    /// <summary>
    /// What is wrong with this entry; null when nothing is. The questions an agent or a step's output wrote are held to
    /// their record's own rule (<see cref="Agents.AgentQuestion.Problems"/>), as the answer that held them was.
    /// </summary>
    public string? Problem()
        => !FlowQuestionIds.IsWellFormed(Id) ? $"'{Id}' is not a question's id"
            : !States.Contains(State, StringComparer.Ordinal) ? $"'{State}' is not a state a question stands in"
            : string.IsNullOrWhiteSpace(Kind) ? "its kind is empty"
            : Options.Count < 2 ? $"it takes {Options.Count.ToString(CultureInfo.InvariantCulture)} answers, and a question takes at least two"
            : Options.Any(option => string.IsNullOrWhiteSpace(option.Answer)) ? "an answer it takes is empty"
            : Agents.AgentQuestion.ProblemIn(Questions ?? []) is { } asked ? $"its {asked}"
            : null;
}

/// <summary>How a question's id is spelled: <c>q&lt;n&gt;</c> from 1, in the order raised.</summary>
public static class FlowQuestionIds
{
    /// <summary>Whether <paramref name="id"/> is a question's id as this build spells it.</summary>
    public static bool IsWellFormed(string? id) => OrchestrationRules.IsNumbered(id, 'q');

    /// <summary>The id the <paramref name="n"/>-th question of a run takes.</summary>
    public static string Of(int n) => OrchestrationRules.Numbered('q', n);
}

/// <summary>
/// <c>questions.json</c>: every question the run raised and every answer it was given, the open ones among them - the
/// one owner of what a question said, what was recommended, who answered and what the engine did, an answer handed over
/// through the inbox among them: one it took under its entry's id (<see cref="FlowAnswerBy.Inbox"/>), one it refused in
/// <see cref="Refused"/>. Written whole each time, outside every directory an agent may write; a step's own record lists
/// only the ids of the questions its step raised. Its texts are not masked yet: a run carries no secret to mask until its
/// steps are served by the step runners and the agent clients, which give the run the mask its records are written with.
/// </summary>
/// <param name="Schema">The file's schema, 1.</param>
/// <param name="Run">The run's name.</param>
/// <param name="UpdatedAt">When the file was last written.</param>
/// <param name="Questions">Every question in the order raised, answered, open and withdrawn alike.</param>
/// <param name="Refused">Every answer handed over through the inbox that the run did not take, in the order refused; null where none was.</param>
public sealed record FlowQuestionsFile(
    int Schema,
    string Run,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset UpdatedAt,
    IReadOnlyList<FlowQuestionEntry> Questions,
    IReadOnlyList<FlowRefusedAnswer>? Refused = null)
{
    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : string.IsNullOrWhiteSpace(Run) ? "its run is empty"
            : Questions.Any(question => question.Problem() is { } problem) ? $"its question '{Questions.First(question => question.Problem() is not null).Id}': {Questions.First(question => question.Problem() is not null).Problem()}"
            : Questions.Select(question => question.Id).GroupBy(id => id, StringComparer.Ordinal).Any(group => group.Count() > 1)
                ? "one id holds two questions"
            : (Refused ?? []).FirstOrDefault(refused => refused.Problem() is not null) is { } wrong ? $"its refused answer '{wrong.Inbox}': {wrong.Problem()}"
            : InboxEntries().GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice
                ? $"inbox entry '{twice.Key}' is recorded twice, and the run weighs an entry once"
                : null;

    /// <summary>The id of every inbox entry the run recorded what it did with: each answer it took under one, then each it refused.</summary>
    private IEnumerable<string> InboxEntries()
        => Questions.SelectMany(question => question.Answers).Select(answer => answer.By?.Inbox).OfType<string>()
            .Concat((Refused ?? []).Select(refused => refused.Inbox));

    /// <summary>The questions the run leaves open, in the order raised.</summary>
    public IReadOnlyList<FlowQuestionEntry> Open => [.. Questions.Where(question => question.State == "open")];

    /// <summary>
    /// What the run did with the answer handed over under the inbox entry <paramref name="inbox"/>, as this file records it
    /// under the entry's id: the question it took it as an answer of, or the refusal it wrote for it - both null where the
    /// run has not weighed it yet.
    /// </summary>
    /// <param name="inbox">The inbox entry's id.</param>
    public (FlowQuestionEntry? Took, FlowRefusedAnswer? Refused) Weighed(string inbox)
        => (
            Questions.FirstOrDefault(question => question.Answers.Any(answer => string.Equals(answer.By?.Inbox, inbox, StringComparison.Ordinal))),
            (Refused ?? []).FirstOrDefault(refused => string.Equals(refused.Inbox, inbox, StringComparison.Ordinal)));

    /// <summary>The entry named, or null where the file holds none.</summary>
    public FlowQuestionEntry? Of(string id) => Questions.FirstOrDefault(question => string.Equals(question.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// The next step an open question takes, as every document of the run names it and every refusal of an answer says it
/// (<see cref="FlowAnswerRules.NextStepOf"/>, <see cref="FlowAnswerRules.NextStepSaid"/>).
/// </summary>
public enum FlowNextStep
{
    /// <summary>It owes nothing: <c>answer-orchestrator</c> answers it.</summary>
    Answer,

    /// <summary>Its advisor seeks the recommendation now: an answer is taken at once, and ends the asking.</summary>
    AnswerWhileAdvised,

    /// <summary>A live engine's pause defers its recommendation, which the pause seeks once complete: it is answered then, and <c>abort</c> is taken now.</summary>
    AfterThePause,

    /// <summary>No engine runs the run, or a gone one: <c>resume-orchestrator</c> seeks its recommendation first, and <c>abort</c> is taken now.</summary>
    Resume,

    /// <summary>The engine is stopping, and seeks none: <c>resume-orchestrator</c> seeks its recommendation once the engine has gone, and <c>abort</c> is taken now.</summary>
    ResumeOnceGone,
}

/// <summary>
/// The rules every path into an answer is held to - <c>answer-orchestrator</c> before anything of the run is touched,
/// and the engine where it weighs an answer handed over, or a change of one - written once, so the command and the
/// engine never disagree about what an answer may be.
/// </summary>
public static class FlowAnswerRules
{
    /// <summary>
    /// Why <paramref name="answer"/>, with <paramref name="text"/>, is no answer the question <paramref name="question"/>
    /// takes; null where it is one. The answer is one of the question's own - <c>abort</c> among them for a first answer,
    /// whatever the kind, and never for a changed one, for a run is ended by <c>stop-orchestrator</c> and gone on with by
    /// <c>resume-orchestrator</c>. An answer whose option takes a text is given one, and one whose option takes none is
    /// given none, whatever the question's kind: a text no step reads would be recorded and never read. A changed answer
    /// changes the answer or its text.
    /// </summary>
    /// <param name="question">The question's id, for the words.</param>
    /// <param name="options">The answers the question takes, as its record holds them: each with whether it takes a text.</param>
    /// <param name="answer">The answer given.</param>
    /// <param name="text">The text given with it; a blank one reads as none.</param>
    /// <param name="standing">For a change of an answer, the answer it changes and its text; null for a first answer.</param>
    public static string? Problem(string question, IReadOnlyList<FlowQuestionOption> options, string answer, string? text, (string Answer, string? Text)? standing = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var change = standing is not null;

        if (change && answer == "abort")
        {
            return "'abort' does not change an answer: a run is ended by stop-orchestrator, and one an abort ended is gone on with by resume-orchestrator";
        }

        var option = options.FirstOrDefault(option => string.Equals(option.Answer, answer, StringComparison.Ordinal));

        if (option is null && (change || answer != "abort"))
        {
            return $"'{answer}' is not one of the answers '{question}' takes: {string.Join(", ", options.Select(option => option.Answer).Where(taken => !change || taken != "abort"))}";
        }

        var given = TextOf(text);

        if (option is { Text: true } && given is null)
        {
            return $"'{answer}' is an answer '{question}' takes with a text; give one";
        }

        if (option is not { Text: true } && given is not null)
        {
            return $"'{answer}' is an answer '{question}' takes with no text; give none";
        }

        return standing is { } was && string.Equals(was.Answer, answer, StringComparison.Ordinal) && was.Text == given
            ? $"'{answer}' is the answer '{question}' already has; a changed answer changes it"
            : null;
    }

    /// <summary>The text an answer is given, as every record holds it: none where it is blank.</summary>
    /// <param name="text">The text as it was given.</param>
    public static string? TextOf(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    /// <summary>
    /// Whether a question of <paramref name="kind"/> carries its recommendation within it and is never put to the advisor:
    /// an agent's <c>ask</c>, and a <c>pause</c> step's that shows an upstream step's questions.
    /// </summary>
    /// <param name="kind">The question's kind.</param>
    /// <param name="agentQuestions">How many questions an agent or a step's output wrote into it.</param>
    public static bool CarriesItsOwn(string kind, int agentQuestions) => kind == "ask" || agentQuestions > 0;

    /// <summary>
    /// Whether a question still owes the recommendation it is to be answered with: none sought yet, one a session that
    /// ended left sought, or one deferred by a pause under way or a stop - never one given, obtained or not, nor an
    /// agent's own question, which carries its own (<see cref="CarriesItsOwn"/>). The one owner of the rule: the engine
    /// seeks advice by it, and a person's answer is held to it (<see cref="Unrecommended"/>).
    /// </summary>
    /// <param name="kind">The question's kind.</param>
    /// <param name="recommendation">The recommendation it carries; null, as an older questions file may hold it, reads as none sought.</param>
    /// <param name="agentQuestions">How many questions an agent or a step's output wrote into it.</param>
    public static bool OwesRecommendation(string kind, FlowRecommendation? recommendation, int agentQuestions)
        => (recommendation ?? FlowRecommendation.NotSought).State is "notSought" or "sought" or "deferred"
            && !CarriesItsOwn(kind, agentQuestions);

    /// <summary>
    /// The next step the open question <paramref name="question"/> takes, as a reader of the run's records names it with
    /// what the run's engine is doing (<see cref="OrchestratorService.EngineOf(OrchestrationStore, FlowRunOwner, OrchestratorLayout, FlowFile)"/>,
    /// the rule <c>answer-orchestrator</c>'s look at the run's owner applies): the one owner of the step every document of
    /// the run names - <c>ask-orchestrator-status</c>'s waits, the report's what is left for a person, which the analyst
    /// is handed too. A question that owes nothing is answered (<see cref="FlowNextStep.Answer"/>). One that still owes the
    /// recommendation it is answered with (<see cref="OwesRecommendation"/>) is, with no engine running the run or a gone
    /// one, the resume's, which seeks it first - no advisor seeks one between sessions (<see cref="FlowNextStep.Resume"/>);
    /// with an engine that is stopping, the resume's once the engine has gone, for a stop starts no advice and ends every
    /// one under way (<see cref="FlowNextStep.ResumeOnceGone"/>); with a live engine whose advisor seeks it now
    /// (<c>sought</c>), answered at once, the answer ending the asking - and so with a running engine whose record does not
    /// say it deferred, for a running engine seeks a recommendation as it raises the question
    /// (<see cref="FlowNextStep.AnswerWhileAdvised"/>); and with a live engine that is pausing or recording what its complete
    /// pause left running - or running, its recommendation deferred, which only a pause asked does - answered once the
    /// pause's advice is given, which a complete pause seeks before its session ends (<see cref="FlowNextStep.AfterThePause"/>).
    /// <c>abort</c> is taken now wherever a recommendation is owed. The engine names the step it refuses an answer with from
    /// its own state, which it knows (<see cref="Unrecommended"/>), and every step is said in one wording (<see cref="NextStepSaid"/>).
    /// </summary>
    /// <param name="question">The open question, as <c>questions.json</c> holds it.</param>
    /// <param name="engine">What the run's engine is doing.</param>
    public static FlowNextStep NextStepOf(FlowQuestionEntry question, FlowEngineState engine)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(engine);

        var recommendation = question.Recommendation ?? FlowRecommendation.NotSought;

        if (!OwesRecommendation(question.Kind, recommendation, (question.Questions ?? []).Count))
        {
            return FlowNextStep.Answer;
        }

        return engine.State switch
        {
            FlowEngineState.None or FlowEngineState.Gone => FlowNextStep.Resume,
            FlowEngineState.Stopping => FlowNextStep.ResumeOnceGone,
            _ when recommendation.State == "sought" => FlowNextStep.AnswerWhileAdvised,
            FlowEngineState.Pausing or FlowEngineState.Recording => FlowNextStep.AfterThePause,
            _ when recommendation.State == "deferred" => FlowNextStep.AfterThePause,
            _ => FlowNextStep.AnswerWhileAdvised,
        };
    }

    /// <summary>
    /// The next step <paramref name="step"/> of the open question <paramref name="question"/> in words: the one wording
    /// every document of the run says it in, each showing a command line as it shows every other (<paramref name="command"/>).
    /// The answer, with <c>answer-orchestrator</c>; while the advisor seeks the recommendation, the answer taken now and
    /// ending the asking; and where the recommendation is still owed, who seeks it - the pause once complete, the resume,
    /// or the resume once a stopping engine has gone - the answer to give then, and <c>abort</c>, taken now.
    /// </summary>
    /// <param name="step">The step (<see cref="NextStepOf"/>).</param>
    /// <param name="run">The run's name; null where the run was given none, and the command lines name it as <c>&lt;run&gt;</c>.</param>
    /// <param name="question">The question's id.</param>
    /// <param name="command">Shows one command line from the command and its arguments; null for the plain words, as a refusal and the report say them.</param>
    public static string NextStepSaid(FlowNextStep step, string? run, string question, Func<string, string[], string>? command = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var shown = command ?? Plain;
        var answer = shown(FlowService.AnswerCommand, [RunOf(run), "<answer>", "--question", question]);

        return step switch
        {
            FlowNextStep.Answer => $"answer with {answer}",
            FlowNextStep.AnswerWhileAdvised => $"its recommendation is being sought: answer with {answer}, taken now and ending the asking",
            _ => $"its recommendation is still owed - {Owed(step, run, question, shown)}",
        };
    }

    /// <summary>
    /// Why a person's answer to the open question <paramref name="question"/> is not taken while it owes its recommendation
    /// and no advisor is seeking it now; null where it is taken. Every question is answered recommended: an answer given
    /// while the advisor works is taken, and ends the asking, but one given while nothing seeks the recommendation is
    /// refused, nothing answered, naming the step that seeks it first - the resume between sessions and as a session
    /// begins, the resume once the engine has gone where a stop ends the session - a stop once the pause is complete among
    /// them, after which the pause seeks none - and the pause once it is complete while a pause under way defers it - in
    /// the one wording every document says the step in (<see cref="NextStepSaid"/>). <c>abort</c> is taken whatever the
    /// recommendation's state: it settles nothing of the question, which stays open with its recommendation owed. The
    /// command, before anything of the run is touched, and the engine, as it weighs a person's answer, hold an answer to
    /// this one rule, each naming the step from what it knows of the engine: the command with no engine running the run
    /// (<see cref="NextStepOf"/>), the engine from its own state.
    /// </summary>
    /// <param name="run">The run's name, for the words; null where the run was given none.</param>
    /// <param name="question">The question's id.</param>
    /// <param name="answer">The answer given.</param>
    /// <param name="step">The step the question takes next: <see cref="FlowNextStep.Answer"/> or <see cref="FlowNextStep.AnswerWhileAdvised"/> where it owes nothing or its advisor seeks it now, which takes the answer.</param>
    public static string? Unrecommended(string? run, string question, string answer, FlowNextStep step)
    {
        if (step is FlowNextStep.Answer or FlowNextStep.AnswerWhileAdvised || answer == "abort")
        {
            return null;
        }

        var named = run is { Length: > 0 } ? $"question '{question}' of run '{run}'" : $"question '{question}'";
        return $"{named} still owes the recommendation it is answered with, so nothing was answered - {Owed(step, run, question, Plain)}";
    }

    /// <summary>
    /// The words of a step whose question still owes its recommendation: who seeks it, the answer to give then, and
    /// <c>abort</c>, taken now.
    /// </summary>
    private static string Owed(FlowNextStep step, string? run, string question, Func<string, string[], string> shown)
    {
        var resume = shown(FlowService.ResumeCommand, [RunOf(run)]);
        var seeker = step switch
        {
            FlowNextStep.AfterThePause => "the run seeks it once its pause is complete",
            FlowNextStep.ResumeOnceGone => $"the stop under way seeks none, and {resume} seeks it first once the engine has gone",
            _ => $"no advisor seeks one between sessions, and {resume} seeks it first",
        };

        return $"{seeker}; answer with {shown(FlowService.AnswerCommand, [RunOf(run), "<answer>", "--question", question])} then, "
            + $"and {shown(FlowService.AnswerCommand, [RunOf(run), "abort", "--question", question])} is taken now";
    }

    /// <summary>A command line in plain words: the command and its arguments, as a refusal and the report say them.</summary>
    private static string Plain(string command, string[] arguments) => string.Join(' ', [command, .. arguments]);

    /// <summary>The run as a command line names it: its name, or <c>&lt;run&gt;</c> where it was given none.</summary>
    private static string RunOf(string? run) => run is { Length: > 0 } ? run : "<run>";

    /// <summary>
    /// Why the set <paramref name="set"/> can have its answer changed no more; null where it can. A set is changed one
    /// time, and the run's plan updates are the authority on it: an update that changes the set's answer - queued, under
    /// way, done or failed, every one but a refused one, which changed nothing - holds its one change, whether or not the
    /// set's own record holds the change yet, for a crash may come between the update's entry and the set's record; and a
    /// set whose own record holds a replaced answer was changed, said with when. The command, and the engine where it
    /// takes a change and where it takes a queued one up again, hold a change to this one rule.
    /// </summary>
    /// <param name="run">The run's name, for the words.</param>
    /// <param name="set">The set's question id.</param>
    /// <param name="replacedAt">When the set's own record says its first answer was replaced; null where it says none was.</param>
    /// <param name="updates">Every plan update the run has taken.</param>
    /// <param name="taking">The update being taken up again, which is not counted against itself; null for a change being given.</param>
    public static string? ChangedAlready(string? run, string set, DateTimeOffset? replacedAt, IEnumerable<FlowUpdateEntry> updates, string? taking = null)
    {
        ArgumentNullException.ThrowIfNull(updates);

        if (replacedAt is { } changed)
        {
            return $"question '{set}' of run '{run}' had its answer changed already, at {FlowTimeConverter.Written(changed)}; a set is changed one time";
        }

        return updates.FirstOrDefault(update => update.Cause == FlowUpdateCauses.ChangedAnswer
                && string.Equals(update.Question, set, StringComparison.Ordinal)
                && update.State != FlowUpdateStates.Refused
                && !string.Equals(update.Id, taking, StringComparison.Ordinal)) is { } holds
            ? $"question '{set}' of run '{run}' has its answer changed already - update {holds.Id} holds the change, taken at {FlowTimeConverter.Written(holds.TakenAt)}; a set is changed one time"
            : null;
    }

    /// <summary>
    /// The last set a person answered: the question whose newest answer by a person is the newest of all the run's,
    /// answered and standing - the one set a changed answer may change. Two answers given in one second, which their
    /// times kept to the second cannot tell apart, are told apart by the order the run recorded them in
    /// (<see cref="FlowAnswerRecord.Order"/>). An answer the configuration or the run's input gave is no set, and is
    /// never counted. Null where no person has answered the run.
    /// </summary>
    /// <param name="questions">Every question the run raised, with every answer each was given.</param>
    public static FlowQuestionEntry? LastSetAnswered(IEnumerable<FlowQuestionEntry> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);

        FlowQuestionEntry? last = null;
        (DateTimeOffset At, int Order)? newest = null;

        foreach (var entry in questions.Where(entry => entry.State == "answered"))
        {
            foreach (var answer in entry.Answers.Where(answer => answer.By?.Who == "person"))
            {
                var given = (answer.At, answer.Order ?? 0);

                if (newest is not { } standing || given.CompareTo(standing) > 0)
                {
                    last = entry;
                    newest = given;
                }
            }
        }

        return last;
    }

    /// <summary>Why a run has no set to change: no person answered it.</summary>
    /// <param name="run">The run's name.</param>
    public static string NoSetToChange(string? run) => $"no answer of run '{run}' was given by a person, so there is no set to change";
}

/// <summary>What each answer a question takes does, one sentence each, as the option is shown with it.</summary>
public static class FlowAnswerDoes
{
    private static readonly IReadOnlyDictionary<string, string> Sentences = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["more"] = "one more of what was bounded",
        ["retry"] = "the step runs again as a new attempt",
        ["accept"] = "what was reached is taken as it stands",
        ["allow"] = "the consent asked for is given by name, and the dry run is made again first",
        ["merge"] = "the paths both trees changed that the dry run named are merged three-way",
        ["stay"] = "the redirect is given up, and the run goes on as if the handler named no then",
        ["compact"] = "the step goes on with the same work in a conversation the engine reduced",
        ["skip"] = "the step ends Skipped, which satisfies what depends on it",
        ["fail"] = "the step ends Failed, with the question's text as its error",
        ["abort"] = "the run ends as a hard stop ends it",
        ["decide"] = "the person leaves it to the agent: it goes on with what it recommended",
        ["answer"] = "the text given is read by the agent as a person wrote it",
        ["direct"] = "the fix given in the text is what the next visit works with",
    };

    /// <summary>What <paramref name="answer"/> does, where this build has its sentence; else what any answer does.</summary>
    public static string Of(string answer)
        => Sentences.TryGetValue(answer, out var sentence) ? sentence : "what this answer does for its question's kind";
}
