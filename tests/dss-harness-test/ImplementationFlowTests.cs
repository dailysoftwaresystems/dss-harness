using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Ci;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

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
    private static async Task<Kit> PrepareAsync(TempDirectory temp, Action<Scripted>? script = null, string? keys = null, Ground? ground = null, Action<MachineSteps>? machine = null)
    {
        var harness = new HarnessFactory();
        var template = AgentProviderTemplates.Read();
        var platform = new DssHarness.Core.Platform.HostPlatform();
        var groundHeld = ground ?? new Ground();

        await harness.InitializeHarnessAsync(
            temp.Path,
            Token,
            new HarnessConfig
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
            });

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
        var steps = new MachineSteps();

        script?.Invoke(agents);
        machine?.Invoke(steps);

        return new Kit(harness, temp, agents, steps, Service(harness, agents, steps));
    }

    /// <summary>The service over the engine's own runners: every type the flow's head works through for real, the rest answered by the machine script.</summary>
    private static FlowService Service(HarnessFactory harness, Scripted agents, MachineSteps machine)
    {
        var keys = new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform);
        var create = new CreateAgentRunner(harness.ContextLoader, harness.Platform, harness.OrchestrationStore, harness.AgentService);
        var reason = new ReasonStepRunner(
            harness.ContextLoader,
            harness.Platform,
            harness.OrchestrationStore,
            create,
            agents,
            harness.FileSystem,
            keys,
            new GhProcess(harness.ProcessRunner),
            harness.Output,
            TimeProvider.System,
            new TestIsolation(),
            harness.GitClient,
            harness.ConfigStore);

        IFlowStepRunner runner = new FlowStepRunners(
            [
                new OrientRunner(harness.ContextLoader, harness.Platform, harness.GitClient, harness.FileSystem, harness.OrchestrationStore, harness.WorktreeService, harness.RunLock, new TreeHolders(harness.RunLock, harness.Slots, harness.Identity)),
                new GraphifyCodebaseRunner(harness.ContextLoader, harness.Platform, harness.CodebaseGraph, harness.PathBudget, DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild, harness.OrchestrationStore),
                new PlanCheckRunner(harness.ContextLoader, harness.Platform, new PlanValidator(harness.FileSystem, harness.PathBudget, harness.Platform)),
                create,
                reason,
                .. "build test run check-mutations anchor-balance anchor-citations ci-read ci-wait fold delete-agent refresh rebase commit push pull-request"
                    .Split(' ')
                    .Select(type => machine.Serving(type)),
            ],
            harness.GraphSyncs);

        return new(
            harness.ContextLoader,
            harness.Platform,
            harness.FileSystem,
            harness.OrchestrationStore,
            harness.Identity,
            new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient),
            new WorkflowValidationService(harness.ContextLoader, harness.FileSystem, harness.Platform, keys, DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild),
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

    /// <summary>An answer to the run's open question, within the bound.</summary>
    private static Task<CommandOutcome> AnswerAsync(Kit kit, string answer, string? text = null)
        => kit.Service.AnswerAsync(kit.Tree.Path, "r1", answer, question: null, text: text, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>An answer to one question the run holds open, by its id, within the bound.</summary>
    private static Task<CommandOutcome> AnswerOneAsync(Kit kit, string question, string answer, string? text = null)
        => kit.Service.AnswerAsync(kit.Tree.Path, "r1", answer, question: question, text: text, textFile: null, again: false, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

    /// <summary>The run's open questions and its last steps, as a diagnostic an assertion carries when it fails.</summary>
    private static string Shown(Kit kit)
    {
        var questions = kit.Harness.OrchestrationStore.ReadFlowQuestions(kit.RunOf())!;
        var open = string.Join(
            " | ",
            questions.Open.Select(question => $"{question.Id} kind {question.Kind} by {question.RaisedBy?.Step}: {question.ProblemShown?.Happened}"));
        var steps = string.Join(
            " | ",
            kit.Harness.OrchestrationStore.ReadFlow(kit.RunOf())!.Steps
                .OrderByDescending(step => step.StartedAt ?? DateTimeOffset.MinValue)
                .Take(14)
                .Select(step => $"{step.Instance} {step.State}{(step.WaitsOn.Count > 0 ? $" waits[{string.Join(",", step.WaitsOn)}]" : string.Empty)} {(step.Exit is { } exit ? exit.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)} {step.Error ?? string.Empty}"));
        var eventsPath = Path.Combine(kit.RunOf().Directory, "events.jsonl");
        var events = System.IO.File.Exists(eventsPath)
            ? string.Join(" | ", System.IO.File.ReadAllLines(eventsPath).TakeLast(6))
            : "no events";

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

        /// <summary>Queues <paramref name="answers"/> for the definition <paramref name="definition"/>, in order.</summary>
        public void Queue(string definition, params AgentRunResult[] answers)
            => _queued[definition] = new Queue<AgentRunResult>(answers);

        /// <summary>The requests the definition <paramref name="definition"/> was asked with, in order.</summary>
        public IReadOnlyList<AgentStepRequest> Of(string definition)
            => [.. Asked.Where(step => string.Equals(step.AgentDefinition, definition, StringComparison.Ordinal))];

        /// <inheritdoc/>
        public Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
        {
            lock (_asked)
            {
                _askedFor.Add(step);
            }

            return Task.FromResult(
                _queued.TryGetValue(step.AgentDefinition ?? string.Empty, out var queued) && queued.Count > 0 ? queued.Dequeue() : Standard(step));
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

    /// <summary>The triage of a request that ships the orders report, with the stops and the pre-approval a test gives.</summary>
    private static JsonObject Triage(IReadOnlyList<JsonObject>? stops = null, string? sentence = null)
        => new()
        {
            ["title"] = "Ship the orders report",
            ["kind"] = "feature",
            ["summary"] = "The orders report, shipped.",
            ["touches"] = new JsonArray("src/**"),
            ["plan"] = new JsonObject { ["found"] = false, ["where"] = "none", ["file"] = "", ["text"] = "" },
            ["rebuilds"] = new JsonArray(),
            ["preapproval"] = new JsonObject { ["given"] = sentence is not null, ["sentence"] = sentence ?? "" },
            ["ambiguities"] = new JsonArray(),
            ["blockers"] = new JsonArray(),
            ["stops"] = new JsonArray([.. stops ?? []]),
            ["preExisting"] = new JsonArray(),
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

    /// <summary>One work item, on the paths it owns, reusing the tool the tree holds.</summary>
    private static JsonObject One(string name = "one", string owned = "src/one/**", string reuses = "src/tool.cs")
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
            ["replaces"] = new JsonArray(),
            ["testFilter"] = "",
        };

    /// <summary>An audit that passes, with what it checked.</summary>
    private static JsonObject Audit()
        => new()
        {
            ["go"] = true,
            ["findings"] = new JsonArray(),
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

    /// <summary>A review that applies and finds nothing - or the finding a test gives.</summary>
    private static JsonObject Review(JsonObject? finding = null)
    {
        var findings = new JsonArray();

        if (finding is not null)
        {
            findings.Add(finding);
        }

        return new JsonObject
        {
            ["applies"] = true,
            ["findings"] = findings,
            ["preExisting"] = new JsonArray(),
        };
    }

    /// <summary>One finding of a review: what goes wrong, where, how much it matters and whether the product ships it.</summary>
    private static JsonObject Finding(string aspect = "correctness", string severity = "medium")
        => new()
        {
            ["aspect"] = aspect,
            ["path"] = "src/one/one.cs:1",
            ["text"] = "The report ships without its frame, so a week of orders reads as one run",
            ["severity"] = severity,
            ["production"] = true,
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
    private static JsonObject Diagnosis(string course, JsonObject[]? items = null, JsonObject[]? choices = null)
        => new()
        {
            ["course"] = course,
            ["summary"] = "The check reads a job the workflow no longer runs",
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

        private static FlowStepResult Ended(JsonObject output)
            => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: output);

        /// <summary>One type this script serves: every run kept, each answered by the queue, else by the type's standing answer.</summary>
        private sealed class Served(MachineSteps script, string type) : IFlowStepTypeRunner
        {
            /// <inheritdoc/>
            public string Type => type;

            /// <inheritdoc/>
            public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(run);

                FlowStepResult result;

                lock (script._kept)
                {
                    result = script._queued.TryGetValue(type, out var queued) && queued.Count > 0 ? queued.Dequeue() : Standing(type, run);
                    script._runs.Add(new MachineRun(run.Instance, run.Step, result));
                }

                return Task.FromResult(result);
            }
        }
    }
}
