using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase;

/// <summary>
/// One entry of an answer the pages hold: a line of it, and how much of its section's page it counts. An entry is never
/// cut: one a page has room for is given whole, the next page holding what follows it; one wider than a whole page is
/// given in pieces, a page each, each marked as going on (<see cref="GraphPages"/>).
/// </summary>
/// <param name="Text">The entry's text, one line of the answer.</param>
/// <param name="Lines">
/// How much of its section's size the entry counts: one for a result, a node, a hit or a served line; none for a line
/// that only says something - a block's head, a gap a body left out, a note - which rides on whatever page holds it.
/// </param>
/// <param name="GoesOn">
/// Where the entry is one of a node's served lines, or the head of its block, that node's qualified name: what a page
/// that ends inside the block says goes on. The block itself is told by <paramref name="Node"/> as well, so two nodes of
/// one qualified name - two overloads - are two blocks, and a page that ends where another node begins says nothing.
/// </param>
/// <param name="Node">The node whose lines this entry gave, or whose block it heads, by its id; what a page's served nodes are read from.</param>
/// <param name="Hit">Whether the entry is a line a text search added where the graph was not sure.</param>
/// <param name="File">
/// The file, relative to the tree, whose lines the entry served, where it served any: what a page counts as viewed - the
/// files whose lines that page gave, and no other.
/// </param>
public sealed record GraphEntry(string Text, int Lines = 1, string? GoesOn = null, string? Node = null, bool Hit = false, string? File = null)
{
    /// <summary>What is wrong with the entry; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => string.IsNullOrEmpty(Text) || Text.Contains('\n') || Text.Contains('\r') ? "an entry is one line, and never empty"
            : Lines < 0 ? "an entry counts no fewer than no lines"
            : File is { Length: 0 } ? "an entry that served a file's lines names the file"
            : null;

    /// <summary>Whether <paramref name="other"/> is of the same node's block as this entry: the same node, said by the same name.</summary>
    /// <param name="other">The entry beside it.</param>
    public bool SameBlock(GraphEntry other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return GoesOn is not null
            && string.Equals(GoesOn, other.GoesOn, StringComparison.Ordinal)
            && string.Equals(Node, other.Node, StringComparison.Ordinal);
    }
}

/// <summary>
/// One section of an answer: a list of entries the pages hold whole, up to <see cref="Size"/> of them a page, its
/// caption said on every page that holds entries of it - so a page read alone still names what its entries are of.
/// </summary>
/// <param name="Caption">The line above the section's entries, where one is given.</param>
/// <param name="Entries">The section's entries, in their stable order.</param>
/// <param name="Size">How much of the section a page holds, in its entries' <see cref="GraphEntry.Lines"/>.</param>
public sealed record GraphSection([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Caption, IReadOnlyList<GraphEntry> Entries, int Size)
{
    /// <summary>What is wrong with the section; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => Size < 1 ? "a section's size is at least one"
            : Caption is { } caption && (caption.Length == 0 || caption.Contains('\n') || caption.Contains('\r')) ? "a section's caption is one line, and never empty"
            : Entries is null || Entries.Any(entry => entry is null) ? "a section holds a list of entries, none of them null"
            : Entries.Select(entry => entry.Problem()).FirstOrDefault(problem => problem is not null);
}

/// <summary>What one page gave of one section: its caption, and the entries - or the pieces of one - the page holds of it.</summary>
/// <param name="Caption">The section's caption, where it has one.</param>
/// <param name="Entries">What the page gave of the section, in order.</param>
public sealed record GraphPageSection(string? Caption, IReadOnlyList<GraphEntry> Entries);

/// <summary>An answer asked as its pages give it.</summary>
/// <param name="Text">The answer for the page asked, as the model reads it.</param>
/// <param name="Page">The page given.</param>
/// <param name="Pages">How many pages the answer holds; one where all of it was given at once.</param>
/// <param name="Total">How many results the whole answer holds, counted once, never estimated.</param>
/// <param name="Sections">What the page gave of each section, in the answer's order: none for a page refused.</param>
/// <param name="Note">
/// What the page's header says after its counts, and a refused page after its range: why the page was made again rather
/// than read from what page 1 kept, or that its result's file could not be written; null where there is nothing to say.
/// </param>
public sealed record GraphPageAnswer(string Text, int Page, int Pages, int Total, IReadOnlyList<GraphPageSection> Sections, string? Note)
{
    /// <summary>Why the page asked is none of the answer, where it is not; <see langword="null"/> where it is.</summary>
    public string? Refusal { get; init; }

    /// <summary>The entries - and the pieces of entries - the page gave, every section's in order.</summary>
    public IReadOnlyList<GraphEntry> Entries => [.. Sections.SelectMany(section => section.Entries)];
}

/// <summary>
/// The one owner of an answer's pages: no answer of the graph is cut, but each is pages of whole entries, with the page
/// said, any page asked by number, and the totals exact. Every page's entries are held within the characters an answer
/// holds: an entry wider than a whole page is given in pieces at character boundaries, never inside a surrogate pair,
/// each piece marked with <see cref="TextCut.Ellipsis"/> where the entry goes on or went on before it, and each section a
/// page ends inside one of, or inside a node's block, says it goes on after what the page gave of it, whatever sections
/// follow it on the page. The notes an answer ends with follow its last entry within the same room, every one of them
/// given, on as many pages as they take. Every operation of the graph tool answers through it, and the text fallback's
/// hits - each line whole - and a body's served lines are entries as any other.
/// </summary>
public static class GraphPages
{
    /// <summary>
    /// The characters held back from a page's entries for what surrounds them: the header, and the lines that say a
    /// section goes on. The notes an answer ends with are entries of their own, held within the room as any entry is.
    /// </summary>
    public const int ReservedCharacters = 4096;

    /// <summary>
    /// The least room a page gives its entries, whatever its leading lines take: what keeps a page of an answer whose
    /// leading lines are long moving the answer on.
    /// </summary>
    public const int LeastRoom = 256;

    /// <summary>What a page made again because the graph the view answers from changed since page 1 says.</summary>
    public const string Recounted = "the graph changed since page 1: pages recounted";

    /// <summary>
    /// What a page past the first says where no result of page 1 is kept to read it from - none was kept, or what was kept
    /// does not read, and <paramref name="why"/> says which - so it is counted on the graph as it stands, and may not be a
    /// page of the result page 1 was.
    /// </summary>
    /// <param name="why">Why what was kept does not read; null where nothing was kept.</param>
    public static string NotKept(string? why)
        => why is null
            ? "page 1 of this ask is not kept: pages counted on the graph as it stands"
            : $"page 1 of this ask is not kept ({why}): pages counted on the graph as it stands";

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

    /// <summary>
    /// The answer's <paramref name="page"/>, from its sections: the page holds whole entries, each section up to its
    /// size and the whole within <paramref name="mostCharacters"/> less <see cref="ReservedCharacters"/> and the leading
    /// lines - an entry wider than that given in pieces, a page each; the notes the answer ends with follow its last
    /// entries, held within the same room, on as many pages as they take; a page of one holds the whole answer and no
    /// header; a page past the last is refused with the answer's range.
    /// </summary>
    /// <param name="sections">The answer's sections, in their order; each page holds of every section what its size leaves.</param>
    /// <param name="leading">The lines the answer begins with, said on every page.</param>
    /// <param name="trailing">The notes the answer ends with, each given whole - or in pieces where it is wider than a page - after every entry.</param>
    /// <param name="page">The page asked, from 1.</param>
    /// <param name="asked">How the answer names what was asked, for a page past the last to say.</param>
    /// <param name="mostCharacters">The most characters an answer may hold.</param>
    /// <param name="note">What the header says after its counts, and a refusal after its range (<see cref="GraphPageAnswer.Note"/>).</param>
    public static GraphPageAnswer Assemble(
        IReadOnlyList<GraphSection> sections,
        IReadOnlyList<string> leading,
        IReadOnlyList<string> trailing,
        int page,
        string asked,
        int mostCharacters,
        string? note = null)
        => Lay(sections, leading, trailing, mostCharacters).Page(page, asked, note);

    /// <summary>
    /// The answer's pages, filled once from its sections as <see cref="Assemble"/> fills them: every page of it is given
    /// from what this fill found, so a later page is never filled again.
    /// </summary>
    /// <param name="sections">The answer's sections, in their order.</param>
    /// <param name="leading">The lines the answer begins with, said on every page.</param>
    /// <param name="trailing">The notes the answer ends with.</param>
    /// <param name="mostCharacters">The most characters an answer may hold.</param>
    public static Layout Lay(IReadOnlyList<GraphSection> sections, IReadOnlyList<string> leading, IReadOnlyList<string> trailing, int mostCharacters)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(leading);
        ArgumentNullException.ThrowIfNull(trailing);

        foreach (var section in sections)
        {
            if (section?.Problem() is { } problem)
            {
                throw new ArgumentException(problem, nameof(sections));
            }
        }

        var room = Math.Max(LeastRoom, mostCharacters - ReservedCharacters - leading.Sum(line => line.Length + 1));

        // The notes are entries of a section of their own that counts nothing, filled once every section is given: what
        // the answer ends with is held within a page's room as any entry is, every note of it given.
        var notes = new GraphSection(
            null,
            [.. trailing.SelectMany(line => line.Split('\n')).Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).Select(line => new GraphEntry(line, Lines: 0))],
            int.MaxValue);
        return new Layout([.. leading], Fills(sections, notes, room), notes, sections.Sum(section => section.Entries.Count(entry => entry.Lines > 0)));
    }

    /// <summary>An answer's pages as one fill found them (<see cref="Lay"/>): any of them given from it, none filled again.</summary>
    public sealed class Layout
    {
        private readonly IReadOnlyList<string> _leading;
        private readonly List<List<SectionOfPage>> _fills;
        private readonly GraphSection _notes;

        internal Layout(IReadOnlyList<string> leading, List<List<SectionOfPage>> fills, GraphSection notes, int total)
        {
            _leading = leading;
            _fills = fills;
            _notes = notes;
            Total = total;
        }

        /// <summary>How many pages the answer holds; one where all of it is given at once.</summary>
        public int Pages => Math.Max(1, _fills.Count);

        /// <summary>How many results the whole answer holds, counted once.</summary>
        public int Total { get; }

        /// <summary>The answer's <paramref name="page"/>, as <see cref="Assemble"/> gives it.</summary>
        /// <param name="page">The page asked, from 1.</param>
        /// <param name="asked">How the answer names what was asked, for a page past the last to say.</param>
        /// <param name="note">What the header says after its counts, and a refusal after its range (<see cref="GraphPageAnswer.Note"/>).</param>
        public GraphPageAnswer Page(int page, string asked, string? note = null)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
            ArgumentException.ThrowIfNullOrWhiteSpace(asked);

            var pages = Pages;
            var total = Total;
            var said = note is null ? string.Empty : $" · {note}";

            if (page > pages)
            {
                return new GraphPageAnswer(
                    $"page {Number(page)} of '{asked}': it has {Number(pages)} page{(pages == 1 ? string.Empty : "s")} (1 to {Number(pages)}){said}",
                    page,
                    pages,
                    total,
                    [],
                    note)
                {
                    Refusal = "past the last",
                };
            }

            var held = _fills.Count == 0 ? new List<SectionOfPage>() : _fills[page - 1];
            var builder = new StringBuilder();

            if (pages > 1 || note is not null)
            {
                builder.Append(Header(page, pages, total)).Append(said).Append('\n');
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

                // A section whose page ends inside an entry, or inside a node's block, says it goes on, right after what the
                // page gave of it - whatever later sections the page holds too: what the next page holds of it is its own.
                if (section.GoesOn is { } goesOn)
                {
                    builder.Append($"{TextCut.Ellipsis} {goesOn} goes on").Append('\n');
                }
            }

            return new GraphPageAnswer(
                builder.ToString().TrimEnd('\n'),
                page,
                pages,
                total,
                [.. held.Where(section => !ReferenceEquals(section.Section, _notes)).Select(section => new GraphPageSection(section.Section.Caption, section.Shown))],
                note);
        }
    }

    /// <summary>The header of a page of more than one: which page, the exact total, and how to ask for the next - or that this is the last.</summary>
    private static string Header(int page, int pages, int total)
        => page == pages
            ? $"page {Number(page)} of {Number(pages)} · the last"
            : $"page {Number(page)} of {Number(pages)} · {Number(total)} results · ask again with page: {Number(page + 1)} for the next";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>What one page held of one section.</summary>
    /// <param name="Section">The section.</param>
    /// <param name="Shown">The entries - or the piece of one - the page gave of it.</param>
    /// <param name="GoesOn">What the page says goes on where it ends here, inside an entry or a node's block; null where it ends between them.</param>
    internal sealed record SectionOfPage(GraphSection Section, IReadOnlyList<GraphEntry> Shown, string? GoesOn);

    /// <summary>One section's fill: where its cursor stands after the pages made so far.</summary>
    private sealed class SectionFill(GraphSection section)
    {
        /// <summary>The section being filled.</summary>
        public GraphSection Section { get; } = section;

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
    /// given, on the page its last entry is and the pages after. A block's head goes with its first counted line, so the
    /// page that serves a node's lines names it. Where a page would hold nothing, it takes as much of the first entry not
    /// yet given as its room holds, marked as going on: a page always moves the answer on, and none passes its room.
    /// </summary>
    private static List<List<SectionOfPage>> Fills(IReadOnlyList<GraphSection> sections, GraphSection notes, int room)
    {
        var fills = new List<List<SectionOfPage>>();
        var state = sections.Select(section => new SectionFill(section)).Append(new SectionFill(notes)).ToList();

        while (state.Any(fill => !fill.Done))
        {
            var page = new List<SectionOfPage>();
            var left = room;
            var given = 0;

            // The head of the block the page begins with, while the page holds nothing else: an entry of that block too
            // wide for what is left is given in pieces here, beside its head, rather than on a page of its own.
            GraphEntry? heading = null;

            foreach (var fill in state)
            {
                // The notes wait for every section's last entry.
                if (ReferenceEquals(fill.Section, notes) && state.Any(other => !other.Done && !ReferenceEquals(other.Section, notes)))
                {
                    continue;
                }

                var shown = new List<GraphEntry>();
                var budget = fill.Section.Size;
                var caption = fill.Section.Caption is { } said ? said.Length + 1 : 0;
                var cutInside = false;

                while (!fill.Done)
                {
                    var entry = fill.Section.Entries[fill.Cursor];
                    var whole = fill.Offset == 0;
                    var rest = whole ? entry.Text : TextCut.Ellipsis + entry.Text[fill.Offset..];
                    var cost = rest.Length + 1 + (shown.Count == 0 ? caption : 0);

                    if (whole)
                    {
                        if (entry.Lines > budget && shown.Count > 0)
                        {
                            break;
                        }

                        // A block's head rides only with the block's first counted line: a page with no room or no size left
                        // for that line ends before the head, and the next page begins with both.
                        if (Opens(entry, shown) && HeadWithItsLine(fill, caption, shown.Count) is var (headCost, headLines)
                            && ((headLines > budget && shown.Count > 0) || (headCost > left && given > 0)))
                        {
                            break;
                        }
                    }

                    if (cost <= left)
                    {
                        heading = (given == 0 || heading?.SameBlock(entry) == true) && whole && entry.Lines == 0 && entry.GoesOn is not null ? heading ?? entry : null;
                        shown.Add(Piece(entry, rest, first: whole, last: true));
                        left -= cost;
                        budget -= whole ? entry.Lines : 0;
                        fill.Cursor++;
                        fill.Offset = 0;
                        given++;
                        continue;
                    }

                    if (given > 0 && heading?.SameBlock(entry) != true)
                    {
                        break;
                    }

                    // The page holds nothing yet and the entry alone passes its room: as much of it as the page holds, the
                    // rest on the pages after. The cut is the one cut by characters, never inside a surrogate pair, and it
                    // always gives at least one character past the mark a continued piece begins with.
                    var lead = whole ? 0 : 1;
                    var keep = Math.Max(lead + 1, left - 1 - (shown.Count == 0 ? caption : 0) - 1);
                    var head = TextCut.Head(rest, keep);

                    if (head.Length <= lead)
                    {
                        head = rest[..Math.Min(rest.Length, lead + (char.IsHighSurrogate(rest[lead]) ? 2 : 1))];
                    }

                    // A piece that would hold the whole rest is no piece: it is given whole, past the room only where the
                    // room left holds less than one character of it.
                    if (head.Length == rest.Length)
                    {
                        shown.Add(Piece(entry, rest, first: whole, last: true));
                        budget -= whole ? entry.Lines : 0;
                        fill.Cursor++;
                        fill.Offset = 0;
                        left = 0;
                        given++;
                        break;
                    }

                    shown.Add(Piece(entry, head + TextCut.Ellipsis, first: whole, last: false));
                    budget -= whole ? entry.Lines : 0;
                    fill.Offset += head.Length - lead;
                    left = 0;
                    given++;
                    cutInside = true;
                    break;
                }

                if (shown.Count > 0)
                {
                    page.Add(new SectionOfPage(fill.Section, shown, GoesOnAt(fill, shown[^1], cutInside)));
                }
            }

            fills.Add(page);
        }

        return fills;
    }

    /// <summary>What a page whose last entry of a section is <paramref name="last"/> says goes on: the entry it ends inside, or the block the next entry goes on with; null where neither.</summary>
    private static string? GoesOnAt(SectionFill fill, GraphEntry last, bool cutInside)
    {
        if (cutInside)
        {
            return last.GoesOn ?? "the line";
        }

        return !fill.Done && last.SameBlock(fill.Section.Entries[fill.Cursor]) ? last.GoesOn : null;
    }

    /// <summary>Whether <paramref name="entry"/> opens a node's block here: an entry that counts nothing, of a block, after none of that block's.</summary>
    private static bool Opens(GraphEntry entry, IReadOnlyList<GraphEntry> shown)
        => entry.Lines == 0 && entry.GoesOn is not null && (shown.Count == 0 || !shown[^1].SameBlock(entry));

    /// <summary>
    /// What a block's head costs with the entries that ride with it through the block's first counted line - its
    /// characters, a section's caption among them where the page shows nothing of it yet, and the lines it counts.
    /// </summary>
    private static (int Cost, int Lines) HeadWithItsLine(SectionFill fill, int caption, int shown)
    {
        var entries = fill.Section.Entries;
        var head = entries[fill.Cursor];
        var cost = shown == 0 ? caption : 0;

        for (var at = fill.Cursor; at < entries.Count && (at == fill.Cursor || head.SameBlock(entries[at])); at++)
        {
            cost += entries[at].Text.Length + 1;

            if (entries[at].Lines > 0)
            {
                return (cost, entries[at].Lines);
            }
        }

        return (cost, 0);
    }

    /// <summary>
    /// The piece of <paramref name="entry"/> a page gives: the entry itself where it is given whole, else the piece's text,
    /// counting the entry's lines and its hit on its first piece alone, so the totals and the hits are counted once.
    /// </summary>
    private static GraphEntry Piece(GraphEntry entry, string text, bool first, bool last)
        => first && last ? entry : entry with { Text = text, Lines = first ? entry.Lines : 0, Hit = first && entry.Hit };
}
