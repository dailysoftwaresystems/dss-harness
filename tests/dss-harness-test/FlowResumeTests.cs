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

        Assert.Contains("the engine's process ended", next.Given[2], StringComparison.Ordinal);
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
    [InlineData("Written by the engine: the run was stopped; attempt 1 was cut off.", "engine", "the run was stopped; attempt 1 was cut off")]
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
            lastOutput = coordinator.Instances["round[1].work"].Output?["done"]?.GetValue<string>();
        }

        Assert.Equal("b", lastOutput);
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
        var (_, _, again2) = Records("r2", other.Path, besideOnly);

        using (again2)
        {
            var coordinator = new FlowCoordinator(besideOnly, new Runner(), new RunnerFacts(), resume: taken2, records: again2);

            var raised = await Assert.ThrowsAsync<HarnessException>(() => coordinator.RunAsync(TestContext.Current.CancellationToken));

            Assert.Equal(HarnessExit.Refused, raised.ExitCode);
            Assert.Contains("answer-orchestrator", raised.Message, StringComparison.Ordinal);
        }
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
        private readonly HashSet<string> _asking = new(StringComparer.Ordinal);

        /// <summary>Every instance asked, in order.</summary>
        public List<string> Started { get; } = [];

        /// <summary>The handoff each attempt was given, by attempt number from 1.</summary>
        public Dictionary<int, string?> Given { get; } = new();

        /// <summary>Each item a work step saw, in order.</summary>
        public List<string> ItemsWorked { get; } = [];

        public Task StartedAsync(string instance)
            => StartOf(instance).Task;

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
                StartOf(run.Instance).TrySetResult();
                Given[run.Attempt] = run.Handoff;

                if (run.Step.Name == "work")
                {
                    ItemsWorked.Add(run.Item.AsTemplateText());
                }
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
