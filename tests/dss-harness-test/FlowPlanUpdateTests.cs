using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The planning of a plan update: the step the <c>amend</c> block names raised by an update alone, in the newest
/// instance of its scope and ahead of everything else; the run held while the update is under way - nothing started
/// outside that scope, what is in flight untouched; <c>then</c> followed by the redirect's machinery inside that
/// scope; the updated plan standing when the scope has come to rest; <c>{update.cause}</c> and its two fellows
/// filled in the prompts of the scope, and the sentence that says none is under way outside one; an update queued
/// behind one under way, taken in the order given; a failed planning step's <c>onFailure</c> deciding, an update
/// given up with the hold ended and the record saying no plan holds the change; and the whole of it across a crash
/// and a safe pause. All over fake step runners and the run's real records in a directory of their own.
/// </summary>
public sealed class FlowPlanUpdateTests
{
    /// <summary>Every bound a test of a session waits for: a session that hangs fails the test rather than the run's minutes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The flow these tests update the plan of: a planning block whose <c>planupdate</c> step is raised by an update
    /// alone - beside <c>plan</c>, its ordinary planner, and <c>check</c>, the step the block's <c>then</c> names -
    /// a pause the block ends at, a pause after it, and a step after that, held by an update as it is outside the
    /// scope.
    /// </summary>
    private const string Flow = """
        name: flow
        inputs:
          - name: request
        steps:
          - name: ask
            type: pause
            question: '{input.request}'
            options: [stay, go]
          - name: planning
            dependsOn: [ask]
            gather: [scope]
            steps:
              - name: plan
                type: reason
                agent: planner
                prompt: plan.md
                outputs: plan.json
              - name: planupdate
                type: reason
                agent: planner
                prompt: plan.md
                outputs: plan.json
              - name: check
                type: orient
                dependsOn: [plan]
              - name: probe
                type: pause
                question: 'cause: {update.cause}|since: {update.since}|work: {update.work}'
                options: [stay, go]
                dependsOn: [check]
          - name: gate
            type: pause
            question: held?
            options: [stay, go]
            dependsOn: [planning]
          - name: later
            type: orient
            dependsOn: [gate]
        """;

    /// <summary>The flow without its probe, for the tests that drive an update whole without a pause inside the scope.</summary>
    private const string Lean = """
        name: flow
        inputs:
          - name: request
        steps:
          - name: ask
            type: pause
            question: '{input.request}'
            options: [stay, go]
          - name: planning
            dependsOn: [ask]
            gather: [scope]
            steps:
              - name: plan
                type: reason
                agent: planner
                prompt: plan.md
                outputs: plan.json
              - name: planupdate
                type: reason
                agent: planner
                prompt: plan.md
                outputs: plan.json
              - name: check
                type: orient
                dependsOn: [plan]
          - name: gate
            type: pause
            question: held?
            options: [stay, go]
            dependsOn: [planning]
          - name: later
            type: orient
            dependsOn: [gate]
        """;

    /// <summary>The flow of <see cref="Lean"/> whose planning step gives a failed update up: <c>onFailure: continue</c>.</summary>
    private static WorkflowDefinition GivingUp
    {
        get
        {
            var parsed = Parse(Lean);

            return parsed with
            {
                Steps = [.. parsed.Steps.Select(step => step.Name == "planning"
                    ? step with { Steps = [.. step.Steps.Select(inner => inner.Name == "planupdate" ? inner with { OnFailure = "continue" } : inner)] }
                    : step)],
            };
        }
    }

    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text) with
        {
            Amend = new FlowAmend("request", 5, null, "planning.planupdate", "check", "planning.scope", "later", "later", "later", null),
        };

    /// <summary>The run's records in a directory of their own.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FlowRunRecords Records) Records(string run, string directory, WorkflowDefinition definition)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        return (harness, layout, new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of(run, definition)));
    }

    /// <summary>A runner that answers each step as its test scripted it, holds one open where the test asks, and tells the test each start.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>Waits until the coordinator has asked for <paramref name="instance"/> once.</summary>
        public Task StartedAsync(string instance)
        {
            lock (_starts)
            {
                if (!_starts.TryGetValue(instance, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _starts.Add(instance, started);
                }

                return started.Task;
            }
        }

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => result;
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it, or a stop cancels it.</summary>
        public TaskCompletionSource<FlowStepResult> Hold(string step)
        {
            var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[step] = gate;
            return gate;
        }

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add(run.Instance);

            lock (_starts)
            {
                if (!_starts.TryGetValue(run.Instance, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _starts.Add(run.Instance, started);
                }

                started.TrySetResult();
            }

            if (_scripts.TryGetValue(run.Step.Name, out var script))
            {
                return script(run);
            }

            if (_gates.TryGetValue(run.Step.Name, out var gate))
            {
                return await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: run.Step.Name == "plan" ? Output("""{"scope": []}""") : null);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>("what I did, what is left, what I was in the middle of");
    }

    /// <summary>A step's output, read from JSON text.</summary>
    private static JsonNode? Output(string json)
        => JsonNode.Parse(json);

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>
    /// An update taken where the plan's scope has begun raises the planning step - a new visit of it in the newest
    /// instance of that scope, which had ended and is in progress again - admitted ahead of everything else while
    /// the run is held: nothing starts outside the scope, and what the session left waiting outside waits still. The
    /// step's <c>then</c> is followed by the redirect's machinery inside the scope, a question open on the step it
    /// resets withdrawn, and once the scope has come to rest the plan stands: the update is done, the hold ends, and
    /// the steps that start from here on go on. The prompts of the scope read the update while it is under way, and
    /// the sentence that says none is outside one.
    /// </summary>
    [Fact]
    public async Task AnUpdateRaisesThePlanningStep_HoldsTheRun_AndStandsWhenItsScopeRests()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Flow);
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // To the pause inside the planning, where no update is under way: its own question says so.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            _ = await coordinator.RunAsync(Token);
            Assert.Equal(
                "cause: no plan update is under way|since: no plan update is under way|work: no plan update is under way",
                coordinator.OpenQuestions.Single().Problem);
            var probe = coordinator.OpenQuestions.Single().Id;
            Assert.True(coordinator.TryAnswer(probe, "go", null, out _));

            // Out of the planning and to the pause after it: the scope an update is planned in has begun and ended.
            _ = await coordinator.RunAsync(Token);
            var gate = coordinator.OpenQuestions.Single().Id;

            // The amendment, between two sessions: the update is under way, the planning step raised in the scope's
            // one instance, the run held - `later`, ready and outside once the pause is answered, starts nothing yet -
            // and flow.json says the hold.
            var was = runner.Started.Count;
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("in fact, build only the parser", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.Null(problem);
            Assert.True(coordinator.TryAnswer(gate, "go", null, out _));

            var flow = harness.OrchestrationStore.ReadFlow(layout)!;
            Assert.Equal("u1", flow.Hold!.Update);
            Assert.Equal(FlowUpdateStates.UnderWay, harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single().State);

            // The session drives the update whole: the planning step first, the check it sends the run back to again,
            // and the probe the check's resetting drew again - asking now with what raised the update, word for word.
            _ = await coordinator.RunAsync(Token);

            var asked = coordinator.OpenQuestions.Single();
            Assert.StartsWith("cause: the request as it stood:\nbuild the tool\n\namended:", asked.Problem, StringComparison.Ordinal);
            Assert.Contains("\n\namended:in fact, build only the parser|since: ", asked.Problem, StringComparison.Ordinal);
            Assert.Contains("|work: in flight: 'planning.probe+2' (probe)", asked.Problem, StringComparison.Ordinal);
            Assert.Equal(FlowUpdateStates.UnderWay, harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single().State);
            Assert.DoesNotContain(runner.Started, started => started == "later");

            // What the check's resetting drew is part of the update: the probe's answer settles it, the scope comes
            // to rest, the plan stands, and only then does the held step outside start.
            Assert.True(coordinator.TryAnswer(asked.Id, "go", null, out _));
            var settled = await coordinator.RunAsync(Token);

            Assert.Equal(FlowRunStatus.Done, settled.Status);

            var update = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single();
            Assert.Equal(("u1", FlowUpdateStates.Done, "check"), (update.Id, update.State, update.Then));
            Assert.NotNull(update.Step);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Single(steps, step => step.Instance == "planning.planupdate" && step.RaisedByUpdate == "u1");
            Assert.Equal(2, steps.Count(step => step.Step == "planning.check"));
            Assert.Contains(steps, step => step.Step == "planning.check" && step.LeftBy == "planning.planupdate");
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)!.Hold);

            // Ahead of everything else: the planning step started first among what the session started, and `later` -
            // ready before it - started only once the plan stood.
            var session = runner.Started.Skip(was).ToList();
            Assert.Equal("planning.planupdate", session[0]);
            Assert.True(session.IndexOf("later") > session.IndexOf("planning.check", 1), string.Join(", ", session));
        }
    }

    /// <summary>
    /// An update that arrives while another is under way is queued, and taken when that one is done, in the order
    /// given: each is planned by its own visit of the planning step, and the check runs again for each.
    /// </summary>
    [Fact]
    public async Task AnUpdateQueuedBehindOneUnderWay_IsTakenWhenThatOneIsDone_InTheOrderGiven()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Lean);
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            _ = await coordinator.RunAsync(Token);
            var gate = coordinator.OpenQuestions.Single().Id;

            // Two amendments, one behind the other: the first is under way, the second queued behind it.
            Assert.Equal(FlowHandedOver.Taken, coordinator.AmendmentHandedOver("first change", "amend-orchestrator-request", out _));
            Assert.Equal(FlowHandedOver.Taken, coordinator.AmendmentHandedOver("second change", "amend-orchestrator-request", out _));

            var updates = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates;
            Assert.Equal(("u1", FlowUpdateStates.UnderWay), (updates[0].Id, updates[0].State));
            Assert.Equal(("u2", FlowUpdateStates.Queued), (updates[1].Id, updates[1].State));

            Assert.True(coordinator.TryAnswer(gate, "go", null, out _));

            // One session drives both: the first update whole, then - as it is done - the second taken and driven
            // whole, each by its own visit of the planning step and its own re-run of the check.
            var settled = await coordinator.RunAsync(Token);

            Assert.Equal(FlowRunStatus.Done, settled.Status);

            var taken = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates;
            Assert.Equal([("u1", FlowUpdateStates.Done), ("u2", FlowUpdateStates.Done)], taken.Select(update => (update.Id, update.State)));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Equal(["u1", "u2"], steps.Where(step => step.Step == "planning.planupdate").Select(step => step.RaisedByUpdate));
            Assert.Equal(3, steps.Count(step => step.Step == "planning.check"));
        }
    }

    /// <summary>
    /// A planning step that fails is decided by its own <c>onFailure</c>, as any step is: <c>continue</c> gives the
    /// update up - <c>failed</c>, nothing placed, nobody told, the hold ended, the run going on with the plan as it
    /// was - and the update's entry says the request is recorded and no plan holds it.
    /// </summary>
    [Fact]
    public async Task AFailedPlanningStepDecidedByItsOnFailure_GivesTheUpdateUp_AndTheRunGoesOnAsItWas()
    {
        using var temp = new TempDirectory();
        var definition = GivingUp;
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner().On("planupdate", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the agent refused"));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            _ = await coordinator.RunAsync(Token);
            var gate = coordinator.OpenQuestions.Single().Id;
            Assert.Equal(FlowHandedOver.Taken, coordinator.AmendmentHandedOver("in fact, build only the parser", "amend-orchestrator-request", out _));
            Assert.True(coordinator.TryAnswer(gate, "go", null, out _));

            var end = await coordinator.RunAsync(Token);

            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single();
            Assert.Equal(("u1", FlowUpdateStates.Failed), (update.Id, update.State));
            Assert.Equal("its planning step 'planning.planupdate' ended failed: the request is recorded and no plan holds it", update.Why);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Equal(1, steps.Count(step => step.Step == "planning.check"));
            Assert.Contains(runner.Started, started => started == "later");
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)!.Hold);
        }
    }

    /// <summary>
    /// An update under way survives a crash: the hold and the visit the raise made are read back from the run's own
    /// records, and the session that takes the run up goes on with the update first - what its <c>then</c> set going
    /// ends, the plan stands, and the steps the hold kept back start from there.
    /// </summary>
    [Fact]
    public async Task AnUpdateUnderWay_SurvivesACrash_AndTheResumeGoesOnWithItFirst()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Flow);
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            _ = await coordinator.RunAsync(Token);
            var gate = coordinator.OpenQuestions.Single().Id;
            Assert.Equal(FlowHandedOver.Taken, coordinator.AmendmentHandedOver("in fact, build only the parser", "amend-orchestrator-request", out _));
            Assert.True(coordinator.TryAnswer(gate, "go", null, out _));

            // The session begins the update - the planning step ends done, its then decided and made, and the probe
            // the check's resetting drew again asks - and the process dies while the update is under way.
            _ = await coordinator.RunAsync(Token);

            Assert.Equal(FlowUpdateStates.UnderWay, harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single().State);
            Assert.NotNull(harness.OrchestrationStore.ReadFlow(layout)!.Hold);
        }

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var resumed = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition)), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The question the probe asked is open, and the run paused beside it with the update under way: the
            // answer drives the update to its end - the scope rests, the plan stands, the hold ends, `later` runs.
            var asked = resumed.OpenQuestions.Single();
            Assert.StartsWith("cause: the request as it stood:", asked.Problem, StringComparison.Ordinal);
            Assert.True(resumed.TryAnswer(asked.Id, "go", null, out _));

            var end = await resumed.RunAsync(Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);

            var update = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single();
            Assert.Equal(("u1", FlowUpdateStates.Done), (update.Id, update.State));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Equal(2, steps.Count(step => step.Step == "planning.check"));
            Assert.Contains(runner.Started, started => started == "later");
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)!.Hold);
        }
    }

    /// <summary>
    /// A changed answer that arrives while an update is under way is queued behind it, as an amendment is: the answer
    /// is not applied until its turn, and when the first update is done the change is applied and planned - the set's
    /// one change, both answers kept, the second naming the update that took it.
    /// </summary>
    [Fact]
    public async Task AChangedAnswerArrivingWhileAnUpdateIsUnderWay_IsQueuedBehindIt_AndAppliedAtItsTurn()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Lean);
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            var ask = coordinator.OpenQuestions.Single().Id;
            Assert.True(coordinator.TryAnswer(ask, "go", null, out _));

            _ = await coordinator.RunAsync(Token);
            var gate = coordinator.OpenQuestions.Single().Id;

            // The amendment is under way, and the change of the last answer a person gave is queued behind it.
            Assert.Equal(FlowHandedOver.Taken, coordinator.AmendmentHandedOver("first change", "amend-orchestrator-request", out _));
            Assert.True(coordinator.TryChangeAnswer("stay", null, out var notTaken, "answer-orchestrator"), notTaken ?? "changed");
            Assert.Null(notTaken);

            var updates = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates;
            Assert.Equal(("u1", FlowUpdateStates.UnderWay), (updates[0].Id, updates[0].State));
            Assert.Equal(("u2", FlowUpdateStates.Queued, "changedAnswer", ask, "go", "stay"), (updates[1].Id, updates[1].State, updates[1].Cause, updates[1].Question, updates[1].FromAnswer, updates[1].ToAnswer));
            Assert.Single(QuestionsOf(harness, layout).Questions.First(question => question.Id == ask).Answers);

            // The session drives the first whole, then the change at its turn: applied - both answers kept, the new
            // one naming its update - and planned by its own visit of the planning step, whole in turn.
            Assert.True(coordinator.TryAnswer(gate, "go", null, out _));
            var settled = await coordinator.RunAsync(Token);

            Assert.Equal(FlowRunStatus.Done, settled.Status);

            var taken = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates;
            Assert.Equal([("u1", FlowUpdateStates.Done), ("u2", FlowUpdateStates.Done)], taken.Select(update => (update.Id, update.State)));

            var answers = QuestionsOf(harness, layout).Questions.First(question => question.Id == ask).Answers;
            Assert.Equal(2, answers.Count);
            Assert.Equal(("go", "stay", "u2"), (answers[0].Answer, answers[1].Answer, answers[1].Update));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Equal(["u1", "u2"], steps.Where(step => step.Step == "planning.planupdate").Select(step => step.RaisedByUpdate));
            Assert.Equal(3, steps.Count(step => step.Step == "planning.check"));
        }
    }

    /// <summary>The run's questions, read whole from the store.</summary>
    private static FlowQuestionsFile QuestionsOf(HarnessFactory harness, OrchestratorLayout layout)
        => harness.OrchestrationStore.ReadFlowQuestions(layout)!;

    /// <summary>
    /// No next iteration is drawn while an update is under way and the loop is outside the scope it is planned in: the
    /// planning step is raised in the newest instance of its scope - the repeat's last iteration - and the plan stands
    /// between the iterations, the next drawn on the plan as it now is.
    /// </summary>
    [Fact]
    public async Task NoNextIterationIsDrawnWhileAnUpdateIsUnderWay_ThePlanStandsBetweenThem()
    {
        using var temp = new TempDirectory();

        // The planning block a repeat runs: at its bound the loop asks, and `more` grants one more round - the moment
        // an update holds, for the next round would begin on a plan about to change.
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            inputs:
              - name: request
            steps:
              - name: ask
                type: pause
                question: '{input.request}'
                options: [stay, go]
              - name: planning
                dependsOn: [ask]
                repeat:
                  until: 'false'
                  maxIterations: 2
                gather: [scope]
                steps:
                  - name: plan
                    type: reason
                    agent: planner
                    prompt: plan.md
                    outputs: plan.json
                  - name: planupdate
                    type: reason
                    agent: planner
                    prompt: plan.md
                    outputs: plan.json
                  - name: check
                    type: orient
                    dependsOn: [plan]
              - name: gate
                type: pause
                question: held?
                options: [stay, go]
                dependsOn: [planning]
            """) with
        {
            Amend = new FlowAmend("request", 5, null, "planning.planupdate", "check", "planning.scope", "gate", "gate", "gate", null),
        };

        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner();

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            // Both rounds run, and the loop asks at its bound: nothing of a third round has begun.
            _ = await coordinator.RunAsync(Token);
            var bound = coordinator.OpenQuestions.Single();
            Assert.Equal("loopBound", bound.Kind);
            Assert.Equal(2, runner.Started.Count(started => started.EndsWith(".plan", StringComparison.Ordinal)));

            // The amendment raises the planning step of the SECOND round's scope - the newest instance - and `more`
            // grants the round: the session runs the update whole first, the third round drawn only once the plan
            // stands, on the plan as it now is.
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("in fact, build only the parser", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(bound.Id, "more", null, out _));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "accept", null, out _));
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var end = await coordinator.RunAsync(Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);

            var update = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single();
            Assert.Equal(("u1", FlowUpdateStates.Done), (update.Id, update.State));

            // The raised visit is of the second round's scope, the check its then re-ran before anything of the third
            // round, and the third round's own planning started only after the plan stood: no item of the next round
            // was begun on a plan that was about to change.
            var raised = runner.Started.Single(started => started.Contains("planupdate"));
            Assert.StartsWith("planning#2.", raised, StringComparison.Ordinal);
            Assert.True(
                runner.Started.IndexOf(raised) < runner.Started.IndexOf("planning#2.check+2")
                    && runner.Started.IndexOf("planning#2.check+2") < runner.Started.IndexOf("planning#3.plan"),
                string.Join(", ", runner.Started));
        }
    }

    /// <summary>
    /// A safe pause finds an update under way as it finds any other work: the planning step in flight hands off, the
    /// hold and what the update owes are in <c>flow.json</c>, and the resume goes on with the update first - the
    /// handed-off step asked again with its handoff, the plan standing and the run going on from there.
    /// </summary>
    [Fact]
    public async Task ASafePauseFindsAnUpdateUnderWay_AndTheResumeGoesOnWithItFirst()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Lean);
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner();
        var planning = runner.Hold("planupdate");

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            _ = await coordinator.RunAsync(Token);
            var gate = coordinator.OpenQuestions.Single().Id;
            Assert.Equal(FlowHandedOver.Taken, coordinator.AmendmentHandedOver("in fact, build only the parser", "amend-orchestrator-request", out _));
            Assert.True(coordinator.TryAnswer(gate, "go", null, out _));

            // The session starts the planning step - held open by the test - and a pause is asked of the engine.
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("planning.planupdate");
            coordinator.RequestPause("command");

            var paused = await session;

            Assert.Equal(FlowRunStatus.Paused, paused.Status);
            Assert.Equal(FlowUpdateStates.UnderWay, harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single().State);
            Assert.NotNull(harness.OrchestrationStore.ReadFlow(layout)!.Hold);
        }

        planning.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var resumed = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition)), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            var end = await resumed.RunAsync(Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);

            var update = harness.OrchestrationStore.ReadFlowUpdates(layout)!.Updates.Single();
            Assert.Equal(("u1", FlowUpdateStates.Done), (update.Id, update.State));

            // The handed-off planning step was asked again - the same visit, a new attempt - and its update went on
            // to stand.
            Assert.Equal(2, runner.Started.Count(started => started == "planning.planupdate"));
            Assert.Contains(runner.Started, started => started == "later");
            Assert.Null(harness.OrchestrationStore.ReadFlow(layout)!.Hold);
        }
    }
}
