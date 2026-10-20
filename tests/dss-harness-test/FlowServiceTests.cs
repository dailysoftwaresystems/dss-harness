using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The commands of a run: a start makes the run's records and drives the session to its end, an answer goes on with a
/// paused run or is handed to a running engine through <c>inbox.json</c>, the safe pause is asked of a running engine
/// through <c>stop.json</c> and waited for, a stop ends it, the two interrupts are counted, and <c>list-orchestrator</c>
/// says what the run's own files hold.
/// </summary>
public sealed class FlowServiceTests
{
    private const string Workflow = "flow";

    /// <summary>A repository with one workflow declared, of whose two steps the second waits on the first.</summary>
    private static async Task<(Repository Repository, FlowService Service, SteppedRunner Runner)> ServiceAsync(string flow = """
        name: flow
        steps:
          - name: one
            type: orient
          - name: two
            type: orient
            dependsOn: [one]
        """)
    {
        var (repository, runner) = await RepositoryAsync(flow);
        return (repository, Service(repository.Harness, runner), runner);
    }

    private static async Task<(Repository Repository, SteppedRunner Runner)> RepositoryAsync(string flow)
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, cancellationToken, new HarnessConfig { Workflows = [Workflow] });
        await harness.CommitAllAsync(temp.Path, "initial", cancellationToken);
        temp.WriteFile($".harness-config/workflows/{Workflow}/{Workflow}.yml", flow);

        return (new Repository(harness, temp), new SteppedRunner());
    }

    private static FlowService Service(HarnessFactory harness, IFlowStepRunner runner)
        => new(
            harness.ContextLoader,
            harness.Platform,
            harness.FileSystem,
            harness.OrchestrationStore,
            harness.Identity,
            new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity),
            new WorkflowValidationService(harness.ContextLoader, harness.FileSystem, harness.Platform),
            runner,
            new WritesNothing(),
            harness.OrchestrationLog);

    private static OrchestratorLayout RunOf(Repository repository)
        => new("r1", Path.Combine(repository.Path, ".orchestrators", "r1"));

    /// <summary>A run starts the first free r&lt;N&gt;, writes every record of the run, drives the session to its end and releases the owner.</summary>
    [Fact]
    public async Task ARunStarts_RecordsAndDrivesTheSessionToItsEnd()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("done", outcome.Message);

        var run = RunOf(repository);
        var store = repository.Harness.OrchestrationStore;

        Assert.NotNull(store.ReadFlow(run));
        Assert.NotNull(store.ReadWorkflowStatus(run));
        Assert.NotNull(store.ReadDefinition(run));
        Assert.NotNull(store.ReadFlowQuestions(run));
        Assert.Null(store.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
        Assert.True(File.Exists(run.ReportJsonFile));
        Assert.Equal(["one", "two"], runner.Started);
    }

    /// <summary>A definition that does not hold is refused (12) before anything of a run is made.</summary>
    [Fact]
    public async Task ADefinitionThatDoesNotHoldIsRefused_BeforeAnythingOfARunIsMade()
    {
        var (repository, service, _) = await ServiceAsync();
        using var disposed = repository;

        var outcome = await service.RunAsync(repository.Path, "no-such-workflow", run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(repository.Path, ".orchestrators", "r1")));
    }

    /// <summary>A session that leaves a question open exits 1, names the way to answer it, and holds the question in questions.json.</summary>
    [Fact]
    public async Task ASessionThatLeavesAQuestionOpen_ExitsOne_AndNamesTheWayToAnswerIt()
    {
        var (repository, service, _) = await ServiceAsync("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
            """);
        using var disposed = repository;

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FlowExit.Paused, outcome.ExitCode);
        Assert.Contains(outcome.Details ?? [], line => line.Contains("answer with answer-orchestrator r1 <answer> --question q1", StringComparison.Ordinal));

        var questions = repository.Harness.OrchestrationStore.ReadFlowQuestions(RunOf(repository));

        Assert.NotNull(questions);
        var asked = Assert.Single(questions.Questions);

        Assert.Equal("open", asked.State);
        Assert.Equal("pause", asked.Kind);
        Assert.Equal("what now?", asked.ProblemShown!.Happened);
        Assert.Equal(["stay", "go"], asked.Options.Select(option => option.Answer));
    }

    /// <summary>An answer is checked before anything of the run is touched: an answer not among the question's, and a text where none is taken, are usage errors.</summary>
    [Fact]
    public async Task AnAnswerIsCheckedBeforeAnythingOfTheRunIsTouched()
    {
        var (repository, service, _) = await ServiceAsync("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
            """);
        using var disposed = repository;

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var wrong = await service.AnswerAsync(repository.Path, "r1", "nope", question: null, text: null, again: false, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, wrong.ExitCode);
        Assert.Contains("'nope' is not one of the answers 'q1' takes: stay, go", wrong.Message, StringComparison.Ordinal);

        var text = await service.AnswerAsync(repository.Path, "r1", "stay", question: null, text: "a text nobody takes", again: false, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, text.ExitCode);
        Assert.Contains("takes with no text; give none", text.Message, StringComparison.Ordinal);
    }

    /// <summary>An answer names the question it answers: with several open, leaving --question out is a usage error that lists them.</summary>
    [Fact]
    public async Task WithSeveralQuestionsOpen_LeavingTheQuestionOutIsAUsageErrorThatListsThem()
    {
        var (repository, service, runner) = await ServiceAsync("""
            name: flow
            steps:
              - name: first
                type: pause
                question: first?
                options: [one, two]
              - name: second
                type: pause
                question: second?
                options: [three, four]
            """);
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await service.AnswerAsync(repository.Path, "r1", "one", question: null, text: null, again: false, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Contains("holds 2 open questions; --question names the one this answers", outcome.Message, StringComparison.Ordinal);
        Assert.Contains(outcome.Details ?? [], line => line.Contains("q1 (pause)", StringComparison.Ordinal));
        Assert.Contains(outcome.Details ?? [], line => line.Contains("q2 (pause)", StringComparison.Ordinal));
    }

    /// <summary>An answer goes on with the run where no engine runs it: the question answered by a person through the command, and everything that can run after it.</summary>
    [Fact]
    public async Task AnAnswerGoesOnWithTheRun_WhereNoEngineRunsIt()
    {
        var (repository, service, _) = await ServiceAsync("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
              - name: after
                type: orient
                dependsOn: [holding]
            """);
        using var disposed = repository;

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, again: false, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        var run = RunOf(repository);
        var questions = repository.Harness.OrchestrationStore.ReadFlowQuestions(run)!;
        var answered = Assert.Single(questions.Questions);

        Assert.Equal("answered", answered.State);
        Assert.Equal("go", answered.Answers[0].Answer);
        var by = answered.Answers[0].By;
        Assert.NotNull(by);
        Assert.Equal("person", by.Who);
        Assert.Equal("answer-orchestrator", by.Through);

        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

        Assert.Equal("Done", flow.Status);
        Assert.Contains(flow.Steps, step => step.Instance == "after" && step.State == "Done");
    }

    /// <summary>An answer to a question no longer open is refused (13), naming what became of the question.</summary>
    [Fact]
    public async Task AnAnswerToAQuestionNoLongerOpenIsRefused()
    {
        var (repository, service, runner) = await ServiceAsync("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
            """);
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, again: false, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var again = await service.AnswerAsync(repository.Path, "r1", "stay", question: "q1", text: null, again: false, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.Contains("question 'q1' of run 'r1' is answered", again.Message, StringComparison.Ordinal);
    }

    /// <summary>An answer handed to a running engine lands in inbox.json and nothing is applied by the command.</summary>
    [Fact]
    public async Task AnAnswerHandedToARunningEngine_LandsInTheInbox()
    {
        var (repository, service, _) = await ServiceAsync("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
            """);
        using var disposed = repository;

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var run = RunOf(repository);
        var identity = repository.Harness.Identity;

        // The engine that runs the run is a live owner: this process stands for it.
        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(identity.CurrentMachine, identity.CurrentId, DateTimeOffset.UtcNow, identity.Current));

        var outcome = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, again: false, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains("handed 'go' for question 'q1' to the engine running 'r1'", outcome.Message, StringComparison.Ordinal);

        var inbox = repository.Harness.OrchestrationStore.ReadInbox(run);

        Assert.NotNull(inbox);
        Assert.Equal("q1", Assert.Single(inbox.Answers).Question);

        // The command applied nothing: the question stands open, for the engine alone takes the answer.
        Assert.Equal("open", repository.Harness.OrchestrationStore.ReadFlowQuestions(run)!.Questions[0].State);
    }

    /// <summary>A pause asked where no engine runs the run is refused (13): there is nothing to pause.</summary>
    [Fact]
    public async Task APauseAskedWhereNoEngineRunsIsRefused()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await service.PauseAsync(repository.Path, "r1", all: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains("run 'r1' is not being run by an engine: there is nothing to pause", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>A stop asked where no engine runs is refused as a pause is.</summary>
    [Fact]
    public async Task AStopAskedWhereNoEngineRunsIsRefused()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var outcome = await service.StopAsync(repository.Path, "r1", now: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains("there is nothing to stop", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>A pause asked of a running engine is written to stop.json, read by it within its poll, and waited for until complete - naming what it left running.</summary>
    [Fact]
    public async Task APauseAskedOfARunningEngine_IsWritten_ReadAndWaitedFor()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", release.Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        await runner.WaitStartedAsync("two");

        var paused = await service.PauseAsync(repository.Path, "r1", all: false, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, paused.ExitCode);
        Assert.Contains("run 'r1' is paused", paused.Message, StringComparison.Ordinal);
        Assert.Contains("1 machine step still in progress: two", string.Join(";", paused.Details ?? []), StringComparison.Ordinal);


        _ = release.TrySetResult();
        var outcome = await driving;

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);

        var run = RunOf(repository);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

        Assert.Equal("Paused", flow.Status);
        Assert.NotNull(flow.Pause);
        Assert.Equal("command", flow.Pause.Cause);
        Assert.NotNull(flow.Pause.CompletedAt);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
    }

    /// <summary>A stop of a running engine ends it stopped: the step in flight stopped, the owner released, the report written.</summary>
    [Fact]
    public async Task AStopOfARunningEngine_EndsItStopped()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.Holds("one", new TaskCompletionSource().Task);
        runner.On("two", FlowStepResult.Ended(passed: true, 0));

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        await runner.WaitStartedAsync("one");

        var stopped = await service.StopAsync(repository.Path, "r1", now: false, json: false, TestContext.Current.CancellationToken);
        var outcome = await driving;

        Assert.Equal(HarnessExit.Success, stopped.ExitCode);
        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);

        var run = RunOf(repository);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

        Assert.Equal("Stopped", flow.Status);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
    }

    /// <summary>The first interrupt asks the engine for a safe pause; the run ends paused once the machine step it left running has ended.</summary>
    [Fact]
    public async Task TheFirstInterruptAsksForASafePause()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", release.Task);

        var interrupts = new FlowInterrupts();
        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, interrupts, TestContext.Current.CancellationToken);

        await runner.WaitStartedAsync("two");

        interrupts.Pressed();

        // The pause completes - no reason step was in flight, so no handoff - while the machine step runs on.
        var store = repository.Harness.OrchestrationStore;
        var run = RunOf(repository);

        await UntilAsync(() => store.ReadFlow(run)?.Pause?.CompletedAt is not null);
        Assert.Equal("interrupt", store.ReadFlow(run)!.Pause!.Cause);

        _ = release.TrySetResult();

        var outcome = await driving;

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("Paused", store.ReadFlow(run)!.Status);
        Assert.Equal(1, interrupts.Presses);
    }

    /// <summary>Waits until <paramref name="holds"/>, polling as the engine's own poller does; fails the test after ten seconds.</summary>
    private static async Task UntilAsync(Func<bool> holds)
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (!holds())
        {
            Assert.True(DateTimeOffset.UtcNow < until, "the engine did not do what was waited for within ten seconds");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>A resume goes on with what a stop left: the stopped step runs again, and the run ends done.</summary>
    [Fact]
    public async Task AResumeGoesOnWithWhatAStopLeft()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", new TaskCompletionSource().Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        await runner.WaitStartedAsync("two");

        _ = await service.StopAsync(repository.Path, "r1", now: false, json: false, TestContext.Current.CancellationToken);
        _ = await driving;

        runner.On("two", FlowStepResult.Ended(passed: true, 0));

        var resumed = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, resumed.ExitCode);
        Assert.Equal("Done", repository.Harness.OrchestrationStore.ReadFlow(RunOf(repository))!.Status);
    }

    /// <summary>An answer taken from the inbox by a running engine is applied to the question it names, and the inbox emptied of it.</summary>
    [Fact]
    public async Task AnAnswerTakenFromTheInbox_IsAppliedAndEmptied()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
              - name: after
                type: orient
                dependsOn: [holding]
            """);
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path);
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity);
        var (run, context) = sessions.Create(layout, definition);

        using (var records = new FlowRunRecords(harness.FileSystem, harness.OrchestrationStore, run, context))
        {
            var coordinator = new FlowCoordinator(definition, new AskingRunner(), new WritesNothing(), records: records);
            var paused = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Paused, paused.Status);

            // The answer arrives from another terminal: the engine is offered it, and its next session - the loop of
            // it - takes and applies it, for the graph is the loop's alone to change.
            var offered = coordinator.OfferAnswerAsync("q1", "go");
            var end = await coordinator.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.True(await offered);
        }

        var questions = harness.OrchestrationStore.ReadFlowQuestions(run)!;

        Assert.Equal("answered", questions.Questions[0].State);
        Assert.Equal("answer-orchestrator", questions.Questions[0].Answers[0].By!.Through);
    }

    /// <summary>list-orchestrator says what the run's own files hold: its definition, its status, its open question - and with --questions and --steps each question and each step.</summary>
    [Fact]
    public async Task ListOrchestratorSaysWhatTheRunsOwnFilesHold()
    {
        var (repository, service, _) = await ServiceAsync("""
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
            """);
        using var disposed = repository;

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var listed = await repository.Harness.OrchestratorService.ListAsync(repository.Path, "r1", steps: true, questions: true, json: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        var lines = string.Join("\n", listed.Details ?? []);
        Assert.Contains("run of 'flow': paused", lines, StringComparison.Ordinal);
        Assert.Contains("open question q1 (pause): what now?", lines, StringComparison.Ordinal);
        Assert.Contains("step holding: paused", lines, StringComparison.Ordinal);
        Assert.Contains("question q1 (pause, open): what now?", lines, StringComparison.Ordinal);
        Assert.Contains("stay: the redirect is given up, and the run goes on as if the handler named no then", lines, StringComparison.Ordinal);
    }

    /// <summary>delete-orchestrator refuses a run a live engine owns, naming pause-orchestrator and stop-orchestrator.</summary>
    [Fact]
    public async Task DeleteOrchestratorRefusesARunALiveEngineOwns()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var run = RunOf(repository);
        var identity = repository.Harness.Identity;

        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(identity.CurrentMachine, identity.CurrentId, DateTimeOffset.UtcNow, identity.Current));

        var outcome = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains("a live engine owns the run", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("pause-orchestrator r1", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("stop-orchestrator r1", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>A run's own steps and report are kept records: deleted only with --delete-evidence, and the owner file beside the directory goes with the run.</summary>
    [Fact]
    public async Task ARunsStepsAndReportAreKeptRecords_DeletedOnlyWithEvidence()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        var run = RunOf(repository);

        var refused = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: false, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("step file", refused.Message, StringComparison.Ordinal);
        Assert.Contains("report file", refused.Message, StringComparison.Ordinal);

        var deleted = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.False(Directory.Exists(run.Directory));
        Assert.False(File.Exists(run.OwnerFile));
    }

    /// <summary>A run whose steps no runner serves fails loudly, saying so: never a run that passes everything over.</summary>
    [Fact]
    public async Task NoRunnerServesAStepOfTheRunsType()
    {
        var (repository, runner) = await RepositoryAsync("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        using var disposed = repository;

        var service = Service(repository.Harness, new NoStepRunner());

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.ExitCode);

        var flow = repository.Harness.OrchestrationStore.ReadFlow(RunOf(repository))!;

        Assert.Equal("Failed", flow.Status);
        Assert.Contains(flow.Steps, step => step.Error!.Contains("no runner serves a step of type 'orient' yet", StringComparison.Ordinal));
    }

    /// <summary>A runner whose steps are scripted: one result each, or a task the test holds until it releases it.</summary>
    private sealed class SteppedRunner : IFlowStepRunner
    {
        private readonly Dictionary<string, FlowStepResult> _results = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Task> _holds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
        private FlowStepResult? _every;

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public List<string> Started { get; } = [];

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public void On(string step, FlowStepResult result) => _results[step] = result;

        /// <summary>Answers every step with one result.</summary>
        public void OnEvery(FlowStepResult result) => _every = result;

        /// <summary>Holds <paramref name="step"/> until <paramref name="release"/> completes, as a machine step the pause leaves running.</summary>
        public void Holds(string step, Task release) => _holds[step] = release;

        /// <summary>Waits until the runner has been asked for <paramref name="step"/>.</summary>
        public async Task WaitStartedAsync(string step)
        {
            if (!_started.TryGetValue(step, out var source))
            {
                source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _started[step] = source;
            }

            await source.Task;
        }

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            if (!_started.TryGetValue(run.Step.Name, out var started))
            {
                started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _started[run.Step.Name] = started;
            }

            _ = started.TrySetResult();
            Started.Add(run.Step.Name);

            if (_holds.TryGetValue(run.Step.Name, out var held))
            {
                // A hold is one attempt's: what a stop or a pause ends, and the resume's own attempt of the step runs
                // as a step that is no longer held.
                _ = _holds.Remove(run.Step.Name);
                await held.WaitAsync(cancellationToken);
                return FlowStepResult.Ended(passed: true, 0);
            }

            return _every ?? _results.GetValueOrDefault(run.Step.Name) ?? FlowStepResult.Ended(passed: true, 0);
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>A runner whose pause step asks, and whose every other step passes.</summary>
    private sealed class AskingRunner : IFlowStepRunner
    {
        /// <inheritdoc/>
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult(run.Step.Type == "pause"
                ? FlowStepResult.Asking(new FlowQuestion("asked", "pause", run.Instance, "what now?", ["stay", "go"]))
                : FlowStepResult.Ended(passed: true, 0));

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    /// <summary>The facts every run of these tests asks of its runners.</summary>
    private sealed class WritesNothing : IFlowRunnerFacts
    {
        public bool WritesTree(string runner) => false;
    }

    /// <summary>A repository initialized with one workflow declared, and its harness.</summary>
    private sealed record Repository(HarnessFactory Harness, TempDirectory Tree) : IDisposable
    {
        /// <summary>The repository's root.</summary>
        public string Path => Tree.Path;

        /// <inheritdoc/>
        public void Dispose() => Tree.Dispose();
    }
}
