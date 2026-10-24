using DssHarness.Core.Platform;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Sync;

/// <summary>
/// A sync into a directory on the machine running the harness. Also what a host runs on its own
/// side, so one implementation serves every transport and none of them can drift.
/// </summary>
public sealed class LocalSyncTransport(
    IFileSystem fileSystem,
    IManifestBuilder manifestBuilder,
    IGitClient gitClient,
    IHostPlatform platform,
    ICopyClaims claims) : ISyncTransport
{
    /// <summary>
    /// The file recording that the harness made this copy, inside the copy's own harness directory,
    /// which is withheld from transfer and so can never be overwritten by the source.
    /// </summary>
    public const string MarkerFileName = HarnessLayout.SyncedCopyMarkerName;

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IManifestBuilder _manifestBuilder = manifestBuilder;
    private readonly IGitClient _gitClient = gitClient;

    /// <summary>
    /// Which run is using a copy kept in a tree, asked before a mutation worker is removed, and what a worker is
    /// named. Never optional: a transport built without it would remove a worker from under the sweep mutating it.
    /// </summary>
    private readonly ICopyClaims _claims = claims ?? throw new ArgumentNullException(nameof(claims));
    private readonly IHostPlatform _platform = platform;

    /// <inheritdoc/>
    public HostId Host { get; } = HostId.Local;

    /// <inheritdoc/>
    public Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default)
        => Task.FromResult(_fileSystem.DirectoryExists(Home(root)));

    /// <inheritdoc/>
    public Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default)
        => MarkAsync(root, mark, reading: null);

    /// <inheritdoc/>
    public Task MarkFinishedAsync(string root, CopyReading reading, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reading);

        return MarkAsync(root, CopyMark.Complete, reading);
    }

    /// <summary>
    /// Creates the copy's root where it is not there and writes its marker: <paramref name="mark"/>, holding
    /// <paramref name="reading"/> where it is given - and no reading where it is not, since a copy a sync began writing,
    /// or marked without saying, holds no reading anybody can name.
    /// </summary>
    private Task MarkAsync(string root, CopyMark mark, CopyReading? reading)
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
            JsonStateFile.Write(
                _fileSystem,
                MarkerPath(root),
                new SyncedCopyMarker(
                    before?.CreatedUtc ?? DateTimeOffset.UtcNow.ToString("O"),
                    before?.CreatedBy ?? Environment.MachineName,
                    Adopted: mark == CopyMark.AdoptionStopped || before is { Adopted: true },
                    Completed: mark != CopyMark.AdoptionStopped,
                    Unfinished: mark == CopyMark.Unfinished,
                    ReadBy: reading?.ReadBy,
                    Commit: reading?.Commit,
                    UncommittedChanges: reading?.UncommittedChanges),
                MarkerOptions);
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
    public async Task<SyncInspectAnswer> InspectAsync(string root, GitHistoryWanted? wanted = null, CancellationToken cancellationToken = default)
    {
        var exists = await RootExistsAsync(root, cancellationToken).ConfigureAwait(false);
        var marker = Marker(root);

        return new(
            exists,
            MarkOf(marker),
            exists ? await ConfigurationInAsync(root, cancellationToken).ConfigureAwait(false) : null)
        {
            // Only where a sync that finished recorded one: a copy marked otherwise holds no reading anybody can name.
            Reading = marker is { UncommittedChanges: { } changes } && MarkOf(marker) == CopyMark.Complete
                ? new CopyReading(marker.ReadBy, marker.Commit, changes)
                : null,

            // Of its own repository alone: inside another's work tree, git answers for that one, which is not the copy's.
            Repository = exists && await IsItsOwnRepositoryAsync(root, cancellationToken).ConfigureAwait(false)
                ? await _gitClient.ReadHistoryAsync(Home(root), wanted, cancellationToken).ConfigureAwait(false)
                : null,
        };
    }

    /// <summary>
    /// Whether <paramref name="root"/> is the top of a git repository of its own, as a copy a sync made or a clone it
    /// took over is. Inside another repository's work tree is not that: git would find that repository from the copy,
    /// and everything the harness there asks of git - the files it tracks, its index, its HEAD - would be answered by,
    /// and written to, a repository that is not the copy's.
    /// </summary>
    private async Task<bool> IsItsOwnRepositoryAsync(string root, CancellationToken cancellationToken)
        => await _gitClient.GetLocationAsync(Home(root), cancellationToken).ConfigureAwait(false) is { Prefix.Length: 0 };

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
        => Task.FromResult(MarkOf(Marker(root)));

    /// <summary>What <paramref name="marker"/> says of how the copy came to be, and whether its last sync finished.</summary>
    private static CopyMark MarkOf(SyncedCopyMarker? marker) => marker switch
    {
        // An unfinished takeover is told apart from a finished copy, because only one of them
        // still needs somebody to say go ahead; and a copy whose sync has not finished from both,
        // because it is this tool's own and the next sync puts it right.
        null => CopyMark.None,
        { Adopted: true, Completed: false } => CopyMark.AdoptionStopped,
        { Unfinished: true } => CopyMark.Unfinished,
        _ => CopyMark.Complete,
    };

    /// <inheritdoc/>
    public async Task InitialiseRepositoryAsync(string root, string? objectFormat = null, CancellationToken cancellationToken = default)
    {
        if (await IsItsOwnRepositoryAsync(root, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var result = await InitAsync(root, objectFormat, cancellationToken).ConfigureAwait(false);

        // A git that does not know the option knows one way to name objects, and it is this one: asked again without.
        if (!result.Succeeded && objectFormat == GitHistoryWanted.DefaultObjectFormat)
        {
            result = await InitAsync(root, null, cancellationToken).ConfigureAwait(false);
        }

        if (!result.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{root}' could not be made a git repository, which the harness there needs to find "
                + $"anything: {result.FailureMessage}");
        }
    }

    /// <summary>
    /// Makes <paramref name="root"/> a git repository naming its objects by <paramref name="objectFormat"/>, where one
    /// is given: said always, since a host's own configuration may choose another, and a repository naming them
    /// another way than the tree's can hold no commit of that tree's.
    /// </summary>
    private Task<GitCommandResult> InitAsync(string root, string? objectFormat, CancellationToken cancellationToken)
        => _gitClient.RunAsync(
            Home(root),
            objectFormat is null ? ["init", "--quiet", "."] : ["init", "--quiet", $"--object-format={objectFormat}", "."],
            cancellationToken: cancellationToken);

    /// <inheritdoc/>
    public Task SendHistoryAsync(string root, string pack, long offset, byte[] piece, CancellationToken cancellationToken = default)
        => _gitClient.ReceiveHistoryAsync(Home(root), pack, offset, piece, cancellationToken);

    /// <inheritdoc/>
    public Task<GitHeadMoved> TakeHistoryAsync(string root, GitHistoryTaken taken, CancellationToken cancellationToken = default)
        => _gitClient.TakeHistoryAsync(Home(root), taken, cancellationToken);

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
        => ListCopiesAsync(repositoryPath, HostCopies.WorktreeSuffix, HostCopies.IsCopyName, cancellationToken);

    /// <summary>
    /// The copies of <paramref name="family"/> kept beside <paramref name="root"/> whose names <paramref name="named"/>
    /// takes - each directory named <c>&lt;root&gt;&lt;family&gt;&lt;name&gt;</c> - as
    /// <see cref="ListCopiesAsync(string, CancellationToken)"/> lists a host's worktree copies, which are such a family:
    /// told by name, each weighed by its files, in the order of their names. A directory under any other name is none of
    /// the family's, and is not weighed; none is listed where the directory they would be kept in is not there.
    /// </summary>
    /// <param name="root">What the copies are kept beside: the main copy.</param>
    /// <param name="family">The family's suffix: <see cref="HostCopies.WorktreeSuffix"/>.</param>
    /// <param name="named">Whether what follows the suffix in a directory's name is the name of a copy to list.</param>
    /// <param name="cancellationToken">Stops the listing between copies.</param>
    public Task<IReadOnlyList<HostCopyFound>> ListCopiesAsync(string root, string family, Func<string, bool> named, CancellationToken cancellationToken = default)
        => Task.FromResult(Weighed(CopiesBeside(root, family, named), cancellationToken));

    /// <summary>
    /// The copies kept in the slot <paramref name="slot"/> of the tree at <paramref name="tree"/> whose names
    /// <paramref name="named"/> takes - each directory <c>&lt;tree&gt;/&lt;slot&gt;/&lt;name&gt;</c> - told by name, each
    /// weighed by its files, in the order of their names. None where the tree keeps no such slot.
    /// </summary>
    /// <param name="tree">The tree the copies are of, as the configuration spells it.</param>
    /// <param name="slot">The slot: <see cref="HarnessLayout.MutationsDirectoryName"/>.</param>
    /// <param name="named">Whether a directory's name in the slot is the name of a copy to list.</param>
    /// <param name="cancellationToken">Stops the listing between copies.</param>
    public Task<IReadOnlyList<HostCopyFound>> ListInSlotAsync(string tree, string slot, Func<string, bool> named, CancellationToken cancellationToken = default)
        => Task.FromResult(Weighed(CopiesInSlot(tree, slot, named), cancellationToken));

    /// <summary>Each of <paramref name="copies"/>, weighed, with what its marker says.</summary>
    private IReadOnlyList<HostCopyFound> Weighed(IReadOnlyList<(string Name, string Path, string Directory)> copies, CancellationToken cancellationToken)
    {
        var found = new List<HostCopyFound>();

        foreach (var copy in copies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            found.Add(Found(copy.Name, copy.Path, copy.Directory));
        }

        return found;
    }

    /// <summary>
    /// The copies of <paramref name="family"/> kept beside <paramref name="root"/> whose names <paramref name="named"/>
    /// takes, in the order of their names, as <see cref="ListCopiesAsync(string, string, Func{string, bool}, CancellationToken)"/>
    /// finds them - but none weighed: only where each is, which costs one look at the directory they are kept in.
    /// </summary>
    /// <param name="root">What the copies are kept beside, as the configuration spells it.</param>
    /// <param name="family">The family's suffix.</param>
    /// <param name="named">Whether what follows the suffix in a directory's name is the name of a copy to list.</param>
    /// <returns>Each copy's name, where it is as the configuration spells it, and where it is as this machine does.</returns>
    /// <exception cref="IOException">The directory they are kept in could not be looked in.</exception>
    /// <exception cref="UnauthorizedAccessException">The directory they are kept in could not be looked in.</exception>
    public IReadOnlyList<(string Name, string Path, string Directory)> CopiesBeside(string root, string family, Func<string, bool> named)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(family);

        return CopiesIn(Beside(root, family), named);
    }

    /// <summary>
    /// The copies kept in the slot <paramref name="slot"/> of <paramref name="tree"/> whose names <paramref name="named"/>
    /// takes, in the order of their names - but none weighed.
    /// </summary>
    /// <param name="tree">The tree the copies are of, as the configuration spells it.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="named">Whether a directory's name in the slot is the name of a copy to list.</param>
    /// <returns>Each copy's name, where it is as the configuration spells it, and where it is as this machine does.</returns>
    /// <exception cref="IOException">The slot could not be looked in.</exception>
    /// <exception cref="UnauthorizedAccessException">The slot could not be looked in.</exception>
    public IReadOnlyList<(string Name, string Path, string Directory)> CopiesInSlot(string tree, string slot, Func<string, bool> named)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);

        return CopiesIn(InSlot(tree, slot), named);
    }

    /// <summary>The copies kept at <paramref name="place"/> whose names <paramref name="named"/> takes, in the order of their names; none where it is not there.</summary>
    private IReadOnlyList<(string Name, string Path, string Directory)> CopiesIn(CopyPlace? place, Func<string, bool> named)
    {
        ArgumentNullException.ThrowIfNull(named);

        if (place is not { } kept)
        {
            return [];
        }

        var found = new List<(string Name, string Path, string Directory)>();

        foreach (var directory in _fileSystem.EnumerateDirectories(kept.Directory))
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));

            if (leaf.Length > kept.Prefix.Length && leaf.StartsWith(kept.Prefix, StringComparison.Ordinal) && named(leaf[kept.Prefix.Length..]))
            {
                var name = leaf[kept.Prefix.Length..];
                found.Add((name, kept.PathOf(name), directory));
            }
        }

        return [.. found.OrderBy(copy => copy.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Where a family of copies is kept on this machine: the directory, what the name of each starts with there, and
    /// where a copy kept under a name is as the configuration spells it.
    /// </summary>
    /// <param name="Directory">The directory the copies are in, as this machine spells it.</param>
    /// <param name="Prefix">What the name of each starts with, before the name it is kept under.</param>
    /// <param name="PathOf">Where the copy kept under a name is, as the configuration spells it.</param>
    private sealed record CopyPlace(string Directory, string Prefix, Func<string, string> PathOf);

    /// <summary>
    /// The trees - there still or gone - beside which an earlier version of this tool kept the workers in
    /// <paramref name="directory"/>: the tree each directory there named <c>&lt;tree&gt;.mutation-&lt;worker&gt;</c>, or
    /// named as what an unfinished removal of one left aside, was kept beside, where <paramref name="treeNamed"/> takes
    /// that tree's name; each once, in the order of their names. None where the directory is not there.
    /// </summary>
    /// <param name="directory">Where trees of the repository are, or were, kept.</param>
    /// <param name="treeNamed">Whether a tree there by that name is one of the repository's.</param>
    /// <remarks>
    /// So the workers beside a tree deleted since - a worktree, or a host's copy of one - are found as those beside one
    /// still there are, and removed by <see cref="RemoveFormerWorkersAsync"/> as theirs.
    /// </remarks>
    public IReadOnlyList<string> FormerWorkerTrees(string directory, Func<string, bool> treeNamed)
    {
        ArgumentNullException.ThrowIfNull(treeNamed);

        var kept = Home(directory);

        if (!_fileSystem.DirectoryExists(kept))
        {
            return [];
        }

        return
        [
            .. _fileSystem.EnumerateDirectories(kept)
                .Select(found => Path.GetFileName(Path.TrimEndingDirectorySeparator(found)))
                .Select(name => RemovalAside.Was(name) ?? name)
                .Select(name => (Name: name, At: name.LastIndexOf(Mutations.MutationWorkers.FormerFamily, StringComparison.Ordinal)))
                .Where(found => found.At > 0
                    && _claims.Names(found.Name[(found.At + Mutations.MutationWorkers.FormerFamily.Length)..])
                    && treeNamed(found.Name[..found.At]))
                .Select(found => found.Name[..found.At])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(tree => Path.Combine(directory, tree)),
        ];
    }

    /// <summary>
    /// Where the copies of <paramref name="family"/> beside <paramref name="root"/> are kept: the directory the root is in,
    /// each named with the root's own name followed by the family's suffix. Or <see langword="null"/> where that directory
    /// is not there, which keeps none.
    /// </summary>
    /// <param name="root">What the copies are kept beside.</param>
    /// <param name="family">The family's suffix.</param>
    private CopyPlace? Beside(string root, string family)
    {
        var main = Path.TrimEndingDirectorySeparator(Home(root));
        var parent = Path.GetDirectoryName(main);

        return string.IsNullOrEmpty(parent) || !_fileSystem.DirectoryExists(parent)
            ? null
            : new CopyPlace(parent, Path.GetFileName(main) + family, name => HostCopies.InFamily(root, family, name));
    }

    /// <summary>
    /// Where the copies kept in the slot <paramref name="slot"/> of <paramref name="tree"/> are: the slot, each named as
    /// it is kept. Or <see langword="null"/> where the tree keeps no such slot, which holds none.
    /// </summary>
    /// <param name="tree">The tree the copies are of.</param>
    /// <param name="slot">The slot.</param>
    private CopyPlace? InSlot(string tree, string slot)
    {
        var directory = Path.Combine(Home(tree), slot);

        return _fileSystem.DirectoryExists(directory)
            ? new CopyPlace(directory, string.Empty, name => HarnessLayout.InSlot(tree, slot, name))
            : null;
    }

    /// <summary>Why a worker a removal was stopped in was left, as its line says it.</summary>
    internal const string StoppedPartWay = "its removal was stopped part way, and asking again finishes it";

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// Each worker is removed as a copy is, by its marker, so one a removal stopped in is still the harness's to remove
    /// when asked again. What a removal left aside (<see cref="RemovalAside"/>) was a worker a sync made, renamed with
    /// nothing sweeping in it, and goes whatever it still holds. Only the tree's own slot is looked in, and the slot of
    /// each copy kept in its .legs that a sync made: nothing of the harness's is kept beside a tree, and a sweep another
    /// machine sent here works in a copy of its own kept there (<see cref="Legs.LegWorkload.CopyOnHosts"/>), with its
    /// workers in it - so a removal of the tree meets each of them, and the claim on one a live sweep holds keeps it.
    /// </para>
    /// <para>
    /// Everything is listed before anything goes, so a listing that fails has removed nothing - and is raised naming
    /// the directory that could not be looked in, as a failure of the harness's, since for every host but this one only
    /// that message comes back; and from then on each worker answers for itself. One that cannot be removed - something
    /// holds a file of it, its marker cannot be read by now - is left, saying why, and the others still go; and a removal
    /// stopped before every worker was dealt with answers with what it had done rather than throwing it away, since a
    /// worker gone is one nothing else would say went.
    /// </para>
    /// </remarks>
    public async Task<WorkersRemoval> RemoveWorkersAsync(string root, bool measureOnly = false, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HostCopyFound> copies;

        // Listed before anything goes, as the workers of each are, so a listing that fails has removed nothing.
        try
        {
            copies = await ListInSlotAsync(root, HarnessLayout.LegsDirectoryName, Legs.LegCopies.IsKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw WorktreeInspector.CouldNotBeLookedIn(Path.Combine(Home(root), HarnessLayout.LegsDirectoryName), ex);
        }

        var removal = await RemoveWorkersInTreeAsync(root, measureOnly, cancellationToken).ConfigureAwait(false);

        foreach (var copy in copies.Where(copy => copy.Origin == CopyOrigin.Made))
        {
            if (removal.Interrupted)
            {
                break;
            }

            var inCopy = await RemoveWorkersInTreeAsync(copy.Path, measureOnly, cancellationToken).ConfigureAwait(false);

            removal = removal.And(inCopy);
        }

        return removal;
    }

    /// <summary>
    /// Removes the mutation workers kept in the tree at <paramref name="root"/>'s own slot alone, as
    /// <see cref="RemoveWorkersAsync"/> removes them - not those in the copies kept in its .legs: for a removal that removes
    /// each of those copies whole, under the lock a sweep in one holds it by, rather than removing the tree's copy whole.
    /// </summary>
    /// <param name="root">The tree.</param>
    /// <param name="measureOnly">Whether to say what would be removed, and what left, and remove nothing.</param>
    /// <param name="cancellationToken">Stops the removal between workers.</param>
    public Task<WorkersRemoval> RemoveWorkersInTreeAsync(string root, bool measureOnly = false, CancellationToken cancellationToken = default)
        => RemoveInSlotAsync(root, HarnessLayout.MutationsDirectoryName, _claims, measureOnly, cancellationToken);

    /// <summary>
    /// Removes the copies kept in the slot <paramref name="slot"/> of the tree at <paramref name="tree"/>, with what an
    /// unfinished removal of one left aside - or, asked only to measure, says what it would remove and what it would
    /// leave, and removes nothing. The one removal there is of copies kept in a tree, whichever slot they are in:
    /// <see cref="RemoveWorkersInTreeAsync"/> is this, of the mutation workers, by the claims the transport was built with.
    /// </summary>
    /// <param name="tree">The tree the copies are of.</param>
    /// <param name="slot">The slot they are kept in: <see cref="HarnessLayout.MutationsDirectoryName"/>.</param>
    /// <param name="claims">The family's claims: what a copy of it is named, which run holds one, and how that is said.</param>
    /// <param name="measureOnly">Whether to say what would be removed, and what left, and remove nothing.</param>
    /// <param name="cancellationToken">Stops the removal between copies.</param>
    public Task<WorkersRemoval> RemoveInSlotAsync(string tree, string slot, ICopyClaims claims, bool measureOnly = false, CancellationToken cancellationToken = default)
        => RemoveInAsync(InSlot(tree, slot), Path.Combine(Home(tree), slot), claims, measureOnly, cancellationToken);

    /// <summary>
    /// Removes the workers an earlier version of this tool kept beside the tree at <paramref name="tree"/>, each named
    /// <c>&lt;tree&gt;.mutation-&lt;key&gt;&lt;mark&gt;-&lt;n&gt;</c> (<see cref="Mutations.MutationWorkers.FormerFamily"/>), as
    /// the workers kept in it are removed (<see cref="RemoveInSlotAsync"/>): each by its marker, one that version's sweep
    /// still claims, by the claim file beside it that version wrote, left - or, asked only to measure, says what it would
    /// remove and what it would leave.
    /// </summary>
    /// <param name="tree">The tree they were kept beside.</param>
    /// <param name="measureOnly">Whether to say what would be removed, and what left, and remove nothing.</param>
    /// <param name="cancellationToken">Stops the removal between workers.</param>
    public Task<WorkersRemoval> RemoveFormerWorkersAsync(string tree, bool measureOnly = false, CancellationToken cancellationToken = default)
        => RemoveInAsync(
            Beside(tree, Mutations.MutationWorkers.FormerFamily),
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Home(tree))) ?? Home(tree),
            _claims,
            measureOnly,
            cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Every tree beside which workers are kept there is listed before any worker goes, so a listing that fails has
    /// removed nothing; the trees are found by the workers' own names (<see cref="FormerWorkerTrees"/>), so a copy
    /// removed since - an agent's, deleted by a version that removed only the workers kept in it - is among them.
    /// </remarks>
    public async Task<WorkersRemoval> RemoveFormerWorkersBesideCopiesAsync(string repositoryPath, bool measureOnly = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var main = Path.TrimEndingDirectorySeparator(Home(repositoryPath));
        var name = Path.GetFileName(main);
        var worktrees = name + HostCopies.WorktreeSuffix;
        IReadOnlyList<string> trees;

        try
        {
            trees = Path.GetDirectoryName(main) is { Length: > 0 } parent
                ? FormerWorkerTrees(
                    parent,
                    tree => string.Equals(tree, name, _platform.PathComparison)
                        || (tree.StartsWith(worktrees, _platform.PathComparison) && HostCopies.IsCopyName(tree[worktrees.Length..])))
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw WorktreeInspector.CouldNotBeLookedIn(Path.GetDirectoryName(main) ?? main, ex);
        }

        var removal = WorkersRemoval.None;

        foreach (var tree in trees)
        {
            removal = removal.And(await RemoveFormerWorkersAsync(tree, measureOnly, cancellationToken).ConfigureAwait(false));

            if (removal.Interrupted)
            {
                break;
            }
        }

        return removal;
    }

    /// <summary>
    /// Removes the copies kept at <paramref name="place"/> whose names <paramref name="claims"/> takes, with what an
    /// unfinished removal of one left aside - or measures them: the one removal of copies kept together, wherever that is.
    /// </summary>
    /// <param name="place">Where they are kept; <see langword="null"/> where that is not there, which keeps none.</param>
    /// <param name="lookedIn">The directory, as a listing that fails names it.</param>
    /// <param name="claims">The family's claims: what a copy of it is named, which run holds one, and how that is said.</param>
    /// <param name="measureOnly">Whether to say what would be removed, and what left, and remove nothing.</param>
    /// <param name="cancellationToken">Stops the removal between copies.</param>
    private async Task<WorkersRemoval> RemoveInAsync(CopyPlace? place, string lookedIn, ICopyClaims claims, bool measureOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claims);

        var removed = new List<WorkerRemoved>();
        var left = new List<WorkerLeft>();

        try
        {
            IReadOnlyList<HostCopyFound> copies;
            IReadOnlyList<string> asides;

            try
            {
                copies = Weighed(CopiesIn(place, claims.Names), cancellationToken);
                asides = AsidesIn(place, claims.Names);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw WorktreeInspector.CouldNotBeLookedIn(lookedIn, ex);
            }

            foreach (var copy in copies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (copy.Origin != CopyOrigin.Made)
                {
                    // One whose marker cannot be read is not told from one a sync made: left as not removed, which keeps
                    // its tree until it can be told, where somebody's keeps nothing.
                    left.Add(new WorkerLeft(
                        copy.Path,
                        WorktreeReports.Origin(copy),
                        copy.Origin == CopyOrigin.Unreadable ? WorkerLeftAs.NotRemoved : WorkerLeftAs.Somebodys));
                    continue;
                }

                // Asked about where this machine keeps the worker, which is where the sweep that holds it claimed it: the
                // path the caller spelt may start from a home only this machine expands, and names another file as it stands.
                var kept = Home(copy.Path);

                if (claims.HeldBy(kept) is { } holder)
                {
                    left.Add(new WorkerLeft(copy.Path, claims.SaidHeld(holder.Said), WorkerLeftAs.Held, holder.Claim));
                    continue;
                }

                if (measureOnly)
                {
                    removed.Add(new WorkerRemoved(copy.Path, copy.Bytes));
                    continue;
                }

                CopyRemoval removal;

                try
                {
                    removal = await RemoveCopyAsync(copy.Path, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Its marker goes last, so what is left of it is still the harness's to remove.
                    left.Add(new WorkerLeft(copy.Path, StoppedPartWay, WorkerLeftAs.NotRemoved));
                    throw;
                }
                catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
                {
                    left.Add(new WorkerLeft(copy.Path, ex.Message, WorkerLeftAs.NotRemoved));
                    continue;
                }

                switch (removal)
                {
                    // Its marker is read again as it is removed, and may say otherwise by then: a directory left where
                    // it is is said as that, never as removed, and the claim on it stays.
                    case CopyRemoval.Adopted or CopyRemoval.NotACopy:
                        var origin = removal == CopyRemoval.Adopted ? CopyOrigin.TakenOver : CopyOrigin.Unmarked;

                        left.Add(new WorkerLeft(copy.Path, WorktreeReports.Origin(copy with { Origin = origin }), WorkerLeftAs.Somebodys));
                        break;

                    // Gone by now: another command took it meanwhile, and says so. This one took nothing of it.
                    case CopyRemoval.Absent:
                        claims.Forget(kept);
                        break;

                    default:
                        claims.Forget(kept);
                        removed.Add(new WorkerRemoved(copy.Path, copy.Bytes));
                        break;
                }
            }

            foreach (var aside in asides)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long bytes;

                try
                {
                    bytes = _fileSystem.DirectorySize(aside);

                    if (!measureOnly)
                    {
                        _fileSystem.DeleteDirectory(aside);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left.Add(new WorkerLeft(aside, $"what a removal left aside could not be {(measureOnly ? "measured" : "removed")}: {ex.Message}", WorkerLeftAs.NotRemoved));
                    continue;
                }

                removed.Add(new WorkerRemoved(aside, bytes));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WorkersRemoval(removed, left) { Interrupted = true };
        }

        return new WorkersRemoval(removed, left);
    }

    /// <summary>
    /// What an unfinished removal of the copies kept in the slot <paramref name="slot"/> of <paramref name="tree"/> left
    /// aside: each directory in the slot named as the aside (<see cref="RemovalAside"/>) of a copy whose name
    /// <paramref name="named"/> takes, in the order of their names. Told by name alone, as nothing else is left to tell
    /// one by: so only what is spelt as a copy of the slot's is one, and a directory named any other way is nobody's to
    /// remove.
    /// </summary>
    /// <param name="tree">The tree the copies are of.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="named">Whether a name in the slot is the name of a copy the caller removes.</param>
    public IReadOnlyList<string> AsidesInSlot(string tree, string slot, Func<string, bool> named)
        => AsidesIn(InSlot(tree, slot), named);

    /// <summary>
    /// What an unfinished removal of the copies kept at <paramref name="place"/> left aside, as
    /// <see cref="AsidesInSlot"/> finds them in a slot: each directory there named as the aside of a copy of the place
    /// whose name <paramref name="named"/> takes, in the order of their names.
    /// </summary>
    private IReadOnlyList<string> AsidesIn(CopyPlace? place, Func<string, bool> named)
    {
        ArgumentNullException.ThrowIfNull(named);

        if (place is not { } kept)
        {
            return [];
        }

        return
        [
            .. _fileSystem.EnumerateDirectories(kept.Directory)
                .Where(directory => RemovalAside.Was(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))) is { } was
                    && was.Length > kept.Prefix.Length
                    && was.StartsWith(kept.Prefix, StringComparison.Ordinal)
                    && named(was[kept.Prefix.Length..]))
                .Order(StringComparer.Ordinal),
        ];
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

    /// <inheritdoc/>
    /// <remarks>
    /// The directory named is walked as it is spelt, a link along its path followed as a file's is: a build directory
    /// pointed at another volume is ordinary. Below it no link is followed, and each is named.
    /// </remarks>
    public Task<SyncDirectoryListing> ListFilesAsync(string root, string relativeDirectory, CancellationToken cancellationToken = default)
    {
        var named = relativeDirectory.Replace('\\', '/').TrimEnd('/') + "/";

        try
        {
            return Task.FromResult(ListFiles(root, relativeDirectory, named, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory this user may not read, or one that changed under the walk: the pull's failure, by name. Left
            // to the runtime it ended the host's command as a defect of this tool, the path inside the system's words.
            throw new HarnessException(HarnessExit.CommandFailed, $"'{named}' could not be listed in '{root}': {ex.Message}", ex);
        }
    }

    private SyncDirectoryListing ListFiles(string root, string relativeDirectory, string named, CancellationToken cancellationToken)
    {
        var expanded = Home(root);
        var directory = Resolve(root, relativeDirectory);

        if (!_fileSystem.DirectoryExists(directory))
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                _fileSystem.FileExists(directory)
                    ? $"'{named}' names a directory, and a file is at that path in '{root}': name it without the separator at its end."
                    : $"'{named}' is not in '{root}': nothing is at that path there.");
        }

        var most = SyncServe.MostFilesPulledFromADirectory;

        // One more than may cross, and no further: a directory holding a build tree is refused without every file of it
        // being listed.
        var walked = _fileSystem.EnumerateWrittenFiles(directory).Take(most + 1).ToList();

        cancellationToken.ThrowIfCancellationRequested();

        if (walked.Count > most)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{named}' holds more than {most.ToString(CultureInfo.InvariantCulture)} files in '{root}', the most a pull brings back from one "
                + "directory, each in a request of its own: name a directory that holds fewer, or the files, or keep them as one archive.");
        }

        var files = new List<SyncListedFile>();
        var links = new List<string>();

        foreach (var file in walked)
        {
            var relative = ManifestBuilder.Relative(expanded, file.Path);

            if (_fileSystem.IsLink(file.Path))
            {
                links.Add(relative);
            }
            else
            {
                files.Add(new SyncListedFile(relative, file.Length));
            }
        }

        links.AddRange(_fileSystem.EnumerateDirectoryLinks(directory).Select(link => ManifestBuilder.Relative(expanded, link) + "/"));

        if (files.FirstOrDefault(file => file.Length > SyncServe.LargestFile) is { } large)
        {
            throw new HarnessException(HarnessExit.CommandFailed, SyncServe.TooLargeToCarry(large.Length, large.Path, "this host"));
        }

        if (files.Sum(file => file.Length) is var bytes && bytes > SyncServe.LargestDirectoryPulled)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{named}' holds {bytes.ToString(CultureInfo.InvariantCulture)} bytes in '{root}', past the "
                + $"{SyncServe.LargestDirectoryPulled.ToString(CultureInfo.InvariantCulture)} a pull brings back from one directory: name a directory "
                + "that holds less, or the files, or keep the smaller thing a later step actually reads.");
        }

        return new SyncDirectoryListing([.. files.OrderBy(file => file.Path, StringComparer.Ordinal)])
        {
            Links = [.. links.Order(StringComparer.Ordinal)],
        };
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
    /// said by <c>Unfinished</c>, never here, so this member means one thing to every build that reads it.
    /// </param>
    /// <param name="Unfinished">
    /// Whether a sync began writing it and has not finished: written before a sync's first write, and
    /// cleared once the copy is verified. Written only where true, so a finished copy's marker never says it.
    /// </param>
    /// <param name="ReadBy">The run whose reading a finished sync made the copy, where it kept one.</param>
    /// <param name="Commit">The commit that reading's tree was at, where it was at one.</param>
    /// <param name="UncommittedChanges">
    /// How many of the paths that reading carried git called changed: written with every reading a finished sync
    /// records, so its presence says the marker holds one. Each of the three spelt as a leg's <c>tree</c> spells it,
    /// and written only where set, so a copy marked without a reading carries none of them.
    /// </param>
    /// <remarks>
    /// Both strings are nullable because deserialising decides that, not this declaration: a file
    /// holding <c>{}</c> parses into a marker with neither, and that is one of the shapes that has
    /// to be told from a marker this tool wrote. Every marker it has ever written carries both.
    /// <para>
    /// A build from before this one cannot read the marker of a copy this one finished: the reading it holds is a
    /// member that build does not know, which it refuses as unreadable, as a build from before <c>Unfinished</c>
    /// refuses an unfinished copy's. Only going back to such a build meets that - a host is brought to the build of
    /// the machine that asks it, and one ahead of that machine is refused - and the remedy is to update again, never
    /// to delete the marker, which would leave the copy one to take over.
    /// </para>
    /// </remarks>
    private sealed record SyncedCopyMarker(
        string? CreatedUtc,
        string? CreatedBy,
        bool Adopted,
        bool Completed,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Unfinished = false,
        [property: JsonPropertyName("readBy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReadBy = null,
        [property: JsonPropertyName("commit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Commit = null,
        [property: JsonPropertyName("uncommittedChanges"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? UncommittedChanges = null);

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
            marker = JsonStateFile.Read<SyncedCopyMarker>(_fileSystem, path, MarkerOptions);
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
