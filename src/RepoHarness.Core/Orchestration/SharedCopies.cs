using RepoHarness.Core.FileSystem;

namespace RepoHarness.Core.Orchestration;

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
/// A copy is made from a file required to hold the digest it is named by, and proved (<see cref="VerifiedFileCopy"/>),
/// so none is ever kept that is not the bytes recorded; read back, it is hashed again and used only where it still
/// hashes to its name. The copies are pruned to what the seed names each time the seed is written, and removed when
/// their agent is deleted. One that cannot be kept, pruned or removed never fails the command that met it: the path is
/// recorded all the same, and why is said.
/// </remarks>
/// <param name="fileSystem">Reads and writes the copies.</param>
/// <param name="layout">The agent's orchestrator, which spells where they are kept.</param>
/// <param name="agent">The agent's name.</param>
internal sealed class SharedCopies(IFileSystem fileSystem, OrchestratorLayout layout, string agent)
{
    /// <summary>
    /// The most bytes of one file that are kept: a file above it is recorded as shared and not kept, since no merge is
    /// offered above that size either and a copy would serve nothing.
    /// </summary>
    public const long MostBytes = 16 * 1024 * 1024;

    /// <summary>The directory the copies are kept in.</summary>
    public string Directory { get; } = layout.SharedDirectory(agent);

    /// <summary>
    /// Keeps the bytes of the file at <paramref name="holding"/> under <paramref name="digest"/>, in place of any copy
    /// already kept under it; a file above <see cref="MostBytes"/> is not kept.
    /// </summary>
    /// <param name="digest">The SHA-256 the seed records, which <paramref name="holding"/> must hold.</param>
    /// <param name="holding">A file that holds those bytes: one of the two trees' copies.</param>
    /// <param name="cancellationToken">Stops it before the copy is put in place.</param>
    /// <returns>Why they could not be kept; null where they were, or where the file is above the bound.</returns>
    public async Task<string?> KeepAsync(string digest, string holding, CancellationToken cancellationToken)
    {
        try
        {
            if (LengthOf(holding) > MostBytes)
            {
                return null;
            }

            await VerifiedFileCopy.CopyAsync(fileSystem, holding, layout.SharedFile(agent, digest), digest, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message.TrimEnd('.');
        }
    }

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

        var left = new List<string>();

        try
        {
            if (!fileSystem.DirectoryExists(Directory))
            {
                return left;
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
                    left.Add($"a kept copy the seed no longer names, '{file}', could not be removed: {ex.Message.TrimEnd('.')}; the next write of the seed removes it");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            left.Add($"the copies kept in '{Directory}' could not be pruned to what the seed names: {ex.Message.TrimEnd('.')}; the next write of the seed prunes them");
        }

        return left;
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
    private long LengthOf(string path)
    {
        using var stream = fileSystem.OpenRead(path);
        return stream.Length;
    }
}
