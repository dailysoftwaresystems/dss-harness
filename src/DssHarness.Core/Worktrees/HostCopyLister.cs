using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Worktrees;

/// <summary>The copies recorded of one tree on this machine, kept under one name.</summary>
/// <param name="Name">The name its copies are kept under.</param>
/// <param name="Tree">The tree, as a full path.</param>
/// <param name="Copies">Its copies, in the order they were recorded.</param>
public sealed record RecordedTree(string Name, string Tree, IReadOnlyList<HostCopyEntry> Copies);

/// <summary>Where a copy a host keeps stands with this machine's record.</summary>
public enum CopyStanding
{
    /// <summary>Recorded as the copy of a worktree the listing holds.</summary>
    Listed,

    /// <summary>Recorded as the copy of a tree still here, outside the worktrees root.</summary>
    Elsewhere,

    /// <summary>Recorded as the copy of a tree that is gone: deleting the name it is kept under deals with it.</summary>
    Gone,

    /// <summary>Not recorded here: made from another checkout or another machine, or forgotten here.</summary>
    Unrecorded,
}

/// <summary>One copy a host keeps, and where it stands with this machine's record.</summary>
/// <param name="Found">What the host said of it.</param>
/// <param name="Standing">Where it stands with the record.</param>
/// <param name="Tree">The tree the record says it is of, where the record holds it.</param>
public sealed record HostCopySeen(HostCopyFound Found, CopyStanding Standing, string? Tree);

/// <summary>What one declared host was asked about the worktree copies it keeps, and what it answered.</summary>
/// <param name="Host">The host.</param>
/// <param name="RepositoryPath">Where it keeps the main checkout's copy, beside which it keeps the rest.</param>
public sealed record HostCopiesAnswer(HostId Host, string RepositoryPath)
{
    /// <summary>Every copy it keeps there, in the order of their names.</summary>
    public IReadOnlyList<HostCopySeen> Copies { get; init; } = [];

    /// <summary>The copies recorded on it that it does not keep: deleting the name each is kept under forgets it.</summary>
    public IReadOnlyList<HostCopyEntry> Missing { get; init; } = [];

    /// <summary>Why it could not be asked, where it could not: what it keeps is then unknown.</summary>
    public string? Unasked { get; init; }

    /// <summary>
    /// What its not being asked leaves the listing with: <see cref="HarnessExit.HostUnavailable"/> where it could not
    /// be reached, and otherwise the code the asking failed with; success where it answered.
    /// </summary>
    public int ExitCode { get; init; }
}

/// <summary>
/// The copies of worktrees this machine records hosts holding, set against the worktrees there are, and, when the
/// hosts were asked, what each declared host keeps.
/// </summary>
public sealed record HostCopyListing
{
    /// <summary>Each listed worktree's recorded copies, by the worktree's name; one with none is not there.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<HostCopyEntry>> OfListed { get; init; } =
        new Dictionary<string, IReadOnlyList<HostCopyEntry>>(StringComparer.Ordinal);

    /// <summary>Trees still here, outside the worktrees root, with their copies.</summary>
    public IReadOnlyList<RecordedTree> Elsewhere { get; init; } = [];

    /// <summary>Trees that are gone, with the copies they left: deleting the name each is kept under deals with them.</summary>
    public IReadOnlyList<RecordedTree> Gone { get; init; } = [];

    /// <summary>What each declared host answered, or <see langword="null"/> where the hosts were not asked.</summary>
    public IReadOnlyList<HostCopiesAnswer>? Hosts { get; init; }

    /// <summary>
    /// Copies recorded on hosts no configuration here declares, which cannot be asked about; set only where the hosts
    /// were asked.
    /// </summary>
    public IReadOnlyList<HostCopyEntry> Undeclared { get; init; } = [];

    /// <summary>Why the record could not be read, where it could not: then nothing else here is known.</summary>
    public string? Unreadable { get; init; }

    /// <summary>
    /// What the listing ends with: refused where the record could not be read, and otherwise the highest code a host
    /// was left with, so that one answer stands for every host; each host's own line says what kept it.
    /// </summary>
    public int ExitCode => Unreadable is not null
        ? HarnessExit.Refused
        : Hosts?.Select(host => host.ExitCode).DefaultIfEmpty(HarnessExit.Success).Max() ?? HarnessExit.Success;
}

/// <summary>Lists the copies hosts keep of a repository's worktrees.</summary>
public interface IHostCopyLister
{
    /// <summary>
    /// The copies this machine records hosts holding - of the worktrees <paramref name="worktrees"/> lists, of trees
    /// still here outside the worktrees root, and of trees that are gone - and, where <paramref name="askHosts"/> is
    /// set, what each host the configuration declares keeps beside its repositoryPath, set against that record.
    /// </summary>
    /// <param name="startDirectory">A directory in the repository.</param>
    /// <param name="worktrees">The worktrees there are, as listing them found them.</param>
    /// <param name="askHosts">Whether to ask the hosts as well.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    /// <exception cref="HarnessException">The repository is not initialised.</exception>
    Task<HostCopyListing> ListAsync(
        string startDirectory,
        IReadOnlyList<WorktreeListing> worktrees,
        bool askHosts,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostCopyLister"/>
/// <remarks>
/// The record is what deleting a worktree acts on, so a copy it holds is one <c>delete-worktree</c> deals with, and
/// the name it is kept under is the name to give it. A copy a host keeps that the record does not hold was made from
/// another checkout or another machine, or forgotten here, and deleting a worktree never reaches it: asking the hosts
/// is how such a copy is found, and how a copy the record holds is found gone. Nothing here changes a host or the
/// record, so no lock is taken.
/// </remarks>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="inspector">Reaches a host, and makes sure the harness there can be asked.</param>
/// <param name="transports">Asks the harness on a host.</param>
/// <param name="fileSystem">Reads the record, and tells a tree that exists from one that is gone.</param>
/// <param name="platform">How this machine compares paths.</param>
public sealed class HostCopyLister(
    IHarnessContextLoader contextLoader,
    IHostInspector inspector,
    ISyncTransportFactory transports,
    IFileSystem fileSystem,
    IHostPlatform platform) : IHostCopyLister
{
    private static readonly IReadOnlyDictionary<string, EmulatorConfig> NoEmulators =
        new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, DeveloperEnvironmentConfig> NoEnvironments =
        new Dictionary<string, DeveloperEnvironmentConfig>(StringComparer.OrdinalIgnoreCase);

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostInspector _inspector = inspector;
    private readonly ISyncTransportFactory _transports = transports;
    private readonly HostCopyRecord _record = new(fileSystem, platform.PathComparison);

    public async Task<HostCopyListing> ListAsync(
        string startDirectory,
        IReadOnlyList<WorktreeListing> worktrees,
        bool askHosts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worktrees);

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<HostCopyEntry> recorded;

        try
        {
            recorded = _record.All(context.Layout);
        }
        catch (HarnessException ex)
        {
            // Said rather than read as empty: an empty record would list every copy a host keeps as one nothing here
            // made, and every gone worktree's copies as nowhere.
            return new HostCopyListing { Unreadable = ex.Message };
        }

        var ofListed = new Dictionary<string, IReadOnlyList<HostCopyEntry>>(StringComparer.Ordinal);
        var elsewhere = new List<RecordedTree>();
        var gone = new List<RecordedTree>();

        foreach (var tree in Trees(recorded))
        {
            if (ListedAt(worktrees, tree.Tree) is { } listed)
            {
                ofListed[listed.Name] = [.. ofListed.GetValueOrDefault(listed.Name) ?? [], .. tree.Copies];
            }
            else
            {
                (_record.Exists(tree.Tree) ? elsewhere : gone).Add(tree);
            }
        }

        var listing = new HostCopyListing
        {
            OfListed = ofListed,
            Elsewhere = [.. elsewhere.OrderBy(tree => tree.Name, StringComparer.Ordinal)],
            Gone = [.. gone.OrderBy(tree => tree.Name, StringComparer.Ordinal)],
        };

        if (!askHosts)
        {
            return listing;
        }

        var declared = Declared(context.Config);
        var answers = new List<HostCopiesAnswer>();

        foreach (var (host, repositoryPath) in declared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            answers.Add(await AskAsync(context, host, repositoryPath, recorded, worktrees, cancellationToken).ConfigureAwait(false));
        }

        return listing with
        {
            Hosts = answers,
            Undeclared = [.. recorded.Where(entry => !declared.Any(pair => OnHost(entry, pair.Host)))],
        };
    }

    /// <summary>What <paramref name="host"/> keeps beside <paramref name="repositoryPath"/>, set against the record.</summary>
    private async Task<HostCopiesAnswer> AskAsync(
        HarnessContext context,
        HostId host,
        string repositoryPath,
        IReadOnlyList<HostCopyEntry> recorded,
        IReadOnlyList<WorktreeListing> worktrees,
        CancellationToken cancellationToken)
    {
        var asked = new HostCopiesAnswer(host, repositoryPath);
        IReadOnlyList<HostCopyFound> found;

        try
        {
            var report = await _inspector
                .InspectAsync(context, host, NoEmulators, NoEnvironments, [], cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!report.Available)
            {
                return asked with { Unasked = report.Reason, ExitCode = HarnessExit.HostUnavailable };
            }

            found = await _transports.For(report).ListCopiesAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            return asked with { Unasked = ex.Message, ExitCode = ex.ExitCode };
        }

        var onHost = recorded.Where(entry => OnHost(entry, host)).ToList();

        return asked with
        {
            Copies = [.. found.Select(copy => Seen(copy, onHost.FirstOrDefault(entry => SamePath(entry, copy)), worktrees))],
            Missing = [.. onHost.Where(entry => !found.Any(copy => SamePath(entry, copy)))],
        };
    }

    /// <summary>Where <paramref name="copy"/> stands with the record, which holds it as <paramref name="entry"/> or not at all.</summary>
    private HostCopySeen Seen(HostCopyFound copy, HostCopyEntry? entry, IReadOnlyList<WorktreeListing> worktrees)
        => entry is null ? new HostCopySeen(copy, CopyStanding.Unrecorded, null)
            : ListedAt(worktrees, entry.Tree) is not null ? new HostCopySeen(copy, CopyStanding.Listed, entry.Tree)
            : _record.Exists(entry.Tree) ? new HostCopySeen(copy, CopyStanding.Elsewhere, entry.Tree)
            : new HostCopySeen(copy, CopyStanding.Gone, entry.Tree);

    /// <summary>The listed worktree at <paramref name="tree"/>, or <see langword="null"/> where none is.</summary>
    private WorktreeListing? ListedAt(IReadOnlyList<WorktreeListing> worktrees, string tree)
        => worktrees.FirstOrDefault(worktree => worktree.Path.Length > 0 && _record.SameTree(worktree.Path, tree));

    /// <summary>
    /// The copies <paramref name="recorded"/> holds, gathered by the tree they are of and the name they are kept under,
    /// in the order each tree was first recorded: two spellings of one tree are one, as the record itself tells them.
    /// </summary>
    private IReadOnlyList<RecordedTree> Trees(IReadOnlyList<HostCopyEntry> recorded)
    {
        var trees = new List<RecordedTree>();

        foreach (var entry in recorded)
        {
            var index = trees.FindIndex(tree => tree.Name == entry.Worktree && _record.SameTree(tree.Tree, entry.Tree));

            if (index < 0)
            {
                trees.Add(new RecordedTree(entry.Worktree, entry.Tree, [entry]));
            }
            else
            {
                trees[index] = trees[index] with { Copies = [.. trees[index].Copies, entry] };
            }
        }

        return trees;
    }

    /// <summary>
    /// The hosts that can keep copies - each WSL distribution and ssh host the configuration declares - with where
    /// each keeps the main checkout's, in the order a reader looks them up.
    /// </summary>
    private static IReadOnlyList<(HostId Host, string RepositoryPath)> Declared(HarnessConfig config)
        =>
        [
            .. config.Hosts.Wsl.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => (HostId.Wsl(pair.Key), pair.Value.RepositoryPath)),
            .. config.Hosts.Ssh.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => (HostId.Ssh(pair.Key), pair.Value.RepositoryPath)),
        ];

    /// <summary>Whether <paramref name="entry"/> records a copy on <paramref name="host"/>, as the record spells a host.</summary>
    private static bool OnHost(HostCopyEntry entry, HostId host)
        => string.Equals(entry.Host, host.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="entry"/> and <paramref name="copy"/> are one directory, both spelt as the configuration spells it.</summary>
    private static bool SamePath(HostCopyEntry entry, HostCopyFound copy)
        => string.Equals(entry.Path, copy.Path, StringComparison.Ordinal);
}
