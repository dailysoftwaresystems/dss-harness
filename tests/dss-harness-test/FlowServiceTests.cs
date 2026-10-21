using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The commands of a run: a start makes the run's records and drives the session to its end, an answer goes on with a
/// paused run or is handed to a running engine through <c>inbox.json</c>, the safe pause is asked of a running engine
/// through <c>stop.json</c> and waited for, a stop ends it, the two interrupts are counted, every exit names the records
/// it wrote in text and in <c>--json</c>, and <c>list-orchestrator</c> says what the run's own files hold.
/// </summary>
public sealed class FlowServiceTests
{
    private const string Workflow = "flow";

    /// <summary>Every bound a test of a session waits for: a session that hangs fails the test rather than the run's minutes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>The engine's own looks, made quick: a poll and a look every 10 ms; the stop waited for as the tool waits.</summary>
    private static readonly FlowTimings Quick = new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private static async Task<(Repository Repository, SteppedRunner Runner)> RepositoryAsync(string flow, HarnessConfig? config = null)
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, config ?? new HarnessConfig { Workflows = [Workflow] });
        await harness.CommitAllAsync(temp.Path, "initial", Token);
        temp.WriteFile($".harness-config/workflows/{Workflow}/{Workflow}.yml", flow);

        return (new Repository(harness, temp), new SteppedRunner());
    }

    /// <summary>The service over <paramref name="harness"/>, with what a test changes of it.</summary>
    private static FlowService Service(
        HarnessFactory harness,
        IFlowStepRunner runner,
        TimeProvider? clock = null,
        FlowTimings? timings = null,
        IHarnessOutput? output = null,
        IFileSystem? fileSystem = null,
        IProcessIdentity? identity = null)
    {
        var files = fileSystem ?? harness.FileSystem;
        var store = fileSystem is null ? harness.OrchestrationStore : new OrchestrationStore(files, harness.Output);
        var engines = identity ?? harness.Identity;

        return new(
            harness.ContextLoader,
            harness.Platform,
            files,
            store,
            engines,
            new FlowSessions(store, files, engines),
            new WorkflowValidationService(harness.ContextLoader, harness.FileSystem, harness.Platform, new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform)),
            runner,
            new WritesNothing(),
            harness.OrchestrationLog,
            harness.ChildSessions,
            harness.PathBudget,
            output ?? harness.Output,
            clock,
            timings ?? Quick);
    }

    private static OrchestratorLayout RunOf(Repository repository, string run = "r1")
        => new(run, Path.Combine(repository.Path, ".orchestrators", run));

    /// <summary>A run starts the next r&lt;N&gt;, writes every record of the run, drives the session to its end, releases the owner, and names every record it wrote.</summary>
    [Fact]
    public async Task ARunStarts_RecordsAndDrivesTheSessionToItsEnd_NamingEveryRecord()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

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
        Assert.Equal(
            [
                $"run: {run.Directory}",
                $"state: {run.FlowFile}",
                $"status: {run.WorkflowStatusFile}",
                $"questions: {run.QuestionsFile}",
                $"issues: {run.PreExistingIssuesFile}",
                $"definition: {run.DefinitionFile}",
                $"events: {run.EventsFile}",
                $"report: {run.ReportFile}",
                $"reportJson: {run.ReportJsonFile}",
                $"log {run.LogFile("r1")}",
            ],
            outcome.Details);
    }

    /// <summary>A session's --json is the run's document: its code and line, the run, its status, what it spent and every record it wrote by its path - never the empty stopped document.</summary>
    [Fact]
    public async Task ASessionsJson_IsTheRunsDocument_NamingEveryRecord()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: true, cancellationToken: Token).WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

        Assert.True(outcome.Quiet);
        Assert.Equal(["exitCode", "summary", "run", "status", "questions", "answered", "passedOver", "handoffs", "leftForAPerson", "spent", "records"], document.Select(member => member.Key));
        Assert.Equal(0, document["exitCode"]!.GetValue<int>());
        Assert.Equal("done", document["summary"]!.GetValue<string>());
        Assert.Equal("r1", document["run"]!.GetValue<string>());
        Assert.Equal("Done", document["status"]!.GetValue<string>());
        Assert.Equal(0d, document["spent"]!["usd"]!.GetValue<double>());
        Assert.Equal(
            ["run", "state", "status", "questions", "issues", "definition", "events", "report", "reportJson", "log"],
            document["records"]!.AsObject().Select(member => member.Key));
        Assert.Equal(run.FlowFile, document["records"]!["state"]!.GetValue<string>());
        Assert.Equal(run.LogFile("r1"), document["records"]!["log"]!.GetValue<string>());
    }

    /// <summary>
    /// Every verb over a run made sweeps what a killed engine's steps left running before it reads anything else of the
    /// run, its own name on what the sweep says: here a record that does not read as one, said and left as it is.
    /// </summary>
    [Theory]
    [InlineData(FlowService.ResumeCommand)]
    [InlineData(FlowService.AnswerCommand)]
    [InlineData(FlowService.PauseCommand)]
    [InlineData(FlowService.StopCommand)]
    [InlineData("list-orchestrator")]
    public async Task EveryVerbOverARun_SweepsWhatAKilledEnginesStepsLeft_First(string command)
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var step = RunOf(repository).StepDirectory("one");
        var record = OrchestratorLayout.ChildFile(step, 7);

        Directory.CreateDirectory(OrchestratorLayout.ChildrenDirectory(step));
        File.WriteAllText(record, """{ "session": 0, "started": "1", "program": "p", "owner": { "machine": "m", "processId": 1 } }""");

        _ = command switch
        {
            FlowService.ResumeCommand => await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token),
            FlowService.AnswerCommand => await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token),
            FlowService.PauseCommand => await service.PauseAsync(repository.Path, "r1", all: false, json: false, Token).WaitAsync(Bound, Token),
            FlowService.StopCommand => await service.StopAsync(repository.Path, "r1", now: false, json: false, Token).WaitAsync(Bound, Token),
            _ => await service.QuestionsAsync(repository.Path, "r1", json: false, Token).WaitAsync(Bound, Token),
        };

        var warning = $"{command}: WARN - '{record}' is the record of a contained child's session, and does not read as one: "
            + "its session is 0, and a session is a process's id. It was left as it is, and nothing it names was ended.";

        Assert.Equal(warning, repository.Harness.StandardError.ToString().Split('\n').Select(line => line.TrimEnd('\r')).FirstOrDefault(line => line.StartsWith($"{command}: ", StringComparison.Ordinal)));
        Assert.True(File.Exists(record));
    }

    /// <summary>A definition that does not hold is refused (13) before anything of a run is made.</summary>
    [Fact]
    public async Task ADefinitionThatDoesNotHoldIsRefused_BeforeAnythingOfARunIsMade()
    {
        var (repository, service, _) = await ServiceAsync();
        using var disposed = repository;

        var outcome = await service.RunAsync(repository.Path, "no-such-workflow", run: null, inputs: null, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(repository.Path, ".orchestrators", "r1")));
    }

    /// <summary>A run's name is held to the length the repository's worktrees take, as an orchestrator's is: a usage error, and nothing made.</summary>
    [Fact]
    public async Task ARunsNameLongerThanAWorktreeTakes_IsAUsageError()
    {
        var (repository, service, _) = await ServiceAsync();
        using var disposed = repository;
        var name = new string('a', new WorktreeSettings().MaxNameLength + 1);

        var outcome = await service.RunAsync(repository.Path, Workflow, run: name, inputs: null, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(repository.Path, ".orchestrators", name)));
    }

    /// <summary>A start whose agents' worktrees would pass this machine's path limit is refused (13) before anything of the run is made.</summary>
    [Fact]
    public async Task AStartWhoseWorktreesWouldPassThePathLimit_IsRefused_BeforeAnythingIsMade()
    {
        var (repository, runner) = await RepositoryAsync(
            """
            name: flow
            steps:
              - name: one
                type: orient
            """,
            new HarnessConfig { Workflows = [Workflow], Worktrees = new WorktreeSettings { PathLimit = 40 } });
        using var disposed = repository;

        var outcome = await Service(repository.Harness, runner).RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.StartsWith("run 'r1' of 'flow' would work in paths too long for this machine", outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(RunOf(repository).Directory));
        Assert.Empty(runner.Started);
    }

    /// <summary>An input's file is read through the tool's file system relative to the directory the command acts on; one that is not there is a usage error naming it, and nothing is made.</summary>
    [Fact]
    public async Task AnInputsFileThatIsNotThere_IsAUsageError_NamingIt()
    {
        var (repository, service, _) = await ServiceAsync();
        using var disposed = repository;

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["brief=@missing.md"], json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Equal($"'{Path.Combine(repository.Path, "missing.md")}' is not a file: --input brief=@<file> names the file whose text is given", outcome.Message);
        Assert.False(Directory.Exists(RunOf(repository).Directory));
    }

    /// <summary>The inputs the command line gives are read as name=value, a value's @file from the command's directory; an input given twice, or with no name or no value, is a usage error.</summary>
    [Fact]
    public void TheCommandLinesInputsAreReadFromTheCommandsDirectory()
    {
        using var temp = new TempDirectory();
        var files = new HarnessFactory().FileSystem;

        temp.WriteFile("notes/brief.md", "the brief\n");

        var (inputs, problem) = FlowInputs.Read(files, temp.Path, ["mode=fast", "brief=@notes/brief.md", "who=@"]);

        Assert.Null(problem);
        Assert.Equal("fast", inputs!["mode"]);
        Assert.Equal("the brief\n", inputs["brief"]);
        Assert.Equal("@", inputs["who"]);
        Assert.Equal("input 'mode' is given twice; --input gives each input once", FlowInputs.Read(files, temp.Path, ["mode=a", "mode=b"]).Problem);
        Assert.Equal("'=b' is not an input as name=value; --input k=v gives one, and k=@<file> a text input a file's text", FlowInputs.Read(files, temp.Path, ["=b"]).Problem);
        Assert.Equal((null, null), FlowInputs.Read(files, temp.Path, null));
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

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowExit.Paused, outcome.ExitCode);
        Assert.Contains(outcome.Details ?? [], line => line.EndsWith("answer with answer-orchestrator r1 <answer> --question q1", StringComparison.Ordinal));

        var questions = repository.Harness.OrchestrationStore.ReadFlowQuestions(RunOf(repository));

        Assert.NotNull(questions);
        var asked = Assert.Single(questions.Questions);

        Assert.Equal("open", asked.State);
        Assert.Equal("pause", asked.Kind);
        Assert.Equal("what now?", asked.ProblemShown!.Happened);
        Assert.Equal(["stay", "go"], asked.Options.Select(option => option.Answer));
    }

    /// <summary>
    /// What a session's end exits with: 0 done; 21 failed where every failure that ended it is one the retry table runs
    /// again, else 20; 1 where a question waits - the budget's own pause among them; 130 a safe pause or a stop.
    /// </summary>
    [Theory]
    [InlineData(FlowRunStatus.Done, null, null, HarnessExit.Success)]
    [InlineData(FlowRunStatus.Failed, 21, null, HarnessExit.Incomplete)]
    [InlineData(FlowRunStatus.Failed, 3, null, HarnessExit.Incomplete)]
    [InlineData(FlowRunStatus.Failed, 20, null, HarnessExit.CommandFailed)]
    [InlineData(FlowRunStatus.Failed, 13, null, HarnessExit.CommandFailed)]
    [InlineData(FlowRunStatus.Paused, null, null, FlowExit.Paused)]
    [InlineData(FlowRunStatus.Paused, null, "budget", FlowExit.Paused)]
    [InlineData(FlowRunStatus.Paused, null, "interrupt", HarnessExit.Cancelled)]
    [InlineData(FlowRunStatus.Paused, null, "command", HarnessExit.Cancelled)]
    [InlineData(FlowRunStatus.Stopped, null, null, HarnessExit.Cancelled)]
    public void ASessionsEndExitsWithItsCode(FlowRunStatus status, int? exit, string? pause, int expected)
    {
        var failed = exit is null ? Array.Empty<string>() : ["one"];
        var end = new FlowSessionEnd(status, [], failed, [], "ended", pause is null ? null : new FlowPauseState(pause, false, DateTimeOffset.UnixEpoch));
        var flow = new FlowFile(1, "r1", "flow", "digest", new Dictionary<string, string>(), status.ToString(), new FlowBudgetRecord(0, 0, 0), 0, [new FlowStepRecord("one", "one", false, "Failed", 1, 1, [], [], "build", Exit: exit)], [], DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, FlowService.ExitOf(end, flow));
    }

    /// <summary>A failure that ended a run is incomplete only where every one is: one failure the retry table does not run again beside an incomplete one is a failure (20).</summary>
    [Fact]
    public void ARunFailedBesideAnIncompleteStep_IsAFailure()
    {
        var end = new FlowSessionEnd(FlowRunStatus.Failed, [], ["one", "two"], [], "failed");
        var flow = new FlowFile(
            1, "r1", "flow", "digest", new Dictionary<string, string>(), "Failed", new FlowBudgetRecord(0, 0, 0), 0,
            [new FlowStepRecord("one", "one", false, "Failed", 1, 1, [], [], "build", Exit: 21), new FlowStepRecord("two", "two", false, "Failed", 1, 1, [], [], "build", Exit: 20)],
            [],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(HarnessExit.CommandFailed, FlowService.ExitOf(end, flow));
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

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var wrong = await service.AnswerAsync(repository.Path, "r1", "nope", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.UsageError, wrong.ExitCode);
        Assert.Equal("'nope' is not one of the answers 'q1' takes: stay, go", wrong.Message);

        var text = await service.AnswerAsync(repository.Path, "r1", "stay", question: null, text: "a text nobody takes", textFile: null, again: false, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.UsageError, text.ExitCode);
        Assert.Equal("'stay' is an answer 'q1' takes with no text; give none", text.Message);
    }

    /// <summary>An answer's text file is read relative to the directory the command acts on; one that is not there, and a text given two ways, are usage errors.</summary>
    [Fact]
    public async Task AnAnswersTextFile_IsReadFromTheCommandsDirectory_AndOneMissingIsAUsageError()
    {
        var (repository, service, _) = await ServiceAsync();
        using var disposed = repository;

        var missing = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: "nope.txt", again: false, json: false, cancellationToken: Token);
        var both = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: "a", textFile: "nope.txt", again: false, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.UsageError, missing.ExitCode);
        Assert.Equal($"'{Path.Combine(repository.Path, "nope.txt")}' is not a file: --text-file names the file whose text is given", missing.Message);
        Assert.Equal(HarnessExit.UsageError, both.ExitCode);
        Assert.Equal("--text and --text-file are one answer's text given two ways; give one", both.Message);
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
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var outcome = await service.AnswerAsync(repository.Path, "r1", "one", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.UsageError, outcome.ExitCode);
        Assert.Equal("run 'r1' holds 2 open questions; --question names the one this answers", outcome.Message);
        Assert.Equal(["q1 (pause): first?", "q2 (pause): second?"], outcome.Details);
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

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var outcome = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: true, cancellationToken: Token).WaitAsync(Bound, Token);

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

        // The document says what the command answered: the question, the answer, who gave it and how.
        var given = Assert.Single(JsonNode.Parse(Assert.Single(outcome.Data))!["answered"]!.AsArray())!;

        Assert.Equal("q1", given["question"]!.GetValue<string>());
        Assert.Equal("go", given["answer"]!.GetValue<string>());
        Assert.Equal("person", given["by"]!.GetValue<string>());
        Assert.Equal("answer-orchestrator", given["through"]!.GetValue<string>());
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
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var again = await service.AnswerAsync(repository.Path, "r1", "stay", question: "q1", text: null, textFile: null, again: false, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.Equal("question 'q1' of run 'r1' is answered", again.Message);
    }

    /// <summary>An answer handed to a running engine lands in inbox.json under an id of its own, nothing is applied by the command, and the inbox is named.</summary>
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

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var identity = repository.Harness.Identity;

        // The engine that runs the run is a live owner: this process stands for it.
        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(identity.CurrentMachine, identity.CurrentId, DateTimeOffset.UtcNow, identity.Current));

        var outcome = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: true, cancellationToken: Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("handed 'go' for question 'q1' to the engine running 'r1'", outcome.Message);
        Assert.Equal(run.InboxFile, JsonNode.Parse(Assert.Single(outcome.Data))!["records"]!["inbox"]!.GetValue<string>());

        var inbox = repository.Harness.OrchestrationStore.ReadInbox(run);

        Assert.NotNull(inbox);
        var entry = Assert.Single(inbox.Answers);
        Assert.Equal("q1", entry.Question);
        Assert.Equal(32, entry.Id.Length);

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
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var outcome = await service.PauseAsync(repository.Path, "r1", all: false, json: false, Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal("run 'r1' is not being run by an engine: there is nothing to pause", outcome.Message);
    }

    /// <summary>A stop asked where no engine runs is refused as a pause is.</summary>
    [Fact]
    public async Task AStopAskedWhereNoEngineRunsIsRefused()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var outcome = await service.StopAsync(repository.Path, "r1", now: false, json: false, Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal("run 'r1' is not being run by an engine: there is nothing to stop", outcome.Message);
    }

    /// <summary>A pause asked of a running engine is written to stop.json, read by it at its poll, and waited for until complete - naming what it left running.</summary>
    [Fact]
    public async Task APauseAskedOfARunningEngine_IsWritten_ReadAndWaitedFor()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", release.Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);

        await runner.WaitStartedAsync("two");

        var paused = await service.PauseAsync(repository.Path, "r1", all: false, json: false, Token).WaitAsync(Bound, Token);
        var run = RunOf(repository);

        Assert.Equal(HarnessExit.Success, paused.ExitCode);
        Assert.Equal("run 'r1' is paused", paused.Message);
        Assert.Equal(
            [
                "1 machine step(s) still in progress: two; the engine's process ends when it has",
                $"stop: {run.StopFile}",
                $"state: {run.FlowFile}",
                $"status: {run.WorkflowStatusFile}",
                $"log {run.LogFile("r1")}",
            ],
            paused.Details);

        _ = release.TrySetResult();
        var outcome = await driving.WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);

        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

        Assert.Equal("Paused", flow.Status);
        Assert.NotNull(flow.Pause);
        Assert.Equal("command", flow.Pause.Cause);
        Assert.NotNull(flow.Pause.CompletedAt);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
    }

    /// <summary>
    /// A pause asked of an engine that ends before it takes it is answered from the run's own state - never "paused"
    /// for a run that ended done - and the request is taken back, so it pauses no later session.
    /// </summary>
    [Fact]
    public async Task APauseTheEngineLeftBeforeTakingIt_IsAnsweredFromTheRunsState_AndTakenBack()
    {
        var (repository, finished, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await finished.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var engines = new Engines(repository.Harness.Identity);
        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, fileSystem: files, identity: engines);

        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(engines.CurrentMachine, Engines.Engine, DateTimeOffset.UtcNow, "its start"));

        var pausing = service.PauseAsync(repository.Path, "r1", all: false, json: false, Token);

        await files.Written(run.StopFile).WaitAsync(Bound, Token);
        engines.Alive = false;

        var outcome = await pausing.WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal("run 'r1' is done; its engine ended before it took the pause, which was taken back, so nothing was paused", outcome.Message);
        Assert.False(File.Exists(run.StopFile));
    }

    /// <summary>Waiting for a pause, cancelled, answers 130 and says the pause stays asked: it is never reported as a pause that did not come.</summary>
    [Fact]
    public async Task WaitingForAPause_Cancelled_Is130_AndThePauseStaysAsked()
    {
        var (repository, finished, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await finished.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var engines = new Engines(repository.Harness.Identity);
        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, fileSystem: files, identity: engines);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(Token);

        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(engines.CurrentMachine, Engines.Engine, DateTimeOffset.UtcNow, "its start"));

        var pausing = service.PauseAsync(repository.Path, "r1", all: false, json: false, waiting.Token);

        await files.Written(run.StopFile).WaitAsync(Bound, Token);
        await waiting.CancelAsync();

        var outcome = await pausing.WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("waiting for the pause of 'r1' was cancelled; the pause stays asked, and the engine takes it at its poll", outcome.Message);
        Assert.True(File.Exists(run.StopFile));
    }

    /// <summary>A stop the engine does not see through within the wait is refused, saying the stop stays asked and naming --now; a cancelled wait is never said to be one that ran out.</summary>
    [Fact]
    public async Task AStopNotSeenThroughWithinItsWait_IsRefused_NamingNow()
    {
        var (repository, finished, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await finished.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var engines = new Engines(repository.Harness.Identity);
        var clock = new JumpingClock();
        var service = Service(repository.Harness, runner, clock, new FlowTimings(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)), identity: engines);

        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(engines.CurrentMachine, Engines.Engine, DateTimeOffset.UtcNow, "its start"));

        var outcome = await service.StopAsync(repository.Path, "r1", now: false, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal("the engine running 'r1' did not stop within 30 s; the stop stays asked", outcome.Message);
        Assert.Equal(["stop-orchestrator r1 --now ends its process where it stands", $"stop: {run.StopFile}", $"log {run.LogFile("r1")}"], outcome.Details);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Moved);
    }

    /// <summary>A stop of a running engine ends it stopped: the step in flight stopped, the owner released, the report written.</summary>
    [Fact]
    public async Task AStopOfARunningEngine_EndsItStopped()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.Holds("one", new TaskCompletionSource().Task);
        runner.On("two", FlowStepResult.Ended(passed: true, 0));

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);

        await runner.WaitStartedAsync("one");

        var stopped = await service.StopAsync(repository.Path, "r1", now: false, json: false, Token).WaitAsync(Bound, Token);
        var outcome = await driving.WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, stopped.ExitCode);
        Assert.Equal("run 'r1' is stopped", stopped.Message);
        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);

        var run = RunOf(repository);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

        Assert.Equal("Stopped", flow.Status);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
    }

    /// <summary><c>--now</c> is refused for an engine on another machine: nothing here can end its process, and a process of this machine that carries its id is never ended for it.</summary>
    [Fact]
    public async Task StopNow_RefusesAnEngineOnAnotherMachine()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var elsewhere = new RunOwner("another-machine", Environment.ProcessId, DateTimeOffset.UtcNow, "its start");

        repository.Harness.OrchestrationStore.WriteOwner(run, elsewhere);

        var outcome = await service.StopAsync(repository.Path, "r1", now: true, json: false, Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(
            $"run 'r1' is owned by an engine on another machine (process {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} on another machine); --now ends only a process of this machine. "
            + "stop-orchestrator r1 without --now asks it to stop, or end it on its own machine. Nothing was changed.",
            outcome.Message);
        Assert.Equal(elsewhere with { StartedAt = repository.Harness.OrchestrationStore.ReadOwner(run)!.StartedAt }, repository.Harness.OrchestrationStore.ReadOwner(run));
    }

    /// <summary><c>--now</c> ends the process its owner names by its start - and all it started - then removes the owner: what is left is what a crash leaves.</summary>
    [Fact]
    public async Task StopNow_EndsTheProcessItsOwnerNames_AndRemovesTheOwner()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        using var engine = Sleeper();

        try
        {
            var identity = repository.Harness.Identity;
            repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(identity.CurrentMachine, engine.Id, DateTimeOffset.UtcNow, identity.StampOf(engine.Id)));

            var outcome = await service.StopAsync(repository.Path, "r1", now: true, json: false, Token).WaitAsync(Bound, Token);

            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.Equal($"ended the engine of run 'r1' (process {engine.Id.ToString(CultureInfo.InvariantCulture)}) where it stood", outcome.Message);
            Assert.True(engine.WaitForExit(Bound), "the engine's process ended");
            Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        }
        finally
        {
            Ending(engine);
        }
    }

    /// <summary>
    /// <c>--now</c> never ends a process it cannot tell is the engine: one whose start its owner did not record is
    /// refused, said; one whose start differs from the recorded one is another process given the engine's id, and the
    /// engine is gone.
    /// </summary>
    [Fact]
    public async Task StopNow_NeverEndsAProcessItCannotTellIsTheEngine()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var identity = repository.Harness.Identity;
        using var other = Sleeper();

        try
        {
            repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(identity.CurrentMachine, other.Id, DateTimeOffset.UtcNow));

            var unstamped = await service.StopAsync(repository.Path, "r1", now: true, json: false, Token);

            Assert.Equal(HarnessExit.Refused, unstamped.ExitCode);
            Assert.Equal(
                $"process {other.Id.ToString(CultureInfo.InvariantCulture)} cannot be told to be the engine of run 'r1' - its start cannot be read beside the one its owner recorded - so it was not ended. Nothing was changed.",
                unstamped.Message);

            repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(identity.CurrentMachine, other.Id, DateTimeOffset.UtcNow, "not its start"));

            var another = await service.StopAsync(repository.Path, "r1", now: true, json: false, Token);

            Assert.Equal(HarnessExit.Refused, another.ExitCode);
            Assert.Equal("run 'r1' is not being run by an engine: there is nothing to stop", another.Message);
            Assert.False(other.HasExited);
        }
        finally
        {
            Ending(other);
        }
    }

    /// <summary>The first interrupt asks the engine for a safe pause; the run ends paused once the machine step it left running has ended, and the count is cleared with the session.</summary>
    [Fact]
    public async Task TheFirstInterruptAsksForASafePause()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", release.Task);

        var interrupts = new FlowInterrupts();
        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, interrupts, Token);

        await runner.WaitStartedAsync("two");

        Assert.Equal(1, interrupts.Pressed());
        _ = release.TrySetResult();

        var outcome = await driving.WaitAsync(Bound, Token);
        var store = repository.Harness.OrchestrationStore;
        var run = RunOf(repository);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("Paused", store.ReadFlow(run)!.Status);
        Assert.Equal("interrupt", store.ReadFlow(run)!.Pause!.Cause);
        Assert.Equal(0, interrupts.Presses);
    }

    /// <summary>
    /// The first interrupt that also cancels the command line's own token - as the command line's handling of termination
    /// would - still pauses safely: the terminal never hands the session that token, so the run is paused, its report
    /// written and its owner released, and never abandoned as a hard stop.
    /// </summary>
    [Fact]
    public async Task AFirstInterruptThatAlsoCancelsTheCommandToken_StillPausesSafely()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", release.Task);

        using var command = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var terminal = new FlowTerminal(repository.Harness.Output, FlowService.RunCommand);
        var driving = terminal.RunAsync(
            (interrupts, token) => service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, interrupts, token),
            command.Token,
            bindSignals: false);

        await runner.WaitStartedAsync("two");

        await command.CancelAsync();
        terminal.Interrupted();
        _ = release.TrySetResult();

        var outcome = await driving.WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("Paused", flow.Status);
        Assert.Equal("interrupt", flow.Pause!.Cause);
        Assert.Equal("Done", flow.Steps.Single(step => step.Instance == "two").State);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
    }

    /// <summary>
    /// The second interrupt stops the run where it stands, in order: the machine step the pause left running is
    /// cancelled and ends stopped, the report is written and the owner released.
    /// </summary>
    [Fact]
    public async Task TheSecondInterrupt_StopsInOrder_ReportWrittenOwnerReleased()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", new TaskCompletionSource().Task);

        var terminal = new FlowTerminal(repository.Harness.Output, FlowService.RunCommand);
        var driving = terminal.RunAsync(
            (interrupts, token) => service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, interrupts, token),
            Token,
            bindSignals: false);

        await runner.WaitStartedAsync("two");

        terminal.Interrupted();
        terminal.Interrupted();

        var outcome = await driving.WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("Stopped", flow.Steps.Single(step => step.Instance == "two").State);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
        Assert.Equal(flow.Status, repository.Harness.OrchestrationStore.ReadWorkflowStatus(run)!.Status);
    }

    /// <summary>The drain of a hard stop is bounded: a session that has not ended within it after the second interrupt is left where it stands, and the command answers 130 saying so.</summary>
    [Fact]
    public async Task TheHardStopsDrainIsBounded()
    {
        var harness = new HarnessFactory();
        var terminal = new FlowTerminal(harness.Output, FlowService.RunCommand, new JumpingClock());
        var never = new TaskCompletionSource<CommandOutcome>();

        var running = terminal.RunAsync((_, _) => never.Task, Token, bindSignals: false);

        terminal.Interrupted();
        Assert.False(running.IsCompleted);
        terminal.Interrupted();

        var outcome = await running.WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal(
            "the run did not stop within 90 s of the second interrupt, and the command ends where it stands: what is left is what a crash leaves, and resume-orchestrator reclaims the run and goes on from its records",
            outcome.Message);
    }

    /// <summary>Interrupts pressed before a session is bound count for it - the first asks its pause at once, before anything starts - and the session's end clears the count.</summary>
    [Fact]
    public async Task InterruptsPressedBeforeTheSessionIsBound_CountForIt()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        var interrupts = new FlowInterrupts();
        Assert.Equal(1, interrupts.Pressed());

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, interrupts, Token).WaitAsync(Bound, Token);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(RunOf(repository))!;

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("interrupt", flow.Pause!.Cause);
        Assert.Empty(runner.Started);
        Assert.Equal(0, interrupts.Presses);
    }

    /// <summary>A cancellation of the session's own token is a hard stop, ended in order: stopped, the report written, the owner released - never an exception that abandons the run.</summary>
    [Fact]
    public async Task ACancellationOfTheSessionsToken_IsAHardStopEndedInOrder()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.Holds("one", new TaskCompletionSource().Task);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: session.Token);

        await runner.WaitStartedAsync("one");
        await session.CancelAsync();

        var outcome = await driving.WaitAsync(Bound, Token);
        var run = RunOf(repository);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("Stopped", repository.Harness.OrchestrationStore.ReadFlow(run)!.Status);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
    }

    /// <summary>A resume goes on with what a stop left: the stopped step runs again, and the run ends done.</summary>
    [Fact]
    public async Task AResumeGoesOnWithWhatAStopLeft()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", new TaskCompletionSource().Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);

        await runner.WaitStartedAsync("two");

        _ = await service.StopAsync(repository.Path, "r1", now: false, json: false, Token).WaitAsync(Bound, Token);
        _ = await driving.WaitAsync(Bound, Token);

        runner.On("two", FlowStepResult.Ended(passed: true, 0));

        var resumed = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, resumed.ExitCode);
        Assert.Equal("Done", repository.Harness.OrchestrationStore.ReadFlow(RunOf(repository))!.Status);
    }

    /// <summary>
    /// A resume refused inside its session - nothing can run until a question is answered - leaves no owner behind,
    /// answers as a refusal, and is in the run's log: the next command over the run is never refused by a dead owner.
    /// </summary>
    [Fact]
    public async Task AResumeRefusedOverAnOpenQuestion_LeavesNoOwner()
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

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var refused = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var run = RunOf(repository);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal("nothing of this run can run until the question of step 'holding' is answered; answer-orchestrator answers it", refused.Message);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));

        var logged = repository.Harness.OrchestrationLog.Read(run.LogFile("r1"))[^1];

        Assert.Equal("resume-orchestrator", logged.Command);
        Assert.Equal(HarnessExit.Refused, logged.ExitCode);
    }

    /// <summary>A resume is held to the definition the run began with: the repository's entry changed since is refused (13), the owner released and the run's state unchanged.</summary>
    [Fact]
    public async Task AResumeOverAChangedDefinition_IsRefused_LeavingTheRunAsItWas()
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

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var before = File.ReadAllText(run.FlowFile);

        repository.Tree.WriteFile($".harness-config/workflows/{Workflow}/{Workflow}.yml", """
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
              - name: added
                type: orient
            """);

        var refused = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "workflow 'flow', as the repository's config declares it now, has changed since run 'r1' began, and a run goes on only under the definition it began with; "
            + "run-orchestrator flow starts a new run of it. Nothing of run 'r1' was changed.",
            refused.Message);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.Equal(before, File.ReadAllText(run.FlowFile));
    }

    /// <summary>An answer handed to a running engine is taken by its poller, applied by the loop as a person's through the command, and emptied from the inbox.</summary>
    [Fact]
    public async Task AnInboxAnswerIsTakenByThePoller_AppliedAndRemoved()
    {
        var (repository, service, runner) = await ServiceAsync(Waiting);
        using var disposed = repository;

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Holds("slow", release.Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);

        await runner.WaitStartedAsync("slow");

        var handed = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.Success, handed.ExitCode);
        Assert.Equal("handed 'go' for question 'q1' to the engine running 'r1'", handed.Message);

        await runner.WaitStartedAsync("after");
        _ = release.TrySetResult();

        var outcome = await driving.WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var answer = Assert.Single(repository.Harness.OrchestrationStore.ReadFlowQuestions(run)!.Questions).Answers.Single();

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("go", answer.Answer);
        Assert.Equal(new FlowAnswerBy("person", "answer-orchestrator"), answer.By);
        Assert.False(File.Exists(run.InboxFile));
    }

    /// <summary>An answer in the inbox to a question the run does not leave open is refused by the loop and recorded as an event, and emptied from the inbox.</summary>
    [Fact]
    public async Task AnInboxAnswerToAClosedQuestion_IsRecordedAsAnEvent()
    {
        var (repository, runner) = await RepositoryAsync(Waiting);
        using var disposed = repository;

        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, fileSystem: files);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Holds("slow", release.Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);
        var run = RunOf(repository);

        await runner.WaitStartedAsync("slow");
        repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q9", "go", DateTimeOffset.UtcNow));

        await files.Deleted(run.InboxFile).WaitAsync(Bound, Token);
        _ = release.TrySetResult();

        var outcome = await driving.WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);

        Assert.Equal(FlowExit.Paused, outcome.ExitCode);
        Assert.Contains(events, read => read.Kind == "answer" && read.Detail == "'q9' is not a question this run leaves open");
    }

    /// <summary>
    /// An answer handed over as the session ended - after the loop's last read - is taken by the same engine's last
    /// poll, and where it settles the question the session paused on, the run is driven again to its end.
    /// </summary>
    [Fact]
    public async Task AnAnswerHandedOverAsTheSessionEnded_IsTakenByTheSameEngine_AndTheRunGoesOn()
    {
        var (repository, runner) = await RepositoryAsync("""
            name: flow
            steps:
              - name: first
                type: orient
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
                dependsOn: [first]
              - name: after
                type: orient
                dependsOn: [holding]
            """);
        using var disposed = repository;

        // The poller reads once, at the session's start, and then no more before the session ends.
        var service = Service(repository.Harness, runner, timings: new FlowTimings(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30)));
        var run = RunOf(repository);

        runner.OnStart("first", () => repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q1", "go", DateTimeOffset.UtcNow)));

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains("the answer 'go' handed over for question 'q1' was taken from the inbox", outcome.Details ?? []);
        Assert.Contains($"inbox: {run.InboxFile}", outcome.Details ?? []);
        Assert.Equal(["first", "after"], runner.Started);
        Assert.False(File.Exists(run.InboxFile));
    }

    /// <summary>A resume takes what was handed to an engine that ended before it took it: the answer applied before anything runs.</summary>
    [Fact]
    public async Task AResumeTakesTheInbox_BeforeAnythingRuns()
    {
        var (repository, service, runner) = await ServiceAsync("""
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

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q1", "go", DateTimeOffset.UtcNow));

        var resumed = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, resumed.ExitCode);
        Assert.Equal(["after"], runner.Started);
        Assert.False(File.Exists(run.InboxFile));
    }

    /// <summary>
    /// What the poller cannot read is said once and read again at the next poll - the poller goes on, and a pause asked
    /// after is still taken; a defect it meets is said and asks the run to pause, never leaving it with nobody reading.
    /// </summary>
    [Fact]
    public async Task WhatThePollerCannotRead_IsSaidOnce_AndItGoesOn()
    {
        var (repository, runner) = await RepositoryAsync(Waiting);
        using var disposed = repository;

        var warned = new Warned(repository.Harness.Output);
        var service = Service(repository.Harness, runner, output: warned);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Holds("slow", release.Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);
        var run = RunOf(repository);

        await runner.WaitStartedAsync("slow");
        repository.Harness.FileSystem.WriteAllTextAtomic(run.InboxFile, "not an inbox");

        await warned.First.WaitAsync(Bound, Token);
        File.Delete(run.InboxFile);

        var paused = await service.PauseAsync(repository.Path, "r1", all: true, json: false, Token).WaitAsync(Bound, Token);
        var outcome = await driving.WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, paused.ExitCode);
        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.StartsWith("what was asked of the engine of run 'r1' could not be read: ", Assert.Single(warned.Warnings), StringComparison.Ordinal);
    }

    /// <summary>A defect the poller meets is said, and the run is asked to pause: it is never left with nobody reading what is asked of it.</summary>
    [Fact]
    public async Task ADefectThePollerMeets_IsSaid_AndTheRunIsAskedToPause()
    {
        var (repository, runner) = await RepositoryAsync(Waiting);
        using var disposed = repository;

        var warned = new Warned(repository.Harness.Output);
        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, output: warned, fileSystem: files);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Holds("slow", release.Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);
        var run = RunOf(repository);

        await runner.WaitStartedAsync("slow");
        files.Breaks(run.InboxFile);
        repository.Harness.FileSystem.WriteAllTextAtomic(run.InboxFile, "{}");

        await warned.First.WaitAsync(Bound, Token);
        files.Mends(run.InboxFile);
        _ = release.TrySetResult();

        var outcome = await driving.WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("command", repository.Harness.OrchestrationStore.ReadFlow(run)!.Pause!.Cause);
        Assert.Equal(
            "the engine of run 'r1' can no longer read what is asked of it (InvalidOperationException: the disk said something no one expected); the run is asked to pause",
            Assert.Single(warned.Warnings));
    }

    /// <summary>A workflow whose question opens while a machine step holds the session open: the engine is running while the question waits.</summary>
    private const string Waiting = """
        name: flow
        steps:
          - name: holding
            type: pause
            question: what now?
            options: [stay, go]
          - name: after
            type: orient
            dependsOn: [holding]
          - name: warm
            type: orient
          - name: slow
            type: orient
            dependsOn: [warm]
        """;

    /// <summary>An answer taken from the inbox by a running engine is applied to the question it names, and the inbox emptied of it.</summary>
    [Fact]
    public async Task AnAnswerOfferedToARunningSession_IsTakenByItsLoop()
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
            var paused = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Paused, paused.Status);

            // The answer arrives from another terminal: the engine is offered it, and its next session - the loop of
            // it - takes and applies it, for the graph is the loop's alone to change.
            var offered = coordinator.OfferAnswerAsync("q1", "go");
            var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

            Assert.Equal(FlowRunStatus.Done, end.Status);
            Assert.True(await offered.WaitAsync(Bound, Token));
        }

        var questions = harness.OrchestrationStore.ReadFlowQuestions(run)!;

        Assert.Equal("answered", questions.Questions[0].State);
        Assert.Equal("answer-orchestrator", questions.Questions[0].Answers[0].By!.Through);
    }

    /// <summary>list-orchestrator says what the run's own files hold: its definition, its status, its engine, its open question - and with --questions and --steps each question and each step, by its visit and its directory.</summary>
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

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var listed = await repository.Harness.OrchestratorService.ListAsync(repository.Path, "r1", steps: true, questions: true, json: false, Token);

        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        var lines = listed.Details ?? [];
        Assert.Contains("  run of 'flow': paused, no engine", lines);
        Assert.Contains("  open question q1 (pause): what now?", lines);
        Assert.Contains($"  step holding: paused (visit 1, attempt 1) at '{run.StepDirectory("holding")}'", lines);
        Assert.Contains("  question q1 (pause, open): what now?", lines);

        // An engine recorded whose process has gone is said to be gone.
        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(repository.Harness.Identity.CurrentMachine, Engines.Engine, DateTimeOffset.UtcNow, "its start"));

        var gone = await repository.Harness.OrchestratorService.ListAsync(repository.Path, "r1", steps: false, questions: false, json: true, Token);
        var engine = JsonNode.Parse(Assert.Single(gone.Data))!["orchestrators"]![0]!["run"]!["engine"]!;

        Assert.Equal("gone", engine["state"]!.GetValue<string>());
        Assert.Equal($"process {Engines.Engine.ToString(CultureInfo.InvariantCulture)}", engine["process"]!.GetValue<string>());
    }

    /// <summary>delete-orchestrator refuses a run a live engine owns, naming its process, pause-orchestrator and stop-orchestrator.</summary>
    [Fact]
    public async Task DeleteOrchestratorRefusesARunALiveEngineOwns()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var identity = repository.Harness.Identity;

        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(identity.CurrentMachine, identity.CurrentId, DateTimeOffset.UtcNow, identity.Current));

        var outcome = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(
            $"Orchestrator 'r1' was not deleted: a live engine owns the run (process {identity.CurrentId.ToString(CultureInfo.InvariantCulture)}). "
            + $"'{DssHarness.Core.Hosts.ToolPackage.Command} pause-orchestrator r1' pauses it and '{DssHarness.Core.Hosts.ToolPackage.Command} stop-orchestrator r1' ends it, "
            + "and once the engine is gone the run can be deleted. Nothing was changed.",
            outcome.Message);
    }

    /// <summary>A run whose engine died - a crash, stop-orchestrator --now - is deleted with its owner file: an owner whose process has gone owns nothing.</summary>
    [Fact]
    public async Task ARunWhoseEngineDied_IsDeleted_WithItsOwnerFile()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);

        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(repository.Harness.Identity.CurrentMachine, Engines.Engine, DateTimeOffset.UtcNow, "its start"));

        var deleted = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.False(Directory.Exists(run.Directory));
        Assert.False(File.Exists(run.OwnerFile));
    }

    /// <summary>A run's own steps and report are kept records: deleted only with --delete-evidence, and the owner file beside the directory goes with the run.</summary>
    [Fact]
    public async Task ARunsStepsAndReportAreKeptRecords_DeletedOnlyWithEvidence()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        runner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);

        var refused = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: false, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("step file", refused.Message, StringComparison.Ordinal);
        Assert.Contains("report file", refused.Message, StringComparison.Ordinal);

        var deleted = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.False(Directory.Exists(run.Directory));
        Assert.False(File.Exists(run.OwnerFile));
    }

    /// <summary>A run whose steps no runner serves fails loudly, saying so: never a run that passes everything over.</summary>
    [Fact]
    public async Task NoRunnerServesAStepOfTheRunsType()
    {
        var (repository, _) = await RepositoryAsync("""
            name: flow
            steps:
              - name: one
                type: orient
            """);
        using var disposed = repository;

        var service = Service(repository.Harness, new NoStepRunner());

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.CommandFailed, outcome.ExitCode);

        var flow = repository.Harness.OrchestrationStore.ReadFlow(RunOf(repository))!;

        Assert.Equal("Failed", flow.Status);
        Assert.Contains(flow.Steps, step => step.Error!.Contains("no runner serves a step of type 'orient' yet", StringComparison.Ordinal));
    }

    /// <summary>
    /// In a real process on Linux and macOS, SIGINT is the run's first interrupt: the process is not ended by it, the
    /// interrupt is said, the run pauses safely, its report is written and its owner released, and the process exits 130.
    /// </summary>
    [Fact]
    public async Task ARealSigint_IsTheRunsFirstInterrupt_OnLinuxAndMacOS()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGINT is sent by the C library's kill; on Windows the console's Ctrl+C reaches the same registration, which FlowTerminal's other tests drive without a console.");

        var (repository, _) = await RepositoryAsync(InterruptedFlow);
        using var disposed = repository;
        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);
        start.ArgumentList.Add(repository.Path);
        start.Environment[TestHost.ChildModeVariable] = "flow-interrupted";

        using var child = Process.Start(start)!;
        var lines = new BlockingCollection<string>();
        child.OutputDataReceived += (_, line) =>
        {
            if (line.Data is { } data)
            {
                lines.Add(data);
            }
        };
        child.BeginOutputReadLine();

        try
        {
            Assert.Equal("started two", Next(lines, line => line.StartsWith("started two", StringComparison.Ordinal)));
            Assert.Equal(0, kill(child.Id, Interrupt));
            Assert.StartsWith("interrupted: a safe pause was asked", Next(lines, line => line.StartsWith("interrupted", StringComparison.Ordinal)), StringComparison.Ordinal);
            Assert.True(child.WaitForExit(Bound), "the child ended once the run was paused");

            var run = RunOf(repository);
            var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

            Assert.Equal(HarnessExit.Cancelled, child.ExitCode);
            Assert.Equal("Paused", flow.Status);
            Assert.Equal("interrupt", flow.Pause!.Cause);
            Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
            Assert.True(File.Exists(run.ReportFile));
        }
        finally
        {
            Ending(child);
        }
    }

    /// <summary>The run the real interrupt is sent to: its second step a machine step the pause leaves running.</summary>
    private const string InterruptedFlow = """
        name: flow
        steps:
          - name: one
            type: orient
          - name: two
            type: orient
            dependsOn: [one]
        """;

    /// <summary>
    /// The child the real interrupt is sent to: it runs <see cref="InterruptedFlow"/> in the repository its argument
    /// names behind a terminal that takes the process's own signals, says when its machine step has started and what
    /// each interrupt asked, lets that step finish once the first is said, and exits with the session's code.
    /// </summary>
    internal static int InterruptedChild(TextWriter output, string[] arguments)
    {
        var harness = new HarnessFactory();
        var runner = new SteppedRunner();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var said = new Warned(harness.Output, line =>
        {
            output.WriteLine(line);
            _ = release.TrySetResult();
        });

        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", release.Task);
        runner.OnStart("two", () => output.WriteLine("started two"));

        var service = Service(harness, runner);
        var terminal = new FlowTerminal(said, FlowService.RunCommand);
        var outcome = terminal.RunAsync(
            (interrupts, token) => service.RunAsync(arguments[0], Workflow, run: null, inputs: null, json: false, interrupts, token),
            CancellationToken.None).GetAwaiter().GetResult();

        output.WriteLine($"exit {outcome.ExitCode.ToString(CultureInfo.InvariantCulture)}");
        return outcome.ExitCode;
    }

    /// <summary>The next line the child says that <paramref name="matches"/>, within the bound.</summary>
    private static string Next(BlockingCollection<string> lines, Func<string, bool> matches)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bound.CancelAfter(Bound);

        foreach (var line in lines.GetConsumingEnumerable(bound.Token))
        {
            if (matches(line))
            {
                return line;
            }
        }

        throw new InvalidOperationException("the child said nothing more");
    }

    /// <summary>A process of this test's own that sleeps for five minutes, standing for an engine.</summary>
    private static Process Sleeper()
    {
        var start = new ProcessStartInfo(TestHost.DotnetExecutable) { UseShellExecute = false };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);
        start.ArgumentList.Add("300000");
        start.Environment[TestHost.ChildModeVariable] = "sleep";

        return Process.Start(start)!;
    }

    /// <summary>Ends a process of this test's own, where it still runs.</summary>
    private static void Ending(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(Bound);
            }
        }
        catch (InvalidOperationException)
        {
            // It ended meanwhile.
        }
    }

    /// <summary>The terminal's interruption, SIGINT, as Linux and macOS number it.</summary>
    private const int Interrupt = 2;

    /// <summary>Signals a process on Linux and macOS.</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int kill(int process, int signal);

    /// <summary>A runner whose steps are scripted: one result each, or a task the test holds until it releases it - safe to script from the test while the session runs it.</summary>
    internal sealed class SteppedRunner : IFlowStepRunner
    {
        private readonly ConcurrentDictionary<string, FlowStepResult> _results = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task> _holds = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Action> _onStart = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _order = new();
        private volatile FlowStepResult? _every;

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public IReadOnlyList<string> Started => [.. _order];

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public void On(string step, FlowStepResult result) => _results[step] = result;

        /// <summary>Answers every step with one result.</summary>
        public void OnEvery(FlowStepResult result) => _every = result;

        /// <summary>Holds <paramref name="step"/> until <paramref name="release"/> completes, as a machine step the pause leaves running.</summary>
        public void Holds(string step, Task release) => _holds[step] = release;

        /// <summary>Does <paramref name="what"/> when <paramref name="step"/> starts, before it answers.</summary>
        public void OnStart(string step, Action what) => _onStart[step] = what;

        /// <summary>Waits, within the bound, until the runner has been asked for <paramref name="step"/>.</summary>
        public Task WaitStartedAsync(string step) => StartOf(step).Task.WaitAsync(Bound, Token);

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            _order.Enqueue(run.Step.Name);

            if (_onStart.TryGetValue(run.Step.Name, out var what))
            {
                what();
            }

            _ = StartOf(run.Step.Name).TrySetResult();

            if (_holds.TryRemove(run.Step.Name, out var held))
            {
                // A hold is one attempt's: what a stop or a pause ends, and the resume's own attempt of the step runs
                // as a step that is no longer held.
                await held.WaitAsync(cancellationToken);
                return FlowStepResult.Ended(passed: true, 0);
            }

            return _every ?? (_results.TryGetValue(run.Step.Name, out var result) ? result : FlowStepResult.Ended(passed: true, 0));
        }

        /// <inheritdoc/>
        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        private TaskCompletionSource StartOf(string step)
            => _started.GetOrAdd(step, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
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

    /// <summary>This process's identity, with one engine of another process whose liveness the test decides.</summary>
    private sealed class Engines(IProcessIdentity real) : IProcessIdentity
    {
        /// <summary>The engine's process id: one no process of this machine carries.</summary>
        public const int Engine = 999_999_001;

        private volatile bool _alive = true;

        /// <summary>Whether the engine still runs.</summary>
        public bool Alive
        {
            get => _alive;
            set => _alive = value;
        }

        public int CurrentId => real.CurrentId;

        public string CurrentMachine => real.CurrentMachine;

        public string? Current => real.Current;

        public bool IsAlive(int processId, string? stamp) => processId == Engine ? _alive : real.IsAlive(processId, stamp);

        public string? StampOf(int processId) => real.StampOf(processId);

        public bool IsSame(int processId, string stamp) => processId != Engine && real.IsSame(processId, stamp);
    }

    /// <summary>A file system that says when a file is written or deleted, and can be made to meet a defect reading one.</summary>
    private sealed class Signalling(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _written = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _deleted = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _broken = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Completes once <paramref name="path"/> is written.</summary>
        public Task Written(string path) => Of(_written, path).Task;

        /// <summary>Completes once <paramref name="path"/> is deleted.</summary>
        public Task Deleted(string path) => Of(_deleted, path).Task;

        /// <summary>Makes every read of <paramref name="path"/> meet a defect.</summary>
        public void Breaks(string path) => _broken[Path.GetFullPath(path)] = true;

        /// <summary>Reads <paramref name="path"/> as it is again.</summary>
        public void Mends(string path) => _broken.TryRemove(Path.GetFullPath(path), out _);

        public override void WriteAllTextAtomic(string path, string contents)
        {
            base.WriteAllTextAtomic(path, contents);
            _ = Of(_written, path).TrySetResult();
        }

        public override void DeleteFile(string path)
        {
            base.DeleteFile(path);
            _ = Of(_deleted, path).TrySetResult();
        }

        public override string ReadAllText(string path)
            => _broken.ContainsKey(Path.GetFullPath(path))
                ? throw new InvalidOperationException("the disk said something no one expected.")
                : base.ReadAllText(path);

        private static TaskCompletionSource Of(ConcurrentDictionary<string, TaskCompletionSource> signals, string path)
            => signals.GetOrAdd(Path.GetFullPath(path), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>An output that keeps every warning it is given, says when the first comes, and passes everything on.</summary>
    private sealed class Warned(IHarnessOutput inner, Action<string>? also = null) : IHarnessOutput
    {
        private readonly ConcurrentQueue<string> _warnings = new();
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Every warning, in order.</summary>
        public IReadOnlyList<string> Warnings => [.. _warnings];

        /// <summary>Completes at the first warning.</summary>
        public Task First => _first.Task;

        public bool IsVerbose => inner.IsVerbose;

        public bool IsDataOnly => inner.IsDataOnly;

        public void Publish(RunEvent @event) => inner.Publish(@event);

        public void Ok(string command, string message) => inner.Ok(command, message);

        public void Fail(string command, string message) => inner.Fail(command, message);

        public void Warn(string command, string message)
        {
            _warnings.Enqueue(message);
            also?.Invoke(message);
            _ = _first.TrySetResult();
            inner.Warn(command, message);
        }

        public void Info(string command, string message) => inner.Info(command, message);

        public void Detail(string command, string message) => inner.Detail(command, message);

        public void Data(string text) => inner.Data(text);

        public void Raw(string line) => inner.Raw(line);

        public void RawError(string line) => inner.RawError(line);

        public string Shown(string text) => inner.Shown(text);

        public IDisposable DataOnly() => inner.DataOnly();
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
