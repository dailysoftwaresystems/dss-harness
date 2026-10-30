using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The telling, the loops and what the run says of a plan update: every told piece's message sent - to a reason step
/// in flight in one message into the conversation it is in, to one that waits on a question of its own with the
/// answer, to one that starts later in work a piece names in its prompt's own part - who was not concerned and what
/// was not delivered recorded; work owed to a loop carried to its next iteration whatever its until says, an
/// iteration added at the loop's bound for the update; and the report and list-orchestrator saying every piece, where
/// it was carried out and how that ended, and the iteration added. All over fake step runners and the run's real
/// records in a directory of their own.
/// </summary>
public sealed class FlowPlanToldTests
{
    /// <summary>Every bound a test of a session waits for: a session that hangs fails the test rather than the run's minutes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The flow these tests tell of: a planning block whose <c>planupdate</c> step is raised by an update alone, beside
    /// <c>plan</c>, the <c>check</c> its <c>then</c> names, and two watchers that start once <c>plan</c> has ended; and
    /// a round of work items: <c>item-check</c> opens it, <c>items</c> expands from what it checked, and each item is
    /// built by one of the three alternatives the <c>amend</c> block names, asked at a <c>probe</c> after the build,
    /// and closed by a <c>drop</c> when it is over.
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
              - name: watch
                type: reason
                agent: watcher
                ask: true
                prompt: watch.md
                outputs: out.json
                dependsOn: [plan]
              - name: beside
                type: reason
                agent: watcher
                prompt: watch.md
                outputs: out.json
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
                    question: '{piece.kind}|{piece.changed}'
                    options: [go, stop]
                    dependsOn: [build]
                  - name: drop
                    type: delete-agent
                    of: make
                    dependsOn: [probe]
        """;

    /// <summary>The flow of <see cref="Flow"/> whose round of work items sits in a repeat at its bound while its until reads false.</summary>
    private static string Looped()
        => Flow.Replace(
            "  - name: rounds\n    dependsOn: [planning]\n    steps:",
            "  - name: rounds\n    dependsOn: [planning]\n    repeat:\n      until: 'false'\n      maxIterations: 1\n    steps:",
            StringComparison.Ordinal);

    /// <summary>
    /// The flow of <see cref="Flow"/> whose second watcher starts only once the first has ended, so it reads what a
    /// plan update told the planning scope in the part of its own prompt.
    /// </summary>
    private static string Later()
        => Flow.Replace(
            "        outputs: out.json\n        dependsOn: [plan]\n  - name: rounds",
            "        outputs: out.json\n        dependsOn: [watch]\n  - name: rounds",
            StringComparison.Ordinal);

    /// <summary>
    /// The flow of <see cref="Flow"/> whose work items sit inside the planning block itself, each asking at its probe
    /// before the three alternatives, so one of them can be under way when an update places its piece against it.
    /// </summary>
    private const string Inside = """
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
              - name: items
                dependsOn: [check]
                forEach: 'check.items'
                steps:
                  - name: make
                    type: create-agent
                    agent: builder
                  - name: probe
                    type: pause
                    question: '{piece.kind}|{piece.changed}'
                    options: [go, stop]
                    dependsOn: [make]
                  - name: build
                    type: reason
                    agent: builder
                    in: make
                    dependsOn: [probe]
                    prompt: build.md
                    outputs: out.json
                  - name: rebuild
                    type: reason
                    agent: builder
                    in: make
                    dependsOn: [probe]
                    prompt: rebuild.md
                    outputs: out.json
                  - name: withdraw
                    type: reason
                    agent: builder
                    in: make
                    dependsOn: [probe]
                    prompt: withdraw.md
                    outputs: out.json
                  - name: drop
                    type: delete-agent
                    of: make
                    dependsOn: [build]
        """;

    private static WorkflowDefinition Parse(string text = Flow, string add = "rounds.items.build", string change = "rounds.items.rebuild", string remove = "rounds.items.withdraw", string? then = "check")
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text) with
        {
            Amend = new FlowAmend("request", 5, null, "planning.planupdate", then, "planning.plan.latest.scope", add, change, remove, null),
        };

    /// <summary>A work item of the plan.</summary>
    private static JsonObject Item(string name)
        => new() { ["name"] = name, ["title"] = $"The {name} item", ["ownedPaths"] = new JsonArray($"src/{name}/**") };

    /// <summary>One piece of the plan's scope that tells work under way, as the planning step concluded it.</summary>
    private static JsonObject Told(string forId, string changed)
        => new() { ["kind"] = "told", ["for"] = forId, ["changed"] = changed, ["keep"] = "what stays", ["undo"] = "what is taken back", ["instead"] = "what is done instead" };

    /// <summary>One piece of the plan's scope that carries a work item out, as the planning step concluded it.</summary>
    private static JsonObject Piece(string kind, JsonNode item, string changed)
        => new() { ["kind"] = kind, ["item"] = item, ["changed"] = changed, ["keep"] = "what stays", ["undo"] = "what is taken back", ["instead"] = "what is done instead" };

    /// <summary>What a planning step concluded: the plan's items, and the pieces of its scope.</summary>
    private static JsonNode? Concluded(JsonArray scope, params JsonNode?[] items)
        => new JsonObject { ["items"] = new JsonArray(items), ["scope"] = scope };

    /// <summary>What a planning step concluded with no piece of its scope.</summary>
    private static JsonNode? Concluded(params JsonNode?[] items)
        => Concluded([], items);

    /// <summary>The run's records in a directory of their own.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FlowRunRecords Records) Records(string run, string directory, WorkflowDefinition definition)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        return (harness, layout, new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of(run, definition)));
    }

    /// <summary>The run's updates, read whole from the store.</summary>
    private static FlowUpdatesFile UpdatesOf(HarnessFactory harness, OrchestratorLayout layout)
        => harness.OrchestrationStore.ReadFlowUpdates(layout)!;

    /// <summary>Waits until the run's first update stands in <paramref name="state"/>, bounded as a session is.</summary>
    private static async Task<FlowUpdateEntry> UntilAsync(HarnessFactory harness, OrchestratorLayout layout, string state)
    {
        for (var at = 0; ; at++)
        {
            var update = UpdatesOf(harness, layout).Updates.FirstOrDefault();

            if (update is { State: var held } && held == state)
            {
                return update;
            }

            if (at > 3000)
            {
                throw new TimeoutException($"the update never became {state}");
            }

            await Task.Delay(10, Token);
        }
    }

    /// <summary>Drives the run on, answering every question it reaches but the one of its kind <paramref name="held"/>, until it is done or only that one is left.</summary>
    private static async Task<FlowSessionEnd> DriveAsync(FlowCoordinator coordinator, string? held = null)
    {
        while (true)
        {
            var end = await coordinator.RunAsync(Token);

            if (end.Status == FlowRunStatus.Done)
            {
                return end;
            }

            var open = coordinator.OpenQuestions.Where(asked => asked.Kind != held).ToList();

            if (open.Count == 0)
            {
                return end;
            }

            foreach (var asked in open)
            {
                Assert.True(coordinator.TryAnswer(asked.Id, asked.Kind == "loopBound" ? "accept" : "go", null, out _), asked.Problem);
            }
        }
    }

    /// <summary>
    /// A runner that answers each step as its test scripted it, holds one open where the test asks, tells the test each
    /// start, and keeps what the engine handed it to tell each attempt's agent with.
    /// </summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<FlowStepRun, CancellationToken, Task<FlowStepResult>>> _slow = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>What the engine handed each instance it asked, to tell its attempt's agent with; null where none was.</summary>
        public Dictionary<string, FlowTelling?> Tellings { get; } = new(StringComparer.Ordinal);

        /// <summary>What a plan update that told the work of each instance asked said, for the part of its own its prompt holds; null where none did.</summary>
        public Dictionary<string, string?> PlanTolds { get; } = new(StringComparer.Ordinal);

        /// <summary>Every run of a step that answers by an async script, with the path its script took.</summary>
        public List<(string Instance, string Path)> AsyncRuns { get; } = [];

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

        /// <summary>Answers <paramref name="step"/> by an async script that sees the cancellation a stop raises.</summary>
        public FakeRunner OnAsync(string step, Func<FlowStepRun, CancellationToken, Task<FlowStepResult>> script)
        {
            _slow[step] = script;
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
            Tellings[run.Instance] = run.Telling;
            PlanTolds[run.Instance] = run.PlanTold;

            if (_scripts.TryGetValue(run.Step.Name, out var script))
            {
                return script(run);
            }

            if (_slow.TryGetValue(run.Step.Name, out var slow))
            {
                try
                {
                    return await slow(run, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"the async script of '{run.Instance}' threw: {ex.GetType().Name}: {ex.Message}", ex);
                }
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

    /// <summary>
    /// A told piece is sent to the reason step it names where that step is in flight: one message, into the
    /// conversation the attempt is in, with what changed the plan quoted and the piece's lines as the plan gave them.
    /// A reason step in flight that no piece names is sent nothing and listed as not concerned.
    /// </summary>
    [Fact]
    public async Task AToldPiece_IsSentToAReasonStepInFlight_InOneMessageIntoItsConversation()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var plan = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var beside = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeRunner()
            .OnAsync("plan", (run, cancellationToken) => plan.Task.WaitAsync(cancellationToken))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Told("planning.watch", "the watcher reads this")),
                Item("one"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .OnAsync("watch", (run, cancellationToken) => watch.Task.WaitAsync(cancellationToken))
            .OnAsync("beside", (run, cancellationToken) => beside.Task.WaitAsync(cancellationToken));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            // The session starts the plan, held open by the test, and the amendment is handed to it while it runs: the
            // update is under way, its planning step raised ahead of everything else, the run held.
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("planning.plan");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("tell the watcher", "amend-orchestrator-request", out var problem), problem ?? "taken");

            // The plan ends, and what its release wakes draws the watchers - beside the raised planning step, inside
            // the scope the update is planned in - so both are in flight when the pieces are placed.
            plan.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))));
            await runner.StartedAsync("planning.watch");
            await runner.StartedAsync("planning.beside");

            var update = await UntilAsync(harness, layout, FlowUpdateStates.Done);

            // The message is in the conversation the attempt is in: what changed the plan quoted, the piece's lines,
            // and the one sentence that its work goes on from here with this.
            var telling = runner.Tellings["planning.watch"]!;
            Assert.True(telling.Messages.TryTake(out var message), "the message was not posted");
            Assert.StartsWith("The plan this work follows was updated while you worked.", message, StringComparison.Ordinal);
            Assert.Contains("What changed it: the request was amended on ", message, StringComparison.Ordinal);
            Assert.Contains(": \"tell the watcher\"", message, StringComparison.Ordinal);
            Assert.Contains("For your work (planning.watch):", message, StringComparison.Ordinal);
            Assert.Contains("- What changed: the watcher reads this", message, StringComparison.Ordinal);
            Assert.Contains("- Keep: what stays", message, StringComparison.Ordinal);
            Assert.EndsWith("Go on from here, with this.", message, StringComparison.Ordinal);

            // The watcher beside it was sent nothing, and the update's record says so.
            Assert.False(runner.Tellings["planning.beside"]!.Messages.TryTake(out _));
            Assert.Equal(["planning.beside"], update.NotConcerned);
            Assert.Equal([("planning.watch", "the change of 'planning.watch'", "by a message into its conversation")],
                update.Told!.Select(one => (one.Who, one.What, one.How)));
            Assert.Empty(update.NotDelivered ?? []);

            // The step's own record says what it was told, and by what way.
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal([("u1", "a message")], steps["planning.watch"].Told!.Select(one => (one.Update, one.How)));
            Assert.Null(steps["planning.beside"].Told);

            // The watchers end, and the run goes on to its round and done.
            watch.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            beside.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            await session.WaitAsync(Bound, Token);

            var driven = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == driven.Status, $"{driven.Status}: {driven.Summary}");
        }
    }

    /// <summary>
    /// A told piece to a reason step that waits on a question of its own is given with the answer: the attempt the
    /// answer begins carries the same text, as the part of its own its first message holds, and the update's record
    /// and the step's own say it that way.
    /// </summary>
    [Fact]
    public async Task AToldPieceToAStepWaitingOnAQuestion_IsGivenWithTheAnswer()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Told("planning.watch", "the watcher reads this too")),
                Item("one"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("watch", run => run.Answers.Count == 0
                ? FlowStepResult.Asking(new FlowQuestion("q0", "ask", "planning.watch", "the watcher asked", ["go", "stop"]))
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // To the watcher's own question, which the run pauses on: the planning scope has begun, and the amendment
            // is taken while the watcher waits on it.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var paused = await DriveAsync(coordinator, held: "ask");
            var waiting = coordinator.OpenQuestions.Single(asked => asked.Kind == "ask");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("tell the waiting watcher", "amend-orchestrator-request", out var problem), problem ?? "taken");

            // The next session plans the update whole - the watcher still waiting on its question, nothing else to
            // hold - and once it is done the answer begins the attempt that carries the telling.
            _ = await DriveAsync(coordinator, held: "ask");
            var update = await UntilAsync(harness, layout, FlowUpdateStates.Done);
            Assert.Equal([("planning.watch", "the change of 'planning.watch'", "with the answer")],
                update.Told!.Select(one => (one.Who, one.What, one.How)));
            Assert.Empty(update.NotDelivered ?? []);

            // The answer begins the attempt that carries the same text in the part of its own, and the step's own
            // record says it that way.
            Assert.True(coordinator.TryAnswer(waiting.Id, "go", null, out _));
            var driven = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == driven.Status, $"{driven.Status}: {driven.Summary}");

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal([("u1", "the answer")], steps["planning.watch"].Told!.Select(one => (one.Update, one.How)));
            Assert.StartsWith("The plan this work follows was updated before this work began.", runner.PlanTolds["planning.watch"], StringComparison.Ordinal);
            Assert.Contains("For your work (planning.watch):", runner.PlanTolds["planning.watch"], StringComparison.Ordinal);
            Assert.Contains("- What changed: the watcher reads this too", runner.PlanTolds["planning.watch"], StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A step that starts later in a scope a told piece names reads the same text in a part of its own prompt; a step
    /// of an instance a piece placed is told by its own prompt's <c>{piece.*}</c> and has no such part.
    /// </summary>
    [Fact]
    public async Task AStepThatStartsLaterInAScopeAToldPieceNames_ReadsItInItsOwnPromptPart()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Later());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var plan = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeRunner()
            .OnAsync("plan", (run, cancellationToken) => plan.Task.WaitAsync(cancellationToken))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Told("planning", "whoever works in the planning block reads this"), Piece("added", Item("two"), "the parser is a second work item")),
                Item("one"),
                Item("two"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .OnAsync("watch", (run, cancellationToken) => watch.Task.WaitAsync(cancellationToken));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            // The amendment is handed to the session that holds the plan, and its release wakes the run: the watcher
            // starts beside the raised planning step, in the scope the update's piece names.
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("planning.plan");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("tell the planning scope", "amend-orchestrator-request", out var problem), problem ?? "taken");

            plan.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))));
            await runner.StartedAsync("planning.watch");

            var update = await UntilAsync(harness, layout, FlowUpdateStates.Done);
            Assert.Equal([("planning.watch", "the change of 'planning'", "by a message into its conversation")],
                update.Told!.Select(one => (one.Who, one.What, one.How)));

            // The watcher ends and the one that starts after it, in the named scope, reads the telling in the part of
            // its own prompt; the run goes on over both items to done, and the carrier of the added piece - told by
            // its own prompt's {piece.*} - reads no such part.
            watch.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            await session.WaitAsync(Bound, Token);

            Assert.True(FlowRunStatus.Done == (await DriveAsync(coordinator)).Status);
            Assert.StartsWith("The plan this work follows was updated before this work began.", runner.PlanTolds["planning.beside"], StringComparison.Ordinal);
            Assert.Contains("For your work (planning):", runner.PlanTolds["planning.beside"], StringComparison.Ordinal);
            Assert.Contains("whoever works in the planning block reads this", runner.PlanTolds["planning.beside"], StringComparison.Ordinal);
            Assert.Null(runner.PlanTolds["rounds.items[1].build"]);
        }
    }

    /// <summary>
    /// A told piece that names nothing under way when the pieces are placed is not delivered, and recorded with why:
    /// the work it named had ended meanwhile, or it is no id of the run.
    /// </summary>
    [Fact]
    public async Task AToldPieceNamingNothingUnderWay_IsNotDelivered_AndRecordedWithWhy()
    {
        using var temp = new TempDirectory();
        var definition = Parse();
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var plan = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var beside = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeRunner()
            .OnAsync("plan", (run, cancellationToken) => plan.Task.WaitAsync(cancellationToken))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Told("planning.plan", "too late for the plan"), Told("rounds.items[2].build", "no such work")),
                Item("one"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .OnAsync("watch", (run, cancellationToken) => watch.Task.WaitAsync(cancellationToken))
            .OnAsync("beside", (run, cancellationToken) => beside.Task.WaitAsync(cancellationToken));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            // The amendment is handed to the session that holds the plan, and its release wakes the run: the watchers
            // are in flight when the pieces are placed, and neither is named by one.
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("planning.plan");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("tell what is not there", "amend-orchestrator-request", out var problem), problem ?? "taken");

            plan.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))));
            await runner.StartedAsync("planning.watch");
            await runner.StartedAsync("planning.beside");

            var update = await UntilAsync(harness, layout, FlowUpdateStates.Done);

            // One piece named the plan's own ended work and one an id no step of the run carries: neither was
            // delivered, each said with why, and the watchers in flight were sent nothing.
            Assert.Equal(
                [
                    ("planning.plan", "'planning.plan' had ended meanwhile: the step that goes on with its work reads it in its prompt"),
                    ("rounds.items[2].build", "'rounds.items[2].build' is no id of the run: a told piece names work under way, as the planning step's prompt listed it"),
                ],
                update.NotDelivered!.Select(one => (one.For, one.Why)));
            Assert.Empty(update.Told ?? []);
            Assert.Equal(["planning.beside", "planning.watch"], update.NotConcerned!.Order(StringComparer.Ordinal));

            watch.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            beside.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            await session.WaitAsync(Bound, Token);

            Assert.True(FlowRunStatus.Done == (await DriveAsync(coordinator)).Status);
        }
    }

    /// <summary>
    /// Work a plan update owes to a loop at its bound is carried to its next iteration whatever its until says: one
    /// iteration is added for the update, published and kept in the update's entry, the work joins that iteration at
    /// the check that opens the round - over the round's own items and the owed one as one list - and the report and
    /// list-orchestrator say every piece, where it was carried out and how that ended, and the iteration added.
    /// </summary>
    [Fact]
    public async Task WorkOwedToALoopAtItsBound_IsCarriedToTheNextIteration_AnIterationAdded()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Looped());
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("three"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The first round runs and ends, the loop's until reads false at its bound of one, and the run pauses on
            // the question its bound asks: the round has ended, and the amendment is taken there.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            await DriveAsync(coordinator, held: "loopBound");
            Assert.NotNull(coordinator.OpenQuestions.SingleOrDefault(asked => asked.Kind == "loopBound"));

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");

            // The next session plans the update whole, still paused on the question the loop's bound asked, and once
            // it is done the loop runs the iteration the work is owed to.
            _ = await DriveAsync(coordinator, held: "loopBound");

            // The update is planned whole, and its piece - a third item no round of the run carries - is owed to the
            // loop's next iteration.
            var owed = await UntilAsync(harness, layout, FlowUpdateStates.Done);
            Assert.Equal("the next iteration of 'rounds'", owed.ThePieces.Single().OwedTo);

            // The loop runs again whatever its until says: one iteration is added for the update - of the one
            // amend.maxAddedIterations allows - the owed item joins the second round at the check that opens it, and
            // the run goes on to done.
            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal("rounds#2", update.AddedIteration);

            var events = FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events;
            Assert.Contains(events, written => written.Kind == "iterationAdded" && written.Detail!.Contains("iteration 2 of 'rounds' was added for update u1", StringComparison.Ordinal));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            var built = steps.Values.Where(step => step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done)).ToList();
            var names = built.Select(built => (built.Item as JsonObject)?["name"]?.GetValue<string>()).OrderBy(name => name, StringComparer.Ordinal).ToList();
            Assert.Equal(["one", "one", "three"], names);

            // The report says every piece, where it was carried out and how that ended, and the iteration added; and
            // so does the listing.
            _ = FlowReports.Write(harness.FileSystem, layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), end.Summary, 0, DateTimeOffset.UtcNow, harness.OrchestrationStore.ReadFlowUpdates(layout));
            var report = File.ReadAllText(layout.ReportFile);

            Assert.Contains("- added 'three': carried out by 'rounds#2.items[1].build', done", report, StringComparison.Ordinal);
            Assert.Contains("- round 2 of 'rounds' was added for update u1", report, StringComparison.Ordinal);

            var listed = OrchestrationReports.List(
                [new OrchestratorInventory(
                    layout,
                    new OrchestratorRecord { Kind = OrchestratorRecord.KindName, Name = "r1", Model = "some-model", CreatedAt = DateTimeOffset.UtcNow, Parallel = 4 },
                    [],
                    [],
                    [],
                    new FlowRunInventory(harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), new FlowEngineState(FlowEngineState.None, null, []), harness.OrchestrationStore.ReadFlowUpdates(layout)))],
                steps: false,
                questions: false,
                json: false);

            // The listing says the iteration added, and the piece - ended - is not among what it holds open.
            Assert.Contains(listed.Details ?? [], line => line.Contains("round 2 of 'rounds' was added for update u1", StringComparison.Ordinal));
            Assert.DoesNotContain(listed.Details ?? [], line => line.Contains("piece added 'three'", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A reason step that a piece of an update ends in order is told its piece first, as the lead of the handoff
    /// question it is asked: what changed the plan, the piece's lines, and the sentence that its step ends here and
    /// another goes on from where it leaves the work.
    /// </summary>
    [Fact]
    public async Task AReasonStepEndedInOrder_IsToldItsPieceFirst_AsTheLeadOfItsHandoffQuestion()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Inside, add: "planning.items.build", change: "planning.items.rebuild", remove: "planning.items.withdraw", then: null);
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var again = Item("one");
        again["title"] = "The one item, done differently";
        var runs = 0;

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("changed", again.DeepClone(), "the one item is done differently")),
                again)))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("item-check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }));

        // The carrier of a piece runs under the add step's own declaration, whatever kind its instance is of, so the
        // async script is keyed by the member name: its first run stands in flight until the update ends it in order,
        // and the visit made again runs to its end.
        runner.OnAsync("build", async (run, cancellationToken) =>
        {
            // The first visit's carrier stands in flight until the update ends it in order; the visit that is made
            // again runs to its end.
            if (Interlocked.Increment(ref runs) == 1)
            {
                runner.AsyncRuns.Add((run.Instance, "held"));
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                runner.AsyncRuns.Add((run.Instance, "released"));
            }
            else
            {
                runner.AsyncRuns.Add((run.Instance, "again"));
            }

            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // To the item's probe, which asks before the three alternatives: answering it lets the carrier of the
            // added kind start, and the amendment is taken while the run pauses on the probe.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            await DriveAsync(coordinator, held: "pause");
            var probe = coordinator.OpenQuestions.Single(asked => asked.Kind == "pause");
            Assert.StartsWith("added|", probe.Problem, StringComparison.Ordinal);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("the one item is different", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(probe.Id, "go", null, out _));

            // The session draws the raised planning step and the carrier together: the carrier is in flight when the
            // changed piece is placed, and it is told its piece first - as the lead of the handoff question - then
            // ended in order and visited again.
            _ = await coordinator.RunAsync(Token);

            var asked = runner.Tellings.GetValueOrDefault("planning.items[0].build")?.Asked;
            Assert.True(asked is not null, $"the carrier was never asked for its handoff; started: {string.Join(", ", runner.Started)}; async: {string.Join(", ", runner.AsyncRuns.Select(one => $"{one.Instance}:{one.Path}"))}; states: {string.Join(", ", harness.OrchestrationStore.ReadFlow(layout)!.Steps.Select(step => $"{step.Instance}={step.State}"))}; events: {string.Join(" | ", FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Select(written => $"{written.Kind}:{written.Instance} {written.From}->{written.To}{(written.Detail is { } detail ? $" ({detail})" : string.Empty)}"))}");
            Assert.StartsWith("ended for update u1", asked.Why, StringComparison.Ordinal);
            Assert.StartsWith("The plan this work follows was updated while you worked, and the update changes your work item.", asked.Lead, StringComparison.Ordinal);
            Assert.Contains("What changed it: the request was amended on ", asked.Lead, StringComparison.Ordinal);
            Assert.Contains("For your work item (one):", asked.Lead, StringComparison.Ordinal);
            Assert.Contains("- What changed: the one item is done differently", asked.Lead, StringComparison.Ordinal);
            Assert.EndsWith("Your step ends here. Do none of this yourself: a step of its own does it, from where you leave the work.", asked.Lead, StringComparison.Ordinal);

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            var piece = update.ThePieces.Single();
            Assert.Equal(("changed", "planning.items[0]", "planning.items[1].build"), (piece.Kind, piece.Placed, piece.CarriedBy));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal([("u1", "a message")], steps["planning.items[0].build"].Told!.Select(one => (one.Update, one.How)));
        }
    }
}
