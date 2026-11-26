using System.Text;
using DssHarness.Core.Output;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Codebase;

/// <summary>One line of a graph text a step is given unasked - its context pack, a conversation's changes - in its section.</summary>
/// <param name="Caption">The line of the section it is of, said above its first line on each page that holds it; null for a section with none.</param>
/// <param name="Text">The line, one line, as the text writes it.</param>
/// <param name="Id">The node it lists; null for a line that lists none.</param>
public sealed record GraphTextLine(string? Caption, string Text, string? Id = null);

/// <summary>One page of a graph text given unasked (<see cref="GraphTextPages"/>).</summary>
/// <param name="Page">The page, from 1.</param>
/// <param name="Text">The page as it is given: its first line, its lines under their captions, and its last line.</param>
/// <param name="Lines">The lines it gives, a line given in pieces once, on the page its first piece is, in the order it gives them.</param>
/// <param name="Tokens">Its estimated tokens (<see cref="EstimatedTokens"/>).</param>
/// <param name="Last">Whether no page follows it.</param>
public sealed record GraphTextPage(int Page, string Text, IReadOnlyList<GraphTextLine> Lines, int Tokens, bool Last);

/// <summary>What a page's last line is made from: which page, the lines it gives, how many pages the text holds, and its tokens.</summary>
/// <param name="Page">The page, from 1.</param>
/// <param name="Lines">The lines it gives.</param>
/// <param name="Pages">How many pages the text holds: known on every page, for every line is laid before any page is written.</param>
/// <param name="Tokens">The page's estimated tokens without its last line: the line says the whole page's.</param>
public sealed record GraphTextPageEnd(int Page, IReadOnlyList<GraphTextLine> Lines, int Pages, int Tokens)
{
    /// <summary>Whether no page follows it.</summary>
    public bool Last => Page == Pages;
}

/// <summary>
/// The pages of a graph text a step is given without asking - its context pack, the changes a conversation gone on with is
/// told, a repository map - each page of a size in estimated tokens (<see cref="EstimatedTokens"/>) or of an exact room in
/// bytes: page 1 goes in the step's message, and every later page is the text going on, so a text of any length is given
/// whole. The text is laid whole as it is made: every line is read once, in order, and laid on its page before any page
/// is written, so every page's last line knows how many pages there are, the first as well as the last, and a page asked
/// again is the one written. A page holds whole lines, each costing its bytes and its line's end and its section's
/// caption where the page begins the section, within the page's room less its first line and <see cref="LastLineRoom"/>
/// for its last - or, in the exact-room form, of all the room and no reserve; a line that does not fit begins the next
/// page, and a line wider than a whole page is given in pieces by the one rule a piece is cut by
/// (<see cref="Pages.Paging"/>'s), each marked where it goes on.
/// </summary>
public sealed class GraphTextPages
{
    /// <summary>The bytes a page keeps for its last line, whatever it says.</summary>
    public const int LastLineRoom = 200;

    private readonly List<GraphTextPage> _pages = [];

    /// <summary>The pages of <paramref name="lines"/>, each of <paramref name="tokens"/> estimated tokens, laid whole.</summary>
    /// <param name="lines">The text's lines, in order: read once, to their end.</param>
    /// <param name="tokens">The estimated tokens of a page, its first and last lines among them; at least one.</param>
    /// <param name="first">The first line of each page, by its number.</param>
    /// <param name="last">The last line of each page, from what it holds; null for a page that ends with its lines, one that says all there is.</param>
    /// <param name="arranged">How a page's lines are written, from the order they were laid in: as they are, where it is null.</param>
    public GraphTextPages(IEnumerable<GraphTextLine> lines, int tokens, Func<int, string> first, Func<GraphTextPageEnd, string?> last, Func<IReadOnlyList<GraphTextLine>, IReadOnlyList<GraphTextLine>>? arranged = null)
        : this(lines, RoomOf(tokens), LastLineRoom, first, last, arranged)
    {
    }

    /// <summary>
    /// The pages of <paramref name="lines"/> in pages of <paramref name="room"/> bytes each - the exact-room form a text
    /// takes whose first page must hold what a size in tokens holds, as a repository map's does - laid whole.
    /// </summary>
    /// <param name="lines">The text's lines, in order: read once, to their end.</param>
    /// <param name="room">The bytes a page holds of its lines, its first line among them; at least one.</param>
    /// <param name="lastLineRoom">The bytes a page keeps of its room for its last line; 0 where the last line stands beyond the room, however long it is.</param>
    /// <param name="first">The first line of each page, by its number.</param>
    /// <param name="last">The last line of each page, from what it holds; null for a page that ends with its lines, one that says all there is.</param>
    /// <param name="arranged">How a page's lines are written, from the order they were laid in: as they are, where it is null.</param>
    public GraphTextPages(IEnumerable<GraphTextLine> lines, long room, int lastLineRoom, Func<int, string> first, Func<GraphTextPageEnd, string?> last, Func<IReadOnlyList<GraphTextLine>, IReadOnlyList<GraphTextLine>>? arranged = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(last);
        ArgumentOutOfRangeException.ThrowIfLessThan(room, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(lastLineRoom);

        // Every page's lines are laid to the text's end before any page is written: only then is how many there are
        // known, and every page's last line says it.
        var laid = new Laying(lines, room, lastLineRoom, first, arranged ?? (given => given)).All();

        for (var at = 0; at < laid.Count; at++)
        {
            var (given, said) = laid[at];
            var body = string.Join('\n', said);
            var draft = new GraphTextPageEnd(at + 1, given, laid.Count, EstimatedTokens.Of(body));
            var text = last(draft) is { } closing

                // The line says the page's tokens, itself among them: made again with the count its first making gives.
                ? body + "\n" + last(draft with { Tokens = EstimatedTokens.Of(body + "\n" + closing) })
                : body;

            _pages.Add(new GraphTextPage(at + 1, text, given, EstimatedTokens.Of(text), draft.Last));
        }
    }

    /// <summary>How many pages the text holds; none where page 1 cannot hold its own first and last lines.</summary>
    public int Count => _pages.Count;

    /// <summary>Every page of the text, in order.</summary>
    public IReadOnlyList<GraphTextPage> All => _pages;

    /// <summary>Page <paramref name="page"/>; null where the text ends before it, or has no page at all.</summary>
    /// <param name="page">The page asked, from 1.</param>
    public GraphTextPage? Page(int page)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        return page <= _pages.Count ? _pages[page - 1] : null;
    }

    /// <summary>The bytes of a page of <paramref name="tokens"/> estimated tokens.</summary>
    private static long RoomOf(int tokens)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tokens, 1);
        return (long)tokens * EstimatedTokens.BytesPerToken;
    }

    /// <summary>The characters a piece's room is, for <see cref="Pieces.Head"/>: as many of <paramref name="rest"/>'s first characters as <paramref name="bytes"/> hold with its mark and its line's end.</summary>
    private static int Fitting(string rest, long bytes)
    {
        var room = bytes - Encoding.UTF8.GetByteCount(TextCut.Ellipsis.ToString()) - 1;
        var characters = 0;

        while (characters < rest.Length)
        {
            var width = char.IsHighSurrogate(rest[characters]) && characters + 1 < rest.Length ? 2 : 1;
            var cost = Encoding.UTF8.GetByteCount(rest.AsSpan(characters, width));

            if (cost > room)
            {
                break;
            }

            room -= cost;
            characters += width;
        }

        // Pieces keeps its room less the mark and the line's end: what fits, given back with them.
        return characters + 2;
    }

    /// <summary>The bytes a line costs a page: its UTF-8 bytes and its line's end.</summary>
    private static int Bytes(string line) => Encoding.UTF8.GetByteCount(line) + 1;

    /// <summary>The laying of a text's lines on its pages, each read once, in order, to the text's end.</summary>
    private sealed class Laying(IEnumerable<GraphTextLine> lines, long room, int lastLineRoom, Func<int, string> first, Func<IReadOnlyList<GraphTextLine>, IReadOnlyList<GraphTextLine>> arranged)
    {
        private readonly IEnumerator<GraphTextLine> _lines = lines.GetEnumerator();

        /// <summary>The line read and not yet given whole, with how many of its characters the pages before gave; null where none is held.</summary>
        private (GraphTextLine Line, int Given)? _held;

        /// <summary>Whether every line is read.</summary>
        private bool _drained;

        /// <summary>Every page's lines and what it says before its last line, in order; none where page 1 cannot hold its own first and last lines.</summary>
        public List<(IReadOnlyList<GraphTextLine> Lines, IReadOnlyList<string> Said)> All()
        {
            var laid = new List<(IReadOnlyList<GraphTextLine> Lines, IReadOnlyList<string> Said)>();

            using (_lines)
            {
                while (Lay(laid.Count + 1) is { } page)
                {
                    laid.Add(page);
                }
            }

            return laid;
        }

        /// <summary>Whether a line is left to give after the pages laid: one held, or one more to read.</summary>
        private bool More()
        {
            if (_held is not null)
            {
                return true;
            }

            if (_drained)
            {
                return false;
            }

            if (_lines.MoveNext())
            {
                _held = (_lines.Current, 0);
                return true;
            }

            _drained = true;
            return false;
        }

        /// <summary>Page <paramref name="number"/>'s lines and what it says before its last line; null where no line is left, or page 1 cannot hold its first and last lines.</summary>
        private (IReadOnlyList<GraphTextLine> Lines, IReadOnlyList<string> Said)? Lay(int number)
        {
            if (number > 1 && !More())
            {
                return null;
            }

            var opening = first(number);
            var left = room - Bytes(opening) - lastLineRoom;

            if (left < 0)
            {
                // A first page that cannot hold its first and last lines gives nothing: the step's own size, too small for
                // its head. A later page that cannot is a text whose lines would be left out, which no text is.
                return number == 1 ? null : throw new InvalidOperationException($"page {number.ToString(System.Globalization.CultureInfo.InvariantCulture)} of a graph text cannot hold its own first and last lines in a page's room, so its lines would be left out");
            }

            // Each line the page gives, with the text it gives of it, and whether that is the line whole.
            var given = new List<Given>();

            while (More())
            {
                var (line, offset) = _held!.Value;
                var rest = Pieces.Rest(line.Text, offset);
                var opens = given.Count == 0 || !string.Equals(given[^1].Line.Caption, line.Caption, StringComparison.Ordinal);
                var caption = opens && line.Caption is { } heading ? Bytes(heading) : 0;
                var cost = Bytes(rest) + caption;

                if (cost <= left)
                {
                    given.Add(new Given(line, rest, First: offset == 0, Whole: offset == 0));
                    left -= cost;
                    _held = null;
                    continue;
                }

                if (given.Count > 0)
                {
                    break;
                }

                // The page holds nothing yet and the line alone passes its room: as much of it as the page holds, by the one
                // rule a piece is cut by, measured in the bytes a page is counted in.
                var head = Pieces.Head(rest, continued: offset > 0, Fitting(rest, left - caption));

                if (head.Length == rest.Length)
                {
                    given.Add(new Given(line, rest, First: offset == 0, Whole: offset == 0));
                    _held = null;
                    break;
                }

                given.Add(new Given(line, Pieces.Marked(head), First: offset == 0, Whole: false));
                _held = (line, offset + Pieces.Taken(head, continued: offset > 0));
                break;
            }

            // A section's caption is said above its first line on each page that holds it; a piece is marked where it goes on.
            var written = Arranged(given);
            var said = new List<string> { opening };
            string? captioned = null;

            foreach (var one in written)
            {
                if (one.Line.Caption is { } caption && !string.Equals(caption, captioned, StringComparison.Ordinal))
                {
                    said.Add(caption);
                }

                captioned = one.Line.Caption;
                said.Add(one.Text);
            }

            // The lines the page gives, each once - on the page its first piece is - in the order the page writes them.
            return ([.. written.Where(one => one.First).Select(one => one.Line)], said);
        }

        /// <summary>
        /// The page's lines in the order <c>arranged</c> writes them, where every one is whole; as they were laid where one
        /// is a piece, for a piece stays where it was cut.
        /// </summary>
        private List<Given> Arranged(List<Given> given)
            => given.All(one => one.Whole) ? [.. arranged([.. given.Select(one => one.Line)]).Select(line => new Given(line, line.Text, First: true, Whole: true))] : given;
    }

    /// <summary>One line a page gives: the text it gives of it, whether this is its first piece, and whether it is the line whole.</summary>
    private sealed record Given(GraphTextLine Line, string Text, bool First, bool Whole);
}
