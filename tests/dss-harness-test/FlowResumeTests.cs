using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// Recovery from the state a run left: the index reconciled against every step's own record - the step's own taken
/// where the two differ, what only a step's directory holds adopted - the graph rebuilt over the items and iterations
/// the records name, what a stop stopped running again with its handoff, a crash cut off given one the engine writes,
/// the definition frozen as it ran, the owner claimed and reclaimed, and a resume beside an open question refused only
/// where nothing can run until it is answered.
/// </summary>
public sealed class FlowResumeTests
{
    /// <summary>Every bound a test of a session waits for: a session that hangs fails the test rather than the run's minutes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>The run's records in a directory of their own.</summary>
    private static (HarnessFactory Harness, OrchestratorLayout Layout, FlowRunRecords Records) Records(string run, string directory, WorkflowDefinition definition)
    {
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(directory, directory), run);
        return (harness, layout, new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of(run, definition)));
    }

    /// <summary>What a run left, taken up: the records read, reconciled, and handed back with the handoffs of its stopped steps.</summary>
    private static FlowResumeState TakeUp(HarnessFactory harness, OrchestratorLayout layout, WorkflowDefinition definition, string? digest = null)
        => new FlowResume(harness.OrchestrationStore, layout, harness.FileSystem).Load(digest ?? FlowRunContext.DigestOf(definition));

    /// <summary>A run that ends writes its state whole: every instance with its state and what it waits on, next empty, the pause it ended by, and the events in order.</summary>
    [Fact]
    public async Task ARunThatEnds_WritesItsStateWhole()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
                dependsOn: [one]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
        }

        var file = harness.OrchestrationStore.ReadFlow(layout);

        Assert.NotNull(file);
        Assert.Equal("r1", file.Run);
        Assert.Equal("flow", file.Definition);
        Assert.Equal("Done", file.Status);
        Assert.Equal(["one", "two"], file.Steps.Select(step => step.Instance));
        Assert.All(file.Steps, step => Assert.Equal("Done", step.State));
        Assert.Empty(file.Next);
        Assert.Null(file.Pause);

        var (events, torn) = FlowEventLog.Read(harness.FileSystem, layout.EventsFile);

        Assert.Null(torn);
        Assert.Equal("sessionBegan", events[0].Kind);
        Assert.Equal("sessionEnded", events[^1].Kind);
        Assert.Equal("Done", events[^1].To);
    }

    /// <summary>The state a pause left is taken up whole: the stopped step runs again as a new attempt, its handoff read from its own directory and given to the attempt, and the file removed.</summary>
    [Fact]
    public async Task TheStateAPauseLeft_IsTakenUpWhole_TheHandoffGivenAndRemoved()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: after
                type: orient
                dependsOn: [think]
            """);
        var runner = new Runner().Hold("think");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            runner.HandsOff("think", "halfway through the reasoning");
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("think");
            coordinator.RequestPause("command");
            var end = await session;

            Assert.Equal(FlowRunStatus.Paused, end.Status);
        }

        var taken = TakeUp(harness, layout, definition);
        var handoff = layout.StepHandoffFile("think");

        Assert.Equal(["think"], taken.File.Pause!.Handoffs);
        Assert.NotNull(taken.File.Pause.CompletedAt);
        Assert.True(taken.Handoffs.TryGetValue("think", out var account));
        Assert.Equal("halfway through the reasoning", account);

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: taken, records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["think"].State);
            Assert.Equal(2, coordinator.Instances["think"].Attempt);
            Assert.True(harness.FileSystem.KindOf(handoff) == PathKind.None, "the handoff file is removed once the attempt is given it");
        }

        var file = harness.OrchestrationStore.ReadFlow(layout);

        Assert.NotNull(file);
        Assert.Null(file.Pause);
    }

    /// <summary>What a crash cut off is taken up: it runs again as a new attempt, and the engine writes the handoff its agent never gave - for the next attempt is told what the cut-off one had done.</summary>
    [Fact]
    public async Task WhatACrashCutOff_RunsAgainWithAHandoffTheEngineWrites()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: after
                type: orient
                dependsOn: [think]
            """);
        var runner = new Runner().Hold("think");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            _ = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("think");

            // The crash: the engine's process ended, and its records stand as its last whole writes left them.
        }

        var taken = TakeUp(harness, layout, definition);

        Assert.Equal("Running", taken.File.Steps.Single(step => step.Instance == "think").State);

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: taken, records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(2, coordinator.Instances["think"].Attempt);
        }

        Assert.Equal("Written by the engine: the engine's process ended.\nAttempt 1 was cut off.", next.Given[2]);
    }

    /// <summary>
    /// A stop asked between sessions - abort answered with no engine running the run - ends the session it begins where
    /// the run stands, stopped, as a stop in a session ends it: what a pause stopped stays stopped with the agent's own
    /// account, its attempt as it was, no handoff of the engine's in its place, the abort's question open again - and the
    /// session after it goes on with all of it.
    /// </summary>
    [Fact]
    public async Task AStopAskedBetweenSessions_EndsWhereTheRunStands_NothingRunAgain()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: asker
                type: pause
                question: which way?
                options: [left, right]
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var first = new Runner().HandsOff("think", "what the agent did").Hold("think");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var watched = new Watched(records);
            var coordinator = new FlowCoordinator(definition, first, new RunnerFacts(), records: watched);
            var session = coordinator.RunAsync(Token);
            await first.StartedAsync("think").WaitAsync(Bound, Token);
            await watched.SeenAsync(snapshot => snapshot.WaitingOn.Count == 1).WaitAsync(Bound, Token);
            coordinator.RequestPause("command");

            Assert.Equal(FlowRunStatus.Paused, (await session.WaitAsync(Bound, Token)).Status);
        }

        var thinkBefore = File.ReadAllText(layout.StepRecordFile("think"));
        var aborted = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, aborted, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.True(coordinator.TryAnswer("q1", "abort", out var problem), problem);

            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Stopped, end.Status);
            Assert.Empty(aborted.Started);
            Assert.Equal(FlowInstanceState.Stopped, coordinator.Instances["think"].State);
            Assert.Equal(1, coordinator.Instances["think"].Attempt);
            Assert.Equal(new FlowHandoff("what the agent did", "agent"), coordinator.Instances["think"].Handoff);
            Assert.Equal(["q1"], coordinator.OpenQuestions.Select(question => question.Id));
        }

        Assert.Equal(thinkBefore, File.ReadAllText(layout.StepRecordFile("think")));

        var next = new Runner();
        var (_, _, last) = Records("r1", temp.Path, definition);

        using (last)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: last);

            Assert.Equal(FlowRunStatus.Paused, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal(["think"], next.Started);
        Assert.Equal("what the agent did", next.Given[2]);
    }

    /// <summary>
    /// An agent's account an attempt still holds - an earlier pause wrote it, and no prompt holds it yet - is never
    /// written over by the engine: not by a later pause whose turn gives none, and not by the resume after a crash.
    /// Each next attempt is given the agent's account as it wrote it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAgentsAccountNoPromptHoldsYet_IsNeverWrittenOverByTheEngine(bool pausedAgain)
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        const string account = "what the agent did:\n- read the parser\n";
        var first = new Runner().HandsOff("think", account).Hold("think");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, first, new RunnerFacts(), records: records);
            var session = coordinator.RunAsync(Token);
            await first.StartedAsync("think").WaitAsync(Bound, Token);
            coordinator.RequestPause("command");

            Assert.Equal(FlowRunStatus.Paused, (await session.WaitAsync(Bound, Token)).Status);
        }

        // The next attempt starts with the account, and is cut off before its prompt holds it - by a pause whose turn
        // gives no account, or by a crash.
        var second = new Runner().Hold("think");
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, second, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var session = coordinator.RunAsync(Token);
            await second.StartedAsync("think", 2).WaitAsync(Bound, Token);

            Assert.Equal(account, second.Given[2]);

            if (pausedAgain)
            {
                coordinator.RequestPause("command");
                var paused = await session.WaitAsync(Bound, Token);

                Assert.Equal(FlowRunStatus.Paused, paused.Status);
                Assert.Empty(paused.Pause!.Handoffs);
                Assert.Equal(new FlowHandoff(account, "agent"), coordinator.Instances["think"].Handoff);
            }
        }

        Assert.Equal(account + "\n", File.ReadAllText(layout.StepHandoffFile("think")));

        var third = new Runner();
        var (_, _, last) = Records("r1", temp.Path, definition);

        using (last)
        {
            var coordinator = new FlowCoordinator(definition, third, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: last);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal(account, third.Given[3]);
    }

    /// <summary>The index is reconciled against the step's own record - written first, so the truth where the two differ - and a step directory the index does not name is adopted.</summary>
    [Fact]
    public async Task TheIndexIsReconciledAgainstTheStepsOwnRecords_AndAdoptsWhatOnlyADirectoryNames()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
                dependsOn: [one]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        // The index is made to say less than the step's own record does, as a crash between the two writes would:
        // the step ended, its own record says so, and the index was never written over it.
        var file = harness.OrchestrationStore.ReadFlow(layout)!;
        var lying = file with
        {
            Steps = [.. file.Steps.Select(step => step.Instance == "two" ? step with { State = "NotStarted", FinishedAt = null } : step)],
        };
        harness.OrchestrationStore.WriteFlow(layout, lying);

        // A step directory the index does not name, holding a record of its own.
        var orphan = layout.StepRecordFile("extra");
        harness.FileSystem.CreateDirectory(Path.GetDirectoryName(orphan)!);
        harness.OrchestrationStore.WriteStepRecord(orphan, new FlowStepRecord("extra", "extra", false, "Done", 1, 1, [], []));

        var taken = TakeUp(harness, layout, definition);

        Assert.Equal(["two"], taken.Differing);
        Assert.Equal(["extra"], taken.Adopted);
        Assert.Equal(["one", "two", "extra"], taken.File.Steps.Select(step => step.Instance));

        var written = harness.OrchestrationStore.ReadFlow(layout);

        Assert.NotNull(written);
        Assert.Equal(["one", "two", "extra"], written.Steps.Select(step => step.Instance));
        Assert.Equal("Done", written.Steps.Single(step => step.Instance == "two").State);
    }

    /// <summary>
    /// The index's own order is kept - the order the run made its steps - never sorted again: <c>b+10</c> stays after
    /// <c>b+2</c> where the run made it so, and what the index never named goes after all it did.
    /// </summary>
    [Fact]
    public async Task TheIndexsOwnOrderIsKept_NeverSortedAgain()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: z
                type: orient
              - name: a
                type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            await new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records).RunAsync(TestContext.Current.CancellationToken);
        }

        var file = harness.OrchestrationStore.ReadFlow(layout)!;
        harness.OrchestrationStore.WriteFlow(layout, file with { Steps = [.. file.Steps.OrderByDescending(step => step.Instance, StringComparer.Ordinal)] });

        var taken = TakeUp(harness, layout, definition);

        Assert.Equal(["z", "a"], taken.File.Steps.Select(step => step.Instance));
        Assert.Equal(["z", "a"], harness.OrchestrationStore.ReadFlow(layout)!.Steps.Select(step => step.Instance));
    }

    /// <summary>
    /// A step's own record is taken whole wherever it says anything the index does not - not only its state, attempt,
    /// exit, question or output: a crash after a handler was written owed, before the index was, leaves the five equal and
    /// the record ahead, and the record is the truth.
    /// </summary>
    [Fact]
    public async Task AStepsOwnRecordIsTakenWhole_WhereItSaysAnythingTheIndexDoesNot()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            await new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records).RunAsync(TestContext.Current.CancellationToken);
        }

        // The crash's cut: the step's own record written with its handler owed and an answer it was given, the index not.
        var own = harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("one"))!;
        harness.OrchestrationStore.WriteStepRecord(layout.StepRecordFile("one"), own with { Handlers = [new FlowHandlerState("tidy", "owed")], Answers = [new FlowAnswerGiven("q1", "go")] });

        var taken = TakeUp(harness, layout, definition);
        var step = Assert.Single(taken.File.Steps);

        Assert.Equal(["one"], taken.Differing);
        Assert.Equal([new FlowHandlerState("tidy", "owed")], step.Handlers);
        Assert.Equal("go", Assert.Single(step.Answers).Answer);
    }

    /// <summary>
    /// The cut between the run's state and its status, and the one before the event: a resume reads neither the status
    /// nor the events, so it goes on from the state alone - its start taken from the state, never from the status a person
    /// reads - and the events go on numbered from their last whole line.
    /// </summary>
    [Fact]
    public async Task AResumeGoesOnFromTheStateAlone_WhereTheStatusOrTheLastEventIsMissing()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            await new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records).RunAsync(TestContext.Current.CancellationToken);
        }

        var began = harness.OrchestrationStore.ReadFlow(layout)!.StartedAt;
        var (events, _) = FlowEventLog.Read(harness.FileSystem, layout.EventsFile);
        var lines = File.ReadAllLines(layout.EventsFile);

        File.Delete(layout.WorkflowStatusFile);
        File.WriteAllText(layout.EventsFile, string.Join("\n", lines[..^1]) + "\n");

        var taken = TakeUp(harness, layout, definition);
        var context = FlowRunContext.Resumed(definition, taken.File);

        Assert.Equal(began, context.StartedAt);
        Assert.Equal(FlowRunContext.DigestOf(definition), context.DefinitionDigest);

        using (var again = FlowEventLog.Open(harness.FileSystem, layout.EventsFile))
        {
            Assert.Equal(events.Count, again.Append(DateTimeOffset.UtcNow, FlowChangeKind.Resumed).Seq);
        }
    }

    /// <summary>A step directory inside an agent's that the index does not name is adopted too, after every step the index named.</summary>
    [Fact]
    public async Task AStepRecordInsideAnAgentsDirectory_TheIndexMissed_IsAdopted()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            await new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records).RunAsync(TestContext.Current.CancellationToken);
        }

        var inAgent = layout.AgentStepRecordFile("a1", "work");
        Directory.CreateDirectory(Path.GetDirectoryName(inAgent)!);
        harness.OrchestrationStore.WriteStepRecord(inAgent, new FlowStepRecord("work", "work", false, "Running", 1, 1, [], [], Type: "build", Agent: "a1"));

        var taken = TakeUp(harness, layout, definition);

        Assert.Equal(["work"], taken.Adopted);
        Assert.Equal(["one", "work"], taken.File.Steps.Select(step => step.Instance));
        Assert.Equal("a1", taken.File.Steps[1].Agent);
    }

    /// <summary>A run's questions or issues missing are refused by name, never made again empty: a step waiting on a question would wait for ever.</summary>
    [Theory]
    [InlineData("questions.json", "its questions and answers")]
    [InlineData("pre-existing-issues.json", "its pre-existing issues")]
    public async Task ARunsQuestionsOrIssuesMissing_AreRefusedByName(string missing, string what)
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            await new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records).RunAsync(TestContext.Current.CancellationToken);
        }

        var file = Path.Combine(layout.Directory, missing);
        File.Delete(file);

        var refused = Assert.Throws<HarnessException>(() => TakeUp(harness, layout, definition));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.StartsWith($"'{file}' is not there, so run 'r1' holds no record of {what}", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(file));

        using (new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, layout, FlowRunContext.Of("r1", definition)))
        {
            // Opened over a run already made: nothing is created in the missing one's place.
        }

        Assert.False(File.Exists(file));
    }

    /// <summary>A record that names a handoff its directory does not hold is refused, naming the file: the engine never writes one of its own in the place of the agent's account.</summary>
    [Fact]
    public async Task ARecordNamingAHandoffItsDirectoryDoesNotHold_IsRefusedNamingTheFile()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new Runner().Hold("think").HandsOff("think", "halfway through");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("think").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            coordinator.RequestPause("command");
            _ = await session.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        File.Delete(layout.StepHandoffFile("think"));

        var refused = Assert.Throws<HarnessException>(() => TakeUp(harness, layout, definition));

        Assert.Equal(
            $"step 'think' of run 'r1' holds a handoff written by the agent, and '{layout.StepHandoffFile("think")}' is not there. "
            + "Nothing was changed: a handoff is never written again in the place of one that was lost; put the file back to go on with the run, "
            + "or delete-orchestrator r1 ends it.",
            refused.Message);
    }

    /// <summary>
    /// A handoff's file a crash left beside a record that does not name it yet - the file is written first - is taken
    /// back: the agent's, or the engine's where its first line says the engine wrote it, and why.
    /// </summary>
    [Theory]
    [InlineData("what the agent said it was doing", "agent", null)]
    [InlineData("what the agent did:\n- read the parser\n", "agent", null)]
    [InlineData("Written by the engine: the run was stopped.\nAttempt 1 was cut off.", "engine", "the run was stopped")]
    public async Task AHandoffACrashLeftBesideItsRecord_IsTakenBack(string text, string by, string? why)
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        var runner = new Runner().Hold("think");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            _ = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("think").WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // The crash, between the handoff's file and the record that would name it.
        }

        File.WriteAllText(layout.StepHandoffFile("think"), text + "\n");

        var taken = TakeUp(harness, layout, definition);
        var step = Assert.Single(taken.File.Steps);

        Assert.Equal(new FlowHandoffRecord(by, why), step.Handoff);
        Assert.Equal(text, taken.Handoffs["think"]);
    }

    /// <summary>A config whose entry now differs from the one the run began under is refused with a pointer to a new run.</summary>
    [Fact]
    public async Task AConfigWhoseEntryNowDiffersIsRefusedWithAPointerToANewRun()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records);
            await coordinator.RunAsync(TestContext.Current.CancellationToken);
        }

        var raised = Assert.Throws<HarnessException>(() => TakeUp(harness, layout, definition, digest: "another digest entirely"));

        Assert.Equal(HarnessExit.Refused, raised.ExitCode);
        Assert.Equal(
            "workflow 'flow', as the repository's config declares it now, has changed since run 'r1' began, and a run goes on only under the definition it began with; "
            + "run-orchestrator flow starts a new run of it. Nothing of run 'r1' was changed.",
            raised.Message);
    }

    /// <summary>The graph is rebuilt over the items and iterations the records name: a taken-up forEach holds each item's value, and what a finished item wrote is read by what comes after.</summary>
    [Fact]
    public async Task TheGraphIsRebuiltOverTheItemsTheRecordsName()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: round
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: work
                    type: reason
                    prompt: work.md
                    outputs: work.json
            """);
        var inputs = new Dictionary<string, string>();
        var runner = new Runner()
            .Hold("round[1].work")
            .On("plan", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("""{"list": ["a", "b"]}""")));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), inputs: inputs, records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("round[0].work");
            runner.Complete("round[0].work");
            await runner.StartedAsync("round[1].work");

            // The crash, between one item's end and another's.
        }

        var taken = TakeUp(harness, layout, definition);
        var next = new Runner().On("work", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("{\"done\": \"" + run.Item.AsTemplateText() + "\"}")));
        var (_, _, again) = Records("r1", temp.Path, definition);

        string? lastOutput = null;

        using (again)
        {
            var coordinator = new FlowCoordinator(
                definition,
                next,
                new RunnerFacts(),
                inputs: inputs,
                resume: taken,
                records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["round[0].work"].State);
            Assert.Equal(["b"], next.ItemsWorked);

            // The groups the resume took up end as they would have: each item, and the loop over them.
            Assert.Equal(
                [FlowInstanceState.Done, FlowInstanceState.Done, FlowInstanceState.Done],
                new[] { "round[0]", "round[1]", "round" }.Select(id => coordinator.Instances[id].State));
            lastOutput = coordinator.Instances["round[1].work"].Output?["done"]?.GetValue<string>();
        }

        Assert.Equal("b", lastOutput);
    }

    /// <summary>
    /// A hard stop is gone on from by a new engine from the records alone: what it stopped runs again as a new attempt -
    /// a reason step given the handoff the engine writes for it - and what never started runs as drawn.
    /// </summary>
    [Fact]
    public async Task AHardStop_IsGoneOnFrom_WhatItStoppedRunsAgain_AndWhatNeverStartedRunsAsDrawn()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: side
                type: build
              - name: after
                type: orient
                dependsOn: [think, side]
            """);
        var first = new Runner().Hold("think").Hold("side");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, first, new RunnerFacts(), records: records);
            var session = coordinator.RunAsync(Token);
            await first.StartedAsync("think").WaitAsync(Bound, Token);
            await first.StartedAsync("side").WaitAsync(Bound, Token);
            coordinator.RequestStop();

            Assert.Equal(FlowRunStatus.Stopped, (await session.WaitAsync(Bound, Token)).Status);
        }

        string? given = null;
        var next = new Runner().On("think", run =>
        {
            given = run.Handoff;
            return FlowStepResult.Ended(passed: true, 0);
        });
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
            Assert.Equal([2, 2, 1], new[] { "think", "side", "after" }.Select(id => coordinator.Instances[id].Attempt));
        }

        Assert.Equal(["after", "side", "think"], next.Started.Order(StringComparer.Ordinal));
        Assert.Equal("Written by the engine: the run was stopped.\nAttempt 1 was cut off.", given);
    }

    /// <summary>
    /// The agents a run holds open are read back from its records: an agent a create-agent step made and no deletion
    /// deleted still holds its slot after a resume, so a second making waits for one; one a deletion deleted holds none.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheAgentsARunHoldsOpen_AreReadBackFromItsRecords(bool deleted)
    {
        using var temp = new TempDirectory();
        var definition = Parse($"""
            name: flow
            maxOpenAgents: 1
            steps:
              - name: one
                type: create-agent
                agent: one.md
              - name: going
                type: {(deleted ? "delete-agent" : "fold")}
                of: one
                dependsOn: [one]
              - name: holding
                type: pause
                question: go on?
                options: [go, stay]
                dependsOn: [going]
              - name: two
                type: create-agent
                agent: two.md
                dependsOn: [holding]
            """);
        var first = new Runner().On("one", run => FlowStepResult.Ended(passed: true, 0, output: new JsonObject { ["agent"] = "a1" }));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            Assert.Equal(FlowRunStatus.Paused, (await new FlowCoordinator(definition, first, new RunnerFacts(), records: records).RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        var next = new Runner().On("two", run => FlowStepResult.Ended(passed: true, 0, output: new JsonObject { ["agent"] = "a2" }));
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.True(coordinator.TryAnswer("q1", "go", out var problem), problem);

            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            if (deleted)
            {
                Assert.Equal(FlowRunStatus.Done, end.Status);
                Assert.Equal(["two"], next.Started);
            }
            else
            {
                Assert.Equal(FlowRunStatus.Failed, end.Status);
                Assert.Empty(next.Started);
                Assert.Equal("it could not start: it waits on open agents, and nothing left in the run frees it", coordinator.Instances["two"].Error);
            }
        }
    }

    /// <summary>A resume beside an open question is not refused where something can run; it is refused only where nothing can until the question is answered, naming answer-orchestrator.</summary>
    [Fact]
    public async Task AResumeBesideAnOpenQuestionIsRefusedOnlyWhereNothingCanRun()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: asker
                type: reason
                prompt: work.md
                outputs: work.json
              - name: gate
                type: build
            """);
        var runner = new Runner().Asking("asker").Hold("gate");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            var session = coordinator.RunAsync(TestContext.Current.CancellationToken);
            await runner.StartedAsync("asker");
            await runner.StartedAsync("gate");

            // The crash, with the question open and the build still running.
        }

        var taken = TakeUp(harness, layout, definition);
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: taken, records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            // Something beside the question could run - the build the crash cut off - so the resume ran it; the
            // question is still open.
            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["gate"].State);
            Assert.Equal(FlowInstanceState.Paused, coordinator.Instances["asker"].State);
        }

        // Now nothing is left but the question: the resume is refused, naming the way on.
        var besideOnly = Parse("""
            name: flow
            steps:
              - name: asker
                type: reason
                prompt: work.md
                outputs: work.json
            """);
        using var other = new TempDirectory();
        var (harness2, layout2, records2) = Records("r2", other.Path, besideOnly);

        using (records2)
        {
            var coordinator = new FlowCoordinator(besideOnly, new Runner().Asking("asker"), new RunnerFacts(), records: records2);
            var session = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, session.Status);
        }

        var taken2 = TakeUp(harness2, layout2, besideOnly);
        var flowBefore = File.ReadAllText(layout2.FlowFile);
        var statusBefore = File.ReadAllText(layout2.WorkflowStatusFile);
        var eventsBefore = File.ReadAllLines(layout2.EventsFile);
        var (_, _, again2) = Records("r2", other.Path, besideOnly);

        using (again2)
        {
            var coordinator = new FlowCoordinator(besideOnly, new Runner(), new RunnerFacts(), resume: taken2, records: again2);

            var raised = await Assert.ThrowsAsync<HarnessException>(() => coordinator.RunAsync(TestContext.Current.CancellationToken));

            Assert.Equal(HarnessExit.Refused, raised.ExitCode);
            Assert.Equal("nothing of this run can run until the question of step 'asker' is answered; answer-orchestrator answers it", raised.Message);
        }

        // The refusal came before anything of the session was written: no event of it, the run's state and status as
        // the last session left them.
        Assert.Equal(eventsBefore, File.ReadAllLines(layout2.EventsFile));
        Assert.Equal(flowBefore, File.ReadAllText(layout2.FlowFile));
        Assert.Equal(statusBefore, File.ReadAllText(layout2.WorkflowStatusFile));
    }

    /// <summary>
    /// A resume refused writes nothing of what its start changed: a failure a pause left owed is decided by the start -
    /// passed over - and with nothing able to run beside the question, its record still says it is owed, for the next
    /// session to decide.
    /// </summary>
    [Fact]
    public async Task ARefusedResume_WritesNothingItsStartChanged()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: asker
                type: pause
                question: which way?
                options: [left, right]
              - name: gate
                type: build
                onFailure: continue
            """);
        var runner = new Runner().Hold("gate");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var watched = new Watched(records);
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: watched);
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("gate").WaitAsync(Bound, Token);
            await watched.SeenAsync(snapshot => snapshot.WaitingOn.Count == 1).WaitAsync(Bound, Token);
            coordinator.RequestPause("command");
            runner.Fail("gate", "a leg failed");

            Assert.Equal(FlowRunStatus.Paused, (await session.WaitAsync(Bound, Token)).Status);
        }

        var gateBefore = File.ReadAllText(layout.StepRecordFile("gate"));
        var eventsBefore = File.ReadAllLines(layout.EventsFile);
        var (_, _, again) = Records("r1", temp.Path, definition);

        Assert.Equal("owed", harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("gate"))!.Failure);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            var raised = await Assert.ThrowsAsync<HarnessException>(() => coordinator.RunAsync(Token)).WaitAsync(Bound, Token);

            Assert.Equal(HarnessExit.Refused, raised.ExitCode);
            Assert.True(coordinator.Instances["gate"].PassedOver);
        }

        Assert.Equal(gateBefore, File.ReadAllText(layout.StepRecordFile("gate")));
        Assert.Equal(eventsBefore, File.ReadAllLines(layout.EventsFile));
    }

    /// <summary>
    /// An answer offered before a resume begins is taken at its start, before anything is admitted: the resume is not
    /// refused over the question the answer settles, and the run goes on with it.
    /// </summary>
    [Fact]
    public async Task AnAnswerOfferedBeforeAResume_IsTakenAtItsStart_NeverRefused()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records);

            Assert.Equal(FlowRunStatus.Paused, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var offered = coordinator.OfferAnswerAsync("q1", "go");
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(FlowHandedOver.Taken, await offered.WaitAsync(Bound, Token));
        }
    }

    /// <summary>
    /// An answer left unread as a session ends - here one that ends by a write that failed - is handed back cancelled,
    /// never refused and never left waiting: it stays where it was handed over, for whoever takes it next.
    /// </summary>
    [Fact]
    public async Task AnAnswerLeftUnreadAsASessionEnds_IsHandedBackCancelled()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
              - name: gate
                type: build
            """);
        var (_, _, records) = Records("r1", temp.Path, definition);
        FlowCoordinator coordinator = null!;
        Task<FlowHandedOver>? offered = null;

        using (records)
        {
            var failing = new Intercepted(records, (kind, instance, to, _) =>
            {
                if (kind is FlowChangeKind.Step && instance == "gate" && to == nameof(FlowInstanceState.Running))
                {
                    // An answer comes in as the session's write fails: it is on the channel, and the loop never reads
                    // it again.
                    offered = coordinator.OfferAnswerAsync("q1", "go");
                    throw new IOException("the disk is full");
                }
            });
            coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: failing);

            var failed = await Assert.ThrowsAsync<IOException>(() => coordinator.RunAsync(Token)).WaitAsync(Bound, Token);

            Assert.Equal("the disk is full", failed.Message);
        }

        Assert.NotNull(offered);
        Assert.True(offered.IsCanceled);
    }

    /// <summary>
    /// What the run did with an answer handed over is written under its inbox entry's id - the answer taken with it, the
    /// refusal in the run's file of questions before the event that tells of it - so an answer handed over again under an
    /// entry the run recorded, by a later session's poll or its take between two drives, is never weighed again, and
    /// nothing more is written of it.
    /// </summary>
    [Fact]
    public async Task AnAnswerHandedOverUnderAnEntryTheRunRecorded_IsNeverWeighedAgain()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
              - name: closing
                type: pause
                question: is it done?
                options: [yes, no]
                dependsOn: [holding]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var refusedAtEachEvent = new List<IReadOnlyList<FlowRefusedAnswer>?>();

        using (records)
        {
            var told = new Intercepted(records, (kind, _, _, _) =>
            {
                if (kind is FlowChangeKind.Answer)
                {
                    refusedAtEachEvent.Add(harness.OrchestrationStore.ReadFlowQuestions(layout)!.Refused);
                }
            });
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: told);

            Assert.Equal(FlowRunStatus.Paused, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
            Assert.Equal(FlowHandedOver.Taken, coordinator.AnswerHandedOver("q1", "go", null, out var none, inbox: "e-taken"));
            Assert.Null(none);
            Assert.Equal(FlowHandedOver.Refused, coordinator.AnswerHandedOver("q1", "stay", null, out var refusal, inbox: "e-refused"));
            Assert.Equal("'q1' is not a question this run leaves open", refusal);
        }

        var written = harness.OrchestrationStore.ReadFlowQuestions(layout)!;
        var refused = Assert.Single(written.Refused!);

        Assert.Equal(["e-refused"], Assert.Single(refusedAtEachEvent)!.Select(entry => entry.Inbox));
        Assert.Equal(("e-refused", "q1", "stay", "'q1' is not a question this run leaves open"), (refused.Inbox, refused.Question, refused.Answer, refused.Why));
        Assert.Equal("e-taken", Assert.Single(written.Of("q1")!.Answers).By!.Inbox);

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var offered = coordinator.OfferAnswerAsync("q1", "go", inbox: "e-taken");

            Assert.Equal(FlowHandedOver.RecordedAlready, coordinator.AnswerHandedOver("q1", "stay", null, out var said, inbox: "e-refused"));
            Assert.Null(said);
            Assert.Equal(FlowRunStatus.Paused, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
            Assert.Equal(FlowHandedOver.RecordedAlready, await offered.WaitAsync(Bound, Token));
        }

        var after = harness.OrchestrationStore.ReadFlowQuestions(layout)!;
        var (events, _) = FlowEventLog.Read(harness.FileSystem, layout.EventsFile);

        Assert.Equal(["e-refused"], after.Refused!.Select(entry => entry.Inbox));
        Assert.Equal("e-taken", Assert.Single(after.Of("q1")!.Answers).By!.Inbox);
        Assert.Equal(["'q1' is not a question this run leaves open"], events.Where(read => read.Kind == "answer").Select(read => read.Detail));
    }

    /// <summary>
    /// A session an answer began is never refused: the answer was taken, and where another question holds all that is
    /// left the session ends paused on it, as any session does.
    /// </summary>
    [Fact]
    public async Task ASessionAnAnswerBegan_EndsPausedOnTheQuestionLeft_NeverRefused()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: first
                type: pause
                question: which way?
                options: [left, right]
              - name: second
                type: pause
                question: how far?
                options: [near, far]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records);
            var paused = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, paused.Status);
        }

        var taken = TakeUp(harness, layout, definition);
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: taken, records: again);

            Assert.True(coordinator.TryAnswer("q1", "left", out var problem), problem);

            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["first"].State);
            Assert.Equal(["q2"], coordinator.OpenQuestions.Select(question => question.Id));
        }
    }

    /// <summary>
    /// A resume with the budget's question open is never refused, though nothing else can run: it is paused again on
    /// that question, which a person answers.
    /// </summary>
    [Fact]
    public async Task AResumeWithTheBudgetsQuestionOpen_PausesAgain_NeverRefused()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            budget:
              usd: 50
              reservePercent: 10
            steps:
              - name: asker
                type: pause
                question: which way?
                options: [left, right]
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
              - name: gate
                type: build
            """);
        IFlowRunEvents? spending = null;
        var runner = new Runner().On("think", run =>
        {
            spending = run.Events;
            return FlowStepResult.Ended(passed: true, HarnessExit.Success);
        }).Hold("gate");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var watched = new Watched(records);
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: watched);
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("gate").WaitAsync(Bound, Token);
            await watched.SeenAsync(snapshot => snapshot.Steps.Any(step => step.Instance == "think" && step.State == FlowInstanceState.Done)).WaitAsync(Bound, Token);

            // What the reasoning spent is counted once it has ended, so the reserve is reached with nothing left that
            // can run but the build the pause leaves to finish.
            spending!.Used(tokens: 1_000, usd: 45.10);
            await watched.SeenAsync(snapshot => snapshot.Pause is not null).WaitAsync(Bound, Token);
            runner.Complete("gate");
            var first = await session.WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Paused, first.Status);
            Assert.Equal(["pause", "runBudget"], coordinator.OpenQuestions.Select(question => question.Kind));
        }

        var taken = TakeUp(harness, layout, definition);
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: taken, records: again);
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Equal("budget", end.Pause!.Cause);
            Assert.Equal(["q1", "q2"], coordinator.OpenQuestions.Select(question => question.Id));
        }
    }

    /// <summary>
    /// The questions a run raised survive every session: a question answered, the session that answered it stopped, and
    /// a resume that asks again - the answered question is still in the run's file with who answered it and through
    /// what, and the new question takes the id after the highest any question took, never one a record already names.
    /// </summary>
    [Fact]
    public async Task AnsweredQuestionsSurviveEverySession_AndIdsGoOnFromTheHighest()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: approval
                type: pause
                question: go on?
                options: [yes, no]
              - name: work
                type: build
                dependsOn: [approval]
              - name: second
                type: pause
                question: and now?
                options: [yes, no]
                dependsOn: [work]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var end = await new FlowCoordinator(definition, new Runner(), new RunnerFacts(), records: records).RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Paused, end.Status);
        }

        // The answer's own session: the question answered through the command, the build it frees started, and the
        // session stopped while the build runs.
        var held = new Runner().Hold("work");
        var (_, _, answering) = Records("r1", temp.Path, definition);

        using (answering)
        {
            var coordinator = new FlowCoordinator(definition, held, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: answering);

            Assert.True(coordinator.TryAnswer("q1", "yes", null, out _, FlowService.AnswerCommand));

            var session = coordinator.RunAsync(Token);
            await held.StartedAsync("work").WaitAsync(Bound, Token);
            coordinator.RequestStop();

            Assert.Equal(FlowRunStatus.Stopped, (await session.WaitAsync(Bound, Token)).Status);
        }

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Equal("q2", Assert.Single(end.OpenQuestions).Id);
        }

        var questions = harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions;

        Assert.Equal(["q1", "q2"], questions.Select(question => question.Id));
        Assert.Equal(["answered", "open"], questions.Select(question => question.State));
        Assert.Equal(["approval", "second"], questions.Select(question => question.RaisedBy!.Step));

        var answer = Assert.Single(questions[0].Answers);

        Assert.Equal("yes", answer.Answer);
        Assert.Equal(new FlowAnswerBy("person", FlowService.AnswerCommand), answer.By);
        Assert.Empty(questions[1].Answers);
    }

    /// <summary>
    /// A counted answer a configuration gave is spent across sessions as within one: <c>retry</c> given its one time,
    /// the run cut off, and at the resume the same question is given the entry's <c>then</c> - never the counted answer
    /// again, which would let a configuration go on for ever across resumes.
    /// </summary>
    [Fact]
    public async Task ACountedAnswerTheConfigurationSpent_IsNotGivenAgainAfterAResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: fold
                type: fold
                of: made
                answers:
                  foldNewRows:
                    answer: retry
                    times: 1
                    then: fail
              - name: made
                type: create-agent
                agent: worker.md
            """);
        var lacking = FlowStepResult.Standing(new FlowDryRun(RequestStanding.NeedsConsent, [ConsentKind.FoldNewRows], ["row 12"], [], "1 new row"));
        var runner = new Runner().OnAsync("fold", (run, cancellationToken) => run.Attempt == 1
            ? Task.FromResult(lacking)
            : new TaskCompletionSource<FlowStepResult>().Task.WaitAsync(cancellationToken));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            _ = coordinator.RunAsync(Token);
            await runner.StartedAsync("fold", attempt: 2).WaitAsync(Bound, Token);

            // The crash, while the attempt the configured retry made runs.
        }

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner().On("fold", _ => lacking), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);
            var fold = coordinator.Instances["fold"];

            Assert.Equal(FlowRunStatus.Failed, end.Status);
            Assert.Equal(3, fold.Attempt);
            Assert.Equal(FlowInstanceState.Failed, fold.State);
            Assert.Equal("foldNewRows", fold.EndedByQuestion);
        }

        var questions = harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions;

        Assert.Equal(["retry", "fail"], questions.Select(question => Assert.Single(question.Answers).Answer));
        Assert.All(questions, question => Assert.Equal(new FlowAnswerBy("configuration", Where: "step 'fold'"), question.Answers[0].By));
    }

    /// <summary>
    /// A run a failure's stop ended goes on at the resume from its records alone: the failure that ended it runs again as
    /// a new attempt, what it blocked and what the run ended before are drawn again, and the run ends done - never taken
    /// as done over the failure, nor left with work NotRun that the stop alone kept from running.
    /// </summary>
    [Fact]
    public async Task ARunAStopEnded_RunsItsFailureAgainAtTheResume_AndWhatTheStopLeftNotRun()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: orient
                onFailure: stop
              - name: after
                type: orient
                dependsOn: [broke]
              - name: slow
                type: build
              - name: later
                type: orient
                dependsOn: [slow]
            """);
        var runner = new Runner()
            .On("broke", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "it broke"))
            .Hold("slow");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            var session = coordinator.RunAsync(Token);

            // The failure's ending is read before the build's: what the build frees is drawn while the run stops.
            await runner.StartedAsync("slow").WaitAsync(Bound, Token);
            runner.Complete("slow");

            Assert.Equal(FlowRunStatus.Failed, (await session.WaitAsync(Bound, Token)).Status);
        }

        var stood = harness.OrchestrationStore.ReadFlow(layout)!.Steps.ToDictionary(step => step.Instance, StringComparer.Ordinal);

        Assert.Equal(("Failed", "endedRun"), (stood["broke"].State, stood["broke"].Failure));
        Assert.Equal(("NotRun", "DependencyFailed"), (stood["after"].State, stood["after"].NotRunCause));
        Assert.Equal(("NotRun", "RunEnded"), (stood["later"].State, stood["later"].NotRunCause));

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done", end.Summary);
            Assert.Equal(["broke", "later", "after"], next.Started);
            Assert.Equal(2, coordinator.Instances["broke"].Attempt);
        }

        var file = harness.OrchestrationStore.ReadFlow(layout)!;

        Assert.Equal("Done", file.Status);
        Assert.Equal(["broke", "slow", "later", "after"], file.Steps.Select(step => step.Instance));
        Assert.All(file.Steps, step => Assert.Equal(("Done", null, null), (step.State, step.Failure, step.NotRunCause)));
    }

    /// <summary>
    /// A group whose member's stop ended the run stands open over what the stop left - the failure and what it blocked -
    /// and is never ended done over them: the resume runs the member again, draws what it blocked, and ends the group
    /// done once they have.
    /// </summary>
    [Fact]
    public async Task AGroupAStopLeftUnfinished_StandsOpen_AndTheResumeFinishesIt()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                steps:
                  - name: broke
                    type: orient
                    onFailure: stop
                  - name: side
                    type: orient
                    dependsOn: [broke]
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, new Runner().On("broke", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "it broke")), new RunnerFacts(), records: records);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Failed, end.Status);
            Assert.Equal(FlowInstanceState.Running, coordinator.Instances["work"].State);
        }

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal(["work.broke", "work.side", "after"], next.Started);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["work"].State);
        }
    }

    /// <summary>
    /// A failure the run passed over is said so in the step's own record the moment it is decided, so a resume - which
    /// takes the step's own record over the index - reads it passed over: what depends on it runs, and the run is done
    /// with it counted.
    /// </summary>
    [Fact]
    public async Task AFailurePassedOver_IsPassedOverStill_AfterACrash()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: flaky
                type: orient
                onFailure: continue
              - name: slow
                type: build
              - name: after
                type: orient
                dependsOn: [flaky, slow]
            """);
        var runner = new Runner()
            .On("flaky", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "flaked"))
            .Hold("slow");
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var watched = new Watched(records);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: watched);
            _ = coordinator.RunAsync(Token);
            await watched.SeenAsync(snapshot => snapshot.Steps.Any(step => step is { Instance: "flaky", Failure: FlowFailure.PassedOver })).WaitAsync(Bound, Token);

            // The crash, with the build still running.
        }

        Assert.Equal("passedOver", harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("flaky"))!.Failure);

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 step failed and was passed over: flaky", end.Summary);
            Assert.Equal(["flaky"], end.PassedOver);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
        }
    }

    /// <summary>
    /// The answers a step's questions were given are read back with its record: the attempt a resume runs again is
    /// given them, as the attempt the answer made was, and the record keeps them.
    /// </summary>
    [Fact]
    public async Task TheAnswersAStepWasGiven_AreGivenToTheAttemptAResumeRunsAgain()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: fold
                type: fold
                of: made
              - name: made
                type: create-agent
                agent: worker.md
            """);
        var lacking = FlowStepResult.Standing(new FlowDryRun(RequestStanding.NeedsConsent, [ConsentKind.FoldNewRows], ["row 12"], [], "1 new row"));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var end = await new FlowCoordinator(definition, new Runner().On("fold", _ => lacking), new RunnerFacts(), records: records).RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Paused, end.Status);
        }

        var held = new Runner().Hold("fold");
        var (_, _, answering) = Records("r1", temp.Path, definition);

        using (answering)
        {
            var coordinator = new FlowCoordinator(definition, held, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: answering);

            Assert.True(coordinator.TryAnswer("q1", "allow", null, out _, FlowService.AnswerCommand));
            _ = coordinator.RunAsync(Token);
            await held.StartedAsync("fold").WaitAsync(Bound, Token);

            // The crash, while the attempt the answer let go on runs.
        }

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal([new FlowAnswerGiven("q1", "allow")], next.Answers["fold@2"]);
        Assert.Equal([new FlowAnswerGiven("q1", "allow")], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("fold"))!.Answers);
    }

    /// <summary>A work item that is JSON null is read back null, never Missing: the item a resume runs again is the one the run expanded.</summary>
    [Fact]
    public async Task AWorkItemThatIsNull_IsReadBackNull()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: round
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: work
                    type: reason
                    prompt: work.md
                    outputs: work.json
            """);
        var runner = new Runner()
            .Hold("round[0].work")
            .On("plan", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("""{"list": [null, "b"]}""")));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            _ = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records).RunAsync(Token);
            await runner.StartedAsync("round[0].work").WaitAsync(Bound, Token);
            await runner.StartedAsync("round[1].work").WaitAsync(Bound, Token);
        }

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        var (instance, item) = next.Items[0];

        Assert.Equal("round[0].work", instance);
        Assert.Equal((true, false), (item.IsNull, item.IsMissing));
    }

    /// <summary>
    /// A machine step that fails while a pause records what it left running owes its policy to the next session, and
    /// its own record says so: a resume by a new engine makes the policy - here, passing it over - from that record.
    /// </summary>
    [Fact]
    public async Task AFailureOwedByAPause_IsMadeByTheResume_FromItsRecord()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: gate
                type: build
                onFailure: continue
              - name: after
                type: orient
                dependsOn: [gate]
            """);
        var runner = new Runner().Hold("gate");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records);
            var session = coordinator.RunAsync(Token);
            await runner.StartedAsync("gate").WaitAsync(Bound, Token);
            coordinator.RequestPause("command");
            runner.Fail("gate", "a leg failed");

            Assert.Equal(FlowRunStatus.Paused, (await session.WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal("owed", harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("gate"))!.Failure);

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 step failed and was passed over: gate", end.Summary);
            Assert.Equal(["after"], next.Started);
        }

        Assert.Equal("passedOver", harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("gate"))!.Failure);
    }

    /// <summary>
    /// An ending whose handler is at its bound waits on the question the bound asked, its other handlers owed until the
    /// answer: a resume that goes on beside the question raises nothing of it again and asks nothing again, and the
    /// session that answers it takes the ending as the answer says.
    /// </summary>
    [Fact]
    public async Task AnEndingWaitingOnItsBound_IsNotRaisedAgainByALaterSession()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                then: build
                maxVisits: 1
              - name: after
                type: orient
                dependsOn: [build]
              - name: side
                type: build
            """);
        var failing = new Runner().On("build", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red")).Hold("side");
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var watched = new Watched(records);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, failing, new RunnerFacts(), records: watched);
            var session = coordinator.RunAsync(Token);
            await watched.SeenAsync(snapshot => snapshot.WaitingOn.Contains("q1")).WaitAsync(Bound, Token);
            coordinator.RequestStop();

            Assert.Equal(FlowRunStatus.Stopped, (await session.WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal("q1", harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build+2"))!.Question);

        // The resume goes on beside the question - the build the stop stopped runs again - and raises nothing of the
        // ending that waits on it.
        var (_, _, beside) = Records("r1", temp.Path, definition);

        using (beside)
        {
            var next = new Runner();
            var end = await new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: beside).RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Equal(["q1"], end.OpenQuestions.Select(question => question.Id));
            Assert.Equal(["side"], next.Started);
        }

        var (_, _, answering) = Records("r1", temp.Path, definition);

        using (answering)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: answering);

            Assert.True(coordinator.TryAnswer("q1", "accept", null, out _, FlowService.AnswerCommand));

            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("q1", coordinator.Instances["build+2"].HandledBy);
        }

        Assert.Equal(["q1"], harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions.Select(question => question.Id));
    }

    /// <summary>
    /// What raised a handler is read back from the handler's record: a handler a crash cut off runs again at the resume
    /// and, ending, settles the failure it was raised for - which counts as handled, so what depends on it runs.
    /// </summary>
    [Fact]
    public async Task AHandlerACrashCutOff_SettlesItsSourceAtTheResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: build
                on: {step: build, outcome: failure}
              - name: after
                type: orient
                dependsOn: [build]
            """);
        var runner = new Runner().On("build", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red")).Hold("fix");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            _ = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records).RunAsync(Token);
            await runner.StartedAsync("fix").WaitAsync(Bound, Token);

            // The crash, while the handler runs.
        }

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Equal("done; 1 failure was handled: build, by fix", end.Summary);
            Assert.Equal(["fix", "after"], next.Started);
        }
    }

    /// <summary>
    /// A bound answered fail gives up the handlers its ending still owed, in the source's own record: the failure goes to
    /// its own policy, and no later session finds a handler owed and raises it, or asks the bound again.
    /// </summary>
    [Fact]
    public async Task ABoundAnsweredFail_LeavesNothingOwedForALaterSession()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
                onFailure: continue
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                then: build
                maxVisits: 1
            """);
        var failing = new Runner().On("build", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red"));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            Assert.Equal(FlowRunStatus.Paused, (await new FlowCoordinator(definition, failing, new RunnerFacts(), records: records).RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        var (_, _, answering) = Records("r1", temp.Path, definition);

        using (answering)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: answering);

            Assert.True(coordinator.TryAnswer("q1", "fail", null, out _, FlowService.AnswerCommand));
            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal([new FlowHandlerState("fix", "passed")], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("build+2"))!.Handlers);

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var end = await new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again).RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Empty(next.Started);
        }

        Assert.Equal(["q1"], harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions.Select(question => question.Id));
    }

    /// <summary>
    /// A member's failure its group took is said in the member's own record: a crash while the group's other members
    /// run leaves the group open, and the resume ends it failed once they have ended - never done over the failure.
    /// </summary>
    [Fact]
    public async Task AFailureAGroupTook_FailsTheGroupStill_AfterACrash()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                steps:
                  - name: gate
                    type: orient
                  - name: slow
                    type: build
              - name: after
                type: orient
                dependsOn: [work]
            """);
        var runner = new Runner()
            .On("gate", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "it broke"))
            .Hold("slow");
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var watched = new Watched(records);

        using (records)
        {
            _ = new FlowCoordinator(definition, runner, new RunnerFacts(), records: watched).RunAsync(Token);
            await watched.SeenAsync(snapshot => snapshot.Steps.Any(step => step is { Instance: "work.gate", Failure: FlowFailure.TakenByGroup })).WaitAsync(Bound, Token);

            // The crash, with the group's other member still running.
        }

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Failed, end.Status);
            Assert.Equal(["work"], end.Failed);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["work.slow"].State);
            Assert.Equal(FlowInstanceState.NotRun, coordinator.Instances["after"].State);
        }
    }

    /// <summary>
    /// Every redirect the run owes is in its records, in the order decided - not the first alone - and a resume by a new
    /// engine makes each in its turn: the second, queued behind the first, is made once the first is.
    /// </summary>
    [Fact]
    public async Task EveryRedirectOwed_IsWrittenAndMadeInItsTurn_ByAResume()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: two
                type: orient
              - name: round
                steps:
                  - name: member
                    type: build
              - name: tail
                type: orient
              - name: mendone
                type: orient
                on: {step: one, outcome: failure}
                then: round
              - name: mendtwo
                type: orient
                on: {step: two, outcome: failure}
                then: tail
            """);
        var runner = new Runner()
            .On("one", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red"))
            .On("two", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red"))
            .Hold("round.member");
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var watched = new Watched(records);

        using (records)
        {
            var coordinator = new FlowCoordinator(definition, runner, new RunnerFacts(), records: watched);
            var session = coordinator.RunAsync(Token);
            await watched.SeenAsync(snapshot => snapshot.Redirects is { Count: 2 }).WaitAsync(Bound, Token);
            coordinator.RequestPause("command");
            runner.Complete("round.member");

            Assert.Equal(FlowRunStatus.Paused, (await session.WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal(["mendone", "mendtwo"], harness.OrchestrationStore.ReadFlow(layout)!.Redirects!.Select(redirect => redirect.Handler));

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
            Assert.Equal(["round+2.member", "tail+2"], next.Started);
        }

        Assert.Null(harness.OrchestrationStore.ReadFlow(layout)!.Redirects);
    }

    /// <summary>
    /// The issues a step reported while the run stopped are owed in its own record, and the issue a triage instance was
    /// raised for is in that instance's record: a resume by a new engine raises the owed triage, and one a crash cut off
    /// runs again with its issue.
    /// </summary>
    [Fact]
    public async Task IssuesOwedAndTheIssueATriageTakes_AreReadBackFromTheRecords()
    {
        using var temp = new TempDirectory();
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
                type: orient
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
        var issue = new FlowPreExistingReport("a broken test", "what it is", "src/Parser.cs", "read the file and saw it", false);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            // The failure ends the run as the issue is read: its triage is owed.
            var first = new Runner()
                .On("broke", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "it broke"))
                .On("think", run => FlowStepResult.Ended(passed: true, 0, preExisting: [issue]));

            Assert.Equal(FlowRunStatus.Failed, (await new FlowCoordinator(definition, first, new RunnerFacts(), records: records).RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal([issue], harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("think"))!.IssuesOwed);

        var held = new Runner().Hold("triage");
        var (_, _, raising) = Records("r1", temp.Path, definition);

        using (raising)
        {
            _ = new FlowCoordinator(definition, held, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: raising).RunAsync(Token);
            await held.StartedAsync("triage").WaitAsync(Bound, Token);

            // The crash, while the owed triage runs.
        }

        Assert.Null(harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("think"))!.IssuesOwed);
        Assert.Equal(issue, harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("triage"))!.Issue);

        var next = new Runner().On("triage", run => FlowStepResult.Ended(passed: true, 0, output: new JsonObject
        {
            ["course"] = "unproven",
            ["title"] = run.Issue!.Title,
            ["description"] = "what it is",
            ["why"] = "not related to the work",
            ["where"] = new JsonArray("src/Parser.cs"),
            ["proof"] = new JsonObject { ["kind"] = "read", ["at"] = "abc123", ["file"] = "src/Parser.cs", ["lines"] = "10-14", ["quote"] = "the broken thing" },
        }));
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            Assert.Equal(FlowRunStatus.Done, (await new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again).RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal([("broke", null), ("triage", "a broken test")], held.Issues);
        Assert.Equal([("triage", "a broken test")], next.Issues);
    }

    /// <summary>
    /// A refusal whose only obstacle was a held lock keeps its cause in the step's own record: the run that ends on it,
    /// its retries spent, ends incomplete (21) from the records alone, and a resume reads the cause back.
    /// </summary>
    [Fact]
    public async Task ARefusalOverAHeldLock_KeepsItsCauseInItsRecord_AndTheRunEndsIncomplete()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: holding
                type: ci-read
            """);
        var runner = new Runner().On("holding", run => new FlowStepResult { Asked = null, Passed = false, Exit = HarnessExit.Refused, Cause = OutcomeCause.LockHeld });
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        FlowSessionEnd end;

        using (records)
        {
            end = await new FlowCoordinator(definition, runner, new RunnerFacts(), new JumpingClock(), records: records).RunAsync(Token).WaitAsync(Bound, Token);
        }

        var flow = harness.OrchestrationStore.ReadFlow(layout)!;

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal(nameof(OutcomeCause.LockHeld), harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("holding"))!.Cause);
        Assert.Equal(HarnessExit.Incomplete, FlowService.ExitOf(end, flow));

        var coordinator = new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition));

        Assert.Equal(OutcomeCause.LockHeld, coordinator.Instances["holding"].Cause);
    }

    /// <summary>
    /// The issue a triage lists is in its end's one write, in the one order - its record, then the run's state and
    /// status, then the run's file of issues: a crash before that file leaves the triage's end written, and a resume
    /// lists the issue from it, once; a resume after it lists nothing again.
    /// </summary>
    [Fact]
    public async Task AnIssueATriageListed_IsInItsEndsOneWrite_AndListedOnceByAResume()
    {
        using var temp = new TempDirectory();
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
        var runner = new Runner()
            .On("think", run => FlowStepResult.Ended(passed: true, 0, preExisting: [new FlowPreExistingReport("a broken test", "what it is", "src/Parser.cs", "read the file and saw it", false)]))
            .On("triage", run => FlowStepResult.Ended(passed: true, 0, output: new JsonObject
            {
                ["course"] = "list",
                ["title"] = run.Issue!.Title,
                ["description"] = "what it is",
                ["why"] = "not related to the work",
                ["where"] = new JsonArray("src/Parser.cs"),
                ["proof"] = new JsonObject { ["kind"] = "read", ["at"] = "abc123", ["file"] = "src/Parser.cs", ["lines"] = "10-14", ["quote"] = "the broken thing" },
            }));
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var none = File.ReadAllText(layout.PreExistingIssuesFile);
        IReadOnlyList<PreExistingIssue>? atTheEnd = null;

        using (records)
        {
            var watched = new Intercepted(records, (kind, instance, to, snapshot) =>
            {
                if (kind is FlowChangeKind.Step && instance == "triage" && to == nameof(FlowInstanceState.Done))
                {
                    atTheEnd = snapshot.Issues;
                }
            });

            Assert.Equal(FlowRunStatus.Done, (await new FlowCoordinator(definition, runner, new RunnerFacts(), records: watched).RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        Assert.Equal(["a broken test"], atTheEnd!.Select(issue => issue.Title));

        // The crash, between the triage's record and the run's file of issues, written after it in the same change.
        File.WriteAllText(layout.PreExistingIssuesFile, none);

        for (var resume = 0; resume < 2; resume++)
        {
            var (_, _, again) = Records("r1", temp.Path, definition);

            using (again)
            {
                Assert.Equal(FlowRunStatus.Done, (await new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again).RunAsync(Token).WaitAsync(Bound, Token)).Status);
            }

            var listed = harness.OrchestrationStore.ReadPreExistingIssues(layout)!.Issues;

            Assert.Equal([("pe-1", "a broken test", "think")], listed.Select(issue => (issue.Id, issue.Title, issue.FoundBy)));
        }
    }

    /// <summary>
    /// An issue a triage answered fix for whose recovery was never raised - a crash came between the triage's end and
    /// the raise - is given to the recovery by the resume, from the triage's record, and counted fixed once it is.
    /// </summary>
    [Fact]
    public async Task AFixWhoseRecoveryWasNeverRaised_IsGivenToItByAResume()
    {
        using var temp = new TempDirectory();
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
              - name: tell
                type: pause
                always: true
                question: 'what was there before: {run.preExisting}'
                options: [ok, no]
              - name: triage
                type: reason
                prompt: triage.md
                outputs: pre-existing.json
                raised: preExisting
                onAddress: recover
            """);
        var runner = new Runner()
            .On("think", run => FlowStepResult.Ended(passed: true, 0, preExisting: [new FlowPreExistingReport("a broken test", "what it is", "src/Parser.cs", "read the file and saw it", false)]))
            .On("triage", run => FlowStepResult.Ended(passed: true, 0, output: new JsonObject
            {
                ["course"] = "fix",
                ["title"] = run.Issue!.Title,
                ["description"] = "what it is",
                ["why"] = "not related to the work",
                ["where"] = new JsonArray("src/Parser.cs"),
                ["proof"] = new JsonObject { ["kind"] = "read", ["at"] = "abc123", ["file"] = "src/Parser.cs", ["lines"] = "10-14", ["quote"] = "the broken thing" },
            }));
        var (harness, layout, records) = Records("r1", temp.Path, definition);
        var cut = new Cut(records, int.MaxValue);
        var triageDone = 0;

        using (records)
        {
            // How many writes the run makes up to the triage's end, the write that records it among them.
            var counted = new Intercepted(cut, (kind, instance, to, _) =>
            {
                if (kind is FlowChangeKind.Step && instance == "triage" && to == nameof(FlowInstanceState.Done))
                {
                    triageDone = cut.Made + 1;
                }
            });

            _ = await new FlowCoordinator(definition, runner, new RunnerFacts(), records: counted).RunAsync(Token).WaitAsync(Bound, Token);
        }

        Assert.True(triageDone > 0, "the triage ended done");

        // The same run, cut right after the triage's end: the recovery it asks for is never raised.
        using var crashed = new TempDirectory();
        var (crashedHarness, crashedLayout, crashedRecords) = Records("r1", crashed.Path, definition);

        using (crashedRecords)
        {
            _ = await new FlowCoordinator(definition, runner, new RunnerFacts(), records: new Cut(crashedRecords, triageDone)).RunAsync(Token).WaitAsync(Bound, Token);
        }

        Assert.Equal(nameof(FlowInstanceState.Done), crashedHarness.OrchestrationStore.ReadStepRecord(crashedLayout.StepRecordFile("triage"))!.State);
        Assert.Null(crashedHarness.OrchestrationStore.ReadFlow(crashedLayout)!.Steps.FirstOrDefault(step => step.Instance == "recover"));

        var next = new Runner();
        var (_, _, again) = Records("r1", crashed.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(crashedHarness, crashedLayout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Paused, end.Status);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["recover"].State);
            Assert.Equal(
                "what was there before: fixed - a broken test (src/Parser.cs)",
                coordinator.OpenQuestions.Single(question => question.Kind == "pause").Problem);
        }

        Assert.Equal(["recover.mend"], next.Started);
    }

    /// <summary>
    /// Which visit an instance is, is read from its record - never worked out again from its id: the recovery's visits
    /// are counted for each failure it takes, and a resume holds each failure to the recovery's bound by its own count.
    /// </summary>
    [Fact]
    public async Task TheRecoverysVisits_AreCountedForEachFailure_AcrossACrash()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            recover:
              maxVisits: 2
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: mend
                  type: build
            steps:
              - name: first
                type: orient
                onFailure: recover
              - name: second
                type: orient
                onFailure: recover
                dependsOn: [first]
            """);

        // Each fails on its first visit; the second fails on its second too.
        Runner Scripted()
            => new Runner()
                .On("first", run => run.Visit == 1 ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red") : FlowStepResult.Ended(passed: true, 0))
                .On("second", run => run.Visit <= 2 ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red") : FlowStepResult.Ended(passed: true, 0));

        var runner = Scripted().Hold("recover+2.mend");
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            _ = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records).RunAsync(Token);
            await runner.StartedAsync("recover+2.mend").WaitAsync(Bound, Token);

            // The crash, while the recovery's first visit for the second failure runs.
        }

        Assert.Equal(1, harness.OrchestrationStore.ReadStepRecord(layout.StepRecordFile("recover+2"))!.Visit);

        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, Scripted(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            // The second failure of 'second' is the recovery's second visit for it - within its bound - and never a third.
            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.Empty(end.OpenQuestions);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["second+3"].State);
        }
    }

    /// <summary>
    /// The records are taken in the order the run made them, never sorted again: with ten visits and more of a step,
    /// the newest is still the one its name reads, and what depends on it reads that visit.
    /// </summary>
    [Fact]
    public async Task TenVisitsAndMore_AreTakenUpInTheOrderTheRunMadeThem()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                then: build
                maxVisits: 12
              - name: after
                type: orient
                dependsOn: [build]
                when: build.code == 0
            """);
        var runner = new Runner().OnAsync("build", (run, cancellationToken) => run.Visit switch
        {
            <= 10 => Task.FromResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red")),
            _ => new TaskCompletionSource<FlowStepResult>().Task.WaitAsync(cancellationToken),
        });
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            _ = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records).RunAsync(Token);
            await runner.StartedAsync("build+11").WaitAsync(Bound, Token);

            // The crash, while the eleventh visit runs.
        }

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
            Assert.Equal(["build+11", "after"], next.Started);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["after"].State);
        }
    }

    /// <summary>
    /// A repeat's iterations are taken up in the order the run made them: with ten and more, the newest is the one its
    /// until reads, and the loop goes on from it - never from the ninth, which an ordinal sort puts last.
    /// </summary>
    [Fact]
    public async Task TenIterationsAndMore_GoOnFromTheNewest()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: attempt.ok
                  maxIterations: 12
                steps:
                  - name: attempt
                    type: orient
            """);
        var runner = new Runner().OnAsync("attempt", (run, cancellationToken) => run.Iteration switch
        {
            <= 10 => Task.FromResult(FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("""{"ok": false}"""))),
            _ => new TaskCompletionSource<FlowStepResult>().Task.WaitAsync(cancellationToken),
        });
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            _ = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records).RunAsync(Token);
            await runner.StartedAsync("rounds#11.attempt").WaitAsync(Bound, Token);

            // The crash, while the eleventh iteration runs.
        }

        var next = new Runner().On("attempt", run => FlowStepResult.Ended(passed: true, 0, output: JsonNode.Parse("""{"ok": true}""")));
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
            Assert.Equal(["rounds#11.attempt"], next.Started);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["rounds"].State);
        }
    }

    /// <summary>
    /// A loop the run was sent back to is its own visit - rounds+2, a member like any other, never an iteration of
    /// rounds - and its iterations are taken up under it.
    /// </summary>
    [Fact]
    public async Task ALoopVisitedAgain_IsTakenUpAsItsOwnVisit()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: attempt.ok
                  maxIterations: 3
                steps:
                  - name: attempt
                    type: orient
              - name: verify
                type: orient
                dependsOn: [rounds]
              - name: mend
                type: orient
                on: {step: verify, outcome: failure}
                then: rounds
            """);
        var ok = JsonNode.Parse("""{"ok": true}""");
        var runner = new Runner()
            .On("verify", run => run.Visit == 1 ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red") : FlowStepResult.Ended(passed: true, 0))
            .OnAsync("attempt", (run, cancellationToken) => run.Instance == "rounds#1.attempt"
                ? Task.FromResult(FlowStepResult.Ended(passed: true, 0, output: ok))
                : new TaskCompletionSource<FlowStepResult>().Task.WaitAsync(cancellationToken));
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            _ = new FlowCoordinator(definition, runner, new RunnerFacts(), records: records).RunAsync(Token);
            await runner.StartedAsync("rounds+2#1.attempt").WaitAsync(Bound, Token);

            // The crash, while the loop's second visit runs its first iteration.
        }

        var next = new Runner()
            .On("attempt", run => FlowStepResult.Ended(passed: true, 0, output: ok))
            .On("verify", run => FlowStepResult.Ended(passed: true, 0));
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
            Assert.Equal(["rounds+2#1.attempt", "verify+2"], next.Started);
            Assert.Equal(FlowInstanceState.Done, coordinator.Instances["rounds+2"].State);
        }
    }

    /// <summary>
    /// A block that failed holds the records of its members, and they are taken up in its scope: the failure its member
    /// handed it ended the run, and the resume runs the block again as a new visit, whole.
    /// </summary>
    [Fact]
    public async Task AFailedBlocksMembers_AreTakenUp_AndTheBlockRunsAgain()
    {
        using var temp = new TempDirectory();
        var definition = Parse("""
            name: flow
            steps:
              - name: work
                steps:
                  - name: gate
                    type: orient
                  - name: side
                    type: orient
            """);
        var (harness, layout, records) = Records("r1", temp.Path, definition);

        using (records)
        {
            var first = new Runner().On("gate", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "it broke"));

            Assert.Equal(FlowRunStatus.Failed, (await new FlowCoordinator(definition, first, new RunnerFacts(), records: records).RunAsync(Token).WaitAsync(Bound, Token)).Status);
        }

        var next = new Runner();
        var (_, _, again) = Records("r1", temp.Path, definition);

        using (again)
        {
            var coordinator = new FlowCoordinator(definition, next, new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again);

            Assert.Equal(FlowRunStatus.Done, (await coordinator.RunAsync(Token).WaitAsync(Bound, Token)).Status);
            Assert.Equal(["work+2.gate", "work+2.side"], next.Started);
            Assert.Equal(FlowFailure.TakenByGroup, coordinator.Instances["work.gate"].Failure);
        }
    }

    /// <summary>A record's state is read by the engine's one strict reader of its names: one it does not have is refused, never taken for a step that runs again.</summary>
    [Fact]
    public void AStateTheEngineDoesNotHave_IsRefused_NeverTakenForOneThatRunsAgain()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var state = new FlowResumeState(
            new FlowFile(1, "r1", "flow", FlowRunContext.DigestOf(definition), new Dictionary<string, string>(), "Done", new FlowBudgetRecord(0, 0, 0), 0, [new FlowStepRecord("one", "one", false, "Finished", 1, 1, [], [], "orient")], [], DateTimeOffset.UnixEpoch),
            [],
            [],
            new Dictionary<string, string>(),
            [],
            []);

        var refused = Assert.Throws<InvalidOperationException>(() => new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: state));

        Assert.Equal("'Finished' is not one of the FlowInstanceState names a run's records hold: NotStarted, Running, Done, Skipped, Failed, Paused, Stopped, NotRun.", refused.Message);
    }

    /// <summary>
    /// A resume never ends done over a step it never drew: a record of a failure nothing decided - which this engine never
    /// leaves, for a failure is recorded owed until something decides it - leaves what depends on it waiting, and the
    /// session says so, failed.
    /// </summary>
    [Fact]
    public async Task AResumeNeverEndsDone_OverAStepItNeverDrew()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: broke
                type: orient
              - name: after
                type: orient
                dependsOn: [broke]
            """);
        var state = new FlowResumeState(
            new FlowFile(1, "r1", "flow", FlowRunContext.DigestOf(definition), new Dictionary<string, string>(), "Failed", new FlowBudgetRecord(0, 0, 0), 0, [new FlowStepRecord("broke", "broke", false, "Failed", 1, 1, [], [], "orient", Exit: HarnessExit.CommandFailed, Error: "it broke")], [], DateTimeOffset.UnixEpoch),
            [],
            [],
            new Dictionary<string, string>(),
            [],
            []);

        var end = await new FlowCoordinator(definition, new Runner(), new RunnerFacts(), resume: state).RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Failed, end.Status);
        Assert.Equal("failed; the session ended with steps that never ended: after", end.Summary);
    }

    /// <summary>
    /// A crash at every cut point of the coordinator's writes - after each record it hands its records, whole - leaves
    /// records a resume goes on from as the run would have gone on: a failure a handler takes and sends back, one passed
    /// over, a limit the configuration answers more once, and what depends on them. Whichever write was the last, the
    /// resume ends the run done, every step where the run uncut left it, the configured answer given once.
    /// </summary>
    [Fact]
    public async Task ACrashAtEveryCutPoint_IsGoneOnFromAsTheRunWouldHaveGoneOn()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: build
                type: orient
              - name: fix
                type: orient
                on: {step: build, outcome: failure}
                then: build
              - name: flaky
                type: orient
                onFailure: continue
              - name: think
                type: reason
                prompt: work.md
                outputs: work.json
                answers:
                  turnLimit:
                    answer: more
                    times: 1
                    then: fail
              - name: after
                type: orient
                dependsOn: [build, flaky, think]
            """);

        Runner Scripted()
            => new Runner()
                .On("build", run => run.Visit == 1 ? FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "red") : FlowStepResult.Ended(passed: true, 0))
                .On("flaky", run => FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, "flaked"))
                .On("think", run => run.Answers.Count == 0 ? FlowStepResult.AtLimit("turns") : FlowStepResult.Ended(passed: true, 0));

        async Task<(FlowSessionEnd End, IReadOnlyList<FlowStepRecord> Steps, IReadOnlyList<FlowQuestionEntry> Questions, int Writes)> RunCutAsync(int writes)
        {
            using var temp = new TempDirectory();
            var (harness, layout, records) = Records("r1", temp.Path, definition);
            var cut = new Cut(records, writes);

            using (records)
            {
                // The run as far as the cut lets its records go: the engine goes on in memory, and nothing it does after
                // the cut reaches the disk - as nothing does once its process has ended.
                _ = await new FlowCoordinator(definition, Scripted(), new RunnerFacts(), records: cut).RunAsync(Token).WaitAsync(Bound, Token);
            }

            if (writes == int.MaxValue)
            {
                return (null!,harness.OrchestrationStore.ReadFlow(layout)!.Steps, harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions, cut.Made);
            }

            var (_, _, again) = Records("r1", temp.Path, definition);

            using (again)
            {
                var end = await new FlowCoordinator(definition, Scripted(), new RunnerFacts(), resume: TakeUp(harness, layout, definition), records: again).RunAsync(Token).WaitAsync(Bound, Token);
                return (end, harness.OrchestrationStore.ReadFlow(layout)!.Steps, harness.OrchestrationStore.ReadFlowQuestions(layout)!.Questions, cut.Made);
            }
        }

        // Each step as it stands, in instance order, with the answers the run's questions were given and the status the
        // session ended with: one line, so a cut that leaves anything else says which write it was.
        static string Standing(FlowSessionEnd? end, IReadOnlyList<FlowStepRecord> steps, IReadOnlyList<FlowQuestionEntry> questions)
            => $"{end?.Status.ToString() ?? "Done"} | "
                + string.Join(", ", steps.OrderBy(step => step.Instance, StringComparer.Ordinal).Select(step => $"{step.Instance} {step.State} {step.Failure} {step.HandledBy}"))
                + " | " + string.Join(", ", questions.Select(question => $"{question.Id} {question.State} {string.Join("/", question.Answers.Select(answer => answer.Answer))}"));

        var uncut = await RunCutAsync(int.MaxValue);
        var expected = Standing(null, uncut.Steps, uncut.Questions);

        Assert.Equal("Done | after Done  , build Failed  fix, build+2 Done  , fix Done  , flaky Failed passedOver , think Done   | q1 answered more", expected);

        for (var writes = 1; writes < uncut.Writes; writes++)
        {
            var (end, steps, questions, _) = await RunCutAsync(writes);

            var actual = Standing(end, steps, questions);

            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                Assert.Fail($"a crash after write {writes} of {uncut.Writes} was gone on from otherwise than the run would have gone on:\nexpected {expected}\nactual   {actual}");
            }
        }
    }

    /// <summary>The run's records, cut: the writes up to the cut reach the disk, and none after it - a process ended there.</summary>
    private sealed class Cut(IFlowRecords inner, int writes) : IFlowRecords
    {
        private int _made;

        /// <summary>How many writes the run handed its records, cut or not.</summary>
        public int Made => _made;

        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            if (Interlocked.Increment(ref _made) <= writes)
            {
                inner.Write(kind, snapshot, instance, from, to, attempt, detail);
            }
        }

        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
        {
            if (Interlocked.Increment(ref _made) <= writes)
            {
                inner.Question(questions, refused);
            }
        }
    }

    /// <summary>The run's records, each change shown to the test before it is written through - which may fail it there.</summary>
    private sealed class Intercepted(IFlowRecords inner, Action<FlowChangeKind, string?, string?, FlowSnapshot> before) : IFlowRecords
    {
        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            before(kind, instance, to, snapshot);
            inner.Write(kind, snapshot, instance, from, to, attempt, detail);
        }

        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
            => inner.Question(questions, refused);
    }

    /// <summary>The run's records, watched: each change is written through, and a test waits on the change it times.</summary>
    private sealed class Watched(IFlowRecords inner) : IFlowRecords
    {
        private readonly List<(Func<FlowSnapshot, bool> Holds, TaskCompletionSource Seen)> _waits = [];
        private readonly List<FlowSnapshot> _written = [];

        /// <summary>Completes once a change is written - or has been - whose snapshot <paramref name="holds"/> says.</summary>
        public Task SeenAsync(Func<FlowSnapshot, bool> holds)
        {
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_waits)
            {
                if (_written.Any(holds))
                {
                    seen.TrySetResult();
                }
                else
                {
                    _waits.Add((holds, seen));
                }
            }

            return seen.Task;
        }

        public void Write(FlowChangeKind kind, FlowSnapshot snapshot, string? instance = null, string? from = null, string? to = null, int? attempt = null, string? detail = null)
        {
            inner.Write(kind, snapshot, instance, from, to, attempt, detail);

            lock (_waits)
            {
                _written.Add(snapshot);

                foreach (var (holds, seen) in _waits.Where(wait => wait.Holds(snapshot)))
                {
                    seen.TrySetResult();
                }
            }
        }

        public void Question(IReadOnlyList<FlowQuestionEntry> questions, IReadOnlyList<FlowRefusedAnswer>? refused)
            => inner.Question(questions, refused);
    }

    /// <summary>The owner of a run is claimed at the start of a session: a live owner refuses it, a dead one is reclaimed and said, and the run is released where its session leaves it.</summary>
    [Fact]
    public void TheOwnerIsClaimed_ALiveOwnerRefusesAndADeadOneIsReclaimed()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var owner = new FlowRunOwner(harness.OrchestrationStore, harness.Identity);

        _ = owner.Claim(layout);

        var held = harness.OrchestrationStore.ReadOwner(layout);

        Assert.NotNull(held);
        Assert.Equal(Environment.ProcessId, held.ProcessId);

        var identity = Substitute.For<IProcessIdentity>();

        // A live owner: another process that still stands.
        identity.CurrentMachine.Returns(held!.Machine);
        identity.IsAlive(Arg.Any<int>(), Arg.Any<string?>()).Returns(true);

        var refused = Assert.Throws<HarnessException>(() => new FlowRunOwner(harness.OrchestrationStore, identity).Claim(layout));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("owned by a live engine", refused.Message, StringComparison.Ordinal);

        // A dead one: reclaimed and said.
        identity.IsAlive(Arg.Any<int>(), Arg.Any<string?>()).Returns(false);
        identity.CurrentId.Returns(99);
        identity.Current.Returns((string?)"stamp");
        var reclaimed = new FlowRunOwner(harness.OrchestrationStore, identity).Claim(layout);

        Assert.NotNull(reclaimed);
        Assert.Contains("reclaimed", reclaimed, StringComparison.Ordinal);
        Assert.Equal(99, harness.OrchestrationStore.ReadOwner(layout)!.ProcessId);

        new FlowRunOwner(harness.OrchestrationStore, identity).Release(layout);

        Assert.Null(harness.OrchestrationStore.ReadOwner(layout));
    }

    /// <summary>A runner that answers each step as its test scripted it, holding open the steps the test wants to time.</summary>
    private sealed class Runner : IFlowStepRunner
    {
        private readonly Dictionary<string, TaskCompletionSource<FlowStepResult>> _gates = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _starts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<FlowStepRun, FlowStepResult>> _scripts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Func<FlowStepRun, CancellationToken, Task<FlowStepResult>>> _asyncScripts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _asking = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in order.</summary>
        public List<string> Started { get; } = [];

        /// <summary>The handoff each attempt was given, by attempt number from 1.</summary>
        public Dictionary<int, string?> Given { get; } = new();

        /// <summary>Each item a work step saw, in order.</summary>
        public List<string> ItemsWorked { get; } = [];

        /// <summary>The item each instance was given, as the run holds it, in the order asked.</summary>
        public List<(string Instance, FlowValue Item)> Items { get; } = [];

        /// <summary>The answers each attempt was given, by instance and attempt.</summary>
        public Dictionary<string, IReadOnlyList<FlowAnswerGiven>> Answers { get; } = new(StringComparer.Ordinal);

        /// <summary>The pre-existing issue each instance was given to triage, in the order asked; null for every other step.</summary>
        public List<(string Instance, string? Issue)> Issues { get; } = [];

        public Task StartedAsync(string instance)
            => StartOf(instance).Task;

        /// <summary>Completes once the runner has been asked for <paramref name="attempt"/> of <paramref name="instance"/>.</summary>
        public Task StartedAsync(string instance, int attempt)
            => StartOf($"{instance}@{attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}").Task;

        /// <summary>Answers <paramref name="step"/> by its script, which sees the whole run and may hold it on a task of its own.</summary>
        public Runner OnAsync(string step, Func<FlowStepRun, CancellationToken, Task<FlowStepResult>> script)
        {
            _asyncScripts[step] = script;
            return this;
        }

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

        private TaskCompletionSource<FlowStepResult> Gate(string instance)
        {
            lock (_gates)
            {
                if (!_gates.TryGetValue(instance, out var gate))
                {
                    gate = new TaskCompletionSource<FlowStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _gates.Add(instance, gate);
                }

                return gate;
            }
        }

        public Runner Hold(string step)
        {
            _ = Gate(step);
            return this;
        }

        public Runner On(string step, Func<FlowStepRun, FlowStepResult> script)
        {
            _scripts[step] = script;
            return this;
        }

        public Runner Asking(string step)
        {
            _ = _asking.Add(step);
            return this;
        }

        public void Complete(string instance)
            => Gate(instance).TrySetResult(FlowStepResult.Ended(passed: true, 0));

        /// <summary>Ends the held <paramref name="instance"/> failed, with <paramref name="error"/>.</summary>
        public void Fail(string instance, string error)
            => Gate(instance).TrySetResult(FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, error));

        public Runner HandsOff(string step, string account)
        {
            _handoffs[step] = account;
            return this;
        }

        private readonly Dictionary<string, string?> _handoffs = new(StringComparer.Ordinal);

        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (_starts)
            {
                Started.Add(run.Instance);
                Items.Add((run.Instance, run.Item));
                Issues.Add((run.Instance, run.Issue?.Title));
                Answers[$"{run.Instance}@{run.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}"] = run.Answers;
                StartOf(run.Instance).TrySetResult();
                StartOf($"{run.Instance}@{run.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)}").TrySetResult();
                Given[run.Attempt] = run.Handoff;

                if (run.Step.Name == "work")
                {
                    ItemsWorked.Add(run.Item.AsTemplateText());
                }
            }

            if (_asyncScripts.TryGetValue(run.Step.Name, out var held))
            {
                return held(run, cancellationToken);
            }

            if (_asking.Contains(run.Step.Name))
            {
                return Task.FromResult(FlowStepResult.AtLimit("turns"));
            }

            if (_scripts.TryGetValue(run.Step.Name, out var script))
            {
                return Task.FromResult(script(run));
            }

            if (_gates.TryGetValue(run.Instance, out var gate) || _gates.TryGetValue(run.Step.Name, out gate))
            {
                return gate.Task.WaitAsync(cancellationToken);
            }

            return Task.FromResult(FlowStepResult.Ended(passed: true, 0));
        }

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            lock (_handoffs)
            {
                return Task.FromResult(_handoffs.TryGetValue(run.Step.Name, out var account) ? account : null);
            }
        }
    }

    /// <summary>The facts every run of these tests asks of its runners.</summary>
    private sealed class RunnerFacts : IFlowRunnerFacts
    {
        public bool WritesTree(string runner) => false;
    }
}
