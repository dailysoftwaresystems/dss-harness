using DssHarness.Core.Output;
using DssHarness.Core.Platform;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// Copies one file from one tree into another and proves the copy: written beside its destination, read back and
/// compared with what was read from the source, and only then put in place in one step. A reader of the destination
/// never sees a file half written, and a copy that came out other than its source is never left in place. Bytes in
/// hand, which no file holds yet, are written the same way.
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

        RequireMeasured(source, read, expected);

        return await PlaceAsync(
                fileSystem,
                destination,
                read,
                temporary =>
                {
                    fileSystem.CopyFile(source, temporary, overwrite: true);
                    return Task.CompletedTask;
                },
                copied => $"'{source}' was copied to '{destination}' as {ReportText.Counted(copied.Length, "byte")} with SHA-256 {copied.Content}, where it holds "
                    + $"{read.Length} with {read.Content}; the copy was not put in place.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the bytes <paramref name="source"/> holds to <paramref name="destination"/> anew, making its directory, as a
    /// file that can be run or one that cannot, and returns what it holds: read whole, required to be what was measured,
    /// and written as bytes in hand are (<see cref="WriteAsync"/>). Never copied, so the file written carries none of the
    /// source's attributes: one made from a read-only file can be replaced, and taken back where it failed.
    /// </summary>
    /// <param name="fileSystem">Reads and writes the files.</param>
    /// <param name="permissions">Gives the file the bit asked for, where the platform keeps one.</param>
    /// <param name="source">The file whose bytes are written.</param>
    /// <param name="destination">Where they go; a file there is replaced.</param>
    /// <param name="expected">The SHA-256 the source held when it was measured; a source that holds anything else now is not copied.</param>
    /// <param name="executable">Whether the file written is one that can be run.</param>
    /// <param name="cancellationToken">Stops it before the file is put in place.</param>
    /// <exception cref="IOException">
    /// The source is not what was measured, or what was written did not read back as the source did; nothing was put in place.
    /// </exception>
    public static async Task<FileContent> RewriteAsync(
        IFileSystem fileSystem,
        IFilePermissions permissions,
        string source,
        string destination,
        string expected,
        bool executable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);

        var contents = await fileSystem.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false);

        RequireMeasured(source, FileContent.Of(contents), expected);

        return await WriteAsync(fileSystem, permissions, contents, destination, executable, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="contents"/>, bytes no file holds yet, to <paramref name="destination"/>, making its
    /// directory, as a file that can be run or one that cannot, and returns what it holds.
    /// </summary>
    /// <param name="fileSystem">Reads and writes the files.</param>
    /// <param name="permissions">Gives the file the bit asked for, where the platform keeps one.</param>
    /// <param name="contents">The bytes to write.</param>
    /// <param name="destination">Where they go; a file there is replaced, and its own bit is not what the new file gets.</param>
    /// <param name="executable">Whether the file written is one that can be run.</param>
    /// <param name="cancellationToken">Stops it before the file is put in place.</param>
    /// <exception cref="IOException">What was written did not read back as the bytes given; nothing was put in place.</exception>
    public static async Task<FileContent> WriteAsync(
        IFileSystem fileSystem,
        IFilePermissions permissions,
        byte[] contents,
        string destination,
        bool executable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var given = FileContent.Of(contents);

        return await PlaceAsync(
                fileSystem,
                destination,
                given,
                async temporary =>
                {
                    await fileSystem.WriteAllBytesAtomicAsync(temporary, contents, cancellationToken).ConfigureAwait(false);

                    // The bit asked for, and never the one of the file replaced: what is written may have been made
                    // runnable, or no longer so, by the change that made its bytes.
                    permissions.SetExecutable(temporary, executable);
                },
                written => $"'{destination}' was written as {ReportText.Counted(written.Length, "byte")} with SHA-256 {written.Content}, where it was given "
                    + $"{given.Length} with {given.Content}; what was written was not put in place.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="CopyAsync"/> where either tree is a place an isolated process writes: the source read once, relative to
    /// its directory held from <paramref name="sourceRoot"/>, and the copy made, read back and put in place relative to its
    /// directory held from <paramref name="destinationRoot"/>, each with no link on the way (<see cref="UnfollowedFiles"/>) -
    /// so a link put on either's way, or swapped in while the copy runs, reaches nothing. What is written is what was read
    /// and hashed, its mode kept on Linux and macOS.
    /// </summary>
    /// <param name="fileSystem">Holds the files' directories.</param>
    /// <param name="sourceRoot">The root of the tree the file is read from.</param>
    /// <param name="source">The file to copy.</param>
    /// <param name="destinationRoot">The root of the tree it is written in.</param>
    /// <param name="destination">Where it goes; a file there is replaced, a link there replaced as itself.</param>
    /// <param name="expected">The SHA-256 the source held when it was measured; a source that holds anything else now is not copied.</param>
    /// <param name="cancellationToken">Stops it before the copy is put in place.</param>
    /// <exception cref="LinkOnTheWayException">A link stands on either's way, or at the source.</exception>
    /// <exception cref="IOException">
    /// The source is not what was measured, or the copy did not read back as the source did; nothing was put in place.
    /// </exception>
    public static async Task<FileContent> CopyUnfollowedAsync(
        IFileSystem fileSystem,
        string sourceRoot,
        string source,
        string destinationRoot,
        string destination,
        string? expected = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var read = UnfollowedFiles.OpenRead(fileSystem, sourceRoot, source);

        await using (read.ConfigureAwait(false))
        {
            FileContent copied = default;

            UnfollowedFiles.Written(
                fileSystem,
                destinationRoot,
                destination,
                write =>
                {
                    copied = Hashed(read, write, cancellationToken);
                    RequireMeasured(source, copied, expected);
                },
                UnfollowedFiles.ModeOf(read),
                back =>
                {
                    var made = Hashed(back, Stream.Null, cancellationToken);

                    if (made != copied)
                    {
                        throw new IOException($"'{source}' was copied to '{destination}' as {ReportText.Counted(made.Length, "byte")} with SHA-256 {made.Content}, where it holds "
                            + $"{copied.Length} with {copied.Content}; the copy was not put in place.");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                });

            return copied;
        }
    }

    /// <summary>
    /// <see cref="WriteAsync"/> where the tree written is a place an isolated process writes: made, read back and put in
    /// place relative to its directory held from <paramref name="root"/> with no link on the way
    /// (<see cref="UnfollowedFiles"/>), as a file that can be run or one that cannot.
    /// </summary>
    /// <param name="fileSystem">Holds the file's directory.</param>
    /// <param name="contents">The bytes to write.</param>
    /// <param name="root">The root of the tree it is written in.</param>
    /// <param name="destination">Where they go; a file there is replaced, a link there replaced as itself.</param>
    /// <param name="executable">Whether the file written is one that can be run, where the platform keeps that.</param>
    /// <param name="cancellationToken">Stops it before the file is put in place.</param>
    /// <exception cref="LinkOnTheWayException">A link stands on the way.</exception>
    /// <exception cref="IOException">What was written did not read back as the bytes given; nothing was put in place.</exception>
    public static Task<FileContent> WriteUnfollowedAsync(
        IFileSystem fileSystem,
        byte[] contents,
        string root,
        string destination,
        bool executable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var given = FileContent.Of(contents);

        UnfollowedFiles.Written(
            fileSystem,
            root,
            destination,
            write => write.Write(contents),
            OperatingSystem.IsWindows() ? null : RunnableAs(executable),
            back =>
            {
                var written = Hashed(back, Stream.Null, cancellationToken);

                if (written != given)
                {
                    throw new IOException($"'{destination}' was written as {ReportText.Counted(written.Length, "byte")} with SHA-256 {written.Content}, where it was given "
                        + $"{given.Length} with {given.Content}; what was written was not put in place.");
                }

                cancellationToken.ThrowIfCancellationRequested();
            });

        return Task.FromResult(given);
    }

    /// <summary>The mode a file made as one that can be run, or one that cannot, is given (<see cref="PosixFilePermissions.Executable"/>).</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static Func<UnixFileMode, UnixFileMode> RunnableAs(bool executable) => made => PosixFilePermissions.Executable(made, executable);

    /// <summary>What <paramref name="from"/> holds, hashed as it is copied to <paramref name="to"/>, a block at a time.</summary>
    private static FileContent Hashed(Stream from, Stream to, CancellationToken cancellationToken)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int read;

        while ((read = from.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            to.Write(buffer, 0, read);
            length += read;
        }

        return new FileContent(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// Stops a copy of <paramref name="source"/> where what was read of it is not what was measured, named by
    /// <paramref name="expected"/>: what would be written is not what was weighed.
    /// </summary>
    private static void RequireMeasured(string source, FileContent read, string? expected)
    {
        if (expected is not null && read.Content != expected)
        {
            throw new IOException($"'{source}' holds other content now than when it was measured - SHA-256 {read.Content}, where it was {expected} - so it was not copied.");
        }
    }

    /// <summary>
    /// Puts a file in place at <paramref name="destination"/>, proved first: made beside it by <paramref name="make"/>,
    /// read back, required to hold <paramref name="expected"/>, and only then renamed over whatever is there. What was
    /// made is taken back where any of that fails.
    /// </summary>
    /// <param name="fileSystem">Reads and writes the files.</param>
    /// <param name="destination">Where the file goes; its directory is made.</param>
    /// <param name="expected">What the file must hold to be put in place.</param>
    /// <param name="make">Makes the file at the temporary path it is given, beside the destination.</param>
    /// <param name="mismatch">What to say where the file made holds something else, given what it holds.</param>
    /// <param name="cancellationToken">Stops it before the file is put in place.</param>
    private static async Task<FileContent> PlaceAsync(
        IFileSystem fileSystem,
        string destination,
        FileContent expected,
        Func<string, Task> make,
        Func<FileContent, string> mismatch,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));

        if (!string.IsNullOrEmpty(directory))
        {
            fileSystem.CreateDirectory(directory);
        }

        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await make(temporary).ConfigureAwait(false);

            var made = await FileContentHash.OfAsync(fileSystem, temporary, cancellationToken).ConfigureAwait(false);

            if (made != expected)
            {
                throw new IOException(mismatch(made));
            }

            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.ReplaceFile(temporary, destination);
            return expected;
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
