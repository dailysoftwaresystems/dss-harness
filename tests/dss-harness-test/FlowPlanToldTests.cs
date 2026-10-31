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
/// iteration added at the loop's bound for the update, and work owed to a work-item group with no loop around it
/// carried by a round of the group added for it, or the update given up where the run has none left to add; and the
/// report and list-orchestrator saying every piece, where it was carried out and how that ended, and the iteration or
/// round added. All over fake step runners and the run's real records in a directory of their own.
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
    /// The flow of <see cref="Flow"/> whose round of work items sits in a repeat of at most three iterations that ends
    /// at its first - its until reads true - and an audit after it, where a plan update finds the loop ended done.
    /// </summary>
    private static string EndedLoop()
        => Flow.Replace(
            "  - name: rounds\n    dependsOn: [planning]\n    steps:",
            "  - name: rounds\n    dependsOn: [planning]\n    repeat:\n      until: 'true'\n      maxIterations: 3\n    steps:",
            StringComparison.Ordinal)
            + "\n  - name: audit\n    type: pause\n    question: anything else?\n    options: [stay, go]\n    dependsOn: [rounds]";

    /// <summary>
    /// The flow of <see cref="Flow"/> whose round of work items has no loop around it, and an audit after it, where a
    /// plan update finds the round ended.
    /// </summary>
    private static string Ended()
        => Flow + "\n  - name: audit\n    type: pause\n    question: anything else?\n    options: [stay, go]\n    dependsOn: [rounds]";

    /// <summary>
    /// The flow of <see cref="Ended"/> whose work-item group passes a failure over, so a round whose work item failed
    /// ends failed and the run goes on.
    /// </summary>
    private static string PassedOver()
        => Ended().Replace(
            "      - name: items\n        dependsOn: [item-check]\n",
            "      - name: items\n        dependsOn: [item-check]\n        onFailure: continue\n",
            StringComparison.Ordinal);

    /// <summary>
    /// The flow of <see cref="Flow"/> whose round of work items sits in a repeat that runs three iterations, each
    /// ending at a review after its work items, where a plan update finds the round of the iteration ended and the loop
    /// still running, below its bound.
    /// </summary>
    private static string Reviewed()
        => Flow.Replace(
            "  - name: rounds\n    dependsOn: [planning]\n    steps:",
            "  - name: rounds\n    dependsOn: [planning]\n    repeat:\n      until: 'false'\n      maxIterations: 3\n    steps:",
            StringComparison.Ordinal)
            + "\n      - name: review\n        type: pause\n        question: reviewed?\n        options: [stay, go]\n        dependsOn: [items]";

    /// <summary>
    /// A flow whose planner is declared in the same repeat iteration as the work-item group it plans: each iteration
    /// plans, checks and expands its round, so a plan update is planned in the iteration whose round it changes.
    /// </summary>
    private const string Planned = """
        name: flow
        inputs:
          - name: request
        steps:
          - name: ask
            type: pause
            question: '{input.request}'
            options: [stay, go]
          - name: rounds
            dependsOn: [ask]
            repeat:
              until: 'true'
              maxIterations: 2
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
                items: 'rounds.plan.latest.items'
                dependsOn: [plan]
              - name: items
                dependsOn: [check]
                forEach: 'check.items'
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
        for (var sessions = 0; ; sessions++)
        {
            // A run that asks without end fails the test, saying what it kept asking, rather than holding it for ever.
            Assert.True(sessions < 50, $"the run kept asking: {string.Join("; ", coordinator.OpenQuestions.Select(asked => $"{asked.Id} ({asked.Kind}) {asked.Problem}"))}");

            var end = await Task.Run(() => coordinator.RunAsync(Token), Token).WaitAsync(Bound, Token);

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

        /// <summary>Answers <paramref name="step"/> by its script, every time it is asked, in the place of any script it had.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            _ = _slow.Remove(step);
            return this;
        }

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
            => On(step, _ => result);

        /// <summary>Answers <paramref name="step"/> by an async script that sees the cancellation a stop raises, in the place of any script it had.</summary>
        public FakeRunner OnAsync(string step, Func<FlowStepRun, CancellationToken, Task<FlowStepResult>> script)
        {
            _slow[step] = script;
            _ = _scripts.Remove(step);
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
            Assert.Equal(
                """
                The plan this work follows was updated while you worked.
                What changed it: the request was amended on 2026-09-30 16:32:14: "tell the watcher"
                For your work (planning.watch):
                - What changed: the watcher reads this
                - Keep: what stays
                - Undo: what is taken back
                - Do instead: what is done instead
                Go on from here, with this.
                """,
                message);

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
            Assert.Equal(
                """
                The plan this work follows was updated before this work began.
                What changed it: the request was amended on 2026-09-30 16:32:14: "tell the waiting watcher"
                For your work (planning.watch):
                - What changed: the watcher reads this too
                - Keep: what stays
                - Undo: what is taken back
                - Do instead: what is done instead
                """,
                runner.PlanTolds["planning.watch"]);
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
            Assert.Equal(
                """
                The plan this work follows was updated before this work began.
                What changed it: the request was amended on 2026-09-30 16:32:14: "tell the planning scope"
                For your work (planning):
                - What changed: whoever works in the planning block reads this
                - Keep: what stays
                - Undo: what is taken back
                - Do instead: what is done instead
                """,
                runner.PlanTolds["planning.beside"]);
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
            Assert.Equal(
                ["iteration 2 of 'rounds' was added for update u1: the run has been given 1 of the 1 amend.maxAddedIterations allows"],
                events.Where(written => written.Kind == "iterationAdded").Select(written => written.Detail));

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
                new JsonArray(Piece("changed", again.DeepClone(), "the one item is done differently"), Told("planning.plan", "too late for the plan")),
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
            Assert.Equal("added|", probe.Problem);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("the one item is different", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(probe.Id, "go", null, out _));

            // The session draws the raised planning step and the carrier together: the carrier is in flight when the
            // changed piece is placed, and it is told its piece first - as the lead of the handoff question - then
            // ended in order and visited again.
            _ = await coordinator.RunAsync(Token);

            var asked = runner.Tellings.GetValueOrDefault("planning.items[0].build")?.Asked;
            Assert.True(asked is not null, $"the carrier was never asked for its handoff; started: {string.Join(", ", runner.Started)}; async: {string.Join(", ", runner.AsyncRuns.Select(one => $"{one.Instance}:{one.Path}"))}; states: {string.Join(", ", harness.OrchestrationStore.ReadFlow(layout)!.Steps.Select(step => $"{step.Instance}={step.State}"))}; events: {string.Join(" | ", FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Select(written => $"{written.Kind}:{written.Instance} {written.From}->{written.To}{(written.Detail is { } detail ? $" ({detail})" : string.Empty)}"))}");
            Assert.Equal("ended for update u1: its work item 'one' is done again, and another goes on from where it leaves the work", asked.Why);
            Assert.Equal(
                """
                The plan this work follows was updated while you worked, and the update changes your work item.
                What changed it: the request was amended on 2026-09-30 16:32:14: "the one item is different"
                For your work item (one):
                - What changed: the one item is done differently
                - Keep: what stays
                - Undo: what is taken back
                - Do instead: what is done instead
                Your step ends here. Do none of this yourself: a step of its own does it, from where you leave the work.
                """,
                asked.Lead);

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            var piece = update.ThePieces.First();
            Assert.Equal(("changed", "planning.items[0]", "planning.items[1].build"), (piece.Kind, piece.Placed, piece.CarriedBy));

            // The carrier this update ended in order was told by its own handoff question, and is no step the update
            // did not concern; the told piece for the plan, which had ended, was not delivered.
            Assert.Empty(update.NotConcerned ?? []);
            Assert.Equal(["planning.plan"], (update.NotDelivered ?? []).Select(one => one.For));

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal([("u1", "a message")], steps["planning.items[0].build"].Told!.Select(one => (one.Update, one.How)));
        }
    }

    /// <summary>
    /// Work a plan update owes a loop that has ended done - its until read true - is carried all the same: the loop
    /// runs its next iteration, below its bound, with no iteration added, and a changed piece whose item had ended in
    /// the iteration before - owed though the instance that ended bears its name - joins the round there, carried by the
    /// step its kind names, with an added piece beside it where the update has one; the run ends done once they are.
    /// </summary>
    /// <param name="alsoAdded">Whether the update adds a work item beside the one it changes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkOwedToALoopThatEndedDone_IsCarriedToItsNextIteration(bool alsoAdded)
    {
        using var temp = new TempDirectory();
        var definition = Parse(EndedLoop());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var differently = Item("one");
        differently["title"] = "The one item, done differently";
        var scope = new JsonArray(Piece("changed", differently.DeepClone(), "the one item is done differently"));

        if (alsoAdded)
        {
            scope.Add(Piece("added", Item("three"), "the registry is a third work item"));
        }

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: alsoAdded
                ? Concluded((JsonArray)scope.DeepClone(), differently.DeepClone(), Item("three"))
                : Concluded((JsonArray)scope.DeepClone(), differently.DeepClone())))
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

            // The round runs and the loop ends done at its first iteration; the audit after it asks, and the amendment is
            // taken there.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");
            Assert.Equal(nameof(FlowInstanceState.Done), harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds").State);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("the one item differently, and the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            List<(string, string, string?, string?, string?)> placed = [("changed", "one", "the next iteration of 'rounds'", "rounds#2.items[0]", "rounds#2.items[0].build")];

            if (alsoAdded)
            {
                placed.Add(("added", "three", "the next iteration of 'rounds'", "rounds#2.items[1]", "rounds#2.items[1].build"));
            }

            Assert.Equal(placed, update.ThePieces.Select(piece => (piece.Kind, piece.ItemName()!, piece.OwedTo, piece.Placed, piece.CarriedBy)));
            Assert.Null(update.AddedIteration);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal(("rounds.items.rebuild", nameof(FlowInstanceState.Done)), (steps["rounds#2.items[0].build"].Step, steps["rounds#2.items[0].build"].State));
            Assert.Equal(
                alsoAdded ? ("rounds.items.build", nameof(FlowInstanceState.Done)) : default((string, string)?),
                steps.TryGetValue("rounds#2.items[1].build", out var added) ? (added.Step, added.State) : default((string, string)?));
            Assert.DoesNotContain("rounds#3", steps.Keys, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Work owed to a loop below its bound is carried by the iteration the loop runs anyway: no iteration is added for
    /// the update, none is said to be, and nothing of what <c>amend.maxAddedIterations</c> allows is used up.
    /// </summary>
    [Fact]
    public async Task WorkOwedToALoopBelowItsBound_AddsNoIteration()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Reviewed());
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

            // The first iteration's round ends, and its review asks: the amendment is taken there, with the loop at the
            // first of its three iterations.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var review = await UntilAskedAsync(coordinator, "reviewed?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(review.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            var piece = update.ThePieces.Single();
            Assert.Equal(("the next iteration of 'rounds'", "rounds#2.items[1]"), (piece.OwedTo, piece.Placed));
            Assert.Null(update.AddedIteration);
            Assert.DoesNotContain(FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events, written => written.Kind == "iterationAdded");

            var iterations = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Where(step => step.Step == "rounds.item-check").Select(step => step.Instance);
            Assert.Equal(["rounds#1.item-check", "rounds#2.item-check", "rounds#3.item-check"], iterations);
        }
    }

    /// <summary>
    /// Work a plan update owes a work-item group whose round has ended, with no loop around it, is carried by the group
    /// run once more, as an added iteration: one round is added for the update, of the one
    /// <c>amend.maxAddedIterations</c> allows, published and kept in the update's entry; the check that opens the round
    /// runs again, handed the owed work with the round's own items as one list; the round carries the owed work alone,
    /// never the items the round before built; and the report and list-orchestrator say the round added.
    /// </summary>
    [Fact]
    public async Task WorkOwedToAGroupThatHasEnded_WithNoLoopAroundIt_IsCarriedByARoundAdded()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();
        var runner = AddingThree(checks);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The round runs and ends, and the audit after it asks: the amendment is taken there, the round ended and no
            // loop around it.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");
            Assert.Equal(nameof(FlowInstanceState.Done), harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds.items").State);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            // One round is added for the update and kept in its entry, and the piece is placed in it, carried and done.
            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal("rounds.items+2", update.AddedIteration);
            Assert.Equal(
                [("added", "three", "a round added to 'items'", "rounds.items+2[0]", "rounds.items+2[0].build", "done")],
                update.ThePieces.Select(piece => (piece.Kind, piece.ItemName()!, piece.OwedTo, piece.Placed, piece.CarriedBy, piece.Ended)));

            var events = FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events;
            Assert.Equal(
                ["round 2 of 'rounds.items' was added for update u1: the run has been given 1 of the 1 amend.maxAddedIterations allows"],
                events.Where(written => written.Kind == "iterationAdded").Select(written => written.Detail));

            // The check that opens the round ran again, handed the owed work - the union check - and the round built
            // the owed item alone; the items the round before built were not built again.
            Assert.Equal([("rounds.item-check", string.Empty), ("rounds.item-check+2", "three")], checks);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal(
                [("rounds.items+2[0].build", "three"), ("rounds.items[0].build", "one"), ("rounds.items[1].build", "two")],
                steps.Values
                    .Where(step => step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done))
                    .Select(step => (step.Instance, (step.Item as JsonObject)?["name"]?.GetValue<string>()))
                    .OrderBy(built => built.Instance, StringComparer.Ordinal));
            Assert.Equal(
                (nameof(FlowInstanceState.Done), nameof(FlowInstanceState.Done), nameof(FlowInstanceState.Done)),
                (steps["rounds.items"].State, steps["rounds.items+2"].State, steps["rounds"].State));

            // The report and the listing say the round added.
            _ = FlowReports.Write(harness.FileSystem, layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), end.Summary, 0, DateTimeOffset.UtcNow, harness.OrchestrationStore.ReadFlowUpdates(layout));
            var report = File.ReadAllText(layout.ReportFile);

            Assert.Contains("- added 'three': carried out by 'rounds.items+2[0].build', done", report, StringComparison.Ordinal);
            Assert.Contains("- round 2 of 'rounds.items' was added for update u1\n", report.ReplaceLineEndings("\n"), StringComparison.Ordinal);

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

            Assert.Contains("    round 2 of 'rounds.items' was added for update u1", listed.Details ?? []);
        }
    }

    /// <summary>
    /// A round of a work-item group with no loop around it that failed under an update - its failure passed over, so the
    /// run goes on - has ended as a round that ended done has: the work the update owes it is carried by a round added
    /// where <c>amend.maxAddedIterations</c> allows one, and where the run has none left to add the update is given up
    /// with why, every piece it decided saying it was never carried out, and nothing of it placed.
    /// </summary>
    /// <param name="allowed">The rounds <c>amend.maxAddedIterations</c> lets updates add.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WorkOwedToAGroupWhoseRoundFailedUnderTheUpdate_IsCarriedByARoundAdded_OrGivenUpWhereNoneIsLeft(int allowed)
    {
        using var temp = new TempDirectory();
        var parsed = Parse(PassedOver());
        var definition = parsed with { Amend = parsed.Amend! with { MaxAddedIterations = allowed } };
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var built = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var planned = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = new List<(string Instance, string Owed)>();

        // The round's own item fails at every attempt, the first held until the update is under way; the item the
        // update adds is built.
        var runner = AddingThree(checks, plan: Concluded(Item("one")))
            .OnAsync("planupdate", (run, cancellationToken) => planned.Task.WaitAsync(cancellationToken))
            .OnAsync("build", async (run, cancellationToken) => run.Instance.StartsWith("rounds.items[", StringComparison.Ordinal)
                ? await built.Task.WaitAsync(cancellationToken).ConfigureAwait(false)
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The amendment is taken while the round's item is under way; the item's failure wakes the run, which
            // raises the planning step, and the step stands in flight while the round ends failed, its failure passed
            // over.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds.items[0].build").WaitAsync(Bound, Token);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            built.TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed));

            await runner.StartedAsync("planning.planupdate").WaitAsync(Bound, Token);
            await RoundEndedAsync(harness, layout, nameof(FlowInstanceState.Failed));

            // The plan stands once the round has ended.
            planned.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("three"))));
            _ = await session.WaitAsync(Bound, Token);

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            var events = FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Where(written => written.Kind == "iterationAdded").Select(written => written.Detail);

            if (allowed == 0)
            {
                // Nothing would carry the work out: the update is given up with why, its piece never carried out, and
                // no round added.
                Assert.Equal(
                    (FlowUpdateStates.Failed, "its work is owed to 'rounds.items', whose round has ended with no loop around it, and the run has been given the 0 round(s) amend.maxAddedIterations allows: nothing would carry it out, the request is recorded and no plan holds it", (string?)null),
                    (update.State, update.Why, update.AddedIteration));
                Assert.Equal(
                    [("added", "three", (string?)null, (string?)null, "never carried out: its update was given up")],
                    update.ThePieces.Select(piece => (piece.Kind, piece.ItemName()!, piece.OwedTo, piece.Placed, piece.Ended)));
                Assert.Empty(events);
                Assert.Equal([("rounds.item-check", string.Empty)], checks);
                Assert.DoesNotContain("rounds.items+2", steps.Keys, StringComparer.Ordinal);
            }
            else
            {
                // The failed round has ended as a round done has: the group runs once more for the work.
                Assert.Equal((FlowUpdateStates.Done, "rounds.items+2"), (update.State, update.AddedIteration));
                Assert.Equal(
                    [("added", "three", "a round added to 'items'", "rounds.items+2[0]", "done")],
                    update.ThePieces.Select(piece => (piece.Kind, piece.ItemName()!, piece.OwedTo, piece.Placed, piece.Ended)));
                Assert.Equal(["round 2 of 'rounds.items' was added for update u1: the run has been given 1 of the 1 amend.maxAddedIterations allows"], events);
                Assert.Equal([("rounds.item-check", string.Empty), ("rounds.item-check+2", "three")], checks);
                Assert.Equal((nameof(FlowInstanceState.Failed), nameof(FlowInstanceState.Done)), (steps["rounds.items"].State, steps["rounds.items+2"].State));
                Assert.Equal(nameof(FlowInstanceState.Done), steps["rounds.items+2[0].build"].State);
            }
        }
    }

    /// <summary>
    /// A crash once the round added for an update is written into its entry, before the group is drawn again, is gone on
    /// from by the resume: the session that takes the run up draws the group once more as the entry says, and the owed
    /// work is carried by it - no round is added twice, and nothing of the round before is built again.
    /// </summary>
    [Fact]
    public async Task ACrashOnceARoundIsAdded_IsGoneOnFromByTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.AddedIteration is not null));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, AddingThree([]), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: crashing, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(crashing.Ended);
        }

        // On disk: the round added in the update's entry, and the group not drawn again.
        Assert.Equal("rounds.items+2", UpdatesOf(harness, layout).Updates.Single().AddedIteration);
        Assert.DoesNotContain("rounds.items+2", harness.OrchestrationStore.ReadFlow(layout)!.Steps.Select(step => step.Instance));

        var (_, _, again) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();

        using (again)
        {
            var resumed = new FlowCoordinator(definition, AddingThree(checks), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition)), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            var end = await DriveAsync(resumed);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal(("rounds.items+2", "rounds.items+2[0]", "done"), (update.AddedIteration, update.ThePieces.Single().Placed, update.ThePieces.Single().Ended));
            Assert.Equal([("rounds.item-check+2", "three")], checks);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Equal(
                ["rounds.items+2[0].build", "rounds.items[0].build", "rounds.items[1].build"],
                steps.Where(step => step.Step == "rounds.items.build").Select(step => step.Instance).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(steps, step => step.Instance == "rounds.items+3");
        }
    }

    /// <summary>
    /// The runner of <see cref="Ended"/> whose planner concludes <paramref name="plan"/> - "one" and "two" where it says
    /// none - and whose update adds "three"; every check answers the one list it is handed, and the check that opens the
    /// round notes in <paramref name="checks"/> the names of the owed work it was handed.
    /// </summary>
    private static FakeRunner AddingThree(List<(string Instance, string Owed)> checks, JsonNode? plan = null)
        => new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: plan?.DeepClone() ?? Concluded(Item("one"), Item("two"))))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("two"),
                Item("three"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }))
            .On("item-check", run =>
            {
                var owed = run.Read!.TryGetValue("owed", out var owing) && !owing!.IsMissing ? (owing.ToNode() as JsonArray)! : new JsonArray();

                lock (checks)
                {
                    checks.Add((run.Instance, string.Join(",", owed.Select(FlowWorkItems.NameOf))));
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
                {
                    ["ok"] = true,
                    ["problems"] = new JsonArray(),
                    ["items"] = UnionOf(run),
                });
            });

    /// <summary>Waits, bounded as a session is, until the round of the work-item group stands ended in <paramref name="state"/> in the run's records.</summary>
    private static async Task RoundEndedAsync(HarnessFactory harness, OrchestratorLayout layout, string state)
    {
        for (var at = 0; ; at++)
        {
            if (harness.OrchestrationStore.ReadFlow(layout)?.Steps.FirstOrDefault(step => step.Instance == "rounds.items") is { State: var held } && held == state)
            {
                return;
            }

            if (at > 3000)
            {
                throw new TimeoutException($"the round never ended {state}");
            }

            await Task.Delay(10, Token);
        }
    }

    /// <summary>
    /// A planner declared in the same repeat iteration as the work-item group it plans: the group the update holds open
    /// is no work the iteration waits on to come to rest, so the plan stands, the added piece joins the round the group
    /// holds open, and the run ends done - no circular wait between the hold and the plan.
    /// </summary>
    [Fact]
    public async Task APlannerInTheIterationOfItsWorkItems_LetsThePlanStand()
    {
        using var temp = new TempDirectory();
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", Planned) with
        {
            Amend = new FlowAmend("request", 5, null, "rounds.planupdate", null, "rounds.plan.latest.scope", "rounds.items.build", "rounds.items.rebuild", "rounds.items.withdraw", null),
        };
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("two"), "the parser is a second work item")),
                Item("one"),
                Item("two"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }));

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var probe = await UntilAskedAsync(coordinator, "added|");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("build the parser too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(probe.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal((FlowUpdateStates.Done, "rounds#1.items[1]", "rounds#1.items[1].build"), (update.State, update.ThePieces.Single().Placed, update.ThePieces.Single().CarriedBy));
            var built = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Where(step => step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done)).Select(step => step.Instance);
            Assert.Equal(["rounds#1.items[0].build", "rounds#1.items[1].build"], built.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// A told piece naming a scope reaches every reason step that starts later in it - in the part of its own prompt -
    /// save a step of an instance a piece of an update placed, which its own prompt's <c>{piece.*}</c> tells: the item
    /// that waited reads the part, and the one the update added reads none.
    /// </summary>
    [Fact]
    public async Task AToldPieceNamingARound_IsReadByItsLaterSteps_SaveThoseOfAPiecePlaced()
    {
        using var temp = new TempDirectory();
        var definition = Parse() with { MaxOpenAgents = 1 };
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"), Item("two"))))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Told("rounds", "whoever builds in the round reads this"), Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("two"),
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
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // To the first item's probe: the second waits for the run's one open agent.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var probe = await UntilAskedAsync(coordinator, "added|");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("tell the round, and add the registry", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(probe.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            Assert.Equal(PlanToldOfTheRound, runner.PlanTolds["rounds.items[1].build"]);
            Assert.Null(runner.PlanTolds["rounds.items[2].build"]);
        }
    }

    /// <summary>What a step that starts later in the round reads of the told piece naming it, in the part of its own prompt.</summary>
    private const string PlanToldOfTheRound = """
        The plan this work follows was updated before this work began.
        What changed it: the request was amended on 2026-09-30 16:32:14: "tell the round, and add the registry"
        For your work (rounds):
        - What changed: whoever builds in the round reads this
        - Keep: what stays
        - Undo: what is taken back
        - Do instead: what is done instead
        """;

    /// <summary>Drives the run on, answering every question it reaches, until one whose problem begins with <paramref name="asks"/> is open: that one, left unanswered.</summary>
    private static async Task<FlowQuestion> UntilAskedAsync(FlowCoordinator coordinator, string asks)
    {
        for (var sessions = 0; sessions < 20; sessions++)
        {
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            if (coordinator.OpenQuestions.FirstOrDefault(asked => asked.Problem.StartsWith(asks, StringComparison.Ordinal)) is { } found)
            {
                return found;
            }

            foreach (var asked in coordinator.OpenQuestions.ToList())
            {
                Assert.True(coordinator.TryAnswer(asked.Id, asked.Kind == "loopBound" ? "accept" : "go", null, out _), asked.Problem);
            }
        }

        throw new TimeoutException($"no question asking '{asks}' was raised");
    }
}
