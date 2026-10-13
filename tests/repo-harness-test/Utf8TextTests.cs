using System.Text;
using RepoHarness.Core.FileSystem;

namespace RepoHarness.Tests;

/// <summary>
/// The one strict reader of UTF-8: bytes that are UTF-8 are the text they spell and are written back as themselves, and
/// bytes that are not are refused, naming the first that forms no character where it stands in its file - never read as
/// U+FFFD, a character nobody wrote.
/// </summary>
public sealed class Utf8TextTests
{
    /// <summary>
    /// Bytes that are not UTF-8 are refused in one sentence, naming the first byte that forms no character and its
    /// offset in the file the bytes stand in.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x41, 0x20, 0xE9, 0x0A }, 0, "is not UTF-8: the byte at offset 2 (0xE9) does not form a UTF-8 character")]
    [InlineData(new byte[] { 0x41, 0xFF }, 3, "is not UTF-8: the byte at offset 4 (0xFF) does not form a UTF-8 character")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, 0, "is not UTF-8: the byte at offset 0 (0xFF) does not form a UTF-8 character")]
    [InlineData(new byte[] { 0x41, 0xC3 }, 0, "is not UTF-8: the byte at offset 1 (0xC3) does not form a UTF-8 character")]
    public void BytesThatAreNotUtf8_AreRefused_NamingTheFirstThatFormsNoCharacter_WhereItStandsInItsFile(byte[] bytes, int offset, string problem)
    {
        Assert.False(Utf8Text.TryDecode(bytes, out var text, out var why, offset));
        Assert.Null(text);
        Assert.Equal(problem, why);
    }

    /// <summary>
    /// UTF-8 is read as the text it spells - a byte order mark among it a character like any other, which whoever reads
    /// a file says what to make of - and written back as the same bytes; a character no UTF-8 can hold is never written.
    /// </summary>
    [Fact]
    public void Utf8_IsReadAsTheTextItSpells_AndWrittenBackAsTheSameBytes()
    {
        byte[] bytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("caf\u00E9 \u2014 \U0001F600\r\n")];

        Assert.True(Utf8Text.TryDecode(bytes, out var text, out var why));
        Assert.Null(why);
        Assert.Equal("\uFEFFcaf\u00E9 \u2014 \U0001F600\r\n", text);
        Assert.Equal(bytes, Utf8Text.Encode(text));
        Assert.True(Utf8Text.OpensWithByteOrderMark(bytes));
        Assert.False(Utf8Text.OpensWithByteOrderMark(bytes.AsSpan(3)));
        Assert.Throws<EncoderFallbackException>(() => Utf8Text.Encode("a\uD800b"));
    }
}
