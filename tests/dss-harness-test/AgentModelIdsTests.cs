using DssHarness.Core.Agents;

namespace DssHarness.Tests;

/// <summary>The one rule a message's model is held to on every client.</summary>
public sealed class AgentModelIdsTests
{
    /// <summary>A model's id with a date after it is that model, and another model is not.</summary>
    [Theory]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5", true)]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5-20261001", true)]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5-2026100", false)]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5-202610011", false)]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5-2026100a", false)]
    [InlineData("claude-sonnet-5-5", "claude-sonnet-5-5-preview", false)]
    [InlineData("claude-sonnet-5-5", "claude-opus-5-5", false)]
    public void AModelsIdWithADateAfterIt_IsThatModel(string asked, string named, bool same)
        => Assert.Equal(same, AgentModelIds.Same(asked, named));
}
