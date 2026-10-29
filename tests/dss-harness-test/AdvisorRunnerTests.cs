using DssHarness.Core.Agents;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Secrets;

namespace DssHarness.Tests;

/// <summary>
/// The advisor runner: one reason step that only reads, worked wherever the run works with its records under the
/// asking step's <c>advice/&lt;n&gt;/</c>, its prompt the question and the bar and the run's inputs, and its output
/// the engine's own schema - the answer one of the question's, the schema itself refusing any other.
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
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "agents", "advisor.md"), "---\nname: advisor\ndescription: recommends\n---\nYou recommend.\n");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "prompts", "recommend.md"), "Question of\n{question.step}\nkind\n{question.kind}\nproblem\n{question.problem}\nThe answers:\n{question.answers}\nThe bar:\n{recommendations}\nThe input:\n{input.request}\n");
        OrchestrationKit.Write(kit.Main, Path.Combine(".harness-config", "workflows", "flow", "recommendations.md"), "Prefer the cheaper answer.\n");

        var runner = new Agents();
        return (kit, runner);
    }

    private AdvisorRunner Runner(OrchestrationKit kit, Agents agents)
        => new(
            kit.Harness.ContextLoader,
            kit.Harness.Platform,
            kit.Harness.OrchestrationStore,
            agents,
            kit.Harness.FileSystem,
            new AgentKeyStore(kit.Harness.FileSystem, kit.Harness.FilePermissions, kit.Harness.Platform),
            kit.Harness.Output);

    private static FlowAdvisorCall Call(OrchestrationKit kit, string? agent = "reader")
        => new(
            new FlowAdvisor("advisor", "prompts/recommend.md", Tier: "best", Model: null, Effort: "high", Client: null, Kinds: []),
            "o1",
            "flow",
            "q1",
            "turnLimit",
            "'think' reached its turns (12 turns)",
            Tried: "attempt 1 of the step",
            Means: null,
            [new FlowQuestionOption("more", "one more of what was bounded"), new FlowQuestionOption("retry", "the step runs again as a new attempt")],
            Step: "think",
            agent,
            Instance: "think",
            Advice: 2,
            new System.Text.Json.Nodes.JsonObject { ["request"] = "build the login" },
            Record: null,
            Bar: "Prefer the cheaper answer.\n",
            new FlowRunPlace(kit.Main, "o1", StartCommit: null, WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: orient\n    type: orient\n")));

    /// <summary>
    /// The advice is one reason step that only reads: its prompt the question - the step, the kind, the problem with
    /// what was tried, each answer with what it does - the bar and the run's inputs, filled by the one grammar; its
    /// output the engine's own schema, the answer one of the question's; its records under
    /// <c>advice/&lt;n&gt;/</c> of the asking step; its model the advisor's tier.
    /// </summary>
    [Fact]
    public async Task TheAdviceIsOneReasonStepThatOnlyReads()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);
        var place = new FlowRunPlace(kit.Main, "o1", StartCommit: null, WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: orient\n    type: orient\n"));
        var call = Call(kit);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: """{"answer":"more","why":"it was nearly done"}""") { Model = "claude-opus-5-5" };

        var advice = await runner.AdviseAsync(call, Token);

        Assert.Equal("more", advice.Answer);
        Assert.Equal("it was nearly done", advice.Why);
        Assert.Equal("claude-opus-5-5", advice.Model);
        Assert.EndsWith(Path.Combine("agents", "reader", "steps", "think", "advice", "2"), advice.Directory);

        var asked = agents.Asked.Single();

        Assert.Equal("claude-opus-5-5", asked.Model);
        Assert.Null(asked.Workspace.Worktree);
        Assert.Equal(Path.Combine(advice.Directory!, "work"), asked.Workspace.WorkDirectory);
        Assert.Equal(
            "Question of\nthink\nkind\nturnLimit\nproblem\n'think' reached its turns (12 turns)\nTried: attempt 1 of the step\nThe answers:\n- more: one more of what was bounded\n- retry: the step runs again as a new attempt\nThe bar:\nPrefer the cheaper answer.\n\nThe input:\nbuild the login\n",
            asked.PromptTemplate);
        Assert.Contains("\"enum\": [\"more\", \"retry\"]", asked.Output!.Schema, StringComparison.Ordinal);
        Assert.Equal("q1", asked.Output!.Name);
        Assert.Null(asked.Output!.AskRounds);
    }

    /// <summary>
    /// Where the advisor's run ends without an answer - it failed, reached a bound, was declined - the advice says so,
    /// and the question it was asked for says one was sought and not obtained.
    /// </summary>
    [Fact]
    public async Task WhereTheRunEndsWithoutAnAnswer_TheAdviceSaysSo()
    {
        using var temp = new TempDirectory();
        var (kit, agents) = await ReadyAsync(temp);
        await kit.CreateReadingAgentAsync("reader");
        var runner = Runner(kit, agents);
        var place = new FlowRunPlace(kit.Main, "o1", StartCommit: null, WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: orient\n    type: orient\n"));
        var call = Call(kit);

        agents.Answer = _ => new AgentRunResult(AgentRunStatus.MaxTurns, Error: "10 turns");

        var bounded = await runner.AdviseAsync(call, Token);

        Assert.Null(bounded.Answer);
        Assert.Contains("10 turns", bounded.NotObtained, StringComparison.Ordinal);
    }
}
