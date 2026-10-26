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
/// A file of lines not opened for writing because another writer - in this process or another - holds its claim
/// (<see cref="IFileSystem.OpenForAppend"/>). Raised for that alone: a claim that could not be taken for any other reason
/// - a read-only file system, a disk's failure - is a plain <see cref="IOException"/>, so a caller that leaves the file to
/// the writer holding it never takes a failure of this machine's for one.
/// </summary>
public sealed class AppendFileHeldException : IOException
{
    /// <summary>The file's claim is held, as <paramref name="message"/> says.</summary>
    /// <param name="message">Which file, and which claim.</param>
    /// <param name="innerException">What the system raised opening the claim.</param>
    public AppendFileHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
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
    /// <exception cref="AppendFileHeldException">Another writer holds the file's claim.</exception>
    /// <exception cref="IOException">The claim or the file could not be opened for any other reason.</exception>
    internal static PhysicalAppendFile Open(string path, Func<string, FileStreamOptions, FileStream>? open = null)
    {
        var claimPath = path + ClaimSuffix;
        FileStream claim;

        try
        {
            claim = new FileStream(claimPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (HeldElsewhere(ex))
        {
            throw new AppendFileHeldException($"'{path}' is not opened for writing: another writer holds its claim '{claimPath}'. Nothing was read or written.", ex);
        }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException))
        {
            throw new IOException($"'{path}' is not opened for writing: its claim '{claimPath}' could not be taken ({ex.Message}). Nothing was read or written.", ex);
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
    /// Whether <paramref name="refusal"/>, raised opening the claim, says another open holds it - and nothing else: on
    /// Windows a sharing or a lock violation, the system refusing an open the claim's sharing nothing forbids; on Linux and
    /// macOS the exclusive lock .NET asks of an open sharing nothing refused (EWOULDBLOCK), which .NET raises with the
    /// system's own error number. A read-only file system, a disk's failure, any other refusal is no writer's hold.
    /// </summary>
    /// <param name="refusal">What opening the claim raised.</param>
    private static bool HeldElsewhere(IOException refusal)
        => OperatingSystem.IsWindows() ? WindowsHolds.HeldBy(refusal) : UnixFiles.WouldBlock(refusal.HResult);

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

