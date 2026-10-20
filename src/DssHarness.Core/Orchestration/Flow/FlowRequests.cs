using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>A safe pause another process asked of the running engine: whether it stops the machine steps too.</summary>
/// <param name="All">Whether the machine steps in progress are stopped too.</param>
/// <param name="RequestedAt">When the request was made.</param>
public sealed record FlowStopPause(bool All, [property: JsonConverter(typeof(FlowIsoTimeConverter))] DateTimeOffset RequestedAt);

/// <summary>A stop another process asked of the running engine: the hard stop.</summary>
/// <param name="RequestedAt">When the request was made.</param>
public sealed record FlowStopStop([property: JsonConverter(typeof(FlowIsoTimeConverter))] DateTimeOffset RequestedAt);

/// <summary>
/// <c>stop.json</c>: what another process asked of the running engine - a safe pause (and whether it stops the
/// machine steps too), or a stop. Written by <c>pause-orchestrator</c> and <c>stop-orchestrator</c> under the store's
/// lock, read by the engine every two seconds, and taken - read and removed - once the engine has applied what it
/// asks. One request at a time: exactly one of a pause and a stop.
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
/// One answer on its way to an engine that is running the run, in <c>inbox.json</c>: taken by the engine, written
/// into <c>questions.json</c>, and removed from the inbox once it has been.
/// </summary>
/// <param name="Question">The question's id.</param>
/// <param name="Answer">The answer, spelled as the question's options spell it.</param>
/// <param name="Text">The text given with the answer, where it takes one.</param>
/// <param name="At">The moment the command took it.</param>
/// <param name="Again">Whether it changes the last answer a person gave.</param>
public sealed record FlowInboxAnswer(
    string Question,
    string Answer,
    [property: JsonConverter(typeof(FlowIsoTimeConverter))] DateTimeOffset At,
    string? Text = null,
    bool Again = false)
{
    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => !FlowQuestionIds.IsWellFormed(Question) ? $"'{Question}' is not a question's id"
            : string.IsNullOrWhiteSpace(Answer) ? "its answer is empty"
            : null;
}

/// <summary>
/// <c>inbox.json</c>: what a person gave from another terminal while the engine runs - here an answer, a changed
/// answer and an amendment being theirs to carry - written under the store's lock, read as <c>stop.json</c> is, and
/// emptied of each thing once the engine has written it where it belongs.
/// </summary>
/// <param name="Schema">The file's schema, 1.</param>
/// <param name="Answers">The answers on their way to the engine, in the order given.</param>
public sealed record FlowInboxFile(int Schema, IReadOnlyList<FlowInboxAnswer> Answers)
{
    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : Answers.Any(answer => answer.Problem() is { } problem) ? $"its answer '{Answers.First(answer => answer.Problem() is not null).Question}': {Answers.First(answer => answer.Problem() is not null).Problem()}"
            : null;
}
