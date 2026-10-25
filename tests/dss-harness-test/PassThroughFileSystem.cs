using System.Text;
using DssHarness.Core.FileSystem;

namespace DssHarness.Tests;

/// <summary>
/// The real file system, member for member, for a double to change what it needs to: every member is
/// virtual, so a double says only what it does differently.
/// </summary>
/// <param name="inner">The file system every member passes through to.</param>
internal class PassThroughFileSystem(IFileSystem inner) : IFileSystem
{
    public virtual bool FileExists(string path) => inner.FileExists(path);

    public virtual bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public virtual FileStanding? StandingOf(string path) => inner.StandingOf(path);

    public virtual PathKind KindOf(string path) => inner.KindOf(path);

    public virtual string ResolveLinks(string path) => inner.ResolveLinks(path);

    public virtual void CreateDirectory(string path) => inner.CreateDirectory(path);

    public virtual void DeleteFile(string path) => inner.DeleteFile(path);

    public virtual string CopyToTemporaryFile(string path) => inner.CopyToTemporaryFile(path);

    public virtual void CopyFile(string source, string destination, bool overwrite = false)
        => inner.CopyFile(source, destination, overwrite);

    public virtual void ReplaceFile(string source, string destination) => inner.ReplaceFile(source, destination);

    public virtual void DeleteDirectory(string path) => inner.DeleteDirectory(path);

    public virtual void MoveDirectory(string source, string destination) => inner.MoveDirectory(source, destination);

    public virtual bool IsLink(string path) => inner.IsLink(path);

    public virtual long DirectorySize(string path) => inner.DirectorySize(path);

    public virtual DiskSpace SpaceAt(string path) => inner.SpaceAt(path);

    public virtual IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);

    public virtual IEnumerable<string> EnumerateReachableFiles(string path, Action<string, Exception> unread) => inner.EnumerateReachableFiles(path, unread);

    public virtual IEnumerable<WrittenFile> EnumerateWrittenFiles(string path) => inner.EnumerateWrittenFiles(path);

    public virtual IEnumerable<string> EnumerateDirectoryLinks(string path) => inner.EnumerateDirectoryLinks(path);

    public virtual IReadOnlyList<string> RemoveJunctions(string path) => inner.RemoveJunctions(path);

    public virtual IReadOnlyList<HeldEntry> FindHeld(string path, CancellationToken cancellationToken = default)
        => inner.FindHeld(path, cancellationToken);

    public virtual IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);

    public virtual Stream OpenRead(string path) => inner.OpenRead(path);

    public virtual IAppendFile OpenForAppend(string path) => inner.OpenForAppend(path);

    public virtual DateTime LastWriteTimeUtc(string path) => inner.LastWriteTimeUtc(path);

    public virtual DateTime CreationTimeUtc(string path) => inner.CreationTimeUtc(path);

    public virtual void SetLastWriteTimeUtc(string path, DateTime writtenUtc) => inner.SetLastWriteTimeUtc(path, writtenUtc);

    public virtual Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        => inner.WriteAllBytesAtomicAsync(path, contents, cancellationToken);

    public virtual void WriteAtomic(string path, Action<Stream> write) => inner.WriteAtomic(path, write);

    public virtual void ProtectSecretFile(string path) => inner.ProtectSecretFile(path);

    /// <summary><paramref name="text"/>, as a file holding it reads: for a double that says what a file holds.</summary>
    /// <param name="text">What the file holds.</param>
    protected static Stream Reading(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// What <paramref name="write"/> writes, as text, and a write of those same bytes again: for a double that decides by
    /// what a file is written holding, and still writes it.
    /// </summary>
    /// <param name="write">The write.</param>
    protected static (string Text, Action<Stream> Again) WrittenText(Action<Stream> write)
    {
        using var buffer = new MemoryStream();
        write(buffer);
        var bytes = buffer.ToArray();

        return (Encoding.UTF8.GetString(bytes), stream => stream.Write(bytes));
    }
}

/// <summary>
/// The real file system, save that <paramref name="path"/> is there and cannot be read, as a file another process holds
/// cannot - or, where <paramref name="denied"/>, as one this user may not read cannot, which is raised as no kind of
/// the other.
/// </summary>
/// <param name="inner">The file system every member passes through to.</param>
/// <param name="path">The file that cannot be read.</param>
/// <param name="denied">Whether it is this user's not to read, rather than another process's to hold.</param>
internal sealed class Held(IFileSystem inner, string path, bool denied = false) : PassThroughFileSystem(inner)
{
    /// <summary>Why it cannot be read, as whatever reads it says.</summary>
    public const string Why = "it is held by another process";

    public override Stream OpenRead(string read)
        => Path.GetFullPath(read) != Path.GetFullPath(path) ? base.OpenRead(read)
            : denied ? throw new UnauthorizedAccessException(Why)
            : throw new IOException(Why);

    public override IAppendFile OpenForAppend(string open)
        => Path.GetFullPath(open) != Path.GetFullPath(path) ? base.OpenForAppend(open)
            : denied ? throw new UnauthorizedAccessException(Why)
            : throw new IOException(Why);
}

/// <summary>
/// The real file system, whose atomic writes and appends fail while <see cref="Full"/> says so, as a full disk's do.
/// </summary>
/// <param name="inner">The file system every member passes through to.</param>
internal sealed class FullDiskFileSystem(IFileSystem inner) : PassThroughFileSystem(inner)
{
    /// <summary>What a full disk says to a write.</summary>
    public const string Why = "There is not enough space on the disk.";

    /// <summary>Whether a write fails.</summary>
    public bool Full { get; set; }

    public override void WriteAtomic(string path, Action<Stream> write)
    {
        if (Full)
        {
            throw new IOException(Why);
        }

        base.WriteAtomic(path, write);
    }

    public override Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        => Full ? Task.FromException(new IOException(Why)) : base.WriteAllBytesAtomicAsync(path, contents, cancellationToken);

    public override IAppendFile OpenForAppend(string path) => new Appending(base.OpenForAppend(path), this);

    /// <summary>The real file, whose appends fail while the disk is full.</summary>
    private sealed class Appending(IAppendFile file, FullDiskFileSystem disk) : IAppendFile
    {
        public long Length => file.Length;

        public void ReadExactlyAt(long position, Span<byte> buffer) => file.ReadExactlyAt(position, buffer);

        public void TruncateTo(long length) => file.TruncateTo(length);

        public void AppendFlushed(ReadOnlySpan<byte> bytes)
        {
            if (disk.Full)
            {
                throw new IOException(Why);
            }

            file.AppendFlushed(bytes);
        }

        public void Dispose() => file.Dispose();
    }
}

/// <summary>A disk that will not say what one directory holds: something holds it, or it is not this user's to read.</summary>
internal sealed class CannotList(IFileSystem inner, string directory, bool denied) : PassThroughFileSystem(inner)
{
    public override IEnumerable<string> EnumerateDirectories(string path)
        => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) ? base.EnumerateDirectories(path)
            : denied ? throw new UnauthorizedAccessException("the disk would not say.")
            : throw new IOException("the disk would not say.");
}

/// <summary>A disk that says what one directory holds once, and never again.</summary>
internal sealed class ListsOnce(IFileSystem inner, string directory) : PassThroughFileSystem(inner)
{
    private int _asked;

    public override IEnumerable<string> EnumerateDirectories(string path)
        => string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) && _asked++ > 0
            ? throw new IOException("the disk would not say.")
            : base.EnumerateDirectories(path);
}

/// <summary>A disk on which the command is interrupted the second time one directory is weighed.</summary>
internal sealed class InterruptsOnceWeighedAgain(IFileSystem inner, string directory, CancellationTokenSource interruption) : PassThroughFileSystem(inner)
{
    private int _weighed;

    public override long DirectorySize(string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) && ++_weighed == 2)
        {
            interruption.Cancel();
        }

        return base.DirectorySize(path);
    }
}
