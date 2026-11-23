using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The analysis of a run's report: one agent reads the deterministic report and brings its pain points, its answer
/// held to the schema the tool ships and to the report's own numbers - a citation the report does not hold asked
/// again for, twice at most, then refused - the analysis kept beside the report it read with the report's hash, its
/// spend the person's and never the run's, and the verb refused where tierConfig names no reporting tier.
/// </summary>
public sealed class AnalyzeReportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every bound a test of the command waits for.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private const string Workflow = "flow";

    /// <summary>
    /// One analysis that cites the report's own numbers: the gate's state and the run's status, both as the report
    /// holds them.
    /// </summary>
    private const string Held = """
        {
          "summary": "The run waits on its gate; nothing else has cost it anything.",
          "painPoints": [
            {
              "title": "The run waits on a person",
              "severity": "low",
              "where": ["gate"],
              "evidence": [
                { "section": "steps", "row": "gate", "of": "state", "said": "Paused" }
              ],
              "impact": "The whole wall time so far, which the report names.",
              "cause": "The gate step asked its question and the run paused for the answer.",
              "recommendation": "Answer the question with answer-orchestrator."
            }
          ],
          "noted": []
        }
        """;

    /// <summary>One analysis whose citation names a number the report does not hold.</summary>
    private const string Invented = """
        {
          "summary": "A step cost the run dearly.",
          "painPoints": [
            {
              "title": "A slow step",
              "severity": "high",
              "where": ["gate"],
              "evidence": [
                { "section": "steps", "row": "gate", "of": "tokens", "said": "999999" }
              ],
              "impact": "Many dollars.",
              "cause": "It waited.",
              "recommendation": "Raise defaults.report.top."
            }
          ],
          "noted": []
        }
        """;

    [Fact]
    public async Task AGoodAnalysis_IsKeptWithTheReportsHash_AndItsSpendIsItsOwn()
    {
        var (harness, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        var runDirectory = Path.Combine(temp.Path, ".orchestrators", run);
        var flowBefore = File.ReadAllBytes(Path.Combine(runDirectory, "flow.json"));
        analyst.Answer = step => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: Held);
        analyst.Says.Add(new UsageReported(new AgentUsage(100, 40, 0, 0, 0, 0), new AgentUsage(100, 40, 0, 0, 0, 0)));

        var outcome = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains("This analysis cost", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("It is not counted in the run's budget", outcome.Message, StringComparison.Ordinal);

        // The run's own state is untouched: its spend is not the analysis's.
        Assert.Equal(flowBefore, File.ReadAllBytes(Path.Combine(runDirectory, "flow.json")));

        var asked = analyst.Asked.Single();

        Assert.StartsWith(runDirectory, asked.RecordDirectory!, StringComparison.Ordinal);
        Assert.Contains($"{Path.DirectorySeparatorChar}analysis{Path.DirectorySeparatorChar}", asked.RecordDirectory!, StringComparison.Ordinal);
        Assert.Equal(runDirectory, asked.Ground!.ReporterRoot);
        Assert.Contains("\"steps\"", asked.Prompt, StringComparison.Ordinal);

        // The analysis is kept beside the report it read, named by the report's hash - the same document the verb
        // answers, so the same bytes hash to the same name.
        var analysis = JsonNode.Parse(File.ReadAllText(Path.Combine(asked.RecordDirectory!, "analysis.json")))!;
        var report = await ReportOf(harness, temp, run);

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(report.ToJsonString(ReportJson.Options)))), analysis["reportHash"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(asked.RecordDirectory!, "analysis.md")));
        Assert.Equal(100 + 40, analysis["tokens"]!.GetValue<long>());
    }

    [Fact]
    public async Task ACitationTheReportDoesNotHold_IsAskedAgainFor_ThenRefused()
    {
        var (_, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        var answers = 0;
        analyst.Answer = step => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: ++answers == 1 ? Invented : Held);

        var repaired = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.Success, repaired.ExitCode);
        Assert.Equal(2, analyst.Asked.Count);
        Assert.Contains("does not read against the schema", analyst.Asked[1].Prompt, StringComparison.Ordinal);

        // Asked again and still inventing, the analysis is refused with its problems - never answered over them.
        var second = new TempDirectory();
        var (harness2, temp2, analyst2, service2, run2) = await AnalyzableAsync(second);
        using var disposed2 = second;
        analyst2.Answer = step => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: Invented);

        var refused = await service2.AnalyzeReportAsync(temp2.Path, run2, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.Contains("no model analysed it", refused.Message, StringComparison.Ordinal);
        Assert.Contains("tokens", refused.Message, StringComparison.Ordinal);
        Assert.Equal(3, analyst2.Asked.Count);
    }

    [Fact]
    public async Task WhereTierConfigNamesNoReportingTier_TheVerbIsRefused_NamingTheKey()
    {
        var (_, temp, analyst, service, run) = await AnalyzableAsync(reporting: false);
        using var disposed = temp;
        analyst.Answer = step => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: Held);

        var outcome = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);

        Assert.True(HarnessExit.ConfigInvalid == outcome.ExitCode, $"{outcome.ExitCode}: {outcome.Message}");
        Assert.Contains("tierConfig names no 'reporting' tier", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(analyst.Asked);
    }

    [Fact]
    public async Task TheJsonAnswer_HoldsTheAnalysisApartFromItsCostAndRecord()
    {
        var (_, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        analyst.Answer = step => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: Held);

        var outcome = await service.AnalyzeReportAsync(temp.Path, run, json: true, cancellationToken: Token).WaitAsync(Bound, Token);
        var document = JsonNode.Parse(Assert.Single(outcome.Data!))!;

        Assert.Equal(["exitCode", "summary", "analysis", "reportHash", "cost", "record"], ((JsonObject)document).Select(member => member.Key));
        Assert.Equal("The run waits on its gate; nothing else has cost it anything.", document["analysis"]!["summary"]!.GetValue<string>());
        Assert.Contains($"{Path.DirectorySeparatorChar}analysis{Path.DirectorySeparatorChar}", document["record"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>The report the verb analyses, recomputed from the run's records as the verb reads them.</summary>
    private static async Task<JsonNode> ReportOf(HarnessFactory harness, TempDirectory temp, string run)
    {
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), run);
        var store = harness.OrchestrationStore;
        var flow = store.ReadFlow(layout)!;

        return FlowRunReport.Of(new FlowRunReportSource(
            layout,
            flow,
            store.ReadFlowQuestions(layout),
            store.ReadFlowUpdates(layout),
            FlowEventLog.Read(harness.FileSystem, layout.EventsFile).Events,
            [],
            store.ReadDefinition(layout)));
    }

    /// <summary>A run paused on its gate question, with its analyst wired and a reporting tier configured unless <paramref name="reporting"/> is false.</summary>
    private static async Task<(HarnessFactory Harness, TempDirectory Tree, Agents Analyst, FlowService Service, string Run)> AnalyzableAsync(TempDirectory? temp = null, bool reporting = true)
    {
        temp ??= new TempDirectory();
        var harness = new HarnessFactory();
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();
        var tiers = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase);

        if (reporting)
        {
            var named = agents.AgentTier is { } tier ? tiers.GetValueOrDefault(tier) : null;
            var chosen = named ?? tiers.Values.First();
            tiers["reporting"] = [.. chosen];

            // A tier's models must name the tier: the chosen ones gain it (their lists are theirs to gain), so the
            // config reads.
            foreach (var provider in agents.Providers)
            {
                foreach (var model in provider.Models.Where(model => chosen.Contains(model.Code, StringComparer.Ordinal)))
                {
                    model.Tiers.Add("reporting");
                }
            }
        }

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            Workflows = [Workflow],
            Defaults = new() { AgentTier = agents.AgentTier },
            Providers = [.. agents.Providers],
            TierConfig = tiers,
        });
        await harness.CommitAllAsync(temp.Path, "initial", Token);
        temp.WriteFile(
            $".harness-config/workflows/{Workflow}/{Workflow}.yml",
            "name: flow\n"
            + "steps:\n"
            + "  - name: gate\n"
            + "    type: pause\n"
            + "    question: what now?\n"
            + "    options: [stay, go]\n");
        OrchestrationKit.Write(temp.Path, Path.Combine(".harness-config", "workflows", ".env", "a.env"), "ZAI_API_KEY=sk-test-one\n");
        harness.FilePermissions.ProtectSecret(Path.Combine(temp.Path, ".harness-config", "workflows", ".env", "a.env"));

        var analyst = new Agents();
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
            analyst,
            new DssHarness.Core.Secrets.AgentKeyStore(harness.FileSystem, harness.FilePermissions, harness.Platform),
            new TestIsolation());

        var started = await service.RunAsync(temp.Path, Workflow, run: null, inputs: null, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        Assert.Equal(FlowExit.Paused, started.ExitCode);

        return (harness, temp, analyst, service, "r1");
    }

    /// <summary>The agent runner as a fake, keeping every request it was asked and answering as a test says.</summary>
    private sealed class Agents : IAgentRunner
    {
        /// <summary>Every request the runner built, in order.</summary>
        public List<AgentStepRequest> Asked { get; } = [];

        /// <summary>How the run answers; a completed run with no output unless a test changes it.</summary>
        public Func<AgentStepRequest, AgentRunResult> Answer { get; set; } = _ => new AgentRunResult(AgentRunStatus.Completed);

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

    /// <summary>A runner that asks on the pause step and passes every other, so a run pauses for its analysis.</summary>
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
}
