using System.Text;
using RepoHarness.Core.Mutations;

namespace RepoHarness.Tests;

/// <summary>
/// The one reader of an arms registry's text: its lines, each with the ending it has, numbered as the parser numbers
/// them; what each line is; and the text written back as the bytes it was read from.
/// </summary>
public sealed class RegistryTextTests
{
    /// <summary>
    /// A line ends wherever <see cref="string.ReplaceLineEndings(string)"/> reads an ending, and nowhere else, so a
    /// line's number is the one the parser gives it and an editor shows - whichever ending each line has, a carriage
    /// return before a line feed being one ending and after it another line. The text after the last ending is a line,
    /// empty or not, and a byte order mark a decoded text still holds is its first line's own.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("A | a")]
    [InlineData("A | a\n")]
    [InlineData("# arms\r\nA | a\rC | a | x\n")]
    [InlineData("one\n\rtwo\r\n\nthree")]
    [InlineData("form\ffeed\x0085next\x2028line\x2029paragraph")]
    [InlineData("\n\n\r\n")]
    [InlineData("tab\tand\vvertical tab end no line\x00A0")]
    [InlineData("\xFEFF# arms\nA | a\n")]
    public void ARegistrysLines_AreNumberedAsTheParserNumbersThem_HoweverEachEnds(string text)
    {
        var expected = text.ReplaceLineEndings("\n").Split('\n');

        var read = RegistryText.Of(text);

        Assert.Equal(expected, read.Lines.Select(line => line.Text));
        Assert.Equal(expected, MutationRegistryParser.Lines(text));
        Assert.Equal(text, string.Concat(read.Lines.Select(line => line.Text + line.Ending)));
        Assert.Equal(string.Empty, read.Lines[^1].Ending);
        Assert.All(read.Lines.SkipLast(1), line => Assert.Contains(line.Ending, new[] { "\r\n", "\r", "\n", "\f", "\x0085", "\x2028", "\x2029" }));
    }

    /// <summary>Each line keeps the ending it has, so a text of mixed endings is read as it stands.</summary>
    [Fact]
    public void EachLine_KeepsItsOwnEnding()
    {
        var read = RegistryText.Of("# arms\r\nA | a\rC | a | x\nlast");

        Assert.Equal(
            [("# arms", "\r\n"), ("A | a", "\r"), ("C | a | x", "\n"), ("last", "")],
            read.Lines.Select(line => (line.Text, line.Ending)));
    }

    /// <summary>
    /// A text read from bytes is written back as those bytes: with its byte order mark, with each line's own ending -
    /// one kind or several - and with or without an ending on its last line.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("A | a | caf\x00E9 \x2014 \U0001F600\n", false)]
    [InlineData("# arms\r\nA | a\r\nC | a | x\r\n", false)]
    [InlineData("# arms\nA | a\nC | a | x", false)]
    [InlineData("# arms\r\nA | a\rC | a | x\n\x2028last\f", false)]
    [InlineData("", true)]
    [InlineData("# arms\r\nA | a\n", true)]
    [InlineData("\xFEFFa second mark is the first line's own\n", true)]
    public void ATextRead_IsWrittenBackByteForByte(string text, bool byteOrderMark)
    {
        byte[] bytes = [.. byteOrderMark ? Encoding.UTF8.Preamble : [], .. Encoding.UTF8.GetBytes(text)];

        Assert.True(RegistryText.TryRead(bytes, out var read, out var unread), unread);

        Assert.Equal(byteOrderMark, read.ByteOrderMark);
        Assert.Equal(text, read.Text);
        Assert.Equal(text.ReplaceLineEndings("\n").Split('\n'), read.Lines.Select(line => line.Text));
        Assert.Equal(bytes, read.ToBytes());
    }

    /// <summary>
    /// The text read from a file's bytes is the text a sweep reads of the same file: without the byte order mark the
    /// file opens with, and otherwise character for character.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATextReadFromBytes_IsTheTextReadOfTheSameFile(bool byteOrderMark)
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("arms.registry");
        byte[] bytes = [.. byteOrderMark ? Encoding.UTF8.Preamble : [], .. Encoding.UTF8.GetBytes("# arms \x00E9\r\nA | a\n")];
        File.WriteAllBytes(path, bytes);

        Assert.True(RegistryText.TryRead(bytes, out var read, out var unread), unread);

        Assert.Equal(new HarnessFactory().FileSystem.ReadAllText(path), read.Text);
        Assert.Equal(MutationRegistryParser.Lines(read.Text), read.Lines.Select(line => line.Text));
    }

    /// <summary>
    /// Bytes that are not UTF-8 are not read, and why names the first byte that forms no character by where it stands
    /// in the file, a byte order mark counted: a byte read leniently would be written back as another.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x41, 0x20, 0xE9, 0x0A }, "is not UTF-8: the byte at offset 2 (0xE9) does not form a UTF-8 character")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41, 0xFF }, "is not UTF-8: the byte at offset 4 (0xFF) does not form a UTF-8 character")]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x41, 0x00 }, "is not UTF-8: the byte at offset 0 (0xFF) does not form a UTF-8 character")]
    [InlineData(new byte[] { 0x41, 0xC3 }, "is not UTF-8: the byte at offset 1 (0xC3) does not form a UTF-8 character")]
    public void BytesThatAreNotUtf8_AreNotRead_AndWhyNamesTheByte(byte[] bytes, string why)
    {
        Assert.False(RegistryText.TryRead(bytes, out var read, out var unread));

        Assert.Null(read);
        Assert.Equal(why, unread);
    }

    /// <summary>
    /// A line is blank, a comment or a row by the parser's rule: trimmed, an empty one is blank and one whose first
    /// character is '#' is a comment, so a '#' further in is no comment.
    /// </summary>
    [Theory]
    [InlineData("", RegistryLineKind.Blank)]
    [InlineData("  \t ", RegistryLineKind.Blank)]
    [InlineData("# arms", RegistryLineKind.Comment)]
    [InlineData("   #A | a", RegistryLineKind.Comment)]
    [InlineData("A | a | why # not a comment", RegistryLineKind.Row)]
    [InlineData("no field at all", RegistryLineKind.Row)]
    [InlineData("| a", RegistryLineKind.Row)]
    public void ALine_IsBlankACommentOrARow_AsTheParserReadsIt(string text, RegistryLineKind kind)
    {
        var line = new RegistryLine(text);

        Assert.Equal(kind, line.Kind);
        Assert.Equal(kind == RegistryLineKind.Row, line.Row is not null);
    }

    /// <summary>
    /// A row gives its kind - the text before its first '|', trimmed - and its fields, the kind first: the line trimmed
    /// and cut at '|' into as many as are asked for and one more, each trimmed, the last taking the rest of the line
    /// as it stands, with every '|' in it. A row with fewer gives those it has.
    /// </summary>
    [Fact]
    public void ARow_GivesItsKindAndItsFieldsTrimmed_TheLastTakingTheRestOfTheLine()
    {
        var row = new RegistryLine("  C |  charge|Fixture.A  | why | with | more  ").Row!;

        Assert.Equal("C", row.Kind);
        Assert.Equal(["C", "charge", "Fixture.A", "why | with | more"], row.Fields(3));
        Assert.Equal(["C", "charge", "Fixture.A  | why | with | more"], row.Fields(2));
        Assert.Equal(["C", "charge", "Fixture.A", "why", "with", "more"], row.Fields(9));
        Assert.Equal("no field at all", new RegistryLine("no field at all").Row!.Kind);
        Assert.Equal(["no field at all"], new RegistryLine("no field at all").Row!.Fields(3));
        Assert.Equal(string.Empty, new RegistryLine(" | a").Row!.Kind);
    }

    /// <summary>
    /// A row belongs to the arm its second field names, whatever its kind - one this registry reads, one it once read,
    /// one it never did - as the row spells it. A row with no second field, or an empty one, names none, and neither
    /// does a line that is no row.
    /// </summary>
    [Theory]
    [InlineData("A | charge | src/charge.cpp | t/b | t/a | TEST-RED | fixture | fixture | 5 | t/d | why", "charge")]
    [InlineData("C|Charge|Fixture.A|why", "Charge")]
    [InlineData("T | charge | charge.cpp | why", "charge")]
    [InlineData("Z |  an arm of a kind nobody reads  ", "an arm of a kind nobody reads")]
    [InlineData("Z", null)]
    [InlineData("Z |   | x", null)]
    [InlineData("Z |", null)]
    [InlineData("# C | charge | commented out", null)]
    [InlineData("   ", null)]
    public void ARowOfAnyKind_BelongsToTheArmItsSecondFieldNames_AndOneWithNoneNamesNone(string text, string? arm)
    {
        Assert.Equal(arm, new RegistryLine(text).Arm);
    }

    /// <summary>
    /// A row's arm can be spelled another way where it stands, and nothing else of the line changes - not the blanks
    /// around it, and not the same letters further along the line; a line that names no arm stays as it is.
    /// </summary>
    [Theory]
    [InlineData("  C |  Charge  | Charge.Case | why Charge ", "  C |  charge  | Charge.Case | why Charge ")]
    [InlineData("A|Charge|src/Charge.cpp", "A|charge|src/Charge.cpp")]
    [InlineData("Charge | Charge", "Charge | charge")]
    [InlineData("# C | Charge | commented out", "# C | Charge | commented out")]
    [InlineData("Z |   | Charge", "Z |   | Charge")]
    [InlineData("", "")]
    public void ARowsArm_IsRespelledWhereItStands_AndNothingElseOfTheLine(string text, string respelled)
    {
        Assert.Equal(respelled, new RegistryLine(text).NamedAs("charge"));
    }

    /// <summary>
    /// Lines given their endings are the text they spell, one after another, behind a byte order mark where one is
    /// asked for - and read as any text is, so a line given no ending runs into the next.
    /// </summary>
    [Fact]
    public void LinesGivenTheirEndings_AreTheTextTheySpell()
    {
        var text = RegistryText.Of([new RegistryLine("# arms", "\r\n"), new RegistryLine("A | a", "\n"), new RegistryLine("C | a | x")], byteOrderMark: true);

        Assert.Equal("# arms\r\nA | a\nC | a | x", text.Text);
        Assert.Equal([("# arms", "\r\n"), ("A | a", "\n"), ("C | a | x", "")], text.Lines.Select(line => (line.Text, line.Ending)));
        Assert.Equal([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes("# arms\r\nA | a\nC | a | x")], text.ToBytes());

        Assert.False(RegistryText.Of([]).ByteOrderMark);
        Assert.Equal(string.Empty, Assert.Single(RegistryText.Of([]).Lines).Text);
        Assert.Equal(["one line"], RegistryText.Of([new RegistryLine("one "), new RegistryLine("line")]).Lines.Select(line => line.Text));
    }
}
