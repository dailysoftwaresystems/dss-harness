using RepoHarness.Core.Build;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>One worker's copy, as a listing of the copies beside a tree finds it.</summary>
/// <param name="Number">The worker's number.</param>
/// <param name="Found">The copy as the listing found it: where it is, what its files hold, and what its marker says.</param>
public sealed record WorkerCopy(int Number, HostCopyFound Found)
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
    /// <summary>The workers of <paramref name="variant"/> kept beside <paramref name="treeRoot"/>, by number.</summary>
    /// <param name="treeRoot">The tree the workers copy.</param>
    /// <param name="variant">The leg's variant.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    Task<IReadOnlyList<WorkerCopy>> ListAsync(string treeRoot, VariantKey variant, CancellationToken cancellationToken);

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

    /// <summary>The sweep that holds <paramref name="worker"/> and still runs, as a refusal names it; <see langword="null"/> where none does.</summary>
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

    /// <summary>Removes <paramref name="worker"/>'s copy and its claim, where the copy is one a sweep made.</summary>
    /// <param name="worker">The worker's copy.</param>
    /// <param name="cancellationToken">Stops the removal.</param>
    Task<CopyRemoval> RemoveAsync(string worker, CancellationToken cancellationToken);
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
    private readonly DirectoryClaims _claims = new(fileSystem, output, identity, MutationWorkers.Claims(commandName));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<WorkerCopy>> ListAsync(string treeRoot, VariantKey variant, CancellationToken cancellationToken)
    {
        var found = await _transport.ListCopiesAsync(treeRoot, HostCopies.MutationSuffix, cancellationToken).ConfigureAwait(false);

        return
        [
            .. found
                .Select(copy => (Copy: copy, Number: MutationWorkers.NumberOf(copy.Name, variant)))
                .Where(pair => pair.Number is not null)
                .Select(pair => new WorkerCopy(pair.Number!.Value, pair.Copy))
                .OrderBy(copy => copy.Number),
        ];
    }

    /// <inheritdoc/>
    public LogOwner? ReleaseAbandoned(string worker) => _claims.ReleaseAbandoned(worker);

    /// <inheritdoc/>
    public LogClaim Claim(string worker, RunId runId, bool force) => _claims.Claim(worker, runId, force);

    /// <inheritdoc/>
    public void Release(string worker, RunId runId) => _claims.Release(worker, runId);

    /// <inheritdoc/>
    public string? HeldBy(string worker)
        => _claims.Owner(worker) is { } owner && _claims.Stands(owner) ? owner.Describe() : null;

    /// <inheritdoc/>
    public Task SyncAsync(SyncSource source, string worker, CancellationToken cancellationToken)
        => _syncService.SyncAsync(source, _transport, worker, new SyncOptions(), cancellationToken);

    /// <inheritdoc/>
    public async Task<CopyRemoval> RemoveAsync(string worker, CancellationToken cancellationToken)
    {
        var removal = await _transport.RemoveCopyAsync(worker, cancellationToken).ConfigureAwait(false);

        // The claim goes with the copy it claimed, and stays beside one that is not the harness's to remove.
        if (removal is CopyRemoval.Removed or CopyRemoval.Absent && _fileSystem.FileExists(_claims.OwnerFile(worker)))
        {
            _fileSystem.DeleteFile(_claims.OwnerFile(worker));
        }

        return removal;
    }
}
