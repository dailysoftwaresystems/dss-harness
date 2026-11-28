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

    /// <summary>
    /// One step instance's record, in the shape flow.json holds it: its declaration path the one its id spells, unless
    /// <paramref name="declared"/> says another - a step of the recovery taken inside a group is declared at
    /// <c>recover.&lt;step&gt;</c> though its id nests where the failure was taken.
    /// </summary>
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
        string? raisedBy = null,
        string? declared = null,
        IReadOnlyList<FlowHandlerState>? handlers = null)
        => new(
            instance,
            declared ?? FlowInstanceId.PathOf(instance),
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
            Handlers: handlers,
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
    /// A run a part of whose cost could not be counted says its dollars as at least what was counted, and why the rest is
    /// not known - never the counted sum as though it were the whole.
    /// </summary>
    [Fact]
    public void ARunAPartOfWhoseCostWasNotCounted_SaysAtLeastWhatWas_AndWhy()
    {
        var definition = Definition("""
            name: flow
            budget: { usd: 10 }
            steps:
              - name: build
                type: orient
            """);
        var flow = File(nameof(FlowRunStatus.Running), [Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 1))]) with
        {
            Spent = new FlowBudgetRecord(1.5, 900, 0, "no price for 'glm-5.3' (providers 'z', model 'glm-5.3': priceOutput)"),
        };

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf);

        Assert.Equal(
            "done      1 step instance ended; at least 1.50 of 10.00 USD (the rest is not known: no price for 'glm-5.3' (providers 'z', model 'glm-5.3': priceOutput)), 900 tokens spent",
            facts.Single(line => line.StartsWith("done ", StringComparison.Ordinal)));
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
            waits     question q1 (ask) since 10:02:00Z: answer with 'dssharness answer-orchestrator r1 <answer> --question q1'
                      plan update u1 under way: the run is held while 'plan+2' plans it
                      plan update u2 is recorded and waits for the run to be gone on with
                      hold waits for a claim
            done      2 step instances ended; 0.00 USD, 0 tokens spent
            left      at least 2 step instances, at most 4998 step instances (the engine's bounds and the run's own bound of at most 5000 instances); plan update u1 is still to be planned; plan update u2 is still to be planned
            estimate  between 1m00s and 1m00s more, from the 2 step instances that ended Done in this run; what no numeric bound holds is not in the estimate; 1 step of what is left has not run to an end in this run and is not in the estimate: hold; not counting the time a person takes to answer q1; not counting a wait for a claim, for the run to be quiet or for room under its limits
            """,
            string.Join("\n", facts));
    }

    /// <summary>
    /// What an open question waits on is its next step, by the one rule of it over what the engine is doing: a question
    /// still owing the recommendation it is answered with waits on the resume, which seeks it first, where no engine runs
    /// the run or a gone one - the answer command refuses any answer but abort until then; on its answer, taken now and
    /// ending the asking, where a live engine's advisor seeks it now - and wherever a live engine is starting, whatever the
    /// session before it left the question saying, for the start seeks every recommendation the run owes and takes an
    /// answer then; on the pause's advice, then its answer, where a live engine's pause defers it; and on the resume once
    /// the engine has gone where the live engine is stopping. A question recommended waits on its answer whatever the
    /// engine does.
    /// </summary>
    [Fact]
    public void AnOpenQuestion_WaitsOnItsNextStep_ByWhatTheEngineIsDoing()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: gate
                type: pause
                question: what now?
                options: [stay, go]
            """);
        var flow = File(nameof(FlowRunStatus.Paused), [Step("gate", nameof(FlowInstanceState.Paused), "pause", started: At(10, 2))]);
        FlowQuestionEntry Open(string id, FlowRecommendation recommendation)
            => new(id, "open", "pause", At(10, 2), [], [new FlowQuestionOption("stay", "it stays"), new FlowQuestionOption("go", "it goes")], [], Recommendation: recommendation);
        var questions = new FlowQuestionsFile(
            1,
            "r1",
            AsOf,
            [
                Open("q1", new FlowRecommendation("deferred", Why: "the run is pausing, and no reason step is started before its pause is complete: the recommendation is sought then, or when the run is resumed where a stop came first")),
                Open("q2", new FlowRecommendation("given", "go", Why: "it is ready", By: "advisor", Agent: "advisor")),
                Open("q3", new FlowRecommendation("sought", Why: "a recommendation is being sought from the advisor")),
            ]);
        string[] Waits(string engine)
            => [.. FlowStatus.Facts(definition, flow, questions, updates: null, new FlowEngineState(engine, engine == FlowEngineState.None ? null : "pid 7 on this machine", []), AsOf)
                .Where(fact => fact.StartsWith("waits", StringComparison.Ordinal) || fact.StartsWith("          question", StringComparison.Ordinal))];

        const string Answer = "          question q2 (pause) since 10:02:00Z: answer with 'dssharness answer-orchestrator r1 <answer> --question q2'";
        string Resume(string id, string start)
            => $"{start}question {id} (pause) since 10:02:00Z: its recommendation is still owed - no advisor seeks one between sessions, and 'dssharness resume-orchestrator r1' seeks it first; "
                + $"answer with 'dssharness answer-orchestrator r1 <answer> --question {id}' then, and 'dssharness answer-orchestrator r1 abort --question {id}' is taken now";

        foreach (var between in new[] { FlowEngineState.None, FlowEngineState.Gone })
        {
            Assert.Equal([Resume("q1", "waits     "), Answer, Resume("q3", "          ")], Waits(between));
        }

        const string PauseQ1 = "waits     question q1 (pause) since 10:02:00Z: its recommendation is still owed - the run seeks it once its pause is complete; "
            + "answer with 'dssharness answer-orchestrator r1 <answer> --question q1' then, and 'dssharness answer-orchestrator r1 abort --question q1' is taken now";
        const string AdvisedQ3 = "          question q3 (pause) since 10:02:00Z: its recommendation is being sought: answer with 'dssharness answer-orchestrator r1 <answer> --question q3', taken now and ending the asking";

        foreach (var live in new[] { FlowEngineState.Running, FlowEngineState.Pausing, FlowEngineState.Recording })
        {
            Assert.Equal([PauseQ1, Answer, AdvisedQ3], Waits(live));
        }

        const string AdvisedQ1 = "waits     question q1 (pause) since 10:02:00Z: its recommendation is being sought: answer with 'dssharness answer-orchestrator r1 <answer> --question q1', taken now and ending the asking";

        Assert.Equal([AdvisedQ1, Answer, AdvisedQ3], Waits(FlowEngineState.Starting));

        string OnceGone(string id, string start)
            => $"{start}question {id} (pause) since 10:02:00Z: its recommendation is still owed - the stop under way seeks none, and 'dssharness resume-orchestrator r1' seeks it first once the engine has gone; "
                + $"answer with 'dssharness answer-orchestrator r1 <answer> --question {id}' then, and 'dssharness answer-orchestrator r1 abort --question {id}' is taken now";

        Assert.Equal([OnceGone("q1", "waits     "), Answer, OnceGone("q3", "          ")], Waits(FlowEngineState.Stopping));
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
            left      at least 7 step instances, at most 4997 step instances (the engine's bounds and the run's own bound of at most 5000 instances); 'sweep' repeats with no numeric bound of its own
            estimate  between 30s and 1m00s more, from the 3 step instances that ended Done in this run; what no numeric bound holds is not in the estimate; 1 step of what is left has not run to an end in this run and is not in the estimate: sweep.clean
            """,
            string.Join("\n", facts));
    }

    /// <summary>
    /// A loop at its numeric bound with no <c>forEach</c> open beside it: the most counts only what its bound allows,
    /// said to be the engine's bounds alone, and the estimate's upper end holds the iterations left at the longest of
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
            "left      at least 4 step instances, at most 7 step instances (the engine's bounds)",
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
            "left      at least 4 step instances, at most 7 step instances (the engine's bounds); update u1 added round 4 of 'rounds', and it is not made yet",
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
            "left      at least 1 step instance, at most 1 step instance (the engine's bounds); question q2 (loopBound) can be answered with more past its bound",
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
            "left      at least 1 step instance, at most 2 step instances (the engine's bounds)",
            FlowStatus.Facts(definition, flow, questions, updates: null, NoEngine, AsOf).First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A step whose failure the recovery can take counts, to the recovery's bound, a visit of the recovery - its record
    /// and its steps - and the steps it sends the run back over - the step and what depends on it - at each visit; a
    /// step not drawn yet counts once: the most is what the engine can make, and the estimate's upper end holds each.
    /// </summary>
    [Fact]
    public void AFailureTheRecoveryCanTake_CountsItsVisitsAndWhatItSendsTheRunBackOver()
    {
        var definition = Definition("""
            name: flow
            recover:
              maxVisits: 2
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: build
                type: orient
                onFailure: recover
              - name: verify
                type: orient
                dependsOn: [build]
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("build", nameof(FlowInstanceState.Failed), "orient", started: At(10, 0), finished: At(10, 1)),
                Step("recover", nameof(FlowInstanceState.Done), group: true, started: At(10, 1), finished: At(10, 2), raisedBy: "build"),
                Step("recover.mend", nameof(FlowInstanceState.Done), "orient", started: At(10, 1), finished: At(10, 2)),
                Step("build+2", nameof(FlowInstanceState.Running), "orient", started: At(10, 9), visit: 2),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf);

        // verify, not drawn yet: 1; one more visit of the recovery (2 instances) with build and verify again (2): 4.
        Assert.Equal(
            "left      at least 1 step instance, at most 6 step instances (the engine's bounds)",
            facts.First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A step of the recovery taken inside a group is timed and ordered by its declaration, as its record holds it, never
    /// by the path its id spells where the failure was taken: the second visit's mend waits for its diagnose, so the
    /// estimate's lower end lays the two one after the other, each at the time the first visit's took.
    /// </summary>
    [Fact]
    public void AStepOfTheRecoveryTakenInsideAGroup_IsTimedAndOrderedByItsDeclaration()
    {
        var definition = Definition("""
            name: flow
            recover:
              maxVisits: 2
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: orient
                - name: mend
                  type: orient
                  dependsOn: [diagnose]
            steps:
              - name: stage
                steps:
                  - name: build
                    type: orient
                    onFailure: recover
            """);
        Assert.Empty(WorkflowValidator.Validate(TestAdvice.Named(definition), hasCommitTemplate: false));
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("stage", nameof(FlowInstanceState.Running), group: true, started: At(10, 0)),
                Step("stage.build", nameof(FlowInstanceState.Failed), "orient", started: At(10, 0), finished: At(10, 1)),
                Step("stage.recover", nameof(FlowInstanceState.Done), group: true, started: At(10, 1), finished: At(10, 5), raisedBy: "stage.build", declared: "recover"),
                Step("stage.recover.diagnose", nameof(FlowInstanceState.Done), "orient", started: At(10, 1), finished: At(10, 2), declared: "recover.diagnose"),
                Step("stage.recover.mend", nameof(FlowInstanceState.Done), "orient", started: At(10, 2), finished: At(10, 5), declared: "recover.mend"),
                Step("stage.build+2", nameof(FlowInstanceState.Failed), "orient", started: At(10, 5), finished: At(10, 6), visit: 2),
                Step("stage.recover+2", nameof(FlowInstanceState.Running), group: true, started: At(10, 6), visit: 2, raisedBy: "stage.build+2", declared: "recover"),
                Step("stage.recover+2.diagnose", nameof(FlowInstanceState.NotStarted), "orient", declared: "recover.diagnose"),
                Step("stage.recover+2.mend", nameof(FlowInstanceState.NotStarted), "orient", declared: "recover.mend"),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf);

        Assert.Equal(
            "estimate  between 4m00s and 4m00s more, from the 3 step instances that ended Done in this run",
            facts.First(fact => fact.StartsWith("estimate", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A group of the recovery taken inside another group counts what it may still make by its declaration, as its record
    /// holds it, never by the path its id spells where the failure was taken: a repeat of the recovery at its first of
    /// three iterations may still make two, each its record and its step.
    /// </summary>
    [Fact]
    public void AGroupOfTheRecoveryTakenInsideAnother_CountsWhatItMayStillMake()
    {
        var definition = Definition("""
            name: flow
            recover:
              maxVisits: 1
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: retry
                  repeat:
                    until: 'again.hasLegs'
                    maxIterations: 3
                  steps:
                    - name: again
                      type: orient
            steps:
              - name: stage
                steps:
                  - name: build
                    type: orient
                    onFailure: recover
            """);
        Assert.Empty(WorkflowValidator.Validate(TestAdvice.Named(definition), hasCommitTemplate: false));
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("stage", nameof(FlowInstanceState.Running), group: true, started: At(10, 0)),
                Step("stage.build", nameof(FlowInstanceState.Failed), "orient", started: At(10, 0), finished: At(10, 1)),
                Step("stage.recover", nameof(FlowInstanceState.Running), group: true, started: At(10, 1), raisedBy: "stage.build", declared: "recover"),
                Step("stage.recover.retry", nameof(FlowInstanceState.Running), group: true, started: At(10, 1), declared: "recover.retry"),
                Step("stage.recover.retry#1", nameof(FlowInstanceState.Running), group: true, started: At(10, 1), iteration: 1, declared: "recover.retry"),
                Step("stage.recover.retry#1.again", nameof(FlowInstanceState.Running), "orient", started: At(10, 1), declared: "recover.retry.again"),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf);

        // Two iterations of retry past the one it made, each its record and again: 4.
        Assert.Equal(
            "left      at least 5 step instances, at most 9 step instances (the engine's bounds)",
            facts.First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A handler that can still be raised counts, to its bound, its own visits and the steps its <c>then</c> sends the
    /// run back over - the step it names and what depends on it in its scope - at each visit; one that sends the run back
    /// over the group it is in is held to its bound in the scope its loop goes round in, as the engine counts it: past
    /// its visits made in every visit of that group, each turn a new visit of the group whole.
    /// </summary>
    [Fact]
    public void AHandlersRedirect_CountsWhatItSendsTheRunBackOver_AtEachVisit()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: test
                type: orient
                dependsOn: [build]
              - name: fix
                type: orient
                on: {step: test, outcome: failure}
                maxVisits: 2
                then: build
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 1)),
                Step("test", nameof(FlowInstanceState.Running), "orient", started: At(10, 9)),
            ]);

        // Two visits of fix, each with build and test again: 6.
        Assert.Equal(
            "left      at least 1 step instance, at most 7 step instances (the engine's bounds)",
            FlowStatus.Facts(definition, flow, questions: null, updates: null, Engine, AsOf).First(fact => fact.StartsWith("left", StringComparison.Ordinal)));

        var outward = Definition("""
            name: flow
            steps:
              - name: prepare
                type: orient
              - name: stage
                dependsOn: [prepare]
                steps:
                  - name: test
                    type: orient
                  - name: fix
                    type: orient
                    on: {step: test, outcome: failure}
                    then: prepare
            """);
        var staged = File(
            nameof(FlowRunStatus.Running),
            [
                Step("prepare", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 1)),
                Step("stage", nameof(FlowInstanceState.Skipped), group: true, started: At(10, 1), finished: At(10, 4)),
                Step("stage.test", nameof(FlowInstanceState.Failed), "orient", started: At(10, 1), finished: At(10, 2)),
                Step("stage.fix", nameof(FlowInstanceState.Done), "orient", started: At(10, 2), finished: At(10, 3), raisedBy: "stage.test"),
                Step("prepare+2", nameof(FlowInstanceState.Done), "orient", started: At(10, 4), finished: At(10, 5), visit: 2),
                Step("stage+2", nameof(FlowInstanceState.Running), group: true, started: At(10, 5), visit: 2),
                Step("stage+2.test", nameof(FlowInstanceState.Running), "orient", started: At(10, 9)),
            ]);

        // The two instances under way, and fix's loop going round at the top, where one of its three visits was made: two
        // turns left, each fix, prepare and stage whole (its own record, test and fix's three visits) - 2 + 2 + 10 - with
        // fix's three in the stage visit under way counted there too.
        Assert.Equal(
            "left      at least 2 step instances, at most 19 step instances (the engine's bounds)",
            FlowStatus.Facts(outward, staged, questions: null, updates: null, Engine, AsOf).First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
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
    /// A step that ended an instance Done within the second it began - the records keep times to the second - is a step
    /// that ended Done all the same: its next instance is measured, at no time, and never said to be of a step that has
    /// ended nothing.
    /// </summary>
    [Fact]
    public void AStepThatEndedDoneWithinASecond_IsMeasured_NeverSaidToHaveEndedNothing()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: build
                type: orient
                maxVisits: 2
            """);
        var facts = FlowStatus.Facts(
            definition,
            File(
                nameof(FlowRunStatus.Running),
                [
                    Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 0)),
                    Step("build+2", nameof(FlowInstanceState.NotStarted), "orient", visit: 2),
                ]),
            questions: null,
            updates: null,
            Engine,
            AsOf);
        var estimate = facts.First(fact => fact.StartsWith("estimate", StringComparison.Ordinal));

        Assert.StartsWith("estimate  between 0s and 0s more, from the 1 step instance that ended Done in this run", estimate, StringComparison.Ordinal);
        Assert.DoesNotContain("ended no instance Done", estimate, StringComparison.Ordinal);
        Assert.DoesNotContain("has not run to an end", estimate, StringComparison.Ordinal);
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
                Step("verify+2", nameof(FlowInstanceState.Stopped), "test", started: At(10, 4), visit: 2, waitsOn: [FlowWaits.Resume]),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, NoEngine, AsOf);

        Assert.Equal(
            """
            run 'r1' (workflow 'flow'), as of 10:10:00Z: stopped, for 10m00s; no engine
            running   nothing is running
            waits     verify+2 waits for the resume
            done      2 step instances ended; 0.00 USD, 0 tokens spent
            left      at least 1 step instance, at most 1 step instance (nothing more can come)
            estimate  between 2m00s and 2m00s more, from the 2 step instances that ended Done in this run
            """,
            string.Join("\n", facts));
    }

    /// <summary>
    /// A run paused with nothing left but a failure its ending owes the session that goes on - a pause recorded the step's
    /// failure while it drained, and raised nothing - has work left by the engine's own rule of what keeps a run from done
    /// (<see cref="FlowInstance.IsUnended"/>): the failure is counted and named among what is left, and the estimate says it
    /// has no time to go by - never that nothing is left, which a run its engine still owes work never is.
    /// </summary>
    [Fact]
    public void AFailureOwedToTheSessionThatGoesOn_IsLeft_AndHasNoTimeToGoBy()
    {
        var definition = Definition("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: verify
                type: test
                dependsOn: [build]
                onFailure: pause
            """);
        var flow = File(
            nameof(FlowRunStatus.Paused),
            [
                Step("build", nameof(FlowInstanceState.Done), "orient", started: At(10, 0), finished: At(10, 2)),
                Step("verify", nameof(FlowInstanceState.Failed), "test", started: At(10, 2), finished: At(10, 4), failure: FlowStepRecord.NameOf(FlowFailure.Owed)),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, NoEngine, AsOf);

        Assert.Equal(
            """
            left      at least 1 step instance, at most 1 step instance (nothing more can come); 1 failure owed to the session that goes on, which its handlers, its policy or the recovery decide: verify
            estimate  none: what is left has no time of this run to go by - 1 failure owed to the session that goes on, which its handlers, its policy or the recovery decide: verify
            """,
            string.Join("\n", facts.Skip(4)));
    }

    /// <summary>
    /// A handler an ending owes a raise - a pause recorded its source's ending and raised nothing - is left by the engine's
    /// own rule of what keeps a run from done (<see cref="FlowInstance.RaisesOwed"/>), as the instance the resume raises:
    /// counted and named among what is left by the visit the resume makes - <c>fix+2</c>, for the scope holds the visit
    /// <c>fix</c> an earlier ending raised - and timed in the estimate by its step's own earlier visit, never left out as
    /// though nothing were left.
    /// </summary>
    [Fact]
    public void AHandlerAnEndingOwesARaise_IsLeft_AndTimedByItsStep()
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
                then: build
                maxVisits: 3
            """);
        var flow = File(
            nameof(FlowRunStatus.Paused),
            [
                Step("build", nameof(FlowInstanceState.Failed), "orient", started: At(10, 0), finished: At(10, 1), handledBy: "fix"),
                Step("fix", nameof(FlowInstanceState.Done), "orient", started: At(10, 1), finished: At(10, 4), raisedBy: "build"),
                Step("build+2", nameof(FlowInstanceState.Failed), "orient", started: At(10, 4), finished: At(10, 5), visit: 2, handlers: [new FlowHandlerState("fix", "owed")]),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, NoEngine, AsOf);

        Assert.Equal(
            """
            left      at least 1 step instance, at most 1 step instance (nothing more can come); 1 handler an ending owes a raise: fix+2
            estimate  between 3m00s and 3m00s more, from the 1 step instance that ended Done in this run
            """,
            string.Join("\n", facts.Skip(4)));
    }

    /// <summary>
    /// A raise whose visit is in the records is made, whatever its source's record still says - an older build wrote the
    /// visit before it wrote its source's raise, so a crash between the two, or a status read between them, finds the raise
    /// owed beside the visit it made: the visit is counted once, as itself, never beside a raise of the same id the facts
    /// would invent - which counted it twice and, both timed by the handler's step, failed the whole facts call - and no
    /// handler is said to be owed a raise. Here a loop's second iteration, whose failed build's handler a crash cut off
    /// after its visit was recorded.
    /// </summary>
    [Fact]
    public void ARaiseWhoseVisitIsRecorded_IsCountedOnceAsThatVisit_EvenWhereItsSourceStillSaysItOwed()
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
                  - name: fix
                    type: orient
                    on:
                      step: build
                      outcome: failure
            """);
        var flow = File(
            nameof(FlowRunStatus.Paused),
            [
                Step("rounds", nameof(FlowInstanceState.Running), group: true, started: At(10, 0)),
                Step("rounds#1", nameof(FlowInstanceState.Done), group: true, started: At(10, 0), finished: At(10, 4), iteration: 1),
                Step("rounds#1.build", nameof(FlowInstanceState.Failed), "orient", started: At(10, 0), finished: At(10, 1), handledBy: "fix", handlers: [new FlowHandlerState("fix", "done")]),
                Step("rounds#1.fix", nameof(FlowInstanceState.Done), "orient", started: At(10, 1), finished: At(10, 4), raisedBy: "rounds#1.build"),
                Step("rounds#2", nameof(FlowInstanceState.Running), group: true, started: At(10, 4), iteration: 2),
                Step("rounds#2.build", nameof(FlowInstanceState.Failed), "orient", started: At(10, 4), finished: At(10, 5), handlers: [new FlowHandlerState("fix", "owed")]),
                Step("rounds#2.fix", nameof(FlowInstanceState.NotStarted), "orient", raisedBy: "rounds#2.build"),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, NoEngine, AsOf);

        Assert.Equal(
            """
            left      at least 3 step instances, at most 3 step instances (nothing more can come)
            estimate  between 3m00s and 3m00s more, from the 2 step instances that ended Done in this run
            """,
            string.Join("\n", facts.Skip(4)));
    }

    /// <summary>
    /// A raise its source's record says with no visit of it recorded - this build writes the source's raise before the
    /// visit's first record, and a crash came between the two, or the status was read between them - is a raise still
    /// owed, by the engine's own rule (<see cref="FlowInstance.RaisesOwed"/>): counted and named by the visit the resume
    /// raises, never taken for a raise made, which no visit stands for.
    /// </summary>
    [Fact]
    public void ARaiseSaidWithNoVisitRecorded_IsStillOwed_AndNamedByTheVisitTheResumeRaises()
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
                then: build
                maxVisits: 3
            """);
        var flow = File(
            nameof(FlowRunStatus.Running),
            [
                Step("build", nameof(FlowInstanceState.Failed), "orient", started: At(10, 0), finished: At(10, 1), handledBy: "fix", handlers: [new FlowHandlerState("fix", "done")]),
                Step("fix", nameof(FlowInstanceState.Done), "orient", started: At(10, 1), finished: At(10, 4), raisedBy: "build"),
                Step("build+2", nameof(FlowInstanceState.Failed), "orient", started: At(10, 4), finished: At(10, 5), visit: 2, handlers: [new FlowHandlerState("fix", "raised")]),
            ]);

        var facts = FlowStatus.Facts(definition, flow, questions: null, updates: null, NoEngine, AsOf);

        Assert.Equal(
            "left      at least 1 step instance, at most 1 step instance (nothing more can come); 1 handler an ending owes a raise: fix+2",
            facts.First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
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
            "left      at least 4 step instances, at most 7 step instances (the engine's bounds)",
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
