using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>One file of a cached workflow directory: its path in the repository, and the git hash of its content.</summary>
/// <param name="Path">The file's path in the repository, forward slashes.</param>
/// <param name="Blob">The git hash of the file's content - the name its content is cached under.</param>
/// <param name="Mode">The file's git mode, as the repository's tree says it.</param>
public sealed record WorkflowCacheFile(string Path, string Blob, string Mode);

/// <summary>
/// One directory of one repository, at one commit, in the workflow cache: every file under it with the git hash of
/// each, beside when it was fetched, the ref it was asked for where one was, and when a run last used it.
/// </summary>
/// <param name="Host">The repository's host.</param>
/// <param name="Owner">The repository's owner.</param>
/// <param name="Repo">The repository's name.</param>
/// <param name="Commit">The commit the directory was fetched at.</param>
/// <param name="Path">The directory's path in the repository, forward slashes.</param>
/// <param name="AskedFor">The ref the fetch was asked for, where one was; a commit SHA names none.</param>
/// <param name="FetchedAt">When the directory was fetched.</param>
/// <param name="LastUsedAt">When a run last resolved a use to this entry.</param>
/// <param name="Files">Every file under the directory, with the git hash of each.</param>
public sealed record WorkflowCacheEntry(
    string Host,
    string Owner,
    string Repo,
    string Commit,
    string Path,
    DateTimeOffset FetchedAt,
    DateTimeOffset LastUsedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? AskedFor,
    IReadOnlyList<WorkflowCacheFile> Files)
{
    /// <summary>The repository the entry's directory is in, as a line about it says it.</summary>
    [JsonIgnore]
    public string Repository => $"{Host}/{Owner}/{Repo}";
}

/// <summary>
/// The hash cache of remote workflows, in this user's own directory for this tool: a directory's files by the git
/// hash of each, shared by whatever entries hold them, and the entries themselves - one for a host, a repository, a
/// commit and a path - read strictly, and each blob hashed again as it is read, so a cache file changed on disk is
/// refused and fetched again, never silently run.
/// </summary>
/// <param name="fileSystem">Reads and writes the cache.</param>
/// <param name="root">The cache's own directory, <c>workflow-cache</c> under this user's directory for this tool.</param>
public sealed class WorkflowCache(IFileSystem fileSystem, string root)
{
    /// <summary>The name of the cache's directory under this user's own directory for this tool.</summary>
    public const string DirectoryName = "workflow-cache";

    /// <summary>The most files one fetched directory may hold; a fetch past it is refused with the count.</summary>
    public const int MostFiles = 2_000;

    /// <summary>The most bytes one fetched directory may hold; a fetch past it is refused with the size.</summary>
    public const long MostBytes = 20 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = JsonConfigOptions.Default;

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly string _root = root;

    /// <summary>
    /// The entry for <paramref name="host"/>/<paramref name="owner"/>/<paramref name="repo"/> at
    /// <paramref name="commit"/>, for the directory <paramref name="path"/>, where the cache holds one; null where it
    /// holds none, or holds one that does not read.
    /// </summary>
    public WorkflowCacheEntry? Find(string host, string owner, string repo, string commit, string path)
    {
        var file = EntryFile(host, owner, repo, commit, path);

        try
        {
            if (!_fileSystem.FileExists(file) || _fileSystem.ReadAllText(file) is not { } text)
            {
                return null;
            }

            return JsonSerializer.Deserialize<WorkflowCacheEntry>(text, Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // An entry that does not read is no entry: the fetch is made again, and the file written over.
            return null;
        }
    }

    /// <summary>
    /// The entry a remote workflow's name - <c>remote/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;/&lt;commit&gt;/&lt;dir&gt;</c> - resolves to,
    /// where the cache holds one; null where the name is no remote workflow's, or the cache holds none.
    /// </summary>
    /// <param name="workflow">A remote workflow's name, as the run resolved it.</param>
    public WorkflowCacheEntry? FindNamed(string workflow)
        => WorkflowRemote.PartsOf(workflow) is { } parts ? Find(parts.Host, parts.Owner, parts.Repo, parts.Commit, parts.Directory) : null;

    /// <summary>
    /// The entry the cache holds for the same repository and path that <paramref name="askedFor"/> last resolved,
    /// where one is there; null where none is. What an offline <c>last-resolved</c> resolution reads.
    /// </summary>
    /// <param name="host">The repository's host.</param>
    /// <param name="owner">The repository's owner.</param>
    /// <param name="repo">The repository's name.</param>
    /// <param name="path">The directory's path in the repository.</param>
    /// <param name="askedFor">The ref that cannot be resolved.</param>
    public WorkflowCacheEntry? LastResolved(string host, string owner, string repo, string path, string askedFor)
    {
        foreach (var (entry, _) in List())
        {
            if (entry.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                && entry.Owner == owner && entry.Repo == repo && entry.Path == path
                && entry.AskedFor == askedFor)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Writes an entry and every file of it, blobs first, each private to this user.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="contents">Each file's content, by its path as the entry names it.</param>
    public void Write(WorkflowCacheEntry entry, IReadOnlyDictionary<string, string> contents)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(contents);

        foreach (var file in entry.Files)
        {
            var blob = BlobFile(file.Blob);

            try
            {
                _fileSystem.CreateDirectory(Path.GetDirectoryName(blob)!);
                _fileSystem.WriteAllTextAtomic(blob, contents[file.Path]);
                _fileSystem.ProtectSecretFile(blob);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"the workflow cache could not write '{blob}': {exception.Message.TrimEnd('.')}. Nothing of the fetch was cached.");
            }
        }

        WriteEntry(entry);
    }

    /// <summary>Says the entry was used now, for the pruning that removes what no run used for its days.</summary>
    /// <param name="entry">The entry a run resolved a use to.</param>
    public void Touch(WorkflowCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        WriteEntry(entry with { LastUsedAt = DateTimeOffset.UtcNow });
    }

    /// <summary>Every entry the cache holds, and the bytes its blobs take, for <c>workflow-cache list</c>.</summary>
    public IReadOnlyList<(WorkflowCacheEntry Entry, long Bytes)> List()
    {
        var entries = new List<(WorkflowCacheEntry, long)>();
        var directory = Path.Combine(_root, "entries");

        if (!_fileSystem.DirectoryExists(directory))
        {
            return entries;
        }

        foreach (var file in _fileSystem.EnumerateFiles(directory, recursive: true))
        {
            try
            {
                if (_fileSystem.ReadAllText(file) is not { } text
                    || JsonSerializer.Deserialize<WorkflowCacheEntry>(text, Options) is not { } entry)
                {
                    continue;
                }

                entries.Add((entry, entry.Files.Sum(cached => BlobBytes(cached.Blob))));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
        }

        return [.. entries.OrderBy(listed => listed.Item1.Repository, StringComparer.Ordinal).ThenBy(listed => listed.Item1.Path, StringComparer.Ordinal)];
    }    /// <summary>
    /// Removes the entries no run used for <paramref name="unusedDays"/> days, and the blobs no entry left holds;
    /// answers what it removed, for <c>workflow-cache prune</c>.
    /// </summary>
    /// <param name="unusedDays">The days an entry may go unused before it is pruned.</param>
    /// <param name="asOf">Now, as the caller knows it.</param>
    public (int Entries, int Blobs) Prune(int unusedDays, DateTimeOffset asOf)
    {
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = 0;
        var cutoff = asOf - TimeSpan.FromDays(unusedDays);

        foreach (var (entry, _) in List())
        {
            if (entry.LastUsedAt >= cutoff || entry.FetchedAt >= cutoff)
            {
                foreach (var file in entry.Files)
                {
                    _ = kept.Add(file.Blob);
                }

                continue;
            }

            try
            {
                _fileSystem.DeleteFile(EntryFile(entry.Host, entry.Owner, entry.Repo, entry.Commit, entry.Path));
                entries++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An entry that cannot be removed is left for the next prune; the blobs it holds stay with it.
                foreach (var file in entry.Files)
                {
                    _ = kept.Add(file.Blob);
                }
            }
        }

        var blobs = 0;
        var blobsDirectory = Path.Combine(_root, "blobs");

        if (_fileSystem.DirectoryExists(blobsDirectory))
        {
            foreach (var blob in _fileSystem.EnumerateFiles(blobsDirectory, recursive: true))
            {
                var name = Path.GetFileName(blob);

                if (kept.Contains(name))
                {
                    continue;
                }

                try
                {
                    _fileSystem.DeleteFile(blob);
                    blobs++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A blob that cannot be removed is left for the next prune.
                }
            }
        }

        return (entries, blobs);
    }

    /// <summary>
    /// The text of one file of <paramref name="entry"/>, its blob hashed again as it is read; null where the entry
    /// names no such file.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The blob is not there, cannot be read, or does not hash to what the entry says (<see cref="HarnessExit.ConfigInvalid"/>)
    /// - a cache file changed on disk, which a resolution fetches again and a run refuses on.
    /// </exception>
    public string? ReadText(WorkflowCacheEntry entry, string path)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var cached = entry.Files.FirstOrDefault(file => file.Path == path);

        if (cached is null)
        {
            return null;
        }

        var blob = BlobFile(cached.Blob);

        try
        {
            var text = _fileSystem.ReadAllText(blob);

            if (BlobSha(text) != cached.Blob)
            {
                throw new HarnessException(
                    HarnessExit.ConfigInvalid,
                    $"the workflow cache's '{blob}' does not hash to '{cached.Blob}' as the entry for '{entry.Repository}/{entry.Path}' says; a cache file was changed on disk, and it is fetched again");
            }

            return text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"the workflow cache's '{blob}' could not be read: {exception.Message.TrimEnd('.')}; the entry for '{entry.Repository}/{entry.Path}' is fetched again");
        }
    }

    /// <summary>The git hash of a blob's content: SHA-1 of <c>blob &lt;bytes&gt;\0</c> and the content, hex.</summary>
    /// <param name="content">The file's content.</param>
    public static string BlobSha(string content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {Encoding.UTF8.GetByteCount(content)}\0");
        var body = Encoding.UTF8.GetBytes(content);
        var hashed = SHA1.HashData([.. header, .. body]);

        return Convert.ToHexStringLower(hashed);
    }

    private void WriteEntry(WorkflowCacheEntry entry)
    {
        var file = EntryFile(entry.Host, entry.Owner, entry.Repo, entry.Commit, entry.Path);

        try
        {
            _fileSystem.CreateDirectory(Path.GetDirectoryName(file)!);
            _fileSystem.WriteAllTextAtomic(file, JsonSerializer.Serialize(entry, Options));
            _fileSystem.ProtectSecretFile(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"the workflow cache could not write '{file}': {exception.Message.TrimEnd('.')}. The fetch is made again.");
        }
    }

    /// <summary>The entry file's path: <c>entries/&lt;sha256 of host, owner, repo, commit, path&gt;.json</c>.</summary>
    private string EntryFile(string host, string owner, string repo, string commit, string path)
        => Path.Combine(_root, "entries", $"{EntrySha(host, owner, repo, commit, path)}.json");

    /// <summary>The blob file's path: <c>blobs/&lt;first two&gt;/&lt;the rest&gt;</c>, by the git hash of its content.</summary>
    private string BlobFile(string blob)
        => Path.Combine(_root, "blobs", blob[..2], blob[2..]);

    private long BlobBytes(string blob)
    {
        try
        {
            var file = BlobFile(blob);

            return _fileSystem.FileExists(file) ? Encoding.UTF8.GetByteCount(_fileSystem.ReadAllText(file)) : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string EntrySha(string host, string owner, string repo, string commit, string path)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', host, owner, repo, commit, path))));
}
