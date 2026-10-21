using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// Pre-existing issues, in memory and on disk over fake step runners: each issue a reason step's answer reports
/// raises one instance of the triage step, never scheduled; the triage's answer is acted on - fix given to the
/// recovery, list added proven to the run's own file, duplicate and unproven recorded and not listed - and
/// <c>{run.preExisting}</c> says what became of each.
/// </summary>
public sealed class FlowPreExistingTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests run one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A runner that answers each step as its test scripted it, seeing the issue a raised instance triages.</summary>
    private sealed class FakeRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, Func<FlowStepRun, Task<FlowStepResult>>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, with the issue it was raised for, where one did.</summary>
        public List<(string Instance, string? Issue)> Started { get; } = [];

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public FakeRunner On(string step, FlowStepResult result)
        {
            _scripts[step] = _ => Task.FromResult(result);
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by how often it has run.</summary>
        public FakeRunner On(string step, Func<int, FlowStepResult> script)
        {
            var asked = 0;
            _scripts[step] = _ => Task.FromResult(script(++asked));
            return this;
        }

        /// <summary>Answers <paramref name="step"/> by the issue its instance triages.</summary>
        public FakeRunner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = run => Task.FromResult(script(run));
            return this;
        }

        /// <summary>Holds <paramref name="step"/> open until the test completes it.</summary>
        public void Hold(string step)
        {
            var gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _scripts[step] = _ => gate.Task;
            _gates[step] = gate;
        }

        /// <summary>Completes a held <paramref name="step"/> as passed.</summary>
        public bool Release(string step)
            => _gates[step].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));

        /// <summary>Completes a held <paramref name="step"/> as passed once <paramref name="when"/> completes.</summary>
        public void ReleaseWhen(string step, TaskCompletionSource when)
        {
            _ = when.Task.ContinueWith(
                _ => _gates[step].TrySetResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)),
                TaskScheduler.Default);
        }

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            Started.Add((run.Instance, run.Issue?.Title));

            return await _scripts.GetValueOrDefault(run.Step.Name, _ => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success)))(run);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>The runners a workflow names, as a test answers them.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        /// <inheritdoc/>
        public bool WritesTree(string runner) => false;
    }

    /// <summary>A step's output, read from JSON text.</summary>
    private static JsonNode? Output(string json)
        => JsonNode.Parse(json);

    /// <summary>One issue a reason step reports, as a test writes it.</summary>
    private static FlowPreExistingReport Issue(string title, bool blocks = false)
        => new(title, "what it is", "src/Parser.cs", "read the file and saw it", blocks);

    /// <summary>The triage's answer for one course.</summary>
    private static FlowStepResult Course(string course, string title)
    {
        var answer = new JsonObject
        {
            ["course"] = course,
            ["title"] = title,
            ["description"] = "what it is",
            ["why"] = "not related to the work",
            ["proof"] = new JsonObject
            {
                ["kind"] = "read",
                ["at"] = "abc123",
                ["file"] = "src/Parser.cs",
                ["lines"] = "10-14",
                ["quote"] = "the broken thing",
            },
        };
        answer["where"] = new JsonArray("src/Parser.cs");

        return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: answer);
    }

    /// <summary>Each issue a reason step's answer reports raises one instance of the triage step, which is never scheduled.</summary>
    [Fact]
    public async Task EachIssueAReasonStepReports_RaisesOneInstanceOfTheTriageStep()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("a stale document")]))
            .On("triage", run => Course("unproven", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // One instance of the triage step for each issue, each given its own - a new visit for each - and the
        // schedule never started it.
        Assert.Equal([("think", null), ("triage", "a broken test"), ("triage+2", "a stale document")], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(2, coordinator.Instances.Values.Count(instance => instance.Step.Name == "triage"));
    }

    /// <summary>
    /// Issues a step reports while a failure's stop is ending the run are not dropped: nothing is started for them then,
    /// and the next session raises their triage.
    /// </summary>
    [Fact]
    public async Task IssuesReportedWhileTheRunStops_AreOwedToTheNextSession()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: broke
                type: ci-read
                onFailure: stop
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);

        // Both end at once, in declaration order: the failure has stopped the run when the issue is read.
        var runner = new FakeRunner()
            .On("broke", FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "the read failed"))
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", run => Course("unproven", run.Issue!.Title));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal([("broke", null), ("think", null)], runner.Started);

        _ = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal([("broke", null), ("think", null), ("triage", "a broken test")], runner.Started);
    }

    /// <summary>An issue the triage answers list for is added to the run's own file, proven, and {run.preExisting} says what became of each.</summary>
    [Fact]
    public async Task AnIssueAnsweredListIsAddedProven_AndRunPreExistingSaysIt()
    {
        var definition = Parse("""
            name: flow
            maxReasoningAgents: 4
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: later
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
                dependsOn: [think]
              - name: tell
                type: pause
                dependsOn: [later]
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("a stale document")]));
        runner.Hold("later");

        // The second issue's answer settles the tally: what waited on the reporter goes on only after it, so the
        // run's words are settled when they are read.
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On("triage", run =>
        {
            var answer = Course(run.Issue!.Title == "a broken test" ? "list" : "unproven", run.Issue.Title);

            if (run.Instance == "triage+2")
            {
                _ = settled.TrySetResult();
            }

            return answer;
        });
        runner.ReleaseWhen("later", settled);
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Paused, end.Status);

        // The listed one is in the run's words; the unproven one is never shown as a fact, only named as unproven.
        var asked = coordinator.OpenQuestions.Single(question => question.Kind == "pause");
        Assert.Equal("what was there before: listed - a broken test (src/Parser.cs); unproven - a stale document", asked.Problem);
    }

    /// <summary>An issue the triage answers fix for is given to the recovery, and where it blocks the reporting step that step runs again.</summary>
    [Fact]
    public async Task AnIssueAnsweredFix_GoesToTheRecovery_AndTheBlockingReporterRunsAgain()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
                  when: "issue.title == 'a hole in the parser' && issue.blocks"
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", asked => asked == 1
                ? FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser", blocks: true)])
                : FlowStepResult.Ended(passed: true, HarnessExit.Success))
            .On("triage", run => Course("fix", run.Issue!.Title))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        // The issue went to the recovery, which read it as {issue}; its work done, the reporting step - whose work
        // the issue blocked - runs again as a new visit, and the run ends done.
        Assert.Equal([("think", null), ("triage", "a hole in the parser"), ("recover.mend", null), ("think+2", null)], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(FlowInstanceState.Done, coordinator.Instances["think+2"].State);
    }

    /// <summary>An issue that blocks nothing leaves the reporting step as it ended: the run simply goes on once the fix is done.</summary>
    [Fact]
    public async Task AnIssueThatBlocksNothing_LeavesTheReporterAsItEnded()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a hole in the parser")]))
            .On("triage", run => Course("fix", run.Issue!.Title))
            .On("mend", FlowStepResult.Ended(passed: true, HarnessExit.Success));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal([("think", null), ("triage", "a hole in the parser"), ("recover.mend", null)], runner.Started);
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.False(coordinator.Instances.ContainsKey("think+2"));
    }

    /// <summary>A fix asked while the recovery is already at work waits for that visit's end: the recovery never calls itself.</summary>
    [Fact]
    public async Task AFixAskedWhileTheRecoveryWorks_WaitsForItsEnd()
    {
        var definition = Parse("""
            name: flow
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: orient
            steps:
              - name: first
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: second
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("first", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the first hole")]))
            .On("second", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("the second hole")]))
            .On("triage", run => Course("fix", run.Issue!.Title));
        runner.Hold("mend");
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // One recovery is at work on the first issue; the second's fix waits for its end.
        var atWork = coordinator.Instances.Values.Where(instance => instance.Step.Name == "recover").ToList();
        Assert.Single(atWork, instance => instance.State == FlowInstanceState.Running);

        _ = runner.Release("mend");
        var end = await session;

        // Both issues were fixed, one recovery at a time, and the run ends done.
        Assert.Equal(FlowRunStatus.Done, end.Status);
        Assert.Equal(
            [("first", null), ("second", null), ("triage", "the first hole"), ("triage+2", "the second hole"), ("recover.mend", null), ("recover+2.mend", null)],
            runner.Started);
    }

    /// <summary>An issue the triage answers duplicate for is recorded and not listed; the run's counts say what became of each.</summary>
    [Fact]
    public async Task AnIssueAnsweredDuplicate_IsRecordedAndNotListed()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("the same broken test")]))
            .On("triage", run => run.Issue!.Title == "a broken test"
                ? Course("list", run.Issue.Title)
                : FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"course": "duplicate", "of": "pe-1"}""")));
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();

        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), clock, records: records);

            _ = await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        // The file holds the one listed issue, proven; the duplicate added nothing.
        var issues = store.ReadPreExistingIssues(layout);

        Assert.NotNull(issues);
        var listed = Assert.Single(issues.Issues);
        Assert.Equal("pe-1", listed.Id);
        Assert.Equal("a broken test", listed.Title);
        Assert.Equal("read", listed.Proof.Kind);
        Assert.Equal("abc123", listed.Proof.At);

        // The run's status counts what became of each.
        var status = store.ReadWorkflowStatus(layout);

        Assert.NotNull(status);
        Assert.Equal(new WorkflowPreExisting(1, 0, 1, 0), status.Progress.PreExisting);
    }

    /// <summary>A triage answer whose issue does not read back fails the triage step: a record is never guessed at.</summary>
    [Fact]
    public async Task ATriageAnswerWhoseIssueDoesNotReadBack_FailsTheTriageStep()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
                onFailure: stop
            """);
        var runner = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test")]))
            .On("triage", FlowStepResult.Ended(passed: true, HarnessExit.Success, output: Output("""{"course": "list", "title": "a broken test", "description": "what", "where": ["src/Parser.cs"], "why": "not related", "proof": {"kind": "read"}}""")));
        var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock());

        var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(FlowInstanceState.Failed, coordinator.Instances["triage"].State);
        Assert.Contains("does not read back", coordinator.Instances["triage"].Error, StringComparison.Ordinal);
    }

    /// <summary>The issues a run listed are its file's: a resume by another engine restores them, and its words are settled.</summary>
    [Fact]
    public async Task TheIssuesARunListed_AreRestoredAtAResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                onPreExisting: triage
              - name: later
                type: orient
                dependsOn: [think]
              - name: tell
                type: pause
                dependsOn: [later]
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var first = new FakeRunner()
            .On("think", FlowStepResult.Ended(passed: true, HarnessExit.Success, preExisting: [Issue("a broken test"), Issue("a stale document")]))
            .On("triage", run => Course("list", run.Issue!.Title));
        first.Hold("later");
        var harness = new HarnessFactory();
        var store = new OrchestrationStore(harness.FileSystem, harness.Output);
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var clock = new ManualClock();

        FlowResumeState taken;
        using (var records = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var coordinator = new FlowCoordinator(definition, first, new RunnerFacts(), clock, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);

            await Task.Delay(100, TestContext.Current.CancellationToken);
            coordinator.RequestPause("interrupt", all: true);
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);

            var issues = store.ReadPreExistingIssues(layout);

            Assert.NotNull(issues);
            Assert.Equal(["pe-1", "pe-2"], issues.Issues.Select(issue => issue.Id));
            Assert.Equal(["a broken test", "a stale document"], issues.Issues.Select(issue => issue.Title));

            taken = new FlowResume(store, layout, harness.FileSystem).Load(FlowRunContext.DigestOf(definition));
        }

        using (var again = new FlowRunRecords(harness.FileSystem, store, layout, FlowRunContext.Of("r1", definition, startedAt: clock.GetUtcNow()), clock))
        {
            var resumed = new FlowCoordinator(definition, new FakeRunner().On("later", FlowStepResult.Ended(passed: true, HarnessExit.Success)), new RunnerFacts(), clock, records: again, resume: taken);
            var done = await resumed.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, done.Status);

            var asked = resumed.OpenQuestions.Single(question => question.Kind == "pause");
            Assert.Equal("what was there before: listed - a broken test (src/Parser.cs); listed - a stale document (src/Parser.cs)", asked.Problem);
        }
    }
}
