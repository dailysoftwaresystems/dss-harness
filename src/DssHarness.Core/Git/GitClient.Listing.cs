using System.Globalization;
using DssHarness.Core.Results;

namespace DssHarness.Core.Git;

/// <summary>The listing of a tree as it stands, for what reads every file of it: three calls, none of which takes a lock.</summary>
public sealed partial class GitClient
{
    /// <summary>How many paths one <c>hash-object</c> is given at a time.</summary>
    internal const int HashBatch = 200;

    /// <summary>The option that keeps git from taking the index's lock to refresh it on its own, which a person's git or a fold may need.</summary>
    private const string NoOptionalLocks = "--no-optional-locks";

    public Task<IReadOnlyList<GitListedFile>> ListIndexedFilesAsync(string directory, CancellationToken cancellationToken = default)
        => ListIndexedAsync(directory, null, cancellationToken);

    public async Task<IReadOnlyList<GitListedFile>> ListIndexedFilesAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var listed = new List<GitListedFile>();

        foreach (var batch in paths.Chunk(HashBatch))
        {
            listed.AddRange(await ListIndexedAsync(directory, batch, cancellationToken).ConfigureAwait(false));
        }

        return listed;
    }

    public Task<IReadOnlyList<GitWorkingFile>> ListWorkingFilesAsync(string directory, CancellationToken cancellationToken = default)
        => ListWorkingAsync(directory, null, cancellationToken);

    public async Task<IReadOnlyList<GitWorkingFile>> ListWorkingFilesAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var working = new List<GitWorkingFile>();

        foreach (var batch in paths.Chunk(HashBatch))
        {
            working.AddRange(await ListWorkingAsync(directory, batch, cancellationToken).ConfigureAwait(false));
        }

        return working;
    }

    /// <summary>The arguments of a listing of every path, or of <paramref name="paths"/> alone, each spelt as it is.</summary>
    private static string[] Listing(string[] command, IReadOnlyList<string>? paths)
        => paths is null ? [NoOptionalLocks, .. command] : ["--literal-pathspecs", NoOptionalLocks, .. command, "--", .. paths];

    private async Task<IReadOnlyList<GitListedFile>> ListIndexedAsync(string directory, IReadOnlyList<string>? paths, CancellationToken cancellationToken)
    {
        var result = await RunForBytesAsync(directory, Listing(["ls-files", "-s", "-t", "-z"], paths), cancellationToken).ConfigureAwait(false);

        Ensure(result, "list the index");

        var listed = new List<GitListedFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in Records(result.StandardOutput))
        {
            // The status tag - S where git keeps the path out of the checkout - then the mode, object and stage, separated
            // by spaces, a tab and the path; a conflicted path is listed once for each of its stages, and named here once.
            var tab = record.IndexOf('\t', StringComparison.Ordinal);
            string[] fields = tab < 0 ? [] : record[..tab].Split(' ');

            if (fields.Length != 4 || fields[0].Length != 1 || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw new HarnessException(HarnessExit.CommandFailed, $"git listed an index entry in a form this build cannot read: '{GitName.FromBytes(record).Quoted}'");
            }

            var name = GitName.FromBytes(record[(tab + 1)..]);

            // By its bytes: two names that are no UTF-8 can decode to one text.
            if (seen.Add(name.Quoted))
            {
                listed.Add(new GitListedFile(name, fields[1], fields[2]) { SkipWorktree = fields[0] == "S" });
            }
        }

        return listed;
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
            // An ordinary change names eight fields before its path, an unmerged one ten, an untracked one none; the
            // working tree's mode is the sixth or the seventh, all zeros where the file is not there.
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

    public async Task<IReadOnlyList<string>> HashWorkingFilesAsync(string directory, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var ids = new List<string>(paths.Count);

        foreach (var batch in paths.Chunk(HashBatch))
        {
            ids.AddRange(await HashAsync(directory, batch, new Hashing(Written: false, Filtered: true, Told: [NoOptionalLocks]), "hash the working tree's files", cancellationToken).ConfigureAwait(false));
        }

        return ids;
    }
}

/// <summary>One path the index lists.</summary>
/// <param name="Name">The path, from the repository's root.</param>
/// <param name="Mode">Its octal mode: <c>120000</c> for a link, <c>160000</c> for a submodule.</param>
/// <param name="ObjectId">The object the index holds for it: a blob, or a submodule's commit.</param>
public sealed record GitListedFile(GitName Name, string Mode, string ObjectId)
{
    /// <summary>Whether git records it as a link.</summary>
    public bool IsLink => Mode == "120000";

    /// <summary>Whether it is a submodule's commit.</summary>
    public bool IsSubmodule => Mode == "160000";

    /// <summary>Whether git keeps it out of the checkout - a sparse checkout's path, or one marked skip-worktree - and so never looks at its file.</summary>
    public bool SkipWorktree { get; init; }
}

/// <summary>One path whose working file is not what the index holds, or that the index does not hold.</summary>
/// <param name="Name">The path, from the repository's root.</param>
/// <param name="Gone">Whether its working file is not there.</param>
/// <param name="Untracked">Whether the index does not hold it, and git does not ignore it.</param>
/// <param name="Mode">Its working file's octal mode, where git says it; <see langword="null"/> for an untracked file.</param>
public sealed record GitWorkingFile(GitName Name, bool Gone, bool Untracked, string? Mode)
{
    /// <summary>Whether its working file differs from what the index holds - an untracked or conflicted file always does - so its object is to be asked of git.</summary>
    public bool Differs { get; init; } = true;
}
