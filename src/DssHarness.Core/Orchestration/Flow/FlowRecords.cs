using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// How every time a run's records hold is written and read - <c>flow.json</c>, each <c>step.json</c>,
/// <c>workflow.json</c>, <c>pre-existing-issues.json</c>, the events, <c>questions.json</c>, <c>stop.json</c>,
/// <c>inbox.json</c>, the owner and the report: UTC, <c>yyyy-MM-dd HH:mm:ss</c> (the user, 2026-10-17: "sql datetime"),
/// so a client parses one form. A time a record keeps that has not come yet is written <see langword="null"/>, never
/// left out, so every entry has the same members: each such member says so with
/// <see cref="JsonIgnoreCondition.Never"/>, which the state files' own rule of leaving nulls out would otherwise drop.
/// </summary>
public sealed class FlowTimeConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>The one form a time is written in.</summary>
    public const string Form = "yyyy-MM-dd HH:mm:ss";

    /// <summary>A time in the one form, as a document that writes it by hand (the report's) writes it.</summary>
    /// <param name="at">The time.</param>
    public static string Written(DateTimeOffset at) => at.ToUniversalTime().ToString(Form, CultureInfo.InvariantCulture);

    /// <summary>Reads a time as it is written; anything else refuses, for a record is never guessed at.</summary>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            && DateTimeOffset.TryParseExact(reader.GetString(), Form, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : throw new JsonException($"a time is written '{Form}' (UTC)");

    /// <summary>Writes a time in the one form.</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(Written(value));
}

/// <summary>
/// How a run's records spell the engine's own values - an instance's state, the run's status, an event's kind: each
/// list owned once, by the engine's enumeration, and a record's member held to exactly one of its names. Never a
/// number, never two names joined, never a name the engine does not have - which <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/>
/// would all let through - so a record that says something the engine cannot mean is refused, not guessed at.
/// </summary>
public static class FlowRecordNames
{
    /// <summary>Whether <paramref name="name"/> is exactly the name of one of <typeparamref name="T"/>'s values.</summary>
    /// <typeparam name="T">The engine's enumeration that owns the list.</typeparam>
    /// <param name="name">The name a record holds.</param>
    public static bool Is<T>(string? name)
        where T : struct, Enum
        => name is not null && Enum.GetNames<T>().Contains(name, StringComparer.Ordinal);

    /// <summary>The value <paramref name="name"/> names; refused where it is not exactly one of <typeparamref name="T"/>'s names.</summary>
    /// <typeparam name="T">The engine's enumeration that owns the list.</typeparam>
    /// <param name="name">The name a record holds, read strictly already.</param>
    /// <exception cref="InvalidOperationException"><paramref name="name"/> is not one of the names.</exception>
    public static T Of<T>(string name)
        where T : struct, Enum
        => Is<T>(name)
            ? Enum.Parse<T>(name)
            : throw new InvalidOperationException($"'{name}' is not one of the {typeof(T).Name} names a run's records hold: {string.Join(", ", Enum.GetNames<T>())}.");
}

/// <summary>
/// One event of a run, as <c>events.jsonl</c> holds it: what changed, whole, one JSON object to a line, append-only.
/// History, never truth - the state files are the truth, and they are written before the event that tells of them.
/// </summary>
/// <param name="Seq">The event's number in the run, from 1.</param>
/// <param name="At">When it happened.</param>
/// <param name="Kind">What kind of event it is: one of <see cref="FlowChangeKind"/>'s names in camelCase (<see cref="KindOf"/>).</param>
/// <param name="Instance">The step instance it tells of; null for the run's own events.</param>
/// <param name="Step">The instance's declaration path; null where no instance is named.</param>
/// <param name="From">The state the instance stood in before it; null where none applies.</param>
/// <param name="To">The state the instance stands in now; null where none applies.</param>
/// <param name="Attempt">Which attempt of the instance's visit the event tells of; null for the run's own events.</param>
/// <param name="Detail">What the event says in words, where the kinds alone cannot.</param>
public sealed record FlowEvent(
    int Seq,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    string Kind,
    string? Instance = null,
    string? Step = null,
    string? From = null,
    string? To = null,
    int? Attempt = null,
    string? Detail = null)
{
    /// <summary>Every kind of event a run appends: one for each kind of change the coordinator hands the records, and no other.</summary>
    public static readonly IReadOnlyList<string> Kinds = [.. Enum.GetValues<FlowChangeKind>().Select(KindOf)];

    /// <summary>The kind an event of <paramref name="change"/> is written as: its name in camelCase.</summary>
    /// <param name="change">What changed.</param>
    public static string KindOf(FlowChangeKind change)
        => Enum.IsDefined(change)
            ? JsonNamingPolicy.CamelCase.ConvertName(change.ToString())
            : throw new ArgumentOutOfRangeException(nameof(change), change, "not a change a run records");

    /// <summary>What is wrong with this event; null when nothing is.</summary>
    public string? Problem()
        => Seq < 1 ? $"its seq is {Seq.ToString(CultureInfo.InvariantCulture)}, and the first event of a run is 1"
            : !Kinds.Contains(Kind, StringComparer.Ordinal) ? $"'{Kind}' is not an event a run writes"
            : !FlowInstanceId.IsWellFormed(Instance ?? "step") && Instance is not null ? $"'{Instance}' is not an instance id"
            : null;
}

/// <summary>
/// A step's own record, <c>step.json</c> in its directory - the run's or an agent's, as the rule places it: the full
/// record of that step, written first, so after a crash it can differ from the index only by being ahead of it.
/// </summary>
/// <param name="Instance">The instance's id, as <see cref="FlowInstanceId"/> spells it.</param>
/// <param name="Step">The declaration's path among the flow.</param>
/// <param name="Group">Whether this is a group's own record - of an expansion or of an iteration.</param>
/// <param name="State">How the instance stands: one of <see cref="FlowInstanceState"/>'s names, exactly.</param>
/// <param name="Attempt">Which attempt of this visit has run: 1 for the first, one more for each retry.</param>
/// <param name="Visit">Which visit of the step this is.</param>
/// <param name="Answers">The answers the questions of this visit were given, in the order they were given.</param>
/// <param name="WaitsOn">What the instance waits on, where it waits: a dependency, a claim, a counter, a question, the resume.</param>
/// <param name="Type">The step's type; null for a group's own record.</param>
/// <param name="StartedAt">When this attempt began; written null until it has.</param>
/// <param name="FinishedAt">When the instance ended; written null until it has.</param>
/// <param name="Exit">The code the step's own work ended with; null until it has.</param>
/// <param name="Error">Why the step ended as it did, where the code alone cannot say.</param>
/// <param name="SkippedBecause">Why the flow decided not to run it, where it is Skipped.</param>
/// <param name="NotRunBecause">Why the run never reached it, where it is NotRun.</param>
/// <param name="Failure">
/// Where a failure left the run (<see cref="FlowFailure"/>): <c>passedOver</c> (the run went on over it), <c>endedRun</c>
/// (it ended the session failed, and the next session runs it again unless an answer <c>fail</c> ended it),
/// <c>owed</c> (it ended while a pause recorded what it left running, and its handlers and policy are the next
/// session's to make) or <c>takenByGroup</c> (its group took it); null for every other ending.
/// </param>
/// <param name="EndedByQuestion">The kind of the question whose <c>fail</c> answer ended this step, where one did.</param>
/// <param name="Agent">The agent the step works in - as its <c>in</c> or <c>of</c> names, made by its create-agent - where it names one.</param>
/// <param name="Made">
/// The agent a create-agent step makes: named by the engine and written here, with <paramref name="Model"/>, before
/// anything makes it, so the step run again asks for the same agent; null for every other step, and for one whose name
/// is spent.
/// </param>
/// <param name="Question">The id of the question the step asks and no answer has come for; null where none is open.</param>
/// <param name="Handoff">The handoff a stop left, where one was written: who wrote it and why.</param>
/// <param name="Output">The step's output, as later steps read it; null where it wrote none.</param>
/// <param name="Item">The work item of the <c>forEach</c> this instance is one of; null outside every one.</param>
/// <param name="Index">The item's index, from 0; null outside every <c>forEach</c>.</param>
/// <param name="Iteration">The iteration of the <c>repeat</c> this instance is one of, from 1; null outside every one.</param>
/// <param name="Handlers">What the ending of this step owes and owes no longer: each handler declared beside it, and where it stands - <c>owed</c>, <c>raised</c>, <c>done</c> or <c>passed</c>.</param>
/// <param name="HandledBy">The handler that took this step's failure, where one did: its name; the question whose answer accepted its ending at a bound, where it did that.</param>
/// <param name="Leftover">What a repeat that ended with its until still false says it left, as its leftover expression read it; null where the loop names none.</param>
/// <param name="RaisedBy">The instance whose ending raised this one - a handler's source, the step whose failure a recovery took; null for every step the schedule starts.</param>
/// <param name="Direction">The fix a person gave for the failure this instance's recovery works on, where recoveryBound was answered direct; null on every other visit.</param>
/// <param name="NotRunCause">
/// Which kind of reason the step is NotRun for, one of <see cref="FlowNotRunCause"/>'s names exactly: a resume runs one
/// NotRun because the run ended before it (<c>RunEnded</c>), and one whose failed dependency runs again, and leaves one a
/// standing failure or its group's failure blocks; null for every other state.
/// </param>
/// <param name="Issue">The pre-existing issue this instance was raised to triage, whole, as the reporting step reported it; null for every step the schedule starts.</param>
/// <param name="IssuesOwed">
/// The pre-existing issues this step reported whose triage is not raised yet - a pause or a stop was under way, or the
/// record was written before the triage was - in the order reported; the next session raises them. Null where none is owed.
/// </param>
/// <param name="LeftBy">
/// The handler whose redirect sent the run back past this visit, by its instance id: a resume draws the step's next visit
/// where this one is still its newest. Null where no redirect left it.
/// </param>
/// <param name="Cause">
/// What its code alone cannot say of its ending, one of <see cref="OutcomeCause"/>'s names exactly: <c>LockHeld</c> for a
/// refusal whose only obstacle was a lock another run held, which the retry table runs again and a run that ended on it
/// counts as incomplete (21). Null for every other ending.
/// </param>
/// <param name="Model">The model the agent a create-agent step makes is made for, written with <paramref name="Made"/>; null where that is.</param>
/// <param name="Copied">
/// A leg step's: true once every leg of its attempt that works in a copy has it, and every one in the tree itself has
/// ended, so the tree is let go while the legs work on; null until then, and for every other step.
/// </param>
/// <param name="Targets">
/// A refresh's or a rebase's of <c>others</c>: each target, in the order of their names, with where it stands, how many
/// attempts its turn ran and how it ended - what a resume goes on from, at the first whose turn has not ended; null for
/// every other step, and before it starts.
/// </param>
public sealed record FlowStepRecord(
    string Instance,
    string Step,
    bool Group,
    string State,
    int Attempt,
    int Visit,
    IReadOnlyList<FlowAnswerGiven> Answers,
    IReadOnlyList<string> WaitsOn,
    string? Type = null,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartedAt = null,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FinishedAt = null,
    int? Exit = null,
    string? Error = null,
    string? SkippedBecause = null,
    string? NotRunBecause = null,
    string? Failure = null,
    string? EndedByQuestion = null,
    string? Agent = null,
    string? Made = null,
    string? Question = null,
    FlowHandoffRecord? Handoff = null,
    JsonNode? Output = null,
    JsonNode? Item = null,
    int? Index = null,
    int? Iteration = null,
    IReadOnlyList<FlowHandlerState>? Handlers = null,
    string? HandledBy = null,
    JsonNode? Leftover = null,
    string? RaisedBy = null,
    string? Direction = null,
    string? NotRunCause = null,
    FlowPreExistingReport? Issue = null,
    IReadOnlyList<FlowPreExistingReport>? IssuesOwed = null,
    string? LeftBy = null,
    string? Cause = null,
    string? Model = null,
    bool? Copied = null,
    IReadOnlyList<FlowTargetRecord>? Targets = null)
{
    /// <summary>Where a failure can leave the run, as the record names each of <see cref="FlowFailure"/>'s values.</summary>
    public static readonly IReadOnlyList<string> Failures = [.. Enum.GetValues<FlowFailure>().Select(NameOf)];

    /// <summary>How the record names <paramref name="failure"/>: its name in camelCase, <c>passedOver</c>, <c>endedRun</c>, <c>owed</c>, <c>takenByGroup</c>.</summary>
    /// <param name="failure">Where the failure left the run.</param>
    public static string NameOf(FlowFailure failure) => JsonNamingPolicy.CamelCase.ConvertName(failure.ToString());

    /// <summary>Where this record's failure left the run, read strictly; null where it names none.</summary>
    /// <exception cref="InvalidOperationException">The record names a failure that is none of <see cref="Failures"/>.</exception>
    public FlowFailure? FailureOf()
        => Failure is null ? null
            : Enum.GetValues<FlowFailure>().Where(value => string.Equals(NameOf(value), Failure, StringComparison.Ordinal)).Cast<FlowFailure?>().FirstOrDefault()
                ?? throw new InvalidOperationException($"'{Failure}' is not where a failure leaves the run: {string.Join(", ", Failures)}.");

    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => !FlowInstanceId.IsWellFormed(Instance) ? $"'{Instance}' is not an instance id"
            : string.IsNullOrWhiteSpace(Step) ? "it names no step"
            : !FlowRecordNames.Is<FlowInstanceState>(State) ? $"'{State}' is not an instance state"
            : Attempt < 1 ? $"its attempt is {Attempt.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : Visit < 1 ? $"its visit is {Visit.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : Failure is { } failure && !Failures.Contains(failure, StringComparer.Ordinal) ? $"'{failure}' is not where a failure leaves the run"
            : NotRunCause is { } cause && !FlowRecordNames.Is<FlowNotRunCause>(cause) ? $"'{cause}' is not a reason a step is NotRun for"
            : Cause is { } outcome && (!FlowRecordNames.Is<OutcomeCause>(outcome) || outcome == nameof(OutcomeCause.None)) ? $"'{outcome}' is not a cause an ending is recorded with"
            : Question is not null && !FlowQuestionIds.IsWellFormed(Question) ? $"'{Question}' is not a question's id"
            : Item is not null && Index is null ? "it holds an item without its index"
            : Answers.Any(answer => string.IsNullOrWhiteSpace(answer.Question) || string.IsNullOrWhiteSpace(answer.Answer)) ? "an answer names neither its question nor its answer"
            : Handlers is { Count: > 0 } && Handlers.Any(handler => handler.Problem() is not null) ? $"its handlers hold one that is not a state: {Handlers.First(handler => handler.Problem() is not null).Problem()}"
            : HandledBy is not null && !FlowInstanceId.IsWellFormed(HandledBy) && !FlowQuestionIds.IsWellFormed(HandledBy) ? $"'{HandledBy}' names neither an instance nor a question"
            : RaisedBy is not null && !FlowInstanceId.IsWellFormed(RaisedBy) ? $"'{RaisedBy}' is not an instance id"
            : LeftBy is not null && !FlowInstanceId.IsWellFormed(LeftBy) ? $"'{LeftBy}' is not an instance id"
            : Handoff?.Problem() is { } handoff ? $"its handoff: {handoff}"
            : Targets?.FirstOrDefault(target => target.Problem() is not null) is { } target ? $"its target '{target.Agent}': {target.Problem()}"
            : Targets?.GroupBy(target => target.Agent, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice ? $"it names target '{twice.Key}' twice"
            : null;
}

/// <summary>The handoff a step's record names: who wrote it and why, for the file is its own record beside the step's.</summary>
/// <param name="By">Who wrote it: <c>agent</c> or <c>engine</c>.</param>
/// <param name="Why">Why the engine wrote it, where it did; null where the agent answered.</param>
public sealed record FlowHandoffRecord(string By, string? Why = null)
{
    /// <summary>
    /// How a handoff the engine wrote begins, its first line saying so (part 04: "says so in its first line"): the one
    /// spelling the engine writes and a resume reads back, where a crash left a handoff's file beside a record that does
    /// not name it yet.
    /// </summary>
    public const string EngineMark = "Written by the engine: ";

    /// <summary>
    /// The record of a handoff whose text is <paramref name="text"/>, read from its first line: the engine's where the
    /// line carries <see cref="EngineMark"/>, why being the rest of it - as <see cref="FlowHandoff.ByEngine"/> writes it -
    /// and the agent's otherwise.
    /// </summary>
    /// <param name="text">The handoff's text, whole.</param>
    public static FlowHandoffRecord Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var first = text.Split('\n', 2)[0].TrimEnd('\r');

        return first.StartsWith(EngineMark, StringComparison.Ordinal)
            ? new FlowHandoffRecord("engine", first[EngineMark.Length..].TrimEnd('.'))
            : new FlowHandoffRecord("agent");
    }

    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => By is not ("agent" or "engine") ? $"'{By}' is not who writes a handoff"
            : By == "engine" && string.IsNullOrWhiteSpace(Why) ? "a handoff the engine wrote says why"
            : null;
}

/// <summary>One handler of a step's ending, as the source's own record names where it stands.</summary>
/// <param name="Handler">The handler's name, as its declaration names it.</param>
/// <param name="State">
/// Where it stands: <c>owed</c> (the ending is written, the raise is not made yet), <c>raised</c>, <c>done</c>,
/// <c>passed</c>, or <c>left</c> (a redirect left its visit before it ended: it took nothing, and the ending it was raised
/// for is abandoned - settled by none of its handlers, and never rebuilt by a resume).
/// </param>
public sealed record FlowHandlerState(string Handler, string State)
{
    /// <summary>Every state a handler of an ending can stand in, as the record spells them.</summary>
    public static readonly IReadOnlyList<string> States = ["owed", "raised", "done", "passed", "left"];

    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Handler) ? "it names no handler"
            : !States.Contains(State, StringComparer.Ordinal) ? $"'{State}' is not where a handler of an ending stands"
            : null;
}

/// <summary>A redirect the run owes and has not made yet, as <c>flow.json</c> holds it: written before the first reset, made once.</summary>
/// <param name="Handler">The handler that named it, by its instance id.</param>
/// <param name="Target">The step the run goes to, by its name.</param>
/// <param name="Steps">
/// What the redirect resets, in the order it was computed: each by the id of its visit now - or, for one not drawn yet,
/// by the id its first visit takes.
/// </param>
/// <param name="Question">The question it asks before it goes, where it would leave an agent open and none answered yet; null otherwise.</param>
public sealed record FlowRedirectRecord(string Handler, string Target, IReadOnlyList<string> Steps, string? Question = null)
{
    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => !FlowInstanceId.IsWellFormed(Handler) ? $"'{Handler}' is not an instance id"
            : string.IsNullOrWhiteSpace(Target) ? "it names no step to go to"
            : Steps.Any(id => !FlowInstanceId.IsWellFormed(id)) ? "it resets an instance its id does not name"
            : Question is not null && !FlowQuestionIds.IsWellFormed(Question) ? $"'{Question}' is not a question's id"
            : null;
}

/// <summary>
/// The run's state in one file, <c>flow.json</c>: every step instance with its state, and under <c>next</c> the steps
/// that would start now, in the order they would start. A resume, <c>list-orchestrator</c> and a front end read this
/// one file and work nothing out again.
/// </summary>
/// <param name="Schema">The file's schema; 1.</param>
/// <param name="Run">The run's name.</param>
/// <param name="Definition">The definition's name.</param>
/// <param name="DefinitionDigest">The SHA-256 of the definition as it ran, frozen in <c>definition.json</c>.</param>
/// <param name="Inputs">The values the run was started with, by input name.</param>
/// <param name="Status">How the run stands: one of <see cref="FlowRunStatus"/>'s names, exactly.</param>
/// <param name="Spent">What the run's reasoning has spent against its budget: usd, tokens and minutes.</param>
/// <param name="BudgetGranted">How many times the budget was answered <c>more</c>.</param>
/// <param name="Steps">Every step instance, in the order the run made them.</param>
/// <param name="Next">The steps that would start now, in the order they would start.</param>
/// <param name="StartedAt">When the run began: what a resume carries on with, for <c>workflow.json</c> is written for a person and never read by one.</param>
/// <param name="StartCommit">The commit the run started from; null where it started beside the tree's history.</param>
/// <param name="Branch">The branch the run started on.</param>
/// <param name="Pause">The safe pause the run is under or was ended by: cause, all, at, completedAt; null where none.</param>
/// <param name="Redirects">
/// Every redirect the run owes and has not made yet, in the order they were decided - each made in its turn - each
/// written before its first reset; null where none is owed.
/// </param>
public sealed record FlowFile(
    int Schema,
    string Run,
    string Definition,
    string DefinitionDigest,
    IReadOnlyDictionary<string, string> Inputs,
    string Status,
    FlowBudgetRecord Spent,
    int BudgetGranted,
    IReadOnlyList<FlowStepRecord> Steps,
    IReadOnlyList<string> Next,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset StartedAt,
    string? StartCommit = null,
    string? Branch = null,
    FlowPauseRecord? Pause = null,
    IReadOnlyList<FlowRedirectRecord>? Redirects = null)
{
    /// <summary>
    /// The steps the file holds running that are no group's own record, by instance: once a pause is complete, the
    /// machine steps it left to finish, which the engine stays to record - the one rule <c>pause-orchestrator</c> and
    /// <c>list-orchestrator</c> both read.
    /// </summary>
    public IReadOnlyList<string> MachineStepsRunning()
        => [.. Steps.Where(step => !step.Group && step.State == nameof(FlowInstanceState.Running)).Select(step => step.Instance)];

    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : string.IsNullOrWhiteSpace(Run) ? "it names no run"
            : string.IsNullOrWhiteSpace(Definition) ? "it names no definition"
            : string.IsNullOrWhiteSpace(DefinitionDigest) ? "it names no digest of its definition"
            : !FlowRecordNames.Is<FlowRunStatus>(Status) ? $"'{Status}' is not a run status"
            : BudgetGranted < 0 ? "the budget was granted more a negative number of times"
            : Steps.Any(step => step.Problem() is { } problem) ? $"its steps hold one that is not a record: {Steps.First(step => step.Problem() is not null).Problem()}"
            : Steps.GroupBy(step => step.Instance, StringComparer.Ordinal).Any(group => group.Count() > 1) ? "it holds an instance twice"
            : Next.Any(id => Steps.All(step => !string.Equals(step.Instance, id, StringComparison.Ordinal))) ? "next names an instance the file does not hold"
            : Pause?.Problem() is { } pause ? $"its pause: {pause}"
            : Redirects?.Select(redirect => redirect.Problem()).FirstOrDefault(problem => problem is not null) is { } redirect ? $"its redirects: {redirect}"
            : null;
}

/// <summary>What a run's reasoning spent, as <c>flow.json</c> holds it.</summary>
/// <param name="Usd">What the reason steps cost, in United States dollars.</param>
/// <param name="Tokens">Every token the clients reported.</param>
/// <param name="Minutes">The time reason steps ran, in minutes: two that reason at once count twice.</param>
public sealed record FlowBudgetRecord(double Usd, long Tokens, double Minutes);

/// <summary>The safe pause a run is under or was ended by, as <c>flow.json</c> holds it.</summary>
/// <param name="Cause">Who asked for the pause.</param>
/// <param name="All">Whether the machine steps were stopped too.</param>
/// <param name="At">When the pause began.</param>
/// <param name="CompletedAt">When the pause completed - its last handoff written and everything it left running recorded; written null while it records.</param>
/// <param name="Handoffs">Every step that handed off, in the order the handoffs were written.</param>
public sealed record FlowPauseRecord(
    string Cause,
    bool All,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    IReadOnlyList<string> Handoffs,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CompletedAt = null)
{
    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => !FlowPauseRequest.Causes.Contains(Cause, StringComparer.Ordinal) ? $"'{Cause}' is not who asks for a pause"
            : Handoffs.Any(id => !FlowInstanceId.IsWellFormed(id)) ? "a handoff names no instance"
            : null;
}

/// <summary>
/// The run's status, <c>workflow.json</c>: for a person and a client to read - every step of the definition with its
/// status, times and instances, the run's own, and the progress of it. It says nothing a resume reads and holds no
/// prompt, no output, no path outside the run and no secret.
/// </summary>
/// <param name="Schema">The file's schema; 1.</param>
/// <param name="Run">The run's name.</param>
/// <param name="Workflow">The workflow's name.</param>
/// <param name="Status">How the run stands: one of <see cref="FlowRunStatus"/>'s names, exactly, as a client switches on them.</param>
/// <param name="StartedAt">When the run began.</param>
/// <param name="UpdatedAt">When this status was written.</param>
/// <param name="Progress">Every instance of the run by its state, and their total.</param>
/// <param name="Steps">Every step of the definition, by its path, from the moment the run is created.</param>
/// <param name="FinishedAt">When the run ended; written null while it runs.</param>
/// <param name="WaitingOn">What the run waits on: the open questions' ids, or the safe pause; written null where it waits on nothing.</param>
public sealed record WorkflowStatus(
    int Schema,
    string Run,
    string Workflow,
    string Status,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset StartedAt,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset UpdatedAt,
    WorkflowProgress Progress,
    IReadOnlyDictionary<string, WorkflowStepStatus> Steps,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FinishedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? WaitingOn = null)
{
    /// <summary>What is wrong with this status; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : string.IsNullOrWhiteSpace(Run) ? "it names no run"
            : string.IsNullOrWhiteSpace(Workflow) ? "it names no workflow"
            : !FlowRecordNames.Is<FlowRunStatus>(Status) ? $"'{Status}' is not a run status"
            : Progress.Problem() is { } progress ? progress
            : Steps.Values.Any(step => step.Problem() is { } problem) ? Steps.Values.First(step => step.Problem() is not null).Problem()
            : null;
}

/// <summary>The run's pre-existing issues by what became of them, as <c>workflow.json</c> counts them.</summary>
/// <param name="Listed">How many the triage answered <c>list</c>: proven, and in the run's own file.</param>
/// <param name="Fixed">How many a recovery fixed at their root.</param>
/// <param name="Duplicate">How many the list already held.</param>
/// <param name="Unproven">How many no one could reproduce on the base commit: never shown to a person as a fact.</param>
public sealed record WorkflowPreExisting(int Listed, int Fixed, int Duplicate, int Unproven);

/// <summary>Every instance of the run by its state, and their total, as <c>workflow.json</c> holds it.</summary>
/// <param name="Steps">How many instances the run has made.</param>
/// <param name="NotStarted">How many have not begun.</param>
/// <param name="Running">How many are running, or between two attempts of one visit.</param>
/// <param name="Recovering">How many failed instances the recovery works on now: each whose failure a recovery visit took that has not ended.</param>
/// <param name="Paused">How many wait on a question.</param>
/// <param name="Done">How many ended as asked.</param>
/// <param name="Failed">How many ended failed.</param>
/// <param name="Skipped">How many the flow decided not to run.</param>
/// <param name="Stopped">How many a stop ended where they stood.</param>
/// <param name="NotRun">How many were never reached.</param>
/// <param name="PreExisting">The run's pre-existing issues by what became of them; null until it has one.</param>
public sealed record WorkflowProgress(
    int Steps,
    int NotStarted,
    int Running,
    int Recovering,
    int Paused,
    int Done,
    int Failed,
    int Skipped,
    int Stopped,
    int NotRun,
    WorkflowPreExisting? PreExisting = null)
{
    /// <summary>What is wrong with this progress; null when nothing is.</summary>
    public string? Problem()
        => Steps < 0 || NotStarted < 0 || Running < 0 || Recovering < 0 || Paused < 0 || Done < 0 || Failed < 0 || Skipped < 0 || Stopped < 0 || NotRun < 0
            ? "a count of instances is negative"
            : null;
}

/// <summary>One step of the definition in <c>workflow.json</c>: its own status, times and seconds - its instances' together - and each instance under its id.</summary>
/// <param name="Status">The status the run would show for the step: one of <see cref="StepStatuses"/>, exactly.</param>
/// <param name="Instances">Each instance of the step, by its id.</param>
/// <param name="StartedAt">Its instances' first start; written null until one has.</param>
/// <param name="FinishedAt">Its instances' last finish; written null until every instance has finished.</param>
/// <param name="Seconds">What the step's instances took together; written null until every instance has finished.</param>
public sealed record WorkflowStepStatus(
    string Status,
    IReadOnlyDictionary<string, WorkflowInstanceStatus> Instances,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartedAt = null,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FinishedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Seconds = null)
{
    /// <summary>The status of a failed instance the recovery works on now: the one status a client sees that is no instance state of the engine's.</summary>
    public const string Recovering = "Recovering";

    /// <summary>The step statuses, as a client switches on them: every instance state of the engine's (<see cref="FlowInstanceState"/>), and <see cref="Recovering"/>.</summary>
    public static readonly IReadOnlyList<string> StepStatuses = [.. Enum.GetNames<FlowInstanceState>(), Recovering];

    /// <summary>What is wrong with this status; null when nothing is.</summary>
    public string? Problem()
        => !StepStatuses.Contains(Status, StringComparer.Ordinal) ? $"'{Status}' is not a step status"
            : Instances.Values.Any(instance => instance.Problem() is { } problem) ? Instances.Values.First(instance => instance.Problem() is not null).Problem()
            : null;
}

/// <summary>One instance of a step in <c>workflow.json</c>.</summary>
/// <param name="Status">How the instance stands: one of <see cref="WorkflowStepStatus.StepStatuses"/>, exactly.</param>
/// <param name="Visit">Which visit of the step this is.</param>
/// <param name="Attempt">Which attempt of this visit has run.</param>
/// <param name="Recoveries">How many times a recovery was given its failure.</param>
/// <param name="StartedAt">When this instance began; written null until it has.</param>
/// <param name="FinishedAt">When this instance ended; written null until it has.</param>
/// <param name="Seconds">What the instance took; written null until it finished.</param>
/// <param name="Recovery">The recovery it is under, where one holds it; null otherwise.</param>
public sealed record WorkflowInstanceStatus(
    string Status,
    int Visit,
    int Attempt,
    int Recoveries,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartedAt = null,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FinishedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Seconds = null,
    WorkflowRecovery? Recovery = null)
{
    /// <summary>What is wrong with this status; null when nothing is.</summary>
    public string? Problem()
        => !WorkflowStepStatus.StepStatuses.Contains(Status, StringComparer.Ordinal) ? $"'{Status}' is not a step status"
            : Visit < 1 || Attempt < 1 || Recoveries < 0 ? "a visit, an attempt or a recovery is counted from nowhere"
            : null;
}

/// <summary>
/// The recovery an instance is under, as <c>workflow.json</c> holds it: the visit the recovery is on and the most it is
/// given. Written for each instance whose status is <see cref="WorkflowStepStatus.Recovering"/>.
/// </summary>
/// <param name="Visit">Which visit of the recovery takes the instance's failure now.</param>
/// <param name="MaxVisits">The most visits the recovery is given of one step in one scope, as the definition bounds it; answers of <c>more</c> to its bound add to it.</param>
/// <param name="Course">What the recovery works on it as: <c>fix</c>, its failure put right at its root - the one course a recovery takes.</param>
public sealed record WorkflowRecovery(int Visit, int MaxVisits, string Course);

/// <summary>
/// The run's list of issues it found and left, <c>pre-existing-issues.json</c>: each proven on the base commit, with a
/// title and a description, so it is shown to the user in the end. Created empty with the run; an entry added at each
/// triage answer <c>list</c>.
/// </summary>
/// <param name="Schema">The file's schema; 1.</param>
/// <param name="Run">The run's name.</param>
/// <param name="UpdatedAt">When this list was written.</param>
/// <param name="Issues">Each issue found, in the order they were added.</param>
public sealed record PreExistingIssuesFile(
    int Schema,
    string Run,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset UpdatedAt,
    IReadOnlyList<PreExistingIssue> Issues)
{
    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : string.IsNullOrWhiteSpace(Run) ? "it names no run"
            : Issues.Any(issue => issue.Problem() is { } problem) ? Issues.First(issue => issue.Problem() is not null).Problem()
            : Issues.Select((issue, at) => (issue, at)).Any(pair => !string.Equals(pair.issue.Id, $"pe-{(pair.at + 1).ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal))
                ? "ids are pe-<n>, in the order added"
            : null;
}

/// <summary>One issue the run found and left, with the proof of it.</summary>
/// <param name="Id">The issue's id: <c>pe-&lt;n&gt;</c>, in the order added.</param>
/// <param name="Title">What it is, in a line.</param>
/// <param name="Description">What it is, in the words the triage gave.</param>
/// <param name="Where">The files it is in, as the repository spells them.</param>
/// <param name="Proof">What the triage read or ran on the base commit - never what the reporting agent said.</param>
/// <param name="Why">Which of the three tests it passed none of: not related to the work, not critical, and in no step's way.</param>
/// <param name="FoundBy">The instance that found it.</param>
/// <param name="FoundAt">When it was found.</param>
public sealed record PreExistingIssue(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> Where,
    PreExistingProof Proof,
    string Why,
    string FoundBy,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset FoundAt)
{
    /// <summary>What is wrong with this issue; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Id) || !Id.StartsWith("pe-", StringComparison.Ordinal) ? "its id is pe-<n>"
            : string.IsNullOrWhiteSpace(Title) ? "it names nothing in its title"
            : string.IsNullOrWhiteSpace(Description) ? "it says nothing in its description"
            : Where.Count == 0 || Where.Any(path => string.IsNullOrWhiteSpace(path)) ? "it names no file it is in"
            : Proof.Problem() is { } proof ? proof
            : string.IsNullOrWhiteSpace(Why) ? "it says not why it was left"
            : !FlowInstanceId.IsWellFormed(FoundBy) ? $"'{FoundBy}' is not an instance id"
            : null;
}

/// <summary>The proof of a pre-existing issue: what the triage read or ran on the base commit.</summary>
/// <param name="Kind">What the proof is: <c>read</c> or <c>ran</c>.</param>
/// <param name="At">The commit it was made on.</param>
/// <param name="File">The file it read; null where it ran a command.</param>
/// <param name="Lines">The lines it read, as a range; null where it ran a command.</param>
/// <param name="Quote">What the lines say, cut to 4,000 characters, the cut said; null where it ran a command.</param>
/// <param name="Command">The command it ran; null where it read.</param>
/// <param name="Output">What the command said, cut to 4,000 characters, the cut said; null where it read.</param>
public sealed record PreExistingProof(string Kind, string At, string? File = null, string? Lines = null, string? Quote = null, string? Command = null, string? Output = null)
{
    /// <summary>The most a quote or an output holds, with the cut said.</summary>
    public const int Most = 4_000;

    /// <summary>What is wrong with this proof; null when nothing is.</summary>
    public string? Problem()
        => Kind is not ("read" or "ran") ? $"'{Kind}' is not what a proof is"
            : string.IsNullOrWhiteSpace(At) ? "it says no commit it was made on"
            : Kind == "read" && (string.IsNullOrWhiteSpace(File) || string.IsNullOrWhiteSpace(Lines) || string.IsNullOrWhiteSpace(Quote)) ? "a proof that reads names the file, the lines and what they say"
            : Kind == "ran" && (string.IsNullOrWhiteSpace(Command) || string.IsNullOrWhiteSpace(Output)) ? "a proof that runs names the command and what it said"
            : (Quote?.Length ?? 0) > Most || (Output?.Length ?? 0) > Most ? $"a quote or an output is cut to {Most.ToString(CultureInfo.InvariantCulture)} characters, the cut said"
            : null;
}

/// <summary>
/// The run's live owner, <c>.orchestrators/&lt;run&gt;.owner.json</c>: the process that drives it. A live owner refuses
/// a session (13); a dead one is reclaimed and said - and what its steps started is ended at the resume before anything
/// is continued.
/// </summary>
/// <param name="Machine">The machine the engine runs on.</param>
/// <param name="ProcessId">The engine's process id.</param>
/// <param name="StartedAt">When the engine took the run.</param>
/// <param name="Stamp">What tells that engine from a later process given its id; null where this machine would not say.</param>
public sealed record RunOwner(string Machine, int ProcessId, [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset StartedAt, string? Stamp = null)
{
    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Machine) ? "it names no machine"
            : ProcessId < 1 ? $"its process id is {ProcessId.ToString(CultureInfo.InvariantCulture)}"
            : null;
}

/// <summary>What one change of a run is, as the coordinator hands it to the records: which of the run's kinds it is, the instance it is about where one is, and the run whole.</summary>
public enum FlowChangeKind
{
    /// <summary>A session of the run began: the run's records are made whole where they are not there.</summary>
    SessionBegan,

    /// <summary>A session began over a run another left: what it left is taken up, and the records say so.</summary>
    Resumed,

    /// <summary>One instance changed: created, started, asked, answered or ended.</summary>
    Step,

    /// <summary>A safe pause began: recorded before anything is touched.</summary>
    PauseBegan,

    /// <summary>One reason step handed off, its handoff written in its own directory.</summary>
    Handoff,

    /// <summary>The safe pause completed: its last handoff written and everything it left running recorded.</summary>
    PauseCompleted,

    /// <summary>A session ended, however it ended.</summary>
    SessionEnded,

    /// <summary>An answer was given from outside the session - taken from the inbox, or refused where its question was no longer open.</summary>
    Answer,
}

/// <summary>
/// The run whole, as the coordinator holds it, handed to the records at every change: everything <c>flow.json</c> and
/// <c>workflow.json</c> say, and each step's own record. The records own nothing of the graph; this is the whole of
/// what they are told, and they are written from it, in the one order the run's law names.
/// </summary>
/// <param name="Status">How the run stands.</param>
/// <param name="EndedAt">When the run ended, where a session ended it; null while it runs.</param>
/// <param name="Spent">What the run's reasoning has spent against its budget.</param>
/// <param name="BudgetGranted">How many times the budget was answered more.</param>
/// <param name="Pause">The safe pause the run is under or was ended by; null where none.</param>
/// <param name="Steps">Every step instance, in the order the run made them.</param>
/// <param name="Next">The steps that would start now, in the order they would start.</param>
/// <param name="WaitingOn">The questions the run holds open, in the order they were raised.</param>
/// <param name="Redirects">Every redirect the run owes and has not made yet, in the order decided; null where none is owed.</param>
/// <param name="PreExisting">The run's pre-existing issues by what became of them; null until it has one.</param>
/// <param name="Issues">
/// Every pre-existing issue the run has listed, whole, where the change holds them: the records write the run's file of
/// them in this change's own order - after the run's status, before the event - where the list differs from the one
/// last written; null where the change says nothing of them.
/// </param>
public sealed record FlowSnapshot(
    FlowRunStatus Status,
    DateTimeOffset? EndedAt,
    FlowBudgetSpent Spent,
    int BudgetGranted,
    FlowPauseState? Pause,
    IReadOnlyList<FlowSnapshotStep> Steps,
    IReadOnlyList<string> Next,
    IReadOnlyList<string> WaitingOn,
    IReadOnlyList<FlowRedirectRecord>? Redirects = null,
    WorkflowPreExisting? PreExisting = null,
    IReadOnlyList<PreExistingIssue>? Issues = null);

/// <summary>One step instance as the snapshot holds it: everything its own record and the index say of it.</summary>
/// <param name="Instance">The instance's id.</param>
/// <param name="Step">The declaration's path among the flow.</param>
/// <param name="Type">The step's type; null for a group's own record.</param>
/// <param name="Group">Whether this is a group's own record.</param>
/// <param name="State">How the instance stands.</param>
/// <param name="Attempt">Which attempt of this visit has run.</param>
/// <param name="Visit">Which visit of the step this is.</param>
/// <param name="StartedAt">When this attempt began; null until it has.</param>
/// <param name="FinishedAt">When the instance ended; null until it has.</param>
/// <param name="Exit">The code the step's own work ended with; null until it has.</param>
/// <param name="Error">Why the step ended as it did, where the code alone cannot say.</param>
/// <param name="SkippedBecause">Why the flow decided not to run it, where it is Skipped.</param>
/// <param name="NotRunBecause">Why the run never reached it, where it is NotRun.</param>
/// <param name="Failure">Where this step's failure left the run; null for every other ending.</param>
/// <param name="EndedByQuestion">The kind of the question whose fail answer ended this step, where one did.</param>
/// <param name="Agent">The agent the step works in, where it names one with in or of.</param>
/// <param name="Made">The agent a create-agent step makes, as the engine named it; null for every other step.</param>
/// <param name="Question">The id of the question the step asks and no answer has come for; null where none is open.</param>
/// <param name="Handoff">The handoff a stop left, where one was written: its text, who wrote it and why.</param>
/// <param name="Output">The step's output; null where it wrote none.</param>
/// <param name="Item">The work item of the forEach this instance is one of; Missing outside every one.</param>
/// <param name="Index">The item's index; null outside every forEach.</param>
/// <param name="Iteration">The iteration of the repeat this instance is one of; null outside every one.</param>
/// <param name="Answers">The answers the questions of this visit were given, in the order they were given.</param>
/// <param name="WaitsOn">What the instance waits on, where it waits.</param>
/// <param name="Handlers">What the ending of this step owes and owes no longer: each handler declared beside it, and where it stands.</param>
/// <param name="HandledBy">The handler that took this step's failure, where one did; null where none did.</param>
/// <param name="Leftover">What a repeat that ended with its until still false says it left; null where the loop names none.</param>
/// <param name="RaisedBy">The instance whose ending raised this one; null for every step the schedule starts.</param>
/// <param name="Direction">The fix a person gave for the failure this instance's recovery works on; null on every other visit.</param>
/// <param name="NotRunCause">Which kind of reason the step is NotRun for, where it is; null otherwise.</param>
/// <param name="Issue">The pre-existing issue this instance was raised to triage; null for every step the schedule starts.</param>
/// <param name="IssuesOwed">The pre-existing issues this step reported whose triage is not raised yet; null where none is owed.</param>
/// <param name="LeftBy">The handler whose redirect sent the run back past this visit; null where none did.</param>
/// <param name="Cause">What the code alone cannot say of its ending: a refusal over a held lock; none for every other.</param>
/// <param name="Model">The model the agent a create-agent step makes is made for; null where <paramref name="Made"/> is.</param>
/// <param name="Copied">Whether the leg step's attempt let its tree go while its legs work on, every copy made (<see cref="FlowStepRecord.Copied"/>).</param>
/// <param name="Targets">A step of others' targets, each with where it stands (<see cref="FlowStepRecord.Targets"/>); null for every other step.</param>
public sealed record FlowSnapshotStep(
    string Instance,
    string Step,
    string? Type,
    bool Group,
    FlowInstanceState State,
    int Attempt,
    int Visit,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    int? Exit,
    string? Error,
    string? SkippedBecause,
    string? NotRunBecause,
    FlowFailure? Failure,
    string? EndedByQuestion,
    string? Agent,
    string? Made,
    string? Question,
    FlowHandoff? Handoff,
    JsonNode? Output,
    FlowValue Item,
    int? Index,
    int? Iteration,
    IReadOnlyList<FlowAnswerGiven> Answers,
    IReadOnlyList<string> WaitsOn,
    IReadOnlyList<FlowHandlerState>? Handlers = null,
    string? HandledBy = null,
    JsonNode? Leftover = null,
    string? RaisedBy = null,
    string? Direction = null,
    FlowNotRunCause? NotRunCause = null,
    FlowPreExistingReport? Issue = null,
    IReadOnlyList<FlowPreExistingReport>? IssuesOwed = null,
    string? LeftBy = null,
    OutcomeCause Cause = OutcomeCause.None,
    string? Model = null,
    bool Copied = false,
    IReadOnlyList<FlowTargetRecord>? Targets = null);
