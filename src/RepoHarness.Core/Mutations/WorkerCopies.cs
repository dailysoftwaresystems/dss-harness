using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>One worker's copy, as a listing of the copies beside a tree finds it.</summary>
/// <param name="Family">Its family: its variant's, or its self-test's.</param>
/// <param name="Number">The worker's number.</param>
/// <param name="Found">The copy as the listing found it: where it is, what its files hold, and what its marker says.</param>
public sealed record WorkerCopy(WorkerFamily Family, int Number, HostCopyFound Found)
{
    /// <summary>Where the copy is.</summary>
    public string Path => Found.Path;

    /// <summary>What its files hold together, its build directory's among them.</summary>
    public long Bytes => Found.Bytes;

    /// <summary>
    /// Whether its marker says a sync made it: a directory under a worker's name that none did is somebody's, which no sweep
    /// syncs into and nothing removes.
    /// </summary>
    public bool Made => Found.Origin == CopyOrigin.Made;
}

/// <summary>
/// The copies a leg's sweep keeps its workers in: listed, claimed for one sweep at a time, made or synced again by
/// content from the sweep's one reading of the tree, and removed.
/// </summary>
internal interface IWorkerCopies
{
    /// <summary>The workers of <paramref name="family"/>, by number.</summary>
    /// <param name="family">The family: a variant's workers beside a tree, or its self-test's.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    Task<IReadOnlyList<WorkerCopy>> ListAsync(WorkerFamily family, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the claim on <paramref name="worker"/> a sweep on this machine died holding, and says so;
    /// <see langword="null"/>, and nothing done, where none did.
    /// </summary>
    /// <param name="worker">The worker's copy.</param>
    LogOwner? ReleaseAbandoned(string worker);

    /// <summary>Claims <paramref name="worker"/> for <paramref name="runId"/>, refusing one a live sweep holds unless <paramref name="force"/>.</summary>
    /// <param name="worker">The worker's copy.</param>
    /// <param name="runId">The run sweeping.</param>
    /// <param name="force">Whether to take one a live sweep holds.</param>
    LogClaim Claim(string worker, RunId runId, bool force);

    /// <summary>Gives up <paramref name="worker"/>, where <paramref name="runId"/> holds it.</summary>
    /// <param name="worker">The worker's copy.</param>
    /// <param name="runId">The run that swept.</param>
    void Release(string worker, RunId runId);

    /// <summary>
    /// The sweep that holds <paramref name="worker"/> and still runs, as a refusal names it; <see langword="null"/> where
    /// none does. A claim that cannot be read holds its worker, and is said as that.
    /// </summary>
    /// <param name="worker">The worker's copy.</param>
    string? HeldBy(string worker);

    /// <summary>
    /// Makes <paramref name="worker"/> the tree <paramref name="source"/> read: a new copy, or one an earlier sweep left
    /// synced again by content, which puts back any site a sweep killed part way left mutated.
    /// </summary>
    /// <param name="source">The sweep's one reading of the tree.</param>
    /// <param name="worker">The worker's copy.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    Task SyncAsync(SyncSource source, string worker, CancellationToken cancellationToken);

    /// <summary>
    /// How many bytes of the tree's git history a worker made new from <paramref name="source"/> keeps: the commit the
    /// tree was at, and those behind it where <c>sync.history</c> asks for them. Packed to be counted, once for every
    /// worker the reading makes; nothing where the reading names no commit.
    /// </summary>
    /// <param name="source">The sweep's one reading of the tree.</param>
    /// <param name="cancellationToken">Stops the packing.</param>
    Task<long> HistoryBytesAsync(SyncSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the dependency sources at <paramref name="directory"/> as each worker is given them, once for every
    /// worker: every file by size and hash, never a clone's own <c>.git</c>.
    /// </summary>
    /// <param name="directory">Where the leg's own build has them.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="HarnessException">They are gone, or changed while they were read (<see cref="LegExit.InputsMoved"/>).</exception>
    Task<SyncManifest> ReadFetchedAsync(string directory, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the dependency sources <paramref name="worker"/> keeps those <paramref name="fetched"/> read, each by
    /// content under its dependency's name (<see cref="FetchedSources.KeptIn(string, string)"/>), and removes what it
    /// keeps of any other: a dependency the leg's build no longer declares, or one the worker now has in its own copy
    /// of the tree. Sources holding no file a copy carries are an empty directory all the same, since the worker is
    /// configured with it.
    /// </summary>
    /// <param name="fetched">The sweep's one reading of each dependency's sources.</param>
    /// <param name="worker">The worker's copy.</param>
    /// <param name="cancellationToken">Stops the sync.</param>
    /// <exception cref="HarnessException">
    /// A file no longer holds what was read of it (<see cref="LegExit.InputsMoved"/>): what the worker keeps is left
    /// part made, until a sweep syncs it again.
    /// </exception>
    Task SyncFetchedAsync(IReadOnlyList<FetchedReading> fetched, string worker, CancellationToken cancellationToken);

    /// <summary>Removes <paramref name="worker"/>'s copy and its claim, where the copy is one a sweep made.</summary>
    /// <param name="worker">The worker's copy.</param>
    /// <param name="cancellationToken">Stops the removal.</param>
    Task<CopyRemoval> RemoveAsync(string worker, CancellationToken cancellationToken);
}

/// <summary>
/// The claims sweeps take on their workers' copies, as whatever would remove a worker asks first: the one place that says
/// whether a worker is in use.
/// </summary>
/// <param name="fileSystem">Reads the claims, and removes one whose copy is gone.</param>
/// <param name="output">Where a claim's own lines go.</param>
/// <param name="identity">Tells a sweep still running from one that has ended.</param>
/// <param name="commandName">The command the lines report under.</param>
internal sealed class WorkerClaims(IFileSystem fileSystem, IHarnessOutput output, IProcessIdentity identity, string commandName) : ICopyClaims
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHarnessOutput _output = output;
    private readonly IProcessIdentity _identity = identity;
    private readonly string _commandName = commandName;

    /// <summary>The claims themselves, in a worker's terms.</summary>
    public DirectoryClaims Claims { get; } = new(fileSystem, output, identity, MutationWorkers.Claims(commandName));

    /// <inheritdoc/>
    public bool Names(string name) => MutationWorkers.Named(name) is not null;

    /// <inheritdoc/>
    public string? HeldBy(string copy)
    {
        try
        {
            return Standing(copy)?.Describe(_identity);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            // A claim that cannot be read says a sweep claimed this worker and nothing more: it holds the worker, naming
            // its file, and is never the end of whatever asked - a clean of the leg, or a sweep removing a worker it no
            // longer runs.
            return ex.Message;
        }
    }

    /// <inheritdoc/>
    public void Forget(string copy)
    {
        try
        {
            // A sweep claims a worker before it makes its copy, so one that began while the copy was being removed holds
            // a claim on a copy that is gone, and is about to make it again: its claim is its own, and stays.
            if (Standing(copy) is not null)
            {
                return;
            }
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            // One that cannot be read says nothing of anybody once its copy is gone, and goes with it.
        }

        var file = Claims.OwnerFile(copy);

        try
        {
            if (_fileSystem.FileExists(file))
            {
                _fileSystem.DeleteFile(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Its copy is gone, so it claims nothing: said, and never the failure of the removal that went through.
            _output.Warn(_commandName, $"The claim file '{file}' of a mutation worker that was removed could not be removed, and is yours to remove: {ex.Message}");
        }
    }

    /// <summary>The sweep still running that claims <paramref name="copy"/>, or <see langword="null"/> where none does.</summary>
    /// <exception cref="HarnessException">Its claim cannot be read.</exception>
    private LogOwner? Standing(string copy) => Claims.Owner(copy) is { } owner && Claims.Stands(owner) ? owner : null;
}

/// <inheritdoc cref="IWorkerCopies"/>
/// <remarks>
/// Made and synced by the sync that makes a host's copy, through this machine's own transport: so a worker's copy is
/// carried, withheld and verified by the very rules a host's copy is, staged in its own git index for the build's guards,
/// and listed and removed as a family of copies of its own. Claimed by the claim a run takes on its log directory, in a
/// worker's terms, which never makes the copy it claims: a sync refuses to make a copy where a directory it did not make
/// is there already.
/// </remarks>
internal sealed class WorkerCopies(
    ISyncService syncService,
    LocalSyncTransport transport,
    IFileSystem fileSystem,
    IHarnessOutput output,
    IProcessIdentity identity,
    string commandName) : IWorkerCopies
{
    private readonly ISyncService _syncService = syncService;
    private readonly LocalSyncTransport _transport = transport;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly WorkerClaims _claims = new(fileSystem, output, identity, commandName);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WorkerCopy>> ListAsync(WorkerFamily family, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(family);

        return [.. (await ListBesideAsync(family.TreeRoot, name => string.Equals(name, family.Name, StringComparison.Ordinal), cancellationToken).ConfigureAwait(false))];
    }

    /// <summary>
    /// The workers kept beside <paramref name="treeRoot"/> in each family <paramref name="families"/> takes by its name -
    /// a variant's, or a self-test's - by family and then by number. A directory of the mutation family spelt as no
    /// worker is none, and one of a family not taken is not weighed.
    /// </summary>
    /// <param name="treeRoot">The tree the workers are kept beside.</param>
    /// <param name="families">Whether a family, by the name its workers are kept under, is among those listed.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    public async Task<IReadOnlyList<WorkerCopy>> ListBesideAsync(string treeRoot, Func<string, bool> families, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(families);

        var found = await _transport
            .ListCopiesAsync(treeRoot, HostCopies.MutationSuffix, name => MutationWorkers.Named(name) is { } worker && families(worker.Family), cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. found
                .Select(copy => (Copy: copy, Worker: MutationWorkers.Named(treeRoot, copy.Name)!.Value))
                .Select(pair => new WorkerCopy(pair.Worker.Family, pair.Worker.Number, pair.Copy))
                .OrderBy(copy => copy.Family.Name, StringComparer.Ordinal)
                .ThenBy(copy => copy.Number),
        ];
    }

    /// <inheritdoc/>
    public LogOwner? ReleaseAbandoned(string worker) => _claims.Claims.ReleaseAbandoned(worker);

    /// <inheritdoc/>
    public LogClaim Claim(string worker, RunId runId, bool force) => _claims.Claims.Claim(worker, runId, force);

    /// <inheritdoc/>
    public void Release(string worker, RunId runId) => _claims.Claims.Release(worker, runId);

    /// <inheritdoc/>
    public string? HeldBy(string worker) => _claims.HeldBy(worker);

    /// <inheritdoc/>
    public Task SyncAsync(SyncSource source, string worker, CancellationToken cancellationToken)
        => _syncService.SyncAsync(source, _transport, worker, new SyncOptions(), cancellationToken);

    /// <inheritdoc/>
    public Task<long> HistoryBytesAsync(SyncSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.NewCopyHistoryBytesAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task<SyncManifest> ReadFetchedAsync(string directory, CancellationToken cancellationToken)
        => _syncService.ReadDirectoryAsync(directory, cancellationToken);

    /// <inheritdoc/>
    public async Task SyncFetchedAsync(IReadOnlyList<FetchedReading> fetched, string worker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetched);

        var kept = FetchedSources.KeptIn(worker);

        // What it keeps of a dependency it is no longer given goes first, as a file the tree no longer has goes from its
        // copy: left, it would be room nothing counts, and sources nothing builds. Given none, it keeps none, and the
        // directory it kept them in goes with them.
        if (_fileSystem.DirectoryExists(kept))
        {
            var given = fetched
                .Select(read => Path.GetFileName(FetchedSources.KeptIn(worker, read.Source.Name)))
                .ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<string> stale = given.Count == 0
                ? [kept]
                : [.. _fileSystem.EnumerateDirectories(kept).Where(directory => !given.Contains(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory))))];

            foreach (var directory in stale)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _fileSystem.DeleteDirectory(directory);
            }
        }

        foreach (var read in fetched)
        {
            var directory = FetchedSources.KeptIn(worker, read.Source.Name);

            // Made even where no file is carried into it: the worker is configured with the directory either way, and a
            // configure pointed at one that is not there fails.
            _fileSystem.CreateDirectory(directory);
            await _syncService.SyncDirectoryAsync(read.Files, _transport, directory, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<CopyRemoval> RemoveAsync(string worker, CancellationToken cancellationToken)
    {
        var removal = await _transport.RemoveCopyAsync(worker, cancellationToken).ConfigureAwait(false);

        // The claim goes with the copy it claimed, and stays beside one that is not the harness's to remove.
        if (removal is CopyRemoval.Removed or CopyRemoval.Absent)
        {
            _claims.Forget(worker);
        }

        return removal;
    }
}
