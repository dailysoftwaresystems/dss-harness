using System.Text;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// A text file read in the encoding it was found in and written back in it, and an edit that changes the lines it is
/// asked to and nothing else of the file: its encoding and mark, its line endings, and whether it ends with a newline.
/// </summary>
public sealed class EditedTextTests
{
    /// <summary>Each encoding a text file is read in is the one it is written back in, its mark kept or left out as found.</summary>
    [Fact]
    public void EachEncoding_IsWrittenBackAsItWasFound()
    {
        foreach (var (bytes, encoding) in new (byte[], TextEncoding)[]
        {
            (Encoding.UTF8.GetBytes("café\n"), TextEncoding.Utf8),
            ([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("café\n")], TextEncoding.Utf8WithMark),
            ([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("café\n")], TextEncoding.Utf16Little),
            ([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("café\n")], TextEncoding.Utf16Big),
        })
        {
            var reading = EncodedText.Read(bytes);

            Assert.True(reading.IsRead);
            Assert.Equal(encoding, reading.Text.Encoding);
            Assert.Equal("café\n", reading.Text.Text);
            Assert.Equal(bytes, reading.Text.Encode(reading.Text.Text));
            Assert.Equal(bytes, EditedText.Of(reading.Text).Bytes());
        }
    }

    /// <summary>A NUL among the first bytes is binary; bytes that form no UTF-8 are not read, and say why; a UTF-32 mark is no UTF-16.</summary>
    [Fact]
    public void BinaryAndUndecodable_AreNotRead_AndSayWhy()
    {
        var binary = EncodedText.Read([0x61, 0x00, 0x62]);
        Assert.False(binary.IsRead);
        Assert.True(binary.Binary);
        Assert.Equal(EncodedText.BinaryWhy, binary.Why);

        var latin = EncodedText.Read([0x63, 0x61, 0x66, 0xE9]);
        Assert.False(latin.IsRead);
        Assert.False(latin.Binary);
        Assert.Equal("it is not UTF-8: the byte at offset 3 (0xE9) does not form a UTF-8 character, and is not UTF-16 by its mark either", latin.Why);

        var broken = EncodedText.Read([0xFF, 0xFE, 0x00, 0xD8]);
        Assert.Equal(EncodedText.NotUtf16Why, broken.Why);

        Assert.True(EncodedText.Read([0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00]).Binary);
    }

    /// <summary>The graph reads a file by the same rule, its states named its own way.</summary>
    [Fact]
    public void TheGraph_ReadsByTheSameRule()
    {
        Assert.Equal(CodeFileStates.Binary, CodeFileText.Decode([0x61, 0x00]).State);
        Assert.Equal(CodeFileStates.Unreadable, CodeFileText.Decode([0xE9]).State);
        Assert.Equal("x", CodeFileText.Decode([0xFF, 0xFE, 0x78, 0x00]).Text);
    }

    /// <summary>Text a model gives with LF is written in a CR LF file's endings, the file's final newline kept.</summary>
    [Fact]
    public void AReplace_WritesInTheFilesEndings()
    {
        var edited = Edited("one\r\ntwo\r\nthree\r\n");

        Assert.Equal("\r\n", edited.Ending);
        Assert.Equal(1, edited.Count("one\ntwo"));

        edited.Replace("one\ntwo", "uno\ndos\ntres");

        Assert.Equal("uno\r\ndos\r\ntres\r\nthree\r\n", edited.Text);
    }

    /// <summary>A replacement with no line break of the text it replaces is still written in the file's endings.</summary>
    [Fact]
    public void AReplacementOfOneWord_WithLines_IsInTheFilesEndings()
    {
        var edited = Edited("a\r\nb\r\n");

        edited.Replace("b", "b\nc");

        Assert.Equal("a\r\nb\r\nc\r\n", edited.Text);
    }

    /// <summary>A file that mixes endings is matched and written as given.</summary>
    [Fact]
    public void AFileOfMixedEndings_IsEditedAsGiven()
    {
        var edited = Edited("a\r\nb\nc");

        Assert.Null(edited.Ending);
        Assert.Equal(0, edited.Count("a\nb"));
        Assert.Equal(1, edited.Count("a\r\nb"));

        edited.Replace("a\r\nb", "x\ny");

        Assert.Equal("x\ny\nc", edited.Text);
    }

    /// <summary>Text given in CR LF is matched in an LF file's endings, and its replacement written in them.</summary>
    [Fact]
    public void TextGivenInOtherEndings_IsMatchedInTheFiles()
    {
        var edited = Edited("a\nb\n");

        Assert.Equal(1, edited.Count("a\r\nb"));

        edited.Replace("a\r\nb", "x\r\ny");

        Assert.Equal("x\ny\n", edited.Text);
    }

    /// <summary>Lines inserted before a line end as the file's do; appended, the file ends with a newline or not as it did.</summary>
    [Fact]
    public void Inserting_KeepsTheFilesEndingsAndItsFinalNewline()
    {
        var before = Edited("a\r\nb\r\n");
        before.Insert(2, "x\ny");
        Assert.Equal("a\r\nx\r\ny\r\nb\r\n", before.Text);

        var appended = Edited("a\nb\n");
        appended.Insert(3, "c");
        Assert.Equal("a\nb\nc\n", appended.Text);

        var open = Edited("a\nb");
        open.Insert(3, "c");
        Assert.Equal("a\nb\nc", open.Text);

        var first = Edited("a\n");
        first.Insert(1, "z");
        Assert.Equal("z\na\n", first.Text);

        var empty = Edited(string.Empty);
        empty.Insert(1, "only");
        Assert.Equal("only", empty.Text);

        var emptyEnded = Edited(string.Empty);
        emptyEnded.Insert(1, "only\n");
        Assert.Equal("only\n", emptyEnded.Text);
    }

    /// <summary>A line past one after the last is refused.</summary>
    [Fact]
    public void ALinePastTheEnd_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Edited("a\n").Insert(3, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Edited("a\n").Insert(0, "x"));
    }

    /// <summary>A range of lines replaced: the last line given ends as the last replaced did, so a final newline, or none, stays.</summary>
    [Fact]
    public void ARangeReplaced_EndsAsTheLastLineReplacedDid()
    {
        var middle = Edited("a\nb\nc\nd\n");
        middle.ReplaceLines(2, 3, "X\nY\nZ");
        Assert.Equal("a\nX\nY\nZ\nd\n", middle.Text);

        var last = Edited("a\nb");
        last.ReplaceLines(2, 2, "c\n");
        Assert.Equal("a\nc", last.Text);

        var taken = Edited("a\nb\nc");
        taken.ReplaceLines(2, 3, string.Empty);
        Assert.Equal("a", taken.Text);

        var takenEnded = Edited("a\nb\nc\n");
        takenEnded.ReplaceLines(3, 3, string.Empty);
        Assert.Equal("a\nb\n", takenEnded.Text);
    }

    /// <summary>A line number here is the one the graph and a view count: CR LF, LF and a lone CR each end a line.</summary>
    [Fact]
    public void LinesAreCounted_AsTheGraphCountsThem()
    {
        foreach (var text in new[] { "a\nb", "a\r\nb\r\n", "a\rb\r", "a\n\nb\n", "x" })
        {
            Assert.Equal(CodeFileText.Lines(text).Count, Edited(text).LineCount);
        }
    }

    private static EditedText Edited(string text) => EditedText.Of(new EncodedText(text, TextEncoding.Utf8));
}
