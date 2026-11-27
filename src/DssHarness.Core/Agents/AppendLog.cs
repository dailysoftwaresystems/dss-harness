using System.Text;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Agents;

/// <summary>
/// One file of lines, appended: a line is written whole and on the disk before the next is taken (<see cref="Append"/>) -
/// or, for a writer that puts its lines on the disk a batch at a time, written whole and handed to the system before the
/// next is taken, and on the disk at the batch's <see cref="Flush"/> (<see cref="Write"/>) - so a file whose writer was
/// killed ends at its last whole line, and one opened again begins there, whatever a torn write left.
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
/// <para>
/// A log written a batch at a time (<see cref="Write"/>) loses, to a power loss, what its last batch had not put on the
/// disk - and that need not be its last line alone: writes never put on the disk reach it in any order, so the file can
/// come back with the batch's later lines and its size, and NUL bytes where an earlier page of the batch never landed,
/// a file system showing a byte it never wrote as NUL. Its lines hold none - each is JSON, which escapes every control
/// character, and a line holding one is refused as one holding a line break is - so the opening of such a log
/// (<c>batched</c>) takes back from its first NUL to its end, every line before the last flush being on the disk.
/// </para>
/// </remarks>
internal sealed class AppendLog : IDisposable
{
    /// <summary>How many bytes the opening reads at a time, from the end backwards, looking for the last whole line.</summary>
    internal const int ScanChunk = 4096;

    private readonly IAppendFile _file;
    private readonly Lock _gate = new();

    /// <summary>Whether a line was written since the last flush (<see cref="Write"/>), which the next flush puts on the disk.</summary>
    private bool _unflushed;

    private AppendLog(IAppendFile file, string path)
    {
        _file = file;
        FilePath = path;
    }

    /// <summary>The log's own file.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Opens <paramref name="path"/> for appending, taking back any torn line a killed writer left - and, for a log written a
    /// batch at a time, whatever a power loss left of the batch it cut off: from the log's first NUL to its end.
    /// </summary>
    /// <param name="fileSystem">The file system the log is kept on.</param>
    /// <param name="path">The log's own path, its directory already there.</param>
    /// <param name="batched">Whether the log's lines are put on the disk a batch at a time (<see cref="Write"/>), each JSON, so no line holds a NUL.</param>
    /// <returns>The log, at the end of its last whole line.</returns>
    /// <exception cref="AppendFileHeldException">Another writer holds it, and nothing of it was read or cut.</exception>
    /// <exception cref="IOException">It could not be opened, or a torn line taken back, for any other reason.</exception>
    public static AppendLog Open(IFileSystem fileSystem, string path, bool batched = false)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = fileSystem.OpenForAppend(path);

        try
        {
            // What a power loss left of a batch never put on the disk begins at its first NUL, which no line holds; every
            // line before the last flush is on the disk whole.
            var written = batched ? FirstNul(file) ?? file.Length : file.Length;

            // A torn line cannot end in the newline that a whole one ends in: the write of a line writes its newline last.
            // Whatever follows the last newline was never whole.
            var whole = EndOfLastWholeLine(file, written);

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
        var bytes = BytesOf(line);

        lock (_gate)
        {
            _file.AppendFlushed(bytes);
        }
    }

    /// <summary>
    /// Appends one line, whole, handed to the system before any other line may be written - a reader sees it, and an end of
    /// this process loses none of it - and on the disk at the next <see cref="Flush"/>, with every line written before it.
    /// </summary>
    /// <param name="line">The line, without its newline, and holding none.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// As <see cref="Append"/>, or the line holds a NUL, which no line of a log written a batch at a time holds; nothing was
    /// written.
    /// </exception>
    public void Write(string line)
    {
        var bytes = BytesOf(line);

        if (line.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(line),
                $"A line of '{FilePath}' holds a NUL, which its opening would take back as what a power loss left of an unwritten batch; nothing was written.");
        }

        lock (_gate)
        {
            _file.Append(bytes);
            _unflushed = true;
        }
    }

    /// <summary>
    /// Puts every line written since the last flush (<see cref="Write"/>) on the disk, at once; nothing where none was, so a
    /// flush asked with nothing written costs nothing.
    /// </summary>
    /// <exception cref="IOException">They could not be put on the disk; they are written still, and the next flush tries again.</exception>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_unflushed)
            {
                return;
            }

            _file.Flush();
            _unflushed = false;
        }
    }

    /// <summary>
    /// <paramref name="line"/> as the bytes its writing appends, its newline last - refused, with nothing written, where it
    /// holds a line break of its own.
    /// </summary>
    private byte[] BytesOf(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.AsSpan().IndexOfAny('\n', '\r') is var at and >= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(line),
                $"A line of '{FilePath}' holds a line break at character {at}, and would be read back as two; nothing was written.");
        }

        return Encoding.UTF8.GetBytes(line + "\n");
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

    /// <summary>
    /// Every line the log holds, whole, in its order, each without its newline: read a piece at a time, so a log of any
    /// length is read through and never held whole - what a writer that keeps its log compact reads it by.
    /// </summary>
    public IEnumerable<string> Lines()
    {
        var buffer = new byte[1 << 16];
        using var line = new MemoryStream();
        var at = 0L;

        while (true)
        {
            int read;

            lock (_gate)
            {
                var left = _file.Length - at;

                if (left <= 0)
                {
                    yield break;
                }

                read = (int)Math.Min(buffer.Length, left);
                _file.ReadExactlyAt(at, buffer.AsSpan(0, read));
            }

            at += read;
            var begun = 0;

            for (var end = 0; end < read; end++)
            {
                if (buffer[end] != (byte)'\n')
                {
                    continue;
                }

                line.Write(buffer, begun, end - begun);
                yield return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
                line.SetLength(0);
                begun = end + 1;
            }

            line.Write(buffer, begun, read - begun);
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

    /// <summary>
    /// Closes the log, every line written since the last flush put on the disk first where it can be. Its last line is
    /// whole, and the next open begins after it.
    /// </summary>
    /// <remarks>
    /// A close runs as its writer leaves by a failure too, so it never throws a flush's failure over the one leaving: what
    /// the flush could not put on the disk is the system's still - an end of this process loses none of it - and a writer
    /// that must hear of it flushes before it closes (<see cref="Flush"/>), as the run's records do.
    /// </remarks>
    public void Dispose()
    {
        try
        {
            Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Heard by the writer's own flush before the close, where it must be; never thrown over a failure in flight.
        }
        finally
        {
            _file.Dispose();
        }
    }

    /// <summary>
    /// Where <paramref name="file"/>'s first NUL is, read from its start a chunk at a time; null where it holds none.
    /// </summary>
    /// <param name="file">The file, open for its writer.</param>
    private static long? FirstNul(IAppendFile file)
    {
        var buffer = new byte[1 << 16];

        for (var at = 0L; at < file.Length;)
        {
            var chunk = buffer.AsSpan(0, (int)Math.Min(buffer.Length, file.Length - at));
            file.ReadExactlyAt(at, chunk);

            if (chunk.IndexOf((byte)0) is var nul and >= 0)
            {
                return at + nul;
            }

            at += chunk.Length;
        }

        return null;
    }

    /// <summary>
    /// How many bytes of <paramref name="file"/>'s first <paramref name="length"/> end at their last whole line, read
    /// backwards from there a chunk at a time: a long record is not read whole to find the end of its last line, which is
    /// almost always near the end.
    /// </summary>
    /// <param name="file">The file, open for its writer.</param>
    /// <param name="length">How much of it is read: its whole length, or up to its first NUL.</param>
    private static long EndOfLastWholeLine(IAppendFile file, long length)
    {
        var buffer = new byte[ScanChunk];
        var end = length;

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
