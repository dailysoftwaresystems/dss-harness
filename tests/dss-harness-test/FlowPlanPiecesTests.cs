using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The pieces of a plan update: the planning step's scope read once the updated plan stands, and each piece placed -
/// one more work item of the round, a waiting instance given the piece's item in the place of its own or withdrawn
/// before it began, one under way brought to rest with the handoff turn's bound and record and visited again in its
/// place with its agent, one that has ended made again with new agents - the three alternatives at one place of the
/// work-item group, the one that carries the instance running under the add step's name and the other two skipped
/// with the reason, and the pieces a round that has not opened waits for joining at the check that opens it, over that
/// round's own items and the owed ones as one list. All over fake step runners and the run's real records in a
/// directory of their own.
/// </summary>
public sealed class FlowPlanPiecesTests
{
    /// <summary>Every bound a test of a session waits for: a session that hangs fails the test rather than the run's minutes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The flow these tests update the plan of: a planning block whose <c>planupdate</c> step is raised by an update
    /// alone, going on with <c>plan</c>'s work, beside <c>check</c>, the step the block's <c>then</c> names - and a
    /// round of work items: <c>item-check</c> opens it, <c>items</c> expands from what it checked, and each item is
    /// built by one of the three alternatives the <c>amend</c> block names, the agent its <c>make</c> step makes,
    /// asked at a <c>probe</c> that reads the piece it carries, and closed by a <c>drop</c> when it is over.
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
            steps:
              - name: plan
                type: reason
                agent: planner
                resume: plan
                prompt: plan.md
                outputs: plan.json
              - name: planupdate
                type: reason
                agent: planner
                resume: plan
                prompt: plan-update.md
                outputs: plan.json
              - name: check
                type: validate-plan
                items: 'planning.plan.latest.items'
                scope: 'planning.plan.latest.scope'
                dependsOn: [plan]
          - name: rounds
            dependsOn: [planning]
            steps:
              - name: item-check
                type: validate-plan
                items: 'planning.plan.latest.items'
              - name: items
                dependsOn: [item-check]
                forEach: 'item-check.items'
                steps:
                  - name: make
                    type: create-agent
                    agent: builder
                  - name: build
                    type: reason
                    agent: builder
                    in: make
                    dependsOn: [make]
                    prompt: build.md
                    outputs: out.json
                  - name: rebuild
                    type: reason
                    agent: builder
                    in: make
                    prompt: rebuild.md
                    outputs: out.json
                  - name: withdraw
                    type: reason
                    agent: builder
                    in: make
                    prompt: withdraw.md
                    outputs: out.json
                  - name: probe
                    type: pause
                    question: '{piece.kind}|{piece.changed}|{piece.before}'
                    options: [go, stop]
                    dependsOn: [build]
                  - name: drop
                    type: delete-agent
                    of: make
                    dependsOn: [probe]
        """;

    private static WorkflowDefinition Parse(string text = Flow)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text) with
        {
            Amend = new FlowAmend("request", 5, null, "planning.planupdate", "check", "planning.plan.latest.scope", "rounds.items.build", "rounds.items.rebuild", "rounds.items.withdraw", null),
        };

    /// <summary>The flow of <see cref="Flow"/> that holds one agent open at a time, so the second work item waits.</summary>
    private static WorkflowDefinition OneAgentAtATime()
        => Parse() with { MaxOpenAgents = 1 };

    /// <summary>A work item of the plan.</summary>
    private static JsonObject Item(string name)
        => new() { ["name"] = name, ["title"] = $"The {name} item", ["ownedPaths"] = new JsonArray($"src/{name}/**") };

    /// <summary>One piece of the plan's scope, as the planning step concluded it.</summary>
    private static JsonObject Piece(string kind, JsonNode item, string changed)
        => new() { ["kind"] = kind, ["item"] = item, ["changed"] = changed, ["keep"] = "what stays", ["undo"] = "what is taken back", ["instead"] = "what is done instead" };

    /// <summary>What a planning step concluded: the plan's items, and the pieces of its scope.</summary>
    private static JsonNode? Concluded(params JsonNode?[] items)
        => new JsonObject { ["items"] = new JsonArray(items), ["scope"] = new JsonArray() };

    /// <summary>What a planning step concluded for an update: the plan's items and the pieces of its scope.</summary>
    private static JsonNode? Concluded(JsonArray scope, params JsonNode?[] items)
        => new JsonObject { ["items"] = new JsonArray(items), ["scope"] = scope };

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
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _slow = new(StringComparer.Ordinal);
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

        /// <summary>Answers <paramref name="step"/> by its script, every time it is asked.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
            => On(step, _ => result);

        /// <summary>Answers <paramref name="step"/> by its script, which is handed the run and the attempt's token and answers when it will.</summary>
        public FakeRunner OnAsync(string step, Func<FlowStepRun, CancellationToken, Task<FlowStepResult>> script)
        {
            _withToken[step] = script;
            return this;
        }

        private readonly Dictionary<string, Func<FlowStepRun, CancellationToken, Task<FlowStepResult>>> _withToken = new(StringComparer.Ordinal);

        /// <summary>Answers <paramref name="step"/> after <paramref name="after"/>, so other steps start beside it.</summary>
        public FakeRunner OnIn(string step, TimeSpan after, FlowStepResult result)
        {
            _slow[step] = _ => Task.Delay(after).ContinueWith(_ => result, TaskScheduler.Default);
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it, or a stop cancels it.</summary>
        public TaskCompletionSource<FlowStepResult> Hold(string step)
        {
            var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[step] = gate;
            return gate;
        }

        /// <summary>Holds the first run of <paramref name="step"/> open until the test completes it, deaf to every cancellation - a runner that outlives its stop; the runs after it answer at once.</summary>
        public TaskCompletionSource<FlowStepResult> HoldPastItsStop(string step)
        {
            var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runs = 0;
            _slow[step] = _ => Interlocked.Increment(ref runs) == 1 ? gate.Task : Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            return gate;
        }

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (_starts)
            {
                if (!_starts.TryGetValue(run.Instance, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _starts.Add(run.Instance, started);
                }

                started.TrySetResult();
            }

            Started.Add(run.Instance);

            if (_scripts.TryGetValue(run.Step.Name, out var script))
            {
                return script(run);
            }

            if (_withToken.TryGetValue(run.Step.Name, out var withToken))
            {
                return await withToken(run, cancellationToken).ConfigureAwait(false);
            }

            if (_slow.TryGetValue(run.Step.Name, out var slow))
            {
                return await slow(run).ConfigureAwait(false);
            }

            if (_gates.TryGetValue(run.Step.Name, out var gate))
            {
                return await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>("what I did, what is left, what I was in the middle of");
    }

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>The one list the check that opens a round answers: its own items and every work item plan updates owe it, none named twice.</summary>
    private static JsonNode UnionOf(FlowStepRun run)
    {
        var items = (run.Read!["items"]!.ToNode() as JsonArray)!;
        var owed = run.Read.TryGetValue("owed", out var owing) && !owing!.IsMissing ? (owing.ToNode() as JsonArray)! : null;

        if (owed is null)
        {
            return items.DeepClone();
        }

        var names = items.Select(item => (item as JsonObject)?["name"]?.GetValue<string>()).ToList();
        return new JsonArray([.. items.Select(item => item?.DeepClone()), .. owed.Where(item => (item as JsonObject)?["name"]?.GetValue<string>() is { } named && !names.Contains(named, StringComparer.Ordinal)).Select(item => item?.DeepClone())]);
    }
    /// <summary>The run's updates, read whole from the store.</summary>
    private static FlowUpdatesFile UpdatesOf(HarnessFactory harness, OrchestratorLayout layout)
        => harness.OrchestrationStore.ReadFlowUpdates(layout)!;

    /// <summary>
    /// An added piece makes one more work item of the round, of the kind <c>added</c>: the step the
    /// <c>whenAddScope</c> key names builds it, under its own name, and the two alternatives are Skipped in its
    /// instance, saying why - as they are in every instance the group's own expression made. The step of its own
    /// instance reads the piece it carries, and the update's entry says where the piece was placed and what carries it.
    /// </summary>
    [Fact]
    public async Task AnAddedPiece_MakesOneMoreWorkItemOfTheRound_AndTheOtherTwoAreSkipped()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("two"), "the parser is a second work item")),
                Item("one"),
                Item("two"))));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // To the first item's probe, where the round is under way.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);
            var firstProbe = coordinator.OpenQuestions.Single().Id;
            Assert.Equal("added||", coordinator.OpenQuestions.Single().Problem);

            // The amendment, between two sessions: the update is planned whole - the planner concludes a second work
            // item, the check it sends the run back to runs again, and once that scope rests the plan stands.
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("build the parser too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(firstProbe, "go", null, out _));

            _ = await coordinator.RunAsync(Token);

            // The new item's probe asks with the piece it carries, and answering it ends the run.
            Assert.Equal("added|the parser is a second work item|", coordinator.OpenQuestions.Single().Problem);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal(FlowUpdateStates.Done, update.State);
            var piece = update.ThePieces.Single();
            Assert.Equal(("added", "two", "rounds.items[1]", "rounds.items[1].build"), (piece.Kind, piece.ItemName(), piece.Placed, piece.CarriedBy));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);

            foreach (var item in new[] { "rounds.items[0]", "rounds.items[1]" })
            {
                Assert.Equal(nameof(FlowInstanceState.Done), steps[$"{item}.build"].State);

                foreach (var other in new[] { "rebuild", "withdraw" })
                {
                    Assert.Equal((nameof(FlowInstanceState.Skipped), $"not this work item's: it is 'added', and 'build' carries it"), (steps[$"{item}.{other}"].State, steps[$"{item}.{other}"].SkippedBecause));
                }
            }

            Assert.Equal("rounds.items.build", steps["rounds.items[1].build"].Step);
            Assert.Contains("rounds.items[1].make", steps.Keys, StringComparer.Ordinal);
            Assert.NotEqual(steps["rounds.items[0].make"].Made, steps["rounds.items[1].make"].Made);
        }
    }

    /// <summary>
    /// A changed piece against a waiting instance is given the piece's item in the place of its own, and runs as it
    /// would have: the step the <c>whenChangeScope</c> key names runs in the place of the one <c>whenAddScope</c>
    /// names - under its name, with its dependencies - and the other two are Skipped in its instance, saying why.
    /// </summary>
    [Fact]
    public async Task AChangedPieceAgainstAWaitingInstance_IsGivenTheItemInThePlaceOfItsOwn()
    {
        using var temp = new TempDirectory();
        var definition = OneAgentAtATime();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var differently = Item("two");
        differently["title"] = "The two item, done differently";

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"), Item("two"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = run.Read!["items"]!.ToNode()!.DeepClone(),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("changed", differently.DeepClone(), "the two item is done differently")),
                Item("one"),
                differently)));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // To the first item's probe: the second item waits, its agent held back by the run's one open agent.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);
            var firstProbe = coordinator.OpenQuestions.Single().Id;

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("the two item is different", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(firstProbe, "go", null, out _));
            _ = await coordinator.RunAsync(Token);

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal(FlowUpdateStates.Done, update.State);
            var piece = update.ThePieces.Single();
            Assert.Equal(("changed", "two", "rounds.items[1]", "rounds.items[1].build"), (piece.Kind, piece.ItemName(), piece.Placed, piece.CarriedBy));

            // The waiting instance was given the piece's item in the place of its own, and its build is the step the
            // whenChangeScope key names, under the add step's name; the alternative that does not carry it is Skipped
            // in its instance, saying why.
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal("rounds.items.rebuild", steps["rounds.items[1].build"].Step);
            Assert.Equal((nameof(FlowInstanceState.Skipped), "not this work item's: it is 'changed', and 'rebuild' carries it"), (steps["rounds.items[1].withdraw"].State, steps["rounds.items[1].withdraw"].SkippedBecause));
            Assert.DoesNotContain("rounds.items[1].rebuild", steps.Keys, StringComparer.Ordinal);

            // The changed item's probe reads the piece, and answering it ends the run.
            Assert.Equal("changed|the two item is done differently|", coordinator.OpenQuestions.Single().Problem);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            // Nothing of the waiting instance had started: its agent was made only at its own turn, after the first
            // item's was closed.
            Assert.NotEqual(steps["rounds.items[0].make"].Made, steps["rounds.items[1].make"].Made);
        }
    }

    /// <summary>
    /// A removed piece against a waiting instance is withdrawn before it began: the instance is Skipped with the
    /// reason, nothing of it runs, and the piece is recorded as needing no step beyond that.
    /// </summary>
    [Fact]
    public async Task ARemovedPieceAgainstAWaitingInstance_IsWithdrawnBeforeItBegan()
    {
        using var temp = new TempDirectory();
        var definition = OneAgentAtATime();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"), Item("two"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = run.Read!["items"]!.ToNode()!.DeepClone(),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("removed", Item("two"), "the second item is withdrawn")),
                Item("one"))));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);
            var firstProbe = coordinator.OpenQuestions.Single().Id;

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("leave the second item out", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(firstProbe, "go", null, out _));

            var end = await coordinator.RunAsync(Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            var piece = update.ThePieces.Single();
            Assert.Equal(("removed", "rounds.items[1]", "withdrawn before it began"), (piece.Kind, piece.Placed, piece.Ended));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal(nameof(FlowInstanceState.Skipped), steps["rounds.items[1]"].State);
            Assert.Equal("withdrawn by update u1 before it began", steps["rounds.items[1]"].SkippedBecause);
        }
    }

    /// <summary>
    /// A changed piece against an instance that has begun brings it to rest and visits it again, in its place and with
    /// its agent: nothing more is started in it and what it owed is dropped, its steps that never started are skipped
    /// with the reason, its own group instance ends Stopped, ended for the update, and the instance is visited again -
    /// a new scope of the piece's kind, in which the agent its create-agent step made, and that is live, is taken and
    /// not made again. The new visit reads the engine's account of the work it starts from.
    /// </summary>
    [Fact]
    public async Task AChangedPieceAgainstAnInstanceThatHasBegun_IsBroughtToRest_AndVisitedAgainWithItsAgent()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var again = Item("one");
        again["title"] = "The one item, done differently";

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = run.Read!["items"]!.ToNode()!.DeepClone(),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("changed", again.DeepClone(), "the one item is done differently")),
                again)));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // To the item's probe, where the instance has begun: its agent is made and its builder has run.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);
            var firstProbe = coordinator.OpenQuestions.Single().Id;

            // The amendment, between two sessions: the update is planned whole, and its changed piece finds the
            // instance begun - the instance is brought to rest, nothing more started in it, and visited again in its
            // place, with its agent, of the piece's kind.
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("the one item is different", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(firstProbe, "go", null, out _));
            _ = await coordinator.RunAsync(Token);

            // The visited-again instance's probe reads the piece and the engine's account of the work it starts from.
            var asked = coordinator.OpenQuestions.Single();
            Assert.Equal(VisitedAgainProbe, asked.Problem);

            Assert.True(coordinator.TryAnswer(asked.Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            var piece = update.ThePieces.Single();
            Assert.Equal(("changed", "rounds.items[0]", "rounds.items[1].build"), (piece.Kind, piece.Placed, piece.CarriedBy));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);

            // The old instance is over - stopped, ended for the update, its work done again in the visited-again one -
            // and the session after the visit took it for no stopped step to go on with.
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u1"), (steps["rounds.items[0]"].State, steps["rounds.items[0]"].EndedFor));
            Assert.Equal(("u1", "rounds.items[0]"), (steps["rounds.items[1]"].PieceOf, steps["rounds.items[1]"].AgainOf));

            // The agent was taken into the new visit: its make is Done at once with the same agent, and the step
            // that carries the item is the one the whenChangeScope key names, under the add step's name.
            Assert.Equal((steps["rounds.items[0].make"].Made, nameof(FlowInstanceState.Done)), (steps["rounds.items[1].make"].Made, steps["rounds.items[1].make"].State));
            Assert.Equal("rounds.items.rebuild", steps["rounds.items[1].build"].Step);
            Assert.Equal(nameof(FlowInstanceState.Done), steps["rounds.items[1].build"].State);
            Assert.Equal((nameof(FlowInstanceState.Skipped), "not this work item's: it is 'changed', and 'rebuild' carries it"), (steps["rounds.items[1].withdraw"].State, steps["rounds.items[1].withdraw"].SkippedBecause));
        }
    }

    /// <summary>
    /// A piece against a round that has not opened is owed to it: the check that opens the round is handed the owed
    /// items with its own, checks them as one list, and its output items is that list - so the group expands from what
    /// was checked and the owed item joins the round, of its kind.
    /// </summary>
    [Fact]
    public async Task APieceAgainstARoundThatHasNotOpened_IsOwedToIt_AndJoinsAtTheCheckThatOpensIt()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner()
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("two"))));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));
            var plan = runner.Hold("plan");

            // To the planning step, held open by the test: the scope an update is planned in has begun, and no round
            // has opened.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("planning.plan");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");

            // The first plan ends with two items, and the update is planned whole: its piece - a third item, which
            // the plan's own list does not read - is owed to the round that has not opened.
            plan.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"), Item("two"))));
            await session;

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal(FlowUpdateStates.Done, update.State);
            var piece = update.ThePieces.Single();
            Assert.Equal(("added", "three"), (piece.Kind, piece.ItemName()));
            Assert.NotNull(piece.OwedTo);
            Assert.Contains("when it opens", piece.OwedTo, StringComparison.Ordinal);

            // The round opens over its own item and the owed one, as one list, and both are built.
            FlowSessionEnd? end = null;

            while (true)
            {
                end = await coordinator.RunAsync(Token);

                if (end.Status == FlowRunStatus.Done)
                {
                    break;
                }

                var open = coordinator.OpenQuestions.ToList();

                if (open.Count == 0)
                {
                    break;
                }

                foreach (var asked in open)
                {
                    Assert.StartsWith("added|", asked.Problem, StringComparison.Ordinal);
                    Assert.True(coordinator.TryAnswer(asked.Id, "go", null, out _));
                }
            }

            Assert.True(FlowRunStatus.Done == end!.Status, end.Summary);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            var built = steps.Values.Where(step => step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done)).ToList();
            var names = built.Select(built => (built.Item as JsonObject)?["name"]?.GetValue<string>()).OrderBy(name => name, StringComparer.Ordinal).ToList();
            Assert.Equal(["one", "three", "two"], names);

            // The check that opened the round checked one list, and its output items is that list.
            var opened = steps.Values.Single(step => step.Step == "rounds.item-check");
            Assert.Equal(["one", "two", "three"], ((opened.Output?["items"] as JsonArray) ?? []).Select(item => (item as JsonObject)?["name"]?.GetValue<string>()).ToList());
        }
    }

    /// <summary>What the probe of the instance visited again for the changed piece of "one" asks: the piece's kind, what changed, and the engine's account of the work it starts from.</summary>
    private const string VisitedAgainProbe =
        "changed|the one item is done differently|the work item was carried by 'rounds.items[0]'; 'rounds.items[0].build' (build): done; "
        + "'rounds.items[0].drop' (drop): skipped (withdrawn before it started: ended for update u1); 'rounds.items[0].make' (make): done; "
        + "'rounds.items[0].probe' (probe): done; 'rounds.items[0].rebuild' (rebuild): skipped (not this work item's: it is 'added', and 'build' carries it); "
        + "'rounds.items[0].withdraw' (withdraw): skipped (not this work item's: it is 'added', and 'build' carries it); "
        + "the newest output of the work, of 'rounds.items[0].probe': {\"answer\":\"go\",\"text\":\"\"}; the work now is in the worktree you are in, with what was not folded";

    /// <summary>The runner of <see cref="Flow"/> whose planner concludes "one" and whose update changes it: the shape most tests of an instance brought to rest drive.</summary>
    private static FakeRunner ChangingOne()
    {
        var again = Item("one");
        again["title"] = "The one item, done differently";

        return new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = run.Read!["items"]!.ToNode()!.DeepClone(),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("changed", again.DeepClone(), "the one item is done differently")),
                again)));
    }

    /// <summary>A coordinator over the run's records, as every test of this class makes one.</summary>
    private static FlowCoordinator CoordinatorOf(WorkflowDefinition definition, IFlowStepRunner runner, IFlowRecords records, HarnessFactory harness, OrchestratorLayout layout, FlowResumeState? resume = null)
        => new(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, resume: resume, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

    /// <summary>The run as its records stand on disk, for a coordinator that takes it up.</summary>
    private static FlowResumeState ResumeOf(HarnessFactory harness, OrchestratorLayout layout, WorkflowDefinition definition)
        => new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));

    /// <summary>Waits, within the test's bound, until the run's records on disk hold what <paramref name="holds"/> looks for.</summary>
    private static async Task<FlowFile> UntilAsync(HarnessFactory harness, OrchestratorLayout layout, Func<FlowFile, bool> holds)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bound.CancelAfter(Bound);

        while (true)
        {
            if (harness.OrchestrationStore.ReadFlow(layout) is { } flow && holds(flow))
            {
                return flow;
            }

            await Task.Delay(10, bound.Token);
        }
    }

    /// <summary>
    /// An instance ended for an update stays ended: its record says so - stopped, ended for the update - and no session
    /// after takes it for a stopped step to go on with, so the step the update ended in order never runs in it again,
    /// and nothing of it closes the agent the instance visited again in its place took over.
    /// </summary>
    [Fact]
    public async Task AnInstanceEndedForAnUpdate_StaysEnded_AtEverySessionAfter()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = ChangingOne();
        var build = runner.Hold("build");

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, records, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            // The amendment reaches the session while the item's build is in flight: the build is ended in order, and
            // the instance is visited again, whose probe the session ends paused on.
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].build");
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("the one item is different").WaitAsync(Bound, Token));
            _ = await session.WaitAsync(Bound, Token);

            var asked = coordinator.OpenQuestions.Single();
            Assert.Equal("rounds.items[1].probe", asked.Instance);

            // Anything that ran the ended build again would find it answering at once now.
            build.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            Assert.True(coordinator.TryAnswer(asked.Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal(["rounds.items[0].build"], runner.Started.Where(started => started.StartsWith("rounds.items[0].", StringComparison.Ordinal) && started != "rounds.items[0].make"));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal(
                [
                    ("rounds.items[0]", nameof(FlowInstanceState.Stopped), "u1"),
                    ("rounds.items[0].build", nameof(FlowInstanceState.Stopped), "u1"),
                    ("rounds.items[0].probe", nameof(FlowInstanceState.Skipped), null),
                    ("rounds.items[0].drop", nameof(FlowInstanceState.Skipped), null),
                ],
                new[] { "rounds.items[0]", "rounds.items[0].build", "rounds.items[0].probe", "rounds.items[0].drop" }.Select(id => (id, steps[id].State, steps[id].EndedFor)));
            Assert.Equal(nameof(FlowInstanceState.Done), steps["rounds.items[1].drop"].State);

            // A visit the update ended waits on nothing - not the resume, for none runs it again - and the run's status
            // counts it among what ended, never among what is left.
            Assert.Equal([("rounds.items[0]", 0), ("rounds.items[0].build", 0)], new[] { "rounds.items[0]", "rounds.items[0].build" }.Select(id => (id, steps[id].WaitsOn?.Count ?? 0)));
            var facts = FlowStatus.Facts(definition, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), UpdatesOf(harness, layout), new FlowEngineState(FlowEngineState.None, null, []), DateTimeOffset.UtcNow);
            Assert.Equal(StaysEndedLeft, facts.First(fact => fact.StartsWith("left", StringComparison.Ordinal)));
            Assert.Equal(StaysEndedDone, facts.First(fact => fact.StartsWith("done", StringComparison.Ordinal)));
        }
    }

    /// <summary>What the status of the run <see cref="AnInstanceEndedForAnUpdate_StaysEnded_AtEverySessionAfter"/> drives says is left: nothing, the visits the update ended among what ended.</summary>
    private const string StaysEndedLeft = "left      at least 0 step instances, at most 0 step instances (nothing more can come)";

    /// <summary>What the status of that run says is done: every instance, the two the update ended among them.</summary>
    private const string StaysEndedDone = "done      22 step instances ended; 0.00 USD, 0 tokens spent";

    /// <summary>The flow of <see cref="Flow"/> whose work item is linted beside its build, by a machine step that runs to its own end.</summary>
    private static string WithLint()
        => Flow.Replace(
            "          - name: probe\n",
            "          - name: lint\n            type: orient\n            dependsOn: [make]\n          - name: probe\n",
            StringComparison.Ordinal);

    /// <summary>The flow of <see cref="WithLint"/> whose build waits for the lint, so a lint whose retry waits holds the item's work.</summary>
    private static string LintedFirst()
        => WithLint().Replace(
            "            dependsOn: [make]\n            prompt: build.md\n",
            "            dependsOn: [make, lint]\n            prompt: build.md\n",
            StringComparison.Ordinal);

    /// <summary>
    /// A step of an instance a plan update brings to rest that waits on its retry when the piece is placed is never given
    /// that retry: it ends stopped for the update as the piece is placed, and the instance comes to rest and is visited
    /// again in its place.
    /// </summary>
    [Fact]
    public async Task AStepWaitingOnItsRetry_InAnInstanceBroughtToRest_IsStoppedForTheUpdate()
    {
        using var temp = new TempDirectory();
        var definition = Parse(LintedFirst());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var again = Item("one");
        again["title"] = "The one item, done differently";

        // The first item's lint meets inputs that moved, which the retry table runs again after a wait - on a clock that
        // never moves, so its retry waits for as long as the test runs; the planning of the update is held until then.
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = run.Read!["items"]!.ToNode()!.DeepClone(),
            }))
            .On("lint", run => run.Instance == "rounds.items[0].lint"
                ? FlowStepResult.Ended(passed: false, DssHarness.Core.Execution.LegExit.InputsMoved, error: "the inputs moved")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var planned = runner.Hold("planupdate");

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].lint").WaitAsync(Bound, Token);

            // The lint's ending reaches the loop before the amendment, which is handed over behind it.
            await Task.Delay(200, Token);
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("the one item is different").WaitAsync(Bound, Token));
            await runner.StartedAsync("planning.planupdate").WaitAsync(Bound, Token);
            planned.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("changed", again.DeepClone(), "the one item is done differently")),
                again)));

            _ = await session.WaitAsync(Bound, Token);

            Assert.Equal("rounds.items[1].probe", coordinator.OpenQuestions.Single().Instance);
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u1", 1), (steps["rounds.items[0].lint"].State, steps["rounds.items[0].lint"].EndedFor, steps["rounds.items[0].lint"].Attempt));
            Assert.Single(runner.Started, started => started == "rounds.items[0].lint");
        }
    }

    /// <summary>
    /// A step of an instance a plan update is bringing to rest is never given a new attempt: a machine step there that
    /// fails with a retry left ends stopped for the update rather than waiting on its retry, and the instance comes to
    /// rest and is visited again in its place.
    /// </summary>
    [Fact]
    public async Task AStepThatFailsInAnInstanceBroughtToRest_IsNeverRetried_AndTheInstanceComesToRest()
    {
        using var temp = new TempDirectory();
        var definition = Parse(WithLint());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = ChangingOne();
        var lint = runner.HoldPastItsStop("lint");

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, records, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            // The amendment reaches the session while the item's lint is in flight: the item is brought to rest, and
            // the lint runs to its own end there - which is a failure the retry table would run again.
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].lint").WaitAsync(Bound, Token);
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("the one item is different").WaitAsync(Bound, Token));
            _ = await UntilAsync(harness, layout, flow => flow.Steps.Any(step => step.Instance == "rounds.items[0].probe" && step.State == nameof(FlowInstanceState.Skipped)));

            lint.TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, error: "the linter was cut off"));
            _ = await session.WaitAsync(Bound, Token);

            // The lint ended stopped for the update at its first attempt, and the instance visited again asks.
            Assert.Equal("rounds.items[1].probe", coordinator.OpenQuestions.Single().Instance);
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u1", 1), (steps["rounds.items[0].lint"].State, steps["rounds.items[0].lint"].EndedFor, steps["rounds.items[0].lint"].Attempt));
            Assert.Single(runner.Started, started => started == "rounds.items[0].lint");

            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
        }
    }

    /// <summary>
    /// A step a stop stopped in an instance a plan update is bringing to rest is not run again at the resume: it ends
    /// stopped for the update, the instance comes to rest, and the session visits it again in its place.
    /// </summary>
    [Fact]
    public async Task AStepAStopStoppedInAnInstanceBroughtToRest_IsNotRunAgainAtTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse(WithLint());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = ChangingOne();
        _ = runner.Hold("lint");

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, records, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            // The item is being brought to rest, its lint in flight, when the run is stopped.
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].lint").WaitAsync(Bound, Token);
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("the one item is different").WaitAsync(Bound, Token));
            _ = await UntilAsync(harness, layout, flow => flow.Steps.Any(step => step.Instance == "rounds.items[0].probe" && step.State == nameof(FlowInstanceState.Skipped)));

            coordinator.RequestStop();
            var stopped = await session.WaitAsync(Bound, Token);
            Assert.True(FlowRunStatus.Stopped == stopped.Status, stopped.Summary);        }

        var (_, _, again) = Records("r1", temp.Path, definition);
        var resumedRunner = ChangingOne();

        using (again)
        {
            var resumed = CoordinatorOf(definition, resumedRunner, again, harness, layout, ResumeOf(harness, layout, definition));
            _ = await resumed.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal("rounds.items[1].probe", resumed.OpenQuestions.Single().Instance);
            Assert.DoesNotContain("rounds.items[0].lint", resumedRunner.Started);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u1"), (steps["rounds.items[0].lint"].State, steps["rounds.items[0].lint"].EndedFor));
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u1"), (steps["rounds.items[0]"].State, steps["rounds.items[0]"].EndedFor));
        }
    }

    /// <summary>
    /// A crash after the instance visited again in the place of one brought to rest is made, before its entry names it,
    /// loses nothing: the instance brought to rest is still being brought to rest at the resume, the visit made again is
    /// found by what it visits again - never made twice - and the piece's entry names it.
    /// </summary>
    [Fact]
    public async Task ACrashOnceTheVisitMadeAgainIsMade_BeforeTheOldInstanceEnds_LosesNothing()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = ChangingOne();
        _ = runner.Hold("build");
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.ThePieces.Any(piece => piece.Again is not null)), lands: false);

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, crashing, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].build");
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("the one item is different").WaitAsync(Bound, Token));
            _ = await session.WaitAsync(Bound, Token);
            Assert.True(crashing.Ended);
        }

        // On disk: the old instance still being brought to rest, and its piece's entry naming no visit made again.
        Assert.Null(UpdatesOf(harness, layout).Updates.Single().ThePieces.Single().Again);
        var before = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds.items[0]");
        Assert.Equal((nameof(FlowInstanceState.Running), "u1"), (before.State, before.EndedFor));

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var resumed = CoordinatorOf(definition, ChangingOne(), again, harness, layout, ResumeOf(harness, layout, definition));
            _ = await resumed.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal("rounds.items[1].probe", resumed.OpenQuestions.Single().Instance);
            Assert.True(resumed.TryAnswer(resumed.OpenQuestions.Single().Id, "go", null, out _));
            var end = await resumed.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var piece = UpdatesOf(harness, layout).Updates.Single().ThePieces.Single();
            Assert.Equal(("rounds.items[0]", "rounds.items[1]", "rounds.items[1].build", "done"), (piece.Placed, piece.Again, piece.CarriedBy, piece.Ended));
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u1"), steps.Where(step => step.Instance == "rounds.items[0]").Select(step => (step.State, step.EndedFor)).Single());
            Assert.DoesNotContain(steps, step => step.Instance == "rounds.items[2]");
        }
    }

    /// <summary>
    /// Placing the pieces of a second update is made once from its entry, however often it is called, against an
    /// instance an earlier update visited again too: a crash after the second update brought that instance to rest, and
    /// before it was done, leaves the instance its own - the resume neither withdraws it as never begun nor makes another
    /// work item for the piece - and it comes to rest and is visited again for the second update's piece.
    /// </summary>
    [Fact]
    public async Task ASecondUpdateAgainstAnInstanceVisitedAgain_IsPlacedOnce_AcrossACrash()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.Id == "u2" && update.State == FlowUpdateStates.Done), lands: false);

        using (records)
        {
            var coordinator = CoordinatorOf(definition, ChangingThenRemovingOne(), crashing, harness, layout);

            // To the first item's probe; the first update changes it, and it is visited again.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("the one item is different", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);
            Assert.Equal("rounds.items[1].probe", coordinator.OpenQuestions.Single().Instance);

            // The second update takes the item back while the visit made again is begun, and the process ends before
            // the update is done.
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("leave the one item out", "amend-orchestrator-request", out problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(crashing.Ended);
        }

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var resumed = CoordinatorOf(definition, ChangingThenRemovingOne(), again, harness, layout, ResumeOf(harness, layout, definition));
            _ = await resumed.RunAsync(Token).WaitAsync(Bound, Token);

            // The instance the second update brought to rest is visited again for its piece, which takes the work back.
            var asked = resumed.OpenQuestions.Single();
            Assert.Equal(("rounds.items[2].probe", "removed|the one item is taken back"), (asked.Instance, string.Join('|', asked.Problem.Split('|')[..2])));
            Assert.True(resumed.TryAnswer(asked.Id, "go", null, out _));
            var end = await resumed.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var piece = UpdatesOf(harness, layout).Updates.Single(update => update.Id == "u2").ThePieces.Single();
            Assert.Equal(("removed", "rounds.items[1]", "rounds.items[2]", "rounds.items[2].build", "done"), (piece.Kind, piece.Placed, piece.Again, piece.CarriedBy, piece.Ended));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u2"), (steps["rounds.items[1]"].State, steps["rounds.items[1]"].EndedFor));
            Assert.Equal("rounds.items.withdraw", steps["rounds.items[2].build"].Step);
            Assert.DoesNotContain("rounds.items[3]", steps.Keys, StringComparer.Ordinal);
        }
    }

    /// <summary>The runner of <see cref="Flow"/> whose planner concludes "one", whose first update changes it and whose second takes it back.</summary>
    private static FakeRunner ChangingThenRemovingOne()
    {
        var again = Item("one");
        again["title"] = "The one item, done differently";
        var updates = 0;

        return ChangingOne()
            .On("planupdate", _ => Interlocked.Increment(ref updates) == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(new JsonArray(Piece("changed", again.DeepClone(), "the one item is done differently")), again.DeepClone()))
                : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(new JsonArray(Piece("removed", again.DeepClone(), "the one item is taken back")))));
    }

    /// <summary>
    /// A crash while an instance is being brought to rest for an update - the update done, its piece placed, the step
    /// in flight not yet ended - loses nothing: the session that takes the run up reads the ending from the instance's
    /// own record, never runs the old step again, and visits the instance again in its place, of the piece's kind.
    /// </summary>
    [Fact]
    public async Task ACrashWhileAnInstanceIsBroughtToRest_IsGoneOnFromByTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = ChangingOne();
        _ = runner.Hold("build");
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.State == FlowUpdateStates.Done));

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, crashing, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].build");
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("the one item is different").WaitAsync(Bound, Token));
            _ = await session.WaitAsync(Bound, Token);
            Assert.True(crashing.Ended);
        }

        // On disk: the update done, its piece placed in the instance that was being brought to rest, nothing visited
        // again yet.
        var placed = UpdatesOf(harness, layout).Updates.Single().ThePieces.Single();
        Assert.Equal(("rounds.items[0]", null, null), (placed.Placed, placed.Again, placed.CarriedBy));

        var (_, _, again) = Records("r1", temp.Path, definition);
        var resumedRunner = ChangingOne();

        using (again)
        {
            var resumed = CoordinatorOf(definition, resumedRunner, again, harness, layout, ResumeOf(harness, layout, definition));
            _ = await resumed.RunAsync(Token).WaitAsync(Bound, Token);

            var asked = resumed.OpenQuestions.Single();
            Assert.Equal("rounds.items[1].probe", asked.Instance);
            Assert.Equal(["changed", "the one item is done differently"], asked.Problem.Split('|')[..2]);
            Assert.DoesNotContain("rounds.items[0].build", resumedRunner.Started);

            Assert.True(resumed.TryAnswer(asked.Id, "go", null, out _));
            var end = await resumed.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var piece = UpdatesOf(harness, layout).Updates.Single().ThePieces.Single();
            Assert.Equal(("rounds.items[0]", "rounds.items[1]", "rounds.items[1].build", "done"), (piece.Placed, piece.Again, piece.CarriedBy, piece.Ended));
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u1"), (steps["rounds.items[0]"].State, steps["rounds.items[0]"].EndedFor));
            Assert.Equal("rounds.items.rebuild", steps["rounds.items[1].build"].Step);
        }
    }

    /// <summary>
    /// A crash once the instance visited again has drawn the step that carries the piece loses nothing of the visit:
    /// the session that takes the run up reads the piece, its kind and the instance it replaces from the visit's own
    /// record, so its steps read the piece as they did before the crash.
    /// </summary>
    [Fact]
    public async Task ACrashOnceTheCarrierIsDrawn_KeepsWhatTheVisitedAgainInstanceReads()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = ChangingOne();
        _ = runner.Hold("build");
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.ThePieces.Any(piece => piece.CarriedBy is not null)));
        string before;

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, crashing, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].build");
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("the one item is different").WaitAsync(Bound, Token));
            _ = await session.WaitAsync(Bound, Token);
            Assert.True(crashing.Ended);

            // What the visit's probe asked in the process that crashed - after the crash, so never written.
            before = coordinator.OpenQuestions.Single().Problem;
            Assert.Equal(["changed", "the one item is done differently"], before.Split('|')[..2]);
        }

        var (_, _, again) = Records("r1", temp.Path, definition);
        var resumedRunner = ChangingOne();

        using (again)
        {
            var resumed = CoordinatorOf(definition, resumedRunner, again, harness, layout, ResumeOf(harness, layout, definition));
            _ = await resumed.RunAsync(Token).WaitAsync(Bound, Token);

            // The resumed visit asks word for word as the crashed one did: the piece, its kind and the account of the
            // instance it replaces all read back from the visit's own record.
            var asked = resumed.OpenQuestions.Single();
            Assert.Equal(("rounds.items[1].probe", before), (asked.Instance, asked.Problem));

            Assert.True(resumed.TryAnswer(asked.Id, "go", null, out _));
            var end = await resumed.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal("done", UpdatesOf(harness, layout).Updates.Single().ThePieces.Single().Ended);
        }
    }

    /// <summary>
    /// A crash after the pieces of an update were placed and before its entry was written done places nothing twice:
    /// the session that takes the run up finds the instance the run's records name as the piece's and leaves it as it
    /// was placed - an added item stays added, carried by the step its kind names, and no second one is made.
    /// </summary>
    [Fact]
    public async Task ACrashBetweenPlacingThePiecesAndTheUpdatesEnd_PlacesNothingTwice()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("two"), "the parser is a second work item")),
                Item("one"),
                Item("two"))));
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.State == FlowUpdateStates.Done), lands: false);

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, crashing, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("build the parser too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);
            Assert.True(crashing.Ended);
        }

        // On disk: the new item made for the piece, and the update still under way.
        Assert.Equal(FlowUpdateStates.UnderWay, UpdatesOf(harness, layout).Updates.Single().State);
        Assert.Contains("rounds.items[1]", harness.OrchestrationStore.ReadFlow(layout)!.Steps.Select(step => step.Instance));

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var resumed = CoordinatorOf(definition, runner, again, harness, layout, ResumeOf(harness, layout, definition));
            _ = await resumed.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(("rounds.items[1].probe", "added|the parser is a second work item|"), (resumed.OpenQuestions.Single().Instance, resumed.OpenQuestions.Single().Problem));
            Assert.True(resumed.TryAnswer(resumed.OpenQuestions.Single().Id, "go", null, out _));
            var end = await resumed.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            var piece = update.ThePieces.Single();
            Assert.Equal((FlowUpdateStates.Done, "added", "rounds.items[1]", "rounds.items[1].build", "done"), (update.State, piece.Kind, piece.Placed, piece.CarriedBy, piece.Ended));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal("rounds.items.build", steps["rounds.items[1].build"].Step);
            Assert.DoesNotContain("rounds.items[2]", steps.Keys, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// A step ended in order for an update whose runner outlives its stop keeps what it claimed until it answers: the
    /// instance visited again in its place, which writes the same agent's tree, waits on that claim - its record saying
    /// so - and starts only once the old runner has answered, so two agents never write one worktree at once.
    /// </summary>
    [Fact]
    public async Task ARunnerThatOutlivesItsStop_KeepsItsClaimUntilItAnswers()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Flow
            .Replace("prompt: build.md\n", "access: write\n            prompt: build.md\n", StringComparison.Ordinal)
            .Replace("prompt: rebuild.md\n", "access: write\n            prompt: rebuild.md\n", StringComparison.Ordinal));
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = ChangingOne();
        var deaf = runner.HoldPastItsStop("build");

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, records, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].build");
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("the one item is different").WaitAsync(Bound, Token));

            // The old build is ended for the update, its runner still running: the visit made again waits on the claim
            // it still holds.
            var waiting = await UntilAsync(harness, layout, flow => flow.Steps.Any(step => step.Instance == "rounds.items[1].build" && step.WaitsOn.Contains(FlowWaits.Claim)));
            Assert.Equal((nameof(FlowInstanceState.Stopped), "u1"), waiting.Steps.Where(step => step.Instance == "rounds.items[0].build").Select(step => (step.State, step.EndedFor)).Single());
            Assert.DoesNotContain("rounds.items[1].build", runner.Started);

            // The old runner answers, and gives its claim back: the visit made again starts.
            deaf.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            _ = await session.WaitAsync(Bound, Token);

            Assert.Equal("rounds.items[1].probe", coordinator.OpenQuestions.Single().Instance);
            Assert.Contains("rounds.items[1].build", runner.Started);
        }
    }

    /// <summary>
    /// Every reason step in flight in the instances an update brings to rest is asked for its handoff at once, as a safe
    /// pause asks its own: none goes on working on the plan the update replaced while another's turn is answered. Each
    /// agent, asked, finds the other asked too - with a clock that never ends a turn on its own - and both write their
    /// own handoffs.
    /// </summary>
    [Fact]
    public async Task TheStepsAnUpdateEndsInOrder_AreAskedForTheirHandoffsAtOnce()
    {
        using var temp = new TempDirectory();

        // Room for both builds and the update's planning at once.
        var definition = Parse() with { MaxReasoningAgents = 3 };
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var one = Item("one");
        one["title"] = "The one item, done differently";
        var two = Item("two");
        two["title"] = "The two item, done differently";
        var inFlight = new System.Collections.Concurrent.ConcurrentDictionary<string, FlowStepRun>(StringComparer.Ordinal);
        var sawTheOtherAsked = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        const string First = "rounds.items[0].build";
        const string Second = "rounds.items[1].build";

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"), Item("two"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = run.Read!["items"]!.ToNode()!.DeepClone(),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("changed", one.DeepClone(), "the one item is done differently"), Piece("changed", two.DeepClone(), "the two item is done differently")),
                one.DeepClone(),
                two.DeepClone())))
            .OnAsync("build", async (run, token) =>
            {
                if (run.Instance is not (First or Second))
                {
                    return FlowStepResult.Ended(passed: true, HarnessExit.Success);
                }

                inFlight[run.Instance] = run;
                _ = await run.Telling!.Handoff.WhenAsked.WaitAsync(Bound, Token);

                // Asked: the other is looked for asked too, for a few seconds - one asked after this turn ended never is.
                var other = run.Instance == First ? Second : First;
                var asked = false;

                for (var tries = 0; tries < 300 && !asked; tries++)
                {
                    asked = inFlight.TryGetValue(other, out var theOther) && theOther.Telling?.Asked is not null;

                    if (!asked)
                    {
                        await Task.Delay(10, Token);
                    }
                }

                sawTheOtherAsked[run.Instance] = asked;
                return FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, "handed off");
            });

        using (records)
        {
            // A clock that ends no handoff turn by its bound: each turn ends as its agent answers.
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new TimerClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync(First).WaitAsync(Bound, Token);
            await runner.StartedAsync(Second).WaitAsync(Bound, Token);
            Assert.Equal(FlowHandedOver.Taken, await coordinator.OfferAmendmentAsync("both items are different").WaitAsync(Bound, Token));
            _ = await session.WaitAsync(Bound, Token);

            Assert.Equal([(First, true), (Second, true)], sawTheOtherAsked.OrderBy(seen => seen.Key, StringComparer.Ordinal).Select(seen => (seen.Key, seen.Value)));
            Assert.Equal(
                [(First, "agent"), (Second, "agent")],
                new[] { First, Second }.Select(instance => (instance, coordinator.Instances[instance].Handoff?.By)));
        }
    }

    /// <summary>
    /// The check of a later update is handed the round as it lives: one item a name, never the instance an earlier
    /// update ended beside the one visited again in its place - so a second update after a visit made again is checked
    /// against the round's own items, and nothing is said to be named twice.
    /// </summary>
    [Fact]
    public async Task TheCheckOfALaterUpdate_IsHandedTheRoundsLiveItems()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var differently = Item("one");
        differently["title"] = "The one item, done differently";
        var plannings = 0;
        var rounds = new List<IReadOnlyList<string?>?>();

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = run.Read!["items"]!.ToNode()!.DeepClone(),
            }))
            .On("check", run =>
            {
                rounds.Add(run.Read!.TryGetValue("round", out var round) && !round.IsMissing ? [.. (round.ToNode() as JsonArray)!.Select(FlowWorkItems.NameOf)] : null);
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["ok"] = true, ["problems"] = new JsonArray(), ["items"] = run.Read!["items"]!.ToNode()!.DeepClone() });
            })
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Interlocked.Increment(ref plannings) == 1
                ? Concluded(new JsonArray(Piece("changed", differently.DeepClone(), "the one item is done differently")), differently.DeepClone())
                : Concluded(new JsonArray(Piece("added", Item("two"), "the parser is a second work item")), differently.DeepClone(), Item("two"))));

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, records, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);

            // The first update changes the item under way: it is ended and visited again.
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("the one item is different", "amend-orchestrator-request", out var first), first ?? "taken");
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);
            Assert.Equal("rounds.items[1].probe", coordinator.OpenQuestions.Single().Instance);

            // The second adds an item, the visit made again still waiting on its probe: its check is handed the round
            // with the one item visited again, once.
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("build the parser too", "amend-orchestrator-request", out var second), second ?? "taken");
            _ = await coordinator.RunAsync(Token);

            Assert.Equal(["one"], rounds[^1]!);

            // Both items end, and the run with them: each update done, its piece said done where it was placed.
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single(asked => asked.Instance == "rounds.items[2].probe").Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single(asked => asked.Instance == "rounds.items[1].probe").Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal(
                [("u1", FlowUpdateStates.Done, "rounds.items[0]", "done"), ("u2", FlowUpdateStates.Done, "rounds.items[2]", "done")],
                UpdatesOf(harness, layout).Updates.Select(update => (update.Id, update.State, update.ThePieces.Single().Placed, update.ThePieces.Single().Ended)));
        }
    }

    /// <summary>
    /// Every update keeps its place in the order taken, however late its entry is written again: the first update's
    /// piece is said done after the update queued behind it was taken and done, and <c>updates.json</c> still lists
    /// the first before the second.
    /// </summary>
    [Fact]
    public async Task EachUpdate_KeepsItsPlaceInTheOrderTaken_HoweverLateItsEntryIsWrittenAgain()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var plannings = 0;
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Interlocked.Increment(ref plannings) == 1
                ? Concluded(new JsonArray(Piece("added", Item("two"), "the parser is a second work item")), Item("one"), Item("two"))
                : Concluded(Item("one"), Item("two"))));

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, records, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);

            // Two amendments: the first adds an item, the second - queued behind it - changes no work item.
            Assert.Equal(FlowHandedOver.Taken, coordinator.AmendmentHandedOver("build the parser too", "amend-orchestrator-request", out _));
            Assert.Equal(FlowHandedOver.Taken, coordinator.AmendmentHandedOver("and keep the names short", "amend-orchestrator-request", out _));
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.Equal([FlowUpdateStates.Done, FlowUpdateStates.Done], UpdatesOf(harness, layout).Updates.Select(update => update.State));

            // The added item ends after both updates are done: the first update's entry is written last.
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single(asked => asked.Instance == "rounds.items[1].probe").Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal(
                [("u1", "the parser is a second work item", "done"), ("u2", null, null)],
                UpdatesOf(harness, layout).Updates.Select(update => (update.Id, update.ThePieces.SingleOrDefault()?.Changed, update.ThePieces.SingleOrDefault()?.Ended)));
        }
    }

    /// <summary>
    /// A piece whose work item's name is of the wrong type is no piece the engine takes: the update is given up - failed,
    /// its why saying which piece and what is wrong with it - and the run goes on as it was, never ended by the engine
    /// failing to read it.
    /// </summary>
    [Fact]
    public async Task APieceWhoseNameIsNoText_GivesTheUpdateUpWithWhy()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = run.Read!["items"]!.ToNode()!.DeepClone(),
            }))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", new JsonObject { ["name"] = 5 }, "a work item named by a number")),
                Item("one"))));

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, records, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("number the work", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal(
                (FlowUpdateStates.Failed, "its scope 'planning.plan.latest.scope' holds piece 1, which holds a work item with no name, and a work item is known by its name: the request is recorded and no plan holds it"),
                (update.State, update.Why));
            Assert.Empty(update.ThePieces);
        }
    }

    /// <summary>
    /// Every piece's end is said whatever ended its work - an instance that failed as well as one done - and a removed
    /// piece no instance carried says that no instance carried it and nothing was built, never a blank place it was
    /// owed to: the update's entry and the run's report alike.
    /// </summary>
    [Fact]
    public async Task EveryPiecesEnd_IsSaid_WhateverEndedItsWork()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("build", run => run.Instance == "rounds.items[1].build"
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error: "the parser did not build")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("two"), "the parser is a second work item"), Piece("removed", Item("gone"), "the old registry is not built")),
                Item("one"),
                Item("two"))));

        using (records)
        {
            var coordinator = CoordinatorOf(definition, runner, records, harness, layout);

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("build the parser, drop the registry", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.True(FlowRunStatus.Failed == end.Status, end.Summary);

            var pieces = UpdatesOf(harness, layout).Updates.Single().ThePieces;
            Assert.Equal(
                [
                    ("added", "two", "rounds.items[1]", "failed (20: the parser did not build)"),
                    ("removed", "gone", null, "nothing was built and nothing is undone"),
                ],
                pieces.Select(piece => (piece.Kind, piece.ItemName()!, piece.Placed, piece.Ended)));

            _ = FlowReports.Write(harness.FileSystem, layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), end.Summary, 0, DateTimeOffset.UtcNow, UpdatesOf(harness, layout));
            Assert.Equal(
                [
                    "  - added 'two': carried out by 'rounds.items[1].build', failed (20: the parser did not build)",
                    "  - removed 'gone': carried by no instance, nothing was built and nothing is undone",
                ],
                File.ReadAllLines(layout.ReportFile).Where(line => line.StartsWith("  - added", StringComparison.Ordinal) || line.StartsWith("  - removed", StringComparison.Ordinal)));
        }
    }
}