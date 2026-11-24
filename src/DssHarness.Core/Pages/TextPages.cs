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
/// begins with a line, more where it goes on with a piece of a line the page before ended inside.
/// </summary>
/// <param name="Offset">The page's first byte, from the file's start.</param>
/// <param name="FirstLine">The line the page begins in, from 1.</param>
/// <param name="FirstColumn">The characters of that line the pages before gave.</param>
public readonly record struct TextPageMark(long Offset, long FirstLine, long FirstColumn);

/// <summary>
/// The kept form of a text's pages: where each page begins (<see cref="TextPageMark"/>), never the text, laid once from
/// the file and kept for the conversation's life - so a page of a 2 GB log at 32K a page is read by seeking to one of some
/// 65,000 marks - under the digest of the bytes they were laid on, which a page asked of a file that changed since is not
/// read from (<see cref="TextPagesFreshness"/>).
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
    /// <summary>The schema this build reads.</summary>
    public const int CurrentSchema = 1;

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
            : Marks[0].FirstLine != 1 || Marks[0].FirstColumn != 0 || Marks[0].Offset < 0 ? "its first page begins at the text's first line"
            : !InOrder(Marks, Lines) ? "its pages begin in the text's order, each in a line of it"
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
/// on from another is not.
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
    public static async Task<TextPagesKept> LayAsync(Func<string, Stream> open, string path, string key, string asked, int room, bool numbered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(asked);
        ArgumentOutOfRangeException.ThrowIfLessThan(room, PageRoom.Least);

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

            // Whether every line is given: none is left to begin where the text has ended after a line - an empty text is
            // one empty line, given first.
            async Task<bool> DoneAsync() => column == 0 && line > 1 && await text.AtEndAsync(cancellationToken).ConfigureAwait(false);

            while (!await DoneAsync().ConfigureAwait(false))
            {
                marks.Add(new TextPageMark(text.Offset, line, column));
                var left = room;
                var given = 0;

                while (!await DoneAsync().ConfigureAwait(false))
                {
                    var prefix = column == 0 && numbered ? Paging.Number(line) + "\t" : string.Empty;
                    var continued = column > 0;
                    var lead = continued ? 1 : 0;

                    // The most of the line's characters left a rest given whole may hold: the rest, its mark or its number
                    // first, costs its characters and its line's end.
                    var fits = left - 1 - lead - prefix.Length;
                    var (remaining, ends) = await text.LineAheadAsync(Math.Max(fits, 0) + 1, cancellationToken).ConfigureAwait(false);

                    if (ends && remaining <= fits)
                    {
                        await text.ConsumeLineAsync(remaining, cancellationToken).ConfigureAwait(false);
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
                    var head = Pieces.Head(rest, continued, left);

                    if (head.Length == rest.Length)
                    {
                        await text.ConsumeLineAsync(seen, cancellationToken).ConfigureAwait(false);
                        line++;
                        column = 0;
                        break;
                    }

                    var taken = Pieces.Taken(head, continued) - prefix.Length;
                    text.Consume(taken);
                    column += taken;
                    break;
                }
            }

            // The digest FileContentHash gives the same bytes, taken as they were read.
            return new TextPagesKept(TextPagesKept.CurrentSchema, key, path, Convert.ToHexStringLower(hash.GetHashAndReset()), line - 1, marks, asked);
        }
    }

    /// <summary>
    /// The text's <paramref name="page"/>, read from the file by seeking to its mark and decoding only its own bytes: its
    /// header where the text holds more than one page or <paramref name="note"/> is said, its lines - numbered where
    /// <paramref name="numbered"/> - a piece of a line marked where it goes on and went on, and the page that ends inside a
    /// line saying the line goes on; a page past the last is refused with the text's range. The file is the one the pages
    /// were laid on: its digest is theirs (<see cref="TextPagesFreshness"/>), as its asker sees to.
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
        string chars;

        var stream = open(kept.Path);

        await using (stream.ConfigureAwait(false))
        {
            var start = new byte[4];
            var read = await TextSource.FillAsync(stream, start, cancellationToken).ConfigureAwait(false);
            var (encoding, _) = TextSource.EncodingOf(start.AsSpan(0, read));
            var bytes = await TextSource.SpanAsync(stream, start.AsMemory(0, read), mark.Offset, next?.Offset, kept.Path, cancellationToken).ConfigureAwait(false);

            try
            {
                chars = encoding.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException($"'{kept.Path}' does not read as it did when its pages were laid: its page {Paging.Number(given)} decodes no more");
            }
        }

        var entries = new List<TextEntry>();
        var number = mark.FirstLine;
        var segments = Segments(chars);

        for (var at = 0; at < segments.Count; at++)
        {
            var continued = at == 0 && mark.FirstColumn > 0;
            var piece = at == segments.Count - 1 && cut;
            var lead = continued ? TextCut.Ellipsis.ToString() : numbered ? Paging.Number(number) + "\t" : string.Empty;

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
