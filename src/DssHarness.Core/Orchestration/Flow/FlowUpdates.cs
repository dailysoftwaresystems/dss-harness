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

    /// <summary>
    /// The check that opens a round found what does not pass: the round's items, with those updates owe it, go back to
    /// the planner before any of them begins. No person raises it.
    /// </summary>
    public const string PlanCheck = "planCheck";

    /// <summary>Every cause a plan update is raised by, in the order the report lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Amendment, ChangedAnswer, PlanCheck];
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

    /// <summary>
    /// The ids of the issues whose fix the work item <paramref name="item"/> needs, as its <c>needs</c> names them - each
    /// as <c>{run.preExisting}</c> words an issue, each once, in its order; none where it names none. The one reading of
    /// the member, which the plan's check holds to the issues the recovery takes - being fixed or fixed, or joined to one
    /// still being triaged - and the engine holds the item by until each fix has ended (<see cref="FlowIssueHold.NeededBy"/>);
    /// an entry that is not a text that is not blank
    /// reads as none.
    /// </summary>
    /// <param name="item">The work item, as a plan, a piece or an instance of the work-item group holds it.</param>
    public static IReadOnlyList<string> NeedsOf(JsonNode? item)
        => item is JsonObject held && held["needs"] is JsonArray needs
            ? [.. needs.Select(need => need is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) ? id : null).OfType<string>().Distinct(StringComparer.Ordinal)]
            : [];
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
/// <param name="Withdrawal">
/// Whether the piece, a <c>removed</c> one owed to a round that has not opened (<paramref name="OwedTo"/>), takes its
/// work item out of the list that round opens with - an item of the list the check that opens it was opening it with,
/// which no instance carries: that check drops it from its items, the round never expands it and nothing undoes it, for
/// nothing was built. True for such a piece alone; null for every other, and in every record written before it was
/// kept, which reads as before.
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Again = null,
    bool? Withdrawal = null)
{
    /// <summary>The name of the work item the piece is, where it is one; null where it is not.</summary>
    public string? ItemName()
        => Kind == FlowUpdatePieceKinds.Told ? null : FlowWorkItems.NameOf(Item);

    /// <summary>
    /// Whether the piece is work owed still: it was owed to a round when it was placed, has joined none since, has not
    /// ended, and is work - no withdrawal from a round's list (<see cref="IsWithdrawing"/>) - what the check that opens
    /// the next round is handed to join its own items, and what makes the loop around the round run again.
    /// </summary>
    public bool IsOwed => OwedTo is not null && Placed is null && Ended is null && Withdrawal != true;

    /// <summary>
    /// Whether the piece is a withdrawal owed still (<see cref="Withdrawal"/>): the round it is owed to has not opened,
    /// so the check that opens it is handed its name to drop from its items, and the round ends it as it opens without
    /// it. It makes no loop run again: it takes work away, and adds none.
    /// </summary>
    public bool IsWithdrawing => Withdrawal == true && OwedTo is not null && Placed is null && Ended is null;

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
            : Withdrawal is not null && (Withdrawal != true || Kind != FlowUpdatePieceKinds.Removed || OwedTo is null) ? "is a withdrawal, and only a removed piece owed to a round that has not opened takes its item out of that round's list"
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
/// the question whose answer was changed with the answer before and the answer now; a plan check holds the check that
/// raised it - the check that opens a round - and the problems it found, and needs neither a person nor a window. Of
/// where it stands, an update taken where no plan holds it yet is <see cref="FlowUpdateStates.DoneAtOnce"/>; one taken
/// where a plan has is <see cref="FlowUpdateStates.UnderWay"/> - the run held while the step the <c>amend</c> block
/// names plans it, its instance named here - until the updated plan stands (<see cref="FlowUpdateStates.Done"/>) or its
/// planning was given up (<see cref="FlowUpdateStates.Failed"/>, saying why no plan holds the change); one the run
/// cannot take is <see cref="FlowUpdateStates.Refused"/>.
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
/// <param name="Check">
/// The instance of the check that raised it, where one did - the check that opens a round, paused until the updated
/// plan stands; null for every other cause.
/// </param>
/// <param name="Problems">What the check found, each problem as its output holds it; null for every other cause.</param>
/// <param name="Why">What the run says of the update where its state alone cannot: why one was refused, or given up.</param>
/// <param name="Step">
/// The instance of the step the <c>amend</c> block names that plans this update, where the run has raised one; null
/// until it has. How that visit ended is its own step's record, which this entry names and does not copy.
/// </param>
/// <param name="Then">
/// The step the <c>amend</c> block's <c>then</c> names, written once the update's redirect to it is decided - after the
/// redirect's own write, which is the decision: a resume that finds the redirect owed and this null reads the then decided
/// and writes it, and one an older build wrote here before a redirect it never wrote is read as undecided; null until one
/// was, and where the block names none.
/// </param>
/// <param name="Text">The text given with a changed answer, where its question takes one; null where none was.</param>
/// <param name="Pieces">
/// Each piece of the update as it was placed: what it is, its work item, where it went and what carries it - written
/// as the updated plan stood, before the update was done; empty where the planning concluded no piece.
/// </param>
/// <param name="Told">Who was told what, and how: each reason step a piece of the update told, with the way it reached it.</param>
/// <param name="NotConcerned">Each reason step that was in flight when the pieces were placed and that no piece named: sent nothing.</param>
/// <param name="NotDelivered">Each told piece that could not be delivered, with why: the work it named had ended, or it is no id of the run.</param>
/// <param name="AddedIteration">
/// What the update added for its work, where it added one: the iteration it added at a loop's bound (<c>rounds#3</c>), or
/// the round it added to a work-item group with no loop around it - the group's visit that runs it (<c>build.items+2</c>);
/// null where it added none.
/// </param>
/// <param name="AskedAtBound">
/// The loop whose bound question carries the work the update owes it, where the run had been given every iteration
/// <c>amend.maxAddedIterations</c> allows when the work was owed: no iteration is added for it, the loop reaches its
/// bound and asks whatever its until says, and the person's answer decides whether the loop runs on to carry it.
/// Written once that question is asked, never before, so no later session runs again a loop its answer ended; null
/// where no loop was asked for it.
/// </param>
/// <param name="AddedWithdrawnBy">
/// The update that took back the iteration or round <paramref name="AddedIteration"/> names before it opened: its
/// withdrawal left this update owing nothing, so nothing is owed that iteration or round - it is never made, never
/// counted against <c>amend.maxAddedIterations</c>, and a loop's bound reads as it did before it was added. Null where
/// none was taken back, and in every record written before it was kept, which reads as before.
/// </param>
/// <param name="CheckedAgain">
/// The visit of the check that opens a round whose list this update changed: it had joined the work updates owed that
/// round, which this update withdrew, changed or added to before the round's work-item group expanded, so the check is drawn again with
/// every step its scope runs after it - as a redirect to it draws them - and the round opens with what the updates owe it
/// now. Written with the update's end, before the redirect is decided, so a session that takes the run up decides it
/// where it was not; null where the update changed no work a check had joined, and in every record written before it was
/// kept, which reads as before.
/// </param>
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
    string? Check = null,
    IReadOnlyList<string>? Problems = null,
    string? Why = null,
    string? Step = null,
    string? Then = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text = null,
    IReadOnlyList<FlowUpdatePiece>? Pieces = null,
    IReadOnlyList<FlowUpdateTold>? Told = null,
    IReadOnlyList<string>? NotConcerned = null,
    IReadOnlyList<FlowNotDelivered>? NotDelivered = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AddedIteration = null,
    string? AskedAtBound = null,
    string? AddedWithdrawnBy = null,
    string? CheckedAgain = null)
{
    /// <summary>Each piece of the update, as it was placed; empty where the planning concluded no piece.</summary>
    public IReadOnlyList<FlowUpdatePiece> ThePieces => Pieces ?? [];

    /// <summary>
    /// The iteration or round this update added that stands: <see cref="AddedIteration"/>, where no later update took it
    /// back before it opened (<see cref="AddedWithdrawnBy"/>); null otherwise. The one reading every count of the
    /// iterations updates added, and every reading of a round an update added that is still to open, makes.
    /// </summary>
    public string? StandingIteration => AddedWithdrawnBy is null ? AddedIteration : null;

    /// <summary>What is wrong with this entry; null when nothing is.</summary>
    public string? Problem()
        => !FlowUpdateIds.IsId(Id) ? $"'{Id}' is not an update's id"
            : !FlowUpdateCauses.All.Contains(Cause, StringComparer.Ordinal) ? $"'{Cause}' is not what raises a plan update"
            : !FlowUpdateStates.All.Contains(State, StringComparer.Ordinal) ? $"'{State}' is not a state an update stands in"
            : Cause == FlowUpdateCauses.Amendment && string.IsNullOrWhiteSpace(Amendment) ? "an amendment is recorded with its text, and its text is empty"
            : Cause == FlowUpdateCauses.ChangedAnswer && !FlowQuestionIds.IsWellFormed(Question) ? $"'{Question}' is not a question's id"
            : Cause == FlowUpdateCauses.ChangedAnswer && (string.IsNullOrWhiteSpace(FromAnswer) || string.IsNullOrWhiteSpace(ToAnswer)) ? "a changed answer is recorded with the answer before and the answer now"
            : Cause == FlowUpdateCauses.PlanCheck && (Check is not { } check || !FlowInstanceId.IsWellFormed(check)) ? $"'{Check}' is not an instance's id"
            : Cause == FlowUpdateCauses.PlanCheck && (Problems is not { Count: > 0 } || Problems.Any(problem => string.IsNullOrWhiteSpace(problem))) ? "a plan check is recorded with the problems it found, and it holds none"
            : Cause != FlowUpdateCauses.PlanCheck && Check is not null ? "it names the check that raised it, and it was no plan check that did"
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
            : AskedAtBound is not null && !FlowInstanceId.IsWellFormed(AskedAtBound) ? $"'{AskedAtBound}' is not an instance's id"
            : AddedWithdrawnBy is not null && (!FlowUpdateIds.IsId(AddedWithdrawnBy) || AddedIteration is null) ? $"it names '{AddedWithdrawnBy}' as taking back what it added, and it is no update's id or the update added nothing"
            : CheckedAgain is not null && !FlowInstanceId.IsWellFormed(CheckedAgain) ? $"'{CheckedAgain}' is not an instance's id"
            : null;

    /// <summary>The piece of this update that names the work item <paramref name="name"/>, where one does; null where none does.</summary>
    public FlowUpdatePiece? PieceOf(string name)
        => ThePieces.FirstOrDefault(piece => string.Equals(piece.ItemName(), name, StringComparison.Ordinal));

    /// <summary>
    /// How the run took this update, as the person who handed it over reads it: queued behind the update under way,
    /// being planned while the run is held, or done at once where no plan held it yet.
    /// </summary>
    public string HowTaken()
        => State switch
        {
            FlowUpdateStates.Queued => $"it is recorded as update {Id}, queued behind the update under way, and planned when that one is done",
            FlowUpdateStates.UnderWay => $"it is update {Id}, being planned: the run is held while '{Step ?? "the step the amend block names"}' plans it",
            FlowUpdateStates.Done => $"it is update {Id}, done at once, for no plan held it yet: every step that starts reads the request with its amendments",
            _ => $"it is update {Id}, {State}",
        };
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
/// What a run refuses a change it is given - an amendment of its request, or a changed answer - before anything of it
/// is carried out: the one owner of the gates every path into a plan update passes, the command's where a person gives
/// one and the engine's where it takes one. Nothing the change asks for is made by a refusal. Where a person gives the
/// change to a command, the command says why and records nothing; where the engine refuses one the run's records hold
/// already - an amendment handed over in the inbox, or a change queued behind an update under way - its entry is
/// recorded refused, with why, so the run says what became of it and never weighs it again.
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
    /// <param name="updates">The run's plan updates, as they stand; null where it holds none.</param>
    /// <param name="cause">What raises the update: one of <see cref="FlowUpdateCauses"/>'s names, for the words that name it.</param>
    public static string? Refused(WorkflowDefinition definition, FlowFile flow, FlowUpdatesFile? updates, string cause)
        => Refused(
            definition,
            flow.Run,
            flow.Status,
            [.. flow.Steps.Select(step => new FlowStepAt(step.Step, step.State, step.Group, step.Iteration, step.Index, step.Visit, step.Instance, step.Counted))],
            updates?.Updates ?? [],
            cause);

    /// <summary>
    /// Why the run refuses a change, as its steps stand - the same gates over the engine's own view of them; null
    /// where it takes it.
    /// </summary>
    /// <param name="definition">The definition as the run began with it, whose <c>amend</c> block says what a change passes.</param>
    /// <param name="run">The run's name.</param>
    /// <param name="status">How the run stands: one of <see cref="FlowRunStatus"/>'s names.</param>
    /// <param name="steps">Every step instance the run holds, as its records read them.</param>
    /// <param name="updates">Every plan update the run has taken, in the order taken.</param>
    /// <param name="cause">What raises the update: one of <see cref="FlowUpdateCauses"/>'s names, for the words that name it.</param>
    public static string? Refused(WorkflowDefinition definition, string run, string status, IReadOnlyList<FlowStepAt> steps, IReadOnlyList<FlowUpdateEntry> updates, string cause)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (cause == FlowUpdateCauses.PlanCheck)
        {
            // No person raises it, and it needs neither an input nor a window: the table of what is refused is not
            // its door, for it is how the work of a round is made to fit together before any of it begins.
            return null;
        }

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

        return PastItsWorkItems(definition, run, steps, updates, what);
    }

    /// <summary>
    /// Why nothing the run could run would carry a change out - the work items of its last round past - where that
    /// is settled by the definition's own bounds; null where it is not. A loop the definition leaves open - an
    /// <c>until</c> with no <c>maxIterations</c> - is never past its work items by this reckoning: what it carries is
    /// decided where an update is taken. A work-item group with no loop around it is past its work items once its newest
    /// round has ended and it runs no round more (<see cref="NoRoundMore"/>), as a loop at its bound is.
    /// </summary>
    private static string? PastItsWorkItems(WorkflowDefinition definition, string run, IReadOnlyList<FlowStepAt> steps, IReadOnlyList<FlowUpdateEntry> updates, string what)
    {
        if (definition.Amend is not { } amend || StepOf(definition, amend.WhenAddScope) is null)
        {
            return null;
        }

        var group = ParentOf(amend.WhenAddScope);
        var loopPath = group is null ? null : LoopAround(definition, group);

        if (loopPath is null && group is not null)
        {
            // The group's newest visit - never an instance of its work items - is the round a change would join: where
            // it is still open, the change joins it.
            if (NewestOf(steps, group) is not { State: nameof(FlowInstanceState.Done) or nameof(FlowInstanceState.Failed) or nameof(FlowInstanceState.Skipped) }
                || NoRoundMore(definition, group, steps, updates) is not { } why)
            {
                return null;
            }

            if (RoundNotOpened(group, steps, updates) is { } checkedRound && updates.First(update => update.StandingIteration == checkedRound) is var adding)
            {
                // A round added whose check has run opens with the work that check was handed: the change joins it once
                // it has opened.
                return $"run '{run}' has checked the work of {FlowReports.IterationSaidOf(checkedRound)}, which update {adding.Id} added, and that round opens with that work alone: nothing would carry the change out before it has opened. Give the {what} again once it has opened, and it joins it";
            }

            return $"run '{run}' is past the work items of '{group}', with no loop around it: its last round has ended, and {why} - nothing would carry the change out. Start a run for the {what} once this one has ended";
        }

        if (loopPath is null || StepOf(definition, loopPath) is not { Repeat: { MaxIterations: { } bound } } loop)
        {
            return null;
        }

        // The loop's rounds counted as the engine counts its bound: from the iteration a plan update was planned in, where
        // one was (FlowLoopCount).
        var rounds = steps.Where(step => step.Group && step.Step == group).ToList();
        var last = rounds.Max(step => step.Iteration) ?? 0;
        var counted = steps.LastOrDefault(step => step.Group && step.Step == loopPath && step.Iteration is null).Counted;
        var allowed = bound + amend.AddedIterationsAllowed;

        if (FlowLoopCount.Made(last, counted) < allowed || rounds.Any(step => step.Iteration == last && step.State is not (nameof(FlowInstanceState.Done) or nameof(FlowInstanceState.Failed) or nameof(FlowInstanceState.Skipped))))
        {
            return null;
        }

        return $"run '{run}' is past the work items of round {last.ToString(CultureInfo.InvariantCulture)} of '{loop.Name}', the last it can run ({bound.ToString(CultureInfo.InvariantCulture)} by its own bound, and the {amend.AddedIterationsAllowed.ToString(CultureInfo.InvariantCulture)} that amend.maxAddedIterations lets updates add): nothing would carry the change out. Start a run for the {what} once this one has ended";
    }

    /// <summary>
    /// Why the work-item group at <paramref name="group"/>, which has no loop around it, runs no round more for the work
    /// of a plan update once its newest round has ended; null where it runs one more. The one owner of that rule: the
    /// gates read it where a change is given, and the engine where an update's plan stands and where it adds the round.
    /// Such a group runs one round of its own and at most the rounds <c>amend.maxAddedIterations</c> lets updates add -
    /// counted by the rounds the updates' entries name and that stand, never by the group's visits, which a redirect adds
    /// to as well, nor by one a later update took back before it opened (<see cref="FlowUpdateEntry.StandingIteration"/>)
    /// - and only where every group around it draws again: one still open, or one that ended done, which is in progress
    /// again for the round. One that ended failed, skipped or never run draws nothing more (<see cref="AroundEnded"/>).
    /// A round an update added that has not opened yet runs for every change given before the check that opens it has
    /// started, which joins it (<see cref="PendingRound"/>): the work owed to the group is handed whole to that check.
    /// Once the check has started, the round opens with the work it was handed alone, and no round more is run for a
    /// change before it has opened.
    /// </summary>
    /// <param name="definition">The definition as the run began with it, whose <c>amend</c> block bounds the rounds.</param>
    /// <param name="group">The path of the work-item group.</param>
    /// <param name="steps">Every step instance the run holds.</param>
    /// <param name="updates">Every plan update the run has taken.</param>
    public static string? NoRoundMore(WorkflowDefinition definition, string group, IReadOnlyList<FlowStepAt> steps, IReadOnlyList<FlowUpdateEntry> updates)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(updates);

        if (AroundEnded(group, steps) is { } ended)
        {
            return ended;
        }

        if (RoundNotOpened(group, steps, updates) is { } added)
        {
            return PendingRound(definition, group, steps, updates) is not null
                ? null
                : $"{FlowReports.IterationSaidOf(added)}, which an update added, has been checked with the work it carries, and opens with that alone";
        }

        var allowed = definition.Amend?.AddedIterationsAllowed ?? 0;

        return updates.Count(update => update.StandingIteration is not null) >= allowed
            ? $"updates have added the {allowed.ToString(CultureInfo.InvariantCulture)} round(s) amend.maxAddedIterations allows"
            : null;
    }

    /// <summary>
    /// The round a plan update added to the work-item group at <paramref name="group"/>, which has no loop around it,
    /// that has not opened yet, by the id of the group's visit it will be - the visit an update's entry names and that
    /// stands (<see cref="FlowUpdateEntry.StandingIteration"/>), which the run's steps do not hold yet; null where none
    /// waits. None waits where a group around it draws nothing more, for that round never opens (<see cref="AroundEnded"/>),
    /// nor where a later update took it back before it opened. The
    /// engine reads it where it adds a round, adding none while one waits, and where a resume draws the group again for
    /// the round an update's entry names.
    /// </summary>
    /// <param name="group">The path of the work-item group.</param>
    /// <param name="steps">Every step instance the run holds, each by its instance's id.</param>
    /// <param name="updates">Every plan update the run has taken.</param>
    public static string? RoundNotOpened(string group, IReadOnlyList<FlowStepAt> steps, IReadOnlyList<FlowUpdateEntry> updates)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(updates);

        return AroundEnded(group, steps) is not null
            ? null
            : updates.Select(update => update.StandingIteration).FirstOrDefault(added => added is not null
                && string.Equals(FlowInstanceId.PathOf(added), group, StringComparison.Ordinal)
                && !steps.Any(step => string.Equals(step.Instance, added, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The round a plan update added to the work-item group at <paramref name="group"/> that a change given now joins:
    /// one that has not opened yet (<see cref="RoundNotOpened"/>) and whose opening check - the visit of the check that
    /// opens the group's rounds drawn after the group's newest visit, which the steps hold in the order the run made
    /// them - has not started, so it is still to be handed the work owed to the group, this change's among it; null
    /// where none is. The one owner of that rule: the gates, <see cref="NoRoundMore"/> and so the engine read it.
    /// </summary>
    /// <param name="definition">The definition, whose declarations say which check opens the group's rounds.</param>
    /// <param name="group">The path of the work-item group.</param>
    /// <param name="steps">Every step instance the run holds, in the order the run made them.</param>
    /// <param name="updates">Every plan update the run has taken.</param>
    public static string? PendingRound(WorkflowDefinition definition, string group, IReadOnlyList<FlowStepAt> steps, IReadOnlyList<FlowUpdateEntry> updates)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (RoundNotOpened(group, steps, updates) is not { } added)
        {
            return null;
        }

        var parent = ParentOf(group);

        if (StepOf(definition, group) is not { } declared
            || OpeningCheckOf(parent is null ? definition.Steps : StepOf(definition, parent)?.Steps ?? [], declared) is not { } check)
        {
            return added;
        }

        var checkPath = parent is null ? check.Name : $"{parent}.{check.Name}";
        var newest = -1;

        for (var at = 0; at < steps.Count; at++)
        {
            if (steps[at] is { Group: true, Index: null } visit && string.Equals(visit.Step, group, StringComparison.Ordinal))
            {
                newest = at;
            }
        }

        return steps.Skip(newest + 1).Any(step => string.Equals(step.Step, checkPath, StringComparison.Ordinal) && step.State != nameof(FlowInstanceState.NotStarted))
            ? null
            : added;
    }

    /// <summary>
    /// The <c>validate-plan</c> step among <paramref name="siblings"/> whose output the <c>forEach</c> of the work-item group
    /// <paramref name="group"/> reads - the check that opens its rounds, which is handed the work plan updates owe them;
    /// null where the group expands from anything else. The one owner of that rule, for the engine and the gates.
    /// </summary>
    /// <param name="siblings">The declarations beside the group.</param>
    /// <param name="group">The work-item group's declaration.</param>
    public static WorkflowStep? OpeningCheckOf(IReadOnlyList<WorkflowStep> siblings, WorkflowStep group)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentNullException.ThrowIfNull(group);

        return group.ForEach is { } items
            ? siblings.FirstOrDefault(step => step.Type == "validate-plan"
                && WorkflowExpressions.ReadsOf(items).Any(read => string.Equals(read.Root, step.Name, StringComparison.Ordinal)))
            : null;
    }

    /// <summary>
    /// The path of the work-item group whose round <paramref name="check"/> opens; null where it opens none. A check
    /// opens a round where it is the check a <c>forEach</c> group declared beside it opens its rounds with
    /// (<see cref="OpeningCheckOf"/>) and that group runs in rounds: declared in an iteration of a <c>repeat</c>, which
    /// runs it again each round, or the group plan updates owe their work to (the group of <c>amend.whenAddScope</c>), to
    /// which an update adds a round where no loop is around it - whichever opens the round. Never the plan's check, a plan
    /// update's or a recovery's, which open no round of their own. Where the check opens rounds of two groups, the one plan
    /// updates owe their work to is named. The one owner of which check opens a round: the engine hands that check the
    /// issues the run owes its next round to hold its items to - each settled, the settling items first - always, as it
    /// hands them to the plan's check while the round its items open is the next, and, where its group is the one updates
    /// owe, the work they owe it, whether it carries that alone, and the plan update its problems raise.
    /// </summary>
    /// <param name="definition">The definition: its steps, where the check is declared at the flow's top, and its <c>amend</c> block, which names the group plan updates owe their work to.</param>
    /// <param name="scopePath">The declaration path of the group the check is declared in; null at the flow's top.</param>
    /// <param name="scope">The group the check is declared in, whose steps are beside it; null at the flow's top.</param>
    /// <param name="check">The check's declaration.</param>
    public static string? RoundOpenedBy(WorkflowDefinition definition, string? scopePath, WorkflowStep? scope, WorkflowStep check)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(check);

        if (check.Type != "validate-plan")
        {
            return null;
        }

        var siblings = scope?.Steps ?? (scopePath is null ? definition.Steps : []);
        var updated = definition.Amend is { } amend ? ParentOf(amend.WhenAddScope) : null;
        string? looped = null;

        foreach (var group in siblings.Where(group => OpeningCheckOf(siblings, group) is { } opening && string.Equals(opening.Name, check.Name, StringComparison.Ordinal)))
        {
            var path = scopePath is null ? group.Name : $"{scopePath}.{group.Name}";

            if (string.Equals(path, updated, StringComparison.Ordinal))
            {
                return path;
            }

            looped ??= scope?.Repeat is not null ? path : null;
        }

        return looped;
    }

    /// <summary>
    /// Whether <paramref name="check"/> checks the items a round of a work-item group opens with: some group's rounds
    /// open with them (<see cref="RoundsOpenedWith"/>). The one owner of which checks take an item that settles an issue
    /// the run owes now: the engine hands each of them the ids of those issues and every other check none, so an item that
    /// names an issue the run does not owe is refused wherever it is checked, and one that names an owed issue is refused
    /// by no check whose items a round opens with. Which of them hold their items to those issues - each settled, the
    /// settling items first - is the engine's to say as the run stands (<c>FlowCoordinator.HoldsTheOwedIssues</c>), for
    /// whether the round they open is the next one is the run's, never the declarations'.
    /// </summary>
    /// <param name="definition">The definition, whose declarations say which checks open a round and what each reads.</param>
    /// <param name="scopePath">The declaration path of the group the check is declared in; null at the flow's top.</param>
    /// <param name="scope">The group the check is declared in, whose steps are beside it; null at the flow's top.</param>
    /// <param name="check">The check's declaration.</param>
    public static bool ChecksTheItemsARoundOpensWith(WorkflowDefinition definition, string? scopePath, WorkflowStep? scope, WorkflowStep check)
        => RoundsOpenedWith(definition, scopePath, scope, check).Count > 0;

    /// <summary>
    /// The paths of the work-item groups whose rounds open with the items <paramref name="check"/> checks: the one whose
    /// round it opens (<see cref="RoundOpenedBy"/>), or each group whose opening check's <c>items</c> read a step the
    /// check's own <c>items</c> read - the plan's check, whose plan a round opens with, and so the check a plan update
    /// sends the run back to. None for a recovery's check, whose items the recovery builds and no round does, and none
    /// for a check whose items no round opens with. Each named once, in the order the definition declares the checks
    /// that open their rounds.
    /// </summary>
    /// <param name="definition">The definition, whose declarations say which checks open a round and what each reads.</param>
    /// <param name="scopePath">The declaration path of the group the check is declared in; null at the flow's top.</param>
    /// <param name="scope">The group the check is declared in, whose steps are beside it; null at the flow's top.</param>
    /// <param name="check">The check's declaration.</param>
    public static IReadOnlyList<string> RoundsOpenedWith(WorkflowDefinition definition, string? scopePath, WorkflowStep? scope, WorkflowStep check)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(check);

        if (RoundOpenedBy(definition, scopePath, scope, check) is { } own)
        {
            return [own];
        }

        // A recovery is known by its reserved name, which no step can take: the flow's own at the root, a used workflow's
        // under the step that uses it.
        if (check.Type != "validate-plan"
            || check.Items is not { } items
            || (scopePath?.Split('.').Contains(FlowScope.Recovery, StringComparer.Ordinal) ?? false))
        {
            return [];
        }

        var mine = StepsRead(definition, scopePath, items).ToHashSet(StringComparer.Ordinal);
        var groups = new List<string>();

        foreach (var (path, declared) in FlowScope.Declared(definition))
        {
            var around = FlowScope.ScopeOf(path);

            if (declared.Items is { } read
                && RoundOpenedBy(definition, around, around is null ? null : FlowScope.At(around, definition)?.Step, declared) is { } group
                && StepsRead(definition, around, read).Any(mine.Contains))
            {
                groups.Add(group);
            }
        }

        return [.. groups.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The paths of the steps whose output <paramref name="expression"/>, read in the scope at <paramref name="scopePath"/>,
    /// reads, each found as <see cref="FlowScope.Read"/> finds a read's step: a read of <c>previous</c> by the names after
    /// it, as the iteration before is read; none for a read of the run's other own names.
    /// </summary>
    private static IEnumerable<string> StepsRead(WorkflowDefinition definition, string? scopePath, string expression)
    {
        foreach (var read in WorkflowExpressions.ReadsOf(expression))
        {
            IReadOnlyList<string> names = read.Root == "previous" ? [.. read.Names.Skip(1)] : WorkflowExpressions.IsRoot(read.Root) ? [] : read.Names;

            if (FlowScope.Read(names, definition, scopePath, out _) is { } found)
            {
                yield return found.Step.Path;
            }
        }
    }

    /// <summary>
    /// Why a group around the group at <paramref name="group"/> draws nothing more - its newest visit ended failed,
    /// skipped or never run - so no visit of the group is drawn again in it; null where every one still draws, open or
    /// ended done.
    /// </summary>
    /// <param name="group">The path of the group.</param>
    /// <param name="steps">Every step instance the run holds.</param>
    public static string? AroundEnded(string group, IReadOnlyList<FlowStepAt> steps)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(steps);

        for (var around = ParentOf(group); around is not null; around = ParentOf(around))
        {
            var said = NewestOf(steps, around)?.State switch
            {
                nameof(FlowInstanceState.Failed) => "failed",
                nameof(FlowInstanceState.Skipped) => "skipped",
                nameof(FlowInstanceState.NotRun) => "never run",
                _ => null,
            };

            if (said is not null)
            {
                return $"'{around}', the group around it, ended {said} and draws nothing more";
            }
        }

        return null;
    }

    /// <summary>The newest visit of the group at <paramref name="group"/> - its own record, never an instance of its work items; null where the run has drawn none.</summary>
    private static FlowStepAt? NewestOf(IReadOnlyList<FlowStepAt> steps, string group)
        => steps.Where(step => step.Group && step.Step == group && step.Index is null).Select(step => (FlowStepAt?)step).MaxBy(step => step!.Value.Visit);

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

/// <summary>One step instance as the gates read it: its id, its declaration's path, its state, and - a group's - its round.</summary>
/// <param name="Step">The declaration's path among the flow.</param>
/// <param name="State">How the instance stands, as the engine's identifiers spell it.</param>
/// <param name="Group">Whether this is a group's own record.</param>
/// <param name="Iteration">The iteration of the repeat this instance is one of; null outside every one.</param>
/// <param name="Index">The index of the work item an instance of a <c>forEach</c> group carries; null for the group's own visit and for every other step.</param>
/// <param name="Visit">Which visit of its step in its scope the instance is, from 1: a group run again for a plan update is its next visit.</param>
/// <param name="Instance">The instance's id: what tells a visit an update's entry names from one the run holds.</param>
/// <param name="Counted">A repeat's own: where its bound counts from, once a plan update was planned in it (<see cref="FlowLoopCount"/>); null for every other.</param>
public readonly record struct FlowStepAt(string Step, string State, bool Group, int? Iteration, int? Index = null, int Visit = 1, string? Instance = null, FlowLoopCount? Counted = null);
