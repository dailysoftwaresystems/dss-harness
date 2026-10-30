using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;

namespace DssHarness.Tests;

/// <summary>
/// What the walk of a step's order records of its keys' spend, and the lane it holds (part 03's "A provider's keys,
/// and balancing them" and "Coordination"): each entry's cost recorded by the key's name - what the client reported
/// where it does, else what the engine counted - and the run's requests held to the client's parallel bound on one
/// provider and key, a 429's cool-down set from when the provider said the limit lifts.
/// </summary>
public sealed class AgentRunnerSpendTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every line a run's step recorded, as the sink the request carried took them.</summary>
    private sealed class Spent : IAgentSpendSink
    {
        public List<(string Provider, string? Key, string Model, decimal Cost)> Lines { get; } = [];

        public void Record(string provider, string? key, string model, decimal cost)
            => Lines.Add((provider, key, model, cost));
    }

    /// <summary>A client that works whatever its script says, remembering the run it was given.</summary>
    private sealed class Scripted(string name) : IAgentClient
    {
        public string Name { get; } = name;

        public AgentClientCapabilities Capabilities { get; } = new(
            Streaming: true,
            Thinking: true,
            WritableWhileWorking: true,
            AgentConversationHandling.Rendered,
            SchemaEnforcedOutput: false,
            AgentToolsOwner.Harness,
            AgentToolsOwner.Harness,
            ReportsCost: false,
            ["low", "high"],
            ProviderType: "none",
            ToldModelBy: "the test");

        public Func<AgentRun, IAgentEventSink, CancellationToken, Task<AgentRunResult>> Script { get; init; }
            = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done"));

        public Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
            => Script(run, events, cancellationToken);

        public async IAsyncEnumerable<AgentEvent> Stream(AgentStepRequest step, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = await Script(null!, null!, cancellationToken);
            yield break;
        }
    }

    /// <summary>One entry of the order, priced, so the engine can count what it used.</summary>
    private static AgentRunEntry Entry(string model = "claude-opus-5-5", string key = "WORK_KEY", string client = "claude-api")
        => new(model, "anthropic", client, key, null, Usable: true)
        {
            Entries = new(
                new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Currency = "USD" },
                new AgentModelConfig { Code = model, ContextTokens = 200000, MaxOutputTokens = 32000, PriceInputMiss = 5, PriceInputHit = 1, PriceOutput = 25 },
                null),
        };

    /// <summary>A step whose order and spend sink a test names, over a temporary directory.</summary>
    private static AgentStepRequest Step(IReadOnlyList<AgentRunEntry> order, Spent spent, TempDirectory temp)
        => new(
            "r1",
            "a1",
            "s1",
            Attempt: 1,
            AgentDefinition: null,
            Provider: null,
            Client: null,
            Tier: null,
            Model: order[0].Model,
            Effort: null,
            Fallback: null,
            SystemPrompt: "be terse",
            PromptTemplate: "do it",
            new Dictionary<string, string>(StringComparer.Ordinal),
            ResumeTemplate: null,
            new AgentWorkspace(null, temp.Path, temp.Path, [], temp.Path, temp.Path),
            Output: null,
            Conversation: null,
            new AgentLimits(10, null, null, null),
            Mask: null)
        {
            Order = order,
            Spend = spent,
        };

    /// <summary>What a run used, at prices that make its cost plain.</summary>
    private static AgentUsageReport Used(long input, long output)
        => new(
            new AgentUsage(input, output, 0, 0, 0, 0),
            [new AgentModelUsage("claude-opus-5-5", new AgentUsage(input, output, 0, 0, 0, 0))],
            new AgentCost(Reported: null, Counted: 0.02m));

    /// <summary>Each entry's run records what its key spent by the key's name: what the client reported where it does, else what the engine counted; a run no price counted records nothing, and a login records nothing.</summary>
    [Fact]
    public async Task EachEntrysRun_RecordsWhatItsKeySpent_ByTheKeysName()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var spent = new Spent();
        var counted = new Scripted("one")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Usage: Used(100, 50)) { DeclinedFor = "the request" }),
        };
        var reported = new Scripted("two")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(
                AgentRunStatus.Refused,
                Usage: Used(100, 50) with { Cost = new AgentCost(Reported: 0.07m, Counted: 0.02m) })
            {
                DeclinedFor = "the request",
            }),
        };
        var done = new Scripted("three") { Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Usage: Used(100, 50) with { Cost = null })) };
        var runner = new AgentRunner([counted, reported, done], harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(
            Step([Entry(client: "one"), Entry(model: "claude-fable-5-1", key: "SPARE_KEY", client: "two"), Entry(model: "claude-haiku-5-3", key: "THIRD_KEY", client: "three")], spent, temp),
            events: null,
            Token);

        Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Error}");
        Assert.Equal(
            [("anthropic", "WORK_KEY", "claude-opus-5-5", 0.02m), ("anthropic", "SPARE_KEY", "claude-fable-5-1", 0.07m)],
            spent.Lines);
    }

    /// <summary>
    /// The run's requests go through the lane of its provider and key: the lane held while the client works - a second
    /// request of it waits - and freed when the run is done, and a 429's cool-down holding every later request of the
    /// lane until the provider said the limit lifts.
    /// </summary>
    [Fact]
    public async Task TheRunsRequests_GoThroughTheLaneOfItsProviderAndKey()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new ManualClock();
        var waits = new List<TimeSpan>();

        // The wait a spent bucket or a cool-down waits out on the standing clock: it never passes on its own, so a
        // request the cool-down holds is held for good until the test says otherwise.
        var gates = new ClientGates(clock, async (wait, cancellationToken) =>
        {
            waits.Add(wait);
            await Task.Delay(wait, clock, cancellationToken);
        });

        // The lane of the run's provider and key, bound to one at a time: made before the run, so the run's own ask
        // of it finds the bound the test wants rather than the client's built-in.
        var lane = gates.Lane("anthropic", DssHarness.Core.FileSystem.FileContentHash.Of(System.Text.Encoding.UTF8.GetBytes("WORK_KEY")), new ClientGateSettings(MaxParallel: 1));
        var working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Scripted("claude-api")
        {
            Script = async (_, _, _) =>
            {
                started.SetResult();
                await working.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
                return new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Usage: Used(100, 50));
            },
        };
        var runner = new AgentRunner([client], harness.FileSystem, Elevation.User, clock, gates);
        var spent = new Spent();
        var run = runner.RunAsync(Step([Entry()], spent, temp), events: null, Token);

        // The lane is held while the client works - the run's own request inside it - so a second request of the same
        // provider and key waits for the run to be done. The lane is the key's own: the entry's credential holds the
        // value, and the runner hashes it, so the test asks by the same name the walk sent.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var second = lane.EnterAsync(inputTokens: 0, Token);

        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromMilliseconds(200), Token));
        Assert.Empty(waits);

        working.SetResult();
        _ = await run;

        // The lane is freed when the run is done: the second request is let in.
        await second.WaitAsync(TimeSpan.FromSeconds(5), Token);
        second.Dispose();
    }

    /// <summary>A run that ended at its account's limit, with no fallback left, sets its lane's cool-down from when the provider said the limit lifts: a later request of the lane waits it out.</summary>
    [Fact]
    public async Task ARunThatEndedAtItsAccountsLimit_SetsTheLanesCoolDown()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new ManualClock();
        var waits = new List<TimeSpan>();

        var gates = new ClientGates(clock, async (wait, cancellationToken) =>
        {
            waits.Add(wait);
            await Task.Delay(wait, clock, cancellationToken);
        });

        var lane = gates.Lane("anthropic", DssHarness.Core.FileSystem.FileContentHash.Of(System.Text.Encoding.UTF8.GetBytes("WORK_KEY")), new ClientGateSettings(MaxParallel: 1));
        var lifts = clock.GetUtcNow() + TimeSpan.FromMinutes(2);
        var client = new Scripted("claude-api")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.RateLimited, Error: "the account is at its limit")
            {
                Limit = new AgentAccountLimit(BalanceSpent: false, lifts, Key: "WORK_KEY"),
            }),
        };
        var runner = new AgentRunner([client], harness.FileSystem, Elevation.User, clock, gates);

        var result = await runner.RunAsync(Step([Entry()], new Spent(), temp), events: null, Token);

        Assert.Equal(AgentRunStatus.RateLimited, result.Status);

        clock.Advance(TimeSpan.FromMinutes(1));

        var later = lane.EnterAsync(inputTokens: 0, Token);

        await Assert.ThrowsAsync<TimeoutException>(() => later.WaitAsync(TimeSpan.FromMilliseconds(200), Token));
        Assert.Contains(waits, wait => wait > TimeSpan.Zero);
    }
}
