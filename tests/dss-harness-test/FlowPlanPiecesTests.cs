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
            Assert.StartsWith("changed|the one item is done differently|", asked.Problem, StringComparison.Ordinal);
            Assert.Contains("the work item was carried by 'rounds.items[0]'", asked.Problem, StringComparison.Ordinal);
            Assert.Contains("'rounds.items[0].make' (make): done", asked.Problem, StringComparison.Ordinal);
            Assert.EndsWith("the work now is in the worktree you are in, with what was not folded", asked.Problem, StringComparison.Ordinal);

            Assert.True(coordinator.TryAnswer(asked.Id, "go", null, out _));
            var end = await coordinator.RunAsync(Token);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            var piece = update.ThePieces.Single();
            Assert.Equal(("changed", "rounds.items[0]", "rounds.items[1].build"), (piece.Kind, piece.Placed, piece.CarriedBy));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);

            // The old instance is over - ended for the update, its work done again in the visited-again one - and
            // nothing of it runs any more.
            Assert.Contains(steps["rounds.items[0]"].State, new[] { nameof(FlowInstanceState.Stopped), nameof(FlowInstanceState.Done) });

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
}