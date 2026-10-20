using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// How every time a run's records hold is written and read: UTC, <c>yyyy-MM-dd HH:mm:ss</c> (the user, 2026-10-17:
/// "sql datetime"), so a client parses one form; and a time that has not come yet is written <see langword="null"/>,
/// never left out, so every entry has the same members.
/// </summary>
public sealed class FlowTimeConverter : JsonConverter<DateTimeOffset>
{
    /// <summary>The one form a time is written in.</summary>
    public const string Form = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Reads a time as it is written; anything else refuses, for a record is never guessed at.</summary>
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            && DateTimeOffset.TryParseExact(reader.GetString(), Form, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : throw new JsonException($"a time is written '{Form}' (UTC)");

    /// <summary>Writes a time in the one form.</summary>
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUniversalTime().ToString(Form, CultureInfo.InvariantCulture));
}

/// <summary>
/// One event of a run, as <c>events.jsonl</c> holds it: what changed, whole, one JSON object to a line, append-only.
/// History, never truth - the state files are the truth, and they are written before the event that tells of them.
/// </summary>
/// <param name="Seq">The event's number in the run, from 1.</param>
/// <param name="At">When it happened.</param>
/// <param name="Kind">What kind of event it is: <c>sessionBegan</c>, <c>step</c>, <c>pauseBegan</c>, <c>handoff</c>, <c>pauseCompleted</c>, <c>resumed</c>, <c>sessionEnded</c>.</param>
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
    /// <summary>Every kind of event a run appends.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["sessionBegan", "step", "pauseBegan", "handoff", "pauseCompleted", "resumed", "sessionEnded", "answer"];

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
/// <param name="State">How the instance stands, as the engine's identifier spells it.</param>
/// <param name="Attempt">Which attempt of this visit has run: 1 for the first, one more for each retry.</param>
/// <param name="Visit">Which visit of the step this is.</param>
/// <param name="Answers">The answers the questions of this visit were given, in the order they were given.</param>
/// <param name="WaitsOn">What the instance waits on, where it waits: a dependency, a claim, a counter, a question, the resume.</param>
/// <param name="Type">The step's type; null for a group's own record.</param>
/// <param name="StartedAt">When this attempt began; null until it has.</param>
/// <param name="FinishedAt">When the instance ended; null until it has.</param>
/// <param name="Exit">The code the step's own work ended with; null until it has.</param>
/// <param name="Error">Why the step ended as it did, where the code alone cannot say.</param>
/// <param name="SkippedBecause">Why the flow decided not to run it, where it is Skipped.</param>
/// <param name="NotRunBecause">Why the run never reached it, where it is NotRun.</param>
/// <param name="Failure">Where a failure left the run: <c>passedOver</c> (the run went on over it), <c>endedRun</c> (its failure ended the session failed); null for every other ending.</param>
/// <param name="EndedByQuestion">The kind of the question whose <c>fail</c> answer ended this step, where one did.</param>
/// <param name="Agent">The agent the step works in - as its <c>in</c> or <c>of</c> names, made by its create-agent - where it names one.</param>
/// <param name="Made">The agent a create-agent step made, as its output names it; null for every other step.</param>
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
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? StartedAt = null,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? FinishedAt = null,
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
    string? Direction = null)
{
    /// <summary>Where a failure can leave the run, as the record names it.</summary>
    public static readonly IReadOnlyList<string> Failures = ["passedOver", "endedRun"];

    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => !FlowInstanceId.IsWellFormed(Instance) ? $"'{Instance}' is not an instance id"
            : string.IsNullOrWhiteSpace(Step) ? "it names no step"
            : !Enum.TryParse<States>(State, out _) ? $"'{State}' is not an instance state"
            : Attempt < 1 ? $"its attempt is {Attempt.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : Visit < 1 ? $"its visit is {Visit.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : Failure is { } failure && !Failures.Contains(failure, StringComparer.Ordinal) ? $"'{failure}' is not where a failure leaves the run"
            : Question is not null && !Question.StartsWith('q') ? $"'{Question}' is not a question's id"
            : Item is not null && Index is null ? "it holds an item without its index"
            : Answers.Any(answer => string.IsNullOrWhiteSpace(answer.Question) || string.IsNullOrWhiteSpace(answer.Answer)) ? "an answer names neither its question nor its answer"
            : Handlers is { Count: > 0 } && Handlers.Any(handler => handler.Problem() is not null) ? $"its handlers hold one that is not a state: {Handlers.First(handler => handler.Problem() is not null).Problem()}"
            : HandledBy is not null && !FlowInstanceId.IsWellFormed(HandledBy) && !HandledBy.StartsWith('q') ? $"'{HandledBy}' names neither an instance nor a question"
            : RaisedBy is not null && !FlowInstanceId.IsWellFormed(RaisedBy) ? $"'{RaisedBy}' is not an instance id"
            : null;

    /// <summary>The instance states, as the record and the engine's identifiers spell them.</summary>
    public enum States
    {
        /// <summary>Declared, and the run has not reached it.</summary>
        NotStarted,

        /// <summary>Running now, or between two attempts of one visit.</summary>
        Running,

        /// <summary>Its work ended as the run wanted it.</summary>
        Done,

        /// <summary>The flow decided not to run it.</summary>
        Skipped,

        /// <summary>Its work ended failed, and nothing passed the failure over.</summary>
        Failed,

        /// <summary>Waiting on a question it asked or its failure raised.</summary>
        Paused,

        /// <summary>A stop ended it where it stood; it runs again at the resume.</summary>
        Stopped,

        /// <summary>Never reached.</summary>
        NotRun,
    }
}

/// <summary>The handoff a step's record names: who wrote it and why, for the file is its own record beside the step's.</summary>
/// <param name="By">Who wrote it: <c>agent</c> or <c>engine</c>.</param>
/// <param name="Why">Why the engine wrote it, where it did; null where the agent answered.</param>
public sealed record FlowHandoffRecord(string By, string? Why = null){
    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => By is not ("agent" or "engine") ? $"'{By}' is not who writes a handoff"
            : By == "engine" && string.IsNullOrWhiteSpace(Why) ? "a handoff the engine wrote says why"
            : null;
}

/// <summary>One handler of a step's ending, as the source's own record names where it stands.</summary>
/// <param name="Handler">The handler's name, as its declaration names it.</param>
/// <param name="State">Where it stands: <c>owed</c> (the ending is written, the raise is not made yet), <c>raised</c>, <c>done</c> or <c>passed</c>.</param>
public sealed record FlowHandlerState(string Handler, string State)
{
    /// <summary>Every state a handler of an ending can stand in, as the record spells them.</summary>
    public static readonly IReadOnlyList<string> States = ["owed", "raised", "done", "passed"];

    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Handler) ? "it names no handler"
            : !States.Contains(State, StringComparer.Ordinal) ? $"'{State}' is not where a handler of an ending stands"
            : null;
}

/// <summary>A redirect the run owes and has not made yet, as <c>flow.json</c> holds it: written before the first reset, made once.</summary>
/// <param name="Handler">The handler that named it, by its instance id.</param>
/// <param name="Target">The step the run goes to, by its name.</param>
/// <param name="Steps">The instances the redirect resets, by their ids, in the order they were computed.</param>
public sealed record FlowRedirectRecord(string Handler, string Target, IReadOnlyList<string> Steps)
{
    /// <summary>What is wrong with this record; null when nothing is.</summary>
    public string? Problem()
        => !FlowInstanceId.IsWellFormed(Handler) ? $"'{Handler}' is not an instance id"
            : string.IsNullOrWhiteSpace(Target) ? "it names no step to go to"
            : Steps.Any(id => !FlowInstanceId.IsWellFormed(id)) ? "it resets an instance its id does not name"
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
/// <param name="Status">How the run stands, as the engine's identifier spells it.</param>
/// <param name="Spent">What the run's reasoning has spent against its budget: usd, tokens and minutes.</param>
/// <param name="BudgetGranted">How many times the budget was answered <c>more</c>.</param>
/// <param name="Steps">Every step instance, in the order the run made them.</param>
/// <param name="Next">The steps that would start now, in the order they would start.</param>
/// <param name="StartCommit">The commit the run started from; null where it started beside the tree's history.</param>
/// <param name="Branch">The branch the run started on.</param>
/// <param name="Pause">The safe pause the run is under or was ended by: cause, all, at, completedAt; null where none.</param>
/// <param name="Redirect">The redirect the run owes and has not made yet, written before the first reset; null where none is owed.</param>
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
    string? StartCommit = null,
    string? Branch = null,
    FlowPauseRecord? Pause = null,
    FlowRedirectRecord? Redirect = null)
{
    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : string.IsNullOrWhiteSpace(Run) ? "it names no run"
            : string.IsNullOrWhiteSpace(Definition) ? "it names no definition"
            : string.IsNullOrWhiteSpace(DefinitionDigest) ? "it names no digest of its definition"
            : !Enum.TryParse<StatusOf>(Status, out _) ? $"'{Status}' is not a run status"
            : BudgetGranted < 0 ? "the budget was granted more a negative number of times"
            : Steps.Any(step => step.Problem() is { } problem) ? $"its steps hold one that is not a record: {Steps.First(step => step.Problem() is not null).Problem()}"
            : Steps.GroupBy(step => step.Instance, StringComparer.Ordinal).Any(group => group.Count() > 1) ? "it holds an instance twice"
            : Next.Any(id => Steps.All(step => !string.Equals(step.Instance, id, StringComparison.Ordinal))) ? "next names an instance the file does not hold"
            : null;

    /// <summary>The run's statuses, as the record and the engine's identifiers spell them.</summary>
    public enum StatusOf
    {
        /// <summary>Steps are running, or nothing has ended the run yet.</summary>
        Running,

        /// <summary>Everything ended as asked.</summary>
        Done,

        /// <summary>A failure nothing passed over ended it.</summary>
        Failed,

        /// <summary>A question is open, and nothing more can run until it is answered.</summary>
        Paused,

        /// <summary>A stop ended it; it goes on at a resume.</summary>
        Stopped,
    }
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
/// <param name="CompletedAt">When the pause completed - its last handoff written and everything it left running recorded; null while it records.</param>
/// <param name="Handoffs">Every step that handed off, in the order the handoffs were written.</param>
public sealed record FlowPauseRecord(
    string Cause,
    bool All,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset At,
    IReadOnlyList<string> Handoffs,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? CompletedAt = null)
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
/// <param name="Status">How the run stands, as the engine's identifier spells it.</param>
/// <param name="StartedAt">When the run began.</param>
/// <param name="UpdatedAt">When this status was written.</param>
/// <param name="Progress">Every instance of the run by its state, and their total.</param>
/// <param name="Steps">Every step of the definition, by its path, from the moment the run is created.</param>
/// <param name="FinishedAt">When the run ended; null while it runs.</param>
/// <param name="WaitingOn">What the run waits on: the open questions' ids, or the safe pause; null where it waits on nothing.</param>
public sealed record WorkflowStatus(
    int Schema,
    string Run,
    string Workflow,
    string Status,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset StartedAt,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset UpdatedAt,
    WorkflowProgress Progress,
    IReadOnlyDictionary<string, WorkflowStepStatus> Steps,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? FinishedAt = null,
    IReadOnlyList<string>? WaitingOn = null)
{
    /// <summary>What is wrong with this status; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : string.IsNullOrWhiteSpace(Run) ? "it names no run"
            : string.IsNullOrWhiteSpace(Workflow) ? "it names no workflow"
            : !RunStatuses.Contains(Status, StringComparer.Ordinal) ? $"'{Status}' is not a run status"
            : Progress.Problem() is { } progress ? progress
            : Steps.Values.Any(step => step.Problem() is { } problem) ? Steps.Values.First(step => step.Problem() is not null).Problem()
            : null;

    /// <summary>The run's statuses, as a client switches on them.</summary>
    public static readonly IReadOnlyList<string> RunStatuses = ["Running", "Paused", "Done", "Failed", "Stopped"];
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
/// <param name="Recovering">How many a recovery works on; none until the recovery is built.</param>
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
/// <param name="Status">The status the run would show for the step, as the engine's identifier spells it.</param>
/// <param name="Instances">Each instance of the step, by its id.</param>
/// <param name="StartedAt">Its instances' first start; null until one has.</param>
/// <param name="FinishedAt">Its instances' last finish; null until one has.</param>
/// <param name="Seconds">What the step's instances took together; null until it finished.</param>
public sealed record WorkflowStepStatus(
    string Status,
    IReadOnlyDictionary<string, WorkflowInstanceStatus> Instances,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? StartedAt = null,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? FinishedAt = null,
    long? Seconds = null)
{
    /// <summary>The step statuses, as a client switches on them.</summary>
    public static readonly IReadOnlyList<string> StepStatuses = ["NotStarted", "Running", "Recovering", "Paused", "Done", "Failed", "Skipped", "Stopped", "NotRun"];

    /// <summary>What is wrong with this status; null when nothing is.</summary>
    public string? Problem()
        => !StepStatuses.Contains(Status, StringComparer.Ordinal) ? $"'{Status}' is not a step status"
            : Instances.Values.Any(instance => instance.Problem() is { } problem) ? Instances.Values.First(instance => instance.Problem() is not null).Problem()
            : null;
}

/// <summary>One instance of a step in <c>workflow.json</c>.</summary>
/// <param name="Status">How the instance stands, as the engine's identifier spells it.</param>
/// <param name="Visit">Which visit of the step this is.</param>
/// <param name="Attempt">Which attempt of this visit has run.</param>
/// <param name="Recoveries">How many times a recovery was given its failure; none until the recovery is built.</param>
/// <param name="StartedAt">When this instance began; null until it has.</param>
/// <param name="FinishedAt">When this instance ended; null until it has.</param>
/// <param name="Seconds">What the instance took; null until it finished.</param>
/// <param name="Recovery">The recovery it is under, where one holds it; null otherwise.</param>
public sealed record WorkflowInstanceStatus(    string Status,
    int Visit,
    int Attempt,
    int Recoveries,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? StartedAt = null,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset? FinishedAt = null,
    long? Seconds = null,
    WorkflowRecovery? Recovery = null)
{
    /// <summary>What is wrong with this status; null when nothing is.</summary>
    public string? Problem()
        => !WorkflowStepStatus.StepStatuses.Contains(Status, StringComparer.Ordinal) ? $"'{Status}' is not a step status"
            : Visit < 1 || Attempt < 1 || Recoveries < 0 ? "a visit, an attempt or a recovery is counted from nowhere"
            : null;
}

/// <summary>The recovery an instance is under, as <c>workflow.json</c> holds it; none until the recovery is built.</summary>
/// <param name="Visit">Which visit the recovery sent the run back for.</param>
/// <param name="MaxVisits">The most visits the step may be given.</param>
/// <param name="Course">What the recovery works on it as.</param>
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
/// <param name="Redirect">The redirect the run owes and has not made yet; null where none is owed.</param>
/// <param name="PreExisting">The run's pre-existing issues by what became of them; null until it has one.</param>
public sealed record FlowSnapshot(
    FlowRunStatus Status,
    DateTimeOffset? EndedAt,
    FlowBudgetSpent Spent,
    int BudgetGranted,
    FlowPauseState? Pause,
    IReadOnlyList<FlowSnapshotStep> Steps,
    IReadOnlyList<string> Next,
    IReadOnlyList<string> WaitingOn,
    FlowRedirectRecord? Redirect = null,
    WorkflowPreExisting? PreExisting = null);

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
/// <param name="PassedOver">Whether the run passed this failure over.</param>
/// <param name="EndedRun">Whether this failure ended the session failed.</param>
/// <param name="EndedByQuestion">The kind of the question whose fail answer ended this step, where one did.</param>
/// <param name="Agent">The agent the step works in, where it names one with in or of.</param>
/// <param name="Made">The agent a create-agent step made; null for every other step.</param>
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
    bool PassedOver,
    bool EndedRun,
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
    string? Direction = null);
