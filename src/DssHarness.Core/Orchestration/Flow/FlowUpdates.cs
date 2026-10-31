using System.Globalization;
using System.Text.Json.Nodes;
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

/// <summary>The kinds a piece of a plan update is: what the update does to one work item.</summary>
public static class FlowUpdatePieceKinds
{
    /// <summary>Work the plan did not have.</summary>
    public const string Added = "added";

    /// <summary>Work the plan had, that is now to be done differently.</summary>
    public const string Changed = "changed";

    /// <summary>Work the plan had, that is withdrawn.</summary>
    public const string Removed = "removed";

    /// <summary>Work under way whose scope the update leaves as it is, and that has to know of the change.</summary>
    public const string Told = "told";

    /// <summary>Every kind a piece is, in the order the report lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Added, Changed, Removed, Told];

    /// <summary>The kinds that carry a work item out: everything but <see cref="Told"/>.</summary>
    public static readonly IReadOnlyList<string> Carried = [Added, Changed, Removed];
}

/// <summary>What a work item is known by: its name, read by the one rule every reader of a plan's items uses.</summary>
public static class FlowWorkItems
{
    /// <summary>
    /// The name of the work item <paramref name="item"/>, where it is an object whose <c>name</c> is a text that is not
    /// blank; null for anything else - a name of another type reads as none, never as a failure of whoever reads it.
    /// </summary>
    /// <param name="item">The work item, as a plan, a piece or a scope holds it.</param>
    public static string? NameOf(JsonNode? item)
        => item is JsonObject held && held["name"] is JsonValue value && value.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
}

/// <summary>
/// One piece of a plan update, as the planning step concluded it and <c>updates.json</c> holds it: what the update
/// does to one work item - adds it, changes it, withdraws it, or tells the work under way - with the planning agent's
/// texts for whoever carries it out, and, once the piece is placed, where it went and what carries it.
/// </summary>
/// <param name="Kind">What the piece is: one of <see cref="FlowUpdatePieceKinds"/>'s names.</param>
/// <param name="Item">
/// The work item, as it now is for <c>added</c> and <c>changed</c>, where it is an item of the updated plan; for
/// <c>removed</c>, the item under the name it had, written for the step that takes it back; null for <c>told</c>.
/// </param>
/// <param name="For">
/// For <c>told</c> alone: the id of the work under way that is to be told, as the planning step's prompt listed it;
/// null for every other kind.
/// </param>
/// <param name="Changed">What the update changes for this work; never empty.</param>
/// <param name="Keep">What of the work done stays, where the piece is one that runs.</param>
/// <param name="Undo">What is taken back, where the piece is one that runs.</param>
/// <param name="Instead">What is done in its place, where the piece is one that runs.</param>
/// <param name="Placed">
/// Where the piece was placed: the instance of the work-item group that carries it out - a new one made for it, the one
/// it was given to in the place of its own, the one it joined at the check that opens a round, or the one it ends to be
/// visited again - null until it is placed, and while it is owed to a round that has not opened.
/// </param>
/// <param name="OwedTo">
/// Where a piece was owed that no instance carried when it was placed: the round it joins when that round opens, or the
/// next iteration of the loop around a round that had ended; null where an instance carried it at once. It stays as it
/// was written once the piece joins a round, which <paramref name="Placed"/> then names.
/// </param>
/// <param name="CarriedBy">The visit that carries the piece out - the step that runs in the work-item group's place; null until it is drawn.</param>
/// <param name="Ended">How the piece's work ended, where it has; null until then.</param>
/// <param name="Again">
/// The instance of the work-item group visited again in the place of the one <paramref name="Placed"/> names, where the
/// piece brought that one to rest: it carries the piece's work out, with the agent of the one it replaces; null until it
/// is made, and for every piece that brought nothing to rest.
/// </param>
public sealed record FlowUpdatePiece(
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonNode? Item = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? For = null,
    string? Changed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Keep = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Undo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Instead = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Placed = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OwedTo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? CarriedBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Ended = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Again = null)
{
    /// <summary>The name of the work item the piece is, where it is one; null where it is not.</summary>
    public string? ItemName()
        => Kind == FlowUpdatePieceKinds.Told ? null : FlowWorkItems.NameOf(Item);

    /// <summary>
    /// Whether the piece is owed still: it was owed to a round when it was placed, has joined none since, and has not
    /// ended - what the check that opens the next round is handed, and what makes the loop around the round run again.
    /// </summary>
    public bool IsOwed => OwedTo is not null && Placed is null && Ended is null;

    /// <summary>
    /// What is wrong with this piece, said of it as its subject: <c>says not what the update changes for its work, and a
    /// piece always does</c>; null when nothing is. The one owner of a piece's shape: the engine reads a planning step's
    /// conclusion by it, <c>validate-plan</c> checks the plan's scope by it, and <c>updates.json</c> is held to it.
    /// </summary>
    public string? Problem()
        => !FlowUpdatePieceKinds.All.Contains(Kind, StringComparer.Ordinal) ? $"is of the kind '{Kind}', and a piece is one of {KindsSaid}"
            : string.IsNullOrWhiteSpace(Changed) ? "says not what the update changes for its work, and a piece always does"
            : Kind == FlowUpdatePieceKinds.Told && string.IsNullOrWhiteSpace(For) ? "is told, and names no work under way: a told piece tells work that is going on"
            : Kind == FlowUpdatePieceKinds.Told && Item is not null ? "is told and holds a work item: it names work under way instead"
            : Kind != FlowUpdatePieceKinds.Told && Item is not JsonObject ? $"is '{Kind}', and holds no work item: a piece that carries a work item out holds one"
            : Kind != FlowUpdatePieceKinds.Told && ItemName() is null ? "holds a work item with no name, and a work item is known by its name"
            : Placed is not null && !FlowInstanceId.IsWellFormed(Placed) ? $"is placed in '{Placed}', which is not an instance's id"
            : OwedTo is not null && string.IsNullOrWhiteSpace(OwedTo) ? "is owed to a round, and says not which"
            : CarriedBy is not null && !FlowInstanceId.IsWellFormed(CarriedBy) ? $"is carried by '{CarriedBy}', which is not an instance's id"
            : Again is not null && !FlowInstanceId.IsWellFormed(Again) ? $"is visited again in '{Again}', which is not an instance's id"
            : null;

    /// <summary>
    /// The piece a planning step's conclusion holds in <paramref name="node"/>, read by the one rule of a piece's shape;
    /// null with why where it is none (<see cref="Problem"/>'s words, said of the piece as their subject).
    /// </summary>
    /// <param name="node">One element of the list the <c>amend</c> block's <c>scope</c> yields.</param>
    /// <param name="problem">What is wrong with it, where it is no piece the engine takes; null where it is one.</param>
    public static FlowUpdatePiece? Read(JsonNode? node, out string? problem)
    {
        if (node is not JsonObject held || held["kind"] is not JsonValue kind || !kind.TryGetValue<string>(out var what))
        {
            problem = $"is of the wrong shape: it is no object with a kind, and a piece is one of {KindsSaid}";
            return null;
        }

        string? Text(string member) => held[member] is JsonValue text && text.TryGetValue<string>(out var written) ? written : null;

        var piece = new FlowUpdatePiece(what, held["item"]?.DeepClone(), Text("for"), Text("changed"), Text("keep"), Text("undo"), Text("instead"));
        problem = piece.Problem();
        return problem is null ? piece : null;
    }

    /// <summary>
    /// What is wrong with <paramref name="pieces"/> as one update's, said with the pieces as its subject - <c>name the
    /// work item 'one' twice, and a work item is named by one piece of an update</c> - where two of them name one work
    /// item; null where none is named twice. The one owner of the rule and its words: the check of a plan's scope, the
    /// engine's reading of it and <c>updates.json</c> are each held to it, each naming the pieces its own way.
    /// </summary>
    /// <param name="pieces">The pieces of one update.</param>
    public static string? NamedTwice(IEnumerable<FlowUpdatePiece> pieces)
        => pieces.Select(piece => piece.ItemName()).OfType<string>().GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(named => named.Count() > 1)?.Key is { } twice
            ? $"name the work item '{twice}' twice, and a work item is named by one piece of an update"
            : null;

    /// <summary>The kinds a piece is, as every sentence about a piece's kind says them.</summary>
    private static string KindsSaid => $"{string.Join(", ", FlowUpdatePieceKinds.All.SkipLast(1).Select(kind => $"'{kind}'"))} or '{FlowUpdatePieceKinds.All[^1]}'";
}

/// <summary>
/// Who a plan update told something, and how: one reason step, what it was told - the pieces that named its work -
/// and the way it reached it, <c>by a message into its conversation</c>, <c>with the answer</c> or <c>in its
/// prompt</c>.
/// </summary>
/// <param name="Who">The step instance that was told.</param>
/// <param name="What">What it was told, as the entry names it.</param>
/// <param name="How">The way it reached it.</param>
public sealed record FlowUpdateTold(string Who, string What, string How);

/// <summary>
/// A told piece a plan update could not deliver: the work under way it named, and why nothing was told - the work
/// had ended meanwhile, or it is no id of the run. The step that goes on with the work has it in its prompt, where
/// one does.
/// </summary>
/// <param name="For">The work the piece named.</param>
/// <param name="Why">Why nothing was told.</param>
public sealed record FlowNotDelivered(string For, string Why);

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
/// <param name="Pieces">
/// Each piece of the update as it was placed: what it is, its work item, where it went and what carries it - written
/// as the updated plan stood, before the update was done; empty where the planning concluded no piece.
/// </param>
/// <param name="Told">Who was told what, and how: each reason step a piece of the update told, with the way it reached it.</param>
/// <param name="NotConcerned">Each reason step that was in flight when the pieces were placed and that no piece named: sent nothing.</param>
/// <param name="NotDelivered">Each told piece that could not be delivered, with why: the work it named had ended, or it is no id of the run.</param>
/// <param name="AddedIteration">The iteration an update added at a loop's bound, where it added one: the iteration's own id; null where it added none.</param>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text = null,
    IReadOnlyList<FlowUpdatePiece>? Pieces = null,
    IReadOnlyList<FlowUpdateTold>? Told = null,
    IReadOnlyList<string>? NotConcerned = null,
    IReadOnlyList<FlowNotDelivered>? NotDelivered = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AddedIteration = null)
{
    /// <summary>Each piece of the update, as it was placed; empty where the planning concluded no piece.</summary>
    public IReadOnlyList<FlowUpdatePiece> ThePieces => Pieces ?? [];

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
            : ThePieces.Select((piece, at) => (Problem: piece.Problem(), At: at + 1)).FirstOrDefault(piece => piece.Problem is not null) is { Problem: { } wrong } first ? $"its piece {first.At.ToString(CultureInfo.InvariantCulture)} {wrong}"
            : FlowUpdatePiece.NamedTwice(ThePieces) is { } twice ? $"its pieces {twice}"
            : Told?.Any(told => !FlowInstanceId.IsWellFormed(told.Who) || string.IsNullOrWhiteSpace(told.What) || string.IsNullOrWhiteSpace(told.How)) == true ? "one it told holds no step, nothing told or no way it reached it"
            : Told?.GroupBy(told => told.Who, StringComparer.Ordinal).Any(group => group.Count() > 1) == true ? "one step was told twice by one update"
            : NotConcerned?.Any(concerned => !FlowInstanceId.IsWellFormed(concerned)) == true ? "one it names as not concerned is not an instance's id"
            : NotDelivered?.Any(not => string.IsNullOrWhiteSpace(not.For) || string.IsNullOrWhiteSpace(not.Why)) == true ? "one not delivered names neither the work nor why"
            : AddedIteration is not null && !FlowInstanceId.IsWellFormed(AddedIteration) ? $"'{AddedIteration}' is not an instance's id"
            : null;

    /// <summary>The piece of this update that names the work item <paramref name="name"/>, where one does; null where none does.</summary>
    public FlowUpdatePiece? PieceOf(string name)
        => ThePieces.FirstOrDefault(piece => string.Equals(piece.ItemName(), name, StringComparison.Ordinal));
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
            [.. flow.Steps.Select(step => new FlowStepAt(step.Step, step.State, step.Group, step.Iteration, step.Index, step.Visit))],
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
    /// decided where an update is taken. A work-item group with no loop around it runs one round of its own and at
    /// most the rounds <c>amend.maxAddedIterations</c> lets updates add, each a visit of the group run again for an
    /// update's work; once the last it may run has ended, it is past its work items as a loop at its bound is.
    /// </summary>
    private static string? PastItsWorkItems(WorkflowDefinition definition, string run, IReadOnlyList<FlowStepAt> steps, string what)
    {
        if (definition.Amend is not { } amend || StepOf(definition, amend.WhenAddScope) is null)
        {
            return null;
        }

        var group = ParentOf(amend.WhenAddScope);
        var loopPath = group is null ? null : LoopAround(definition, group);

        if (loopPath is null && group is not null)
        {
            // The group's own visits, never the instances of its work items: the round it ran, and each run again.
            var visits = steps.Where(step => step.Group && step.Step == group && step.Index is null).ToList();
            var runs = 1 + amend.AddedIterationsAllowed;

            if (visits.Count < runs
                || visits.MaxBy(step => step.Visit) is not { State: nameof(FlowInstanceState.Done) or nameof(FlowInstanceState.Failed) or nameof(FlowInstanceState.Skipped) })
            {
                return null;
            }

            return $"run '{run}' is past the work items of '{group}', which has run {visits.Count.ToString(CultureInfo.InvariantCulture)} round(s), the last it can run (1 of its own, and the {amend.AddedIterationsAllowed.ToString(CultureInfo.InvariantCulture)} that amend.maxAddedIterations lets updates add), with no loop around it: nothing would carry the change out. Start a run for the {what} once this one has ended";
        }

        if (loopPath is null || StepOf(definition, loopPath) is not { Repeat: { MaxIterations: { } bound } } loop)
        {
            return null;
        }

        var rounds = steps.Where(step => step.Group && step.Step == group).ToList();
        var last = rounds.Max(step => step.Iteration) ?? 0;
        var allowed = bound + amend.AddedIterationsAllowed;

        if (last < allowed || rounds.Any(step => step.Iteration == last && step.State is not (nameof(FlowInstanceState.Done) or nameof(FlowInstanceState.Failed) or nameof(FlowInstanceState.Skipped))))
        {
            return null;
        }

        return $"run '{run}' is past the work items of round {last.ToString(CultureInfo.InvariantCulture)} of '{loop.Name}', the last it can run ({bound.ToString(CultureInfo.InvariantCulture)} by its own bound, and the {amend.AddedIterationsAllowed.ToString(CultureInfo.InvariantCulture)} that amend.maxAddedIterations lets updates add): nothing would carry the change out. Start a run for the {what} once this one has ended";
    }

    /// <summary>
    /// The path of the nearest <c>repeat</c> around the group at <paramref name="groupPath"/>: the loop whose next
    /// iteration work is owed to where the group's round has ended; null where no repeat is around it.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="groupPath">The path of the group, the work-item group.</param>
    internal static string? LoopAround(WorkflowDefinition definition, string groupPath)
    {
        for (var around = ParentOf(groupPath); around is not null; around = ParentOf(around))
        {
            if (StepOf(definition, around) is { Repeat: not null })
            {
                return around;
            }
        }

        return null;
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
/// <param name="Index">The index of the work item an instance of a <c>forEach</c> group carries; null for the group's own visit and for every other step.</param>
/// <param name="Visit">Which visit of its step in its scope the instance is, from 1: a group run again for a plan update is its next visit.</param>
public readonly record struct FlowStepAt(string Step, string State, bool Group, int? Iteration, int? Index = null, int Visit = 1);
