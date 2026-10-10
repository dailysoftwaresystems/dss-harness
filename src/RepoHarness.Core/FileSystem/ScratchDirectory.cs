namespace RepoHarness.Core.FileSystem;

/// <summary>
/// A directory of one use's own under the temporary directory, for files written only to hand to a
/// program and read back, and removed once that use is done. Under a directory its user names
/// instead, where what it holds is large enough to belong on that directory's volume: a system may
/// keep its temporary directory in memory.
/// </summary>
/// <remarks>
/// Made and written by whoever uses it, which says what failing to - a temporary directory this user
/// cannot write, a disk that is full - means for that use, naming <see cref="Path"/>. Removed on
/// <see cref="Dispose"/>, where one that cannot be - a scanner holding a file in it - is warned about
/// and fails nothing: what it held was already read, and the use it served has its answer.
/// </remarks>
public sealed class ScratchDirectory : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly Action<string> _warn;

    /// <summary>Names one; nothing is made until its user makes it.</summary>
    /// <param name="fileSystem">What it is removed through.</param>
    /// <param name="purpose">A word for what it holds, in its name: <c>vcvars</c>, <c>ignore</c>.</param>
    /// <param name="warn">Says, for the command using it, that it could not be removed, and why.</param>
    /// <param name="under">The directory it is made in, or <see langword="null"/> for the temporary directory.</param>
    public ScratchDirectory(IFileSystem fileSystem, string purpose, Action<string> warn, string? under = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(warn);

        _fileSystem = fileSystem;
        _warn = warn;
        Path = System.IO.Path.Combine(under ?? System.IO.Path.GetTempPath(), $"{Prefix}{purpose}-{Guid.NewGuid():N}");
    }

    /// <summary>What every one's name starts with, before its purpose.</summary>
    public const string Prefix = "dssharness-";

    /// <summary>Where it is: a name no other use shares, under the temporary directory or the one its user named.</summary>
    public string Path { get; }

    public void Dispose()
    {
        try
        {
            _fileSystem.DeleteDirectory(Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _warn($"'{Path}' could not be removed: {exception.Message}");
        }
    }
}
