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
/// <c>given</c>; <c>sought</c> while the advisor runs; <c>deferred</c> where a pause or a stop came first, sought again at
/// the resume; <c>cancelled</c> where an answer came while the advisor ran; <c>notObtained</c> where it was sought and none
/// was had; or <c>notSought</c>.
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
    /// <summary>The recommendation a question carries where none was sought: said plainly, and nothing is put in a person's place.</summary>
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
