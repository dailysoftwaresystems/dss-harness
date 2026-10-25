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
}
