using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Text;

namespace DssHarness.Core.Pages;

/// <summary>
/// One entry of a text's page: a line of the text, or a piece of one too wide for a page, each piece marked where the line
/// goes on and went on. A text's empty line is an entry of no text, which a text's page keeps where it falls - its last
/// line among them - as no answer laid in memory does (<see cref="IPageEntry.ProblemOf"/>).
/// </summary>
/// <param name="Text">The line, or the piece, as the page gives it: numbered where the text's pages are.</param>
/// <param name="Counts">One for a line, or a line's first piece; none for a later piece, so a text's lines are counted once.</param>
public sealed record TextEntry(string Text, int Counts = 1) : IPageEntry
{
    /// <inheritdoc/>
    string? IPageEntry.Block => null;

    /// <inheritdoc/>
    string? IPageEntry.BlockName => null;
}

/// <summary>
/// Where a page of a text begins: the byte in the text's file its first character is - in the file's own encoding, after
/// its mark - the line that character is of, from 1, and how many characters into that line it is: 0 where the page
/// begins with a line, more where it goes on with a piece of a line the page before ended inside; and, where it begins
/// inside a block of the text (<see cref="TextBlock"/>), the block's head, which it gives again first.
/// </summary>
/// <param name="Offset">The page's first byte, from the file's start.</param>
/// <param name="FirstLine">The line the page begins in, from 1.</param>
/// <param name="FirstColumn">The characters of that line the pages before gave.</param>
/// <param name="Head">
/// The head of the block the page begins inside, given again first; null where it begins inside none, or where its room
/// holds the head beside nothing - a head wider than a page among them.
/// </param>
public readonly record struct TextPageMark(long Offset, long FirstLine, long FirstColumn, TextPageHead? Head = null);

/// <summary>
/// The head of the block a page of a text begins inside (<see cref="TextPageMark.Head"/>): where the text's file holds the
/// line that heads it - its first byte and the bytes its characters take, its ending not among them - and the line it is,
/// from 1: the line itself is read from the file with the page, never kept.
/// </summary>
/// <param name="Offset">The head line's first byte, from the file's start.</param>
/// <param name="Length">The bytes its characters take.</param>
/// <param name="Line">The line it is, from 1.</param>
public readonly record struct TextPageHead(long Offset, int Length, long Line);

/// <summary>
/// The kept form of a text's pages: where each page begins (<see cref="TextPageMark"/>), never the text, laid once from
/// the file and kept for the conversation's life - so a page of a 2 GB log at 32K a page is read by seeking to one of some
/// 65,000 marks - under the digest of the bytes they were laid on, which a page asked of a file that changed since is not
/// read from (<see cref="TextPagesFreshness"/>). A form of an earlier schema - its marks naming no block's head - reads as
/// none, and the pages are laid again.
/// </summary>
/// <param name="Schema">The record's schema it was written under.</param>
/// <param name="Key">The ask it is kept for, as <see cref="PageKey"/> spells it.</param>
/// <param name="Path">The file the pages were laid on, absolute.</param>
/// <param name="Digest">The digest of the file's bytes as they were laid (<see cref="FileContentHash.Of(ReadOnlySpan{byte})"/>).</param>
/// <param name="Lines">How many lines the text holds, cut as a file's lines are: at CR LF, LF and CR, an empty text one empty line.</param>
/// <param name="Marks">Where each page begins, in order: one a page.</param>
/// <param name="Asked">How the pages name what was asked, for a page past the last to say.</param>
public sealed record TextPagesKept(int Schema, string Key, string Path, string Digest, long Lines, IReadOnlyList<TextPageMark> Marks, string Asked)
{
    /// <summary>The schema this build reads: 2 since a page that begins inside a block names its head.</summary>
    public const int CurrentSchema = 2;

    /// <summary>The contract the kept form is written and read with: one line of a state file's (<see cref="JsonStateFile.LineOptions"/>).</summary>
    public static JsonTypeInfo<TextPagesKept> Contract { get; } = KeptAnswers.Contract<TextPagesKept>(JsonStateFile.LineOptions);

    /// <summary>What is wrong with the kept form; <see langword="null"/> where nothing is. Every member is asked of, a missing one among them.</summary>
    public string? Problem()
        => Schema != CurrentSchema ? $"its schema is {Schema.ToString(CultureInfo.InvariantCulture)}, and this build reads {CurrentSchema.ToString(CultureInfo.InvariantCulture)}"
            : string.IsNullOrWhiteSpace(Key) ? "it names no ask"
            : string.IsNullOrWhiteSpace(Path) ? "it names no file"
            : string.IsNullOrWhiteSpace(Digest) ? "it names no digest"
            : string.IsNullOrWhiteSpace(Asked) ? "it names no ask to say"
            : Lines < 1 ? "a text holds a line at the least"
            : Marks is null || Marks.Count == 0 ? "it marks where each page begins"
            : Marks[0].FirstLine != 1 || Marks[0].FirstColumn != 0 || Marks[0].Offset < 0 || Marks[0].Head is not null ? "its first page begins at the text's first line"
            : !InOrder(Marks, Lines) ? "its pages begin in the text's order, each in a line of it"
            : !Headed(Marks) ? "the head a page gives again is a whole line of the text before the page"
            : null;

    /// <summary>Whether each mark begins after the one before it, in the file and in the text, and in a line of the text.</summary>
    private static bool InOrder(IReadOnlyList<TextPageMark> marks, long lines)
    {
        for (var at = 1; at < marks.Count; at++)
        {
            var (before, mark) = (marks[at - 1], marks[at]);

            if (mark.Offset <= before.Offset || mark.FirstColumn < 0 || mark.FirstLine > lines
                || mark.FirstLine < before.FirstLine || (mark.FirstLine == before.FirstLine && mark.FirstColumn <= before.FirstColumn))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether each head a page gives again is a line of the text before the page: past the text's first byte, its
    /// characters and the ending after them before the page's first byte, and its line before the page's first.
    /// </summary>
    private static bool Headed(IReadOnlyList<TextPageMark> marks)
        => marks.All(mark => mark.Head is not { } head
            || (head.Offset >= marks[0].Offset && head.Length >= 0 && head.Offset + head.Length < mark.Offset && head.Line >= 1 && head.Line < mark.FirstLine));
}

/// <summary>
/// What tells whether a text's pages kept still answer (<see cref="IKeptFreshness{TKept}"/>): the ask they are kept for, a
/// shape this build does not read, and the digest of the bytes they were laid on - a page asked of the file as it is now
/// is read from them only where its digest is theirs.
/// </summary>
public sealed class TextPagesFreshness : IKeptFreshness<TextPagesKept>
{
    /// <inheritdoc/>
    public string KeyOf(TextPagesKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        return kept.Key;
    }

    /// <inheritdoc/>
    public string? Problem(TextPagesKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        return kept.Problem();
    }

    /// <inheritdoc/>
    public string StateOf(TextPagesKept kept)
    {
        ArgumentNullException.ThrowIfNull(kept);

        return kept.Digest;
    }
}

/// <summary>
/// A text file's pages, streamed: laid in one pass over the file - its encoding read as <see cref="EncodedText"/> reads it,
/// its digest and its lines counted in the same pass - into the marks where each page begins, and any page then read by
/// seeking to its mark and decoding only its own bytes, so what a text costs in memory follows a page and its marks, never
/// the file's size. A page holds whole lines while its room lasts, each costing its characters and its line's end, and a
/// line that does not fit a page holding lines begins the next page; a line wider than a whole page is given in pieces by
/// the one rule a piece is cut by (<see cref="Pieces"/>), the page that ends inside it saying the line goes on - the pages
/// an answer laid in memory of the same lines gives (<see cref="Paging.Lay"/>), its header among them. A page's lines are
/// numbered <c>&lt;line&gt;\t&lt;text&gt;</c> where its pages are, as <c>view_file</c> numbers a file's lines; a piece that goes
/// on from another is not. A text laid with the blocks its writer kept beside it (<see cref="TextBlocks"/>) - a search's
/// hits beneath their declaration's line - gives a page that begins inside a block its head again first, said as going on
/// and counting nothing, charged to the page's room where the room holds it beside the least piece of what follows
/// (<see cref="Paging.HoldsAgain"/>), as a page of an answer laid in memory gives a result's block's; a page that begins at
/// a block's head, and every page of a text laid as its lines alone, is as it was.
/// </summary>
public static class TextPages
{
    /// <summary>What a page laid again because its text changed since its pages were laid says, after its header's counts.</summary>
    public const string Relaid = "the text changed since its pages were laid: pages laid again";

    /// <summary>
    /// What the page that laid a text's pages says where their marks could not be written: the marks are held in memory
    /// for the conversation's process, and a resumed conversation lays the pages again from the file.
    /// </summary>
    /// <param name="why">Why they could not be written.</param>
    public static string Unkept(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return $"its pages could not be kept ({why}): a resumed conversation lays them again from the file";
    }

    /// <summary>
    /// An entry of a numbered page as its text holds the line: the number and the tab a numbered page leads a line with
    /// taken off; a piece that goes on from another, which no number leads, as it stands.
    /// </summary>
    /// <param name="entry">The entry's text, as the numbered page gives it.</param>
    public static string Unnumbered(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var tab = entry.IndexOf('\t', StringComparison.Ordinal);

        return tab > 0 && entry.AsSpan(0, tab).IndexOfAnyExceptInRange('0', '9') < 0 ? entry[(tab + 1)..] : entry;
    }

    /// <summary>
    /// Lays the pages of the text at <paramref name="path"/> in one pass over its bytes - never holding the file, a line of
    /// it or a page of it whole - into where each page begins, with the digest of the bytes and the text's lines.
    /// </summary>
    /// <param name="fileSystem">The file system the file is read through.</param>
    /// <param name="path">The file, absolute.</param>
    /// <param name="key">The ask the pages are kept for (<see cref="PageKey"/>).</param>
    /// <param name="asked">How the pages name what was asked, for a page past the last to say.</param>
    /// <param name="room">The characters a page gives its lines (<see cref="PageRoom.For"/>), <see cref="PageRoom.Least"/> at the least.</param>
    /// <param name="numbered">Whether each line is numbered, as a page of it says it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The file does not read as text, with why, in <see cref="EncodedText"/>'s words.</exception>
    public static Task<TextPagesKept> LayAsync(IFileSystem fileSystem, string path, string key, string asked, int room, bool numbered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        return LayAsync(fileSystem.OpenRead, path, key, asked, room, numbered, cancellationToken);
    }

    /// <summary>
    /// Lays the pages of the text at <paramref name="path"/> as <see cref="LayAsync(IFileSystem, string, string, string, int, bool, CancellationToken)"/>
    /// lays them, the file opened by <paramref name="open"/>: a reader that decides where an open file stands before a
    /// byte of it is read opens it so, and a text held in memory is laid from a stream over its bytes.
    /// </summary>
    /// <param name="open">Opens the file at a path to read, from its start.</param>
    /// <param name="path">The file, absolute.</param>
    /// <param name="key">The ask the pages are kept for (<see cref="PageKey"/>).</param>
    /// <param name="asked">How the pages name what was asked, for a page past the last to say.</param>
    /// <param name="room">The characters a page gives its lines (<see cref="PageRoom.For"/>), <see cref="PageRoom.Least"/> at the least.</param>
    /// <param name="numbered">Whether each line is numbered, as a page of it says it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The file does not read as text, with why, in <see cref="EncodedText"/>'s words.</exception>
    public static Task<TextPagesKept> LayAsync(Func<string, Stream> open, string path, string key, string asked, int room, bool numbered, CancellationToken cancellationToken)
        => LayAsync(open, path, key, asked, room, numbered, blocks: null, cancellationToken);

    /// <summary>
    /// Lays the pages of the text at <paramref name="path"/> as <see cref="LayAsync(Func{string, Stream}, string, string, string, int, bool, CancellationToken)"/>
    /// lays them, with the <paramref name="blocks"/> its writer kept beside it, read in the same pass: a page that begins
    /// inside one gives its head again first. Blocks that are no blocks of the text - written beside it as it was before it
    /// changed, or not read as blocks are (<see cref="TextBlocks.Problem"/>) - give it none: its pages are laid again as its
    /// lines alone.
    /// </summary>
    /// <param name="open">Opens the file at a path to read, from its start.</param>
    /// <param name="path">The file, absolute.</param>
    /// <param name="key">The ask the pages are kept for (<see cref="PageKey"/>).</param>
    /// <param name="asked">How the pages name what was asked, for a page past the last to say.</param>
    /// <param name="room">The characters a page gives its lines (<see cref="PageRoom.For"/>), <see cref="PageRoom.Least"/> at the least.</param>
    /// <param name="numbered">Whether each line is numbered, as a page of it says it.</param>
    /// <param name="blocks">The text's blocks, read as its pages are laid; null where it is laid as its lines alone.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The file does not read as text, with why, in <see cref="EncodedText"/>'s words.</exception>
    public static async Task<TextPagesKept> LayAsync(Func<string, Stream> open, string path, string key, string asked, int room, bool numbered, TextBlocks? blocks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(asked);
        ArgumentOutOfRangeException.ThrowIfLessThan(room, PageRoom.Least);

        // Blocks known already to be none of the text's - another schema's - cost no pass of their own.
        var laid = blocks is { Problem: null } ? await LaidAsync(open, path, key, asked, room, numbered, blocks, cancellationToken).ConfigureAwait(false) : null;

        return laid ?? (await LaidAsync(open, path, key, asked, room, numbered, blocks: null, cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The pages of the text at <paramref name="path"/>, laid in one pass over its bytes with <paramref name="blocks"/> where
    /// they are given; null where they were and are no blocks of the text: not its as it is, or not read as blocks are.
    /// </summary>
    private static async Task<TextPagesKept?> LaidAsync(Func<string, Stream> open, string path, string key, string asked, int room, bool numbered, TextBlocks? blocks, CancellationToken cancellationToken)
    {
        var stream = open(path);

        await using (stream.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var text = await TextSource.OpenAsync(stream, path, hash, cancellationToken).ConfigureAwait(false);
            var marks = new List<TextPageMark>();

            // The line the next entry is of, and how many of its characters the pages so far gave: a line is done once its
            // last piece is given, so every line before `line` is.
            long line = 1;
            long column = 0;

            // The head of the block the lines given last are of, where a page gave it whole: the block, where its line is,
            // and its text - a page's room at the most - for a page that begins inside the block to give again.
            (TextBlock Block, TextPageHead Head, string Text)? held = null;

            // Whether every line is given: none is left to begin where the text has ended after a line - an empty text is
            // one empty line, given first.
            async Task<bool> DoneAsync() => column == 0 && line > 1 && await text.AtEndAsync(cancellationToken).ConfigureAwait(false);

            // Gives the rest of the line - its next count characters - and its ending: a line given whole that heads a block
            // is the head a page beginning inside the block gives again, where its line ends where the block's own begin.
            async Task GivenAsync(int count, bool whole)
            {
                var begins = text.Offset;
                var heads = whole && blocks?.At(begins) is { } block && block.Head == begins ? block : (TextBlock?)null;
                var said = heads is null ? null : text.Peek(count);

                text.Consume(count);
                var ends = text.Offset;
                await text.ConsumeLineAsync(0, cancellationToken).ConfigureAwait(false);

                if (heads is not { } headed)
                {
                    return;
                }

                if (text.Offset != headed.Body)
                {
                    blocks!.Refuse("a block's head is no one line of the text");
                    return;
                }

                held = (headed, new TextPageHead(begins, (int)(ends - begins), line), said!);
            }

            while (!await DoneAsync().ConfigureAwait(false))
            {
                var left = room;
                var given = 0;
                TextPageHead? again = null;

                // A page that begins inside a block gives its head again first, said as going on and counting nothing - a
                // head given whole, where the page's room holds it beside the least of the line it goes on with.
                if (blocks?.At(text.Offset) is { } inside && inside.Body <= text.Offset && held is { } head && head.Block == inside)
                {
                    var cost = Paging.Again(Lead(head.Head.Line, numbered) + head.Text).Length + 1;

                    if (Paging.HoldsAgain(cost + (column == 0 ? Lead(line, numbered).Length : 0), left))
                    {
                        left -= cost;
                        again = head.Head;
                    }
                }

                marks.Add(new TextPageMark(text.Offset, line, column, again));

                while (!await DoneAsync().ConfigureAwait(false))
                {
                    var prefix = column == 0 ? Lead(line, numbered) : string.Empty;
                    var continued = column > 0;
                    var lead = continued ? 1 : 0;

                    // The most of the line's characters left a rest given whole may hold: the rest, its mark or its number
                    // first, costs its characters and its line's end.
                    var fits = left - 1 - lead - prefix.Length;
                    var (remaining, ends) = await text.LineAheadAsync(Math.Max(fits, 0) + 1, cancellationToken).ConfigureAwait(false);

                    if (ends && remaining <= fits)
                    {
                        await GivenAsync(remaining, whole: !continued).ConfigureAwait(false);
                        left -= lead + prefix.Length + remaining + 1;
                        given++;
                        line++;
                        column = 0;
                        continue;
                    }

                    if (given > 0)
                    {
                        break;
                    }

                    // The page holds nothing yet and the rest of the line passes its room: a piece, by the one rule a piece is
                    // cut by, handed as much of the rest as the rule looks at.
                    var (seen, _) = await text.LineAheadAsync(Pieces.Window(continued, left) - lead - prefix.Length, cancellationToken).ConfigureAwait(false);
                    var rest = (continued ? TextCut.Ellipsis.ToString() : prefix) + text.Peek(seen);
                    var piece = Pieces.Head(rest, continued, left);

                    if (piece.Length == rest.Length)
                    {
                        await GivenAsync(seen, whole: !continued).ConfigureAwait(false);
                        line++;
                        column = 0;
                        break;
                    }

                    var taken = Pieces.Taken(piece, continued) - prefix.Length;
                    text.Consume(taken);
                    column += taken;
                    break;
                }
            }

            // The digest FileContentHash gives the same bytes, taken as they were read.
            var digest = Convert.ToHexStringLower(hash.GetHashAndReset());

            return blocks is not null && !string.Equals(blocks.End(text.Offset), digest, StringComparison.Ordinal)
                ? null
                : new TextPagesKept(TextPagesKept.CurrentSchema, key, path, digest, line - 1, marks, asked);
        }
    }

    /// <summary>What leads the text of <paramref name="line"/> as a page gives it whole: its number and a tab where the page is numbered, nothing where not.</summary>
    private static string Lead(long line, bool numbered) => numbered ? Paging.Number(line) + "\t" : string.Empty;

    /// <summary>
    /// The text's <paramref name="page"/>, read from the file by seeking to its mark and decoding only its own bytes: its
    /// header where the text holds more than one page or <paramref name="note"/> is said, the head of the block it begins
    /// inside again where its mark names one (<see cref="TextPageMark.Head"/>) - read from its own bytes, said as going on
    /// and counting nothing - its lines - numbered where <paramref name="numbered"/> - a piece of a line marked where it goes
    /// on and went on, and the page that ends inside a line saying the line goes on; a page past the last is refused with the
    /// text's range. The file is the one the pages were laid on: its digest is theirs (<see cref="TextPagesFreshness"/>), as
    /// its asker sees to.
    /// </summary>
    /// <param name="fileSystem">The file system the file is read through.</param>
    /// <param name="kept">The text's pages, as they were laid.</param>
    /// <param name="page">The page asked, from 1: one past what an int counts is past the last, said by its own number.</param>
    /// <param name="numbered">Whether each line is numbered: as the pages were laid.</param>
    /// <param name="note">What the header says after its counts, and a refusal after its range; null where nothing is said.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The page's bytes no longer read as they were laid.</exception>
    public static Task<PageAnswer<TextEntry>> PageAsync(IFileSystem fileSystem, TextPagesKept kept, long page, bool numbered, string? note, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        return PageAsync(fileSystem.OpenRead, kept, page, numbered, note, Paging.AskAgain, cancellationToken);
    }

    /// <summary>
    /// The text's <paramref name="page"/> as <see cref="PageAsync(IFileSystem, TextPagesKept, long, bool, string?, CancellationToken)"/>
    /// gives it, the file opened by <paramref name="open"/>, its header saying the next page is asked for as
    /// <paramref name="ask"/> says (<see cref="Paging.Header"/>).
    /// </summary>
    /// <param name="open">Opens the file at a path to read, from its start.</param>
    /// <param name="kept">The text's pages, as they were laid.</param>
    /// <param name="page">The page asked, from 1: one past what an int counts is past the last, said by its own number.</param>
    /// <param name="numbered">Whether each line is numbered: as the pages were laid.</param>
    /// <param name="note">What the header says after its counts, and a refusal after its range; null where nothing is said.</param>
    /// <param name="ask">How the header says the next page is asked for, before its <c>page</c>.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The page's bytes no longer read as they were laid.</exception>
    public static async Task<PageAnswer<TextEntry>> PageAsync(Func<string, Stream> open, TextPagesKept kept, long page, bool numbered, string? note, string ask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentException.ThrowIfNullOrWhiteSpace(ask);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        if (kept.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(kept));
        }

        var pages = kept.Marks.Count;

        if (page > pages)
        {
            // The answer's page is an int, as every answer's is: its text says the page as it was asked.
            return new PageAnswer<TextEntry>(Paging.PastTheLastSaid(page, pages, kept.Asked, note), (int)Math.Min(page, int.MaxValue), pages, kept.Lines, [], note)
            {
                Refusal = Paging.PastTheLast,
            };
        }

        // Within the pages, an int as their count is.
        var given = (int)page;
        var mark = kept.Marks[given - 1];
        TextPageMark? next = given < pages ? kept.Marks[given] : null;
        var cut = next is { FirstColumn: > 0 };
        var chars = await CharsAsync(open, kept.Path, mark.Offset, next?.Offset, $"its page {Paging.Number(given)} decodes no more", cancellationToken).ConfigureAwait(false);
        var entries = new List<TextEntry>();

        // A page that begins inside a block gives its head again first, said as going on and counting nothing: the line is
        // read from where the mark says the file holds it, which is the file the pages were laid on.
        if (mark.Head is { } head)
        {
            var again = await CharsAsync(open, kept.Path, head.Offset, head.Offset + head.Length, $"the head its page {Paging.Number(given)} gives again decodes no more", cancellationToken).ConfigureAwait(false);

            if (LineEnds.IndexOf(again) >= 0)
            {
                throw new InvalidDataException($"'{kept.Path}' does not read as it did when its pages were laid: the head its page {Paging.Number(given)} gives again is no one line");
            }

            entries.Add(new TextEntry(Paging.Again(Lead(head.Line, numbered) + again), Counts: 0));
        }

        var number = mark.FirstLine;
        var segments = Segments(chars);

        for (var at = 0; at < segments.Count; at++)
        {
            var continued = at == 0 && mark.FirstColumn > 0;
            var piece = at == segments.Count - 1 && cut;
            var lead = continued ? TextCut.Ellipsis.ToString() : Lead(number, numbered);

            entries.Add(new TextEntry(lead + segments[at] + (piece ? TextCut.Ellipsis.ToString() : string.Empty), continued ? 0 : 1));
            number++;
        }

        var said = new List<string>();

        if (Paging.HeaderLine(given, pages, kept.Lines, note, ask) is { } header)
        {
            said.Add(header);
        }

        said.AddRange(entries.Select(entry => entry.Text));

        if (cut)
        {
            said.Add(Paging.GoesOnLine(Paging.TheLine));
        }

        // Joined, never trimmed: a page that ends with empty lines gives them, as the text holds them.
        return new PageAnswer<TextEntry>(
            string.Join('\n', said),
            given,
            pages,
            kept.Lines,
            [new PageOfSection<TextEntry>(null, entries)],
            note);
    }

    /// <summary>
    /// The characters of the file at <paramref name="path"/> from its byte <paramref name="from"/> to <paramref name="to"/> -
    /// to its end where that is null - decoded strictly by the encoding its first bytes say: a span that no longer decodes
    /// is the file's refusal, saying <paramref name="what"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The span no longer reads as it did when the pages were laid.</exception>
    private static async Task<string> CharsAsync(Func<string, Stream> open, string path, long from, long? to, string what, CancellationToken cancellationToken)
    {
        var stream = open(path);

        await using (stream.ConfigureAwait(false))
        {
            var start = new byte[4];
            var read = await TextSource.FillAsync(stream, start, cancellationToken).ConfigureAwait(false);
            var (encoding, _) = TextSource.EncodingOf(start.AsSpan(0, read));
            var bytes = await TextSource.SpanAsync(stream, start.AsMemory(0, read), from, to, path, cancellationToken).ConfigureAwait(false);

            try
            {
                return encoding.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException($"'{path}' does not read as it did when its pages were laid: {what}");
            }
        }
    }

    /// <summary>
    /// The page that holds the start of <paramref name="line"/>, from 1: the last page that begins at or before it - so a
    /// line given in pieces is found on the page of its first.
    /// </summary>
    /// <param name="kept">The text's pages, as they were laid.</param>
    /// <param name="line">The line, from 1 to the text's last.</param>
    public static int PageOfLine(TextPagesKept kept, long line)
    {
        ArgumentNullException.ThrowIfNull(kept);
        ArgumentOutOfRangeException.ThrowIfLessThan(line, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(line, kept.Lines);

        var low = 0;
        var high = kept.Marks.Count - 1;

        while (low < high)
        {
            var middle = low + ((high - low + 1) / 2);
            var mark = kept.Marks[middle];

            if (mark.FirstLine < line || (mark.FirstLine == line && mark.FirstColumn == 0))
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low + 1;
    }

    /// <summary>The key of a refusal's data that holds whether its file is binary, rather than no text the rule reads.</summary>
    private const string BinaryKey = "binary";

    /// <summary>The key of a refusal's data that holds why its file reads as no text, as <see cref="EncodedTextReading.Why"/> says it.</summary>
    private const string WhyKey = "why";

    /// <summary>
    /// The refusal of a file's bytes that read as no text: binary, or no text <see cref="EncodedText"/>'s rule reads - the
    /// file named in its message as a page's reader says it, and whether it is binary and why, in the rule's words, in its
    /// data, for a whole text's reading to answer as <see cref="EncodedText.Read"/> does.
    /// </summary>
    internal static InvalidDataException NotText(string message, bool binary, string why)
    {
        var refusal = new InvalidDataException(message);
        refusal.Data[BinaryKey] = binary;
        refusal.Data[WhyKey] = why;
        return refusal;
    }

    /// <summary>
    /// What <paramref name="refusal"/>, a refusal of a file's bytes as no text (<see cref="NotText"/>), says of them, as
    /// <see cref="EncodedText.Read"/> says it - binary, or no text the rule reads, and why; <see langword="null"/> where it
    /// is no such refusal.
    /// </summary>
    /// <param name="refusal">What a streamed reading of the file threw.</param>
    internal static EncodedTextReading? ReadingOf(InvalidDataException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        return refusal.Data[WhyKey] is string why ? EncodedTextReading.Unread(refusal.Data[BinaryKey] is true, why) : null;
    }

    /// <summary>
    /// The strict encoding the bytes a text's file begins with say, and how many of them its mark is - the rule a text's
    /// pages are laid by, <see cref="EncodedText"/>'s with its NUL apart: what every reader that streams a file's text
    /// decodes it by, so a text it reads is the text its pages give.
    /// </summary>
    /// <param name="start">The file's first bytes, four at the least where it holds them.</param>
    internal static (Encoding Encoding, int Preamble) EncodingOf(ReadOnlySpan<byte> start) => TextSource.EncodingOf(start);

    /// <summary>
    /// A page's characters cut into its lines, or the pieces of lines its ends hold, without their endings, by the one rule
    /// of where a line ends (<see cref="LineEnds"/>): an ending the page's characters end with closes its line rather than
    /// opening another, and a page of no characters is an empty text's one empty line.
    /// </summary>
    private static List<string> Segments(string chars) => LineEnds.Spans(chars, (start, end, _) => chars[start..end]);

    /// <summary>
    /// A file's text as one pass reads it, a block at a time: its encoding read from its first bytes as
    /// <see cref="EncodedText"/> reads it, each byte read hashed, and each character decoded strictly and held only until it
    /// is consumed, the byte offset of the next one kept: the one streamed reading of a text file by <see cref="EncodedText"/>'s
    /// rule, a page's and the codebase graph's text of a file alike (<see cref="Codebase.CodeText"/>).
    /// </summary>
    internal sealed class TextSource
    {
        /// <summary>How many bytes are read at a time.</summary>
        private const int Block = 64 * 1024;

        private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly UnicodeEncoding Utf16LittleStrict = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
        private static readonly UnicodeEncoding Utf16BigStrict = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

        private readonly Stream _stream;
        private readonly IncrementalHash? _hash;
        private readonly string _path;
        private readonly Decoder _decoder;
        private readonly bool _utf16;
        private readonly int _preamble;
        private readonly byte[] _block = new byte[Block];
        private char[] _chars = new char[Block + 4];
        private int _start;
        private int _end;

        /// <summary>Whether every byte of the file is decoded, its decoder flushed.</summary>
        private bool _drained;

        /// <summary>How many of the bytes after the mark were handed to the decoder before the block being decoded: where a byte that does not decode stands.</summary>
        private long _decoded;

        private TextSource(Stream stream, IncrementalHash? hash, string path, Encoding encoding, int preamble)
        {
            _stream = stream;
            _hash = hash;
            _path = path;
            _decoder = encoding.GetDecoder();
            _utf16 = encoding is UnicodeEncoding;
            _preamble = preamble;
            Offset = preamble;
            Decoding = encoding;
        }

        /// <summary>The byte of the file the next character not consumed begins at.</summary>
        public long Offset { get; private set; }

        /// <summary>The strict encoding its bytes after the mark are decoded by.</summary>
        public Encoding Decoding { get; }

        /// <summary>How many bytes its mark is: none where it has none.</summary>
        public int Preamble => _preamble;

        /// <summary>
        /// The text of <paramref name="stream"/>, from its first byte: UTF-16 by its mark - a UTF-32 mark, which begins as
        /// UTF-16's does, being none - then binary by a NUL among its first <see cref="BinaryContent.Window"/> bytes, then
        /// UTF-8 with or without its mark. Each byte read is hashed by <paramref name="hash"/>, where one is given.
        /// </summary>
        /// <exception cref="InvalidDataException">It is binary.</exception>
        public static async Task<TextSource> OpenAsync(Stream stream, string path, IncrementalHash? hash, CancellationToken cancellationToken)
        {
            var head = new byte[BinaryContent.Window];
            var read = await FillAsync(stream, head, cancellationToken).ConfigureAwait(false);
            var (encoding, preamble) = EncodingOf(head.AsSpan(0, read));

            hash?.AppendData(head, 0, read);

            if (encoding is not UnicodeEncoding && BinaryContent.Holds(head.AsSpan(0, read)))
            {
                throw NotText($"'{path}' is binary: {EncodedText.BinaryWhy}", binary: true, EncodedText.BinaryWhy);
            }

            var text = new TextSource(stream, hash, path, encoding, preamble);
            text.Decode(head.AsSpan(preamble, read - preamble), flush: read < head.Length);
            return text;
        }

        /// <summary>The strict encoding the bytes a file begins with say, and how many of them its mark is: <see cref="EncodedText"/>'s rule, its NUL apart.</summary>
        public static (Encoding Encoding, int Preamble) EncodingOf(ReadOnlySpan<byte> start)
            => start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]) ? (Utf8Strict, 0)
                : start.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) ? (Utf16LittleStrict, 2)
                : start.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? (Utf16BigStrict, 2)
                : Utf8Text.OpensWithByteOrderMark(start) ? (Utf8Strict, Encoding.UTF8.Preamble.Length)
                : (Utf8Strict, 0);

        /// <summary>Reads <paramref name="stream"/> into <paramref name="buffer"/> until it is full or the stream ends: how many bytes it holds.</summary>
        public static async Task<int> FillAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var filled = 0;

            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[filled..], cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            return filled;
        }

        /// <summary>
        /// The bytes of <paramref name="stream"/> from <paramref name="from"/> to <paramref name="to"/> - to its end where
        /// that is null - its first bytes, <paramref name="start"/>, read already: seeking where the stream seeks, else
        /// reading on past what is not asked.
        /// </summary>
        /// <exception cref="InvalidDataException">The file ends before the page does: it is no longer the file its pages were laid on.</exception>
        public static async Task<byte[]> SpanAsync(Stream stream, ReadOnlyMemory<byte> start, long from, long? to, string path, CancellationToken cancellationToken)
        {
            using var span = new MemoryStream();

            if (stream.CanSeek)
            {
                stream.Seek(from, SeekOrigin.Begin);
            }
            else if (from < start.Length)
            {
                span.Write(start.Span[(int)from..]);
            }
            else
            {
                var skip = new byte[Block];

                for (var left = from - start.Length; left > 0;)
                {
                    var read = await stream.ReadAsync(skip.AsMemory(0, (int)Math.Min(left, skip.Length)), cancellationToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        throw Shorter(path);
                    }

                    left -= read;
                }
            }

            if (to is not { } end)
            {
                await stream.CopyToAsync(span, cancellationToken).ConfigureAwait(false);
                return span.ToArray();
            }

            var bytes = new byte[end - from];
            span.Position = 0;
            var held = span.Read(bytes);

            if (held + await FillAsync(stream, bytes.AsMemory(held), cancellationToken).ConfigureAwait(false) < bytes.Length)
            {
                throw Shorter(path);
            }

            return bytes;
        }

        /// <summary>Whether no character is left: every byte read and decoded, and every character consumed.</summary>
        public async Task<bool> AtEndAsync(CancellationToken cancellationToken) => await AvailableAsync(1, cancellationToken).ConfigureAwait(false) == 0;

        /// <summary>
        /// How many characters of the line the next character is of come before its ending, looked at up to
        /// <paramref name="most"/> of them, and whether its ending - or the text's end - is among them: where it is not, the
        /// line holds at least that many more.
        /// </summary>
        public async Task<(int Characters, bool Ends)> LineAheadAsync(int most, CancellationToken cancellationToken)
        {
            var available = await AvailableAsync(most, cancellationToken).ConfigureAwait(false);
            var ending = LineEnds.IndexOf(_chars.AsSpan(_start, available));

            return ending >= 0 ? (ending, true)
                : available < most ? (available, true)
                : (most, false);
        }

        /// <summary>The next <paramref name="count"/> characters, which <see cref="LineAheadAsync"/> looked at, not consumed.</summary>
        public string Peek(int count) => new(_chars, _start, count);

        /// <summary>Consumes the next <paramref name="count"/> characters, which <see cref="LineAheadAsync"/> looked at, the offset moving past their bytes.</summary>
        public void Consume(int count)
        {
            for (var at = _start; at < _start + count; at++)
            {
                Offset += _utf16 ? 2 : Utf8Width(_chars[at]);
            }

            _start += count;
        }

        /// <summary>Consumes the next <paramref name="count"/> characters, the rest of a line, and the ending after them: a CR LF whole.</summary>
        public async Task ConsumeLineAsync(int count, CancellationToken cancellationToken)
        {
            Consume(count);

            if (await AvailableAsync(1, cancellationToken).ConfigureAwait(false) == 0)
            {
                return;
            }

            var ending = _chars[_start];
            Consume(1);

            if (await AvailableAsync(1, cancellationToken).ConfigureAwait(false) > 0 && LineEnds.Joined(ending, _chars[_start]))
            {
                Consume(1);
            }
        }

        /// <summary>The bytes a character of a UTF-8 text took: each half of a surrogate pair two, so the pair takes its four.</summary>
        private static int Utf8Width(char character) => character < 0x80 ? 1 : character < 0x800 || char.IsSurrogate(character) ? 2 : 3;

        /// <summary>Why a file whose bytes ended before a page of it did is read no more.</summary>
        private static InvalidDataException Shorter(string path) => new($"'{path}' does not read as it did when its pages were laid: it ends before them");

        /// <summary>How many characters are held unconsumed, reading on until <paramref name="count"/> are or the file ends: at most <paramref name="count"/>.</summary>
        private async Task<int> AvailableAsync(int count, CancellationToken cancellationToken)
        {
            while (_end - _start < count && !_drained)
            {
                var read = await _stream.ReadAsync(_block, cancellationToken).ConfigureAwait(false);

                _hash?.AppendData(_block, 0, read);
                Decode(_block.AsSpan(0, read), flush: read == 0);
            }

            return Math.Min(count, _end - _start);
        }

        /// <summary>
        /// The characters next, as many as <paramref name="into"/> holds or the file has left, copied into it and consumed -
        /// each decoded strictly, so a byte no character is made of is the file's refusal as a page's is: how many. The
        /// offset is not kept past them, for a reader of every character counts their bytes itself.
        /// </summary>
        public async Task<int> ReadAsync(Memory<char> into, CancellationToken cancellationToken)
        {
            var held = await AvailableAsync(into.Length, cancellationToken).ConfigureAwait(false);

            _chars.AsSpan(_start, held).CopyTo(into.Span);
            _start += held;
            return held;
        }

        /// <summary>Decodes <paramref name="bytes"/> after the characters held, strictly: a byte no character is made of is the file's refusal, with where it stands.</summary>
        private void Decode(ReadOnlySpan<byte> bytes, bool flush)
        {
            // Room for every character the bytes can make, what the decoder held of the block before among them.
            if (_chars.Length - _end < bytes.Length + 4)
            {
                var held = _end - _start;
                var chars = _chars.Length - held < bytes.Length + 4 ? new char[Math.Max(_chars.Length * 2, held + bytes.Length + 4)] : _chars;

                Array.Copy(_chars, _start, chars, 0, held);
                (_chars, _start, _end) = (chars, 0, held);
            }

            var used = 0;

            try
            {
                while (used < bytes.Length || flush)
                {
                    _decoder.Convert(bytes[used..], _chars.AsSpan(_end), flush, out var bytesUsed, out var charsUsed, out var completed);
                    used += bytesUsed;
                    _end += charsUsed;

                    if (completed && used >= bytes.Length)
                    {
                        break;
                    }
                }
            }
            catch (DecoderFallbackException ex)
            {
                // Where the byte stands is counted from the first after the mark, as the whole text's reading counts it; a
                // sequence the block before began stands before this block's first byte.
                var why = _utf16 ? EncodedText.NotUtf16Why : EncodedText.NotUtf8Why(Utf8Text.NotUtf8(_decoded + used + ex.Index, ex.BytesUnknown?.FirstOrDefault() ?? 0));

                throw NotText($"'{_path}' is no text this tool reads: {why}", binary: false, why);
            }

            _decoded += bytes.Length;
            _drained = flush;
        }
    }
}
