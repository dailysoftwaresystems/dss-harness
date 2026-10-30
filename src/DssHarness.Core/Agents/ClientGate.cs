using System.Collections.Concurrent;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Agents;

/// <summary>
/// One client's bounds as the gate holds them (part 03, "Coordination"): how many of its runs - requests, for an API -
/// are under way at once on one provider and one key, and the optional buckets that pace them. The built-in clients
/// each name their own parallel bound; nothing else is said by default, and the provider's answers pace the client.
/// </summary>
/// <param name="MaxParallel">The most runs under way at once on one lane.</param>
/// <param name="RequestsPerMinute">At most this many requests in any minute; null for no bucket.</param>
/// <param name="InputTokensPerMinute">At most this many input tokens in any minute; null for no bucket.</param>
/// <param name="OutputTokensPerMinute">At most this many output tokens in any minute; null for no bucket.</param>
public sealed record ClientGateSettings(
    int MaxParallel,
    int? RequestsPerMinute = null,
    int? InputTokensPerMinute = null,
    int? OutputTokensPerMinute = null)
{
    /// <summary>
    /// The parallel bound of each client the tool builds in, where its entry says none: the runs of one provider and
    /// key a client has under way at once.
    /// </summary>
    public static IReadOnlyDictionary<string, int> BuiltIn { get; } = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [ClaudeCode.ClaudeCodeClient.ClientName] = 3,
        [ClaudeApi.ClaudeApiClient.ClientName] = 4,
        [ZaiApi.ZaiApiClient.ClientName] = 2,
    };

    /// <summary>
    /// The settings of the client <paramref name="client"/>: its entry's bounds, each at the client's own where its
    /// entry says none and its buckets left out where it declares none.
    /// </summary>
    /// <param name="client">The client's name, as the configuration declares it or the built-in's own.</param>
    /// <param name="entry">Its entry under <c>agentClients</c>; null where the configuration declares none.</param>
    public static ClientGateSettings Of(string client, AgentClientConfig? entry)
        => new(
            entry?.MaxParallel ?? BuiltIn[client],
            entry?.RequestsPerMinute,
            entry?.InputTokensPerMinute,
            entry?.OutputTokensPerMinute);
}

/// <summary>
/// The lanes of a session's runs, one per provider and key (part 03, "Coordination"): a client that signs in with a
/// login is one lane by its client, for the login is the account, and two keys of one provider are two lanes, keyed by
/// a hash of the key the caller hands. The gate bounds requests; the orchestrator's <c>maxReasoningAgents</c> bounds
/// agents; both apply.
/// </summary>
/// <param name="clock">Tells the time each request is counted at.</param>
/// <param name="wait">Waits a spent bucket or a cool-down out, as the web search's pace waits its minutes.</param>
public sealed class ClientGates(TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait)
{
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? throw new ArgumentNullException(nameof(wait));
    private readonly ConcurrentDictionary<(string Provider, string Identity), ClientGate> _lanes = new();

    /// <summary>
    /// The lane of <paramref name="provider"/> and <paramref name="identity"/> - the hash of the key every run of the
    /// lane sends, or the client's own name where it signs in with a login - made on first ask and shared by every run
    /// of it after.
    /// </summary>
    /// <param name="provider">The provider both reach.</param>
    /// <param name="identity">The key's hash, or the client's name for a login.</param>
    /// <param name="settings">The client's bounds.</param>
    public ClientGate Lane(string provider, string identity, ClientGateSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(settings);

        return _lanes.GetOrAdd((provider, identity), _ => new ClientGate(settings, _clock, _wait));
    }
}

/// <summary>
/// One lane of the gate: the runs - requests, for an API - that share one provider and one key, held to the client's
/// parallel bound, its optional buckets and a cool-down a 429 sets for every waiter. The engine's step runner holds the
/// lane a run's requests go through; the buckets are kept in memory, for a lane is one engine's own.
/// </summary>
public sealed class ClientGate
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly ClientGateSettings _settings;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly SemaphoreSlim _slots;

    private readonly object _state = new();
    private readonly List<DateTimeOffset> _requests = [];
    private readonly List<(DateTimeOffset At, long Tokens)> _input = [];
    private readonly List<(DateTimeOffset At, long Tokens)> _output = [];
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    internal ClientGate(ClientGateSettings settings, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait)
    {
        _settings = settings;
        _clock = clock;
        _wait = wait;
        _slots = new SemaphoreSlim(settings.MaxParallel, settings.MaxParallel);
    }

    /// <summary>
    /// Takes the lane's turn: waits while a cool-down holds it, a bucket is spent or every parallel slot is taken, then
    /// counts the request and its <paramref name="inputTokens"/> against its buckets and answers the slot, released
    /// when the request's run is done. The answer's tokens are charged by <see cref="ClientSlot.Answered"/>, for they
    /// are no lane's to know before it has them.
    /// </summary>
    /// <param name="inputTokens">The tokens the request sends, as the caller counts them.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    public async Task<ClientSlot> EnterAsync(long inputTokens, CancellationToken cancellationToken)
    {
        while (true)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            TimeSpan spent;

            lock (_state)
            {
                var now = _clock.GetUtcNow();
                Live(now);

                if (now < _notBefore)
                {
                    spent = _notBefore - now;
                }
                else if (RoomFor(inputTokens, now) is { } until)
                {
                    spent = until - now;
                }
                else
                {
                    Take(inputTokens, now);
                    return new ClientSlot(this);
                }

                // The slot is given back before the wait: the lane's bound is on requests under way, and one that waits
                // its turn is none of them.
            }

            _slots.Release();
            await _wait(spent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A request the provider answered 429: every waiter of the lane waits <paramref name="wait"/> out, a later or
    /// longer one holding past a sooner one, as the pace's pause does.
    /// </summary>
    /// <param name="wait">How long the provider asked to wait; zero or negative is nothing.</param>
    public void RateLimited(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        lock (_state)
        {
            var until = _clock.GetUtcNow() + wait;

            if (until > _notBefore)
            {
                _notBefore = until;
            }
        }
    }

    /// <summary>Drops every count a minute old or more, as the lane stands at <paramref name="now"/>.</summary>
    private void Live(DateTimeOffset now)
    {
        _requests.RemoveAll(at => at <= now - Minute);
        _input.RemoveAll(entry => entry.At <= now - Minute);
        _output.RemoveAll(entry => entry.At <= now - Minute);
    }

    /// <summary>When the oldest count that must leave its minute leaves it, where <paramref name="inputTokens"/> does not fit; null where it does.</summary>
    private DateTimeOffset? RoomFor(long inputTokens, DateTimeOffset now)
    {
        if (_settings.RequestsPerMinute is { } requests && _requests.Count >= requests)
        {
            return _requests[0] + Minute;
        }

        if (_settings.InputTokensPerMinute is { } tokens)
        {
            var spent = Sum(_input);

            if (spent + inputTokens > tokens)
            {
                // The oldest entries whose leaving makes room: the last of them leaving is when.
                var freeing = 0L;

                foreach (var entry in _input)
                {
                    freeing += entry.Tokens;

                    if (spent - freeing + inputTokens <= tokens)
                    {
                        return entry.At + Minute;
                    }
                }

                return now + Minute;
            }
        }

        // The output bucket paces on what was answered: a lane whose last minute answered its fill waits for the
        // oldest of it to age out, for the next answer's size is no lane's to know.
        if (_settings.OutputTokensPerMinute is { } answered && _output.Count > 0 && Sum(_output) >= answered)
        {
            return _output[0].At + Minute;
        }

        return null;
    }

    /// <summary>Counts the request and its input tokens, both at <paramref name="now"/>.</summary>
    private void Take(long inputTokens, DateTimeOffset now)
    {
        _requests.Add(now);
        _input.Add((now, inputTokens));
    }

    /// <summary>Counts <paramref name="tokens"/> of output at <paramref name="now"/>: the answer's, once it is had.</summary>
    private void Answered(long tokens, DateTimeOffset now)
    {
        if (_settings.OutputTokensPerMinute is not null)
        {
            _output.Add((now, tokens));
        }
    }

    /// <summary>Releases the parallel slot.</summary>
    private void Leave() => _slots.Release();

    private static long Sum(IEnumerable<(DateTimeOffset At, long Tokens)> entries) => entries.Sum(entry => entry.Tokens);

    /// <summary>
    /// One request's place in its lane: released when its run is done, and the answer's tokens charged to the output
    /// bucket once it has them - at most <see cref="ClientGateSettings.MaxParallel"/> held at once.
    /// </summary>
    public sealed class ClientSlot(ClientGate gate) : IDisposable
    {
        private readonly ClientGate _gate = gate;

        /// <summary>The lane the request runs in, for a 429 it meets to set the cool-down of.</summary>
        public ClientGate Gate => _gate;

        /// <summary>Charges the answer's <paramref name="tokens"/> to the output bucket; the request's input was charged at entry.</summary>
        /// <param name="tokens">The output tokens the answer used.</param>
        public void Answered(long tokens)
        {
            lock (_gate._state)
            {
                _gate.Answered(tokens, _gate._clock.GetUtcNow());
            }
        }

        /// <summary>Releases the lane's parallel slot.</summary>
        public void Dispose() => _gate.Leave();
    }
}
