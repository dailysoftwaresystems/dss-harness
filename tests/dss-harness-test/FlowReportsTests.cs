using System.Text.Json.Nodes;
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
}
