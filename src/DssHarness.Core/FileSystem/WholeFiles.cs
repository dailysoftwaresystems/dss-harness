using System.Text;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// A whole file read or written through the file system's two streams, <see cref="IFileSystem.OpenRead"/> and
/// <see cref="IFileSystem.WriteAtomic"/>: one way a file is read and one way it is written, so a double that changes how a
/// file reads or writes changes it for every caller, whatever form the caller takes it in.
/// </summary>
public static class WholeFiles
{
    /// <summary>UTF-8 without a byte order mark, as every file the harness writes is.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads a whole file as text: UTF-8, unless a byte order mark says otherwise.</summary>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The file.</param>
    public static string ReadAllText(this IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        using var reader = new StreamReader(fileSystem.OpenRead(path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Reads a whole file's bytes, as they are.</summary>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The file.</param>
    public static byte[] ReadAllBytes(this IFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        using var stream = fileSystem.OpenRead(path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Reads a whole file's bytes, as they are, stopping between one block and the next when asked.</summary>
    /// <param name="fileSystem">The file system it is read through.</param>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public static async Task<byte[]> ReadAllBytesAsync(this IFileSystem fileSystem, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        // Opened as every read of a tree's file is, sharing with the tools that hold one open: a sync that hashed a file
        // through OpenRead is never refused copying it for a narrower share.
        var stream = fileSystem.OpenRead(path);

        await using (stream.ConfigureAwait(false))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            return copy.ToArray();
        }
    }

    /// <summary>
    /// Writes UTF-8 text, without a byte order mark, whole and atomically (<see cref="IFileSystem.WriteAtomic"/>), encoded a
    /// little at a time: encoding the text at once takes a buffer of the most bytes it could encode to, three times its
    /// length, which the runtime's shared pool then keeps.
    /// </summary>
    /// <param name="fileSystem">The file system it is written through.</param>
    /// <param name="path">The file.</param>
    /// <param name="contents">The text.</param>
    public static void WriteAllTextAtomic(this IFileSystem fileSystem, string path, string contents)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(contents);

        fileSystem.WriteAtomic(path, stream =>
        {
            using var writer = new StreamWriter(stream, Utf8NoBom, leaveOpen: true);
            writer.Write(contents);
        });
    }
}
