using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;

namespace DssHarness.Tests;

/// <summary>
/// The gate (part 03, "Coordination"): one lane per provider and key - a client that signs in with a login one lane by
/// its client - holding the client's <c>maxParallel</c> (the built-in's where its entry says none), the optional
/// buckets that pace a minute's requests, input and output tokens, and a cool-down a 429 sets for every waiter. The
/// engine's step runner holds the lane a run's requests go through; here the lane is held over a clock and a wait that
/// move only when told.
/// </summary>
public sealed class ClientGateTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The built-in parallel bounds, each at its own where the entry says none; an entry's own standing.</summary>
    [Fact]
    public void TheBuiltInBoundsHoldWhereAnEntrySaysNone()
    {
        Assert.Equal(3, ClientGateSettings.Of(ClaudeCodeClientName(), null).MaxParallel);
        Assert.Equal(4, ClientGateSettings.Of("claude-api", null).MaxParallel);
        Assert.Equal(2, ClientGateSettings.Of("glm", null).MaxParallel);
        Assert.Null(ClientGateSettings.Of("glm", null).RequestsPerMinute);

        var set = ClientGateSettings.Of("glm", new AgentClientConfig { MaxParallel = 6, RequestsPerMinute = 30, InputTokensPerMinute = 100_000, OutputTokensPerMinute = 20_000 });

        Assert.Equal(new ClientGateSettings(6, 30, 100_000, 20_000), set);
    }

    /// <summary>A lane holds its client's parallel bound: a request past it waits for a slot to be released, another lane's its own.</summary>
    [Fact]
    public async Task AParallelBoundIsHeldToTheSlotsOfOneLane()
    {
        var (gates, _, _) = Gates();
        var lane = gates.Lane("anthropic", "hash-of-key-a", new ClientGateSettings(MaxParallel: 1));
        var other = gates.Lane("anthropic", "hash-of-key-b", new ClientGateSettings(MaxParallel: 1));

        var first = await lane.EnterAsync(0, TestContext.Current.CancellationToken);
        var pending = lane.EnterAsync(0, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);

        // The lane's bound is the lane's alone: another key of the provider runs beside it.
        using (await other.EnterAsync(0, TestContext.Current.CancellationToken))
        {
            Assert.False(pending.IsCompleted);
        }

        first.Dispose();
        using var second = await pending.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    /// <summary>A minute's requests are waited past: the third of two waits for the oldest of the minute to be a minute old.</summary>
    [Fact]
    public async Task ARequestsBucketIsWaitedPast()
    {
        var (gates, clock, waited) = Gates();
        var lane = gates.Lane("zai", "hash-of-key", new ClientGateSettings(4, RequestsPerMinute: 2));

        using var first = await lane.EnterAsync(0, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(20));
        using var second = await lane.EnterAsync(0, TestContext.Current.CancellationToken);
        Assert.Empty(waited);

        using var third = await lane.EnterAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(40)], waited);
        Assert.Equal(At.AddSeconds(60), clock.GetUtcNow());
    }

    /// <summary>A request's own input tokens are counted against its bucket as it enters; a later one waits for the oldest to age out.</summary>
    [Fact]
    public async Task AnInputBucketCountsTheRequestsOwn()
    {
        var (gates, clock, waited) = Gates();
        var lane = gates.Lane("anthropic", "hash-of-key", new ClientGateSettings(4, InputTokensPerMinute: 100));

        using var first = await lane.EnterAsync(60, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        using var second = await lane.EnterAsync(60, TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(30)], waited);
        Assert.Equal(At.AddSeconds(60), clock.GetUtcNow());
    }

    /// <summary>An answer's output tokens are charged once they are had: a lane whose last minute answered its fill waits for the oldest to age out.</summary>
    [Fact]
    public async Task AnOutputBucketIsChargedByTheAnswer()
    {
        var (gates, clock, waited) = Gates();
        var lane = gates.Lane("anthropic", "hash-of-key", new ClientGateSettings(4, OutputTokensPerMinute: 100));

        var first = await lane.EnterAsync(0, TestContext.Current.CancellationToken);
        first.Answered(80);
        first.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));

        var second = await lane.EnterAsync(0, TestContext.Current.CancellationToken);
        Assert.Empty(waited);

        second.Answered(60);
        second.Dispose();
        clock.Advance(TimeSpan.FromSeconds(10));

        using var third = await lane.EnterAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(40)], waited);
        Assert.Equal(At.AddSeconds(60), clock.GetUtcNow());
    }

    /// <summary>A 429's cool-down holds every waiter of the lane, a later or longer one holding past a sooner one.</summary>
    [Fact]
    public async Task ACoolDownA429Sets_HoldsEveryWaiter()
    {
        var (gates, clock, waited) = Gates();
        var lane = gates.Lane("anthropic", "hash-of-key", new ClientGateSettings(2));

        lane.RateLimited(TimeSpan.FromSeconds(30));
        lane.RateLimited(TimeSpan.FromSeconds(10));

        using var first = await lane.EnterAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(30)], waited);
        Assert.Equal(At.AddSeconds(30), clock.GetUtcNow());

        // A sooner cool-down never shortens the one in force; a lane is free the moment it lifts.
        lane.RateLimited(TimeSpan.Zero);
        using var second = await lane.EnterAsync(0, TestContext.Current.CancellationToken);
        Assert.Single(waited);
    }

    /// <summary>One registry, one lane per provider and key: the same two again answer the same lane, another key its own.</summary>
    [Fact]
    public void TheRegistryLanesByProviderAndKey()
    {
        var (gates, _, _) = Gates();

        Assert.Same(gates.Lane("anthropic", "hash-of-key-a", Settings), gates.Lane("anthropic", "hash-of-key-a", Settings));
        Assert.NotSame(gates.Lane("anthropic", "hash-of-key-a", Settings), gates.Lane("anthropic", "hash-of-key-b", Settings));
        Assert.NotSame(gates.Lane("anthropic", "hash-of-key-a", Settings), gates.Lane("zai", "hash-of-key-a", Settings));

        // A client that signs in with a login is one lane by its client, whatever key would be hashed for another.
        Assert.Same(gates.Lane("anthropic", ClaudeCodeClientName(), Settings), gates.Lane("anthropic", ClaudeCodeClientName(), Settings));
    }

    /// <summary>The client's bounds are held to what they can be, and the buckets to the API clients, where the configuration is read.</summary>
    [Fact]
    public void TheClientsBoundsAreHeldToWhatTheyCanBe()
    {
        var (_, problems) = AgentProviders.Resolve(Config(
            new()
            {
                ["claude-code"] = new AgentClientConfig { MaxParallel = 0, RequestsPerMinute = 30 },
                ["glm"] = new AgentClientConfig { InputTokensPerMinute = 0 },
            }));

        Assert.Equal(
            [
                "agentClients 'claude-code' names requestsPerMinute, a key of the API clients alone (claude-api, glm): at most this many requests in any minute on one provider and key",
                "agentClients 'claude-code' maxParallel is 0; the runs of one provider and key under way at once is one or more",
                "agentClients 'glm' inputTokensPerMinute is 0; a minute's bucket is one or more, or left out",
            ],
            problems);
    }

    /// <summary>The gates, the clock they count by and the waits they waited, each wait moving the clock by what it waited.</summary>
    private static (ClientGates Gates, ManualClock Clock, List<TimeSpan> Waited) Gates()
    {
        var clock = new ManualClock(At);
        var waited = new List<TimeSpan>();

        return (new ClientGates(clock, (wait, _) =>
        {
            waited.Add(wait);
            clock.Advance(wait);
            return Task.CompletedTask;
        }), clock, waited);
    }

    private static ClientGateSettings Settings => new(2);

    private static string ClaudeCodeClientName() => DssHarness.Core.Agents.ClaudeCode.ClaudeCodeClient.ClientName;

    /// <summary>A config over both providers whose client entries are <paramref name="clients"/>, as the provider tests build one.</summary>
    private static HarnessConfig Config(Dictionary<string, AgentClientConfig> clients)
        => new()
        {
            Defaults = new HarnessDefaults { AgentTier = "balanced" },
            Providers =
            [
                new AgentProviderConfig
                {
                    Name = "anthropic",
                    Type = "anthropic",
                    Api = "https://api.anthropic.com",
                    Client = "claude-api",
                    ApiKeyNames = ["ANTHROPIC_API_KEY"],
                    Currency = "USD",
                    ReadOn = "2026-10-12",
                    Models = [new AgentModelConfig { Code = "claude-sonnet-5-5", Name = "Claude Sonnet 5.5", Efforts = ["low", "high"], Tiers = ["balanced"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 }],
                },
                new AgentProviderConfig
                {
                    Name = "zai",
                    Type = "zai",
                    Api = "https://api.z.ai",
                    Client = "glm",
                    ApiKeyNames = ["ZAI_API_KEY"],
                    Currency = "USD",
                    ReadOn = "2026-10-12",
                    Models = [new AgentModelConfig { Code = "glm-5.3", Name = "GLM 5.3", Efforts = ["low", "high"], Tiers = ["balanced"], ContextTokens = 1_000_000, MaxOutputTokens = 131_072 }],
                },
            ],
            TierConfig = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["balanced"] = ["claude-sonnet-5-5", "glm-5.3"],
            },
            AgentClients = clients,
        };
}
