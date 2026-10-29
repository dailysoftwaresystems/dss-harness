using System.Globalization;
using System.Text.Json.Serialization;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What raised a plan update of a run.</summary>
public static class FlowUpdateCauses
{
    /// <summary>A person amended the request the run was started with.</summary>
    public const string Amendment = "amendment";

    /// <summary>A person changed the last answer they gave the run.</summary>
    public const string ChangedAnswer = "changedAnswer";

    /// <summary>Every cause a plan update is raised by, in the order the report lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Amendment, ChangedAnswer];
}

/// <summary>How an update's id is spelled: <c>u&lt;n&gt;</c> from 1, in the order taken.</summary>
public static class FlowUpdateIds
{
    /// <summary>Whether <paramref name="id"/> is an update's id as this build spells it.</summary>
    public static bool IsId(string? id) => OrchestrationRules.IsNumbered(id, 'u');

    /// <summary>The id the <paramref name="n"/>-th update of a run takes.</summary>
    public static string Of(int n) => OrchestrationRules.Numbered('u', n);
}

/// <summary>
/// One plan update a run has taken, as <c>updates.json</c> holds it: its id, what raised it, and where it stands. Of
/// what raised it, an amendment holds its text, when it was made and through what; a changed answer holds the id of
/// the question whose answer was changed with the answer before and the answer now. Of where it stands, an update
/// taken where no plan holds it yet is <see cref="FlowUpdateStates.DoneAtOnce"/>; one taken where a plan has is
/// <see cref="FlowUpdateStates.UnderWay"/> - the run held while the step the <c>amend</c> block names plans it, its
/// instance named here - until the updated plan stands (<see cref="FlowUpdateStates.Done"/>) or its planning was
/// given up (<see cref="FlowUpdateStates.Failed"/>, saying why no plan holds the change); one the run cannot take
/// is <see cref="FlowUpdateStates.Refused"/>.
/// </summary>
/// <param name="Id">The update's id: <c>u&lt;n&gt;</c>, in the order taken.</param>
/// <param name="Cause">What raised it: an <c>amendment</c> or a <c>changedAnswer</c>.</param>
/// <param name="State">Where the update stands: <c>queued</c>, <c>underWay</c>, <c>done</c>, <c>failed</c> or <c>refused</c>.</param>
/// <param name="TakenAt">When the update was taken into this file.</param>
/// <param name="Amendment">The amendment's text, in the person's words; null for every other cause.</param>
/// <param name="Through">How the person gave it: <c>amend-orchestrator-request</c>, <c>answer-orchestrator</c> or <c>front-end</c>.</param>
/// <param name="Question">The id of the question whose answer was changed, where one was; null for every other cause.</param>
/// <param name="FromAnswer">The answer as it stood, where one was changed; null for every other cause.</param>
/// <param name="ToAnswer">The answer as it was changed to, where one was; null for every other cause.</param>
/// <param name="Inbox">
/// The id of the <c>inbox.json</c> entry the change was handed over under, to an engine running the run; null where
/// none was, so an entry the run's records hold is never weighed again.
/// </param>
/// <param name="Why">What the run says of the update where its state alone cannot: why one was refused, or given up.</param>
/// <param name="Step">
/// The instance of the step the <c>amend</c> block names that plans this update, where the run has raised one; null
/// until it has. How that visit ended is its own step's record, which this entry names and does not copy.
/// </param>
/// <param name="Then">
/// The step the <c>amend</c> block's <c>then</c> names, written as the update's redirect to it is decided; null until
/// one was, and where the block names none.
/// </param>
/// <param name="Text">The text given with a changed answer, where its question takes one; null where none was.</param>
public sealed record FlowUpdateEntry(
    string Id,
    string Cause,
    string State,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset TakenAt,
    string? Amendment = null,
    string? Through = null,
    string? Question = null,
    string? FromAnswer = null,
    string? ToAnswer = null,
    string? Inbox = null,
    string? Why = null,
    string? Step = null,
    string? Then = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text = null)
{
    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => !FlowUpdateIds.IsId(Id) ? $"'{Id}' is not an update's id"
            : !FlowUpdateCauses.All.Contains(Cause, StringComparer.Ordinal) ? $"'{Cause}' is not what raises a plan update"
            : !FlowUpdateStates.All.Contains(State, StringComparer.Ordinal) ? $"'{State}' is not a state an update stands in"
            : Cause == FlowUpdateCauses.Amendment && string.IsNullOrWhiteSpace(Amendment) ? "an amendment is recorded with its text, and its text is empty"
            : Cause == FlowUpdateCauses.ChangedAnswer && !FlowQuestionIds.IsWellFormed(Question) ? $"'{Question}' is not a question's id"
            : Cause == FlowUpdateCauses.ChangedAnswer && (string.IsNullOrWhiteSpace(FromAnswer) || string.IsNullOrWhiteSpace(ToAnswer)) ? "a changed answer is recorded with the answer before and the answer now"
            : State == FlowUpdateStates.Refused && string.IsNullOrWhiteSpace(Why) ? "it says not why it was refused"
            : Inbox is not null && string.IsNullOrWhiteSpace(Inbox) ? "it names no inbox entry"
            : Step is not null && !FlowInstanceId.IsWellFormed(Step) ? $"'{Step}' is not an instance's id"
            : null;
}

/// <summary>The states a plan update stands in.</summary>
public static class FlowUpdateStates
{
    /// <summary>Recorded, and waiting for the run to be gone on with; nothing of it has been taken yet.</summary>
    public const string Queued = "queued";

    /// <summary>Taken, and being planned: the run is held while it is.</summary>
    public const string UnderWay = "underWay";

    /// <summary>Done: its pieces are placed and what it changed stands.</summary>
    public const string Done = "done";

    /// <summary>Failed: its planning was given up, and no plan holds the change.</summary>
    public const string Failed = "failed";

    /// <summary>Refused: the run did not take it, and what it says is in its <c>why</c>.</summary>
    public const string Refused = "refused";

    /// <summary>
    /// Done the moment it was taken, for no plan held it: every step that starts reads the change. Where a plan has
    /// begun, an update is planned by the step the <c>amend</c> block names and ends <see cref="Done"/> when the
    /// updated plan stands.
    /// </summary>
    public const string DoneAtOnce = Done;

    /// <summary>Every state an update stands in, in the order the report lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Queued, UnderWay, Done, Failed, Refused];
}

/// <summary>
/// <c>updates.json</c>: the one owner of what a plan update is - every update the run has taken, in the order taken,
/// with what raised it, word for word, and where it stands. Written whole each time, under the run's own section,
/// outside every directory an agent may write. The request the run was started with is never rewritten: it stays in
/// <c>flow.json</c>, and every prompt and expression that reads the input reads the value as given with each
/// amendment of this file under it (<see cref="FlowUpdatesFile.InputOf"/>).
/// </summary>
/// <param name="Schema">The file's schema, 1.</param>
/// <param name="Run">The run's name.</param>
/// <param name="UpdatedAt">When the file was last written.</param>
/// <param name="Updates">Every update in the order taken, whatever its state.</param>
public sealed record FlowUpdatesFile(
    int Schema,
    string Run,
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset UpdatedAt,
    IReadOnlyList<FlowUpdateEntry> Updates)
{
    /// <summary>What is wrong with this file; null when nothing is.</summary>
    public string? Problem()
        => Schema != 1 ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads 1"
            : string.IsNullOrWhiteSpace(Run) ? "it names no run"
            : Updates.Any(update => update.Problem() is { } problem) ? $"its update '{Updates.First(update => update.Problem() is not null).Id}': {Updates.First(update => update.Problem() is not null).Problem()}"
            : Updates.GroupBy(update => update.Id, StringComparer.Ordinal).Any(group => group.Count() > 1) ? "one id holds two updates"
            : Updates.GroupBy(update => update.Inbox, StringComparer.Ordinal).Where(group => group.Key is not null).Any(group => group.Count() > 1) ? "one inbox entry holds two updates"
            : null;

    /// <summary>Every amendment the run has taken, in the order taken, whatever became of it: what the input reads under the value as given.</summary>
    public IReadOnlyList<FlowUpdateEntry> Amendments()
        => [.. Updates.Where(update => update.Cause == FlowUpdateCauses.Amendment)];

    /// <summary>The id the next update taken is given.</summary>
    public string NextId()
        => FlowUpdateIds.Of(Updates.Count + 1);

    /// <summary>
    /// The input as every prompt and expression reads it once amendments have been taken: the value as given, then
    /// each amendment under a line that says when it was made - and of one whose update failed, that no plan holds it.
    /// An amendment the run refused is never read: nothing of it was changed.
    /// </summary>
    /// <param name="value">The value the run was started with, as <c>flow.json</c> holds it.</param>
    /// <param name="amendments">Every amendment the run has taken, in the order taken, from <see cref="Amendments"/>.</param>
    public static string InputOf(string value, IReadOnlyList<FlowUpdateEntry> amendments)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(amendments);

        if (amendments.Count == 0)
        {
            return value;
        }

        var rendered = new System.Text.StringBuilder(value);

        foreach (var amendment in amendments.Where(amendment => amendment.State != FlowUpdateStates.Refused))
        {
            _ = rendered.Append("\n\namended at ").Append(FlowTimeConverter.Written(amendment.TakenAt))
                .Append(amendment.State == FlowUpdateStates.Failed ? ", and no plan holds it:\n" : ":\n")
                .Append(amendment.Amendment);
        }

        return rendered.ToString();
    }
}

/// <summary>
/// What a run refuses a change it is given - an amendment of its request, or a changed answer - before anything of
/// it is recorded: the one owner of the gates every path into a plan update passes, the command's where a person
/// gives one and the engine's where it takes one. Nothing is changed by a refusal, and no update is recorded for one.
/// </summary>
public static class FlowUpdateGates
{
    /// <summary>
    /// Why the run refuses a change of the kind its <paramref name="definition"/> names, as it stands in
    /// <paramref name="flow"/>; null where it takes it. The run's own state is the gates' - it is done, it has
    /// started the step past which nothing is planned again, or nothing it could run would carry the change out.
    /// </summary>
    /// <param name="definition">The definition as the run began with it, whose <c>amend</c> block says what a change passes.</param>
    /// <param name="flow">The run's state in one file, as it stands.</param>
    /// <param name="cause">What raises the update: one of <see cref="FlowUpdateCauses"/>'s names, for the words that name it.</param>
    public static string? Refused(WorkflowDefinition definition, FlowFile flow, string cause)
        => Refused(
            definition,
            flow.Run,
            flow.Status,
            [.. flow.Steps.Select(step => new FlowStepAt(step.Step, step.State, step.Group, step.Iteration))],
            cause);

    /// <summary>
    /// Why the run refuses a change, as its steps stand - the same gates over the engine's own view of them; null
    /// where it takes it.
    /// </summary>
    /// <param name="definition">The definition as the run began with it, whose <c>amend</c> block says what a change passes.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="status">How the run stands: one of <see cref="FlowRunStatus"/>'s names.</param>
    /// <param name="steps">Every step instance the run holds, as its records read them.</param>
    /// <param name="cause">What raises the update: one of <see cref="FlowUpdateCauses"/>'s names, for the words that name it.</param>
    public static string? Refused(WorkflowDefinition definition, string run, string status, IReadOnlyList<FlowStepAt> steps, string cause)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var what = cause == FlowUpdateCauses.ChangedAnswer ? "answer" : "request";

        if (status == nameof(FlowRunStatus.Done))
        {
            return $"run '{run}' is done; there is nothing left to go back to";
        }

        if (definition.Amend is { } amend && amend.Before is { } before
            && steps.Any(step => step.Step == before && step.State != nameof(FlowInstanceState.NotRun)))
        {
            return $"run '{run}' has started '{before}': what it built is being written down, and its plan is no longer changed. Start a run for the {what} once this one has ended";
        }

        return PastItsWorkItems(definition, run, steps, what);
    }

    /// <summary>
    /// Why nothing the run could run would carry a change out - the work items of its last round past - where that
    /// is settled by the definition's own bounds; null where it is not. A loop the definition leaves open - an
    /// <c>until</c> with no <c>maxIterations</c> - is never past its work items by this reckoning: what it carries is
    /// decided where an update is taken.
    /// </summary>
    private static string? PastItsWorkItems(WorkflowDefinition definition, string run, IReadOnlyList<FlowStepAt> steps, string what)
    {
        if (definition.Amend is not { } amend || StepOf(definition, amend.WhenAddScope) is null)
        {
            return null;
        }

        var group = ParentOf(amend.WhenAddScope);
        var loopPath = group is null ? null : ParentOf(group);

        if (loopPath is null || StepOf(definition, loopPath) is not { Repeat: { MaxIterations: { } bound } } loop)
        {
            return null;
        }

        var rounds = steps.Where(step => step.Group && step.Step == group).ToList();
        var last = rounds.Max(step => step.Iteration) ?? 0;
        var allowed = bound + (amend.MaxAddedIterations ?? 1);

        if (last < allowed || rounds.Any(step => step.Iteration == last && step.State is not (nameof(FlowInstanceState.Done) or nameof(FlowInstanceState.Failed) or nameof(FlowInstanceState.Skipped))))
        {
            return null;
        }

        return $"run '{run}' is past the work items of round {last.ToString(CultureInfo.InvariantCulture)} of '{loop.Name}', the last it can run ({bound.ToString(CultureInfo.InvariantCulture)} by its own bound, and the {(amend.MaxAddedIterations ?? 1).ToString(CultureInfo.InvariantCulture)} that amend.maxAddedIterations lets updates add): nothing would carry the change out. Start a run for the {what} once this one has ended";
    }

    /// <summary>The declaration a path names, walking the definition's groups; null where the path names no step.</summary>
    internal static WorkflowStep? StepOf(WorkflowDefinition definition, string path)
    {
        IEnumerable<WorkflowStep> siblings = definition.Steps;
        WorkflowStep? found = null;

        foreach (var name in path.Split('.'))
        {
            found = siblings.FirstOrDefault(step => string.Equals(step.Name, name, StringComparison.Ordinal));

            if (found is null)
            {
                return null;
            }

            siblings = found.Steps;
        }

        return found;
    }

    /// <summary>The path of the group a path is directly in: the path less its own name; null for a step of the flow's top.</summary>
    internal static string? ParentOf(string path)
    {
        var last = path.LastIndexOf('.');

        return last < 0 ? null : path[..last];
    }
}

/// <summary>One step instance as the gates read it: its declaration's path, its state, and - a group's - its round.</summary>
/// <param name="Step">The declaration's path among the flow.</param>
/// <param name="State">How the instance stands, as the engine's identifiers spell it.</param>
/// <param name="Group">Whether this is a group's own record.</param>
/// <param name="Iteration">The iteration of the repeat this instance is one of; null outside every one.</param>
public readonly record struct FlowStepAt(string Step, string State, bool Group, int? Iteration);
