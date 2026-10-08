using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>One worker's copy, as a listing of the copies beside a tree finds it.</summary>
/// <param name="Family">The name its family's workers are kept under: its variant's, or its self-test's.</param>
/// <param name="Number">The worker's number.</param>
/// <param name="Found">The copy as the listing found it: where it is, what its files hold, and what its marker says.</param>
public sealed record WorkerCopy(string Family, int Number, HostCopyFound Found)
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
    private readonly string _commandName = commandName;

    /// <summary>The claims themselves, in a worker's terms.</summary>
    public DirectoryClaims Claims { get; } = new(fileSystem, output, identity, MutationWorkers.Claims(commandName));

    /// <inheritdoc/>
    public string? HeldBy(string copy)
    {
        LogOwner? owner;

        try
        {
            owner = Claims.Owner(copy);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            // A claim that cannot be read says a sweep claimed this worker and nothing more: it holds the worker, naming
            // its file, and is never the end of whatever asked - a clean of the leg, or a sweep removing a worker it no
            // longer runs.
            return ex.Message;
        }

        return owner is not null && Claims.Stands(owner) ? owner.Describe() : null;
    }

    /// <inheritdoc/>
    public void Forget(string copy)
    {
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
                .Select(copy => (Copy: copy, Worker: MutationWorkers.Named(copy.Name)!.Value))
                .Select(pair => new WorkerCopy(pair.Worker.Family, pair.Worker.Number, pair.Copy))
                .OrderBy(copy => copy.Family, StringComparer.Ordinal)
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
