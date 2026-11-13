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

        public void Used(long tokens, double? usd, string? uncounted = null) { }

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

            // One launch, as the runner makes it: its first message is written and the step told what of the graph it holds -
            // a conversation that goes on where the step hands one, a new one otherwise.
            step.CodebaseGiven?.Invoke(step.Compaction is { BeforeItsTask: false }
                ? AgentCodebaseGiven.None
                : AgentPromptParts.CodebaseOf(step.Map, step.Codebase, step.CodebaseChanges, newConversation: step.Conversation is null));

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

    private static ReasonStepRunner Runner(OrchestrationKit kit, IAgentRunner agents, ClaudeCodeUpdateService? updates = null, DssHarness.Core.Agents.Isolation.IAgentIsolation? isolation = null, DssHarness.Core.Codebase.ICodebaseGraphService? graphService = null)
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
            isolation ?? new TestIsolation(),
            kit.Harness.GitClient,
            kit.Harness.ConfigStore,
            updates,
            graphService);

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
    /// The agents' configuration is read when each step starts, as its prompts are, and kept in that step's policy.json:
    /// the ground as resolved - its restrictions from every source at once, the worktree rules and the protected paths -
    /// the web search it asks, and what it runs on, each entry of its order with the key's name alone and never a value.
    /// </summary>
    [Fact]
    public async Task AStepsPolicy_KeptInItsRecords_AsTheConfigurationStoodWhenItStarted()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);

        // A restriction from the configuration and one from the step, to read each with where it stands.
        var config = Path.Combine(kit.Main, ".harness-config", "config.json");
        File.WriteAllText(config, File.ReadAllText(config).Replace("\"worktrees\":", "\"agentPolicy\": { \"restrictions\": [\"Read(secrets/**)\"] },\n  \"worktrees\":", StringComparison.Ordinal));
        var run = Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")) with { WorksIn = "a9" };
        run = run with { Step = run.Step with { Restrictions = ["Run(rm *)"] } };

        var result = await Runner(kit, agents).RunAsync(run, Token);

        Assert.True(result.Passed, result.Error);

        var policyFile = Assert.Single(Directory.EnumerateFiles(kit.Layout.Directory, "policy.json", SearchOption.AllDirectories));
        var kept = File.ReadAllText(policyFile);

        // The slot's key is named, never its value: nothing of the slot's secret is in the run's kept records.
        Assert.DoesNotContain("sk-test-one", kept, StringComparison.Ordinal);

        var policy = JsonNode.Parse(kept)!;

        // The configuration holds the worktree rules and the protected paths on where nothing is said: the record says
        // the policy as resolved, the configuration's own defaults among it.
        Assert.True(policy["agentPolicy"]!["worktreeRules"]!.GetValue<bool>());
        Assert.True(policy["agentPolicy"]!["protectedPaths"]!.GetValue<bool>());
        Assert.Equal(["Read(secrets/**) (config)", "Run(rm *) (agent)"], policy["agentPolicy"]!["restrictions"]!.AsArray().Select(one => one!.GetValue<string>()).Order(StringComparer.Ordinal));

        // The search the step asks, as its search names it: with none named, every source the configuration holds on.
        Assert.Equal(
            DssHarness.Core.Web.Search.WebSearchSources.Of(kit.Harness.ConfigStore.Load(config), DssHarness.Core.Web.Search.WebSearchKinds.OfThisBuild).Chosen(null).Select(source => source.Name),
            policy["agentWebSearch"]!["sources"]!.AsArray().Select(source => source!.GetValue<string>()));

        Assert.Equal(
            """[{"provider":"anthropic","model":"claude-opus-5-5","client":"claude-api","key":"ANTHROPIC_KEY","effort":null}]""",
            policy["runsOn"]!.ToJsonString());
    }

    /// <summary>
    /// A step's search names the sources its web_search asks: an edit of the configuration that takes a source it names
    /// away refuses the step's start with V55's words, as a configuration that no longer reads does - and the step runs
    /// again once the configuration names the source.
    /// </summary>
    [Fact]
    public async Task ASearchThatNoLongerReads_RefusesTheStepsStart_WithTheValidatorsWords()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);

        OrchestrationKit.Write(
            kit.Main,
            Path.Combine(".harness-config", "config.json"),
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
              "defaults": { "agentTier": "best" },
              "agentWebSearch": {
                "sources": {
                  "alpha": { "kind": "json-api", "description": "Alpha's answers", "url": "https://alpha.example.test/?q={query}", "items": "$.items", "title": "{@.title}", "link": "{@.link}" }
                }
              }
            }
            """);

        var step = Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")) with
        {
            WorksIn = "a9",
            Step = new WorkflowStep { Name = "think", Type = "reason", Prompt = "prompts/plan.md", Search = ["alpha"] },
        };

        Assert.True((await Runner(kit, agents).RunAsync(step, Token)).Passed);

        // The side-task's edit: the source taken away, the rest of the configuration whole.
        OrchestrationKit.Write(
            kit.Main,
            Path.Combine(".harness-config", "config.json"),
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
              "defaults": { "agentTier": "best" },
              "agentWebSearch": {
                "sources": {
                  "beta": { "kind": "json-api", "description": "Beta's answers", "url": "https://beta.example.test/?q={query}", "items": "$.items", "title": "{@.title}", "link": "{@.link}" }
                }
              }
            }
            """);

        var refused = await Runner(kit, agents).RunAsync(step with { Attempt = 2 }, Token);

        Assert.False(refused.Passed);
        Assert.Equal(HarnessExit.ConfigInvalid, refused.Exit);
        Assert.StartsWith("'think' searches 'alpha', which is no source or tag of agentWebSearch", refused.Error, StringComparison.Ordinal);
    }

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
    /// Where agentPolicy.isolation is required and the machine has no isolation of an agent's commands, the step does not
    /// start: it ends failed with the code for what could not be started, saying why and what would give it one, and no
    /// agent is asked. The isolation is asked of the step's own ground and its own directory. Where it is preferred, the
    /// step starts, its request carrying why it runs on the words policy alone.
    /// </summary>
    [Fact]
    public async Task AStepWithNoIsolationWhereItIsRequired_DoesNotStart_AndWherePreferredCarriesWhy()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        var isolation = TestIsolation.Unavailable("bubblewrap is not here", "install it");

        var refused = await Runner(kit, agents, isolation: isolation).RunAsync(Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")) with { WorksIn = "a9" }, Token);

        Assert.Equal(
            (false, HarnessExit.ToolMissing, "'think' is refused: agentPolicy.isolation is \"required\" and bubblewrap is not here, so no agent that may run a command starts here; install it"),
            (refused.Passed, refused.Exit, refused.Error));
        Assert.Empty(agents.Asked);

        var (ground, launchDirectory) = Assert.Single(isolation.Asked);

        Assert.Equal([kit.Layout.WorkDirectory("a9")], ground.WritableRoots);
        Assert.Equal(kit.Layout.InstanceDirectory("think", "a9"), launchDirectory);

        var preferred = new TestIsolation((_, _) => new DssHarness.Core.Agents.Isolation.LaunchIsolation("preferred", null, null, "bubblewrap is not here", "install it"));
        var ran = await Runner(kit, agents, isolation: preferred).RunAsync(Run(kit, made: new FlowMadeAgent("a9", "claude-opus-5-5")) with { WorksIn = "a9" }, Token);

        Assert.True(ran.Passed, ran.Error);
        Assert.Equal(
            "agentPolicy.isolation is \"preferred\" and bubblewrap is not here, so the agent's commands run on the words policy alone, with no isolation of the operating system's; install it",
            agents.Asked.Single().Isolation!.Notice);
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
    /// Each skill the prompt names is looked up again where the step starts, its folder copied whole into what the step's
    /// agent is handed, in the step's own directory, and its block put where the placeholder stood - its
    /// <c>${CLAUDE_SESSION_ID}</c> the id the run's first session begins as, which the request hands the client - and the
    /// skill recorded on the request, for the step's stream to say what it was given.
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
        var copied = Path.Combine(OrchestratorLayout.HandedDirectory(kit.Layout.InstanceDirectory("think", "a9")), "skills", "planning");
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
    /// What the step's agent is handed is never written where its commands write: a link one of them planted in its work
    /// directory, where a skill's copy would go, leads no write of the tool's out of the places it writes - the copy is in
    /// the step's own directory, which the agent reads, and what the link leads to is untouched.
    /// </summary>
    [Fact]
    public async Task ALinkPlantedInTheWorkDirectory_LeadsNoCopyOfASkillOutOfIt()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "skills", "planning", "SKILL.md"), "---\ndescription: how to plan\n---\nPlan in steps.\n");
        var work = kit.Layout.WorkDirectory("a9");
        Directory.CreateDirectory(work);
        TestLinks.DirectoryLink(Path.Combine(work, "skills"), outside.Path);

        var handed = await Runner(kit, agents).RunAsync(Run(kit, prompt: "Plan with {skill.planning}.", made: new FlowMadeAgent("a9", "claude-opus-5-5")), Token);

        Assert.True(handed.Passed, handed.Error);
        var directory = OrchestratorLayout.HandedDirectory(kit.Layout.InstanceDirectory("think", "a9"));
        Assert.Equal(Path.Combine(directory, "skills", "planning"), Assert.Single(agents.Asked.Single().Skills).Folder);
        Assert.Contains(directory, agents.Asked.Single().Ground!.ReadRoots);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
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
    /// A step's launch is built as every launch's is: the tool's own Claude Code directory and the places the user keeps
    /// credentials in among those no action of its agent reaches, whatever client it is on; every secret the slot answers in its mask; and the retries its workflow
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
        Assert.Contains(Path.Combine(kit.Harness.Platform.HomeDirectory, ".ssh"), asked.Ground.CredentialPlaces.Select(place => place.Path));
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
        var sessions = Path.Combine(kit.Layout.AgentDirectory("reader"), "sessions", session);
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "conversation.jsonl"), "[]\n", Token);

        // The attempt's two launches as Claude Code sends them: the run context and the task and a question, then a launch
        // whose first message carries the run context, the continuation record and the person's answer as its task, and
        // the work until the context filled.
        var launch = AgentRuns.Of(directory, directory) with { RunContext = "the run context", UserPrompt = "the task" };
        var stream = StoredStream.Open(kit.Harness.FileSystem, directory, new Masking());
        await stream.EmitAsync(new RunStarted(1));
        await stream.EmitAsync(new MessageSent(AgentPromptParts.FirstMessage(launch, newConversation: true), AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new TurnStarted(1));
        await stream.EmitAsync(new AssistantMessage("Which file?", []));
        await stream.EmitAsync(new RunFinished(AgentRunStatus.Asked));
        await stream.EmitAsync(new RunStarted(1));
        await stream.EmitAsync(new SessionStarted("claude-api", "claude-opus-5-5", "hash", [], [], HandedConversation: false, Provider: "anthropic", SessionId: session));
        await stream.EmitAsync(new MessageSent(AgentPromptParts.FirstMessage(launch with { Continuation = "the record of the launch before", UserPrompt = "the person's answer" }, newConversation: true), AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new TurnStarted(1));
        await stream.EmitAsync(new AssistantMessage("Reading it now.", []));
        await stream.EmitAsync(new RunFinished(AgentRunStatus.ContextExhausted));
        stream.Dispose();

        await Runner(kit, agents).RunAsync(
            Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "compact", null, "contextLimit", Attempt: 1)]),
            Token);

        var asked = agents.Asked.Single();
        var first = $"<run-context>{NL}the run context{NL}</run-context>{NL}{NL}<task>{NL}the task{NL}</task>";

        // The record of the attempt's every launch: compacted from its task, the person's answer in its place as the
        // engine's message, and never the record the later launch carried.
        Assert.Null(asked.Conversation);
        Assert.Equal(
            $"{AgentCompaction.SaysWhatItIs}2. Everything up to iteration 0 is reduced to what you wrote and what you called; the last 2 iterations are word for word.{NL}{NL}"
                + $"The run context and the task, as they were sent:{NL}{NL}{first}{NL}{NL}"
                + $"The last iterations, word for word:{NL}{NL}"
                + $"iteration 1 of 2:{NL}Which file?{NL}{NL}{AgentContinuation.EngineSaid}{NL}the person's answer{NL}{NL}"
                + $"iteration 2 of 2:{NL}Reading it now.{NL}{NL}"
                + $"The task again:{NL}{NL}{first}",
            asked.Compaction!.Record);
        Assert.Equal(session, asked.Compaction.Left);
    }

    private static string NL => Environment.NewLine;

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
            [(4700L, (double?)0.02925, (string?)null), (120L, 0.002, null)],
            events.Spent.Select(spent => (spent.Tokens, spent.Usd is { } usd ? Math.Round(usd, 9) : (double?)null, spent.Uncounted)));
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
            workflowFile: (owner, named) => File.ReadAllText(Path.Combine(kit.Main, ".harness-config", "workflows", "flow", named)));

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
        public List<(long Tokens, double? Usd, string? Uncounted)> Spent { get; } = [];

        public void LegPlaced(string leg, bool inACopy) { }

        public void EveryLegPlaced() { }

        public void LegCopyMade(string leg) { }

        public void LegEnded(string leg) { }

        public void Used(long tokens, double? usd, string? uncounted = null) => Spent.Add((tokens, usd, uncounted));

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

        public void Used(long tokens, double? usd, string? uncounted = null) { }

        public void HandoffTaken() => Taken++;
    }


    /// <summary>The source the graph tests' tree holds: one class and its method.</summary>
    private const string PlannerSource = "namespace Demo;\n\npublic sealed class Planner\n{\n    public int Plan()\n    {\n        return 1;\n    }\n}\n";

    /// <summary>
    /// A workflow whose graphify step is upstream of <c>think</c>, with what a test puts at its head and among
    /// <c>think</c>'s keys, and the steps it adds after it.
    /// </summary>
    private static WorkflowDefinition Graphed(string head = "", string think = "", string after = "")
        => WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            $"name: flow\n{head}steps:\n"
                + "  - name: codebase\n    type: graphify-codebase\n"
                + $"  - name: think\n    type: reason\n    dependsOn: [codebase]\n{think}    prompt: prompts/plan.md\n    outputs: schemas/plan.schema.json\n"
                + after);

    /// <summary>The sync of the base of run o1's graph, as the engine asks one.</summary>
    private static DssHarness.Core.Codebase.CodebaseGraphSync BaseSync(OrchestrationKit kit)
        => new(new DssHarness.Core.Repository.HarnessLayout(kit.Main, kit.Main), kit.Layout, DssHarness.Core.Codebase.CodebaseGraphShape.Default, Path.Combine(kit.Main, OrchestrationKit.Settings.Root), Rebuild: false, "test");

    /// <summary>The kit's tree holding <c>src/Planner.cs</c> and what a test adds, committed, and run o1's graph synced on it.</summary>
    private static async Task GraphedTreeAsync(OrchestrationKit kit, params (string Path, string Text)[] more)
    {
        OrchestrationKit.Write(kit.Main, Path.Combine("src", "Planner.cs"), PlannerSource);

        foreach (var (path, text) in more)
        {
            OrchestrationKit.Write(kit.Main, path, text);
        }

        await kit.Harness.CommitAllAsync(kit.Main, "code", Token);
        _ = await kit.Harness.CodebaseGraph.SyncAsync(BaseSync(kit), Token);
    }

    /// <summary>
    /// The run of <paramref name="definition"/>'s step declared at <paramref name="path"/>, as instance
    /// <paramref name="instance"/>, worked as <paramref name="agent"/> - the engine's own read-only agent made for it, or
    /// one the test made where <paramref name="made"/> is false.
    /// </summary>
    private static FlowStepRun Graphing(OrchestrationKit kit, WorkflowDefinition definition, string path = "think", string? instance = null, string agent = "a9", bool made = true, string prompt = "Plan the work.")
    {
        var run = Run(kit, prompt, made: made ? new FlowMadeAgent(agent, "claude-opus-5-5") : null) with { WorksIn = agent };

        return run with
        {
            Step = FlowScope.At(path, definition)!.Step,
            Instance = instance ?? path,
            Path = path,
            Place = run.Place! with { Definition = definition },
        };
    }

    /// <summary>The graph lines step instance <paramref name="instance"/> of agent <paramref name="agent"/> left.</summary>
    private static IReadOnlyList<GraphStepLine> LinesOf(OrchestrationKit kit, string instance, string agent = "a9")
        => kit.Harness.OrchestrationStore.ReadGraphStepLines(kit.Layout.InstanceDirectory(instance, agent));

    /// <summary>
    /// A reason step that has a graph - a graphify-codebase step upstream of it, the run's graph made - is given its
    /// context pack as its codebase section, which the one writer of a first message's parts wraps once in its
    /// <c>&lt;codebase&gt;</c> tags, and the one tool <c>graph</c> under <c>read</c>, with the operations its agent's
    /// capabilities give and the narrowing of its reads; its launch's first message is a line of its <c>graph.jsonl</c>,
    /// its working set is written beside its output when it ends, and its figures are held in its result for its record.
    /// </summary>
    [Fact]
    public async Task AReasonStepThatHasAGraph_IsGivenItsPackAndItsTool_WithTheOperationsItsAgentGives()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(Graphing(kit, Graphed()), Token);

        Assert.True(result.Passed, result.Error);

        var asked = agents.Asked.Single();

        // The section is the pack's text, and the first message wraps it once.
        Assert.False(asked.Codebase!.Contains("<codebase>", StringComparison.Ordinal), $"the section holds its own tags:\n{asked.Codebase}");
        var first = AgentPromptParts.FirstMessage(AgentRuns.Of(temp.Combine("step"), temp.Combine("work")) with { Codebase = asked.Codebase, Map = asked.Map, UserPrompt = asked.Prompt }, newConversation: true);
        Assert.Equal(1, first.Split("<codebase>").Length - 1);
        Assert.Equal(asked.Codebase, AgentPromptParts.PartOf(first, AgentPromptParts.CodebaseTag));

        var support = asked.Graph;
        Assert.NotNull(support);
        Assert.NotNull(support.Tool);
        Assert.True(support.Narrow);
        Assert.Equal([GraphOp.Find, GraphOp.Node, GraphOp.Related, GraphOp.Outline, GraphOp.Body, GraphOp.Edit, GraphOp.WholeFile], support.Operations!.Order());

        var promptLine = Assert.IsType<GraphPromptLine>(Assert.Single(LinesOf(kit, "think")));
        Assert.Null(promptLine.Why);
        Assert.True(promptLine.Nodes > 0, $"the pack held nothing: {asked.Codebase}");
        Assert.True(promptLine.Tokens > 0);
        Assert.Equal(0, promptLine.ChangesTokens);

        Assert.True(File.Exists(Path.Combine(kit.Layout.InstanceDirectory("think", "a9"), "working-set.json")), "the working set was not written");
        Assert.Equal(promptLine.Nodes, result.Graph!["pack"]!["nodes"]!.GetValue<int>());
        Assert.Equal("tools", result.Graph["mode"]!.GetValue<string>());
        Assert.Equal("base", result.Graph["view"]!.GetValue<string>());
        Assert.Equal(WorkflowStep.GraphFromTask, result.Graph["from"]!.GetValue<string>());
    }

    /// <summary>
    /// A step that works in an agent's worktree is served its graph through a request of that worktree's view: the changes
    /// its change-first pack leads with are the worktree's own work against its base - not the main checkout's - and the
    /// view it is served is the agent's.
    /// </summary>
    [Fact]
    public async Task AStepWorkingInAnAgentsWorktree_IsServedItsOwnWorktreesChanges()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        var worktree = await kit.CreateAgentAsync("a1");

        // The agent's own work: Plan answers otherwise in its worktree, the main checkout as it was.
        OrchestrationKit.Write(worktree, Path.Combine("src", "Planner.cs"), PlannerSource.Replace("return 1;", "return 2;", StringComparison.Ordinal));
        _ = await kit.Harness.CodebaseGraph.SyncAsync(BaseSync(kit) with { Worktree = new DssHarness.Core.Codebase.CodebaseGraphWorktree("a1", worktree) }, Token);

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph)
            .RunAsync(Graphing(kit, Graphed(think: "    graphFrom: changes\n"), agent: "a1", made: false, prompt: "Go on."), Token);

        Assert.True(result.Passed, result.Error);

        var asked = agents.Asked.Single();

        Assert.Equal("a1", result.Graph!["view"]!.GetValue<string>());
        Assert.True(asked.Codebase!.Contains("1 file(s) differing from its base", StringComparison.Ordinal), $"the pack does not lead with the worktree's change:\n{asked.Codebase}");
        Assert.Contains("\nchanged:\n", asked.Codebase, StringComparison.Ordinal);
        Assert.Contains("member public int Plan() | src/Planner.cs:5-8", asked.Codebase, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reason step that has a graph is given the run's repository map - made once, from the base as it stands when the
    /// first graphed reason step is given its prompt, kept in <c>graph/map.md</c> with what it was made from in
    /// <c>graph/map.json</c> - as the <c>&lt;repository-map&gt;</c> part ahead of its pack. The base moves - a fold lands
    /// - and the map does not: a step after it, the run resumed, is given the same bytes, so a client that caches a
    /// prefix reads the map from the cache.
    /// </summary>
    [Fact]
    public async Task AReasonStepThatHasAGraph_IsGivenTheRunMap_MadeOnceAndReadAsItIs()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        var runner = Runner(kit, agents, graphService: kit.Harness.CodebaseGraph);
        var run = Graphing(kit, Graphed());

        var first = await runner.RunAsync(run, Token);

        Assert.True(first.Passed, first.Error);

        var asked = agents.Asked.Single();

        Assert.NotNull(asked.Map);
        Assert.Contains("the graph tool answers for the tree as it is now", asked.Map, StringComparison.Ordinal);
        Assert.True(File.Exists(kit.Layout.GraphMapFile), "the map was not kept");
        Assert.True(File.Exists(kit.Layout.GraphMapMetaFile), "what the map was made from was not kept");
        Assert.True(first.Graph!["mapTokens"]!.GetValue<int>() > 0, "the map's tokens are not in the step's figures");

        var promptLine = Assert.IsType<GraphPromptLine>(Assert.Single(LinesOf(kit, "think")));
        Assert.Equal(DssHarness.Core.Codebase.EstimatedTokens.Of(asked.Map!), promptLine.MapTokens);

        // The base moves - as a fold lands its work in the tree and the graph is synced again - and the map does not:
        // the step after reads the same bytes, made once.
        var kept = File.ReadAllText(kit.Layout.GraphMapFile);
        OrchestrationKit.Write(kit.Main, Path.Combine("src", "Other.cs"), "namespace Demo;\n\npublic sealed class Other\n{\n}\n");
        await kit.Harness.CommitAllAsync(kit.Main, "more code", Token);
        _ = await kit.Harness.CodebaseGraph.SyncAsync(BaseSync(kit), Token);

        var second = await runner.RunAsync(run with { Visit = 2 }, Token);

        Assert.True(second.Passed, second.Error);

        Assert.Equal(asked.Map, agents.Asked[^1].Map);
        Assert.Equal(kept, File.ReadAllText(kit.Layout.GraphMapFile));
    }

    /// <summary>
    /// The map is made once for each ground of reads the run's steps share: a step whose read rules deny a file is given a
    /// map of its own, kept beside the run's and named by a digest of those rules, which holds no declaration of that
    /// file - while the steps no read rule narrows share the run's, which lists it.
    /// </summary>
    [Fact]
    public async Task AStepWhoseReadsDenyAFile_IsGivenAMapThatHoldsNoneOfItsDeclarations()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit, (Path.Combine("src", "Keys.cs"), "namespace Demo;\n\npublic static class Keys\n{\n    public const string Token = \"t\";\n}\n"));
        var definition = Graphed(after: "  - name: look\n    type: reason\n    dependsOn: [codebase]\n    restrictions: [\"Read(src/Keys.cs)\"]\n    prompt: prompts/plan.md\n    outputs: schemas/plan.schema.json\n");
        var runner = Runner(kit, agents, graphService: kit.Harness.CodebaseGraph);

        var open = await runner.RunAsync(Graphing(kit, definition), Token);
        var narrowed = await runner.RunAsync(Graphing(kit, definition, "look"), Token);

        Assert.True(open.Passed, open.Error);
        Assert.True(narrowed.Passed, narrowed.Error);

        Assert.Contains("class Keys | src/Keys.cs", agents.Asked[0].Map, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Keys.cs", agents.Asked[1].Map, StringComparison.Ordinal);
        Assert.Contains("class Planner | src/Planner.cs", agents.Asked[1].Map, StringComparison.Ordinal);

        // Each kept on its own: the run's for the steps no rule narrows, the narrowed steps' named by their rules.
        var maps = Directory.EnumerateFiles(kit.Layout.GraphDirectory, "map*.md").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, maps.Count);
        Assert.Equal("map.md", maps[^1]);
        Assert.Equal(agents.Asked[1].Map, File.ReadAllText(Path.Combine(kit.Layout.GraphDirectory, maps[0]!)));
    }

    /// <summary>
    /// A map that cannot be kept - its file in the way - never fails the step: it is said, and the step is served the
    /// pack and the tool without a map; the next step tries again.
    /// </summary>
    [Fact]
    public async Task AMapThatCannotBeKept_IsSaid_AndTheStepGoesOnWithoutIt()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        Directory.CreateDirectory(kit.Layout.GraphMapFile);

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(Graphing(kit, Graphed()), Token);

        Assert.True(result.Passed, result.Error);

        var asked = agents.Asked.Single();

        Assert.Null(asked.Map);
        Assert.NotNull(asked.Codebase);
        Assert.Contains($"the repository map of run 'o1' was not made or read for 'think': ", kit.Harness.StandardError.ToString() + kit.Harness.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, result.Graph!["mapTokens"]!.GetValue<int>());
    }

    /// <summary>
    /// A step that asks for no pack in a workflow that asks for no map - both valid - is served its tool and nothing in its
    /// first message, its launch's line saying why it was given no pack; it never fails over it.
    /// </summary>
    [Fact]
    public async Task AStepThatAsksForNoPackAndNoMap_IsServedItsTool_AndItsLineSaysWhy()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph)
            .RunAsync(Graphing(kit, Graphed(head: "codebaseGraph:\n  mapTokens: 0\n", think: "    graphPromptTokens: 0\n")), Token);

        Assert.True(result.Passed, result.Error);

        var asked = agents.Asked.Single();

        Assert.Null(asked.Map);
        Assert.Equal(string.Empty, asked.Codebase);
        Assert.NotNull(asked.Graph?.Tool);
        Assert.Equal(new GraphPromptLine(0, 0, 0, 0, 0, FlowStepGraph.NoPackAsked), Assert.Single(LinesOf(kit, "think")));
    }

    /// <summary>
    /// An answer that goes on with a stored conversation of the attempt - one whose first message held the run's map ahead
    /// of the pack - is told what changed of the codebase against that pack: the declarations that moved since, with their
    /// new range and hash, and the nodes of its own pack it was not given. The step carries the map and the pack beside the
    /// changes, for a launch that begins its own conversation is given them; this one goes on, so its line says it was told
    /// the changes and no map and no pack, as its figures do.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatGoesOnWithAStoredConversation_IsToldWhatChangedOfTheCodebase()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        var layout = kit.Layout;

        // The attempt's own stream: a conversation the engine holds, begun with a first message that carried the run's
        // map and then the pack - the graph's own line for Plan, where it stood then.
        await kit.CreateReadingAgentAsync("a9");
        var directory = layout.InstanceDirectory("think", "a9");
        Directory.CreateDirectory(directory);
        var session = Guid.NewGuid().ToString("D");
        var sessions = Path.Combine(layout.AgentDirectory("a9"), "sessions", session);
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "conversation.jsonl"), "[]\n", Token);
        var held = DssHarness.Core.Codebase.GraphLines.Of(Assert.IsType<DssHarness.Core.Codebase.GraphResolve.Found>((await CodebaseGraphQueriesOf(kit.Harness.CodebaseGraph, layout)).Resolve("Plan")).Card);
        var launch = AgentRuns.Of(directory, directory) with { UserPrompt = "the task" };
        var stream = StoredStream.Open(kit.Harness.FileSystem, directory, new Masking());
        await stream.EmitAsync(new RunStarted(1));
        await stream.EmitAsync(new SessionStarted("claude-api", "claude-opus-5-5", "hash", [], [], HandedConversation: false, Provider: "anthropic", SessionId: session));
        await stream.EmitAsync(new MessageSent(AgentPromptParts.FirstMessage(launch with { Map = "the run's map", Codebase = held }, newConversation: true), AgentMessageKind.FirstPrompt));
        await stream.EmitAsync(new TurnStarted(1));
        await stream.EmitAsync(new AssistantMessage("done", []));
        await stream.EmitAsync(new RunFinished(AgentRunStatus.Completed));
        stream.Dispose();

        // The tree moves under it: Plan shifts two lines down, and the graph is synced again.
        OrchestrationKit.Write(kit.Main, Path.Combine("src", "Planner.cs"), "namespace Demo;\n\npublic sealed class Planner\n{\n    // a line that moves what follows it\n    // and one more\n    public int Plan()\n    {\n        return 1;\n    }\n}\n");
        await kit.Harness.CommitAllAsync(kit.Main, "moved", Token);
        _ = await kit.Harness.CodebaseGraph.SyncAsync(BaseSync(kit), Token);

        var run = Graphing(kit, Graphed(), made: false) with { Answers = [new FlowAnswerGiven("q1", "answer", "go on", "ask", Attempt: 1)] };

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(run, Token);

        Assert.True(result.Passed, result.Error);

        var asked = agents.Asked.Single();

        Assert.NotNull(asked.Conversation);
        Assert.NotNull(asked.CodebaseChanges);
        Assert.Contains("1 declaration the conversation held: 1 moved or changed, 0 removed;", asked.CodebaseChanges, StringComparison.Ordinal);
        Assert.True(asked.CodebaseChanges.Contains("src/Planner.cs:7-10", StringComparison.Ordinal), $"the delta:\n{asked.CodebaseChanges}");
        Assert.DoesNotContain("src/Planner.cs:5-8", asked.CodebaseChanges, StringComparison.Ordinal);

        // A launch that begins its own conversation is given the map and the pack: the step carries them all.
        Assert.NotNull(asked.Map);
        Assert.False(string.IsNullOrEmpty(asked.Codebase));

        // This launch went on: its line and its figures say the changes it was told, no map and no pack.
        var told = DssHarness.Core.Codebase.EstimatedTokens.Of(asked.CodebaseChanges);
        Assert.Equal(new GraphPromptLine(0, 0, 0, 0, 0, ChangesTokens: told), Assert.Single(LinesOf(kit, "think")));
        Assert.Equal((0, 0, told), (result.Graph!["mapTokens"]!.GetValue<int>(), result.Graph["pack"]!["nodes"]!.GetValue<int>(), result.Graph["changesTokens"]!.GetValue<int>()));
    }

    private static async Task<DssHarness.Core.Codebase.CodebaseGraphQueries> CodebaseGraphQueriesOf(DssHarness.Core.Codebase.ICodebaseGraphService graph, OrchestratorLayout layout)
        => new(await graph.ReadOnlyAsync(layout, null, Token) ?? throw new Xunit.Sdk.XunitException("the run's graph did not read"));

    /// <summary>
    /// Step <c>think</c>'s own stream as the engine stored it, for agent <c>a9</c>: one run of each launch - its session,
    /// handed the stored conversation or begun afresh, then its first message - the last launch's conversation held under
    /// the agent's sessions, for an answer to go on with.
    /// </summary>
    private static async Task StoredLaunchesAsync(OrchestrationKit kit, params (bool Handed, string FirstMessage)[] launches)
    {
        var directory = kit.Layout.InstanceDirectory("think", "a9");
        Directory.CreateDirectory(directory);
        var stream = StoredStream.Open(kit.Harness.FileSystem, directory, new Masking());
        string? session = null;

        foreach (var (handed, first) in launches)
        {
            session = handed && session is not null ? session : Guid.NewGuid().ToString("D");
            await stream.EmitAsync(new RunStarted(1));
            await stream.EmitAsync(new SessionStarted("claude-api", "claude-opus-5-5", "hash", [], [], HandedConversation: handed, Provider: "anthropic", SessionId: session));
            await stream.EmitAsync(new MessageSent(first, AgentMessageKind.FirstPrompt));
            await stream.EmitAsync(new TurnStarted(1));
            await stream.EmitAsync(new AssistantMessage("done", []));
            await stream.EmitAsync(new RunFinished(AgentRunStatus.Completed));
        }

        stream.Dispose();

        var sessions = Path.Combine(kit.Layout.AgentDirectory("a9"), "sessions", session!);
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "conversation.jsonl"), "[]\n", Token);
    }

    /// <summary>
    /// An answer that goes on with a stored conversation is told what moved of what that conversation was told even where
    /// its own pack holds nothing - its cap below the pack's first line: the changes are made against the pack as it was
    /// made, never left out because the part a conversation of its own would be given is empty.
    /// </summary>
    [Fact]
    public async Task AnAnswerWhosePackHoldsNothing_IsStillToldWhatMovedOfWhatItsConversationHeld()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        await kit.CreateReadingAgentAsync("a9");
        var held = DssHarness.Core.Codebase.GraphLines.Of(Assert.IsType<DssHarness.Core.Codebase.GraphResolve.Found>((await CodebaseGraphQueriesOf(kit.Harness.CodebaseGraph, kit.Layout)).Resolve("Plan")).Card);
        var launch = AgentRuns.Of(kit.Layout.InstanceDirectory("think", "a9"), kit.Main) with { UserPrompt = "the task" };
        await StoredLaunchesAsync(kit, (false, AgentPromptParts.FirstMessage(launch with { Map = "the run's map", Codebase = held }, newConversation: true)));

        // The tree moves under it: Plan shifts two lines down, and the graph is synced again.
        OrchestrationKit.Write(kit.Main, Path.Combine("src", "Planner.cs"), "namespace Demo;\n\npublic sealed class Planner\n{\n    // a line that moves what follows it\n    // and one more\n    public int Plan()\n    {\n        return 1;\n    }\n}\n");
        await kit.Harness.CommitAllAsync(kit.Main, "moved", Token);
        _ = await kit.Harness.CodebaseGraph.SyncAsync(BaseSync(kit), Token);

        var run = Graphing(kit, Graphed(think: "    graphPromptTokens: 60\n"), made: false) with { Answers = [new FlowAnswerGiven("q1", "answer", "yes", "ask", Attempt: 1)] };

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(run, Token);

        Assert.True(result.Passed, result.Error);

        var asked = agents.Asked.Single();

        Assert.NotNull(asked.Conversation);
        Assert.Equal(string.Empty, asked.Codebase);
        Assert.NotNull(asked.CodebaseChanges);
        Assert.StartsWith("1 declaration the conversation held: 1 moved or changed, 0 removed;", asked.CodebaseChanges, StringComparison.Ordinal);
        Assert.True(asked.CodebaseChanges.Contains("src/Planner.cs:7-10", StringComparison.Ordinal), $"the delta:\n{asked.CodebaseChanges}");
    }

    /// <summary>
    /// What a conversation was told of the codebase begins afresh with every conversation the step begins, whether or not
    /// its first message holds a pack: an answer that goes on with a conversation begun with no pack - its pack held
    /// nothing, the walk moved it to another provider - is told its own pack's nodes as new, never a delta of what an
    /// earlier conversation was told.
    /// </summary>
    [Fact]
    public async Task AConversationBegunWithNoPack_IsToldItsOwnPackAsNew_NeverADeltaOfAnEarlierOne()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        await kit.CreateReadingAgentAsync("a9");
        var held = DssHarness.Core.Codebase.GraphLines.Of(Assert.IsType<DssHarness.Core.Codebase.GraphResolve.Found>((await CodebaseGraphQueriesOf(kit.Harness.CodebaseGraph, kit.Layout)).Resolve("Plan")).Card);
        var launch = AgentRuns.Of(kit.Layout.InstanceDirectory("think", "a9"), kit.Main) with { UserPrompt = "the task" };

        // The first conversation was given the pack; a second began afresh with the map alone; the answer goes on with
        // the second.
        await StoredLaunchesAsync(
            kit,
            (false, AgentPromptParts.FirstMessage(launch with { Map = "the run's map", Codebase = held }, newConversation: true)),
            (false, AgentPromptParts.FirstMessage(launch with { Map = "the run's map" }, newConversation: true)));

        var run = Graphing(kit, Graphed(), made: false) with { Answers = [new FlowAnswerGiven("q1", "answer", "go on with Plan", "ask", Attempt: 1)] };

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(run, Token);

        Assert.True(result.Passed, result.Error);

        var asked = agents.Asked.Single();

        Assert.NotNull(asked.CodebaseChanges);
        Assert.Matches("^0 declarations the conversation held: 0 moved or changed, 0 removed; [1-9][0-9]* nodes? of this step's pack added\n", asked.CodebaseChanges);
    }

    /// <summary>
    /// A working set that cannot be written - the disk refuses it - never fails a step that ended well: it is said, the
    /// step's ending stands, and its figures are its own.
    /// </summary>
    [Fact]
    public async Task AWorkingSetTheDiskRefuses_IsSaid_AndTheStepsEndingStands()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);

        // A directory where the working set's file would be: the disk refuses the file at once.
        Directory.CreateDirectory(Path.Combine(kit.Layout.InstanceDirectory("think", "a9"), OrchestratorLayout.StepWorkingSetFileName));

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(Graphing(kit, Graphed()), Token);

        Assert.True(result.Passed, result.Error);
        Assert.NotNull(result.Graph);
        Assert.Contains("the working set of 'think' was not written: ", kit.Harness.StandardError.ToString() + kit.Harness.StandardOutput.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A workflow that asks for no map - <c>codebaseGraph.mapTokens: 0</c> - gives no step one, and none is made: the
    /// pack and the tool say what the tree is alone.
    /// </summary>
    [Fact]
    public async Task AWorkflowThatAsksForNoMap_GivesItsStepsNone()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(Graphing(kit, Graphed(head: "codebaseGraph:\n  mapTokens: 0\n")), Token);

        Assert.True(result.Passed, result.Error);
        Assert.Null(agents.Asked.Single().Map);
        Assert.False(File.Exists(kit.Layout.GraphMapFile), "a map was made where the workflow asks for none");
        Assert.Equal(0, result.Graph!["mapTokens"]!.GetValue<int>());
    }

    /// <summary>
    /// A reason step whose run's graph was never built - its graphify step left no output - is given the sentence for a
    /// step that left no output in the place of its pack, and its <c>graph</c> tool, still offered, answers the same:
    /// the run goes on from a missing graph as from any missing output.
    /// </summary>
    [Fact]
    public async Task AReasonStepWhoseGraphifyStepLeftNoOutput_IsGivenTheSentenceForAStepThatLeftNoOutput()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(Graphing(kit, Graphed()), Token);

        Assert.True(result.Passed, result.Error);

        var asked = agents.Asked.Single();

        Assert.Equal("(step 'codebase' left no output: it has not run)", asked.Codebase);
        Assert.NotNull(asked.Graph);
        Assert.Null(asked.Graph.Tool);
        Assert.Equal("(step 'codebase' left no output: it has not run)", asked.Graph.Gone);

        var promptLine = Assert.IsType<GraphPromptLine>(Assert.Single(LinesOf(kit, "think")));
        Assert.Equal("(step 'codebase' left no output: it has not run)", promptLine.Why);
        Assert.Equal(0, promptLine.Nodes);
        Assert.True(File.Exists(Path.Combine(kit.Layout.InstanceDirectory("think", "a9"), "working-set.json")));
        Assert.Equal("tools", result.Graph!["mode"]!.GetValue<string>());
        Assert.Equal(0, result.Graph["calls"]!.GetValue<int>());
    }

    /// <summary>
    /// A step of the recovery has a graph by default only where a graphify step of the run has ended done before it - the
    /// recovery runs wherever a failure is taken, a build that failed before the graph among them - so it is served none
    /// until one has, and then the run's.
    /// </summary>
    [Fact]
    public async Task AStepOfTheRecovery_HasAGraphOnlyWhereAGraphifyStepOfTheRunHasEndedDone()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        var definition = Graphed(after: "recover:\n  onSuccess: failed-step\n  onFailure: pause\n  steps:\n    - name: diagnose\n      type: reason\n      prompt: prompts/plan.md\n      outputs: schemas/plan.schema.json\n");
        var runner = Runner(kit, agents, graphService: kit.Harness.CodebaseGraph);
        var diagnose = Graphing(kit, definition, "recover.diagnose");

        var before = await runner.RunAsync(diagnose, Token);

        Assert.True(before.Passed, before.Error);
        Assert.Null(agents.Asked[0].Graph);
        Assert.Null(agents.Asked[0].Codebase);
        Assert.Null(before.Graph);

        // The run's graphify step ends done: the recovery's step is served the graph it made.
        var now = DateTimeOffset.UtcNow;
        kit.Harness.OrchestrationStore.WriteFlow(kit.Layout, new FlowFile(
            1, "o1", "flow", "digest", new Dictionary<string, string>(), "Running", new FlowBudgetRecord(0, 0, 0), 0,
            [new FlowStepRecord("codebase", "codebase", false, "Done", 1, 1, [], [], Type: "graphify-codebase", StartedAt: now, FinishedAt: now)],
            [],
            now));

        var after = await runner.RunAsync(diagnose with { Visit = 2 }, Token);

        Assert.True(after.Passed, after.Error);
        Assert.NotNull(agents.Asked[1].Graph?.Tool);
        Assert.Equal("tools", after.Graph!["mode"]!.GetValue<string>());
    }

    /// <summary>
    /// A step of the recovery taken for a failure inside a group runs with an id nested in the failing scope, but is
    /// declared at the recovery's own path, which the engine hands its runner: the work it goes on with resolves from
    /// there, so it works by the definition its chain began with.
    /// </summary>
    [Fact]
    public async Task ARecoveryStepTakenInsideAGroup_WorksByTheDefinitionItsChainBeganWith()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "agents", "diagnostician.md"), "---\nname: diagnostician\ndescription: diagnoses\ncapabilities: [read]\n---\nYou diagnose.\n");
        var definition = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            "name: flow\nsteps:\n"
                + "  - name: planning\n    steps:\n      - name: check\n        type: reason\n        onFailure: recover\n        prompt: prompts/plan.md\n        outputs: schemas/plan.schema.json\n"
                + "recover:\n  onSuccess: failed-step\n  onFailure: pause\n  steps:\n"
                + "    - name: diagnose\n      type: reason\n      agent: diagnostician\n      prompt: prompts/plan.md\n      outputs: schemas/plan.schema.json\n"
                + "    - name: redirect\n      type: reason\n      resume: diagnose\n      dependsOn: [diagnose]\n      prompt: prompts/plan.md\n      outputs: schemas/plan.schema.json\n");
        var run = Graphing(kit, definition, "recover.redirect", instance: "planning.recover.redirect", agent: "reader", made: false);

        var result = await Runner(kit, agents).RunAsync(run, Token);

        Assert.True(result.Passed, result.Error);
        var asked = agents.Asked.Single();
        Assert.Equal(("diagnostician", "You diagnose.\n"), (asked.AgentDefinition, asked.SystemPrompt));
    }

    /// <summary>
    /// A step that names another as its pack's seed is seeded from that step's instance in its own work item - never from
    /// another item's beside it, however late that one ended - and the name is found as the definition's check finds it.
    /// </summary>
    [Fact]
    public async Task AStepSeededFromAnother_IsSeededFromItsInstanceInTheStepsOwnWorkItem()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit, (Path.Combine("src", "Alpha.cs"), "namespace Demo;\n\npublic sealed class Alpha\n{\n}\n"), (Path.Combine("src", "Beta.cs"), "namespace Demo;\n\npublic sealed class Beta\n{\n}\n"));
        var definition = Graphed(after:
            "  - name: items\n    dependsOn: [think]\n    forEach: think.items\n    steps:\n"
                + "      - name: implement\n        type: reason\n        prompt: prompts/plan.md\n        outputs: schemas/plan.schema.json\n"
                + "      - name: review\n        type: reason\n        graphFrom: implement\n        dependsOn: [implement]\n        prompt: prompts/plan.md\n        outputs: schemas/plan.schema.json\n");
        var view = await CodebaseGraphQueriesOf(kit.Harness.CodebaseGraph, kit.Layout);

        // Each item's implement worked on its own class; the second item's ended last.
        var now = DateTimeOffset.UtcNow;
        var records = new List<FlowStepRecord>();

        foreach (var (item, named, ended) in new[] { (0, "Alpha", now), (1, "Beta", now.AddMinutes(1)) })
        {
            var instance = $"items[{item}].implement";
            var card = Assert.IsType<DssHarness.Core.Codebase.GraphResolve.Found>(view.Resolve(named)).Card;
            kit.Harness.OrchestrationStore.WriteWorkingSet(kit.Layout.InstanceDirectory(instance, "a9"), new DssHarness.Core.Codebase.CodebaseWorkingSet([new DssHarness.Core.Codebase.WorkingSetNode(card.Id, [DssHarness.Core.Codebase.WorkingSetUses.Edited], 1, card.Handle, card.Path, card.Start, card.End, card.Hash, Gone: false)]));
            records.Add(new FlowStepRecord(instance, "items.implement", false, "Done", 1, 1, [], [], Type: "reason", StartedAt: ended, FinishedAt: ended, Agent: "a9", Index: item));
        }

        kit.Harness.OrchestrationStore.WriteFlow(kit.Layout, new FlowFile(1, "o1", "flow", "digest", new Dictionary<string, string>(), "Running", new FlowBudgetRecord(0, 0, 0), 0, records, [], now));

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph)
            .RunAsync(Graphing(kit, definition, "items.review", instance: "items[0].review", prompt: "Review it."), Token);

        Assert.True(result.Passed, result.Error);

        var pack = agents.Asked.Single().Codebase!;
        Assert.True(pack.Contains("class Alpha | src/Alpha.cs:3-5", StringComparison.Ordinal), $"the pack is not seeded from its own item's work:\n{pack}");
        Assert.DoesNotContain("src/Beta.cs", pack, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step that goes on with another's work shapes its own pack: its own graphFrom and graphPromptTokens, where it says
    /// them, are what its pack is made by - the shipped flow's menders start theirs from their changes - while its mode is
    /// the step's its chain began with.
    /// </summary>
    [Fact]
    public async Task TheShippedFlowsMender_StartsItsPackFromItsChanges()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        var workflows = Path.Combine(kit.Main, ".harness-config", "workflows");
        WorkflowTemplates.EnsureShipped(workflows, kit.Harness.FileSystem);
        var definition = WorkflowResolver.Resolve(
            WorkflowFileParser.Parse("workflows/implementation/implementation.yml", File.ReadAllText(Path.Combine(workflows, "implementation", "implementation.yml"))),
            "implementation",
            relative => File.ReadAllText(Path.Combine([workflows, .. relative.Split('/')]))).Definition;
        var mend = FlowScope.Declared(definition).First(one => one.Step.Name == "mend" && !one.Path.StartsWith(FlowScope.Recovery + ".", StringComparison.Ordinal));
        var implement = ResumeChain.Root(definition, mend.Path, mend.Step);

        Assert.Equal(("implement", WorkflowStep.GraphFromChanges, (string?)null), (implement.Name, mend.Step.GraphFrom, implement.GraphFrom));

        var config = Path.Combine(kit.Main, ".harness-config", "config.json");
        File.WriteAllText(config, File.ReadAllText(config)
            .Replace("\"tiers\": [\"best\"]", "\"tiers\": [\"best\", \"balanced\"]", StringComparison.Ordinal)
            .Replace("\"efforts\": [\"high\"]", "\"efforts\": [\"high\", \"xhigh\"]", StringComparison.Ordinal)
            .Replace("\"tierConfig\": { \"best\": [\"claude-opus-5-5\"] }", "\"tierConfig\": { \"best\": [\"claude-opus-5-5\"], \"balanced\": [\"claude-opus-5-5\"] }", StringComparison.Ordinal));
        var run = Graphing(kit, definition, mend.Path, instance: "rounds#1.items[0].mend", made: false);
        await kit.CreateReadingAgentAsync("a9");

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(run, Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal(WorkflowStep.GraphFromChanges, result.Graph!["from"]!.GetValue<string>());
    }

    /// <summary>
    /// The step's <c>find</c> breaks a tie by how near each node is to the step's own work - its pack, the files its prompt
    /// names - so of two declarations that match alike, the one its task names comes first, whatever their ids say.
    /// </summary>
    [Fact]
    public async Task AStepsFind_BreaksATieByNearnessToItsWork()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(
            kit,
            (Path.Combine("src", "One", "Widget.cs"), "namespace One;\n\npublic sealed class Widget\n{\n}\n"),
            (Path.Combine("src", "Two", "Widget.cs"), "namespace Two;\n\npublic sealed class Widget\n{\n}\n"));
        var widgets = (await CodebaseGraphQueriesOf(kit.Harness.CodebaseGraph, kit.Layout)).Find("Widget", "type", null, 10, 0).Cards.OrderBy(card => card.Id, StringComparer.Ordinal).ToList();

        Assert.Equal(2, widgets.Count);

        // The task names the namespace of the one the ids put last: its pack holds that one, and not the other.
        var named = widgets[^1].Path;
        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(Graphing(kit, Graphed(), prompt: $"Look under {widgets[^1].Qualified.Split('.')[0]}."), Token);

        Assert.True(result.Passed, result.Error);

        var support = agents.Asked.Single().Graph!;
        var found = await support.Tool!.RunAsync(new JsonObject { ["op"] = "find", ["query"] = "Widget", ["kind"] = "type" }, support.Operations!, Token);
        var near = found.Text.IndexOf($"| {named}:", StringComparison.Ordinal);
        var far = found.Text.IndexOf($"| {widgets[0].Path}:", StringComparison.Ordinal);

        Assert.True(near >= 0 && far > near, $"the nearer declaration is not first:\n{found.Text}");
    }

    /// <summary>
    /// What the agent still read whole is counted on every client's form of a read, as the working set reads its calls:
    /// Claude Code's Read with neither an offset nor a limit as much as view_file with neither a start nor an end - a
    /// ranged read of either is no whole read.
    /// </summary>
    [Fact]
    public async Task EveryClientsWholeFileRead_IsCountedInTheStepsFigures()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await GraphedTreeAsync(kit);
        var directory = kit.Layout.InstanceDirectory("think", "a9");
        Directory.CreateDirectory(directory);
        var stream = StoredStream.Open(kit.Harness.FileSystem, directory, new Masking());
        var calls = new (string Id, string Tool, string Input)[]
        {
            ("1", ClaudeCodeTools.Read, "{\"file_path\":\"src/Planner.cs\"}"),
            ("2", ClaudeCodeTools.Read, "{\"file_path\":\"src/Planner.cs\",\"offset\":2,\"limit\":3}"),
            ("3", AgentTools.ViewFile, "{\"path\":\"src/Planner.cs\"}"),
            ("4", AgentTools.ViewFile, "{\"path\":\"src/Planner.cs\",\"start_line\":2}"),
        };

        foreach (var (id, tool, input) in calls)
        {
            await stream.EmitAsync(new ToolCallStarted(id, tool, AgentToolRunner.ClaudeCode));
            await stream.EmitAsync(new ToolCallFinished(id, true, TimeSpan.Zero, input, "1\tnamespace Demo;"));
        }

        stream.Dispose();

        var result = await Runner(kit, agents, graphService: kit.Harness.CodebaseGraph).RunAsync(Graphing(kit, Graphed()), Token);

        Assert.True(result.Passed, result.Error);
        Assert.Equal(2, result.Graph!["wholeFileReads"]!["count"]!.GetValue<int>());
        Assert.Equal(2 * DssHarness.Core.Codebase.EstimatedTokens.Of("1\tnamespace Demo;"), result.Graph["wholeFileReads"]!["tokens"]!.GetValue<int>());
    }
}
