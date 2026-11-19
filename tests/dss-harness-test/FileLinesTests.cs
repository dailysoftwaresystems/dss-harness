using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>A file's first line is read whole at any length, a block at a time, ended as git ends the one line of a .git file.</summary>
public sealed class FileLinesTests
{
    /// <summary>The real file system: a first line is read as the harness reads one off a disk.</summary>
    private static readonly IFileSystem FileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

    /// <summary>
    /// A first line of 42,008 characters - a .git file's gitdir far past the 4,096 characters an old read took, its
    /// letters of more than one byte falling across the blocks it is read in - is read whole, and the line after it is not.
    /// </summary>
    [Fact]
    public async Task TheFirstLine_IsReadWholePastFourThousandCharacters()
    {
        using var directory = new TempDirectory();
        var line = "gitdir: " + string.Concat(Enumerable.Repeat("répertoire-très-long/", 2000));
        var file = directory.WriteFile(".git", line + "\r\nsecond line\n");

        Assert.Equal(42_008, line.Length);
        Assert.Equal(line, await FileLines.FirstLineWholeAsync(FileSystem, file, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The first line ends at the first line feed, which is not part of it, nor is any carriage return that ends it -
    /// before the feed or before the end - as git takes them off; a carriage return within the line stays.
    /// </summary>
    [Theory]
    [InlineData("x\r\n", "x")]
    [InlineData("x\n", "x")]
    [InlineData("x\r\r\n", "x")]
    [InlineData("x\r", "x")]
    [InlineData("x", "x")]
    [InlineData("\n", "")]
    [InlineData("\r\n", "")]
    [InlineData("x\ry\r\nz", "x\ry")]
    [InlineData("x\nrest\r\n", "x")]
    public async Task TheFirstLine_EndsAtItsLineFeed_WithoutTheCarriageReturnsThatEndIt(string text, string first)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));

        Assert.Equal(first, await FileLines.FirstLineWholeAsync(stream, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A file of no character - empty, or a byte order mark alone - has no first line, told apart from a file whose first
    /// line is empty.
    /// </summary>
    [Fact]
    public async Task AFileOfNoCharacter_HasNoFirstLine_AndAnEmptyFirstLineIsEmpty()
    {
        using var directory = new TempDirectory();
        var empty = directory.WriteFile("empty");
        var markAlone = directory.Combine("mark");
        var emptyLine = directory.WriteFile("line", "\n");

        await File.WriteAllBytesAsync(markAlone, Encoding.UTF8.GetPreamble(), TestContext.Current.CancellationToken);

        Assert.Null(await FileLines.FirstLineWholeAsync(FileSystem, empty, TestContext.Current.CancellationToken));
        Assert.Null(await FileLines.FirstLineWholeAsync(FileSystem, markAlone, TestContext.Current.CancellationToken));
        Assert.Equal(string.Empty, await FileLines.FirstLineWholeAsync(FileSystem, emptyLine, TestContext.Current.CancellationToken));
    }

    /// <summary>A byte order mark says how the file is written, and is no part of the line: UTF-16 is read as UTF-16, and UTF-8's mark is taken off.</summary>
    [Fact]
    public async Task AByteOrderMark_SaysHowTheLineIsWritten()
    {
        using var directory = new TempDirectory();
        var sixteen = directory.Combine("sixteen");
        var eight = directory.Combine("eight");

        await File.WriteAllBytesAsync(sixteen, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("gitdir: é\r\nnext")], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(eight, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("gitdir: é\n")], TestContext.Current.CancellationToken);

        Assert.Equal("gitdir: é", await FileLines.FirstLineWholeAsync(FileSystem, sixteen, TestContext.Current.CancellationToken));
        Assert.Equal("gitdir: é", await FileLines.FirstLineWholeAsync(FileSystem, eight, TestContext.Current.CancellationToken));
    }

    /// <summary>A stream's first line is read from where the stream stands, and the stream is left open: its caller owns it.</summary>
    [Fact]
    public async Task AStreamsFirstLine_IsReadFromWhereItStands_AndTheStreamIsLeftOpen()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("skip\nfirst\r\nsecond\n")) { Position = 5 };

        Assert.Equal("first", await FileLines.FirstLineWholeAsync(stream, TestContext.Current.CancellationToken));
        Assert.True(stream.CanRead);
    }
}
