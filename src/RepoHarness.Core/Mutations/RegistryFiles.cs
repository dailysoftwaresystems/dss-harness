using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// The files of a tree as a registry's validation reads them: the texts and sites its rows cite, and the text
/// directory's listing. A view, so a registry's text can be judged by the tree as it stands or by the tree as a command
/// is about to leave it, and by one validation either way.
/// </summary>
/// <remarks>
/// A path is relative to the tree's root, with forward separators, as a row spells it.
/// </remarks>
public interface IRegistryFiles
{
    /// <summary>How the tree's own file system compares the names of its files (<see cref="PathCase.In"/>).</summary>
    StringComparer Names { get; }

    /// <summary>Whether <paramref name="path"/> is a file of the tree.</summary>
    /// <param name="path">The path, relative to the root.</param>
    bool Exists(string path);

    /// <summary>The bytes <paramref name="path"/> holds.</summary>
    /// <param name="path">The path, relative to the root.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="IOException">The file could not be read, as any file that cannot be read fails.</exception>
    /// <exception cref="UnauthorizedAccessException">The file is not this user's to read.</exception>
    Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken);

    /// <summary>Whether a sync withholds <paramref name="path"/> from every copy of the tree, so no worker would hold it.</summary>
    /// <param name="path">The path, relative to the root.</param>
    bool IsWithheld(string path);

    /// <summary>
    /// The files directly in <paramref name="textDirectory"/> that a copy of the tree holds, as the cover check reads
    /// them - or why it could not be listed - or <see langword="null"/> where no text directory is configured.
    /// </summary>
    /// <param name="textDirectory">The configured text directory, relative to the root, or <see langword="null"/>.</param>
    TextDirectoryListing? List(string? textDirectory);
}

/// <summary>A tree as it stands: its files as its file system holds them, and what a sync of it withholds.</summary>
/// <remarks>
/// Made one way, by <see cref="OfAsync"/>, which asks what a sync withholds in that tree - by the rules its
/// configuration names and by git's, which only the tree itself says - once. So whoever reads a registry in a tree, or
/// is about to write one into it, asks "withheld" the same way, of that tree.
/// </remarks>
public sealed class TreeRegistryFiles : IRegistryFiles
{
    private readonly IFileSystem _fileSystem;
    private readonly string _root;
    private readonly SyncExclusions _carried;
    private StringComparer? _names;

    private TreeRegistryFiles(IFileSystem fileSystem, string root, SyncExclusions carried)
    {
        _fileSystem = fileSystem;
        _root = root;
        _carried = carried;
    }

    /// <inheritdoc/>
    public StringComparer Names => _names ??= PathCase.In(_fileSystem, _root);

    /// <summary>The view of <paramref name="tree"/> as it stands.</summary>
    /// <param name="sync">Says what a sync of the tree withholds from every copy of it.</param>
    /// <param name="fileSystem">Reads the tree.</param>
    /// <param name="tree">The tree, and the configuration a sync of it decides by.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    /// <exception cref="Results.HarnessException">git could not be asked what it ignores in the tree.</exception>
    public static async Task<TreeRegistryFiles> OfAsync(ISyncService sync, IFileSystem fileSystem, HarnessContext tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(tree);

        return new TreeRegistryFiles(fileSystem, tree.Layout.RepositoryRoot, await sync.ExclusionsAsync(tree, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Where <paramref name="relative"/>, as configuration and rows spell it, is in <paramref name="root"/>.</summary>
    internal static string In(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <inheritdoc/>
    public bool Exists(string path) => _fileSystem.FileExists(In(_root, path));

    /// <inheritdoc/>
    /// <remarks>Read in one call, as a sweep reads the texts its registry cites: each is a few lines.</remarks>
    public Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken) => Task.FromResult(_fileSystem.ReadAllBytes(In(_root, path)));

    /// <inheritdoc/>
    public bool IsWithheld(string path) => _carried.IsWithheldFromTransfer(path);

    /// <inheritdoc/>
    /// <remarks>
    /// A file a sync withholds - what git ignores, as a desktop's or an editor's leftover is, or what the configuration
    /// keeps from a sync - is counted and left out: no worker holds it, so it is no text anybody could drive.
    /// </remarks>
    public TextDirectoryListing? List(string? textDirectory)
    {
        if (textDirectory is null)
        {
            return null;
        }

        var directory = In(_root, textDirectory);
        var named = textDirectory.TrimEnd('/');

        if (!_fileSystem.DirectoryExists(directory))
        {
            return new TextDirectoryListing(named, null);
        }

        try
        {
            var carried = _fileSystem.EnumerateFiles(directory, recursive: false)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToLookup(name => !IsWithheld(PathPatterns.Normalize(named + "/" + name)));

            return new TextDirectoryListing(named, [.. carried[true]]) { Withheld = carried[false].Count() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TextDirectoryListing(named, null) { Unlisted = ex.Message };
        }
    }
}

/// <summary>
/// A tree with some paths written and some removed: the tree a command is about to leave, before it has written
/// anything.
/// </summary>
/// <remarks>
/// <para>
/// Each written path is given as what reads its new bytes when they are asked for, never as the bytes: a command that
/// writes hundreds of paths has a registry cite a few of them, and some - a blob only git reads - cost a process to
/// read. Nothing of a written path is read until the validation asks for that path.
/// </para>
/// <para>
/// A written path is there, read as written, listed in its directory, and not withheld: it is what the command puts in
/// the tree, whatever stood under that path before. A removed path is not there. Every other path is the tree's. Paths
/// are found as the tree's own file system compares names.
/// </para>
/// </remarks>
public sealed class OverlaidRegistryFiles : IRegistryFiles
{
    private readonly IRegistryFiles _under;
    private readonly Dictionary<string, Func<CancellationToken, Task<byte[]>>?> _written;

    /// <summary>The tree <paramref name="under"/> as it would be with <paramref name="written"/> applied.</summary>
    /// <param name="under">The tree as it stands.</param>
    /// <param name="written">
    /// Each path the command writes, relative to the root, with what reads its new bytes when they are asked for; and
    /// each path it removes, with <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Two of the paths are one file as the tree's file system compares names: one file cannot be written twice over.
    /// </exception>
    public OverlaidRegistryFiles(IRegistryFiles under, IReadOnlyDictionary<string, Func<CancellationToken, Task<byte[]>>?> written)
    {
        ArgumentNullException.ThrowIfNull(under);
        ArgumentNullException.ThrowIfNull(written);

        _under = under;
        _written = new Dictionary<string, Func<CancellationToken, Task<byte[]>>?>(written, under.Names);
    }

    /// <inheritdoc/>
    public StringComparer Names => _under.Names;

    /// <inheritdoc/>
    public bool Exists(string path) => _written.TryGetValue(path, out var read) ? read is not null : _under.Exists(path);

    /// <inheritdoc/>
    public Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
        => !_written.TryGetValue(path, out var read) ? _under.ReadAsync(path, cancellationToken)
            : read is not null ? read(cancellationToken)
            : Task.FromException<byte[]>(new FileNotFoundException($"'{path}' is removed from the tree.", path));

    /// <inheritdoc/>
    public bool IsWithheld(string path) => !(_written.TryGetValue(path, out var read) && read is not null) && _under.IsWithheld(path);

    /// <inheritdoc/>
    /// <remarks>
    /// The tree's listing, without each file the command removes or writes over and with each it writes directly into
    /// the directory. A directory the tree does not hold is one once a file is written into it; one that could not be
    /// listed stays so, since what else it holds cannot be told.
    /// </remarks>
    public TextDirectoryListing? List(string? textDirectory)
    {
        var under = _under.List(textDirectory);

        if (under is null || under.Unlisted is not null)
        {
            return under;
        }

        var within = under.Directory + "/";
        var changed = _written
            .Where(entry => entry.Key.Length > within.Length && Names.Equals(entry.Key[..within.Length], within) && entry.Key.IndexOf('/', within.Length) < 0)
            .Select(entry => (Path: entry.Key, Name: entry.Key[within.Length..], Written: entry.Value is not null))
            .ToList();
        IReadOnlyList<string> written = [.. changed.Where(entry => entry.Written).Select(entry => entry.Name)];

        if (under.Files is null)
        {
            return written.Count == 0 ? under : new TextDirectoryListing(under.Directory, written);
        }

        var names = new HashSet<string>(changed.Select(entry => entry.Name), Names);

        // A file the tree's listing counted as withheld, which the command writes or removes, is withheld no longer.
        var withheld = changed.Count(entry => _under.Exists(entry.Path) && _under.IsWithheld(PathPatterns.Normalize(entry.Path)));

        return new TextDirectoryListing(under.Directory, [.. under.Files.Where(name => !names.Contains(name)), .. written]) { Withheld = under.Withheld - withheld };
    }
}
