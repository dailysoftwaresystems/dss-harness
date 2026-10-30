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

    private ReasonStepRunner Runner(OrchestrationKit kit, Agents agents, ClaudeCodeUpdateService? updates = null)
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
            new Dictionary<string, FlowValue>(StringComparer.Ordinal) { ["prompt"] = FlowValue.Of(prompt ?? string.Empty) },
            made,
            agent,
            Target: null,
            workedFrom);

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
        var copied = Path.Combine(kit.Layout.WorkDirectory("a9"), "skills", "planning");
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
        Assert.Equal(kit.Layout.WorkDirectory("ag"), asked.Workspace.WorkDirectory);
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

        Assert.Equal($"The person answered:{Environment.NewLine}split the parser item in two", agents.Asked[0].PromptTemplate);

        await runner.RunAsync(Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "decide", null, "ask", Attempt: 1)]), Token);

        Assert.Contains("leaves the questions to you", agents.Asked[1].PromptTemplate, StringComparison.Ordinal);

        await runner.RunAsync(Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "more", null, "turnLimit", Attempt: 1)]), Token);

        Assert.Contains("allowed the step's turn bound once again", agents.Asked[2].PromptTemplate, StringComparison.Ordinal);

        await runner.RunAsync(Run(kit, agent: "reader"), Token);

        Assert.Equal("Plan the work.", agents.Asked[3].PromptTemplate);

        // An earlier attempt's answers are never served: each attempt is its own, and the second begins a fresh start
        // even where the first was answered.
        await runner.RunAsync(Run(kit, agent: "reader", attempt: 2, answers: [new FlowAnswerGiven("q1", "answer", "split the parser item in two", "ask", Attempt: 1)]), Token);

        Assert.Equal("Plan the work.", agents.Asked[4].PromptTemplate);
    }

    /// <summary>
    /// A full context answered <c>compact</c> begins a NEW conversation from the engine's reduction of the attempt's
    /// own - the exhausted conversation is handed to no client, however plainly it is there, and stays whole on the
    /// disk.
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

        await Runner(kit, agents).RunAsync(
            Run(kit, agent: "reader", answers: [new FlowAnswerGiven("q1", "compact", null, "contextLimit", Attempt: 1)]),
            Token);

        Assert.Null(agents.Asked.Single().Conversation);
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

        Assert.StartsWith($"<handoff>{Environment.NewLine}what was left{Environment.NewLine}</handoff>", agents.Asked.Single().PromptTemplate, StringComparison.Ordinal);
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
