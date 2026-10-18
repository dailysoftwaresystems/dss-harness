using System.Text;

namespace DssHarness.Core.Agents;

/// <summary>
/// One file of lines, appended: a line is written whole and flushed before the next is taken, so a file whose writer
/// was killed ends at its last whole line - and one opened again begins there, whatever a torn write left.
/// </summary>
/// <remarks>
/// The seam both records of a step hang off: the stored stream's events and a stored conversation's turns are the
/// same kind of thing, lines that only grow and must never tear.
/// </remarks>
internal sealed class AppendLog : IDisposable
{
    private readonly FileStream _file;

    private AppendLog(FileStream file) => _file = file;

    /// <summary>Opens <paramref name="path"/> for appending, taking back any torn line a killed writer left.</summary>
    /// <param name="path">The log's own path, its directory already there.</param>
    /// <returns>The log, at the end of its last whole line.</returns>
    public static AppendLog Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // A torn line cannot end in the newline that a whole one ends in: the write of a line writes its newline last,
        // and is flushed before the next line is taken. Whatever follows the last newline was never whole.
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);

        try
        {
            if (file.Length > 0)
            {
                var whole = ReadToLastNewline(file);

                if (whole < file.Length)
                {
                    file.SetLength(whole);
                }
            }

            file.Position = file.Length;
            return new AppendLog(file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Appends one line, whole, and flushes it before any other line may be written.</summary>
    /// <param name="line">The line, without its newline.</param>
    public void Append(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        _file.Write(bytes, 0, bytes.Length);
        _file.Flush(flushToDisk: true);
    }

    /// <summary>Closes the log. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _file.Dispose();

    /// <summary>How many bytes of <paramref name="file"/> end at its last whole line.</summary>
    /// <param name="file">The file, open for reading.</param>
    private static long ReadToLastNewline(FileStream file)
    {
        var bytes = new byte[file.Length];

        file.Position = 0;
        _ = file.Read(bytes, 0, bytes.Length);

        for (var index = bytes.Length - 1; index >= 0; index--)
        {
            if (bytes[index] == '\n')
            {
                return index + 1;
            }
        }

        return 0;
    }
}
