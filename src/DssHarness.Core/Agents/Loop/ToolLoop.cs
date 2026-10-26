using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;

namespace DssHarness.Core.Agents.Loop;

/// <summary>
/// What a run of the loop is held to beyond the run itself: the client it runs as, the model's entry and its provider's -
/// the most a reply holds, and the prices its cost is counted at - and the client's tries.
/// </summary>
/// <param name="Client">The client's key under <c>agentClients</c>, as its records name it.</param>
/// <param name="Provider">The provider's entry: its name and the currency its prices are in.</param>
/// <param name="Model">The model's entry: the most a reply holds, and its prices.</param>
/// <param name="MaxRetries">How many times a failed request is asked again: the client's, <see cref="DefaultMaxRetries"/> where it says none.</param>
/// <param name="MaxRetryWait">
/// The longest wait before a request is asked again: the client's, <see cref="DefaultMaxRetryWait"/> where it says none. A
/// rate limit that names a longer one is the account at its limit, and a service that names a longer one has failed.
/// </param>
public sealed record ToolLoopTerms(
    string Client,
    AgentProviderConfig Provider,
    AgentModelConfig Model,
    int MaxRetries = ToolLoopTerms.DefaultMaxRetries,
    TimeSpan? MaxRetryWait = null)
{
    /// <summary>How many times a failed request is asked again where the client says not: six.</summary>
    public const int DefaultMaxRetries = 6;

    /// <summary>The longest wait before a request is asked again where the client says not: five minutes.</summary>
    public static readonly TimeSpan DefaultMaxRetryWait = TimeSpan.FromSeconds(300);

    /// <summary>The longest wait before a request is asked again.</summary>
    public TimeSpan LongestWait => MaxRetryWait ?? DefaultMaxRetryWait;

    /// <summary>The terms an API client works a run by: its entries' - the provider, the model, and the client's own tries and longest wait.</summary>
    /// <param name="client">The client's key under <c>agentClients</c>.</param>
    /// <param name="entries">The run's entries.</param>
    public static ToolLoopTerms Of(string client, AgentEntries entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return new ToolLoopTerms(
            client,
            entries.Provider,
            entries.Model,
            entries.Client?.MaxRetries ?? DefaultMaxRetries,
            entries.Client?.MaxRetryWaitSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }

    /// <summary>
    /// Why <paramref name="run"/> may not start on an API client of <paramref name="kind"/> - its entries missing, of a
    /// provider the client does not reach, of another provider than the run's, or at no absolute address - and null
    /// where it may.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="kind">The client's kind, as the tool builds it in.</param>
    public static string? Unstartable(AgentRun run, AgentClientKind kind)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(kind);

        return run.Entries is not { } entries ? "the run was given no entries of its provider, model and client, which every request is built from"
            : !string.Equals(entries.Provider.Type, kind.ProviderType, StringComparison.Ordinal) ? $"the run's provider '{entries.Provider.Name}' is of type '{entries.Provider.Type}', and {kind.Name} reaches '{kind.ProviderType}' alone"
            : run.Provider is { } named && !string.Equals(named, entries.Provider.Name, StringComparison.Ordinal) ? $"the run is of provider '{named}', and the entries it was given are provider '{entries.Provider.Name}''s"
            : !Uri.TryCreate(entries.Provider.Api, UriKind.Absolute, out _) ? $"provider '{entries.Provider.Name}' api '{entries.Provider.Api}' is no absolute address"
            : null;
    }
}

/// <summary>
/// The loop the two API clients share (part 03, "The loop for the two APIs"): one request after another over a provider
/// (<see cref="IChatProvider"/>), each turn's calls decided and run by the harness's tool runtime before the agent goes on,
/// and everything that happens told as the run's events. The loop owns the budgets, the policy and the tools, the events,
/// the stored conversation, the keys, the retries and the stops; the provider owns its wire.
/// </summary>
/// <remarks>
/// <para>
/// The conversation is stored as the provider's own messages under the agent's <c>sessions/&lt;id&gt;/</c>
/// (<see cref="StoredConversation"/>), one line for each point where the history is whole again: an agent's message
/// that called tools is stored with the answers to them, and one the provider paused with what goes on from it, so a run
/// cut anywhere leaves a history the provider takes. A message the engine wrote is stored with the turn that answered it;
/// one no turn answered is not stored, and a step that goes on in the conversation says it again. A message of the
/// agent's that says nothing and calls nothing is never stored: an empty message is no history a provider takes.
/// </para>
/// <para>
/// A turn is one answer of the agent's: the request that asks for it, and every request it is asked again by - a turn
/// the provider paused goes on as the same turn, and one thrown away, cut off while it called a tool or failed part way,
/// is asked again as the same turn. Each counts against the step's <c>maxTurns</c> but a handoff turn's. Every request's
/// usage is counted at the model's prices, and no request is sent once the step's budget is spent, but in the handoff
/// turn. An answer cut off at its length, or paused, is gone on from: the answer is every piece of it, in order.
/// </para>
/// <para>
/// No call runs undecided: a run given no policy is never started, and every call is decided by the runtime - its tool
/// among those the step is given, its input held to the tool's schema, its place to the agent's roots and the policy -
/// before it runs, a denial answered with its rule's sentence, which the agent reads.
/// </para>
/// </remarks>
/// <param name="fileSystem">Keeps the conversation and the step's records.</param>
/// <param name="tools">The host each run's tool runtime is opened on.</param>
/// <param name="clock">Times the run's bounds - its duration, a stall, a handoff's bound - and each wait.</param>
/// <param name="wait">Waits before a request is asked again; <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/> on the clock where none is given.</param>
/// <param name="jitter">A number from naught up to one, which takes up to a quarter off each wait; a random one where none is given.</param>
public sealed class ToolLoop(
    IFileSystem fileSystem,
    AgentToolHost tools,
    TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task>? wait = null,
    Func<double>? jitter = null)
{
    /// <summary>What the agent is told when its answer was cut off at its length and called no tool.</summary>
    public const string ContinueNudge = "Your answer was cut off at its length. Go on from exactly where it stopped.";

    /// <summary>How many times in a row an answer cut off at its length is nudged on before the run ends <see cref="AgentRunStatus.Truncated"/>.</summary>
    public const int MostNudges = 2;

    /// <summary>How many paused turns in a row end the run <see cref="AgentRunStatus.Failed"/>.</summary>
    public const int MostPausesInARow = 10;

    /// <summary>Why a turn's calls are cut, and its request abandoned, when the run pauses: what each call it cut is answered.</summary>
    public const string Pausing = "the run is pausing";

    /// <summary>Why a turn's calls are cut, and its request abandoned, when the step ran past its time.</summary>
    public const string PastItsTime = "the step ran past its time";

    /// <summary>Why a turn's calls are answered unrun when the step's budget was spent by the turn that made them.</summary>
    public const string BudgetSpent = "the step's budget is spent";

    /// <summary>The wait before the first try after a failure, before it doubles.</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(2);

    /// <summary>The longest a doubled wait grows to.</summary>
    public static readonly TimeSpan LongestDoubledWait = TimeSpan.FromSeconds(60);

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly AgentToolHost _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? ((delay, token) => Task.Delay(delay, clock, token));
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    /// <summary>
    /// The most a run's first request asks for: a quarter of the most a reply of the model holds, rounded up (user,
    /// 2026-10-26), so an ordinary turn reserves no more of the account's output allowance than it needs; a turn cut off
    /// while it calls a tool doubles it, up to the most, for the rest of the run.
    /// </summary>
    /// <param name="most">The most a reply of the model holds.</param>
    public static long FirstMaxTokens(long most) => Math.Max(1, (most + 3) / 4);

    /// <summary>The wait before the <paramref name="retry"/>th try after a failure: <c>min(60 s, 2 s x 2^(n-1))</c>, up to a quarter of it taken off by <paramref name="jitter"/>.</summary>
    /// <param name="retry">Which try after the failure, from one.</param>
    /// <param name="jitter">A number from naught up to one.</param>
    public static TimeSpan Backoff(int retry, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retry, 1);

        var doubled = FirstWait.TotalSeconds * Math.Pow(2, retry - 1);

        return TimeSpan.FromSeconds(Math.Min(doubled, LongestDoubledWait.TotalSeconds) * (1 - (0.25 * Math.Clamp(jitter, 0, 1))));
    }

    /// <summary>Works <paramref name="run"/> over <paramref name="provider"/>, as <see cref="IAgentClient.RunAsync"/> does.</summary>
    /// <param name="run">The run, resolved.</param>
    /// <param name="events">Where what happens is emitted.</param>
    /// <param name="provider">The provider's side of the loop.</param>
    /// <param name="terms">The client, the model's entry and the tries.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    /// <returns>How the run ended.</returns>
    public async Task<AgentRunResult> RunAsync(AgentRun run, IAgentEventSink events, IChatProvider provider, ToolLoopTerms terms, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(terms);

        var began = _clock.GetTimestamp();

        if (Unstartable(run, terms, provider.Uses) is { } refused)
        {
            return AgentEndings.NotStarted(terms.Client, refused.Because, refused.Why, run.Model, run.Mask);
        }

        var looping = new Looping(this, run, events, provider, terms);
        var result = await looping.RunAsync(cancellationToken).ConfigureAwait(false);

        return result with { Duration = _clock.GetElapsedTime(began) };
    }

    /// <summary>Why the run may not start, and the reason a run that never began gives; null where it may.</summary>
    /// <param name="run">The run.</param>
    /// <param name="terms">The client, the model's entry and the tries.</param>
    /// <param name="uses">What every request of the run's provider is billed by.</param>
    private (AgentNotStartedBecause Because, string Why)? Unstartable(AgentRun run, ToolLoopTerms terms, AgentUsage uses)
        => run.Model is not { Length: > 0 } ? (AgentNotStartedBecause.ConfigurationWrong, AgentEndings.NoModel)
            : run.Ground is not { } ground ? (AgentNotStartedBecause.ConfigurationWrong, AgentEndings.NoPolicy)
            : run.SessionsDirectory is not { Length: > 0 } ? (AgentNotStartedBecause.ConfigurationWrong, "the run was given no place to keep its conversation")
            : !string.Equals(terms.Model.Code, run.Model, StringComparison.Ordinal) ? (AgentNotStartedBecause.ConfigurationWrong, $"the step runs on '{run.Model}', and the model's entry it was given is '{terms.Model.Code}'")
            : terms.Model.MaxOutputTokens < 1 ? (AgentNotStartedBecause.ConfigurationWrong, $"the entry of '{terms.Model.Code}' says no maxOutputTokens, and a request asks for at most that many")
            : run.Conversation is { } handed && !string.Equals(handed.Client, terms.Client, StringComparison.Ordinal) ? (AgentNotStartedBecause.ConfigurationWrong, $"the conversation it was handed is client '{handed.Client}''s, and a conversation is gone on with by the client that began it")
            : run.Conversation is { } named && !OrchestratorLayout.IsSessionId(named.Id) ? (AgentNotStartedBecause.ConfigurationWrong, $"the conversation it was handed is named '{named.Id}', which is no conversation's id: {OrchestratorLayout.SessionIdShape}")
            : run.Credential is null ? (AgentNotStartedBecause.KeyMissing, "the run was given no credential, and every request is signed with a key")
            : run.Credential is { Keys.Count: 0 } credential ? (AgentNotStartedBecause.KeyMissing, AgentEndings.NoKey(credential.Auth))
            : _tools.Unserved(AgentTools.Offered(ground, run.Commands, run.WebSearch)) is { } unserved ? (AgentNotStartedBecause.ConfigurationWrong, unserved)
            : run.Limits.MaxBudgetUsd is not null && AgentPrices.BudgetRefusal(terms.Provider, terms.Model, uses) is { } priceless ? (AgentNotStartedBecause.ConfigurationWrong, priceless)
            : null;

    /// <summary>One request answered, or how the run ends, or that the run's pause or the handoff's bound abandoned it.</summary>
    private sealed record Sent(ChatTurn? Turn = null, AgentRunResult? Ending = null, bool Left = false);

    /// <summary>Why the run can go no further from a turn: how it ends, and why.</summary>
    private sealed record Stuck(AgentRunStatus Status, string Why);

    /// <summary>What became of a turn the agent goes on from: it goes on, or, where <see cref="Stuck"/> says why, it cannot.</summary>
    private sealed record Went(Stuck? Stuck = null)
    {
        /// <summary>The agent goes on.</summary>
        public static readonly Went On = new();
    }

    /// <summary>One run of the loop: its conversation, its counts and its bounds.</summary>
    private sealed class Looping(ToolLoop loop, AgentRun run, IAgentEventSink events, IChatProvider provider, ToolLoopTerms terms)
    {
        // The messages sent and not yet stored: the engine's, and what began the turn under way.
        private readonly List<string> _pending = [];

        // The agent's messages the provider paused, sent again after the pending ones and stored with what goes on from them.
        private readonly List<string> _paused = [];

        private readonly AgentUsageTally _used = new();
        private readonly List<string> _keysRefused = [];
        private readonly IReadOnlyList<AgentKey> _keys = run.Credential!.Keys;
        private readonly CancellationTokenSource _pause = new();
        private readonly string _session = run.Conversation?.Id ?? Guid.NewGuid().ToString("D");

        // Whether the step has a model after this one to go on with: a declined request is to leave it a credit.
        private readonly bool _goesOn = run.Fallback.Any(model => model.UsableHere);

        // The answer so far: each piece cut off at its length or paused that the agent went on from, in order.
        private readonly StringBuilder _said = new();

        private StoredConversation? _conversation;
        private AgentToolRuntime? _runtime;
        private CancellationTokenSource? _deadline;
        private int _keyAt;
        private int _turns;
        private int _counted;
        private int _retries;
        private int _nudges;
        private int _pauses;
        private bool _inTurn;
        private bool _answered;
        private AgentUsage _turnUsage = AgentUsage.None;
        private long _maxTokens = FirstMaxTokens(terms.Model.MaxOutputTokens);
        private decimal _cost;
        private string? _unknown;
        private bool _handing;

        // The gate of the step's answer, where it names an output; and whether the answer it last sent back read as no JSON.
        private readonly StructuredOutputGate? _gate = run.Output is { } output ? new(output, run.Mask) : null;
        private bool _jsonAlone;

        private StoredConversation Conversation => _conversation!;

        private AgentToolRuntime Runtime => _runtime!;

        private CancellationToken Deadline => _deadline!.Token;

        private AgentKey Key => _keys[_keyAt];

        public async Task<AgentRunResult> RunAsync(CancellationToken stop)
        {
            loop._fileSystem.CreateDirectory(run.StepDirectory);

            using var runtime = _runtime = loop._tools.Open(run, events);
            var system = Masked(run.SystemPrompt);
            var tools = provider.Tools(runtime.Tools);
            using var conversation = _conversation = StoredConversation.Open(
                loop._fileSystem,
                OrchestratorLayout.SessionDirectoryIn(run.SessionsDirectory!, _session),
                system,
                tools);
            using var deadline = _deadline = run.Limits.MaxDuration is { } most ? new CancellationTokenSource(most, loop._clock) : new CancellationTokenSource();
            using var pause = _pause;

            // The run's pause abandons the request under way and cuts the calls running: the handoff is asked at once.
            _ = run.Handoff.WhenAsked.ContinueWith(
                _ => _pause.TryCancel(),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            if (run.Conversation is not null)
            {
                // A conversation goes on with what it began with, as every request of it was sent: never the run's own.
                if (!string.Equals(conversation.SystemPrompt, system, StringComparison.Ordinal))
                {
                    await Emit(new Notice($"The conversation {_session} goes on with the system prompt it began with; the step's composes otherwise now.")).ConfigureAwait(false);
                }

                if (!string.Equals(conversation.Tools, tools, StringComparison.Ordinal))
                {
                    await Emit(new Notice($"The conversation {_session} goes on with the tools it began with; the step's compose otherwise now.")).ConfigureAwait(false);
                }
            }

            await Emit(new SessionStarted(
                terms.Client,
                run.Model!,
                FileContentHash.Of(Encoding.UTF8.GetBytes(conversation.SystemPrompt)),
                [.. runtime.Tools.Select(tool => tool.Name)],
                [.. run.Skills.Select(skill => skill.Name)],
                HandedConversation: run.Conversation is not null,
                run.Provider,
                Effort: run.Effort,
                SessionId: _session,
                Credential: new AgentCredentialSource(AgentCredentialSource.EnvironmentFile, Key.Name))).ConfigureAwait(false);

            // A provider whose wire holds no answer to the schema is told it in words, in the step's first message.
            await SayAsync(
                _gate is { } gate && !provider.SendsSchema ? $"{run.UserPrompt}\n\n{AgentEnvelope.InWords(gate.Envelope)}" : run.UserPrompt,
                AgentMessageKind.FirstPrompt).ConfigureAwait(false);

            var result = await LoopAsync(stop).ConfigureAwait(false);

            // A turn the run ended in the middle of ends with it.
            await LeaveTurnAsync($"the run ended {result.Status}").ConfigureAwait(false);

            return Finished(result);
        }

        /// <summary>Takes turns until the run ends: each request answered, its calls run, its answer checked.</summary>
        private async Task<AgentRunResult> LoopAsync(CancellationToken stop)
        {
            while (true)
            {
                if (PauseAsked())
                {
                    return await HandOffAsync(stop).ConfigureAwait(false);
                }

                if (Deadline.IsCancellationRequested)
                {
                    return TimedOut();
                }

                if (run.Limits.MaxBudgetUsd is { } budget && _cost >= budget)
                {
                    return Spent(budget);
                }

                if (!_inTurn)
                {
                    if (run.Limits.MaxTurns is { } most && _counted >= most)
                    {
                        return Ended(AgentRunStatus.MaxTurns, $"the step took the {most} turns it may");
                    }

                    _counted++;
                    await BeginTurnAsync().ConfigureAwait(false);
                }

                var sent = await SendAsync(stop, CancellationToken.None).ConfigureAwait(false);

                if (sent.Left)
                {
                    continue;
                }

                if (sent.Ending is { } ending)
                {
                    return ending;
                }

                var turn = sent.Turn!;

                if (await CountedAsync(turn).ConfigureAwait(false) is { } uncounted)
                {
                    return uncounted;
                }

                if (await GoesOnAsync(turn).ConfigureAwait(false) is { } went)
                {
                    if (went.Stuck is { } stuck)
                    {
                        return Ended(stuck.Status, stuck.Why);
                    }

                    continue;
                }

                switch (turn.Stop)
                {
                    case ChatStop.Done:
                    {
                        await AnsweredAsync(turn).ConfigureAwait(false);
                        var answer = Said(turn);
                        _jsonAlone = false;

                        // The engine's message written while the answer was: the answer is not the step's, and the agent
                        // goes on with what it was told.
                        if (Told() is [_, ..] told)
                        {
                            Store(Kept(turn));
                            await TellAsync(told).ConfigureAwait(false);
                            _pending.AddRange(provider.Answers(turn, [], told));
                            continue;
                        }

                        Store(Kept(turn));

                        if (await CheckedAsync(answer).ConfigureAwait(false) is { } checkedEnd)
                        {
                            return checkedEnd;
                        }

                        continue;
                    }

                    case ChatStop.Tools:
                    {
                        await AnsweredAsync(turn).ConfigureAwait(false);
                        Forget();
                        _jsonAlone = false;

                        // A pause asked before the calls began cuts them before they run, and outranks the budget. What a
                        // cut comes to - the handoff, the step past its time, its budget spent - the loop's top decides,
                        // in that order.
                        var spent = !PauseAsked() && run.Limits.MaxBudgetUsd is { } cap && _cost >= cap;
                        await RunCallsAsync(turn, stop, spent ? BudgetSpent : null).ConfigureAwait(false);
                        continue;
                    }

                    default:
                        await AnsweredAsync(turn).ConfigureAwait(false);
                        return TurnEnding(turn);
                }
            }
        }

        /// <summary>
        /// The handoff turn: the turn under way abandoned, one more message - the handoff's question - and the agent's
        /// answer, no tool run, within the handoff's bound and its answers, counted against none of the step's turns,
        /// budget or time.
        /// </summary>
        private async Task<AgentRunResult> HandOffAsync(CancellationToken stop)
        {
            // The turn under way is abandoned with its request: what it began with is sent with the question, what the
            // provider paused of it is not.
            await LeaveTurnAsync(Pausing).ConfigureAwait(false);
            _handing = true;
            _jsonAlone = false;
            _paused.Clear();
            _pauses = 0;
            Forget();

            var asked = await run.Handoff.WhenAsked.ConfigureAwait(false);
            await Emit(new HandoffAsked(asked.Why, run.Handoff.Bound)).ConfigureAwait(false);

            using var bound = new CancellationTokenSource(run.Handoff.Bound, loop._clock);
            var question = AgentHandoffSignal.Question(asked.Lead);
            var answers = 0;

            await SayAsync(question, AgentMessageKind.HandoffQuestion).ConfigureAwait(false);

            while (true)
            {
                if (bound.IsCancellationRequested)
                {
                    return await HandedAsync(null, AgentHandoffSignal.BoundPassed).ConfigureAwait(false);
                }

                if (!_inTurn)
                {
                    // A turn that goes on from an answer cut off at its length is that answer's, and no other.
                    if (_nudges == 0)
                    {
                        if (answers >= run.Handoff.MaxAnswers)
                        {
                            return await HandedAsync(null, AgentHandoffSignal.NoneIn(answers)).ConfigureAwait(false);
                        }

                        answers++;
                    }

                    await BeginTurnAsync().ConfigureAwait(false);
                }

                var sent = await SendAsync(stop, bound.Token).ConfigureAwait(false);

                if (sent.Left)
                {
                    return await HandedAsync(null, AgentHandoffSignal.BoundPassed).ConfigureAwait(false);
                }

                if (sent.Ending is { } ending)
                {
                    return await HandedAsync(null, AgentHandoffSignal.TurnEnded(ending.Status, ending.Error)).ConfigureAwait(false);
                }

                var turn = sent.Turn!;

                if (await CountedAsync(turn).ConfigureAwait(false) is { } wrong)
                {
                    return await HandedAsync(null, AgentHandoffSignal.TurnEnded(wrong.Status, wrong.Error)).ConfigureAwait(false);
                }

                if (await GoesOnAsync(turn).ConfigureAwait(false) is { } went)
                {
                    if (went.Stuck is { } stuck)
                    {
                        return await HandedAsync(null, AgentHandoffSignal.TurnEnded(stuck.Status, stuck.Why)).ConfigureAwait(false);
                    }

                    continue;
                }

                switch (turn.Stop)
                {
                    case ChatStop.Tools:
                    {
                        // No tool runs in a handoff turn: each call is refused, as every denial is, and the agent asked again.
                        await AnsweredAsync(turn).ConfigureAwait(false);
                        Forget();

                        var refused = new List<ChatAnswer>();

                        foreach (var call in turn.Calls)
                        {
                            var answer = await Runtime.RefuseAsync(
                                new AgentToolCallRequest(call.Id, call.Name, call.Input),
                                new AgentPolicyAnswer.Denied(AgentHandoffSignal.TurnRule, AgentPolicySource.Builtin, AgentHandoffSignal.ToolRefusal)).ConfigureAwait(false);
                            refused.Add(new ChatAnswer(answer.Id, answer.Ok, answer.Text));
                        }

                        Store([turn.Message, .. provider.Answers(turn, refused, [])]);
                        continue;
                    }

                    case ChatStop.Done:
                    {
                        await AnsweredAsync(turn).ConfigureAwait(false);
                        Store(Kept(turn));
                        var text = Said(turn);

                        if (AgentHandoffSignal.AnswerOf(run.Output is null ? null : Parsed(text), text) is { } answer)
                        {
                            return await HandedAsync(answer, null).ConfigureAwait(false);
                        }

                        await SayAsync(question, AgentMessageKind.HandoffQuestion).ConfigureAwait(false);
                        continue;
                    }

                    default:
                    {
                        await AnsweredAsync(turn).ConfigureAwait(false);
                        var ended = TurnEnding(turn);

                        return await HandedAsync(null, AgentHandoffSignal.TurnEnded(ended.Status, ended.Error)).ConfigureAwait(false);
                    }
                }
            }
        }

        /// <summary>The run handed off: the agent's answer, or why there is none, said by the event that ends the handoff turn.</summary>
        private async Task<AgentRunResult> HandedAsync(string? answer, string? whyNone)
        {
            await LeaveTurnAsync(whyNone ?? "the agent answered").ConfigureAwait(false);
            var handed = AgentEndings.HandedOff(answer, whyNone, run.Mask);
            await Emit(HandoffAnswered.Of(handed.Handoff!)).ConfigureAwait(false);

            return handed;
        }

        /// <summary>A turn begins: counted, told, its usage from naught.</summary>
        private async Task BeginTurnAsync()
        {
            _turns++;
            _inTurn = true;
            _turnUsage = AgentUsage.None;
            await Emit(new TurnStarted(_turns)).ConfigureAwait(false);
        }

        /// <summary>
        /// What becomes of a turn the agent goes on from - an answer cut off at its length, a turn cut off while it called a
        /// tool, a turn the provider paused - in every turn alike, the handoff's among them; null for every other turn.
        /// </summary>
        private async Task<Went?> GoesOnAsync(ChatTurn turn)
        {
            _pauses = turn.Stop == ChatStop.Paused ? _pauses + 1 : 0;

            switch (turn.Stop)
            {
                case ChatStop.CutOff when turn.Calls.Count == 0 && Says(turn):
                {
                    // Kept, and gone on from as a turn of its own: the answer is every piece of it.
                    await AnsweredAsync(turn).ConfigureAwait(false);
                    Store(Kept(turn));
                    _said.Append(turn.Text);

                    if (_nudges >= MostNudges)
                    {
                        return new Went(new Stuck(AgentRunStatus.Truncated, $"the answer was cut off at its length {_nudges + 1} times in a row, at {_maxTokens} tokens"));
                    }

                    _nudges++;
                    await SayAsync(ContinueNudge, AgentMessageKind.Continue).ConfigureAwait(false);
                    return Went.On;
                }

                case ChatStop.CutOff:
                {
                    // Never run, never stored: a call no answer follows is a history the provider refuses, and an answer
                    // cut off before it wrote anything - its room spent on thinking - has nothing to go on from.
                    var where = turn.Calls.Count > 0 ? "while it called a tool" : "before it wrote anything";

                    if (Regrown() is not { } larger)
                    {
                        var why = $"the answer was cut off at {_maxTokens} tokens {where}, the most a reply of '{terms.Model.Code}' holds";
                        await Emit(new TurnRestarted(why)).ConfigureAwait(false);
                        await FinishTurnAsync(turn).ConfigureAwait(false);

                        return new Went(new Stuck(AgentRunStatus.Truncated, why));
                    }

                    await Emit(new TurnRestarted($"the answer was cut off at {_maxTokens} tokens {where}; asked again with room for {larger}")).ConfigureAwait(false);
                    _maxTokens = larger;
                    return Went.On;
                }

                case ChatStop.Paused:
                {
                    // Sent again with nothing added, as the same turn - the model goes on where it paused - and stored with
                    // what goes on from it: a paused message is no point where the history is whole.
                    await Emit(Assistant(turn)).ConfigureAwait(false);
                    _paused.Add(turn.Message);
                    _said.Append(turn.Text);

                    return _pauses >= MostPausesInARow
                        ? new Went(new Stuck(AgentRunStatus.Failed, $"the provider paused the turn {_pauses} times in a row"))
                        : Went.On;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// Sends the conversation and reads what it answers, asking again as each failure allows: the turn, or how the run
        /// ends, or that the pause - outside the handoff turn - or the handoff's bound abandoned the request.
        /// </summary>
        /// <param name="stop">The hard stop, which abandons the request and ends the run.</param>
        /// <param name="bound">The handoff's bound, in the handoff turn; none otherwise.</param>
        private async Task<Sent> SendAsync(CancellationToken stop, CancellationToken bound)
        {
            for (var tried = 1; ;)
            {
                if (!_handing && PauseAsked())
                {
                    return new Sent(Left: true);
                }

                var request = new ChatRequest(run.Model!, run.Effort, Conversation.SystemPrompt, Conversation.Tools, [.. Conversation.Messages(), .. _pending, .. _paused], _maxTokens, Key, _gate?.Envelope, Credit: _goesOn, ShortenedBefore: Conversation.ShortenedBefore, JsonAlone: _jsonAlone);

                // The handoff turn is held to its bound, not to the step's time: it counts against none of the step's
                // turns, budget or time.
                using var left = _handing
                    ? CancellationTokenSource.CreateLinkedTokenSource(stop, bound)
                    : CancellationTokenSource.CreateLinkedTokenSource(stop, Deadline, _pause.Token);
                using var stall = new CancellationTokenSource(Timeout.InfiniteTimeSpan, loop._clock);
                using var asking = CancellationTokenSource.CreateLinkedTokenSource(left.Token, stall.Token);
                var parts = 0;
                AgentUsage? soFar = null;
                string? wrote = null;
                ChatTurn? answered = null;
                ChatFailure? failed = null;
                AgentRunResult? other = null;

                try
                {
                    var enumerator = provider.SendAsync(request, asking.Token).GetAsyncEnumerator(asking.Token);

                    await using (enumerator.ConfigureAwait(false))
                    {
                        while (answered is null && failed is null && other is null)
                        {
                            if (run.Limits.StallSeconds is > 0 and var seconds)
                            {
                                stall.CancelAfter(TimeSpan.FromSeconds(seconds));
                            }

                            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                            {
                                failed = new ChatFailure(ChatFailureKind.Transient, "the answer ended before its turn did");
                                break;
                            }

                            // What the answer wrote counts: a turn that failed after it wrote anything is thrown away.
                            switch (enumerator.Current)
                            {
                                case ChatPart.Heard:
                                    break;
                                case ChatPart.Used said:
                                    soFar = said.SoFar;
                                    break;
                                case ChatPart.Began began:
                                    wrote = began.Model;
                                    other = AgentModelIds.Same(run.Model!, began.Model) ? null : WrongModel(began.Model);
                                    break;
                                case ChatPart.Notice notice:
                                    await Emit(new Notice(Masked(notice.Message))).ConfigureAwait(false);
                                    break;
                                case ChatPart.Shortened shortened:
                                    // The first point holds: a provider that says another later is sending what it began with.
                                    if (Conversation.Shorten(shortened.Before))
                                    {
                                        await Emit(new Notice($"The conversation {_session} is sent shortened before message {shortened.Before.ToString(CultureInfo.InvariantCulture)} from now on, to keep it within the context of '{terms.Model.Code}'; what is stored stays whole.")).ConfigureAwait(false);
                                    }

                                    break;
                                case ChatPart.Answered done:
                                    answered = done.Turn;
                                    break;
                                case ChatPart.Failed ended:
                                    failed = ended.Failure;
                                    break;
                                case ChatPart.Text text:
                                    parts++;
                                    await Emit(new TextDelta(Masked(text.Fragment))).ConfigureAwait(false);
                                    break;
                                case ChatPart.Thinking thinking:
                                    parts++;
                                    await Emit(new ThinkingDelta(Masked(thinking.Fragment))).ConfigureAwait(false);
                                    break;
                                case ChatPart.CallBegan call:
                                    parts++;
                                    await Emit(new ToolCallStarted(call.Id, call.Name, AgentToolRunner.Harness)).ConfigureAwait(false);
                                    break;
                                case ChatPart.CallInput input:
                                    parts++;
                                    await Emit(new ToolCallInputDelta(input.Id, Masked(input.Fragment))).ConfigureAwait(false);
                                    break;
                                case var unknown:
                                    throw new InvalidOperationException($"'{unknown?.GetType().Name ?? "null"}' is no part a provider streams.");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested && left.IsCancellationRequested)
                {
                    return await LeftAsync(parts, soFar).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested && stall.IsCancellationRequested)
                {
                    failed = new ChatFailure(ChatFailureKind.Transient, $"nothing of the answer came for {run.Limits.StallSeconds} s");
                }

                if (other is not null)
                {
                    // What it read is billed all the same: counted to the model that wrote, as a turn of another model is.
                    if (soFar is { } read)
                    {
                        await TalliedAsync(wrote!, read).ConfigureAwait(false);
                    }

                    return new Sent(Ending: other);
                }

                if (answered is not null)
                {
                    _answered = true;
                    return new Sent(Turn: answered);
                }

                var failure = failed!;

                if (parts > 0)
                {
                    // A failure part way throws the partial turn away; the same history is asked again.
                    await Emit(new TurnRestarted($"the answer failed part way: {Masked(failure.Message)}")).ConfigureAwait(false);
                }

                // What a request that failed said it used is counted as an answer's is: its input was read, and billed.
                var uncounted = soFar is { } used ? await CountedAsync(used).ConfigureAwait(false) : null;

                // A refused key is passed over for the next, where there is one, and the same request sent again: no try of
                // the service's.
                var passedOver = failure.Kind == ChatFailureKind.KeyRefused && NextKey();

                // The failure's own end outranks a cost that cannot be counted.
                if (!passedOver && Failure(failure, tried, stall.IsCancellationRequested) is { } ending)
                {
                    return new Sent(Ending: ending);
                }

                if (uncounted is not null)
                {
                    return new Sent(Ending: uncounted);
                }

                // No request is sent again once the budget is spent - by what the failed ones used - but the handoff turn's:
                // the loop's top says how the run ends, the pause before the time and the time before the budget.
                if (!_handing && run.Limits.MaxBudgetUsd is { } budget && _cost >= budget)
                {
                    // A refused key is never asked again: a handoff turn asked meanwhile goes with the next.
                    if (passedOver)
                    {
                        _keyAt++;
                    }

                    return new Sent(Left: true);
                }

                if (passedOver)
                {
                    await PassedOverAsync(failure, parts > 0).ConfigureAwait(false);
                    continue;
                }

                // The service's own wait where it names one; else the doubled wait, held to the longest - a named wait past
                // it is the account's limit or the service failed, said above.
                var delay = failure.RetryAfter ?? Waits.Shorter(Backoff(tried, loop._jitter()), terms.LongestWait);
                _retries++;
                await Emit(new WaitingToRetry(Masked(failure.Message), tried, terms.MaxRetries, delay, loop._clock.GetUtcNow() + delay)).ConfigureAwait(false);

                try
                {
                    await loop._wait(delay, left.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested && left.IsCancellationRequested)
                {
                    return await LeftAsync(0, null).ConfigureAwait(false);
                }

                tried++;
            }
        }

        /// <summary>
        /// The request abandoned by the run itself - its pause, its time, the handoff's bound - what it used counted and
        /// what it wrote thrown away. The pause outranks the time: the engine asked for the handoff.
        /// </summary>
        private async Task<Sent> LeftAsync(int parts, AgentUsage? soFar)
        {
            // Left is left: a cost that cannot be counted is said where the run ends, never in place of how.
            if (soFar is { } used)
            {
                await CountedAsync(used).ConfigureAwait(false);
            }

            var timedOut = !PauseAsked() && Deadline.IsCancellationRequested;

            if (parts > 0)
            {
                await Emit(new TurnRestarted(_handing ? AgentHandoffSignal.BoundPassed : timedOut ? PastItsTime : Pausing)).ConfigureAwait(false);
            }

            return timedOut ? new Sent(Ending: TimedOut()) : new Sent(Left: true);
        }

        /// <summary>Records the key the provider refused, and whether the run has one after it.</summary>
        private bool NextKey()
        {
            _keysRefused.Add(Key.Name);

            return _keyAt + 1 < _keys.Count;
        }

        /// <summary>
        /// Passes over the key the provider refused for the next of the run's, told as a fallback to the same model - once
        /// the request is to be sent again with it, nothing ending the run first.
        /// </summary>
        private async Task PassedOverAsync(ChatFailure failure, bool threwAway)
        {
            var refused = Key;
            _keyAt++;
            await Emit(FallbackStarted.KeyRefused(
                run.Model!,
                terms.Provider.Name,
                terms.Client,
                refused.Name,
                Key.Name,
                Masked($"the provider refused {refused.Name}: {failure.Message}"),
                "the request sent again",
                _turns,
                threwAway)).ConfigureAwait(false);
        }

        /// <summary>
        /// How a request's failure ends the run, where it does: one asked again within the client's tries and its longest
        /// wait ends nothing; a rate limit past the tries, or naming a wait past the longest, is the account at its limit -
        /// the model's on the key - and a limit at once is the key's; a service still failing past the tries, or naming
        /// such a wait, ends the run - <see cref="AgentRunStatus.Stalled"/> where it went silent; a model the provider will
        /// not serve the account means a run with no answer yet never began; and every other failure ends it at once.
        /// </summary>
        private AgentRunResult? Failure(ChatFailure failure, int tried, bool silent)
        {
            var said = failure.Message;
            var past = tried > terms.MaxRetries;
            var tooLong = failure.RetryAfter is { } named && named > terms.LongestWait ? $"asked for a wait of {named}, past the longest of {terms.LongestWait}" : null;

            return failure.Kind switch
            {
                ChatFailureKind.RateLimited when past || tooLong is not null => AgentEndings.AtLimit(
                    "the provider",
                    new AgentAccountLimit(BalanceSpent: false, failure.RetryAfter is { } after ? loop._clock.GetUtcNow() + after : failure.Lifts, Key.Name, run.Model),
                    past ? $"still limited after {terms.MaxRetries} tries: {said}" : $"it {tooLong}: {said}",
                    run.Mask),
                ChatFailureKind.Transient when past && silent => Ended(AgentRunStatus.Stalled, $"the provider's answer went silent past its {terms.MaxRetries} tries: {said}"),
                ChatFailureKind.Transient when past => Ended(AgentRunStatus.Failed, $"the provider's service failed past its {terms.MaxRetries} tries: {said}"),
                ChatFailureKind.Transient when tooLong is not null => Ended(AgentRunStatus.Failed, $"the provider {tooLong}: {said}"),
                ChatFailureKind.Transient or ChatFailureKind.RateLimited => null,
                ChatFailureKind.AccountLimit => AgentEndings.AtLimit("the provider", new AgentAccountLimit(failure.BalanceSpent, failure.Lifts, Key.Name), said, run.Mask),
                ChatFailureKind.KeyRefused => Ended(AgentRunStatus.Failed, $"the provider refused every key the run was given ({string.Join(", ", _keysRefused)}): {said}"),
                ChatFailureKind.Declined => AgentEndings.Declined(failure.Category, said, run.Mask),
                ChatFailureKind.ContextFull => Ended(AgentRunStatus.ContextExhausted, said),
                ChatFailureKind.Unserved when !_answered => AgentEndings.NotStarted(terms.Client, AgentNotStartedBecause.ConfigurationWrong, said, run.Model, run.Mask),
                ChatFailureKind.Unserved or ChatFailureKind.Fatal => Ended(AgentRunStatus.Failed, said),
                _ => throw new InvalidOperationException($"'{failure.Kind}' is no failure a provider classes."),
            };
        }

        /// <summary>
        /// Runs a turn's calls and stores the turn with their answers - the engine's messages given after them - as the
        /// point where the history is whole again. Where <paramref name="unrun"/> says why, no call runs, each answered so.
        /// </summary>
        private async Task RunCallsAsync(ChatTurn turn, CancellationToken stop, string? unrun)
        {
            using var cut = new AgentTurnCut();

            if (unrun is not null)
            {
                cut.Cut(unrun);
            }

            using var paused = _pause.Token.Register(() => cut.Cut(Pausing));
            using var late = Deadline.Register(() => cut.Cut(PastItsTime));

            var results = await Runtime.RunTurnAsync([.. turn.Calls.Select(call => new AgentToolCallRequest(call.Id, call.Name, call.Input))], stop, cut).ConfigureAwait(false);
            var answers = results.Select(result => new ChatAnswer(result.Id, result.Ok, result.Text)).ToList();

            // The engine's messages are given with the answers, unless the calls were cut: the step is being left.
            var told = cut.Why is null ? Told() : [];
            await TellAsync(told).ConfigureAwait(false);
            Store([turn.Message, .. provider.Answers(turn, answers, told)]);
        }

        /// <summary>
        /// The answer read by the step's gate, where it names an output: the run's end, or null where a repair was sent - one
        /// that read as no JSON asked again in JSON alone.
        /// </summary>
        private async Task<AgentRunResult?> CheckedAsync(string text)
        {
            if (_gate is not { } gate)
            {
                return new AgentRunResult(AgentRunStatus.Completed, FinalText: Masked(text));
            }

            var answer = Parsed(text);
            _jsonAlone = answer is null;

            return await gate.CheckedAsync(answer, text, events, repair => SayAsync(repair, AgentMessageKind.Repair)).ConfigureAwait(false);
        }

        /// <summary>The answer's text as JSON, where it reads as JSON; null where it does not.</summary>
        private static JsonNode? Parsed(string text)
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Counts what a request used - the run's totals, the model's part, the cost at the model's prices - and holds the
        /// turn to the model asked for: the run's end where another model wrote it, or where a budget's cost cannot be
        /// counted outside the handoff turn.
        /// </summary>
        private async Task<AgentRunResult?> CountedAsync(ChatTurn turn)
        {
            if (!AgentModelIds.Same(run.Model!, turn.Model))
            {
                await TalliedAsync(turn.Model, turn.Usage).ConfigureAwait(false);
                await FinishTurnAsync(turn).ConfigureAwait(false);
                return WrongModel(turn.Model);
            }

            if (await CountedAsync(turn.Usage).ConfigureAwait(false) is not { } uncounted)
            {
                return null;
            }

            await FinishTurnAsync(turn).ConfigureAwait(false);
            return uncounted;
        }

        /// <summary>
        /// Counts what a request of the model asked for used - the run's totals, the model's part, the cost at the model's
        /// prices - and the run's end where a budget's cost cannot be counted outside the handoff turn.
        /// </summary>
        private async Task<AgentRunResult?> CountedAsync(AgentUsage usage)
        {
            await TalliedAsync(run.Model!, usage).ConfigureAwait(false);

            if (AgentPrices.CostOf(terms.Provider, terms.Model, usage) is { } cost)
            {
                _cost += (decimal)cost;
                return null;
            }

            var unknown = AgentPrices.UnknownOf(terms.Provider, terms.Model, usage);
            _unknown ??= unknown;

            return run.Limits.MaxBudgetUsd is null || _handing ? null : Ended(AgentRunStatus.Failed, $"the step's cost cannot be counted against its budget: {unknown}");
        }

        /// <summary>Adds what a request used to the turn's, the run's and <paramref name="model"/>'s, and says it.</summary>
        private async Task TalliedAsync(string model, AgentUsage usage)
        {
            _turnUsage = _turnUsage.Plus(usage);
            _used.Add(model, usage, _turns);
            await Emit(new UsageReported(usage, _used.Total)).ConfigureAwait(false);
        }

        /// <summary>The room a turn cut off while it called a tool is asked again with: double, up to the most; null where it had the most.</summary>
        private long? Regrown() => _maxTokens >= terms.Model.MaxOutputTokens ? null : Math.Min(terms.Model.MaxOutputTokens, _maxTokens * 2);

        /// <summary>The run's end a turn that ends the conversation comes to: declined, with the credit it left where it left one, or out of context.</summary>
        private AgentRunResult TurnEnding(ChatTurn turn)
            => turn.Stop switch
            {
                ChatStop.Declined => AgentEndings.Declined(turn.Category, Says(turn) ? turn.Text : "it wrote nothing before it declined", run.Mask, turn.Credit),
                ChatStop.ContextFull => Ended(AgentRunStatus.ContextExhausted, $"the model's context could hold no more ({turn.StopReason})"),
                _ => throw new InvalidOperationException($"'{turn.Stop}' is no way a turn ends."),
            };

        /// <summary>The whole answer a turn gives: every piece the agent went on from, then the turn's own text; the pieces let go.</summary>
        private string Said(ChatTurn turn)
        {
            var said = _said.Append(turn.Text).ToString();
            Forget();

            return said;
        }

        /// <summary>Lets go of the answer so far: its pieces, and the nudges that asked for them.</summary>
        private void Forget()
        {
            _said.Clear();
            _nudges = 0;
        }

        /// <summary>The messages a turn that calls no tool is stored with: its own, where it says anything.</summary>
        private static IReadOnlyList<string> Kept(ChatTurn turn) => Says(turn) ? [turn.Message] : [];

        /// <summary>Whether a turn's text says anything: a message of nothing but white space is no message a provider takes back.</summary>
        private static bool Says(ChatTurn turn) => !string.IsNullOrWhiteSpace(turn.Text);

        /// <summary>A turn's message whole, and its end: what a turn the run keeps tells.</summary>
        private async Task AnsweredAsync(ChatTurn turn)
        {
            await Emit(Assistant(turn)).ConfigureAwait(false);
            await FinishTurnAsync(turn).ConfigureAwait(false);
        }

        /// <summary>The turn under way ends with <paramref name="turn"/>: what it used, in all.</summary>
        private async Task FinishTurnAsync(ChatTurn turn)
        {
            _inTurn = false;
            await Emit(new TurnFinished(_turns, turn.Model, _turnUsage, turn.StopReason)).ConfigureAwait(false);
        }

        /// <summary>The turn under way, where one is, ends unanswered, for <paramref name="why"/>: what it used, in all.</summary>
        private async Task LeaveTurnAsync(string why)
        {
            if (_inTurn)
            {
                _inTurn = false;
                await Emit(new TurnFinished(_turns, run.Model!, _turnUsage, Masked(why))).ConfigureAwait(false);
            }
        }

        private AssistantMessage Assistant(ChatTurn turn)
            => new(Masked(turn.Text), [.. turn.Calls.Select(call => new AgentToolCall(call.Id, call.Name))], turn.Thinking is null ? null : Masked(turn.Thinking));

        /// <summary>Stores what was sent with <paramref name="messages"/> - the point where the history is whole again - where there is anything.</summary>
        private void Store(IReadOnlyList<string> messages)
        {
            if (_pending.Count + _paused.Count + messages.Count == 0)
            {
                return;
            }

            Conversation.Append([.. _pending, .. _paused, .. messages]);
            _pending.Clear();
            _paused.Clear();
        }

        /// <summary>Every message the engine has posted and the agent not yet been given, in order.</summary>
        private List<string> Told()
        {
            var told = new List<string>();

            while (run.Messages.TryTake(out var message))
            {
                told.Add(message);
            }

            return told;
        }

        /// <summary>Says each message the engine gives the agent, as the event that records it.</summary>
        private async Task TellAsync(IReadOnlyList<string> told)
        {
            foreach (var message in told)
            {
                await Emit(new MessageSent(Masked(message), AgentMessageKind.PlanUpdate)).ConfigureAwait(false);
            }
        }

        /// <summary>A message of the engine's, said as the event that records it and sent with the next request.</summary>
        private async Task SayAsync(string text, AgentMessageKind kind)
        {
            await Emit(new MessageSent(Masked(text), kind)).ConfigureAwait(false);
            _pending.Add(provider.UserMessage(text));
        }

        /// <summary>
        /// Whether the run is pausing: the handoff raised - seen at once, before its own continuation cuts what runs - and
        /// the pause then set.
        /// </summary>
        private bool PauseAsked()
        {
            if (run.Handoff.Asked)
            {
                _pause.TryCancel();
            }

            return _pause.IsCancellationRequested;
        }

        /// <summary>The result with what the whole run used, did and left.</summary>
        private AgentRunResult Finished(AgentRunResult result)
            => result with
            {
                Model = run.Model,
                Session = _session,
                Turns = _turns,
                ToolCalls = Runtime.Tally.Calls,
                Denials = Runtime.Tally.Denials,
                FilesWritten = Runtime.Tally.FilesWritten,
                WebRequests = Runtime.Tally.WebRequests,
                Retries = _retries,
                Usage = _used.Report(_unknown is null ? new AgentCost(Counted: _cost) : new AgentCost(MissingPrice: _unknown)),
                Served = [.. _used.Served],
                Files = Runtime.Tally.Files,
                KeysRefused = [.. _keysRefused],
                Uncontained = Runtime.Tally.Uncontained,
            };

        private AgentRunResult Ended(AgentRunStatus status, string error) => AgentEndings.Ended(status, error, run.Mask);

        private AgentRunResult TimedOut() => Ended(AgentRunStatus.TimedOut, $"the step ran past its {run.Limits.MaxDuration}");

        private AgentRunResult Spent(decimal budget)
            => Ended(AgentRunStatus.BudgetSpent, string.Create(CultureInfo.InvariantCulture, $"the step spent {_cost:0.######} of its {budget} {AgentPrices.BudgetCurrency} budget"));

        private AgentRunResult WrongModel(string named) => AgentEndings.WrongModel(run.Model!, named, $"its turn {_turns}", run.Mask);

        private ValueTask Emit(AgentEvent @event) => events.EmitAsync(@event);

        private string Masked(string text) => run.Mask.Redact(text);

    }
}
