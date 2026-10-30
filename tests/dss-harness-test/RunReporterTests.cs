using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The run's reporter: one reason step that only reads, run by the reason step's own runner over the engine's facts,
/// its reads narrowed to the run's directory and nowhere else. Neither ask command writes anything under the run but
/// its own <c>asks/</c> record or takes a lock; its cost is the person's, never the run's; where no model words the
/// facts the status still answers with them, and the person's question is refused with the code of why.
/// </summary>
public sealed class RunReporterTests
{
    private const string Workflow = "flow";

    /// <summary>Every bound a test of these waits for.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The agent runner as a fake, keeping every request it was asked and answering as a test says.</summary>
    private sealed class Agents : IAgentRunner
    {
        /// <summary>Every request the runner built, in order.</summary>
        public List<AgentStepRequest> Asked { get; } = [];

        /// <summary>How the run answers; a completed run with no answer unless a test changes it.</summary>
        public Func<AgentStepRequest, AgentRunResult> Answer { get; set; } = _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "All good.");

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

    /// <summary>A repository with one workflow declared that names a reporter, and a run of it paused on its pause step.</summary>
    private static async Task<(HarnessFactory Harness, TempDirectory Tree, Agents Reporter, FlowService Service, string Run)> PausedAsync(bool reporter = true)
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            Workflows = [Workflow],
            Defaults = new() { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers],
            TierConfig = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase),
        });
        await harness.CommitAllAsync(temp.Path, "initial", Token);
        temp.WriteFile(
            $".harness-config/workflows/{Workflow}/{Workflow}.yml",
            "name: flow\n"
            + "steps:\n"
            + "  - name: gate\n"
            + "    type: pause\n"
            + "    question: what now?\n"
            + "    options: [stay, go]\n"
            + "  - name: build\n"
            + "    type: orient\n"
            + "    dependsOn: [gate]\n"
            + (reporter
                ? "reporter:\n  agent: reporter\n  statusPrompt: prompts/status.md\n  askPrompt: prompts/ask.md\n"
                : string.Empty));
        temp.WriteFile($".harness-config/workflows/{Workflow}/agents/reporter.md", "---\nname: reporter\ndescription: words the run's facts\nmaxTurns: 12\nmaxMinutes: 3\ncapabilities: [read]\n---\nYou word the run's facts.\n");
        temp.WriteFile($".harness-config/workflows/{Workflow}/prompts/status.md", "The facts:\n{status.facts}\nThe records:\n{status.records}\n");
        temp.WriteFile($".harness-config/workflows/{Workflow}/prompts/ask.md", "The question:\n{status.question}\nThe facts:\n{status.facts}\nThe records:\n{status.records}\n");
        OrchestrationKit.Write(temp.Path, Path.Combine(".harness-config", "workflows", ".env", "a.env"), "ZAI_API_KEY=sk-test-one\n");
        harness.FilePermissions.ProtectSecret(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "a.env"));

        var reporterRunner = new Agents();
        var service = new FlowService(
            harness.ContextLoader,
            harness.Platform,
            harness.FileSystem,
            harness.OrchestrationStore,
            harness.Identity,
            new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient),
            new WorkflowValidationService(harness.ContextLoader, harness.FileSystem, harness.Platform, new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform), DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild),
            new AskingRunner(),
            harness.OrchestrationLog,
            harness.ChildSessions,
            harness.PathBudget,
            harness.Output,
            harness.GitClient,
            harness.AgentService,
            reporterRunner,
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform));

        var started = await service.RunAsync(temp.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        Assert.Equal(FlowExit.Paused, started.ExitCode);

        return (harness, temp, reporterRunner, service, "r1");
    }

    /// <summary>Every file under the run's directory, by its path and bytes - the run's state as it stands.</summary>
    private static Dictionary<string, byte[]> Snapshot(string runDirectory)
        => Directory.EnumerateFiles(runDirectory, "*", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}asks{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(file => file, File.ReadAllBytes, StringComparer.Ordinal);

    /// <summary>
    /// The reporter words the facts, reading the run alone: its reads narrowed to the run's directory, no command, no
    /// write and no web, its answer masked, its prompt the facts and where the records are - and neither command
    /// writes anything under the run but its own <c>asks/</c> record, its cost the person's and never the run's.
    /// </summary>
    [Fact]
    public async Task TheReporterWordsTheFacts_ReadingTheRunAlone_NothingOfTheRunWritten()
    {
        var (harness, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;
        var runDirectory = Path.Combine(temp.Path, ".orchestrators", run);
        var before = Snapshot(runDirectory);

        var outcome = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith($"run '{run}' (workflow '{Workflow}'), as of ", outcome.Message, StringComparison.Ordinal);
        Assert.Equal("All good.", outcome.Details!.Single(line => line == "All good."));

        // Nothing of the run was written or locked: the run's files stand as they stood, but for the call's own asks/.
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), Snapshot(runDirectory).Keys.Order(StringComparer.Ordinal));
        Assert.All(before, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));

        var asked = reporter.Asked.Single();
        var asks = asked.RecordDirectory!;

        Assert.StartsWith(runDirectory, asks, StringComparison.Ordinal);
        Assert.Contains($"{Path.DirectorySeparatorChar}asks{Path.DirectorySeparatorChar}", asks, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(asks, "facts.md")), "the facts as given are kept in the call's own record");
        Assert.True(File.Exists(Path.Combine(asks, "ask.json")), "the call's own record holds ask.json");
        Assert.Contains($"Record: {asks}", outcome.Details!, StringComparer.Ordinal);

        // The reporter reads the run's directory and nowhere else: no command, no write and no web, whatever its file says.
        Assert.Equal(runDirectory, asked.Ground!.ReporterRoot);
        Assert.Equal(asks, asked.RecordDirectory);
        Assert.Equal(runDirectory, asked.Workspace.RunDirectory);
        Assert.Null(asked.Output);
        Assert.Equal("reporter", asked.Agent);

        var policy = new AgentPolicy(harness.FileSystem, harness.Platform, asked.Ground!);

        Assert.Null(policy.Read(Path.Combine(runDirectory, "flow.json")).Refusal());
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Read(Path.Combine(temp.Path, "file.txt"))).Rule);
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Run(["git", "status"])).Rule);
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Write(Path.Combine(runDirectory, "x.md"))).Rule);
        Assert.Equal("the reporter's reads", Assert.IsType<AgentPolicyAnswer.Denied>(policy.Web("https://example.com/x")).Rule);
    }

    /// <summary>The reporter's prompt is the facts and where the records are - and, for the person's question, the question.</summary>
    [Fact]
    public async Task TheReportersPromptIsTheFactsTheQuestionAndTheRecords()
    {
        var (_, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;

        _ = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);
        _ = await service.AskAsync(temp.Path, run, "what did the gate ask?", json: false, Token).WaitAsync(Bound, Token);

        var status = reporter.Asked[0];
        var asked = reporter.Asked[1];

        Assert.StartsWith($"The facts:\nrun '{run}' (workflow '{Workflow}'), as of ", status.PromptTemplate, StringComparison.Ordinal);
        Assert.Contains($"\nThe records:\n{Path.Combine(temp.Path, ".orchestrators", run)}\n", status.PromptTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("The question:", status.PromptTemplate, StringComparison.Ordinal);
        Assert.StartsWith("The question:\nwhat did the gate ask?\nThe facts:\n", asked.PromptTemplate, StringComparison.Ordinal);
        Assert.Contains($"\nThe records:\n{Path.Combine(temp.Path, ".orchestrators", run)}\n", asked.PromptTemplate, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the workflow names no reporter the status still answers with the facts, says no model worded them and why,
    /// and exits 0 - and the person's question is refused with 12, both keeping the call's own record.
    /// </summary>
    [Fact]
    public async Task WhereNoModelIsGiven_TheStatusStillAnswersWithTheFacts_AndTheQuestionIsRefused()
    {
        var (_, temp, reporter, service, run) = await PausedAsync(reporter: false);
        using var disposed = temp;

        var status = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, status.ExitCode);
        Assert.Equal("no model worded this: the workflow names no reporter, so no model words the run's facts", status.Details!.Single(line => line.StartsWith("no model worded this", StringComparison.Ordinal)));
        Assert.Contains("This answer cost nothing: no model worded it.", status.Details!, StringComparer.Ordinal);
        Assert.Empty(reporter.Asked);

        var asked = await service.AskAsync(temp.Path, run, "what is it doing?", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.ConfigInvalid, asked.ExitCode);
        Assert.StartsWith("ask-orchestrator was refused: the workflow names no reporter", asked.Message, StringComparison.Ordinal);
        Assert.True(Directory.EnumerateDirectories(Path.Combine(temp.Path, ".orchestrators", run, "asks")).Any(), "the refused call keeps its own record");
    }

    /// <summary>
    /// Where the model cannot answer - the key is not there, Claude Code is missing or too old, the model failed, the
    /// account is at its limit - the person's question is refused with the code of why, and the status still answers
    /// with the facts, saying why no model worded them, and exits 0.
    /// </summary>
    [Fact]
    public async Task WhereTheModelCannotAnswer_TheAskIsRefusedWithItsCode()
    {
        var (_, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;

        foreach (var (result, code) in new (AgentRunResult, int)[]
        {
            (new AgentRunResult(AgentRunStatus.NotStarted, AgentNotStartedBecause.KeyMissing, Error: "no key"), HarnessExit.Refused),
            (new AgentRunResult(AgentRunStatus.NotStarted, AgentNotStartedBecause.ProgramMissing, Error: "no claude"), HarnessExit.ToolMissing),
            (new AgentRunResult(AgentRunStatus.Failed, Error: "it failed"), HarnessExit.CommandFailed),
            (new AgentRunResult(AgentRunStatus.RateLimited, Error: "the limit") { Limit = new AgentAccountLimit(false) }, HarnessExit.Incomplete),
        })
        {
            reporter.Answer = _ => result;

            var asked = await service.AskAsync(temp.Path, run, "what is it doing?", json: false, Token).WaitAsync(Bound, Token);

            Assert.Equal(code, asked.ExitCode);
            Assert.StartsWith("ask-orchestrator was refused: no model worded an answer:", asked.Message, StringComparison.Ordinal);

            var status = await service.AskStatusAsync(temp.Path, run, json: false, Token).WaitAsync(Bound, Token);

            Assert.Equal(HarnessExit.Success, status.ExitCode);
            Assert.Contains(status.Details!, line => line.StartsWith("no model worded this:", StringComparison.Ordinal));
        }
    }

    /// <summary>The <c>--json</c> answer holds the facts and the answer apart, with the cost and the record.</summary>
    [Fact]
    public async Task TheJsonAnswerHoldsTheFactsAndTheAnswerApart()
    {
        var (_, temp, reporter, service, run) = await PausedAsync();
        using var disposed = temp;
        reporter.Answer = _ => new AgentRunResult(AgentRunStatus.Completed, FinalText: "All good.") { Model = "glm-5.3", Usage = new AgentUsageReport(new AgentUsage(1, 2, 3, 4, 5, 6), []), };

        var outcome = await service.AskStatusAsync(temp.Path, run, json: true, Token).WaitAsync(Bound, Token);
        var document = JsonNode.Parse(Assert.Single(outcome.Data))!.AsObject();

        Assert.Equal(HarnessExit.Success, (int)document["exitCode"]!);
        Assert.True((bool)document["worded"]!);
        Assert.Equal("All good.", (string)document["answer"]!);
        Assert.StartsWith($"run '{run}' (workflow '{Workflow}'), as of ", (string)document["facts"]!, StringComparison.Ordinal);
        Assert.Equal("glm-5.3", (string)document["cost"]!["model"]!);
        Assert.NotNull((string)document["record"]!);
    }

    /// <summary>
    /// An orchestrator made by hand, which no flow runs, has no reporter and fewer facts: its status prints what
    /// list-orchestrator has of it and says so, and the person's question is refused (12).
    /// </summary>
    [Fact]
    public async Task AnOrchestratorMadeByHand_HasNoReporterAndFewerFacts()
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig { Workflows = [Workflow] });
        await harness.CommitAllAsync(temp.Path, "initial", Token);
        temp.WriteFile($".harness-config/workflows/{Workflow}/{Workflow}.yml", "name: flow\nsteps:\n  - name: one\n    type: orient\n");
        harness.OrchestrationStore.WriteOrchestrator(
            OrchestratorLayout.Of(new DssHarness.Core.Repository.HarnessLayout(temp.Path, temp.Path), "o1"),
            new OrchestratorRecord { Kind = OrchestratorRecord.KindName, Name = "o1", Model = "glm-5.3", Parallel = 4, CreatedAt = DateTimeOffset.UtcNow });
        using var disposed = temp;

        var service = new FlowService(
            harness.ContextLoader,
            harness.Platform,
            harness.FileSystem,
            harness.OrchestrationStore,
            harness.Identity,
            new FlowSessions(harness.OrchestrationStore, harness.FileSystem, harness.Identity, harness.GitClient),
            new WorkflowValidationService(harness.ContextLoader, harness.FileSystem, harness.Platform, new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform), DssHarness.Core.Codebase.CodeGraphExtractors.OfThisBuild),
            new AskingRunner(),
            harness.OrchestrationLog,
            harness.ChildSessions,
            harness.PathBudget,
            harness.Output,
            harness.GitClient,
            harness.AgentService,
            new Agents(),
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform));

        var status = await service.AskStatusAsync(temp.Path, "o1", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, status.ExitCode);
        Assert.Equal("orchestrator 'o1' holds no run: it was made by hand, and these are list-orchestrator's facts of it", status.Message);
        Assert.Contains("model glm-5.3, at most 4 agents open", status.Details!, StringComparer.Ordinal);
        Assert.Equal("no agents", status.Details!.Single(line => line == "no agents"));

        var asked = await service.AskAsync(temp.Path, "o1", "what is it doing?", json: false, Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.ConfigInvalid, asked.ExitCode);
    }
}
