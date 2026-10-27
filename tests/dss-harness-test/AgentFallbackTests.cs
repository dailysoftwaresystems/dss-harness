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

        // The balance spent on WORK_KEY covers the later model's entry on it too: said, and passed over.
        var passed = _events.All.OfType<FallbackPassedOver>().Single();

        Assert.Equal(("claude-opus-4-8", "a limit the run remembers covers it"), (passed.Model, passed.Why));
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
        var runner = new AgentRunner([first, second, third], _harness.FileSystem, _harness.Platform, TimeProvider.System);
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
        var runner = new AgentRunner([first, second], _harness.FileSystem, _harness.Platform, TimeProvider.System);
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
        var runner = new AgentRunner([first, third], _harness.FileSystem, _harness.Platform, TimeProvider.System);
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
        var walking = new AgentRunner([other], _harness.FileSystem, _harness.Platform, TimeProvider.System);
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
        var runner = new AgentRunner([other], _harness.FileSystem, _harness.Platform, TimeProvider.System);

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
        var runner = new AgentRunner([first, limited, third], _harness.FileSystem, _harness.Platform, TimeProvider.System);
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

        Assert.Equal("the limit covers it until 2026-10-28 12:00:00Z", passed.Why);
        Assert.Equal("glm-5.3", passed.Model);
    }

    /// <summary>
    /// A message written by a model nobody asked for ends the step: no entry takes over, whatever the order holds.
    /// </summary>
    [Fact]
    public async Task AMessageOfAModelNobodyAskedFor_EndsTheStep()
    {
        var first = new Scripted("claude-api")
        {
            Script = (run, events, _) => Task.FromResult(new AgentRunResult(AgentRunStatus.WrongModel, Model: run.Model, Error: "the step asked for 'claude-fable-5-1' and its turn 4 was written by 'claude-opus-5'")),
        };
        var second = new Scripted("glm") { Script = (run, events, _) => Completed(run, events, "glm-5.3", turns: 1) };
        var runner = new AgentRunner([first, second], _harness.FileSystem, _harness.Platform, TimeProvider.System);

        var result = await runner.RunAsync(Step(order: [Entry("claude-fable-5-1", "anthropic", "claude-api", key: "WORK_KEY"), Entry("glm-5.3", "zai", "glm", key: null)]), events: null, Token);

        Assert.Equal(AgentRunStatus.WrongModel, result.Status);
        Assert.Null(second.Given);
    }

    /// <summary>When a limit lifts, where the provider says: a future one for coverage, a past one for its lifting.</summary>
    private static readonly DateTimeOffset Lifts = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A limit that has not lifted yet, on the day after the one above.</summary>
    private static readonly DateTimeOffset StillLifts = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

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
    private static AgentRunEntry Entry(string model, string provider = "anthropic", string client = "claude-api", string? key = "WORK_KEY", string? effort = null, bool usable = true)
        => new(
            model,
            provider,
            client,
            key,
            effort,
            usable,
            usable ? null : $"no file under '.harness-config/workflows/.env/' declares '{key}'")
        {
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
