using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Schemas;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// The advisor runner: one reason step that only reads, worked wherever the run works with its records and its own
/// conversations under the asking step's <c>advice/&lt;n&gt;/</c>, numbered after those already there; its prompt the
/// question and the bar and the run's inputs; its output the engine's own schema, in the subset every answer is held to;
/// its spend counted from its turns, an asking ended part way among them.
/// </summary>
public sealed class AdvisorRunnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The agent runner as a fake, keeping every request it was asked and answering as a test says.</summary>
    private sealed class Agents : IAgentRunner
    {
        /// <summary>Every request the runner built, in order.</summary>
        public List<AgentStepRequest> Asked { get; } = [];

        /// <summary>How the run answers; a completed run with no output unless a test changes it.</summary>
        public Func<AgentStepRequest, AgentRunResult> Answer { get; set; } = _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "done");

        /// <summary>The events the run says before it answers, as a client says them.</summary>
        public List<AgentEvent> Says { get; } = [];

        /// <summary>Where set, the run holds after saying its events until its token ends it, as a client cancelled part way does.</summary>
        public TaskCompletionSource? HoldsAfterSaying { get; set; }

        public async Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
        {
            Asked.Add(step);

            foreach (var said in Says)
            {
                await events!.EmitAsync(said);
            }

            if (HoldsAfterSaying is { } held)
            {
                held.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
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

    /// <summary>The kit with a configuration whose providers resolve, whose model is priced, and whose slot holds the model's key.</summary>
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
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", ".env", "a.env"), "ANTHROPIC_KEY=sk-test-one\n");
        kit.Harness.FilePermissions.ProtectSecret(Path.Combine(kit.Main, ".harness-config", "workflows", ".env", "a.env"));
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "agents", "advisor.md"), "---\nname: advisor\ndescription: recommends\n---\nYou recommend.\n");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "prompts", "recommend.md"), "Question of\n{question.step}\nkind\n{question.kind}\nproblem\n{question.problem}\nThe answers:\n{question.answers}\nThe bar:\n{recommendations}\nThe input:\n{input.request}\n");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "recommendations.md"), "Prefer the cheaper answer.\n");

        var runner = new Agents();
        return (kit, runner);
    }

    private AdvisorRunner Runner(OrchestrationKit kit, Agents agents, DssHarness.Core.Agents.ClaudeCode.ClaudeCodeUpdateService? updates = null)
        => new(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            agents,
            kit.Harness.FileSystem,
            new AgentKeyStore(kit.Harness.FileSystem, kit.Harness.FilePermissions, kit.Harness.Platform),
            kit.Harness.Output,
            updates);

    /// <summary>The two answers a turn limit's question takes, neither with a text.</summary>
    private static readonly IReadOnlyList<FlowQuestionOption> LimitAnswers =
        [new FlowQuestionOption("more", "one more of what was bounded"), new FlowQuestionOption("retry", "the step runs again as a new attempt")];

    private static FlowAdvisorCall Call(OrchestrationKit kit, string? agent = "reader", string instance = "think", IReadOnlyList<FlowQuestionOption>? answers = null, double? budgetLeftUsd = null)
        => new(
            new FlowAdvisor("advisor", "prompts/recommend.md", Tier: "best", Model: null, Effort: "high", Client: null, Kinds: []),
            "o1",
            "flow",
            "q1",
            "turnLimit",
            "'think' reached its turns (12 turns)",
            Tried: "attempt 1 of the step",
            Means: null,
            answers ?? LimitAnswers,
            agent,
            instance,
            new JsonObject { ["request"] = "build the login" },
            Record: null,
            Bar: "Prefer the cheaper answer.\n",
            new FlowRunPlace(kit.Main, "o1", StartCommit: null, WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: orient\n    type: orient\n")),
            budgetLeftUsd);

    /// <summary>
    /// The advice is one reason step that only reads: its prompt the question - the step, the kind, the problem with
    /// what was tried, each answer with what it does - the bar and the run's inputs, filled by the one grammar; its
    /// output the engine's own schema, in the subset - the answer one of the question's, the object closed; its records
    /// and its conversations under <c>advice/1/</c> of the asking step's own directory, named on the request; its model
    /// the advisor's tier.
    /// </summary>
    [Fact]
    public async Task TheAdviceIsOneReasonStepThatOnlyReads()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);
        var startsIn = (Directory: (string?)null, There: false);

        agents.Answer = step =>
        {
            startsIn = (step.Workspace.WorkingDirectory, Directory.Exists(step.Workspace.WorkingDirectory));
            return new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: """{"answer":"more","why":"it was nearly done"}""") { Model = "claude-opus-5-5" };
        };

        var advice = await runner.AdviseAsync(Call(kit), Token);
        var directory = Path.Combine(kit.Layout.InstanceDirectory("think", "reader"), "advice", "1");

        Assert.Equal(("more", "it was nearly done", "claude-opus-5-5", directory), (advice.Answer, advice.Why, advice.Model, advice.Directory));

        var asked = agents.Asked.Single();

        Assert.Equal(("claude-opus-5-5", "reader", "think/advice/1"), (asked.Model, asked.Agent, asked.StepInstance));
        Assert.Equal((directory, Path.Combine(directory, "sessions")), (asked.RecordDirectory, asked.SessionsDirectory));
        Assert.Null(asked.Workspace.Worktree);
        Assert.Equal(Path.Combine(directory, "work"), asked.Workspace.WorkDirectory);

        // An agent with no worktree starts the advice in the advice's own work directory, which is there before its
        // client is handed it: a program is never started in a directory that is not there.
        Assert.Equal((Path.Combine(directory, "work"), true), startsIn);
        Assert.Equal(
            "Question of\nthink\nkind\nturnLimit\nproblem\n'think' reached its turns (12 turns)\nTried: attempt 1 of the step\nThe answers:\n- more: one more of what was bounded\n- retry: the step runs again as a new attempt\nThe bar:\nPrefer the cheaper answer.\n\nThe input:\nbuild the login\n",
            asked.Prompt);
        Assert.Equal(
            """{"type":"object","properties":{"answer":{"type":"string","enum":["more","retry"]},"why":{"type":"string"}},"required":["answer","why"],"additionalProperties":false}""",
            asked.Output!.Schema);
        Assert.Empty(JsonSchemaSubset.SubsetProblems(JsonNode.Parse(asked.Output.Schema)!));
        Assert.Equal("q1", asked.Output.Name);
        Assert.Null(asked.Output.AskRounds);
    }

    /// <summary>
    /// The advice asked of a step whose agent writes in a worktree works where that agent works - the worktree's path on
    /// the disk, never its record's address - and only reads it: its own work directory is all it writes. The tool's
    /// own Claude Code directory is kept from it, and every secret the slot answers is in its mask.
    /// </summary>
    [Fact]
    public async Task AnAdviceInAWritingAgentsWorktree_ReadsItAndWritesNothingThere()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateAgentAsync("ag");
        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: """{"answer":"retry","why":"start again"}""");

        var advice = await Runner(kit, agents).AdviseAsync(Call(kit, agent: "ag"), Token);

        Assert.Equal("retry", advice.Answer);
        var asked = agents.Asked.Single();
        var work = Path.Combine(advice.Directory!, "work");
        Assert.Equal((kit.Worktree("ag"), kit.Worktree("ag"), work), (asked.Workspace.Worktree, asked.Workspace.WorkingDirectory, asked.Workspace.WorkDirectory));
        Assert.Equal([work], asked.Ground!.WritableRoots);
        Assert.Contains(new ClaudeTranscripts(kit.Harness.FileSystem, kit.Harness.Platform).ConfigDirectory, asked.Ground.ClaudeCodeDirectories);
        Assert.Equal($"the key {DssHarness.Core.Runners.ActionValues.Mask}", asked.Mask!.Redact("the key sk-test-one"));
    }

    /// <summary>
    /// Where the advisor's order holds the client that starts a program, the program is found as a run's step finds it -
    /// one the slot names read for what it is, nothing installed or brought up to date - and the request carries it and
    /// the version it answered, for the client to start what was found; one not there is why no advice was obtained,
    /// and the advisor's run is never asked.
    /// </summary>
    [Fact]
    public async Task TheAdvisorsClaudeCode_IsFoundAndCarriedOnItsRequest()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var named = await DotnetAsync(kit.Harness);
        var slot = Path.Combine(kit.Main, ".harness-config", "workflows", ".env", "a.env");

        File.WriteAllText(
            Path.Combine(kit.Main, ".harness-config", "config.json"),
            File.ReadAllText(Path.Combine(kit.Main, ".harness-config", "config.json")).Replace("\"client\": \"claude-api\"", "\"client\": \"claude-code\"", StringComparison.Ordinal));
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", ".env", "a.env"), $"ANTHROPIC_KEY=sk-test-one\nCLAUDE_EXECUTABLE={named}\n");
        kit.Harness.FilePermissions.ProtectSecret(slot);
        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: """{"answer":"more","why":"nearly done"}""");

        var updates = new DssHarness.Core.Agents.ClaudeCode.ClaudeCodeUpdateService(
            kit.Harness.ProcessTable,
            kit.Harness.ProcessRunner,
            kit.Harness.FileSystem,
            kit.Harness.FilePermissions,
            kit.Harness.Platform,
            kit.Harness.Identity,
            new AgentKeyStore(kit.Harness.FileSystem, kit.Harness.FilePermissions, kit.Harness.Platform));

        var advice = await Runner(kit, agents, updates).AdviseAsync(Call(kit), Token);

        Assert.Equal("more", advice.Answer);
        var asked = agents.Asked.Single();
        Assert.Equal(named, asked.Program);
        Assert.NotNull(asked.ProgramVersion);

        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", ".env", "a.env"), $"ANTHROPIC_KEY=sk-test-one\nCLAUDE_EXECUTABLE={Path.Combine(kit.Main, "not-there.exe")}\n");
        kit.Harness.FilePermissions.ProtectSecret(slot);

        var missing = await Runner(kit, agents, updates).AdviseAsync(Call(kit), Token);

        Assert.Null(missing.Answer);
        // The finder's own words, whole but for why the machine could not start the program, which is the machine's.
        Assert.Matches(
            $"(?s)^{System.Text.RegularExpressions.Regex.Escape($"'{Path.Combine(kit.Main, "not-there.exe")}', the program the slot names, would not answer its --version: ")}.+"
                + $"{System.Text.RegularExpressions.Regex.Escape(". Update it by hand, or take CLAUDE_EXECUTABLE out of the slot")}$",
            missing.NotObtained!);
        Assert.Single(agents.Asked);
    }

    /// <summary>Where this machine's dotnet is, as the system finds it: a program that answers <c>--version</c>, standing in for Claude Code.</summary>
    /// <param name="harness">The harness whose process runner asks.</param>
    private static async Task<string> DotnetAsync(HarnessFactory harness)
    {
        var where = harness.Platform.Current == DssHarness.Core.Platform.PlatformId.Windows ? "where.exe" : "which";
        var found = await harness.ProcessRunner.RunAsync(new DssHarness.Core.Processes.ProcessRequest { FileName = where, Arguments = ["dotnet"] }, Token);

        return found.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
    }

    /// <summary>
    /// Where some of the question's answers take a text and some do not, the schema says both in the subset - one closed
    /// shape requiring the text for the answers that take one, one with no text for those that take none - and the gate
    /// holds an answer to it: a text answer without its text is refused, a text on an answer that takes none is refused.
    /// </summary>
    [Fact]
    public async Task AnswersThatTakeATextAndAnswersThatDoNot_AreHeldInTheSubset()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");

        _ = await Runner(kit, agents).AdviseAsync(Call(kit, answers: [new FlowQuestionOption("retry", "runs again"), new FlowQuestionOption("answer", "the text goes back", Text: true)]), Token);

        var schema = JsonNode.Parse(agents.Asked.Single().Output!.Schema)!;

        Assert.Equal(
            """{"anyOf":[{"type":"object","properties":{"answer":{"type":"string","enum":["answer"]},"why":{"type":"string"},"text":{"type":"string"}},"required":["answer","why","text"],"additionalProperties":false},{"type":"object","properties":{"answer":{"type":"string","enum":["retry"]},"why":{"type":"string"}},"required":["answer","why"],"additionalProperties":false}]}""",
            schema.ToJsonString());
        Assert.Empty(JsonSchemaSubset.SubsetProblems(schema));
        Assert.Empty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"answer":"answer","why":"w","text":"t"}""")));
        Assert.Empty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"answer":"retry","why":"w"}""")));
        Assert.NotEmpty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"answer":"answer","why":"w"}""")));
        Assert.NotEmpty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"answer":"retry","why":"w","text":"t"}""")));
        Assert.NotEmpty(JsonSchemaSubset.Problems(schema, JsonNode.Parse("""{"answer":"skip","why":"w"}""")));
    }

    /// <summary>
    /// A step that names no agent keeps its advice under its own directory in the run's steps, and no agent's directory is
    /// spelt for it: the request names the records and the conversations there, and no directory of an agent called
    /// after the advisor is made.
    /// </summary>
    [Fact]
    public async Task ForAStepThatNamesNoAgent_TheAdviceIsKeptUnderTheStepsOwnDirectory()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);

        var advice = await Runner(kit, agents).AdviseAsync(Call(kit, agent: null, instance: "build"), Token);
        var directory = Path.Combine(kit.Layout.StepDirectory("build"), "advice", "1");
        var asked = agents.Asked.Single();

        Assert.Equal(directory, advice.Directory);
        Assert.Equal((directory, Path.Combine(directory, "sessions"), "build/advice/1"), (asked.RecordDirectory, asked.SessionsDirectory, asked.StepInstance));
        Assert.True(Directory.Exists(directory));
        Assert.False(Directory.Exists(kit.Layout.AgentDirectory("advisor")));
    }

    /// <summary>
    /// The asking step's directory is spelt by the one owner of where a step instance is kept - a work item's brackets as
    /// the run's store writes them - and the advice's instance keeps the step's own id, with no separator of this machine.
    /// </summary>
    [Fact]
    public async Task AnAskingStepOfAWorkItem_KeepsItsAdviceWhereTheRunKeepsTheStep()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");

        var advice = await Runner(kit, agents).AdviseAsync(Call(kit, instance: "rounds#1.items[2].mend"), Token);
        var asked = agents.Asked.Single();

        Assert.Equal(Path.Combine(kit.Layout.AgentStepsDirectory("reader"), "rounds#1.items@2.mend", "advice", "1"), advice.Directory);
        Assert.Equal((advice.Directory, "rounds#1.items[2].mend/advice/1"), (asked.RecordDirectory, asked.StepInstance));
    }

    /// <summary>
    /// Each advice takes the number after the highest its asking step's directory holds already - one a session before
    /// this one left among them - so no advice is written over another's records.
    /// </summary>
    [Fact]
    public async Task AnAdviceIsNumberedAfterTheAdvicesAlreadyThere()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var advices = Path.Combine(kit.Layout.InstanceDirectory("think", "reader"), "advice");
        Directory.CreateDirectory(Path.Combine(advices, "1"));
        Directory.CreateDirectory(Path.Combine(advices, "3"));
        Directory.CreateDirectory(Path.Combine(advices, "notes"));
        var runner = Runner(kit, agents);

        var first = await runner.AdviseAsync(Call(kit), Token);
        var second = await runner.AdviseAsync(Call(kit), Token);

        Assert.Equal((Path.Combine(advices, "4"), Path.Combine(advices, "5")), (first.Directory, second.Directory));
    }

    /// <summary>
    /// The advisor's launch is held to what is left of the run's dollars before its reserve where that is less than its
    /// own bound - one long advice cannot spend through the reserve - and to its own where the run's is more.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData(0.75, "0.75")]
    [InlineData(-1.0, "0")]
    public async Task TheAdvisorsLaunch_IsHeldToWhatIsLeftOfTheRunsDollars(double? left, string? bound)
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");

        _ = await Runner(kit, agents).AdviseAsync(Call(kit, budgetLeftUsd: left), Token);

        Assert.Equal(bound is null ? null : decimal.Parse(bound, System.Globalization.CultureInfo.InvariantCulture), agents.Asked.Single().Limits.MaxBudgetUsd);
    }

    /// <summary>
    /// What the advice spent is counted from its turns as they are reported: every token of every kind, and the cost at
    /// the configuration's prices for the model serving it.
    /// </summary>
    [Fact]
    public async Task TheAdvicesSpend_CountsEveryKindOfToken_AtTheConfigurationsPrices()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        agents.Says.Add(new UsageReported(new AgentUsage(1000, 200, 3000, 500, 0, 0), new AgentUsage(1000, 200, 3000, 500, 0, 0)));
        agents.Says.Add(new UsageReported(new AgentUsage(100, 20, 0, 0, 0, 0), new AgentUsage(1100, 220, 3000, 500, 0, 0)));
        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: """{"answer":"retry","why":"it went wrong"}""");

        var advice = await Runner(kit, agents).AdviseAsync(Call(kit), Token);

        Assert.Equal(4820, advice.Tokens);
        Assert.Equal((1100 * 10 + 220 * 50 + 3000 * 1 + 500 * 12.5) / 1_000_000.0, advice.Usd!.Value, 9);
    }

    /// <summary>
    /// On a model the configuration gives no price, what the advice spent is its tokens and a cost not known past what
    /// was counted - nothing here - with why, never a cost of nothing: as the reporter's answer says it, by the one count
    /// of what a run spent, and as the run's budget is charged with it.
    /// </summary>
    [Fact]
    public async Task AnAdviceOnAModelWithNoPrice_SaysItsTokens_AndItsCostNotKnown()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var config = Path.Combine(kit.Main, ".harness-config", "config.json");
        File.WriteAllText(config, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(config), @",\s*""priceInputMiss""[^}]*", " "));
        var turn = new AgentUsage(1000, 200, 0, 0, 0, 0);
        agents.Says.Add(new UsageReported(turn, turn));
        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: """{"answer":"retry","why":"it went wrong"}""");

        var advice = await Runner(kit, agents).AdviseAsync(Call(kit), Token);
        var served = agents.Asked.Single().Model!;
        var priced = AgentProviders.Resolve(kit.Harness.ConfigStore.Load(config))!.Providers!.Of(served)
            ?? throw new InvalidOperationException($"'{served}' is no model the configuration lists");

        Assert.Equal<(string?, long?, FlowDollars)>(
            ("retry", 1200L, new FlowDollars(0, AgentPrices.UnknownOf(priced.Provider, priced.Model, turn))),
            (advice.Answer, advice.Tokens, advice.Dollars));
    }

    /// <summary>
    /// An asking its token ends part way - a person answered, or the run stopped - answers with what it had spent by
    /// then, for that was spent, and says the asking was ended.
    /// </summary>
    [Fact]
    public async Task AnAskingEndedPartWay_AnswersWithWhatItHadSpent()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        agents.Says.Add(new UsageReported(new AgentUsage(1000, 200, 0, 0, 0, 0), new AgentUsage(1000, 200, 0, 0, 0, 0)));
        agents.HoldsAfterSaying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ending = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var asking = Runner(kit, agents).AdviseAsync(Call(kit), ending.Token);
        await agents.HoldsAfterSaying.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        await ending.CancelAsync();
        var advice = await asking.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(
            (null, "the asking was ended before the advisor answered", 1200L, (1000 * 10 + 200 * 50) / 1_000_000.0, Path.Combine(kit.Layout.InstanceDirectory("think", "reader"), "advice", "1")),
            (advice.Answer, advice.NotObtained, advice.Tokens, advice.Usd, advice.Directory));
    }

    /// <summary>
    /// Where the advisor's run ends without an answer - it failed, reached a bound, was declined - the advice says why,
    /// in the run's own words, and the question it was asked for says one was sought and not obtained.
    /// </summary>
    [Fact]
    public async Task WhereTheRunEndsWithoutAnAnswer_TheAdviceSaysSo()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.MaxTurns, Error: "10 turns");

        var bounded = await Runner(kit, agents).AdviseAsync(Call(kit), Token);

        Assert.Equal((null, "10 turns"), (bounded.Answer, bounded.NotObtained));
    }
}
