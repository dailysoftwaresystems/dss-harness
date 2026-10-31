using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The engine's facts about a run (<see cref="FlowStatus"/>, pure): computed from the run's records and never guessed -
/// how the run stands, what is running, what it waits for, what is done, what is left at least and at most with the
/// bound that gave the most, and when it may finish as a range that rests on this run's own times, with a step that
/// never ran named and left out, and no estimate at all where nothing has ended or the run has ended.
/// </summary>
public sealed class FlowStatusTests
{
    /// <summary>One moment every fact of these tests is stamped with.</summary>
    private static readonly DateTimeOffset AsOf = new(2026, 10, 30, 10, 10, 0, TimeSpan.Zero);

    /// <summary>The run's one engine, live: named once so its saying is one text.</summary>
    private static FlowEngineState Engine => new(FlowEngineState.Running, "process 7", []);

    /// <summary>No engine: what a run between sessions, and one a person asks about after its engine ended, is read by.</summary>
    private static FlowEngineState NoEngine => new(FlowEngineState.None, null, []);

    /// <summary>A definition parsed from its yml, as the run's frozen definition reads it.</summary>
    private static WorkflowDefinition Definition(string yaml)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", yaml);

    private static DateTimeOffset At(int hour, int minute, int second = 0)
        => new(2026, 10, 30, hour, minute, second, TimeSpan.Zero);

    /// <summary>One step instance's record, in the shape flow.json holds it.</summary>
    private static FlowStepRecord Step(
        string instance,
        string state,
        string? type = null,
        bool group = false,
        DateTimeOffset? started = null,
        DateTimeOffset? finished = null,
        int attempt = 1,
        int visit = 1,
        string? agent = null,
        string? model = null,
        int? iteration = null,
        IReadOnlyList<string>? waitsOn = null,
        string? handledBy = null,
        string? failure = null,
        string? raisedBy = null)
        => new(
            instance,
            instance.Split('.')[^1].Split(['+', '#', '['])[0],
            group,
            state,
            attempt,
            visit,
            [],
            waitsOn ?? [],
            type,
            started,
            finished,
            Agent: agent,
            Model: model,
            Iteration: iteration,
            HandledBy: handledBy,
            Failure: failure,
            RaisedBy: raisedBy);

    /// <summary>The run's state in one file, in the shape flow.json holds it.</summary>
    private static FlowFile File(string status, IReadOnlyList<FlowStepRecord> steps, DateTimeOffset? started = null, double usd = 0, FlowPauseRecord? pause = null)
        => new(
            1,
            "r1",
            "flow",
            "digest",
            new Dictionary<string, string>(StringComparer.Ordinal),
            status,
            new FlowBudgetRecord(usd, 0, 0),
            0,
            steps,
            [],
            started ?? At(10, 0),
            Pause: pause);

    /// <summary>
    /// The facts of a run in flight: the header with the moment, the run's standing and its engine; what is running
    /// with its type, agent, model, attempt and for how long; what is done and what was spent of the budget; what is
    /// left, with nothing more that can come; and the estimate's two ends, a step in progress counted with what is
    /// left of the time its own earlier visit took.
    /// </summary>
    [Fact]
    public void TheFactsOfARunInFlight()
    {
        var definition = Definition("""
            name: flow
            budget: { usd: 10 }
            steps:
              - name: build
                type: orient
              - name: verify
                type: test
                dependsOn: [build]
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 1)),
                Step("verify", nameof(FlowInstanceState.Done), "test", started: At(10, 1), finished: At(10, 6)),
                Step("verify+2", nameof(FlowInstanceState.Running), "test", started: At(10, 6), attempt: 1, visit: 2, agent: "v", model: "glm-5.3"),
            ],
            usd: 1.5);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf);

        Assert.Equal(
            """
            run 'r1' (workflow 'flow'), as of 10:10:00Z: running, for 10m00s; its engine (process 7) is running
            running   verify+2 (test, agent 'v', model glm-5.3, attempt 1) for 4m00s
            waits     nothing
            done      2 step instances ended; 1.50 of 10.00 USD, 0 tokens spent
            left      at least 1 step instance, at most 1 step instance (nothing more can come)
            estimate  between 1m00s and 1m00s more, from the 2 step instances that ended Done in this run
            """,
            string.Join("\n", facts));
    }

    /// <summary>
    /// The facts of a run that waits: each open question with since when and the command that answers it, a plan update
    /// under way and one queued, an instance waiting on a claim - and the estimate leaving out, and saying it does, the
    /// time a person takes to answer and the wait for the claim.
    /// </summary>
    [Fact]
    public void TheFactsOfARunThatWaits()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: gate
                type: pause
                question: what now?
                options: [stay, go]
                dependsOn: [build]
              - name: hold
                type: orient
                dependsOn: [gate]
            """);
        var flow = File(
            nameof(FlowRunStatus.Paused),
            [
                Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 2)),
                Step("gate", nameof(FlowInstanceState.Done), "pause", started: At(10, 1), finished: At(10, 2)),
                Step("gate+2", nameof(FlowInstanceState.Paused), "pause", started: At(10, 2), visit: 2),
                Step("hold", nameof(FlowInstanceState.NotStarted), waitsOn: [FlowWaits.Claim]),
            ]);
        var questions = new FlowQuestionsFile(
            1,
            "r1",
            AsOf,
            [
                new FlowQuestionEntry(
                    "q1",
                    "open",
                    "ask",
                    At(10, 2),
                    [],
                    [new FlowQuestionOption("stay", "it stays"), new FlowQuestionOption("go", "it goes")],
                    []),
            ]);
        var updates = new FlowUpdatesFile(
            1,
            "r1",
            AsOf,
            [
                new FlowUpdateEntry("u1", FlowUpdateCauses.Amendment, FlowUpdateStates.UnderWay, At(10, 3), Amendment: "narrow it", Step: "plan+2"),
                new FlowUpdateEntry("u2", FlowUpdateCauses.Amendment, FlowUpdateStates.Queued, At(10, 4), Amendment: "widen it again"),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions, updates, NoEngine, AsOf);

        Assert.Equal(
            """
            run 'r1' (workflow 'flow'), as of 10:10:00Z: paused, for 10m00s; no engine
            running   nothing is running
            waits     question q1 (ask) since 10:02:00Z: 'dssharness answer-orchestrator r1 <answer> --question q1'
                      plan update u1 under way: the run is held while 'plan+2' plans it
                      plan update u2 is recorded and waits for the run to be gone on with
                      hold waits for a claim
            done      2 step instances ended; 0.00 USD, 0 tokens spent
            left      at least 2 step instances, at most 2 step instances (nothing more can come)
            estimate  between 1m00s and 1m00s more, from the 2 step instances that ended Done in this run; 1 step of what is left has not run to an end in this run and is not in the estimate: hold; not counting the time a person takes to answer q1; not counting a wait for a claim, for the run to be quiet or for room under its limits
            """,
            string.Join("\n", facts));
    }

    /// <summary>
    /// Least and most of what is left with a loop at each bound: a <c>repeat</c> at its <c>maxIterations</c> adds the
    /// iterations left to its bound times the steps its group holds, and a <c>repeat</c> with no numeric bound of its
    /// own, like a <c>forEach</c> not yet expanded, is bounded only by the run's own bound of instances, which the
    /// facts name.
    /// </summary>
    [Fact]
    public void LeastAndMostOfWhatIsLeft_WithALoopAtEachBound()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: 'rounds.done'
                  maxIterations: 3
                steps:
                  - name: build
                    type: orient
                  - name: verify
                    type: orient
                    dependsOn: [build]
              - name: sweep
                repeat:
                  until: 'sweep.done'
                  rounds: 1
                  blocking: 'sweep.done'
                  blockersLimitRounds: false
                steps:
                  - name: clean
                    type: orient
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("rounds", nameof(FlowInstanceState.Running), group: true, started: At(10, 0)),
                Step("rounds#1", nameof(FlowInstanceState.Done), group: true, started: At(10, 0), finished: At(10, 0, 30), iteration: 1),
                Step("rounds#1.build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 0, 10)),
                Step("rounds#1.verify", nameof(FlowInstanceState.Done), "orient", started: At(10, 0, 10), finished: At(10, 0, 30)),
                Step("rounds#2", nameof(FlowInstanceState.Running), group: true, started: At(10, 1), iteration: 2),
                Step("rounds#2.build", nameof(FlowInstanceState.NotStarted), "orient", iteration: 2),
                Step("rounds#2.verify", nameof(FlowInstanceState.NotStarted), "orient", iteration: 2),
                Step("sweep", nameof(FlowInstanceState.Running), group: true),
                Step("sweep#1", nameof(FlowInstanceState.NotStarted), group: true),
                Step("sweep#1.clean", nameof(FlowInstanceState.NotStarted), "orient"),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf);

        Assert.Equal(
            """
            run 'r1' (workflow 'flow'), as of 10:10:00Z: running, for 10m00s; its engine (process 7) is running
            running   nothing is running
            waits     nothing
            done      3 step instances ended; 0.00 USD, 0 tokens spent
            left      at least 7 step instances, at most 4997 step instances (the loops' bounds and the run's own bound of at most 5000 instances)
            estimate  between 30s and 1m00s more, from the 3 step instances that ended Done in this run; a loop still open has no numeric bound of its own, and the time it may yet take is not in the estimate; 1 step of what is left has not run to an end in this run and is not in the estimate: sweep.clean
            """,
            string.Join("\n", facts));
    }

    /// <summary>
    /// A loop at its numeric bound with no <c>forEach</c> open beside it: the most counts only what its bound allows,
    /// said to be the loops' bounds alone, and the estimate's upper end holds the iterations left at the longest of
    /// the group's steps.
    /// </summary>
    [Fact]
    public void ALoopAtItsOwnBound_GivesTheMostAlone()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: 'rounds.done'
                  maxIterations: 3
                steps:
                  - name: build
                    type: orient
                  - name: verify
                    type: orient
                    dependsOn: [build]
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("rounds", nameof(FlowInstanceState.Running), group: true, started: At(10, 0)),
                Step("rounds#1", nameof(FlowInstanceState.Done), group: true, started: At(10, 0), finished: At(10, 0, 30), iteration: 1),
                Step("rounds#1.build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 0, 10)),
                Step("rounds#1.verify", nameof(FlowInstanceState.Done), "orient", started: At(10, 0, 10), finished: At(10, 0, 30)),
                Step("rounds#2", nameof(FlowInstanceState.Running), group: true, started: At(10, 1), iteration: 2),
                Step("rounds#2.build", nameof(FlowInstanceState.NotStarted), "orient", iteration: 2),
                Step("rounds#2.verify", nameof(FlowInstanceState.NotStarted), "orient", iteration: 2),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf);

        Assert.Equal(
            "left      at least 4 step instances, at most 7 step instances (the loops' bounds)",
            facts.First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
        Assert.Equal(
            "estimate  between 30s and 1m00s more, from the 3 step instances that ended Done in this run",
            facts.First(fact => fact.StartsWith("estimate", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A loop's bound is widened by what was granted past it - each <c>more</c> a person answered its bound with, in
    /// <c>questions.json</c>, and each iteration a plan update added to it - and counted past the iterations the loop has
    /// made, its own record standing for it whatever iteration is under way: the most is the bound the engine holds it
    /// to.
    /// </summary>
    [Fact]
    public void ALoopsBound_IsWidenedByWhatItsAnswersAndUpdatesGranted()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: 'rounds.done'
                  maxIterations: 2
                steps:
                  - name: build
                    type: orient
                  - name: verify
                    type: orient
                    dependsOn: [build]
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("rounds", nameof(FlowInstanceState.Running), group: true, started: At(10, 0)),
                Step("rounds#1", nameof(FlowInstanceState.Done), group: true, started: At(10, 0), finished: At(10, 0, 30), iteration: 1),
                Step("rounds#1.build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 0, 10)),
                Step("rounds#1.verify", nameof(FlowInstanceState.Done), "orient", started: At(10, 0, 10), finished: At(10, 0, 30)),
                Step("rounds#2", nameof(FlowInstanceState.Done), group: true, started: At(10, 1), finished: At(10, 1, 30), iteration: 2),
                Step("rounds#3", nameof(FlowInstanceState.Running), group: true, started: At(10, 2), iteration: 3),
                Step("rounds#3.build", nameof(FlowInstanceState.NotStarted), "orient", iteration: 3),
                Step("rounds#3.verify", nameof(FlowInstanceState.NotStarted), "orient", iteration: 3),
            ]);
        var questions = new FlowQuestionsFile(
            1,
            "r1",
            AsOf,
            [
                new FlowQuestionEntry(
                    "q1",
                    "answered",
                    "loopBound",
                    At(10, 1, 30),
                    [],
                    [new FlowQuestionOption("more", "one more iteration"), new FlowQuestionOption("accept", "the loop ends")],
                    [new FlowAnswerRecord("more", At(10, 2))],
                    RaisedBy: new FlowQuestionRaisedBy("engine", "rounds")),
            ]);
        var updates = new FlowUpdatesFile(
            1,
            "r1",
            AsOf,
            [new FlowUpdateEntry("u1", FlowUpdateCauses.Amendment, FlowUpdateStates.Done, At(10, 3), Amendment: "one more round", AddedIteration: "rounds#4")]);

        var facts = FlowStatus.Facts(definition, flow, questions, updates, Engine, AsOf);

        // Its bound, 2, a person's more and an update's iteration: 4, three of them made - one more of three instances.
        Assert.Equal(
            "left      at least 4 step instances, at most 7 step instances (the loops' bounds); update u1 owes 'rounds#4' to its loop, and it is not made yet",
            facts.First(fact => fact.StartsWith("left", StringComparison.Ordinal)));

        // At its bound with its question open, the loop's own record is all that is left of it: no iteration is counted
        // past the ones it made, and the question is said to be what can grant more.
        var asking = File(
            nameof(FlowRunStatus.Paused),
            [
                Step("rounds", nameof(FlowInstanceState.Paused), group: true, started: At(10, 0)) with { Question = "q2" },
                Step("rounds#1", nameof(FlowInstanceState.Done), group: true, started: At(10, 0), finished: At(10, 0, 30), iteration: 1),
                Step("rounds#2", nameof(FlowInstanceState.Done), group: true, started: At(10, 1), finished: At(10, 1, 30), iteration: 2),
            ]);
        var open = new FlowQuestionsFile(
            1,
            "r1",
            AsOf,
            [
                new FlowQuestionEntry(
                    "q2",
                    "open",
                    "loopBound",
                    At(10, 1, 30),
                    [],
                    [new FlowQuestionOption("more", "one more iteration"), new FlowQuestionOption("accept", "the loop ends")],
                    [],
                    RaisedBy: new FlowQuestionRaisedBy("engine", "rounds")),
            ]);

        Assert.Equal(
            "left      at least 1 step instance, at most 1 step instance (the loops' bounds); question q2 (loopBound) can be answered with more past its bound",
            FlowStatus.Facts(definition, asking, open, updates: null, NoEngine, AsOf).First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A handler granted one more visit by an answer the run has not carried out yet - the source's record still waits
    /// on the question, its ending stands - is counted in the scope it was granted in, though its source is under way no
    /// longer: the visit is the engine's to raise when the run goes on.
    /// </summary>
    [Fact]
    public void AHandlerGrantedAVisitNotCarriedOutYet_IsCountedInTheScopeItWasGranted()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on:
                  step: build
                  outcome: failure
                maxVisits: 1
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var flow = File(
            nameof(FlowRunStatus.Paused),
            [
                Step("build", nameof(FlowInstanceState.Failed), "orient", started: At(10, 0), finished: At(10, 1), handledBy: "fix"),
                Step("fix", nameof(FlowInstanceState.Done), "orient", started: At(10, 1), finished: At(10, 3), raisedBy: "build"),
                Step("build+2", nameof(FlowInstanceState.Failed), "orient", started: At(10, 3), finished: At(10, 4), visit: 2) with { Question = "q1" },
                Step("after", nameof(FlowInstanceState.NotStarted), "orient"),
            ]);
        var questions = new FlowQuestionsFile(
            1,
            "r1",
            AsOf,
            [
                new FlowQuestionEntry(
                    "q1",
                    "answered",
                    "visitBound",
                    At(10, 4),
                    [],
                    [new FlowQuestionOption("more", "one more visit"), new FlowQuestionOption("accept", "the ending stands")],
                    [new FlowAnswerRecord("more", At(10, 5))],
                    RaisedBy: new FlowQuestionRaisedBy("engine", "build+2"),
                    Handler: "fix"),
            ]);

        Assert.Equal(
            "left      at least 1 step instance, at most 2 step instances (the loops' bounds)",
            FlowStatus.Facts(definition, flow, questions, updates: null, NoEngine, AsOf).First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Where nothing has ended there is no estimate and the facts say which - and where the run has ended there is
    /// none either, said as its own reason, the run said to have run for the time its steps' endings name.
    /// </summary>
    [Fact]
    public void NoEstimateWhereNothingHasEnded_AndNoneWhereTheRunHasEnded()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: build
                type: orient
            """);
        var nothing = FlowStatus.Facts(
            definition,
            File(nameof(FlowRunStatus.Running), [Step("build", nameof(FlowInstanceState.NotStarted), "orient")]),
            questions: null,
            updates: null,
            Engine,
            AsOf);

        Assert.Equal(
            "estimate  none: no step instance has ended Done in this run yet, so there is no time of its own to go by",
            nothing.First(fact => fact.StartsWith("estimate", StringComparison.Ordinal)));

        var ended = FlowStatus.Facts(
            definition,
            File(
                nameof(FlowRunStatus.Done),
                [Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 2))]),
            questions: null,
            updates: null,
            NoEngine,
            AsOf);

        Assert.Equal("run 'r1' (workflow 'flow'), as of 10:10:00Z: done, ran for 2m00s; no engine", ended[0]);
        Assert.Equal("estimate  none: the run has ended", ended.First(fact => fact.StartsWith("estimate", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A piece of a plan update not carried out to an end and an iteration an update added and the run has not made yet
    /// - of a loop, or a round of a work-item group with no loop around it - are named among what is left, and a failure
    /// a handler took is counted among what is done.
    /// </summary>
    /// <param name="added">The iteration the update added, as its entry holds it.</param>
    /// <param name="said">How what is left says it.</param>
    [Theory]
    [InlineData("rounds#2", "round 2 of 'rounds'")]
    [InlineData("rounds.items+2", "round 2 of 'rounds.items'")]
    public void APieceNotCarriedOutAndAnOwedIteration_AreNamedAmongWhatIsLeft(string added, string said)
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: build
                type: orient
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 1), handledBy: "again+1", failure: "endedRun"),
                Step("again+1", nameof(FlowInstanceState.Done), "orient", visit: 2, started: At(10, 1), finished: At(10, 2)),
                Step("build+2", nameof(FlowInstanceState.NotStarted), "orient", visit: 2),
            ]);
        var updates = new FlowUpdatesFile(
            1,
            "r1",
            AsOf,
            [
                new FlowUpdateEntry(
                    "u1",
                    FlowUpdateCauses.Amendment,
                    FlowUpdateStates.Done,
                    At(10, 3),
                    Amendment: "narrow it",
                    Pieces: [new FlowUpdatePiece(FlowUpdatePieceKinds.Added, Item: new System.Text.Json.Nodes.JsonObject { ["name"] = "the parser" }, Changed: "only the parser")],
                    AddedIteration: added),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates, Engine, AsOf);

        Assert.Equal(
            "done      2 step instances ended, 1 failure a handler took; 0.00 USD, 0 tokens spent",
            facts.First(fact => fact.StartsWith("done", StringComparison.Ordinal)));
        Assert.Equal(
            $"left      at least 1 step instance, at most 1 step instance (nothing more can come); 1 piece of plan updates not carried out to an end yet; update u1 added {said}, and it is not made yet",
            facts.First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A stopped run has not ended - it goes on at its resume - and a stopped instance, which runs again there, is among
    /// what is left and in the estimate, waiting for the resume: never counted as ended.
    /// </summary>
    [Fact]
    public void AStoppedRun_HasNotEnded_ItsStoppedInstancesAreLeft()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: verify
                type: test
                dependsOn: [build]
            """);
        var flow = File(
            nameof(FlowRunStatus.Stopped),
            [
                Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 2)),
                Step("verify", nameof(FlowInstanceState.Done), "test", started: At(10, 2), finished: At(10, 4)),
                Step("verify+2", nameof(FlowInstanceState.Stopped), "test", started: At(10, 4), visit: 2, waitsOn: ["resume"]),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, NoEngine, AsOf);

        Assert.Equal(
            """
            run 'r1' (workflow 'flow'), as of 10:10:00Z: stopped, for 10m00s; no engine
            running   nothing is running
            waits     verify+2 waits for resume
            done      2 step instances ended; 0.00 USD, 0 tokens spent
            left      at least 1 step instance, at most 1 step instance (nothing more can come)
            estimate  between 2m00s and 2m00s more, from the 2 step instances that ended Done in this run
            """,
            string.Join("\n", facts));
    }

    /// <summary>
    /// A handler whose source is under way can still be raised, though it has no instance until it is: in each scope the
    /// source is under way in, the visits its bound leaves there - counted as the engine counts them, the visits raised
    /// in that scope alone - are among the most that is left and in the estimate's upper end, each at the handler's
    /// longest. Two items of a <c>forEach</c> run beside each other, so neither is laid after the other.
    /// </summary>
    [Fact]
    public void AHandlerWhoseSourceIsUnderWay_IsCountedInEachScopeAtItsBound()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: build
                    type: orient
                  - name: fix
                    type: orient
                    on:
                      step: build
                      outcome: failure
                    maxVisits: 2
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("plan", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 1)),
                Step("items[0]", nameof(FlowInstanceState.Running), group: true, started: At(10, 1)),
                Step("items[0].build", nameof(FlowInstanceState.Done), "orient", started: At(10, 1), finished: At(10, 2)),
                Step("items[0].fix", nameof(FlowInstanceState.Done), "orient", started: At(10, 2), finished: At(10, 4), raisedBy: "items[0].build"),
                Step("items[0].build+2", nameof(FlowInstanceState.Running), "orient", started: At(10, 9, 30), visit: 2),
                Step("items[1]", nameof(FlowInstanceState.Running), group: true, started: At(10, 1)),
                Step("items[1].build", nameof(FlowInstanceState.Running), "orient", started: At(10, 9, 30)),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf);

        Assert.Equal(
            "left      at least 4 step instances, at most 7 step instances (the loops' bounds)",
            facts.First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
        Assert.Equal(
            "estimate  between 30s and 7m00s more, from the 3 step instances that ended Done in this run",
            facts.First(fact => fact.StartsWith("estimate", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A wait for room is read as the admission classes its own words: a counter of the run's limits is one, left out of
    /// the estimate and said to be; a dependency whose name holds the word of a claim is a step, and is not.
    /// </summary>
    [Fact]
    public void AWaitForRoom_IsReadAsTheAdmissionClassesIt()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: claims
                type: orient
              - name: think
                type: orient
                dependsOn: [claims]
            """);
        FlowFile Waiting(string wait)
            => File(
                nameof(FlowRunStatus.Running),
                [
                    Step("claims", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 1)),
                    Step("claims+2", nameof(FlowInstanceState.Running), "orient", started: At(10, 9, 30), visit: 2),
                    Step("think", nameof(FlowInstanceState.NotStarted), "orient", waitsOn: [wait]),
                ]);
        string EstimateOf(FlowFile flow)
            => FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf).First(fact => fact.StartsWith("estimate", StringComparison.Ordinal));

        Assert.Equal(
            "estimate  between 30s and 30s more, from the 1 step instance that ended Done in this run; 1 step of what is left has not run to an end in this run and is not in the estimate: think; not counting a wait for a claim, for the run to be quiet or for room under its limits",
            EstimateOf(Waiting(FlowWaits.ReasoningAgents)));
        Assert.Equal(
            "estimate  between 30s and 30s more, from the 1 step instance that ended Done in this run; 1 step of what is left has not run to an end in this run and is not in the estimate: think",
            EstimateOf(Waiting("claims")));
    }
}
