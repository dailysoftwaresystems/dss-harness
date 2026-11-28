using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;

namespace DssHarness.Tests;

/// <summary>What a run's reports say it left for a person.</summary>
public sealed class FlowReportsTests
{
    /// <summary>
    /// Each pull request a pull-request step opened or found open is left for a person, by its address and once however
    /// many steps answered it, before what each loop left: the run never merges one. A step that answered none leaves none,
    /// and an address another type of step answered is no pull request.
    /// </summary>
    [Fact]
    public void EveryPullRequestTheRunOpened_IsLeftForAPersonToMerge()
    {
        var layout = OrchestratorLayout.Of(new HarnessLayout("/repo", "/repo"), "r1");
        var opened = new FlowStepRecord("pr", "pr", false, "Done", 1, 1, [], [], Type: "pull-request", Output: JsonNode.Parse("""{"number":42,"url":"https://github.com/acme/widgets/pull/42","created":true}"""));
        var file = new FlowFile(
            1,
            "r1",
            "flow",
            "digest",
            new Dictionary<string, string>(),
            "Done",
            new FlowBudgetRecord(0, 0, 0),
            0,
            [
                opened,
                opened with { Instance = "again", Output = JsonNode.Parse("""{"number":42,"url":"https://github.com/acme/widgets/pull/42","created":false}""") },
                new FlowStepRecord("failed", "failed", false, "Failed", 1, 1, [], [], Type: "pull-request"),
                new FlowStepRecord("link", "link", false, "Done", 1, 1, [], [], Type: "reason", Output: JsonNode.Parse("""{"url":"https://example.com/not-a-pull-request"}""")),
                new FlowStepRecord("loop", "loop", true, "Done", 1, 1, [], [], Leftover: JsonValue.Create("one test")),
            ],
            [],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(
            [
                "pull request https://github.com/acme/widgets/pull/42 is open: the run did not merge it and will not - merging it is a person's",
                "loop loop left: one test",
            ],
            FlowReports.LeftForAPerson(layout, file, questions: null, FlowEngineState.NoEngine));
    }

    /// <summary>
    /// Each open question is left for a person with the next step it takes, where no engine runs the run: a question still
    /// owing the recommendation it is answered with - one a stop deferred, and one a crash left sought, for no advisor seeks
    /// one between sessions - names resume-orchestrator, which seeks it first, its answer then and abort taken now, while a
    /// question recommended, and an agent's own, which carries its own, name the answer itself. A question answered already
    /// is left for nobody.
    /// </summary>
    [Fact]
    public void EveryOpenQuestion_IsLeftForAPerson_WithItsNextStep_TheResumeWhereItOwesItsRecommendationAndNoEngineRuns()
    {
        var (layout, file, questions) = OpenQuestions();

        Assert.Equal(
            [
                "question q1 (failure) is open: 'q1' happened; the run was stopped while the advisor ran: the recommendation is sought when the run is resumed with this question still open - "
                    + "its recommendation is still owed - no advisor seeks one between sessions, and resume-orchestrator r1 seeks it first; answer with answer-orchestrator r1 <answer> --question q1 then, and answer-orchestrator r1 abort --question q1 is taken now",
                "question q2 (turnLimit) is open: 'q2' happened; recommended: 'retry', the step was nearly done - answer with answer-orchestrator r1 <answer> --question q2",
                "question q3 (ask) is open: 'q3' happened; no recommendation was sought - answer with answer-orchestrator r1 <answer> --question q3",
                "question q4 (failure) is open: 'q4' happened; a recommendation is being sought from the advisor - "
                    + "its recommendation is still owed - no advisor seeks one between sessions, and resume-orchestrator r1 seeks it first; answer with answer-orchestrator r1 <answer> --question q4 then, and answer-orchestrator r1 abort --question q4 is taken now",
                "question q6 (failure) is open: 'q6' happened; no recommendation was sought - "
                    + "its recommendation is still owed - no advisor seeks one between sessions, and resume-orchestrator r1 seeks it first; answer with answer-orchestrator r1 <answer> --question q6 then, and answer-orchestrator r1 abort --question q6 is taken now",
            ],
            FlowReports.LeftForAPerson(layout, file, questions, FlowEngineState.NoEngine));

        // An engine recorded whose process has gone is no engine running the run: its next session, a resume, seeks it.
        Assert.Equal(
            FlowReports.LeftForAPerson(layout, file, questions, FlowEngineState.NoEngine),
            FlowReports.LeftForAPerson(layout, file, questions, new FlowEngineState(FlowEngineState.Gone, "process 7", [])));
    }

    /// <summary>
    /// Where a live engine runs the run, a question whose advisor seeks its recommendation now is left with its answer,
    /// taken at once and ending the asking - never the resume, which a live owner refuses, nor a stop, which would end the
    /// advice and leave the next resume to pay for it again - and one a pause defers is left to the pause's advice, the
    /// answer given then, abort taken now; what owes nothing names the answer whatever the engine does.
    /// </summary>
    [Fact]
    public void AnOpenQuestionOfALiveRun_IsLeftWithTheAnswerWhileAdvised_AndThePausesAdviceWhereAPauseDefersIt()
    {
        var (layout, file, questions) = OpenQuestions();
        string[] Steps(string state) => [.. FlowReports.LeftForAPerson(layout, file, questions, new FlowEngineState(state, "process 7", [])).Select(line => line[(line.IndexOf(" - ", StringComparison.Ordinal) + 3)..])];

        const string AnswerQ2 = "answer with answer-orchestrator r1 <answer> --question q2";
        const string AnswerQ3 = "answer with answer-orchestrator r1 <answer> --question q3";
        const string AdvisedQ4 = "its recommendation is being sought: answer with answer-orchestrator r1 <answer> --question q4, taken now and ending the asking";

        const string PauseQ1 = "its recommendation is still owed - the run seeks it once its pause is complete; answer with answer-orchestrator r1 <answer> --question q1 then, and answer-orchestrator r1 abort --question q1 is taken now";

        // A running engine seeks a recommendation as it raises the question, and defers one only for a pause asked,
        // which begins at its next pass.
        Assert.Equal(
            [
                PauseQ1,
                AnswerQ2,
                AnswerQ3,
                AdvisedQ4,
                "its recommendation is being sought: answer with answer-orchestrator r1 <answer> --question q6, taken now and ending the asking",
            ],
            Steps(FlowEngineState.Running));

        // A pausing engine, and one recording what its complete pause left running, defers what it raises to the pause's
        // advice, which a complete pause gives before its session ends; the advice under way finishes, and is answered now.
        foreach (var pausing in new[] { FlowEngineState.Pausing, FlowEngineState.Recording })
        {
            Assert.Equal(
                [
                    PauseQ1,
                    AnswerQ2,
                    AnswerQ3,
                    AdvisedQ4,
                    "its recommendation is still owed - the run seeks it once its pause is complete; answer with answer-orchestrator r1 <answer> --question q6 then, and answer-orchestrator r1 abort --question q6 is taken now",
                ],
                Steps(pausing));
        }

        // A starting engine seeks every recommendation the run owes - one a stop deferred, one a crash left sought, one no
        // advisor was asked of yet - and takes an answer as it does: what the session before left says nothing of it.
        Assert.Equal(
            [
                "its recommendation is being sought: answer with answer-orchestrator r1 <answer> --question q1, taken now and ending the asking",
                AnswerQ2,
                AnswerQ3,
                AdvisedQ4,
                "its recommendation is being sought: answer with answer-orchestrator r1 <answer> --question q6, taken now and ending the asking",
            ],
            Steps(FlowEngineState.Starting));
    }

    /// <summary>
    /// Where the live engine is stopping - its hard stop, or a stop once its pause was complete - a question still owing
    /// its recommendation is left to the resume once the engine has gone, for the stop ends every advice under way and
    /// starts none - the one sought now among them - and once the engine has gone, the resume itself.
    /// </summary>
    [Fact]
    public void AnOpenQuestionOfAStoppingRun_IsLeftToTheResumeOnceTheEngineHasGone()
    {
        var (layout, file, questions) = OpenQuestions();

        Assert.Equal(
            [
                "question q1 (failure) is open: 'q1' happened; the run was stopped while the advisor ran: the recommendation is sought when the run is resumed with this question still open - "
                    + "its recommendation is still owed - the stop under way seeks none, and resume-orchestrator r1 seeks it first once the engine has gone; answer with answer-orchestrator r1 <answer> --question q1 then, and answer-orchestrator r1 abort --question q1 is taken now",
                "question q2 (turnLimit) is open: 'q2' happened; recommended: 'retry', the step was nearly done - answer with answer-orchestrator r1 <answer> --question q2",
                "question q3 (ask) is open: 'q3' happened; no recommendation was sought - answer with answer-orchestrator r1 <answer> --question q3",
                "question q4 (failure) is open: 'q4' happened; a recommendation is being sought from the advisor - "
                    + "its recommendation is still owed - the stop under way seeks none, and resume-orchestrator r1 seeks it first once the engine has gone; answer with answer-orchestrator r1 <answer> --question q4 then, and answer-orchestrator r1 abort --question q4 is taken now",
                "question q6 (failure) is open: 'q6' happened; no recommendation was sought - "
                    + "its recommendation is still owed - the stop under way seeks none, and resume-orchestrator r1 seeks it first once the engine has gone; answer with answer-orchestrator r1 <answer> --question q6 then, and answer-orchestrator r1 abort --question q6 is taken now",
            ],
            FlowReports.LeftForAPerson(layout, file, questions, new FlowEngineState(FlowEngineState.Stopping, "process 7", [])));
    }

    /// <summary>
    /// A run left with five questions open - one a stop or a pause deferred, one recommended, an agent's own, one a crash
    /// or a live advisor left sought, one no advisor has been asked of yet - and one answered already.
    /// </summary>
    private static (OrchestratorLayout Layout, FlowFile File, FlowQuestionsFile Questions) OpenQuestions()
    {
        var layout = OrchestratorLayout.Of(new HarnessLayout("/repo", "/repo"), "r1");
        var file = new FlowFile(
            1,
            "r1",
            "flow",
            "digest",
            new Dictionary<string, string>(),
            "Stopped",
            new FlowBudgetRecord(0, 0, 0),
            0,
            [],
            [],
            DateTimeOffset.UnixEpoch);
        FlowQuestionEntry Open(string id, string kind, FlowRecommendation recommendation, string state = "open")
            => new(id, state, kind, DateTimeOffset.UnixEpoch, [], [new FlowQuestionOption("retry", "the step runs again as a new attempt")], [], ProblemShown: new FlowQuestionProblem($"'{id}' happened"), Recommendation: recommendation);
        var questions = new FlowQuestionsFile(
            1,
            "r1",
            DateTimeOffset.UnixEpoch,
            [
                Open("q1", "failure", new FlowRecommendation("deferred", Why: "the run was stopped while the advisor ran: the recommendation is sought when the run is resumed with this question still open")),
                Open("q2", "turnLimit", new FlowRecommendation("given", "retry", Why: "the step was nearly done", By: "advisor", Agent: "advisor")),
                Open("q3", "ask", FlowRecommendation.NotSought),
                Open("q4", "failure", new FlowRecommendation("sought", Why: "a recommendation is being sought from the advisor")),
                Open("q5", "failure", new FlowRecommendation("deferred", Why: "never read"), state: "answered"),
                Open("q6", "failure", FlowRecommendation.NotSought),
            ]);

        return (layout, file, questions);
    }

    /// <summary>
    /// Each path the run's folds changed that git ignores - as its commit steps answered them, once however many did, and
    /// none a later commit took once git no longer ignored it - is left for a person in one line, before the questions and
    /// the loops: folded, ignored by git, never committed, and never in what the run pushed.
    /// </summary>
    [Fact]
    public void EveryPathGitIgnored_IsLeftForAPerson_AsFoldedAndNeverCommitted()
    {
        var layout = OrchestratorLayout.Of(new HarnessLayout("/repo", "/repo"), "r1");
        var file = new FlowFile(
            1,
            "r1",
            "flow",
            "digest",
            new Dictionary<string, string>(),
            "Done",
            new FlowBudgetRecord(0, 0, 0),
            0,
            [
                new FlowStepRecord("first", "first", false, "Done", 1, 1, [], [], Type: "commit", Output: JsonNode.Parse("""{"committed":false,"sha":null,"paths":[],"ignored":["evidence/run.txt","later.txt"]}""")),
                new FlowStepRecord("second", "second", false, "Done", 1, 1, [], [], Type: "commit", Output: JsonNode.Parse("""{"committed":true,"sha":"abc","paths":["later.txt"],"ignored":["evidence/run.txt"]}""")),
                new FlowStepRecord("other", "other", false, "Done", 1, 1, [], [], Type: "reason", Output: JsonNode.Parse("""{"ignored":["not/a/commit.txt"]}""")),
                new FlowStepRecord("loop", "loop", true, "Done", 1, 1, [], [], Leftover: JsonValue.Create("one test")),
            ],
            [],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(
            [
                "1 path(s) folded, ignored by git, never committed: 'evidence/run.txt' - the run commits no path git ignores, and committing one, or leaving it out, is a person's",
                "loop loop left: one test",
            ],
            FlowReports.LeftForAPerson(layout, file, questions: null, FlowEngineState.NoEngine));
    }

    /// <summary>
    /// The report carries the counts by which a repository sees whether its rounds converge, gathered by the one owner
    /// the run's words read too: each class's claim as its item's newest class-closure check judged the record - a check
    /// run again after a rework is the one that stands, a plan audit's own closure member is no item's record, and an
    /// item whose check was skipped has none - each item's counts, the classes its reviews reopened with how often, by
    /// each item's ledger, which its reopened-twice signal reads by the route's own rule, the homes touched often enough
    /// to name, and each issue carried to a later round, fixed there or left.
    /// </summary>
    [Fact]
    public void TheReportCarriesTheClosureCounts_OfRecordsItemsReopeningsHotspotsAndLaterRounds()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        static JsonNode Checked(string record, string hotspots = "[]")
            => JsonNode.Parse($$"""{"passed":true,"problems":[],"hotspots":{{hotspots}},"touches":[],"record":{{record}}}""")!;
        const string RateRecord = """
            [{"class":"c-rate","claim":"the rate holds over every format",
              "sites":[{"path":"src/Rates/Home.cs","after":"home"},{"path":"src/Old.cs","after":"reads-home"},{"path":"docs/old.md","after":"deleted"},{"path":"src/Older.cs","after":"deleted"}],
              "universe":{"axes":[],"cellsPinned":4,"cellsArmed":0},
              "neighbours":[{"cell":"json","result":"passed"},{"cell":"xml","result":"failed-fixed"},{"cell":"yaml","result":"unmeasured","reason":"no reader"}],
              "homeAudit":{"consolidated":["the rate"],"invariants":["one place decides"],"structural":["Home holds it"]}}]
            """;
        var file = new FlowFile(
            1,
            "r1",
            "flow",
            "digest",
            new Dictionary<string, string>(),
            "Done",
            new FlowBudgetRecord(0, 0, 0),
            0,
            [
                new FlowStepRecord("planning.plan-audit", "plan-audit", false, "Done", 1, 1, [], [], Type: "reason", Output: JsonNode.Parse("""{"go":false,"closure":[{"class":"c-plan","missingSites":["src/Plan.cs"],"missingCells":[]}]}""")),
                new FlowStepRecord("rounds#1.items[0].implement", "implement", false, "Done", 1, 1, [], [], Type: "reason", Output: JsonNode.Parse("""{"closure":[{"class":"c-rate","claim":"an answer no check judged"}]}""")),
                new FlowStepRecord("rounds#1.items[0].class-closure", "class-closure", false, "Done", 1, 1, [], [], Type: "class-closure", Output: Checked("""[{"class":"c-rate","claim":"named a case: one format"}]""")),
                new FlowStepRecord("rounds#1.items[1].class-closure", "class-closure", false, "Done", 1, 1, [], [], Type: "class-closure", Output: Checked("""[{"class":"c-frame","claim":"the frame is decided once"}]""")),
                new FlowStepRecord("rounds#1.items[0].fix#1.route", "route", false, "Done", 1, 1, [], [], Type: "route-findings", Output: JsonNode.Parse("""{"rework":[],"homeAudit":[],"reopened":[{"class":"c-rate","rounds":1}]}""")),
                new FlowStepRecord("rounds#1.items[0].fix#1.revise.reclass-closure", "reclass-closure", false, "Done", 1, 1, [], [], Type: "class-closure", Output: Checked(RateRecord, """[{"class":"c-rate","path":"src/Rates/Home.cs","touches":6}]""")),
                new FlowStepRecord("rounds#1.items[0].fix#2.route", "route", false, "Done", 1, 2, [], [], Type: "route-findings", Output: JsonNode.Parse("""{"rework":[],"homeAudit":[],"reopened":[{"class":"c-rate","rounds":2}]}""")),
                new FlowStepRecord("rounds#1.items[0].fix#3.route", "route", false, "Failed", 1, 1, [], [], Type: "route-findings", Output: JsonNode.Parse("""{"rework":[],"homeAudit":[],"reopened":[{"class":"c-rate","rounds":9}]}""")),
                new FlowStepRecord("rounds#1.items[2].class-closure", "class-closure", false, "Skipped", 1, 1, [], [], Type: "class-closure", Output: Checked("""[{"class":"c-skip","claim":"never judged"}]""")),
            ],
            [],
            DateTimeOffset.UnixEpoch);
        var ledgers = new List<(string Agent, FlowClosureLedgerFile Ledger)>
        {
            ("a1", new FlowClosureLedgerFile(1, "a1", [new FlowClosureReopens("c-rate", ["rounds#1.items[0].fix#1.route", "rounds#1.items[0].fix#2.route"])])),
        };
        var next = new FlowNextRoundFile(
            1,
            "r1",
            DateTimeOffset.UnixEpoch,
            [
                new FlowNextIssue("nx-1", "a broken parser", "it reads one format", ["src/Parser.cs"], ["src/Parser.cs"], "", [], "rounds#1.items[0].triage", DateTimeOffset.UnixEpoch) { SettledBy = "rounds#2.items[0]" },
                new FlowNextIssue("nx-2", "a stale document", "it names the old rate", ["docs/rate.md"], ["docs/rate.md"], "", [], "rounds#1.items[1].triage", DateTimeOffset.UnixEpoch),
            ]);

        _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, file, Ledgers: ledgers, NextRound: next), "done", 0, DateTimeOffset.UnixEpoch);

        var report = harness.FileSystem.ReadAllText(layout.ReportFile).Replace("\r\n", "\n");

        // Of each item: its rounds of review - a routing that failed is none - its home audits, and of each class of the
        // record its newest check judged the sites now reading the home and those deleted, the cells pinned, the
        // neighbours measured and not, and its send-backs as the item's newest routing that ended done counted them.
        Assert.Equal(
            [
                "- 'c-rate': the rate holds over every format",
                "- 'c-frame': the frame is decided once",
                "- item 'rounds#1.items[0]': 2 review round(s), 1 home audit(s), classes 'c-rate' (1 site(s) consolidated, 2 deleted, 4 cell(s) pinned, 2 neighbour(s) measured, 1 unmeasured, sent back 2 time(s), home audited)",
                "- item 'rounds#1.items[1]': 0 review round(s), 0 home audit(s), classes 'c-frame' (0 site(s) consolidated, 0 deleted, 0 cell(s) pinned, 0 neighbour(s) measured, 0 unmeasured, sent back 0 time(s))",
                "- 'c-rate', reopened 2 time(s)",
                "- 'c-rate' home 'src/Rates/Home.cs', touched 6 time(s)",
                "- issue 'nx-1' a broken parser: fixed in a later round, by 'rounds#2.items[0]'",
                "- issue 'nx-2' a stale document: next round, not settled",
            ],
            SectionOf(report, "## Convergence"));

        // The report's document keeps each item's counts as numbers, each issue with the item that settled it, and every
        // row with an id of its own: its list, the item or agent that said it, and what it is of.
        var document = JsonNode.Parse(harness.FileSystem.ReadAllText(layout.ReportJsonFile))!["convergence"]!;
        var relaxed = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        Assert.Equal(
            """[{"id":"items:rounds#1.items[0]","item":"rounds#1.items[0]","reviewRounds":2,"homeAudits":1,"classes":[{"id":"items:rounds#1.items[0]:c-rate","class":"c-rate","sitesConsolidated":1,"sitesDeleted":2,"cellsPinned":4,"neighboursMeasured":2,"neighboursUnmeasured":1,"sendBacks":2,"homeAudit":true}]},{"id":"items:rounds#1.items[1]","item":"rounds#1.items[1]","reviewRounds":0,"homeAudits":0,"classes":[{"id":"items:rounds#1.items[1]:c-frame","class":"c-frame","sitesConsolidated":0,"sitesDeleted":0,"cellsPinned":0,"neighboursMeasured":0,"neighboursUnmeasured":0,"sendBacks":0,"homeAudit":false}]}]""",
            document["items"]!.ToJsonString(relaxed));
        Assert.Equal(
            """[{"id":"nextRound:nx-1","issue":"nx-1","said":"'nx-1' a broken parser: fixed in a later round, by 'rounds#2.items[0]'","settledBy":"rounds#2.items[0]"},{"id":"nextRound:nx-2","issue":"nx-2","said":"'nx-2' a stale document: next round, not settled","settledBy":null}]""",
            document["nextRound"]!.ToJsonString(relaxed));
        Assert.Equal(
            [
                "classesClosed:rounds#1.items[0]:c-rate",
                "classesClosed:rounds#1.items[1]:c-frame",
                "classesReopened:a1:c-rate",
                "hotspots:rounds#1.items[0]:c-rate:src/Rates/Home.cs",
            ],
            new[] { "classesClosed", "classesReopened", "hotspots" }.SelectMany(list => document[list]!.AsArray().Select(row => row!["id"]!.GetValue<string>())));
        Assert.Contains("\n- classReopenedTwice#1: 'c-rate' of agent 'a1' was reopened 2 times\n", report, StringComparison.Ordinal);
        Assert.DoesNotContain("c-plan", report, StringComparison.Ordinal);
        Assert.DoesNotContain("c-skip", report, StringComparison.Ordinal);
        Assert.DoesNotContain("an answer no check judged", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a run spent, a part of whose cost could not be counted, is said in the report, in <c>report.json</c> and in a
    /// command's <c>--json</c> as the dollars counted and why the rest is not known - never the counted sum as though it
    /// were the whole - and a run whose every part was counted holds no such member. The report says it among the run's
    /// own lines, with its four decimals and its currency.
    /// </summary>
    [Fact]
    public void WhatARunSpent_APartOfWhichWasNotCounted_IsSaidAsAtLeastWhatWas()
    {
        const string Why = "no price for 'glm-5.3' (providers 'z', model 'glm-5.3': priceOutput)";
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var spent = new FlowBudgetRecord(1.25, 1500, 2.5, Why);
        var file = new FlowFile(1, "r1", "flow", "digest", new Dictionary<string, string>(), "Done", spent, 0, [], [], DateTimeOffset.UnixEpoch);

        _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, file), "done", 0, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            [
                "- sessions: 0",
                $"- beganAt: {FlowTimeConverter.Written(DateTimeOffset.UnixEpoch)}",
                $"- budget: at least 1.2500 USD (the rest is not known: {Why}), 1500 tokens, 2.5 minutes of reasoning",
            ],
            SectionOf(harness.FileSystem.ReadAllText(layout.ReportFile), "## The run"));

        var written = JsonNode.Parse(harness.FileSystem.ReadAllText(layout.ReportJsonFile))!["theRun"]!["budget"]!;
        var answered = JsonNode.Parse(FlowReports.Document(new FlowRunResult("Done", "r1", "done", [], [], [], [], [], spent, 0, [])))!["spent"]!;
        var counted = JsonNode.Parse(FlowReports.Document(new FlowRunResult("Done", "r1", "done", [], [], [], [], [], spent with { Uncounted = null }, 0, [])))!["spent"]!;

        Assert.Equal(["usd", "tokens", "minutes", "uncounted"], written.AsObject().Select(member => member.Key));
        Assert.Equal((1.25, 1500L, 2.5, Why), (written["usd"]!.GetValue<double>(), written["tokens"]!.GetValue<long>(), written["minutes"]!.GetValue<double>(), written["uncounted"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(written, answered));
        Assert.Equal(["usd", "tokens", "minutes"], counted.AsObject().Select(member => member.Key));
    }

    /// <summary>
    /// The report the session's end writes is the run's report with the session's own members beside it in
    /// <c>report.json</c>: its line, its code, when it ended and every handoff the run's steps hold, by its step, who
    /// wrote it and its file.
    /// </summary>
    [Fact]
    public void TheSessionsReport_HoldsTheSessionsOwnMembers()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "r1");
        var ended = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var file = new FlowFile(
            1,
            "r1",
            "flow",
            "digest",
            new Dictionary<string, string>(),
            "Paused",
            new FlowBudgetRecord(0, 0, 0),
            0,
            [new FlowStepRecord("plan", "plan", false, "Paused", 1, 1, [], [], Type: "reason", Agent: "a1", Handoff: new FlowHandoffRecord("agent"))],
            [],
            DateTimeOffset.UnixEpoch);

        _ = FlowReports.Write(harness.FileSystem, new FlowRunReportSource(layout, file), "paused: plan handed off", 3, ended);

        var session = JsonNode.Parse(harness.FileSystem.ReadAllText(layout.ReportJsonFile))!["session"]!;

        Assert.True(
            JsonNode.DeepEquals(
                new JsonObject
                {
                    ["summary"] = "paused: plan handed off",
                    ["exitCode"] = 3,
                    ["endedAt"] = FlowTimeConverter.Written(ended),
                    ["handoffs"] = new JsonArray(new JsonObject { ["instance"] = "plan", ["by"] = "agent", ["file"] = layout.InstanceHandoffFile("plan", "a1") }),
                },
                session),
            session.ToJsonString());
    }

    /// <summary>The lines of the Markdown section under <paramref name="heading"/>, up to the blank line that ends it.</summary>
    private static List<string> SectionOf(string markdown, string heading)
        => [.. markdown.Replace("\r\n", "\n").Split('\n').SkipWhile(line => line != heading).Skip(2).TakeWhile(line => line.Length > 0)];
}
