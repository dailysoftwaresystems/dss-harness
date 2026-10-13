using System.Text.Json.Serialization;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>One file in a tree, identified by what it holds rather than by when it was written.</summary>
/// <param name="Path">The file's path relative to the tree root, with forward separators.</param>
/// <param name="Size">Its length in bytes.</param>
/// <param name="ContentHash">
/// Lowercase hex SHA-256 of its bytes. Content, never a timestamp: one host this tool serves has a
/// wall clock that steps forward by about 25 seconds every few seconds, and the steps reach file
/// modification times, so a file written after a marker can carry a stamp from before it. Equality
/// of content carries the same distortion on both sides and a clock cannot bend it.
/// </param>
public sealed record SyncEntry(string Path, long Size, string ContentHash)
{
    /// <summary>What the file holds, as everything that compares files compares it.</summary>
    /// <remarks>
    /// Never written out: it is <see cref="Size"/> and <see cref="ContentHash"/> again, and an entry crosses to and
    /// from every host in a manifest.
    /// </remarks>
    [JsonIgnore]
    public FileContent Identity => new(Size, ContentHash);
}

/// <summary>The content of a tree, as the identity sync compares and confirms.</summary>
/// <param name="Root">The tree this manifest describes.</param>
/// <param name="Entries">Every transferable file, keyed by relative path.</param>
public sealed record SyncManifest(string Root, IReadOnlyDictionary<string, SyncEntry> Entries)
{
    /// <summary>
    /// Every link the walk refused to follow, by relative path. Not content, and never compared with
    /// anything: a link is neither transferred nor deleted. Recorded because a reader deciding
    /// whether to let this tool take a directory over cannot see them any other way — a link is in
    /// no entry, so a file written at its name replaces it and is reported as an ordinary write,
    /// and a directory behind one hides everything under it from the list entirely.
    /// </summary>
    public IReadOnlyList<string> Links { get; init; } = [];

    /// <summary>An empty manifest, for a destination that does not exist yet.</summary>
    /// <param name="root">The tree the manifest would describe.</param>
    public static SyncManifest Empty(string root)
        => new(root, new Dictionary<string, SyncEntry>(StringComparer.Ordinal));

    /// <summary>The paths in this manifest, in a stable order.</summary>
    public IEnumerable<string> Paths => Entries.Keys.OrderBy(path => path, StringComparer.Ordinal);
}

/// <summary>Builds a manifest of a tree.</summary>
public interface IManifestBuilder
{
    /// <summary>Reads every transferable file under <paramref name="root"/> and hashes its content.</summary>
    /// <param name="root">The tree root.</param>
    /// <param name="isWithheld">
    /// Decides whether a relative path is withheld. Consulted on directories as well as files, so a
    /// withheld directory is never walked: walking a build tree to discard it costs the whole tree.
    /// </param>
    /// <param name="cancellationToken">Stops the walk.</param>
    Task<SyncManifest> BuildAsync(
        string root,
        Func<string, bool> isWithheld,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the tree as <see cref="BuildAsync"/> does, then looks at it once more, without hashing: the same files, each
    /// standing as it did as it was hashed - or refuses the reading, naming the first path that moved.
    /// </summary>
    /// <param name="root">The tree root.</param>
    /// <param name="isWithheld">Decides whether a relative path is withheld, as for <see cref="BuildAsync"/>.</param>
    /// <param name="cancellationToken">Stops the walks.</param>
    /// <exception cref="HarnessException">
    /// A file was written after it was hashed, one appeared, or one went, before the second look reached it: the tree
    /// changed while it was read (<see cref="LegExit.InputsMoved"/>).
    /// </exception>
    /// <remarks>
    /// One pass reads each file once, at its own moment, so a write that begins and ends while the tree is read gives a
    /// manifest holding some files as they were before it and some as after - a tree that never existed. Every hash
    /// precedes every second look, so a reading that passes is the tree as it stood the instant the hashing ended. How
    /// each file stood is taken before its bytes are hashed: taken after, a write in between would be recorded as how
    /// the file stood, beside a hash of what it held before.
    /// </remarks>
    Task<SyncManifest> BuildSettledAsync(
        string root,
        Func<string, bool> isWithheld,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IManifestBuilder"/>
public sealed class ManifestBuilder(IFileSystem fileSystem, IHostPlatform platform) : IManifestBuilder
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <summary>
    /// Turns an absolute path into the form a manifest keys by: relative to the root, with forward
    /// separators.
    /// </summary>
    /// <param name="root">The tree root.</param>
    /// <param name="path">A path inside it.</param>
    /// <remarks>
    /// Forward separators on every platform, because the two sides of a sync frequently do not agree
    /// about separators: a Windows machine syncing to a WSL distribution would otherwise find no
    /// path in common and rewrite the whole tree on every run.
    /// </remarks>
    public static string Relative(string root, string path)
        => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <inheritdoc/>
    public async Task<SyncManifest> BuildAsync(
        string root,
        Func<string, bool> isWithheld,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isWithheld);

        return (await ReadAsync(root, isWithheld, standings: null, cancellationToken).ConfigureAwait(false)).Manifest;
    }

    /// <inheritdoc/>
    public async Task<SyncManifest> BuildSettledAsync(
        string root,
        Func<string, bool> isWithheld,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isWithheld);

        var standings = new Dictionary<string, FileStanding?>(StringComparer.Ordinal);
        var (manifest, links) = await ReadAsync(root, isWithheld, standings, cancellationToken).ConfigureAwait(false);

        // Every hash is taken by now. The second look lists the tree again and looks at each file, reading no byte.
        var now = new Dictionary<string, FileStanding?>(StringComparer.Ordinal);

        if (_fileSystem.DirectoryExists(root))
        {
            Look(root, root, isWithheld, links, now, cancellationToken);
        }

        var moved = manifest.Entries.Keys
            .Select(path => (Path: path, What: Moved(standings[path], now.TryGetValue(path, out var stands) ? stands : null)))
            .Concat(now.Keys.Where(path => !manifest.Entries.ContainsKey(path)).Select(path => (Path: path, What: (string?)"appeared")))
            .Where(change => change.What is not null)
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .FirstOrDefault();

        return moved.What is null
            ? manifest
            : throw new HarnessException(
                LegExit.InputsMoved,
                $"'{root}' changed while it was read: '{ReportText.Printable(moved.Path)}' {moved.What}. Let it settle, then run again.");

        // What became of a file the first walk hashed, by how it stood then and how it stands now; nothing where it
        // stands as it did.
        static string? Moved(FileStanding? was, FileStanding? now)
            => now is null ? "went" : was is not null && was.StandsAs(now) ? null : "was written";
    }

    /// <summary>
    /// The first walk: every file's entry, and where <paramref name="standings"/> is given, how each stood as it was
    /// about to be hashed.
    /// </summary>
    private async Task<(SyncManifest Manifest, HashSet<string> Links)> ReadAsync(
        string root,
        Func<string, bool> isWithheld,
        Dictionary<string, FileStanding?>? standings,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, SyncEntry>(StringComparer.Ordinal);

        if (!_fileSystem.DirectoryExists(root))
        {
            return (SyncManifest.Empty(root), []);
        }

        var links = new List<string>();

        await WalkAsync(root, root, isWithheld, entries, links, standings, cancellationToken).ConfigureAwait(false);

        links.Sort(StringComparer.Ordinal);

        return (new SyncManifest(root, entries) { Links = links }, new HashSet<string>(links, StringComparer.Ordinal));
    }

    /// <summary>
    /// The second walk of <see cref="BuildSettledAsync"/>: each file the first walk would read, by how it stands now. A
    /// path the first found to be a link is passed over by name, and a directory is asked again whether it is one: one
    /// that became a link is not followed, and the files below it read as gone.
    /// </summary>
    private void Look(
        string root,
        string directory,
        Func<string, bool> isWithheld,
        HashSet<string> links,
        Dictionary<string, FileStanding?> now,
        CancellationToken cancellationToken)
    {
        foreach (var file in _fileSystem.EnumerateFiles(directory, recursive: false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Relative(root, file);

            if (!isWithheld(relative) && !links.Contains(relative))
            {
                now[relative] = _fileSystem.StandingOf(file);
            }
        }

        foreach (var child in _fileSystem.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Relative(root, child);

            if (!isWithheld(relative) && !links.Contains(relative) && !IsLink(child))
            {
                Look(root, child, isWithheld, links, now, cancellationToken);
            }
        }
    }

    private async Task WalkAsync(
        string root,
        string directory,
        Func<string, bool> isWithheld,
        Dictionary<string, SyncEntry> entries,
        List<string> links,
        Dictionary<string, FileStanding?>? standings,
        CancellationToken cancellationToken)
    {
        foreach (var file in _fileSystem.EnumerateFiles(directory, recursive: false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Relative(root, file);

            if (isWithheld(relative))
            {
                continue;
            }

            // A link is not followed, for a file as much as for a directory. Opening one reads
            // whatever it points at, so a link committed to the repository and aimed outside it
            // would have its target's bytes hashed here and written to another machine under the
            // link's own name — the repository's content deciding what leaves this machine.
            if (IsLink(file))
            {
                links.Add(relative);
                continue;
            }

            // Before its bytes: a write between the hash and the look would otherwise be recorded as how it stood.
            if (standings is not null)
            {
                standings[relative] = _fileSystem.StandingOf(file);
            }

            entries[relative] = await DescribeAsync(relative, file, cancellationToken).ConfigureAwait(false);
        }

        foreach (var child in _fileSystem.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Relative(root, child);

            if (isWithheld(relative))
            {
                continue;
            }

            // A directory link inside a tree makes the walk unbounded, and one that leaves the tree
            // would put files outside it into the manifest.
            if (IsLink(child))
            {
                links.Add(relative);
                continue;
            }

            await WalkAsync(root, child, isWithheld, entries, links, standings, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SyncEntry> DescribeAsync(string relative, string file, CancellationToken cancellationToken)
    {
        var content = await FileContentHash.OfAsync(_fileSystem, file, cancellationToken).ConfigureAwait(false);

        return new SyncEntry(relative, content.Length, content.Content);
    }

    /// <summary>
    /// Whether <paramref name="path"/> reaches somewhere other than itself, so that reading or
    /// walking it would act on something the tree does not contain.
    /// </summary>
    /// <remarks>
    /// Asked of a file as well as a directory. Every link along the path is followed and the answer
    /// compared with the path as spelled, so a link whose target happens to sit inside the tree is
    /// still not transferred: what would arrive on the other machine is a copy of the target under
    /// the link's name, which is not what the source holds.
    /// </remarks>
    private bool IsLink(string path) => LinkPaths.IsLink(_fileSystem, path, _platform.PathComparison);
}
