using System.Globalization;
using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

/// <summary>
/// The listing of a tree as it stands - its index, what of its working files is not the index's, and the blobs of those
/// files - none of which takes a lock.
/// </summary>
public sealed partial class GitClient
{
    /// <summary>How many paths one <c>hash-object</c> is given at a time, on its standard input, which no command line bounds.</summary>
    internal const int HashBatch = 200;

    /// <summary>The option that keeps git from taking the index's lock to refresh it on its own, which a person's git or a fold may need.</summary>
    private const string NoOptionalLocks = "--no-optional-locks";

    public Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, CancellationToken cancellationToken = default)
        => ReadIndexAsync(directory, null, cancellationToken);

    public async Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var entries = new List<GitIndexEntry>();

        foreach (var batch in CommandLineBatches(paths))
        {
            entries.AddRange(await ReadIndexAsync(directory, batch, cancellationToken).ConfigureAwait(false));
        }

        return entries;
    }

    public Task<IReadOnlyList<GitWorkingFile>> ListWorkingFilesAsync(string directory, CancellationToken cancellationToken = default)
        => ListWorkingAsync(directory, null, cancellationToken);

    public async Task<IReadOnlyList<GitWorkingFile>> ListWorkingFilesAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var working = new List<GitWorkingFile>();

        foreach (var batch in CommandLineBatches(paths))
        {
            working.AddRange(await ListWorkingAsync(directory, batch, cancellationToken).ConfigureAwait(false));
        }

        return working;
    }

    /// <summary>The arguments of a listing of every path, or of <paramref name="paths"/> alone, each spelt as it is.</summary>
    private static string[] Listing(string[] command, IReadOnlyList<string>? paths)
        => paths is null ? [NoOptionalLocks, .. command] : ["--literal-pathspecs", NoOptionalLocks, .. command, "--", .. paths];

    private async Task<IReadOnlyList<GitIndexEntry>> ReadIndexAsync(string directory, IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        // -v is what shows the marks that hide an edit from status, and -z keeps every path intact; read as bytes, so a
        // name that is not UTF-8 is told apart from another that decodes alike.
        var result = await RunForBytesAsync(directory, Listing(["ls-files", "--stage", "-v", "-z"], paths), cancellationToken).ConfigureAwait(false);

        Ensure(result, "read the index");

        var entries = new List<GitIndexEntry>();

        foreach (var record in Records(result.StandardOutput))
        {
            // The tag, mode, object and stage, separated by spaces, then a tab and the path; a conflicted path is listed
            // once for each of its stages.
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            string[] fields = tab < 0 ? [] : record[..tab].Split(' ');

            if (fields.Length != 4 || fields[0].Length != 1 || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var stage))
            {
                throw new HarnessException(HarnessExit.CommandFailed, $"git listed an index entry in a form this build cannot read: '{GitName.FromBytes(record).Quoted}'");
            }

            entries.Add(new GitIndexEntry(fields[0][0], fields[1], fields[2], stage, GitName.FromBytes(record[(tab + 1)..])));
        }

        return entries;
    }

    private async Task<IReadOnlyList<GitWorkingFile>> ListWorkingAsync(string directory, IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        var result = await RunForBytesAsync(
            directory,
            Listing(["status", "--porcelain=v2", "-z", "--untracked-files=all", "--no-renames"], paths),
            cancellationToken).ConfigureAwait(false);

        Ensure(result, "read the working tree's status");

        var working = new List<GitWorkingFile>();

        foreach (var record in Records(result.StandardOutput))
        {
            // An ordinary change names eight fields before its path, an unmerged one ten, an untracked one only its tag;
            // the working tree's mode is the sixth or the seventh, all zeros where the file is not there.
            var (fields, worktreeMode) = record[0] switch
            {
                '1' => (8, 5),
                'u' => (10, 6),
                '?' => (1, -1),
                _ => (0, -1),
            };

            if (fields == 0)
            {
                // Ignored files are not asked for, and a rename is not made with --no-renames: nothing else is listed.
                throw new HarnessException(HarnessExit.CommandFailed, $"git listed a change in a form this build cannot read: '{GitName.FromBytes(record).Quoted}'");
            }

            var parts = record.Split(' ', fields + 1);

            if (parts.Length != fields + 1)
            {
                throw new HarnessException(HarnessExit.CommandFailed, $"git listed a change in a form this build cannot read: '{GitName.FromBytes(record).Quoted}'");
            }

            // The second column of an ordinary change says whether the working file differs from what the index holds;
            // a change staged and nothing more leaves the index's own object the file's.
            var mode = worktreeMode < 0 ? null : parts[worktreeMode];
            var differs = record[0] != '1' || parts[1] is not [_, '.'];
            working.Add(new GitWorkingFile(GitName.FromBytes(parts[^1]), Gone: mode == "000000", Untracked: record[0] == '?', mode) { Differs = differs });
        }

        return working;
    }

    public async Task<IReadOnlyList<string?>> HashWorkingFilesAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var how = new Hashing(Written: false, Filtered: true, Told: [NoOptionalLocks]);
        const string What = "hash the working tree's files";
        var ids = new List<string?>(paths.Count);

        foreach (var batch in paths.Chunk(HashBatch))
        {
            try
            {
                ids.AddRange(await HashAsync(directory, batch, how, What, cancellationToken).ConfigureAwait(false));
            }
            catch (HarnessException) when (batch.Any(path => !_fileSystem.FileExists(Path.Combine(directory, path))))
            {
                // git stops at the first file it cannot open, and says nothing of the rest: each is asked on its own, and
                // one whose file is not there answers null where git cannot hash it.
                foreach (var path in batch)
                {
                    try
                    {
                        ids.Add((await HashAsync(directory, [path], how, What, cancellationToken).ConfigureAwait(false))[0]);
                    }
                    catch (HarnessException) when (!_fileSystem.FileExists(Path.Combine(directory, path)))
                    {
                        ids.Add(null);
                    }
                }
            }
        }

        return ids;
    }
}

/// <summary>
/// One path whose working file is not what the index holds - changed, gone, conflicted, or changed in the index alone - or
/// that the index does not hold.
/// </summary>
/// <param name="Name">The path, from the repository's root.</param>
/// <param name="Gone">Whether its working file is not there.</param>
/// <param name="Untracked">Whether the index does not hold it, and git does not ignore it.</param>
/// <param name="Mode">Its working file's octal mode, where git says it; <see langword="null"/> for an untracked file.</param>
public sealed record GitWorkingFile(GitName Name, bool Gone, bool Untracked, string? Mode)
{
    /// <summary>
    /// Whether its working file differs from what the index holds - an untracked or conflicted file always does; a change
    /// staged and nothing more does not - so its object is to be asked of git.
    /// </summary>
    public bool Differs { get; init; } = true;
}
