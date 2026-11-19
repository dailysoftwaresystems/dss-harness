using System.Text;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// A file of three gigabytes - more characters than one string of .NET holds - read whole by a reader of the scanning core
/// and by the C# reader: its text is read by position over the file a block at a time (<see cref="CodeText"/>), so what
/// reading it holds is what its reader keeps of it, never its text.
/// </summary>
/// <remarks>
/// Measured alone (<see cref="MemoryMeasured"/>): the heap a reading is held to is the process's. Each file is written to
/// the disk the suite's temporary files are on, and a test is skipped where that disk has no room for its file.
/// </remarks>
[Collection(MemoryMeasured.Name)]
public sealed class CodeGraphHugeFileTests
{
    /// <summary>How large a file is written: three gigabytes.</summary>
    private const long FileBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>The room a file is written with to spare on its disk.</summary>
    private const long Spare = 1L * 1024 * 1024 * 1024;

    /// <summary>The most characters one string of .NET holds: the runtime's own limit of a string's length.</summary>
    private const long LongestString = 0x3FFFFFDF;

    private static readonly IFileSystem Disk = new PhysicalFileSystem(DssHarness.Core.Platform.FilePermissionsFactory.Create());

    /// <summary>
    /// A C file of three gigabytes - a function, then lines of a million spaces, then another function: three billion
    /// characters - is read whole by the C reader, both functions found where they stand, in a heap that grows by what the
    /// reader keeps - a few blocks of the text, where its lines begin, its tokens - not a fiftieth of the file. Red with
    /// every block of the text decoded held (CodeText's HeldBlocks raised past the file's blocks): the heap holds the text,
    /// two bytes a character.
    /// </summary>
    [Fact]
    public async Task AThreeGigabyteCFile_IsReadByItsReader_InAHeapOfWhatTheReaderKeeps()
    {
        using var directory = new TempDirectory();
        var path = Write(directory, "huge.c", "int first(void)\n{\n    return 1;\n}\n", new string(' ', 1 << 20) + "\n", "int last(void)\n{\n    return 2;\n}\n");
        var (extract, length, lines, growth, where) = await ReadAsync(path, "huge.c", new CExtractor());

        Assert.True(length > int.MaxValue, $"the file holds {length:N0} characters, no more than an Int32 counts");
        Assert.True(growth <= FileBytes / 48, $"the heap grew by {growth:N0} bytes as a file of {FileBytes:N0} was read, past a forty-eighth of it: {where}");

        var first = Assert.Single(extract.Nodes, node => node.Name == "first");
        var last = Assert.Single(extract.Nodes, node => node.Name == "last");

        Assert.Equal((1, 4), (first.Start, first.End));
        Assert.Equal((lines - 3, lines), (last.Start, last.End));
        Assert.All(extract.Nodes, node => Assert.NotNull(node.Digest));
    }

    /// <summary>
    /// A C# file of three gigabytes - a class, then lines of ideographic spaces, then another class with its documentation:
    /// more characters than one string holds, fewer than the compiler's text counts - is read whole by the C# reader, both
    /// classes and their members found where they stand and the last one's summary read past every line before it, in a
    /// heap that grows by what the compiler's syntax tree keeps, never the text. Red with the reader's text given the
    /// compiler as one string (SourceText.From of the text's characters, as before the text was read by position): past
    /// the longest string, none is made.
    /// </summary>
    /// <remarks>
    /// The compiler's tree is whole by its design - every token and every run of trivia an entry, its text held where it
    /// is 42 characters or more, a shorter one shared - so its lines are written as runs of 41 spaces, which it shares, and
    /// what it keeps is two entries a line: about a fifth of the file's bytes, and the text itself as .NET holds it two
    /// thirds of them. The bound is half the file.
    /// </remarks>
    [Fact]
    public async Task AThreeGigabyteCSharpFile_IsReadByItsReader_InAHeapOfWhatTheCompilersTreeKeeps()
    {
        using var directory = new TempDirectory();
        const string First = "/// <summary>The first.</summary>\npublic class First\n{\n    public void Go()\n    {\n    }\n}\n";
        const string Last = "/// <summary>The last.</summary>\npublic class Last\n{\n    public void Stop()\n    {\n    }\n}\n";
        var path = Write(directory, "Huge.cs", First, new string('　', 41) + "\n", Last);
        var (extract, length, lines, growth, where) = await ReadAsync(path, "Huge.cs", new CSharpExtractor());

        Assert.True(length is > LongestString and <= int.MaxValue, $"the file holds {length:N0} characters, not more than a string holds and as many as the compiler counts");
        Assert.True(growth <= FileBytes / 2, $"the heap grew by {growth:N0} bytes as a file of {FileBytes:N0} was read, past half of it: {where}");
        Assert.Empty(extract.Dynamic);

        var first = Assert.Single(extract.Nodes, node => node.Name == "First");
        var last = Assert.Single(extract.Nodes, node => node.Name == "Last");

        Assert.Equal((2, 7), (first.Start, first.End));
        Assert.Equal((lines - 5, lines), (last.Start, last.End));
        Assert.Equal("The last.", last.Summary);
        Assert.Single(extract.Nodes, node => node.Name == "Go" && node.Parent == first.Id);
        Assert.Single(extract.Nodes, node => node.Name == "Stop" && node.Parent == last.Id);
        Assert.All(extract.Nodes, node => Assert.NotNull(node.Digest));
    }

    /// <summary>
    /// The file <paramref name="path"/> read as a sync reads it - decoded streamed into its text, extracted by
    /// <paramref name="extractor"/>, every node digested - under a watch of the heap: the extract, how many characters and
    /// lines the text holds, and the most the heap grew by, with where it held it.
    /// </summary>
    private static async Task<(FileExtract Extract, long Length, int Lines, long Growth, string Where)> ReadAsync(string path, string name, ICodeGraphExtractor extractor)
    {
        using var heap = new HeapWatch();
        using var decoded = await CodeFileText.DecodeAsync(Disk, path, new FileInfo(path).Length, TestContext.Current.CancellationToken);

        Assert.True(decoded.IsRead, $"the file is not read: {decoded.Why}");

        var extract = extractor.Extract(new CodeFileInput(name, decoded.Text)).WithDigests(decoded.Text);
        return (extract, decoded.Text.Length, decoded.Text.LineIndex.Count, heap.Growth, heap.Where);
    }

    /// <summary>
    /// Writes <paramref name="name"/> in <paramref name="directory"/>, in UTF-8: <paramref name="head"/>, then
    /// <paramref name="line"/> as many times as bring the file to <see cref="FileBytes"/>, then <paramref name="tail"/> -
    /// a megabyte at a time - and skips the test where the disk has no room for it with <see cref="Spare"/> to spare.
    /// </summary>
    private static string Write(TempDirectory directory, string name, string head, string line, string tail)
    {
        var room = Disk.SpaceAt(directory.Path).FreeBytes;
        Assert.SkipWhen(room < FileBytes + Spare, $"the disk of the suite's temporary files has {room:N0} bytes free, and a file of {FileBytes:N0} is written with {Spare:N0} to spare");

        var (headBytes, lineBytes, tailBytes) = (Encoding.UTF8.GetBytes(head), Encoding.UTF8.GetBytes(line), Encoding.UTF8.GetBytes(tail));
        var together = Math.Max(1, (1 << 20) / lineBytes.Length);
        var block = new byte[together * lineBytes.Length];

        for (var at = 0; at < together; at++)
        {
            lineBytes.CopyTo(block, at * lineBytes.Length);
        }

        var path = directory.Combine(name);

        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0))
        {
            file.Write(headBytes);

            for (var left = (FileBytes - headBytes.Length - tailBytes.Length) / lineBytes.Length; left > 0;)
            {
                var lines = (int)Math.Min(left, together);
                file.Write(block, 0, lines * lineBytes.Length);
                left -= lines;
            }

            file.Write(tailBytes);
        }

        return path;
    }
}
