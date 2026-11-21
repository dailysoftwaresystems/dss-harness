using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Waiting;
using DssHarness.Core.Walks;

namespace DssHarness.Core.Web.Index;

/// <summary>
/// Fills the index of a site: its <c>robots.txt</c> read whole, then every one of its sitemaps - those the source names,
/// else those <c>robots.txt</c> names, else <c>/sitemap.xml</c>, an index's sitemaps after it, gzip too, each met once
/// however many name it (<see cref="Walk"/>) - for every page the site lets this tool read and the source's patterns
/// include; then each page whose <c>lastmod</c> moved, then each not yet read, then each with no <c>lastmod</c> read longer
/// ago than the index may be old, then each that could not be read that long ago - one that could not be read since waits -
/// through the harness's fetch, every request a redirect's too, at the site's <c>Crawl-delay</c> and in the source's turn
/// on this machine (the run's gate's pace), each address held to <c>robots.txt</c> before it is asked: a page always, a
/// sitemap of the site's own unless <c>robots.txt</c> names it. Each page is indexed as it is read, so a search answers
/// from what is held while the rest are read. A page that could not be read is recorded, and the fill goes on to the
/// next. A page the sitemaps no longer list is dropped, once every sitemap was read; one that could not be leaves the
/// pages held as they were, and the fill is read again later. The site asking to be left alone, or saying its requests
/// are spent, and the source's turn spent end the fill until the time given - a wait it asks for without saying how long
/// doubled from its crawl delay each time it asks so again; robots.txt that cannot be read, as RFC 9309 asks, ends it
/// reading nothing; a fill that stopped short for any other reason is begun again by the next search.
/// </summary>
/// <remarks>
/// One fill of an index at a time, on the whole machine: the fill holds <c>fill.jsonl</c> - its record of every address it
/// asked - open as its one writer, which a second fill, in this process or another, is refused. Each request and what it
/// came to is a line of it; the record keeps each address's latest line, written anew once lines an address's later line
/// replaced outnumber those kept and <see cref="MostStaleLines"/> - never emptied.
/// </remarks>
public sealed class SitemapFill
{
    /// <summary>The fill's record, in the index's directory.</summary>
    public const string RecordFileName = "fill.jsonl";

    /// <summary>
    /// How many lines replaced or gone an index's file - and the fill's record, lines an address's later line replaced -
    /// keeps however few it holds, before it is written anew with each one's latest line alone: a size of the writing, never
    /// a bound on what is kept.
    /// </summary>
    public const int MostStaleLines = 50;

    /// <summary>Why a fill is held off where the site answered it is to be left alone.</summary>
    public const string SiteLimited = "the site asks to be left alone";

    /// <summary>Why a fill is held off where the site said its requests are spent, for longer than the fill waits between two.</summary>
    public const string SiteSpent = "the site says its requests are spent";

    /// <summary>Why a fill is held off where the source's turn on this machine is spent.</summary>
    public const string TurnSpent = "the source's requests on this machine are spent";

    /// <summary>The rule an address robots.txt or the source's patterns leave unread is refused by, as a denial names it.</summary>
    private const string RobotsRule = "robots.txt";

    private readonly IFileSystem _fileSystem;
    private readonly HarnessFetch _fetch;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <summary>A fill through <paramref name="fetch"/>, its crawl delay kept by <paramref name="clock"/> and waited by <paramref name="wait"/>.</summary>
    /// <param name="fileSystem">Where the index is kept.</param>
    /// <param name="fetch">The harness's fetch.</param>
    /// <param name="clock">The clock: when a page was read, and how long since the last request.</param>
    /// <param name="wait">Waits so long between two requests: the clock's own delay, or a test's.</param>
    public SitemapFill(IFileSystem fileSystem, HarnessFetch fetch, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> wait)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(wait);

        _fileSystem = fileSystem;
        _fetch = fetch;
        _clock = clock;
        _wait = wait;
    }

    /// <summary>
    /// Takes the fill of the index in <paramref name="directory"/> - made where it is not there - for this fill alone; or
    /// <see langword="null"/> where another fill holds it.
    /// </summary>
    /// <param name="directory">The index's directory.</param>
    /// <exception cref="IOException">
    /// The fill's record could not be opened for any reason but another fill holding it - a read-only file system, a disk's
    /// failure, a torn line it could not take back - which is no fill held elsewhere, and is said as what it is.
    /// </exception>
    public IDisposable? Claim(string directory)
    {
        _fileSystem.CreateDirectory(directory);

        try
        {
            return AppendLog.Open(_fileSystem, Path.Combine(directory, RecordFileName));
        }
        catch (AppendFileHeldException)
        {
            return null;
        }
    }

    /// <summary>Fills <paramref name="index"/> as <paramref name="settings"/> say, its fill held by <paramref name="claim"/>, which it lets go once done.</summary>
    /// <param name="claim">The fill, as <see cref="Claim"/> took it.</param>
    /// <param name="index">The index.</param>
    /// <param name="settings">What it reads.</param>
    /// <param name="runGate">
    /// The run's gate, never one step's: it decides, records and paces every address the fill asks, until the fill ends -
    /// long after the search that began it. It gives the source's turn (<see cref="WebFetchGate.Pace"/>): a fill asks a site
    /// many times, and is never unpaced.
    /// </param>
    /// <param name="cancellationToken">Stops it, between two requests or inside one; what it read is kept.</param>
    public async Task RunAsync(IDisposable claim, WebIndex index, SitemapSettings settings, WebFetchGate runGate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(settings);
        Paced(runGate);

        using var record = claim as AppendLog ?? throw new ArgumentException("A fill is run under the claim Claim took.", nameof(claim));

        Compact(record);

        var run = new Run(this, record, index, settings, runGate, cancellationToken);
        await run.FillAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the fill's <paramref name="record"/> anew with each address's latest line alone, in their order, where lines a
    /// later line of their address replaced outnumber those kept and <see cref="MostStaleLines"/>: the record grows with
    /// the site, never with its fills, and keeps what each address last came to. Read a piece at a time.
    /// </summary>
    internal static void Compact(AppendLog record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var latest = new Dictionary<string, (long At, string Line)>(StringComparer.Ordinal);
        var lines = 0L;

        foreach (var line in record.Lines())
        {
            // A line that names no address - none this fill writes - is kept as its own.
            latest[AddressOf(line) ?? $"\u0000{lines.ToString(CultureInfo.InvariantCulture)}"] = (lines, line);
            lines++;
        }

        if (lines - latest.Count <= Math.Max(latest.Count, MostStaleLines))
        {
            return;
        }

        record.Clear();

        foreach (var (_, line) in latest.Values.OrderBy(kept => kept.At))
        {
            record.Append(line);
        }

        static string? AddressOf(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line, Json.JsonDepth.Document);
                return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String
                    ? address.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Refuses a gate that gives no turn: a fill asks a site many times, and is never unpaced.</summary>
    /// <param name="runGate">The run's gate.</param>
    /// <exception cref="ArgumentException">It gives no turn.</exception>
    internal static void Paced(WebFetchGate runGate)
    {
        ArgumentNullException.ThrowIfNull(runGate);

        if (runGate.Pace is null)
        {
            throw new ArgumentException("A fill asks a site many times, and is paced by its source's turn on this machine, which the run's gate gives; this one gives none.", nameof(runGate));
        }
    }

    /// <summary>
    /// What the sitemaps list that the fill may read; why the listing is not whole (<paramref name="Short"/>) - and whether
    /// for the site's failure (<paramref name="Failed"/>), asked again by the next search, or by a rule of the site's or the
    /// protocol's, which holds until the index is old; and, where it is whole and lists none, what kept each page it lists
    /// from the fill.
    /// </summary>
    private sealed record Listing(List<(string Address, string? LastModified)> Pages, string? Short, bool Failed, string? Empty);

    /// <summary>One fill, start to end: when it asked last, and its crawl delay.</summary>
    private sealed class Run(SitemapFill fill, AppendLog record, WebIndex index, SitemapSettings settings, WebFetchGate gate, CancellationToken cancellationToken)
    {
        private long? _asked;
        private DateTimeOffset? _spent;
        private TimeSpan? _unsaid;
        private string? _firstFailure;
        private DateTimeOffset? _waitingUntil;
        private string? _waitingFirst;
        private TimeSpan _delay = TimeSpan.Zero;
        private WebIndexState _state = WebIndexState.Read(fill._fileSystem, index.Directory, settings.Site);

        public async Task FillAsync()
        {
            // Why a fill stopped short is kept until one ends whole: a fill held off before it gets as far leaves it as true as it was.
            _state = _state with { FillStartedAt = fill._clock.GetUtcNow(), PausedUntil = null, PausedFor = null };
            _unsaid = _state.UnsaidFor;
            _state.Write(fill._fileSystem, index.Directory);

            var robots = await RobotsAsync().ConfigureAwait(false);

            if (robots is null)
            {
                return;
            }

            _delay = robots.CrawlDelay ?? TimeSpan.Zero;

            var listing = await SitemapsAsync(robots).ConfigureAwait(false);

            if (listing is null)
            {
                return;
            }

            // A listing not read whole lists fewer than the site holds: the count one gave under these settings is kept, where it is more.
            var known = listing.Short is null || !string.Equals(_state.KnownUnder, settings.Fingerprint, StringComparison.Ordinal) ? 0 : _state.Known ?? 0;
            _state = _state with
            {
                Known = Math.Max(known, listing.Pages.Count),
                KnownUnder = settings.Fingerprint,
            };
            _state.Write(fill._fileSystem, index.Directory);

            if (!await PagesAsync(robots, listing).ConfigureAwait(false))
            {
                return;
            }

            index.Refresh();

            // Written anew once lines replaced and gone outnumber the lines kept, so the file grows with the site, not with its reads.
            if (index.Lines - index.Held - index.Failed > Math.Max(index.Held + index.Failed, MostStaleLines))
            {
                _state = index.Compact() with { Known = _state.Known, FillStartedAt = _state.FillStartedAt };
            }

            // The site failed the listing: asked again by the next search. A listing short by a rule - the site's, the
            // protocol's - that lists nothing, and nothing held: asked again when the index is old, or at once under settings
            // changed. Pages it lists that could not be read are said below, as a whole listing's are.
            if ((listing.Failed ? listing.Short : listing.Empty) is { } failure)
            {
                Stop(failure);
                return;
            }

            if (listing.Short is { } bounded && listing.Pages.Count == 0 && index.HeldOf(settings.ReadsPage) == 0)
            {
                Stop(bounded, WebRateLimit.After(fill._clock.GetUtcNow(), settings.Refresh));
                return;
            }

            // A fill that holds nothing a search may find has failed, however whole its reading: its source answers nothing.
            // Where it asked no page - each waiting, as one that could not be read does - why one stopped short before stands,
            // and the next waits for the first of them.
            if (index.HeldOf(settings.ReadsPage) == 0)
            {
                var none = $"none of the {ReportText.Counted(listing.Pages.Count, "page")} its sitemaps list that it may read could be read";

                if (_firstFailure is { } first)
                {
                    Stop($"{none}, the first {first}");
                }
                else
                {
                    Stop($"{none}, the first {_waitingFirst}; each waits as long as a page read does before it is asked again", _waitingUntil);
                }

                return;
            }

            // Read as far as its rules let: ended, the index read again when it is old, saying what of the listing it left unread.
            _state = _state with { FilledAt = fill._clock.GetUtcNow(), Settings = settings.Fingerprint, Problem = listing.Short, UnsaidFor = _unsaid };
            _state.Write(fill._fileSystem, index.Directory);
        }

        /// <summary>The site's robots.txt read; <see langword="null"/> where the fill ends on it.</summary>
        private async Task<RobotsTxt?> RobotsAsync()
        {
            var address = $"{settings.Site}/robots.txt";

            // Its text as sent, whatever type it is said to be, or none: a robots.txt said to be HTML and read as a page's text
            // would lose its lines, and refuse nothing; one said to be bytes is text by its name, and answered (RFC 9309, 2.3.1.1);
            // read whole, every rule its site wrote, past the 500 KiB a crawler parses at least (2.5).
            if (await AskAsync(address, new WebFetchAsk { Html = WebFetchHtml.Markup, Accept = "text/plain", AnyType = true }, readable: null).ConfigureAwait(false) is not { } fetched)
            {
                return null;
            }

            if (fetched.Denial is { } denial)
            {
                Stop($"the policy refuses its robots.txt: {denial.Reason}");
                return null;
            }

            return fetched.Status switch
            {
                >= 200 and <= 299 when fetched.Text is { } text => RobotsTxt.Read(text, ToolPackage.Command),

                // A robots.txt that is not there, or that its site will not give, refuses nothing (RFC 9309, 2.3.1.3).
                >= 400 and <= 499 => RobotsTxt.AllowAll,
                _ => Unread(),
            };

            RobotsTxt? Unread()
            {
                Stop($"its robots.txt could not be read ({fetched.Failure ?? $"it answered {fetched.Status?.ToString(CultureInfo.InvariantCulture)}"}), and a site that cannot say what it allows is read nothing of (RFC 9309, 2.3.1.4)");
                return null;
            }
        }

        /// <summary>
        /// Every page its sitemaps list that the fill may read, in their order, and why the listing is not whole where a
        /// sitemap could not be read or lists what the sitemaps protocol refuses; <see langword="null"/> where the fill ends
        /// on them.
        /// </summary>
        private async Task<Listing?> SitemapsAsync(RobotsTxt robots)
        {
            // A sitemap robots.txt names is one the site gives; any other of the site's own is held to its rules, as a page is.
            var named = robots.Sitemaps.Select(sitemap => WebAddresses.Absolute(sitemap, settings.Site)).OfType<string>().ToList();
            var readable = (string address) => named.Contains(address, StringComparer.Ordinal) || !OnSite(address) || robots.Allows(new Uri(address).PathAndQuery);

            IReadOnlyList<string> starts = settings.Sitemap is { } given ? [given]
                : named.Count > 0 ? named
                : [$"{settings.Site}/sitemap.xml"];

            // Every sitemap reached, by levels, each met once however many name it - an index naming itself or another that
            // names it back ends where it closes: the sitemaps a sitemap names are its next, stashed as it is read.
            var stashed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var pages = new List<(string, string?)>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var read = 0;
            var unread = 0;
            var denied = 0;
            string? firstDenied = null;
            var elsewhere = 0;
            var refused = 0;
            var protocol = new List<string>();
            string? firstProblem = null;

            foreach (var sitemap in Walk.Visit(starts, address => address, address => stashed.Remove(address, out var children) ? children : [], WalkOrder.Breadth, StringComparer.Ordinal))
            {
                read++;

                // Its markup kept, whatever type it is said to be, or none: an XML sitemap said to be HTML, or bytes, is read as XML.
                if (await AskAsync(sitemap, new WebFetchAsk { Html = WebFetchHtml.Markup, Gzip = true, Accept = "application/xml, text/xml;q=0.9, text/plain;q=0.8", AnyType = true, Most = SitemapDocument.MostBytes }, readable).ConfigureAwait(false) is not { } fetched)
                {
                    return null;
                }

                // One the site's robots.txt or the policy leaves unread is a rule of theirs, no failure of the site's.
                if (fetched.Denial is { } denial)
                {
                    denied++;
                    firstDenied ??= $"'{sitemap}': {denial.Reason}";
                    Note(sitemap, $"no sitemap: refused: {denial.Reason}");
                    continue;
                }

                var document = fetched.Text is { } text && fetched.Status is >= 200 and <= 299
                    ? SitemapDocument.Read(text, fetched.Address)
                    : new SitemapDocument([], [], fetched.Failure ?? $"it answered {fetched.Status?.ToString(CultureInfo.InvariantCulture)}");

                if (document.Problem is { } problem)
                {
                    unread++;
                    firstProblem ??= $"'{sitemap}': {problem}";
                    Note(sitemap, $"no sitemap: {problem}");
                    continue;
                }

                stashed[sitemap] = document.Sitemaps;

                // What the protocol refuses is said, each by name, and is no page: a listing that holds it is not whole.
                foreach (var (loc, why) in document.Refused)
                {
                    Note(loc, $"no page: {why}");
                }

                if (document.Refused.Count > 0)
                {
                    protocol.Add($"'{sitemap}' lists {ReportText.Counted(document.Refused.Count, "address")} the sitemaps protocol refuses, the first '{document.Refused[0].Loc}': {document.Refused[0].Why}");
                }

                if (document.Unread > 0)
                {
                    protocol.Add($"'{sitemap}' lists {document.Unread.ToString("N0", CultureInfo.InvariantCulture)} entries past the {SitemapDocument.MostEntries.ToString("N0", CultureInfo.InvariantCulture)} the sitemaps protocol lets one sitemap hold, which were not read");
                }

                foreach (var (address, lastModified) in document.Pages)
                {
                    // A sitemap lists as many as its site likes, each held to robots.txt and the source's patterns: the fill
                    // is stopped between any two.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!Readable(address, robots))
                    {
                        if (OnSite(address))
                        {
                            refused++;
                        }
                        else
                        {
                            elsewhere++;
                        }
                    }
                    else if (taken.Add(address))
                    {
                        pages.Add((address, lastModified));
                    }
                }
            }

            var problems = new List<string>();

            if (firstProblem is not null)
            {
                problems.Add(unread == read ? $"no sitemap of it could be read, the first {firstProblem}" : $"{unread.ToString(CultureInfo.InvariantCulture)} of its sitemaps could not be read, the first {firstProblem}");
            }

            if (firstDenied is not null)
            {
                problems.Add($"{denied.ToString(CultureInfo.InvariantCulture)} of its sitemaps the source may not read, the first {firstDenied}");
            }

            problems.AddRange(protocol);

            // Nothing to read, where none is listed: said as what kept each listed page from the fill, beside what was not read.
            if (problems.Count == 0)
            {
                return new Listing(pages, null, Failed: false, pages.Count == 0 ? NoPage() : null);
            }

            if (pages.Count > 0)
            {
                problems.Add("the pages it holds that the rest list are kept");
            }
            else if (unread + denied < read)
            {
                problems.Add($"and {NoPage()}");
            }

            return new Listing(pages, string.Join("; ", problems), Failed: firstProblem is not null, null);

            string NoPage()
            {
                if (elsewhere + refused == 0)
                {
                    return "its sitemaps list no page";
                }

                var why = new List<string>();

                if (elsewhere > 0)
                {
                    why.Add($"{elsewhere.ToString(CultureInfo.InvariantCulture)} of another site than '{settings.Site}'");
                }

                if (refused > 0)
                {
                    why.Add($"{refused.ToString(CultureInfo.InvariantCulture)} left unread by its robots.txt or the source's include and exclude");
                }

                return $"its sitemaps list {ReportText.Counted(elsewhere + refused, "page")}, none the source may read: {string.Join(", ", why)}";
            }
        }

        /// <summary>
        /// Reads the pages due, in their order, each that could not be read remembered so the next fill asks it last, and
        /// drops those no longer listed where the listing is whole; false where the fill ends among them.
        /// </summary>
        private async Task<bool> PagesAsync(RobotsTxt robots, Listing listing)
        {
            index.Refresh();

            var held = index.Pages();
            var failed = index.Failures();
            var known = listing.Pages;
            var now = fill._clock.GetUtcNow();

            using var pages = AppendLog.Open(fill._fileSystem, index.PagesFile);

            // A page the site or the source no longer lets the fill read is gone, whatever its listing, and one a listing read
            // whole no longer lists. One a listing read short does not list is not known to be gone, and is kept.
            // A failure the listing does not name is forgotten: it holds no page, and is asked last again where named again.
            var listed = known.Select(page => page.Address).ToHashSet(StringComparer.Ordinal);
            var unlisted = held.Keys.Where(address => !listed.Contains(address)).ToList();

            foreach (var gone in unlisted.Where(address => !Readable(address, robots) || listing.Short is null)
                .Concat(failed.Keys.Where(address => !listed.Contains(address) && !held.ContainsKey(address))))
            {
                pages.Append(WebIndex.GoneLine(gone));
            }

            // A page that could not be read waits as long as a page read does before it is asked again, and is asked last.
            bool Waits(string address) => failed.TryGetValue(address, out var was) && now - was.At < settings.Refresh;

            var waiting = known.Where(page => !held.ContainsKey(page.Address) && Waits(page.Address)).Select(page => (page.Address, Failure: failed[page.Address])).ToList();
            _waitingUntil = waiting.Count > 0 ? WebRateLimit.After(waiting.Min(page => page.Failure.At), settings.Refresh) : null;
            _waitingFirst = waiting.Count > 0 ? $"'{waiting[0].Address}': {waiting[0].Failure.Why}" : null;

            var moved = known.Where(page => held.TryGetValue(page.Address, out var was) && page.LastModified is not null && page.LastModified != was.LastModified && !Waits(page.Address));
            var unread = known.Where(page => !held.ContainsKey(page.Address) && !failed.ContainsKey(page.Address));
            var aged = known.Where(page => held.TryGetValue(page.Address, out var was) && page.LastModified is null && now - was.ReadAt >= settings.Refresh && !Waits(page.Address));
            var again = known.Where(page => !held.ContainsKey(page.Address) && failed.ContainsKey(page.Address) && !Waits(page.Address));

            foreach (var (address, lastModified) in moved.Concat(unread).Concat(aged).Concat(again).ToList())
            {
                if (await AskAsync(address, new WebFetchAsk { Html = WebFetchHtml.Parts }, page => Readable(page, robots)).ConfigureAwait(false) is not { } fetched)
                {
                    return false;
                }

                // A page refused - a redirect to where it may not be read - waits as one that could not be read does.
                if (fetched.Denial is { } denial)
                {
                    var refused = $"refused: {denial.Reason}";
                    _firstFailure ??= $"'{address}': {refused}";
                    pages.Append(WebIndex.FailedLine(address, fill._clock.GetUtcNow(), refused));
                    continue;
                }

                if (fetched.Status is 404 or 410)
                {
                    if (held.ContainsKey(address))
                    {
                        pages.Append(WebIndex.GoneLine(address));
                    }

                    _firstFailure ??= $"'{address}': it answered {fetched.Status.Value.ToString(CultureInfo.InvariantCulture)}";
                    pages.Append(WebIndex.FailedLine(address, fill._clock.GetUtcNow(), $"it answered {fetched.Status.Value.ToString(CultureInfo.InvariantCulture)}"));
                    continue;
                }

                // Read where it ended, its redirects followed - each address held to robots.txt before it was asked.
                if (fetched.Text is { } text && fetched.Status is >= 200 and <= 299)
                {
                    var parts = fetched.Parts ?? new WebPageParts(string.Empty, [], text);

                    pages.Append(WebIndex.PageLine(address, lastModified, fill._clock.GetUtcNow(), parts));
                    continue;
                }

                // No answer, a server's error, a refusal of this tool: recorded, asked last by the next fill, and the fill goes
                // on to the next page - however many failed before it.
                var why = fetched.Failure ?? $"it answered {fetched.Status?.ToString(CultureInfo.InvariantCulture)}";
                _firstFailure ??= $"'{address}': {why}";
                pages.Append(WebIndex.FailedLine(address, fill._clock.GetUtcNow(), why));
            }

            return true;
        }

        /// <summary>Whether <paramref name="address"/> is a page of the site that its robots.txt and the source's patterns let the fill read.</summary>
        private bool Readable(string address, RobotsTxt robots)
            => settings.ReadsPage(address) && robots.Allows(new Uri(address).PathAndQuery);

        /// <summary>Whether <paramref name="address"/> is of the site itself, whose robots.txt the fill read.</summary>
        private bool OnSite(string address)
            => Uri.TryCreate(address, UriKind.Absolute, out var uri) && string.Equals(uri.GetLeftPart(UriPartial.Authority), settings.Site, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Asks <paramref name="address"/>, each address of it - the first and each redirect - held to <paramref name="readable"/>
        /// where it is given, then asked in its turn; and records what it came to. <see langword="null"/> where the fill ends
        /// held off: the site asked to be left alone, or said its requests are spent for longer than the fill's crawl delay,
        /// or the source's turn on this machine is spent.
        /// </summary>
        private async Task<WebFetched?> AskAsync(string address, WebFetchAsk ask, Func<string, bool>? readable)
        {
            string? heldFor = null;

            var turn = gate with
            {
                Decide = asked => readable is not null && !readable(asked)
                    ? Task.FromResult<AgentPolicyAnswer>(new AgentPolicyAnswer.Denied(RobotsRule, AgentPolicySource.Builtin, $"'{asked}' is an address the site's robots.txt or the source's include and exclude leave unread"))
                    : gate.Decide(asked),
                Pace = async stop =>
                {
                    if (await TurnAsync().ConfigureAwait(false) is { } spent)
                    {
                        heldFor = SiteSpent;
                        return spent;
                    }

                    if (await gate.Pace!(stop).ConfigureAwait(false) is { } until)
                    {
                        heldFor = TurnSpent;
                        return until;
                    }

                    // Asked now: the next request's crawl delay runs from here.
                    _asked = fill._clock.GetTimestamp();
                    return null;
                },
            };

            var fetched = await fill._fetch.FetchAsync(address, ask, turn, cancellationToken).ConfigureAwait(false);

            if (fetched.Until is { } held)
            {
                HoldOff(fetched.Address, held, heldFor ?? TurnSpent);
                return null;
            }

            Note(address, fetched.Denial is { } denial ? $"refused: {denial.Reason}" : fetched.Failure, fetched.Status, fetched.Failure is null && fetched.Denial is null);

            // A wait the site asks for without saying how long is doubled from its crawl delay each time it asks so again; any
            // other answer begins the doubling again.
            var unsaid = WebRateLimit.Unsaid(_unsaid, _delay);

            if (fetched.Status is { } status && WebRateLimit.Read(status, fetched.Headers, fill._clock.GetUtcNow(), unsaid) is { } limit)
            {
                _unsaid = limit.Said ? null : unsaid;

                if (limit.Limited)
                {
                    _state = _state with { UnsaidFor = _unsaid };
                    HoldOff(fetched.Address, limit.Until, SiteLimited);
                    return null;
                }

                _spent = limit.Until;
            }
            else if (fetched.Status is not null)
            {
                _unsaid = null;
            }

            return fetched;
        }

        /// <summary>
        /// Waits for the fill's turn by the site's own word: its crawl delay since the last request, and a pause its last
        /// answer asked for that is no longer than the delay. When it may be asked again where that pause is longer: the
        /// fill ends until then.
        /// </summary>
        private async Task<DateTimeOffset?> TurnAsync()
        {
            var now = fill._clock.GetUtcNow();
            var wait = _asked is { } last && fill._clock.GetElapsedTime(last) is var since && since < _delay ? _delay - since : TimeSpan.Zero;

            if (_spent is { } spent && spent > now)
            {
                if (spent - now > _delay)
                {
                    return spent;
                }

                wait = spent - now > wait ? spent - now : wait;
            }

            // Waited as given, a day as a day: in pieces no longer than a wait is told in, so no wait is one the clock cannot hold.
            while (wait > TimeSpan.Zero)
            {
                var piece = Waits.Shorter(Waits.SaidEvery, wait);
                await fill._wait(piece, cancellationToken).ConfigureAwait(false);
                wait -= piece;
            }

            _spent = null;
            return null;
        }

        /// <summary>Ends the fill, held off until <paramref name="until"/>, for <paramref name="why"/>.</summary>
        private void HoldOff(string address, DateTimeOffset until, string why)
        {
            _state = _state with { PausedUntil = until, PausedFor = why };
            _state.Write(fill._fileSystem, index.Directory);
            Note(address, $"{why} until {WebRateLimit.Said(until)}");
        }

        /// <summary>
        /// Ends the fill short, for <paramref name="problem"/>, held off until <paramref name="until"/> under these settings
        /// alone, where a time is given; else begun again by the next search.
        /// </summary>
        private void Stop(string problem, DateTimeOffset? until = null)
        {
            _state = _state.Stopped(problem, until, settings.All) with { UnsaidFor = _unsaid };
            _state.Write(fill._fileSystem, index.Directory);
        }

        private void Note(string address, string? why, int? status = null, bool read = false)
        {
            using var buffer = new MemoryStream();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("at", fill._clock.GetUtcNow());
                writer.WriteString("address", address);

                if (status is { } said)
                {
                    writer.WriteNumber("status", said);
                }

                writer.WriteBoolean("read", read);

                if (why is not null)
                {
                    writer.WriteString("why", why);
                }

                writer.WriteEndObject();
            }

            record.Append(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        }
    }
}
