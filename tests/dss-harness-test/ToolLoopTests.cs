using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
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
/// The loop the two API clients share, driven by a fake provider: each turn's calls decided and run by the harness's own
/// tools - a denial obeyed, nothing run undecided - the conversation stored whole and sent back as it was, the two ways a
/// turn is cut off, the provider's pause, the retries and their waits, the step's bounds, the handoff turn and a message
/// to an agent at work.
/// </summary>
public sealed class ToolLoopTests : IDisposable
{
    private const string Asked = "the task";

    private readonly TempDirectory _temp = new();
    private readonly HarnessFactory _harness = new();
    private readonly RecordedEvents _events = new();
    private readonly List<IDisposable> _opened = [];
    private readonly List<TimeSpan> _waits = [];
    private readonly FakeChat _chat = new();
    private readonly string _worktree;
    private readonly string _main;
    private readonly string _step;
    private readonly string _sessions;

    public ToolLoopTests()
    {
        // By their real paths, so a temporary root reached through a link (macOS's /var) says nothing of the links here.
        string Real(string name) => _harness.FileSystem.ResolveLinks(Directory.CreateDirectory(_temp.Combine(name)).FullName);

        _worktree = Real("worktree");
        _main = Real("main");
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
    /// The calls a turn makes run as the policy lets them, and one it denies is refused with its rule's sentence, which the
    /// agent reads beside the other's answer in the request that follows; the denial is recorded as every denial is.
    /// </summary>
    [Fact]
    public async Task TheCallsThePolicyLets_Run_ADenialIsObeyed_AndTheAgentReadsBoth()
    {
        Write("a.txt", "alpha");
        Write(Path.Combine("secrets", "key.txt"), "s3cret");
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"), View("c2", "secrets/key.txt"))).Answer(FakeChat.Said("done"));

        var result = await Ran(Run(restrictions: [new AgentRestriction("Read(secrets/**)", AgentPolicySource.Config)]));

        Assert.Equal((AgentRunStatus.Completed, "done", 2, 2, 1), (result.Status, result.FinalText, result.Turns, result.ToolCalls, result.Denials));

        var second = _chat.Requests[1].Messages;
        var (read, denied) = (Answer(second, "c1"), Answer(second, "c2"));
        Assert.True(read.Ok);
        Assert.Contains("alpha", read.Text, StringComparison.Ordinal);
        Assert.False(denied.Ok);
        Assert.Contains("Read(secrets/**)", denied.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", string.Concat(second), StringComparison.Ordinal);

        Assert.Single(File.ReadAllLines(Path.Combine(_step, AgentCallRecords.DenialsFileName)));
        Assert.Single(_events.All.OfType<PermissionDenied>());
        Assert.Equal(["c1", "c2"], _events.All.OfType<ToolCallFinished>().Select(finished => finished.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            [("c1", AgentTools.ViewFile, AgentToolRunner.Harness), ("c2", AgentTools.ViewFile, AgentToolRunner.Harness)],
            _events.All.OfType<ToolCallStarted>().Select(started => (started.Id, started.Tool, started.Runner)));
        Assert.All(
            _events.All.OfType<ToolCallFinished>(),
            finished => Assert.True(_events.All.ToList().FindIndex(@event => @event is ToolCallStarted started && started.Id == finished.Id) < _events.All.ToList().IndexOf(finished)));
    }

    /// <summary>A run given no policy to decide its calls by is never started, and the provider is asked nothing.</summary>
    [Fact]
    public async Task ARunGivenNoPolicy_NeverStarts_AndAsksTheProviderNothing()
    {
        var result = await Ran(Run(ground: false));

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ConfigurationWrong), (result.Status, result.NotStartedBecause));
        Assert.Equal("client 'claude-api' was not started: the step was given no policy to decide its calls by, and no call runs undecided", result.Error);
        Assert.Empty(_chat.Requests);
        Assert.False(Directory.Exists(_sessions));
    }

    /// <summary>A call the policy cannot decide runs nothing: what kept it from being decided is its answer, and its file is never written.</summary>
    [Fact]
    public async Task ACallThePolicyCannotDecide_RunsNothing()
    {
        _chat.Answer(FakeChat.Calling(FakeChat.Call("c1", AgentTools.CreateFile, ("path", "faulty.txt"), ("content", "x")))).Answer(FakeChat.Said("ok"));

        var result = await Ran(Run(), loop: Loop(new FaultsOnResolve(_harness.FileSystem, "faulty.txt")));

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var answer = Answer(_chat.Requests[1].Messages, "c1");
        Assert.False(answer.Ok);
        Assert.Contains("cannot be looked at", answer.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_worktree, "faulty.txt")));
        Assert.Equal(0, result.FilesWritten);
    }

    /// <summary>
    /// The conversation is stored as it was sent and received, one line for each point where the history is whole - a
    /// turn that called tools with their answers - and a step that goes on in it sends it back byte for byte, with what it
    /// began with, never what the step composes now, which a notice says.
    /// </summary>
    [Fact]
    public async Task TheConversation_IsStoredAsItWasSent_AndAStepThatGoesOnSendsItBack()
    {
        Write("a.txt", "alpha");
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said("first"));

        var first = await Ran(Run());

        var lines = File.ReadAllLines(Path.Combine(_sessions, first.Session!, StoredConversation.ConversationFileName));
        Assert.Equal(2, lines.Length);
        Assert.Equal(["user", "assistant", "tool"], Roles(lines[0]));
        Assert.Equal(["assistant"], Roles(lines[1]));

        _chat.Answer(FakeChat.Said("second"));
        var goneOn = Run(userPrompt: "the next task", conversation: new AgentSessionRef("claude-api", "anthropic", first.Session!)) with { SystemPrompt = "another system" };

        var second = await Ran(goneOn);

        Assert.Equal((AgentRunStatus.Completed, first.Session), (second.Status, second.Session));

        var sent = _chat.Requests[2];
        Assert.Equal("system", sent.System);
        Assert.Equal([.. _chat.Requests[1].Messages, FakeChat.Said("first").Message, FakeChat.User("the next task")], sent.Messages);
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text.Contains("goes on with the system prompt it began with", StringComparison.Ordinal));
        Assert.True(_events.All.OfType<SessionStarted>().Last().HandedConversation);
        Assert.Equal(3, File.ReadAllLines(Path.Combine(_sessions, first.Session!, StoredConversation.ConversationFileName)).Length);
    }

    /// <summary>
    /// Where the provider says it began sending the conversation shortened is stored with it, which a notice says once,
    /// and given with every later request - the one asked again after a failure, and a later step's that goes on in it,
    /// among them; the first point holds, and a later one the provider says is let go of.
    /// </summary>
    [Fact]
    public async Task WhereTheProviderBeganSendingTheConversationShortened_IsKept_ForEveryLaterRequest()
    {
        Write("a.txt", "alpha");
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt")))
            .Reply(new ChatPart.Shortened(2), new ChatPart.Failed(new ChatFailure(ChatFailureKind.Transient, "overloaded")))
            .Answer(FakeChat.Calling(View("c2", "a.txt")))
            .Answer(FakeChat.Said("first"));

        var first = await Ran(Run());

        _chat.Reply(new ChatPart.Shortened(4), new ChatPart.Began(FakeChat.Model), new ChatPart.Answered(FakeChat.Said("second")));

        var second = await Ran(Run(userPrompt: "the next task", conversation: new AgentSessionRef("claude-api", "anthropic", first.Session!)));

        Assert.Equal((AgentRunStatus.Completed, AgentRunStatus.Completed), (first.Status, second.Status));
        Assert.Equal([null, null, 2, 2, 2], _chat.Requests.Select(request => request.ShortenedBefore));
        Assert.Equal("{\"before\":2}", File.ReadAllText(Path.Combine(_sessions, first.Session!, StoredConversation.ShortenedFileName)));
        Assert.Equal(
            $"The conversation {first.Session} is sent shortened before message 2 from now on, to keep it within the context of '{FakeChat.Model}'; what is stored stays whole.",
            Assert.Single(_events.All.OfType<Notice>(), notice => notice.Text.Contains("is sent shortened", StringComparison.Ordinal)).Text);
        Assert.Equal(_chat.Requests[3].Messages, _chat.Requests[4].Messages.Take(_chat.Requests[3].Messages.Count));
    }

    /// <summary>
    /// An answer cut off at its length and calling no tool is stored and nudged on, twice at most, and the answer is every
    /// piece of it; a third cut ends the run truncated.
    /// </summary>
    [Fact]
    public async Task AnAnswerCutOff_IsNudgedOnTwice_ThenTheRunIsTruncated()
    {
        _chat.Answer(FakeChat.CutOff("one ")).Answer(FakeChat.CutOff("two ")).Answer(FakeChat.Said("three"));

        var done = await Ran(Run());

        Assert.Equal((AgentRunStatus.Completed, "one two three", 3), (done.Status, done.FinalText, done.Turns));
        Assert.Equal([FakeChat.User(Asked), FakeChat.CutOff("one ").Message, FakeChat.User(ToolLoop.ContinueNudge)], _chat.Requests[1].Messages);
        Assert.Equal(2, _events.All.OfType<MessageSent>().Count(sent => sent.Which == AgentMessageKind.Continue));

        var chat = new FakeChat();
        chat.Answer(FakeChat.CutOff("one")).Answer(FakeChat.CutOff("two")).Answer(FakeChat.CutOff("three"));

        var cut = await Ran(Run(), chat: chat);

        Assert.Equal(AgentRunStatus.Truncated, cut.Status);
        Assert.Equal("the answer was cut off at its length 3 times in a row, at 16000 tokens", cut.Error);
    }

    /// <summary>
    /// A turn cut off while it called a tool never runs its calls and is never stored: it is thrown away and asked again
    /// with room to grow - a quarter of the model's most, then half, then all - and cut at the most it ends the run truncated.
    /// </summary>
    [Fact]
    public async Task ATurnCutOffWhileItCalledATool_IsThrownAway_AndAskedAgainWithRoomToGrow()
    {
        var calling = FakeChat.CutOff(string.Empty, FakeChat.Call("c1", AgentTools.CreateFile, ("path", "made.txt"), ("content", "x")));
        _chat.Answer(calling).Answer(calling).Answer(calling);

        var result = await Ran(Run());

        Assert.Equal(AgentRunStatus.Truncated, result.Status);
        Assert.Equal("the answer was cut off at 64000 tokens while it called a tool, the most a reply of 'claude-opus-5-5' holds", result.Error);
        Assert.Equal([16000L, 32000L, 64000L], _chat.Requests.Select(request => request.MaxOutputTokens));
        Assert.All(_chat.Requests, request => Assert.Equal([FakeChat.User(Asked)], request.Messages));
        Assert.Equal(3, _events.All.OfType<TurnRestarted>().Count());
        Assert.Equal((1, new AgentUsage(300, 30, 0, 0, 0, 0), "max_tokens"), (Assert.Single(_events.All.OfType<TurnFinished>()).Turn, _events.All.OfType<TurnFinished>().Single().Usage, _events.All.OfType<TurnFinished>().Single().StopReason));
        Assert.Empty(_events.All.OfType<ToolCallFinished>());
        Assert.False(File.Exists(Path.Combine(_worktree, "made.txt")));
        Assert.Equal(1, result.Turns);
        Assert.Empty(File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName)));
    }

    /// <summary>Room a turn grew to is kept for the rest of the run: the next request asks for as much.</summary>
    [Fact]
    public async Task RoomATurnGrewTo_IsKeptForTheRestOfTheRun()
    {
        Write("a.txt", "alpha");
        _chat.Answer(FakeChat.CutOff(string.Empty, View("c1", "a.txt"))).Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said("done"));

        var result = await Ran(Run());

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal([16000L, 32000L, 32000L], _chat.Requests.Select(request => request.MaxOutputTokens));
    }

    /// <summary>
    /// A turn the provider paused is stored as it is and sent again with nothing added, as the same turn - no turn of the
    /// step's bound - and ten in a row end the run failed.
    /// </summary>
    [Fact]
    public async Task APausedTurn_IsSentAgainWithNothingAdded_AsTheSameTurn()
    {
        var paused = FakeChat.Said("a", stop: ChatStop.Paused);
        _chat.Answer(paused).Answer(FakeChat.Said("b"));

        var result = await Ran(Run(limits: Limits(maxTurns: 1)));

        Assert.Equal((AgentRunStatus.Completed, "ab", 1), (result.Status, result.FinalText, result.Turns));
        Assert.Equal([FakeChat.User(Asked), paused.Message], _chat.Requests[1].Messages);
        Assert.Equal(new AgentUsage(200, 20, 0, 0, 0, 0), Assert.Single(_events.All.OfType<TurnFinished>()).Usage);
        Assert.Equal(["a", "b"], _events.All.OfType<AssistantMessage>().Select(message => message.Text));
        Assert.Equal(["user", "assistant", "assistant"], Roles(Assert.Single(File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName)))));

        var chat = new FakeChat();

        for (var each = 0; each < ToolLoop.MostPausesInARow; each++)
        {
            chat.Answer(paused);
        }

        var failed = await Ran(Run(), chat: chat);

        Assert.Equal((AgentRunStatus.Failed, "the provider paused the turn 10 times in a row"), (failed.Status, failed.Error));

        Write("a.txt", "alpha");
        var broken = new FakeChat();

        for (var each = 1; each < ToolLoop.MostPausesInARow; each++)
        {
            broken.Answer(paused);
        }

        broken.Answer(FakeChat.Calling(View("c1", "a.txt")));

        for (var each = 1; each < ToolLoop.MostPausesInARow; each++)
        {
            broken.Answer(paused);
        }

        broken.Answer(FakeChat.Said("b"));

        Assert.Equal(AgentRunStatus.Completed, (await Ran(Run(), chat: broken)).Status);

        // Once stored, a paused turn is in the history: it is sent once, never again beside it.
        var stored = new FakeChat();
        stored.Answer(paused).Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said("b"));

        Assert.Equal(AgentRunStatus.Completed, (await Ran(Run(), chat: stored)).Status);
        Assert.Single(stored.Requests[2].Messages, message => message == paused.Message);
    }

    /// <summary>
    /// A request the service fails is asked again after a wait that doubles from two seconds - or the one the provider
    /// names - each wait said; a failure part way throws the partial turn away and asks the same history again.
    /// </summary>
    [Fact]
    public async Task AFailedRequest_IsAskedAgain_AfterAWaitThatDoubles()
    {
        _chat.Fail(new ChatFailure(ChatFailureKind.Transient, "overloaded"))
            .Fail(new ChatFailure(ChatFailureKind.Transient, "cut"), written: "half")
            .Fail(new ChatFailure(ChatFailureKind.RateLimited, "slow down", RetryAfter: TimeSpan.FromSeconds(7)))
            .Answer(FakeChat.Said("ok"));

        var before = DateTimeOffset.UtcNow;

        var result = await Ran(Run());

        var after = DateTimeOffset.UtcNow;
        Assert.Equal((AgentRunStatus.Completed, 3, 1), (result.Status, result.Retries, result.Turns));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7)], _waits);
        Assert.Equal(_waits, _events.All.OfType<WaitingToRetry>().Select(waiting => waiting.Delay));
        Assert.All(_events.All.OfType<WaitingToRetry>(), waiting => Assert.InRange(waiting.Until, before + waiting.Delay, after + waiting.Delay));
        Assert.Equal([(1, 3), (2, 3), (3, 3)], _events.All.OfType<WaitingToRetry>().Select(waiting => (waiting.Attempt, waiting.Of)));
        Assert.Equal("the answer failed part way: cut", Assert.Single(_events.All.OfType<TurnRestarted>()).Why);
        Assert.All(_chat.Requests, request => Assert.Equal([FakeChat.User(Asked)], request.Messages));
    }

    /// <summary>
    /// A service failing past the client's tries ends the run failed, saying so; a rate limit past them, or naming a wait past
    /// the longest, is the account at its limit; every other failure ends the run at once, never asked again.
    /// </summary>
    [Theory]
    [InlineData("transient", "Failed", 3)]
    [InlineData("limited", "RateLimited", 3)]
    [InlineData("longWait", "RateLimited", 1)]
    [InlineData("account", "RateLimited", 1)]
    [InlineData("key", "Failed", 1)]
    [InlineData("declined", "Refused", 1)]
    [InlineData("context", "ContextExhausted", 1)]
    [InlineData("unserved", "NotStarted", 1)]
    [InlineData("fatal", "Failed", 1)]
    public async Task AFailure_EndsTheRunAsItsKindSays(string kind, string status, int requests)
    {
        var failure = kind switch
        {
            "transient" => new ChatFailure(ChatFailureKind.Transient, "down"),
            "limited" => new ChatFailure(ChatFailureKind.RateLimited, "slow down", Lifts: Lifts),
            "longWait" => new ChatFailure(ChatFailureKind.RateLimited, "slow down", RetryAfter: TimeSpan.FromMinutes(10)),
            "account" => new ChatFailure(ChatFailureKind.AccountLimit, "the balance is spent", Lifts: Lifts, BalanceSpent: true),
            "key" => new ChatFailure(ChatFailureKind.KeyRefused, "invalid x-api-key"),
            "declined" => new ChatFailure(ChatFailureKind.Declined, "sensitive", Category: "cyber"),
            "context" => new ChatFailure(ChatFailureKind.ContextFull, "prompt is too long"),
            "unserved" => new ChatFailure(ChatFailureKind.Unserved, "the model is not given this organization"),
            _ => new ChatFailure(ChatFailureKind.Fatal, "the plan does not cover the call"),
        };

        for (var each = 0; each < requests; each++)
        {
            _chat.Fail(failure);
        }

        var result = await Ran(Run(), Terms(maxRetries: 2));

        Assert.Equal(Enum.Parse<AgentRunStatus>(status), result.Status);
        Assert.Equal(requests, _chat.Requests.Count);
        Assert.Equal(kind == "key" ? new[] { "WORK_KEY" } : [], result.KeysRefused);
        Assert.Equal(kind == "declined" ? "cyber" : null, result.DeclinedFor);
        Assert.Equal("sk-1", _chat.Requests[0].Key!.Value);

        switch (kind)
        {
            case "transient":
                Assert.Equal("the provider's service failed past its 2 tries: down", result.Error);
                break;
            case "longWait":
                Assert.Empty(_waits);
                Assert.NotNull(result.Limit!.Lifts);
                break;
            case "limited":
                // A rate limit is the model's on the key; a limit at once is the key's whole.
                Assert.Equal(new AgentAccountLimit(false, Lifts, "WORK_KEY", FakeChat.Model), result.Limit);
                Assert.StartsWith($"the provider reported the account at its limit for '{FakeChat.Model}' on WORK_KEY, an allowance used up until ", result.Error);
                break;
            case "account":
                Assert.Equal(new AgentAccountLimit(true, Lifts, "WORK_KEY"), result.Limit);
                Assert.StartsWith("the provider reported the account at its limit on WORK_KEY, its balance or included usage spent until ", result.Error);
                break;
            case "unserved":
                Assert.Equal((AgentNotStartedBecause.ConfigurationWrong, "client 'claude-api' was not started: the model is not given this organization"), (result.NotStartedBecause, result.Error));
                break;
        }
    }

    /// <summary>A model the provider will not serve the account, met after the run had an answer, fails the step: the run began.</summary>
    [Fact]
    public async Task AModelUnservedAfterAnAnswer_FailsTheStep()
    {
        Write("a.txt", "alpha");
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Fail(new ChatFailure(ChatFailureKind.Unserved, "the model is not given this organization"));

        var result = await Ran(Run());

        Assert.Equal((AgentRunStatus.Failed, null, "the model is not given this organization"), (result.Status, result.NotStartedBecause, result.Error));
    }

    /// <summary>
    /// A request of a step with a model after its own asks that a decline leave it a credit, and a declined turn's credit is
    /// the run's, beside what it was of; a step with no model after its own asks none.
    /// </summary>
    [Fact]
    public async Task ADeclinedTurnsCredit_IsTheRuns_WhereTheStepGoesOn()
    {
        var credit = new AgentFallbackCredit("tok-1", PrefillClaim: true);
        _chat.Answer(FakeChat.Said(string.Empty, stop: ChatStop.Declined, category: "cyber") with { Credit = credit });

        var result = await Ran(Run() with { Fallback = [new AgentFallbackModel("claude-opus-4-8", "anthropic", null, null, UsableHere: true)] });

        Assert.Equal((AgentRunStatus.Refused, "cyber", credit), (result.Status, result.DeclinedFor, result.Credit));
        Assert.Equal("the model declined the request (cyber): it wrote nothing before it declined", result.Error);
        Assert.True(Assert.Single(_chat.Requests).Credit);

        var alone = new FakeChat().Answer(FakeChat.Said("no", stop: ChatStop.Declined));
        var unused = new FakeChat().Answer(FakeChat.Said("no", stop: ChatStop.Declined));

        Assert.Null((await Ran(Run(), chat: alone)).Credit);
        Assert.False(Assert.Single(alone.Requests).Credit);

        await Ran(Run() with { Fallback = [new AgentFallbackModel("claude-opus-4-8", "anthropic", null, null, UsableHere: false)] }, chat: unused);
        Assert.False(Assert.Single(unused.Requests).Credit);
    }

    /// <summary>
    /// A step held to a budget is refused before it starts where its model lacks the price of a kind its provider's every
    /// request is billed by - the cache write of a provider whose requests mark what they send - naming the key to fill.
    /// </summary>
    [Fact]
    public async Task ABudgetOnAModelMissingThePriceOfWhatItsProviderBillsBy_NeverStarts()
    {
        var writes = new FakeChat { Uses = AgentPrices.EveryRequestUses with { CacheWriteTokens = 1 } };

        var result = await Ran(Run(limits: Limits(budget: 5m)), chat: writes);

        Assert.Equal(AgentRunStatus.NotStarted, result.Status);
        Assert.Equal($"client 'claude-api' was not started: no price for '{FakeChat.Model}' (providers 'anthropic', model '{FakeChat.Model}': priceCacheWrite), and a budget is charged with the cost counted from them", result.Error);
        Assert.Empty(writes.Requests);
    }

    /// <summary>A request whose answer goes silent past the step's bound on silence is asked again, and still silent past the tries ends the run stalled.</summary>
    [Fact]
    public async Task ARequestSilentPastItsBound_IsAskedAgain_ThenTheRunIsStalled()
    {
        _chat.Hang().Hang();

        var result = await Ran(Run(limits: Limits(stallSeconds: 1)), Terms(maxRetries: 1));

        Assert.Equal(AgentRunStatus.Stalled, result.Status);
        Assert.Equal("the provider's answer went silent past its 1 tries: nothing of the answer came for 1 s", result.Error);
        Assert.Equal(2, _chat.Requests.Count);
    }

    /// <summary>A step past its time ends timed out, the request under way abandoned.</summary>
    [Fact]
    public async Task AStepPastItsTime_EndsTimedOut()
    {
        _chat.Hang("half");

        var result = await Ran(Run(limits: Limits(maxDuration: TimeSpan.FromMilliseconds(300))));

        Assert.Equal(AgentRunStatus.TimedOut, result.Status);
        Assert.Equal(ToolLoop.PastItsTime, Assert.Single(_events.All.OfType<TurnRestarted>()).Why);
        Assert.Equal((1, "the run ended TimedOut"), (Assert.Single(_events.All.OfType<TurnFinished>()).Turn, _events.All.OfType<TurnFinished>().Single().StopReason));
    }

    /// <summary>The turn bound ends the run before a turn past it is asked: the calls of the last turn it allows still run.</summary>
    [Fact]
    public async Task TheTurnBound_EndsTheRunBeforeATurnPastIt()
    {
        Write("a.txt", "alpha");
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Calling(View("c2", "a.txt")));

        var result = await Ran(Run(limits: Limits(maxTurns: 2)));

        Assert.Equal((AgentRunStatus.MaxTurns, 2, 2), (result.Status, result.Turns, result.ToolCalls));
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal("the step took the 2 turns it may", result.Error);
    }

    /// <summary>
    /// A turn whose request spends the step's budget runs none of its calls: each is answered as not run, the turn stored
    /// with those answers, and the run ends with its budget spent.
    /// </summary>
    [Fact]
    public async Task ATurnThatSpendsTheBudget_RunsNoneOfItsCalls()
    {
        _chat.Answer(FakeChat.Calling(FakeChat.Used(input: 1000), FakeChat.Call("c1", AgentTools.CreateFile, ("path", "made.txt"), ("content", "x"))));

        var result = await Ran(Run(limits: Limits(budget: 0.001m)));

        Assert.Equal(AgentRunStatus.BudgetSpent, result.Status);
        Assert.False(File.Exists(Path.Combine(_worktree, "made.txt")));

        var stored = File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName));
        var answer = JsonNode.Parse(Assert.Single(stored))!.AsArray()[^1]!;
        Assert.Equal((false, AgentToolRuntime.NotRun(ToolLoop.BudgetSpent)), (answer["ok"]!.GetValue<bool>(), answer["content"]!.GetValue<string>()));
        Assert.Equal(0.00525m, result.Usage!.Cost!.Counted);
    }

    /// <summary>A step held to a budget on a model missing a price its cost needs is never started, naming the key to fill (user, 2026-10-26).</summary>
    [Fact]
    public async Task ABudgetOnAModelMissingAPrice_NeverStarts()
    {
        var result = await Ran(Run(limits: Limits(budget: 1)), Terms(priceHit: null));

        Assert.Equal((AgentRunStatus.NotStarted, AgentNotStartedBecause.ConfigurationWrong), (result.Status, result.NotStartedBecause));
        Assert.Equal("client 'claude-api' was not started: no price for 'claude-opus-5-5' (providers 'anthropic', model 'claude-opus-5-5': priceInputHit), and a budget is charged with the cost counted from them", result.Error);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>Without a budget, a model missing a price runs: its cost is unknown and says the key to fill, never zero.</summary>
    [Fact]
    public async Task WithoutABudget_AMissingPrice_LeavesTheCostUnknown()
    {
        _chat.Answer(FakeChat.Said("ok", new AgentUsage(100, 10, 50, 0, 0, 0)));

        var result = await Ran(Run(), Terms(priceHit: null));

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Null(result.Usage!.Cost!.Counted);
        Assert.Equal("no price for 'claude-opus-5-5' (providers 'anthropic', model 'claude-opus-5-5': priceInputHit)", result.Usage.Cost.MissingPrice);
    }

    /// <summary>The hard stop abandons the request under way and stores nothing of it: the conversation ends at its last whole turn.</summary>
    [Fact]
    public async Task TheHardStop_AbandonsTheRequest_AndStoresNothingOfIt()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        _chat.Hang("half", whenHanging: stop.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Loop().RunAsync(Run(), _events, _chat, Terms(), stop.Token));

        var session = _events.All.OfType<SessionStarted>().Single().SessionId!;
        Assert.Empty(File.ReadAllLines(Path.Combine(_sessions, session, StoredConversation.ConversationFileName)));
    }

    /// <summary>
    /// A handoff asked while a request is under way abandons it, throws its partial turn away, and asks the agent for its
    /// handoff in one more message of the same conversation; the answer is the run's handoff.
    /// </summary>
    [Fact]
    public async Task AHandoffAskedWhileARequestIsUnderWay_AbandonsIt_AndAsksTheAgent()
    {
        var run = Run();
        _chat.Hang("half", whenHanging: () => run.Handoff.Ask()).Answer(FakeChat.Said("done: a; left: b"));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "done: a; left: b", 2), (result.Status, result.Handoff!.Answer, result.Turns));
        Assert.Equal(ToolLoop.Pausing, Assert.Single(_events.All.OfType<TurnRestarted>()).Why);
        Assert.Equal([(1, ToolLoop.Pausing), (2, "Done")], _events.All.OfType<TurnFinished>().Select(finished => (finished.Turn, finished.StopReason)));
        Assert.Equal([FakeChat.User(Asked), FakeChat.User(AgentHandoffSignal.Prompt)], _chat.Requests[1].Messages);
        Assert.True(Assert.Single(_events.All.OfType<HandoffAnswered>()).Answered);
        Assert.Single(_events.All.OfType<HandoffAsked>());
    }

    /// <summary>
    /// A handoff asked while a turn's calls run cuts them: a command in flight is ended and answered as interrupted, a call
    /// queued behind it answered as not run - each counted and finished as every call is - the turn stored with those
    /// answers, and the handoff asked next.
    /// </summary>
    [Fact]
    public async Task AHandoffAskedWhileCallsRun_CutsThem_AndStoresTheTurnWhole()
    {
        Write("a.txt", "alpha");
        var run = Run();
        _chat.Answer(FakeChat.Calling(FakeChat.Call("c1", AgentTools.RunCommand, ("program", "tool"), ("args", new JsonArray("go"))), View("c2", "a.txt"))).Answer(FakeChat.Said("left it running"));

        var result = await Ran(run, loop: Loop(processes: new Blocking(() =>
        {
            run.Messages.Post("too late");
            run.Handoff.Ask();
        })));

        Assert.Equal((AgentRunStatus.HandedOff, "left it running", 2), (result.Status, result.Handoff!.Answer, result.ToolCalls));
        Assert.DoesNotContain(_chat.Requests.SelectMany(request => request.Messages), message => message.Contains("too late", StringComparison.Ordinal));
        Assert.Equal(["c1", "c2"], _events.All.OfType<ToolCallFinished>().Select(finished => finished.Id).Order(StringComparer.Ordinal));

        var stored = File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName));
        Assert.Equal(2, stored.Length);
        var messages = JsonNode.Parse(stored[0])!.AsArray().Select(message => message!.ToJsonString()).ToList();
        Assert.Equal((false, AgentToolRuntime.Interrupted(ToolLoop.Pausing)), Answer(messages, "c1"));
        Assert.Equal((false, AgentToolRuntime.NotRun(ToolLoop.Pausing)), Answer(messages, "c2"));
        Assert.Equal(["user", "assistant"], Roles(stored[1]));
    }

    /// <summary>
    /// No tool runs in a handoff turn: each call is refused with the handoff's rule, recorded as every denial is, and the
    /// agent asked again - three answers at most, then the run hands off with none.
    /// </summary>
    [Fact]
    public async Task NoToolRunsInAHandoffTurn_AndThreeAnswersAtMost()
    {
        Write("a.txt", "alpha");
        var run = Run();
        run.Handoff.Ask();
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said("here it is"));

        var answered = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "here it is"), (answered.Status, answered.Handoff!.Answer));
        var refused = Answer(_chat.Requests[1].Messages, "c1");
        Assert.Equal((false, AgentHandoffSignal.ToolRefusal), (refused.Ok, refused.Text));
        Assert.Contains(FakeChat.Calling(View("c1", "a.txt")).Message, _chat.Requests[1].Messages);
        Assert.Equal(AgentHandoffSignal.TurnRule, Assert.Single(_events.All.OfType<PermissionDenied>()).Rule);

        var chat = new FakeChat();
        var again = Run();
        again.Handoff.Ask();
        chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Calling(View("c2", "a.txt"))).Answer(FakeChat.Calling(View("c3", "a.txt")));

        var none = await Ran(again, chat: chat);

        Assert.Equal((AgentRunStatus.HandedOff, null, "the agent gave no handoff in 3 answers"), (none.Status, none.Handoff!.Answer, none.Handoff.WhyNone));
    }

    /// <summary>A handoff not answered within its bound hands off with none, saying why.</summary>
    [Fact]
    public async Task AHandoffNotAnsweredWithinItsBound_HandsOffWithNone()
    {
        var run = Run(limits: Limits(maxDuration: TimeSpan.FromMilliseconds(100))) with { Handoff = new AgentHandoffSignal(TimeSpan.FromMilliseconds(400)) };
        run.Handoff.Ask();
        _chat.Hang("half");

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "the bound passed before the agent answered"), (result.Status, result.Handoff!.WhyNone));
        Assert.Equal(AgentHandoffSignal.BoundPassed, Assert.Single(_events.All.OfType<TurnRestarted>()).Why);
        Assert.Equal(AgentHandoffSignal.BoundPassed, Assert.Single(_events.All.OfType<TurnFinished>()).StopReason);
        Assert.False(Assert.Single(_events.All.OfType<HandoffAnswered>()).Answered);
    }

    /// <summary>A handoff turn is counted against no bound of the step's: a step that took every turn it may still hands off.</summary>
    [Fact]
    public async Task AHandoffTurn_IsCountedAgainstNoBound()
    {
        Write("a.txt", "alpha");
        var run = Run(limits: Limits(maxTurns: 1, budget: 0.001m));
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said("handed"));

        var result = await Ran(run, sink: new Reacting(_events, @event => @event is ToolCallFinished, () => run.Handoff.Ask()));

        Assert.Equal((AgentRunStatus.HandedOff, "handed", 2), (result.Status, result.Handoff!.Answer, result.Turns));
    }

    /// <summary>
    /// A message the engine gives an agent at work lands after the results of the calls it is running, in the same request,
    /// and the earlier request stays a byte-for-byte prefix of the later one; no call is cut for it.
    /// </summary>
    [Fact]
    public async Task AMessageToAnAgentAtWork_LandsAfterTheResultsOfItsCalls()
    {
        Write("a.txt", "alpha");
        var run = Run();
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said("done"));

        var result = await Ran(run, sink: new Reacting(_events, @event => @event is AssistantMessage { Calls.Count: > 0 }, () => run.Messages.Post("the plan changed")));

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        var (first, second) = (_chat.Requests[0].Messages, _chat.Requests[1].Messages);
        Assert.Equal(first, second.Take(first.Count));
        Assert.Equal(FakeChat.User("the plan changed"), second[^1]);
        Assert.True(Answer(second, "c1").Ok);
        Assert.Equal("the plan changed", Assert.Single(_events.All.OfType<MessageSent>(), sent => sent.Which == AgentMessageKind.PlanUpdate).Text);
    }

    /// <summary>A message written while the agent's answer was is given after it, and the answer is not the step's: the step ends on the one that follows.</summary>
    [Fact]
    public async Task AMessageWrittenWhileTheAnswerWas_MakesTheStepEndOnTheAnswerThatFollows()
    {
        var run = Run();
        _chat.Reply((_, _) => PostingThenAnswering(run, FakeChat.Said("my answer"))).Answer(FakeChat.Said("the answer, told"));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.Completed, "the answer, told"), (result.Status, result.FinalText));
        Assert.Equal([FakeChat.User(Asked), FakeChat.Said("my answer").Message, FakeChat.User("the plan changed")], _chat.Requests[1].Messages);
    }

    /// <summary>An answer that does not read against the step's schema is sent back with its problems, twice at most, then the run's output is invalid.</summary>
    [Fact]
    public async Task AnAnswerOutsideItsSchema_IsSentBack_ThenTheOutputIsInvalid()
    {
        var output = new AgentOutput("result", """{ "type": "object", "additionalProperties": false, "required": ["a"], "properties": { "a": { "type": "string" } } }""");
        _chat.Answer(FakeChat.Said("not json")).Answer(FakeChat.Said("""{ "a": "x" }"""));

        var repaired = await Ran(Run(output: output));

        Assert.Equal((AgentRunStatus.Completed, """{"a":"x"}"""), (repaired.Status, repaired.StructuredOutput));
        var repair = Assert.Single(_events.All.OfType<OutputRepair>());
        Assert.Equal(1, repair.Attempt);
        Assert.Equal(StructuredOutputGate.Problems(output, null), repair.Problems);
        Assert.Equal(StructuredOutputGate.Repair(repair.Problems), Assert.Single(_events.All.OfType<MessageSent>(), sent => sent.Which == AgentMessageKind.Repair).Text);
        Assert.StartsWith("Your answer does not read against the schema", FakeChat.ContentOf(_chat.Requests[1].Messages[^1]), StringComparison.Ordinal);

        var chat = new FakeChat();
        chat.Answer(FakeChat.Said("no")).Answer(FakeChat.Said("still no")).Answer(FakeChat.Said("never"));

        var invalid = await Ran(Run(output: output), chat: chat);

        Assert.Equal(AgentRunStatus.InvalidOutput, invalid.Status);
    }

    /// <summary>
    /// A turn written by another model than the one asked for ends the step - at once where the answer begins, its text
    /// never told, or on the turn where it named none before - and the id with a date after it is the same model.
    /// </summary>
    [Fact]
    public async Task ATurnByAnotherModel_EndsTheStep()
    {
        _chat.Answer(FakeChat.Said("dated", model: "claude-opus-5-5-20261001"));
        var dated = await Ran(Run());
        Assert.Equal(AgentRunStatus.Completed, dated.Status);
        Assert.Equal([FakeChat.Model], dated.Usage!.PerModel.Select(part => part.Model));

        var chat = new FakeChat();
        chat.Answer(FakeChat.Said("other", model: "claude-sonnet-5-5"));
        var events = new RecordedEvents();

        var wrong = await Ran(Run(), chat: chat, sink: events);

        Assert.Equal(AgentRunStatus.WrongModel, wrong.Status);
        Assert.Equal("the step asked for 'claude-opus-5-5' and its turn 1 was written by 'claude-sonnet-5-5'; a step's model is its configuration's and the engine's to choose, and neither chose this one", wrong.Error);
        Assert.Empty(events.All.OfType<TextDelta>());
        Assert.Empty(events.All.OfType<UsageReported>());

        var unnamed = new FakeChat();
        unnamed.Reply(new ChatPart.Answered(FakeChat.Said("other", model: "claude-sonnet-5-5")));
        var finished = new RecordedEvents();

        var late = await Ran(Run(), chat: unnamed, sink: finished);

        Assert.Equal(AgentRunStatus.WrongModel, late.Status);
        Assert.Equal("claude-sonnet-5-5", Assert.Single(finished.All.OfType<TurnFinished>()).Model);
        Assert.Equal(["claude-sonnet-5-5"], late.Usage!.PerModel.Select(part => part.Model));

        // What a request of another model read before it was known as one is counted to that model all the same.
        var read = new FakeChat();
        read.Reply(new ChatPart.Used(FakeChat.Used(input: 700, output: 0)), new ChatPart.Began("claude-sonnet-5-5"), new ChatPart.Text("never read"));

        var counted = await Ran(Run(), chat: read);

        Assert.Equal(AgentRunStatus.WrongModel, counted.Status);
        Assert.Equal(("claude-sonnet-5-5", 700), (Assert.Single(counted.Usage!.PerModel).Model, counted.Usage.Total.InputTokens));
    }

    /// <summary>A turn the model declined ends the run refused with what it was of; one its context could not hold ends it out of context.</summary>
    [Theory]
    [InlineData(ChatStop.Declined, "Refused")]
    [InlineData(ChatStop.ContextFull, "ContextExhausted")]
    public async Task ATurnThatEndsTheConversation_EndsTheRun(ChatStop stop, string status)
    {
        _chat.Answer(FakeChat.Said("no", stop: stop, category: "cyber"));

        var result = await Ran(Run());

        Assert.Equal(Enum.Parse<AgentRunStatus>(status), result.Status);
        Assert.Equal(stop == ChatStop.Declined ? "cyber" : null, result.DeclinedFor);
        Assert.Equal((1, FakeChat.Used(), stop.ToString()), (Assert.Single(_events.All.OfType<TurnFinished>()).Turn, _events.All.OfType<TurnFinished>().Single().Usage, _events.All.OfType<TurnFinished>().Single().StopReason));
        Assert.Equal(["no"], _events.All.OfType<AssistantMessage>().Select(message => message.Text));
        Assert.Empty(File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName)));
    }

    /// <summary>
    /// What a run says of itself: the session it began with its tools and its credential's name, each turn numbered and
    /// finished with what it used, the cumulative usage, and the cost counted at the model's prices.
    /// </summary>
    [Fact]
    public async Task ARunSaysWhatItBeganWith_EachTurn_AndWhatItCost()
    {
        Write("a.txt", "alpha");
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said("done"));
        var run = Run() with { Skills = [new AgentSkill("review", "the run's skills", "digest", "review")] };

        var result = await Ran(run);

        var began = Assert.Single(_events.All.OfType<SessionStarted>());
        Assert.Equal(["review"], began.Skills);
        Assert.Equal(AgentCredentialSource.EnvironmentFile, began.Credential!.Kind);
        Assert.Equal([FakeChat.Used(), FakeChat.Used()], _events.All.OfType<UsageReported>().Select(used => used.Turn));
        Assert.Equal(("claude-api", FakeChat.Model, "WORK_KEY", false), (began.Client, began.Model, began.Credential!.Variable, began.HandedConversation));
        Assert.Equal(("anthropic", null, result.Session, FileContentHash.Of(System.Text.Encoding.UTF8.GetBytes("system"))), (began.Provider, began.Effort, began.SessionId, began.SystemPromptHash));
        Assert.Equal(FakeChat.Model, result.Model);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Equal([new AgentModelUsage(FakeChat.Model, new AgentUsage(200, 20, 0, 0, 0, 0))], result.Usage!.PerModel);
        Assert.Contains(AgentTools.ViewFile, began.Tools);
        Assert.Equal([1, 2], _events.All.OfType<TurnStarted>().Select(turn => turn.Turn));
        Assert.Equal([1, 2], _events.All.OfType<TurnFinished>().Select(turn => turn.Turn));
        Assert.Equal(new AgentUsage(200, 20, 0, 0, 0, 0), _events.All.OfType<UsageReported>().Last().Cumulative);
        Assert.Equal(new AgentUsage(200, 20, 0, 0, 0, 0), result.Usage!.Total);
        Assert.Equal(0.0015m, result.Usage.Cost!.Counted);
        Assert.Equal([new AgentServedModel(FakeChat.Model, 1)], result.Served);
        Assert.Equal(FakeChat.User(Asked), _chat.Requests[0].Messages[0]);
    }

    /// <summary>
    /// A conversation is sent with the tools it began with, in the order the runtime offers them - by name - and a step
    /// that goes on in it with other capabilities still sends those, which a notice says.
    /// </summary>
    [Fact]
    public async Task TheToolsAConversationBeganWith_AreSentWithEveryRequest()
    {
        _chat.Answer(FakeChat.Said("first")).Answer(FakeChat.Said("second"));

        var first = await Ran(Run());
        var goneOn = await Ran(Run(conversation: new AgentSessionRef("claude-api", "anthropic", first.Session!), capabilities: [AgentCapability.Read]));

        Assert.Equal(AgentRunStatus.Completed, goneOn.Status);
        Assert.Equal(_chat.Requests[0].Tools, _chat.Requests[1].Tools);
        var names = JsonNode.Parse(_chat.Requests[0].Tools)!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()).ToList();
        Assert.Contains(AgentTools.CreateFile, names);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text.Contains("goes on with the tools it began with", StringComparison.Ordinal));
    }

    /// <summary>The wait before a try doubles from two seconds to a minute, up to a quarter of it taken off; a try before the first is no try.</summary>
    [Fact]
    public void TheWaitBeforeATry_DoublesFromTwoSeconds_ToAMinute()
    {
        Assert.Equal(
            [2, 4, 8, 16, 32, 60, 60],
            Enumerable.Range(1, 7).Select(retry => ToolLoop.Backoff(retry, 0).TotalSeconds));
        Assert.Equal(TimeSpan.FromSeconds(1.5), ToolLoop.Backoff(1, 1));
        Assert.Equal(TimeSpan.FromSeconds(45), ToolLoop.Backoff(40, 1));
        Assert.Equal(TimeSpan.FromSeconds(1.5), ToolLoop.Backoff(1, 2));
        Assert.Equal(TimeSpan.FromSeconds(2), ToolLoop.Backoff(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolLoop.Backoff(0, 0));
        Assert.Equal(16000, ToolLoop.FirstMaxTokens(64000));
        Assert.Equal(1, ToolLoop.FirstMaxTokens(1));
        Assert.Equal(2, ToolLoop.FirstMaxTokens(5));
    }

    /// <summary>
    /// A doubled wait is held to the client's longest; a service that names a wait past it has failed, never waited for,
    /// and a rate limit that names one is the account at its limit.
    /// </summary>
    [Fact]
    public async Task AWait_IsHeldToTheLongest_AndAServiceNamingOnePastItHasFailed()
    {
        _chat.Fail(new ChatFailure(ChatFailureKind.Transient, "down")).Fail(new ChatFailure(ChatFailureKind.Transient, "down")).Fail(new ChatFailure(ChatFailureKind.Transient, "down")).Answer(FakeChat.Said("ok"));

        var held = await Ran(Run(), Terms(maxRetryWait: TimeSpan.FromSeconds(3)));

        Assert.Equal(AgentRunStatus.Completed, held.Status);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], _waits);

        var chat = new FakeChat();
        chat.Fail(new ChatFailure(ChatFailureKind.Transient, "come back later", RetryAfter: TimeSpan.FromSeconds(4)));

        var failed = await Ran(Run(), Terms(maxRetryWait: TimeSpan.FromSeconds(3)), chat: chat);

        Assert.Equal((AgentRunStatus.Failed, "the provider asked for a wait of 00:00:04, past the longest of 00:00:03: come back later"), (failed.Status, failed.Error));
        Assert.Equal(3, _waits.Count);
    }

    /// <summary>
    /// A key the provider refuses is passed over for the next of the run's, told as a fallback to the same model, and the
    /// same request sent again with it - no try of the service's, no wait; the refused key is named in the result.
    /// </summary>
    [Fact]
    public async Task ARefusedKey_IsPassedOverForTheNext_AndTheSameRequestSentAgain()
    {
        _chat.Fail(new ChatFailure(ChatFailureKind.KeyRefused, "invalid x-api-key")).Answer(FakeChat.Said("ok"));
        var run = Run() with { Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1"), new AgentKey("SPARE_KEY", "sk-2")]) };

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.Completed, 0), (result.Status, result.Retries));
        Assert.Equal(["sk-1", "sk-2"], _chat.Requests.Select(request => request.Key!.Value));
        Assert.Equal((1, false, "key refused: the provider refused WORK_KEY: invalid x-api-key"), (_events.All.OfType<FallbackStarted>().Single().Turn, _events.All.OfType<FallbackStarted>().Single().ThrewAway, _events.All.OfType<FallbackStarted>().Single().Why));
        Assert.Equal(_chat.Requests[0].Messages, _chat.Requests[1].Messages);
        Assert.Equal(["WORK_KEY"], result.KeysRefused);
        Assert.Empty(_waits);
        var fallback = Assert.Single(_events.All.OfType<FallbackStarted>());
        Assert.Equal((FakeChat.Model, FakeChat.Model, "WORK_KEY", "SPARE_KEY", "the request sent again"), (fallback.FromModel, fallback.ToModel, fallback.FromKey, fallback.ToKey, fallback.How));

        var chat = new FakeChat();
        chat.Fail(new ChatFailure(ChatFailureKind.KeyRefused, "invalid")).Fail(new ChatFailure(ChatFailureKind.KeyRefused, "invalid"));

        var none = await Ran(run, chat: chat);

        Assert.Equal((AgentRunStatus.Failed, "the provider refused every key the run was given (WORK_KEY, SPARE_KEY): invalid"), (none.Status, none.Error));
        Assert.Equal(["WORK_KEY", "SPARE_KEY"], none.KeysRefused);
    }

    /// <summary>A run the loop cannot sign, keep or serve is never started, saying why, and the provider is asked nothing.</summary>
    [Theory]
    [InlineData("noCredential", "KeyMissing", "the run was given no credential, and every request is signed with a key")]
    [InlineData("noKeys", "KeyMissing", "the run signs in with auth: apiKey, and the slot holds no value for it")]
    [InlineData("noModel", "ConfigurationWrong", "the step names no model, and its client is told the model by its id")]
    [InlineData("noSessions", "ConfigurationWrong", "the run was given no place to keep its conversation")]
    [InlineData("otherEntry", "ConfigurationWrong", "the step runs on 'claude-sonnet-5-5', and the model's entry it was given is 'claude-opus-5-5'")]
    [InlineData("noMaxOutput", "ConfigurationWrong", "the entry of 'claude-opus-5-5' says no maxOutputTokens, and a request asks for at most that many")]
    [InlineData("longSession", "ConfigurationWrong", "the conversation it was handed is named 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', which is no conversation's id: letters, digits, '-' and '_', at most 64")]
    [InlineData("badSession", "ConfigurationWrong", "the conversation it was handed is named '../other', which is no conversation's id: letters, digits, '-' and '_', at most 64")]
    [InlineData("otherClient", "ConfigurationWrong", "the conversation it was handed is client 'claude-code''s, and a conversation is gone on with by the client that began it")]
    [InlineData("search", "ConfigurationWrong", "the run's step has sources to search, and its host was given no web search to search them with")]
    [InlineData("currency", "ConfigurationWrong", "providers 'anthropic' prices its models in EUR, and a budget is counted in USD")]
    public async Task ARunTheLoopCannotSignKeepOrServe_NeverStarts(string kind, string because, string why)
    {
        var run = kind switch
        {
            "noCredential" => Run() with { Credential = null },
            "noKeys" => Run() with { Credential = new AgentCredential(AgentAuth.ApiKey, []) },
            "badSession" => Run(conversation: new AgentSessionRef("claude-api", "anthropic", "../other")),
            "otherClient" => Run(conversation: new AgentSessionRef("claude-code", "anthropic", "s1")),
            "noModel" => Run() with { Model = null },
            "noSessions" => Run() with { SessionsDirectory = null },
            "otherEntry" => Run() with { Model = "claude-sonnet-5-5" },
            "longSession" => Run(conversation: new AgentSessionRef("claude-api", "anthropic", new string('a', 65))),
            "noMaxOutput" => Run(),
            "search" => Run() with { WebSearch = new AgentWebSearch([WebSearchFixtures.Source("alpha", """{ "kind": "json-api", "url": "https://alpha.example.test/?q={query}", "items": "$.items", "title": "{@.title}", "link": "{@.link}" }""")], 8, 60, new Dictionary<string, DssHarness.Core.Web.Search.WebSearchKey>()) },
            _ => Run(limits: Limits(budget: 1)),
        };

        var result = await Ran(run, Terms(currency: kind == "currency" ? "EUR" : "USD", maxOutput: kind == "noMaxOutput" ? 0 : 64000));

        Assert.Equal((AgentRunStatus.NotStarted, Enum.Parse<AgentNotStartedBecause>(because)), (result.Status, result.NotStartedBecause));
        Assert.Equal($"client 'claude-api' was not started: {why}", result.Error);
        Assert.Empty(_chat.Requests);
        Assert.False(Directory.Exists(_sessions));
    }

    /// <summary>No request is sent once the step's budget is spent: an answer that spent it and needs a repair ends the run there.</summary>
    [Fact]
    public async Task NoRequestIsSent_OnceTheBudgetIsSpent()
    {
        var output = new AgentOutput("result", """{ "type": "object", "additionalProperties": false, "required": ["a"], "properties": { "a": { "type": "string" } } }""");
        _chat.Answer(FakeChat.Said("not json", FakeChat.Used(input: 1000)));

        var result = await Ran(Run(limits: Limits(budget: 0.001m), output: output));

        Assert.Equal((AgentRunStatus.BudgetSpent, "the step spent 0.00525 of its 0.001 USD budget"), (result.Status, result.Error));
        Assert.Single(_chat.Requests);
    }

    /// <summary>
    /// What a request that ended without its turn said it used is counted as an answer's is - one that failed, one gone
    /// silent past the stall bound, one the run left - in the run's usage and its cost, and once the budget is spent by
    /// it no request is sent again, a refused key's next among them.
    /// </summary>
    [Fact]
    public async Task WhatARequestWithoutItsTurnUsed_IsCounted_AndSpendsTheBudget()
    {
        _chat.Reply(Using(1000, new ChatPart.Failed(new ChatFailure(ChatFailureKind.Transient, "overloaded")))).Answer(FakeChat.Said("done"));

        var counted = await Ran(Run());

        Assert.Equal((AgentRunStatus.Completed, 1100, 10), (counted.Status, counted.Usage!.Total.InputTokens, counted.Usage.Total.OutputTokens));
        Assert.Equal(2, _events.All.OfType<UsageReported>().Count());
        Assert.Empty(_events.All.OfType<TurnRestarted>());

        var silent = new FakeChat();
        silent.Reply((_, cancellationToken) => Silent(null, cancellationToken)).Answer(FakeChat.Said("done"));

        var stalled = await Ran(Run(limits: Limits(stallSeconds: 1)), chat: silent);

        Assert.Equal((AgentRunStatus.Completed, 1100), (stalled.Status, stalled.Usage!.Total.InputTokens));

        var pausing = Run();
        var left = new FakeChat();
        left.Reply((_, cancellationToken) => Silent(() => pausing.Handoff.Ask(), cancellationToken)).Answer(FakeChat.Said("handed"));

        var handed = await Ran(pausing, chat: left);

        Assert.Equal((AgentRunStatus.HandedOff, 1100), (handed.Status, handed.Usage!.Total.InputTokens));

        var budgeted = new FakeChat();
        budgeted.Reply(Using(1000, new ChatPart.Failed(new ChatFailure(ChatFailureKind.Transient, "overloaded")))).Answer(FakeChat.Said("done"));

        var spent = await Ran(Run(limits: Limits(budget: 0.001m)), chat: budgeted);

        Assert.Equal((AgentRunStatus.BudgetSpent, "the step spent 0.005 of its 0.001 USD budget"), (spent.Status, spent.Error));
        Assert.Single(budgeted.Requests);

        var refused = new FakeChat();
        refused.Reply(Using(1000, new ChatPart.Failed(new ChatFailure(ChatFailureKind.KeyRefused, "invalid")))).Answer(FakeChat.Said("done"));

        var twoKeys = Run(limits: Limits(budget: 0.001m)) with { Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1"), new AgentKey("SPARE_KEY", "sk-2")]) };

        Assert.Equal(AgentRunStatus.BudgetSpent, (await Ran(twoKeys, chat: refused)).Status);
        Assert.Single(refused.Requests);
        Assert.Empty(_events.All.OfType<FallbackStarted>());

        // Begins, says what it used, then says nothing more - doing what then says once it has said it.
        static async IAsyncEnumerable<ChatPart> Silent(Action? then, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatPart.Began(FakeChat.Model);
            yield return new ChatPart.Used(FakeChat.Used(input: 1000, output: 0));
            then?.Invoke();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>
    /// A request whose usage spent the budget leaves the end to the loop's own order - a pause asked meanwhile outranks
    /// the budget - and the handoff turn's requests are sent whatever the budget says.
    /// </summary>
    [Fact]
    public async Task ABudgetSpentByAFailedRequest_KeepsTheLoopsOrder_AndTheHandoffTurnGoesOn()
    {
        var run = Run(limits: Limits(budget: 0.001m));
        _chat.Reply(Using(1000, new ChatPart.Failed(new ChatFailure(ChatFailureKind.Transient, "overloaded")))).Answer(FakeChat.Said("handed"));

        var paused = await Ran(run, sink: new Reacting(_events, @event => @event is UsageReported, () => run.Handoff.Ask()));

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (paused.Status, paused.Handoff!.Answer));

        var handing = Run(limits: Limits(budget: 0.001m));
        var chat = new FakeChat();
        chat.Answer(FakeChat.Calling(FakeChat.Used(input: 1000), View("c1", "a.txt")))
            .Reply(Using(1000, new ChatPart.Failed(new ChatFailure(ChatFailureKind.Transient, "overloaded"))))
            .Answer(FakeChat.Said("handed too"));

        var handed = await Ran(handing, chat: chat, sink: new Reacting(_events, @event => @event is UsageReported, () => handing.Handoff.Ask()));

        Assert.Equal((AgentRunStatus.HandedOff, "handed too", 3), (handed.Status, handed.Handoff!.Answer, chat.Requests.Count));

        // A key refused by the request whose usage spent the budget is never asked again, the handoff turn's among them.
        var refusing = Run(limits: Limits(budget: 0.001m)) with { Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1"), new AgentKey("SPARE_KEY", "sk-2")]) };
        var refused = new FakeChat();
        refused.Reply(Using(1000, new ChatPart.Failed(new ChatFailure(ChatFailureKind.KeyRefused, "invalid")))).Answer(FakeChat.Said("handed with the next"));

        var next = await Ran(refusing, chat: refused, sink: new Reacting(new RecordedEvents(), @event => @event is UsageReported, () => refusing.Handoff.Ask()));

        Assert.Equal((AgentRunStatus.HandedOff, "SPARE_KEY"), (next.Status, refused.Requests[1].Key!.Name));
        Assert.Equal(["WORK_KEY"], next.KeysRefused);
    }

    /// <summary>
    /// A failed request's usage that cannot be priced under a budget ends the run saying so, unless the failure ends it
    /// otherwise: the failure's own end outranks it.
    /// </summary>
    [Fact]
    public async Task AFailedRequestsUncountableUsage_EndsTheRun_UnlessItsFailureDoes()
    {
        var searched = new AgentUsage(100, 0, 0, 0, 0, 1);
        _chat.Reply(Used(searched, new ChatPart.Failed(new ChatFailure(ChatFailureKind.Transient, "overloaded"))));

        var uncounted = await Ran(Run(limits: Limits(budget: 5m)));

        Assert.Equal(AgentRunStatus.Failed, uncounted.Status);
        Assert.StartsWith("the step's cost cannot be counted against its budget:", uncounted.Error, StringComparison.Ordinal);

        var full = new FakeChat();
        full.Reply(Used(searched, new ChatPart.Failed(new ChatFailure(ChatFailureKind.ContextFull, "prompt is too long"))));

        var exhausted = await Ran(Run(limits: Limits(budget: 5m)), chat: full);

        Assert.Equal((AgentRunStatus.ContextExhausted, 100), (exhausted.Status, exhausted.Usage!.Total.InputTokens));

        var fatal = new FakeChat();
        fatal.Reply(Used(FakeChat.Used(input: 300, output: 0), new ChatPart.Failed(new ChatFailure(ChatFailureKind.Fatal, "no"))));

        var failed = await Ran(Run(), chat: fatal);

        Assert.Equal((AgentRunStatus.Failed, 300), (failed.Status, failed.Usage!.Total.InputTokens));

        // A request the run left is left whatever its cost: the handoff goes on, its cost said missing where the run ends.
        var pausing = Run(limits: Limits(budget: 5m));
        var left = new FakeChat();
        left.Reply((_, cancellationToken) => SaidThenSilent(searched, () => pausing.Handoff.Ask(), cancellationToken)).Answer(FakeChat.Said("handed"));

        var handed = await Ran(pausing, chat: left);

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (handed.Status, handed.Handoff!.Answer));
        Assert.NotNull(handed.Usage!.Cost!.MissingPrice);

        static async IAsyncEnumerable<ChatPart> SaidThenSilent(AgentUsage usage, Action then, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatPart.Began(FakeChat.Model);
            yield return new ChatPart.Used(usage);
            then();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>A step whose time and budget have both run out ends past its time: the time is said before the budget.</summary>
    [Fact]
    public async Task TheStepsTime_OutranksItsBudget()
    {
        _chat.Answer(FakeChat.Calling(FakeChat.Used(input: 1000), View("c1", "a.txt")));
        Write("a.txt", "alpha");

        var result = await Ran(
            Run(limits: Limits(budget: 0.001m, maxDuration: TimeSpan.FromMilliseconds(200))),
            sink: new Reacting(_events, @event => @event is UsageReported, () => Thread.Sleep(600)));

        Assert.Equal(AgentRunStatus.TimedOut, result.Status);
        Assert.Single(_chat.Requests);
    }

    /// <summary>A reply that begins, says it used <paramref name="input"/> tokens in, and then ends as <paramref name="last"/> says.</summary>
    private static ChatPart[] Using(long input, ChatPart last) => Used(FakeChat.Used(input: input, output: 0), last);

    /// <summary>A reply that begins, says it used <paramref name="usage"/>, and then ends as <paramref name="last"/> says.</summary>
    private static ChatPart[] Used(AgentUsage usage, ChatPart last) => [new ChatPart.Began(FakeChat.Model), new ChatPart.Used(usage), last];

    /// <summary>
    /// An answer heard of within the stall bound is never stalled, however long it takes: what the provider says of no
    /// part - its keep-alive among it - is the answer heard.
    /// </summary>
    [Fact]
    public async Task AnAnswerHeardOf_IsNeverStalled()
    {
        _chat.Reply((_, cancellationToken) => Heard(FakeChat.Said("done"), cancellationToken));

        var result = await Ran(Run(limits: Limits(stallSeconds: 1)));

        Assert.Equal((AgentRunStatus.Completed, "done"), (result.Status, result.FinalText));
        Assert.Single(_chat.Requests);
        Assert.Empty(_events.All.OfType<WaitingToRetry>());

        static async IAsyncEnumerable<ChatPart> Heard(ChatTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatPart.Began(turn.Model);

            for (var at = 0; at < 5; at++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
                yield return new ChatPart.Heard();
            }

            yield return new ChatPart.Answered(turn);
        }
    }

    /// <summary>An answer to a schema cut off at its length is read whole: its pieces, gone on from, are the JSON.</summary>
    [Fact]
    public async Task AnAnswerToASchemaCutOff_IsReadWhole()
    {
        var output = new AgentOutput("result", """{ "type": "object", "additionalProperties": false, "required": ["a"], "properties": { "a": { "type": "string" } } }""");
        _chat.Answer(FakeChat.CutOff("""{ "a": """)).Answer(FakeChat.Said(""" "x" }"""));

        var result = await Ran(Run(output: output));

        Assert.Equal((AgentRunStatus.Completed, """{"a":"x"}"""), (result.Status, result.StructuredOutput));
        Assert.Empty(_events.All.OfType<OutputRepair>());
    }

    /// <summary>A message written while the agent's answer was makes one more turn, which counts against the step's turns.</summary>
    [Fact]
    public async Task TheTurnAMessageMakes_CountsAgainstTheStepsTurns()
    {
        var run = Run(limits: Limits(maxTurns: 1));
        _chat.Reply((_, _) => PostingThenAnswering(run, FakeChat.Said("my answer")));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.MaxTurns, 1), (result.Status, result.Turns));
        Assert.Single(_chat.Requests);
    }

    /// <summary>
    /// The handoff's question is said after what the engine tells the agent first; a message posted while the agent is
    /// asked is never given it, and its answer cut off at its length is read whole.
    /// </summary>
    [Fact]
    public async Task TheHandoffsQuestion_FollowsItsLead_AndItsAnswerIsReadWhole()
    {
        var run = Run();
        run.Handoff.Ask("the plan changed", lead: "Your work item was withdrawn.");
        run.Messages.Post("too late");
        _chat.Answer(FakeChat.CutOff("done: a; ")).Answer(FakeChat.Said("left: b"));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "done: a; left: b"), (result.Status, result.Handoff!.Answer));
        Assert.Equal(new HandoffAsked("the plan changed", TimeSpan.FromSeconds(30)), Assert.Single(_events.All.OfType<HandoffAsked>()));
        Assert.Equal([FakeChat.User(Asked), FakeChat.User($"Your work item was withdrawn.\n\n{AgentHandoffSignal.Prompt}")], _chat.Requests[0].Messages);
        Assert.DoesNotContain(_chat.Requests.SelectMany(request => request.Messages), message => message.Contains("too late", StringComparison.Ordinal));
        Assert.DoesNotContain(_events.All.OfType<MessageSent>(), sent => sent.Which == AgentMessageKind.PlanUpdate);
        Assert.Equal(AgentMessageKind.Continue, _events.All.OfType<MessageSent>().Last().Which);
    }

    /// <summary>A handoff turn is held to its bound alone: the step's time passing while the agent answers cuts nothing.</summary>
    [Fact]
    public async Task AHandoffTurn_IsHeldToItsBoundAlone()
    {
        var run = Run(limits: Limits(maxDuration: TimeSpan.FromMilliseconds(200)));
        run.Handoff.Ask();
        _chat.Reply((_, cancellationToken) => Late(FakeChat.Said("handed"), TimeSpan.FromMilliseconds(600), cancellationToken));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (result.Status, result.Handoff!.Answer));
    }

    /// <summary>What the provider says of a request beside its answer is told as a notice; a part no provider streams is a defect, which fails the run.</summary>
    [Fact]
    public async Task AProvidersNotice_IsTold_AndAPartItCannotStream_FailsTheRun()
    {
        _chat.Reply(new ChatPart.Began(FakeChat.Model), new ChatPart.Notice("cleared 3 tool results"), new ChatPart.Answered(FakeChat.Said("ok")));

        Assert.Equal(AgentRunStatus.Completed, (await Ran(Run())).Status);
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text == "cleared 3 tool results");

        var chat = new FakeChat();
        chat.Reply(new Strange());

        var defect = await Assert.ThrowsAsync<InvalidOperationException>(() => Loop().RunAsync(Run(), _events, chat, Terms(), Token));
        Assert.Equal("'Strange' is no part a provider streams.", defect.Message);
    }

    /// <summary>
    /// An answer gone on from is let go once the agent calls a tool: its pieces are no part of a later answer, and the
    /// nudges that asked for them no part of a later streak.
    /// </summary>
    [Fact]
    public async Task AnAnswerGoneOnFrom_IsLetGoOnceItCallsATool()
    {
        Write("a.txt", "alpha");
        _chat.Answer(FakeChat.CutOff("a ")).Answer(FakeChat.Calling(View("c1", "a.txt")))
            .Answer(FakeChat.CutOff("b ")).Answer(FakeChat.Calling(View("c2", "a.txt")))
            .Answer(FakeChat.CutOff("c ")).Answer(FakeChat.Said("d"));

        var result = await Ran(Run());

        Assert.Equal((AgentRunStatus.Completed, "c d"), (result.Status, result.FinalText));
    }

    /// <summary>An answer that needed a repair may itself be cut off and nudged on: the nudges of the answer before are let go with it.</summary>
    [Fact]
    public async Task AnAnswerAfterARepair_MayBeNudgedOnAgain()
    {
        _chat.Answer(FakeChat.CutOff("x")).Answer(FakeChat.CutOff("y")).Answer(FakeChat.Said("z"))
            .Answer(FakeChat.CutOff("""{ "a": """)).Answer(FakeChat.Said(""" "b" }"""));

        var result = await Ran(Run(output: Schema));

        Assert.Equal((AgentRunStatus.Completed, """{"a":"b"}"""), (result.Status, result.StructuredOutput));
        Assert.Single(_events.All.OfType<OutputRepair>());
    }

    /// <summary>A rate limit that names no wait is waited out by the doubled wait, held to the longest: the service asked for nothing past it.</summary>
    [Fact]
    public async Task ARateLimitNamingNoWait_IsWaitedOut_HeldToTheLongest()
    {
        var limited = new ChatFailure(ChatFailureKind.RateLimited, "slow down");
        _chat.Fail(limited).Fail(limited).Fail(limited).Answer(FakeChat.Said("ok"));

        var result = await Ran(Run(), Terms(maxRetryWait: TimeSpan.FromSeconds(3)));

        Assert.Equal((AgentRunStatus.Completed, 3), (result.Status, result.Retries));
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], _waits);
    }

    /// <summary>Each wait takes off what the jitter gives it, up to a quarter.</summary>
    [Fact]
    public async Task EachWait_TakesOffWhatTheJitterGives()
    {
        _chat.Fail(new ChatFailure(ChatFailureKind.Transient, "down")).Fail(new ChatFailure(ChatFailureKind.Transient, "down")).Answer(FakeChat.Said("ok"));

        await Ran(Run(), loop: Loop(jitter: () => 1));

        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3)], _waits);
    }

    /// <summary>A stream that ends before its turn did is a failure part way: what it wrote is thrown away, and the request asked again.</summary>
    [Fact]
    public async Task AStreamThatEndsBeforeItsTurn_IsAskedAgain()
    {
        _chat.Reply(new ChatPart.Began(FakeChat.Model), new ChatPart.Text("half")).Answer(FakeChat.Said("ok"));

        var result = await Ran(Run());

        Assert.Equal((AgentRunStatus.Completed, 1), (result.Status, result.Retries));
        Assert.Equal("the answer failed part way: the answer ended before its turn did", Assert.Single(_events.All.OfType<TurnRestarted>()).Why);
    }

    /// <summary>The bound on silence is the longest the answer may go without a part: an answer slower than it in all, but never silent that long, is read whole.</summary>
    [Fact]
    public async Task TheBoundOnSilence_RunsFromEachPart()
    {
        _chat.Reply((_, cancellationToken) => Slowly(FakeChat.Said("abcd"), TimeSpan.FromMilliseconds(400), cancellationToken));

        var result = await Ran(Run(limits: Limits(stallSeconds: 1)));

        Assert.Equal((AgentRunStatus.Completed, "abcd", 0), (result.Status, result.FinalText, result.Retries));
    }

    /// <summary>A file the agent writes is counted and named in the result.</summary>
    [Fact]
    public async Task AFileTheAgentWrites_IsCountedAndNamed()
    {
        _chat.Answer(FakeChat.Calling(FakeChat.Call("c1", AgentTools.CreateFile, ("path", "made.txt"), ("content", "x")))).Answer(FakeChat.Said("done"));

        var result = await Ran(Run());

        Assert.Equal((AgentRunStatus.Completed, 1), (result.Status, result.FilesWritten));
        Assert.Contains(result.Files, file => file.EndsWith("made.txt", StringComparison.Ordinal));
        Assert.Equal("x", File.ReadAllText(Path.Combine(_worktree, "made.txt")));
    }

    /// <summary>
    /// An answer that says nothing and calls nothing is never stored - an empty message is no history a provider takes - and
    /// what was sent before it stays to be sent.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatSaysNothing_IsNeverStored()
    {
        _chat.Answer(FakeChat.Said(string.Empty)).Answer(FakeChat.Said("""{ "a": "x" }"""));

        var result = await Ran(Run(output: Schema));

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(["user", "user"], _chat.Requests[1].Messages.Select(message => JsonNode.Parse(message)!["role"]!.GetValue<string>()));
        Assert.Equal([["user"], ["user", "assistant"]], File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName)).Select(Roles));

        Write("a.txt", "alpha");
        var chat = new FakeChat();
        chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said(string.Empty));

        var quiet = await Ran(Run(), chat: chat);

        Assert.Equal((AgentRunStatus.Completed, string.Empty), (quiet.Status, quiet.FinalText));
        Assert.Single(File.ReadAllLines(Path.Combine(_sessions, quiet.Session!, StoredConversation.ConversationFileName)));
    }

    /// <summary>
    /// What a run says of a secret is masked wherever it says it - a refusal and a failure as they are told, each part of
    /// an answer as it streams, the answer whole, how the run ended, and a handoff.
    /// </summary>
    [Fact]
    public async Task ASecretTheProviderEchoes_IsMaskedWhereverTheRunSaysIt()
    {
        var run = Run() with { Mask = new Masking("sk-1"), Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1"), new AgentKey("SPARE_KEY", "sk-2")]) };
        run = run with { SystemPrompt = "system sk-1", UserPrompt = "the task sk-1" };
        _chat.Fail(new ChatFailure(ChatFailureKind.KeyRefused, "refused sk-1"))
            .Fail(new ChatFailure(ChatFailureKind.Transient, "down sk-1"))
            .Fail(new ChatFailure(ChatFailureKind.Transient, "cut sk-1"), written: "half")
            .Reply(new ChatPart.Began(FakeChat.Model), new ChatPart.Text("text sk-1"), new ChatPart.Thinking("think sk-1"), new ChatPart.Notice("notice sk-1"), new ChatPart.CallInput("c0", "input sk-1"), new ChatPart.Answered(FakeChat.Said("final sk-1") with { Thinking = "deep sk-1" }));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.Completed, "final ***"), (result.Status, result.FinalText));
        Assert.Equal("key refused: the provider refused WORK_KEY: refused ***", Assert.Single(_events.All.OfType<FallbackStarted>()).Why);
        Assert.Equal(["down ***", "cut ***"], _events.All.OfType<WaitingToRetry>().Select(waiting => waiting.Cause));
        Assert.Equal("the answer failed part way: cut ***", Assert.Single(_events.All.OfType<TurnRestarted>()).Why);
        Assert.Equal("system ***", _chat.Requests[0].System);
        Assert.Equal("the task ***", Assert.Single(_events.All.OfType<MessageSent>(), sent => sent.Which == AgentMessageKind.FirstPrompt).Text);
        Assert.Equal(["half", "text ***"], _events.All.OfType<TextDelta>().Select(delta => delta.Text));
        Assert.Equal("think ***", Assert.Single(_events.All.OfType<ThinkingDelta>()).Text);
        Assert.Contains(_events.All.OfType<Notice>(), notice => notice.Text == "notice ***");
        Assert.Equal(new ToolCallInputDelta("c0", "input ***"), Assert.Single(_events.All.OfType<ToolCallInputDelta>()));
        Assert.Equal(("final ***", "deep ***"), (Assert.Single(_events.All.OfType<AssistantMessage>()).Text, _events.All.OfType<AssistantMessage>().Single().Thinking));

        var failing = new FakeChat();
        failing.Fail(new ChatFailure(ChatFailureKind.Fatal, "boom sk-1"));

        Assert.Equal("boom ***", (await Ran(run, chat: failing)).Error);

        var handing = Run() with { Mask = new Masking("sk-1") };
        handing.Handoff.Ask();
        var answering = new FakeChat();
        answering.Answer(FakeChat.Said("handed sk-1"));

        Assert.Equal("handed ***", (await Ran(handing, chat: answering)).Handoff!.Answer);

        var structured = new FakeChat();
        structured.Answer(FakeChat.Said("""{ "a": "sk-1" }"""));

        Assert.Equal("""{"a":"***"}""", (await Ran(Run(output: Schema) with { Mask = new Masking("sk-1") }, chat: structured)).StructuredOutput);
    }

    /// <summary>
    /// A handoff is what the answer says: the notes of an answer held to a schema, else the answer whole - its text where
    /// the step has no schema - and an answer that says nothing is asked again.
    /// </summary>
    [Theory]
    [InlineData("blank", "ok", 2)]
    [InlineData("notes", "done: a", 1)]
    [InlineData("blankNotes", "x", 2)]
    [InlineData("noSchema", """{"notes":"x"}""", 1)]
    public async Task AHandoff_IsWhatTheAnswerSays_AndOneThatSaysNothingIsAskedAgain(string kind, string handed, int asked)
    {
        var run = Run(output: kind is "notes" or "blankNotes" ? Schema : null);
        run.Handoff.Ask();
        _ = kind switch
        {
            "blank" => _chat.Answer(FakeChat.Said("  ")).Answer(FakeChat.Said("ok")),
            "notes" => _chat.Answer(FakeChat.Said("""{"notes":"done: a"}""")),
            "blankNotes" => _chat.Answer(FakeChat.Said("""{"notes":""}""")).Answer(FakeChat.Said("""{"notes":"x"}""")),
            _ => _chat.Answer(FakeChat.Said("""{"notes":"x"}""")),
        };

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, handed), (result.Status, result.Handoff!.Answer));
        Assert.Equal(asked, _events.All.OfType<MessageSent>().Count(sent => sent.Which == AgentMessageKind.HandoffQuestion));
    }

    /// <summary>A handoff turn that ends otherwise than with an answer hands off with none, saying how it ended.</summary>
    [Theory]
    [InlineData("failed", "the handoff turn ended Failed: boom")]
    [InlineData("wrong", "the handoff turn ended WrongModel: the step asked for 'claude-opus-5-5' and its turn 1 was written by 'claude-sonnet-5-5'; a step's model is its configuration's and the engine's to choose, and neither chose this one")]
    [InlineData("wrongLate", "the handoff turn ended WrongModel: the step asked for 'claude-opus-5-5' and its turn 1 was written by 'claude-sonnet-5-5'; a step's model is its configuration's and the engine's to choose, and neither chose this one")]
    [InlineData("declined", "the handoff turn ended Refused: the model declined the request (cyber): no")]
    [InlineData("stuck", "the handoff turn ended Truncated: the answer was cut off at its length 3 times in a row, at 16000 tokens")]
    public async Task AHandoffTurnThatEnds_HandsOffWithNone_SayingHow(string kind, string why)
    {
        var run = Run();
        run.Handoff.Ask();
        _ = kind switch
        {
            "failed" => _chat.Fail(new ChatFailure(ChatFailureKind.Fatal, "boom")),
            "wrong" => _chat.Answer(FakeChat.Said("x", model: "claude-sonnet-5-5")),
            "wrongLate" => _chat.Reply(new ChatPart.Answered(FakeChat.Said("x", model: "claude-sonnet-5-5"))),
            "declined" => _chat.Answer(FakeChat.Said("no", stop: ChatStop.Declined, category: "cyber")),
            _ => _chat.Answer(FakeChat.CutOff("x")).Answer(FakeChat.CutOff("y")).Answer(FakeChat.CutOff("z")),
        };

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, null, why), (result.Status, result.Handoff!.Answer, result.Handoff.WhyNone));
    }

    /// <summary>The pieces of a handoff answer cut off at its length are one answer: they never use up the answers the handoff may take.</summary>
    [Fact]
    public async Task ThePiecesOfAHandoffAnswer_AreOneAnswer()
    {
        Write("a.txt", "alpha");
        var run = Run();
        run.Handoff.Ask();
        _chat.Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.CutOff("x ")).Answer(FakeChat.CutOff("y ")).Answer(FakeChat.Said("z"));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "x y z"), (result.Status, result.Handoff!.Answer));
    }

    /// <summary>A handoff answer gone on from is let go once the agent calls a tool: the handoff is the answer that follows.</summary>
    [Fact]
    public async Task AHandoffAnswerGoneOnFrom_IsLetGoOnceItCallsATool()
    {
        Write("a.txt", "alpha");
        var run = Run();
        run.Handoff.Ask();
        _chat.Answer(FakeChat.CutOff("x ")).Answer(FakeChat.Calling(View("c1", "a.txt"))).Answer(FakeChat.Said("z"));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "z"), (result.Status, result.Handoff!.Answer));
    }

    /// <summary>The handoff turn begins its own streak of pauses: the turn it abandoned counts nothing against it.</summary>
    [Fact]
    public async Task AStreakOfPauses_BeginsAfreshInTheHandoffTurn()
    {
        var run = Run();
        var paused = FakeChat.Said("a", stop: ChatStop.Paused);

        for (var each = 1; each < ToolLoop.MostPausesInARow; each++)
        {
            _chat.Answer(paused);
        }

        _chat.Hang(whenHanging: () => run.Handoff.Ask()).Answer(paused).Answer(FakeChat.Said("handed"));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "ahanded"), (result.Status, result.Handoff!.Answer));
    }

    /// <summary>
    /// An answer cut off before it wrote anything - its room spent on thinking - is thrown away and asked again with room
    /// to grow, never nudged on: there is nothing to go on from, and a message of white space is never kept.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    public async Task AnAnswerCutOffBeforeItWroteAnything_IsAskedAgainWithRoomToGrow(string text)
    {
        _chat.Answer(FakeChat.CutOff(text)).Answer(FakeChat.Said("done"));

        var result = await Ran(Run());

        Assert.Equal((AgentRunStatus.Completed, "done", 1), (result.Status, result.FinalText, result.Turns));
        Assert.Equal([16000L, 32000L], _chat.Requests.Select(request => request.MaxOutputTokens));
        Assert.Equal([FakeChat.User(Asked)], _chat.Requests[1].Messages);
        Assert.Equal("the answer was cut off at 16000 tokens before it wrote anything; asked again with room for 32000", Assert.Single(_events.All.OfType<TurnRestarted>()).Why);
        Assert.DoesNotContain(_events.All.OfType<MessageSent>(), sent => sent.Which == AgentMessageKind.Continue);

        var chat = new FakeChat();
        chat.Answer(FakeChat.CutOff(text)).Answer(FakeChat.CutOff(text)).Answer(FakeChat.CutOff(text));

        var cut = await Ran(Run(), chat: chat);

        Assert.Equal((AgentRunStatus.Truncated, "the answer was cut off at 64000 tokens before it wrote anything, the most a reply of 'claude-opus-5-5' holds"), (cut.Status, cut.Error));
    }

    /// <summary>An answer of nothing but white space is never stored: no provider takes such a message back.</summary>
    [Fact]
    public async Task AnAnswerOfWhiteSpace_IsNeverStored()
    {
        _chat.Answer(FakeChat.Said("\n\n")).Answer(FakeChat.Said("""{ "a": "x" }"""));

        var result = await Ran(Run(output: Schema));

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal([["user"], ["user", "assistant"]], File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName)).Select(Roles));
    }

    /// <summary>The room a turn grows to is the model's most at most, however its doubling falls.</summary>
    [Fact]
    public async Task TheRoomATurnGrowsTo_IsTheModelsMostAtMost()
    {
        var calling = FakeChat.CutOff(string.Empty, FakeChat.Call("c1", AgentTools.CreateFile, ("path", "made.txt"), ("content", "x")));
        _chat.Answer(calling).Answer(calling).Answer(calling);

        var result = await Ran(Run(), Terms(maxOutput: 5));

        Assert.Equal(AgentRunStatus.Truncated, result.Status);
        Assert.Equal([2L, 4L, 5L], _chat.Requests.Select(request => request.MaxOutputTokens));
    }

    /// <summary>An answer gone on from when the handoff is asked is let go: the handoff is the answer to its question alone.</summary>
    [Fact]
    public async Task AnAnswerGoneOnFromWhenTheHandoffIsAsked_IsNoPartOfIt()
    {
        var run = Run();
        _chat.Answer(FakeChat.CutOff("x ")).Answer(FakeChat.Said("handed"));

        var result = await Ran(run, sink: new Reacting(_events, @event => @event is MessageSent { Which: AgentMessageKind.Continue }, () => run.Handoff.Ask()));

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (result.Status, result.Handoff!.Answer));
        Assert.Equal([FakeChat.User(Asked), FakeChat.CutOff("x ").Message, FakeChat.User(ToolLoop.ContinueNudge), FakeChat.User(AgentHandoffSignal.Prompt)], _chat.Requests[1].Messages);
    }

    /// <summary>A turn the provider paused, abandoned by the handoff, is never sent again nor stored: no request goes on from it.</summary>
    [Fact]
    public async Task APausedTurnTheHandoffAbandons_IsNeverSentAgain()
    {
        var run = Run();
        var paused = FakeChat.Said("a", stop: ChatStop.Paused);
        _chat.Answer(paused).Hang(whenHanging: () => run.Handoff.Ask()).Answer(FakeChat.Said("handed"));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (result.Status, result.Handoff!.Answer));
        Assert.Equal([FakeChat.User(Asked), paused.Message], _chat.Requests[1].Messages);
        Assert.Equal([FakeChat.User(Asked), FakeChat.User(AgentHandoffSignal.Prompt)], _chat.Requests[2].Messages);
        Assert.Equal(["user", "user", "assistant"], Roles(Assert.Single(File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName)))));
    }

    /// <summary>A handoff asked while the run waits to ask again ends the wait - or the try after it - and asks the agent.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AHandoffAskedWhileTheRunWaitsToAskAgain_AsksTheAgent(bool waitHolds)
    {
        var run = Run();
        _chat.Fail(new ChatFailure(ChatFailureKind.Transient, "down")).Answer(FakeChat.Said("handed"));
        var loop = Loop(wait: (_, token) =>
        {
            run.Handoff.Ask();

            return waitHolds ? Task.Delay(Timeout.InfiniteTimeSpan, token) : Task.CompletedTask;
        });

        var result = await Ran(run, loop: loop);

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (result.Status, result.Handoff!.Answer));
        Assert.Equal([FakeChat.User(Asked), FakeChat.User(AgentHandoffSignal.Prompt)], _chat.Requests[1].Messages);
    }

    /// <summary>The run's pause outranks its time and its budget: asked as either runs out, the run hands off.</summary>
    [Fact]
    public async Task APauseAsked_OutranksTheStepsTimeAndBudget()
    {
        var late = Run(limits: Limits(maxDuration: TimeSpan.FromMilliseconds(200)));
        _chat.Reply((_, cancellationToken) => AskingLate(late, cancellationToken)).Answer(FakeChat.Said("handed"));

        var handed = await Ran(late);

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (handed.Status, handed.Handoff!.Answer));

        var spent = Run(limits: Limits(budget: 0.001m));
        var chat = new FakeChat();
        chat.Answer(FakeChat.Calling(FakeChat.Used(input: 1000), FakeChat.Call("c1", AgentTools.CreateFile, ("path", "made.txt"), ("content", "x")))).Answer(FakeChat.Said("handed"));
        var events = new RecordedEvents();

        var result = await Ran(spent, chat: chat, sink: new Reacting(events, @event => @event is AssistantMessage { Calls.Count: > 0 }, () => spent.Handoff.Ask()));

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (result.Status, result.Handoff!.Answer));
        Assert.Equal((false, AgentToolRuntime.NotRun(ToolLoop.Pausing)), Answer(chat.Requests[1].Messages, "c1"));
        Assert.False(File.Exists(Path.Combine(_worktree, "made.txt")));
    }

    /// <summary>A step past its time while its calls run cuts them, each answered as interrupted, and ends timed out.</summary>
    [Fact]
    public async Task AStepPastItsTimeWhileCallsRun_CutsThem_AndEndsTimedOut()
    {
        _chat.Answer(FakeChat.Calling(FakeChat.Call("c1", AgentTools.RunCommand, ("program", "tool"), ("args", new JsonArray("go")))));

        var result = await Ran(Run(limits: Limits(maxDuration: TimeSpan.FromMilliseconds(300))), loop: Loop(processes: new Blocking(() => { })));

        Assert.Equal(AgentRunStatus.TimedOut, result.Status);
        var stored = JsonNode.Parse(Assert.Single(File.ReadAllLines(Path.Combine(_sessions, result.Session!, StoredConversation.ConversationFileName))))!.AsArray().Select(message => message!.ToJsonString()).ToList();
        Assert.Equal((false, AgentToolRuntime.Interrupted(ToolLoop.PastItsTime)), Answer(stored, "c1"));
    }

    /// <summary>A cost the model's prices cannot count ends a step held to a budget - which could no longer be kept - but never its handoff.</summary>
    [Fact]
    public async Task ACostThatCannotBeCounted_EndsAStepHeldToABudget_ButNotItsHandoff()
    {
        var unpriced = new AgentUsage(100, 10, 0, 50, 0, 0);
        _chat.Answer(FakeChat.Said("ok", unpriced));

        var failed = await Ran(Run(limits: Limits(budget: 1)));

        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Equal("the step's cost cannot be counted against its budget: no price for 'claude-opus-5-5' (providers 'anthropic', model 'claude-opus-5-5': priceCacheWrite)", failed.Error);
        Assert.Equal("Done", Assert.Single(_events.All.OfType<TurnFinished>()).StopReason);

        var handing = Run(limits: Limits(budget: 1));
        handing.Handoff.Ask();
        var chat = new FakeChat();
        chat.Answer(FakeChat.Said("handed", unpriced));

        var handed = await Ran(handing, chat: chat);

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (handed.Status, handed.Handoff!.Answer));
    }

    /// <summary>A request carries the step's model, its effort, its output and the key it is signed with.</summary>
    [Fact]
    public async Task ARequest_CarriesTheStepsModelEffortOutputAndKey()
    {
        _chat.Answer(FakeChat.Said("""{ "a": "x" }"""));

        await Ran(Run(output: Schema) with { Effort = "high" });

        var sent = Assert.Single(_chat.Requests);
        Assert.Equal((FakeChat.Model, "high", Schema, "WORK_KEY", 16000L), (sent.Model, sent.Effort, sent.Output, sent.Key!.Name, sent.MaxOutputTokens));
    }

    /// <summary>A step past its time between requests asks nothing more: the answer that needed a repair is its last.</summary>
    [Fact]
    public async Task AStepPastItsTimeBetweenRequests_AsksNothingMore()
    {
        _chat.Reply((_, _) => Unheeding(FakeChat.Said("not json"), TimeSpan.FromMilliseconds(400)));

        var result = await Ran(Run(output: Schema, limits: Limits(maxDuration: TimeSpan.FromMilliseconds(200))));

        Assert.Equal((AgentRunStatus.TimedOut, 1), (result.Status, result.Turns));
        Assert.Single(_chat.Requests);
    }

    /// <summary>A handoff's bound passing between its requests asks nothing more: the run hands off with none.</summary>
    [Fact]
    public async Task AHandoffsBoundPassingBetweenItsRequests_AsksNothingMore()
    {
        var run = Run() with { Handoff = new AgentHandoffSignal(TimeSpan.FromMilliseconds(200)) };
        run.Handoff.Ask();
        _chat.Reply((_, _) => Unheeding(FakeChat.Said(" "), TimeSpan.FromMilliseconds(400)));

        var result = await Ran(run);

        Assert.Equal((AgentRunStatus.HandedOff, AgentHandoffSignal.BoundPassed), (result.Status, result.Handoff!.WhyNone));
        Assert.Single(_chat.Requests);
    }

    /// <summary>A key refused after the answer began throws away what it wrote, and says so.</summary>
    [Fact]
    public async Task AKeyRefusedAfterTheAnswerBegan_SaysWhatWasThrownAway()
    {
        _chat.Fail(new ChatFailure(ChatFailureKind.KeyRefused, "invalid"), written: "half").Answer(FakeChat.Said("ok"));
        var run = Run() with { Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1"), new AgentKey("SPARE_KEY", "sk-2")]) };

        var result = await Ran(run);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.True(Assert.Single(_events.All.OfType<FallbackStarted>()).ThrewAway);
        Assert.Equal("the answer failed part way: invalid", Assert.Single(_events.All.OfType<TurnRestarted>()).Why);
    }

    /// <summary>A handoff asked while the calls are cut for the step's time is the run's end: the pause outranks the time.</summary>
    [Fact]
    public async Task AHandoffAskedAsTheCallsAreCutForTime_HandsOff()
    {
        var run = Run(limits: Limits(maxDuration: TimeSpan.FromMilliseconds(300)));
        _chat.Answer(FakeChat.Calling(FakeChat.Call("c1", AgentTools.RunCommand, ("program", "tool"), ("args", new JsonArray("go"))))).Answer(FakeChat.Said("handed"));

        var result = await Ran(run, loop: Loop(processes: new Blocking(() => { })), sink: new Reacting(_events, @event => @event is ToolCallFinished, () => run.Handoff.Ask()));

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (result.Status, result.Handoff!.Answer));
        Assert.Equal((false, AgentToolRuntime.Interrupted(ToolLoop.PastItsTime)), Answer(_chat.Requests[1].Messages, "c1"));
    }

    /// <summary>Calls that begin with the run both pausing and past its time are cut for the pause, which outranks it.</summary>
    [Fact]
    public async Task CallsBegunPausingAndPastTheStepsTime_AreCutForThePause()
    {
        var run = Run(limits: Limits(maxDuration: TimeSpan.FromMilliseconds(200)));
        _chat.Reply((_, _) => Unheeding(FakeChat.Calling(FakeChat.Call("c1", AgentTools.CreateFile, ("path", "made.txt"), ("content", "x"))), TimeSpan.FromMilliseconds(400))).Answer(FakeChat.Said("handed"));

        var result = await Ran(run, sink: new Reacting(_events, @event => @event is AssistantMessage { Calls.Count: > 0 }, () => run.Handoff.Ask()));

        Assert.Equal((AgentRunStatus.HandedOff, "handed"), (result.Status, result.Handoff!.Answer));
        Assert.Equal((false, AgentToolRuntime.NotRun(ToolLoop.Pausing)), Answer(_chat.Requests[1].Messages, "c1"));
        Assert.False(File.Exists(Path.Combine(_worktree, "made.txt")));
    }

    /// <summary>When a limit lifts, where the provider names it.</summary>
    private static readonly DateTimeOffset Lifts = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An answer that comes after <paramref name="after"/> whatever the run asks: a provider slower than the bound it is held to.</summary>
    private static async IAsyncEnumerable<ChatPart> Unheeding(ChatTurn turn, TimeSpan after)
    {
        await Task.Delay(after, CancellationToken.None);

        yield return new ChatPart.Began(turn.Model);
        yield return new ChatPart.Answered(turn);
    }

    private static readonly AgentOutput Schema = new("result", """{ "type": "object", "additionalProperties": false, "required": ["a"], "properties": { "a": { "type": "string" } } }""");

    private static ChatCall View(string id, string path) => FakeChat.Call(id, AgentTools.ViewFile, ("path", path));

    private static async IAsyncEnumerable<ChatPart> Slowly(ChatTurn turn, TimeSpan each, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatPart.Began(turn.Model);

        foreach (var letter in turn.Text)
        {
            await Task.Delay(each, cancellationToken);
            yield return new ChatPart.Text(letter.ToString());
        }

        yield return new ChatPart.Answered(turn);
    }

    /// <summary>A request that asks the run's handoff only after the step's time has run out, then is abandoned.</summary>
    private static async IAsyncEnumerable<ChatPart> AskingLate(AgentRun run, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
        run.Handoff.Ask();
        cancellationToken.ThrowIfCancellationRequested();

        yield break;
    }

    private static async IAsyncEnumerable<ChatPart> Late(ChatTurn turn, TimeSpan after, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(after, cancellationToken);
        yield return new ChatPart.Began(turn.Model);
        yield return new ChatPart.Answered(turn);
    }

    private static AgentLimits Limits(int? maxTurns = null, decimal? budget = null, TimeSpan? maxDuration = null, int? stallSeconds = null)
        => new(maxTurns, budget, maxDuration, stallSeconds);

    private static ToolLoopTerms Terms(double? priceHit = 1, int maxRetries = 3, TimeSpan? maxRetryWait = null, string currency = "USD", long maxOutput = 64000)
        => new(
            "claude-api",
            new AgentProviderConfig { Name = "anthropic", Type = "anthropic", Currency = currency },
            new AgentModelConfig { Code = FakeChat.Model, MaxOutputTokens = maxOutput, ContextTokens = 200000, PriceInputMiss = 5, PriceInputHit = priceHit, PriceOutput = 25 },
            maxRetries,
            maxRetryWait);

    /// <summary>The answer a request gave the call <paramref name="id"/>.</summary>
    private static (bool Ok, string Text) Answer(IReadOnlyList<string> messages, string id)
    {
        var answer = messages.Select(message => JsonNode.Parse(message)!).Single(message => message["role"]!.GetValue<string>() == "tool" && message["id"]!.GetValue<string>() == id);

        return (answer["ok"]!.GetValue<bool>(), answer["content"]!.GetValue<string>());
    }

    /// <summary>The roles of the messages a stored line holds, in order.</summary>
    private static string[] Roles(string line) => [.. JsonNode.Parse(line)!.AsArray().Select(message => message!["role"]!.GetValue<string>())];

    private static async IAsyncEnumerable<ChatPart> PostingThenAnswering(AgentRun run, ChatTurn turn, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatPart.Text(turn.Text);
        run.Messages.Post("the plan changed");
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatPart.Answered(turn);
    }

    private void Write(string relative, string text)
    {
        var full = Path.Combine(_worktree, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private AgentRun Run(
        AgentLimits? limits = null,
        IReadOnlyList<AgentRestriction>? restrictions = null,
        AgentOutput? output = null,
        AgentSessionRef? conversation = null,
        bool ground = true,
        string userPrompt = Asked,
        IReadOnlyList<AgentCapability>? capabilities = null)
        => AgentRuns.Of(_step, _worktree, null, ground ? PolicyGrounds.Of(_worktree, [_main], [_worktree], [], [], restrictions ?? [], capabilities) : null, limits) with
        {
            UserPrompt = userPrompt,
            Output = output,
            Conversation = conversation,
            Model = FakeChat.Model,
            Handoff = new AgentHandoffSignal(TimeSpan.FromSeconds(30)),
            SessionsDirectory = _sessions,
            Credential = new AgentCredential(AgentAuth.ApiKey, [new AgentKey("WORK_KEY", "sk-1")]),
        };

    private ToolLoop Loop(IFileSystem? fileSystem = null, IProcessRunner? processes = null, Func<TimeSpan, CancellationToken, Task>? wait = null, Func<double>? jitter = null)
    {
        var fetch = new HarnessFetch(new RunningToolDouble("1.2.3"), WebNamesDouble.AllPublic, new WebEndpoint(_ => WebEndpoint.Page("page", "text/plain")));
        var files = fileSystem ?? _harness.FileSystem;
        var host = new AgentToolHost(files, _harness.Platform, _harness.GitClient, new PhaseRunner(processes ?? _harness.ProcessRunner, files, _harness.Output), _harness.ChildSessions, fetch, new RunningToolDouble("1.2.3"), TimeProvider.System);
        _opened.Add(fetch);

        return new ToolLoop(
            files,
            host,
            TimeProvider.System,
            (delay, token) =>
            {
                lock (_waits)
                {
                    _waits.Add(delay);
                }

                return wait?.Invoke(delay, token) ?? Task.CompletedTask;
            },
            jitter ?? (() => 0));
    }

    /// <summary>Works <paramref name="run"/> and holds its result together, as the runner would.</summary>
    private async Task<AgentRunResult> Ran(AgentRun run, ToolLoopTerms? terms = null, ToolLoop? loop = null, FakeChat? chat = null, IAgentEventSink? sink = null)
    {
        var result = await (loop ?? Loop()).RunAsync(run, sink ?? _events, chat ?? _chat, terms ?? Terms(), Token);

        Assert.Null(result.Problem());

        return result;
    }

    /// <summary>A part no provider streams.</summary>
    private sealed record Strange : ChatPart;

    /// <summary>A sink that records every event and, at the first that <paramref name="when"/> picks, does <paramref name="then"/>.</summary>
    private sealed class Reacting(RecordedEvents inner, Func<AgentEvent, bool> when, Action then) : IAgentEventSink
    {
        private int _done;

        public async ValueTask EmitAsync(AgentEvent @event)
        {
            await inner.EmitAsync(@event);

            if (when(@event) && Interlocked.Exchange(ref _done, 1) == 0)
            {
                then();
            }
        }
    }

    /// <summary>A file system that cannot look at <paramref name="file"/>: what decides a path's place fails on it.</summary>
    private sealed class FaultsOnResolve(IFileSystem inner, string file) : PassThroughFileSystem(inner)
    {
        public override string ResolveLinks(string path)
            => Path.GetFileName(path) == file ? throw new IOException($"'{path}' cannot be looked at") : base.ResolveLinks(path);
    }

    /// <summary>Processes that run until they are stopped, doing <paramref name="started"/> as each starts.</summary>
    private sealed class Blocking(Action started) : IProcessRunner
    {
        public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            started();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false);
        }

        public string? FindExecutable(string command) => command;
    }
}
