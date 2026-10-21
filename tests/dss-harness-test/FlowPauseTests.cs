using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The safe pause, the hard stop and the budget, over fake agents and fake builds: handoffs first, builds left
/// running, the pause complete at the last handoff, what <c>--all</c> stops, and the run's budget pausing before it
/// is spent.
/// </summary>
public sealed class FlowPauseTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A step's output, read from JSON text.</summary>
    private static JsonNode? Output(string json)
        => JsonNode.Parse(json);

    /// <summary>A runner that answers each step as its test scripted it, holding open the steps the test wants to time.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<FlowStepRun, CancellationToken, Task<string?>>> _handoffs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>Waits until the coordinator has asked for <paramref name="instance"/> once.</summary>
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

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => result;
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run and may report usage.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        /// <summary>Answers <paramref name="step"/>'s handoff turn with one account, every time it is asked.</summary>
        public FakeRunner HandsOff(string step, string? account)
        {
            _handoffs[step] = (_, _) => Task.FromResult(account);
            return this;
        }

        /// <summary>Holds <paramref name="step"/>'s handoff turn open until the test completes it, or a stop cancels it.</summary>
        public Task HandoffHeld(string step)
        {
            var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _handoffs[step] = async (_, token) =>
            {
                try
                {
                    return await gate.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            };
            return gate.Task.ContinueWith(_ => (string?)null);
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it, or a stop cancels it.</summary>
        public HeldStep Hold(string step)
        {
            var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[step] = gate;
            return new HeldStep(gate);
        }

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add(run.Instance);
            StartOf(run.Instance).TrySetResult();

            if (_scripts.TryGetValue(run.Step.Name, out var script))
            {
                return script(run);
            }

            if (_gates.TryGetValue(run.Step.Name, out var gate))
            {
                return await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => _handoffs.TryGetValue(run.Step.Name, out var account)
                ? account(run, cancellationToken)
                : Task.FromResult<string?>(null);
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
    }

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>A safe pause writes every reason step in flight a handoff, leaves the machine steps to finish and be recorded, and starts nothing more.</summary>
    [Fact]
    public async Task ASafePause_HandsOffFirst_LeavesBuildsRunning_StartsNothingMore()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: after
                type: orient
                dependsOn: [think, gate]
            """);
        var runner = new FakeRunner()
            .HandsOff("think", "what I did, what is left, what I was in the middle of")
            .On("after", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var thinking = runner.Hold("think");
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("think");
        await runner.StartedAsync("gate");
        coordinator.RequestPause("interrupt");
        gate.Done();

        var end = await session;

        // The reason step handed off and is stopped; the build was left to finish and is recorded; nothing more
        // started - 'after' waits for the resume, and the session says what the pause did.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(FlowRunStatus.Paused, coordinator.Status);
        var think = coordinator.Instances["think"];
        Assert.Equal(FlowInstanceState.Stopped, think.State);
        Assert.Equal("agent", think.Handoff!.By);
        Assert.Equal("what I did, what is left, what I was in the middle of", think.Handoff.Text);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["gate"].State);

        Assert.Equal(["think"], end.Pause!.Handoffs);
        Assert.Equal("interrupt", end.Pause.Cause);
        Assert.Equal("paused; 1 handoff written", end.Summary);

        // The resume goes on: the stopped step runs again, and what waited runs.
        thinking.Done(output: Output("""{"plan": "done"}"""));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(2, runner.Started.Count(started => started == "think"));
        Assert.Contains("after", runner.Started);
    }

    /// <summary>An agent that gives no account still leaves a handoff: the engine writes it, saying so with the bound it passed.</summary>
    [Fact]
    public async Task AnAgentThatGivesNoAccount_LeavesAHandoffTheEngineWrites()
    {
        var definition = Parse("""
            name: flow
            handoffSeconds: 60
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner();
        _ = runner.Hold("think");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("think");
        coordinator.RequestPause("command");

        var end = await session;

        var think = coordinator.Instances["think"];
        Assert.Equal(FlowInstanceState.Stopped, think.State);
        Assert.Equal("engine", think.Handoff!.By);
        Assert.Equal("Written by the engine: the agent gave no account before its handoff turn's 60 s bound", think.Handoff.Text);
        Assert.Equal(["think"], end.Pause!.Handoffs);
    }

    /// <summary>A pause that says all stops the machine steps it would have left running; a later request that says all stops what an earlier one left.</summary>
    [Fact]
    public async Task APauseThatSaysAll_StopsTheMachineStepsItWouldHaveLeft()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
              - name: wait
                type: ci-wait
                dependsOn: [gate]
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");
        coordinator.RequestPause("frontEnd", all: true);

        var end = await session;

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(FlowInstanceState.Stopped, coordinator.Instances["gate"].State);
        // A ci-wait was never reached: nothing more started.
        Assert.DoesNotContain("wait", runner.Started);
        gate.Fail(HarnessExit.Incomplete, "stopped where it stands");
        runner.On("gate", FlowStepResult.Ended(passed: true, HarnessExit.Success));

        // A second session goes on with the stopped build.
        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Contains("wait", runner.Started);
    }

    /// <summary>A hard stop cancels everything in flight, writes no handoff, runs no always step, and the run stands stopped.</summary>
    [Fact]
    public async Task AHardStop_CancelsEverythingInFlight_AndRunsNoAlwaysStep()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: gate
                type: build
                always: true
            """);
        var runner = new FakeRunner();
        _ = runner.Hold("think");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("think");
        coordinator.RequestStop();

        var end = await session;

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
        Assert.Equal("stopped", end.Summary);
        var think = coordinator.Instances["think"];
        Assert.Equal(FlowInstanceState.Stopped, think.State);
        Assert.Null(think.Handoff);
        // A hard stop runs no always step.
        Assert.DoesNotContain("gate", runner.Started);
    }

    /// <summary>The always steps that are machine steps run once the machine steps a pause left running have ended; a reason step among them does not.</summary>
    [Fact]
    public async Task TheMachineAlwaysStepsRunAfterThePause_WhileAReasonOneWaitsForTheNextSession()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
              - name: sweep
                type: orient
                always: true
                dependsOn: [gate]
              - name: report
                type: reason
                prompt: work.md
                outputs: work.json
                always: true
                dependsOn: [gate]
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");
        coordinator.RequestPause("interrupt");
        gate.Done();

        var paused = await session;

        // The machine always step runs once the machine step the pause left running has ended; the reason one among
        // them does not run - it is not even drawn, and runs in the next session.
        Assert.Equal(FlowRunStatus.Paused, paused.Status);
        Assert.Contains("sweep", runner.Started);
        Assert.DoesNotContain("report", runner.Started);
        Assert.False(coordinator.Instances.ContainsKey("report"));

        var resumed = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, resumed.Status);
        Assert.Contains("report", runner.Started);
    }

    /// <summary>A machine step the pause left running that ends incomplete with retries left is stopped where it stands, and runs again at the resume.</summary>
    [Fact]
    public async Task ALeftRunningStepThatEndsIncomplete_IsStoppedAndRunsAgainAtTheResume()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
              - name: after
                type: orient
                dependsOn: [gate]
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");
        coordinator.RequestPause("interrupt");
        // The build ends incomplete while the pause records it: it is not tried again now.
        gate.Fail(HarnessExit.Incomplete, "stopped between attempts");

        var end = await session;

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var stopped = coordinator.Instances["gate"];
        Assert.Equal(FlowInstanceState.Stopped, stopped.State);
        Assert.Equal(HarnessExit.Incomplete, stopped.Exit);
        Assert.DoesNotContain("after", runner.Started);

        // The resume runs it again from the retry it was owed.
        runner.On("gate", FlowStepResult.Ended(passed: true, HarnessExit.Success));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(2, runner.Started.Count(started => started == "gate"));
        Assert.Contains("after", runner.Started);
    }

    /// <summary>A machine step the pause left running that ends failed is recorded as it ended, and its policy waits for the resume: nothing is asked while the run records.</summary>
    [Fact]
    public async Task ALeftRunningStepThatFails_IsRecorded_AndItsPolicyWaitsForTheResume()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
                onFailure: pause
              - name: after
                type: orient
                dependsOn: [gate]
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");
        coordinator.RequestPause("interrupt");
        // The build ends failed - past what its retries would take - while the pause records it.
        gate.Fail(HarnessExit.CommandFailed, "a leg failed");

        var end = await session;

        // It is recorded as it ended, and no question is asked while the run records what it left: the failure's
        // policy is owed to the resume.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Empty(coordinator.OpenQuestions);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["gate"].State);

        // At the resume the policy applies: it asks.
        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal("failure", coordinator.OpenQuestions.Single().Kind);
        Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "retry", out _));
        runner.On("gate", FlowStepResult.Ended(passed: true, HarnessExit.Success));

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Contains("after", runner.Started);
    }

    /// <summary>A pause under way starts nothing new once the steps it left running have ended - not even in its always phase.</summary>
    [Fact]
    public async Task APauseUnderWay_StartsNothingNewOnceWhatItLeftRunningHasEnded()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
              - name: after
                type: orient
                dependsOn: [gate]
              - name: sweep
                type: orient
                always: true
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");
        coordinator.RequestPause("interrupt");
        gate.Done();

        var end = await session;

        // The machine step was recorded and the machine always step ran; what only waited - 'after' - never started,
        // for the pause had begun.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.DoesNotContain("after", runner.Started);
        Assert.Contains("sweep", runner.Started);

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Contains("after", runner.Started);
    }

    /// <summary>
    /// In a pause's always phase only the always steps that are machine steps start: a step held behind a limit the
    /// handoffs freed - a reason step, which would then never hand off - waits for the resume like every other.
    /// </summary>
    [Fact]
    public async Task APausesAlwaysPhase_StartsNoStepHeldBehindALimit()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 1
            steps:
              - name: first
                type: reason
                prompt: work.md
                outputs: work.json
              - name: second
                type: reason
                prompt: work.md
                outputs: work.json
              - name: sweep
                type: orient
                always: true
            """);
        var runner = new FakeRunner();
        _ = runner.Hold("first");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("first").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        coordinator.RequestPause("interrupt");

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Contains("sweep", runner.Started);
        Assert.DoesNotContain("second", runner.Started);
        Assert.Equal(FlowInstanceState.NotStarted, coordinator.Instances["second"].State);
    }

    /// <summary>
    /// A machine step the pause left running that ends done raises no handler while the pause records: what its ending
    /// owes is owed, and the resume raises it.
    /// </summary>
    [Fact]
    public async Task ASuccessWhileThePauseRecords_OwesItsHandler_AndTheResumeRaisesIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
              - name: tell
                type: orient
                on:
                  step: gate
                  outcome: success
              - name: sweep
                type: orient
                always: true
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        coordinator.RequestPause("interrupt");
        gate.Done();

        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Contains("sweep", runner.Started);
        Assert.DoesNotContain("tell", runner.Started);

        end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Contains("tell", runner.Started);
    }

    /// <summary>A block whose member failed while the pause recorded it does not end done over the failure: its policy is owed to the resume.</summary>
    [Fact]
    public async Task ABlockWhoseMemberFailedWhileThePauseRecords_DoesNotEndDoneOverIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                onFailure: stop
                steps:
                  - name: gate
                    type: build
                  - name: side
                    type: orient
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("work.gate");
        await runner.StartedAsync("work.side");
        coordinator.RequestPause("interrupt");
        // The build fails - past its retries - while the pause records it; its sibling has ended.
        gate.Fail(HarnessExit.CommandFailed, "a leg failed");

        var end = await session;

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var work = coordinator.Instances["work"];
        Assert.Equal(FlowInstanceState.Running, work.State);

        // At the resume the owed policy applies: the run stops over the group's failure.
        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["work.gate"].State);
        Assert.Contains("work", end.Failed);
    }

    /// <summary>A later pause request that says all, after one that did not, stops the machine steps the first left running - and the pause's record says so.</summary>
    [Fact]
    public async Task ALaterPauseRequestThatSaysAll_StopsWhatTheFirstLeftRunning()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");
        coordinator.RequestPause("interrupt");

        // The pause has begun and the build runs on; a later request says all.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        coordinator.RequestPause("command", all: true);

        var end = await session;

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(FlowInstanceState.Stopped, coordinator.Instances["gate"].State);
        Assert.True(end.Pause!.All);
        gate.Fail(HarnessExit.Incomplete, "stopped");
    }

    /// <summary>A hard stop while the handoffs are being written cancels the turns and keeps no handoff.</summary>
    [Fact]
    public async Task AHardStopWhileTheHandoffsAreWritten_CancelsTheTurnsAndKeepsNoHandoff()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner();
        _ = runner.Hold("think");
        _ = runner.HandoffHeld("think");

        // A clock that never fires the turn's bound on its own: only the stop ends the turn.
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new SteppingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("think");
        coordinator.RequestPause("interrupt");

        // The pause is writing its handoff; the stop lands while the turn is still open, and cancels it.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        coordinator.RequestStop();

        var end = await session;

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
        var think = coordinator.Instances["think"];
        Assert.Equal(FlowInstanceState.Stopped, think.State);
        Assert.Null(think.Handoff);
        Assert.Null(end.Pause);
    }

    /// <summary>The answer abort ends the run as a hard stop: what is in flight is cancelled and ends stopped, not left to finish.</summary>
    [Fact]
    public async Task AnAbortAnswer_CancelsWhatIsInFlight()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: ask
                type: pause
                question: may it go ahead?
                options: [approve]
              - name: gate
                type: build
            """);
        var runner = new FakeRunner();
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");

        // The pause step's question is open while the build runs; abort ends both.
        _ = coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "abort", out _);
        gate.Fail(HarnessExit.Incomplete, "stopped");

        var end = await session;

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
        Assert.Equal(FlowInstanceState.Stopped, coordinator.Instances["gate"].State);
    }

    /// <summary>A run granted more reaches its new reserve and asks again: the budget's question is asked once per pause, not once per run.</summary>
    [Fact]
    public async Task ABudgetGrantedMore_AsksAgainAtItsNewReserve()
    {
        var definition = Parse("""
            name: flow
            budget:
              usd: 50
              reservePercent: 10
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var at = 0;
        var runner = new FakeRunner().On("think", run =>
        {
            // Each attempt reports what the whole conversation has spent so far.
            at++;
            run.Events.Used(tokens: 1_000, usd: 45.10 * at);
            return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}"""));
        });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.True(coordinator.TryAnswer(coordinator.OpenQuestions.Single().Id, "more", out _));

        // The amounts allowed once again: the run goes on, and the re-run spends past the doubled reserve, which
        // pauses and asks a second time.
        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal("runBudget", coordinator.OpenQuestions.Single().Kind);
        Assert.Contains("of 100.00 USD", coordinator.OpenQuestions.Single().Problem);
    }

    /// <summary>Several reason steps hand off at once, and the pause's record names them in the order they were declared.</summary>
    [Fact]
    public async Task SeveralReasonStepsHandOffAtOnce_NamedInOrder()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 2
            steps:
              - name: first
                type: reason
                prompt: work.md
                outputs: work.json
              - name: second
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner()
            .HandsOff("first", "first's account")
            .HandsOff("second", "second's account");
        _ = runner.Hold("first");
        _ = runner.Hold("second");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("first");
        await runner.StartedAsync("second");
        coordinator.RequestPause("interrupt");

        var end = await session;

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(["first", "second"], end.Pause!.Handoffs);
        Assert.Equal("agent", coordinator.Instances["first"].Handoff!.By);
        Assert.Equal("second's account", coordinator.Instances["second"].Handoff!.Text);
    }

    /// <summary>A run's budget at its reserve pauses the run before it is spent, hands off what is reasoning, leaves the machine steps, and asks.</summary>
    [Fact]
    public async Task ABudgetAtItsReserve_PausesTheRunAndAsks()
    {
        var definition = Parse("""
            name: flow
            budget:
              usd: 50
              reservePercent: 10
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                dependsOn: [gate]
              - name: gate
                type: build
            """);
        var runner = new FakeRunner()
            .On("think", run =>
            {
                run.Events.Used(tokens: 1_000, usd: 45.10);
                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}"""));
            })
            .HandsOff("think", "half way through");
        var gate = runner.Hold("gate");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await runner.StartedAsync("gate");
        gate.Done();

        var end = await session;

        // The reserve was reached while the reason step ran: the run paused before its budget was spent, the step
        // handed off, the build was left to finish and recorded, and the run holds its question.
        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal("budget", end.Pause!.Cause);
        Assert.Equal(["think"], end.Pause.Handoffs);
        var think = coordinator.Instances["think"];
        Assert.Equal(FlowInstanceState.Stopped, think.State);
        Assert.Equal("half way through", think.Handoff!.Text);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["gate"].State);

        var question = coordinator.OpenQuestions.Single();
        Assert.Equal("runBudget", question.Kind);
        Assert.Equal(["more", "abort"], question.Options);
        Assert.Equal("run 'r1' has spent 45.10 of 50.00 USD (its reserve of 10% was reached)", question.Problem);
        Assert.Equal("paused; 1 handoff written, waiting on q1", end.Summary);
    }

    /// <summary>A budget answered more - by a person, or beforehand by the workflow's own answers - allows each amount once again and the run goes on.</summary>
    [Fact]
    public async Task ABudgetAnsweredMore_AllowsEachAmountOnceAgain()
    {
        var definition = Parse("""
            name: flow
            budget:
              usd: 50
              reservePercent: 10
            answers:
              runBudget:
                answer: more
                times: 1
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner()
            .On("think", run =>
            {
                // Only the first attempt reports this spend: the re-run goes on with the same conversation.
                if (run.Attempt == 1)
                {
                    run.Events.Used(tokens: 1_000, usd: 45.10);
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}"""));
            });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The workflow's own answers gave more: the run was not paused, and the question is recorded answered by the
        // configuration.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        var question = coordinator.Questions.Single();
        Assert.Equal("runBudget", question.Kind);
        Assert.Equal("answered", question.State);
        Assert.Equal("more", question.Given[0].Answer);
        Assert.Equal("configuration", question.Given[0].By);

        // A person's more, on a run the budget paused: the same spend no longer reaches the reserve, and the run
        // goes on to its end.
        var asked = Parse("""
            name: flow
            budget:
              usd: 50
              reservePercent: 10
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                dependsOn: [gate]
              - name: gate
                type: build
            """);
        var gateRunner = new FakeRunner()
            .On("think", run =>
            {
                // The re-run continues the same conversation: only its first attempt reports this spend.
                if (run.Attempt == 1)
                {
                    run.Events.Used(tokens: 1_000, usd: 45.10);
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}"""));
            })
            .HandsOff("think", "stopped mid-thought");
        var gate = gateRunner.Hold("gate");
        var paused = new FlowCoordinator(asked, gateRunner, new RunnerFacts(), new JumpingClock());

        var session = paused.RunAsync(TestContext.Current.CancellationToken);
        await gateRunner.StartedAsync("gate");
        gate.Done();

        end = await session;

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.True(paused.TryAnswer(paused.OpenQuestions.Single().Id, "more", out _));

        end = await paused.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);
    }

    /// <summary>A person answers the budget's question more and the run goes on; abort ends it stopped.</summary>
    [Fact]
    public async Task APersonAnswersTheBudgetsQuestion_AndTheRunGoesOnOrEnds()
    {
        var definition = Parse("""
            name: flow
            budget:
              usd: 50
              reservePercent: 10
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new FakeRunner()
            .On("think", run =>
            {
                // Only the first attempt reports this spend: the re-run goes on with the same conversation.
                if (run.Attempt == 1)
                {
                    run.Events.Used(tokens: 1_000, usd: 45.10);
                }

                return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"plan": "short"}"""));
            });
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        var question = coordinator.OpenQuestions.Single();
        Assert.True(coordinator.TryAnswer(question.Id, "more", out _));

        // Each amount is allowed once again: the same spend no longer reaches the reserve, and the run goes on.
        end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Done, end.Status);

        // The minutes a run's reasoning ran are counted as they pass, and reach the reserve as the dollars do.
        var timed = Parse("""
            name: flow
            budget:
              minutes: 30
              reservePercent: 10
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var clock = new ManualClock();
        var timedRunner = new FakeRunner();
        var thinking = timedRunner.Hold("think");
        var run = new FlowCoordinator(timed, timedRunner, new RunnerFacts(), clock);

        var timedSession = run.RunAsync(TestContext.Current.CancellationToken);
        await timedRunner.StartedAsync("think");
        clock.Advance(TimeSpan.FromMinutes(27.5));
        thinking.Done(output: Output("""{"plan": "short"}"""));

        end = await timedSession;

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Contains("27.5 of 30.0 minutes", run.OpenQuestions.Single().Problem);
        Assert.True(run.TryAnswer(run.OpenQuestions.Single().Id, "abort", out _));

        end = await run.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
    }
}
