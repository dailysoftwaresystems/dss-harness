using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Ci;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// The reason step runner: the agent a step that names none is given made read-only and worked as, the step's model
/// resolved and its order built with its credentials, its prompt's skills handed over and their blocks put in the
/// prompt, and its run's answer mapped to the step's own ending.
/// </summary>
public sealed class ReasonStepRunnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>No events of a run, where a test wants none heard.</summary>
    private sealed class NoEvents : IFlowRunEvents
    {
        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double usd) { }

        public void HandoffTaken() { }
    }

    /// <summary>The agent runner as a fake, keeping every request it was asked and answering as a test says.</summary>
    private sealed class Agents : IAgentRunner
    {
        /// <summary>Every request the runner built, in order.</summary>
        public List<AgentStepRequest> Asked { get; } = [];

        /// <summary>How the run answers; a completed run with no output unless a test changes it.</summary>
        public Func<AgentStepRequest, AgentRunResult> Answer { get; set; } = _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "done");

        public Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
        {
            Asked.Add(step);
            return Task.FromResult(Answer(step));
        }

        public async IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var @event in One(RunAsync(step, null, cancellationToken)))
            {
                yield return @event;
            }

            static async IAsyncEnumerable<AgentEvent> One(Task<AgentRunResult> run)
            {
                _ = await run;
                yield break;
            }
        }
    }

    /// <summary>The kit with a configuration whose providers resolve and whose slot holds the model's key.</summary>
    private static async Task<(OrchestrationKit Kit, Agents Runner)> ReadyAsync(TempDirectory temp)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);

        File.WriteAllText(
            Path.Combine(kit.Main, ".harness-config", "config.json"),
            """
            {
              "worktrees": { "root": ".worktrees" },
              "providers": [
                {
                  "name": "anthropic",
                  "type": "anthropic",
                  "api": "https://api.anthropic.com",
                  "apiKeyNames": ["ANTHROPIC_KEY"],
                  "client": "claude-api",
                  "models": [
                    { "code": "claude-opus-5-5", "name": "Opus", "tiers": ["best"], "efforts": ["high"], "contextTokens": 200000, "maxOutputTokens": 32000 }
                  ]
                }
              ],
              "tierConfig": { "best": ["claude-opus-5-5"] },
              "defaults": { "agentTier": "best" }
            }
            """);
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", ".env", "a.env"), "ANTHROPIC_KEY=sk-test-one\n");
        kit.Harness.FilePermissions.ProtectSecret(Path.Combine(kit.Main, ".harness-config", "workflows", ".env", "a.env"));
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "schemas", "plan.schema.json"), "{\"type\":\"object\"}\n");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "prompts", "plan.md"), "Plan the work.\n");

        var runner = new Agents();
        return (kit, runner);
    }

    private ReasonStepRunner Runner(OrchestrationKit kit, Agents agents)
        => new(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            new CreateAgentRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService),
            agents,
            kit.Harness.FileSystem,
            new AgentKeyStore(kit.Harness.FileSystem, kit.Harness.FilePermissions, kit.Harness.Platform),
            new GhProcess(kit.Harness.ProcessRunner),
            kit.Harness.Output,
            TimeProvider.System);

    /// <summary>The run a reason step is asked with, with what a test changes of it.</summary>
    private static FlowStepRun Run(
        OrchestrationKit kit,
        string? prompt = "Plan the work.",
        string? agent = null,
        FlowMadeAgent? made = null,
        IReadOnlyList<FlowWorkedFrom>? workedFrom = null,
        string? handoff = null,
        int attempt = 1)
        => new(
            new WorkflowStep { Name = "think", Type = "reason", Prompt = "prompts/plan.md", Outputs = "schemas/plan.schema.json" },
            "think",
            attempt,
            FlowValue.Missing,
            null,
            null,
            [],
            new NoEvents(),
            handoff,
            Issue: null,
            Visit: 1,
            new FlowRunPlace(kit.Main, "o1", StartCommit: null, WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: orient\n    type: orient\n")),
            new Dictionary<string, FlowValue>(StringComparer.Ordinal) { ["prompt"] = FlowValue.Of(prompt ?? string.Empty) },
            made,
            agent,
            Target: null,
            workedFrom);

    /// <summary>
    /// A step the engine named a read-only agent for is given one - made, with no worktree - and worked as: its
    /// workspace reads the main tree, writes its work directory, and the request carries the agent's name.
    /// </summary>
    [Fact]
    public async Task AStepTheEngineNamedAnAgentFor_IsGivenItMadeReadOnly_AndWorkedAs()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        var result = await Runner(kit, agents).RunAsync(Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")), Token);

        Assert.True(result.Passed, result.Error);
        var asked = agents.Asked.Single();
        Assert.Equal("a9", asked.Agent);
        Assert.NotNull(kit.Record("a9"));
        Assert.True(kit.Record("a9").Reads);
        Assert.Null(asked.Workspace.Worktree);
        Assert.Equal(kit.Main, asked.Workspace.MainCheckout);
        Assert.Equal(Path.Combine(kit.Layout.AgentDirectory("a9"), "work"), asked.Workspace.WorkDirectory);
        Assert.Null(asked.Ground!.ReporterRoot);
        Assert.Contains(kit.Main, asked.Ground!.ReadRoots);
        Assert.Equal([Path.Combine(kit.Layout.AgentDirectory("a9"), "work")], asked.Ground!.WritableRoots);
    }

    /// <summary>
    /// The step's model is resolved with its definition below its own and its order built: the serving entry first,
    /// its credential read from the slot, and the request's model the step's own.
    /// </summary>
    [Fact]
    public async Task TheStepsModelIsResolved_AndItsOrderBuilt_WithItsCredentialReadFromTheSlot()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "agents", "planner.md"), "---\nname: planner\ndescription: plans\n---\nYou plan.\n");

        var step = new WorkflowStep { Name = "think", Type = "reason", Agent = "planner", Prompt = "prompts/plan.md", Outputs = "schemas/plan.schema.json" };
        var run = Run(kit, agent: "reader") with { Step = step };
        var result = await Runner(kit, agents).RunAsync(run, Token);

        Assert.True(result.Passed, result.Error);
        var asked = agents.Asked.Single();
        Assert.Equal("You plan.\n", asked.SystemPrompt);
        Assert.Equal("claude-opus-5-5", asked.Model);
        Assert.Equal("claude-api", asked.Client);
        Assert.Equal("claude-opus-5-5", asked.Order!.Single().Model);
        Assert.Equal("ANTHROPIC_KEY", asked.Order!.Single().Key);
        Assert.True(asked.Order!.Single().Usable);
        Assert.Equal("sk-test-one", asked.Order!.Single().Credential!.Keys[0].Value);
    }

    /// <summary>
    /// Each skill the prompt names is looked up again where the step starts, its folder copied whole into the agent's
    /// work directory, and its block put where the placeholder stood; a skill that is not there refuses the step, and
    /// so does one that asks a tool of a capability the agent has not.
    /// </summary>
    [Fact]
    public async Task EachSkillThePromptNames_IsHandedOver_AndItsBlockPutInThePrompt()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "skills", "planning", "SKILL.md"), "---\ndescription: how to plan\n---\nPlan in steps, session ${CLAUDE_SESSION_ID}.\n");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "skills", "planning", "ways.md"), "Three ways.\n");

        var handed = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.planning}.", made: new FlowMadeAgent("a9", "claude-opus-5-5")), Token);

        Assert.True(handed.Passed, handed.Error);
        var asked = agents.Asked.Single();
        Assert.StartsWith("Plan with ", asked.PromptTemplate, StringComparison.Ordinal);
        Assert.Contains("<skill name=\"planning\" from=\".harness-config/skills\" directory=\"", asked.PromptTemplate, StringComparison.Ordinal);
        Assert.Contains("Files this skill refers to are in that directory. Read them from there.", asked.PromptTemplate, StringComparison.Ordinal);
        Assert.EndsWith("Plan in steps, session think.\n\n</skill>.", asked.PromptTemplate, StringComparison.Ordinal);
        var copied = Path.Combine(kit.Layout.AgentDirectory("a9"), "work", "skills", "planning");
        Assert.Equal("---\ndescription: how to plan\n---\nPlan in steps, session ${CLAUDE_SESSION_ID}.\n", OrchestrationKit.Read(copied, "SKILL.md"));
        Assert.Equal("Three ways.\n", OrchestrationKit.Read(copied, "ways.md"));

        var missing = await Assert.ThrowsAsync<HarnessException>(
            () => Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.nosuch}.", made: new FlowMadeAgent("a9", "claude-opus-5-5")), Token));

        Assert.Equal(HarnessExit.Refused, missing.ExitCode);
        Assert.Contains("there is no such skill", missing.Message, StringComparison.Ordinal);

        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "skills", "searching", "SKILL.md"), "---\ndescription: how to search\nallowed-tools: Bash, WebSearch\n---\nSearch.\n");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "agents", "narrow.md"), "---\nname: narrow\ndescription: narrow\ncapabilities: [read, write]\n---\nYou work without the web.\n");
        var narrow = new WorkflowStep { Name = "think", Type = "reason", Agent = "narrow", Prompt = "prompts/plan.md", Outputs = "schemas/plan.schema.json" };

        var beyond = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.searching}.", agent: "reader") with { Step = narrow }, Token);

        Assert.False(beyond.Passed);
        Assert.Equal(HarnessExit.Refused, beyond.Exit);
        Assert.Contains("has no 'run' capability", beyond.Error, StringComparison.Ordinal);
    }

    /// <summary>A step that goes on with earlier work is given the continuation record of the instances it goes on with.</summary>
    [Fact]
    public async Task AStepThatGoesOnWithEarlierWork_IsGivenItsContinuationRecord()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");

        var gone = await Runner(kit, agents).RunAsync(
            Run(kit,
                agent: "reader",
                workedFrom: [new FlowWorkedFrom("plan", "reader")],
                handoff: "what was left"),
            Token);

        Assert.True(gone.Passed, gone.Error);
        var asked = agents.Asked.Single();
        Assert.NotNull(asked.Continuation);
        Assert.Contains("You are going on with work begun by step 'plan'.", asked.Continuation, StringComparison.Ordinal);
    }

    /// <summary>A step that works in an agent's worktree is given it: the worktree its workspace names, the working directory the worktree itself.</summary>
    [Fact]
    public async Task AStepWorkingInAnAgentsWorktree_IsGivenTheWorktreeAsItsWorkingDirectory()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateAgentAsync("ag");
        var worktree = kit.Record("ag").Worktree!;

        await Runner(kit, agents).RunAsync(Run(kit, agent: "ag"), Token);

        var asked = agents.Asked.Single();
        Assert.Equal(worktree, asked.Workspace.Worktree);
        Assert.Equal(worktree, asked.Workspace.WorkingDirectory);
        Assert.Equal(Path.Combine(kit.Layout.AgentDirectory("ag"), "work"), asked.Workspace.WorkDirectory);
        Assert.Contains(worktree, asked.Ground!.WritableRoots);
    }

    /// <summary>
    /// Where the configuration balances a provider's keys, the step's order runs each model's keys by least spent in
    /// the last seven days - the repository's key-spend record read where the step starts - a tie in the list's order.
    /// </summary>
    [Fact]
    public async Task WhereTheConfigurationBalancesTheKeys_TheOrderRunsThemByLeastSpent()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", ".env", "b.env"), "ANTHROPIC_SPARE=sk-test-two\n");
        kit.Harness.FilePermissions.ProtectSecret(Path.Combine(kit.Main, ".harness-config", "workflows", ".env", "b.env"));
        File.WriteAllText(
            Path.Combine(kit.Main, ".harness-config", "config.json"),
            File.ReadAllText(Path.Combine(kit.Main, ".harness-config", "config.json"))
                .Replace("\"apiKeyNames\": [\"ANTHROPIC_KEY\"]", "\"apiKeyNames\": [\"ANTHROPIC_KEY\", \"ANTHROPIC_SPARE\"]")
                .Replace("\"contextTokens\": 200000, \"maxOutputTokens\": 32000", "\"contextTokens\": 200000, \"maxOutputTokens\": 32000, \"priceInputMiss\": 5, \"priceInputHit\": 1, \"priceOutput\": 25")
                .Replace("\"client\": \"claude-api\",", "\"client\": \"claude-api\", \"currency\": \"USD\", \"readOn\": \"2026-10-12\",")
                .Replace("\"defaults\": { \"agentTier\": \"best\" }", "\"balanceKeyUsage\": true, \"defaults\": { \"agentTier\": \"best\" }"));
        var spentFile = Path.Combine(new DssHarness.Core.Repository.HarnessLayout(kit.Main, kit.Main).OrchestratorsDirectory, "key-spend.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(spentFile)!);
        File.WriteAllText(
            spentFile,
            $$"""{"time":"{{DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)}}","provider":"anthropic","key":"ANTHROPIC_KEY","model":"claude-opus-5-5","cost":3.5}""");

        var result = await Runner(kit, agents).RunAsync(Run(kit, agent: "reader"), Token);

        Assert.True(result.Passed, result.Error);

        // The list's order is ANTHROPIC_KEY first; the week's spend turns it around, so the order proves the record
        // was read, not merely the list.
        Assert.Equal(
            ["ANTHROPIC_SPARE", "ANTHROPIC_KEY"],
            agents.Asked.Single().Order!.Select(entry => entry.Key));
    }

    /// <summary>A stopped step's handoff turn is answered of its own record: the agent's answer where it gave one, and null where it did not.</summary>
    [Fact]
    public async Task AStoppedStepsHandoffTurn_IsAnsweredOfItsOwnRecord()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);
        var directory = kit.Layout.AgentStepDirectory("reader", "think");

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, AgentRunner.ResultFileName),
            """{"status":"HandedOff","handoff":{"answer":"what was left"}}""");

        var answered = await runner.HandoffAsync(Run(kit, agent: "reader", handoff: "cut off"), Token);

        Assert.Equal("what was left", answered);

        File.Delete(Path.Combine(directory, AgentRunner.ResultFileName));

        Assert.Null(await runner.HandoffAsync(Run(kit, agent: "reader", handoff: "cut off"), Token));
    }

    /// <summary>The dispatcher hands a stopped reason step's handoff turn to its runner; every other type's answers none, for the engine to write the handoff itself.</summary>
    [Fact]
    public async Task TheDispatcherHandsAStoppedReasonStepsHandoffTurnToItsRunner()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);
        var directory = kit.Layout.AgentStepDirectory("reader", "think");

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, AgentRunner.ResultFileName),
            """{"status":"HandedOff","handoff":{"answer":"what was left"}}""");

        var dispatched = await new FlowStepRunners([runner], kit.Harness.GraphSyncs).HandoffAsync(Run(kit, agent: "reader", handoff: "cut off"), Token);

        Assert.Equal("what was left", dispatched);
    }

    /// <summary>The run's answer is mapped to the step's own ending: a completed one passes with its output, a failed one fails with its words, a stop is incomplete.</summary>
    [Fact]
    public async Task TheRunsAnswerIsMappedToTheStepsOwnEnding()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: """{"answer":"two"}""");

        var done = await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.True(done.Passed);
        Assert.Equal(0, done.Exit);
        Assert.Equal("two", (string?)done.Output!["answer"]);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Failed, Error: "the provider is down");

        var failed = await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.False(failed.Passed);
        Assert.Equal(HarnessExit.CommandFailed, failed.Exit);
        Assert.Equal("the provider is down", failed.Error);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Cancelled, Error: null);

        var stopped = await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.False(stopped.Passed);
        Assert.Equal(HarnessExit.Incomplete, stopped.Exit);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.MaxTurns, Error: "20 turns");

        var limited = await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.Null(limited.Asked);
        Assert.False(limited.Passed);
        Assert.Equal("turns", limited.Limit);
    }
}
