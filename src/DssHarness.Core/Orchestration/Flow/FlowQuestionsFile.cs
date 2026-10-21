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
/// The recommendation a question carries: an answer, why, and who gave it - or the plain statement that none was
/// sought. Nothing is put in a person's place.
/// </summary>
/// <param name="State"><c>given</c> or <c>notSought</c>.</param>
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
    /// <summary>The one recommendation this build's questions carry: none sought, said plainly.</summary>
    public static FlowRecommendation NotSought { get; } = new("notSought", Why: "no recommendation was sought: this build does not ask an advisor yet");
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
public sealed record FlowAnswerRecord(
    string Answer,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    string? Text = null,
    FlowAnswerBy? By = null,
    string? Did = null,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? ReplacedAt = null);

/// <summary>One question a step's output wrote or an agent asked, in the shape every question holds; empty for the engine's own.</summary>
/// <param name="Question">What is asked.</param>
/// <param name="Problem">What is open or stands in the way.</param>
/// <param name="Tried">Where the agent looked before it asked.</param>
/// <param name="Means">What depends on the answer.</param>
/// <param name="Options">The choices, at least two, each a sentence that says what it leads to.</param>
/// <param name="Recommended">The one recommended, word for word as it stands among the choices.</param>
/// <param name="Why">Why, by the bar the agent was given.</param>
public sealed record FlowAgentQuestion(
    string Question,
    string Problem,
    IReadOnlyList<string> Options,
    string Recommended,
    string Why,
    string? Tried = null,
    string? Means = null);

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
    IReadOnlyList<FlowAgentQuestion> Questions,
    IReadOnlyList<FlowQuestionOption> Options,
    IReadOnlyList<FlowAnswerRecord> Answers,
    FlowQuestionRaisedBy? RaisedBy = null,
    [property: JsonPropertyName("problem")] FlowQuestionProblem? ProblemShown = null,
    FlowRecommendation? Recommendation = null,
    string? Handler = null)
{
    /// <summary>The states a question stands in.</summary>
    public static readonly IReadOnlyList<string> States = ["open", "answered", "withdrawn"];

    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => !FlowQuestionIds.IsWellFormed(Id) ? $"'{Id}' is not a question's id"
            : !States.Contains(State, StringComparer.Ordinal) ? $"'{State}' is not a state a question stands in"
            : string.IsNullOrWhiteSpace(Kind) ? "its kind is empty"
            : Options.Count < 2 ? $"it takes {Options.Count.ToString(CultureInfo.InvariantCulture)} answers, and a question takes at least two"
            : Options.Any(option => string.IsNullOrWhiteSpace(option.Answer)) ? "an answer it takes is empty"
            : null;
}

/// <summary>How a question's id is spelled: <c>q&lt;n&gt;</c> from 1, in the order raised.</summary>
public static class FlowQuestionIds
{
    /// <summary>Whether <paramref name="id"/> is a question's id as this build spells it.</summary>
    public static bool IsWellFormed(string? id)
        => id is { Length: > 1 } && id[0] == 'q' && int.TryParse(id[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0;

    /// <summary>The id the <paramref name="n"/>-th question of a run takes.</summary>
    public static string Of(int n) => $"q{n.ToString(CultureInfo.InvariantCulture)}";
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

    /// <summary>The entry named, or null where the file holds none.</summary>
    public FlowQuestionEntry? Of(string id) => Questions.FirstOrDefault(question => string.Equals(question.Id, id, StringComparison.Ordinal));
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
