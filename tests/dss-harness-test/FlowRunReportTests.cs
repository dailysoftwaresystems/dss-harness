using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The report of a run, built from its records alone: the same records give the same bytes - text, JSON and Markdown,
/// whatever order the records' files are listed in - the steps in the run's order, the critical path is the chain of
/// steps a faster run must shorten, each signal fires on what it names and is silent below its threshold, and a run
/// still going is said so at the time of its last record, marked partial.
/// </summary>
public sealed class FlowRunReportTests
{
    /// <summary>The fixed time every record of these tests began at.</summary>
    private static readonly DateTimeOffset Began = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The same records give the same bytes, in every form, and the order the item agents' ledgers are listed in changes
    /// nothing; the steps are in the run's order - the instance order of <c>flow.json</c> - so a run whose instances are
    /// listed otherwise is reported in its own order.
    /// </summary>
    [Fact]
    public void TheSameRecords_GiveTheSameBytes_InEveryForm_WhateverOrderTheirFilesAreListedIn()
    {
        var source = Source();

        var first = FlowRunReport.Of(source);
        var again = FlowRunReport.Of(source with { Ledgers = [.. source.Ledgers!.Reverse()] });

        var json = first.ToJsonString(ReportJson.Options);
        Assert.Equal(json, again.ToJsonString(ReportJson.Options));
        Assert.Equal(FlowRunReport.Text(first), FlowRunReport.Text(again));
        Assert.Equal(FlowRunReport.Markdown(first), FlowRunReport.Markdown(again));

        // And a second read of the same records changes nothing.
        Assert.Equal(json, FlowRunReport.Of(source).ToJsonString(ReportJson.Options));

        Assert.Equal(["a", "b", "c"], first["steps"]!.AsArray().Select(step => step!["step"]!.GetValue<string>()));
        Assert.Equal(
            ["c", "b", "a"],
            FlowRunReport.Of(source with { Flow = source.Flow with { Steps = [.. source.Flow.Steps.Reverse()] } })["steps"]!.AsArray().Select(step => step!["step"]!.GetValue<string>()));
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
        Assert.Equal([10.0, 30.0, 10.0], path.Select(link => link!["seconds"]!.GetValue<double>()));
        Assert.Equal("5.0 s on the run to be quiet", path[1]!["waited"]!.GetValue<string>());
    }

    /// <summary>
    /// The critical path reads each instance as its declaration, through a <c>forEach</c>, a <c>repeat</c> and a visit
    /// again: a step in an item is linked from the latest visit of what it depends on in that item, the first step of an
    /// item from what its group depends on, a visit after the first from what sent it back, the first step of an
    /// iteration from the iteration before it, and a step that depends on a group from every instance inside it.
    /// </summary>
    [Fact]
    public void TheCriticalPath_ReadsEachInstanceAsItsDeclaration_ThroughAForEachARepeatAndAVisitAgain()
    {
        var definition = WorkflowFileParser.Parse(
            "workflows/flow/flow.yml",
            """
            name: flow
            steps:
              - name: plan
                type: orient
              - name: items
                forEach: plan.list
                dependsOn: [plan]
                steps:
                  - name: build
                    type: orient
                  - name: check
                    type: orient
                    dependsOn: [build]
              - name: fix
                dependsOn: [items]
                repeat:
                  until: gate.ok
                  maxIterations: 3
                steps:
                  - name: change
                    type: orient
                  - name: gate
                    type: orient
                    dependsOn: [change]
              - name: ship
                type: orient
                dependsOn: [fix]
            """);
        FlowStepRecord Ran(string instance, int from, int to, bool group = false)
            => new(instance, FlowInstanceId.PathOf(instance), group, "Done", 1, FlowInstanceId.VisitOf(instance), [], [], Type: group ? null : "orient", StartedAt: Began.AddSeconds(from), FinishedAt: Began.AddSeconds(to));

        var source = Source() with
        {
            Definition = definition,
            Flow = Source().Flow with
            {
                Steps =
                [
                    Ran("plan", 0, 10),
                    Ran("items", 10, 60, group: true),
                    Ran("items[0].build", 10, 20),
                    Ran("items[0].check", 20, 25),
                    Ran("items[0].build+2", 25, 40),
                    Ran("items[0].check+2", 40, 60),
                    Ran("items[1].build", 10, 30),
                    Ran("items[1].check", 30, 35),
                    Ran("fix", 70, 130, group: true),
                    Ran("fix#1.change", 70, 80),
                    Ran("fix#1.gate", 80, 90),
                    Ran("fix#2.change", 90, 100),
                    Ran("fix#2.gate", 100, 130),
                    Ran("ship", 130, 135),
                ],
            },
        };

        var path = FlowRunReport.Of(source)["criticalPath"]!.AsArray();

        Assert.Equal(
            ["plan", "items[0].build", "items[0].check", "items[0].build+2", "items[0].check+2", "fix#1.change", "fix#1.gate", "fix#2.change", "fix#2.gate", "ship"],
            path.Select(link => link!["step"]!.GetValue<string>()));
        Assert.Equal(125.0, path.Sum(link => link!["seconds"]!.GetValue<double>()));
    }

    /// <summary>
    /// The run's own times are its records': its pause the time between its sessions, its active time the union of every
    /// attempt's runs - two steps running at once counted once, a step whose measure keeps no runs by its own start and
    /// end - and its wait for a person each question's to its first answer, or to the run's last record where it is still
    /// open; each question's row and the longest wait's signal read the same rule.
    /// </summary>
    [Fact]
    public void TheRunsTimes_AreItsPausesBetweenSessions_ItsRunsUnion_AndEachQuestionsWaitToItsFirstAnswer()
    {
        FlowStepRecord Ran(string name, (int From, int To)[] runs, int from, int to)
            => Step(name, "orient", "Done", Began.AddSeconds(from), Began.AddSeconds(to), measure: runs.Length == 0 ? null : new FlowStepMeasure(Attempts: [new FlowStepAttempt(1, Outcome: "Done", Runs: [.. runs.Select(run => new FlowStepSpan(Began.AddSeconds(run.From), Began.AddSeconds(run.To)))])]));

        var source = Source() with
        {
            Flow = Source().Flow with
            {
                Steps =
                [
                    Ran("a", [(0, 50)], 0, 50),
                    Ran("b", [(30, 80), (1000, 1050)], 30, 1050),
                    Ran("c", [], 1060, 1070),
                ],
            },
            Events =
            [
                new(1, Began, FlowEvent.KindOf(FlowChangeKind.SessionBegan)),
                new(2, Began.AddSeconds(100), FlowEvent.KindOf(FlowChangeKind.SessionEnded), Detail: "paused"),
                new(3, Began.AddSeconds(1000), FlowEvent.KindOf(FlowChangeKind.Resumed)),
                new(4, Began.AddSeconds(1100), FlowEvent.KindOf(FlowChangeKind.SessionEnded), Detail: "done"),
            ],
            Questions = new FlowQuestionsFile(1, "r1", Began,
            [
                new FlowQuestionEntry("q1", "answered", "failure", Began.AddSeconds(10), [], [new FlowQuestionOption("retry", "runs it again")],
                    [new FlowAnswerRecord("retry", Began.AddSeconds(40), By: new FlowAnswerBy("person")), new FlowAnswerRecord("retry", Began.AddSeconds(500), By: new FlowAnswerBy("person"))]),
                new FlowQuestionEntry("q2", "open", "failure", Began.AddSeconds(1090), [], [new FlowQuestionOption("retry", "runs it again")], []),
            ]),
        };

        var report = FlowRunReport.Of(source);
        var run = report["theRun"]!;

        Assert.Equal(
            (2, 1100.0, 140.0, 900.0, 40.0),
            (run["sessions"]!.GetValue<int>(), run["wallSeconds"]!.GetValue<double>(), run["activeSeconds"]!.GetValue<double>(), run["pausedSeconds"]!.GetValue<double>(), run["waitingForAPersonSeconds"]!.GetValue<double>()));
        Assert.Equal([30.0, 10.0], report["people"]!.AsArray().Select(person => person!["waitedSeconds"]!.GetValue<double>()));
        Assert.Equal(
            "q1 waited 30.0 s for its answer",
            report["signals"]!.AsArray().Single(signal => signal!["kind"]!.GetValue<string>() == "longestWaitForAPerson")!["said"]!.GetValue<string>());

        // The critical path weighs each step by its runs, never the pause its span held: b began before a ended, so
        // nothing comes before it, and it weighs its two runs.
        Assert.Equal(
            [("b", 100.0), ("c", 10.0)],
            report["criticalPath"]!.AsArray().Select(link => (link!["step"]!.GetValue<string>(), link["seconds"]!.GetValue<double>())));
    }

    /// <summary>
    /// Each signal fires on what it names - the slowest steps, the most expensive, the most retried, every fallback,
    /// the longest wait for a person, a queue wait past its share of a step's time, a cache share under its bound,
    /// output a digest held unread, a review at its bound, a class reopened twice, a hotspot, spend that could not be
    /// counted - each with an id of its own and naming its steps and its numbers, and each threshold-bound one is silent
    /// below its threshold.
    /// </summary>
    [Fact]
    public void EachSignal_FiresOnWhatItNames_AndIsSilentBelowItsThreshold()
    {
        var signals = FlowRunReport.Of(Source())["signals"]!.AsArray();

        Assert.Equal(
            [
                "slowestStep#1", "slowestStep#2", "slowestStep#3", "mostExpensiveStep#1", "mostExpensiveStep#2", "retriedStep#1", "fallback#1",
                "longestWaitForAPerson#1", "queueWait#1", "cacheShare#1", "unparsedOutput#1", "reviewAtItsBound#1", "classReopenedTwice#1",
                "classReopenedTwice#2", "hotspot#1", "uncountedSpend#1",
            ],
            signals.Select(signal => signal!["id"]!.GetValue<string>()));
        Assert.Equal(
            [
                "c attempt 1: leg 'linux' printed 4 line(s) that look like errors and no parser read",
                "'c-rate' of agent 'item-1' was reopened 2 times",
                "'c-frame' of agent 'item-2' was reopened 2 times",
            ],
            signals.Where(signal => signal!["kind"]!.GetValue<string>() is "unparsedOutput" or "classReopenedTwice").Select(signal => signal!["said"]!.GetValue<string>()));

        // The same run under thresholds nothing reaches: only the signals no threshold holds back remain.
        var quiet = FlowRunReport.Of(Source() with
        {
            Thresholds = new FlowReportThresholds(Top: 1, QueueSharePercent: 90, CacheSharePercent: 100, CacheInputTokens: 10_000_000),
        })["signals"]!.AsArray().Select(signal => signal!["kind"]!.GetValue<string>()).ToList();

        Assert.Equal(["slowestStep", "mostExpensiveStep", "retriedStep", "fallback", "longestWaitForAPerson", "unparsedOutput", "reviewAtItsBound", "classReopenedTwice", "classReopenedTwice", "hotspot", "uncountedSpend"], quiet);
    }

    /// <summary>
    /// Every failed attempt is an error row of its own (<c>&lt;instance&gt;@&lt;attempt&gt;</c>): its code, why it
    /// ended, its error, the exception it threw whole - type, message, stack and causes - the first error its legs'
    /// digests read, what handled it and the question it raised; every form says them.
    /// </summary>
    [Fact]
    public void EachFailedAttempt_IsAnErrorRow_WithItsCauseItsExceptionAndItsDigestsFirstError()
    {
        var thrown = new ExceptionRecord("InvalidOperationException", "the node has a parent", [new StackFrame("at Rows.Keep()", File: "src/Rows.cs", Line: 12, InTree: true)], []);
        var source = Source();
        var failed = source.Flow.Steps[2] with
        {
            HandledBy = "fix",
            Question = "q1",
            Measure = new FlowStepMeasure(Attempts: [new FlowStepAttempt(1, Outcome: "Failed", Exit: 70, Error: "it threw", Cause: "LockHeld", Exception: thrown)]),
            Output = JsonNode.Parse("""{"red":[{"leg":"linux","digest":{"first":{"code":"CS1002","file":"src/a.cs","line":3}}}]}"""),
        };

        var report = FlowRunReport.Of(source with { Flow = source.Flow with { Steps = [source.Flow.Steps[0], source.Flow.Steps[1], failed] } });
        var errors = report["errors"]!.AsArray();

        Assert.Equal(["b@1", "c@1"], errors.Select(error => error!["id"]!.GetValue<string>()));
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""
                    {
                      "id": "c@1",
                      "step": "c",
                      "attempt": 1,
                      "exit": 70,
                      "cause": "LockHeld",
                      "error": "it threw",
                      "exception": {"type": "InvalidOperationException", "message": "the node has a parent", "stack": [{"text": "at Rows.Keep()", "file": "src/Rows.cs", "line": 12, "kind": "call", "inTree": true}], "causedBy": []},
                      "firstError": {"code": "CS1002", "file": "src/a.cs", "line": 3},
                      "handledBy": "fix",
                      "question": "q1"
                    }
                    """),
                errors[1]),
            errors[1]!.ToJsonString());

        const string Said = "c@1, exit 70 (LockHeld): it threw; threw InvalidOperationException: the node has a parent; first error code CS1002; file src/a.cs; line 3 (handled by fix), asked as q1";
        Assert.Contains($"  {Said}", FlowRunReport.Text(report).Split('\n'));
        Assert.Contains($"- {Said}", FlowRunReport.Markdown(report).Split('\n'));
    }

    /// <summary>
    /// Every row of every list the report holds has an id of its own, one within its section, by which a reader - the
    /// analyst among them - names it: a step's and a link's its instance, a question's its own, a dollars group's its
    /// name, a failed attempt's and a signal's their own.
    /// </summary>
    [Fact]
    public void EveryRowOfAList_HoldsAnIdOfItsOwn_OneWithinItsSection()
    {
        var report = FlowRunReport.Of(Source());

        string[] IdsOf(JsonNode section) => [.. section.AsArray().Select(row => row!["id"]!.GetValue<string>())];

        Assert.Equal(["a", "b", "c"], IdsOf(report["steps"]!));
        Assert.Equal(["a", "b", "c"], IdsOf(report["criticalPath"]!));
        Assert.Equal(["b@1", "c@1"], IdsOf(report["errors"]!));
        Assert.Equal(["q1", "q2"], IdsOf(report["people"]!));
        Assert.Equal(["model-x", "model-y"], IdsOf(report["totals"]!["dollarsBy"]!["groups"]!));
        Assert.Equal(IdsOf(report["signals"]!).Length, IdsOf(report["signals"]!).Distinct(StringComparer.Ordinal).Count());
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
    /// The report's notes say what its records could not give it: an events file whose last line was torn - the report
    /// as of the event before it - and a definition that does not read, so no critical path is named.
    /// </summary>
    [Fact]
    public void TheNotes_SayATornEventsLine_AndADefinitionThatDoesNotRead()
    {
        var source = Source();

        Assert.Empty(FlowRunReport.Of(source)["notes"]!.AsArray());
        Assert.Equal(
            [
                $"the last line of '{source.Layout.EventsFile}' was torn - a writer stopped mid-line - and is not read: the report is as of the event before it",
                $"'{source.Layout.DefinitionFile}' does not read, so the report names no critical path: the steps' dependencies are the definition's",
            ],
            FlowRunReport.Of(source with { EventsTorn = true, Definition = null })["notes"]!.AsArray().Select(note => note!.GetValue<string>()));
    }

    /// <summary>
    /// The verb answers the report over a run's records on disk, in text, as Markdown and as one JSON document - its
    /// code, its line and the report, the shape a read that stopped early keeps, its report null - and says in its notes
    /// an events file's torn last line, reading the report as of the event before it.
    /// </summary>
    [Fact]
    public async Task TheVerb_AnswersTheReport_FromTheRunsRecordsOnDisk_InOneShape()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var layout = OrchestratorLayout.Of(new HarnessLayout(kit.Main, kit.Main), "o1");
        kit.Harness.OrchestrationStore.WriteFlow(layout, Source().Flow with { Run = "o1" });

        var text = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "-C", kit.Main], TestContext.Current.CancellationToken);
        var markdown = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "--markdown", "-C", kit.Main], TestContext.Current.CancellationToken);
        var json = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "--json", "-C", kit.Main], TestContext.Current.CancellationToken);
        var stopped = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o9", "--json", "-C", kit.Main], TestContext.Current.CancellationToken);

        Assert.Equal(0, text.ExitCode);
        Assert.Contains("totals:", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("## Steps", markdown.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("| a | orient | Done |", markdown.StandardOutput, StringComparison.Ordinal);

        var document = JsonNode.Parse(json.StandardOutput)!.AsObject();
        var refused = JsonNode.Parse(stopped.StandardOutput)!.AsObject();

        Assert.Equal(["exitCode", "summary", "report"], document.Select(member => member.Key));
        Assert.Equal(["exitCode", "summary", "report"], refused.Select(member => member.Key));
        Assert.Equal((0, "o1"), (document["exitCode"]!.GetValue<int>(), document["report"]!["run"]!.GetValue<string>()));
        Assert.Equal(["a", "b", "c"], document["report"]!["steps"]!.AsArray().Select(step => step!["step"]!.GetValue<string>()));
        Assert.Equal((HarnessExit.Refused, (JsonNode?)null), (refused["exitCode"]!.GetValue<int>(), refused["report"]));

        // An events file whose writer stopped mid-line: the report reads the events before it, and its notes say so.
        File.WriteAllText(
            layout.EventsFile,
            System.Text.Json.JsonSerializer.Serialize(new FlowEvent(1, Began, FlowEvent.KindOf(FlowChangeKind.SessionBegan)), JsonStateFile.LineOptions) + "\n{\"seq\":2,\"at\":\"2026-");

        var torn = JsonNode.Parse((await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "--json", "-C", kit.Main], TestContext.Current.CancellationToken)).StandardOutput)!["report"]!;

        Assert.Equal(FlowTimeConverter.Written(Began), torn["at"]!.GetValue<string>());
        Assert.Contains($"the last line of '{layout.EventsFile}' was torn", torn["notes"]![0]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>--steps</c> keeps the rows of the instances it names, every other section whole, and refuses an instance the run
    /// does not hold, naming it, where an empty table would say the run ran none; <c>--by</c> groups the totals' dollars
    /// by what it names, in every form; and the item agents' ledgers are read in one order whatever order the run's steps
    /// name their agents in.
    /// </summary>
    [Fact]
    public async Task TheVerb_KeepsTheRowsAsked_RefusesAnInstanceTheRunDoesNotHold_AndGroupsTheDollarsAsAsked()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var layout = OrchestratorLayout.Of(new HarnessLayout(kit.Main, kit.Main), "o1");
        var source = Source();
        kit.Harness.OrchestrationStore.WriteFlow(layout, source.Flow with { Run = "o1" });
        kit.Harness.OrchestrationStore.WriteDefinition(layout, source.Definition!);

        foreach (var (agent, ledger) in source.Ledgers!)
        {
            kit.Harness.OrchestrationStore.WriteClosureLedger(layout, agent, ledger);
        }

        async Task<JsonNode> ReadAsync(params string[] options)
        {
            var read = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", .. options, "--json", "-C", kit.Main], TestContext.Current.CancellationToken);
            Assert.True(read.ExitCode == 0, read.StandardOutput + read.StandardError);
            return JsonNode.Parse(read.StandardOutput)!["report"]!;
        }

        var kept = await ReadAsync("--steps", "b");
        Assert.Equal(["b"], kept["steps"]!.AsArray().Select(step => step!["step"]!.GetValue<string>()));
        Assert.Equal(3, kept["criticalPath"]!.AsArray().Count);

        var unknown = await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "--steps", "b", "x", "--steps", "w", "-C", kit.Main], TestContext.Current.CancellationToken);
        Assert.Equal(HarnessExit.UsageError, unknown.ExitCode);
        Assert.Contains("--steps names 'w', 'x', which run 'o1' does not hold", unknown.StandardOutput + unknown.StandardError, StringComparison.Ordinal);

        var byStep = await ReadAsync("--by", "step");
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""{"key":"step","groups":[{"id":"a","group":"a","usd":0.01,"tokens":150500},{"id":"b","group":"b","usd":0.02,"tokens":100500}]}"""),
                byStep["totals"]!["dollarsBy"]),
            byStep["totals"]!["dollarsBy"]!.ToJsonString());

        var text = (await CliRunner.RunAsync([FlowService.ReadReportCommand, "o1", "--by", "step", "-C", kit.Main], TestContext.Current.CancellationToken)).StandardOutput.Replace("\r\n", "\n");
        Assert.Contains("  tokens: 180000 input, 1000 output, 70000 cacheRead, 0 cacheWrite, 0 cacheWrite1h, 0 searches\n", text, StringComparison.Ordinal);
        Assert.Contains("  dollars by step:\n    a: 0.0100 USD, 150500 tokens\n    b: 0.0200 USD, 100500 tokens\n", text, StringComparison.Ordinal);

        // The ledgers in another order - the run's steps naming item-2 first - give the same signals.
        var signals = (await ReadAsync())["signals"]!.ToJsonString();
        kit.Harness.OrchestrationStore.WriteFlow(layout, source.Flow with { Run = "o1", Steps = [source.Flow.Steps[1], source.Flow.Steps[0], source.Flow.Steps[2]] });

        Assert.Equal(signals, (await ReadAsync())["signals"]!.ToJsonString());
        Assert.Contains("classReopenedTwice#2", signals, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report's own numbers: the totals are the steps' measures summed, the run's spend is their sum, and the dollars
    /// are grouped by each part of each attempt by the model that served it - an attempt that fell back over to another
    /// model is two parts, each at its own price - with each step's advice by the advisor's model; by the provider, the
    /// step, the agent or the type where asked.
    /// </summary>
    [Fact]
    public void TheTotals_AreTheStepsMeasuresSummed_TheDollarsByEachPartsModel()
    {
        var report = FlowRunReport.Of(Source());

        Assert.Equal(2, report["totals"]!["agents"]!.GetValue<int>());
        Assert.Equal(1, report["totals"]!["stepsFailed"]!.GetValue<int>());
        Assert.Equal(1, report["totals"]!["retries"]!.GetValue<int>());
        Assert.Equal(1, report["totals"]!["fallbacks"]!.GetValue<int>());
        Assert.Equal(0.03, report["totals"]!["usd"]!.GetValue<double>());
        Assert.Equal(180_000, report["totals"]!["tokens"]!["input"]!.GetValue<long>());

        var source = Source();
        var fellBack = source.Flow.Steps[0] with
        {
            Measure = new FlowStepMeasure(
                Attempts:
                [
                    new FlowStepAttempt(
                        1,
                        Outcome: "Done",
                        Provider: "p2",
                        Model: "model-b",
                        FallbackFrom: "model-a",
                        Usd: 0.6,
                        Parts: [new FlowStepPart("model-a", "p1", new AgentUsage(1000, 0, 0, 0, 0, 0), 0.5), new FlowStepPart("model-b", "p2", new AgentUsage(200, 0, 0, 0, 0, 0), 0.1)]),
                ],
                Advice: new FlowStepAdvice("model-c", 50, 0.05),
                Totals: new FlowStepTotals(new AgentUsage(1200, 0, 0, 0, 0, 0), 0.6)),
        };
        var parted = source with { Flow = source.Flow with { Steps = [fellBack, source.Flow.Steps[1]] } };

        string GroupsBy(string? by)
            => FlowRunReport.Of(parted with { By = by })["totals"]!["dollarsBy"]!.ToJsonString(ReportJson.Options).Replace("\r\n", "\n").Replace("\n", string.Empty).Replace(" ", string.Empty);

        Assert.Equal("""{"key":"model","groups":[{"id":"model-a","group":"model-a","usd":0.5,"tokens":1000},{"id":"model-b","group":"model-b","usd":0.1,"tokens":200},{"id":"model-c","group":"model-c","usd":0.05,"tokens":50},{"id":"model-y","group":"model-y","usd":0.02,"tokens":100500}]}""", GroupsBy(null));
        Assert.Equal("""{"key":"provider","groups":[{"id":"-","group":"-","usd":0.07,"tokens":100550},{"id":"p1","group":"p1","usd":0.5,"tokens":1000},{"id":"p2","group":"p2","usd":0.1,"tokens":200}]}""", GroupsBy("provider"));
        Assert.Equal("""{"key":"type","groups":[{"id":"orient","group":"orient","usd":0.67,"tokens":101750}]}""", GroupsBy("type"));
        Assert.Equal(0.67, FlowRunReport.Of(parted)["totals"]!["usd"]!.GetValue<double>());
    }

    /// <summary>
    /// The records of a run that exercises every signal: three steps in a chain, one retried, one cold, one that asked a
    /// person, one whose leg printed lines no parser read; two item agents, each with a class reopened twice.
    /// </summary>
    internal static FlowRunReportSource Source()
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
                Step("c", "class-closure", "Failed", Began.AddSeconds(50), Began.AddSeconds(60), agent: "item-1", measure: new FlowStepMeasure(Attempts: [Attempt(1, "Failed", exit: 20, error: "it did not pass") with { Legs = [new FlowStepLeg("linux", "Failed", Unparsed: 4)] }], Totals: new FlowStepTotals()),
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

        return new FlowRunReportSource(
            layout,
            flow,
            questions,
            null,
            events,
            [
                ("item-2", new FlowClosureLedgerFile(1, "item-2", [new FlowClosureReopens("c-frame", ["b#1.route", "b#2.route"])])),
                ("item-1", new FlowClosureLedgerFile(1, "item-1", [new FlowClosureReopens("c-rate", ["a#1.route", "a#2.route"]), new FlowClosureReopens("c-load", ["a#1.route"])])),
            ],
            definition);
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
