using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;

namespace DssHarness.Core.Orchestration;

/// <summary>The bytes kept for one digest a seed records.</summary>
/// <param name="File">Where they are kept.</param>
/// <param name="Holds">Whether the file still holds them: read again, it hashes to its name.</param>
internal sealed record SharedCopy(string File, bool Holds);

/// <summary>
/// The bytes of every file an agent shares with the main tree, kept beside its seed as plain files, each named by the
/// SHA-256 the seed already records for it: what both trees held at a shared path, which is what a three-way merge of
/// that path starts from. The seed is not changed by them - it names digests, and a digest names a file here - so a seed
/// written before any copy was kept reads as it always did, and has none.
/// </summary>
/// <remarks>
/// A copy is written from bytes required to hash to the digest it is named by, and proved (<see cref="VerifiedFileCopy"/>),
/// so none is ever kept that is not the bytes recorded; read back, it is hashed again and used only where it still
/// hashes to its name. It is a file of the agent's own, whatever the file it was kept from: never read-only, and never one
/// that can be run. A digest whose copy still holds its bytes is kept already, and is not written again. The copies are
/// pruned to what the seed names each time the seed is written, and removed when their agent is deleted. One that cannot
/// be kept, pruned or removed never fails the command that met it: the path is recorded all the same, and why is said.
/// </remarks>
/// <param name="fileSystem">Reads and writes the copies.</param>
/// <param name="permissions">Writes each copy as a file that is not run.</param>
/// <param name="layout">The agent's orchestrator, which spells where they are kept.</param>
/// <param name="agent">The agent's name.</param>
internal sealed class SharedCopies(IFileSystem fileSystem, IFilePermissions permissions, OrchestratorLayout layout, string agent)
{
    /// <summary>The directory the copies are kept in.</summary>
    public string Directory { get; } = layout.SharedDirectory(agent);

    /// <summary>
    /// Keeps the bytes of the file at <paramref name="holding"/> under <paramref name="digest"/>, where no copy kept under
    /// it holds them already; a file above <see cref="TwoChanges.MostBytes"/> is recorded as shared and not kept, since no
    /// merge is offered above that size either and a copy would serve nothing.
    /// </summary>
    /// <param name="digest">The SHA-256 the seed records, which <paramref name="holding"/> must hold.</param>
    /// <param name="holding">A file that holds those bytes: one of the two trees' copies.</param>
    /// <param name="cancellationToken">Stops it before the copy is put in place.</param>
    /// <returns>Why they could not be kept; null where they were, or are already, or where the file is above the bound.</returns>
    public async Task<string?> KeepAsync(string digest, string holding, CancellationToken cancellationToken)
    {
        try
        {
            if (await KeptAlreadyAsync(digest, cancellationToken).ConfigureAwait(false) || LengthOf(fileSystem, holding) > TwoChanges.MostBytes)
            {
                return null;
            }

            // Written anew, never copied: a copy carries the attributes of the file it was made from, and one kept from a
            // read-only file could not be replaced, nor taken back where it failed.
            await VerifiedFileCopy.RewriteAsync(fileSystem, permissions, holding, layout.SharedFile(agent, digest), digest, executable: false, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>
    /// Keeps <paramref name="contents"/>, bytes in hand that hash to <paramref name="digest"/>, under it, where no copy kept
    /// under it holds them already: what a search of the main tree's history found for a seed whose bytes no copy was kept
    /// of, so the search is made once.
    /// </summary>
    /// <param name="digest">The SHA-256 the seed records.</param>
    /// <param name="contents">Bytes proved to hash to it.</param>
    /// <param name="cancellationToken">Stops it before the copy is put in place.</param>
    /// <returns>Why they could not be kept; null where they were, or are already, or where they are not those bytes or above the bound.</returns>
    public async Task<string?> KeepAsync(string digest, byte[] contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contents);

        if (contents.LongLength > TwoChanges.MostBytes || FileContentHash.Of(contents) != digest)
        {
            return null;
        }

        try
        {
            if (!await KeptAlreadyAsync(digest, cancellationToken).ConfigureAwait(false))
            {
                await WriteAsync(digest, contents, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

    /// <summary>Whether the copy kept under <paramref name="digest"/> holds its bytes already: kept, and not written again.</summary>
    private async Task<bool> KeptAlreadyAsync(string digest, CancellationToken cancellationToken)
        => await FindAsync(digest, cancellationToken).ConfigureAwait(false) is { Holds: true };

    /// <summary>Writes <paramref name="contents"/> as the copy kept under <paramref name="digest"/>: a new file, proved, that is not run.</summary>
    private Task WriteAsync(string digest, byte[] contents, CancellationToken cancellationToken)
        => VerifiedFileCopy.WriteAsync(fileSystem, permissions, contents, layout.SharedFile(agent, digest), executable: false, cancellationToken);

    /// <summary>The copy kept under <paramref name="digest"/>, read again and hashed, or null where none was kept.</summary>
    /// <param name="digest">A SHA-256 the seed records.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="IOException">Whether one is kept, or what it holds, could not be read.</exception>
    public async Task<SharedCopy?> FindAsync(string digest, CancellationToken cancellationToken)
    {
        var file = layout.SharedFile(agent, digest);

        if (fileSystem.KindOf(file) != PathKind.File)
        {
            return null;
        }

        return new SharedCopy(file, (await FileContentHash.OfAsync(fileSystem, file, cancellationToken).ConfigureAwait(false)).Content == digest);
    }

    /// <summary>
    /// Removes every kept file <paramref name="seed"/> names no digest for: a copy of bytes no path shares any more, and
    /// what a copy that stopped part way left. Asked once the seed is written, so a copy is never gone that the seed on
    /// disk still names.
    /// </summary>
    /// <param name="seed">The seed as it was just written.</param>
    /// <returns>What could not be removed, each with why; the next write of the seed prunes it.</returns>
    public IReadOnlyList<string> Prune(SeedRecord seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        var left = new List<(string File, string Why)>();

        try
        {
            if (!fileSystem.DirectoryExists(Directory))
            {
                return [];
            }

            var named = seed.Paths.Values.ToHashSet(StringComparer.Ordinal);

            foreach (var file in fileSystem.EnumerateFiles(Directory, recursive: false).Where(file => !named.Contains(Path.GetFileName(file))).Order(StringComparer.Ordinal).ToList())
            {
                try
                {
                    fileSystem.DeleteFile(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left.Add((file, ex.Message.TrimEnd('.')));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"the copies kept in '{Directory}' could not be pruned to what the seed names: {ex.Message.TrimEnd('.')}; the next write of the seed prunes them"];
        }

        // One line however many: a directory nothing can be removed from would otherwise say so for every file in it.
        return ReportText.FailedInOneLine(
            [.. left.Select(copy => (Path.GetFileName(copy.File), copy.Why))],
            "kept copy",
            (name, why) => $"a kept copy the seed no longer names, '{Path.Combine(Directory, name)}', could not be removed: {why}; the next write of the seed removes it",
            (counted, names, why) => $"{counted} the seed no longer names, in '{Directory}', could not be removed - {names}: {why}; the next write of the seed removes them");
    }

    /// <summary>Removes every copy, with their directory: what deleting the agent does, once its record says it is deleted.</summary>
    /// <returns>Why they could not be removed, or null.</returns>
    public string? Remove()
    {
        try
        {
            fileSystem.DeleteDirectory(Directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the bytes of the files it shared are still kept in '{Directory}', which could not be removed: {ex.Message.TrimEnd('.')}; "
                + "deleting its orchestrator removes them with its directory";
        }
    }

    /// <summary>How many bytes the file at <paramref name="path"/> holds, asked without reading them.</summary>
    /// <param name="fileSystem">Opens the file.</param>
    /// <param name="path">The file.</param>
    internal static long LengthOf(IFileSystem fileSystem, string path)
    {
        using var stream = fileSystem.OpenRead(path);
        return stream.Length;
    }
}
