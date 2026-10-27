using System.Globalization;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Web;

/// <summary>
/// What an answer's status and headers say of how soon its source may be asked again, for every reader of the web alike:
/// a limit - a 429; a 403 or 503 with <c>Retry-After</c>, or with its requests said to be spent, as GitHub's secondary
/// limits answer - which answered nothing; and, on a good answer, its requests said to be spent - <c>RateLimit</c> or
/// <c>X-RateLimit-Remaining</c> at zero with their reset - which holds the source until the reset. No pause, said by a
/// header, a body or a site's <c>Crawl-delay</c>, is longer than <see cref="MostPause"/>. The one owner of these rules:
/// the web search reads its answers by them, and the fill of a site's index its requests.
/// </summary>
public static partial class WebRateLimit
{
    /// <summary>How long a source that asks to be left alone and says not for how long is left: a minute.</summary>
    public static readonly TimeSpan Unsaid = TimeSpan.FromMinutes(1);

    /// <summary>The longest a source is left alone for, whatever it asks: a day, so a time it gives by mistake never ends its use.</summary>
    public static readonly TimeSpan MostPause = TimeSpan.FromDays(1);

    /// <summary>A reset past this many seconds is a time (seconds since 1970), as GitHub's is; one below, a wait, as the standard's is.</summary>
    private const long EpochFrom = 1_000_000_000;

    /// <summary>
    /// What <paramref name="status"/> and <paramref name="headers"/>, read at <paramref name="at"/>, say: a limit, or a good
    /// answer whose requests are spent, each with when the source may be asked again; <see langword="null"/> where neither.
    /// </summary>
    /// <param name="status">The status answered.</param>
    /// <param name="headers">The headers answered, by name in any case.</param>
    /// <param name="at">When the answer was read.</param>
    public static WebLimit? Read(int status, IReadOnlyDictionary<string, string> headers, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var retryAfter = RetryAfter(headers, at);
        var spent = Spent(headers, at);

        if (status == 429 || (status is 403 or 503 && (retryAfter ?? spent) is not null))
        {
            return new WebLimit(retryAfter ?? spent ?? at + Unsaid, Limited: true, Said: (retryAfter ?? spent) is not null);
        }

        return status is >= 200 and <= 299 && spent is { } until ? new WebLimit(until, Limited: false, Said: true) : null;
    }

    /// <summary>When <paramref name="seconds"/> after <paramref name="at"/> ends, no later than <see cref="MostPause"/> after it.</summary>
    /// <param name="at">When the wait begins.</param>
    /// <param name="seconds">How long it is, in seconds.</param>
    public static DateTimeOffset After(DateTimeOffset at, double seconds) => at + TimeSpan.FromSeconds(Math.Clamp(seconds, 0, MostPause.TotalSeconds));

    /// <summary>The later of two pauses, either of which may be none.</summary>
    public static DateTimeOffset? Later(DateTimeOffset? one, DateTimeOffset? other)
        => one is null ? other : other is null ? one : one > other ? one : other;

    /// <summary>A time as a reader of the web says it, to an agent and in a record: UTC, to the second.</summary>
    /// <param name="at">The time.</param>
    public static string Said(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>The header <paramref name="name"/> of <paramref name="headers"/>, in any case; <see langword="null"/> where it is not there.</summary>
    /// <param name="headers">Headers by name.</param>
    /// <param name="name">The header's name.</param>
    public static string? Header(IReadOnlyDictionary<string, string> headers, string name)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>When <c>Retry-After</c> says the source may be asked again: a number of seconds, or a date.</summary>
    private static DateTimeOffset? RetryAfter(IReadOnlyDictionary<string, string> headers, DateTimeOffset at)
    {
        if (Header(headers, "Retry-After")?.Trim() is not { Length: > 0 } said)
        {
            return null;
        }

        if (long.TryParse(said, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return After(at, seconds);
        }

        return DateTimeOffset.TryParseExact(said, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? Capped(at, date) : null;
    }

    /// <summary>
    /// When a source whose headers say its requests are spent - <c>X-RateLimit-Remaining: 0</c>, or the standard's
    /// <c>RateLimit</c> with nothing remaining - may be asked again, by its reset; <see langword="null"/> where it is not spent.
    /// </summary>
    private static DateTimeOffset? Spent(IReadOnlyDictionary<string, string> headers, DateTimeOffset at)
    {
        if (Header(headers, "X-RateLimit-Remaining")?.Trim() == "0")
        {
            return Reset(at, Header(headers, "X-RateLimit-Reset")) ?? at + Unsaid;
        }

        if (Header(headers, "RateLimit") is not { } limit)
        {
            return null;
        }

        // The standard's two spellings: 'limit=10, remaining=0, reset=30' (its earlier drafts) and '"default";r=0;t=30'.
        var remaining = Remaining().Match(limit);

        if (!remaining.Success || remaining.Groups["count"].Value != "0")
        {
            return null;
        }

        return Reset(at, ResetIn().Match(limit) is { Success: true } reset ? reset.Groups["seconds"].Value : Header(headers, "RateLimit-Reset"))
            ?? at + Unsaid;
    }

    /// <summary>A reset: a number of seconds to wait, or, past <see cref="EpochFrom"/>, a time in seconds since 1970.</summary>
    private static DateTimeOffset? Reset(DateTimeOffset at, string? said)
        => long.TryParse(said?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value >= EpochFrom
                ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(value, (at + MostPause).ToUnixTimeSeconds()))
                : After(at, value)
            : null;

    /// <summary><paramref name="until"/>, no later than <see cref="MostPause"/> after <paramref name="at"/>.</summary>
    private static DateTimeOffset Capped(DateTimeOffset at, DateTimeOffset until) => until > at + MostPause ? at + MostPause : until;

    [GeneratedRegex(@"(?:^|[;,\s])(?:remaining|r)=(?<count>[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Remaining();

    [GeneratedRegex(@"(?:^|[;,\s])(?:reset|t)=(?<seconds>[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ResetIn();
}

/// <summary>What an answer said of how soon its source may be asked again.</summary>
/// <param name="Until">When it may be asked again.</param>
/// <param name="Limited">Whether the request was answered nothing - a limit - rather than a good answer whose requests are spent.</param>
/// <param name="Said">Whether the source said the time; where it did not, it is <see cref="WebRateLimit.Unsaid"/> after the answer.</param>
public sealed record WebLimit(DateTimeOffset Until, bool Limited, bool Said);
