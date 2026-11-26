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

        // The prompt is filled as a template, so the placeholder it names is written there with its braces doubled.
        Assert.Contains("each figure a placeholder naming one of its citations by its place in the evidence, `{1}` the first,", Prose.Words(asked.Prompt), StringComparison.Ordinal);
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
    /// An analyst's client that throws after paid turns is refused with why, and what it spent is still said and kept:
    /// the run's spend and its model outlive the throw, so the cost names them, analysis.json is written with them, and
    /// the refusal names the directory its records are in - in the text and in the --json document alike - where the
    /// throw lost the spend with the result it kept the engine from reading.
    /// </summary>
    [Fact]
    public async Task AClientThatThrowsAfterPaidTurns_StillSaysAndKeepsWhatItSpent()
    {
        var (harness, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        var turn = new AgentUsage(100, 40, 0, 0, 0, 0);
        analyst.Says.Add(new UsageReported(turn, turn));
        analyst.Throws = true;

        var text = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var json = await service.AnalyzeReportAsync(temp.Path, run, json: true, cancellationToken: Token).WaitAsync(Bound, Token);

        var served = analyst.Asked[0].Model!;
        var priced = AgentProviders.Resolve(harness.ConfigStore.Load(HarnessFactory.ConfigPath(temp.Path)))!.Providers!.Of(served)
            ?? throw new InvalidOperationException($"'{served}' is no model the configuration lists");
        var document = JsonNode.Parse(Assert.Single(json.Data))!.AsObject();

        Assert.Equal(HarnessExit.InternalError, text.ExitCode);
        Assert.StartsWith("analyze-orchestrator-report was refused: the analyst's run gave no analysis that holds: Unexpected InvalidOperationException: ", text.Message, StringComparison.Ordinal);

        var said = string.Join("\n", text.Details!);

        // The cost names the model that served the turns, as an ask's does.
        Assert.Equal($"This analysis cost {new FlowDollars(AgentPrices.CostOf(priced.Provider, priced.Model, turn)!.Value).Said(format: "0.####")} ({served}, 140 tokens). It is not counted in the run's budget.", text.Details![0]);
        Assert.Contains($"records: {analyst.Asked[0].RecordDirectory!}", said, StringComparison.Ordinal);

        Assert.Equal(HarnessExit.InternalError, (int)document["exitCode"]!);
        Assert.Null((JsonNode?)document["analysis"]!);
        Assert.Equal(
            (served, AgentPrices.CostOf(priced.Provider, priced.Model, turn), turn.AllTokens),
            ((string)document["cost"]!["model"]!, (double?)document["cost"]!["usd"], (long)document["cost"]!["tokens"]!));
        Assert.Equal(analyst.Asked[1].RecordDirectory!, (string)document["record"]!);

        var kept = JsonNode.Parse(File.ReadAllText(Path.Combine(analyst.Asked[0].RecordDirectory!, "analysis.json")))!;

        Assert.Equal((AgentPrices.CostOf(priced.Provider, priced.Model, turn), turn.AllTokens), ((double?)kept["usd"]!, (long)kept["tokens"]!));
    }

    /// <summary>
    /// An analysis a person interrupts after paid turns is answered with what it spent, as every other run that gave no
    /// analysis is: the command ends interrupted, says what it cost and names every record it left - analysis.json written
    /// with the spend, the model that served it and why no analysis holds - in the text and in the --json document alike,
    /// where the interrupt would lose the spend with the run it stopped.
    /// </summary>
    [Fact]
    public async Task AnAnalysisInterruptedAfterPaidTurns_SaysAndKeepsWhatItSpent()
    {
        var (harness, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        var turn = new AgentUsage(100, 40, 0, 0, 0, 0);
        analyst.Says.Add(new UsageReported(turn, turn));
        analyst.Interrupted = true;

        var text = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var json = await service.AnalyzeReportAsync(temp.Path, run, json: true, cancellationToken: Token).WaitAsync(Bound, Token);

        var served = analyst.Asked[0].Model!;
        var priced = AgentProviders.Resolve(harness.ConfigStore.Load(HarnessFactory.ConfigPath(temp.Path)))!.Providers!.Of(served)
            ?? throw new InvalidOperationException($"'{served}' is no model the configuration lists");
        var usd = AgentPrices.CostOf(priced.Provider, priced.Model, turn);
        var directory = analyst.Asked[0].RecordDirectory!;
        var document = JsonNode.Parse(Assert.Single(json.Data))!.AsObject();
        var kept = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "analysis.json")))!;

        Assert.Equal(HarnessExit.Cancelled, text.ExitCode);
        Assert.Equal("analyze-orchestrator-report was refused: the analyst's run gave no analysis that holds: Interrupted before completion.", text.Message);
        Assert.Equal(
            [
                $"This analysis cost {new FlowDollars(usd!.Value).Said(format: "0.####")} ({served}, 140 tokens). It is not counted in the run's budget.",
                $"records: {directory}",
                $"analysis: {Path.Combine(directory, "analysis.json")}",
                $"report: {Path.Combine(directory, "report.json")}",
            ],
            text.Details!);
        Assert.Equal(
            (served, usd, 140L, "Interrupted before completion."),
            ((string)kept["model"]!, (double?)kept["usd"], (long)kept["tokens"]!, (string)kept["refused"]!));

        Assert.Equal(HarnessExit.Cancelled, (int)document["exitCode"]!);
        Assert.Null((JsonNode?)document["analysis"]);
        Assert.Equal((served, usd, 140L), ((string)document["cost"]!["model"]!, (double?)document["cost"]!["usd"], (long)document["cost"]!["tokens"]!));
        Assert.Equal(analyst.Asked[1].RecordDirectory!, (string)document["record"]!);
    }

    /// <summary>
    /// A run that fell back to another model before its client threw is said and kept at the model that served it - the
    /// one its spend's own account priced every turn at - never the model it was resolved to: analysis.json and the --json
    /// document name the model that took over, and what they say it cost is that model's price of the turn.
    /// </summary>
    [Fact]
    public async Task ARunThatFellBackAndThenThrew_IsKeptAtTheModelThatServedIt()
    {
        var (harness, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        var config = harness.ConfigStore.Load(HarnessFactory.ConfigPath(temp.Path));
        var turn = new AgentUsage(100, 40, 0, 0, 0, 0);
        analyst.SaysFirst = step =>
        {
            var (provider, model) = config.Providers.SelectMany(provider => provider.Models.Select(model => (provider, model))).First(entry => entry.model.Code != step.Model && entry.model.PriceInputMiss is not null);
            return [new FallbackStarted(step.Model!, "first", "first", model.Code, provider.Name, provider.Client, "key refused", AgentFallback.SentAgain, 1, ThrewAway: false, CreditAsked: false)];
        };
        analyst.Says.Add(new UsageReported(turn, turn));
        analyst.Throws = true;

        _ = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var json = await service.AnalyzeReportAsync(temp.Path, run, json: true, cancellationToken: Token).WaitAsync(Bound, Token);

        var providers = AgentProviders.Resolve(config)!.Providers!;
        var resolved = analyst.Asked[0].Model!;
        var took = ((FallbackStarted)analyst.SaysFirst(analyst.Asked[0])[0]).ToModel;
        var served = providers.Of(took) ?? throw new InvalidOperationException($"'{took}' is no model the configuration lists");
        var first = providers.Of(resolved) ?? throw new InvalidOperationException($"'{resolved}' is no model the configuration lists");
        var usd = AgentPrices.CostOf(served.Provider, served.Model, turn);
        var kept = JsonNode.Parse(File.ReadAllText(Path.Combine(analyst.Asked[0].RecordDirectory!, "analysis.json")))!;
        var document = JsonNode.Parse(Assert.Single(json.Data))!.AsObject();

        // The two models' prices differ, so the cost itself says which model's turn it is.
        Assert.NotEqual(AgentPrices.CostOf(first.Provider, first.Model, turn), usd);
        Assert.Equal((took, usd), ((string)kept["model"]!, (double?)kept["usd"]));
        Assert.Equal((took, usd), ((string)document["cost"]!["model"]!, (double?)document["cost"]!["usd"]));
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
        analyst.Answer = step => new AgentRunResult(AgentRunStatus.InvalidOutput, Error: Why) { Model = step.Model };
        analyst.Says.Add(new UsageReported(new AgentUsage(100, 40, 0, 0, 0, 0), new AgentUsage(100, 40, 0, 0, 0, 0)));

        var refused = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var directory = Assert.Single(analyst.Asked).RecordDirectory!;

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
        Assert.Equal($"analyze-orchestrator-report was refused: the analyst's run gave no analysis that holds: {Why}", refused.Message);
        Assert.StartsWith("This analysis cost ", refused.Details![0], StringComparison.Ordinal);
        Assert.EndsWith($" ({analyst.Asked[0].Model}, 140 tokens). It is not counted in the run's budget.", refused.Details[0], StringComparison.Ordinal);
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
    /// names are rows of the report's steps, and its impact writes its figures as placeholders of its citations. Each
    /// one that is not is a problem at its path.
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
                "b took {9} and cost {7} of the {6} spent; a cost {2}, and {1} went to b."),
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
                $"painPoints[0].impact: writes '7', {OfItsOwn}",
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
    /// Each placeholder of an impact is written by the tool as the value of the citation it names, in the unit the report
    /// holds that member in and as the report's own words say a value of that unit: a wait and a step's run - a text that
    /// is one number whole - in seconds, the budget's reasoning in minutes, a cost in dollars, a cache share in percent,
    /// tokens as tokens and any other count as the report writes it. The analysis keeps the written impact and, beside it,
    /// the template it was written from, in every form.
    /// </summary>
    [Fact]
    public void EachPlaceholder_IsWrittenAsItsCitedValue_InTheUnitTheReportHoldsItIn()
    {
        const string Template = "q1 waited {1} and b {2} on the path; the run reasoned {3} and spent {4} on model-y, {5} of its input read from the cache; b used {6} over {7}, a ran {8} and c-rate had {9}.";
        var source = FlowRunReportTests.Source();
        var report = FlowRunReport.Of(source with
        {
            Flow = source.Flow with
            {
                Spent = source.Flow.Spent with { Minutes = 90 },
                Steps = [.. source.Flow.Steps.Select(step => step.Instance == "a" ? step with { Measure = step.Measure! with { Ran = 10 } } : step)],
            },
        });
        var answer = AnalysisOf(
            string.Join(
                ",",
                Cited("people", "q1", "waitedSeconds", "60"),
                Cited("criticalPath", "b", "seconds", "30.0"),
                Cited("theRun", string.Empty, "budget.minutes", "90"),
                Cited("totals", "model-y", "usd", "0.02"),
                Cited("totals", string.Empty, "cacheSharePercent", "28.0"),
                Cited("steps", "b", "tokens", "100500"),
                Cited("steps", "b", "tries", "2"),
                Cited("steps", "a", "ran", "10.0"),
                Cited("convergence", "classesReopened:item-1:c-rate", "reopens", "2")),
            Template);

        var rendered = FlowAnalysisEvidence.Rendered(answer, report, out var problems)!;
        var analysis = System.Text.Json.JsonSerializer.Deserialize<FlowAnalysis>(rendered, DssHarness.Core.FileSystem.JsonStateFile.Options)!;
        var point = Assert.Single(analysis.PainPoints);
        const string Written = "q1 waited 60.0 s and b 30.0 s on the path; the run reasoned 90.0 minutes and spent 0.0200 USD on model-y, 28.0% of its input read from the cache; b used 100500 tokens over 2 tries, a ran 10.0 s and c-rate had 2 reopens.";

        Assert.Empty(problems);
        Assert.Equal((Written, Template), (point.Impact, point.ImpactTemplate));
        Assert.Equal(
            ["title", "severity", "where", "evidence", "impact", "impactTemplate", "cause", "recommendation"],
            ((JsonObject)analysis.Document()["painPoints"]![0]!).Select(member => member.Key));
        Assert.Equal((Written, Template), (analysis.Document()["painPoints"]![0]!["impact"]!.GetValue<string>(), analysis.Document()["painPoints"]![0]!["impactTemplate"]!.GetValue<string>()));
        Assert.Contains($"\n  impact: {Written}\n", analysis.Text(), StringComparison.Ordinal);
        Assert.Contains($"\n- impact: {Written}\n", analysis.Markdown(), StringComparison.Ordinal);
    }

    /// <summary>
    /// An impact states no number of its own: a digit anywhere outside a placeholder and an id the report holds is
    /// refused, once for each word it stands in - a bare one, one joined to its unit, one after an id the report holds,
    /// one in brackets or after a currency's sign, any numeral Unicode names, and one glued to a placeholder - while the
    /// report's own ids and the placeholders say nothing; so no measurement passes by how it is written.
    /// </summary>
    [Fact]
    public void ANumberOfTheImpactsOwn_IsRefused_HoweverItIsWritten()
    {
        var report = FlowRunReport.Of(FlowRunReportTests.Source());
        var evidence = string.Join(
            ",",
            Cited("people", "q1", "waitedSeconds", "60"),
            Cited("errors", "c@1", "exit", "20"));

        Assert.Empty(FlowAnalysisEvidence.Problems(AnalysisOf(evidence, "q1 waited {1}; c@1 exited {2}, as c@1.exit says - b@1 and slowestStep#2 are the report's rows."), report));
        Assert.Equal(
            [
                $"painPoints[0].impact: writes '60', {OfItsOwn}",
                $"painPoints[0].impact: writes '3600s', {OfItsOwn}",
                $"painPoints[0].impact: writes 'q1:3600s;', {OfItsOwn}",
                $"painPoints[0].impact: writes '(20)', {OfItsOwn}",
                $"painPoints[0].impact: writes '$0.02,', {OfItsOwn}",
                $"painPoints[0].impact: writes '½', {OfItsOwn}",
                $"painPoints[0].impact: {{1}} touches '0' after it - {Touches}",
                $"painPoints[0].impact: writes '{{1}}0', {OfItsOwn}",
                $"painPoints[0].impact: writes 'b@2', {OfItsOwn}",
            ],
            FlowAnalysisEvidence.Problems(AnalysisOf(evidence, "q1 waited 60 s, the gate 3600s and q1:3600s; c@1 exited (20) for $0.02, ½ of it {1}0 in all, as b@2 did."), report));
    }

    /// <summary>
    /// A word the report holds as an id is written whole, its digits no number of the impact's own: a step whose instance
    /// holds digits - a build2, a windows-2022 - is named as the report names it, while a word the report holds as no id
    /// - a step it never had - is refused, every digit of it read.
    /// </summary>
    [Fact]
    public void AReportsIdWithDigits_IsWrittenWhole_AndAWordItHoldsAsNoIdIsRefused()
    {
        var source = FlowRunReportTests.Source();
        var report = FlowRunReport.Of(source with
        {
            Flow = source.Flow with
            {
                Steps = [.. source.Flow.Steps.Select(step => step.Instance switch { "a" => step with { Instance = "build2" }, "c" => step with { Instance = "windows-2022" }, _ => step })],
            },
        });
        var evidence = Cited("criticalPath", "b", "seconds", "30.0");

        Assert.Equal(["build2", "b", "windows-2022"], report["steps"]!.AsArray().Select(row => row!["id"]!.GetValue<string>()));
        Assert.Empty(FlowAnalysisEvidence.Problems(AnalysisOf(evidence, "build2 and windows-2022 waited on b for {1}; windows-2022's attempt windows-2022@1 failed."), report));
        Assert.Equal(
            [$"painPoints[0].impact: writes 'build3', {OfItsOwn}", $"painPoints[0].impact: writes 'windows-2023', {OfItsOwn}", $"painPoints[0].impact: writes 'build2-fast', {OfItsOwn}"],
            FlowAnalysisEvidence.Problems(AnalysisOf(evidence, "build3 and windows-2023 waited on b for {1}, as build2-fast did."), report));
    }

    /// <summary>
    /// A word is an id only where the report holds it as one - a row's id, a name the id is made of as the row's own
    /// members hold it (a hotspot's item and path), a section's or a member's name - or a citation writes it as its path,
    /// and an id is written whole, its <c>: + @ # /</c> and brackets included, its digits no number of the impact's own: a
    /// hotspot's path whose file name starts with a date, a forEach step's instance, its failed attempt and that attempt's
    /// cited member. The same characters in a word the report holds as no id are refused for their digits: an attempt or a
    /// signal the report never had, and the hotspot's path where the report holds no such hotspot - an analysis cannot pass
    /// a number off as part of a name the report does not hold.
    /// </summary>
    [Fact]
    public void AWordIsAnIdOnlyWhereTheReportOrACitationHoldsIt_AndEveryOtherDigitIsRefused()
    {
        const string Hotspot = "hotspots:rounds#2.items[0]:c-rate:db/migrate/20240101_users.rb";
        var source = FlowRunReportTests.Source();
        var report = FlowRunReport.Of(source with
        {
            Flow = source.Flow with
            {
                Steps = [.. source.Flow.Steps.Select(step => step.Instance == "c"
                    ? step with { Instance = "rounds#2.items[0].fix+2", State = "Done", Output = JsonNode.Parse("""{"hotspots":[{"class":"c-rate","path":"db/migrate/20240101_users.rb","touches":6}]}""") }
                    : step)],
            },
        });
        var evidence = string.Join(
            ",",
            Cited("criticalPath", "b", "seconds", "30.0"),
            Cited("errors", "rounds#2.items[0].fix+2@1", "exit", "20"),
            Cited("convergence", Hotspot, "touches", "6"));

        static IEnumerable<string> Ids(JsonNode? rows)
            => rows!.AsArray().Select(row => row!["id"]!.GetValue<string>());

        // The rows the impact names are the report's own: the step's instance, its failed attempt, its hotspot, a signal.
        Assert.Equal(["a", "b", "rounds#2.items[0].fix+2"], Ids(report["steps"]));
        Assert.Equal(["b@1", "rounds#2.items[0].fix+2@1"], Ids(report["errors"]));
        Assert.Equal([Hotspot], Ids(report["convergence"]!["hotspots"]));
        Assert.Contains("slowestStep#2", Ids(report["signals"]));
        Assert.Empty(FlowAnalysisEvidence.Problems(
            AnalysisOf(evidence, $"the hotspot `db/migrate/20240101_users.rb` of item rounds#2.items[0] ({Hotspot}.touches says {{3}}) cost the gate {{1}}; rounds#2.items[0].fix+2's attempt rounds#2.items[0].fix+2@1 exited {{2}}, as rounds#2.items[0].fix+2@1.exit says - b@1, slowestStep#2 and classesReopened:item-1:c-rate are the report's rows."),
            report));
        Assert.Equal(
            [$"painPoints[0].impact: writes 'b@2,', {OfItsOwn}", $"painPoints[0].impact: writes 'slowestStep#9', {OfItsOwn}", $"painPoints[0].impact: writes 'rounds#2.items[0].fix+2@2.', {OfItsOwn}"],
            FlowAnalysisEvidence.Problems(AnalysisOf(evidence, "the gate waited on b@2, slowestStep#9 and rounds#2.items[0].fix+2@2."), report));
        Assert.Equal(
            [$"painPoints[0].impact: writes '`db/migrate/20240101_users.rb`', {OfItsOwn}"],
            FlowAnalysisEvidence.Problems(
                AnalysisOf(Cited("criticalPath", "b", "seconds", "30.0"), "the hotspot `db/migrate/20240101_users.rb` cost the gate {1}."),
                FlowRunReport.Of(source)));
    }

    /// <summary>
    /// A placeholder names one of its pain point's citations by its place in the evidence, and only one that holds a number
    /// the report measures: <c>{3}</c> past two citations and <c>{0}</c> name none, and one that names a text of words is
    /// refused; one whose citation the report does not hold says nothing more than that citation's own problem.
    /// </summary>
    [Fact]
    public void APlaceholderPastItsEvidence_OrNamingNoNumber_IsRefused()
    {
        var report = FlowRunReport.Of(FlowRunReportTests.Source());

        Assert.Equal(
            [
                "painPoints[0].impact: {2} names 'steps', b.model, which holds no number the report measures - a placeholder is written as a cited number in its unit; cite a number of the report's, or say it in words",
                "painPoints[0].impact: {3} names no citation - its evidence holds 2, and {n} names the n-th of them, from {1}",
                "painPoints[0].impact: {0} names no citation - its evidence holds 2, and {n} names the n-th of them, from {1}",
            ],
            FlowAnalysisEvidence.Problems(
                AnalysisOf(string.Join(",", Cited("criticalPath", "b", "seconds", "30.0"), Cited("steps", "b", "model", "model-y")), "b waited {1} on {2}, {3} in all and {0} more."),
                report));
        Assert.Equal(
            ["painPoints[0].evidence[0]: 'steps', b.tokens is 100500, not '999'"],
            FlowAnalysisEvidence.Problems(AnalysisOf(Cited("steps", "b", "tokens", "999"), "b used {1}."), report));
    }

    /// <summary>
    /// Every count the report holds is said with what it counts, one and more than one, and every number that is no
    /// amount after its label - a failed attempt's exception and first error after the path of the member that holds it -
    /// so no figure of an impact is ever written as a bare number a word beside it could make a cost or a time. Pinned for
    /// every such member of the report's one table of units.
    /// </summary>
    [Fact]
    public void EveryCountTheReportHolds_IsSaidWithWhatItCounts()
    {
        static string Said(string path, string member)
        {
            var unit = FlowRunReport.Units[path];
            return $"{path}: {FlowRunReport.SaidIn(unit, JsonValue.Create(1), member)} / {FlowRunReport.SaidIn(unit, JsonValue.Create(2), member)}";
        }

        Assert.Equal(
            [
                "theRun.sessions: 1 session / 2 sessions",
                "totals.tokens.searches: 1 search / 2 searches",
                "totals.agents: 1 agent / 2 agents",
                "totals.stepsRun: 1 step run / 2 steps run",
                "totals.stepsSkipped: 1 step skipped / 2 steps skipped",
                "totals.stepsFailed: 1 step failed / 2 steps failed",
                "totals.retries: 1 retry / 2 retries",
                "totals.fallbacks: 1 fallback / 2 fallbacks",
                "totals.graph.steps: 1 step given a graph / 2 steps given a graph",
                "totals.graph.calls: 1 graph call / 2 graph calls",
                "totals.graph.bodies: 1 body served / 2 bodies served",
                "totals.graph.textHits: 1 text hit / 2 text hits",
                "totals.graph.edits.made: 1 graph edit made / 2 graph edits made",
                "totals.graph.edits.refused: 1 graph edit refused / 2 graph edits refused",
                "totals.graph.wholeFileReads.count: 1 whole-file read / 2 whole-file reads",
                "totals.graph.outlinedReads.count: 1 outlined read / 2 outlined reads",
                "totals.graph.escapes.count: 1 graph escape / 2 graph escapes",
                "totals.graph.escapes.lowConfidence: 1 low-confidence escape / 2 low-confidence escapes",
                "totals.graph.escapes.stated: 1 stated escape / 2 stated escapes",
                "steps[].tries: 1 try / 2 tries",
                "steps[].exit: exit 1 / exit 2",
                "steps[].graph.pack.seeds: 1 pack seed / 2 pack seeds",
                "steps[].graph.pack.nodes: 1 pack node / 2 pack nodes",
                "steps[].graph.pack.used: 1 pack node used / 2 pack nodes used",
                "steps[].graph.pulled.map.pages: 1 map page pulled / 2 map pages pulled",
                "steps[].graph.pulled.pack.pages: 1 pack page pulled / 2 pack pages pulled",
                "steps[].graph.calls: 1 graph call / 2 graph calls",
                "steps[].graph.bodies: 1 body served / 2 bodies served",
                "steps[].graph.textHits: 1 text hit / 2 text hits",
                "steps[].graph.edits.made: 1 graph edit made / 2 graph edits made",
                "steps[].graph.edits.refused: 1 graph edit refused / 2 graph edits refused",
                "steps[].graph.wholeFileReads.count: 1 whole-file read / 2 whole-file reads",
                "steps[].graph.outlinedReads.count: 1 outlined read / 2 outlined reads",
                "steps[].graph.escapes.count: 1 graph escape / 2 graph escapes",
                "steps[].graph.escapes.lowConfidence: 1 low-confidence escape / 2 low-confidence escapes",
                "steps[].graph.escapes.stated: 1 stated escape / 2 stated escapes",
                "errors[].attempt: attempt 1 / attempt 2",
                "errors[].exit: exit 1 / exit 2",
                "errors[].exception: exception stackCut 1 / exception stackCut 2",
                "errors[].firstError: firstError line 1 / firstError line 2",
                "convergence.items[].reviewRounds: 1 review round / 2 review rounds",
                "convergence.items[].homeAudits: 1 home audit / 2 home audits",
                "convergence.items[].classes[].sitesConsolidated: 1 site consolidated / 2 sites consolidated",
                "convergence.items[].classes[].sitesDeleted: 1 site deleted / 2 sites deleted",
                "convergence.items[].classes[].cellsPinned: 1 cell pinned / 2 cells pinned",
                "convergence.items[].classes[].neighboursMeasured: 1 neighbour measured / 2 neighbours measured",
                "convergence.items[].classes[].neighboursUnmeasured: 1 neighbour unmeasured / 2 neighbours unmeasured",
                "convergence.items[].classes[].sendBacks: 1 send-back / 2 send-backs",
                "convergence.classesReopened[].reopens: 1 reopen / 2 reopens",
                "convergence.hotspots[].touches: 1 touch / 2 touches",
            ],
            FlowRunReport.Units
                .Where(entry => entry.Value.Kind is FlowUnitKind.Counted or FlowUnitKind.Labelled)
                .Select(entry => Said(entry.Key, entry.Key switch { "errors[].exception" => "exception.stackCut", "errors[].firstError" => "firstError.line", _ => "unread" })));
    }

    /// <summary>
    /// A placeholder stands apart from what would make its figure another measure: a letter, a number, a currency's sign,
    /// a percent or another placeholder touching it on either side, or a sign before it, is refused - a cited count of
    /// tries written after a dollar's sign is no cost of two dollars, a wait glued to an h no hours, and two figures
    /// written together no one figure - while brackets, Markdown's marks and punctuation leave it its own, and a cited
    /// count is written with what it counts.
    /// </summary>
    [Fact]
    public void APlaceholderTouchingALetterANumberASignOrPercent_IsRefused()
    {
        var report = FlowRunReport.Of(FlowRunReportTests.Source());
        var evidence = string.Join(",", Cited("steps", "b", "tries", "2"), Cited("criticalPath", "b", "seconds", "30.0"));

        Assert.Equal(
            "b ran 2 tries (30.0 s), **2 tries**, `30.0 s`; 2 tries - 30.0 s.",
            FlowAnalysisEvidence.Rendered(AnalysisOf(evidence, "b ran {1} ({2}), **{1}**, `{2}`; {1} - {2}."), report, out var problems)!["painPoints"]![0]!["impact"]!.GetValue<string>());
        Assert.Empty(problems);
        Assert.Equal(
            [
                $"painPoints[0].impact: {{1}} touches '$' before it - {Touches}",
                $"painPoints[0].impact: {{2}} touches 'h' after it - {Touches}",
                $"painPoints[0].impact: {{1}} touches 'k' after it - {Touches}",
                $"painPoints[0].impact: {{1}} touches '%' after it - {Touches}",
                $"painPoints[0].impact: {{2}} touches '$' before it - {Touches}",
                $"painPoints[0].impact: {{1}} touches '€' before it - {Touches}",
                $"painPoints[0].impact: {{2}} touches '-' before it - {Touches}",
                $"painPoints[0].impact: {{1}} touches 'z' before it and 'z' after it - {Touches}",
                $"painPoints[0].impact: {{2}} touches '{{1}}' after it - {Touches}",
                $"painPoints[0].impact: {{1}} touches '{{2}}' before it - {Touches}",
            ],
            FlowAnalysisEvidence.Problems(AnalysisOf(evidence, "b's retries cost ${1}, took {2}h, {1}k, {1}%, US${2}, €{1}, -{2}, z{1}z and {2}{1}."), report));
    }

    /// <summary>
    /// The analysis as a whole states no number of its own: the summary, each pain point's title and cause, and each thing
    /// noted and why are refused for a numeral outside an id the report or a citation holds, each at its member, for its
    /// figures are its impacts' and its citations'; a recommendation states the values it proposes and is held as written.
    /// </summary>
    [Fact]
    public void EveryTextOfTheAnalysisButItsRecommendation_HoldsNoNumberOfItsOwn()
    {
        var report = FlowRunReport.Of(FlowRunReportTests.Source());
        JsonNode Analysis(string summary, string title, string cause, string what, string why)
            => new JsonObject
            {
                ["summary"] = summary,
                ["painPoints"] = new JsonArray(new JsonObject
                {
                    ["title"] = title,
                    ["severity"] = "high",
                    ["where"] = new JsonArray("b"),
                    ["evidence"] = JsonNode.Parse($"[{Cited("criticalPath", "b", "seconds", "30.0")}]"),
                    ["impact"] = "b waited {1}.",
                    ["cause"] = cause,
                    ["recommendation"] = "Set maxRetries: 2 and defaults.report.top: 5 in flow.yml.",
                }),
                ["noted"] = new JsonArray(new JsonObject { ["what"] = what, ["why"] = why }),
            };

        Assert.Empty(FlowAnalysisEvidence.Problems(
            Analysis("b's wait on the queue cost the run most, as q1's did.", "b waits on the machine's queue", "b@1 failed and c@1 failed after it, as b.seconds says.", "q1 waited on a person", "q2 is still open"),
            report));
        Assert.Equal(
            [
                $"summary: writes '3600', {Free}",
                $"painPoints[0].title: writes '2', {Free}",
                $"painPoints[0].cause: writes '20', {Free}",
                $"noted[0].what: writes '{{1}}', {Free}",
                $"noted[0].why: writes '2nd', {Free}",
            ],
            FlowAnalysisEvidence.Problems(
                Analysis("The build alone wasted 3600 s on the queue, as q1 did.", "b retried 2 times", "b@1 failed and c@1 exited 20", "q1 waited {1}", "the 2nd answer is not in the records"),
                report));
    }

    /// <summary>
    /// Every word the report holds - in its texts too - that starts with a letter and holds a digit is an id every checked
    /// member may repeat: a failed attempt's first error's code and file, a leg only a signal's text names, a model a
    /// fallback moved off that spent nothing and so is no dollars group, and a zone and a model its reason names in
    /// brackets and before a stop, each read without them; a word that starts with a digit is none, whatever text of the
    /// report holds it, and a number of the analysis's own is refused beside them.
    /// </summary>
    [Fact]
    public void AWordTheReportsTextsHold_StartingWithALetterAndHoldingADigit_IsAnIdOfEveryCheckedMember()
    {
        var source = FlowRunReportTests.Source();
        var report = FlowRunReport.Of(source with
        {
            Flow = source.Flow with
            {
                Steps = [.. source.Flow.Steps.Select(step => step.Instance switch
                {
                    "b" => step with { Measure = step.Measure! with { Attempts = [step.Measure.Attempts![0], step.Measure.Attempts[1] with { FallbackFrom = "glm-4.6", FallbackReason = "zone [eu-west2] refused the key for model-y2." }] } },
                    "c" => step with
                    {
                        Measure = step.Measure! with
                        {
                            Attempts = [step.Measure.Attempts![0] with { Legs = [new FlowStepLeg("win-arm64", "Failed", Unparsed: 4)], FirstError = JsonNode.Parse("""{"code":"CS0246","file":"src/Parser2.cs","line":12}""")!.AsObject() }],
                        },
                    },
                    _ => step,
                })],
            },
        });
        string Said(string signal)
            => report["signals"]!.AsArray().Single(row => row!["id"]!.GetValue<string>() == signal)!["said"]!.GetValue<string>();
        var began = report["theRun"]!["beganAt"]!.GetValue<string>();

        // The words are held by the report's texts alone: no row, section or member of it is named so.
        Assert.Equal("c attempt 1: leg 'win-arm64' printed 4 line(s) that look like errors and no parser read", Said("unparsedOutput#1"));
        Assert.Equal("b attempt 2 moved from glm-4.6 to model-y: zone [eu-west2] refused the key for model-y2.", Said("fallback#1"));
        Assert.Equal("""{"code":"CS0246","file":"src/Parser2.cs","line":12}""", report["errors"]![1]!["firstError"]!.ToJsonString());

        JsonNode Analysis(string summary, string why)
            => new JsonObject
            {
                ["summary"] = summary,
                ["painPoints"] = new JsonArray(new JsonObject
                {
                    ["title"] = "Fix CS0246 in Parser2.cs",
                    ["severity"] = "high",
                    ["where"] = new JsonArray("c", "b"),
                    ["evidence"] = JsonNode.Parse($"[{string.Join(",", Cited("errors", "c@1", "firstError.code", "CS0246"), Cited("signals", "unparsedOutput#1", "said", Said("unparsedOutput#1")), Cited("signals", "fallback#1", "said", Said("fallback#1")), Cited("criticalPath", "b", "seconds", "30.0"))}]"),
                    ["impact"] = "c@1 stopped on CS0246 in src/Parser2.cs, its win-arm64 leg unread, after b waited {4} - b's first try on glm-4.6 refused.",
                    ["cause"] = "c@1 failed to compile on CS0246; b's first try on glm-4.6 was refused, so it moved to model-y.",
                    ["recommendation"] = "r",
                }),
                ["noted"] = new JsonArray(new JsonObject { ["what"] = "b moved off glm-4.6 before any token", ["why"] = why }),
            };

        Assert.Empty(FlowAnalysisEvidence.Problems(Analysis("Most of the run went to c, whose win-arm64 leg printed errors no parser read.", "the records hold no usage for glm-4.6, as eu-west2 refused model-y2"), report));
        Assert.Equal(
            [$"summary: writes '3600', {Free}", .. began.Split(' ').Select(word => $"noted[0].why: writes '{word}', {Free}")],
            FlowAnalysisEvidence.Problems(Analysis("Most of the run went to c, whose win-arm64 leg printed errors no parser read, and 3600 s to the queue.", $"the stream ends at {began} with no result line"), report));
    }

    /// <summary>
    /// A word of the report's texts is held whole between white space as well as in its pieces, and a word of the
    /// analysis is read whole first: a parameterized test's id, a first error's file under a directory that starts with a
    /// digit, an SDK's header path and a compiler's C++20 - each a word whose pieces after a slash, a backslash or a plus
    /// start with a digit - are written as the report holds them in a title, a cause, the summary and a thing noted, in
    /// Markdown's marks too, while a number of the analysis's own, a numeral glued to either end of a word the report holds
    /// (<c>C++20²</c>, <c>½src/3rdparty/zlib/inflate.c</c>, refused once as the whole word), a word the report holds
    /// nowhere and a figure glued to a placeholder are still refused.
    /// </summary>
    [Fact]
    public void AWordOfTheReportsTexts_IsHeldWhole_AndTheAnalysissWordIsReadWholeFirst()
    {
        const string Parameterized = "Sizes/ParserTest.ReadsItsForm/1";
        const string Inflate = "src/3rdparty/zlib/inflate.c";
        const string Sdk = @"C:\Program Files (x86)\Windows Kits\10\Include\10.0.22621.0\um\winnt.h";
        var source = FlowRunReportTests.Source();
        var report = FlowRunReport.Of(source with
        {
            Flow = source.Flow with
            {
                Steps = [.. source.Flow.Steps.Select(step => step.Instance switch
                {
                    "b" => step with { Measure = step.Measure! with { Attempts = [step.Measure.Attempts![0] with { FirstError = new JsonObject { ["file"] = Sdk, ["line"] = 3 } }, step.Measure.Attempts[1]] } },
                    "c" => step with
                    {
                        Measure = step.Measure! with
                        {
                            Attempts = [step.Measure.Attempts![0] with { FirstError = new JsonObject { ["id"] = Parameterized, ["file"] = Inflate, ["line"] = 42, ["message"] = "designated initializers are a C++20 extension" } }],
                        },
                    },
                    _ => step,
                })],
            },
        });
        var summary = $"Most of the run went to c, whose test {Parameterized} fails on a **C++20** feature.";
        JsonNode Analysis(string summary, string title, string impact, string cause)
            => new JsonObject
            {
                ["summary"] = summary,
                ["painPoints"] = new JsonArray(new JsonObject
                {
                    ["title"] = title,
                    ["severity"] = "high",
                    ["where"] = new JsonArray("c", "b"),
                    ["evidence"] = JsonNode.Parse($"[{Cited("criticalPath", "b", "seconds", "30.0")}]"),
                    ["impact"] = impact,
                    ["cause"] = cause,
                    ["recommendation"] = "r",
                }),
                ["noted"] = new JsonArray(new JsonObject { ["what"] = $"c@1 stops in `{Inflate}`.", ["why"] = $"the records say only that the code uses a C++20 extension, read from {Sdk}" }),
            };

        // Each is a text of the report alone: no row, section or member of it is named so.
        Assert.Equal(
            [Sdk, Parameterized, Inflate, "designated initializers are a C++20 extension"],
            [report["errors"]![0]!["firstError"]!["file"]!.GetValue<string>(), .. new[] { "id", "file", "message" }.Select(member => report["errors"]![1]!["firstError"]![member]!.GetValue<string>())]);
        Assert.Empty(FlowAnalysisEvidence.Problems(Analysis(summary, $"Fix {Parameterized} and its _C++20_ use", "c@1 failed after b waited {1}.", $"c@1 stops in {Inflate} and b@1 in {Sdk}."), report));
        Assert.Equal(
            [
                $"summary: writes '3600', {Free}",
                $"painPoints[0].title: writes 'C++20²', {Free}",
                $"painPoints[0].impact: {{1}} touches '0' after it - {Touches}",
                $"painPoints[0].impact: writes '{{1}}0.', {OfItsOwn}",
                $"painPoints[0].cause: writes '½{Inflate}', {Free}",
                $"painPoints[0].cause: writes 't+3600', {Free}",
            ],
            FlowAnalysisEvidence.Problems(Analysis($"{summary} It lost 3600 s.", $"Fix {Parameterized} and its C++20² use", "c@1 failed after b waited {1}0.", $"c@1 stops in ½{Inflate} and b@1 in {Sdk} at t+3600 s."), report));
    }

    /// <summary>
    /// The paths the citations write are ids of the members they stand for: the summary and what was noted, which belong
    /// to no pain point, are read with every citation's, and a pain point's title and cause with its own - so c@1.exit,
    /// which the first point cites, is written whole in the summary, a thing noted and why, and the first point's title and
    /// cause, while the second point's title, whose evidence writes no such path, writes it as a number of its own.
    /// </summary>
    [Fact]
    public void ACitationsPath_IsAnIdOfTheSummaryAndTheNoted_AndOfItsOwnPointsTitleAndCause()
    {
        var report = FlowRunReport.Of(FlowRunReportTests.Source());
        static JsonObject Point(string title, string cause, string evidence, string impact)
            => new()
            {
                ["title"] = title,
                ["severity"] = "high",
                ["where"] = new JsonArray("c"),
                ["evidence"] = JsonNode.Parse($"[{evidence}]"),
                ["impact"] = impact,
                ["cause"] = cause,
                ["recommendation"] = "r",
            };
        var analysis = new JsonObject
        {
            ["summary"] = "c@1.exit says why the run failed.",
            ["painPoints"] = new JsonArray(
                Point("c@1.exit is the run's failure", "c@1.exit repeats in every attempt.", Cited("errors", "c@1", "exit", "20"), "c ended {1}."),
                Point("c@1.exit again", "c waited on b.", Cited("criticalPath", "b", "seconds", "30.0"), "b waited {1}.")),
            ["noted"] = new JsonArray(new JsonObject { ["what"] = "c@1.exit is the same in the records", ["why"] = "c@1.exit is all the records hold" }),
        };

        Assert.Equal([$"painPoints[1].title: writes 'c@1.exit', {Free}"], FlowAnalysisEvidence.Problems(analysis, report));
    }

    /// <summary>
    /// An impact the gate refuses is asked again for in the analyst's own conversation, under the gate's one count of
    /// repairs, naming what is wrong: a number of its own is sent back, the answer written with a placeholder in its place
    /// is held on the second turn, and the analysis is kept with its impact as the tool wrote it and the template beside
    /// it, in analysis.json, analysis.md and the text alike.
    /// </summary>
    [Fact]
    public async Task AnImpactRefusedForANumberOfItsOwn_IsRepairedInTheSameConversation_AndKeptAsTheToolWroteIt()
    {
        var (_, temp, analyst, service, run) = await AnalyzableAsync();
        using var disposed = temp;
        const string Template = "The run has spent {1} waiting on its gate.";
        const string Written = "The run has spent 1 session waiting on its gate.";
        string Answer(string impact)
            => Held.Replace("\"The whole wall time so far, which the report names.\"", $"\"{impact}\"", StringComparison.Ordinal)
                .Replace("""{ "section": "steps", "row": "gate", "of": "state", "said": "Paused" }""", """{ "section": "theRun", "row": "", "of": "sessions", "said": "1" }""", StringComparison.Ordinal);
        analyst.Gated.AddRange([Answer(Written), Answer(Template)]);

        var outcome = await service.AnalyzeReportAsync(temp.Path, run, json: false, cancellationToken: Token).WaitAsync(Bound, Token);
        var directory = Assert.Single(analyst.Asked).RecordDirectory!;
        var kept = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "analysis.json")))!["analysis"]!["painPoints"]![0]!;

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal([StructuredOutputGate.Repair([$"$.output.painPoints[0].impact: writes '1', {OfItsOwn}"])], analyst.SentBack);
        Assert.Equal((Written, Template), (kept["impact"]!.GetValue<string>(), kept["impactTemplate"]!.GetValue<string>()));
        Assert.Contains($"\n- impact: {Written}\n", File.ReadAllText(Path.Combine(directory, "analysis.md")), StringComparison.Ordinal);
        Assert.Contains($"\n  impact: {Written}\n", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>What every refusal of a number a member but an impact writes of its own says after the word it stands in.</summary>
    private const string Free = "a number of its own - an analysis's figures are its impacts', each a placeholder of a citation the tool writes, and its evidence's; say this without one, a digit standing only in an id the report holds";

    /// <summary>What every refusal of a placeholder something touches says after what touches it.</summary>
    private const string Touches = "a placeholder stands apart from every letter, number, currency's sign, % and other placeholder, and from a sign before it, for the tool writes the figure with its own unit; leave the unit to it";

    /// <summary>What every refusal of a number an impact writes of its own says after the word it stands in.</summary>
    private const string OfItsOwn = "a number of its own - write each figure as the placeholder of the citation that holds it ({1} the first of its evidence), which the tool writes as the report does; a digit stands only in a placeholder or in an id the report holds";

    /// <summary>An analysis of one pain point, of step b, resting on <paramref name="evidence"/>, whose impact says <paramref name="impact"/>.</summary>
    private static JsonNode AnalysisOf(string evidence, string impact)
        => new JsonObject
        {
            ["summary"] = "s",
            ["painPoints"] = new JsonArray(new JsonObject
            {
                ["title"] = "t",
                ["severity"] = "high",
                ["where"] = new JsonArray("b"),
                ["evidence"] = JsonNode.Parse($"[{evidence}]"),
                ["impact"] = impact,
                ["cause"] = "c",
                ["recommendation"] = "r",
            }),
            ["noted"] = new JsonArray(),
        };

    /// <summary>One citation, as an analysis writes it.</summary>
    private static string Cited(string section, string row, string of, string said)
        => $$"""{"section":"{{section}}","row":"{{row}}","of":"{{of}}","said":"{{said}}"}""";

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

        /// <summary>The events the run says first, made of the request it was asked - a fallback from the model it was resolved to; none unless a test sets them.</summary>
        public Func<AgentStepRequest, IReadOnlyList<AgentEvent>> SaysFirst { get; set; } = _ => [];

        /// <summary>Whether the run throws once its events are said, as a client that fails after paid turns.</summary>
        public bool Throws { get; set; }

        /// <summary>Whether the run is stopped once its events are said, as a person's interrupt stops a client after paid turns.</summary>
        public bool Interrupted { get; set; }

        /// <summary>The answers the run gives in turn, each held by the request's own gate in its one conversation; none unless a test gives them.</summary>
        public List<string> Gated { get; } = [];

        /// <summary>Every message the gate sent back to the run, in order.</summary>
        public List<string> SentBack { get; } = [];

        public async Task<AgentRunResult> RunAsync(AgentStepRequest step, IAgentEventSink? events, CancellationToken cancellationToken)
        {
            Asked.Add(step);

            foreach (var said in SaysFirst(step).Concat(Says))
            {
                await events!.EmitAsync(said);
            }

            if (Throws)
            {
                throw new InvalidOperationException("the client failed after paid turns");
            }

            if (Interrupted)
            {
                throw new OperationCanceledException("the person interrupted the run");
            }

            // Each answer given in turn is held by the request's own gate, as a client holds its agent's, in the one
            // conversation: one the gate sends back is answered again by the next.
            if (Gated.Count > 0)
            {
                var gate = new StructuredOutputGate(step.Output!, new Masking());

                foreach (var answer in Gated)
                {
                    if (await gate.CheckedAsync(Envelopes.Of(JsonNode.Parse(answer)), "answered", new RecordedEvents(), text => { SentBack.Add(text); return Task.CompletedTask; }) is { } ended)
                    {
                        return ended;
                    }
                }
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
