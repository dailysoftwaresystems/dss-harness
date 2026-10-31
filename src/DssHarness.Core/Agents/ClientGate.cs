using System.Collections.Concurrent;
using System.Text;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Agents;

/// <summary>
/// One client's bounds as the gate holds them: how many of its requests - its launches, for
/// Claude Code, which has no seam between one request and the next - are under way at once on one provider and one
/// key, and the optional buckets that pace them. The built-in clients each name their own parallel bound; nothing else
/// is said by default, and the provider's answers pace the client.
/// </summary>
/// <param name="MaxParallel">The most requests under way at once on one lane.</param>
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
    /// The parallel bound of each client the tool builds in, where its entry says none: the requests of one provider and
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
/// The lanes of a session's requests, one per provider and key: a client that signs in with a
/// login is one lane by its client, for the login is the account, and two keys of one provider are two lanes, keyed by
/// a hash of the key. The gate bounds requests; the orchestrator's <c>maxReasoningAgents</c> bounds agents; both
/// apply.
/// </summary>
/// <param name="clock">Tells the time each request is counted at.</param>
/// <param name="wait">Waits a spent bucket or a cool-down out, as the web search's pace waits its minutes.</param>
public sealed class ClientGates(TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait)
{
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Func<TimeSpan, CancellationToken, Task> _wait = wait ?? throw new ArgumentNullException(nameof(wait));
    private readonly ConcurrentDictionary<(string Provider, string Identity), ClientGate> _lanes = new();

    /// <summary>
    /// The lane of <paramref name="provider"/> and <paramref name="identity"/> - the hash of the key every request of the
    /// lane sends, or the client's own name where it signs in with a login - made on first ask and shared by every
    /// request of it after.
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

    /// <summary>
    /// The lane a request of <paramref name="client"/> on <paramref name="provider"/> signed with <paramref name="key"/>
    /// goes through: the key's own value is the account, hashed before the registry holds it - for the value is a secret
    /// no dictionary of the process holds in the clear - and a request signed with the machine's login is one lane by its
    /// client.
    /// </summary>
    /// <param name="provider">The provider the request reaches.</param>
    /// <param name="client">The client that sends it, whose bounds the lane holds.</param>
    /// <param name="entry">The client's entry under <c>agentClients</c>; null where the configuration declares none.</param>
    /// <param name="key">The key the request is signed with; null for the machine's login.</param>
    public ClientGate LaneOf(string provider, string client, AgentClientConfig? entry, AgentKey? key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(client);

        return Lane(provider, key is { Value.Length: > 0 } signed ? FileSystem.FileContentHash.Of(Encoding.UTF8.GetBytes(signed.Value)) : client, ClientGateSettings.Of(client, entry));
    }
}

/// <summary>
/// One lane of the gate: the requests that share one provider and one key, held to the client's parallel bound, its
/// optional buckets and the cool-downs a 429 sets, each scoped as the failure was - a rate limit holding that model's
/// requests on the key, an account's limit every request of it. Each client enters it at its own seam - the loop the
/// API clients share before each request, the runner around each Claude Code launch - and the buckets are kept in
/// memory, for a lane is one engine's own. Its minutes and its cool-downs are counted on the clock's timestamps, so a
/// wall clock stepped back or forward holds no lane longer or shorter than the time that passed.
/// </summary>
public sealed class ClientGate
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private readonly ClientGateSettings _settings;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly SemaphoreSlim _slots;

    private readonly Lock _state = new();
    private readonly List<long> _requests = [];
    private readonly List<(long At, long Tokens)> _input = [];
    private readonly List<(long At, long Tokens)> _output = [];
    private readonly Dictionary<string, long> _modelNotBefore = new(StringComparer.Ordinal);
    private long? _notBefore;

    internal ClientGate(ClientGateSettings settings, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait)
    {
        _settings = settings;
        _clock = clock;
        _wait = wait;
        _slots = new SemaphoreSlim(settings.MaxParallel, settings.MaxParallel);
    }

    /// <summary>
    /// How long a cool-down still holds the lane's requests of <paramref name="model"/>, and whose it is: the longer of
    /// the key's whole - an account's limit, <see cref="ClientCoolDown.Model"/> null - and the model's own; null where
    /// none does. Another model's rate limit is never this one's.
    /// </summary>
    /// <param name="model">The model the request asks; null where it names none, which only the key's whole holds.</param>
    public ClientCoolDown? CoolDownOf(string? model)
    {
        lock (_state)
        {
            return CoolingFor(_clock.GetTimestamp(), model);
        }
    }

    /// <summary>
    /// Takes the lane's turn: waits while a cool-down holds it, a bucket is spent or every parallel slot is taken, then
    /// counts the request and its <paramref name="inputTokens"/> against its buckets and answers the slot, released
    /// when the request is done. The answer's tokens are charged by <see cref="ClientSlot.Answered"/>, for they are no
    /// lane's to know before it has them.
    /// </summary>
    /// <param name="inputTokens">The tokens the request sends, as the caller counts them.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    public Task<ClientSlot> EnterAsync(long inputTokens, CancellationToken cancellationToken)
        => EnterAsync(inputTokens, model: null, waiting: null, cancellationToken);

    /// <summary>
    /// Takes the lane's turn as <see cref="EnterAsync(long, CancellationToken)"/> does for a request of
    /// <paramref name="model"/> - held by the key's cool-down and that model's alone - telling <paramref name="waiting"/>
    /// each wait before it is waited - how long, and why - so the caller says it rather than going silent.
    /// </summary>
    /// <remarks>
    /// A request whose own input is more than the input bucket holds in a minute waits for the bucket to be empty and is
    /// then let in alone: no minute could ever hold it beside another, and waiting for one that does would wait forever.
    /// </remarks>
    /// <param name="inputTokens">The tokens the request sends, as the caller counts them.</param>
    /// <param name="model">The model the request asks; null where it names none.</param>
    /// <param name="waiting">Told each wait before it is waited, with what holds the lane; null where nothing is told.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    public async Task<ClientSlot> EnterAsync(long inputTokens, string? model, Func<TimeSpan, string, ValueTask>? waiting, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);

        while (true)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            TimeSpan spent;
            string why;

            lock (_state)
            {
                var now = _clock.GetTimestamp();
                Live(now);

                if (CoolingFor(now, model) is { } cooling)
                {
                    (spent, why) = (cooling.Left, CoolDown);
                }
                else if (RoomFor(inputTokens, now) is { } until)
                {
                    (spent, why) = until;
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

            if (waiting is not null)
            {
                await waiting(spent, why).ConfigureAwait(false);
            }

            await _wait(spent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>What a lane a 429 holds is said to wait for.</summary>
    public const string CoolDown = "the lane's cool-down after a 429";

    /// <summary>What a lane whose minute's requests are spent is said to wait for.</summary>
    public const string RequestsSpent = "the lane's minute of requests is spent";

    /// <summary>What a lane whose minute's input tokens are spent is said to wait for.</summary>
    public const string InputSpent = "the lane's minute of input tokens is spent";

    /// <summary>What a lane whose minute's output tokens are spent is said to wait for.</summary>
    public const string OutputSpent = "the lane's minute of output tokens is spent";

    /// <summary>
    /// A request the provider answered 429: every waiter the failure covers waits <paramref name="wait"/> out - the
    /// lane's requests of <paramref name="model"/> for a rate limit, which is a model's on a key, and every request of
    /// the lane for an account's limit - a later or longer one holding past a sooner one, as the pace's pause does.
    /// </summary>
    /// <param name="wait">How long the provider asked to wait; zero or negative is nothing.</param>
    /// <param name="model">The model the limit is of; null where it is the key's whole.</param>
    public void RateLimited(TimeSpan wait, string? model = null)
    {
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        lock (_state)
        {
            var until = _clock.GetTimestamp() + Ticks(wait);

            if (model is null)
            {
                _notBefore = _notBefore is { } held && held >= until ? held : until;
            }
            else
            {
                _modelNotBefore[model] = _modelNotBefore.TryGetValue(model, out var held) && held >= until ? held : until;
            }
        }
    }

    /// <summary>
    /// A request of <paramref name="model"/> answered with <paramref name="failure"/>: where it is a 429 - a rate limit,
    /// which holds the lane's requests of that model, or an account's limit, which holds every request of the lane - the
    /// waiters it covers wait out what the provider named, the wait it asked or the time it said the limit lifts; any
    /// other failure holds nothing. The one reading of a 429's cool-down every request through a lane is held to.
    /// </summary>
    /// <param name="failure">How the request failed.</param>
    /// <param name="model">The model the request asked.</param>
    public void Met(Loop.ChatFailure failure, string model)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure.Kind is not (Loop.ChatFailureKind.RateLimited or Loop.ChatFailureKind.AccountLimit))
        {
            return;
        }

        if ((failure.RetryAfter ?? (failure.Lifts is { } lifts ? lifts - _clock.GetUtcNow() : null)) is { } wait)
        {
            RateLimited(wait, failure.Kind == Loop.ChatFailureKind.RateLimited ? model : null);
        }
    }

    /// <summary>The longer of the key's cool-down and <paramref name="model"/>'s at <paramref name="now"/>, with whose it is; null where neither holds.</summary>
    private ClientCoolDown? CoolingFor(long now, string? model)
    {
        var key = _notBefore is { } whole && now < whole ? whole : (long?)null;
        var own = model is not null && _modelNotBefore.TryGetValue(model, out var until) && now < until ? until : (long?)null;

        return (key, own) switch
        {
            (null, null) => null,
            ({ } all, { } its) when its > all => new ClientCoolDown(_clock.GetElapsedTime(now, its), model),
            ({ } all, _) => new ClientCoolDown(_clock.GetElapsedTime(now, all), null),
            (null, { } its) => new ClientCoolDown(_clock.GetElapsedTime(now, its), model),
        };
    }

    /// <summary>The clock's ticks <paramref name="span"/> is.</summary>
    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _clock.TimestampFrequency);

    /// <summary>Drops every count a minute old or more, as the lane stands at <paramref name="now"/>.</summary>
    private void Live(long now)
    {
        var minuteAgo = now - Ticks(Minute);

        _requests.RemoveAll(at => at <= minuteAgo);
        _input.RemoveAll(entry => entry.At <= minuteAgo);
        _output.RemoveAll(entry => entry.At <= minuteAgo);
    }

    /// <summary>How long until the oldest count that must leave its minute leaves it, and which bucket holds the request, where <paramref name="inputTokens"/> does not fit; null where it does.</summary>
    private (TimeSpan Wait, string Why)? RoomFor(long inputTokens, long now)
    {
        if (_settings.RequestsPerMinute is { } requests && _requests.Count >= requests)
        {
            return (Until(_requests[0], now), RequestsSpent);
        }

        if (_settings.InputTokensPerMinute is { } tokens && _input.Count > 0)
        {
            var spent = Sum(_input);

            // A request more than a minute holds waits for the bucket to be empty, and then goes in alone.
            var room = Math.Min(inputTokens, tokens);

            if (spent + room > tokens)
            {
                // The oldest entries whose leaving makes room: the last of them leaving is when.
                var freeing = 0L;

                foreach (var entry in _input)
                {
                    freeing += entry.Tokens;

                    if (spent - freeing + room <= tokens)
                    {
                        return (Until(entry.At, now), InputSpent);
                    }
                }
            }
        }

        // The output bucket paces on what was answered: a lane whose last minute answered its fill waits for the
        // oldest of it to age out, for the next answer's size is no lane's to know.
        if (_settings.OutputTokensPerMinute is { } answered && _output.Count > 0 && Sum(_output) >= answered)
        {
            return (Until(_output[0].At, now), OutputSpent);
        }

        return null;
    }

    /// <summary>How long from <paramref name="now"/> until a count made at <paramref name="at"/> is a minute old.</summary>
    private TimeSpan Until(long at, long now) => _clock.GetElapsedTime(now, at + Ticks(Minute));

    /// <summary>Counts the request and its input tokens, both at <paramref name="now"/>.</summary>
    private void Take(long inputTokens, long now)
    {
        _requests.Add(now);
        _input.Add((now, inputTokens));
    }

    /// <summary>Counts <paramref name="tokens"/> of output at <paramref name="now"/>: the answer's, once it is had.</summary>
    private void Answered(long tokens, long now)
    {
        if (_settings.OutputTokensPerMinute is not null)
        {
            _output.Add((now, tokens));
        }
    }

    /// <summary>Releases the parallel slot.</summary>
    private void Leave() => _slots.Release();

    private static long Sum(IEnumerable<(long At, long Tokens)> entries) => entries.Sum(entry => entry.Tokens);

    /// <summary>
    /// One request's place in its lane: released when the request is done, and the answer's tokens charged to the output
    /// bucket once it has them - at most <see cref="ClientGateSettings.MaxParallel"/> held at once.
    /// </summary>
    public sealed class ClientSlot(ClientGate gate) : IDisposable
    {
        private readonly ClientGate _gate = gate;
        private int _released;

        /// <summary>The lane the request runs in, for a 429 it meets to set the cool-down of.</summary>
        public ClientGate Gate => _gate;

        /// <summary>Charges the answer's <paramref name="tokens"/> to the output bucket; the request's input was charged at entry.</summary>
        /// <param name="tokens">The output tokens the answer used.</param>
        public void Answered(long tokens)
        {
            lock (_gate._state)
            {
                _gate.Answered(tokens, _gate._clock.GetTimestamp());
            }
        }

        /// <summary>Releases the lane's parallel slot, once however often it is asked.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _gate.Leave();
            }
        }
    }
}

/// <summary>A cool-down that holds a lane's requests of a model: how long it still holds them, and whose it is.</summary>
/// <param name="Left">How long it still holds them.</param>
/// <param name="Model">The model whose rate limit it is; null where it is the key's whole, an account's limit.</param>
public sealed record ClientCoolDown(TimeSpan Left, string? Model);
