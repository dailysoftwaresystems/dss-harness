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
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
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

    private static async Task<(Repository Repository, SteppedRunner Runner)> RepositoryAsync(string flow, HarnessConfig? config = null, IReadOnlyDictionary<string, string>? files = null)
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, config ?? new HarnessConfig { Workflows = [Workflow] });
        await harness.CommitAllAsync(temp.Path, "initial", Token);
        temp.WriteFile($".harness-config/workflows/{Workflow}/{Workflow}.yml", flow);

        if (files is not null)
        {
            foreach (var (path, text) in files)
            {
                temp.WriteFile($".harness-config/workflows/{Workflow}/{path}", text);
            }
        }

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
        IProcessIdentity? identity = null,
        ChildSessions? children = null,
        DssHarness.Core.Agents.IAgentRunner? agentRunner = null,
        DssHarness.Core.Secrets.AgentKeyStore? keyStore = null)
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
            new FlowSessions(store, files, engines, harness.GitClient),
            new WorkflowValidationService(harness.ContextLoader, harness.FileSystem, harness.Platform, new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform), DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild),
            runner,
            harness.OrchestrationLog,
            children ?? harness.ChildSessions,
            harness.PathBudget,
            output ?? harness.Output,
            harness.GitClient,
            harness.AgentService,
            agentRunner ?? new OfNoStep(),
            keyStore ?? new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            clock,
            timings ?? Quick);
    }

    private static OrchestratorLayout RunOf(Repository repository, string run = "r1")
        => new(run, Path.Combine(repository.Path, ".orchestrators", run));

    /// <summary>
    /// A workflow that takes plan updates whole: an intake pause asking the request itself, one in between, a planning
    /// group with its planner and the check beside it, the work-item group with an alternative beside the step that
    /// builds - and a pause at the end asking the request again, which says what the input reads by then.
    /// </summary>
    private const string Amended = """
        name: flow
        inputs:
          - name: request
        steps:
          - name: ask
            type: pause
            question: '{input.request}'
            options: [stay, go]
          - name: warmup
            type: orient
            dependsOn: [ask]
          - name: mid
            type: pause
            question: and in between?
            options: [stay, go]
            dependsOn: [warmup]
          - name: planning
            dependsOn: [mid]
            gather: [items, scope]
            steps:
              - name: replan
                type: reason
                agent: planner
                prompt: update.md
                outputs: update.json
              - name: planupdate
                type: reason
                agent: planner
                prompt: update.md
                outputs: update.json
              - name: check
                type: orient
                dependsOn: [replan]
          - name: itemcheck
            type: validate-plan
            dependsOn: [planning]
            items: 'planning.items'
          - name: items
            dependsOn: [itemcheck]
            forEach: 'itemcheck.items'
            steps:
              - name: build
                type: orient
              - name: rebuild
                type: orient
          - name: after
            type: pause
            question: '{input.request}'
            options: [stay, go]
            dependsOn: [items]
        amend:
          input: request
          changedAnswerMinutes: 5
          step: planning.planupdate
          then: check
          scope: 'planning.scope'
          whenAddScope: items.build
          whenChangeScope: items.rebuild
          whenRemoveScope: items.build
        """;

    /// <summary>
    /// The workflow of <see cref="Amended"/> in a repository whose config declares the agent sections as init writes
    /// them, with its planner's agent definition, prompt and output schema beside the definition.
    /// </summary>
    private static async Task<(Repository Repository, FlowService Service, SteppedRunner Runner)> AmendedAsync(string? flow = null)
    {
        // The agent sections the workflow's reason steps resolve their tiers against: as init writes them.
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();
        var (repository, runner) = await RepositoryAsync(
            flow ?? Amended,
            config: new HarnessConfig
            {
                Workflows = [Workflow],
                Defaults = new() { AgentTier = agents.AgentTier },
                Providers = [.. agents.Providers],
                TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase),
            },
            files: new Dictionary<string, string>
            {
                ["agents/planner.md"] = "---\nname: planner\ndescription: plans the change\n---\nYou plan the change.\n",
                ["update.md"] = "plan the change\n",
                ["update.json"] = "{ \"type\": \"object\", \"properties\": { } }",
            });
        return (repository, Service(repository.Harness, runner), runner);
    }

    /// <summary>Every step of a scripted run passes, and the planner and the plan check answer what the flow reads of them.</summary>
    private static void Script(SteppedRunner runner)
    {
        runner.On("replan", FlowStepResult.Ended(passed: true, 0, output: new JsonObject { ["items"] = new JsonArray("one"), ["scope"] = new JsonArray() }));
        runner.On("planupdate", FlowStepResult.Ended(passed: true, 0, output: new JsonObject { ["items"] = new JsonArray("two"), ["scope"] = new JsonArray() }));
        runner.On("itemcheck", FlowStepResult.Ended(passed: true, 0, output: new JsonObject { ["items"] = new JsonArray("one") }));
    }

    /// <summary>Drives the paused run of <paramref name="service"/> on by answering every pause it reaches, one answer a session, until it is done.</summary>
    private static async Task DoneAsync(Repository repository, FlowService service)
    {
        for (var answers = 0; answers < 10; answers++)
        {
            var open = repository.Harness.OrchestrationStore.ReadFlowQuestions(RunOf(repository))?.Open ?? [];

            if (open.Count == 0)
            {
                return;
            }

            _ = await service.AnswerAsync(repository.Path, "r1", "go", open[0].Id, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        }
    }

    /// <summary>
    /// An amendment given while no engine runs is recorded in <c>updates.json</c> as an update <c>queued</c>, nothing
    /// else changed and nothing started; the next session of the run takes it before anything else - and, where no
    /// plan holds it, records it <c>done</c> at once, so every step that starts reads the request with its
    /// amendments: the value as given, then the amendment under a line that says when it was made.
    /// </summary>
    [Fact]
    public async Task AnAmendmentGivenWhileNoEngineRuns_IsRecordedQueued_AndTakenByTheNextSession_BeforeAnythingElse()
    {
        var (repository, service, runner) = await AmendedAsync();
        using var disposed = repository;

        Script(runner);
        var started = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["request=build the thing"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        Assert.Equal(FlowExit.Paused, started.ExitCode);
        var run = RunOf(repository);
        var store = repository.Harness.OrchestrationStore;

        var amended = await service.AmendAsync(repository.Path, "r1", "in fact, only the parser", textFile: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, amended.ExitCode);
        var queued = Assert.Single(store.ReadFlowUpdates(run)!.Updates);
        Assert.Equal(("u1", "amendment", "queued", "in fact, only the parser", "amend-orchestrator-request"), (queued.Id, queued.Cause, queued.State, queued.Amendment, queued.Through));

        // The next session - here an answer - takes it first: no plan holds it yet, so it is done at once, and what
        // the run reads from then on says the request with its amendment.
        var answered = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowExit.Paused, answered.ExitCode);
        var taken = Assert.Single(store.ReadFlowUpdates(run)!.Updates);
        Assert.Equal(("u1", "done"), (taken.Id, taken.State));
        Assert.Contains(answered.Details ?? [], line => line.StartsWith("update u1, an amendment given while no engine ran the run, was taken", StringComparison.Ordinal));

        await DoneAsync(repository, service);

        var asked = store.ReadFlowQuestions(run)!.Questions[^1];
        Assert.True(
            asked.ProblemShown!.Happened!.StartsWith("build the thing", StringComparison.Ordinal),
            asked.ProblemShown!.Happened!);
        Assert.Contains("\n\namended at ", asked.ProblemShown!.Happened, StringComparison.Ordinal);
        Assert.EndsWith(":\nin fact, only the parser", asked.ProblemShown!.Happened, StringComparison.Ordinal);

        // The report says where the update stands, and what is left for a person says the amendment was refused
        // none of it was: it is done, and every step that started read the request with it.
        Assert.Contains("- u1 (amendment), taken at ", File.ReadAllText(run.ReportFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// An amendment given while an engine runs the run is handed to it through <c>inbox.json</c> and taken at its
    /// poll: weighed against the same gates, recorded in <c>updates.json</c> under the inbox entry's id, and read
    /// with the request by every step that starts from then on.
    /// </summary>
    [Fact]
    public async Task AnAmendmentGivenToARunningEngine_IsTakenFromTheInbox_AndReadWithTheRequest()
    {
        var (repository, service, runner) = await AmendedAsync();
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["request=build the thing"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var store = repository.Harness.OrchestrationStore;

        var held = new TaskCompletionSource();
        runner.Holds("warmup", held.Task);
        var answering = service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token);
        await runner.WaitStartedAsync("warmup");

        var amended = await service.AmendAsync(repository.Path, "r1", "in fact, only the parser", textFile: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, amended.ExitCode);
        Assert.Contains(amended.Details ?? [], line => line == $"inbox: {run.InboxFile}");

        held.SetResult();
        var outcome = await answering.WaitAsync(Bound, Token);

        Assert.Contains(outcome.Details ?? [], line => line.StartsWith("the amendment handed over was taken from the inbox", StringComparison.Ordinal));
        Assert.Equal(FlowExit.Paused, outcome.ExitCode);

        await DoneAsync(repository, service);

        var taken = Assert.Single(store.ReadFlowUpdates(run)!.Updates);
        Assert.Equal(("u1", "amendment", "done"), (taken.Id, taken.Cause, taken.State));
        Assert.NotNull(taken.Inbox);
        Assert.False(File.Exists(run.InboxFile));

        var asked = store.ReadFlowQuestions(run)!.Questions[^1];
        Assert.EndsWith(":\nin fact, only the parser", asked.ProblemShown!.Happened, StringComparison.Ordinal);
    }

    /// <summary>
    /// An amendment asked of a run whose plan's scope has begun is taken by the next session: the update is
    /// <c>underWay</c>, the step the <c>amend</c> block names is raised - a new visit of it in the newest instance of
    /// its scope, which had ended and is in progress again - and the run is held while it plans. The block's
    /// <c>then</c> is followed when it is done - the check runs again - and once that scope has come to rest the plan
    /// stands: the update is <c>done</c>, the hold ends, and every step that starts from there reads the request with
    /// its amendments.
    /// </summary>
    [Fact]
    public async Task AnAmendmentTakenWhereThePlansScopeHasBegun_RaisesThePlanningStep_AndThePlanStandsWhenItsScopeRests()
    {
        var (repository, service, runner) = await AmendedAsync();
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["request=build the thing"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var store = repository.Harness.OrchestrationStore;

        // Past the planning, paused at the last pause: the amendment is recorded - the command's gates pass - and the
        // next session takes it by raising the planning step.
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        _ = await service.AmendAsync(repository.Path, "r1", "in fact, only the parser", textFile: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var answered = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.True(HarnessExit.Success == answered.ExitCode, $"{answered.ExitCode}: {answered.Message}");

        // The session that took it drove the update whole: the planner raised and ended, the update done, and the run
        // ended done with the request read with its amendment.
        var update = Assert.Single(store.ReadFlowUpdates(run)!.Updates);
        Assert.Equal(("u1", "done"), (update.Id, update.State));
        Assert.NotNull(update.Step);
        Assert.Equal("check", update.Then);
        Assert.Null(update.Why);

        var steps = store.ReadFlow(run)!.Steps;
        Assert.Single(steps, step => step.Step == "planning.planupdate" && step.RaisedByUpdate == "u1");
        Assert.Equal(2, steps.Count(step => step.Step == "planning.check"));
        Assert.Contains(steps, step => step.Step == "planning.check" && step.LeftBy == "planning.planupdate");

        // The pause after had asked before the update was taken, and its question stands as it was filled: a step
        // that started reads the request with its amendments, and one that had asked already asks nothing again.
        var asked = store.ReadFlowQuestions(run)!.Questions[^1];
        Assert.Equal("build the thing", asked.ProblemShown!.Happened);
    }

    /// <summary>
    /// The command's own refusals, read of the run as it stands and of the definition it began with: a run that is
    /// done has nothing to go back to, one past the step its <c>amend.before</c> names has a plan that is no longer
    /// changed, and a definition whose amend names no input takes no amended request. Nothing is recorded for one,
    /// and an empty amendment, or one given two ways, is a usage error.
    /// </summary>
    [Fact]
    public async Task AmendCommandRefusals_NothingRecorded()
    {
        var (repository, service, runner) = await AmendedAsync(Amended.Replace("  input: request", "  before: after\n  input: request", StringComparison.Ordinal));
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["request=build the thing"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var store = repository.Harness.OrchestrationStore;

        // To the last pause, past the step before names: refused, nothing recorded.
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var before = await service.AmendAsync(repository.Path, "r1", "narrow it", textFile: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, before.ExitCode);
        Assert.Equal("run 'r1' has started 'after': what it built is being written down, and its plan is no longer changed. Start a run for the request once this one has ended", before.Message);
        Assert.Empty(store.ReadFlowUpdates(run)!.Updates);

        // Done: nothing left to go back to.
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var done = await service.AmendAsync(repository.Path, "r1", "narrow it", textFile: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, done.ExitCode);
        Assert.Equal("run 'r1' is done; there is nothing left to go back to", done.Message);
        Assert.Empty(store.ReadFlowUpdates(run)!.Updates);

        // A definition whose amend names no input takes no amended request at all.
        var (plain, plainService, plainRunner) = await AmendedAsync(Amended.Replace("  input: request", string.Empty, StringComparison.Ordinal));
        using var plainDisposed = plain;

        plainRunner.OnEvery(FlowStepResult.Ended(passed: true, 0));
        _ = await plainService.RunAsync(plain.Path, Workflow, run: null, inputs: ["request=build the thing"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var noInput = await plainService.AmendAsync(plain.Path, "r1", "narrow it", textFile: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, noInput.ExitCode);
        Assert.Equal("orchestrator 'flow' takes no amended request: its amend names no input", noInput.Message);

        // An empty amendment, and one given two ways, are usage errors.
        Assert.Equal(HarnessExit.UsageError, (await service.AmendAsync(repository.Path, "r1", "  ", textFile: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token)).ExitCode);
        Assert.Equal(HarnessExit.UsageError, (await service.AmendAsync(repository.Path, "r1", "one", textFile: "two.txt", json: false, cancellationToken: Token).WaitAsync(Bound, Token)).ExitCode);
    }

    /// <summary>
    /// A changed answer within its window is taken once, where no plan holds it: the entry keeps both answers, each
    /// with when it was given, and the answer now stands; the update is recorded <c>done</c>; and a <c>pause</c>
    /// step whose answer was changed is visited again, done at once with the new answer, so whatever reads the step
    /// from then on reads the new one. A second change of the set is refused, and so is one that is the answer the
    /// set already has.
    /// </summary>
    [Fact]
    public async Task AChangedAnswerWithinItsWindow_IsTakenOnce_BothAnswersKept_AndAPauseStepRevisitedDone()
    {
        var (repository, service, runner) = await AmendedAsync();
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["request=build the thing"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var store = repository.Harness.OrchestrationStore;

        // The run stands paused at the pause in between, the intake's answer the last a person gave and no plan
        // begun: the change is taken by the session the command starts.
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var changed = await service.AnswerAsync(repository.Path, "r1", "stay", question: null, text: null, textFile: null, again: true, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowExit.Paused, changed.ExitCode);
        var first = store.ReadFlowQuestions(run)!.Questions[0];
        Assert.Equal("answered", first.State);
        Assert.Equal(2, first.Answers.Count);
        Assert.Equal(("go", false), (first.Answers[0].Answer, first.Answers[0].ReplacedAt is null));
        Assert.NotNull(first.Answers[0].ReplacedAt);
        Assert.Equal(("stay", "person", "u1"), (first.Answers[^1].Answer, first.Answers[^1].By!.Who, first.Answers[^1].Update));

        var update = Assert.Single(store.ReadFlowUpdates(run)!.Updates);
        Assert.Equal(("u1", "changedAnswer", "done", "q1", "go", "stay"), (update.Id, update.Cause, update.State, update.Question, update.FromAnswer, update.ToAnswer));

        // The pause step that asked is visited again, done at once with the new answer.
        var asked = store.ReadFlow(run)!.Steps.Where(step => step.Step == "ask").ToList();
        Assert.Equal(2, asked.Count);
        Assert.Equal(("Done", "stay"), (asked[^1].State, (string?)asked[^1].Output!["answer"]));

        // The set is changed one time: a second change is refused, and one that is the answer it already has is a
        // usage error.
        var second = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: true, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, second.ExitCode);
        Assert.StartsWith("question 'q1' of run 'r1' had its answer changed already, at ", second.Message, StringComparison.Ordinal);
        Assert.EndsWith("a set is changed one time", second.Message, StringComparison.Ordinal);

        var same = await service.AnswerAsync(repository.Path, "r1", "stay", question: null, text: null, textFile: null, again: true, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.UsageError, same.ExitCode);
        Assert.Equal("'stay' is the answer 'q1' already has; a changed answer changes it", same.Message);
    }

    /// <summary>
    /// A changed answer past its window is refused - naming the answer, when it was given, until when it could be
    /// changed and what time it is - and the answer stands; where the run's plan has begun, the change is taken: the
    /// update is under way, the step the <c>amend</c> block names is raised and plans it with both answers, and the
    /// block's <c>then</c> is followed - the check runs again - until the plan stands again and the update is done.
    /// </summary>
    [Fact]
    public async Task AChangedAnswerPastItsWindow_IsRefused_AndWhereAPlanHasBegun_IsPlannedAndThePlanStands()
    {
        var (repository, service, runner) = await AmendedAsync();
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["request=build the thing"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var store = repository.Harness.OrchestrationStore;

        // The window is past: the answer was written ten minutes ago, and the workflow gives five.
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var questions = store.ReadFlowQuestions(run)!;
        var aged = questions with
        {
            Questions =
            [
                .. questions.Questions.Select(question => question.Id == "q1"
                    ? question with { Answers = [new FlowAnswerRecord("go", DateTimeOffset.UtcNow.AddMinutes(-10), By: new FlowAnswerBy("person", "answer-orchestrator"))] }
                    : question),
            ],
        };
        store.WriteFlowQuestions(run, aged);

        var late = await service.AnswerAsync(repository.Path, "r1", "stay", question: null, text: null, textFile: null, again: true, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, late.ExitCode);
        Assert.StartsWith("question q1 of run 'r1' was answered 'go' at ", late.Message, StringComparison.Ordinal);
        Assert.Contains("The answer stands. To change what the run builds, amend its request: amend-orchestrator-request r1 \"<what to change>\"", late.Message, StringComparison.Ordinal);
        Assert.Single(store.ReadFlowQuestions(run)!.Questions[0].Answers);
        Assert.Empty(store.ReadFlowUpdates(run)!.Updates);

        // Where the run's plan has begun, the change is taken within its window: the update is planned by the step
        // the amend block names, and both answers are kept.
        _ = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var begun = await service.AnswerAsync(repository.Path, "r1", "stay", question: null, text: null, textFile: null, again: true, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        // The change is taken and its update planned while the session runs; the run itself parks on the question
        // the pause after asks, which no step answers.
        Assert.True(FlowExit.Paused == begun.ExitCode, $"{begun.ExitCode}: {begun.Message}");

        var update = Assert.Single(store.ReadFlowUpdates(run)!.Updates);
        Assert.Equal(("u1", "changedAnswer", "done", "q2", "go", "stay"), (update.Id, update.Cause, update.State, update.Question, update.FromAnswer, update.ToAnswer));
        Assert.NotNull(update.Step);
        Assert.Equal("check", update.Then);

        var answers = store.ReadFlowQuestions(run)!.Questions[1].Answers;
        Assert.Equal(2, answers.Count);
        Assert.Equal(("go", "stay", "u1"), (answers[0].Answer, answers[1].Answer, answers[1].Update));

        var steps = store.ReadFlow(run)!.Steps;
        Assert.Single(steps, step => step.Step == "planning.planupdate" && step.RaisedByUpdate == "u1");
        Assert.Equal(2, steps.Count(step => step.Step == "planning.check"));
    }
    /// <summary>A run starts the next r-N, writes every record of the run, drives the session to its end, releases the owner, and names every record it wrote.</summary>
    [Fact]
    public async Task ARunStarts_RecordsAndDrivesTheSessionToItsEnd_NamingEveryRecord()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        Script(runner);

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
                $"updates: {run.UpdatesFile}",
                $"issues: {run.PreExistingIssuesFile}",
                $"definition: {run.DefinitionFile}",
                $"events: {run.EventsFile}",
                $"report: {run.ReportFile}",
                $"reportJson: {run.ReportJsonFile}",
                $"folds: {run.FoldsFile}",
                $"log {run.LogFile("r1")}",
            ],
            outcome.Details);
    }

    /// <summary>
    /// A run of a workflow is driven with what the repository's configuration says: its create-agent step asked for the
    /// model the configuration resolves for it, and a run step whose runner writes the tree holds that tree whole - a step
    /// that reads it starts once the run step has ended, never beside it.
    /// </summary>
    [Fact]
    public async Task ARunOfAWorkflow_MakesItsAgentForTheConfiguredModel_AndAWritingRunnerHoldsWrite()
    {
        var (repository, runner) = await RepositoryAsync(
            """
            name: flow
            steps:
              - name: make
                type: create-agent
                agent: worker
              - name: tidy
                type: run
                runner: format
                in: make
                dependsOn: [make]
              - name: map
                type: graphify-codebase
                in: make
                dependsOn: [make]
            """,
            new HarnessConfig
            {
                Workflows = [Workflow],
                Defaults = new HarnessDefaults { AgentTier = "balanced" },
                Providers =
                [
                    new()
                    {
                        Name = "zai", Type = "zai", Api = "https://api.z.ai", ApiKeyNames = ["ZAI_API_KEY"], Client = "glm",
                        Currency = "USD", ReadOn = "2026-10-12",
                        Models = [new() { Code = "glm-5.3", Name = "GLM 5.3", Tiers = ["balanced"], Efforts = ["low", "high", "max"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 }],
                    },
                ],
                TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["balanced"] = ["glm-5.3"] },
                PredefinedRunners = { ["format"] = new RunnerConfig { WritesTree = true, Phases = [new RunnerPhase { Name = "tidy", Command = ["dotnet", "--version"] }] } },
            });
        using var disposed = repository;
        repository.Tree.WriteFile($".harness-config/workflows/{Workflow}/agents/worker.md", "You tidy.\n");
        var service = Service(repository.Harness, runner);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mappedWhileHeld = false;
        runner.Holds("tidy", release.Task);
        runner.OnStart("map", () => mappedWhileHeld = !release.Task.IsCompleted);

        var session = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);
        var tidying = runner.WaitStartedAsync("tidy");

        if (await Task.WhenAny(tidying, session) == session)
        {
            var ended = await session;
            Assert.Fail($"the run ended before its run step started: {ended.Message} {string.Join(" | ", ended.Details ?? [])}");
        }

        release.SetResult();
        var outcome = await session.WaitAsync(Bound, Token);

        Assert.True(outcome.ExitCode == HarnessExit.Success, outcome.Message);
        Assert.Equal(new FlowMadeAgent("a1", "glm-5.3"), runner.Asked.Single(run => run.Instance == "make").Made);
        Assert.Equal(["make", "tidy", "map"], runner.Started);
        Assert.False(mappedWhileHeld, "the step that reads the agent's tree started while the writing run held it");
    }

    /// <summary>
    /// A start whose pull request's base cannot be told - the repository has a remote, and it names no default branch here
    /// - says so, the checkout's branch held to none of it.
    /// </summary>
    [Fact]
    public async Task AStartWhoseBaseCannotBeTold_SaysSo()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;
        await repository.Harness.RunGitAsync(repository.Path, ["remote", "add", "origin", Path.Combine(repository.Path, "nowhere")], Token);
        await repository.Harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "feature"], Token);
        Script(runner);

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(
            "the branch the run's pull request is opened against cannot be told here - no pull-request step names a base, and remote "
                + "'origin' names no default branch here - so the main checkout's branch 'feature' was held to none: name the base on the "
                + "pull-request step, or read the remote's own with git remote set-head origin --auto",
            outcome.Details![0]);
    }

    /// <summary>A run records the commit and the branch it started from, as the probe its orient step reads them.</summary>
    [Fact]
    public async Task ARun_RecordsTheCommitAndTheBranchItStartedFrom()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;
        var git = repository.Harness;
        await git.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "feature"], Token);
        var head = (await git.RunGitAsync(repository.Path, ["rev-parse", "HEAD"], Token)).StandardOutput.Trim();

        Script(runner);

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(RunOf(repository))!;
        Assert.Equal(head, flow.StartCommit);
        Assert.Equal("feature", flow.Branch);
    }

    /// <summary>
    /// The branch a run's start holds the checkout to is its pull request's base, filled from the run's inputs, where its
    /// pull-request step names one: refused on it, started off it.
    /// </summary>
    [Fact]
    public async Task ARunsStart_HoldsTheCheckoutToItsPullRequestsBase()
    {
        var (repository, service, runner) = await ServiceAsync("""
            name: flow
            inputs:
              - name: target
                default: work
            steps:
              - name: one
                type: orient
              - name: pr
                type: pull-request
                dependsOn: [one]
                title: the work
                body: body.md
                base: '{input.target}'
            """);
        using var disposed = repository;
        repository.Tree.WriteFile($".harness-config/workflows/{Workflow}/body.md", "the work\n");
        await repository.Harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "work"], Token);
        Script(runner);

        var refused = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal("run 'r1' of 'flow' cannot start: the main checkout is on 'work', the branch the run's pull request is opened against, and a run commits its work on a branch of its own: create one for it and check it out. Nothing was made.", refused.Message);
        Assert.False(Directory.Exists(RunOf(repository).Directory));

        var started = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["target=release"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, started.ExitCode);
    }

    /// <summary>
    /// With no pull-request base, the branch a run's start holds the checkout to is the default of the remote its push step
    /// names, not origin's; and a merge under way refuses a start too.
    /// </summary>
    [Fact]
    public async Task ARunsStart_ReadsThePushRemotesDefault_AndIsRefusedWhileAMergeIsUnderWay()
    {
        var (repository, service, _) = await ServiceAsync("""
            name: flow
            steps:
              - name: one
                type: orient
              - name: send
                type: push
                remote: upstream
                dependsOn: [one]
            """);
        using var disposed = repository;
        using var origin = new TempDirectory();
        using var upstream = new TempDirectory();
        var git = repository.Harness;
        var branch = (await git.RunGitAsync(repository.Path, ["symbolic-ref", "--short", "HEAD"], Token)).StandardOutput.Trim();

        foreach (var (name, remote, head) in new[] { ("origin", origin, branch), ("upstream", upstream, "trunk") })
        {
            await git.RunGitAsync(remote.Path, ["init", "--quiet", "--bare", "."], Token);
            await git.RunGitAsync(repository.Path, ["remote", "add", name, remote.Path], Token);
            await git.RunGitAsync(repository.Path, ["push", "--quiet", name, $"{branch}:{head}"], Token);
            await git.RunGitAsync(repository.Path, ["remote", "set-head", name, head], Token);
        }

        async Task<string> RefusedAsync()
        {
            var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.False(Directory.Exists(RunOf(repository).Directory));
            return outcome.Message;
        }

        await git.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "trunk"], Token);
        Assert.Equal("run 'r1' of 'flow' cannot start: the main checkout is on 'trunk', the branch the run's pull request is opened against, and a run commits its work on a branch of its own: create one for it and check it out. Nothing was made.", await RefusedAsync());

        await git.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "side"], Token);
        repository.Tree.WriteFile("file.txt", "side\n");
        await git.CommitAllAsync(repository.Path, "side", Token);
        await git.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "feature", "trunk"], Token);
        repository.Tree.WriteFile("file.txt", "feature\n");
        await git.CommitAllAsync(repository.Path, "feature", Token);
        Assert.False((await git.GitClient.RunAsync(repository.Path, ["merge", "--quiet", "side"], cancellationToken: Token)).Succeeded);
        Assert.Equal("run 'r1' of 'flow' cannot start: a merge is under way in the main checkout, and a run folds its work into the tree the merge is writing: finish the merge, or abort it. Nothing was made.", await RefusedAsync());
    }

    /// <summary>A start whose pull request's base cannot be filled is refused, saying why, before the checkout is weighed - here a detached HEAD.</summary>
    [Fact]
    public void AStartWhosePullRequestsBaseCannotBeFilled_IsRefused()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            steps:
              - name: pr
                type: pull-request
                title: t
                body: body.md
                base: '{input.target}'
            """);
        var detached = new FlowCheckout(null, "abc", false, [], [], [], null, null);

        Assert.Equal(
            "run 'r1' of 'flow' cannot start: its pull request's base cannot be filled: '{input.target}' reads nothing. Nothing was made.",
            FlowService.StartRefusal(detached, definition, new Dictionary<string, string>(), "r1"));
        Assert.Null(FlowService.StartRefusal(detached with { Branch = "work" }, definition, new Dictionary<string, string> { ["target"] = "main" }, "r1"));
    }

    /// <summary>
    /// A run is refused, having made nothing, where the checkout is no place a run starts from: HEAD detached, the branch
    /// its pull request goes to checked out - here the remote's default - and changes staged.
    /// </summary>
    [Fact]
    public async Task ARunIsRefused_WhereTheCheckoutIsNoPlaceARunStartsFrom()
    {
        var (repository, service, _) = await ServiceAsync();
        using var disposed = repository;
        using var remote = new TempDirectory();
        var git = repository.Harness;
        var branch = (await git.RunGitAsync(repository.Path, ["symbolic-ref", "--short", "HEAD"], Token)).StandardOutput.Trim();

        async Task<CommandOutcome> StartAsync()
            => await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        void Refused(CommandOutcome outcome, string why)
        {
            Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
            Assert.Equal($"run 'r1' of 'flow' cannot start: {why}. Nothing was made.", outcome.Message);
            Assert.False(Directory.Exists(RunOf(repository).Directory));
        }

        await git.RunGitAsync(remote.Path, ["init", "--quiet", "--bare", "."], Token);
        await git.RunGitAsync(repository.Path, ["remote", "add", "origin", remote.Path], Token);
        await git.RunGitAsync(repository.Path, ["push", "--quiet", "origin", branch], Token);
        await git.RunGitAsync(repository.Path, ["remote", "set-head", "origin", branch], Token);
        Refused(await StartAsync(), $"the main checkout is on '{branch}', the branch the run's pull request is opened against, and a run commits its work on a branch of its own: create one for it and check it out");

        await git.RunGitAsync(repository.Path, ["checkout", "--quiet", "--detach"], Token);
        Refused(await StartAsync(), "the main checkout's HEAD is detached, and a run commits its work on a branch: check out a branch of its own for it");

        await git.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "feature"], Token);
        repository.Tree.WriteFile("staged.txt", "staged\n");
        await git.RunGitAsync(repository.Path, ["add", "staged.txt"], Token);
        Refused(await StartAsync(), "the main checkout has staged changes (1 path(s): staged.txt), and a run's commit stages what it folded alone: commit them, or unstage them");
    }

    /// <summary>A session's --json is the run's document: its code and line, the run, its status, what it spent and every record it wrote by its path - never the empty stopped document.</summary>
    [Fact]
    public async Task ASessionsJson_IsTheRunsDocument_NamingEveryRecord()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        Script(runner);

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
            ["run", "state", "status", "questions", "updates", "issues", "definition", "events", "report", "reportJson", "folds", "log"],
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

        Script(runner);
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

        var warning = $"{command}: WARN - '{record}' is the record of a step's child's session or process group, and does not read as one: "
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

    /// <summary>
    /// What an input takes has one owner: the values a run starts with are each input given or its default, an input
    /// with neither left out, and a required input nothing gave, a value outside an input's options and a required param
    /// with no default are each refused.
    /// </summary>
    [Fact]
    public void TheValuesARunStartsWith_AreEachInputGivenOrItsDefault()
    {
        var definition = WorkflowFileParser.Parse("workflows/flow/flow.yml", """
            name: flow
            inputs:
              - name: request
                required: true
              - name: mode
                options: [fast, slow]
                default: slow
              - name: notes
            steps:
              - name: only
                type: orient
            """);

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["request"] = "do it", ["mode"] = "slow" },
            FlowInputs.Resolve(definition, new Dictionary<string, string>(StringComparer.Ordinal) { ["request"] = "do it" }));
        Assert.Equal("input 'request' is required, and nothing gave the run a value for it", Assert.Throws<HarnessException>(() => FlowInputs.Resolve(definition, null)).Message);
        Assert.Equal(
            "input 'mode' takes one of fast, slow, and the run was given 'quick'",
            Assert.Throws<HarnessException>(() => FlowInputs.Resolve(definition, new Dictionary<string, string>(StringComparer.Ordinal) { ["request"] = "do it", ["mode"] = "quick" })).Message);
    }

    /// <summary>A start whose inputs its workflow does not take is refused (13) before anything of the run is made.</summary>
    [Fact]
    public async Task AStartWhoseInputsItsWorkflowDoesNotTake_IsRefused_BeforeAnythingIsMade()
    {
        var (repository, service, _) = await ServiceAsync("""
            name: flow
            inputs:
              - name: mode
                options: [fast, slow]
            steps:
              - name: only
                type: orient
            """);
        using var disposed = repository;

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: ["mode=quick"], json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal("input 'mode' takes one of fast, slow, and the run was given 'quick'", outcome.Message);
        Assert.False(Directory.Exists(RunOf(repository).Directory));
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
    [InlineData(FlowRunStatus.Failed, 13, null, HarnessExit.Incomplete, "LockHeld")]
    [InlineData(FlowRunStatus.Paused, null, null, FlowExit.Paused)]
    [InlineData(FlowRunStatus.Paused, null, "budget", FlowExit.Paused)]
    [InlineData(FlowRunStatus.Paused, null, "interrupt", HarnessExit.Cancelled)]
    [InlineData(FlowRunStatus.Paused, null, "command", HarnessExit.Cancelled)]
    [InlineData(FlowRunStatus.Stopped, null, null, HarnessExit.Cancelled)]
    public void ASessionsEndExitsWithItsCode(FlowRunStatus status, int? exit, string? pause, int expected, string? cause = null)
    {
        var failed = exit is null ? Array.Empty<string>() : ["one"];
        var end = new FlowSessionEnd(status, [], failed, [], "ended", pause is null ? null : new FlowPauseState(pause, false, DateTimeOffset.UnixEpoch));
        var flow = new FlowFile(1, "r1", "flow", "digest", new Dictionary<string, string>(), status.ToString(), new FlowBudgetRecord(0, 0, 0), 0, [new FlowStepRecord("one", "one", false, "Failed", 1, 1, [], [], "build", Exit: exit, Cause: cause)], [], DateTimeOffset.UnixEpoch);

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

        Script(runner);
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

    /// <summary>
    /// An answer given where no engine runs the run is applied though the inbox beside it cannot be read: the answer,
    /// once recorded, stands - the run is driven on, and what could not be read is said and left for the next session -
    /// never a refusal over an answer already on disk.
    /// </summary>
    [Fact]
    public async Task AnAnswerWithNoEngine_OverAnInboxThatCannotBeRead_IsAppliedAndTheRunDrivenOn()
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

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        repository.Harness.FileSystem.WriteAllTextAtomic(run.InboxFile, "not an inbox");

        var outcome = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("done", outcome.Message);
        Assert.Equal(["after"], runner.Started);
        Assert.Equal("answered", Assert.Single(repository.Harness.OrchestrationStore.ReadFlowQuestions(run)!.Questions).State);
        Assert.StartsWith("what was handed to the engine as its session began could not be read: ", outcome.Details![0], StringComparison.Ordinal);
        Assert.EndsWith("; it stays for the next session of the run", outcome.Details![0], StringComparison.Ordinal);
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

        Script(runner);
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

        Script(runner);
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

        Script(runner);
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

        Script(runner);
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

        Script(runner);
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

        Script(runner);
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

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var elsewhere = new RunOwner("another-machine", Environment.ProcessId, DateTimeOffset.UtcNow, "its start");

        repository.Harness.OrchestrationStore.WriteOwner(run, elsewhere);

        var outcome = await service.StopAsync(repository.Path, "r1", now: true, json: false, Token);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal(
            $"the engine of run 'r1' runs on another machine (process {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)} on another machine); --now ends only a process of this machine. "
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

        Script(runner);
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
    /// <c>--now</c> ends what the engine's steps left running once it has ended the engine where it stood - the engine's
    /// whole tree, its leg's phase among it: here what the phase handed on, no one's child and still in the phase's group -
    /// refusing a hangup, as a build's leftover that outlives its engine does: a tree is ended by stopping each process
    /// before killing it, and the system hangs up a group orphaned while a member of it is stopped - ended by the sweep
    /// that follows though the phase that led the group is gone, and a contained child's session the
    /// engine recorded; said in the answer by kind, their records removed. An engine found gone already - a crash, which
    /// ends nothing it started - is nothing to stop, and the sweep every verb over the run starts with has ended what it
    /// left all the same, its phase still leading its group, said in its lines.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopNow_EndsTheProcessGroupsTheEnginesStepsLeftRunning(bool goneAlready)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a leg's phase there is bound by the engine's job, which ends it with the engine, and nothing is recorded.");

        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var identity = repository.Harness.Identity;
        var step = run.StepDirectory("one");
        var said = new TaskCompletionSource<(LearnedProcess Engine, LearnedProcess HandedOn)>(TaskCreationOptions.RunContinuationsAsynchronously);
        (LearnedProcess Engine, LearnedProcess HandedOn) ids = default;
        using var contained = Process.Start(TestHost.StartOf(TestHost.ChildRequest("own-session-and-sleep", "300000")))!;
        using var signals = new TempDirectory();
        var ready = signals.Combine("ready");

        // The engine starts its leg's phase as a machine step does - bound, under a process scope that records its group
        // in the step's directory - and the phase hands on a process of its own, no one's child, still in its group.
        var engineRun = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram).RunAsync(
            TestHost.ChildRequest("scoped-recorded-and-wait", CliRunner.CliProgram, step, "spawn-orphan-and-sleep", "300000", "300000", "ignore-hup-and-sleep", ready) with
            {
                Timeout = Bound,
                OnOutputLine = line =>
                {
                    var words = line.Split(' ').Select(word => word.Split('=')).ToDictionary(pair => pair[0], pair => int.Parse(pair[1], CultureInfo.InvariantCulture));
                    said.TrySetResult((LearnedProcess.Of(words["owner"]), LearnedProcess.Of(words["orphan"])));
                },
            },
            Token);

        try
        {
            ids = await said.Task.WaitAsync(Bound, Token);
            Assert.True(SpinWait.SpinUntil(() => File.Exists(ready), Bound), $"what the phase handed on, {ids.HandedOn}, never came to refuse a hangup");

            var groupRecord = Assert.Single(Directory.GetFiles(OrchestratorLayout.ChildrenDirectory(step)));
            var phase = LearnedProcess.Of(JsonNode.Parse(File.ReadAllText(groupRecord))!["session"]!.GetValue<int>());
            var session = OrchestratorLayout.ChildFile(step, contained.Id);

            Assert.True(SpinWait.SpinUntil(() => TestChild.SessionOf(contained.Id) == contained.Id, Bound), $"the child {contained.Id} never led a session of its own");

            File.WriteAllText(session, new JsonObject
            {
                ["session"] = contained.Id,
                ["started"] = identity.StampOf(contained.Id),
                ["program"] = "dotnet",
                ["owner"] = new JsonObject { ["machine"] = identity.CurrentMachine, ["processId"] = ids.Engine.Id, ["stamp"] = ids.Engine.Stamp },
            }.ToJsonString() + "\n");

            if (goneAlready)
            {
                // Killed alone, as a crash ends it: its phase still leads its group.
                using (var killed = Process.GetProcessById(ids.Engine.Id))
                {
                    killed.Kill(entireProcessTree: false);
                }

                await Record.ExceptionAsync(() => engineRun.WaitAsync(Bound, Token));
            }

            repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(identity.CurrentMachine, ids.Engine.Id, DateTimeOffset.UtcNow, ids.Engine.Stamp));

            var outcome = await service.StopAsync(repository.Path, "r1", now: true, json: false, Token).WaitAsync(Bound, Token);

            Assert.True(ids.HandedOn.AwaitGone(), $"what the phase handed on, {ids.HandedOn}, outlived the stop of the engine that left it");
            Assert.True(phase.AwaitGone(), $"the phase {phase} outlived the stop of the engine that left it");
            Assert.True(contained.WaitForExit(Bound), $"the contained child {contained.Id} outlived the stop of the engine that left it");
            Assert.False(File.Exists(groupRecord), $"the record of the ended group is still at '{groupRecord}'");
            Assert.False(File.Exists(session), $"the record of the ended session is still at '{session}'");

            var groupLine = $"{FlowService.StopCommand}: step 'one' of 'r1' left process group {phase.Id} ('{TestHost.AssemblyProgram}') running, "
                + $"and the process that started it (pid {ids.Engine.Id}) has gone; it was ended";
            Assert.Contains(groupLine, repository.Harness.StandardOutput.ToString().Split('\n').Select(line => line.TrimEnd('\r')));

            if (goneAlready)
            {
                Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
                Assert.Equal("run 'r1' is not being run by an engine: there is nothing to stop", outcome.Message);
                return;
            }

            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.Equal($"ended the engine of run 'r1' (process {ids.Engine.Id}) where it stood", outcome.Message);
            Assert.Equal(
                ["what its steps left running was ended with it: 1 session(s), 1 process group(s)", $"what it left is what a crash leaves; {FlowService.ResumeCommand} goes on from the records"],
                outcome.Details!.Take(2));
        }
        finally
        {
            ids.HandedOn.TryEnding();
            ids.Engine.TryEnding();
            Ending(contained);
            await Record.ExceptionAsync(() => engineRun.WaitAsync(Bound, Token));
        }
    }

    /// <summary>
    /// <c>--now</c> over an engine that ended in the moment it was stopped - standing when the stop read its owner, gone
    /// when it came to end it - removes its owner, and ends what its steps left running all the same, said in the answer.
    /// </summary>
    [Fact]
    public async Task StopNow_OverAnEngineThatEndedAsItWasStopped_EndsWhatItsStepsLeftAllTheSame()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a leg's phase there is bound by the engine's job, which ends it with the engine, and nothing is recorded.");

        var (repository, finished, runner) = await ServiceAsync();
        using var disposed = repository;

        Script(runner);
        _ = await finished.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var engines = new Engines(repository.Harness.Identity) { GoesWhenEnded = true };
        var harness = repository.Harness;
        var service = Service(
            harness,
            runner,
            identity: engines,
            children: new ChildSessions(harness.FileSystem, engines, new LeftSessions(harness.Platform, harness.ProcessTable), harness.Output, new EndedChildSessions()));
        var step = run.StepDirectory("one");
        using var phase = Process.Start(TestHost.StartOf(TestHost.ChildRequest("own-group-and-sleep", "300000")))!;

        try
        {
            Assert.True(SpinWait.SpinUntil(() => TestChild.GroupOf(phase.Id) == phase.Id, Bound), $"the phase {phase.Id} never led a group of its own");

            var record = OrchestratorLayout.ChildFile(step, phase.Id);

            Directory.CreateDirectory(OrchestratorLayout.ChildrenDirectory(step));
            File.WriteAllText(record, new JsonObject
            {
                ["session"] = phase.Id,
                ["group"] = true,
                ["started"] = engines.StampOf(phase.Id),
                ["program"] = "dotnet",
                ["owner"] = new JsonObject { ["machine"] = engines.CurrentMachine, ["processId"] = Engines.Engine, ["stamp"] = "its start" },
            }.ToJsonString() + "\n");
            repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(engines.CurrentMachine, Engines.Engine, DateTimeOffset.UtcNow, "its start"));

            var outcome = await service.StopAsync(repository.Path, "r1", now: true, json: false, Token).WaitAsync(Bound, Token);

            Assert.Equal(HarnessExit.Success, outcome.ExitCode);
            Assert.Equal($"the engine of run 'r1' (process {Engines.Engine}) had already ended; its owner was removed", outcome.Message);
            Assert.Equal(
                ["what its steps left running was ended with it: 1 process group(s)", $"{FlowService.ResumeCommand} goes on from the records"],
                outcome.Details!.Take(2));
            Assert.True(phase.WaitForExit(Bound), $"the phase {phase.Id} outlived the stop of the engine that left it");
            Assert.False(File.Exists(record), $"the record of the ended group is still at '{record}'");
            Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        }
        finally
        {
            Ending(phase);
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

        Script(runner);
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

    /// <summary>
    /// The terminal's bound on a hard stop is the engine's own drain and a margin for writing the end: a machine step whose
    /// runner does not hear the stop is recorded stopped once the engine's 90 s pass, on the one clock the terminal and
    /// the engine share, and the session ends in order - its report written, its owner released - never cut off by the
    /// terminal at the same moment.
    /// </summary>
    [Fact]
    public async Task TheSecondInterrupt_OverARunnerDeafToTheStop_EndsInOrderOnceTheEnginesDrainPasses()
    {
        var (repository, runner) = await RepositoryAsync(InterruptedFlow);
        using var disposed = repository;
        var clock = new TimerClock();
        var service = Service(repository.Harness, runner, clock);
        var deaf = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.HoldsDeaf("two", deaf.Task);

        var terminal = new FlowTerminal(repository.Harness.Output, FlowService.RunCommand, clock);
        var driving = terminal.RunAsync(
            (interrupts, token) => service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, interrupts, token),
            Token,
            bindSignals: false);

        await runner.WaitStartedAsync("two");

        // The poll's wait, then the terminal's bound and the engine's drain: three waits on the one clock.
        await clock.WaitingAsync(1).WaitAsync(Bound, Token);
        terminal.Interrupted();
        terminal.Interrupted();
        await clock.WaitingAsync(3).WaitAsync(Bound, Token);
        clock.Advance(PointOfNoReturn.SafePauseDrain);

        var outcome = await driving.WaitAsync(Bound, Token);
        var run = RunOf(repository);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;
        var two = flow.Steps.Single(step => step.Instance == "two");

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("Stopped", flow.Status);
        Assert.Equal("Stopped", two.State);
        Assert.Equal("its runner did not end within 90 s of the stop", two.Error);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
        _ = deaf.TrySetResult();
    }

    /// <summary>
    /// A session the terminal cuts off answers as every command over a run does: the line names the run and the command
    /// that goes on with it, the text and the --json document name every record the run holds, and the run's log has
    /// the command's line.
    /// </summary>
    [Fact]
    public async Task ASessionCutOffByTheTerminal_NamesTheRunAndItsRecords_InTextJsonAndItsLog()
    {
        var (repository, runner) = await RepositoryAsync(InterruptedFlow);
        using var disposed = repository;
        var clock = new TimerClock();
        var service = Service(repository.Harness, runner, clock);
        var deaf = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CommandOutcome>? session = null;

        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.HoldsDeaf("two", deaf.Task);

        var terminal = new FlowTerminal(repository.Harness.Output, FlowService.RunCommand, clock, TimeSpan.FromSeconds(10));
        var driving = terminal.RunAsync(
            (interrupts, token) => session = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: true, interrupts, token),
            Token,
            bindSignals: false);

        await runner.WaitStartedAsync("two");
        await clock.WaitingAsync(1).WaitAsync(Bound, Token);
        terminal.Interrupted();
        terminal.Interrupted();
        await clock.WaitingAsync(3).WaitAsync(Bound, Token);
        clock.Advance(TimeSpan.FromSeconds(10));

        var outcome = await driving.WaitAsync(Bound, Token);
        var run = RunOf(repository);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal(
            "run 'r1' did not stop within 10 s of the second interrupt, and the command ends where it stands: what is left is what a crash leaves, and resume-orchestrator r1 reclaims the run and goes on from its records",
            outcome.Message);
        Assert.Equal(
            [
                $"run: {run.Directory}",
                $"state: {run.FlowFile}",
                $"status: {run.WorkflowStatusFile}",
                $"questions: {run.QuestionsFile}",
                $"updates: {run.UpdatesFile}",
                $"issues: {run.PreExistingIssuesFile}",
                $"definition: {run.DefinitionFile}",
                $"events: {run.EventsFile}",
                $"report: {run.ReportFile}",
                $"reportJson: {run.ReportJsonFile}",
                $"folds: {run.FoldsFile}",
                $"log {run.LogFile("r1")}",
            ],
            outcome.Details);

        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

        Assert.True(outcome.Quiet);
        Assert.Equal(130, document["exitCode"]!.GetValue<int>());
        Assert.Equal("r1", document["run"]!.GetValue<string>());
        Assert.Equal("Running", document["status"]!.GetValue<string>());
        Assert.Equal(
            ["run", "state", "status", "questions", "updates", "issues", "definition", "events", "report", "reportJson", "folds", "log"],
            document["records"]!.AsObject().Select(member => member.Key));

        var logged = repository.Harness.OrchestrationLog.Read(run.LogFile("r1"))[^1];

        Assert.Equal(FlowService.RunCommand, logged.Command);
        Assert.Equal(HarnessExit.Cancelled, logged.ExitCode);

        // The session cut off goes on behind the command, as it would in a process that ended: let it end before the
        // repository goes.
        _ = deaf.TrySetResult();
        _ = await session!.WaitAsync(Bound, Token);
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
            "the run did not stop within 120 s of the second interrupt, and the command ends where it stands: what is left is what a crash leaves, and resume-orchestrator reclaims the run and goes on from its records",
            outcome.Message);
    }

    /// <summary>
    /// The interrupts are counted once, for the whole session: a drive of it that ends - an answer taken as it ended drives
    /// it again - keeps the count, so the next interrupt is the second, the hard stop, applied to the session's engine;
    /// only the session's end clears it.
    /// </summary>
    [Fact]
    public async Task TheCount_IsTheSessions_KeptBetweenItsDrives_ClearedAtItsEnd()
    {
        var definition = WorkflowFileParser.Parse("flow.yml", """
            name: flow
            steps:
              - name: one
                type: orient
            """);
        var runner = new SteppedRunner();
        var coordinator = new FlowCoordinator(definition, runner, new WritesNothing(), new ManualClock());
        var interrupts = new FlowInterrupts();

        interrupts.BoundTo(coordinator);
        Assert.Equal(1, interrupts.Pressed());
        interrupts.Unbound();

        Assert.Equal(1, interrupts.Presses);

        interrupts.BoundTo(coordinator);
        Assert.Equal(2, interrupts.Pressed());
        interrupts.Unbound();

        // The second was the hard stop: the engine applies it, and nothing of the run starts.
        var end = await coordinator.RunAsync(Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowRunStatus.Stopped, end.Status);
        Assert.Empty(runner.Started);

        interrupts.SessionEnded();
        Assert.Equal(0, interrupts.Presses);
    }

    /// <summary>
    /// The terminal bounds the hard stop by the session's one count: an interrupt a front end pressed through the
    /// interrupts, then one the terminal took, is the second - the drain begun and the command cut off once it passes.
    /// </summary>
    [Fact]
    public async Task TheTerminalsDrain_StartsAtTheSessionsSecondInterrupt_WhoeverPressedTheFirst()
    {
        var harness = new HarnessFactory();
        var terminal = new FlowTerminal(harness.Output, FlowService.RunCommand, new JumpingClock());
        var never = new TaskCompletionSource<CommandOutcome>();

        var running = terminal.RunAsync((_, _) => never.Task, Token, bindSignals: false);

        Assert.Equal(1, terminal.Interrupts.Pressed());
        terminal.Interrupted();

        var outcome = await running.WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
    }

    /// <summary>
    /// A hang-up is the run's interrupt though nothing can be written to the terminal any more: the terminal says nothing
    /// once it has hung up, and a line the poller says while the session runs is dropped where the terminal cannot take
    /// it, never an exception that ends the process part way - the run pauses safely, its report written and its owner
    /// released.
    /// </summary>
    [Fact]
    public async Task AHangUp_PausesTheRunSafely_ThoughTheTerminalCanNoLongerBeWrittenTo()
    {
        var (repository, runner) = await RepositoryAsync(InterruptedFlow);
        using var disposed = repository;
        var gone = new Warned(repository.Harness.Output, _ => throw new IOException("Input/output error"));
        var service = Service(repository.Harness, runner, output: gone);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RunOf(repository);

        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", release.Task);

        var terminal = new FlowTerminal(gone, FlowService.RunCommand);
        var driving = terminal.RunAsync(
            (interrupts, token) => service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, interrupts, token),
            Token,
            bindSignals: false);

        await runner.WaitStartedAsync("two");
        terminal.HungUp();

        // A line the poller says to the terminal gone: what it cannot read.
        repository.Harness.FileSystem.WriteAllTextAtomic(run.InboxFile, "not an inbox");
        await gone.First.WaitAsync(Bound, Token);
        File.Delete(run.InboxFile);
        _ = release.TrySetResult();

        var outcome = await driving.WaitAsync(Bound, Token);
        var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Equal("Paused", flow.Status);
        Assert.Equal("interrupt", flow.Pause!.Cause);
        Assert.Null(repository.Harness.OrchestrationStore.ReadOwner(run));
        Assert.True(File.Exists(run.ReportFile));
        Assert.StartsWith("what was asked of the engine of run 'r1' could not be read: ", Assert.Single(gone.Warnings), StringComparison.Ordinal);
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
    /// Every step of a run is handed the branch the run started on, as its records keep it - by the session that started
    /// it and by every one that resumes it, whatever the main checkout is on by then - which its commits, pushes and pull
    /// request are held to.
    /// </summary>
    [Fact]
    public async Task EveryStep_IsHandedTheBranchTheRunStartedOn_ThroughEverySession()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;
        await repository.Harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "feature"], Token);

        runner.On("one", FlowStepResult.Ended(passed: true, 0));
        runner.Holds("two", new TaskCompletionSource().Task);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);

        await runner.WaitStartedAsync("two");

        _ = await service.StopAsync(repository.Path, "r1", now: false, json: false, Token).WaitAsync(Bound, Token);
        _ = await driving.WaitAsync(Bound, Token);
        await repository.Harness.RunGitAsync(repository.Path, ["checkout", "--quiet", "-b", "elsewhere"], Token);
        runner.On("two", FlowStepResult.Ended(passed: true, 0));

        var resumed = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, resumed.ExitCode);
        Assert.Equal([("one", "feature"), ("two", "feature"), ("two", "feature")], runner.Asked.Select(run => (run.Instance, run.Place!.Branch)));
    }

    /// <summary>
    /// <c>--again</c> finds the set it would change - the last a person answered, which the run's file keeps with
    /// every answer it was given through every later session - and is refused where the definition the run began with
    /// takes no changed answer, never for a question the file lost or for being answered.
    /// </summary>
    [Fact]
    public async Task AgainFindsTheAnsweredQuestion_ThroughLaterSessions_AndIsRefusedWhereNoChangeIsTaken()
    {
        var (repository, service, runner) = await ServiceAsync("""
            name: flow
            steps:
              - name: approval
                type: pause
                question: go on?
                options: [stay, go]
              - name: middle
                type: orient
                dependsOn: [approval]
              - name: second
                type: pause
                question: and now?
                options: [stay, go]
                dependsOn: [middle]
            """);
        using var disposed = repository;

        Assert.Equal(FlowExit.Paused, (await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token)).ExitCode);

        runner.Holds("middle", new TaskCompletionSource().Task);
        var answering = service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token);
        await runner.WaitStartedAsync("middle");
        _ = await service.StopAsync(repository.Path, "r1", now: false, json: false, Token).WaitAsync(Bound, Token);
        _ = await answering.WaitAsync(Bound, Token);

        var resumed = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowExit.Paused, resumed.ExitCode);

        var again = await service.AnswerAsync(repository.Path, "r1", "stay", question: "q1", text: null, textFile: null, again: true, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Refused, again.ExitCode);
        Assert.Equal("orchestrator 'flow' takes no changed answer: its amend names no changedAnswerMinutes", again.Message);
        Assert.Equal(["answered", "open"], repository.Harness.OrchestrationStore.ReadFlowQuestions(RunOf(repository))!.Questions.Select(question => question.State));
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

    /// <summary>
    /// The definition tree is the main checkout, wherever a command is typed: a run begun there is resumed and answered
    /// from another tree of the repository - one holding the same workflow file at a path of its own, then an edited copy
    /// - under the entry it began with, never refused as changed, and never held to the other tree's copy.
    /// </summary>
    [Fact]
    public async Task ARunIsResumedAndAnsweredFromAnotherTree_HeldToTheMainCheckoutsEntry()
    {
        const string Flow = """
            name: flow
            steps:
              - name: holding
                type: pause
                question: what now?
                options: [stay, go]
              - name: after
                type: orient
                dependsOn: [holding]
            """;

        var (repository, service, runner) = await ServiceAsync(Flow);
        using var disposed = repository;
        using var elsewhere = new TempDirectory();

        Script(runner);

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var tree = Path.Combine(elsewhere.Path, "tree");
        await repository.Harness.RunGitAsync(repository.Path, ["worktree", "add", "--quiet", "--detach", tree], Token);
        var copy = Path.Combine(tree, ".harness-config", "workflows", Workflow, $"{Workflow}.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.WriteAllText(copy, Flow);

        // The run is taken up, and answers for its open question - never refused as changed.
        var resumed = await service.ResumeAsync(tree, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal("nothing of this run can run until the question of step 'holding' is answered; answer-orchestrator answers it", resumed.Message);
        Assert.Equal(HarnessExit.Refused, resumed.ExitCode);

        // The other tree's own copy, edited, is no part of the run: the main checkout's entry is what it is held to.
        File.WriteAllText(copy, Flow + "  - name: added\n    type: orient\n");

        var answered = await service.AnswerAsync(tree, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, answered.ExitCode);
        Assert.Equal("done", answered.Message);
        Assert.Equal(["after"], runner.Started);

        // A run started from the other tree is of the main checkout's entry too, never of the tree's edited copy.
        _ = await service.RunAsync(tree, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(["holding", "after"], repository.Harness.OrchestrationStore.ReadDefinition(RunOf(repository, "r2"))!.Steps.Select(step => step.Name));
        Assert.Equal(["after"], runner.Started);
    }

    /// <summary>An answer handed to a running engine is taken by its poller, applied by the loop as a person's through the command - recorded under the id of the inbox entry that handed it over - and emptied from the inbox.</summary>
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
        Assert.Equal(new FlowAnswerBy("person", "answer-orchestrator"), answer.By! with { Inbox = null });
        Assert.Matches("^[0-9a-f]{32}$", answer.By.Inbox);
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
    /// An answer a resume takes from the inbox between sessions that the run does not take is recorded as an answer event,
    /// as the loop records one it refuses, beside the line that says it - and emptied from the inbox.
    /// </summary>
    [Fact]
    public async Task AnInboxAnswerRefusedBetweenSessions_IsRecordedAsAnEvent()
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
        repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q9", "go", DateTimeOffset.UtcNow));
        repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q1", "go", DateTimeOffset.UtcNow));

        var resumed = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);

        Assert.Equal(HarnessExit.Success, resumed.ExitCode);
        Assert.Contains("the answer 'go' handed over for question 'q9' was not taken: 'q9' is not a question this run leaves open", resumed.Details ?? []);
        Assert.Equal(["'q9' is not a question this run leaves open"], events.Where(read => read.Kind == "answer").Select(read => read.Detail));
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

    /// <summary>
    /// An answer the loop took stays the poller's until the inbox lets it go: a removal that fails is tried again at the
    /// next poll, and the answer is never offered to the engine again and recorded as one it refused.
    /// </summary>
    [Fact]
    public async Task AnAnswerTakenWhoseRemovalFails_IsRemovedAtTheNextPoll_NeverOfferedAgain()
    {
        var (repository, runner) = await RepositoryAsync(Waiting);
        using var disposed = repository;

        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, fileSystem: files);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = RunOf(repository);

        runner.Holds("slow", release.Task);
        files.FailsDeleteOnce(run.InboxFile);

        var driving = service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token);

        await runner.WaitStartedAsync("slow");

        var handed = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token);

        Assert.Equal(HarnessExit.Success, handed.ExitCode);

        await runner.WaitStartedAsync("after");
        await files.Deleted(run.InboxFile).WaitAsync(Bound, Token);
        _ = release.TrySetResult();

        var outcome = await driving.WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.DoesNotContain(events, read => read.Kind == "answer");
        Assert.False(File.Exists(run.InboxFile));
    }

    /// <summary>
    /// An answer a session's last poll takes stays taken though its removal from the inbox fails: it settles the question
    /// the session paused on, so the run is driven again - whose poller empties it from the inbox - and it is never
    /// offered again nor said as not taken.
    /// </summary>
    [Fact]
    public async Task AnAnswerTheLastPollTakesWhoseRemovalFails_StillDrivesTheRunOn_AndIsRemovedAfter()
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
        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, timings: new FlowTimings(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30)), fileSystem: files);
        var run = RunOf(repository);

        files.FailsDeleteOnce(run.InboxFile);
        runner.OnStart("first", () => repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q1", "go", DateTimeOffset.UtcNow)));

        var outcome = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal(["first", "after"], runner.Started);
        Assert.Equal(
            ["the answer 'go' handed over for question 'q1' was taken from the inbox"],
            (outcome.Details ?? []).Where(line => line.StartsWith("the answer ", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, read => read.Kind == "answer");
        Assert.False(File.Exists(run.InboxFile));
    }

    /// <summary>
    /// An answer a session takes from the inbox as it begins stays taken though its removal fails: the poller empties it
    /// from the inbox at a later poll, and it is never offered to the engine again and recorded as one it refused.
    /// </summary>
    [Fact]
    public async Task AnAnswerTakenAsASessionBeganWhoseRemovalFails_IsRemovedLater_NeverOfferedAgain()
    {
        var (repository, runner) = await RepositoryAsync("""
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

        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, fileSystem: files);

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q1", "go", DateTimeOffset.UtcNow));
        files.FailsDeleteOnce(run.InboxFile);

        var resumed = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);

        Assert.Equal(HarnessExit.Success, resumed.ExitCode);
        Assert.Equal(["after"], runner.Started);
        Assert.Equal(
            ["the answer 'go' handed over for question 'q1' was taken from the inbox"],
            (resumed.Details ?? []).Where(line => line.StartsWith("the answer ", StringComparison.Ordinal) || line.StartsWith("what was ", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, read => read.Kind == "answer");
        Assert.False(File.Exists(run.InboxFile));
    }

    /// <summary>
    /// An answer a session took whose removal from the inbox fails to the session's very end is never weighed again by the
    /// next session: the run's records hold it under its entry's id, so the next session only empties it from the inbox -
    /// no answer event, no line saying it was not taken.
    /// </summary>
    [Fact]
    public async Task AnAnswerWhoseRemovalFailsToTheSessionsEnd_IsOnlyEmptiedByTheNextSession_NeverRefused()
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
              - name: closing
                type: pause
                question: is it done?
                options: [yes, no]
                dependsOn: [after]
            """);
        using var disposed = repository;

        // The poller reads once, at each drive's start; the inbox lets nothing go until the next session.
        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, timings: new FlowTimings(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30)), fileSystem: files);
        var run = RunOf(repository);

        files.FailsEveryDelete(run.InboxFile);
        runner.OnStart("first", () => repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q1", "go", DateTimeOffset.UtcNow)));

        var first = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(FlowExit.Paused, first.ExitCode);
        Assert.True(File.Exists(run.InboxFile));

        files.Deletes(run.InboxFile);

        var next = await service.AnswerAsync(repository.Path, "r1", "yes", question: "q2", text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);

        Assert.Equal(HarnessExit.Success, next.ExitCode);
        Assert.Equal(["first", "after"], runner.Started);
        Assert.Equal(
            ["the answer 'go' handed over for question 'q1' was recorded by the run already, so it is only emptied from the inbox"],
            (next.Details ?? []).Where(line => line.StartsWith("the answer ", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, read => read.Kind == "answer");
        Assert.False(File.Exists(run.InboxFile));
        Assert.Equal(
            [$"1 answer(s) this session settled could not be emptied from '{run.InboxFile}': The process cannot access the file '{run.InboxFile}' because it is being used by another process; it stays there, and the next session of the run only empties it"],
            (first.Details ?? []).Where(line => line.Contains(" could not be emptied ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An answer taken from the inbox by a session that ended before the inbox let it go - a crash between the take and
    /// the removal - is never weighed again by the next session: the run's records hold it under its entry's id, so the
    /// next session only empties it from the inbox, and goes on with the run as the answer left it.
    /// </summary>
    [Fact]
    public async Task AnAnswerACrashLeftInTheInboxAfterItsTake_IsOnlyEmptiedByTheNextSession_NeverRefused()
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

        // The poller reads once, at the session's start: the last poll takes the answer, and the process ends where the
        // inbox would let it go.
        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, timings: new FlowTimings(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30)), fileSystem: files);
        var run = RunOf(repository);

        files.EndsAtDeleteOnce(run.InboxFile);
        runner.OnStart("first", () => repository.Harness.OrchestrationStore.AddToInbox(run, new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q1", "go", DateTimeOffset.UtcNow)));

        _ = await Assert.ThrowsAsync<Crashed>(() => service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token)).WaitAsync(Bound, Token);

        Assert.True(File.Exists(run.InboxFile));
        Assert.Equal("go", Assert.Single(repository.Harness.OrchestrationStore.ReadFlowQuestions(run)!.Of("q1")!.Answers).Answer);

        var resumed = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);

        Assert.Equal(HarnessExit.Success, resumed.ExitCode);
        Assert.Equal(["first", "after"], runner.Started);
        Assert.Equal(
            ["the answer 'go' handed over for question 'q1' was recorded by the run already, so it is only emptied from the inbox"],
            (resumed.Details ?? []).Where(line => line.StartsWith("the answer ", StringComparison.Ordinal)));
        Assert.DoesNotContain(events, read => read.Kind == "answer");
        Assert.False(File.Exists(run.InboxFile));
    }

    /// <summary>
    /// An answer handed over that changes one already given is refused by the engine as any answer it does not take is:
    /// said, its refusal written under its inbox entry's id and told as an answer event, and emptied from the inbox.
    /// </summary>
    [Fact]
    public async Task AChangedAnswerHandedOver_IsRefusedAndRecordedUnderItsEntry_AsAnyAnswerTheRunDoesNotTake()
    {
        var (repository, runner) = await RepositoryAsync("""
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

        var service = Service(repository.Harness, runner);

        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var changed = new FlowInboxAnswer(FlowInboxAnswer.NewId(), "q1", "stay", DateTimeOffset.UtcNow, Again: true);
        repository.Harness.OrchestrationStore.AddToInbox(run, changed);

        var answered = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);
        var refused = Assert.Single(repository.Harness.OrchestrationStore.ReadFlowQuestions(run)!.Refused!);
        const string Why = "orchestrator 'flow' takes no changed answer: its amend names no changedAnswerMinutes";

        Assert.Equal(HarnessExit.Success, answered.ExitCode);
        Assert.Equal(
            [$"the answer 'stay' handed over for question 'q1' was not taken: {Why}"],
            (answered.Details ?? []).Where(line => line.StartsWith("the answer ", StringComparison.Ordinal)));
        Assert.Equal((changed.Id, "q1", "stay", Why), (refused.Inbox, refused.Question, refused.Answer, refused.Why));
        Assert.Equal([Why], events.Where(read => read.Kind == "answer").Select(read => read.Detail));
        Assert.False(File.Exists(run.InboxFile));
    }

    /// <summary>
    /// An answer the poller offers as a resume begins is weighed at the resume's start, whose writes are held until it is
    /// known the session goes on - and leaves the inbox only once what became of it is on disk: where the resume is
    /// refused, which writes nothing, an answer weighed and refused at its start is never written, so it stays in the
    /// inbox - though the poller polled again after the loop weighed it - and the next session weighs it and records what
    /// became of it. Nothing handed over is lost between the two.
    /// </summary>
    [Fact]
    public async Task AnAnswerWeighedAtTheStartOfARefusedResume_StaysInTheInbox_AndTheNextSessionRecordsIt()
    {
        var (repository, runner) = await RepositoryAsync("""
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

        // The poller polls at once and then only where the clock makes it: at each read of the time the session makes
        // while the poller waits.
        var clock = new PolledAtEachRead(TimeSpan.FromHours(1));
        var files = new Signalling(repository.Harness.FileSystem);
        var service = Service(repository.Harness, runner, clock: clock, timings: new FlowTimings(TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30)), fileSystem: files);
        var run = RunOf(repository);
        var store = repository.Harness.OrchestrationStore;

        Assert.Equal(FlowExit.Paused, (await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token)).ExitCode);

        // Two answers the run does not take are handed over just after the resume looked at the inbox: the poller offers
        // both at its first poll, the loop weighs them at the resume's start - the time read for each refusal - and the
        // poller polls again at each.
        FlowInboxAnswer[] late =
        [
            new(FlowInboxAnswer.NewId(), "q8", "go", DateTimeOffset.UtcNow),
            new(FlowInboxAnswer.NewId(), "q9", "go", DateTimeOffset.UtcNow),
        ];

        files.AfterLookingAt(run.InboxFile, () =>
        {
            foreach (var answer in late)
            {
                store.AddToInbox(run, answer);
            }
        });

        clock.Polls = true;
        var refused = await service.ResumeAsync(repository.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        clock.Polls = false;

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal("nothing of this run can run until the question of step 'holding' is answered; answer-orchestrator answers it", refused.Message);
        Assert.Equal(late.Select(answer => answer.Id), store.ReadInbox(run)!.Answers.Select(answer => answer.Id));
        Assert.Null(store.ReadFlowQuestions(run)!.Refused);
        Assert.Equal(
            [
                $"2 answer(s) handed to this engine were not taken before its session ended; they stay in '{run.InboxFile}', and the next session of the run takes them",
                $"inbox: {run.InboxFile}",
            ],
            (refused.Details ?? []).Where(line => line.Contains("inbox", StringComparison.Ordinal)));

        var answered = await service.AnswerAsync(repository.Path, "r1", "go", question: null, text: null, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var (events, _) = FlowEventLog.Read(repository.Harness.FileSystem, run.EventsFile);

        Assert.Equal(HarnessExit.Success, answered.ExitCode);
        Assert.Equal(["after"], runner.Started);
        Assert.Equal(late.Select(answer => (answer.Id, answer.Question)), store.ReadFlowQuestions(run)!.Refused!.Select(entry => (entry.Inbox, entry.Question)));
        Assert.Equal(
            ["'q8' is not a question this run leaves open", "'q9' is not a question this run leaves open"],
            events.Where(read => read.Kind == "answer").Select(read => read.Detail));
        Assert.False(File.Exists(run.InboxFile));
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
        var sessions = new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient);
        var (run, context) = await sessions.CreateAsync(layout, definition, cancellationToken: Token);

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
            Assert.Equal(FlowHandedOver.Taken, await offered.WaitAsync(Bound, Token));
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

        Script(runner);
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

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);

        repository.Harness.OrchestrationStore.WriteOwner(run, new RunOwner(repository.Harness.Identity.CurrentMachine, Engines.Engine, DateTimeOffset.UtcNow, "its start"));

        var deleted = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.False(Directory.Exists(run.Directory));
        Assert.False(File.Exists(run.OwnerFile));
    }

    /// <summary>
    /// A session's claim is made in the repository's one section and holds the run to being there: a run deleted since the
    /// session looked at it is refused by name, and no owner is written beside a run that is gone.
    /// </summary>
    [Fact]
    public async Task AClaimOfARunDeletedSinceItsSessionLooked_IsRefused_WritingNoOwner()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var deleted = await repository.Harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: true, Token);
        var sessions = new FlowSessions(repository.Harness.OrchestrationStore, repository.Harness.FileSystem, repository.Harness.Identity, repository.Harness.GitClient);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);

        var refused = await Assert.ThrowsAsync<HarnessException>(() => sessions.ClaimAsync(new DssHarness.Core.Repository.HarnessLayout(repository.Path, repository.Path), run, Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(OrchestrationReports.NoOrchestrator("r1"), refused.Message);
        Assert.False(File.Exists(run.OwnerFile));
    }

    /// <summary>
    /// A deletion decides and removes inside the repository's one section, which a session's claim is made in: taken
    /// before the run's own, so no claim can come between the deletion's look at the owner and its removal of the run.
    /// </summary>
    [Fact]
    public async Task ADeletion_DecidesAndRemovesInsideTheRepositorysOneSection_SoNoClaimComesBetween()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var harness = repository.Harness;
        var section = OrchestrationStore.RunsSectionFile(new DssHarness.Core.Repository.HarnessLayout(repository.Path, repository.Path));
        var removing = new Removing(harness.FileSystem, run.RecordFile, section);
        var orchestrators = new OrchestratorService(harness.ContextLoader, harness.GitClient, removing, harness.Platform, harness.Output, harness.WorktreeService, harness.OrchestrationStore, harness.OrchestrationLog, harness.ChildSessions, harness.Identity, TimeProvider.System);

        var deleted = await orchestrators.DeleteAsync(repository.Path, "r1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.True(removing.SectionHeld);
    }

    /// <summary>
    /// A file system that, as the run's record is removed, asks from another thread whether the repository's one section
    /// is free - without waiting: held, a claim there would wait for the deletion to end.
    /// </summary>
    private sealed class Removing(IFileSystem inner, string record, string section) : PassThroughFileSystem(inner)
    {
        /// <summary>Whether the section was held as the record was removed; null until it was.</summary>
        public bool? SectionHeld { get; private set; }

        public override void DeleteFile(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(record), StringComparison.OrdinalIgnoreCase))
            {
                var asked = new Thread(() =>
                {
                    try
                    {
                        _ = MachineWideFile.Update(section, TimeSpan.Zero, () => true);
                        SectionHeld = false;
                    }
                    catch (HarnessException)
                    {
                        SectionHeld = true;
                    }
                });

                asked.Start();
                Assert.True(asked.Join(Bound), "the look at the section ended");
            }

            base.DeleteFile(path);
        }
    }

    /// <summary>A run's own steps and report are kept records: deleted only with --delete-evidence, and the owner file beside the directory goes with the run.</summary>
    [Fact]
    public async Task ARunsStepsAndReportAreKeptRecords_DeletedOnlyWithEvidence()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        Script(runner);
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
        var said = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        child.OutputDataReceived += (_, line) =>
        {
            if (line.Data is { } data)
            {
                said.Enqueue(data);
                lines.Add(data);
            }
        };
        child.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is { } data)
            {
                errors.Enqueue(data);
            }
        };
        child.BeginOutputReadLine();
        child.BeginErrorReadLine();

        // What the child is and said, for a failure to name: whether it ended and with what, every line of its output
        // and of its error stream.
        string Child()
            => $"the child {(child.HasExited ? $"exited {child.ExitCode.ToString(CultureInfo.InvariantCulture)}" : "is still running")}; "
                + $"it said: [{string.Join(" | ", said)}]; its error stream: [{string.Join(" | ", errors)}]";

        try
        {
            Assert.Equal("started two", Next(lines, line => line.StartsWith("started two", StringComparison.Ordinal), Child));
            Assert.Equal(0, kill(child.Id, Interrupt));
            Assert.StartsWith("interrupted: a safe pause was asked", Next(lines, line => line.StartsWith("interrupted", StringComparison.Ordinal), Child), StringComparison.Ordinal);
            Assert.True(child.WaitForExit(Bound), $"the child ended once the run was paused; {Child()}");

            var run = RunOf(repository);
            var flow = repository.Harness.OrchestrationStore.ReadFlow(run)!;

            Assert.True(child.ExitCode == HarnessExit.Cancelled, Child());
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

    /// <summary>The next line the child says that <paramref name="matches"/>, within the bound - or a failure naming what <paramref name="child"/> says of it.</summary>
    private static string Next(BlockingCollection<string> lines, Func<string, bool> matches, Func<string> child)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bound.CancelAfter(Bound);

        try
        {
            foreach (var line in lines.GetConsumingEnumerable(bound.Token))
            {
                if (matches(line))
                {
                    return line;
                }
            }
        }
        catch (OperationCanceledException) when (!Token.IsCancellationRequested)
        {
            // The bound passed: said below, with what the child is.
        }

        Assert.Fail($"the child did not say the line looked for within {Bound.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s; {child()}");
        return string.Empty;
    }

    /// <summary>A process of this test's own that sleeps for five minutes, standing for an engine.</summary>
    private static Process Sleeper() => Process.Start(TestHost.StartOf(TestHost.ChildRequest("sleep", "300000")))!;

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
        private readonly ConcurrentDictionary<string, bool> _deaf = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _order = new();
        private readonly ConcurrentQueue<FlowStepRun> _asked = new();
        private volatile FlowStepResult? _every;

        /// <summary>Every instance asked, in the order the coordinator started it.</summary>
        public IReadOnlyList<string> Started => [.. _order];

        /// <summary>Every run asked, whole, in the order the coordinator started it.</summary>
        public IReadOnlyList<FlowStepRun> Asked => [.. _asked];

        /// <summary>Answers <paramref name="step"/> with one result, every time it is asked.</summary>
        public void On(string step, FlowStepResult result) => _results[step] = result;

        /// <summary>Answers every step with one result.</summary>
        public void OnEvery(FlowStepResult result) => _every = result;

        /// <summary>Holds <paramref name="step"/> until <paramref name="release"/> completes, as a machine step the pause leaves running.</summary>
        public void Holds(string step, Task release) => _holds[step] = release;

        /// <summary>Holds <paramref name="step"/> until <paramref name="release"/> completes, deaf to the stop: a runner that does not end when its step is cancelled.</summary>
        public void HoldsDeaf(string step, Task release)
        {
            _holds[step] = release;
            _ = _deaf.TryAdd(step, true);
        }

        /// <summary>Does <paramref name="what"/> when <paramref name="step"/> starts, before it answers.</summary>
        public void OnStart(string step, Action what) => _onStart[step] = what;

        /// <summary>Waits, within the bound, until the runner has been asked for <paramref name="step"/>.</summary>
        public Task WaitStartedAsync(string step) => StartOf(step).Task.WaitAsync(Bound, Token);

        /// <inheritdoc/>
        public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        {
            _order.Enqueue(run.Step.Name);
            _asked.Enqueue(run);

            if (_onStart.TryGetValue(run.Step.Name, out var what))
            {
                what();
            }

            _ = StartOf(run.Step.Name).TrySetResult();

            if (_holds.TryRemove(run.Step.Name, out var held))
            {
                // A hold is one attempt's: what a stop or a pause ends, and the resume's own attempt of the step runs
                // as a step that is no longer held.
                await (_deaf.ContainsKey(run.Step.Name) ? held : held.WaitAsync(cancellationToken));
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

        /// <summary>Whether the engine ends in the moment a stop comes to end it: as it is told apart, before it is ended.</summary>
        public bool GoesWhenEnded { get; init; }

        public int CurrentId => real.CurrentId;

        public string CurrentMachine => real.CurrentMachine;

        public string? Current => real.Current;

        public bool IsAlive(int processId, string? stamp) => processId == Engine ? _alive : real.IsAlive(processId, stamp);

        public string? StampOf(int processId) => real.StampOf(processId);

        public bool IsSame(int processId, string stamp)
        {
            if (processId == Engine && GoesWhenEnded)
            {
                _alive = false;
            }

            return processId != Engine && real.IsSame(processId, stamp);
        }
    }

    /// <summary>A file system that says when a file is written or deleted, and can be made to meet a defect reading one.</summary>
    private sealed class Signalling(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _written = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _deleted = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _broken = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _failsOnce = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _failsAlways = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _endsOnce = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Action> _afterLooking = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Does <paramref name="what"/> once, right after the next look at whether <paramref name="path"/> is there: as another process writing it just after this one looked.</summary>
        public void AfterLookingAt(string path, Action what) => _afterLooking[Path.GetFullPath(path)] = what;

        /// <summary>Makes the next deletion of <paramref name="path"/> fail, as a file held open by another process does.</summary>
        public void FailsDeleteOnce(string path) => _failsOnce[Path.GetFullPath(path)] = true;

        /// <summary>Makes every deletion of <paramref name="path"/> fail, as a file another process keeps open does, until <see cref="Deletes"/>.</summary>
        public void FailsEveryDelete(string path) => _failsAlways[Path.GetFullPath(path)] = true;

        /// <summary>Deletes <paramref name="path"/> as it is again.</summary>
        public void Deletes(string path) => _failsAlways.TryRemove(Path.GetFullPath(path), out _);

        /// <summary>Makes the next deletion of <paramref name="path"/> end the session where it stands, as a crash of its process there would.</summary>
        public void EndsAtDeleteOnce(string path) => _endsOnce[Path.GetFullPath(path)] = true;

        /// <summary>Completes once <paramref name="path"/> is written.</summary>
        public Task Written(string path) => Of(_written, path).Task;

        /// <summary>Completes once <paramref name="path"/> is deleted.</summary>
        public Task Deleted(string path) => Of(_deleted, path).Task;

        /// <summary>Makes every read of <paramref name="path"/> meet a defect.</summary>
        public void Breaks(string path) => _broken[Path.GetFullPath(path)] = true;

        /// <summary>Reads <paramref name="path"/> as it is again.</summary>
        public void Mends(string path) => _broken.TryRemove(Path.GetFullPath(path), out _);

        public override void WriteAtomic(string path, Action<Stream> write)
        {
            base.WriteAtomic(path, write);
            _ = Of(_written, path).TrySetResult();
        }

        public override void DeleteFile(string path)
        {
            if (_failsOnce.TryRemove(Path.GetFullPath(path), out _) || _failsAlways.ContainsKey(Path.GetFullPath(path)))
            {
                throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
            }

            if (_endsOnce.TryRemove(Path.GetFullPath(path), out _))
            {
                throw new Crashed();
            }

            base.DeleteFile(path);
            _ = Of(_deleted, path).TrySetResult();
        }

        public override Stream OpenRead(string path)
            => _broken.ContainsKey(Path.GetFullPath(path))
                ? throw new InvalidOperationException("the disk said something no one expected.")
                : base.OpenRead(path);

        public override PathKind KindOf(string path)
        {
            var kind = base.KindOf(path);

            if (_afterLooking.TryRemove(Path.GetFullPath(path), out var what))
            {
                what();
            }

            return kind;
        }

        private static TaskCompletionSource Of(ConcurrentDictionary<string, TaskCompletionSource> signals, string path)
            => signals.GetOrAdd(Path.GetFullPath(path), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>
    /// A clock whose waits end only when it is moved (<see cref="TimerClock"/>) - and which, while it <see cref="Polls"/>,
    /// moves itself past the wait that stands each time the time is read, and gives the time only once a wait stands
    /// again: so the engine's poller, the one that waits on it, polls exactly where the session reads the time, and has
    /// polled before the session goes on.
    /// </summary>
    /// <param name="poll">How far each read moves it: the poller's own wait.</param>
    private sealed class PolledAtEachRead(TimeSpan poll) : TimeProvider
    {
        private readonly TimerClock _clock = new();
        private volatile bool _polls;
        private int _moving;

        /// <summary>Whether each read of the time makes the waiting poller poll.</summary>
        public bool Polls
        {
            get => _polls;
            set => _polls = value;
        }

        public override long TimestampFrequency => _clock.TimestampFrequency;

        public override long GetTimestamp() => _clock.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _clock.CreateTimer(callback, state, dueTime, period);

        public override DateTimeOffset GetUtcNow()
        {
            // Only a read made while the poller waits moves the clock, and never one made inside the poll it starts.
            if (_polls && _clock.Waiting > 0 && Interlocked.Exchange(ref _moving, 1) == 0)
            {
                try
                {
                    _clock.Advance(poll);
                    _clock.WaitingAsync().WaitAsync(Bound, Token).GetAwaiter().GetResult();
                }
                finally
                {
                    _ = Interlocked.Exchange(ref _moving, 0);
                }
            }

            return _clock.GetUtcNow();
        }
    }

    /// <summary>What a session's process meets where a test makes it end, as a crash would end it: nothing of the session catches it.</summary>
    private sealed class Crashed() : Exception("the process ended here");

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
            _ = _first.TrySetResult();
            also?.Invoke(message);
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

    /// <summary>
    /// A run's codebase graph goes with the run when it is deleted, and is never counted among what --delete-evidence
    /// guards: it is made from the tree, and made again whenever asked.
    /// </summary>
    [Fact]
    public async Task ARunsCodebaseGraph_GoesWithItsRun_WithoutDeleteEvidence()
    {
        var (repository, service, runner) = await ServiceAsync();
        using var disposed = repository;

        Script(runner);
        _ = await service.RunAsync(repository.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        var run = RunOf(repository);
        var harness = repository.Harness;
        var guarded = await harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: false, Token);
        var graph = new DssHarness.Core.Codebase.CodebaseGraphService(harness.GitClient, harness.FileSystem, harness.OrchestrationStore, DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild, harness.Platform, new RunningToolDouble(), harness.Output, TimeProvider.System);
        _ = await graph.SyncAsync(new DssHarness.Core.Codebase.CodebaseGraphSync(new DssHarness.Core.Repository.HarnessLayout(repository.Path, repository.Path), run, DssHarness.Core.Codebase.CodebaseGraphShape.Default, "worktrees", Rebuild: false, "test"), Token);
        Assert.True(File.Exists(run.GraphManifestFile));
        Assert.NotEmpty(Directory.EnumerateFiles(run.GraphExtractsDirectory, "*", SearchOption.AllDirectories));

        // The run's own records are what --delete-evidence guards, the same with its graph as without it.
        var refused = await harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: false, Token);

        Assert.Equal((HarnessExit.Refused, guarded.Message), (refused.ExitCode, refused.Message));
        Assert.True(File.Exists(run.GraphManifestFile));

        var deleted = await harness.OrchestratorService.DeleteAsync(repository.Path, "r1", deleteEvidence: true, Token);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.False(Directory.Exists(run.Directory));
    }
}
