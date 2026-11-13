using System.Security.Cryptography;
using System.Text;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>One file of a cached workflow directory: its path in the repository, and the git hash of its content.</summary>
/// <param name="Path">The file's path in the repository, forward slashes.</param>
/// <param name="Blob">The git hash of the file's content - the name its content is cached under.</param>
/// <param name="Mode">The file's git mode, as the repository's tree says it: <c>100644</c>, or <c>100755</c> for one that runs.</param>
public sealed record WorkflowCacheFile(string Path, string Blob, string Mode);

/// <summary>
/// One directory of one repository, at one commit, in the workflow cache: every file under it with the git hash of
/// each, beside when it was fetched and when a run last used it. Which ref resolved to it, and when, is the cache's
/// record of resolutions (<see cref="WorkflowCacheResolution"/>), kept apart: one commit may be the resolution of many
/// refs, and one ref of many commits over time.
/// </summary>
/// <param name="Host">The repository's host.</param>
/// <param name="Owner">The repository's owner.</param>
/// <param name="Repo">The repository's name.</param>
/// <param name="Commit">The commit the directory was fetched at, 40 hex characters.</param>
/// <param name="Path">The directory's path in the repository, forward slashes.</param>
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
    IReadOnlyList<WorkflowCacheFile> Files)
{
    /// <summary>The repository the entry's directory is in, as a line about it says it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Repository => $"{Host}/{Owner}/{Repo}";
}

/// <summary>
/// One resolution of a ref the cache recorded: the ref a use named, the commit the forge answered for it, whether the
/// ref was a branch, and when - the newest of each ref's, kept on every resolution that reached the forge, a cache hit
/// included, so an offline <c>last-resolved</c> stands at the commit the ref last resolved to and no other.
/// </summary>
/// <param name="Host">The repository's host.</param>
/// <param name="Owner">The repository's owner.</param>
/// <param name="Repo">The repository's name.</param>
/// <param name="Path">The workflow's directory in the repository.</param>
/// <param name="Ref">The ref the use named: a tag, or a branch where the pin allows one.</param>
/// <param name="Commit">The commit it resolved to.</param>
/// <param name="Branch">Whether the ref was a branch, which moves.</param>
/// <param name="ResolvedAt">When it resolved to it.</param>
public sealed record WorkflowCacheResolution(
    string Host,
    string Owner,
    string Repo,
    string Path,
    string Ref,
    string Commit,
    bool Branch,
    DateTimeOffset ResolvedAt);

/// <summary>A file of the cache's own that could not be read, and why - said, and never taken for one that is not there.</summary>
/// <param name="File">The file.</param>
/// <param name="Why">Why it could not be read.</param>
public sealed record WorkflowCacheUnread(string File, string Why);

/// <summary>One entry as the cache's list says it: the entry, the bytes its blobs take, the blobs of it not there, and the refs whose newest resolution it is.</summary>
/// <param name="Entry">The entry.</param>
/// <param name="Bytes">The bytes its blobs take on disk.</param>
/// <param name="Missing">How many of its files' blobs are not there, or could not be looked at.</param>
/// <param name="Resolutions">The refs whose newest resolution is this entry's commit.</param>
public sealed record WorkflowCacheListed(WorkflowCacheEntry Entry, long Bytes, int Missing, IReadOnlyList<WorkflowCacheResolution> Resolutions);

/// <summary>What the cache holds: every entry it could read, and every one of its files it could not.</summary>
/// <param name="Entries">Each entry, by repository and path.</param>
/// <param name="Unread">Each entry or resolution file that could not be read, and why.</param>
public sealed record WorkflowCacheListing(IReadOnlyList<WorkflowCacheListed> Entries, IReadOnlyList<WorkflowCacheUnread> Unread);

/// <summary>What a pruning removed, and what it could not.</summary>
/// <param name="Entries">How many entries it removed.</param>
/// <param name="Blobs">How many blobs it removed, that no entry left held.</param>
/// <param name="NotRemoved">Each file it was to remove and could not, with why.</param>
/// <param name="Unread">
/// Each entry or resolution file it could not read: while one is there, no blob is removed, for which blobs it holds
/// cannot be told.
/// </param>
public sealed record WorkflowCachePruning(int Entries, int Blobs, IReadOnlyList<WorkflowCacheUnread> NotRemoved, IReadOnlyList<WorkflowCacheUnread> Unread)
{
    /// <summary>Whether the pruning did all it was to: nothing it was to remove stayed, and nothing went unread.</summary>
    public bool Whole => NotRemoved.Count == 0 && Unread.Count == 0;
}

/// <summary>
/// The hash cache of remote workflows, in this user's own directory for this tool: a directory's files by the git
/// hash of each, kept as the bytes the forge gave and shared by whatever entries hold them; the entries themselves -
/// one for a host, a repository, a commit and a path - read strictly and held to the key they were looked up by; and
/// the newest resolution of each ref. Each blob is hashed again as it is read, so a blob changed on disk is refused and
/// fetched again, never silently run. Every write and every pruning is made under one machine-wide lock, so a pruning
/// never takes the blobs a fetch is writing.
/// </summary>
public sealed class WorkflowCache
{
    /// <summary>The name of the cache's directory under this user's own directory for this tool.</summary>
    public const string DirectoryName = "workflow-cache";

    /// <summary>The most files one fetched directory may hold; a fetch past it is refused with the count.</summary>
    public const int MostFiles = 2_000;

    /// <summary>The most bytes one fetched directory may hold; a fetch past it is refused with the size.</summary>
    public const long MostBytes = 20 * 1024 * 1024;

    /// <summary>The most days a pruning may name: a hundred years, past which no entry's date reaches.</summary>
    public const int MostUnusedDays = 36_500;

    /// <summary>The git modes a cached file may have: a plain file, and one that runs.</summary>
    public static IReadOnlyList<string> FileModes { get; } = ["100644", "100755"];

    /// <summary>UTF-8 that refuses what is not UTF-8, rather than read it as something else.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IFileSystem _fileSystem;
    private readonly Lazy<string> _root;

    /// <summary>The cache under the directory <paramref name="root"/> names, asked the first time the cache is used.</summary>
    /// <param name="fileSystem">Reads and writes the cache.</param>
    /// <param name="root">
    /// Names the cache's own directory - <c>workflow-cache</c> under this user's directory for this tool
    /// (<see cref="DefaultDirectory"/>) - asked at first use, so a process with no home of its own still runs every
    /// workflow that names nothing remote; it raises <see cref="DirectoryNotFoundException"/> where no directory can be named.
    /// </param>
    public WorkflowCache(IFileSystem fileSystem, Func<string> root)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(root);

        _fileSystem = fileSystem;
        _root = new Lazy<string>(() => Named(root), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The cache under <paramref name="root"/>.</summary>
    /// <param name="fileSystem">Reads and writes the cache.</param>
    /// <param name="root">The cache's own directory.</param>
    public WorkflowCache(IFileSystem fileSystem, string root)
        : this(fileSystem, () => root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
    }

    /// <summary>
    /// The cache's own directory, where every entry, blob and resolution of it lands - named the first time it is asked.
    /// </summary>
    /// <exception cref="HarnessException">No directory of this user's own could be named (<see cref="HarnessExit.Refused"/>).</exception>
    public string Directory => _root.Value;

    /// <summary>The cache's directory for this user: <c>workflow-cache</c> under this user's own directory for this tool.</summary>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string DefaultDirectory() => UserState.File(DirectoryName);

    /// <summary>The cache's directory for this user, or null where no directory of this user's own can be named.</summary>
    public static string? DefaultDirectoryIfNamed()
    {
        try
        {
            return DefaultDirectory();
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The entry for <paramref name="host"/>/<paramref name="owner"/>/<paramref name="repo"/> at
    /// <paramref name="commit"/>, for the directory <paramref name="path"/>, where the cache holds one; null where it holds
    /// none.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The entry is there and could not be read, does not read as an entry, or is not the entry of the key it is filed
    /// under (<see cref="HarnessExit.ConfigInvalid"/>) - never taken for one that is not there.
    /// </exception>
    public WorkflowCacheEntry? Find(string host, string owner, string repo, string commit, string path)
    {
        var file = EntryFile(host, owner, repo, commit, path);
        var entry = Read<WorkflowCacheEntry>(file, "entry");

        if (entry is null)
        {
            return null;
        }

        return EntryProblem(entry, host, owner, repo, commit, path) is { } problem
            ? throw new HarnessException(HarnessExit.ConfigInvalid, $"the workflow cache's entry '{file}' {problem}; it was not read, and a resolution of it fetches it again")
            : entry;
    }

    /// <summary>
    /// The entry a remote workflow's name - <c>remote/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;/&lt;commit&gt;/&lt;dir&gt;</c> - resolves to,
    /// where the cache holds one; null where the name is no remote workflow's, or the cache holds none.
    /// </summary>
    /// <param name="workflow">A remote workflow's name, as the run resolved it.</param>
    /// <exception cref="HarnessException">The entry is there and could not be read (<see cref="Find"/>).</exception>
    public WorkflowCacheEntry? FindNamed(string workflow)
        => WorkflowRemote.PartsOf(workflow) is { } parts ? Find(parts.Host, parts.Owner, parts.Repo, parts.Commit, parts.Directory) : null;

    /// <summary>
    /// The newest resolution the cache recorded of <paramref name="ref"/> for the directory <paramref name="path"/> of
    /// the repository, where it recorded one; null where it recorded none. What an offline <c>last-resolved</c> stands
    /// at, and what an agent's command, which never reaches the forge, resolves a ref by.
    /// </summary>
    /// <param name="host">The repository's host.</param>
    /// <param name="owner">The repository's owner.</param>
    /// <param name="repo">The repository's name.</param>
    /// <param name="path">The directory's path in the repository.</param>
    /// <param name="ref">The ref.</param>
    /// <exception cref="HarnessException">The record is there and could not be read (<see cref="HarnessExit.ConfigInvalid"/>).</exception>
    public WorkflowCacheResolution? LastResolved(string host, string owner, string repo, string path, string @ref)
    {
        var file = ResolutionFile(host, owner, repo, path, @ref);
        var resolution = Read<WorkflowCacheResolution>(file, "resolution");

        if (resolution is null)
        {
            return null;
        }

        return resolution.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            && resolution.Owner == owner && resolution.Repo == repo && resolution.Path == path && resolution.Ref == @ref
            && WorkflowRemoteSource.IsCommit(resolution.Commit)
                ? resolution
                : throw new HarnessException(HarnessExit.ConfigInvalid, $"the workflow cache's resolution '{file}' is not the record of '{host}/{owner}/{repo}/{path}@{@ref}' it is filed under; it was not read");
    }

    /// <summary>
    /// Writes an entry and every file of it, blobs first, each private to this user, under the cache's lock - and the
    /// resolution that led to it, where one did.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="contents">Each file's bytes, by its path as the entry names it.</param>
    /// <param name="resolution">The resolution of a ref that led to the entry; null where a commit was named.</param>
    /// <exception cref="HarnessException">Something of it could not be written (<see cref="HarnessExit.Refused"/>): nothing of the fetch is used.</exception>
    public void Write(WorkflowCacheEntry entry, IReadOnlyDictionary<string, byte[]> contents, WorkflowCacheResolution? resolution)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(contents);

        Locked(() =>
        {
            foreach (var file in entry.Files)
            {
                var blob = BlobFile(file.Blob);

                Written(blob, "Nothing of the fetch was cached.", () =>
                {
                    _fileSystem.CreateDirectory(System.IO.Path.GetDirectoryName(blob)!);
                    _fileSystem.WriteAtomic(blob, stream => stream.Write(contents[file.Path]));
                    _fileSystem.ProtectSecretFile(blob);
                });
            }

            WriteRecord(EntryFile(entry.Host, entry.Owner, entry.Repo, entry.Commit, entry.Path), entry, "Nothing of the fetch was cached.");

            if (resolution is not null)
            {
                WriteRecord(ResolutionFile(resolution.Host, resolution.Owner, resolution.Repo, resolution.Path, resolution.Ref), resolution, "The fetch was cached, and its resolution was not recorded.");
            }

            return true;
        });
    }

    /// <summary>
    /// Says the entry was used now, for the pruning that removes what no run used for its days, and records the
    /// resolution that led to it where one did - under the cache's lock, so a pruning never removes an entry as it is used.
    /// </summary>
    /// <param name="entry">The entry a run resolved a use to.</param>
    /// <param name="resolution">The resolution of a ref that led to it; null where a commit was named.</param>
    /// <param name="now">Now.</param>
    /// <exception cref="HarnessException">
    /// It could not be written (<see cref="HarnessExit.Refused"/>): what was read stands, and only the use's date and the
    /// resolution were not kept.
    /// </exception>
    public void Touch(WorkflowCacheEntry entry, WorkflowCacheResolution? resolution, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Locked(() =>
        {
            var file = EntryFile(entry.Host, entry.Owner, entry.Repo, entry.Commit, entry.Path);

            // An entry a pruning removed between the read and now is not written again from what this process holds:
            // its blobs went with it.
            if (_fileSystem.KindOf(file) == PathKind.File)
            {
                WriteRecord(file, entry with { LastUsedAt = now }, "What was read stands; a pruning may count the entry unused.");
            }

            if (resolution is not null)
            {
                WriteRecord(ResolutionFile(resolution.Host, resolution.Owner, resolution.Repo, resolution.Path, resolution.Ref), resolution, "What was read stands; an offline last-resolved does not stand at this resolution.");
            }

            return true;
        });
    }

    /// <summary>Every entry the cache holds, for <c>workflow-cache list</c>, and every file of its own it could not read.</summary>
    public WorkflowCacheListing List()
    {
        var unread = new List<WorkflowCacheUnread>();
        var resolutions = Records<WorkflowCacheResolution>("resolutions", unread);
        var entries = new List<WorkflowCacheListed>();

        foreach (var (file, entry) in Records<WorkflowCacheEntry>("entries", unread))
        {
            var problem = EntryProblem(entry, entry.Host, entry.Owner, entry.Repo, entry.Commit, entry.Path)
                ?? (System.IO.Path.GetFileName(file) == System.IO.Path.GetFileName(EntryFile(entry.Host, entry.Owner, entry.Repo, entry.Commit, entry.Path))
                    ? null
                    : "it is filed under a name that is not its own");

            if (problem is not null)
            {
                unread.Add(new WorkflowCacheUnread(file, problem));
                continue;
            }

            var (bytes, missing) = Measured(entry);
            var newest = resolutions
                .Select(record => record.Value)
                .Where(resolution => resolution.Host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase)
                    && resolution.Owner == entry.Owner && resolution.Repo == entry.Repo && resolution.Path == entry.Path && resolution.Commit == entry.Commit)
                .OrderBy(resolution => resolution.Ref, StringComparer.Ordinal)
                .ToList();

            entries.Add(new WorkflowCacheListed(entry, bytes, missing, newest));
        }

        return new WorkflowCacheListing(
            [.. entries.OrderBy(listed => listed.Entry.Repository, StringComparer.Ordinal).ThenBy(listed => listed.Entry.Path, StringComparer.Ordinal).ThenBy(listed => listed.Entry.Commit, StringComparer.Ordinal)],
            [.. unread.OrderBy(file => file.File, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Removes, under the cache's lock, the entries no run used and none fetched for <paramref name="unusedDays"/> days,
    /// the resolutions that named them, and the blobs no entry left holds - every blob an entry it keeps holds kept, and
    /// no blob removed at all where an entry could not be read, for which blobs that one holds cannot be told. Answers what
    /// it removed, what it could not and what it could not read, for <c>workflow-cache prune</c>.
    /// </summary>
    /// <param name="unusedDays">The days an entry may go unused before it is pruned, 0 to <see cref="MostUnusedDays"/>.</param>
    /// <param name="asOf">Now, as the caller knows it.</param>
    public WorkflowCachePruning Prune(int unusedDays, DateTimeOffset asOf)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unusedDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(unusedDays, MostUnusedDays);

        var cutoff = asOf - TimeSpan.FromDays(unusedDays);

        return Locked(() =>
        {
            var unread = new List<WorkflowCacheUnread>();
            var notRemoved = new List<WorkflowCacheUnread>();
            var kept = new HashSet<string>(StringComparer.Ordinal);
            var removed = new HashSet<(string, string, string, string, string)>();
            var entries = 0;

            foreach (var (file, entry) in Records<WorkflowCacheEntry>("entries", unread))
            {
                if (EntryProblem(entry, entry.Host, entry.Owner, entry.Repo, entry.Commit, entry.Path) is { } problem)
                {
                    unread.Add(new WorkflowCacheUnread(file, problem));
                    continue;
                }

                if (entry.LastUsedAt >= cutoff || entry.FetchedAt >= cutoff)
                {
                    kept.UnionWith(entry.Files.Select(cached => cached.Blob));
                    continue;
                }

                try
                {
                    _fileSystem.DeleteFile(file);
                    entries++;
                    removed.Add((entry.Host.ToLowerInvariant(), entry.Owner, entry.Repo, entry.Commit, entry.Path));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // An entry that cannot be removed is said, and the blobs it holds stay with it.
                    notRemoved.Add(new WorkflowCacheUnread(file, exception.Message.TrimEnd('.')));
                    kept.UnionWith(entry.Files.Select(cached => cached.Blob));
                }
            }

            foreach (var (file, resolution) in Records<WorkflowCacheResolution>("resolutions", unread))
            {
                if (!removed.Contains((resolution.Host.ToLowerInvariant(), resolution.Owner, resolution.Repo, resolution.Commit, resolution.Path)))
                {
                    continue;
                }

                try
                {
                    _fileSystem.DeleteFile(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    notRemoved.Add(new WorkflowCacheUnread(file, exception.Message.TrimEnd('.')));
                }
            }

            var blobs = 0;
            var blobsDirectory = System.IO.Path.Combine(Directory, "blobs");

            // An entry that could not be read may hold any blob: none is removed while one is there.
            if (unread.Count == 0 && _fileSystem.DirectoryExists(blobsDirectory))
            {
                foreach (var blob in _fileSystem.EnumerateFiles(blobsDirectory, recursive: true).ToList())
                {
                    // Only a blob of the cache's own naming is ever removed, and only one no entry kept holds.
                    if (BlobOf(blob) is not { } hash || kept.Contains(hash))
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
                        notRemoved.Add(new WorkflowCacheUnread(blob, exception.Message.TrimEnd('.')));
                    }
                }
            }

            return new WorkflowCachePruning(entries, blobs, notRemoved, unread);
        });
    }

    /// <summary>
    /// The bytes of one file of <paramref name="entry"/>, its blob hashed again as it is read; null where the entry
    /// names no such file.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="path">The file's path in the repository, as the entry names it.</param>
    /// <exception cref="HarnessException">
    /// The blob is not there, cannot be read, or does not hash to what the entry says (<see cref="HarnessExit.ConfigInvalid"/>)
    /// - a blob changed on disk, which a resolution fetches again and a run refuses on.
    /// </exception>
    public byte[]? ReadBytes(WorkflowCacheEntry entry, string path)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var cached = entry.Files.FirstOrDefault(file => file.Path == path);

        if (cached is null)
        {
            return null;
        }

        var blob = BlobFile(cached.Blob);
        byte[] bytes;

        try
        {
            bytes = _fileSystem.ReadAllBytes(blob);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"the workflow cache's '{blob}', the file '{path}' of the entry for '{entry.Repository}' at '{entry.Commit}', could not be read: {exception.Message.TrimEnd('.')}; a resolution of it fetches it again");
        }

        return BlobSha(bytes) == cached.Blob
            ? bytes
            : throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"the workflow cache's '{blob}', the file '{path}' of the entry for '{entry.Repository}' at '{entry.Commit}', does not hash to '{cached.Blob}' as the entry says: it was changed on disk, and is not read; a resolution of it fetches it again");
    }

    /// <summary>
    /// The text of one file of <paramref name="entry"/>: its bytes (<see cref="ReadBytes"/>) read as UTF-8, a byte order
    /// mark at its start dropped as a repository's own file read is; null where the entry names no such file.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="path">The file's path in the repository, as the entry names it.</param>
    /// <exception cref="HarnessException">
    /// The blob cannot be read or does not hash to what the entry says, or is not UTF-8 text (<see cref="HarnessExit.ConfigInvalid"/>).
    /// </exception>
    public string? ReadText(WorkflowCacheEntry entry, string path)
        => ReadBytes(entry, path) is { } bytes ? Text(bytes, $"'{path}' of the workflow '{entry.Repository}' at '{entry.Commit}'") : null;

    /// <summary>
    /// <paramref name="bytes"/> as text: UTF-8, a byte order mark at their start dropped as a repository's own file read
    /// is - so a file the forge gave with one reads as the same file read locally does.
    /// </summary>
    /// <param name="bytes">A file's bytes.</param>
    /// <param name="what">The file, as a refusal names it.</param>
    /// <exception cref="HarnessException">The bytes are not UTF-8 (<see cref="HarnessExit.ConfigInvalid"/>).</exception>
    public static string Text(byte[] bytes, string what)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var start = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;

        try
        {
            return StrictUtf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"{what} is not UTF-8 text, and a workflow reads its files as text");
        }
    }

    /// <summary>The git hash of a blob's content: SHA-1 of <c>blob &lt;bytes&gt;\0</c> and the bytes, hex, lower case.</summary>
    /// <param name="content">The file's bytes, as they are.</param>
    public static string BlobSha(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var header = Encoding.ASCII.GetBytes($"blob {content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}\0");

        return Convert.ToHexStringLower(SHA1.HashData([.. header, .. content]));
    }

    /// <summary>The git hash of a text's UTF-8 bytes, with no byte order mark (<see cref="BlobSha(byte[])"/>).</summary>
    /// <param name="content">The file's content.</param>
    public static string BlobSha(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return BlobSha(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// Every problem with the paths of one fetched directory's tree, each relative to the directory: a path rooted on
    /// any platform; a part empty, <c>.</c> or <c>..</c>; a part holding <c>\</c>, <c>:</c>, NUL or another control
    /// character - which a git tree may hold, and which a file system reads as a way out of the directory; and two paths
    /// one file system that folds case reads as one. Empty where every path is one the cache and an export write inside
    /// the directory and nowhere else.
    /// </summary>
    /// <param name="paths">The paths of the directory's every file and directory, relative to it, forward slashes.</param>
    public static IReadOnlyList<string> TreePathProblems(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var problems = new List<string>();

        foreach (var path in paths)
        {
            if (PlatformPaths.IsRootedOnAnyPlatform(path))
            {
                problems.Add($"'{path}' is rooted");
            }
            else if (path.Split('/').FirstOrDefault(part => part.Length == 0 || part is "." or "..") is { } part)
            {
                problems.Add($"'{path}' has a part that is '{part}'");
            }
            else if (Held(path) is { } held)
            {
                problems.Add($"'{path}' holds {held}, which no path of a workflow's may");
            }
        }

        foreach (var same in paths.GroupBy(path => path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            problems.Add($"'{string.Join("' and '", same.Order(StringComparer.Ordinal))}' are one path where case is folded");
        }

        return problems;

        static string? Held(string path)
        {
            foreach (var character in path)
            {
                if (character is '\\' or ':')
                {
                    return $"'{character}'";
                }

                if (char.IsControl(character))
                {
                    return character == '\0' ? "NUL" : $"the control character U+{((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)}";
                }
            }

            return null;
        }
    }

    /// <summary>The blob file's path: <c>blobs/&lt;first two hex&gt;/&lt;remaining 38 hex&gt;</c>, by the git hash of its content.</summary>
    /// <param name="blob">The git hash, 40 hex characters.</param>
    internal string BlobFile(string blob)
        => System.IO.Path.Combine(Directory, "blobs", blob[..2], blob[2..]);

    /// <summary>
    /// The git hash a file of the cache's blobs is named by - <see cref="BlobFile"/>'s inverse - or null where the file
    /// is no blob of the cache's naming: anything else under <c>blobs/</c> is never taken for one.
    /// </summary>
    /// <param name="file">A file under <c>blobs/</c>.</param>
    internal static string? BlobOf(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var rest = System.IO.Path.GetFileName(file);
        var first = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file) ?? string.Empty);
        var hash = first + rest;

        return first.Length == 2 && rest.Length == 38 && WorkflowRemoteSource.IsCommit(hash) ? hash : null;
    }

    /// <summary>
    /// The one problem with <paramref name="entry"/> as it was read: that it is the entry of the key it was looked up
    /// by, its commit and every blob 40 hex characters, every file under its directory with a mode the cache keeps and
    /// a path its tree's rule takes; null where it is.
    /// </summary>
    private static string? EntryProblem(WorkflowCacheEntry entry, string host, string owner, string repo, string commit, string path)
    {
        // A member left out or null is refused as the record is read (JsonStateFile); what is read here is whole.
        if (!entry.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || entry.Owner != owner || entry.Repo != repo || entry.Commit != commit || entry.Path != path)
        {
            return $"is the entry of '{entry.Repository}/{entry.Path}' at '{entry.Commit}', not of the '{host}/{owner}/{repo}/{path}' at '{commit}' it is filed under";
        }

        if (!WorkflowRemoteSource.IsCommit(entry.Commit))
        {
            return $"names the commit '{entry.Commit}', which is no 40 hex characters";
        }

        if (entry.Files.FirstOrDefault(file => !WorkflowRemoteSource.IsCommit(file.Blob)) is { } blob)
        {
            return $"names the blob '{blob.Blob}', which is no 40 hex characters";
        }

        if (entry.Files.FirstOrDefault(file => !file.Path.StartsWith($"{entry.Path}/", StringComparison.Ordinal) || !FileModes.Contains(file.Mode)) is { } outside)
        {
            return $"names '{outside.Path}' (mode {outside.Mode}), which is no file the cache keeps under '{entry.Path}'";
        }

        return TreePathProblems([.. entry.Files.Select(file => file.Path[(entry.Path.Length + 1)..])]) is [var first, ..]
            ? $"names a path its tree's rule refuses: {first}"
            : null;
    }

    /// <summary>The bytes <paramref name="entry"/>'s blobs take on disk, and how many of them are not there or could not be looked at.</summary>
    private (long Bytes, int Missing) Measured(WorkflowCacheEntry entry)
    {
        var bytes = 0L;
        var missing = 0;

        foreach (var blob in entry.Files.Select(file => file.Blob).Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (_fileSystem.StandingOf(BlobFile(blob)) is { } standing)
                {
                    bytes += standing.Length;
                }
                else
                {
                    missing++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                missing++;
            }
        }

        return (bytes, missing);
    }

    /// <summary>Every record of <typeparamref name="T"/> under the cache's <paramref name="directory"/>, by its file; each that could not be read said in <paramref name="unread"/>.</summary>
    private List<(string File, T Value)> Records<T>(string directory, List<WorkflowCacheUnread> unread)
        where T : class
    {
        var records = new List<(string, T)>();
        var under = System.IO.Path.Combine(Directory, directory);

        if (!_fileSystem.DirectoryExists(under))
        {
            return records;
        }

        foreach (var file in _fileSystem.EnumerateFiles(under, recursive: true).Where(file => file.EndsWith(".json", StringComparison.Ordinal)).ToList())
        {
            try
            {
                if (Read<T>(file, directory) is { } value)
                {
                    records.Add((file, value));
                }
            }
            catch (HarnessException refused)
            {
                unread.Add(new WorkflowCacheUnread(file, refused.Message));
            }
        }

        return records;
    }

    /// <summary>
    /// The record at <paramref name="file"/>, read strictly; null where nothing is there.
    /// </summary>
    /// <exception cref="HarnessException">It is there and could not be read, or not as this build's record (<see cref="HarnessExit.ConfigInvalid"/>).</exception>
    private T? Read<T>(string file, string what)
        where T : class
        => MachineWideFile.TryRead<T>(_fileSystem, file, out var value, out var unread)
            ? value
            : throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"the workflow cache's {what} '{file}' could not be read: {unread.Message.TrimEnd('.')}; it is not taken for one that is not there",
                unread);

    /// <summary>Writes one record of the cache's, as every state file is and private to this user; refused saying <paramref name="consequence"/> where it could not be.</summary>
    private void WriteRecord<T>(string file, T record, string consequence)
        => Written(file, consequence, () =>
        {
            _fileSystem.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            JsonStateFile.Write(_fileSystem, file, record);
            _fileSystem.ProtectSecretFile(file);
        });

    /// <summary>Does <paramref name="write"/>, refused naming <paramref name="file"/> and <paramref name="consequence"/> where it could not be done.</summary>
    private static void Written(string file, string consequence, Action write)
        => MachineWideFile.Written($"The workflow cache's '{file}'", consequence, write);

    /// <summary>Runs <paramref name="work"/> under the cache's one machine-wide lock, which every write and every pruning takes.</summary>
    private T Locked<T>(Func<T> work)
        => MachineWideFile.Update(System.IO.Path.Combine(Directory, "entries"), MachineWideFile.Window, work);

    /// <summary>The entry file's path: <c>entries/&lt;sha256 of host, owner, repo, commit, path&gt;.json</c>.</summary>
    private string EntryFile(string host, string owner, string repo, string commit, string path)
        => System.IO.Path.Combine(Directory, "entries", $"{KeySha(host.ToLowerInvariant(), owner, repo, commit, path)}.json");

    /// <summary>The resolution file's path: <c>resolutions/&lt;sha256 of host, owner, repo, path, ref&gt;.json</c>.</summary>
    private string ResolutionFile(string host, string owner, string repo, string path, string @ref)
        => System.IO.Path.Combine(Directory, "resolutions", $"{KeySha(host.ToLowerInvariant(), owner, repo, path, @ref)}.json");

    private static string KeySha(params string[] parts)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))));

    /// <summary>The cache's directory, as <paramref name="root"/> names it; refused with why where no directory can be named.</summary>
    private static string Named(Func<string> root)
    {
        try
        {
            return root();
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"the workflow cache has nowhere to be kept: {ex.Message.TrimEnd('.')}; a remote workflow is neither fetched nor read, and a workflow that names none runs as it would",
                ex);
        }
    }
}
