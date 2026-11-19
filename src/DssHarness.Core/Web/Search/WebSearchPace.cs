using System.Collections.Concurrent;
using System.Globalization;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Web.Search;

/// <summary>
/// The pace of each search source on this machine, whatever process asks it: a source that names its rate is asked no
/// more than its <c>perMinute</c> in any minute nor its <c>perDay</c> in any day; one that names none is asked one request
/// at a time, the next once the last is answered, paced by its answers; and a pause it asked for - a limit, a header, a
/// body's <c>backoff</c> - holds it until the time it gave, a wait it asked for without saying how long doubled from its
/// pace each time it asks so again. One record of the machine's, beside admission's, read, decided on and written back as
/// one step (<see cref="MachineWideList{TEntry}"/>), so two runs never both take the last turn.
/// </summary>
/// <remarks>
/// A minute is counted by the times of its requests, a day by the hour each request began in - so a day's count holds
/// hours, never one time for each request, and a day spent is spent until the end of its oldest hour is a day old. A
/// source asked one request at a time is held by the fetch that asks it, by its process and that process's stamp, until
/// the fetch ends: a holder whose process has ended, or one of this process's that no fetch of it holds any more, holds
/// nothing.
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
    private readonly IProcessIdentity _identity;
    private readonly ConcurrentDictionary<string, byte> _open = new(StringComparer.Ordinal);

    /// <summary>The pace kept in the record at <paramref name="path"/>.</summary>
    /// <param name="fileSystem">Reads and writes the record.</param>
    /// <param name="path">The record: <see cref="PathFor"/> where none is given; asked when it is first needed.</param>
    /// <param name="clock">Tells the time each request is counted at.</param>
    /// <param name="wait">Waits for a minute's turn.</param>
    /// <param name="identity">Tells whether the process that holds a source asked one request at a time still runs.</param>
    public WebSearchPace(IFileSystem fileSystem, Func<string> path, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait, IProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(wait);
        ArgumentNullException.ThrowIfNull(identity);

        _record = new(() => new MachineWideList<WebSearchPaceEntry>(
            fileSystem,
            path(),
            "The record of the web search's sources",
            "and no source of the web search is asked until it can be",
            "Remove it: it holds no more than the pace of the last day's requests, and every source is asked from none."));
        _clock = clock;
        _wait = wait;
        _identity = identity;
    }

    /// <summary>The machine's record, in this user's own directory for this tool (<see cref="UserState.MachineFile"/>).</summary>
    /// <param name="platform">Tells this machine from every other.</param>
    /// <param name="output">Says the record is named by the machine's name, where it is.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string PathFor(IHostPlatform platform, IHarnessOutput output)
        => UserState.MachineFile(platform, output, CommandName, FilePrefix, "search sources' paces", "paces its search sources beside the ones recorded under the old");

    /// <summary>
    /// Takes <paramref name="source"/>'s turn: waits while its minute is spent - or, for a source asked one request at a
    /// time, while another fetch holds it, told to <paramref name="tell"/> as every wait is - then counts the request and
    /// answers its turn, the hold it took where it takes one; or answers the hold - a pause it asked for, or its day spent -
    /// and counts nothing.
    /// </summary>
    /// <param name="source">The source asked.</param>
    /// <param name="held">The hold the asking fetch took at its first address, where it took one: its redirects are its own request, never waited for behind itself.</param>
    /// <param name="tell">Told where a wait for another fetch's turn stands; nothing is told where none is given.</param>
    /// <param name="cancellationToken">Stops the wait.</param>
    /// <exception cref="Results.HarnessException">The record could not be read or written.</exception>
    public async Task<WebSearchTurn> TurnAsync(WebSearchSource source, string? held, Action<WaitTold>? tell, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        while (true)
        {
            var now = _clock.GetUtcNow();
            var (turn, wait, asking) = _record.Value.Update<(WebSearchTurn? Turn, TimeSpan? Wait, bool Asking)>((entries, _) =>
            {
                var list = Live(entries, now);
                var entry = list.FirstOrDefault(kept => kept.Source == source.Name) ?? new WebSearchPaceEntry { Source = source.Name };

                if (Paused(entry, now) is { } paused)
                {
                    return (null, (new WebSearchTurn(paused, null), null, false));
                }

                if (source.PerDay is { } perDay && entry.Hours.Sum(hour => (long)hour.Count) >= perDay)
                {
                    // Free once its oldest hour has wholly left the day, as Live drops it.
                    var until = entry.Hours.Min(hour => hour.Start) + Hour + Day;
                    return (null, (new WebSearchTurn(new WebSearchHold(until, $"its perDay of {perDay.ToString("N0", CultureInfo.InvariantCulture)} requests on this machine is spent until {WebRateLimit.Said(until)}", Paused: false), null), null, false));
                }

                if (source.PerMinute is { } perMinute && entry.Minute.Count >= perMinute)
                {
                    var free = entry.Minute.Min() + Minute - now;
                    return (null, (null, free > TimeSpan.Zero ? free : TimeSpan.FromMilliseconds(1), false));
                }

                // A source that names no rate is asked one request at a time: another fetch's hold is waited out.
                string? lease = null;

                if (source.PerMinute is null)
                {
                    if (entry.Asking is { } asking && asking.Lease != held && Holds(asking))
                    {
                        return (null, (null, null, true));
                    }

                    lease = held ?? Guid.NewGuid().ToString("N");
                }

                var hourStart = new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.UtcDateTime.Hour), TimeSpan.Zero);
                var hours = entry.Hours.Where(hour => hour.Start != hourStart).ToList();
                hours.Add(new WebSearchHour { Start = hourStart, Count = (entry.Hours.FirstOrDefault(hour => hour.Start == hourStart)?.Count ?? 0) + 1 });

                var taken = entry with
                {
                    Minute = [.. entry.Minute, now],
                    Hours = hours,
                    Asking = lease is null ? entry.Asking : new WebSearchAsking { Lease = lease, ProcessId = _identity.CurrentId, Stamp = _identity.Current, Since = entry.Asking?.Lease == lease ? entry.Asking.Since : now },
                };

                if (lease is not null)
                {
                    _open[lease] = 0;
                }

                return ([.. list.Where(kept => kept.Source != source.Name), taken], (new WebSearchTurn(null, lease), null, false));
            });

            if (turn is not null)
            {
                return turn;
            }

            if (asking)
            {
                // Waited for as every hold is: for as long as its holder lives, looked at again each second, told every five
                // minutes; a holder found ended holds nothing, and the next look takes the turn.
                var waited = await Waits.WhileHeldAsync(
                    _ => Task.FromResult(Holder(source.Name)),
                    _identity,
                    new WaitWatch($"{source.Name}'s turn, which it gives one request at a time", tell ?? (_ => { }), null, _clock),
                    cancellationToken).ConfigureAwait(false);

                if (waited.Kind == WaitEndKind.Cancelled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                continue;
            }

            await _wait(wait!.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lets go of the turn <paramref name="lease"/> holds of <paramref name="source"/>, which the fetch that took it has
    /// done with: the next fetch of it, in any process, may ask. Where the record cannot be written, this process holds it
    /// no more all the same, and its next turn takes it back.
    /// </summary>
    /// <param name="source">The source's name.</param>
    /// <param name="lease">The hold its turn took (<see cref="WebSearchTurn.Lease"/>).</param>
    /// <exception cref="Results.HarnessException">The record could not be read or written.</exception>
    public void Done(string source, string lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease);

        _open.TryRemove(lease, out _);

        var now = _clock.GetUtcNow();

        _record.Value.Change((entries, _) =>
        {
            var list = Live(entries, now);
            return [.. list.Select(entry => entry.Source == source && entry.Asking?.Lease == lease ? entry with { Asking = null } : entry)];
        });
    }

    /// <summary>
    /// Holds <paramref name="source"/> until <paramref name="until"/> on this machine, as <paramref name="why"/> says: a pause
    /// it asked for - <paramref name="unsaid"/> where it said not for how long, which the next such pause doubles from.
    /// </summary>
    /// <param name="source">The source's name.</param>
    /// <param name="until">When it may be asked again.</param>
    /// <param name="why">Why, as a search that finds it held says.</param>
    /// <param name="unsaid">The wait it was left for where it said not how long (<see cref="WebRateLimit.Unsaid"/>); <see langword="null"/> where it said its time.</param>
    /// <exception cref="Results.HarnessException">The record could not be read or written.</exception>
    public void Pause(string source, DateTimeOffset until, string why, TimeSpan? unsaid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        var now = _clock.GetUtcNow();

        _record.Value.Change((entries, _) =>
        {
            var list = Live(entries, now);
            var entry = list.FirstOrDefault(other => other.Source == source) ?? new WebSearchPaceEntry { Source = source };

            // A later pause holds; one ending sooner than the pause in force never shortens it. The doubling follows what the
            // source last said: a time it gave begins it again.
            var paused = (entry.PausedUntil is { } standing && standing >= until ? entry : entry with { PausedUntil = until, PausedWhy = why }) with { UnsaidFor = unsaid };

            return [.. list.Where(other => other.Source != source), paused];
        });
    }

    /// <summary>
    /// How long <paramref name="source"/> is left where it next asks to be left alone and says not for how long: its own
    /// pace's interval the first time, doubled from the last such wait (<see cref="WebRateLimit.Unsaid"/>).
    /// </summary>
    /// <param name="source">The source.</param>
    /// <exception cref="Results.HarnessException">The record could not be read.</exception>
    public TimeSpan Unsaid(WebSearchSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var last = _record.Value.Read().FirstOrDefault(entry => entry.Source == source.Name)?.UnsaidFor;
        return WebRateLimit.Unsaid(last, source.PerMinute is { } perMinute ? Minute / perMinute : null);
    }

    /// <summary>Forgets the waits <paramref name="source"/> asked for without saying how long: it answered, and the next such wait begins from its pace again.</summary>
    /// <param name="source">The source's name.</param>
    /// <exception cref="Results.HarnessException">The record could not be read or written.</exception>
    public void Answered(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (_record.Value.Read().FirstOrDefault(entry => entry.Source == source) is not { UnsaidFor: not null })
        {
            return;
        }

        var now = _clock.GetUtcNow();
        _record.Value.Change((entries, _) => [.. Live(entries, now).Select(entry => entry.Source == source ? entry with { UnsaidFor = null } : entry)]);
    }

    /// <summary>The pause <paramref name="source"/> is held to on this machine; <see langword="null"/> where none is in force.</summary>
    /// <param name="source">The source's name.</param>
    /// <exception cref="Results.HarnessException">The record could not be read.</exception>
    public WebSearchHold? Held(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        return _record.Value.Read().FirstOrDefault(entry => entry.Source == source) is { } entry ? Paused(entry, _clock.GetUtcNow()) : null;
    }

    /// <summary>
    /// Whether <paramref name="asking"/> holds its source: its process runs, and - where it is this process - a fetch of
    /// this process still holds it, for one that let go of it without the record saying so holds nothing.
    /// </summary>
    private bool Holds(WebSearchAsking asking)
        => asking.ProcessId == _identity.CurrentId && asking.Stamp == _identity.Current
            ? _open.ContainsKey(asking.Lease)
            : _identity.IsAlive(asking.ProcessId, asking.Stamp);

    /// <summary>Who holds <paramref name="source"/> as the record says it now, as a wait names it; <see langword="null"/> where nothing does.</summary>
    private WaitHolder? Holder(string source)
        => _record.Value.Read().FirstOrDefault(entry => entry.Source == source)?.Asking is { } asking
            && (asking.ProcessId != _identity.CurrentId || asking.Stamp != _identity.Current || _open.ContainsKey(asking.Lease))
            ? new WaitHolder(asking.ProcessId, asking.Stamp, $"pid {asking.ProcessId.ToString(CultureInfo.InvariantCulture)}, asking it since {WebRateLimit.Said(asking.Since)}")
            : null;

    private static WebSearchHold? Paused(WebSearchPaceEntry entry, DateTimeOffset now)
        => entry is { PausedUntil: { } until, PausedWhy: { } why } && until > now ? new WebSearchHold(until, why, Paused: true) : null;

    /// <summary>
    /// The entries as they stand at <paramref name="now"/>: requests a minute or an hour's day past dropped, and a source with
    /// nothing left - no request, pause, hold or doubled wait - dropped whole.
    /// </summary>
    private static List<WebSearchPaceEntry> Live(IReadOnlyList<WebSearchPaceEntry> entries, DateTimeOffset now)
        => [.. entries
            .Select(entry => entry with
            {
                Minute = [.. entry.Minute.Where(at => at > now - Minute)],
                Hours = [.. entry.Hours.Where(hour => hour.Start + Hour > now - Day)],
                PausedUntil = entry.PausedUntil > now ? entry.PausedUntil : null,
                PausedWhy = entry.PausedUntil > now ? entry.PausedWhy : null,
            })
            .Where(entry => entry.Minute.Count > 0 || entry.Hours.Count > 0 || entry.PausedUntil is not null || entry.Asking is not null || entry.UnsaidFor is not null)];
}

/// <summary>A source's turn: the hold that refused it, or the request it counted, with the hold it took where it is asked one request at a time.</summary>
/// <param name="Hold">Why it is not asked now, and until when; <see langword="null"/> where its turn was taken.</param>
/// <param name="Lease">What holds it for the fetch that took the turn, let go once that fetch ends (<see cref="WebSearchPace.Done"/>); <see langword="null"/> where its pace holds nothing past a request.</param>
public sealed record WebSearchTurn(WebSearchHold? Hold, string? Lease);

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

    /// <summary>The last wait it asked for without saying how long, which the next such wait doubles from; <see langword="null"/> where its last answer said its time, or was good.</summary>
    public TimeSpan? UnsaidFor { get; init; }

    /// <summary>The fetch asking it, where it is asked one request at a time; <see langword="null"/> where none is.</summary>
    public WebSearchAsking? Asking { get; init; }
}

/// <summary>The fetch that holds a source asked one request at a time: what it holds it by, its process, and since when.</summary>
internal sealed record WebSearchAsking
{
    /// <summary>What the fetch holds it by, let go when the fetch ends.</summary>
    public required string Lease { get; init; }

    /// <summary>The fetch's process.</summary>
    public required int ProcessId { get; init; }

    /// <summary>What tells that process from a later one of its id (<see cref="IProcessIdentity.Current"/>).</summary>
    public string? Stamp { get; init; }

    /// <summary>When it first asked.</summary>
    public required DateTimeOffset Since { get; init; }
}

/// <summary>How many requests of a source began in one hour.</summary>
internal sealed record WebSearchHour
{
    /// <summary>The hour's start.</summary>
    public required DateTimeOffset Start { get; init; }

    /// <summary>How many requests began in it.</summary>
    public required int Count { get; init; }
}
