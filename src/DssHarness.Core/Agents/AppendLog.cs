using System.Text;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Agents;

/// <summary>
/// One file of lines, appended: a line is written whole and on the disk before the next is taken, so a file whose writer
/// was killed ends at its last whole line - and one opened again begins there, whatever a torn write left.
/// </summary>
/// <remarks>
/// <para>
/// The seam both records of a step hang off: the stored stream's events and a stored conversation's turns are the
/// same kind of thing, lines that only grow and must never tear. The file is reached through
/// <see cref="IFileSystem.OpenForAppend"/>, and every append takes the one lock the log has, so two writers in this
/// process never interleave their lines.
/// </para>
/// <para>
/// A line holds no line break of its own: a record cut after a break inside it would be taken back as half a record that
/// looks whole, so such a line is refused before anything is written.
/// </para>
/// </remarks>
internal sealed class AppendLog : IDisposable
{
    /// <summary>How many bytes the opening reads at a time, from the end backwards, looking for the last whole line.</summary>
    internal const int ScanChunk = 4096;

    private readonly IAppendFile _file;
    private readonly Lock _gate = new();

    private AppendLog(IAppendFile file, string path)
    {
        _file = file;
        FilePath = path;
    }

    /// <summary>The log's own file.</summary>
    public string FilePath { get; }

    /// <summary>Opens <paramref name="path"/> for appending, taking back any torn line a killed writer left.</summary>
    /// <param name="fileSystem">The file system the log is kept on.</param>
    /// <param name="path">The log's own path, its directory already there.</param>
    /// <returns>The log, at the end of its last whole line.</returns>
    /// <exception cref="AppendFileHeldException">Another writer holds it, and nothing of it was read or cut.</exception>
    /// <exception cref="IOException">It could not be opened, or a torn line taken back, for any other reason.</exception>
    public static AppendLog Open(IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = fileSystem.OpenForAppend(path);

        try
        {
            // A torn line cannot end in the newline that a whole one ends in: the write of a line writes its newline last,
            // and is on the disk before the next line is taken. Whatever follows the last newline was never whole.
            var whole = EndOfLastWholeLine(file);

            if (whole < file.Length)
            {
                file.TruncateTo(whole);
            }

            return new AppendLog(file, path);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Appends one line, whole, and has it on the disk before any other line may be written.</summary>
    /// <param name="line">The line, without its newline, and holding none.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The line holds a line break of its own, which no line a writer here makes holds - a guard against its writer's
    /// defect, raised as one; nothing was written.
    /// </exception>
    public void Append(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.AsSpan().IndexOfAny('\n', '\r') is var at and >= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(line),
                $"A line of '{FilePath}' holds a line break at character {at}, and would be read back as two; nothing was written.");
        }

        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        lock (_gate)
        {
            _file.AppendFlushed(bytes);
        }
    }

    /// <summary>How many bytes the log holds, its last line whole.</summary>
    public long Length
    {
        get
        {
            lock (_gate)
            {
                return _file.Length;
            }
        }
    }

    /// <summary>Everything the log holds, every line whole, as UTF-8 text.</summary>
    public string ReadAll()
    {
        lock (_gate)
        {
            var bytes = new byte[checked((int)_file.Length)];
            _file.ReadExactlyAt(0, bytes);

            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>Empties the log, for a log kept to a most: what it held is gone, and the next line is its first.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _file.TruncateTo(0);
        }
    }

    /// <summary>Closes the log. Its last line is whole, and the next open begins after it.</summary>
    public void Dispose() => _file.Dispose();

    /// <summary>
    /// How many bytes of <paramref name="file"/> end at its last whole line, read backwards from its end a chunk at a
    /// time: a long record is not read whole to find the end of its last line, which is almost always near the end.
    /// </summary>
    /// <param name="file">The file, open for its writer.</param>
    private static long EndOfLastWholeLine(IAppendFile file)
    {
        var buffer = new byte[ScanChunk];
        var end = file.Length;

        while (end > 0)
        {
            var size = (int)Math.Min(ScanChunk, end);
            var start = end - size;
            var chunk = buffer.AsSpan(0, size);

            file.ReadExactlyAt(start, chunk);

            if (chunk.LastIndexOf((byte)'\n') is var newline and >= 0)
            {
                return start + newline + 1;
            }

            end = start;
        }

        return 0;
    }
}
