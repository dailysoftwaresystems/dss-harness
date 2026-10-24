using System.Text;

namespace DssHarness.Core.FileSystem;

/// <summary>One line of a file: its text, without the line feed that ended it, and whether one did.</summary>
/// <param name="Text">The line, as it is between one line feed and the next - a carriage return before the feed kept.</param>
/// <param name="Ended">Whether a line feed ended it: the last line of a file being appended to may not be whole yet.</param>
public readonly record struct FileLine(string Text, bool Ended);

/// <summary>
/// The lines of a file, read through <see cref="IFileSystem.OpenRead"/> a block at a time - never the whole file at once,
/// as a log that grows with its run would be - each split at a line feed alone, as every log the harness keeps is
/// written.
/// </summary>
public static class FileLines
{
    /// <summary>The lines of <paramref name="path"/>, in order, read as they are asked for: UTF-8, unless a byte order mark says otherwise.</summary>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The file.</param>
    /// <returns>Each line; a file that ends with a line feed has no empty line after it.</returns>
    public static IEnumerable<FileLine> Read(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        return Lines(fileSystem, path);
    }

    private static IEnumerable<FileLine> Lines(IFileSystem fileSystem, string path)
    {
        using var reader = new StreamReader(fileSystem.OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var line = new StringBuilder();
        var block = new char[16 << 10];
        int read;

        while ((read = reader.Read(block, 0, block.Length)) > 0)
        {
            var rest = block.AsMemory(0, read);

            for (var feed = rest.Span.IndexOf('\n'); feed >= 0; feed = rest.Span.IndexOf('\n'))
            {
                line.Append(rest.Span[..feed]);
                yield return new FileLine(line.ToString(), Ended: true);
                line.Clear();
                rest = rest[(feed + 1)..];
            }

            line.Append(rest.Span);
        }

        if (line.Length > 0)
        {
            yield return new FileLine(line.ToString(), Ended: false);
        }
    }
}
