using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The records of a plan update: what an update is in <c>updates.json</c> - its id, cause and state, held to
/// their own rules - the request as its amendments are read with it, the gates every change passes as both the
/// command and the engine read them, and the store's one writer of an entry under the run's own lock.
/// </summary>
public sealed class FlowUpdatesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DateTimeOffset At(int minute) => new(2026, 10, 29, 14, minute, 0, TimeSpan.Zero);

    /// <summary>An update's entry: its id by the order taken, its cause among the two that raise one, and its state one of the five an update stands in; a refused one says why, and no two share an id or an inbox entry.</summary>
    [Fact]
    public void AnEntry_IsHeldToItsOwnShape()
    {
        Assert.Null(new FlowUpdateEntry("u1", "amendment", "done", At(0), Amendment: "narrow it").Problem());
        Assert.Null(new FlowUpdateEntry("u2", "changedAnswer", "queued", At(0), Question: "q4", FromAnswer: "go", ToAnswer: "stay").Problem());
        Assert.Null(new FlowUpdateEntry("u3", "changedAnswer", "refused", At(0), Question: "q4", FromAnswer: "go", ToAnswer: "stay", Why: "past its window").Problem());

        Assert.Equal("'x1' is not an update's id", new FlowUpdateEntry("x1", "amendment", "done", At(0), Amendment: "narrow it").Problem());
        Assert.Equal("'later' is not what raises a plan update", new FlowUpdateEntry("u1", "later", "done", At(0), Amendment: "narrow it").Problem());
        Assert.Equal("'open' is not a state an update stands in", new FlowUpdateEntry("u1", "amendment", "open", At(0), Amendment: "narrow it").Problem());
        Assert.Equal("an amendment is recorded with its text, and its text is empty", new FlowUpdateEntry("u1", "amendment", "done", At(0)).Problem());
        Assert.Equal("'x4' is not a question's id", new FlowUpdateEntry("u1", "changedAnswer", "done", At(0), Question: "x4", FromAnswer: "go", ToAnswer: "stay").Problem());
        Assert.Equal("a changed answer is recorded with the answer before and the answer now", new FlowUpdateEntry("u1", "changedAnswer", "done", At(0), Question: "q1", FromAnswer: "go").Problem());
        Assert.Equal("it says not why it was refused", new FlowUpdateEntry("u1", "amendment", "refused", At(0), Amendment: "narrow it").Problem());

        var twice = new FlowUpdatesFile(1, "r1", At(0),
        [
            new FlowUpdateEntry("u1", "amendment", "done", At(0), Amendment: "one"),
            new FlowUpdateEntry("u1", "amendment", "done", At(1), Amendment: "two"),
        ]);
        Assert.Equal("one id holds two updates", twice.Problem());

        var twoInbox = new FlowUpdatesFile(1, "r1", At(0),
        [
            new FlowUpdateEntry("u1", "amendment", "done", At(0), Amendment: "one", Inbox: "a"),
            new FlowUpdateEntry("u2", "amendment", "done", At(1), Amendment: "two", Inbox: "a"),
        ]);
        Assert.Equal("one inbox entry holds two updates", twoInbox.Problem());

        Assert.Equal("u2", new FlowUpdatesFile(1, "r1", At(0), [new FlowUpdateEntry("u1", "amendment", "done", At(0), Amendment: "one")]).NextId());
    }

    /// <summary>
    /// A piece is held to its own shape by one rule - the engine reading a planning step's conclusion, the check of a
    /// plan's scope and <c>updates.json</c> alike - and an entry to its pieces, the steps it told, the ones it did not
    /// concern, what it could not deliver and the iteration it added: each breach said of what breaks it.
    /// </summary>
    [Fact]
    public void APiece_IsHeldToItsOwnShape_AndAnEntryToItsPiecesAndWhatItTold()
    {
        static JsonObject Item(string name) => new() { ["name"] = name };
        var added = new FlowUpdatePiece("added", Item("one"), Changed: "one more item");

        Assert.Null(added.Problem());
        Assert.Null(new FlowUpdatePiece("told", For: "rounds.items[0].build", Changed: "the parser moved").Problem());
        Assert.Null((added with { Placed = "rounds.items[1]", CarriedBy = "rounds.items[1].build", Again = "rounds.items[2]" }).Problem());

        Assert.Equal("is of the kind 'moved', and a piece is one of 'added', 'changed', 'removed' or 'told'", (added with { Kind = "moved" }).Problem());
        Assert.Equal("says not what the update changes for its work, and a piece always does", (added with { Changed = " " }).Problem());
        Assert.Equal("is told, and names no work under way: a told piece tells work that is going on", new FlowUpdatePiece("told", Changed: "the parser moved").Problem());
        Assert.Equal("is told and holds a work item: it names work under way instead", new FlowUpdatePiece("told", Item("one"), For: "rounds.items[0].build", Changed: "the parser moved").Problem());
        Assert.Equal("is 'added', and holds no work item: a piece that carries a work item out holds one", (added with { Item = JsonValue.Create("one") }).Problem());
        Assert.Equal("holds a work item with no name, and a work item is known by its name", (added with { Item = new JsonObject { ["name"] = 5 } }).Problem());
        Assert.Equal("is placed in 'nowhere!', which is not an instance's id", (added with { Placed = "nowhere!" }).Problem());
        Assert.Equal("is owed to a round, and says not which", (added with { OwedTo = " " }).Problem());
        Assert.Equal("is carried by 'nobody!', which is not an instance's id", (added with { CarriedBy = "nobody!" }).Problem());
        Assert.Equal("is visited again in 'elsewhere!', which is not an instance's id", (added with { Again = "elsewhere!" }).Problem());

        // A planning step's conclusion is read by the same rule, and a name of another type is a piece the engine gives
        // the update up over, never a failure of whoever reads it.
        var read = FlowUpdatePiece.Read(new JsonObject { ["kind"] = "added", ["item"] = Item("one"), ["changed"] = "one more item", ["keep"] = "the lexer" }, out var none);
        Assert.Null(none);
        Assert.Equal(("added", "one", "one more item", "the lexer", null, null), (read!.Kind, read.ItemName(), read.Changed, read.Keep, read.Undo, read.For));
        Assert.Null(FlowUpdatePiece.Read(new JsonObject { ["kind"] = "added", ["item"] = new JsonObject { ["name"] = 5 }, ["changed"] = "one more item" }, out var unnamed));
        Assert.Equal("holds a work item with no name, and a work item is known by its name", unnamed);
        Assert.Null(FlowUpdatePiece.Read(new JsonObject { ["kind"] = 7 }, out var shapeless));
        Assert.Equal("is of the wrong shape: it is no object with a kind, and a piece is one of 'added', 'changed', 'removed' or 'told'", shapeless);
        Assert.Null(FlowWorkItems.NameOf(new JsonObject { ["name"] = 5 }));
        Assert.Equal("one", FlowWorkItems.NameOf(Item("one")));

        Assert.Null(FlowUpdatePiece.NamedTwice([added, added with { Kind = "changed" } with { Item = Item("two") }]));
        Assert.Equal("name the work item 'one' twice, and a work item is named by one piece of an update", FlowUpdatePiece.NamedTwice([added, added with { Kind = "changed" }]));

        var entry = new FlowUpdateEntry("u1", "amendment", "done", At(0), Amendment: "narrow it", Pieces: [added]);
        Assert.Null(entry.Problem());
        Assert.Null((entry with
        {
            Told = [new FlowUpdateTold("rounds.items[0].build", "the parser moved", "by a message into its conversation")],
            NotConcerned = ["rounds.items[1].build"],
            NotDelivered = [new FlowNotDelivered("rounds.items[2].build", "the work it named had ended")],
            AddedIteration = "rounds#2",
        }).Problem());

        Assert.Equal(
            "its piece 2 is of the kind 'moved', and a piece is one of 'added', 'changed', 'removed' or 'told'",
            (entry with { Pieces = [added with { Item = Item("two") }, added with { Kind = "moved" }] }).Problem());
        Assert.Equal(
            "its pieces name the work item 'one' twice, and a work item is named by one piece of an update",
            (entry with { Pieces = [added, added with { Kind = "changed" }] }).Problem());
        Assert.Equal(
            "one it told holds no step, nothing told or no way it reached it",
            (entry with { Told = [new FlowUpdateTold("rounds.items[0].build", " ", "with the answer")] }).Problem());
        Assert.Equal(
            "one step was told twice by one update",
            (entry with { Told = [new FlowUpdateTold("rounds.items[0].build", "one", "with the answer"), new FlowUpdateTold("rounds.items[0].build", "two", "in its prompt")] }).Problem());
        Assert.Equal("one it names as not concerned is not an instance's id", (entry with { NotConcerned = ["nobody!"] }).Problem());
        Assert.Equal("one not delivered names neither the work nor why", (entry with { NotDelivered = [new FlowNotDelivered("rounds.items[2].build", " ")] }).Problem());
        Assert.Equal("'later!' is not an instance's id", (entry with { AddedIteration = "later!" }).Problem());
    }

    /// <summary>
    /// What an answer may be is one rule, held by the command and the engine alike: one of the question's own - abort
    /// among them for a first answer and never for a change - with a text where its option takes one and none where it
    /// takes none, whatever the question's kind, and a change that changes something; the answers a question takes are
    /// said as their names. The last set a person answered is the one with the newest answer a person gave.
    /// </summary>
    [Fact]
    public void TheAnswerRules_AreOneRuleForTheCommandAndTheEngine()
    {
        IReadOnlyList<FlowQuestionOption> pause = [new("go", "goes on"), new("stay", "stays"), new("abort", "ends the run")];
        IReadOnlyList<FlowQuestionOption> ask = [new("answer", "the text is read", Text: true), new("decide", "the agent decides"), new("abort", "ends the run")];

        Assert.Null(FlowAnswerRules.Problem("q1", pause, "go", text: null));
        Assert.Null(FlowAnswerRules.Problem("q1", pause, "abort", text: null));
        Assert.Null(FlowAnswerRules.Problem("q2", ask, "answer", "only the parser"));
        Assert.Null(FlowAnswerRules.Problem("q1", pause, "stay", text: null, standing: ("go", null)));

        Assert.Equal("'later' is not one of the answers 'q1' takes: go, stay, abort", FlowAnswerRules.Problem("q1", pause, "later", text: null));
        Assert.Equal("'later' is not one of the answers 'q1' takes: go, stay", FlowAnswerRules.Problem("q1", pause, "later", text: null, standing: ("go", null)));
        Assert.Equal(
            "'abort' does not change an answer: a run is ended by stop-orchestrator, and one an abort ended is gone on with by resume-orchestrator",
            FlowAnswerRules.Problem("q1", pause, "abort", text: null, standing: ("go", null)));
        Assert.Equal("'answer' is an answer 'q2' takes with a text; give one", FlowAnswerRules.Problem("q2", ask, "answer", "  "));
        Assert.Equal("'decide' is an answer 'q2' takes with no text; give none", FlowAnswerRules.Problem("q2", ask, "decide", "only the parser"));
        Assert.Equal("'go' is an answer 'q1' takes with no text; give none", FlowAnswerRules.Problem("q1", pause, "go", "a note"));
        Assert.Equal("'go' is the answer 'q1' already has; a changed answer changes it", FlowAnswerRules.Problem("q1", pause, "go", text: null, standing: ("go", null)));
        Assert.Null(FlowAnswerRules.Problem("q2", ask, "answer", "the registry too", standing: ("answer", "only the parser")));

        Assert.Null(FlowAnswerRules.TextOf("  "));
        Assert.Equal("a note", FlowAnswerRules.TextOf("a note"));

        static FlowQuestionEntry Answered(string id, string by, int minute)
            => new(id, "answered", "pause", At(0), [], [new("go", "goes on"), new("stay", "stays")], [new FlowAnswerRecord("go", At(minute), By: new FlowAnswerBy(by))]);

        Assert.Equal("q2", FlowAnswerRules.LastSetAnswered([Answered("q1", "person", 1), Answered("q2", "person", 3), Answered("q3", "configuration", 5)])!.Id);
        Assert.Null(FlowAnswerRules.LastSetAnswered([Answered("q1", "input", 1)]));
        Assert.Equal("no answer of run 'r1' was given by a person, so there is no set to change", FlowAnswerRules.NoSetToChange("r1"));
    }

    /// <summary>How the run took an update is said by the state its entry stands in: queued, being planned, or done at once.</summary>
    [Fact]
    public void HowAnUpdateWasTaken_IsSaidByItsState()
    {
        var entry = new FlowUpdateEntry("u2", FlowUpdateCauses.Amendment, FlowUpdateStates.Queued, At(0), Amendment: "more");

        Assert.Equal("it is recorded as update u2, queued behind the update under way, and planned when that one is done", entry.HowTaken());
        Assert.Equal("it is update u2, being planned: the run is held while 'planning.planupdate' plans it", (entry with { State = FlowUpdateStates.UnderWay, Step = "planning.planupdate" }).HowTaken());
        Assert.Equal("it is update u2, done at once, for no plan held it yet: every step that starts reads the request with its amendments", (entry with { State = FlowUpdateStates.Done }).HowTaken());
    }

    /// <summary>
    /// A set is changed one time, the run's updates the authority: an update that changes the set's answer - any but a
    /// refused one - holds its one change though the set's own record does not hold it yet, one being taken up again is
    /// not counted against itself, and a set whose record holds a replaced answer says when it was changed.
    /// </summary>
    [Fact]
    public void ASetIsChangedOneTime_ByTheRunsUpdates()
    {
        static FlowUpdateEntry Change(string id, string state, string question = "q1")
            => new(id, FlowUpdateCauses.ChangedAnswer, state, At(2), Question: question, FromAnswer: "go", ToAnswer: "stay");

        Assert.Null(FlowAnswerRules.ChangedAlready("r1", "q1", replacedAt: null, []));
        Assert.Null(FlowAnswerRules.ChangedAlready("r1", "q1", replacedAt: null, [Change("u1", FlowUpdateStates.Refused), Change("u2", FlowUpdateStates.Done, question: "q2")]));
        Assert.Null(FlowAnswerRules.ChangedAlready("r1", "q1", replacedAt: null, [Change("u1", FlowUpdateStates.Queued)], taking: "u1"));

        foreach (var state in new[] { FlowUpdateStates.Queued, FlowUpdateStates.UnderWay, FlowUpdateStates.Done, FlowUpdateStates.Failed })
        {
            Assert.Equal(
                "question 'q1' of run 'r1' has its answer changed already - update u1 holds the change, taken at 2026-10-29 14:02:00; a set is changed one time",
                FlowAnswerRules.ChangedAlready("r1", "q1", replacedAt: null, [Change("u1", state)]));
        }

        Assert.Equal(
            "question 'q1' of run 'r1' had its answer changed already, at 2026-10-29 14:03:00; a set is changed one time",
            FlowAnswerRules.ChangedAlready("r1", "q1", replacedAt: At(3), [Change("u1", FlowUpdateStates.Done)]));
    }

    /// <summary>The number of iterations updates may add to a loop at its bound is one property of the amend block: what it names, else one.</summary>
    [Fact]
    public void TheIterationsUpdatesMayAdd_AreWhatTheAmendBlockNames_ElseOne()
    {
        var amend = new FlowAmend(Input: "request", ChangedAnswerMinutes: 5, Before: null, Step: "planner.replan", Then: null, Scope: "x", WhenAddScope: "a", WhenChangeScope: "a", WhenRemoveScope: "a", MaxAddedIterations: null);

        Assert.Equal(1, FlowAmend.DefaultMaxAddedIterations);
        Assert.Equal(FlowAmend.DefaultMaxAddedIterations, amend.AddedIterationsAllowed);
        Assert.Equal(3, (amend with { MaxAddedIterations = 3 }).AddedIterationsAllowed);
        Assert.Equal(0, (amend with { MaxAddedIterations = 0 }).AddedIterationsAllowed);
    }

    /// <summary>
    /// The input as every prompt and expression reads it once amendments have been taken: the value as given, then
    /// each amendment under a line that says when it was made - of one whose update failed, that no plan holds it -
    /// and an amendment the run refused is never read, for nothing of it was changed.
    /// </summary>
    [Fact]
    public void TheInputReadsTheValueAsGiven_WithEachAmendmentUnderALineThatSaysWhenItWasMade()
    {
        Assert.Equal("build it", FlowUpdatesFile.InputOf("build it", []));

        Assert.Equal(
            "build it\n\namended at 2026-10-29 14:00:00:\nin fact, only the parser",
            FlowUpdatesFile.InputOf("build it", [new FlowUpdateEntry("u1", "amendment", "done", At(0), Amendment: "in fact, only the parser", Through: "amend-orchestrator-request")]));

        Assert.Equal(
            "build it\n\namended at 2026-10-29 14:00:00:\nfirst\n\namended at 2026-10-29 14:01:00, and no plan holds it:\nsecond",
            FlowUpdatesFile.InputOf("build it",
            [
                new FlowUpdateEntry("u1", "amendment", "done", At(0), Amendment: "first"),
                new FlowUpdateEntry("u2", "amendment", "failed", At(1), Amendment: "second"),
            ]));

        Assert.Equal(
            "build it",
            FlowUpdatesFile.InputOf("build it", [new FlowUpdateEntry("u1", "amendment", "refused", At(0), Amendment: "narrowed away", Why: "past its work items")]));
    }

    /// <summary>
    /// The gates every change passes, read of a run's state in one file: a run that is done has nothing to go back
    /// to; one past the step its <c>amend.before</c> names has a plan that is no longer changed; one past the work
    /// items of its last round - the loop around the work-item group at its own bound, with everything updates may
    /// add - has nothing that would carry the change out; and a loop the definition leaves open is never past its
    /// work items, for what it carries is decided where an update is taken.
    /// </summary>
    [Fact]
    public void TheGatesReadTheRunsOwnState()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: notes
                type: orient
              - name: rounds
                repeat:
                  until: 'false'
                  maxIterations: 2
                steps:
                  - name: items
                    forEach: 'check.items'
                    steps:
                      - name: build
                        type: orient
              - name: last
                type: pause
                question: done?
                options: [yes]
            """);

        var gates = new FlowAmend(Input: "request", ChangedAnswerMinutes: 5, Before: "notes", Step: "planner.replan", Then: null, Scope: "x", WhenAddScope: "rounds.items.build", WhenChangeScope: "rounds.items.build", WhenRemoveScope: "rounds.items.build", MaxAddedIterations: 0);
        var open = new FlowAmend(Input: "request", ChangedAnswerMinutes: 5, Before: null, Step: "planner.replan", Then: null, Scope: "x", WhenAddScope: "rounds.items.build", WhenChangeScope: "rounds.items.build", WhenRemoveScope: "rounds.items.build", MaxAddedIterations: null);
        var addsNothing = new FlowAmend(Input: "request", ChangedAnswerMinutes: 5, Before: null, Step: "planner.replan", Then: null, Scope: "x", WhenAddScope: "rounds.items.build", WhenChangeScope: "rounds.items.build", WhenRemoveScope: "rounds.items.build", MaxAddedIterations: 0);

        // Done: nothing to go back to.
        Assert.Equal(
            "run 'r1' is done; there is nothing left to go back to",
            FlowUpdateGates.Refused(definition with { Amend = open }, Flow("Done", round: 2), null, FlowUpdateCauses.Amendment));

        // Past the step before names.
        Assert.Equal(
            "run 'r1' has started 'notes': what it built is being written down, and its plan is no longer changed. Start a run for the answer once this one has ended",
            FlowUpdateGates.Refused(definition with { Amend = gates }, Flow("Paused", round: 1), null, FlowUpdateCauses.ChangedAnswer));

        // Past the work items of the last round: 2 by its own bound, 0 that updates may add.
        Assert.Equal(
            "run 'r1' is past the work items of round 2 of 'rounds', the last it can run (2 by its own bound, and the 0 that amend.maxAddedIterations lets updates add): nothing would carry the change out. Start a run for the request once this one has ended",
            FlowUpdateGates.Refused(definition with { Amend = addsNothing }, Flow("Paused", round: 2), null, FlowUpdateCauses.Amendment));

        // The round before the last still carries work, and one still open carries its own.
        Assert.Null(FlowUpdateGates.Refused(definition with { Amend = addsNothing }, Flow("Paused", round: 1), null, FlowUpdateCauses.Amendment));
        Assert.Null(FlowUpdateGates.Refused(definition with { Amend = addsNothing }, Flow("Paused", round: 2, groupRunning: true), null, FlowUpdateCauses.Amendment));

        // A loop the definition leaves open - no maxIterations - is never past its work items by these gates.
        var unbounded = Definition("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: 'false'
                steps:
                  - name: items
                    forEach: 'check.items'
                    steps:
                      - name: build
                        type: orient
            """);

        Assert.Null(FlowUpdateGates.Refused(unbounded with { Amend = open }, Flow("Paused", round: 9), null, FlowUpdateCauses.Amendment));

        // Where the amend block names no before, and the run is not done and not past its work items, it takes one.
        Assert.Null(FlowUpdateGates.Refused(definition with { Amend = open }, Flow("Paused", round: 0), null, FlowUpdateCauses.Amendment));
    }

    /// <summary>
    /// A work-item group with no loop around it runs its own round and each round <c>amend.maxAddedIterations</c> lets
    /// updates add - a visit of the group run again: the gates take a change while a round may still be added or the
    /// newest is under way, and refuse it, saying why, once the newest has ended and no round more is run - the rounds
    /// counted by the updates' entries, never by the group's visits, a redirect's among them, nor by the instances of
    /// its work items - or once a group around it ended otherwise than done, for nothing draws a round of it again.
    /// </summary>
    [Fact]
    public void AGroupWithNoLoopAroundIt_IsPastItsWorkItems_OnceTheLastRoundItMayRunHasEnded()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: rounds
                steps:
                  - name: items
                    forEach: 'check.items'
                    steps:
                      - name: build
                        type: orient
              - name: last
                type: pause
                question: done?
                options: [yes]
            """);

        var one = definition with { Amend = new FlowAmend(Input: "request", ChangedAnswerMinutes: 5, Before: null, Step: "planner.replan", Then: null, Scope: "x", WhenAddScope: "rounds.items.build", WhenChangeScope: "rounds.items.build", WhenRemoveScope: "rounds.items.build", MaxAddedIterations: null) };
        var none = one with { Amend = one.Amend! with { MaxAddedIterations = 0 } };

        // Its own round ended, with one round still to be added; the added round under way; its own under way; and a
        // second visit a redirect drew, which no update added.
        Assert.Null(FlowUpdateGates.Refused(one, Rounds("Running", "Done"), Added(0), FlowUpdateCauses.Amendment));
        Assert.Null(FlowUpdateGates.Refused(one, Rounds("Running", "Done", "Running"), Added(1), FlowUpdateCauses.Amendment));
        Assert.Null(FlowUpdateGates.Refused(none, Rounds("Running", "Running"), Added(0), FlowUpdateCauses.Amendment));
        Assert.Null(FlowUpdateGates.Refused(one, Rounds("Running", "Done", "Done"), Added(0), FlowUpdateCauses.Amendment));

        // The newest round has ended and no round more is run - the one added, or its own where none may be added.
        Assert.Equal(
            "run 'r1' is past the work items of 'rounds.items', with no loop around it: its last round has ended, and updates have added the 1 round(s) amend.maxAddedIterations allows - nothing would carry the change out. Start a run for the request once this one has ended",
            FlowUpdateGates.Refused(one, Rounds("Running", "Done", "Failed"), Added(1), FlowUpdateCauses.Amendment));
        Assert.Equal(
            "run 'r1' is past the work items of 'rounds.items', with no loop around it: its last round has ended, and updates have added the 0 round(s) amend.maxAddedIterations allows - nothing would carry the change out. Start a run for the answer once this one has ended",
            FlowUpdateGates.Refused(none, Rounds("Running", "Done"), Added(0), FlowUpdateCauses.ChangedAnswer));

        // A group around it that ended failed draws no round of it again, whatever may still be added.
        Assert.Equal(
            "run 'r1' is past the work items of 'rounds.items', with no loop around it: its last round has ended, and 'rounds', the group around it, ended failed and draws nothing more - nothing would carry the change out. Start a run for the request once this one has ended",
            FlowUpdateGates.Refused(one, Rounds("Failed", "Failed"), Added(0), FlowUpdateCauses.Amendment));
    }

    /// <summary>The run's plan updates: <paramref name="rounds"/> of them done, each having added a round of the work-item group.</summary>
    private static FlowUpdatesFile Added(int rounds)
        => new(1, "r1", At(0), [.. Enumerable.Range(1, rounds).Select(at => new FlowUpdateEntry(FlowUpdateIds.Of(at), FlowUpdateCauses.Amendment, FlowUpdateStates.Done, At(at), Amendment: "more", AddedIteration: $"rounds.items+{(at + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}"))]);

    /// <summary>
    /// The run's state in one file: paused, the group around the work-item group as <paramref name="around"/> says, and
    /// the work-item group, with no loop around it, visited once for each of <paramref name="rounds"/> - each visit as
    /// it stands - every visit with a work item of its own, ended.
    /// </summary>
    private static FlowFile Rounds(string around, params string[] rounds)
    {
        List<FlowStepRecord> steps = [new FlowStepRecord("rounds", "rounds", true, around, 1, 1, [], [])];

        for (var at = 0; at < rounds.Length; at++)
        {
            var visit = at == 0 ? "rounds.items" : $"rounds.items+{(at + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            steps.Add(new FlowStepRecord(visit, "rounds.items", true, rounds[at], 1, at + 1, [], []));
            steps.Add(new FlowStepRecord($"{visit}[0]", "rounds.items.items", true, "Done", 1, 1, [], [], Index: 0));
            steps.Add(new FlowStepRecord($"{visit}[0].build", "rounds.items.build", false, "Done", 1, 1, [], [], Index: 0));
        }

        return new FlowFile(1, "r1", "flow", "digest", new Dictionary<string, string>(), "Paused", new FlowBudgetRecord(0, 0, 0), 0, steps, [], At(0));
    }

    /// <summary>The store takes one update into the run's record under its own lock: the id over every update the file holds, so two taken at once never share one.</summary>
    [Fact]
    public async Task TheStoreGivesEachUpdateItsOwnId_UnderTheRunsOwnLock()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { Workflows = ["flow"] });

        var store = harness.OrchestrationStore;
        var run = new OrchestratorLayout("r1", Path.Combine(temp.Path, ".orchestrators", "r1"));

        var first = store.AddUpdate(run, "r1", At(0), id => new FlowUpdateEntry(id, "amendment", "queued", At(0), Amendment: "one", Through: "amend-orchestrator-request"));
        var second = store.AddUpdate(run, "r1", At(1), id => new FlowUpdateEntry(id, "amendment", "queued", At(1), Amendment: "two", Through: "amend-orchestrator-request"));

        Assert.Equal(("u1", "one"), (first.Id, first.Amendment));
        Assert.Equal(("u2", "two"), (second.Id, second.Amendment));
        Assert.Equal(["u1", "u2"], store.ReadFlowUpdates(run)!.Updates.Select(update => update.Id));
        Assert.Equal("the run's plan updates", OrchestrationStore.UpdatesKind);
    }

    /// <summary>The definition of the gates' tests, parsed from <paramref name="text"/>.</summary>
    private static WorkflowDefinition Definition(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The run's state in one file: paused, with the work-item group of round <paramref name="round"/> done - or still running, where <paramref name="groupRunning"/> says so.</summary>
    private static FlowFile Flow(string status, int round, bool groupRunning = false)
    {
        List<FlowStepRecord> steps =
        [
            new FlowStepRecord("i1", "notes", false, "Done", 1, 1, [], []),
            new FlowStepRecord("i2", "rounds", true, groupRunning ? "Running" : round == 0 ? "NotStarted" : "Done", 1, 1, [], [], Iteration: round),
        ];

        if (round > 0)
        {
            steps.Add(new FlowStepRecord("i3", "rounds.items", true, groupRunning ? "Running" : "Done", 1, 1, [], [], Iteration: round));
        }

        return new FlowFile(1, "r1", "flow", "digest", new Dictionary<string, string>(), status, new FlowBudgetRecord(0, 0, 0), 0, steps, [], At(0));
    }
}
