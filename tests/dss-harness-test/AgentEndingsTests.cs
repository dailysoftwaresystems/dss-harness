using System.Text.Json.Nodes;
using DssHarness.Core.Agents;
using DssHarness.Core.Orchestration;

namespace DssHarness.Tests;

/// <summary>
/// The endings, the handoff's sentences and the usage every client says alike: each ending in its one sentence, masked,
/// with what it carries beside it, and a run's usage counted by model.
/// </summary>
public sealed class AgentEndingsTests
{
    private static readonly Masking Mask = new("sk-1");

    /// <summary>Each ending is said in its one sentence, masked, carrying what it carries.</summary>
    [Fact]
    public void EachEnding_IsSaidInItsOneSentence_Masked()
    {
        var never = AgentEndings.NotStarted("claude-api", AgentNotStartedBecause.KeyMissing, "the slot holds sk-1", "m1", Mask);
        Assert.Equal(
            (AgentRunStatus.NotStarted, AgentNotStartedBecause.KeyMissing, "m1", "client 'claude-api' was not started: the slot holds ***"),
            (never.Status, never.NotStartedBecause, never.Model, never.Error));

        var ended = AgentEndings.Ended(AgentRunStatus.Failed, "boom sk-1", Mask);
        Assert.Equal((AgentRunStatus.Failed, "boom ***"), (ended.Status, ended.Error));

        var wrong = AgentEndings.WrongModel("m1", "m2 sk-1", "its turn 2", Mask);
        Assert.Equal(
            (AgentRunStatus.WrongModel, "the step asked for 'm1' and its turn 2 was written by 'm2 ***'; a step's model is its configuration's and the engine's to choose, and neither chose this one"),
            (wrong.Status, wrong.Error));

        var invalid = AgentEndings.InvalidOutput(2, ["one", "two sk-1"], Mask);
        Assert.Equal((AgentRunStatus.InvalidOutput, "the answer did not read against its schema after 2 repairs: one; two ***"), (invalid.Status, invalid.Error));

        var declined = AgentEndings.Declined("cyber", "no sk-1", Mask);
        Assert.Equal((AgentRunStatus.Refused, "the model declined the request (cyber): no ***", "cyber"), (declined.Status, declined.Error, declined.DeclinedFor));
        Assert.Equal(("the model declined the request: no", null), (AgentEndings.Declined(null, "no", Mask).Error, AgentEndings.Declined(string.Empty, "no", Mask).DeclinedFor));
    }

    /// <summary>An account at its limit says who reported it, whether its balance or an allowance is spent and when it lifts, and carries the limit.</summary>
    [Fact]
    public void AnAccountAtItsLimit_SaysWhatIsSpent_AndWhenItLifts()
    {
        var lifts = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var spent = AgentEndings.AtLimit("the provider", new AgentAccountLimit(BalanceSpent: true, lifts), "spent sk-1", Mask);

        Assert.Equal(
            (AgentRunStatus.RateLimited, "the provider reported the account at its limit, its balance or included usage spent until 2026-10-26 12:00:00Z: spent ***"),
            (spent.Status, spent.Error));
        Assert.Equal(new AgentAccountLimit(true, lifts), spent.Limit);
        Assert.Equal(
            "Claude Code reported the account at its limit, an allowance used up: five_hour",
            AgentEndings.AtLimit("Claude Code", new AgentAccountLimit(BalanceSpent: false), "five_hour", Mask).Error);
    }

    /// <summary>A run's usage is counted in all and by model, its first model serving from the turn it began on.</summary>
    [Fact]
    public void ARunsUsage_IsCountedInAllAndByModel()
    {
        var tally = new AgentUsageTally();

        tally.Add("m1", new AgentUsage(1, 2, 0, 0, 0, 0), 1);
        tally.Add("m2", new AgentUsage(10, 20, 0, 0, 0, 0), 3);
        tally.Add("m1", new AgentUsage(100, 200, 0, 0, 0, 0), 4);

        Assert.Equal(new AgentUsage(111, 222, 0, 0, 0, 0), tally.Total);
        Assert.Equal([new AgentServedModel("m1", 1)], tally.Served);

        var report = tally.Report(new AgentCost(Counted: 1.5m));

        Assert.Equal([new AgentModelUsage("m1", new AgentUsage(101, 202, 0, 0, 0, 0)), new AgentModelUsage("m2", new AgentUsage(10, 20, 0, 0, 0, 0))], report.PerModel);
        Assert.Equal((new AgentUsage(111, 222, 0, 0, 0, 0), 1.5m), (report.Total, report.Cost!.Counted));
    }

    /// <summary>A handoff, or why there is none, is masked, and told as the event that ends the handoff turn.</summary>
    [Fact]
    public void AHandoff_IsMasked_AndToldAsTheEventThatEndsItsTurn()
    {
        var handed = AgentEndings.HandedOff("done sk-1", null, Mask);
        Assert.Equal((AgentRunStatus.HandedOff, new AgentHandoff("done ***")), (handed.Status, handed.Handoff));
        Assert.Equal(new HandoffAnswered(true), HandoffAnswered.Of(handed.Handoff!));

        var none = AgentEndings.HandedOff(null, "the bound passed sk-1", Mask);
        Assert.Equal(new AgentHandoff(WhyNone: "the bound passed ***"), none.Handoff);
        Assert.Equal(new HandoffAnswered(false, "the bound passed ***"), HandoffAnswered.Of(none.Handoff!));
    }

    /// <summary>A key refused is a fallback to the same model of the same provider and client, on the next key, each named.</summary>
    [Fact]
    public void ARefusedKey_IsAFallbackToTheSameModel_OnTheNextKey()
        => Assert.Equal(
            new FallbackStarted("m1", "anthropic", "claude-api", "m1", "anthropic", "claude-api", "key refused: the provider refused WORK_KEY", "the request sent again", 3, ThrewAway: true, CreditAsked: false, "WORK_KEY", "SPARE_KEY"),
            FallbackStarted.KeyRefused("m1", "anthropic", "claude-api", "WORK_KEY", "SPARE_KEY", "the provider refused WORK_KEY", "the request sent again", 3, threwAway: true));

    /// <summary>The handoff's question, its answer and why there is none are one for every client.</summary>
    [Fact]
    public void TheHandoffsSentences_AreOneForEveryClient()
    {
        Assert.Equal(AgentHandoffSignal.Prompt, AgentHandoffSignal.Question(null));
        Assert.Equal($"Your work item was withdrawn.\n\n{AgentHandoffSignal.Prompt}", AgentHandoffSignal.Question("Your work item was withdrawn."));
        Assert.Equal("the agent gave no handoff in 3 answers", AgentHandoffSignal.NoneIn(3));
        Assert.Equal("the handoff turn ended Failed: boom", AgentHandoffSignal.TurnEnded(AgentRunStatus.Failed, "boom"));

        Assert.Equal("done", AgentHandoffSignal.AnswerOf(JsonNode.Parse("""{"notes":"done","left":"x"}"""), "the text"));
        Assert.Equal("""{"left":"x"}""", AgentHandoffSignal.AnswerOf(JsonNode.Parse("""{"left":"x"}"""), "the text"));
        Assert.Equal("the text", AgentHandoffSignal.AnswerOf(null, "the text"));
        Assert.Null(AgentHandoffSignal.AnswerOf(JsonNode.Parse("""{"notes":"  "}"""), "the text"));
        Assert.Null(AgentHandoffSignal.AnswerOf(null, " \n"));
        Assert.Null(AgentHandoffSignal.AnswerOf(null, null));
    }

    /// <summary>A conversation's directory is named by its id alone: an id that would name another directory is refused.</summary>
    [Theory]
    [InlineData("conv-1_A", true)]
    [InlineData("../other", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void AConversationsDirectory_IsNamedByItsIdAlone(string id, bool named)
    {
        var sessions = Path.Combine(Path.GetTempPath(), "sessions");

        Assert.Equal(named, OrchestratorLayout.IsSessionId(id));

        if (named)
        {
            Assert.Equal(Path.Combine(sessions, id), OrchestratorLayout.SessionDirectoryIn(sessions, id));
        }
        else
        {
            Assert.Equal($"'{id}' is no conversation's id: {OrchestratorLayout.SessionIdShape}. (Parameter 'id')", Assert.Throws<ArgumentException>(() => OrchestratorLayout.SessionDirectoryIn(sessions, id)).Message);
        }
    }
}
