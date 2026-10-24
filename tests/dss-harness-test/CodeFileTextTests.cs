using System.Text;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>The core's rules for a file's bytes before any implementation reads it: binary, a Git LFS pointer, encodings, and lines.</summary>
public sealed class CodeFileTextTests
{
    /// <summary>A Git LFS pointer is binary - git keeps its file elsewhere - and a longer file that begins like one is a file of its own.</summary>
    [Fact]
    public void AGitLfsPointer_IsBinary_AndALongerFileThatBeginsLikeOne_IsText()
    {
        var pointer = "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('a', 64) + "\nsize 12345\n";

        Assert.Equal((null, CodeFileStates.Binary), Read(Encoding.UTF8.GetBytes(pointer)));
        Assert.Contains("Git LFS pointer", CodeFileText.Decode(Encoding.UTF8.GetBytes(pointer)).Why, StringComparison.Ordinal);

        var longer = pointer + new string('x', 1024);
        Assert.Equal((longer, null), Read(Encoding.UTF8.GetBytes(longer)));
    }

    /// <summary>UTF-16 is read by its mark, either way round, though it holds a NUL in every character of ASCII.</summary>
    [Fact]
    public void Utf16_IsReadByItsMark_EitherWayRound()
    {
        Assert.Equal(("a\nb", null), Read([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("a\nb")]));
        Assert.Equal(("a\nb", null), Read([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("a\nb")]));
    }

    /// <summary>A file that begins with UTF-16's mark and is not UTF-16 after it is unreadable, with why.</summary>
    [Fact]
    public void AFileWithUtf16sMark_ThatIsNotUtf16_IsUnreadable()
    {
        var decoded = CodeFileText.Decode([0xFF, 0xFE, 0x00, 0xD8, 0x41, 0x00]);

        Assert.Equal((null, CodeFileStates.Unreadable), (decoded.Text, decoded.State));
        Assert.Equal("it begins with UTF-16's mark and is not UTF-16 after it", decoded.Why);
    }

    /// <summary>UTF-32's mark begins as UTF-16's does and is no UTF-16: its NULs make the file binary.</summary>
    [Fact]
    public void Utf32sMark_IsNoUtf16_AndItsNulsMakeTheFileBinary()
    {
        Assert.Equal((null, CodeFileStates.Binary), Read([0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00]));
    }

    /// <summary>A NUL among the first bytes looked at makes a file binary; one past them does not.</summary>
    [Fact]
    public void ANulAmongTheFirstBytes_MakesAFileBinary_AndOnePastThemDoesNot()
    {
        var inside = Enumerable.Repeat((byte)'a', BinaryContent.Window + 10).ToArray();
        inside[BinaryContent.Window - 1] = 0;
        Assert.Equal((null, CodeFileStates.Binary), Read(inside));
        Assert.Equal("a NUL among its first bytes", CodeFileText.Decode(inside).Why);

        var past = Enumerable.Repeat((byte)'a', BinaryContent.Window + 10).ToArray();
        past[BinaryContent.Window] = 0;
        Assert.Equal(BinaryContent.Window + 10, CodeFileText.Decode(past).Text?.Length);
    }

    /// <summary>UTF-8 is read with its mark or without, the mark not part of the text; an empty file is an empty text.</summary>
    [Fact]
    public void Utf8_IsReadWithItsMarkOrWithout_AndAnEmptyFileIsAnEmptyText()
    {
        Assert.Equal(("été", null), Read(Encoding.UTF8.GetBytes("été")));
        Assert.Equal(("été", null), Read([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("été")]));
        Assert.Equal((string.Empty, null), Read([]));
    }

    /// <summary>Bytes that are no UTF-8 and carry no mark of UTF-16 are unreadable, with why - never read with a guess.</summary>
    [Fact]
    public void BytesThatAreNoUtf8_AreUnreadable_WithWhy()
    {
        var decoded = CodeFileText.Decode([0x61, 0xE9, 0x62]);

        Assert.Equal((null, CodeFileStates.Unreadable), (decoded.Text, decoded.State));
        Assert.EndsWith(", and is not UTF-16 by its mark either", decoded.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lines are cut at each CR LF, LF and CR; a final ending closes the last line rather than opening another; an empty
    /// text is one empty line.
    /// </summary>
    /// <param name="text">The text, its endings escaped.</param>
    /// <param name="expected">Its lines, joined by a bar.</param>
    [Theory]
    [InlineData("", "")]
    [InlineData("a", "a")]
    [InlineData("a\\n", "a")]
    [InlineData("a\\r\\nb\\nc\\rd", "a|b|c|d")]
    [InlineData("a\\n\\n", "a|")]
    [InlineData("\\n", "")]
    [InlineData("\\r\\n\\r\\n", "|")]
    [InlineData("a\\n\\rb", "a||b")]
    [InlineData("a\\r", "a")]
    public void Lines_AreCutAtEveryEnding_AndAFinalEndingOpensNoLine(string text, string expected)
    {
        var lines = CodeFileText.Lines(text.Replace("\\r", "\r", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal));

        Assert.Equal(expected.Split('|'), lines);
    }

    /// <summary>
    /// A position is on the line <see cref="CodeFileText.Lines"/> puts it on: an ending is its line's, a separator a parser
    /// may break at is inside one, and the text's end is on its last line.
    /// </summary>
    [Fact]
    public void APosition_IsOnTheLineLinesPutsItOn_ItsEndingAmongIt()
    {
        var text = $"a\r\nb\nc\rd{(char)0x2028}e{(char)0x85}f";
        var index = CodeFileText.LineIndex(text);

        Assert.Equal(CodeFileText.Lines(text).Count, index.Starts.Count);
        Assert.Equal([1, 1, 1, 2, 2, 3, 3, 4, 4, 4, 4, 4, 4], Enumerable.Range(0, text.Length + 1).Select(index.LineAt));
        Assert.Equal(1, CodeFileText.LineIndex(string.Empty).LineAt(0));
    }

    /// <summary>A probe is a decoded text's first characters, never cut inside a surrogate pair.</summary>
    [Fact]
    public void AProbe_IsTheTextsFirstCharacters_NeverInsideASurrogatePair()
    {
        Assert.Equal("short", CodeFileText.Probe("short"));
        Assert.Equal(CodeFileText.ProbeCharacters, CodeFileText.Probe(new string('a', CodeFileText.ProbeCharacters * 2)).Length);

        var split = new string('a', CodeFileText.ProbeCharacters - 1) + "😀" + "tail";
        var probe = CodeFileText.Probe(split);

        Assert.Equal(CodeFileText.ProbeCharacters - 1, probe.Length);
        Assert.False(char.IsHighSurrogate(probe[^1]));
    }

    private static (string? Text, string? State) Read(byte[] bytes)
    {
        var decoded = CodeFileText.Decode(bytes);
        return (decoded.Text, decoded.State);
    }
}
