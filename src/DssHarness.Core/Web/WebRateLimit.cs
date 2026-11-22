using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Text;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Web;

/// <summary>
/// What an answer's status and headers say of how soon its source may be asked again, for every reader of the web alike:
/// a limit - a 429; a 403 or 503 with <c>Retry-After</c>, or with its requests said to be spent, as GitHub's secondary
/// limits answer - which answered nothing; and, on a good answer, its requests said to be spent - <c>RateLimit</c> or
/// <c>X-RateLimit-Remaining</c> at zero with their reset - which holds the source until the reset. Every pause a source
/// says - by a header, a body or a site's <c>Crawl-delay</c> - is kept as given, a day as a day and a month as a month;
/// one it asks for without saying how long is the asker's to size (<see cref="Unsaid"/>). The one owner of these rules:
/// the web search reads its answers by them, and the fill of a site's index its requests.
/// </summary>
public static class WebRateLimit
{
    /// <summary>A reset past this many seconds is a time (seconds since 1970), as GitHub's is; one below, a wait, as the standard's is.</summary>
    private const long EpochFrom = 1_000_000_000;

    /// <summary>The standard's count of requests left, in its two spellings: 'limit=10, remaining=0, reset=30' and '"default";r=0;t=30'.</summary>
    private static readonly Regex Remaining = LinearRegex.Compile(@"(?:^|[;,\s])(?:remaining|r)=(?<count>[0-9]+)");

    /// <summary>The standard's reset, in the same two spellings.</summary>
    private static readonly Regex ResetIn = LinearRegex.Compile(@"(?:^|[;,\s])(?:reset|t)=(?<seconds>[0-9]+)");

    /// <summary>
    /// What <paramref name="status"/> and <paramref name="headers"/>, read at <paramref name="at"/>, say: a limit, or a good
    /// answer whose requests are spent, each with when the source may be asked again - <paramref name="unsaid"/> after the
    /// answer where it asks to be left alone and says not for how long; <see langword="null"/> where neither.
    /// </summary>
    /// <param name="status">The status answered.</param>
    /// <param name="headers">The headers answered, by name in any case.</param>
    /// <param name="at">When the answer was read.</param>
    /// <param name="unsaid">
    /// How long a source that says not for how long is left: the asker's, doubled from its source's own pace each time the
    /// source asks so again (<see cref="Unsaid"/>).
    /// </param>
    public static WebLimit? Read(int status, IReadOnlyDictionary<string, string> headers, DateTimeOffset at, TimeSpan unsaid)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var retryAfter = RetryAfter(headers, at);
        var spent = Spent(headers, at, unsaid);

        if (status == 429 || (status is 403 or 503 && (retryAfter ?? spent?.Until) is not null))
        {
            return retryAfter is { } given ? new WebLimit(given, Limited: true, Said: true)
                : spent is { } reset ? new WebLimit(reset.Until, Limited: true, Said: reset.Said)
                : new WebLimit(After(at, unsaid), Limited: true, Said: false);
        }

        return status is >= 200 and <= 299 && spent is { } until ? new WebLimit(until.Until, Limited: false, Said: until.Said) : null;
    }

    /// <summary>
    /// How long a source that asks to be left alone and says not for how long is left, the time after <paramref name="last"/>
    /// such a wait: <paramref name="pace"/> - the source's own interval between two requests - the first time, then doubled
    /// as every wait no cause names the length of is (<see cref="Retrying.Doubled"/>).
    /// </summary>
    /// <param name="last">The wait the source was last left for so; <see langword="null"/> where its last answer said its time, or was good.</param>
    /// <param name="pace">The source's own interval between two requests; where it has none, <see cref="Waits.LooksAgainEvery"/>.</param>
    public static TimeSpan Unsaid(TimeSpan? last, TimeSpan? pace)
        => Retrying.Doubled(last, pace is { } interval && interval > TimeSpan.Zero ? interval : Waits.LooksAgainEvery);

    /// <summary>When <paramref name="seconds"/> after <paramref name="at"/> ends, as given: the latest time there is where it is later still.</summary>
    /// <param name="at">When the wait begins.</param>
    /// <param name="seconds">How long it is, in seconds.</param>
    public static DateTimeOffset After(DateTimeOffset at, double seconds)
        => double.IsNaN(seconds) || seconds <= 0 ? at
            : seconds >= (DateTimeOffset.MaxValue - at).TotalSeconds ? DateTimeOffset.MaxValue
            : at + TimeSpan.FromSeconds(seconds);

    /// <summary>When <paramref name="wait"/> after <paramref name="at"/> ends, as given: the latest time there is where it is later still.</summary>
    /// <param name="at">When the wait begins.</param>
    /// <param name="wait">How long it is.</param>
    public static DateTimeOffset After(DateTimeOffset at, TimeSpan wait)
        => wait <= TimeSpan.Zero ? at : wait >= DateTimeOffset.MaxValue - at ? DateTimeOffset.MaxValue : at + wait;

    /// <summary><paramref name="seconds"/> as a time, as given: the longest time there is where it is longer still.</summary>
    /// <param name="seconds">A number of seconds, none below nothing.</param>
    public static TimeSpan Seconds(double seconds)
        => double.IsNaN(seconds) || seconds <= 0 ? TimeSpan.Zero : seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);

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

    /// <summary>When <c>Retry-After</c> says the source may be asked again: a number of seconds, or a date - each as given.</summary>
    private static DateTimeOffset? RetryAfter(IReadOnlyDictionary<string, string> headers, DateTimeOffset at)
    {
        if (Header(headers, "Retry-After")?.Trim() is not { Length: > 0 } said)
        {
            return null;
        }

        if (double.TryParse(said, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return After(at, seconds);
        }

        return DateTimeOffset.TryParseExact(said, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    /// <summary>
    /// When a source whose headers say its requests are spent - <c>X-RateLimit-Remaining: 0</c>, or the standard's
    /// <c>RateLimit</c> with nothing remaining - may be asked again, by its reset, and whether it said one; <see langword="null"/>
    /// where it is not spent.
    /// </summary>
    private static (DateTimeOffset Until, bool Said)? Spent(IReadOnlyDictionary<string, string> headers, DateTimeOffset at, TimeSpan unsaid)
    {
        if (Header(headers, "X-RateLimit-Remaining")?.Trim() == "0")
        {
            return Reset(at, Header(headers, "X-RateLimit-Reset")) is { } reset ? (reset, true) : (After(at, unsaid), false);
        }

        if (Header(headers, "RateLimit") is not { } limit)
        {
            return null;
        }

        var remaining = Remaining.Match(limit);

        if (!remaining.Success || remaining.Groups["count"].Value.TrimStart('0').Length > 0)
        {
            return null;
        }

        return Reset(at, ResetIn.Match(limit) is { Success: true } resetIn ? resetIn.Groups["seconds"].Value : Header(headers, "RateLimit-Reset")) is { } until
            ? (until, true)
            : (After(at, unsaid), false);
    }

    /// <summary>A reset: a number of seconds to wait, or, past <see cref="EpochFrom"/>, a time in seconds since 1970 - each as given.</summary>
    private static DateTimeOffset? Reset(DateTimeOffset at, string? said)
    {
        if (!double.TryParse(said?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        if (value < EpochFrom)
        {
            return After(at, value);
        }

        // A time later than any a date holds is the latest there is: the type's range, never a bound of this tool's.
        return value >= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.MaxValue : DateTimeOffset.FromUnixTimeSeconds((long)value);
    }
}

/// <summary>What an answer said of how soon its source may be asked again.</summary>
/// <param name="Until">When it may be asked again.</param>
/// <param name="Limited">Whether the request was answered nothing - a limit - rather than a good answer whose requests are spent.</param>
/// <param name="Said">Whether the source said the time; where it did not, it is the asker's unsaid wait after the answer (<see cref="WebRateLimit.Unsaid"/>).</param>
public sealed record WebLimit(DateTimeOffset Until, bool Limited, bool Said);
