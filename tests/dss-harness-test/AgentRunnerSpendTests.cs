using DssHarness.Core.Agents;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;

namespace DssHarness.Tests;

/// <summary>
/// What the walk of a step's order records of its keys' spend, and the lane it holds: each entry's cost recorded by the
/// key's name - what the client reported where it does, else what the engine counted - and the run's requests held to
/// the client's parallel bound on one provider and key, a 429's cool-down set from when the provider said the limit
/// lifts.
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

    /// <summary>Starts no program: each it is asked to is counted and refused, so a client launched where none may be fails at once.</summary>
    private sealed class NoProcesses : DssHarness.Core.Processes.IProcessRunner
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public Task<DssHarness.Core.Processes.ProcessResult> RunAsync(DssHarness.Core.Processes.ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asked);
            throw new InvalidOperationException($"'{request.FileName}' was started, and no program may be");
        }

        public string? FindExecutable(string command) => command;
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
            Prompt: "do it",
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

    /// <summary>
    /// What a run used, at prices that make its cost plain: one part, of <paramref name="model"/> on <paramref name="key"/>,
    /// costing <paramref name="cost"/> - the engine's count of two cents where none is named - which is the whole's too.
    /// </summary>
    private static AgentUsageReport Used(long input, long output, string model = "claude-opus-5-5", string? key = "WORK_KEY", AgentCost? cost = null)
    {
        var priced = cost ?? new AgentCost(Reported: null, Counted: 0.02m);

        return new(
            new AgentUsage(input, output, 0, 0, 0, 0),
            [new AgentModelUsage(model, new AgentUsage(input, output, 0, 0, 0, 0), key, priced)],
            priced);
    }

    /// <summary>
    /// Each part of each entry's run records what it spent under the name of the key its requests were signed with: what
    /// the client reported where it does, else what the engine counted; a part no price counted records nothing, and a
    /// part signed with a login records nothing.
    /// </summary>
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
                Usage: Used(100, 50, "claude-fable-5-1", "SPARE_KEY", new AgentCost(Reported: 0.07m, Counted: 0.02m)))
            {
                DeclinedFor = "the request",
            }),
        };
        var login = new Scripted("three")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Usage: Used(100, 50, "claude-sonnet-5-0", key: null)) { DeclinedFor = "the request" }),
        };
        var done = new Scripted("four") { Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Usage: Used(100, 50, "claude-haiku-5-3", "THIRD_KEY", AgentCost.Unknown))) };
        var runner = new AgentRunner([counted, reported, login, done], harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(
            Step(
                [
                    Entry(client: "one"),
                    Entry(model: "claude-fable-5-1", key: "SPARE_KEY", client: "two"),
                    Entry(model: "claude-sonnet-5-0", key: "FOURTH_KEY", client: "three"),
                    Entry(model: "claude-haiku-5-3", key: "THIRD_KEY", client: "four"),
                ],
                spent,
                temp),
            events: null,
            Token);

        Assert.True(result.Status == AgentRunStatus.Completed, $"{result.Status}: {result.Error}");
        Assert.Equal(
            [("anthropic", "WORK_KEY", "claude-opus-5-5", 0.02m), ("anthropic", "SPARE_KEY", "claude-fable-5-1", 0.07m)],
            spent.Lines);
    }

    /// <summary>
    /// A run whose client moved through its model's keys by itself books each key's part to that key - never the whole to
    /// the key the run began on, which would have the balance choose the key that spent first.
    /// </summary>
    [Fact]
    public async Task ARunOverTwoKeys_BooksEachKeysPartToThatKey()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var spent = new Spent();
        var moved = new Scripted("one")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(
                AgentRunStatus.Completed,
                FinalText: "done",
                Usage: new AgentUsageReport(
                    new AgentUsage(300, 30, 0, 0, 0, 0),
                    [
                        new AgentModelUsage("claude-opus-5-5", new AgentUsage(100, 10, 0, 0, 0, 0), "WORK_KEY", new AgentCost(Counted: 0.01m)),
                        new AgentModelUsage("claude-opus-5-5", new AgentUsage(200, 20, 0, 0, 0, 0), "SPARE_KEY", new AgentCost(Counted: 5m)),
                    ],
                    new AgentCost(Counted: 5.01m)))),
        };
        var runner = new AgentRunner([moved], harness.FileSystem, Elevation.User, TimeProvider.System);

        _ = await runner.RunAsync(Step([Entry(client: "one"), Entry(key: "SPARE_KEY", client: "one")], spent, temp), events: null, Token);

        Assert.Equal([("anthropic", "WORK_KEY", "claude-opus-5-5", 0.01m), ("anthropic", "SPARE_KEY", "claude-opus-5-5", 5m)], spent.Lines);
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
    /// A launch that ended at one model's limit holds that model's launches on the lane alone: another model of the same
    /// key is free to go at once.
    /// </summary>
    [Fact]
    public async Task ALaunchAtAModelsLimit_HoldsThatModelOfTheLaneAlone()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new ManualClock();
        var gates = new ClientGates(clock, (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var lifts = clock.GetUtcNow() + TimeSpan.FromMinutes(2);
        var client = new Scripted("claude-code")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.RateLimited, Error: "the model is at its limit")
            {
                Limit = new AgentAccountLimit(BalanceSpent: false, lifts, Key: "WORK_KEY", Model: "claude-opus-5-5"),
            }),
        };
        var runner = new AgentRunner([client], harness.FileSystem, Elevation.User, clock, gates);

        var result = await runner.RunAsync(Step([Entry(client: "claude-code")], new Spent(), temp), events: null, Token);

        Assert.Equal(AgentRunStatus.RateLimited, result.Status);
        Assert.Equal(
            (new ClientCoolDown(TimeSpan.FromMinutes(2), "claude-opus-5-5"), (ClientCoolDown?)null),
            (LaneOfTheKey(gates, "WORK_KEY").CoolDownOf("claude-opus-5-5"), LaneOfTheKey(gates, "WORK_KEY").CoolDownOf("claude-fable-5-1")));
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
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Usage: Used(100, 50, cost: new AgentCost(Reported: 1.80m))) { DeclinedFor = "the request" }),
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
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Usage: Used(100, 50, cost: new AgentCost(MissingPrice: "claude-opus-5-5's priceOutput"))) { DeclinedFor = "the request" }),
        };
        var counted = new Scripted("two") { Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Usage: Used(100, 50))) };
        var runner = new AgentRunner([unpriced, counted], harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(Step([Entry(client: "one"), Entry(model: "claude-fable-5-1", key: "SPARE_KEY", client: "two")], new Spent(), temp), events: null, Token);

        Assert.Equal(new AgentCost(Reported: null, Counted: null, MissingPrice: "claude-opus-5-5's priceOutput"), result.Usage!.Cost);
        Assert.Null(result.Usage.Cost!.Usd);

        // An entry whose client said and counted nothing of what it used leaves the walk's cost unknown alike.
        var silent = new Scripted("one")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Usage: new AgentUsageReport(new AgentUsage(100, 50, 0, 0, 0, 0), [], null)) { DeclinedFor = "the request" }),
        };
        var unknown = await new AgentRunner([silent, counted], harness.FileSystem, Elevation.User, TimeProvider.System)
            .RunAsync(Step([Entry(client: "one"), Entry(model: "claude-fable-5-1", key: "SPARE_KEY", client: "two")], new Spent(), temp), events: null, Token);

        Assert.Equal((AgentRunStatus.Completed, AgentCost.Unknown), (unknown.Status, unknown.Usage!.Cost));
    }

    /// <summary>
    /// An entry whose launch used nothing and said no number - Claude Code rejected by its limit before any result - adds
    /// nothing to the walk's cost: the entry after it that counted its cost is the walk's whole, never made unknown by a
    /// launch that never began its work.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEntryThatUsedNothing_LeavesTheWalksCostToTheOthers(bool emptyCost)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var rejected = new Scripted("one")
        {
            Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.RateLimited, Error: "five_hour", Usage: new AgentUsageReport(AgentUsage.None, [], emptyCost ? new AgentCost() : null))
            {
                Limit = new AgentAccountLimit(BalanceSpent: false, Key: "WORK_KEY"),
            }),
        };
        var counted = new Scripted("two") { Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Usage: Used(100, 50, "claude-fable-5-1", "SPARE_KEY", new AgentCost(Counted: 0.40m)))) };
        var runner = new AgentRunner([rejected, counted], harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(Step([Entry(client: "one"), Entry(model: "claude-fable-5-1", key: "SPARE_KEY", client: "two")], new Spent(), temp), events: null, Token);

        Assert.Equal((AgentRunStatus.Completed, new AgentCost(Counted: 0.40m)), (result.Status, result.Usage!.Cost));
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
    /// A step budgeted in dollars is never started on a model without a price of each kind its client is billed by, on
    /// every client and before any is taken: a claude-code launch writes both caches, so a model priced for input and
    /// output alone is refused, naming the keys to fill - its cost could never be charged to the budget.
    /// </summary>
    [Fact]
    public async Task AStepBudgetedInDollars_IsNeverStarted_OnAModelWithoutThePricesItsClientIsBilledBy()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var started = new NoProcesses();
        var code = new DssHarness.Core.Agents.ClaudeCode.ClaudeCodeClient(started, harness.FileSystem, harness.Platform, harness.ChildSessions, TimeProvider.System);
        var runner = new AgentRunner([code], harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(
            Step([Entry(client: code.Name)], new Spent(), temp) with { Limits = new AgentLimits(10, 5m, null, null) },
            events: null,
            Token);

        Assert.Equal(
            (AgentRunStatus.NotStarted, AgentNotStartedBecause.ConfigurationWrong, "client 'claude-code' was not started: no price for 'claude-opus-5-5' (providers 'anthropic', model 'claude-opus-5-5': priceCacheWrite, priceCacheWrite1h), and a budget is charged with the cost counted from them", 0),
            (result.Status, result.NotStartedBecause, result.Error, started.Asked));
        Assert.Equal(AgentPrices.EveryRequestUses with { CacheWriteTokens = 1, CacheWrite1hTokens = 1 }, ((IAgentClient)code).BilledBy(Entry().Entries!));
    }

    /// <summary>
    /// A later entry of a budgeted step without the prices its client is billed by is handed to the serving client as one
    /// it may not take, with the reason, and passed over where the walk reaches it - never asked; a provider whose prices
    /// are in another currency is refused alike. A step with no budget takes it.
    /// </summary>
    [Fact]
    public async Task ALaterEntryWithoutThePrices_IsPassedOver_WhereTheStepIsBudgeted()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        AgentRun? given = null;
        var declines = new Scripted("one")
        {
            Script = (run, _, _) =>
            {
                given = run;
                return Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Error: "no") { DeclinedFor = "the request" });
            },
        };
        var asked = 0;
        var later = new Scripted("two") { Script = (_, _, _) => { asked++; return Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done")); } };
        var runner = new AgentRunner([declines, later], harness.FileSystem, Elevation.User, TimeProvider.System);
        var priced = Entry(model: "claude-fable-5-1", client: "two");
        var euros = priced with { Entries = priced.Entries! with { Provider = new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Currency = "EUR" } } };
        var seen = new List<AgentEvent>();

        var result = await runner.RunAsync(Step([Entry(client: "one"), euros], new Spent(), temp) with { Limits = new AgentLimits(10, 5m, null, null) }, new Collecting(seen), Token);

        const string Why = "providers 'anthropic' prices its models in EUR, and a budget is counted in USD";

        Assert.Equal((AgentRunStatus.Refused, 0), (result.Status, asked));
        Assert.Equal((false, Why), (given!.Fallback.Single().Usable, given.Fallback.Single().WhyNot));
        Assert.Equal(("claude-fable-5-1", $"client 'two' was not started: {Why}"), (Assert.Single(seen.OfType<FallbackPassedOver>()).Model, seen.OfType<FallbackPassedOver>().Single().Why));

        var free = await runner.RunAsync(Step([Entry(client: "one"), euros], new Spent(), temp), events: null, Token);

        Assert.Equal((AgentRunStatus.Completed, 1), (free.Status, asked));
    }

    /// <summary>
    /// A launch whose lane a cool-down holds past the client's longest wait is not begun: that limit is met, as the loop
    /// reads one, and the walk moves the step to an entry the cool-down does not hold - the lane keeping it for every
    /// later step of the run. The launch never waits the hours out.
    /// </summary>
    [Fact]
    public async Task ALaunchHeldPastTheLongestWait_IsPassedOver_ToAnEntryTheCoolDownDoesNotHold()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var clock = new ManualClock();
        var waits = new List<TimeSpan>();
        var gates = new ClientGates(clock, (wait, cancellationToken) =>
        {
            waits.Add(wait);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        LaneOfTheKey(gates, "WORK_KEY").RateLimited(TimeSpan.FromHours(2), "claude-opus-5-5");

        var held = new Scripted("claude-code") { Script = (_, _, _) => throw new InvalidOperationException("the held launch was begun") };
        var free = new Scripted("claude-api") { Script = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done")) };
        var runner = new AgentRunner([held, free], harness.FileSystem, Elevation.User, clock, gates);
        var seen = new List<AgentEvent>();

        var result = await runner.RunAsync(Step([Entry(client: "claude-code"), Entry(model: "claude-fable-5-1", client: "claude-api")], new Spent(), temp), new Collecting(seen), Token)
            .WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
        Assert.Empty(waits);

        var moved = Assert.Single(seen.OfType<FallbackStarted>());

        Assert.Equal(("claude-opus-5-5", "claude-fable-5-1", "limit"), (moved.FromModel, moved.ToModel, moved.Why));
        Assert.Equal(new ClientCoolDown(TimeSpan.FromHours(2), "claude-opus-5-5"), LaneOfTheKey(gates, "WORK_KEY").CoolDownOf("claude-opus-5-5"));
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
        LaneOfTheKey(gates, "WORK_KEY").RateLimited(TimeSpan.FromMinutes(2));

        var asked = 0;
        var client = new Scripted("claude-api") { Script = (_, _, _) => { asked++; return Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done")); } };
        var runner = new AgentRunner([client], harness.FileSystem, Elevation.User, clock, gates);
        var handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30));
        var waiting = new TaskCompletionSource<WaitingToRetry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<AgentEvent>();

        var run = runner.RunAsync(Step([Entry()], new Spent(), temp) with { Handoff = handoff }, new Collecting(seen, waiting), Token);
        var wait = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(
            ($"the run waits for {ClientGate.CoolDown}", TimeSpan.FromMinutes(2), clock.GetUtcNow() + TimeSpan.FromMinutes(2)),
            (wait.Cause, wait.Delay, wait.Until));

        handoff.Ask();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal((AgentRunStatus.HandedOff, AgentRunner.PausedAtTheGate), (result.Status, result.Handoff!.WhyNone));
        Assert.Equal(0, asked);
    }

    /// <summary>
    /// A failed agent waiting to be tried again is a wait the run's pause ends: the step ends handed off at once, saying it
    /// paused while it waited and what failed, and the launch it waited for is never begun - however long the backoff.
    /// </summary>
    [Fact]
    public async Task AFailedAgentWaitingToBeTriedAgain_IsEndedByThePause_AndBeginsNoLaunch()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var asked = 0;
        var client = new Scripted("claude-api") { Script = (_, _, _) => { asked++; return Task.FromResult(new AgentRunResult(AgentRunStatus.Failed, Error: "boom")); } };
        var runner = new AgentRunner([client], harness.FileSystem, Elevation.User, new LateTimers());
        var handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30));
        var waiting = new TaskCompletionSource<WaitingToRetry>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<AgentEvent>();

        var run = runner.RunAsync(Step([Entry()], new Spent(), temp) with { Handoff = handoff, Retries = 2, BackoffSeconds = 600 }, new Collecting(seen, waiting), Token);
        var wait = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(("the agent failed: boom", 1, 2, TimeSpan.FromSeconds(600)), (wait.Cause, wait.Attempt, wait.Of, wait.Delay));

        handoff.Ask();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal((AgentRunStatus.HandedOff, $"{AgentRunner.PausedWhileItWaited}: boom", 1), (result.Status, result.Handoff!.WhyNone, asked));
    }

    /// <summary>
    /// A pause asked while a launch ends that moves the step begins no launch after it: the step ends handed off between
    /// the two, and the entry it would have moved to is never asked.
    /// </summary>
    [Fact]
    public async Task APauseAskedAsALaunchEnds_BeginsNoLaunchAfterIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30));
        var declines = new Scripted("one")
        {
            Script = (_, _, _) =>
            {
                handoff.Ask();
                return Task.FromResult(new AgentRunResult(AgentRunStatus.Refused, Error: "no") { DeclinedFor = "the request" });
            },
        };
        var asked = 0;
        var later = new Scripted("two") { Script = (_, _, _) => { asked++; return Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done")); } };
        var runner = new AgentRunner([declines, later], harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(Step([Entry(client: "one"), Entry(model: "claude-fable-5-1", client: "two")], new Spent(), temp) with { Handoff = handoff }, events: null, Token);

        Assert.Equal((AgentRunStatus.HandedOff, AgentRunner.PausedBeforeItsNextLaunch, 0), (result.Status, result.Handoff!.WhyNone, asked));
    }

    /// <summary>
    /// A run that ended on a key its provider refused - the client handed the model's other key too, and ended before it
    /// moved to it - moves to the model's next key at once, never waiting a retry's backoff, which is for an agent that
    /// failed, and the step goes on there.
    /// </summary>
    [Fact]
    public async Task ARefusedKeyWithAnotherLeft_MovesToItAtOnce()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var keys = new List<string?>();
        var client = new Scripted("claude-api")
        {
            Script = (run, _, _) =>
            {
                var key = run.Credential?.Keys[0].Name;
                keys.Add(key);

                return Task.FromResult(key == "WORK_KEY"
                    ? new AgentRunResult(AgentRunStatus.Failed, Error: "invalid x-api-key") { KeysRefused = ["WORK_KEY"] }
                    : new AgentRunResult(AgentRunStatus.Completed, FinalText: "done"));
            },
        };
        var runner = new AgentRunner([client], harness.FileSystem, Elevation.User, new LateTimers());
        var seen = new List<AgentEvent>();

        var result = await runner.RunAsync(Step([Entry(), Entry(key: "OTHER_KEY")], new Spent(), temp) with { Retries = 2, BackoffSeconds = 600 }, new Collecting(seen), Token)
            .WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
        Assert.Equal(["WORK_KEY", "OTHER_KEY"], keys);
        Assert.Empty(seen.OfType<WaitingToRetry>());

        var moved = Assert.Single(seen.OfType<FallbackStarted>());

        Assert.Equal(("WORK_KEY", "OTHER_KEY", "key refused"), (moved.FromKey, moved.ToKey, moved.Why));
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
