using RepoHarness.Core.Build;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>One file a target's build lines read, whole, and whether a build line makes it.</summary>
/// <param name="Path">The file, whole, as the build directory resolves it.</param>
/// <param name="Produced">Whether a build line other than a phony one makes it, so what it is made from is read too.</param>
internal sealed record BuiltFromPath(string Path, bool Produced);

/// <summary>
/// What a worker's own records say a target's build reads: the build lines behind it, in the order the manifest reaches
/// them, and every file they read, each marked as made by a build line or not.
/// </summary>
/// <param name="Target">The target, as <c>--target</c> names it.</param>
/// <param name="Lines">The build lines that build it, and every build line building what they read, however far back.</param>
/// <param name="Read">
/// Every file those lines read - their inputs, what their builds recorded, and the same of every line producing one of
/// them - as <see cref="NinjaRebuildGraph.ReadBy"/> gives them.
/// </param>
internal sealed record BuiltFromSource(string Target, IReadOnlyList<NinjaEdge> Lines, IReadOnlyList<BuiltFromPath> Read);

/// <summary>The files of one directory a target's build reads that are no file of the tree: how many, and one digest of them.</summary>
/// <param name="Files">How many.</param>
/// <param name="Digest">The SHA-256 of their names and what each holds, sorted by name.</param>
public sealed record FilesIn(int Files, string Digest);

/// <summary>
/// What one target is built from, as a worker's build of it read: its build lines, every file of the tree they read, the
/// dependency sources its build was given, and what else they read - in the worker, made by its configure, and outside
/// it - each told so that every worker of every tree of the leg comes to the same table for the same content.
/// </summary>
/// <remarks>
/// The effects of what a build is configured by, never the causes: a list file edited for another target changes no
/// build line of this one and no file it reads, so it changes nothing of this table. Its digest is the SHA-256 of its
/// canonical JSON (<see cref="CanonicalJson"/>), which an arm's key holds for each target its build builds.
/// </remarks>
public sealed record BuiltFromTable
{
    /// <summary>The target.</summary>
    public required string Target { get; init; }

    /// <summary>
    /// The SHA-256 of its build lines in the order reached - each its rule, outputs, inputs and command - with the worker's
    /// own path told (<see cref="WorkerPath"/>).
    /// </summary>
    public required string Lines { get; init; }

    /// <summary>Each file of the tree its build reads, as the reading of the tree spells its path, with what it holds.</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }

    /// <summary>
    /// Each dependency whose sources the worker was given and its build reads, by its name, with the digest of every file
    /// of those sources as the sweep read them: the whole dependency stands for the files of it the target reads.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Fetched { get; init; }

    /// <summary>
    /// What its build reads that the worker's configure made - in the worker, made by no build line, and no file of the
    /// tree - by directory, <c>{build}/&lt;dir&gt;</c> or <c>{tree}/&lt;dir&gt;</c>, each file read with the worker's path told.
    /// </summary>
    public required IReadOnlyDictionary<string, FilesIn> Made { get; init; }

    /// <summary>What its build reads outside the worker - a system's header, a library a link names - by directory, read where it is.</summary>
    public required IReadOnlyDictionary<string, FilesIn> Outside { get; init; }

    /// <summary>The SHA-256 of the table's canonical JSON: what an arm's key holds of it.</summary>
    public string Digest() => CanonicalJson.Digest(new
    {
        Target,
        Lines,
        Files = CanonicalJson.Sorted(Files),
        Fetched = CanonicalJson.Sorted(Fetched),
        Made = CanonicalJson.Sorted(Made),
        Outside = CanonicalJson.Sorted(Outside),
    });

    /// <summary>The table by the digests of its parts (<see cref="TableIndex"/>).</summary>
    public TableIndex Index() => new(
        Target,
        Lines,
        CanonicalJson.Digest(CanonicalJson.Sorted(Files)),
        CanonicalJson.Sorted(Fetched),
        CanonicalJson.Digest(CanonicalJson.Sorted(Made)),
        CanonicalJson.Digest(CanonicalJson.Sorted(Outside)));
}

/// <summary>
/// A table by the digests of its parts, which the record of verdicts keeps for each table an entry names, so which part of
/// a table differs can be named without reading the table.
/// </summary>
/// <param name="Target">The target.</param>
/// <param name="Lines">The digest of its build lines, as the table holds it.</param>
/// <param name="Files">The digest of the files of the tree its build reads.</param>
/// <param name="Fetched">Each dependency whose sources its build reads, with the digest of those sources, as the table holds them.</param>
/// <param name="Made">The digest of what its build reads that the worker's configure made.</param>
/// <param name="Outside">The digest of what its build reads outside the worker.</param>
public sealed record TableIndex(
    string Target,
    string Lines,
    string Files,
    IReadOnlyDictionary<string, string> Fetched,
    string Made,
    string Outside);

/// <summary>A target's table as a worker's build read it, or why it could not be made.</summary>
/// <param name="Target">The target.</param>
/// <param name="Table">The table, or <see langword="null"/> where a file its build reads could not be read.</param>
/// <param name="Unread">Why there is no table, as what follows "cannot be read now: "; <see langword="null"/> where there is one.</param>
internal sealed record BuiltFromReading(string Target, BuiltFromTable? Table, string? Unread);

/// <summary>
/// Turns what a worker's records say each target's build reads into what the target is built from: each file sorted into
/// what a build line makes, which is left out - what it is made from is read already - a file of the tree, a dependency's
/// sources the worker was given, a file the worker's configure made, and a file outside the worker.
/// </summary>
/// <remarks>
/// A file of the tree is taken by what the sweep's one reading of the tree says it holds, found as the tree's file system
/// compares names and named as the reading spells it, and is never hashed a second time. Every other file is hashed where
/// it is, once however many targets read it. A file that cannot be read leaves its target with no table, and says why.
/// </remarks>
internal sealed class BuiltFromReader
{
    /// <summary>The directory of the tree's files a table names, standing for the worker's root.</summary>
    public const string TreeDirectory = WorkerPath.Tree;

    /// <summary>The directory of the worker's build a table names, standing for its build directory.</summary>
    public const string BuildDirectory = "{build}";

    private readonly IFileSystem _fileSystem;
    private readonly string _worker;
    private readonly string _build;
    private readonly StringComparer _names;
    private readonly Dictionary<string, SyncEntry> _tree;
    private readonly IReadOnlyList<(string Directory, string Name, string Digest)> _fetched;

    /// <summary>What each file read so far holds, or why it could not be read, by its path.</summary>
    private readonly Dictionary<string, (string? Hash, string? Unread)> _hashed;

    /// <summary>A reader of the tables of one worker's build.</summary>
    /// <param name="fileSystem">Reads the files no reading holds.</param>
    /// <param name="reading">The sweep's one reading of the tree: every file a sync carries, by size and hash.</param>
    /// <param name="fetched">The dependency sources the worker was given, as the sweep read them.</param>
    /// <param name="worker">The worker's root.</param>
    /// <param name="buildDirectory">The worker's build directory.</param>
    /// <param name="names">How the tree's file system compares names.</param>
    public BuiltFromReader(
        IFileSystem fileSystem,
        SyncManifest reading,
        IReadOnlyList<FetchedReading> fetched,
        string worker,
        string buildDirectory,
        StringComparer names)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(fetched);
        ArgumentNullException.ThrowIfNull(names);

        _fileSystem = fileSystem;
        _worker = Whole(worker);
        _build = Whole(buildDirectory);
        _names = names;
        _hashed = new(names);
        _tree = new(names);

        foreach (var entry in reading.Entries.Values.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            _tree.TryAdd(entry.Path, entry);
        }

        _fetched = [.. fetched.Select(read => (Whole(FetchedSources.KeptIn(_worker, read.Source.Name)), read.Source.Name, ManifestDigest(read.Files)))];
    }

    /// <summary>The table of what <paramref name="source"/>'s target is built from, or why it could not be made.</summary>
    /// <param name="source">What the worker's records say the target's build reads.</param>
    /// <param name="cancellationToken">Stops the reading of the files no reading holds.</param>
    public async Task<BuiltFromReading> ReadAsync(BuiltFromSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var fetched = new Dictionary<string, string>(StringComparer.Ordinal);
        var made = new Dictionary<string, List<(string Name, string Hash)>>(StringComparer.Ordinal);
        var outside = new Dictionary<string, List<(string Name, string Hash)>>(StringComparer.Ordinal);

        foreach (var read in source.Read)
        {
            if (read.Produced)
            {
                continue;
            }

            var path = Whole(read.Path);

            if (!Under(path, _worker))
            {
                if (await HashAsync(path, told: false, cancellationToken).ConfigureAwait(false) is not { } held)
                {
                    return Unread(source, path);
                }

                Add(outside, Path.GetDirectoryName(path) ?? path, path, held);
                continue;
            }

            if (_fetched.FirstOrDefault(given => Under(path, given.Directory)) is { Name: not null } dependency)
            {
                fetched[dependency.Name] = dependency.Digest;
                continue;
            }

            if (OfTheTree(path) is { } entry)
            {
                files[entry.Path] = entry.ContentHash;
                continue;
            }

            if (await HashAsync(path, told: true, cancellationToken).ConfigureAwait(false) is not { } configured)
            {
                return Unread(source, path);
            }

            var within = Under(path, _build) ? (BuildDirectory, _build) : (TreeDirectory, _worker);
            var directory = Path.GetRelativePath(within.Item2, Path.GetDirectoryName(path)!).Replace('\\', '/');

            Add(made, directory == "." ? within.Item1 : $"{within.Item1}/{directory}", path, configured);
        }

        return new BuiltFromReading(
            source.Target,
            new BuiltFromTable
            {
                Target = source.Target,
                Lines = CanonicalJson.Digest(source.Lines.Select(line => new
                {
                    line.Rule,
                    Outputs = line.Outputs.Select(Told),
                    Inputs = line.Inputs.Select(Told),
                    Command = Told(line.Command),
                })),
                Files = CanonicalJson.Sorted(files),
                Fetched = CanonicalJson.Sorted(fetched),
                Made = Grouped(made),
                Outside = Grouped(outside),
            },
            null);
    }

    /// <summary>
    /// Each of <paramref name="paths"/> that is a file of the tree - in the worker, and held by the sweep's reading - by its
    /// path as the reading spells it, with what the reading says it holds; every other path left out.
    /// </summary>
    /// <param name="paths">Files, whole, as the build directory resolves them.</param>
    public IReadOnlyDictionary<string, string> OfTheTree(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var held = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            if (OfTheTree(Whole(path)) is { } entry)
            {
                held[entry.Path] = entry.ContentHash;
            }
        }

        return CanonicalJson.Sorted(held);
    }

    /// <summary>
    /// The reading's entry for <paramref name="path"/>, where it is in the worker and the reading holds it, found as the
    /// tree's file system compares names; otherwise <see langword="null"/>.
    /// </summary>
    /// <param name="path">A file, whole.</param>
    private SyncEntry? OfTheTree(string path)
        => Under(path, _worker) && _tree.TryGetValue(Path.GetRelativePath(_worker, path).Replace('\\', '/'), out var entry) ? entry : null;

    /// <summary>The digest of every file of a dependency's sources as read: each path with its size and what it holds, sorted by path.</summary>
    private static string ManifestDigest(SyncManifest files)
        => CanonicalJson.Digest(files.Entries.Values
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => new { entry.Path, entry.Size, entry.ContentHash }));

    /// <summary>Each directory's files, counted and digested by their names and what each holds.</summary>
    private static IReadOnlyDictionary<string, FilesIn> Grouped(Dictionary<string, List<(string Name, string Hash)>> directories)
        => CanonicalJson.Sorted(directories.ToDictionary(
            pair => pair.Key,
            pair => new FilesIn(
                pair.Value.Count,
                CanonicalJson.Digest(pair.Value.OrderBy(file => file.Name, StringComparer.Ordinal).Select(file => new { file.Name, file.Hash }))),
            StringComparer.Ordinal));

    /// <summary>Adds <paramref name="path"/>, holding <paramref name="hash"/>, to the files of <paramref name="directory"/>, once.</summary>
    private static void Add(Dictionary<string, List<(string Name, string Hash)>> directories, string directory, string path, string hash)
    {
        if (!directories.TryGetValue(directory, out var files))
        {
            directories[directory] = files = [];
        }

        var name = Path.GetFileName(path);

        if (!files.Exists(file => string.Equals(file.Name, name, StringComparison.Ordinal)))
        {
            files.Add((name, hash));
        }
    }

    /// <summary>A path whole, with no separator at its end.</summary>
    private static string Whole(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary><paramref name="source"/>'s target with no table, because <paramref name="path"/> could not be read.</summary>
    private BuiltFromReading Unread(BuiltFromSource source, string path)
        => new(source.Target, null, $"'{path}', which the build of '{source.Target}' reads, {_hashed[path].Unread}");

    /// <summary>Whether <paramref name="path"/> is below <paramref name="directory"/>, as the tree's file system compares names.</summary>
    private bool Under(string path, string directory)
        => path.Length > directory.Length
            && path[directory.Length] is '/' or '\\'
            && _names.Equals(path[..directory.Length], directory);

    /// <summary>A build line's word with the worker's own path told.</summary>
    private string Told(string text) => WorkerPath.Told(text, _worker);

    /// <summary>
    /// What <paramref name="path"/> holds - with the worker's own path told in it where <paramref name="told"/> - read once
    /// however many targets read it; <see langword="null"/> where it could not be read, which is then kept to say why.
    /// </summary>
    private async Task<string?> HashAsync(string path, bool told, CancellationToken cancellationToken)
    {
        if (_hashed.TryGetValue(path, out var known))
        {
            return known.Hash;
        }

        try
        {
            if (!_fileSystem.FileExists(path))
            {
                _hashed[path] = (null, "is gone");
                return null;
            }

            var hash = told
                ? FileContentHash.Of(WorkerPath.Told(await _fileSystem.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), _worker))
                : (await FileContentHash.OfAsync(_fileSystem, path, cancellationToken).ConfigureAwait(false)).Content;

            _hashed[path] = (hash, null);

            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _hashed[path] = (null, $"could not be read: {ex.Message.TrimEnd('.')}");
            return null;
        }
    }
}
