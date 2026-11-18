using System.Globalization;
using System.Text;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase;

/// <summary>
/// One entry of an answer the pages hold: a whole line of it, and how much of its section's page it counts. An entry is
/// never cut; a page that cannot hold the next one ends, and the next page holds it.
/// </summary>
/// <param name="Text">The entry's text, one line of the answer.</param>
/// <param name="Lines">
/// How much of its section's size the entry counts: one for a result, a node, a hit or a served line; none for a line
/// that only says something - a block's head, a gap a body left out, a note - which rides on whatever page holds it.
/// </param>
/// <param name="GoesOn">
/// Where the entry is one of a node's served lines, or the head of its block, that node's qualified name: what a page
/// that ends inside the block says goes on. Set on every entry of the block, so a page that ends where another node
/// begins says nothing.
/// </param>
/// <param name="Node">The node whose lines this entry gave, by its id, where it gave any; what a page's served nodes are read from.</param>
/// <param name="Hit">Whether the entry is a line a text search added where the graph was not sure.</param>
public sealed record GraphEntry(string Text, int Lines = 1, string? GoesOn = null, string? Node = null, bool Hit = false)
{
    /// <summary>What is wrong with the entry; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => string.IsNullOrEmpty(Text) || Text.Contains('\n') || Text.Contains('\r') ? "an entry is one line, and never empty"
            : Lines < 0 ? "an entry counts no fewer than no lines"
            : null;
}

/// <summary>
/// One section of an answer: a list of entries the pages hold whole, up to <see cref="Size"/> of them a page, its
/// caption said on every page that holds entries of it - so a page read alone still names what its entries are of.
/// </summary>
/// <param name="Caption">The line above the section's entries, where one is given.</param>
/// <param name="Entries">The section's entries, in their stable order.</param>
/// <param name="Size">How much of the section a page holds, in its entries' <see cref="GraphEntry.Lines"/>.</param>
public sealed record GraphSection(string? Caption, IReadOnlyList<GraphEntry> Entries, int Size)
{
    /// <summary>What is wrong with the section; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
        => Size < 1 ? "a section's size is at least one"
            : Entries.FirstOrDefault(entry => entry?.Problem() is not null) is { } wrong ? wrong.Problem()!
            : null;
}

/// <summary>An answer asked as its pages give it.</summary>
/// <param name="Text">The answer for the page asked, as the model reads it.</param>
/// <param name="Page">The page given.</param>
/// <param name="Pages">How many pages the answer holds; one where all of it was given at once.</param>
/// <param name="Total">How many results the whole answer holds, counted once, never estimated.</param>
/// <param name="Entries">The entries the page gave.</param>
/// <param name="Recounted">Whether the page was made again because the graph changed since page 1.</param>
public sealed record GraphPageAnswer(string Text, int Page, int Pages, int Total, IReadOnlyList<GraphEntry> Entries, bool Recounted)
{
    /// <summary>Why the page asked is none of the answer, where it is not; <see langword="null"/> where it is.</summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// The one owner of an answer's pages: no answer of the graph is cut, but each is pages of whole entries, with the page
/// said, any page asked by number, and the totals exact. Every operation of the graph tool answers through it, and the
/// text fallback's hits and a body's served lines are entries as any other.
/// </summary>
public static class GraphPages
{
    /// <summary>
    /// The characters held back from a page's entries for what surrounds them: the header, the notes an answer ends
    /// with, and room for a split block to end at a whole line.
    /// </summary>
    public const int ReservedCharacters = 4096;

    /// <summary>
    /// The answer's <paramref name="page"/>, from its sections: the page holds whole entries, each section up to its
    /// size and the whole within <paramref name="mostCharacters"/> less <see cref="ReservedCharacters"/>; a page of one
    /// holds the whole answer and no header; a page past the last is refused with the answer's range.
    /// </summary>
    /// <param name="sections">The answer's sections, in their order; each page holds of every section what its size leaves.</param>
    /// <param name="leading">The lines the answer begins with, said on every page.</param>
    /// <param name="trailing">The notes the answer ends with, said on its last page alone.</param>
    /// <param name="page">The page asked, from 1.</param>
    /// <param name="asked">How the answer names what was asked, for a page past the last to say.</param>
    /// <param name="mostCharacters">The most characters an answer may hold.</param>
    /// <param name="recounted">Whether the page was made again because the graph changed since page 1.</param>
    public static GraphPageAnswer Assemble(
        IReadOnlyList<GraphSection> sections,
        IReadOnlyList<string> leading,
        IReadOnlyList<string> trailing,
        int page,
        string asked,
        int mostCharacters,
        bool recounted = false)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(leading);
        ArgumentNullException.ThrowIfNull(trailing);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(asked);

        foreach (var section in sections)
        {
            if (section.Problem() is { } problem)
            {
                throw new ArgumentException(problem, nameof(sections));
            }
        }

        var fills = Fills(sections, mostCharacters);
        var pages = Math.Max(1, fills.Count);
        var total = sections.Sum(section => section.Entries.Count(entry => entry.Lines > 0));

        if (page > pages)
        {
            return new GraphPageAnswer(
                $"page {page.ToString(CultureInfo.InvariantCulture)} of '{asked}': it has {pages.ToString(CultureInfo.InvariantCulture)} page{(pages == 1 ? string.Empty : "s")} (1 to {pages.ToString(CultureInfo.InvariantCulture)})",
                page,
                pages,
                total,
                [],
                recounted)
            {
                Refusal = "past the last",
            };
        }

        var entries = fills.Count == 0 ? new List<SectionOfPage>() : fills[page - 1];
        var builder = new StringBuilder();

        if (pages > 1)
        {
            builder.Append(Header(page, pages, total, recounted)).Append('\n');
        }

        foreach (var line in leading)
        {
            builder.Append(line).Append('\n');
        }

        foreach (var (_, section, shown, _) in entries)
        {
            if (shown.Count > 0 && section.Caption is { } caption)
            {
                builder.Append(caption).Append('\n');
            }

            foreach (var entry in shown)
            {
                builder.Append(entry.Text).Append('\n');
            }
        }

        // A page that ends inside a node's block says the node goes on: the entries after it are its own.
        if (entries.Count > 0)
        {
            var last = entries[^1];

            if (last.Through < last.Section.Entries.Count && last.Shown[^1].GoesOn is { } goesOn
                && last.Section.Entries[last.Through].GoesOn == goesOn)
            {
                builder.Append($"… {goesOn} goes on").Append('\n');
            }
        }

        if (page == pages)
        {
            foreach (var line in trailing)
            {
                builder.Append(line).Append('\n');
            }
        }

        return new GraphPageAnswer(
            builder.ToString().TrimEnd('\n'),
            page,
            pages,
            total,
            [.. entries.SelectMany(fill => fill.Shown)],
            recounted);
    }

    /// <summary>The header of a page of more than one: which page, the exact total, and how to ask for the next - or that this is the last.</summary>
    private static string Header(int page, int pages, int total, bool recounted)
    {
        var head = $"page {page.ToString(CultureInfo.InvariantCulture)} of {pages.ToString(CultureInfo.InvariantCulture)}";

        if (recounted)
        {
            return page == pages ? $"{head} · the last · the graph changed since page 1: pages recounted" : $"{head} · the graph changed since page 1: pages recounted";
        }

        return page == pages
            ? $"{head} · the last"
            : $"{head} · {total.ToString(CultureInfo.InvariantCulture)} results · ask again with page: {(page + 1).ToString(CultureInfo.InvariantCulture)} for the next";
    }

    /// <summary>What one page held of one section.</summary>
    /// <param name="Fill">The section's fill, holding where its cursor stands.</param>
    /// <param name="Section">The section.</param>
    /// <param name="Shown">The entries the page gave of it.</param>
    /// <param name="Through">Where the section's cursor stood at this page's end: what tells a page that ends inside a block that the block goes on.</param>
    private sealed record SectionOfPage(SectionFill Fill, GraphSection Section, IReadOnlyList<GraphEntry> Shown, int Through);

    /// <summary>One section's fill: where its cursor stands after the pages made so far.</summary>
    private sealed class SectionFill(GraphSection section)
    {
        /// <summary>The section being filled.</summary>
        public GraphSection Section { get; } = section;

        /// <summary>How many of its entries the pages so far have given.</summary>
        public int Cursor { get; set; }
    }

    /// <summary>
    /// The entries of every page, filled once from the sections: each page takes of every section, in its order, whole
    /// entries up to the section's size while the page has room; an entry no room is left for begins the page after,
    /// save where a page would hold nothing, which takes the first entry not yet given whole. A page always holds at
    /// least one entry while any section has one left, so no answer is cut.
    /// </summary>
    private static List<List<SectionOfPage>> Fills(IReadOnlyList<GraphSection> sections, int mostCharacters)
    {
        var fills = new List<List<SectionOfPage>>();
        var state = sections.Select(section => new SectionFill(section)).ToList();

        for (; ; )
        {
            var page = new List<SectionOfPage>();
            var room = Math.Max(0, mostCharacters - ReservedCharacters);
            var given = 0;

            foreach (var fill in state)
            {
                var shown = new List<GraphEntry>();
                var budget = fill.Section.Size;

                while (fill.Cursor < fill.Section.Entries.Count)
                {
                    var entry = fill.Section.Entries[fill.Cursor];
                    var cost = entry.Text.Length + 1;

                    if ((entry.Lines > budget && shown.Count > 0) || (cost > room && given > 0))
                    {
                        break;
                    }

                    room -= Math.Min(cost, room);
                    budget -= entry.Lines;
                    shown.Add(entry);
                    fill.Cursor++;
                    given++;
                }

                if (shown.Count > 0)
                {
                    page.Add(new SectionOfPage(fill, fill.Section, shown, fill.Cursor));
                }
            }

            if (page.Count == 0)
            {
                break;
            }

            fills.Add(page);

            if (state.All(fill => fill.Cursor >= fill.Section.Entries.Count))
            {
                break;
            }
        }

        return fills;
    }
}
