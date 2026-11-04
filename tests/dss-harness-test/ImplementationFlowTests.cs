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

    /// <summary>The kit: a repository holding the shipped workflow as init writes it, and the service over scripted agents.</summary>
    private sealed record Kit(HarnessFactory Harness, TempDirectory Tree, Scripted Agents, FlowService Service)
    {
        /// <summary>The run's records, r1 as a run starts unnamed.</summary>
        public OrchestratorLayout RunOf(string run = "r1")
            => new(run, Path.Combine(Tree.Path, ".orchestrators", run));
    }

    /// <summary>
    /// A repository whose init wrote the shipped implementation workflow and declared it, whose configuration holds the
    /// providers and tiers init seeds, and whose slot holds the keys of both providers - over the engine's own step
    /// runners with every machine step the flow's mechanics do not reach answered as done.
    /// </summary>
    private static async Task<Kit> PrepareAsync(TempDirectory temp, Action<Scripted>? script = null, string? keys = null)
    {
        var harness = new HarnessFactory();
        var template = AgentProviderTemplates.Read();

        await harness.InitializeHarnessAsync(
            temp.Path,
            Token,
            new HarnessConfig
            {
                Workflows = [WorkflowTemplates.ShippedWorkflowName],
                Worktrees = new WorktreeSettings { Root = ".worktrees" },
                Defaults = new HarnessDefaults { AgentTier = template.AgentTier },
                Providers = [.. template.Providers],
                TierConfig = new Dictionary<string, List<string>>(template.TierConfig, StringComparer.OrdinalIgnoreCase),
            });

        OrchestrationKit.Write(temp.Path, Path.Combine(".harness-config", "workflows", ".env", "a.env"), keys ?? "ANTHROPIC_API_KEY=sk-test-one\nZAI_API_KEY=sk-test-two\n");
        harness.FilePermissions.ProtectSecret(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "a.env"));
        OrchestrationKit.Write(temp.Path, Path.Combine("src", "tool.cs"), "namespace Tool;\n");
        await harness.CommitAllAsync(temp.Path, "the workflow as init wrote it", Token);

        var agents = new Scripted();

        script?.Invoke(agents);

        return new Kit(harness, temp, agents, Service(harness, agents));
    }

    /// <summary>The service over the engine's own runners: every type the flow's head works through for real, the rest answered as done.</summary>
    private static FlowService Service(HarnessFactory harness, Scripted agents)
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
                    .Select(type => new Done(type)),
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
                "reviewer-correctness" => Completed(Review()),
                "auditor" => Completed(SelfAudit(clean: true)),
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

    /// <summary>An implementer's report: everything done, nothing blocked, nothing new to the registry.</summary>
    private static JsonObject Implement()
        => new()
        {
            ["summary"] = "Built the item.",
            ["done"] = new JsonArray(new JsonObject { ["statement"] = "the report ships", ["holds"] = true, ["shows"] = "its code, read again" }),
            ["claims"] = new JsonObject { ["built"] = "src/one/one.cs", ["tested"] = "nothing run: the repository keeps no legs" },
            ["reuse"] = "",
            ["newRows"] = new JsonArray(),
            ["blocked"] = new JsonArray(),
            ["declined"] = new JsonArray(),
            ["preExisting"] = new JsonArray(),
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

    /// <summary>A review that applies and finds nothing.</summary>
    private static JsonObject Review()
        => new()
        {
            ["applies"] = true,
            ["findings"] = new JsonArray(),
            ["preExisting"] = new JsonArray(),
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

    /// <summary>The scribe's summary.</summary>
    private static JsonObject Scribe()
        => new()
        {
            ["summary"] = "The handoff, written.",
            ["newRows"] = new JsonArray(),
            ["preExisting"] = new JsonArray(),
        };

    /// <summary>A machine step answered as done: the flow's mechanics are what these tests hold, not the machine's own work.</summary>
    private sealed class Done(string type) : IFlowStepTypeRunner
    {
        /// <inheritdoc/>
        public string Type => type;

        /// <inheritdoc/>
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            => Task.FromResult(FlowStepResult.Ended(passed: true, HarnessExit.Success));
    }
}
