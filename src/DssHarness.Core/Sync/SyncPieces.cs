using DssHarness.Core.FileSystem;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Core.Sync;

/// <summary>
/// How a file crosses between two trees reached through transports a piece at a time: the one way a file larger than a
/// piece (<see cref="ISyncTransport.PieceBytes"/>) is carried, whichever way it goes, so no side ever holds it whole.
/// </summary>
/// <remarks>
/// <para>
/// Held whole inside one request, as every file once was, the largest that could cross was one whose base64 one string
/// holds, about 767 MiB, and a build's output that size was refused. Now each piece is a request of its own, checked as it
/// arrives against the hash the side that read it took of it (<see cref="SyncServe.RefuseAPieceChanged"/>), and kept aside
/// where the file lands until every piece has arrived (<see cref="ISyncTransport.WritePieceAsync"/>). The file is put in place
/// only then, and only where what its pieces hold together is what the whole file was read as - its length, and the hash
/// the side that read it took of all of it as the first piece was read (<see cref="ISyncTransport.PlacePiecesAsync"/>).
/// </para>
/// <para>
/// As a pack of history is sent (<see cref="ISyncTransport.SendHistoryAsync"/>): a piece follows exactly what arrived before
/// it, and the first begins the file again, so a carrying stopped part way is carried again from its start the next time,
/// clearing what the last left aside. A call over ssh that fails before its session begins is made again for that one piece.
/// </para>
/// </remarks>
internal static class SyncPieces
{
    /// <summary>
    /// One piece of a file of <paramref name="from"/>'s tree: read through no link where <paramref name="hidden"/> is null,
    /// and as a pull reads one otherwise (<see cref="ISyncTransport.PullFileAsync"/>).
    /// </summary>
    /// <param name="from">How the tree is reached.</param>
    /// <param name="root">The tree's root.</param>
    /// <param name="relativePath">The file, relative to the root.</param>
    /// <param name="offset">How many bytes of the file come before the piece.</param>
    /// <param name="count">The most bytes the piece holds.</param>
    /// <param name="hidden">The places a pull never reaches, or null for a read that follows no link.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    public static Task<SyncFilePiece> ReadAsync(
        ISyncTransport from,
        string root,
        string relativePath,
        long offset,
        int count,
        IReadOnlyList<IsolatedPath>? hidden,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);

        return hidden is null
            ? from.ReadFileAsync(root, relativePath, offset, count, cancellationToken)
            : from.PullFileAsync(root, relativePath, offset, count, hidden, cancellationToken);
    }

    /// <summary>
    /// Sends the file whose first piece <paramref name="first"/> is to <paramref name="to"/>, a piece at a time - each read
    /// by <paramref name="next"/> once the one before it is sent, and never kept past that - and puts it in place there once
    /// every piece has arrived. On any failure, what was sent is removed where it was kept aside, and the file at its path
    /// there is as it was.
    /// </summary>
    /// <param name="first">The file's first piece, which says what the whole of it holds.</param>
    /// <param name="next">Reads the piece that starts after the bytes given.</param>
    /// <param name="to">How the tree it is written to is reached.</param>
    /// <param name="toRoot">That tree's root.</param>
    /// <param name="relativePath">The file, relative to both roots.</param>
    /// <param name="changed">
    /// What is raised where the pieces read do not hold what the first said the whole file holds - another length, or bytes
    /// that hash otherwise together: the file changed while it was read.
    /// </param>
    /// <param name="warn">Told why what was sent could not be removed, where a failure left it and it could not.</param>
    /// <param name="cancellationToken">Stops the carrying between pieces.</param>
    /// <exception cref="ArgumentException"><paramref name="first"/> does not start the file.</exception>
    public static async Task SendAsync(
        SyncFilePiece first,
        Func<long, CancellationToken, Task<SyncFilePiece>> next,
        ISyncTransport to,
        string toRoot,
        string relativePath,
        Func<Exception> changed,
        Action<string> warn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(warn);

        var whole = first.Whole ?? throw new ArgumentException("The first piece of a file sent starts it, and says what all of it holds.", nameof(first));

        try
        {
            using var hash = FileContentHash.Running();
            var piece = first;
            var sent = 0L;

            while (true)
            {
                // A piece of a file of another length, or one holding nothing short of the end, was read of a file that
                // changed since its first piece was.
                if (piece.Length != whole.Length || (piece.Bytes.Length == 0 && sent < whole.Length))
                {
                    throw changed();
                }

                hash.Append(piece.Bytes);
                await to.WritePieceAsync(toRoot, relativePath, sent, piece.Bytes, cancellationToken).ConfigureAwait(false);
                sent += piece.Bytes.Length;

                if (sent >= whole.Length)
                {
                    break;
                }

                piece = await next(sent, cancellationToken).ConfigureAwait(false);
            }

            // Every piece was read as it was, and together they are not the file the first was read of: it changed between.
            if (!string.Equals(hash.Content(), whole.Content, StringComparison.Ordinal))
            {
                throw changed();
            }

            await to.PlacePiecesAsync(toRoot, relativePath, whole, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await DropAsync(to, toRoot, relativePath, warn).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The whole of one file of a tree, read a piece at a time into the bytes it holds: for a file whose reader holds it whole
    /// by its nature - a configuration it parses - and never for one that crosses on, which goes a piece at a time
    /// (<see cref="SendAsync"/>).
    /// </summary>
    /// <param name="from">How the tree is reached.</param>
    /// <param name="root">The tree's root.</param>
    /// <param name="relativePath">The file, relative to the root.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="HarnessException">
    /// As <see cref="ISyncTransport.ReadFileAsync"/>; or the file changed while it was read (<see cref="HarnessExit.CommandFailed"/>).
    /// </exception>
    public static async Task<byte[]> ReadWholeAsync(ISyncTransport from, string root, string relativePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);

        var first = await from.ReadFileAsync(root, relativePath, 0, from.PieceBytes, cancellationToken).ConfigureAwait(false);

        if (first.IsWholeFile)
        {
            return first.Bytes;
        }

        var whole = first.Whole ?? throw new HarnessException(HarnessExit.CommandFailed, $"{from.Host} did not say what '{relativePath}' holds as its first piece was read.");
        var bytes = new byte[whole.Length];
        using var hash = FileContentHash.Running();
        var piece = first;
        var read = 0L;

        while (true)
        {
            if (piece.Length != whole.Length || piece.Bytes.Length == 0)
            {
                throw ChangedWhileRead(from, root, relativePath);
            }

            piece.Bytes.CopyTo(bytes.AsSpan((int)read));
            hash.Append(piece.Bytes);
            read += piece.Bytes.Length;

            if (read >= whole.Length)
            {
                break;
            }

            piece = await from.ReadFileAsync(root, relativePath, read, from.PieceBytes, cancellationToken).ConfigureAwait(false);
        }

        return string.Equals(hash.Content(), whole.Content, StringComparison.Ordinal) ? bytes : throw ChangedWhileRead(from, root, relativePath);
    }

    /// <summary>What a whole read of a file that changed while it was read raises.</summary>
    private static HarnessException ChangedWhileRead(ISyncTransport from, string root, string relativePath)
        => new(HarnessExit.CommandFailed, $"'{relativePath}' changed in '{root}' on {from.Host} while it was read a piece at a time; read it again once it has settled.");

    /// <summary>
    /// Removes what a carrying that stopped left aside of the file where it was going: never cancelled, since it is the
    /// carrying's being stopped that leaves the pieces there. Where that fails too - the connection that stopped the carrying
    /// gone with it - the carrying's own failure is what is raised, and why its pieces stayed is told; the next carrying of the
    /// file clears them, its first piece beginning the file again, and removing the copy removes them with it.
    /// </summary>
    private static async Task DropAsync(ISyncTransport to, string toRoot, string relativePath, Action<string> warn)
    {
        try
        {
            await to.DropPiecesAsync(toRoot, relativePath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            warn(
                $"{to.Host}: the pieces of '{relativePath}' sent to '{toRoot}' before carrying it stopped could not be removed there: "
                + $"{ex.Message.TrimEnd('.')}. The next carrying of it clears them.");
        }
    }
}
