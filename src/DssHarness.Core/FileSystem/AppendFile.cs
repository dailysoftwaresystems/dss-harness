namespace DssHarness.Core.FileSystem;

/// <summary>
/// One file of lines that only grows, open for its one writer (<see cref="IFileSystem.OpenForAppend"/>): what a writer
/// needs to take back a torn last line when it opens the file, and to add a line that is on the disk before the next is
/// taken. Readers stand beside it, sharing what it holds.
/// </summary>
public interface IAppendFile : IDisposable
{
    /// <summary>How many bytes the file holds now.</summary>
    long Length { get; }

    /// <summary>
    /// Fills <paramref name="buffer"/> with the bytes that begin at <paramref name="position"/>, every one of them: a read
    /// that comes back short is read on, never taken for the file's end.
    /// </summary>
    /// <param name="position">Where the bytes begin.</param>
    /// <param name="buffer">Where they go; its length is how many are read.</param>
    /// <exception cref="EndOfStreamException">The file ends before the buffer is filled.</exception>
    void ReadExactlyAt(long position, Span<byte> buffer);

    /// <summary>Cuts the file to its first <paramref name="length"/> bytes.</summary>
    /// <param name="length">How many bytes it keeps.</param>
    void TruncateTo(long length);

    /// <summary>Writes <paramref name="bytes"/> at the file's end, and returns once they are on the disk.</summary>
    /// <param name="bytes">What is appended.</param>
    void AppendFlushed(ReadOnlySpan<byte> bytes);
}

/// <summary>The real file behind <see cref="IAppendFile"/>: one stream, open to read and write, shared for reading only.</summary>
/// <param name="file">The stream, open to read and write.</param>
internal sealed class PhysicalAppendFile(FileStream file) : IAppendFile
{
    private readonly FileStream _file = file;

    public long Length => _file.Length;

    public void ReadExactlyAt(long position, Span<byte> buffer)
    {
        _file.Position = position;
        _file.ReadExactly(buffer);
    }

    public void TruncateTo(long length) => _file.SetLength(length);

    public void AppendFlushed(ReadOnlySpan<byte> bytes)
    {
        _file.Seek(0, SeekOrigin.End);
        _file.Write(bytes);
        _file.Flush(flushToDisk: true);
    }

    public void Dispose() => _file.Dispose();
}
