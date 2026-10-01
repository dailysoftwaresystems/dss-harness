using RepoHarness.Core.Platform;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>
/// A sync into a directory on the machine running the harness. Also what a host runs on its own
/// side, so one implementation serves every transport and none of them can drift.
/// </summary>
public sealed class LocalSyncTransport(
    IFileSystem fileSystem,
    IManifestBuilder manifestBuilder,
    IGitClient gitClient,
    IHostPlatform platform) : ISyncTransport
{
    /// <summary>
    /// The file recording that the harness made this copy, inside the copy's own harness directory,
    /// which is withheld from transfer and so can never be overwritten by the source.
    /// </summary>
    public const string MarkerFileName = HarnessLayout.SyncedCopyMarkerName;

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IManifestBuilder _manifestBuilder = manifestBuilder;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IHostPlatform _platform = platform;

    /// <inheritdoc/>
    public HostId Host { get; } = HostId.Local;

    /// <inheritdoc/>
    public Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default)
        => Task.FromResult(_fileSystem.DirectoryExists(Home(root)));

    /// <inheritdoc/>
    public Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default)
    {
        // None is not a mark a copy can carry: it says there is no marker at all, and encoding it
        // would write one claiming the copy was both taken over and finished — the most permissive
        // thing this file can say, and the one nobody asked for. Checked before anything is created,
        // so a caller that gets this wrong leaves nothing behind.
        if (mark is not (CopyMark.Complete or CopyMark.AdoptionStopped or CopyMark.Unfinished))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mark),
                mark,
                "A copy is marked complete, as a takeover that has begun, or as a sync that has begun, never as unmarked.");
        }

        // A file where the directory should be is named rather than worked around. Creating the
        // copy somewhere else would leave a leg reporting on a tree nobody can find.
        if (_fileSystem.FileExists(Home(root)))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{root}' exists and is a file, so the repository copy cannot be created there.");
        }

        try
        {
            _fileSystem.CreateDirectory(Home(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{root}' could not be created: {ex.Message}");
        }

        // Read whenever a marker is written over one - a takeover begun or finished, a sync begun or finished - so that
        // rewriting it never erases how the copy came to be: when it was made, by which machine, and whether somebody's
        // directory was taken over to make it. A marker that cannot be read refuses, as it would anywhere: answering
        // 'not taken over' would write one indistinguishable from a copy made in an empty directory.
        var before = Marker(root);

        try
        {
            _fileSystem.WriteAllTextAtomic(
                MarkerPath(root),
                JsonSerializer.Serialize(
                    new SyncedCopyMarker(
                        before?.CreatedUtc ?? DateTimeOffset.UtcNow.ToString("O"),
                        before?.CreatedBy ?? Environment.MachineName,
                        Adopted: mark == CopyMark.AdoptionStopped || before is { Adopted: true },
                        Completed: mark != CopyMark.AdoptionStopped,
                        Unfinished: mark == CopyMark.Unfinished),
                    MarkerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Named, as a file a sync writes is: raised raw, a full disk on a host arrived as a defect in this tool.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{MarkerPath(root)}', which records how '{root}' came to be and whether its last sync finished, could not "
                + $"be written: {ex.Message}",
                ex);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<SyncInspectAnswer> InspectAsync(string root, CancellationToken cancellationToken = default)
    {
        var exists = await RootExistsAsync(root, cancellationToken).ConfigureAwait(false);

        return new(
            exists,
            await ReadMarkAsync(root, cancellationToken).ConfigureAwait(false),
            exists ? await ConfigurationInAsync(root, cancellationToken).ConfigureAwait(false) : null);
    }

    /// <summary>
    /// What the configuration the copy at <paramref name="root"/> holds, by content; <see langword="null"/> where it holds
    /// none, or one that cannot be read, which a sync reads as one its own differs from.
    /// </summary>
    private async Task<string?> ConfigurationInAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Home(root), HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);

        try
        {
            return _fileSystem.FileExists(path)
                ? (await FileContentHash.OfAsync(_fileSystem, path, cancellationToken).ConfigureAwait(false)).Content
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<CopyMark> ReadMarkAsync(string root, CancellationToken cancellationToken = default)
        => Task.FromResult(Marker(root) switch
        {
            // An unfinished takeover is told apart from a finished copy, because only one of them
            // still needs somebody to say go ahead; and a copy whose sync has not finished from both,
            // because it is this tool's own and the next sync puts it right.
            null => CopyMark.None,
            { Adopted: true, Completed: false } => CopyMark.AdoptionStopped,
            { Unfinished: true } => CopyMark.Unfinished,
            _ => CopyMark.Complete,
        });

    /// <inheritdoc/>
    public async Task InitialiseRepositoryAsync(string root, CancellationToken cancellationToken = default)
    {
        // The top of a repository of its own, as a copy a sync made or a clone it took over is. Inside
        // another repository's work tree is not that: git would find that repository from the copy, and
        // everything the harness there asks of git - the files it tracks, its index - would be answered
        // by, and written to, a repository that is not the copy's.
        if (await _gitClient.GetLocationAsync(Home(root), cancellationToken).ConfigureAwait(false) is { Prefix.Length: 0 })
        {
            return;
        }

        var result = await _gitClient
            .RunAsync(Home(root), ["init", "--quiet", "."], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{root}' could not be made a git repository, which the harness there needs to find "
                + $"anything: {result.FailureMessage}");
        }
    }

    /// <inheritdoc/>
    public Task IndexAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return _gitClient.IndexExactlyAsync(Home(root), paths, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SyncManifest> ReadManifestAsync(
        string root,
        IReadOnlyList<string> withheld,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(withheld);

        var policy = new PathSet(withheld);

        return _manifestBuilder.BuildAsync(Home(root), policy.Contains, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task WriteFileAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _fileSystem
                .WriteAllBytesAtomicAsync(Resolve(root, relativePath), contents, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Named here rather than in the loop that drives the sync, because for every host but
            // this one the write happens on the far side and only the far side's message comes
            // back. Unnamed, the reader gets an exception from somewhere inside a write with
            // neither the path nor what was being written to it.
            //
            // The likeliest cause is offered as a possibility, not asserted: the two halves of it
            // arrive as different exceptions — a file where this tree has a directory fails the
            // directory's creation, a directory where it has a file fails the replace — and a full
            // disk, a read-only mount and a file another process holds open all arrive as one of
            // the same two types. Asserting would send the reader looking for a collision that is
            // not there while the disk stays full.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' could not be written to '{root}': {ex.Message} If something along "
                + "that path is there as the other kind of thing — a file where this tree has a "
                + "directory, or a directory where it has a file — remove it there and sync again.",
                ex);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Written one after another. Nothing here opens a session, so a batch costs exactly what the files
    /// cost; it exists so that the far side of a connection is asked once rather than once per file, and
    /// this side answers the same question the same way.
    /// </remarks>
    public async Task WriteFilesAsync(
        string root,
        IReadOnlyList<SyncFileContent> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteFileAsync(root, file.Path, file.Contents, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Told apart by the copy's marker, which a copy the harness made or took over carries, and the marker goes
    /// last. A removal that stops part way - a file held open, an interruption - then leaves a directory still
    /// marked as the harness's, which asking again finishes; removed in whatever order the disk lists it, it could
    /// leave the rest of the copy unmarked, which the next request would have to leave where it is as somebody's.
    /// The one thing that can outlast the marker is a directory holding nothing at all, the root among them, when
    /// its last removal fails - a process whose working directory it is holds it on Windows - and a directory that
    /// holds no file and no link is removed whatever marks it, as there is nothing in it anybody could lose.
    /// </remarks>
    public Task<CopyRemoval> RemoveCopyAsync(string root, CancellationToken cancellationToken = default)
    {
        var home = Home(root);

        if (!_fileSystem.DirectoryExists(home))
        {
            return Task.FromResult(CopyRemoval.Absent);
        }

        var removal = Marker(root) switch
        {
            null when HoldsNothing(home) => CopyRemoval.Removed,
            null => CopyRemoval.NotACopy,
            { Adopted: true } => CopyRemoval.Adopted,
            _ => CopyRemoval.Removed,
        };

        if (removal == CopyRemoval.Removed)
        {
            try
            {
                // Each entry told by its name alone. A listing spells what it holds as the root was spelt, which is
                // how the host's configuration gives it - 'C:/src/repo', say - where a path built from the root is
                // spelt as this machine spells one, and the two would never be found equal.
                RemoveAllBut(home, HarnessLayout.DirectoryName, cancellationToken);
                RemoveAllBut(Path.Combine(home, HarnessLayout.DirectoryName), MarkerFileName, cancellationToken);
                _fileSystem.DeleteDirectory(home);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Named here, as a write's failure is, because for every host but this one the removal happens on the
                // far side and only this message comes back.
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"'{root}' could not be removed whole: {ex.Message} Its marker goes last, so what is left of it is "
                    + "still marked as the harness's copy, or holds nothing at all, and asking again removes it once "
                    + "nothing holds it.",
                    ex);
            }
        }

        return Task.FromResult(removal);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Told by name, as a sync names them: what follows the main copy's own directory name and
    /// <see cref="HostCopies.WorktreeSuffix"/>. Each is weighed by its files, a link counted as itself and none
    /// walked, and a marker that cannot be read is said as that copy's, rather than ending the listing.
    /// </remarks>
    public Task<IReadOnlyList<HostCopyFound>> ListCopiesAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var main = Path.TrimEndingDirectorySeparator(Home(repositoryPath));
        var parent = Path.GetDirectoryName(main);
        var prefix = Path.GetFileName(main) + HostCopies.WorktreeSuffix;
        var found = new List<HostCopyFound>();

        if (string.IsNullOrEmpty(parent) || !_fileSystem.DirectoryExists(parent))
        {
            return Task.FromResult<IReadOnlyList<HostCopyFound>>(found);
        }

        foreach (var directory in _fileSystem.EnumerateDirectories(parent))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

            if (leaf.Length > prefix.Length && leaf.StartsWith(prefix, StringComparison.Ordinal))
            {
                var name = leaf[prefix.Length..];
                found.Add(Found(name, HostCopies.ForWorktree(repositoryPath, name), directory));
            }
        }

        return Task.FromResult<IReadOnlyList<HostCopyFound>>([.. found.OrderBy(copy => copy.Name, StringComparer.Ordinal)]);
    }

    /// <summary>What the copy at <paramref name="directory"/> is, as a listing says it.</summary>
    /// <param name="name">The name it is kept under.</param>
    /// <param name="path">Where it is, as the configuration spells it.</param>
    /// <param name="directory">Where it is, as this machine spells it.</param>
    private HostCopyFound Found(string name, string path, string directory)
    {
        var bytes = _fileSystem.DirectorySize(directory);

        try
        {
            return Marker(directory) switch
            {
                null => new HostCopyFound(name, path, CopyOrigin.Unmarked, bytes),
                var marker => new HostCopyFound(name, path, marker.Adopted ? CopyOrigin.TakenOver : CopyOrigin.Made, bytes)
                {
                    CreatedBy = marker.CreatedBy,
                    CreatedUtc = marker.CreatedUtc,
                },
            };
        }
        catch (HarnessException ex)
        {
            return new HostCopyFound(name, path, CopyOrigin.Unreadable, bytes) { Problem = ex.Message };
        }
    }

    /// <summary>Whether <paramref name="directory"/> holds no file and no link, at any depth.</summary>
    /// <param name="directory">The directory.</param>
    private bool HoldsNothing(string directory)
        => !_fileSystem.EnumerateFiles(directory, recursive: true).Any() && !_fileSystem.EnumerateDirectoryLinks(directory).Any();

    /// <summary>Removes everything in <paramref name="directory"/> but the entry named <paramref name="kept"/>, which stays whole.</summary>
    /// <param name="directory">The directory to empty, if it is there.</param>
    /// <param name="kept">The name of the one entry in it to keep.</param>
    /// <param name="cancellationToken">Stops the removal between entries.</param>
    private void RemoveAllBut(string directory, string kept, CancellationToken cancellationToken)
    {
        if (!_fileSystem.DirectoryExists(directory))
        {
            return;
        }

        foreach (var child in _fileSystem.EnumerateDirectories(directory).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(Path.GetFileName(child), kept, _platform.PathComparison))
            {
                _fileSystem.DeleteDirectory(child);
            }
        }

        foreach (var file in _fileSystem.EnumerateFiles(directory, recursive: false).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(Path.GetFileName(file), kept, _platform.PathComparison))
            {
                _fileSystem.DeleteFile(file);
            }
        }
    }

    /// <inheritdoc/>
    public Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        _fileSystem.DeleteFile(Resolve(root, relativePath));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<EmptiedDirectory>> RemoveEmptyDirectoriesAsync(
        string root,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);

        var expanded = Home(root);
        var answered = new List<EmptiedDirectory>();

        // Deepest first, so a directory that empties only because its child went is considered
        // after that child has gone rather than before.
        foreach (var directory in directories
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(path => path.Count(character => character is '/' or '\\'))
            .ThenBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Resolved the same way every other path a sync acts on is, so one arriving with '..'
            // cannot reach a directory beside the copy.
            var full = Resolve(root, directory);

            if (!PathContainment.IsStrictlyInside(expanded, full, _platform.PathComparison) || !_fileSystem.DirectoryExists(full))
            {
                continue;
            }

            // Files and subdirectories both, and links among them, as the one emptiness test counts them: a directory
            // holding nothing but a link is correctly not empty. That is the case the manifest cannot see, and the one
            // where being wrong deletes what no plan can speak for.
            if (!_fileSystem.IsEmpty(full))
            {
                // Answered for rather than passed over. This is the shape a consumer measured: a directory whose every
                // managed file the plan deleted, kept alive by bytecode that sync.neverTransfer protects, so the checkout
                // diverges from this tree and the next structural check on that host fails with nothing naming the cause.
                answered.Add(EmptiedDirectory.Kept(directory.Replace('\\', '/'), HeldNames(full)));
                continue;
            }

            // Only the directory the plan emptied is answered for. The walk continues upward to take parents the removal
            // has just emptied in turn, but a parent that still holds something is the ordinary case - it is where the
            // tree keeps its other files - and reporting it as a copy that diverges would put a false warning on almost
            // every sync that deletes a nested directory.
            var removed = _fileSystem.RemoveEmptiedDirectories(expanded, full, _platform.PathComparison);
            answered.AddRange(removed.Select((gone, index) => EmptiedDirectory.Gone((index == 0 ? directory : ManifestBuilder.Relative(expanded, gone)).Replace('\\', '/'))));
        }

        return Task.FromResult<IReadOnlyList<EmptiedDirectory>>(answered);

        // The first names a directory still holds, as an answer for it gives them.
        IReadOnlyList<string> HeldNames(string full)
            => [.. _fileSystem
                .EnumerateFiles(full, recursive: false)
                .Concat(_fileSystem.EnumerateDirectories(full))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => name.Length > 0)
                .Order(StringComparer.Ordinal)
                .Take(HeldNamesReported)];
    }

    /// <summary>
    /// How many of a surviving directory's names are reported. Enough to recognise what kept it
    /// without printing somebody's whole cache.
    /// </summary>
    private const int HeldNamesReported = 5;

    /// <inheritdoc/>
    /// <exception cref="HarnessException">No file is at the path: nothing, a directory, or nothing along it.</exception>
    /// <remarks>
    /// Named here, as <see cref="WriteFileAsync"/> names a write that failed, and with its exit code: for
    /// every host but this one the read happens on the far side and only its message comes back, and the
    /// runtime's own exception arrived as a defect in the harness, exit 70, with the path only inside its
    /// words. A path <c>--pull</c> names is the likeliest. Never a refusal, whose code ends a whole run as a
    /// configuration would: the same read serves a sync's own plan and a carry, and a file removed since
    /// the plan was made stops that transfer part way, for the legs that needed it alone.
    /// </remarks>
    public async Task<byte[]> ReadFileAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var path = Resolve(root, relativePath);

        if (_fileSystem.DirectoryExists(path))
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' is a directory in '{root}', and only files cross, each named: name the files in it.");
        }

        try
        {
            return await _fileSystem.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' is not in '{root}': nothing is at that path there.",
                ex);
        }
    }

    /// <summary>
    /// Joins a relative path to the copy's root and refuses anything that would land outside it.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="relativePath">The path to resolve, relative to the root.</param>
    /// <exception cref="HarnessException">The path leaves the tree.</exception>
    /// <remarks>
    /// Deletion is confined to the transferred tree, and this is where that is enforced. A path
    /// holding <c>..</c> is what turns a deletion inside a repository copy into a deletion of
    /// whatever sits beside it, and the source of a path is a manifest the far side produced.
    /// </remarks>
    public string Resolve(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{relativePath}' is an absolute path, and a sync acts only inside the tree it was given.");
        }

        var expanded = Home(root);
        var full = Path.GetFullPath(Path.Combine(expanded, relativePath));

        // Compared as spelled, deliberately. Following links here looked like a stronger guard and
        // was a worse one: a build directory pointed at another volume is ordinary and withheld by
        // default for exactly that reason, and resolving it refused '--pull build/...' — a path the
        // reader named, inside the tree they named — with a message about leaving the tree. A link
        // the copy holds is disclosed where the reader can act on it, in the list an adoption prints,
        // rather than guarded here where it cannot be told from a path somebody meant.
        //
        // This machine's own comparison. Testing both would be no test at all: a path inside under
        // Ordinal is inside under OrdinalIgnoreCase too, so the pair reduces to the looser of them,
        // and on Linux a path differing only in case would escape the tree.
        if (!PathContainment.IsStrictlyInside(expanded, full, _platform.PathComparison))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{relativePath}' resolves outside '{root}', so a sync will not touch it.");
        }

        return full;
    }

    /// <summary>
    /// A declared path with a leading <c>~</c> expanded to this machine's home directory.
    /// </summary>
    /// <param name="root">The path as the configuration declares it.</param>
    /// <remarks>
    /// <c>hosts.*.repositoryPath</c> is documented with <c>~/</c> and the README's own example uses
    /// it. Passed through unexpanded it becomes a directory literally named <c>~</c> beside wherever
    /// the tool happened to run, while the host agent — which does expand it — looks in the home
    /// directory: the sync and the run then disagree about where the tree is.
    /// </remarks>
    internal static string Home(string root)
    {
        if (root != "~" && !PlatformPaths.IsHomeRelative(root))
        {
            return root;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return home.Length == 0 ? root : Path.Combine(home, root.Length <= 2 ? string.Empty : root[2..]);
    }

    private static string MarkerPath(string root)
        => Path.Combine(Home(root), HarnessLayout.DirectoryName, MarkerFileName);

    private static readonly JsonSerializerOptions MarkerOptions = new()
    {
        WriteIndented = true,

        // A shape this build does not recognise is a hard failure rather than silent data loss, as
        // it is wherever this tool reads JSON that decides something. This file decides whose
        // directory a sync is about to delete into, so it is the last one that should guess.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>What the marker file records, so a reader can tell where a copy came from.</summary>
    /// <param name="CreatedUtc">When the harness created this copy.</param>
    /// <param name="CreatedBy">The machine that created it.</param>
    /// <param name="Adopted">
    /// Whether it took over a directory that was already there, deleting what the source did not
    /// have, rather than creating an empty one. Afterwards the two are indistinguishable, and only
    /// one of them destroyed something.
    /// </param>
    /// <param name="Completed">
    /// Whether a takeover of it got to the end: false only for a takeover that stopped part way, which
    /// is neither the checkout somebody had nor a copy of the source. A sync that has not finished is
    /// said by <c>Unfinished</c>, never here, so the members a build from before that one reads keep
    /// their meaning.
    /// </param>
    /// <param name="Unfinished">
    /// Whether a sync began writing it and has not finished: written before a sync's first write, and
    /// cleared once the copy is verified. Written only where true, so a copy every sync finished carries
    /// the marker a build from before it reads.
    /// </param>
    /// <remarks>
    /// Both strings are nullable because deserialising decides that, not this declaration: a file
    /// holding <c>{}</c> parses into a marker with neither, and that is one of the shapes that has
    /// to be told from a marker this tool wrote. Every marker it has ever written carries both.
    /// </remarks>
    private sealed record SyncedCopyMarker(
        string? CreatedUtc,
        string? CreatedBy,
        bool Adopted,
        bool Completed,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Unfinished = false);

    /// <summary>
    /// What the marker at <paramref name="root"/> says, or null where there is none.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <exception cref="HarnessException">A marker is there and cannot be read.</exception>
    /// <remarks>
    /// Every unreadable shape refuses, and that direction is the whole point.
    /// <see cref="CopyMark.Complete"/> is the most permissive answer this can produce — it is what
    /// lets an ordinary <c>build</c> or <c>test</c>, which never carries an adopt list, delete a
    /// directory without asking anybody — so a damaged marker must never reach it. Reading one as
    /// complete because this build wrote it gets the inference backwards: a marker that cannot be
    /// parsed says nothing about who wrote it, and quite a lot about the record of whose directory
    /// this is having been lost. The reader loses nothing by being asked, and the one flag that
    /// answers is already in the message.
    /// <para>
    /// A marker from before takeovers existed holds neither <c>Adopted</c> nor <c>Completed</c>,
    /// which is a copy this tool made and finished. So a missing member stays legal while an
    /// unknown one does not, and the two are told apart by <c>CreatedUtc</c>, which every marker
    /// this tool has ever written carries.
    /// </para>
    /// </remarks>
    private SyncedCopyMarker? Marker(string root)
    {
        var path = MarkerPath(root);

        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        SyncedCopyMarker? marker;

        try
        {
            marker = JsonSerializer.Deserialize<SyncedCopyMarker>(_fileSystem.ReadAllText(path), MarkerOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Damaged(path, root, ex.Message, ex);
        }

        return marker is null or { CreatedUtc: null } or { CreatedBy: null }
            ? throw Damaged(path, root, "it does not hold what this tool writes there.", inner: null)
            : marker;
    }

    private static HarnessException Damaged(string path, string root, string why, Exception? inner)
        => new(
            HarnessExit.Refused,
            $"'{path}' records how '{root}' came to be, and this build cannot read it: {why} Until it "
            + "can be read there is no telling a copy this tool made from a checkout it would be "
            + "deleting into, so nothing was changed. Delete that file to have the directory treated "
            + "as one this tool did not make, which '--adopt' can then take over after listing what "
            + "it would cost.",
            inner);

    /// <summary>A withheld-path test over a list put into its comparison form once.</summary>
    /// <remarks>
    /// Through the one matcher the plan is built with, not a second copy of the rule. The walk on a
    /// host and the plan on the asking machine have to agree about what is covered, and two
    /// implementations are how a file ends up protected by one and deleted by the other.
    /// <para>
    /// The list is normalised here rather than on every comparison. A host walk asks this once per
    /// file and the matcher normalises whatever it is handed, so an un-normalised list would be
    /// re-parsed once per file per pattern — which is what "built once" was claiming not to do.
    /// </para>
    /// </remarks>
    private sealed class PathSet(IReadOnlyList<string> paths)
    {
        private readonly IReadOnlyList<string> _paths = [.. paths.Select(PathPatterns.Normalize)];

        // The harness's own directory is decided in code on this side as on the sending one, so a
        // file the source would carry is one this side sees and can remove when the source no
        // longer has it, and this machine's own state there is never listed at all.
        public bool Contains(string relativePath)
            => HarnessDirectorySync.Withholds(relativePath) || PathPatterns.Matches(_paths, relativePath);
    }
}
