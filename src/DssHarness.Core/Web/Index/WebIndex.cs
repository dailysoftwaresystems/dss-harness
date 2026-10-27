using System.Globalization;
using System.Text;
using System.Text.Json;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;

namespace DssHarness.Core.Web.Index;

/// <summary>
/// The harness's own full-text index of one site's pages, kept in its directory and scored by BM25: no search library, and
/// no service. Its pages are lines of <c>pages.jsonl</c>, which only grows - each page read is a line, a later line of an
/// address replacing an earlier, a page the site no longer lists a line that says it is gone, and a page that could not
/// be read a line that says when, so the next fill asks it last - and is written anew under a new generation once the
/// lines replaced outnumber those kept; a fill appends while any number of searches read beside it, each reading only
/// what was added since it last looked.
/// </summary>
/// <remarks>
/// <para>
/// A page's title counts three times, each heading twice and its text once: what a page is about is said first in its
/// title and headings. Terms are runs of letters and digits, compared in lower case, unstemmed. Of a page's text the first
/// <see cref="MostCharacters"/> characters are kept, of its title and each of its first <see cref="MostHeadings"/>
/// headings the first <see cref="MostField"/>: enough to say what it holds, and a bound on what a site of thousands of
/// pages costs the disk.
/// </para>
/// <para>
/// Held in memory are each page's address, its title, its weighted length, where its line begins and the terms it holds
/// - each term's text once, however many pages hold it - and each term's pages with how often it is on each; a result's
/// snippet is read back from its line. A search holds the index's lock while it scores, and a reading of new lines takes
/// it too.
/// </para>
/// </remarks>
public sealed class WebIndex
{
    /// <summary>The file of the index's pages, in its directory.</summary>
    public const string PagesFileName = "pages.jsonl";

    /// <summary>The most characters of a page's text kept.</summary>
    public const int MostCharacters = 32_000;

    /// <summary>The most characters of a page's title, and of each heading, kept.</summary>
    public const int MostField = 300;

    /// <summary>The most headings of a page kept, its first.</summary>
    public const int MostHeadings = 100;

    /// <summary>BM25's saturation of a term's frequency.</summary>
    private const double K1 = 1.2;

    /// <summary>BM25's weight of a page's length.</summary>
    private const double B = 0.75;

    /// <summary>How many characters of a page's text a snippet begins before the first term it holds.</summary>
    private const int Lead = 60;

    private readonly IFileSystem _fileSystem;
    private readonly string _site;
    private readonly Lock _gate = new();
    private readonly List<Page> _pages = [];
    private readonly Dictionary<string, int> _byAddress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Term> _terms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset At, string Why, long Start)> _failed = new(StringComparer.Ordinal);
    private long _offset;
    private int _generation = -1;
    private double _length;
    private int _held;
    private int _lines;

    /// <summary>The index of <paramref name="site"/> kept in <paramref name="directory"/>.</summary>
    /// <param name="fileSystem">Where it is kept.</param>
    /// <param name="directory">Its directory.</param>
    /// <param name="site">The site it is of.</param>
    public WebIndex(IFileSystem fileSystem, string directory, string site)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(site);

        _fileSystem = fileSystem;
        _site = site;
        Directory = directory;
    }

    /// <summary>Its directory.</summary>
    public string Directory { get; }

    /// <summary>The site it is of.</summary>
    public string Site => _site;

    /// <summary>Its pages' file.</summary>
    public string PagesFile => Path.Combine(Directory, PagesFileName);

    /// <summary>How many pages it holds, as of its last <see cref="Refresh"/>.</summary>
    public int Held
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }
    }

    /// <summary>How many pages it holds that <paramref name="keep"/> keeps, as of its last <see cref="Refresh"/>: those a search of it that keeps them may find.</summary>
    /// <param name="keep">Whether a page's address is kept.</param>
    public int HeldOf(Func<string, bool> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);

        lock (_gate)
        {
            return _byAddress.Keys.Count(keep);
        }
    }

    /// <summary>How many pages it remembers could not be read, as of its last <see cref="Refresh"/>.</summary>
    public int Failed
    {
        get
        {
            lock (_gate)
            {
                return _failed.Count;
            }
        }
    }

    /// <summary>How many lines of its pages' file it has read, as of its last <see cref="Refresh"/>: each page held or that could not be read, and each replaced or gone.</summary>
    public int Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines;
            }
        }
    }

    /// <summary>The line a page read is kept as.</summary>
    /// <param name="address">Its address.</param>
    /// <param name="lastModified">Its <c>lastmod</c>, as its sitemap wrote it; none where it wrote none.</param>
    /// <param name="readAt">When it was read.</param>
    /// <param name="parts">What was read of it.</param>
    public static string PageLine(string address, string? lastModified, DateTimeOffset readAt, WebPageParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        return Line(writer =>
        {
            writer.WriteString("address", address);

            if (lastModified is not null)
            {
                writer.WriteString("lastmod", lastModified);
            }

            writer.WriteString("readAt", readAt);
            writer.WriteString("title", TextCut.Head(parts.Title, MostField));
            writer.WriteStartArray("headings");

            foreach (var heading in parts.Headings.Take(MostHeadings))
            {
                writer.WriteStringValue(TextCut.Head(heading, MostField));
            }

            writer.WriteEndArray();
            writer.WriteString("text", TextCut.Head(parts.Text, MostCharacters));
        });
    }

    /// <summary>The line that says the page at <paramref name="address"/> is gone from the index, and forgets that it could not be read.</summary>
    /// <param name="address">Its address.</param>
    public static string GoneLine(string address) => Line(writer =>
    {
        writer.WriteString("address", address);
        writer.WriteBoolean("gone", true);
    });

    /// <summary>
    /// The line that says the page at <paramref name="address"/> could not be read at <paramref name="at"/>, for
    /// <paramref name="why"/>: a page held stays as it was, and the next fill asks it last.
    /// </summary>
    /// <param name="address">Its address.</param>
    /// <param name="at">When it was asked.</param>
    /// <param name="why">Why it could not be read.</param>
    public static string FailedLine(string address, DateTimeOffset at, string why) => Line(writer =>
    {
        writer.WriteString("address", address);
        writer.WriteString("failedAt", at);
        writer.WriteString("why", TextCut.Head(why, MostField));
    });

    /// <summary>
    /// Reads what <c>pages.jsonl</c> added since this index last looked - all of it again where its generation moved, the
    /// file written anew - a torn last line left for the next look.
    /// </summary>
    public void Refresh()
    {
        lock (_gate)
        {
            var generation = WebIndexState.Read(_fileSystem, Directory, _site).Generation;

            if (generation != _generation)
            {
                Clear();
                _generation = generation;
            }

            if (!_fileSystem.FileExists(PagesFile))
            {
                return;
            }

            using var stream = _fileSystem.OpenRead(PagesFile);

            if (stream.Length < _offset)
            {
                // Written anew with no generation to say so: read whole again.
                Clear();
                _generation = generation;
            }

            _offset = EachLine(stream, _offset, Take);
        }
    }

    /// <summary>
    /// Hands <paramref name="take"/> each whole line of <paramref name="stream"/> from <paramref name="from"/>, with where it
    /// begins; a last line with no end yet is left. Where the next line would begin.
    /// </summary>
    private static long EachLine(Stream stream, long from, Action<ReadOnlyMemory<byte>, long> take)
    {
        stream.Seek(from, SeekOrigin.Begin);

        var buffer = new byte[1 << 16];
        using var line = new MemoryStream();
        var start = from;

        while (stream.Read(buffer) is var read and > 0)
        {
            var begun = 0;

            for (var at = 0; at < read; at++)
            {
                if (buffer[at] != (byte)'\n')
                {
                    continue;
                }

                line.Write(buffer, begun, at - begun);
                take(line.GetBuffer().AsMemory(0, (int)line.Length), start);
                start += line.Length + 1;
                line.SetLength(0);
                begun = at + 1;
            }

            line.Write(buffer, begun, read - begun);
        }

        return start;
    }

    /// <summary>Every page it remembers could not be read, by address: when it was last asked, and why it could not be read.</summary>
    public IReadOnlyDictionary<string, (DateTimeOffset At, string Why)> Failures()
    {
        lock (_gate)
        {
            return _failed.ToDictionary(entry => entry.Key, entry => (entry.Value.At, entry.Value.Why), StringComparer.Ordinal);
        }
    }

    /// <summary>Every page it holds, by address: its <c>lastmod</c> and when it was read.</summary>
    public IReadOnlyDictionary<string, (string? LastModified, DateTimeOffset ReadAt)> Pages()
    {
        lock (_gate)
        {
            return _byAddress.ToDictionary(entry => entry.Key, entry => (_pages[entry.Value].LastModified, _pages[entry.Value].ReadAt), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The pages that best answer <paramref name="query"/>, best first, at most <paramref name="limit"/>: BM25 over its
    /// terms, each hit with its title and a snippet of its text around the first term it holds - of the pages
    /// <paramref name="keep"/> keeps, where it is given.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="limit">The most hits.</param>
    /// <param name="keep">Whether a page, by its address, may be a hit; every page held where none is given.</param>
    public IReadOnlyList<WebIndexHit> Search(string query, int limit, Func<string, bool>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var asked = WebIndexTerms.Of(query).Distinct(StringComparer.Ordinal).ToList();
        List<(Page Page, double Score)> best;

        lock (_gate)
        {
            if (_held == 0 || asked.Count == 0)
            {
                return [];
            }

            var average = _length / _held;
            var scores = new Dictionary<int, double>();
            var kept = new Dictionary<int, bool>();

            foreach (var word in asked)
            {
                if (!_terms.TryGetValue(word, out var term) || term.Pages == 0)
                {
                    continue;
                }

                var idf = Math.Log(1 + ((_held - term.Pages + 0.5) / (term.Pages + 0.5)));

                foreach (var (id, frequency) in term.Postings)
                {
                    var page = _pages[id];

                    if (!page.Held || (keep is not null && !Keeps(id, page)))
                    {
                        continue;
                    }

                    scores[id] = scores.GetValueOrDefault(id) + (idf * frequency * (K1 + 1) / (frequency + (K1 * (1 - B + (B * page.Length / average)))));
                }
            }

            best = [.. scores.OrderByDescending(score => score.Value).ThenBy(score => score.Key).Take(limit).Select(score => (_pages[score.Key], score.Value))];

            bool Keeps(int id, Page page)
            {
                if (!kept.TryGetValue(id, out var keeps))
                {
                    keeps = keep(page.Address);
                    kept[id] = keeps;
                }

                return keeps;
            }
        }

        return [.. best.Select(hit => Hit(hit.Page, hit.Score, asked))];
    }

    private static string Line(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Writes its pages' file anew with each held page's last line alone, and each failure it remembers, and raises its
    /// generation so every reader reads it again from its start: what the fill does once lines replaced and gone outnumber
    /// those kept. The fill's alone to do, holding the fill and no writer of the file open.
    /// </summary>
    /// <returns>The index's state, its generation raised.</returns>
    public WebIndexState Compact()
    {
        lock (_gate)
        {
            Refresh();

            // Copied line by line, never held whole: a page's last line is the one its start names, and a failure's its own.
            var held = _pages.Where(page => page.Held).Select(page => page.Start).Concat(_failed.Values.Select(failure => failure.Start)).ToHashSet();

            _fileSystem.WriteAtomic(PagesFile, written =>
            {
                using var read = _fileSystem.OpenRead(PagesFile);

                EachLine(read, 0, (line, start) =>
                {
                    if (held.Contains(start))
                    {
                        written.Write(line.Span);
                        written.WriteByte((byte)'\n');
                    }
                });
            });

            var state = WebIndexState.Read(_fileSystem, Directory, _site);
            var compacted = state with { Generation = state.Generation + 1 };
            compacted.Write(_fileSystem, Directory);

            Refresh();

            return compacted;
        }
    }

    private void Clear()
    {
        _pages.Clear();
        _byAddress.Clear();
        _terms.Clear();
        _failed.Clear();
        _offset = 0;
        _length = 0;
        _held = 0;
        _lines = 0;
    }

    /// <summary>Takes one line of <c>pages.jsonl</c>, which begins at <paramref name="start"/>: a page, a page gone, or a page that could not be read.</summary>
    private void Take(ReadOnlyMemory<byte> bytes, long start)
    {
        _lines++;

        string address;
        string? lastModified;
        DateTimeOffset readAt;
        string title;
        List<string> headings;
        string text;

        try
        {
            using var line = JsonDocument.Parse(bytes);
            var root = line.RootElement;

            address = root.GetProperty("address").GetString()!;

            // A page of another site - the source's site changed under its index - is none of this one's.
            if (!address.StartsWith(_site + "/", StringComparison.Ordinal))
            {
                return;
            }

            if (root.TryGetProperty("gone", out var gone) && gone.ValueKind == JsonValueKind.True)
            {
                Drop(address);
                _failed.Remove(address);
                return;
            }

            if (root.TryGetProperty("failedAt", out var failedAt))
            {
                _failed[address] = (failedAt.GetDateTimeOffset(), root.TryGetProperty("why", out var why) ? why.GetString() ?? string.Empty : string.Empty, start);
                return;
            }

            lastModified = root.TryGetProperty("lastmod", out var lastmod) ? lastmod.GetString() : null;
            readAt = root.GetProperty("readAt").GetDateTimeOffset();
            title = root.GetProperty("title").GetString() ?? string.Empty;
            headings = [.. root.GetProperty("headings").EnumerateArray().Select(heading => heading.GetString() ?? string.Empty)];
            text = root.GetProperty("text").GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A line this index never wrote is no page; the index is derived, and the next fill writes what it lacks.
            return;
        }

        Drop(address);
        _failed.Remove(address);

        var weighted = new Dictionary<string, int>(StringComparer.Ordinal);
        var length = Count(WebIndexTerms.Of(title), 3, weighted) + Count(headings.SelectMany(WebIndexTerms.Of), 2, weighted) + Count(WebIndexTerms.Of(text), 1, weighted);
        var id = _pages.Count;

        var terms = new Term[weighted.Count];
        var next = 0;

        foreach (var (word, frequency) in weighted)
        {
            if (!_terms.TryGetValue(word, out var term))
            {
                term = new Term();
                _terms[word] = term;
            }

            term.Postings.Add((id, frequency));
            term.Pages++;
            terms[next++] = term;
        }

        _pages.Add(new Page(address, lastModified, readAt, Shown(title, headings, address), length, start, terms) { Held = true });
        _byAddress[address] = id;
        _length += length;
        _held++;

        static int Count(IEnumerable<string> words, int weight, Dictionary<string, int> weighted)
        {
            var count = 0;

            foreach (var word in words)
            {
                weighted[word] = weighted.GetValueOrDefault(word) + weight;
                count += weight;
            }

            return count;
        }
    }

    /// <summary>Takes the page at <paramref name="address"/> out of what is scored, where it is held.</summary>
    private void Drop(string address)
    {
        if (!_byAddress.Remove(address, out var id))
        {
            return;
        }

        var page = _pages[id];
        page.Held = false;
        _length -= page.Length;
        _held--;

        foreach (var term in page.Terms)
        {
            term.Pages--;
        }
    }

    /// <summary>A page's title as a hit shows it: its title, else its first heading, else its address.</summary>
    private static string Shown(string title, List<string> headings, string address)
        => title.Length > 0 ? title : headings.FirstOrDefault(heading => heading.Length > 0) ?? address;

    /// <summary>A hit: the page, its score, and a snippet of its text read back from its line.</summary>
    private WebIndexHit Hit(Page page, double score, IReadOnlyList<string> asked)
    {
        var text = TextAt(page);

        return new WebIndexHit(page.Address, page.Title, text is null ? string.Empty : Snippet(text, asked), score);
    }

    /// <summary>The text of <paramref name="page"/>'s line; <see langword="null"/> where the line is no longer its own, the file written anew since.</summary>
    private string? TextAt(Page page)
    {
        if (Raw(page) is not { } line)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.GetProperty("text").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The bytes of <paramref name="page"/>'s line; <see langword="null"/> where the line is no longer its own, the file written anew since.</summary>
    private byte[]? Raw(Page page)
    {
        try
        {
            using var stream = new BufferedStream(_fileSystem.OpenRead(PagesFile));
            stream.Seek(page.Start, SeekOrigin.Begin);

            using var line = new MemoryStream();

            while (stream.ReadByte() is var octet and >= 0 and not '\n')
            {
                line.WriteByte((byte)octet);
            }

            var bytes = line.ToArray();
            using var document = JsonDocument.Parse(bytes);

            return document.RootElement.TryGetProperty("address", out var address) && address.GetString() == page.Address ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A snippet of <paramref name="text"/>: from a little before the first term of <paramref name="asked"/> it holds, else from its start.</summary>
    internal static string Snippet(string text, IReadOnlyList<string> asked)
    {
        var first = WebIndexTerms.Positions(text).FirstOrDefault(found => asked.Contains(found.Term, StringComparer.Ordinal));
        var start = first.Term is null ? 0 : Math.Max(0, first.Start - Lead);

        // Begun at a blank, so no word is cut at its front.
        if (start > 0 && text.IndexOf(' ', start, Math.Min(Lead, text.Length - start)) is var blank and >= 0)
        {
            start = blank + 1;
        }

        var snippet = TextCut.AtWord(string.Join(" ", text[start..].Split('\n', StringSplitOptions.RemoveEmptyEntries)), MostField);

        return start > 0 ? $"…{snippet}" : snippet;
    }

    /// <summary>A page held, or once held: the terms it holds by reference, each term's text kept once by the index.</summary>
    private sealed record Page(string Address, string? LastModified, DateTimeOffset ReadAt, string Title, int Length, long Start, Term[] Terms)
    {
        /// <summary>Whether it is held: false once a later line replaced it, or said it is gone.</summary>
        public bool Held { get; set; }
    }

    /// <summary>One term: how many held pages hold it, and each page that held it with how often, weighted.</summary>
    private sealed class Term
    {
        public int Pages { get; set; }

        public List<(int Page, int Frequency)> Postings { get; } = [];
    }
}

/// <summary>The terms of a text as the index reads them: runs of letters and digits, in lower case.</summary>
internal static class WebIndexTerms
{
    /// <summary>The longest term kept: a longer run is no word anyone searches for.</summary>
    private const int Longest = 64;

    /// <summary>The terms of <paramref name="text"/>, in its order.</summary>
    public static IEnumerable<string> Of(string text) => Positions(text).Select(found => found.Term);

    /// <summary>The terms of <paramref name="text"/>, each with where it begins.</summary>
    public static IEnumerable<(string Term, int Start)> Positions(string text)
    {
        var builder = new StringBuilder();
        var start = 0;
        var at = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (builder.Length == 0)
                {
                    start = at;
                }

                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }
            else if (builder.Length > 0)
            {
                if (builder.Length <= Longest)
                {
                    yield return (builder.ToString(), start);
                }

                builder.Clear();
            }

            at += rune.Utf16SequenceLength;
        }

        if (builder.Length is > 0 and <= Longest)
        {
            yield return (builder.ToString(), start);
        }
    }
}

/// <summary>One page an index search found.</summary>
/// <param name="Address">Its address.</param>
/// <param name="Title">Its title, else its first heading, else its address.</param>
/// <param name="Snippet">A snippet of its text around what was asked.</param>
/// <param name="Score">Its BM25 score.</param>
public sealed record WebIndexHit(string Address, string Title, string Snippet, double Score);

/// <summary>
/// What a search of a site's index answered: its hits, and how much of the site it held - how many pages, of how many its
/// sitemaps list - whether it is being filled, and why it holds less than it might.
/// </summary>
/// <param name="Site">The site.</param>
/// <param name="Hits">The pages found, best first.</param>
/// <param name="Held">How many pages it holds that its source's settings read: those a search may find.</param>
/// <param name="Known">How many pages its sitemaps list that it may read; <see langword="null"/> until they were read.</param>
/// <param name="Filling">Whether it is being filled now, here or by another run.</param>
/// <param name="Problem">Why a fill stopped short, where none has ended whole since; <see langword="null"/> otherwise.</param>
/// <param name="PausedUntil">When the site may be asked again, where a fill is held off; <see langword="null"/> otherwise.</param>
public sealed record WebIndexAnswer(string Site, IReadOnlyList<WebIndexHit> Hits, int Held, int? Known, bool Filling, string? Problem, DateTimeOffset? PausedUntil)
{
    /// <summary>Why a fill is held off until <see cref="PausedUntil"/>, where it was not for a problem (<see cref="WebIndexState.PausedFor"/>).</summary>
    public string? PausedFor { get; init; }

    /// <summary>How much of the site it held, as an agent is told: <c>indexed 240 of 1,904 pages of digital.gov; the rest are being read</c>.</summary>
    public string Said
    {
        get
        {
            var host = Uri.TryCreate(Site, UriKind.Absolute, out var site) ? site.Host : Site;
            var held = Held.ToString("N0", CultureInfo.InvariantCulture);

            // Of how many it knows, where it holds no more than that: a listing read short knows fewer than it holds.
            var said = Known is { } known && Held <= known
                ? $"indexed {held} of {known.ToString("N0", CultureInfo.InvariantCulture)} pages of {host}"
                : $"indexed {held} pages of {host}";

            said = Filling
                ? Known is null ? $"{said}; its sitemaps are being read" : Held < Known ? $"{said}; the rest are being read" : $"{said}; they are being read again"
                : PausedFor is { } why && PausedUntil is { } until ? $"{said}; reading it is held off: {why} until {WebRateLimit.Said(until)}"
                : said;

            return Problem is { } problem ? $"{said}; no fill has ended whole since one stopped short: {problem}" : said;
        }
    }
}
