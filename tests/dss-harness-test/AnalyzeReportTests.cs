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

    /// <summary>
    /// A good analysis is kept with the report it read - the very bytes its hash names, beside it - and its spend is its
    /// own, never the run's; the analyst is run by the one runner of the records agents, tried again where it fails as
    /// the run's reasoning steps are, its answer held to the report by the gate's own check, and every record it left is
    /// named.
    /// </summary>
    [Fact]
    public async Task AGoodAnalysis_IsKeptWithTheReportItRead_ByItsHash_AndItsSpendIsItsOwn()
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

        var asked = Assert.Single(analyst.Asked);
        var directory = asked.RecordDirectory!;

        Assert.StartsWith(runDirectory, directory, StringComparison.Ordinal);
        Assert.Contains($"{Path.DirectorySeparatorChar}analysis{Path.DirectorySeparatorChar}", directory, StringComparison.Ordinal);
        Assert.Equal(runDirectory, asked.Ground!.ReporterRoot);
        Assert.Contains("\"steps\"", asked.Prompt, StringComparison.Ordinal);
        Assert.Equal((WorkflowDefinition.DefaultMaxRetries, (int?)null), (asked.Retries, asked.BackoffSeconds));
        Assert.NotNull(asked.Output!.Holds);

        // The analysis is kept beside the report it read, the bytes it was given, named by their hash - the same
        // document the verb answers, so the same bytes hash to the same name.
        var analysis = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "analysis.json")))!;
        var report = (await ReportOf(harness, temp, run)).ToJsonString(ReportJson.Options);
        var kept = File.ReadAllText(Path.Combine(directory, OrchestratorLayout.AnalysisReportFileName));

        Assert.Equal(report, kept);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(kept))), analysis["reportHash"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(directory, "analysis.md")));
        Assert.Equal(100 + 40, analysis["tokens"]!.GetValue<long>());
        Assert.EndsWith(
            string.Join(
                "\n",
                $"records: {directory}",
                $"analysis: {Path.Combine(directory, "analysis.json")}",
                $"analysisMarkdown: {Path.Combine(directory, "analysis.md")}",
                $"report: {Path.Combine(directory, "report.json")}"),
            outcome.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The analyst's answer is held to the report by the structured-output gate itself - the check the runner gives its
    /// output (<see cref="AgentOutput.Holds"/>), asked again for in the analyst's own conversation under the schema's
    /// count of repairs, never by a run asked anew: a citation the report does not hold is a problem at its path; the
    /// check reads only an answer the schema holds; the schema states every rule a text must keep; and an answer that
    /// names no pain point reads.
    /// </summary>
    [Fact]
    public async Task TheAnalystsAnswer_IsHeldToTheReportByTheGate_InItsOwnConversation()
    {
        var (_, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        analyst.Answer = step => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: Held);

        Assert.Equal(HarnessExit.Success, (await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token)).ExitCode);

        var gate = new StructuredOutputGate(Assert.Single(analyst.Asked).Output!, new Masking());
        IReadOnlyList<string> ProblemsOf(string output) => gate.Problems(Envelopes.Of(JsonNode.Parse(output)));

        Assert.Empty(ProblemsOf(Held));
        Assert.Equal(["$.output.painPoints[0].evidence[0]: 'steps', gate.tokens holds nothing in the report"], ProblemsOf(Invented));
        Assert.Equal(["$.output.painPoints[0].evidence[0].row: 3 is not a string"], ProblemsOf(Held.Replace("\"row\": \"gate\"", "\"row\": 3", StringComparison.Ordinal)));
        Assert.Equal(["$.output.summary: \" \" does not match its pattern"], ProblemsOf(Held.Replace("\"The run waits on its gate; nothing else has cost it anything.\"", "\" \"", StringComparison.Ordinal)));
        Assert.Empty(ProblemsOf("""{"summary":"Nothing in the report would have paid for a change.","painPoints":[],"noted":[]}"""));
    }

    /// <summary>
    /// An analysis whose answer the gate refused after its repairs was a paid run: the command is refused naming why,
    /// what it cost and every record it left - the report it read, and analysis.json with why it was refused and its
    /// spend - in text and in --json alike, and the analyst is asked once, its repairs its own conversation's.
    /// </summary>
    [Fact]
    public async Task ARefusedAnalysis_SaysWhatItCostAndNamesEveryRecordItLeft()
    {
        const string Why = "the answer did not read against its schema after 2 repairs: $.output.painPoints[0].evidence[0]: 'steps', gate.tokens holds nothing in the report";
        var (_, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        analyst.Answer = step => new AgentRunResult(AgentRunStatus.InvalidOutput, Error: Why);
        analyst.Says.Add(new UsageReported(new AgentUsage(100, 40, 0, 0, 0, 0), new AgentUsage(100, 40, 0, 0, 0, 0)));

        var refused = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var directory = Assert.Single(analyst.Asked).RecordDirectory!;

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.Equal($"analyze-orchestrator-report was refused: the analyst's run gave no analysis that holds: {Why}", refused.Message);
        Assert.StartsWith("This analysis cost ", refused.Details![0], StringComparison.Ordinal);
        Assert.Contains(", 140 tokens. It is not counted in the run's budget.", refused.Details[0], StringComparison.Ordinal);
        Assert.Equal([$"records: {directory}", $"analysis: {Path.Combine(directory, "analysis.json")}", $"report: {Path.Combine(directory, "report.json")}"], refused.Details.Skip(1));

        var analysis = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "analysis.json")))!;
        Assert.Equal((Why, 140L, (JsonNode?)null), (analysis["refused"]!.GetValue<string>(), analysis["tokens"]!.GetValue<long>(), analysis["analysis"]));

        var json = await service.AnalyzeReportAsync(temp.Path, run, json: true, cancellationToken: Token).WaitAsync(Bound, Token);
        var document = JsonNode.Parse(Assert.Single(json.Data))!;

        Assert.Equal(["exitCode", "summary", "analysis", "reportHash", "cost", "record"], ((JsonObject)document).Select(member => member.Key));
        Assert.Equal((HarnessExit.CommandFailed, (JsonNode?)null, 140L), (document["exitCode"]!.GetValue<int>(), document["analysis"], document["cost"]!["tokens"]!.GetValue<long>()));
        Assert.Equal(analyst.Asked[1].RecordDirectory, document["record"]!.GetValue<string>());
        Assert.Equal(2, analyst.Asked.Count);
    }
    /// <summary>
    /// Where tierConfig names no reporting tier no model is asked: the verb is refused naming the key, says that nothing
    /// was written, and its --json holds no cost and no record.
    /// </summary>
    [Fact]
    public async Task WhereTierConfigNamesNoReportingTier_TheVerbIsRefused_NamingTheKey()
    {
        var (_, temp, analyst, service, run) = await AnalyzableAsync(reporting: false);
        using var disposed = temp;
        analyst.Answer = step => new AgentRunResult(AgentRunStatus.Completed, StructuredOutput: Held);

        var outcome = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var json = await service.AnalyzeReportAsync(temp.Path, run, json: true, cancellationToken: Token).WaitAsync(Bound, Token);
        var document = JsonNode.Parse(Assert.Single(json.Data))!;

        Assert.True(HarnessExit.ConfigInvalid == outcome.ExitCode, $"{outcome.ExitCode}: {outcome.Message}");
        Assert.StartsWith("analyze-orchestrator-report was refused: no model analysed it: tierConfig names no 'reporting' tier", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(["records: none was written, for no analyst ran"], outcome.Details);
        Assert.Equal(((JsonNode?)null, string.Empty), (document["cost"], document["record"]!.GetValue<string>()));
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

    /// <summary>
    /// A citation is held by the report as its JSON holds it: a row by its id in every section - the second signal of a
    /// kind, a failed attempt, a dollars group inside the totals - a member by its path, a number as a number whatever
    /// form it is written in (an integer, a fraction, thousands grouped) and a text word for word; the steps a point
    /// names are rows of the report's steps, and every number its impact says is one its citations hold. Each one that
    /// is not is a problem at its path.
    /// </summary>
    [Fact]
    public void EachCitation_IsHeldByARowsId_AMembersPath_AndTheValueTheReportHolds()
    {
        var report = FlowRunReport.Of(FlowRunReportTests.Source());
        JsonNode Analysis(string where, string evidence, string impact)
            => JsonNode.Parse($$"""{"summary":"s","painPoints":[{"title":"t","severity":"high","where":{{where}},"evidence":[{{evidence}}],"impact":"{{impact}}","cause":"c","recommendation":"r"}],"noted":[]}""")!;
        static string Cited(string section, string row, string of, string said)
            => $$"""{"section":"{{section}}","row":"{{row}}","of":"{{of}}","said":"{{said}}"}""";

        Assert.Empty(FlowAnalysisEvidence.Problems(
            Analysis(
                """["b", "a"]""",
                string.Join(
                    ",",
                    Cited("steps", "b", "tokens", "100500"),
                    Cited("steps", "a", "usd", "0.0100"),
                    Cited("errors", "c@1", "exit", "20"),
                    Cited("signals", "slowestStep#2", "said", "a took 10.0 s of run time"),
                    Cited("totals", string.Empty, "tokens.input", "180000"),
                    Cited("theRun", string.Empty, "budget.usd", "0.03"),
                    Cited("totals", "model-y", "usd", "0.02"),
                    Cited("people", "q1", "waitedSeconds", "60"),
                    Cited("criticalPath", "b", "seconds", "30.0")),
                "b took 30 s and cost $0.02 of the 0.03 USD spent; a took 10.0 s and 100,500 tokens went to b."),
            report));

        Assert.Equal(
            [
                "painPoints[0].where[1]: the report's steps hold no row 'nope' - name a step by its row's id",
                "painPoints[0].evidence[0]: 'steps', b.tokens is 100500, not '999'",
                "painPoints[0].evidence[1]: the section 'steps' is a list: name its row by its id",
                "painPoints[0].evidence[2]: the section 'signals' holds no row 'slowestStep' - name a row by its id",
                "painPoints[0].evidence[3]: 'totals'.tokens is no value: cite a member inside it",
                "painPoints[0].evidence[4]: 'totals'.nothing is no member the report holds",
                "painPoints[0].evidence[5]: 'steps', c.tokens holds nothing in the report",
                "painPoints[0].evidence[6]: 'steps', b.state is \"Done\", not 'done'",
                "painPoints[0].evidence[7]: 'signals', classReopenedTwice#1.said is \"'c-rate' of agent 'item-1' was reopened 2 times\", not 'reopened'",
                "painPoints[0].impact: says 7, which none of its citations holds - cite where the report holds it, or leave it out",
            ],
            FlowAnalysisEvidence.Problems(
                Analysis(
                    """["b", "nope"]""",
                    string.Join(
                        ",",
                        Cited("steps", "b", "tokens", "999"),
                        Cited("steps", string.Empty, "tokens", "1"),
                        Cited("signals", "slowestStep", "said", "x"),
                        Cited("totals", string.Empty, "tokens", "1"),
                        Cited("totals", string.Empty, "nothing", "1"),
                        Cited("steps", "c", "tokens", "1"),
                        Cited("steps", "b", "state", "done"),
                        Cited("signals", "classReopenedTwice#1", "said", "reopened")),
                    "it cost 7 USD"),
                report));
    }

    /// <summary>
    /// Every number an impact says is held to its citations, one joined to its unit too: read for its numeric head, it is
    /// held as written or converted exactly from its unit to the report's - seconds for a time, ones for a count in
    /// thousands or millions - so a wait of 3600s, 1.5h or 12k tokens no citation holds is refused as a bare number is, a
    /// cited value written in another unit is held, and a word that starts as a number and reads as no one number - a
    /// date, a compound duration - is refused as such: an analyst writes no measurement past the check by gluing it to
    /// its unit.
    /// </summary>
    [Fact]
    public void ANumberJoinedToItsUnit_IsHeldToTheCitations_AsWrittenOrConvertedExactly()
    {
        var report = FlowRunReport.Of(FlowRunReportTests.Source());
        JsonNode Analysis(string evidence, string impact)
            => JsonNode.Parse($$"""{"summary":"s","painPoints":[{"title":"t","severity":"high","where":["b"],"evidence":[{{evidence}}],"impact":"{{impact}}","cause":"c","recommendation":"r"}],"noted":[]}""")!;
        static string Cited(string section, string row, string of, string said)
            => $$"""{"section":"{{section}}","row":"{{row}}","of":"{{of}}","said":"{{said}}"}""";
        var evidence = string.Join(
            ",",
            Cited("people", "q1", "waitedSeconds", "60"),
            Cited("criticalPath", "b", "seconds", "30.0"),
            Cited("totals", string.Empty, "tokens.input", "180000"),
            Cited("steps", "b", "tokens", "100500"));

        Assert.Empty(FlowAnalysisEvidence.Problems(
            Analysis(evidence, "q1 waited 60s, 1min, 1 minute and a 1-minute wait; b ran 0.5min of the path; 180k input tokens, 100.5k of them b's."),
            report));

        Assert.Equal(
            [
                "painPoints[0].impact: says 3600s, which none of its citations holds - cite where the report holds it, or leave it out",
                "painPoints[0].impact: says 1.5h, which none of its citations holds - cite where the report holds it, or leave it out",
                "painPoints[0].impact: says 12k, which none of its citations holds - cite where the report holds it, or leave it out",
                "painPoints[0].impact: says 2x, which none of its citations holds - cite where the report holds it, or leave it out",
                "painPoints[0].impact: says 61s, which none of its citations holds - cite where the report holds it, or leave it out",
                "painPoints[0].impact: says 2026-11-24, which reads as no one number - write each number alone or joined to its unit, as one of its citations' values",
                "painPoints[0].impact: says 1h30m, which reads as no one number - write each number alone or joined to its unit, as one of its citations' values",
            ],
            FlowAnalysisEvidence.Problems(
                Analysis(evidence, "the gate waited 3600s, 1.5h and 12k tokens, cost 2x the build and 61s more since 2026-11-24, 1h30m in all"),
                report));
    }

    /// <summary>
    /// The sections a citation may name are the schema's one list, and each is a section of the report that holds rows or
    /// members - so the schema refuses a section the report does not hold before the report is read.
    /// </summary>
    [Fact]
    public void TheSectionsACitationMayName_AreTheReportsOwn()
    {
        var schema = JsonNode.Parse(WorkflowTemplates.TextOf($"schemas/{WorkflowTemplates.AnalystSchema}.schema.json")!)!;
        var sections = schema["properties"]!["painPoints"]!["items"]!["properties"]!["evidence"]!["items"]!["properties"]!["section"]!["enum"]!.AsArray().Select(name => name!.GetValue<string>()).ToList();
        var report = FlowRunReport.Of(FlowRunReportTests.Source());

        Assert.Equal(["theRun", "totals", "steps", "criticalPath", "errors", "people", "convergence", "signals"], sections);
        Assert.All(sections, section => Assert.True(report[section] is JsonObject or JsonArray, section));
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

    /// <summary>
    /// A run paused on its gate question, with its analyst wired, in a repository whose agent sections are the ones init
    /// writes - its reporting tier the one init seeds, unless <paramref name="reporting"/> is false and it was taken out.
    /// </summary>
    private static async Task<(HarnessFactory Harness, TempDirectory Tree, Agents Analyst, FlowService Service, string Run)> AnalyzableAsync(TempDirectory? temp = null, bool reporting = true)
    {
        temp ??= new TempDirectory();
        var harness = new HarnessFactory();
        var agents = DssHarness.Core.Agents.AgentProviderTemplates.Read();
        var tiers = new Dictionary<string, List<string>>(agents.TierConfig, StringComparer.OrdinalIgnoreCase);

        // The agent sections init writes seed the reporting tier, as they seed every tier; a configuration that names none
        // is one a person took it out of - its models' lists of tiers with it, so the configuration reads.
        if (!reporting)
        {
            tiers.Remove("reporting");

            foreach (var model in agents.Providers.SelectMany(provider => provider.Models))
            {
                model.Tiers.Remove("reporting");
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
