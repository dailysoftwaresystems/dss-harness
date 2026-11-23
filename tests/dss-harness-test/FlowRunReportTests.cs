using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>
/// The report of a run, built from its records alone: the same records give the same bytes - text, JSON and Markdown,
/// whatever order the records' files hold - the critical path is the chain of steps a faster run must shorten, each
/// signal fires on what it names and is silent below its threshold, and a run still going is said so at the time of
/// its last record, marked partial.
/// </summary>
public sealed class FlowRunReportTests
{
    /// <summary>The fixed time every record of these tests began at.</summary>
    private static readonly DateTimeOffset Began = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The same records give the same bytes, in every form - and the steps' order in <c>flow.json</c> changes nothing,
    /// for the report reads the instances in one order of its own.
    /// </summary>
    [Fact]
    public void TheSameRecords_GiveTheSameBytes_InEveryForm_WhateverTheirFilesOrder()
    {
        var source = Source();

        var first = FlowRunReport.Of(source);
        var again = FlowRunReport.Of(source with { Flow = source.Flow with { Steps = [.. source.Flow.Steps.Reverse()] } });

        var json = first.ToJsonString(ReportJson.Options);
        Assert.Equal(json, again.ToJsonString(ReportJson.Options));
        Assert.Equal(FlowRunReport.Text(first), FlowRunReport.Text(again));
        Assert.Equal(FlowRunReport.Markdown(first), FlowRunReport.Markdown(again));

        // And a second read of the same records changes nothing.
        Assert.Equal(json, FlowRunReport.Of(source).ToJsonString(ReportJson.Options));
    }

    /// <summary>
    /// The critical path is the chain of <c>dependsOn</c> whose times add to the run's active time: the longest road
    /// through the run's instances by each one's wall time, each link with what the step waited for.
    /// </summary>
    [Fact]
    public void TheCriticalPath_IsTheLongestRoadThroughTheRun_ByEachStepsWallTime()
    {
        var report = FlowRunReport.Of(Source());

        var path = report["criticalPath"]!.AsArray();

        Assert.Equal(["a", "b", "c"], path.Select(link => link!["step"]!.GetValue<string>()));
        Assert.Equal(10.0, path[0]!["seconds"]!.GetValue<double>());
        Assert.Equal(30.0, path[1]!["seconds"]!.GetValue<double>());
        Assert.Equal("5.0 on the run to be quiet", path[1]!["waited"]!.GetValue<string>());
    }

    /// <summary>
    /// Each signal fires on what it names - the slowest steps, the most expensive, the most retried, every fallback,
    /// the longest wait for a person, a queue wait past its share of a step's time, a cache share under its bound, a
    /// review at its bound, a class reopened twice, a hotspot, spend that could not be counted - each naming its steps
    /// and its numbers, and each threshold-bound one is silent below its threshold.
    /// </summary>
    [Fact]
    public void EachSignal_FiresOnWhatItNames_AndIsSilentBelowItsThreshold()
    {
        var fired = FlowRunReport.Of(Source())["signals"]!.AsArray().Select(signal => signal!["kind"]!.GetValue<string>()).ToList();

        Assert.True(
            fired.SequenceEqual(["slowestStep", "slowestStep", "slowestStep", "mostExpensiveStep", "mostExpensiveStep", "retriedStep", "fallback", "longestWaitForAPerson", "queueWait", "cacheShare", "reviewAtItsBound", "classReopenedTwice", "hotspot", "uncountedSpend"]),
            string.Join(", ", fired));

        // The same run under thresholds nothing reaches: only the signals no threshold holds back remain.
        var quiet = FlowRunReport.Of(Source() with
        {
            Thresholds = new FlowReportThresholds(Top: 1, QueueSharePercent: 90, CacheSharePercent: 100, CacheInputTokens: 10_000_000),
        })["signals"]!.AsArray().Select(signal => signal!["kind"]!.GetValue<string>()).ToList();

        Assert.Equal(["slowestStep", "mostExpensiveStep", "retriedStep", "fallback", "longestWaitForAPerson", "reviewAtItsBound", "classReopenedTwice", "hotspot", "uncountedSpend"], quiet);
    }

    /// <summary>A run still going is said so at the time of its last record, marked partial.</summary>
    [Fact]
    public void ARunStillGoing_IsSaidSoAtItsLastRecord_MarkedPartial()
    {
        var report = FlowRunReport.Of(Source() with { Flow = Source().Flow with { Status = "Running" } });

        Assert.True(report["partial"]!.GetValue<bool>());
        Assert.Equal(FlowTimeConverter.Written(Began.AddSeconds(100)), report["at"]!.GetValue<string>());
    }

    /// <summary>
    /// The verb answers the report over a run's records on disk, in text, as Markdown and as one JSON document - the
    /// same document the builder answers, for the run is still there however it ended.
    /// </summary>
    [Fact]
    public async Task TheVerb_AnswersTheReport_FromTheRunsRecordsOnDisk()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var layout = OrchestratorLayout.Of(new HarnessLayout(kit.Main, kit.Main), "o1");
        kit.Harness.OrchestrationStore.WriteFlow(layout, Source().Flow with { Run = "o1" });

        var text = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "-C", kit.Main], TestContext.Current.CancellationToken);
        var markdown = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "--markdown", "-C", kit.Main], TestContext.Current.CancellationToken);
        var json = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "--json", "-C", kit.Main], TestContext.Current.CancellationToken);

        Assert.Equal(0, text.ExitCode);
        Assert.Contains("totals:", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("## Steps", markdown.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("| a | orient | Done |", markdown.StandardOutput, StringComparison.Ordinal);

        var document = JsonNode.Parse(json.StandardOutput)!;

        Assert.Equal("o1", document["run"]!.GetValue<string>());
        Assert.Equal(["a", "b", "c"], document["steps"]!.AsArray().Select(step => step!["step"]!.GetValue<string>()));
    }

    /// <summary>The report's own numbers: the totals are the steps' measures summed, and the run's spend is their sum.</summary>
    [Fact]
    public void TheTotals_AreTheStepsMeasuresSummed()
    {
        var report = FlowRunReport.Of(Source());

        Assert.Equal(2, report["totals"]!["agents"]!.GetValue<int>());
        Assert.Equal(1, report["totals"]!["stepsFailed"]!.GetValue<int>());
        Assert.Equal(1, report["totals"]!["retries"]!.GetValue<int>());
        Assert.Equal(1, report["totals"]!["fallbacks"]!.GetValue<int>());
        Assert.Equal(0.03, report["totals"]!["usd"]!.GetValue<double>());
        Assert.Equal(180_000, report["totals"]!["tokens"]!["input"]!.GetValue<long>());
        Assert.Equal(2, report["totals"]!["dollarsByModel"]!.AsArray().Count);
    }

    /// <summary>The records of a run that exercises every signal: three steps in a chain, one retried, one cold, one that asked a person.</summary>
    private static FlowRunReportSource Source()
    {
        using var temp = new TempDirectory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var definition = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            """
            name: flow
            steps:
              - name: a
                type: orient
              - name: b
                type: orient
                dependsOn: [a]
              - name: c
                type: orient
                dependsOn: [b]
            """);
        var flow = new FlowFile(
            1,
            "r1",
            "flow",
            "digest",
            new Dictionary<string, string>(),
            "Done",
            new FlowBudgetRecord(0.03, 180_000, 0, "no provider lists 'model-z'"),
            0,
            [
                Step("a", "orient", "Done", Began, Began.AddSeconds(10), agent: "item-1", measure: new FlowStepMeasure(Attempts: [Attempt(1, "Done", usd: 0.01, model: "model-x", tokens: new AgentUsage(90_000, 500, 60_000, 0, 0, 0))], Totals: new FlowStepTotals(new AgentUsage(90_000, 500, 60_000, 0, 0, 0), 0.01))),
                Step("b", "orient", "Done", Began.AddSeconds(15), Began.AddSeconds(45), agent: "item-2", measure: new FlowStepMeasure(
                    Waited: [new FlowStepWait(FlowWaits.Quiet, 5.0)],
                    Attempts: [Attempt(1, "Failed"), Attempt(2, "Done", usd: 0.02, model: "model-y", tokens: new AgentUsage(90_000, 500, 10_000, 0, 0, 0), fallbackFrom: "model-w", fallbackReason: "key refused")],
                    Totals: new FlowStepTotals(new AgentUsage(90_000, 500, 10_000, 0, 0, 0), 0.02))),
                Step("c", "orient", "Failed", Began.AddSeconds(50), Began.AddSeconds(60), agent: "item-1", measure: new FlowStepMeasure(Attempts: [Attempt(1, "Failed", exit: 20, error: "it did not pass")]),
                    output: JsonNode.Parse("""{"hotspots":[{"class":"c-rate","path":"src/Rates/Home.cs","touches":6}]}""")),
            ],
            [],
            Began);
        var questions = new FlowQuestionsFile(1, "r1", Began,
        [
            new FlowQuestionEntry(
                "q1",
                "answered",
                "failure",
                Began.AddSeconds(20),
                [],
                [new FlowQuestionOption("retry", "runs it again"), new FlowQuestionOption("abort", "ends the run")],
                [new FlowAnswerRecord("retry", Began.AddSeconds(80), By: new FlowAnswerBy("person"))]),
            new FlowQuestionEntry(
                "q2",
                "open",
                "loopBound",
                Began.AddSeconds(30),
                [],
                [new FlowQuestionOption("more", "one more"), new FlowQuestionOption("accept", "ends as it stands"), new FlowQuestionOption("abort", "ends the run")],
                []),
        ]);
        var events = new List<FlowEvent>
        {
            new(1, Began, FlowEvent.KindOf(FlowChangeKind.SessionBegan), null, null, null, null, null, null),
            new(2, Began.AddSeconds(100), FlowEvent.KindOf(FlowChangeKind.SessionEnded), null, null, null, null, null, "done"),
        };
        var ledger = new FlowClosureLedgerFile(1, "item-1", [new FlowClosureReopens("c-rate", 2)]);

        return new FlowRunReportSource(layout, flow, questions, null, events, [("item-1", ledger)], definition);
    }

    /// <summary>One step record of these tests' run.</summary>
    private static FlowStepRecord Step(
        string name,
        string type,
        string state,
        DateTimeOffset began,
        DateTimeOffset ended,
        string? agent = null,
        FlowStepMeasure? measure = null,
        JsonNode? output = null)
        => new(name, name, false, state, measure?.Attempts?.Count ?? 1, 1, [], [], Type: type, StartedAt: began, FinishedAt: ended, Agent: agent, Measure: measure, Output: output);

    /// <summary>One attempt of these tests' steps.</summary>
    private static FlowStepAttempt Attempt(int n, string outcome, double? usd = null, string? model = null, AgentUsage? tokens = null, string? fallbackFrom = null, string? fallbackReason = null, int? exit = null, string? error = null)
        => new(n, Outcome: outcome, Exit: exit, Error: error, Model: model, Tokens: tokens, Usd: usd, FallbackFrom: fallbackFrom, FallbackReason: fallbackReason);
}
