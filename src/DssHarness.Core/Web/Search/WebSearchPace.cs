using System.Globalization;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// The pace of each search source on this machine, whatever process asks it: no source is asked more than its
/// <c>perMinute</c> in any minute nor its <c>perDay</c> in any day, and a pause it asked for - a limit, a header, a body's
/// <c>backoff</c> - holds it until the time it gave. One record of the machine's, beside admission's, read, decided on and
/// written back as one step (<see cref="MachineWideList{TEntry}"/>), so two runs never both take the last turn.
/// </summary>
/// <remarks>
/// A minute is counted by the times of its requests, a day by the hour each request began in - so a day's count holds
/// hours, never one time for each request, and a day spent is spent until the end of its oldest hour is a day old.
/// </remarks>
public sealed class WebSearchPace
{
    /// <summary>What the machine's record's file name begins with.</summary>
    public const string FilePrefix = "web-search";

    /// <summary>What a line about the record is said under.</summary>
    public const string CommandName = "web-search";

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly Lazy<MachineWideList<WebSearchPaceEntry>> _record;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <summary>The pace kept in the record at <paramref name="path"/>.</summary>
    /// <param name="fileSystem">Reads and writes the record.</param>
    /// <param name="path">The record: <see cref="PathFor"/> where none is given; asked when it is first needed.</param>
    /// <param name="clock">Tells the time each request is counted at.</param>
    /// <param name="wait">Waits for a minute's turn.</param>
    public WebSearchPace(IFileSystem fileSystem, Func<string> path, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(wait);

        _record = new(() => new MachineWideList<WebSearchPaceEntry>(
            fileSystem,
            path(),
            "The record of the web search's sources",
            "and no source of the web search is asked until it can be",
            "Remove it: it holds no more than the pace of the last day's requests, and every source is asked from none."));
        _clock = clock;
        _wait = wait;
    }

    /// <summary>The machine's record, in this user's own directory for this tool (<see cref="UserState.MachineFile"/>).</summary>
    /// <param name="platform">Tells this machine from every other.</param>
    /// <param name="output">Says the record is named by the machine's name, where it is.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string PathFor(IHostPlatform platform, IHarnessOutput output)
        => UserState.MachineFile(platform, output, CommandName, FilePrefix, "search sources' paces", "paces its search sources beside the ones recorded under the old");

    /// <summary>
    /// Takes <paramref name="source"/>'s turn: waits while its minute is spent, then counts the request and answers
    /// <see langword="null"/>; or answers the hold - a pause it asked for, or its day spent - and counts nothing.
    /// </summary>
    /// <param name="source">The source asked.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="Results.HarnessException">The record could not be read or written.</exception>
    public async Task<WebSearchHold?> TurnAsync(WebSearchSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        while (true)
        {
            var now = _clock.GetUtcNow();
            var (hold, wait) = _record.Value.Update<(WebSearchHold? Hold, TimeSpan? Wait)>((entries, _) =>
            {
                var list = Live(entries, now);
                var entry = list.FirstOrDefault(held => held.Source == source.Name) ?? new WebSearchPaceEntry { Source = source.Name };

                if (Paused(entry, now) is { } paused)
                {
                    return (null, (paused, null));
                }

                if (source.PerDay is { } perDay && entry.Hours.Sum(hour => hour.Count) >= perDay)
                {
                    // Free once its oldest hour has wholly left the day, as Live drops it.
                    var until = entry.Hours.Min(hour => hour.Start) + Hour + Day;
                    return (null, (new WebSearchHold(until, $"its perDay of {perDay.ToString("N0", CultureInfo.InvariantCulture)} requests on this machine is spent until {WebRateLimit.Said(until)}", Paused: false), null));
                }

                if (entry.Minute.Count >= source.PerMinute)
                {
                    var free = entry.Minute.Min() + Minute - now;
                    return (null, (null, free > TimeSpan.Zero ? free : TimeSpan.FromMilliseconds(1)));
                }

                var hourStart = new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero);
                var hours = entry.Hours.Where(hour => hour.Start != hourStart).ToList();
                hours.Add(new WebSearchHour { Start = hourStart, Count = (entry.Hours.FirstOrDefault(hour => hour.Start == hourStart)?.Count ?? 0) + 1 });

                var taken = entry with { Minute = [.. entry.Minute, now], Hours = hours };

                return ([.. list.Where(held => held.Source != source.Name), taken], (null, null));
            });

            if (hold is not null || wait is not { } spent)
            {
                return hold;
            }

            await _wait(spent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Holds <paramref name="source"/> until <paramref name="until"/> on this machine, as <paramref name="why"/> says: a pause it asked for.</summary>
    /// <param name="source">The source's name.</param>
    /// <param name="until">When it may be asked again.</param>
    /// <param name="why">Why, as a search that finds it held says.</param>
    /// <exception cref="Results.HarnessException">The record could not be read or written.</exception>
    public void Pause(string source, DateTimeOffset until, string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        var now = _clock.GetUtcNow();

        _record.Value.Change((entries, _) =>
        {
            var list = Live(entries, now);
            var entry = list.FirstOrDefault(held => held.Source == source) ?? new WebSearchPaceEntry { Source = source };

            // A later pause holds; one ending sooner than the pause in force never shortens it.
            var paused = entry.PausedUntil is { } held && held >= until ? entry : entry with { PausedUntil = until, PausedWhy = why };

            return [.. list.Where(other => other.Source != source), paused];
        });
    }

    /// <summary>The pause <paramref name="source"/> is held to on this machine; <see langword="null"/> where none is in force.</summary>
    /// <param name="source">The source's name.</param>
    /// <exception cref="Results.HarnessException">The record could not be read.</exception>
    public WebSearchHold? Held(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return _record.Value.Read().FirstOrDefault(entry => entry.Source == source) is { } entry ? Paused(entry, _clock.GetUtcNow()) : null;
    }

    private static WebSearchHold? Paused(WebSearchPaceEntry entry, DateTimeOffset now)
        => entry is { PausedUntil: { } until, PausedWhy: { } why } && until > now ? new WebSearchHold(until, why, Paused: true) : null;

    /// <summary>The entries as they stand at <paramref name="now"/>: requests a minute or an hour's day past dropped, and a source with nothing left dropped whole.</summary>
    private static List<WebSearchPaceEntry> Live(IReadOnlyList<WebSearchPaceEntry> entries, DateTimeOffset now)
        => [.. entries
            .Select(entry => entry with
            {
                Minute = [.. entry.Minute.Where(at => at > now - Minute)],
                Hours = [.. entry.Hours.Where(hour => hour.Start + Hour > now - Day)],
                PausedUntil = entry.PausedUntil > now ? entry.PausedUntil : null,
                PausedWhy = entry.PausedUntil > now ? entry.PausedWhy : null,
            })
            .Where(entry => entry.Minute.Count > 0 || entry.Hours.Count > 0 || entry.PausedUntil is not null)];
}

/// <summary>Why a source is not asked now, and until when.</summary>
/// <param name="Until">When it may be asked again.</param>
/// <param name="Why">Why, as the agent is told.</param>
/// <param name="Paused">Whether it asked for the pause itself - a limit it answered - rather than its own pace being spent.</param>
public sealed record WebSearchHold(DateTimeOffset Until, string Why, bool Paused);

/// <summary>One source's pace in the machine's record.</summary>
internal sealed record WebSearchPaceEntry
{
    /// <summary>The source's name.</summary>
    public required string Source { get; init; }

    /// <summary>When each of its requests of the last minute was made.</summary>
    public List<DateTimeOffset> Minute { get; init; } = [];

    /// <summary>How many requests began in each hour of the last day.</summary>
    public List<WebSearchHour> Hours { get; init; } = [];

    /// <summary>When the pause it asked for ends; <see langword="null"/> where none holds.</summary>
    public DateTimeOffset? PausedUntil { get; init; }

    /// <summary>Why it is paused.</summary>
    public string? PausedWhy { get; init; }
}

/// <summary>How many requests of a source began in one hour.</summary>
internal sealed record WebSearchHour
{
    /// <summary>The hour's start.</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>How many requests began in it.</summary>
    public required int Count { get; init; }
}
