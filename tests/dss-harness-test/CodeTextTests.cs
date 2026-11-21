using System.Text;
using DssHarness.Core.Codebase;

namespace DssHarness.Tests;

/// <summary>
/// A file's text as every reader of the graph reads it, by position (<see cref="CodeText"/>): read over its file a block
/// at a time, in blocks of any size and in every encoding a file is read in, it reads as its string does - its
/// characters, its runs, its lines, its views - and a block that no longer decodes as it did says the file changed.
/// </summary>
public sealed class CodeTextTests
{
    /// <summary>
    /// A run of every kind of line ending, an empty line, surrogate pairs - one at a line's end - and characters UTF-8
    /// writes in two bytes and in three: what a block's end must never part, nor read otherwise than its string.
    /// </summary>
    private const string Piece = "a\r\nb\nc\rd\U0001F600e f　g\r\n\r\n\n\rh\U0001F600\U0001F600\r";

    /// <summary>The text: the run many times over, so a text of a few characters a block has more blocks than it holds at once.</summary>
    private static readonly string Text = string.Concat(Enumerable.Repeat(Piece, 12));

    /// <summary>
    /// The text read over its file, in blocks of two characters - a surrogate pair's, the fewest - to a file's own, in each
    /// encoding a file is read in, reads as its string does: each character from the first and back from the last, every
    /// run, its lines and where each begins, a search across a block's end, a reader and its UTF-8. Red with a block that
    /// ends in a surrogate pair's first half kept whole (CodeText.OpenAsync's Close keeping every character it filled): the
    /// next block's bytes are counted from the wrong place.
    /// </summary>
    /// <param name="encoding">The encoding the file is written in.</param>
    /// <param name="blockCharacters">How many characters a block holds.</param>
    [Theory]
    [InlineData("utf-8", 2)]
    [InlineData("utf-8", 3)]
    [InlineData("utf-8", 7)]
    [InlineData("utf-8", CodeText.BlockCharacters)]
    [InlineData("utf-8 with its mark", 3)]
    [InlineData("utf-16le", 2)]
    [InlineData("utf-16le", 5)]
    [InlineData("utf-16be", 3)]
    public async Task AFilesText_ReadsAsItsString_InBlocksOfAnySize(string encoding, int blockCharacters)
    {
        var bytes = Bytes(encoding, Text);
        using var whole = CodeFileText.Decode(bytes);
        using var text = await Open(bytes, blockCharacters);

        Assert.Equal(Text, whole.Text!.Slice(0, whole.Text.Length));
        Assert.Equal(Text.Length, text.Length);
        Assert.Equal(Text, string.Concat(Enumerable.Range(0, Text.Length).Select(at => text[at])));
        Assert.Equal(Text.Reverse(), Enumerable.Range(0, Text.Length).Reverse().Select(at => text[at]));

        for (var start = 0; start <= Text.Length; start += 5)
        {
            for (var end = start; end <= Text.Length; end += 11)
            {
                Assert.Equal(Text[start..end], text.Slice(start, end));
                Assert.Equal(Text.AsSpan(start, end - start).IndexOf("\r\n", StringComparison.Ordinal) is var found and >= 0 ? start + found : -1, text.Find("\r\n", start, end));
                Assert.Equal(Text.AsSpan(start, end - start).IndexOfAny('　', '\uDE00') is var any and >= 0 ? start + any : -1, text.FindAny("　\uDE00", start, end));
            }

            Assert.Equal(Text.AsSpan(start).StartsWith("\U0001F600\U0001F600", StringComparison.Ordinal), text.At(start, "\U0001F600\U0001F600"));
        }

        Assert.Equal(CodeFileText.Lines(Text), text.Lines);
        Assert.Equal(CodeText.Of(Text).LineIndex.Starts, text.LineIndex.Starts);
        Assert.Equal(Text.Contains('\r', StringComparison.Ordinal), text.HoldsCarriageReturn);
        Assert.Equal(Text, await text.Reader().ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Text[100..300], await text.Reader(100, 300).ReadToEndAsync(TestContext.Current.CancellationToken));

        using var utf8 = new MemoryStream();
        await text.Utf8().CopyToAsync(utf8, TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetBytes(Text), utf8.ToArray());
    }

    /// <summary>
    /// A text read over its file, every line ending made one line feed, reads as its string does so: a CR LF one line
    /// feed across a block's end, a CR another, its lines the ones it has. Red with a CR LF read again as two line feeds
    /// (the LF after a CR not passed over in LineFedBlocks.Read).
    /// </summary>
    /// <param name="blockCharacters">How many characters a block holds.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(CodeText.BlockCharacters)]
    public async Task ItsLineFedText_ReadsEveryEndingAsOneLineFeed(int blockCharacters)
    {
        using var text = await Open(Bytes("utf-8", Text), blockCharacters);
        var fed = text.LineFed();
        var expected = Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

        Assert.Equal(expected.Length, fed.Length);
        Assert.Equal(expected.Reverse(), Enumerable.Range(0, expected.Length).Reverse().Select(at => fed[(long)at]));
        Assert.Equal(expected, fed.Slice(0, fed.Length));
        Assert.Equal(CodeText.Of(Text).LineFed().LineIndex.Starts, fed.LineIndex.Starts);
        Assert.Equal(text.LineIndex.Count, fed.LineIndex.Count);
        Assert.False(fed.HoldsCarriageReturn);
        Assert.Same(fed, text.LineFed());
    }

    /// <summary>
    /// A text made of a file's text's runs and strings between them reads as the same pieces of its string do - each
    /// piece in order, a run blanked and a character repeated among them - with its own lines. Red with every block of it
    /// read from its first piece on (ComposedBlocks.Read's search for the piece a block begins in left out).
    /// </summary>
    [Fact]
    public async Task AComposedText_ReadsItsPiecesInOrder_AsItsStringsDo()
    {
        using var text = await Open(Bytes("utf-8", Text), 3);
        var pieces = new CodeTextPieces().Run(3, 50).Write("written\r\n").Blank(50, 120).Write('x', 7).Run(120, 200).Run(200, 260).Pieces;
        var composed = text.Composed(pieces);
        var expected = CodeText.Of(Text).Composed(pieces);

        Assert.Equal(expected.Length, composed.Length);
        Assert.Equal(expected.Slice(0, expected.Length), composed.Slice(0, composed.Length));
        Assert.Equal(expected.Slice(0, expected.Length).Reverse(), Enumerable.Range(0, (int)expected.Length).Reverse().Select(at => composed[at]));
        Assert.Equal(expected.Lines, composed.Lines);
        Assert.Equal(expected.HoldsCarriageReturn, composed.HoldsCarriageReturn);
    }

    /// <summary>
    /// A block read again that ends before, or decodes otherwise than, the file's reading through found it - the file
    /// changed while it was read - says so, the file's name with it, rather than handing its reader the file's bytes. Red
    /// with what FileBlocks.Read reads again taken as read (its catch left out).
    /// </summary>
    [Fact]
    public async Task ABlockReadAgainThatNoLongerDecodes_SaysTheFileChanged()
    {
        var bytes = Bytes("utf-8", Text);

        foreach (var changed in new[] { bytes[..(bytes.Length / 2)], [.. bytes.Select(_ => (byte)0xFF)] })
        {
            var opened = 0;
            using var text = await CodeText.OpenAsync(() => new MemoryStream(opened++ == 0 ? bytes : changed, writable: false), "a.txt", 2, TestContext.Current.CancellationToken);

            Assert.Equal(Text[0], text[0]);
            var thrown = Assert.Throws<CodeTextChangedException>(() => text[text.Length - 1]);
            Assert.Contains("a.txt", thrown.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A text's UTF-8, read into room for fewer bytes than a character takes - one byte, three - is the text's bytes, each
    /// character's given across the reads: a read answers none at the text's end alone, as a stream's must. Red with a read
    /// of under four bytes' room answered none (the bytes CodeTextUtf8 sets aside left out): its reader takes the text to
    /// end at its first read so short.
    /// </summary>
    /// <param name="room">How many bytes each read has room for.</param>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task ItsUtf8_ReadIntoLittleRoom_IsTheTextsBytes(int room)
    {
        using var file = await Open(Bytes("utf-16le", Text), 3);
        using var utf8 = file.Utf8();
        var read = new List<byte>();
        var into = new byte[room];

        for (var count = await utf8.ReadAsync(into, TestContext.Current.CancellationToken); count > 0; count = await utf8.ReadAsync(into, TestContext.Current.CancellationToken))
        {
            read.AddRange(into.AsSpan(0, count).ToArray());
        }

        Assert.Equal(Encoding.UTF8.GetBytes(Text), read);
        using var after = new MemoryStream();
        await file.Utf8(100).CopyToAsync(after, TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetBytes(Text[100..]), after.ToArray());
    }

    /// <summary>A text of no characters, read over its file, is one empty line, as its string is.</summary>
    [Fact]
    public async Task AnEmptyFilesText_IsOneEmptyLine()
    {
        using var text = await Open([], 2);

        Assert.Equal(0, text.Length);
        Assert.Equal([string.Empty], text.Lines);
        Assert.Equal(-1, text.IndexOf('a'));
        Assert.Equal(string.Empty, text.LineFed().Slice(0, 0));
    }

    /// <summary>The text's bytes in <paramref name="encoding"/>, its mark first where it is one a file is read by.</summary>
    private static byte[] Bytes(string encoding, string text) => encoding switch
    {
        "utf-8" => Encoding.UTF8.GetBytes(text),
        "utf-8 with its mark" => [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)],
        "utf-16le" => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)],
        "utf-16be" => [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)],
        _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, "no encoding a file is read in"),
    };

    /// <summary>The text of a file of <paramref name="bytes"/>, read over it in blocks of <paramref name="blockCharacters"/>.</summary>
    private static Task<CodeText> Open(byte[] bytes, int blockCharacters)
        => CodeText.OpenAsync(() => new MemoryStream(bytes, writable: false), "a.txt", blockCharacters, TestContext.Current.CancellationToken);
}
