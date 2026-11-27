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
    /// The flow of <see cref="Ended"/> whose round of work items hands a failure to the group around it, and that group
    /// passes it over, so a round whose work item failed fails the group around it too and the run goes on.
    /// </summary>
    private static string AroundPassesOver()
        => Ended().Replace(
            "  - name: rounds\n    dependsOn: [planning]\n    steps:",
            "  - name: rounds\n    dependsOn: [planning]\n    onFailure: continue\n    steps:",
            StringComparison.Ordinal);

    /// <summary>
    /// The flow of <see cref="Ended"/> whose round opens through the three steps the shipped implementation workflow
    /// opens one through: the check, a pause asked only where the check found problems, and the work-item group, which
    /// depends on that pause.
    /// </summary>
    private static string ThroughAPause()
        => Ended().Replace(
            "      - name: items\n        dependsOn: [item-check]\n",
            "      - name: item-problems\n        type: pause\n        dependsOn: [item-check]\n        when: '!item-check.ok'\n        question: problems?\n        options: [continue, stop]\n      - name: items\n        dependsOn: [item-problems]\n",
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
    /// The flow of <see cref="Reviewed"/> whose loop runs two iterations, and whose round from the second iteration on
    /// opens through a pause between the check that opens it and the work-item group - the shipped flow's audit of a
    /// later round's items - so the check has joined the owed work and the group has not expanded while the pause waits.
    /// </summary>
    private static string AuditedLater()
        => Reviewed()
            .Replace("maxIterations: 3", "maxIterations: 2", StringComparison.Ordinal)
            .Replace(
                "      - name: items\n        dependsOn: [item-check]\n",
                "      - name: item-audit\n        type: pause\n        dependsOn: [item-check]\n        when: 'iteration != 1'\n        question: audited?\n        options: [go, stop]\n      - name: items\n        dependsOn: [item-check, item-audit]\n",
                StringComparison.Ordinal);

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

    /// <summary>A work item of the plan, shaped as the real plan check reads one.</summary>
    private static JsonObject Item(string name)
        => new()
        {
            ["name"] = name,
            ["title"] = $"The {name} item",
            ["instructions"] = $"Write the {name} item's code.",
            ["done"] = $"the {name} item ships",
            ["ownedPaths"] = new JsonArray($"src/{name}/**"),
            ["forbiddenPaths"] = new JsonArray(),
            ["reuses"] = new JsonArray(new JsonObject { ["path"] = "file.txt", ["what"] = "the content" }),
            ["creates"] = new JsonArray(),
            ["replaces"] = new JsonArray(),
            ["tests"] = new JsonArray("OneTests"),
            ["noTests"] = "",
            ["classes"] = new JsonArray(),
            ["noClass"] = $"the {name} item decides nothing: it adds a file",
        };

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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), harness.OrchestrationStore.ReadFlowUpdates(layout)), end.Summary, 0, DateTimeOffset.UtcNow);
            var report = File.ReadAllText(layout.ReportFile);

            Assert.Equal(LoopReport, UpdatesSectionOf(report));

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
            Assert.Equal(["  plan update u1 (amendment): done", "    round 2 of 'rounds' was added for update u1"], UpdateLinesOf(listed));
        }
    }

    /// <summary>
    /// Work a plan update owes to a loop that decides after a step of its own is carried the same way where that step
    /// ends at the loop's bound: the decision read there has the iteration the work is owed added first, as an
    /// iteration's end would, and the loop goes on to it - never asking its bound over work the update owes it.
    /// </summary>
    [Fact]
    public async Task WorkOwedToALoopThatDecidesAtItsReview_AtItsBound_IsCarriedByAnIterationAdded()
    {
        using var temp = new TempDirectory();
        var flow = Flow.ReplaceLineEndings("\n");
        const string Rounds = "  - name: rounds\n    dependsOn: [planning]\n    steps:";
        Assert.Contains(Rounds, flow, StringComparison.Ordinal);
        var definition = Parse(
            flow.Replace(Rounds, "  - name: rounds\n    dependsOn: [planning]\n    repeat:\n      until: 'false'\n      maxIterations: 1\n      decideAfter: review\n    steps:", StringComparison.Ordinal)
                + "\n      - name: review\n        type: pause\n        question: reviewed?\n        options: [stay, go]\n        dependsOn: [items]");
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The first round runs and ends, and the iteration's review - its deciding step - asks: the amendment is
            // taken there, and planned whole while the review waits, its piece owed to the loop's next iteration.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var review = await UntilAskedAsync(coordinator, "reviewed?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            _ = await DriveAsync(coordinator, held: "pause");

            var owed = await UntilAsync(harness, layout, FlowUpdateStates.Done);
            Assert.Equal("the next iteration of 'rounds'", owed.ThePieces.Single().OwedTo);

            // The review ends at the loop's bound: the iteration the work is owed is added, and the loop goes on to it.
            Assert.True(coordinator.TryAnswer(review.Id, "go", null, out _));
            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            Assert.Equal("rounds#2", UpdatesOf(harness, layout).Updates.Single().AddedIteration);
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Contains(steps, step => step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done) && (step.Item as JsonObject)?["name"]?.GetValue<string>() == "three");
        }
    }

    /// <summary>
    /// The flow of <see cref="Flow"/> whose round of work items sits in a repeat that decides at a review after its work
    /// items: <paramref name="until"/> is its until, read there, and <paramref name="bound"/> its maxIterations; with
    /// <paramref name="audited"/> an audit follows the loop, where a plan update finds it ended.
    /// </summary>
    private static string DecidedAtItsReview(string until, int bound, bool audited, string? leftover = null)
        => Flow.Replace(
            "  - name: rounds\n    dependsOn: [planning]\n    steps:",
            $"  - name: rounds\n    dependsOn: [planning]\n    repeat:\n      until: '{until}'\n      maxIterations: {bound.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n      decideAfter: review{(leftover is null ? string.Empty : $"\n      leftover: '{leftover}'")}\n    steps:",
            StringComparison.Ordinal)
            + "\n      - name: review\n        type: pause\n        question: reviewed?\n        options: [stay, go]\n        dependsOn: [items]"
            + (audited ? "\n  - name: audit\n    type: pause\n    question: anything else?\n    options: [stay, go]\n    dependsOn: [rounds]" : string.Empty);

    /// <summary>The runner of the deciding-review tests: one item planned, and an update that adds a third, each check passing what it reads.</summary>
    private static FakeRunner AddingAThirdItem()
        => new FakeRunner()
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

    /// <summary>
    /// Work a plan update owes a loop that ended at its decision - its until read true at its review - is carried all the
    /// same, as it is for a loop that ended at an iteration's end: the decision gives way to the work, which runs the loop
    /// again to its next iteration, and the iteration's own record says the loop went on there. Never a loop that runs
    /// again only to end at the decision it had already read, the piece owed for ever.
    /// </summary>
    [Fact]
    public async Task WorkOwedToALoopThatEndedAtItsDecision_IsCarriedToItsNextIteration()
    {
        using var temp = new TempDirectory();
        var definition = Parse(DecidedAtItsReview("true", bound: 3, audited: true));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, AddingAThirdItem(), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The round runs, its review ends the loop where it decides, and the audit after the loop asks: the
            // amendment is taken there.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");
            var ended = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Done), FlowLoopDecision.Ends), (ended["rounds"].State, ended["rounds#1"].Decided?.To));

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            // The piece joined the loop's next iteration, below its bound, with no iteration added; the first
            // iteration's decision gave way to it, and the second iteration ended the loop at its own review.
            var update = UpdatesOf(harness, layout).Updates.Single();
            var piece = update.ThePieces.Single();
            Assert.Equal(("the next iteration of 'rounds'", "rounds#2.items[1]"), (piece.OwedTo, piece.Placed));
            Assert.Null(update.AddedIteration);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Done), "three"), (steps["rounds#2.items[1].build"].State, (steps["rounds#2.items[1].build"].Item as JsonObject)?["name"]?.GetValue<string>()));
            Assert.Equal([FlowLoopDecision.GoesOn, FlowLoopDecision.Ends], new[] { "rounds#1", "rounds#2" }.Select(iteration => steps[iteration].Decided?.To));
            Assert.Equal(nameof(FlowInstanceState.Done), steps["rounds"].State);
            Assert.DoesNotContain("rounds#3", steps.Keys, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// A loop that ended leaving something - its bound accepted with its until still false - and runs again for work a
    /// plan update owes it holds nothing it left while it runs, and where it then ends with its until true it left nothing:
    /// its record, what <c>run.leftover</c> reads and the report say no leftover, never what the first end left for work
    /// the last review found done.
    /// </summary>
    [Fact]
    public async Task ALoopThatEndedLeavingAndRunsAgain_EndingCleanLeavesNothing()
    {
        using var temp = new TempDirectory();
        var definition = Parse(DecidedAtItsReview("review.answer == ''stay''", bound: 1, audited: true, leftover: "review.answer"));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, AddingAThirdItem(), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The first round's review goes on with its until false, the bound's question is accepted, and the loop ends
            // leaving what that review said; the audit after it asks, and the amendment is taken there.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");
            var ended = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds");
            Assert.Equal((nameof(FlowInstanceState.Done), "\"go\""), (ended.State, ended.Leftover?.ToJsonString()));

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            // The loop runs again for the owed work, and its second review finds nothing more: its until reads true.
            var review = await UntilAskedAsync(coordinator, "reviewed?");
            Assert.Null(coordinator.Instances["rounds"].Leftover);
            Assert.True(coordinator.TryAnswer(review.Id, "stay", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            // It ended with its until true and left nothing: the instance run.leftover reads, its record, and the report.
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Done), FlowLoopDecision.Ends), (steps["rounds"].State, steps["rounds#2"].Decided?.To));
            Assert.Null(coordinator.Instances["rounds"].Leftover);
            Assert.Null(steps["rounds"].Leftover);

            _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), harness.OrchestrationStore.ReadFlowUpdates(layout)), end.Summary, 0, DateTimeOffset.UtcNow);
            var report = File.ReadAllText(layout.ReportFile);
            Assert.DoesNotContain("## What the run's loops left", report, StringComparison.Ordinal);
            Assert.DoesNotContain("loop rounds left:", report, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Work a plan update owes a loop that asked at its bound where its review decided is carried by the iteration added
    /// for it: the question is withdrawn, the decision it was asked for goes on in the iteration's own record, and the loop
    /// makes the added iteration - never one left running with its question gone and nothing to carry it on.
    /// </summary>
    [Fact]
    public async Task WorkOwedToALoopThatAskedAtItsDecision_WithdrawsTheQuestion_AndIsCarriedByAnIterationAdded()
    {
        using var temp = new TempDirectory();
        var definition = Parse(DecidedAtItsReview("false", bound: 1, audited: false));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, AddingAThirdItem(), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The first round runs, its review ends at the loop's bound of one, and the loop asks there: the amendment
            // is taken with that question open.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            _ = await DriveAsync(coordinator, held: "loopBound");
            var bound = Assert.Single(coordinator.OpenQuestions);
            Assert.Equal("loopBound", bound.Kind);
            Assert.Equal(FlowLoopDecision.Asks, harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds#1").Decided?.To);

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            _ = await DriveAsync(coordinator, held: "loopBound");
            _ = await UntilAsync(harness, layout, FlowUpdateStates.Done);

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            // The question was withdrawn for the iteration added and never asked again of the first iteration: the
            // only other bound question is the added iteration's own, at its review. The first iteration's decision
            // went on, and the owed item was built in the added iteration.
            var bounds = harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions.Where(question => question.Kind == "loopBound").ToList();
            Assert.Equal(
                [("withdrawn", string.Empty), ("answered", "accept")],
                bounds.Select(question => (question.State, string.Join("/", question.Answers.Select(answer => answer.Answer)))));
            Assert.Equal(bound.Id, bounds[0].Id);
            Assert.Equal("rounds#2", UpdatesOf(harness, layout).Updates.Single().AddedIteration);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal([FlowLoopDecision.GoesOn, FlowLoopDecision.EndsLeaving], new[] { "rounds#1", "rounds#2" }.Select(iteration => steps[iteration].Decided?.To));
            Assert.Contains(steps.Values, step => step.Instance.StartsWith("rounds#2.", StringComparison.Ordinal) && step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done) && (step.Item as JsonObject)?["name"]?.GetValue<string>() == "three");
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), harness.OrchestrationStore.ReadFlowUpdates(layout)), end.Summary, 0, DateTimeOffset.UtcNow);
            var report = File.ReadAllText(layout.ReportFile);

            Assert.Equal(GroupReport, UpdatesSectionOf(report));

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

            Assert.Equal(["  plan update u1 (amendment): done", "    round 2 of 'rounds.items' was added for update u1"], UpdateLinesOf(listed));
        }
    }

    /// <summary>
    /// The check that opens a round of a work-item group with no loop around it is the check that opens a round whichever
    /// opens it - the group's own first round, and the round a plan update adds - and is handed what that check alone is
    /// handed: the issues the run owes its next round beside the work updates owe the group. One owner decides which
    /// check opens a round, so an issue owed the next round reaches the round an update adds, where an item that settles
    /// it is held to it, never refused as owed to no round.
    /// </summary>
    [Fact]
    public async Task TheCheckOfARoundAnUpdateAdds_WithNoLoopAroundIt_IsHandedTheIssuesOwedTheNextRound()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var handed = new List<(string Instance, string? Next, string Owed)>();
        var runner = AddingThree([]).On("item-check", run =>
        {
            var owed = run.Read!.TryGetValue("owed", out var owing) && !owing!.IsMissing ? (owing.ToNode() as JsonArray)! : new JsonArray();

            lock (handed)
            {
                handed.Add((run.Instance, run.Read.TryGetValue("next", out var next) ? next.ToNode()?.ToJsonString() : null, string.Join(",", owed.Select(FlowWorkItems.NameOf))));
            }

            return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            });
        });

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal("rounds.items+2", UpdatesOf(harness, layout).Updates.Single().AddedIteration);
        }

        // Each round's check is handed the issues owed the next round - none are owed here, and the list is still the
        // check's to hold its items to - and the added round's the owed work besides.
        Assert.Equal([("rounds.item-check", "[]", string.Empty), ("rounds.item-check+2", "[]", "three")], handed);
    }

    /// <summary>
    /// A round of a work-item group with no loop around it that failed under an update - its failure passed over, so the
    /// run goes on - has ended as a round that ended done has: the work the update owes it is carried by a round added
    /// where <c>amend.maxAddedIterations</c> allows one. Where the run has none left to add, or the round's failure failed
    /// the group around it too - which draws nothing more, so no round of the group is drawn again - the update is given
    /// up with why, every piece it decided saying it was never carried out, and nothing of it placed or added.
    /// </summary>
    /// <param name="aroundFails">Whether the round's failure is passed over by the group around it, which it fails, rather than by the work-item group itself.</param>
    /// <param name="allowed">The rounds <c>amend.maxAddedIterations</c> lets updates add.</param>
    /// <param name="why">Why the update is given up; null where a round is added for it.</param>
    [Theory]
    [InlineData(false, 0, "its work is owed to 'rounds.items', whose round has ended with no loop around it, and updates have added the 0 round(s) amend.maxAddedIterations allows: nothing would carry it out, the request is recorded and no plan holds it")]
    [InlineData(false, 1, null)]
    [InlineData(true, 1, "its work is owed to 'rounds.items', whose round has ended with no loop around it, and 'rounds', the group around it, ended failed and draws nothing more: nothing would carry it out, the request is recorded and no plan holds it")]
    public async Task WorkOwedToAGroupWhoseRoundFailedUnderTheUpdate_IsCarriedByARoundAdded_OrGivenUpWhereNoneIsLeft(bool aroundFails, int allowed, string? why)
    {
        using var temp = new TempDirectory();
        var parsed = Parse(aroundFails ? AroundPassesOver() : PassedOver());
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            await EndedAsync(harness, layout, aroundFails ? "rounds" : "rounds.items", nameof(FlowInstanceState.Failed));

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

            if (why is not null)
            {
                // Nothing would carry the work out: the update is given up with why, its piece never carried out, and
                // no round added.
                Assert.Equal((FlowUpdateStates.Failed, why, (string?)null), (update.State, update.Why, update.AddedIteration));
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
            var coordinator = TestAdvice.Coordinator(definition, AddingThree([]), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: crashing, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
            var resumed = TestAdvice.Coordinator(definition, AddingThree(checks), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
    /// A check that waits on the plan update it raised, left paused by a crash once the update was done, is left as the
    /// records hold it by a session that begins over a stop asked between sessions - the stop draws nothing, so nothing
    /// would stand in its place - and the session after runs it again on the list as it now is: the added round opens
    /// and is built.
    /// </summary>
    [Fact]
    public async Task ACheckWaitingOnTheUpdateItRaised_IsLeftAsTheRecordsHoldItByAStopBetweenSessions_AndRunsAgainAfter()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();
        var runner = FindingAProblemOnce(checks);
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.Cause == FlowUpdateCauses.PlanCheck && update.State == FlowUpdateStates.Done));

        using (records)
        {
            await UntilTheUpdateItRaisedAsync(TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: crashing, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout)), crashing);
        }

        // On disk: the update done, and the check that raised it still paused on it.
        Assert.Equal(nameof(FlowInstanceState.Paused), StepOf(harness, layout, "rounds.item-check+2").State);

        var (_, _, stopped) = Records("r1", temp.Path, definition);

        using (stopped)
        {
            var resumed = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: stopped, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            resumed.RequestStop();
            _ = await resumed.RunAsync(Token).WaitAsync(Bound, Token);
        }

        // The stop ended nothing that waited and drew nothing: the check stays paused on its update, as the records held it.
        Assert.Equal(nameof(FlowInstanceState.Paused), StepOf(harness, layout, "rounds.item-check+2").State);
        Assert.DoesNotContain(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance == "rounds.item-check+3");

        await TheRoundOpensAgainAsync(temp.Path, definition, harness, layout, runner, checks);
    }

    /// <summary>
    /// A crash once the check that waited on the plan update it raised is ended for that update, before its next visit is
    /// drawn, is gone on from by the resume: the records alone say the check is owed a new visit, and the session that
    /// takes the run up draws it - the added round opens and is built.
    /// </summary>
    [Fact]
    public async Task ACrashOnceTheWaitingCheckIsEndedForItsUpdate_BeforeItsNextVisit_IsGoneOnFromByTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();
        var runner = FindingAProblemOnce(checks);
        var crashing = new CrashingRecords(records, _ => false, picksStep: (instance, to) => instance == "rounds.item-check+2" && to == nameof(FlowInstanceState.Stopped));

        using (records)
        {
            await UntilTheUpdateItRaisedAsync(TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: crashing, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout)), crashing);
        }

        // On disk: the check ended for the update it raised, and nothing drawn in its place.
        var ended = StepOf(harness, layout, "rounds.item-check+2");

        Assert.Equal((nameof(FlowInstanceState.Stopped), "u2"), (ended.State, ended.EndedFor));
        Assert.DoesNotContain(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance == "rounds.item-check+3");

        await TheRoundOpensAgainAsync(temp.Path, definition, harness, layout, runner, checks);
    }

    /// <summary>
    /// The runner of <see cref="Ended"/> whose first update adds "three", whose round added for it opens with a check
    /// that finds a problem on that visit alone - raising a plan update that plans nothing more - and whose every other
    /// check answers the one list it is handed, noting in <paramref name="checks"/> the owed work each was handed.
    /// </summary>
    private static FakeRunner FindingAProblemOnce(List<(string Instance, string Owed)> checks)
    {
        var planned = 0;

        return AddingThree(checks)
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Interlocked.Increment(ref planned) == 1
                ? Concluded(new JsonArray(Piece("added", Item("three"), "the registry is a third work item")), Item("one"), Item("two"), Item("three"))
                : Concluded(Item("one"), Item("two"), Item("three"))))
            .On("item-check", run =>
            {
                var owed = run.Read!.TryGetValue("owed", out var owing) && !owing!.IsMissing ? (owing.ToNode() as JsonArray)! : new JsonArray();

                lock (checks)
                {
                    checks.Add((run.Instance, string.Join(",", owed.Select(FlowWorkItems.NameOf))));
                }

                var ok = run.Instance != "rounds.item-check+2";
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
                {
                    ["ok"] = ok,
                    ["problems"] = ok ? new JsonArray() : new JsonArray("the registry owns a file the parser owns"),
                    ["items"] = UnionOf(run),
                });
            });
    }

    /// <summary>
    /// Drives a run of <see cref="Ended"/> until its crash: through its first round and the audit, an amendment adding a
    /// round, and the check that opens that round raising a plan update - every question answered to go on.
    /// </summary>
    private static async Task UntilTheUpdateItRaisedAsync(FlowCoordinator coordinator, CrashingRecords crashing)
    {
        _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
        Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
        var audit = await UntilAskedAsync(coordinator, "anything else?");

        Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
        Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

        for (var sessions = 0; !crashing.Ended; sessions++)
        {
            Assert.True(sessions < 20, "the run never reached its crash");
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            foreach (var asked in coordinator.OpenQuestions.ToList())
            {
                Assert.True(coordinator.TryAnswer(asked.Id, "go", null, out _), asked.Problem);
            }
        }
    }

    /// <summary>
    /// Takes the run up from its records and drives it to its end: the check that opens the added round runs again on
    /// the list as it now is - handed the owed work - and the round is built, the run done.
    /// </summary>
    private static async Task TheRoundOpensAgainAsync(string directory, WorkflowDefinition definition, HarnessFactory harness, OrchestratorLayout layout, FakeRunner runner, List<(string Instance, string Owed)> checks)
    {
        var (_, _, again) = Records("r1", directory, definition);

        using (again)
        {
            lock (checks)
            {
                checks.Clear();
            }

            var resumed = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));
            var end = await DriveAsync(resumed);

            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal([("rounds.item-check+3", "three")], checks);
            Assert.Equal(nameof(FlowInstanceState.Done), StepOf(harness, layout, "rounds.items+2[0].build").State);
        }
    }

    /// <summary>The record of the instance <paramref name="instance"/> in the run's state.</summary>
    private static FlowStepRecord StepOf(HarnessFactory harness, OrchestratorLayout layout, string instance)
        => harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == instance);

    /// <summary>
    /// A round added to a work-item group with no loop around it opens through the real plan check: the check is handed
    /// the owed work beside the round's own items and answers them as one list, and the round built from that list
    /// carries the owed work alone - the items the round before built are not built again.
    /// </summary>
    [Fact]
    public async Task ARoundAdded_OpensThroughTheRealPlanCheck_OnTheOwedWorkAlone()
    {
        var harness = new HarnessFactory();
        using var repository = new TempDirectory();
        repository.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(repository.Path, Token);
        await harness.CommitAllAsync(repository.Path, "initial", Token);

        var definition = Parse(Ended());
        var layout = OrchestratorLayout.Of(new HarnessLayout(repository.Path, repository.Path), "r1");
        var check = new PlanCheckRunner(harness.ContextLoader, harness.Platform, new PlanValidator(harness.FileSystem, harness.PathBudget, harness.Platform));
        var checks = new List<(string Instance, string Owed)>();

        // The check that opens the round is the real one, run where the run's repository is.
        var runner = AddingThree([])
            .OnAsync("item-check", async (run, cancellationToken) =>
            {
                var owed = run.Read!.TryGetValue("owed", out var owing) && !owing!.IsMissing ? (owing.ToNode() as JsonArray)! : new JsonArray();

                lock (checks)
                {
                    checks.Add((run.Instance, string.Join(",", owed.Select(FlowWorkItems.NameOf))));
                }

                return await check.RunAsync(run with { Place = new FlowRunPlace(repository.Path, "r1", null, definition) }, cancellationToken).ConfigureAwait(false);
            });

        using var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition));
        var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

        _ = await coordinator.RunAsync(Token);
        Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
        var audit = await UntilAskedAsync(coordinator, "anything else?");

        Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
        Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

        var end = await DriveAsync(coordinator);
        Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

        Assert.Equal([("rounds.item-check", string.Empty), ("rounds.item-check+2", "three")], checks);
        var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
        Assert.Equal(["one", "two", "three"], ((steps["rounds.item-check+2"].Output?["items"] as JsonArray) ?? []).Select(FlowWorkItems.NameOf));
        Assert.Equal(
            [("rounds.items+2[0].build", "three"), ("rounds.items[0].build", "one"), ("rounds.items[1].build", "two")],
            steps.Values
                .Where(step => step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done))
                .Select(step => (step.Instance, FlowWorkItems.NameOf(step.Item)))
                .OrderBy(built => built.Instance, StringComparer.Ordinal));
        Assert.Equal(("rounds.items+2[0]", "done"), (UpdatesOf(harness, layout).Updates.Single().ThePieces.Single().Placed, UpdatesOf(harness, layout).Updates.Single().ThePieces.Single().Ended));
    }

    /// <summary>
    /// Every check of the run is handed, apart from the names the work-item group carries, the names of the items an
    /// opened round holds that nothing owed is to build again - the items its rules that read the main tree as it now is
    /// pass over: none before any round opens; a round's own items once it has; never an item an update owes the next
    /// round that does one again, though an opened round carries its name, for that round builds it as the piece has it;
    /// and never an item of the list the check that opens a round was opening it with, where that check raised the update
    /// under way - the round it joins has not opened, and no instance carries it.
    /// </summary>
    [Fact]
    public async Task EveryCheck_IsHandedTheItemsAnOpenedRoundHolds_NeverOnesOwedToBeBuiltAgain_NorARoundNotOpened()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var handed = new List<string>();
        var again = Item("one");
        again["title"] = "The one item, done again";
        var planned = 0;

        // A round is handed as its items, the names as texts: each said by its name.
        static string NamesOf(FlowStepRun run, string key)
            => run.Read!.TryGetValue(key, out var given) && !given.IsMissing
                ? $"[{string.Join(", ", given.ToNode()!.AsArray().Select(entry => FlowWorkItems.NameOf(entry) ?? entry!.GetValue<string>()))}]"
                : "none";

        void Note(FlowStepRun run)
        {
            lock (handed)
            {
                handed.Add($"{run.Instance}: round {NamesOf(run, "round")}, carried {NamesOf(run, "carried")}, opened {NamesOf(run, "opened")}");
            }
        }

        // The update the amendment raises does the round's 'one' again and adds 'three', both owed to a round added; the
        // check that opens that round finds a problem once, raising an update the planner answers with no piece.
        var runner = AddingThree([])
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Interlocked.Increment(ref planned) == 1
                ? Concluded(new JsonArray(Piece("changed", again.DeepClone(), "the one item is done again"), Piece("added", Item("three"), "the registry is a third work item")), again.DeepClone(), Item("two"), Item("three"))
                : Concluded(again.DeepClone(), Item("two"), Item("three"))))
            .On("check", run =>
            {
                Note(run);
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["ok"] = true, ["problems"] = new JsonArray(), ["items"] = UnionOf(run) });
            })
            .On("item-check", run =>
            {
                Note(run);
                var ok = run.Instance != "rounds.item-check+2";
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
                {
                    ["ok"] = ok,
                    ["problems"] = ok ? new JsonArray() : new JsonArray("the registry owns a file the parser owns"),
                    ["items"] = UnionOf(run),
                });
            });

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("do the one item again, and add the registry", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal([(FlowUpdateCauses.Amendment, FlowUpdateStates.Done), (FlowUpdateCauses.PlanCheck, FlowUpdateStates.Done)], UpdatesOf(harness, layout).Updates.Select(update => (update.Cause, update.State)));
        }

        Assert.Equal(
            [
                "planning.check: round none, carried [], opened []",
                "rounds.item-check: round none, carried [], opened []",
                "planning.check+2: round [one, two], carried [one, two], opened [one, two]",
                "rounds.item-check+2: round [one, two], carried [one, two], opened [two]",
                "planning.check+3: round [one, two, three], carried [one, two], opened [two]",
                "rounds.item-check+3: round [one, two], carried [one, two], opened [two]",
            ],
            handed);
    }

    /// <summary>
    /// A round added to a work-item group that opens through a step between its check and the group - the pause the
    /// shipped implementation workflow asks only where the check found problems - opens on the check's new visit: the
    /// check and every step between are drawn again, so the group waits for the new check, never expanding from the
    /// visit before or from one that has not run.
    /// </summary>
    [Fact]
    public async Task ARoundAddedThroughAStepBetweenTheCheckAndTheGroup_OpensOnTheChecksNewVisit()
    {
        using var temp = new TempDirectory();
        var definition = Parse(ThroughAPause());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, AddingThree(checks), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var piece = UpdatesOf(harness, layout).Updates.Single().ThePieces.Single();
            Assert.Equal(("rounds.items+2[0]", "rounds.items+2[0].build", "done"), (piece.Placed, piece.CarriedBy, piece.Ended));
            Assert.Equal([("rounds.item-check", string.Empty), ("rounds.item-check+2", "three")], checks);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal(
                [("rounds.item-problems", nameof(FlowInstanceState.Skipped)), ("rounds.item-problems+2", nameof(FlowInstanceState.Skipped)), ("rounds.items+2", nameof(FlowInstanceState.Done))],
                new[] { "rounds.item-problems", "rounds.item-problems+2", "rounds.items+2" }.Select(id => (id, steps[id].State)));
        }
    }

    /// <summary>
    /// A crash once an update is done and before what it owes is carried loses nothing: the session that takes the run
    /// up carries it from the update's entry - a loop around the round that ended runs again for it, and a work-item
    /// group with no loop around it is given its round - and the owed work is built.
    /// </summary>
    /// <param name="looped">Whether the round sits in a loop that ended done, rather than in no loop at all.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACrashOnceTheUpdateIsDone_BeforeWhatItOwesIsCarried_IsCarriedByTheResume(bool looped)
    {
        using var temp = new TempDirectory();
        var definition = Parse(looped ? EndedLoop() : Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.State == FlowUpdateStates.Done));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, AddingThree([]), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: crashing, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(crashing.Ended);
        }

        // On disk: the update done, its piece owed, and nothing carried.
        var done = UpdatesOf(harness, layout).Updates.Single();
        Assert.Equal((FlowUpdateStates.Done, (string?)null, true), (done.State, done.AddedIteration, done.ThePieces.Single().IsOwed));

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var resumed = TestAdvice.Coordinator(definition, AddingThree([]), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            var end = await DriveAsync(resumed);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var piece = UpdatesOf(harness, layout).Updates.Single().ThePieces.Single();
            Assert.Equal((looped ? "rounds#2.items[2]" : "rounds.items+2[0]", "done"), (piece.Placed, piece.Ended));
        }
    }

    /// <summary>
    /// A change given while the round an update added to a work-item group with no loop around it waits to open joins
    /// that round: an update queued behind the one that added it is taken at its turn - not refused as past the group's
    /// work items, at the allowance of one round - and the one round, opened once, carries the work of both.
    /// </summary>
    [Fact]
    public async Task AnUpdateQueuedBehindTheOneThatAddedARound_JoinsThatRound()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();
        var planned = 0;
        var runner = AddingThree(checks).On("planupdate", _ => Interlocked.Increment(ref planned) == 1
            ? FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("two"),
                Item("three")))
            : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("four"), "the cache is a fourth work item")),
                Item("one"),
                Item("two"),
                Item("three"),
                Item("four"))));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The round runs and ends, and the audit after it asks: two amendments are given there, the second queued
            // behind the first.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the cache as well", "amend-orchestrator-request", out problem), problem ?? "taken");
            Assert.Equal([FlowUpdateStates.UnderWay, FlowUpdateStates.Queued], UpdatesOf(harness, layout).Updates.Select(update => update.State));
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            // The first added the round; the second, taken while that round waited to open, joined it.
            var updates = UpdatesOf(harness, layout).Updates;
            Assert.Equal(
                [("u1", FlowUpdateStates.Done, "rounds.items+2", (string?)null), ("u2", FlowUpdateStates.Done, null, null)],
                updates.Select(update => (update.Id, update.State, update.AddedIteration, update.Why)));
            Assert.Equal(
                [("three", "rounds.items+2[0]", "done"), ("four", "rounds.items+2[1]", "done")],
                updates.SelectMany(update => update.ThePieces).Select(piece => (piece.ItemName()!, piece.Placed, piece.Ended)));
            Assert.Equal([("rounds.item-check", string.Empty), ("rounds.item-check+2", "three,four")], checks);
        }
    }

    /// <summary>
    /// A removed piece whose work item no instance carries withdraws the work an earlier update owes a round under that
    /// name: an update queued behind the one that added a round for "three" takes it back before that round opens, so
    /// no round builds it - never the item the plan took back - and, nothing owed the round any more, the round is taken
    /// back with it: its check, drawn again for it, ends skipped saying why and never runs, the group is not drawn again,
    /// and the group around it ends as it had. The earlier piece's entry, the round's and the report say each.
    /// </summary>
    [Fact]
    public async Task ARemovedPieceNamingOwedWork_WithdrawsIt_AndNoRoundBuildsIt()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();
        var planned = 0;
        var runner = AddingThree(checks).On("planupdate", _ => Interlocked.Increment(ref planned) == 1
            ? FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("two"),
                Item("three")))
            : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("removed", Item("three"), "the registry is not built after all")),
                Item("one"),
                Item("two"))));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("drop the registry after all", "amend-orchestrator-request", out problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var updates = UpdatesOf(harness, layout).Updates;
            Assert.Equal(
                [
                    ("u1", "added", "three", "a round added to 'items'", (string?)null, "withdrawn by update u2 before it was built"),
                    ("u2", "removed", "three", null, null, "nothing was built and nothing is undone"),
                ],
                updates.SelectMany(update => update.ThePieces.Select(piece => (update.Id, piece.Kind, piece.ItemName()!, piece.OwedTo, piece.Placed, piece.Ended))));
            Assert.Equal([("rounds.items+2", "u2"), (null, null)], updates.Select(update => (update.AddedIteration, update.AddedWithdrawnBy)));

            // The round added for the first update is taken back: its check never runs, and the round before's items
            // alone were built.
            Assert.Equal([("rounds.item-check", string.Empty)], checks);
            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Equal(
                ["rounds.items[0].build", "rounds.items[1].build"],
                steps.Where(step => step.Step == "rounds.items.build").Select(step => step.Instance).Order(StringComparer.Ordinal));
            Assert.Equal(
                (nameof(FlowInstanceState.Skipped), "round 2 of 'rounds.items', which update u1 added, was taken back by update u2 before it opened: nothing is owed it"),
                (StepOf(harness, layout, "rounds.item-check+2").State, StepOf(harness, layout, "rounds.item-check+2").SkippedBecause));
            Assert.DoesNotContain(steps, step => step.Instance == "rounds.items+2");
            Assert.Equal(nameof(FlowInstanceState.Done), StepOf(harness, layout, "rounds").State);

            _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), harness.OrchestrationStore.ReadFlowUpdates(layout)), end.Summary, 0, DateTimeOffset.UtcNow);
            Assert.Equal(
                [
                    "  - added 'three': owed to a round added to 'items', withdrawn by update u2 before it was built",
                    "  - round 2 of 'rounds.items' was added for update u1, and taken back by update u2 before it opened: nothing was owed it any more",
                    "  - removed 'three': carried by no instance, nothing was built and nothing is undone",
                ],
                File.ReadAllLines(layout.ReportFile).Where(line => line.StartsWith("  - added", StringComparison.Ordinal) || line.StartsWith("  - removed", StringComparison.Ordinal) || line.StartsWith("  - round", StringComparison.Ordinal)));
        }

        // The help and the architecture say it where they say how the pieces are placed.
        Assert.Contains(
            "a removed piece naming work no instance carries withdraws what an earlier update owes a round under that name, so no "
                + "round builds it, and that piece says it was withdrawn before it was built;",
            Prose.Words(DssHarness.Core.Help.HelpTopics.Render("orchestrators")),
            StringComparison.Ordinal);
        Assert.Contains(
            "such piece ends in its own update's entry as withdrawn by this one before it was built (`FlowCoordinator.WithdrawWhatIsOwed`), so no round builds what the plan took back.",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner of <see cref="Ended"/> or <see cref="Looped"/> whose first update adds "three", whose second withdraws
    /// it and whose third adds "four"; every check that opens a round notes in <paramref name="checks"/> the owed work it
    /// was handed.
    /// </summary>
    private static FakeRunner AddingThenWithdrawing(List<(string Instance, string Owed)> checks, JsonNode? plan = null)
    {
        var planned = 0;

        return AddingThree(checks, plan).On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Interlocked.Increment(ref planned) switch
        {
            1 => Concluded(new JsonArray(Piece("added", Item("three"), "the registry is a third work item")), Item("one"), Item("two"), Item("three")),
            2 => Concluded(new JsonArray(Piece("removed", Item("three"), "the registry is not built after all")), Item("one"), Item("two")),
            _ => Concluded(new JsonArray(Piece("added", Item("four"), "the cache is a fourth work item")), Item("one"), Item("two"), Item("four")),
        }));
    }

    /// <summary>
    /// A round an update added to a work-item group with no loop around it, whose only work a later update withdraws
    /// before it opens, is taken back and counted against no bound: with the one round amend.maxAddedIterations allows, a
    /// change given after it is taken - never refused as past the group's rounds - and is given the round, which opens
    /// through a new visit of the check and carries that change's work alone.
    /// </summary>
    [Fact]
    public async Task ARoundTakenBack_CountsAgainstNoBound_AndALaterChangeIsGivenItsRound()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, AddingThenWithdrawing(checks), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout), runName: "r1");

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("drop the registry after all", "amend-orchestrator-request", out problem), problem ?? "taken");

            // Both are planned and done while the audit asks: the round the first added is taken back by the second.
            for (var sessions = 0; UpdatesOf(harness, layout).Updates.Any(update => update.State != FlowUpdateStates.Done); sessions++)
            {
                Assert.True(sessions < 20, "the updates were never done");
                _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            }

            Assert.Equal("u2", UpdatesOf(harness, layout).Updates[0].AddedWithdrawnBy);

            // The run has been given no round: a change given now is taken, and is given the round.
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the cache as well", "amend-orchestrator-request", out problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var updates = UpdatesOf(harness, layout).Updates;
            Assert.Equal(
                [("u1", "rounds.items+2", "u2", (string?)null), ("u2", null, null, null), ("u3", "rounds.items+2", null, "rounds.items+2")],
                updates.Select(update => (update.Id, update.AddedIteration, update.AddedWithdrawnBy, update.StandingIteration)));
            Assert.Equal([("four", "rounds.items+2[0]", "done")], updates[2].ThePieces.Select(piece => (piece.ItemName()!, piece.Placed, piece.Ended)));
            Assert.Equal([("rounds.item-check", string.Empty), ("rounds.item-check+3", "four")], checks);
            Assert.Equal(
                [
                    "round 2 of 'rounds.items' was added for update u1: the run has been given 1 of the 1 amend.maxAddedIterations allows",
                    "round 2 of 'rounds.items' was added for update u3: the run has been given 1 of the 1 amend.maxAddedIterations allows",
                ],
                FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Where(written => written.Kind == "iterationAdded").Select(written => written.Detail));
            Assert.Equal(
                [("rounds.items+2[0].build", "four"), ("rounds.items[0].build", "one"), ("rounds.items[1].build", "two")],
                harness.OrchestrationStore.ReadFlow(layout)!.Steps
                    .Where(step => step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done))
                    .Select(step => (step.Instance, FlowWorkItems.NameOf(step.Item)!))
                    .OrderBy(built => built.Instance, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// An iteration an update added to a loop at its bound, whose only work a later update withdraws before the iteration
    /// is made, is taken back: the loop's bound reads as it did before it was added, so the loop asks its bound's question
    /// again - the one the added iteration had withdrawn - and, accepted, ends with no iteration run for nothing. The
    /// update's entry, the report and the listing say the iteration was taken back.
    /// </summary>
    [Fact]
    public async Task AnIterationAddedWhoseWorkIsWithdrawnBeforeItIsMade_IsTakenBack_AndTheBoundAsksAgain()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Looped());
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, AddingThenWithdrawing([], plan: Concluded(Item("one"))), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            await DriveAsync(coordinator, held: "loopBound");
            var first = coordinator.OpenQuestions.Single(asked => asked.Kind == "loopBound");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("drop the registry after all", "amend-orchestrator-request", out problem), problem ?? "taken");

            // The first update's iteration withdraws the bound's question; the second takes the iteration back, and the
            // bound asks again.
            _ = await DriveAsync(coordinator, held: "loopBound");
            var again = coordinator.OpenQuestions.Single(asked => asked.Kind == "loopBound");
            Assert.NotEqual(first.Id, again.Id);
            Assert.Equal(
                [("rounds#2", "u2", (string?)null), (null, null, null)],
                UpdatesOf(harness, layout).Updates.Select(update => (update.AddedIteration, update.AddedWithdrawnBy, update.StandingIteration)));

            Assert.True(coordinator.TryAnswer(again.Id, "accept", null, out _));
            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            Assert.DoesNotContain(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance.StartsWith("rounds#2", StringComparison.Ordinal));
            Assert.Equal(
                [(first.Id, "withdrawn"), (again.Id, "answered")],
                harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions.Where(asked => asked.Kind == "loopBound").Select(asked => (asked.Id, asked.State)));

            _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), harness.OrchestrationStore.ReadFlowUpdates(layout)), end.Summary, 0, DateTimeOffset.UtcNow);
            Assert.Contains("  - round 2 of 'rounds' was added for update u1, and taken back by update u2 before it opened: nothing was owed it any more", File.ReadAllLines(layout.ReportFile));

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

            Assert.Equal(
                [
                    "  plan update u1 (amendment): done",
                    "    round 2 of 'rounds' was added for update u1, and taken back by update u2 before it opened: nothing was owed it any more",
                    "  plan update u2 (amendment): done",
                ],
                UpdateLinesOf(listed));
        }
    }

    /// <summary>
    /// A crash once the entry of the update whose round was taken back says so, before the check drawn again for that round
    /// is ended, is gone on from by the resume: the round is closed again from the entry - the check ends skipped and never
    /// runs, the round is never made - and a record written before rounds were taken back reads as it did.
    /// </summary>
    [Fact]
    public async Task ACrashOnceARoundIsTakenBack_BeforeItIsClosed_IsClosedByTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Ended());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.AddedWithdrawnBy is not null));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, AddingThenWithdrawing([]), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: crashing, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("drop the registry after all", "amend-orchestrator-request", out problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(crashing.Ended);
        }

        // On disk: the round taken back in the first update's entry, and the check drawn for it not ended.
        Assert.Equal("u2", UpdatesOf(harness, layout).Updates[0].AddedWithdrawnBy);
        Assert.Equal(nameof(FlowInstanceState.NotStarted), StepOf(harness, layout, "rounds.item-check+2").State);

        var (_, _, again) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();

        using (again)
        {
            var resumed = TestAdvice.Coordinator(definition, AddingThenWithdrawing(checks), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            var end = await DriveAsync(resumed);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Empty(checks);
            Assert.Equal(nameof(FlowInstanceState.Skipped), StepOf(harness, layout, "rounds.item-check+2").State);
            Assert.DoesNotContain(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance == "rounds.items+2");
        }

        // An entry written before rounds were taken back reads as one whose round stands.
        var older = System.Text.Json.JsonSerializer.Deserialize<FlowUpdateEntry>("""{"id":"u1","cause":"amendment","state":"done","takenAt":"2026-09-30 16:32:14","amendment":"add it","addedIteration":"rounds.items+2"}""", DssHarness.Core.FileSystem.JsonStateFile.Options)!;
        Assert.Equal(((string?)null, "rounds.items+2"), (older.AddedWithdrawnBy, older.StandingIteration));
    }

    /// <summary>
    /// The runner of <see cref="AuditedLater"/>: the plan holds "one" and "two"; the first update adds "three", owed to
    /// the loop's second iteration, and the second withdraws it - or, <paramref name="changed"/>, does it differently; the
    /// second iteration's round holds only the work owed it; and every check that opens a round notes in
    /// <paramref name="checks"/> the owed work it was handed.
    /// </summary>
    private static FakeRunner OwingThenChanging(List<(string Instance, string Owed)> checks, bool changed)
    {
        var planned = 0;
        var again = Item("three");
        again["title"] = "The registry, done differently";

        return AddingThree(checks)
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Interlocked.Increment(ref planned) == 1
                ? Concluded(new JsonArray(Piece("added", Item("three"), "the registry is a third work item")), Item("one"), Item("two"), Item("three"))
                : changed
                    ? Concluded(new JsonArray(Piece("changed", again.DeepClone(), "the registry is done differently")), Item("one"), Item("two"), again.DeepClone())
                    : Concluded(new JsonArray(Piece("removed", Item("three"), "the registry is not built after all")), Item("one"), Item("two"))))
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
                    ["items"] = run.Instance.StartsWith("rounds#1.", StringComparison.Ordinal) ? UnionOf(run) : owed.DeepClone(),
                });
            });
    }

    /// <summary>
    /// Drives a run of <see cref="AuditedLater"/> to the audit of its second round: through the first round and its review,
    /// where an amendment adds work owed to the second iteration, and that iteration's check, which joins it - the round's
    /// group not expanded while the audit asks. Answers every other question to go on.
    /// </summary>
    private static async Task<FlowQuestion> UntilTheSecondRoundsAuditAsync(FlowCoordinator coordinator)
    {
        _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
        Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
        var review = await UntilAskedAsync(coordinator, "reviewed?");

        Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
        Assert.True(coordinator.TryAnswer(review.Id, "go", null, out _));
        return await UntilAskedAsync(coordinator, "audited?");
    }

    /// <summary>
    /// An update done that withdraws or changes work the check that opens a round has joined to it, before the round's
    /// work-item group expands, draws that check again - with every step of its scope after it, the audit that waits
    /// between it and the group among them, its question withdrawn - so the round opens with what the updates owe it now:
    /// a withdrawn item is never built, and a changed one is built as its newest piece says, never as the piece it
    /// replaced. The update's entry and the report name the check drawn again.
    /// </summary>
    /// <param name="changed">Whether the second update does the owed work differently, rather than withdraw it.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUpdateChangingWorkACheckJoinedToARoundNotExpanded_DrawsTheCheckAgain(bool changed)
    {
        using var temp = new TempDirectory();
        var definition = Parse(AuditedLater());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, OwingThenChanging(checks, changed), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await UntilTheSecondRoundsAuditAsync(coordinator);
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver(changed ? "do the registry differently" : "drop the registry after all", "amend-orchestrator-request", out var problem), problem ?? "taken");

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var updates = UpdatesOf(harness, layout).Updates;
            Assert.Equal(((string?)null, "rounds#2.item-check"), (updates[0].CheckedAgain, updates[1].CheckedAgain));

            // The work joined the round of the iteration under way, whose check had yet to open it: no iteration was added.
            Assert.Equal([(string?)null, null], updates.Select(update => update.AddedIteration));
            Assert.Equal(
                changed
                    ? [("u1", "added", "the next iteration of 'rounds'", (string?)null, "carried on by update u2, whose piece names the same work item"), ("u2", "changed", "the next iteration of 'rounds'", "rounds#2.items[0]", "done")]
                    : [("u1", "added", "the next iteration of 'rounds'", null, "withdrawn by update u2 before it was built"), ("u2", "removed", null, null, "nothing was built and nothing is undone")],
                updates.SelectMany(update => update.ThePieces.Select(piece => (update.Id, piece.Kind, piece.OwedTo, piece.Placed, piece.Ended))));

            // The check ran again on what the updates owe the round now, and the audit between it and the group with it;
            // the changed item is built by the step that carries changed work.
            Assert.Equal([("rounds#1.item-check", string.Empty), ("rounds#2.item-check", "three"), ("rounds#2.item-check+2", changed ? "three" : string.Empty)], checks);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);
            Assert.Equal((nameof(FlowInstanceState.Skipped), nameof(FlowInstanceState.Done)), (steps["rounds#2.item-audit"].State, steps["rounds#2.item-audit+2"].State));
            Assert.Equal(
                changed
                    ? [("rounds#1.items[0].build", "The one item"), ("rounds#1.items[1].build", "The two item"), ("rounds#2.items[0].build", "The registry, done differently")]
                    : [("rounds#1.items[0].build", "The one item"), ("rounds#1.items[1].build", "The two item")],
                steps.Values
                    .Where(step => step.Step is "rounds.items.build" or "rounds.items.rebuild" && step.State == nameof(FlowInstanceState.Done))
                    .Select(step => (step.Instance, (step.Item as JsonObject)?["title"]?.GetValue<string>()))
                    .OrderBy(built => built.Instance, StringComparer.Ordinal));

            _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), harness.OrchestrationStore.ReadFlowUpdates(layout)), end.Summary, 0, DateTimeOffset.UtcNow);
            Assert.Contains(
                "  - 'rounds#2.item-check' is drawn again for update u2: the work updates owe its round, which it had joined, changed before the round opened",
                File.ReadAllLines(layout.ReportFile));
        }
    }

    /// <summary>
    /// A crash once the update's entry names the check it draws again, before the redirect that draws it is written, is
    /// gone on from by the resume: the session that takes the run up draws the check again from the entry, and the
    /// withdrawn item is never built.
    /// </summary>
    [Fact]
    public async Task ACrashOnceAnUpdateNamesTheCheckItDrawsAgain_IsGoneOnFromByTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse(AuditedLater());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.CheckedAgain is not null));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, OwingThenChanging([], changed: false), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: crashing, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await UntilTheSecondRoundsAuditAsync(coordinator);
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("drop the registry after all", "amend-orchestrator-request", out var problem), problem ?? "taken");
            _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            Assert.True(crashing.Ended);
        }

        // On disk: the entry names the check, and the check stands as it ran, its audit still asking.
        Assert.Equal("rounds#2.item-check", UpdatesOf(harness, layout).Updates[1].CheckedAgain);
        Assert.DoesNotContain(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance == "rounds#2.item-check+2");

        var (_, _, again) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();

        using (again)
        {
            var resumed = TestAdvice.Coordinator(definition, OwingThenChanging(checks, changed: false), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            var end = await DriveAsync(resumed);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal([("rounds#2.item-check+2", string.Empty)], checks);
            Assert.DoesNotContain(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance.StartsWith("rounds#2.items[", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The runner of <see cref="Reviewed"/> bounded at two iterations whose second round's own list - what the round before
    /// wrote for it - holds "q" and "r", and whose check that opens it finds "q" wanting on its first visit, raising a plan
    /// update; the planner answers it with <paramref name="scope"/>. Every check that opens a round notes in
    /// <paramref name="checks"/> the names it was handed as withdrawn, and every check what it was handed as the names of a
    /// round not opened (<c>opening</c>), in <paramref name="opening"/>.
    /// </summary>
    private static FakeRunner FindingQWanting(List<(string Instance, string Withdrawn)> checks, List<(string Instance, string Opening)> opening, JsonArray scope)
    {
        static string NamesOf(FlowStepRun run, string key)
            => run.Read!.TryGetValue(key, out var given) && !given.IsMissing
                ? $"[{string.Join(", ", given.ToNode()!.AsArray().Select(name => name!.GetValue<string>()))}]"
                : "none";

        return AddingThree([])
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded((JsonArray)scope.DeepClone(), Item("one"), Item("two"))))
            .On("check", run =>
            {
                lock (opening)
                {
                    opening.Add((run.Instance, NamesOf(run, "opening")));
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["ok"] = true, ["problems"] = new JsonArray(), ["items"] = UnionOf(run) });
            })
            .On("item-check", run =>
            {
                lock (checks)
                {
                    checks.Add((run.Instance, NamesOf(run, "withdrawn")));
                }

                lock (opening)
                {
                    opening.Add((run.Instance, NamesOf(run, "opening")));
                }

                var first = run.Instance.StartsWith("rounds#1.", StringComparison.Ordinal);
                var wanting = run.Instance == "rounds#2.item-check";
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
                {
                    ["ok"] = !wanting,
                    ["problems"] = wanting ? new JsonArray("work item 'q' reuses 'src/old.cs', which is not in the main tree: a plan builds on what is there") : new JsonArray(),
                    ["items"] = first ? UnionOf(run) : new JsonArray(Item("q"), Item("r")),
                });
            });
    }

    /// <summary>
    /// A removed piece naming an item of the list a round is opening with - which no instance carries, for the round has
    /// not opened - takes it out of that list: raised by the check that opens the second round, which found the item the
    /// round before wrote for it wanting, the update's piece is owed to that round as a withdrawal; the check that runs
    /// again on its own items, which still hold the item, is handed it as withdrawn, the round never expands it and nothing
    /// undoes it, and the piece ends as the round opens without it - in its entry, the report and the listing.
    /// </summary>
    [Fact]
    public async Task ARemovedPieceNamingAnItemOfTheListARoundOpensWith_TakesItOutOfThatList()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Reviewed().Replace("maxIterations: 3", "maxIterations: 2", StringComparison.Ordinal));
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Withdrawn)>();

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, FindingQWanting(checks, [], new JsonArray(Piece("removed", Item("q"), "the item is not built: the file it reuses is gone"))), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal(FlowUpdateCauses.PlanCheck, update.Cause);
            Assert.Equal(
                [("removed", "q", "the next iteration of 'rounds'", (bool?)true, (string?)null, "'rounds#2.items' opened without it: nothing was built and nothing is undone")],
                update.ThePieces.Select(piece => (piece.Kind, piece.ItemName()!, piece.OwedTo, piece.Withdrawal, piece.Placed, piece.Ended)));
            Assert.Equal([("rounds#1.item-check", "[]"), ("rounds#2.item-check", "[]"), ("rounds#2.item-check+2", "[q]")], checks);

            var steps = harness.OrchestrationStore.ReadFlow(layout)!.Steps;
            Assert.Equal(
                [("rounds#1.items[0].build", "one"), ("rounds#1.items[1].build", "two"), ("rounds#2.items[0].build", "r")],
                steps.Where(step => step.Step == "rounds.items.build" && step.State == nameof(FlowInstanceState.Done))
                    .Select(step => (step.Instance, FlowWorkItems.NameOf(step.Item)!))
                    .OrderBy(built => built.Instance, StringComparer.Ordinal));
            Assert.DoesNotContain(steps, step => step.Step == "rounds.items.withdraw" && step.State == nameof(FlowInstanceState.Done));

            _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), harness.OrchestrationStore.ReadFlowUpdates(layout)), end.Summary, 0, DateTimeOffset.UtcNow);
            Assert.Contains(
                "  - removed 'q': taken out of the work items of the next iteration of 'rounds', 'rounds#2.items' opened without it: nothing was built and nothing is undone",
                File.ReadAllLines(layout.ReportFile));
        }

        // The help and the architecture say it where they say how a removed piece no instance carries is placed.
        Assert.Contains(
            "one naming an item of the list the check that opens a round was opening it with takes it out of that list, and the round opens without it.",
            Prose.Words(DssHarness.Core.Help.HelpTopics.Render("orchestrators")),
            StringComparison.Ordinal);
        Assert.Contains(
            "takes it out of that list: the piece is owed to that round as a withdrawal (`FlowUpdatePiece.Withdrawal`",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A crash once the update that takes an item out of the list a round opens with is done, before the check that raised
    /// it runs again, is gone on from by the resume: the withdrawal is read back from the update's entry, the check is
    /// handed it, and the round never expands the item. An entry written before withdrawals were kept reads as it did.
    /// </summary>
    [Fact]
    public async Task ACrashOnceAWithdrawalIsOwedARound_IsCarriedByTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Reviewed().Replace("maxIterations: 3", "maxIterations: 2", StringComparison.Ordinal));
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var crashing = new CrashingRecords(records, updates => updates.Updates.Any(update => update.State == FlowUpdateStates.Done));
        var scope = new JsonArray(Piece("removed", Item("q"), "the item is not built: the file it reuses is gone"));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, FindingQWanting([], [], scope), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: crashing, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            for (var sessions = 0; !crashing.Ended; sessions++)
            {
                Assert.True(sessions < 20, "the run never reached its crash");
                _ = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

                foreach (var asked in coordinator.OpenQuestions.ToList())
                {
                    Assert.True(coordinator.TryAnswer(asked.Id, "go", null, out _), asked.Problem);
                }
            }
        }

        // On disk: the withdrawal owed, and the check that raised the update still waiting on it.
        Assert.True(UpdatesOf(harness, layout).Updates.Single().ThePieces.Single().IsWithdrawing);
        Assert.Equal(nameof(FlowInstanceState.Paused), StepOf(harness, layout, "rounds#2.item-check").State);

        var (_, _, again) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Withdrawn)>();

        using (again)
        {
            var resumed = TestAdvice.Coordinator(definition, FindingQWanting(checks, [], scope), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: again, resume: new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(definition), updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            var end = await DriveAsync(resumed);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
            Assert.Equal([("rounds#2.item-check+2", "[q]")], checks);
            Assert.Equal(
                ["r"],
                harness.OrchestrationStore.ReadFlow(layout)!.Steps
                    .Where(step => step.Step == "rounds.items.build" && step.Instance.StartsWith("rounds#2.", StringComparison.Ordinal))
                    .Select(step => FlowWorkItems.NameOf(step.Item)!));
        }

        // A piece written before withdrawals were kept reads as work owed, as it always did.
        var older = System.Text.Json.JsonSerializer.Deserialize<FlowUpdatePiece>("""{"kind":"removed","item":{"name":"q"},"changed":"it goes","owedTo":"the next iteration of 'rounds'"}""", DssHarness.Core.FileSystem.JsonStateFile.Options)!;
        Assert.Equal(((bool?)null, true, false), (older.Withdrawal, older.IsOwed, older.IsWithdrawing));
    }

    /// <summary>
    /// The update the check that opens a round raises joins a round that has not opened, and every check made while it is
    /// under way - its own planning's check above all, at the step's start and at the planner's answer alike - is handed
    /// the names an earlier round carries that the round's list may not take (<c>opening</c>), so it holds that list
    /// whether or not the update answers a piece. No other check is handed them.
    /// </summary>
    [Fact]
    public async Task TheChecksOfAnUpdateTheCheckThatOpensARoundRaised_AreHandedThatRoundNotOpened()
    {
        using var temp = new TempDirectory();
        var definition = Parse(Reviewed().Replace("maxIterations: 3", "maxIterations: 2", StringComparison.Ordinal));
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var opening = new List<(string Instance, string Opening)>();

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, FindingQWanting([], opening, []), new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
        }

        Assert.Equal(
            [
                ("planning.check", "none"),
                ("rounds#1.item-check", "none"),
                ("rounds#2.item-check", "none"),
                ("planning.check+2", "[one, two]"),
                ("rounds#2.item-check+2", "none"),
            ],
            opening);
    }

    /// <summary>
    /// The flow of <see cref="Looped"/> - a loop of one iteration whose until reads false - whose work-item group passes a
    /// failure over, so a round whose work item failed ends failed and the loop goes on, with an audit after the loop, so
    /// the run goes on asking once the loop has ended.
    /// </summary>
    private static string BoundThenAudit()
        => Looped().Replace(
            "      - name: items\n        dependsOn: [item-check]\n",
            "      - name: items\n        dependsOn: [item-check]\n        onFailure: continue\n",
            StringComparison.Ordinal)
            + "\n  - name: audit\n    type: pause\n    question: anything else?\n    options: [stay, go]\n    dependsOn: [rounds]";

    /// <summary>
    /// Work an update owes a loop at its bound, where the run has been given every iteration <c>amend.maxAddedIterations</c>
    /// allows, is left to the question the bound asks, and the update's entry says so once: the person accepting the bound
    /// ends the loop for good - no later session runs it again for that work, nor asks its bound again - and the report
    /// and the listing say the bound was asked for it.
    /// </summary>
    [Fact]
    public async Task WorkOwedToALoopAtItsBound_WithNoIterationLeftToAdd_IsLeftToTheBoundsAnswer_ForEverySessionAfter()
    {
        using var temp = new TempDirectory();
        var parsed = Parse(BoundThenAudit());
        var definition = parsed with { Amend = parsed.Amend! with { MaxAddedIterations = 0 } };
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var built = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var planned = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The round's one item fails, held until the update is under way: the round ends failed under the update, its
        // failure passed over, and the loop is at its bound.
        var runner = AddingThree([], plan: Concluded(Item("one")))
            .OnAsync("planupdate", (_, token) => planned.Task.WaitAsync(token))
            .OnAsync("build", (_, token) => built.Task.WaitAsync(token));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The amendment is taken while the round's item is under way, and its plan stands once the round has ended:
            // its work is owed to the loop's next iteration, at the loop's bound, with no iteration left to add.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds#1.items[0].build").WaitAsync(Bound, Token);
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            built.TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed));
            await runner.StartedAsync("planning.planupdate").WaitAsync(Bound, Token);
            await EndedAsync(harness, layout, "rounds#1.items", nameof(FlowInstanceState.Failed));
            planned.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("three"))));
            _ = await session.WaitAsync(Bound, Token);

            Assert.Equal("loopBound", coordinator.OpenQuestions.Single().Kind);

            // The bound is accepted, the audit answered, and the run ends: the loop is not run again for the work.
            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal((FlowUpdateStates.Done, (string?)null, "rounds"), (update.State, update.AddedIteration, update.AskedAtBound));
            Assert.Single(harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions, asked => asked.Kind == "loopBound");

            var events = FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events;
            Assert.Equal(
                ["update u1 is owed work of 'rounds' and the run has been given the 0 iteration(s) amend.maxAddedIterations allows: its loop asks at its bound"],
                events.Where(written => written.Detail?.Contains("asks at its bound", StringComparison.Ordinal) == true).Select(written => written.Detail));

            // The report and the listing say the bound was asked for the work, which no iteration carried.
            _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowQuestions(layout), harness.OrchestrationStore.ReadFlowUpdates(layout)), end.Summary, 0, DateTimeOffset.UtcNow);
            Assert.Equal(AskedAtBoundReport, UpdatesSectionOf(File.ReadAllText(layout.ReportFile)));

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

            Assert.Equal(AskedAtBoundListing, UpdateLinesOf(listed));
        }
    }

    /// <summary>What the report says of the update whose work was left to its loop's bound.</summary>
    private static readonly List<string> AskedAtBoundReport =
    [
        "- u1 (amendment), taken at 2026-09-30 16:32:14: done",
        "  > add the registry too",
        "  - added 'three': owed to the next iteration of 'rounds', still under way",
        "  - 'rounds' asked at its bound for the work update u1 owes it: the run had been given every iteration amend.maxAddedIterations allows",
    ];

    /// <summary>What list-orchestrator says of that update.</summary>
    private static readonly List<string> AskedAtBoundListing =
    [
        "  plan update u1 (amendment): done",
        "    piece added 'three' is owed to the next iteration of 'rounds'",
        "    'rounds' asked at its bound for the work update u1 owes it: the run had been given every iteration amend.maxAddedIterations allows",
    ];

    /// <summary>
    /// Work an update owes a loop at its own bound, where the run has no iteration left to add, is left to the question
    /// its bound asks, and the loop reaches that question whatever its until says: a true until ends no loop that owes
    /// work, the update's entry says the bound was asked only once the question is written, the question is recommended
    /// on before a person is left with it, and the iteration an answer of more gives carries the work.
    /// </summary>
    [Fact]
    public async Task WorkOwedToALoopAtItsBound_WhoseUntilReadsTrue_IsAskedFor_AndCarriedByTheIterationAnAnswerGives()
    {
        using var temp = new TempDirectory();
        var parsed = Parse(BoundThenAudit().Replace("until: 'false'", "until: 'true'", StringComparison.Ordinal));
        var definition = parsed with { Amend = parsed.Amend! with { MaxAddedIterations = 0 } };
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var built = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var planned = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The first round's one item fails, held until the update is under way: the round ends failed under the update,
        // its failure passed over, and the loop is at its bound with its until true. Every later build is done.
        var runner = AddingThree([], plan: Concluded(Item("one")))
            .OnAsync("planupdate", (_, token) => planned.Task.WaitAsync(token))
            .OnAsync("build", (run, token) => run.Instance.StartsWith("rounds#1.", StringComparison.Ordinal)
                ? built.Task.WaitAsync(token)
                : Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("rounds#1.items[0].build").WaitAsync(Bound, Token);
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            built.TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed));
            await runner.StartedAsync("planning.planupdate").WaitAsync(Bound, Token);
            await EndedAsync(harness, layout, "rounds#1.items", nameof(FlowInstanceState.Failed));
            planned.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                Item("one"),
                Item("three"))));
            _ = await session.WaitAsync(Bound, Token);

            // The loop asks at its bound, its true until notwithstanding, and only now does the entry say so; the
            // question was put to the advisor before the session left it with a person, as every question is.
            var bound = coordinator.OpenQuestions.Single();
            Assert.Equal(("loopBound", "rounds"), (bound.Kind, UpdatesOf(harness, layout).Updates.Single().AskedAtBound));
            Assert.Equal(new FlowRecommendation("notObtained", Why: $"no recommendation: one was sought and not obtained ({TestAdvice.NotObtained})"), bound.Recommendation);

            // More is answered, and the iteration it gives carries the work.
            Assert.True(coordinator.TryAnswer(bound.Id, "more", null, out _));
            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var update = UpdatesOf(harness, layout).Updates.Single();
            Assert.Equal(("rounds", (string?)null), (update.AskedAtBound, update.AddedIteration));
            Assert.Equal([("three", "rounds#2.items[1]", "done")], update.ThePieces.Select(piece => (piece.ItemName()!, piece.Placed, piece.Ended)));
            Assert.Equal(nameof(FlowInstanceState.Done), harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds#2.items[1].build").State);
            Assert.Single(harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions, asked => asked.Kind == "loopBound");
            Assert.Equal(
                ["update u1 is owed work of 'rounds' and the run has been given the 0 iteration(s) amend.maxAddedIterations allows: its loop asks at its bound"],
                FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events.Where(written => written.Detail?.Contains("asks at its bound", StringComparison.Ordinal) == true).Select(written => written.Detail));
        }
    }

    /// <summary>
    /// A round an update added to a work-item group with no loop around it opens with the work its check was handed:
    /// where that check finds a problem it raises a plan update and waits, and the check that runs again on the list as
    /// it now is fails once more, the flow's own reaction takes it, the pause asking a person. While either stands -
    /// the update under way, the round's check run and waiting for it, or the pause asking - a change is refused,
    /// saying why and that it joins the round once the round has opened - never taken to be owed to a round that opens
    /// without it - and given then, it joins the open round and is carried out.
    /// </summary>
    [Fact]
    public async Task AChangeGivenOnceTheAddedRoundsCheckHasRun_IsRefusedUntilTheRoundOpens_AndJoinsItThen()
    {
        using var temp = new TempDirectory();
        var definition = Parse(ThroughAPause());
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var checks = new List<(string Instance, string Owed)>();
        var built = new List<string>();
        var planned = 0;
        var settling = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The check of the added round finds a problem, and the one that runs again on the list as it then is fails
        // once more, so the pause between the check and the group asks. The settle of the raised update is held, that
        // a change given while it is under way has a window to be queued in.
        var runner = AddingThree(checks)
            .OnAsync("planupdate", async (run, cancellationToken) =>
            {
                var which = Interlocked.Increment(ref planned);

                if (which == 2)
                {
                    _ = await settling.Task.WaitAsync(cancellationToken);
                }

                return which switch
                {
                    1 => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                        new JsonArray(Piece("added", Item("three"), "the registry is a third work item")),
                        Item("one"),
                        Item("two"),
                        Item("three"))),
                    2 => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                        new JsonArray(Piece("added", Item("four"), "the cache is a fourth work item")),
                        Item("one"),
                        Item("two"),
                        Item("three"),
                        Item("four"))),
                    _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(
                        new JsonArray(Piece("added", Item("five"), "the listing is a fifth work item")),
                        Item("one"),
                        Item("two"),
                        Item("three"),
                        Item("four"),
                        Item("five"))),
                };
            })
            .On("item-check", run =>
            {
                var owed = run.Read!.TryGetValue("owed", out var owing) && !owing!.IsMissing ? (owing.ToNode() as JsonArray)! : new JsonArray();

                lock (checks)
                {
                    checks.Add((run.Instance, string.Join(",", owed.Select(FlowWorkItems.NameOf))));
                }

                var ok = run.Instance is not ("rounds.item-check+2" or "rounds.item-check+3");
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
                {
                    ["ok"] = ok,
                    ["problems"] = ok ? new JsonArray() : new JsonArray("one problem"),
                    ["items"] = UnionOf(run),
                });
            })
            .On("build", run =>
            {
                lock (built)
                {
                    built.Add($"{run.Instance}={FlowWorkItems.NameOf(run.Item.ToNode())}");
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject());
            });

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new ManualClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout), runName: "r1");

            // The first amendment adds a round, whose check runs on its work and finds a problem: it raises a plan
            // update, and the planner settles it - held here, that a change has its window.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the registry too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));

            var driving = coordinator.RunAsync(Token);

            for (var at = 0; Volatile.Read(ref planned) < 2; at++)
            {
                if (at > 3000)
                {
                    throw new TimeoutException("the settle of the raised update never began");
                }

                await Task.Delay(10, Token);
            }

            // While the raised update is under way - the round's check has run, and waits for it - a second amendment
            // is refused, by the engine and by the command's gates alike, saying it joins the round once it has opened.
            Assert.Equal(FlowHandedOver.Refused, coordinator.AmendmentHandedOver("add the cache as well", "amend-orchestrator-request", out problem));
            Assert.Equal(CheckedRoundRefusal, problem);
            Assert.Equal(CheckedRoundRefusal, FlowUpdateGates.Refused(definition, harness.OrchestrationStore.ReadFlow(layout)!, harness.OrchestrationStore.ReadFlowUpdates(layout), FlowUpdateCauses.Amendment));
            settling.SetResult(true);
            _ = await driving;

            // The check runs again on the list as it now is, fails once more, and the flow's own reaction takes it:
            // the pause asks a person.
            var problems = await UntilAskedAsync(coordinator, "problems?");

            // Once the round has opened, the same amendment is taken and joins it.
            Assert.True(coordinator.TryAnswer(problems.Id, "continue", null, out _));
            _ = await UntilAskedAsync(coordinator, "added|the registry");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("add the cache as well", "amend-orchestrator-request", out problem), problem ?? "taken");

            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);

            var updates = UpdatesOf(harness, layout).Updates;
            Assert.Equal(
                [("u1", FlowUpdateStates.Done, "rounds.items+2"), ("u2", FlowUpdateStates.Done, null), ("u3", FlowUpdateStates.Done, null)],
                updates.Select(update => (update.Id, update.State, update.AddedIteration)));
            Assert.Equal(
                [("three", "rounds.items+2[0]", "done"), ("four", "rounds.items+2[1]", "done"), ("five", "rounds.items+2[2]", "done")],
                updates.SelectMany(update => update.ThePieces).Select(piece => (piece.ItemName()!, piece.Placed, piece.Ended)));
            Assert.Equal(
                ["rounds.items+2[0].build=three", "rounds.items+2[1].build=four", "rounds.items+2[2].build=five"],
                built.Where(one => one.StartsWith("rounds.items+2", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            Assert.Equal([("rounds.item-check", string.Empty), ("rounds.item-check+2", "three"), ("rounds.item-check+3", "three,four")], checks);
        }
    }

    /// <summary>What a change given once the check of the round an update added has run is refused with.</summary>
    private const string CheckedRoundRefusal =
        "run 'r1' has checked the work of round 2 of 'rounds.items', which update u1 added, and that round opens with that work alone: nothing would carry the change out before it has opened. Give the request again once it has opened, and it joins it";

    /// <summary>The report's plan-update section, line by line: every line under its heading, to the blank line that ends it.</summary>
    private static List<string> UpdatesSectionOf(string report)
        => [.. report.ReplaceLineEndings("\n").Split('\n').SkipWhile(line => line != "## Plan updates").Skip(1).TakeWhile(line => line.Length > 0)];

    /// <summary>The listing's lines of the run's plan updates: from the first update's line to the last line the run's entry holds.</summary>
    private static List<string> UpdateLinesOf(CommandOutcome listed)
        => [.. (listed.Details ?? []).SkipWhile(line => !line.StartsWith("  plan update ", StringComparison.Ordinal))];

    /// <summary>What the report says of the update that added an iteration to the loop at its bound.</summary>
    private static readonly List<string> LoopReport =
    [
        "- u1 (amendment), taken at 2026-09-30 16:32:14: done",
        "  > add the registry too",
        "  - added 'three': carried out by 'rounds#2.items[1].build', done",
        "  - round 2 of 'rounds' was added for update u1",
    ];

    /// <summary>What the report says of the update that added a round to the group with no loop around it.</summary>
    private static readonly List<string> GroupReport =
    [
        "- u1 (amendment), taken at 2026-09-30 16:32:14: done",
        "  > add the registry too",
        "  - added 'three': carried out by 'rounds.items+2[0].build', done",
        "  - round 2 of 'rounds.items' was added for update u1",
    ];

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

    /// <summary>Waits, bounded as a session is, until the instance <paramref name="instance"/> stands ended in <paramref name="state"/> in the run's records.</summary>
    private static async Task EndedAsync(HarnessFactory harness, OrchestratorLayout layout, string instance, string state)
    {
        for (var at = 0; ; at++)
        {
            if (harness.OrchestrationStore.ReadFlow(layout)?.Steps.FirstOrDefault(step => step.Instance == instance) is { State: var held } && held == state)
            {
                return;
            }

            if (at > 3000)
            {
                throw new TimeoutException($"'{instance}' never ended {state}");
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
    /// A loop that ended leaving something is put in progress again by the step that plans an update in its last
    /// iteration, and holds nothing of the end that undid while it runs: no leftover, no time it finished - so the next
    /// end says what it leaves and when, never what the first said.
    /// </summary>
    [Fact]
    public async Task ALoopInProgressAgainForThePlanningOfAnUpdate_HoldsNothingOfTheEndItUndid()
    {
        using var temp = new TempDirectory();
        var flow = Planned.ReplaceLineEndings("\n");
        const string Repeat = "      until: 'true'\n      maxIterations: 2\n";
        Assert.Contains(Repeat, flow, StringComparison.Ordinal);
        var definition = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            flow.Replace(Repeat, "      until: 'false'\n      maxIterations: 1\n      leftover: 'true'\n", StringComparison.Ordinal)
                + "\n  - name: audit\n    type: pause\n    question: anything else?\n    options: [stay, go]\n    dependsOn: [rounds]") with
        {
            Amend = new FlowAmend("request", 5, null, "rounds.planupdate", null, "rounds.plan.latest.scope", "rounds.items.build", "rounds.items.rebuild", "rounds.items.withdraw", null),
        };
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }));
        var planning = runner.Hold("planupdate");

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The round runs, the loop's bound is accepted with its until false, and it ends leaving what its leftover
            // reads; the audit after it asks, and the amendment is taken there.
            _ = await coordinator.RunAsync(Token);
            Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", null, out _));
            var audit = await UntilAskedAsync(coordinator, "anything else?");
            var ended = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds");
            Assert.Equal((nameof(FlowInstanceState.Done), "true", true), (ended.State, ended.Leftover?.ToJsonString(), ended.FinishedAt is not null));

            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("build the parser too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            var session = Task.Run(() => coordinator.RunAsync(Token), Token);
            await Task.WhenAny(runner.StartedAsync("rounds#1.planupdate"), session).Unwrap().WaitAsync(TimeSpan.FromSeconds(30), Token);

            // The planner runs in the loop's last iteration, and the loop is in progress again with nothing of its end.
            var again = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds");
            Assert.Equal((nameof(FlowInstanceState.Running), null, null), (again.State, again.Leftover?.ToJsonString(), again.FinishedAt));
            Assert.Null(coordinator.Instances["rounds"].Leftover);

            _ = planning.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))));
            _ = await session.WaitAsync(TimeSpan.FromSeconds(30), Token);
            Assert.True(coordinator.TryAnswer(audit.Id, "go", null, out _));
            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
        }
    }

    /// <summary>
    /// A loop whose own bound counts from the iteration a plan update is planned in leaves out the iterations granted
    /// before that count began - and no more of them than stand: where the second update, planned with the first's
    /// added iteration granted, withdraws its work and takes it back, the count names that iteration
    /// (<see cref="FlowLoopCount.AddedBy"/>) and leaves it out no more, so the bound reads as the loop's own and a
    /// person's more at it is one more iteration, run - never a bound below the loop's own that asks again at once.
    /// </summary>
    [Fact]
    public async Task AnIterationTakenBackThatALoopsCountLeftOut_IsLeftOutNoMore_AndAPersonsMoreRunsOne()
    {
        using var temp = new TempDirectory();
        var flow = Planned.ReplaceLineEndings("\n");
        const string Repeat = "      until: 'true'\n      maxIterations: 2\n";
        Assert.Contains(Repeat, flow, StringComparison.Ordinal);
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", flow.Replace(Repeat, "      until: 'false'\n      maxIterations: 1\n", StringComparison.Ordinal)) with
        {
            Amend = new FlowAmend("request", 5, null, "rounds.planupdate", null, "rounds.plan.latest.scope", "rounds.items.build", "rounds.items.rebuild", "rounds.items.withdraw", null),
        };
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var planned = 0;

        var runner = new FakeRunner()
            .On("plan", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Concluded(Item("one"))))
            .On("planupdate", _ => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Interlocked.Increment(ref planned) == 1
                ? Concluded(new JsonArray(Piece("added", Item("two"), "the parser is a second work item")), Item("one"), Item("two"))
                : Concluded(new JsonArray(Piece("removed", Item("two"), "the parser is not built after all")), Item("one"))))
            .On("check", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject
            {
                ["ok"] = true,
                ["problems"] = new JsonArray(),
                ["items"] = UnionOf(run),
            }));

        using (records)
        {
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

            // The loop's one iteration ends at its bound; both updates are handed while it asks, the second queued.
            _ = await UntilAskedAsync(coordinator, "'rounds' has run");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("build the parser too", "amend-orchestrator-request", out var problem), problem ?? "taken");
            Assert.True(FlowHandedOver.Taken == coordinator.AmendmentHandedOver("drop the parser after all", "amend-orchestrator-request", out problem), problem ?? "taken");
            _ = await DriveAsync(coordinator, held: "loopBound");

            // The first update's iteration was granted when the second's count began, and taken back by the second.
            Assert.Equal(
                [("u1", "rounds#2", "u2"), ("u2", null, null)],
                UpdatesOf(harness, layout).Updates.Select(update => (update.Id, update.AddedIteration, update.AddedWithdrawnBy)));
            var counted = harness.OrchestrationStore.ReadFlow(layout)!.Steps.Single(step => step.Instance == "rounds").Counted!;
            Assert.Equal(("u2", 0, 1, "u1"), (counted.Update, counted.Before, counted.Granted, string.Join(",", counted.AddedBy ?? [])));

            // The bound is the loop's own again: it asks, and a person's more runs one more iteration.
            var bound = coordinator.OpenQuestions.Single(asked => asked.Kind == "loopBound");
            Assert.Equal("'rounds' has run 1 iterations since plan update u2 was planned in it and its until still reads false; at most 1 are allowed (maxIterations)", bound.Problem);
            Assert.True(coordinator.TryAnswer(bound.Id, "more", null, out _));
            _ = await DriveAsync(coordinator, held: "loopBound");

            var again = coordinator.OpenQuestions.Single(asked => asked.Kind == "loopBound");
            Assert.Equal("'rounds' has run 2 iterations since plan update u2 was planned in it and its until still reads false; at most 1 are allowed (maxIterations), and 1 more were given by its answers", again.Problem);
            Assert.Contains(harness.OrchestrationStore.ReadFlow(layout)!.Steps, step => step.Instance == "rounds#2.items" && step.State == nameof(FlowInstanceState.Done));

            Assert.True(coordinator.TryAnswer(again.Id, "accept", null, out _));
            var end = await DriveAsync(coordinator);
            Assert.True(FlowRunStatus.Done == end.Status, end.Summary);
        }

        // A count written before iterations were taken back names none, and reads as it did.
        var older = System.Text.Json.JsonSerializer.Deserialize<FlowLoopCount>("""{"update":"u1","before":1,"granted":2}""", DssHarness.Core.FileSystem.JsonStateFile.Options)!;
        Assert.Equal((null, 2), (older.AddedBy, FlowLoopCount.Given(4, older, [])));
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
            var coordinator = TestAdvice.Coordinator(definition, runner, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string> { ["request"] = "build the tool" }, records: records, updates: () => harness.OrchestrationStore.ReadFlowUpdates(layout));

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
