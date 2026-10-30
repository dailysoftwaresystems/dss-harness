using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The records of a plan update (31d1): what an update is in <c>updates.json</c> - its id, cause and state, held to
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
            FlowUpdateGates.Refused(definition with { Amend = open }, Flow("Done", round: 2), FlowUpdateCauses.Amendment));

        // Past the step before names.
        Assert.Equal(
            "run 'r1' has started 'notes': what it built is being written down, and its plan is no longer changed. Start a run for the answer once this one has ended",
            FlowUpdateGates.Refused(definition with { Amend = gates }, Flow("Paused", round: 1), FlowUpdateCauses.ChangedAnswer));

        // Past the work items of the last round: 2 by its own bound, 0 that updates may add.
        Assert.Equal(
            "run 'r1' is past the work items of round 2 of 'rounds', the last it can run (2 by its own bound, and the 0 that amend.maxAddedIterations lets updates add): nothing would carry the change out. Start a run for the request once this one has ended",
            FlowUpdateGates.Refused(definition with { Amend = addsNothing }, Flow("Paused", round: 2), FlowUpdateCauses.Amendment));

        // The round before the last still carries work, and one still open carries its own.
        Assert.Null(FlowUpdateGates.Refused(definition with { Amend = addsNothing }, Flow("Paused", round: 1), FlowUpdateCauses.Amendment));
        Assert.Null(FlowUpdateGates.Refused(definition with { Amend = addsNothing }, Flow("Paused", round: 2, groupRunning: true), FlowUpdateCauses.Amendment));

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

        Assert.Null(FlowUpdateGates.Refused(unbounded with { Amend = open }, Flow("Paused", round: 9), FlowUpdateCauses.Amendment));

        // Where the amend block names no before, and the run is not done and not past its work items, it takes one.
        Assert.Null(FlowUpdateGates.Refused(definition with { Amend = open }, Flow("Paused", round: 0), FlowUpdateCauses.Amendment));
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
