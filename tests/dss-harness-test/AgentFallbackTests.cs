using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Loop;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Processes;
using DssHarness.Core.Web;

namespace DssHarness.Tests;

/// <summary>
/// The engine's fallback (part 03, "The engine's fallback"): a declined request, an account at its limit and a key the
/// provider refuses move a step through its order - inside the one conversation on the same provider and client, across
/// clients with the step's prompt - each model's keys before the next model, a limit passed over by what it covers, and
/// what each move records.
/// </summary>
public sealed class AgentFallbackTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();
    private readonly RecordedEvents _events = new();
    private readonly List<IDisposable> _opened = [];
    private readonly FakeChat _chat = new();
    private readonly string _worktree;
    private readonly string _step;
    private readonly string _sessions;

    public AgentFallbackTests()
    {
        string Real(string name) => _harness.FileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine(name)).FullName);

        _worktree = Real("worktree");
        var agent = Path.Combine(Real("run"), "agents", "a1");
        _step = Path.Combine(agent, "steps", "s1");
        _sessions = Path.Combine(agent, "sessions");
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var opened in _opened)
        {
            opened.Dispose();
        }

        _temp.Dispose();
    }

    /// <summary>
    /// A decline part way through an answer moves the run to its next model of the same provider and client - the same
    /// request sent again with the model changed - and the part is thrown away: none of it in the answer, none in the
    /// conversation, and every later request on the model that took over.
    /// </summary>
    [Fact]
    public async Task ADeclinePartWay_IsThrownAway_AndTheNextModelTakesTheConversation()
    {
        _chat
            .Reply(new ChatPart.Began(FakeChat.Model), new ChatPart.Text("thrown "), new ChatPart.Answered(FakeChat.Said("thrown ", stop: ChatStop.Declined, category: "cyber")))
            .Answer(FakeChat.Said("done", model: "claude-opus-4-8"));

        var result = await Ran(Run() with { Fallback = [Entry("claude-opus-4-8", "anthropic")] });

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
        Assert.Equal(["claude-opus-5-5", "claude-opus-4-8"], _chat.Requests.Select(request => request.Model));
        Assert.Equal(["claude-opus-5-5", "claude-opus-4-8"], result.Served.Select(served => served.Model));

        var moved = Assert.Single(_events.All.OfType<FallbackStarted>());

        Assert.Equal(
            ("claude-opus-5-5", "anthropic", "claude-api", "claude-opus-4-8", "anthropic", "claude-api", "declined (cyber)", AgentFallback.SentAgain, 1, true, false, "WORK_KEY", "WORK_KEY"),
            (moved.FromModel, moved.FromProvider, moved.FromClient, moved.ToModel, moved.ToProvider, moved.ToClient, moved.Why, moved.How, moved.Turn, moved.ThrewAway, moved.CreditAsked, moved.FromKey, moved.ToKey));
        Assert.Equal("the model declined the request: the turn is thrown away, and none of it is the agent's", Assert.Single(_events.All.OfType<TurnRestarted>(), restarted => restarted.Why.StartsWith("the model declined", StringComparison.Ordinal)).Why);

        // The declined turn was stored never: the conversation holds the first message and the taking model's answer.
        Assert.Equal(["user", "assistant"], [.. ReadConversation().Select(message => (string)System.Text.Json.Nodes.JsonNode.Parse(message)!["role"]!)]);
        Assert.DoesNotContain("thrown", string.Concat(ReadConversation()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A move to a model the step gives another effort goes on at that level and says both: the change is told in the
    /// conversation as the provider takes it, the request carries the new level, and a provider that caches the
    /// conversation is sent top-level the level it began at - and no credit token goes with it, for it is not the same
    /// request.
    /// </summary>
    [Fact]
    public async Task AMoveToAModelOfAnotherEffort_GoesOnAtThatLevel_AndSaysBoth()
    {
        _chat
            .Answer(FakeChat.Said("no", stop: ChatStop.Declined) with { Credit = new AgentFallbackCredit("tok-1", PrefillClaim: false) })
            .Answer(FakeChat.Said("done", model: "claude-opus-4-8"));

        var result = await Ran(Run() with { Effort = "low", Fallback = [Entry("claude-opus-4-8", "anthropic", effort: "high")] });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(("low", "high", "low"), (_chat.Requests[0].Effort, _chat.Requests[1].Effort, _chat.Requests[1].FirstEffort));
        Assert.Null(_chat.Requests[1].CreditToken);

        var told = Assert.Single(_events.All.OfType<MessageSent>(), sent => sent.Which == AgentMessageKind.Effort);

        Assert.Equal("The step goes on at effort 'high'.", told.Text);

        // The effort message is the provider's own form, appended to the conversation: the taking model reads it.
        var message = System.Text.Json.Nodes.JsonNode.Parse(_chat.Requests[1].Messages[^1])!;

        Assert.Equal(("system", "high"), (message["role"]!.GetValue<string>(), message["output_config"]!["effort"]!.GetValue<string>()));
        Assert.Equal(("low", "high"), (Assert.Single(_events.All.OfType<FallbackStarted>()).FromEffort, _events.All.OfType<FallbackStarted>().Single().ToEffort));
    }

    /// <summary>
    /// A rate limit past the client's tries moves the run to the same model's next key first - the same request sent
    /// again with only the key changed - and the limit is said with when it lifts.
    /// </summary>
    [Fact]
    public async Task ARateLimitPastTheTries_MovesToTheSameModelsNextKeyFirst()
    {
        for (var each = 0; each <= 2; each++)
        {
            _chat.Fail(new ChatFailure(ChatFailureKind.RateLimited, "slow down", Lifts: Lifts));
        }

        _chat.Answer(FakeChat.Said("done"));

        var result = await Ran(Run() with { Credential = Keys("WORK_KEY", "SPARE_KEY") }, Terms(maxRetries: 2));

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(["sk-1", "sk-1", "sk-1", "sk-2"], _chat.Requests.Select(request => request.Key!.Value));
        Assert.Equal(_chat.Requests[2].Messages, _chat.Requests[3].Messages);

        var moved = Assert.Single(_events.All.OfType<FallbackStarted>());

        Assert.Equal(("claude-opus-5-5", "claude-opus-5-5", "WORK_KEY", "SPARE_KEY"), (moved.FromModel, moved.ToModel, moved.FromKey, moved.ToKey));
        Assert.StartsWith("limit, until ", moved.Why, StringComparison.Ordinal);
        Assert.EndsWith(": slow down", moved.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// A spent balance is the key's whole limit: every later entry on that key is passed over - a later model's too -
    /// and the same model on another key is tried first.
    /// </summary>
    [Fact]
    public async Task ASpentBalance_PassesOverEveryEntryOnItsKey_OfAnyModel()
    {
        _chat
            .Fail(new ChatFailure(ChatFailureKind.AccountLimit, "credit balance too low", BalanceSpent: true))
            .Answer(FakeChat.Said("no", stop: ChatStop.Declined))
            .Answer(FakeChat.Said("done", model: "claude-opus-4-8"));

        var run = Run() with
        {
            Credential = Keys("WORK_KEY", "SPARE_KEY"),
            Fallback = [Entry("claude-opus-4-8", "anthropic", key: "WORK_KEY"), Entry("claude-opus-4-8", "anthropic", key: "SPARE_KEY")],
        };

        var result = await Ran(run);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(["sk-1", "sk-2", "sk-2"], _chat.Requests.Select(request => request.Key!.Value));
        Assert.Equal(["claude-opus-5-5", "claude-opus-5-5", "claude-opus-4-8"], _chat.Requests.Select(request => request.Model));

        // The balance spent on WORK_KEY covers the later model's entry on it too: said, and passed over, in the words of
        // the fallback's one rule, which the loop and the runner both walk by.
        var passed = _events.All.OfType<FallbackPassedOver>().Single();

        Assert.Equal(("claude-opus-4-8", "the limit covers it"), (passed.Model, passed.Why));
    }

    /// <summary>
    /// A key the provider refuses is said, passed over for the rest of the run - never asked again, of this model or
    /// another - and the next entry taken: the same model's next key, and past its last key the next model's entries,
    /// each of whose keys the run has seen refused passed over and said, for a provider's models share its keys.
    /// </summary>
    [Fact]
    public async Task AKeyTheProviderRefuses_IsNeverAskedAgain_OfAnyModel()
    {
        _chat
            .Fail(new ChatFailure(ChatFailureKind.KeyRefused, "invalid sk-1"))
            .Fail(new ChatFailure(ChatFailureKind.KeyRefused, "invalid sk-2"));

        var run = Run() with
        {
            Credential = Keys("WORK_KEY", "SPARE_KEY"),
            Fallback = [Entry("claude-opus-4-8", "anthropic", key: "WORK_KEY"), Entry("claude-opus-4-8", "anthropic", key: "SPARE_KEY")],
        };

        var result = await Ran(run);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(["sk-1", "sk-2"], _chat.Requests.Select(request => request.Key!.Value));
        Assert.Equal(["claude-opus-5-5", "claude-opus-5-5"], _chat.Requests.Select(request => request.Model));
        Assert.Equal(["WORK_KEY", "SPARE_KEY"], result.KeysRefused);

        // The ending names every key the run was given, all refused - the next model's entries name the same two, for
        // a provider's models share its keys, and neither is asked again.
        Assert.Equal("the provider refused every key the run was given (WORK_KEY, SPARE_KEY): invalid sk-2", result.Error);
        Assert.Empty(_events.All.OfType<FallbackPassedOver>());
    }

    /// <summary>
    /// A declined request passes over the model's other keys - another account of the same model is asked the same and
    /// answers the same - and the next model takes over; a later entry naming one of the serving model's own keys is
    /// that key's entry already in the order, said once and never twice.
    /// </summary>
    [Fact]
    public async Task ADeclinePassesOverTheServingModelsOtherKeys()
    {
        _chat.Answer(FakeChat.Said("no", stop: ChatStop.Declined)).Answer(FakeChat.Said("done", model: "claude-opus-4-8"));

        var run = Run() with
        {
            Credential = Keys("WORK_KEY", "SPARE_KEY"),
            Fallback = [Entry("claude-opus-5-5", "anthropic", key: "SPARE_KEY"), Entry("claude-opus-4-8", "anthropic", key: "WORK_KEY"), Entry("claude-opus-4-8", "anthropic", key: "SPARE_KEY")],
        };

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
        Assert.Equal(["sk-1", "sk-1"], _chat.Requests.Select(request => request.Key!.Value));
        Assert.Equal(["claude-opus-5-5", "claude-opus-4-8"], _chat.Requests.Select(request => request.Model));

        var passed = Assert.Single(_events.All.OfType<FallbackPassedOver>());

        Assert.Equal(("claude-opus-5-5", "a decline passes over the model's other keys"), (passed.Model, passed.Why));
    }

    /// <summary>
    /// A credit the provider refused - a failure naming the token - is answered by the same request sent again without
    /// it, no try of the service's.
    /// </summary>
    [Fact]
    public async Task ACreditTheProviderRefuses_IsAnsweredByTheSameRequestWithoutIt()
    {
        _chat
            .Answer(FakeChat.Said("no", stop: ChatStop.Declined) with { Credit = new AgentFallbackCredit("tok-1", PrefillClaim: false) })
            .Fail(new ChatFailure(ChatFailureKind.Fatal, "the fallback_credit_token tok-1 is not valid for this model"))
            .Answer(FakeChat.Said("done", model: "claude-opus-4-8"));

        var result = await Ran(Run() with { Fallback = [Entry("claude-opus-4-8", "anthropic")] });

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
        Assert.Equal(["tok-1", null], [.. _chat.Requests.Skip(1).Select(request => request.CreditToken?.Token)]);
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text.StartsWith("The provider refused the credit token", StringComparison.Ordinal));
    }

    /// <summary>
    /// What a client said it moved to is that run's, not the next one's: a later run that said nothing is read from
    /// the entry the walk started it on.
    /// </summary>
    [Fact]
    public async Task WhatTheLastRunSaidItMovedTo_IsNotTheNextRunsFrom()
    {
        var first = new Scripted("one")
        {
            Script = async (run, events, _) =>
            {
                await events.EmitAsync(new FallbackStarted(run.Model!, "anthropic", "one", "claude-opus-5-5", "anthropic", "one", "limit", AgentFallback.SentAgain, 1, false, false, "WORK_KEY", "SPARE_KEY"));
                return await Refused(run, events, null, turns: 2);
            },
        };
        var second = new Scripted("two") { Script = (run, events, _) => Refused(run, events, null, turns: 1) };
        var third = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };
        var runner = new AgentRunner([first, second, third], _harness.FileSystem, Elevation.User, TimeProvider.System);
        var seen = new Collecting();

        var result = await runner.RunAsync(
            Step(order:
            [
                Entry("claude-fable-5-1", "anthropic", "one", key: "WORK_KEY"),
                Entry("claude-opus-5-5", "anthropic", "two", key: "WORK_KEY"),
                Entry("glm-5.3", "zai", "glm", key: null),
            ]),
            seen,
            Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        // The walk's own moves - not the client's internal one - each read from where its run stood: the first from
        // what its client said it moved to, the second from the entry its run began on, never the first run's said move.
        var moves = seen.Seen.OfType<FallbackStarted>().Where(moved => moved.How == AgentFallback.NewConversation).ToList();

        Assert.Equal([("claude-opus-5-5", "claude-opus-5-5"), ("claude-opus-5-5", "glm-5.3")], [.. moves.Select(moved => (moved.FromModel, moved.ToModel))]);
        Assert.Equal(["SPARE_KEY", "WORK_KEY"], [.. moves.Select(moved => moved.FromKey)]);
    }

    /// <summary>
    /// What moves no step: a rate limit the client can still wait out, and an overloaded service - the provider's state,
    /// not the account's - and a context that is full; and a decline about what the prompt asks is no other model's to
    /// answer differently.
    /// </summary>
    [Fact]
    public async Task WhatMovesNoStep()
    {
        _chat.Fail(new ChatFailure(ChatFailureKind.RateLimited, "slow down", RetryAfter: TimeSpan.FromSeconds(1))).Answer(FakeChat.Said("done"));

        var waited = await Ran(Run() with { Fallback = [Entry("claude-opus-4-8", "anthropic")] });

        Assert.Equal(AgentRunStatus.Completed, waited.Status);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(FakeChat.Model, _chat.Requests[1].Model);
        Assert.Empty(_events.All.OfType<FallbackStarted>());

        var overloaded = new FakeChat();
        for (var each = 0; each <= 2; each++)
        {
            overloaded.Fail(new ChatFailure(ChatFailureKind.Transient, "overloaded"));
        }

        var failed = await Ran(Run() with { Fallback = [Entry("claude-opus-4-8", "anthropic")] }, Terms(maxRetries: 2), chat: overloaded);

        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Equal(3, overloaded.Requests.Count);

        var full = new FakeChat();
        full.Fail(new ChatFailure(ChatFailureKind.ContextFull, "prompt is too long"));

        var exhausted = await Ran(Run() with { Fallback = [Entry("claude-opus-4-8", "anthropic")] }, chat: full);

        Assert.Equal(AgentRunStatus.ContextExhausted, exhausted.Status);
        Assert.Single(full.Requests);

        var extraction = new FakeChat();
        extraction.Answer(FakeChat.Said("no", stop: ChatStop.Declined, category: AgentFallback.ReasoningExtraction));

        var aboutThePrompt = await Ran(Run() with { Fallback = [Entry("claude-opus-4-8", "anthropic")] }, chat: extraction);

        Assert.Equal(AgentRunStatus.Refused, aboutThePrompt.Status);
        Assert.Equal(AgentFallback.ReasoningExtraction, aboutThePrompt.DeclinedFor);
        Assert.Single(extraction.Requests);
        Assert.DoesNotContain(_events.All, @event => @event is FallbackStarted);
    }

    /// <summary>
    /// The walk across clients: a declined request with no entry left on the client that served moves the step to its
    /// next entry on another client - a new conversation with the step's prompt - and what both did is the step's one
    /// result.
    /// </summary>
    [Fact]
    public async Task ADeclineWithNoEntryOnItsClient_MovesTheStepToAnotherClientsEntry()
    {
        var first = new Scripted("claude-code") { Script = (run, events, _) => Refused(run, events, "cyber", turns: 2) };
        var second = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 3) };
        var runner = new AgentRunner([first, second], _harness.FileSystem, Elevation.User, TimeProvider.System);
        var seen = new Collecting();

        var result = await runner.RunAsync(Step(order: [Entry("claude-fable-5-1", "anthropic", "claude-code", key: null), Entry("glm-5.3", "zai", "glm", key: null)]), seen, Token);

        Assert.Equal((AgentRunStatus.Completed, "done", 5), (result.Status, result.FinalText, result.Turns));
        Assert.Equal((2, 2, 2), (result.Retries, result.ToolCalls, result.WebRequests));
        Assert.Equal(["claude-fable-5-1", "glm-5.3"], result.Served.Select(served => served.Model));
        Assert.Equal([1, 3], result.Served.Select(served => served.FromTurn));
        Assert.Equal("glm-5.3", second.Given!.Model);

        var moved = Assert.Single(seen.Seen.OfType<FallbackStarted>());

        Assert.Equal(
            ("claude-fable-5-1", "anthropic", "claude-code", "glm-5.3", "zai", "glm", "declined (cyber)", AgentFallback.NewConversation, 2),
            (moved.FromModel, moved.FromProvider, moved.FromClient, moved.ToModel, moved.ToProvider, moved.ToClient, moved.Why, moved.How, moved.Turn));

        // A move to another client is never a conversation gone on with: the run it begins is a new one.
        Assert.Null(second.Given.Conversation);
    }

    /// <summary>
    /// An entry the machine cannot use is said and passed over when its turn comes, and never refuses a run: the
    /// step's own first model's too, where another of its keys is held.
    /// </summary>
    [Fact]
    public async Task AnEntryTheMachineCannotUse_IsSaidAndPassedOver_AtItsTurn()
    {
        var first = new Scripted("claude-code") { Script = (run, events, _) => Refused(run, events, null, turns: 1) };
        var third = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };
        var runner = new AgentRunner([first, third], _harness.FileSystem, Elevation.User, TimeProvider.System);
        var seen = new Collecting();

        var result = await runner.RunAsync(
            Step(order:
            [
                Entry("claude-fable-5-1", "anthropic", "claude-code", key: null),
                new AgentRunEntry("claude-opus-5-5", "anthropic", "claude-code", null, null, Usable: false, "no file under '.harness-config/workflows/.env/' declares 'ANTHROPIC_API_KEY'"),
                Entry("glm-5.3", "zai", "glm", key: null),
            ]),
            seen,
            Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);

        var passed = Assert.Single(seen.Seen.OfType<FallbackPassedOver>());

        Assert.Equal(("claude-opus-5-5", "no file under '.harness-config/workflows/.env/' declares 'ANTHROPIC_API_KEY'"), (passed.Model, passed.Why));

        // A first model with another key that is held is begun on that key, its unusable entry said as the run starts.
        var other = new Scripted("one") { Script = (run, events, _) => Completed(run, events, "claude-fable-5-1", turns: 1) };
        var walking = new AgentRunner([other], _harness.FileSystem, Elevation.User, TimeProvider.System);
        var told = new Collecting();

        var begun = await walking.RunAsync(
            Step(order:
            [
                new AgentRunEntry("claude-fable-5-1", "anthropic", "one", "ANTHROPIC_MISSING", null, Usable: false, "no file under '.harness-config/workflows/.env/' declares 'ANTHROPIC_MISSING'"),
                Entry("claude-fable-5-1", "anthropic", "one", key: "ANTHROPIC_HELD"),
            ]),
            told,
            Token);

        Assert.Equal(AgentRunStatus.Completed, begun.Status);

        var said = Assert.Single(told.Seen.OfType<FallbackPassedOver>());

        Assert.Equal(("claude-fable-5-1", "no file under '.harness-config/workflows/.env/' declares 'ANTHROPIC_MISSING'"), (said.Model, said.Why));
    }

    /// <summary>
    /// The slot holds none of the first model's keys: the run never begins, refused as one that could not start. A
    /// later model's missing key is only said and passed over, and never refuses a run.
    /// </summary>
    [Fact]
    public async Task WhereNoneOfTheFirstModelsKeysIsThere_TheRunIsRefused()
    {
        var other = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };
        var runner = new AgentRunner([other], _harness.FileSystem, Elevation.User, TimeProvider.System);

        var refused = await runner.RunAsync(
            Step(order:
            [
                new AgentRunEntry("claude-fable-5-1", "anthropic", "claude-api", "ANTHROPIC_API_KEY", null, Usable: false, "no file under '.harness-config/workflows/.env/' declares 'ANTHROPIC_API_KEY'"),
                new AgentRunEntry("claude-fable-5-1", "anthropic", "claude-api", "ANTHROPIC_SPARE", null, Usable: false, "no file under '.harness-config/workflows/.env/' declares 'ANTHROPIC_SPARE'"),
            ]),
            events: null,
            Token);

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.KeyMissing), (refused.Status, refused.NotStartedBecause));
        Assert.Equal(
            "client 'claude-api' was not started: the slot holds none of the keys of the step's first model (ANTHROPIC_API_KEY, ANTHROPIC_SPARE)",
            refused.Error);
        Assert.Null(other.Given);

        var moved = await runner.RunAsync(
            Step(order:
            [
                Entry("glm-5.3", "zai", "glm", key: null),
                new AgentRunEntry("claude-fable-5-1", "anthropic", "claude-api", "ANTHROPIC_API_KEY", null, Usable: false, "no file under '.harness-config/workflows/.env/' declares 'ANTHROPIC_API_KEY'"),
            ]),
            events: null,
            Token);

        Assert.Equal(AgentRunStatus.Completed, moved.Status);
    }

    /// <summary>
    /// A limit the run met is remembered until its end: an entry the limit covers is passed over, said so, whichever
    /// client would serve it - and the first the limit does not cover takes over.
    /// </summary>
    [Fact]
    public async Task ALimitTheRunRemembers_CoversTheEntriesAfterIt()
    {
        var first = new Scripted("one") { Script = (run, events, _) => Refused(run, events, null, turns: 1) };
        var limited = new Scripted("two") { Script = (run, events, _) => AtLimit(run, events, "WORK_KEY") };
        var third = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };
        var runner = new AgentRunner([first, limited, third], _harness.FileSystem, Elevation.User, TimeProvider.System);
        var seen = new Collecting();

        var result = await runner.RunAsync(
            Step(order:
            [
                Entry("claude-fable-5-1", "anthropic", "one", key: "WORK_KEY"),
                Entry("claude-opus-5-5", "anthropic", "two", key: "WORK_KEY"),
                Entry("glm-5.3", "zai", "glm", key: "WORK_KEY"),
                Entry("glm-5.3", "zai", "glm", key: "SPARE_KEY"),
            ]),
            seen,
            Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(("glm-5.3", 0), (third.Given!.Model, third.Given.Fallback.Count));

        var passed = Assert.Single(seen.Seen.OfType<FallbackPassedOver>());

        Assert.Equal($"the limit covers it until {StillLifts:u}", passed.Why);
        Assert.Equal("glm-5.3", passed.Model);
    }

    /// <summary>
    /// A message written by a model nobody asked for is an agent failure like any other (the user, 2026-10-17): tried
    /// again on the model that served it - its retries, and none here - and then on each next model of the order, for
    /// only when every model was tried does the step end failed.
    /// </summary>
    [Fact]
    public async Task AMessageOfAModelNobodyAskedFor_IsTriedAgainOnTheNextModel()
    {
        var first = new Scripted("claude-api")
        {
            Script = (run, events, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.WrongModel, Model: run.Model, Error: "the step asked for 'claude-fable-5-1' and its turn 4 was written by 'claude-opus-5'")),
        };
        var second = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };
        var runner = new AgentRunner([first, second], _harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(Step(order: [Entry("claude-fable-5-1", "anthropic", "claude-api", key: "WORK_KEY"), Entry("glm-5.3", "zai", "glm", key: null)]), events: null, Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.NotNull(second.Given);
    }

    /// <summary>When a limit lifts, where the provider says: a future one for coverage, a past one for its lifting.</summary>
    private static readonly DateTimeOffset Lifts = DateTimeOffset.UtcNow.AddDays(-2);

    /// <summary>A limit that has not lifted yet: an hour from whenever the test runs, never a date the clock has passed.</summary>
    private static readonly DateTimeOffset StillLifts = DateTimeOffset.UtcNow.AddHours(1);

    /// <summary>A run as the loop takes it, on the loop's provider and client.</summary>
    private AgentRun Run()
        => AgentRuns.Of(_step, _worktree, null, PolicyGrounds.Of(_worktree, [_worktree], [_worktree], [], [], [], null)) with
        {
            Model = FakeChat.Model,
            Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30)),
            SessionsDirectory = _sessions,
            Credential = Keys("WORK_KEY"),
        };

    /// <summary>The provider's keys, one per name, all of one value shape.</summary>
    private static AgentCredential Keys(params string[] names)
        => new(AgentAuth.ApiKey, [.. names.Select((name, at) => new AgentKey(name, $"sk-{at + 1}"))]);

    /// <summary>One entry of the run's order, of the loop's provider and client, for a fallback a test names.</summary>
    private static AgentRunEntry Entry(string model, string provider = "anthropic", string client = "claude-api", string? key = "WORK_KEY", string? effort = null, bool usable = true, AgentCredential? credential = null)
        => new(
            model,
            provider,
            client,
            key,
            effort,
            usable,
            usable ? null : $"no file under '.harness-config/workflows/.env/' declares '{key}'")
        {
            Credential = credential,
            Entries = usable
                ? new AgentEntries(
                    new AgentProviderConfig { Name = provider, Type = "anthropic", Currency = "USD" },
                    new AgentModelConfig { Code = model, MaxOutputTokens = 64000, ContextTokens = 200000, PriceInputMiss = 5, PriceInputHit = 1, PriceOutput = 25 },
                    null)
                : null,
        };

    private static ToolLoopTerms Terms(int maxRetries = 3)
        => new(
            "claude-api",
            new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Currency = "USD" },
            new AgentModelConfig { Code = FakeChat.Model, MaxOutputTokens = 64000, ContextTokens = 200000, PriceInputMiss = 5, PriceInputHit = 1, PriceOutput = 25 },
            maxRetries);

    private ToolLoop Loop()
    {
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        _opened.Add(fetch);

        return new ToolLoop(
            _harness.FileSystem,
            new AgentToolHost(_harness.FileSystem, _harness.Platform, _harness.GitClient, new PhaseRunner(_harness.ProcessRunner, _harness.FileSystem, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System),
            TimeProvider.System,
            (_, _) => Task.CompletedTask,
            () => 0);
    }

    /// <summary>Works <paramref name="run"/> and holds its result together, as the runner would.</summary>
    private async Task<AgentRunResult> Ran(AgentRun run, ToolLoopTerms? terms = null, FakeChat? chat = null)
    {
        var result = await Loop().RunAsync(run, _events, chat ?? _chat, terms ?? Terms(), Token);

        Assert.Null(result.Problem());

        return result;
    }

    /// <summary>The stored conversation's messages, as they were stored: every line's array, flattened.</summary>
    private IReadOnlyList<string> ReadConversation()
    {
        var session = _events.All.OfType<SessionStarted>().Single().SessionId;

        Assert.NotNull(session);

        return [.. File.ReadAllLines(Path.Combine(_sessions, session!, "conversation.jsonl"))
            .SelectMany(line => System.Text.Json.Nodes.JsonNode.Parse(line)!.AsArray().Select(message => message!.ToJsonString()))];
    }

    /// <summary>A step to work, on a repository of a temporary directory, with the order its engine resolved.</summary>
    private AgentStepRequest Step(IReadOnlyList<AgentRunEntry> order)
        => new(
            "r1",
            "a7",
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
            PromptTemplate: "do the {item}",
            new Dictionary<string, string> { ["item"] = "thing" },
            ResumeTemplate: null,
            new AgentWorkspace(
                Worktree: null,
                WorkDirectory(_temp),
                _temp.Path,
                ExtraRoots: [],
                WorkingDirectory: WorkDirectory(_temp),
                RunDirectory: Path.Combine(_temp.Path, ".orchestrators", "r1")),
            Output: null,
            Conversation: null,
            new AgentLimits(MaxTurns: 10, MaxBudgetUsd: null, MaxDuration: null, StallSeconds: 7),
            Mask: null)
        {
            Order = order,
        };

    /// <summary>The agent's work directory, the only place it writes.</summary>
    private string WorkDirectory(TempDirectory temp) => Path.Combine(temp.Path, ".orchestrators", "r1", "work", "a7");

    /// <summary>A client that works whatever its script says, remembering the run it was given.</summary>
    private sealed class Scripted(string name) : IAgentClient
    {
        public string Name { get; } = name;

        public AgentClientCapabilities Capabilities { get; init; } = new(
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

        public AgentRun? Given { get; private set; }

        public Func<AgentRun, IAgentEventSink, CancellationToken, Task<AgentRunResult>> Script { get; init; }
            = (_, _, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Completed, FinalText: "done"));

        public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
        {
            Given = run;
            return await Script(run, events, cancellationToken);
        }
    }

    /// <summary>A run the model declined, with what it was of.</summary>
    private static async Task<AgentRunResult> Refused(AgentRun run, IAgentEventSink events, string? category, int turns)
    {
        await events.EmitAsync(new SessionStarted("the client", run.Model!, "hash", [], [], HandedConversation: false, run.Provider));
        return new AgentRunResult(AgentRunStatus.Refused, Model: run.Model, Turns: turns, Error: $"the model declined the request{(category is null ? string.Empty : $" ({category})")}")
        {
            DeclinedFor = category,
            Served = [new AgentServedModel(run.Model!, 1)],
            Retries = 1,
        };
    }

    /// <summary>A run that completed its step's work.</summary>
    private static async Task<AgentRunResult> Completed(AgentRun run, IAgentEventSink events, string model, int turns)
    {
        await events.EmitAsync(new SessionStarted("the client", model, "hash", [], [], HandedConversation: false, run.Provider));
        return new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Model: run.Model, Turns: turns)
        {
            Served = [new AgentServedModel(model, 1)],
            Retries = 1,
            ToolCalls = 2,
            WebRequests = 2,
            Usage = new AgentUsageReport(FakeChat.Used(), [new AgentModelUsage(model, FakeChat.Used())]),
        };
    }

    /// <summary>A run that ended on its account's limit, the key's whole spent, lifting in the future.</summary>
    private static async Task<AgentRunResult> AtLimit(AgentRun run, IAgentEventSink events, string key)
    {
        await events.EmitAsync(new SessionStarted("claude-api", run.Model!, "hash", [], [], HandedConversation: false, run.Provider));
        return AgentEndings.AtLimit("the provider", new AgentAccountLimit(true, StillLifts, key), "credit balance too low", run.Mask) with { Turns = 1 };
    }

    /// <summary>
    /// A failed agent is tried again on its model - its retries, waiting its backoff doubled before each later ask,
    /// at most fifteen minutes - and only then does the step move to the next model, said so by the move its walk emits.
    /// </summary>
    [Fact]
    public async Task AFailedAgent_IsTriedAgainOnItsModel_BeforeTheStepMovesToTheNextModel()
    {
        var asked = 0;
        var failing = new Scripted("one")
        {
            Script = (run, events, _) =>
            {
                asked++;
                return Task.FromResult(new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "it broke"));
            },
        };
        var next = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };

        // Every wait passes at once on the clock, which moves by what was waited: the backoff is the clock's, not the test's.
        var clock = new JumpingClock();
        var runner = new AgentRunner([failing, next], _harness.FileSystem, Elevation.User, clock);
        var seen = new Collecting();

        var result = await runner.RunAsync(
            Step([Entry("claude-fable-5-1", "anthropic", "one"), Entry("glm-5.3", "zai", "glm", key: null)]) with { Retries = 2, BackoffSeconds = 1 },
            seen,
            Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(3, asked);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], clock.Waits);
        Assert.Equal(
            [("the agent failed: it broke", 1, 2, TimeSpan.FromSeconds(1)), ("the agent failed: it broke", 2, 2, TimeSpan.FromSeconds(2))],
            seen.Seen.OfType<WaitingToRetry>().Select(wait => (wait.Cause, wait.Attempt, wait.Of, wait.Delay)));

        var moved = seen.Seen.OfType<FallbackStarted>().Single();

        Assert.Equal("failed past its 2 retries", moved.Why);
        Assert.Equal("glm-5.3", moved.ToModel);
    }

    /// <summary>
    /// A failed agent with no retries given moves at once, and one whose retries are spent on its last model ends
    /// failed: only when every model was tried does the failure meet the step's own ending.
    /// </summary>
    [Fact]
    public async Task AFailedAgentWithNoRetries_MovesAtOnce_AndEndsFailedWhereEveryModelWasTried()
    {
        var failing = new Scripted("one") { Script = (run, events, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "it broke")) };
        var alsoFailing = new Scripted("glm") { Script = (run, events, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "it broke too")) };
        var runner = new AgentRunner([failing, alsoFailing], _harness.FileSystem, Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(
            Step([Entry("claude-fable-5-1", "anthropic", "one"), Entry("glm-5.3", "zai", "glm", key: null)]),
            events: null,
            Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Contains("it broke too", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The provider's service failing past the client's own tries is neither retried nor moved past: every model of
    /// the step is reached through the same service, so the ending stands for the run to pause on.
    /// </summary>
    [Fact]
    public async Task AProvidersServiceFailure_IsNeitherRetriedNorMovedPast()
    {
        var asked = 0;
        var unreachable = new Scripted("one")
        {
            Script = (run, events, _) =>
            {
                asked++;
                return Task.FromResult(new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "the provider's service failed past its 4 tries: unreachable") { ServiceFailed = true });
            },
        };
        var next = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };
        var runner = new AgentRunner([unreachable, next], _harness.FileSystem, Elevation.User, TimeProvider.System);
        var seen = new Collecting();

        var result = await runner.RunAsync(
            Step([Entry("claude-fable-5-1", "anthropic", "one"), Entry("glm-5.3", "zai", "glm", key: null)]) with { Retries = 3, BackoffSeconds = 1 },
            seen,
            Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.True(result.ServiceFailed);
        Assert.Equal(1, asked);
        Assert.Empty(seen.Seen.OfType<WaitingToRetry>());
        Assert.Empty(seen.Seen.OfType<FallbackStarted>());
    }

    /// <summary>
    /// The stored conversation an attempt goes on with is handed to the entry that began it - its own client and
    /// provider, on a client the engine hands conversations to - and to no other entry, nor a client that renders its
    /// conversations.
    /// </summary>
    [Fact]
    public async Task TheStoredConversationAnAttemptGoesOnWith_IsHandedToTheEntryThatBeganIt()
    {
        var handed = new AgentSessionRef("api", "anthropic", "11111111-1111-1111-1111-111111111111");
        var stores = new Scripted("api")
        {
            Capabilities = new AgentClientCapabilities(
                Streaming: true, Thinking: true, WritableWhileWorking: true, AgentConversationHandling.Handed, SchemaEnforcedOutput: false,
                AgentToolsOwner.Harness, AgentToolsOwner.Harness, ReportsCost: false, ["low", "high"], ProviderType: "none", ToldModelBy: "the test"),
            Script = (run, events, _) => Completed(run, events, run.Model!, turns: 1),
        };
        var renders = new Scripted("claude") { Script = (run, events, _) => Completed(run, events, run.Model!, turns: 1) };
        var runner = new AgentRunner([stores, renders], _harness.FileSystem, Elevation.User, TimeProvider.System);
        var step = Step([Entry("claude-fable-5-1", "anthropic", "api"), Entry("claude-opus-5-5", "anthropic", "claude")]) with { Conversation = handed };

        _ = await runner.RunAsync(step, events: null, Token);

        Assert.Same(handed, stores.Given!.Conversation);
        Assert.Null(renders.Given);
    }

    /// <summary>
    /// The order the slot gives - one model, two keys of its provider, each an entry of its own - is walked inside the
    /// client: a key the provider refuses moves the run to the same model's next key in the one conversation, the request
    /// sent again - never the refused key asked again, and never a move past the model.
    /// </summary>
    [Fact]
    public async Task AKeyRefused_MovesToTheSameModelsNextKey_InTheOrderTheSlotGives()
    {
        _chat.Fail(new ChatFailure(ChatFailureKind.KeyRefused, "invalid x-api-key")).Answer(FakeChat.Said("done"));

        var (result, seen) = await WalkedAsync();

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
        Assert.Equal(["sk-first", "sk-second"], _chat.Requests.Select(request => request.Key!.Value));
        Assert.Equal(["ANTHROPIC_KEY"], result.KeysRefused);

        var moved = Assert.Single(seen.OfType<FallbackStarted>());

        Assert.Equal(
            (FakeChat.Model, FakeChat.Model, "ANTHROPIC_KEY", "ANTHROPIC_SECOND", AgentFallback.SentAgain),
            (moved.FromModel, moved.ToModel, moved.FromKey, moved.ToKey, moved.How));
        Assert.Empty(seen.OfType<WaitingToRetry>());
    }

    /// <summary>
    /// An account at its limit on the slot's first key moves the run to the same model's next key inside the client first,
    /// in the one conversation, the request sent again - as the limit's rule says, never past it to another model, and
    /// never a launch of its own.
    /// </summary>
    [Fact]
    public async Task AnAccountAtItsLimit_MovesToTheSameModelsNextKey_InTheOrderTheSlotGives()
    {
        _chat.Fail(new ChatFailure(ChatFailureKind.AccountLimit, "credit balance too low", BalanceSpent: true)).Answer(FakeChat.Said("done"));

        var (result, seen) = await WalkedAsync();

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(["sk-first", "sk-second"], _chat.Requests.Select(request => request.Key!.Value));

        var moved = Assert.Single(seen.OfType<FallbackStarted>());

        Assert.Equal(("ANTHROPIC_KEY", "ANTHROPIC_SECOND", AgentFallback.SentAgain), (moved.FromKey, moved.ToKey, moved.How));
    }

    /// <summary>
    /// The entry a move reaches is asked with tries of its own: the tries the entry it left spent are that entry's, so a
    /// passing failure of the next one is waited and asked again, not taken for the account at its limit.
    /// </summary>
    [Fact]
    public async Task TheEntryAMoveReaches_IsAskedWithTriesOfItsOwn()
    {
        for (var each = 0; each <= 2; each++)
        {
            _chat.Fail(new ChatFailure(ChatFailureKind.RateLimited, "slow down"));
        }

        _chat.Fail(new ChatFailure(ChatFailureKind.RateLimited, "slow down")).Answer(FakeChat.Said("done"));

        var result = await Ran(Run() with { Credential = Keys("WORK_KEY", "SPARE_KEY") }, Terms(maxRetries: 2));

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(["sk-1", "sk-1", "sk-1", "sk-2", "sk-2"], _chat.Requests.Select(request => request.Key!.Value));
    }

    /// <summary>
    /// The loop's own walk ends at the first entry of another provider or client: the step's order is walked in its order,
    /// so an entry of the same provider and client after one of another is the runner's to reach, never the loop's.
    /// </summary>
    [Fact]
    public async Task TheLoopsWalk_EndsAtTheFirstEntryOfAnotherProviderOrClient()
    {
        _chat.Answer(FakeChat.Said("no", stop: ChatStop.Declined)).Answer(FakeChat.Said("done", model: "claude-opus-4-8"));

        var result = await Ran(Run() with { Fallback = [Entry("glm-5.3", "zai", "glm"), Entry("claude-opus-4-8", "anthropic")] });

        Assert.Equal(AgentRunStatus.Refused, result.Status);
        Assert.Equal([FakeChat.Model], _chat.Requests.Select(request => request.Model));
        Assert.Empty(_events.All.OfType<FallbackStarted>());
    }

    /// <summary>
    /// A move to a model the step gives no level goes on at the model's own default: its requests name no level from then
    /// on - neither the level the conversation began at nor the last model's - and a notice says so, never a message the
    /// conversation was not sent.
    /// </summary>
    [Fact]
    public async Task AMoveToAModelOfItsOwnDefaultEffort_SendsNoLevel_AndSaysSo()
    {
        _chat.Answer(FakeChat.Said("no", stop: ChatStop.Declined)).Answer(FakeChat.Said("done", model: "claude-opus-4-8"));

        var result = await Ran(Run() with { Effort = "low", Fallback = [Entry("claude-opus-4-8", "anthropic", effort: null)] });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(("low", null, null), (_chat.Requests[0].Effort, _chat.Requests[1].Effort, _chat.Requests[1].FirstEffort));
        Assert.Equal(_chat.Requests[0].Messages, _chat.Requests[1].Messages);
        Assert.DoesNotContain(_events.All.OfType<MessageSent>(), sent => sent.Which == AgentMessageKind.Effort);
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text == "The step goes on at its model's own default effort: its requests name no level from now on.");

        // The Claude API's body names no level at all: the model's own default is what no level asks for.
        var body = System.Text.Json.Nodes.JsonNode.Parse(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.Body(_chat.Requests[1], DssHarness.Core.Agents.ClaudeApi.ClaudeApiSettings.Of(null)))!;

        Assert.Null(body["output_config"]);
    }

    /// <summary>
    /// A move to a model's own default after a level was told in the conversation sends the level no more: the requests
    /// that follow carry none top-level - never the level the conversation began at - and the change is said, for no
    /// message of the conversation tells it.
    /// </summary>
    [Fact]
    public async Task AMoveToTheDefaultAfterALevelWasTold_SendsNoLevelFromThen()
    {
        _chat
            .Answer(FakeChat.Said("no", stop: ChatStop.Declined))
            .Answer(FakeChat.Said("no", stop: ChatStop.Declined, model: "claude-opus-4-8"))
            .Answer(FakeChat.Said("done", model: "claude-fable-5-1"));

        var result = await Ran(Run() with
        {
            Effort = "low",
            Fallback = [Entry("claude-opus-4-8", "anthropic", effort: "high"), Entry("claude-fable-5-1", "anthropic", effort: null)],
        });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(("high", "low"), (_chat.Requests[1].Effort, _chat.Requests[1].FirstEffort));
        Assert.Equal((null, string.Empty), (_chat.Requests[2].Effort, _chat.Requests[2].FirstEffort));
        Assert.Equal(["The step goes on at effort 'high'."], _events.All.OfType<MessageSent>().Where(sent => sent.Which == AgentMessageKind.Effort).Select(sent => sent.Text));
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text == "The step goes on at its model's own default effort: its requests name no level from now on.");

        var body = System.Text.Json.Nodes.JsonNode.Parse(DssHarness.Core.Agents.ClaudeApi.ClaudeApiRequest.Body(_chat.Requests[2], DssHarness.Core.Agents.ClaudeApi.ClaudeApiSettings.Of(null)))!;

        Assert.Null(body["output_config"]);
    }

    /// <summary>
    /// After a move inside the client, a failed agent is tried again where the step stood - the entry its client moved
    /// to - never the entry it began on, whose key the move left behind.
    /// </summary>
    [Fact]
    public async Task AFailedAgentAfterAMoveInsideItsClient_IsTriedAgainWhereItStood()
    {
        var given = new List<AgentRun>();
        var client = new Scripted("one")
        {
            Script = async (run, events, _) =>
            {
                given.Add(run);

                if (given.Count == 1)
                {
                    await events.EmitAsync(new FallbackStarted(run.Model!, "anthropic", "one", run.Model!, "anthropic", "one", "limit", AgentFallback.SentAgain, 1, false, false, "WORK_KEY", "SPARE_KEY"));
                    return new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "it broke");
                }

                return new AgentRunResult(AgentRunStatus.Completed, FinalText: "done", Model: run.Model);
            },
        };
        var runner = new AgentRunner([client], _harness.FileSystem, Elevation.User, new JumpingClock());

        var result = await runner.RunAsync(
            Step([Entry("claude-fable-5-1", "anthropic", "one", credential: Keys("WORK_KEY")), Entry("claude-fable-5-1", "anthropic", "one", key: "SPARE_KEY", credential: Keys("SPARE_KEY"))]) with { Retries = 1, BackoffSeconds = 1 },
            events: null,
            Token);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal([["WORK_KEY", "SPARE_KEY"], ["SPARE_KEY"]], given.Select(run => run.Credential!.Keys.Select(key => key.Name).ToArray()));
    }

    /// <summary>
    /// An entry a move reaches that cannot be started is passed over, said so, as one the machine cannot use is: the step
    /// has done work by then, so it goes on to the entry after it, and the move that brought it there is said again to
    /// the one that takes over.
    /// </summary>
    [Fact]
    public async Task AnEntryAMoveReachesThatCannotStart_IsPassedOver()
    {
        var first = new Scripted("one") { Script = (run, events, _) => Refused(run, events, "cyber", turns: 2) };
        var unserved = new Scripted("two") { Script = (run, _, _) => Task.FromResult(AgentEndings.NotStarted("two", AgentNotStartedBecause.ConfigurationWrong, "the account is not served 'claude-opus-5-5'", run.Model, run.Mask)) };
        var third = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };
        var runner = new AgentRunner([first, unserved, third], _harness.FileSystem, Elevation.User, TimeProvider.System);
        var seen = new Collecting();

        var result = await runner.RunAsync(
            Step([Entry("claude-fable-5-1", "anthropic", "one", key: null), Entry("claude-opus-5-5", "anthropic", "two", key: null), Entry("glm-5.3", "zai", "glm", key: null)]),
            seen,
            Token);

        Assert.Equal((AgentRunStatus.Completed, 3), (result.Status, result.Turns));
        Assert.Equal(
            ("claude-opus-5-5", "client 'two' was not started: the account is not served 'claude-opus-5-5'"),
            (Assert.Single(seen.Seen.OfType<FallbackPassedOver>()).Model, seen.Seen.OfType<FallbackPassedOver>().Single().Why));
        Assert.Equal(
            [("claude-fable-5-1", "claude-opus-5-5", "declined (cyber)"), ("claude-fable-5-1", "glm-5.3", "declined (cyber)")],
            seen.Seen.OfType<FallbackStarted>().Select(moved => (moved.FromModel, moved.ToModel, moved.Why)));
    }

    /// <summary>
    /// Each request of the loop goes through the lane of its provider and key, its input counted as it enters: a lane
    /// whose minute of requests is spent holds the next request until the minute passes, said as the request waits.
    /// </summary>
    [Fact]
    public async Task EachRequest_GoesThroughItsLane_SaidWhereItWaits()
    {
        var (gates, waits) = Gates();
        _chat.Fail(new ChatFailure(ChatFailureKind.Transient, "overloaded")).Answer(FakeChat.Said("done"));

        var result = await Ran(Run() with { Gates = gates, Entries = LaneEntries(new AgentClientConfig { RequestsPerMinute = 1 }) });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal([TimeSpan.FromMinutes(1)], waits);
        Assert.Contains(_events.All.OfType<WaitingToRetry>(), wait => wait.Cause == $"the request waits for {ClientGate.RequestsSpent}" && wait.Delay == TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The runner hands its gate to a client that paces its own requests and holds it in no lane of its own: the loop's
    /// request enters the lane of its provider and key, the lane's cool-down said as the request's wait - never the run's.
    /// </summary>
    [Fact]
    public async Task TheRunnersGate_IsHandedToTheLoop_WhichEntersItForEachRequest()
    {
        var (gates, waits) = Gates();
        gates.LaneOf("anthropic", DssHarness.Core.Agents.ClaudeApi.ClaudeApiClient.ClientName, null, new AgentKey("ANTHROPIC_KEY", "sk-first")).RateLimited(TimeSpan.FromSeconds(5));
        _chat.Answer(FakeChat.Said("done"));

        var (result, seen) = await WalkedAsync(gates);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal([TimeSpan.FromSeconds(5)], waits);
        Assert.Equal([$"the request waits for {ClientGate.CoolDown}"], seen.OfType<WaitingToRetry>().Select(wait => wait.Cause));
    }

    /// <summary>
    /// A 429 a request meets sets its lane's cool-down from the wait the provider named, holding every request of the lane
    /// until it lifts - the loop's own next request among them.
    /// </summary>
    [Fact]
    public async Task A429_SetsItsLanesCoolDown()
    {
        var (gates, waits) = Gates();
        _chat.Fail(new ChatFailure(ChatFailureKind.RateLimited, "slow down", RetryAfter: TimeSpan.FromSeconds(5))).Answer(FakeChat.Said("done"));

        var result = await Ran(Run() with { Gates = gates, Entries = LaneEntries(null) });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal([TimeSpan.FromSeconds(5)], waits);
    }

    /// <summary>
    /// A lane held by a cool-down longer than the client's longest wait is the account at its limit, as a 429 naming that
    /// wait is: the run moves to the same model's next key, whose lane is free, and never sends the held one's request.
    /// </summary>
    [Fact]
    public async Task ALaneHeldPastTheLongestWait_IsTheAccountAtItsLimit()
    {
        var (gates, waits) = Gates();
        gates.LaneOf("anthropic", "claude-api", null, new AgentKey("WORK_KEY", "sk-1")).RateLimited(TimeSpan.FromHours(1));
        _chat.Answer(FakeChat.Said("done"));

        var result = await Ran(Run() with { Gates = gates, Entries = LaneEntries(null), Credential = Keys("WORK_KEY", "SPARE_KEY") });

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(["sk-2"], _chat.Requests.Select(request => request.Key!.Value));
        Assert.Empty(waits);
        Assert.Equal(("WORK_KEY", "SPARE_KEY"), (Assert.Single(_events.All.OfType<FallbackStarted>()).FromKey, _events.All.OfType<FallbackStarted>().Single().ToKey));
    }

    /// <summary>
    /// A record lost while a failed agent waits to be tried again stops the wait as it stops a client: the run is not one
    /// to go on with, so it ends at once, failed and naming the record, and the agent is never asked again.
    /// </summary>
    [Fact]
    public async Task ARecordLostWhileARetryWaits_EndsTheWait()
    {
        var asked = 0;
        var failing = new Scripted("one")
        {
            Script = (run, _, _) =>
            {
                asked++;
                return Task.FromResult(new AgentRunResult(AgentRunStatus.Failed, Model: run.Model, Error: "it broke"));
            },
        };

        // The record takes the run's first line and refuses the second: the wait's own.
        var runner = new AgentRunner([failing], new AppendsFailFrom(_harness.FileSystem, 2), Elevation.User, TimeProvider.System);

        var result = await runner.RunAsync(Step([Entry("claude-fable-5-1", "anthropic", "one")]) with { Retries = 1, BackoffSeconds = 900 }, events: null, Token)
            .WaitAsync(TimeSpan.FromSeconds(30), Token);

        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.StartsWith("The step's stored stream '", result.Error, StringComparison.Ordinal);
        Assert.EndsWith($"' could not be written, so the run was stopped: {AppendsFailFrom.Why}", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, asked);
    }

    /// <summary>The real file system, whose appends to a file opened for appending fail from the one numbered <paramref name="failing"/> on.</summary>
    private sealed class AppendsFailFrom(IFileSystem inner, int failing) : PassThroughFileSystem(inner)
    {
        /// <summary>What the disk says once it fails.</summary>
        public const string Why = "the disk went away";

        public override IAppendFile OpenForAppend(string path) => new Appending(base.OpenForAppend(path), failing);

        private sealed class Appending(IAppendFile file, int failing) : IAppendFile
        {
            private int _appended;

            public long Length => file.Length;

            public void ReadExactlyAt(long position, Span<byte> buffer) => file.ReadExactlyAt(position, buffer);

            public void TruncateTo(long length) => file.TruncateTo(length);

            public void AppendFlushed(ReadOnlySpan<byte> bytes)
            {
                if (++_appended >= failing)
                {
                    throw new IOException(Why);
                }

                file.AppendFlushed(bytes);
            }

            public void Dispose() => file.Dispose();
        }
    }

    /// <summary>The gates the loop's requests go through, each wait recorded and passing at once on its own clock.</summary>
    private static (ClientGates Gates, List<TimeSpan> Waits) Gates()
    {
        var clock = new ManualClock();
        var waits = new List<TimeSpan>();

        return (new ClientGates(clock, (wait, _) =>
        {
            waits.Add(wait);
            clock.Advance(wait);
            return Task.CompletedTask;
        }), waits);
    }

    /// <summary>The loop's provider's entries with the client's entry <paramref name="client"/>, whose buckets pace the lane.</summary>
    private static AgentEntries LaneEntries(AgentClientConfig? client)
        => new(
            new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Currency = "USD" },
            new AgentModelConfig { Code = FakeChat.Model, MaxOutputTokens = 64000, ContextTokens = 200000, PriceInputMiss = 5, PriceInputHit = 1, PriceOutput = 25 },
            client);

    /// <summary>
    /// Works a step whose order <see cref="AgentStepOrder.Of"/> built from a slot holding two keys of one provider -
    /// its one model's two entries - through the runner, on a client that is the loop over the fake provider.
    /// </summary>
    private async Task<(AgentRunResult Result, IReadOnlyList<AgentEvent> Seen)> WalkedAsync(ClientGates? gates = null)
    {
        var (providers, layout, keys, temp) = AgentStepOrderTests.Ready(["ANTHROPIC_KEY=sk-first", "ANTHROPIC_SECOND=sk-second"]);
        using var owned = temp;
        var order = AgentStepOrder.Of(providers, AgentResolution.Of(providers, "best", null, null, null, null, null)!, layout, keys, toolConfigDirectory: null);

        Assert.Equal([("ANTHROPIC_KEY", true), ("ANTHROPIC_SECOND", true)], order.Select(entry => (entry.Key!, entry.Usable)));

        var work = WorkDirectory(_temp);
        Directory.CreateDirectory(work);
        var runner = new AgentRunner([new LoopClient(Loop(), _chat)], _harness.FileSystem, Elevation.User, TimeProvider.System, gates);
        var seen = new Collecting();
        var result = await runner.RunAsync(Step(order) with { Ground = PolicyGrounds.Of(work, [work], [work], [], [], [], null) }, seen, Token);

        return (result, seen.Seen);
    }

    /// <summary>The claude-api client as the loop over the fake provider: what the runner hands it is what the loop runs.</summary>
    private sealed class LoopClient(ToolLoop loop, FakeChat chat) : IAgentClient
    {
        public string Name => DssHarness.Core.Agents.ClaudeApi.ClaudeApiClient.ClientName;

        public AgentClientCapabilities Capabilities => DssHarness.Core.Agents.ClaudeApi.ClaudeApiClient.Descriptor;

        public Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, CancellationToken cancellationToken)
            => loop.RunAsync(run, events, chat, ToolLoopTerms.Of(Name, run.Entries!), cancellationToken);
    }

    /// <summary>A sink that collects what it is given.</summary>
    private sealed class Collecting : IAgentEventSink
    {
        public List<AgentEvent> Seen { get; } = [];

        public ValueTask EmitAsync(AgentEvent @event)
        {
            Seen.Add(@event);
            return ValueTask.CompletedTask;
        }
    }
}
