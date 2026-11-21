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
    /// <summary>How many characters are read at a time: a block, whatever a line's length.</summary>
    private const int Block = 16 << 10;

    /// <summary>The lines of <paramref name="path"/>, in order, read as they are asked for: UTF-8, unless a byte order mark says otherwise.</summary>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The file.</param>
    /// <returns>Each line; a file that ends with a line feed has no empty line after it.</returns>
    public static IEnumerable<FileLine> Read(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        return Lines(fileSystem, path);
    }

    /// <summary>
    /// The first line of <paramref name="path"/>, whole at any length, as git reads the one line of a <c>.git</c> file or a
    /// <c>commondir</c>: read a block at a time, UTF-8 unless a byte order mark says otherwise, up to the first line feed,
    /// which is not part of it - nor is any carriage return that ends it, before the feed or before the file's end.
    /// </summary>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>
    /// The line; <see langword="null"/> where the file holds no character - empty, or a byte order mark alone - which is a
    /// file of no line, told apart from one whose first line is empty.
    /// </returns>
    public static async Task<string?> FirstLineWholeAsync(IFileSystem fileSystem, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var stream = fileSystem.OpenRead(path);

        await using (stream.ConfigureAwait(false))
        {
            return await FirstLineWholeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The first line of what <paramref name="stream"/> holds from where it stands, read as
    /// <see cref="FirstLineWholeAsync(IFileSystem, string, CancellationToken)"/> reads a file's: for a file its caller opens
    /// itself - one opened without following a link, as a worktree's <c>.git</c> file is.
    /// </summary>
    /// <param name="stream">The bytes. It is left open: its caller owns it.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The line; <see langword="null"/> where the stream holds no character.</returns>
    public static async Task<string?> FirstLineWholeAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = Reader(stream, leaveOpen: true);
        var line = new StringBuilder();
        var block = new char[Block];
        var any = false;
        int read;

        while ((read = await reader.ReadAsync(block.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            any = true;

            var feed = block.AsSpan(0, read).IndexOf('\n');

            if (feed >= 0)
            {
                line.Append(block, 0, feed);
                return Unended(line);
            }

            line.Append(block, 0, read);
        }

        return any ? Unended(line) : null;
    }

    private static IEnumerable<FileLine> Lines(IFileSystem fileSystem, string path)
    {
        using var reader = Reader(fileSystem.OpenRead(path), leaveOpen: false);
        var line = new StringBuilder();
        var block = new char[Block];
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

    /// <summary>How a file's text is read: UTF-8, unless a byte order mark says otherwise.</summary>
    private static StreamReader Reader(Stream stream, bool leaveOpen)
        => new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: leaveOpen);

    /// <summary>A line's text without the carriage returns that end it, as git takes them off.</summary>
    private static string Unended(StringBuilder line)
    {
        var end = line.Length;

        while (end > 0 && line[end - 1] == '\r')
        {
            end--;
        }

        return line.ToString(0, end);
    }
}
