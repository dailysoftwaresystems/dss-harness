using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// The implementation workflow this build ships, run as a run runs it: through its inputs, its triage and the questions
/// it may ask, the stops it shows as questions, the approval of its plan by a person, by the run's input or by the
/// request's own words, the advisor, the reuse lists its plan check holds, the tiers and efforts its steps say with the
/// fallback its tier gives them, and the answers its own configuration gives its questions - over scripted agents
/// through the engine's own runners, under the definition check a run begins with.
/// </summary>
public sealed class ImplementationFlowTests
{
    /// <summary>
    /// Every bound a test of a session waits for. A session of the shipped flow is a whole run's worth of git work -
    /// agents' worktrees made and folded, every step's records written - and under the whole suite's load, sharded two
    /// processes to the machine's cores, a session has been measured taking ten minutes where it takes seconds alone:
    /// the bound ends a session that is truly hung, never one the machine is merely slow at, for a session cut off by
    /// it would keep running and hold its files.
    /// </summary>
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(20);

    /// <summary>The engine's own looks, made quick.</summary>
    private static readonly FlowTimings Quick = new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// What a repository holds beside the workflow, as the flow's steps ask it: a remote the run pushes to and pulls a
    /// request against, legs its tests run on, CI configured on the base branch, a citation root the anchor checks
    /// read, a registry of mutation arms the sweep sweeps.
    /// </summary>
    private sealed record Ground(bool Remote = false, bool Legs = false, bool Ci = false, bool Anchors = false, bool Mutations = false);

    /// <summary>The kit: a repository holding the shipped workflow as init writes it, and the service over scripted agents.</summary>
    private sealed record Kit(HarnessFactory Harness, TempDirectory Tree, Scripted Agents, MachineSteps Steps, FlowService Service)
    {
        /// <summary>The run's records, r1 as a run starts unnamed.</summary>
        public OrchestratorLayout RunOf(string run = "r1")
            => new(run, Path.Combine(Tree.Path, ".orchestrators", run));
    }

    /// <summary>
    /// A repository whose init wrote the shipped implementation workflow and declared it, whose configuration holds the
    /// providers and tiers init seeds and whatever of <paramref name="ground"/> it asks, and whose slot holds the keys
    /// of both providers - over the engine's own step runners with every machine step the flow's mechanics do not
    /// reach answered by <see cref="MachineSteps"/>.
    /// </summary>
    private static async Task<Kit> PrepareAsync(TempDirectory temp, Action<Scripted>? script = null, string? keys = null, Ground? ground = null, Action<MachineSteps>? machine = null, Action<HarnessConfig>? adjust = null, IFileSystem? files = null, Func<HarnessFactory, IAgentRunner>? walking = null)
    {
        var harness = new HarnessFactory();
        var template = AgentProviderTemplates.Read();
        var platform = new DssHarness.Core.Platform.HostPlatform();
        var groundHeld = ground ?? new Ground();

        var config = new HarnessConfig
        {
            Workflows = [WorkflowTemplates.ShippedWorkflowName],
            Worktrees = new WorktreeSettings { Root = ".worktrees", PathLimit = groundHeld.Legs ? 480 : 260 },
            Defaults = new HarnessDefaults { AgentTier = template.AgentTier },
            Providers = [.. template.Providers],
            TierConfig = new Dictionary<string, List<string>>(template.TierConfig, StringComparer.OrdinalIgnoreCase),
            Legs = groundHeld.Legs
                ? new Dictionary<string, LegConfig>(StringComparer.Ordinal)
                {
                    ["local"] = new() { Os = platform.PlatformKey, Processor = platform.Processor, Toolchain = "dotnet", Config = "debug" },
                }
                : [],
            BuildConfigs = groundHeld.Legs ? new Dictionary<string, BuildConfiguration>(StringComparer.Ordinal) { ["debug"] = new() { DotnetConfiguration = "Debug" } } : [],
            Toolchains = groundHeld.Legs ? new Dictionary<string, ToolchainConfig>(StringComparer.Ordinal) { ["dotnet"] = new() } : [],
            Ci = groundHeld.Ci ? new CiSettings { LegJobPattern = "build-(?<leg>.+)", BuildStep = "Build", TestStep = "Test" } : new(),
            Anchors = groundHeld.Anchors ? new AnchorSettings { CitationRoots = ["src"] } : new(),
            Mutations = groundHeld.Mutations ? new MutationSettings { Registry = "arms.md" } : new(),
        };

        adjust?.Invoke(config);

        await harness.InitializeHarnessAsync(temp.Path, Token, config);

        OrchestrationKit.Write(temp.Path, Path.Combine(".harness-config", "workflows", ".env", "a.env"), keys ?? "ANTHROPIC_API_KEY=sk-test-one\nZAI_API_KEY=sk-test-two\n");
        harness.FilePermissions.ProtectSecret(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "a.env"));
        OrchestrationKit.Write(temp.Path, Path.Combine("src", "tool.cs"), "namespace Tool;\n");

        if (groundHeld.Mutations)
        {
            OrchestrationKit.Write(temp.Path, "arms.md", "# arms\n");
        }

        await harness.CommitAllAsync(temp.Path, "the workflow as init wrote it", Token);

        if (groundHeld.Remote)
        {
            using var bare = new TempDirectory();
            await harness.RunGitAsync(bare.Path, ["init", "--quiet", "--bare", "."], Token);
            await harness.RunGitAsync(temp.Path, ["remote", "add", "origin", bare.Path], Token);
            await harness.RunGitAsync(temp.Path, ["push", "--quiet", "origin", "HEAD:trunk"], Token);
            await harness.RunGitAsync(temp.Path, ["remote", "set-head", "origin", "trunk"], Token);
        }

        var agents = new Scripted();
        var steps = new MachineSteps { Store = harness.OrchestrationStore, Worktrees = harness.WorktreeService };

        script?.Invoke(agents);
        machine?.Invoke(steps);

        return new Kit(harness, temp, agents, steps, Service(harness, agents, steps, files, walking?.Invoke(harness)));
    }

    /// <summary>The service over the engine's own runners: every type the flow's head works through for real, the rest answered by the machine script - over <paramref name="files"/> where one is given, so a test stands between the engine and its records, and reasoning worked by <paramref name="walking"/> where one is given, so the engine's own fallback walk runs over a client a test scripts.</summary>
    private static FlowService Service(HarnessFactory harness, Scripted agents, MachineSteps machine, IFileSystem? files = null, IAgentRunner? walking = null)
    {
        var fileSystem = files ?? harness.FileSystem;
        var store = files is null ? harness.OrchestrationStore : new OrchestrationStore(fileSystem, harness.Output);
        var keys = new DssHarness.Core.Secrets.AgentKeyStore(fileSystem, harness.FilePermissions, harness.Platform);
        var create = new CreateAgentRunner(harness.ContextLoader, harness.Platform, store, harness.AgentService);
        var reason = new ReasonStepRunner(
            harness.ContextLoader,
            harness.Platform,
            store,
            create,
            walking ?? agents,
            fileSystem,
            keys,
            new GhProcess(harness.ProcessRunner),
            harness.Output,
            TimeProvider.System,
            new TestIsolation(),
            harness.GitClient,
            harness.ConfigStore);

        IFlowStepRunner runner = new FlowStepRunners(
            [
                new OrientRunner(harness.ContextLoader, harness.Platform, harness.GitClient, fileSystem, store, harness.WorktreeService, harness.RunLock, new TreeHolders(harness.RunLock, harness.Slots, harness.Identity)),
                new GraphifyCodebaseRunner(harness.ContextLoader, harness.Platform, harness.CodebaseGraph, harness.PathBudget, DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild, store),
                new PlanCheckRunner(harness.ContextLoader, harness.Platform, new PlanValidator(fileSystem, harness.PathBudget, harness.Platform)),
                create,
                reason,
                .. "build test run check-mutations anchor-balance anchor-citations ci-read ci-wait fold delete-agent refresh rebase commit push pull-request"
                    .Split(' ')
                    .Select(type => machine.Serving(type)),
            ],
            harness.GraphSyncs);

        machine.Store = store;

        return new(
            harness.ContextLoader,
            harness.Platform,
            fileSystem,
            store,
            harness.Identity,
            new FlowSessions(store, fileSystem, harness.Identity, harness.GitClient),
            new WorkflowValidationService(harness.ContextLoader, fileSystem, harness.Platform, keys, DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild),
            runner,
            harness.OrchestrationLog,
            harness.ChildSessions,
            harness.PathBudget,
            harness.Output,
            harness.GitClient,
            harness.AgentService,
            agents,
            keys,
            new TestIsolation(),
            clock: null,
            timings: Quick);
    }

    /// <summary>The run's own driving: a start over the shipped workflow with its inputs, within the bound.</summary>
    private static Task<CommandOutcome> StartAsync(Kit kit, params string[] inputs)
        => kit.Service.RunAsync(kit.Tree.Path, WorkflowTemplates.ShippedWorkflowName, run: null, inputs, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>A safe pause of the run's engine, within the bound.</summary>
    private static Task<CommandOutcome> PauseAsync(Kit kit, bool all = false)
        => kit.Service.PauseAsync(kit.Tree.Path, "r1", all, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>A stop of the run's engine, within the bound.</summary>
    private static Task<CommandOutcome> StopAsync(Kit kit)
        => kit.Service.StopAsync(kit.Tree.Path, "r1", now: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>A resume of the run, within the bound.</summary>
    private static Task<CommandOutcome> ResumeAsync(Kit kit)
        => kit.Service.ResumeAsync(kit.Tree.Path, "r1", json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>An answer to the run's open question, within the bound.</summary>
    private static Task<CommandOutcome> AnswerAsync(Kit kit, string answer, string? text = null)
        => kit.Service.AnswerAsync(kit.Tree.Path, "r1", answer, question: null, text: text, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>An answer to one question the run holds open, by its id, within the bound.</summary>
    private static Task<CommandOutcome> AnswerOneAsync(Kit kit, string question, string answer, string? text = null)
        => kit.Service.AnswerAsync(kit.Tree.Path, "r1", answer, question: question, text: text, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>An amendment to the run's request, within the bound: handed to the engine where one runs it.</summary>
    private static Task<CommandOutcome> AmendAsync(Kit kit, string text)
        => kit.Service.AmendAsync(kit.Tree.Path, "r1", text, textFile: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>A changed answer to the last set a person answered, within the bound.</summary>
    private static Task<CommandOutcome> ChangeAsync(Kit kit, string answer, string? text = null)
        => kit.Service.AnswerAsync(kit.Tree.Path, "r1", answer, question: null, text: text, textFile: null, again: true, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>
    /// An answer to one question the run holds open, by its id, kept up until the run takes it: an answer handed to
    /// an engine whose session is ending lands in the inbox for that session's next - the design's own rule - and
    /// where none comes the answer is given again, the question standing open until one is taken.
    /// </summary>
    private static async Task AnswerTakenAsync(Kit kit, string question, string answer, string? text = null)
    {
        for (var tries = 0; tries < 10; tries++)
        {
            _ = await AnswerOneAsync(kit, question, answer, text);

            var taken = false;

            for (var waited = 0; waited < 100 && !(taken = (kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())?.Open ?? []).All(one => one.Id != question)); waited++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), Token);
            }

            if (taken)
            {
                return;
            }
        }

        Assert.Fail($"the answer of '{question}' was never taken: {Shown(kit)}");
    }

    /// <summary>The run's questions, as the store holds them now.</summary>
    private static FlowQuestionsFile Questions(Kit kit)
        => kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf()) ?? throw new InvalidOperationException("the run holds no questions file");

    /// <summary>The last set a person answered, as the run's questions file reads it - the one a changed answer names.</summary>
    private static string? NewestPersonAnswer(Kit kit)
        => Questions(kit).Questions
            .SelectMany(question => question.Answers.Where(answer => answer.By?.Who == "person").Select(answer => (question.Id, answer.At)))
            .OrderByDescending(answer => answer.At)
            .Select(answer => (string?)answer.Id)
            .FirstOrDefault();

    /// <summary>When the last answer a person gave was recorded, as the run's questions file reads it.</summary>
    private static DateTimeOffset NewestPersonAnswerAt(Kit kit)
        => Questions(kit).Questions
            .SelectMany(question => question.Answers.Where(answer => answer.By?.Who == "person").Select(answer => answer.At))
            .DefaultIfEmpty()
            .Max();

    /// <summary>The run's events, whole, read as a diagnostic that shares the file with the engine writing it.</summary>
    private static string Events(Kit kit)
    {
        using var shared = new FileStream(Path.Combine(kit.RunOf().Directory, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var read = new StreamReader(shared);

        return read.ReadToEnd() ?? string.Empty;
    }

    /// <summary>Waits within the bound until the run holds an open question the predicate names, and returns it.</summary>
    private static async Task<FlowQuestionEntry> WhenOpenAsync(Kit kit, Func<FlowQuestionEntry, bool> which, Func<string>? said = null)
    {
        await WhenAsync(
            () => (kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())?.Open ?? []).Any(which),
            said ?? (() => Shown(kit)));

        return Questions(kit).Open.Single(which);
    }

    /// <summary>The run's open questions and its last steps, as a diagnostic an assertion carries when it fails.</summary>
    private static string Shown(Kit kit)
    {
        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf());
        var open = string.Join(
            " | ",
            (questions?.Open ?? []).Select(question => $"{question.Id} kind {question.Kind} by {question.RaisedBy?.Step}: {question.ProblemShown?.Happened}"));
        var steps = string.Join(
            " | ",
            (kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Steps ?? [])
                .OrderByDescending(step => step.StartedAt ?? DateTimeOffset.MinValue)
                .Take(14)
                .Select(step => $"{step.Instance} {step.State}{(step.WaitsOn.Count > 0 ? $" waits[{string.Join(",", step.WaitsOn)}]" : string.Empty)} {(step.Exit is { } exit ? exit.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)} {step.Error ?? string.Empty}"));
        var eventsPath = Path.Combine(kit.RunOf().Directory, "events.jsonl");
        var events = "no events";

        if (System.IO.File.Exists(eventsPath))
        {
            try
            {
                using var shared = new FileStream(eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var read = new StreamReader(shared);
                events = string.Join(" | ", (read.ReadToEnd() ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6));
            }
            catch (IOException)
            {
                events = "the events file was being written";
            }
        }

        return $"open: {open} || steps: {steps} || events: {events}";
    }

    /// <summary>
    /// The shipped flow runs from its request to its end: triage asks the person and is answered in its own conversation, what
    /// only a person can lift is put to them as the stops triage wrote - with the advisor's recommendation - and the
    /// person's decision reaches the planner and the auditor; the plan passes the machine's check of its reuse lists and
    /// the independent audit, a person approves it with the advisor's recommendation beside, and the run builds its one
    /// item, reviews it, audits itself and writes its records to the end. Every step works on its tier's first model with
    /// its own effort, the advisor and the reporter on theirs.
    /// </summary>
    [Fact]
    public async Task TheShippedFlow_RunsFromItsRequestToItsEnd_WithTriageAsked_TheStopsPutToThePerson_AndThePlanApprovedByOne()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            agents =>
            {
                agents.Queue("triage", Asked("Which report is it?"), Completed(Triage(stops: [Stop()])));
                agents.Queue("planner", Completed(Plan(One())));
            });

        var triage = await StartAsync(kit, "request=Ship the orders report.");

        // Triage asked the person: the step waits on its question alone, with the choices answer and decide.
        Assert.Equal(FlowExit.Paused, triage.ExitCode);
        var asked = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

        Assert.Equal(("ask", "intake.triage"), (asked.Kind, asked.RaisedBy?.Step));
        Assert.Equal(["answer", "decide", "abort"], asked.Options.Select(option => option.Answer));
        Assert.Equal("Which report is it?", Assert.Single(asked.Questions).Question);

        // The answer is the agent's next message, in the same attempt: the scripted triage goes on with it and reports one stop.
        var stopped = await AnswerAsync(kit, "answer", "The orders report, weekly.");

        Assert.Equal(FlowExit.Paused, stopped.ExitCode);
        var triageAgain = kit.Agents.Of("triage");

        Assert.Equal(2, triageAgain.Count);
        Assert.Equal("The person answered:\nThe orders report, weekly.", triageAgain[1].Prompt);
        _ = await AnswerAsync(kit, "continue", "Ship the weekly one; the monthly waits.");
        _ = await AnswerAsync(kit, "approve");

        var done = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        Assert.Equal(nameof(FlowRunStatus.Done), done.Status);

        // The stop reached the planner and the auditor as the flow's prompts read it, with what the person decided.
        var plan = kit.Agents.Of("planner").Single();
        var audit = kit.Agents.Of("plan-auditor").Single();

        Assert.Contains("Is the orders report shipped weekly or monthly?", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Ship the weekly one; the monthly waits.", plan.Prompt, StringComparison.Ordinal);
        Assert.Contains("Is the orders report shipped weekly or monthly?", audit.Prompt, StringComparison.Ordinal);
        Assert.Contains("Ship the weekly one; the monthly waits.", audit.Prompt, StringComparison.Ordinal);

        // Every question the run raised, and every answer, with its recommendation.
        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

        Assert.Equal(["q1", "q2", "q3"], questions.Questions.Select(question => question.Id));
        Assert.All(questions.Questions, question => Assert.Equal("answered", question.State));
        Assert.Equal("continue", questions.Questions[1].Answers.Single().Answer);
        Assert.Equal("Ship the weekly one; the monthly waits.", questions.Questions[1].Answers.Single().Text);
        Assert.Equal("approve", questions.Questions[2].Answers.Single().Answer);
        // The blocked question carries the stop's own recommendation, the agent's; the approval's, an engine question,
        // is the advisor's.
        Assert.Equal(("given", "agent"), (questions.Questions[1].Recommendation!.State, questions.Questions[1].Recommendation!.By));
        Assert.Equal("the weekly one", questions.Questions[1].Questions.Single().Recommended);
        var recommended = questions.Questions[2].Recommendation!;
        Assert.True(("given", "advisor", "approve") == (recommended.State, recommended.By, recommended.Answer), $"{recommended.State}: {recommended.Why}");

        // Each step on its tier's first model with its own effort, the advisor and the reporter on theirs.
        Assert.Equal(
            [
                ("auditor", "claude-opus-5-5", "xhigh"),
                ("implementer", "claude-sonnet-5-5", "xhigh"),
                ("plan-auditor", "claude-opus-5-5", "xhigh"),
                ("planner", "claude-fable-5-1", "xhigh"),
                ("review-scope", "claude-haiku-5-5", "low"),
                ("reviewer-correctness", "claude-opus-5-5", "xhigh"),
                ("scribe", "claude-sonnet-5-5", "high"),
                ("triage", "claude-opus-5-5", "high"),
            ],
            kit.Agents.Asked
                .Where(step => step.Order is { Count: > 0 } && step.AgentDefinition is not "advisor" and not "reporter")
                .Select(ServedOf)
                .GroupBy(served => served.Definition, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(served => served.Definition, StringComparer.Ordinal)
                .ToArray());

        // The advisor too, on its own tier, asked as many times as the run had an open question a person was left with.
        Assert.All(kit.Agents.Of("advisor"), step => Assert.Equal(("claude-opus-5-5", "high"), (step.Order![0].Model, step.Order![0].Effort)));
        Assert.NotEmpty(kit.Agents.Of("advisor"));

        static (string Definition, string Model, string Effort) ServedOf(AgentStepRequest step)
            => (step.AgentDefinition!, step.Order![0].Model, step.Order![0].Effort!);
    }

    /// <summary>
    /// The plan is approved beforehand only by the person's own word: the input takes yes and no and refuses any other
    /// before the run is made, and a run given yes never asks - the record of its approval says the input gave it, quoted.
    /// </summary>
    [Fact]
    public async Task APlanApprovedBeforehand_ByTheRunsInput_IsNeverAsked_AndTheRecordQuotesTheInput()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(temp);

        var refused = await StartAsync(kit, "request=Ship it.", "preapproved=maybe");

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal("input 'preapproved' takes one of yes, no, and the run was given 'maybe'", refused.Message);

        var done = await StartAsync(kit, "request=Ship it.", "preapproved=yes");

        Assert.Equal(HarnessExit.Success, done.ExitCode);
        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

        Assert.Empty(questions.Open);
        var approval = questions.Questions.Single(question => question.RaisedBy?.Step == "planning#1.approval");

        Assert.Equal(("answered", "approve", "input", "preapproved = 'yes'"),
            (approval.State, approval.Answers.Single().Answer, approval.Answers.Single().By!.Who, approval.Answers.Single().By!.Input));
    }

    /// <summary>
    /// The request's own words approve a plan only where they stand in the request: a sentence triage reports that is
    /// there approves it, quoted word for word in the record; one that is not there approves nothing and the person is
    /// asked, the record saying why; and the input no overrules whatever the request says.
    /// </summary>
    [Fact]
    public async Task APlanApprovedByTheRequestsOwnWords_OnlyWhereTheSentenceStandsThere()
    {
        using var approved = new TempDirectory();
        var kit = await PrepareAsync(
            approved,
            agents => agents.Queue("triage", Completed(Triage(sentence: "This plan is approved beforehand; build it without showing it for approval."))));

        var done = await StartAsync(kit, "request=Ship the orders report. This plan is approved beforehand; build it without showing it for approval.");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])}");
        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

        Assert.Empty(questions.Open);
        var preApproved = Assert.Single(questions.Questions, question => question.RaisedBy?.Step?.EndsWith(".approval", StringComparison.Ordinal) == true);
        var by = preApproved.Answers.Single().By!;

        Assert.Equal(("input", "approve"), (by.Who, preApproved.Answers.Single().Answer));
        Assert.Equal("the words \"This plan is approved beforehand; build it without showing it for approval.\" in input 'request'", by.Input);

        // A sentence the request does not hold approves nothing: the person is asked, and the record says the words were looked for.
        using var missing = new TempDirectory();
        var other = await PrepareAsync(
            missing,
            agents => agents.Queue("triage", Completed(Triage(sentence: "This plan is approved beforehand; build it without showing it for approval."))));

        var asked = await StartAsync(other, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, asked.ExitCode);
        var approval = other.Harness.OrchestrationStore.ReadFlowQuestions(other.RunOf())!.Open.Single();

        Assert.Equal("planning#1.approval", approval.RaisedBy?.Step);

        // The input no asks the person whatever the request says.
        using var overruled = new TempDirectory();
        var third = await PrepareAsync(
            overruled,
            agents => agents.Queue("triage", Completed(Triage(sentence: "This plan is approved beforehand; build it without showing it for approval."))));

        var held = await StartAsync(third, "request=Ship the orders report. This plan is approved beforehand; build it without showing it for approval.", "preapproved=no");

        Assert.Equal(FlowExit.Paused, held.ExitCode);
        Assert.Equal("planning#1.approval", third.Harness.OrchestrationStore.ReadFlowQuestions(third.RunOf())!.Open.Single().RaisedBy?.Step);
    }

    /// <summary>
    /// The reuse lists run through the flow's own check: a plan whose item reuses a path the tree does not hold does not
    /// pass, the planning loop runs again and the planner is told what did not pass, and the plan that passes - every
    /// reused path in the tree, the item naming something under one of its three lists - is the one a person approves.
    /// </summary>
    [Fact]
    public async Task TheReuseLists_RunThroughThePlansOwnCheck_AndThePlannerIsToldWhatDidNotPass()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            agents =>
            {
                agents.Queue("triage", Completed(Triage()));
                agents.Queue("planner", Completed(Plan(One(reuses: "src/not-there.cs"))), Completed(Plan(One())));
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var end = await AnswerAsync(kit, "approve");

        Assert.True(HarnessExit.Success == end.ExitCode, $"{end.Message} | {string.Join(" | ", end.Details ?? [])}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        var plans = kit.Agents.Of("planner");

        Assert.Equal(2, plans.Count);

        // The planner's second round goes on with the check's problems, on the resume prompt.
        Assert.Contains("src/not-there.cs", plans[1].Prompt, StringComparison.Ordinal);
        Assert.StartsWith("Your plan did not pass.", plans[1].Prompt, StringComparison.Ordinal);

        // The check ran twice: the first not ok with the missing path among its problems, the second ok.
        var checks = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps
            .Where(step => step.Step == "planning.check")
            .OrderBy(step => step.Instance, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, checks.Count);
        Assert.False(checks[0].Output!["ok"]!.GetValue<bool>());
        Assert.Contains(checks[0].Output!["problems"]!.AsArray(), problem => problem!.GetValue<string>().Contains("src/not-there.cs", StringComparison.Ordinal));
        Assert.True(checks[1].Output!["ok"]!.GetValue<bool>());
    }

    /// <summary>
    /// Where the first provider's key is not in the slot, every step falls back to its tier's later models: a run with
    /// only the Z.AI key works every step of the shipped flow on glm-5.3, each with its own effort.
    /// </summary>
    [Fact]
    public async Task WhereNoAnthropicKeyIsHeld_EveryStepFallsBackToItsTiersLaterModels()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(temp, keys: "ZAI_API_KEY=sk-test-two\n");

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])}");
        Assert.Equal(
            ["auditor", "implementer", "plan-auditor", "planner", "review-scope", "reviewer-correctness", "scribe", "triage"],
            kit.Agents.Asked.Where(step => step.Order is { Count: > 0 }).Select(step => step.AgentDefinition).Order(StringComparer.Ordinal).ToArray());
        Assert.All(
            kit.Agents.Asked.Where(step => step.Entries is not null),
            step => Assert.Equal(("glm-5.3", "zai"), (step.Entries!.Model.Code, step.Entries.Provider.Name)));
    }

    /// <summary>
    /// The answers the flow gives its own questions: work items of a round that do not pass its plan check are built as
    /// they are - the pause that asks a person is answered by the flow's own configuration, never asked, and the record
    /// says the configuration gave it.
    /// </summary>
    [Fact]
    public async Task TheAnswersTheFlowGivesItsOwnQuestions_AreGivenByItsConfiguration_NeverAsked()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            agents =>
            {
                agents.Queue("triage", Completed(Triage()));
                agents.Queue("planner", Completed(Plan(One())));

                // The round's audit is not clean and names the next round's work: two items that own one path.
                agents.Queue(
                    "auditor",
                    Completed(SelfAudit(clean: false, items: [One(name: "first"), One(name: "first-again", owned: "src/one/**")])));
            });

        var end = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == end.ExitCode, $"{end.Message} | {string.Join(" | ", end.Details ?? [])}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

        Assert.Empty(questions.Open);
        var problems = questions.Questions.Single(question => question.RaisedBy?.Step == "rounds#2.item-problems");

        Assert.Equal(("answered", "continue", "configuration"), (problems.State, problems.Answers.Single().Answer, problems.Answers.Single().By!.Who));

        // The items were built as they are: the plan's one in the first round, and the audit's two in the second.
        Assert.Equal(3, kit.Agents.Of("implementer").Count);
    }

    /// <summary>
    /// The review runs its six aspects behind the scope that names them, its gathering block holding their findings:
    /// a scope that takes every aspect in asks each of the six reviewers once, and the round after - a scope that
    /// keeps every aspect but correctness out - asks the correctness reviewer alone, for it runs every round. A
    /// finding the review confirms is worked: the builder's agent reworks it on the prompt that names every aspect's
    /// findings as one list, and the fix is folded and committed again. What the builder declined of a review before
    /// is the one thing of the author's a reviewer sees, carried whole into the reviewer's prompt with its reason.
    /// </summary>
    [Fact]
    public async Task TheReviewOfItsSixAspects_RunsBehindTheScope_CorrectnessEveryRound_AndItsFindingsWorkedAndDeclined()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            agents =>
            {
                agents.Queue(
                    "implementer",
                    Completed(Implement(declined: [Declined("correctness src/one/one.cs:1: The report ships without its frame, so a week of orders reads as one run")])),
                    Completed(Implement()));
                agents.Queue("review-scope", Completed(Scope(true, true, true, true, true)), Completed(Scope()));
                agents.Queue("reviewer-correctness", Completed(Review(Finding())), Completed(Review()));
            });

        var end = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == end.ExitCode, $"{end.Message} | {string.Join(" | ", end.Details ?? [])}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // Round one asked every aspect the scope took in; the round after, correctness alone, as it runs every round.
        Assert.Equal(2, kit.Agents.Of("reviewer-correctness").Count);
        Assert.Single(kit.Agents.Of("reviewer-errors"));
        Assert.Single(kit.Agents.Of("reviewer-tests"));
        Assert.Single(kit.Agents.Of("reviewer-types"));
        Assert.Single(kit.Agents.Of("reviewer-comments"));
        Assert.Single(kit.Agents.Of("reviewer-simplicity"));

        // The reviewer saw what the builder declined, with the reason a reviewer can check.
        Assert.Contains("the finding is wrong: the frame is decided elsewhere", kit.Agents.Of("reviewer-correctness")[0].Prompt, StringComparison.Ordinal);

        // The builder's agent reworked the finding, on the prompt that names it where it is.
        var rework = kit.Agents.Of("implementer")[1];

        Assert.StartsWith("Your work was folded and reviewed, and the review found something.", rework.Prompt, StringComparison.Ordinal);
        Assert.Contains("src/one/one.cs:1", rework.Prompt, StringComparison.Ordinal);
        Assert.Contains("The report ships without its frame, so a week of orders reads as one run", rework.Prompt, StringComparison.Ordinal);

        // The fix was folded and committed again before the next review round.
        Assert.Contains(
            kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps,
            step => step.Step.EndsWith(".recheckpoint", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done));
    }

    /// <summary>
    /// What the item's builder says only a person can give is put to the person before anything of the item is tested
    /// or folded, with the advisor's recommendation: answered continue, the person's words go back to the builder as
    /// its unblock prompt's answer; answered skip, the item is left out of the run, its agent dropped with its
    /// uncommitted work and its folder folded by no one.
    /// </summary>
    [Fact]
    public async Task ABlockedItem_IsPutToThePerson_ItsUnblockCarryingThePersonsText_OrItsWorkDropped()
    {
        using var held = new TempDirectory();
        var kit = await PrepareAsync(
            held,
            agents => agents.Queue("implementer", Completed(Implement(blocked: [Blocked()])), Completed(Implement())));

        var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);
        var asked = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

        Assert.Contains("item-blocked", asked.RaisedBy?.Step, StringComparison.Ordinal);
        Assert.Equal(["continue", "skip", "abort"], asked.Options.Select(option => option.Answer));
        Assert.Contains("The report frame", asked.ProblemShown!.Happened, StringComparison.Ordinal);
        Assert.Equal(("given", "advisor"), (asked.Recommendation!.State, asked.Recommendation!.By));

        var end = await AnswerAsync(kit, "continue", "Write the report without its frame; the frame waits.");

        Assert.True(HarnessExit.Success == end.ExitCode, $"{end.Message} | {string.Join(" | ", end.Details ?? [])}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // The person's words went back to the builder, as the answer its unblock prompt reads.
        var unblock = kit.Agents.Of("implementer")[1];

        Assert.StartsWith("A person has answered what your last report said you could not do.", unblock.Prompt, StringComparison.Ordinal);
        Assert.Contains("Write the report without its frame; the frame waits.", unblock.Prompt, StringComparison.Ordinal);

        // Answered skip, the item is left out: its agent is dropped with its uncommitted work, nothing of it is folded or committed.
        using var dropped = new TempDirectory();
        var other = await PrepareAsync(dropped, agents => agents.Queue("implementer", Completed(Implement(blocked: [Blocked()]))));

        var waiting = await StartAsync(other, "request=Ship the orders report.", "preapproved=yes");

        Assert.Equal(FlowExit.Paused, waiting.ExitCode);

        var skipped = await AnswerAsync(other, "skip");

        Assert.True(HarnessExit.Success == skipped.ExitCode, $"{skipped.Message} | {string.Join(" | ", skipped.Details ?? [])}");
        Assert.Equal(nameof(FlowRunStatus.Done), other.Harness.OrchestrationStore.ReadFlow(other.RunOf())!.Status);

        var fold = other.Harness.OrchestrationStore.ReadFlow(other.RunOf())!.Steps;

        // Nothing of the item was folded, and its agent was released by no one: only the drop took it.
        Assert.DoesNotContain(fold, step => step.Step.EndsWith(".fold", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done));
        Assert.DoesNotContain(fold, step => step.Step.EndsWith(".release", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done));
        var drop = other.Steps.Of("delete-agent").Single(run => run.Instance.EndsWith(".drop", StringComparison.Ordinal));

        Assert.Contains(".drop", drop.Instance, StringComparison.Ordinal);
    }

    /// <summary>
    /// The item's handlers: a test that ends red goes to the item's builder on its mend prompt, and the test runs
    /// again; a fold refused for a path its within leaves out is answered fail by its own configuration and goes to
    /// the builder's reconciler - the agent caught up with the tree, then settled on its reconcile prompt - before
    /// the fold runs again; a registry finding after the fold goes to the builder on its anchors prompt, and the
    /// checks run again. All in one item, to its end.
    /// </summary>
    [Fact]
    public async Task TheHandlers_TakeARedTestToTheMender_AFoldRefusedOutsideToTheReconciler_ARegistryFindingToTheCiter()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            ground: new Ground(Legs: true, Anchors: true),
            script: agents => agents.Queue("implementer", Completed(Implement()), Completed(Implement()), Completed(Implement()), Completed(Implement())),
            machine: steps =>
            {
                steps.Queue("test", MachineSteps.Green(), MachineSteps.Red(HarnessExit.CommandFailed, "the item's tests are red"));
                steps.Queue(
                    "fold",
                    FlowStepResult.Standing(new FlowDryRun(RequestStanding.Sound, [], [], [], "the fold's plan") { Outside = ["docs/late.md"], Agent = "a1" }));
                steps.Queue(
                    "anchor-citations",
                    new FlowStepResult { Asked = null, Passed = true, Red = true, Exit = DssHarness.Core.Anchors.AnchorExit.Findings, Output = new JsonObject { ["passed"] = false, ["findings"] = new JsonArray("src/one/one.cs:1: `DS-1` resolves to no row") } });
            });

        var end = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == end.ExitCode, $"{end.Message} | {Shown(kit)}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps;
        var builder = kit.Agents.Of("implementer");

        // The red test went to the mender, and the test ran again - and again after each handler sent the run back to
        // the item's block, green every time.
        var verify = flow.Where(step => step.Step.EndsWith(".verify", StringComparison.Ordinal)).ToList();

        Assert.Equal((nameof(FlowInstanceState.Failed), HarnessExit.CommandFailed, "mend"), (verify[0].State, verify[0].Exit, verify[0].HandledBy));
        Assert.All(verify.Skip(1), run => Assert.Equal(nameof(FlowInstanceState.Done), run.State));
        Assert.StartsWith("The engine built your work and ran its tests, and the result is red.", builder[1].Prompt, StringComparison.Ordinal);

        // The refused fold went to the reconciler - caught up with the tree, then settled - and the fold ran again,
        // folding at last.
        var folds = flow.Where(step => step.Step.EndsWith(".fold", StringComparison.Ordinal)).ToList();

        Assert.Equal((nameof(FlowInstanceState.Failed), HarnessExit.Refused, "foldOutside", "reconcile"), (folds[0].State, folds[0].Exit, folds[0].EndedByQuestion, folds[0].HandledBy));
        Assert.Equal(nameof(FlowInstanceState.Done), folds[^1].State);
        Assert.Contains(flow, step => step.Step.EndsWith(".catch-up", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done));
        Assert.StartsWith("The engine tried to fold your work into the main tree, and the fold was refused.", builder[2].Prompt, StringComparison.Ordinal);

        // The registry finding after the fold went to the citer, and the checks ran again, clean at last.
        var checks = flow.Where(step => step.Step.EndsWith(".fold-checks", StringComparison.Ordinal) && step.Group).ToList();

        Assert.Equal((nameof(FlowInstanceState.Failed), DssHarness.Core.Anchors.AnchorExit.Findings, "cite"), (checks[0].State, checks[0].Exit, checks[0].HandledBy));
        Assert.Equal(nameof(FlowInstanceState.Done), checks[^1].State);
        Assert.StartsWith("Your work was folded, and the repository's registry checks found something wrong with it.", builder[3].Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The recovery of a red check of the pull request: the diagnostician reads the failure, a course only a person
    /// can lift is put to the person with the choices and the advisor's recommendation, and the person's words are
    /// turned into the diagnosis the run acts on - its remedies built, tested, folded, committed and reviewed as work
    /// items are, what their builders cannot do put to the person as an item's is; the fix is published, and the
    /// step that failed runs again.
    /// </summary>
    [Fact]
    public async Task TheRecovery_OfARedCheckOfThePullRequest_PutsDirectionToThePerson_Redirects_BuildsItsRemediesAsItemsAre_Publishes_AndRunsTheStepAgain()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            ground: new Ground(Remote: true, Ci: true),
            script: agents =>
            {
                agents.Queue(
                    "implementer",
                    Completed(Implement()),
                    Completed(Implement(blocked: [Blocked()])),
                    Completed(Implement(blocked: [Blocked()])),
                    Completed(Implement()));
                agents.Queue(
                    "diagnostician",
                    Completed(Diagnosis(course: "person", choices: [Choice("Fix the workflow's file", "the run builds the fix and the check runs again")])),
                    Completed(Diagnosis(course: "fix", items: [Remedy("frame", "src/frame/**"), Remedy("name", "src/name/**")])));
            },
            machine: steps => steps.Queue(
                "ci-wait",
                FlowStepResult.Ended(
                    passed: false,
                    DssHarness.Core.Ci.CiExit.LegRed,
                    "build-reports red on sha-pushed",
                    new JsonObject { ["passed"] = false, ["commit"] = "sha-pushed", ["checks"] = new JsonArray(), ["red"] = new JsonArray(new JsonObject { ["name"] = "build-reports", ["state"] = "red", ["conclusion"] = "failure", ["url"] = "https://forge/checks/1" }), ["pending"] = new JsonArray(), ["unsettled"] = new JsonArray() })));

        var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);
        var direction = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

        Assert.True(direction.RaisedBy?.Step?.Contains("direction", StringComparison.Ordinal) == true, Shown(kit));
        Assert.Equal(["retry", "continue", "abort"], direction.Options.Select(option => option.Answer));
        Assert.Contains("The check reads a job the workflow no longer runs", direction.ProblemShown!.Happened, StringComparison.Ordinal);
        Assert.Contains("the run builds the fix and the check runs again", direction.ProblemShown!.Happened, StringComparison.Ordinal);
        Assert.Equal(("given", "advisor"), (direction.Recommendation!.State, direction.Recommendation!.By));

        // The diagnostician read the failure of the step that failed.
        Assert.StartsWith("A step of this run failed, and nothing in the flow took the failure.", kit.Agents.Of("diagnostician")[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("checks", kit.Agents.Of("diagnostician")[0].Prompt, StringComparison.Ordinal);

        // The person's words are turned into the diagnosis the run acts on, and its remedies are built - what their
        // builders cannot do put to the person as an item's is, each as its builder reports it: the first left out,
        // the second gone on with.
        var answering = await AnswerAsync(kit, "continue", "Fix the workflow file; the job name changed.");

        Assert.StartsWith("The person you asked has answered, with what the run is to do", kit.Agents.Of("diagnostician")[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("Fix the workflow file; the job name changed.", kit.Agents.Of("diagnostician")[1].Prompt, StringComparison.Ordinal);
        Assert.StartsWith("Build this work item. It puts right a failure of this run", kit.Agents.Of("implementer")[1].Prompt, StringComparison.Ordinal);

        CommandOutcome end = answering;

        while (kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open.Any(question => question.RaisedBy?.Step?.Contains("item-blocked", StringComparison.Ordinal) == true))
        {
            foreach (var waiting in kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open.Where(question => question.RaisedBy?.Step?.Contains("item-blocked", StringComparison.Ordinal) == true).ToList())
            {
                end = waiting.RaisedBy?.Step?.Contains("items#1", StringComparison.Ordinal) == true
                    ? await AnswerOneAsync(kit, waiting.Id, "skip")
                    : await AnswerOneAsync(kit, waiting.Id, "continue", "Take the name from the workflow own file.");
            }
        }

        Assert.True(HarnessExit.Success == end.ExitCode, $"{end.Message} | {string.Join(" | ", end.Details ?? [])} | {Shown(kit)}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // The remedy that went on was unblocked with the person's words, and reviewed as work items are.
        Assert.StartsWith("A person has answered what your last report said you could not do.", kit.Agents.Of("implementer")[^1].Prompt, StringComparison.Ordinal);
        Assert.Contains("Take the name from the workflow own file.", kit.Agents.Of("implementer")[^1].Prompt, StringComparison.Ordinal);
        Assert.Contains(kit.Agents.Of("reviewer-correctness"), step => step.Prompt.Contains("Name the job the workflow runs", StringComparison.Ordinal));

        // The fix was published, and the step that failed ran again, green.
        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps;
        var checks = flow.Where(step => step.Step == "checks" || step.Step.EndsWith(".checks", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, checks.Count);
        Assert.Equal(nameof(FlowInstanceState.Failed), checks[0].State);
        Assert.Equal(nameof(FlowInstanceState.Done), checks[1].State);
        Assert.Contains(flow, step => step.Step == "publish" || step.Step.EndsWith(".publish", StringComparison.Ordinal));
        Assert.Equal(nameof(FlowInstanceState.Done), flow.Single(step => step.Step == "publish" || step.Step.EndsWith(".publish", StringComparison.Ordinal)).State);
    }

    /// <summary>
    /// A step the recovery took five times, and failed again, is asked of the person at the next - with the
    /// advisor's recommendation of a fix - the diagnoses after the first on the resume prompt that says the last one
    /// did not put it right.
    /// </summary>
    [Fact]
    public async Task AStepTheRecoveryTookFiveTimes_IsAskedAtTheNext_WithTheAdvisorsRecommendation()
    {
        using var temp = new TempDirectory();
        var red = FlowStepResult.Ended(
            passed: false,
            DssHarness.Core.Ci.CiExit.LegRed,
            "build-reports red on sha-pushed",
            new JsonObject { ["passed"] = false, ["commit"] = "sha-pushed", ["checks"] = new JsonArray(), ["red"] = new JsonArray(new JsonObject { ["name"] = "build-reports", ["state"] = "red", ["conclusion"] = "failure", ["url"] = "https://forge/checks/1" }), ["pending"] = new JsonArray(), ["unsettled"] = new JsonArray() });
        var kit = await PrepareAsync(
            temp,
            ground: new Ground(Remote: true, Ci: true),
            machine: steps => steps.Queue("ci-wait", red, red, red, red, red, red, red, red));

        var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var diagnoses = kit.Agents.Of("diagnostician");

        Assert.True(diagnoses.Count == 5, Shown(kit));
        Assert.StartsWith("A step of this run failed, and nothing in the flow took the failure.", diagnoses[0].Prompt, StringComparison.Ordinal);
        Assert.All(diagnoses.Skip(1), diagnosis => Assert.StartsWith("What you decided last time did not put the failure", diagnosis.Prompt, StringComparison.Ordinal));

        var bound = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

        Assert.Equal("recoveryBound", bound.Kind);
        Assert.Contains("checks", bound.RaisedBy?.Step, StringComparison.Ordinal);
        Assert.Equal(["more", "direct", "abort"], bound.Options.Select(option => option.Answer));
        Assert.Equal(("given", "advisor"), (bound.Recommendation!.State, bound.Recommendation!.By));
    }

    /// <summary>
    /// A red verify raises the item's mender and nothing else: the builder's agent is resumed on the mend prompt, and
    /// the verify runs again in the same item - green the second time, the run going on to its fold and its end.
    /// </summary>
    [Fact]
    public async Task ARedVerify_RaisesItsMender_BackToVerify_GreenAgain()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            ground: new Ground(Legs: true),
            machine: steps => steps.Queue(
                "test",
                MachineSteps.Green(),
                MachineSteps.Red(HarnessExit.CommandFailed, "the report test fails on its frame"),
                MachineSteps.Green()));

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // The builder's agent was resumed once, on the mend prompt; the verify ran twice - red, then green.
        var implementer = kit.Agents.Of("implementer");

        Assert.Equal(2, implementer.Count);
        Assert.StartsWith("The engine built your work and ran its tests, and the result is red.", implementer[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("the report test fails on its frame", implementer[1].Prompt, StringComparison.Ordinal);

        var verify = kit.Steps.Of("test").Where(run => run.Step.Name == "verify").ToList();

        Assert.Equal(2, verify.Count);
        Assert.False(verify[0].Result.Passed);
        Assert.True(verify[1].Result.Passed);
        Assert.Empty(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);
    }

    /// <summary>
    /// A verify that stays red past its mender's three visits is left to the recovery: no fourth mend is raised, the
    /// diagnostician reads the failure, and what stands in its way is put to the person - answered retry, the verify
    /// runs again as it is and comes back green, the run reaching its end.
    /// </summary>
    [Fact]
    public async Task AVerifyRedPastItsMendersThreeVisits_ReachesThePersonByTheRecovery_AndIsRetriedGreen()
    {
        using var temp = new TempDirectory();
        var red = MachineSteps.Red(HarnessExit.CommandFailed, "the report test still fails on its frame");
        var kit = await PrepareAsync(
            temp,
            ground: new Ground(Legs: true),
            script: agents => agents.Queue(
                "diagnostician",
                Completed(Diagnosis(
                    course: "person",
                    summary: "The verify is still red after the mender ran three times",
                    choices: [Choice("Retry the verify", "the step runs again as it is")]))),
            machine: steps => steps.Queue("test", MachineSteps.Green(), red, red, red, red, MachineSteps.Green()));

        var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        // Three mends, no fourth: the builder's agent was resumed exactly three times on the mend prompt.
        var implementer = kit.Agents.Of("implementer");

        Assert.Equal(4, implementer.Count);
        Assert.All(implementer.Skip(1), mend => Assert.StartsWith("The engine built your work and ran its tests, and the result is red.", mend.Prompt, StringComparison.Ordinal));

        // The recovery's diagnostician read the red verify, and the person was asked.
        var diagnoses = kit.Agents.Of("diagnostician");

        Assert.True(diagnoses.Count == 1, Shown(kit));
        Assert.Contains("verify", diagnoses[0].Prompt, StringComparison.Ordinal);
        var direction = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

        Assert.True(direction.RaisedBy?.Step?.Contains("direction", StringComparison.Ordinal) == true, Shown(kit));
        Assert.Contains("retry", direction.Options.Select(option => option.Answer));
        Assert.Contains("The verify is still red after the mender ran three times", direction.ProblemShown!.Happened, StringComparison.Ordinal);

        var done = await AnswerAsync(kit, "retry");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");
        Assert.Equal(4, kit.Agents.Of("implementer").Count);
        Assert.Equal(5, kit.Steps.Of("test").Count(run => run.Step.Name == "verify"));
    }

    /// <summary>
    /// A verify whose inputs moved raises nothing and asks no builder: the retry table runs it again as the tree
    /// settles, and past its retries the step fails as what it is - no verdict of its own - which no handler takes and
    /// the recovery puts to the person.
    /// </summary>
    [Fact]
    public async Task AVerifyWhoseInputsMoved_RaisesNothing_AndIsAskedOfThePerson()
    {
        using var temp = new TempDirectory();
        var moved = MachineSteps.Moved("the leg's inputs moved while it ran");
        var kit = await PrepareAsync(
            temp,
            ground: new Ground(Legs: true),
            script: agents => agents.Queue(
                "diagnostician",
                Completed(Diagnosis(
                    course: "person",
                    summary: "The tree will not settle while the verify runs",
                    choices: [Choice("Retry the verify", "the step runs again once the tree settles")]))),
            machine: steps => steps.Queue("test", MachineSteps.Green(), moved, moved, moved, moved));

        var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        // No mend was raised: the builder's agent was asked once, for the item's implement alone.
        Assert.Single(kit.Agents.Of("implementer"));
        Assert.Equal(4, kit.Steps.Of("test").Count(run => run.Step.Name == "verify"));

        var diagnoses = kit.Agents.Of("diagnostician");

        Assert.True(diagnoses.Count == 1, Shown(kit));
        Assert.Contains("verify", diagnoses[0].Prompt, StringComparison.Ordinal);

        var asked = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

        Assert.True(asked.RaisedBy?.Step?.Contains("direction", StringComparison.Ordinal) == true, Shown(kit));
        Assert.Contains("The tree will not settle while the verify runs", asked.ProblemShown!.Happened, StringComparison.Ordinal);
    }

    /// <summary>
    /// A review that still finds a low finding after three rounds ends the loop: nobody is asked - the bound is
    /// accepted by the flow's own configuration, the answer recorded as the configuration's - and what the loop left
    /// is kept in its record, named in the run's report and given to the scribe that writes the handoff.
    /// </summary>
    [Fact]
    public async Task ALowFindingTheThirdRoundStillFinds_EndsTheLoop_Unasked_LeftInTheRecordAndGivenToTheScribe()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            script: agents => agents.Queue(
                "reviewer-correctness",
                Completed(Review(Finding(severity: "low"))),
                Completed(Review(Finding(severity: "low"))),
                Completed(Review(Finding(severity: "low")))));

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // Three rounds of review, each reworking the finding; the loop ended at its rounds with no question asked -
        // nothing blocking stands, so the bound is not reached and nobody is asked.
        Assert.Equal(3, kit.Agents.Of("reviewer-correctness").Count);
        Assert.Equal(4, kit.Agents.Of("implementer").Count);

        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

        Assert.Empty(questions.Open);
        Assert.DoesNotContain(questions.Questions, question => question.Kind == "loopBound");

        // What the loop left is kept in its record and given to the scribe.
        var fix = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps.Single(step => step.Leftover is not null);

        Assert.True(fix.Instance.Contains("fix", StringComparison.Ordinal), fix.Instance);
        Assert.Contains("The report ships without its frame", fix.Leftover!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("low", fix.Leftover!.ToJsonString(), StringComparison.Ordinal);
        var handoff = kit.Agents.Of("scribe").Single();

        Assert.Contains("The report ships without its frame", handoff.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A finding the loop counts - critical, high or medium, in production code or out of it - keeps the loop going
    /// past its three rounds to the further three its blocking holds for; at the sixth the configuration accepts the
    /// bound, and what is left is said in the record and to the scribe, which is told to file it in the run's backlog.
    /// </summary>
    [Fact]
    public async Task ABlockingFindingStandsPastThreeRoundsToSix_ThenTheBoundIsAcceptedByTheConfiguration()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            script: agents => agents.Queue(
                "reviewer-correctness",
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false)))));

        OrchestrationKit.Write(kit.Tree.Path, Path.Combine("docs", "backlog.md"), "# Backlog\n");
        await kit.Harness.CommitAllAsync(kit.Tree.Path, "the backlog the run files what it leaves in", Token);

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes", "backlog=docs/backlog.md");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

        // Six rounds, one rework each, the bound reached once - at the sixth, not asked of a person at the third.
        Assert.Equal(6, kit.Agents.Of("reviewer-correctness").Count);
        Assert.Equal(7, kit.Agents.Of("implementer").Count);

        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

        Assert.Empty(questions.Open);
        var bound = questions.Questions.Single(question => question.Kind == "loopBound");

        Assert.Contains("6 iterations", bound.ProblemShown!.Happened, StringComparison.Ordinal);
        Assert.Equal(("answered", "accept", "configuration"), (bound.State, bound.Answers.Single().Answer, bound.Answers.Single().By!.Who));

        var fix = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps.Single(step => step.Leftover is not null);

        Assert.Contains("The report ships without its frame", fix.Leftover!.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("high", fix.Leftover!.ToJsonString(), StringComparison.Ordinal);
        var handoff = kit.Agents.Of("scribe").Single();

        Assert.Contains("The report ships without its frame", handoff.Prompt, StringComparison.Ordinal);
        Assert.Contains("docs/backlog.md", handoff.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run given a plan: the items come from the file the person wrote, named to the planner and to the plan's
    /// auditor - and a red CI leg is not the run's work: the prioritizer puts it to a person as a stop, and the
    /// person's decision reaches the planner with it.
    /// </summary>
    [Fact]
    public async Task ARunGivenAPlan_ReadsItsItemsFromIt_AuditsIt_AndPutsARedCiLegToThePersonNotThePriority()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(temp, ground: new Ground(Remote: true, Ci: true));

        OrchestrationKit.Write(kit.Tree.Path, Path.Combine("docs", "plan.md"), "# Ship the orders report\n\nOne item: the report's code and its test.\n");
        await kit.Harness.CommitAllAsync(kit.Tree.Path, "a plan a person wrote", Token);

        kit.Steps.Queue(
            "ci-read",
            FlowStepResult.Ended(
                passed: true,
                HarnessExit.Success,
                output: new JsonObject
                {
                    ["passed"] = false,
                    ["branch"] = "trunk",
                    ["matrixRan"] = true,
                    ["legs"] = new JsonArray(),
                    ["red"] = new JsonArray(new JsonObject { ["name"] = "build-reports", ["state"] = "red", ["conclusion"] = "failure", ["url"] = "https://forge/checks/1" }),
                }));
        kit.Agents.Queue(
            "prioritizer",
            Completed(Priority(stops:
            [
                new JsonObject
                {
                    ["question"] = "Does the plan go ahead while build-reports is red on trunk?",
                    ["problem"] = "build-reports is red on trunk",
                    ["tried"] = "the CI read of trunk, and the plan's items against it",
                    ["means"] = "the plan's work would build beside a red leg",
                    ["options"] = new JsonArray("go ahead, and the plan's first item mends the leg", "wait for the leg to be green"),
                    ["recommended"] = "go ahead, and the plan's first item mends the leg",
                    ["why"] = "the bar prefers the work that mends the red",
                },
            ])));

        var paused = await StartAsync(kit, "plan=docs/plan.md", "preapproved=yes");

        // The prioritizer read the plan and the red leg, and chose the plan's work - the red is a stop, not the work.
        var priorityPrompt = kit.Agents.Of("prioritizer").Single().Prompt;

        Assert.Contains("docs/plan.md", priorityPrompt, StringComparison.Ordinal);
        Assert.True(priorityPrompt.Contains("build-reports", StringComparison.Ordinal), priorityPrompt);

        var asked = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

        Assert.Equal("Does the plan go ahead while build-reports is red on trunk?", Assert.Single(asked.Questions).Question);

        var done = await AnswerAsync(kit, "continue", "Go ahead; the plan's first item mends the leg.");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

        // The plan the person wrote was given to the planner and to its auditor, with the person's decision.
        Assert.Contains("docs/plan.md", kit.Agents.Of("planner").Single().Prompt, StringComparison.Ordinal);
        Assert.Contains("docs/plan.md", kit.Agents.Of("plan-auditor").Single().Prompt, StringComparison.Ordinal);
        Assert.Contains("Go ahead; the plan's first item mends the leg.", kit.Agents.Of("planner").Single().Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// A red baseline stops nothing: the step holds its red under failOn never, the prioritizer is told it in the
    /// prompt that chooses the work, its blocker reaches the planner, and the run builds its two items to its end.
    /// </summary>
    [Fact]
    public async Task ARedBaseline_IsToldToThePrioritiser_AsItsBlocker_AndStopsNothing()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            ground: new Ground(Legs: true),
            script: agents => agents.Queue("planner", Completed(Plan(One(), One(name: "two", owned: "src/two/**")))));

        kit.Steps.Queue("test", MachineSteps.RedHeld("the baseline test of the report is red"));
        kit.Agents.Queue("prioritizer", Completed(Priority(blockers: ["the baseline test of the report is red (the leg local, src/one/**)"])));

        var done = await StartAsync(kit, "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // The baseline held its red - no failure, no question - and the prioritizer and the planner were told it.
        var baseline = kit.Steps.Of("test").Single(run => run.Step.Name == "baseline");
        var priority = kit.Agents.Of("prioritizer").Single().Prompt;

        Assert.True(baseline.Result.Passed);
        Assert.True(priority.Contains("the baseline test of the report is red", StringComparison.Ordinal), priority);
        Assert.Contains("the baseline test of the report is red", kit.Agents.Of("planner").Single().Prompt, StringComparison.Ordinal);
        Assert.Equal(2, kit.Agents.Of("implementer").Count);
        Assert.Empty(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);
    }

    /// <summary>
    /// The scope a review's round runs behind omits the aspects the change has nothing of: their reviewers are skipped
    /// and start no agent, while the aspects it takes in are asked; a reviewer that answers that its aspect does not
    /// apply decides nothing - no finding is gathered, no rework is run.
    /// </summary>
    [Fact]
    public async Task TheScopeTheChangeNames_SkipsTheReviewersItOmits_AndAnAspectThatDoesNotApplyDecidesNothing()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue("review-scope", Completed(Scope(true, true, false, false, true)));
                agents.Queue("reviewer-tests", Completed(Review(applies: false)));
            });

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

        // The omitted aspects started no agent; the taken-in ones ran, tests answering that it does not apply.
        Assert.Empty(kit.Agents.Of("reviewer-types"));
        Assert.Empty(kit.Agents.Of("reviewer-comments"));
        Assert.Single(kit.Agents.Of("reviewer-errors"));
        Assert.Single(kit.Agents.Of("reviewer-tests"));
        Assert.Single(kit.Agents.Of("reviewer-simplicity"));

        // Nothing was found: the item's builder was asked once, for the implement alone.
        Assert.Single(kit.Agents.Of("implementer"));
        Assert.Empty(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);
    }

    /// <summary>
    /// Findings of two aspects reach one rework as one list: the builder's agent is resumed once, on a prompt that
    /// names each finding with the aspect that reported it.
    /// </summary>
    [Fact]
    public async Task FindingsOfTwoAspects_ReachOneReworkAsOneList_EachWithItsAspect()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue("review-scope", Completed(Scope(true, false, true, false, false)));
                agents.Queue("reviewer-errors", Completed(Review(Finding(aspect: "errors", text: "An error path ships each week of orders twice where the frame is late"))));
                agents.Queue("reviewer-types", Completed(Review(Finding(aspect: "types", text: "Each week of orders is read as a date and written as a string"))));
            });

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

        var rework = kit.Agents.Of("implementer")[1];

        Assert.StartsWith("Your work was folded and reviewed, and the review found something.", rework.Prompt, StringComparison.Ordinal);
        Assert.True(rework.Prompt.Contains("An error path ships each week of orders twice where the frame is late", StringComparison.Ordinal), rework.Prompt);
        Assert.Contains("Each week of orders is read as a date and written as a string", rework.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// An aspect's step taken out of the entry changes nothing else: the entry still validates - the run starts - and
    /// its review runs with the aspects the entry holds, the taken-out aspect starting no agent however the scope
    /// reads the change.
    /// </summary>
    [Fact]
    public async Task AnAspectStepTakenOutOfTheEntry_TheEntryStillValidates_AndTheRunGoesWithoutIt()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(temp);

        var workflow = Path.Combine(kit.Tree.Path, ".harness-config", "workflows", "implementation", "implementation.yml");
        var lines = File.ReadAllLines(workflow).ToList();
        var from = lines.FindIndex(line => line.Trim().Equals("- name: errors", StringComparison.Ordinal));
        var to = lines.FindIndex(from + 1, line => line.Trim().Equals("- name: tests", StringComparison.Ordinal));

        Assert.True(from >= 0 && to > from, $"{from} {to}");
        lines.RemoveRange(from, to - from);
        File.WriteAllLines(workflow, lines);
        await kit.Harness.CommitAllAsync(kit.Tree.Path, "the entry without the errors reviewer", Token);

        kit.Agents.Queue("review-scope", Completed(Scope(true, false, false, false, false)));

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");
        Assert.Empty(kit.Agents.Of("reviewer-errors"));
        Assert.Single(kit.Agents.Of("reviewer-correctness"));
        Assert.Single(kit.Agents.Of("implementer"));
    }

    /// <summary>
    /// The wait for the pull request's checks: the run reads the CI of the branch its pull request goes to - the base
    /// branch the orientation found, filled into the read's branch - and after the pull request it waits for the
    /// checks of the commit it pushed, its pending answered more twice by its own configuration and never asked; and
    /// the mutation sweep it runs between them sweeps only what no longer holds, as its pending says.
    /// </summary>
    [Fact]
    public async Task TheChecksOfThePullRequest_AreWaitedForOnThePushedCommit_PendingTwiceByTheConfiguration_TheSweepPending()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            ground: new Ground(Remote: true, Legs: true, Ci: true, Mutations: true),
            machine: steps => steps.Queue(
                "ci-wait",
                FlowStepResult.Found("pending", "2 checks still running on sha-pushed"),
                FlowStepResult.Found("pending", "2 checks still running on sha-pushed")));

        var end = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == end.ExitCode, $"{end.Message} | {string.Join(" | ", end.Details ?? [])}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // The CI read ran on the base branch the orientation found.
        Assert.Equal("trunk", kit.Steps.Of("ci-read").Single().Step.Branch);

        // The checks were waited for three times - twice pending, once green - the pending asked once each time and
        // answered by the configuration alone, never of a person.
        var waits = kit.Steps.Of("ci-wait");

        Assert.Equal(3, waits.Count);
        var pending = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Questions.Where(question => question.Kind == "checksPending").ToList();

        Assert.Equal(2, pending.Count);
        Assert.All(pending, question => Assert.Equal("answered", question.State));
        Assert.Equal(["more", "more"], pending.SelectMany(question => question.Answers.Select(answer => answer.Answer)));
        Assert.All(pending.SelectMany(question => question.Answers), answer => Assert.Equal("configuration", answer.By!.Who));
        Assert.Empty(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

        // The sweep swept, pending only, as its step says.
        Assert.True(kit.Steps.Of("check-mutations").Single().Step.Pending);
    }

    /// <summary>
    /// A question the engine raises is answered mid-run, while the run's own asking of an advisor still runs on beside
    /// it: a review loop at its bound in a copy of the entry that does not answer it is open at once with its
    /// recommendation sought - the advisor's run a branch the question does not stop - and answered before the advisor
    /// ends, which ends the advisor's asking; the loop takes the answer and the run goes on to its end, the
    /// recommendation said as cancelled by the answer that ended it.
    /// </summary>
    [Fact]
    public async Task AQuestionRaisedWhileTheAdvisorStillRunsOn_IsAnsweredMidRun_AndEndsTheAdvisor()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            script: agents => agents.Queue(
                "reviewer-correctness",
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false)))));

        await EditWorkflowAsync(kit, WithoutConfiguredLoopBounds);

        OrchestrationKit.Write(kit.Tree.Path, Path.Combine("docs", "backlog.md"), "# Backlog\n");
        await kit.Harness.CommitAllAsync(kit.Tree.Path, "the backlog the run files what it leaves in", Token);

        // The advisor, held mid-asking: a branch of the run still working while the question is open.
        var advising = kit.Agents.Hold("advisor");

        var run = StartAsync(kit, "request=Ship the orders report.", "preapproved=yes", "backlog=docs/backlog.md");

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())?.Open.Any(question => question.Kind == "loopBound") == true
                && kit.Agents.Of("advisor").Count > 0,
            () =>
            {
                var asked = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf());
                return $"{(run.IsCompletedSuccessfully ? $"{run.Result.ExitCode} {run.Result.Message}" : run.Status.ToString())}"
                    + $" | bound open: {asked?.Open.Any(question => question.Kind == "loopBound")} | advisor asked: {kit.Agents.Of("advisor").Count} | {Shown(kit)}";
            });

        var open = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open.Single(question => question.Kind == "loopBound");

        // The advisor was sought and is still running - held, its asking not ended: what the answer will end.
        Assert.Single(kit.Agents.Of("advisor"));

        _ = await AnswerOneAsync(kit, open.Id, "accept");

        var done = await run;

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // The question carries the person's answer, and the advisor's asking was ended by it - a recommendation that
        // never arrived, said as cancelled with the answer that ended it.
        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;
        var bound = questions.Questions.Single(question => question.Kind == "loopBound");

        Assert.Equal(("answered", "accept", "person"), (bound.State, bound.Answers.Single().Answer, bound.Answers.Single().By!.Who));
        Assert.Equal("cancelled", bound.Recommendation!.State);
        Assert.Contains("answered by a person while the advisor ran", bound.Recommendation.Why, StringComparison.Ordinal);

        // What the loop left was filed in the backlog, as the bound's answer says.
        var handoff = kit.Agents.Of("scribe").Single();

        Assert.Contains("The report ships without its frame", handoff.Prompt, StringComparison.Ordinal);
        Assert.Contains("docs/backlog.md", handoff.Prompt, StringComparison.Ordinal);
        Assert.Empty(questions.Open);
    }

    /// <summary>
    /// The advisor's recommendation arrives while the question is still open: it is in the record with who gave it, and
    /// in list-orchestrator's saying of the open question; an advisor that ends without an answer leaves the question
    /// saying one was sought and not obtained, and the bound is still asked of the person, who answers it; and an entry
    /// that names no advisor at all says none was sought.
    /// </summary>
    [Fact]
    public async Task TheAdvisorsRecommendationArrivesIntoTheOpenQuestion_AnAdvisorWithoutAnAnswerIsSoughtAndNotObtained_AndAnEntryThatNamesNone_SaysSo()
    {
        using var withAdvice = new TempDirectory();
        var kit = await PrepareAsync(
            withAdvice,
            script: agents => agents.Queue(
                "reviewer-correctness",
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false))),
                Completed(Review(Finding(severity: "high", production: false)))));

        await EditWorkflowAsync(kit, WithoutConfiguredLoopBounds);

        OrchestrationKit.Write(kit.Tree.Path, Path.Combine("docs", "backlog.md"), "# Backlog\n");
        await kit.Harness.CommitAllAsync(kit.Tree.Path, "the backlog the run files what it leaves in", Token);

        var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);
        var open = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open.Single(question => question.Kind == "loopBound");
        var given = open.Recommendation!;

        Assert.True(("given", "advisor", "more") == (given.State, given.By, given.Answer), $"{given.State}: {given.Why}");

        // The listing says the open question, with the recommendation in it.
        var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Tree.Path, "r1", steps: false, questions: true, json: false, Token);
        var lines = string.Join("\n", listed.Details ?? []);

        Assert.Contains($"open question {open.Id} (loopBound)", lines, StringComparison.Ordinal);
        Assert.Contains("recommended: 'more'", lines, StringComparison.Ordinal);

        var answered = await AnswerOneAsync(kit, open.Id, "accept");

        Assert.True(HarnessExit.Success == answered.ExitCode, $"{answered.Message} | {string.Join(" | ", answered.Details ?? [])} | {Shown(kit)}");

        // The report of the ended run holds the recommendation, with who gave it.
        var report = File.ReadAllText(kit.RunOf().ReportFile);

        Assert.Contains("recommended: 'more', the repository's bar decides it", report, StringComparison.Ordinal);

        // An advisor whose every attempt fails leaves the question saying one was sought and not obtained, and the
        // person is still asked.
        using var withoutAnAnswer = new TempDirectory();
        var other = await PrepareAsync(
            withoutAnAnswer,
            script: agents =>
            {
                agents.Queue(
                    "advisor",
                    [.. Enumerable.Repeat(new AgentRunResult(AgentRunStatus.Failed, Error: "the launch ran past its 00:10:00"), 48)]);
                agents.Queue(
                    "reviewer-correctness",
                    Completed(Review(Finding(severity: "high", production: false))),
                    Completed(Review(Finding(severity: "high", production: false))),
                    Completed(Review(Finding(severity: "high", production: false))),
                    Completed(Review(Finding(severity: "high", production: false))),
                    Completed(Review(Finding(severity: "high", production: false))),
                    Completed(Review(Finding(severity: "high", production: false))));
            });

        await EditWorkflowAsync(other, WithoutConfiguredLoopBounds);
        OrchestrationKit.Write(other.Tree.Path, Path.Combine("docs", "backlog.md"), "# Backlog\n");
        await other.Harness.CommitAllAsync(other.Tree.Path, "the backlog the run files what it leaves in", Token);

        var held = await StartAsync(other, "request=Ship the orders report.", "preapproved=yes");

        Assert.Equal(FlowExit.Paused, held.ExitCode);
        var notObtained = other.Harness.OrchestrationStore.ReadFlowQuestions(other.RunOf())!.Open.Single(question => question.Kind == "loopBound");

        Assert.Equal(("notObtained", null), (notObtained.Recommendation!.State, notObtained.Recommendation.Answer));
        Assert.Contains("one was sought and not obtained", notObtained.Recommendation.Why, StringComparison.Ordinal);
        Assert.Contains("the launch ran past its 00:10:00", notObtained.Recommendation.Why, StringComparison.Ordinal);

        _ = await AnswerOneAsync(other, notObtained.Id, "accept");

        Assert.Equal(nameof(FlowRunStatus.Done), other.Harness.OrchestrationStore.ReadFlow(other.RunOf())!.Status);

        // An entry that names no advisor says none was sought, and asks the person all the same.
        using var withoutAdvisor = new TempDirectory();
        var third = await PrepareAsync(withoutAdvisor);

        await EditWorkflowAsync(third, lines => lines.TakeWhile(line => !line.Equals("advisor:", StringComparison.Ordinal))
            .Concat(lines.SkipWhile(line => !line.Equals("reporter:", StringComparison.Ordinal))).ToArray());

        var asked = await StartAsync(third, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, asked.ExitCode);
        var approval = third.Harness.OrchestrationStore.ReadFlowQuestions(third.RunOf())!.Open.Single(question => question.RaisedBy?.Step?.EndsWith(".approval", StringComparison.Ordinal) == true);

        Assert.Equal(("notSought", null), (approval.Recommendation!.State, approval.Recommendation.Answer));
        Assert.Contains("orchestrator 'implementation' names no advisor", approval.Recommendation.Why, StringComparison.Ordinal);
        Assert.Empty(third.Agents.Of("advisor"));

        _ = await AnswerAsync(third, "approve");

        Assert.Equal(nameof(FlowRunStatus.Done), third.Harness.OrchestrationStore.ReadFlow(third.RunOf())!.Status);
    }

    /// <summary>
    /// The repository's bar for a recommendation is read as each question is raised, never kept by the run: the advisor
    /// of the first question is given the file as it stands, and one edited between two questions of one run - the plan
    /// sent back and approved again - reaches only the second, whose advisor follows the new text.
    /// </summary>
    [Fact]
    public async Task TheRepositorysBar_IsReadAsEachQuestionIsRaised_AnEditHoldingFromTheNext()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            script: agents => agents.Queue("planner", Completed(Plan(One())), Completed(Plan(One(), One(name: "two", owned: "src/two/**")))));

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);
        var first = kit.Agents.Of("advisor").Single();

        Assert.Contains("Recommend the solution that is best for the long term", first.Prompt, StringComparison.Ordinal);

        // With the first question still open, the repository's bar is edited: the run keeps no copy of it, and the
        // question the plan's return raises reads the file as it stands then.
        var bar = Path.Combine(kit.Tree.Path, ".harness-config", "workflows", "implementation", "recommendations.md");
        File.WriteAllLines(
            bar,
            [
                "# How recommendations are made in this repository",
                string.Empty,
                "## The bar",
                string.Empty,
                "1. Recommend nothing that ships without a frame; the frame is the repository's own.",
            ]);

        var revised = await AnswerAsync(kit, "revise", "Make it two items.");

        Assert.Equal(FlowExit.Paused, revised.ExitCode);

        var second = kit.Agents.Of("advisor")[1];

        Assert.Contains("Recommend nothing that ships without a frame", second.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Recommend the solution that is best for the long term", second.Prompt, StringComparison.Ordinal);

        _ = await AnswerAsync(kit, "approve");

        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);
    }

    /// <summary>
    /// A run with a budget in dollars counts each step's cost at the configuration's prices - its steps on the fallback
    /// model among them, every turn reported - and its report says the whole of it, of the budget; the same run with no
    /// budget counts what it can and says the rest is not known, naming the model and the keys to fill; and where the
    /// budget needs a price the configuration does not hold, the run is refused before it starts, naming them too.
    /// </summary>
    [Fact]
    public async Task TheUsdBudget_CountsEachStepsCostAtTheConfigurationsPrices_AndSaysWhatItCannotCount()
    {
        using var budgeted = new TempDirectory();
        var kit = await PrepareAsync(budgeted, keys: "ZAI_API_KEY=sk-test-two\n");

        kit.Agents.DefaultUsage = new AgentUsage(InputTokens: 1_000_000, OutputTokens: 500_000, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0);
        kit.Agents.ServingModel = "glm-5.3";

        await EditWorkflowAsync(
            kit,
            lines =>
            {
                var at = Array.IndexOf(lines, "answers:");
                return [.. lines[..at], "budget:", "  usd: 500", .. lines[at..]];
            });

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

        var spent = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Spent!;

        Assert.True(spent.Usd > 0, $"{spent.Usd}");
        Assert.Null(spent.Uncounted);

        var report = File.ReadAllLines(kit.RunOf().ReportFile).Single(line => line.Contains(" USD", StringComparison.Ordinal));

        Assert.StartsWith("- ", report, StringComparison.Ordinal);
        Assert.DoesNotContain("not known", report, StringComparison.Ordinal);

        // With the fallback's prices taken out, the run under the budget is refused before it starts, naming the model
        // and the keys to fill.
        using var priceless = new TempDirectory();
        var other = await PrepareAsync(priceless, keys: "ZAI_API_KEY=sk-test-two\n", adjust: WithoutGlmPrices);

        await EditWorkflowAsync(
            other,
            lines =>
            {
                var at = Array.IndexOf(lines, "answers:");
                return [.. lines[..at], "budget:", "  usd: 500", .. lines[at..]];
            });

        var refused = await StartAsync(other, "request=Ship the orders report.", "preapproved=yes");

        var said = $"{refused.Message} | {string.Join(" | ", refused.Details ?? [])}";

        Assert.Equal(HarnessExit.ConfigInvalid, refused.ExitCode);
        Assert.Contains("runs under a budget in dollars through client 'glm'", said, StringComparison.Ordinal);
        Assert.Contains("no price for 'glm-5.3' (providers 'zai', model 'glm-5.3'", said, StringComparison.Ordinal);
        Assert.Contains("and a budget is charged with the cost counted from them", said, StringComparison.Ordinal);

        // With no budget at all, the same unpriced model is worked and its cost is said as not known, the run going on.
        using var unbudgeted = new TempDirectory();
        var third = await PrepareAsync(unbudgeted, keys: "ZAI_API_KEY=sk-test-two\n", adjust: WithoutGlmPrices);

        third.Agents.DefaultUsage = new AgentUsage(InputTokens: 1_000_000, OutputTokens: 500_000, CacheReadTokens: 0, CacheWriteTokens: 0, CacheWrite1hTokens: 0, Searches: 0);
        third.Agents.ServingModel = "glm-5.3";

        var gone = await StartAsync(third, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == gone.ExitCode, $"{gone.Message} | {string.Join(" | ", gone.Details ?? [])} | {Shown(third)}");

        var unknown = third.Harness.OrchestrationStore.ReadFlow(third.RunOf())!.Spent!;

        Assert.Equal(0, unknown.Usd);
        Assert.Contains("no price for 'glm-5.3' (providers 'zai', model 'glm-5.3': priceInputMiss, priceOutput)", unknown.Uncounted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The independent audit of a plan: one that refuses - the item builds what the repository already has - loops the
    /// planning with the finding given to the planner, and the plan that passes is the one approved; and what the
    /// request itself asks to have built again is no duplicate: triage's list of it reaches the auditor, and the item
    /// that replaces exactly that passes the audit first time.
    /// </summary>
    [Fact]
    public async Task TheAuditThatRefuses_LoopsThePlanningWithTheFinding_AndWhatTheRequestAsksToHaveBuiltAgain_IsNoDuplicate()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue("planner", Completed(Plan(One())), Completed(Plan(One())));
                agents.Queue(
                    "plan-auditor",
                    Completed(Audit(go: false, "the item builds what the repository already has: the orders report is already src/report.cs")),
                    Completed(Audit()));
            });

        OrchestrationKit.Write(kit.Tree.Path, Path.Combine("src", "report.cs"), "namespace Report;\n");
        await kit.Harness.CommitAllAsync(kit.Tree.Path, "the report the repository already has", Token);

        var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

        var plans = kit.Agents.Of("planner");

        Assert.Equal(2, plans.Count);
        Assert.Contains("the item builds what the repository already has", plans[1].Prompt, StringComparison.Ordinal);

        // What the request asks to have built again is the auditor's own list: the item replacing exactly that is no
        // duplicate, and the audit passes first time.
        using var rebuilt = new TempDirectory();
        var other = await PrepareAsync(
            rebuilt,
            script: agents =>
            {
                agents.Queue(
                    "triage",
                    Completed(Triage(
                        rebuilds: [Rebuild("the orders report", "src/report.cs")],
                        sentence: "This plan is approved beforehand; build it without showing it for approval.")));
                agents.Queue("planner", Completed(Plan(One(replaces: "src/report.cs"))));
            });

        OrchestrationKit.Write(other.Tree.Path, Path.Combine("src", "report.cs"), "namespace Report;\n");
        await other.Harness.CommitAllAsync(other.Tree.Path, "the report the request asks to have built again", Token);

        var through = await StartAsync(other, "request=Ship the orders report again, as its own code. This plan is approved beforehand; build it without showing it for approval.");

        Assert.True(HarnessExit.Success == through.ExitCode, $"{through.Message} | {string.Join(" | ", through.Details ?? [])} | {Shown(other)}");

        var audit = other.Agents.Of("plan-auditor").Single();

        Assert.Contains("src/report.cs", audit.Prompt, StringComparison.Ordinal);
        Assert.Contains("the orders report", audit.Prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// An agent at its turn limit: the configuration answers retry twice and fail the third - each answer recorded with
    /// where it was configured - and the failed step is put to the person as a failure question that says what happened
    /// (the step reached its turns); answered retry, the step runs again and passes. A reasoning step is never
    /// recovered: no diagnostician is asked for it.
    /// </summary>
    [Fact]
    public async Task AnAgentAtItsTurnLimit_IsAnsweredByItsConfiguration_RetryTwiceThenFail_AndThePersonRetriesIt()
    {
        using var temp = new TempDirectory();
        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "implementer",
                    Completed(Implement()),
                    new AgentRunResult(AgentRunStatus.MaxTurns),
                    new AgentRunResult(AgentRunStatus.MaxTurns),
                    new AgentRunResult(AgentRunStatus.MaxTurns),
                    Completed(Implement()));
                agents.Queue("reviewer-correctness", Completed(Review(Finding())), Completed(Review()));
            });

        await EditWorkflowAsync(
            kit,
            lines =>
            {
                var list = lines.ToList();
                var at = list.FindIndex(line => line.Trim().Equals("turnLimit:", StringComparison.Ordinal));
                var times = list.FindIndex(at + 1, line => line.Trim().Equals("times: 2", StringComparison.Ordinal));

                return times >= 0 ? [.. list[..(times + 1)], "    then: fail", .. list[(times + 1)..]] : [.. list];
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        // The failed step is put to the person, its question saying what happened.
        var failure = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open.Single(question => question.Kind == "failure");

        Assert.Contains("reached its turns", failure.ProblemShown!.Happened, StringComparison.Ordinal);

        var retried = await AnswerOneAsync(kit, failure.Id, "retry");

        Assert.True(HarnessExit.Success == retried.ExitCode, $"{retried.Message} | {string.Join(" | ", retried.Details ?? [])} | {Shown(kit)}");
        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // The implementer ran the item, hit its limit three times, and passed on the person's retry.
        Assert.Equal(5, kit.Agents.Of("implementer").Count);

        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

        Assert.Empty(questions.Open);
        var limits = questions.Questions.Where(question => question.Kind == "turnLimit").ToList();

        Assert.Equal(["retry", "retry", "fail"], [.. limits.SelectMany(question => question.Answers).Select(answer => answer.Answer)]);
        Assert.All(
            limits.SelectMany(question => question.Answers),
            answer => Assert.Equal(("configuration", "the orchestrator's answers"), (answer.By!.Who, answer.By!.Where)));

        var answered = questions.Questions.Single(question => question.Id == failure.Id);

        Assert.Equal(("answered", "retry", "person"), (answered.State, answered.Answers.Single().Answer, answered.Answers.Single().By!.Who));

        // A reasoning step is never recovered: no diagnostician was asked for it.
        Assert.Empty(kit.Agents.Of("diagnostician"));

        // The report lists each answer with where it was configured.
        var report = File.ReadAllText(kit.RunOf().ReportFile);

        Assert.Contains("answered 'retry' by the configuration, the orchestrator's answers", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// A request amended while its round builds: nothing new starts and the items go on; the planner is given the
    /// request as it stood and the amendment in its own conversation, asks one question and is answered; the updated
    /// plan is checked, audited and approved by a person, and the item the update adds is built as one more work item
    /// of the round, to the run's end.
    /// </summary>
    [Fact]
    public async Task ARequestAmendedWhileItsRoundBuilds_NothingNewStarts_ThePlannerAskedAndAnswered_AndTheAddedItemBuiltInTheRound()
    {
        using var temp = new TempDirectory();
        Held building = null!;
        var taken = 0;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One(), One(name: "two", owned: "src/two/**"))),
                    Asked("Which view does the amendment mean?"),
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report and the invoices view.",
                            ["done"] = "the report ships; its view ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**"), One(name: "three", owned: "src/three/**", reuses: "src/tool.cs")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "three", owned: "src/three/**", reuses: "src/tool.cs"),
                                    ["changed"] = "the amendment adds the invoices view, as the item 'three'",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                building = agents.Hold("implementer", step => Interlocked.Increment(ref taken) <= 2);
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        // The round builds its two items, both builders held at their work.
        var drive = AnswerAsync(kit, "approve");

        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2,
            () => $"{Shown(kit)} | implementers asked: {kit.Agents.Of("implementer").Count}");

        // The amendment lands while both builders are at work.
        var amended = await AmendAsync(kit, "Also ship the invoices view.");

        Assert.True(amended.ExitCode == HarnessExit.Success, $"{amended.Message} | {Shown(kit)}");

        var update = await WhenOpenAsync(kit, question => question.Kind == "ask" && question.RaisedBy?.Step?.Contains(".plan-update", StringComparison.Ordinal) == true);

        // Nothing new started: the round's two builders are those the run asked, both still at their work.
        Assert.Equal(2, kit.Agents.Of("implementer").Count);

        // The planner was given the request as it stood and the amendment, in its own conversation.
        var resumed = kit.Agents.Of("planner")[^1];

        Assert.Contains("Ship the orders report", resumed.Prompt, StringComparison.Ordinal);
        Assert.Contains("Also ship the invoices view.", resumed.Prompt, StringComparison.Ordinal);

        // The builders answer, the planner's question is answered - its command's outcome says where the run then
        // stands, paused on the approval of the updated plan - and the updated plan is asked of the person.
        building.Open();

        _ = await AnswerOneAsync(kit, update.Id, "answer", "The monthly one.");

        var approval = await WhenOpenAsync(
            kit,
            question => question.Options.Take(2).Select(option => option.Answer).SequenceEqual(["approve", "revise"]),
            () => $"drive: {(drive.IsCompleted ? $"{drive.Status}" : "running")} | raw: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "questions.json"))} | {Shown(kit)}");

        // The person approves the updated plan. The drive ends paused on the approval; the answer then drives the
        // run itself - the added item is built as one more work item of the round, and the run ends done.
        _ = await drive;
        var approved = await AnswerOneAsync(kit, approval.Id, "approve");

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"approve said: {approved.ExitCode} {approved.Message} | updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

        // The item the update adds was built as one more work item of the round.
        Assert.Equal(3, kit.Agents.Of("implementer").Count);

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        Assert.Equal(3, flow.Steps.Count(step => step.Step.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done)));

        // The update's entry: done, its added piece placed, nobody told.
        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;
        var entry = Assert.Single(updates.Updates);

        Assert.Equal(("amendment", "done"), (entry.Cause, entry.State));
        Assert.Single(entry.Pieces!, piece => piece.Kind == "added" && piece.ItemName() == "three");
        Assert.Empty(entry.Told ?? []);

        // list-orchestrator says what runs next: nothing, the run ended done.
        var listed = await kit.Harness.OrchestratorService.ListAsync(kit.Tree.Path, "r1", steps: false, questions: false, json: false, Token);

        Assert.Contains("done", string.Join("\n", listed.Details ?? []), StringComparison.Ordinal);
    }

    /// <summary>
    /// The item an update changes, its implementer at work: that step is ended in order, with a handoff, and the item
    /// is done again by redo in the same worktree, then tested, folded, committed and reviewed, to the run's end.
    /// </summary>
    [Fact]
    public async Task TheItemAnUpdateChanges_ItsImplementerAtWorkIsEndedInOrderWithAHandoff_AndRedoDoesItAgainInTheSameWorktree()
    {
        using var temp = new TempDirectory();
        Held building = null!;
        var taken = 0;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One(), One(name: "two", owned: "src/two/**"))),
                    Asked("Which totals does the change mean?"),
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report and its totals.",
                            ["done"] = "the report ships; its totals ship; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "changed",
                                    ["item"] = One(name: "two", owned: "src/two/**"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment makes the report total each region",
                                    ["keep"] = "the files the item already wrote",
                                    ["undo"] = "the view that holds no totals",
                                    ["instead"] = "the view with its totals of each region",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                building = agents.Hold("implementer", step => Interlocked.Increment(ref taken) <= 2);
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        // The round builds its two items, both builders held at their work.
        var drive = AnswerAsync(kit, "approve");

        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2,
            () => $"{Shown(kit)} | implementers asked: {kit.Agents.Of("implementer").Count}");

        // The change lands while both builders are at work.
        var amended = await AmendAsync(kit, "Also make the report total each region.");

        Assert.True(amended.ExitCode == HarnessExit.Success, $"{amended.Message} | {Shown(kit)}");

        var update = await WhenOpenAsync(kit, question => question.Kind == "ask" && question.RaisedBy?.Step?.Contains(".plan-update", StringComparison.Ordinal) == true);

        // Nothing new started: the round's two builders are those the run asked, both still at their work.
        Assert.Equal(2, kit.Agents.Of("implementer").Count);

        _ = await AnswerOneAsync(kit, update.Id, "answer", "The weekly one.");

        var approval = await WhenOpenAsync(
            kit,
            question => question.Options.Take(2).Select(option => option.Answer).SequenceEqual(["approve", "revise"]),
            () => $"drive: {(drive.IsCompleted ? $"{drive.Status}" : "running")} | raw: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "questions.json"))} | {Shown(kit)}");

        // The person approves the updated plan while both builders are still at work - the drive session itself goes
        // on waiting on them. The change names the item whose builder is at work: that step is ended in order -
        // stopped, ended for the update, its handoff written - and the item is done again by redo in the same
        // worktree, the agent its round made taken and not made again.
        var approving = AnswerOneAsync(kit, approval.Id, "approve");

        FlowStepRecord? ended = null;
        AgentStepRequest? again = null;

        await WhenAsync(
            () =>
            {
                ended = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Steps.FirstOrDefault(step => step.Step.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Stopped));
                again = kit.Agents.Of("implementer").FirstOrDefault(step => step.Prompt.Contains("the view that holds no totals", StringComparison.Ordinal));
                return ended is not null && again is not null;
            },
            () => $"updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        Assert.Contains("ended for update", ended!.Error, StringComparison.Ordinal);
        Assert.Contains("done again", ended.Error, StringComparison.Ordinal);
        Assert.NotNull(ended.Handoff);

        // The visit that does the item again runs in the add step's name, whatever declaration carries it: it is told
        // the piece - what is taken back, and what is done in its place - and it works the worktree the ended step
        // worked, for the agent the round made is taken and not made again.
        var was = kit.Agents.Of("implementer").First(step => step.StepInstance == ended.Instance);

        Assert.Contains("the view with its totals of each region", again!.Prompt, StringComparison.Ordinal);
        Assert.Equal(was.Workspace.Worktree, again.Workspace.Worktree);
        Assert.NotNull(again.Workspace.Worktree);

        // The builders answer - the ended one's hold is over, and the other goes on with its item - and the answer,
        // once given, carries the run to its end: the item done again is tested, folded, committed and reviewed as
        // any item of the round is.
        building.Open();

        var approved = await approving;

        _ = await drive;

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"approve said: {approved.ExitCode} {approved.Message} | updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        // One implement done, one stopped ended for the update, and the visit that does the item again - in the add
        // step's name - done; and the item done again went through the same round any item does: its tests, its fold,
        // its checkpoint and its review.
        var implements = flow.Steps.Where(step => step.Step.EndsWith(".implement", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, implements.Count);
        Assert.Equal(1, implements.Count(step => step.State == nameof(FlowInstanceState.Done)));

        // The visit that does the item again is the redo declaration's, run in the add step's name: its record says
        // what ran, and its instance is the place it holds in the round.
        var carried = again!.StepInstance.Split('/')[0];
        var scope = string.Join('.', carried.Split('.')[..^1]);
        var carrier = Assert.Single(flow.Steps, step => step.Instance == carried);

        Assert.Equal(("rounds.items.redo", nameof(FlowInstanceState.Done)), (carrier.Step, carrier.State));

        // The item done again went through the same round any item does - its tests drawn as the workflow directs
        // them (skipped where the tree holds no legs, as every item's are), its fold, its checkpoint and its review.
        Assert.Contains(flow.Steps, step => step.Instance == $"{scope}.verify");

        foreach (var name in new[] { ".fold", ".checkpoint", ".fix" })
        {
            Assert.Contains(flow.Steps, step => step.Instance == $"{scope}{name}" && step.State == nameof(FlowInstanceState.Done));
        }

        // The update's entry: done, its changed piece placed, nobody told.
        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;
        var entry = Assert.Single(updates.Updates);

        Assert.Equal(("amendment", "done"), (entry.Cause, entry.State));
        Assert.Single(entry.Pieces!, piece => piece.Kind == "changed" && piece.ItemName() == "two");
        Assert.Empty(entry.Told ?? []);
    }

    /// <summary>
    /// The item an update withdraws, its work folded and committed: it is taken back by undo in a new agent, from
    /// what its fold wrote, and the removal is folded, committed and reviewed, to the run's end.
    /// </summary>
    [Fact]
    public async Task TheItemAnUpdateWithdraws_FoldedAndCommitted_IsTakenBackByUndoInANewAgent_AndTheRemovalFoldedCommittedAndReviewed()
    {
        using var temp = new TempDirectory();
        Held review = null!;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One(name: "two", owned: "src/two/**"), One())),
                    Asked("What does taking the item back mean?"),
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "One item ships the report.",
                            ["done"] = "the report ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One()),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "removed",
                                    ["item"] = One(name: "two", owned: "src/two/**"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment withdraws the second item",
                                    ["keep"] = "",
                                    ["undo"] = "the report's second view, folded and committed",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                // The round's self-audit is held mid-flight, both items' work - the withdrawn one's among it -
                // folded, committed and released whole before it: the round works on while the withdrawn item stands
                // as the update finds it, its work in the main tree and its agent gone.
                review = agents.Hold("auditor");
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var drive = AnswerAsync(kit, "approve");

        // The withdrawn item's work is folded, committed and released, and the round sits in its self-audit.
        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Steps is { } steps
                && steps.Any(step => step.Instance == "rounds#1.items[0].release" && step.State == nameof(FlowInstanceState.Done))
                && kit.Agents.Of("auditor").Count >= 1,
            () => $"{Shown(kit)}");

        var amended = await AmendAsync(kit, "In fact, ship the report alone.");

        Assert.True(amended.ExitCode == HarnessExit.Success, $"{amended.Message} | {Shown(kit)}");

        var update = await WhenOpenAsync(kit, question => question.Kind == "ask" && question.RaisedBy?.Step?.Contains(".plan-update", StringComparison.Ordinal) == true);

        // Nothing new started: the round's two builders are those the run asked, both their items built.
        Assert.Equal(2, kit.Agents.Of("implementer").Count);

        _ = await AnswerOneAsync(kit, update.Id, "answer", "The weekly one.");

        var approval = await WhenOpenAsync(
            kit,
            question => question.Options.Take(2).Select(option => option.Answer).SequenceEqual(["approve", "revise"]),
            () => $"drive: {(drive.IsCompleted ? $"{drive.Status}" : "running")} | raw: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "questions.json"))} | {Shown(kit)}");

        // The person approves the updated plan. The withdrawal names the item whose work was folded and committed:
        // it is taken back by undo, in a new agent, from what its fold wrote - the visit runs in the add step's name,
        // whatever declaration carries it.
        // The audit answers - the new agent the taking-back works in is made over the main tree, which no reader may
        // hold while it is - and the answer, once given, carries the run to its end: the removal is folded, committed
        // and reviewed as any item's work is.
        review.Open();

        var approving = AnswerOneAsync(kit, approval.Id, "approve");

        var approved = await approving;

        FlowStepRecord? undone = null;
        AgentStepRequest? takingBack = null;

        await WhenAsync(
            () =>
            {
                var steps = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Steps;
                undone = steps?.FirstOrDefault(step => step.Step.EndsWith(".undo", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done));
                takingBack = kit.Agents.Of("implementer").FirstOrDefault(step => step.Prompt.Contains("the report's second view", StringComparison.Ordinal));
                return undone is not null && takingBack is not null;
            },
            () => $"approve said: {approved.ExitCode} {approved.Message} | updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        // The new agent: the visit that takes the item back works a worktree of its own, not the one the withdrawn
        // item's builder worked.
        var was = kit.Agents.Of("implementer").First(step => step.StepInstance == "rounds#1.items[0].implement");

        Assert.NotEqual(was.Workspace.Worktree, takingBack!.Workspace.Worktree);

        _ = await drive;

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"approve said: {approved.ExitCode} {approved.Message} | updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        // The visit that takes the item back is the undo declaration's, run in the add step's name, and the removal
        // went through the same round any item's work does: its fold, its checkpoint and its review.
        Assert.Equal(("rounds.items.undo", nameof(FlowInstanceState.Done)), (undone!.Step, undone.State));

        var scope = string.Join('.', undone.Instance.Split('.')[..^1]);

        Assert.Contains(flow.Steps, step => step.Instance == $"{scope}.verify");

        foreach (var name in new[] { ".fold", ".checkpoint", ".fix" })
        {
            Assert.Contains(flow.Steps, step => step.Instance == $"{scope}{name}" && step.State == nameof(FlowInstanceState.Done));
        }

        // The update's entry: done, its removed piece placed, nobody told.
        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;
        var entry = Assert.Single(updates.Updates);

        Assert.Equal(("amendment", "done"), (entry.Cause, entry.State));
        Assert.Single(entry.Pieces!, piece => piece.Kind == "removed" && piece.ItemName() == "two");
        Assert.Empty(entry.Told ?? []);
    }

    /// <summary>
    /// A request amended to tell work under way: the step the update names is told its piece and goes on, an agent no
    /// piece names is sent nothing, nothing is built again, and the run ends done with its two items as they stood.
    /// </summary>
    [Fact]
    public async Task ARequestAmendedToTellWorkUnderWay_TheStepItNamesIsToldAndGoesOn_AndAnAgentNoPieceNamesIsSentNothing()
    {
        using var temp = new TempDirectory();
        Held building = null!;
        var taken = 0;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One(), One(name: "two", owned: "src/two/**"))),
                    Asked("What does the telling mean?"),
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report.",
                            ["done"] = "the report ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "told",
                                    ["item"] = null,
                                    ["for"] = "rounds#1.items[0]",
                                    ["changed"] = "the report is the monthly one from now on",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                building = agents.Hold("implementer", step => Interlocked.Increment(ref taken) <= 2);
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        // The round builds its two items, both builders held at their work.
        var drive = AnswerAsync(kit, "approve");

        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2,
            () => $"{Shown(kit)} | implementers asked: {kit.Agents.Of("implementer").Count}");

        var amended = await AmendAsync(kit, "The report is the monthly one from now on.");

        Assert.True(amended.ExitCode == HarnessExit.Success, $"{amended.Message} | {Shown(kit)}");

        var update = await WhenOpenAsync(kit, question => question.Kind == "ask" && question.RaisedBy?.Step?.Contains(".plan-update", StringComparison.Ordinal) == true);

        // Nothing new started: the round's two builders are those the run asked, both still at their work.
        Assert.Equal(2, kit.Agents.Of("implementer").Count);

        _ = await AnswerOneAsync(kit, update.Id, "answer", "The weekly one.");

        var approval = await WhenOpenAsync(
            kit,
            question => question.Options.Take(2).Select(option => option.Answer).SequenceEqual(["approve", "revise"]),
            () => $"drive: {(drive.IsCompleted ? $"{drive.Status}" : "running")} | raw: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "questions.json"))} | {Shown(kit)}");

        // The person approves the told plan while both builders are still at work: the piece names one of them - it
        // is told, and it goes on - and the other is sent nothing. Nothing is built again.
        var approving = AnswerOneAsync(kit, approval.Id, "approve");

        var approved = await approving;

        await WhenAsync(
            () => (kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())?.Updates ?? []).FirstOrDefault() is { State: "done" } told && told.Told is { Count: 1 },
            () => $"approve said: {approved.ExitCode} {approved.Message} | updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        var told = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;

        Assert.Empty(told.Updates[0].NotDelivered ?? []);
        Assert.Equal("rounds#1.items[0].implement", told.Updates[0].Told![0].Who);
        Assert.Equal("by a message into its conversation", told.Updates[0].Told![0].How);
        Assert.Equal(["rounds#1.items[1].implement"], told.Updates[0].NotConcerned);

        // The builders answer - the told one went on with its work, ended done as any item's builder does - and the
        // run ends done with its two items as they stood: no item built again, nothing taken back.
        building.Open();

        _ = await drive;

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"{Shown(kit)}");

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        Assert.Equal(2, kit.Agents.Of("implementer").Count);
        Assert.Equal(2, flow.Steps.Count(step => step.Step.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done)));
        Assert.DoesNotContain(flow.Steps, step => (step.Step.EndsWith(".redo", StringComparison.Ordinal) || step.Step.EndsWith(".undo", StringComparison.Ordinal)) && step.State != nameof(FlowInstanceState.Skipped));

        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;
        var entry = Assert.Single(updates.Updates);

        Assert.Equal(("amendment", "done"), (entry.Cause, entry.State));
        Assert.Null(entry.Pieces!.Single(piece => piece.Kind == "told").Item);
        Assert.Equal("rounds#1.items[0]", entry.Pieces!.Single().For);
    }

    /// <summary>
    /// The same amendment in a run the person approved beforehand by its input: nobody is asked to approve - not the
    /// plan the run began from, not the plan the update stands - and the item the update adds is built as one more
    /// work item of the round, to the run's end.
    /// </summary>
    [Fact]
    public async Task TheSameAmendedRunStartedWithTheInputPreapproved_NobodyIsAskedToApprove_AndTheAddedItemIsBuilt()
    {
        using var temp = new TempDirectory();
        Held building = null!;
        var taken = 0;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One(), One(name: "two", owned: "src/two/**"))),
                    Asked("Which view does the amendment mean?"),
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report and the invoices view.",
                            ["done"] = "the report ships; its view ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**"), One(name: "three", owned: "src/three/**", reuses: "src/tool.cs")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "three", owned: "src/three/**", reuses: "src/tool.cs"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment adds the invoices view, as the item 'three'",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                building = agents.Hold("implementer", step => Interlocked.Increment(ref taken) <= 2);
            });

        // The run, approved beforehand by its input, drives itself: it pauses only on the planner's question.
        var run = StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2,
            () => $"{Shown(kit)} | implementers asked: {kit.Agents.Of("implementer").Count}");

        var amended = await AmendAsync(kit, "Also ship the invoices view.");

        Assert.True(amended.ExitCode == HarnessExit.Success, $"{amended.Message} | {Shown(kit)}");

        var update = await WhenOpenAsync(kit, question => question.Kind == "ask" && question.RaisedBy?.Step?.Contains(".plan-update", StringComparison.Ordinal) == true);

        Assert.Equal(2, kit.Agents.Of("implementer").Count);

        // The builders answer, the planner's question is answered - and nobody is asked to approve the updated plan:
        // the input that approved the run's plan approves it, and the answer's command carries the run to its end.
        building.Open();

        var answered = await AnswerOneAsync(kit, update.Id, "answer", "The monthly one.");

        Assert.True(answered.ExitCode == HarnessExit.Success, $"{answered.ExitCode} {answered.Message} | {Shown(kit)}");

        _ = await run;

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"{Shown(kit)}");

        // Nobody was asked to approve: both the run's plan and the update's were approved by the input alone.
        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

        Assert.Empty(questions.Open);
        Assert.All(questions.Questions.Where(question => question.RaisedBy?.Step?.EndsWith(".approval", StringComparison.Ordinal) == true), question => Assert.Equal(("answered", "input"), (question.State, question.Answers.Single().By!.Who)));

        // The item the update adds was built as one more work item of the round.
        Assert.Equal(3, kit.Agents.Of("implementer").Count);

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        Assert.Equal(3, flow.Steps.Count(step => step.Step.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done)));

        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;
        var entry = Assert.Single(updates.Updates);

        Assert.Equal(("amendment", "done"), (entry.Cause, entry.State));
        Assert.Single(entry.Pieces!, piece => piece.Kind == "added" && piece.ItemName() == "three");
    }

    /// <summary>
    /// A second amendment made while the first is under way waits its turn - queued, nothing raised - and when the
    /// first stands, the planner is given the request as it now stands with both amendments and asks in its own
    /// conversation; both are approved, and both items are built, to the run's end.
    /// </summary>
    [Fact]
    public async Task ASecondAmendmentMadeWhileTheFirstIsUnderWay_WaitsItsTurn_AndBothArePlannedApprovedAndBuilt()
    {
        using var temp = new TempDirectory();
        Held building = null!;
        var taken = 0;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One(), One(name: "two", owned: "src/two/**"))),
                    Asked("Which view does the amendment mean?"),
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report and the invoices view.",
                            ["done"] = "the report ships; its view ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**"), One(name: "three", owned: "src/three/**", reuses: "src/tool.cs")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "three", owned: "src/three/**", reuses: "src/tool.cs"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment adds the invoices view, as the item 'three'",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }),
                    Asked("What does taking the second item back mean?"),
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report and the invoices view.",
                            ["done"] = "the report ships; its view ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "three", owned: "src/three/**", reuses: "src/tool.cs")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "removed",
                                    ["item"] = One(name: "two", owned: "src/two/**"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment withdraws the report's second view",
                                    ["keep"] = "",
                                    ["undo"] = "the report's second view, folded and committed",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                building = agents.Hold("implementer", step => Interlocked.Increment(ref taken) <= 2);
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var drive = AnswerAsync(kit, "approve");

        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2,
            () => $"{Shown(kit)} | implementers asked: {kit.Agents.Of("implementer").Count}");

        // The first amendment lands, its update under way - and the second, made meanwhile, waits its turn.
        var first = await AmendAsync(kit, "Also ship the invoices view.");

        Assert.True(first.ExitCode == HarnessExit.Success, $"{first.Message} | {Shown(kit)}");

        var update = await WhenOpenAsync(kit, question => question.Kind == "ask" && question.RaisedBy?.Step?.Contains(".plan-update", StringComparison.Ordinal) == true);

        var second = await AmendAsync(kit, "In fact, drop the second view.");

        Assert.True(second.ExitCode == HarnessExit.Success, $"{second.Message} | {Shown(kit)}");

        building.Open();

        _ = await AnswerOneAsync(kit, update.Id, "answer", "The monthly one.");

        // The engine takes the second amendment while the first is still short of the person's approval, and it waits
        // its turn: queued, nothing raised, no planner asked.
        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())?.Updates is [{ } one, { } two],
            () => $"updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        var waiting = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;

        Assert.Equal(("amendment", "underWay"), (waiting.Updates[0].Cause, waiting.Updates[0].State));
        Assert.Equal(("amendment", "queued"), (waiting.Updates[1].Cause, waiting.Updates[1].State));
        Assert.Equal("In fact, drop the second view.", waiting.Updates[1].Amendment);

        var approval = await WhenOpenAsync(
            kit,
            question => question.Options.Take(2).Select(option => option.Answer).SequenceEqual(["approve", "revise"]),
            () => $"drive: {(drive.IsCompleted ? $"{drive.Status}" : "running")} | raw: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "questions.json"))} | {Shown(kit)}");

        _ = await drive;

        var approving = await AnswerOneAsync(kit, approval.Id, "approve");

        Assert.True(approving.ExitCode is HarnessExit.Success or 1, $"{approving.ExitCode} {approving.Message} | {Shown(kit)}");

        // The first stands, and the second takes its turn: the planner is asked in its own conversation, given the
        // request as it now stands - the original, the first amendment, and the second.
        var secondUpdate = await WhenOpenAsync(
            kit,
            question => question.Kind == "ask" && question.RaisedBy?.Step?.Contains(".plan-update", StringComparison.Ordinal) == true,
            () => $"updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        var resumed = kit.Agents.Of("planner")[^1];

        Assert.Contains("Ship the orders report", resumed.Prompt, StringComparison.Ordinal);
        Assert.Contains("Also ship the invoices view.", resumed.Prompt, StringComparison.Ordinal);
        Assert.Contains("In fact, drop the second view.", resumed.Prompt, StringComparison.Ordinal);

        await AnswerTakenAsync(kit, secondUpdate.Id, "answer", "Taken back whole.");

        var secondApproval = await WhenOpenAsync(
            kit,
            question => question.Options.Take(2).Select(option => option.Answer).SequenceEqual(["approve", "revise"]),
            () => $"raw: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "questions.json"))} | {Shown(kit)}");

        var secondApproving = await AnswerOneAsync(kit, secondApproval.Id, "approve");

        Assert.True(secondApproving.ExitCode is HarnessExit.Success or 1, $"{secondApproving.ExitCode} {secondApproving.Message} | {Shown(kit)}");

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        // Both amendments stand: the first's item was built as one more work item of the round, the second's
        // withdrawal was taken back by undo in a new agent, and the run ended done with the plan it stood.
        Assert.Equal(4, kit.Agents.Of("implementer").Count);

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        Assert.Equal(3, flow.Steps.Count(step => step.Step.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done)));
        Assert.Contains(flow.Steps, step => step.Step.EndsWith(".undo", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done));

        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;

        Assert.Equal(2, updates.Updates.Count);
        Assert.All(updates.Updates, entry => Assert.Equal(("amendment", "done"), (entry.Cause, entry.State)));
        Assert.Single(updates.Updates[0].Pieces!, piece => piece.Kind == "added" && piece.ItemName() == "three");
        Assert.Single(updates.Updates[1].Pieces!, piece => piece.Kind == "removed" && piece.ItemName() == "two");
    }

    /// <summary>
    /// An amendment that adds an item whose paths an item of the running round owns: the check of the updated plan
    /// returns it as a problem, nobody is asked to approve it, the planner settles it - resumed in its own
    /// conversation with the problem - and the person is asked only over the settled plan, whose item builds in the
    /// round under paths of its own, to the run's end.
    /// </summary>
    [Fact]
    public async Task AnAmendmentAddsAnItemWhosePathsARunningItemOwns_TheCheckReturnsTheProblem_ThePlannerSettlesIt_AndOnlyTheSettledPlanIsApproved()
    {
        using var temp = new TempDirectory();
        Held building = null!;
        var taken = 0;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One(), One(name: "two", owned: "src/two/**"))),
                    Asked("Which view does the amendment mean?"),
                    // What the planner first returns: the invoices view as an item that owns the paths the running
                    // item 'one' owns - the check of the updated plan returns that as a problem.
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Three items ship the report, the invoices view beside it.",
                            ["done"] = "the report ships; its view ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**"), One(name: "three", owned: "src/one/**", reuses: "src/tool.cs")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "three", owned: "src/one/**", reuses: "src/tool.cs"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment adds the invoices view, as the item 'three'",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }),
                    // What it returns once the check's problem is put to it: the same item, under paths of its own.
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Three items ship the report, the invoices view under paths of its own.",
                            ["done"] = "the report ships; its view ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**"), One(name: "three", owned: "src/three/**", reuses: "src/tool.cs")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "three", owned: "src/three/**", reuses: "src/tool.cs"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment adds the invoices view, as the item 'three', under paths of its own",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                building = agents.Hold("implementer", step => Interlocked.Increment(ref taken) <= 2);
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var drive = AnswerAsync(kit, "approve");

        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2,
            () => $"{Shown(kit)} | implementers asked: {kit.Agents.Of("implementer").Count}");

        // The amendment lands while both builders are at work: it adds an item whose paths item 'one' owns.
        var amended = await AmendAsync(kit, "Also ship the invoices view.");

        Assert.True(amended.ExitCode == HarnessExit.Success, $"{amended.Message} | {Shown(kit)}");

        var update = await WhenOpenAsync(kit, question => question.Kind == "ask" && question.RaisedBy?.Step?.Contains(".plan-update", StringComparison.Ordinal) == true);

        // The builders answer and the planner's question is answered: the check of the updated plan returns the
        // ownership problem, and with it standing nobody is asked to approve - the planner is resumed with the
        // problem instead, in its own conversation, to settle it.
        building.Open();

        _ = await AnswerOneAsync(kit, update.Id, "answer", "The monthly one.");

        await WhenAsync(
            () => kit.Agents.Of("planner").Any(visit => visit.Prompt.Contains("no file is written by two items", StringComparison.Ordinal)),
            () => $"{Shown(kit)} | planner visits: {kit.Agents.Of("planner").Count}");

        // The planner settles: its resumed conversation was given the problem the check returned and the amendment.
        var settled = Assert.Single(kit.Agents.Of("planner"), visit => visit.Prompt.Contains("no file is written by two items", StringComparison.Ordinal));

        Assert.Contains("Also ship the invoices view.", settled.Prompt, StringComparison.Ordinal);

        // Only the settled plan is put to the person: the approval is raised in the planning the settling made, and
        // its question holds the settled plan, not the one the check refused.
        var approval = await WhenOpenAsync(
            kit,
            question => question.Options.Take(2).Select(option => option.Answer).SequenceEqual(["approve", "revise"]),
            () => $"drive: {(drive.IsCompleted ? $"{drive.Status}" : "running")} | raw: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "questions.json"))} | {Shown(kit)}");

        Assert.EndsWith("#2.approval", approval.RaisedBy?.Step, StringComparison.Ordinal);
        Assert.Contains("under paths of its own", approval.ProblemShown!.Happened, StringComparison.Ordinal);

        _ = await drive;

        var approved = await AnswerOneAsync(kit, approval.Id, "approve");

        // The answer, once given, carries the run to its end: the settled item is built as one more work item of the
        // round, and nothing of the offending plan is placed anywhere.
        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"approve said: {approved.ExitCode} {approved.Message} | updates: {File.ReadAllText(Path.Combine(kit.RunOf().Directory, "updates.json"))} | {Shown(kit)}");

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        // The check's problem, as the run's own record holds it: the added item owned the running item's paths.
        var refused = Assert.Single(
            flow.Steps,
            step => step.Step.EndsWith(".check", StringComparison.Ordinal) && step.Output?["ok"]?.GetValue<bool>() == false);

        Assert.Contains("work items 'one' and 'three' both own", string.Join("\n", (refused.Output!["problems"] as JsonArray)!.Select(problem => (string)problem!)), StringComparison.Ordinal);

        // The settled item was built: three builders asked, three implements done.
        Assert.Equal(3, kit.Agents.Of("implementer").Count);

        Assert.Equal(3, flow.Steps.Count(step => step.Step.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done)));

        // The update's entry: done, and the piece it placed is the settled item, under paths of its own - never the
        // one the check refused.
        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;
        var entry = Assert.Single(updates.Updates);

        Assert.Equal(("amendment", "done"), (entry.Cause, entry.State));

        var added = Assert.Single(entry.Pieces!, piece => piece.Kind == "added");

        Assert.Equal("three", added.ItemName());
        Assert.Equal(["src/three/**"], [.. ((JsonArray)added.Item!["ownedPaths"]!).Select(paths => (string)paths!)]);
    }

    [Fact]
    public async Task AnAmendmentAddsAnItemTheNextRoundsOwnItemSharesAFileWith_TheCheckThatOpensTheRoundRaisesAPlanUpdate_Waits_AndOnlyTheSettledRoundBuilds()
    {
        using var temp = new TempDirectory();
        Held auditing = null!;
        var audits = 0;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One(), One(name: "two", owned: "src/two/**"))),
                    // What it returns for the amendment: the whole plan again, with the invoices view as an item of
                    // its own - checked with the round's items, approved, and owed to the round that opens next.
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Three items ship the report, the invoices view beside it.",
                            ["done"] = "the report ships; its view ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**"), One(name: "four", owned: "src/shared/**", reuses: "src/tool.cs")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "four", owned: "src/shared/**", reuses: "src/tool.cs"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment adds the invoices view, as the item 'four'",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }),
                    // What it returns for the plan check that raised the update: the same item, under paths the
                    // round's own item does not share.
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Three items ship the report, the invoices view under paths of its own.",
                            ["done"] = "the report ships; its view ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**"), One(name: "four", owned: "src/four/**", reuses: "src/tool.cs")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "changed",
                                    ["item"] = One(name: "four", owned: "src/four/**", reuses: "src/tool.cs"),
                                    ["for"] = "",
                                    ["changed"] = "the round's own item 'three' and 'four' shared a file: 'four' moves to paths of its own",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                auditing = agents.Hold("auditor", _ => Interlocked.Increment(ref audits) == 1);
            });

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var drive = AnswerAsync(kit, "approve");

        // The amendment lands where the plan's own words put it: while the first round is at its audit, past its
        // items, so the work it adds is owed to the round that opens next.
        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2 && kit.Agents.Of("auditor").Count >= 1,
            () => $"{Shown(kit)}");

        var amended = await AmendAsync(kit, "Also ship the invoices view.");

        Assert.True(amended.ExitCode == HarnessExit.Success, $"{amended.Message} | {Shown(kit)}");

        // The amendment's plan is approved: its settle's approval, in the planning it made.
        var first = await WhenOpenAsync(
            kit,
            question => question.ProblemShown?.Happened?.Contains("the invoices view beside it", StringComparison.Ordinal) == true,
            () => $"{Shown(kit)}");

        // The first round's audit concludes: not clean, and the second round is its items - one of them owning the
        // file the amendment's item owes. With it ended, nothing of the run can go on but the amendment's approval,
        // and the drive ends paused on it.
        auditing.Open(with: Completed(SelfAudit(clean: false, items: [One(name: "three", owned: "src/shared/**", reuses: "src/tool.cs")])));

        _ = await drive;

        _ = await AnswerOneAsync(kit, first.Id, "approve");

        // The second round opens at its check: the round's own item and the owed one share a file, and the check
        // raises a plan update and waits - no work item of the round begins, and nobody is asked to build them as
        // they are. The planner is given the check's problems, in its own conversation.
        var settled = await WhenOpenAsync(
            kit,
            question => question.ProblemShown?.Happened?.Contains("the invoices view under paths of its own", StringComparison.Ordinal) == true,
            () => $"{Shown(kit)}");

        var given = Assert.Single(kit.Agents.Of("planner"), visit => visit.Prompt.Contains("found what does not pass as the round opens", StringComparison.Ordinal));

        Assert.Contains("work items 'three' and 'four' both own", given.Prompt, StringComparison.Ordinal);

        // The settled plan is approved; the check runs again on the list as it now is, and passes; the round builds
        // the settled items - and no fold of them is refused, wherever either came from.
        _ = await AnswerOneAsync(kit, settled.Id, "approve");

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"{Shown(kit)}");

        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        Assert.DoesNotContain(flow.Steps, step => step.Step.EndsWith(".fold", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Failed));

        // The check that opened the round: its first visit waits no more - stopped for the update it raised - and
        // its second ends on what it found, the settled list.
        var opening = flow.Steps.Where(step => step.Instance.Equals("rounds#2.item-check", StringComparison.Ordinal) || step.Instance.StartsWith("rounds#2.item-check+", StringComparison.Ordinal)).ToList();

        Assert.Contains(opening, step => step.State == nameof(FlowInstanceState.Stopped) && step.EndedFor == "u2");
        Assert.Contains(opening, step => step.State == nameof(FlowInstanceState.Done) && step.Output?["ok"]?.GetValue<bool>() == true);

        // The round built the settled items: the audit's own and the amendment's, under paths they do not share - the
        // settled one done again, in the place it holds in the round.
        Assert.Equal(4, kit.Agents.Of("implementer").Count);

        Assert.Equal(4, flow.Steps.Count(step => step.Instance.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done)));

        // The updates the run took: the amendment, owed to the round that opened next, and the plan check's, raised
        // as the round opened - its cause the check that found what does not pass, with its problems.
        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;

        Assert.Equal([("amendment", "done"), ("planCheck", "done")], [.. updates.Updates.Select(update => (update.Cause, update.State))]);

        var raised = updates.Updates[^1];

        Assert.Equal("rounds#2.item-check", raised.Check);
        Assert.Contains(raised.Problems!, problem => problem.Contains("work items 'three' and 'four' both own", StringComparison.Ordinal));

        var moved = Assert.Single(raised.Pieces!, piece => piece.Kind == "changed");

        Assert.Equal("four", moved.ItemName());
        Assert.Equal(["src/four/**"], [.. ((JsonArray)moved.Item!["ownedPaths"]!).Select(paths => (string)paths!)]);
        Assert.NotNull(moved.OwedTo);
    }

    /// <summary>
    /// An amendment made while the second round is at its gate - its audit, past its items, its loop's own bound spent -
    /// is given a third round, named in the report; one made past the items of that third round, one once the run's
    /// write-down has begun and one to the run that has ended are each refused with its sentence, and nothing of the
    /// refused ones is recorded.
    /// </summary>
    [Fact]
    public async Task AnAmendmentAtTheSecondRoundsGate_IsGivenAThirdRoundNamedInTheReport_AndOnesPastItsItems_InTheWriteDownAndToTheEndedRun_AreEachRefused()
    {
        using var temp = new TempDirectory();
        Held auditing = null!;
        Held writing = null!;
        var audits = 0;
        var scribes = 0;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One())),
                    // What it returns for the amendment: the whole plan again, with the ledger as an item of its own -
                    // checked, approved, and owed to the round that runs next.
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report, and the amendment adds the ledger as a third.",
                            ["done"] = "the report ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**"), One(name: "three", owned: "src/three/**")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "three", owned: "src/three/**"),
                                    ["for"] = "",
                                    ["changed"] = "the amendment adds the ledger, as the item 'three'",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                agents.Queue(
                    "auditor",
                    // The first round's audit: not clean, and the second round is its items. The third round's: not
                    // clean with nothing more to build, so the loop asks at its bound once that round has ended.
                    Completed(SelfAudit(clean: false, items: [One(name: "two", owned: "src/two/**")])),
                    Completed(SelfAudit(clean: false, items: [])));

                // The second round's audit is the gate the amendment is made at; the run's write-down is held at its
                // handoff.
                auditing = agents.Hold("auditor", _ => Interlocked.Increment(ref audits) == 2);
                writing = agents.Hold("scribe", _ => Interlocked.Increment(ref scribes) == 1);
            });

        // The rounds loop asks at its bound where the configuration would answer for it: the window an amendment past
        // the items of the last round stands in.
        await EditWorkflowAsync(kit, WithoutConfiguredLoopBounds);

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var drive = AnswerAsync(kit, "approve");

        // The amendment lands where the plan's own words put it: while the second round is at its audit, past its
        // items, its loop's own bound spent - so the work it adds is owed to a third round, added for the update.
        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2 && kit.Agents.Of("auditor").Count >= 2,
            () => $"{Shown(kit)}");

        var amended = await AmendAsync(kit, "Also ship the ledger.");

        Assert.True(amended.ExitCode == HarnessExit.Success, $"{amended.Message} | {Shown(kit)}");

        // The amendment's plan is approved: its settle's approval, in the planning it made.
        var settle = await WhenOpenAsync(
            kit,
            question => question.ProblemShown?.Happened?.Contains("adds the ledger as a third", StringComparison.Ordinal) == true,
            () => $"{Shown(kit)}");

        _ = await AnswerOneAsync(kit, settle.Id, "approve");

        // The update is done while the second round still stands at its audit, and the run is given its third round.
        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())?.Updates.FirstOrDefault(update => update.Id == "u1") is { State: "done", AddedIteration: "rounds#3" },
            () => $"{Shown(kit)}");

        // The second round's audit concludes clean: the loop would end, and runs its third round instead - the one the
        // update was given, the owed item alone.
        auditing.Open(with: Completed(SelfAudit(clean: true)));

        await WhenAsync(() => kit.Agents.Of("implementer").Count >= 3, () => $"{Shown(kit)}");

        // The third round's audit finds what is left and names nothing more to build: the loop asks at its bound, past
        // every item of every round it may run, and the run pauses on the question.
        var bound = await WhenOpenAsync(kit, question => question.Kind == "loopBound", () => $"{Shown(kit)}");

        _ = await drive;

        // An amendment past the items of that third round: refused, for no round it may run would carry the work out.
        var past = await AmendAsync(kit, "And the summary page too.");

        Assert.Equal(HarnessExit.Refused, past.ExitCode);
        Assert.Equal(
            "run 'r1' is past the work items of round 3 of 'rounds', the last it can run (2 by its own bound, and the 1 that amend.maxAddedIterations lets updates add): nothing would carry the change out. Start a run for the request once this one has ended",
            past.Message);

        // The loop's bound is accepted and the run writes down what it built; held there, past the step its amend
        // block names, a third amendment is refused.
        var tail = AnswerOneAsync(kit, bound.Id, "accept");

        await WhenAsync(() => kit.Agents.Of("scribe").Count >= 1, () => $"{Shown(kit)}");

        var written = await AmendAsync(kit, "And the handoff rewritten.");

        Assert.Equal(HarnessExit.Refused, written.ExitCode);
        Assert.Equal(
            "run 'r1' has started 'notes': what it built is being written down, and its plan is no longer changed. Start a run for the request once this one has ended",
            written.Message);

        writing.Open();
        _ = await tail;

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"{Shown(kit)}");

        // To a run that has ended: nothing left to go back to.
        var ended = await AmendAsync(kit, "One more thing.");

        Assert.Equal(HarnessExit.Refused, ended.ExitCode);
        Assert.Equal("run 'r1' is done; there is nothing left to go back to", ended.Message);

        // The run took one amendment, and it was given the third round: every round built its items - the third the
        // owed item alone - and nothing of the refused ones was recorded, and the report names the round the update
        // was given.
        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        Assert.Equal(3, kit.Agents.Of("implementer").Count);
        Assert.Equal(3, flow.Steps.Count(step => step.Instance.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done)));
        Assert.Contains(flow.Steps, step => step.Instance == "rounds#3" && step.State == nameof(FlowInstanceState.Done));
        Assert.Contains(flow.Steps, step => step.Instance == "rounds#3.items" && step.State == nameof(FlowInstanceState.Done));

        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;

        Assert.Equal([("amendment", "done", "rounds#3")], [.. updates.Updates.Select(update => (update.Cause, update.State, update.AddedIteration))]);

        var added = Assert.Single(updates.Updates[0].Pieces!, piece => piece.Kind == "added");

        Assert.Equal("three", added.ItemName());
        Assert.Equal("the next iteration of 'rounds'", added.OwedTo);

        Assert.Contains("  - round 3 of 'rounds' was added for update u1", File.ReadAllText(kit.RunOf().ReportFile), StringComparison.Ordinal);
    }

    /// <summary>
    /// The approval answered 'approve' and changed to 'revise' within the minute: the planner is given both answers and
    /// what the first set going, the plan is approved again, and what the change adds is carried out as an amendment's
    /// is; a changed answer that changes no scope, after which nothing is raised and nobody is told; the same past the
    /// minute, refused with the time it could be changed until; a second change of a set already changed; and a change
    /// of an answer the configuration gave, which is no set to change.
    /// </summary>
    [Fact]
    public async Task AnApprovalAnsweredApproveThenChangedToReviseWithinTheMinute_IsGivenToThePlannerWithBothAnswers_ApprovedAgain_AndCarriedOutAsAnAmendmentIs_AnEmptyChangeRaisesNothing_PastItsMinuteAndASecondChangeAreRefused_AndSoIsOneOfAnAnswerTheConfigurationGave()
    {
        using var temp = new TempDirectory();
        Held firstAudit = null!;
        Held secondAudit = null!;

        var kit = await PrepareAsync(
            temp,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One())),
                    // What it returns for the change: the whole plan again, the ledger one more item - checked,
                    // approved again, and owed to the round that runs next.
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report; the change adds the ledger as a second.",
                            ["done"] = "the report ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "two", owned: "src/two/**"),
                                    ["for"] = "",
                                    ["changed"] = "the change adds the ledger, as the item 'two'",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }),
                    // What it returns for the second change: the words change nothing to build - the same items, the
                    // scope empty, so nothing is raised and nobody is told.
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report; the words change nothing to build.",
                            ["done"] = "the report ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**")),
                            ["scope"] = new JsonArray(),
                            ["preExisting"] = new JsonArray(),
                        }));

                // This run's own audits are held, each by the round it audits - a held visit is answered once, by the
                // answer its hold is opened with, and the second run's audits of the same names stand for work slow
                // enough to be answered the same way.
                firstAudit = agents.Hold("auditor", step => step.StepInstance.StartsWith("rounds#1.self-audit", StringComparison.Ordinal));
                secondAudit = agents.Hold("auditor", step => step.StepInstance.StartsWith("rounds#2.self-audit", StringComparison.Ordinal));
            });

        // The rounds loop asks at its bound where the configuration would answer for it: the window a change past its
        // minute stands in.
        var workflow = Path.Combine(kit.Tree.Path, ".harness-config", "workflows", "implementation", "implementation.yml");
        var asShipped = File.ReadAllLines(workflow);

        await EditWorkflowAsync(kit, WithoutConfiguredLoopBounds);

        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var drive = AnswerAsync(kit, "approve");

        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 1 && kit.Agents.Of("auditor").Count >= 1,
            () => $"{Shown(kit)}");

        // The change, within the minute of the answer it changes: handed to the engine running the run.
        var changed = await ChangeAsync(kit, "revise", "Also ship the ledger.");

        Assert.True(HarnessExit.Success == changed.ExitCode, $"{changed.ExitCode}: {changed.Message} | {Shown(kit)}");

        // The planner is given both answers and what the first set going: the question with the answer before and the
        // answer now, and each step of the round the first answer set going, with how it stands.
        await WhenAsync(() => kit.Agents.Of("planner").Count >= 2, () => $"{Shown(kit)}");

        var planned = kit.Agents.Of("planner")[1].Prompt;

        Assert.Contains("was answered 'approve' and is answered 'revise' now, with: Also ship the ledger.", planned, StringComparison.Ordinal);
        Assert.Contains("(item-agent)", planned, StringComparison.Ordinal);
        Assert.Contains("(implement)", planned, StringComparison.Ordinal);

        // The plan is approved again: the change's settle, in the planning it made. The run's records keep times to
        // the second, and the last set a person answered is the newest by them: the approval waits for a later
        // second than the change, so what it records is the last beyond any tie.
        var settle = await WhenOpenAsync(
            kit,
            question => question.ProblemShown?.Happened?.Contains("adds the ledger as a second", StringComparison.Ordinal) == true,
            () => $"{Shown(kit)}");

        await WhenAsync(
            () => DateTimeOffset.UtcNow - NewestPersonAnswerAt(kit) > TimeSpan.FromSeconds(1),
            () => "the clock never moved past the change");

        _ = await AnswerOneAsync(kit, settle.Id, "approve");

        // The settle's answer is the last a person gave, as the run's records read, before the next change is given:
        // what a change names the last set by is the record.
        await WhenAsync(
            () => NewestPersonAnswer(kit) == settle.Id,
            () => $"{Shown(kit)} || answers: {string.Join(" | ", Questions(kit).Questions.SelectMany(question => question.Answers.Select(answer => $"{question.Id}/{answer.Answer}/by {answer.By?.Who}/at {answer.At:O}/update {answer.Update}")))}");

        // The update is done: the ledger is owed to the round that runs next, as an amendment's added item is.
        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())?.Updates.FirstOrDefault(update => update.Id == "u1") is { State: "done" },
            () => $"{Shown(kit)}");

        // The first round's audit concludes not clean, naming the ledger for the round that runs next - which the
        // update owes it too, so the second round builds it once.
        firstAudit.Open(with: Completed(SelfAudit(clean: false, items: [One(name: "two", owned: "src/two/**")])));

        await WhenAsync(
            () => kit.Agents.Of("implementer").Count >= 2 && kit.Agents.Of("auditor").Count >= 2,
            () => $"{Shown(kit)}");

        // A changed answer that changes no scope: the words of the second answer name nothing to build.
        var emptied = await ChangeAsync(kit, "revise", "Only the words of the summary change; nothing to build.");

        Assert.True(HarnessExit.Success == emptied.ExitCode, $"{emptied.ExitCode}: {emptied.Message} | {Shown(kit)}");

        await WhenAsync(() => kit.Agents.Of("planner").Count >= 3, () => $"{Shown(kit)}");

        var replanned = kit.Agents.Of("planner")[2].Prompt;

        Assert.Contains("was answered 'approve' and is answered 'revise' now, with: Only the words of the summary change; nothing to build.", replanned, StringComparison.Ordinal);

        var reApproved = await WhenOpenAsync(
            kit,
            question => question.ProblemShown?.Happened?.Contains("the words change nothing to build", StringComparison.Ordinal) == true,
            () => $"{Shown(kit)}");

        // A second change of the set the first change changed: refused, for a set is changed one time.
        await WhenAsync(() => NewestPersonAnswer(kit) == settle.Id, () => $"{Shown(kit)}");

        var second = await ChangeAsync(kit, "approve");

        Assert.Equal(HarnessExit.Refused, second.ExitCode);
        Assert.StartsWith($"question '{settle.Id}' of run 'r1' had its answer changed already, at ", second.Message, StringComparison.Ordinal);
        Assert.EndsWith("; a set is changed one time", second.Message, StringComparison.Ordinal);

        _ = await AnswerOneAsync(kit, reApproved.Id, "approve");

        // The empty change is done, and nothing is raised and nobody is told.
        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())?.Updates.FirstOrDefault(update => update.Id == "u2") is { State: "done" },
            () => $"{Shown(kit)}");

        Assert.Contains(
            "update u2 is done: its plan stands, and nothing of its scope changes",
            Events(kit),
            StringComparison.Ordinal);

        // The second round's audit concludes not clean with nothing more to name: the loop asks at its bound, which
        // the run parks on - the last set a person answered behind it, its minute spent.
        secondAudit.Open(with: Completed(SelfAudit(clean: false, items: [])));

        var bound = await WhenOpenAsync(kit, question => question.Kind == "loopBound", () => $"{Shown(kit)}");

        _ = await drive;

        // Every answer a person gave, aged past the minute its set could be changed in - the last of them least, so it
        // is still the last set a person answered, and its minute is spent.
        var older = 0;
        var aged = Questions(kit) with
        {
            Questions =
            [
                .. Questions(kit).Questions.Select(question => question with
                {
                    Answers = [.. question.Answers.Select(answer => answer.By?.Who == "person" ? answer with { At = DateTimeOffset.UtcNow.AddMinutes(-(20 - older++)) } : answer)],
                }),
            ],
        };

        kit.Harness.OrchestrationStore.WriteFlowQuestions(kit.RunOf(), aged);

        // The same change, past the minute: refused, with the time the answer could be changed until - and the answer
        // stands.
        var late = await ChangeAsync(kit, "revise", "And the summary page too.");

        Assert.Equal(HarnessExit.Refused, late.ExitCode);
        Assert.StartsWith($"question {reApproved.Id} of run 'r1' was answered 'approve' at ", late.Message, StringComparison.Ordinal);
        Assert.Contains("and could be answered again until ", late.Message, StringComparison.Ordinal);
        Assert.EndsWith(". The answer stands. To change what the run builds, amend its request: amend-orchestrator-request r1 \"<what to change>\"", late.Message, StringComparison.Ordinal);

        Assert.Single(Questions(kit).Questions.Single(question => question.Id == reApproved.Id).Answers);

        // The bound is accepted and the run ends on its leftover.
        var tail = AnswerOneAsync(kit, bound.Id, "accept");

        await tail;

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"{Shown(kit)}");

        // Both changes were carried out as an amendment's is: every round built its items - the second the owed item
        // alone - both answers kept with their times, the set each changed names its update, and nothing of the
        // refused ones was recorded.
        var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

        Assert.Equal(2, kit.Agents.Of("implementer").Count);
        Assert.Equal(2, flow.Steps.Count(step => step.Instance.EndsWith(".implement", StringComparison.Ordinal) && step.State == nameof(FlowInstanceState.Done)));
        Assert.Contains(flow.Steps, step => step.Instance == "rounds#2" && step.State == nameof(FlowInstanceState.Done));

        var updates = kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())!;

        Assert.Equal(
            [("changedAnswer", "done", "approve", "revise"), ("changedAnswer", "done", "approve", "revise")],
            [.. updates.Updates.Select(update => (update.Cause, update.State, update.FromAnswer, update.ToAnswer))]);

        var added = Assert.Single(updates.Updates[0].Pieces!, piece => piece.Kind == "added");

        Assert.Equal("two", added.ItemName());
        Assert.Equal("the next iteration of 'rounds'", added.OwedTo);
        Assert.Null(updates.Updates[0].AddedIteration);

        Assert.Empty(updates.Updates[1].Pieces!);
        Assert.True((updates.Updates[1].Told?.Count ?? 0) == 0, $"{Shown(kit)}");
        Assert.Null(updates.Updates[1].AddedIteration);

        var questions = Questions(kit).Questions;

        Assert.Equal([questions[0].Id, settle.Id], [.. updates.Updates.Select(update => update.Question)]);

        Assert.Equal(2, questions[0].Answers.Count);
        Assert.Equal(("approve", false), (questions[0].Answers[0].Answer, questions[0].Answers[0].ReplacedAt is null));
        Assert.Equal(("revise", "u1", "person"), (questions[0].Answers[1].Answer, questions[0].Answers[1].Update, questions[0].Answers[1].By?.Who));

        Assert.Equal(2, questions[1].Answers.Count);
        Assert.Equal(("revise", "u2"), (questions[1].Answers[1].Answer, questions[1].Answers[1].Update));
        Assert.Single(questions[2].Answers);

        // A second run, its plan approved beforehand by its input and its bound answered by its configuration: no
        // person answered anything, so a change of the answer the configuration gave is refused - it is no set to
        // change.
        await EditWorkflowAsync(kit, _ => asShipped);

        var secondRun = await kit.Service.RunAsync(
            kit.Tree.Path,
            WorkflowTemplates.ShippedWorkflowName,
            run: null,
            ["request=Ship the ledger too.", "preapproved=yes"],
            json: false,
            cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.True(HarnessExit.Success == secondRun.ExitCode, $"{secondRun.ExitCode}: {secondRun.Message} | {Shown(kit)}");

        var secondQuestions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf("r2"))!;

        Assert.Equal("input", secondQuestions.Questions.Single(question => question.Kind == "pause").Answers[0].By?.Who);
        Assert.Equal("configuration", secondQuestions.Questions.Single(question => question.Kind == "loopBound").Answers[0].By?.Who);

        Assert.Equal("input", secondQuestions.Questions.Single(question => question.Kind == "pause").Answers[0].By?.Who);
        Assert.Equal("configuration", secondQuestions.Questions.Single(question => question.Kind == "loopBound").Answers[0].By?.Who);

        var configured = await kit.Service.AnswerAsync(
            kit.Tree.Path,
            "r2",
            "revise",
            question: null,
            text: "make it two ledgers",
            textFile: null,
            again: true,
            json: false,
            cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal((HarnessExit.Refused, "no answer of run 'r2' was given by a person, so there is no set to change"), (configured.ExitCode, configured.Message));
    }

    /// <summary>
    /// The status of a run at each of its moments - waiting on its approval, at work in its first round, done - is the
    /// engine's facts and the reporter's words, and the call changes no file of the run but its own record under
    /// <c>asks/</c>, its cost the person's and never the run's; where the slot holds no key for the reporter's model the
    /// facts are still the answer, with why no model worded them; a person's question is answered from a step's notes
    /// the run's records hold; and the listing of its questions holds both answers of the changed set and what became
    /// of each.
    /// </summary>
    [Fact]
    public async Task ARunsStatusAtItsQuestionItsWorkAndItsEnd_IsTheFactsAndTheReportersWords_ChangingNoFileOfTheRun_AndWithNoKeyForTheReportersModelItSaysSo_AnAskIsAnsweredFromAStepsNotes_AndTheQuestionsListingHoldsEveryAnswer()
    {
        using var temp = new TempDirectory();
        Held firstImplement = null!;
        Held firstAudit = null!;
        Held secondAudit = null!;

        // The reporter serves the keyed model alone: its tier's first entry - the client that signs in with the
        // machine's login - is taken out, so a slot that holds no key holds none for it.
        const string serving = "glm-5.3";

        AgentRunResult Worded(string text) => new(AgentRunStatus.Completed, FinalText: text) { Model = serving };

        var kit = await PrepareAsync(
            temp,
            adjust: WithoutTheLoginModelOfTheReportersTier,
            script: agents =>
            {
                agents.Queue(
                    "planner",
                    Completed(Plan(One())),
                    // What it returns for the change: the whole plan again, the ledger one more item - checked and
                    // approved again, owed to the round that runs next.
                    Completed(
                        new JsonObject
                        {
                            ["summary"] = "Two items ship the report; the change adds the ledger as a second.",
                            ["done"] = "the report ships; its test holds",
                            ["source"] = "made",
                            ["departures"] = new JsonArray(),
                            ["items"] = new JsonArray(One(), One(name: "two", owned: "src/two/**")),
                            ["scope"] = new JsonArray(
                                new JsonObject
                                {
                                    ["kind"] = "added",
                                    ["item"] = One(name: "two", owned: "src/two/**"),
                                    ["for"] = "",
                                    ["changed"] = "the change adds the ledger, as the item 'two'",
                                    ["keep"] = "",
                                    ["undo"] = "",
                                    ["instead"] = "",
                                }),
                            ["preExisting"] = new JsonArray(),
                        }));

                // The first round's implement is held to leave its notes: what the person's question asks is in the
                // run's records, as every agent's run keeps them.
                firstImplement = agents.Hold("implementer", step => step.StepInstance.StartsWith("rounds#1", StringComparison.Ordinal));

                firstAudit = agents.Hold("auditor", step => step.StepInstance.StartsWith("rounds#1.self-audit", StringComparison.Ordinal));
                secondAudit = agents.Hold("auditor", step => step.StepInstance.StartsWith("rounds#2.self-audit", StringComparison.Ordinal));

                // The reporter's own answers: three statuses, a person's question, and - where the slot holds no key
                // for its model - the answer a run with no credential gives.
                agents.Queue(
                    "reporter",
                    Worded("The run waits on its approval."),
                    Worded("The second round is at its self-audit."),
                    Worded("The run is done; both items shipped."),
                    Worded("In src/one.cs, as the notes of the implement of 'one' say."),
                    new AgentRunResult(AgentRunStatus.NotStarted, AgentNotStartedBecause.KeyMissing, Error: "the run was given no credential, and every request is signed with a key"));
            });

        // It waits on a question: the approval open, the run paused on it. The call answers with the facts - the
        // question among them, and the command that answers it - worded by the reporter, and changes no file of the
        // run but its own record.
        var paused = await StartAsync(kit, "request=Ship the orders report.");

        Assert.Equal(FlowExit.Paused, paused.ExitCode);

        var approval = Questions(kit).Questions.Single(question => question.Kind == "pause");
        var atQuestion = await AskingOnlyAsync(kit, () => kit.Service.AskStatusAsync(kit.Tree.Path, "r1", json: false, Token).WaitAsync(Bound, Token));

        Assert.Equal(HarnessExit.Success, atQuestion.ExitCode);
        Assert.StartsWith($"run 'r1' (workflow '{WorkflowTemplates.ShippedWorkflowName}'), as of ", atQuestion.Message, StringComparison.Ordinal);

        var atQuestionText = string.Join("\n", atQuestion.Details!);

        Assert.Contains($"question {approval.Id} (pause) since ", atQuestionText, StringComparison.Ordinal);
        Assert.Contains($"answer-orchestrator r1 <answer> --question {approval.Id}", atQuestionText, StringComparison.Ordinal);
        Assert.Contains(
            "estimate  none: every step instance that is left is of a step that has ended no instance Done in this run",
            atQuestionText,
            StringComparison.Ordinal);
        Assert.Equal("The run waits on its approval.", atQuestion.Details!.Single(line => line == "The run waits on its approval."));
        Assert.Equal(
            $"This answer cost an unknown amount ({serving}). It is not counted in the run's budget.",
            atQuestion.Details!.Single(line => line.StartsWith("This answer cost", StringComparison.Ordinal)));

        // The reporter was given the facts and where the records are, its reads narrowed to the run's directory.
        var statusAsked = kit.Agents.Of("reporter")[0];

        Assert.StartsWith("Say how this run stands, for a person who asked.", statusAsked.Prompt, StringComparison.Ordinal);
        Assert.Contains("## The facts", statusAsked.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## The question", statusAsked.Prompt, StringComparison.Ordinal);
        Assert.Contains(kit.RunOf().Directory, statusAsked.Prompt, StringComparison.Ordinal);
        Assert.Equal(kit.RunOf().Directory, statusAsked.Ground!.ReporterRoot);
        Assert.Equal(serving, statusAsked.Model);
        Assert.True(statusAsked.Order![0].Usable, $"{statusAsked.Order[0].Model} is not usable");

        var asks = statusAsked.RecordDirectory!;

        Assert.StartsWith(kit.RunOf().Directory, asks, StringComparison.Ordinal);
        Assert.Contains($"{Path.DirectorySeparatorChar}asks{Path.DirectorySeparatorChar}", asks, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(asks, "facts.md")), "the facts as given are kept in the call's own record");
        Assert.Contains($"Record: {asks}", atQuestion.Details!, StringComparer.Ordinal);

        // It works: the first round built its item and, the change approved again, the second round is building the
        // ledger. The facts say what is running and when the run may finish - a range now, for a step of what is left
        // has ended Done once in this run - and the call leaves the run's files as it found it, the engine parked at
        // the implement.
        var drive = AnswerAsync(kit, "approve");

        await WhenAsync(() => kit.Agents.Of("implementer").Count >= 1, () => $"{Shown(kit)}");

        // The first round's implement leaves its notes, as every agent's run does: the answer to the person's later
        // question is in the run's records from here on.
        File.WriteAllText(
            Path.Combine(kit.Agents.Of("implementer")[0].RecordDirectory!, OrchestratorLayout.StepNotesFileName),
            "the item 'one' lives in src/one.cs, made as one file");

        firstImplement.Open();

        await WhenAsync(() => kit.Agents.Of("auditor").Count >= 1, () => $"{Shown(kit)}");

        // The approval answered 'approve' and changed to 'revise' within the minute: the plan is approved again and
        // the ledger is owed to the round that runs next.
        var changed = await ChangeAsync(kit, "revise", "Also ship the ledger.");

        Assert.True(HarnessExit.Success == changed.ExitCode, $"{changed.ExitCode}: {changed.Message} | {Shown(kit)}");

        await WhenAsync(() => kit.Agents.Of("planner").Count >= 2, () => $"{Shown(kit)}");

        var settle = await WhenOpenAsync(
            kit,
            question => question.ProblemShown?.Happened?.Contains("adds the ledger as a second", StringComparison.Ordinal) == true,
            () => $"{Shown(kit)}");

        await WhenAsync(
            () => DateTimeOffset.UtcNow - NewestPersonAnswerAt(kit) > TimeSpan.FromSeconds(1),
            () => "the clock never moved past the change");

        _ = await AnswerOneAsync(kit, settle.Id, "approve");

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlowUpdates(kit.RunOf())?.Updates.FirstOrDefault(update => update.Id == "u1") is { State: "done" },
            () => $"{Shown(kit)}");

        firstAudit.Open(with: Completed(SelfAudit(clean: false, items: [One(name: "two", owned: "src/two/**")])));

        await WhenAsync(() => kit.Agents.Of("implementer").Count >= 2 && kit.Agents.Of("auditor").Count >= 2, () => $"{Shown(kit)}");

        // It works: the second round built the ledger and its audit is at work. The facts say what is running and
        // when the run may finish - a range now, for a step of what is left, this run's audits, has ended Done once
        // in it - and the call leaves the run's files as it found it, the engine parked at the audit.
        var atWork = await AskingOnlyAsync(kit, () => kit.Service.AskStatusAsync(kit.Tree.Path, "r1", json: false, Token).WaitAsync(Bound, Token));

        Assert.Equal(HarnessExit.Success, atWork.ExitCode);

        var atWorkText = string.Join("\n", atWork.Details!);

        Assert.Contains("rounds#2.self-audit (reason, agent '", atWorkText, StringComparison.Ordinal);
        Assert.Contains(", attempt 1) for ", atWorkText, StringComparison.Ordinal);
        Assert.Contains("between ", atWorkText, StringComparison.Ordinal);
        Assert.Contains(" more, from the ", atWorkText, StringComparison.Ordinal);
        Assert.Equal("The second round is at its self-audit.", atWork.Details!.Single(line => line == "The second round is at its self-audit."));

        secondAudit.Open();

        var done = await drive;

        Assert.True(HarnessExit.Success == done.ExitCode, $"{done.ExitCode}: {done.Message} | {Shown(kit)}");

        await WhenAsync(
            () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
            () => $"{Shown(kit)}");

        // It has ended: no estimate and nothing more can come, the facts saying both, and the reporter's words beside
        // them.
        var atEnd = await AskingOnlyAsync(kit, () => kit.Service.AskStatusAsync(kit.Tree.Path, "r1", json: false, Token).WaitAsync(Bound, Token));

        Assert.Equal(HarnessExit.Success, atEnd.ExitCode);
        Assert.Contains(": done, ran for ", atEnd.Message, StringComparison.Ordinal);

        var atEndText = string.Join("\n", atEnd.Details!);

        Assert.Contains("none: the run has ended", atEndText, StringComparison.Ordinal);
        Assert.Contains("nothing more can come", atEndText, StringComparison.Ordinal);
        Assert.Contains(" tokens spent", atEndText, StringComparison.Ordinal);
        Assert.Equal("The run is done; both items shipped.", atEnd.Details!.Single(line => line == "The run is done; both items shipped."));

        // A person's question whose answer is in a step's notes: the reporter is given the question and the facts,
        // and its answer names what the records hold.
        var asked = await AskingOnlyAsync(kit, () => kit.Service.AskAsync(kit.Tree.Path, "r1", "where does the one item live?", json: false, Token).WaitAsync(Bound, Token));

        Assert.Equal(HarnessExit.Success, asked.ExitCode);
        Assert.Equal($"run 'r1' answered the question, worded by {serving}", asked.Message);
        Assert.Equal("In src/one.cs, as the notes of the implement of 'one' say.", asked.Details!.Single(line => line == "In src/one.cs, as the notes of the implement of 'one' say."));

        var askVisit = kit.Agents.Of("reporter").Single(visit => visit.Prompt.Contains("## The question", StringComparison.Ordinal));

        Assert.StartsWith("Answer a person's question about this run, from its records.", askVisit.Prompt, StringComparison.Ordinal);
        Assert.Contains($"## The question\n\nwhere does the one item live?\n\n## The facts", askVisit.Prompt, StringComparison.Ordinal);
        Assert.Contains(kit.RunOf().Directory, askVisit.Prompt, StringComparison.Ordinal);

        var implement = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps
            .Single(step => step.Instance.StartsWith("rounds#1", StringComparison.Ordinal) && step.Instance.EndsWith(".implement", StringComparison.Ordinal));

        Assert.Contains(
            "the item 'one' lives in src/one.cs",
            File.ReadAllText(Path.Combine(kit.RunOf().InstanceDirectory(implement.Instance, implement.Agent!), OrchestratorLayout.StepNotesFileName)),
            StringComparison.Ordinal);

        // The slot holds no key for the reporter's model: the facts are still the answer, said why no model worded
        // them, and the order the reporter was given holds no usable entry and no credential.
        OrchestrationKit.Write(kit.Tree.Path, Path.Combine(".harness-config", "workflows", ".env", "a.env"), string.Empty);

        var keyless = await AskingOnlyAsync(kit, () => kit.Service.AskStatusAsync(kit.Tree.Path, "r1", json: false, Token).WaitAsync(Bound, Token));

        Assert.Equal(HarnessExit.Success, keyless.ExitCode);
        Assert.StartsWith($"run 'r1' (workflow '{WorkflowTemplates.ShippedWorkflowName}'), as of ", keyless.Message, StringComparison.Ordinal);
        Assert.Contains("no model worded this: the run was given no credential, and every request is signed with a key", string.Join("\n", keyless.Details!), StringComparison.Ordinal);
        Assert.Equal("This answer cost nothing: no model worded it.", keyless.Details!.Single(line => line.StartsWith("This answer cost", StringComparison.Ordinal)));

        var keylessVisit = kit.Agents.Of("reporter")[^1];

        Assert.All(keylessVisit.Order!, entry => Assert.False(entry.Usable, $"{entry.Model} stayed usable"));
        Assert.Null(keylessVisit.Credential);

        // The listing holds every question of the run - the approval the change changed and the settle of its plan,
        // both answers of the one and the person's answer of the other - and what became of each.
        var listed = await kit.Service.QuestionsAsync(kit.Tree.Path, "r1", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        Assert.True(
            listed.Message == "run 'r1' raised 2 question(s)",
            $"{listed.Message} | questions: {string.Join(", ", Questions(kit).Questions.Select(question => $"{question.Id}/{question.Kind}/{question.State}/{question.Answers.Count} answer(s)"))}");

        var questions = Questions(kit).Questions;
        var changedSet = questions.Single(question => question.Kind == "pause" && question.Answers.Count == 2);
        var settled = questions.Single(question => question.Kind == "pause" && question.Answers.Count == 1);
        var listedText = string.Join("\n", listed.Details!);

        Assert.Contains($"question {changedSet.Id} (pause, answered): ", listedText, StringComparison.Ordinal);
        Assert.Contains("  answered 'approve' by a person, through answer-orchestrator", listedText, StringComparison.Ordinal);
        Assert.Contains("  answered 'revise' by a person, through answer-orchestrator: \"Also ship the ledger.\"", listedText, StringComparison.Ordinal);
        Assert.Contains($"question {settled.Id} (pause, answered): ", listedText, StringComparison.Ordinal);
        Assert.Contains($"questions: {kit.RunOf().QuestionsFile}", listedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The safe pause over the shipped flow: one in the middle of an implementation asks its agent for its handoff and
    /// the step's next attempt is given it, its file spent; one in the middle of a review, whose agent gave no account,
    /// leaves the handoff the engine writes in the step's own directory and gives it the same way; one in the middle of
    /// a build pauses the run while the build runs, says the engine stays to record it, refuses a resume while it lives
    /// and ends once the build is recorded; <c>--all</c> stops the machine steps the pause leaves running, and a second
    /// interrupt during the handoffs stops the run and keeps no handoff, while one after the pause is what <c>--all</c> is.
    /// </summary>
    [Fact]
    public async Task ASafePauseInTheMiddleOfAnImplementation_GivesTheAgentsHandoffToItsNextAttempt_OneOfAReview_LeavesTheEnginesOwnInTheStepsDirectory_AndOneOfABuild_PausesTheRunWhileItRuns_RefusingAResumeUntilTheEngineHasRecordedIt_WithAllAndASecondInterruptStoppingWhatRuns()
    {
        // The implementation and the review: one run, paused at its working step twice - the agent's account asked of
        // the implementer (the record its runner writes when the client answers the handoff turn), and none of the
        // reviewer, so the engine writes that one itself - each given to the step's next attempt at the resume.
        {
            using var temp = new TempDirectory();
            Held firstImplement = null!, reviewer = null!;

            var kit = await PrepareAsync(
                temp,
                agents =>
                {
                    agents.Queue("planner", Completed(Plan(One())));
                    firstImplement = agents.Hold("implementer", step => step.StepInstance.StartsWith("rounds#1", StringComparison.Ordinal));
                    reviewer = agents.Hold("reviewer-correctness");
                });

            var parked = await StartAsync(kit, "request=Ship the orders report.");

            Assert.Equal(FlowExit.Paused, parked.ExitCode);

            var drive = AnswerAsync(kit, "approve");

            await WhenAsync(() => kit.Agents.Of("implementer").Count == 1, () => Shown(kit));

            var implement = kit.Agents.Of("implementer")[0];

            // The agent's own account of where its work stands, as its runner records it when the client answers the
            // handoff turn: what the pause's turn reads back of the step's record.
            File.WriteAllText(
                Path.Combine(implement.RecordDirectory!, AgentRunner.ResultFileName),
                System.Text.Json.JsonSerializer.Serialize(
                    new AgentRunResult(AgentRunStatus.HandedOff, Handoff: new AgentHandoff("Done: the report's shape stands; the seeding is half applied.")),
                    DssHarness.Core.FileSystem.JsonStateFile.Options));

            var pausing = PauseAsync(kit);

            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Pause is { CompletedAt: null },
                () => $"the pause never began | {Shown(kit)}");

            firstImplement.Open();

            var paused = await pausing;

            Assert.Equal(HarnessExit.Success, paused.ExitCode);
            Assert.Equal("run 'r1' is paused; 1 handoff(s) written", paused.Message);

            // The pause answers once its last handoff is written; the session then writes its end, so the run's
            // standing settles to paused as the driving session ends.
            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Paused) },
                () => Shown(kit));

            var pausedText = string.Join("\n", paused.Details!);

            Assert.Contains($"handoff of {implement.StepInstance}, written by the agent: ", pausedText, StringComparison.Ordinal);
            Assert.Contains(OrchestratorLayout.HandoffFileName, pausedText, StringComparison.Ordinal);

            var atTheImplement = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;
            var stoppedImplement = atTheImplement.Steps.Single(step => step.Instance == implement.StepInstance);

            Assert.Equal((nameof(FlowRunStatus.Paused), "command"), (atTheImplement.Status, atTheImplement.Pause?.Cause));
            Assert.Equal([implement.StepInstance], atTheImplement.Pause?.Handoffs);
            Assert.Equal((nameof(FlowInstanceState.Stopped), "agent", 1), (stoppedImplement.State, stoppedImplement.Handoff?.By, stoppedImplement.Attempt));

            var implementHandoff = kit.RunOf().InstanceHandoffFile(implement.StepInstance, implement.Agent);

            Assert.Equal("Done: the report's shape stands; the seeding is half applied.\n", File.ReadAllText(implementHandoff));

            // The session that drove the round is over, ended by the pause.
            var ended = await drive;

            Assert.Equal(HarnessExit.Cancelled, ended.ExitCode);

            // The resume gives the handoff to the step's next attempt, in its continuation record, and the file is
            // spent: the handoff is always in the file or in the prompt, never neither.
            var resumed = ResumeAsync(kit);

            await WhenAsync(() => kit.Agents.Of("implementer").Count == 2, () => Shown(kit));

            var again = kit.Agents.Of("implementer")[1];

            Assert.Equal(2, again.Attempt);
            Assert.StartsWith(
                "<handoff>\nDone: the report's shape stands; the seeding is half applied.\n</handoff>",
                again.Prompt,
                StringComparison.Ordinal);

            await WhenAsync(() => !File.Exists(implementHandoff), () => "the handoff was never spent");

            // The item's reviews: the correctness reviewer is held mid-read, and a pause there - its agent gives no
            // account - leaves the handoff the engine writes in the step's own directory.
            await WhenAsync(() => kit.Agents.Of("reviewer-correctness").Count == 1, () => Shown(kit));

            var reading = kit.Agents.Of("reviewer-correctness")[0];

            var pausingAgain = PauseAsync(kit);

            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Pause is { CompletedAt: null, Handoffs: [] },
                () => $"the second pause never began | {Shown(kit)}");

            reviewer.Open();

            var pausedAgain = await pausingAgain;

            Assert.Equal(HarnessExit.Success, pausedAgain.ExitCode);
            Assert.Contains(
                $"handoff of {reading.StepInstance}, written by the engine: ",
                string.Join("\n", pausedAgain.Details!),
                StringComparison.Ordinal);

            var reviewHandoff = kit.RunOf().InstanceHandoffFile(reading.StepInstance, reading.Agent);

            Assert.Equal("Written by the engine: the agent's answer to its handoff turn held no account.\n", File.ReadAllText(reviewHandoff));
            Assert.Equal("engine", kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps.Single(step => step.Instance == reading.StepInstance).Handoff?.By);

            var endedAgain = await resumed;

            Assert.Equal(HarnessExit.Cancelled, endedAgain.ExitCode);

            // The engine's handoff is given the same way, and the run goes on to its end.
            var finished = await ResumeAsync(kit);

            Assert.True(HarnessExit.Success == finished.ExitCode, $"{finished.ExitCode}: {finished.Message} | {Shown(kit)}");

            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Done) },
                () => Shown(kit));

            var readAgain = kit.Agents.Of("reviewer-correctness")[1];

            Assert.StartsWith(
                "<handoff>\nWritten by the engine: the agent's answer to its handoff turn held no account.\n</handoff>",
                readAgain.Prompt,
                StringComparison.Ordinal);
        }

        // The build: the run pauses while it runs, the engine stays to record it, a resume typed before that is
        // refused, and the engine's process ends once the build is recorded.
        {
            using var temp = new TempDirectory();
            var kit = await PrepareAsync(temp, ground: new Ground(Legs: true));
            var build = kit.Steps.Hold("build");

            var starting = StartAsync(kit, "request=Ship the orders report.");

            await WhenAsync(() => kit.Steps.Of("build").Count == 1, () => Shown(kit));

            var paused = await PauseAsync(kit);

            Assert.Equal(HarnessExit.Success, paused.ExitCode);
            Assert.Equal("run 'r1' is paused", paused.Message);
            Assert.Contains(
                "1 machine step(s) still in progress: build; the engine's process ends when it has",
                string.Join("\n", paused.Details!),
                StringComparison.Ordinal);

            // The run is paused under a completed pause while the build still runs - the engine stays to record it -
            // and a resume typed before it lives is refused.
            var whileItBuilds = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

            Assert.False(whileItBuilds.Pause?.All);
            Assert.Equal(["build"], whileItBuilds.MachineStepsRunning());
            Assert.False(starting.IsCompleted, "the engine left before the build was recorded");

            var refused = await ResumeAsync(kit);

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Contains("is owned by a live engine", refused.Message, StringComparison.Ordinal);

            build.Open();

            var engineLeft = await starting;

            Assert.Equal(HarnessExit.Cancelled, engineLeft.ExitCode);

            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Steps.Single(step => step.Instance == "build") is { State: nameof(FlowInstanceState.Done) },
                () => Shown(kit));

            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf()) is { Status: nameof(FlowRunStatus.Paused) },
                () => Shown(kit));

            var on = await ResumeAsync(kit);

            Assert.Equal(FlowExit.Paused, on.ExitCode);
            await WhenOpenAsync(kit, question => question.Kind == "pause", () => Shown(kit));
        }

        // The same with --all: the pause itself stops the machine steps - a leg it stops may still be draining as its
        // runner ends, said so at the pause, and the legs end stopped, to run again at the resume.
        {
            using var temp = new TempDirectory();
            var kit = await PrepareAsync(temp, ground: new Ground(Legs: true));
            var build = kit.Steps.Hold("build");

            var starting = StartAsync(kit, "request=Ship the orders report.");

            await WhenAsync(() => kit.Steps.Of("build").Count == 1, () => Shown(kit));

            var paused = await PauseAsync(kit, all: true);

            Assert.Equal(HarnessExit.Success, paused.ExitCode);

            var engineLeft = await starting;

            Assert.Equal(HarnessExit.Cancelled, engineLeft.ExitCode);

            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Steps.Single(step => step.Instance == "build") is { State: nameof(FlowInstanceState.Stopped) },
                () => Shown(kit));

            Assert.True(kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Pause?.All is true, "the pause's record does not say the machine steps were stopped");

            build.Open();

            var on = await ResumeAsync(kit);

            Assert.Equal(FlowExit.Paused, on.ExitCode);

            await WhenAsync(() => kit.Steps.Of("build").Count == 2, () => Shown(kit));
        }

        // A second interrupt during the handoffs: the turn is cancelled, no handoff is kept, and the run is stopped.
        {
            using var temp = new TempDirectory();
            Held implement = null!;

            var kit = await PrepareAsync(
                temp,
                agents =>
                {
                    agents.Queue("planner", Completed(Plan(One())));
                    implement = agents.Hold("implementer", step => step.StepInstance.StartsWith("rounds#1", StringComparison.Ordinal));
                });

            var parked = await StartAsync(kit, "request=Ship the orders report.");

            Assert.Equal(FlowExit.Paused, parked.ExitCode);

            var drive = AnswerAsync(kit, "approve");

            await WhenAsync(() => kit.Agents.Of("implementer").Count == 1, () => Shown(kit));

            var working = kit.Agents.Of("implementer")[0];
            var pausing = PauseAsync(kit);

            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Pause is { CompletedAt: null },
                () => $"the pause never began | {Shown(kit)}");

            var stopping = StopAsync(kit);

            var stopped = await drive;

            Assert.Equal(HarnessExit.Cancelled, stopped.ExitCode);

            var end = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

            Assert.Equal(nameof(FlowRunStatus.Stopped), end.Status);
            Assert.Equal(nameof(FlowInstanceState.Stopped), end.Steps.Single(step => step.Instance == working.StepInstance).State);
            Assert.False(File.Exists(kit.RunOf().InstanceHandoffFile(working.StepInstance, working.Agent)), "a stop kept a handoff");

            var neverPaused = await pausing;

            Assert.Equal(HarnessExit.Refused, neverPaused.ExitCode);
            Assert.Contains("so nothing was paused", neverPaused.Message, StringComparison.Ordinal);

            var ended = await stopping;

            Assert.Equal(HarnessExit.Success, ended.ExitCode);
            Assert.Contains("is stopped", ended.Message, StringComparison.Ordinal);
        }

        // A second interrupt after the handoffs - the pause complete, a build left running: it is what --all is, the
        // machine steps stopped and the run left paused, to go on with at a resume.
        {
            using var temp = new TempDirectory();
            var kit = await PrepareAsync(temp, ground: new Ground(Legs: true));
            var build = kit.Steps.Hold("build");

            var starting = StartAsync(kit, "request=Ship the orders report.");

            await WhenAsync(() => kit.Steps.Of("build").Count == 1, () => Shown(kit));

            _ = await PauseAsync(kit);

            var stopping = StopAsync(kit);

            var engineLeft = await starting;

            Assert.Equal(HarnessExit.Cancelled, engineLeft.ExitCode);

            await WhenAsync(
                () => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Steps.Single(step => step.Instance == "build") is { State: nameof(FlowInstanceState.Stopped) },
                () => Shown(kit));

            var end = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

            Assert.Equal((nameof(FlowRunStatus.Paused), true), (end.Status, end.Pause?.All));

            var stopped = await stopping;

            Assert.Equal(HarnessExit.Success, stopped.ExitCode);
            Assert.Contains("is paused", stopped.Message, StringComparison.Ordinal);

            build.Open();

            var on = await ResumeAsync(kit);

            Assert.Equal(FlowExit.Paused, on.ExitCode);

            await WhenAsync(() => kit.Steps.Of("build").Count == 2, () => Shown(kit));
        }
    }

    /// <summary>
    /// Every file of the run as it stands, but the calls' own records under its <c>asks/</c> and the lock files the
    /// engine holds open while it works: what a call that reads only must leave as it found it. The files are read
    /// sharing them with the engine, which holds its events open while it works.
    /// </summary>
    private static Dictionary<string, byte[]> FilesOf(Kit kit)
        => Directory.EnumerateFiles(kit.RunOf().Directory, "*", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}asks{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(file => file, SharedRead, StringComparer.Ordinal);

    /// <summary>A file's bytes, read sharing it with whoever holds it open.</summary>
    private static byte[] SharedRead(string file)
    {
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var read = new MemoryStream();
        held.CopyTo(read);

        return read.ToArray();
    }

    /// <summary>A call of one of the two ask commands, checked to change no file of the run but its own record under <c>asks/</c>.</summary>
    private static async Task<CommandOutcome> AskingOnlyAsync(Kit kit, Func<Task<CommandOutcome>> call)
    {
        var before = FilesOf(kit);
        var outcome = await call();

        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), FilesOf(kit).Keys.Order(StringComparer.Ordinal));
        Assert.All(before, file => Assert.True(file.Value.SequenceEqual(SharedRead(file.Key)), $"{file.Key} changed"));

        return outcome;
    }

    /// <summary>Rewrites the workflow the kit's repository holds, and commits it as the run reads it.</summary>
    private static async Task EditWorkflowAsync(Kit kit, Func<string[], string[]> edit)
    {
        var workflow = Path.Combine(kit.Tree.Path, ".harness-config", "workflows", "implementation", "implementation.yml");
        File.WriteAllLines(workflow, edit([.. File.ReadAllLines(workflow)]));
        await kit.Harness.CommitAllAsync(kit.Tree.Path, "the workflow as this run reads it", Token);
    }

    /// <summary>
    /// The workflow's lines with every configured answer a loop's bound takes out: a loop at its bound is asked of a
    /// person, the whole <c>answers</c> block gone with the entry - an empty one is no mapping at all.
    /// </summary>
    private static string[] WithoutConfiguredLoopBounds(string[] lines)
    {
        var kept = new List<string>();

        for (var at = 0; at < lines.Length; at++)
        {
            var bound = lines[at].Trim().Equals("loopBound: accept", StringComparison.Ordinal);
            var answers = !bound && lines[at].Trim().Equals("answers:", StringComparison.Ordinal)
                && at + 1 < lines.Length && lines[at + 1].Trim().Equals("loopBound: accept", StringComparison.Ordinal);

            if (!bound && !answers)
            {
                kept.Add(lines[at]);
            }
        }

        return [.. kept];
    }

    /// <summary>
    /// Takes the model of the reporter's tier that signs in with the machine's login out of it, tier and model both:
    /// the tier holds the keyed model alone, so a slot that holds no key holds none for the reporter.
    /// </summary>
    private static void WithoutTheLoginModelOfTheReportersTier(HarnessConfig config)
    {
        config.TierConfig["fast"].Remove("claude-haiku-5-5");
        config.Providers.Single(entry => entry.Name == "anthropic").Models
            .Single(model => model.Code == "claude-haiku-5-5")
            .Tiers.Remove("fast");
    }

    /// <summary>Takes the fallback model's prices out of the configuration: a model whose cost cannot be counted.</summary>
    private static void WithoutGlmPrices(HarnessConfig config)
    {
        var provider = config.Providers.Single(entry => entry.Name == "zai");
        var glm = provider.Models.Single(model => model.Code == "glm-5.3");

        provider.Models.Remove(glm);
        provider.Models.Add(
            new AgentModelConfig
            {
                Code = glm.Code,
                Name = glm.Name,
                Tiers = glm.Tiers,
                Efforts = glm.Efforts,
                ContextTokens = glm.ContextTokens,
                MaxOutputTokens = glm.MaxOutputTokens,
                LargePrompt = glm.LargePrompt,
            });
    }

    /// <summary>
    /// A hard stop is gone on from: what it stopped runs again, its next attempt given the handoff the engine wrote of
    /// the attempt the stop cut off. A budget that reaches its reserve while a build runs - the run's second item
    /// reviewed as the first's verify builds - spends none of it there and stops nothing: the build runs to its end and
    /// is recorded, the run pauses and asks, and a grant of more carries it to its end. And a crash - the engine's
    /// process ended, stood in for by its records stopping at a write - after each kind of write the run makes, a
    /// raise and a redirect owed among them, is gone on from as the run would have gone on: the questions it answered
    /// stay answered, what it drew stands, and the mender a red verify owed is raised and its verify green again.
    /// </summary>
    [Fact]
    public async Task AHardStopIsGoneOnFromWithTheHandoffTheEngineWrote_ABudgetReachingItsReserveWhileABuildRuns_SpendsNoneOfIt_StopsNothingAndAsks_AndACrashAfterEachKindOfWrite_IsGoneOnFromAsTheRunWouldHaveGoneOn()
    {
        // A hard stop, resumed: the implementer the stop cut off runs again, its attempt told what the engine's account
        // of the cut-off attempt held.
        using (var first = new TempDirectory())
        {
            var kit = await PrepareAsync(first);
            var implementer = kit.Agents.Hold("implementer");
            var started = StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            await WhenAsync(() => kit.Agents.Of("implementer").Count == 1, () => Shown(kit));

            var stopping = StopAsync(kit);
            var stoppedAt = await stopping;

            Assert.True(HarnessExit.Success == stoppedAt.ExitCode, $"{stoppedAt.Message} | {Shown(kit)}");
            Assert.Contains("is stopped", stoppedAt.Message, StringComparison.Ordinal);
            Assert.Equal(HarnessExit.Cancelled, (await started).ExitCode);

            var stopped = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

            Assert.Equal(nameof(FlowRunStatus.Stopped), stopped.Status);
            Assert.Equal(nameof(FlowInstanceState.Stopped), stopped.Steps.Single(step => step.Instance == "rounds#1.items[0].implement").State);

            implementer.Open();

            var resumed = await ResumeAsync(kit);

            Assert.True(HarnessExit.Success == resumed.ExitCode, $"{resumed.Message} | {string.Join(" | ", resumed.Details ?? [])} | {Shown(kit)}");
            Assert.Equal(nameof(FlowRunStatus.Done), kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Status);

            var attempts = kit.Agents.Of("implementer");

            Assert.Equal(2, attempts.Count);
            Assert.StartsWith("<handoff>\n", attempts[1].Prompt, StringComparison.Ordinal);
        }

        // A budget reaching its reserve while a build runs: the first item's verify is held mid-build as the second
        // item's review spends the run past its reserve - the pause the spend asks for leaves the build running, it
        // spends none of the budget and runs to its end and is recorded, and the run waits on its question alone.
        using (var second = new TempDirectory())
        {
            var kit = await PrepareAsync(
                second,
                keys: "ZAI_API_KEY=sk-test-two\n",
                ground: new Ground(Legs: true),
                script: agents =>
                {
                    agents.Queue("planner", Completed(Plan(One("one", "src/one/**"), One("two", "src/two/**"))));
                    agents.ServingModel = "glm-5.3";
                    agents.Usage = request => request.Attempt == 1 && request.AgentDefinition == "review-scope" && request.StepInstance.StartsWith("rounds#1.items[1]")
                        ? new AgentUsage(11_000_000, 5_500_000, 0, 0, 0, 0)
                        : new AgentUsage(1_000, 0, 0, 0, 0, 0);
                },
                machine: steps => steps.Queue("test", MachineSteps.Green(), MachineSteps.Green()));

            await EditWorkflowAsync(
                kit,
                lines =>
                {
                    var at = Array.IndexOf(lines, "answers:");
                    return [.. lines[..at], "budget:", "  usd: 40", .. lines[at..]];
                });

            var secondItem = kit.Agents.Hold("implementer", request => request.StepInstance.StartsWith("rounds#1.items[1]"));
            var firstsVerify = kit.Steps.Hold("test", run => run.Instance.StartsWith("rounds#1.items[0]"));
            var started = StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            await WhenAsync(() => kit.Steps.Of("test").Count == 2, () => Shown(kit));

            secondItem.Open();

            await WhenAsync(() => kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())?.Pause is { CompletedAt: not null }, () => Shown(kit));

            var atPause = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

            Assert.Equal(nameof(FlowRunStatus.Paused), atPause.Status);
            Assert.Equal("budget", atPause.Pause!.Cause);
            Assert.False(atPause.Pause.All);

            // The build the pause left running: neither stopped nor spending, and the run not stopped either.
            var verify = atPause.Steps.Single(step => step.Instance == "rounds#1.items[0].verify");

            Assert.Equal(nameof(FlowInstanceState.Running), verify.State);

            firstsVerify.Open();

            Assert.Equal(FlowExit.Paused, (await started).ExitCode);

            var parked = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

            Assert.Equal(nameof(FlowRunStatus.Paused), parked.Status);
            Assert.Equal(nameof(FlowInstanceState.Done), parked.Steps.Single(step => step.Instance == "rounds#1.items[0].verify").State);
            Assert.Equal(atPause.Spent!.Usd, parked.Spent!.Usd);

            var asked = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

            Assert.Equal("runBudget", asked.Kind);
            Assert.Equal(["more", "abort"], asked.Options.Select(option => option.Answer).ToArray());
            Assert.Contains("of 40.00 USD", asked.ProblemShown!.Happened, StringComparison.Ordinal);
            Assert.Contains("its reserve of 10% was reached", asked.ProblemShown!.Happened, StringComparison.Ordinal);

            var granted = await AnswerAsync(kit, "more");

            Assert.True(HarnessExit.Success == granted.ExitCode, $"{granted.Message} | {string.Join(" | ", granted.Details ?? [])} | {Shown(kit)}");

            var gone = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;

            Assert.Equal(nameof(FlowRunStatus.Done), gone.Status);
            Assert.Contains(
                "more",
                kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Questions
                    .Single(question => question.Kind == "runBudget").Answers.Select(answer => answer.Answer));
            Assert.True(gone.Spent!.Usd < 40, $"{gone.Spent.Usd}");
            Assert.True(File.Exists(kit.RunOf().ReportFile), Shown(kit));
        }

        // A crash after each kind of write: the engine's process is killed at a write the test names, its session
        // dying as a process does and its records standing as they were, and the session that answers the run's
        // question goes on from what stands - the owner the killed process left reclaimed, what it was in flight in
        // run again - to the very standing the uncut run reached. The arc holds a raise - the plan's approval - and a
        // redirect owed - the mender a red first verify raises - among the writes it is killed beside.
        async Task<(string Standing, IReadOnlyList<string> Writes, string Said)> CrashedAsync(int cut)
        {
            using var temp = new TempDirectory();
            var files = new CutFiles(new PhysicalFileSystem(DssHarness.Core.Platform.FilePermissionsFactory.Create()), Path.Combine(temp.Path, ".orchestrators", "r1")) { Cut = cut };
            var kit = await PrepareAsync(
                temp,
                ground: new Ground(Legs: true),
                machine: steps => steps.Answer = run => run.Step.Name == "verify"
                    ? run.Visit == 1 ? MachineSteps.Red(HarnessExit.CommandFailed, "the report test fails on its frame") : MachineSteps.Green()
                    : null,
                files: files);

            string parked;

            try
            {
                var whole = await StartAsync(kit, "request=Ship the orders report.");

                Assert.True(FlowExit.Paused == whole.ExitCode, $"cut {cut}: {whole.Message} | {string.Join(" | ", whole.Details ?? [])} | {Shown(kit)}");

                parked = whole.Message;
            }
            catch (CutFiles.Killed)
            {
                // The process the owner names has gone, as the one that died here has: the resume reclaims it.
                kit.Harness.OrchestrationStore.WriteOwner(kit.RunOf(), new RunOwner(kit.Harness.Identity.CurrentMachine, int.MaxValue, DateTimeOffset.Now, "gone"));

                parked = "killed";
            }

            var writes = files.Writes;

            files.Cut = int.MaxValue;

            // A run killed in flight is gone on with by a resume, which reclaims the owner the killed process left and
            // runs what it was in flight in again, to park on the run's question; one killed beside a question it
            // already asked is answered, never resumed, for nothing of it can run until the question is. Either way
            // the answer carries the run to its end.
            string gone = "none";

            if (parked == "killed")
            {
                var again = await ResumeAsync(kit);

                if (FlowExit.Paused != again.ExitCode)
                {
                    Assert.True(
                        HarnessExit.Refused == again.ExitCode && again.Message.Contains("answer-orchestrator answers it", StringComparison.Ordinal),
                        $"cut {cut}: {again.Message} | {string.Join(" | ", again.Details ?? [])} | {Shown(kit)}");
                }

                gone = again.Message;
            }

            var answered = await AnswerAsync(kit, "approve");

            Assert.True(HarnessExit.Success == answered.ExitCode, $"cut {cut} ({gone}): {answered.Message} | {string.Join(" | ", answered.Details ?? [])} | {Shown(kit)}");

            var flow = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!;
            var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;

            return ($"{flow.Status} | "
                + string.Join(", ", flow.Steps.OrderBy(step => step.Instance, StringComparer.Ordinal).Select(step => $"{step.Instance} {step.State} {step.Failure} {step.HandledBy}"))
                + " | " + string.Join(", ", questions.Questions.Select(question => $"{question.Id} {question.State} {string.Join("/", question.Answers.Select(answer => answer.Answer))}")),
                writes,
                $"parked '{parked}'; gone on with '{gone}'; answered '{answered.Message}'");
        }

        var (expected, writes, uncutSaid) = await CrashedAsync(int.MaxValue);

        // The uncut run raised the mender for the red first verify - the redirect owed among the writes - and asked
        // the person to approve its plan, the answer every crash stands beside.
        Assert.Contains(".mend ", expected, StringComparison.Ordinal);
        Assert.Contains("q1 answered approve", expected, StringComparison.Ordinal);

        // Each kind of write, crashed after its last: the cut leaves the run's state on disk - a crash before the
        // state first stood holds nothing to go on from, and the run's own session says so - and every kind that
        // stands after it is cut at its deepest.
        var firstState = writes.ToList().IndexOf(OrchestratorLayout.FlowFileName);

        var cuts = writes.Select((name, index) => (name, index))
            .GroupBy(write => write.name)
            .Select(group => group.Last().index)
            .Where(index => index > firstState)
            .Order()
            .ToList();

        Assert.True(cuts.Count >= 4, $"{cuts.Count} kinds past the state's first write among {writes.Count} writes: {string.Join(", ", writes)}");

        foreach (var cut in cuts)
        {
            var (standing, _, said) = await CrashedAsync(cut);

            Assert.True(string.Equals(expected, standing, StringComparison.Ordinal),
                $"a crash after write {cut + 1} of {writes.Count} ({writes[cut]}, the last of its kind) was gone on from otherwise than the run would have gone on ({said}; uncut {uncutSaid}):\nexpected {expected}\nactual   {standing}");
        }
    }

    /// <summary>
    /// A conversation that fills its context: the step's own question asks what to do, <c>compact</c> among its
    /// answers, and the run waits on it alone; answered compact, the step's next attempt is given the handoff the
    /// engine wrote of the attempt that filled it, and its conversation is a new one that goes on from the last
    /// iterations of the old word for word. Where the workflow's own configuration answers compact, nobody is asked:
    /// the question is raised and answered by the configuration in the same breath, the run never waiting on a person.
    /// </summary>
    [Fact]
    public async Task AConversationThatFillsItsContext_AsksWithCompactAmongItsAnswers_AndAnsweredCompact_GoesOnFromItsLastIterationsWordForWord_WhileTheConfigurationsOwnAnswerAsksNobody()
    {
        Action<Scripted> filled = agents => agents.Queue(
            "implementer",
            new AgentRunResult(AgentRunStatus.ContextExhausted, Error: "the context is full") { Turns = 1 },
            Completed(Implement()));

        // The attempt's stored conversation, as a client keeps it: a session the stream names, and one iteration of
        // work it did - what a compacted conversation goes on from, word for word.
        void StoredConversation(Kit kit)
        {
            kit.Agents.Events = (step, _) =>
            {
                if (step.AgentDefinition != "implementer" || step.Attempt != 1)
                {
                    return Task.CompletedTask;
                }

                kit.Harness.FileSystem.CreateDirectory(step.RecordDirectory!);
                var stream = StoredStream.Open(kit.Harness.FileSystem, step.RecordDirectory!, new Masking());
                stream.EmitAsync(new RunStarted(1));
                stream.EmitAsync(new SessionStarted("scripted", "glm-5.3", "hash", [], [], HandedConversation: false, Provider: "zai", SessionId: "session-one"));
                stream.EmitAsync(new TurnStarted(1));
                stream.EmitAsync(new AssistantMessage("Built the report's frame.", []));
                stream.EmitAsync(new RunFinished(AgentRunStatus.ContextExhausted));
                stream.Dispose();
                return Task.CompletedTask;
            };
        }

        // The question: the workflow's own answer taken out, the person is asked - compact among the answers - and the
        // run waits on that alone.
        using (var asks = new TempDirectory())
        {
            var kit = await PrepareAsync(asks, script: filled);
            StoredConversation(kit);

            await EditWorkflowAsync(
                kit,
                lines => [.. lines.Where((line, at) => at < Array.IndexOf(lines, "  contextLimit:") || at > Array.IndexOf(lines, "  contextLimit:") + 2)]);

            var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            Assert.Equal(FlowExit.Paused, paused.ExitCode);

            var asked = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

            Assert.Equal("contextLimit", asked.Kind);
            Assert.Equal(["retry", "compact", "fail", "abort"], asked.Options.Select(option => option.Answer).ToArray());
            Assert.Equal("rounds#1.items[0].implement", asked.RaisedBy?.Step?.ToString());
            Assert.Contains("reached its context", asked.ProblemShown!.Happened, StringComparison.Ordinal);

            var done = await AnswerAsync(kit, "compact");

            Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

            var attempts = kit.Agents.Of("implementer");

            Assert.Equal(2, attempts.Count);
            Assert.StartsWith("<handoff>\n", attempts[1].Prompt, StringComparison.Ordinal);
            Assert.Contains("reached its context", attempts[1].Prompt, StringComparison.Ordinal);
        }

        // The configuration's own answer: the same full context, asked of nobody - the question raised and answered by
        // the workflow's own answers in the same breath, the run reaching its end without waiting on a person.
        using (var configured = new TempDirectory())
        {
            var kit = await PrepareAsync(configured, script: filled);
            StoredConversation(kit);

            var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

            var answered = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Questions
                .Single(question => question.Kind == "contextLimit");

            Assert.Equal("answered", answered.State);
            Assert.Contains("compact", answered.Answers.Select(answer => answer.Answer));

            var attempts = kit.Agents.Of("implementer");

            Assert.Equal(2, attempts.Count);
            Assert.Contains("Built the report's frame.", attempts[1].Compaction!.Record, StringComparison.Ordinal);
            Assert.Contains("iterations are word for word", attempts[1].Compaction!.Record, StringComparison.Ordinal);
            Assert.Equal("session-one", attempts[1].Compaction!.Left);
        }
    }

    /// <summary>
    /// The engine's own fallback walk over the shipped flow's steps: a request the step's model declines moves the step
    /// to its fallback, its record naming both models; every model declining fails the step naming each; an account at
    /// its limit moves past the provider's one key to another company's model, in a new conversation given the record,
    /// and is remembered, so the next step on that account starts on its fallback and says so; a message written by a
    /// model nobody asked for fails the step naming both; a step that names a model outright is served by it, its
    /// provider, key and client found from it; and a first model none of whose keys is in the slot refuses the run
    /// before it starts, naming the provider and the variable, while validate-workflow reports the same key and passes.
    /// </summary>
    [Fact]
    public async Task ARequestTheStepsModelDeclines_MovesTheStepToItsFallbackItsRecordNamingBothModels_EveryModelDecliningFailsTheStepNamingEach_AnAccountAtItsLimitMovesToAnotherCompanysModelInANewConversationGivenTheRecord_AMessageByAModelNobodyAskedForFailsTheStepNamingBoth_AStepNamingAModelOutrightIsServedByIt_AndAFirstModelWithNoKeyRefusesTheRunWhileValidateWorkflowPasses()
    {
        // The engine's own walk over clients a test scripts: every step's agent answered by its standard output, and
        // the implementer's launches by what the session under test scripts.
        (Kit Kit, WalkingClient Claude, WalkingClient Glm) WalkingAsync(TempDirectory temp, Func<AgentRun, AgentRunResult> implement, string? keys = null, Func<string[], string[]>? edit = null)
        {
            WalkingClient? claude = null;
            WalkingClient? glm = null;

            var kit = PrepareAsync(
                temp,
                keys: keys,
                walking: harness =>
                {
                    AgentRunResult Answer(AgentRun run)
                        => run.Output?.Name switch
                        {
                            "triage" => Completed(Triage()),
                            "plan" => Completed(Plan(One())),
                            "plan-audit" => Completed(Audit()),
                            "implement" => implement(run),
                            "review-scope" => Completed(Scope()),
                            "review" => Completed(Review()),
                            "diagnosis" => Completed(Diagnosis(course: "retry")),
                            "self-audit" => Completed(SelfAudit(clean: true)),
                            "summary" => Completed(Scribe()),
                            _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "done"),
                        };

                    claude = new WalkingClient("claude-code", Answer);
                    glm = new WalkingClient("glm", Answer);

                    return new AgentRunner([claude, glm], harness.FileSystem, harness.Platform, TimeProvider.System);
                }).Result;

            if (edit is not null)
            {
                EditWorkflowAsync(kit, edit).Wait(Token);
            }

            return (kit, claude!, glm!);
        }

        static bool Serving(AgentRun run, string model)
            => string.Equals(run.Model, model, StringComparison.Ordinal) && run.Output?.Name == "implement";

        // A request the step's model declines: the step goes on with its fallback, and its record names both models.
        using (var declined = new TempDirectory())
        {
            var (kit, claude, glm) = WalkingAsync(
                declined,
                run => run.Model == "claude-sonnet-5-5"
                    ? AgentEndings.Declined("output tokens", "the request asks for more than the model serves", new Masking())
                    : Completed(Implement()));

            var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            var plan = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps.Single(step => step.Instance == "planning#1.plan");

            Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | plan output: {plan.Output?.ToJsonString() ?? "none"} | launched: {string.Join("; ", claude.Launches.Select(run => $"{run.Model}:{run.Output?.Name}"))} + {string.Join("; ", glm.Launches.Select(run => $"{run.Model}:{run.Output?.Name}"))} | {Shown(kit)}");

            Assert.Contains(claude.Launches, run => Serving(run, "claude-sonnet-5-5"));
            var moved = Assert.Single(glm.Launches, run => Serving(run, "glm-5.3"));

            var record = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps.Single(step => step.Instance == "rounds#1.items[0].implement");

            // The step's own record names both models and the turn each took the serving over from: the decline's
            // model served the first turn, the fallback the rest.
            var served = DssHarness.Core.FileSystem.JsonStateFile.Read<AgentRunResult>(
                kit.Harness.FileSystem,
                Path.Combine(moved.StepDirectory, AgentRunner.ResultFileName))!.Served;

            Assert.Equal([("claude-sonnet-5-5", 1), ("glm-5.3", 2)], [.. served.Select(entry => (entry.Model, entry.FromTurn))]);
            Assert.Equal("Done", record.State);
        }

        // Every model declining: the step fails, naming each, and the run asks the person.
        using (var refusing = new TempDirectory())
        {
            var (kit, claude, glm) = WalkingAsync(
                refusing,
                run => AgentEndings.Declined("output tokens", "the request asks for more than the model serves", new Masking()),
                edit: lines => [.. lines.Select(line => line.Trim() == "backoffSeconds: 30" ? line.Replace("backoffSeconds: 30", "backoffSeconds: 1") : line)]);

            var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            Assert.True(FlowExit.Paused == paused.ExitCode, $"{paused.Message} | {string.Join(" | ", paused.Details ?? [])} | {Shown(kit)}");

            var failed = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps.Single(step => step.Instance == "rounds#1.items[0].implement");

            Assert.True(nameof(FlowInstanceState.Paused) == failed.State, $"the implement record: {failed.State} {failed.Error} | {Shown(kit)}");
            Assert.Contains("declined", failed.Error, StringComparison.Ordinal);

            // Both models served it, each named with the turn it began at, before the step gave them both up.
            var implementAsked = claude.Launches.Concat(glm.Launches).Where(run => run.Output?.Name == "implement").ToList();
            var recordFile = Path.Combine(implementAsked[^1].StepDirectory, AgentRunner.ResultFileName);
            var ended = DssHarness.Core.FileSystem.JsonStateFile.Read<AgentRunResult>(kit.Harness.FileSystem, recordFile);

            Assert.True(ended is { Served.Count: > 0 },
                $"{recordFile} exists={File.Exists(recordFile)}; implement launches: {string.Join(", ", implementAsked.Select(run => $"{run.Model}@{run.StepDirectory}"))}; files there: {string.Join(", ", Directory.Exists(implementAsked[^1].StepDirectory) ? Directory.GetFiles(implementAsked[^1].StepDirectory) : [])}");

            var both = ended.Served;

            Assert.Equal([("claude-sonnet-5-5", 1), ("glm-5.3", 2)], [.. both.Select(entry => (entry.Model, entry.FromTurn))]);

            var asked = Assert.Single(kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!.Open);

            Assert.Equal("failure", asked.Kind);
        }

        // An account at its limit: past the provider's one key to another company's model, in a new conversation given
        // the record - and remembered, so the next step on that account starts on its fallback.
        using (var limited = new TempDirectory())
        {
            var (kit, claude, glm) = WalkingAsync(
                limited,
                run => run.Model == "claude-sonnet-5-5"
                    ? AgentEndings.AtLimit("the provider", new AgentAccountLimit(BalanceSpent: true, Key: "ANTHROPIC_API_KEY"), "the balance is spent", new Masking())
                    : Completed(Implement()));

            var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

            var carried = Assert.Single(glm.Launches, run => Serving(run, "glm-5.3"));

            // Another company's, in a new conversation: the limited provider's session is never handed across.
            Assert.Null(carried.Conversation);
        }

        // A message written by a model nobody asked for: the step fails, naming both.
        using (var ghost = new TempDirectory())
        {
            var (kit, claude, glm) = WalkingAsync(
                ghost,
                run => AgentEndings.WrongModel(run.Model!, "claude-fable-5-1", "its turn 1", new Masking()),
                edit: lines => [.. lines.Select(line => line.Trim() == "backoffSeconds: 30" ? line.Replace("backoffSeconds: 30", "backoffSeconds: 1") : line)]);

            var paused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            Assert.Equal(FlowExit.Paused, paused.ExitCode);

            var failed = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps.Single(step => step.Instance == "rounds#1.items[0].implement");

            Assert.True(nameof(FlowInstanceState.Paused) == failed.State, $"the implement record: {failed.State} {failed.Error} | {Shown(kit)}");
            Assert.Contains("glm-5.3", failed.Error, StringComparison.Ordinal);
            Assert.Contains("claude-fable-5-1", failed.Error, StringComparison.Ordinal);
        }

        // A step that names a model outright: its provider, key and client are found from it, and it serves the step.
        using (var named = new TempDirectory())
        {
            var (kit, claude, glm) = WalkingAsync(
                named,
                run => Completed(Implement()),
                keys: "ZAI_API_KEY=sk-test-two\n",
                edit: lines =>
                {
                    var at = Array.IndexOf(lines, "            tier: balanced");
                    return [.. lines[..at], "            model: glm-5.3", .. lines[(at + 1)..]];
                });

            var done = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            Assert.True(HarnessExit.Success == done.ExitCode, $"{done.Message} | {string.Join(" | ", done.Details ?? [])} | {Shown(kit)}");

            var served = Assert.Single(glm.Launches, run => Serving(run, "glm-5.3"));

            Assert.DoesNotContain(claude.Launches, run => run.Output?.Name == "implement");
            Assert.DoesNotContain(served.Fallback, entry => entry.Usable && entry.Model != "glm-5.3");
        }

        // A first model none of whose keys is in the slot: the step's agent run is refused before it starts, naming the
        // provider and the variable; validate-workflow reports the same key and passes.
        using (var keyless = new TempDirectory())
        {
            var (kit, claude, glm) = WalkingAsync(
                keyless,
                run => Completed(Implement()),
                keys: "ANTHROPIC_API_KEY=sk-test-one\n",
                edit: lines =>
                {
                    var at = Array.IndexOf(lines, "            tier: balanced");
                    return [.. lines[..at], "            model: glm-5.3", .. lines[(at + 1)..]];
                });

            var refused = await StartAsync(kit, "request=Ship the orders report.", "preapproved=yes");

            Assert.NotEqual(HarnessExit.Success, refused.ExitCode);

            var failed = kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps.Single(step => step.Instance == "rounds#1.items[0].implement");

            Assert.Contains("ZAI_API_KEY", failed.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(glm.Launches, run => run.Output?.Name == "implement");

            var validation = await new WorkflowValidationService(
                kit.Harness.ContextLoader,
                kit.Harness.FileSystem,
                kit.Harness.Platform,
                new DssHarness.Core.Secrets.AgentKeyStore(kit.Harness.FileSystem, kit.Harness.FilePermissions, kit.Harness.Platform),
                DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild).CheckAsync(keyless.Path, WorkflowTemplates.ShippedWorkflowName, Token);

            Assert.True(validation.Passed, string.Join("; ", validation.Workflows.Select(workflow => string.Join("; ", workflow.Lines))));
            Assert.Contains("ZAI_API_KEY", string.Join("; ", validation.Workflows.SelectMany(workflow => workflow.Lines)), StringComparison.Ordinal);
        }
    }

    /// <summary>Waits, a little at a time, for <paramref name="done"/>, and fails the test when it never comes.</summary>
    private static async Task WhenAsync(Func<bool> done, Func<string>? said = null)
    {
        for (var waited = 0; waited < 3000 && !done(); waited++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), Token);
        }

        Assert.True(done(), $"{said?.Invoke() ?? "what was waited for did not happen"} within five minutes");
    }

    /// <summary>The scripted agents: every request kept, answering by the definition each step works as.</summary>
    private sealed class Scripted : IAgentRunner
    {
        /// <summary>Every request the engine built, in order, kept under a lock: the run's steps, its advisor and its reporter ask from more than one thread.</summary>
        private readonly Lock _asked = new();

        /// <summary>Every request the engine built, in order.</summary>
        private readonly List<AgentStepRequest> _askedFor = [];

        /// <summary>Every request the engine built, in order.</summary>
        public IReadOnlyList<AgentStepRequest> Asked
        {
            get
            {
                lock (_asked)
                {
                    return [.. _askedFor];
                }
            }
        }

        /// <summary>The answers a test queues for a definition, taken before the standard answer.</summary>
        private readonly Dictionary<string, Queue<AgentRunResult>> _queued = new(StringComparer.Ordinal);

        /// <summary>The agents a test holds mid-run, each standing in for work slow enough to span a question.</summary>
        private readonly List<(string Definition, Func<AgentStepRequest, bool> When, Held Hold)> _held = [];

        /// <summary>What every completed agent of the script reports it used, where a test sets it; null where none does.</summary>
        public AgentUsage? DefaultUsage { get; set; }

        /// <summary>
        /// What one agent of the script reports it used, where a test sets it: asked of each request before
        /// <see cref="DefaultUsage"/>, and where it answers null that stands instead - so a test spends one turn of a
        /// run and no other, whatever the run asks in what order.
        /// </summary>
        public Func<AgentStepRequest, AgentUsage?>? Usage { get; set; }

        /// <summary>
        /// What one agent of the script says as it works, where a test sets it: asked of each request with the sink the
        /// engine records the attempt's conversation through - a test writes the stored stream a compacted or handed
        /// conversation goes on from, iteration by iteration.
        /// </summary>
        public Func<AgentStepRequest, IAgentEventSink?, Task>? Events { get; set; }

        /// <summary>
        /// The model the script's agents serve on, where a test names it: the walk the script stands in for moves a step
        /// to the first entry whose key the slot holds, and the script cannot see the slot. Null where the first usable
        /// entry is the model that serves.
        /// </summary>
        public string? ServingModel { get; set; }

        /// <summary>Queues <paramref name="answers"/> for the definition <paramref name="definition"/>, in order.</summary>
        public void Queue(string definition, params AgentRunResult[] answers)
            => _queued[definition] = new Queue<AgentRunResult>(answers);

        /// <summary>
        /// Holds every agent of <paramref name="definition"/> - or those <paramref name="when"/> names - until the hold is
        /// opened: an agent whose work a test spans a question with, answered by its standard answer once opened.
        /// </summary>
        public Held Hold(string definition, Func<AgentStepRequest, bool>? when = null)
        {
            ArgumentNullException.ThrowIfNull(definition);

            var held = new Held();

            lock (_asked)
            {
                _held.Add((definition, when ?? (static _ => true), held));
            }

            return held;
        }

        /// <summary>The requests the definition <paramref name="definition"/> was asked with, in order.</summary>
        public IReadOnlyList<AgentStepRequest> Of(string definition)
            => [.. Asked.Where(step => string.Equals(step.AgentDefinition, definition, StringComparison.Ordinal))];

        /// <inheritdoc/>
        public async Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
        {
            Held? held;

            lock (_asked)
            {
                _askedFor.Add(step);
                held = _held.FirstOrDefault(entry => string.Equals(entry.Definition, step.AgentDefinition, StringComparison.Ordinal) && entry.When(step)).Hold;
            }

            var result = held is { } waiting
                ? await HeldAnswerAsync(waiting, step, cancellationToken)
                : _queued.TryGetValue(step.AgentDefinition ?? string.Empty, out var queued) && queued.Count > 0 ? queued.Dequeue() : Standard(step);

            if (Events is { } says)
            {
                await says(step, events);
            }

            var spent = Usage?.Invoke(step) ?? DefaultUsage;

            if (spent is { } usage && events is not null && result.Status == AgentRunStatus.Completed)
            {
                // What the run's budget is charged with, as each turn reports it: the model the step serves on - the one
                // the test names, the walk's own choice where the script bypasses it - then one turn per answer.
                var serving = (step.Order ?? []).FirstOrDefault(entry => entry.Usable);
                var model = ServingModel ?? serving?.Model ?? step.Model ?? (step.Order is [{ } first, ..] ? first.Model : null) ?? "scripted";

                await events.EmitAsync(new SessionStarted(serving?.Client ?? step.Client ?? "scripted", model, string.Empty, [], [], false));
                await events.EmitAsync(new UsageReported(usage, usage));
            }

            return result;
        }

        /// <summary>The held agent's answer: its standard one once the hold is opened, and nothing where what ended it was cancelled.</summary>
        private static async Task<AgentRunResult> HeldAnswerAsync(Held held, AgentStepRequest step, CancellationToken cancellationToken)
        {
            var ended = await Task.WhenAny(held.Answer, Task.Delay(Timeout.Infinite, cancellationToken));

            if (ended != held.Answer)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await held.Answer ?? Standard(step);
        }

        /// <inheritdoc/>
        public async IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = await RunAsync(step, null, cancellationToken);
            yield break;
        }

        /// <summary>The standard answer of each definition the shipped flow runs.</summary>
        private static AgentRunResult Standard(AgentStepRequest step)
            => step.AgentDefinition switch
            {
                "triage" => Completed(Triage()),
                "planner" => Completed(Plan(One())),
                "plan-auditor" => Completed(Audit()),
                "implementer" => Completed(Implement()),
                "review-scope" => Completed(Scope()),
                "reviewer-correctness" or "reviewer-errors" or "reviewer-tests" or "reviewer-types" or "reviewer-comments" or "reviewer-simplicity" => Completed(Review()),
                "auditor" => Completed(SelfAudit(clean: true)),
                "diagnostician" => Completed(Diagnosis(course: "retry")),
                "scribe" => Completed(Scribe()),
                "advisor" => Advice(step),
                "reporter" => new AgentRunResult(AgentRunStatus.Completed, FinalText: "The run's report, in the person's words."),
                _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "done"),
            };

        /// <summary>
        /// The advisor's answer: the first of the question's own choices that takes no text, as the schema it was given
        /// spells them - a question of both kinds offers its shapes as <c>anyOf</c> - with a text where every choice
        /// takes one.
        /// </summary>
        private static AgentRunResult Advice(AgentStepRequest step)
        {
            var schema = JsonNode.Parse(step.Output!.Schema)!;
            var shapes = (schema["anyOf"]?.AsArray() ?? new JsonArray(schema)).OfType<JsonObject>().ToList();
            var shape = shapes.FirstOrDefault(candidate => candidate["required"]!.AsArray().All(member => member!.GetValue<string>() != "text")) ?? shapes[0];
            var answer = shape["properties"]!["answer"]!["enum"]!.AsArray().First()!.GetValue<string>();
            var output = new JsonObject { ["answer"] = answer, ["why"] = "the repository's bar decides it" };

            if (shape["required"]!.AsArray().Any(member => member!.GetValue<string>() == "text"))
            {
                output["text"] = "what the person decides";
            }

            return Completed(output);
        }
    }

    /// <summary>A completed run whose structured output is <paramref name="output"/>.</summary>
    private static AgentRunResult Completed(JsonObject output)
        => new(AgentRunStatus.Completed, StructuredOutput: output.ToJsonString());

    /// <summary>
    /// A machine step a test holds mid-run, its answer waiting until the test opens it: work long enough to span a
    /// pause, ended by what ends the run's asking of it where that comes first.
    /// </summary>
    private sealed class MachineHold
    {
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Ends the hold, the step answered by its standing answer.</summary>
        public void Open() => _opened.TrySetResult();

        /// <summary>Completed once the hold is opened.</summary>
        public Task Opened => _opened.Task;
    }

    /// <summary>
    /// An agent a test holds mid-run, its answer waiting until the test opens it: work slow enough to span a question,
    /// ended by what ends the run's asking of it where that comes first.
    /// </summary>
    private sealed class Held
    {
        private readonly TaskCompletionSource<AgentRunResult?> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Ends the hold, the agent answered by its standard answer - or by <paramref name="with"/>, where one is given.</summary>
        public void Open(AgentRunResult? with = null) => _opened.TrySetResult(with);

        /// <summary>The held agent's answer, once it is opened; null stands for its standard answer.</summary>
        public Task<AgentRunResult?> Answer => _opened.Task;
    }

    /// <summary>
    /// One client of the engine's own fallback walk, answering as a test scripts it per launch: every launch kept, with
    /// the model it served, the keys it was signed with and what the engine handed it, for a test to read the walk's
    /// moves. The walk itself is the engine's - a declined request, an account at its limit and a failed run move the
    /// step from entry to entry by the walk's own rules, over this client, never around it.
    /// </summary>
    private sealed class WalkingClient(string name, Func<AgentRun, AgentRunResult> answer) : IAgentClient
    {
        private readonly Lock _kept = new();

        /// <summary>Every launch this client was given, in order.</summary>
        private readonly List<AgentRun> _launches = [];

        /// <inheritdoc/>
        public string Name => name;

        /// <inheritdoc/>
        public AgentClientCapabilities Capabilities
            => name == "glm"
                ? DssHarness.Core.Agents.ZaiApi.ZaiApiClient.Descriptor
                : DssHarness.Core.Agents.ClaudeCode.ClaudeCodeClient.Descriptor;

        /// <summary>Every launch this client was given, in order.</summary>
        public IReadOnlyList<AgentRun> Launches
        {
            get
            {
                lock (_kept)
                {
                    return [.. _launches];
                }
            }
        }

        /// <inheritdoc/>
        public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(run);

            AgentRunResult answered;

            lock (_kept)
            {
                _launches.Add(run);
                answered = answer(run);
            }

            // The launch's conversation, as the client's own events say it: one turn of work, recorded by the runner
            // into the stored stream a move to another company's model goes on from through the engine's record of it.
            if (events is not null && run.Output?.Name == "implement")
            {
                await events.EmitAsync(new TurnStarted(1));
                await events.EmitAsync(new AssistantMessage($"Working on {run.Output?.Name} on {run.Model}.", []));
            }

            // What the client served, as the real ones say it: the model of this launch, from its first turn.
            if (answered.Served.Count == 0 && run.Model is { } model)
            {
                answered = answered with { Served = [new AgentServedModel(model, 1)], Turns = Math.Max(answered.Turns, 1) };
            }

            return answered;
        }
    }

    /// <summary>
    /// A file system that stands where the engine's own does and kills it at a write a test names: past the cut, the
    /// first thing the engine writes or removes of the run throws - nothing catches it, for the process is gone - so
    /// the session dies as a killed process does, its records standing as they were, while every write before it lands
    /// and is counted and named, for a test to cut at each kind of write the run makes. What no session of the run
    /// owns - the owner, the questions and their answers, the inbox, the log - passes through untouched, and nothing
    /// past the cut reaches them either, so the run's next session starts from what stands.
    /// </summary>
    private sealed class CutFiles(IFileSystem inner, string runDirectory) : IFileSystem
    {
        /// <summary>The engine's process dying: an exception nothing catches, for no process is left to catch it.</summary>
        public sealed class Killed(string what) : Exception(what);

        private readonly Lock _kept = new();

        /// <summary>Every write the engine made of the run, each by its file's name, in order.</summary>
        private readonly List<string> _writes = [];

        /// <summary>How many of the run's writes reach the disk; every one, where this is <see cref="int.MaxValue"/>.</summary>
        public int Cut { get; set; } = int.MaxValue;

        /// <summary>Every write the engine made of the run, each by its file's name, in order.</summary>
        public IReadOnlyList<string> Writes
        {
            get
            {
                lock (_kept)
                {
                    return [.. _writes];
                }
            }
        }

        /// <summary>Whether <paramref name="path"/> is the run's own, this session's or a later one's: under the run's directory, and no file every session of the run owns in common.</summary>
        private bool WritesTheRun(string path)
            => path.StartsWith(
                runDirectory.EndsWith(Path.DirectorySeparatorChar) ? runDirectory : runDirectory + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && Path.GetFileName(path) is not (OrchestratorLayout.OwnerFileName or OrchestratorLayout.QuestionsFileName or OrchestratorLayout.InboxFileName);

        /// <summary>Takes one write of the run: counted and named, and past the cut it kills the process - the write never landing, as one the engine's process ended beside.</summary>
        private void Take(string path)
        {
            lock (_kept)
            {
                if (!WritesTheRun(path))
                {
                    return;
                }

                _writes.Add(Path.GetFileName(path));

                if (_writes.Count > Cut)
                {
                    throw new Killed($"'{path}', the run's own record, was the write the engine's process was killed beside");
                }
            }
        }

        /// <inheritdoc/>
        public void WriteAtomic(string path, Action<Stream> write)
        {
            Take(path);

            inner.WriteAtomic(path, write);
        }

        /// <inheritdoc/>
        public async Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        {
            Take(path);

            await inner.WriteAllBytesAtomicAsync(path, contents, cancellationToken);
        }

        /// <inheritdoc/>
        public void ReplaceFile(string source, string destination)
        {
            Take(destination);

            inner.ReplaceFile(source, destination);
        }

        /// <inheritdoc/>
        public void DeleteFile(string path)
        {
            lock (_kept)
            {
                if (WritesTheRun(path) && _writes.Count > Cut)
                {
                    // A file the run held stands as the cut left it: what the engine removed after the write the cut
                    // stands at, its process had not lived to remove.
                    throw new Killed($"'{path}', the run's own record, was the removal the engine's process was killed beside");
                }
            }

            inner.DeleteFile(path);
        }

        /// <inheritdoc/>
        public bool FileExists(string path) => inner.FileExists(path);

        /// <inheritdoc/>
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        /// <inheritdoc/>
        public PathKind KindOf(string path) => inner.KindOf(path);

        /// <inheritdoc/>
        public string ResolveLinks(string path) => inner.ResolveLinks(path);

        /// <inheritdoc/>
        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        /// <inheritdoc/>
        public string CopyToTemporaryFile(string path) => inner.CopyToTemporaryFile(path);

        /// <inheritdoc/>
        public void CopyFile(string source, string destination, bool overwrite = false) => inner.CopyFile(source, destination, overwrite);

        /// <inheritdoc/>
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);

        /// <inheritdoc/>
        public void MoveDirectory(string source, string destination) => inner.MoveDirectory(source, destination);

        /// <inheritdoc/>
        public bool IsLink(string path) => inner.IsLink(path);

        /// <inheritdoc/>
        public long DirectorySize(string path) => inner.DirectorySize(path);

        /// <inheritdoc/>
        public DiskSpace SpaceAt(string path) => inner.SpaceAt(path);

        /// <inheritdoc/>
        public IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);

        /// <inheritdoc/>
        public IEnumerable<string> EnumerateReachableFiles(string path, Action<string, Exception> unread) => inner.EnumerateReachableFiles(path, unread);

        /// <inheritdoc/>
        public IEnumerable<WrittenFile> EnumerateWrittenFiles(string path) => inner.EnumerateWrittenFiles(path);

        /// <inheritdoc/>
        public IEnumerable<string> EnumerateDirectoryLinks(string path) => inner.EnumerateDirectoryLinks(path);

        /// <inheritdoc/>
        public IReadOnlyList<string> RemoveJunctions(string path) => inner.RemoveJunctions(path);

        /// <inheritdoc/>
        public IReadOnlyList<HeldEntry> FindHeld(string path, CancellationToken cancellationToken = default) => inner.FindHeld(path, cancellationToken);

        /// <inheritdoc/>
        public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);

        /// <inheritdoc/>
        public Stream OpenRead(string path) => inner.OpenRead(path);

        /// <inheritdoc/>
        public string FinalPathOf(Stream opened) => inner.FinalPathOf(opened);

        /// <inheritdoc/>
        public string FinalPathOfDirectory(string directory) => inner.FinalPathOfDirectory(directory);

        /// <inheritdoc/>
        public HeldDirectory Hold(string anchor, string directory, bool make, string? asked = null) => inner.Hold(anchor, directory, make, asked);

        /// <inheritdoc/>
        public IAppendFile OpenForAppend(string path) => inner.OpenForAppend(path);

        /// <inheritdoc/>
        public DateTime LastWriteTimeUtc(string path) => inner.LastWriteTimeUtc(path);

        /// <inheritdoc/>
        public FileStanding? StandingOf(string path) => inner.StandingOf(path);

        /// <inheritdoc/>
        public DateTime CreationTimeUtc(string path) => inner.CreationTimeUtc(path);

        /// <inheritdoc/>
        public void SetLastWriteTimeUtc(string path, DateTime writtenUtc) => inner.SetLastWriteTimeUtc(path, writtenUtc);

        /// <inheritdoc/>
        public void ProtectSecretFile(string path) => inner.ProtectSecretFile(path);
    }

    



    /// <summary>A run that asks the person one question, with two choices and one recommended.</summary>
    private static AgentRunResult Asked(string question)
        => new(AgentRunStatus.Asked)
        {
            Questions =
            [
                new AgentQuestion(
                    question,
                    "the request does not say",
                    "the request, read twice",
                    "the plan cannot be written without it",
                    ["the weekly one", "the monthly one"],
                    "the weekly one",
                    "the bar prefers the smaller thing"),
            ],
        };

    /// <summary>The triage of a request that ships the orders report, with the stops, the pre-approval and the rebuilds a test gives.</summary>
    private static JsonObject Triage(IReadOnlyList<JsonObject>? stops = null, string? sentence = null, IReadOnlyList<JsonObject>? rebuilds = null)
        => new()
        {
            ["title"] = "Ship the orders report",
            ["kind"] = "feature",
            ["summary"] = "The orders report, shipped.",
            ["touches"] = new JsonArray("src/**"),
            ["plan"] = new JsonObject { ["found"] = false, ["where"] = "none", ["file"] = "", ["text"] = "" },
            ["rebuilds"] = new JsonArray([.. rebuilds ?? []]),
            ["preapproval"] = new JsonObject { ["given"] = sentence is not null, ["sentence"] = sentence ?? "" },
            ["ambiguities"] = new JsonArray(),
            ["blockers"] = new JsonArray(),
            ["stops"] = new JsonArray([.. stops ?? []]),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>One thing triage says the request itself asks to have built again.</summary>
    private static JsonObject Rebuild(string what, string path)
        => new()
        {
            ["what"] = what,
            ["path"] = path,
        };

    /// <summary>The one stop triage puts to the person: a question with its choices and the one recommended.</summary>
    private static JsonObject Stop()
        => new()
        {
            ["question"] = "Is the orders report shipped weekly or monthly?",
            ["problem"] = "the request does not say",
            ["tried"] = "the request, read twice",
            ["means"] = "the plan cannot be written without it",
            ["options"] = new JsonArray("the weekly one", "the monthly one"),
            ["recommended"] = "the weekly one",
            ["why"] = "the bar prefers the smaller thing",
        };

    /// <summary>A plan of <paramref name="items"/>, made here, with its definition of done.</summary>
    private static JsonObject Plan(params JsonObject[] items)
        => new()
        {
            ["summary"] = "One item ships the report.",
            ["done"] = "the report ships; its test holds",
            ["source"] = "made",
            ["departures"] = new JsonArray(),
            ["items"] = new JsonArray([.. items]),
            ["scope"] = new JsonArray(),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>One work item, on the paths it owns, reusing the tool the tree holds - replacing what a test names, where it does.</summary>
    private static JsonObject One(string name = "one", string owned = "src/one/**", string reuses = "src/tool.cs", string? replaces = null)
        => new()
        {
            ["name"] = name,
            ["title"] = "The report",
            ["instructions"] = "Write the report's code.",
            ["done"] = "the report ships",
            ["ownedPaths"] = new JsonArray(owned),
            ["forbiddenPaths"] = new JsonArray(),
            ["reuses"] = new JsonArray(new JsonObject { ["path"] = reuses, ["what"] = "the tool" }),
            ["creates"] = new JsonArray(),
            ["replaces"] = replaces is null ? new JsonArray() : new JsonArray(new JsonObject { ["path"] = replaces, ["what"] = "the report, built again" }),
            ["testFilter"] = "",
        };

    /// <summary>An audit that passes or refuses, with its findings and what it checked.</summary>
    private static JsonObject Audit(bool go = true, params string[] findings)
        => new()
        {
            ["go"] = go,
            ["findings"] = new JsonArray([.. findings]),
            ["checked"] = new JsonArray("each item's reused path is in the tree"),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>An implementer's report: everything done, nothing blocked, nothing new to the registry - with what it declines and what it cannot do where a test gives them.</summary>
    private static JsonObject Implement(IReadOnlyList<JsonObject>? blocked = null, IReadOnlyList<JsonObject>? declined = null)
        => new()
        {
            ["summary"] = "Built the item.",
            ["done"] = new JsonArray(new JsonObject { ["statement"] = "the report ships", ["holds"] = true, ["shows"] = "its code, read again" }),
            ["claims"] = new JsonObject { ["built"] = "src/one/one.cs", ["tested"] = "nothing run: the repository keeps no legs" },
            ["reuse"] = "",
            ["newRows"] = new JsonArray(),
            ["blocked"] = new JsonArray([.. blocked ?? []]),
            ["declined"] = new JsonArray([.. declined ?? []]),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>What the item's builder says it cannot do, with what stands in the way and what would let it be done.</summary>
    private static JsonObject Blocked()
        => new()
        {
            ["what"] = "The report frame",
            ["why"] = "The frame file is a decision nobody made",
            ["needs"] = "a decision on the frame, from the person",
        };

    /// <summary>A review finding the builder holds wrong, with why.</summary>
    private static JsonObject Declined(string finding)
        => new()
        {
            ["finding"] = finding,
            ["why"] = "the finding is wrong: the frame is decided elsewhere",
        };

    /// <summary>A review scope that keeps every aspect but correctness out.</summary>
    private static JsonObject Scope()
        => new()
        {
            ["errors"] = false,
            ["tests"] = false,
            ["types"] = false,
            ["comments"] = false,
            ["simplicity"] = false,
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>A review scope with each aspect as a test gives it.</summary>
    private static JsonObject Scope(bool errors, bool tests, bool types, bool comments, bool simplicity)
        => new()
        {
            ["errors"] = errors,
            ["tests"] = tests,
            ["types"] = types,
            ["comments"] = comments,
            ["simplicity"] = simplicity,
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>A review that applies and finds nothing - or the finding a test gives, where it does not apply where a test says so.</summary>
    private static JsonObject Review(JsonObject? finding = null, bool applies = true)
    {
        var findings = new JsonArray();

        if (finding is not null)
        {
            findings.Add(finding);
        }

        return new JsonObject
        {
            ["applies"] = applies,
            ["findings"] = findings,
            ["preExisting"] = new JsonArray(),
        };
    }

    /// <summary>One finding of a review: what goes wrong, where, how much it matters and whether the product ships it.</summary>
    private static JsonObject Finding(string aspect = "correctness", string severity = "medium", string text = "The report ships without its frame, so a week of orders reads as one run", bool production = true)
        => new()
        {
            ["aspect"] = aspect,
            ["path"] = "src/one/one.cs:1",
            ["text"] = text,
            ["severity"] = severity,
            ["production"] = production,
        };

    /// <summary>The run's own audit: clean or not, with the next round's items where it is not.</summary>
    private static JsonObject SelfAudit(bool clean, JsonObject[]? items = null)
        => new()
        {
            ["clean"] = clean,
            ["findings"] = clean ? new JsonArray() : new JsonArray("the report does not ship"),
            ["unverified"] = new JsonArray(),
            ["items"] = new JsonArray([.. items ?? []]),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>A diagnosis: what course it takes, with the choices it offers a person and the remedy items it names where it fixes.</summary>
    private static JsonObject Diagnosis(string course, JsonObject[]? items = null, JsonObject[]? choices = null, string summary = "The check reads a job the workflow no longer runs")
        => new()
        {
            ["course"] = course,
            ["summary"] = summary,
            ["cause"] = "the workflow's file names a job that is gone",
            ["evidence"] = new JsonArray("the check's output: build-reports was not found"),
            ["items"] = new JsonArray([.. items ?? []]),
            ["choices"] = new JsonArray([.. choices ?? []]),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>A remedy item, owning its own paths and reusing the tool the tree holds.</summary>
    private static JsonObject Remedy(string name, string owned)
        => new()
        {
            ["name"] = name,
            ["title"] = "Name the job the workflow runs",
            ["instructions"] = "Write the workflow's job name so the check finds it.",
            ["done"] = "the check reads the workflow's job",
            ["ownedPaths"] = new JsonArray(owned),
            ["forbiddenPaths"] = new JsonArray(),
            ["reuses"] = new JsonArray(new JsonObject { ["path"] = "src/tool.cs", ["what"] = "the tool" }),
            ["creates"] = new JsonArray(),
            ["replaces"] = new JsonArray(),
            ["testFilter"] = "",
        };

    /// <summary>One choice a diagnosis offers a person, with what it leads to.</summary>
    private static JsonObject Choice(string what, string leadsTo)
        => new()
        {
            ["choice"] = what,
            ["leadsTo"] = leadsTo,
        };

    /// <summary>The prioritizer's choice of the work: the request it writes, why it comes first, its blockers and the stops it puts to a person.</summary>
    private static JsonObject Priority(IReadOnlyList<string>? blockers = null, IReadOnlyList<JsonObject>? stops = null)
        => new()
        {
            ["title"] = "Ship the orders report",
            ["request"] = "Ship the orders report.",
            ["rationale"] = "The handoff names it first, and nothing stands in its way the run cannot do.",
            ["blockers"] = new JsonArray([.. blockers ?? []]),
            ["stops"] = new JsonArray([.. stops ?? []]),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>The scribe's summary.</summary>
    private static JsonObject Scribe()
        => new()
        {
            ["summary"] = "The handoff, written.",
            ["newRows"] = new JsonArray(),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>One machine step a scripted run was asked to run, as it was handed it.</summary>
    private sealed record MachineRun(string Instance, WorkflowStep Step, FlowStepResult Result);

    /// <summary>
    /// The machine steps of the shipped flow, answered by the script: every run of every type kept as it was handed
    /// in, each answered by the results a test queued for its type in turn - past them, by the type's own standing
    /// answer, which carries what the flow's later steps read of it.
    /// </summary>
    private sealed class MachineSteps
    {
        private readonly Lock _kept = new();

        /// <summary>Every run the engine asked, in order, under a lock: the run's steps ask from more than one thread.</summary>
        private readonly List<MachineRun> _runs = [];

        /// <summary>The results a test queued for a type, taken before the type's standing answer.</summary>
        private readonly Dictionary<string, Queue<FlowStepResult>> _queued = new(StringComparer.Ordinal);

        /// <summary>The machine steps a test holds mid-run, each standing in for a build long enough to span a pause.</summary>
        private readonly List<(string Type, Func<FlowStepRun, bool> When, MachineHold Hold)> _held = [];

        /// <summary>
        /// Holds every step of <paramref name="type"/> - or those <paramref name="when"/> names - until the hold is
        /// opened: a machine step whose work a test spans a pause with, ended by what ends the run's asking of it
        /// where that comes first.
        /// </summary>
        public MachineHold Hold(string type, Func<FlowStepRun, bool>? when = null)
        {
            ArgumentNullException.ThrowIfNull(type);

            var held = new MachineHold();

            lock (_kept)
            {
                _held.Add((type, when ?? (static _ => true), held));
            }

            return held;
        }

        /// <summary>
        /// What one machine step answers, where a test sets it: asked of each run before the queue and the standing
        /// answer, its null falling through to them. Asked of the run's visit, so a step a crash or a resume asks
        /// again is answered as it was the first time.
        /// </summary>
        public Func<FlowStepRun, FlowStepResult?>? Answer { get; set; }

        /// <summary>Every run the engine asked, in order.</summary>
        public IReadOnlyList<MachineRun> Runs
        {
            get
            {
                lock (_kept)
                {
                    return [.. _runs];
                }
            }
        }

        /// <summary>The runs of one type, in order.</summary>
        public IReadOnlyList<MachineRun> Of(string type)
            => [.. Runs.Where(run => string.Equals(run.Step.Type, type, StringComparison.Ordinal))];

        /// <summary>Queues <paramref name="results"/> for the type <paramref name="type"/>, answered in turn before its standing answer.</summary>
        public void Queue(string type, params FlowStepResult[] results)
            => _queued[type] = new Queue<FlowStepResult>(results);

        /// <summary>
        /// The run's store and worktrees, where the script is given them: a <c>delete-agent</c> step's standing answer
        /// removes the agent's worktree and marks the agent deleted in the store, as the real one would, so the run's
        /// open agents are counted as the flow budgets them.
        /// </summary>
        internal OrchestrationStore? Store { get; set; }

        /// <summary>The worktrees the script removes an agent's worktree through, where it is given them.</summary>
        internal IWorktreeService? Worktrees { get; set; }

        /// <summary>The runner this script serves a type as.</summary>
        public IFlowStepTypeRunner Serving(string type)
            => new Served(this, type);

        /// <summary>The standing answer of a type: what its later steps read of it - a push's commit, a pull request's url, a fold's paths and rows, a check's verdict.</summary>
        private static FlowStepResult Standing(string type, FlowStepRun run)
            => type switch
            {
                "push" => Ended(new JsonObject { ["pushed"] = true, ["remote"] = "origin", ["branch"] = "work", ["sha"] = $"sha-{run.Instance}" }),
                "pull-request" => Ended(new JsonObject { ["url"] = $"https://forge/{run.Instance}/pull/1", ["number"] = 1 }),
                "commit" => Ended(new JsonObject { ["committed"] = true, ["sha"] = $"sha-{run.Instance}", ["paths"] = new JsonArray(), ["ignored"] = new JsonArray() }),
                "fold" or "delete-agent" or "refresh" => Ended(new JsonObject { ["written"] = new JsonArray(), ["deleted"] = new JsonArray(), ["merged"] = new JsonArray(), ["registries"] = new JsonArray(), ["rows"] = new JsonArray() }),
                "ci-read" => Ended(new JsonObject { ["passed"] = true, ["branch"] = run.Step.Branch, ["matrixRan"] = true, ["legs"] = new JsonArray(), ["red"] = new JsonArray() }),
                "ci-wait" => Ended(new JsonObject { ["passed"] = true, ["commit"] = "sha-pushed", ["checks"] = new JsonArray(), ["red"] = new JsonArray(), ["pending"] = new JsonArray(), ["unsettled"] = new JsonArray() }),
                "build" or "test" or "run" or "check-mutations" => Green(),
                _ => Ended(new JsonObject { ["passed"] = true }),
            };

        /// <summary>A leg step's green answer, as its type answers it: every leg green, nothing red, its records in the run's own directory.</summary>
        internal static FlowStepResult Green()
            => Ended(
                new JsonObject
                {
                    ["passed"] = true,
                    ["exitCode"] = HarnessExit.Success,
                    ["verdicts"] = new JsonArray(new JsonObject { ["leg"] = "local", ["verdict"] = "green" }),
                    ["red"] = new JsonArray(),
                    ["runDirectory"] = string.Empty,
                    ["isolation"] = new JsonObject { ["mode"] = "none" },
                });

        /// <summary>A leg step's red answer: one red leg, its phase, what it said and its last lines.</summary>
        internal static FlowStepResult Red(int code, string what)
            => FlowStepResult.Ended(
                passed: false,
                code,
                what,
                new JsonObject
                {
                    ["passed"] = false,
                    ["exitCode"] = code,
                    ["verdicts"] = new JsonArray(new JsonObject { ["leg"] = "local", ["verdict"] = "red" }),
                    ["red"] = new JsonArray(new JsonObject { ["leg"] = "local", ["verdict"] = "red", ["phase"] = "test", ["detail"] = what, ["tail"] = new JsonArray("1 failed") }),
                    ["runDirectory"] = string.Empty,
                    ["isolation"] = new JsonObject { ["mode"] = "none" },
                });

        /// <summary>A leg step's answer that found red and holds it, as failOn never holds a check's: its work done, no failure, the red kept for the prompts after it.</summary>
        internal static FlowStepResult RedHeld(string detail)
            => Ended(
                new JsonObject
                {
                    ["passed"] = false,
                    ["exitCode"] = HarnessExit.CommandFailed,
                    ["verdicts"] = new JsonArray(new JsonObject { ["leg"] = "local", ["verdict"] = "red" }),
                    ["red"] = new JsonArray(new JsonObject { ["leg"] = "local", ["verdict"] = "red", ["phase"] = "test", ["detail"] = detail, ["tail"] = new JsonArray("1 failed") }),
                    ["runDirectory"] = string.Empty,
                    ["isolation"] = new JsonObject { ["mode"] = "none" },
                });

        /// <summary>A leg step's answer whose inputs moved: no verdict of its own, saying nothing about the code, held for the retry table to run again.</summary>
        internal static FlowStepResult Moved(string what)
            => FlowStepResult.Ended(
                passed: false,
                LegExit.InputsMoved,
                what,
                new JsonObject
                {
                    ["passed"] = false,
                    ["exitCode"] = LegExit.InputsMoved,
                    ["verdicts"] = new JsonArray(new JsonObject { ["leg"] = "local", ["verdict"] = "inputsMoved" }),
                    ["red"] = new JsonArray(),
                    ["runDirectory"] = string.Empty,
                    ["isolation"] = new JsonObject { ["mode"] = "none" },
                });

        private static FlowStepResult Ended(JsonObject output)
            => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: output);

        /// <summary>One type this script serves: every run kept, each answered by the queue, else by the type's standing answer.</summary>
        private sealed class Served(MachineSteps script, string type) : IFlowStepTypeRunner
        {
            /// <inheritdoc/>
            public string Type => type;

            /// <inheritdoc/>
            public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(run);

                FlowStepResult result;
                MachineHold? held;

                lock (script._kept)
                {
                    result = script.Answer?.Invoke(run) ?? (script._queued.TryGetValue(type, out var queued) && queued.Count > 0 ? queued.Dequeue() : Standing(type, run));
                    script._runs.Add(new MachineRun(run.Instance, run.Step, result));
                    held = script._held.FirstOrDefault(entry => string.Equals(entry.Type, type, StringComparison.Ordinal) && entry.When(run)).Hold;
                }

                // The held step's answer waits until the test opens it, and ends where the run's asking of it is
                // cancelled first - a safe pause that stops the machine steps, a stop - as the agents' holds do.
                if (held is { } waiting)
                {
                    var ended = await Task.WhenAny(waiting.Opened, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);

                    if (ended != waiting.Opened)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }

                if (type == "delete-agent" && script.Store is { } store && run.Place is { } place && (run.Target ?? run.WorksIn) is { } deleted)
                {
                    var layout = new OrchestratorLayout(place.Run, Path.Combine(place.Repository, ".orchestrators", place.Run));

                    if (script.Worktrees is { } worktrees)
                    {
                        await worktrees.DeleteAsync(place.Repository, WorktreeAddress.Nested(place.Run, deleted).Name, force: true, deleteEvidence: true, discardUncommitted: true, cancellationToken);
                    }

                    if (store.ReadAgent(layout, deleted) is { } record)
                    {
                        store.WriteAgent(layout, record with { State = AgentStates.Deleted, DeletedAt = DateTimeOffset.Now, Abandoned = false });
                    }
                }

                return result;
            }
        }
    }
}
