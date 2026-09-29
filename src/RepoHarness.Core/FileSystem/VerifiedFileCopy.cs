namespace RepoHarness.Core.FileSystem;

/// <summary>
/// Copies one file from one tree into another and proves the copy: written beside its destination, read back and
/// compared with what was read from the source, and only then put in place in one step. A reader of the destination
/// never sees a file half written, and a copy that came out other than its source is never left in place.
/// </summary>
/// <remarks>
/// The copy is the platform's own, which on Linux and macOS keeps the file's mode - an executable script stays one -
/// where writing the bytes out again would drop it. What is compared is the content's SHA-256
/// (<see cref="FileContentHash"/>), the one identity every part of the harness compares files by. Between trees
/// reached through a transport, sync's own copy reads a file back the same way where it arrives.
/// </remarks>
public static class VerifiedFileCopy
{
    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>, making its directory, and returns what it holds.</summary>
    /// <param name="fileSystem">Reads and writes the files.</param>
    /// <param name="source">The file to copy.</param>
    /// <param name="destination">Where it goes; a file there is replaced.</param>
    /// <param name="expected">
    /// The SHA-256 the source held when it was measured; where given, a source that holds anything else now is not
    /// copied, since what would be written is not what was weighed.
    /// </param>
    /// <param name="cancellationToken">Stops it before the copy is put in place.</param>
    /// <exception cref="IOException">
    /// The source is not what was measured, or the copy did not read back as the source did; nothing was put in place.
    /// </exception>
    public static async Task<FileContent> CopyAsync(
        IFileSystem fileSystem,
        string source,
        string destination,
        string? expected = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var read = await FileContentHash.OfAsync(fileSystem, source, cancellationToken).ConfigureAwait(false);

        if (expected is not null && read.Content != expected)
        {
            throw new IOException($"'{source}' holds other content now than when it was measured - SHA-256 {read.Content}, where it was {expected} - so it was not copied.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));

        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.CreateDirectory(directory);
        }

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            fileSystem.CopyFile(source, temporary, overwrite: true);

            var copied = await FileContentHash.OfAsync(fileSystem, temporary, cancellationToken).ConfigureAwait(false);

            if (copied != read)
            {
                throw new IOException(
                    $"'{source}' was copied to '{destination}' as {copied.Length} byte(s) with SHA-256 {copied.Content}, where it holds "
                    + $"{read.Length} with {read.Content}; the copy was not put in place.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.ReplaceFile(temporary, destination);
            return read;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The copy that failed is taken back, and one that cannot be is named with why the copy failed, never in
            // place of it: a stray temporary beside the destination is litter, and the first failure is the one to act on.
            try
            {
                fileSystem.DeleteFile(temporary);
            }
            catch (Exception left) when (left is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{ex.Message.TrimEnd('.')}; and its temporary copy '{temporary}' could not be removed: {left.Message.TrimEnd('.')}", ex);
            }

            throw;
        }
    }
}
