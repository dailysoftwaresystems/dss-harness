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
            FlowReports.LeftForAPerson(layout, file, questions: null));
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
            FlowReports.LeftForAPerson(layout, file, questions: null));
    }

    /// <summary>
    /// What a run spent, a part of whose cost could not be counted, is said in the report, in <c>report.json</c> and in a
    /// command's <c>--json</c> as the dollars counted and why the rest is not known - never the counted sum as though it
    /// were the whole - and a run whose every part was counted holds no such member.
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

        _ = FlowReports.Write(harness.FileSystem, layout, file, null, "done", 0, DateTimeOffset.UnixEpoch);

        var report = harness.FileSystem.ReadAllText(layout.ReportFile).Split('\n');
        Assert.Equal(
            "- at least 1.25 USD (the rest is not known: no price for 'glm-5.3' (providers 'z', model 'glm-5.3': priceOutput)), 1500 tokens, 2.5 minutes of reasoning",
            report[Array.IndexOf(report, "## Spent") + 1]);

        var written = JsonNode.Parse(harness.FileSystem.ReadAllText(layout.ReportJsonFile))!["spent"]!;
        var answered = JsonNode.Parse(FlowReports.Document(new FlowRunResult("Done", "r1", "done", [], [], [], [], [], spent, 0, [])))!["spent"]!;
        var counted = JsonNode.Parse(FlowReports.Document(new FlowRunResult("Done", "r1", "done", [], [], [], [], [], spent with { Uncounted = null }, 0, [])))!["spent"]!;

        Assert.Equal(["usd", "tokens", "minutes", "uncounted"], written.AsObject().Select(member => member.Key));
        Assert.Equal((1.25, 1500L, 2.5, Why), (written["usd"]!.GetValue<double>(), written["tokens"]!.GetValue<long>(), written["minutes"]!.GetValue<double>(), written["uncounted"]!.GetValue<string>()));
        Assert.True(JsonNode.DeepEquals(written, answered));
        Assert.Equal(["usd", "tokens", "minutes"], counted.AsObject().Select(member => member.Key));
    }
}
