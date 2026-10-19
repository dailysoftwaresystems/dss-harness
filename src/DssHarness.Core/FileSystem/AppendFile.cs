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

    /// <summary>
    /// Writes <paramref name="bytes"/> at the file's end, and returns once they are on the disk. Where they could not be
    /// put there, what was written of them is taken back as far as the disk allows, so that bytes counted lost do not land
    /// later.
    /// </summary>
    /// <param name="bytes">What is appended.</param>
    /// <exception cref="IOException">The bytes could not be written or put on the disk.</exception>
    void AppendFlushed(ReadOnlySpan<byte> bytes);
}

/// <summary>
/// The real file behind <see cref="IAppendFile"/>: the writer's claim on a file beside it, held for as long as the writer
/// is open, and the file itself, open to read and write without a buffer of its own.
/// </summary>
/// <remarks>
/// <para>
/// One writer, on every system. Windows refuses a second writer of a file opened sharing reads only, but Linux and macOS
/// lock a file only by advice, and .NET asks them for a shared lock for any sharing but none - which a second writer is
/// granted as readily as the first. So the writer first claims <c>&lt;file&gt;.lock</c>, opened sharing nothing: an
/// exclusive lock of that open on Linux and macOS and a refusal of every other open on Windows, which a second writer -
/// in this process or another - is refused before it reads or cuts a byte of the file. Readers never open the claim, and
/// read the file beside its writer. The claim is left in place when the writer closes: removing it would let a writer
/// that opened it a moment before hold a lock on a file no later writer could see.
/// </para>
/// <para>
/// The file is opened without a buffer, so a write is the system's at once and a failure is raised by the write or the
/// flush that met it - never kept in a buffer to be written, and raised again, when the file is closed.
/// </para>
/// </remarks>
internal sealed class PhysicalAppendFile : IAppendFile
{
    /// <summary>What the writer's claim adds to the file's name.</summary>
    internal const string ClaimSuffix = ".lock";

    private readonly FileStream _claim;
    private readonly FileStream _file;

    private PhysicalAppendFile(FileStream claim, FileStream file)
    {
        _claim = claim;
        _file = file;
    }

    public long Length => _file.Length;

    /// <summary>Claims <paramref name="path"/> for its one writer, and opens it.</summary>
    /// <param name="path">The file, its directory already there.</param>
    /// <param name="open">Opens the file itself with the options given; the runtime's own unless a test says otherwise.</param>
    /// <exception cref="IOException">Another writer holds the file's claim, or the claim or the file could not be opened.</exception>
    internal static PhysicalAppendFile Open(string path, Func<string, FileStreamOptions, FileStream>? open = null)
    {
        var claimPath = path + ClaimSuffix;
        FileStream claim;

        try
        {
            claim = new FileStream(claimPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
        {
            throw new IOException(
                $"'{path}' is not opened for writing: another writer holds its claim '{claimPath}', or the claim could not be taken ({ex.Message}). Nothing was read or written.",
                ex);
        }

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.Read,
                BufferSize = 0,
            };

            return new PhysicalAppendFile(claim, (open ?? ((file, with) => new FileStream(file, with)))(path, options));
        }
        catch
        {
            claim.Dispose();
            throw;
        }
    }

    public void ReadExactlyAt(long position, Span<byte> buffer)
    {
        _file.Position = position;
        _file.ReadExactly(buffer);
    }

    public void TruncateTo(long length) => _file.SetLength(length);

    public void AppendFlushed(ReadOnlySpan<byte> bytes)
    {
        var end = _file.Seek(0, SeekOrigin.End);

        try
        {
            _file.Write(bytes);
            _file.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            TakeBack(end);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _file.Dispose();
        }
        finally
        {
            _claim.Dispose();
        }
    }

    /// <summary>
    /// Cuts the file back to <paramref name="end"/>, where an append failed: a line the disk did not take whole is not
    /// left to land later. Where the disk will not even do that, the next opening takes a torn line back all the same, so
    /// the failure of the append is the one raised.
    /// </summary>
    /// <param name="end">Where the file ended before the append.</param>
    private void TakeBack(long end)
    {
        try
        {
            _file.SetLength(end);
        }
        catch (IOException)
        {
            // The append's own failure is the one that matters, and is raised by the caller.
        }
    }
}

