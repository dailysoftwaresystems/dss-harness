using System.Globalization;
using System.Text;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Pages;

namespace DssHarness.Tests;

/// <summary>
/// A text file's pages, streamed: laid in one pass into where each page begins, and any page read by seeking to its mark -
/// a file of hundreds of megabytes with a line of tens of them in a bounded heap; the page of a line found by its number;
/// a file that changed since its pages were laid laid again, the page saying so; the pages an answer laid in memory of the
/// same lines gives, in every encoding a text is read in, its lines cut as a file's are, numbered or not, a line wider than
/// a page in pieces never inside a surrogate pair; its empty lines kept; and a file that is no text refused as the text
/// rule refuses it.
/// </summary>
/// <remarks>Measured alone (<see cref="MemoryMeasured"/>): the heap one of them is held to is the process's.</remarks>
[Collection(MemoryMeasured.Name)]
public sealed class TextPagesTests
{
    /// <summary>
    /// How much more this process's heap may hold while a file of some 200 MB is paged: many times what a page and its
    /// marks hold, and a small part of its 30 MB line held whole as a string, 60 MB, or of the file held whole.
    /// </summary>
    private const long HeapBound = 16L * 1024 * 1024;

    private static readonly IFileSystem Disk = new PhysicalFileSystem(DssHarness.Core.Platform.FilePermissionsFactory.Create());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A file of some 200 MB - a million lines of 99 characters, one line of 30 MB, 700,000 lines more - is paged in a
    /// heap that grows by no more than a few pages: its lines counted exactly, every page of short lines as many whole
    /// ones as the room holds, the long line beginning a page of its own and given in pieces, each marked where it goes on
    /// and went on - its first the room less its mark and its line's end, each later one a character fewer for the mark it
    /// goes on from - its rest riding with the lines after it; the page of a piece in its middle read by seeking to its
    /// mark, and the page of each line found by its number.
    /// </summary>
    [Fact]
    public async Task ATwoHundredMegabyteFileWithAThirtyMegabyteLine_IsPaged_InBoundedMemory()
    {
        const int Before = 1_000_000;
        const int After = 700_000;
        const int Giant = 30 * 1024 * 1024;
        const long Lines = Before + 1L + After;
        var room = PageRoom.For(PageRoom.ModelAnswer, []);

        using var temp = new TempDirectory();
        var path = temp.Combine("big.log");
        WriteBig(path, Before, Giant, After);

        // Whole short lines a page, each costing its 99 characters and its line's end; the long line begins the page after
        // the last of them, wider than any page.
        var perPage = room / 100;
        var giantPage = (Before / perPage) + (Before % perPage > 0 ? 1 : 0) + 1;

        // Its pieces, by the rule a piece is cut by: the first the room less its mark and its line's end, each later one a
        // character fewer for the mark it goes on from, until its rest fits a page whole after its mark.
        var first = room - 2;
        var later = room - 3;
        long given = first;
        var pieces = 1;

        while (Giant - given > room - 2)
        {
            given += later;
            pieces++;
        }

        var rest = (int)(Giant - given);
        var restPage = giantPage + pieces;
        var riding = (room - (rest + 2)) / 100;
        var remainder = (After - riding) % perPage;
        var lastCount = remainder > 0 ? remainder : perPage;
        var pages = restPage + ((After - riding + perPage - 1) / perPage);
        var middle = giantPage + 5;
        var middleColumn = first + (4L * later);

        TextPagesKept kept;
        PageAnswer<TextEntry> inMiddle;
        PageAnswer<TextEntry> ofRest;
        PageAnswer<TextEntry> ofLast;

        using (var heap = new HeapWatch())
        {
            kept = await TextPages.LayAsync(Disk, path, PageKey.OfCall("toolu_big"), "read_result toolu_big", room, numbered: false, Token);
            inMiddle = await TextPages.PageAsync(Disk, kept, middle, numbered: false, note: null, Token);
            ofRest = await TextPages.PageAsync(Disk, kept, restPage, numbered: false, note: null, Token);
            ofLast = await TextPages.PageAsync(Disk, kept, pages, numbered: false, note: null, Token);

            Assert.True(heap.Growth <= HeapBound, $"the heap grew by {heap.Growth:N0} bytes as the file was paged, past the bound of {HeapBound:N0}: {heap.Where}");
        }

        Assert.Equal((Lines, pages), (kept.Lines, kept.Marks.Count));
        Assert.Equal(new TextPageMark(Before * 100L, Before + 1, 0), kept.Marks[giantPage - 1]);
        Assert.Equal(new TextPageMark((Before * 100L) + middleColumn, Before + 1, middleColumn), kept.Marks[middle - 1]);

        Assert.Equal(
            $"page {middle} of {pages} · {Lines} results · ask again with page: {middle + 1} for the next\n…{Patterned(middleColumn, later)}…\n… the line goes on",
            inMiddle.Text);
        Assert.Equal(
            $"page {restPage} of {pages} · {Lines} results · ask again with page: {restPage + 1} for the next\n…{Patterned(given, rest)}\n"
                + string.Join('\n', Enumerable.Range(Before + 2, riding).Select(Short)),
            ofRest.Text);
        Assert.Equal(
            $"page {pages} of {pages} · the last\n" + string.Join('\n', Enumerable.Range((int)Lines - lastCount + 1, lastCount).Select(Short)),
            ofLast.Text);

        // Each line's page found by its number: a line given in pieces on the page of its first.
        Assert.Equal(1, TextPages.PageOfLine(kept, 1));
        Assert.Equal(giantPage - 1, TextPages.PageOfLine(kept, Before));
        Assert.Equal(giantPage, TextPages.PageOfLine(kept, Before + 1));
        Assert.Equal(restPage, TextPages.PageOfLine(kept, Before + 2));
        Assert.Equal(pages, TextPages.PageOfLine(kept, Lines));
    }

    /// <summary>
    /// The page of a line is the page that holds its start - for every line of a text, the page whose counted entries hold
    /// it - a line given in pieces found on the page of its first piece, never on a later one that goes on with it.
    /// </summary>
    [Fact]
    public async Task PageOfLine_FindsAMiddleLinesPage()
    {
        using var temp = new TempDirectory();
        var lines = Enumerable.Range(1, 10).Select(Sixty).Append(Patterned(0, 1000)).Concat(Enumerable.Range(12, 10).Select(Sixty)).ToList();
        var path = temp.Combine("lines.txt");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");

        var kept = await Lay(path, PageRoom.Least);

        // The page each line's counted entry is on, read from the pages themselves.
        var onPage = new List<int>();

        for (var page = 1; page <= kept.Marks.Count; page++)
        {
            var answer = await TextPages.PageAsync(Disk, kept, page, numbered: false, note: null, Token);
            onPage.AddRange(answer.Entries.Where(entry => entry.Counts > 0).Select(_ => page));
        }

        Assert.Equal(lines.Count, onPage.Count);
        Assert.All(Enumerable.Range(1, lines.Count), line => Assert.Equal(onPage[line - 1], TextPages.PageOfLine(kept, line)));

        // Four lines of sixty a page; the wide line begins page 4 and goes on to page 7, the line after it begins page 8.
        Assert.Equal([1, 2, 3, 4, 8, 10], new long[] { 1, 6, 10, 11, 12, 21 }.Select(line => TextPages.PageOfLine(kept, line)));
    }

    /// <summary>
    /// A result's file that changed since its pages were laid is laid again, the page that lays it saying so after its
    /// counts; the page after it is read from the pages laid again, with nothing said; and a resumed conversation reads
    /// the pages kept on the file as it now is, saying nothing either.
    /// </summary>
    [Fact]
    public async Task AFileThatChangedSinceItsPagesWereLaid_IsLaidAgain_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var file = AgentCallRecords.ResultFile(temp.Path, "toolu_r");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Concat(Enumerable.Range(1, 400).Select(line => Marked('r', line) + "\n")));

        var results = KeptResults.Of(Disk, temp.Path);
        var perPage = KeptResults.Room / 100;

        var before = await results.ReadAsync("toolu_r", 1, Token);

        Assert.Equal($"page 1 of 2 · 400 results · ask again with page: 2 for the next\n{Joined('r', 1, perPage)}", before!.Text);

        File.WriteAllText(file, string.Concat(Enumerable.Range(1, 450).Select(line => Marked('s', line) + "\n")));

        var relaid = await results.ReadAsync("toolu_r", 1, Token);
        var next = await results.ReadAsync("toolu_r", 2, Token);
        var resumed = await KeptResults.Of(Disk, temp.Path).ReadAsync("toolu_r", 2, Token);

        Assert.Equal($"page 1 of 2 · 450 results · ask again with page: 2 for the next · {TextPages.Relaid}\n{Joined('s', 1, perPage)}", relaid!.Text);
        Assert.Equal($"page 2 of 2 · the last\n{Joined('s', perPage + 1, 450 - perPage)}", next!.Text);
        Assert.Equal(next.Text, resumed!.Text);
    }

    /// <summary>
    /// A line wider than a page is cut where an answer laid in memory of the same lines cuts it, never inside a surrogate
    /// pair: a piece whose last character would be the first half of a pair ends one before it, the next piece beginning
    /// with the pair whole; each page's mark is at the byte its first character is, each half of a pair two bytes of UTF-8.
    /// </summary>
    [Fact]
    public async Task ALineWiderThanAPage_IsCutBetweenSurrogatePairs_AsAnAnswerInMemoryIs()
    {
        using var temp = new TempDirectory();
        var wide = "a" + string.Concat(Enumerable.Repeat("😀", 300));
        var path = temp.Combine("pairs.txt");
        File.WriteAllText(path, $"before\n{wide}\nafter\n");

        var kept = await Lay(path, PageRoom.Least);
        var pages = await AllPages(kept, numbered: false);

        AssertTheOracle(["before", wide, "after"], PageRoom.Least, numbered: false, kept, pages);
        Assert.Equal($"page 2 of {kept.Marks.Count} · 3 results · ask again with page: 3 for the next\n{wide[..253]}…\n… the line goes on", pages[1].Text);
        Assert.Equal(7 + Encoding.UTF8.GetByteCount(wide[..253]), kept.Marks[2].Offset);
        Assert.All(pages, page => _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetBytes(page.Text));
    }

    /// <summary>
    /// A text gives the same pages in every encoding a text is read in - UTF-8 with and without its mark, UTF-16 by its
    /// mark either way round - each page's mark the byte its first line begins at, after the mark, in the file's own
    /// encoding; and the digest the pages are kept under is the file's bytes' own.
    /// </summary>
    [Fact]
    public async Task EveryEncodingATextIsReadIn_GivesTheSamePages_EachMarkAtItsByte()
    {
        using var temp = new TempDirectory();
        string[] lines = ["é" + new string('a', 125), "😀" + new string('b', 124), new string('c', 126), new string('d', 126)];
        var text = string.Join('\n', lines) + "\n";

        foreach (var (name, bytes, marks) in new (string, byte[], long[])[]
        {
            ("utf-8", Encoding.UTF8.GetBytes(text), [0, 257]),
            ("utf-8-marked", [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)], [3, 260]),
            ("utf-16-little", [.. Encoding.Unicode.Preamble, .. Encoding.Unicode.GetBytes(text)], [2, 510]),
            ("utf-16-big", [.. Encoding.BigEndianUnicode.Preamble, .. Encoding.BigEndianUnicode.GetBytes(text)], [2, 510]),
        })
        {
            var path = temp.Combine(name + ".txt");
            File.WriteAllBytes(path, bytes);

            var kept = await Lay(path, PageRoom.Least);
            var pages = await AllPages(kept, numbered: false);

            Assert.Equal([new TextPageMark(marks[0], 1, 0), new TextPageMark(marks[1], 3, 0)], kept.Marks);
            Assert.Equal(FileContentHash.Of(bytes), kept.Digest);
            Assert.Equal(
                [
                    $"page 1 of 2 · 4 results · ask again with page: 2 for the next\n{lines[0]}\n{lines[1]}",
                    $"page 2 of 2 · the last\n{lines[2]}\n{lines[3]}",
                ],
                pages.Select(page => page.Text));
        }
    }

    /// <summary>
    /// Every page of a text whose lines end every way a file's do - CR LF, LF, CR, and none at its end - is the page an
    /// answer laid in memory of the same lines gives, numbered or not, asked in any order: its lines the ones a file's text
    /// is cut into, a line wider than a page in pieces, the totals and the headers the same.
    /// </summary>
    [Fact]
    public async Task EveryPageOfATextWithMixedEndings_IsThePageAnAnswerInMemoryGives_InAnyOrder()
    {
        using var temp = new TempDirectory();
        var text = "one\r\ntwo\rthree\n" + Patterned(0, 700) + "\r\n"
            + string.Concat(Enumerable.Range(0, 30).Select(at => $"line {at:D2} {new string('m', 40 + at)}" + (at % 3) switch { 0 => "\n", 1 => "\r\n", _ => "\r" }))
            + "the last, with no ending";
        var lines = CodeFileText.Lines(text);
        var path = temp.Combine("mixed.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

        Assert.DoesNotContain(string.Empty, lines);

        foreach (var numbered in new[] { false, true })
        {
            var kept = await Lay(path, PageRoom.Least, numbered);

            AssertTheOracle(lines, PageRoom.Least, numbered, kept, await AllPages(kept, numbered));
        }
    }

    /// <summary>
    /// A text's empty lines are lines of its pages where they fall, its last among them: a page that ends with them gives
    /// them, never trimmed; a text that ends with an empty line before its final ending gives it; and an empty text is one
    /// empty line.
    /// </summary>
    [Fact]
    public async Task ATextsEmptyLines_AreKept_ItsLastAmongThem()
    {
        using var temp = new TempDirectory();
        var (a, b) = (new string('a', 126), new string('b', 126));

        foreach (var (name, text, expected) in new (string, string, string[])[]
        {
            ("ends-with-empty.txt", $"{a}\n{b}\n\n\nc", [$"page 1 of 2 · 5 results · ask again with page: 2 for the next\n{a}\n{b}\n\n", "page 2 of 2 · the last\nc"]),
            ("empty-last.txt", "x\n\n", ["x\n"]),
            ("empty.txt", string.Empty, [string.Empty]),
        })
        {
            var path = temp.Combine(name);
            File.WriteAllText(path, text);

            var kept = await Lay(path, PageRoom.Least);

            Assert.Equal(CodeFileText.Lines(text).Count, kept.Lines);
            Assert.Equal(expected, (await AllPages(kept, numbered: false)).Select(page => page.Text));
        }
    }

    /// <summary>
    /// A file that is no text is refused as the one text rule refuses it, by its words: a byte that is no UTF-8 past the
    /// first block read, one whose sequence the block before began, one the file ends inside, a UTF-16 mark with no UTF-16
    /// after it; and a file that holds a NUL among its first bytes, a UTF-32 mark among them, as binary.
    /// </summary>
    [Fact]
    public async Task AFileThatIsNoText_IsRefused_AsTheTextRuleRefusesIt()
    {
        using var temp = new TempDirectory();

        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("deep.txt", [.. Enumerable.Repeat((byte)'a', 100_000), 0xE9, (byte)'A', (byte)'\n']),
            ("across.txt", [.. Enumerable.Repeat((byte)'a', BinaryContent.Window + (64 * 1024) - 1), 0xE9, (byte)'A', (byte)'\n']),
            ("cut.txt", [.. Enumerable.Repeat((byte)'a', 70_000), 0xC3]),
            ("odd.txt", [0xFF, 0xFE, (byte)'a', 0, (byte)'b']),
        })
        {
            var path = temp.Combine(name);
            File.WriteAllBytes(path, bytes);

            var refused = await Assert.ThrowsAsync<InvalidDataException>(() => Lay(path, PageRoom.Least));

            Assert.Equal($"'{path}' is no text this tool reads: {EncodedText.Read(bytes).Why}", refused.Message);
        }

        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("nul.bin", [(byte)'a', 0, (byte)'b']),
            ("utf-32.txt", [0xFF, 0xFE, 0, 0, (byte)'a', 0, 0, 0]),
        })
        {
            var path = temp.Combine(name);
            File.WriteAllBytes(path, bytes);

            var refused = await Assert.ThrowsAsync<InvalidDataException>(() => Lay(path, PageRoom.Least));

            Assert.True(EncodedText.Read(bytes).Binary, name);
            Assert.Equal($"'{path}' is binary: {EncodedText.BinaryWhy}", refused.Message);
        }
    }

    /// <summary>The pages of the text at <paramref name="path"/>, laid at <paramref name="room"/> for a call of these tests.</summary>
    private static Task<TextPagesKept> Lay(string path, int room, bool numbered = false)
        => TextPages.LayAsync(Disk, path, PageKey.OfCall("toolu_t"), "read_result toolu_t", room, numbered, Token);

    /// <summary>Every page of <paramref name="kept"/>, read last first, in their order.</summary>
    private static async Task<IReadOnlyList<PageAnswer<TextEntry>>> AllPages(TextPagesKept kept, bool numbered)
    {
        var pages = new List<PageAnswer<TextEntry>>();

        for (var page = kept.Marks.Count; page >= 1; page--)
        {
            pages.Insert(0, await TextPages.PageAsync(Disk, kept, page, numbered, note: null, Token));
        }

        return pages;
    }

    /// <summary>
    /// That <paramref name="pages"/> are the pages an answer laid in memory of <paramref name="lines"/> gives at
    /// <paramref name="room"/>: as many, the same totals, each page's text and its entries - their pieces and what each
    /// counts - the same.
    /// </summary>
    private static void AssertTheOracle(IReadOnlyList<string> lines, int room, bool numbered, TextPagesKept kept, IReadOnlyList<PageAnswer<TextEntry>> pages)
    {
        var oracle = Paging.Lay(
            [new PageSection<TextEntry>(null, [.. lines.Select((line, at) => new TextEntry(numbered ? $"{(at + 1).ToString(CultureInfo.InvariantCulture)}\t{line}" : line))], int.MaxValue)],
            [],
            [],
            room,
            (entry, text, first) => entry with { Text = text, Counts = first ? entry.Counts : 0 },
            line => new TextEntry(line, Counts: 0));

        Assert.Equal((oracle.Pages, oracle.Total), (kept.Marks.Count, kept.Lines));
        Assert.True(oracle.Pages > 1, "the text is more than a page");

        for (var page = oracle.Pages; page >= 1; page--)
        {
            var expected = oracle.Page(page, "read_result toolu_t");

            Assert.Equal(expected.Text, pages[page - 1].Text);
            Assert.Equal(expected.Entries.Select(entry => (entry.Text, entry.Counts)), pages[page - 1].Entries.Select(entry => (entry.Text, entry.Counts)));
        }
    }

    /// <summary>Writes the big file: <paramref name="before"/> short lines, one of <paramref name="giant"/> characters of a pattern, <paramref name="after"/> short lines - each line ended with LF.</summary>
    private static void WriteBig(string path, int before, int giant, int after)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);

        for (var line = 1; line <= before; line++)
        {
            file.Write(Encoding.ASCII.GetBytes(Short(line) + "\n"));
        }

        // A block of whole runs of the pattern, so each block goes on with the pattern where the one before ended.
        var block = Encoding.ASCII.GetBytes(Patterned(0, 26 * 4096));

        for (long left = giant; left > 0;)
        {
            var take = (int)Math.Min(left, block.Length);
            file.Write(block, 0, take);
            left -= take;
        }

        file.WriteByte((byte)'\n');

        for (var line = before + 2; line <= before + 1 + after; line++)
        {
            file.Write(Encoding.ASCII.GetBytes(Short(line) + "\n"));
        }
    }

    /// <summary>The big file's short line <paramref name="line"/>: its number and a run of the same letter, 99 characters.</summary>
    private static string Short(int line) => line.ToString("D9", CultureInfo.InvariantCulture) + " " + new string('s', 89);

    /// <summary>A line of sixty characters that says its number.</summary>
    private static string Sixty(int line) => $"line {line:D2} " + new string('l', 52);

    /// <summary>A result's line <paramref name="line"/> of the letter <paramref name="letter"/>: its number and a run of the letter, 99 characters.</summary>
    private static string Marked(char letter, int line) => line.ToString("D9", CultureInfo.InvariantCulture) + " " + new string(letter, 89);

    /// <summary>The result's lines <paramref name="first"/> on, <paramref name="count"/> of them, as a page joins them.</summary>
    private static string Joined(char letter, int first, int count) => string.Join('\n', Enumerable.Range(first, count).Select(line => Marked(letter, line)));

    /// <summary>The <paramref name="length"/> characters of the long line's pattern from its character <paramref name="start"/>: the alphabet over and over.</summary>
    private static string Patterned(long start, int length)
        => string.Create(length, start, (span, from) =>
        {
            for (var at = 0; at < span.Length; at++)
            {
                span[at] = (char)('a' + ((from + at) % 26));
            }
        });
}
