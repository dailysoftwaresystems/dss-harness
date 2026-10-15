using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// The one reading of a tree a sweep makes: every worker of the leg is synced from it, so every arm is measured against
/// the same tree however long the sweep takes, and a file edited in the tree meanwhile reaches none of them.
/// </summary>
internal interface IMutationSource
{
    /// <summary>Reads <paramref name="treeRoot"/> as a sync carries it.</summary>
    /// <param name="treeRoot">The tree a leg sweeps.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="Results.HarnessException">The tree changed while it was read, or could not be read.</exception>
    Task<SyncSource> ReadAsync(string treeRoot, CancellationToken cancellationToken);
}

/// <summary>The tree a leg declares, read as a sync reads it for a host's copy.</summary>
internal sealed class TreeMutationSource(ISyncService syncService) : IMutationSource
{
    private readonly ISyncService _syncService = syncService;

    /// <inheritdoc/>
    public Task<SyncSource> ReadAsync(string treeRoot, CancellationToken cancellationToken)
        => _syncService.ReadSourceAsync(treeRoot, cancellationToken);
}
