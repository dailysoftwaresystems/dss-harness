using System.Text;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// A mutation's edit of its site: exactly one occurrence of the before-text replaced, overlapping occurrences counted,
/// every other byte as it was, and the texts given the site's line endings so one registry serves a checkout with either.
/// </summary>
public sealed class SiteEditTests
{
    /// <summary>
    /// A cited text is its file less a UTF-8 byte order mark and the one line ending an editor adds at its end - CRLF or
    /// LF - and nothing else: a second ending, or one in the middle, is the text's own.
    /// </summary>
    [Theory]
    [InlineData("return a < b;\n", "return a < b;")]
    [InlineData("return a < b;\r\n", "return a < b;")]
    [InlineData("﻿return a < b;\n", "return a < b;")]
    [InlineData("return a < b;\n\n", "return a < b;\n")]
    [InlineData("first\r\nsecond", "first\r\nsecond")]
    [InlineData("  spaced  ", "  spaced  ")]
    [InlineData("", "")]
    public void AText_IsItsFile_LessAByteOrderMarkAndOneEndingLineEnding(string file, string expected)
        => Assert.Equal(Encoding.UTF8.GetBytes(expected), SiteEdit.Text(Bytes(file)));

    /// <summary>
    /// A text is given the site's line endings: CRLF where every line of the site ends so, LF where none does; and kept
    /// as it is where the site ends its lines both ways, or has one line.
    /// </summary>
    [Theory]
    [InlineData("a\nb", "x\r\ny\r\n", "a\r\nb")]
    [InlineData("a\r\nb", "x\ny\n", "a\nb")]
    [InlineData("a\r\nb\nc", "x\r\ny\r\n", "a\r\nb\r\nc")]
    [InlineData("a\nb", "x\r\ny\n", "a\nb")]
    [InlineData("a\r\nb", "x\r\ny\n", "a\r\nb")]
    [InlineData("a\nb", "one line", "a\nb")]
    [InlineData("a\r\nb", "one line", "a\r\nb")]
    public void AText_TakesTheSitesLineEndings(string text, string site, string expected)
        => Assert.Equal(Bytes(expected), SiteEdit.Adapted(Bytes(text), Bytes(site)));

    /// <summary>Occurrences overlap, as the arm's declaration counts them; an empty text is never one to count.</summary>
    [Theory]
    [InlineData("aaa", "aa", 2)]
    [InlineData("abab", "ab", 2)]
    [InlineData("ababa", "aba", 2)]
    [InlineData("x < y", "<=", 0)]
    [InlineData("x <= y", "<=", 1)]
    public void Occurrences_AreCountedOverlapping(string site, string text, int expected)
    {
        Assert.Equal(expected, SiteEdit.Occurrences(Bytes(site), Bytes(text)));
        Assert.Throws<ArgumentException>(() => SiteEdit.Occurrences(Bytes(site), []));
    }

    /// <summary>
    /// A before-text that occurs exactly once is replaced, and every other byte is left as it was - bytes no text could
    /// spell, trailing spaces, a file ending with no line ending - never rewritten as text.
    /// </summary>
    [Fact]
    public void AnEdit_ReplacesTheOneOccurrence_AndNothingElse()
    {
        byte[] site = [0xFF, 0x00, .. Bytes("int f() { return a < b; }  \n// été"), 0xC3];
        byte[] expected = [0xFF, 0x00, .. Bytes("int f() { return a <= b; }  \n// été"), 0xC3];

        var edit = SiteEdit.Apply(site, Bytes("a < b"), Bytes("a <= b"));

        Assert.Equal(1, edit.Occurrences);
        Assert.Equal(expected, edit.Edited);
    }

    /// <summary>A before-text that is not there, or is there twice - overlapping included - edits nothing, and says how often it occurs.</summary>
    [Theory]
    [InlineData("return a > b;", "a < b", 0)]
    [InlineData("a < b; a < b;", "a < b", 2)]
    [InlineData("aaa", "aa", 2)]
    public void ABeforeTextNotThereExactlyOnce_EditsNothing(string site, string before, int occurrences)
    {
        var edit = SiteEdit.Apply(Bytes(site), Bytes(before), Bytes("x"));

        Assert.Equal(occurrences, edit.Occurrences);
        Assert.Null(edit.Edited);
    }

    /// <summary>
    /// Texts committed with LF endings edit a checkout whose lines end CRLF - the before-text found, and the after-text
    /// written - with CRLF endings, and texts with CRLF endings edit an LF checkout with LF ones.
    /// </summary>
    [Fact]
    public void TextsOfEitherEnding_EditASiteOfEither_WithTheSitesEndings()
    {
        var crlf = SiteEdit.Apply(Bytes("int f()\r\n{\r\n    return 1;\r\n}\r\n"), Bytes("{\n    return 1;"), Bytes("{\n    return 2;\n    // mutated"));
        var lf = SiteEdit.Apply(Bytes("int f()\n{\n    return 1;\n}\n"), Bytes("{\r\n    return 1;"), Bytes("{\r\n    return 2;"));

        Assert.Equal(Bytes("int f()\r\n{\r\n    return 2;\r\n    // mutated\r\n}\r\n"), crlf.Edited);
        Assert.Equal(Bytes("int f()\n{\n    return 2;\n}\n"), lf.Edited);
    }

    /// <summary>
    /// An edit says where it changes nothing of its site: an after-text that is the before-text - as its file holds it,
    /// or once each is given the site's line endings - puts the one occurrence back as it was. One that is not made, its
    /// before-text there no times or twice, is never said to: its count says what is wrong with it.
    /// </summary>
    [Theory]
    [InlineData("return a < b;\n", "a < b", "a <= b", false)]
    [InlineData("return a < b;\n", "a < b", "a < b", true)]
    [InlineData("x\r\na\r\nb\r\n", "a\nb", "a\r\nb", true)]
    [InlineData("x\na\nb\n", "a\r\nb", "a\nb", true)]
    [InlineData("return a > b;\n", "a < b", "a < b", false)]
    [InlineData("a < b; a < b;\n", "a < b", "a < b", false)]
    public void AnEdit_SaysWhereItChangesNothingOfItsSite(string site, string before, string after, bool changesNothing)
        => Assert.Equal(changesNothing, SiteEdit.Apply(Bytes(site), Bytes(before), Bytes(after)).ChangesNothing);

    /// <summary>
    /// An arm with several sites edits each alone: a text found in one site is counted there only, and editing one site
    /// leaves what another holds as it was.
    /// </summary>
    [Fact]
    public void EachOfSeveralSites_IsEditedAlone()
    {
        var header = Bytes("constexpr int depth = 4;\n");
        var source = Bytes("int charge(int n) { return n * depth; }\n");

        var headerEdit = SiteEdit.Apply(header, Bytes("depth = 4"), Bytes("depth = 5"));
        var sourceEdit = SiteEdit.Apply(source, Bytes("n * depth"), Bytes("n + depth"));
        var crossed = SiteEdit.Apply(source, Bytes("depth = 4"), Bytes("depth = 5"));

        Assert.Equal(Bytes("constexpr int depth = 5;\n"), headerEdit.Edited);
        Assert.Equal(Bytes("int charge(int n) { return n + depth; }\n"), sourceEdit.Edited);
        Assert.Equal((0, (byte[]?)null), (crossed.Occurrences, crossed.Edited));
        Assert.Equal(Bytes("constexpr int depth = 4;\n"), header);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
