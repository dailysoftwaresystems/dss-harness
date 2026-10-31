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

    /// <summary>One entry of the order, priced, so the engine can count what it used, signing in with its key's value <c>sk-</c> and the key's name.</summary>
    private static AgentRunEntry Entry(string model = "claude-opus-5-5", string key = "WORK_KEY", string client = "claude-api")
        => new(model, "anthropic", client, key, null, Usable: true)
        {
            Entries = new(
                new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Currency = "USD" },
                new AgentModelConfig { Code = model, ContextTokens = 200000, MaxOutputTokens = 32000, PriceInputMiss = 5, PriceInputHit = 1, PriceOutput = 25 },
                null),
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey(key, $"sk-{key}")]),
        };

    /// <summary>
    /// The lane the key named <paramref name="key"/> signs a request into, found as the account is - by a hash of the
    /// key's value, never its name - so a test that holds it holds the lane the runner enters.
    /// </summary>
    private static ClientGate LaneOfTheKey(ClientGates gates, string key)
        => gates.Lane("anthropic", DssHarness.Core.FileSystem.FileContentHash.Of(System.Text.Encoding.UTF8.GetBytes($"sk-{key}")), new ClientGateSettings(MaxParallel: 1));

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
        var lane = LaneOfTheKey(gates, "WORK_KEY");
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

        // The lane is held while the client works - a client that does not pace its own requests is held in it for the
        // whole run - so a second request of the same provider and key waits for the run to be done. The lane is the
        // key's own: the entry's credential holds the value, and the runner hashes the value, never the name.
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

        var lane = LaneOfTheKey(gates, "WORK_KEY");
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

        // Two minutes from the run's end, one of them passed: the minute left is what the later request waits.
        await Assert.ThrowsAsync<TimeoutException>(() => later.WaitAsync(TimeSpan.FromMilliseconds(200), Token));
        Assert.Equal([TimeSpan.FromMinutes(1)], waits);
    }

    /// <summary>
    /// What the walk cost is every entry's own cost summed: a client's reported number - a Claude Code launch's - kept, an
    /// entry its client reports nothing of counted in at the engine's count, never dropped and never read as zero.
    /// </summary>
    [Fact]
    public async Task TheWalksCost_IsEveryEntrysOwn_TheReportedKept()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var reported = new Scripted("one")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Usage: Used(100, 50) with { Cost = new AgentCost(Reported: 1.80m) }) { DeclinedFor = "the request" }),
        };
        var counted = new Scripted("two") { Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Usage: Used(100, 50))) };
        var runner = new AgentRunner([reported, counted], harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(Step([Entry(client: "one"), Entry(model: "claude-fable-5-1", key: "SPARE_KEY", client: "two")], new Spent(), temp), events: null, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(new AgentCost(Reported: 1.82m, Counted: null), result.Usage!.Cost);
        Assert.Equal(1.82m, result.Usage.Cost!.Usd);

        // The one entry's own: what the client said, as it said it.
        var alone = await new AgentRunner([reported], harness.FileSystem, Elevation.User, TimeProvider.System)
            .RunAsync(Step([Entry(client: "one")], new Spent(), temp), events: null, Token);

        Assert.Equal(new AgentCost(Reported: 1.80m), alone.Usage!.Cost);
    }

    /// <summary>
    /// A walk where one entry's cost is known by neither the client nor the engine has an unknown cost, never zero, the
    /// price that was missing named.
    /// </summary>
    [Fact]
    public async Task AWalkWithAnEntryOfUnknownCost_HasAnUnknownCost()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var unpriced = new Scripted("one")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Usage: Used(100, 50) with { Cost = new AgentCost(MissingPrice: "claude-opus-5-5's priceOutput") }) { DeclinedFor = "the request" }),
        };
        var counted = new Scripted("two") { Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Usage: Used(100, 50))) };
        var runner = new AgentRunner([unpriced, counted], harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(Step([Entry(client: "one"), Entry(model: "claude-fable-5-1", key: "SPARE_KEY", client: "two")], new Spent(), temp), events: null, Token);

        Assert.Equal(new AgentCost(Reported: null, Counted: null, MissingPrice: "claude-opus-5-5's priceOutput"), result.Usage!.Cost);
        Assert.Null(result.Usage.Cost!.Usd);
    }

    /// <summary>
    /// What a run spent that cannot be recorded for the balancing of keys - its section held past its wait, the disk full -
    /// is said as a notice, and the step ends as it ended: the record is for choosing a key, never part of the work.
    /// </summary>
    [Fact]
    public async Task ASpendThatCannotBeRecorded_IsSaid_AndNeverEndsTheStep()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var done = new Scripted("one") { Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Usage: Used(100, 50))) };
        var runner = new AgentRunner([done], harness.FileSystem, Elevation.User, TimeProvider.System);
        var seen = new List<AgentEvent>();

        var result = await runner.RunAsync(Step([Entry(client: "one")], new Spent(), temp) with { Spend = new Unwritable() }, new Collecting(seen), Token);

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
        Assert.Equal(
            $"What WORK_KEY spent on 'claude-opus-5-5' could not be recorded for the balancing of keys: {Unwritable.Why}",
            Assert.Single(seen.OfType<Notice>()).Text);
    }

    /// <summary>
    /// A launch waiting its turn at the gate says so - the wait and when it ends - and the run's pause ends the wait: the
    /// launch never began, so the run hands off nothing, saying why, and its client is never asked.
    /// </summary>
    [Fact]
    public async Task ALaunchWaitingAtTheGate_IsSaid_AndThePauseEndsIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new ManualClock();
        var gates = new ClientGates(clock, (wait, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        LaneOfTheKey(gates, "WORK_KEY").RateLimited(TimeSpan.FromHours(2));

        var asked = 0;
        var client = new Scripted("claude-api") { Script = (_, _, _) => { asked++; return Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done")); } };
        var runner = new AgentRunner([client], harness.FileSystem, Elevation.User, clock, gates);
        var handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30));
        var waiting = new TaskCompletionSource<WaitingToRetry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<AgentEvent>();

        var run = runner.RunAsync(Step([Entry()], new Spent(), temp) with { Handoff = handoff }, new Collecting(seen, waiting), Token);
        var wait = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(
            ($"the run waits for {ClientGate.CoolDown}", TimeSpan.FromHours(2), clock.GetUtcNow() + TimeSpan.FromHours(2)),
            (wait.Cause, wait.Delay, wait.Until));

        handoff.Ask();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal((AgentRunStatus.HandedOff, AgentRunner.PausedAtTheGate), (result.Status, result.Handoff!.WhyNone));
        Assert.Equal(0, asked);
    }

    /// <summary>A recorder whose every line fails as a section held past its wait does.</summary>
    private sealed class Unwritable : IAgentSpendSink
    {
        public const string Why = "the section across runs is held past its wait";

        public void Record(string provider, string? key, string model, decimal cost)
            => throw new DssHarness.Core.Results.HarnessException(DssHarness.Core.Results.HarnessExit.CommandFailed, Why);
    }

    /// <summary>A sink that collects what it is given, telling the first wait it sees.</summary>
    private sealed class Collecting(List<AgentEvent> seen, TaskCompletionSource<WaitingToRetry>? waiting = null) : IAgentEventSink
    {
        public ValueTask EmitAsync(AgentEvent @event)
        {
            lock (seen)
            {
                seen.Add(@event);
            }

            if (@event is WaitingToRetry wait)
            {
                waiting?.TrySetResult(wait);
            }

            return ValueTask.CompletedTask;
        }
    }
}
