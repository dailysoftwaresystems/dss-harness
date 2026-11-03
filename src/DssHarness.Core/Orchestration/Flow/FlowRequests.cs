using System.Globalization;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>A safe pause another process asked of the running engine: whether it stops the machine steps too.</summary>
/// <param name="All">Whether the machine steps in progress are stopped too.</param>
/// <param name="RequestedAt">When the request was made.</param>
public sealed record FlowStopPause(bool All, [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset RequestedAt);

/// <summary>A stop another process asked of the running engine: the hard stop.</summary>
/// <param name="RequestedAt">When the request was made.</param>
public sealed record FlowStopStop([property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset RequestedAt);

/// <summary>
/// <c>stop.json</c>: what another process asked of the running engine - a safe pause (and whether it stops the
/// machine steps too), or a stop. Written by <c>pause-orchestrator</c> and <c>stop-orchestrator</c> under the store's
/// lock, read by the engine at its poll, and taken - read and removed - once the engine has applied what it asks. One
/// request at a time: exactly one of a pause and a stop. A request no engine took is removed by the session that
/// ends without having taken it and by the next that begins, so it never reaches a session it was not asked of.
/// </summary>
/// <param name="Schema">The file's schema, 1.</param>
/// <param name="Pause">The safe pause asked for, where one is.</param>
/// <param name="Stop">The stop asked for, where one is.</param>
public sealed record FlowStopFile(int Schema, FlowStopPause? Pause = null, FlowStopStop? Stop = null)
{
    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : Pause is not null && Stop is not null ? "it asks for a pause and a stop at once"
            : Pause is null && Stop is null ? "it asks for nothing"
            : null;
}

/// <summary>
/// One answer on its way to an engine that is running the run, in <c>inbox.json</c>: weighed by the engine, what became
/// of it written into <c>questions.json</c> under its id - the answer taken, or its refusal - and removed from the inbox
/// by its id once it has been - so two answers handed in the same second are two entries, each removed alone, and an
/// entry the run's records hold is only removed by a session that finds it still there, never weighed again.
/// </summary>
/// <param name="Id">What tells this entry from every other the inbox ever held: given by the command that handed it over.</param>
/// <param name="Question">The question's id.</param>
/// <param name="Answer">The answer, spelled as the question's options spell it.</param>
/// <param name="At">The moment the command took it.</param>
/// <param name="Text">The text given with the answer, where it takes one.</param>
/// <param name="Again">Whether it changes the last answer a person gave.</param>
public sealed record FlowInboxAnswer(
    string Id,
    string Question,
    string Answer,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    string? Text = null,
    bool Again = false)
{
    /// <summary>A new entry's id: random, so two commands handing answers at once can never give one id twice.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Id) ? "it names no id"
            : !FlowQuestionIds.IsWellFormed(Question) ? $"'{Question}' is not a question's id"
            : string.IsNullOrWhiteSpace(Answer) ? "its answer is empty"
            : null;
}

/// <summary>
/// One amendment of the request on its way to an engine that is running the run, in <c>inbox.json</c>: taken by the
/// engine as <c>amend-orchestrator-request</c> takes one given while no engine runs - weighed against the same gates,
/// and what became of it written into <c>updates.json</c> - and removed from the inbox by its id once it has been.
/// </summary>
/// <param name="Id">What tells this entry from every other the inbox ever held: given by the command that handed it over.</param>
/// <param name="Text">The amendment, in the person's words: it adds to the request, corrects it or narrows it.</param>
/// <param name="At">The moment the command took it: the moment its run's answers are held to, if one changes while it waits.</param>
/// <param name="Through">How the person gave it: <c>amend-orchestrator-request</c> or <c>front-end</c>.</param>
public sealed record FlowInboxAmendment(
    string Id,
    string Text,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    string? Through = null)
{
    /// <summary>A new entry's id: random, so two commands handing changes at once can never give one id twice.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Id) ? "it names no id"
            : string.IsNullOrWhiteSpace(Text) ? "its amendment is empty"
            : null;
}

/// <summary>
/// <c>inbox.json</c>: what a person gave from another terminal while the engine runs - here an answer, a changed
/// answer and an amendment being theirs to carry - written under the store's lock, read as <c>stop.json</c> is, and
/// emptied of each thing once the engine has written it where it belongs. What a session ends without taking stays for
/// the next session of the run, which takes it before anything else runs.
/// </summary>
/// <param name="Schema">The file's schema, 1.</param>
/// <param name="Answers">The answers on their way to the engine, in the order given.</param>
/// <param name="Amendments">The amendments of the request on their way to the engine, in the order given.</param>
public sealed record FlowInboxFile(int Schema, IReadOnlyList<FlowInboxAnswer> Answers, IReadOnlyList<FlowInboxAmendment>? Amendments = null)
{
    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : Answers.Any(answer => answer.Problem() is { } problem) ? $"its answer '{Answers.First(answer => answer.Problem() is not null).Question}': {Answers.First(answer => answer.Problem() is not null).Problem()}"
            : (Amendments ?? []).Any(amendment => amendment.Problem() is { } problem) ? $"its amendment: {(Amendments ?? []).First(amendment => amendment.Problem() is not null).Problem()}"
            : Answers.GroupBy(answer => answer.Id, StringComparer.Ordinal).Any(group => group.Count() > 1) ? "one id holds two answers"
            : (Amendments ?? []).GroupBy(amendment => amendment.Id, StringComparer.Ordinal).Any(group => group.Count() > 1) ? "one id holds two amendments"
            : null;
}

/// <summary>What became of an answer handed over to the run (<see cref="FlowCoordinator.AnswerHandedOver"/>).</summary>
public enum FlowHandedOver
{
    /// <summary>The run took it: written into <c>questions.json</c>, under its inbox entry's id where one handed it over, and carried out.</summary>
    Taken,

    /// <summary>The run did not take it: the refusal written into <c>questions.json</c> under its inbox entry's id where one handed it over, and told as an <c>answer</c> event naming why.</summary>
    Refused,

    /// <summary>The run's records already hold what it did with the inbox entry it came under - taken or refused before - so it was not weighed again, and nothing was written.</summary>
    RecordedAlready,
}
