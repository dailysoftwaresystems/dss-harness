using System.Globalization;
using System.Text;
using DssHarness.Core.Output;

namespace DssHarness.Core.Pages;

/// <summary>
/// The one owner of an answer's pages: no answer is cut, but each is pages of whole entries, with the page said, any page
/// asked by number, and the totals exact. Every page's entries are held within the room a page gives them
/// (<see cref="PageRoom"/>): an entry wider than a whole page is given in pieces at character boundaries, never inside a
/// surrogate pair, each piece marked with <see cref="TextCut.Ellipsis"/> where the entry goes on or went on before it
/// (<see cref="Pieces"/>), and each section a page ends inside one of, or inside a block, says it goes on after what the
/// page gave of it, whatever sections follow it on the page. The notes an answer ends with follow its last entry within the
/// same room, every one of them given, on as many pages as they take. The graph's answers are laid by it
/// (<see cref="Codebase.GraphPages"/>).
/// </summary>
public static class Paging
{
    /// <summary>What a page refused for being past the last is refused as (<see cref="PageAnswer{TEntry}.Refusal"/>).</summary>
    public const string PastTheLast = "past the last";

    /// <summary>What a page that ends inside an entry of no block says goes on.</summary>
    internal const string TheLine = "the line";

    /// <summary>
    /// What the page that computed a result of more than one page says where its file could not be written: the result is
    /// held in memory for the conversation's process, and a later page asked in a resumed conversation is counted again,
    /// and says so.
    /// </summary>
    /// <param name="why">Why its file could not be written.</param>
    public static string Unkept(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return $"its result could not be written ({why}): a resumed conversation counts its later pages again, and says so";
    }

    /// <summary>What a page asked below the first is refused with: pages are counted from 1.</summary>
    /// <param name="asked">The page asked.</param>
    public static string BelowTheFirst(long asked) => $"page is {asked.ToString(CultureInfo.InvariantCulture)}: pages start at 1";

    /// <summary>
    /// The header of a page of more than one: which page, the exact total, and how to ask for the next - or that this is
    /// the last.
    /// </summary>
    /// <param name="page">The page, from 1.</param>
    /// <param name="pages">How many pages the answer holds.</param>
    /// <param name="total">How many results the whole answer holds.</param>
    public static string Header(int page, int pages, long total)
        => page == pages
            ? $"page {Number(page)} of {Number(pages)} · the last"
            : $"page {Number(page)} of {Number(pages)} · {Number(total)} results · ask again with page: {Number(page + 1)} for the next";

    /// <summary>
    /// The answer's pages, filled once from its sections: each page takes of every section, in its order, whole entries up
    /// to the section's size while the page has <paramref name="room"/> - a section's caption charged with the first it
    /// gives - and an entry no room is left for begins the page after; the notes the answer ends with follow its last
    /// entries, held within the same room, on as many pages as they take. A block's head goes with its first counted
    /// entry, so the page that gives a block's lines names it. Where a page would hold nothing, it takes as much of the
    /// first entry not yet given as its room holds, marked as going on: a page always moves the answer on, and none passes
    /// its room. Every page of it is given from what this fill found, so a later page is never filled again.
    /// </summary>
    /// <typeparam name="TEntry">The entries' kind.</typeparam>
    /// <param name="sections">The answer's sections, in their order; each page holds of every section what its size leaves.</param>
    /// <param name="leading">The lines the answer begins with, said on every page.</param>
    /// <param name="trailing">The notes the answer ends with, each given whole - or in pieces where it is wider than a page - after every entry.</param>
    /// <param name="room">The characters a page gives its entries (<see cref="PageRoom.For"/>).</param>
    /// <param name="piece">
    /// The entry that carries one piece of a wider one: the entry, the piece's text, and whether the piece is the entry's
    /// first - a later piece counts nothing, so the totals count each entry once.
    /// </param>
    /// <param name="note">The entry of one line of a trailing note, which counts nothing.</param>
    public static PageLayout<TEntry> Lay<TEntry>(
        IReadOnlyList<PageSection<TEntry>> sections,
        IReadOnlyList<string> leading,
        IReadOnlyList<string> trailing,
        int room,
        Func<TEntry, string, bool, TEntry> piece,
        Func<string, TEntry> note)
        where TEntry : IPageEntry
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(leading);
        ArgumentNullException.ThrowIfNull(trailing);
        ArgumentNullException.ThrowIfNull(piece);
        ArgumentNullException.ThrowIfNull(note);
        ArgumentOutOfRangeException.ThrowIfLessThan(room, 1);

        foreach (var section in sections)
        {
            if (section?.Problem() is { } problem)
            {
                throw new ArgumentException(problem, nameof(sections));
            }
        }

        // The notes are entries of a section of their own that counts nothing, filled once every section is given: what
        // the answer ends with is held within a page's room as any entry is, every note of it given.
        var notes = new PageSection<TEntry>(
            null,
            [.. trailing.SelectMany(line => line.Split('\n')).Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).Select(note)],
            int.MaxValue);

        return new PageLayout<TEntry>(
            [.. leading],
            Fills(sections, notes, room, piece),
            notes,
            sections.Sum(section => (long)section.Entries.Count(entry => entry.Counts > 0)));
    }

    /// <summary>A page number, or a count, said as a person reads it.</summary>
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a header or a refusal says after its counts or its range: the note, where there is one.</summary>
    internal static string Said(string? note) => note is null ? string.Empty : $" · {note}";

    /// <summary>The first line of a page: its header and its note, where it holds more than one page or says a note; null where it has none.</summary>
    internal static string? HeaderLine(int page, int pages, long total, string? note)
        => pages > 1 || note is not null ? Header(page, pages, total) + Said(note) : null;

    /// <summary>What a page past the last says: the page asked, of what, and the answer's range, with the note after it.</summary>
    internal static string PastTheLastSaid(int page, int pages, string asked, string? note)
        => $"page {Number(page)} of '{asked}': it has {Number(pages)} page{(pages == 1 ? string.Empty : "s")} (1 to {Number(pages)}){Said(note)}";

    /// <summary>What a page that ends inside an entry, or a block, says goes on.</summary>
    internal static string GoesOnLine(string what) => $"{TextCut.Ellipsis} {what} goes on";

    /// <summary>Whether <paramref name="other"/> is of the same block as <paramref name="entry"/>: the same block, said by the same name.</summary>
    internal static bool SameBlock<TEntry>(TEntry entry, TEntry other)
        where TEntry : IPageEntry
        => entry.BlockName is not null
            && string.Equals(entry.BlockName, other.BlockName, StringComparison.Ordinal)
            && string.Equals(entry.Block, other.Block, StringComparison.Ordinal);

    /// <summary>One section's fill: where its cursor stands after the pages made so far.</summary>
    private sealed class SectionFill<TEntry>(PageSection<TEntry> section)
        where TEntry : IPageEntry
    {
        /// <summary>The section being filled.</summary>
        public PageSection<TEntry> Section { get; } = section;

        /// <summary>How many of its entries the pages so far have given whole.</summary>
        public int Cursor { get; set; }

        /// <summary>How many characters of the entry at <see cref="Cursor"/> the pages so far have given: none where they gave none of it.</summary>
        public int Offset { get; set; }

        /// <summary>Whether every entry of the section is given.</summary>
        public bool Done => Cursor >= Section.Entries.Count;
    }

    /// <summary>
    /// The entries of every page, filled once from the sections: each page takes of every section, in its order, whole
    /// entries up to the section's size while the page has room - a section's caption charged with the first it gives -
    /// and an entry no room is left for begins the page after; <paramref name="notes"/> are taken once every section is
    /// given, on the page its last entry is and the pages after. A block's head goes with its first counted entry, so the
    /// page that gives a block's lines names it. Where a page would hold nothing, it takes as much of the first entry not
    /// yet given as its room holds, marked as going on: a page always moves the answer on, and none passes its room.
    /// </summary>
    private static List<List<SectionOfPage<TEntry>>> Fills<TEntry>(IReadOnlyList<PageSection<TEntry>> sections, PageSection<TEntry> notes, int room, Func<TEntry, string, bool, TEntry> piece)
        where TEntry : IPageEntry
    {
        var fills = new List<List<SectionOfPage<TEntry>>>();
        var state = sections.Select(section => new SectionFill<TEntry>(section)).Append(new SectionFill<TEntry>(notes)).ToList();

        while (state.Any(fill => !fill.Done))
        {
            var page = new List<SectionOfPage<TEntry>>();
            var left = room;
            var given = 0;

            // The head of the block the page begins with, while the page holds nothing else: an entry of that block too
            // wide for what is left is given in pieces here, beside its head, rather than on a page of its own.
            TEntry? heading = default;
            var headed = false;

            foreach (var fill in state)
            {
                // The notes wait for every section's last entry.
                if (ReferenceEquals(fill.Section, notes) && state.Any(other => !other.Done && !ReferenceEquals(other.Section, notes)))
                {
                    continue;
                }

                var shown = new List<TEntry>();
                var budget = fill.Section.Size;
                var caption = fill.Section.Caption is { } said ? said.Length + 1 : 0;
                var cutInside = false;

                while (!fill.Done)
                {
                    var entry = fill.Section.Entries[fill.Cursor];
                    var whole = fill.Offset == 0;
                    var rest = Pieces.Rest(entry.Text, fill.Offset);
                    var cost = rest.Length + 1 + (shown.Count == 0 ? caption : 0);

                    if (whole)
                    {
                        if (entry.Counts > budget && shown.Count > 0)
                        {
                            break;
                        }

                        // A block's head rides only with the block's first counted entry: a page with no room or no size left
                        // for that entry ends before the head, and the next page begins with both.
                        if (Opens(entry, shown) && HeadWithItsLine(fill, caption, shown.Count) is var (headCost, headCounts)
                            && ((headCounts > budget && shown.Count > 0) || (headCost > left && given > 0)))
                        {
                            break;
                        }
                    }

                    if (cost <= left)
                    {
                        var heads = (given == 0 || (headed && SameBlock(heading!, entry))) && whole && entry.Counts == 0 && entry.BlockName is not null;
                        heading = heads ? (headed ? heading : entry) : default;
                        headed = heads;
                        shown.Add(Piece(entry, rest, whole, last: true, piece));
                        left -= cost;
                        budget -= whole ? entry.Counts : 0;
                        fill.Cursor++;
                        fill.Offset = 0;
                        given++;
                        continue;
                    }

                    if (given > 0 && !(headed && SameBlock(heading!, entry)))
                    {
                        break;
                    }

                    // The page holds nothing yet and the entry alone passes its room: as much of it as the page holds, the
                    // rest on the pages after - by the one rule a piece is cut by.
                    var head = Pieces.Head(rest, continued: !whole, left - (shown.Count == 0 ? caption : 0));

                    // A piece that would hold the whole rest is no piece: it is given whole, past the room only where the
                    // room left holds less than one character of it.
                    if (head.Length == rest.Length)
                    {
                        shown.Add(Piece(entry, rest, whole, last: true, piece));
                        budget -= whole ? entry.Counts : 0;
                        fill.Cursor++;
                        fill.Offset = 0;
                        left = 0;
                        given++;
                        break;
                    }

                    shown.Add(Piece(entry, Pieces.Marked(head), whole, last: false, piece));
                    budget -= whole ? entry.Counts : 0;
                    fill.Offset += Pieces.Taken(head, continued: !whole);
                    left = 0;
                    given++;
                    cutInside = true;
                    break;
                }

                if (shown.Count > 0)
                {
                    page.Add(new SectionOfPage<TEntry>(fill.Section, shown, GoesOnAt(fill, shown[^1], cutInside)));
                }
            }

            fills.Add(page);
        }

        return fills;
    }

    /// <summary>What a page whose last entry of a section is <paramref name="last"/> says goes on: the entry it ends inside, or the block the next entry goes on with; null where neither.</summary>
    private static string? GoesOnAt<TEntry>(SectionFill<TEntry> fill, TEntry last, bool cutInside)
        where TEntry : IPageEntry
    {
        if (cutInside)
        {
            return last.BlockName ?? TheLine;
        }

        return !fill.Done && SameBlock(last, fill.Section.Entries[fill.Cursor]) ? last.BlockName : null;
    }

    /// <summary>Whether <paramref name="entry"/> opens a block here: an entry that counts nothing, of a block, after none of that block's.</summary>
    private static bool Opens<TEntry>(TEntry entry, IReadOnlyList<TEntry> shown)
        where TEntry : IPageEntry
        => entry.Counts == 0 && entry.BlockName is not null && (shown.Count == 0 || !SameBlock(shown[^1], entry));

    /// <summary>
    /// What a block's head costs with the entries that ride with it through the block's first counted entry - its
    /// characters, a section's caption among them where the page shows nothing of it yet, and what it counts.
    /// </summary>
    private static (int Cost, int Counts) HeadWithItsLine<TEntry>(SectionFill<TEntry> fill, int caption, int shown)
        where TEntry : IPageEntry
    {
        var entries = fill.Section.Entries;
        var head = entries[fill.Cursor];
        var cost = shown == 0 ? caption : 0;

        for (var at = fill.Cursor; at < entries.Count && (at == fill.Cursor || SameBlock(head, entries[at])); at++)
        {
            cost += entries[at].Text.Length + 1;

            if (entries[at].Counts > 0)
            {
                return (cost, entries[at].Counts);
            }
        }

        return (cost, 0);
    }

    /// <summary>
    /// The piece of <paramref name="entry"/> a page gives: the entry itself where it is given whole, else what
    /// <paramref name="piece"/> makes of the piece's text - counting the entry on its first piece alone.
    /// </summary>
    private static TEntry Piece<TEntry>(TEntry entry, string text, bool first, bool last, Func<TEntry, string, bool, TEntry> piece)
        where TEntry : IPageEntry
        => first && last ? entry : piece(entry, text, first);
}

/// <summary>What one page held of one section.</summary>
/// <typeparam name="TEntry">The entries' kind.</typeparam>
/// <param name="Section">The section.</param>
/// <param name="Shown">The entries - or the piece of one - the page gave of it.</param>
/// <param name="GoesOn">What the page says goes on where it ends here, inside an entry or a block; null where it ends between them.</param>
internal sealed record SectionOfPage<TEntry>(PageSection<TEntry> Section, IReadOnlyList<TEntry> Shown, string? GoesOn)
    where TEntry : IPageEntry;

/// <summary>An answer's pages as one fill found them (<see cref="Paging.Lay"/>): any of them given from it, none filled again.</summary>
/// <typeparam name="TEntry">The entries' kind.</typeparam>
public sealed class PageLayout<TEntry>
    where TEntry : IPageEntry
{
    private readonly IReadOnlyList<string> _leading;
    private readonly List<List<SectionOfPage<TEntry>>> _fills;
    private readonly PageSection<TEntry> _notes;

    internal PageLayout(IReadOnlyList<string> leading, List<List<SectionOfPage<TEntry>>> fills, PageSection<TEntry> notes, long total)
    {
        _leading = leading;
        _fills = fills;
        _notes = notes;
        Total = total;
    }

    /// <summary>How many pages the answer holds; one where all of it is given at once.</summary>
    public int Pages => Math.Max(1, _fills.Count);

    /// <summary>How many results the whole answer holds, counted once.</summary>
    public long Total { get; }

    /// <summary>
    /// The answer's <paramref name="page"/>: the leading lines, then what the page holds of each section, its caption
    /// first; a page of one holds the whole answer and no header but where a note is said; a page past the last is refused
    /// with the answer's range.
    /// </summary>
    /// <param name="page">The page asked, from 1.</param>
    /// <param name="asked">How the answer names what was asked, for a page past the last to say.</param>
    /// <param name="note">What the header says after its counts, and a refusal after its range (<see cref="PageAnswer{TEntry}.Note"/>).</param>
    public PageAnswer<TEntry> Page(int page, string asked, string? note = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(asked);

        var pages = Pages;
        var total = Total;

        if (page > pages)
        {
            return new PageAnswer<TEntry>(Paging.PastTheLastSaid(page, pages, asked, note), page, pages, total, [], note)
            {
                Refusal = Paging.PastTheLast,
            };
        }

        var held = _fills.Count == 0 ? [] : _fills[page - 1];
        var builder = new StringBuilder();

        if (Paging.HeaderLine(page, pages, total, note) is { } header)
        {
            builder.Append(header).Append('\n');
        }

        foreach (var line in _leading)
        {
            builder.Append(line).Append('\n');
        }

        foreach (var section in held)
        {
            if (section.Section.Caption is { } caption)
            {
                builder.Append(caption).Append('\n');
            }

            foreach (var entry in section.Shown)
            {
                builder.Append(entry.Text).Append('\n');
            }

            // A section whose page ends inside an entry, or inside a block, says it goes on, right after what the page gave
            // of it - whatever later sections the page holds too: what the next page holds of it is its own.
            if (section.GoesOn is { } goesOn)
            {
                builder.Append(Paging.GoesOnLine(goesOn)).Append('\n');
            }
        }

        return new PageAnswer<TEntry>(
            builder.ToString().TrimEnd('\n'),
            page,
            pages,
            total,
            [.. held.Where(section => !ReferenceEquals(section.Section, _notes)).Select(section => new PageOfSection<TEntry>(section.Section.Caption, section.Shown))],
            note);
    }
}
