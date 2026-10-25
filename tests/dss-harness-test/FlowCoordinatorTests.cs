using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The coordinator in memory, over fake step runners: the instances of a workflow's steps and their bounds, the claims
/// a step holds on trees, all-or-nothing admission against the limits, the retries of an incomplete step, what a step
/// that runs legs ends with, the failure policies, Skipped and NotRun, the always steps, and what a block gathers.
/// </summary>
public sealed class FlowCoordinatorTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A runner that answers each step as its test scripted it, holding open the steps the test wants to time.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<FlowStepRun, Task<string?>>> _handoffs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>Every run asked, with the scope it was given.</summary>
        public List<FlowStepRun> Runs { get; } = [];

        /// <summary>The token each instance's last attempt was given, by instance.</summary>
        public Dictionary<string, CancellationToken> Tokens { get; } = new(StringComparer.Ordinal);

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => Task.FromResult(result);
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = run => Task.FromResult(script(run));
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run and answers when it will.</summary>
        public FakeRunner OnAsync(string step, Func<FlowStepRun, Task<FlowStepResult>> script)
        {
            _scripts[step] = script;
            return this;
        }

        /// <summary>Answers <paramref name="step"/>'s handoff turn with one account, every time it is asked.</summary>
        public FakeRunner HandsOff(string step, string? account)
        {
            _handoffs[step] = _ => Task.FromResult(account);
            return this;
        }

        /// <summary>Answers <paramref name="step"/>'s handoff turn by its script.</summary>
        public FakeRunner HandsOff(string step, Func<FlowStepRun, Task<string?>> account)
        {
            _handoffs[step] = account;
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it, after telling the coordinator what its legs do.</summary>
        public HeldStep Hold(string step, Action<FlowStepRun>? whileItRuns = null)
        {
            var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _scripts[step] = run =>
            {
                whileItRuns?.Invoke(run);
                return gate.Task;
            };

            return new HeldStep(gate);
        }

        /// <summary>Waits until the coordinator has asked for <paramref name="instance"/>.</summary>
        public Task StartedAsync(string instance)
            => StartOf(instance).Task;

        /// <summary>The completion of one instance's start, made once however often it is asked for.</summary>
        private TaskCompletionSource StartOf(string instance)
        {
            lock (_starts)
            {
                if (!_starts.TryGetValue(instance, out var started))
                {
                    started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _starts.Add(instance, started);
                }

                return started;
            }
        }

        /// <inheritdoc/>
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add(run.Instance);
            Runs.Add(run);

            lock (Tokens)
            {
                Tokens[run.Instance] = cancellationToken;
            }

            StartOf(run.Instance).TrySetResult();

            return _scripts.GetValueOrDefault(run.Step.Name, _ => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => _handoffs.GetValueOrDefault(run.Step.Name, _ => Task.FromResult<string?>(null))(run);
    }

    /// <summary>A held step's completion, in the test's hand.</summary>
    private sealed class HeldStep(TaskCompletionSource<FlowStepResult> gate)
    {
        /// <summary>Ends the step as asked.</summary>
        public void Done(JsonNode? output = null)
            => _ = gate.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success, output: output));

        /// <summary>Ends the step failed, with its code.</summary>
        public void Fail(int exit, string? error = null)
            => _ = gate.TrySetResult(FlowStepResult.Ended(passed: false, exit, error));

        /// <summary>Ends the step asking a question it asks, with the answers it takes.</summary>
        public void Ask(string kind, string problem, params string[] options)
            => _ = gate.TrySetResult(FlowStepResult.Asking(new FlowQuestion(string.Empty, kind, string.Empty, problem, options)));
    }

    /// <summary>A step's output, read from JSON text.</summary>
    private static JsonNode? Output(string json)
        => JsonNode.Parse(json);

    /// <summary>A step that runs legs, with each leg's own verdict and what its command said.</summary>
    private static FlowStepResult Legs(params (string Leg, LegVerdict Verdict)[] legs)
        => new() { Asked = null, Passed = false, Exit = HarnessExit.Incomplete, Legs = [.. legs.Select(leg => new FlowLeg(leg.Leg, leg.Verdict))] };

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts(params (string Runner, bool WritesTree)[] runners) : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner)
            => runners.Any(entry => entry.Runner == runner && entry.WritesTree);
    }

    /// <summary>A run's records that keep only the last snapshot written, and tell a test the first change whose snapshot holds what it waits for.</summary>
    private sealed class Snapshots : IFlowRecords
    {
        private readonly List<(Func<FlowSnapshot, bool> Holds, TaskCompletionSource<FlowSnapshot> Seen)> _waits = [];

        /// <summary>The last snapshot written.</summary>
        public FlowSnapshot? Last { get; private set; }

        /// <summary>The first snapshot written after this call that <paramref name="holds"/> holds of.</summary>
        public Task<FlowSnapshot> SeenAsync(Func<FlowSnapshot, bool> holds)
        {
            var seen = new TaskCompletionSource<FlowSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_waits)
            {
                _waits.Add((holds, seen));
            }

            return seen.Task;
        }

        /// <inheritdoc/>
        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            lock (_waits)
            {
                Last = snapshot;

                foreach (var (_, seen) in _waits.Where(wait => wait.Holds(snapshot)))
                {
                    seen.TrySetResult(snapshot);
                }
            }
        }

        /// <inheritdoc/>
        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
        {
        }
    }

    /// <summary>Steps ready at once start in a fixed order: declaration order, then instance id.</summary>
    [Fact]
    public async Task StepsReadyAtOnce_StartInDeclarationOrderThenInstanceId()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: c-first
                type: orient
                dependsOn: [plan]
              - name: a-second
                type: orient
                dependsOn: [plan]
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: work
                    type: orient
            """);
        var runner = new FakeRunner().On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["x", "y"]}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["plan", "c-first", "a-second", "items[0].work", "items[1].work"], runner.Started);
    }

    /// <summary>
    /// A test step's filterWhen, where it reads false, gives the step's runner no filter and fills no template: every
    /// test of the step's legs runs. Where it reads true, the filter is given as written; absent, it is given too.
    /// </summary>
    [Fact]
    public async Task ATestStepsFilterWhen_GivesTheRunnerNoFilterWhereItReadsFalse()
    {
        var definition = Parse("""
            name: flow
            inputs:
              - name: affectedTests
                options: ['on', 'off']
                default: 'off'
            steps:
              - name: verify
                type: test
                filter: 'fast-only'
                filterWhen: "input.affectedTests == 'on'"
            """);

        var off = new FakeRunner();
        _ = await new FlowCoordinator(definition, off, new RunnerFacts(), new JumpingClock()).RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(off.Runs.Single().Step.Filter);

        var on = new FakeRunner();
        _ = await new FlowCoordinator(definition, on, new RunnerFacts(), new JumpingClock(), inputs: new Dictionary<string, string>(StringComparer.Ordinal) { ["affectedTests"] = "on" }).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("fast-only", on.Runs.Single().Step.Filter);

        var absent = Parse("""
            name: flow
            steps:
              - name: verify
                type: test
                filter: 'fast-only'
            """);
        var given = new FakeRunner();
        _ = await new FlowCoordinator(absent, given, new RunnerFacts(), new JumpingClock()).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("fast-only", given.Runs.Single().Step.Filter);
    }

    /// <summary>
    /// An expression the engine asks whether something holds that reads neither true nor false - a test step's filterWhen
    /// where the step starts, a when where a step is drawn, a repeat's until - is unreadable: what reads it fails 12,
    /// saying why, and is never ended as the engine's own fault, nor does it end the session.
    /// </summary>
    [Fact]
    public async Task AnExpressionReadingNeitherTrueNorFalse_IsUnreadable_WhereverATruthIsAsked()
    {
        const string Neither = "'input.mode' is a text, and nothing is read as true or as false but true and false";
        var definition = Parse("""
            name: flow
            inputs:
              - name: mode
                default: 'on'
            steps:
              - name: verify
                type: test
                filter: 'fast-only'
                filterWhen: input.mode
                onFailure: continue
              - name: drawn
                type: orient
                when: input.mode
                onFailure: continue
              - name: loop
                onFailure: continue
                repeat:
                  until: input.mode
                  maxIterations: 2
                steps:
                  - name: attempt
                    type: orient
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            (FlowInstanceState.Failed, HarnessExit.ConfigInvalid, $"expression 'filterWhen' of 'verify': {Neither}"),
            (coordinator.Instances["verify"].State, coordinator.Instances["verify"].Exit, coordinator.Instances["verify"].Error));
        Assert.Equal(
            (FlowInstanceState.Failed, HarnessExit.ConfigInvalid, $"expression 'when' of 'drawn': {Neither}"),
            (coordinator.Instances["drawn"].State, coordinator.Instances["drawn"].Exit, coordinator.Instances["drawn"].Error));
        Assert.NotNull(coordinator.Instances["drawn"].FinishedAt);
        Assert.Equal((FlowInstanceState.Failed, HarnessExit.ConfigInvalid), (coordinator.Instances["loop"].State, coordinator.Instances["loop"].Exit));
        Assert.Equal(["loop#1.attempt"], runner.Started);
    }

    /// <summary>
    /// A step whose create-agent step ended otherwise than done - skipped, or failed and passed over - is never run: what
    /// it depends on lets it start, and the agent it works in was never made, so it is NotRun, saying so, and no runner is
    /// asked to work in an agent that is not there.
    /// </summary>
    [Fact]
    public async Task AStepWhoseAgentWasNeverMade_IsNotRun_SayingSo()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: skipped
                type: create-agent
                agent: worker.md
                when: run.name == 'nobody'
              - name: failed
                type: create-agent
                agent: worker.md
                onFailure: continue
              - name: build
                type: build
                in: skipped
                dependsOn: [skipped]
              - name: fold
                type: fold
                of: failed
                dependsOn: [failed]
            """);
        var runner = new FakeRunner().On("failed", FlowStepResult.Ended(passed: false, HarnessExit.Refused, "a run holds the main tree"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);
        var build = coordinator.Instances["build"];
        var fold = coordinator.Instances["fold"];

        Assert.Equal((FlowInstanceState.NotRun, FlowNotRunCause.AgentNotMade), (build.State, build.NotRunCause));
        Assert.Equal("the agent it works in was never made: 'skipped' is Skipped", build.NotRunBecause);
        Assert.Equal((FlowInstanceState.NotRun, FlowNotRunCause.AgentNotMade), (fold.State, fold.NotRunCause));
        Assert.Equal("the agent it works in was never made: 'failed' is Failed", fold.NotRunBecause);
        Assert.Equal(["failed"], runner.Started);
    }

    /// <summary>
    /// The agent a step works on is the one it was drawn or started with: a deletion keeps naming the agent it deleted
    /// once the run is sent back to its create-agent step and that step's new visit makes another - so its record, its
    /// directory and the agents a resume counts open never take the new agent for the one deleted.
    /// </summary>
    [Fact]
    public async Task ADeletionKeepsNamingTheAgentItDeleted_AfterItsCreateAgentMakesAnother()
    {
        var definition = Parse("""
            name: flow
            maxOpenAgents: 3
            steps:
              - name: make
                type: create-agent
                agent: worker.md
              - name: drop
                type: delete-agent
                of: make
                dependsOn: [make]
              - name: check
                type: orient
                dependsOn: [drop]
              - name: again
                type: orient
                on: {step: check, outcome: failure, codes: [20]}
                then: make
            """);
        var records = new Snapshots();
        var runner = new FakeRunner().On("check", run => run.Visit == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: records);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);
        var steps = records.Last!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(("a1", "a1"), (steps["make"].Made, steps["drop"].Agent));
        Assert.Equal(("a2", "a2"), (steps["make+2"].Made, steps["drop+2"].Agent));
        Assert.Equal(["a1", "a2"], runner.Runs.Where(run => run.Step.Name == "drop").Select(run => run.WorksIn));
    }

    /// <summary>A step waits for its dependency until that dependency ends, and then runs.</summary>
    [Fact]
    public async Task AStepWaitsForItsDependency_UntilItEnds()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: first
                type: orient
              - name: second
                type: orient
                dependsOn: [first]
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["second"].State);
    }

    /// <summary>A forEach expands to an item scope for each work item when its dependencies are done, and each member sees its own item and index.</summary>
    [Fact]
    public async Task AForEachExpandsToAnItemScopeForEachWorkItem()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: work
                    type: orient
            """);
        var runner = new FakeRunner().On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["a", "b", "c"]}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["plan", "items[0].work", "items[1].work", "items[2].work"], runner.Started);

        var given = runner.Runs.Where(run => run.Step.Name == "work").Select(run => (run.Item.ToNode()?.ToJsonString(), run.Index)).ToList();

        Assert.Equal([("\"a\"", 0), ("\"b\"", 1), ("\"c\"", 2)], given);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items[1]"].State);
    }

    /// <summary>A repeat makes an iteration, reads its <c>until</c> when the body ends, and makes the next while it is false.</summary>
    [Fact]
    public async Task ARepeatMakesIterationsWhileItsUntilIsFalse()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fix
                repeat:
                  until: attempt.ok
                  maxIterations: 5
                steps:
                  - name: attempt
                    type: orient
            """);
        var attempts = 0;
        var runner = new FakeRunner().On("attempt", run => FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: Output(++attempts < 3 ? """{"ok": false}""" : """{"ok": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["fix#1.attempt", "fix#2.attempt", "fix#3.attempt"], runner.Started);
        Assert.Equal(3, runner.Runs.Single(run => run.Step.Name == "attempt" && run.Iteration == 3).Iteration);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["fix"].State);
    }

    /// <summary>A repeat at its bound with its until still false asks, and <c>more</c> gives it one iteration, after which the same question is asked again; <c>accept</c> ends it as it stands.</summary>
    [Fact]
    public async Task ARepeatAtItsBoundAsks_MoreGivesOneIteration_AcceptEndsIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fix
                repeat:
                  until: attempt.ok
                  maxIterations: 2
                steps:
                  - name: attempt
                    type: orient
            """);
        var runner = new FakeRunner().On("attempt", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var first = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, first.Status);

        var bound = Assert.Single(coordinator.OpenQuestions);

        Assert.Equal("q1", bound.Id);
        Assert.Equal("loopBound", bound.Kind);
        Assert.Equal(["more", "accept", "abort"], bound.Options);
        Assert.Equal(FlowInstanceState.Paused, coordinator.Instances["fix"].State);

        Assert.True(coordinator.TryAnswer("q1", "more", out _));
        var second = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["fix#1.attempt", "fix#2.attempt", "fix#3.attempt"], runner.Started);
        Assert.Equal(FlowRunStatus.Paused, second.Status);

        var askedAgain = Assert.Single(coordinator.OpenQuestions);

        Assert.Equal("loopBound", askedAgain.Kind);
        Assert.True(coordinator.TryAnswer(askedAgain.Id, "accept", out _));
        var third = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, third.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["fix"].State);
    }

    /// <summary>A group whose next expansion would pass the instance bound is not expanded: it fails with the bound's own words, and its onFailure decides what follows.</summary>
    [Fact]
    public async Task AGroupThatWouldPassTheInstanceBound_IsNotExpandedAndFailsWithTheWording()
    {
        var definition = Parse("""
            name: flow
            maxInstances: 10
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                onFailure: continue
                steps:
                  - name: one
                    type: orient
                  - name: two
                    type: orient
                    dependsOn: [one]
            """);
        var runner = new FakeRunner().On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["a", "b", "c"]}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // Made so far: plan and the group itself; three item scopes of two members each would be nine more.
        Assert.Equal(
            "group 'items' would bring this run to 11 step instances, and at most 10 are allowed (maxInstances). A step instance is one run of a step: one for each work item, each iteration and each visit. Raise maxInstances, or lower a loop's bounds",
            coordinator.Instances["items"].Error);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["items"].State);
        Assert.Equal(HarnessExit.Refused, coordinator.Instances["items"].Exit);
        Assert.Equal(["plan"], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["items"], end.PassedOver);
    }

    /// <summary>A step whose when reads false is Skipped, and Skipped satisfies what depends on it.</summary>
    [Fact]
    public async Task AFalseWhenSkipsTheStep_AndSkippedSatisfiesDependents()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: orient
              - name: extra
                type: orient
                dependsOn: [gate]
                when: gate.go == false
              - name: after
                type: orient
                dependsOn: [extra]
            """);
        var runner = new FakeRunner().On("gate", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"go": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["extra"].State);
        Assert.Equal("its when read false", coordinator.Instances["extra"].SkippedBecause);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
        Assert.Equal(["gate", "after"], runner.Started);
    }

    /// <summary>NotRun satisfies nothing: a step that depends on one is NotRun too, down the chain, and the run ends failed.</summary>
    [Fact]
    public async Task NotRunSatisfiesNothing_DownTheChain()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: ci-read
              - name: next
                type: orient
                dependsOn: [broke]
              - name: last
                type: orient
                dependsOn: [next]
            """);
        var runner = new FakeRunner().On("broke", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["broke"].State);
        Assert.Equal(FlowInstanceState.NotRun, coordinator.Instances["next"].State);
        Assert.Equal(FlowInstanceState.NotRun, coordinator.Instances["last"].State);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>A write on a tree excludes every other claim on it - a fold waits behind a build's run and a reviewer's read - and once it ends, a reader and a runner share the tree.</summary>
    [Fact]
    public async Task AWriterExcludesReadersAndRunners_OnOneTree_AndReadersShareItWithRunners()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: folding
                type: fold
                of: work
                dependsOn: [work]
              - name: looking
                type: orient
                dependsOn: [work]
              - name: building
                type: build
                in: work
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        var folding = runner.Hold("folding");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("folding");

        // The fold holds write on the main tree and on the agent's: neither a reader nor a runner on either starts.
        Assert.Equal(["work", "folding"], runner.Started);

        folding.Done();
        await runner.StartedAsync("looking");
        await runner.StartedAsync("building");

        // A reader and a runner share the tree: both were admitted at once.
        _ = await session;
    }

    /// <summary>A run step whose runner writes the tree claims write on it, and not run: a reader, which shares the tree with a runner, waits behind it.</summary>
    [Fact]
    public async Task ARunStepWhoseRunnerWritesTheTree_ClaimsWriteOnItsTree()
    {
        var writes = Parse("""
            name: flow
            steps:
              - name: mutates
                type: run
                runner: mutator
              - name: looking
                type: orient
            """);
        var writesRunner = new FakeRunner();
        var mutates = writesRunner.Hold("mutates");
        var writesRun = new FlowCoordinator(writes, writesRunner, new RunnerFacts(("mutator", true)), new JumpingClock());

        var session = writesRun.RunAsync(TestContext.Current.CancellationToken);
        await writesRunner.StartedAsync("mutates");

        // Write excludes even a reader; run would share the tree with one.
        Assert.Equal(["mutates"], writesRunner.Started);

        mutates.Done();
        await writesRunner.StartedAsync("looking");
        _ = await session;

        var runs = Parse("""
            name: flow
            steps:
              - name: mutates
                type: run
                runner: mutator
              - name: looking
                type: orient
            """);
        var runsRunner = new FakeRunner();
        var held = runsRunner.Hold("mutates");
        var runsRun = new FlowCoordinator(runs, runsRunner, new RunnerFacts(("mutator", false)), new JumpingClock());

        var runsSession = runsRun.RunAsync(TestContext.Current.CancellationToken);
        await runsRunner.StartedAsync("looking");

        // A runner and a reader share the tree: the reader was admitted at once, beside the runner, which still runs.
        Assert.Equal(FlowInstanceState.Running, runsRun.Instances["mutates"].State);

        held.Done();
        _ = await runsSession;
    }

    /// <summary>A leg that works in a copy lets the tree go once its copy is made: a fold waiting on the tree goes in while the leg still builds in its copy.</summary>
    [Fact]
    public async Task ALegInACopyLetsTheTreeGo_OnceItsCopyIsMade()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: checks
                type: build
                in: work
                dependsOn: [work]
              - name: folding
                type: fold
                of: work
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        var checks = runner.Hold("checks", run =>
        {
            run.Events.LegPlaced("far", inACopy: true);
            run.Events.EveryLegPlaced();
            run.Events.LegCopyMade("far");
        });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("checks");
        await runner.StartedAsync("folding");

        // The fold started while the build still runs: the copy is made, and the tree was let go.
        Assert.Equal(["work", "checks", "folding"], runner.Started);

        checks.Done();
        _ = await session;
    }

    /// <summary>
    /// The tree is held until the runner says every leg is placed: a leg placed after an earlier leg's copy was made
    /// finds the tree still held, and the fold waiting on it goes in only once the last leg in the tree itself ends.
    /// </summary>
    [Fact]
    public async Task TheTreeIsHeld_UntilEveryLegIsPlaced()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: checks
                type: build
                in: work
                dependsOn: [work]
              - name: folding
                type: fold
                of: work
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        IFlowRunEvents? events = null;
        var said = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = runner.Hold("checks", run =>
        {
            events = run.Events;
            run.Events.LegPlaced("far", inACopy: true);
            run.Events.LegCopyMade("far");
            said.SetResult();
        });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);
        await said.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // An answer the run refuses is read after every event before it, and after the admission those events led to.
        await coordinator.SettledAsync(cancel);
        Assert.DoesNotContain("folding", runner.Started);

        events!.LegPlaced("here", inACopy: false);
        events.EveryLegPlaced();
        await coordinator.SettledAsync(cancel);
        Assert.DoesNotContain("folding", runner.Started);

        events.LegEnded("here");
        await runner.StartedAsync("folding").WaitAsync(TimeSpan.FromSeconds(30), cancel);

        checks.Done();
        _ = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
    }

    /// <summary>
    /// What an attempt said of its legs is its own: a new attempt that works in a copy alone lets the tree go once its
    /// copy is made, whatever leg the attempt before it placed in the tree itself.
    /// </summary>
    [Fact]
    public async Task ANewAttemptsLegs_AreItsOwn_NotTheAttemptBeforesLegs()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: checks
                type: build
                in: work
                dependsOn: [work]
              - name: folding
                type: fold
                of: work
                dependsOn: [keeper]
              - name: keeper
                type: orient
                dependsOn: [work]
            """);
        var second = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBegan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner().OnAsync("checks", run =>
        {
            if (run.Attempt == 1)
            {
                // The first attempt works in the tree itself and ends incomplete: it is run again.
                run.Events.LegPlaced("here", inACopy: false);
                run.Events.EveryLegPlaced();
                return Task.FromResult(Legs(("here", LegVerdict.InputsMoved)));
            }

            // The second works in a copy alone, and holds on after its copy is made.
            run.Events.LegPlaced("far", inACopy: true);
            run.Events.EveryLegPlaced();
            run.Events.LegCopyMade("far");
            secondBegan.SetResult();
            return second.Task;
        });
        // The keeper's completion is posted inline, as the test ends it: what the session reads after it is ordered.
        var keeper = new TaskCompletionSource<FlowStepResult>();
        runner.OnAsync("keeper", _ => keeper.Task);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);
        await secondBegan.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The fold is drawn while the second attempt still runs in its copy, and goes in at once.
        keeper.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        await coordinator.SettledAsync(cancel);
        Assert.Contains("folding", runner.Started);
        Assert.Equal(FlowInstanceState.Running, coordinator.Instances["checks"].State);

        second.SetResult(Legs(("far", LegVerdict.Passed)));
        _ = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
    }


    /// <summary>
    /// A step that names an agent claims that agent's own tree - the one its create-agent made - and not the name the
    /// step wrote: each item of a forEach makes its own agent, so the items' writing steps share no tree and run at once.
    /// </summary>
    [Fact]
    public async Task EachItemsAgentIsATreeOfItsOwn_SoTheItemsWritingStepsRunAtOnce()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 2
            maxOpenAgents: 2
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: maker
                    type: create-agent
                    agent: worker.md
                  - name: work
                    type: reason
                    prompt: work.md
                    outputs: work.json
                    in: maker
                    access: write
                    dependsOn: [maker]
            """);
        var runner = new FakeRunner()
            .On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["x", "y"]}""")))
            .On("maker", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["agent"] = run.Made!.Name }));
        var first = runner.Hold("work");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());
        var cancel = TestContext.Current.CancellationToken;

        var session = coordinator.RunAsync(cancel);

        // Both items' writing steps run at once: neither holds the other's agent.
        await runner.StartedAsync("items[0].work").WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await runner.StartedAsync("items[1].work").WaitAsync(TimeSpan.FromSeconds(30), cancel);

        first.Done();
        _ = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
    }

    /// <summary>A leg that works in the tree itself holds it to that leg's end: a copy made alone does not let the tree go.</summary>
    [Fact]
    public async Task ALegInTheTreeItself_HoldsTheTreeToItsEnd()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: checks
                type: build
                in: work
                dependsOn: [work]
              - name: folding
                type: fold
                of: work
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        var checks = runner.Hold("checks", run =>
        {
            run.Events.LegPlaced("far", inACopy: true);
            run.Events.LegPlaced("here", inACopy: false);
            run.Events.EveryLegPlaced();
            run.Events.LegCopyMade("far");
        });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("checks");

        Assert.Equal(["work", "checks"], runner.Started);

        checks.Done();
        await runner.StartedAsync("folding");
        _ = await session;
    }

    /// <summary>A paused step gives back every claim: another step takes what it held, and it is admitted again, against the claims as they then are, when answered.</summary>
    [Fact]
    public async Task APausedStepGivesBackItsClaims_AndIsAdmittedAgainWhenAnswered()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: folding
                type: fold
                of: work
                dependsOn: [work]
              - name: commits
                type: commit
                message: checkpoint
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        var asked = false;
        _ = runner.On("folding", run =>
        {
            // The first run asks; the run it is given the answer in ends done.
            if (!asked)
            {
                asked = true;
                return FlowStepResult.Asking(new FlowQuestion(string.Empty, "foldNewRows", string.Empty, "the fold found a new row", ["allow", "abort"]));
            }

            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var commits = runner.Hold("commits");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The fold asked: it is Paused, holds nothing, and the commit - which the fold's write held off - takes the
        // main tree meanwhile.
        await runner.StartedAsync("commits");

        Assert.Equal(FlowInstanceState.Paused, coordinator.Instances["folding"].State);
        Assert.Equal(FlowInstanceState.Running, coordinator.Instances["commits"].State);

        Assert.True(coordinator.TryAnswer(Assert.Single(coordinator.OpenQuestions).Id, "allow", out _));

        // Answered, it waits for the tree the commit holds, and goes once the commit ends.
        commits.Done();
        _ = await session;

        var again = runner.Runs.Count(run => run.Step.Name == "folding");

        Assert.Equal(2, again);
        Assert.Equal(["q1"], runner.Runs[^1].Answers.Select(answer => answer.Question));
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["folding"].State);
    }

    /// <summary>A red step under failOn red fails with its worst red leg's code, and never the command's own: a failed leg outranks one whose inputs moved.</summary>
    [Fact]
    public async Task ARedStepUnderFailOnRed_FailsWithItsWorstRedLegsCode()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: suite
                type: test
            """);
        var runner = new FakeRunner().On("suite", Legs(("here", LegVerdict.Failed), ("there", LegVerdict.InputsMoved)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var suite = coordinator.Instances["suite"];

        Assert.Equal(FlowInstanceState.Failed, suite.State);
        Assert.Equal(HarnessExit.CommandFailed, suite.Exit);
        Assert.Equal(["here"], suite.RedLegs.Select(red => red.Leg));
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>A red step under failOn never is Done with what it found: red is no failure where the step says so.</summary>
    [Fact]
    public async Task ARedStepUnderFailOnNever_IsDoneWithWhatItFound()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: baseline
                type: ci-read
                failOn: never
            """);
        var runner = new FakeRunner().On("baseline", Legs(("here", LegVerdict.Failed)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["baseline"].State);
        Assert.Equal(HarnessExit.CommandFailed, coordinator.Instances["baseline"].Exit);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A step with no red leg and an incomplete one is run again by the table's row for its code, and a passing attempt ends it Done.</summary>
    [Fact]
    public async Task AStepWithAnIncompleteLeg_IsRunAgainByTheTablesRow()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: suite
                type: test
            """);
        var attempts = 0;
        var runner = new FakeRunner().On("suite", run => ++attempts == 1
            ? Legs(("here", LegVerdict.InputsMoved))
            : Legs(("here", LegVerdict.Passed)));
        var clock = new JumpingClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["suite"].State);
        Assert.Equal(2, coordinator.Instances["suite"].Attempt);
        Assert.Equal([TimeSpan.FromSeconds(10)], clock.Waits);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>
    /// A step past its time limit is stopped - its runner's work cancelled - and ends incomplete, whatever its runner then
    /// answered, and runs again as an incomplete step does, each attempt given its whole limit anew; past its retries it
    /// fails, saying it passed its timeoutMinutes.
    /// </summary>
    [Fact]
    public async Task AStepPastItsTimeLimit_IsStopped_AndRunsAgainAsAnIncompleteStep_EachAttemptCountedAnew()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
                timeoutMinutes: 2
                retries: 1
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        _ = runner.OnAsync("gate", async run =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, TokenOf(runner, run.Instance));
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(2));

        // The first attempt's stop has its drain's bound armed; the second attempt its own limit, counted from its start.
        await clock.WaitingAsync(2).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(2));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var gate = coordinator.Instances["gate"];

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal((FlowInstanceState.Failed, HarnessExit.Incomplete), (gate.State, gate.Exit));
        Assert.Equal("passed its timeoutMinutes (2), and still so after 2 attempts", gate.Error);
        Assert.Equal([1, 2], runner.Runs.Select(run => run.Attempt));
    }

    /// <summary>
    /// An attempt after one that passed its time limit is counted as its own: one that answers within its limit ends as it
    /// answered, never incomplete for the attempt before it.
    /// </summary>
    [Fact]
    public async Task AnAttemptAfterOnePastItsTimeLimit_EndsAsItAnswers_WithinItsOwnLimit()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
                timeoutMinutes: 2
                retries: 1
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        _ = runner.OnAsync("gate", async run =>
        {
            if (run.Attempt == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, TokenOf(runner, run.Instance));
            }

            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(2));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var gate = coordinator.Instances["gate"];

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal((FlowInstanceState.Done, 2, null), (gate.State, gate.Attempt, gate.Error));
    }

    /// <summary>A reasoning step past its time limit fails incomplete and is never run again at step level: what handles its failure takes it.</summary>
    [Fact]
    public async Task AReasoningStepPastItsTimeLimit_FailsIncomplete_AndIsNotRunAgainAtStepLevel()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                timeoutMinutes: 1
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        _ = runner.OnAsync("think", async run =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, TokenOf(runner, run.Instance));
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(1));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var think = coordinator.Instances["think"];

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal((FlowInstanceState.Failed, HarnessExit.Incomplete, "passed its timeoutMinutes (1)"), (think.State, think.Exit, think.Error));
        Assert.Single(runner.Runs);
    }

    /// <summary>
    /// A step's time limit is never counted while it waits on a question of its own - its timer goes as it asks - and the
    /// run after the answer is counted from its own start; a step that ends within its limit leaves no timer behind.
    /// </summary>
    [Fact]
    public async Task ATimeLimit_IsNotCountedWhileTheStepWaitsOnAQuestion_AndStartsAgainWithTheRunAfterTheAnswer()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
                timeoutMinutes: 1
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        var answered = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = runner.OnAsync("gate", run => runner.Runs.Count == 1
            ? Task.FromResult(FlowStepResult.Asking(new FlowQuestion(string.Empty, "foldNewRows", string.Empty, "the fold found a new row", ["allow", "abort"])))
            : answered.Task);
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var asked = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, asked.Status);
        Assert.Equal(0, clock.Waiting);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(coordinator.TryAnswer(Assert.Single(coordinator.OpenQuestions).Id, "allow", out _));

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromSeconds(59));
        answered.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["gate"].State);
        Assert.Equal(0, clock.Waiting);
    }

    /// <summary>
    /// A runner that does not end once stopped for its step's time limit is waited for no longer than the drain's 90 s:
    /// the step then fails incomplete, saying so, and is never run again beside a runner still going.
    /// </summary>
    [Fact]
    public async Task ARunnerThatDoesNotEndPastItsTimeLimit_FailsOnceTheDrainBoundPasses_AndIsNotRunAgain()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
                timeoutMinutes: 1
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(1));

        // Its runner was told to stop, and runs on: the drain's bound is armed.
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        Assert.True(TokenOf(runner, "gate").IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(90));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var stopped = coordinator.Instances["gate"];

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal((FlowInstanceState.Failed, HarnessExit.Incomplete), (stopped.State, stopped.Exit));
        Assert.Equal("passed its timeoutMinutes (1), and its runner did not end within 90 s of the stop", stopped.Error);
        Assert.Single(runner.Runs);
        gate.Done();
    }

    /// <summary>A hard stop met while a step drains for its time limit wins: the step ends stopped, to run again at the resume, and not failed.</summary>
    [Fact]
    public async Task AHardStopWhileAStepDrainsForItsTimeLimit_StopsIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
                timeoutMinutes: 1
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(1));
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        coordinator.RequestStop();

        // The stop arms its own drain's bound; the time limit's, taken off the wakes, waits on the clock unread.
        await clock.WaitingAsync(2).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        gate.Done();

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var stopped = coordinator.Instances["gate"];

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
        Assert.Equal(FlowInstanceState.Stopped, stopped.State);
        Assert.Null(stopped.Error);
    }

    /// <summary>Three agents made, and a rebase of every one but the first's, each target's turn bounded by a minute and never retried.</summary>
    private static WorkflowDefinition TimedOthers()
        => Parse("""
            name: flow
            maxOpenAgents: 4
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: three
                type: create-agent
                agent: worker.md
                dependsOn: [two]
              - name: rebase
                type: rebase
                others: one
                dependsOn: [three]
                timeoutMinutes: 1
                retries: 0
                onFailure: continue
            """);

    /// <summary>
    /// A target's turn past its time limit ends that target alone - incomplete, saying so - and the next target has its
    /// turn: each target is ended on its own, and the step fails with the code of the one that failed once each has.
    /// </summary>
    [Fact]
    public async Task ATargetPastItsTimeLimit_EndsThatTargetAlone_TheNextHasItsTurn()
    {
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        _ = runner.OnAsync("rebase", async run =>
        {
            if (run.Target == "a2")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, TokenOf(runner, run.Instance));
            }

            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(TimedOthers(), runner, new RunnerFacts(), clock);

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(1));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var rebase = coordinator.Instances["rebase"];

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [
                new FlowTargetRecord("a2", FlowTargetRecord.Failed, 1, HarnessExit.Incomplete, "passed its timeoutMinutes (1)"),
                new FlowTargetRecord("a3", FlowTargetRecord.Done, 1, HarnessExit.Success),
            ],
            rebase.Targets);
        Assert.Equal((FlowInstanceState.Failed, HarnessExit.Incomplete, "target 'a2': passed its timeoutMinutes (1)"), (rebase.State, rebase.Exit, rebase.Error));
    }

    /// <summary>
    /// A target whose runner does not end within the drain's bound of the stop its time limit made is recorded failed in
    /// the step's targets, saying so, never left as a turn not ended: the next target has its turn, on its own tree, while
    /// that runner still runs.
    /// </summary>
    [Fact]
    public async Task ATargetWhoseRunnerOutlivesTheDrain_IsRecordedFailed_InTheStepsTargets()
    {
        var cancel = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner().OnAsync("rebase", run => run.Target == "a2" ? gate.Task : Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)));
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(TimedOthers(), runner, new RunnerFacts(), clock);

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(1));
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromSeconds(90));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var rebase = coordinator.Instances["rebase"];
        const string Outlived = "passed its timeoutMinutes (1), and its runner did not end within 90 s of the stop";

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [
                new FlowTargetRecord("a2", FlowTargetRecord.Failed, 1, HarnessExit.Incomplete, Outlived),
                new FlowTargetRecord("a3", FlowTargetRecord.Done, 1, HarnessExit.Success),
            ],
            rebase.Targets);
        Assert.Equal((FlowInstanceState.Failed, HarnessExit.Incomplete, $"target 'a2': {Outlived}"), (rebase.State, rebase.Exit, rebase.Error));
        Assert.Equal(["a2", "a3"], runner.Runs.Where(run => run.Instance == "rebase").Select(run => run.Target));
        gate.SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
    }

    /// <summary>
    /// Two targets of a step of others whose runners both outlive the drain each keep their own tree held until their own
    /// runner answers, whichever answers first: a step that works in the first target's tree waits until that target's
    /// runner has answered - never freed by the other's answer - and once both have, nothing is left held.
    /// </summary>
    [Theory]
    [InlineData("a3")]
    [InlineData("a2")]
    public async Task TwoTargetsWhoseRunnersOutliveTheDrain_EachHoldsItsOwnTree_UntilItsOwnRunnerAnswers(string answersFirst)
    {
        var definition = Parse("""
            name: flow
            maxOpenAgents: 4
            steps:
              - name: one
                type: create-agent
                agent: worker.md
              - name: two
                type: create-agent
                agent: worker.md
                dependsOn: [one]
              - name: three
                type: create-agent
                agent: worker.md
                dependsOn: [two]
              - name: rebase
                type: rebase
                others: one
                dependsOn: [three]
                timeoutMinutes: 1
                retries: 0
                onFailure: continue
              - name: after
                type: build
                in: two
                dependsOn: [three]
            """);
        var cancel = TestContext.Current.CancellationToken;

        // Gates made without asynchronous continuations: a runner's answer is posted before its gate's completion returns.
        var gates = new Dictionary<string, TaskCompletionSource<FlowStepResult>>(StringComparer.Ordinal)
        {
            ["a2"] = new(),
            ["a3"] = new(),
        };
        var runner = new FakeRunner().OnAsync("rebase", run => gates[run.Target!].Task);
        var clock = new TimerClock();
        var records = new Snapshots();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
        var ended = records.SeenAsync(snapshot => snapshot.Steps.Any(step => step.Instance == "rebase" && step.State == FlowInstanceState.Failed));

        var session = coordinator.RunAsync(cancel);

        // Each target's turn passes its minute, and its runner the drain's bound of the stop that made.
        foreach (var _ in gates)
        {
            await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
            clock.Advance(TimeSpan.FromMinutes(1));
            await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
            clock.Advance(TimeSpan.FromSeconds(90));
        }

        _ = await ended.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await coordinator.SettledAsync(cancel);
        var rebase = coordinator.Instances["rebase"];

        Assert.Equal([FlowTargetRecord.Failed, FlowTargetRecord.Failed], rebase.Targets!.Select(target => target.State));
        Assert.DoesNotContain("after", runner.Started.ToArray());

        // One runner answers: what its own turn claimed is given back, and the other's stays held.
        gates[answersFirst].SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));

        if (answersFirst == "a2")
        {
            // The first target's tree is free: the step in it runs, and the session ends with a3's runner still going.
            var freed = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

            Assert.Equal(FlowRunStatus.Done, freed.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
            Assert.Equal(["a3"], rebase.Lingering.SelectMany(lingering => lingering.Claims).Select(claim => claim.Tree));
            gates["a3"].SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
            return;
        }

        await coordinator.SettledAsync(cancel);

        Assert.DoesNotContain("after", runner.Started.ToArray());
        Assert.Equal(["a2"], rebase.Lingering.SelectMany(lingering => lingering.Claims).Select(claim => claim.Tree));

        gates["a2"].SetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
        Assert.Empty(rebase.Lingering);
    }

    /// <summary>
    /// A step failed past its drain's bound keeps what it claimed until its runner ends, for that runner may still be at
    /// work in the tree: a step that claims the same tree waits on the claim, and runs once the runner has answered.
    /// </summary>
    [Fact]
    public async Task AStepFailedPastItsDrainBound_AdmitsNoWriterOfItsTreeUntilItsRunnerEnds()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
                timeoutMinutes: 1
                onFailure: continue
              - name: next
                type: build
                dependsOn: [gate]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var clock = new TimerClock();
        var records = new Snapshots();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);
        var after = records.SeenAsync(snapshot => snapshot.Steps.Any(step => step.Instance == "next"
            && (step.State == FlowInstanceState.Running || step.WaitsOn.Count > 0)));

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(1));
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromSeconds(90));
        var next = (await after.WaitAsync(TimeSpan.FromSeconds(30), cancel)).Steps.Single(step => step.Instance == "next");

        Assert.Equal(FlowInstanceState.NotStarted, next.State);
        Assert.Equal(["claim"], next.WaitsOn);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["gate"].State);
        Assert.DoesNotContain("next", runner.Started.ToArray());

        gate.Done();
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["gate", "next"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["next"].State);
    }

    /// <summary>The architecture says what a step's time limit is, as the reader, the validator and the coordinator hold it.</summary>
    [Fact]
    public void TheArchitecture_SaysWhatAStepsTimeLimitIs()
    {
        Assert.Contains(
            "Every step that runs - every typed step but a `pause` step, which only asks (`StepType.Asks`) - may say `timeoutMinutes` "
                + "(`WorkflowStepTypes.Running`): the longest one attempt of it may take, in whole minutes from 1 to "
                + $"{WorkflowStep.MostTimeoutMinutes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} (`WorkflowStep.MostTimeoutMinutes`) - "
                + $"absent, its type's own limit where the type says one (`{nameof(WorkflowStepTypes)}.{nameof(WorkflowStepTypes.TimeLimitOf)}`), else none; "
                + "a group, which runs nothing of its own, and a `pause` step are refused the key where the file is read, and a `ci-wait`'s is "
                + "always more than its `waitMinutes` (absent, `WorkflowStep.DefaultWaitMinutes`). The coordinator counts it with the engine's "
                + "clock from each start of the step - an attempt, or its run after a question of its own was answered (a timer armed at `Start`, "
                + "disarmed as its runner answers) - and never while the step waits on a question; a reason step's handoff turn at a pause is "
                + "bounded by the workflow's top-level `handoffSeconds`, not by it, and its run at the resume counts anew, while a machine step a "
                + "pause leaves running keeps its count. Past it the step is stopped as a stop stops one - its runner cancelled, a reason step's minutes "
                + "counted - and, once its runner has answered, ends `Failed` with 21 whatever it answered, its error saying "
                + "`passed its timeoutMinutes (<n>)`: a machine or leg step runs again by the retry table's row for 21, a reasoning step goes to "
                + "its handlers and its `onFailure`. A runner that does not end within `PointOfNoReturn.SafePauseDrain` of that stop fails the "
                + "step all the same, saying so; it goes to its handlers and its `onFailure` and is not retried - of a step of `others`, the "
                + "target whose turn it was fails so, and the step goes on with the next. What that attempt claimed stays held until its runner "
                + $"answers (`{nameof(FlowInstance)}.{nameof(FlowInstance.Lingering)}`), a stop's drained step's too, so nothing that conflicts with what "
                + "the runner may still write starts meanwhile, a later attempt of the step itself among it - each attempt's claims kept apart, "
                + "so of two targets whose runners both outlive their turns, each one's answer gives back its own tree alone. Both stops share one body "
                + "(`StopWork`). A pause leaves a step its limit stopped to end as that stop ends it, with no handoff turn - a failure it owes "
                + "while the run is paused waits for the next session - and a stop of the run records it stopped, as it records every step in "
                + "flight.",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
    }

    /// <summary>The token the coordinator gave <paramref name="instance"/>'s last attempt, read as the runner sets it.</summary>
    private static CancellationToken TokenOf(FakeRunner runner, string instance)
    {
        lock (runner.Tokens)
        {
            return runner.Tokens[instance];
        }
    }

    /// <summary>An incomplete step past its retries fails with the incomplete code it kept meeting.</summary>
    [Fact]
    public async Task AnIncompleteStepPastItsRetries_FailsWithTheIncompleteCode()
    {
        var definition = Parse("""
            name: flow
            maxRetries: 3
            steps:
              - name: suite
                type: test
            """);
        var runner = new FakeRunner().On("suite", Legs(("here", LegVerdict.InputsMoved)));
        var clock = new JumpingClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var suite = coordinator.Instances["suite"];

        Assert.Equal(FlowInstanceState.Failed, suite.State);
        Assert.Equal(LegExit.InputsMoved, suite.Exit);
        Assert.Equal(4, suite.Attempt);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)], clock.Waits);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>A step that ends 21 is run again at once, then after 5 s and 10 s; a step that ends 13 over a held lock after 5 s; a 13 that is not is never run again.</summary>
    [Fact]
    public async Task AnIncompleteStepIsRunAgainByItsOwnRow_ALocked13IsAndABare13IsNot()
    {
        var stopped = Parse("""
            name: flow
            steps:
              - name: reader
                type: ci-read
            """);
        var attempts = 0;
        var runner = new FakeRunner().On("reader", run => FlowStepResult.Ended(passed: false, ++attempts == 4 ? HarnessExit.CommandFailed : HarnessExit.Incomplete));
        var clock = new JumpingClock();
        var coordinator = new FlowCoordinator(stopped, runner, new RunnerFacts(), clock);

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, coordinator.Instances["reader"].Attempt);

        // The first retry runs at once, so the clock is only asked for the two waits after it.
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], clock.Waits);

        var locked = Parse("""
            name: flow
            steps:
              - name: holding
                type: ci-read
            """);
        var lockedAttempts = 0;
        var lockedRunner = new FakeRunner().On("holding", run => new FlowStepResult
        {
            Asked = null,
            Passed = ++lockedAttempts == 2,
            Exit = HarnessExit.Refused,
            Cause = OutcomeCause.LockHeld,
        });
        var lockedClock = new JumpingClock();
        var lockedRun = new FlowCoordinator(locked, lockedRunner, new RunnerFacts(), lockedClock);

        _ = await lockedRun.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, lockedRun.Instances["holding"].Attempt);
        Assert.Equal([TimeSpan.FromSeconds(5)], lockedClock.Waits);

        var refused = Parse("""
            name: flow
            steps:
              - name: pointed
                type: ci-read
            """);
        var refusedRunner = new FakeRunner().On("pointed", new FlowStepResult { Asked = null, Passed = false, Exit = HarnessExit.Refused });
        var refusedRun = new FlowCoordinator(refused, refusedRunner, new RunnerFacts(), new JumpingClock());

        _ = await refusedRun.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, refusedRun.Instances["pointed"].Attempt);
        Assert.Equal(FlowInstanceState.Failed, refusedRun.Instances["pointed"].State);
    }

    /// <summary>
    /// A leg refused over a lock another run holds is run again by the lock's own row - the verdict says the cause,
    /// whether or not the runner set one beside the code - and a step that ends incomplete past its retries says what
    /// its legs ended with and how often it ran, never that it is run again.
    /// </summary>
    [Fact]
    public async Task ARefusedLockedLeg_IsRunAgainByTheLocksRow_AndPastItsRetriesSaysSoTruly()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: suite
                type: test
            """);
        var attempts = 0;
        var runner = new FakeRunner().On("suite", run => ++attempts == 1
            ? Legs(("here", LegVerdict.RefusedLocked))
            : Legs(("here", LegVerdict.Passed)));
        var clock = new JumpingClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["suite"].State);
        Assert.Equal(2, coordinator.Instances["suite"].Attempt);
        Assert.Equal([TimeSpan.FromSeconds(5)], clock.Waits);
        Assert.Equal(FlowRunStatus.Done, end.Status);

        var capped = Parse("""
            name: flow
            steps:
              - name: suite
                type: test
                retries: 1
            """);
        var cappedRun = new FlowCoordinator(capped, new FakeRunner().On("suite", Legs(("here", LegVerdict.InputsMoved))), new RunnerFacts(), new JumpingClock());

        _ = await cappedRun.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var suite = cappedRun.Instances["suite"];
        Assert.Equal(FlowInstanceState.Failed, suite.State);
        Assert.Equal("leg 'here' ended inputs-moved (3), and still so after 2 attempts", suite.Error);
    }

    /// <summary>
    /// The retry cap stops a step that keeps ending incomplete at its retries, though the table's row would run it
    /// again: with retries 1 a 21 runs twice, where the row alone would run it four times.
    /// </summary>
    [Fact]
    public async Task TheRetryCap_StopsARowThatWouldRunOn()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: reader
                type: ci-read
                retries: 1
            """);
        var runner = new FakeRunner().On("reader", FlowStepResult.Ended(passed: false, HarnessExit.Incomplete));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(2, runner.Started.Count);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["reader"].State);
    }

    /// <summary>An unwitnessed, log-held or poisoned leg fails the step whatever failOn says, and is not retried: running it again would change nothing.</summary>
    [Theory]
    [InlineData(LegVerdict.Unwitnessed, LegExit.Unwitnessed)]
    [InlineData(LegVerdict.LogHeld, LegExit.LogHeld)]
    [InlineData(LegVerdict.Poisoned, HarnessExit.InternalError)]
    public async Task AFailureThatIsNotRed_FailsTheStepWhateverFailOnSays_AndIsNotRetried(LegVerdict verdict, int exit)
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: suite
                type: test
                failOn: never
            """);
        var runner = new FakeRunner().On("suite", Legs(("here", verdict)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        var suite = coordinator.Instances["suite"];

        Assert.Equal(FlowInstanceState.Failed, suite.State);
        Assert.Equal(exit, suite.Exit);
        Assert.Equal(1, suite.Attempt);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>
    /// The always steps run once per session, however it ends, and are reset on resume: a session that ends on a
    /// question runs them, and the resume that goes on runs them again at its own end.
    /// </summary>
    [Fact]
    public async Task TheAlwaysSteps_RunOncePerSession_AndAgainAtTheResume()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: ask
                type: pause
                question: go on?
                options: [go]
              - name: after
                type: orient
                dependsOn: [ask]
              - name: notes
                type: orient
                always: true
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(["notes"], runner.Started);
        Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", out _));

        end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["notes", "after", "notes+2"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["notes"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["notes+2"].State);
    }

    /// <summary>
    /// An always member a session ran inside a group still open is drawn again at the resume, and the group's ending does
    /// not wait for it: the group ends once its work is done, what depends on it runs, and only then the always member
    /// runs again, in that session's always phase.
    /// </summary>
    [Fact]
    public async Task AnAlwaysMemberRunAgainAtTheResume_KeepsNoGroupOpen_ItRunsAfterWhatDependsOnTheGroup()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                steps:
                  - name: approve
                    type: pause
                    question: go on?
                    options: [go]
                  - name: build
                    type: orient
                    dependsOn: [approve]
                  - name: tidy
                    type: orient
                    always: true
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(["work.tidy"], runner.Started);
        Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "go", out _));

        end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["work.tidy", "work.build", "after", "work.tidy+2"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["work"].State);
    }

    /// <summary>
    /// A group's ending never waits on its always members: one that takes a member's failure ends failed before the
    /// always phase, its policy decided and what depends on it run, and its always member runs in that phase beside the
    /// run's own always steps - each reading run statuses the group's failure has already settled.
    /// </summary>
    [Fact]
    public async Task AFailedGroupsAlwaysMember_RunsInTheAlwaysPhase_AfterTheGroupEndedFailedAndWhatDependsOnItRan()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                onFailure: continue
                steps:
                  - name: gate
                    type: build
                  - name: tidy
                    type: orient
                    always: true
              - name: after
                type: orient
                dependsOn: [work]
              - name: notes
                type: orient
                always: true
                when: run.passedOver == 1
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner().On("gate", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "a leg failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(["work.gate", "after", "work.tidy", "notes"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["work.tidy"].State);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["work"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["notes"].State);
        Assert.Equal(["work"], end.PassedOver);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>
    /// An always member that fails after its group ended is decided where the group's own failure would be - the group,
    /// done, is never failed over a step that ran after it: here the group's onFailure passes it over.
    /// </summary>
    [Fact]
    public async Task AnAlwaysMemberFailingAfterItsGroupEnded_IsDecidedByTheGroupsPolicy_TheGroupStandingDone()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                onFailure: continue
                steps:
                  - name: build
                    type: build
                  - name: tidy
                    type: ci-read
                    always: true
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner().On("tidy", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(["work.build", "after", "work.tidy"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["work"].State);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["work.tidy"].State);
        Assert.Equal(["work.tidy"], end.PassedOver);
        Assert.Equal("done; 1 step failed and was passed over: work.tidy", end.Summary);
    }

    /// <summary>onFailure stop ends the run failed: nothing more is started, what runs finishes, the always steps run, and what was never started is NotRun.</summary>
    [Fact]
    public async Task OnFailureStop_EndsTheRunFailed_WhatRunsFinishes_AndAlwaysRuns()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: ci-read
              - name: slow
                type: orient
              - name: after
                type: orient
                dependsOn: [broke]
              - name: teardown
                type: orient
                always: true
            """);
        var cancel = TestContext.Current.CancellationToken;

        // A gate made without asynchronous continuations: the failure is posted before its completion returns.
        var broke = new TaskCompletionSource<FlowStepResult>();
        var runner = new FakeRunner().OnAsync("broke", _ => broke.Task);
        var slow = runner.Hold("slow");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(cancel);
        await runner.StartedAsync("broke").WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await runner.StartedAsync("slow").WaitAsync(TimeSpan.FromSeconds(30), cancel);

        _ = broke.TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        await coordinator.SettledAsync(cancel);

        // The failure stopped the run: nothing more starts, and what is in flight is left to run to its end - never
        // cancelled - before the always step runs.
        lock (runner.Tokens)
        {
            Assert.False(runner.Tokens["slow"].IsCancellationRequested);
        }

        Assert.DoesNotContain("teardown", runner.Started);
        slow.Done();
        await runner.StartedAsync("teardown").WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["slow"].State);

        Assert.Equal(FlowInstanceState.NotRun, coordinator.Instances["after"].State);
        Assert.Equal("a step it depends on failed and was neither handled nor passed over", coordinator.Instances["after"].NotRunBecause);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["teardown"].State);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(["broke"], end.Failed);
    }

    /// <summary>
    /// A NotRun step says why, by kind as well as in words: a failure it depends on, a run that ended before it, a group
    /// that took a member's failure - and a handler left NotRun by its source says its source's.
    /// </summary>
    [Fact]
    public async Task ANotRunStepSaysWhy_AFailedDependency_OrARunThatEndedFirst()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: ci-read
              - name: quick
                type: orient
              - name: after
                type: orient
                dependsOn: [broke]
              - name: later
                type: orient
                dependsOn: [quick]
              - name: tell
                type: orient
                on: {step: later, outcome: success}
            """);

        // Both end at once, in declaration order: the failure stops the run before 'quick' has ended.
        var runner = new FakeRunner().On("broke", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        var after = coordinator.Instances["after"];
        Assert.Equal(FlowNotRunCause.DependencyFailed, after.NotRunCause);
        Assert.Equal("a step it depends on failed and was neither handled nor passed over", after.NotRunBecause);
        var later = coordinator.Instances["later"];
        Assert.Equal(FlowNotRunCause.RunEnded, later.NotRunCause);
        Assert.Equal("the run ended before it", later.NotRunBecause);
        var tell = coordinator.Instances["tell"];
        Assert.Equal(FlowInstanceState.NotRun, tell.State);
        Assert.Equal(FlowNotRunCause.RunEnded, tell.NotRunCause);
        Assert.Equal("'later': the run ended before it", tell.NotRunBecause);
        Assert.Null(coordinator.Instances["quick"].NotRunCause);
    }

    /// <summary>onFailure pause asks about the step alone: what depends on it waits, every other branch runs to its end, nothing is cancelled, and the session ends paused.</summary>
    [Fact]
    public async Task OnFailurePause_BlocksItsDependentsOnly_AndCancelsNothing()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: ci-read
                onFailure: pause
              - name: after
                type: orient
                dependsOn: [broke]
              - name: elsewhere
                type: orient
            """);
        var passed = false;
        var runner = new FakeRunner().On("broke", run => passed
            ? FlowStepResult.Ended(passed: true, HarnessExit.Success)
            : FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(FlowInstanceState.Paused, coordinator.Instances["broke"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["elsewhere"].State);
        Assert.DoesNotContain("after", runner.Started);

        var asked = Assert.Single(coordinator.OpenQuestions);

        Assert.Equal("failure", asked.Kind);
        Assert.Equal(["retry", "abort"], asked.Options);

        Assert.True(coordinator.TryAnswer(asked.Id, "retry", out _));
        passed = true;
        var again = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, coordinator.Instances["broke"].Attempt);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["broke"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
        Assert.Equal(FlowRunStatus.Done, again.Status);
    }

    /// <summary>onFailure continue passes the failure over: what depends on the step runs reading its output as Missing, and the run ends done with the failure counted and listed.</summary>
    [Fact]
    public async Task OnFailureContinue_RunsDependentsOnMissing_AndEndsDoneWithItListed()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: checks
                type: ci-read
                onFailure: continue
              - name: goes-on
                type: orient
                dependsOn: [checks]
                when: checks.output ?? 'none' == 'none'
            """);
        var runner = new FakeRunner().On("checks", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["checks"].State);
        Assert.True(coordinator.Instances["checks"].PassedOver);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["goes-on"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["checks"], end.PassedOver);
        Assert.Equal("done; 1 step failed and was passed over: checks", end.Summary);
    }

    /// <summary>A member that wrote no onFailure hands its failure to its group: nothing more of the group starts - of a forEach, no further item - and the group ends failed with that member's code, then its own onFailure applies.</summary>
    [Fact]
    public async Task AMembersFailureItsGroupTook_EndsTheGroupFailedWithItsCode_AndNoFurtherItemBegins()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 1
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                onFailure: continue
                steps:
                  - name: work
                    type: reason
                    prompt: work.md
                    outputs: work.json
            """);
        var runner = new FakeRunner()
            .On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["a", "b"]}""")))
            .On("work", run => run.Index == 0
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the work failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The items run one at a time against the reasoning limit, so the second had not begun when the first failed.
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["items[0].work"].State);
        Assert.Equal(FlowInstanceState.NotRun, coordinator.Instances["items[1].work"].State);
        Assert.Equal(FlowNotRunCause.GroupFailed, coordinator.Instances["items[1].work"].NotRunCause);
        Assert.Equal(["plan", "items[0].work"], runner.Started);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["items"].State);
        Assert.Equal(HarnessExit.CommandFailed, coordinator.Instances["items"].Exit);
        Assert.Equal(["items"], end.PassedOver);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A member's own onFailure applies on the member: continue passes it over inside its group, and the group goes on to its next item.</summary>
    [Fact]
    public async Task AMembersOwnOnFailure_AppliesOnTheMember()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 1
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: work
                    type: reason
                    prompt: work.md
                    outputs: work.json
                    onFailure: continue
            """);
        var runner = new FakeRunner()
            .On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["a", "b"]}""")))
            .On("work", run => run.Index == 0
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the work failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["items[0].work"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items[1].work"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["items"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A step blocked on a tree claim reserves what it waits for: no later step takes it first, and when the claim is let go the reservation holds its place.</summary>
    [Fact]
    public async Task AStepBlockedOnATreeClaim_ReservesItAgainstLaterSteps()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: other
                type: create-agent
                agent: other.md
              - name: first-fold
                type: fold
                of: work
                dependsOn: [work]
              - name: work-rebase
                type: rebase
                of: work
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        var other = runner.Hold("other");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("other");

        // The making of the second agent holds the main tree's write. The fold waits for it and needs the first
        // agent's tree too, which is free - and it reserves both, so the rebase, declared after it and needing the
        // first agent's tree alone, does not take that tree while the fold waits for the main one.
        other.Done();
        var end = await session;

        Assert.Equal(["work", "other", "first-fold", "work-rebase"], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>All-or-nothing admission: a step short of one of its resources starts with none of them, so a step that conflicts with nothing it needs goes ahead of it.</summary>
    [Fact]
    public async Task AStepShortOfOneResource_StartsWithNoneOfThem()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: create-agent
                agent: worker.md
              - name: gate
                type: commit
                message: checkpoint
                dependsOn: [work]
              - name: waiting-fold
                type: fold
                of: work
                dependsOn: [work]
              - name: free-read
                type: ci-read
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");

        // The fold needs the main tree (held by the commit) and the agent's tree; it holds neither while it waits, so
        // the read, which needs no tree at all, goes ahead of it.
        await runner.StartedAsync("free-read");

        Assert.DoesNotContain("waiting-fold", runner.Started);

        gate.Done();
        await runner.StartedAsync("waiting-fold");
        _ = await session;
    }

    /// <summary>An alone step starts when nothing runs, and nothing starts while it runs: the barrier drains first.</summary>
    [Fact]
    public async Task AnAloneStep_StartsWhenNothingRuns_AndNothingStartsWhileItRuns()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: orient
                alone: true
              - name: beside
                type: orient
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");

        Assert.Equal(["gate"], runner.Started);

        gate.Done();
        await runner.StartedAsync("beside");
        _ = await session;
    }

    /// <summary>
    /// An alone step inside a group starts once nothing else runs: the groups around it, which stand open while their
    /// members work, are no step that runs. The shipped flow's gate and mutations sit inside its rounds.
    /// </summary>
    [Fact]
    public async Task AnAloneStepInsideAGroup_StartsWhenNoOtherStepRuns()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: gate.ok
                  maxIterations: 2
                steps:
                  - name: work
                    type: orient
                  - name: gate
                    type: orient
                    alone: true
                    dependsOn: [work]
              - name: after
                type: orient
                dependsOn: [rounds]
            """);
        var runner = new FakeRunner().On("gate", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(["rounds#1.work", "rounds#1.gate", "after"], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>
    /// Once an alone step is ready nothing new is admitted: it waits for what runs to end, and a step ready beside it
    /// waits too, so the barrier is never starved by steps that keep starting.
    /// </summary>
    [Fact]
    public async Task OnceAnAloneStepIsReady_NothingNewIsAdmitted()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: slow
                type: build
              - name: gate
                type: orient
                alone: true
              - name: other
                type: orient
            """);
        var runner = new FakeRunner();
        var slow = runner.Hold("slow");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("slow").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        slow.Done();
        _ = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(["slow", "gate", "other"], runner.Started);
    }

    /// <summary>A checkpoint push is skipped while the switch is off, with the reason, and runs where the workflow turns it on.</summary>
    [Fact]
    public async Task ACheckpointPush_IsSkippedWhileTheSwitchIsOff()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: checkpoint
                type: push
                checkpoint: true
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["checkpoint"].State);
        Assert.Equal("checkpoint pushes are off (pushCheckpoints)", coordinator.Instances["checkpoint"].SkippedBecause);
        Assert.Empty(runner.Started);

        var on = Parse("""
            name: flow
            pushCheckpoints: true
            steps:
              - name: checkpoint
                type: push
                checkpoint: true
            """);
        var onRunner = new FakeRunner();
        var onRun = new FlowCoordinator(on, onRunner, new RunnerFacts(), new JumpingClock());

        _ = await onRun.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, onRun.Instances["checkpoint"].State);
        Assert.Equal(["checkpoint"], onRunner.Started);
    }

    /// <summary>The always steps run after the rest is quiescent in any state - after a failure - and an always group's members each run.</summary>
    [Fact]
    public async Task AlwaysStepsRunAfterAFailure_AndAnAlwaysGroupsMembersEachRun()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: ci-read
              - name: closing
                always: true
                steps:
                  - name: teardown
                    type: orient
                  - name: report
                    type: orient
                    dependsOn: [teardown]
            """);
        var runner = new FakeRunner().On("broke", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["closing.teardown"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["closing.report"].State);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>
    /// An always group runs its members within its own visit, as any group does - they run in the always phase as it
    /// does: it ends once they have run, failed with a member's code where one failed, its own policy deciding it, and
    /// what it gathers reads them.
    /// </summary>
    [Fact]
    public async Task AnAlwaysBlock_EndsOnceItsMembersRan_FailedOverAFailingMember_GatheringThem()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: orient
              - name: closing
                always: true
                onFailure: continue
                gather: [notes]
                steps:
                  - name: teardown
                    type: ci-read
                  - name: report
                    type: orient
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner()
            .On("teardown", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"))
            .On("report", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"notes": ["torn down"]}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(["work", "closing.teardown", "closing.report"], runner.Started);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["closing.teardown"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["closing.report"].State);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["closing"].State);
        Assert.Equal(HarnessExit.CommandFailed, coordinator.Instances["closing"].Exit);
        Assert.Equal("""{"notes":["torn down"]}""", coordinator.Instances["closing"].Output!.ToJsonString());
        Assert.Equal(["closing"], end.PassedOver);
    }

    /// <summary>An always repeat reads its until on what each iteration's body did, and makes the next while it is false.</summary>
    [Fact]
    public async Task AnAlwaysRepeat_ReadsItsUntilOnEachIterationsBody()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                type: orient
              - name: polish
                always: true
                repeat:
                  until: attempt.ok
                  maxIterations: 5
                steps:
                  - name: attempt
                    type: orient
            """);
        var cancel = TestContext.Current.CancellationToken;
        var attempts = 0;
        var runner = new FakeRunner().On("attempt", run => FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: Output(++attempts < 3 ? """{"ok": false}""" : """{"ok": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(["work", "polish#1.attempt", "polish#2.attempt", "polish#3.attempt"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["polish"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>An always forEach ends once its items have run: one whose member failed fails it, its own policy deciding it.</summary>
    [Fact]
    public async Task AnAlwaysForEach_EndsOnceItsItemsRan_FailedOverAFailingMember()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: tidy
                always: true
                onFailure: continue
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: clean
                    type: orient
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner()
            .On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"list": ["a", "b"]}""")))
            .On("clean", run => run.Index == 1
                ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the clean failed")
                : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(["plan", "tidy[0].clean", "tidy[1].clean"], runner.Started);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["tidy[0].clean"].State);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["tidy[1].clean"].State);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["tidy"].State);
        Assert.Equal(["tidy"], end.PassedOver);
    }

    /// <summary>The run's own values an always step reads leave the always steps out, so none of them reads its own effect: an always step that fails does not make the run failed for one that follows it.</summary>
    [Fact]
    public async Task TheRunValuesAnAlwaysStepReads_LeaveTheAlwaysStepsOut()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fine
                type: orient
              - name: failing-always
                type: ci-read
                always: true
              - name: reading-always
                type: orient
                always: true
                when: run.done
            """);
        var runner = new FakeRunner().On("failing-always", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["failing-always"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["reading-always"].State);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>
    /// A group takes the failure of its first member in declaration order, whatever order the endings were read in: a
    /// later member's failure read first is replaced by the earlier member's code and words.
    /// </summary>
    [Fact]
    public async Task AGroupTakesItsFirstDeclaredFailingMembersCode_WhateverOrderTheyEnded()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: block
                steps:
                  - name: first
                    type: orient
                  - name: second
                    type: orient
            """);
        var cancel = TestContext.Current.CancellationToken;

        // Gates made without asynchronous continuations: each ending is posted before its completion returns.
        var first = new TaskCompletionSource<FlowStepResult>();
        var second = new TaskCompletionSource<FlowStepResult>();
        var runner = new FakeRunner()
            .OnAsync("first", _ => first.Task)
            .OnAsync("second", _ => second.Task);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(cancel);
        await runner.StartedAsync("block.first").WaitAsync(TimeSpan.FromSeconds(30), cancel);
        await runner.StartedAsync("block.second").WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The second member's failure is read first; the first member, still running, is left to end - and fails too.
        _ = second.TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, "the second's config"));
        await coordinator.SettledAsync(cancel);
        _ = first.TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the first's command"));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        var block = coordinator.Instances["block"];
        Assert.Equal(FlowInstanceState.Failed, block.State);
        Assert.Equal(HarnessExit.CommandFailed, block.Exit);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>
    /// A completion that comes from an attempt that is over - one a stop gave up on once its drain bound passed - is
    /// dropped: it says nothing of the attempt the run is on now, which ends as its own runner says. Until it comes, what
    /// that attempt claimed is held, so the next attempt never builds beside a runner still at work in its tree.
    /// </summary>
    [Fact]
    public async Task ALateCompletionOfAnEarlierAttempt_IsDropped()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: compile
                type: build
            """);
        var cancel = TestContext.Current.CancellationToken;

        // Gates made without asynchronous continuations, and runners deaf to the stop: the first attempt runs on
        // after it, and its ending is posted before its completion returns.
        var first = new TaskCompletionSource<FlowStepResult>();
        var second = new TaskCompletionSource<FlowStepResult>();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner().OnAsync("compile", run =>
        {
            if (run.Attempt == 1)
            {
                return first.Task;
            }

            _ = secondStarted.TrySetResult();
            return second.Task;
        });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(cancel);
        await runner.StartedAsync("compile").WaitAsync(TimeSpan.FromSeconds(30), cancel);
        coordinator.RequestStop();
        var stopped = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Stopped, stopped.Status);

        // Resumed while the first attempt's runner still runs: the second attempt waits for the tree it holds.
        session = coordinator.RunAsync(cancel);
        await coordinator.SettledAsync(cancel);

        Assert.False(secondStarted.Task.IsCompleted, "the second attempt started while the first attempt's runner still held its tree");

        // The first attempt's runner ends at last, failed: the run is on the second attempt, which it says nothing of.
        _ = first.TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the first attempt's late end"));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        _ = second.TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);

        var compile = coordinator.Instances["compile"];
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, compile.State);
        Assert.Equal(2, compile.Attempt);
        Assert.Null(compile.Error);
    }

    /// <summary>
    /// A runner that refuses the step with a code of its own ends it with that code and its message; anything else it
    /// throws is the engine's own fault, and ends the step 70.
    /// </summary>
    [Fact]
    public async Task ARunnersOwnRefusal_EndsTheStepWithItsCode_AndAnythingElseWith70()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: refused
                type: orient
                onFailure: continue
              - name: broken
                type: orient
                onFailure: continue
            """);
        var runner = new FakeRunner()
            .OnAsync("refused", _ => Task.FromException<FlowStepResult>(new HarnessException(HarnessExit.ConfigInvalid, "its runner refused the step's config")))
            .OnAsync("broken", _ => Task.FromException<FlowStepResult>(new InvalidOperationException("the runner broke")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var refused = coordinator.Instances["refused"];
        Assert.Equal(FlowInstanceState.Failed, refused.State);
        Assert.Equal(HarnessExit.ConfigInvalid, refused.Exit);
        Assert.Equal("its runner refused the step's config", refused.Error);
        var broken = coordinator.Instances["broken"];
        Assert.Equal(FlowInstanceState.Failed, broken.State);
        Assert.Equal(HarnessExit.InternalError, broken.Exit);
        Assert.Equal("the runner broke", broken.Error);
    }

    /// <summary>
    /// A failure a handler took leaves the run done for the always steps that read it - done is a run whose every
    /// failure was passed over or handled - and <c>run.name</c> reads the name the run was given.
    /// </summary>
    [Fact]
    public async Task AHandledFailure_LeavesTheRunDone_ForTheAlwaysStepsThatReadIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
              - name: report
                type: orient
                always: true
                when: run.done && !run.failed && run.status == 'done' && run.name == 'r7'
            """);
        var runner = new FakeRunner().On("build", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the build failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), runName: "r7");

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal("fix", coordinator.Instances["build"].HandledBy);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["report"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A block gathers its members' lists into one, in declaration order, when it ends - done or failed alike - and a member that was skipped adds nothing.</summary>
    [Fact]
    public async Task ABlockGathersItsMembersLists_WhenItEnds()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: review
                gather: [findings]
                steps:
                  - name: one
                    type: orient
                  - name: two
                    type: orient
                    dependsOn: [one]
                    when: one.skip == false
            """);
        var runner = new FakeRunner()
            .On("one", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"findings": ["a"], "skip": false}""")))
            .On("two", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"findings": ["b", "c"]}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal("""{"findings":["a","b","c"]}""", coordinator.Instances["review"].Output!.ToJsonString());

        var failed = Parse("""
            name: flow
            steps:
              - name: review
                gather: [findings]
                onFailure: continue
                steps:
                  - name: one
                    type: orient
                    when: run.name == 'nobody'
                  - name: two
                    type: orient
                    dependsOn: [one]
            """);
        var failedRunner = new FakeRunner().On("two", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "failed"));
        var failedRun = new FlowCoordinator(failed, failedRunner, new RunnerFacts(), new JumpingClock());

        _ = await failedRun.RunAsync(TestContext.Current.CancellationToken);

        // The block failed and the member before the failure was skipped: what it gathered is an empty list and
        // never Missing, which any and empty both take.
        Assert.Equal(FlowInstanceState.Failed, failedRun.Instances["review"].State);
        Assert.Equal("""{"findings":[]}""", failedRun.Instances["review"].Output!.ToJsonString());
    }

    /// <summary>What one iteration left, the next reads as <c>previous</c>: the first iteration has none before it, so a read that reaches for it reads nothing.</summary>
    [Fact]
    public async Task APreviousRead_ReachesTheIterationBefore_TheFirstHasNone()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fix
                repeat:
                  until: attempt.ok
                  maxIterations: 3
                steps:
                  - name: attempt
                    type: orient
                  - name: notes
                    type: orient
                    dependsOn: [attempt]
                    when: empty(previous.attempt.ok)
            """);
        var runner = new FakeRunner().On("attempt", run => FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: Output(run.Iteration == 3 ? """{"ok": true}""" : """{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["fix#1.attempt", "fix#1.notes", "fix#2.attempt", "fix#3.attempt"], runner.Started);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["fix#2.notes"].State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["fix#3.notes"].State);
    }

    /// <summary>A step's name reads its output and the engine's facts of it: its outcome, its code, its error.</summary>
    [Fact]
    public async Task AStepsNameReadsItsOutputAndItsFacts()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: reads-output
                type: orient
                dependsOn: [plan]
                when: plan.ok && plan.code == 0 && plan.outcome == 'done'
              - name: reads-error
                type: orient
                dependsOn: [plan]
                when: plan.error == 'went wrong'
            """);
        var runner = new FakeRunner()
            .On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["reads-output"].State);
        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["reads-error"].State);
    }

    /// <summary>
    /// A block's member and a repeat's member, read from outside by their paths, read what the member wrote - the
    /// repeat's from its last iteration - and the names after the member walk its output; a pause step's question
    /// fills such a name too, and so does a member's own fact.
    /// </summary>
    [Fact]
    public async Task AGroupsMemberReadFromOutside_ReadsWhatTheMemberWrote()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: review
                steps:
                  - name: correctness
                    type: orient
              - name: fix
                repeat:
                  until: attempt.ok
                  maxIterations: 3
                steps:
                  - name: attempt
                    type: orient
              - name: reads-block
                type: orient
                dependsOn: [review, fix]
                when: "review.correctness.verdict == 'ok' && review.correctness.outcome == 'done' && fix.attempt.ok"
              - name: asks
                type: pause
                dependsOn: [review]
                question: "the review said {review.correctness.verdict}"
                options: [go]
            """);
        var runner = new FakeRunner()
            .On("correctness", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"verdict": "ok"}""")))
            .On("attempt", run => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output(run.Iteration == 2 ? """{"ok": true}""" : """{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["reads-block"].State);
        Assert.Equal("the review said ok", coordinator.OpenQuestions.Single().Problem);
    }

    /// <summary>
    /// A placeholder inside an expression reads the path it names - its first name the root, the rest walked - as any
    /// read does: a step's member, an input's value, written as text and compared.
    /// </summary>
    [Fact]
    public async Task APlaceholderInsideAnExpression_ReadsThePathItNames()
    {
        var definition = Parse("""
            name: flow
            inputs:
              - name: target
                default: main
            steps:
              - name: orient
                type: orient
              - name: on-main
                type: orient
                dependsOn: [orient]
                when: "{orient.branch} == 'main' && {input.target} == 'main'"
            """);
        var runner = new FakeRunner()
            .On("orient", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"branch": "main"}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["on-main"].State);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>The latest output of a step whose failure the run passed over is Missing, as its output is: nothing it left is read.</summary>
    [Fact]
    public async Task APassedOverStepsLatest_ReadsNothing()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onFailure: continue
              - name: after
                type: orient
                dependsOn: [think]
                when: empty(think.latest)
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "it went wrong", Output("""{"plan": "half"}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
    }

    /// <summary>An answer must name an open question and be one it takes: anything else is refused, and the question stays open.</summary>
    [Fact]
    public async Task AnAnswerNotAmongTheOptions_IsRefused_AndTheQuestionStaysOpen()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // A pause step asks and asks only: no runner is asked, and the run waits on its question.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(runner.Started);
        Assert.Equal("pause", coordinator.Questions.Single().Kind);
        Assert.Equal("what now?", coordinator.Questions.Single().Problem);
        Assert.False(coordinator.TryAnswer("q9", "stay", out var unknown));
        Assert.Equal("'q9' is not a question this run leaves open", unknown);
        Assert.False(coordinator.TryAnswer("q1", "leave", out var problem));
        Assert.Equal("'leave' is not one of the answers 'q1' takes: stay, go", problem);
        Assert.Single(coordinator.OpenQuestions);
    }

    /// <summary>An input the run needs is refused with its name, and a value outside the ones its input takes is refused with the list.</summary>
    [Fact]
    public void ARunIsRefused_ForAMissingInputOrAValueOutsideItsOptions()
    {
        var definition = Parse("""
            name: flow
            inputs:
              - name: request
                required: true
              - name: mode
                options: [fast, slow]
            steps:
              - name: only
                type: orient
            """);
        var runner = new FakeRunner();

        var missing = Assert.Throws<HarnessException>(() => new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock()));

        Assert.Equal(HarnessExit.Refused, missing.ExitCode);
        Assert.Equal("input 'request' is required, and nothing gave the run a value for it", missing.Message);

        var wrong = Assert.Throws<HarnessException>(
            () => new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["request"] = "do it",
                ["mode"] = "quick",
            }));

        Assert.Equal(HarnessExit.Refused, wrong.ExitCode);
        Assert.Equal("input 'mode' takes one of fast, slow, and the run was given 'quick'", wrong.Message);
    }

    /// <summary>An open agent's slot outlives the step that made it, and only its deletion releases it: a fold carries its work and leaves it open.</summary>
    [Fact]
    public async Task AnOpenAgentSlot_OutlivesItsMake_AndOnlyADeletionReleasesIt()
    {
        var definition = Parse("""
            name: flow
            maxOpenAgents: 1
            steps:
              - name: one
                type: create-agent
                agent: one.md
              - name: going
                type: delete-agent
                of: one
                dependsOn: [one]
              - name: two
                type: create-agent
                agent: two.md
                dependsOn: [going]
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["one", "going", "two"], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);

        var folded = Parse("""
            name: flow
            maxOpenAgents: 1
            steps:
              - name: one
                type: create-agent
                agent: one.md
              - name: folding
                type: fold
                of: one
                dependsOn: [one]
              - name: two
                type: create-agent
                agent: two.md
                dependsOn: [folding]
            """);
        var foldedRunner = new FakeRunner();
        var foldedRun = new FlowCoordinator(folded, foldedRunner, new RunnerFacts(), new JumpingClock());

        var foldedEnd = await foldedRun.RunAsync(TestContext.Current.CancellationToken);

        // The fold leaves the agent open, so the second making waits for a slot nothing frees: once nothing else can
        // run it fails, saying what it waits on, and the run ends failed - never done over a step that never ran.
        Assert.Equal(["one", "folding"], foldedRunner.Started);
        var two = foldedRun.Instances["two"];
        Assert.Equal(FlowInstanceState.Failed, two.State);
        Assert.Equal(HarnessExit.Refused, two.Exit);
        Assert.Equal("it could not start: it waits on open agents, and nothing left in the run frees it", two.Error);
        Assert.Equal(FlowRunStatus.Failed, foldedEnd.Status);
        Assert.Equal(["two"], foldedEnd.Failed);
    }

    /// <summary>
    /// retry, answered to a group's failure, runs the group again: a new visit of it, opened whole with fresh members,
    /// and what depends on it runs once it ends - never a group left NotStarted that nothing opens.
    /// </summary>
    [Fact]
    public async Task ARetryOfAGroupsFailure_OpensTheGroupAgain_AsANewVisit()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                onFailure: pause
                steps:
                  - name: gate
                    type: build
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var cancel = TestContext.Current.CancellationToken;
        var gates = 0;
        var runner = new FakeRunner().On("gate", _ => ++gates == 1
            ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "a leg failed")
            : FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = Assert.Single(coordinator.OpenQuestions);
        Assert.Equal("work", asked.Instance);
        Assert.True(coordinator.TryAnswer(asked.Id, "retry", out _));

        end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(["work.gate", "work+2.gate", "after"], runner.Started);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["work"].State);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["work+2"].State);
        Assert.Equal(2, coordinator.Instances["work+2"].Visit);
    }

    /// <summary>retry, after a when that could not be read, reads the when again: the step is never run past a when nobody read.</summary>
    [Fact]
    public async Task ARetryAfterAnUnreadableWhen_ReadsTheWhenAgain()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: gated
                type: orient
                dependsOn: [plan]
                when: plan.go
                onFailure: pause
            """);
        var cancel = TestContext.Current.CancellationToken;

        // plan writes nothing named go: the when reads nothing, every time.
        var runner = new FakeRunner().On("plan", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"other": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var first = Assert.Single(coordinator.OpenQuestions);
        Assert.True(coordinator.TryAnswer(first.Id, "retry", out _));

        end = await coordinator.RunAsync(cancel).WaitAsync(TimeSpan.FromSeconds(30), cancel);

        // The retried attempt read its when again, which still reads nothing: it failed again and asks again, unrun.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var second = Assert.Single(coordinator.OpenQuestions);
        Assert.NotEqual(first.Id, second.Id);
        var gated = coordinator.Instances["gated"];
        Assert.Equal(2, gated.Attempt);
        Assert.Equal(HarnessExit.ConfigInvalid, gated.Exit);
        Assert.Equal(["plan"], runner.Started);
    }

    /// <summary>
    /// A step nothing left in the run can free fails as any step does, so its own onFailure decides: under pause the
    /// session ends on its question, and what depends on it waits rather than being dropped unlisted.
    /// </summary>
    [Fact]
    public async Task AStepNothingCanFree_FailsUnderItsOwnPolicy_SoPauseAsks()
    {
        var definition = Parse("""
            name: flow
            maxOpenAgents: 1
            steps:
              - name: one
                type: create-agent
                agent: one.md
              - name: two
                type: create-agent
                agent: two.md
                dependsOn: [one]
                onFailure: pause
              - name: after
                type: orient
                dependsOn: [two]
            """);
        var runner = new FakeRunner();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var asked = Assert.Single(coordinator.OpenQuestions);
        Assert.Equal("failure", asked.Kind);
        Assert.Equal("'two' failed (13): it could not start: it waits on open agents, and nothing left in the run frees it. What does the run do?", asked.Problem);
        Assert.Equal(["one"], runner.Started);
        Assert.False(coordinator.Instances.ContainsKey("after"));
    }

    /// <summary>A failed step that is skippable offers skip with its other answers, and skip ends it skipped and lets what depends on it go on.</summary>
    [Fact]
    public async Task ASkippableFailedStep_OffersSkip_AndSkipLetsItsDependentsGoOn()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: ci-read
                onFailure: pause
                skippable: true
              - name: after
                type: orient
                dependsOn: [broke]
            """);
        var runner = new FakeRunner().On("broke", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);

        var asked = Assert.Single(coordinator.OpenQuestions);

        Assert.Equal(["retry", "abort", "skip"], asked.Options);

        Assert.True(coordinator.TryAnswer(asked.Id, "skip", out _));
        var again = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Skipped, coordinator.Instances["broke"].State);
        Assert.Equal("skipped by the answer to its failure", coordinator.Instances["broke"].SkippedBecause);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
        Assert.Equal(FlowRunStatus.Done, again.Status);
    }

    /// <summary>Under failOn never a red leg is no failure, but a leg that fails without being red still fails the step, whatever failOn says.</summary>
    [Fact]
    public async Task AFailOnNeverStepWithARedAndANotRedLeg_FailsWithTheNotRedLegsCode()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: baseline
                type: test
                failOn: never
            """);
        var runner = new FakeRunner().On("baseline", Legs(("here", LegVerdict.Failed), ("there", LegVerdict.Unwitnessed)));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["baseline"].State);
        Assert.Equal(LegExit.Unwitnessed, coordinator.Instances["baseline"].Exit);
        Assert.Equal(1, coordinator.Instances["baseline"].Attempt);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>Under failOn never a red leg is no failure, and an incomplete leg still decides: the step is run again, and a passing attempt ends it Done.</summary>
    [Fact]
    public async Task AFailOnNeverStepWithARedAndAnIncompleteLeg_IsRunAgain()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: baseline
                type: test
                failOn: never
            """);
        var attempts = 0;
        var runner = new FakeRunner().On("baseline", run => ++attempts == 1
            ? Legs(("here", LegVerdict.Failed), ("there", LegVerdict.InputsMoved))
            : Legs(("here", LegVerdict.Passed)));
        var clock = new JumpingClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["baseline"].State);
        Assert.Equal(2, coordinator.Instances["baseline"].Attempt);
        Assert.Equal([TimeSpan.FromSeconds(10)], clock.Waits);
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A step whose failure the run passed over is read as Missing: its output adds nothing to a block's gathered list, and a read of it falls through to ??.</summary>
    [Fact]
    public async Task APassedOverStep_IsReadAsMissing_AndAddsNothingToAGather()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: review
                gather: [findings]
                steps:
                  - name: one
                    type: orient
                    onFailure: continue
                  - name: two
                    type: orient
                    dependsOn: [one]
                    when: one.findings ?? 'none' == 'none'
            """);
        var runner = new FakeRunner()
            .On("one", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "failed", output: Output("""{"findings": ["kept"]}""")))
            .On("two", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"findings": ["added"]}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["review.one"].State);
        Assert.True(coordinator.Instances["review.one"].PassedOver);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["review.two"].State);
        Assert.Equal("""{"findings":["added"]}""", coordinator.Instances["review"].Output!.ToJsonString());
        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>An always step runs even where its dependencies did not reach it: after a failure nothing else passes.</summary>
    [Fact]
    public async Task AnAlwaysStepRuns_EvenWhereItsDependenciesDidNotReachIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: ci-read
              - name: teardown
                type: orient
                always: true
                dependsOn: [broke]
            """);
        var runner = new FakeRunner().On("broke", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["teardown"].State);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>A reasoning step is never retried at step level: its client owns its transient errors.</summary>
    [Fact]
    public async Task AReasoningStep_IsNotTriedAgainAtStepLevel()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: thinking
                type: reason
                prompt: think.md
                outputs: think.json
            """);
        var runner = new FakeRunner().On("thinking", FlowStepResult.Ended(passed: false, HarnessExit.Incomplete));
        var clock = new JumpingClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["thinking"].State);
        Assert.Equal(HarnessExit.Incomplete, coordinator.Instances["thinking"].Exit);
        Assert.Equal(1, coordinator.Instances["thinking"].Attempt);
        Assert.Empty(clock.Waits);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>A step's retries can shorten a row and never lengthen it: past the row's own waits no more are given, however many retries the step says.</summary>
    [Fact]
    public async Task ARetriesCap_ShortensARow_AndNeverLengthensIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: reader
                type: ci-read
                retries: 20
            """);
        var runner = new FakeRunner().On("reader", FlowStepResult.Ended(passed: false, HarnessExit.Incomplete));
        var clock = new JumpingClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, coordinator.Instances["reader"].Attempt);

        // The row gives three waits - at once, then 5 s, then 10 s - and twenty retries cannot lengthen it.
        Assert.Equal([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)], clock.Waits);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["reader"].State);
    }

    /// <summary>A repeat's iterations are bound by maxInstances too: the next iteration that would pass the bound fails its group with the bound's own words.</summary>
    [Fact]
    public async Task ARepeatsIterations_AreBoundByMaxInstances()
    {
        var definition = Parse("""
            name: flow
            maxInstances: 4
            steps:
              - name: fix
                repeat:
                  until: attempt.ok
                  maxIterations: 9
                steps:
                  - name: attempt
                    type: orient
            """);
        var runner = new FakeRunner().On("attempt", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["fix#1.attempt"], runner.Started);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["fix"].State);
        Assert.Equal(HarnessExit.Refused, coordinator.Instances["fix"].Exit);
        Assert.Equal(
            "group 'fix' would bring this run to 5 step instances, and at most 4 are allowed (maxInstances). A step instance is one run of a step: one for each work item, each iteration and each visit. Raise maxInstances, or lower a loop's bounds",
            coordinator.Instances["fix"].Error);
        Assert.Equal(FlowRunStatus.Failed, end.Status);
    }

    /// <summary>
    /// A repeat at its own bound asks its loop's question, though its next iteration would also pass the instance
    /// bound: the bound it was written with is said first, for an answer of more is what the instance bound then holds.
    /// </summary>
    [Fact]
    public async Task ARepeatAtItsBound_AsksBeforeTheInstanceBoundRefuses()
    {
        var definition = Parse("""
            name: flow
            maxInstances: 5
            steps:
              - name: fix
                repeat:
                  until: attempt.ok
                  maxIterations: 2
                steps:
                  - name: attempt
                    type: orient
            """);
        var runner = new FakeRunner().On("attempt", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"ok": false}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(["fix#1.attempt", "fix#2.attempt"], runner.Started);
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal("loopBound", coordinator.OpenQuestions.Single().Kind);
    }

    /// <summary>
    /// A rounds repeat whose blockersLimitRounds is false has no bound past its rounds but the budget: it runs on while
    /// blocking holds, asking nothing, until its until reads true.
    /// </summary>
    [Fact]
    public async Task ARoundsRepeatWithNoBlockersLimit_RunsOnWhileBlockingHolds()
    {
        var definition = Parse("""
            name: flow
            budget:
              usd: 50
            steps:
              - name: loop
                repeat:
                  until: review.passed
                  rounds: 1
                  blocking: review.blocking
                  blockersLimitRounds: false
                steps:
                  - name: review
                    type: orient
            """);
        var runner = new FakeRunner().On("review", run => FlowStepResult.Ended(
            passed: true,
            HarnessExit.Success,
            output: Output(run.Iteration == 5 ? """{"passed": true, "blocking": true}""" : """{"passed": false, "blocking": true}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(5, runner.Started.Count);
        Assert.Empty(coordinator.Questions);
    }

    /// <summary>
    /// A step whose type has a time limit of its own and that names none is held to its type's: stopped past it, run again
    /// as an incomplete step is, and failing past its retries saying the limit was its type's.
    /// </summary>
    [Fact]
    public async Task AStepThatNamesNoTimeLimit_IsHeldToItsTypes()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: map
                type: graphify-codebase
                retries: 1
            """);
        var cancel = TestContext.Current.CancellationToken;
        var runner = new FakeRunner();
        _ = runner.OnAsync("map", async run =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, TokenOf(runner, run.Instance));
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        });
        var clock = new TimerClock();
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock);

        var session = coordinator.RunAsync(cancel);
        await clock.WaitingAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal([1], runner.Runs.Select(run => run.Attempt));
        clock.Advance(TimeSpan.FromMinutes(1));

        await clock.WaitingAsync(2).WaitAsync(TimeSpan.FromSeconds(30), cancel);
        clock.Advance(TimeSpan.FromMinutes(30));

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), cancel);
        var map = coordinator.Instances["map"];

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal((FlowInstanceState.Failed, HarnessExit.Incomplete), (map.State, map.Exit));
        Assert.Equal("passed its timeoutMinutes (30, its type's), and still so after 2 attempts", map.Error);
        Assert.Equal([1, 2], runner.Runs.Select(run => run.Attempt));
    }
}
