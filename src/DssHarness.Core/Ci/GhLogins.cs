using System.Collections.Concurrent;
using System.Globalization;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Ci;

/// <summary>
/// The machine's <c>gh</c> login for a host, as the harness's own reads send it - the one owner of its token in the tool:
/// read through the one runner of <c>gh</c> (<c>gh auth token --hostname</c>) once per engine session
/// (<see cref="EngineSession"/>) and held for every step of it; read again before the expiry the host told of it passes
/// (<see cref="Expires"/>), and once where the host refuses it (<see cref="RenewAsync"/>). Outside a session, one is held
/// for as long as its caller holds it - a step's search - and no longer.
/// </summary>
/// <remarks>
/// <para>
/// Only a login that holds a token is held. A reading that gave none - gh signed out, or not answering in time - answers
/// the asking that read it alone, and the next asking reads gh again: a gh that timed out once, or a person who signed
/// back in, is asked again by the next step, never answered "signed out" for the rest of the session. And a reading that
/// gives none never takes the place of a token held: the token is kept, and the reading answered by it.
/// </para>
/// <para>
/// <c>gh</c> says no expiry of the token it holds - neither <c>gh auth token</c> nor <c>gh auth status --json hosts</c>
/// names one - so the expiry is the one the host tells: GitHub's API names it on every answer to a request a token that
/// expires signed (<see cref="ExpiryHeader"/>).
/// </para>
/// <para>
/// The token is held here, in memory, and nowhere else: it is never written to a record, a log or anything a run keeps,
/// and nothing made of a login prints it (<see cref="Login.ToString"/>). Every step that sends it holds each form it is
/// sent in in its mask before it is sent.
/// </para>
/// </remarks>
public sealed class GhLogins
{
    /// <summary>The header GitHub's API names a token's expiry in, on an answer to a request it signed: <c>2026-12-01 10:00:00 UTC</c>.</summary>
    public const string ExpiryHeader = "github-authentication-token-expiration";

    /// <summary>How long before its expiry a token is read again, so no request it signs lands after it has passed.</summary>
    public static readonly TimeSpan Ahead = TimeSpan.FromMinutes(1);

    private readonly GhProcess _gh;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, Held> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A holder of logins of its own, for as long as its caller holds it.</summary>
    /// <param name="gh">Runs <c>gh</c>.</param>
    /// <param name="clock">Says when an expiry has come.</param>
    public GhLogins(GhProcess gh, TimeProvider? clock = null)
    {
        _gh = gh ?? throw new ArgumentNullException(nameof(gh));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The logins the calling work's engine session holds, made at its first asking; outside a session, a holder of the caller's own.</summary>
    /// <param name="gh">Runs <c>gh</c>.</param>
    /// <param name="clock">Says when an expiry has come.</param>
    public static GhLogins For(GhProcess gh, TimeProvider? clock = null)
        => EngineSession.Of(() => new GhLogins(gh, clock)) ?? new GhLogins(gh, clock);

    /// <summary>
    /// The login for <paramref name="host"/>: the token held, read where none is - and read again where the expiry the host
    /// told of the token held is less than <see cref="Ahead"/> away, once for that expiry. A reading that gives no token
    /// answers this asking alone, by the token held where there is one, and leaves the next asking to read gh again.
    /// </summary>
    /// <param name="directory">Where <c>gh</c> runs: the repository's main checkout.</param>
    /// <param name="host">The host the login is for.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<Login> ReadAsync(string directory, string host, CancellationToken cancellationToken)
    {
        var held = HeldFor(host);
        await held.Asking.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (held.Login is { Token: not null } login && !Due(login))
            {
                return login;
            }

            return Kept(held, await AskAsync(directory, host, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            held.Asking.Release();
        }
    }

    /// <summary>
    /// Whether a token is held for <paramref name="host"/> now: one a step of the session read, which a step that began
    /// without one is sent from then on (<see cref="Web.Search.WebSearchRenewal"/>). Asks nothing of <c>gh</c>.
    /// </summary>
    /// <param name="host">The host the login is for.</param>
    public bool Holds(string host)
        => _hosts.TryGetValue(host, out var held) && held.Login is { Token: not null };

    /// <summary>
    /// The login for <paramref name="host"/> after the host refused <paramref name="refused"/>: read again where that is
    /// still the token held; where another asking read a new one since, that one, unread.
    /// </summary>
    /// <param name="directory">Where <c>gh</c> runs: the repository's main checkout.</param>
    /// <param name="host">The host the login is for.</param>
    /// <param name="refused">The token the host refused.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<Login> RenewAsync(string directory, string host, string refused, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(refused);

        var held = HeldFor(host);
        await held.Asking.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (held.Login is { Token: { } token } login && !string.Equals(token, refused, StringComparison.Ordinal))
            {
                return login;
            }

            return Kept(held, await AskAsync(directory, host, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            held.Asking.Release();
        }
    }

    /// <summary>
    /// Keeps <paramref name="at"/> beside <paramref name="token"/>, where it is the token held for <paramref name="host"/>:
    /// the expiry the host told of it, which the next reading heeds.
    /// </summary>
    /// <param name="host">The host the login is for.</param>
    /// <param name="token">The token the host told the expiry of.</param>
    /// <param name="at">When it expires.</param>
    public void Expires(string host, string token, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (!_hosts.TryGetValue(host, out var held))
        {
            return;
        }

        // Told beside a reading that may be replacing the login: kept only where the login it was told of is still the one
        // held, for an answer to the token before it tells nothing of the next. A telling lost to a reading is told again by
        // the host's next answer, which names the expiry on every one.
        while (held.Login is { Token: { } kept } login && string.Equals(kept, token, StringComparison.Ordinal) && login.Expiry != at)
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref held.Current, login with { Expiry = at, ReadForExpiry = false }, login), login))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The expiry <paramref name="said"/> names, as GitHub's API writes it in <see cref="ExpiryHeader"/> -
    /// <c>2026-12-01 10:00:00 UTC</c>, or with an offset of its own; null where it names none this reads.
    /// </summary>
    /// <param name="said">The header's value.</param>
    public static DateTimeOffset? ExpiryOf(string? said)
    {
        if (said is not { Length: > 0 })
        {
            return null;
        }

        var text = said.Trim();

        if (text.EndsWith(" UTC", StringComparison.Ordinal))
        {
            text = text[..^4] + " +00:00";
        }

        return DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd HH:mm:ss zzz", "yyyy-MM-dd HH:mm:ss zzzz"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : null;
    }

    /// <summary>Whether <paramref name="login"/>'s token is read again before it is used: its expiry is less than <see cref="Ahead"/> away, and it was not read again for it yet.</summary>
    private bool Due(Login login)
        => login is { Expiry: { } expiry, ReadForExpiry: false } && _clock.GetUtcNow() >= expiry - Ahead;

    /// <summary>
    /// What an asking answers once <paramref name="read"/> was read for <paramref name="held"/>, and what is held after it:
    /// the token read, its expiry not known yet - or, where it is the token held already, that token with its expiry, read
    /// again for it once. A reading that gave no token is held nowhere: the token held answers it where there is one, still
    /// due where it was, and else the reading itself, which the next asking reads again.
    /// </summary>
    private static Login Kept(Held held, Login read)
    {
        var was = held.Login;

        if (read.Token is null)
        {
            return was is { Token: not null } ? was : read;
        }

        held.Login = was is { Token: { } token } && string.Equals(token, read.Token, StringComparison.Ordinal)
            ? was with { ReadForExpiry = was.Expiry is not null }
            : read;

        return held.Login;
    }

    private Held HeldFor(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        return _hosts.GetOrAdd(host, _ => new Held());
    }

    private async Task<Login> AskAsync(string directory, string host, CancellationToken cancellationToken)
    {
        try
        {
            // Waited for as every call of gh is, by the runner's own limit: no shorter one of this reader's.
            var token = (await _gh.RunAsync(directory, ["auth", "token", "--hostname", host], "so the source is asked without the machine's gh login", limit: null, cancellationToken).ConfigureAwait(false)).Trim();

            return token.Length > 0 ? new Login(token, null) : new Login(null, $"gh answered no token: the machine's gh login for {host} is signed out");
        }
        catch (HarnessException ex)
        {
            return new Login(null, ex.Message);
        }
    }

    /// <summary>One host's login as held, and the one asking of it at a time.</summary>
    private sealed class Held
    {
        /// <summary>
        /// The login held - one that holds a token, or none - replaced whole: by a reading that gave a token, one at a time,
        /// and by an expiry told (<see cref="Expires"/>).
        /// </summary>
        public Login? Current;

        public SemaphoreSlim Asking { get; } = new(1, 1);

        public Login? Login
        {
            get => Volatile.Read(ref Current);
            set => Volatile.Write(ref Current, value);
        }
    }

    /// <summary>A host's login as read: its token, or why there is none; and the expiry the host told of the token, where it told one.</summary>
    /// <param name="Token">The token; null where there is none.</param>
    /// <param name="Missing">Why there is none; null where there is one.</param>
    public sealed record Login(string? Token, string? Missing)
    {
        /// <summary>When the token expires, as the host told; null where it told none.</summary>
        public DateTimeOffset? Expiry { get; init; }

        /// <summary>Whether the token was read again for its <see cref="Expiry"/> already, and gh answered the same one.</summary>
        public bool ReadForExpiry { get; init; }

        /// <summary>The login by whether it holds a token alone, so no text made of it holds the token.</summary>
        public override string ToString() => Token is null ? $"no token: {Missing}" : "a token";
    }
}
