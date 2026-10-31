using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Runtime;
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

        /// <summary>The events the run says before it answers, as a client says them.</summary>
        public List<AgentEvent> Says { get; } = [];

        public async Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
        {
            Asked.Add(step);

            foreach (var said in Says)
            {
                await events!.EmitAsync(said);
            }

            return Answer(step);
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

    private static ReasonStepRunner Runner(OrchestrationKit kit, IAgentRunner agents, ClaudeCodeUpdateService? updates = null)
        => new(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            new CreateAgentRunner(kit.Harness.ContextLoader, kit.Harness.Platform, kit.Harness.OrchestrationStore, kit.Harness.AgentService),
            agents,
            kit.Harness.FileSystem,
            Keys(kit),
            new GhProcess(kit.Harness.ProcessRunner),
            kit.Harness.Output,
            TimeProvider.System,
            kit.Harness.GitClient,
            kit.Harness.ConfigStore,
            updates);

    /// <summary>The kit's slot, as the runner reads it.</summary>
    private static AgentKeyStore Keys(OrchestrationKit kit)
        => new(kit.Harness.FileSystem, kit.Harness.FilePermissions, kit.Harness.Platform);

    /// <summary>The run a reason step is asked with, with what a test changes of it.</summary>
    private static FlowStepRun Run(
        OrchestrationKit kit,
        string? prompt = "Plan the work.",
        string? agent = null,
        FlowMadeAgent? made = null,
        IReadOnlyList<FlowWorkedFrom>? workedFrom = null,
        string? handoff = null,
        int attempt = 1,
        IReadOnlyList<FlowAnswerGiven>? answers = null,
        bool ask = false,
        int? askRounds = null)
        => new(
            new WorkflowStep { Name = "think", Type = "reason", Prompt = "prompts/plan.md", Outputs = "schemas/plan.schema.json", Ask = ask, AskRounds = askRounds },
            "think",
            attempt,
            FlowValue.Missing,
            null,
            null,
            answers ?? [],
            new NoEvents(),
            handoff,
            Issue: null,
            Visit: 1,
            new FlowRunPlace(kit.Main, "o1", StartCommit: null, WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: orient\n    type: orient\n")),
            Read: null,
            made,
            agent,
            Target: null,
            workedFrom,
            Prompts: new Dictionary<string, IReadOnlyList<FlowTemplate.Piece>>(StringComparer.Ordinal) { ["prompt"] = Filled(prompt ?? string.Empty) });

    /// <summary>
    /// A prompt as the engine hands it to the runner, filled around its skills: each name <paramref name="values"/>
    /// holds read by its root as a run's expressions read it, every other name reading nothing.
    /// </summary>
    /// <param name="prompt">The prompt file's text.</param>
    /// <param name="values">What each root reads; none where the prompt reads nothing but skills.</param>
    private static IReadOnlyList<FlowTemplate.Piece> Filled(string prompt, IReadOnlyDictionary<string, JsonNode?>? values = null)
        => FlowTemplate.FillAroundSkills(
                prompt,
                path => values is not null && values.TryGetValue(path.Root, out var value) ? new(FlowValue.Of(value), 0) : new(FlowValue.Missing, 0),
                out var problem)
            ?? throw new InvalidOperationException(problem);

    /// <summary>
    /// A step the engine named a read-only agent for is given one - made, with no worktree - and worked as: its
    /// workspace reads the main tree, writes its work directory, and the request carries the agent's name. The engine
    /// stores the agent it named as the one the step works in, so the run hands both: the making answers an agent
    /// already live as already made, and the step works as it.
    /// </summary>
    [Fact]
    public async Task AStepTheEngineNamedAnAgentFor_IsGivenItMadeReadOnly_AndWorkedAs()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);

        // The engine stores the agent it named as the one the step works in, so the run hands both.
        var result = await Runner(kit, agents).RunAsync(Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")) with { WorksIn = "a9" }, Token);

        Assert.True(result.Passed, result.Error);
        var asked = agents.Asked.Single();
        Assert.Equal("a9", asked.Agent);
        Assert.NotNull(kit.Record("a9"));
        Assert.True(kit.Record("a9").Reads);
        Assert.Null(asked.Workspace.Worktree);
        Assert.Equal(kit.Main, asked.Workspace.MainCheckout);
        Assert.Equal(kit.Layout.WorkDirectory("a9"), asked.Workspace.WorkDirectory);
        Assert.Null(asked.Ground!.ReporterRoot);
        Assert.Contains(kit.Main, asked.Ground!.ReadRoots);
        Assert.Equal([kit.Layout.WorkDirectory("a9")], asked.Ground!.WritableRoots);

        var again = await Runner(kit, agents).RunAsync(Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")) with { WorksIn = "a9", Attempt = 2 }, Token);

        Assert.True(again.Passed, again.Error);
        Assert.Equal("a9", agents.Asked[1].Agent);
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
    /// work directory, and its block put where the placeholder stood - its <c>${CLAUDE_SESSION_ID}</c> the id the run's
    /// first session begins as, which the request hands the client - and the skill recorded on the request, for the
    /// step's stream to say what it was given.
    /// </summary>
    [Fact]
    public async Task EachSkillThePromptNames_IsHandedOver_AndItsBlockPutInThePrompt()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        const string text = "---\ndescription: how to plan\n---\nPlan in steps, session ${CLAUDE_SESSION_ID}.\n";
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "skills", "planning", "SKILL.md"), text);
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "skills", "planning", "ways.md"), "Three ways.\n");

        var handed = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.planning}.", made: new FlowMadeAgent("a9", "claude-opus-5-5")), Token);

        Assert.True(handed.Passed, handed.Error);
        var asked = agents.Asked.Single();
        var copied = Path.Combine(kit.Layout.WorkDirectory("a9"), "skills", "planning");
        Assert.True(Guid.TryParseExact(asked.SessionId, "D", out _), asked.SessionId);
        Assert.Equal(
            $"Plan with <skill name=\"planning\" from=\".harness-config/skills\" directory=\"{copied}\">\nFiles this skill refers to are in that directory. Read them from there.\n\nPlan in steps, session {asked.SessionId}.\n\n</skill>.",
            asked.Prompt);
        Assert.Equal(text, OrchestrationKit.Read(copied, "SKILL.md"));
        Assert.Equal("Three ways.\n", OrchestrationKit.Read(copied, "ways.md"));

        var skill = Assert.Single(asked.Skills);
        var found = new AgentSkills(kit.Harness.FileSystem, kit.Harness.Platform, kit.Main).Find("planning")!;
        Assert.Equal(
            new AgentSkill("planning", ".harness-config/skills", Path.Combine(kit.Main, ".harness-config", "skills", "planning", "SKILL.md"), found.Digest!, found.FolderDigest!, copied),
            skill);

        // The step's result carries what its agent was given, for the step's own record to name it - and an agent that
        // never started was given nothing.
        Assert.Equal([skill], handed.Skills);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.NotStarted, AgentNotStartedBecause.KeyMissing, Error: "no key");

        var notStarted = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.planning}.", made: new FlowMadeAgent("a9", "claude-opus-5-5")) with { Attempt = 2 }, Token);

        Assert.Equal((false, HarnessExit.Refused), (notStarted.Passed, notStarted.Exit));
        Assert.Empty(notStarted.Skills);
    }

    /// <summary>
    /// What a skill the prompt names cannot be handed for refuses the step with the configuration's code, as the
    /// definition's check refuses it - one that is not there, one whose name reaches past the places it is looked for
    /// in, one named by no name, and one that asks a tool of a capability the agent has not - each said whole.
    /// </summary>
    [Fact]
    public async Task ASkillThatCannotBeHanded_RefusesTheStep_AsTheConfigurations()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var skills = new AgentSkills(kit.Harness.FileSystem, kit.Harness.Platform, kit.Main);

        var missing = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.nosuch}.", agent: "reader"), Token);

        Assert.Equal((false, HarnessExit.ConfigInvalid), (missing.Passed, missing.Exit));
        Assert.Equal($"'think' prompt names {{skill.nosuch}}, and {skills.Missing("nosuch")}", missing.Error);

        var beyond = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.a/../../x}.", agent: "reader"), Token);

        Assert.Equal((false, HarnessExit.ConfigInvalid), (beyond.Passed, beyond.Exit));
        Assert.Equal(
            "'think' prompt names {skill.a/../../x}, which is no skill's name: a skill is named by its folder's name alone, in letters, digits, '-' and '_', a '.' between two words",
            beyond.Error);

        var unnamed = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill}.", agent: "reader"), Token);

        Assert.Equal((false, HarnessExit.ConfigInvalid), (unnamed.Passed, unnamed.Exit));
        Assert.Equal("'think' prompt names {skill} alone: name one - {skill.<name>} names it by its folder's name", unnamed.Error);

        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "skills", "searching", "SKILL.md"), "---\ndescription: how to search\nallowed-tools: Bash\n---\nSearch.\n");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "agents", "narrow.md"), "---\nname: narrow\ndescription: narrow\ncapabilities: [read, write]\n---\nYou work without the web.\n");
        var narrow = new WorkflowStep { Name = "think", Type = "reason", Agent = "narrow", Prompt = "prompts/plan.md", Outputs = "schemas/plan.schema.json" };

        var withoutRun = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.searching}.", agent: "reader") with { Step = narrow }, Token);

        Assert.Equal((false, HarnessExit.ConfigInvalid), (withoutRun.Passed, withoutRun.Exit));
        Assert.Equal(
            $"skill 'searching' ('{Path.Combine(kit.Main, ".harness-config", "skills", "searching", "SKILL.md")}') cannot be used by step 'think': it asks for the tool 'Bash', and agent 'narrow' has no 'run' capability",
            withoutRun.Error);
        Assert.Empty(agents.Asked);
    }

    /// <summary>
    /// The prompt the engine filled is never read as a template again: a value's braces - a red leg's JSON - and a skill
    /// a value names stand as the value holds them, and only the skills the prompt file itself names are handed over.
    /// </summary>
    [Fact]
    public async Task ThePromptTheEngineFilled_IsNeverReadAgain_AValuesBracesStand()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var red = JsonValue.Create("the {item} test failed, {{left}} as {skill.x} said");

        var run = Run(kit, agent: "reader") with
        {
            Prompts = new Dictionary<string, IReadOnlyList<FlowTemplate.Piece>>(StringComparer.Ordinal)
            {
                ["prompt"] = Filled("Fix what {red}.", new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["red"] = red }),
            },
        };
        var result = await Runner(kit, agents).RunAsync(run, Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal("Fix what the {item} test failed, {{left}} as {skill.x} said.", agents.Asked.Single().Prompt);
        Assert.Empty(agents.Asked.Single().Skills);
    }

    /// <summary>
    /// A step that goes on with another's work is the same agent going on: it works by the definition, the client, the
    /// writes and the web search of the step its chain of work began with - none of which it names - the definition's
    /// system prompt sent, its limits held, its capabilities on the ground and its <c>web_search</c> asking the sources
    /// that step's <c>search</c> picks, with its own effort beside them.
    /// </summary>
    [Fact]
    public async Task AStepThatGoesOnWithAnothersWork_WorksByTheDefinitionItsChainBeganWith()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "agents", "planner.md"), "---\nname: planner\ndescription: plans\ncapabilities: [read, run, web]\nmaxTurns: 7\nstallSeconds: 90\n---\nYou plan.\n");
        var definition = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            "name: flow\nsteps:\n"
                + "  - name: plan\n    type: reason\n    agent: planner\n    client: claude-code\n    writes: [src/**]\n    search: [npm]\n    prompt: prompts/plan.md\n    outputs: schemas/plan.schema.json\n"
                + "  - name: go-on\n    type: reason\n    resume: plan\n    prompt: prompts/plan.md\n    outputs: schemas/plan.schema.json\n"
                + "  - name: think\n    type: reason\n    resume: go-on\n    effort: high\n    prompt: prompts/plan.md\n    outputs: schemas/plan.schema.json\n");
        var run = Run(kit, agent: "reader");

        var result = await Runner(kit, agents).RunAsync(run with { Step = definition.Steps.Single(step => step.Name == "think"), Place = run.Place! with { Definition = definition } }, Token);

        Assert.True(result.Passed, result.Error);
        var asked = agents.Asked.Single();
        Assert.Equal(("planner", "You plan.\n", "claude-code", "high"), (asked.AgentDefinition, asked.SystemPrompt, asked.Client, asked.Effort));
        Assert.Equal((7, 90), (asked.Limits.MaxTurns, asked.Limits.StallSeconds));
        Assert.Equal([AgentCapability.Read, AgentCapability.Run, AgentCapability.Web], asked.Ground!.Capabilities);
        Assert.Equal(["src/**"], asked.Ground.Writes);
        Assert.Equal(["npm"], asked.WebSearch!.Sources.Select(source => source.Name));
    }

    /// <summary>
    /// A step's launch is built as every launch's is: the tool's own Claude Code directory among those no action of its
    /// agent reaches, whatever client it is on; every secret the slot answers in its mask; and the retries its workflow
    /// allows a step.
    /// </summary>
    [Fact]
    public async Task AStepsLaunch_KeepsTheToolsClaudeDirectory_AndMasksTheSlotsSecrets()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");

        var result = await Runner(kit, agents).RunAsync(Run(kit, agent: "reader"), Token);

        Assert.True(result.Passed, result.Error);
        var asked = agents.Asked.Single();
        Assert.Equal([new ClaudeTranscripts(kit.Harness.FileSystem, kit.Harness.Platform).ConfigDirectory], asked.Ground!.ClaudeCodeDirectories);
        Assert.Equal($"the key {DssHarness.Core.Runners.ActionValues.Mask}", asked.Mask!.Redact("the key sk-test-one"));
        Assert.Equal(WorkflowDefinition.DefaultMaxRetries, asked.Retries);
    }

    /// <summary>
    /// The issues a step's answer reports were there before the run are the step's own, each as its output spells it -
    /// a list of places joined - for the triage its <c>onPreExisting</c> names.
    /// </summary>
    [Fact]
    public async Task TheIssuesAnAnswerReportsWereThereBefore_AreTheStepsOwn()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        agents.Answer = _ => new AgentRunResult(
            AgentRunStatus.Completed,
            StructuredOutput: """{"preExisting":[{"title":"flaky test","description":"fails one run in ten","where":["tests/a.cs","tests/b.cs"],"evidence":"3 of 30 runs","blocks":true}]}""");

        var result = await Runner(kit, agents).RunAsync(Run(kit, agent: "reader"), Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal([new FlowPreExistingReport("flaky test", "fails one run in ten", "tests/a.cs, tests/b.cs", "3 of 30 runs", Blocks: true)], result.PreExisting);
    }

    /// <summary>
    /// The step's own stream is read by the store's one reader of it: a whole line that does not read refuses the
    /// attempt that goes on with it, in the reader's own words - never a stream read past the line - and leaves the
    /// engine's account of it nothing to say.
    /// </summary>
    [Fact]
    public async Task AStreamWithALineThatDoesNotRead_RefusesTheAttemptThatGoesOnWithIt()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var directory = kit.Layout.AgentStepDirectory("reader", "think");
        var stream = StoredStream.Open(kit.Harness.FileSystem, directory, new Masking());

        await stream.EmitAsync(new RunStarted(1));
        await stream.EmitAsync(new AssistantMessage("I planned.", []));
        stream.Dispose();

        var lines = File.ReadAllLines(Path.Combine(directory, StoredStream.FileName));
        File.WriteAllText(Path.Combine(directory, StoredStream.FileName), $"{lines[0]}\nnot an event\n{lines[1]}\n");
        var unread = Assert.Throws<HarnessException>(() => kit.Harness.OrchestrationStore.ReadStepStream(directory));

        var refused = await Runner(kit, agents).RunAsync(Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "more", null, "turnLimit", Attempt: 1)]), Token);

        Assert.Equal((false, HarnessExit.Refused, unread.Message), (refused.Passed, refused.Exit, refused.Error));
        Assert.Empty(agents.Asked);
        Assert.Null(((IFlowHandoffRunner)Runner(kit, agents)).Account(Run(kit, agent: "reader", handoff: "cut off")));
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

    /// <summary>
    /// A step that works in an agent's worktree is given it: the worktree's path on the disk - the one its record
    /// resolves under the worktrees root, never the record's address - as its workspace's worktree and its working
    /// directory, and among the roots it writes.
    /// </summary>
    [Fact]
    public async Task AStepWorkingInAnAgentsWorktree_IsGivenTheWorktreeAsItsWorkingDirectory()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateAgentAsync("ag");
        var worktree = kit.Worktree("ag");

        Assert.True(Directory.Exists(worktree));
        Assert.NotEqual(worktree, kit.Record("ag").Worktree);

        await Runner(kit, agents).RunAsync(Run(kit, agent: "ag"), Token);

        var asked = agents.Asked.Single();
        Assert.Equal(worktree, asked.Workspace.Worktree);
        Assert.Equal(worktree, asked.Workspace.WorkingDirectory);
        Assert.Equal(kit.Layout.WorkDirectory("ag"), asked.Workspace.WorkDirectory);
        Assert.Equal([worktree, kit.Layout.WorkDirectory("ag")], asked.Ground!.WritableRoots);
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

    /// <summary>
    /// Where the step's order holds the client that starts a program, the program is found ready where the step starts
    /// - a program the slot names read for what it is - and the request carries it and the version it answered, for the
    /// client to start what was found; one not ready refuses the step with the finder's own words.
    /// </summary>
    [Fact]
    public async Task TheProgramAClientThatStartsOneStarts_IsFoundReadyWhereTheStepStarts()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);

        var where = kit.Harness.Platform.Current == DssHarness.Core.Platform.PlatformId.Windows ? "where.exe" : "which";
        var found = await kit.Harness.ProcessRunner.RunAsync(new DssHarness.Core.Processes.ProcessRequest { FileName = where, Arguments = ["dotnet"] }, Token);
        var named = found.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        File.WriteAllText(
            Path.Combine(kit.Main, ".harness-config", "config.json"),
            File.ReadAllText(Path.Combine(kit.Main, ".harness-config", "config.json")).Replace("\"client\": \"claude-api\"", "\"client\": \"claude-code\""));
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", ".env", "a.env"), $"ANTHROPIC_KEY=sk-test-one\nCLAUDE_EXECUTABLE={named}\n");
        kit.Harness.FilePermissions.ProtectSecret(Path.Combine(kit.Main, ".harness-config", "workflows", ".env", "a.env"));

        var updates = new ClaudeCodeUpdateService(
            kit.Harness.ProcessTable,
            kit.Harness.ProcessRunner,
            kit.Harness.FileSystem,
            kit.Harness.FilePermissions,
            kit.Harness.Platform,
            kit.Harness.Identity,
            Keys(kit));

        var result = await Runner(kit, agents, updates).RunAsync(Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")), Token);

        Assert.True(result.Passed, result.Error);
        var asked = agents.Asked.Single();
        Assert.Equal(named, asked.Program);
        Assert.NotNull(asked.ProgramVersion);

        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", ".env", "a.env"), $"ANTHROPIC_KEY=sk-test-one\nCLAUDE_EXECUTABLE={kit.Main}\\not-there.exe\n");
        kit.Harness.FilePermissions.ProtectSecret(Path.Combine(kit.Main, ".harness-config", "workflows", ".env", "a.env"));

        var refused = await Runner(kit, agents, updates).RunAsync(Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")) with { Attempt = 2 }, Token);

        Assert.False(refused.Passed);
        Assert.Equal(HarnessExit.ToolMissing, refused.Exit);
        Assert.Contains("not-there", refused.Error, StringComparison.Ordinal);
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

    /// <summary>
    /// The engine's account of a stored stream holds what the stream shows and no more: the files the newest attempt
    /// wrote, the last commands it ran and how each ended, and its last messages word for word - an earlier attempt's
    /// events left out - beside the steps before it that left notes, named by step and not by path. Where the step
    /// streamed nothing there is no account, and the handoff stands on its first line alone.
    /// </summary>
    [Fact]
    public async Task TheEnginesAccountOfAStoredStream_HoldsWhatItShows()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = (IFlowHandoffRunner)Runner(kit, agents);

        var plan = kit.Layout.AgentStepDirectory("reader", "plan");
        OrchestrationKit.Write(kit.Main, Path.Combine(plan, "notes.md"), "Planned three items.");

        var stream = StoredStream.Open(kit.Harness.FileSystem, kit.Layout.AgentStepDirectory("reader", "think"), new Masking());

        await stream.EmitAsync(new RunStarted(1));
        await stream.EmitAsync(new ToolCallStarted("c1", AgentTools.CreateFile, AgentToolRunner.Harness));
        await stream.EmitAsync(new ToolCallFinished("c1", Ok: true, TimeSpan.FromSeconds(1), "{\"path\": \"src/one.cs\", \"content\": \"one\"}", "written"));
        await stream.EmitAsync(new RunFinished(AgentRunStatus.Failed));
        await stream.EmitAsync(new RunStarted(2));
        await stream.EmitAsync(new ToolCallStarted("c2", AgentTools.CreateFile, AgentToolRunner.Harness));
        await stream.EmitAsync(new ToolCallFinished("c2", Ok: true, TimeSpan.FromSeconds(1), "{\"path\": \"src/two.cs\", \"content\": \"two\"}", "written"));
        await stream.EmitAsync(new ToolCallStarted("c3", AgentTools.RunCommand, AgentToolRunner.Harness));
        await stream.EmitAsync(new ToolCallFinished("c3", Ok: false, TimeSpan.FromSeconds(2), "{\"program\": \"dotnet\", \"args\": [\"test\"]}", "2 tests failed"));
        await stream.EmitAsync(new AssistantMessage("I wrote the file.", [new AgentToolCall("c2", AgentTools.CreateFile), new AgentToolCall("c3", AgentTools.RunCommand)]));
        await stream.EmitAsync(new AssistantMessage("The tests failed.", []));
        stream.Dispose();

        var account = runner.Account(Run(kit, agent: "reader", workedFrom: [new FlowWorkedFrom("plan", "reader")], handoff: "cut off", attempt: 2));

        Assert.NotNull(account);
        Assert.DoesNotContain("src/one.cs", account, StringComparison.Ordinal);
        Assert.Contains("The files the attempt wrote:", account, StringComparison.Ordinal);
        Assert.Contains("- src/two.cs (create_file)", account, StringComparison.Ordinal);
        Assert.Contains("The last commands it ran and how each ended:", account, StringComparison.Ordinal);
        Assert.Contains("- dotnet test (failed)", account, StringComparison.Ordinal);
        Assert.Contains("The agent's last messages, word for word:", account, StringComparison.Ordinal);
        Assert.Contains("I wrote the file.", account, StringComparison.Ordinal);
        Assert.Contains("The tests failed.", account, StringComparison.Ordinal);
        Assert.EndsWith("The steps before it that left notes: 'plan'.", account, StringComparison.Ordinal);

        File.Delete(Path.Combine(kit.Layout.AgentStepDirectory("reader", "think"), "stream.jsonl"));

        Assert.Null(runner.Account(Run(kit, agent: "reader", attempt: 2)));
    }

    /// <summary>
    /// A continuation record says how each step before ended, as its own record states it, lists the paths this
    /// agent's folds wrote into the main tree, and holds the red legs of its newest red build or test with where the
    /// copied ledger and its logs are.
    /// </summary>
    [Fact]
    public async Task AContinuationRecord_SaysHowEachStepEnded_AndHoldsTheChangedPathsAndTheRedLegs()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var store = kit.Harness.OrchestrationStore;

        var plan = kit.Layout.AgentStepDirectory("reader", "plan");
        OrchestrationKit.Write(kit.Main, Path.Combine(plan, "notes.md"), "Planned three items.");
        store.WriteStepRecord(
            Path.Combine(plan, DssHarness.Core.Orchestration.OrchestratorLayout.StepRecordFileName),
            new FlowStepRecord("plan", "plan", false, "Stopped", 1, 1, [], [], Type: "reason", Agent: "reader"));

        _ = store.UpdateFolds(kit.Layout, ledger => ledger with
        {
            Folds =
            [
                new FlowFoldEntry("f1", "plan", 1, "fold", "reader", "applied", DateTimeOffset.UtcNow, [new FlowFoldPath("src/one.cs", "written", null, new string('a', 64), Changed: true)], [], SettledAt: DateTimeOffset.UtcNow),
            ],
        });

        store.WriteFlow(kit.Layout, new FlowFile(
            1,
            "o1",
            "flow",
            "digest",
            new Dictionary<string, string>(StringComparer.Ordinal),
            nameof(FlowRunStatus.Running),
            new FlowBudgetRecord(0, 0, 0),
            0,
            [new FlowStepRecord("verify", "verify", false, "Failed", 1, 1, [], [], Type: "test", Agent: "reader", FinishedAt: DateTimeOffset.UtcNow, Output: JsonNode.Parse("""{"passed": false, "red": [{"leg": "unit", "verdict": "failed", "phase": "test", "detail": "2 tests failed", "tail": []}]}"""))],
            [],
            DateTimeOffset.UtcNow));

        var gone = await Runner(kit, agents).RunAsync(
            Run(kit, agent: "reader", workedFrom: [new FlowWorkedFrom("plan", "reader")], handoff: "what was left"),
            Token);

        Assert.True(gone.Passed, gone.Error);
        var continuation = agents.Asked.Single().Continuation!;
        Assert.Contains("step 'plan' ended stopped by a pause.", continuation, StringComparison.Ordinal);
        Assert.Contains("The files changed so far, by name:", continuation, StringComparison.Ordinal);
        Assert.Contains("src/one.cs", continuation, StringComparison.Ordinal);
        Assert.Contains("The last build or test in this worktree was red:", continuation, StringComparison.Ordinal);
        Assert.Contains($"leg 'unit', phase 'test': 2 tests failed (the ledger and its logs: {kit.Layout.AgentStepDirectory("reader", "verify")})", continuation, StringComparison.Ordinal);
    }

    /// <summary>
    /// The paths an agent's worktree holds that no fold has taken are listed beside the ones its folds wrote, by the
    /// fold's own rule of what an agent's change is: every path the worktree's status lists, less the floor.
    /// </summary>
    [Fact]
    public async Task AContinuationRecord_ListsThePathsTheWorktreeHolds_ThatNoFoldHasTaken()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateAgentAsync("ag");
        var worktree = kit.Worktree("ag");
        OrchestrationKit.Write(worktree, "two.cs", "two");

        var gone = await Runner(kit, agents).RunAsync(
            Run(kit, agent: "ag", workedFrom: [new FlowWorkedFrom("plan", "ag")], handoff: "what was left"),
            Token);

        Assert.True(gone.Passed, gone.Error);
        Assert.Contains("two.cs", agents.Asked.Single().Continuation!, StringComparison.Ordinal);
        Assert.Contains("The files changed so far, by name:", agents.Asked.Single().Continuation!, StringComparison.Ordinal);
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

        // No stored conversation of the attempt is there - the engine holds none - so more of the same conversation
        // could not help, and the question does not offer it.
        Assert.False(limited.StoredConversation);

        // The assumptions of an answer that read are the step's, whatever else it ended by.
        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "done")
        {
            Assumptions = [new AgentAssumption("the framework", "the newest", "the plan names none")],
        };

        var assumed = await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.Single(assumed.Assumptions);
        Assert.Equal("the framework", assumed.Assumptions[0].About);
    }

    /// <summary>
    /// The person asked is the step's question: the agent's questions ride with it, the engine's problem is written
    /// from what its records hold - each question with the answer its agent recommends - and the answers it takes are
    /// the ask kind's own.
    /// </summary>
    [Fact]
    public async Task ThePersonAsked_IsTheStepsQuestion_WithTheAgentsQuestionsAndItsRecommendation()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Asked, FinalText: "which way?")
        {
            Questions = [new AgentQuestion("which way?", "two ways stand", "the map", "the route", ["left", "right"], "left", "it is shorter")],
        };

        var asking = await Runner(kit, agents).RunAsync(Run(kit, agent: "reader"), Token);

        Assert.NotNull(asking.Asked);
        Assert.Equal("ask", asking.Asked.Kind);
        Assert.Equal(["answer", "decide", "abort"], asking.Asked.Options);
        Assert.Equal("which way?", asking.Asked.AgentQuestions.Single().Question);
        Assert.Equal("agent", asking.Asked.Recommendation.By);
        Assert.Contains("which way?", asking.Asked.Problem, StringComparison.Ordinal);
        Assert.Contains("it recommends 'left'", asking.Asked.Problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer that goes on with the attempt is its next message: the person's words where they answered, what a
    /// granted bound says where they allowed more - and the step's own filled prompt where none did.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatGoesOnWithTheAttempt_IsItsNextMessage()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);

        await runner.RunAsync(Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "answer", "split the parser item in two", "ask", Attempt: 1)]), Token);

        Assert.Equal("The person answered:\nsplit the parser item in two", agents.Asked[0].Prompt);

        await runner.RunAsync(Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "decide", null, "ask", Attempt: 1)]), Token);

        Assert.Contains("leaves the questions to you", agents.Asked[1].Prompt, StringComparison.Ordinal);

        await runner.RunAsync(Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "more", null, "turnLimit", Attempt: 1)]), Token);

        Assert.Contains("allowed the step's turn bound once again", agents.Asked[2].Prompt, StringComparison.Ordinal);

        await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.Equal("Plan the work.", agents.Asked[3].Prompt);

        // An earlier attempt's answers are never served: each attempt is its own, and the second begins a fresh start
        // even where the first was answered.
        await runner.RunAsync(Run(kit, agent: "reader", attempt: 2, answers: [new FlowAnswerGiven("q1", "answer", "split the parser item in two", "ask", Attempt: 1)]), Token);

        Assert.Equal("Plan the work.", agents.Asked[4].Prompt);
    }

    /// <summary>
    /// A full context answered <c>compact</c> begins a NEW conversation from the engine's reduction of the attempt's
    /// own - the exhausted conversation is handed to no client, however plainly it is there, and stays whole on the
    /// disk. The reduction is of the attempt's every run: an attempt that asked, was answered and then filled its
    /// context is compacted from its task, the person's answer in its place as the engine's message - never from the
    /// answer as though it were the task.
    /// </summary>
    [Fact]
    public async Task AFullContextAnsweredCompact_BeginsANewConversation()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");

        // A stored conversation of the attempt, plain on the disk: a session the stream names and the engine holds.
        var session = Guid.NewGuid().ToString("D");
        var directory = kit.Layout.AgentStepDirectory("reader", "think");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, StoredStream.FileName),
            $$"""{"kind":"sessionStarted","seq":1,"at":"2026-10-28T10:00:00Z","client":"claude-api","model":"claude-opus-5-5","systemPromptHash":"hash","tools":[],"skills":[],"handedConversation":false,"provider":"anthropic","sessionId":"{{session}}"}""" + "\n",
            Token);
        var sessions = Path.Combine(kit.Layout.AgentDirectory("reader"), "sessions", session);
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "conversation.jsonl"), "[]\n", Token);

        // The attempt's two runs: the task and a question, then the person's answer and the work until the context filled.
        var stream = StoredStream.Open(kit.Harness.FileSystem, directory, new Masking());
        await stream.EmitAsync(new RunStarted(1));
        await stream.EmitAsync(new MessageSent("the task", AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new TurnStarted(1));
        await stream.EmitAsync(new AssistantMessage("Which file?", []));
        await stream.EmitAsync(new RunFinished(AgentRunStatus.Asked));
        await stream.EmitAsync(new RunStarted(1));
        await stream.EmitAsync(new MessageSent("the person's answer", AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new TurnStarted(1));
        await stream.EmitAsync(new AssistantMessage("Reading it now.", []));
        await stream.EmitAsync(new RunFinished(AgentRunStatus.ContextExhausted));
        stream.Dispose();

        await Runner(kit, agents).RunAsync(
            Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "compact", null, "contextLimit", Attempt: 1)]),
            Token);

        var asked = agents.Asked.Single();
        Assert.Null(asked.Conversation);
        Assert.NotNull(asked.Compaction);
        Assert.StartsWith(AgentCompaction.SaysWhatItIs, asked.Compaction, StringComparison.Ordinal);
        Assert.Contains($"The run context and the task, as they were sent:{Environment.NewLine}{Environment.NewLine}the task", asked.Compaction, StringComparison.Ordinal);
        Assert.Contains($"{AgentContinuation.EngineSaid}{Environment.NewLine}the person's answer", asked.Compaction, StringComparison.Ordinal);
        Assert.DoesNotContain($"as they were sent:{Environment.NewLine}{Environment.NewLine}the person's answer", asked.Compaction, StringComparison.Ordinal);
    }

    /// <summary>
    /// The handoff the attempt is given stands at the head of its prompt, and is spent once the prompt holds it - the
    /// engine's own one-line handoff a limit's question wrote, or a stop's the agent's words of.
    /// </summary>
    [Fact]
    public async Task TheHandoffTheAttemptIsGiven_StandsAtTheHeadOfItsPrompt_AndIsSpent()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var spent = new CountingEvents();

        await Runner(kit, agents).RunAsync(Run(kit, agent: "reader", handoff: "what was left", answers: [new FlowAnswerGiven("q1", "retry", null, "turnLimit")]) with { Events = spent }, Token);

        Assert.Equal("<handoff>\nwhat was left\n</handoff>\n\nPlan the work.", agents.Asked.Single().Prompt);
        Assert.Equal(1, spent.Taken);
    }

    /// <summary>
    /// The rounds a step that may ask is given are spent by the answers its questions were given: the output it is
    /// held to offers what is left, and none after the last.
    /// </summary>
    [Fact]
    public async Task TheRoundsAStepThatMayAskIsGiven_AreSpentByItsAnswers()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);

        await runner.RunAsync(Run(kit, agent: "reader", ask: true, askRounds: 3), Token);

        Assert.Equal(3, agents.Asked[0].Output!.AskRounds);

        await runner.RunAsync(Run(kit, agent: "reader", ask: true, askRounds: 3, answers: [new FlowAnswerGiven("q1", "answer", "first", "ask", Attempt: 1)]), Token);

        Assert.Equal(2, agents.Asked[1].Output!.AskRounds);

        await runner.RunAsync(Run(kit, agent: "reader", ask: true, askRounds: 2, answers: [new FlowAnswerGiven("q1", "answer", "first", "ask", Attempt: 1), new FlowAnswerGiven("q2", "decide", null, "ask", Attempt: 1)]), Token);

        Assert.Equal(0, agents.Asked[2].Output!.AskRounds);

        await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.Null(agents.Asked[3].Output!.AskRounds);
    }

    /// <summary>
    /// Every account at its limit, and the provider's service failing past the client's own tries, pause the run safely
    /// and resume this very step: no failure, no question, and the step runs again.
    /// </summary>
    [Fact]
    public async Task EveryAccountAtItsLimit_AndAServiceFailure_PauseTheRunSafely()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.RateLimited, Error: "still limited after 4 tries")
        {
            Limit = new AgentAccountLimit(BalanceSpent: false),
        };

        var limited = await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.Null(limited.Asked);
        Assert.Null(limited.Limit);
        Assert.NotNull(limited.Pauses);
        Assert.Contains("still limited", limited.Pauses, StringComparison.Ordinal);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Failed, Error: "the provider's service failed past its 4 tries: unreachable") { ServiceFailed = true };

        var service = await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.NotNull(service.Pauses);
        Assert.Null(service.Error);
    }

    /// <summary>The kit's configuration with its model priced, as a run budgeted in dollars needs it.</summary>
    private static void Priced(OrchestrationKit kit)
        => File.WriteAllText(
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
                  "currency": "USD",
                  "readOn": "2026-10-01",
                  "models": [
                    { "code": "claude-opus-5-5", "name": "Opus", "tiers": ["best"], "efforts": ["high"], "contextTokens": 200000, "maxOutputTokens": 32000,
                      "priceInputMiss": 10, "priceInputHit": 1, "priceOutput": 50, "priceCacheWrite": 12.5, "priceCacheWrite1h": 20 }
                  ]
                }
              ],
              "tierConfig": { "best": ["claude-opus-5-5"] },
              "defaults": { "agentTier": "best" }
            }
            """);

    /// <summary>
    /// What an attempt spends is handed on to the run turn by turn as its agent works - each turn's every token of every
    /// kind, and its cost at the configuration's prices for the model serving it - so the run's budget is charged while
    /// the attempt runs, not once it has ended.
    /// </summary>
    [Fact]
    public async Task WhatAnAttemptSpends_IsHandedOnTurnByTurn_EveryKindOfToken_AtTheConfigurationsPrices()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        Priced(kit);
        await kit.CreateReadingAgentAsync("reader");
        var events = new SpendingEvents();
        agents.Says.Add(new UsageReported(new AgentUsage(1000, 200, 3000, 500, 0, 0), new AgentUsage(1000, 200, 3000, 500, 0, 0)));
        agents.Says.Add(new UsageReported(new AgentUsage(100, 20, 0, 0, 0, 0), new AgentUsage(1100, 220, 3000, 500, 0, 0)));

        var result = await Runner(kit, agents).RunAsync(Run(kit, agent: "reader") with { Events = events }, Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal(
            [(4700L, 0.02925), (120L, 0.002)],
            events.Spent.Select(spent => (spent.Tokens, Math.Round(spent.Usd, 9))));
    }

    /// <summary>
    /// An attempt's launch is held to what is left of the run's dollars before its reserve where that is less than its
    /// agent's own bound, and a launch that bound ended reaches the run's reserve - the run pauses for its budget - never
    /// the step's own budget; where its own bound is the less, it ends at the step's own budget, which asks.
    /// </summary>
    [Fact]
    public async Task ALaunchHeldToTheRunsShare_EndsAtTheRunsReserve_NotAtItsOwnBudget()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        Priced(kit);
        await kit.CreateReadingAgentAsync("reader");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "agents", "planner.md"), "---\nname: planner\ndescription: plans\nmaxBudgetUsd: 2\n---\nYou plan.\n");
        var step = new WorkflowStep { Name = "think", Type = "reason", Agent = "planner", Prompt = "prompts/plan.md", Outputs = "schemas/plan.schema.json" };
        var runner = Runner(kit, agents);
        agents.Answer = _ => new AgentRunResult(AgentRunStatus.BudgetSpent, Error: "spent 0.75 USD");

        var share = await runner.RunAsync(Run(kit, agent: "reader") with { Step = step, BudgetLeftUsd = 0.75 }, Token);
        var own = await runner.RunAsync(Run(kit, agent: "reader") with { Step = step, BudgetLeftUsd = 30.0 }, Token);

        Assert.Equal([0.75m, 2m], agents.Asked.Select(asked => asked.Limits.MaxBudgetUsd));
        Assert.Equal(("'think' spent what was left of the run's budget before its reserve (spent 0.75 USD)", "budget", null), (share.Pauses, share.PauseCause, share.Limit));
        Assert.Equal((null, "budget"), (own.Pauses, own.Limit));
    }

    /// <summary>
    /// A step of a work item keeps its records where the run keeps the step - the one owner of a step instance's
    /// directory, its item's brackets as the run's store writes them - and its handoff turn's answer is read there.
    /// </summary>
    [Fact]
    public async Task AStepOfAWorkItem_KeepsItsRecordsWhereTheRunKeepsTheStep_AndItsHandoffIsReadThere()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);
        var run = Run(kit, agent: "reader") with { Instance = "items[0].think" };
        var directory = kit.Layout.InstanceDirectory("items[0].think", "reader");

        _ = await runner.RunAsync(run, Token);

        Assert.Equal(Path.Combine(kit.Layout.AgentStepsDirectory("reader"), "items@0.think"), directory);
        Assert.Equal(directory, agents.Asked.Single().RecordDirectory);

        DssHarness.Core.FileSystem.JsonStateFile.Write(
            kit.Harness.FileSystem,
            Path.Combine(directory, AgentRunner.ResultFileName),
            new AgentRunResult(AgentRunStatus.HandedOff, Handoff: new AgentHandoff("Done: the parser. Left: its tests.")));

        Assert.Equal("Done: the parser. Left: its tests.", await runner.HandoffAsync(run, Token));
    }

    /// <summary>
    /// A safe pause asks the agent of a reason step in flight for its handoff in the conversation it is in - composed
    /// here with the real reason step runner and an agent runner that answers the turn as a client does - waits for its
    /// runner to end, and keeps the agent's own answer, read of the step's own record, as the step's handoff.
    /// </summary>
    [Fact]
    public async Task ASafePause_AsksTheAgentForItsHandoff_AndKeepsItsAnswer()
    {
        using var temp = new TempDirectory();
        var (kit, _) = await ReadyAsync(temp);
        var agents = new HandingOff(kit.Harness.FileSystem, "Done: the parser. Left: its tests.");
        var definition = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            "name: flow\nhandoffSeconds: 60\nsteps:\n  - name: think\n    type: reason\n    prompt: prompts/plan.md\n    outputs: schemas/plan.schema.json\n");
        var coordinator = new FlowCoordinator(
            definition,
            new Reasoned(Runner(kit, agents.AsRunner())),
            new ConfiguredRunnerFacts(new DssHarness.Core.Configuration.HarnessConfig()),
            TimeProvider.System,
            runName: "o1",
            place: new FlowRunPlace(kit.Main, "o1", StartCommit: null, definition),
            agentModels: new Dictionary<string, string>(StringComparer.Ordinal) { ["think"] = "claude-opus-5-5" },
            workflowFile: named => File.ReadAllText(Path.Combine(kit.Main, ".harness-config", "workflows", "flow", named)));

        var session = coordinator.RunAsync(Token);
        await agents.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        coordinator.RequestPause("interrupt");
        var end = await session.WaitAsync(TimeSpan.FromSeconds(30), Token);
        var think = coordinator.Instances["think"];

        Assert.Equal(FlowRunStatus.Paused, end.Status);
        Assert.Equal(["think"], end.Pause!.Handoffs);
        Assert.Equal(new AgentHandoffAsked("the run is pausing", null), agents.AskedWith);
        Assert.Equal((FlowInstanceState.Stopped, new FlowHandoff("Done: the parser. Left: its tests.", "agent")), (think.State, think.Handoff));
    }

    /// <summary>The step runner a composed run is driven by: every step the real reason step runner's, as the service routes a reason step.</summary>
    private sealed class Reasoned(ReasonStepRunner reason) : IFlowStepRunner
    {
        public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
            => reason.RunAsync(run, cancellationToken);

        public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
            => reason.HandoffAsync(run, cancellationToken);
    }

    /// <summary>
    /// An agent runner that works until the engine asks for the handoff, and answers it as a client does: the turn is
    /// answered, the run ends handed off, and the result - its answer in it - is written to the step's own directory.
    /// </summary>
    private sealed class HandingOff(DssHarness.Core.FileSystem.IFileSystem fileSystem, string answer)
    {
        /// <summary>Completes once the run has begun.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>What the engine asked the handoff with, once it did.</summary>
        public AgentHandoffAsked? AskedWith { get; private set; }

        public IAgentRunner AsRunner() => new Running(this);

        private sealed class Running(HandingOff owner) : IAgentRunner
        {
            public async Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
            {
                owner.Started.TrySetResult();
                owner.AskedWith = await step.Handoff!.WhenAsked.WaitAsync(cancellationToken);

                var result = new AgentRunResult(AgentRunStatus.HandedOff, Handoff: new AgentHandoff(owner.Answer));
                DssHarness.Core.FileSystem.JsonStateFile.Write(owner.FileSystem, Path.Combine(step.RecordDirectory!, AgentRunner.ResultFileName), result);
                return result;
            }

            public async IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                _ = await RunAsync(step, null, cancellationToken);
                yield break;
            }
        }

        /// <summary>Where the result is written.</summary>
        private DssHarness.Core.FileSystem.IFileSystem FileSystem { get; } = fileSystem;

        /// <summary>The handoff the agent answers.</summary>
        private string Answer { get; } = answer;
    }

    /// <summary>The events of a run, keeping what each turn spent as it was handed on.</summary>
    private sealed class SpendingEvents : IFlowRunEvents
    {
        /// <summary>Every turn's spend, in the order handed on.</summary>
        public List<(long Tokens, double Usd)> Spent { get; } = [];

        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double usd) => Spent.Add((tokens, usd));

        public void HandoffTaken() { }
    }

    /// <summary>The events of a run, counting the handoff it says was taken.</summary>
    private sealed class CountingEvents : IFlowRunEvents
    {
        /// <summary>How many handoffs were taken.</summary>
        public int Taken { get; private set; }

        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double usd) { }

        public void HandoffTaken() => Taken++;
    }
}
